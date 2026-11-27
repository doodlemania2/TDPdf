namespace TDPdf.Services
{
    /// <summary>
    /// The multiply blend a highlight is saved and printed with, done on the pixels of the page
    /// bitmap so the screen shows the same thing: the colour darkens the paper and the text under
    /// it stays dark, at any opacity.
    /// </summary>
    /// <remarks>
    /// WPF has no multiply blend for an element laid over the page, so the on-screen highlight used
    /// to be a plain alpha fill, which washes dark text out under a strong highlight. For a PDF
    /// Multiply blend with constant alpha <c>a</c>, a backdrop channel <c>b</c> and a highlight
    /// channel <c>c</c> gives <c>(1 - a) * b + a * (b * c)</c>, which is <c>b * (1 - a + a * c)</c>,
    /// a per-channel lerp from 1 to the colour scaled by the backdrop.
    /// </remarks>
    internal static class HighlightMultiply
    {
        /// <summary>The per-channel multiplier, 0..1: <c>lerp(1, colour / 255, opacity)</c>.</summary>
        public static double Factor(byte colour, double opacity)
        {
            double a = double.IsNaN(opacity) ? 0 : Math.Clamp(opacity, 0, 1);
            return 1 - a + a * colour / 255.0;
        }

        /// <summary>
        /// Multiplies the highlight colour into <paramref name="region"/> of a straight-alpha BGRA
        /// buffer, in place. Pixels in the region are first composited over white and made opaque
        /// (a page raster may leave the paper transparent), so the paper takes the colour; pixels
        /// outside the region are left exactly as they were. Highlights applied one after another
        /// stack the way overlapping highlights do in the saved file.
        /// </summary>
        public static void Apply(byte[] bgra, int width, int height,
                                 (int X, int Y, int W, int H) region,
                                 byte r, byte g, byte b, double opacity)
        {
            if (width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4) return;
            int x0 = Math.Max(0, region.X), y0 = Math.Max(0, region.Y);
            int x1 = Math.Min(width, region.X + region.W), y1 = Math.Min(height, region.Y + region.H);
            if (x1 <= x0 || y1 <= y0) return;

            double fb = Factor(b, opacity), fg = Factor(g, opacity), fr = Factor(r, opacity);
            for (int y = y0; y < y1; y++)
            {
                int i = (y * width + x0) * 4;
                for (int x = x0; x < x1; x++, i += 4)
                {
                    int a = bgra[i + 3];
                    int pb = bgra[i], pg = bgra[i + 1], pr = bgra[i + 2];
                    if (a != 255)
                    {
                        pb = (pb * a + 255 * (255 - a) + 127) / 255;
                        pg = (pg * a + 255 * (255 - a) + 127) / 255;
                        pr = (pr * a + 255 * (255 - a) + 127) / 255;
                    }
                    bgra[i] = (byte)Math.Round(pb * fb);
                    bgra[i + 1] = (byte)Math.Round(pg * fg);
                    bgra[i + 2] = (byte)Math.Round(pr * fr);
                    bgra[i + 3] = 255;
                }
            }
        }
    }
}
