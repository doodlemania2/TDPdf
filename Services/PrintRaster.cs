namespace TDPdf.Services
{
    /// <summary>
    /// How big to rasterize a page for the print window: the pixel box for the size the page will have
    /// on the sheet, at a target density, lowered when the box would exceed what PDFium, a .NET array
    /// and WPF can hold. Pure, so tests/PdfCore compiles this file as-is.
    /// </summary>
    internal readonly record struct PrintRasterBox(int Width, int Height, double Dpi, bool Capped)
    {
        // A BGRA buffer of 100 MP is 400 MB: the largest the renderer allocates twice at once (the
        // PDFium buffer, then the WPF bitmap copy) on a 64-bit process without thrashing. 16384 px is
        // the edge limit WPF's software and hardware paths both handle (many GPU textures stop there),
        // and keeps width * 4 and the byte count far inside int range.
        public const int MaxEdge = 16384;
        public const long MaxPixels = 100_000_000;

        /// <summary>True when a <paramref name="width"/> x <paramref name="height"/> raster is within both limits.</summary>
        public static bool WithinRenderLimit(long width, long height) =>
            width > 0 && height > 0 && width <= MaxEdge && height <= MaxEdge && width * height <= MaxPixels;

        /// <summary>
        /// The raster for a page shown <paramref name="printedWidthDip"/> x <paramref name="printedHeightDip"/>
        /// (device-independent pixels, 96 per inch) at <paramref name="targetDpi"/>. When the result would
        /// exceed the limits the density is lowered until it fits, never the page cropped.
        /// </summary>
        public static PrintRasterBox For(double printedWidthDip, double printedHeightDip, double targetDpi)
        {
            if (!(printedWidthDip > 0) || !(printedHeightDip > 0) || !(targetDpi > 0))
                return new PrintRasterBox(1, 1, targetDpi > 0 ? targetDpi : 1, false);

            double dpi = targetDpi;
            double w = printedWidthDip / 96.0 * dpi, h = printedHeightDip / 96.0 * dpi;
            bool capped = false;
            double edge = Math.Max(w, h);
            if (edge > MaxEdge) { dpi *= MaxEdge / edge; capped = true; }
            w = printedWidthDip / 96.0 * dpi; h = printedHeightDip / 96.0 * dpi;
            if (w * h > MaxPixels) { dpi *= Math.Sqrt(MaxPixels / (w * h)); capped = true; }

            // Round down so rounding never tips a capped box back over a limit.
            int pw = (int)Math.Max(1, Math.Floor(printedWidthDip / 96.0 * dpi));
            int ph = (int)Math.Max(1, Math.Floor(printedHeightDip / 96.0 * dpi));
            return new PrintRasterBox(pw, ph, dpi, capped);
        }
    }
}
