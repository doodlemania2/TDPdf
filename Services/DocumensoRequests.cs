using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace TDPdf.Services
{
    // Signature Requests' WPF-free core: how an envelope reads in the list, which actions a row
    // offers, where "Open in browser" and "Copy signing link" go, what a download is called, how the
    // auto-refresh backs off, and how this device's sends are merged with the server's list. The
    // window (DocumensoRequestsWindow) only draws what this decides, so tests/PdfCore can pin it.

    /// <summary>The three PDFs a completed envelope can be downloaded as.</summary>
    internal enum DocumensoArtifact
    {
        /// <summary>The sealed, completed document (<c>/envelope/item/{id}/download?version=signed</c>).</summary>
        SignedCopy,
        /// <summary>The certificate of completion (<c>/envelope/{id}/certificate/download</c>).</summary>
        Certificate,
        /// <summary>The audit trail (<c>/envelope/{id}/audit-log/download</c>).</summary>
        AuditLog,
    }

    /// <summary>What a row in Signature Requests offers. Computed from the status alone.</summary>
    internal readonly record struct DocumensoRowActions(
        bool DownloadSignedCopy,
        bool DownloadCertificate,
        bool DownloadAuditLog,
        bool Cancel,
        bool Delete);

    /// <summary>One envelope as Signature Requests lists it.</summary>
    /// <param name="FromThisComputer">Sent from this device (it is in <see cref="DocumensoRequestStore"/>).</param>
    /// <param name="SentUtc">When it was sent: this device's record when there is one, else the server's createdAt.</param>
    internal sealed record DocumensoRequestRow(DocumensoEnvelope Envelope, bool FromThisComputer, DateTime? SentUtc);

    internal static class DocumensoRequestView
    {
        /// <summary>The auto-refresh interval while Signature Requests is open (design §5.4c).</summary>
        internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The ceiling the interval backs off to on HTTP 429. Self-hosted rate limits are
        /// undocumented, so a busy server is answered by asking less often, never by retrying.
        /// </summary>
        internal static readonly TimeSpan MaxRefreshInterval = TimeSpan.FromMinutes(5);

        /// <summary>How many of this device's sends are refreshed — one <c>get-many</c> call.</summary>
        internal const int TrackedRefreshLimit = DocumensoClient.GetManyLimit;

        /// <summary>The page size of the server-wide list.</summary>
        internal const int PageSize = 20;

        internal static string StatusLabel(DocumensoEnvelopeStatus s) => s switch
        {
            DocumensoEnvelopeStatus.Draft => "Draft",
            DocumensoEnvelopeStatus.Pending => "Pending",
            DocumensoEnvelopeStatus.Completed => "Completed",
            DocumensoEnvelopeStatus.Rejected => "Rejected",
            DocumensoEnvelopeStatus.Cancelled => "Cancelled",
            _ => "Unknown",
        };

        /// <summary>
        /// The theme brush a status is drawn in. Completed is the accent colour because that is
        /// what the service reports — not a trust statement TDPdf makes about the file.
        /// </summary>
        internal static string StatusBrushKey(DocumensoEnvelopeStatus s) => s switch
        {
            DocumensoEnvelopeStatus.Completed => "AccentGreen",
            DocumensoEnvelopeStatus.Pending => "WarningOrange",
            DocumensoEnvelopeStatus.Rejected => "DangerRed",
            _ => "TextSecondary",
        };

        /// <summary>
        /// Downloads exist only for a completed envelope (the server seals it on completion; before
        /// that there is no signed copy, certificate or full audit trail to give). Cancel is
        /// Documenso's own rule — only a PENDING envelope can be cancelled
        /// (<c>cancel-document.ts</c>). Delete is offered for drafts and for envelopes that ended
        /// without completing, and is deliberately <b>never</b> offered for a completed one: v2.18
        /// would soft-delete it, which takes an executed agreement out of the list for good.
        /// </summary>
        internal static DocumensoRowActions Actions(DocumensoEnvelopeStatus s)
        {
            bool completed = s == DocumensoEnvelopeStatus.Completed;
            return new DocumensoRowActions(
                DownloadSignedCopy: completed,
                DownloadCertificate: completed,
                DownloadAuditLog: completed,
                Cancel: s == DocumensoEnvelopeStatus.Pending,
                Delete: s is DocumensoEnvelopeStatus.Draft or DocumensoEnvelopeStatus.Cancelled or DocumensoEnvelopeStatus.Rejected);
        }

        /// <summary>
        /// One recipient's progress, in words. CCs are never asked to act; a Viewer only views.
        /// </summary>
        internal static string RecipientStatus(DocumensoRecipient r, DocumensoEnvelopeStatus envelopeStatus)
        {
            if (r.Role == DocumensoRecipientRole.Cc) return "gets a copy";
            if (r.SigningStatus == DocumensoSigningStatus.Rejected) return "rejected";
            string? when = FormatDate(ParseApiDate(r.SignedAt));
            string on = when is null ? "" : " " + when;
            if (r.SigningStatus == DocumensoSigningStatus.Signed)
            {
                return r.Role switch
                {
                    DocumensoRecipientRole.Approver => "approved" + on,
                    DocumensoRecipientRole.Viewer => "viewed" + on,
                    DocumensoRecipientRole.Assistant => "done" + on,
                    _ => "signed" + on,
                };
            }
            if (envelopeStatus == DocumensoEnvelopeStatus.Draft) return "not sent yet";
            if (envelopeStatus is DocumensoEnvelopeStatus.Cancelled or DocumensoEnvelopeStatus.Rejected) return "did not sign";
            bool opened = r.ReadStatus == DocumensoReadStatus.Opened;
            return r.Role switch
            {
                DocumensoRecipientRole.Approver => opened ? "opened, not approved yet" : "not approved yet",
                DocumensoRecipientRole.Viewer => opened ? "viewed" : "not viewed yet",
                _ => opened ? "opened, not signed yet" : "not signed yet",
            };
        }

        /// <summary>"Name &lt;email&gt;", or whichever of the two exists.</summary>
        internal static string RecipientLabel(DocumensoRecipient r)
        {
            string name = (r.Name ?? "").Trim(), email = (r.Email ?? "").Trim();
            if (name.Length > 0 && email.Length > 0 && !string.Equals(name, email, StringComparison.OrdinalIgnoreCase))
                return $"{name} <{email}>";
            return name.Length > 0 ? name : email.Length > 0 ? email : "Recipient";
        }

        /// <summary>
        /// Whether a recipient's signing link is worth copying: the envelope is still out, they
        /// have not acted, they are someone who acts (a CC's link opens nothing to do), and the API
        /// returned their token. The list endpoint does return tokens to the envelope's owner.
        /// </summary>
        internal static bool CanCopySigningLink(DocumensoEnvelopeStatus envelopeStatus, DocumensoRecipient r)
            => envelopeStatus == DocumensoEnvelopeStatus.Pending
               && r.Role != DocumensoRecipientRole.Cc
               && r.SigningStatus == DocumensoSigningStatus.NotSigned
               && !string.IsNullOrWhiteSpace(r.Token);

        /// <summary>
        /// A recipient's signing page: <c>{BaseUrl}/sign/{token}</c>, exactly what Documenso's own
        /// <c>formatSigningLink</c> (packages/lib/utils/recipients.ts, v2.18.0) builds and what
        /// distribute returns as <c>signingUrl</c>. Null without a token.
        /// </summary>
        internal static string? SigningUrl(string baseUrl, string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            string? b = DocumensoConfig.Normalise(baseUrl);
            return b is null ? null : $"{b}/sign/{Uri.EscapeDataString(token.Trim())}";
        }

        /// <summary>
        /// The envelope's page in Documenso's web app: <c>/t/{teamUrl}/documents/{envelopeId}</c>
        /// (apps/remix/app/routes/_authenticated+/t.$teamUrl+/documents.$id._index.tsx, which loads
        /// <c>envelope.get</c> with the route id, so the envelope id is the right one). Without a
        /// team URL there is no page to name, so it falls back to the instance's front page.
        /// </summary>
        internal static string WebUrl(string baseUrl, string? teamUrl, string envelopeId)
        {
            string b = DocumensoConfig.Normalise(baseUrl) ?? baseUrl.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(teamUrl) || string.IsNullOrWhiteSpace(envelopeId)) return b + "/";
            return $"{b}/t/{Uri.EscapeDataString(teamUrl.Trim())}/documents/{Uri.EscapeDataString(envelopeId.Trim())}";
        }

        /// <summary>
        /// The name the Save dialog suggests: "{title} (signed).pdf", "(certificate)", "(audit log)".
        /// The title is the user's own words from the server, so it is made safe for a Windows file
        /// name here rather than trusted.
        /// </summary>
        internal static string DefaultFileName(string? title, DocumensoArtifact what)
        {
            string stem = SafeFileStem(title);
            string suffix = what switch
            {
                DocumensoArtifact.Certificate => " (certificate)",
                DocumensoArtifact.AuditLog => " (audit log)",
                _ => " (signed)",
            };
            return stem + suffix + ".pdf";
        }

        /// <summary>
        /// A Windows-safe file stem: no reserved characters, no control characters, no trailing
        /// dots or spaces, no device names, at most 120 characters, never empty. Explicit rather
        /// than <c>Path.GetInvalidFileNameChars</c>, which on the test runner (Linux) is only "/".
        /// </summary>
        internal static string SafeFileStem(string? title)
        {
            string t = (title ?? "").Trim();
            if (t.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) t = t[..^4];
            var sb = new StringBuilder(t.Length);
            foreach (char c in t)
                sb.Append(char.IsControl(c) || "<>:\"/\\|?*".IndexOf(c) >= 0 ? '_' : c);
            string s = sb.ToString().Trim().TrimEnd('.', ' ');
            if (s.Length > 120) s = s[..120].TrimEnd('.', ' ');
            string upper = s.ToUpperInvariant();
            if (upper is "CON" or "PRN" or "AUX" or "NUL"
                || (upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(upper[3])))
                s = "_" + s;
            return s.Length == 0 ? "document" : s;
        }

        /// <summary>
        /// The next auto-refresh interval. A success resets to <see cref="RefreshInterval"/>; a 429
        /// doubles it (honouring a longer <c>Retry-After</c>), capped at
        /// <see cref="MaxRefreshInterval"/>. Other failures keep the current interval: an offline
        /// laptop asking every 30 s costs nothing and recovers on its own.
        /// </summary>
        /// <param name="failure">How the last refresh failed; null when it succeeded.</param>
        internal static TimeSpan NextRefreshInterval(TimeSpan current, DocumensoFailure? failure, TimeSpan? retryAfter = null)
        {
            if (failure is null) return RefreshInterval;
            if (failure != DocumensoFailure.RateLimited) return current > RefreshInterval ? current : RefreshInterval;
            var doubled = TimeSpan.FromTicks(Math.Max(current.Ticks, RefreshInterval.Ticks) * 2);
            if (retryAfter is { } ra && ra > doubled) doubled = ra;
            return doubled > MaxRefreshInterval ? MaxRefreshInterval : doubled;
        }

        /// <summary>
        /// Splits what the server returned into "Sent from this computer" (this device's tracked
        /// sends, in the order they were sent, newest first) and everything else on the current
        /// page of the server-wide list, with no envelope shown twice.
        /// </summary>
        internal static (List<DocumensoRequestRow> Mine, List<DocumensoRequestRow> Others) Merge(
            IReadOnlyList<DocumensoRequestStore.Entry> tracked,
            IReadOnlyList<DocumensoEnvelope> trackedEnvelopes,
            IReadOnlyList<DocumensoEnvelope> listed)
        {
            var byId = new Dictionary<string, DocumensoEnvelope>(StringComparer.Ordinal);
            foreach (var e in trackedEnvelopes)
                if (!string.IsNullOrEmpty(e.Id)) byId[e.Id] = e;
            // An envelope on the listed page that this device sent, but that the get-many did not
            // return (or was not asked about, being past the refresh limit), is still "mine".
            var trackedIds = new HashSet<string>(tracked.Select(t => t.EnvelopeId), StringComparer.Ordinal);
            foreach (var e in listed)
                if (!string.IsNullOrEmpty(e.Id) && trackedIds.Contains(e.Id) && !byId.ContainsKey(e.Id)) byId[e.Id] = e;

            var mine = new List<DocumensoRequestRow>();
            var shown = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in tracked)
            {
                if (!byId.TryGetValue(t.EnvelopeId, out var env) || !shown.Add(env.Id)) continue;
                mine.Add(new DocumensoRequestRow(env, true, t.SentUtc));
            }

            var others = new List<DocumensoRequestRow>();
            foreach (var e in listed)
            {
                if (string.IsNullOrEmpty(e.Id) || !shown.Add(e.Id)) continue;
                others.Add(new DocumensoRequestRow(e, false, ParseApiDate(e.CreatedAt)));
            }
            return (mine, others);
        }

        /// <summary>
        /// A string that changes whenever anything a row shows changes, so a refresh that brought
        /// nothing new does not rebuild the list (and steal keyboard focus) every 30 seconds.
        /// </summary>
        internal static string Fingerprint(IEnumerable<DocumensoRequestRow> rows)
        {
            var sb = new StringBuilder();
            foreach (var r in rows)
            {
                var e = r.Envelope;
                sb.Append(e.Id).Append('|').Append(e.Status).Append('|').Append(e.Title).Append('|').Append(r.FromThisComputer);
                foreach (var p in e.Recipients)
                    sb.Append('|').Append(p.Id).Append(':').Append(p.SigningStatus).Append(':').Append(p.ReadStatus).Append(':').Append(p.SignedAt);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Which of this device's tracked requests an open file is the signed copy of, for Verify
        /// Signatures' certificate and audit-log downloads. Documenso writes no envelope id into the
        /// PDF it seals (no /Info, no XMP — checked in seal-document.handler.ts, v2.18.0), so:
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Exact:</b> the file's SHA-256 equals the fingerprint recorded when TDPdf downloaded
        /// that signed copy. Unambiguous.
        /// </para>
        /// <para>
        /// <b>By name:</b> only when the file states it was sealed by the configured instance
        /// (<paramref name="sealedByThisInstance"/>, from the signature's /Location), and its name,
        /// less a " (signed)" / "_signed" suffix, equals exactly one tracked title. Two candidates
        /// is no match — offering the wrong agreement's audit log is worse than offering none.
        /// </para>
        /// </remarks>
        internal static (DocumensoRequestStore.Entry? Entry, bool Exact) MatchTracked(
            IReadOnlyList<DocumensoRequestStore.Entry> entries, string? fileSha256, string? fileName, bool sealedByThisInstance)
        {
            if (!string.IsNullOrEmpty(fileSha256))
            {
                var exact = entries.FirstOrDefault(e => string.Equals(e.SignedSha256, fileSha256, StringComparison.OrdinalIgnoreCase));
                if (exact is not null) return (exact, true);
            }
            if (!sealedByThisInstance || string.IsNullOrWhiteSpace(fileName)) return (null, false);

            string stem = System.IO.Path.GetFileNameWithoutExtension(fileName.Trim());
            foreach (var suffix in new[] { " (signed)", "_signed", "-signed", " signed" })
            {
                if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { stem = stem[..^suffix.Length]; break; }
            }
            stem = stem.Trim();
            if (stem.Length == 0) return (null, false);
            var byName = entries.Where(e => string.Equals(SafeFileStem(e.Title), stem, StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(e.Title.Trim(), stem, StringComparison.OrdinalIgnoreCase)).ToList();
            return byName.Count == 1 ? (byName[0], false) : (null, false);
        }

        /// <summary>The API's ISO-8601 timestamps, as UTC. Null for anything unreadable.</summary>
        internal static DateTime? ParseApiDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
                ? d.UtcDateTime : null;
        }

        /// <summary>A date the way the list shows it, in local time: "10 Oct 2026". Null in, null out.</summary>
        internal static string? FormatDate(DateTime? utc)
            => utc is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : null;
    }
}
