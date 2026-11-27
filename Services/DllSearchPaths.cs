using System.IO;

namespace TDPdf.Services
{
    /// <summary>
    /// Chooses which native-library folders the process-wide DLL search is told about
    /// (see <c>DllSearchHardening</c>). Pure, so it is tested off Windows.
    /// </summary>
    internal static class DllSearchPaths
    {
        /// <summary>
        /// The folders in the host's <c>NATIVE_DLL_SEARCH_DIRECTORIES</c> list (semicolon separated:
        /// the single-file extraction folder, the .NET runtime folders) that may be added to the DLL
        /// search path, in order and without duplicates. The folder holding the exe itself is left
        /// out: for a portable copy that is Downloads, the folder this hardening exists to stop
        /// searching. Relative entries and ones that do not exist are skipped.
        /// </summary>
        internal static IReadOnlyList<string> NativeDirectories(
            string? hostList, string? exeDirectory, Func<string, bool> exists)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(hostList)) return result;

            string? exe = Normalize(exeDirectory);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in hostList.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string entry = raw.Trim();
                if (entry.Length == 0 || !Path.IsPathRooted(entry)) continue;
                string? norm = Normalize(entry);
                if (norm is null) continue;
                if (exe is not null && string.Equals(norm, exe, StringComparison.OrdinalIgnoreCase)) continue;
                if (!exists(norm) || !seen.Add(norm)) continue;
                result.Add(norm);
            }
            return result;
        }

        private static string? Normalize(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return null;
            try { return Path.GetFullPath(dir).TrimEnd('\\', '/'); }
            catch { return null; }
        }
    }
}
