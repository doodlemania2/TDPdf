using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using TDPdf;
using TDPdf.Services;

/// <summary>
/// Pins the night-mode picture carve-out: the boxes <see cref="PdfImages.GetFracRects"/> returns,
/// scaled to a PDFium render of the page, must sit on the image's pixels — on every quarter turn
/// of a page whose CropBox does not start at the origin, and with /Rotate inherited from the page
/// tree as well as set on the page.
/// </summary>
/// <remarks>
/// Night mode (DocInvert.cs) inverts the BGRA buffer PDFium hands back and then inverts the image
/// boxes a second time so pictures keep their real colours. There is no TDPdf-side pixel rotation
/// anywhere on that path: every render site sizes the buffer from Docnet's page reader (the page
/// as displayed) and calls FPDF_RenderPageBitmap with rotate 0, which draws the visible box with
/// the page's own /Rotate already applied. So the frame the boxes must be in is the DISPLAYED
/// page, and a box in any other frame carves a hole in the wrong part of the page while the
/// picture itself shows as a negative.
///
/// As in RegionCopy.cs, the renderer is the authority: "where the image is" means the pixels
/// PDFium paints in the image's colour, not a rectangle derived from the conversion under test.
/// The pixel box is built with the same floor / ceiling rounding InvertBgraInPlaceExcept uses.
///
/// The last check per page pins PdfPig's convention for IMAGE boxes the way RegionCopy pins it for
/// words: displayed frame, crop origin subtracted and /Rotate applied — i.e. exactly
/// <see cref="PdfPageGeometry.PdfToTextFrame(PdfPage, PdfiumInterop.PdfRect)"/> of the user-space
/// placement. A PdfPig upgrade that moved image boxes into user space fails there first.
///
/// Written test-first against the shipping code, which it passed unchanged: the frame was already
/// right and only the doc comments (which said "unrotated page") were wrong. It stays as the
/// guard. Note the boxes PdfPig returns on a 90 / 270 page are not normalized (Left > Right, or
/// Bottom > Top); dropping GetFracRects' edge swap fails this test on every quarter-turned page.
/// </remarks>
internal static class NightModeImages
{
    // Where the fixture draws its picture, in XGraphics' top-left-origin points on a 612 x 792
    // page: user space x 200..260, y 412..492 — inside both crops below.
    private const double ImgX = 200, ImgY = 300, ImgW = 60, ImgH = 80;

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nNight mode: image carve-out boxes vs the PDFium render (cropped + rotated)");

        string pngPath = Path.Combine(tmp, "nightmode-red.png");
        File.WriteAllBytes(pngPath, ImageGuard.Png(20, 20, 255, 0, 0));

        var crops = new (string Name, double[]? Box)[]
        {
            ("no CropBox", null),
            ("offset CropBox", new[] { 100.0, 200.0, 500.0, 700.0 }),
            ("CropBox past the MediaBox", new[] { -50.0, 150.0, 480.0, 900.0 }),
        };

        foreach (var (cropName, cropBox) in crops)
        foreach (int rotate in new[] { 0, 90, 180, 270 })
        foreach (bool inherited in new[] { false, true })
        {
            // Inheritance only means something on a turned page; skip the duplicate 0 case.
            if (inherited && rotate == 0) continue;
            string label = $"/Rotate {rotate}{(inherited ? " (inherited)" : "")} + {cropName}";
            string path = Path.Combine(tmp, $"nightmode-{rotate}-{inherited}-{cropName.Replace(' ', '_')}.pdf");
            {
                var d = new PdfSharpCore.Pdf.PdfDocument();
                var p = d.AddPage();   // 612 x 792
                using (var g = XGraphics.FromPdfPage(p))
                    g.DrawImage(XImage.FromFile(pngPath), ImgX, ImgY, ImgW, ImgH);
                if (cropBox is not null)
                {
                    var arr = new PdfArray();
                    foreach (double v in cropBox) arr.Elements.Add(new PdfReal(v));
                    p.Elements["/CropBox"] = arr;
                }
                // After drawing: content stays in unrotated user space.
                if (inherited)
                {
                    p.Elements.Remove("/Rotate");
                    d.Pages.Elements["/Rotate"] = new PdfInteger(rotate);
                }
                else p.Rotate = rotate;
                d.Save(path);
            }

            var (bgra, rw, rh) = Geometry.RenderFirstPage(path);
            bool turned = rotate is 90 or 270;
            // Fixture sanity: the rotation really took effect (an inherited /Rotate the writer
            // dropped would make every check below pass vacuously on an unturned page).
            // Every visible box above is portrait, so a quarter turn must render landscape.
            Check($"{label}: fixture renders with the rotation applied", (rw > rh) == turned, $"{rw}x{rh}");

            // The image's pixels as PDFium drew them.
            int redCount = 0;
            var isRed = new bool[rw * rh];
            for (int i = 0; i < rw * rh; i++)
            {
                int o = i * 4;   // BGRA
                if (bgra[o + 2] > 200 && bgra[o + 1] < 80 && bgra[o] < 80) { isRed[i] = true; redCount++; }
            }
            if (redCount == 0) { Check($"{label}: fixture renders the image", false, "no image pixels"); continue; }

            FracRect[] rects;
            PdfiumInterop.PdfRect pigBox;
            using (var pig = UglyToad.PdfPig.PdfDocument.Open(path))
            {
                rects = PdfImages.GetFracRects(pig, 0);
                var imgs = pig.GetPage(1).GetImages().ToList();
                var b = imgs.Count > 0 ? imgs[0].BoundingBox : default;
                pigBox = new PdfiumInterop.PdfRect(b.Left, b.Bottom, b.Right, b.Top);
            }
            if (rects.Length != 1)
            {
                Check($"{label}: exactly one carve-out box", false, $"{rects.Length} boxes");
                continue;
            }

            // Fractions -> pixels exactly as InvertBgraInPlaceExcept does it.
            var r = rects[0];
            int x0 = Math.Max(0, (int)Math.Floor(r.L * rw)), x1 = Math.Min(rw, (int)Math.Ceiling(r.R * rw));
            int y0 = Math.Max(0, (int)Math.Floor(r.T * rh)), y1 = Math.Min(rh, (int)Math.Ceiling(r.B * rh));
            int inside = 0, area = Math.Max(0, x1 - x0) * Math.Max(0, y1 - y0);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    if (isRed[y * rw + x]) inside++;
            double recall = (double)inside / redCount;
            double precision = area > 0 ? (double)inside / area : 0;
            string detail = $"box px [{x0},{y0})-[{x1},{y1}) on {rw}x{rh}; covers {recall:P0} of the image, "
                          + $"{precision:P0} of the box is image";
            Check($"{label}: the carve-out covers the image", recall >= 0.9, detail);
            Check($"{label}: the carve-out is the image, not a larger or displaced box", precision >= 0.9, detail);

            // PdfPig's convention for image boxes, pinned (see remarks).
            using var sharp = PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import);
            var user = new PdfiumInterop.PdfRect(ImgX, 792 - ImgY - ImgH, ImgX + ImgW, 792 - ImgY);
            var expected = PdfPageGeometry.PdfToTextFrame(sharp.Pages[0], user);
            bool same = Math.Abs(Math.Min(pigBox.Left, pigBox.Right) - expected.Left) < 1
                     && Math.Abs(Math.Max(pigBox.Left, pigBox.Right) - expected.Right) < 1
                     && Math.Abs(Math.Min(pigBox.Bottom, pigBox.Top) - expected.Bottom) < 1
                     && Math.Abs(Math.Max(pigBox.Bottom, pigBox.Top) - expected.Top) < 1;
            Check($"{label}: PdfPig image boxes are in the displayed frame, like its words", same,
                  $"PdfPig L={pigBox.Left:F1} B={pigBox.Bottom:F1} R={pigBox.Right:F1} T={pigBox.Top:F1}; "
                  + $"displayed L={expected.Left:F1} B={expected.Bottom:F1} R={expected.Right:F1} T={expected.Top:F1}");
        }
    }
}
