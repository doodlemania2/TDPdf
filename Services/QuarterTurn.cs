namespace TDPdf.Services
{
    /// <summary>
    /// The geometry of turning the visual frame a page is drawn on by a quarter turn, with no WPF
    /// types, so it can be pinned by tests. <see cref="AnnotationRotate"/> applies it to annotations.
    /// </summary>
    /// <remarks>
    /// <c>d</c> is the clockwise turn in degrees, already folded to 90, 180 or 270 (see
    /// <see cref="Angles.Mod360"/>); <c>oldW</c> and <c>oldH</c> are the frame's size BEFORE the turn.
    /// The convention is the render path's clockwise bitmap rotation.
    /// </remarks>
    internal static class QuarterTurn
    {
        /// <summary>Where the point (x, y) of the old frame lands in the turned frame.</summary>
        internal static (double X, double Y) MapPoint(int d, double oldW, double oldH, double x, double y) => d switch
        {
            90  => (oldH - y, x),
            270 => (y, oldW - x),
            _   => (oldW - x, oldH - y),   // 180
        };

        /// <summary>The rectangle (x, y, w, h) in the turned frame, with its corners normalised.</summary>
        internal static (double X, double Y, double W, double H) MapRect(int d, double oldW, double oldH,
            double x, double y, double w, double h)
        {
            var a = MapPoint(d, oldW, oldH, x, y);
            var b = MapPoint(d, oldW, oldH, x + w, y + h);
            double left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y);
            return (left, top, Math.Max(a.X, b.X) - left, Math.Max(a.Y, b.Y) - top);
        }

        /// <summary>
        /// The top-left of an UPRIGHT item of size (w, h) whose old top-left was (x, y): its centre
        /// follows the page, and it is then clamped inside the turned frame so a tall item near the
        /// old long edge stays reachable rather than landing off-sheet.
        /// </summary>
        internal static (double X, double Y) MapAnchor(int d, double oldW, double oldH,
            double x, double y, double w, double h)
        {
            bool swap = d == 90 || d == 270;
            double newW = swap ? oldH : oldW;
            double newH = swap ? oldW : oldH;
            var c = MapPoint(d, oldW, oldH, x + w / 2, y + h / 2);
            double px = c.X - w / 2;
            double py = c.Y - h / 2;
            return (Math.Max(0, Math.Min(px, Math.Max(0, newW - w))),
                    Math.Max(0, Math.Min(py, Math.Max(0, newH - h))));
        }
    }
}
