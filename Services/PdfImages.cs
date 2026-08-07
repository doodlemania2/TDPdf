using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf.Services
{
    // ============================================================
    // Image placement extraction for the display-only night mode
    // (upstream KillerPDF #135 follow-up: pictures keep their real
    // colors while the rest of the page inverts).
    //
    // Pure functions over an ALREADY-OPEN PdfPig document: the caller
    // owns the open and the dispose. That is deliberate — TDPdf works
    // against a temp copy of the file, and the save path swaps that
    // file out from under the viewer. A PdfPig handle held here for
    // the life of the document would keep the temp file locked and
    // break the swap, so the render loops open one document, fill
    // every page they need, and dispose it before returning.
    //
    // PdfPig is TDPdf's text/geometry reader (see CLAUDE.md): the same
    // library that already backs search, drag-select and the inline
    // text editor. Docnet/PDFium rasterizes; it does not report where
    // the images sit.
    // ============================================================
    internal static class PdfImages
    {
        /// <summary>
        /// The page's image bounding boxes as fractions of the UNROTATED page, top-left origin.
        /// PdfPig reports PDF points with a bottom-left origin, so the y axis is flipped here —
        /// the same flip the annotation pipeline uses. Two properties matter to the callers:
        ///
        ///  * Fractional, so ONE cached set serves every render resolution (the primary tile, the
        ///    grid tiles, the continuous base pass and its hi-res re-sharpen all rasterize the same
        ///    page at different pixel sizes).
        ///  * Unrotated, because the render sites apply the inversion BEFORE any pixel-buffer
        ///    rotation, which keeps these boxes in the space PdfPig measured them in.
        ///
        /// <paramref name="pageIndex"/> is 0-based; PdfPig's GetPage is 1-based.
        /// Returns an empty array for a missing or degenerate page, which the callers read as
        /// "carve nothing out" — i.e. the plain full-page inversion.
        /// </summary>
        internal static FracRect[] GetFracRects(PdfPigDoc doc, int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) return [];

            var page = doc.GetPage(pageIndex + 1);
            double pw = page.Width, ph = page.Height;
            if (!(pw > 0) || !(ph > 0)) return [];   // also rejects NaN

            var list = new List<FracRect>();
            foreach (var img in page.GetImages())
            {
                var b = img.BoundingBox;
                double l = b.Left / pw, r = b.Right / pw;
                double t = (ph - b.Top) / ph, bo = (ph - b.Bottom) / ph;
                // A PdfRectangle is normalized in the common case, but a content stream can place an
                // image with a negative scale; take whichever edge is actually smaller.
                if (r < l) (l, r) = (r, l);
                if (bo < t) (t, bo) = (bo, t);
                if (!double.IsFinite(l) || !double.IsFinite(r)
                    || !double.IsFinite(t) || !double.IsFinite(bo)) continue;
                l = Clamp01(l); r = Clamp01(r);
                t = Clamp01(t); bo = Clamp01(bo);
                if (r - l <= 0 || bo - t <= 0) continue;   // degenerate, or clamped fully off-page
                list.Add(new FracRect(l, t, r, bo));
            }
            return list.ToArray();
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    }
}
