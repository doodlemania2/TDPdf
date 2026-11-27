using TDPdf.Services;

/// <summary>
/// The print window's pure planning (sheet grouping and reversal, scale modes, saved two-sided choice)
/// plus a source-text pin on the printer Properties-dialog conversions.
/// </summary>
/// <remarks>
/// The pin exists because 2.1.0.0 switched DEVMODE &lt;-&gt; PrintTicket to DocumentScope, which drops every
/// Job* feature the driver dialog returns (stapling, punch, output bin, secure-print PIN...), and 2.1.1.1
/// had to put JobScope back. PrintPreviewWindow is WPF and cannot run here, so the test reads its source.
/// </remarks>
internal static class PrintPlan
{
    private static string J(IEnumerable<List<int>> sheets) => string.Join(" ", sheets.Select(s => "[" + string.Join(",", s) + "]"));

    private static string? FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nPrint: sheet plan, scale, saved duplex, Properties-dialog scope pin");

        var pages = new[] { 0, 1, 2, 3, 4 };
        Check("1-up in order", J(PrintSheetPlan.Sheets(pages, 1, false)) == "[0] [1] [2] [3] [4]", "");
        Check("1-up reversed", J(PrintSheetPlan.Sheets(pages, 1, true)) == "[4] [3] [2] [1] [0]", "");
        string two = J(PrintSheetPlan.Sheets(pages, 2, false));
        Check("2-up groups pairs, short last sheet", two == "[0,1] [2,3] [4]", two);
        string twoRev = J(PrintSheetPlan.Sheets(pages, 2, true));
        Check("2-up reverse flips SHEETS, not page indices", twoRev == "[4] [2,3] [0,1]", twoRev);
        var odd = new[] { 0, 2, 4, 6, 8 };
        Check("odd-only reversed walks 9,7,5,3,1", J(PrintSheetPlan.Sheets(odd, 1, true)) == "[8] [6] [4] [2] [0]", "");
        Check("empty selection has no sheets", PrintSheetPlan.Sheets(Array.Empty<int>(), 4, true).Count == 0, "");
        Check("nUp below 1 is treated as 1", PrintSheetPlan.Sheets(pages, 0, false).Count == 5, "");

        // Fit to page turns a page 90 degrees only when page and sheet orientations differ.
        const int Fit = PrintSheetPlan.ScaleFit;
        Check("turn: landscape page on portrait sheet", PrintSheetPlan.TurnToFit(Fit, 792, 612, 816, 1056), "");
        Check("turn: portrait page on landscape sheet", PrintSheetPlan.TurnToFit(Fit, 612, 792, 1056, 816), "");
        Check("no turn: same orientation (portrait/portrait)", !PrintSheetPlan.TurnToFit(Fit, 612, 792, 816, 1056), "");
        Check("no turn: same orientation (landscape/landscape)", !PrintSheetPlan.TurnToFit(Fit, 792, 612, 1056, 816), "");
        Check("no turn: square page", !PrintSheetPlan.TurnToFit(Fit, 600, 600, 816, 1056), "");
        Check("no turn: square sheet", !PrintSheetPlan.TurnToFit(Fit, 792, 612, 900, 900), "");
        Check("no turn: Actual size", !PrintSheetPlan.TurnToFit(PrintSheetPlan.ScaleActual, 792, 612, 816, 1056), "");
        Check("no turn: Shrink oversized", !PrintSheetPlan.TurnToFit(PrintSheetPlan.ScaleShrink, 792, 612, 816, 1056), "");
        Check("no turn: Custom scale", !PrintSheetPlan.TurnToFit(PrintSheetPlan.ScaleCustom, 792, 612, 816, 1056), "");
        Check("no turn: unusable sizes", !PrintSheetPlan.TurnToFit(Fit, 0, 612, 816, 1056), "");
        Check("sheet count matches ceil(n/nUp)", PrintSheetPlan.Sheets(pages, 4, false).Count == 2, "");

        // Scale: fit 0.5 (page bigger than sheet), actual 1.0.
        Check("fit mode uses the fit factor", PrintSheetPlan.Factor(PrintSheetPlan.ScaleFit, 0.5, 1.0, 100) == 0.5, "");
        Check("actual size is the page's real size", PrintSheetPlan.Factor(PrintSheetPlan.ScaleActual, 0.5, 1.0, 100) == 1.0, "");
        Check("shrink oversized shrinks a big page", PrintSheetPlan.Factor(PrintSheetPlan.ScaleShrink, 0.5, 1.0, 100) == 0.5, "");
        Check("shrink oversized leaves a small page at real size", PrintSheetPlan.Factor(PrintSheetPlan.ScaleShrink, 2.0, 1.0, 100) == 1.0, "");
        Check("custom is a percentage of real size", PrintSheetPlan.Factor(PrintSheetPlan.ScaleCustom, 0.5, 1.0, 150) == 1.5, "");
        Check("custom is clamped to 25-400", PrintSheetPlan.Factor(PrintSheetPlan.ScaleCustom, 0.5, 1.0, 1000) == 4.0
              && PrintSheetPlan.Factor(PrintSheetPlan.ScaleCustom, 0.5, 1.0, 1) == 0.25, "");
        Check("unknown page size falls back to fit", PrintSheetPlan.Factor(PrintSheetPlan.ScaleActual, 0.5, 0, 100) == 0.5, "");
        Check("fit-limited: fit and a shrunk page", PrintSheetPlan.IsFitLimited(PrintSheetPlan.ScaleFit, 0.5, 1.0)
              && PrintSheetPlan.IsFitLimited(PrintSheetPlan.ScaleShrink, 0.5, 1.0), "");
        Check("not fit-limited: actual, custom, or an unshrunk page",
              !PrintSheetPlan.IsFitLimited(PrintSheetPlan.ScaleActual, 0.5, 1.0)
              && !PrintSheetPlan.IsFitLimited(PrintSheetPlan.ScaleCustom, 0.5, 1.0)
              && !PrintSheetPlan.IsFitLimited(PrintSheetPlan.ScaleShrink, 2.0, 1.0), "");

        // Saved two-sided choice.
        Check("old bool true reads as long edge", PrintSheetPlan.ResolveDuplex("", true) == "TwoSidedLongEdge", "");
        Check("old bool false reads as one-sided", PrintSheetPlan.ResolveDuplex(null, false) == "OneSided", "");
        Check("new setting wins over the old bool",
              PrintSheetPlan.ResolveDuplex("TwoSidedShortEdge", false) == "TwoSidedShortEdge"
              && PrintSheetPlan.ResolveDuplex("OneSided", true) == "OneSided", "");
        Check("garbage in the new setting falls back to the bool", PrintSheetPlan.ResolveDuplex("Sideways", true) == "TwoSidedLongEdge", "");
        Check("combo index round-trips",
              Enumerable.Range(0, 3).All(i => PrintSheetPlan.DuplexComboIndex(PrintSheetPlan.DuplexNameFromComboIndex(i)) == i), "");
        Check("combo order is one-sided, long, short",
              PrintSheetPlan.DuplexNameFromComboIndex(1) == "TwoSidedLongEdge"
              && PrintSheetPlan.DuplexNameFromComboIndex(2) == "TwoSidedShortEdge", "");

        // Source pin: the driver-dialog conversions must stay at JobScope.
        string? path = FindRepoFile("Services/PrintPreviewWindow.cs");
        if (path is null) { Check("Services/PrintPreviewWindow.cs found for the scope pin", false, "not found"); return; }
        string src = File.ReadAllText(path);
        Check("no PrintTicketScope.DocumentScope anywhere", !src.Contains("PrintTicketScope.DocumentScope"), "");
        foreach (var call in new[] { "ConvertDevModeToPrintTicket(", "ConvertPrintTicketToDevMode(" })
        {
            var sites = new List<string>();
            for (int i = src.IndexOf(call, StringComparison.Ordinal); i >= 0; i = src.IndexOf(call, i + 1, StringComparison.Ordinal))
            {
                int end = src.IndexOf(';', i);
                sites.Add(src[i..end]);
            }
            Check($"{call.TrimEnd('(')} is called", sites.Count >= 1, $"{sites.Count} call(s)");
            Check($"every {call.TrimEnd('(')} call uses PrintTicketScope.JobScope",
                  sites.All(c => c.Contains("PrintTicketScope.JobScope")), string.Join(" | ", sites));
        }
    }
}
