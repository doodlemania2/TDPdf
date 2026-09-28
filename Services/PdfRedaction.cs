using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
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

            /// <summary>
            /// Pages to redact by rasterising rather than by removing objects.
            /// </summary>
            /// <remarks>
            /// Never chosen here. Rasterising costs the page its text — no search, no selection, no
            /// screen reader — and that is the user's call to make, not the pipeline's. The two
            /// situations that need it (a mark inside a scan, a page the content inspector flags)
            /// are both reported to the caller so it can ask. See <see cref="PdfPageRasterizer"/>.
            /// </remarks>
            internal IReadOnlyCollection<int> RasterizePages { get; init; } = Array.Empty<int>();

            /// <summary>Resolution for <see cref="RasterizePages"/>.</summary>
            internal int RasterDpi { get; init; } = PdfPageRasterizer.DefaultDpi;
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

            /// <summary>
            /// Pages where a mark falls inside an image, so removing objects cannot clear it.
            /// </summary>
            /// <remarks>
            /// The signature of a scanned document: the page is one big image and every mark
            /// merely straddles it. Deleting that image would blank the page, so the engine
            /// reports it instead — and this is the list the caller needs in order to offer the
            /// only thing that does work, which is rasterising the page. Non-empty means the
            /// output was NOT written.
            /// </remarks>
            internal IReadOnlyList<int> PagesNeedingRaster { get; set; } = Array.Empty<int>();

            /// <summary>Pages that were rasterised, and so no longer carry text.</summary>
            internal IReadOnlyList<int> Rasterized { get; set; } = Array.Empty<int>();
        }

        internal static Result Apply(string srcPath, string destPath, Request request)
        {
            var result = new Result();
            string workDir = Path.GetDirectoryName(destPath) ?? Path.GetTempPath();
            string stripped = Path.Combine(workDir, $"tdpdf_redact_{Guid.NewGuid():N}.pdf");
            string scrubbed = Path.Combine(workDir, $"tdpdf_redact_{Guid.NewGuid():N}.pdf");

            string rasterized = Path.Combine(workDir, $"tdpdf_redact_{Guid.NewGuid():N}.pdf");

            try
            {
                // A page is redacted one way or the other, never both: rasterising it replaces
                // everything on it anyway, so removing objects there first would be work thrown
                // away — and it would trip the image refusal below on the very pages the caller has
                // already agreed to rasterise.
                var raster = new HashSet<int>(request.RasterizePages);
                var objectRects = request.RectsByPage
                    .Where(kv => !raster.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                // 1. Remove the content.
                var removal = PdfiumInterop.RemoveObjectsIntersecting(
                    srcPath, stripped, objectRects, request.RemovePartialOverlaps);

                result.ObjectsRemoved = removal.ObjectsRemoved;
                result.InvisibleTextRemoved = removal.InvisibleTextRemoved;
                result.Partial = removal.Partial;
                if (!removal.Ok)
                {
                    result.Error = removal.Error ?? "the content could not be removed";
                    return result;
                }

                // 2. An image the marks only straddle is still showing whatever was marked, and
                //    the engine deliberately refused to delete it (deleting a page-sized scan to
                //    redact a name in it is not a trade anyone would accept). There is nothing
                //    useful to write here, so stop and hand the caller the pages involved.
                var rasterPages = removal.Partial
                    .Where(p => p.ObjectType == PdfiumInterop.PageObjImage)
                    .Select(p => p.PageIndex)
                    .Distinct()
                    .OrderBy(i => i)
                    .ToList();
                if (rasterPages.Count > 0)
                {
                    result.PagesNeedingRaster = rasterPages;
                    result.Error =
                        $"the marked content is part of an image on page{(rasterPages.Count == 1 ? "" : "s")} " +
                        string.Join(", ", rasterPages.Select(i => i + 1)) +
                        ", which cannot be edited object by object";
                    return result;
                }

                // 3. Rasterise the pages the caller opted in for. Everything on those pages is
                //    replaced by one picture with the marked areas painted out, which is the only
                //    thing that works when the marked content IS the picture.
                string afterRaster = stripped;
                if (raster.Count > 0)
                {
                    if (!PdfPageRasterizer.TryReplacePages(
                            stripped, rasterized, request.RectsByPage, raster,
                            request.RasterDpi, out string? rasterError))
                    {
                        result.Error = rasterError ?? "the affected pages could not be rasterised";
                        return result;
                    }
                    afterRaster = rasterized;
                    result.Rasterized = raster.OrderBy(i => i).ToList();
                }

                // 4. Scrub what lives outside the page content. Redacting a name from the body
                //    while leaving it in the Title is a common and embarrassing miss.
                string verifyPath = afterRaster;
                if (request.ScrubMetadata)
                {
                    if (TryScrubMetadata(afterRaster, scrubbed, out string? scrubError))
                        verifyPath = scrubbed;
                    else
                    {
                        result.Error = scrubError ?? "the document metadata could not be scrubbed";
                        return result;
                    }
                }

                // 5. Prove it, on the OUTPUT rather than on intent.
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
                TryDelete(rasterized);
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
        /// clipping the boundary does not raise a false alarm. It is asked per LETTER, not per
        /// word: a long word the rectangle covers only half of has its centre outside, and a
        /// word-level test would pass the half that is still sitting in the redacted area.
        ///
        /// The two sides are NOT in the same space, and this is where that matters most. The
        /// rectangles are PDF user space — what PDFium removed objects against. PdfPig reports
        /// glyphs in the page AS DISPLAYED: the visible box's origin subtracted and /Rotate
        /// applied. The two coincide only on an unrotated page whose visible box starts at 0,0,
        /// and on any other page a raw comparison looks somewhere else entirely and reports a
        /// clean redaction over text that is still there. Each rectangle is therefore carried into
        /// PdfPig's frame through <see cref="PdfPageGeometry.PdfToTextFrame(PdfPage, PdfiumInterop.PdfRect)"/>,
        /// with the page geometry read from the file being verified — the finished output, not
        /// the source — through the same inheritance-aware, MediaBox-clipped reads the rest of the
        /// pipeline uses. tests/PdfCore RedactVerify.cs runs this against an unredacted word on a
        /// cropped page at every quarter turn, and it must report it every time.
        ///
        /// Anything unreadable throws, which <see cref="Apply"/> turns into a refusal: a check
        /// that cannot run has not confirmed anything.
        /// </remarks>
        internal static IReadOnlyList<string> FindSurvivingText(
            string path, IReadOnlyDictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> rectsByPage)
        {
            var survivors = new List<string>();
            using var doc = PdfPigDoc.Open(path);
            using var geometry = PdfReader.Open(path, PdfDocumentOpenMode.Import);

            foreach (var (pageIndex, rects) in rectsByPage)
            {
                if (rects is null || rects.Count == 0) continue;
                if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) continue;
                if (pageIndex >= geometry.PageCount)
                    throw new InvalidOperationException(
                        $"page {pageIndex + 1} could not be read back to verify the redaction");

                var pageGeometry = geometry.Pages[pageIndex];
                var framed = rects.Select(r => PdfPageGeometry.PdfToTextFrame(pageGeometry, r)).ToList();

                var page = doc.GetPage(pageIndex + 1);   // PdfPig pages are 1-based
                foreach (var word in page.GetWords())
                {
                    bool inside = word.Letters.Any(letter =>
                    {
                        var b = letter.BoundingBox;
                        return framed.Any(r => PdfPageGeometry.ContainsCenter(r, b.Left, b.Bottom, b.Right, b.Top));
                    });
                    if (inside) survivors.Add(word.Text);

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
