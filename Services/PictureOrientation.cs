namespace TDPdf.Services
{
    /// <summary>
    /// Turns a picture's raw pixels into the picture AS SHOWN on the page: PDF stores the pixels one way
    /// and the page's matrix (and the page's own /Rotate) may flip or quarter-turn them, so a copy that
    /// used the raw pixels would paste upside down or mirrored.
    /// </summary>
    internal static class PictureOrientation
    {
        /// <summary>
        /// Reorients straight BGRA pixels. <paramref name="rightX"/>, <paramref name="rightY"/> is where the
        /// picture's own left-to-right direction points on the canvas (y down), and
        /// <paramref name="downX"/>, <paramref name="downY"/> where its own top-to-bottom direction points.
        /// Only the dominant axis of each counts, so a picture drawn at an odd angle is quarter-turned to
        /// the nearest upright view rather than resampled.
        /// </summary>
        internal static (byte[] Bgra, int W, int H)? Apply(
            byte[] bgra, int w, int h, double rightX, double rightY, double downX, double downY)
        {
            if (w <= 0 || h <= 0 || (long)w * h * 4 != bgra.Length) return null;

            bool swap = Math.Abs(rightX) < Math.Abs(rightY);
            if (!swap && Math.Abs(downY) < Math.Abs(downX)) swap = true;   // inconsistent axes: trust the down axis

            if (!swap && rightX > 0 && downY > 0) return (bgra, w, h);

            int ow = swap ? h : w, oh = swap ? w : h;
            var o = new byte[bgra.Length];
            bool flipU, flipV;
            if (!swap) { flipU = rightX < 0; flipV = downY < 0; }
            else { flipU = rightY < 0; flipV = downX < 0; }

            for (int y = 0; y < oh; y++)
                for (int x = 0; x < ow; x++)
                {
                    int u, v;
                    if (!swap)
                    {
                        u = flipU ? w - 1 - x : x;
                        v = flipV ? h - 1 - y : y;
                    }
                    else
                    {
                        // The picture's left-to-right runs down the canvas, its top-to-bottom across it.
                        v = flipV ? h - 1 - x : x;
                        u = flipU ? w - 1 - y : y;
                    }
                    Buffer.BlockCopy(bgra, (v * w + u) * 4, o, (y * ow + x) * 4, 4);
                }
            return (o, ow, oh);
        }
    }
}
