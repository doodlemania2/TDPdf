using TDPdf.Services;

/// <summary>
/// Pins the portable copy's "already installed?" decision: <see cref="PortableInstallPolicy"/>.
/// </summary>
/// <remarks>
/// Each wrong answer here is a different kind of damage, not a cosmetic wobble:
///
///   * a machine-wide install offered as Upgrade / Uninstall would have a portable copy, running as
///     the user, try to overwrite or tear down the organisation's Intune-managed install;
///   * an older portable copy offered as "Upgrade" (or as the default button) would quietly
///     downgrade the installed TDPdf;
///   * a never-installed machine NOT getting the plain fresh-install offer would change first run.
///
/// The version comparison is checked with mixed-length versions too, because <c>new Version("2.0")</c>
/// carries -1 for its unset parts and would otherwise compare as older than 2.0.0.0.
/// </remarks>
internal static class PortableInstall
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("── portable copy vs. an existing install ─────────────────────");

        var v2   = new Version(2, 0, 0, 0);
        var v21  = new Version(2, 1, 0, 0);
        var v201 = new Version(2, 0, 0, 1);

        PortableInstallOffer D(ExistingInstall e, Version? installed, Version running) =>
            PortableInstallPolicy.Decide(e, installed, running);

        // ── which offer ─────────────────────────────────────────────────────────────────────
        Check("nothing installed → the unchanged fresh-install offer",
              D(ExistingInstall.None, null, v2) == PortableInstallOffer.FreshInstall, "");
        Check("machine-wide install → managed, even when this copy is newer",
              D(ExistingInstall.PerMachine, v2, v21) == PortableInstallOffer.Managed, "");
        Check("machine-wide install with an unreadable version → still managed",
              D(ExistingInstall.PerMachine, null, v2) == PortableInstallOffer.Managed, "");
        Check("per-user, this copy newer → Upgrade",
              D(ExistingInstall.PerUser, v2, v21) == PortableInstallOffer.Upgrade, "");
        Check("per-user, a revision-only bump still counts as newer → Upgrade",
              D(ExistingInstall.PerUser, v2, v201) == PortableInstallOffer.Upgrade, "");
        Check("per-user, same version → Reinstall",
              D(ExistingInstall.PerUser, v2, v2) == PortableInstallOffer.Reinstall, "");
        Check("per-user, 2.0 and 2.0.0.0 are the same version → Reinstall",
              D(ExistingInstall.PerUser, new Version(2, 0), v2) == PortableInstallOffer.Reinstall, "");
        Check("per-user, this copy OLDER → Downgrade, never Upgrade",
              D(ExistingInstall.PerUser, v21, v2) == PortableInstallOffer.Downgrade, "");
        Check("per-user, installed version unreadable → Reinstall (a repair)",
              D(ExistingInstall.PerUser, null, v2) == PortableInstallOffer.Reinstall, "");

        // ── what the dialog may do ──────────────────────────────────────────────────────────
        Check("managed install: no install-side action",
              PortableInstallPolicy.ActionLabel(PortableInstallOffer.Managed) is null, "");
        Check("managed install: no Uninstall",
              !PortableInstallPolicy.CanUninstall(PortableInstallOffer.Managed), "");
        Check("fresh install: no Uninstall",
              !PortableInstallPolicy.CanUninstall(PortableInstallOffer.FreshInstall), "");
        Check("per-user install: Uninstall offered for every comparison",
              PortableInstallPolicy.CanUninstall(PortableInstallOffer.Upgrade)
              && PortableInstallPolicy.CanUninstall(PortableInstallOffer.Reinstall)
              && PortableInstallPolicy.CanUninstall(PortableInstallOffer.Downgrade), "");
        string? down = PortableInstallPolicy.ActionLabel(PortableInstallOffer.Downgrade);
        Check("the downgrade button says older, not upgrade/reinstall",
              down is not null && down.Contains("older", StringComparison.OrdinalIgnoreCase), down ?? "(null)");

        // ── badge caption ───────────────────────────────────────────────────────────────────
        string fresh = PortableInstallPolicy.BadgeLabel(PortableInstallOffer.FreshInstall, null);
        Check("fresh-install badge is unchanged", fresh == "Install TDPdf...", fresh);
        string user = PortableInstallPolicy.BadgeLabel(PortableInstallOffer.Upgrade, new Version(2, 0));
        Check("per-user badge names the installed version in four parts", user == "Installed v2.0.0.0 — Manage...", user);
        string managed = PortableInstallPolicy.BadgeLabel(PortableInstallOffer.Managed, v2);
        Check("managed badge says managed", managed.Contains("managed"), managed);
        string unknown = PortableInstallPolicy.BadgeLabel(PortableInstallOffer.Reinstall, null);
        Check("unreadable version leaves no dangling 'v'", unknown == "Installed — Manage...", unknown);

        Uninstall(Check);
    }

    // The deferred-delete script and the "is TDPdf still running from there" decision.
    private static void Uninstall(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("── uninstall: deferred delete + running-instance check ───────");

        string script = UninstallPolicy.DeferredDeleteScript;
        Check("delete script is pure ASCII (cmd reads it in the OEM code page)",
              script.All(c => c < 128), "");
        Check("delete script names no folder; it reads the environment variable",
              !script.Contains(":\\") && script.Contains("\"%" + UninstallPolicy.DirVariable + "%\""), "");
        Check("delete script refuses to run without the variable (no rmdir of \"\")",
              script.Contains("if not defined " + UninstallPolicy.DirVariable + " goto done"), "");
        Check("delete script turns delayed expansion off (a ! in the path stays literal)",
              script.Contains("DisableDelayedExpansion"), "");
        Check("delete script uses CRLF line endings throughout",
              !script.Replace("\r\n", "").Contains('\n'), "");

        string args = UninstallPolicy.DeferredDeleteArguments("tdpdf_uninstall_0123abcd.bat");
        Check("cmd arguments name only the batch file, relative to the temp folder",
              args == "/d /v:off /c .\\tdpdf_uninstall_0123abcd.bat", args);
        bool threw = false;
        try { UninstallPolicy.DeferredDeleteArguments("C:\\Users\\Jörg 100%\\x.bat"); }
        catch (ArgumentException) { threw = true; }
        Check("a path, non-ASCII letter or % never reaches cmd's command line", threw, "");

        string dir = "C:\\Users\\Jörg\\AppData\\Local\\Programs\\TDPdf";
        Check("the installed exe is inside the install folder",
              UninstallPolicy.IsInside(dir + "\\TDPdf.exe", dir), "");
        Check("inside-check ignores case and a trailing slash on the folder",
              UninstallPolicy.IsInside(dir.ToUpperInvariant() + "\\tdpdf.EXE", dir + "\\"), "");
        Check("a sibling folder with the same prefix is not inside",
              !UninstallPolicy.IsInside("C:\\Users\\Jörg\\AppData\\Local\\Programs\\TDPdf2\\TDPdf.exe", dir), "");
        Check("a portable copy elsewhere does not block uninstall",
              !UninstallPolicy.BlocksUninstall("C:\\Users\\Jörg\\Downloads\\TDPdf.exe", dir), "");
        Check("a copy running from the install folder blocks uninstall",
              UninstallPolicy.BlocksUninstall(dir + "\\TDPdf.exe", dir), "");
        Check("a TDPdf whose path cannot be read counts as running",
              UninstallPolicy.BlocksUninstall(null, dir), "");
        Check("no shortcut target is not inside anything",
              !UninstallPolicy.IsInside(null, dir) && !UninstallPolicy.IsInside("", dir), "");
    }
}
