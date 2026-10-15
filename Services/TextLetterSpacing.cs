using System;
using System.Collections.Generic;
using System.Globalization;

namespace TDPdf.Services
{
    /// <summary>
    /// The arithmetic behind letter spacing on a text annotation: how wide a spaced string is,
    /// where each character sits inside it, and where the lines break. #135 item 2.
    /// </summary>
    /// <remarks>
    /// <b>Why this is out here and not in MainWindow.</b> Letter spacing has to be applied in four
    /// places that must agree to the pixel — the on-screen annotation, <c>MeasureTextAnnotation</c>
    /// (which is also the hit/selection box), <c>WrapTextToWidth</c>, and the PDF burn-in — and
    /// <c>TextTypeface</c>'s docblock already records what happens when they do not: the text moves
    /// when you save. Four copies of "advance plus spacing" is exactly how that divergence gets
    /// reintroduced, so there is one copy, and it is somewhere tests/PdfCore can reach it. Nothing
    /// here references WPF or PdfSharpCore: each caller passes in its own measuring function, which
    /// is what lets the same arithmetic serve a WPF <c>FormattedText</c> and an <c>XGraphics</c>.
    ///
    /// <b>The whole-string / per-character split is the load-bearing design decision.</b> Measuring
    /// a whole string and summing the advances of its characters do NOT give the same answer: a
    /// whole string picks up kerning pairs and ligatures that a sum of isolated advances cannot.
    /// So there are two models, and a given annotation is wholly in one of them:
    ///
    ///   * <b>spacing == 0</b> (<see cref="IsNone"/>) — every caller keeps doing exactly what it
    ///     did before this feature existed: one whole-string measurement, one whole-string draw.
    ///     Every annotation written by every earlier build is in this model, and must render and
    ///     burn in byte-identically. <see cref="Width"/> therefore routes straight to the caller's
    ///     whole-string measure and never looks at a single character.
    ///   * <b>spacing != 0</b> — per-character advances everywhere, in all four places at once.
    ///     Kerning is given up, which is the honest price of positioning characters individually;
    ///     what is bought is that the box, the wrap points, the pixels and the PDF all come from
    ///     the same sum.
    ///
    /// <b>Spacing goes BETWEEN characters, never after the last one.</b> An n-character string gets
    /// (n - 1) gaps. Trailing spacing would make the measured box one gap wider than the ink it
    /// contains, which then drifts a right-aligned or centred box by a whole spacing unit and
    /// leaves a visible dead strip inside a whiteout fill. See <see cref="Layout"/> for the one
    /// place a trailing advance is computed and deliberately discarded.
    /// </remarks>
    internal static class TextLetterSpacing
    {
        /// <summary>Slider floor for the text style bar. Negative = tightening, which is a real need.</summary>
        /// <remarks>
        /// Tightening past about a third of an em collides glyphs into illegibility, and the bar's
        /// spacing is in canvas px rather than ems, so the floor is a flat number chosen to be
        /// useful at the sizes the size dropdown offers (8-72) without letting a stray drag turn a
        /// line into a smear.
        /// </remarks>
        internal const double SliderMin = -5.0;

        /// <summary>
        /// Slider ceiling. Wide enough to drop one character per box on the preprinted forms this
        /// exists for, at any font size in the picker.
        /// </summary>
        internal const double SliderMax = 20.0;

        /// <summary>Slider granularity, in canvas px.</summary>
        internal const double SliderStep = 0.5;

        /// <summary>
        /// True when this annotation is in the untouched, pre-feature model: no spacing is applied
        /// and every caller must fall back to its original whole-string path.
        /// </summary>
        /// <remarks>
        /// NaN and infinity are folded in here rather than being left to arrive at a
        /// <c>FormattedText</c> or an <c>XGraphics</c>. A persisted annotation is JSON the app did
        /// not necessarily write, and WPF refuses a non-finite Width outright — the same class of
        /// crash <c>RenderTextAnnotation</c>'s IsFinite guard was added for (#181). Treating a
        /// nonsense value as "no spacing" degrades to the appearance the box has always had.
        /// </remarks>
        internal static bool IsNone(double spacing) =>
            spacing == 0 || double.IsNaN(spacing) || double.IsInfinity(spacing);

        /// <summary>
        /// Splits <paramref name="text"/> into the units spacing is inserted between.
        /// </summary>
        /// <remarks>
        /// Text ELEMENTS, not chars. A surrogate pair is one character to the reader and two UTF-16
        /// code units to a <c>foreach (char)</c>, so splitting on chars would insert a gap through
        /// the middle of an emoji and draw each half as its own (meaningless) glyph. The same goes
        /// for a combining accent, which belongs hard against the base letter it modifies and must
        /// not be pushed off it.
        /// </remarks>
        internal static List<string> Clusters(string text)
        {
            var clusters = new List<string>();
            if (string.IsNullOrEmpty(text)) return clusters;
            var e = StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext()) clusters.Add((string)e.Current);
            return clusters;
        }

        /// <summary>
        /// How wide <paramref name="text"/> renders at the given spacing.
        /// </summary>
        /// <param name="text">The string to measure.</param>
        /// <param name="spacing">Extra space between characters, in the caller's own units.</param>
        /// <param name="measureWhole">
        /// Measures a whole string the way the caller has always measured it. Used, and ONLY used,
        /// when <paramref name="spacing"/> is none — this is the byte-identical path.
        /// </param>
        /// <param name="measureCluster">
        /// Measures the ADVANCE of one text element — how far the pen moves, which for a space
        /// character is not the same as the ink it covers. A WPF caller must pass
        /// <c>WidthIncludingTrailingWhitespace</c> here and <c>Width</c> to
        /// <paramref name="measureWhole"/>, because <c>FormattedText.Width</c> drops trailing
        /// whitespace and a lone space would otherwise measure zero and collapse.
        /// </param>
        internal static double Width(string text, double spacing,
                                     Func<string, double> measureWhole,
                                     Func<string, double> measureCluster)
        {
            if (IsNone(spacing) || string.IsNullOrEmpty(text)) return measureWhole(text);

            var clusters = Clusters(text);
            if (clusters.Count == 0) return measureWhole(text);

            double w = 0;
            for (int i = 0; i < clusters.Count; i++) w += measureCluster(clusters[i]);
            // (n - 1), not n: BETWEEN the characters. See the type docblock.
            return w + spacing * (clusters.Count - 1);
        }

        /// <summary>
        /// Where each text element of <paramref name="text"/> sits, as an offset from the string's
        /// left edge. Callers draw one element per entry at that offset.
        /// </summary>
        /// <remarks>
        /// The running pen adds spacing after every element INCLUDING the last, but the last
        /// addition is never read back out — there is no entry after it — so the widest offset this
        /// yields plus its own advance is exactly <see cref="Width"/>. That is the "between, not
        /// after" rule falling out of the loop rather than being special-cased at the end, and it
        /// is why <see cref="Width"/> is the authority on extent and this is not.
        /// </remarks>
        internal static List<(string Cluster, double X)> Layout(
            string text, double spacing, Func<string, double> measureCluster)
        {
            var clusters = Clusters(text);
            var placed = new List<(string, double)>(clusters.Count);
            double x = 0;
            for (int i = 0; i < clusters.Count; i++)
            {
                placed.Add((clusters[i], x));
                x += measureCluster(clusters[i]) + spacing;
            }
            return placed;
        }

        /// <summary>
        /// Greedy word-wrap of <paramref name="text"/> to <paramref name="maxWidth"/>, breaking at
        /// whatever points <paramref name="width"/> says the line overflows. Over-long single words
        /// are hard-broken by character.
        /// </summary>
        /// <remarks>
        /// Lifted unchanged out of <c>MainWindow.WrapTextToWidth</c>, which now calls this. The
        /// algorithm is untouched on purpose: it is what decides where a saved PDF breaks its
        /// lines, so any behaviour change here silently reflows every existing annotation in every
        /// document the fleet has. What makes it spacing-aware is entirely the
        /// <paramref name="width"/> function the caller hands in — a <see cref="Width"/> closure
        /// carrying the annotation's spacing — so a wider gap between characters pushes the break
        /// earlier without this loop knowing spacing exists at all.
        /// </remarks>
        internal static List<string> Wrap(string text, double maxWidth, Func<string, double> width)
        {
            var lines = new List<string>();
            if (maxWidth <= 0) { lines.Add(text); return lines; }

            // Appends a word to a fresh line, hard-breaking it across lines if it alone overflows.
            string HardBreakAppend(string word)
            {
                if (width(word) <= maxWidth || word.Length <= 1) return word;
                string chunk = "";
                foreach (char ch in word)
                {
                    string next = chunk + ch;
                    if (chunk.Length > 0 && width(next) > maxWidth)
                    {
                        lines.Add(chunk);
                        chunk = ch.ToString();
                    }
                    else chunk = next;
                }
                return chunk;
            }

            foreach (var para in text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
            {
                string cur = "";
                foreach (var word in para.Split(' '))
                {
                    if (cur.Length == 0)
                    {
                        cur = HardBreakAppend(word);
                    }
                    else if (width(cur + " " + word) <= maxWidth)
                    {
                        cur += " " + word;
                    }
                    else
                    {
                        lines.Add(cur);
                        cur = HardBreakAppend(word);
                    }
                }
                lines.Add(cur);
            }
            return lines;
        }
    }
}
