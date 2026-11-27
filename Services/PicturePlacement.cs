namespace TDPdf.Services
{
    /// <summary>
    /// A PDF transformation matrix, <c>[a b c d e f]</c>, as a page object carries it: the unit square
    /// of the picture maps to <c>x' = a x + c y + e</c>, <c>y' = b x + d y + f</c> in page user space.
    /// </summary>
    internal readonly record struct Affine(double A, double B, double C, double D, double E, double F)
    {
        /// <summary>The page-space box the unit square lands in (the picture's bounds before any clip).</summary>
        internal PdfiumInterop.PdfRect Bounds()
        {
            double x0 = E, y0 = F;
            double x1 = A + E, y1 = B + F;
            double x2 = C + E, y2 = D + F;
            double x3 = A + C + E, y3 = B + D + F;
            return new PdfiumInterop.PdfRect(
                Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)), Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)),
                Math.Max(Math.Max(x0, x1), Math.Max(x2, x3)), Math.Max(Math.Max(y0, y1), Math.Max(y2, y3)));
        }

        /// <summary>Negative when the picture is mirrored (an odd number of flips).</summary>
        internal double Determinant => A * D - B * C;

        internal bool IsMirrored => Determinant < 0;
    }

    /// <summary>
    /// Pure maths for moving, resizing and replacing a picture on a page. Everything works in PDF user
    /// space (points, y up) and canvas boxes are plain <c>(X, Y, W, H)</c> tuples, so the whole of it is
    /// testable without WPF.
    /// </summary>
    /// <remarks>
    /// One idea carries it: moving and resizing are the same operation. Both say "the picture's
    /// bounding box used to be <c>old</c> and is now <c>new</c>", and the matrix follows by scaling
    /// about the page origin and translating. That keeps every sign in the matrix, so a mirrored
    /// picture stays mirrored and a rotated one stays rotated, with no special case for either.
    /// </remarks>
    internal static class PicturePlacement
    {
        /// <summary>Smallest a picture may be dragged to on the canvas, in canvas DIPs.</summary>
        internal const double MinCanvasSize = 8;

        /// <summary>
        /// The matrix that makes a picture drawn with <paramref name="m"/> fill <paramref name="to"/>
        /// instead of <paramref name="from"/> (its current bounds). Signs are never touched, so flips
        /// survive.
        /// </summary>
        internal static Affine FitBounds(Affine m, PdfiumInterop.PdfRect from, PdfiumInterop.PdfRect to)
        {
            double fw = from.Right - from.Left, fh = from.Top - from.Bottom;
            double sx = fw > 1e-9 ? (to.Right - to.Left) / fw : 1;
            double sy = fh > 1e-9 ? (to.Top - to.Bottom) / fh : 1;
            // x' = sx * (x - from.Left) + to.Left, applied after the matrix.
            double tx = to.Left - sx * from.Left;
            double ty = to.Bottom - sy * from.Bottom;
            return new Affine(m.A * sx, m.B * sy, m.C * sx, m.D * sy, m.E * sx + tx, m.F * sy + ty);
        }

        /// <summary>
        /// Where a picture's bounds land when its canvas box is dragged by (<paramref name="dx"/>,
        /// <paramref name="dy"/>) canvas units. The size is exactly the old size, whatever the page's
        /// rotation or crop, so a plain move can never change it by a rounding error.
        /// </summary>
        internal static PdfiumInterop.PdfRect MovedBounds(
            PdfPageGeometry.PageBox box, int rotation, double renderW, double renderH,
            PdfiumInterop.PdfRect old, double dx, double dy)
        {
            var (cx, cy, cw, ch) = PdfPageGeometry.RectToCanvas(box, rotation, renderW, renderH,
                old.Left, old.Bottom, old.Right, old.Top);
            var before = PdfPageGeometry.CanvasRectToPdf(box, rotation, cx, cy, cw, ch, renderW, renderH);
            var after = PdfPageGeometry.CanvasRectToPdf(box, rotation, cx + dx, cy + dy, cw, ch, renderW, renderH);
            double mx = after.Left - before.Left, my = after.Bottom - before.Bottom;
            return new PdfiumInterop.PdfRect(old.Left + mx, old.Bottom + my, old.Right + mx, old.Top + my);
        }

        /// <summary>The picture's canvas box (X, Y, W, H) for its page-space bounds.</summary>
        internal static (double X, double Y, double W, double H) ToCanvas(
            PdfPageGeometry.PageBox box, int rotation, double renderW, double renderH, PdfiumInterop.PdfRect b)
            => PdfPageGeometry.RectToCanvas(box, rotation, renderW, renderH, b.Left, b.Bottom, b.Right, b.Top);

        /// <summary>The page-space bounds for a canvas box.</summary>
        internal static PdfiumInterop.PdfRect ToPdf(
            PdfPageGeometry.PageBox box, int rotation, double renderW, double renderH,
            (double X, double Y, double W, double H) c)
            => PdfPageGeometry.CanvasRectToPdf(box, rotation, c.X, c.Y, c.W, c.H, renderW, renderH);

        /// <summary>The four corner handles of a picture's box.</summary>
        internal enum Corner { TopLeft, TopRight, BottomRight, BottomLeft }

        /// <summary>
        /// The canvas box after dragging <paramref name="corner"/> by (<paramref name="dx"/>,
        /// <paramref name="dy"/>), with the opposite corner fixed. With <paramref name="keepAspect"/>
        /// (the default gesture; Shift frees it) the box keeps the proportions it started with and
        /// follows whichever axis the pointer moved further. It never goes below
        /// <see cref="MinCanvasSize"/> and never turns inside out.
        /// </summary>
        internal static (double X, double Y, double W, double H) ResizeCanvasBox(
            (double X, double Y, double W, double H) start, Corner corner, double dx, double dy, bool keepAspect)
        {
            bool left = corner is Corner.TopLeft or Corner.BottomLeft;
            bool top = corner is Corner.TopLeft or Corner.TopRight;

            double w = start.W + (left ? -dx : dx);
            double h = start.H + (top ? -dy : dy);
            w = Math.Max(MinCanvasSize, w);
            h = Math.Max(MinCanvasSize, h);

            if (keepAspect && start.W > 0 && start.H > 0)
            {
                double s = Math.Max(w / start.W, h / start.H);
                s = Math.Max(s, Math.Max(MinCanvasSize / start.W, MinCanvasSize / start.H));
                w = start.W * s;
                h = start.H * s;
            }

            double x = left ? start.X + start.W - w : start.X;
            double y = top ? start.Y + start.H - h : start.Y;
            return (x, y, w, h);
        }

        /// <summary>
        /// The matrix for a replacement picture of <paramref name="pxW"/> x <paramref name="pxH"/> pixels
        /// standing in for one drawn with <paramref name="old"/>: the same width along the same
        /// direction, the height that keeps the new picture's proportions, and the same top edge, so the
        /// new picture hangs from where the old one did. Flips and turns carry over.
        /// </summary>
        internal static Affine ReplacementMatrix(Affine old, int pxW, int pxH)
        {
            double wLen = Math.Sqrt(old.A * old.A + old.B * old.B);
            double hLen = Math.Sqrt(old.C * old.C + old.D * old.D);
            if (pxW <= 0 || pxH <= 0 || wLen < 1e-9 || hLen < 1e-9) return old;

            double newHLen = wLen * pxH / pxW;
            double k = newHLen / hLen;
            double c = old.C * k, d = old.D * k;
            // The edge at y = 1 in picture space stays where it was: (e + c, f + d) before and after.
            return new Affine(old.A, old.B, c, d, old.E + old.C - c, old.F + old.D - d);
        }

        /// <summary>The page-space box moved by (dx, dy) points, size untouched.</summary>
        internal static PdfiumInterop.PdfRect Translate(PdfiumInterop.PdfRect r, double dx, double dy)
            => new(r.Left + dx, r.Bottom + dy, r.Right + dx, r.Top + dy);
    }

    /// <summary>
    /// The decision whether a picture may be edited in place on a page, kept pure so the rule is one
    /// readable function instead of a scatter of conditions in the UI.
    /// </summary>
    internal static class PictureGate
    {
        internal readonly record struct Decision(bool Allowed, string? Reason)
        {
            /// <summary>Every refusal has the same good answer: the cover-and-redraw edit TDPdf has always had.</summary>
            internal bool OfferOverlay => !Allowed;
        }

        /// <summary>
        /// PDFium rewrites a page's whole content stream when a picture changes, and that rewrite drops
        /// non-device colour, gradients, inline images and soft masks (see <see cref="PdfContentInspector"/>).
        /// A picture inside a form object is shared by every use of the form. Both are refused, with
        /// the reason, rather than risking damage elsewhere on the page.
        /// </summary>
        internal static Decision Decide(bool engineCanEdit, ContentHazard hazard, bool insideForm)
        {
            if (!engineCanEdit)
                return new Decision(false, "this build's PDF engine cannot change page content in place");
            if (insideForm)
                return new Decision(false, "this picture belongs to a form object that other parts of the file may share");
            if (hazard != ContentHazard.None)
                return new Decision(false, $"changing it in place would damage {PdfContentInspector.Describe(hazard)} on this page");
            return new Decision(true, null);
        }
    }

    /// <summary>
    /// Reads just enough of a JPEG to say whether its bytes can be handed to the user as they are.
    /// </summary>
    internal static class JpegProbe
    {
        /// <summary>
        /// The number of colour components in the JPEG's frame header (1 grey, 3 colour, 4 CMYK), or null
        /// when the bytes are not a JPEG this can read.
        /// </summary>
        internal static int? Components(ReadOnlySpan<byte> data)
        {
            if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
            int i = 2;
            while (i + 4 <= data.Length)
            {
                if (data[i] != 0xFF) { i++; continue; }
                byte marker = data[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker is 0x00 or 0x01 or (>= 0xD0 and <= 0xD8)) { i += 2; continue; }   // standalone markers
                if (marker == 0xD9) return null;
                int len = (data[i + 2] << 8) | data[i + 3];
                bool isSof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
                if (isSof)
                    return i + 9 < data.Length ? data[i + 9] : null;   // FF Cn, len(2), precision, h(2), w(2), Nf
                if (len < 2) return null;
                i += 2 + len;
            }
            return null;
        }

        /// <summary>True for a JPEG whose pixels any image viewer reads the way the PDF does.</summary>
        internal static bool IsPlainColour(ReadOnlySpan<byte> data) => Components(data) is 1 or 3;
    }
}
