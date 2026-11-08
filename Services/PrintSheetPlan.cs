using System;
using System.Collections.Generic;

namespace TDPdf.Services
{
    /// <summary>
    /// Pure helpers for the print window: how selected pages group into sheets (and in what order the
    /// sheets go out), how the 1-up scale modes resolve to a factor, and how the saved two-sided choice
    /// maps to the combo. No WPF types, so tests/PdfCore compiles this file as-is.
    /// </summary>
    internal static class PrintSheetPlan
    {
        /// <summary>
        /// Groups <paramref name="selected"/> (already range- and odd/even-filtered, in document order)
        /// into sheets of <paramref name="nUp"/> pages. <paramref name="reverse"/> reverses the whole
        /// SHEETS after grouping, not the page indices: with 2-up over pages 1..5 the sheets are
        /// [1,2] [3,4] [5], and reversed they go out [5] [3,4] [1,2], so the tiles on each sheet keep
        /// their reading order and the stack comes off the printer last-sheet-first.
        /// </summary>
        public static List<List<int>> Sheets(IReadOnlyList<int> selected, int nUp, bool reverse)
        {
            if (nUp < 1) nUp = 1;
            var sheets = new List<List<int>>((selected.Count + nUp - 1) / nUp);
            for (int i = 0; i < selected.Count; i += nUp)
            {
                var sheet = new List<int>(Math.Min(nUp, selected.Count - i));
                for (int j = i; j < Math.Min(selected.Count, i + nUp); j++) sheet.Add(selected[j]);
                sheets.Add(sheet);
            }
            if (reverse) sheets.Reverse();
            return sheets;
        }

        // Scale modes of the 1-up Scale combo. Values are the combo indices.
        public const int ScaleFit = 0, ScaleActual = 1, ScaleShrink = 2, ScaleCustom = 3;

        /// <summary>
        /// The image-scale factor for a 1-up page. <paramref name="fit"/> is the factor that fills the
        /// printable area; <paramref name="actual"/> the one that gives the page its real size (100%).
        /// Shrink-oversized is the smaller of the two, so a page larger than the sheet is shrunk to fit
        /// and a smaller one stays at its real size instead of being blown up.
        /// </summary>
        public static double Factor(int mode, double fit, double actual, double customPct)
        {
            if (!(actual > 0)) return fit;   // no usable page size: fit is the only meaningful answer
            return mode switch
            {
                ScaleActual => actual,
                ScaleShrink => Math.Min(actual, fit),
                ScaleCustom => actual * (Math.Clamp(customPct, 25, 400) / 100.0),
                _           => fit
            };
        }

        /// <summary>True when the result is the fit factor, so the caller may snap to the printable edge.</summary>
        public static bool IsFitLimited(int mode, double fit, double actual)
            => mode == ScaleFit || !(actual > 0) || (mode == ScaleShrink && fit <= actual);

        // ---- Two-sided choice persistence ----

        /// <summary>
        /// Resolves the saved two-sided choice to a <c>Duplexing</c> name. The new edge setting wins when
        /// present; otherwise the original on/off bool from before the edge was saved is read as long edge.
        /// </summary>
        public static string ResolveDuplex(string? savedEdge, bool legacyTwoSided)
        {
            switch (savedEdge)
            {
                case "OneSided":
                case "TwoSidedLongEdge":
                case "TwoSidedShortEdge":
                    return savedEdge;
            }
            return legacyTwoSided ? "TwoSidedLongEdge" : "OneSided";
        }

        /// <summary>Combo order: One-sided, Two-sided (long edge), Two-sided (short edge).</summary>
        public static int DuplexComboIndex(string duplexName) => duplexName switch
        {
            "TwoSidedLongEdge"  => 1,
            "TwoSidedShortEdge" => 2,
            _                   => 0
        };

        public static string DuplexNameFromComboIndex(int index) => index switch
        {
            1 => "TwoSidedLongEdge",
            2 => "TwoSidedShortEdge",
            _ => "OneSided"
        };
    }
}
