using System;
using System.IO;
using System.Security.Cryptography;

namespace TDPdf.Services
{
    /// <summary>What a save in place finds at the document's path compared with what was opened.</summary>
    internal enum FileChangeState
    {
        /// <summary>Same content as when it was opened or last saved.</summary>
        Unchanged,

        /// <summary>Another program rewrote it.</summary>
        Changed,

        /// <summary>Deleted, moved or renamed.</summary>
        Missing,

        /// <summary>Nothing was recorded, or it cannot be read now: no basis for a warning.</summary>
        Unknown
    }

    /// <summary>
    /// Length and SHA-256 of a document as TDPdf last saw it, so a save in place can tell whether
    /// another program changed the file in the meantime. WPF-free for tests/PdfCore.
    /// </summary>
    /// <remarks>
    /// Hashed rather than compared by size and timestamp: a tool that rewrites a file in place can
    /// keep both (upstream KillerPDF 1.9.0 detects that case too), and a copy that only touched the
    /// timestamp is not a change worth interrupting a save for.
    /// </remarks>
    internal sealed record FileFingerprint(long Length, byte[] Sha256)
    {
        /// <summary>Reads and hashes <paramref name="path"/>; null when it cannot be read.</summary>
        public static FileFingerprint? TryCapture(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                // Share read/write/delete so hashing never blocks the user's other programs.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
                long length = stream.Length;
                byte[] hash = SHA256.HashData(stream);
                return new FileFingerprint(length, hash);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Compares the file at <paramref name="path"/> now with <paramref name="recorded"/>.</summary>
        public static FileChangeState Compare(FileFingerprint? recorded, string path)
        {
            if (recorded is null) return FileChangeState.Unknown;
            try
            {
                if (!File.Exists(path)) return FileChangeState.Missing;
                // A different length is a change without reading the file.
                if (new FileInfo(path).Length != recorded.Length) return FileChangeState.Changed;
            }
            catch
            {
                return FileChangeState.Unknown;
            }
            var now = TryCapture(path);
            if (now is null) return FileChangeState.Unknown;
            return now.Length == recorded.Length && now.Sha256.AsSpan().SequenceEqual(recorded.Sha256)
                ? FileChangeState.Unchanged
                : FileChangeState.Changed;
        }
    }
}
