using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PdfSharpCore.Pdf.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Outline;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf.Services
{
    /// <summary>
    /// File ▸ Export as EPUB, and the CLI's <c>--export-epub</c>. Reads a PDF page by page, works out
    /// its structure, and writes a reflowable EPUB 3 book. Nothing here touches a window or the open document.
    /// </summary>
    internal static class EpubExporter
    {
        private const int MinPicturePoints = 16;

        private sealed record OutlineItem(string Title, int Level, int PageIndex);

        /// <summary>Analyses the PDF and builds the book in memory.</summary>
        internal static EpubExportResult Build(string pdfPath, EpubExportOptions opt, Action<int, int>? progress, CancellationToken ct)
        {
            var result = new EpubExportResult();
            var book = result.Book;
            using var pig = PdfPigDoc.Open(pdfPath);
            int total = pig.NumberOfPages;
            var selected = (opt.Pages ?? Enumerable.Range(0, total).ToList()).Where(p => p >= 0 && p < total).Distinct().OrderBy(p => p).ToList();
            result.Pages = selected.Count;

            using var session = PdfiumInterop.EpubSession.Open(pdfPath);
            if (session is null) result.Notes.Add("Pictures, tagged structure and fonts could not be read from this file, so the book is text only.");

            ReadMetadata(pdfPath, pig, book);

            var images = new Dictionary<string, EpubImage>();       // id -> image
            var imageByHash = new Dictionary<string, string>();
            string AddImage(byte[] bytes, string ext, string media)
            {
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (imageByHash.TryGetValue(hash, out var existing)) return existing;
                string id = $"img{images.Count + 1:000}";
                images[id] = new EpubImage(id, bytes, ext, media);
                imageByHash[hash] = id;
                return id;
            }

            var fontBag = new Dictionary<string, List<byte[]>>();
            var seenFonts = new HashSet<string>();
            var usedChars = new Dictionary<string, HashSet<int>>();

            var pageBlocks = new Dictionary<int, List<BookBlock>>();
            var protos = new List<EpubProtoBlock>();
            var pendingFigures = new Dictionary<int, List<(PdfRectangle, string)>>();

            int done = 0;
            foreach (int i in selected)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(++done, selected.Count);

                var page = pig.GetPage(i + 1);
                List<Word> words;
                try { words = page.GetWords(NearestNeighbourWordExtractor.Instance).Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList(); }
                catch { words = new(); }

                foreach (var l in page.Letters)
                {
                    if (string.IsNullOrEmpty(l.Value) || string.IsNullOrWhiteSpace(l.Value)) continue;
                    string key = EpubFontPolicy.BaseName(l.FontName);
                    if (!usedChars.TryGetValue(key, out var set)) usedChars[key] = set = new();
                    set.Add(char.IsHighSurrogate(l.Value[0]) && l.Value.Length > 1 ? char.ConvertToUtf32(l.Value[0], l.Value[1]) : l.Value[0]);
                }
                if (opt.EmbedFonts && words.Count > 0) session?.CollectFonts(i, fontBag, seenFonts);

                PageRaster? raster = null;
                PageRaster Raster(double dpi)
                {
                    if (raster != null) return raster;
                    double scale = dpi / 72.0;
                    int w = Math.Max(1, (int)Math.Round(page.Width * scale)), h = Math.Max(1, (int)Math.Round(page.Height * scale));
                    double shrink = Math.Min(1.0, 4500.0 / Math.Max(w, h));
                    w = Math.Max(1, (int)(w * shrink)); h = Math.Max(1, (int)(h * shrink));
                    return raster = new PageRaster(session?.RenderPage(i, w, h), w, h);
                }

                string? Picture(PdfRectangle b)
                {
                    if (!opt.IncludePictures || session is null) return null;
                    double bw = Math.Abs(b.Right - b.Left), bh = Math.Abs(b.Top - b.Bottom);
                    if (bw < MinPicturePoints || bh < MinPicturePoints) return null;
                    var r = Raster(PictureDpi(page));
                    return r.Pixels is null ? null : CropAndEncode(r, b, page.Width, page.Height, AddImage);
                }

                if (words.Count == 0)
                {
                    // No text at all: a scan, a drawing, or a blank page. Anything visible goes in as a picture.
                    var scan = session is null ? null : ScannedPage(session, i, page, AddImage);
                    if (scan is not null)
                    {
                        result.ScannedPages++;
                        pageBlocks[i] = new() { scan };
                    }
                    else if (session is null) result.Notes.Add($"Page {i + 1} has no text and could not be read as a picture.");
                    continue;
                }

                List<BookBlock>? tagged = null;
                var structure = session?.ReadStructure(i);
                if (structure is { Count: > 0 })
                {
                    tagged = EpubExportAnalyzer.AnalyzeTagged(page, i, structure, Picture);
                    if (tagged is not null) result.TaggedPages++;
                }
                if (tagged is not null) { pageBlocks[i] = tagged; continue; }

                protos.AddRange(EpubExportAnalyzer.SegmentPage(page, i, words));
                if (opt.IncludePictures && session is not null)
                {
                    var figs = new List<(PdfRectangle, string)>();
                    try
                    {
                        foreach (var img in page.GetImages())
                        {
                            var bb = img.BoundingBox;
                            double w = Math.Abs(bb.Right - bb.Left), h = Math.Abs(bb.Top - bb.Bottom);
                            // A picture behind the whole page is its background, not content.
                            if (w >= page.Width * 0.9 && h >= page.Height * 0.9) continue;
                            if (Picture(bb) is { } id) figs.Add((Norm(bb), id));
                        }
                    }
                    catch { /* a picture that cannot be read is skipped, the text is already safe */ }
                    if (figs.Count > 0) pendingFigures[i] = figs;
                }
            }

            // Untagged pages: heading levels need the whole document's sizes, so they are decided last.
            var classified = EpubExportAnalyzer.Classify(protos);
            if (opt.DropHeadersAndFooters) classified = EpubExportAnalyzer.DropRunningHeads(classified, selected.Count);
            foreach (var g in classified.GroupBy(b => b.PageIndex))
                pageBlocks[g.Key] = g.ToList();
            foreach (var (pi, figs) in pendingFigures)
            {
                var list = pageBlocks.TryGetValue(pi, out var l) ? l : new List<BookBlock>();
                pageBlocks[pi] = EpubExportAnalyzer.PlaceFigures(list, figs, pi, pig.GetPage(pi + 1).Height);
            }

            var all = selected.Where(pageBlocks.ContainsKey).SelectMany(p => pageBlocks[p]).ToList();
            result.PictureCount = images.Count;
            book.Images = images.Values.ToList();

            DecideFonts(opt, fontBag, usedChars, result);
            BuildChapters(pig, opt, selected, all, book);
            if (book.Chapters.Count == 0)
            {
                book.Chapters.Add(new EpubChapter { Title = book.Title, Blocks = { new BookBlock { Kind = BookBlockKind.Paragraph, Runs = new() { new EpubRun("This document has no text or pictures to export.", false, false, null) } } } });
                result.Notes.Add("The PDF has nothing to export.");
            }
            return result;
        }

        /// <summary>Builds the book and writes it to <paramref name="outPath"/> in one piece, replacing nothing until it is complete.</summary>
        internal static EpubExportResult Export(string pdfPath, string outPath, EpubExportOptions opt, Action<int, int>? progress, CancellationToken ct)
        {
            var result = Build(pdfPath, opt, progress, ct);
            AtomicFileWriter.Write(staging =>
            {
                using var fs = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None);
                EpubExportPackage.Write(result.Book, fs);
            }, outPath, ct);
            return result;
        }

        // ── Pictures ──────────────────────────────────────────────────────────────────────────

        private sealed record PageRaster(byte[]? Pixels, int Width, int Height);

        private static double PictureDpi(Page page)
        {
            double best = 150;
            try
            {
                foreach (var img in page.GetImages())
                {
                    double w = Math.Abs(img.BoundingBox.Right - img.BoundingBox.Left);
                    if (w > 1) best = Math.Max(best, img.WidthInSamples / (w / 72.0));
                }
            }
            catch { }
            return Math.Clamp(best, 110, 300);
        }

        private static PdfRectangle Norm(PdfRectangle b) => new(Math.Min(b.Left, b.Right), Math.Min(b.Bottom, b.Top), Math.Max(b.Left, b.Right), Math.Max(b.Bottom, b.Top));

        private static string? CropAndEncode(PageRaster r, PdfRectangle b, double pw, double ph, Func<byte[], string, string, string> add)
        {
            if (!(pw > 0) || !(ph > 0) || r.Pixels is null) return null;
            double l = Math.Min(b.Left, b.Right) / pw, rr = Math.Max(b.Left, b.Right) / pw;
            double t = (ph - Math.Max(b.Top, b.Bottom)) / ph, bo = (ph - Math.Min(b.Top, b.Bottom)) / ph;
            int x0 = Math.Clamp((int)Math.Floor(l * r.Width), 0, r.Width), x1 = Math.Clamp((int)Math.Ceiling(rr * r.Width), 0, r.Width);
            int y0 = Math.Clamp((int)Math.Floor(t * r.Height), 0, r.Height), y1 = Math.Clamp((int)Math.Ceiling(bo * r.Height), 0, r.Height);
            int w = x1 - x0, h = y1 - y0;
            if (w < 12 || h < 12) return null;
            var crop = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(r.Pixels, ((y0 + y) * r.Width + x0) * 4, crop, y * w * 4, w * 4);
            var (bytes, ext, media) = Encode(crop, w, h);
            return add(bytes, ext, media);
        }

        /// <summary>PNG for graphics with few colours, JPEG for photographs.</summary>
        internal static (byte[] Bytes, string Ext, string Media) Encode(byte[] bgra, int w, int h)
        {
            var colours = new HashSet<int>();
            int step = Math.Max(1, (w * h) / 4096);
            for (int p = 0; p < w * h && colours.Count <= 256; p += step)
            {
                int o = p * 4;
                colours.Add((bgra[o] >> 3) | ((bgra[o + 1] >> 3) << 5) | ((bgra[o + 2] >> 3) << 10));
            }
            if (colours.Count > 256 && w * h > 20000)
                return (PdfPageImageEncoder.EncodeJpeg(bgra, w, h).Bytes, "jpg", "image/jpeg");
            using var image = Image.LoadPixelData<Bgra32>(bgra, w, h);
            using var ms = new MemoryStream();
            image.SaveAsPng(ms, new PngEncoder());
            return (ms.ToArray(), "png", "image/png");
        }

        /// <summary>A page with no text becomes one full-page picture, unless it is blank.</summary>
        private static BookBlock? ScannedPage(PdfiumInterop.EpubSession session, int index, Page page, Func<byte[], string, string, string> add)
        {
            double scale = 150.0 / 72.0;
            int w = Math.Max(1, (int)Math.Round(page.Width * scale)), h = Math.Max(1, (int)Math.Round(page.Height * scale));
            double shrink = Math.Min(1.0, 3000.0 / Math.Max(w, h));
            w = Math.Max(1, (int)(w * shrink)); h = Math.Max(1, (int)(h * shrink));
            var px = session.RenderPage(index, w, h);
            if (px is null || IsBlank(px)) return null;
            var (bytes, ext, media) = Encode(px, w, h);
            string id = add(bytes, ext, media);
            return new BookBlock
            {
                Kind = BookBlockKind.Figure, ImageId = id, Alt = $"Page {index + 1}", PageIndex = index,
                PageHeight = page.Height, Top = page.Height, Bottom = 0, Left = 0, Right = page.Width,
            };
        }

        private static bool IsBlank(byte[] bgra)
        {
            for (int o = 0; o + 3 < bgra.Length; o += 4)
                if (bgra[o] < 245 || bgra[o + 1] < 245 || bgra[o + 2] < 245) return false;
            return true;
        }

        // ── Fonts ─────────────────────────────────────────────────────────────────────────────

        private static void DecideFonts(EpubExportOptions opt, Dictionary<string, List<byte[]>> bag,
                                        Dictionary<string, HashSet<int>> used, EpubExportResult result)
        {
            if (!opt.EmbedFonts) return;
            int n = 0;
            foreach (var (key, candidates) in bag.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (!used.TryGetValue(key, out var chars) || chars.Count == 0) continue;
                EpubFontDecision last = EpubFontDecision.DeniedNotExtractable;
                bool embedded = false;
                foreach (var data in candidates)
                {
                    var info = EpubFontFile.Inspect(data);
                    var d = EpubFontPolicy.Decide(info, chars);
                    last = d;
                    if (d != EpubFontDecision.Embed) continue;
                    bool otf = info.Kind == EpubFontKind.OpenTypeCff;
                    string family = $"tdpdffont{++n}";
                    result.Book.Fonts.Add(new EpubEmbeddedFont(key, family, data, otf ? "otf" : "ttf", otf ? "font/otf" : "font/ttf"));
                    result.EmbeddedFonts.Add(key);
                    embedded = true;
                    break;
                }
                if (!embedded) result.SkippedFonts.Add($"{key}: not embedded in the book because {EpubFontPolicy.Describe(last)}");
            }
        }

        // ── Metadata and chapters ─────────────────────────────────────────────────────────────

        private static void ReadMetadata(string path, PdfPigDoc pig, EpubBook book)
        {
            string? title = null, author = null;
            try { title = pig.Information.Title; author = pig.Information.Author; } catch { }
            book.Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title.Trim();
            book.Author = string.IsNullOrWhiteSpace(author) ? null : author.Trim();
            string? lang = null;
            try
            {
                using var d = PdfReader.Open(path, PdfDocumentOpenMode.InformationOnly);
                lang = d.Internals.Catalog.Elements.GetString("/Lang");
            }
            catch { }
            book.Language = !string.IsNullOrWhiteSpace(lang) && Regex.IsMatch(lang.Trim(), @"^[A-Za-z]{2,3}(-[A-Za-z0-9]{1,8})*$") ? lang.Trim() : "en";
        }

        private static List<OutlineItem> ReadOutline(PdfPigDoc pig)
        {
            var items = new List<OutlineItem>();
            try
            {
                if (!pig.TryGetBookmarks(out var bm)) return items;
                void Walk(BookmarkNode n, int level)
                {
                    if (level > 12) return;
                    if (n is DocumentBookmarkNode d && d.PageNumber >= 1)
                        items.Add(new OutlineItem(d.Title?.Trim() ?? "", level, d.PageNumber - 1));
                    foreach (var c in n.Children) Walk(c, level + 1);
                }
                foreach (var r in bm.Roots) Walk(r, 1);
            }
            catch { }
            return items.Where(i => i.PageIndex < pig.NumberOfPages).ToList();
        }

        private static void BuildChapters(PdfPigDoc pig, EpubExportOptions opt, List<int> selected, List<BookBlock> all, EpubBook book)
        {
            if (all.Count == 0) return;
            var outline = ReadOutline(pig);
            var firstPage = all[0].PageIndex;

            // Outline pages are snapped to the first exported page at or after them.
            var pagesWithBlocks = all.Select(b => b.PageIndex).Distinct().OrderBy(p => p).ToList();
            int Snap(int page) { foreach (int p in pagesWithBlocks) if (p >= page) return p; return -1; }
            var snapped = outline.Select(o => (Item: o, Page: Snap(o.PageIndex))).Where(x => x.Page >= 0).ToList();

            // Chapter starts: (page, title).
            List<(int Page, string Title)> starts = new();
            if (opt.Split == EpubChapterSplit.Bookmarks)
            {
                for (int level = 1; level <= 3 && starts.Count < 2; level++)
                    starts = snapped.Where(x => x.Item.Level == level).GroupBy(x => x.Page).OrderBy(g => g.Key)
                                    .Select(g => (g.Key, g.First().Item.Title)).ToList();
                if (starts.Count < 2) starts = new();
            }

            var chapters = new List<EpubChapter>();
            if (starts.Count >= 2)
            {
                var pageToChapter = new SortedList<int, int>();
                foreach (var (page, title) in starts)
                {
                    pageToChapter[page] = chapters.Count;
                    chapters.Add(new EpubChapter { Title = title, FirstPage = page });
                }
                EpubChapter? front = null;
                foreach (var b in all)
                {
                    int idx = -1;
                    foreach (var kv in pageToChapter) { if (kv.Key <= b.PageIndex) idx = kv.Value; else break; }
                    if (idx < 0) { front ??= new EpubChapter { Title = book.Title, FirstPage = firstPage }; front.Blocks.Add(b); }
                    else chapters[idx].Blocks.Add(b);
                }
                if (front != null) chapters.Insert(0, front);
            }
            else if (opt.Split != EpubChapterSplit.None)
            {
                int minLevel = all.Where(b => b.Kind == BookBlockKind.Heading && b.Level <= 2).Select(b => b.Level).DefaultIfEmpty(0).Min();
                int headings = minLevel == 0 ? 0 : all.Count(b => b.Kind == BookBlockKind.Heading && b.Level == minLevel);
                if (headings >= 2)
                {
                    EpubChapter? cur = null;
                    foreach (var b in all)
                    {
                        if (b.Kind == BookBlockKind.Heading && b.Level == minLevel)
                        {
                            cur = new EpubChapter { Title = b.Text, FirstPage = b.PageIndex };
                            chapters.Add(cur);
                        }
                        else if (cur == null) { cur = new EpubChapter { Title = book.Title, FirstPage = b.PageIndex }; chapters.Add(cur); }
                        cur.Blocks.Add(b);
                    }
                }
                else if (pagesWithBlocks.Count > opt.PagesPerChapter)
                {
                    int per = Math.Max(1, opt.PagesPerChapter);
                    foreach (var grp in all.GroupBy(b => pagesWithBlocks.IndexOf(b.PageIndex) / per))
                        chapters.Add(new EpubChapter { Title = $"Pages {grp.First().PageIndex + 1} to {grp.Last().PageIndex + 1}", FirstPage = grp.First().PageIndex, Blocks = grp.ToList() });
                }
            }
            if (chapters.Count == 0) chapters.Add(new EpubChapter { Title = book.Title, FirstPage = firstPage, Blocks = all });

            for (int i = 0; i < chapters.Count; i++)
                if (string.IsNullOrWhiteSpace(chapters[i].Title))
                    chapters[i].Title = chapters[i].Blocks.FirstOrDefault(b => b.Kind == BookBlockKind.Heading)?.Text is { Length: > 0 } t ? t : $"Part {i + 1}";
            book.Chapters = chapters;

            // Table of contents: the PDF's outline when it has one, otherwise the headings.
            var nav = new List<EpubNavEntry>();
            if (snapped.Count > 0)
            {
                foreach (var (item, page) in snapped)
                {
                    int ci = chapters.FindIndex(c => c.Blocks.Any(b => b.PageIndex == page));
                    if (ci < 0) continue;
                    bool atTop = chapters[ci].Blocks[0].PageIndex >= page;
                    string title = string.IsNullOrWhiteSpace(item.Title) ? $"Page {page + 1}" : item.Title;
                    nav.Add(new EpubNavEntry(title, item.Level, ci, atTop ? null : "pg" + (page + 1)));
                }
            }
            else
            {
                for (int ci = 0; ci < chapters.Count; ci++)
                {
                    int n = 0;
                    foreach (var b in chapters[ci].Blocks.Where(b => b.Kind == BookBlockKind.Heading))
                    {
                        n++;
                        if (b.Level <= 3 && b.Text.Length > 0) nav.Add(new EpubNavEntry(b.Text, b.Level, ci, $"h{ci + 1}-{n}"));
                    }
                }
            }
            book.Nav = nav;
        }
    }
}
