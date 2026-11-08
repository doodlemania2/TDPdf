using TDPdf.Services;
using static TDPdf.Services.PdfTextSearch;

/// <summary>
/// Search normalisation (upstream 1.9.1): ligatures, invisible characters and words hyphenated
/// across a line break stay searchable, a literal hyphen is never lost, and the highlight boxes
/// still sit on the glyphs the match came from. Synthetic words, so no PDF is needed.
/// </summary>
internal static class SearchNormalise
{
    private const double W = 10, H = 12;

    // A word whose letters are the given strings (a ligature is one string), starting at x on a baseline y.
    private static SearchWord Word(double x, double y, params string[] letters)
    {
        var l = new List<SearchLetter>();
        foreach (var v in letters)
        {
            l.Add(new SearchLetter(v, x, y, x + W, y + H, true));
            x += W;
        }
        return new SearchWord(l);
    }

    private static SearchWord Word(double x, double y, string text) =>
        Word(x, y, text.Select(c => c.ToString()).ToArray());

    private static int Count(IReadOnlyList<SearchWord> w, string q) => FindMatches(w, q).Count;

    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nSearch normalisation (ligatures, invisibles, line-break hyphens)");

        // Ligature glyph: the page has "o" + U+FB01 + "ce" (one glyph for "fi").
        var lig = new List<SearchWord> { Word(0, 100, "o", "ﬁ", "c", "e") };
        Check("ligature: \"office\" is not it, \"ofice\" is not either, \"ofi\" is found", Count(lig, "ofi") == 1, "");
        Check("ligature: \"fi\" finds the ligature glyph", Count(lig, "fi") == 1, "");
        Check("ligature: query typed with the ligature code point finds it too", Count(lig, "ﬁce") == 1, "");
        var box = FindMatches(lig, "fi")[0].Boxes[0];
        Check("ligature: the box is the ligature glyph's box (x 10..20), not the neighbours'",
              box.Left > 8 && box.Right < 22, $"{box.Left:F1}..{box.Right:F1}");
        var ffi = new List<SearchWord> { Word(0, 100, "e", "ﬃ", "e", "n", "t") };
        Check("ligature: U+FB03 reads as ffi (\"effient\")", Count(ffi, "effient") == 1 && Count(ffi, "ffi") == 1, "");
        var ffl = new List<SearchWord> { Word(0, 100, "ﬄ") };
        var st = new List<SearchWord> { Word(0, 100, "ﬆ") };
        Check("ligature: FB04 is ffl, FB06 is st",
              Count(ffl, "ffl") == 1 && Count(st, "st") == 1 && Count(st, "ffl") == 0, "");

        // Invisible characters inside a word.
        var soft = new List<SearchWord> { Word(0, 100, "co­op") };
        Check("soft hyphen inside a word is ignored (\"coop\")", Count(soft, "coop") == 1, "");
        var zw = new List<SearchWord> { Word(0, 100, "a​b‌c‍d﻿e") };
        Check("zero-width characters and BOM are ignored (\"abcde\")", Count(zw, "abcde") == 1, "");
        Check("a query containing an invisible character still matches", Count(zw, "ab​cde") == 1, "");
        var skip = new List<SearchWord> { Word(0, 100, "a​b") };
        var skipBox = FindMatches(skip, "ab")[0].Boxes[0];
        Check("invisible characters keep the box on the visible letters", skipBox.Left < 1 && skipBox.Right > 29,
              $"{skipBox.Left:F1}..{skipBox.Right:F1}");

        // Hyphenation across a line break.
        var wrap = new List<SearchWord> { Word(0, 100, "hyphen-"), Word(0, 80, "ated") };
        Check("line-break hyphen: \"hyphenated\" is found across the break", Count(wrap, "hyphenated") == 1, "");
        Check("line-break hyphen: the literal \"hyphen-ated\" is found too", Count(wrap, "hyphen-ated") == 1, "");
        Check("line-break hyphen: the plain reading \"hyphen- ated\" still works", Count(wrap, "hyphen- ated") == 1, "");
        var wrapBoxes = FindMatches(wrap, "hyphenated")[0].Boxes;
        Check("line-break hyphen: one box per line, the hyphen glyph included",
              wrapBoxes.Count == 2 && wrapBoxes[0].Right > 59 && wrapBoxes[0].Right < 70 && wrapBoxes[1].Right < 42,
              string.Join(" ", wrapBoxes.Select(b => $"{b.Left:F0}..{b.Right:F0}@{b.Bottom:F0}")));
        var wrapSoft = new List<SearchWord> { Word(0, 100, "hyphen­"), Word(0, 80, "ated") };
        Check("line-break soft hyphen joins too", Count(wrapSoft, "hyphenated") == 1, "");

        // Literal hyphens survive.
        var well = new List<SearchWord> { Word(0, 100, "well-known"), Word(120, 100, "facts") };
        Check("literal hyphen: \"well-known\" on one line is found", Count(well, "well-known") == 1, "");
        Check("literal hyphen: \"wellknown\" is not", Count(well, "wellknown") == 0, "");
        var sameLine = new List<SearchWord> { Word(0, 100, "pre-"), Word(50, 100, "and") };
        Check("a hyphen followed by a word on the SAME line is not joined", Count(sameLine, "preand") == 0
              && Count(sameLine, "pre- and") == 1, "");
        var wrapLiteral = new List<SearchWord> { Word(0, 100, "well-"), Word(0, 80, "known") };
        Check("literal hyphen split by a line break: \"well-known\" and \"wellknown\" both found",
              Count(wrapLiteral, "well-known") == 1 && Count(wrapLiteral, "wellknown") == 1, "");
        var dash = new List<SearchWord> { Word(0, 100, "a-"), Word(0, 80, "Bcd") };
        Check("a hyphen after a one-letter stem still joins, and an uppercase next word does too",
              Count(dash, "abcd") == 1, "");
        var number = new List<SearchWord> { Word(0, 100, "2024-"), Word(0, 80, "2025") };
        Check("a digit after the break joins only keeping the hyphen (\"2024-2025\" yes, \"20242025\" no)",
              Count(number, "20242025") == 0 && Count(number, "2024-2025") == 1, "");

        // Existing behaviour that must not move.
        var cs = new List<SearchWord> { Word(0, 100, "Jordan"), Word(80, 100, "Ellis") };
        Check("case-insensitive across words", Count(cs, "jordan ellis") == 1 && Count(cs, "JORDAN  ELLIS") == 1, "");
        var rep = new List<SearchWord> { Word(0, 100, "aaaa") };
        Check("non-overlapping counting is unchanged (\"aa\" in \"aaaa\" = 2)", Count(rep, "aa") == 2, "");
        var twice = new List<SearchWord> { Word(0, 100, "hyphen-"), Word(0, 80, "ated"), Word(0, 60, "hyphenated") };
        Check("the joined and the plain reading do not double-count (2 hits)", Count(twice, "hyphenated") == 2, "");
        Check("empty page and empty query are empty", Count(new List<SearchWord>(), "x") == 0 && Count(cs, "") == 0, "");
    }
}
