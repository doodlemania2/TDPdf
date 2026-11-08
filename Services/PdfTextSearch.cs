using System.Threading;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace TDPdf.Services
{
    /// <summary>
    /// Finds a search query on a PdfPig page and returns the rectangles of the matched glyphs —
    /// what the search bar highlights and what "Redact matches" turns into redaction marks.
    /// </summary>
    /// <remarks>
    /// The rectangles are in PdfPig's text frame (the page AS DISPLAYED: crop origin subtracted,
    /// /Rotate applied), not PDF user space. Anything that needs user space carries them through
    /// <see cref="PdfPageGeometry.TextFrameToPdf(PdfSharpCore.Pdf.PdfPage, PdfiumInterop.PdfRect)"/>.
    ///
    /// WHY IT IS LETTER-BASED (#187). The search this replaced worked in whole words: for every
    /// word on the page it joined the following words until the joined string CONTAINED the
    /// query, then returned the box of everything it had joined. "Contains" does not require the
    /// match to start in the first word, so every word up to ~20 characters before an occurrence
    /// produced its own hit, spanning from that word to the end of the match. On the demo
    /// incident report "Jordan Ellis" (4 occurrences) gave 17 hits — 14 after duplicate marks
    /// collapsed — covering "badge 30881, issued to Jordan Ellis", the table header row
    /// (Person / Role / Phone / Badge, which precede the first data cell in reading order), and
    /// "was wedged at 21:58. Jordan Ellis". tests/PdfCore TextSearch.cs pins it.
    ///
    /// Here the page's letters are laid out as one string (words joined by single spaces, the
    /// same joining the old search used, so a match may still run across words, cells and line
    /// breaks), every occurrence is found once, and each occurrence becomes the box of exactly
    /// the letters it consists of — split into one box per line, so a match that wraps does not
    /// become a box tall enough to cover both lines and everything between them.
    ///
    /// The mark's size is all this decides. What a mark REMOVES is decided at apply time, by
    /// <see cref="PdfRedaction"/> against whole PDF text objects ("Include partly covered
    /// objects"), and is verified afterwards against every glyph whose centre falls inside a mark.
    /// </remarks>
    internal static class PdfTextSearch
    {
        /// <summary>Breathing room around the glyphs, as a fraction of the line's thickness.</summary>
        /// <remarks>
        /// Glyph boxes are tight to the outlines, so an unpadded mark looks like it clips the
        /// letters. Kept small across the line so a mark cannot reach the line above or below, and
        /// capped along it so it cannot reach the neighbouring letter of a partial-word match.
        /// </remarks>
        internal const double PadFraction = 0.12;
        internal const double MaxAlongPad = 1.0;

        /// <summary>One occurrence of the query: one box per line it occupies.</summary>
        internal sealed record Match(IReadOnlyList<PdfiumInterop.PdfRect> Boxes);

        /// <summary>Every occurrence of <paramref name="query"/> on <paramref name="page"/>.</summary>
        /// <remarks>
        /// Words come from PdfPig's nearest-neighbour extractor, not the default one. On a turned
        /// page (/Rotate 90, 180, 270) the default extractor groups the letters of different
        /// lines into one "word" and reverses runs ("dedni" for "reminded"), so nothing on such a
        /// page could be found at all. The nearest-neighbour extractor keeps content order and
        /// real words at every quarter turn; tests/PdfCore TextSearch.cs searches a page at each.
        /// </remarks>
        internal static List<Match> FindMatches(Page page, string query) =>
            FindMatches(page.GetWords(NearestNeighbourWordExtractor.Instance).ToList(), query);

        /// <summary>The matches as one flat list of boxes (one per match per line), as the search bar keeps them.</summary>
        internal static List<(double left, double bottom, double right, double top)> Flatten(
            IEnumerable<Match> matches) =>
            matches.SelectMany(m => m.Boxes).Select(b => (b.Left, b.Bottom, b.Right, b.Top)).ToList();

        /// <summary>Every occurrence of <paramref name="query"/> in <paramref name="words"/>, case-insensitively.</summary>
        internal static List<Match> FindMatches(IReadOnlyList<Word> words, string query) =>
            FindMatches(words.Select(ToSearchWord).ToList(), query);

        /// <summary>
        /// A PdfPig page reduced to plain data. Extraction is the part that needs a PdfPig document
        /// (and a thread of its own); everything after it is pure and can run, cache and be tested
        /// without one.
        /// </summary>
        internal static List<SearchWord> ExtractWords(Page page) =>
            page.GetWords(NearestNeighbourWordExtractor.Instance).Select(ToSearchWord).ToList();

        /// <summary>One character run (usually one glyph; a ligature glyph carries several characters) and its box in the text frame.</summary>
        internal readonly record struct SearchLetter(
            string Value, double Left, double Bottom, double Right, double Top, bool Horizontal);

        /// <summary>A word as the matcher sees it: its letters, in order, each with its own box.</summary>
        internal sealed record SearchWord(IReadOnlyList<SearchLetter> Letters);

        private static SearchWord ToSearchWord(Word word)
        {
            var letters = word.Letters;
            bool perLetter = letters.Count > 0
                && string.Concat(letters.Select(l => l.Value)) == word.Text;

            var result = new List<SearchLetter>(perLetter ? letters.Count : word.Text.Length);
            if (!perLetter)
            {
                // No usable letter breakdown: every character maps to the whole word, which is
                // the most the page can say about where it is.
                var (l, b, r, t) = PdfBoxExtent.Of(word.BoundingBox);
                bool h = Horizontal(word.Letters);
                foreach (char c in word.Text) result.Add(new SearchLetter(c.ToString(), l, b, r, t, h));
                return new SearchWord(result);
            }

            foreach (var letter in letters)
            {
                var (l, b, r, t) = PdfBoxExtent.Of(letter.BoundingBox);
                result.Add(new SearchLetter(letter.Value, l, b, r, t,
                    IsHorizontal(letter.StartBaseLine, letter.EndBaseLine)));
            }
            return new SearchWord(result);
        }

        /// <summary>Every occurrence of <paramref name="query"/> in <paramref name="input"/>, case-insensitively.</summary>
        /// <remarks>
        /// The page text is normalised before matching, with every character keeping the glyph it
        /// came from so the highlight boxes stay on the right letters: ligature code points
        /// (U+FB00-FB06) are expanded so "fi" finds "ﬁ"; invisible characters (soft hyphen,
        /// zero-width space / joiners, BOM) are dropped; and a word hyphenated across a line break
        /// is also searched joined ("hyphen-" + "ated" matches "hyphenated"). The plain reading
        /// (words joined by a space, hyphen kept) is always searched too, so a literal hyphen is
        /// never lost: "well-known" matches "well-known", and "well-" at a line end followed by
        /// "known" matches "well-known" via the hyphen-kept join.
        /// </remarks>
        internal static List<Match> FindMatches(IReadOnlyList<SearchWord> input, string query)
        {
            var result = new List<Match>();
            string needle = Normalise(query);
            if (needle.Length == 0 || input.Count == 0) return result;

            // Words with no characters at all (the extractor returns the spaces between words as
            // words of their own) are dropped; the join stands for them.
            var words = input.Where(w => w.Letters.Any(l => !string.IsNullOrWhiteSpace(l.Value))).ToList();
            if (words.Count == 0) return result;

            var hits = new List<Hit>();
            Scan(words, needle, JoinMode.Plain, null, hits);

            var joinAfter = LineBreakHyphens(words);
            if (joinAfter.Any(j => j != Join.None))
            {
                var extra = new List<Hit>();
                Scan(words, needle, JoinMode.DropHyphen, joinAfter, extra);
                if (needle.Contains('-')) Scan(words, needle, JoinMode.KeepHyphen, joinAfter, extra);

                // The plain reading wins where two readings found the same place.
                var accepted = new List<Hit>(hits);
                foreach (var h in extra.OrderBy(h => h.Start))
                    if (!accepted.Any(a => h.Start <= a.End && a.Start <= h.End)) { accepted.Add(h); hits.Add(h); }
            }

            foreach (var h in hits.OrderBy(h => h.Start)) result.Add(h.Match);
            return result;
        }

        private readonly record struct Hit(int Start, int End, Match Match);

        /// <summary>How a line-end hyphen may be read: not joined; joined with or without its hyphen; joined only keeping it (2024- / 2025).</summary>
        private enum Join { None, Both, KeepOnly }

        private enum JoinMode { Plain, DropHyphen, KeepHyphen }

        private static void Scan(List<SearchWord> words, string needle, JoinMode mode, Join[]? joinAfter, List<Hit> hits)
        {
            // The page as one string, and for each character the glyph it came from (null for the
            // space joining two words) and its position in the page's raw characters (-1 for the
            // joining space), which is what lets readings be compared.
            var text = new System.Text.StringBuilder();
            var source = new List<Glyph?>();
            var seq = new List<int>();
            int counter = 0;
            bool joined = false;   // the previous word runs on into this one

            for (int w = 0; w < words.Count; w++)
            {
                var wText = new System.Text.StringBuilder();
                var wSource = new List<Glyph?>();
                var wSeq = new List<int>();
                foreach (var letter in words[w].Letters)
                {
                    var g = new Glyph(w, letter.Left, letter.Bottom, letter.Right, letter.Top, letter.Horizontal,
                        Ink: letter.Right > letter.Left && letter.Top > letter.Bottom
                             && double.IsFinite(letter.Left) && double.IsFinite(letter.Right)
                             && double.IsFinite(letter.Bottom) && double.IsFinite(letter.Top)
                             && !string.IsNullOrWhiteSpace(letter.Value));
                    foreach (char c in letter.Value)
                    {
                        int at = counter++;
                        foreach (char n in Expand(c)) { wText.Append(n); wSource.Add(g); wSeq.Add(at); }
                    }
                }
                if (wText.Length == 0) { joined = false; continue; }

                var how = mode != JoinMode.Plain && joinAfter is not null ? joinAfter[w] : Join.None;
                bool join = how == Join.Both || how == Join.KeepOnly && mode == JoinMode.KeepHyphen;
                if (join && mode == JoinMode.DropHyphen && IsHyphen(wText[^1]))
                {
                    wText.Length--; wSource.RemoveAt(wSource.Count - 1); wSeq.RemoveAt(wSeq.Count - 1);
                }

                if (text.Length > 0 && !joined) { text.Append(' '); source.Add(null); seq.Add(-1); }
                text.Append(wText); source.AddRange(wSource); seq.AddRange(wSeq);
                joined = join;
            }

            string hay = text.ToString();
            int pos = 0;
            while (pos <= hay.Length - needle.Length)
            {
                int found = hay.IndexOf(needle, pos, StringComparison.Ordinal);
                if (found < 0) break;

                var boxes = BoxesOf(source, found, needle.Length);
                if (boxes.Count > 0)
                {
                    int start = -1, end = -1;
                    for (int i = found; i < found + needle.Length; i++)
                        if (seq[i] >= 0) { if (start < 0) start = seq[i]; end = seq[i]; }
                    hits.Add(new Hit(start, end, new Match(boxes)));
                }
                pos = found + needle.Length;   // non-overlapping: each occurrence once
            }
        }

        /// <summary>
        /// For each word, whether it is a hyphenated word broken at the end of a line: it ends in
        /// a hyphen after a letter or digit, and the next word starts with a letter on a different
        /// line.
        /// </summary>
        private static Join[] LineBreakHyphens(List<SearchWord> words)
        {
            var result = new Join[words.Count];
            for (int i = 0; i + 1 < words.Count; i++)
            {
                var a = words[i].Letters;
                var b = words[i + 1].Letters;
                string raw = string.Concat(a.Select(l => l.Value)).TrimEnd();
                string vis = StripInvisible(raw);
                string next = StripInvisible(string.Concat(b.Select(l => l.Value)).TrimStart());
                bool soft = raw.Length > 0 && raw[^1] == '\u00AD';   // invisible, but the hyphen of a line-end break
                bool hard = vis.Length > 0 && IsHyphen(vis[^1]);
                if (!soft && !hard) continue;
                string stem = hard ? vis[..^1] : vis;
                if (stem.Length == 0 || !char.IsLetterOrDigit(stem[^1])) continue;
                if (next.Length == 0 || !char.IsLetterOrDigit(next[0])) continue;

                var lastA = LastInk(a); var firstB = FirstInk(b);
                if (lastA is not SearchLetter la || firstB is not SearchLetter fb) continue;
                var ga = new Glyph(0, la.Left, la.Bottom, la.Right, la.Top, la.Horizontal, true);
                var gb = new Glyph(1, fb.Left, fb.Bottom, fb.Right, fb.Top, fb.Horizontal, true);
                if (!SameLine(new List<Glyph> { ga }, gb))
                    result[i] = char.IsLetter(next[0]) ? Join.Both : Join.KeepOnly;
            }
            return result;
        }

        private static SearchLetter? LastInk(IReadOnlyList<SearchLetter> l)
        {
            for (int i = l.Count - 1; i >= 0; i--) if (HasInk(l[i])) return l[i];
            return null;
        }

        private static SearchLetter? FirstInk(IReadOnlyList<SearchLetter> l)
        {
            for (int i = 0; i < l.Count; i++) if (HasInk(l[i])) return l[i];
            return null;
        }

        private static bool HasInk(SearchLetter l) =>
            l.Right > l.Left && l.Top > l.Bottom && !string.IsNullOrWhiteSpace(l.Value) && !l.Value.All(IsInvisible);

        private static bool IsHyphen(char c) => c == '-' || c == '‐';

        /// <summary>Characters that print nothing and must not break a match: soft hyphen, zero-width space / non-joiner / joiner, word joiner, BOM.</summary>
        internal static bool IsInvisible(char c) =>
            c == '­' || (c >= '​' && c <= '‍') || c == '⁠' || c == '﻿';

        private static string StripInvisible(string s) =>
            s.Any(IsInvisible) ? new string(s.Where(c => !IsInvisible(c)).ToArray()) : s;

        /// <summary>The characters a code point stands for in the haystack: lower-cased, ligatures spelled out, invisibles gone.</summary>
        private static string Expand(char c)
        {
            if (IsInvisible(c)) return "";
            return c switch
            {
                'ﬀ' => "ff",
                'ﬁ' => "fi",
                'ﬂ' => "fl",
                'ﬃ' => "ffi",
                'ﬄ' => "ffl",
                'ﬅ' => "st",   // long s + t
                'ﬆ' => "st",
                _ => char.ToLowerInvariant(c).ToString(),
            };
        }

        /// <summary>Lower-cased, ligatures expanded, invisibles dropped, with any run of whitespace as one space — how words are joined.</summary>
        private static string Normalise(string query)
        {
            var sb = new System.Text.StringBuilder(query.Length);
            bool space = false;
            foreach (char c in query)
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                string e = Expand(c);
                if (e.Length == 0) continue;
                if (space) sb.Append(' ');
                space = false;
                sb.Append(e);
            }
            if (space) sb.Append(' ');
            return sb.ToString();
        }

        private readonly record struct Glyph(
            int Word, double Left, double Bottom, double Right, double Top, bool Horizontal, bool Ink);

        private static bool Horizontal(IReadOnlyList<Letter> letters) =>
            letters.Count == 0 || IsHorizontal(letters[0].StartBaseLine, letters[^1].EndBaseLine);

        private static bool IsHorizontal(PdfPoint start, PdfPoint end)
        {
            double dx = Math.Abs(end.X - start.X), dy = Math.Abs(end.Y - start.Y);
            return dx >= dy;
        }

        /// <summary>The padded boxes of the glyphs in [start, start+length), one per line.</summary>
        private static List<PdfiumInterop.PdfRect> BoxesOf(List<Glyph?> source, int start, int length)
        {
            var lines = new List<List<Glyph>>();
            Glyph? last = null;
            for (int i = start; i < start + length; i++)
            {
                if (source[i] is not Glyph g || !g.Ink) continue;
                if (last is Glyph prev && prev == g) continue;   // a multi-character letter (ligature)

                if (lines.Count == 0 || !SameLine(lines[^1], g)) lines.Add(new List<Glyph>());
                lines[^1].Add(g);
                last = g;
            }

            var boxes = new List<PdfiumInterop.PdfRect>(lines.Count);
            foreach (var line in lines)
            {
                double l = line.Min(g => g.Left), r = line.Max(g => g.Right);
                double b = line.Min(g => g.Bottom), t = line.Max(g => g.Top);
                bool horizontal = line[0].Horizontal;
                double thickness = horizontal ? t - b : r - l;
                double across = thickness * PadFraction;
                double along = Math.Min(across, MaxAlongPad);
                double px = horizontal ? along : across, py = horizontal ? across : along;
                boxes.Add(new PdfiumInterop.PdfRect(Left: l - px, Bottom: b - py, Right: r + px, Top: t + py));
            }
            return boxes;
        }

        /// <summary>
        /// Whether <paramref name="g"/> continues the line being built: same reading direction, and
        /// overlapping it across the line by at least half the thinner of the two. A wrapped match's
        /// next line does not overlap the previous one, so it starts a new box.
        /// </summary>
        private static bool SameLine(List<Glyph> line, Glyph g)
        {
            var first = line[0];
            if (first.Horizontal != g.Horizontal) return false;
            if (g.Word == line[^1].Word) return true;   // inside one word is always one line

            double lo, hi, glo, ghi;
            if (g.Horizontal)
            {
                lo = line.Min(x => x.Bottom); hi = line.Max(x => x.Top); glo = g.Bottom; ghi = g.Top;
            }
            else
            {
                lo = line.Min(x => x.Left); hi = line.Max(x => x.Right); glo = g.Left; ghi = g.Right;
            }
            double overlap = Math.Min(hi, ghi) - Math.Max(lo, glo);
            double thinner = Math.Min(hi - lo, ghi - glo);
            return thinner > 0 && overlap >= 0.5 * thinner;
        }

        // ── Per-document search index ───────────────────────────────────────────────────────

        /// <summary>Everything a search produces: the boxes per page, the pages with hits, the match count.</summary>
        internal sealed record SearchOutcome(
            Dictionary<int, List<(double left, double bottom, double right, double top)>> Rects,
            List<int> Pages,
            int Hits);

        /// <summary>
        /// A document's extracted page text, kept between queries so typing "a", "ab", "abc" opens
        /// and walks the file once, not three times.
        /// </summary>
        /// <remarks>
        /// THREADING. <see cref="Search"/> is meant to run on a worker thread, and two searches
        /// (the superseded one winding down, the new one) can overlap. Each call opens its OWN
        /// PdfPig document and uses it on its own thread only (PdfPig documents are not
        /// thread-safe). The only shared state is the page dictionary, whose values are never
        /// mutated after they are published.
        /// The index belongs to one file at one modification stamp; <see cref="For"/> hands back a
        /// fresh index when the file was saved, replaced or reloaded. Memory is bounded: pages are
        /// cached only until <see cref="MaxCachedLetters"/> letters are held, after which further
        /// pages are re-extracted per query as before.
        /// </remarks>
        internal sealed class Index
        {
            internal const long MaxCachedLetters = 2_000_000;   // ~100-150 MB worst case with boxes

            private readonly string _path;
            private readonly long _length;
            private readonly DateTime _stamp;
            private readonly System.Collections.Concurrent.ConcurrentDictionary<int, List<SearchWord>> _pages = new();
            private long _letters;
            private int _pageCount = -1;

            private Index(string path, long length, DateTime stamp) { _path = path; _length = length; _stamp = stamp; }

            /// <summary>The index for <paramref name="path"/> as it is on disk now: <paramref name="existing"/> if still valid, else a new empty one.</summary>
            internal static Index For(Index? existing, string path)
            {
                var fi = new System.IO.FileInfo(path);
                if (existing is not null && existing._path == path
                    && existing._length == fi.Length && existing._stamp == fi.LastWriteTimeUtc)
                    return existing;
                return new Index(path, fi.Length, fi.LastWriteTimeUtc);
            }

            /// <exception cref="OperationCanceledException">The token fired between pages.</exception>
            internal SearchOutcome Search(string query, CancellationToken ct)
            {
                var rects = new Dictionary<int, List<(double, double, double, double)>>();
                var pages = new List<int>();
                int hits = 0;
                UglyToad.PdfPig.PdfDocument? pig = null;
                try
                {
                    int count = _pageCount;
                    for (int pi = 0; count < 0 || pi < count; pi++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!_pages.TryGetValue(pi, out var words))
                        {
                            pig ??= UglyToad.PdfPig.PdfDocument.Open(_path);
                            if (count < 0) { count = pig.NumberOfPages; _pageCount = count; }
                            if (pi >= count) break;
                            words = ExtractWords(pig.GetPage(pi + 1));
                            long n = words.Sum(w => (long)w.Letters.Count);
                            if (Interlocked.Add(ref _letters, n) > MaxCachedLetters) Interlocked.Add(ref _letters, -n);
                            else _pages[pi] = words;
                        }
                        var matches = FindMatches(words, query);
                        if (matches.Count > 0)
                        {
                            rects[pi] = Flatten(matches);
                            pages.Add(pi);
                            hits += matches.Count;
                        }
                    }
                }
                finally { pig?.Dispose(); }
                return new SearchOutcome(rects, pages, hits);
            }
        }
    }
}
