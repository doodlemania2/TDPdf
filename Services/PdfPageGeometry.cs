using System;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;

namespace TDPdf.Services
{
    /// <summary>
    /// The mapping between the page as it is drawn on screen and the page as PDF operators see it.
    /// </summary>
    /// <remarks>
    /// Its own file because two very different things depend on being able to do this correctly and
    /// identically — redaction, which converts a rectangle the user dragged into a rectangle of
    /// content to destroy, and the rasteriser, which converts that same rectangle back into pixels
    /// to paint over. If those two disagreed, a redaction would black out one part of the page and
    /// delete another.
    ///
    /// It is also the only piece of the redaction feature that is pure geometry, which is what
    /// makes it the piece worth testing hardest. See tests/Redaction: each quarter turn is rendered
    /// through PDFium and the ink mapped back, because a mapping checked only against a second copy
    /// of the same derivation agrees with itself whichever way round it is.
    /// </remarks>
    internal static class PdfPageGeometry
    {
        /// <summary>
        /// Converts a rectangle drawn on the on-screen page image into the PDF user-space rectangle
        /// PDFium reports object bounds in.
        /// </summary>
        /// <param name="page">The page the rectangle was drawn on.</param>
        /// <param name="x">Rectangle left, in rendered-image pixels, measured from the LEFT.</param>
        /// <param name="y">Rectangle top, in rendered-image pixels, measured DOWN from the top.</param>
        /// <param name="w">Rectangle width, in rendered-image pixels.</param>
        /// <param name="h">Rectangle height, in rendered-image pixels.</param>
        /// <param name="renderW">Width of the rendered page image.</param>
        /// <param name="renderH">Height of the rendered page image.</param>
        /// <remarks>
        /// Three things make this more than a scale, and getting any of them wrong points the
        /// redaction at the wrong part of the page:
        ///
        ///   * <b>Origin.</b> The image measures down from the top-left; PDF user space measures up
        ///     from the bottom-left.
        ///   * <b>The visible box.</b> PDFium rasterises the CropBox when a page has one (ours do,
        ///     after the crop tool), not the MediaBox, and neither is obliged to start at 0,0.
        ///   * <b>/Rotate.</b> The image is rotated; object coordinates are not. On a 90-degree page
        ///     the image's x axis runs along the PDF y axis, so a naive mapping lands the rectangle
        ///     in empty space.
        ///
        /// That last failure is caught rather than shipped — <see cref="Apply"/> verifies the output
        /// and refuses to write a file when marked text survives — but "safely refuses every time"
        /// is not a working feature, so the geometry is pinned by tests that render each quarter
        /// turn through PDFium and map the ink back. See tests/Redaction.
        ///
        /// The corners are mapped individually and re-normalised, because every quarter turn except
        /// 0 swaps or flips at least one axis.
        ///
        /// Both /Rotate and the page boxes are INHERITABLE attributes: a document is entitled to
        /// set them once on the page tree and never on a page. PdfSharpCore's own accessors read
        /// the page dictionary alone, so they are resolved here by walking /Parent.
        /// </remarks>
        internal static PdfiumInterop.PdfRect CanvasRectToPdf(
            PdfPage page, double x, double y, double w, double h, double renderW, double renderH)
        {
            var box = VisibleBox(page);
            double bx = box.X1, by = box.Y1;
            double bw = box.X2 - box.X1, bh = box.Y2 - box.Y1;
            int rotate = Rotation(page);

            (double X, double Y) Map(double u, double v) => rotate switch
            {
                90  => (bx + v * bw,       by + u * bh),
                180 => (bx + (1 - u) * bw, by + v * bh),
                270 => (bx + (1 - v) * bw, by + (1 - u) * bh),
                _   => (bx + u * bw,       by + (1 - v) * bh),
            };

            var a = Map(x / renderW, y / renderH);
            var b = Map((x + w) / renderW, (y + h) / renderH);

            return new PdfiumInterop.PdfRect(
                Left: Math.Min(a.X, b.X), Bottom: Math.Min(a.Y, b.Y),
                Right: Math.Max(a.X, b.X), Top: Math.Max(a.Y, b.Y));
        }

        /// <summary>
        /// The inverse of <see cref="CanvasRectToPdf"/>: where a PDF-space rectangle lands in the
        /// rendered page image.
        /// </summary>
        /// <remarks>
        /// Used by the rasteriser to paint over exactly the areas the object pass would have
        /// removed. Deliberately derived from the same rotation table rather than re-derived, so
        /// the two directions cannot drift apart; the round trip is asserted in the tests.
        ///
        /// Returned in image pixels, clamped to the image, with y measured DOWN from the top.
        /// </remarks>
        internal static (int X, int Y, int W, int H) PdfRectToCanvas(
            PdfPage page, PdfiumInterop.PdfRect rect, double renderW, double renderH)
        {
            var box = VisibleBox(page);
            double bx = box.X1, by = box.Y1;
            double bw = box.X2 - box.X1, bh = box.Y2 - box.Y1;
            int rotate = Rotation(page);

            // Inverse of Map() in CanvasRectToPdf, term for term.
            (double U, double V) Unmap(double X, double Y)
            {
                double fx = (X - bx) / bw;      // fraction along the PDF x axis
                double fy = (Y - by) / bh;      // fraction along the PDF y axis
                return rotate switch
                {
                    90  => (fy, fx),
                    180 => (1 - fx, fy),
                    270 => (1 - fy, 1 - fx),
                    _   => (fx, 1 - fy),
                };
            }

            var a = Unmap(rect.Left, rect.Bottom);
            var b = Unmap(rect.Right, rect.Top);

            double u0 = Math.Min(a.U, b.U), u1 = Math.Max(a.U, b.U);
            double v0 = Math.Min(a.V, b.V), v1 = Math.Max(a.V, b.V);

            // Outward rounding. A redaction rectangle that lands half a pixel short leaves a sliver
            // of the original showing, and half a pixel of a 200 dpi scan is still readable when
            // it is the top of a digit.
            int x0 = (int)Math.Floor(Math.Clamp(u0, 0, 1) * renderW);
            int y0 = (int)Math.Floor(Math.Clamp(v0, 0, 1) * renderH);
            int x1 = (int)Math.Ceiling(Math.Clamp(u1, 0, 1) * renderW);
            int y1 = (int)Math.Ceiling(Math.Clamp(v1, 0, 1) * renderH);

            return (x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        }

        /// <summary>The page's size as displayed, in points — width and height swapped on a quarter turn.</summary>
        internal static (double W, double H) DisplaySize(PdfPage page)
        {
            var box = VisibleBox(page);
            double w = box.X2 - box.X1, h = box.Y2 - box.Y1;
            return Rotation(page) is 90 or 270 ? (h, w) : (w, h);
        }

        /// <summary>/Rotate, normalised to 0, 90, 180 or 270.</summary>
        internal static int Rotation(PdfPage page)
        {
            int r = ((InheritedInt(page, "/Rotate") % 360) + 360) % 360;
            // A file is entitled to write 45; PDF readers round to the nearest quarter turn rather
            // than refuse the page, and so does the renderer we are matching.
            return (int)(Math.Round(r / 90.0) * 90) % 360;
        }

        /// <summary>The box PDFium rasterises: the CropBox when there is a usable one, else the MediaBox.</summary>
        /// <remarks>
        /// Falls back to US Letter only when a document declares neither, which is malformed. A
        /// guess is still better than a divide-by-zero here: the verification pass will refuse the
        /// redaction if the guess was wrong, whereas a crash loses the user's work.
        /// </remarks>
        internal static (double X1, double Y1, double X2, double Y2) VisibleBox(PdfPage page)
        {
            var crop = InheritedRect(page, "/CropBox");
            if (crop is { } c && c.X2 - c.X1 > 0 && c.Y2 - c.Y1 > 0) return c;
            var media = InheritedRect(page, "/MediaBox");
            if (media is { } m && m.X2 - m.X1 > 0 && m.Y2 - m.Y1 > 0) return m;
            return (0, 0, 612, 792);
        }

        /// <summary>Reads an inheritable rectangle, normalised so X1&lt;X2 and Y1&lt;Y2.</summary>
        private static (double X1, double Y1, double X2, double Y2)? InheritedRect(PdfPage page, string key)
        {
            if (InheritedItem(page, key) is not PdfArray arr || arr.Elements.Count < 4) return null;
            double[] v = new double[4];
            for (int i = 0; i < 4; i++)
            {
                var item = arr.Elements[i];
                if (item is PdfReference r) item = r.Value;
                v[i] = item switch
                {
                    PdfReal real => real.Value,
                    PdfInteger n => n.Value,
                    _ => double.NaN,
                };
                if (double.IsNaN(v[i])) return null;
            }
            return (Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
        }

        private static int InheritedInt(PdfPage page, string key)
        {
            var item = InheritedItem(page, key);
            if (item is PdfReference r) item = r.Value;
            return item switch { PdfInteger n => n.Value, PdfReal d => (int)d.Value, _ => 0 };
        }

        /// <summary>Walks the page and then its /Parent chain for an inheritable attribute.</summary>
        private static PdfItem? InheritedItem(PdfPage page, string key)
        {
            PdfDictionary? node = page;
            // Depth-capped: a malformed file can point /Parent back at a descendant, and a
            // redaction is not the place to hang on someone else's cycle.
            for (int depth = 0; node is not null && depth < 64; depth++)
            {
                if (node.Elements.ContainsKey(key)) return node.Elements[key];
                var parent = node.Elements["/Parent"];
                if (parent is PdfReference pr) parent = pr.Value;
                node = parent as PdfDictionary;
            }
            return null;
        }
    }
}
