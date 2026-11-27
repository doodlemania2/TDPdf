using TDPdf.Services;

/// <summary>
/// The raster box the print window renders each page at: 300 DPI of the size the page has on the sheet,
/// lowered when a poster-size page would exceed the render limits (it used to overflow and print blank).
/// </summary>
internal static class PrintRaster
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nPrint: raster box and render limits");

        // Letter, 8.5 x 11 in = 816 x 1056 DIPs, at 300 DPI.
        var letter = PrintRasterBox.For(816, 1056, 300);
        Check("letter at 300 DPI is 2550 x 3300", letter.Width == 2550 && letter.Height == 3300 && !letter.Capped,
              $"{letter.Width}x{letter.Height}");
        Check("a normal page keeps the requested density", Math.Abs(letter.Dpi - 300) < 1e-9, "");

        // The box follows the PRINTED size: a letter page shown at half size needs a quarter of the pixels.
        var half = PrintRasterBox.For(408, 528, 300);
        Check("half-size print renders half the pixels per edge", half.Width == 1275 && half.Height == 1650, $"{half.Width}x{half.Height}");

        // A 100 x 100 in poster: 30000 px a side at 300 DPI, far past the limits.
        var poster = PrintRasterBox.For(9600, 9600, 300);
        Check("poster page is capped", poster.Capped, "");
        Check("poster page fits the limits", PrintRasterBox.WithinRenderLimit(poster.Width, poster.Height), $"{poster.Width}x{poster.Height}");
        Check("poster page lowers the density", poster.Dpi < 300 && poster.Dpi > 0, poster.Dpi.ToString("0.0"));
        Check("poster page stays square", poster.Width == poster.Height, "");

        // A long thin banner is limited by its edge, not by the pixel count.
        var banner = PrintRasterBox.For(96 * 120, 96 * 2, 300);   // 120 x 2 in
        Check("banner longest edge is within the limit", banner.Width <= PrintRasterBox.MaxEdge && banner.Capped, $"{banner.Width}x{banner.Height}");
        Check("banner keeps its aspect", Math.Abs((double)banner.Width / banner.Height - 60) < 0.5, $"{banner.Width}x{banner.Height}");

        // Within the edge limit but over the pixel budget: 14000 x 14000 = 196 MP.
        var big = PrintRasterBox.For(96 * 46.0, 96 * 46.0, 300);  // 46 in = 13800 px
        Check("pixel budget lowers a big square page", big.Capped && (long)big.Width * big.Height <= PrintRasterBox.MaxPixels, $"{big.Width}x{big.Height}");

        Check("limits: exactly at the edge limit passes", PrintRasterBox.WithinRenderLimit(16384, 6103), "");
        Check("limits: one past the edge fails", !PrintRasterBox.WithinRenderLimit(16385, 10), "");
        Check("limits: over the pixel budget fails", !PrintRasterBox.WithinRenderLimit(12000, 12000), "");
        Check("limits: zero and negative fail", !PrintRasterBox.WithinRenderLimit(0, 10) && !PrintRasterBox.WithinRenderLimit(10, -1), "");

        var junk = PrintRasterBox.For(0, 0, 300);
        Check("a zero-size page gives a 1 px box, not a crash", junk.Width == 1 && junk.Height == 1, "");
        var tiny = PrintRasterBox.For(0.5, 0.5, 300);
        Check("a tiny page never rounds to 0 px", tiny.Width >= 1 && tiny.Height >= 1, "");
    }
}
