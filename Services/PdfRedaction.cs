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

            /// <summary>Annotations and form widgets removed because they sat under a mark.</summary>
            internal int AnnotationsRemoved { get; set; }
            internal IReadOnlyList<PdfiumInterop.RedactionOverlap> Partial { get; set; }
                = Array.Empty<PdfiumInterop.RedactionOverlap>();

            /// <summary>Text still sitting inside a redaction rectangle after the fact.</summary>
            /// <remarks>Non-empty means the output was NOT written.</remarks>
            internal IReadOnlyList<string> Survivors { get; set; } = Array.Empty<string>();

            /// <summary>True when <see cref="Survivors"/> lists annotations or form fields, not text.</summary>
            internal bool SurvivorsAreAnnotations { get; set; }

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
            string annotated = Path.Combine(workDir, $"tdpdf_redact_{Guid.NewGuid():N}.pdf");

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

                // 3. Remove annotations and form fields under a mark. They are not page content,
                //    so the object pass above never sees them, and a comment, a link or a filled
                //    field over a redacted name would otherwise carry it straight through. Every
                //    marked page, rasterised ones included: rasterising drops the page's /Annots
                //    but not a field's value, which lives in the AcroForm tree.
                string afterAnnots = stripped;
                if (!TryRemoveAnnotations(stripped, annotated, request.RectsByPage,
                        out int annotsRemoved, out string? annotError))
                {
                    result.Error = annotError ?? "the annotations under the marks could not be removed";
                    return result;
                }
                if (annotsRemoved > 0) afterAnnots = annotated;
                result.AnnotationsRemoved = annotsRemoved;

                // 4. Rasterise the pages the caller opted in for. Everything on those pages is
                //    replaced by one picture with the marked areas painted out, which is the only
                //    thing that works when the marked content IS the picture.
                string afterRaster = afterAnnots;
                if (raster.Count > 0)
                {
                    if (!PdfPageRasterizer.TryReplacePages(
                            afterAnnots, rasterized, request.RectsByPage, raster,
                            request.RasterDpi, out string? rasterError))
                    {
                        result.Error = rasterError ?? "the affected pages could not be rasterised";
                        return result;
                    }
                    afterRaster = rasterized;
                    result.Rasterized = raster.OrderBy(i => i).ToList();
                }

                // 5. Scrub what lives outside the page content. Redacting a name from the body
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

                // 6. Prove it, on the OUTPUT rather than on intent.
                result.Survivors = FindSurvivingText(verifyPath, request.RectsByPage);
                if (result.Survivors.Count > 0)
                {
                    result.Error =
                        $"redaction could not be confirmed because {result.Survivors.Count} item(s) of text " +
                        "remain inside a redacted area, so the file was not written";
                    return result;
                }
                var annotSurvivors = FindSurvivingAnnotations(verifyPath, request.RectsByPage);
                if (annotSurvivors.Count > 0)
                {
                    result.Survivors = annotSurvivors;
                    result.SurvivorsAreAnnotations = true;
                    result.Error =
                        $"redaction could not be confirmed because {annotSurvivors.Count} annotation(s) or " +
                        "form field(s) remain inside a redacted area, so the file was not written";
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
                TryDelete(annotated);
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

        // ── Annotations and form fields ─────────────────────────────────────────────────

        /// <summary>
        /// Removes every annotation under a mark, and for a form widget the field value it shows.
        /// </summary>
        /// <remarks>
        /// No geometry conversion is needed: an annotation's /Rect and the redaction rectangles are
        /// both PDF user space, before /Rotate and independent of the CropBox.
        ///
        /// Fail-closed throughout. An annotation whose /Rect cannot be read cannot be shown to be
        /// clear of the marks, so it goes. So do the popups and replies hanging off a removed
        /// annotation, which repeat its text wherever they happen to sit. Every removed dictionary
        /// is also emptied, because something else (a structure tree /OBJR, an AcroForm /CO entry)
        /// can keep it reachable, and then PdfSharpCore's save-time compaction would write it out.
        ///
        /// Nothing is written when nothing is removed; <paramref name="removed"/> is then 0.
        /// </remarks>
        internal static bool TryRemoveAnnotations(
            string srcPath, string destPath,
            IReadOnlyDictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> rectsByPage,
            out int removed, out string? error)
        {
            removed = 0;
            error = null;
            try
            {
                using var doc = PdfReader.Open(srcPath, PdfDocumentOpenMode.Modify);
                var acroForm = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
                var fieldWidgets = CollectFieldWidgets(acroForm);
                bool widgetRemoved = false;

                foreach (var (pageIndex, rects) in rectsByPage)
                {
                    if (rects is null || rects.Count == 0) continue;
                    if (pageIndex < 0 || pageIndex >= doc.PageCount) continue;
                    var page = doc.Pages[pageIndex];
                    var annots = page.Elements.GetObject("/Annots") as PdfArray;
                    var pageAnnots = new List<PdfDictionary>();
                    if (annots is not null)
                        for (int i = 0; i < annots.Elements.Count; i++)
                            if (Resolve(annots.Elements[i]) is PdfDictionary d) pageAnnots.Add(d);

                    var doomed = new List<PdfDictionary>();
                    foreach (var a in pageAnnots)
                        if (IsUnder(a, rects)) doomed.Add(a);
                    // A widget the field tree places on this page but the page's /Annots does not
                    // list is never drawn, yet its value is still in the file.
                    foreach (var w in fieldWidgets)
                        if (IsOnPage(w, page) && IsUnder(w, rects) && !Contains(doomed, w)) doomed.Add(w);

                    // Popups and replies of a removed annotation: their text is its text.
                    bool grew = true;
                    while (grew)
                    {
                        grew = false;
                        foreach (var a in pageAnnots)
                        {
                            if (Contains(doomed, a)) continue;
                            bool linked =
                                (Resolve(a.Elements["/Parent"]) is PdfDictionary p && Contains(doomed, p) &&
                                 a.Elements.GetName("/Subtype") == "/Popup") ||
                                (Resolve(a.Elements["/IRT"]) is PdfDictionary irt && Contains(doomed, irt)) ||
                                doomed.Any(d => Resolve(d.Elements["/Popup"]) is PdfDictionary pp && Same(pp, a));
                            if (linked) { doomed.Add(a); grew = true; }
                        }
                    }
                    if (doomed.Count == 0 && annots is null) continue;

                    // Entries that are not dictionaries at all cannot be placed either.
                    if (annots is not null)
                        for (int i = annots.Elements.Count - 1; i >= 0; i--)
                        {
                            var d = Resolve(annots.Elements[i]) as PdfDictionary;
                            if (d is null || Contains(doomed, d))
                            {
                                annots.Elements.RemoveAt(i);
                                if (d is null) removed++;
                            }
                        }

                    foreach (var a in doomed)
                    {
                        if (IsWidget(a) && acroForm is not null)
                        {
                            DetachWidget(acroForm, a);
                            widgetRemoved = true;
                        }
                        foreach (var key in a.Elements.Keys.ToList())
                            if (key != "/Type" && key != "/Subtype") a.Elements.Remove(key);
                        removed++;
                    }
                }

                // A hybrid form keeps a second copy of every value in its XFA datasets, which no
                // AcroForm edit reaches. Dropping it leaves the AcroForm form working on its own.
                if (widgetRemoved && acroForm is not null) acroForm.Elements.Remove("/XFA");

                if (removed == 0) return true;
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
        /// Takes a removed widget out of the field tree, and takes its value with it.
        /// </summary>
        /// <remarks>
        /// A widget is either merged with its field (it carries /T) or is one of several /Kids of
        /// one. When the field is left with no widgets it is dropped, and so is a parent that is
        /// left empty. When other widgets of the same field survive elsewhere they would go on
        /// showing the value, so the value is cleared anyway, failing closed: /V and /DV go, a
        /// checkbox or radio sibling is switched off, and a text or choice sibling loses the
        /// appearance that still draws the old value. That costs the user the value in fields
        /// they did not mark, which is the right side to err on for a redaction.
        /// </remarks>
        private static void DetachWidget(PdfDictionary acroForm, PdfDictionary widget)
        {
            var parent = Resolve(widget.Elements["/Parent"]) as PdfDictionary;
            RemoveFromContainer(acroForm, widget, parent);
            if (acroForm.Elements.GetObject("/CO") is PdfArray co) RemoveFrom(co, widget);

            if (widget.Elements.ContainsKey("/T") || parent is null)
            {
                // Merged field and widget: the field itself is gone. A value it inherits from a
                // parent would be shown by nothing now, but goes too, failing closed.
                ClearValue(widget, parent);
                if (parent is not null && !HasKids(parent)) PruneEmpty(acroForm, parent);
                return;
            }

            // A pure widget: `parent` is its field.
            widget.Elements.Remove("/AP");
            ClearValue(parent, Resolve(parent.Elements["/Parent"]) as PdfDictionary);
            if (!HasKids(parent))
            {
                PruneEmpty(acroForm, parent);
                return;
            }
            // Other widgets of this field survive and would go on showing the value.
            var kids = (PdfArray)parent.Elements.GetObject("/Kids");
            for (int i = 0; i < kids.Elements.Count; i++)
            {
                if (Resolve(kids.Elements[i]) is not PdfDictionary sibling) continue;
                if (sibling.Elements.ContainsKey("/AS")) sibling.Elements["/AS"] = new PdfName("/Off");
                else sibling.Elements.Remove("/AP");
            }
        }

        private static bool HasKids(PdfDictionary node) =>
            node.Elements.GetObject("/Kids") is PdfArray kids && kids.Elements.Count > 0;

        /// <summary>Drops a field left with no kids, and any ancestor that leaves empty.</summary>
        private static void PruneEmpty(PdfDictionary acroForm, PdfDictionary field)
        {
            for (int depth = 0; field is not null && depth < 64; depth++)
            {
                var parent = Resolve(field.Elements["/Parent"]) as PdfDictionary;
                RemoveFromContainer(acroForm, field, parent);
                foreach (var key in new[] { "/V", "/DV", "/RV", "/AP", "/AS" }) field.Elements.Remove(key);
                if (acroForm.Elements.GetObject("/CO") is PdfArray co) RemoveFrom(co, field);
                if (parent is null) return;
                var kids = parent.Elements.GetObject("/Kids") as PdfArray;
                if (kids is not null && kids.Elements.Count > 0) return;
                field = parent;
            }
        }

        /// <summary>
        /// Clears the value <paramref name="node"/> shows: its own, or, when it has none, the one it
        /// inherits from the nearest ancestor that has one (/V is an inheritable field attribute).
        /// </summary>
        private static void ClearValue(PdfDictionary node, PdfDictionary? parent)
        {
            PdfDictionary? n = node;
            for (int depth = 0; n is not null && depth < 64; depth++)
            {
                bool had = n.Elements.ContainsKey("/V");
                foreach (var key in new[] { "/V", "/DV", "/RV" }) n.Elements.Remove(key);
                if (had) return;
                n = depth == 0 ? parent : Resolve(n.Elements["/Parent"]) as PdfDictionary;
            }
        }

        private static void RemoveFromContainer(PdfDictionary acroForm, PdfDictionary node, PdfDictionary? parent)
        {
            if (parent?.Elements.GetObject("/Kids") is PdfArray kids) RemoveFrom(kids, node);
            if (acroForm.Elements.GetObject("/Fields") is PdfArray fields) RemoveFrom(fields, node);
        }

        private static void RemoveFrom(PdfArray array, PdfDictionary node)
        {
            for (int i = array.Elements.Count - 1; i >= 0; i--)
                if (Resolve(array.Elements[i]) is PdfDictionary d && Same(d, node)) array.Elements.RemoveAt(i);
        }

        /// <summary>
        /// Annotations and form widgets still inside a redaction rectangle in <paramref name="path"/>.
        /// </summary>
        /// <remarks>
        /// The annotation counterpart of <see cref="FindSurvivingText"/>, and just as positional.
        /// An annotation whose rectangle cannot be read counts as a survivor: it cannot be shown to
        /// be clear. So does a widget the field tree places on the page outside its /Annots.
        /// </remarks>
        internal static IReadOnlyList<string> FindSurvivingAnnotations(
            string path, IReadOnlyDictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> rectsByPage)
        {
            var survivors = new List<string>();
            using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            var fieldWidgets = CollectFieldWidgets(doc.Internals.Catalog.Elements.GetDictionary("/AcroForm"));

            foreach (var (pageIndex, rects) in rectsByPage)
            {
                if (rects is null || rects.Count == 0) continue;
                if (pageIndex < 0 || pageIndex >= doc.PageCount)
                    throw new InvalidOperationException(
                        $"page {pageIndex + 1} could not be read back to verify the redaction");
                var page = doc.Pages[pageIndex];
                var found = new List<PdfDictionary>();
                if (page.Elements.GetObject("/Annots") is PdfArray annots)
                    for (int i = 0; i < annots.Elements.Count; i++)
                    {
                        var d = Resolve(annots.Elements[i]) as PdfDictionary;
                        if (d is null) { survivors.Add($"an unreadable annotation on page {pageIndex + 1}"); continue; }
                        if (IsUnder(d, rects)) found.Add(d);
                    }
                foreach (var w in fieldWidgets)
                    if (IsOnPage(w, page) && IsUnder(w, rects) && !Contains(found, w)) found.Add(w);

                foreach (var d in found)
                {
                    string subtype = d.Elements.GetName("/Subtype").TrimStart('/');
                    survivors.Add(IsWidget(d)
                        ? $"a form field on page {pageIndex + 1}"
                        : $"a {(subtype.Length == 0 ? "untyped" : subtype)} annotation on page {pageIndex + 1}");
                }
            }
            return survivors;
        }

        /// <summary>Every widget reachable from the AcroForm field tree.</summary>
        private static List<PdfDictionary> CollectFieldWidgets(PdfDictionary? acroForm)
        {
            var widgets = new List<PdfDictionary>();
            if (acroForm?.Elements.GetObject("/Fields") is not PdfArray fields) return widgets;
            var seen = new List<PdfDictionary>();
            var stack = new Stack<(PdfDictionary Node, int Depth)>();
            for (int i = 0; i < fields.Elements.Count; i++)
                if (Resolve(fields.Elements[i]) is PdfDictionary f) stack.Push((f, 0));
            while (stack.Count > 0)
            {
                var (node, depth) = stack.Pop();
                if (depth > 64 || Contains(seen, node)) continue;   // malformed, cyclic trees
                seen.Add(node);
                if (node.Elements.GetObject("/Kids") is PdfArray kids && kids.Elements.Count > 0)
                {
                    for (int i = 0; i < kids.Elements.Count; i++)
                        if (Resolve(kids.Elements[i]) is PdfDictionary k) stack.Push((k, depth + 1));
                }
                else widgets.Add(node);
            }
            return widgets;
        }

        /// <summary>
        /// True when the annotation's /Rect touches a mark, or cannot be read at all.
        /// </summary>
        /// <remarks>
        /// Touching counts, and a zero-size rectangle counts: a hidden or degenerate annotation can
        /// still carry text, and the strict overlap test used for page objects would miss it.
        /// </remarks>
        private static bool IsUnder(PdfDictionary annot, IReadOnlyList<PdfiumInterop.PdfRect> rects)
        {
            if (!TryReadRect(annot, out double l, out double b, out double r, out double t)) return true;
            return rects.Any(m => l <= m.Right && r >= m.Left && b <= m.Top && t >= m.Bottom);
        }

        private static bool TryReadRect(PdfDictionary annot, out double l, out double b, out double r, out double t)
        {
            l = b = r = t = 0;
            if (annot.Elements.GetObject("/Rect") is not PdfArray arr || arr.Elements.Count < 4) return false;
            var v = new double[4];
            for (int i = 0; i < 4; i++)
            {
                switch (Resolve(arr.Elements[i]))
                {
                    case PdfReal x: v[i] = x.Value; break;
                    case PdfInteger x: v[i] = x.Value; break;
                    case PdfRealObject x: v[i] = x.Value; break;
                    case PdfIntegerObject x: v[i] = x.Value; break;
                    default: return false;
                }
                if (double.IsNaN(v[i]) || double.IsInfinity(v[i])) return false;
            }
            l = Math.Min(v[0], v[2]); r = Math.Max(v[0], v[2]);
            b = Math.Min(v[1], v[3]); t = Math.Max(v[1], v[3]);
            return true;
        }

        private static bool IsWidget(PdfDictionary d) => d.Elements.GetName("/Subtype") == "/Widget";

        private static bool IsOnPage(PdfDictionary widget, PdfPage page) =>
            Resolve(widget.Elements["/P"]) is PdfDictionary p && Same(p, page);

        private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

        private static bool Same(PdfDictionary a, PdfDictionary b) =>
            ReferenceEquals(a, b) ||
            (a.Reference is not null && b.Reference is not null && a.Reference.ObjectID == b.Reference.ObjectID);

        private static bool Contains(List<PdfDictionary> list, PdfDictionary d) => list.Any(x => Same(x, d));

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* temp file; best effort */ }
        }
    }
}
