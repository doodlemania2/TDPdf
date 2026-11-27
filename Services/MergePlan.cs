using System.IO;

namespace TDPdf.Services
{
    /// <summary>One file in the Merge Files list.</summary>
    internal sealed class MergeSource
    {
        internal MergeSource(int id, string displayName, string workPath, int pageCount,
                             bool isOpenDocument = false, bool isTemporary = false)
        {
            IsTemporary = isTemporary;
            Id = id;
            DisplayName = displayName;
            WorkPath = workPath;
            PageCount = pageCount;
            IsOpenDocument = isOpenDocument;
        }

        internal int Id { get; }

        /// <summary>The file's name as listed, and the title of its top-level bookmark.</summary>
        internal string DisplayName { get; }

        /// <summary>
        /// The file the pages are read from: the user's file, or an unprotected working copy when it
        /// was password protected, or the saved snapshot of the document that was open.
        /// </summary>
        internal string WorkPath { get; }

        internal int PageCount { get; }

        /// <summary>The tab that was open when the dialog started (it always starts the list).</summary>
        internal bool IsOpenDocument { get; }

        /// <summary>
        /// <see cref="WorkPath"/> is a copy TDPdf made (a decrypted file, a snapshot of the open
        /// document) and is deleted once the merge is over; otherwise it is the user's own file.
        /// </summary>
        internal bool IsTemporary { get; }
    }

    /// <summary>One page of one source.</summary>
    internal readonly record struct MergePage(int SourceId, int PageIndex);

    /// <summary>A page in the arrangement: where it comes from and whether it will be merged.</summary>
    internal sealed class MergePageEntry
    {
        internal MergePageEntry(int sourceId, int pageIndex)
        {
            SourceId = sourceId;
            PageIndex = pageIndex;
        }

        internal int SourceId { get; }
        internal int PageIndex { get; }
        internal bool Included { get; set; } = true;
        internal MergePage Page => new(SourceId, PageIndex);
    }

    /// <summary>Consecutive included pages that come from one file: merged in one go.</summary>
    internal sealed record MergeRun(int SourceId, IReadOnlyList<int> PageIndices);

    /// <summary>
    /// The ordering and page selection behind File > Merge Files: which files, in what order, and
    /// (once "Arrange pages" is used) which of their pages in what order. No UI and no PDF reading,
    /// so every rule here is pinned by a test.
    /// </summary>
    /// <remarks>
    /// Two modes share one list. Until the pages are arranged the page list simply follows the file
    /// order, so reordering files reorders pages. Once the user moves or drops a page the arrangement
    /// is theirs: file moves no longer reshuffle it, files added later go on the end, and a removed
    /// file takes its pages with it. <see cref="ResetArrangement"/> goes back to following the files.
    /// </remarks>
    internal sealed class MergePlan
    {
        private readonly List<MergeSource> _sources = new();
        private readonly List<MergePageEntry> _pages = new();
        private int _nextId = 1;

        internal IReadOnlyList<MergeSource> Sources => _sources;
        internal IReadOnlyList<MergePageEntry> Pages => _pages;

        /// <summary>True once a page has been moved or dropped by hand.</summary>
        internal bool Arranged { get; private set; }

        internal int NextSourceId() => _nextId++;

        internal int TotalPages => _pages.Count;
        internal int IncludedCount => _pages.Count(p => p.Included);

        internal MergeSource? Find(int id) => _sources.FirstOrDefault(s => s.Id == id);

        /// <summary>Adds a file at <paramref name="index"/> (the end by default; the open document goes first).</summary>
        internal void AddSource(MergeSource source, int? index = null)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (_sources.Any(s => s.Id == source.Id)) throw new ArgumentException("A file with that id is already listed.");
            _nextId = Math.Max(_nextId, source.Id + 1);

            int at = Math.Clamp(index ?? _sources.Count, 0, _sources.Count);
            _sources.Insert(at, source);

            if (!Arranged)
            {
                Regenerate();
            }
            else
            {
                // Hand-arranged: the new file's pages go on the end, whatever its place in the file list.
                for (int p = 0; p < source.PageCount; p++) _pages.Add(new MergePageEntry(source.Id, p));
            }
        }

        /// <summary>Removes a file and every one of its pages. False when no such file is listed.</summary>
        internal bool RemoveSource(int id)
        {
            int i = _sources.FindIndex(s => s.Id == id);
            if (i < 0) return false;
            _sources.RemoveAt(i);
            _pages.RemoveAll(p => p.SourceId == id);
            if (!Arranged) Regenerate();
            return true;
        }

        /// <summary>Moves the file at <paramref name="from"/> so it sits at <paramref name="to"/>. Returns where it ended up.</summary>
        internal int MoveSource(int from, int to)
        {
            int landed = MoveItem(_sources, from, to);
            if (!Arranged) Regenerate();
            return landed;
        }

        /// <summary>Moves one page of the arrangement. Marks the arrangement as hand-made.</summary>
        internal int MovePage(int from, int to)
        {
            int landed = MoveItem(_pages, from, to);
            if (landed != from) Arranged = true;
            return landed;
        }

        /// <summary>Includes or drops one page. Marks the arrangement as hand-made.</summary>
        internal void SetIncluded(int index, bool included)
        {
            if (index < 0 || index >= _pages.Count) return;
            if (_pages[index].Included == included) return;
            _pages[index].Included = included;
            Arranged = true;
        }

        /// <summary>Includes or drops the page whose entry this is (the row a person ticked).</summary>
        internal void SetIncluded(MergePageEntry entry, bool included) =>
            SetIncluded(_pages.IndexOf(entry), included);

        /// <summary>Back to every page of every file, in file order.</summary>
        internal void ResetArrangement()
        {
            Arranged = false;
            Regenerate();
        }

        /// <summary>The pages that will be merged, in order.</summary>
        internal List<MergePage> IncludedPages() => _pages.Where(p => p.Included).Select(p => p.Page).ToList();

        /// <summary>
        /// The included pages grouped into runs of consecutive pages from the same file, so a file
        /// that was not rearranged is read once.
        /// </summary>
        internal List<MergeRun> Runs() => BuildRuns(IncludedPages());

        internal static List<MergeRun> BuildRuns(IReadOnlyList<MergePage> pages)
        {
            var runs = new List<MergeRun>();
            List<int>? current = null;
            int currentSource = -1;
            foreach (var p in pages)
            {
                if (current is null || p.SourceId != currentSource)
                {
                    current = new List<int>();
                    currentSource = p.SourceId;
                    runs.Add(new MergeRun(p.SourceId, current));
                }
                current.Add(p.PageIndex);
            }
            return runs;
        }

        /// <summary>
        /// Every page of every file in file order, all included. Only runs while nothing has been
        /// arranged by hand, so there are no choices to keep.
        /// </summary>
        private void Regenerate()
        {
            _pages.Clear();
            foreach (var s in _sources)
                for (int p = 0; p < s.PageCount; p++)
                    _pages.Add(new MergePageEntry(s.Id, p));
        }

        /// <summary>
        /// Moves the item at <paramref name="from"/> to index <paramref name="to"/> (both clamped).
        /// Returns the index it ended up at. The shared rule for dragging a row.
        /// </summary>
        internal static int MoveItem<T>(IList<T> list, int from, int to)
        {
            if (from < 0 || from >= list.Count) return from;
            to = Math.Clamp(to, 0, list.Count - 1);
            if (to == from) return from;
            var item = list[from];
            list.RemoveAt(from);
            list.Insert(to, item);
            return to;
        }
    }

    /// <summary>
    /// What went into the list: the PDFs found among files and folders that were added or dropped,
    /// and what was set aside.
    /// </summary>
    internal sealed record MergeExpansion(IReadOnlyList<string> Files, IReadOnlyList<string> Skipped);

    internal static class MergeInputs
    {
        /// <summary>Folders can hold a lot; this bounds what one drop can add.</summary>
        internal const int MaxFiles = 2000;

        /// <summary>
        /// Files and folders to a flat list of PDFs. A folder contributes every PDF under it
        /// (sub-folders included), in natural name order; a file that is not a PDF is skipped.
        /// The file system is passed in so the rules are testable.
        /// </summary>
        internal static MergeExpansion Expand(
            IEnumerable<string> paths,
            Func<string, bool> isDirectory,
            Func<string, IEnumerable<string>> enumerateFiles)
        {
            var files = new List<string>();
            var skipped = new List<string>();
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (isDirectory(path))
                {
                    IEnumerable<string> inside;
                    try { inside = enumerateFiles(path).ToList(); }
                    catch { skipped.Add(path); continue; }
                    foreach (var f in inside.OrderBy(f => f, NaturalComparer.Instance))
                    {
                        if (IsPdf(f)) files.Add(f);
                        // A folder's other files are not worth listing one by one.
                    }
                }
                else if (IsPdf(path)) files.Add(path);
                else skipped.Add(path);
                if (files.Count >= MaxFiles)
                {
                    skipped.Add($"(stopped after {MaxFiles} files)");
                    break;
                }
            }
            return new MergeExpansion(files.Take(MaxFiles).ToList(), skipped);
        }

        internal static bool IsPdf(string path) =>
            string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

        /// <summary>A name for a file in the list and in the bookmarks: the file name without ".pdf".</summary>
        internal static string DisplayName(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
    }

    /// <summary>"file2" before "file10", and case ignored: the order a person expects from a folder.</summary>
    internal sealed class NaturalComparer : IComparer<string>
    {
        internal static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    string a = x.Substring(si, i - si).TrimStart('0'), b = y.Substring(sj, j - sj).TrimStart('0');
                    if (a.Length != b.Length) return a.Length < b.Length ? -1 : 1;
                    int c = string.CompareOrdinal(a, b);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
