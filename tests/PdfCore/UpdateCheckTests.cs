using TDPdf.Services;

/// <summary>
/// Pins the two pure decisions behind the update notice: when a check is due, and whether the EXE on
/// disk is newer than the running build (which is what makes the status bar say a restart is waiting).
/// </summary>
internal static class UpdateCheckTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("── update check decisions ─────────────────────────────────────");

        var now = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        var twelve = TimeSpan.FromHours(12);
        string Stamp(TimeSpan ago) => (now - ago).ToString("O");

        Check("interval: no stamp stored means a check is due", UpdateCheckPolicy.IntervalElapsed(null, now, twelve), "");
        Check("interval: an unparseable stamp means a check is due", UpdateCheckPolicy.IntervalElapsed("not a date", now, twelve), "");
        Check("interval: an empty stamp means a check is due", UpdateCheckPolicy.IntervalElapsed("", now, twelve), "");
        Check("interval: a check a minute ago is not due", !UpdateCheckPolicy.IntervalElapsed(Stamp(TimeSpan.FromMinutes(1)), now, twelve), "");
        Check("interval: eleven hours fifty-nine minutes ago is not due",
              !UpdateCheckPolicy.IntervalElapsed(Stamp(twelve - TimeSpan.FromMinutes(1)), now, twelve), "");
        Check("interval: exactly twelve hours ago is due (the boundary counts)",
              UpdateCheckPolicy.IntervalElapsed(Stamp(twelve), now, twelve), "");
        Check("interval: three days ago is due", UpdateCheckPolicy.IntervalElapsed(Stamp(TimeSpan.FromDays(3)), now, twelve), "");
        Check("interval: a stamp in the future (clock moved back) is due rather than disabling checks",
              UpdateCheckPolicy.IntervalElapsed(now.AddHours(5).ToString("O"), now, twelve), "");
        Check("interval: a zero interval is always due once a stamp exists",
              UpdateCheckPolicy.IntervalElapsed(Stamp(TimeSpan.Zero), now, TimeSpan.Zero), "");

        var running = new Version(2, 3, 0, 0);
        Check("restart pending: a newer file version is pending", UpdateCheckPolicy.IsNewerThanRunning("2.3.1.0", running), "");
        Check("restart pending: a newer minor version is pending", UpdateCheckPolicy.IsNewerThanRunning("2.4.0.0", running), "");
        Check("restart pending: the same version is not", !UpdateCheckPolicy.IsNewerThanRunning("2.3.0.0", running), "");
        Check("restart pending: an older file version is not", !UpdateCheckPolicy.IsNewerThanRunning("2.2.9.9", running), "");
        Check("restart pending: a missing file version is not", !UpdateCheckPolicy.IsNewerThanRunning(null, running), "");
        Check("restart pending: an unparseable file version is not", !UpdateCheckPolicy.IsNewerThanRunning("garbage", running), "");
        Check("restart pending: a three-part file version is read as such (2.3.1 > 2.3.0.0)",
              UpdateCheckPolicy.IsNewerThanRunning("2.3.1", running), "");
    }
}
