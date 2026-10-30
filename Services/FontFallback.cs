using System.Globalization;
using System.Text;

namespace TDPdf.Services
{
    /// <summary>One stretch of text that a single font family draws.</summary>
    internal readonly record struct FontRun(string Text, string Family);

    // ============================================================
    // The pure half of FontCoverage: which families to try, in what order, and how a value is cut
    // into per-family runs. Coverage itself is passed in as a predicate, so this file has no font
    // I/O and the test harness can drive it with a made-up font set.
    // ============================================================
    internal static class FontFallback
    {
        // Per-script preference, first match wins. Sans-first throughout, mirroring what Windows
        // itself falls back to, so a saved file looks like the editor did. Every entry is a family
        // that ships with Windows; missing ones are skipped harmlessly at lookup time.
        internal static readonly string[] ChainJapanese     = ["Yu Gothic UI", "Yu Gothic", "Meiryo", "MS Gothic", "Yu Mincho"];
        internal static readonly string[] ChainSimplified   = ["Microsoft YaHei", "DengXian", "SimSun"];
        internal static readonly string[] ChainTraditional  = ["Microsoft JhengHei", "MingLiU", "PMingLiU"];
        internal static readonly string[] ChainKorean       = ["Malgun Gothic", "Gulim"];
        internal static readonly string[] ChainIndic        = ["Nirmala UI"];
        internal static readonly string[] ChainThai         = ["Leelawadee UI", "Tahoma"];
        internal static readonly string[] ChainArabic       = ["Segoe UI", "Tahoma", "Traditional Arabic"];
        internal static readonly string[] ChainDefault      = ["Segoe UI", "Arial", "Tahoma"];

        // Tried after every script chain. Check marks, arrows, stars, dingbats and the BMP emoji live
        // here and in no text face. Segoe UI Emoji is a color font, but its glyf outlines are what
        // an embedded PDF subset carries, so it draws in monochrome, which is fine for a form value.
        internal static readonly string[] ChainSymbol       = ["Segoe UI Symbol", "Segoe UI Emoji"];

        /// <summary>
        /// Every family worth trying for <paramref name="text"/>, in order: the script's own chain,
        /// then the symbol faces, then the general default chain. No duplicates.
        /// </summary>
        internal static List<string> Candidates(string text)
        {
            var list = new List<string>();
            foreach (var chain in new[] { ChainFor(text), ChainSymbol, ChainDefault })
                foreach (string f in chain)
                    if (!list.Contains(f, StringComparer.OrdinalIgnoreCase)) list.Add(f);
            return list;
        }

        /// <summary>
        /// The caller's family when it covers all of <paramref name="text"/>, otherwise the first
        /// candidate that does, otherwise the caller's family (never worse than before).
        /// </summary>
        internal static string PickFamily(string preferred, string? text, Func<string, string, bool> coversAll)
        {
            if (string.IsNullOrEmpty(text)) return preferred;
            if (coversAll(preferred, text)) return preferred;
            foreach (string family in Candidates(text))
            {
                if (string.Equals(family, preferred, StringComparison.OrdinalIgnoreCase)) continue;
                if (coversAll(family, text)) return family;
            }
            return preferred;
        }

        /// <summary>
        /// Cuts <paramref name="text"/> into runs, each drawn by one family, for a value no single
        /// family covers. Per character: the caller's family if it covers it, else the family the
        /// current run is already in (fewer font switches), else the first candidate that does
        /// (the value's chain, the symbol faces, the defaults, then the character's own script chain).
        /// A character nothing covers stays in the caller's family. Whitespace, zero-width
        /// characters and combining marks never start a run of their own; they ride with the
        /// character before them so a mark is never split from its base.
        /// </summary>
        internal static List<FontRun> SplitRuns(string preferred, string text, Func<string, int, bool> covers)
        {
            var runs = new List<FontRun>();
            if (string.IsNullOrEmpty(text)) return runs;

            List<string>? candidates = null;
            var sb = new StringBuilder();
            string? current = null;

            for (int i = 0; i < text.Length; i++)
            {
                int len = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
                int cp = len == 2 ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
                string piece = text.Substring(i, len);
                i += len - 1;

                string family;
                if (IsIgnorable(cp) || IsMark(cp))
                {
                    if (current is not null) { sb.Append(piece); continue; }
                    family = preferred;
                }
                else if (covers(preferred, cp)) family = preferred;
                else if (current is not null && covers(current, cp)) family = current;
                else
                {
                    // The whole value's chain first (a Japanese face for the kanji in a kana line),
                    // then the chain for this one character, which is what finds the face for a
                    // second script (Bengali after Japanese) that the value's own chain lacks.
                    candidates ??= Candidates(text);
                    family = candidates.FirstOrDefault(f => covers(f, cp))
                          ?? ChainFor(piece).FirstOrDefault(f => covers(f, cp))
                          ?? preferred;
                }

                if (current is not null && !string.Equals(family, current, StringComparison.OrdinalIgnoreCase))
                {
                    runs.Add(new FontRun(sb.ToString(), current));
                    sb.Clear();
                }
                current = family;
                sb.Append(piece);
            }
            if (current is not null && sb.Length > 0) runs.Add(new FontRun(sb.ToString(), current));
            return runs;
        }

        /// <summary>
        /// Removes the characters that are meant to draw nothing (zero-width space and joiners,
        /// BOM, variation selectors). An embedded subset that lacks one draws its .notdef glyph,
        /// which in most faces is a visible box, so they are dropped from a drawn appearance. The
        /// field's /V keeps them.
        /// </summary>
        internal static string StripInvisible(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
                if (!IsZeroWidth(c)) sb.Append(c);
            return sb.Length == text.Length ? text : sb.ToString();
        }

        // Whitespace and zero-width controls never render a box, so they must not veto a font.
        internal static bool IsIgnorable(int cp) =>
            cp is 0x09 or 0x0A or 0x0D or 0x20 or 0xA0 || IsZeroWidth(cp);

        private static bool IsZeroWidth(int cp) =>
            cp is 0x200B or 0x200C or 0x200D or 0xFEFF or (>= 0xFE00 and <= 0xFE0F);

        private static bool IsMark(int cp)
        {
            if (cp is < 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF)) return false;
            var cat = CharUnicodeInfo.GetUnicodeCategory(cp);
            return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark
                       or UnicodeCategory.SpacingCombiningMark;
        }

        // ── Chain selection ───────────────────────────────────────────────────────────────────

        // Picked from the first character that NEEDS help, not the first character overall: a line
        // starting "Re: " and continuing in Japanese is Japanese text, not Latin text. Symbols and
        // emoji match no script here and are skipped, so "✓ 日本語" is still Chinese/Japanese text;
        // they are reached through ChainSymbol in Candidates.
        internal static string[] ChainFor(string text)
        {
            foreach (int cp in CodePoints(text))
            {
                if (cp < 0x0370) continue;                                        // Latin / punctuation
                if (cp is >= 0x3040 and <= 0x30FF) return ChainJapanese;           // kana - unambiguous
                if (cp is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF) return ChainKorean;
                if (cp is >= 0x0E00 and <= 0x0E7F) return ChainThai;
                if (cp is >= 0x0590 and <= 0x08FF) return ChainArabic;             // Hebrew + Arabic
                if (cp is >= 0x0900 and <= 0x0DFF) return ChainIndic;              // Devanagari..Sinhala
                if (cp is >= 0x3400 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF)
                {
                    // Han with no kana anywhere: Chinese. Traditional-only blocks are rare, so
                    // prefer Simplified and let the Traditional chain cover what it misses.
                    foreach (int c2 in CodePoints(text))
                        if (c2 is >= 0x3040 and <= 0x30FF) return ChainJapanese;
                    return HasTraditionalMarker(text) ? ChainTraditional : ChainSimplified;
                }
            }
            return ChainDefault;
        }

        // Bopomofo is Traditional-only, so it settles the Simplified/Traditional question when a
        // document carries it. Otherwise the Simplified chain leads and Traditional follows.
        private static bool HasTraditionalMarker(string text)
        {
            foreach (int cp in CodePoints(text))
                if (cp is >= 0x3100 and <= 0x312F) return true;
            return false;
        }

        internal static IEnumerable<int> CodePoints(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    yield return char.ConvertToUtf32(s[i], s[i + 1]);
                    i++;
                }
                else yield return s[i];
            }
        }
    }
}
