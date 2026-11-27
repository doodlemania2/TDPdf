using TDPdf.Services;

internal static class OpenBatchTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        Check("open batch: no failures, no message", OpenBatchReport.Summarize(3, Array.Empty<(string, string)>()) is null, "");

        string? one = OpenBatchReport.Summarize(1, new[] { ("a.pdf", "The file is damaged.") });
        Check("open batch: a single file is named with its reason",
              one == "That file could not be opened:\n  a.pdf: The file is damaged.", one ?? "null");

        string? some = OpenBatchReport.Summarize(5, new[] { ("a.pdf", "bad"), ("b.pdf", "worse\nsecond line") });
        Check("open batch: counts failures out of the total and says the rest are open",
              some is not null && some.StartsWith("2 of 5 files could not be opened. The others are open."), some ?? "null");
        Check("open batch: only the first line of a reason is shown", some is not null && !some.Contains("second line") && some.Contains("b.pdf: worse"), some ?? "null");

        var many = Enumerable.Range(1, 12).Select(i => ($"f{i}.pdf", "x")).ToList();
        string? capped = OpenBatchReport.Summarize(20, many);
        Check("open batch: long lists are capped", capped is not null && capped.Contains("f8.pdf") && !capped.Contains("f9.pdf") && capped.EndsWith("and 4 more"), capped ?? "null");

        string? blank = OpenBatchReport.Summarize(2, new[] { ("a.pdf", "  ") });
        Check("open batch: a blank reason lists just the name", blank is not null && blank.EndsWith("\n  a.pdf"), blank ?? "null");

        string? longReason = OpenBatchReport.Summarize(2, new[] { ("a.pdf", new string('x', 500)) });
        Check("open batch: an enormous reason is trimmed", longReason is not null && longReason.Length < 300, $"{longReason?.Length}");
    }
}
