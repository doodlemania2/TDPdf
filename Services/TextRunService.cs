using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UglyToad.PdfPig;

namespace TDPdf.Services
{
    /// <summary>
    /// One selectable character on a page, in reading order. Coordinates are PDF space (points,
    /// bottom-left origin) — the same space the search-highlight and region-extraction paths work
    /// in, so a selection quad lands exactly where a search highlight for the same word lands.
    /// </summary>
    internal readonly struct RunChar
    {
        /// <summary>PdfPig letters can be multi-character (ligatures), so this is a string.</summary>
        public readonly string Value;
        public readonly double Left;
        public readonly double Right;
        /// <summary>Ordinal of the word this character belongs to (drives spacing + word counts).</summary>
        public readonly int Word;
        /// <summary>Index into <see cref="PageTextRuns.Lines"/> of the line this character sits on.</summary>
        public readonly int Line;

        public RunChar(string value, double left, double right, int word, int line)
        {
            Value = value; Left = left; Right = right; Word = word; Line = line;
        }
    }

    /// <summary>
    /// A visual line of text: a contiguous slice of the page's flattened character list plus its
    /// vertical band. Caret positions run 0..N over the flattened characters; a line's End caret is
    /// the next line's Start, so a selection ending at End stops cleanly at the line break.
    /// </summary>
    internal sealed class RunLine
    {
        /// <summary>Caret index of the line's first character.</summary>
        public int Start;
        public int Count;
        /// <summary>PDF space, so Top &gt; Bottom.</summary>
        public double Top;
        public double Bottom;
        public double Left;
        public double Right;
        /// <summary>
        /// True when this line reads right-to-left (Hebrew / Arabic / …). Detected per line, so a
        /// page that mixes scripts gets each line's own direction without a document-wide setting.
        /// </summary>
        public bool RightToLeft;
        public int End => Start + Count;
    }

    /// <summary>Reading-order text geometry for one page.</summary>
    internal sealed class PageTextRuns
    {
        public double PdfWidth;
        public double PdfHeight;
        public List<RunChar> Chars = new();
        public List<RunLine> Lines = new();
    }

    /// <summary>
    /// Builds and caches per-page reading-order character runs, the geometry behind flowing text
    /// selection and the line-hugging markup tools (upstream KillerPDF v1.6.5, #127).
    ///
    /// Word geometry comes from PdfPig's <c>GetWords()</c> — the same source the search and the
    /// rectangle region extractor already use — so nothing can drift between the three. Words are
    /// grouped into lines by vertical overlap and ordered top-to-bottom, then in each line's own
    /// detected reading direction; each word's <c>Letters</c> supply the per-character boxes the
    /// caret model needs.
    ///
    /// Performance: <c>GetWords()</c> means opening and parsing the page with PdfPig, which is far
    /// too slow to do on every mouse-move. Everything is therefore cached per page, keyed by the
    /// working file's path AND its last-write timestamp — see <see cref="GetPage"/>.
    ///
    /// Multi-column pages are handled: the raw vertical bands are re-ordered column-aware before the
    /// characters are flattened, so a drag down the left column of a two-column page no longer sweeps
    /// its right-hand neighbour and the copied text comes out column by column (#185).
    ///
    /// Known limitations, shared with the search highlights this deliberately mirrors:
    /// • In-memory page rotation is not applied to the boxes.
    /// </summary>
    internal sealed class TextRunService
    {
        // Keyed by (working path, last-write ticks, page). TDPdf repoints the working path at a
        // fresh %TEMP% copy after every structural edit (rotate / delete / reorder / crop /
        // transform / OCR / repair — see SaveTempAndReload) and on document reload, so the path
        // alone already invalidates those; the timestamp additionally covers an in-place save that
        // rewrites the same path. A null result is cached too: a file PdfPig cannot open must not
        // be re-parsed on every click.
        private readonly Dictionary<(string Path, long Ticks, int Page), PageTextRuns?> _cache = new();

        /// <summary>Drops everything. Called from the same places that clear the render cache.</summary>
        public void Clear() => _cache.Clear();

        public PageTextRuns? GetPage(string? path, int pageIdx)
        {
            if (string.IsNullOrEmpty(path) || pageIdx < 0) return null;
            long ticks;
            try { ticks = File.GetLastWriteTimeUtc(path).Ticks; }
            catch { return null; }

            var key = (path, ticks, pageIdx);
            if (_cache.TryGetValue(key, out var hit)) return hit;
            // Entries are small (a few hundred chars per page) but unbounded is unbounded: on a
            // 5,000-page document a full scroll-through would otherwise hold every page's geometry.
            if (_cache.Count > 512) _cache.Clear();

            PageTextRuns? runs = null;
            try
            {
                using var doc = PdfDocument.Open(path);
                if (pageIdx < doc.NumberOfPages)
                    runs = Build(doc.GetPage(pageIdx + 1));   // PdfPig is 1-based
            }
            catch
            {
                // Encrypted / malformed: flowing selection simply isn't offered on this page and
                // the caller falls back to the classic rectangle marquee.
            }

            _cache[key] = runs;
            return runs;
        }

        /// <summary>
        /// Splits one vertical band's words into horizontal segments at column-gutter-sized gaps
        /// (#185). The threshold is derived from the band's own average character width — roughly
        /// three characters — with a 10pt floor, which sits well past a word space (~0.25em) but
        /// below any real column gutter, so ordinary prose comes back as a single segment.
        /// </summary>
        /// <param name="words">The band's words. Sorted left-to-right in place by this method.</param>
        internal static List<List<UglyToad.PdfPig.Content.Word>> SplitAtColumnGaps(
            List<UglyToad.PdfPig.Content.Word> words)
        {
            var segments = new List<List<UglyToad.PdfPig.Content.Word>>();
            if (words.Count == 0) return segments;

            words.Sort((a, b) => LeftOf(a.BoundingBox).CompareTo(LeftOf(b.BoundingBox)));

            double totalWidth = 0;
            int totalChars = 0;
            foreach (var w in words)
            {
                totalWidth += RightOf(w.BoundingBox) - LeftOf(w.BoundingBox);
                totalChars += Math.Max(1, w.Text.Length);
            }
            double gapThreshold = Math.Max(10, (totalChars > 0 ? totalWidth / totalChars : 5) * 3);

            segments.Add([words[0]]);
            for (int i = 1; i < words.Count; i++)
            {
                if (LeftOf(words[i].BoundingBox) - RightOf(words[i - 1].BoundingBox) > gapThreshold)
                    segments.Add([]);
                segments[^1].Add(words[i]);
            }
            return segments;
        }

        /// <summary>
        /// Re-orders the vertical bands into column reading order (#185); see the call site comment
        /// in <see cref="Build"/> for what the ordering is for. Bands arrive top-to-bottom and the
        /// result is the same tuple shape, reordered so the flattened character list reads one
        /// column at a time.
        /// </summary>
        private static List<(List<UglyToad.PdfPig.Content.Word> Words, double Top, double Bottom)>
            OrderColumnAware(List<(List<UglyToad.PdfPig.Content.Word> Words, double Top, double Bottom)> bands)
        {
            if (bands.Count < 2) return bands;

            // The horizontal extent of everything on the page, which is what "spans most of the
            // width" is measured against — a page-relative threshold would misfire on a document
            // with generous margins.
            double textL = double.MaxValue, textR = double.MinValue;
            foreach (var (ws, _, _) in bands)
                foreach (var w in ws)
                {
                    if (LeftOf(w.BoundingBox) < textL) textL = LeftOf(w.BoundingBox);
                    if (RightOf(w.BoundingBox) > textR) textR = RightOf(w.BoundingBox);
                }
            double wideW = (textR - textL) * 0.62;   // spans most of the text width = not a column line

            var reordered = new List<(List<UglyToad.PdfPig.Content.Word>, double, double)>();
            // Segments waiting to be grouped into columns: everything since the last wide segment.
            var pending = new List<(List<UglyToad.PdfPig.Content.Word> Words, double Top, double Bottom, double L, double R, int Band)>();
            // How many bands in the open group actually held two or more side-by-side segments —
            // the only direct evidence that a gutter exists. See the guard inside Flush.
            int gutterBands = 0;

            // The "this group is not columns after all" exit: hand the pending segments back as the
            // bands they were cut from, so a rejected group is byte-for-byte what pre-#185 built.
            // Segments of one band are always consecutive in pending, and merging them left-to-right
            // rebuilds the band's word order and its Top/Bottom exactly.
            void EmitAsBands()
            {
                int i = 0;
                while (i < pending.Count)
                {
                    int band = pending[i].Band;
                    var merged = pending[i].Words;   // a fresh list from SplitAtColumnGaps, safe to grow
                    double top = pending[i].Top, bottom = pending[i].Bottom;
                    int j = i + 1;
                    while (j < pending.Count && pending[j].Band == band)
                    {
                        merged.AddRange(pending[j].Words);
                        if (pending[j].Top > top) top = pending[j].Top;
                        if (pending[j].Bottom < bottom) bottom = pending[j].Bottom;
                        j++;
                    }
                    reordered.Add((merged, top, bottom));
                    i = j;
                }
                pending.Clear();
                gutterBands = 0;
            }

            void Flush()
            {
                if (pending.Count == 0) return;

                // DELIBERATE DIVERGENCE from upstream, which clusters unconditionally. Clustering
                // narrow segments that came from bands with no gutter in them reorders perfectly
                // ordinary single-column pages: a letter whose head is a right-aligned date, a
                // centred title and a short salutation has three narrow lines that overlap each
                // other in X not at all, so they cluster as three "columns" and come back out
                // left-to-right as salutation / title / date. Nothing on such a page is a column,
                // and the words themselves say so — no band was ever split. Require two bands with
                // a real gutter before believing in columns; one stray split (a form row, a header
                // with a right-aligned date) is not a layout. Below that bar the group goes back out
                // as the bands it came in as, i.e. exactly the pre-#185 result.
                if (gutterBands < 2) { EmitAsBands(); return; }

                // Cluster the pending segments into columns by X-interval overlap (at least half the
                // narrower range). Walking left-to-right means a cluster only ever grows rightward,
                // so a slightly ragged column still collects into one entry.
                var cols = new List<(double L, double R, List<int> Idx)>();
                var byLeft = Enumerable.Range(0, pending.Count).OrderBy(i => pending[i].L).ToList();
                foreach (int i in byLeft)
                {
                    var seg = pending[i];
                    int hit = -1;
                    for (int c = 0; c < cols.Count && hit < 0; c++)
                    {
                        double ov = Math.Min(cols[c].R, seg.R) - Math.Max(cols[c].L, seg.L);
                        double minW = Math.Min(cols[c].R - cols[c].L, seg.R - seg.L);
                        if (minW > 0 && ov >= minW * 0.5) hit = c;
                    }
                    if (hit < 0) cols.Add((seg.L, seg.R, [i]));
                    else
                    {
                        var c0 = cols[hit];
                        c0.Idx.Add(i);
                        cols[hit] = (Math.Min(c0.L, seg.L), Math.Max(c0.R, seg.R), c0.Idx);
                    }
                }
                // Second guard, also a divergence from upstream: a table row splits at its cell
                // gaps exactly like a column gutter, and reading an invoice column-major ("every
                // name, then every quantity, then every price") is far worse than the row-major
                // order this started with. Real text columns are WIDE — half the text width in a
                // two-column layout, a third in a three-column one — while grid cells are not. If
                // the widest cluster does not reach a quarter of the text width, this is a grid,
                // so leave the rows alone. Being wrong here only ever falls back to the pre-#185
                // order, never to something new.
                double widest = 0;
                foreach (var c in cols) if (c.R - c.L > widest) widest = c.R - c.L;
                if (widest < (textR - textL) * 0.25) { EmitAsBands(); return; }

                // Whole columns left-to-right, top-to-bottom inside each. OrderByDescending is a
                // stable sort, so segments that share a Top keep the order the band sort gave them —
                // which is what makes a single-column page come back byte-for-byte unchanged.
                foreach (var col in cols.OrderBy(c => c.L))
                    foreach (int i in col.Idx.OrderByDescending(i => pending[i].Top))
                        reordered.Add((pending[i].Words, pending[i].Top, pending[i].Bottom));
                pending.Clear();
                gutterBands = 0;
            }

            for (int bi = 0; bi < bands.Count; bi++)
            {
                var segments = SplitAtColumnGaps(bands[bi].Words);
                foreach (var sws in segments)
                {
                    double sT = double.MinValue, sB = double.MaxValue, sL = double.MaxValue, sR = double.MinValue;
                    foreach (var w in sws)
                    {
                        var bb = w.BoundingBox;
                        if (bb.Top > sT) sT = bb.Top;
                        if (bb.Bottom < sB) sB = bb.Bottom;
                        if (LeftOf(bb) < sL) sL = LeftOf(bb);
                        if (RightOf(bb) > sR) sR = RightOf(bb);
                    }
                    // A wide segment is a title, a full-measure line of body text or a footer: it closes the
                    // open column section and emits in place, so the sections above and below a
                    // two-column block never get interleaved with it.
                    if (sR - sL >= wideW) { Flush(); reordered.Add((sws, sT, sB)); }
                    else pending.Add((sws, sT, sB, sL, sR, bi));
                }
                // Credited after the band's segments, so a band whose wide segment closed the
                // previous group counts toward the group its narrow remainder landed in.
                if (segments.Count > 1) gutterBands++;
            }
            Flush();
            return reordered;
        }

        private static PageTextRuns Build(UglyToad.PdfPig.Content.Page page)
        {
            var result = new PageTextRuns { PdfWidth = page.Width, PdfHeight = page.Height };
            var words = page.GetWords().ToList();
            if (words.Count == 0) return result;

            // Group words into lines: a word joins a line when its vertical band overlaps the
            // line's band by at least half the smaller of the two heights. Bands grow as members
            // join, so a line assembled left-to-right tolerates mixed font sizes.
            var lineWords = new List<(List<UglyToad.PdfPig.Content.Word> Words, double Top, double Bottom)>();
            foreach (var w in words)
            {
                var bb = w.BoundingBox;
                double wTop = bb.Top, wBottom = bb.Bottom;
                int found = -1;
                for (int i = 0; i < lineWords.Count; i++)
                {
                    var (_, lTop, lBottom) = lineWords[i];
                    double overlap = Math.Min(lTop, wTop) - Math.Max(lBottom, wBottom);
                    double minH = Math.Min(lTop - lBottom, wTop - wBottom);
                    if (minH > 0 && overlap >= minH * 0.5) { found = i; break; }
                }
                if (found < 0)
                {
                    lineWords.Add((new List<UglyToad.PdfPig.Content.Word> { w }, wTop, wBottom));
                }
                else
                {
                    var entry = lineWords[found];
                    entry.Words.Add(w);
                    lineWords[found] = (entry.Words, Math.Max(entry.Top, wTop), Math.Min(entry.Bottom, wBottom));
                }
            }

            // Reading order: lines top-to-bottom (PDF Y grows upward, so larger Top first). Each
            // line then picks its own horizontal direction, so a page mixing Latin and Hebrew /
            // Arabic paragraphs gets both right without a document-wide setting (#170).
            lineWords.Sort((a, b) => b.Top.CompareTo(a.Top));

            // ---- #185: column-aware reading order ----------------------------------------------
            // A Y band spans the whole page, so on a two-column layout every "line" mixed both
            // columns: a drag down the left column swept its right-hand neighbour, and the copied
            // text came out row-interleaved instead of column by column. Split each band into
            // segments at column-gutter-sized gaps, cluster the narrow segments into columns by X
            // overlap, and emit whole columns left-to-right (top-to-bottom inside each). Wide
            // segments — titles and footers spanning the text width — close the open column
            // section, so a title / two columns / footer page keeps a sane order.
            //
            // Nothing that is not demonstrably a column layout is touched, because a wrong reorder
            // on the single-column pages that are most of what anyone opens would be far worse than
            // the bug: prose bands never split (no gutter-sized gaps), and the two guards inside
            // OrderColumnAware hand a group back unchanged unless at least two of its bands really
            // did hold side-by-side segments AND the resulting columns are column-wide rather than
            // table-cell-wide. Verified against a plain page, a ragged letter head, a
            // title/two-column/footer paper, a three-column layout and an invoice table.
            lineWords = OrderColumnAware(lineWords);

            int wordOrdinal = 0;
            for (int li = 0; li < lineWords.Count; li++)
            {
                var (ws, top, bottom) = lineWords[li];
                bool rtl = IsRightToLeftText(ws.Select(w => w.Text));
                ws.Sort(rtl
                    ? (a, b) => RightOf(b.BoundingBox).CompareTo(RightOf(a.BoundingBox))
                    : (a, b) => LeftOf(a.BoundingBox).CompareTo(LeftOf(b.BoundingBox)));

                // The line index stored on each character is the index this line will occupy in
                // result.Lines, which is only the same as li while no line has been skipped.
                int lineIndex = result.Lines.Count;
                var line = new RunLine
                {
                    Start = result.Chars.Count,
                    Top = top,
                    Bottom = bottom,
                    RightToLeft = rtl,
                };
                foreach (var w in ws)
                {
                    // Letters arrive in the content stream's order, which for RTL runs is not the
                    // visual order, so they are re-sorted into the line's reading direction too.
                    var letters = w.Letters.ToList();
                    letters.Sort(rtl
                        ? (a, b) => RightOf(b.BoundingBox).CompareTo(RightOf(a.BoundingBox))
                        : (a, b) => LeftOf(a.BoundingBox).CompareTo(LeftOf(b.BoundingBox)));
                    foreach (var letter in letters)
                    {
                        var g = letter.BoundingBox;
                        double l = Math.Min(g.Left, g.Right);
                        double r = Math.Max(g.Left, g.Right);
                        result.Chars.Add(new RunChar(letter.Value, l, r, wordOrdinal, lineIndex));
                    }
                    wordOrdinal++;
                }
                line.Count = result.Chars.Count - line.Start;
                if (line.Count == 0) continue;
                // Physical extremes, not "first char / last char": on an RTL line the first
                // character in reading order is the rightmost one.
                double minLeft = double.MaxValue, maxRight = double.MinValue;
                for (int ci = line.Start; ci < line.End; ci++)
                {
                    if (result.Chars[ci].Left < minLeft) minLeft = result.Chars[ci].Left;
                    if (result.Chars[ci].Right > maxRight) maxRight = result.Chars[ci].Right;
                }
                line.Left = minLeft;
                line.Right = maxRight;
                result.Lines.Add(line);
            }
            return result;
        }

        private static double LeftOf(UglyToad.PdfPig.Core.PdfRectangle r) => Math.Min(r.Left, r.Right);
        private static double RightOf(UglyToad.PdfPig.Core.PdfRectangle r) => Math.Max(r.Left, r.Right);

        /// <summary>
        /// Majority vote over a line's characters: RTL when right-to-left letters outnumber the
        /// left-to-right ones. The ranges are Hebrew/Arabic/Syriac/Thaana/NKo/Samaritan and friends
        /// (U+0590–U+08FF) plus the Arabic presentation-form blocks many PDFs actually encode.
        /// Digits and punctuation are neutral and deliberately do not vote either way.
        /// </summary>
        internal static bool IsRightToLeftText(IEnumerable<string> values)
        {
            int rtl = 0, ltr = 0;
            foreach (string value in values)
            {
                if (string.IsNullOrEmpty(value)) continue;
                foreach (char c in value)
                {
                    if ((c >= '\u0590' && c <= '\u08FF') ||
                        (c >= '\uFB1D' && c <= '\uFDFF') ||
                        (c >= '\uFE70' && c <= '\uFEFF')) rtl++;
                    else if (char.IsLetter(c)) ltr++;
                }
            }
            return rtl > ltr;
        }

        /// <summary>
        /// True when the point sits ON text: inside a line's vertical band and within its
        /// horizontal extent (small slop). This is the gate that decides flowing selection versus
        /// the classic marquee — empty page areas must keep the marquee.
        /// </summary>
        public static bool IsOverText(PageTextRuns runs, double x, double y)
        {
            const double slop = 2.0;   // PDF points
            foreach (var line in runs.Lines)
            {
                if (y <= line.Top + slop && y >= line.Bottom - slop &&
                    x >= line.Left - slop && x <= line.Right + slop)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Caret position (0..Chars.Count) nearest a point, browser-style: above the first line
        /// selects from the page start, below the last line to the page end, between lines snaps to
        /// the closer line, and beyond a line's ends clamps to those ends.
        /// </summary>
        public static int CaretFromPoint(PageTextRuns runs, double x, double y)
        {
            if (runs.Lines.Count == 0) return 0;

            // Since #185 several lines can share a Y band — that is exactly what a two-column page
            // is — so a vertical hit is no longer enough to identify the line: the first match in
            // reading order is always the LEFT column's, and a click in the right column would
            // otherwise land at the end of the left column's line. Among the lines that contain y,
            // take the one nearest in x (0 when the point is inside its horizontal extent, which
            // short-circuits). On a single-column page at most one line contains y, so this picks
            // the same line the plain break did.
            RunLine? onLine = null;
            double bestDx = double.MaxValue;
            RunLine? nearLine = null;
            double bestDy = double.MaxValue;
            foreach (var line in runs.Lines)
            {
                if (y <= line.Top && y >= line.Bottom)
                {
                    double dx = x < line.Left ? line.Left - x : (x > line.Right ? x - line.Right : 0);
                    if (dx < bestDx) { bestDx = dx; onLine = line; }
                    if (dx <= 0) break;
                }
                else if (onLine is null)
                {
                    // Vertical near-misses only matter while nothing has contained y yet, which
                    // keeps the "snap to the closer line" behaviour for clicks between lines.
                    double d = y > line.Top ? y - line.Top : line.Bottom - y;
                    if (d < bestDy) { bestDy = d; nearLine = line; }
                }
            }
            RunLine? target = onLine ?? nearLine;
            if (target is null) return 0;

            // Entirely above the first line -> page start; entirely below the last -> page end.
            var first = runs.Lines[0];
            var last = runs.Lines[runs.Lines.Count - 1];
            if (y > first.Top && ReferenceEquals(target, first) && x < first.Left) return 0;
            if (y < last.Bottom && ReferenceEquals(target, last) && x > last.Right) return runs.Chars.Count;

            // On an RTL line the caret grows leftward, so every comparison mirrors (#170).
            if (target.RightToLeft)
            {
                if (x >= target.Right) return target.Start;
                if (x <= target.Left) return target.End;
                for (int i = target.Start; i < target.End; i++)
                {
                    var c = runs.Chars[i];
                    double mid = (c.Left + c.Right) / 2;
                    if (x > mid) return i;
                }
                return target.End;
            }

            if (x <= target.Left) return target.Start;
            if (x >= target.Right) return target.End;
            for (int i = target.Start; i < target.End; i++)
            {
                var c = runs.Chars[i];
                double mid = (c.Left + c.Right) / 2;
                if (x < mid) return i;
            }
            return target.End;
        }

        /// <summary>
        /// Text for the caret range [start, end): spaces between words, newlines between lines —
        /// i.e. reading order across lines and paragraphs. Also reports how many distinct words the
        /// range touches, for the status line.
        /// </summary>
        public static string TextForRange(PageTextRuns runs, int start, int end, out int wordCount)
        {
            wordCount = 0;
            var sb = new StringBuilder();
            int lastWord = -1, lastLine = -1;
            for (int i = Math.Max(0, start); i < Math.Min(end, runs.Chars.Count); i++)
            {
                var c = runs.Chars[i];
                if (lastLine >= 0 && c.Line != lastLine) sb.Append('\n');
                else if (lastWord >= 0 && c.Word != lastWord) sb.Append(' ');
                if (c.Word != lastWord) wordCount++;
                sb.Append(c.Value);
                lastWord = c.Word;
                lastLine = c.Line;
            }
            return sb.ToString();
        }
    }
}
