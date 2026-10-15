using TDPdf.Services;

/// <summary>
/// Pins <see cref="ZoomAnchorMath"/>: the Ctrl+wheel / pinch zoom step and the scroll that keeps
/// the point under the pointer still.
/// </summary>
/// <remarks>
/// The two properties that made the old wheel zoom feel broken are asserted directly rather than
/// through sample values: that a touchpad's burst of small deltas lands exactly where one geared
/// notch does (so a pinch is a glide, not a skip across presets), and that zoom-in then zoom-out
/// by the same delta is the identity (so there is no drift to accumulate over a long gesture).
/// The anchor half is checked against a small model of the real layout — the Border's padding, a
/// page centred when narrower than the viewport, content scaled about its own origin — because
/// that is exactly where a (offset + origin) / zoom shortcut stops being exact.
/// </remarks>
internal static class ZoomAnchor
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("Smooth cursor-anchored zoom (ZoomAnchorMath)");

        const double Min = 0.05, Max = 4.0;
        static bool Near(double a, double b, double eps = 1e-9) => Math.Abs(a - b) <= eps;

        // ── Delta -> zoom ────────────────────────────────────────────────────────────────
        double notch = ZoomAnchorMath.ZoomForWheel(1.0, 120, Min, Max);
        Check("one geared notch in is one step of WheelZoomFactorPerNotch",
              Near(notch, ZoomAnchorMath.WheelZoomFactorPerNotch), $"{notch:F4}");
        double perNotch = ZoomAnchorMath.WheelFactor(120);
        Check("the per-notch step sits in the preset spacing (1.1x-1.25x)",
              perNotch >= 1.1 && perNotch <= 1.25, $"{perNotch}");

        double z = 1.0;
        for (int i = 0; i < 5; i++) z = ZoomAnchorMath.ZoomForWheel(z, 24, Min, Max);
        Check("five 24-delta touchpad messages land where one 120 notch does",
              Near(z, notch, 1e-12), $"{z:F6} vs {notch:F6}");

        double small = ZoomAnchorMath.ZoomForWheel(1.0, 24, Min, Max);
        Check("a small touchpad delta is a small zoom change (< 5%)",
              small > 1.0 && small < 1.05, $"{small:F4}");

        double back = ZoomAnchorMath.ZoomForWheel(ZoomAnchorMath.ZoomForWheel(1.37, 120, Min, Max), -120, Min, Max);
        Check("in then out by the same delta returns to the start", Near(back, 1.37, 1e-12), $"{back:F6}");

        double ratioLow = ZoomAnchorMath.ZoomForWheel(0.1, 120, Min, Max) / 0.1;
        double ratioHigh = ZoomAnchorMath.ZoomForWheel(2.0, 120, Min, Max) / 2.0;
        Check("a notch is the same RATIO at 10% and at 200%", Near(ratioLow, ratioHigh, 1e-12),
              $"{ratioLow:F4} vs {ratioHigh:F4}");

        Check("zoom-in clamps at the maximum", ZoomAnchorMath.ZoomForWheel(3.9, 1200, Min, Max) == Max, "");
        Check("zoom-out clamps at the minimum", ZoomAnchorMath.ZoomForWheel(0.06, -1200, Min, Max) == Min, "");
        Check("a zero delta changes nothing", ZoomAnchorMath.ZoomForWheel(1.25, 0, Min, Max) == 1.25, "");

        Check("pinch scale multiplies the zoom", Near(ZoomAnchorMath.ZoomForPinch(1.0, 1.5, Min, Max), 1.5), "");
        Check("a NaN pinch scale leaves the zoom alone", ZoomAnchorMath.ZoomForPinch(1.25, double.NaN, Min, Max) == 1.25, "");
        Check("a non-positive pinch scale leaves the zoom alone", ZoomAnchorMath.ZoomForPinch(1.25, 0, Min, Max) == 1.25, "");

        // ── Anchor -> offsets, against a model of the real layout ──────────────────────────
        // Viewport of width V. Content = padding P + page of natural width W scaled by zoom. When
        // the content is narrower than the viewport WPF centres it and the scrollable width is 0.
        const double P = 12, W = 800, H = 1000, Vw = 1000, Vh = 700;
        static double ContentW(double zoom) => 2 * P + W * zoom;
        static double ContentH(double zoom) => 2 * P + H * zoom;
        static double ScreenX(double pageX, double zoom, double offset)
        {
            double cw = ContentW(zoom);
            double left = cw < Vw ? (Vw - cw) / 2 : -offset;
            return left + P + pageX * zoom;
        }
        static double ScreenY(double pageY, double zoom, double offset) => -offset + P + pageY * zoom;

        // Zoomed in far enough to scroll both ways: the point under the pointer stays put exactly.
        {
            double z0 = 2.0, z1 = ZoomAnchorMath.ZoomForWheel(z0, 120, Min, Max);
            double h0 = 500, v0 = 900, cursorX = 420, cursorY = 310;
            double pageX = (cursorX - ScreenX(0, z0, h0)) / z0;   // the document point under the pointer
            double pageY = (cursorY - ScreenY(0, z0, v0)) / z0;
            var (h1, v1) = ZoomAnchorMath.OffsetsKeepingAnchor(h0, v0,
                ScreenX(pageX, z1, h0), ScreenY(pageY, z1, v0), cursorX, cursorY,
                ContentW(z1) - Vw, ContentH(z1) - Vh);
            double ex = Math.Abs(ScreenX(pageX, z1, h1) - cursorX), ey = Math.Abs(ScreenY(pageY, z1, v1) - cursorY);
            Check("zooming in keeps the document point under the pointer", ex < 1e-9 && ey < 1e-9,
                  $"drift {ex:E1}, {ey:E1}");
        }

        // Several messages in one gesture, each corrected against the ONE anchor captured at the
        // start: no drift builds up however long the pinch runs.
        {
            double zz = 1.5, h = 0, v = 400, cursorX = 700, cursorY = 200;
            double pageX = (cursorX - ScreenX(0, zz, h)) / zz, pageY = (cursorY - ScreenY(0, zz, v)) / zz;
            double worst = 0;
            foreach (double delta in new[] { 30.0, 45, 12, 120, -60, 90, 8, -24, 240 })
            {
                zz = ZoomAnchorMath.ZoomForWheel(zz, delta, Min, Max);
                (h, v) = ZoomAnchorMath.OffsetsKeepingAnchor(h, v,
                    ScreenX(pageX, zz, h), ScreenY(pageY, zz, v), cursorX, cursorY,
                    Math.Max(0, ContentW(zz) - Vw), Math.Max(0, ContentH(zz) - Vh));
                worst = Math.Max(worst, Math.Abs(ScreenX(pageX, zz, h) - cursorX));
                worst = Math.Max(worst, Math.Abs(ScreenY(pageY, zz, v) - cursorY));
            }
            Check("a nine-message gesture stays anchored throughout", worst < 1e-9, $"worst drift {worst:E1}");
        }

        // A page narrower than the viewport is centred and cannot scroll sideways: the horizontal
        // offset must stay 0 rather than go negative, while the vertical anchor still holds.
        {
            double z0 = 0.7, z1 = ZoomAnchorMath.ZoomForWheel(z0, 120, Min, Max);   // 696 wide, 864 tall
            double v0 = 20, cursorX = 500, cursorY = 300;
            double pageY = (cursorY - ScreenY(0, z0, v0)) / z0;
            var (h1, v1) = ZoomAnchorMath.OffsetsKeepingAnchor(0, v0,
                ScreenX(200, z1, 0), ScreenY(pageY, z1, v0), cursorX, cursorY,
                Math.Max(0, ContentW(z1) - Vw), Math.Max(0, ContentH(z1) - Vh));
            Check("a centred narrow page keeps a zero horizontal offset", h1 == 0, $"{h1}");
            Check("...while the vertical anchor still holds",
                  Math.Abs(ScreenY(pageY, z1, v1) - cursorY) < 1e-9, $"{v1:F3}");
        }

        // At the top-left edge the anchor genuinely cannot stay put: zooming out past the start of
        // the document clamps to 0 instead of asking for a negative offset.
        {
            var (h1, v1) = ZoomAnchorMath.OffsetsKeepingAnchor(5, 5, 50, 50, 200, 200, 3000, 3000);
            Check("offsets clamp at the leading edge", h1 == 0 && v1 == 0, $"{h1}, {v1}");
            var (h2, v2) = ZoomAnchorMath.OffsetsKeepingAnchor(2990, 2990, 400, 400, 100, 100, 3000, 3000);
            Check("offsets clamp at the trailing edge", h2 == 3000 && v2 == 3000, $"{h2}, {v2}");
            var (h3, v3) = ZoomAnchorMath.OffsetsKeepingAnchor(10, 10, double.NaN, 20, 5, 5, 100, 100);
            Check("a non-finite position does not poison the offsets", h3 == 0 && v3 == 25, $"{h3}, {v3}");
        }
    }
}
