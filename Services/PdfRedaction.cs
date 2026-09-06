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
            int rotate = ((InheritedInt(page, "/Rotate") % 360) + 360) % 360;

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

        /// <summary>The box PDFium rasterises: the CropBox when there is a usable one, else the MediaBox.</summary>
        /// <remarks>
        /// Falls back to US Letter only when a document declares neither, which is malformed. A
        /// guess is still better than a divide-by-zero here: the verification pass will refuse the
        /// redaction if the guess was wrong, whereas a crash loses the user's work.
        /// </remarks>
        private static (double X1, double Y1, double X2, double Y2) VisibleBox(PdfPage page)
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

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* temp file; best effort */ }
        }
    }
}
