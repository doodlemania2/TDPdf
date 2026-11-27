using TDPdf.Services;

/// <summary>Pins <see cref="PageRangeSpec"/>: the typed page list and the Extract Pages plan.</summary>
internal static class PageRangeSpecTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        static string J(IEnumerable<int> xs) => string.Join(",", xs);

        bool ok = PageRangeSpec.TryParse("1-3,7", 10, out var p, out var err);
        Check("page range: 1-3,7", ok && J(p) == "0,1,2,6", $"{ok} {J(p)} {err}");
        ok = PageRangeSpec.TryParse(" 3 ; 1-2  2-4 ", 10, out p, out err);
        Check("page range: mixed separators, overlap, order", ok && J(p) == "0,1,2,3", $"{ok} {J(p)}");
        ok = PageRangeSpec.TryParse("5-2", 10, out p, out err);
        Check("page range: reversed range reads both ways", ok && J(p) == "1,2,3,4", $"{J(p)}");
        ok = PageRangeSpec.TryParse("2–4", 10, out p, out err);
        Check("page range: en dash", ok && J(p) == "1,2,3", $"{J(p)}");
        ok = PageRangeSpec.TryParse("1,10", 10, out p, out err);
        Check("page range: last page is allowed", ok && J(p) == "0,9", "");

        foreach (var bad in new[] { "", "   ", "abc", "1-", "-3", "1--3", "0", "11", "1-11", "2.5", "1,,x", "+3", "99999999999" })
        {
            bool r = PageRangeSpec.TryParse(bad, 10, out var q, out var e);
            Check($"page range: rejects \"{bad}\"", !r && q.Count == 0 && e.Length > 0, $"{r} {e}");
        }
        Check("page range: no pages is an error", !PageRangeSpec.TryParse("1", 0, out _, out _), "");

        // Lenient reading (Print, Export Pages as Images): pins what PrintPreviewWindow.ParseRange did.
        static string L(string? t, int n) => J(PageRangeSpec.ParseLenient(t, n));
        Check("lenient: blank is every page", L("", 4) == "0,1,2,3" && L("   ", 4) == "0,1,2,3" && L(null, 4) == "0,1,2,3", L("", 4));
        Check("lenient: list and range", L("1-3,7", 10) == "0,1,2,6", L("1-3,7", 10));
        Check("lenient: reversed, overlapping, unsorted", L("5-2, 3, 1", 10) == "0,1,2,3,4", L("5-2, 3, 1", 10));
        Check("lenient: numbers past the end are dropped, a range is clamped", L("2,9,3-99", 5) == "1,2,3,4", L("2,9,3-99", 5));
        Check("lenient: junk parts are ignored, good ones kept", L("x,2,1-,3-4-5,,-2", 6) == "1", L("x,2,1-,3-4-5,,-2", 6));
        Check("lenient: nothing matching is EMPTY, never every page", L("99", 5) == "" && L("abc", 5) == "" && L("0", 5) == "", L("99", 5));
        Check("lenient: a huge range ends and is clamped", L("1-2147483647", 3) == "0,1,2" && L("-5-2147483647", 3) == "", L("1-2147483647", 3));
        Check("lenient: semicolons and spaces are not separators", L("1;2", 5) == "" && L("1 2", 5) == "", L("1;2", 5));
        Check("lenient: spaces around the numbers are fine", L(" 2 - 3 , 5 ", 5) == "1,2,4", L(" 2 - 3 , 5 ", 5));

        // The command line keeps its own wording over the strict parse.
        foreach (var (spec, n, want) in new[]
        {
            ("1-3,x", 10, "Bad page number: \"x\""),
            ("1-,5", 10, "Bad page range: \"1-\""),
            ("9-12", 8, "Pages 9-12 out of range - the document has 8 pages"),
            ("", 8, "Empty page range"),
            (",", 8, "Empty page range"),
        })
        {
            bool r = PageRangeSpec.TryParseForCli(spec, n, out var q, out var e);
            Check($"page range (CLI): \"{spec}\" says \"{want}\"", !r && q.Count == 0 && e == want, e);
        }
        bool cliOk = PageRangeSpec.TryParseForCli("1-3,5,9-12", 12, out var cp, out _);
        Check("page range (CLI): the documented example parses", cliOk && J(cp) == "0,1,2,4,8,9,10,11", J(cp));

        Check("describe: runs", PageRangeSpec.Describe(new[] { 0, 1, 2, 6 }) == "1-3,7", PageRangeSpec.Describe(new[] { 0, 1, 2, 6 }));
        Check("describe: single and unsorted duplicates", PageRangeSpec.Describe(new[] { 4, 4, 0 }) == "1,5", "");

        var one = PageRangeSpec.Plan(new[] { 0, 1, 2, 6 }, ExtractMode.OneFile, 0, "Report");
        Check("plan: one file", one.Count == 1 && one[0].FileName == "Report p1-3,7.pdf" && J(one[0].Pages) == "0,1,2,6", one[0].FileName);
        var each = PageRangeSpec.Plan(new[] { 2, 0 }, ExtractMode.OnePerPage, 0, "Report");
        Check("plan: one per page", each.Count == 2 && each[0].FileName == "Report p1.pdf" && each[1].FileName == "Report p3.pdf", string.Join("|", each.Select(x => x.FileName)));
        var every = PageRangeSpec.Plan(Enumerable.Range(0, 7).ToList(), ExtractMode.EveryN, 3, "Report");
        Check("plan: every 3 of 7 pages", every.Count == 3 && J(every[2].Pages) == "6" && every[1].FileName == "Report part 2.pdf" && J(every[0].Pages) == "0,1,2",
              string.Join("|", every.Select(x => x.FileName)));
        var every0 = PageRangeSpec.Plan(new[] { 0, 1 }, ExtractMode.EveryN, 0, "R");
        Check("plan: every 0 behaves as every 1", every0.Count == 2, $"{every0.Count}");
        var many = PageRangeSpec.Plan(Enumerable.Range(0, 100).Where(i => i % 2 == 0).ToList(), ExtractMode.OneFile, 0, "R");
        Check("plan: a scattered pick gets a short name", many[0].FileName == "R 50 pages.pdf", many[0].FileName);
        Check("plan: nothing in, nothing out", PageRangeSpec.Plan(new int[0], ExtractMode.OneFile, 0, "R").Count == 0, "");
        Check("plan: file names are distinct",
              every.Select(x => x.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == every.Count, "");
        Check("safe name: strips path characters and never empties",
              PageRangeSpec.SafeName("a/b:c") == "abc" && PageRangeSpec.SafeName("///") == "Document" && PageRangeSpec.SafeName(null) == "Document", "");
        var taken = PageRangeSpec.Taken(every.Select(x => x.FileName), n => n.EndsWith("2.pdf"));
        Check("taken: names an existing file", taken.Count == 1 && taken[0] == "Report part 2.pdf", string.Join("|", taken));
    }
}
