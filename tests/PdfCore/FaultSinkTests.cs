using TDPdf.Diagnostics;

/// <summary>
/// Pins <see cref="FaultSink"/>: a recoverable failure reaches the installed handler with its source,
/// and neither a missing handler nor a throwing one can make the caller fail.
/// </summary>
internal static class FaultSinkTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        var saved = FaultSink.Handler;
        try
        {
            FaultSink.Handler = null;
            bool noHandlerOk;
            try { FaultSink.Report("test", new InvalidOperationException("x")); noHandlerOk = true; }
            catch { noHandlerOk = false; }
            Check("fault sink: no handler installed does not throw", noHandlerOk, "");

            string? gotSource = null; Exception? gotEx = null;
            FaultSink.Handler = (s, e) => { gotSource = s; gotEx = e; };
            var boom = new FormatException("boom");
            FaultSink.Report("Forms.Test", boom);
            Check("fault sink: handler receives the source and the exception",
                  gotSource == "Forms.Test" && ReferenceEquals(gotEx, boom), $"{gotSource}");

            FaultSink.Handler = (_, _) => throw new InvalidOperationException("handler broke");
            bool throwingOk;
            try { FaultSink.Report("test", boom); throwingOk = true; }
            catch { throwingOk = false; }
            Check("fault sink: a throwing handler is swallowed", throwingOk, "");
        }
        finally { FaultSink.Handler = saved; }
    }
}
