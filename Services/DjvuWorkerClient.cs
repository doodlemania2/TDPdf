using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TDPdf.Services
{
    /// <summary>Caps on one request to the DjVu worker.</summary>
    internal sealed record DjvuProcessLimits(TimeSpan Timeout, long MaxOutputBytes, long MaxMemoryBytes)
    {
        /// <summary>A page render: 3 minutes, 600 MB of picture (a 100-megapixel colour page), 2 GB of memory for the worker.</summary>
        internal static DjvuProcessLimits ForPage { get; } = new(TimeSpan.FromMinutes(3), 600L * 1024 * 1024, 2L * 1024 * 1024 * 1024);

        /// <summary>Opening a book, its text or its bookmarks: 2 minutes, 256 MB of answer, 2 GB of memory for the worker.</summary>
        internal static DjvuProcessLimits ForListing { get; } = new(TimeSpan.FromMinutes(2), 256L * 1024 * 1024, 2L * 1024 * 1024 * 1024);
    }

    /// <summary>
    /// The worker is gone or broke its contract: it crashed, hung past the time limit, printed more than it
    /// may, or sent something that is not the protocol. The process has been killed; the next request starts a
    /// new one. Distinct from <see cref="InvalidDataException"/>, which is the worker saying "this request
    /// failed" and staying alive.
    /// </summary>
    internal sealed class DjvuWorkerDiedException : IOException
    {
        internal DjvuWorkerDiedException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// One running DjVu worker (<c>TDPdf.exe --djvu-worker</c>) and the pipe to it. DjVuLibre is native code
    /// reading a file it was handed, so a hostile book can crash or hang it; it can do that only to this
    /// child. The child is started with no shell, a nearly empty environment, a job object that caps its
    /// memory and kills it if TDPdf goes away, and every request has a time limit and a cap on how much the
    /// answer may weigh. Requests are strictly one at a time (the caller holds a lock).
    /// </summary>
    internal sealed class DjvuWorkerProcess : IDisposable
    {
        private const int MaxStderrBytes = 64 * 1024;
        private readonly Process _process;
        private readonly object? _job;
        private readonly Stream _in, _out;
        private readonly StringBuilder _err = new();
        private bool _dead;

        private DjvuWorkerProcess(Process p, object? job)
        {
            _process = p;
            _job = job;
            _in = p.StandardInput.BaseStream;
            _out = p.StandardOutput.BaseStream;
        }

        internal bool IsAlive => !_dead && !_process.HasExited;

        /// <summary>The worker's stderr so far (bounded), for diagnostics.</summary>
        internal string Stderr { get { lock (_err) return _err.ToString(); } }

        /// <summary>
        /// Starts <paramref name="exe"/> with <paramref name="args"/> and waits for its hello. Throws
        /// <see cref="IOException"/> when it cannot be started, <see cref="DjvuWorkerDiedException"/> when it
        /// does not say hello in time.
        /// </summary>
        internal static DjvuWorkerProcess Start(string exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? extraEnvironment, long memoryBytes, TimeSpan helloTimeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetTempPath(),
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
                // The worker is TDPdf itself: the single-file host and the runtime want these to find their
                // own files. Nothing here is a place the book can write.
                foreach (string k in new[] { "LOCALAPPDATA", "APPDATA", "USERPROFILE", "ProgramFiles", "ProgramData" })
                    if (Environment.GetEnvironmentVariable(k) is { Length: > 0 } v) psi.Environment[k] = v;
            }
            else psi.Environment["PATH"] = "/usr/bin:/bin";
            if (extraEnvironment is not null) foreach (var kv in extraEnvironment) psi.Environment[kv.Key] = kv.Value;

            var process = new Process { StartInfo = psi };
            try { process.Start(); }
            catch (Win32Exception ex) { process.Dispose(); throw new IOException("could not start the DjVu worker: " + ex.Message, ex); }

            object? job = OperatingSystem.IsWindows() && memoryBytes > 0 ? WindowsJob.TryCreate(process, memoryBytes) : null;
            var w = new DjvuWorkerProcess(process, job);
            w.StartStderrPump();
            try
            {
                var (type, payload) = w.ReadWithin(helloTimeout, 1024, ct);
                DjvuWorkerProtocol.CheckHello(type, payload);
                return w;
            }
            catch { w.Dispose(); throw; }
        }

        private void StartStderrPump()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var buf = new byte[4096];
                    int n;
                    var s = _process.StandardError.BaseStream;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        lock (_err) if (_err.Length < MaxStderrBytes) _err.Append(Encoding.Latin1.GetString(buf, 0, n));
                }
                catch { /* the pipe closed with the process */ }
            });
        }

        /// <summary>
        /// Sends one request and returns the worker's reply frame. A reply of type <see cref="DjvuFrame.Error"/>
        /// is returned like any other; the caller decides. Throws <see cref="TimeoutException"/> (the time
        /// limit passed; the worker is killed), <see cref="OperationCanceledException"/> (cancelled; killed) or
        /// <see cref="DjvuWorkerDiedException"/> (crash, broken pipe, bad frame, answer too big; killed).
        /// </summary>
        internal (DjvuFrame Type, byte[] Payload) Request(DjvuFrame type, byte[] payload, DjvuProcessLimits limits, CancellationToken ct)
        {
            if (!IsAlive) throw new DjvuWorkerDiedException("the DjVu worker is not running");
            ct.ThrowIfCancellationRequested();
            try
            {
                DjvuWorkerProtocol.WriteFrame(_in, type, payload);
            }
            catch (IOException ex) { Kill(); throw new DjvuWorkerDiedException("the DjVu worker went away: " + FirstStderrLine(), ex); }
            return ReadWithin(limits.Timeout, limits.MaxOutputBytes, ct);
        }

        private (DjvuFrame Type, byte[] Payload) ReadWithin(TimeSpan timeout, long maxPayload, CancellationToken ct)
        {
            bool timedOut = false;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var reg = cts.Token.Register(() => { Volatile.Write(ref timedOut, !ct.IsCancellationRequested); Kill(); });
            cts.CancelAfter(timeout);
            try
            {
                return DjvuWorkerProtocol.ReadFrame(_out, maxPayload);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or InvalidOperationException)
            {
                Kill();
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                if (Volatile.Read(ref timedOut)) throw new TimeoutException($"the DjVu worker did not answer within {timeout.TotalSeconds:0} seconds");
                string why = ex is InvalidDataException ? ex.Message : "the DjVu worker stopped (" + FirstStderrLine() + ")";
                throw new DjvuWorkerDiedException(why, ex);
            }
        }

        private string FirstStderrLine()
        {
            foreach (string l in Stderr.Split('\n'))
            {
                string t = l.Trim().TrimStart('*').Trim();
                if (t.Length > 0) return t.Length > 160 ? t[..160] : t;
            }
            try { return _process.HasExited ? "exit code " + _process.ExitCode : "no reason given"; } catch { return "no reason given"; }
        }

        internal void Kill()
        {
            _dead = true;
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        }

        /// <summary>Asks the worker to finish, then makes sure it is gone.</summary>
        public void Dispose()
        {
            if (!_dead && IsAlive)
            {
                try { DjvuWorkerProtocol.WriteFrame(_in, DjvuFrame.Quit, ReadOnlySpan<byte>.Empty); _in.Close(); } catch { }
                try { _process.WaitForExit(1500); } catch { }
            }
            Kill();
            try { _process.WaitForExit(2000); } catch { }
            try { _process.Dispose(); } catch { }
            (_job as IDisposable)?.Dispose();
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
