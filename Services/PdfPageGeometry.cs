using System;
using PdfSharpCore.Drawing;
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
    /// testing hardest. See tests/PdfCore: each quarter turn is RENDERED through PDFium and the
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
        /// That last failure is caught rather than shipped — <see cref="PdfRedaction.Apply"/> verifies the output
        /// and refuses to write a file when marked text survives — but "safely refuses every time"
        /// is not a working feature, so the geometry is pinned by tests that render each quarter
        /// turn through PDFium and map the ink back. See tests/PdfCore.
        ///
        /// The corners are mapped individually and re-normalised, because every quarter turn except
        /// 0 swaps or flips at least one axis.
        ///
        /// This is the one place the table is written in the canvas-to-PDF direction;
        /// <see cref="RectToCanvas"/> is the other direction and everything else goes through one
        /// of the two. They are kept side by side, and the tests assert that composing them is the
        /// identity, precisely because an inverse written independently is an inverse until someone
        /// edits one of them.
        ///
        /// Both /Rotate and the page boxes are INHERITABLE attributes: a document is entitled to
        /// set them once on the page tree and never on a page. PdfSharpCore's own accessors read
        /// the page dictionary alone, so they are resolved here by walking /Parent.
        /// </remarks>
        internal static PdfiumInterop.PdfRect CanvasRectToPdf(
            PdfPage page, double x, double y, double w, double h, double renderW, double renderH)
        {
            var box = VisibleBox(page);
            int rotate = Rotation(page);

            var a = VisualFractionToPdf(box, rotate, x / renderW, y / renderH);
            var b = VisualFractionToPdf(box, rotate, (x + w) / renderW, (y + h) / renderH);

            return new PdfiumInterop.PdfRect(
                Left: Math.Min(a.X, b.X), Bottom: Math.Min(a.Y, b.Y),
                Right: Math.Max(a.X, b.X), Top: Math.Max(a.Y, b.Y));
        }

        /// <summary>
        /// The canvas-to-PDF table itself: a point on the page AS DISPLAYED, given as fractions of
        /// the displayed width (<paramref name="u"/>, from the left) and height (<paramref name="v"/>,
        /// DOWN from the top), to PDF user space.
        /// </summary>
        /// <remarks>
        /// Factored out so <see cref="CanvasRectToPdf"/> and <see cref="TextFrameToPdf(PageBox, int, PdfiumInterop.PdfRect)"/>
        /// share one copy. Both frames are the displayed page and differ only in scale and in which
        /// way y runs, so both reduce to this once expressed as fractions.
        /// </remarks>
        private static (double X, double Y) VisualFractionToPdf(PageBox box, int rotate, double u, double v)
        {
            double bx = box.X, by = box.Y, bw = box.Width, bh = box.Height;
            return rotate switch
            {
                90  => (bx + v * bw,       by + u * bh),
                180 => (bx + (1 - u) * bw, by + v * bh),
                270 => (bx + (1 - v) * bw, by + (1 - u) * bh),
                _   => (bx + u * bw,       by + (1 - v) * bh),
            };
        }

        /// <summary>
        /// Converts a rectangle in PdfPig's TEXT frame — where <c>Page.GetWords()</c> and
        /// <c>Letter.GlyphRectangle</c> put things — into PDF user space, where PDFium reports object
        /// bounds and where every redaction rectangle lives.
        /// </summary>
        /// <param name="box">The page's <see cref="VisibleBox"/> (unrotated, real origin).</param>
        /// <param name="rotation">/Rotate, normalised to 0/90/180/270 (<see cref="Rotation"/>).</param>
        /// <param name="rect">A rectangle in the text frame. Need not be normalised — PdfPig's
        /// boxes on a turned page can have Left &gt; Right — and the result always is.</param>
        /// <remarks>
        /// THE ONE PLACE THIS CONVERSION IS WRITTEN, with <see cref="PdfToTextFrame(PageBox, int, PdfiumInterop.PdfRect)"/>
        /// as its inverse. PdfPig 0.1.14 reports text in the page as displayed: the visible box's
        /// origin subtracted, /Rotate applied, y UP from the bottom-left of the turned page, and
        /// Page.Width/Height the displayed size (tests/PdfCore RegionCopy.cs pins that against a
        /// PDFium render at every quarter turn). That is the canvas frame scaled to points with y
        /// flipped, so a text-frame point (tx, ty) is the displayed-page fraction
        /// (tx / W, 1 - ty / H) and goes through the same table as a canvas point.
        ///
        /// Anything that holds a PdfPig box and a user-space box at once — the post-redaction
        /// verification, search hits turned into redaction marks — must meet through here. Comparing
        /// them raw is right only on an unrotated page whose visible box starts at 0,0, which is
        /// exactly the page every hand-made test fixture is, and so it looks right until a cropped
        /// or turned page arrives.
        /// </remarks>
        internal static PdfiumInterop.PdfRect TextFrameToPdf(PageBox box, int rotation, PdfiumInterop.PdfRect rect)
        {
            var (dw, dh) = rotation is 90 or 270 ? (box.Height, box.Width) : (box.Width, box.Height);
            if (!(dw > 0) || !(dh > 0)) return rect;

            var a = VisualFractionToPdf(box, rotation, rect.Left / dw, 1 - rect.Bottom / dh);
            var b = VisualFractionToPdf(box, rotation, rect.Right / dw, 1 - rect.Top / dh);
            return new PdfiumInterop.PdfRect(
                Left: Math.Min(a.X, b.X), Bottom: Math.Min(a.Y, b.Y),
                Right: Math.Max(a.X, b.X), Top: Math.Max(a.Y, b.Y));
        }

        /// <summary><see cref="TextFrameToPdf(PageBox, int, PdfiumInterop.PdfRect)"/> for a page.</summary>
        internal static PdfiumInterop.PdfRect TextFrameToPdf(PdfPage page, PdfiumInterop.PdfRect rect)
            => TextFrameToPdf(VisibleBox(page), Rotation(page), rect);

        /// <summary>
        /// The inverse of <see cref="TextFrameToPdf(PageBox, int, PdfiumInterop.PdfRect)"/>: a PDF
        /// user-space rectangle expressed in PdfPig's text frame, so it can be compared with word boxes.
        /// </summary>
        /// <remarks>
        /// Goes through <see cref="RectToCanvas"/> — laid out on a canvas the size of the displayed
        /// page in points, then y flipped — rather than an inverse table of its own, for the same
        /// reason <see cref="PdfRectToCanvas"/> does. The tests assert the round trip.
        /// </remarks>
        internal static PdfiumInterop.PdfRect PdfToTextFrame(PageBox box, int rotation, PdfiumInterop.PdfRect rect)
        {
            var (dw, dh) = rotation is 90 or 270 ? (box.Height, box.Width) : (box.Width, box.Height);
            if (!(dw > 0) || !(dh > 0)) return rect;

            var (cx, cy, cw, ch) = RectToCanvas(box, rotation, dw, dh,
                rect.Left, rect.Bottom, rect.Right, rect.Top);
            return new PdfiumInterop.PdfRect(
                Left: cx, Bottom: dh - (cy + ch), Right: cx + cw, Top: dh - cy);
        }

        /// <summary><see cref="PdfToTextFrame(PageBox, int, PdfiumInterop.PdfRect)"/> for a page.</summary>
        internal static PdfiumInterop.PdfRect PdfToTextFrame(PdfPage page, PdfiumInterop.PdfRect rect)
            => PdfToTextFrame(VisibleBox(page), Rotation(page), rect);

        /// <summary>
        /// The inverse of <see cref="CanvasRectToPdf"/>: where a PDF-space rectangle lands in the
        /// rendered page image.
        /// </summary>
        /// <remarks>
        /// Used by the rasteriser to paint over exactly the areas the object pass would have
        /// removed. It goes through <see cref="RectToCanvas"/> — the same table the link and
        /// form-field overlays use — rather than carrying an inverse of its own, so the two
        /// directions cannot drift apart. The round trip is asserted in the tests as well.
        ///
        /// Returned in image pixels, clamped to the image, with y measured DOWN from the top.
        /// </remarks>
        internal static (int X, int Y, int W, int H) PdfRectToCanvas(
            PdfPage page, PdfiumInterop.PdfRect rect, double renderW, double renderH)
        {
            var (cx, cy, cw, ch) = RectToCanvas(
                VisibleBox(page), Rotation(page), renderW, renderH,
                rect.Left, rect.Bottom, rect.Right, rect.Top);

            // Outward rounding, then clamp to the image. A redaction rectangle that lands half a
            // pixel short leaves a sliver of the original showing, and half a pixel of a 200 dpi
            // scan is still readable when it is the top of a digit.
            int x0 = (int)Math.Floor(Math.Clamp(cx, 0, renderW));
            int y0 = (int)Math.Floor(Math.Clamp(cy, 0, renderH));
            int x1 = (int)Math.Ceiling(Math.Clamp(cx + cw, 0, renderW));
            int y1 = (int)Math.Ceiling(Math.Clamp(cy + ch, 0, renderH));

            return (x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        }

        /// <summary>
        /// Converts a rectangle on the rendered page into PdfPig's TEXT frame — the frame
        /// <c>Page.GetWords()</c> reports word boxes in — as opposed to PDF user space, which is
        /// what <see cref="CanvasRectToPdf"/> produces.
        /// </summary>
        /// <param name="x">Rectangle left, in rendered-image units, from the left.</param>
        /// <param name="y">Rectangle top, in rendered-image units, DOWN from the top.</param>
        /// <param name="w">Rectangle width.</param>
        /// <param name="h">Rectangle height.</param>
        /// <param name="renderW">Width of the rendered page image.</param>
        /// <param name="renderH">Height of the rendered page image.</param>
        /// <param name="textPageW">PdfPig's <c>Page.Width</c>.</param>
        /// <param name="textPageH">PdfPig's <c>Page.Height</c>.</param>
        /// <remarks>
        /// The two are NOT the same space, and mixing them is silent: PdfPig (0.1.14, pinned by
        /// tests/PdfCore RegionCopy.cs) reports words in the page AS DISPLAYED — the CropBox origin
        /// already subtracted and /Rotate already applied, y up from the bottom-left of the turned
        /// page — and its Page.Width/Height are that displayed size. PDFium renders exactly that
        /// view, so from the canvas it is a plain scale and a Y flip; running the rectangle through
        /// the user-space table instead moves it by the crop inset and turns it a quarter turn.
        /// </remarks>
        internal static PdfiumInterop.PdfRect CanvasRectToTextFrame(
            double x, double y, double w, double h,
            double renderW, double renderH, double textPageW, double textPageH)
        {
            double sx = renderW > 0 ? textPageW / renderW : 0;
            double sy = renderH > 0 ? textPageH / renderH : 0;
            double yA = textPageH - y * sy;
            double yB = textPageH - (y + h) * sy;
            double xA = x * sx, xB = (x + w) * sx;
            return new PdfiumInterop.PdfRect(
                Left: Math.Min(xA, xB), Bottom: Math.Min(yA, yB),
                Right: Math.Max(xA, xB), Top: Math.Max(yA, yB));
        }

        /// <summary>
        /// Whether a box — a PdfPig word, typically — belongs to <paramref name="region"/>: its
        /// CENTRE has to lie inside. Both must be in the same frame (see
        /// <see cref="CanvasRectToTextFrame"/>).
        /// </summary>
        /// <remarks>
        /// The centre rather than any overlap because a marquee dragged along a line of text
        /// clips the ascenders of the line below; an overlap test would copy that line too. The
        /// centre also survives PdfPig's letter boxes on a turned page, whose Left can exceed their
        /// Right (and Bottom their Top).
        /// </remarks>
        internal static bool ContainsCenter(
            PdfiumInterop.PdfRect region, double left, double bottom, double right, double top)
        {
            double cx = (left + right) / 2.0;
            double cy = (bottom + top) / 2.0;
            return cx >= region.Left && cx <= region.Right && cy >= region.Bottom && cy <= region.Top;
        }

        /// <summary>The page's size as displayed, in points — width and height swapped on a quarter turn.</summary>
        internal static (double W, double H) DisplaySize(PdfPage page)
        {
            var box = VisibleBox(page);
            return Rotation(page) is 90 or 270 ? (box.Height, box.Width) : (box.Width, box.Height);
        }

        /// <summary>
        /// The exact inverse of <see cref="RectToCanvas"/>, expressed as a matrix to PREPEND to an
        /// <see cref="XGraphics"/> transform: it maps VISUAL-frame points — canvas coordinates scaled to
        /// points, top-left origin, y down, laid out on the box PDFium actually rendered with /Rotate
        /// already applied — onto the frame XGraphics draws in. Prepend it and every subsequent draw call
        /// can keep passing canvas-scaled coordinates unchanged. Null when there is nothing to apply.
        /// </summary>
        /// <param name="rotation">Page /Rotate, already normalized to 0/90/180/270.</param>
        /// <param name="box">The rendered page box from <see cref="VisibleBox"/> (UNROTATED, and
        /// with its real origin — a /CropBox inset from or offset within the /MediaBox is why the
        /// mapping is not simply a rotation about (0,0)).</param>
        /// <param name="pageHeightPt">
        /// <c>page.Height.Point</c> — the height XGraphics flips about: its Initialize builds
        /// DefaultViewMatrix = [1 0 0 -1 0 pageHeight] from the page size, so a draw at (X, Y) lands at
        /// user-space (X, pageHeightPt - Y). It is passed in rather than derived because PdfSharpCore
        /// reports the SWAPPED media-box dimensions for a page whose /Rotate is 90/270 (PdfPage's
        /// dictionary ctor sets _orientation = Landscape), so "page height" there is really the visual
        /// height. Every case below is written as "pageHeightPt minus the user-space y we want", so the
        /// value cancels out of the result: a page whose /MediaBox is unreadable — the empty [0 0 0 0]
        /// the lazy getter plants — still burns in the right place.
        /// </param>
        internal static XMatrix? VisualToXGraphics(int rotation, PageBox box, double pageHeightPt)
        {
            // Inverting RectToCanvas point-by-point gives visual (vx, vy) -> PDF user space:
            //    0 : (box.X + vx,            box.Y + box.Height - vy)
            //   90 : (box.X + vy,            box.Y + vx)
            //  180 : (box.X + box.Width - vx, box.Y + vy)
            //  270 : (box.X + box.Width - vy, box.Y + box.Height - vx)
            // XGraphics then applies (X, Y) -> (X, pageHeightPt - Y), so this matrix has to produce
            // X = user x and Y = pageHeightPt - user y. XMatrix is (m11, m12, m21, m22, dx, dy) with
            // x' = x*m11 + y*m21 + dx and y' = x*m12 + y*m22 + dy.
            double atTop    = pageHeightPt - box.Top;   // Y for a user-space y at the box's top edge
            double atBottom = pageHeightPt - box.Y;     // ...and at its bottom edge
            switch (rotation)
            {
                case 90:  return new XMatrix(0, -1, 1, 0, box.X,     atBottom);
                case 180: return new XMatrix(-1, 0, 0, -1, box.Right, atBottom);
                case 270: return new XMatrix(0, 1, -1, 0, box.Right, atTop);
                default:
                    // Unrotated page whose rendered box is the whole media box at the origin: the
                    // matrix is the identity XGraphics already applies, so emit nothing and keep the
                    // content stream byte-identical to what earlier builds wrote.
                    return box.X == 0 && atTop == 0 ? null : new XMatrix(1, 0, 0, 1, box.X, atTop);
            }
        }

        /// <summary>
        /// Everything needed to draw onto a PdfSharpCore page in the coordinates of a PDFium raster
        /// of it: the pixel → visual-point scale, and the matrix to PREPEND to the XGraphics
        /// transform (<see cref="VisualToXGraphics"/>; null when it would be the identity).
        /// </summary>
        /// <param name="page">The page being drawn on.</param>
        /// <param name="renderW">Width of the raster, in pixels.</param>
        /// <param name="renderH">Height of the raster, in pixels.</param>
        /// <remarks>
        /// PDFium rasterises the VISIBLE box with /Rotate applied, so a pixel position maps to a
        /// visual-frame point by scaling against <see cref="DisplaySize"/> — never against
        /// page.Width/Height, which are MediaBox-derived, assume a (0,0) origin and read /Rotate
        /// only from the page's own dictionary. After <c>gfx.MultiplyTransform(matrix, Prepend)</c>,
        /// drawing at (px * Sx, py * Sy) lands exactly on raster pixel (px, py).
        /// </remarks>
        internal static (double Sx, double Sy, XMatrix? VisualToPage) RasterToXGraphics(
            PdfPage page, double renderW, double renderH)
        {
            var (dw, dh) = DisplaySize(page);
            double sx = renderW > 0 ? dw / renderW : 0;
            double sy = renderH > 0 ? dh / renderH : 0;
            // page.Height is the one exception to this file's "raw entries only" rule, and a safe
            // one: it must be the SAME value XGraphics.FromPdfPage flips about, which reads it
            // itself regardless, and VisualToXGraphics cancels it out of the result.
            return (sx, sy, VisualToXGraphics(Rotation(page), VisibleBox(page), page.Height.Point));
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
