using System;

namespace TDPdf.Services
{
    // ============================================================
    // Smooth, cursor-anchored Ctrl+wheel / pinch zoom (idea from upstream KillerPDF's
    // PinchZoomMath, re-derived for our layout).
    //
    // Ctrl+wheel used to jump one whole preset per wheel MESSAGE, whatever its delta. A geared
    // wheel sends one 120 message per notch, so that was tolerable there — but a precision
    // touchpad pinch arrives as a burst of small-delta Ctrl+wheel messages, so one gentle pinch
    // walked straight through half the preset list. The zoom also grew about the page's top-left
    // (Single) or snapped back to the page top (Continuous), so the thing under the pointer left
    // the screen on every step.
    //
    // This holds the two pieces of that which are pure arithmetic, so they can be pinned in
    // tests/PdfCore without WPF:
    //
    //   1. Delta -> zoom. MULTIPLICATIVE and proportional to the delta: every 120 of delta changes
    //      the zoom by the same ratio, wherever on the range it is, and because
    //      f^(a/120) * f^(b/120) == f^((a+b)/120), a touchpad's five 24-delta messages land on
    //      exactly the zoom one geared notch does. That identity IS the delta accumulation — there
    //      is no separate accumulator to drift, reset or leak across gestures.
    //   2. Anchor -> scroll offsets. MainWindow captures the document point under the pointer
    //      once per gesture, lets WPF lay out the new scale, asks where that point has moved to,
    //      and scrolls by the difference. Measuring the moved point from the real visual tree —
    //      rather than modelling it as upstream does with (offset + origin) / zoom — is what keeps
    //      it exact under our Border padding, the centred narrow page, and Continuous view's slot
    //      layout, none of which scale about the scroll origin.
    // ============================================================
    internal static class ZoomAnchorMath
    {
        /// <summary>One standard geared wheel notch, as WPF reports it in <c>MouseWheelEventArgs.Delta</c>.</summary>
        internal const double WheelNotchDelta = 120.0;

        /// <summary>
        /// Zoom ratio per full wheel notch. The presets between 50% and 200% are 1.2-1.33x apart
        /// (100 -> 125 -> 150 -> 200), so 1.2 keeps one notch feeling like roughly one preset step
        /// did in the range people actually read in, while staying a constant ratio at the ends,
        /// where the old 5% -> 10% -> 25% steps were a doubling or more per notch.
        /// </summary>
        internal const double WheelZoomFactorPerNotch = 1.2;

        /// <summary>The zoom multiplier for one wheel message of <paramref name="delta"/>.</summary>
        internal static double WheelFactor(double delta) =>
            double.IsFinite(delta) ? Math.Pow(WheelZoomFactorPerNotch, delta / WheelNotchDelta) : 1.0;

        /// <summary>The zoom after one Ctrl+wheel message, clamped to <paramref name="min"/>..<paramref name="max"/>.</summary>
        internal static double ZoomForWheel(double current, double delta, double min, double max) =>
            Scale(current, WheelFactor(delta), min, max);

        /// <summary>
        /// The zoom after a pinch frame reporting <paramref name="scale"/> (the manipulation's
        /// incremental scale — 1.0 means no change). A non-finite or non-positive scale, which a
        /// digitizer glitch can produce, leaves the zoom where it is rather than poisoning it.
        /// </summary>
        internal static double ZoomForPinch(double current, double scale, double min, double max) =>
            Scale(current, double.IsFinite(scale) && scale > 0 ? scale : 1.0, min, max);

        private static double Scale(double current, double factor, double min, double max)
        {
            if (!double.IsFinite(current) || current <= 0) current = Math.Max(min, 1e-6);
            return Math.Clamp(current * factor, min, max);
        }

        /// <summary>
        /// The scroll offsets that put the gesture's anchor point back at <paramref name="targetX"/>,
        /// <paramref name="targetY"/> (both in viewport coordinates).
        /// </summary>
        /// <param name="horizontalOffset">The scroll viewer's current horizontal offset.</param>
        /// <param name="verticalOffset">The scroll viewer's current vertical offset.</param>
        /// <param name="anchorNowX">Where the anchor point is in the viewport NOW, laid out at the new zoom and the current offsets.</param>
        /// <param name="anchorNowY">As <paramref name="anchorNowX"/>, vertically.</param>
        /// <param name="targetX">Where it has to be — the pointer position the gesture began at.</param>
        /// <param name="targetY">As <paramref name="targetX"/>, vertically.</param>
        /// <param name="scrollableWidth">The scroll viewer's <c>ScrollableWidth</c> at the new zoom.</param>
        /// <param name="scrollableHeight">The scroll viewer's <c>ScrollableHeight</c> at the new zoom.</param>
        /// <remarks>
        /// Scrolling right by d moves every content point d to the left, so the offset simply grows
        /// by how far the anchor overshot its target. The clamp is not cosmetic: a ScrollViewer
        /// clamps too, but doing it here keeps the returned value honest for the caller and the
        /// tests — near an edge (or on a page narrower than the viewport, which WPF centres and
        /// cannot scroll at all) the anchor genuinely cannot stay put, and zooming about the edge is
        /// the right fallback.
        /// </remarks>
        internal static (double Horizontal, double Vertical) OffsetsKeepingAnchor(
            double horizontalOffset, double verticalOffset,
            double anchorNowX, double anchorNowY,
            double targetX, double targetY,
            double scrollableWidth, double scrollableHeight)
        {
            double h = horizontalOffset + (anchorNowX - targetX);
            double v = verticalOffset + (anchorNowY - targetY);
            return (ClampOffset(h, scrollableWidth), ClampOffset(v, scrollableHeight));
        }

        private static double ClampOffset(double value, double scrollable)
        {
            if (!double.IsFinite(value)) return 0;
            double max = double.IsFinite(scrollable) ? Math.Max(0, scrollable) : 0;
            return Math.Clamp(value, 0, max);
        }
    }
}
