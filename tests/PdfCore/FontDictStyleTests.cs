using PdfSharpCore.Pdf;
using TDPdf.Services;

/// <summary>
/// Generic font names ("CIDFont+F2", "TT1") say nothing about the face, so the font dictionary
/// decides: descriptor name, weight, ForceBold, Italic, /ItalicAngle, /FontFamily, Type0 descendants.
/// </summary>
internal static class FontDictStyleTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nFont dictionary styling (generic names)");
        var d = new PdfDocument();
        d.AddPage();

        PdfDictionary Descriptor(string? name, int weight = 0, int flags = 0, double angle = 0, string? family = null)
        {
            var fd = new PdfDictionary(d);
            if (name is not null) fd.Elements["/FontName"] = new PdfName("/" + name);
            if (weight != 0) fd.Elements["/FontWeight"] = new PdfInteger(weight);
            fd.Elements["/Flags"] = new PdfInteger(flags);
            fd.Elements["/ItalicAngle"] = new PdfReal(angle);
            if (family is not null) fd.Elements["/FontFamily"] = new PdfString(family);
            return fd;
        }
        PdfDictionary Simple(string baseFont, PdfDictionary fd)
        {
            var f = new PdfDictionary(d);
            f.Elements["/Type"] = new PdfName("/Font");
            f.Elements["/Subtype"] = new PdfName("/TrueType");
            f.Elements["/BaseFont"] = new PdfName("/" + baseFont);
            f.Elements["/FontDescriptor"] = fd;
            return f;
        }

        Check("generic: F2", PdfFontStyle.IsGenericName("F2"), "");
        Check("generic: /F1", PdfFontStyle.IsGenericName("/F1"), "");
        Check("generic: CIDFont+F2", PdfFontStyle.IsGenericName("CIDFont+F2"), "");
        Check("generic: TT1", PdfFontStyle.IsGenericName("TT1"), "");
        Check("generic: ABCDEF+C2_0", PdfFontStyle.IsGenericName("ABCDEF+C2_0"), "");
        Check("not generic: Arial", !PdfFontStyle.IsGenericName("Arial"), "");
        Check("not generic: ABCDEF+Arial-BoldMT", !PdfFontStyle.IsGenericName("ABCDEF+Arial-BoldMT"), "");
        Check("not generic: Calibri", !PdfFontStyle.IsGenericName("Calibri"), "");

        // Descriptive letter name: the dictionary is ignored, as before.
        var arialBold = PdfFontStyle.ReadFontDict(Simple("X", Descriptor("Verdana", 400)));
        var s = PdfFontStyle.FromPdfName("Arial-BoldMT", arialBold);
        Check("descriptive name wins over the dictionary", s == new DetectedPdfFontStyle("Arial", true, false), $"{s}");

        // Generic name, real descriptor name.
        var info = PdfFontStyle.ReadFontDict(Simple("CIDFont+F2", Descriptor("ABCDEF+Calibri-Bold", 700, 32)));
        s = PdfFontStyle.FromPdfName("CIDFont+F2", info);
        Check("generic name takes the descriptor name", s.Family == "Calibri" && s.Bold && !s.Italic, $"{s}");

        // Generic everywhere: weight and flags still style it.
        info = PdfFontStyle.ReadFontDict(Simple("TT1", Descriptor("TT1", 700)));
        s = PdfFontStyle.FromPdfName("TT1", info);
        Check("weight >= 600 is bold", s.Bold && s.Family == PdfFontStyle.DefaultFamily, $"{s}");
        info = PdfFontStyle.ReadFontDict(Simple("TT1", Descriptor("TT1", 500)));
        Check("weight 500 is not bold", !PdfFontStyle.FromPdfName("TT1", info).Bold, "");
        info = PdfFontStyle.ReadFontDict(Simple("F1", Descriptor("F1", 0, 1 << 18)));
        Check("ForceBold flag is bold", PdfFontStyle.FromPdfName("F1", info).Bold, "");
        info = PdfFontStyle.ReadFontDict(Simple("F1", Descriptor("F1", 0, 1 << 6)));
        Check("Italic flag is italic", PdfFontStyle.FromPdfName("F1", info).Italic, "");
        info = PdfFontStyle.ReadFontDict(Simple("F1", Descriptor("F1", 0, 0, -12)));
        Check("a non-zero /ItalicAngle is italic", PdfFontStyle.FromPdfName("F1", info).Italic, "");
        info = PdfFontStyle.ReadFontDict(Simple("F1", Descriptor("F1", 400, 32, 0, "Georgia")));
        s = PdfFontStyle.FromPdfName("F1", info);
        Check("/FontFamily supplies the family", s.Family == "Georgia" && !s.Bold && !s.Italic, $"{s}");

        // Type0: the descendant carries the descriptor.
        var desc = Simple("ABCDEF+Cambria-BoldItalic", Descriptor("ABCDEF+Cambria-BoldItalic", 700, 96));
        desc.Elements["/Subtype"] = new PdfName("/CIDFontType2");
        var kids = new PdfArray(d);
        kids.Elements.Add(desc);
        var type0 = new PdfDictionary(d);
        type0.Elements["/Subtype"] = new PdfName("/Type0");
        type0.Elements["/BaseFont"] = new PdfName("/CIDFont+F2");
        type0.Elements["/DescendantFonts"] = kids;
        s = PdfFontStyle.FromPdfName("CIDFont+F2", PdfFontStyle.ReadFontDict(type0));
        Check("Type0 reads the descendant's descriptor", s.Family == "Cambria" && s.Bold && s.Italic, $"{s}");

        // Page lookup, including a resource inherited from the page tree and the subset-stripped key.
        var fonts = new PdfDictionary(d);
        fonts.Elements["/F2"] = type0;
        var res = new PdfDictionary(d);
        res.Elements["/Font"] = fonts;
        d.Pages[0].Elements["/Resources"] = res;
        var map = PdfFontStyle.ReadPageFonts(d.Pages[0]);
        Check("page fonts are keyed by resource name and BaseFont",
              map.ContainsKey("/F2") && map.ContainsKey("CIDFont+F2") && map.ContainsKey("F2"), string.Join(",", map.Keys));

        Check("no dictionary: a generic name keeps the old result",
              PdfFontStyle.FromPdfName("F2", null) == PdfFontStyle.FromPdfName("F2"), "");
    }
}
