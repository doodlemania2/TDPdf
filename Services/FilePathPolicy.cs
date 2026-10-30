using System;
using System.Collections.Generic;
using System.IO;

namespace TDPdf.Services
{
    /// <summary>
    /// Path comparisons and the restore-on-launch session merge. WPF-free so tests/PdfCore can pin
    /// them. Windows paths are case-insensitive, so every comparison here is too.
    /// </summary>
    internal static class FilePathPolicy
    {
        /// <summary>The full path, or null when <paramref name="path"/> is empty or not a valid path.</summary>
        public static string? Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path.Trim()); }
            catch { return null; }
        }

        /// <summary>True when both name the same file (after normalising; case-insensitive).</summary>
        public static bool SamePath(string? a, string? b)
        {
            string? na = Normalize(a), nb = Normalize(b);
            return na is not null && nb is not null && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Normalises each entry and drops invalid ones and later duplicates, keeping first-seen
        /// order. Old recent-file entries were stored as typed (forward slashes, relative segments),
        /// so the same file could otherwise appear twice.
        /// </summary>
        public static List<string> NormalizeDistinct(IEnumerable<string?> paths)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                var n = Normalize(p);
                if (n is not null && seen.Add(n)) result.Add(n);
            }
            return result;
        }

        /// <summary>
        /// Merges one window's tabs into the saved restore-on-launch session.
        /// </summary>
        /// <remarks>
        /// Every TDPdf window is its own process and they share one saved list. Overwriting it on
        /// close meant the last window to close won, and a window closing with nothing open wiped
        /// the others' documents. Instead each window removes only the entries it contributed
        /// earlier (<paramref name="ownPrevious"/>: what it restored or claimed at startup) and
        /// appends its current tabs; entries other windows wrote are left alone.
        /// </remarks>
        public static List<string> MergeSession(IEnumerable<string?> saved, IEnumerable<string?> ownPrevious,
                                                IEnumerable<string?> ownCurrent)
        {
            var mine = new HashSet<string>(NormalizeDistinct(ownPrevious), StringComparer.OrdinalIgnoreCase);
            var merged = new List<string>();
            foreach (var p in NormalizeDistinct(saved))
                if (!mine.Contains(p)) merged.Add(p);
            merged.AddRange(NormalizeDistinct(ownCurrent));
            return NormalizeDistinct(merged);
        }
    }
}
