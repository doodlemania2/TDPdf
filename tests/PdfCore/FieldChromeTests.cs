using PdfSharpCore.Pdf;
using TDPdf.Services;

/// <summary>
/// A filled field keeps the background and border it was designed with (/MK /BG, /BC, /BS), the text
/// starts inside the border, and /Q alignment moves the text origin.
/// </summary>
internal static class FieldChromeTests
{
    private static PdfArray Arr(PdfDocument d, params double[] v)
    {
        var a = new PdfArray(d);
        foreach (var x in v) a.Elements.Add(new PdfReal(x));
        return a;
    }

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nField chrome (background, border, inset, alignment)");
        var d = new PdfDocument();
        var page = d.AddPage();

        // Colour arrays: 1, 3, 4 components; empty means transparent; anything else is ignored.
        Check("chrome: gray colour operator", FieldChrome.ParseColor(Arr(d, 0.5))?.Operator(false) == "0.5 g", "");
        Check("chrome: RGB stroke operator", FieldChrome.ParseColor(Arr(d, 1, 0, 0.25))?.Operator(true) == "1 0 0.25 RG", "");
        Check("chrome: CMYK fill operator", FieldChrome.ParseColor(Arr(d, 0, 1, 1, 0))?.Operator(false) == "0 1 1 0 k", "");
        Check("chrome: empty array is transparent", FieldChrome.ParseColor(Arr(d)) is null, "");
        Check("chrome: two components are ignored", FieldChrome.ParseColor(Arr(d, 1, 1)) is null, "");
        var rgb = FieldChrome.ParseColor(Arr(d, 0, 0, 0, 1))!.Value.ToRgb();
        Check("chrome: CMYK black converts to RGB black", rgb == (0, 0, 0), $"{rgb}");

        // Border width and style, from /BS and from the legacy /Border.
        PdfDictionary Widget(Action<PdfDictionary> setup)
        {
            var w = new PdfDictionary(d);
            setup(w);
            return w;
        }
        var mkRedBlue = new Action<PdfDictionary>(w =>
        {
            var mk = new PdfDictionary(d);
            mk.Elements["/BG"] = Arr(d, 1, 0, 0);
            mk.Elements["/BC"] = Arr(d, 0, 0, 1);
            w.Elements["/MK"] = mk;
        });
        var plain = FieldChrome.Read(Widget(w => { }));
        Check("chrome: no /MK means no frame and the old 2pt inset", plain.BorderWidth == 0 && plain.Background is null && plain.TextInset == 2, "");

        var bs = FieldChrome.Read(Widget(w =>
        {
            mkRedBlue(w);
            var b = new PdfDictionary(d);
            b.Elements["/W"] = new PdfInteger(3);
            b.Elements["/S"] = new PdfName("/D");
            w.Elements["/BS"] = b;
        }));
        Check("chrome: /BS width and dashed style", bs.BorderWidth == 3 && bs.Style == FieldBorderStyle.Dashed, $"{bs.BorderWidth} {bs.Style}");
        Check("chrome: text inset is border width + 2", bs.TextInset == 5, $"{bs.TextInset}");

        var dflt = FieldChrome.Read(Widget(mkRedBlue));
        Check("chrome: default width is 1, solid", dflt.BorderWidth == 1 && dflt.Style == FieldBorderStyle.Solid, "");

        var legacy = FieldChrome.Read(Widget(w =>
        {
            mkRedBlue(w);
            w.Elements["/Border"] = Arr(d, 0, 0, 2);
        }));
        Check("chrome: /Border width is honoured", legacy.BorderWidth == 2, $"{legacy.BorderWidth}");

        var under = FieldChrome.Read(Widget(w =>
        {
            mkRedBlue(w);
            var b = new PdfDictionary(d);
            b.Elements["/S"] = new PdfName("/U");
            w.Elements["/BS"] = b;
        }));
        Check("chrome: underline draws a line, not a rectangle",
              under.Style == FieldBorderStyle.Underline && under.ToContent(100, 20).Contains("0 0.5 m 100 0.5 l S"), under.ToContent(100, 20));

        var bev = FieldChrome.Read(Widget(w =>
        {
            mkRedBlue(w);
            var b = new PdfDictionary(d);
            b.Elements["/S"] = new PdfName("/B");
            w.Elements["/BS"] = b;
        }));
        Check("chrome: beveled border doubles the inset", bev.TextInset == 4, $"{bev.TextInset}");

        var rot = FieldChrome.Read(Widget(w =>
        {
            var mk = new PdfDictionary(d);
            mk.Elements["/R"] = new PdfInteger(450);
            w.Elements["/MK"] = mk;
        }));
        Check("chrome: /MK /R is normalised", rot.Rotation == 90, $"{rot.Rotation}");

        // Alignment.
        Check("align: left starts at the inset", FieldChrome.AlignedX(0, 200, 50, 3) == 3, "");
        Check("align: centre", FieldChrome.AlignedX(1, 200, 50, 3) == 75, "");
        Check("align: right ends at the inset", FieldChrome.AlignedX(2, 200, 50, 3) == 147, "");
        Check("align: text wider than the box stays at the inset", FieldChrome.AlignedX(2, 40, 90, 3) == 3, "");

        // The generated stream really paints: build an appearance from the chrome alone and render it
        // through the shipping renderer.
        foreach (var variant in new[] { "listed" })
        {
            var doc = new PdfDocument();
            var pg = doc.AddPage();
            var wd = new PdfDictionary(doc);
            mkRedBlue(wd);
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
            if (true)
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
            Check($"chrome stream ({variant}): interior is the /BG red", inside.r > 200 && inside.g < 60 && inside.b < 60, $"{inside}");
            Check($"chrome stream ({variant}): edge is the /BC blue", edge.b > 200 && edge.r < 60 && edge.g < 60, $"{edge}");
        }
    }
}
