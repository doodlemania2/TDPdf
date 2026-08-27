using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace TDPdf.Diagnostics
{
    /// <summary>
    /// The device-level telemetry opt-out marker, and retirement of the legacy at-rest store.
    ///
    /// Until 1.24.0.0 this type also held a DPAPI-encrypted Application Insights connection string
    /// in <c>%ProgramData%\TDPdf\telemetry.dat</c>, written by <c>TDPdf.exe /set-telemetry</c>.
    /// Application Insights was retired in that release and the OTLP collector is provisioned by
    /// Intune policy instead, so there is no longer a secret to store. What survives is the
    /// <c>telemetry.disabled</c> sentinel, plus <see cref="RemoveLegacyStore"/> to delete the dead
    /// ciphertext from machines that upgrade.
    /// </summary>
    /// <remarks>
    /// The sentinel is the strongest opt-out in the product: <see cref="TelemetryConfig"/> checks it
    /// before reading any destination, so it outranks the managed policy rather than racing it. An
    /// administrator taking one machine out of reporting should not have to wait for a profile
    /// change, nor be silently overridden by one.
    /// </remarks>
    internal static class TelemetryStore
    {
        private static readonly string s_dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "TDPdf");

        /// <summary>Pre-1.24.0.0 store. Retained only so it can be deleted.</summary>
        private static string LegacyStorePath => System.IO.Path.Combine(s_dir, "telemetry.dat");

        /// <summary>
        /// Sentinel file that, when present, disables all telemetry on this device regardless of
        /// what policy configures. Written by <see cref="MarkDisabled"/> when an administrator runs
        /// <c>TDPdf.exe /clear-telemetry</c>; respected by <see cref="IsDisabled"/>, which
        /// <see cref="TelemetryConfig.TryResolveOtlp"/> consults before reading any destination.
        /// </summary>
        public static string DisabledMarkerPath => System.IO.Path.Combine(s_dir, "telemetry.disabled");

        /// <summary>
        /// True if telemetry has been explicitly disabled on this device via
        /// <c>/clear-telemetry</c>. Destination resolution stops when this returns true.
        /// </summary>
        public static bool IsDisabled()
        {
            try { return File.Exists(DisabledMarkerPath); }
            catch { return false; }
        }

        /// <summary>
        /// Delete a pre-1.24.0.0 <c>telemetry.dat</c> if one is present. Best-effort and never
        /// throws: it runs on every launch, and a machine where it cannot be removed (locked file,
        /// non-elevated user) must still start normally. The elevated <c>/install</c> path calls it
        /// too, which is where it will usually succeed.
        /// </summary>
        public static void RemoveLegacyStore() => Clear();

        /// <summary>Remove the legacy store file. No-op if absent. Never throws.</summary>
        public static void Clear()
        {
            try
            {
                if (File.Exists(LegacyStorePath))
                    File.Delete(LegacyStorePath);
            }
            catch { /* swallow — caller logs */ }
        }

        /// <summary>
        /// Write the disabled-marker sentinel so the next launch will
        /// not re-provision from the build-time-embedded key. Best
        /// effort — failure is swallowed.
        /// </summary>
        public static void MarkDisabled()
        {
            try
            {
                EnsureHardenedDirectory();
                File.WriteAllText(DisabledMarkerPath, "disabled\r\n");
                HardenFileAcl(DisabledMarkerPath);
            }
            catch { /* swallow */ }
        }

        /// <summary>
        /// Clear the disabled-marker sentinel so the next launch can
        /// auto-provision again from the build-time-embedded key.
        /// </summary>
        public static void ClearDisabledMarker()
        {
            try
            {
                if (File.Exists(DisabledMarkerPath))
                    File.Delete(DisabledMarkerPath);
            }
            catch { /* swallow */ }
        }

        // ============================================================
        // ACL hardening — explicit, not inherited.
        //
        // Rubber-duck blocking finding #3: relying on default
        // %ProgramData% ACLs is risky because a non-admin user can
        // pre-create C:\ProgramData\TDPdf before the installer runs
        // and influence ownership. Setting an explicit DACL here is
        // idempotent and overrides any pre-existing weakness.
        // ============================================================

        private static void EnsureHardenedDirectory()
        {
            if (!Directory.Exists(s_dir))
                Directory.CreateDirectory(s_dir);

            try
            {
                var info = new DirectoryInfo(s_dir);
                var acl = new DirectorySecurity();
                acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var users = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

                acl.AddAccessRule(new FileSystemAccessRule(
                    system, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));

                acl.AddAccessRule(new FileSystemAccessRule(
                    admins, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));

                acl.AddAccessRule(new FileSystemAccessRule(
                    users, FileSystemRights.ReadAndExecute,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));

                info.SetAccessControl(acl);
            }
            catch
            {
                // ACL hardening is best-effort. If we lack the right
                // (e.g. the caller is not elevated), the write above will
                // also fail at file-write time and surface a useful
                // error. Don't mask that with an ACL exception here.
            }
        }

        private static void HardenFileAcl(string path)
        {
            try
            {
                var info = new FileInfo(path);
                var acl = new FileSecurity();
                acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var users = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

                acl.AddAccessRule(new FileSystemAccessRule(
                    system, FileSystemRights.FullControl,
                    AccessControlType.Allow));
                acl.AddAccessRule(new FileSystemAccessRule(
                    admins, FileSystemRights.FullControl,
                    AccessControlType.Allow));

                // Read-only to non-admins. The GUI process running as
                // the interactive user still decrypts at startup.
                acl.AddAccessRule(new FileSystemAccessRule(
                    users, FileSystemRights.Read,
                    AccessControlType.Allow));

                info.SetAccessControl(acl);
            }
            catch
            {
                // Same rationale as EnsureHardenedDirectory.
            }
        }
    }
}
