using System;
using UglyToad.PdfPig.Core;

namespace TDPdf.Services
{
    /// <summary>
    /// The axis-aligned extent of a PdfPig box, in the frame PdfPig reports it in.
    /// </summary>
    /// <remarks>
    /// PdfPig's <c>Left</c>/<c>Right</c>/<c>Top</c>/<c>Bottom</c> are only valid for an unrotated
    /// box. Upside-down text has Top below Bottom, mirrored text has Right left of Left, and
    /// rotated text has neither, so code that read them raw grouped no lines and built 1px boxes
    /// (upstream KillerPDF #437, #452, #453). The min and max over the four corners is right for
    /// every orientation.
    /// </remarks>
    internal static class PdfBoxExtent
    {
        internal static (double Left, double Bottom, double Right, double Top) Of(PdfRectangle r)
        {
            double l = Math.Min(Math.Min(r.TopLeft.X, r.TopRight.X), Math.Min(r.BottomLeft.X, r.BottomRight.X));
            double rr = Math.Max(Math.Max(r.TopLeft.X, r.TopRight.X), Math.Max(r.BottomLeft.X, r.BottomRight.X));
            double b = Math.Min(Math.Min(r.TopLeft.Y, r.TopRight.Y), Math.Min(r.BottomLeft.Y, r.BottomRight.Y));
            double t = Math.Max(Math.Max(r.TopLeft.Y, r.TopRight.Y), Math.Max(r.BottomLeft.Y, r.BottomRight.Y));
            return (l, b, rr, t);
        }

        internal static double LeftOf(PdfRectangle r) => Of(r).Left;
        internal static double RightOf(PdfRectangle r) => Of(r).Right;
        internal static double TopOf(PdfRectangle r) => Of(r).Top;
        internal static double BottomOf(PdfRectangle r) => Of(r).Bottom;
    }
}
