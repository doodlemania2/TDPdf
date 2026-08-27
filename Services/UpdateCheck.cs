using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using TDPdf.Diagnostics;

namespace TDPdf.Services
{
    /// <summary>
    /// Notices that a newer TDPdf has been released and, on a managed device, asks Intune to come
    /// and fetch it instead of waiting for the next routine check-in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Intune polls on its own schedule — roughly every eight hours, and
    /// only if the device is awake and checking in at all. On 2026-08-27 a machine was found still
    /// running 1.23.5.0, six releases behind, whose user placed 32 text boxes in 28 minutes and got
    /// nothing from any of them, because every one of those releases had fixed the bug they were
    /// hitting and none had reached them. Nothing about that machine was broken; it simply had not
    /// been told. This closes that gap from the one place that always knows the device is in use:
    /// the application the person is sitting in front of.
    /// </para>
    /// <para>
    /// <b>What it deliberately does not do.</b> It never downloads, never installs, and never
    /// touches the binary. On a managed device the update path is Intune's and stays Intune's —
    /// this only kicks the sync, so the fleet keeps one delivery mechanism, one audit trail, and
    /// one set of assignments. An app that self-updated underneath Intune would fight the detection
    /// rule every cycle.
    /// </para>
    /// <para>
    /// <b>The check is a plain HTTPS GET to the public releases API</b> and carries nothing about
    /// the user or their documents — no identifier, no document state, not even the installed
    /// version (the comparison happens here, on the device). It is disclosed in
    /// <c>PRIVACY.md</c> and an administrator can switch it off fleet-wide with
    /// <c>Enabled = 0</c> under <see cref="PolicyPath"/>.
    /// </para>
    /// </remarks>
    internal static class UpdateCheck
    {
        private const string ReleasesApi =
            "https://api.github.com/repos/doodlemania2/TDPdf/releases/latest";

        /// <summary>Administrator switch, alongside the telemetry policy and for the same reasons.</summary>
        private const string PolicyPath = @"SOFTWARE\Policies\TDPdf\Update";

        /// <summary>Where the last-checked stamp lives. State, not policy — an uninstall may take it.</summary>
        private const string StatePath = @"Software\TDPdf";
        private const string LastCheckValue = "LastUpdateCheckUtc";

        /// <summary>Twice a day. Often enough that a fix lands the same working day, rare enough to be invisible.</summary>
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

        /// <summary>
        /// Runs the whole thing best-effort and off the UI thread. Never throws, never blocks
        /// startup, and does nothing at all when the policy switch is off or the interval has not
        /// elapsed.
        /// </summary>
        public static void StartBackgroundCheck(Version runningVersion)
        {
            _ = Task.Run(async () =>
            {
                try { await RunAsync(runningVersion).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    // An update check is a convenience. It may never be the reason anything fails.
                    Telemetry.TrackEvent("Update.CheckFailed", new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["Reason"] = ex.GetType().Name
                    });
                }
            });
        }

        private static async Task RunAsync(Version runningVersion)
        {
            if (!IsEnabledByPolicy()) return;
            if (!IntervalElapsed()) return;
            StampCheckedNow();   // stamp BEFORE the call, so a hanging endpoint cannot retry-loop

            Version? latest = await FetchLatestAsync().ConfigureAwait(false);
            if (latest is null || latest <= runningVersion) return;

            Telemetry.TrackEvent("Update.Available", new System.Collections.Generic.Dictionary<string, string>
            {
                // Both are TDPdf's own version numbers — no device or user information.
                ["Latest"] = latest.ToString(),
                ["Running"] = runningVersion.ToString()
            });

            string? enrollment = FindMdmEnrollmentId();
            if (enrollment is null) return;   // unmanaged: nothing to ask, and nothing to nag about yet

            bool triggered = TryTriggerMdmSync(enrollment);
            Telemetry.TrackEvent("Update.SyncTriggered", new System.Collections.Generic.Dictionary<string, string>
            {
                ["Result"] = triggered ? "ok" : "failed"
            });
        }

        private static bool IsEnabledByPolicy()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(PolicyPath);
                // Absent means enabled. An administrator opts OUT explicitly; a machine that has
                // never heard of the policy still gets its updates noticed.
                return key?.GetValue("Enabled") is not int disabled || disabled != 0;
            }
            catch { return true; }
        }

        private static bool IntervalElapsed()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StatePath);
                if (key?.GetValue(LastCheckValue) is not string stamp) return true;
                if (!DateTime.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var last)) return true;
                // A stamp in the future means the clock moved backwards; treat it as due rather
                // than letting a bad clock disable checking until it catches up.
                return DateTime.UtcNow - last >= CheckInterval || last > DateTime.UtcNow;
            }
            catch { return true; }
        }

        private static void StampCheckedNow()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(StatePath);
                key?.SetValue(LastCheckValue, DateTime.UtcNow.ToString("O"), RegistryValueKind.String);
            }
            catch { /* a missed stamp only costs one extra check */ }
        }

        private static async Task<Version?> FetchLatestAsync()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // GitHub rejects a request with no User-Agent outright.
            http.DefaultRequestHeaders.Add("User-Agent", "TDPdf");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

            using var response = await http.GetAsync(ReleasesApi).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tag)) return null;

            string? name = tag.GetString();
            if (string.IsNullOrWhiteSpace(name)) return null;
            return Version.TryParse(name.TrimStart('v', 'V'), out var parsed) ? parsed : null;
        }

        /// <summary>
        /// The device's MDM enrollment id, or <c>null</c> when the machine is not enrolled — which
        /// is also how "is this an Intune-managed device" is answered.
        /// </summary>
        private static string? FindMdmEnrollmentId()
        {
            try
            {
                using var enrollments = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments");
                if (enrollments is null) return null;

                foreach (string id in enrollments.GetSubKeyNames())
                {
                    using var e = enrollments.OpenSubKey(id);
                    if (e is null) continue;
                    // "MS DM Server" is the Intune/Windows MDM provider. EnrollmentState 1 is
                    // enrolled; anything else is a stale or partial record.
                    if (e.GetValue("ProviderID") as string == "MS DM Server"
                        && e.GetValue("EnrollmentState") is int state && state == 1)
                    {
                        return id;
                    }
                }
            }
            catch { /* not enrolled, or not permitted to look — either way, unmanaged */ }
            return null;
        }

        /// <summary>
        /// Runs the scheduled task the enrollment client created for its own check-ins. This is the
        /// same thing the Company Portal's "Sync" button does, and it is deliberately preferred to
        /// poking <c>deviceenroller.exe</c> directly: the task already exists, already runs with the
        /// right identity, and starting it needs no elevation.
        /// </summary>
        private static bool TryTriggerMdmSync(string enrollmentId)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe")
                {
                    // "Schedule #3" is the enrollment client's own periodic sync.
                    Arguments = $"/Run /TN \"\\Microsoft\\Windows\\EnterpriseMgmt\\{enrollmentId}\\Schedule #3 created by enrollment client\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p is null) return false;
                return p.WaitForExit(15_000) && p.ExitCode == 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// True when the EXE sitting at the install path is NEWER than the process that is running
        /// — i.e. an update landed underneath a running TDPdf and will take effect on next launch.
        /// </summary>
        /// <remarks>
        /// This is the visible half of the problem the installer's copy-over-lock handling solves.
        /// Windows will not let an executing file be overwritten, but it will let it be renamed
        /// aside, so <c>App.CopyExeOverLock</c> displaces the running image and drops the new build
        /// into place. That works — but the person carries on using the old one, unaware, until
        /// something restarts it. Saying so is the difference between an update that landed and an
        /// update that arrived.
        /// </remarks>
        public static bool IsRestartPending(string installedExePath, Version runningVersion)
        {
            try
            {
                if (!File.Exists(installedExePath)) return false;
                var info = FileVersionInfo.GetVersionInfo(installedExePath);
                return Version.TryParse(info.FileVersion, out var onDisk) && onDisk > runningVersion;
            }
            catch { return false; }
        }
    }
}
