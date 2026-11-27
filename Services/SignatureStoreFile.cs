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

        /// <summary>
        /// The list to write when another TDPdf window may have changed the file since this one read it.
        /// </summary>
        /// <remarks>
        /// Three-way, by id. <paramref name="baseIds"/> is what this window last read or wrote.
        /// Everything this window holds is kept except what another window has since deleted;
        /// entries another window ADDED (on disk, not in the base, not held here) are appended, so
        /// saving here cannot drop a signature saved there; and what this window deleted (in the
        /// base, no longer held) stays deleted. <paramref name="onDisk"/> is null when the file
        /// could not be read, in which case this window's list is written as it is.
        /// </remarks>
        internal static List<T> Merge<T>(IReadOnlyList<T> mine, IReadOnlyCollection<string> baseIds,
            IReadOnlyList<T>? onDisk, Func<T, string> id)
        {
            if (onDisk is null) return new List<T>(mine);
            var disk = new HashSet<string>(onDisk.Select(id), StringComparer.Ordinal);
            var held = new HashSet<string>(mine.Select(id), StringComparer.Ordinal);
            var bases = new HashSet<string>(baseIds, StringComparer.Ordinal);

            var result = new List<T>();
            foreach (var item in mine)
            {
                string key = id(item);
                // Known to this window at the last sync but gone from disk: another window deleted it.
                if (bases.Contains(key) && !disk.Contains(key)) continue;
                result.Add(item);
            }
            foreach (var item in onDisk)
            {
                string key = id(item);
                if (!held.Contains(key) && !bases.Contains(key)) result.Add(item);
            }
            return result;
        }

        private static string MutexName(string file)
        {
            // Per file, not per session: the file is per user, and two sessions of one user share it.
            byte[] hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(file).ToUpperInvariant()));
            return "Global\\TDPdf.Store." + Convert.ToHexString(hash, 0, 8);
        }

        /// <summary>
        /// Reads the file, merges this window's list into it (<see cref="Merge{T}"/>) and writes the
        /// result, all under a named mutex so two windows saving at once take turns. Returns what was
        /// written, which the caller adopts as its list. Without the mutex (it cannot be created, as
        /// when one window is elevated and the other is not, or the wait times out) it still merges,
        /// which narrows the window but cannot close it.
        /// </summary>
        internal static List<T> SaveMerged<T>(string file, IReadOnlyList<T> mine, IReadOnlyCollection<string> baseIds,
            Func<T, string> id, System.Text.Json.JsonSerializerOptions options)
        {
            Mutex? gate = null;
            bool held = false;
            try
            {
                try
                {
                    gate = new Mutex(false, MutexName(file));
                    try { held = gate.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (AbandonedMutexException) { held = true; }   // the other window died holding it
                }
                catch { /* no cross-process gate available: carry on without */ }

                List<T>? onDisk = null;
                try
                {
                    if (File.Exists(file))
                        onDisk = System.Text.Json.JsonSerializer.Deserialize<List<T>>(File.ReadAllText(file), options);
                }
                catch { onDisk = null; }   // unreadable: the caller has already kept a backup

                var merged = Merge(mine, baseIds, onDisk, id);
                WriteAtomic(file, System.Text.Json.JsonSerializer.Serialize(merged, options));
                return merged;
            }
            finally
            {
                if (held) { try { gate!.ReleaseMutex(); } catch { } }
                gate?.Dispose();
            }
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
