using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TDPdf.Services
{
    /// <summary>
    /// A whole EPUB as ONE XHTML document: every readable spine file, in reading order, each wrapped in its
    /// own block that starts a new page. Printing one document is what lets Chromium keep links between
    /// chapters as real links and write one set of named destinations the outline can point at.
    /// </summary>
    /// <remarks>
    /// What changes on the way in, and why:
    /// <list type="bullet">
    /// <item>Anything that runs or loads from elsewhere is dropped: script, iframe, object, embed, forms,
    /// event attributes, links to the web in src attributes. The page is printed with script off and the
    /// network blocked as well; this is the second lock on the same door.</item>
    /// <item>Every file reference becomes <c>/z/&lt;zip path&gt;</c> so the one document can pull each
    /// chapter's pictures and style sheets from the zip whatever folder the chapter sat in.</item>
    /// <item>An id that something links to (or that another chapter also uses) gets a safe name,
    /// <c>epub-c&lt;chapter&gt;-&lt;n&gt;</c>, and every link and url(#...) that pointed at it is rewritten to
    /// match. Other ids are left alone so a style sheet that selects them still works. The safe names are also
    /// what lets PDF named destinations be read back without escaping.</item>
    /// </list>
    /// A style sheet linked from one chapter applies to all of them, because they share a page.
    /// </remarks>
    internal sealed class EpubBook
    {
        internal static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
        private const int MaxDepth = 200;

        private sealed class Chapter
        {
            public int Index;
            public string Path = "";
            public XElement? Root;          // <html>
            public XElement? Head;
            public XElement? Body;
            public List<string> IdsInOrder = new();
            public HashSet<string> Ids = new(StringComparer.Ordinal);
            public string Dir = "";
        }

        /// <summary>The finished document, ready to serve at <see cref="EpubResourceServer.BookPath"/>.</summary>
        internal string Html { get; private set; } = "";

        /// <summary>Things left out or approximated, worded for the notice shown after opening.</summary>
        internal List<string> Notices { get; } = new();

        /// <summary>Titles of sections that could not be included (DRM or damaged).</summary>
        internal List<string> OmittedSections { get; } = new();

        internal int ChapterCount => _chapters.Count;
        internal string Title { get; private set; } = "";
        internal string? Author { get; private set; }

        private readonly List<Chapter> _chapters = new();
        private readonly Dictionary<string, Chapter> _byPath = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Path, string Id), string> _idMap = new();
        private readonly HashSet<(string Path, string Id)> _referenced = new();
        private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);
        private readonly List<string> _destinations = new();

        /// <summary>Every PDF destination name this book defines (chapter starts and linked ids).</summary>
        internal IReadOnlyList<string> Destinations => _destinations;

        /// <summary>
        /// The named destination for a place in the book: the element's own when the book's contents point at
        /// one that exists, else the start of its chapter, else null (the file is not part of the reading order).
        /// </summary>
        internal string? DestinationFor(string? path, string? fragment)
        {
            if (path is null || !_byPath.TryGetValue(path, out var ch)) return null;
            if (!string.IsNullOrEmpty(fragment) && _idMap.TryGetValue((path, fragment), out var id)) return id;
            return ChapterId(ch.Index);
        }

        internal static string ChapterId(int index) => $"epub-c{index}";

        private static readonly HashSet<string> DropElements = new(StringComparer.OrdinalIgnoreCase)
        {
            "script", "iframe", "object", "embed", "applet", "base", "meta", "link", "title", "noscript",
            "audio", "video", "form", "input", "button", "select", "textarea", "frame", "frameset", "foreignObject",
        };

        private static readonly Regex UrlFn = new(@"url\(\s*(['""]?)([^)'""]*)\1\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ImportRule = new(@"@import\s+(['""])([^'""]+)\1", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HashRef = new(@"url\(\s*(['""]?)#([^)'""\s]+)\1\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        internal static EpubBook Build(EpubPackage pkg, IEpubArchive archive, BookLayoutOptions options)
        {
            var book = new EpubBook { Title = pkg.Title, Author = pkg.Author };
            book.Load(pkg, archive);
            book.CollectReferences(pkg);
            book.AssignIds();
            book.Emit(pkg, archive, options);
            return book;
        }

        // ── 1. Parse every readable spine file ─────────────────────────────────────────────────

        private void Load(EpubPackage pkg, IEpubArchive archive)
        {
            var titles = new Dictionary<string, string>(StringComparer.Ordinal);
            void Walk(IEnumerable<EpubTocEntry> es)
            {
                foreach (var e in es)
                {
                    if (e.Path is not null && e.Title.Length > 0) titles.TryAdd(e.Path, e.Title);
                    Walk(e.Children);
                }
            }
            Walk(pkg.Toc);
            string Name(EpubManifestItem it) => titles.GetValueOrDefault(it.Path) ?? System.IO.Path.GetFileName(it.Path);

            int unreadable = 0, unsupported = 0;
            foreach (var item in pkg.Spine)
            {
                if (pkg.IsEncrypted(item.Path)) { OmittedSections.Add(Name(item)); continue; }
                if (_byPath.ContainsKey(item.Path)) continue;   // a file listed twice in the reading order is read once

                bool markup = item.MediaType is "application/xhtml+xml" or "text/html";
                bool picture = item.MediaType.StartsWith("image/", StringComparison.Ordinal);
                if (!markup && !picture) { unsupported++; continue; }

                XDocument? doc = null;
                try
                {
                    byte[]? bytes = archive.Read(item.Path, EpubLimits.MaxXmlBytes);
                    if (bytes is null) { unreadable++; continue; }
                    if (markup) doc = EpubXml.Parse(bytes);
                }
                catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException)
                {
                    unreadable++;
                    continue;
                }

                var ch = new Chapter { Index = _chapters.Count, Path = item.Path, Dir = EpubPaths.DirectoryOf(item.Path) };
                if (doc is not null)
                {
                    ch.Root = doc.Root;
                    ch.Head = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "head");
                    ch.Body = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "body");
                    if (ch.Body is null) { unreadable++; continue; }
                    foreach (var el in ch.Body.Descendants())
                    {
                        string? id = (string?)el.Attribute("id");
                        if (string.IsNullOrEmpty(id) && el.Name.LocalName == "a") id = (string?)el.Attribute("name");
                        if (!string.IsNullOrEmpty(id) && ch.Ids.Add(id)) ch.IdsInOrder.Add(id);
                    }
                }
                else
                {
                    // A picture on its own in the reading order (a cover, say): show it on a page of its own.
                    ch.Body = new XElement(Xhtml + "body", new XElement(Xhtml + "img",
                        new XAttribute("src", ResourceUrl(item.Path)), new XAttribute("alt", "")));
                }
                _chapters.Add(ch);
                _byPath[item.Path] = ch;
            }

            if (OmittedSections.Count > 0)
                Notices.Add($"{OmittedSections.Count} section(s) are protected by DRM and were left out: " + Listing(OmittedSections));
            int otherEncrypted = pkg.Encryption.Count(kv => !EpubFontDeobfuscation.IsObfuscation(kv.Value) && !_byPath.ContainsKey(kv.Key)
                                                            && !pkg.Spine.Any(s => s.Path == kv.Key));
            if (otherEncrypted > 0)
                Notices.Add($"{otherEncrypted} picture(s), font(s) or other file(s) are protected by DRM and were left out.");
            if (unreadable > 0) Notices.Add($"{unreadable} section(s) could not be read and were left out.");
            if (unsupported > 0) Notices.Add($"{unsupported} section(s) of a kind TDPdf does not show were left out.");
            if (pkg.FixedLayout) Notices.Add("This book has fixed page layouts. It was laid out as flowing text, so some pages may look different.");
            foreach (var w in pkg.Warnings.Take(5)) Notices.Add(char.ToUpperInvariant(w[0]) + w[1..] + ".");
            if (_chapters.Count == 0) throw new InvalidDataException("none of the book's sections could be read");
        }

        private static string Listing(List<string> names)
        {
            var shown = names.Take(8).Select(n => n.Length > 60 ? n[..60] + "..." : n);
            string s = string.Join("; ", shown);
            return names.Count > 8 ? s + $"; and {names.Count - 8} more" : s;
        }

        // ── 2. Which ids does something point at? ──────────────────────────────────────────────

        private void CollectReferences(EpubPackage pkg)
        {
            foreach (var ch in _chapters)
            {
                if (ch.Body is null) continue;
                foreach (var el in ch.Body.Descendants())
                {
                    foreach (var a in el.Attributes())
                    {
                        string v = a.Value;
                        if (a.Name.LocalName == "href" && v.Length > 0)
                        {
                            var (p, f) = Target(ch, v);
                            if (p is not null && f is not null && _byPath.TryGetValue(p, out var t) && t.Ids.Contains(f))
                                _referenced.Add((p, f));
                        }
                        else if (v.Contains("url(", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (Match m in HashRef.Matches(v))
                                if (ch.Ids.Contains(m.Groups[2].Value)) _referenced.Add((ch.Path, m.Groups[2].Value));
                        }
                    }
                }
            }
            void Walk(IEnumerable<EpubTocEntry> es)
            {
                foreach (var e in es)
                {
                    if (e.Path is not null && e.Fragment is not null && _byPath.TryGetValue(e.Path, out var t) && t.Ids.Contains(e.Fragment))
                        _referenced.Add((e.Path, e.Fragment));
                    Walk(e.Children);
                }
            }
            Walk(pkg.Toc);
        }

        /// <summary>Where a link written in <paramref name="ch"/> points: (zip path, fragment). Path null for the web.</summary>
        private static (string? Path, string? Fragment) Target(Chapter ch, string href)
        {
            string h = href.Trim();
            if (h.StartsWith('#')) return (ch.Path, EpubPaths.FragmentOf(h));
            if (EpubPaths.HasScheme(h)) return (null, null);
            return EpubPaths.ResolveWithFragment(ch.Dir, h);
        }

        // ── 3. Decide the final id of every id ─────────────────────────────────────────────────

        private void AssignIds()
        {
            foreach (var ch in _chapters)
            {
                int n = 0;
                foreach (string id in ch.IdsInOrder)
                {
                    string final;
                    if (_referenced.Contains((ch.Path, id)) || _usedIds.Contains(id) || id.StartsWith("epub-c", StringComparison.Ordinal))
                    {
                        final = $"epub-c{ch.Index}-{n++}";
                    }
                    else final = id;
                    _usedIds.Add(final);
                    _idMap[(ch.Path, id)] = final;
                }
            }
        }

        // ── 4. Write the one document ──────────────────────────────────────────────────────────

        private void Emit(EpubPackage pkg, IEpubArchive archive, BookLayoutOptions o)
        {
            var head = new XElement(Xhtml + "head");
            head.Add(new XElement(Xhtml + "meta", new XAttribute("charset", "utf-8")));
            head.Add(new XElement(Xhtml + "title", string.IsNullOrWhiteSpace(Title) ? "Book" : Title));

            double usable = o.UsableHeightPt;
            string defaults =
                $"@page {{ size: {BookLayoutOptions.Pt(o.WidthPt)}pt {BookLayoutOptions.Pt(o.HeightPt)}pt; margin: {BookLayoutOptions.Pt(o.MarginMm)}mm; }}\n" +
                $"html {{ font-size: {BookLayoutOptions.Pt(o.FontSizePt)}pt; }}\n" +
                "body { margin: 0; padding: 0; }\n" +
                ".epub-ch { break-before: page; }\n.epub-ch:first-of-type { break-before: auto; }\n";
            head.Add(new XElement(Xhtml + "style", defaults));

            var linked = new HashSet<string>(StringComparer.Ordinal);
            var body = new XElement(Xhtml + "body");
            foreach (var ch in _chapters)
            {
                if (ch.Head is not null) AddHeadStyles(ch, head, linked);
                var wrapper = new XElement(Xhtml + "div", new XAttribute("id", ChapterId(ch.Index)));
                string cls = "epub-ch";
                if (ch.Body is { } b)
                {
                    if ((string?)b.Attribute("class") is { Length: > 0 } bc) cls += " " + bc;
                    string? lang = (string?)b.Attribute("lang") ?? (string?)ch.Root?.Attribute("lang");
                    if (!string.IsNullOrEmpty(lang)) wrapper.SetAttributeValue("lang", lang);
                    if ((string?)b.Attribute("dir") is { Length: > 0 } dir) wrapper.SetAttributeValue("dir", dir);
                    if ((string?)b.Attribute("style") is { Length: > 0 } st) wrapper.SetAttributeValue("style", RewriteCss(ch, st));
                }
                wrapper.SetAttributeValue("class", cls);
                if (ch.Body is not null)
                    foreach (var child in ch.Body.Nodes()) AppendClone(wrapper, child, ch, 0);
                body.Add(wrapper);
            }

            string overrides =
                $"img, svg {{ max-width: 100%; }}\nimg {{ max-height: {BookLayoutOptions.Pt(usable)}pt; object-fit: contain; }}\n" +
                "pre { white-space: pre-wrap; }\ntable { max-width: 100%; }\n";
            head.Add(new XElement(Xhtml + "style", overrides));

            // Chromium writes a named destination only for an id that some link in the document points at.
            // The outline's targets are not always linked from the text, so a hidden list of links does it.
            _destinations.Clear();
            foreach (var ch in _chapters) _destinations.Add(ChapterId(ch.Index));
            foreach (var kv in _idMap.Where(kv => _referenced.Contains(kv.Key))) _destinations.Add(kv.Value);
            var anchors = new XElement(Xhtml + "div", new XAttribute("style", "display:none"));
            foreach (string d in _destinations)
                anchors.Add(new XElement(Xhtml + "a", new XAttribute("href", "#" + d), "."));
            body.Add(anchors);

            var html = new XElement(Xhtml + "html", head, body);
            if (!string.IsNullOrEmpty(pkg.Language)) html.SetAttributeValue("lang", pkg.Language);
            if (pkg.RightToLeft) html.SetAttributeValue("dir", "rtl");
            // No XML declaration: a StringWriter would label the text utf-16, and it is served as UTF-8.
            var doc = new XDocument(html);
            var sb = new StringBuilder();
            using (var w = new StringWriter(sb)) doc.Save(w, SaveOptions.DisableFormatting);
            Html = sb.ToString();
        }

        private void AddHeadStyles(Chapter ch, XElement head, HashSet<string> linked)
        {
            foreach (var el in ch.Head!.Elements())
            {
                string name = el.Name.LocalName;
                if (name == "link" && ((string?)el.Attribute("rel") ?? "").Contains("stylesheet", StringComparison.OrdinalIgnoreCase)
                    && (string?)el.Attribute("href") is { Length: > 0 } href)
                {
                    string? path = EpubPaths.Resolve(ch.Dir, href);
                    if (path is null || !linked.Add(path)) continue;
                    var link = new XElement(Xhtml + "link", new XAttribute("rel", "stylesheet"), new XAttribute("href", ResourceUrl(path)));
                    if ((string?)el.Attribute("media") is { Length: > 0 } media) link.SetAttributeValue("media", media);
                    head.Add(link);
                }
                else if (name == "style")
                {
                    head.Add(new XElement(Xhtml + "style", RewriteCss(ch, el.Value)));
                }
            }
        }

        private void AppendClone(XElement parent, XNode node, Chapter ch, int depth)
        {
            switch (node)
            {
                case XText t:
                    parent.Add(new XText(StripInvalidXml(t.Value)));
                    return;
                case XElement el:
                    if (depth >= MaxDepth) return;
                    if (DropElements.Contains(el.Name.LocalName)) return;
                    XNamespace ns = el.Name.Namespace == XNamespace.None ? Xhtml : el.Name.Namespace;
                    bool isHtml = ns == Xhtml;
                    string local = el.Name.LocalName;
                    var copy = new XElement(ns + local);
                    foreach (var attr in el.Attributes())
                    {
                        if (attr.IsNamespaceDeclaration) continue;
                        string an = attr.Name.LocalName;
                        string av = attr.Value;
                        if (an.StartsWith("on", StringComparison.OrdinalIgnoreCase)) continue;
                        if (an == "id" || (an == "name" && local == "a")) continue;   // set below
                        if (an == "style") { copy.SetAttributeValue(attr.Name, RewriteCss(ch, av)); continue; }
                        if (an == "href" && attr.Name.Namespace != XNamespace.None && attr.Name.Namespace.NamespaceName != "http://www.w3.org/1999/xlink") continue;
                        if (an == "href")
                        {
                            string? fixedHref = RewriteHref(ch, av, local, isHtml);
                            if (fixedHref is not null) copy.SetAttributeValue(attr.Name, fixedHref);
                            continue;
                        }
                        if (an is "src" or "poster" or "data")
                        {
                            string? u = RewriteResource(ch, av);
                            if (u is not null) copy.SetAttributeValue(attr.Name, u);
                            continue;
                        }
                        if (an == "srcset")
                        {
                            var parts = av.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).Select(p =>
                            {
                                int sp = p.IndexOfAny(new[] { ' ', '\t' });
                                string u = sp < 0 ? p : p[..sp];
                                string rest = sp < 0 ? "" : p[sp..];
                                string? r = RewriteResource(ch, u);
                                return r is null ? null : r + rest;
                            }).Where(p => p is not null);
                            copy.SetAttributeValue(attr.Name, string.Join(", ", parts));
                            continue;
                        }
                        if (av.Contains("url(", StringComparison.OrdinalIgnoreCase)) av = RewriteCss(ch, av);
                        copy.SetAttributeValue(attr.Name, StripInvalidXml(av));
                    }

                    string? origId = (string?)el.Attribute("id");
                    if (string.IsNullOrEmpty(origId) && local == "a") origId = (string?)el.Attribute("name");
                    if (!string.IsNullOrEmpty(origId) && _idMap.TryGetValue((ch.Path, origId), out var finalId))
                        copy.SetAttributeValue("id", finalId);

                    foreach (var child in el.Nodes()) AppendClone(copy, child, ch, depth + 1);
                    parent.Add(copy);
                    return;
                // Comments, processing instructions and CDATA are dropped (CDATA text arrives as XText).
            }
        }

        private static string StripInvalidXml(string s)
        {
            foreach (char c in s)
            {
                if (c < 0x20 && c is not ('\t' or '\n' or '\r') || c is '￾' or '￿')
                    return new string(s.Where(x => !(x < 0x20 && x is not ('\t' or '\n' or '\r')) && x is not ('￾' or '￿')).ToArray());
            }
            return s;
        }

        /// <summary>The new value of an href, or null to drop the attribute.</summary>
        private string? RewriteHref(Chapter ch, string href, string localName, bool isHtml)
        {
            string h = href.Trim();
            if (h.Length == 0) return null;
            if (EpubPaths.HasScheme(h))
            {
                // Only links a person may follow out of a printed page.
                string scheme = h[..h.IndexOf(':')].ToLowerInvariant();
                return scheme is "http" or "https" or "mailto" or "tel" && isHtml && localName is "a" or "area" ? h : null;
            }
            var (path, frag) = Target(ch, h);
            if (path is null || !_byPath.TryGetValue(path, out var target))
            {
                // A reference to a file (a picture or style sheet) rather than another section.
                if (localName is "image" or "use" && path is not null) return ResourceUrl(path);
                return null;
            }
            if (!string.IsNullOrEmpty(frag) && _idMap.TryGetValue((path, frag), out var id)) return "#" + id;
            return "#" + ChapterId(target.Index);
        }

        private string? RewriteResource(Chapter ch, string url)
        {
            string u = url.Trim();
            if (u.Length == 0) return null;
            if (u.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return u;
            if (u.StartsWith('#')) return u;
            if (EpubPaths.HasScheme(u)) return null;
            string? path = EpubPaths.Resolve(ch.Dir, u);
            return path is null ? null : ResourceUrl(path);
        }

        private string RewriteCss(Chapter ch, string css)
        {
            css = ImportRule.Replace(css, m => RewriteResource(ch, m.Groups[2].Value) is { } r ? $"@import \"{r}\"" : "");
            return UrlFn.Replace(css, m =>
            {
                string u = m.Groups[2].Value.Trim();
                if (u.StartsWith('#'))
                {
                    string id = u[1..];
                    return _idMap.TryGetValue((ch.Path, id), out var f) ? $"url(#{f})" : m.Value;
                }
                string? r = RewriteResource(ch, u);
                return r is null ? "none" : $"url(\"{r}\")";
            });
        }

        internal static string ResourceUrl(string zipPath) =>
            EpubResourceServer.ResourcePrefix + string.Join('/', zipPath.Split('/').Select(Uri.EscapeDataString));
    }
}
