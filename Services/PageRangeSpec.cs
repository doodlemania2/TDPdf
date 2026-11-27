using System.Text;

namespace TDPdf.Services
{
    /// <summary>How Extract Pages writes the pages it was given.</summary>
    internal enum ExtractMode
    {
        /// <summary>All the pages in one PDF.</summary>
        OneFile,
        /// <summary>One PDF per page.</summary>
        OnePerPage,
        /// <summary>One PDF for every N pages.</summary>
        EveryN
    }

    /// <summary>One output file of an extraction: its name and the 0-based pages that go in it.</summary>
    internal sealed record ExtractPart(string FileName, IReadOnlyList<int> Pages);

    /// <summary>
    /// A typed page list such as "1-3,7" (1-based) and the plan for extracting it. Pure logic, shared
    /// by Extract Pages and Delete Pages.
    /// </summary>
    internal static class PageRangeSpec
    {
        /// <summary>
        /// Parses "1-3,7, 9-10" into sorted, distinct 0-based page indices. Items are separated by
        /// commas, semicolons or spaces; a range may run either way ("5-2" is 2-5) and may use an
        /// en dash. Fails with a message for the user when the text is empty, not a list of numbers
        /// and ranges, or names a page the document does not have.
        /// </summary>
        public static bool TryParse(string? spec, int pageCount, out List<int> pages, out string error)
        {
            pages = new List<int>();
            error = string.Empty;
            if (pageCount < 1) { error = "The document has no pages."; return false; }
            if (string.IsNullOrWhiteSpace(spec)) { error = "Type the pages, for example 1-3,7."; return false; }

            var set = new SortedSet<int>();
            var items = spec.Replace('–', '-').Replace('—', '-')
                .Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var item in items)
            {
                int dash = item.IndexOf('-');
                int a, b;
                if (dash < 0)
                {
                    if (!TryNumber(item, out a)) { error = $"\"{item}\" is not a page number."; return false; }
                    b = a;
                }
                else
                {
                    if (!TryNumber(item[..dash], out a) || !TryNumber(item[(dash + 1)..], out b))
                    { error = $"\"{item}\" is not a page range. Use a form like 3-5."; return false; }
                }
                if (a > b) (a, b) = (b, a);
                if (a < 1 || b > pageCount)
                {
                    error = pageCount == 1
                        ? $"Page {(a < 1 ? a : b)} is outside the document, which has 1 page."
                        : $"Page {(a < 1 ? a : b)} is outside the document, which has {pageCount} pages.";
                    return false;
                }
                for (int p = a; p <= b; p++) set.Add(p - 1);
            }
            if (set.Count == 0) { error = "Type the pages, for example 1-3,7."; return false; }
            pages = [.. set];
            return true;
        }

        private static bool TryNumber(string s, out int n) =>
            int.TryParse(s.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n);

        /// <summary>The 0-based pages as a compact 1-based list: 0,1,2,6 becomes "1-3,7".</summary>
        public static string Describe(IReadOnlyList<int> pages0)
        {
            var sorted = pages0.Distinct().OrderBy(p => p).ToList();
            var sb = new StringBuilder();
            for (int i = 0; i < sorted.Count;)
            {
                int j = i;
                while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
                if (sb.Length > 0) sb.Append(',');
                if (j == i) sb.Append(sorted[i] + 1);
                else sb.Append(sorted[i] + 1).Append('-').Append(sorted[j] + 1);
                i = j + 1;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Splits the pages into the files to write. Names are "&lt;name&gt; p1-3,7.pdf" for one file,
        /// "&lt;name&gt; p5.pdf" for one per page and "&lt;name&gt; part 2.pdf" for every N pages. They are
        /// distinct by construction, so a plan never overwrites one of its own files.
        /// </summary>
        public static List<ExtractPart> Plan(IReadOnlyList<int> pages0, ExtractMode mode, int everyN, string baseName)
        {
            var pages = pages0.Distinct().OrderBy(p => p).ToList();
            string name = SafeName(baseName);
            var parts = new List<ExtractPart>();
            if (pages.Count == 0) return parts;

            switch (mode)
            {
                case ExtractMode.OnePerPage:
                    foreach (var p in pages)
                        parts.Add(new ExtractPart($"{name} p{p + 1}.pdf", new[] { p }));
                    break;
                case ExtractMode.EveryN:
                    int n = Math.Max(1, everyN);
                    int k = 1;
                    for (int i = 0; i < pages.Count; i += n, k++)
                        parts.Add(new ExtractPart($"{name} part {k}.pdf", pages.Skip(i).Take(n).ToList()));
                    break;
                default:
                    string label = Describe(pages);
                    // A scattered pick would make a name too long to be useful; say how many instead.
                    string tail = label.Length <= 40 ? $"p{label}" : $"{pages.Count} pages";
                    parts.Add(new ExtractPart($"{name} {tail}.pdf", pages));
                    break;
            }
            return parts;
        }

        /// <summary>Strips characters a file name cannot hold; never returns an empty name.</summary>
        public static string SafeName(string? baseName)
        {
            // The Windows set, spelled out, so the answer does not depend on the machine running it.
            const string bad = "\\/:*?\"<>|";
            var cleaned = new string((baseName ?? "").Where(c => c >= ' ' && bad.IndexOf(c) < 0).ToArray()).Trim().TrimEnd('.', ' ');
            return cleaned.Length == 0 ? "Document" : cleaned;
        }

        /// <summary>
        /// The names in <paramref name="fileNames"/> that already exist according to
        /// <paramref name="exists"/>. Empty means nothing would be overwritten.
        /// </summary>
        public static List<string> Taken(IEnumerable<string> fileNames, Func<string, bool> exists) =>
            fileNames.Where(exists).ToList();
    }
}
