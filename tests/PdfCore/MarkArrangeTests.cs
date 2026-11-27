using TDPdf.Services;

/// <summary>
/// Pins <see cref="MarkArrange"/>: where a pasted copy lands (#226), and how selected marks line up
/// (#248). Pure geometry, no WPF.
/// </summary>
internal static class MarkArrangeTests
{
    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

    public static void Run(Action<string, bool, string> Check)
    {
        // ── Paste offset ────────────────────────────────────────────────────────────────
        var first = MarkArrange.PasteShift((100, 100, 50, 20), 600, 800, 1);
        Check("paste: first paste is 12 DIP down and right", first == (12, 12), $"{first}");

        var third = MarkArrange.PasteShift((100, 100, 50, 20), 600, 800, 3);
        Check("paste: each repeat adds another 12", third == (36, 36), $"{third}");

        var right = MarkArrange.PasteShift((540, 100, 50, 20), 600, 800, 1);
        Check("paste: clamped at the right edge, still offset down", Near(right.Dx, 10) && Near(right.Dy, 12), $"{right}");

        var corner = MarkArrange.PasteShift((550, 780, 50, 20), 600, 800, 1);
        Check("paste: a mark already in the corner stays on the page", corner == (0, 0), $"{corner}");

        var left = MarkArrange.PasteShift((-30, -5, 50, 20), 600, 800, 1);
        Check("paste: a mark hanging off the left is pulled back on", Near(left.Dx, 30) && Near(left.Dy, 12), $"{left}");

        var huge = MarkArrange.PasteShift((10, 10, 900, 900), 600, 800, 1);
        Check("paste: a group bigger than the page pins to the top-left", Near(huge.Dx, -10) && Near(huge.Dy, -10), $"{huge}");

        var nan = MarkArrange.PasteShift((double.NaN, 0, 5, 5), 600, 800, 1);
        Check("paste: nonsense input shifts nothing", nan == (0, 0), $"{nan}");

        var step0 = MarkArrange.PasteShift((10, 10, 5, 5), 600, 800, 0);
        Check("paste: step 0 shifts nothing", step0 == (0, 0), $"{step0}");
    }
}
