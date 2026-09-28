using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf
{
    /// <summary>
    /// One image's placement on a page, as FRACTIONS of the page AS RENDERED (the visible box, with
    /// /Rotate applied) with a top-left origin, so a single cached set serves every render
    /// resolution. Produced by <see cref="TDPdf.Services.PdfImages.GetFracRects"/> and consumed by
    /// <c>MainWindow.InvertBgraInPlaceExcept</c> (DocInvert.cs). Declared here, beside its producer,
    /// rather than beside that WPF consumer so tests/PdfCore can compile the real producer and
    /// check its boxes against a PDFium render.
    /// </summary>
    internal readonly record struct FracRect(double L, double T, double R, double B);
}

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
        /// The page's image bounding boxes as fractions of the page AS RENDERED, top-left origin.
        /// Two properties matter to the callers:
        ///
        ///  * Fractional, so ONE cached set serves every render resolution (the primary tile, the
        ///    grid tiles, the continuous base pass and its hi-res re-sharpen all rasterize the same
        ///    page at different pixel sizes).
        ///  * In the DISPLAYED frame — the visible box (CropBox clipped to the MediaBox) with
        ///    /Rotate applied — because that is the buffer the inversion runs over. Every render
        ///    site sizes it from Docnet's page reader and calls FPDF_RenderPageBitmap with rotate 0,
        ///    which draws the page with its own /Rotate already applied, and nothing in TDPdf turns
        ///    the pixels afterwards.
        ///
        /// That makes this a plain scale and a y flip, with no crop or rotation table, for the same
        /// reason <see cref="PdfPageGeometry.CanvasRectToTextFrame"/> is one: PdfPig 0.1.14 reports
        /// IMAGE boxes in that same displayed frame as its words (crop origin subtracted, /Rotate —
        /// inherited or not — applied, y up), and Page.Width/Height are the displayed size.
        /// Routing the box through the user-space table would move it by the crop inset and turn
        /// it a quarter turn. tests/PdfCore NightModeImages.cs renders cropped pages at every
        /// quarter turn and pins both the convention and the carve-out against PDFium's pixels.
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
                // Not normalized: on a 90 / 270 page PdfPig hands image boxes back with Left > Right
                // (90) or Bottom > Top (270), and a content stream can place an image with a negative
                // scale on any page. Take whichever edge is actually smaller — without this every
                // picture on a turned page is dropped as degenerate and gets inverted.
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
