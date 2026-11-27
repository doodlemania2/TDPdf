namespace TDPdf.Services
{
    /// <summary>How one row of the comparison relates the two documents.</summary>
    internal enum PairKind { Matched, Added, Removed, Moved }

    /// <summary>
    /// One row of the comparison: a left page, a right page, or both. Added has no left page, Removed has no
    /// right page; Moved has both (the page was found at a different place in the other document).
    /// </summary>
    internal readonly record struct PagePair(int? Left, int? Right, PairKind Kind);

    /// <summary>What it takes to decide that two pages are the same page: their words and a small picture.</summary>
    internal sealed class PageFingerprint
    {
        internal PageFingerprint(int[] sortedWordHashes, byte[]? thumbnail)
        {
            WordHashes = sortedWordHashes;
            Thumbnail = thumbnail;
        }

        /// <summary>The hash of every word on the page, sorted, so two pages compare in one merge pass.</summary>
        internal int[] WordHashes { get; }

        /// <summary>16 x 16 grey levels, or null when the page has enough text to be told apart without one.</summary>
        internal byte[]? Thumbnail { get; }

        internal int WordCount => WordHashes.Length;
    }

    /// <summary>
    /// Pairs the pages of two documents (the first step of a comparison) so that an inserted, removed or
    /// moved page shows up as that, not as a page full of differences. Pages are paired by their text and, for
    /// pages with little or no text, by a small thumbnail.
    /// </summary>
    internal static class PdfCompareAlign
    {
        /// <summary>Pages with fewer words than this are told apart by their picture instead.</summary>
        internal const int MinWordsForText = 6;
        /// <summary>The text similarity at which two pages count as the same page.</summary>
        internal const double SameTextSimilarity = 0.6;
        /// <summary>The mean grey-level difference (0 to 1) under which two thumbnails count as the same picture.</summary>
        internal const double SameThumbDistance = 0.08;
        /// <summary>The largest dynamic-programming table used; above it unmatched middles are paired by position.</summary>
        internal const long MaxTableCells = 6_000_000;

        internal static PageFingerprint Fingerprint(IEnumerable<string> normalisedWords, byte[]? thumbnail)
        {
            var hashes = normalisedWords.Select(StableHash).ToArray();
            Array.Sort(hashes);
            return new PageFingerprint(hashes, thumbnail);
        }

        private static int StableHash(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in s) { h ^= c; h *= 16777619; }
                return (int)h;
            }
        }

        /// <summary>The Dice coefficient of two word multisets: 1 for the same words, 0 for none in common.</summary>
        internal static double TextSimilarity(PageFingerprint a, PageFingerprint b)
        {
            int na = a.WordHashes.Length, nb = b.WordHashes.Length;
            if (na == 0 && nb == 0) return 1;
            if (na == 0 || nb == 0) return 0;
            // A page cannot share more than the smaller one has.
            if (2.0 * Math.Min(na, nb) / (na + nb) < SameTextSimilarity * 0.5) return 0;
            int i = 0, j = 0, common = 0;
            while (i < na && j < nb)
            {
                int x = a.WordHashes[i], y = b.WordHashes[j];
                if (x == y) { common++; i++; j++; }
                else if (x < y) i++;
                else j++;
            }
            return 2.0 * common / (na + nb);
        }

        /// <summary>The mean absolute grey difference between two thumbnails, 0 (identical) to 1.</summary>
        internal static double ThumbnailDistance(byte[] a, byte[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            if (n == 0) return 1;
            long sum = 0;
            for (int i = 0; i < n; i++) sum += Math.Abs(a[i] - b[i]);
            return sum / (255.0 * n);
        }

        /// <summary>
        /// How well two pages match, 0 for not at all, or a score above zero when they count as the same
        /// page. Pages with enough words are judged by text alone; sparse pages by their picture, and two
        /// empty pages are the same.
        /// </summary>
        internal static double Score(PageFingerprint a, PageFingerprint b)
        {
            bool aText = a.WordCount >= MinWordsForText, bText = b.WordCount >= MinWordsForText;
            if (aText || bText)
            {
                double s = TextSimilarity(a, b);
                return s >= SameTextSimilarity ? s : 0;
            }
            // Both sparse. Text decides when it is conclusive, the picture otherwise.
            if (a.WordCount == 0 && b.WordCount == 0 && (a.Thumbnail is null || b.Thumbnail is null)) return 1;
            if (a.Thumbnail is { } ta && b.Thumbnail is { } tb)
            {
                double d = ThumbnailDistance(ta, tb);
                return d <= SameThumbDistance ? Math.Max(0.01, 1 - d) : 0;
            }
            double t = TextSimilarity(a, b);
            return t >= SameTextSimilarity ? t : 0;
        }

        /// <summary>Pairs the pages of the two documents. The rows come out in reading order.</summary>
        internal static List<PagePair> Align(IReadOnlyList<PageFingerprint> left, IReadOnlyList<PageFingerprint> right)
        {
            int n = left.Count, m = right.Count;
            var matchedL = new Dictionary<int, int>();   // left index -> right index, in order

            // Peel identical pages off both ends: the common case is two copies of one document.
            int head = 0;
            while (head < n && head < m && Score(left[head], right[head]) >= 0.95) { matchedL[head] = head; head++; }
            int tail = 0;
            while (tail < n - head && tail < m - head && Score(left[n - 1 - tail], right[m - 1 - tail]) >= 0.95)
            {
                matchedL[n - 1 - tail] = m - 1 - tail;
                tail++;
            }

            int ln = n - head - tail, rn = m - head - tail;
            if (ln > 0 && rn > 0)
            {
                if ((long)ln * rn <= MaxTableCells) WeightedLcs(left, right, head, ln, head, rn, matchedL);
                else
                {
                    // Too big for a table: pair by position while the pages still look alike.
                    for (int i = 0; i < Math.Min(ln, rn); i++)
                        if (Score(left[head + i], right[head + i]) > 0) matchedL[head + i] = head + i;
                }
            }

            // Unpaired pages: a left page that matches an unpaired right page elsewhere has moved.
            var matchedR = new HashSet<int>(matchedL.Values);
            var freeR = Enumerable.Range(0, m).Where(j => !matchedR.Contains(j)).ToList();
            var moved = new Dictionary<int, int>();       // right index -> left index
            var movedL = new HashSet<int>();
            for (int i = 0; i < n && freeR.Count > 0; i++)
            {
                if (matchedL.ContainsKey(i)) continue;
                int best = -1; double bestScore = 0;
                foreach (int j in freeR)
                {
                    if (moved.ContainsKey(j)) continue;
                    double s = Score(left[i], right[j]);
                    if (s > bestScore) { bestScore = s; best = j; }
                }
                if (best >= 0) { moved[best] = i; movedL.Add(i); }
            }

            // Emit rows in order, walking both documents past each matched page.
            var rows = new List<PagePair>();
            int li = 0, rj = 0;
            var anchors = matchedL.OrderBy(kv => kv.Key).ToList();
            void Gap(int leftEnd, int rightEnd)
            {
                for (; li < leftEnd; li++)
                    if (!movedL.Contains(li)) rows.Add(new PagePair(li, null, PairKind.Removed));
                for (; rj < rightEnd; rj++)
                    rows.Add(moved.TryGetValue(rj, out int from)
                        ? new PagePair(from, rj, PairKind.Moved)
                        : new PagePair(null, rj, PairKind.Added));
            }
            foreach (var (l, r) in anchors)
            {
                Gap(l, r);
                rows.Add(new PagePair(l, r, PairKind.Matched));
                li = l + 1; rj = r + 1;
            }
            Gap(n, m);
            return rows;
        }

        /// <summary>
        /// The best order-preserving pairing of left[lo..lo+ln) with right[ro..ro+rn): the largest total match score.
        /// </summary>
        private static void WeightedLcs(
            IReadOnlyList<PageFingerprint> left, IReadOnlyList<PageFingerprint> right,
            int lo, int ln, int ro, int rn, Dictionary<int, int> into)
        {
            var best = new double[(ln + 1) * (rn + 1)];
            int W = rn + 1;
            var score = new double[ln * rn];
            for (int i = 0; i < ln; i++)
                for (int j = 0; j < rn; j++)
                    score[i * rn + j] = Score(left[lo + i], right[ro + j]);

            for (int i = ln - 1; i >= 0; i--)
                for (int j = rn - 1; j >= 0; j--)
                {
                    double v = Math.Max(best[(i + 1) * W + j], best[i * W + j + 1]);
                    double s = score[i * rn + j];
                    if (s > 0) v = Math.Max(v, s + best[(i + 1) * W + j + 1]);
                    best[i * W + j] = v;
                }

            int x = 0, y = 0;
            while (x < ln && y < rn)
            {
                double s = score[x * rn + y];
                if (s > 0 && Math.Abs(best[x * W + y] - (s + best[(x + 1) * W + y + 1])) < 1e-12)
                {
                    into[lo + x] = ro + y; x++; y++;
                }
                else if (best[x * W + y] <= best[(x + 1) * W + y] + 1e-12) x++;
                else y++;
            }
        }

        /// <summary>Averages a BGRA raster down to 16 x 16 grey levels.</summary>
        internal static byte[] Thumbnail16(byte[] bgra, int width, int height)
        {
            const int N = 16;
            var result = new byte[N * N];
            if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return result;
            for (int ty = 0; ty < N; ty++)
            {
                int y0 = ty * height / N, y1 = Math.Max(y0 + 1, (ty + 1) * height / N);
                for (int tx = 0; tx < N; tx++)
                {
                    int x0 = tx * width / N, x1 = Math.Max(x0 + 1, (tx + 1) * width / N);
                    long sum = 0; int count = 0;
                    for (int y = y0; y < y1 && y < height; y++)
                        for (int x = x0; x < x1 && x < width; x++)
                        {
                            int p = (y * width + x) * 4;
                            sum += (bgra[p] * 29 + bgra[p + 1] * 150 + bgra[p + 2] * 77) >> 8;
                            count++;
                        }
                    result[ty * N + tx] = count == 0 ? (byte)255 : (byte)Math.Min(255, sum / count);
                }
            }
            return result;
        }
    }
}
