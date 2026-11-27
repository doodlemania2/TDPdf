using TDPdf.Services;
using static TDPdf.Services.WindowPlacementMath;

/// <summary>Pins <see cref="WindowPlacementMath"/>: restoring a saved window onto a visible monitor and cascading a torn-off tab's window.</summary>
internal static class WindowPlacement
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("Window placement (WindowPlacementMath)");

        var main = new Box(0, 0, 1920, 1040);
        var second = new Box(1920, 0, 1280, 688);
        var areas = new[] { main, second };

        var r = Restore(new Box(100, 80, 1000, 700), areas, 640, 420);
        Check("a saved window fully on a monitor is restored as saved", r == new Box(100, 80, 1000, 700), $"{r}");

        r = Restore(new Box(2000, 100, 1000, 700), areas, 640, 420);
        Check("a window saved on the second monitor stays there", r is Box b && b.X == 2000 && b.Y == 0 && b.Height == 688, $"{r}");

        r = Restore(new Box(5000, 100, 1000, 700), areas, 640, 420);
        Check("a rectangle off every monitor falls back (null)", r is null, $"{r}");

        r = Restore(new Box(1500, 100, 1000, 700), new[] { main }, 640, 420);
        Check("a window hanging mostly off the edge is slid back inside", r is Box c && c.X + c.Width <= 1920 && c.X >= 0, $"{r}");

        r = Restore(new Box(-950, 100, 1000, 700), new[] { main }, 640, 420);
        Check("a window with under 100 DIPs visible counts as off-screen", r is null, $"{r}");

        r = Restore(new Box(0, 0, 4000, 3000), new[] { second }, 640, 420);
        Check("an oversized window shrinks to the work area and stays inside",
              r is Box d && d.Width <= 1280 && d.Height <= 688 && d.X >= 1920 && d.Y >= 0, $"{r}");

        Check("NaN and zero sizes are rejected",
              Restore(new Box(double.NaN, 0, 800, 600), areas, 640, 420) is null
              && Restore(new Box(0, 0, 0, 600), areas, 640, 420) is null, "");

        var cas = Cascade(new Box(100, 100, 1000, 700), areas, 640, 420);
        Check("a torn-off window sits 32 DIPs down and right of its source", cas == new Box(132, 132, 1000, 700), $"{cas}");

        cas = Cascade(new Box(900, 340, 1000, 700), new[] { main }, 640, 420);
        Check("a cascade that would run off the work area restarts from its corner, still on screen",
              cas is Box e && e.X >= 0 && e.Y >= 0 && e.X + e.Width <= 1920 && e.Y + e.Height <= 1040 && e != new Box(900, 340, 1000, 700), $"{cas}");

        cas = Cascade(new Box(2000, 50, 1000, 600), areas, 640, 420);
        Check("a cascade stays on the monitor its source is on", cas is Box f && f.X >= 1920, $"{cas}");

        string arg = FormatBoundsArg(new Box(132, -8, 1000.4, 700.6));
        Check("the bounds argument round-trips (rounded to whole DIPs)",
              arg == "--window-bounds=132,-8,1000,701"
              && TryParseBoundsArg(new[] { "--new-window", "a.pdf", arg }, out var parsed) && parsed == new Box(132, -8, 1000, 701), arg);

        Check("a malformed bounds argument is rejected",
              !TryParseBoundsArg(new[] { "--window-bounds=1,2,3" }, out _)
              && !TryParseBoundsArg(new[] { "--window-bounds=a,b,c,d" }, out _)
              && !TryParseBoundsArg(new[] { "--window-bounds=0,0,-5,100" }, out _)
              && !TryParseBoundsArg(new[] { "--window-bounds=0,0,NaN,100" }, out _)
              && !TryParseBoundsArg(new[] { "file.pdf" }, out _), "");
    }
}
