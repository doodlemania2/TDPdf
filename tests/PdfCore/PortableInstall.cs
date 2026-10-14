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
    }
}
