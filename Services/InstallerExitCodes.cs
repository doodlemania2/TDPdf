using System;
using System.ComponentModel;
using System.IO;
using System.Security;
using System.Threading;

namespace TDPdf.Services
{
    /// <summary>
    /// Process exit codes for <c>/install</c> and <c>/uninstall</c>, and the lock that keeps two
    /// of them from running at once (upstream KillerPDF 1.8.90).
    /// </summary>
    /// <remarks>
    /// Every failure used to exit 1, which tells a deployment tool nothing it can act on. These are
    /// the standard Windows codes, so Intune and other tools read them as intended: 1618 ("another
    /// installation is in progress") is the one Intune retries by itself, and it is exactly what a
    /// second concurrent install now returns rather than racing the first over the same files.
    /// </remarks>
    internal static class InstallerExitCodes
    {
        internal const int Success      = 0;
        internal const int Failure      = 1;
        internal const int AccessDenied = 5;
        internal const int DiskFull     = 112;
        internal const int Cancelled    = 1223;
        internal const int Busy         = 1618;

        private const int HandleDiskFull = 39;   // ERROR_HANDLE_DISK_FULL, the other "no space" code

        /// <summary>Maps a failure to the most specific exit code its exception chain supports.</summary>
        internal static int FromException(Exception error)
        {
            for (Exception? current = error; current is not null; current = current.InnerException)
            {
                if (current is OperationCanceledException) return Cancelled;
                if (current is UnauthorizedAccessException or SecurityException) return AccessDenied;
                int code = current is Win32Exception native ? native.NativeErrorCode
                         : current is IOException ? current.HResult & 0xffff
                         : 0;
                if (code is HandleDiskFull or DiskFull) return DiskFull;
                if (code is AccessDenied or Cancelled or Busy) return code;
            }
            return Failure;
        }

        /// <summary>
        /// Takes the install lock for one scope, or throws a <see cref="Win32Exception"/> carrying
        /// <see cref="Busy"/> when another install or uninstall holds it. Dispose to release.
        /// </summary>
        /// <param name="scopeKey">
        /// What is being installed into: "Machine", or a per-user key. Two users installing into
        /// their own profiles do not touch each other's files and must not block each other.
        /// </param>
        internal static IDisposable Acquire(string scopeKey)
        {
            Mutex mutex;
            try
            {
                mutex = new Mutex(false, @"Global\TDPdf-Install-" + scopeKey);
            }
            catch (UnauthorizedAccessException)
            {
                // The mutex exists and its owner's security does not let us open it — a SYSTEM
                // install holding it, seen from a user context. A mutex only exists while some
                // process has it open, so this is a live install, not a stale one.
                throw new Win32Exception(Busy, "Another installation of TDPdf is in progress.");
            }

            bool owned;
            try
            {
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }   // its holder died: ours now
            }
            catch { mutex.Dispose(); throw; }

            if (!owned)
            {
                mutex.Dispose();
                throw new Win32Exception(Busy, "Another installation of TDPdf is in progress.");
            }
            return new Lease(mutex);
        }

        private sealed class Lease : IDisposable
        {
            private Mutex? _mutex;
            internal Lease(Mutex mutex) { _mutex = mutex; }

            public void Dispose()
            {
                var mutex = _mutex;
                if (mutex is null) return;
                _mutex = null;
                try { mutex.ReleaseMutex(); }
                finally { mutex.Dispose(); }
            }
        }
    }
}
