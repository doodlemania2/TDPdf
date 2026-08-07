namespace TDPdf.Services
{
    // ============================================================
    // Glyph coverage and the per-script fallback chain (upstream KillerPDF #168).
    //
    // The editor is WPF, which falls back per character across every installed
    // font, so anything typed looks right on screen. PdfSharpCore resolves ONE
    // face and emits .notdef (an empty box) for anything that face lacks. So
    // before drawing, ask which family can actually carry this text.
    //
    // Coverage is read from the font's own 'cmap' rather than from a helper
    // library: the bytes are already in hand (TdpFontResolver hands back a
    // standalone face, collections included), and parsing the table is
    // deterministic across font-library versions.
    //
    // SCOPE: this picks the best single family for a whole run of text, which is
    // what real documents need - a Japanese face covers Latin too, so a line
    // mixing English and Japanese still lands on one font. Text no ONE family can
    // carry (say Japanese and Bengali in the same box) falls back to the caller's
    // own font for the uncovered part; that tail is what the commit-time warning
    // in MainWindow is for. True per-character run splitting would mean
    // re-implementing line breaking, which is not worth it for that tail.
    // ============================================================
    internal static class FontCoverage
    {
        // Per-script preference, first match wins. Sans-first throughout, mirroring what Windows
        // itself falls back to, so a saved file looks like the editor did. Every entry is a family
        // that ships with Windows; missing ones are skipped harmlessly at lookup time.
        private static readonly string[] ChainJapanese     = ["Yu Gothic UI", "Yu Gothic", "Meiryo", "MS Gothic", "Yu Mincho"];
        private static readonly string[] ChainSimplified   = ["Microsoft YaHei", "DengXian", "SimSun"];
        private static readonly string[] ChainTraditional  = ["Microsoft JhengHei", "MingLiU", "PMingLiU"];
        private static readonly string[] ChainKorean       = ["Malgun Gothic", "Gulim"];
        private static readonly string[] ChainIndic        = ["Nirmala UI"];
        private static readonly string[] ChainThai         = ["Leelawadee UI", "Tahoma"];
        private static readonly string[] ChainArabic       = ["Segoe UI", "Tahoma", "Traditional Arabic"];
        private static readonly string[] ChainDefault      = ["Segoe UI", "Arial", "Tahoma"];

        /// <summary>
        /// The family to draw <paramref name="text"/> with: the caller's own choice whenever it
        /// covers everything, otherwise the first family in the script's chain that does. Falls back
        /// to the caller's choice when nothing covers it, so behavior is never WORSE than before -
        /// the caller warns in that case (see MainWindow.WarnIfGlyphsWillBeLost).
        /// </summary>
        internal static string PickFamily(string preferred, string? text)
        {
            if (string.IsNullOrEmpty(text)) return preferred;
            if (Covers(preferred, text)) return preferred;

            foreach (string family in ChainFor(text))
            {
                if (string.Equals(family, preferred, StringComparison.OrdinalIgnoreCase)) continue;
                if (Covers(family, text)) return family;
            }
            return preferred;
        }

        /// <summary>
        /// The characters nothing installed can draw - what the warning shows the user. Empty when
        /// the text is fully covered, and also when <paramref name="family"/> itself is not installed
        /// (coverage is then unknown rather than absent, so there is nothing honest to list).
        /// </summary>
        internal static string UncoveredChars(string family, string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var coverage = CoverageFor(family);
            if (coverage is null) return "";
            var bad = new List<char>();
            foreach (int cp in CodePoints(text))
            {
                if (coverage.Covers(cp) || IsIgnorable(cp)) continue;
                foreach (char c in char.ConvertFromUtf32(cp))
                    if (!bad.Contains(c)) bad.Add(c);
                if (bad.Count >= 12) break;   // a sample is enough; the box could be a whole page
            }
            return new string([.. bad]);
        }

        // ── Chain selection ───────────────────────────────────────────────────────────────────

        // Picked from the first character that NEEDS help, not the first character overall: a line
        // starting "Re: " and continuing in Japanese is Japanese text, not Latin text.
        private static string[] ChainFor(string text)
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

        // ── Coverage ──────────────────────────────────────────────────────────────────────────

        // family -> parsed cmap (null = not installed / unreadable / unfamiliar). Cached because a
        // miss costs a full read of the font file, and a CJK collection is tens of megabytes.
        private static readonly Dictionary<string, CmapCoverage?> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new();

        private static bool Covers(string family, string text)
        {
            var coverage = CoverageFor(family);
            if (coverage is null) return false;   // not installed / unreadable: cannot promise anything
            foreach (int cp in CodePoints(text))
                if (!coverage.Covers(cp) && !IsIgnorable(cp)) return false;
            return true;
        }

        // Whitespace and zero-width controls never render a box, so they must not veto a font.
        private static bool IsIgnorable(int cp) =>
            cp is 0x09 or 0x0A or 0x0D or 0x20 or 0xA0 or 0x200B or 0x200C or 0x200D or 0xFEFF;

        private static CmapCoverage? CoverageFor(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return null;
            lock (Gate)
            {
                if (Cache.TryGetValue(family, out var hit)) return hit;
                CmapCoverage? coverage = null;
                try
                {
                    byte[]? bytes = TdpFontResolver.RegularFaceBytes(family);
                    if (bytes is not null) coverage = CmapCoverage.Parse(bytes);
                }
                catch { coverage = null; }   // a font problem must never reach the save path
                Cache[family] = coverage;
                return coverage;
            }
        }

        private static IEnumerable<int> CodePoints(string s)
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
