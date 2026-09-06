using System.Runtime.InteropServices;
using PdfSharpCore.Drawing;
using TDPdf.Services;

/// <summary>
/// Pins <see cref="PdfRedaction.CanvasRectToPdf"/> against what PDFium actually draws.
/// </summary>
/// <remarks>
/// The mapping from a rectangle the user dragged on screen to a rectangle in PDF user space is the
/// one place in redaction where being wrong is silent and total: the redaction runs, the pipeline
/// reports success on whatever it did find in that area, and the content the user marked is still
/// in the file. /Rotate is where it goes wrong, because the page image is rotated and object
/// coordinates are not.
///
/// So this does not check the mapping against a second copy of the same derivation — that would
/// agree with itself no matter which way round the convention is. It renders each quarter turn
/// through PDFium, finds the ink in the resulting image, maps that back, and requires the answer to
/// land on the object PDFium says is there. The renderer is the authority, and it is the same
/// renderer the app puts on screen.
/// </remarks>
internal static class Geometry
{
    private const string Pdfium = "pdfium.dll";
    private const int BgrxAlpha = 0;   // FPDFBitmap_BGRx: 4 bytes per pixel, no alpha channel

    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern IntPtr FPDF_LoadDocument(string path, string? password);
    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDFBitmap_FillRect(IntPtr bmp, int l, int t, int w, int h, uint color);
    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_RenderPageBitmap(IntPtr bmp, IntPtr page, int x, int y, int w, int h, int rotate, int flags);
    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bmp);
    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FPDFBitmap_GetStride(IntPtr bmp);
    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDFBitmap_Destroy(IntPtr bmp);
    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FPDFPage_CountObjects(IntPtr page);
    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);
    [DllImport(Pdfium, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool FPDFPageObj_GetBounds(IntPtr obj, out float l, out float b, out float r, out float t);

    /// <summary>
    /// A page carrying one short word, deliberately off-centre in BOTH axes so that all four
    /// quarter turns put it somewhere visibly different. A word in the middle of the page would
    /// satisfy a 180-degree error.
    /// </summary>
    private static string MakeFixture(string dir, int rotate)
    {
        string path = Path.Combine(dir, $"rot{rotate}.pdf");
        var doc = new PdfSharpCore.Pdf.PdfDocument();
        var page = doc.AddPage();                       // 612 x 792, PdfSharpCore's default
        using (var gfx = XGraphics.FromPdfPage(page))
            gfx.DrawString("MARK", new XFont("Helvetica", 24), XBrushes.Black, 70, 120);
        // Set AFTER drawing: XGraphics lays content out in unrotated page space, which is exactly
        // the space object coordinates live in, and /Rotate is a display instruction on top.
        page.Rotate = rotate;
        doc.Save(path);
        return path;
    }

    /// <summary>Union of every page object's bounds, in PDF user space. The ground truth.</summary>
    private static (double L, double B, double R, double T) ObjectBounds(IntPtr page)
    {
        double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
        int n = FPDFPage_CountObjects(page);
        for (int i = 0; i < n; i++)
        {
            var obj = FPDFPage_GetObject(page, i);
            if (obj == IntPtr.Zero) continue;
            if (!FPDFPageObj_GetBounds(obj, out float ol, out float ob, out float or, out float ot)) continue;
            l = Math.Min(l, ol); b = Math.Min(b, ob); r = Math.Max(r, or); t = Math.Max(t, ot);
        }
        return (l, b, r, t);
    }

    /// <summary>Bounding box of the non-white pixels, in image coordinates (y down from the top).</summary>
    private static (double X, double Y, double W, double H, int RW, int RH) InkBox(IntPtr page)
    {
        int rw = (int)Math.Round(FPDF_GetPageWidthF(page));
        int rh = (int)Math.Round(FPDF_GetPageHeightF(page));
        IntPtr bmp = FPDFBitmap_Create(rw, rh, BgrxAlpha);
        try
        {
            FPDFBitmap_FillRect(bmp, 0, 0, rw, rh, 0xFFFFFFFF);
            // rotate: 0 — the page's own /Rotate is applied by the renderer, which is the whole
            // point. Asking for an extra turn here would test the wrong thing.
            FPDF_RenderPageBitmap(bmp, page, 0, 0, rw, rh, 0, 0);

            int stride = FPDFBitmap_GetStride(bmp);
            IntPtr buf = FPDFBitmap_GetBuffer(bmp);
            var row = new byte[stride];
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (int y = 0; y < rh; y++)
            {
                Marshal.Copy(buf + y * stride, row, 0, stride);
                for (int x = 0; x < rw; x++)
                {
                    // Antialiased glyph edges are pale grey; 200 keeps the faintest of them and
                    // still rejects the white ground.
                    if (row[x * 4] > 200 && row[x * 4 + 1] > 200 && row[x * 4 + 2] > 200) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (minX > maxX) return (0, 0, 0, 0, rw, rh);
            return (minX, minY, maxX - minX + 1, maxY - minY + 1, rw, rh);
        }
        finally { FPDFBitmap_Destroy(bmp); }
    }

    /// <summary>FPDF_ANNOT: draw annotation and widget appearance streams as well as page content.</summary>
    public const int WithAnnotations = 0x01;

    /// <summary>Page 1 rendered at 1 pixel per point, as BGRA. Shared with the rasterise tests.</summary>
    public static (byte[] bgra, int w, int h) RenderFirstPage(string path) => RenderFirstPage(path, 0);

    public static (byte[] bgra, int w, int h) RenderFirstPage(string path, int flags)
    {
        IntPtr doc = FPDF_LoadDocument(path, null);
        try
        {
            IntPtr page = FPDF_LoadPage(doc, 0);
            try
            {
                int rw = (int)Math.Round(FPDF_GetPageWidthF(page));
                int rh = (int)Math.Round(FPDF_GetPageHeightF(page));
                IntPtr bmp = FPDFBitmap_Create(rw, rh, BgrxAlpha);
                try
                {
                    FPDFBitmap_FillRect(bmp, 0, 0, rw, rh, 0xFFFFFFFF);
                    FPDF_RenderPageBitmap(bmp, page, 0, 0, rw, rh, 0, flags);
                    int stride = FPDFBitmap_GetStride(bmp);
                    IntPtr buf = FPDFBitmap_GetBuffer(bmp);
                    var outp = new byte[rw * rh * 4];
                    var row = new byte[stride];
                    for (int y = 0; y < rh; y++)
                    {
                        Marshal.Copy(buf + y * stride, row, 0, stride);
                        Array.Copy(row, 0, outp, y * rw * 4, rw * 4);
                    }
                    return (outp, rw, rh);
                }
                finally { FPDFBitmap_Destroy(bmp); }
            }
            finally { FPDF_ClosePage(page); }
        }
        finally { FPDF_CloseDocument(doc); }
    }

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nCanvas rect -> PDF rect, checked against PDFium's own rendering");

        foreach (int rotate in new[] { 0, 90, 180, 270 })
        {
            string path = MakeFixture(tmp, rotate);

            IntPtr doc = FPDF_LoadDocument(path, null);
            (double L, double B, double R, double T) truth;
            (double X, double Y, double W, double H, int RW, int RH) ink;
            try
            {
                IntPtr page = FPDF_LoadPage(doc, 0);
                try { truth = ObjectBounds(page); ink = InkBox(page); }
                finally { FPDF_ClosePage(page); }
            }
            finally { FPDF_CloseDocument(doc); }

            using var sharp = PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import);
            var mapped = PdfPageGeometry.CanvasRectToPdf(
                sharp.Pages[0], ink.X, ink.Y, ink.W, ink.H, ink.RW, ink.RH);

            // The ink sits INSIDE the object's bounds — glyph bounds carry the font's ascender and
            // descender, which no letter of "MARK" reaches — so containment, not equality, is the
            // honest assertion. 3pt of slack absorbs the pixel rounding at scale 1.
            const double slack = 3;
            bool inside = mapped.Left   >= truth.L - slack
                       && mapped.Bottom >= truth.B - slack
                       && mapped.Right  <= truth.R + slack
                       && mapped.Top    <= truth.T + slack;

            // Containment alone would also be satisfied by a rectangle that collapsed to a point,
            // so require the mapped rectangle to actually cover the word it came from.
            double wFrac = (mapped.Right - mapped.Left) / (truth.R - truth.L);
            double hFrac = (mapped.Top - mapped.Bottom) / (truth.T - truth.B);

            Console.WriteLine($"  /Rotate {rotate,3}: render {ink.RW}x{ink.RH}, ink at ({ink.X:F0},{ink.Y:F0}) {ink.W:F0}x{ink.H:F0}");
            Console.WriteLine($"             mapped  L={mapped.Left:F1} B={mapped.Bottom:F1} R={mapped.Right:F1} T={mapped.Top:F1}");
            Console.WriteLine($"             object  L={truth.L:F1} B={truth.B:F1} R={truth.R:F1} T={truth.T:F1}");
            Check($"/Rotate {rotate}: mapped rect lands on the object", inside,
                  inside ? "" : "mapped somewhere else entirely");
            Check($"/Rotate {rotate}: mapped rect covers the word", wFrac > 0.8 && hFrac > 0.4,
                  $"width {wFrac:P0} height {hFrac:P0} of the object box");
        }

        // ── The two directions must be exact inverses ────────────────────────────────────
        // Redaction removes objects using CanvasRectToPdf and the rasteriser paints over them using
        // PdfRectToCanvas. If those two disagreed by even a little, a redaction would black out one
        // part of the page and delete another — and both halves would look plausible on their own.
        {
            foreach (int rotate in new[] { 0, 90, 180, 270 })
            {
                string path = MakeFixture(tmp, rotate);
                using var sharp = PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import);
                var page = sharp.Pages[0];
                var (dw, dh) = PdfPageGeometry.DisplaySize(page);
                double rw = dw, rh = dh;                    // 1 pixel per point keeps the arithmetic honest

                double worst = 0;
                foreach (var (x, y, w, h) in new[]
                         { (10.0, 20.0, 100.0, 40.0), (0.0, 0.0, 50.0, 50.0),
                           (dw - 60, dh - 30, 55.0, 25.0), (dw / 3, dh / 4, 120.0, 90.0) })
                {
                    var pdf = PdfPageGeometry.CanvasRectToPdf(page, x, y, w, h, rw, rh);
                    var back = PdfPageGeometry.PdfRectToCanvas(page, pdf, rw, rh);
                    // PdfRectToCanvas rounds outward on purpose, so it can only ever be a whole
                    // pixel wider — never narrower, and never in the wrong place.
                    worst = Math.Max(worst, Math.Abs(back.X - x));
                    worst = Math.Max(worst, Math.Abs(back.Y - y));
                    worst = Math.Max(worst, Math.Abs(back.W - w));
                    worst = Math.Max(worst, Math.Abs(back.H - h));
                }
                Check($"/Rotate {rotate}: canvas -> PDF -> canvas round-trips", worst <= 1.0,
                      $"worst drift {worst:F2}px");
            }
        }

        // ── A CropBox that does not start at the origin ───────────────────────────────────
        // PDFium rasterises the CropBox, so the top-left of the image is the top-left of the CROP,
        // not of the page. Ignoring the offset shifts every redaction by the inset.
        {
            string path = Path.Combine(tmp, "cropped.pdf");
            var d = new PdfSharpCore.Pdf.PdfDocument();
            var p = d.AddPage();
            using (var g = XGraphics.FromPdfPage(p))
                g.DrawString("MARK", new XFont("Helvetica", 24), XBrushes.Black, 200, 300);
            var arr = new PdfSharpCore.Pdf.PdfArray();
            foreach (double v in new[] { 100.0, 200.0, 500.0, 700.0 })
                arr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(v));
            p.Elements["/CropBox"] = arr;
            d.Save(path);

            IntPtr doc = FPDF_LoadDocument(path, null);
            (double L, double B, double R, double T) truth;
            (double X, double Y, double W, double H, int RW, int RH) ink;
            try
            {
                IntPtr page = FPDF_LoadPage(doc, 0);
                try { truth = ObjectBounds(page); ink = InkBox(page); }
                finally { FPDF_ClosePage(page); }
            }
            finally { FPDF_CloseDocument(doc); }

            using var sharp = PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import);
            var mapped = PdfPageGeometry.CanvasRectToPdf(
                sharp.Pages[0], ink.X, ink.Y, ink.W, ink.H, ink.RW, ink.RH);

            const double slack = 3;
            bool inside = mapped.Left >= truth.L - slack && mapped.Bottom >= truth.B - slack
                       && mapped.Right <= truth.R + slack && mapped.Top <= truth.T + slack;
            Console.WriteLine($"  CropBox 100,200..500,700: render {ink.RW}x{ink.RH}");
            Console.WriteLine($"             mapped  L={mapped.Left:F1} B={mapped.Bottom:F1} R={mapped.Right:F1} T={mapped.Top:F1}");
            Console.WriteLine($"             object  L={truth.L:F1} B={truth.B:F1} R={truth.R:F1} T={truth.T:F1}");
            Check("offset CropBox: mapped rect lands on the object", inside,
                  inside ? "" : "the crop origin was not accounted for");
        }
    }
}
