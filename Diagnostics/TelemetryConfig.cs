using System;
using Microsoft.Win32;

namespace TDPdf.Diagnostics
{
    /// <summary>
    /// Resolves <em>where</em> telemetry is sent, separately from <em>whether</em> it is sent.
    /// <see cref="Telemetry"/> needs both: a destination from here, and consent from
    /// <see cref="TDPdf.Properties.Settings.TelemetryEnabled"/>.
    ///
    /// The split is what makes this app safe to open-source. A public build ships with no
    /// destination configured anywhere, so <see cref="TryResolveConnectionString"/> returns
    /// <c>null</c> and the app sends nothing to anyone — no separate build, no compile flag, and
    /// nothing to take on trust. A managed install gets a destination pushed to the registry and
    /// reports normally.
    /// </summary>
    /// <remarks>
    /// Precedence, first match wins:
    /// <list type="number">
    ///   <item><c>TDPDF_TELEMETRY_CONNECTION</c> environment variable — for a developer, or for
    ///   someone self-hosting who wants to point this build at their own collector without
    ///   administrative rights. Deliberately not a registry value under HKCU: a user-writable
    ///   production path is a redirection surface, and an environment variable is obviously
    ///   session-scoped to anyone reading it.</item>
    ///   <item><see cref="RegistryPath"/> under <c>HKEY_LOCAL_MACHINE</c> — the managed policy path,
    ///   delivered by an Intune configuration profile. <b>This is the one that matters:</b> it
    ///   makes rotation a policy push instead of a signed release of the whole application, which
    ///   is the defect in the build-time-embedded key it replaces.</item>
    ///   <item><b>Retired 2026-08-20:</b> the DPAPI provisioning file and the build-time-embedded
    ///   constant are no longer consulted for the Application Insights destination. Those were the
    ///   two sources keeping the fleet reporting to Azure, so no longer reading them IS the
    ///   cutover. Application Insights is now reachable only by explicitly setting one of the two
    ///   sources above, which is also the rollback path.</item>
    /// </list>
    /// A device-level opt-out (<c>TDPdf.exe /clear-telemetry</c>, which writes
    /// <see cref="TelemetryStore.DisabledMarkerPath"/>) outranks every source above.
    /// </remarks>
    internal static class TelemetryConfig
    {
        /// <summary>
        /// Registry key an Intune configuration profile writes the destination into.
        /// </summary>
        /// <remarks>
        /// Under <c>SOFTWARE\Policies\</c> for two reasons, both load-bearing.
        /// <para>
        /// It is the Windows convention for administrator-pushed policy, so an admin reading the
        /// hive can tell at a glance that this is managed configuration rather than something the
        /// application wrote about itself.
        /// </para>
        /// <para>
        /// More concretely: <c>App.Uninstall</c> runs
        /// <c>DeleteSubKeyTree(@"Software\TDPdf")</c> against BOTH hives. Putting the destination
        /// under <c>SOFTWARE\TDPdf\</c> would mean an uninstall silently destroyed the
        /// organisation's pushed configuration along with the app's own keys — the Intune profile
        /// would eventually re-apply, but reporting would be dark until it did, and nothing would
        /// say why. Policy is not the application's to delete.
        /// </para>
        /// </remarks>
        internal const string RegistryPath = @"SOFTWARE\Policies\TDPdf\Telemetry";

        /// <summary>Value name under <see cref="RegistryPath"/> for the Application Insights destination.</summary>
        internal const string RegistryValueName = "ConnectionString";

        /// <summary>Value name under <see cref="RegistryPath"/> for the OTLP collector base URL.</summary>
        internal const string RegistryOtlpEndpointValueName = "OtlpEndpoint";

        /// <summary>Value name under <see cref="RegistryPath"/> for the OTLP bearer token.</summary>
        internal const string RegistryOtlpTokenValueName = "OtlpToken";

        /// <summary>Environment-variable override, for developers and self-hosters.</summary>
        internal const string EnvironmentVariableName = "TDPDF_TELEMETRY_CONNECTION";

        /// <summary>Environment-variable override for the OTLP collector base URL.</summary>
        internal const string OtlpEndpointEnvironmentVariableName = "TDPDF_OTLP_ENDPOINT";

        /// <summary>Environment-variable override for the OTLP bearer token.</summary>
        internal const string OtlpTokenEnvironmentVariableName = "TDPDF_OTLP_TOKEN";

        /// <summary>Where the live destination came from. For the Settings dialog and support.</summary>
        internal enum Source
        {
            None,
            Environment,
            Registry,
            ProvisioningFile,
            EmbeddedKey,
        }

        /// <summary>
        /// The destination for this device, or <c>null</c> when none is configured — in which case
        /// telemetry is inert no matter what the user setting says. Never throws.
        /// </summary>
        public static string? TryResolveConnectionString() => TryResolve(out _);

        /// <summary>
        /// As <see cref="TryResolveConnectionString"/>, also reporting which source supplied the
        /// value so the Settings dialog can tell the user whether telemetry is actually wired up.
        /// </summary>
        public static string? TryResolve(out Source source)
        {
            source = Source.None;

            // An explicit device-level opt-out beats every source below. Checked first so a
            // machine that has been opted out never even reads a destination.
            if (TelemetryStore.IsDisabled()) return null;

            try
            {
                string? env = Environment.GetEnvironmentVariable(EnvironmentVariableName);
                if (!string.IsNullOrWhiteSpace(env))
                {
                    source = Source.Environment;
                    return env.Trim();
                }
            }
            catch { /* environment access can throw under restricted hosts */ }

            string? fromRegistry = TryReadRegistryValue(RegistryValueName);
            if (!string.IsNullOrWhiteSpace(fromRegistry))
            {
                source = Source.Registry;
                return fromRegistry;
            }

            // RETIRED 2026-08-20: the DPAPI provisioning file and the build-time-embedded key are
            // no longer consulted. Those two are what kept every machine in the fleet reporting to
            // Application Insights, so ignoring them is what actually performs the cutover — the
            // fleet goes quiet on Azure at the moment this build lands, without needing a script
            // to visit 30 machines and delete a file.
            //
            // The code path above is deliberately LEFT INTACT rather than deleted. Application
            // Insights is now opt-in through the two explicit sources only, so restoring it is
            // setting one policy value:
            //
            //   HKLM\SOFTWARE\Policies\TDPdf\Telemetry\ConnectionString
            //
            // That is the rollback if the OTLP path turns out to have a problem on real devices —
            // a policy push, not a release. Delete the rest of this machinery (EmbeddedTelemetry,
            // /set-telemetry, the embed step in release.yml) once OTLP has proven itself over a
            // few weeks, not before: an unused code path is cheap, and being blind is not.
            return null;
        }

        /// <summary>
        /// The OTLP collector for this device, or <c>null</c> when none is configured.
        /// </summary>
        /// <remarks>
        /// Resolved independently of the Application Insights destination, and deliberately so:
        /// this is a dual-export migration, and either destination may be present without the
        /// other. During the migration both are configured; afterwards, retiring Application
        /// Insights is a matter of clearing one registry value rather than shipping a build.
        /// <para>
        /// The endpoint and the token are returned together or not at all — an endpoint with no
        /// token would produce a stream of 401s from every laptop in the fleet, which is worse
        /// than staying quiet.
        /// </para>
        /// </remarks>
        public static (string Endpoint, string Token)? TryResolveOtlp()
        {
            // Same device-level opt-out that outranks the Application Insights destination.
            if (TelemetryStore.IsDisabled()) return null;

            string? endpoint = null;
            string? token = null;

            try
            {
                endpoint = Environment.GetEnvironmentVariable(OtlpEndpointEnvironmentVariableName);
                token = Environment.GetEnvironmentVariable(OtlpTokenEnvironmentVariableName);
            }
            catch { /* environment access can throw under restricted hosts */ }

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(token))
            {
                endpoint = TryReadRegistryValue(RegistryOtlpEndpointValueName);
                token = TryReadRegistryValue(RegistryOtlpTokenValueName);
            }

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(token))
                return null;

            return (endpoint.Trim().TrimEnd('/'), token.Trim());
        }

        /// <summary>True when this build has somewhere to send telemetry at all.</summary>
        public static bool HasDestination() =>
            TryResolveConnectionString() is not null || TryResolveOtlp() is not null;

        /// <summary>
        /// Reads the managed destination. Returns <c>null</c> when the key is absent, which is the
        /// normal case for a public build and for a machine whose profile has not arrived yet.
        /// </summary>
        private static string? TryReadRegistryValue(string valueName)
        {
            try
            {
                // Explicit 64-bit view: TDPdf publishes win-x64, but being explicit keeps the
                // lookup pointing at the same key an Intune profile writes even if the process
                // is ever hosted 32-bit, where WOW64 would silently redirect to Wow6432Node.
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = hklm.OpenSubKey(RegistryPath, writable: false);
                if (key is null) return null;

                return key.GetValue(valueName) as string is { } value && !string.IsNullOrWhiteSpace(value)
                    ? value.Trim()
                    : null;
            }
            catch
            {
                // A locked-down or malformed hive must degrade to "no destination", never crash.
                return null;
            }
        }
    }
}
