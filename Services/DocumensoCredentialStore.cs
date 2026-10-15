using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TDPdf.Services
{
    /// <summary>
    /// Holds the user's Documenso API token on their behalf: DPAPI <c>CurrentUser</c>, in
    /// <c>%LOCALAPPDATA%\TDPdf\documenso.cred</c>, bound to the instance it was issued for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why DPAPI is back, when 1.24.0.0 removed it.</b> What was removed was a fleet-wide,
    /// build-provisioned secret the app hid from the machine's owner and could not rotate without
    /// a release. This is the opposite case: a credential the user personally created and pasted,
    /// rotatable by them at any moment in Documenso's own UI and deletable with one button
    /// (Disconnect). Derek approved it as a deliberate reversal on 2026-09-15 (design Q10).
    /// </para>
    /// <para>
    /// <b>Never <c>user.config</c>.</b> <c>App.EnsureSettingsHealthy</c> resets that file without
    /// warning when it fails to parse, and it is plain text.
    /// </para>
    /// <para>
    /// <b>Endpoint binding.</b> The DPAPI entropy is <c>SHA256("TDPdf/Documenso/v1" + baseUrl)</c>
    /// over the normalised URL, so if policy repoints the instance the old blob simply does not
    /// decrypt — a token can never be replayed against a host it was not issued for. That, a
    /// different Windows user, a roamed profile and an admin password reset (DPAPI master-key
    /// loss) all land in the same place: <see cref="TryLoadToken"/> returns <c>null</c> and the
    /// feature reads as Not connected. Never a crash, never a dialog at startup.
    /// </para>
    /// <para>
    /// <b>Plain-text metadata</b> sits beside the blob — the instance, the account's email and
    /// name when known, the team URL, and when it was connected. None of those is a secret, and
    /// keeping them readable lets Settings say "Connected as …" without decrypting anything.
    /// </para>
    /// <para>
    /// <c>%LOCALAPPDATA%</c> rather than <c>%APPDATA%</c> on purpose: it does not roam, so a
    /// second machine starts Not connected instead of carrying a blob that only decrypts with
    /// roaming DPAPI keys. A portable install still writes here, never beside the EXE — the same
    /// rule <c>signatures.json</c> follows.
    /// </para>
    /// </remarks>
    internal static class DocumensoCredentialStore
    {
        internal static readonly string StoreDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TDPdf");

        internal static readonly string CredentialFile = Path.Combine(StoreDirectory, "documenso.cred");

        private const int FormatVersion = 1;

        /// <summary>What Settings shows. Never contains the token.</summary>
        internal sealed record Metadata(
            string BaseUrl,
            string? AccountEmail,
            string? AccountName,
            string? TeamUrl,
            DateTime ConnectedUtc);

        // The on-disk shape. ProtectedToken is the DPAPI blob, base64.
        private sealed class FileShape
        {
            public int Version { get; set; }
            public string BaseUrl { get; set; } = "";
            public string? AccountEmail { get; set; }
            public string? AccountName { get; set; }
            public string? TeamUrl { get; set; }
            public DateTime ConnectedUtc { get; set; }
            public string ProtectedToken { get; set; } = "";
        }

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        /// <summary>The stored metadata, or <c>null</c> when there is no usable file. No decryption.</summary>
        internal static Metadata? ReadMetadata()
        {
            var f = ReadFile();
            return f is null ? null : new Metadata(f.BaseUrl, f.AccountEmail, f.AccountName, f.TeamUrl, f.ConnectedUtc);
        }

        /// <summary>
        /// The metadata, but only when it belongs to <paramref name="baseUrl"/> — the instance
        /// Electronic Signature currently resolves to. A credential for another instance is, for
        /// every purpose, not a connection.
        /// </summary>
        internal static Metadata? ReadMetadataFor(string baseUrl)
            => ReadMetadata() is { } m && string.Equals(m.BaseUrl, DocumensoConfig.Normalise(baseUrl), StringComparison.Ordinal)
                ? m : null;

        /// <summary>
        /// The token issued for <paramref name="baseUrl"/>, or <c>null</c> — for a missing file, a
        /// different instance, a different Windows user, a corrupt file, or lost DPAPI keys alike.
        /// </summary>
        internal static string? TryLoadToken(string baseUrl)
        {
            try
            {
                string? normalised = DocumensoConfig.Normalise(baseUrl);
                if (normalised is null) return null;
                var f = ReadFile();
                if (f is null || !string.Equals(f.BaseUrl, normalised, StringComparison.Ordinal)) return null;

                byte[] blob = Convert.FromBase64String(f.ProtectedToken);
                byte[] plain = ProtectedData.Unprotect(blob, Entropy(normalised), DataProtectionScope.CurrentUser);
                try
                {
                    string token = Encoding.UTF8.GetString(plain);
                    return string.IsNullOrWhiteSpace(token) ? null : token;
                }
                finally
                {
                    Array.Clear(plain);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Stores a token that has <b>already been validated</b> against <paramref name="baseUrl"/>.
        /// Callers must never store an unvalidated credential. Written to a <c>.part</c> file and
        /// moved into place, so a crash mid-write leaves the previous file, not half of one.
        /// </summary>
        internal static bool Save(string baseUrl, string token, DocumensoAccount? account)
        {
            string? normalised = DocumensoConfig.Normalise(baseUrl);
            if (normalised is null || string.IsNullOrWhiteSpace(token)) return false;

            byte[] plain = Encoding.UTF8.GetBytes(token.Trim());
            try
            {
                byte[] blob = ProtectedData.Protect(plain, Entropy(normalised), DataProtectionScope.CurrentUser);
                var shape = new FileShape
                {
                    Version = FormatVersion,
                    BaseUrl = normalised,
                    AccountEmail = account?.Email,
                    AccountName = account?.Name,
                    TeamUrl = account?.TeamUrl,
                    ConnectedUtc = DateTime.UtcNow,
                    ProtectedToken = Convert.ToBase64String(blob),
                };
                return WriteFile(shape);
            }
            catch
            {
                return false;
            }
            finally
            {
                Array.Clear(plain);
            }
        }

        /// <summary>
        /// Fills in account details learned after connecting — the email and name only become
        /// knowable from an envelope this token created (see <see cref="DocumensoAccount"/>).
        /// Touches the metadata only; the protected blob is carried over untouched.
        /// </summary>
        internal static void RememberAccount(string baseUrl, DocumensoAccount account)
        {
            try
            {
                var f = ReadFile();
                if (f is null || !string.Equals(f.BaseUrl, DocumensoConfig.Normalise(baseUrl), StringComparison.Ordinal)) return;
                f.AccountEmail = account.Email ?? f.AccountEmail;
                f.AccountName = account.Name ?? f.AccountName;
                f.TeamUrl = account.TeamUrl ?? f.TeamUrl;
                WriteFile(f);
            }
            catch { /* metadata is a nicety; the connection is unaffected */ }
        }

        /// <summary>
        /// Disconnect: deletes the file. It cannot revoke the token on the server — Documenso has
        /// no API for that — so the UI says so and points at the token page.
        /// </summary>
        internal static bool Delete()
        {
            try
            {
                if (File.Exists(CredentialFile)) File.Delete(CredentialFile);
                string part = CredentialFile + ".part";
                if (File.Exists(part)) File.Delete(part);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// DPAPI entropy for an instance. Changing the prefix invalidates every stored token,
        /// which is the intended lever if the format ever has to change incompatibly.
        /// </summary>
        internal static byte[] Entropy(string normalisedBaseUrl)
            => SHA256.HashData(Encoding.UTF8.GetBytes("TDPdf/Documenso/v1" + normalisedBaseUrl));

        private static FileShape? ReadFile()
        {
            try
            {
                if (!File.Exists(CredentialFile)) return null;
                var f = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(CredentialFile), Json);
                if (f is null || f.Version != FormatVersion) return null;
                if (string.IsNullOrEmpty(f.BaseUrl) || string.IsNullOrEmpty(f.ProtectedToken)) return null;
                return f;
            }
            catch
            {
                // Corrupt, truncated, hand-edited, unreadable: all mean "not connected".
                return null;
            }
        }

        private static bool WriteFile(FileShape shape)
        {
            string part = CredentialFile + ".part";
            try
            {
                Directory.CreateDirectory(StoreDirectory);
                File.WriteAllText(part, JsonSerializer.Serialize(shape, Json));
                DocumensoClient.MoveIntoPlace(part, CredentialFile);
                return true;
            }
            catch
            {
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                return false;
            }
        }
    }
}
