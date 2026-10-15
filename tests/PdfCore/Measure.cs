using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using TDPdf.Services;

/// <summary>
/// Pins the Measure tool's arithmetic: <see cref="MeasureGeometry"/>.
/// </summary>
/// <remarks>
/// A ruler is the kind of feature that cannot fail loudly. Wrong by 30% on a quarter-turned page,
/// or by the crop inset on a cropped one, and it still draws a convincing line and still prints a
/// plausible number — the user only finds out when the thing they cut to that measurement does not
/// fit. So the properties worth asserting are the ones a wrong mapping would break:
///
///   * a span across the page reads back as the page's own dimensions, on all four quarter turns,
///     with the turn set on the page AND with it inherited from the parent /Pages node;
///   * the same span reads the same number whatever resolution the page was rendered at, which is
///     what "the same at any zoom, on any monitor" reduces to once zoom is out of the canvas
///     coordinates;
///   * a CropBox that differs from the MediaBox measures the CROP, because that is the box PDFium
///     rasterised and therefore the box the user dragged across;
///   * the unit conversions round-trip.
///
/// Geometry.cs already pins CanvasRectToPdf itself against PDFium's own rendering. This does not
/// repeat that: it checks that the ruler is actually wired to it and that the distance falls out
/// correctly, which is the part that is this file's own.
/// </remarks>
internal static class Measure
{
    /// <summary>A default (612 x 792) page carrying one word, at the given /Rotate.</summary>
    private static string MakeFixture(string dir, int rotate)
    {
        string path = Path.Combine(dir, $"measure-rot{rotate}.pdf");
        var doc = new PdfDocument();
        var page = doc.AddPage();
        using (var gfx = XGraphics.FromPdfPage(page))
            gfx.DrawString("RULE", new XFont("Helvetica", 24), XBrushes.Black, 70, 120);
        page.Rotate = rotate;
        doc.Save(path);
        return path;
    }

    /// <summary>
    /// The same page, but with /Rotate moved off the page and onto its parent /Pages node.
    /// </summary>
    /// <remarks>
    /// Legal (PDF 32000-1 7.7.3.3: /Rotate is an inheritable page attribute) and not rare —
    /// several scanner drivers write the angle once on the tree. PdfSharpCore's own PdfPage.Rotate
    /// reads the page dictionary alone and reports 0 for these, which would put the ruler's axes a
    /// quarter turn out on exactly the documents most likely to need measuring.
    /// </remarks>
    private static string MakeInheritedRotateFixture(string dir, int rotate)
    {
        string path = Path.Combine(dir, $"measure-inherited-rot{rotate}.pdf");
        string flat = MakeFixture(dir, 0);

        using (var doc = PdfSharpCore.Pdf.IO.PdfReader.Open(flat, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify))
        {
            var page = doc.Pages[0];
            page.Elements.Remove("/Rotate");

            var parentItem = page.Elements["/Parent"];
            var parent = parentItem as PdfDictionary
                      ?? (parentItem as PdfReference)?.Value as PdfDictionary;
            if (parent is null) throw new InvalidOperationException("page has no /Parent to inherit from");
            parent.Elements["/Rotate"] = new PdfInteger(rotate);

            doc.Save(path);
        }
        return path;
    }

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nMeasure tool: canvas drag -> distance in PDF points");

        // ── The page measures itself ────────────────────────────────────────────────────────
        // A drag from corner to corner of the rendered image spans the page as displayed, so it
        // must read back as exactly the displayed width / height / diagonal. On a quarter turn the
        // displayed dimensions swap, and a mapping that ignored the turn would report the UNSWAPPED
        // ones — 612 where the page shows 792 — which is the failure this catches.
        foreach (var (label, path) in new[]
                 {
                     ("/Rotate 0",   MakeFixture(tmp, 0)),
                     ("/Rotate 90",  MakeFixture(tmp, 90)),
                     ("/Rotate 180", MakeFixture(tmp, 180)),
                     ("/Rotate 270", MakeFixture(tmp, 270)),
                     ("inherited /Rotate 90",  MakeInheritedRotateFixture(tmp, 90)),
                     ("inherited /Rotate 270", MakeInheritedRotateFixture(tmp, 270)),
                 })
        {
            using var doc = PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import);
            var page = doc.Pages[0];
            var (dw, dh) = PdfPageGeometry.DisplaySize(page);

            // Canvas at 1 DIP per point keeps the arithmetic readable; the resolution-invariance
            // block below is what proves the number does not depend on this choice.
            double across = MeasureGeometry.DistancePoints(page, 0, 0, dw, 0, dw, dh);
            double down   = MeasureGeometry.DistancePoints(page, 0, 0, 0, dh, dw, dh);
            double diag   = MeasureGeometry.DistancePoints(page, 0, 0, dw, dh, dw, dh);
            double wantDiag = Math.Sqrt(dw * dw + dh * dh);

            Console.WriteLine($"  {label,-24} display {dw:F0}x{dh:F0}  across={across:F2} down={down:F2} diag={diag:F2}");
            Check($"{label}: a full-width drag measures the displayed width",
                  Math.Abs(across - dw) < 0.01, $"got {across:F3}, expected {dw:F3}");
            Check($"{label}: a full-height drag measures the displayed height",
                  Math.Abs(down - dh) < 0.01, $"got {down:F3}, expected {dh:F3}");
            Check($"{label}: a corner-to-corner drag measures the diagonal",
                  Math.Abs(diag - wantDiag) < 0.01, $"got {diag:F3}, expected {wantDiag:F3}");
        }

        // ── The same distance at any render resolution ──────────────────────────────────────
        // Zoom never reaches these coordinates (it is an ancestor LayoutTransform in the app), but
        // the render frame itself does move: RenderBoxDip, the monitor's DPI scaling and the grid
        // view's device-pixel tiles all change how many canvas units the page is drawn across. The
        // same physical span must report the same number through all of them.
        {
            using var doc = PdfSharpCore.Pdf.IO.PdfReader.Open(MakeFixture(tmp, 90), PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import);
            var page = doc.Pages[0];
            var (dw, dh) = PdfPageGeometry.DisplaySize(page);

            double baseline = MeasureGeometry.DistancePoints(page, 0.25 * dw, 0.10 * dh, 0.80 * dw, 0.70 * dh, dw, dh);
            double worst = 0;
            foreach (double k in new[] { 0.37, 1.0, 2.0, 5.5 })
            {
                double rw = dw * k, rh = dh * k;
                double got = MeasureGeometry.DistancePoints(page, 0.25 * rw, 0.10 * rh, 0.80 * rw, 0.70 * rh, rw, rh);
                worst = Math.Max(worst, Math.Abs(got - baseline));
            }
            Console.WriteLine($"  resolution invariance    baseline={baseline:F3}pt, worst drift {worst:F6}pt");
            Check("the same span measures the same at any render resolution", worst < 0.001,
                  $"worst drift {worst:F6}pt");
        }

        // ── A CropBox that is not the MediaBox ──────────────────────────────────────────────
        // PDFium rasterises the CropBox, so the image the user dragged across IS the crop. Measuring
        // against the MediaBox instead would report 612 x 792 for a page showing 400 x 500 — every
        // reading inflated by half, and nothing on screen to suggest it.
        {
            string path = Path.Combine(tmp, "measure-cropped.pdf");
            var d = new PdfDocument();
            var p = d.AddPage();
            using (var g = XGraphics.FromPdfPage(p))
                g.DrawString("MARK", new XFont("Helvetica", 24), XBrushes.Black, 200, 300);
            var arr = new PdfArray();
            foreach (double v in new[] { 100.0, 200.0, 500.0, 700.0 })   // 400 x 500, offset origin
                arr.Elements.Add(new PdfReal(v));
            p.Elements["/CropBox"] = arr;
            d.Save(path);

            using var doc = PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import);
            var page = doc.Pages[0];
            var (dw, dh) = PdfPageGeometry.DisplaySize(page);
            double across = MeasureGeometry.DistancePoints(page, 0, 0, dw, 0, dw, dh);
            double down   = MeasureGeometry.DistancePoints(page, 0, 0, 0, dh, dw, dh);

            Console.WriteLine($"  CropBox 100,200..500,700 display {dw:F0}x{dh:F0}  across={across:F2} down={down:F2}");
            Check("a cropped page measures the CROP, not the MediaBox",
                  Math.Abs(across - 400) < 0.01 && Math.Abs(down - 500) < 0.01,
                  $"got {across:F2} x {down:F2}, expected 400 x 500");
        }

        // ── Unit conversions ────────────────────────────────────────────────────────────────
        // The numbers the caption is built from. Checked as conversions rather than as an exact
        // caption string, because the caption formats under the user's culture (a decimal comma is
        // correct for a German user) and pinning the separator would make this fail on their build.
        {
            Check("72 pt is exactly 1 inch",
                  Math.Abs(MeasureGeometry.ToInches(72) - 1) < 1e-12,
                  $"got {MeasureGeometry.ToInches(72)}");
            Check("72 pt is exactly 25.4 mm",
                  Math.Abs(MeasureGeometry.ToMillimetres(72) - 25.4) < 1e-12,
                  $"got {MeasureGeometry.ToMillimetres(72)}");
            Check("1 inch of millimetres is 1 inch of points",
                  Math.Abs(MeasureGeometry.ToMillimetres(MeasureGeometry.PointsPerInch)
                           - MeasureGeometry.MillimetresPerInch) < 1e-12, "");

            double worst = 0;
            foreach (double pt in new[] { 0.0, 0.3, 12.0, 72.0, 612.0, 1234.5 })
            {
                worst = Math.Max(worst, Math.Abs(MeasureGeometry.ToInches(pt) * 72.0 - pt));
                worst = Math.Max(worst, Math.Abs(MeasureGeometry.ToMillimetres(pt) / 25.4 * 72.0 - pt));
            }
            Check("points -> inches / mm -> points round-trips", worst < 1e-9, $"worst drift {worst:E2}");

            // Structure only: all three units present, in the documented order, so a future edit
            // that drops one is caught even though the digits themselves are culture-dependent.
            string caption = MeasureGeometry.Format(180);
            int i = caption.IndexOf(" in", StringComparison.Ordinal);
            int m = caption.IndexOf(" mm", StringComparison.Ordinal);
            int t = caption.IndexOf(" pt", StringComparison.Ordinal);
            Console.WriteLine($"  caption for 180pt: {caption}");
            Check("the caption carries inches, millimetres and points, in that order",
                  i >= 0 && m > i && t > m, caption);
        }
    }
}
