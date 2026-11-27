namespace TDPdf.Services
{
    /// <summary>
    /// The single message that reports every file that would not open when several were chosen at
    /// once. Pure so tests/PdfCore can pin the wording and the cap on how many lines it shows.
    /// </summary>
    internal static class OpenBatchReport
    {
        /// <summary>Failures listed individually before the rest are summarised as "and N more".</summary>
        public const int MaxListed = 8;

        /// <summary>The message for the failures, or null when nothing failed.</summary>
        public static string? Summarize(int total, IReadOnlyList<(string Name, string Reason)> failures)
        {
            if (failures.Count == 0) return null;
            string head = total == 1
                ? "That file could not be opened:"
                : $"{failures.Count} of {total} files could not be opened. The others are open.";
            var lines = new List<string> { head };
            foreach (var (name, reason) in failures.Take(MaxListed))
            {
                string why = FirstLine(reason);
                lines.Add(why.Length == 0 ? $"  {name}" : $"  {name}: {why}");
            }
            if (failures.Count > MaxListed) lines.Add($"  and {failures.Count - MaxListed} more");
            return string.Join("\n", lines);
        }

        private static string FirstLine(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string line = text.Split('\n')[0].Trim();
            return line.Length > 160 ? line[..160].TrimEnd() + "…" : line;
        }
    }
}
