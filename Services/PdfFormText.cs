using System.Collections.Generic;

namespace TDPdf.Services
{
    /// <summary>Text helpers for AcroForm appearance streams: DA parsing and PDF string escaping.</summary>
    internal static class PdfFormText
    {
        /// <summary>
        /// Parses a PDF Default Appearance string ("/Helv 12 Tf 0 g") to extract the font
        /// resource name and point size.
        /// </summary>
        internal static (string fontName, double fontSize) ParseDaString(string? da)
        {
            string fontName = "/Helv";
            double fontSize = 0;
            if (string.IsNullOrWhiteSpace(da)) return (fontName, fontSize);

            var tokens = da.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 2 < tokens.Length; i++)
            {
                if (tokens[i + 2] == "Tf" &&
                    double.TryParse(tokens[i + 1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double fs))
                {
                    fontName = tokens[i];
                    fontSize = fs;
                    break;
                }
            }
            return (fontName, fontSize);
        }

        // Upstream v1.7.1 (#180): the generated appearance streams declare /WinAnsiEncoding, but the
        // escape that fed them replaced every character above U+00FF with '?'. WinAnsi is code page
        // 1252, whose 0x80-0x9F block holds exactly the characters that were being thrown away —
        // curly quotes and apostrophes, en/em dashes, bullets, ellipses — so a field pasted in from
        // a word processor came out full of question marks ("Hunter?s Mark").
        //
        // Upstream calls Encoding.GetEncoding(1252). We deliberately do NOT: on .NET (Core) code
        // page 1252 is not built in, and GetEncoding(1252) throws ArgumentException unless
        // CodePagesEncodingProvider is registered — which this app does not do anywhere (see the
        // remark on PdfDocumentService.FileHasEncryption, which avoids 1252 for the same reason).
        // In a static initializer that throw would surface as a TypeInitializationException on the
        // save path. This table is the complete set of CP1252 code points above U+00FF, so it is
        // equivalent for every character WinAnsi can actually represent, needs no provider
        // registration, and cannot throw. Below U+0100 CP1252 and Latin-1 agree, so those pass
        // through untouched exactly as before; anything with no WinAnsi slot (CJK and the like)
        // still falls back to '?', the same as the old behaviour.
        internal static readonly Dictionary<char, char> WinAnsiHighMap = new()
        {
            ['\u20AC'] = '\u0080',   // euro sign
            ['\u201A'] = '\u0082',   // single low-9 quotation mark
            ['\u0192'] = '\u0083',   // latin small letter f with hook
            ['\u201E'] = '\u0084',   // double low-9 quotation mark
            ['\u2026'] = '\u0085',   // horizontal ellipsis
            ['\u2020'] = '\u0086',   // dagger
            ['\u2021'] = '\u0087',   // double dagger
            ['\u02C6'] = '\u0088',   // modifier letter circumflex accent
            ['\u2030'] = '\u0089',   // per mille sign
            ['\u0160'] = '\u008A',   // capital S with caron
            ['\u2039'] = '\u008B',   // single left-pointing angle quotation mark
            ['\u0152'] = '\u008C',   // capital ligature OE
            ['\u017D'] = '\u008E',   // capital Z with caron
            ['\u2018'] = '\u0091',   // left single quotation mark
            ['\u2019'] = '\u0092',   // right single quotation mark (the curly apostrophe)
            ['\u201C'] = '\u0093',   // left double quotation mark
            ['\u201D'] = '\u0094',   // right double quotation mark
            ['\u2022'] = '\u0095',   // bullet
            ['\u2013'] = '\u0096',   // en dash
            ['\u2014'] = '\u0097',   // em dash
            ['\u02DC'] = '\u0098',   // small tilde
            ['\u2122'] = '\u0099',   // trade mark sign
            ['\u0161'] = '\u009A',   // small s with caron
            ['\u203A'] = '\u009B',   // single right-pointing angle quotation mark
            ['\u0153'] = '\u009C',   // small ligature oe
            ['\u017E'] = '\u009E',   // small z with caron
            ['\u0178'] = '\u009F',   // capital Y with diaeresis
            // No WinAnsi slot of their own, but these are the folds the platform's own best-fit
            // table applies, and word processors emit them constantly.
            ['\u2010'] = '-',        // hyphen
            ['\u2011'] = '-',        // non-breaking hyphen
            ['\u2012'] = '-',        // figure dash
            ['\u2015'] = '\u0097',   // horizontal bar -> em dash
            ['\u2032'] = '\'',       // prime -> apostrophe
            ['\u2033'] = '"',        // double prime -> quotation mark
        };

        /// <summary>Escapes a string for use in a PDF literal string (parentheses syntax).</summary>
        internal static string EscapePdfString(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char raw in s)
            {
                // Fold to the single WinAnsi byte the appearance stream's /WinAnsiEncoding will read
                // BEFORE escaping, so a mapped character that happens to need escaping still gets it.
                char c = raw < 256 ? raw
                       : WinAnsiHighMap.TryGetValue(raw, out var mapped) ? mapped
                       : '?';
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '(':  sb.Append("\\(");  break;
                    case ')':  sb.Append("\\)");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\n': sb.Append("\\n");  break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
