using TDPdf.Services;

/// <summary>
/// Pins <see cref="WindowFitMath"/>: the main window opening inside a small or heavily scaled
/// monitor's work area, and the WM_GETMINMAXINFO minimum track size that never exceeds it.
/// </summary>
internal static class WindowFit
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("Main window fits its monitor (WindowFitMath)");

        // 1920 x 1080 at 150%: 1280 x 720 DIPs, minus a 48 px (32 DIP) taskbar = 1280 x 688.
        var f = WindowFitMath.FitCentered(1000, 700, 0, 0, 1280, 688, 640, 420);
        Check("a 700 DIP window on a 688 DIP work area is shrunk to 90% of it",
              f.Height == Math.Floor(688 * 0.9), $"{f.Height}");
        Check("its width, which fits, is left alone", f.Width == 1000, $"{f.Width}");
        Check("it is re-centered on the work area",
              f.Top == (688 - f.Height) / 2 && f.Left == (1280 - 1000) / 2.0, $"{f.Left},{f.Top}");
        Check("the whole window is inside the work area",
              f.Left >= 0 && f.Top >= 0 && f.Left + f.Width <= 1280 && f.Top + f.Height <= 688, "");

        var big = WindowFitMath.FitCentered(1000, 700, 0, 0, 2560, 1400, 640, 420);
        Check("a window that already fits keeps its size", big.Width == 1000 && big.Height == 700, "");

        var offset = WindowFitMath.FitCentered(1000, 700, 1920, -200, 800, 560, 640, 420);
        Check("a secondary monitor's origin is honoured",
              offset.Left >= 1920 && offset.Top >= -200 && offset.Left + offset.Width <= 2720 && offset.Top + offset.Height <= 360,
              $"{offset}");
        Check("each side shrinks to 90% of the work area", offset.Width == 720 && offset.Height == 504, $"{offset}");

        var snug = WindowFitMath.FitCentered(1000, 700, 0, 0, 680, 440, 640, 420);
        Check("shrinking stops at the minimum size, margin or not", snug.Width == 640 && snug.Height == 420, $"{snug}");

        var tiny = WindowFitMath.FitCentered(1000, 700, 0, 0, 600, 400, 640, 420);
        Check("on a work area smaller than the minimum, the minimum gives way",
              tiny.Width <= 600 && tiny.Height <= 400, $"{tiny}");

        // MinTrackPixels: DIP minimum scaled to device pixels, capped at the work area.
        Check("640 DIPs at 150% is 960 px", WindowFitMath.MinTrackPixels(640, 1.5, 1920, 0) == 960, "");
        Check("the minimum never exceeds the work area",
              WindowFitMath.MinTrackPixels(420, 2.5, 1000, 0) == 1000, "");
        Check("a larger system minimum is kept", WindowFitMath.MinTrackPixels(100, 1.0, 1920, 136) == 136, "");
        Check("no minimum leaves Windows' value alone",
              WindowFitMath.MinTrackPixels(0, 1.5, 1920, 136) == 136
              && WindowFitMath.MinTrackPixels(double.PositiveInfinity, 1.5, 1920, 136) == 136
              && WindowFitMath.MinTrackPixels(640, 0, 1920, 136) == 136, "");
    }
}
