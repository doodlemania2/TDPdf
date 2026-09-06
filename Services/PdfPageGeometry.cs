using System;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;

namespace TDPdf.Services
{
    /// <summary>
    /// The mapping between the page as it is drawn on screen and the page as PDF operators see it.
    /// </summary>
    /// <remarks>
    /// THE SINGLE HOME FOR THIS MAPPING. Four things now depend on getting it right and, more to
    /// the point, on getting it identically: the link overlays, the interactive form-field
    /// overlays, redaction (which turns a rectangle the user dragged into a rectangle of content to
    /// destroy) and the rasteriser (which turns that same rectangle back into pixels to paint out).
    /// Redaction briefly grew a second, independently derived copy of the rotation table; the two
    /// agreed exactly, but two copies is how a later fix lands in one of them and the other quietly
    /// drifts a quarter turn. They were folded together rather than left to find out.
    ///
    /// It is also the only part of all this that is pure geometry, which makes it the part worth
    /// testing hardest. See tests/Redaction: each quarter turn is RENDERED through PDFium and the
    /// ink mapped back, because a mapping checked against a second copy of the same derivation
    /// agrees with itself whichever way round the convention is; and the two directions are checked
    /// to round-trip, because a redaction that blacks out one part of the page and deletes another
    /// looks plausible from either end on its own.
    ///
    /// Every read here goes through the RAW dictionary entries and the /Parent chain. Never
    /// page.MediaBox / page.CropBox / page.Width / page.Rotate: those getters ignore inheritance,
    /// and the box ones PLANT a degenerate [0 0 0 0] into the page dictionary, which then saves to
    /// disk and makes Adobe reject the page. See ReadInheritedPageBox.
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
            double bx = box.X, by = box.Y, bw = box.Width, bh = box.Height;
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
            double bx = box.X, by = box.Y, bw = box.Width, bh = box.Height;
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
            return Rotation(page) is 90 or 270 ? (box.Height, box.Width) : (box.Width, box.Height);
        }

        /// <summary>/Rotate, normalised to 0, 90, 180 or 270.</summary>
        internal static int Rotation(PdfPage page)
        {
            int r = ((InheritedInt(page, "/Rotate") % 360) + 360) % 360;
            // A file is entitled to write 45; PDF readers round to the nearest quarter turn rather
            // than refuse the page, and so does the renderer we are matching.
            return (int)(Math.Round(r / 90.0) * 90) % 360;
        }

        /// <summary>
        /// Reads an inheritable integer attribute, walking the /Parent chain.
        /// </summary>
        /// <remarks>
        /// /Rotate is inheritable exactly like the page boxes (PDF 32000-1 7.7.3.3) and
        /// PdfSharpCore's own <c>PdfPage.Rotate</c> reads only the page's own dictionary, so a
        /// document that sets the angle once on the page tree reports 0 for every page — and every
        /// overlay, redaction rectangle and rasterised page then lands a quarter turn out.
        /// </remarks>
        private static int InheritedInt(PdfDictionary? node, string key)
        {
            for (int depth = 0; node is not null && depth < 32; depth++)
            {
                if (node.Elements.ContainsKey(key))
                {
                    var item = node.Elements[key];
                    if (item is not null and not PdfInteger and not PdfReal) item = Deref(item);
                    if (item is PdfInteger n) return n.Value;
                    if (item is PdfReal d) return (int)d.Value;
                    return 0;
                }
                var parent = node.Elements["/Parent"];
                node = parent is null ? null
                     : parent as PdfDictionary ?? Deref(parent) as PdfDictionary;
            }
            return 0;
        }

        /// <summary>
        /// Resolves an indirect reference to the object it points at, leaving a direct item alone.
        /// </summary>
        /// <remarks>
        /// Reflection over a "Value" property rather than a cast, because the same call has to
        /// handle both a PdfReference (whose Value is the object) and any already-direct item, and
        /// PdfSharpCore exposes no common interface for that. Mirrors MainWindow's DerefItemStatic,
        /// which is the same trick for the same reason.
        /// </remarks>
        private static PdfItem Deref(PdfItem item)
        {
            var valueProp = item.GetType().GetProperty("Value",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (valueProp?.GetValue(item) is PdfObject resolved) return resolved;
            return item;
        }

        /// <summary>
        /// A page box in PDF user space: lower-left origin (<see cref="X"/>, <see cref="Y"/>) plus a
        /// size, always normalized so Width/Height are positive. The origin matters — [0 0 612 792] is
        /// the common case but [9 9 621 801] is legal, and content/annotation coordinates are absolute
        /// in user space, so anything mapping into the rendered bitmap must subtract the box origin
        /// rather than assume (0,0). /Rotate is NOT applied; see Transform.cs VisiblePageSize.
        /// </summary>
        internal readonly record struct PageBox(double X, double Y, double Width, double Height)
        {
            public double Right => X + Width;
            public double Top   => Y + Height;
        }

        /// <summary>
        /// Reads an inheritable page-tree box (/MediaBox or /CropBox) for a page, walking the /Parent
        /// chain. Both are inheritable page attributes (PDF 32000-1 7.7.3.3): they may live on any
        /// ancestor /Pages node instead of the page itself, and our vendored PdfSharpCore never resolves
        /// inheritance (PdfPage.InheritValues / PdfPages.FlattenPageTree have no callers). Returns null
        /// when no node in the chain carries a usable box.
        /// </summary>
        /// <remarks>
        /// CRITICAL: this reads the RAW dictionary entry and must never be "simplified" to
        /// page.MediaBox / page.CropBox / page.Width. Those getters route through
        /// PdfDictionary.GetRectangle(key, create: true), which (a) returns an EMPTY rectangle for a
        /// box that is only inherited — the caller then falls back to some hardcoded page size and every
        /// overlay on the page is misplaced — and (b) PLANTS an empty /MediaBox|/CropBox [0 0 0 0] into
        /// the page dictionary, which saves to disk and makes Adobe reject the page as "dimensions
        /// out-of-range". That is the same lazy-getter trap as the phantom /Outlines (#103) and the
        /// degenerate /CropBox fixed in v1.18.0.0; see ScrubDegeneratePageBoxes.
        ///
        /// The entry can be a parsed PdfArray (as loaded from disk), a PdfRectangle (GetRectangle stores
        /// its conversion back into the dictionary — "this[key] = value" — so one earlier property read
        /// anywhere in the app replaces the array), or an indirect reference to either. Handle all three.
        /// </remarks>
        internal static PageBox? ReadInheritedPageBox(PdfDictionary? node, string key)
        {
            // Depth cap: a malformed file can have a cyclic /Parent chain.
            for (int depth = 0; node is not null && depth < 32; depth++)
            {
                PdfItem? item = node.Elements[key];
                if (item is not null and not PdfArray and not PdfRectangle)
                    item = Deref(item);

                if (item is PdfRectangle pr)
                    return Normalize(pr.X1, pr.Y1, pr.X2, pr.Y2);
                if (item is PdfArray { Elements.Count: 4 } arr)
                    return Normalize(arr.Elements.GetReal(0), arr.Elements.GetReal(1),
                                     arr.Elements.GetReal(2), arr.Elements.GetReal(3));

                var parent = node.Elements["/Parent"];
                node = parent is null ? null
                     : parent as PdfDictionary ?? Deref(parent) as PdfDictionary;
            }
            return null;

            static PageBox Normalize(double x1, double y1, double x2, double y2) =>
                new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        }

        /// <summary>
        /// The page box a renderer actually draws, and therefore the box every overlay and every
        /// canvas↔PDF mapping must use: the /CropBox when present and usable, otherwise the /MediaBox.
        /// Inheritance-aware and origin-preserving. Mirrors PDFium's own CPDF_Page rules — clip the crop
        /// box to the media box, and fall back to US Letter when a page carries no usable box at all —
        /// because Docnet/PDFium produced the bitmap our overlays sit on, so our geometry must agree
        /// with it rather than with some other notion of "the page size".
        /// </summary>
        internal static PageBox VisibleBox(PdfPage page)
        {
            var media = ReadInheritedPageBox(page, "/MediaBox");
            var crop  = ReadInheritedPageBox(page, "/CropBox");

            // Sub-1pt boxes are degenerate (typically a [0 0 0 0] planted by the lazy getter), never a
            // real page; treat them as absent.
            if (crop is { Width: > 1, Height: > 1 } c)
            {
                if (media is { Width: > 1, Height: > 1 } m)
                {
                    double x1 = Math.Max(c.X, m.X), y1 = Math.Max(c.Y, m.Y);
                    double x2 = Math.Min(c.Right, m.Right), y2 = Math.Min(c.Top, m.Top);
                    if (x2 - x1 > 1 && y2 - y1 > 1) return new PageBox(x1, y1, x2 - x1, y2 - y1);
                    return m;   // crop lies outside the media box: bogus, ignore it
                }
                return c;
            }
            if (media is { Width: > 1, Height: > 1 } mb) return mb;

            // No usable box anywhere in the page tree — a malformed document. PDFium, which rendered the
            // bitmap we are aligning to, substitutes US Letter in exactly this case, so match that instead
            // of inventing a size (in particular A4) that the render never used.
            return new PageBox(0, 0, 612, 792);
        }

        /// <summary>
        /// Maps an annotation /Rect — absolute PDF user-space coordinates, bottom-left origin, always
        /// UNROTATED — onto the canvas/bitmap PDFium rendered for the page, which has the page /Rotate
        /// already applied. Shared by the link and form-field overlays so the two can never drift apart.
        /// </summary>
        /// <param name="box">The rendered page box from <see cref="VisibleBox"/> (unrotated).</param>
        /// <param name="rotation">Page /Rotate, already normalized to 0/90/180/270.</param>
        internal static (double cx, double cy, double cw, double ch) RectToCanvas(
            PageBox box, int rotation, double canvasW, double canvasH,
            double rx1, double ry1, double rx2, double ry2)
        {
            if (rx1 > rx2) (rx1, rx2) = (rx2, rx1);
            if (ry1 > ry2) (ry1, ry2) = (ry2, ry1);

            // Re-express the rect relative to the rendered box's lower-left corner, so a box with a
            // non-zero origin (or a CropBox inset from the MediaBox) doesn't shift every overlay off
            // the drawn page. fx/fy are now in [0, box.Width] x [0, box.Height].
            double fx1 = rx1 - box.X, fy1 = ry1 - box.Y;
            double fx2 = rx2 - box.X, fy2 = ry2 - box.Y;
            double pageW = box.Width, pageH = box.Height;

            // For 90/270 the bitmap's axes are swapped: canvasW spans the box's HEIGHT and canvasH
            // its WIDTH, so the box dimension each canvas axis is divided by swaps with it.
            switch (rotation)
            {
                case 90:  // 90 CW: PDF (x,y) -> canvas (y, x); canvas is pageH-wide x pageW-tall
                    return (fy1         / pageH * canvasW,
                            fx1         / pageW * canvasH,
                            (fy2 - fy1) / pageH * canvasW,
                            (fx2 - fx1) / pageW * canvasH);
                case 180: // both axes flipped; the PDF->canvas y-flip cancels out
                    return ((pageW - fx2) / pageW * canvasW,
                            fy1           / pageH * canvasH,
                            (fx2 - fx1)   / pageW * canvasW,
                            (fy2 - fy1)   / pageH * canvasH);
                case 270: // 270 CW: PDF (x,y) -> canvas (pageH - y, pageW - x)
                    return ((pageH - fy2) / pageH * canvasW,
                            (pageW - fx2) / pageW * canvasH,
                            (fy2 - fy1)   / pageH * canvasW,
                            (fx2 - fx1)   / pageW * canvasH);
                default:  // 0 — standard bottom-left PDF -> top-left canvas
                    return (fx1           / pageW * canvasW,
                            (pageH - fy2) / pageH * canvasH,
                            (fx2 - fx1)   / pageW * canvasW,
                            (fy2 - fy1)   / pageH * canvasH);
            }
        }
    }
}
