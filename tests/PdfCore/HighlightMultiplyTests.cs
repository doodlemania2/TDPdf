using TDPdf.Services;

/// <summary>
/// Pins <see cref="HighlightMultiply"/>: the on-screen highlight is the multiply blend the saved
/// file uses, so dark text under it stays dark at any opacity.
/// </summary>
internal static class HighlightMultiplyTests
{
    private static byte[] Fill(int w, int h, byte b, byte g, byte r, byte a)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = a; }
        return px;
    }

    public static void Run(Action<string, bool, string> Check)
    {
        // Full-strength yellow on white paper: paper becomes yellow.
        var paper = Fill(4, 4, 255, 255, 255, 255);
        HighlightMultiply.Apply(paper, 4, 4, (0, 0, 4, 4), 255, 255, 0, 1.0);
        Check("highlight multiply: opaque yellow turns white paper yellow",
              paper[0] == 0 && paper[1] == 255 && paper[2] == 255 && paper[3] == 255, $"{paper[0]},{paper[1]},{paper[2]}");

        // Black text stays black however strong the highlight (plain alpha would wash it out).
        foreach (double op in new[] { 0.1, 0.5, 1.0 })
        {
            var ink = Fill(2, 2, 0, 0, 0, 255);
            HighlightMultiply.Apply(ink, 2, 2, (0, 0, 2, 2), 255, 255, 0, op);
            Check($"highlight multiply: black ink stays black at opacity {op}",
                  ink[0] == 0 && ink[1] == 0 && ink[2] == 0, $"{ink[0]},{ink[1]},{ink[2]}");
        }

        // Opacity is a lerp from 1 to the colour: 80/255 opacity of pure red on white.
        var half = Fill(1, 1, 255, 255, 255, 255);
        HighlightMultiply.Apply(half, 1, 1, (0, 0, 1, 1), 255, 0, 0, 0.5);
        Check("highlight multiply: half-opacity red leaves red at 255 and halves green and blue",
              half[2] == 255 && half[1] == 128 && half[0] == 128, $"{half[0]},{half[1]},{half[2]}");

        // Zero opacity changes nothing on an opaque page.
        var none = Fill(1, 1, 10, 20, 30, 255);
        HighlightMultiply.Apply(none, 1, 1, (0, 0, 1, 1), 255, 0, 0, 0);
        Check("highlight multiply: zero opacity is a no-op", none[0] == 10 && none[1] == 20 && none[2] == 30, "");

        // Only the region changes; the rest of the bitmap is untouched.
        var buf = Fill(4, 4, 200, 200, 200, 255);
        HighlightMultiply.Apply(buf, 4, 4, (1, 1, 2, 2), 255, 255, 0, 1.0);
        int inside = (1 * 4 + 1) * 4, outside = 0, edgeRight = (1 * 4 + 3) * 4, edgeBelow = (3 * 4 + 1) * 4;
        Check("highlight multiply: pixels inside the region change",
              buf[inside] == 0 && buf[inside + 1] == 200, $"{buf[inside]}");
        Check("highlight multiply: pixels outside the region do not",
              buf[outside] == 200 && buf[edgeRight] == 200 && buf[edgeBelow] == 200, "");

        // A transparent paper pixel is treated as white paper, and ends opaque.
        var clear = Fill(1, 1, 0, 0, 0, 0);
        HighlightMultiply.Apply(clear, 1, 1, (0, 0, 1, 1), 255, 255, 0, 1.0);
        Check("highlight multiply: transparent paper counts as white and ends opaque",
              clear[0] == 0 && clear[1] == 255 && clear[2] == 255 && clear[3] == 255, $"{clear[0]},{clear[1]},{clear[2]},{clear[3]}");

        // Two overlapping highlights stack (multiply is order-free).
        var two = Fill(1, 1, 255, 255, 255, 255);
        HighlightMultiply.Apply(two, 1, 1, (0, 0, 1, 1), 255, 255, 0, 1.0);
        HighlightMultiply.Apply(two, 1, 1, (0, 0, 1, 1), 0, 255, 255, 1.0);
        Check("highlight multiply: yellow over cyan leaves green",
              two[0] == 0 && two[1] == 255 && two[2] == 0, $"{two[0]},{two[1]},{two[2]}");

        // A region that runs off the bitmap is clamped, a bad buffer or opacity is harmless.
        var small = Fill(2, 2, 255, 255, 255, 255);
        HighlightMultiply.Apply(small, 2, 2, (-5, -5, 100, 100), 0, 0, 0, 1.0);
        Check("highlight multiply: an oversized region is clamped", small[0] == 0 && small[15] == 255 && small[12] == 0, "");
        HighlightMultiply.Apply(new byte[3], 2, 2, (0, 0, 2, 2), 0, 0, 0, 1.0);
        var nan = Fill(1, 1, 50, 50, 50, 255);
        HighlightMultiply.Apply(nan, 1, 1, (0, 0, 1, 1), 0, 0, 0, double.NaN);
        Check("highlight multiply: a short buffer and a NaN opacity change nothing", nan[0] == 50, "");

        // The region comes from CanvasPixels, so it follows the render DPI.
        var reg = CanvasPixels.Region(10, 10, 20, 10, 200, 100, 100, 50);
        var scaled = Fill(200, 100, 255, 255, 255, 255);
        if (reg is { } rg) HighlightMultiply.Apply(scaled, 200, 100, rg, 255, 255, 0, 1.0);
        int at(int x, int y) => (y * 200 + x) * 4;
        Check("highlight multiply: a 2x bitmap is highlighted over the DIP rectangle's pixels",
              scaled[at(20, 20)] == 0 && scaled[at(59, 39)] == 0 && scaled[at(19, 20)] == 255 && scaled[at(60, 20)] == 255, $"{reg}");
    }
}
