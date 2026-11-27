using System.IO;
using System.IO.Compression;
using System.Text;

namespace TDPdf.Services
{
    /// <summary>
    /// Writes an <see cref="EpubExportBook"/> as an EPUB 3 package: the mimetype entry first and stored,
    /// container.xml, an OPF package document, a nav document built from the outline, an NCX for
    /// EPUB 2 readers, one stylesheet, the chapters as XHTML, and the pictures and fonts.
    /// </summary>
    internal static class EpubExportPackage
    {
        private const string Xhtml = "http://www.w3.org/1999/xhtml";

        internal static string ChapterPath(int index) => $"text/ch{index + 1:000}.xhtml";

        internal static void Write(EpubExportBook book, Stream output)
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: new UTF8Encoding(false));

            // The first entry, stored (not compressed), with no extra field: how readers recognise an EPUB.
            var mt = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var s = mt.Open()) s.Write(Encoding.ASCII.GetBytes("application/epub+zip"));

            Add(zip, "META-INF/container.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
                "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");

            Add(zip, "OEBPS/content.opf", Opf(book));
            Add(zip, "OEBPS/nav.xhtml", Nav(book));
            Add(zip, "OEBPS/toc.ncx", Ncx(book));
            Add(zip, "OEBPS/book.css", Css(book));
            for (int i = 0; i < book.Chapters.Count; i++)
                Add(zip, "OEBPS/" + ChapterPath(i), Chapter(book, book.Chapters[i], i));
            foreach (var img in book.Images) AddBytes(zip, $"OEBPS/images/{img.Id}.{img.Extension}", img.Bytes);
            foreach (var f in book.Fonts) AddBytes(zip, $"OEBPS/fonts/{f.Family}.{f.Extension}", f.Bytes);
        }

        private static void Add(ZipArchive zip, string name, string text) => AddBytes(zip, name, new UTF8Encoding(false).GetBytes(text));

        private static void AddBytes(ZipArchive zip, string name, byte[] bytes)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var s = e.Open();
            s.Write(bytes);
        }

        // ── OPF ───────────────────────────────────────────────────────────────────────────────

        private static string Opf(EpubExportBook b)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\" xml:lang=\"")
              .Append(EpubExportXml.Escape(b.Language)).Append("\">\n<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n");
            sb.Append("<dc:identifier id=\"bookid\">").Append(EpubExportXml.Escape(b.Identifier)).Append("</dc:identifier>\n");
            sb.Append("<dc:title>").Append(EpubExportXml.Escape(b.Title)).Append("</dc:title>\n");
            if (!string.IsNullOrWhiteSpace(b.Author)) sb.Append("<dc:creator>").Append(EpubExportXml.Escape(b.Author)).Append("</dc:creator>\n");
            sb.Append("<dc:language>").Append(EpubExportXml.Escape(b.Language)).Append("</dc:language>\n");
            sb.Append("<meta property=\"dcterms:modified\">").Append(b.ModifiedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")).Append("</meta>\n");
            sb.Append("</metadata>\n<manifest>\n");
            sb.Append("<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>\n");
            sb.Append("<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>\n");
            sb.Append("<item id=\"css\" href=\"book.css\" media-type=\"text/css\"/>\n");
            for (int i = 0; i < b.Chapters.Count; i++)
                sb.Append($"<item id=\"ch{i + 1:000}\" href=\"{ChapterPath(i)}\" media-type=\"application/xhtml+xml\"/>\n");
            foreach (var img in b.Images)
                sb.Append($"<item id=\"{img.Id}\" href=\"images/{img.Id}.{img.Extension}\" media-type=\"{img.MediaType}\"/>\n");
            foreach (var f in b.Fonts)
                sb.Append($"<item id=\"{f.Family}\" href=\"fonts/{f.Family}.{f.Extension}\" media-type=\"{f.MediaType}\"/>\n");
            sb.Append("</manifest>\n<spine toc=\"ncx\">\n");
            for (int i = 0; i < b.Chapters.Count; i++) sb.Append($"<itemref idref=\"ch{i + 1:000}\"/>\n");
            sb.Append("</spine>\n</package>\n");
            return sb.ToString();
        }

        // ── Navigation ────────────────────────────────────────────────────────────────────────

        private static string Href(EpubNavEntry e) => ChapterPath(e.ChapterIndex) + (e.Fragment is null ? "" : "#" + e.Fragment);

        private static string Nav(EpubExportBook b)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append($"<html xmlns=\"{Xhtml}\" xmlns:epub=\"http://www.idpf.org/2007/ops\" lang=\"{EpubExportXml.Escape(b.Language)}\" xml:lang=\"{EpubExportXml.Escape(b.Language)}\">\n");
            sb.Append("<head><meta charset=\"utf-8\"/><title>").Append(EpubExportXml.Escape(b.Title)).Append("</title></head>\n<body>\n");
            sb.Append("<nav epub:type=\"toc\" id=\"toc\"><h1>Contents</h1>\n");
            var entries = b.Nav.Count > 0 ? b.Nav : b.Chapters.Select((c, i) => new EpubNavEntry(c.Title, 1, i, null)).ToList();
            AppendNavList(sb, entries);
            sb.Append("</nav>\n</body>\n</html>\n");
            return sb.ToString();
        }

        /// <summary>Nested ol from a flat list with levels (1-based). A level that jumps down is clamped to one deeper.</summary>
        private static void AppendNavList(StringBuilder sb, List<EpubNavEntry> entries)
        {
            if (entries.Count == 0) return;
            int depth = 0;
            sb.Append("<ol>\n");
            depth = 1;
            bool open = false;   // an <li> is open at the current depth
            int prevLevel = 1;
            for (int i = 0; i < entries.Count; i++)
            {
                int level = Math.Max(1, Math.Min(entries[i].Level, prevLevel + 1));
                if (i == 0) level = 1;
                while (depth > level) { sb.Append("</li>\n</ol>\n"); depth--; open = true; }
                if (depth < level) { sb.Append("\n<ol>\n"); depth++; open = false; }
                else if (open) sb.Append("</li>\n");
                sb.Append("<li><a href=\"").Append(EpubExportXml.Escape(Href(entries[i]))).Append("\">")
                  .Append(EpubExportXml.Escape(string.IsNullOrWhiteSpace(entries[i].Title) ? "Untitled" : entries[i].Title)).Append("</a>");
                open = true;
                prevLevel = level;
            }
            while (depth > 1) { sb.Append("</li>\n</ol>\n"); depth--; }
            sb.Append("</li>\n</ol>\n");
        }

        private static string Ncx(EpubExportBook b)
        {
            var entries = b.Nav.Count > 0 ? b.Nav : b.Chapters.Select((c, i) => new EpubNavEntry(c.Title, 1, i, null)).ToList();
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\">\n<head>");
            sb.Append("<meta name=\"dtb:uid\" content=\"").Append(EpubExportXml.Escape(b.Identifier)).Append("\"/>");
            sb.Append("<meta name=\"dtb:depth\" content=\"1\"/><meta name=\"dtb:totalPageCount\" content=\"0\"/><meta name=\"dtb:maxPageNumber\" content=\"0\"/></head>\n");
            sb.Append("<docTitle><text>").Append(EpubExportXml.Escape(b.Title)).Append("</text></docTitle>\n<navMap>\n");
            for (int i = 0; i < entries.Count; i++)
            {
                sb.Append($"<navPoint id=\"np{i + 1}\" playOrder=\"{i + 1}\"><navLabel><text>")
                  .Append(EpubExportXml.Escape(string.IsNullOrWhiteSpace(entries[i].Title) ? "Untitled" : entries[i].Title))
                  .Append("</text></navLabel><content src=\"").Append(EpubExportXml.Escape(Href(entries[i]))).Append("\"/></navPoint>\n");
            }
            sb.Append("</navMap>\n</ncx>\n");
            return sb.ToString();
        }

        // ── Style and chapters ────────────────────────────────────────────────────────────────

        private static string Css(EpubExportBook b)
        {
            var sb = new StringBuilder();
            foreach (var f in b.Fonts)
                sb.Append($"@font-face {{ font-family: \"{f.Family}\"; src: url(\"fonts/{f.Family}.{f.Extension}\"); }}\n");
            sb.Append("body { line-height: 1.4; margin: 0 1em; }\n");
            sb.Append("h1, h2, h3, h4, h5, h6 { line-height: 1.2; margin: 1.2em 0 0.5em; }\n");
            sb.Append("p { margin: 0 0 0.8em; text-align: left; }\n");
            sb.Append("figure { margin: 1em 0; text-align: center; }\n");
            sb.Append("img { max-width: 100%; height: auto; }\n");
            sb.Append("figcaption { font-size: 0.9em; }\n");
            sb.Append("table { border-collapse: collapse; margin: 1em 0; }\n");
            sb.Append("td, th { border: 1px solid #888; padding: 0.2em 0.5em; text-align: left; }\n");
            foreach (var f in b.Fonts) sb.Append($".{f.Family} {{ font-family: \"{f.Family}\", serif; }}\n");
            return sb.ToString();
        }

        private static string Chapter(EpubExportBook b, EpubChapter ch, int index)
        {
            var embedded = b.Fonts.ToDictionary(f => f.Key, f => f.Family);
            var anchors = b.Nav.Where(n => n.ChapterIndex == index && n.Fragment != null).Select(n => n.Fragment!).ToHashSet();
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append($"<html xmlns=\"{Xhtml}\" lang=\"{EpubExportXml.Escape(b.Language)}\" xml:lang=\"{EpubExportXml.Escape(b.Language)}\">\n");
            sb.Append("<head><meta charset=\"utf-8\"/><title>").Append(EpubExportXml.Escape(string.IsNullOrWhiteSpace(ch.Title) ? b.Title : ch.Title))
              .Append("</title><link rel=\"stylesheet\" type=\"text/css\" href=\"../book.css\"/></head>\n<body>\n");

            int lastPage = int.MinValue;
            string? openList = null;
            int headingNo = 0;
            void CloseList() { if (openList != null) { sb.Append("</").Append(openList).Append(">\n"); openList = null; } }

            foreach (var blk in ch.Blocks)
            {
                if (blk.PageIndex != lastPage)
                {
                    lastPage = blk.PageIndex;
                    string id = "pg" + (blk.PageIndex + 1);
                    if (anchors.Contains(id)) { CloseList(); sb.Append("<span id=\"").Append(id).Append("\"></span>\n"); }
                }
                if (blk.Kind != BookBlockKind.ListItem) CloseList();
                switch (blk.Kind)
                {
                    case BookBlockKind.Heading:
                    {
                        headingNo++;
                        int lv = Math.Clamp(blk.Level, 1, 6);
                        sb.Append($"<h{lv} id=\"h{index + 1}-{headingNo}\">").Append(EpubExportXml.Escape(blk.Text)).Append($"</h{lv}>\n");
                        break;
                    }
                    case BookBlockKind.Paragraph:
                        if (blk.Text.Length == 0) break;
                        sb.Append("<p>"); AppendRuns(sb, blk.Runs, embedded); sb.Append("</p>\n");
                        break;
                    case BookBlockKind.ListItem:
                    {
                        string tag = blk.Ordered ? "ol" : "ul";
                        if (openList != tag) { CloseList(); sb.Append('<').Append(tag).Append(">\n"); openList = tag; }
                        sb.Append("<li>"); AppendRuns(sb, blk.Runs, embedded); sb.Append("</li>\n");
                        break;
                    }
                    case BookBlockKind.Figure:
                    {
                        var img = b.Images.FirstOrDefault(i => i.Id == blk.ImageId);
                        if (img is null) break;
                        sb.Append("<figure><img src=\"../images/").Append(img.Id).Append('.').Append(img.Extension)
                          .Append("\" alt=\"").Append(EpubExportXml.Escape(blk.Alt ?? "")).Append("\"/></figure>\n");
                        break;
                    }
                    case BookBlockKind.Table:
                        sb.Append("<table>\n");
                        foreach (var row in blk.Rows ?? new())
                        {
                            sb.Append("<tr>");
                            foreach (var (text, header) in row)
                                sb.Append(header ? "<th>" : "<td>").Append(EpubExportXml.Escape(text)).Append(header ? "</th>" : "</td>");
                            sb.Append("</tr>\n");
                        }
                        sb.Append("</table>\n");
                        break;
                }
            }
            CloseList();
            sb.Append("</body>\n</html>\n");
            return sb.ToString();
        }

        private static void AppendRuns(StringBuilder sb, List<EpubRun> runs, Dictionary<string, string> embedded)
        {
            foreach (var r in runs)
            {
                if (r.Text.Length == 0) continue;
                string text = EpubExportXml.Escape(r.Text);
                if (r.FontKey != null && embedded.TryGetValue(r.FontKey, out var fam))
                    sb.Append("<span class=\"").Append(fam).Append("\">").Append(text).Append("</span>");
                else
                {
                    if (r.Bold) sb.Append("<b>");
                    if (r.Italic) sb.Append("<i>");
                    sb.Append(text);
                    if (r.Italic) sb.Append("</i>");
                    if (r.Bold) sb.Append("</b>");
                }
            }
        }
    }
}
