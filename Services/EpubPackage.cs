using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TDPdf.Services
{
    /// <summary>One file listed in the package's manifest. <see cref="Path"/> is the name inside the zip.</summary>
    internal sealed record EpubManifestItem(string Id, string Path, string MediaType, string Properties);

    /// <summary>One entry of the contents tree. <see cref="Path"/> is null for a heading with no target.</summary>
    internal sealed class EpubTocEntry
    {
        public string Title { get; init; } = "";
        public string? Path { get; init; }
        public string? Fragment { get; init; }
        public List<EpubTocEntry> Children { get; } = new();
    }

    /// <summary>
    /// What an EPUB says about itself: where the package file is, its title and identifier, the reading
    /// order, the contents tree and which parts are encrypted. Read by hand (about 400 lines) rather than
    /// through a library so that every limit sits in one place and the whole reader can be fed hostile
    /// input in tests/PdfCore.
    /// </summary>
    internal sealed class EpubPackage
    {
        internal string OpfPath { get; private set; } = "";
        internal string Title { get; private set; } = "";
        internal string? Author { get; private set; }
        internal string? Language { get; private set; }
        internal string UniqueIdentifier { get; private set; } = "";
        internal bool RightToLeft { get; private set; }
        internal bool FixedLayout { get; private set; }
        internal Dictionary<string, EpubManifestItem> Manifest { get; } = new(StringComparer.Ordinal);
        internal List<EpubManifestItem> Spine { get; } = new();
        internal List<EpubTocEntry> Toc { get; } = new();

        /// <summary>Zip path -> the algorithm URI encryption.xml names for it.</summary>
        internal Dictionary<string, string> Encryption { get; } = new(StringComparer.Ordinal);

        /// <summary>Things the book got wrong that did not stop it opening (a missing spine item, say).</summary>
        internal List<string> Warnings { get; } = new();

        internal bool IsEncrypted(string path) =>
            Encryption.TryGetValue(path, out var alg) && !EpubFontDeobfuscation.IsObfuscation(alg);

        internal string? ObfuscationAlgorithm(string path) =>
            Encryption.TryGetValue(path, out var alg) && EpubFontDeobfuscation.IsObfuscation(alg) ? alg : null;

        private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";

        /// <summary>Reads the package from an archive; throws <see cref="InvalidDataException"/> with a plain reason.</summary>
        internal static EpubPackage Read(IEpubArchive archive)
        {
            var pkg = new EpubPackage();
            byte[]? container = archive.Read("META-INF/container.xml", EpubLimits.MaxXmlBytes);
            string? opfPath = null;
            if (container is not null)
            {
                try
                {
                    var doc = EpubXml.Parse(container);
                    foreach (var rf in doc.Descendants().Where(e => e.Name.LocalName == "rootfile"))
                    {
                        string? fullPath = (string?)rf.Attribute("full-path");
                        string? type = (string?)rf.Attribute("media-type");
                        if (string.IsNullOrWhiteSpace(fullPath)) continue;
                        if (type is null || type.Contains("oebps-package", StringComparison.OrdinalIgnoreCase))
                        {
                            opfPath = EpubPaths.Resolve("", fullPath);
                            if (opfPath is not null) break;
                        }
                    }
                }
                catch (XmlException) { /* fall through to the search below */ }
            }
            opfPath ??= archive.EntryNames.Where(n => n.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
                                          .OrderBy(n => n.Count(c => c == '/')).ThenBy(n => n, StringComparer.Ordinal)
                                          .FirstOrDefault();
            if (opfPath is null) throw new InvalidDataException("this is not an EPUB: it has no package file");

            byte[]? opfBytes = archive.Read(opfPath, EpubLimits.MaxXmlBytes);
            if (opfBytes is null) throw new InvalidDataException("the book's package file is missing");
            XDocument opf;
            try { opf = EpubXml.Parse(opfBytes); }
            catch (XmlException ex) { throw new InvalidDataException("the book's package file could not be read (" + ex.Message + ")"); }

            pkg.OpfPath = opfPath;
            pkg.ReadPackage(opf, archive);
            pkg.ReadEncryption(archive);
            return pkg;
        }

        private void ReadPackage(XDocument opf, IEpubArchive archive)
        {
            var root = opf.Root;
            if (root is null || root.Name.LocalName != "package")
                throw new InvalidDataException("the book's package file is not a package document");
            string opfDir = EpubPaths.DirectoryOf(OpfPath);

            // Metadata. Matched by local name so the dc: and opf: prefixes can be anything.
            var meta = root.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
            string? uidRef = (string?)root.Attribute("unique-identifier");
            if (meta is not null)
            {
                Title = Clean(meta.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value);
                Author = NullIfEmpty(Clean(meta.Elements().FirstOrDefault(e => e.Name.LocalName == "creator")?.Value));
                Language = NullIfEmpty(Clean(meta.Elements().FirstOrDefault(e => e.Name.LocalName == "language")?.Value));
                var ids = meta.Elements().Where(e => e.Name.LocalName == "identifier").ToList();
                var chosen = ids.FirstOrDefault(e => uidRef is not null && (string?)e.Attribute("id") == uidRef) ?? ids.FirstOrDefault();
                UniqueIdentifier = chosen?.Value ?? "";
                foreach (var m in meta.Elements().Where(e => e.Name.LocalName == "meta"))
                {
                    if ((string?)m.Attribute("property") == "rendition:layout" &&
                        m.Value.Trim().Equals("pre-paginated", StringComparison.OrdinalIgnoreCase))
                        FixedLayout = true;
                }
            }

            var manifest = root.Elements().FirstOrDefault(e => e.Name.LocalName == "manifest");
            if (manifest is not null)
            {
                foreach (var item in manifest.Elements().Where(e => e.Name.LocalName == "item"))
                {
                    string? id = (string?)item.Attribute("id");
                    string? href = (string?)item.Attribute("href");
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(href)) continue;
                    string? path = EpubPaths.Resolve(opfDir, href);
                    if (path is null) { Warnings.Add($"a manifest entry points outside the book: {href}"); continue; }
                    Manifest.TryAdd(id, new EpubManifestItem(id, path,
                        ((string?)item.Attribute("media-type") ?? "").Trim().ToLowerInvariant(),
                        (string?)item.Attribute("properties") ?? ""));
                }
            }

            var spine = root.Elements().FirstOrDefault(e => e.Name.LocalName == "spine");
            string? tocId = null;
            if (spine is not null)
            {
                tocId = (string?)spine.Attribute("toc");
                RightToLeft = string.Equals((string?)spine.Attribute("page-progression-direction"), "rtl", StringComparison.OrdinalIgnoreCase);
                foreach (var ir in spine.Elements().Where(e => e.Name.LocalName == "itemref"))
                {
                    string? idref = (string?)ir.Attribute("idref");
                    if (idref is not null && Manifest.TryGetValue(idref, out var it))
                    {
                        if (Spine.Count < EpubLimits.MaxChapters) Spine.Add(it);
                        else { Warnings.Add($"the book lists more than {EpubLimits.MaxChapters} sections; the rest were left out"); break; }
                    }
                    else Warnings.Add($"the reading order names a file the book does not list ({idref})");
                }
            }
            if (Spine.Count == 0) throw new InvalidDataException("the book has no readable sections");

            // Contents: the EPUB 3 navigation document wins over the EPUB 2 NCX.
            var navItem = Manifest.Values.FirstOrDefault(i => i.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav"));
            int budget = EpubLimits.MaxTocEntries;
            if (navItem is not null && archive.Read(navItem.Path, EpubLimits.MaxXmlBytes) is { } navBytes)
            {
                try { ReadNav(EpubXml.Parse(navBytes), EpubPaths.DirectoryOf(navItem.Path), Toc, ref budget); }
                catch (XmlException) { Warnings.Add("the book's contents page could not be read"); }
            }
            if (Toc.Count == 0)
            {
                EpubManifestItem? ncx = tocId is not null && Manifest.TryGetValue(tocId, out var n) ? n
                    : Manifest.Values.FirstOrDefault(i => i.MediaType == "application/x-dtbncx+xml");
                if (ncx is not null && archive.Read(ncx.Path, EpubLimits.MaxXmlBytes) is { } ncxBytes)
                {
                    try
                    {
                        var navMap = EpubXml.Parse(ncxBytes).Descendants().FirstOrDefault(e => e.Name.LocalName == "navMap");
                        if (navMap is not null) ReadNcx(navMap, EpubPaths.DirectoryOf(ncx.Path), Toc, 0, ref budget);
                    }
                    catch (XmlException) { Warnings.Add("the book's contents file could not be read"); }
                }
            }
        }

        private static void ReadNav(XDocument nav, string navDir, List<EpubTocEntry> into, ref int budget)
        {
            // <nav epub:type="toc"> (the type attribute lives in the epub namespace; match by local name).
            var tocNav = nav.Descendants().FirstOrDefault(e => e.Name.LocalName == "nav" &&
                e.Attributes().Any(a => a.Name.LocalName == "type" &&
                    a.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("toc")))
                ?? nav.Descendants().FirstOrDefault(e => e.Name.LocalName == "nav");
            var ol = tocNav?.Elements().FirstOrDefault(e => e.Name.LocalName == "ol");
            if (ol is not null) ReadNavList(ol, navDir, into, 0, ref budget);
        }

        private static void ReadNavList(XElement ol, string navDir, List<EpubTocEntry> into, int depth, ref int budget)
        {
            if (depth >= EpubLimits.MaxTocDepth) return;
            foreach (var li in ol.Elements().Where(e => e.Name.LocalName == "li"))
            {
                if (budget-- <= 0) return;
                var a = li.Elements().FirstOrDefault(e => e.Name.LocalName == "a");
                var label = a ?? li.Elements().FirstOrDefault(e => e.Name.LocalName is "span" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6");
                string title = Clean(label?.Value);
                string? path = null, frag = null;
                if (a is not null && (string?)a.Attribute("href") is { Length: > 0 } href)
                    (path, frag) = EpubPaths.ResolveWithFragment(navDir, href);
                var entry = new EpubTocEntry { Title = title, Path = path, Fragment = frag };
                var childOl = li.Elements().FirstOrDefault(e => e.Name.LocalName == "ol");
                if (childOl is not null) ReadNavList(childOl, navDir, entry.Children, depth + 1, ref budget);
                if (title.Length > 0 || entry.Children.Count > 0) into.Add(entry);
            }
        }

        private static void ReadNcx(XElement parent, string ncxDir, List<EpubTocEntry> into, int depth, ref int budget)
        {
            if (depth >= EpubLimits.MaxTocDepth) return;
            foreach (var np in parent.Elements().Where(e => e.Name.LocalName == "navPoint"))
            {
                if (budget-- <= 0) return;
                string title = Clean(np.Elements().FirstOrDefault(e => e.Name.LocalName == "navLabel")?
                    .Elements().FirstOrDefault(e => e.Name.LocalName == "text")?.Value);
                string? src = (string?)np.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Attribute("src");
                string? path = null, frag = null;
                if (!string.IsNullOrEmpty(src)) (path, frag) = EpubPaths.ResolveWithFragment(ncxDir, src);
                var entry = new EpubTocEntry { Title = title, Path = path, Fragment = frag };
                ReadNcx(np, ncxDir, entry.Children, depth + 1, ref budget);
                if (title.Length > 0 || entry.Children.Count > 0) into.Add(entry);
            }
        }

        private void ReadEncryption(IEpubArchive archive)
        {
            byte[]? bytes = archive.Read("META-INF/encryption.xml", EpubLimits.MaxXmlBytes);
            if (bytes is null) return;
            try
            {
                foreach (var ed in EpubXml.Parse(bytes).Descendants().Where(e => e.Name.LocalName == "EncryptedData"))
                {
                    string? alg = (string?)ed.Descendants().FirstOrDefault(e => e.Name.LocalName == "EncryptionMethod")?.Attribute("Algorithm");
                    string? uri = (string?)ed.Descendants().FirstOrDefault(e => e.Name.LocalName == "CipherReference")?.Attribute("URI");
                    if (string.IsNullOrEmpty(uri)) continue;
                    string? path = EpubPaths.Resolve("", uri);
                    if (path is null) continue;
                    // An entry that names no algorithm is unknown, which counts as encrypted.
                    Encryption[path] = alg ?? "unknown";
                }
            }
            catch (XmlException)
            {
                // A damaged encryption.xml: refuse nothing we cannot name, but say so.
                Warnings.Add("the book's encryption list could not be read");
            }
        }

        private static string Clean(string? s) => s is null ? "" : Regex.Replace(s, @"\s+", " ").Trim();
        private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
    }

    /// <summary>Resolving the relative names a book uses against the folder they are written in.</summary>
    internal static class EpubPaths
    {
        private static readonly Regex Scheme = new(@"^[A-Za-z][A-Za-z0-9+.\-]*:", RegexOptions.Compiled);

        internal static bool HasScheme(string href) => Scheme.IsMatch(href);

        internal static string DirectoryOf(string path)
        {
            int i = path.LastIndexOf('/');
            return i < 0 ? "" : path[..i];
        }

        /// <summary>
        /// The zip name that <paramref name="href"/> (query and fragment dropped, percent-escapes undone)
        /// names when written in <paramref name="baseDir"/>, or null for a link to the web or one that
        /// climbs out of the book.
        /// </summary>
        internal static string? Resolve(string baseDir, string href)
        {
            if (string.IsNullOrWhiteSpace(href) || HasScheme(href.Trim())) return null;
            string h = href.Trim();
            int cut = h.IndexOfAny(new[] { '#', '?' });
            if (cut >= 0) h = h[..cut];
            if (h.Length == 0) return null;
            try { h = Uri.UnescapeDataString(h); } catch (UriFormatException) { return null; }
            h = h.Replace('\\', '/');
            var parts = new List<string>();
            if (!h.StartsWith('/') && baseDir.Length > 0) parts.AddRange(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));
            foreach (var seg in h.Split('/'))
            {
                if (seg.Length == 0 || seg == ".") continue;
                if (seg == "..")
                {
                    if (parts.Count == 0) return null;
                    parts.RemoveAt(parts.Count - 1);
                }
                else parts.Add(seg);
            }
            return parts.Count == 0 ? null : string.Join('/', parts);
        }

        /// <summary>The fragment of an href without its leading '#', percent-escapes undone; null when there is none.</summary>
        internal static string? FragmentOf(string href)
        {
            int i = href.IndexOf('#');
            if (i < 0 || i == href.Length - 1) return null;
            string f = href[(i + 1)..];
            try { return Uri.UnescapeDataString(f); } catch (UriFormatException) { return f; }
        }

        internal static (string? Path, string? Fragment) ResolveWithFragment(string baseDir, string href) =>
            (Resolve(baseDir, href), FragmentOf(href));
    }

    /// <summary>XML from a book. DTDs are ignored, never fetched, and named HTML entities are repaired.</summary>
    internal static class EpubXml
    {
        private static readonly Dictionary<string, string> Entities = new(StringComparer.Ordinal)
        {
            ["nbsp"] = "&#160;", ["copy"] = "&#169;", ["reg"] = "&#174;", ["mdash"] = "&#8212;", ["ndash"] = "&#8211;",
            ["hellip"] = "&#8230;", ["lsquo"] = "&#8216;", ["rsquo"] = "&#8217;", ["ldquo"] = "&#8220;", ["rdquo"] = "&#8221;",
            ["laquo"] = "&#171;", ["raquo"] = "&#187;", ["middot"] = "&#183;", ["bull"] = "&#8226;", ["deg"] = "&#176;",
            ["times"] = "&#215;", ["divide"] = "&#247;", ["eacute"] = "&#233;", ["egrave"] = "&#232;", ["agrave"] = "&#224;",
            ["aacute"] = "&#225;", ["uuml"] = "&#252;", ["ouml"] = "&#246;", ["auml"] = "&#228;", ["ccedil"] = "&#231;",
            ["ntilde"] = "&#241;", ["iexcl"] = "&#161;", ["iquest"] = "&#191;", ["shy"] = "&#173;", ["thinsp"] = "&#8201;",
            ["ensp"] = "&#8194;", ["emsp"] = "&#8195;", ["sect"] = "&#167;", ["para"] = "&#182;", ["dagger"] = "&#8224;",
            ["trade"] = "&#8482;", ["euro"] = "&#8364;", ["pound"] = "&#163;", ["frac12"] = "&#189;", ["frac14"] = "&#188;",
        };

        private static readonly Regex NamedEntity = new(@"&(?!(?:amp|lt|gt|quot|apos);)([A-Za-z][A-Za-z0-9]*);", RegexOptions.Compiled);

        private static XmlReaderSettings Settings() => new()
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = EpubLimits.MaxXmlBytes,
            MaxCharactersFromEntities = 1024,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        /// <summary>Parses a book's XML; throws <see cref="XmlException"/> when it is not well formed even after repair.</summary>
        internal static XDocument Parse(byte[] bytes)
        {
            try
            {
                using var ms = new MemoryStream(bytes, writable: false);
                using var xr = XmlReader.Create(ms, Settings());
                return XDocument.Load(xr, LoadOptions.None);
            }
            catch (XmlException)
            {
                string text;
                using (var sr = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                    text = sr.ReadToEnd();
                text = NamedEntity.Replace(text, m => Entities.TryGetValue(m.Groups[1].Value, out var rep) ? rep : "");
                using var xr = XmlReader.Create(new StringReader(text), Settings());
                return XDocument.Load(xr, LoadOptions.None);
            }
        }
    }
}
