using System.Globalization;
using System.Text;

namespace TDPdf.Services
{
    /// <summary>
    /// The pure half of "Rasterize a region" (#233): turns a dragged rectangle into the pixels to
    /// render and the matrix that puts them back exactly where they came from, and decides which
    /// glyphs of a text run are inside the box.
    /// </summary>
    /// <remarks>
    /// Rasterizing a region replaces a part of the page with a picture of itself and removes the
    /// text and graphics underneath, so the words can no longer be searched, selected or copied
    /// from under the picture. It is NOT redaction: redaction destroys content and proves it is
    /// gone; this keeps what the box shows, as pixels.
    ///
    /// Everything geometric lives here so it can be tested without a window: the box in PDF user
    /// space on a cropped and rotated page (via <see cref="PdfPageGeometry"/>, the one home of that
    /// table), the pixel grid the box snaps to, the placement matrix for each /Rotate, and the
    /// glyph rule.
    /// </remarks>
    internal static class RegionRaster
    {
        internal const int DefaultDpi = 300;
        internal const int MinDpi = 72;
        internal const int MaxDpi = 1200;

        /// <summary>Largest picture we will render in one go (about 240 MB of BGRA).</summary>
        internal const long MaxPixels = 60_000_000;

        /// <summary>The resolutions the dialog offers.</summary>
        internal static readonly int[] DpiChoices = { 150, 300, 600 };

        // ── Glyph rule ────────────────────────────────────────────────────────────────────────

        /// <summary>A glyph's box in PDF user space.</summary>
        internal readonly record struct GlyphBox(double Left, double Bottom, double Right, double Top);

        /// <summary>
        /// Share of a glyph's height, at the top and at the bottom, that is allowed to poke out of
        /// the box. The box PDFium reports for a character is the font's line box (ascent to
        /// descent), which is taller than the ink; a box dragged tight around a word would otherwise
        /// never contain a single letter.
        /// </summary>
        internal const double VerticalSlack = 0.15;

        /// <summary>Rounding noise in coordinates read back from the file.</summary>
        internal const double Epsilon = 0.01;

        /// <summary>
        /// True when the glyph lies fully inside <paramref name="region"/>: horizontally all of it,
        /// vertically all of it bar <see cref="VerticalSlack"/> top and bottom. A letter that crosses
        /// the left or right edge is NOT inside, so it stays on the page.
        /// </summary>
        internal static bool GlyphInside(GlyphBox g, PdfiumInterop.PdfRect region)
        {
            double l = Math.Min(g.Left, g.Right), r = Math.Max(g.Left, g.Right);
            double b = Math.Min(g.Bottom, g.Top), t = Math.Max(g.Bottom, g.Top);
            if (!(r > l) || !(t > b)) return false;
            double slack = (t - b) * VerticalSlack;
            return l >= region.Left - Epsilon && r <= region.Right + Epsilon
                && b + slack >= region.Bottom - Epsilon && t - slack <= region.Top + Epsilon;
        }

        /// <summary>For each glyph, whether it is inside <paramref name="region"/>.</summary>
        internal static bool[] InsideFlags(IReadOnlyList<GlyphBox> glyphs, PdfiumInterop.PdfRect region)
        {
            var flags = new bool[glyphs.Count];
            for (int i = 0; i < flags.Length; i++) flags[i] = GlyphInside(glyphs[i], region);
            return flags;
        }

        internal enum RunAction
        {
            /// <summary>No glyph is inside: leave the run alone.</summary>
            Keep,
            /// <summary>Every glyph is inside: remove the whole run.</summary>
            RemoveAll,
            /// <summary>Some are: the kept glyphs are written back, the others dropped.</summary>
            Rewrite,
        }

        internal static RunAction Classify(IReadOnlyList<bool> inside)
        {
            int n = 0;
            for (int i = 0; i < inside.Count; i++) if (inside[i]) n++;
            if (n == 0) return RunAction.Keep;
            return n == inside.Count ? RunAction.RemoveAll : RunAction.Rewrite;
        }

        /// <summary>A stretch of consecutive glyphs: <c>Start</c> and how many.</summary>
        internal readonly record struct Span(int Start, int Length);

        /// <summary>The maximal stretches of glyphs that are NOT inside, in order.</summary>
        internal static List<Span> KeptSpans(IReadOnlyList<bool> inside)
        {
            var spans = new List<Span>();
            int start = -1;
            for (int i = 0; i <= inside.Count; i++)
            {
                bool kept = i < inside.Count && !inside[i];
                if (kept && start < 0) start = i;
                else if (!kept && start >= 0) { spans.Add(new Span(start, i - start)); start = -1; }
            }
            return spans;
        }

        // ── Pixels ────────────────────────────────────────────────────────────────────────────

        /// <summary>Where the box falls on the page raster at one resolution.</summary>
        /// <param name="SnappedRect">The box grown to whole pixels, in PDF user space: the rectangle the picture is placed on.</param>
        /// <param name="FullWidth">Width in pixels of the whole page, as displayed, at this resolution.</param>
        /// <param name="FullHeight">Height in pixels of the whole page, as displayed.</param>
        /// <param name="X">Left of the region in the displayed page's pixels.</param>
        /// <param name="Y">Top of the region, measured down.</param>
        /// <param name="Width">Region width in pixels.</param>
        /// <param name="Height">Region height in pixels.</param>
        internal sealed record RegionPlan(
            PdfiumInterop.PdfRect SnappedRect, int FullWidth, int FullHeight,
            int X, int Y, int Width, int Height, int Dpi)
        {
            internal long Pixels => (long)Width * Height;
        }

        /// <summary>
        /// The pixel region to render for <paramref name="userRect"/> on a page whose visible box is
        /// <paramref name="box"/> and whose /Rotate is <paramref name="rotation"/>, or null when
        /// nothing of the box is on the page. The box is rounded OUTWARDS to whole pixels (so no
        /// sliver of the original shows at an edge) and clamped to the page.
        /// </summary>
        internal static RegionPlan? Plan(
            PdfPageGeometry.PageBox box, int rotation, PdfiumInterop.PdfRect userRect, int dpi)
        {
            if (dpi < 1 || !(box.Width > 0) || !(box.Height > 0)) return null;
            if (!double.IsFinite(userRect.Left + userRect.Right + userRect.Bottom + userRect.Top)) return null;

            bool turned = rotation is 90 or 270;
            double visualW = turned ? box.Height : box.Width;
            double visualH = turned ? box.Width : box.Height;
            int fullW = Math.Max(1, (int)Math.Round(visualW * dpi / 72.0));
            int fullH = Math.Max(1, (int)Math.Round(visualH * dpi / 72.0));

            var (cx, cy, cw, ch) = PdfPageGeometry.RectToCanvas(
                box, rotation, fullW, fullH,
                userRect.Left, userRect.Bottom, userRect.Right, userRect.Top);

            int x0 = (int)Math.Floor(Math.Clamp(cx, 0, fullW));
            int y0 = (int)Math.Floor(Math.Clamp(cy, 0, fullH));
            int x1 = (int)Math.Ceiling(Math.Clamp(cx + cw, 0, fullW));
            int y1 = (int)Math.Ceiling(Math.Clamp(cy + ch, 0, fullH));
            if (x1 - x0 < 1 || y1 - y0 < 1) return null;

            var snapped = PdfPageGeometry.CanvasRectToPdf(box, rotation, x0, y0, x1 - x0, y1 - y0, fullW, fullH);
            return new RegionPlan(snapped, fullW, fullH, x0, y0, x1 - x0, y1 - y0, dpi);
        }

        // ── Placement ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The <c>cm</c> matrix (a b c d e f) that maps the image's unit square onto
        /// <paramref name="rect"/> in PDF user space so that, once the viewer applies /Rotate, the
        /// picture is upright and exactly over the box. The image is the box as DISPLAYED, so on a
        /// turned page its axes run along the user-space ones swapped.
        /// </summary>
        internal static (double A, double B, double C, double D, double E, double F) PlacementMatrix(
            PdfiumInterop.PdfRect rect, int rotation)
        {
            double w = rect.Right - rect.Left, h = rect.Top - rect.Bottom;
            return rotation switch
            {
                90  => (0, h, -w, 0, rect.Right, rect.Bottom),
                180 => (-w, 0, 0, -h, rect.Right, rect.Top),
                270 => (0, -h, w, 0, rect.Left, rect.Top),
                _   => (w, 0, 0, h, rect.Left, rect.Bottom),
            };
        }

        /// <summary>The matrix as the operator line <c>a b c d e f cm</c>, culture-independent.</summary>
        internal static string CmOperator((double A, double B, double C, double D, double E, double F) m)
        {
            static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
            return $"{N(m.A)} {N(m.B)} {N(m.C)} {N(m.D)} {N(m.E)} {N(m.F)} cm";
        }

        // ── Alternate text ────────────────────────────────────────────────────────────────────

        /// <summary>Longest alternate description we write.</summary>
        internal const int MaxAltChars = 500;

        /// <summary>
        /// The /Alt text for the picture that replaced <paramref name="removedWords"/> on a tagged
        /// document: what a screen reader says instead of the text that used to be there.
        /// </summary>
        internal static string BuildAlt(string removedWords)
        {
            var sb = new StringBuilder();
            bool lastSpace = true;
            foreach (char c in removedWords ?? "")
            {
                if (char.IsControl(c) && !char.IsWhiteSpace(c)) continue;
                if (char.IsWhiteSpace(c))
                {
                    if (!lastSpace) sb.Append(' ');
                    lastSpace = true;
                }
                else { sb.Append(c); lastSpace = false; }
            }
            string words = sb.ToString().Trim();
            if (words.Length == 0) return "Picture of part of the page.";
            string prefix = "Picture of text: ";
            int room = MaxAltChars - prefix.Length;
            if (words.Length > room)
            {
                int cut = words.LastIndexOf(' ', Math.Max(0, room - 1));
                words = words.Substring(0, cut > room / 2 ? cut : room).TrimEnd() + "...";
            }
            return prefix + words;
        }
    }
}
