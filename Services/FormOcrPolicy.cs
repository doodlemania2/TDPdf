using System;
using System.Collections.Generic;
using System.Linq;

namespace TDPdf.Services
{
    // ============================================================
    // Constraints for form-aware OCR (upstream KillerPDF #242).
    //
    // Recognizing a form field's rectangle instead of the whole page is only half the win. The
    // other half is that a form field says what it is allowed to contain, and Tesseract does not
    // have to guess where the PDF already knows:
    //
    //   * a numeric field cannot hold O, l or S, which are the three characters OCR most often
    //     substitutes for 0, 1 and 5 on a scan;
    //   * a comb field has exactly MaxLen cells, each holding one glyph, so it is read cell by
    //     cell rather than as a word that Tesseract may split or join;
    //   * a choice field's value has to be one of its /Opt entries, so a near miss can be snapped
    //     to the real option instead of left wrong.
    //
    // Deliberately dependency-free: no PDF types, no Tesseract types, no UI. The orchestration in
    // Ocr.cs decides WHAT to recognize; everything here decides what the answer is allowed to be.
    // ============================================================
    internal static class FormOcrPolicy
    {
        internal const string Digits = "0123456789";

        /// <summary>Digits plus the separators a formatted number can legitimately carry.</summary>
        internal const string NumericChars = Digits + ".,-+$%()/ ";

        /// <summary>
        /// The Tesseract character whitelist for a field, or null to leave recognition unrestricted.
        /// Only returned where the PDF genuinely constrains the value — guessing a whitelist for an
        /// ordinary text field would silently delete characters the user actually wrote.
        /// </summary>
        internal static string? WhitelistFor(bool isNumeric, bool isComb)
        {
            if (isNumeric) return NumericChars;
            // A comb cell is one glyph, but not necessarily a digit — comb is used for postcodes and
            // reference codes too — so it gets a segmentation hint rather than a character one.
            _ = isComb;
            return null;
        }

        /// <summary>
        /// Cleans one field's raw OCR output: collapses the line breaks a single-line field cannot
        /// contain, trims, applies /MaxLen, and snaps a choice value onto its real option.
        /// </summary>
        internal static string Normalize(string? raw, bool isMultiLine, int maxLen,
            IReadOnlyList<string>? options)
        {
            string value = (raw ?? "").Trim();
            if (value.Length == 0) return "";

            if (!isMultiLine)
                value = value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

            // Tesseract likes to pad with interior runs of spaces on sparse crops.
            value = string.Join(" ", value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

            if (options is { Count: > 0 } && NearestOption(value, options) is { } snapped)
                return snapped;

            if (maxLen > 0 && value.Length > maxLen) value = value[..maxLen];
            return value;
        }

        /// <summary>
        /// The option this value most likely IS, or null when nothing is close enough to justify
        /// overriding what was actually read.
        /// </summary>
        /// <remarks>
        /// The two failure modes are not equally bad. Failing to snap leaves the raw reading, which
        /// is visibly imperfect and gets checked. Snapping to the WRONG option silently replaces a
        /// value with a confident-looking lie, which does not. So two conditions must both hold:
        /// the candidate is within 40% of its own length in edits — enough for the classic OCR
        /// confusions, "rn" read as "m" and "li" as "h" — AND it is unambiguously better than the
        /// runner-up. The second is what makes the first safe: a value that is merely nearest to
        /// something, like a state not on the list at all, leaves two options roughly equidistant
        /// and is left alone.
        /// </remarks>
        internal static string? NearestOption(string value, IReadOnlyList<string> options)
        {
            foreach (string opt in options)
                if (string.Equals(opt, value, StringComparison.OrdinalIgnoreCase)) return opt;

            string? best = null;
            int bestDistance = int.MaxValue, runnerUp = int.MaxValue;
            foreach (string opt in options)
            {
                if (string.IsNullOrEmpty(opt)) continue;
                int d = EditDistance(value, opt);
                if (d < bestDistance) { runnerUp = bestDistance; bestDistance = d; best = opt; }
                else if (d < runnerUp) { runnerUp = d; }
            }
            if (best is null) return null;

            int budget = Math.Max(1, best.Length * 2 / 5);
            if (bestDistance > budget) return null;
            // A single candidate has no runner-up to be ambiguous against.
            return runnerUp == int.MaxValue || runnerUp >= bestDistance + 2 ? best : null;
        }

        /// <summary>Levenshtein distance, case-insensitive, two-row so a long /Opt list stays cheap.</summary>
        private static int EditDistance(string a, string b)
        {
            a = a.ToLowerInvariant();
            b = b.ToLowerInvariant();
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            var prev = new int[b.Length + 1];
            var cur  = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                (prev, cur) = (cur, prev);
            }
            return prev[b.Length];
        }

        /// <summary>
        /// True when a field's additional-action JavaScript marks it as a formatted number. Acrobat
        /// and LiveCycle both write <c>AFNumber_Format</c> / <c>AFNumber_Keystroke</c> into /AA, so
        /// this reads a standard signal rather than inferring numeric-ness from the field's name.
        /// </summary>
        internal static bool LooksNumeric(string? additionalActionJs) =>
            additionalActionJs is not null
            && (additionalActionJs.Contains("AFNumber_", StringComparison.Ordinal)
                || additionalActionJs.Contains("AFPercent_", StringComparison.Ordinal));
    }
}
