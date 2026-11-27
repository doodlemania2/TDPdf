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

        /// <summary>
        /// The full path of a folder with no trailing separator (a drive root keeps its one), or null
        /// when it is empty or not a valid path.
        /// </summary>
        public static string? NormalizeFolder(string? path)
        {
            string? n = Normalize(path);
            if (n is null) return null;
            string root = Path.GetPathRoot(n) ?? "";
            return n.Length > root.Length ? n.TrimEnd('\\', '/') : n;
        }

        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>
        /// Checks a path a file dialog returned (or the user typed). Returns null when it is a
        /// usable absolute path, otherwise a short sentence for the user. Refuses a relative path,
        /// illegal characters and a reserved device name such as CON or NUL.
        /// </summary>
        public static string? CheckDialogPath(string? path)
        {
            const string BadChars = "That name has characters Windows does not allow in a file name.";
            if (string.IsNullOrWhiteSpace(path)) return "No file name was given.";
            string p = path.Trim();
            // The extended-length prefix \\?\ is the one place a '?' is legal.
            string probe = p.StartsWith("\\\\?\\", StringComparison.Ordinal) ? p[4..] : p;
            if (probe.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*' }) >= 0 || p.Any(c => c < ' ')) return BadChars;

            // Absolute means a drive ("C:\" or "C:/") or a UNC / extended path (starts with two separators).
            // Spelled out here, not Path.IsPathFullyQualified, so the answer is the same on every OS.
            bool drive = p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/');
            bool unc = p.Length >= 3 && (p[0] == '\\' || p[0] == '/') && (p[1] == '\\' || p[1] == '/');
            if (!drive && !unc)
                return "Type a full path, such as C:\\Folder\\file.pdf, not a relative one.";
            // A colon belongs only to the drive letter; \\?\C:\ keeps its own.
            string rest = drive ? p[2..] : p;
            if (unc && rest.StartsWith("\\\\?\\", StringComparison.Ordinal) && rest.Length > 6 && rest[5] == ':')
                rest = rest[6..];
            if (rest.Contains(':')) return BadChars;

            foreach (var segment in p.Split('\\', '/'))
            {
                if (segment.Length == 0 || segment == "?") continue;
                string stem = segment;
                int dot = stem.IndexOf('.');
                if (dot >= 0) stem = stem[..dot];
                stem = stem.TrimEnd(' ');
                if (Array.Exists(ReservedNames, r => string.Equals(r, stem, StringComparison.OrdinalIgnoreCase)))
                    return $"\"{stem.ToUpperInvariant()}\" is a reserved Windows name and cannot be used.";
            }
            return null;
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
