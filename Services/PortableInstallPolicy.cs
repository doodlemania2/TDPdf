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

namespace TDPdf.Services
{
    /// <summary>
    /// The pure parts of uninstall: which running processes block it, and the deferred-delete
    /// script. Kept off Windows APIs so tests/PdfCore can pin them.
    /// </summary>
    internal static class UninstallPolicy
    {
        /// <summary>
        /// Environment variable that carries the install folder to the deferred-delete script.
        /// </summary>
        public const string DirVariable = "TDPDF_UNINSTALL_DIR";

        /// <summary>
        /// The deferred-delete batch. It contains no path at all: cmd reads a batch file in the
        /// OEM code page, so a UTF-8 path with non-ASCII letters (a profile like C:\Users\Jörg)
        /// came out mangled and the rmdir silently failed, and a % in the path was expanded as a
        /// variable. The folder arrives in <see cref="DirVariable"/> instead: cmd keeps the
        /// environment in Unicode and expands a variable's value once, never re-scanning it, so
        /// the path reaches rmdir exactly as written. Pure ASCII, so the code page is irrelevant.
        /// </summary>
        /// <remarks>
        /// Retries once a second for up to a minute: antivirus and the exiting process hold the
        /// exe briefly (upstream KillerPDF #434 found the same lock).
        /// </remarks>
        public static readonly string DeferredDeleteScript =
            "@echo off\r\n" +
            "setlocal DisableDelayedExpansion\r\n" +
            "if not defined " + DirVariable + " goto done\r\n" +
            "set tries=0\r\n" +
            ":retry\r\n" +
            "ping -n 2 127.0.0.1 >nul\r\n" +
            "rmdir /s /q \"%" + DirVariable + "%\" 2>nul\r\n" +
            "if not exist \"%" + DirVariable + "%\\\" goto done\r\n" +
            "set /a tries+=1\r\n" +
            "if %tries% lss 60 goto retry\r\n" +
            ":done\r\n" +
            "del \"%~f0\"\r\n";

        /// <summary>
        /// cmd.exe arguments that run <paramref name="batchFileName"/> from the working directory
        /// (the temp folder, set by the caller). Only the bare, ASCII file name appears on the
        /// command line, where cmd would otherwise expand % in a temp path such as a profile
        /// folder named with a percent sign. /d skips AutoRun commands, /v:off keeps ! literal.
        /// </summary>
        public static string DeferredDeleteArguments(string batchFileName)
        {
            foreach (char c in batchFileName)
            {
                bool safe = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                              or '_' or '-' or '.';
                if (!safe)
                    throw new ArgumentException("The batch file name must be plain ASCII.", nameof(batchFileName));
            }
            return $"/d /v:off /c .\\{batchFileName}";
        }

        /// <summary>
        /// True when <paramref name="path"/> is <paramref name="directory"/> or lies inside it.
        /// Windows paths, compared case-insensitively; a sibling such as "TDPdf2" is not inside
        /// "TDPdf".
        /// </summary>
        public static bool IsInside(string? path, string directory)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(directory)) return false;
            string dir = directory.Replace('/', '\\').TrimEnd('\\');
            string p = path.Replace('/', '\\');
            if (p.Length == dir.Length) return string.Equals(p, dir, StringComparison.OrdinalIgnoreCase);
            return p.Length > dir.Length
                && p[dir.Length] == '\\'
                && p.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether a running TDPdf process blocks uninstalling <paramref name="installDir"/>.
        /// <paramref name="imagePath"/> is its exe path, or <c>null</c> when it could not be read
        /// (another account's process): that one counts as running, since deleting the folder
        /// under it would fail anyway and a false "close TDPdf" beats a half-done uninstall.
        /// </summary>
        public static bool BlocksUninstall(string? imagePath, string installDir) =>
            imagePath is null || IsInside(imagePath, installDir);
    }
}
