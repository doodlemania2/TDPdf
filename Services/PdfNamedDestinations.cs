using PdfSharpCore.Pdf;

namespace TDPdf.Services
{
    /// <summary>
    /// Named destinations: lookup (page render, link hit-testing), the source-document name -> page
    /// map used when merging, and the rewrite of named links to explicit ones. Pure PdfSharpCore.
    /// </summary>
    /// <remarks>
    /// A name tree comes from the file, so it can loop (/Kids pointing back at an ancestor) or nest
    /// without end. <see cref="WalkNameTree"/> is the single walker: a visited set plus a depth cap,
    /// so a crafted file costs one finished walk rather than a stack overflow.
    /// </remarks>
    internal static class PdfNamedDestinations
    {
        /// <summary>Deepest /Kids nesting followed. Real name trees are two or three levels.</summary>
        internal const int MaxTreeDepth = 64;

        /// <summary>
        /// Visits every [key, value] pair of a name tree, leaf /Names first then /Kids in order.
        /// <paramref name="visit"/> returns true to stop early; the method then returns true.
        /// </summary>
        internal static bool WalkNameTree(PdfDictionary root, Func<PdfItem?, PdfItem?, bool> visit)
        {
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            return Walk(root, visit, seen, 0);
        }

        private static bool Walk(PdfDictionary node, Func<PdfItem?, PdfItem?, bool> visit,
            HashSet<PdfDictionary> seen, int depth)
        {
            if (depth > MaxTreeDepth || !seen.Add(node)) return false;

            var namesArr = node.Elements.GetArray("/Names");
            if (namesArr != null)
            {
                for (int i = 0; i + 1 < namesArr.Elements.Count; i += 2)
                    if (visit(namesArr.Elements[i], PdfItems.Deref(namesArr.Elements[i + 1]))) return true;
            }

            var kids = node.Elements.GetArray("/Kids");
            if (kids != null)
            {
                for (int i = 0; i < kids.Elements.Count; i++)
                    if (PdfItems.Deref(kids.Elements[i]) is PdfDictionary kid && Walk(kid, visit, seen, depth + 1))
                        return true;
            }
            return false;
        }

        /// <summary>The destination array a destination value names: the array itself, or a dictionary's /D.</summary>
        internal static PdfArray? DestArray(PdfItem? val) =>
            val as PdfArray ?? (val as PdfDictionary)?.Elements.GetArray("/D");

        /// <summary>
        /// Resolves a named destination (string or name) to its destination array through the
        /// catalog's legacy /Dests dictionary, then its /Names /Dests name tree.
        /// </summary>
        internal static PdfArray? Resolve(PdfDocument doc, PdfItem nameItem)
        {
            string name = nameItem switch
            {
                PdfString s => s.Value,
                PdfName   n => n.Value.TrimStart('/'),
                _           => ""
            };
            if (string.IsNullOrEmpty(name)) return null;

            var catalog = doc.Internals.Catalog;

            // Legacy /Dests dictionary (direct mapping)
            var dests = catalog.Elements.GetDictionary("/Dests");
            if (dests != null)
            {
                PdfItem? val = PdfItems.Deref(dests.Elements[name] ?? dests.Elements["/" + name] ?? new PdfInteger(-1));
                if (val is PdfArray da) return da;
                if (val is PdfDictionary dd) return dd.Elements.GetArray("/D");
            }

            // Modern /Names /Dests name tree
            var destTree = catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/Dests");
            if (destTree != null)
                return ResolveInTree(destTree, name);

            return null;
        }

        /// <summary>Finds the destination array for <paramref name="name"/> in a name tree.</summary>
        internal static PdfArray? ResolveInTree(PdfDictionary root, string name)
        {
            PdfArray? found = null;
            WalkNameTree(root, (key, val) =>
            {
                string keyStr = key is PdfString ks ? ks.Value : key?.ToString() ?? "";
                if (keyStr != name) return false;
                found = DestArray(val);
                return found != null;
            });
            return found;
        }

        /// <summary>
        /// Builds a map of named destination → 0-based page index from a source document's
        /// /Dests dictionary and /Names /Dests name tree.
        /// </summary>
        internal static Dictionary<string, int> BuildMap(PdfDocument src)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                var catalog = src.Internals.Catalog;

                // Legacy flat /Dests dictionary
                var destsDict = catalog.Elements.GetDictionary("/Dests");
                if (destsDict != null)
                {
                    foreach (var key in destsDict.Elements.Keys)
                    {
                        PdfItem? val = PdfItems.Deref(destsDict.Elements[key] ?? new PdfInteger(-1));
                        int? idx = ResolvePageIndex(src, val);
                        if (idx.HasValue) map[key.TrimStart('/')] = idx.Value;
                    }
                }

                // Modern /Names /Dests name tree
                var destTree = catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/Dests");
                if (destTree != null)
                {
                    WalkNameTree(destTree, (keyItem, val) =>
                    {
                        string key = keyItem is PdfString ks ? ks.Value : keyItem?.ToString()?.TrimStart('/') ?? "";
                        if (string.IsNullOrEmpty(key)) return false;
                        int? idx = ResolvePageIndex(src, val);
                        if (idx.HasValue) map[key] = idx.Value;
                        return false;
                    });
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"BuildNamedDestMap: {ex}"); }
            return map;
        }

        /// <summary>
        /// Resolves a destination value (array, or dictionary with /D) to a page index within
        /// <paramref name="src"/> by matching the page object number.
        /// </summary>
        internal static int? ResolvePageIndex(PdfDocument src, PdfItem? val)
        {
            PdfArray? arr = DestArray(val);
            if (arr is null || arr.Elements.Count == 0) return null;

            var first = arr.Elements[0];
            int objNum = PdfItems.ObjectNumber(first);
            if (objNum > 0)
            {
                for (int i = 0; i < src.PageCount; i++)
                {
                    var pgRef = src.Pages[i].Reference;
                    if (pgRef != null && pgRef.ObjectNumber == objNum) return i;
                }
            }
            else if (first is PdfInteger pi && pi.Value >= 0 && pi.Value < src.PageCount)
            {
                return pi.Value;
            }
            return null;
        }

        /// <summary>
        /// Rewrites every named-destination link on pages [pageOffset, doc.PageCount) to an explicit
        /// [pageRef /Fit] array using the merged document's page references. PdfSharpCore's import does
        /// not copy the source catalog's /Names /Dests, so merged links would otherwise dangle.
        /// </summary>
        internal static void RewriteLinks(PdfDocument doc, int pageOffset, Dictionary<string, int> namedDestMap)
        {
            for (int pi = pageOffset; pi < doc.PageCount; pi++)
            {
                try
                {
                    var annotsArr = doc.Pages[pi].Elements.GetArray("/Annots");
                    if (annotsArr is null) continue;

                    for (int ai = 0; ai < annotsArr.Elements.Count; ai++)
                    {
                        PdfItem? elem = annotsArr.Elements[ai];
                        PdfDictionary? ann = elem as PdfDictionary ?? PdfItems.DerefDict(elem);
                        if (ann is null) continue;

                        var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                        if (!subtype.Contains("Link")) continue;

                        // Check /A /D (GoTo action)
                        var actionDict = ann.Elements.GetDictionary("/A");
                        if (actionDict != null)
                        {
                            var s = actionDict.Elements["/S"]?.ToString() ?? "";
                            if (s.Contains("GoTo"))
                            {
                                string? name = ExtractName(actionDict.Elements["/D"]);
                                if (name != null && namedDestMap.TryGetValue(name, out int srcIdx))
                                {
                                    int targetIdx = pageOffset + srcIdx;
                                    if (targetIdx < doc.PageCount)
                                        actionDict.Elements["/D"] = MakeExplicitDest(doc, targetIdx);
                                }
                            }
                        }
                        else
                        {
                            // Bare /Dest on annotation
                            string? name = ExtractName(ann.Elements["/Dest"]);
                            if (name != null && namedDestMap.TryGetValue(name, out int srcIdx))
                            {
                                int targetIdx = pageOffset + srcIdx;
                                if (targetIdx < doc.PageCount)
                                    ann.Elements["/Dest"] = MakeExplicitDest(doc, targetIdx);
                            }
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RewriteNamedDestLinks p{pi}: {ex}"); }
            }
        }

        internal static string? ExtractName(PdfItem? item)
        {
            if (item is null) return null;
            if (item is PdfString ps) return ps.Value;
            if (item is PdfName   pn) return pn.Value.TrimStart('/');
            return null;
        }

        internal static PdfArray MakeExplicitDest(PdfDocument doc, int pageIndex)
        {
            var arr = new PdfArray(doc);
            arr.Elements.Add(doc.Pages[pageIndex].Reference);
            arr.Elements.Add(new PdfName("/Fit"));
            return arr;
        }
    }
}
