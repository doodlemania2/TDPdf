using System.IO;

namespace TDPdf.Services
{
    /// <summary>
    /// <c>TDPdf.exe --pdfua &lt;file|folder&gt; [more ...] [--json]</c> (#243): checks one or many files and prints
    /// the findings as text or as JSON that names the standard (PDF/UA-1 or PDF/UA-2) each file was checked against.
    /// </summary>
    /// <remarks>
    /// Exit codes: 0 every file passed TDPdf's machine checks, 3 at least one failed or was only partly checked,
    /// 1 at least one could not be read, 2 bad usage (nothing to check, or a path that is not there).
    /// </remarks>
    internal static class PdfUaCli
    {
        internal const int Passed = 0, Unreadable = 1, BadUsage = 2, NotPassed = 3;

        internal static int Run(IReadOnlyList<string> paths, bool json, TextWriter con)
        {
            if (paths.Count == 0)
            {
                con.WriteLine("Usage: TDPdf.exe --pdfua <file.pdf|folder> [more ...] [--json]");
                return BadUsage;
            }
            var files = new List<string>();
            foreach (var p in paths)
            {
                string full = Path.GetFullPath(p);
                if (Directory.Exists(full))
                    files.AddRange(Directory.EnumerateFiles(full, "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal));
                else if (File.Exists(full)) files.Add(full);
                else { con.WriteLine($"Not found: {full}"); return BadUsage; }
            }
            if (files.Count == 0) { con.WriteLine("No PDF files to check."); return BadUsage; }

            bool unreadable = false, notPassed = false;
            var items = new List<string>();
            foreach (var f in files)
            {
                PdfUaReport report;
                try { report = PdfUaValidator.Check(f); }
                catch (Exception ex)
                {
                    unreadable = true;
                    string msg = OneLine(ex.Message);
                    if (json) items.Add("{\"file\":" + PdfUaReport.Quote(f) + ",\"error\":" + PdfUaReport.Quote(msg) + "}");
                    else con.WriteLine($"{f}: could not be read ({msg})");
                    continue;
                }
                if (report.Verdict != PdfUaVerdict.PassedMachineChecks) notPassed = true;
                if (json) { items.Add(report.ToJson(f)); continue; }

                con.WriteLine($"{f}: {report.VerdictText}");
                foreach (var finding in report.Findings)
                {
                    string where = finding.PageIndex is { } pg ? $" (page {pg + 1})" : "";
                    con.WriteLine($"  {finding.Severity.ToString().ToLowerInvariant()} {PdfUaReport.StandardLabel(finding.Standard)} {finding.Checkpoint}{where}: {finding.Message}");
                }
                con.WriteLine("  Still to check by hand: reading order, quality of alternative text, headings, contrast.");
            }

            // One file named directly gives one JSON object; a folder or several files give an array.
            if (json)
                con.WriteLine(paths.Count == 1 && items.Count == 1 && !Directory.Exists(Path.GetFullPath(paths[0]))
                    ? items[0] : "[" + string.Join(",", items) + "]");
            return unreadable ? Unreadable : notPassed ? NotPassed : Passed;
        }

        private static string OneLine(string s)
        {
            int i = s.IndexOfAny(new[] { '\r', '\n' });
            return i < 0 ? s : s[..i];
        }
    }
}
