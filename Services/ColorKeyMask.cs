namespace TDPdf.Services
{
    /// <summary>
    /// The pixel maths behind Extract Image: cut a box out of a picture, or make one colour area of it
    /// transparent (or keep only that area), with a tolerance and soft edges. Works on straight
    /// (non-premultiplied) BGRA, the layout PDFium and WPF both hand around, and never touches WPF.
    /// </summary>
    internal static class ColorKeyMask
    {
        /// <summary>The largest distance between two colours: black to white, sqrt(3 * 255^2).</summary>
        internal const double MaxDistance = 441.6729559300637;

        /// <summary>A rectangle of pixels.</summary>
        internal readonly record struct PixelBox(int X, int Y, int W, int H)
        {
            internal bool IsEmpty => W <= 0 || H <= 0;
        }

        /// <summary>
        /// A copy of <paramref name="box"/> from the picture, clamped to it. Null when nothing is left.
        /// </summary>
        internal static (byte[] Bgra, int W, int H)? CropBox(byte[] bgra, int w, int h, PixelBox box)
        {
            if (!Valid(bgra, w, h)) return null;
            int x0 = Math.Clamp(box.X, 0, w), y0 = Math.Clamp(box.Y, 0, h);
            int x1 = Math.Clamp(box.X + box.W, 0, w), y1 = Math.Clamp(box.Y + box.H, 0, h);
            int cw = x1 - x0, ch = y1 - y0;
            if (cw <= 0 || ch <= 0) return null;

            var o = new byte[cw * ch * 4];
            for (int y = 0; y < ch; y++)
                Buffer.BlockCopy(bgra, ((y0 + y) * w + x0) * 4, o, y * cw * 4, cw * 4);
            return (o, cw, ch);
        }

        /// <summary>
        /// The colour of the pixel at (<paramref name="x"/>, <paramref name="y"/>) as (B, G, R), or null
        /// off the picture.
        /// </summary>
        internal static (byte B, byte G, byte R)? ColourAt(byte[] bgra, int w, int h, int x, int y)
        {
            if (!Valid(bgra, w, h) || x < 0 || y < 0 || x >= w || y >= h) return null;
            int i = (y * w + x) * 4;
            return (bgra[i], bgra[i + 1], bgra[i + 2]);
        }

        /// <summary>
        /// Applies the colour key. A pixel whose colour is within <paramref name="tolerance"/> of the key
        /// matches fully; between <paramref name="tolerance"/> and <paramref name="tolerance"/> +
        /// <paramref name="soft"/> it matches partly, which is what gives the edge its softness; beyond
        /// that it does not match. Distances are Euclidean in RGB on a 0..255 scale per channel.
        /// </summary>
        /// <param name="keepArea">
        /// False (the usual) makes the matching colour transparent and keeps everything else, which is
        /// how a signature on white paper comes off the paper. True keeps only the matching area and
        /// clears the rest.
        /// </param>
        /// <param name="contiguousFrom">
        /// When set, only the area connected to that pixel counts as matching, so the same colour
        /// elsewhere (the white inside a letter's loop, say) is left alone. Null matches the colour
        /// everywhere.
        /// </param>
        /// <returns>A new buffer; the input is never changed. Null for an unusable picture.</returns>
        internal static byte[]? Apply(
            byte[] bgra, int w, int h, (byte B, byte G, byte R) key,
            double tolerance, double soft, bool keepArea, (int X, int Y)? contiguousFrom)
        {
            if (!Valid(bgra, w, h)) return null;
            tolerance = Math.Clamp(tolerance, 0, MaxDistance);
            soft = Math.Clamp(soft, 0, MaxDistance);
            double outer = tolerance + soft;

            int n = w * h;
            var dist = new double[n];
            for (int p = 0; p < n; p++)
            {
                int i = p * 4;
                double db = bgra[i] - key.B, dg = bgra[i + 1] - key.G, dr = bgra[i + 2] - key.R;
                dist[p] = Math.Sqrt(db * db + dg * dg + dr * dr);
            }

            bool[]? region = null;
            if (contiguousFrom is { } seed)
            {
                if (seed.X < 0 || seed.Y < 0 || seed.X >= w || seed.Y >= h) return null;
                region = Flood(dist, w, h, seed.X, seed.Y, outer);
            }

            var o = new byte[bgra.Length];
            Buffer.BlockCopy(bgra, 0, o, 0, bgra.Length);
            for (int p = 0; p < n; p++)
            {
                // How much this pixel belongs to the key area: 1 inside the tolerance, falling to 0
                // across the soft band, 0 outside it (and 0 outside the connected region).
                double match;
                if (region is not null && !region[p]) match = 0;
                else if (dist[p] <= tolerance) match = 1;
                else if (dist[p] >= outer) match = 0;
                else match = 1 - (dist[p] - tolerance) / soft;

                double keep = keepArea ? match : 1 - match;
                int ai = p * 4 + 3;
                o[ai] = (byte)Math.Round(bgra[ai] * keep);
            }
            return o;
        }

        /// <summary>
        /// The smallest box holding every pixel that is not fully transparent, so an extracted shape
        /// does not drag the empty paper around it. Null when nothing is opaque.
        /// </summary>
        internal static PixelBox? OpaqueBounds(byte[] bgra, int w, int h, int minAlpha = 1)
        {
            if (!Valid(bgra, w, h)) return null;
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (bgra[(y * w + x) * 4 + 3] >= minAlpha)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
            return maxX < 0 ? null : new PixelBox(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        /// <summary>
        /// Lays the alpha of one picture over the colours of another, scaled to fit: how a soft-masked
        /// picture keeps its transparency when its colour data and its mask come from separate reads.
        /// </summary>
        internal static byte[]? WithAlphaFrom(byte[] colour, int w, int h, byte[] alphaSource, int aw, int ah)
        {
            if (!Valid(colour, w, h) || !Valid(alphaSource, aw, ah)) return null;
            var o = new byte[colour.Length];
            Buffer.BlockCopy(colour, 0, o, 0, colour.Length);
            for (int y = 0; y < h; y++)
            {
                int sy = Math.Min(ah - 1, (int)((y + 0.5) * ah / h));
                for (int x = 0; x < w; x++)
                {
                    int sx = Math.Min(aw - 1, (int)((x + 0.5) * aw / w));
                    o[(y * w + x) * 4 + 3] = alphaSource[(sy * aw + sx) * 4 + 3];
                }
            }
            return o;
        }

        private static bool[] Flood(double[] dist, int w, int h, int sx, int sy, double limit)
        {
            var seen = new bool[w * h];
            int start = sy * w + sx;
            // A seed that does not itself match selects nothing rather than leaking out.
            if (dist[start] > limit) return seen;

            var stack = new Stack<int>();
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int x = p % w, y = p / w;
                if (x > 0) Visit(p - 1);
                if (x < w - 1) Visit(p + 1);
                if (y > 0) Visit(p - w);
                if (y < h - 1) Visit(p + w);
            }
            return seen;

            void Visit(int q)
            {
                if (seen[q] || dist[q] > limit) return;
                seen[q] = true;
                stack.Push(q);
            }
        }

        private static bool Valid(byte[] bgra, int w, int h) =>
            w > 0 && h > 0 && (long)w * h * 4 == bgra.Length;
    }
}
