using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TDPdf.Services;

/// <summary>
/// Save Flattened keeps each page's own resolution (upstream #462): the DPI choice and the
/// PdfPig reader that feeds it.
/// </summary>
internal static class FlattenDpi
{
    private static FlattenResolution.PlacedImage Img(int w, int h, double ptW, double ptH) => new(w, h, ptW, ptH);

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nFlatten resolution (FlattenResolution)");

        // Letter, 8.5 x 11 in.
        const double W = 612, H = 792;
        Check("a page with no images stays at 150", FlattenResolution.ChooseDpi(W, H, []) == 150, "");
        Check("a 300 dpi scan flattens at 300",
              FlattenResolution.ChooseDpi(W, H, [Img(2550, 3300, W, H)]) == 300, "");
        Check("an image below 150 dpi never lowers the page below 150",
              FlattenResolution.ChooseDpi(W, H, [Img(816, 1056, W, H)]) == 150, "");
        Check("the ceiling is 600",
              FlattenResolution.ChooseDpi(W, H, [Img(9000, 12000, W, H)]) == 600, "");
        Check("the largest image decides, not the first",
              FlattenResolution.ChooseDpi(W, H, [Img(100, 100, 72, 72), Img(2550, 3300, W, H)]) == 300, "");
        Check("a degenerate image box is ignored",
              FlattenResolution.ChooseDpi(W, H, [Img(5000, 5000, 0, 0)]) == 150, "");
        Check("a turned (swapped-axis) image reads the same",
              FlattenResolution.ChooseDpi(W, H, [Img(3300, 2550, W, H)]) == 300, "");

        // A 40 x 40 in sheet at 600 dpi would be 576 MP; the pixel cap pulls it down.
        double big = 40 * 72;
        double dpi = FlattenResolution.ChooseDpi(big, big, [Img(24000, 24000, big, big)]);
        double px = big * dpi / 72 * (big * dpi / 72);
        Check("a huge page is capped to the pixel budget", px <= FlattenResolution.MaxPagePixels && dpi < 600, $"{dpi} dpi, {px:N0} px");
        Check("and the cap is not needlessly low", dpi >= 150 || px > FlattenResolution.MaxPagePixels * 0.9, $"{dpi}");
        // A full 600 dpi Letter page is 33.7 MP, inside the budget.
        Check("a 600 dpi Letter page is not capped",
              FlattenResolution.ChooseDpi(W, H, [Img(5100, 6600, W, H)]) == 600, "");

        // The PdfPig reader against a real file: page 0 is a 300 dpi image, page 1 has none.
        string path = Path.Combine(tmp, "flatten-dpi.pdf");
        {
            var photo = new byte[40 * 40 * 4];
            for (int i = 0; i < photo.Length; i++) photo[i] = (byte)(i * 7);
            string png = Path.Combine(tmp, "flatten-dpi.png");
            using (var im = Image.LoadPixelData<Bgra32>(photo, 40, 40)) im.SaveAsPng(png);

            var doc = new PdfDocument();
            var p0 = doc.AddPage();
            p0.Width = 144; p0.Height = 144;          
            using (var gfx = XGraphics.FromPdfPage(p0))
                gfx.DrawImage(XImage.FromFile(png), 0, 0, 12, 12);   // 40 px over 12 pt = 240 dpi
            var p1 = doc.AddPage();
            p1.Width = 144; p1.Height = 144;
            doc.Save(path);
        }
        double[] dpis = FlattenResolution.ReadPageDpis(path, [(144, 144), (144, 144)]);
        Console.WriteLine($"        dpis: [{string.Join(", ", dpis)}]");
        Check("the reader finds the placed image's effective resolution", dpis[0] == 240, $"{dpis[0]}");
        Check("a page without images gets 150", dpis[1] == 150, $"{dpis[1]}");
        Check("a file PdfPig cannot open gives 150 for every page",
              FlattenResolution.ReadPageDpis(Path.Combine(tmp, "flatten-dpi.png"), [(144, 144), (144, 144)]) is [150, 150], "");
        // #234: the options step.
        var sizes = new (double, double)[] { (612, 792), (1224, 792), (300, 300) };
        Check("Automatic choice returns the automatic dpi", FlattenResolution.ForChoice(0, 240, 612, 792) == 240, "");
        Check("a fixed choice is used as asked", FlattenResolution.ForChoice(300, 150, 612, 792) == 300, "");
        Check("a fixed choice is lowered on a huge page", FlattenResolution.ForChoice(300, 150, 7200, 7200) < 300, "");
        Check("pixel size at 150 dpi for Letter", FlattenResolution.PixelSize(612, 792, 150) == (1275, 1650), "");
        Check("pixel size at 72 dpi is the point size", FlattenResolution.PixelSize(612, 792, 72) == (612, 792), "");
        Check("pixel size is never below 1", FlattenResolution.PixelSize(0.1, 0.1, 72) == (1, 1), "");
        Check("scale percent", FlattenResolution.ScalePercent(150) == 208 && FlattenResolution.ScalePercent(72) == 100, "");
        Check("the largest page is found by area", FlattenResolution.LargestPage(sizes) == 1, "");
        Check("no pages has no largest", FlattenResolution.LargestPage([]) == -1, "");
        Check("choices normalise", FlattenResolution.NormaliseChoice(200) == 200 && FlattenResolution.NormaliseChoice(123) == 0
              && FlattenResolution.NormaliseChoice(0) == 0, "");
        double[] fixedDpis = FlattenResolution.ReadPageDpis(path, [(144, 144), (144, 144)], 200);
        Check("a fixed choice overrides the image-derived dpi on every page", fixedDpis is [200, 200], $"{string.Join(",", fixedDpis)}");
        double[] onlyDpis = FlattenResolution.ReadPageDpis(path, [(144, 144), (144, 144)], 0, new HashSet<int> { 1 });
        Check("a restricted read leaves skipped pages at the default", onlyDpis is [150, 150], $"{string.Join(",", onlyDpis)}");

    }
}
