using System;
using Microsoft.Win32;

namespace TDPdf.Services
{
    /// <summary>
    /// Resolves <em>which</em> Documenso instance Electronic Signature talks to, and whether the
    /// feature exists on this device at all. <see cref="HasEndpoint"/> is the single gate the whole
    /// feature hangs off — the File menu, Settings and every handler ask it and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Modelled on <see cref="TDPdf.Diagnostics.TelemetryConfig"/>, with two deliberate
    /// differences. The URL is not a secret, so there is a user-editable tier that telemetry does
    /// not have. And — the 2026-10-10 decision that reversed "no compiled-in endpoint" — there is
    /// a built-in default, <see cref="DefaultBaseUrl"/>, so Electronic Signature ships switched
    /// <em>on</em>: the headline of 2.0 should not need a registry key to be found.
    /// </para>
    /// <para>
    /// Precedence, first match wins:
    /// <list type="number">
    ///   <item><c>Enabled = 0</c> under <see cref="PolicyPath"/> — no endpoint, unconditionally.
    ///   This is the administrator's kill switch and it outranks everything, including a URL the
    ///   user typed and the built-in default.</item>
    ///   <item><see cref="BaseUrlEnvironmentVariableName"/> — developers, or someone pointing a
    ///   public build at their own instance without admin rights. Session-scoped and obvious to
    ///   anyone reading it, the same argument TelemetryConfig makes for not using HKCU.</item>
    ///   <item><c>Settings.Default.DocumensoBaseUrl</c> — the user's own choice, but only when no
    ///   policy URL is set or the policy allows overriding it (<c>AllowUserOverride</c> absent or
    ///   non-zero). An organisation that pins its instance sets <c>AllowUserOverride = 0</c>.</item>
    ///   <item><c>BaseUrl</c> under <see cref="PolicyPath"/> — the managed path, pushed by an
    ///   Intune configuration profile.</item>
    ///   <item><see cref="DefaultBaseUrl"/> — the lowest tier.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>A malformed value at any tier that is present resolves to no endpoint</b> rather than
    /// falling through to the next one. Falling through would be the dangerous direction: an
    /// organisation that pushed a typo'd <c>BaseUrl</c> for its own server would otherwise have
    /// its documents quietly sent to the public default instead. Degrade, never redirect.
    /// </para>
    /// <para>
    /// <b>There is deliberately no token here, and no policy value for one.</b> A Documenso API
    /// token is one named person's whole account; pushing it to a fleet would let every device
    /// read, alter and delete that person's documents. The token is per-user, user-entered and
    /// DPAPI-protected — see <see cref="DocumensoCredentialStore"/>.
    /// </para>
    /// <para>
    /// The pure parts (<see cref="Normalise"/>, <see cref="Resolve"/>) take no WPF, registry or
    /// settings types so tests/PdfCore can compile this file and pin them.
    /// </para>
    /// </remarks>
    internal static class DocumensoConfig
    {
        /// <summary>
        /// Policy key. Under <c>SOFTWARE\Policies\</c> for the reason TelemetryConfig gives:
        /// <c>App.Uninstall</c> deletes <c>Software\TDPdf</c> from both hives, and policy is not
        /// the application's to delete.
        /// </summary>
        internal const string PolicyPath = @"SOFTWARE\Policies\TDPdf\Documenso";

        internal const string PolicyEnabledValueName = "Enabled";
        internal const string PolicyBaseUrlValueName = "BaseUrl";
        internal const string PolicyAllowUserOverrideValueName = "AllowUserOverride";

        /// <summary>Environment-variable override for the instance URL.</summary>
        internal const string BaseUrlEnvironmentVariableName = "TDPDF_DOCUMENSO_BASEURL";

        /// <summary>
        /// The Documenso instance operated by The Doodle Project, used when nothing else is
        /// configured. Its hostname is settled for good: Documenso writes its own address into the
        /// PAdES <c>location</c> of every signature and into every emailed signing link, so it can
        /// never move without breaking audit trails already out in the world.
        /// </summary>
        internal const string DefaultBaseUrl = "https://sign.tdpdf.com";

        /// <summary>Where the live URL came from. For the Settings section and support.</summary>
        internal enum Source
        {
            None,
            Environment,
            User,
            Policy,
            Default,
        }

        /// <summary>
        /// Everything resolution depends on, gathered in one place so <see cref="Resolve"/> can be
        /// a pure function. A <c>null</c> string means "not set at this tier".
        /// </summary>
        internal readonly record struct Inputs(
            bool PolicyEnabled,
            string? Environment,
            string? PolicyBaseUrl,
            bool AllowUserOverride,
            string? UserBaseUrl);

        /// <summary>
        /// Canonical form of an instance URL, or <c>null</c> when it is not acceptable.
        /// </summary>
        /// <remarks>
        /// Trimmed, absolute, <c>https</c> (plain <c>http</c> only for a loopback host, which is
        /// the local-development case), no credentials, query or fragment, no trailing slash, and
        /// scheme and host lower-cased. The canonical form matters beyond tidiness: it is mixed
        /// into the DPAPI entropy that binds a stored token to its instance, so two spellings of
        /// one address must normalise to the same string or a token would stop decrypting.
        /// </remarks>
        internal static string? Normalise(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();

            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
            if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback) return null;
            if (string.IsNullOrEmpty(uri.Host)) return null;
            if (!string.IsNullOrEmpty(uri.UserInfo)) return null;   // never carry credentials in the URL
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;

            // Authority keeps a non-default port and drops a default one, and Uri has already
            // lower-cased the scheme and host.
            string path = uri.AbsolutePath.TrimEnd('/');
            return $"{uri.Scheme}://{uri.Authority}{path}";
        }

        /// <summary>
        /// The pure resolution rule over already-read <paramref name="i"/>. See the class remarks
        /// for the order and for why a malformed tier yields no endpoint instead of the next tier.
        /// </summary>
        internal static (string BaseUrl, Source From)? Resolve(Inputs i)
        {
            if (!i.PolicyEnabled) return null;

            if (!string.IsNullOrWhiteSpace(i.Environment))
                return Normalise(i.Environment) is { } env ? (env, Source.Environment) : null;

            bool policySet = !string.IsNullOrWhiteSpace(i.PolicyBaseUrl);

            if (!string.IsNullOrWhiteSpace(i.UserBaseUrl) && (!policySet || i.AllowUserOverride))
                return Normalise(i.UserBaseUrl) is { } user ? (user, Source.User) : null;

            if (policySet)
                return Normalise(i.PolicyBaseUrl) is { } policy ? (policy, Source.Policy) : null;

            return (DefaultBaseUrl, Source.Default);
        }

        /// <summary>The live instance for this device, or <c>null</c> when the feature is off.</summary>
        internal static (string BaseUrl, Source From)? TryResolveBaseUrl() => Resolve(ReadInputs());

        /// <summary>
        /// True when Electronic Signature exists on this device. Reads one environment variable,
        /// one registry key and one setting — no network, no file I/O, no await — so it is safe on
        /// every submenu open. The exact analogue of <c>TelemetryConfig.HasDestination()</c>.
        /// </summary>
        internal static bool HasEndpoint() => TryResolveBaseUrl() is not null;

        /// <summary>
        /// True unless the administrator removed the feature with <c>Enabled = 0</c>. Absent means
        /// enabled, matching <c>Policies\TDPdf\Update</c>.
        /// </summary>
        internal static bool IsEnabledByPolicy()
            => ReadPolicyDword(PolicyEnabledValueName) is not int enabled || enabled != 0;

        /// <summary>
        /// True when a URL the user types in Settings would actually take effect: no policy URL is
        /// set, or the policy allows overriding it. The Settings text box is read-only otherwise.
        /// </summary>
        internal static bool IsUserOverrideAllowed()
            => ReadPolicyString(PolicyBaseUrlValueName) is null
               || ReadPolicyDword(PolicyAllowUserOverrideValueName) is not int allow
               || allow != 0;

        /// <summary>The policy-pushed URL as written, or <c>null</c>. For the Settings section.</summary>
        internal static string? PolicyBaseUrl() => ReadPolicyString(PolicyBaseUrlValueName);

        /// <summary>The host of a normalised base URL, for user-facing copy ("sign.tdpdf.com").</summary>
        internal static string HostOf(string baseUrl)
            => Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) ? u.Authority : baseUrl;

        private static Inputs ReadInputs()
        {
            string? env = null;
            try { env = System.Environment.GetEnvironmentVariable(BaseUrlEnvironmentVariableName); }
            catch { /* environment access can throw under restricted hosts */ }

            string? user = null;
            try { user = TDPdf.Properties.Settings.Default.DocumensoBaseUrl; }
            catch { /* a corrupt user.config is reset at startup; until then, treat as unset */ }

            return new Inputs(
                PolicyEnabled: IsEnabledByPolicy(),
                Environment: env,
                PolicyBaseUrl: ReadPolicyString(PolicyBaseUrlValueName),
                AllowUserOverride: ReadPolicyDword(PolicyAllowUserOverrideValueName) is not int allow || allow != 0,
                UserBaseUrl: user);
        }

        private static string? ReadPolicyString(string valueName)
            => ReadPolicyValue(valueName) is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

        private static int? ReadPolicyDword(string valueName)
            => ReadPolicyValue(valueName) is int i ? i : null;

        private static object? ReadPolicyValue(string valueName)
        {
            // The tests compile this file on macOS and Linux, where there is no registry; there is
            // no policy either, so "absent" is the truthful answer.
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                // Explicit 64-bit view, as TelemetryConfig does, so WOW64 can never redirect the
                // lookup to Wow6432Node and miss the key an Intune profile wrote.
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = hklm.OpenSubKey(PolicyPath, writable: false);
                return key?.GetValue(valueName);
            }
            catch
            {
                // A locked-down or malformed hive must degrade to "absent", never crash.
                return null;
            }
        }
    }
}
