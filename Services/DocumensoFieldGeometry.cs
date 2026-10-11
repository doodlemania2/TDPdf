using System;

namespace TDPdf.Services
{
    /// <summary>
    /// Converts a field rectangle between TDPdf's page canvas and Documenso's percentage frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two frames are the same frame, scaled.</b> TDPdf's canvas is PDFium's render of the
    /// page AS DISPLAYED: the visible box (CropBox clipped to MediaBox, <see cref="PdfPageGeometry.VisibleBox"/>)
    /// with /Rotate applied, origin top-left, y down, fitted into
    /// <c>PdfDocumentService.RenderBoxDip</c>. Documenso v2.18.0 keeps every field as percent of
    /// the page as displayed too, established from its source rather than assumed:
    /// </para>
    /// <list type="bullet">
    /// <item>The signer's browser renders with react-pdf (pdf.js), whose viewport is the
    /// CropBox∩MediaBox with /Rotate applied; fields are positioned in percent of that.</item>
    /// <item>The seal (<c>packages/lib/jobs/definitions/internal/seal-document.handler.ts</c>,
    /// the <c>internalVersion === 2</c> branch every API-created envelope takes) renders the
    /// fields onto an overlay of <c>page.width × page.height</c> and draws it rotated by
    /// <c>page.rotation</c> "to the orientation that the react-pdf renders on the frontend".
    /// <c>@libpdf/core</c> 0.4.2's <c>page.width/height</c> are the effective box (CropBox when it
    /// is smaller than the MediaBox, else MediaBox) with width and height SWAPPED on a 90/270
    /// page — the displayed size.</item>
    /// <item>Its own placeholder detector (<c>packages/lib/server-only/pdf/auto-place-fields.ts</c>)
    /// produces fields as <c>x / page.width * 100</c> with y flipped to top-left — the same frame.</item>
    /// </list>
    /// <para>
    /// So a canvas rect divided by the canvas size is the Documenso rect, at every quarter turn.
    /// tests/PdfCore DocumensoSend.cs pins that by modelling the seal's matrices and checking a
    /// field placed on the canvas of a rotated page lands, in PDF user space, exactly where TDPdf's
    /// own <see cref="PdfPageGeometry.CanvasRectToPdf"/> says the canvas rect is.
    /// </para>
    /// <para>
    /// <b>One Documenso caveat, not compensated here:</b> the v2 seal draws the overlay at user-space
    /// (0,0) and never adds the visible box's origin. On a page whose CropBox/MediaBox does not start
    /// at 0,0 — which includes pages TDPdf's own Crop tool has cropped — the field shows in the right
    /// place in the signer's browser but is burned into the final PDF shifted left/down by that
    /// origin. Compensating here would move it in the browser instead, which is worse (the signer
    /// would sign in the wrong place), so the send dialog warns instead; see
    /// <see cref="HasOffsetOrigin"/>. The test suite pins the offset so a Documenso fix shows up.
    /// </para>
    /// No WPF types: tests/PdfCore compiles this file.
    /// </remarks>
    internal static class DocumensoFieldGeometry
    {
        /// <summary>
        /// The Documenso rect for a rectangle drawn on a page canvas of
        /// <paramref name="canvasW"/> × <paramref name="canvasH"/>. The rectangle is normalised
        /// (a drag up-and-left has negative size) and clipped to the page first, so a drag that
        /// ran off the edge places the part that is on the page. Null when nothing of it is on the
        /// page or the canvas size is unusable.
        /// </summary>
        internal static DocumensoFieldRect? CanvasToPercent(double x, double y, double w, double h,
            double canvasW, double canvasH)
        {
            if (!Finite(x) || !Finite(y) || !Finite(w) || !Finite(h)) return null;
            if (!(canvasW > 0) || !(canvasH > 0) || !Finite(canvasW) || !Finite(canvasH)) return null;

            double l = Math.Min(x, x + w), r = Math.Max(x, x + w);
            double t = Math.Min(y, y + h), b = Math.Max(y, y + h);
            l = Math.Clamp(l, 0, canvasW); r = Math.Clamp(r, 0, canvasW);
            t = Math.Clamp(t, 0, canvasH); b = Math.Clamp(b, 0, canvasH);
            if (r - l <= 0 || b - t <= 0) return null;

            return new DocumensoFieldRect(
                Pct(l / canvasW), Pct(t / canvasH), Pct((r - l) / canvasW), Pct((b - t) / canvasH));
        }

        /// <summary>The inverse: where a Documenso rect sits on a canvas of the given size.</summary>
        internal static (double X, double Y, double W, double H) PercentToCanvas(DocumensoFieldRect r,
            double canvasW, double canvasH)
            => (r.X / 100.0 * canvasW, r.Y / 100.0 * canvasH, r.Width / 100.0 * canvasW, r.Height / 100.0 * canvasH);

        /// <summary>
        /// A field of a sensible default size with its top-left at a clicked canvas point, kept
        /// inside the page — what a click (as opposed to a drag) places. Sizes are fractions of the
        /// page WIDTH, so a field looks the same size on a portrait and a landscape page.
        /// </summary>
        internal static DocumensoFieldRect? DefaultAt(DocumensoFieldType type, double x, double y,
            double canvasW, double canvasH)
        {
            if (!(canvasW > 0) || !(canvasH > 0)) return null;
            var (fw, fh) = type switch
            {
                DocumensoFieldType.Signature or DocumensoFieldType.FreeSignature => (0.28, 0.07),
                DocumensoFieldType.Initials => (0.10, 0.06),
                DocumensoFieldType.Date => (0.18, 0.04),
                _ => (0.30, 0.04),
            };
            double w = Math.Min(fw * canvasW, canvasW), h = Math.Min(fh * canvasW, canvasH);
            double left = Math.Clamp(x, 0, canvasW - w), top = Math.Clamp(y, 0, canvasH - h);
            return CanvasToPercent(left, top, w, h, canvasW, canvasH);
        }

        /// <summary>
        /// True when a page's visible box does not start at user-space 0,0 — the pages Documenso
        /// v2.18's seal places fields on offset by that origin (see the class remarks).
        /// Half a point of tolerance: real boxes carry float noise.
        /// </summary>
        internal static bool HasOffsetOrigin(double boxX, double boxY)
            => Math.Abs(boxX) > 0.5 || Math.Abs(boxY) > 0.5;

        // Rounded to 4 decimals: far below a pixel on any page (0.0001 % of A4 is 0.0008 pt) and it
        // keeps the JSON short and stable. Clamped because the API rejects anything outside 0–100.
        private static double Pct(double fraction) => Math.Clamp(Math.Round(fraction * 100.0, 4), 0, 100);

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
