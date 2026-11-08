using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf.Services
{
    /// <summary>
    /// The places outside the page content that keep a second copy of what a mark covered:
    /// private editor data, page thumbnails, structure-tree descriptions and bookmark titles.
    /// </summary>
    /// <remarks>
    /// Same rule as the rest of the pipeline: fail closed. A structure tree or outline too deep to
    /// walk with a bounded stack is refused with a message rather than half cleaned.
    ///
    /// What is deliberately NOT touched: named destinations (they name a place, not text),
    /// document-level JavaScript and embedded files (whole-document features the user did not
    /// mark; they are outside what a positional mark can address), and the ParentTree / IDTree
    /// (object references and ids only, no text).
    /// </remarks>
    internal static partial class PdfRedaction
    {
        /// <summary>Deepest structure-tree or outline nesting that will be walked.</summary>
        private const int MaxResidueDepth = 256;

        /// <summary>Most nodes of a structure tree or outline that will be walked.</summary>
        private const int MaxResidueNodes = 1_000_000;

        private static readonly string[] StructTextKeys = { "/Alt", "/ActualText", "/E", "/T" };

        // ── Removed text ───────────────────────────────────────────────────────────────

        /// <summary>
        /// The words (and runs of adjacent words) that sit under a mark in the SOURCE file.
        /// </summary>
        /// <remarks>
        /// Read before anything is removed, with the same centre-inside test and the same frame
        /// conversion as <see cref="FindSurvivingText"/>. This is what lets a bookmark title be
        /// cleaned of the text that was redacted without guessing at which title "mentions" it.
        /// Words are trimmed of edge punctuation; anything under three characters is dropped, since
        /// matching "a" or "of" in titles would wreck them for nothing.
        /// </remarks>
        internal static IReadOnlyList<string> CollectRemovedTerms(
            string srcPath, IReadOnlyDictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> rectsByPage)
        {
            var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var doc = PdfPigDoc.Open(srcPath);
            using var geometry = PdfReader.Open(srcPath, PdfDocumentOpenMode.Import);

            foreach (var (pageIndex, rects) in rectsByPage)
            {
                if (rects is null || rects.Count == 0) continue;
                if (pageIndex < 0 || pageIndex >= doc.NumberOfPages || pageIndex >= geometry.PageCount) continue;
                var framed = rects.Select(r => PdfPageGeometry.PdfToTextFrame(geometry.Pages[pageIndex], r)).ToList();

                var run = new List<string>();
                void Flush()
                {
                    if (run.Count > 1) AddTerm(terms, string.Join(" ", run));
                    run.Clear();
                }
                foreach (var word in doc.GetPage(pageIndex + 1).GetWords())
                {
                    bool inside = word.Letters.Any(letter =>
                    {
                        var b = letter.BoundingBox;
                        return framed.Any(r => PdfPageGeometry.ContainsCenter(r, b.Left, b.Bottom, b.Right, b.Top));
                    });
                    if (!inside) { Flush(); continue; }
                    string w = word.Text.Trim().Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}');
                    AddTerm(terms, w);
                    run.Add(w);
                    if (terms.Count > 5000) break;   // a whole page redacted; the structure clearing covers it
                }
                Flush();
            }
            return terms.ToList();
        }

        private static void AddTerm(HashSet<string> terms, string t)
        {
            if (t.Length >= 3) terms.Add(t);
        }

        /// <summary>Case-insensitive match / replace of a set of removed strings, longest first.</summary>
        private sealed class TermMatcher
        {
            private readonly Regex? _re;
            internal TermMatcher(IReadOnlyList<string>? terms)
            {
                if (terms is null || terms.Count == 0) return;
                var parts = terms.OrderByDescending(t => t.Length).Select(t => Regex.Escape(t).Replace("\\ ", "\\s+"));
                _re = new Regex(string.Join("|", parts),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
            }
            internal bool IsMatch(string s) => _re is not null && _re.IsMatch(s);
            internal string Replace(string s) => _re is null ? s : _re.Replace(s, "[redacted]");
        }

        // ── Structure tree ─────────────────────────────────────────────────────────────

        private sealed class StructWalk
        {
            internal readonly List<PdfDictionary> Nodes = new();
            internal readonly HashSet<PdfDictionary> Touched = new(ReferenceEqualityComparer.Instance);
        }

        /// <summary>
        /// Walks the whole structure tree. Throws when it is nested or sized beyond the bounds.
        /// </summary>
        /// <remarks>
        /// An element "touches" a marked page when it has marked content (an MCID, an MCR or an OBJR)
        /// on one, or has no page it can be pinned to. Every ancestor of a touching element touches
        /// too, since a Div's description can summarise its children. Mapping MCIDs to the actual
        /// mark rectangles is not attempted: it needs the content stream's marked-content spans,
        /// which PDFium has already rewritten by this point. This is the conservative choice, and
        /// it costs descriptions on elements of a marked page that the marks did not cover.
        /// </remarks>
        private static StructWalk WalkStructure(PdfDocument doc, IReadOnlyCollection<int> markedPages)
        {
            var walk = new StructWalk();
            if (doc.Internals.Catalog.Elements.GetDictionary("/StructTreeRoot") is not PdfDictionary root) return walk;

            var marked = markedPages.Where(i => i >= 0 && i < doc.PageCount).Select(i => (PdfDictionary)doc.Pages[i]).ToList();
            bool IsMarked(PdfDictionary? pg) => pg is not null && marked.Any(m => Same(m, pg));

            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var parentsOf = new Dictionary<PdfDictionary, List<PdfDictionary>>(ReferenceEqualityComparer.Instance);
            var touching = new List<PdfDictionary>();
            var stack = new Stack<(PdfDictionary Node, PdfDictionary? Pg, int Depth)>();
            stack.Push((root, null, 0));
            seen.Add(root);

            while (stack.Count > 0)
            {
                var (node, inherited, depth) = stack.Pop();
                if (depth > MaxResidueDepth)
                    throw new InvalidOperationException(
                        $"the document's structure tree is nested more than {MaxResidueDepth} levels deep, " +
                        "so its descriptions could not be cleaned completely");
                if (walk.Nodes.Count >= MaxResidueNodes)
                    throw new InvalidOperationException(
                        "the document's structure tree is too large to clean completely");
                walk.Nodes.Add(node);

                var pg = Resolve(node.Elements["/Pg"]) as PdfDictionary ?? inherited;
                bool touches = false;
                foreach (var kid in KidsOf(node))
                {
                    if (kid is PdfInteger || kid is PdfIntegerObject)
                    {
                        if (pg is null || IsMarked(pg)) touches = true;
                    }
                    else if (kid is PdfDictionary kd)
                    {
                        string type = kd.Elements.GetName("/Type");
                        if (type == "/MCR" || type == "/OBJR")
                        {
                            var kpg = Resolve(kd.Elements["/Pg"]) as PdfDictionary ?? pg;
                            if (kpg is null || IsMarked(kpg)) touches = true;
                        }
                        else
                        {
                            if (!parentsOf.TryGetValue(kd, out var list)) parentsOf[kd] = list = new();
                            list.Add(node);
                            if (seen.Add(kd)) stack.Push((kd, pg, depth + 1));
                        }
                    }
                }
                if (touches) touching.Add(node);
            }

            // Propagate upward to every ancestor.
            var queue = new Queue<PdfDictionary>(touching);
            foreach (var t in touching) walk.Touched.Add(t);
            while (queue.Count > 0)
            {
                var n = queue.Dequeue();
                if (!parentsOf.TryGetValue(n, out var ps)) continue;
                foreach (var p in ps)
                    if (walk.Touched.Add(p)) queue.Enqueue(p);
            }
            return walk;
        }

        private static IEnumerable<PdfItem> KidsOf(PdfDictionary node)
        {
            var k = Resolve(node.Elements["/K"]);
            if (k is PdfArray arr)
            {
                for (int i = 0; i < arr.Elements.Count; i++)
                    if (Resolve(arr.Elements[i]) is PdfItem item) yield return item;
            }
            else if (k is not null) yield return k;
        }

        private static string? StringOf(PdfItem? item) => Resolve(item) is PdfString s ? s.Value : null;

        // ── Outlines ───────────────────────────────────────────────────────────────────

        /// <summary>Every bookmark item. Throws when nesting or size is beyond the bounds.</summary>
        private static List<PdfDictionary> WalkOutlines(PdfDocument doc)
        {
            var items = new List<PdfDictionary>();
            if (doc.Internals.Catalog.Elements.GetDictionary("/Outlines") is not PdfDictionary root) return items;
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance) { root };
            var stack = new Stack<(PdfDictionary Node, int Depth)>();
            if (Resolve(root.Elements["/First"]) is PdfDictionary first && seen.Add(first)) stack.Push((first, 1));
            while (stack.Count > 0)
            {
                var (node, depth) = stack.Pop();
                if (depth > MaxResidueDepth)
                    throw new InvalidOperationException(
                        $"the bookmarks are nested more than {MaxResidueDepth} levels deep, " +
                        "so their titles could not be cleaned completely");
                if (items.Count >= MaxResidueNodes)
                    throw new InvalidOperationException("the bookmark tree is too large to clean completely");
                items.Add(node);
                if (Resolve(node.Elements["/First"]) is PdfDictionary c && seen.Add(c)) stack.Push((c, depth + 1));
                if (Resolve(node.Elements["/Next"]) is PdfDictionary n && seen.Add(n)) stack.Push((n, depth));
            }
            return items;
        }

        // ── Scrub ──────────────────────────────────────────────────────────────────────

        /// <summary>What <see cref="TryScrubResidue"/> did, for the caller and the tests.</summary>
        internal sealed class ResidueReport
        {
            internal int PieceInfoRemoved { get; set; }
            internal int ThumbnailsRemoved { get; set; }
            internal int StructElementsCleared { get; set; }
            internal int BookmarkTitlesCleaned { get; set; }
        }

        /// <summary>
        /// Removes /PieceInfo (catalog and marked pages), marked pages' /Thumb, the text
        /// descriptions of structure elements on marked pages, and removed strings from bookmark
        /// titles. Writes <paramref name="destPath"/> only when something changed.
        /// </summary>
        /// <param name="removedTerms">
        /// Text taken from under the marks, or null when it could not be read; bookmark titles are
        /// then unverifiable and any existing title makes this fail rather than pass blind.
        /// </param>
        internal static bool TryScrubResidue(
            string srcPath, string destPath,
            IReadOnlyDictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> rectsByPage,
            IReadOnlyList<string>? removedTerms,
            out ResidueReport report, out bool wrote, out string? error)
        {
            report = new ResidueReport();
            wrote = false;
            error = null;
            try
            {
                using var doc = PdfReader.Open(srcPath, PdfDocumentOpenMode.Modify);
                var markedPages = MarkedPages(rectsByPage, doc.PageCount);
                var matcher = new TermMatcher(removedTerms);

                var catalog = doc.Internals.Catalog;
                if (catalog.Elements.ContainsKey("/PieceInfo")) { catalog.Elements.Remove("/PieceInfo"); report.PieceInfoRemoved++; }
                foreach (int i in markedPages)
                {
                    var page = doc.Pages[i];
                    if (page.Elements.ContainsKey("/PieceInfo")) { page.Elements.Remove("/PieceInfo"); report.PieceInfoRemoved++; }
                    if (page.Elements.ContainsKey("/Thumb")) { page.Elements.Remove("/Thumb"); report.ThumbnailsRemoved++; }
                }

                var walk = WalkStructure(doc, markedPages);
                foreach (var node in walk.Nodes)
                {
                    bool touched = walk.Touched.Contains(node);
                    bool changed = false;
                    foreach (var key in StructTextKeys)
                    {
                        if (!node.Elements.ContainsKey(key)) continue;
                        if (touched) { node.Elements.Remove(key); changed = true; continue; }
                        // Elsewhere in the tree: keep the description unless it repeats removed text.
                        var s = StringOf(node.Elements[key]);
                        if (s is not null && matcher.IsMatch(s))
                        {
                            node.Elements[key] = new PdfString(matcher.Replace(s));
                            changed = true;
                        }
                    }
                    if (changed) report.StructElementsCleared++;
                }

                var outlines = WalkOutlines(doc);
                foreach (var item in outlines)
                {
                    var title = StringOf(item.Elements["/Title"]);
                    if (title is null) continue;
                    if (removedTerms is null)
                    {
                        error = "the bookmark titles could not be checked against the removed text";
                        return false;
                    }
                    if (matcher.IsMatch(title))
                    {
                        item.Elements["/Title"] = new PdfString(matcher.Replace(title));
                        report.BookmarkTitlesCleaned++;
                    }
                }

                if (report.PieceInfoRemoved + report.ThumbnailsRemoved +
                    report.StructElementsCleared + report.BookmarkTitlesCleaned == 0)
                    return true;

                doc.Save(destPath);
                wrote = System.IO.File.Exists(destPath);
                return wrote;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static List<int> MarkedPages(
            IReadOnlyDictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> rectsByPage, int pageCount) =>
            rectsByPage.Where(kv => kv.Value is not null && kv.Value.Count > 0 && kv.Key >= 0 && kv.Key < pageCount)
                       .Select(kv => kv.Key).OrderBy(i => i).ToList();

        // ── Verification ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Residue still present in <paramref name="path"/>: the same vectors
        /// <see cref="TryScrubResidue"/> clears, checked on the OUTPUT. Empty means clean.
        /// </summary>
        /// <remarks>Anything it cannot walk throws, which <see cref="Apply"/> turns into a refusal.</remarks>
        internal static IReadOnlyList<string> FindResidue(
            string path, IReadOnlyDictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> rectsByPage,
            IReadOnlyList<string>? removedTerms)
        {
            var found = new List<string>();
            using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            var markedPages = MarkedPages(rectsByPage, doc.PageCount);
            var matcher = new TermMatcher(removedTerms);

            if (doc.Internals.Catalog.Elements.ContainsKey("/PieceInfo"))
                found.Add("private editor data (/PieceInfo) on the document");
            foreach (int i in markedPages)
            {
                var page = doc.Pages[i];
                if (page.Elements.ContainsKey("/PieceInfo")) found.Add($"private editor data (/PieceInfo) on page {i + 1}");
                if (page.Elements.ContainsKey("/Thumb")) found.Add($"a thumbnail image of page {i + 1}");
            }

            var walk = WalkStructure(doc, markedPages);
            foreach (var node in walk.Nodes)
            {
                bool touched = walk.Touched.Contains(node);
                foreach (var key in StructTextKeys)
                {
                    if (!node.Elements.ContainsKey(key)) continue;
                    var s = StringOf(node.Elements[key]);
                    if (touched || (s is not null && matcher.IsMatch(s)))
                        found.Add($"a structure element's {key} description");
                }
            }

            foreach (var item in WalkOutlines(doc))
            {
                var title = StringOf(item.Elements["/Title"]);
                if (title is not null && matcher.IsMatch(title)) found.Add("a bookmark title containing redacted text");
            }
            return found;
        }
    }
}
