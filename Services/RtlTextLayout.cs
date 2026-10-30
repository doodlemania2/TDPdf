using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TDPdf.Services
{
    /// <summary>
    /// Visual-order layout for a line of Hebrew burned into a saved PDF. Upstream KillerPDF #435.
    /// </summary>
    /// <remarks>
    /// <b>Why this exists.</b> On screen, WPF runs the Unicode bidi algorithm, so a Hebrew line
    /// typed into a text annotation reads right-to-left. PdfSharpCore's <c>DrawString</c> does no
    /// bidi at all: it emits the code points in LOGICAL order, left to right, and a PDF viewer shows
    /// them exactly as drawn — so the saved word came out backwards. The fix is to do the bidi
    /// ourselves and draw the line's text elements one at a time at their visual positions.
    ///
    /// <b>Which bidi.</b> A practical subset of UAX #9 for a paragraph with no explicit embeddings
    /// or isolates: strong L and R (Hebrew), European numbers with their separators and terminators
    /// (W4–W7, so "1,234.5" and "50%" stay one left-to-right number), neutrals (N1/N2), implicit
    /// levels 0–2 (I1/I2), trailing whitespace reset (L1), reordering (L2) and mirrored brackets in
    /// right-to-left runs (L4). No N0 bracket pairing: WPF's text formatter predates it, and the
    /// point is to match what the text box shows, not the newest standard. Levels are resolved over
    /// the whole hard paragraph and only the reordering is per line, as WPF does for wrapped text.
    ///
    /// <b>Hebrew only.</b> <see cref="IsHebrewRightToLeftLine"/> admits a line that has Hebrew in it
    /// and no Arabic (or other right-to-left script): Arabic needs contextual shaping as well as
    /// reordering, and reordered but unshaped Arabic is still wrong, so it keeps the path it has
    /// always had. A line with no Hebrew is never reordered either.
    ///
    /// <b>The extent does not move.</b> The annotation's text box is left-to-right, so a line that
    /// contains right-to-left text still occupies the same horizontal span a left-to-right line of
    /// the same width would. <see cref="Layout(string, double, Func{string, double})"/> therefore
    /// places elements inside exactly the span <see cref="TextLetterSpacing.Layout"/> would —
    /// leftmost ink at offset 0, rightmost advance ending at <see cref="TextLetterSpacing.Width"/> —
    /// only in visual order. The box, the fill, the underline and the selection stay where they were.
    ///
    /// Nothing here references WPF or PdfSharpCore, for the same reason as
    /// <see cref="TextLetterSpacing"/>: the caller passes its own measure, and tests/PdfCore can
    /// reach it.
    /// </remarks>
    internal static class RtlTextLayout
    {
        // Bidi classes this subset distinguishes. S is a segment separator (tab, and a stray CR).
        private enum Kind { L, R, EN, ES, ET, CS, NSM, WS, S, ON }

        /// <summary>
        /// True when <paramref name="line"/> contains Hebrew and no Arabic or other right-to-left
        /// script, so it is drawn in visual order through <see cref="Layout(string, double, Func{string, double})"/>.
        /// </summary>
        /// <remarks>
        /// Hebrew is the Hebrew block (U+0590–U+05FF, which carries the niqqud and cantillation
        /// marks too) and the Alphabetic Presentation Forms Hebrew range (U+FB1D–U+FB4F). Latin,
        /// digits, spaces and punctuation alongside it are reordered by the bidi rules (#435).
        /// Callers pass one line: a line break inside it would be reordered as if it were not there.
        /// </remarks>
        internal static bool IsHebrewRightToLeftLine(string? line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            bool hasHebrew = false;
            foreach (Rune rune in line.EnumerateRunes())
            {
                if (IsHebrew(rune.Value)) hasHebrew = true;
                else if (IsOtherRightToLeft(rune.Value)) return false;
            }
            return hasHebrew;
        }

        /// <summary>
        /// Where each text element of <paramref name="text"/> sits when drawn in visual order, as
        /// an offset from the line's LEFT edge. Entries are in logical order. The text is one whole
        /// paragraph in a left-to-right text box.
        /// </summary>
        /// <param name="text">One line, in logical (typed) order.</param>
        /// <param name="spacing">
        /// Letter spacing between elements, in the caller's units. None (<see
        /// cref="TextLetterSpacing.IsNone"/>) is treated as zero.
        /// </param>
        /// <param name="measureCluster">The advance of one text element, as in <see cref="TextLetterSpacing.Layout"/>.</param>
        /// <remarks>
        /// Text elements, not chars, via <see cref="TextLetterSpacing.Clusters"/>: a niqqud point is
        /// a combining mark and has to be drawn with the letter it sits on.
        ///
        /// A space or punctuation mark between two Hebrew letters takes their direction, but one
        /// before the first or after the last sits against the left-to-right paragraph edge and
        /// takes the paragraph's — so "שלום." shows its period on the right, where it was typed, and
        /// a trailing space stays trailing.
        ///
        /// The <c>Cluster</c> of an element in a right-to-left run is its mirrored glyph when it has
        /// one, so a typed "(" draws as ")" exactly as the screen shows it.
        ///
        /// Elements are placed left to right in visual order, with the gap between neighbours and
        /// never after the last, as in <see cref="TextLetterSpacing.Layout"/> — so the line spans
        /// exactly the width its left-to-right draw would.
        /// </remarks>
        internal static List<(string Cluster, double X)> Layout(
            string text, double spacing, Func<string, double> measureCluster)
            => Layout(text, 0, text.Length, spacing, measureCluster);

        /// <summary>
        /// As <see cref="Layout(string, double, Func{string, double})"/>, for the wrapped line that
        /// starts at <paramref name="start"/> in the hard <paramref name="paragraph"/>: directions
        /// are resolved over the whole paragraph, as the text box does, and only reordered per line.
        /// </summary>
        /// <param name="rightToLeftParagraph">
        /// The paragraph's base direction. TDPdf's text boxes are left-to-right, which is the default.
        /// </param>
        internal static List<(string Cluster, double X)> Layout(
            string paragraph, int start, int length, double spacing, Func<string, double> measureCluster,
            bool rightToLeftParagraph = false)
        {
            if (TextLetterSpacing.IsNone(spacing)) spacing = 0;
            var (clusters, glyphs, visual) = Reorder(paragraph, start, length, rightToLeftParagraph);
            var placed = new List<(string, double)>(clusters.Count);
            if (clusters.Count == 0) return placed;

            var xs = new double[clusters.Count];
            double x = 0;
            foreach (int i in visual)
            {
                xs[i] = x;
                x += measureCluster(glyphs[i]) + spacing;
            }
            for (int i = 0; i < clusters.Count; i++) placed.Add((glyphs[i], xs[i]));
            return placed;
        }

        /// <summary>The line as it reads on screen, left to right, mirrored glyphs included.</summary>
        internal static string VisualText(string line, bool rightToLeftParagraph = false)
            => VisualText(line, 0, line.Length, rightToLeftParagraph);

        /// <summary>The wrapped line of <paramref name="paragraph"/> as it reads on screen, left to right.</summary>
        internal static string VisualText(string paragraph, int start, int length, bool rightToLeftParagraph = false)
        {
            var (_, glyphs, visual) = Reorder(paragraph, start, length, rightToLeftParagraph);
            var sb = new StringBuilder();
            foreach (int i in visual) sb.Append(glyphs[i]);
            return sb.ToString();
        }

        /// <summary>
        /// For each line <see cref="TextLetterSpacing.Wrap"/> produced from <paramref name="text"/>,
        /// the hard paragraph it came from and where it starts in it. Null if the lines do not map
        /// back onto the text, in which case each line is its own paragraph.
        /// </summary>
        /// <remarks>
        /// Wrap emits every paragraph's lines in order as consecutive substrings, dropping only the
        /// space it breaks at, and the last line of a paragraph always runs to its end — so the
        /// lines can be found again by scanning forward.
        /// </remarks>
        internal static List<(string Paragraph, int Start)>? LocateWrappedLines(string text, IReadOnlyList<string> lines)
        {
            var located = new List<(string, int)>(lines.Count);
            int li = 0;
            foreach (var para in text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
            {
                int pos = 0;
                while (true)
                {
                    if (li >= lines.Count) return null;
                    string line = lines[li++];
                    int at = para.IndexOf(line, pos, StringComparison.Ordinal);
                    if (at < 0) return null;
                    located.Add((para, at));
                    pos = at + line.Length;
                    if (pos == para.Length) break;
                }
            }
            return li == lines.Count ? located : null;
        }

        // ── the algorithm ─────────────────────────────────────────────────────────────────────

        // The line's clusters, the glyph each draws (mirrored on an odd level), and the logical
        // indices of the clusters in visual (left-to-right) order.
        private static (List<string> Clusters, string[] Glyphs, List<int> Visual) Reorder(
            string paragraph, int start, int length, bool rtl)
        {
            start = Math.Clamp(start, 0, paragraph.Length);
            length = Math.Clamp(length, 0, paragraph.Length - start);
            string line = paragraph.Substring(start, length);
            var clusters = TextLetterSpacing.Clusters(line);
            int n = clusters.Count;
            var glyphs = new string[n];
            var visual = new List<int>(n);
            if (n == 0) return (clusters, glyphs, visual);

            int baseLevel = rtl ? 1 : 0;
            var (charLevels, charKinds) = ResolveLevels(paragraph, baseLevel);

            var levels = new int[n];
            var kinds = new Kind[n];
            int offset = start;
            for (int i = 0; i < n; i++)
            {
                levels[i] = charLevels[offset];
                kinds[i] = charKinds[offset];
                offset += clusters[i].Length;
            }

            // L1: segment separators, and whitespace before one or at the line end, return to the
            // paragraph level — so a trailing space stays trailing.
            bool reset = true;
            for (int i = n - 1; i >= 0; i--)
            {
                if (kinds[i] == Kind.S) { levels[i] = baseLevel; reset = true; }
                else if (reset && kinds[i] == Kind.WS) levels[i] = baseLevel;
                else reset = false;
            }

            // L2: from the highest level down to the lowest odd one, reverse every run at or above it.
            for (int i = 0; i < n; i++) visual.Add(i);
            int max = 0, minOdd = int.MaxValue;
            foreach (int lv in levels)
            {
                max = Math.Max(max, lv);
                if ((lv & 1) == 1) minOdd = Math.Min(minOdd, lv);
            }
            for (int level = max; level >= minOdd && level >= 1; level--)
            {
                for (int i = 0; i < n; i++)
                {
                    if (levels[visual[i]] < level) continue;
                    int end = i;
                    while (end < n && levels[visual[end]] >= level) end++;
                    visual.Reverse(i, end - i);
                    i = end - 1;
                }
            }

            // L4: a mirrored character on an odd level draws as its pair.
            for (int i = 0; i < n; i++)
                glyphs[i] = (levels[i] & 1) == 1 ? Mirror(clusters[i]) : clusters[i];
            return (clusters, glyphs, visual);
        }

        // Embedding level and original class per UTF-16 index of a paragraph (rules W1–I2).
        private static (int[] Levels, Kind[] Kinds) ResolveLevels(string paragraph, int baseLevel)
        {
            var clusters = TextLetterSpacing.Clusters(paragraph);
            int n = clusters.Count;
            var original = new Kind[n];
            for (int i = 0; i < n; i++) original[i] = Classify(clusters[i]);
            var k = (Kind[])original.Clone();
            Kind sos = baseLevel == 1 ? Kind.R : Kind.L;

            // W1: a mark with no base of its own takes the class of what precedes it.
            for (int i = 0; i < n; i++)
                if (k[i] == Kind.NSM) k[i] = i == 0 ? sos : k[i - 1];
            // W4: one separator between two European numbers joins them ("1,234.5").
            for (int i = 1; i + 1 < n; i++)
                if (k[i] is Kind.ES or Kind.CS && k[i - 1] == Kind.EN && k[i + 1] == Kind.EN)
                    k[i] = Kind.EN;
            // W5: terminators touching a European number join it ("50%", "$5").
            for (int i = 0; i < n; i++)
            {
                if (k[i] != Kind.ET) continue;
                int end = i;
                while (end < n && k[end] == Kind.ET) end++;
                if ((i > 0 && k[i - 1] == Kind.EN) || (end < n && k[end] == Kind.EN))
                    for (int j = i; j < end; j++) k[j] = Kind.EN;
                i = end - 1;
            }
            // W6: separators and terminators left over are neutral.
            for (int i = 0; i < n; i++)
                if (k[i] is Kind.ES or Kind.ET or Kind.CS) k[i] = Kind.ON;
            // W7: a European number whose nearest strong predecessor is L is L.
            Kind strong = sos;
            for (int i = 0; i < n; i++)
            {
                if (k[i] is Kind.L or Kind.R) strong = k[i];
                else if (k[i] == Kind.EN && strong == Kind.L) k[i] = Kind.L;
            }
            // N1/N2: a run of neutrals between two sides of one direction takes it (numbers count
            // as R); otherwise it takes the paragraph's direction.
            for (int i = 0; i < n; i++)
            {
                if (k[i] is not (Kind.WS or Kind.S or Kind.ON)) continue;
                int end = i;
                while (end < n && k[end] is Kind.WS or Kind.S or Kind.ON) end++;
                Kind before = i == 0 ? sos : Direction(k[i - 1]);
                Kind after = end == n ? sos : Direction(k[end]);
                Kind resolved = before == after ? before : sos;
                for (int j = i; j < end; j++) k[j] = resolved;
                i = end - 1;
            }

            // I1/I2.
            var levels = new int[paragraph.Length];
            var kinds = new Kind[paragraph.Length];
            int c = 0;
            for (int i = 0; i < n; i++)
            {
                int level = baseLevel == 0
                    ? k[i] switch { Kind.R => 1, Kind.EN => 2, _ => 0 }
                    : k[i] switch { Kind.R => 1, _ => 2 };
                for (int j = 0; j < clusters[i].Length; j++, c++)
                {
                    levels[c] = level;
                    kinds[c] = original[i];
                }
            }
            return (levels, kinds);
        }

        private static Kind Direction(Kind kind) => kind == Kind.EN ? Kind.R : kind;

        // A text element takes the class of its base. A Hebrew mark with no base counts as Hebrew.
        private static Kind Classify(string cluster)
        {
            Rune.DecodeFromUtf16(cluster, out Rune rune, out _);
            int v = rune.Value;
            if (v == 0xFB29) return Kind.ES;   // Hebrew alternative plus sign
            if (IsHebrew(v) || v == 0x200F) return Kind.R;
            if (v is >= '0' and <= '9' or 0xB2 or 0xB3 or 0xB9 or 0x2070 or >= 0x2074 and <= 0x2079
                or >= 0x2080 and <= 0x2089 or >= 0xFF10 and <= 0xFF19) return Kind.EN;
            if (v is '+' or '-' or 0x207A or 0x207B or 0x208A or 0x208B or 0x2212 or 0xFE62 or 0xFE63
                or 0xFF0B or 0xFF0D) return Kind.ES;
            if (v is '#' or '$' or '%' or >= 0xA2 and <= 0xA5 or 0xB0 or 0xB1 or 0x058F or 0x0E3F
                or >= 0x2030 and <= 0x2034 or >= 0x20A0 and <= 0x20CF or 0x212E or 0x2213 or 0xFE5F
                or 0xFE69 or 0xFE6A or 0xFF03 or 0xFF04 or 0xFF05 or 0xFFE0 or 0xFFE1 or 0xFFE5 or 0xFFE6)
                return Kind.ET;
            if (v is ',' or '.' or '/' or ':' or 0xA0 or 0x202F or 0x2044 or 0xFE50 or 0xFE52 or 0xFE55
                or 0xFF0C or 0xFF0E or 0xFF0F or 0xFF1A) return Kind.CS;
            if (v is '\t' or '\r' or '\n' or 0x1F) return Kind.S;
            if (v is ' ' or 0x0C or 0x1680 or >= 0x2000 and <= 0x200A or 0x2028 or 0x205F or 0x3000) return Kind.WS;
            if (v == 0x200E) return Kind.L;
            return Rune.GetUnicodeCategory(rune) switch
            {
                UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark => Kind.NSM,
                UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                    or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
                    or UnicodeCategory.OtherLetter or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber
                    or UnicodeCategory.PrivateUse => Kind.L,
                _ => Kind.ON,
            };
        }

        private static bool IsHebrew(int v) => v is >= 0x0590 and <= 0x05FF or >= 0xFB1D and <= 0xFB4F;

        // Arabic, Syriac, Thaana, NKo, Samaritan, Mandaic and their presentation forms, plus the
        // supplementary right-to-left blocks: scripts this layout does not handle.
        private static bool IsOtherRightToLeft(int v) =>
            v is >= 0x0600 and <= 0x08FF or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF
                or >= 0x10800 and <= 0x10FFF or >= 0x1E800 and <= 0x1EFFF;

        private static string Mirror(string cluster)
        {
            if (cluster.Length != 1) return cluster;
            return cluster[0] switch
            {
                '(' => ")", ')' => "(",
                '[' => "]", ']' => "[",
                '{' => "}", '}' => "{",
                '<' => ">", '>' => "<",
                '«' => "»", '»' => "«",
                '‹' => "›", '›' => "‹",
                '≤' => "≥", '≥' => "≤",
                _ => cluster,
            };
        }
    }
}
