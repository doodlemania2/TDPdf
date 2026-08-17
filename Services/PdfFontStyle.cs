using System;
using System.Collections.Generic;
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

        /// <summary>
        /// PostScript name to Windows family (#187). A PDF font resource carries a POSTSCRIPT name,
        /// which is not a Windows family name: "ArialMT", "TimesNewRomanPSMT" and "Helvetica" name
        /// no installed family at all. The literal reached <c>new FontFamily(...)</c> in the in-place
        /// text editor, where WPF silently substituted a fallback face, and <c>FontCoverage</c> on
        /// the save path, where it fell through to the last-resort family — so the detected bold /
        /// italic was right but the family never applied, which reads to the user as "all my
        /// formatting was lost". Keyed on the name with separators removed, lowercased.
        /// </summary>
        private static readonly Dictionary<string, string> PsNameMap = new(StringComparer.Ordinal)
        {
            ["helvetica"]         = "Arial",
            ["helveticaneue"]     = "Arial",
            ["arial"]             = "Arial",
            ["arialmt"]           = "Arial",
            ["arialnarrow"]       = "Arial Narrow",
            ["times"]             = "Times New Roman",
            ["timesnewroman"]     = "Times New Roman",
            ["timesnewromanps"]   = "Times New Roman",
            ["timesnewromanpsmt"] = "Times New Roman",
            ["courier"]           = "Courier New",
            ["couriernew"]        = "Courier New",
            ["couriernewps"]      = "Courier New",
            ["couriernewpsmt"]    = "Courier New",
            ["symbol"]            = "Symbol",
            ["zapfdingbats"]      = "Wingdings",
            ["segoeui"]           = "Segoe UI",
        };

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

            // Only now, with the subset prefix and the trailing face tokens gone, is what remains a
            // bare family name that can be looked up — "Helvetica-BoldOblique" has to become
            // "Helvetica" before the map can turn it into "Arial" (#182 + #187 compose in that
            // order, and the bold/italic flags were already read off the untouched name above).
            family = NormalizePsFamily(family);

            if (string.IsNullOrWhiteSpace(family)) family = DefaultFamily;
            return new DetectedPdfFontStyle(family, bold, italic);
        }

        /// <summary>
        /// Maps a face-stripped PostScript family to the Windows family it means (#187). An unknown
        /// name is retried with its trailing PostScript foundry tag dropped, then CamelCase-split
        /// into words ("BookAntiqua" -> "Book Antiqua"), which is how PostScript names encode a
        /// family that has spaces in it.
        /// </summary>
        private static string NormalizePsFamily(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return family;

            string key = Regex.Replace(family, @"[-_, ]", string.Empty).ToLowerInvariant();
            if (PsNameMap.TryGetValue(key, out var mapped)) return mapped;

            // Trailing foundry tags: "TimesNewRomanMT"-style names that the map does not carry
            // verbatim. DELIBERATE DIVERGENCE from upstream, which keeps the trimmed name when the
            // retry also misses. Bare "MT" is a real part of several installed Windows families
            // ("Bell MT", "Bodoni MT", "Calisto MT", "Gill Sans MT"), and TdpFontResolver already
            // matches those loosely by ignoring spaces — so dropping the tag on a miss would turn a
            // "BellMT" that resolved correctly into a "Bell" that resolves to nothing. Keeping the
            // original instead lets the CamelCase split below produce "Bell MT", which is both the
            // real family name and still a loose match. The map retry is unaffected either way.
            string trimmed = Regex.Replace(family, @"(?:PSMT|PS|MT)$", string.Empty);
            if (trimmed.Length > 0 && trimmed != family)
            {
                key = Regex.Replace(trimmed, @"[-_, ]", string.Empty).ToLowerInvariant();
                if (PsNameMap.TryGetValue(key, out mapped)) return mapped;
            }

            // CamelCase -> spaced words, only when the name has no separators already: a name that
            // already reads as a family ("Book Antiqua", "MS-Gothic") is left exactly as it is.
            if (!family.Contains(' ') && !family.Contains('-') && !family.Contains('_'))
                family = Regex.Replace(family, @"(?<=[a-z])(?=[A-Z])|(?<=[A-Za-z])(?=\d)", " ");

            return family;
        }
    }
}
