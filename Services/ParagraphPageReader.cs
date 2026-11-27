using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace TDPdf.Services
{
    /// <summary>The lines of one page, in the displayed frame, with the page's displayed size.</summary>
    internal sealed class ParagraphPage
    {
        public double Width { get; init; }
        public double Height { get; init; }
        public IReadOnlyList<ParagraphLine> Lines { get; init; } = Array.Empty<ParagraphLine>();
    }

    /// <summary>
    /// Reads a page's text with PdfPig and turns it into <see cref="ParagraphLine"/>s.
    /// </summary>
    /// <remarks>
    /// PdfPig reports letters in the page AS DISPLAYED (see <see cref="PdfPageGeometry.TextFrameToPdf(PageBox, int, PdfiumInterop.PdfRect)"/>),
    /// which is the frame the canvas draws, scaled to points. Everything the finder does therefore
    /// happens in that frame and is converted to PDF user space only at the engine boundary, with
    /// <see cref="PdfPageGeometry"/>, exactly as the line editor does.
    ///
    /// Lines are bands of words that overlap vertically, split again at column gutters
    /// (<see cref="TextRunService.SplitAtColumnGaps"/>) so that two columns on one band are two lines.
    /// </remarks>
    internal static class ParagraphPageReader
    {
        internal static ParagraphPage? Read(string path, int pageIndex)
        {
            try
            {
                using var doc = PdfDocument.Open(path);
                if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) return null;
                return Build(doc.GetPage(pageIndex + 1));
            }
            catch
            {
                return null;
            }
        }

        internal static ParagraphPage Build(Page page)
        {
            var words = page.GetWords().Where(w => w.Letters.Count > 0).ToList();

            // Bands by vertical overlap, as TextRunService does.
            var bands = new List<(List<Word> Words, double Top, double Bottom)>();
            foreach (var w in words)
            {
                var (_, wb, _, wt) = PdfBoxExtent.Of(w.BoundingBox);
                int found = -1;
                for (int i = 0; i < bands.Count; i++)
                {
                    var (_, bt, bb) = bands[i];
                    double overlap = Math.Min(bt, wt) - Math.Max(bb, wb);
                    double minH = Math.Min(bt - bb, wt - wb);
                    if (minH > 0 && overlap >= minH * 0.5) { found = i; break; }
                }
                if (found < 0) bands.Add((new List<Word> { w }, wt, wb));
                else
                {
                    var e = bands[found];
                    e.Words.Add(w);
                    bands[found] = (e.Words, Math.Max(e.Top, wt), Math.Min(e.Bottom, wb));
                }
            }

            var lines = new List<ParagraphLine>();
            foreach (var band in bands.OrderByDescending(b => b.Top))
                foreach (var segment in TextRunService.SplitAtColumnGaps(band.Words))
                {
                    var line = MakeLine(segment);
                    if (line is not null) lines.Add(line);
                }
            return new ParagraphPage { Width = page.Width, Height = page.Height, Lines = lines };
        }

        private static ParagraphLine? MakeLine(List<Word> words)
        {
            if (words.Count == 0) return null;
            bool rtl = TextRunService.IsRightToLeftText(words.Select(w => w.Text));
            var ordered = words.OrderBy(w => PdfBoxExtent.LeftOf(w.BoundingBox)).ToList();

            var text = new System.Text.StringBuilder();
            var chars = new List<ParagraphChar>();
            var letters = new List<Letter>();
            double prevRight = double.NaN;

            foreach (var w in ordered)
            {
                var ls = w.Letters.OrderBy(l => PdfBoxExtent.LeftOf(l.BoundingBox)).ToList();
                if (ls.Count == 0) continue;
                double wordLeft = PdfBoxExtent.LeftOf(ls[0].BoundingBox);
                if (text.Length > 0)
                {
                    text.Append(' ');
                    chars.Add(new ParagraphChar(prevRight, Math.Max(wordLeft, prevRight)));
                }
                foreach (var l in ls)
                {
                    var (gl, _, gr, _) = PdfBoxExtent.Of(l.BoundingBox);
                    string v = l.Value ?? "";
                    if (v.Length == 0) continue;
                    // A ligature is one glyph with several characters: share its width between them.
                    double step = (gr - gl) / v.Length;
                    for (int k = 0; k < v.Length; k++)
                        chars.Add(new ParagraphChar(gl + step * k, gl + step * (k + 1)));
                    text.Append(v);
                    prevRight = gr;
                    letters.Add(l);
                }
            }
            if (letters.Count == 0 || text.Length == 0) return null;

            double left = chars.Min(c => c.Left), right = chars.Max(c => c.Right);
            double top = double.MinValue, bottom = double.MaxValue;
            foreach (var l in letters)
            {
                var (_, gb, _, gt) = PdfBoxExtent.Of(l.BoundingBox);
                top = Math.Max(top, gt);
                bottom = Math.Min(bottom, gb);
            }

            // Baseline: the middle value of the letters' own baselines, so a superscript or a
            // drop cap does not move the line.
            var baselines = letters.Select(l => l.StartBaseLine.Y).OrderBy(y => y).ToList();
            double baseline = baselines[baselines.Count / 2];

            var sizes = letters.Select(l => Math.Round(l.PointSize > 0 ? l.PointSize : l.FontSize, 1)).ToList();
            double size = sizes.GroupBy(s => s).OrderByDescending(g => g.Count()).First().Key;
            var fonts = letters.Select(l => CleanFontName(l.FontName)).ToList();
            string font = fonts.GroupBy(f => f).OrderByDescending(g => g.Count()).First().Key;
            bool mixed = fonts.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
                         || sizes.Max() - sizes.Min() > Math.Max(0.12 * size, 0.6);

            int color = 0;
            try
            {
                var (r, g, b) = letters[0].Color.ToRGBValues();
                color = ((int)Math.Round(r * 255) << 16) | ((int)Math.Round(g * 255) << 8) | (int)Math.Round(b * 255);
            }
            catch { /* black */ }

            return new ParagraphLine
            {
                Text = text.ToString(),
                Left = left, Right = right, Top = top, Bottom = bottom, Baseline = baseline,
                FontSize = size, FontName = font, MixedStyles = mixed, RightToLeft = rtl,
                Color = color, Chars = chars,
            };
        }

        /// <summary>"ABCDEF+Helvetica-Bold" is "Helvetica-Bold": the subset tag says nothing about the face.</summary>
        internal static string CleanFontName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int plus = name.IndexOf('+');
            return plus == 6 ? name[(plus + 1)..] : name;
        }

        /// <summary>The line (by index) a point of the displayed page falls on, or the nearest within a line height.</summary>
        internal static int LineAt(IReadOnlyList<ParagraphLine> lines, double x, double y)
        {
            int best = -1;
            double bestScore = double.MaxValue;
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                double slopY = Math.Max(l.FontSize * 0.25, 2);
                if (y > l.Top + slopY || y < l.Bottom - slopY) continue;
                double dx = x < l.Left ? l.Left - x : x > l.Right ? x - l.Right : 0;
                if (dx > l.FontSize * 2) continue;
                double score = dx + Math.Abs(y - (l.Top + l.Bottom) / 2) * 0.1;
                if (score < bestScore) { bestScore = score; best = i; }
            }
            return best;
        }
    }
}
