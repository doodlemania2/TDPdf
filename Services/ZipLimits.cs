using System.IO.Compression;

namespace TDPdf.Services
{
    /// <summary>
    /// Refuses a dropped .zip that is too big to expand: a "zip bomb" (a few KB that unpack to many GB)
    /// would otherwise fill the temp drive. Zip-slip (entries escaping the target folder) is already
    /// rejected by <c>ZipFile.ExtractToDirectory</c>; this is only about size and count.
    /// </summary>
    internal static class ZipLimits
    {
        internal const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;   // 2 GB of declared uncompressed data
        internal const int MaxEntries = 10_000;

        /// <summary>
        /// Null when the archive may be extracted; otherwise a user-facing reason. An unreadable
        /// archive is not refused here, so extraction reports its own error as before.
        /// </summary>
        internal static string? Refusal(string zipPath, long maxTotalBytes = MaxTotalBytes, int maxEntries = MaxEntries)
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                return Refusal(zip.Entries.Count, zip.Entries.Sum(e => e.Length), maxTotalBytes, maxEntries);
            }
            catch (Exception) { return null; }
        }

        internal static string? Refusal(int entryCount, long totalBytes, long maxTotalBytes = MaxTotalBytes, int maxEntries = MaxEntries)
        {
            if (entryCount > maxEntries)
                return $"it holds {entryCount:N0} entries (the limit is {maxEntries:N0})";
            if (totalBytes > maxTotalBytes)
                return $"it would expand to {totalBytes / (1024.0 * 1024 * 1024):0.0} GB (the limit is {maxTotalBytes / (1024.0 * 1024 * 1024):0.#} GB)";
            return null;
        }
    }
}
