using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf.Services
{
    /// <summary>
    /// Applies redactions: removes the content, scrubs the document metadata, and then proves
    /// the result before letting it reach the user's file.
    /// </summary>
    /// <remarks>
    /// The guiding rule for everything here is that a redaction which leaves the bytes in the
    /// file is worse than no redaction at all, because it looks finished. So the pipeline is
    /// deliberately fail-closed: if the verification pass cannot confirm the content is gone,
    /// the destination file is never written and the caller gets an error rather than a
    /// document the user would reasonably believe was safe.
    /// </remarks>
    internal static class PdfRedaction
    {
        internal sealed class Request
        {
            /// <summary>Redaction rectangles in PDF page space (points, origin bottom-left).</summary>
            internal IReadOnlyDictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> RectsByPage { get; init; }
                = new Dictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>>();

            /// <summary>
            /// Remove an object that merely overlaps a rectangle, rather than reporting it.
            /// True over-deletes; false can under-delete. See <see cref="Result.Partial"/>.
            /// </summary>
            internal bool RemovePartialOverlaps { get; init; } = true;

            /// <summary>Clear the Info dictionary and drop the XMP packet.</summary>
            internal bool ScrubMetadata { get; init; } = true;
        }

        internal sealed class Result
        {
            internal bool Ok { get; set; }
            internal string? Error { get; set; }
            internal int ObjectsRemoved { get; set; }
            internal int InvisibleTextRemoved { get; set; }
            internal IReadOnlyList<PdfiumInterop.RedactionOverlap> Partial { get; set; }
                = Array.Empty<PdfiumInterop.RedactionOverlap>();

            /// <summary>Text still sitting inside a redaction rectangle after the fact.</summary>
            /// <remarks>Non-empty means the output was NOT written.</remarks>
            internal IReadOnlyList<string> Survivors { get; set; } = Array.Empty<string>();
        }

        internal static Result Apply(string srcPath, string destPath, Request request)
        {
            var result = new Result();
            string workDir = Path.GetDirectoryName(destPath) ?? Path.GetTempPath();
            string stripped = Path.Combine(workDir, $"tdpdf_redact_{Guid.NewGuid():N}.pdf");
            string scrubbed = Path.Combine(workDir, $"tdpdf_redact_{Guid.NewGuid():N}.pdf");

            try
            {
                // 1. Remove the content.
                var removal = PdfiumInterop.RemoveObjectsIntersecting(
                    srcPath, stripped, request.RectsByPage, request.RemovePartialOverlaps);

                result.ObjectsRemoved = removal.ObjectsRemoved;
                result.InvisibleTextRemoved = removal.InvisibleTextRemoved;
                result.Partial = removal.Partial;
                if (!removal.Ok)
                {
                    result.Error = removal.Error ?? "the content could not be removed";
                    return result;
                }

                // 2. Scrub what lives outside the page content. Redacting a name from the body
                //    while leaving it in the Title is a common and embarrassing miss.
                string verifyPath = stripped;
                if (request.ScrubMetadata)
                {
                    if (TryScrubMetadata(stripped, scrubbed, out string? scrubError))
                        verifyPath = scrubbed;
                    else
                    {
                        result.Error = scrubError ?? "the document metadata could not be scrubbed";
                        return result;
                    }
                }

                // 3. Prove it, on the OUTPUT rather than on intent.
                result.Survivors = FindSurvivingText(verifyPath, request.RectsByPage);
                if (result.Survivors.Count > 0)
                {
                    result.Error =
                        $"redaction could not be confirmed — {result.Survivors.Count} item(s) of text " +
                        "remain inside a redacted area, so the file was not written";
                    return result;
                }

                File.Copy(verifyPath, destPath, overwrite: true);
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
                TryDelete(stripped);
                TryDelete(scrubbed);
            }
        }

        /// <summary>
        /// Clears the Info dictionary and removes the XMP packet.
        /// </summary>
        /// <remarks>
        /// Both are needed. The Info dictionary is what most tools show as Title/Author, but XMP
        /// is a second, independent copy of much the same thing, and clearing only one leaves the
        /// other to be found by anyone who looks.
        ///
        /// Deliberately a full rewrite through PdfSharpCore rather than an incremental update: an
        /// incremental save appends, leaving the pre-redaction revision intact earlier in the file
        /// and reachable by anyone willing to read it.
        /// </remarks>
        private static bool TryScrubMetadata(string srcPath, string destPath, out string? error)
        {
            error = null;
            try
            {
                using var doc = PdfReader.Open(srcPath, PdfDocumentOpenMode.Modify);

                // Every key, not just the well-known ones — producers write custom entries.
                var info = doc.Info;
                foreach (var key in info.Elements.Keys.ToList())
                {
                    try { info.Elements.Remove(key); } catch { /* keep going; one bad key is not fatal */ }
                }

                // The XMP packet, which carries its own copy of title, author, and often a full
                // edit history.
                try { doc.Internals.Catalog.Elements.Remove("/Metadata"); } catch { }

                doc.Save(destPath);
                return File.Exists(destPath);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Text still present inside a redaction rectangle in <paramref name="path"/>.
        /// </summary>
        /// <remarks>
        /// Positional rather than string-matching, deliberately. Searching the output for the
        /// words that used to be there gives false positives the moment a redacted word also
        /// appears legitimately elsewhere ("the", a surname that recurs in a letterhead), and
        /// a verification step that cries wolf gets switched off.
        ///
        /// Asking "does any glyph still sit inside the area the user redacted" has no such
        /// ambiguity. A letter counts as inside when its CENTRE is inside, so a glyph merely
        /// clipping the boundary does not raise a false alarm.
        ///
        /// PdfPig reads page geometry in the same space PDFium reported it — points, origin
        /// bottom-left — so no conversion is involved and there is nothing to get wrong.
        /// </remarks>
        private static IReadOnlyList<string> FindSurvivingText(
            string path, IReadOnlyDictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> rectsByPage)
        {
            var survivors = new List<string>();
            using var doc = PdfPigDoc.Open(path);

            foreach (var (pageIndex, rects) in rectsByPage)
            {
                if (rects is null || rects.Count == 0) continue;
                if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) continue;

                var page = doc.GetPage(pageIndex + 1);   // PdfPig pages are 1-based
                foreach (var word in page.GetWords())
                {
                    var b = word.BoundingBox;
                    double cx = (b.Left + b.Right) / 2.0;
                    double cy = (b.Bottom + b.Top) / 2.0;

                    foreach (var r in rects)
                    {
                        if (cx >= r.Left && cx <= r.Right && cy >= r.Bottom && cy <= r.Top)
                        {
                            survivors.Add(word.Text);
                            break;
                        }
                    }
                    // A handful is enough to prove the failure; no need to enumerate a whole page.
                    if (survivors.Count >= 25) return survivors;
                }
            }
            return survivors;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* temp file; best effort */ }
        }
    }
}
