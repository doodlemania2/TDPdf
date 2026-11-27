using System.IO;
using PdfSharpCore.Pdf;

namespace TDPdf.Services
{
    /// <summary>One picture on a page that can be selected: where it is, how it is placed, what it is made of.</summary>
    /// <param name="ObjectIndex">Its index among the page's objects when it was listed. Edits re-find the
    /// picture by its bounds, so a page that has changed since is noticed rather than edited blindly.</param>
    /// <param name="InForm">True for a picture inside a form object: visible and copyable, never edited in place.</param>
    internal sealed record PagePicture(
        int PageIndex, int ObjectIndex, PdfiumInterop.PdfRect Bounds, Affine Matrix,
        int PixelWidth, int PixelHeight, IReadOnlyList<string> Filters, bool InForm)
    {
        internal bool IsMirrored => Matrix.IsMirrored;

        /// <summary>The picture's bounds, nudged to this box (same size or not).</summary>
        internal PagePicture At(PdfiumInterop.PdfRect bounds) =>
            this with { Bounds = bounds, Matrix = PicturePlacement.FitBounds(Matrix, Bounds, bounds) };
    }

    /// <summary>
    /// Edits the pictures on a page in the file itself: select, move, resize, delete, replace, and read one
    /// back at its own resolution (#244).
    /// </summary>
    /// <remarks>
    /// This replaces the cover-and-redraw overlay as the way to change a picture the PDF already has. The
    /// overlay paints white over the old picture and draws a screen capture of it somewhere else, which is
    /// lossy, leaves the original in the file and shows on any page that is not white. Here the page's own
    /// image object changes, so the original pixels are used unchanged wherever the picture ends up.
    ///
    /// Two rules keep it honest:
    ///   * Every edit goes through <see cref="PictureGate"/> first. PDFium rewrites the page's whole content
    ///     stream when it saves a change and silently drops non-device colour, gradients and soft masks, so
    ///     a page carrying them is refused with the reason and the caller offers the overlay.
    ///   * Every edit is checked in the FINISHED file. A change that did not land is reported, not assumed.
    /// </remarks>
    internal static class PdfImageObjects
    {
        /// <summary>The most pixels a picture may have to be lifted: about 36 megapixels, a 600 dpi A4 scan.</summary>
        internal const long MaxLiftPixels = 36_000_000;

        internal sealed class EditResult
        {
            internal bool Ok { get; set; }
            internal string? Error { get; set; }

            /// <summary>The gate refused: the caller should say why and offer the cover-and-redraw edit.</summary>
            internal bool ShouldOfferOverlay { get; set; }
        }

        internal sealed class LiftResult
        {
            internal string? Error { get; set; }
            /// <summary>The original JPEG bytes, when the picture is a plain JPEG left as it was.</summary>
            internal byte[]? Jpeg { get; set; }
            internal byte[]? Bgra { get; set; }
            internal int Width { get; set; }
            internal int Height { get; set; }
            internal bool Ok => Error is null && Bgra is not null;
        }

        /// <summary>Every picture on a page, last (topmost) last. Empty when the file cannot be read or has none.</summary>
        internal static List<PagePicture> List(string path, int pageIndex)
        {
            var found = PdfiumInterop.ListImageObjects(path, pageIndex);
            var result = new List<PagePicture>();
            if (found is null) return result;
            foreach (var f in found)
                result.Add(new PagePicture(pageIndex, f.ObjectIndex, f.Bounds, f.Matrix, f.PixelWidth, f.PixelHeight, f.Filters, f.InForm));
            return result;
        }

        /// <summary>The topmost picture whose box contains the page-space point, or null.</summary>
        internal static PagePicture? TopmostAt(IReadOnlyList<PagePicture> pictures, double x, double y)
        {
            for (int i = pictures.Count - 1; i >= 0; i--)
            {
                var b = pictures[i].Bounds;
                if (x >= b.Left && x <= b.Right && y >= b.Bottom && y <= b.Top) return pictures[i];
            }
            return null;
        }

        /// <summary>The gate for one picture on one page of the open document.</summary>
        internal static PictureGate.Decision Gate(PdfDocument? open, PagePicture pic)
        {
            ContentHazard hazard = ContentHazard.None;
            if (open is not null && pic.PageIndex >= 0 && pic.PageIndex < open.PageCount)
                hazard = PdfContentInspector.Inspect(open.Pages[pic.PageIndex]);
            return PictureGate.Decide(PdfiumInterop.CanEditPictures, hazard, pic.InForm);
        }

        /// <summary>Reads a picture at its own resolution (JPEG bytes too, when it is a plain JPEG).</summary>
        internal static LiftResult Lift(string path, PagePicture pic)
        {
            var r = new LiftResult();
            if (pic.InForm) { r.Error = "this picture is inside a form object and cannot be lifted"; return r; }
            var lifted = PdfiumInterop.LiftImageObject(path, pic.PageIndex, pic.ObjectIndex, pic.Bounds, MaxLiftPixels);
            if (lifted.Error is not null || lifted.Bgra is null) { r.Error = lifted.Error ?? "this picture cannot be lifted"; return r; }
            r.Bgra = lifted.Bgra;
            r.Jpeg = lifted.Jpeg;
            r.Width = lifted.Width;
            r.Height = lifted.Height;
            return r;
        }

        /// <summary>Moves and/or resizes the picture so it fills <paramref name="newBounds"/>. Keeps flips and turns.</summary>
        internal static EditResult Reposition(
            string srcPath, string destPath, PagePicture pic, PdfiumInterop.PdfRect newBounds, PdfDocument? open)
        {
            var gate = Gate(open, pic);
            if (!gate.Allowed) return Refused(gate);

            var matrix = PicturePlacement.FitBounds(pic.Matrix, pic.Bounds, newBounds);
            return Run(srcPath, destPath, pic, PdfiumInterop.PictureOp.SetMatrix, matrix, null, 0, 0,
                after: (before, now) => now.Any(p => Near(p.Bounds, newBounds, 1.0)) && now.Count == before.Count);
        }

        /// <summary>Takes the picture off the page.</summary>
        internal static EditResult Delete(string srcPath, string destPath, PagePicture pic, PdfDocument? open)
        {
            var gate = Gate(open, pic);
            if (!gate.Allowed) return Refused(gate);

            return Run(srcPath, destPath, pic, PdfiumInterop.PictureOp.Remove, null, null, 0, 0,
                after: (before, now) => now.Count == before.Count - 1);
        }

        /// <summary>
        /// Puts a different picture where this one is, at the same width, hanging from the same top edge,
        /// in the same stacking position. The old image data is not touched, so any other place the file
        /// draws it is unchanged.
        /// </summary>
        internal static EditResult Replace(
            string srcPath, string destPath, PagePicture pic, byte[] bgra, int pixelWidth, int pixelHeight, PdfDocument? open)
        {
            var gate = Gate(open, pic);
            if (!gate.Allowed) return Refused(gate);

            var expected = PicturePlacement.ReplacementMatrix(pic.Matrix, pixelWidth, pixelHeight).Bounds();
            return Run(srcPath, destPath, pic, PdfiumInterop.PictureOp.Replace, null, bgra, pixelWidth, pixelHeight,
                after: (before, now) => now.Count == before.Count && now.Any(p => Near(p.Bounds, expected, 1.5)));
        }

        private static EditResult Refused(PictureGate.Decision gate) =>
            new() { Error = gate.Reason, ShouldOfferOverlay = true };

        private static EditResult Run(
            string srcPath, string destPath, PagePicture pic, PdfiumInterop.PictureOp op,
            Affine? matrix, byte[]? bgra, int pw, int ph,
            Func<List<PagePicture>, List<PagePicture>, bool> after)
        {
            var result = new EditResult();
            string dir = Path.GetDirectoryName(destPath) ?? Path.GetTempPath();
            string staged = Path.Combine(dir, $"tdpdf_pictureedit_{Guid.NewGuid():N}.pdf");
            try
            {
                var before = List(srcPath, pic.PageIndex).Where(p => !p.InForm).ToList();
                var engine = PdfiumInterop.EditImageObject(
                    srcPath, staged, pic.PageIndex, pic.ObjectIndex, pic.Bounds, op, matrix, bgra, pw, ph);
                if (!engine.Ok)
                {
                    result.Error = engine.Error ?? "the picture could not be changed";
                    // A page that has changed under the selection is not the gate's doing: no offer.
                    return result;
                }

                var now = List(staged, pic.PageIndex).Where(p => !p.InForm).ToList();
                if (!after(before, now))
                {
                    result.Error = "the change did not reach the saved page";
                    result.ShouldOfferOverlay = true;
                    return result;
                }

                File.Copy(staged, destPath, overwrite: true);
                result.Ok = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
            finally
            {
                try { if (File.Exists(staged)) File.Delete(staged); } catch { /* temp file */ }
            }
        }

        private static bool Near(PdfiumInterop.PdfRect a, PdfiumInterop.PdfRect b, double tol) =>
            Math.Abs(a.Left - b.Left) <= tol && Math.Abs(a.Bottom - b.Bottom) <= tol
            && Math.Abs(a.Right - b.Right) <= tol && Math.Abs(a.Top - b.Top) <= tol;
    }
}
