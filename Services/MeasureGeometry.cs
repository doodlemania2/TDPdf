using System;
using PdfSharpCore.Pdf;

namespace TDPdf.Services
{
    /// <summary>
    /// The arithmetic behind the Measure tool: how far apart two points the user clicked on the
    /// page image really are, and how that distance reads in the three units a PDF is measured in.
    /// </summary>
    /// <remarks>
    /// It lives out here rather than in MainWindow for the same reason the redaction mapping does:
    /// it is pure geometry, it is the part where being wrong is silent — a ruler that is 12% out on
    /// a rotated page still draws a perfectly convincing line and prints a perfectly plausible
    /// number — and out here the tests can reach it. See tests/PdfCore/Measure.cs.
    ///
    /// Nothing here knows about WPF. That is deliberate and load-bearing: tests/PdfCore compiles
    /// the real shipping Services files and runs on macOS and Linux, so a System.Windows.Point in
    /// this signature would take the whole file out of the harness.
    ///
    /// <b>Every canvas-to-PDF step goes through <see cref="PdfPageGeometry"/>.</b> There is no
    /// second mapping here, not even a "simpler" one for the single-point case, because that is
    /// exactly how redaction briefly grew a second rotation table. Routing through the one home
    /// for the mapping is also how this inherits, for free, everything it already handles: page
    /// /Rotate (including a quarter turn inherited from a parent /Pages node), a CropBox that
    /// differs from the MediaBox or does not start at the origin, and the fact that the rendered
    /// image measures down from the top-left while PDF user space measures up from the
    /// bottom-left.
    /// </remarks>
    internal static class MeasureGeometry
    {
        /// <summary>PDF user space is 1/72 inch per unit, by definition (PDF 32000-1 8.3.2.3).</summary>
        internal const double PointsPerInch = 72.0;

        internal const double MillimetresPerInch = 25.4;

        /// <summary>
        /// Where a point on the rendered page image lands in PDF user space.
        /// </summary>
        /// <param name="x">Distance from the LEFT of the page image, in canvas units.</param>
        /// <param name="y">Distance DOWN from the top of the page image, in canvas units.</param>
        /// <param name="canvasW">Width of the page image the point was taken on.</param>
        /// <param name="canvasH">Height of that same image.</param>
        /// <remarks>
        /// A point is a rectangle of no size, so this is <see cref="PdfPageGeometry.CanvasRectToPdf"/>
        /// with a zero width and height: both mapped corners come out identical, and Left/Bottom is
        /// then the point itself. Written this way on purpose rather than as a hand-rolled
        /// point-mapping — the four-way rotation table only has to exist in one place, and a future
        /// fix to it reaches the ruler without anyone remembering the ruler exists.
        /// </remarks>
        internal static (double X, double Y) CanvasPointToPdf(
            PdfPage page, double x, double y, double canvasW, double canvasH)
        {
            var r = PdfPageGeometry.CanvasRectToPdf(page, x, y, 0, 0, canvasW, canvasH);
            return (r.Left, r.Bottom);
        }

        /// <summary>
        /// The straight-line distance between two points on the rendered page image, in PDF points.
        /// </summary>
        /// <remarks>
        /// Both ends are mapped into PDF user space FIRST and the distance taken there, never taken
        /// on the canvas and scaled afterwards. Two reasons, and the second is the one that bites:
        ///
        ///   * Canvas units are whatever the current render happened to produce — they move with
        ///     the zoom, with the monitor's DPI scaling and with PdfDocumentService.RenderBoxDip.
        ///     PDF points do not move with any of them, so the same drag reports the same number on
        ///     a 4K laptop at 400% as on a 1080p monitor at 50%.
        ///   * On a quarter-turned page the canvas axes are swapped relative to the PDF's, so a
        ///     distance computed on the canvas and then scaled by a single width ratio is wrong by
        ///     the page's aspect ratio — about 30% on US Letter, and wrong in a direction that
        ///     still looks like a believable measurement.
        ///
        /// Returns 0 rather than NaN for a degenerate frame; the caller has nothing to show yet.
        /// </remarks>
        internal static double DistancePoints(
            PdfPage page, double ax, double ay, double bx, double by, double canvasW, double canvasH)
        {
            if (!(canvasW > 0) || !(canvasH > 0)) return 0;
            var a = CanvasPointToPdf(page, ax, ay, canvasW, canvasH);
            var b = CanvasPointToPdf(page, bx, by, canvasW, canvasH);
            double dx = b.X - a.X, dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Inches, from a distance in PDF points.</summary>
        internal static double ToInches(double points) => points / PointsPerInch;

        /// <summary>Millimetres, from a distance in PDF points.</summary>
        internal static double ToMillimetres(double points) => points / PointsPerInch * MillimetresPerInch;

        /// <summary>
        /// The ruler's caption: all three units at once, e.g. <c>2.45 in · 62.2 mm · 176.4 pt</c>.
        /// </summary>
        /// <remarks>
        /// <b>Why all three rather than one unit with a switch.</b> The footer page-size chip
        /// (MainWindow's FormatPageSize) cycles px → in → mm → pt on click, and that is right for
        /// it: the chip is always on screen, so a fourth unit costs nothing to reach and showing
        /// four numbers at once would be noise. The ruler is the opposite — it exists only for the
        /// few seconds a drag is being read, and a unit switch there is a mode the user has to
        /// discover, get wrong once, and re-drag. Somebody measuring a margin usually wants
        /// millimetres, somebody checking a US form usually wants inches, and somebody chasing a
        /// layout bug wants points; printing all three answers all of them with no mode at all.
        ///
        /// <b>Formatting.</b> The conversions, the unit suffixes and the trailing-zero suppression
        /// are the chip's, so the same distance reads the same way in both places. The precision is
        /// not: the chip rounds mm and pt to whole units because a PAGE is hundreds of them, while
        /// a measurement is routinely a handful — "0 mm" for a 0.4 mm drag would be a defect, not a
        /// rounding. So inches keep the chip's two decimals and mm/pt each carry one, which lands
        /// all three at roughly the same real precision (0.01 in ≈ 0.25 mm ≈ 0.7 pt).
        /// </remarks>
        internal static string Format(double points) =>
            $"{ToInches(points):0.##} in · {ToMillimetres(points):0.#} mm · {points:0.#} pt";
    }
}
