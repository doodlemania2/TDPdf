namespace TDPdf.Services
{
    /// <summary>
    /// Names a page size the way people say it (Letter, A4, ARCH D, ...). Pure, so tests/PdfCore can
    /// pin the table and the tolerance. Sizes are PostScript points (72 per inch), and a page matches
    /// in either orientation.
    /// </summary>
    public static class PaperSizeNames
    {
        /// <summary>How far, in points, each side may differ from the standard and still match.</summary>
        public const double Tolerance = 2.0;

        // Portrait (short x long) points. ISO sizes are from their millimetre definitions.
        private static readonly (string Name, double Short, double Long)[] Table =
        {
            ("Letter",    612.0,   792.0),
            ("Legal",     612.0,   1008.0),
            ("Tabloid",   792.0,   1224.0),   // Ledger when it lies on its side
            ("Executive", 522.0,   756.0),
            ("A3",        841.89,  1190.55),
            ("A4",        595.28,  841.89),
            ("A5",        419.53,  595.28),
            ("B4",        708.66,  1000.63),  // ISO 250 x 353 mm
            ("B5",        498.90,  708.66),   // ISO 176 x 250 mm
            ("ARCH A",    648.0,   864.0),
            ("ARCH B",    864.0,   1296.0),
            ("ARCH C",    1296.0,  1728.0),
            ("ARCH D",    1728.0,  2592.0),
            ("ARCH E",    2592.0,  3456.0),
        };

        /// <summary>The standard name for a page of this size, or null when it matches none.</summary>
        public static string? Name(double widthPt, double heightPt)
        {
            if (!double.IsFinite(widthPt) || !double.IsFinite(heightPt) || widthPt <= 0 || heightPt <= 0) return null;
            double shortSide = Math.Min(widthPt, heightPt), longSide = Math.Max(widthPt, heightPt);
            foreach (var (name, s, l) in Table)
            {
                if (Math.Abs(shortSide - s) > Tolerance || Math.Abs(longSide - l) > Tolerance) continue;
                // A tabloid sheet turned sideways is a ledger.
                if (name == "Tabloid" && widthPt > heightPt) return "Ledger";
                return name;
            }
            return null;
        }
    }
}
