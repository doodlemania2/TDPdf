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
    /// grouped into lines by vertical overlap and ordered top-to-bottom, left-to-right; each word's
    /// <c>Letters</c> supply the per-character boxes the caret model needs.
    ///
    /// Performance: <c>GetWords()</c> means opening and parsing the page with PdfPig, which is far
    /// too slow to do on every mouse-move. Everything is therefore cached per page, keyed by the
    /// working file's path AND its last-write timestamp — see <see cref="GetPage"/>.
    ///
    /// Known limitations, shared with the search highlights this deliberately mirrors:
    /// • In-memory page rotation is not applied to the boxes.
    /// • Line grouping is by vertical band, so side-by-side columns merge into one line.
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

            // Reading order: lines top-to-bottom (PDF Y grows upward, so larger Top first), words
            // left-to-right inside each line.
            lineWords.Sort((a, b) => b.Top.CompareTo(a.Top));

            int wordOrdinal = 0;
            for (int li = 0; li < lineWords.Count; li++)
            {
                var (ws, top, bottom) = lineWords[li];
                ws.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));

                // The line index stored on each character is the index this line will occupy in
                // result.Lines, which is only the same as li while no line has been skipped.
                int lineIndex = result.Lines.Count;
                var line = new RunLine { Start = result.Chars.Count, Top = top, Bottom = bottom };
                foreach (var w in ws)
                {
                    foreach (var letter in w.Letters)
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
                line.Left = result.Chars[line.Start].Left;
                line.Right = result.Chars[line.End - 1].Right;
                result.Lines.Add(line);
            }
            return result;
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

            RunLine? target = null;
            double best = double.MaxValue;
            foreach (var line in runs.Lines)
            {
                if (y <= line.Top && y >= line.Bottom) { target = line; break; }
                double d = y > line.Top ? y - line.Top : line.Bottom - y;
                if (d < best) { best = d; target = line; }
            }
            if (target is null) return 0;

            // Entirely above the first line -> page start; entirely below the last -> page end.
            var first = runs.Lines[0];
            var last = runs.Lines[runs.Lines.Count - 1];
            if (y > first.Top && ReferenceEquals(target, first) && x < first.Left) return 0;
            if (y < last.Bottom && ReferenceEquals(target, last) && x > last.Right) return runs.Chars.Count;

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
