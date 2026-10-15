using System;
using System.Collections.Generic;
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
    /// them exactly as drawn — so the saved word came out backwards. The fix is to do the one piece
    /// of bidi a pure-Hebrew line needs ourselves: draw its text elements one at a time, starting
    /// from the right edge of the line and walking left.
    ///
    /// <b>Only pure-Hebrew lines.</b> <see cref="IsHebrewRightToLeftLine"/> is deliberately narrow,
    /// matching upstream: Hebrew letters (and niqqud) with nothing else that is a letter or digit.
    /// A mixed line — Hebrew with Latin, or with digits, whose runs stay left-to-right inside the
    /// right-to-left flow — needs the full algorithm, and reversing it whole would scramble the
    /// embedded run, which is worse than the logical order it replaces. Arabic is excluded
    /// outright: it needs contextual shaping as well as reordering, and reversed but unshaped
    /// Arabic is still wrong. Both keep the path they have always had.
    ///
    /// <b>The extent does not move.</b> WPF lays a right-to-left line out inside the annotation's
    /// left-to-right flow, so on screen it occupies the same horizontal span a left-to-right line
    /// of the same width would. <see cref="Layout"/> therefore places elements inside exactly the
    /// span <see cref="TextLetterSpacing.Layout"/> would — leftmost ink at offset 0, rightmost
    /// advance ending at <see cref="TextLetterSpacing.Width"/> — only mirrored. The box, the fill,
    /// the underline and the selection all stay where they were.
    ///
    /// Nothing here references WPF or PdfSharpCore, for the same reason as
    /// <see cref="TextLetterSpacing"/>: the caller passes its own measure, and tests/PdfCore can
    /// reach it.
    /// </remarks>
    internal static class RtlTextLayout
    {
        /// <summary>
        /// True when <paramref name="line"/> contains Hebrew and no other letter or digit, so it can
        /// be drawn in visual order by simply reversing its text elements.
        /// </summary>
        /// <remarks>
        /// Hebrew is the Hebrew block (U+0590–U+05FF, which carries the niqqud and cantillation
        /// marks too) and the Alphabetic Presentation Forms Hebrew range (U+FB1D–U+FB4F). Spaces and
        /// punctuation are neutral and do not disqualify a line. Callers pass one line: a line
        /// break is neutral to this test, but reversing across one would swap the lines' contents.
        /// </remarks>
        internal static bool IsHebrewRightToLeftLine(string? line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            bool hasHebrew = false;
            foreach (Rune rune in line.EnumerateRunes())
            {
                bool hebrew = rune.Value is >= 0x0590 and <= 0x05FF or >= 0xFB1D and <= 0xFB4F;
                if (hebrew) hasHebrew = true;
                else if (Rune.IsLetterOrDigit(rune)) return false;
            }
            return hasHebrew;
        }

        /// <summary>
        /// Where each text element of <paramref name="text"/> sits when drawn in visual order, as
        /// an offset from the line's LEFT edge. Entries are in logical order.
        /// </summary>
        /// <param name="text">One line, in logical (typed) order.</param>
        /// <param name="spacing">
        /// Letter spacing between elements, in the caller's units. None (<see
        /// cref="TextLetterSpacing.IsNone"/>) is treated as zero.
        /// </param>
        /// <param name="measureCluster">The advance of one text element, as in <see cref="TextLetterSpacing.Layout"/>.</param>
        /// <remarks>
        /// Text elements, not chars, via <see cref="TextLetterSpacing.Clusters"/>: a niqqud point is
        /// a combining mark and has to be drawn with the letter it sits on. Reversing chars would
        /// put the vowel point before its base and the font would hang it on the wrong letter.
        ///
        /// <b>Only the span from the first Hebrew letter to the last is reversed.</b> The screen
        /// lays the line out in a left-to-right paragraph, and under the bidi algorithm a space or
        /// punctuation mark BETWEEN two Hebrew letters takes their direction, but one before the
        /// first or after the last sits between a Hebrew letter and the paragraph edge and takes
        /// the paragraph's — so "שלום." shows its period on the right, where it was typed, and a
        /// trailing space stays trailing. Upstream reverses the whole line, which moves both to the
        /// wrong end on save; this keeps them where WPF draws them.
        ///
        /// Elements are then placed left to right in that visual order, with the gap between
        /// neighbours and never after the last, as in <see cref="TextLetterSpacing.Layout"/> — so
        /// the line spans exactly the width its left-to-right draw would.
        /// </remarks>
        internal static List<(string Cluster, double X)> Layout(
            string text, double spacing, Func<string, double> measureCluster)
        {
            if (TextLetterSpacing.IsNone(spacing)) spacing = 0;
            var clusters = TextLetterSpacing.Clusters(text);
            var placed = new List<(string, double)>(clusters.Count);
            if (clusters.Count == 0) return placed;

            int first = -1, last = -1;
            for (int i = 0; i < clusters.Count; i++)
            {
                if (!ContainsHebrew(clusters[i])) continue;
                if (first < 0) first = i;
                last = i;
            }

            var visual = new List<int>(clusters.Count);
            if (first < 0)
            {
                for (int i = 0; i < clusters.Count; i++) visual.Add(i);
            }
            else
            {
                for (int i = 0; i < first; i++) visual.Add(i);
                for (int i = last; i >= first; i--) visual.Add(i);
                for (int i = last + 1; i < clusters.Count; i++) visual.Add(i);
            }

            var xs = new double[clusters.Count];
            double x = 0;
            foreach (int i in visual)
            {
                xs[i] = x;
                x += measureCluster(clusters[i]) + spacing;
            }
            for (int i = 0; i < clusters.Count; i++) placed.Add((clusters[i], xs[i]));
            return placed;
        }

        private static bool ContainsHebrew(string cluster)
        {
            foreach (Rune rune in cluster.EnumerateRunes())
                if (rune.Value is >= 0x0590 and <= 0x05FF or >= 0xFB1D and <= 0xFB4F) return true;
            return false;
        }
    }
}
