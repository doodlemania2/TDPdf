using PdfSharpCore.Pdf;
using TDPdf.Services;

/// <summary>Flatten and export keep widgets that a damaged field list does not mention.</summary>
internal static class FlattenOrphanWidgets
{
    private static PdfArray Arr(PdfDocument d, params double[] v)
    {
        var a = new PdfArray(d);
        foreach (var x in v) a.Elements.Add(new PdfReal(x));
        return a;
    }

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nWidgets missing from the field list still render");
        var mkRedBlue = new Action<PdfDictionary, PdfDocument>((w, d) =>
        {
            var mk = new PdfDictionary(d);
            mk.Elements["/BG"] = Arr(d, 1, 0, 0);
            mk.Elements["/BC"] = Arr(d, 0, 0, 1);
            w.Elements["/MK"] = mk;
        });
        // Widgets that are only in a page's /Annots still paint through the shipping render path
        // (the one print, flatten and image export use): (a) listed in /AcroForm /Fields, (b) not
        // listed, (c) no /AcroForm at all. PDFium's form-fill layer builds a control from the field
        // type, not from /Fields, so a damaged field list does not lose the appearance.
        // Not covered: a widget with no /FT anywhere up its /Parent chain. PDFium paints widgets only
        // through the form-fill layer, which makes no control for it, so it cannot be drawn there.
        foreach (var variant in new[] { "listed", "unlisted", "no AcroForm" })
        {
            var doc = new PdfDocument();
            var pg = doc.AddPage();
            var wd = new PdfDictionary(doc);
            mkRedBlue(wd, doc);
            var bsd = new PdfDictionary(doc);
            bsd.Elements["/W"] = new PdfInteger(4);
            wd.Elements["/BS"] = bsd;
            var ch = FieldChrome.Read(wd);
            var xo = new PdfDictionary(doc);
            xo.Elements["/Type"] = new PdfName("/XObject");
            xo.Elements["/Subtype"] = new PdfName("/Form");
            xo.Elements["/BBox"] = Arr(doc, 0, 0, 100, 40);
            xo.CreateStream(System.Text.Encoding.ASCII.GetBytes(ch.ToContent(100, 40)));
            doc.Internals.AddObject(xo);
            wd.Elements["/Type"] = new PdfName("/Annot");
            wd.Elements["/Subtype"] = new PdfName("/Widget");
            wd.Elements["/FT"] = new PdfName("/Tx");
            wd.Elements["/T"] = new PdfString("f");
            wd.Elements["/F"] = new PdfInteger(4);
            wd.Elements["/Rect"] = Arr(doc, 50, 700, 150, 740);
            wd.Elements["/V"] = new PdfString("");
            var apd = new PdfDictionary(doc);
            apd.Elements["/N"] = xo.Reference!;
            wd.Elements["/AP"] = apd;
            doc.Internals.AddObject(wd);
            var an = new PdfArray(doc);
            an.Elements.Add(wd.Reference!);
            pg.Elements["/Annots"] = an;
            if (variant != "no AcroForm")
            {
                var af = new PdfDictionary(doc);
                var fl = new PdfArray(doc);
                if (variant == "listed") fl.Elements.Add(wd.Reference!);
                af.Elements["/Fields"] = fl;
                doc.Internals.Catalog.Elements["/AcroForm"] = af;
            }
            double pageH = pg.Height.Point;
            string path = Path.Combine(tmp, "chrome.pdf");
            doc.Save(path);

            int rw = 612, rh = 792;
            var bgra = PdfiumInterop.RenderPageWithAnnotations(path, 0, rw, rh)
                       ?? throw new Exception("the page did not render at all");
            (int r, int g, int b) Px(double x, double y)
            {
                int at = (((int)(pageH - y)) * rw + (int)x) * 4;
                return (bgra[at + 2], bgra[at + 1], bgra[at]);
            }
            var inside = Px(100, 720);
            var edge = Px(51.5, 720);
            Check($"flatten widget ({variant}): interior is the /BG red", inside.r > 200 && inside.g < 60 && inside.b < 60, $"{inside}");
            Check($"flatten widget ({variant}): edge is the /BC blue", edge.b > 200 && edge.r < 60 && edge.g < 60, $"{edge}");
        }
    }
}
