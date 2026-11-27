using System.Globalization;
using System.Windows;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using TDPdf;
using TDPdf.Services;

/// <summary>
/// Stamps (3.0): the Stamp tool's number rules, and the real /Stamp annotation the baker writes.
/// The placement checks render the saved file with PDFium and compare the ink's bounding box with the
/// box the canvas showed, on every combination of stamp turn and page /Rotate.
/// </summary>
internal static class StampTests
{
    private const double PageW = 600, PageH = 800;
    private static readonly AnnotationTextLayout Layout = new(ta => (100, 28), (ta, w) => ta.Content.Split(' ').ToList());

    // 1x1 PNG, valid and decodable.
    private const string Png1x1 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("== Stamps ==");
        Sizes(Check);
        Settings(Check);
        Factory(Check);
        Written(Check, tmp);
        Placement(Check, tmp);
    }

    private static void Sizes(Action<string, bool, string> Check)
    {
        bool Size(string? text, double expect) =>
            StampSettings.TryParseSize(text, out double v) && Math.Abs(v - expect) < 1e-9;

        Check("size: a dot decimal is read", Size("32.5", 32.5), "");
        Check("size: a comma decimal is read the same", Size("32,5", 32.5), "");
        Check("size: both separators, last one is the decimal ('1.234,5' clamps to the maximum)", Size("1.234,5", StampSettings.MaxSize), "");
        Check("size: both separators the other way round ('1,234.5')", Size("1,234.5", StampSettings.MaxSize), "");
        Check("size: below the slider clamps to its minimum", Size("5", StampSettings.MinSize) && Size("-20", StampSettings.MinSize) && Size("0", StampSettings.MinSize), "");
        Check("size: above the slider clamps to its maximum", Size("9999", StampSettings.MaxSize) && Size("120.01", StampSettings.MaxSize), "");
        Check("size: the ends themselves are kept", Size("12", 12) && Size("120", 120), "");
        Check("size: surrounding space and a unit are ignored", Size("  40 px ", 40) && Size("24pt", 24), "");
        Check("size: text that is not a number is refused", !StampSettings.TryParseSize("big", out _) && !StampSettings.TryParseSize("", out _)
              && !StampSettings.TryParseSize(null, out _) && !StampSettings.TryParseSize("1,2,3", out _) && !StampSettings.TryParseSize("NaN", out _)
              && !StampSettings.TryParseSize("1e999", out _), "");
        Check("size: a refused entry leaves the default, not zero", !StampSettings.TryParseSize("x", out double d) && d == StampSettings.DefaultSize, d.ToString(CultureInfo.InvariantCulture));
        Check("size: parsing does not depend on the machine's culture", WithCulture("de-DE", () => Size("32,5", 32.5) && Size("32.5", 32.5)), "");

        Check("opacity: percent clamps to 10..100 and reads either separator",
              StampSettings.TryParseOpacityPercent("55,5", out double a) && Math.Abs(a - 55.5) < 1e-9
              && StampSettings.TryParseOpacityPercent("3%", out double b) && b == 10
              && StampSettings.TryParseOpacityPercent("250", out double c) && c == 100, "");
        Check("opacity: a fraction is kept inside the slider's range", StampSettings.ClampOpacity(0) == 0.1 && StampSettings.ClampOpacity(5) == 1 && StampSettings.ClampOpacity(0.4) == 0.4, "");
    }

    private static bool WithCulture(string name, Func<bool> body)
    {
        var old = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = new CultureInfo(name); return body(); }
        finally { CultureInfo.CurrentCulture = old; }
    }

    private static void Settings(Action<string, bool, string> Check)
    {
        var s = new StampSettings
        {
            Choice = StampSettings.ImageChoice, Size = 99, OpacityPercent = 30,
            CustomText = "Hello", CustomR = 1, CustomG = 2, CustomB = 3,
            ImageData = "abc", ImagePixelWidth = 10, ImagePixelHeight = 20, ImageName = "x.png"
        };
        s.Reset();
        var d = new StampSettings();
        Check("reset: every setting returns to its default",
              s.Choice == d.Choice && s.Size == d.Size && s.OpacityPercent == d.OpacityPercent && s.CustomText == d.CustomText
              && s.CustomR == d.CustomR && s.CustomG == d.CustomG && s.CustomB == d.CustomB
              && s.ImageData is null && s.ImagePixelWidth == 0 && s.ImagePixelHeight == 0 && s.ImageName == "", "");
        Check("defaults sit inside their sliders", d.Size is >= StampSettings.MinSize and <= StampSettings.MaxSize
              && d.OpacityPercent is >= StampSettings.MinOpacityPercent and <= StampSettings.MaxOpacityPercent, "");
    }

    private static void Factory(Action<string, bool, string> Check)
    {
        Check("eight standard stamps, each with its own id and a fixed colour",
              StampLibrary.Standard.Count == 8 && StampLibrary.Standard.Select(x => x.Id).Distinct().Count() == 8
              && new[] { "Approved", "Draft", "Confidential", "Final", "Not Approved", "Received", "Reviewed", "For Comment" }
                  .All(l => StampLibrary.Standard.Any(x => x.Label == l)), "");
        var def = StampLibrary.Find("Approved")!;
        var st = StampLibrary.Create(def, new Point(300, 400), 2, 36, 1);
        double w = st.SourceWidth * st.Scale, h = st.SourceHeight * st.Scale;
        Check("a stamp is centred on the click, on its page, and its words are upper case",
              Math.Abs(st.Position.X + w / 2 - 300) < 1e-9 && Math.Abs(st.Position.Y + h / 2 - 400) < 1e-9
              && st.PageIndex == 2 && st.Label == "APPROVED", "");
        Check("size is the text size: Scale is size over the design font", Math.Abs(st.Scale - 36.0 / StampLibrary.DesignFont) < 1e-9, "");
        var big = StampLibrary.Create(def, new Point(0, 0), 2, 5000, 7);
        Check("a stamp made with an out-of-range size and opacity is clamped", Math.Abs(big.Scale - StampSettings.MaxSize / StampLibrary.DesignFont) < 1e-9 && big.Opacity == 1, "");
        Check("a longer text makes a wider stamp", StampLibrary.DesignWidth("NOT APPROVED") > StampLibrary.DesignWidth("DRAFT"), "");
        Check("custom text: empty text makes no stamp, text is upper-cased on one line",
              StampLibrary.CreateCustom("  ", 1, 2, 3, new Point(1, 1), 0, 36, 1) is null
              && StampLibrary.CreateCustom("hold\nfor review", 1, 2, 3, new Point(1, 1), 0, 36, 1)?.Label == "HOLD FOR REVIEW", "");
        var img = StampLibrary.CreateImage(Png1x1, 200, 100, new Point(50, 50), 0, 36, 0.5);
        Check("a picture stamp keeps the picture's shape", img is not null && Math.Abs(img.SourceWidth / img.SourceHeight - 2) < 1e-9 && img.Opacity == 0.5, "");
        Check("a picture with no size makes no stamp", StampLibrary.CreateImage(Png1x1, 0, 0, new Point(1, 1), 0, 36, 1) is null, "");
        var clone = (StampAnnotation)st.Clone();
        st.GroupId = "g";
        Check("clone copies every field (undo and copy/paste rely on it)",
              clone.Kind == st.Kind && clone.StampId == st.StampId && clone.Label == st.Label && clone.Scale == st.Scale
              && clone.Opacity == st.Opacity && clone.ColorR == st.ColorR && clone.Position.X == st.Position.X && clone.GroupId is null
              && ((StampAnnotation)st.Clone()).GroupId == "g", "");
    }

    // ---- baking --------------------------------------------------------------------------------

    private static (string Path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode Mode) Save(string tmp, string name, int pageRotate,
        IReadOnlyList<PageAnnotation> annots)
    {
        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = PageW; page.Height = PageH;
        if (pageRotate != 0) page.Rotate = pageRotate;
        int fw = pageRotate is 90 or 270 ? (int)PageH : (int)PageW, fh = pageRotate is 90 or 270 ? (int)PageW : (int)PageH;
        AnnotationBaker.Bake(doc, new Dictionary<int, List<PageAnnotation>> { [0] = annots.ToList() }, _ => (fw, fh), Layout);
        string path = Path.Combine(tmp, name + ".pdf");
        doc.Save(path);
        return (path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);
    }

    private static PdfDictionary? FirstStamp(PdfDocument doc)
    {
        var annots = doc.Pages[0].Elements.GetArray("/Annots");
        if (annots is null) return null;
        foreach (var item in annots.Elements)
        {
            var d = PdfItems.Deref(item) as PdfDictionary;
            if (d?.Elements.GetName("/Subtype") == "/Stamp") return d;
        }
        return null;
    }

    private static void Written(Action<string, bool, string> Check, string tmp)
    {
        var st = new StampAnnotation
        {
            Kind = StampKind.Standard, StampId = "Approved", Label = "APPROVED", ColorR = 30, ColorG = 138, ColorB = 60,
            Position = new Point(100, 100), Scale = 2, SourceWidth = 110, SourceHeight = 60
        };
        var (path, mode) = Save(tmp, "stamp-basic", 0, new PageAnnotation[] { st });
        using var re = PdfSharpCore.Pdf.IO.PdfReader.Open(path, mode);
        var ann = FirstStamp(re);
        Check("a stamp is saved as a real /Stamp annotation", ann is not null, "");
        if (ann is null) return;
        var rect = ann.Elements.GetArray("/Rect")!;
        double[] r = rect.Elements.Select(e => e is PdfReal x ? x.Value : ((PdfInteger)e).Value).ToArray();
        // 220 x 120 canvas px at (100,100) on an 800 pt tall page: y flips to 800-220..800-100.
        Check("its /Rect is the canvas box in PDF space", Near(r[0], 100) && Near(r[1], 580) && Near(r[2], 320) && Near(r[3], 700), string.Join(",", r));
        Check("it is printable and named for the standard stamp", ann.Elements.GetInteger("/F") == 4 && ann.Elements.GetName("/Name") == "/Approved"
              && ann.Elements.GetString("/Contents") == "APPROVED", "");
        var n = PdfItems.Deref((ann.Elements["/AP"] as PdfDictionary)?.Elements["/N"]) as PdfDictionary;
        Check("it has an appearance stream that draws something", n is not null && n.Stream is { Length: > 20 }, "");
        Check("the page content itself was not painted on (the stamp is not burned in)",
              re.Pages[0].Contents.Elements.Count == 0 || !System.Text.Encoding.Latin1.GetString(re.Pages[0].Contents.CreateSingleContent().Stream.UnfilteredValue).Contains(" re"), "");

        // Ink: green pixels, and none of them outside the box.
        var (ix, w, h) = Render(path, (int)PageW, (int)PageH);
        var box = InkBox(ix, w, h, p => p.G > p.R + 40 && p.G > p.B + 40);
        Check("PDFium draws the stamp, in its own colour, inside the box", box is { } b && b.L >= 99 && b.T >= 99 && b.R <= 321 && b.B <= 221 && b.R - b.L > 150, box is null ? "no ink" : box.ToString()!);

        // Opacity: a half-opaque stamp is paler.
        st.Opacity = 0.4;
        var (path2, _) = Save(tmp, "stamp-faint", 0, new PageAnnotation[] { st });
        var (ix2, _, _) = Render(path2, (int)PageW, (int)PageH);
        int fullMin = DarkestGreen(ix, w, h), faintMin = DarkestGreen(ix2, w, h);
        Check("opacity is baked into the appearance: a 40% stamp is paler than a full one", faintMin > fullMin + 40, $"{fullMin} vs {faintMin}");

        // A picture's opacity: the appearance paints the picture as a transparency group under an opacity state.
        var png = WatermarkTests.MakePng(40, 20, 220, 20, 20, 96);
        var picFaint = new StampAnnotation { Kind = StampKind.Image, ImageData = Convert.ToBase64String(png), Opacity = 0.4, Position = new Point(100, 100), Scale = 2, SourceWidth = 60, SourceHeight = 30 };
        var (pathP, _) = Save(tmp, "stamp-pic-faint", 0, new PageAnnotation[] { picFaint });
        using (var reP = PdfSharpCore.Pdf.IO.PdfReader.Open(pathP, mode))
        {
            var fm = PdfItems.Deref((FirstStamp(reP)!.Elements["/AP"] as PdfDictionary)!.Elements["/N"]) as PdfDictionary;
            string body = System.Text.Encoding.Latin1.GetString(fm!.Stream.UnfilteredValue);
            Check("a half-opaque picture stamp paints its picture as a group under an opacity state", body.Contains("/GSa gs") && body.Contains("/Fm0 Do"), body);
        }
        var (pxs, pw, ph) = Render(pathP, (int)PageW, (int)PageH);
        int pi = ((130 * (int)PageW) + 160) * 4;
        Check("picture stamp opacity: 40% red over white renders pale", pxs.Length > pi + 2 && pxs[pi + 2] > 200 && pxs[pi + 1] > 130 && pxs[pi + 1] < 190, pxs.Length > pi + 2 ? $"{pxs[pi + 2]},{pxs[pi + 1]},{pxs[pi]}" : "no render");

        // Picture and custom text stamps are real annotations too, with their own names.
        var pic = new StampAnnotation { Kind = StampKind.Image, ImageData = Png1x1, Position = new Point(10, 10), Scale = 1, SourceWidth = 120, SourceHeight = 60 };
        var custom = new StampAnnotation { Kind = StampKind.CustomText, Label = "HOLD", ColorR = 200, Position = new Point(10, 300), Scale = 1, SourceWidth = 130, SourceHeight = 60 };
        var broken = new StampAnnotation { Kind = StampKind.Image, ImageData = "not base64 !!", Position = new Point(10, 500), Scale = 1, SourceWidth = 120, SourceHeight = 60 };
        var (path3, _) = Save(tmp, "stamp-kinds", 0, new PageAnnotation[] { pic, custom, broken });
        using var re3 = PdfSharpCore.Pdf.IO.PdfReader.Open(path3, mode);
        var names = (re3.Pages[0].Elements.GetArray("/Annots")?.Elements.ToList() ?? new List<PdfItem>())
            .Select(i => (PdfItems.Deref(i) as PdfDictionary)?.Elements.GetName("/Name")).ToList();
        Check("a picture stamp and a custom stamp are annotations; one that will not decode is skipped, the save still works",
              names.Count == 2 && names.Contains("/TDPdfImage") && names.Contains("/TDPdfCustom"), string.Join(",", names));

        // Reopen and resave: the stamp is not duplicated or lost.
        string again = Path.Combine(tmp, "stamp-resaved.pdf");
        re.Save(again);
        using var re4 = PdfSharpCore.Pdf.IO.PdfReader.Open(again, mode);
        Check("a saved stamp survives a reopen and resave", FirstStamp(re4) is not null && re4.Pages[0].Elements.GetArray("/Annots")!.Elements.Count == 1, "");
    }

    private static bool Near(double a, double b, double eps = 0.01) => Math.Abs(a - b) <= eps;

    private static (byte[] Bgra, int W, int H) Render(string path, int w, int h) =>
        (PdfiumInterop.RenderPageWithAnnotations(path, 0, w, h) ?? Array.Empty<byte>(), w, h);

    private readonly record struct Px(int B, int G, int R);

    private static (int L, int T, int R, int B)? InkBox(byte[] bgra, int w, int h, Func<Px, bool> ink)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (i + 2 >= bgra.Length) return null;
                if (!ink(new Px(bgra[i], bgra[i + 1], bgra[i + 2]))) continue;
                l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y);
            }
        return r < 0 ? null : (l, t, r + 1, b + 1);
    }

    /// <summary>The smallest red channel among green-ish pixels: lower is a stronger green.</summary>
    private static int DarkestGreen(byte[] bgra, int w, int h)
    {
        int min = 255;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
            if (bgra[i + 1] < 250) min = Math.Min(min, bgra[i + 2]);
        return min;
    }

    private static void Placement(Action<string, bool, string> Check, string tmp)
    {
        // A 220x120 stamp at (100,100): turned about its centre it fills 120x220 at (150,50).
        foreach (int pageRot in new[] { 0, 90, 180, 270 })
            foreach (int stampRot in new[] { 0, 90, 180, 270 })
            {
                var st = new StampAnnotation
                {
                    Kind = StampKind.CustomText, Label = "MM", ColorR = 20, ColorG = 160, ColorB = 40, Rotation = stampRot,
                    Position = new Point(100, 100), Scale = 2, SourceWidth = 110, SourceHeight = 60
                };
                var (path, _) = Save(tmp, $"stamp-r{pageRot}-s{stampRot}", pageRot, new PageAnnotation[] { st });
                int fw = pageRot is 90 or 270 ? (int)PageH : (int)PageW, fh = pageRot is 90 or 270 ? (int)PageW : (int)PageH;
                var (bgra, w, h) = Render(path, fw, fh);
                var box = InkBox(bgra, w, h, p => p.G > p.R + 40 && p.G > p.B + 40);
                bool quarter = stampRot is 90 or 270;
                double el = quarter ? 150 : 100, et = quarter ? 50 : 100, er = quarter ? 270 : 320, eb = quarter ? 270 : 220;
                bool ok = box is { } b && Math.Abs(b.L - el) <= 3 && Math.Abs(b.T - et) <= 3 && Math.Abs(b.R - er) <= 3 && Math.Abs(b.B - eb) <= 3;
                Check($"placement: page /Rotate {pageRot}, stamp turned {stampRot}: the ink fills the box the canvas showed",
                      ok, box is null ? "no ink" : $"{box} expected {el},{et},{er},{eb}");
            }
    }
}
