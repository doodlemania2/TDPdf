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
        internal static List<Match> FindMatches(IReadOnlyList<Word> words, string query)
        {
            var result = new List<Match>();
            string needle = Normalise(query);
            if (needle.Length == 0 || words.Count == 0) return result;

            // The page as one string, and for each character the glyph it came from (null for the
            // space joining two words).
            var text = new System.Text.StringBuilder();
            var source = new List<Glyph?>();
            for (int w = 0; w < words.Count; w++)
            {
                // The nearest-neighbour extractor returns the spaces between words as words of
                // their own; the join below already stands for them.
                if (string.IsNullOrWhiteSpace(words[w].Text)) continue;
                if (text.Length > 0) { text.Append(' '); source.Add(null); }
                AppendWord(words[w], w, text, source);
            }

            string hay = text.ToString();
            int at = 0;
            while (at <= hay.Length - needle.Length)
            {
                int found = hay.IndexOf(needle, at, StringComparison.Ordinal);
                if (found < 0) break;

                var boxes = BoxesOf(source, found, needle.Length);
                if (boxes.Count > 0) result.Add(new Match(boxes));
                at = found + needle.Length;   // non-overlapping: each occurrence once
            }
            return result;
        }

        /// <summary>Lower-cased, with any run of whitespace as one space — how words are joined.</summary>
        private static string Normalise(string query)
        {
            var sb = new System.Text.StringBuilder(query.Length);
            bool space = false;
            foreach (char c in query)
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space) sb.Append(' ');
                space = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            if (space) sb.Append(' ');
            return sb.ToString();
        }

        private readonly record struct Glyph(
            int Word, double Left, double Bottom, double Right, double Top, bool Horizontal, bool Ink);

        private static void AppendWord(Word word, int index, System.Text.StringBuilder text, List<Glyph?> source)
        {
            var letters = word.Letters;
            bool perLetter = letters.Count > 0
                && string.Concat(letters.Select(l => l.Value)) == word.Text;

            if (!perLetter)
            {
                // No usable letter breakdown: every character maps to the whole word, which is
                // the most the page can say about where it is.
                var g = FromBox(index, word.BoundingBox, Horizontal(word.Letters));
                foreach (char c in word.Text) { text.Append(char.ToLowerInvariant(c)); source.Add(g); }
                return;
            }

            foreach (var letter in letters)
            {
                var g = FromBox(index, letter.BoundingBox, IsHorizontal(letter.StartBaseLine, letter.EndBaseLine));
                if (string.IsNullOrWhiteSpace(letter.Value)) g = g with { Ink = false };
                foreach (char c in letter.Value) { text.Append(char.ToLowerInvariant(c)); source.Add(g); }
            }
        }

        private static bool Horizontal(IReadOnlyList<Letter> letters) =>
            letters.Count == 0 || IsHorizontal(letters[0].StartBaseLine, letters[^1].EndBaseLine);

        private static bool IsHorizontal(PdfPoint start, PdfPoint end)
        {
            double dx = Math.Abs(end.X - start.X), dy = Math.Abs(end.Y - start.Y);
            return dx >= dy;
        }

        private static Glyph FromBox(int word, PdfRectangle r, bool horizontal)
        {
            // A box can be rotated with the text; take its axis-aligned extent in the frame.
            double l = Min4(r.TopLeft.X, r.TopRight.X, r.BottomLeft.X, r.BottomRight.X);
            double rr = Max4(r.TopLeft.X, r.TopRight.X, r.BottomLeft.X, r.BottomRight.X);
            double b = Min4(r.TopLeft.Y, r.TopRight.Y, r.BottomLeft.Y, r.BottomRight.Y);
            double t = Max4(r.TopLeft.Y, r.TopRight.Y, r.BottomLeft.Y, r.BottomRight.Y);
            bool ink = rr > l && t > b && double.IsFinite(l) && double.IsFinite(rr)
                       && double.IsFinite(b) && double.IsFinite(t);
            return new Glyph(word, l, b, rr, t, horizontal, ink);
        }

        private static double Min4(double a, double b, double c, double d) => Math.Min(Math.Min(a, b), Math.Min(c, d));
        private static double Max4(double a, double b, double c, double d) => Math.Max(Math.Max(a, b), Math.Max(c, d));

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
    }
}
