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
    /// destination configured anywhere, so <see cref="TryResolveOtlp"/> returns <c>null</c> and the
    /// app sends nothing to anyone — no separate build, no compile flag, and nothing to take on
    /// trust. A managed install gets an OTLP collector pushed to the registry and reports normally.
    /// </summary>
    /// <remarks>
    /// Precedence, first match wins:
    /// <list type="number">
    ///   <item><c>TDPDF_OTLP_ENDPOINT</c> / <c>TDPDF_OTLP_TOKEN</c> environment variables — for a
    ///   developer, or for someone self-hosting who wants to point this build at their own
    ///   collector without administrative rights. Deliberately not registry values under HKCU: a
    ///   user-writable production path is a redirection surface, and an environment variable is
    ///   obviously session-scoped to anyone reading it.</item>
    ///   <item><see cref="RegistryPath"/> under <c>HKEY_LOCAL_MACHINE</c> — the managed policy path,
    ///   delivered by an Intune configuration profile. <b>This is the one that matters:</b> it
    ///   makes rotation a policy push instead of a signed release of the whole application.</item>
    /// </list>
    /// A device-level opt-out (<c>TDPdf.exe /clear-telemetry</c>, which writes
    /// <see cref="TelemetryStore.DisabledMarkerPath"/>) outranks every source above.
    /// <para>
    /// <b>Application Insights was retired in 1.24.0.0</b> and OTLP is now the only destination.
    /// Fourteen days of dual export settled it: Azure received a lossy ~17% subset — installs and
    /// heartbeats, none of the interaction events — while the collector carried the full stream,
    /// including the evidence that identified the 1.23.x text-editor defect. With it went the DPAPI
    /// provisioning file as a source of destinations and the build-time-embedded key, which
    /// obfuscated a secret inside a binary shipped to end-user laptops and could not be rotated
    /// without a release.
    /// </para>
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

        /// <summary>Value name under <see cref="RegistryPath"/> for the OTLP collector base URL.</summary>
        internal const string RegistryOtlpEndpointValueName = "OtlpEndpoint";

        /// <summary>Value name under <see cref="RegistryPath"/> for the OTLP bearer token.</summary>
        internal const string RegistryOtlpTokenValueName = "OtlpToken";

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
        }

        /// <summary>
        /// The OTLP collector for this device, or <c>null</c> when none is configured.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The endpoint and the token are returned together or not at all — an endpoint with no
        /// token would produce a stream of 401s from every laptop in the fleet, which is worse
        /// than staying quiet.
        /// </para>
        /// </remarks>
        public static (string Endpoint, string Token)? TryResolveOtlp()
        {
            // The device-level opt-out outranks policy, so it is checked before anything is read.
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
        public static bool HasDestination() => TryResolveOtlp() is not null;

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
