using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TDPdf.Services
{
    /// <summary>Caps on one run of a DjVuLibre program.</summary>
    internal sealed record DjvuProcessLimits(TimeSpan Timeout, long MaxOutputBytes, long MaxMemoryBytes)
    {
        /// <summary>A page render: 3 minutes, 600 MB of output (a 100-megapixel colour page), 2 GB of memory.</summary>
        internal static DjvuProcessLimits ForPage { get; } = new(TimeSpan.FromMinutes(3), 600L * 1024 * 1024, 2L * 1024 * 1024 * 1024);

        /// <summary>A listing or text dump: 2 minutes, 256 MB of output, 1 GB of memory.</summary>
        internal static DjvuProcessLimits ForListing { get; } = new(TimeSpan.FromMinutes(2), 256L * 1024 * 1024, 1L * 1024 * 1024 * 1024);
    }

    internal sealed record DjvuProcessResult(int ExitCode, byte[] Stdout, string Stderr);

    /// <summary>
    /// Runs one of DjVuLibre's programs as a child process and nothing more: no shell, a nearly empty
    /// environment, a time limit, a cap on how much it may print, and (on Windows) a job object that caps its
    /// memory and kills it if TDPdf goes away. DjVuLibre is native code reading a file it was handed, so a
    /// hostile book can crash or hang it; it can do that only to this child.
    /// </summary>
    internal static class DjvuProcess
    {
        private const int MaxStderrBytes = 64 * 1024;

        /// <summary>Runs <paramref name="exe"/>; throws <see cref="TimeoutException"/>, <see cref="InvalidDataException"/> (output too big) or <see cref="OperationCanceledException"/>.</summary>
        internal static DjvuProcessResult Run(string exe, IReadOnlyList<string> args, DjvuProcessLimits limits, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe)) ?? Path.GetTempPath(),
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.Environment.Clear();
            string temp = Path.GetTempPath();
            psi.Environment["TEMP"] = temp;
            psi.Environment["TMP"] = temp;
            if (OperatingSystem.IsWindows())
            {
                string root = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
                psi.Environment["SystemRoot"] = root;
                psi.Environment["windir"] = root;
            }
            else psi.Environment["PATH"] = "/usr/bin:/bin";

            using var process = new Process { StartInfo = psi };
            try { process.Start(); }
            catch (Win32Exception ex) { throw new IOException("could not start " + Path.GetFileName(exe) + ": " + ex.Message, ex); }

            using var job = OperatingSystem.IsWindows() && limits.MaxMemoryBytes > 0
                ? WindowsJob.TryCreate(process, limits.MaxMemoryBytes) : null;

            try { process.StandardInput.Close(); } catch { }

            var stdout = new MemoryStream();
            bool tooBig = false;
            var outTask = Task.Run(() =>
            {
                var buf = new byte[81920];
                int n;
                var s = process.StandardOutput.BaseStream;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    if (stdout.Length + n > limits.MaxOutputBytes) { tooBig = true; Kill(process); return; }
                    stdout.Write(buf, 0, n);
                }
            });
            var err = new StringBuilder();
            var errTask = Task.Run(() =>
            {
                var buf = new byte[4096];
                int n;
                var s = process.StandardError.BaseStream;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    if (err.Length < MaxStderrBytes) err.Append(Encoding.Latin1.GetString(buf, 0, n));
            });

            var deadline = DateTime.UtcNow + limits.Timeout;
            while (!process.WaitForExit(100))
            {
                if (ct.IsCancellationRequested) { Kill(process); process.WaitForExit(2000); ct.ThrowIfCancellationRequested(); }
                if (DateTime.UtcNow > deadline)
                {
                    Kill(process);
                    process.WaitForExit(2000);
                    throw new TimeoutException($"{Path.GetFileName(exe)} did not finish within {limits.Timeout.TotalSeconds:0} seconds");
                }
            }
            try { Task.WaitAll(new[] { outTask, errTask }, 5000); } catch { }
            if (tooBig) throw new InvalidDataException($"{Path.GetFileName(exe)} printed more than {limits.MaxOutputBytes / (1024 * 1024)} MB");
            return new DjvuProcessResult(process.ExitCode, stdout.ToArray(), err.ToString());
        }

        private static void Kill(Process p)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }

        /// <summary>
        /// A job object holding the child: a memory ceiling per process, and the child dies with the job.
        /// Windows only. Best effort: if the job cannot be made, the child runs under the time and output
        /// limits alone.
        /// </summary>
        private sealed class WindowsJob : IDisposable
        {
            private IntPtr _handle;
            private WindowsJob(IntPtr h) => _handle = h;

            [StructLayout(LayoutKind.Sequential)]
            private struct BasicLimits
            {
                public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass, SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct IoCounters
            {
                public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount,
                    ReadTransferCount, WriteTransferCount, OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct ExtendedLimits
            {
                public BasicLimits Basic;
                public IoCounters Io;
                public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
            }

            private const uint LimitProcessMemory = 0x00000100;
            private const uint LimitDieOnUnhandledException = 0x00000400;
            private const uint LimitKillOnJobClose = 0x00002000;
            private const int ExtendedLimitInformation = 9;

            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateJobObjectW(IntPtr attrs, string? name);

            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [DllImport("kernel32", SetLastError = true)]
            private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, int size);

            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [DllImport("kernel32", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            [DllImport("kernel32", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr handle);

            internal static WindowsJob? TryCreate(Process process, long memoryBytes)
            {
                try
                {
                    IntPtr h = CreateJobObjectW(IntPtr.Zero, null);
                    if (h == IntPtr.Zero) return null;
                    var limits = new ExtendedLimits();
                    limits.Basic.LimitFlags = LimitProcessMemory | LimitKillOnJobClose | LimitDieOnUnhandledException;
                    limits.ProcessMemoryLimit = (UIntPtr)(ulong)memoryBytes;
                    if (!SetInformationJobObject(h, ExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimits>()) ||
                        !AssignProcessToJobObject(h, process.Handle))
                    {
                        CloseHandle(h);
                        return null;
                    }
                    return new WindowsJob(h);
                }
                catch { return null; }
            }

            public void Dispose()
            {
                if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
            }
        }
    }
}
