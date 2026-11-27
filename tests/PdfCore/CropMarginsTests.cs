using TDPdf.Services;

/// <summary>
/// Pins <see cref="CropMargins"/>: "apply crop to all pages" carries the margins the user drew, as
/// displayed, to every page's own box and rotation.
/// </summary>
internal static class CropMarginsTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        static bool Near(CropMargins.UserRect a, CropMargins.UserRect b) =>
            Math.Abs(a.Left - b.Left) < 1e-6 && Math.Abs(a.Bottom - b.Bottom) < 1e-6 &&
            Math.Abs(a.Right - b.Right) < 1e-6 && Math.Abs(a.Top - b.Top) < 1e-6;

        // Letter page at the origin, 36 pt in from the left/right, 72 from the top, 18 from the bottom.
        var crop = new CropMargins.UserRect(36, 18, 576, 720);
        var m0 = CropMargins.FromRect(0, 0, 612, 792, 0, crop);
        Check("crop margins: unrotated margins", m0 == new CropMargins.Margins(36, 72, 36, 18), $"{m0}");

        // Same margins onto A4 (595.28 x 841.89) at the origin.
        var a4 = CropMargins.ToRect(0, 0, 595.28, 841.89, 0, m0);
        Check("crop margins: other page size keeps the margins, not the rectangle",
              Near(a4, new CropMargins.UserRect(36, 18, 595.28 - 36, 841.89 - 72)), $"{a4}");

        // A box that does not start at the origin.
        var off = CropMargins.ToRect(9, 9, 612, 792, 0, m0);
        Check("crop margins: box origin is honoured", Near(off, new CropMargins.UserRect(45, 27, 585, 729)), $"{off}");

        // Round trip on the same page, at every rotation.
        foreach (int rot in new[] { 0, 90, 180, 270 })
        {
            var m = CropMargins.FromRect(9, 5, 612, 792, rot, crop with { Left = 45, Bottom = 30, Right = 500, Top = 700 });
            var back = CropMargins.ToRect(9, 5, 612, 792, rot, m);
            Check($"crop margins: round trip at {rot}", Near(back, new CropMargins.UserRect(45, 30, 500, 700)), $"{back}");
        }

        // Displayed frame: on a page rotated 90 degrees clockwise the displayed LEFT edge is the
        // user-space bottom, the displayed TOP is the user-space left.
        var r90 = CropMargins.FromRect(0, 0, 612, 792, 90, new CropMargins.UserRect(10, 20, 600, 780));
        Check("crop margins: 90 maps displayed left to user bottom, top to user left",
              r90 == new CropMargins.Margins(Left: 20, Top: 10, Right: 12, Bottom: 12), $"{r90}");

        // One displayed margin set, two pages with different /Rotate: the same look on screen.
        var disp = new CropMargins.Margins(Left: 30, Top: 50, Right: 10, Bottom: 20);
        var p0 = CropMargins.ToRect(0, 0, 600, 800, 0, disp);
        Check("crop margins: rotate 0 target", Near(p0, new CropMargins.UserRect(30, 20, 590, 750)), $"{p0}");
        var p180 = CropMargins.ToRect(0, 0, 600, 800, 180, disp);
        Check("crop margins: rotate 180 target", Near(p180, new CropMargins.UserRect(10, 50, 570, 780)), $"{p180}");
        var p90 = CropMargins.ToRect(0, 0, 600, 800, 90, disp);   // displayed 800 wide, 600 high
        Check("crop margins: rotate 90 target", Near(p90, new CropMargins.UserRect(50, 30, 580, 790)), $"{p90}");
        var p270 = CropMargins.ToRect(0, 0, 600, 800, 270, disp);
        Check("crop margins: rotate 270 target", Near(p270, new CropMargins.UserRect(20, 10, 550, 770)), $"{p270}");

        // A page too small for the margins is scaled down, never emptied or inverted.
        var big = new CropMargins.Margins(400, 400, 400, 400);
        var tiny = CropMargins.ToRect(0, 0, 200, 100, 0, big);
        Check("crop margins: oversized margins leave a non-empty page",
              tiny.Right - tiny.Left >= 1 - 1e-9 && tiny.Top - tiny.Bottom >= 1 - 1e-9 &&
              tiny.Left >= 0 && tiny.Right <= 200 && tiny.Bottom >= 0 && tiny.Top <= 100, $"{tiny}");
        Check("crop margins: oversized margins keep their ratio", Math.Abs(tiny.Left - (200 - tiny.Right)) < 1e-9, $"{tiny}");

        // Nonsense in, sane out.
        var nan = CropMargins.ToRect(0, 0, 100, 100, 0, new CropMargins.Margins(double.NaN, -5, double.PositiveInfinity, 0));
        Check("crop margins: NaN, negative and infinite margins are tamed",
              double.IsFinite(nan.Left + nan.Right + nan.Top + nan.Bottom) && nan.Right > nan.Left && nan.Top > nan.Bottom, $"{nan}");
        // Rotation outside 0..359 normalizes.
        Check("crop margins: -90 is 270",
              Near(CropMargins.ToRect(0, 0, 600, 800, -90, disp), p270), "");
    }
}
