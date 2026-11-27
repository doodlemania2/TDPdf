namespace TDPdf.Services
{
    /// <summary>
    /// The colour of the paper around a block of text, read from a rendered page.
    /// </summary>
    /// <remarks>
    /// A cover drawn over scanned text has to match the scan, which is cream, grey or speckled
    /// rather than the white of a PDF page. The ring of pixels just outside the text box is almost
    /// all paper, so the median of each channel over it is the paper; a median rather than a mean
    /// keeps the odd dark pixel of a neighbouring line out of it.
    /// </remarks>
    internal static class PaperColor
    {
        /// <summary>
        /// Median colour of a ring <paramref name="margin"/> pixels wide just outside the rectangle.
        /// Returns white when the ring holds nothing to read.
        /// </summary>
        /// <param name="bgra">Pixels, 4 bytes each in B, G, R, A order.</param>
        internal static (byte R, byte G, byte B) Sample(
            byte[] bgra, int width, int height, int stride, int left, int top, int right, int bottom, int margin = 4)
        {
            var rs = new List<byte>(); var gs = new List<byte>(); var bs = new List<byte>();
            int x0 = Math.Max(0, left - margin), x1 = Math.Min(width - 1, right + margin);
            int y0 = Math.Max(0, top - margin), y1 = Math.Min(height - 1, bottom + margin);
            for (int y = y0; y <= y1; y++)
            {
                bool rowInside = y >= top && y <= bottom;
                for (int x = x0; x <= x1; x++)
                {
                    if (rowInside && x >= left && x <= right) continue;
                    int p = y * stride + x * 4;
                    if (p + 3 >= bgra.Length) continue;
                    bs.Add(bgra[p]); gs.Add(bgra[p + 1]); rs.Add(bgra[p + 2]);
                }
            }
            if (rs.Count == 0) return (255, 255, 255);
            return (Median(rs), Median(gs), Median(bs));
        }

        private static byte Median(List<byte> v)
        {
            v.Sort();
            return v[v.Count / 2];
        }
    }
}
