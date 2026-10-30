using TDPdf.Services;

/// <summary>
/// Pins the font fallback a form value's appearance uses: <see cref="FontFallback"/>.
/// </summary>
/// <remarks>
/// A form value outside WinAnsi is drawn in an embedded font. One family used to be picked for the
/// whole value from per-script chains that held no symbol face, so a check mark or a BMP emoji
/// saved as an empty box. The properties that must hold, against a made-up font set so the result
/// does not depend on what this machine has installed:
///
///   * <b>symbol faces are candidates.</b> "Done ✓" lands on Segoe UI Symbol when it covers it all.
///   * <b>a value no one family covers is cut into runs</b>, each in a family that covers it, and
///     the runs put back together are exactly the input.
///   * <b>marks, joiners and spaces never start a run</b>, so a variation selector stays on its emoji.
///   * <b>a second script finds its own chain</b> even when the value's chain is another script's.
///   * <b>nothing covered</b> stays in the caller's family, as before.
/// </remarks>
internal static class FontRuns
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("── Form value font fallback and per-family runs ───────────────");

        bool Ascii(int cp) => cp is >= 0x20 and < 0x7F;
        var fonts = new Dictionary<string, Func<int, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Segoe UI"]        = cp => cp < 0x0250,
            ["Segoe UI Symbol"] = cp => Ascii(cp) || cp is 0x2713 or 0x2605 or 0x20DD,
            ["Segoe UI Emoji"]  = cp => cp is 0x2764 or 0x263A or (>= 0x30 and <= 0x39),
            ["Yu Gothic UI"]    = cp => Ascii(cp) || cp is >= 0x3040 and <= 0x30FF or >= 0x4E00 and <= 0x9FFF,
            ["Nirmala UI"]      = cp => Ascii(cp) || cp is >= 0x0980 and <= 0x09FF,
        };
        bool Covers(string f, int cp) => fonts.TryGetValue(f, out var c) && c(cp);
        bool CoversAll(string f, string text)
        {
            foreach (int cp in FontFallback.CodePoints(text))
                if (!FontFallback.IsIgnorable(cp) && !Covers(f, cp)) return false;
            return true;
        }
        string Show(List<FontRun> runs) => string.Join(" | ", runs.Select(r => $"[{r.Text}]{r.Family}"));

        Check("a value the default face covers stays in it",
              FontFallback.PickFamily("Segoe UI", "Plain text", CoversAll) == "Segoe UI", "");
        string pick = FontFallback.PickFamily("Segoe UI", "Done ✓", CoversAll);
        Check("a check mark picks the symbol face when it covers the whole value", pick == "Segoe UI Symbol", pick);
        pick = FontFallback.PickFamily("Segoe UI", "Love ❤", CoversAll);
        Check("nothing covers the whole value: the caller's family, as before", pick == "Segoe UI", pick);

        var cands = FontFallback.Candidates("ひらがな");
        Check("candidates: the script chain leads, then the symbol faces",
              cands[0] == "Yu Gothic UI" && cands.IndexOf("Segoe UI Symbol") > cands.IndexOf("Yu Mincho")
              && cands.Contains("Segoe UI Emoji"), string.Join(",", cands));
        Check("candidates carry no duplicates",
              cands.Count == cands.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "");

        string love = "Love ❤️ you";
        var runs = FontFallback.SplitRuns("Segoe UI", love, Covers);
        Check("text plus emoji splits into three runs, each in a covering face",
              runs.Count == 3 && runs[0].Family == "Segoe UI" && runs[1].Family == "Segoe UI Emoji"
              && runs[2].Family == "Segoe UI", Show(runs));
        Check("the variation selector and the space ride with the emoji",
              runs.Count == 3 && runs[1].Text == "❤️ ", Show(runs));
        Check("runs rejoin to exactly the input", string.Concat(runs.Select(r => r.Text)) == love, "");

        runs = FontFallback.SplitRuns("Segoe UI", "a✓⃝", Covers);
        Check("an enclosing mark stays on its base character",
              runs.Count == 2 && runs[1].Text == "✓⃝" && runs[1].Family == "Segoe UI Symbol", Show(runs));

        runs = FontFallback.SplitRuns("Segoe UI", "ひらがな বাংলা", Covers);
        Check("a second script finds its own face",
              runs.Count == 2 && runs[0].Family == "Yu Gothic UI" && runs[1].Family == "Nirmala UI"
              && runs[0].Text == "ひらがな " && runs[1].Text == "বাংলা", Show(runs));

        runs = FontFallback.SplitRuns("Segoe UI", "✓ ok ★", Covers);
        Check("the current face is kept while it covers (no switch back for every space)",
              runs.Count == 3 && runs[0].Family == "Segoe UI Symbol" && runs[1].Text == "ok "
              && runs[2].Family == "Segoe UI Symbol", Show(runs));

        runs = FontFallback.SplitRuns("Segoe UI", "x一ก", (f, cp) => f == "Segoe UI" && cp < 0x80);
        Check("a character nothing covers stays in the caller's family",
              runs.Count == 1 && runs[0].Family == "Segoe UI", Show(runs));
        Check("an empty value has no runs", FontFallback.SplitRuns("Segoe UI", "", Covers).Count == 0, "");

        Check("variation selectors and joiners are dropped from a drawn value",
              FontFallback.StripInvisible("❤️ a‍b​") == "❤ ab", "");
        const string plain = "plain ✓";
        Check("a value with nothing invisible is returned as is",
              ReferenceEquals(FontFallback.StripInvisible(plain), plain), "");
    }
}
