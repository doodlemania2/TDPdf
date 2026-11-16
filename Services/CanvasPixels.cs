namespace TDPdf.Services
{
    /// <summary>
    /// Converts a rectangle on the page canvas (DIPs) into the pixels of the page bitmap behind it.
    /// </summary>
    /// <remarks>
    /// The page is rendered at a DPI chosen from the zoom, so the bitmap is usually not the same
    /// size in pixels as the canvas is in DIPs (#220: about 8/7 at the default fit-width zoom, and
    /// twice as large on a 200% display). Code that took canvas coordinates as bitmap pixels cut
    /// the wrong region of the page.
    /// </remarks>
    internal static class CanvasPixels
    {
        /// <summary>
        /// The pixel region of a <paramref name="pixelW"/> x <paramref name="pixelH"/> bitmap shown at
        /// <paramref name="dipW"/> x <paramref name="dipH"/> DIPs that covers the canvas rectangle
        /// (<paramref name="x"/>, <paramref name="y"/>, <paramref name="w"/>, <paramref name="h"/>),
        /// rounded outwards and clamped to the bitmap. <c>null</c> when nothing of it is on the bitmap.
        /// </summary>
        public static (int X, int Y, int W, int H)? Region(
            double x, double y, double w, double h,
            int pixelW, int pixelH, double dipW, double dipH)
        {
            if (pixelW <= 0 || pixelH <= 0 || !(dipW > 0) || !(dipH > 0)) return null;
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h)) return null;
            double sx = pixelW / dipW, sy = pixelH / dipH;
            int left = Math.Max(0, (int)Math.Floor(x * sx));
            int top = Math.Max(0, (int)Math.Floor(y * sy));
            int right = Math.Min(pixelW, (int)Math.Ceiling((x + w) * sx));
            int bottom = Math.Min(pixelH, (int)Math.Ceiling((y + h) * sy));
            if (right <= left || bottom <= top) return null;
            return (left, top, right - left, bottom - top);
        }
    }
}
