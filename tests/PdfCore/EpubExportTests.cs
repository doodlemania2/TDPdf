using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TDPdf.Services;

/// <summary>
/// #247 Export as EPUB: the font licence table, the package structure, reading order and headings on
/// synthetic PDFs, the tagged path, and the scanned-page fallback.
/// </summary>
internal static class EpubExportTests
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nExport as EPUB (#247)");
        FontPolicy(Check);
        Package(Check, tmp);
        Untagged(Check, tmp);
        PositionSpaced(Check, tmp);
        Tagged(Check, tmp);
        Scanned(Check, tmp);
        Chapters(Check, tmp);
        Cancel(Check, tmp);
        DemoFiles(Check, tmp);
    }

    // ── Fonts: the fsType decision table ──────────────────────────────────────────────────────

    private static void FontPolicy(Action<string, bool, string> Check)
    {
        void Fs(ushort fs, EpubFontDecision want, string why) =>
            Check($"epub fonts: fsType 0x{fs:X4} {why}", EpubFontPolicy.FromFsType(fs) == want, EpubFontPolicy.FromFsType(fs).ToString());

        Fs(0x0000, EpubFontDecision.Embed, "installable embeds");
        Fs(0x0008, EpubFontDecision.Embed, "editable embeds");
        Fs(0x0100, EpubFontDecision.Embed, "no-subsetting alone still embeds");
        Fs(0x0004, EpubFontDecision.DeniedPreviewPrint, "preview and print does not");
        Fs(0x0002, EpubFontDecision.DeniedRestricted, "restricted does not");
        Fs(0x0200, EpubFontDecision.DeniedBitmapOnly, "bitmap-only does not");
        Fs(0x0208, EpubFontDecision.DeniedBitmapOnly, "bitmap-only wins over editable");
        Fs(0x000A, EpubFontDecision.DeniedRestricted, "restricted wins over editable");
        Fs(0x000C, EpubFontDecision.DeniedPreviewPrint, "preview wins over editable");
        Fs(0x0001, EpubFontDecision.DeniedRestricted, "the reserved bit is treated as restricted");

        var chars = new[] { (int)'A', (int)'B' };
        var covers = EpubFontFile.Inspect(SyntheticFont(0, 'A', 'Z', cff: false));
        Check("epub fonts: synthetic TrueType is read as TrueType with its fsType",
              covers.Kind == EpubFontKind.TrueType && covers.FsType == 0 && covers.Cmap != null, $"{covers.Kind} {covers.FsType}");
        Check("epub fonts: installable TrueType whose map covers the text is embedded",
              EpubFontPolicy.Decide(covers, chars) == EpubFontDecision.Embed, EpubFontPolicy.Decide(covers, chars).ToString());
        Check("epub fonts: a space needs no glyph", EpubFontPolicy.Decide(covers, new[] { (int)'A', 0x20, 0xA0 }) == EpubFontDecision.Embed, "");
        Check("epub fonts: a character the map lacks is a custom encoding, not embedded",
              EpubFontPolicy.Decide(covers, new[] { (int)'A', (int)'a' }) == EpubFontDecision.DeniedEncoding, "");
        var noCmap = new EpubFontInfo(EpubFontKind.TrueType, 0, null);
        Check("epub fonts: no usable Unicode map is not embedded", EpubFontPolicy.Decide(noCmap, chars) == EpubFontDecision.DeniedEncoding, "");
        Check("epub fonts: no OS/2 table means no licence flags, not embedded",
              EpubFontPolicy.Decide(new EpubFontInfo(EpubFontKind.TrueType, null, covers.Cmap), chars) == EpubFontDecision.DeniedNoLicenceInfo, "");
        foreach (var k in new[] { EpubFontKind.Type1, EpubFontKind.BareCff, EpubFontKind.Unknown })
            Check($"epub fonts: {k} is never embedded", EpubFontPolicy.Decide(new EpubFontInfo(k, 0, covers.Cmap), chars) == EpubFontDecision.DeniedFormat, "");
        Check("epub fonts: an unreadable font is not embedded", EpubFontPolicy.Decide(null, chars) == EpubFontDecision.DeniedNotExtractable, "");

        var restricted = EpubFontFile.Inspect(SyntheticFont(2, 'A', 'Z', cff: false));
        Check("epub fonts: a restricted file is refused through the whole path",
              EpubFontPolicy.Decide(restricted, chars) == EpubFontDecision.DeniedRestricted, "");
        var preview = EpubFontFile.Inspect(SyntheticFont(4, 'A', 'Z', cff: false));
        Check("epub fonts: a preview-only file is refused", EpubFontPolicy.Decide(preview, chars) == EpubFontDecision.DeniedPreviewPrint, "");
        var otf = EpubFontFile.Inspect(SyntheticFont(8, 'A', 'Z', cff: true));
        Check("epub fonts: editable OpenType (CFF in an sfnt) is embedded",
              otf.Kind == EpubFontKind.OpenTypeCff && EpubFontPolicy.Decide(otf, chars) == EpubFontDecision.Embed, $"{otf.Kind}");
        Check("epub fonts: a %! file is Type 1", EpubFontFile.Inspect(Encoding.ASCII.GetBytes("%!PS-AdobeFont-1.0")).Kind == EpubFontKind.Type1, "");
        Check("epub fonts: junk bytes do not throw", EpubFontFile.Inspect(new byte[] { 1, 2, 3 }).Kind == EpubFontKind.Unknown, "");
        Check("epub fonts: subset tag is stripped from the name", EpubFontPolicy.BaseName("ABCDEF+Arial-BoldMT") == "Arial-BoldMT"
              && EpubFontPolicy.BaseName("Helvetica") == "Helvetica" && EpubFontPolicy.BaseName("abcdef+X") == "abcdef+X", "");
    }

    /// <summary>A minimal sfnt: head, OS/2 (with <paramref name="fsType"/>), cmap format 12 for [lo..hi], and glyf or CFF.</summary>
    private static byte[] SyntheticFont(ushort fsType, char lo, char hi, bool cff)
    {
        var tables = new List<(string Tag, byte[] Data)>();
        tables.Add(("head", new byte[54]));
        var os2 = new byte[78];
        os2[8] = (byte)(fsType >> 8); os2[9] = (byte)fsType;
        tables.Add(("OS/2", os2));
        var cmap = new List<byte>();
        void U16(List<byte> l, int v) { l.Add((byte)(v >> 8)); l.Add((byte)v); }
        void U32(List<byte> l, uint v) { l.Add((byte)(v >> 24)); l.Add((byte)(v >> 16)); l.Add((byte)(v >> 8)); l.Add((byte)v); }
        U16(cmap, 0); U16(cmap, 1); U16(cmap, 3); U16(cmap, 10); U32(cmap, 12);
        U16(cmap, 12); U16(cmap, 0); U32(cmap, 16 + 12); U32(cmap, 0); U32(cmap, 1);
        U32(cmap, lo); U32(cmap, hi); U32(cmap, 1);
        tables.Add(("cmap", cmap.ToArray()));
        tables.Add((cff ? "CFF " : "glyf", new byte[16]));

        var f = new List<byte>();
        U32(f, cff ? 0x4F54544Fu : 0x00010000u);
        U16(f, tables.Count); U16(f, 0); U16(f, 0); U16(f, 0);
        int off = 12 + 16 * tables.Count;
        var body = new List<byte>();
        foreach (var (tag, data) in tables)
        {
            f.AddRange(Encoding.ASCII.GetBytes(tag)); U32(f, 0); U32(f, (uint)(off + body.Count)); U32(f, (uint)data.Length);
            body.AddRange(data);
            while (body.Count % 4 != 0) body.Add(0);
        }
        f.AddRange(body);
        return f.ToArray();
    }

    // ── The package ───────────────────────────────────────────────────────────────────────────

    private static EpubBook SampleBook()
    {
        var b = new EpubBook { Title = "A & B <Book>", Author = "Ann \"Q\" O'Neil", Language = "en-GB" };
        b.Images.Add(new EpubImage("img001", new byte[] { 1, 2, 3 }, "png", "image/png"));
        b.Fonts.Add(new EpubEmbeddedFont("Face", "tdpdffont1", new byte[] { 0, 1, 0, 0 }, "ttf", "font/ttf"));
        var c1 = new EpubChapter { Title = "One & only" };
        c1.Blocks.Add(new BookBlock { Kind = BookBlockKind.Heading, Level = 1, PageIndex = 0, Runs = { new EpubRun("Intro <1>", false, false, null) } });
        c1.Blocks.Add(new BookBlock { Kind = BookBlockKind.Paragraph, PageIndex = 0, Runs = { new EpubRun("Plain & simple \u0001 text ", false, false, null), new EpubRun("bold", true, false, null), new EpubRun(" font", false, false, "Face") } });
        c1.Blocks.Add(new BookBlock { Kind = BookBlockKind.ListItem, PageIndex = 0, Runs = { new EpubRun("item", false, false, null) } });
        c1.Blocks.Add(new BookBlock { Kind = BookBlockKind.Figure, PageIndex = 1, ImageId = "img001", Alt = "a \"pic\"" });
        c1.Blocks.Add(new BookBlock { Kind = BookBlockKind.Table, PageIndex = 1, Rows = new() { new() { ("H", true), ("x < y", false) } } });
        var c2 = new EpubChapter { Title = "Two" };
        c2.Blocks.Add(new BookBlock { Kind = BookBlockKind.Paragraph, PageIndex = 2, Runs = { new EpubRun("second", false, false, null) } });
        b.Chapters.Add(c1); b.Chapters.Add(c2);
        b.Nav.Add(new EpubNavEntry("One & only", 1, 0, null));
        b.Nav.Add(new EpubNavEntry("Deeper", 2, 0, "pg2"));
        b.Nav.Add(new EpubNavEntry("Two", 1, 1, null));
        return b;
    }

    private static void Package(Action<string, bool, string> Check, string tmp)
    {
        var book = SampleBook();
        using var ms = new MemoryStream();
        EpubExportPackage.Write(book, ms);
        byte[] raw = ms.ToArray();

        // Raw local header of the first entry: stored, name "mimetype", no extra field, content right after.
        bool sig = raw.Length > 60 && raw[0] == 'P' && raw[1] == 'K' && raw[2] == 3 && raw[3] == 4;
        int method = raw[8] | (raw[9] << 8), nameLen = raw[26] | (raw[27] << 8), extraLen = raw[28] | (raw[29] << 8);
        string name = Encoding.ASCII.GetString(raw, 30, nameLen);
        string content = Encoding.ASCII.GetString(raw, 30 + nameLen + extraLen, 20);
        Check("epub package: mimetype is the first entry", sig && name == "mimetype", name);
        Check("epub package: mimetype is stored, not compressed", method == 0, $"method {method}");
        Check("epub package: mimetype has no extra field and the exact content", extraLen == 0 && content == "application/epub+zip", $"{extraLen} '{content}'");

        using var zip = new ZipArchive(new MemoryStream(raw), ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        Check("epub package: container.xml points at the OPF",
              names.Contains("META-INF/container.xml") && Text(zip, "META-INF/container.xml").Contains("OEBPS/content.opf"), "");

        var opf = XDocument.Parse(Text(zip, "OEBPS/content.opf"));
        XNamespace o = "http://www.idpf.org/2007/opf", dc = "http://purl.org/dc/elements/1.1/";
        var manifest = opf.Root!.Element(o + "manifest")!.Elements(o + "item").ToList();
        var ids = manifest.Select(i => (string)i.Attribute("id")!).ToList();
        Check("epub package: manifest ids are unique", ids.Distinct().Count() == ids.Count, "");
        Check("epub package: every manifest file is in the zip",
              manifest.All(i => names.Contains("OEBPS/" + (string)i.Attribute("href")!)),
              string.Join(",", manifest.Where(i => !names.Contains("OEBPS/" + (string)i.Attribute("href")!)).Select(i => (string)i.Attribute("href")!)));
        var spine = opf.Root.Element(o + "spine")!.Elements(o + "itemref").Select(r => (string)r.Attribute("idref")!).ToList();
        Check("epub package: the spine lists every chapter, each in the manifest",
              spine.Count == 2 && spine.All(ids.Contains), string.Join(",", spine));
        Check("epub package: exactly one nav item", manifest.Count(i => ((string?)i.Attribute("properties"))?.Contains("nav") == true) == 1, "");
        Check("epub package: every file except mimetype, container and the OPF is in the manifest",
              zip.Entries.Where(e => e.FullName.StartsWith("OEBPS/") && e.FullName != "OEBPS/content.opf")
                         .All(e => manifest.Any(i => "OEBPS/" + (string)i.Attribute("href")! == e.FullName)), "");
        var md = opf.Root.Element(o + "metadata")!;
        Check("epub package: title, creator, language and a uuid identifier",
              (string)md.Element(dc + "title")! == "A & B <Book>" && (string)md.Element(dc + "creator")! == "Ann \"Q\" O'Neil"
              && (string)md.Element(dc + "language")! == "en-GB" && ((string)md.Element(dc + "identifier")!).StartsWith("urn:uuid:"), "");
        Check("epub package: modified date is present", md.Elements(o + "meta").Any(m => (string?)m.Attribute("property") == "dcterms:modified"), "");

        // Every XHTML and XML part is well formed.
        foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".xhtml") || e.FullName.EndsWith(".opf") || e.FullName.EndsWith(".ncx") || e.FullName.EndsWith("container.xml")))
        {
            string ok = WellFormed(Text(zip, e.FullName));
            Check($"epub package: {e.FullName} is well-formed XML", ok == "", ok);
        }
        string ch1 = Text(zip, "OEBPS/text/ch001.xhtml");
        Check("epub package: text is escaped and an invalid control character is dropped",
              ch1.Contains("Plain &amp; simple  text") && ch1.Contains("Intro &lt;1&gt;") && !ch1.Contains('\u0001'), "");
        Check("epub package: bold run is bold, embedded font run uses its class",
              ch1.Contains("<b>bold</b>") && ch1.Contains("<span class=\"tdpdffont1\"> font</span>"), "");
        Check("epub package: the list, the figure and the table are written",
              ch1.Contains("<ul>") && ch1.Contains("<li>item</li>") && ch1.Contains("src=\"../images/img001.png\"") && ch1.Contains("<th>H</th>"), "");
        Check("epub package: the font is declared in the stylesheet", Text(zip, "OEBPS/book.css").Contains("@font-face") && Text(zip, "OEBPS/book.css").Contains("fonts/tdpdffont1.ttf"), "");

        // The table of contents resolves, including the page anchor.
        var nav = XDocument.Parse(Text(zip, "OEBPS/nav.xhtml"));
        XNamespace x = "http://www.w3.org/1999/xhtml";
        var hrefs = nav.Descendants(x + "a").Select(a => (string)a.Attribute("href")!).ToList();
        Check("epub package: nav has one link per entry", hrefs.Count == 3, string.Join(",", hrefs));
        Check("epub package: nav nests the deeper entry", nav.Descendants(x + "ol").Count() == 2, "");
        bool resolves = true;
        foreach (var h in hrefs)
        {
            var parts = h.Split('#');
            string file = "OEBPS/" + parts[0];
            if (!names.Contains(file)) { resolves = false; break; }
            if (parts.Length > 1 && !XDocument.Parse(Text(zip, file)).Descendants().Any(d => (string?)d.Attribute("id") == parts[1])) { resolves = false; break; }
        }
        Check("epub package: every nav link lands on a file and an existing id", resolves, string.Join(",", hrefs));
        Check("epub package: toc.ncx lists the same entries", XDocument.Parse(Text(zip, "OEBPS/toc.ncx")).Descendants().Count(d => d.Name.LocalName == "navPoint") == 3, "");

        // An empty nav still produces a usable one (a link per chapter).
        book.Nav.Clear();
        using var ms2 = new MemoryStream();
        EpubExportPackage.Write(book, ms2);
        using var z2 = new ZipArchive(new MemoryStream(ms2.ToArray()), ZipArchiveMode.Read);
        Check("epub package: with no outline the nav falls back to the chapters",
              XDocument.Parse(Text(z2, "OEBPS/nav.xhtml")).Descendants(x + "a").Count() == 2, "");
    }

    private static string Text(ZipArchive z, string name)
    {
        using var r = new StreamReader(z.GetEntry(name)!.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static string WellFormed(string xml)
    {
        try
        {
            using var r = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            while (r.Read()) { }
            return "";
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ── Synthetic PDFs ────────────────────────────────────────────────────────────────────────

    private sealed class PdfBuilder
    {
        private readonly List<byte[]?> _objs = new();
        internal int Reserve() { _objs.Add(null); return _objs.Count; }
        internal int Add(string body) { _objs.Add(Encoding.Latin1.GetBytes(body)); return _objs.Count; }
        internal void Set(int id, string body) => _objs[id - 1] = Encoding.Latin1.GetBytes(body);
        internal int AddStream(string dict, byte[] data)
        {
            var ms = new MemoryStream();
            ms.Write(Encoding.Latin1.GetBytes($"<< {dict} /Length {data.Length} >>\nstream\n"));
            ms.Write(data); ms.Write(Encoding.Latin1.GetBytes("\nendstream"));
            _objs.Add(ms.ToArray()); return _objs.Count;
        }
        internal byte[] Build(int root)
        {
            var ms = new MemoryStream();
            ms.Write(Encoding.Latin1.GetBytes("%PDF-1.7\n"));
            var offs = new List<long>();
            for (int i = 0; i < _objs.Count; i++)
            {
                offs.Add(ms.Position);
                ms.Write(Encoding.Latin1.GetBytes($"{i + 1} 0 obj\n")); ms.Write(_objs[i] ?? Encoding.Latin1.GetBytes("null")); ms.Write(Encoding.Latin1.GetBytes("\nendobj\n"));
            }
            long xref = ms.Position;
            var sb = new StringBuilder($"xref\n0 {_objs.Count + 1}\n0000000000 65535 f \n");
            foreach (var o in offs) sb.Append($"{o:0000000000} 00000 n \n");
            sb.Append($"trailer\n<< /Size {_objs.Count + 1} /Root {root} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            ms.Write(Encoding.Latin1.GetBytes(sb.ToString()));
            return ms.ToArray();
        }
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static string T(double x, double y, double size, bool bold, string s) =>
        $"BT /{(bold ? "F2" : "F1")} {size} Tf {x} {y} Td ({Esc(s)}) Tj ET\n";

    /// <summary>A catalog with Helvetica as /F1 and Helvetica-Bold as /F2, and one page per content string.</summary>
    private static (PdfBuilder B, int Catalog, int Pages, List<int> PageIds, int Font1, int Font2) Skeleton(IReadOnlyList<string> contents,
        Func<int, string>? extraResources = null, Func<int, string>? extraPageEntries = null)
    {
        var b = new PdfBuilder();
        int cat = b.Reserve(), pages = b.Reserve();
        int f1 = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        int f2 = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        var ids = new List<int>();
        for (int i = 0; i < contents.Count; i++)
        {
            int c = b.AddStream("", Encoding.Latin1.GetBytes(contents[i]));
            string res = $"/Font << /F1 {f1} 0 R /F2 {f2} 0 R >> {extraResources?.Invoke(i) ?? ""}";
            ids.Add(b.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Contents {c} 0 R /Resources << {res} >> {extraPageEntries?.Invoke(i) ?? ""} >>"));
        }
        b.Set(pages, $"<< /Type /Pages /Count {ids.Count} /Kids [{string.Join(" ", ids.Select(i => i + " 0 R"))}] >>");
        return (b, cat, pages, ids, f1, f2);
    }

    private static string Write(string tmp, string name, byte[] pdf)
    {
        string p = Path.Combine(tmp, name);
        File.WriteAllBytes(p, pdf);
        return p;
    }

    private static string AllText(EpubBook b) => string.Join("\n", b.Chapters.SelectMany(c => c.Blocks).Select(x => x.Kind + ":" + x.Text));

    // ── Reading order, headings, lists, running heads ─────────────────────────────────────────

    private static void Untagged(Action<string, bool, string> Check, string tmp)
    {
        var pages = new List<string>();
        for (int p = 0; p < 5; p++)
        {
            var sb = new StringBuilder();
            sb.Append(T(72, 760, 9, false, "Acme Corp Confidential"));
            sb.Append(T(290, 30, 9, false, $"Page {p + 1}"));
            if (p == 0)
            {
                sb.Append(T(72, 700, 26, true, "Annual Report"));
                sb.Append(T(72, 660, 16, true, "Overview"));
                double y = 630;
                foreach (var l in new[] { "The company grew steadily during the year and expanded into", "several new markets with strong inter-", "national growth across all regions of the world." })
                { sb.Append(T(72, y, 11, false, l)); y -= 14; }
                y -= 14;
                foreach (var l in new[] { "- First item here", "- Second item here" }) { sb.Append(T(72, y, 11, false, l)); y -= 14; }
                y -= 14;
                foreach (var l in new[] { "1. Numbered one", "2. Numbered two" }) { sb.Append(T(72, y, 11, false, l)); y -= 14; }
            }
            else
            {
                sb.Append(T(72, 700, 16, true, $"Chapter {p}"));
                double y = 670;
                for (int i = 0; i < 4; i++) { sb.Append(T(72, y, 11, false, $"Body text line {i} of chapter {p} that keeps the body size dominant here.")); y -= 14; }
            }
            pages.Add(sb.ToString());
        }
        var (b, cat, pagesId, _, _, _) = Skeleton(pages);
        b.Set(cat, $"<< /Type /Catalog /Pages {pagesId} 0 R >>");
        string pdf = Write(tmp, "epub-untagged.pdf", b.Build(cat));

        var res = EpubExporter.Build(pdf, new EpubExportOptions { Split = EpubChapterSplit.None }, null, CancellationToken.None);
        var blocks = res.Book.Chapters.SelectMany(c => c.Blocks).ToList();
        string dump = AllText(res.Book);
        Check("epub untagged: the largest size is the h1", blocks.Any(x => x.Kind == BookBlockKind.Heading && x.Level == 1 && x.Text == "Annual Report"), dump);
        Check("epub untagged: the next size is the h2", blocks.Any(x => x.Kind == BookBlockKind.Heading && x.Level == 2 && x.Text == "Overview")
              && blocks.Any(x => x.Kind == BookBlockKind.Heading && x.Level == 2 && x.Text == "Chapter 1"), dump);
        Check("epub untagged: body text is not a heading", !blocks.Any(x => x.Kind == BookBlockKind.Heading && x.Text.StartsWith("Body text")), dump);
        Check("epub untagged: a line ending in a hyphen joins the next line into one word",
              blocks.Any(x => x.Kind == BookBlockKind.Paragraph && x.Text.Contains("strong international growth across")), dump);
        Check("epub untagged: bullets become list items without the bullet",
              blocks.Count(x => x.Kind == BookBlockKind.ListItem && !x.Ordered) == 2 && blocks.Any(x => x.Kind == BookBlockKind.ListItem && x.Text == "First item here"), dump);
        Check("epub untagged: numbers become ordered list items without the number",
              blocks.Count(x => x.Kind == BookBlockKind.ListItem && x.Ordered) == 2 && blocks.Any(x => x.Text == "Numbered two"), dump);
        Check("epub untagged: the running head and page numbers repeated on every page are dropped",
              !dump.Contains("Acme Corp") && !dump.Contains("Page 1") && !dump.Contains("Page 3"), dump);
        // The bullet characters themselves, on lines as the analyzer sees them.
        var bulletLines = new[] { "\u2022 One", "\u25AA Two", "3) Three" }.Select(t => new EpubProtoLine
            { Text = t, Runs = { new EpubRun(t, false, false, null) }, Size = 11, Chars = t.Length, Top = 100, Bottom = 90, Left = 72, Right = 200 }).ToList();
        var bl = EpubExportAnalyzer.Classify(new[] { new EpubProtoBlock { PageIndex = 0, PageHeight = 792, Lines = bulletLines } });
        Check("epub untagged: bullet glyphs and 3) markers start list items",
              bl.Count == 3 && bl.All(x => x.Kind == BookBlockKind.ListItem) && bl[0].Text == "One" && bl[1].Text == "Two" && bl[2].Ordered && bl[2].Text == "Three",
              string.Join("|", bl.Select(x => x.Kind + ":" + x.Text)));
        var kept = EpubExporter.Build(pdf, new EpubExportOptions { Split = EpubChapterSplit.None, DropHeadersAndFooters = false }, null, CancellationToken.None);
        Check("epub untagged: with the option off they are kept", AllText(kept.Book).Contains("Acme Corp"), "");
        var order = blocks.Select(x => x.Text).ToList();
        Check("epub untagged: blocks follow the page order",
              order.IndexOf("Annual Report") < order.IndexOf("Overview") && order.IndexOf("Overview") < order.IndexOf("Chapter 1") && order.IndexOf("Chapter 1") < order.IndexOf("Chapter 4"), string.Join("|", order.Take(8)));
        Check("epub untagged: text pages are not counted as tagged or scanned", res.TaggedPages == 0 && res.ScannedPages == 0, "");
        Check("epub untagged: standard fonts are never copied into the book", res.Book.Fonts.Count == 0 && res.SkippedFonts.Count == 0, string.Join(";", res.SkippedFonts));

        // Two columns: the left column is read before the right.
        var cols = new StringBuilder();
        double yy = 700;
        for (int i = 0; i < 12; i++)
        {
            cols.Append(T(60, yy, 11, false, $"left column row {i} with some more words to fill"));
            cols.Append(T(330, yy, 11, false, $"right column row {i} with some more words to fill"));
            yy -= 14;
        }
        var (b2, cat2, p2, _, _, _) = Skeleton(new[] { cols.ToString() });
        b2.Set(cat2, $"<< /Type /Catalog /Pages {p2} 0 R >>");
        var two = EpubExporter.Build(Write(tmp, "epub-columns.pdf", b2.Build(cat2)), new EpubExportOptions(), null, CancellationToken.None);
        string flat = string.Join(" ", two.Book.Chapters.SelectMany(c => c.Blocks).Select(x => x.Text));
        Check("epub untagged: two columns read left column first, then right",
              flat.IndexOf("left column row 11") >= 0 && flat.IndexOf("right column row 0") >= 0 && flat.IndexOf("left column row 11") < flat.IndexOf("right column row 0"), flat.Length > 200 ? flat[..200] : flat);
    }

    private static void PositionSpaced(Action<string, bool, string> Check, string tmp)
    {
        // Every word is placed by position; the file has no space characters at all.
        string c = "BT /F1 11 Tf 72 700 Td (Hello) Tj 42 0 Td (brave) Tj 46 0 Td (new) Tj 34 0 Td (world) Tj ET\n" +
                   "BT /F1 11 Tf 72 686 Td (second) Tj 48 0 Td (line) Tj 30 0 Td (follows) Tj ET\n";
        var (b, cat, p, _, _, _) = Skeleton(new[] { c });
        b.Set(cat, $"<< /Type /Catalog /Pages {p} 0 R >>");
        var res = EpubExporter.Build(Write(tmp, "epub-positioned.pdf", b.Build(cat)), new EpubExportOptions(), null, CancellationToken.None);
        string text = AllText(res.Book);
        Check("epub spacing: words placed by position are still separated", text.Contains("Hello brave new world"), text);
    }

    // ── Tagged PDF ────────────────────────────────────────────────────────────────────────────

    private static void Tagged(Action<string, bool, string> Check, string tmp)
    {
        // All text is the same size and weight: only the tags can say what is a heading.
        string c =
            "/H2 << /MCID 0 >> BDC\n" + T(72, 700, 11, false, "Tagged heading") + "EMC\n" +
            "/P << /MCID 1 >> BDC\n" + T(72, 680, 11, false, "A tagged paragraph of text.") + "EMC\n" +
            "/Lbl << /MCID 2 >> BDC\n" + T(72, 660, 11, false, "1.") + "EMC\n" +
            "/LBody << /MCID 3 >> BDC\n" + T(92, 660, 11, false, "First step") + "EMC\n" +
            "/P << /MCID 4 /ActualText (Replaced text) >> BDC\n" + T(72, 640, 11, false, "xx yy") + "EMC\n" +
            "/TH << /MCID 5 >> BDC\n" + T(72, 600, 11, false, "Name") + "EMC\n" +
            "/TH << /MCID 6 >> BDC\n" + T(172, 600, 11, false, "Qty") + "EMC\n" +
            "/TD << /MCID 7 >> BDC\n" + T(72, 580, 11, false, "Apples") + "EMC\n" +
            "/TD << /MCID 8 >> BDC\n" + T(172, 580, 11, false, "12") + "EMC\n" +
            "/Artifact BMC\n" + T(72, 760, 9, false, "Running header") + "EMC\n";

        var (b, cat, pagesId, pageIds, _, _) = Skeleton(new[] { c }, null, i => "/StructParents 0");
        int page = pageIds[0];
        int root = b.Reserve(), doc = b.Reserve();
        int Elem(string type, int parent, int mcid, string extra = "") =>
            b.Add($"<< /Type /StructElem /S /{type} /P {parent} 0 R /Pg {page} 0 R /K {mcid} {extra} >>");
        int h = Elem("H2", doc, 0), par = Elem("P", doc, 1);
        int li = b.Reserve(), lbl = Elem("Lbl", li, 2), lbody = Elem("LBody", li, 3);
        b.Set(li, $"<< /Type /StructElem /S /LI /P {doc} 0 R /Pg {page} 0 R /K [{lbl} 0 R {lbody} 0 R] >>");
        int actual = Elem("P", doc, 4, "/ActualText (Replaced text)");
        int table = b.Reserve(), tr1 = b.Reserve(), tr2 = b.Reserve();
        int th1 = Elem("TH", tr1, 5), th2 = Elem("TH", tr1, 6), td1 = Elem("TD", tr2, 7), td2 = Elem("TD", tr2, 8);
        b.Set(tr1, $"<< /Type /StructElem /S /TR /P {table} 0 R /Pg {page} 0 R /K [{th1} 0 R {th2} 0 R] >>");
        b.Set(tr2, $"<< /Type /StructElem /S /TR /P {table} 0 R /Pg {page} 0 R /K [{td1} 0 R {td2} 0 R] >>");
        b.Set(table, $"<< /Type /StructElem /S /Table /P {doc} 0 R /Pg {page} 0 R /K [{tr1} 0 R {tr2} 0 R] >>");
        b.Set(doc, $"<< /Type /StructElem /S /Document /P {root} 0 R /K [{h} 0 R {par} 0 R {li} 0 R {actual} 0 R {table} 0 R] >>");
        var byMcid = new[] { h, par, lbl, lbody, actual, th1, th2, td1, td2 };
        int parentTree = b.Add($"<< /Nums [0 [{string.Join(" ", byMcid.Select(i => i + " 0 R"))}]] >>");
        b.Set(root, $"<< /Type /StructTreeRoot /K {doc} 0 R /ParentTree {parentTree} 0 R >>");
        b.Set(cat, $"<< /Type /Catalog /Pages {pagesId} 0 R /MarkInfo << /Marked true >> /StructTreeRoot {root} 0 R /Lang (fr-CA) >>");
        string pdf = Write(tmp, "epub-tagged.pdf", b.Build(cat));

        var res = EpubExporter.Build(pdf, new EpubExportOptions(), null, CancellationToken.None);
        var blocks = res.Book.Chapters.SelectMany(x => x.Blocks).ToList();
        string dump = AllText(res.Book);
        Check("epub tagged: the page is read from its tags", res.TaggedPages == 1, dump);
        Check("epub tagged: H2 is a level 2 heading although it is body size and weight",
              blocks.Any(x => x.Kind == BookBlockKind.Heading && x.Level == 2 && x.Text == "Tagged heading"), dump);
        Check("epub tagged: P is a paragraph", blocks.Any(x => x.Kind == BookBlockKind.Paragraph && x.Text == "A tagged paragraph of text."), dump);
        Check("epub tagged: LI becomes one ordered list item without its label",
              blocks.Any(x => x.Kind == BookBlockKind.ListItem && x.Ordered && x.Text == "First step"), dump);
        Check("epub tagged: ActualText replaces the glyph text", blocks.Any(x => x.Text == "Replaced text") && !dump.Contains("xx yy"), dump);
        var table2 = blocks.FirstOrDefault(x => x.Kind == BookBlockKind.Table);
        Check("epub tagged: a tagged table is a table with header cells",
              table2?.Rows is { Count: 2 } rows && rows[0][0] == ("Name", true) && rows[1][1] == ("12", false), dump);
        Check("epub tagged: artifacts are left out", !dump.Contains("Running header"), dump);
        Check("epub tagged: the catalog language is used", res.Book.Language == "fr-CA", res.Book.Language);

        // A partly tagged page is read as untagged: nothing is dropped.
        string partial = c + T(72, 500, 11, false, "Untagged trailing text that the tree does not mention at all.");
        var (b2, cat2, p2, pg2, _, _) = Skeleton(new[] { partial }, null, i => "/StructParents 0");
        int r2 = b2.Reserve(); int d2 = b2.Reserve();
        int e2 = b2.Add($"<< /Type /StructElem /S /P /P {d2} 0 R /Pg {pg2[0]} 0 R /K 1 >>");
        b2.Set(d2, $"<< /Type /StructElem /S /Document /P {r2} 0 R /K [{e2} 0 R] >>");
        int pt = b2.Add($"<< /Nums [0 [null {e2} 0 R]] >>");
        b2.Set(r2, $"<< /Type /StructTreeRoot /K {d2} 0 R /ParentTree {pt} 0 R >>");
        b2.Set(cat2, $"<< /Type /Catalog /Pages {p2} 0 R /MarkInfo << /Marked true >> /StructTreeRoot {r2} 0 R >>");
        var part = EpubExporter.Build(Write(tmp, "epub-partial.pdf", b2.Build(cat2)), new EpubExportOptions(), null, CancellationToken.None);
        Check("epub tagged: a partly tagged page falls back to reading it as untagged",
              part.TaggedPages == 0 && AllText(part.Book).Contains("Untagged trailing text"), AllText(part.Book));
    }

    // ── Scanned pages ─────────────────────────────────────────────────────────────────────────

    private static void Scanned(Action<string, bool, string> Check, string tmp)
    {
        // Page 1: text. Page 2: a picture only (a scan). Page 3: blank. Page 4: a picture inside a text page.
        var img = new byte[32 * 32 * 3];
        for (int i = 0; i < img.Length; i += 3) { img[i] = (byte)(i % 251); img[i + 1] = 40; img[i + 2] = (byte)(255 - i % 200); }
        var contents = new[]
        {
            T(72, 700, 11, false, "A page with text on it."),
            "q 612 0 0 792 0 0 cm /Im0 Do Q\n",
            "",
            T(72, 700, 11, false, "Text above the picture.") + "q 200 0 0 200 100 400 cm /Im0 Do Q\n",
        };
        int im;
        var b = new PdfBuilder();
        int cat = b.Reserve(), pages = b.Reserve();
        int f1 = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        im = b.AddStream("/Type /XObject /Subtype /Image /Width 32 /Height 32 /ColorSpace /DeviceRGB /BitsPerComponent 8", img);
        var ids = new List<int>();
        for (int i = 0; i < contents.Length; i++)
        {
            int c = b.AddStream("", Encoding.Latin1.GetBytes(contents[i]));
            string xo = i is 1 or 3 ? $"/XObject << /Im0 {im} 0 R >>" : "";
            ids.Add(b.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Contents {c} 0 R /Resources << /Font << /F1 {f1} 0 R >> {xo} >> >>"));
        }
        b.Set(pages, $"<< /Type /Pages /Count {ids.Count} /Kids [{string.Join(" ", ids.Select(i => i + " 0 R"))}] >>");
        b.Set(cat, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        string pdf = Write(tmp, "epub-scan.pdf", b.Build(cat));

        var res = EpubExporter.Build(pdf, new EpubExportOptions { Split = EpubChapterSplit.None }, null, CancellationToken.None);
        var blocks = res.Book.Chapters.SelectMany(x => x.Blocks).ToList();
        var scan = blocks.FirstOrDefault(x => x.Kind == BookBlockKind.Figure && x.PageIndex == 1);
        Check("epub scanned: a page with no text becomes a full-page picture", scan != null && res.ScannedPages == 1, AllText(res.Book));
        var scanImg = res.Book.Images.FirstOrDefault(i => i.Id == scan?.ImageId);
        bool valid = scanImg != null && (scanImg.Bytes.Take(4).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) || scanImg.Bytes.Take(2).SequenceEqual(new byte[] { 0xFF, 0xD8 }));
        Check("epub scanned: the picture is a real PNG or JPEG", valid, scanImg is null ? "none" : $"{scanImg.Extension} {scanImg.Bytes.Length} bytes");
        Check("epub scanned: a blank page adds nothing", !blocks.Any(x => x.PageIndex == 2), "");
        Check("epub scanned: the text pages keep their text", blocks.Any(x => x.Text == "A page with text on it.") && blocks.Any(x => x.Text == "Text above the picture."), AllText(res.Book));
        var inline = blocks.FirstOrDefault(x => x.Kind == BookBlockKind.Figure && x.PageIndex == 3);
        Check("epub scanned: a picture inside a text page is placed with that page's text",
              inline != null && blocks.IndexOf(inline) > blocks.FindIndex(x => x.Text == "A page with text on it."), AllText(res.Book));
        Check("epub scanned: pictures can be turned off", !EpubExporter.Build(pdf, new EpubExportOptions { IncludePictures = false }, null, CancellationToken.None)
              .Book.Chapters.SelectMany(x => x.Blocks).Any(x => x.Kind == BookBlockKind.Figure && x.PageIndex == 3), "");

        // And the whole thing as a file.
        string outPath = Path.Combine(tmp, "scan.epub");
        EpubExporter.Export(pdf, outPath, new EpubExportOptions(), null, CancellationToken.None);
        using var zip = ZipFile.OpenRead(outPath);
        Check("epub scanned: the exported file holds the pictures", zip.Entries.Count(e => e.FullName.StartsWith("OEBPS/images/")) >= 1, "");
    }

    // ── Chapters from bookmarks ───────────────────────────────────────────────────────────────

    private static void Chapters(Action<string, bool, string> Check, string tmp)
    {
        var contents = Enumerable.Range(0, 4).Select(i => T(72, 500, 11, false, $"Text on page {i + 1} of the book, long enough to be a line.")).ToList();
        var (b, cat, pagesId, pg, _, _) = Skeleton(contents);
        int outlines = b.Reserve(), a = b.Reserve(), c = b.Reserve(), sub = b.Reserve();
        b.Set(a, $"<< /Title (Part One) /Parent {outlines} 0 R /Next {c} 0 R /First {sub} 0 R /Last {sub} 0 R /Count 1 /Dest [{pg[0]} 0 R /Fit] >>");
        b.Set(sub, $"<< /Title (Section 1.1) /Parent {a} 0 R /Dest [{pg[1]} 0 R /Fit] >>");
        b.Set(c, $"<< /Title (Part Two) /Parent {outlines} 0 R /Prev {a} 0 R /Dest [{pg[2]} 0 R /Fit] >>");
        b.Set(outlines, $"<< /Type /Outlines /First {a} 0 R /Last {c} 0 R /Count 3 >>");
        b.Set(cat, $"<< /Type /Catalog /Pages {pagesId} 0 R /Outlines {outlines} 0 R >>");
        string pdf = Write(tmp, "epub-outline.pdf", b.Build(cat));

        var res = EpubExporter.Build(pdf, new EpubExportOptions(), null, CancellationToken.None);
        var ch = res.Book.Chapters;
        Check("epub chapters: split at the top-level bookmarks", ch.Count == 2 && ch[0].Title == "Part One" && ch[1].Title == "Part Two", string.Join("|", ch.Select(x => x.Title)));
        Check("epub chapters: pages go to the chapter whose bookmark starts at or before them",
              ch[0].Blocks.All(x => x.PageIndex <= 1) && ch[1].Blocks.All(x => x.PageIndex >= 2) && ch[1].Blocks.Count == 2, "");
        var nav = res.Book.Nav;
        Check("epub chapters: the outline becomes the table of contents, nested",
              nav.Count == 3 && nav[0].Level == 1 && nav[1].Level == 2 && nav[1].Title == "Section 1.1" && nav[1].Fragment == "pg2" && nav[2].ChapterIndex == 1, string.Join("|", nav.Select(n => $"{n.Title}/{n.Level}/{n.Fragment}")));

        var none = EpubExporter.Build(pdf, new EpubExportOptions { Split = EpubChapterSplit.None }, null, CancellationToken.None);
        Check("epub chapters: 'none' keeps one chapter", none.Book.Chapters.Count == 1, "");
        var some = EpubExporter.Build(pdf, new EpubExportOptions { Pages = new[] { 2, 3 } }, null, CancellationToken.None);
        Check("epub chapters: a page range exports just those pages", some.Pages == 2 && some.Book.Chapters.SelectMany(x => x.Blocks).All(x => x.PageIndex >= 2), "");

        // The written file validates end to end.
        string outPath = Path.Combine(tmp, "outline.epub");
        EpubExporter.Export(pdf, outPath, new EpubExportOptions(), null, CancellationToken.None);
        using var zip = ZipFile.OpenRead(outPath);
        bool allWell = zip.Entries.Where(e => e.FullName.EndsWith(".xhtml") || e.FullName.EndsWith(".opf") || e.FullName.EndsWith(".ncx"))
                                  .All(e => WellFormed(Text(zip, e.FullName)) == "");
        Check("epub chapters: every XHTML part of a real export is well formed", allWell, "");
        Check("epub chapters: first entry of a real export is mimetype, stored", zip.Entries[0].FullName == "mimetype" && zip.Entries[0].CompressedLength == zip.Entries[0].Length, zip.Entries[0].FullName);
        var nx = XDocument.Parse(Text(zip, "OEBPS/nav.xhtml"));
        XNamespace x = "http://www.w3.org/1999/xhtml";
        bool links = nx.Descendants(x + "a").Select(l => (string)l.Attribute("href")!).All(h =>
        {
            var parts = h.Split('#');
            var e = zip.GetEntry("OEBPS/" + parts[0]);
            return e != null && (parts.Length == 1 || XDocument.Parse(Text(zip, e.FullName)).Descendants().Any(d => (string?)d.Attribute("id") == parts[1]));
        });
        Check("epub chapters: every table-of-contents link of a real export lands", links, "");
    }

    // ── Cancel and never overwrite silently ───────────────────────────────────────────────────

    private static void Cancel(Action<string, bool, string> Check, string tmp)
    {
        var (b, cat, p, _, _, _) = Skeleton(Enumerable.Range(0, 6).Select(i => T(72, 700, 11, false, $"Page {i} text line.")).ToList());
        b.Set(cat, $"<< /Type /Catalog /Pages {p} 0 R >>");
        string pdf = Write(tmp, "epub-cancel.pdf", b.Build(cat));
        string outPath = Path.Combine(tmp, "keep.epub");
        File.WriteAllText(outPath, "existing book");

        using var cts = new CancellationTokenSource();
        int seen = 0;
        bool cancelled = false;
        try { EpubExporter.Export(pdf, outPath, new EpubExportOptions(), (done, total) => { seen = done; if (done == 2) cts.Cancel(); }, cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check("epub cancel: cancelling stops the export", cancelled && seen == 2, $"cancelled={cancelled} seen={seen}");
        Check("epub cancel: a cancelled export leaves the existing file untouched", File.ReadAllText(outPath) == "existing book", "");
        Check("epub cancel: and no staging file is left beside it", !Directory.GetFiles(tmp, ".tdpdf_save_*").Any(), "");

        var ticks = new List<(int, int)>();
        EpubExporter.Build(pdf, new EpubExportOptions(), (d, t) => ticks.Add((d, t)), CancellationToken.None);
        Check("epub cancel: progress reports every page in order", ticks.Count == 6 && ticks[0] == (1, 6) && ticks[5] == (6, 6), string.Join(",", ticks));
    }

    // ── The demo PDFs the product is shown with ───────────────────────────────────────────────

    private static void DemoFiles(Action<string, bool, string> Check, string tmp)
    {
        foreach (string name in new[] { "incident-report", "membership-form", "services-agreement" })
        {
            string? src = null;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && src is null; dir = dir.Parent)
            {
                string c = Path.Combine(dir.FullName, "build/screenshots/demo", name + ".pdf");
                if (File.Exists(c)) src = c;
            }
            if (src is null) { Check($"epub demo: {name}.pdf found", false, "not found"); continue; }
            string outPath = Path.Combine(tmp, name + ".epub");
            var res = EpubExporter.Export(src, outPath, new EpubExportOptions(), null, CancellationToken.None);
            using var zip = ZipFile.OpenRead(outPath);
            bool well = zip.Entries.Where(e => e.FullName.EndsWith(".xhtml") || e.FullName.EndsWith(".opf") || e.FullName.EndsWith(".ncx"))
                                   .All(e => WellFormed(Text(zip, e.FullName)) == "");
            string text = string.Join(" ", res.Book.Chapters.SelectMany(c => c.Blocks).Select(b => b.Text));
            Check($"epub demo: {name}.pdf exports to a well-formed book with its text",
                  well && zip.Entries[0].FullName == "mimetype" && text.Length > 100,
                  $"{text.Length} chars, {res.Book.Chapters.Count} chapter(s), fonts embedded [{string.Join(",", res.EmbeddedFonts)}] skipped [{string.Join("; ", res.SkippedFonts)}]");
            // Whatever fonts went in, each is a TrueType/OpenType file whose licence flags allow it.
            bool licensed = res.Book.Fonts.All(f =>
            {
                var info = EpubFontFile.Inspect(f.Bytes);
                return info.Kind is EpubFontKind.TrueType or EpubFontKind.OpenTypeCff && info.FsType is { } fs && EpubFontPolicy.FromFsType(fs) == EpubFontDecision.Embed;
            });
            Check($"epub demo: {name}.pdf embeds only fonts whose licence allows it", licensed, "");
        }
    }
}
