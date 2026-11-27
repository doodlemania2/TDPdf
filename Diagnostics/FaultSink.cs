using System.Diagnostics;

namespace TDPdf.Diagnostics
{
    /// <summary>
    /// Where a recoverable failure that has no UI of its own goes. The old pattern was
    /// <c>catch (Exception ex) { Debug.WriteLine(...); }</c>, which is a no-op in a Release build, so
    /// the failure vanished. <see cref="Report"/> still writes the debug line, and also hands the
    /// failure to <see cref="Handler"/>, which the app points at the sanitised telemetry path
    /// (<c>Telemetry.TrackFault</c>). The exception object itself never leaves this method: the
    /// handler gets it only to take the type and grouping key from it.
    /// </summary>
    /// <remarks>
    /// Kept free of any WPF or telemetry dependency so Services/ code (and the test harness, where
    /// <see cref="Handler"/> stays null) can call it.
    /// </remarks>
    internal static class FaultSink
    {
        /// <summary>Set once by <c>Telemetry.Initialize</c>; null in the test harness.</summary>
        internal static Action<string, Exception>? Handler { get; set; }

        /// <summary>Records a failure that was handled by carrying on. Never throws.</summary>
        internal static void Report(string source, Exception ex)
        {
            try { Debug.WriteLine($"{source}: {ex}"); } catch { }
            try { Handler?.Invoke(source, ex); } catch { }
        }
    }
}
