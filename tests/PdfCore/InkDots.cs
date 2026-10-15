using PdfSharpCore.Drawing;
using TDPdf.Services;

/// <summary>
/// Pins pen dots: <see cref="InkDot"/>. A click with the Draw tool used to be thrown away; it is now
/// kept as a round dot one stroke-width across (upstream KillerPDF v1.8.72).
/// </summary>
/// <remarks>
/// Two things can go silently wrong, and both are checked here.
///
///   * <b>What counts as a dot.</b> One point, or a handful that never left an
///     <see cref="InkDot.MaxExtent"/> square, is a dot; anything longer is a real segment and must
///     keep the ordinary stroked path. An empty stroke is neither.
///   * <b>What the saved PDF actually shows.</b> The whole reason a dot is a filled circle rather than
///     a zero-length round-capped stroke is that viewers disagree about degenerate caps. So the burn
///     is judged by what PDFium renders, not by what the content stream says: ink at the centre and
///     out along both axes to just inside the radius, paper just outside it — and paper on the
///     diagonal where a SQUARE of the same size would still be inked, so a filled rectangle (the
///     easy wrong answer) cannot pass.
/// </remarks>
internal static class InkDots
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("── pen dots (upstream v1.8.72) ───────────────────────────────");

        // ── what counts as a dot ────────────────────────────────────────────────────────────
        bool one = InkDot.TryGetDot(new[] { (40.0, 60.0) }, out double cx1, out double cy1);
        Check("a single point is a dot, centred on that point",
              one && cx1 == 40 && cy1 == 60, $"{one} ({cx1}, {cy1})");

        bool jitter = InkDot.TryGetDot(new[] { (40.0, 60.0), (40.25, 60.0), (40.5, 60.5) }, out double cxj, out double cyj);
        Check("a click that jittered inside MaxExtent is a dot, centred on its box",
              jitter && Math.Abs(cxj - 40.25) < 1e-9 && Math.Abs(cyj - 60.25) < 1e-9, $"{jitter} ({cxj}, {cyj})");

        Check("a stroke longer than MaxExtent is NOT a dot",
              !InkDot.TryGetDot(new[] { (40.0, 60.0), (45.0, 60.0) }, out _, out _), "");
        Check("a stroke that is long in Y only is NOT a dot",
              !InkDot.TryGetDot(new[] { (40.0, 60.0), (40.0, 63.0) }, out _, out _), "");
        Check("an empty stroke is NOT a dot",
              !InkDot.TryGetDot(Array.Empty<(double, double)>(), out _, out _), "");

        // ── what the saved PDF shows ────────────────────────────────────────────────────────
        const double c = 100, d = 20, r = d / 2;
        string path = Path.Combine(tmp, "inkdot.pdf");
        {
            var doc = new PdfSharpCore.Pdf.PdfDocument();
            var page = doc.AddPage();
            page.Width = 200;
            page.Height = 200;
            using (var g = XGraphics.FromPdfPage(page))
                InkDot.Burn(g, XColors.Black, c, c, d);
            doc.Save(path);
        }

        var (bgra, w, _) = Geometry.RenderFirstPage(path);   // 1 px per point, top-left origin
        bool Inked(double x, double y)
        {
            int i = ((int)y * w + (int)x) * 4;
            return bgra[i] < 128 && bgra[i + 1] < 128 && bgra[i + 2] < 128;
        }

        Check("the dot is inked at its centre", Inked(c, c), "");
        Check("the dot is inked out to just inside its radius, on both axes and both sides",
              Inked(c + r - 2, c) && Inked(c - r + 1, c) && Inked(c, c + r - 2) && Inked(c, c - r + 1), "");
        Check("the dot's diameter is the stroke width: paper just outside the radius",
              !Inked(c + r + 2, c) && !Inked(c - r - 3, c) && !Inked(c, c + r + 2) && !Inked(c, c - r - 3), "");
        // 0.85r along each axis is 1.2r from the centre: outside a circle, inside a square.
        Check("the dot is ROUND: paper on the diagonal where a square would be inked",
              !Inked(c + 0.85 * r, c + 0.85 * r) && !Inked(c - 0.85 * r - 1, c - 0.85 * r - 1), "");
    }
}
