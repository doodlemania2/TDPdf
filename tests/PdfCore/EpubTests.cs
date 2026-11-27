using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using PdfSharpCore.Pdf;
using TDPdf.Services;

internal static class BookTestUtil
{
    internal static int PageNo(PdfDocument d, PdfPage p)
    {
        for (int i = 0; i < d.PageCount; i++) if (ReferenceEquals(d.Pages[i], p)) return i;
        return -1;
    }
}

/// <summary>An EPUB held in memory, so the reader and the page builder run without a zip on disk.</summary>
internal sealed class MemoryEpub : IEpubArchive
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> EntryNames => Files.Keys;
    public bool Contains(string path) => Files.ContainsKey(path);
    public byte[]? Read(string path, long maxBytes = EpubLimits.MaxEntryBytes)
    {
        if (!Files.TryGetValue(path, out var b)) return null;
        if (b.Length > maxBytes) throw new InvalidDataException("too big");
        return b;
    }
    public MemoryEpub Add(string path, string text) { Files[path] = Encoding.UTF8.GetBytes(text); return this; }
    public MemoryEpub Add(string path, byte[] data) { Files[path] = data; return this; }
}

/// <summary>
/// EPUB reading (#235): the package reader, font de-obfuscation, link rewriting, the resource server that
/// answers the page's requests from the zip, and the outline built from the contents. The WebView2 print
/// itself is the one part that cannot run here; everything it is fed and everything done with its output is.
/// </summary>
internal static class EpubTests
{
    private const string Uuid = "urn:uuid:12345678-1234-5678-9abc-def012345678";

    private static MemoryEpub SampleBook(bool ncxOnly = false)
    {
        var b = new MemoryEpub();
        b.Add("mimetype", "application/epub+zip");
        b.Add("META-INF/container.xml",
            "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
            "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
        string nav = ncxOnly ? "" : "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>";
        b.Add("OEBPS/content.opf",
            "<?xml version=\"1.0\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\">" +
            "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>Sample Book</dc:title><dc:creator>A. Writer</dc:creator>" +
            $"<dc:language>en</dc:language><dc:identifier id=\"bookid\">{Uuid}</dc:identifier></metadata>" +
            "<manifest>" + nav +
            "<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>" +
            "<item id=\"c1\" href=\"text/ch1.xhtml\" media-type=\"application/xhtml+xml\"/>" +
            "<item id=\"c2\" href=\"text/ch2.xhtml\" media-type=\"application/xhtml+xml\"/>" +
            "<item id=\"c3\" href=\"text/ch3.xhtml\" media-type=\"application/xhtml+xml\"/>" +
            "<item id=\"css\" href=\"styles/book.css\" media-type=\"text/css\"/>" +
            "<item id=\"img\" href=\"images/a b.png\" media-type=\"image/png\"/>" +
            "<item id=\"font\" href=\"fonts/f.otf\" media-type=\"font/otf\"/>" +
            "</manifest><spine toc=\"ncx\"><itemref idref=\"c1\"/><itemref idref=\"c2\"/><itemref idref=\"c3\"/></spine></package>");
        b.Add("OEBPS/nav.xhtml",
            "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><body>" +
            "<nav epub:type=\"toc\"><ol>" +
            "<li><a href=\"text/ch1.xhtml\">One&nbsp;Title</a><ol><li><a href=\"text/ch1.xhtml#sub\">Sub one</a></li></ol></li>" +
            "<li><a href=\"text/ch2.xhtml#sec\">Two</a></li>" +
            "<li><a href=\"text/ch3.xhtml\">Three</a></li></ol></nav></body></html>");
        b.Add("OEBPS/toc.ncx",
            "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\"><navMap>" +
            "<navPoint id=\"n1\"><navLabel><text>NCX One</text></navLabel><content src=\"text/ch1.xhtml\"/>" +
            "<navPoint id=\"n2\"><navLabel><text>NCX Sub</text></navLabel><content src=\"text/ch1.xhtml#sub\"/></navPoint></navPoint>" +
            "<navPoint id=\"n3\"><navLabel><text>NCX Two</text></navLabel><content src=\"text/ch2.xhtml\"/></navPoint>" +
            "</navMap></ncx>");
        b.Add("OEBPS/text/ch1.xhtml",
            "<?xml version=\"1.0\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>c1</title>" +
            "<link rel=\"stylesheet\" href=\"../styles/book.css\"/><style>p { background: url(../images/a%20b.png) }</style>" +
            "<script>alert(1)</script></head><body class=\"bk\"><h1 id=\"top\">One</h1>" +
            "<p id=\"sub\" onclick=\"x()\">See <a href=\"ch2.xhtml#sec\">two</a>, <a href=\"#sub\">here</a>, " +
            "<a href=\"http://example.org/x\">web</a>, <a href=\"javascript:alert(1)\">js</a>, <a href=\"../images/a%20b.png\">pic</a>, " +
            "<a href=\"ch2.xhtml#nope\">missing id</a>, <a href=\"../../../etc/passwd\">escape</a></p>" +
            "<p id=\"dup\">dup in one</p><img src=\"../images/a%20b.png\" alt=\"x\"/><iframe src=\"http://x\"></iframe>" +
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><defs><linearGradient id=\"g1\"/></defs><rect fill=\"url(#g1)\"/></svg></body></html>");
        b.Add("OEBPS/text/ch2.xhtml",
            "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>c2</title><link rel=\"stylesheet\" href=\"../styles/book.css\"/></head>" +
            "<body><h1 id=\"sec\">Two</h1><p id=\"dup\">dup in two</p><a name=\"anch\"></a><p>Back to <a href=\"ch1.xhtml#sub\">one</a>.</p></body></html>");
        b.Add("OEBPS/text/ch3.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>Three &copy; &bogus; end</p></body></html>");
        b.Add("OEBPS/styles/book.css", "body { margin: 1em }");
        b.Add("OEBPS/images/a b.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });
        b.Add("OEBPS/fonts/f.otf", Enumerable.Range(0, 3000).Select(i => (byte)(i * 7 + 3)).ToArray());
        return b;
    }

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("── EPUB books (Services/Epub*) ─────────────────────────────────");

        // ── package ─────────────────────────────────────────────────────────────────────────
        var ar = SampleBook();
        var pkg = EpubPackage.Read(ar);
        Check("epub: container.xml leads to the package", pkg.OpfPath == "OEBPS/content.opf", pkg.OpfPath);
        Check("epub: title, author, language, identifier",
            pkg.Title == "Sample Book" && pkg.Author == "A. Writer" && pkg.Language == "en" && pkg.UniqueIdentifier == Uuid, pkg.Title);
        Check("epub: spine is in reading order with resolved zip paths",
            pkg.Spine.Select(s => s.Path).SequenceEqual(new[] { "OEBPS/text/ch1.xhtml", "OEBPS/text/ch2.xhtml", "OEBPS/text/ch3.xhtml" }),
            string.Join(",", pkg.Spine.Select(s => s.Path)));
        Check("epub: percent-escaped manifest names are decoded", pkg.Manifest["img"].Path == "OEBPS/images/a b.png", pkg.Manifest["img"].Path);
        Check("epub: nav contents tree (nesting, fragments, &nbsp; repaired)",
            pkg.Toc.Count == 3 && pkg.Toc[0].Title == "One Title" && pkg.Toc[0].Children.Count == 1 &&
            pkg.Toc[0].Children[0].Fragment == "sub" && pkg.Toc[1].Path == "OEBPS/text/ch2.xhtml" && pkg.Toc[1].Fragment == "sec",
            string.Join("|", pkg.Toc.Select(t => t.Title)));

        var ncx = EpubPackage.Read(SampleBook(ncxOnly: true));
        Check("epub: EPUB 2 NCX is used when there is no nav",
            ncx.Toc.Count == 2 && ncx.Toc[0].Title == "NCX One" && ncx.Toc[0].Children[0].Fragment == "sub", string.Join("|", ncx.Toc.Select(t => t.Title)));

        var noContainer = SampleBook();
        noContainer.Files.Remove("META-INF/container.xml");
        Check("epub: a missing container.xml falls back to the .opf in the zip", EpubPackage.Read(noContainer).Title == "Sample Book", "");

        // Hostile or broken.
        void Throws(string label, Action a)
        {
            try { a(); Check(label, false, "did not throw"); }
            catch (InvalidDataException) { Check(label, true, ""); }
            catch (Exception ex) { Check(label, false, ex.GetType().Name + ": " + ex.Message); }
        }
        Throws("epub hostile: no package at all", () => EpubPackage.Read(new MemoryEpub().Add("a.txt", "x")));
        Throws("epub hostile: package that is not XML", () => EpubPackage.Read(new MemoryEpub()
            .Add("META-INF/container.xml", "<container><rootfiles><rootfile full-path=\"a.opf\"/></rootfiles></container>").Add("a.opf", "<<<not xml")));
        Throws("epub hostile: a spine with nothing readable", () => EpubPackage.Read(new MemoryEpub()
            .Add("META-INF/container.xml", "<container><rootfiles><rootfile full-path=\"a.opf\"/></rootfiles></container>")
            .Add("a.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\"><metadata/><manifest/><spine><itemref idref=\"zz\"/></spine></package>")));
        {
            // A manifest entry that climbs out of the book is not followed.
            var evil = SampleBook();
            evil.Add("OEBPS/content.opf", Encoding.UTF8.GetString(evil.Files["OEBPS/content.opf"]).Replace(
                "<item id=\"css\" href=\"styles/book.css\"", "<item id=\"css\" href=\"../../../etc/passwd\""));
            var p = EpubPackage.Read(evil);
            Check("epub hostile: a manifest path that escapes the book is dropped", !p.Manifest.ContainsKey("css") && p.Warnings.Count > 0, "");
        }
        {
            // An external entity in a DOCTYPE must not be fetched or expanded.
            string secret = Path.Combine(tmp, "xxe-secret.txt");
            File.WriteAllText(secret, "TOPSECRET");
            var x = SampleBook();
            x.Add("OEBPS/text/ch3.xhtml",
                $"<!DOCTYPE html [<!ENTITY xxe SYSTEM \"file://{secret}\">]><html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>&xxe;</p></body></html>");
            var bx = EpubBook.Build(EpubPackage.Read(x), x, BookLayoutOptions.For("Letter", 20, 11));
            Check("epub hostile: an external entity is never read", !bx.Html.Contains("TOPSECRET"), "");
        }
        {
            // A contents tree nested 5000 deep, and one with 30000 entries, is cut off.
            var sb = new StringBuilder("<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><nav><ol>");
            for (int i = 0; i < 5000; i++) sb.Append("<li><a href=\"text/ch1.xhtml\">d</a><ol>");
            for (int i = 0; i < 5000; i++) sb.Append("</ol></li>");
            sb.Append("</ol></nav></body></html>");
            var d = SampleBook();
            d.Add("OEBPS/nav.xhtml", sb.ToString());
            var dp = EpubPackage.Read(d);
            int depth = 0;
            for (var t = dp.Toc.FirstOrDefault(); t is not null; t = t.Children.FirstOrDefault()) depth++;
            Check("epub hostile: contents nesting is capped", depth <= EpubLimits.MaxTocDepth, $"{depth}");
            var flat = new StringBuilder("<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><nav><ol>");
            for (int i = 0; i < 30000; i++) flat.Append("<li><a href=\"text/ch1.xhtml\">e</a></li>");
            flat.Append("</ol></nav></body></html>");
            d.Add("OEBPS/nav.xhtml", flat.ToString());
            Check("epub hostile: contents size is capped", EpubPackage.Read(d).Toc.Count <= EpubLimits.MaxTocEntries, "");
        }

        // Paths.
        Check("epub paths: relative, dot segments and escapes",
            EpubPaths.Resolve("OEBPS/text", "../images/a%20b.png#x") == "OEBPS/images/a b.png" &&
            EpubPaths.Resolve("a", "/root/x.png") == "root/x.png" &&
            EpubPaths.Resolve("a", "../../x") is null && EpubPaths.Resolve("a", "http://x/y") is null &&
            EpubPaths.Resolve("a", "mailto:me@x") is null && EpubPaths.Resolve("a", "#frag") is null, "");

        // ── encryption ──────────────────────────────────────────────────────────────────────
        {
            var e = SampleBook();
            e.Add("META-INF/encryption.xml",
                "<encryption xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" xmlns:enc=\"http://www.w3.org/2001/04/xmlenc#\">" +
                "<enc:EncryptedData><enc:EncryptionMethod Algorithm=\"http://www.idpf.org/2008/embedding\"/><enc:CipherData><enc:CipherReference URI=\"OEBPS/fonts/f.otf\"/></enc:CipherData></enc:EncryptedData>" +
                "<enc:EncryptedData><enc:EncryptionMethod Algorithm=\"http://www.w3.org/2001/04/xmlenc#aes128-cbc\"/><enc:CipherData><enc:CipherReference URI=\"OEBPS/text/ch2.xhtml\"/></enc:CipherData></enc:EncryptedData>" +
                "<enc:EncryptedData><enc:EncryptionMethod Algorithm=\"http://www.w3.org/2001/04/xmlenc#aes128-cbc\"/><enc:CipherData><enc:CipherReference URI=\"OEBPS/images/a%20b.png\"/></enc:CipherData></enc:EncryptedData>" +
                "</encryption>");
            var ep = EpubPackage.Read(e);
            Check("epub encryption: font obfuscation is not DRM, AES is", !ep.IsEncrypted("OEBPS/fonts/f.otf") &&
                ep.ObfuscationAlgorithm("OEBPS/fonts/f.otf") == EpubFontDeobfuscation.IdpfAlgorithm && ep.IsEncrypted("OEBPS/text/ch2.xhtml")
                && ep.IsEncrypted("OEBPS/images/a b.png"), "");
            var eb = EpubBook.Build(ep, e, BookLayoutOptions.For("Letter", 20, 11));
            Check("epub encryption: the DRM chapter is left out and listed by its contents title",
                eb.ChapterCount == 2 && eb.OmittedSections.SequenceEqual(new[] { "Two" }) &&
                eb.Notices.Any(n => n.Contains("DRM") && n.Contains("Two")) && !eb.Html.Contains("dup in two"), string.Join("|", eb.Notices));
            Check("epub encryption: a DRM picture is counted in the notice", eb.Notices.Any(n => n.Contains("picture")), string.Join("|", eb.Notices));
            Check("epub encryption: outline target in a left-out chapter has no destination", eb.DestinationFor("OEBPS/text/ch2.xhtml", "sec") is null, "");
            var srv = new EpubResourceServer(e, ep, eb);
            Check("epub encryption: a DRM file is refused by the server and reported",
                srv.Get("/z/OEBPS/images/a%20b.png").Status == 404 && srv.Failed.Contains("OEBPS/images/a b.png"), "");
            // The obfuscated font comes back un-obfuscated.
            byte[] clear = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7 + 3)).ToArray();
            e.Files["OEBPS/fonts/f.otf"] = EpubFontDeobfuscation.Xor(clear, EpubFontDeobfuscation.IdpfKey(Uuid), 1040);
            var font = srv.Get("/z/OEBPS/fonts/f.otf");
            Check("epub encryption: the server serves an IDPF font un-obfuscated", font.Status == 200 && font.Body.SequenceEqual(clear), "");
        }

        // ── de-obfuscation ──────────────────────────────────────────────────────────────────
        {
            byte[] data = Enumerable.Range(0, 2000).Select(i => (byte)(i % 251)).ToArray();
            byte[] key = EpubFontDeobfuscation.IdpfKey(" urn:uuid:ABC\n\t DEF ");
            byte[] expectKey = SHA1.HashData(Encoding.UTF8.GetBytes("urn:uuid:ABCDEF"));
            Check("epub font: IDPF key is SHA-1 of the identifier without whitespace", key.SequenceEqual(expectKey) && key.Length == 20, "");
            byte[] ob = EpubFontDeobfuscation.Xor(data, key, EpubFontDeobfuscation.IdpfLength);
            Check("epub font: IDPF changes exactly the first 1040 bytes",
                !ob.Take(1040).SequenceEqual(data.Take(1040)) && ob.Skip(1040).SequenceEqual(data.Skip(1040)), "");
            Check("epub font: IDPF round trip", EpubFontDeobfuscation.Deobfuscate(EpubFontDeobfuscation.IdpfAlgorithm, " urn:uuid:ABC\n\t DEF ", ob)!.SequenceEqual(data), "");
            // Spec vector shape: the key repeats every 20 bytes.
            Check("epub font: IDPF key repeats every 20 bytes", ob[0] == (byte)(data[0] ^ key[0]) && ob[20] == (byte)(data[20] ^ key[0]) && ob[39] == (byte)(data[39] ^ key[19]), "");

            byte[]? akey = EpubFontDeobfuscation.AdobeKey("urn:uuid:12345678-1234-5678-9abc-def012345678");
            Check("epub font: Adobe key is the UUID's 16 bytes", akey is not null && akey.Length == 16 && akey[0] == 0x12 && akey[15] == 0x78 && akey[8] == 0x9a, "");
            byte[] aob = EpubFontDeobfuscation.Xor(data, akey!, EpubFontDeobfuscation.AdobeLength);
            Check("epub font: Adobe changes exactly the first 1024 bytes",
                !aob.Take(1024).SequenceEqual(data.Take(1024)) && aob.Skip(1024).SequenceEqual(data.Skip(1024)), "");
            Check("epub font: Adobe round trip", EpubFontDeobfuscation.Deobfuscate(EpubFontDeobfuscation.AdobeAlgorithm, Uuid, aob)!.SequenceEqual(data), "");
            Check("epub font: Adobe with an identifier that is not a UUID cannot unlock",
                EpubFontDeobfuscation.AdobeKey("isbn:9780000000002") is null && EpubFontDeobfuscation.AdobeKey("urn:uuid:zz345678-1234-5678-9abc-def012345678") is null
                && EpubFontDeobfuscation.Deobfuscate(EpubFontDeobfuscation.AdobeAlgorithm, "isbn:1", data) is null, "");
            Check("epub font: an unknown algorithm is not touched", EpubFontDeobfuscation.Deobfuscate("http://example/aes", Uuid, data) is null
                && !EpubFontDeobfuscation.IsObfuscation("http://www.w3.org/2001/04/xmlenc#aes128-cbc"), "");
        }

        // ── the one document: links, ids, hostile markup ────────────────────────────────────
        {
            var book = EpubBook.Build(pkg, ar, BookLayoutOptions.For("A4", 15, 12));
            XDocument doc = XDocument.Parse(book.Html);
            XNamespace h = "http://www.w3.org/1999/xhtml";
            var sections = doc.Descendants(h + "div").Where(d => ((string?)d.Attribute("class") ?? "").Contains("epub-ch")).ToList();
            Check("epub doc: well-formed XHTML, one block per chapter", sections.Count == 3 &&
                sections.Select(s => (string?)s.Attribute("id")).SequenceEqual(new[] { "epub-c0", "epub-c1", "epub-c2" }), $"{sections.Count}");
            Check("epub doc: body class travels to the chapter block", ((string?)sections[0].Attribute("class"))!.Contains("bk"), "");
            Check("epub doc: no script, iframe or event handlers survive",
                !book.Html.Contains("<script", StringComparison.OrdinalIgnoreCase) && !book.Html.Contains("iframe", StringComparison.OrdinalIgnoreCase)
                && !book.Html.Contains("onclick") && !book.Html.Contains("alert(1)"), "");
            Check("epub doc: page size, margin and text size reach the style",
                book.Html.Contains("size: 595.28pt 841.89pt") && book.Html.Contains("margin: 15mm") && book.Html.Contains("font-size: 12pt"), "");

            var links = doc.Descendants(h + "a").Where(a => a.Attribute("href") is not null && !a.Ancestors(h + "div").Any(d => (string?)d.Attribute("style") == "display:none")).ToList();
            string? Href(string text) => (string?)links.FirstOrDefault(a => a.Value == text)?.Attribute("href");
            string? Id(string xmlId) => doc.Descendants().FirstOrDefault(e => (string?)e.Attribute("id") == xmlId)?.Name.LocalName;

            string secDest = book.DestinationFor("OEBPS/text/ch2.xhtml", "sec")!;
            string subDest = book.DestinationFor("OEBPS/text/ch1.xhtml", "sub")!;
            Check("epub links: a link to another chapter's id points at that element's new id", Href("two") == "#" + secDest && Id(secDest) == "h1", $"{Href("two")} {secDest}");
            Check("epub links: a link inside a chapter and one back from another chapter agree", Href("here") == "#" + subDest && Href("one") == "#" + subDest && Id(subDest) == "p", subDest);
            Check("epub links: an id that is linked to gets a safe name", System.Text.RegularExpressions.Regex.IsMatch(subDest, "^epub-c\\d+-\\d+$"), subDest);
            Check("epub links: a link to a missing id goes to the chapter start", Href("missing id") == "#epub-c1", Href("missing id") ?? "null");
            Check("epub links: web links stay, script and escaping links are dropped",
                Href("web") == "http://example.org/x" && Href("js") is null && Href("escape") is null && Href("pic") is null, "");
            Check("epub links: an unlinked id that nothing else uses is left alone", Id("top") == "h1", "");
            var dups = doc.Descendants().Where(e => (string?)e.Attribute("id") == "dup").ToList();
            Check("epub links: the same id in two chapters is made unique", dups.Count == 1 && doc.Descendants().Select(e => (string?)e.Attribute("id")).Where(i => i is not null).GroupBy(i => i).All(g => g.Count() == 1), $"{dups.Count}");
            Check("epub links: <a name> becomes an id", doc.Descendants(h + "a").Any(a => a.Attribute("id") is not null && a.Attribute("name") is null && a.Value == "" ) , "");
            string? fillRef = doc.Descendants().Where(e => e.Name.LocalName == "rect").Select(r => (string?)r.Attribute("fill")).FirstOrDefault();
            string? gradId = fillRef is not null && fillRef.StartsWith("url(#") ? fillRef[5..^1] : null;
            Check("epub links: svg url(#id) follows a renamed id", gradId is not null && gradId != "g1" && Id(gradId) == "linearGradient", fillRef ?? "null");

            Check("epub files: img src, style url() and stylesheet link point at /z/ paths with escapes",
                book.Html.Contains("src=\"/z/OEBPS/images/a%20b.png\"") && book.Html.Contains("url(\"/z/OEBPS/images/a%20b.png\")") &&
                book.Html.Contains("href=\"/z/OEBPS/styles/book.css\""), "");
            Check("epub files: a stylesheet shared by two chapters is linked once", System.Text.RegularExpressions.Regex.Matches(book.Html, "book\\.css").Count == 1, "");

            Check("epub dest: a place in the contents resolves to its element, else its chapter, else nothing",
                book.DestinationFor("OEBPS/text/ch1.xhtml", null) == "epub-c0" && book.DestinationFor("OEBPS/text/ch2.xhtml", "zzz") == "epub-c1"
                && book.DestinationFor("OEBPS/nav.xhtml", null) is null && book.DestinationFor(null, null) is null, "");
            // Every destination the outline can need is reachable by a hidden link, which is what makes Chromium write it.
            var hidden = doc.Descendants(h + "div").First(d => (string?)d.Attribute("style") == "display:none").Elements(h + "a").Select(a => ((string)a.Attribute("href")!)[1..]).ToHashSet();
            Check("epub dest: a hidden link exists for every chapter and linked id", book.Destinations.All(hidden.Contains) && hidden.Contains("epub-c2") && hidden.Contains(secDest), "");
            Check("epub doc: &copy; is repaired, an unknown entity is dropped, not fatal", book.Html.Contains("©") && !book.Html.Contains("bogus"), "");

            // Resource server.
            var srv = new EpubResourceServer(ar, pkg, book);
            var page = srv.Get(EpubResourceServer.BookPath);
            Check("epub server: the book itself", page.Status == 200 && page.ContentType.StartsWith("application/xhtml+xml") && Encoding.UTF8.GetString(page.Body) == book.Html, "");
            var css = srv.Get("/z/OEBPS/styles/book.css");
            var img = srv.Get("/z/OEBPS/images/a%20b.png");
            Check("epub server: a stylesheet and an escaped picture name",
                css.Status == 200 && css.ContentType.StartsWith("text/css") && img.Status == 200 && img.ContentType == "image/png" && img.Body.Length == 7, "");
            Check("epub server: anything else is a 404 and never reaches outside the zip",
                srv.Get("/z/nothing.png").Status == 404 && srv.Get("/z/../META-INF/container.xml").Status == 404
                && srv.Get("/etc/passwd").Status == 404 && srv.Get("/z/%2e%2e/x").Status == 404 && srv.Get("/z/").Status == 404, "");
            Check("epub server: the policy allows nothing but the book's own parts",
                EpubResourceServer.ContentSecurityPolicy.Contains("default-src 'none'") && !EpubResourceServer.ContentSecurityPolicy.Contains("script"), "");
        }

        // ── outline ─────────────────────────────────────────────────────────────────────────
        {
            var book = EpubBook.Build(pkg, ar, BookLayoutOptions.For("Letter", 20, 11));
            var pages = new Dictionary<string, int>
            {
                ["epub-c0"] = 0, [book.DestinationFor("OEBPS/text/ch1.xhtml", "sub")!] = 2, ["epub-c1"] = 3, ["epub-c2"] = 9,
            };
            var nodes = EpubOutline.Build(pkg.Toc, EpubOutline.PageResolver(book, pages), 6);
            Check("epub outline: pages come from the element, else the chapter", nodes.Count == 3 && nodes[0].PageIndex == 0 && nodes[0].Children[0].PageIndex == 2
                && nodes[1].PageIndex == 3, string.Join(",", nodes.Select(n => n.PageIndex)));
            Check("epub outline: a page past the end is clamped to the last page", nodes[2].PageIndex == 5, $"{nodes[2].PageIndex}");
            var sparse = EpubOutline.Build(pkg.Toc, EpubOutline.PageResolver(book, new Dictionary<string, int> { ["epub-c1"] = 4 }), 6);
            Check("epub outline: an entry with no destination takes the page before it",
                sparse[0].PageIndex == 0 && sparse[0].Children[0].PageIndex == 0 && sparse[1].PageIndex == 4 && sparse[2].PageIndex == 4,
                string.Join(",", sparse.Select(n => n.PageIndex)));
            Check("epub outline: no pages gives no outline", EpubOutline.Build(pkg.Toc, _ => 0, 0).Count == 0, "");

            string pdf = Path.Combine(tmp, "epub-outline.pdf");
            using (var d = new PdfDocument())
            {
                for (int i = 0; i < 6; i++) d.AddPage();
                Check("epub outline: written count", EpubOutline.Apply(d, nodes) == 4, "");
                d.Save(pdf);
            }
            using (var back = PdfSharpCore.Pdf.IO.PdfReader.Open(pdf, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import))
            {
                var o = back.Outlines;
                Check("epub outline: titles, nesting and targets survive a save",
                    o.Count == 3 && o[0].Title == "One Title" && o[0].Outlines.Count == 1 && o[0].Outlines[0].Title == "Sub one"
                    && BookTestUtil.PageNo(back, o[1].DestinationPage) == 3 && BookTestUtil.PageNo(back, o[0].Outlines[0].DestinationPage) == 2, "");
            }
        }

        // ── the printed PDF: destinations in, bookmarks out ─────────────────────────────────
        {
            var book = EpubBook.Build(pkg, ar, BookLayoutOptions.For("Letter", 20, 11));
            string subName = book.DestinationFor("OEBPS/text/ch1.xhtml", "sub")!;
            string printed = Path.Combine(tmp, "epub-printed.pdf");
            using (var d = new PdfDocument())
            {
                var pgs = Enumerable.Range(0, 5).Select(_ => d.AddPage()).ToList();
                var dests = new PdfDictionary(d);
                PdfArray At(int p) => new PdfArray(d, pgs[p].Reference!, new PdfName("/XYZ"), new PdfInteger(0), new PdfInteger(700), new PdfInteger(0));
                // What Chromium writes: a legacy /Dests dictionary keyed by the destination names.
                dests.Elements["/epub-c0"] = At(0);
                dests.Elements["/" + subName] = At(1);
                dests.Elements["/epub-c1"] = At(3);
                dests.Elements["/" + book.DestinationFor("OEBPS/text/ch2.xhtml", "sec")!] = At(3);
                dests.Elements["/epub-c2"] = At(4);
                d.Internals.Catalog.Elements["/Dests"] = dests;
                d.Save(printed);
            }
            var notes = new List<string>();
            using (var d = PdfSharpCore.Pdf.IO.PdfReader.Open(printed, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify))
            {
                Check("epub pdf: outline is written from the named destinations", EpubOutline.ApplyTo(d, book, pkg, notes), "");
                Check("epub pdf: each bookmark opens the page its destination is on",
                    d.Outlines.Count == 3 && BookTestUtil.PageNo(d, d.Outlines[0].DestinationPage) == 0 && BookTestUtil.PageNo(d, d.Outlines[0].Outlines[0].DestinationPage) == 1
                    && BookTestUtil.PageNo(d, d.Outlines[1].DestinationPage) == 3 && BookTestUtil.PageNo(d, d.Outlines[2].DestinationPage) == 4, "");
            }
            using (var d = new PdfDocument())
            {
                d.AddPage();
                var n2 = new List<string>();
                Check("epub pdf: with no destinations in the file there is no outline, and a note says so",
                    !EpubOutline.ApplyTo(d, book, pkg, n2) && d.Outlines.Count == 0 && n2.Count == 1, "");
            }
        }

        // ── the zip itself ──────────────────────────────────────────────────────────────────
        {
            string zipPath = Path.Combine(tmp, "sample.epub");
            using (var fs = File.Create(zipPath))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var kv in SampleBook().Files)
                {
                    var e = z.CreateEntry(kv.Key.Replace("OEBPS/", "OEBPS\\"));   // a writer that used backslashes
                    using var s = e.Open();
                    s.Write(kv.Value);
                }
                var big = z.CreateEntry("big.bin");
                using (var s = big.Open()) s.Write(new byte[200_000]);
            }
            using var za = ZipEpubArchive.Open(zipPath);
            Check("epub zip: backslash names are normalised and the book reads", EpubPackage.Read(za).Title == "Sample Book", "");
            Check("epub zip: names match ignoring case", za.Contains("oebps/text/CH1.xhtml"), "");
            bool capped = false;
            try { za.Read("big.bin", 1000); } catch (InvalidDataException) { capped = true; }
            Check("epub zip: a file past the read cap is refused", capped, "");
            Check("epub zip: a missing file is null", za.Read("nope") is null, "");
            string notZip = Path.Combine(tmp, "bad.epub");
            File.WriteAllText(notZip, "this is not a zip");
            bool refused = false;
            try { ZipEpubArchive.Open(notZip); } catch (InvalidDataException) { refused = true; }
            Check("epub zip: a file that is not a zip is refused with a reason", refused, "");
        }

        // ── layout options and file kinds ───────────────────────────────────────────────────
        {
            var o = BookLayoutOptions.For("nonsense", 500, double.NaN);
            Check("book layout: unknown paper, wild margin and NaN text size are brought into range",
                o.PaperName == "Letter" && o.MarginMm == BookLayoutOptions.MaxMarginMm && o.FontSizePt == 11, $"{o}");
            Check("book layout: default paper follows the region", BookLayoutOptions.Default(true).PaperName == "A4" && BookLayoutOptions.Default(false).PaperName == "Letter", "");
            Check("book layout: usable height leaves room for the margins", Math.Abs(BookLayoutOptions.For("Letter", 25.4, 11).UsableHeightPt - (792 - 144)) < 0.001, "");
            Check("book kinds: extensions", BookKinds.Of("C:\\a\\B.EPUB") == BookKind.Epub && BookKinds.Of("x.djvu") == BookKind.Djvu && BookKinds.Of("x.DJV") == BookKind.Djvu
                && BookKinds.Of("x.pdf") == BookKind.None && BookKinds.Of(null) == BookKind.None, "");
            Check("book kinds: the saved name is the book's with .pdf", BookKinds.PdfNameFor("C:\\books\\Moby Dick.epub") == "Moby Dick.pdf" || BookKinds.PdfNameFor("/b/Moby Dick.epub") == "Moby Dick.pdf", "");
        }
    }
}
