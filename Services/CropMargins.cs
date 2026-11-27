namespace TDPdf.Services
{
    /// <summary>
    /// Carries one page's crop over to other pages as margins instead of as an absolute rectangle.
    /// </summary>
    /// <remarks>
    /// "Apply to all pages" used to hand every page the first page's rectangle in user space, so a
    /// page with a different size, a box that does not start at (0,0), or a different /Rotate was
    /// cropped in the wrong place. The margins below are what the user actually drew: how far the
    /// crop sits in from each edge of the page AS DISPLAYED (so a landscape page rotated 90 degrees
    /// is cropped the way it looks), measured in points from that page's own visible box. Each
    /// target page applies the same margins to its own box and rotation. When a smaller page
    /// cannot honour both margins on an axis they are scaled down together, keeping their ratio, so
    /// something at least <see cref="MinSize"/> points wide remains.
    /// </remarks>
    internal static class CropMargins
    {
        /// <summary>Smallest width or height a crop may leave, in points.</summary>
        public const double MinSize = 1;

        /// <summary>Margins in the displayed frame, in points.</summary>
        public readonly record struct Margins(double Left, double Top, double Right, double Bottom);

        /// <summary>A rectangle in PDF user space: lower-left and upper-right corners.</summary>
        public readonly record struct UserRect(double Left, double Bottom, double Right, double Top);

        /// <summary>
        /// The margins of <paramref name="crop"/> inside a page box (user space, lower-left origin)
        /// whose /Rotate is <paramref name="rotation"/>. The crop is clamped to the box first.
        /// </summary>
        public static Margins FromRect(double boxX, double boxY, double boxW, double boxH,
            int rotation, UserRect crop)
        {
            double uL = Math.Clamp(crop.Left - boxX, 0, boxW);
            double uR = Math.Clamp(boxX + boxW - crop.Right, 0, boxW);
            double uB = Math.Clamp(crop.Bottom - boxY, 0, boxH);
            double uT = Math.Clamp(boxY + boxH - crop.Top, 0, boxH);
            return Normalize(rotation) switch
            {
                90 => new Margins(Left: uB, Top: uL, Right: uT, Bottom: uR),
                180 => new Margins(Left: uR, Top: uB, Right: uL, Bottom: uT),
                270 => new Margins(Left: uT, Top: uR, Right: uB, Bottom: uL),
                _ => new Margins(Left: uL, Top: uT, Right: uR, Bottom: uB),
            };
        }

        /// <summary>
        /// The crop rectangle (user space) that applies <paramref name="m"/> to a page box with the
        /// given /Rotate. Never empty: the result is at least <see cref="MinSize"/> wide and high
        /// whenever the box itself is.
        /// </summary>
        public static UserRect ToRect(double boxX, double boxY, double boxW, double boxH,
            int rotation, Margins m)
        {
            int rot = Normalize(rotation);
            bool quarter = rot is 90 or 270;
            // Displayed span: width and height swap for a quarter turn.
            double dispW = quarter ? boxH : boxW;
            double dispH = quarter ? boxW : boxH;
            var (l, r) = Fit(m.Left, m.Right, dispW);
            var (t, b) = Fit(m.Top, m.Bottom, dispH);

            double uL, uR, uB, uT;
            switch (rot)
            {
                case 90: uB = l; uL = t; uT = r; uR = b; break;
                case 180: uR = l; uB = t; uL = r; uT = b; break;
                case 270: uT = l; uR = t; uB = r; uL = b; break;
                default: uL = l; uT = t; uR = r; uB = b; break;
            }
            return new UserRect(boxX + uL, boxY + uB, boxX + boxW - uR, boxY + boxH - uT);
        }

        // Scales a pair of opposing margins down together when they would leave less than MinSize.
        private static (double A, double B) Fit(double a, double b, double span)
        {
            a = double.IsFinite(a) ? Math.Max(0, a) : 0;
            b = double.IsFinite(b) ? Math.Max(0, b) : 0;
            double keep = Math.Min(MinSize, span);
            double room = span - keep;
            double sum = a + b;
            if (sum <= room || sum <= 0) return (a, b);
            double k = room / sum;
            return (a * k, b * k);
        }

        private static int Normalize(int rotation) => Angles.Mod360(rotation);
    }
}
