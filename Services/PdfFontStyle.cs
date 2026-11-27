using System.Text.RegularExpressions;
using PdfSharpCore.Pdf;

namespace TDPdf.Services
{
    /// <summary>
    /// The face styling read out of a PDF font resource name: the family with its trailing face
    /// tokens removed, plus the bold / italic flags those tokens encoded.
    /// </summary>
    internal readonly record struct DetectedPdfFontStyle(string Family, bool Bold, bool Italic);

    /// <summary>
    /// What a PDF font dictionary says about its own face, read from /BaseFont, /FontDescriptor and
    /// the descendant font of a Type0. Used when the font's NAME says nothing ("CIDFont+F2", "TT1").
    /// </summary>
    internal sealed record PdfFontDictInfo(
        string? BaseFont, string? DescriptorName, string? Family, double Weight, int Flags, double ItalicAngle)
    {
        internal const int FlagItalic = 1 << 6;
        internal const int FlagForceBold = 1 << 18;
        internal bool IsBold => Weight >= 600 || (Flags & FlagForceBold) != 0;
        internal bool IsItalic => ItalicAngle != 0 || (Flags & FlagItalic) != 0;
    }

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

        /// <summary>
        /// "ABCDEF+Helvetica" becomes "Helvetica": drops everything up to the first '+', the PDF
        /// font-subset tag. A name with no '+', or one that ends in it, is returned unchanged.
        /// </summary>
        internal static string StripSubsetPrefix(string name)
        {
            int plus = name.IndexOf('+');
            return plus >= 0 && plus + 1 < name.Length ? name[(plus + 1)..] : name;
        }

        /// <summary>
        /// True for a name that carries no family or face information: a resource-style name such as
        /// "F2", "/F1", "TT1", "C2_0" or "CIDFont+F2" (what Word and many exporters write). The
        /// subset prefix does not count towards the name.
        /// </summary>
        internal static bool IsGenericName(string? rawName)
        {
            string name = rawName?.Trim().TrimStart('/') ?? string.Empty;
            name = StripSubsetPrefix(name);
            return name.Length == 0
                || Regex.IsMatch(name, @"(?i)^(?:F|TT|T|C|R|G|Fnt|Font|CIDFont)[-_]?\d{1,3}(?:_\d+)?$");
        }

        /// <summary>
        /// The face for a letter whose font is named <paramref name="rawName"/>. A descriptive name
        /// is read as before; a generic one (see <see cref="IsGenericName"/>) falls back to the font
        /// dictionary <paramref name="info"/>: a real name in /FontDescriptor or /BaseFont, then
        /// /FontFamily, with weight, ForceBold, Italic and /ItalicAngle OR-ed into the flags.
        /// </summary>
        internal static DetectedPdfFontStyle FromPdfName(string? rawName, PdfFontDictInfo? info)
        {
            if (info is null || !IsGenericName(rawName)) return FromPdfName(rawName);

            DetectedPdfFontStyle? named = null;
            foreach (var candidate in new[] { info.DescriptorName, info.BaseFont })
                if (!string.IsNullOrWhiteSpace(candidate) && !IsGenericName(candidate))
                {
                    named = FromPdfName(candidate);
                    break;
                }
            if (named is null && !string.IsNullOrWhiteSpace(info.Family))
                named = FromPdfName(info.Family);

            var basis = named ?? new DetectedPdfFontStyle(DefaultFamily, false, false);
            return new DetectedPdfFontStyle(basis.Family, basis.Bold || info.IsBold, basis.Italic || info.IsItalic);
        }

        /// <summary>Reads one font dictionary; a Type0 contributes its descendant's descriptor.</summary>
        internal static PdfFontDictInfo? ReadFontDict(PdfDictionary? font)
        {
            if (font is null) return null;
            string? baseFont = font.Elements.GetName("/BaseFont");
            baseFont = baseFont?.TrimStart('/');

            var descriptorOwner = font;
            if (font.Elements.GetName("/Subtype") == "/Type0"
                && font.Elements.GetArray("/DescendantFonts") is { Elements.Count: > 0 } kids
                && PdfItems.DerefDict(kids.Elements[0]) is { } desc)
            {
                descriptorOwner = desc;
                baseFont ??= desc.Elements.GetName("/BaseFont")?.TrimStart('/');
            }

            var fd = PdfItems.DerefDict(descriptorOwner.Elements["/FontDescriptor"]);
            string? descName = fd?.Elements.GetName("/FontName")?.TrimStart('/');
            string? family = fd is not null && PdfItems.Deref(fd.Elements["/FontFamily"]) is PdfString fs ? fs.Value : null;
            double weight = fd is not null ? Num(fd.Elements["/FontWeight"]) : 0;
            int flags = fd is not null ? (int)Num(fd.Elements["/Flags"]) : 0;
            double angle = fd is not null ? Num(fd.Elements["/ItalicAngle"]) : 0;
            return new PdfFontDictInfo(baseFont, descName, family, weight, flags, angle);
        }

        /// <summary>
        /// Every font in the page's /Resources /Font (inherited from the page tree), keyed by the
        /// resource name ("/F1"), by /BaseFont, and by /BaseFont without its subset prefix.
        /// </summary>
        internal static Dictionary<string, PdfFontDictInfo> ReadPageFonts(PdfDictionary page)
        {
            var map = new Dictionary<string, PdfFontDictInfo>(StringComparer.Ordinal);
            foreach (var node in PdfItems.Ancestors(page))
            {
                var fonts = PdfItems.DerefDict(node.Elements["/Resources"])?.Elements.GetDictionary("/Font");
                if (fonts is null) continue;
                foreach (var key in fonts.Elements.Keys)
                {
                    if (ReadFontDict(PdfItems.DerefDict(fonts.Elements[key])) is not { } info) continue;
                    map.TryAdd(key, info);
                    if (!string.IsNullOrEmpty(info.BaseFont))
                    {
                        map.TryAdd(info.BaseFont, info);
                        map.TryAdd(StripSubsetPrefix(info.BaseFont), info);
                    }
                }
                break;   // nearest /Resources wins, as in the content stream
            }
            return map;
        }

        private static double Num(PdfItem? item) => PdfItems.Deref(item) switch
        {
            PdfInteger i => i.Value,
            PdfReal r => r.Value,
            _ => 0,
        };

        internal static DetectedPdfFontStyle FromPdfName(string? rawName)
        {
            string name = rawName?.Trim() ?? string.Empty;

            // Strip the PDF subset prefix: "ABCDEF+Helvetica" -> "Helvetica".
            name = StripSubsetPrefix(name);

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
