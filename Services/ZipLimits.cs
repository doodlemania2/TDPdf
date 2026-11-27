using System.IO;
using System.IO.Compression;

namespace TDPdf.Services
{
    /// <summary>
    /// Refuses a dropped .zip that is too big to expand: a "zip bomb" (a few KB that unpack to many GB)
    /// would otherwise fill the temp drive. Zip-slip (entries escaping the target folder) is already
    /// rejected by <see cref="ExtractToDirectory"/>; the rest of this class is about size and count.
    /// </summary>
    internal static class ZipLimits
    {
        internal const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;   // 2 GB of declared uncompressed data
        internal const int MaxEntries = 10_000;

        /// <summary>
        /// Null when the archive may be extracted; otherwise a user-facing reason. An archive that
        /// cannot be read is refused too: a file whose directory cannot be inspected is not one to
        /// expand. The sizes it declares are only a first check, since an archive can lie about them;
        /// <see cref="ExtractToDirectory"/> counts the bytes it really writes.
        /// </summary>
        internal static string? Refusal(string zipPath, long maxTotalBytes = MaxTotalBytes, int maxEntries = MaxEntries)
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                return Refusal(zip.Entries.Count, zip.Entries.Sum(e => e.Length), maxTotalBytes, maxEntries);
            }
            catch (Exception) { return "it could not be read as a zip archive"; }
        }

        /// <summary>
        /// Expands <paramref name="zipPath"/> into <paramref name="destination"/> entry by entry, so
        /// the bytes actually written can be capped whatever sizes the archive declares. Entries that
        /// would land outside the folder are refused (zip-slip), as are duplicate names. Throws
        /// <see cref="InvalidDataException"/> past either cap; the caller removes the folder.
        /// </summary>
        internal static void ExtractToDirectory(string zipPath, string destination,
            long maxTotalBytes = MaxTotalBytes, int maxEntries = MaxEntries)
        {
            string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            Directory.CreateDirectory(root);

            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Count > maxEntries)
                throw new InvalidDataException($"it holds {zip.Entries.Count:N0} entries (the limit is {maxEntries:N0})");

            long written = 0;
            var buffer = new byte[81920];
            foreach (var entry in zip.Entries)
            {
                string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!target.StartsWith(root, comparison))
                    throw new IOException("An entry in the archive points outside the extraction folder.");

                bool isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                if (isDirectory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += n;
                    if (written > maxTotalBytes)
                        throw new InvalidDataException(
                            $"it expands to more than {maxTotalBytes / (1024.0 * 1024 * 1024):0.#} GB");
                    output.Write(buffer, 0, n);
                }
            }
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
