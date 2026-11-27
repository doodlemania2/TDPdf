using System.Text;

namespace TDPdf.Services
{
    /// <summary>A box on one page, in points, measured from the TOP-LEFT of the page as displayed.</summary>
    internal readonly record struct PageBox(int Page, double X, double Y, double W, double H);

    /// <summary>One word of a document with where it sits (points, top-left origin, page as displayed).</summary>
    internal readonly record struct CompareWord(string Text, int Page, double X, double Y, double W, double H);

    /// <summary>What one run of a diff does to the left sequence to make the right one.</summary>
    internal enum DiffOp { Equal, Delete, Insert }

    /// <summary>A run of the diff. For <see cref="DiffOp.Equal"/> both lengths are the same; a Delete has a zero B length and an Insert a zero A length.</summary>
    internal readonly record struct DiffRun(DiffOp Op, int AStart, int ALength, int BStart, int BLength);

    /// <summary>A group of neighbouring differences: the words removed from the left and the words added on the right.</summary>
    internal readonly record struct DiffHunk(int AStart, int ALength, int BStart, int BLength);

    /// <summary>
    /// Myers' O(ND) difference over integer tokens (the shortest edit script), with a cap on the
    /// number of edits so a pair of unrelated documents cannot take unbounded time or memory.
    /// </summary>
    internal static class MyersDiff
    {
        /// <summary>
        /// The runs that turn <paramref name="a"/> into <paramref name="b"/>, or null when it needs more than
        /// <paramref name="maxEdits"/> insertions plus deletions. A shared head and tail are peeled off
        /// first, so two nearly identical documents cost almost nothing however long they are.
        /// Memory is about <c>maxEdits</c> squared ints at worst.
        /// </summary>
        internal static List<DiffRun>? Diff(int[] a, int[] b, int maxEdits)
        {
            int head = 0;
            int limit = Math.Min(a.Length, b.Length);
            while (head < limit && a[head] == b[head]) head++;
            int tail = 0;
            while (tail < limit - head && a[a.Length - 1 - tail] == b[b.Length - 1 - tail]) tail++;

            int n = a.Length - head - tail, m = b.Length - head - tail;
            var runs = new List<DiffRun>();
            if (head > 0) runs.Add(new DiffRun(DiffOp.Equal, 0, head, 0, head));

            if (n > 0 || m > 0)
            {
                var middle = Middle(a, head, n, b, head, m, maxEdits);
                if (middle is null) return null;
                runs.AddRange(middle);
            }

            if (tail > 0)
                runs.Add(new DiffRun(DiffOp.Equal, a.Length - tail, tail, b.Length - tail, tail));
            return Coalesce(runs);
        }

        private static List<DiffRun>? Middle(int[] a, int aOff, int n, int[] b, int bOff, int m, int maxEdits)
        {
            if (n == 0) return new List<DiffRun> { new(DiffOp.Insert, aOff, 0, bOff, m) };
            if (m == 0) return new List<DiffRun> { new(DiffOp.Delete, aOff, n, bOff, 0) };

            int max = (int)Math.Min((long)n + m, maxEdits);
            int offset = max + 1;
            var v = new int[2 * max + 3];
            var trace = new List<int[]>();
            int found = -1;

            for (int d = 0; d <= max && found < 0; d++)
            {
                for (int k = -d; k <= d; k += 2)
                {
                    int x;
                    if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])) x = v[offset + k + 1];
                    else x = v[offset + k - 1] + 1;
                    int y = x - k;
                    while (x < n && y < m && a[aOff + x] == b[bOff + y]) { x++; y++; }
                    v[offset + k] = x;
                    if (x >= n && y >= m) { found = d; break; }
                }
                // The values for k in [-d, d], kept so the path can be walked back.
                var slice = new int[2 * d + 1];
                Array.Copy(v, offset - d, slice, 0, slice.Length);
                trace.Add(slice);
            }
            if (found < 0) return null;

            // Walk back from (n, m), collecting per-token ops in reverse.
            var ops = new List<DiffOp>(n + m);
            int cx = n, cy = m;
            for (int d = found; d > 0; d--)
            {
                var prev = trace[d - 1];   // k in [-(d-1), d-1], index k + (d-1)
                int k = cx - cy;
                int Prev(int kk) => prev[kk + (d - 1)];
                int prevK = (k == -d || (k != d && Prev(k - 1) < Prev(k + 1))) ? k + 1 : k - 1;
                int prevX = Prev(prevK);
                int prevY = prevX - prevK;
                int startX = prevK == k + 1 ? prevX : prevX + 1;
                int startY = prevK == k + 1 ? prevY + 1 : prevY;
                while (cx > startX && cy > startY) { ops.Add(DiffOp.Equal); cx--; cy--; }
                ops.Add(prevK == k + 1 ? DiffOp.Insert : DiffOp.Delete);
                cx = prevX; cy = prevY;
            }
            while (cx > 0 && cy > 0) { ops.Add(DiffOp.Equal); cx--; cy--; }
            ops.Reverse();

            var result = new List<DiffRun>();
            int ai = aOff, bi = bOff;
            foreach (var op in ops)
            {
                switch (op)
                {
                    case DiffOp.Equal: result.Add(new DiffRun(DiffOp.Equal, ai, 1, bi, 1)); ai++; bi++; break;
                    case DiffOp.Delete: result.Add(new DiffRun(DiffOp.Delete, ai, 1, bi, 0)); ai++; break;
                    default: result.Add(new DiffRun(DiffOp.Insert, ai, 0, bi, 1)); bi++; break;
                }
            }
            return result;
        }

        /// <summary>Joins neighbouring runs of the same kind.</summary>
        private static List<DiffRun> Coalesce(List<DiffRun> runs)
        {
            var result = new List<DiffRun>();
            foreach (var r in runs)
            {
                if (result.Count > 0 && result[^1].Op == r.Op)
                {
                    var p = result[^1];
                    result[^1] = new DiffRun(p.Op, p.AStart, p.ALength + r.ALength, p.BStart, p.BLength + r.BLength);
                }
                else result.Add(r);
            }
            return result;
        }

        /// <summary>The differences as hunks: every maximal stretch of non-equal runs is one hunk.</summary>
        internal static List<DiffHunk> Hunks(IReadOnlyList<DiffRun> runs)
        {
            var hunks = new List<DiffHunk>();
            int aStart = -1, bStart = -1, aLen = 0, bLen = 0;
            void Flush()
            {
                if (aStart >= 0 && (aLen > 0 || bLen > 0)) hunks.Add(new DiffHunk(aStart, aLen, bStart, bLen));
                aStart = -1; aLen = 0; bLen = 0;
            }
            foreach (var r in runs)
            {
                if (r.Op == DiffOp.Equal) { Flush(); continue; }
                if (aStart < 0) { aStart = r.AStart; bStart = r.BStart; }
                aLen += r.ALength;
                bLen += r.BLength;
            }
            Flush();
            return hunks;
        }
    }

    /// <summary>Word-level comparison of two documents' text, and turning a stretch of words into highlight boxes.</summary>
    internal static class PdfCompareText
    {
        /// <summary>The edit budget for one diff; beyond it the caller falls back to a smaller scope.</summary>
        internal const int DefaultMaxEdits = 2500;

        /// <summary>
        /// How a word is compared: ligature code points expanded, compatibility forms folded, invisible
        /// characters dropped. Case is kept, because a change of case is a change.
        /// </summary>
        internal static string Token(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '­': case '​': case '‌': case '‍': case '⁠': case '﻿':
                        continue;
                }
                sb.Append(c);
            }
            string s = sb.ToString();
            try { s = s.Normalize(NormalizationForm.FormKC); } catch (ArgumentException) { /* lone surrogates: keep as is */ }
            return s.Trim();
        }

        /// <summary>Hunks between two word sequences, or null when they differ by more than <paramref name="maxEdits"/> words.</summary>
        internal static List<DiffHunk>? Compare(
            IReadOnlyList<CompareWord> left, IReadOnlyList<CompareWord> right, int maxEdits = DefaultMaxEdits)
        {
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            int[] Map(IReadOnlyList<CompareWord> words)
            {
                var arr = new int[words.Count];
                for (int i = 0; i < arr.Length; i++)
                {
                    string t = Token(words[i].Text);
                    if (!ids.TryGetValue(t, out int id)) ids[t] = id = ids.Count;
                    arr[i] = id;
                }
                return arr;
            }
            var a = Map(left);
            var b = Map(right);
            var runs = MyersDiff.Diff(a, b, maxEdits);
            return runs is null ? null : MyersDiff.Hunks(runs);
        }

        /// <summary>
        /// One box per line per page for <paramref name="count"/> words from <paramref name="start"/>: words on the
        /// same line that sit close together are joined, so a changed sentence is one highlight rather than a dozen.
        /// </summary>
        internal static List<PageBox> LineBoxes(IReadOnlyList<CompareWord> words, int start, int count)
        {
            var boxes = new List<PageBox>();
            double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            int page = -1;
            bool open = false;
            void Flush()
            {
                if (open) boxes.Add(new PageBox(page, x0, y0, x1 - x0, y1 - y0));
                open = false;
            }
            for (int i = start; i < start + count && i < words.Count; i++)
            {
                var w = words[i];
                if (w.W <= 0 || w.H <= 0 || double.IsNaN(w.X + w.Y + w.W + w.H)) continue;
                double wr = w.X + w.W, wb = w.Y + w.H;
                if (open && w.Page == page)
                {
                    double overlap = Math.Min(y1, wb) - Math.Max(y0, w.Y);
                    double height = Math.Min(y1 - y0, w.H);
                    double gap = w.X - x1;
                    if (overlap >= 0.5 * height && gap <= 1.5 * Math.Max(y1 - y0, w.H) && gap > -(x1 - x0))
                    {
                        x0 = Math.Min(x0, w.X); y0 = Math.Min(y0, w.Y);
                        x1 = Math.Max(x1, wr); y1 = Math.Max(y1, wb);
                        continue;
                    }
                }
                Flush();
                page = w.Page; x0 = w.X; y0 = w.Y; x1 = wr; y1 = wb; open = true;
            }
            Flush();
            return boxes;
        }

        /// <summary>The words of a hunk side as one line of text for the change list, shortened to <paramref name="max"/> characters.</summary>
        internal static string Excerpt(IReadOnlyList<CompareWord> words, int start, int count, int max = 60)
        {
            var sb = new StringBuilder();
            for (int i = start; i < start + count && i < words.Count; i++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(words[i].Text);
                if (sb.Length > max) break;
            }
            string s = sb.ToString();
            return s.Length > max ? s[..(max - 1)].TrimEnd() + "…" : s;
        }
    }
}
