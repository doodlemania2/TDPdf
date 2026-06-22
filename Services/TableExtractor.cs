using System.Text;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;
using Word = UglyToad.PdfPig.Content.Word;

namespace TDPdf
{
    /// <summary>
    /// Best-effort extraction of tabular text from a PDF into CSV (which Excel opens
    /// directly). This is a lightweight, heuristic "stream"-style detector: words are
    /// grouped into rows by vertical position and into columns by clustering their
    /// left edges across the page. There is no ruling-line or true table-region
    /// recognition, so it works best on clean, left-aligned grid layouts and may merge
    /// or split columns on irregular, right-aligned, or merged-cell tables. Non-tabular
    /// pages still export as a single-column grid of their text.
    ///
    /// Reuses the same PdfPig <c>GetWords()</c> + <c>BoundingBox</c> data already used
    /// by search, drag-select-copy, and <see cref="PdfContentEditor"/>.
    /// </summary>
    internal static class TableExtractor
    {
        /// <summary>
        /// Reads every page of <paramref name="pdfPath"/> and returns one CSV document.
        /// Each page's detected grid is written as its own block, preceded by a
        /// "Page N" marker row and separated by a blank line. Pages with no extractable
        /// text are skipped.
        /// </summary>
        /// <returns>The CSV text and the number of pages that contributed content.</returns>
        public static (string Csv, int PagesWithContent) ExtractAllPagesCsv(string pdfPath)
        {
            var sb = new StringBuilder();
            int pagesWithContent = 0;

            using var pigDoc = PdfPigDoc.Open(pdfPath);
            for (int p = 1; p <= pigDoc.NumberOfPages; p++)
            {
                var grid = BuildGrid(pigDoc.GetPage(p).GetWords());
                if (grid.Count == 0) continue;

                pagesWithContent++;
                if (sb.Length > 0) sb.Append("\r\n");          // blank line between page blocks
                sb.Append(CsvEscape($"Page {p}")).Append("\r\n");
                foreach (var row in grid)
                    sb.Append(string.Join(",", row.Select(CsvEscape))).Append("\r\n");
            }

            return (sb.ToString(), pagesWithContent);
        }

        /// <summary>
        /// Turns a page's words into a rectangular string grid by clustering them into
        /// rows (by vertical position) and columns (by left-edge position).
        /// </summary>
        private static List<List<string>> BuildGrid(IEnumerable<Word> wordsSource)
        {
            var words = wordsSource.Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            if (words.Count == 0) return new List<List<string>>();

            // --- Rows: cluster by BoundingBox.Top. PDF origin is bottom-left, so a
            // larger Top is higher on the page; we walk top-to-bottom. Threshold mirrors
            // WordsToText in MainWindow (40% of average glyph height). ---
            double avgH = words.Average(w => w.BoundingBox.Height);
            double rowThresh = Math.Max(4.0, avgH * 0.4);

            var rows = new List<List<Word>>();
            double lineY = double.MaxValue;
            foreach (var w in words.OrderByDescending(w => w.BoundingBox.Top))
            {
                if (rows.Count == 0 || Math.Abs(w.BoundingBox.Top - lineY) > rowThresh)
                {
                    rows.Add(new List<Word>());
                    lineY = w.BoundingBox.Top;
                }
                rows[^1].Add(w);
            }

            // --- Columns: cluster every word's left edge across the whole page. Most
            // rows share the same column start positions, so distinct columns surface as
            // distinct clusters of left edges. Each cluster's first (smallest) left is a
            // column anchor. ---
            double medH = Median(words.Select(w => w.BoundingBox.Height).ToList());
            double colThresh = Math.Max(3.0, medH * 0.5);

            var lefts = words.Select(w => w.BoundingBox.Left).OrderBy(x => x).ToList();
            var anchors = new List<double> { lefts[0] };
            double prev = lefts[0];
            foreach (var x in lefts.Skip(1))
            {
                if (x - prev > colThresh) anchors.Add(x);
                prev = x;
            }

            // --- Fill the grid: place each word in the column whose anchor starts its
            // cluster (the last anchor at or below the word's left). Words sharing a cell
            // are joined with a space. ---
            var grid = new List<List<string>>(rows.Count);
            foreach (var row in rows)
            {
                var cells = new string[anchors.Count];
                foreach (var w in row.OrderBy(w => w.BoundingBox.Left))
                {
                    int col = ColumnIndex(anchors, w.BoundingBox.Left);
                    cells[col] = string.IsNullOrEmpty(cells[col]) ? w.Text : cells[col] + " " + w.Text;
                }
                grid.Add(cells.Select(c => c ?? string.Empty).ToList());
            }

            return grid;
        }

        /// <summary>Index of the last column anchor at or below <paramref name="left"/>.</summary>
        private static int ColumnIndex(List<double> anchors, double left)
        {
            int col = 0;
            for (int i = 0; i < anchors.Count; i++)
            {
                if (left >= anchors[i] - 0.5) col = i;   // small tolerance for float jitter
                else break;                              // anchors are sorted ascending
            }
            return col;
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
        }

        /// <summary>RFC-4180 field escaping: quote when the value contains a comma, quote, or newline.</summary>
        private static string CsvEscape(string field)
        {
            field ??= string.Empty;
            bool needsQuote = field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            return needsQuote ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
        }
    }
}
