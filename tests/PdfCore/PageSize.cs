using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using TDPdf.Services;

/// <summary>
/// Pins what PdfSharpCore reports as Width/Height on a /Rotate 90/270 page against
/// <see cref="PdfPageGeometry.DisplaySize"/>. PdfPage's dictionary constructor marks such a page
/// Landscape, so Width/Height come back ALREADY swapped; code that swaps them again (the continuous
/// view slot and Fit Page used to) lands back on the unrotated shape. Also pins that the annotation
/// bake's old inline scale maths equals <see cref="PdfPageGeometry.RasterToXGraphics"/>.
/// </summary>
internal static class PageSize
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nPage size on rotated pages: PdfPage.Width/Height vs DisplaySize");
        foreach (int rotate in new[] { 0, 90, 180, 270 })
        {
            foreach (bool cropped in new[] { false, true })
            {
                string path = Path.Combine(tmp, $"pagesize-{rotate}-{cropped}.pdf");
                {
                    var d = new PdfDocument();
                    var p = d.AddPage();   // 612 x 792
                    if (cropped)
                    {
                        var arr = new PdfArray();
                        foreach (double v in new[] { 50.0, 100.0, 450.0, 600.0 })   // 400 x 500
                            arr.Elements.Add(new PdfReal(v));
                        p.Elements["/CropBox"] = arr;
                    }
                    p.Rotate = rotate;
                    d.Save(path);
                }
                using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
                var page = doc.Pages[0];
                bool quarter = rotate is 90 or 270;
                double bw = cropped ? 400 : 612, bh = cropped ? 500 : 792;
                var (dw, dh) = PdfPageGeometry.DisplaySize(page);
                string tag = $"/Rotate {rotate}{(cropped ? " cropped" : "")}";

                Check($"{tag}: DisplaySize is the displayed shape",
                      Math.Abs(dw - (quarter ? bh : bw)) < 0.01 && Math.Abs(dh - (quarter ? bw : bh)) < 0.01,
                      $"{dw}x{dh}");
                if (!cropped)
                    Check($"{tag}: PdfPage.Width/Height already match (so a second swap is wrong)",
                          Math.Abs(page.Width.Point - dw) < 0.01 && Math.Abs(page.Height.Point - dh) < 0.01,
                          $"{page.Width.Point}x{page.Height.Point} vs {dw}x{dh}");

                // The annotation bake's former inline scale, against the shared helper.
                var box = PdfPageGeometry.VisibleBox(page);
                int rot = PdfPageGeometry.Rotation(page);
                const double renderW = 800, renderH = 1000;
                double oldSx = (quarter ? box.Height : box.Width) / renderW;
                double oldSy = (quarter ? box.Width : box.Height) / renderH;
                var (sx, sy, m) = PdfPageGeometry.RasterToXGraphics(page, renderW, renderH);
                var oldM = PdfPageGeometry.VisualToXGraphics(rot, box, page.Height.Point);
                Check($"{tag}: RasterToXGraphics scale == old bake scale",
                      Math.Abs(sx - oldSx) < 1e-9 && Math.Abs(sy - oldSy) < 1e-9, $"{sx},{sy} vs {oldSx},{oldSy}");
                Check($"{tag}: RasterToXGraphics matrix == old bake matrix",
                      Nullable.Equals(m, oldM), $"{m} vs {oldM}");
            }
        }
    }
}
