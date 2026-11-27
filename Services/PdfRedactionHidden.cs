using System.IO;
using System.Text;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf.Services
{
    /// <summary>
    /// Two more places a marked picture or an attached original can hide: page resources that
    /// outlive the content which used them, and attached files.
    /// </summary>
    /// <remarks>
    /// Removing a page object regenerates the page's content stream, but the /Resources entry the
    /// object was drawn through stays behind, and with it the image stream. Nothing in the file
    /// draws it any more, yet every tool that walks the objects (or just reads the bytes) still
    /// finds it. So resources are pruned against the regenerated content, and the output is then
    /// checked for any image still drawn under a mark.
    ///
    /// Attached files are the other half: an embedded file, an /AF entry or a file-attachment
    /// annotation is routinely the unredacted original. They are removed from every page, not just
    /// the marked ones, and the output is checked for any embedded-file stream.
    /// </remarks>
    internal static partial class PdfRedaction
    {
        private const int MaxFormDepth = 32;
        private static readonly string[] PrunedCategories = { "/XObject", "/Font", "/ExtGState" };

        // ── Resource pruning ────────────────────────────────────────────────────────────

        /// <summary>
        /// Drops /XObject, /Font and /ExtGState entries no content stream on a marked page names any
        /// more, so the save-time compaction drops the objects behind them. Nothing is written when
        /// nothing is pruned.
        /// </summary>
        /// <remarks>
        /// Conservative by construction: a name counts as used when it appears anywhere in a content
        /// stream as a name token, whatever the operator. A resource dictionary shared with an
        /// unmarked page keeps whatever that page uses; one that cannot be read, or that is also the
        /// form default resources, is left alone. The verification that follows does not depend on
        /// this step succeeding.
        /// </remarks>
        internal static bool TryPruneResources(
            string srcPath, string destPath,
            IReadOnlyDictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> rectsByPage,
            IReadOnlyCollection<int> skipPages, out int pruned, out string? error)
        {
            pruned = 0;
            error = null;
            try
            {
                using var doc = PdfReader.Open(srcPath, PdfDocumentOpenMode.Modify);
                var targets = MarkedPages(rectsByPage, doc.PageCount).Where(i => !skipPages.Contains(i)).ToList();
                if (targets.Count == 0) return true;

                var used = new Dictionary<PdfDictionary, HashSet<string>>(ReferenceEqualityComparer.Instance);
                var unsafeDicts = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

                // The form default resources are looked up by name by fields, outside any page.
                if (Resolve(doc.Internals.Catalog.Elements["/AcroForm"]) is PdfDictionary af &&
                    Resolve(af.Elements["/DR"]) is PdfDictionary dr)
                    foreach (var d in CategoryDicts(dr)) unsafeDicts.Add(d);

                var targetDicts = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                foreach (int i in targets)
                    foreach (var d in CategoryDicts(InheritedResources(doc.Pages[i]))) targetDicts.Add(d);

                for (int i = 0; i < doc.PageCount; i++)
                {
                    var page = doc.Pages[i];
                    var res = InheritedResources(page);
                    var dicts = CategoryDicts(res);
                    if (dicts.Count == 0) continue;
                    if (!targets.Contains(i) && !dicts.Any(targetDicts.Contains)) continue;
                    try
                    {
                        var visited = new HashSet<(PdfDictionary, PdfDictionary?)>();
                        foreach (var data in PageContentStreams(page)) WalkContent(data, res, 0, used, visited, unsafeDicts);
                        if (HasResourcelessType3(res)) foreach (var d in dicts) unsafeDicts.Add(d);
                    }
                    catch
                    {
                        foreach (var d in dicts) unsafeDicts.Add(d);
                    }
                }

                foreach (int i in targets)
                {
                    foreach (var d in CategoryDicts(InheritedResources(doc.Pages[i])))
                    {
                        if (unsafeDicts.Contains(d)) continue;
                        used.TryGetValue(d, out var names);
                        foreach (var key in d.Elements.Keys.ToList())
                        {
                            if (names is not null && names.Contains(key)) continue;
                            d.Elements.Remove(key);
                            pruned++;
                        }
                    }
                }

                if (pruned == 0) return true;
                doc.Save(destPath);
                return File.Exists(destPath);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static PdfDictionary? InheritedResources(PdfPage page)
        {
            PdfDictionary? node = page;
            for (int depth = 0; node is not null && depth < 64; depth++)
            {
                if (Resolve(node.Elements["/Resources"]) is PdfDictionary r) return r;
                node = Resolve(node.Elements["/Parent"]) as PdfDictionary;
            }
            return null;
        }

        private static List<PdfDictionary> CategoryDicts(PdfDictionary? resources)
        {
            var list = new List<PdfDictionary>();
            if (resources is null) return list;
            foreach (var cat in PrunedCategories)
                if (Resolve(resources.Elements[cat]) is PdfDictionary d) list.Add(d);
            return list;
        }

        private static bool HasResourcelessType3(PdfDictionary? resources)
        {
            if (Resolve(resources?.Elements["/Font"]) is not PdfDictionary fonts) return false;
            foreach (var key in fonts.Elements.Keys)
                if (Resolve(fonts.Elements[key]) is PdfDictionary f &&
                    f.Elements.GetName("/Subtype") == "/Type3" && f.Elements["/Resources"] is null)
                    return true;
            return false;
        }

        private static List<byte[]> PageContentStreams(PdfPage page)
        {
            var list = new List<byte[]>();
            var c = Resolve(page.Elements["/Contents"]);
            if (c is PdfArray arr)
            {
                for (int i = 0; i < arr.Elements.Count; i++)
                    if (Resolve(arr.Elements[i]) is PdfDictionary { Stream: { } s }) list.Add(s.UnfilteredValue);
            }
            else if (c is PdfDictionary { Stream: { } s }) list.Add(s.UnfilteredValue);
            return list;
        }

        private static void WalkContent(
            byte[] data, PdfDictionary? resources, int depth,
            Dictionary<PdfDictionary, HashSet<string>> used,
            HashSet<(PdfDictionary, PdfDictionary?)> visited, HashSet<PdfDictionary> unsafeDicts)
        {
            if (depth > MaxFormDepth) throw new InvalidOperationException("forms nested too deeply");
            if (resources is null) return;
            var names = ScanNames(data);
            var dicts = CategoryDicts(resources);
            foreach (var d in dicts)
            {
                if (!used.TryGetValue(d, out var set))
                    used[d] = set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var n in names) set.Add(n);
            }

            if (Resolve(resources.Elements["/XObject"]) is not PdfDictionary xo) return;
            foreach (var key in xo.Elements.Keys.ToList())
            {
                if (!names.Contains(key)) continue;
                if (Resolve(xo.Elements[key]) is not PdfDictionary { Stream: { } stm } form) continue;
                if (form.Elements.GetName("/Subtype") != "/Form") continue;
                var formRes = Resolve(form.Elements["/Resources"]) as PdfDictionary ?? resources;
                if (!visited.Add((form, formRes))) continue;
                WalkContent(stm.UnfilteredValue, formRes, depth + 1, used, visited, unsafeDicts);
                if (!ReferenceEquals(formRes, resources) && HasResourcelessType3(formRes))
                    foreach (var d in CategoryDicts(formRes)) unsafeDicts.Add(d);
            }
        }

        /// <summary>
        /// Every name token in a content stream, as written with its leading slash (and also with
        /// #xx escapes decoded). Strings, comments and inline image data are skipped, so a stray
        /// slash or parenthesis in binary data cannot hide a later name.
        /// </summary>
        internal static HashSet<string> ScanNames(byte[] s)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            int n = s.Length, i = 0;
            static bool Ws(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
            static bool Delim(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
                or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

            while (i < n)
            {
                byte b = s[i];
                if (Ws(b)) { i++; continue; }
                if (b == '%') { while (i < n && s[i] != 10 && s[i] != 13) i++; continue; }
                if (b == '(')
                {
                    int level = 1; i++;
                    while (i < n && level > 0)
                    {
                        if (s[i] == '\\') i++;
                        else if (s[i] == '(') level++;
                        else if (s[i] == ')') level--;
                        i++;
                    }
                    continue;
                }
                if (b == '<')
                {
                    if (i + 1 < n && s[i + 1] == '<') { i += 2; continue; }
                    while (i < n && s[i] != '>') i++;
                    i++;
                    continue;
                }
                if (b == '/')
                {
                    int start = ++i;
                    while (i < n && !Ws(s[i]) && !Delim(s[i])) i++;
                    string raw = Encoding.Latin1.GetString(s, start, i - start);
                    names.Add("/" + raw);
                    if (raw.Contains('#'))
                    {
                        var sb = new StringBuilder();
                        for (int k = 0; k < raw.Length; k++)
                        {
                            if (raw[k] == '#' && k + 2 < raw.Length && Uri.IsHexDigit(raw[k + 1]) && Uri.IsHexDigit(raw[k + 2]))
                            { sb.Append((char)Convert.ToInt32(raw.Substring(k + 1, 2), 16)); k += 2; }
                            else sb.Append(raw[k]);
                        }
                        names.Add("/" + sb);
                    }
                    continue;
                }
                if (Delim(b)) { i++; continue; }

                int ts = i;
                while (i < n && !Ws(s[i]) && !Delim(s[i])) i++;
                if (i - ts == 2 && s[ts] == 'B' && s[ts + 1] == 'I')
                {
                    // Inline image: names in its dictionary count; the data after ID does not.
                    while (i < n)
                    {
                        // Tokenise the dictionary cheaply: only names matter here.
                        while (i < n && Ws(s[i])) i++;
                        if (i >= n) break;
                        if (s[i] == '/')
                        {
                            int st = ++i;
                            while (i < n && !Ws(s[i]) && !Delim(s[i])) i++;
                            names.Add("/" + Encoding.Latin1.GetString(s, st, i - st));
                            continue;
                        }
                        int us = i;
                        while (i < n && !Ws(s[i]) && !Delim(s[i])) i++;
                        if (i == us) { i++; continue; }
                        if (i - us == 2 && s[us] == 'I' && s[us + 1] == 'D') break;
                    }
                    // Data runs to a whitespace-delimited EI.
                    while (i + 1 < n)
                    {
                        if (s[i] == 'E' && s[i + 1] == 'I' && (i == 0 || Ws(s[i - 1])) && (i + 2 >= n || Ws(s[i + 2]) || Delim(s[i + 2])))
                        { i += 2; break; }
                        i++;
                    }
                }
            }
            return names;
        }

        // ── Images still under a mark ───────────────────────────────────────────────────

        /// <summary>
        /// Images still drawn where a mark is, on the pages that were not rasterised. Empty means
        /// none; each entry is the 0-based page index.
        /// </summary>
        /// <remarks>
        /// PdfPig reports image bounds in the page AS DISPLAYED, so each mark is carried into that
        /// frame through <see cref="PdfPageGeometry.PdfToTextFrame(PdfPage, PdfiumInterop.PdfRect)"/>
        /// exactly as <see cref="FindSurvivingText"/> does. Images inside form XObjects are
        /// reported by PdfPig too. An image whose box merely touches a mark does not count.
        /// </remarks>
        internal static IReadOnlyList<int> FindSurvivingImages(
            string path, IReadOnlyDictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> rectsByPage,
            IReadOnlyCollection<int> rasterizedPages)
        {
            var pages = new List<int>();
            using var doc = PdfPigDoc.Open(path);
            using var geometry = PdfReader.Open(path, PdfDocumentOpenMode.Import);

            foreach (var (pageIndex, rects) in rectsByPage.OrderBy(kv => kv.Key))
            {
                if (rects is null || rects.Count == 0 || rasterizedPages.Contains(pageIndex)) continue;
                if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) continue;
                if (pageIndex >= geometry.PageCount)
                    throw new InvalidOperationException(
                        $"page {pageIndex + 1} could not be read back to verify the redaction");

                var framed = rects.Select(r => PdfPageGeometry.PdfToTextFrame(geometry.Pages[pageIndex], r)).ToList();
                foreach (var img in doc.GetPage(pageIndex + 1).GetImages())
                {
                    var b = img.BoundingBox;
                    double l = Math.Min(b.Left, b.Right), r = Math.Max(b.Left, b.Right);
                    double bo = Math.Min(b.Bottom, b.Top), t = Math.Max(b.Bottom, b.Top);
                    if (framed.Any(m => l < m.Right && r > m.Left && bo < m.Top && t > m.Bottom))
                    {
                        pages.Add(pageIndex);
                        break;
                    }
                }
            }
            return pages;
        }

        // ── Attached files ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Removes every attached file: the catalog's embedded-files name tree, /AF arrays on the
        /// catalog and every page, file-attachment annotations on every page, and the /EF stream
        /// reference of any file specification left anywhere. Writes nothing when there is none.
        /// </summary>
        internal static bool TryRemoveAttachments(string srcPath, string destPath, out int removed, out string? error)
        {
            removed = 0;
            error = null;
            try
            {
                using var doc = PdfReader.Open(srcPath, PdfDocumentOpenMode.Modify);
                int streams = CountEmbeddedStreams(doc);
                int containers = 0;

                var catalog = doc.Internals.Catalog;
                if (Resolve(catalog.Elements["/Names"]) is PdfDictionary names && names.Elements.ContainsKey("/EmbeddedFiles"))
                { names.Elements.Remove("/EmbeddedFiles"); containers++; }
                if (catalog.Elements.ContainsKey("/AF")) { catalog.Elements.Remove("/AF"); containers++; }

                for (int p = 0; p < doc.PageCount; p++)
                {
                    var page = doc.Pages[p];
                    if (page.Elements.ContainsKey("/AF")) { page.Elements.Remove("/AF"); containers++; }
                    if (Resolve(page.Elements["/Annots"]) is not PdfArray annots) continue;
                    for (int i = annots.Elements.Count - 1; i >= 0; i--)
                    {
                        if (Resolve(annots.Elements[i]) is not PdfDictionary a || a.Elements.GetName("/Subtype") != "/FileAttachment") continue;
                        annots.Elements.RemoveAt(i);
                        foreach (var key in a.Elements.Keys.ToList())
                            if (key != "/Type" && key != "/Subtype") a.Elements.Remove(key);
                        containers++;
                    }
                }

                foreach (var obj in doc.Internals.GetAllObjects())
                    if (obj is PdfDictionary d && d.Elements.ContainsKey("/EF")) { d.Elements.Remove("/EF"); containers++; }

                removed = streams > 0 ? streams : containers;
                if (containers == 0) return true;
                doc.Save(destPath);
                return File.Exists(destPath);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static int CountEmbeddedStreams(PdfDocument doc)
        {
            int n = 0;
            foreach (var obj in doc.Internals.GetAllObjects())
                if (obj is PdfDictionary { Stream: not null } d && d.Elements.GetName("/Type") == "/EmbeddedFile") n++;
            return n;
        }

        /// <summary>What still carries an attached file in <paramref name="path"/>. Empty means none.</summary>
        internal static IReadOnlyList<string> FindAttachments(string path)
        {
            var found = new List<string>();
            using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            var catalog = doc.Internals.Catalog;
            if (Resolve(catalog.Elements["/Names"]) is PdfDictionary names && names.Elements.ContainsKey("/EmbeddedFiles"))
                found.Add("an attached file (the document's embedded-files list)");
            if (catalog.Elements.ContainsKey("/AF")) found.Add("an attached file (/AF on the document)");
            for (int p = 0; p < doc.PageCount; p++)
            {
                var page = doc.Pages[p];
                if (page.Elements.ContainsKey("/AF")) found.Add($"an attached file (/AF on page {p + 1})");
                if (Resolve(page.Elements["/Annots"]) is PdfArray annots)
                    for (int i = 0; i < annots.Elements.Count; i++)
                        if (Resolve(annots.Elements[i]) is PdfDictionary a && a.Elements.GetName("/Subtype") == "/FileAttachment")
                            found.Add($"a file attachment on page {p + 1}");
            }
            foreach (var obj in doc.Internals.GetAllObjects())
                if (obj is PdfDictionary { Stream: not null } d && d.Elements.GetName("/Type") == "/EmbeddedFile")
                { found.Add("an embedded file stream"); break; }
            return found;
        }
    }
}
