using System;

namespace TDPdf.Services
{
    /// <summary>
    /// Pure sizing rules for keeping the main window on its monitor. No WPF or Win32 types, so
    /// tests/PdfCore can pin them without a screen of a particular size.
    /// </summary>
    /// <remarks>
    /// Idea from upstream KillerPDF 1.8.92 (WorkAreaFit, and "fit native resize minimums within
    /// the monitor work area"); written against TDPdf's own window hook.
    /// </remarks>
    public static class WindowFitMath
    {
        /// <summary>Share of the work area, each way, a window opening centered may use.</summary>
        public const double MarginFill = 0.9;

        /// <summary>
        /// The size and centered position, in DIPs, for a window of <paramref name="width"/> x
        /// <paramref name="height"/> opening on a work area at (<paramref name="areaX"/>,
        /// <paramref name="areaY"/>) of <paramref name="areaWidth"/> x <paramref name="areaHeight"/>.
        /// A side that does not fit within <see cref="MarginFill"/> of the area is shrunk to it,
        /// never below its minimum unless the area itself is smaller. A window that already fits
        /// keeps its size.
        /// </summary>
        public static (double Left, double Top, double Width, double Height) FitCentered(
            double width, double height,
            double areaX, double areaY, double areaWidth, double areaHeight,
            double minWidth, double minHeight)
        {
            double capW = Math.Floor(areaWidth * MarginFill);
            double capH = Math.Floor(areaHeight * MarginFill);
            double minW = Math.Min(minWidth, areaWidth);
            double minH = Math.Min(minHeight, areaHeight);

            double w = width > capW ? Math.Max(minW, capW) : width;
            double h = height > capH ? Math.Max(minH, capH) : height;
            return (areaX + (areaWidth - w) / 2, areaY + (areaHeight - h) / 2, w, h);
        }

        /// <summary>
        /// The WM_GETMINMAXINFO minimum tracking size along one axis, in device pixels: the DIP
        /// minimum scaled by the window's DPI, but never more than the monitor's work area, so a
        /// window on a small or heavily scaled screen can still be resized to fit it.
        /// Returns <paramref name="current"/> when there is no usable minimum.
        /// </summary>
        public static int MinTrackPixels(double minDip, double dpiScale, int workAreaPixels, int current)
        {
            if (!(minDip > 0) || double.IsInfinity(minDip) || !(dpiScale > 0) || workAreaPixels <= 0)
                return current;
            int px = (int)Math.Ceiling(minDip * dpiScale);
            return Math.Max(current, Math.Min(px, workAreaPixels));
        }
    }
}
