using System.Text;
using PdfSharpCore.Pdf;
using TDPdf.Services;

/// <summary>
/// Print and Save Flattened render annotations by their Print flag; the screen does not.
/// </summary>
/// <remarks>
/// Each annotation is a filled black square, so "is it drawn" is "is there ink in its rectangle".
/// The page is rendered both ways through the real renderer: as on screen (forPrint false), where
/// NoView decides, and as printed, where the Print flag decides. Widgets are checked separately
/// because they are painted by FFLDraw, which ignores the Print flag unless TDPdf adjusts it.
/// </remarks>
internal static class PrintFlags
{
    private const int Print = 4, NoView = 32;

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nPrint rendering honours annotation Print flags");

        string path = Path.Combine(tmp, "printflags.pdf");
        var cases = new (string Name, string Subtype, int Flags, double X, bool Screen, bool Paper)[]
        {
            ("printable annotation",   "/Square", Print,          50,  true,  true),
            ("screen-only annotation", "/Square", 0,              150, true,  false),
            ("print-only annotation",  "/Square", Print | NoView, 250, false, true),
            ("printable field",        "/Widget", Print,          350, true,  true),
            ("screen-only field",      "/Widget", 0,              450, true,  false),
            ("print-only field",       "/Widget", Print | NoView, 520, false, true),
        };
        const double y = 600, size = 40;

        {
            var doc = new PdfDocument();
            var page = doc.AddPage();   // 612 x 792
            var annots = new PdfArray(doc);
            var fields = new PdfArray(doc);
            int n = 0;
            foreach (var c in cases)
            {
                var ap = new PdfDictionary(doc);
                ap.Elements["/Type"] = new PdfName("/XObject");
                ap.Elements["/Subtype"] = new PdfName("/Form");
                ap.Elements["/BBox"] = Arr(doc, 0, 0, size, size);
                ap.CreateStream(Encoding.ASCII.GetBytes($"0 0 0 rg 0 0 {size} {size} re f\n"));
                doc.Internals.AddObject(ap);
                var apDict = new PdfDictionary(doc);
                apDict.Elements["/N"] = ap.Reference;

                var a = new PdfDictionary(doc);
                a.Elements["/Type"] = new PdfName("/Annot");
                a.Elements["/Subtype"] = new PdfName(c.Subtype);
                a.Elements["/Rect"] = Arr(doc, c.X, y, c.X + size, y + size);
                a.Elements["/F"] = new PdfInteger(c.Flags);
                a.Elements["/AP"] = apDict;
                a.Elements["/P"] = page.Reference;
                if (c.Subtype == "/Widget")
                {
                    a.Elements["/FT"] = new PdfName("/Btn");
                    a.Elements["/Ff"] = new PdfInteger(1 << 16);   // push button: no value, just its /AP
                    a.Elements["/T"] = new PdfString($"f{n++}");
                }
                doc.Internals.AddObject(a);
                annots.Elements.Add(a.Reference);
                if (c.Subtype == "/Widget") fields.Elements.Add(a.Reference);
            }
            page.Elements["/Annots"] = annots;
            var acro = new PdfDictionary(doc);
            acro.Elements["/Fields"] = fields;
            doc.Internals.AddObject(acro);
            doc.Internals.Catalog.Elements["/AcroForm"] = acro.Reference;
            doc.Save(path);
        }

        const int rw = 612, rh = 792;
        var screen = PdfiumInterop.RenderPageWithAnnotations(path, 0, rw, rh);
        var paper = PdfiumInterop.RenderPageWithAnnotations(path, 0, rw, rh, forPrint: true);
        if (screen is null || paper is null) { Check("print-flag fixture renders", false, "null render"); return; }

        foreach (var c in cases)
        {
            double s = Ink(screen, rw, rh, c.X, y, size), p = Ink(paper, rw, rh, c.X, y, size);
            Check($"{c.Name}: {(c.Screen ? "shown" : "not shown")} on screen", (s > 0.5) == c.Screen, $"ink {s:P0}");
            Check($"{c.Name}: {(c.Paper ? "printed" : "not printed")}", (p > 0.5) == c.Paper, $"ink {p:P0}");
        }
    }

    /// <summary>Fraction of dark pixels in a page-space square (1 px per point, y flipped).</summary>
    private static double Ink(byte[] bgra, int rw, int rh, double x, double y, double size)
    {
        int x0 = (int)x + 4, x1 = (int)(x + size) - 4;
        int y0 = (int)(rh - (y + size)) + 4, y1 = (int)(rh - y) - 4;
        int ink = 0, total = 0;
        for (int py = y0; py < y1; py++)
            for (int px = x0; px < x1; px++)
            {
                int at = (py * rw + px) * 4;
                if (bgra[at] < 128 && bgra[at + 1] < 128 && bgra[at + 2] < 128) ink++;
                total++;
            }
        return total == 0 ? 0 : (double)ink / total;
    }

    private static PdfArray Arr(PdfDocument doc, params double[] v)
    {
        var a = new PdfArray(doc);
        foreach (double x in v) a.Elements.Add(new PdfReal(x));
        return a;
    }
}
