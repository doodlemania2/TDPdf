using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace TDPdf.Services
{
    /// <summary>
    /// Where the "what's new" feature keeps its state and reads its policy: the last version whose
    /// notes were shown, the user's "Show what's new after updates" choice, and the administrator
    /// switch. Everything is best effort and never throws, because this may never be the reason a
    /// launch fails.
    /// </summary>
    /// <remarks>
    /// State lives under <c>HKCU\Software\TDPdf</c>, beside the update-check stamp, rather than in
    /// the per-version <c>user.config</c>: a settings file that is born again with each version
    /// would make every update look like a fresh install. The administrator switch is
    /// <c>HKLM\SOFTWARE\Policies\TDPdf\WhatsNew</c> → <c>Enabled</c> (DWORD), under
    /// <c>SOFTWARE\Policies\</c> for the reason <see cref="UpdateCheck"/> gives.
    /// </remarks>
    internal static class WhatsNewState
    {
        private const string StatePath = @"Software\TDPdf";
        private const string PolicyPath = @"SOFTWARE\Policies\TDPdf\WhatsNew";
        private const string LastSeenValue = "WhatsNewLastSeenVersion";
        private const string ShowValue = "ShowWhatsNew";
        private const string UpdateStampValue = "LastUpdateCheckUtc";   // written by UpdateCheck since 2.x

        /// <summary>The versions this build carries notes for (embedded as TDPdf.ReleaseNotes.v&lt;version&gt;.md).</summary>
        internal static IReadOnlyList<Version> AvailableVersions()
        {
            try
            {
                return Assembly.GetExecutingAssembly().GetManifestResourceNames()
                    .Select(WhatsNewPolicy.VersionFromResourceName)
                    .Where(v => v is not null && v >= WhatsNewPolicy.OldestNotes)
                    .Select(v => v!)
                    .ToList();
            }
            catch { return Array.Empty<Version>(); }
        }

        /// <summary>One version's notes, or null when this build has none for it.</summary>
        internal static string? LoadEmbeddedNotes(Version version)
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("TDPdf.ReleaseNotes.v" + version + ".md");
                if (stream is null) return null;
                using var reader = new StreamReader(stream);
                string text = reader.ReadToEnd();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch { return null; }
        }

        /// <summary>False when an administrator turned the automatic popup off. Absent means allowed.</summary>
        internal static bool IsAllowedByPolicy()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(PolicyPath);
                return key?.GetValue("Enabled") is not int v || v != 0;
            }
            catch { return true; }
        }

        /// <summary>The user's "Show what's new after updates" box. On by default.</summary>
        internal static bool ShowAfterUpdates
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(StatePath);
                    return key?.GetValue(ShowValue) is not int v || v != 0;
                }
                catch { return true; }
            }
            set
            {
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(StatePath);
                    key?.SetValue(ShowValue, value ? 1 : 0, RegistryValueKind.DWord);
                }
                catch { /* the choice still holds for this session in the window that set it */ }
            }
        }

        /// <summary>
        /// The versions whose notes to open at this launch (none when they should not open), then records the running version so the
        /// next launch will not. Call once, after the main window is shown. Runs before the update
        /// check's first stamp so "has run before" is still truthful on a fresh install.
        /// </summary>
        internal static IReadOnlyList<Version> VersionsToShowAtLaunchAndRecord(Version running)
        {
            try
            {
                string? lastSeen = null;
                bool hasPriorUse = false;
                using (var key = Registry.CurrentUser.OpenSubKey(StatePath))
                {
                    lastSeen = key?.GetValue(LastSeenValue) as string;
                    hasPriorUse = key?.GetValue(UpdateStampValue) is not null;
                }

                bool show = WhatsNewPolicy.ShouldShow(lastSeen, hasPriorUse, running, ShowAfterUpdates, IsAllowedByPolicy());
                if (lastSeen != WhatsNewPolicy.VersionToStore(running))
                {
                    using var key = Registry.CurrentUser.CreateSubKey(StatePath);
                    key?.SetValue(LastSeenValue, WhatsNewPolicy.VersionToStore(running), RegistryValueKind.String);
                }
                if (!show) return Array.Empty<Version>();
                return WhatsNewPolicy.VersionsToShow(AvailableVersions(), lastSeen, running);
            }
            catch { return Array.Empty<Version>(); }
        }
    }
}
