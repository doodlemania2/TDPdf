using System.Windows;
using TDPdf.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf
{
    internal sealed class PdfContentEditor
    {
        private readonly Dictionary<string, Dictionary<int, ParsedPageContent>> _pageCache = new();

        public void ClearCache() => _pageCache.Clear();

        public TextRunHit? FindTextRunAt(string pdfPath, int pageIndex, Point canvasPoint, int renderWidth, int renderHeight)
        {
            var parsed = GetParsedPage(pdfPath, pageIndex, renderWidth, renderHeight);
            if (parsed.TextRuns.Count == 0) return null;

            var direct = parsed.TextRuns
                .Where(r => r.CanvasBounds.Contains(canvasPoint))
                .OrderBy(r => r.CanvasBounds.Width * r.CanvasBounds.Height)
                .FirstOrDefault();
            if (direct is not null) return direct;

            return parsed.TextRuns
                .Where(r => canvasPoint.Y >= r.CanvasBounds.Top - 3 && canvasPoint.Y <= r.CanvasBounds.Bottom + 3)
                .OrderBy(r => Math.Abs((r.CanvasBounds.Left + r.CanvasBounds.Right) / 2 - canvasPoint.X))
                .FirstOrDefault();
        }

        public ImageHit? FindImageAt(string pdfPath, int pageIndex, Point canvasPoint, int renderWidth, int renderHeight)
        {
            var parsed = GetParsedPage(pdfPath, pageIndex, renderWidth, renderHeight);
            return parsed.Images
                .Where(i => i.CanvasBounds.Contains(canvasPoint))
                .OrderBy(i => i.CanvasBounds.Width * i.CanvasBounds.Height)
                .FirstOrDefault();
        }

        private ParsedPageContent GetParsedPage(string pdfPath, int pageIndex, int renderWidth, int renderHeight)
        {
            string cacheKey = $"{pdfPath}|{renderWidth}x{renderHeight}";
            if (_pageCache.TryGetValue(cacheKey, out var pages) &&
                pages.TryGetValue(pageIndex, out var cached))
                return cached;

            pages ??= new Dictionary<int, ParsedPageContent>();
            _pageCache[cacheKey] = pages;

            using var pigDoc = PdfPigDoc.Open(pdfPath);
            if (pageIndex < 0 || pageIndex >= pigDoc.NumberOfPages)
            {
                var empty = new ParsedPageContent();
                pages[pageIndex] = empty;
                return empty;
            }

            var page = pigDoc.GetPage(pageIndex + 1);
            double sx = renderWidth / page.Width;
            double sy = renderHeight / page.Height;

            var textRuns = page.GetWords()
                .GroupBy(w => Math.Round((renderHeight - (w.BoundingBox.Top * sy)) / 4.0))
                // #185: a Y bucket spans the whole page, so on a two-column layout one "line" held
                // both columns — double-clicking to edit picked up a run whose text was the left
                // and right columns space-joined and whose box straddled the gutter. Splitting each
                // bucket at column-gutter-sized gaps (the same helper the selection geometry uses)
                // gives one editable run per column, and per cell on a table row. Ordinary
                // single-column prose has no such gap and comes back as a single segment, i.e.
                // exactly the runs this produced before. SplitAtColumnGaps sorts left-to-right.
                .SelectMany(g => TextRunService.SplitAtColumnGaps(g.ToList()))
                .Select(words =>
                {
                    double left = words.Min(w => w.BoundingBox.Left) * sx;
                    double top = renderHeight - (words.Max(w => w.BoundingBox.Top) * sy);
                    double right = words.Max(w => w.BoundingBox.Right) * sx;
                    double bottom = renderHeight - (words.Min(w => w.BoundingBox.Bottom) * sy);
                    string text = string.Join(" ", words.Select(w => w.Text));
                    // Line-height estimate, used only when the content stream yields no usable size.
                    double fontSize = Math.Max((bottom - top) * 0.75, 10);
                    // #166: never seed the family from Word.FontName — a Word joins its letters' font
                    // names into one string ("Helvetica Helvetica Helvetica…"), which would reach
                    // FontFamily verbatim on a line that has words but no letters.
                    string fontName = PdfFontStyle.DefaultFamily;
                    bool fontBold = false, fontItalic = false;

                    var firstLetter = words.SelectMany(w => w.Letters).FirstOrDefault();
                    if (firstLetter is not null)
                    {
                        // #163/#165: PointSize is the glyph size in points. FontSize is the raw
                        // "/F1 <n> Tf" operand, which equals the visual size only when the text matrix
                        // carries no scale — a generator that emits "/F1 1 Tf" and scales through Tm
                        // reports 1 and collapsed the box onto its lower clamp. Fall back to FontSize,
                        // then keep the line-height estimate above, because PointSize can be 0 on
                        // fonts with no usable metrics (some Type3).
                        double pdfPoints = firstLetter.PointSize > 0 ? firstLetter.PointSize : firstLetter.FontSize;
                        if (pdfPoints > 0)
                            fontSize = Math.Max(pdfPoints * sy, 10);

                        var detected = PdfFontStyle.FromPdfName(firstLetter.FontName);
                        fontName = detected.Family;
                        fontBold = detected.Bold;
                        fontItalic = detected.Italic;
                    }

                    return new TextRunHit
                    {
                        Text = text,
                        CanvasBounds = new Rect(left, top, Math.Max(right - left, 1), Math.Max(bottom - top, 1)),
                        Position = new Point(left, top),
                        FontSize = fontSize,
                        FontName = fontName,
                        Bold = fontBold,
                        Italic = fontItalic
                    };
                })
                .Where(r => !string.IsNullOrWhiteSpace(r.Text))
                .ToList();

            var images = page.GetImages()
                .Select(img =>
                {
                    var box = img.BoundingBox;
                    double left = box.Left * sx;
                    double top = renderHeight - (box.Top * sy);
                    double width = (box.Right - box.Left) * sx;
                    double height = (box.Top - box.Bottom) * sy;
                    return new ImageHit { CanvasBounds = new Rect(left, top, Math.Max(width, 1), Math.Max(height, 1)) };
                })
                .Where(i => i.CanvasBounds.Width > 1 && i.CanvasBounds.Height > 1)
                .ToList();

            var parsed = new ParsedPageContent { TextRuns = textRuns, Images = images };
            pages[pageIndex] = parsed;
            return parsed;
        }

        private sealed class ParsedPageContent
        {
            public List<TextRunHit> TextRuns { get; set; } = new();
            public List<ImageHit> Images { get; set; } = new();
        }
    }

    internal sealed class TextRunHit
    {
        public string Text { get; set; } = "";
        public Rect CanvasBounds { get; set; }
        public Point Position { get; set; }
        public double FontSize { get; set; } = 14;
        public string FontName { get; set; } = PdfFontStyle.DefaultFamily;
        /// <summary>Face styling encoded in the source font's PostScript name (#182).</summary>
        public bool Bold { get; set; }
        public bool Italic { get; set; }
    }

    internal sealed class ImageHit
    {
        public Rect CanvasBounds { get; set; }
    }
}
