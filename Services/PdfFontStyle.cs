using System.Text.RegularExpressions;

namespace TDPdf.Services
{
    /// <summary>
    /// The face styling read out of a PDF font resource name: the family with its trailing face
    /// tokens removed, plus the bold / italic flags those tokens encoded.
    /// </summary>
    internal readonly record struct DetectedPdfFontStyle(string Family, bool Bold, bool Italic);

    /// <summary>
    /// PDF font resources commonly carry face styling in their PostScript names rather than in
    /// separate metadata — "Helvetica-BoldOblique", "Arial,BoldItalic", "ABCDEF+TimesNewRomanPS-BoldMT".
    /// Keep that styling when a source line is lifted into the in-place text editor (#182).
    /// </summary>
    internal static class PdfFontStyle
    {
        /// <summary>Family used when a font resource carries no usable name.</summary>
        internal const string DefaultFamily = "Segoe UI";

        internal static DetectedPdfFontStyle FromPdfName(string? rawName)
        {
            string name = rawName?.Trim() ?? string.Empty;

            // Strip the PDF subset prefix: "ABCDEF+Helvetica" -> "Helvetica".
            int subset = name.IndexOf('+');
            if (subset >= 0 && subset + 1 < name.Length) name = name[(subset + 1)..];

            bool bold = Regex.IsMatch(name, @"(?i)(bold|semibold|demibold|black|heavy|[-_,]bd(?:mt)?$)");
            bool italic = Regex.IsMatch(name, @"(?i)(italic|oblique|[-_,](?:it|obl)(?:mt)?$)");

            // Remove only TRAILING face tokens. The blanket Replace() chain this superseded deleted
            // "-Bold" out of the MIDDLE of "Helvetica-BoldOblique" and left "HelveticaOblique", which
            // matches no installed family and silently fell back to the default font; "-Oblique" on
            // its own was never stripped at all. A style word that is genuinely part of a family name
            // elsewhere in the string is left alone.
            string family = Regex.Replace(name,
                @"(?i)(?:[-_, ]?(?:bolditalic|boldoblique|semibolditalic|demibolditalic|bold|semibold|demibold|black|heavy|italic|oblique|regular|roman|bd|it|obl)(?:mt)?)$",
                string.Empty).Trim(' ', '-', '_', ',');

            if (string.IsNullOrWhiteSpace(family)) family = DefaultFamily;
            return new DetectedPdfFontStyle(family, bold, italic);
        }
    }
}
