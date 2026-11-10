using System.IO;
using System.Text.RegularExpressions;

namespace TDPdf.Services
{
    /// <summary>
    /// Naming, tracking and sweeping of the working copies TDPdf writes under %TEMP%.
    /// </summary>
    /// <remarks>
    /// A decrypted copy of a password-protected file, a pre-redaction snapshot and every structural
    /// edit's working file used to be left in %TEMP% forever. Each tab now owns a
    /// <see cref="TempFileSet"/> that is emptied when the tab closes and when the window closes;
    /// <see cref="Sweep"/> collects what a crash or a kill left behind.
    /// </remarks>
    internal static class TempFilePolicy
    {
        /// <summary><c>tdpdf_&lt;tag&gt;_&lt;32 hex&gt;.pdf</c> — the only names TDPdf ever writes here.</summary>
        private static readonly Regex s_name = new(
            @"^tdpdf_[a-z][a-z_]*_[0-9a-f]{32}\.pdf$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>No other TDPdf is running: anything this old and unlocked is somebody's leftover.</summary>
        internal static readonly TimeSpan AloneMinAge = TimeSpan.FromHours(1);

        /// <summary>
        /// Another TDPdf process is running. Its working files are written once and then only read,
        /// so their timestamps do not advance while the document stays open; a day is not long
        /// enough to tell "abandoned" from "open since yesterday".
        /// </summary>
        internal static readonly TimeSpan OthersRunningMinAge = TimeSpan.FromDays(7);

        /// <summary>A fresh working-file path in the temp folder.</summary>
        internal static string NewPath(string tag, string? directory = null)
        {
            string clean = Regex.Replace((tag ?? "").ToLowerInvariant(), "[^a-z_]", "_").Trim('_');
            if (clean.Length == 0) clean = "temp";
            return Path.Combine(directory ?? Path.GetTempPath(), $"tdpdf_{clean}_{Guid.NewGuid():N}.pdf");
        }

        /// <summary>True for a file TDPdf wrote into the temp folder (by name and by location).</summary>
        internal static bool IsOurs(string? path, string? tempDirectory = null)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string full = Path.GetFullPath(path);
                if (!s_name.IsMatch(Path.GetFileName(full))) return false;
                string dir = Path.GetFullPath(tempDirectory ?? Path.GetTempPath())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string? parent = Path.GetDirectoryName(full);
                return parent is not null && string.Equals(
                    parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), dir,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>Whether a leftover file is old enough to delete.</summary>
        internal static bool ShouldSweep(string fileName, DateTime lastWriteUtc, DateTime nowUtc, bool otherInstanceRunning)
        {
            if (!s_name.IsMatch(fileName)) return false;
            TimeSpan min = otherInstanceRunning ? OthersRunningMinAge : AloneMinAge;
            return nowUtc - lastWriteUtc >= min;
        }

        /// <summary>
        /// Deletes stale <c>tdpdf_*</c> working files in <paramref name="directory"/>. A file another
        /// process has open is left alone. Never throws. Returns how many files were removed.
        /// </summary>
        internal static int Sweep(string directory, DateTime nowUtc, bool otherInstanceRunning,
                                  Func<string, bool>? isLocked = null)
        {
            int removed = 0;
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, "tdpdf_*.pdf"))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.LinkTarget is not null) continue;   // never follow a link planted in %TEMP%
                        if (!ShouldSweep(info.Name, info.LastWriteTimeUtc, nowUtc, otherInstanceRunning)) continue;
                        if ((isLocked ?? IsLocked)(file)) continue;
                        File.Delete(file);
                        removed++;
                    }
                    catch { /* in use, or gone: next one */ }
                }
            }
            catch { /* the folder vanished or is unreadable: nothing to do */ }
            return removed;
        }

        /// <summary>True if another process holds the file open (or it cannot be opened exclusively).</summary>
        internal static bool IsLocked(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch { return true; }
        }

        /// <summary>Best-effort delete of one temp file. The single deleter for working copies.</summary>
        internal static bool TryDelete(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch { return false; }   // locked or already gone; the startup sweep collects it later
        }
    }

    /// <summary>
    /// The working files one tab owns. Deleted when the tab closes or the window exits.
    /// </summary>
    internal sealed class TempFileSet
    {
        private readonly HashSet<string> _paths = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        internal int Count => _paths.Count;

        internal bool Contains(string path) => _paths.Contains(path);

        /// <summary>Starts owning <paramref name="path"/>. Null and empty are ignored.</summary>
        internal void Track(string? path)
        {
            if (!string.IsNullOrEmpty(path)) _paths.Add(path);
        }

        /// <summary>Stops owning <paramref name="path"/> without deleting it.</summary>
        internal void Release(string? path)
        {
            if (!string.IsNullOrEmpty(path)) _paths.Remove(path);
        }

        /// <summary>
        /// Deletes every owned file except <paramref name="keep"/> (a file handed to another window).
        /// Files that could not be deleted stay owned, so a later call retries them.
        /// </summary>
        internal int DeleteAll(string? keep = null)
        {
            int removed = 0;
            foreach (string p in new List<string>(_paths))
            {
                if (keep is not null && string.Equals(p, keep, StringComparison.OrdinalIgnoreCase))
                {
                    _paths.Remove(p);   // ownership moves with the file
                    continue;
                }
                if (TempFilePolicy.TryDelete(p) || !File.Exists(p))
                {
                    _paths.Remove(p);
                    removed++;
                }
            }
            return removed;
        }
    }
}
