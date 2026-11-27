using System.Globalization;

namespace TDPdf.Services
{
    /// <summary>
    /// How an EPUB is laid out into pages: the page size, the margin and the base text size. An EPUB has no
    /// pages of its own, so these are the reader's choice, kept in Settings and asked for the first time a
    /// book is opened. Pure, so the presets and the clamping are pinned by tests/PdfCore.
    /// </summary>
    internal sealed record BookLayoutOptions(string PaperName, double WidthPt, double HeightPt, double MarginMm, double FontSizePt)
    {
        internal const double MinMarginMm = 5, MaxMarginMm = 40, MinFontPt = 7, MaxFontPt = 24;

        /// <summary>The page sizes offered. Points, portrait.</summary>
        internal static readonly (string Name, double Width, double Height)[] Papers =
        {
            ("Letter", 612.0, 792.0),
            ("A4", 595.28, 841.89),
            ("A5", 419.53, 595.28),
            ("Book (6 x 9 in)", 432.0, 648.0),
            ("Pocket (4.25 x 6.87 in)", 306.0, 495.0),
        };

        /// <summary>Letter for people in the United States, A4 elsewhere; 20 mm margins; 11 pt text.</summary>
        internal static BookLayoutOptions Default(bool metricRegion) =>
            For(metricRegion ? "A4" : "Letter", 20, 11);

        internal static BookLayoutOptions For(string? paperName, double marginMm, double fontSizePt)
        {
            var p = Papers.FirstOrDefault(x => string.Equals(x.Name, paperName, StringComparison.OrdinalIgnoreCase));
            if (p.Name is null) p = Papers[0];
            return new BookLayoutOptions(p.Name, p.Width, p.Height, Clamp(marginMm, MinMarginMm, MaxMarginMm, 20),
                Clamp(fontSizePt, MinFontPt, MaxFontPt, 11));
        }

        private static double Clamp(double v, double lo, double hi, double fallback) =>
            double.IsFinite(v) ? Math.Min(hi, Math.Max(lo, v)) : fallback;

        /// <summary>Margin in points.</summary>
        internal double MarginPt => MarginMm * 72.0 / 25.4;

        /// <summary>Height of the area text can use on one page, in points.</summary>
        internal double UsableHeightPt => Math.Max(72, HeightPt - 2 * MarginPt);

        internal static string Pt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
