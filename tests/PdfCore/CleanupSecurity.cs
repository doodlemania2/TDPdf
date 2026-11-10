using TDPdf.Diagnostics;
using TDPdf.Services;

/// <summary>
/// Pins the 2.3.0.0 cleanup items that are plain logic: temp working-file naming, tracking and
/// sweeping, the Sanitizer's UNC rule, the privileged-path guard's pure checks, and the
/// protected-save impact assessment.
/// </summary>
internal static class CleanupSecurity
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("── temp files, privileged paths, sanitizer, protected-save impact ──");

        // ── naming ─────────────────────────────────────────────────────────────────────────
        string dir = Path.Combine(tmp, "tempfiles");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);

        string named = TempFilePolicy.NewPath("clean", dir);
        Check("NewPath is tdpdf_<tag>_<guid>.pdf in the folder asked for",
              TempFilePolicy.IsOurs(named, dir) && Path.GetFileName(named).StartsWith("tdpdf_clean_"), named);
        Check("a tag with odd characters is made safe",
              TempFilePolicy.IsOurs(TempFilePolicy.NewPath("Edit Src!", dir), dir), "");
        Check("a user's own file is never ours",
              !TempFilePolicy.IsOurs(Path.Combine(dir, "report.pdf"), dir)
              && !TempFilePolicy.IsOurs(Path.Combine(dir, "tdpdf_report.pdf"), dir), "");
        Check("a matching name in another folder is not ours",
              !TempFilePolicy.IsOurs(named, Path.Combine(dir, "elsewhere")), "");
        Check("null and empty are not ours", !TempFilePolicy.IsOurs(null) && !TempFilePolicy.IsOurs(""), "");

        // ── sweep policy ───────────────────────────────────────────────────────────────────
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        string leftover = Path.GetFileName(named);
        Check("alone: a 2 h old leftover is swept",
              TempFilePolicy.ShouldSweep(leftover, now.AddHours(-2), now, otherInstanceRunning: false), "");
        Check("alone: a fresh file is kept",
              !TempFilePolicy.ShouldSweep(leftover, now.AddMinutes(-5), now, otherInstanceRunning: false), "");
        Check("another TDPdf running: a 3 day old file is kept (it may be open there)",
              !TempFilePolicy.ShouldSweep(leftover, now.AddDays(-3), now, otherInstanceRunning: true), "");
        Check("another TDPdf running: a 9 day old file is swept",
              TempFilePolicy.ShouldSweep(leftover, now.AddDays(-9), now, otherInstanceRunning: true), "");
        Check("a non-TDPdf name is never swept",
              !TempFilePolicy.ShouldSweep("notes.pdf", now.AddYears(-1), now, otherInstanceRunning: false), "");

        // ── sweep on disk ──────────────────────────────────────────────────────────────────
        string old1 = TempFilePolicy.NewPath("dec", dir);
        string old2 = TempFilePolicy.NewPath("temp", dir);
        string fresh = TempFilePolicy.NewPath("undo", dir);
        string user = Path.Combine(dir, "mine.pdf");
        string locked = TempFilePolicy.NewPath("repair", dir);
        foreach (var f in new[] { old1, old2, fresh, user, locked }) File.WriteAllText(f, "x");
        foreach (var f in new[] { old1, old2, user, locked }) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-2));
        int removed = TempFilePolicy.Sweep(dir, DateTime.UtcNow, otherInstanceRunning: false, isLocked: p => p == locked);
        Check("sweep removes the stale unlocked working files",
              removed == 2 && !File.Exists(old1) && !File.Exists(old2), $"removed {removed}");
        Check("sweep keeps a fresh file, a user's file, and a file another process holds",
              File.Exists(fresh) && File.Exists(user) && File.Exists(locked), "");
        Check("sweep of a missing folder does not throw",
              TempFilePolicy.Sweep(Path.Combine(dir, "nope"), DateTime.UtcNow, false) == 0, "");

        // A real lock (FileShare.None) is detected.
        using (var held = new FileStream(fresh, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check("IsLocked sees a file held open exclusively", TempFilePolicy.IsLocked(fresh), "");
        Check("IsLocked is false once released", !TempFilePolicy.IsLocked(fresh), "");

        // ── per-tab set ────────────────────────────────────────────────────────────────────
        string a = TempFilePolicy.NewPath("temp", dir), b = TempFilePolicy.NewPath("undo", dir), c = TempFilePolicy.NewPath("clean", dir);
        foreach (var f in new[] { a, b, c }) File.WriteAllText(f, "x");
        var set = new TempFileSet();
        set.Track(a); set.Track(b); set.Track(c); set.Track(null); set.Track("");
        Check("the set owns what it was given", set.Count == 3 && set.Contains(a), "");
        set.Release(c);
        int gone = set.DeleteAll(keep: b);
        Check("DeleteAll removes owned files but not the one handed to another window or a released one",
              gone == 1 && !File.Exists(a) && File.Exists(b) && File.Exists(c) && set.Count == 0, $"gone {gone}");
        Check("TryDelete is the single deleter: absent file is false, present file true",
              !TempFilePolicy.TryDelete(a) && TempFilePolicy.TryDelete(b) && !File.Exists(b), "");

        // ── Sanitizer UNC ──────────────────────────────────────────────────────────────────
        string unc = Sanitizer.Scrub(@"Could not open \\srv\Legal Docs\Smith divorce.pdf");
        Check("a UNC path with spaces is redacted whole",
              !unc.Contains("Smith") && !unc.Contains("divorce") && !unc.Contains("Docs") && unc.Contains("[path redacted]"), unc);
        string quoted = Sanitizer.Scrub("path \"\\\\srv\\Legal Docs\\Smith.pdf\" failed");
        Check("a quoted UNC path stops at the closing quote",
              !quoted.Contains("Smith") && quoted.Contains("failed"), quoted);
        string drive = Sanitizer.Scrub(@"C:\Users\Bob\My Docs\a.pdf");
        Check("a drive path with spaces is still redacted", !drive.Contains("Bob") && !drive.Contains("a.pdf"), drive);

        // ── privileged-path guard (pure parts) ─────────────────────────────────────────────
        Check("SYSTEM and Administrators own a trusted file",
              PrivilegedPathGuard.IsTrustedOwnerSid("S-1-5-18") && PrivilegedPathGuard.IsTrustedOwnerSid("S-1-5-32-544"), "");
        Check("an ordinary user SID, Users, Everyone and null do not",
              !PrivilegedPathGuard.IsTrustedOwnerSid("S-1-5-21-1-2-3-1001")
              && !PrivilegedPathGuard.IsTrustedOwnerSid("S-1-5-32-545")
              && !PrivilegedPathGuard.IsTrustedOwnerSid("S-1-1-0")
              && !PrivilegedPathGuard.IsTrustedOwnerSid(null), "");
        Check("a reparse point is recognised",
              PrivilegedPathGuard.IsReparsePoint(FileAttributes.Directory | FileAttributes.ReparsePoint)
              && !PrivilegedPathGuard.IsReparsePoint(FileAttributes.Directory), "");

        // ── protected-save impact ──────────────────────────────────────────────────────────
        var I = ProtectedSave.Impact.None;
        Check("an unprotected file is never a problem",
              ProtectedSave.Assess(false, false, false) == I && ProtectedSave.Assess(false, true, true) == I, "");
        Check("a protected file whose protection can't be reproduced is flagged as removed",
              ProtectedSave.Assess(true, false, false) == ProtectedSave.Impact.Removed
              && ProtectedSave.Assess(true, false, true) == ProtectedSave.Impact.Removed, "");
        Check("an AES file that can be re-keyed is flagged as downgraded",
              ProtectedSave.Assess(true, true, true) == ProtectedSave.Impact.Downgraded, "");
        Check("an RC4 file written back as RC4 is silent", ProtectedSave.Assess(true, true, false) == I, "");
        Check("the dialog text names the file and what will be written",
              ProtectedSave.ImpactMessage(ProtectedSave.Impact.Removed, "a.pdf", null).Contains("NO password")
              && ProtectedSave.ImpactMessage(ProtectedSave.Impact.Downgraded, "a.pdf", null).Contains("RC4")
              && ProtectedSave.ImpactMessage(ProtectedSave.Impact.Downgraded, "a.pdf", null).Contains("a.pdf"), "");

        try { Directory.Delete(dir, true); } catch { }
    }
}
