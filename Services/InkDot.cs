using System;
using System.Collections.Generic;
using PdfSharpCore.Drawing;

namespace TDPdf.Services
{
    /// <summary>
    /// Pen dots: a click, or a stroke too small to be a line, with the Draw tool (upstream
    /// KillerPDF v1.8.72). It used to be thrown away on mouse-up; now it is kept as a round dot
    /// one stroke-width across, on screen and in the saved PDF alike.
    /// </summary>
    /// <remarks>
    /// The obvious way to draw a dot — a zero-length segment with round caps — is exactly the case
    /// PDF viewers disagree on: the spec leaves a degenerate subpath's caps to the implementation,
    /// so the same file shows a dot in one viewer and nothing in the next. (WPF does the same with
    /// a single-point Polyline: it draws nothing.) So a dot is never stroked. It is a FILLED circle
    /// of diameter = stroke width, which every renderer agrees on, and the on-screen render does
    /// the same with an Ellipse.
    ///
    /// Nothing here knows about WPF, for the reason MeasureGeometry gives: tests/PdfCore compiles
    /// this file and runs on macOS and Linux. See tests/PdfCore/InkDots.cs.
    /// </remarks>
    internal static class InkDot
    {
        /// <summary>
        /// A stroke whose points all fit inside a square this many canvas units across is a dot.
        /// Anything longer is a genuine (if short) segment, whose round caps render reliably.
        /// </summary>
        internal const double MaxExtent = 1.0;

        /// <summary>
        /// True when <paramref name="points"/> is a dot — one point, or several that never left a
        /// <see cref="MaxExtent"/> square — and if so where its centre is. False for no points.
        /// </summary>
        internal static bool TryGetDot(IEnumerable<(double X, double Y)> points, out double cx, out double cy)
        {
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            int count = 0;
            foreach (var (x, y) in points)
            {
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                count++;
            }
            cx = (minX + maxX) / 2;
            cy = (minY + maxY) / 2;
            // The count is not optional: with no points the extremes stay at ±infinity, and
            // -inf - (+inf) is -inf, which passes the extent test below.
            return count > 0 && maxX - minX <= MaxExtent && maxY - minY <= MaxExtent;
        }

        /// <summary>
        /// Burns a dot into a page: a filled circle of <paramref name="diameter"/> centred on
        /// (<paramref name="cx"/>, <paramref name="cy"/>), all in the page's drawing units.
        /// </summary>
        internal static void Burn(XGraphics gfx, XColor color, double cx, double cy, double diameter)
        {
            double r = diameter / 2;
            gfx.DrawEllipse(new XSolidBrush(color), cx - r, cy - r, diameter, diameter);
        }
    }
}
