namespace TDPdf.Services
{
    /// <summary>
    /// Geometry of a box (a text box, signature or picture) that is turned in quarter turns about its
    /// own centre.
    /// </summary>
    /// <remarks>
    /// Such an item keeps its UNROTATED box: a top-left corner plus a width and height. The turn is
    /// applied about the centre of that box when it is drawn, hit-tested and burned in. Because the
    /// turn is a multiple of 90 degrees, the turned box is again an axis-aligned rectangle with the
    /// same centre (width and height swapped for 90 and 270), and that is what the selection border,
    /// the resize handle and click hit-testing work with. Everything here is plain numbers so the
    /// arithmetic can be tested off Windows.
    /// </remarks>
    internal static class AnnotationFrame
    {
        /// <summary>Folds any angle into 0, 90, 180 or 270 (nearest quarter turn).</summary>
        public static int Normalize(int rotation)
        {
            int r = ((rotation % 360) + 360) % 360;
            return (int)(Math.Round(r / 90.0) * 90) % 360;
        }

        public static bool IsQuarterTurn(int rotation) => Normalize(rotation) is 90 or 270;

        /// <summary>Width and height of the box as it appears once turned.</summary>
        public static (double W, double H) VisualSize(double w, double h, int rotation) =>
            IsQuarterTurn(rotation) ? (h, w) : (w, h);

        /// <summary>The turned box as an axis-aligned rectangle: same centre, swapped size for 90/270.</summary>
        public static (double X, double Y, double W, double H) VisualRect(
            double x, double y, double w, double h, int rotation)
        {
            var (vw, vh) = VisualSize(w, h, rotation);
            double cx = x + w / 2, cy = y + h / 2;
            return (cx - vw / 2, cy - vh / 2, vw, vh);
        }

        /// <summary>
        /// The unrotated top-left that puts a turned box of size <paramref name="w"/> x
        /// <paramref name="h"/> with its visual top-left at (<paramref name="vx"/>, <paramref name="vy"/>).
        /// Resizing keeps the visual top-left still, and goes through this to get the new position.
        /// </summary>
        public static (double X, double Y) PositionFromVisualTopLeft(
            double vx, double vy, double w, double h, int rotation)
        {
            var (vw, vh) = VisualSize(w, h, rotation);
            double cx = vx + vw / 2, cy = vy + vh / 2;
            return (cx - w / 2, cy - h / 2);
        }

        /// <summary>
        /// Where a box lands when its page is turned clockwise by <paramref name="delta"/> degrees.
        /// <paramref name="oldW"/> and <paramref name="oldH"/> are the page's size before the turn.
        /// The centre follows the page; the box turns with it (its rotation grows by the same
        /// amount); and the turned box is kept inside the new page when it fits, so nothing lands
        /// where the user cannot reach it. Returns the new unrotated top-left and rotation.
        /// </summary>
        public static (double X, double Y, int Rotation) TurnWithPage(
            double x, double y, double w, double h, int rotation,
            int delta, double oldW, double oldH)
        {
            int d = Normalize(delta);
            int newRot = Normalize(rotation + d);
            if (d == 0) return (x, y, newRot);

            double cx = x + w / 2, cy = y + h / 2;
            (double nx, double ny) = d switch
            {
                90 => (oldH - cy, cx),
                270 => (cy, oldW - cx),
                _ => (oldW - cx, oldH - cy),
            };
            double newW = d is 90 or 270 ? oldH : oldW;
            double newH = d is 90 or 270 ? oldW : oldH;

            var (vw, vh) = VisualSize(w, h, newRot);
            double vx = Math.Max(0, Math.Min(nx - vw / 2, Math.Max(0, newW - vw)));
            double vy = Math.Max(0, Math.Min(ny - vh / 2, Math.Max(0, newH - vh)));
            var (px, py) = PositionFromVisualTopLeft(vx, vy, w, h, newRot);
            return (px, py, newRot);
        }
    }
}
