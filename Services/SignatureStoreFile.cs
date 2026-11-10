using System.IO;
using System.Globalization;

namespace TDPdf.Services
{
    /// <summary>
    /// File handling for signatures.json, WPF-free so tests/PdfCore can exercise it. A signature
    /// file that cannot be read is kept (copied aside under a dated name) before anything is allowed
    /// to write over it, and every write goes through <see cref="AtomicFileWriter"/>.
    /// </summary>
    internal static class SignatureStoreFile
    {
        /// <summary>"signatures.json.damaged-20261107-143005" beside the original.</summary>
        internal static string DamagedBackupPath(string file, DateTime now) =>
            file + ".damaged-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        /// <summary>
        /// Copies the unreadable file to a dated sibling and returns that path, or null when the
        /// copy failed (the caller must then leave the original alone). A second backup within the
        /// same second gets a numeric suffix rather than replacing the first.
        /// </summary>
        internal static string? TryBackUpDamaged(string file, DateTime now)
        {
            try
            {
                string target = DamagedBackupPath(file, now);
                for (int n = 2; File.Exists(target); n++) target = DamagedBackupPath(file, now) + "-" + n;
                File.Copy(file, target, overwrite: false);
                return target;
            }
            catch { return null; }
        }

        /// <summary>Writes the JSON beside a temp file and moves it into place, so a crash mid-write cannot truncate it.</summary>
        internal static void WriteAtomic(string file, string json)
        {
            string? dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFileWriter.Write(staging => File.WriteAllText(staging, json), file, CancellationToken.None);
        }
    }
}
