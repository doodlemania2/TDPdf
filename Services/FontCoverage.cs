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
    // SCOPE: PickFamily picks the best single family for a whole run of text, which
    // is what real documents need - a Japanese face covers Latin too, so a line
    // mixing English and Japanese still lands on one font. Text no ONE family can
    // carry (say Japanese and Bengali, or text plus a symbol only Segoe UI Symbol
    // has) is split into per-family runs by SplitRuns, which form field appearances
    // use. Annotation burn-in still takes the single family and the commit-time
    // warning in MainWindow covers its uncovered tail.
    // ============================================================
    internal static class FontCoverage
    {
        /// <summary>
        /// The family to draw <paramref name="text"/> with: the caller's own choice whenever it
        /// covers everything, otherwise the first family in the script's chain (then the symbol
        /// faces, then the defaults) that does. Falls back to the caller's choice when nothing
        /// covers it, so behavior is never WORSE than before - the caller warns in that case (see
        /// MainWindow.WarnIfGlyphsWillBeLost). Chains live in <see cref="FontFallback"/>.
        /// </summary>
        internal static string PickFamily(string preferred, string? text) =>
            FontFallback.PickFamily(preferred, text, Covers);

        /// <summary>True when <paramref name="family"/> is installed and covers all of <paramref name="text"/>.</summary>
        internal static bool CoversAll(string family, string? text) =>
            string.IsNullOrEmpty(text) || Covers(family, text);

        /// <summary>
        /// Per-family runs for a value no single family covers (see <see cref="FontFallback.SplitRuns"/>).
        /// </summary>
        internal static List<FontRun> SplitRuns(string preferred, string text) =>
            FontFallback.SplitRuns(preferred, text, (family, cp) => !IsAstral(cp) && CoverageFor(family)?.Covers(cp) == true);

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
                if ((coverage.Covers(cp) && !IsAstral(cp)) || IsIgnorable(cp)) continue;
                foreach (char c in char.ConvertFromUtf32(cp))
                    if (!bad.Contains(c)) bad.Add(c);
                if (bad.Count >= 12) break;   // a sample is enough; the box could be a whole page
            }
            return new string([.. bad]);
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
                if (!IsAstral(cp) && !coverage.Covers(cp) && !IsIgnorable(cp)) return false;
            return true;
        }

        private static bool IsIgnorable(int cp) => FontFallback.IsIgnorable(cp);

        // PdfSharpCore maps glyphs one UTF-16 unit at a time through the font's BMP cmap, so nothing
        // past U+FFFF (most emoji, CJK Ext B) draws in any face. Such a character must not steer the
        // family choice, since no family can help, and it always counts as uncovered for the warning.
        private static bool IsAstral(int cp) => cp > 0xFFFF;

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

        private static IEnumerable<int> CodePoints(string s) => FontFallback.CodePoints(s);
    }
}
