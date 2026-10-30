using System;
using System.IO;
using System.Threading;

namespace TDPdf.Services
{
    /// <summary>
    /// Thrown when a save could not be moved over the destination and the destination may have been
    /// left damaged. The complete document is kept at <see cref="RecoveredPath"/>, and the message
    /// says where, so the caller's ordinary "Save failed" dialog tells the user how to get it back.
    /// </summary>
    internal sealed class SaveRecoveredException : IOException
    {
        public string RecoveredPath { get; }

        public SaveRecoveredException(string destinationPath, string recoveredPath, Exception inner)
            : base($"TDPdf could not finish writing over {Path.GetFileName(destinationPath)}, so that file may be incomplete. " +
                   $"A complete copy of your document was saved as:\n{recoveredPath}\n\n({inner.Message})", inner)
        {
            RecoveredPath = recoveredPath;
        }
    }

    /// <summary>
    /// Serializes to a sibling temp file and only then moves it into place. WPF-free so the
    /// failure paths can be exercised by tests/PdfCore.
    /// </summary>
    internal static class AtomicFileWriter
    {
        /// <param name="replace">Test seam for <see cref="File.Replace(string,string,string,bool)"/>.</param>
        /// <param name="copy">Test seam for the overwrite fallback.</param>
        public static void Write(Action<string> saveToPath, string destinationPath, CancellationToken cancellationToken,
                                 Action<string, string>? replace = null, Action<string, string>? copy = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            replace ??= (src, dst) => File.Replace(src, dst, null, ignoreMetadataErrors: true);
            copy ??= (src, dst) => File.Copy(src, dst, overwrite: true);

            string directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath)) ?? ".";
            string staging = Path.Combine(directory, $".tdpdf_save_{Guid.NewGuid():N}.tmp");
            // Once the destination may be damaged, the staged file is the only complete copy and
            // must never be deleted, whatever is thrown afterwards.
            bool keepStaging = false;
            try
            {
                saveToPath(staging);
                cancellationToken.ThrowIfCancellationRequested();

                if (File.Exists(destinationPath))
                {
                    // No backup file is passed: a backup would be clutter beside every successful
                    // save, and File.Replace already leaves the destination intact when it refuses.
                    try
                    {
                        replace(staging, destinationPath);
                        return;
                    }
                    catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                                               && File.Exists(staging))
                    {
                        // Refused (some SMB shares and cloud-sync folders do). Fall through to a
                        // plain overwrite from a file that is by now complete and closed.
                    }

                    try
                    {
                        copy(staging, destinationPath);
                    }
                    catch (Exception copyEx)
                    {
                        // The overwrite may have stopped part-way, truncating the user's file. Keep
                        // the complete copy under a name the user can find next to the original.
                        keepStaging = true;
                        string recovered = KeepRecoveredCopy(staging, destinationPath);
                        throw new SaveRecoveredException(destinationPath, recovered, copyEx);
                    }
                    TryDelete(staging);
                }
                else
                {
                    File.Move(staging, destinationPath);
                }
            }
            catch
            {
                if (!keepStaging) TryDelete(staging);
                throw;
            }
        }

        /// <summary>
        /// Renames the staged file to "&lt;name&gt; (TDPdf recovered &lt;timestamp&gt;).pdf" beside the
        /// destination. If even that rename fails, the staged file stays where it is and its own
        /// path is reported.
        /// </summary>
        internal static string KeepRecoveredCopy(string staging, string destinationPath)
        {
            try
            {
                string full = Path.GetFullPath(destinationPath);
                string dir = Path.GetDirectoryName(full) ?? ".";
                string name = Path.GetFileNameWithoutExtension(full);
                string ext = Path.GetExtension(full);
                if (string.IsNullOrEmpty(ext)) ext = ".pdf";
                string stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss");
                string target = Path.Combine(dir, $"{name} (TDPdf recovered {stamp}){ext}");
                for (int n = 2; File.Exists(target); n++)
                    target = Path.Combine(dir, $"{name} (TDPdf recovered {stamp} {n}){ext}");
                File.Move(staging, target);
                return target;
            }
            catch
            {
                return staging;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
    }
}
