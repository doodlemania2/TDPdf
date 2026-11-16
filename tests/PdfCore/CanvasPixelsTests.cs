using TDPdf.Services;

/// <summary>
/// Pins <see cref="CanvasPixels.Region"/> (#220): a rectangle on the page canvas, in DIPs, must cut
/// the same part of the page out of the bitmap whatever DPI the page was rendered at.
/// </summary>
internal static class CanvasPixelsTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        // 1:1: the old assumption, still right when the render DPI is 96.
        var same = CanvasPixels.Region(10, 20, 30, 40, 1187, 1536, 1187, 1536);
        Check("canvas pixels: 1:1 bitmap is unchanged", same == (10, 20, 30, 40), $"{same}");

        // 8/7, as at the default fit-width zoom: a bitmap of 1357 x 1755 px shown at 1187 x 1536 DIPs.
        var r = CanvasPixels.Region(70, 140, 350, 70, 1357, 1755, 1187, 1536);
        Check("canvas pixels: 8/7 bitmap scales the region",
              r is { } a && a.X == 80 && a.Y == 159 && a.W == 401 && a.H == 81, $"{r}");

        // 200% display: twice the pixels.
        var d = CanvasPixels.Region(100, 50, 10, 10, 2374, 3072, 1187, 1536);
        Check("canvas pixels: 2x bitmap doubles the region", d == (200, 100, 20, 20), $"{d}");

        // The same page fraction lands on the same page fraction at any DPI.
        foreach (int k in new[] { 1, 2, 3 })
        {
            var q = CanvasPixels.Region(1187 / 2.0, 1536 / 4.0, 1, 1, 1187 * k, 1536 * k, 1187, 1536);
            Check($"canvas pixels: centre-x quarter-y at {k}x", q is { } b && b.X == 1187 * k / 2 && b.Y == 1536 * k / 4, $"{q}");
        }

        // Clamped to the bitmap, rounded outwards.
        var edge = CanvasPixels.Region(-5, -5, 20.2, 20.2, 100, 100, 100, 100);
        Check("canvas pixels: clamped at the top-left, rounded out", edge == (0, 0, 16, 16), $"{edge}");
        var over = CanvasPixels.Region(90, 90, 50, 50, 100, 100, 100, 100);
        Check("canvas pixels: clamped at the bottom-right", over == (90, 90, 10, 10), $"{over}");

        // Nothing on the bitmap, or nonsense input: no region.
        Check("canvas pixels: off the bitmap is null", CanvasPixels.Region(200, 200, 5, 5, 100, 100, 100, 100) is null, "");
        Check("canvas pixels: empty is null", CanvasPixels.Region(10, 10, 0, 5, 100, 100, 100, 100) is null, "");
        Check("canvas pixels: NaN is null", CanvasPixels.Region(double.NaN, 0, 5, 5, 100, 100, 100, 100) is null, "");
        Check("canvas pixels: zero DIP size is null", CanvasPixels.Region(0, 0, 5, 5, 100, 100, 0, 100) is null, "");
    }
}
