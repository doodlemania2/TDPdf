namespace TDPdf.Services
{
    /// <summary>
    /// Where, if anywhere, TDPdf is already installed on this machine, as seen from a portable copy.
    /// Mirrors App's private install scopes so the decision below can be tested off Windows.
    /// </summary>
    internal enum ExistingInstall
    {
        /// <summary>No install marker in either hive: a never-installed machine.</summary>
        None,

        /// <summary>Installed for this user under %LOCALAPPDATA%\Programs\TDPdf (HKCU marker).</summary>
        PerUser,

        /// <summary>
        /// Installed for all users under Program Files (HKLM marker) — in practice by Intune running
        /// the installer as SYSTEM.
        /// </summary>
        PerMachine
    }

    /// <summary>What the portable badge's button offers when clicked.</summary>
    internal enum PortableInstallOffer
    {
        /// <summary>Nothing installed: the original "Install TDPdf..." first-run offer, unchanged.</summary>
        FreshInstall,

        /// <summary>
        /// A machine-wide install exists. It is the organisation's, kept current by Intune; a portable
        /// copy running as the user can neither overwrite nor remove it without elevation, and must not
        /// fight the management channel if it could. Explain, offer nothing destructive.
        /// </summary>
        Managed,

        /// <summary>This copy is newer than the per-user install.</summary>
        Upgrade,

        /// <summary>
        /// Same version, or the installed version could not be read (marker present, exe missing or
        /// unreadable) — reinstalling is then a repair.
        /// </summary>
        Reinstall,

        /// <summary>
        /// This copy is OLDER than the per-user install. Still allowed, but only as an explicit,
        /// non-default choice.
        /// </summary>
        Downgrade
    }

    /// <summary>
    /// The portable copy's "already installed?" decision, as a pure function (ported in idea from
    /// upstream KillerPDF's launcher, which offers Upgrade / Reinstall / Uninstall / Cancel when it
    /// finds an existing install).
    ///
    /// Before this, the portable badge offered "Install TDPdf..." whenever the running exe was not the
    /// installed one — including on a machine where TDPdf was already installed, where it would then
    /// silently overwrite that install with whatever version the portable copy happened to be.
    /// </summary>
    internal static class PortableInstallPolicy
    {
        /// <summary>
        /// Decides what to offer. <paramref name="installed"/> is the installed exe's FileVersion, or
        /// <c>null</c> when it could not be read.
        /// </summary>
        public static PortableInstallOffer Decide(ExistingInstall existing, Version? installed, Version running)
        {
            switch (existing)
            {
                case ExistingInstall.None:       return PortableInstallOffer.FreshInstall;
                case ExistingInstall.PerMachine: return PortableInstallOffer.Managed;
            }

            if (installed is null) return PortableInstallOffer.Reinstall;
            int cmp = Normalize(running).CompareTo(Normalize(installed));
            return cmp > 0 ? PortableInstallOffer.Upgrade
                 : cmp < 0 ? PortableInstallOffer.Downgrade
                 : PortableInstallOffer.Reinstall;
        }

        /// <summary>
        /// The badge button's caption. Kept short — it sits centred in the status bar beside the
        /// PORTABLE tag.
        /// </summary>
        public static string BadgeLabel(PortableInstallOffer offer, Version? installed)
        {
            string v = installed is null ? "" : $" v{Display(installed)}";
            return offer switch
            {
                PortableInstallOffer.FreshInstall => "Install TDPdf...",
                PortableInstallOffer.Managed      => $"Installed{v} (managed)...",
                _                                 => $"Installed{v} — Manage...",
            };
        }

        /// <summary>
        /// The caption of the dialog's install-side button, or <c>null</c> when the offer has none
        /// (fresh install keeps its own prompt; a managed install offers nothing destructive).
        /// </summary>
        public static string? ActionLabel(PortableInstallOffer offer) => offer switch
        {
            PortableInstallOffer.Upgrade   => "Upgrade",
            PortableInstallOffer.Reinstall => "Reinstall",
            PortableInstallOffer.Downgrade => "Replace with older",
            _                              => null,
        };

        /// <summary>Whether the dialog may offer Uninstall. Only an install this user owns.</summary>
        public static bool CanUninstall(PortableInstallOffer offer) =>
            offer is PortableInstallOffer.Upgrade
                  or PortableInstallOffer.Reinstall
                  or PortableInstallOffer.Downgrade;

        /// <summary>A version as shown to the user: always four parts, matching Add/Remove Programs.</summary>
        public static string Display(Version v) => Normalize(v).ToString();

        // `new Version("2.0")` reports Build and Revision as -1, which compares LESS than 0 — so
        // 2.0 and 2.0.0.0 would read as different versions. Pin the unset parts to zero.
        private static Version Normalize(Version v) =>
            new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    }
}
