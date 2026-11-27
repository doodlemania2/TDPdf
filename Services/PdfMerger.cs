using System.IO;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using TDPdf.Diagnostics;

namespace TDPdf.Services
{
    /// <summary>
    /// Builds the merged document for File > Merge Files: the chosen pages of the chosen files, in
    /// order, with form fields, named-destination links and bookmarks carried across.
    /// </summary>
    /// <remarks>
    /// Bookmarks: each file's outline is appended under ONE top-level bookmark named after the file,
    /// rather than merged into the top level. That is the choice that cannot go wrong. Two reports
    /// that both start with "Introduction" and "Summary" would otherwise interleave into an outline
    /// no one can tell apart, and a file with no outline at all would vanish from the navigation. As
    /// a bonus the file bookmark is a way to jump to where each file begins. An entry whose page was
    /// dropped is left out and its children move up a level, so nothing points at a page that is gone.
    ///
    /// Pages are read from an Import-mode open of each file; the outline and the named destinations
    /// come from a second, read-only open, as File > Merge has always done. A file is closed as soon
    /// as its last page has been taken, so a long list does not hold every file open at once.
    /// </remarks>
    internal static class PdfMerger
    {
        internal sealed record Report(
            int Pages, int FormFieldsRenamed, int FileBookmarks, int OutlineEntries, IReadOnlyList<string> Notes);

        private const int MaxOutlineDepth = 32;
        private const int MaxOutlineEntriesPerFile = 20000;

        /// <summary>
        /// Writes the merged document to <paramref name="destPath"/>. Honours <paramref name="ct"/>
        /// between pages' worth of work; a cancelled merge leaves no file.
        /// </summary>
        internal static Report Merge(
            IReadOnlyList<MergeSource> sources,
            IReadOnlyList<MergePage> pages,
            string destPath,
            IProgress<string>? progress,
            CancellationToken ct,
            bool bookmarks = true)
        {
            if (pages.Count == 0) throw new InvalidOperationException("There are no pages to merge.");
            var byId = sources.ToDictionary(s => s.Id);
            var runs = MergePlan.BuildRuns(pages);

            // Where each file's last run is, so it can be closed there.
            var lastRunOf = new Dictionary<int, int>();
            for (int r = 0; r < runs.Count; r++) lastRunOf[runs[r].SourceId] = r;

            var notes = new List<string>();
            int renamed = 0;
            using var outPdf = new PdfDocument();
            var open = new Dictionary<int, (PdfDocument Import, PdfDocument Read)>();

            // (source id, source page) -> page in the merged document.
            var finalIndex = new Dictionary<MergePage, int>();
            var finalPagesOf = new Dictionary<int, List<int>>();

            // Outlines are read while their source is open and attached once the top-level bookmarks
            // exist, so the file bookmarks can be created in result order.
            var pendingOutlines = new Dictionary<int, OutlineSnapshot>();

            try
            {
                for (int r = 0; r < runs.Count; r++)
                {
                    ct.ThrowIfCancellationRequested();
                    var run = runs[r];
                    var source = byId[run.SourceId];
                    progress?.Report($"Adding {source.DisplayName}  ({r + 1} of {runs.Count})");

                    if (!open.TryGetValue(source.Id, out var docs))
                    {
                        var read = PdfReader.Open(source.WorkPath, PdfDocumentOpenMode.ReadOnly);
                        var import = PdfReader.Open(source.WorkPath, PdfDocumentOpenMode.Import);
                        docs = (import, read);
                        open[source.Id] = docs;
                    }

                    int offset = outPdf.PageCount;
                    foreach (int p in run.PageIndices)
                    {
                        if (p < 0 || p >= docs.Import.PageCount) continue;
                        outPdf.AddPage(docs.Import.Pages[p]);
                        int at = outPdf.PageCount - 1;
                        finalIndex[new MergePage(source.Id, p)] = at;
                        if (!finalPagesOf.TryGetValue(source.Id, out var list)) finalPagesOf[source.Id] = list = new List<int>();
                        list.Add(at);
                    }

                    // The widgets came across with their pages; register their fields (and the /DR
                    // fonts their appearances name) in the merged /AcroForm.
                    var form = PdfFormMerge.AdoptImportedWidgets(outPdf, offset, docs.Import);
                    renamed += form.FieldsRenamed;
                    notes.AddRange(form.Notes);

                    if (lastRunOf[source.Id] == r)
                    {
                        // Everything that needs the source open happens before it is let go.
                        Finish(outPdf, source, docs.Read, finalIndex, finalPagesOf, pendingOutlines, notes);
                        docs.Import.Dispose();
                        docs.Read.Dispose();
                        open.Remove(source.Id);
                    }
                }

                int fileBookmarks = 0, entries = 0;
                if (bookmarks)
                {
                    // File-level bookmarks in the order the files appear in the result.
                    foreach (var id in OrderOfFirstPage(pages))
                    {
                        ct.ThrowIfCancellationRequested();
                        var source = byId[id];
                        if (!finalPagesOf.TryGetValue(id, out var own) || own.Count == 0) continue;
                        var top = outPdf.Outlines.Add(source.DisplayName, outPdf.Pages[own.Min()], false);
                        fileBookmarks++;
                        if (pendingOutlines.TryGetValue(id, out var tree))
                            entries += tree.Attach(outPdf, top.Outlines);
                    }
                }

                ct.ThrowIfCancellationRequested();
                progress?.Report("Saving...");
                // Not Normalize(): its outline scrub drops any /Outlines whose root has no /First yet, and
                // the writer only links the tree we just built when it saves.
                PdfSaveNormalizer.ScrubDegeneratePageBoxes(outPdf);
                PdfSaveNormalizer.ScrubDeadSignatures(outPdf);
                AtomicFileWriter.Write(outPdf.Save, destPath, ct);
                return new Report(outPdf.PageCount, renamed, fileBookmarks, entries, notes);
            }
            finally
            {
                foreach (var (import, read) in open.Values)
                {
                    try { import.Dispose(); } catch { }
                    try { read.Dispose(); } catch { }
                }
            }
        }

        private static IEnumerable<int> OrderOfFirstPage(IReadOnlyList<MergePage> pages)
        {
            var seen = new HashSet<int>();
            foreach (var p in pages)
                if (seen.Add(p.SourceId)) yield return p.SourceId;
        }

        private static void Finish(
            PdfDocument outPdf, MergeSource source, PdfDocument read,
            Dictionary<MergePage, int> finalIndex, Dictionary<int, List<int>> finalPagesOf,
            Dictionary<int, OutlineSnapshot> pendingOutlines, List<string> notes)
        {
            // Named destinations: explicit page links in place of names the merge does not carry.
            try
            {
                var map = PdfNamedDestinations.BuildMap(read);
                if (map.Count > 0 && finalPagesOf.TryGetValue(source.Id, out var own))
                {
                    var resolved = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var (name, srcPage) in map)
                        if (finalIndex.TryGetValue(new MergePage(source.Id, srcPage), out int at)) resolved[name] = at;
                    PdfNamedDestinations.RewriteLinksOnPages(outPdf, own, resolved);
                }
            }
            catch (Exception ex) { FaultSink.Report("PdfMerger.NamedDestinations", ex); }

            // The outline, as a snapshot of titles and (final) pages that no longer needs the source.
            try
            {
                var snap = OutlineSnapshot.Read(read, source.Id, finalIndex);
                if (snap is not null) pendingOutlines[source.Id] = snap;
            }
            catch (Exception ex)
            {
                FaultSink.Report("PdfMerger.Outline", ex);
                notes.Add($"The bookmarks of {source.DisplayName} could not be read and were left out.");
            }
        }

        /// <summary>A source's outline reduced to titles and destination pages in the MERGED document.</summary>
        private sealed class OutlineSnapshot
        {
            private sealed record Node(string Title, int? Page, List<Node> Children);
            private readonly List<Node> _roots = new();

            internal static OutlineSnapshot? Read(
                PdfDocument read, int sourceId, Dictionary<MergePage, int> finalIndex)
            {
                // Never touch .Outlines on a document that has none: PdfSharpCore would create one.
                if (!read.Internals.Catalog.Elements.ContainsKey("/Outlines")) return null;
                var col = read.Outlines;
                if (col.Count == 0) return null;

                var pageIndex = new Dictionary<PdfPage, int>(ReferenceEqualityComparer.Instance);
                for (int i = 0; i < read.PageCount; i++) pageIndex[read.Pages[i]] = i;
                Dictionary<string, int>? named = null;

                int budget = MaxOutlineEntriesPerFile;
                int? Resolve(PdfOutline o)
                {
                    int? src = null;
                    if (o.DestinationPage is PdfPage dp && pageIndex.TryGetValue(dp, out int idx)) src = idx;
                    if (src is null)
                    {
                        // A named destination: /Dest (name or string) or a GoTo action's /D.
                        PdfItem? d = o.Elements["/Dest"];
                        if (d is null && o.Elements.GetDictionary("/A") is { } a) d = a.Elements["/D"];
                        string? name = PdfNamedDestinations.ExtractName(d);
                        if (name is not null)
                        {
                            named ??= PdfNamedDestinations.BuildMap(read);
                            if (named.TryGetValue(name, out int ni)) src = ni;
                        }
                    }
                    if (src is null) return null;
                    return finalIndex.TryGetValue(new MergePage(sourceId, src.Value), out int at) ? at : null;
                }

                List<Node> Walk(PdfOutlineCollection c, int depth)
                {
                    var list = new List<Node>();
                    if (depth > MaxOutlineDepth) return list;
                    foreach (PdfOutline o in c)
                    {
                        if (--budget < 0) break;
                        var kids = Walk(o.Outlines, depth + 1);
                        int? page = Resolve(o);
                        if (page is null)
                        {
                            // Nothing to jump to here any more; its children still do.
                            list.AddRange(kids);
                            continue;
                        }
                        list.Add(new Node(o.Title ?? "", page, kids));
                    }
                    return list;
                }

                var snap = new OutlineSnapshot();
                snap._roots.AddRange(Walk(col, 0));
                return snap._roots.Count == 0 ? null : snap;
            }

            /// <summary>Appends the entries under <paramref name="into"/>. Returns how many were added.</summary>
            internal int Attach(PdfDocument doc, PdfOutlineCollection into)
            {
                int added = 0;
                void Add(List<Node> nodes, PdfOutlineCollection target)
                {
                    foreach (var n in nodes)
                    {
                        if (n.Page is not int p || p < 0 || p >= doc.PageCount) continue;
                        var o = target.Add(n.Title, doc.Pages[p], false);
                        added++;
                        if (n.Children.Count > 0) Add(n.Children, o.Outlines);
                    }
                }
                Add(_roots, into);
                return added;
            }
        }
    }

    /// <summary>
    /// Opens a PDF for the merge list: how many pages it has and where to read them from. A
    /// password-protected file is decrypted to a working copy, so the merge itself never needs the
    /// password again.
    /// </summary>
    internal static class MergeSourceLoader
    {
        internal enum Outcome { Ok, NeedsPassword, Failed }

        internal sealed record Loaded(Outcome Outcome, MergeSource? Source, string? Error);

        /// <summary>
        /// Reads <paramref name="path"/>. With <paramref name="password"/> null the file is tried with
        /// no password (a file with only an owner password opens that way); a file that needs one comes
        /// back as <see cref="Outcome.NeedsPassword"/>, and the caller asks and tries again.
        /// </summary>
        /// <param name="newWorkPath">Where to put the decrypted copy, only called for an encrypted file.</param>
        internal static Loaded Load(int id, string path, string displayName, string? password, Func<string> newWorkPath)
        {
            try
            {
                if (!PdfEncryptionScan.FileHasEncryption(path))
                {
                    using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                    return new Loaded(Outcome.Ok, new MergeSource(id, displayName, path, doc.PageCount), null);
                }

                PdfDocument opened;
                try { opened = PdfReader.OpenAllowingUserPassword(path, password ?? string.Empty); }
                catch (Exception ex) when (LooksLikePassword(ex))
                {
                    return new Loaded(Outcome.NeedsPassword, null, ex.Message);
                }

                using (opened)
                {
                    string work = newWorkPath();
                    opened.Save(work);
                    int count = opened.PageCount;
                    return new Loaded(Outcome.Ok, new MergeSource(id, displayName, work, count, isTemporary: true), null);
                }
            }
            catch (Exception ex)
            {
                return new Loaded(Outcome.Failed, null, ex.Message);
            }
        }

        private static bool LooksLikePassword(Exception ex) =>
            ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("protected", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("encrypted", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
