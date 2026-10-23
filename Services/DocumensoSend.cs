using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TDPdf.Services
{
    // Send for Signature's WPF-free core: what the user has entered (a session), what is wrong with
    // it (a check), what it becomes on the wire (a create request), and the create → distribute
    // pipeline with its rollback. The window (DocumensoSendWindow) and the canvas placement mode
    // (MainWindow.ESignatureFields.cs) only edit a session and display a check; everything that
    // decides what leaves the machine is here, where tests/PdfCore can reach it.

    /// <summary>One editable recipient row. <see cref="Key"/> is what placed fields point at, so
    /// reordering or renaming a recipient never detaches their fields.</summary>
    internal sealed class ESignRecipientEntry
    {
        public Guid Key { get; init; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public DocumensoRecipientRole Role { get; set; } = DocumensoRecipientRole.Signer;
    }

    /// <summary>
    /// A field placed on the canvas, waiting to be sent. NOT an annotation: it is never baked into
    /// the PDF and never enters <c>_annotations</c>. <see cref="PageIndex"/> is 0-based (the
    /// canvas's); the wire's <c>page</c> is 1-based and is converted only when the request is built.
    /// </summary>
    internal sealed record ESignFieldDraft(Guid RecipientKey, DocumensoFieldType Type, int PageIndex, DocumensoFieldRect Rect);

    /// <summary>
    /// Everything one Send for Signature has gathered so far. It lives on the document's tab
    /// (<c>DocumentContext.ESignSession</c>), so cancelling the dialog and reopening it — or going
    /// off to place fields and coming back — keeps the recipients and fields; a successful send,
    /// closing the tab, or a page-layout change clears it.
    /// </summary>
    internal sealed class ESignSendSession
    {
        public string Title { get; set; } = "";
        public List<ESignRecipientEntry> Recipients { get; } = new();
        public bool SequentialOrder { get; set; }
        public string Subject { get; set; } = "";
        public string Message { get; set; } = "";
        public List<ESignFieldDraft> Fields { get; } = new();

        /// <summary>Removes a recipient and the fields placed for them — a field with no recipient
        /// cannot be sent, and silently re-homing it to someone else would be worse.</summary>
        public int RemoveRecipient(Guid key)
        {
            Recipients.RemoveAll(r => r.Key == key);
            return Fields.RemoveAll(f => f.RecipientKey == key);
        }

        /// <summary>Moves a recipient up (-1) or down (+1). The order IS the signing order when
        /// signing is sequential, and is always what <c>r1</c>, <c>r2</c>… placeholders refer to.</summary>
        public bool Move(int index, int delta)
        {
            int to = index + delta;
            if (index < 0 || index >= Recipients.Count || to < 0 || to >= Recipients.Count) return false;
            (Recipients[index], Recipients[to]) = (Recipients[to], Recipients[index]);
            return true;
        }

        public int FieldCountFor(Guid key) => Fields.Count(f => f.RecipientKey == key);
    }

    /// <summary>The result of checking a session: errors block Send, warnings only inform.</summary>
    internal sealed record ESignCheck(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
    {
        public bool CanSend => Errors.Count == 0;
    }

    /// <summary>A recipient's signing link, matched back to the row the user typed.</summary>
    internal sealed record ESignSigningLink(Guid RecipientKey, string Label, string SigningUrl);

    internal static class DocumensoSendPlan
    {
        // The API's own limits, from the live OpenAPI document (v2.18.0).
        internal const int MaxTitle = 255;
        internal const int MaxSubject = 254;
        internal const int MaxMessage = 5000;
        internal const int MaxName = 255;
        internal const int MaxEmail = 254;

        // Deliberately looser than Documenso's own pattern (which the server applies anyway): one @,
        // something on both sides, a dot in the domain, no spaces or characters no address carries.
        // Its job is to catch the typo before a round trip, not to be the authority.
        private static readonly Regex EmailShape = new(
            @"^[^\s@""(),:;<>\[\]\\]+@[^\s@""(),:;<>\[\]\\]+\.[^\s@""(),:;<>\[\]\\]+$",
            RegexOptions.CultureInvariant);

        internal static bool IsValidEmail(string? email)
        {
            string e = (email ?? "").Trim();
            return e.Length is > 0 and <= MaxEmail && EmailShape.IsMatch(e) && !e.EndsWith(".", StringComparison.Ordinal);
        }

        /// <summary>Documenso's own rule (<c>isSignatureFieldType</c>): either signature type satisfies a signer.</summary>
        internal static bool IsSignatureType(DocumensoFieldType t)
            => t is DocumensoFieldType.Signature or DocumensoFieldType.FreeSignature;

        /// <summary>Roles that fill fields. A CC or Viewer is never asked to, so a field placed for one
        /// could never be completed — those are left out of the request (and the check says so).</summary>
        internal static bool TakesFields(DocumensoRecipientRole role)
            => role is DocumensoRecipientRole.Signer or DocumensoRecipientRole.Approver;

        /// <summary>How a recipient is named in the dialog and the placement palette.</summary>
        internal static string Label(ESignRecipientEntry r, int index)
            => r.Name.Trim() is { Length: > 0 } n ? n
             : r.Email.Trim() is { Length: > 0 } e ? e
             : $"Recipient {index + 1}";

        /// <summary>
        /// What stands between this session and a send that Documenso v2.18.0 will accept.
        /// </summary>
        /// <param name="pageHasOffsetOrigin">0-based page index → does its visible box start away
        /// from 0,0 (see <see cref="DocumensoFieldGeometry.HasOffsetOrigin"/>). Null skips that warning.</param>
        /// <remarks>
        /// <b>A signer with no signature field is an error, not a warning.</b> Documenso's distribute
        /// refuses it outright — <c>getRecipientsWithMissingFields</c> in
        /// <c>packages/lib/utils/recipients.ts</c>, enforced in
        /// <c>packages/lib/server-only/document/send-document.ts</c> as MISSING_SIGNATURE_FIELD
        /// ("Signers must have at least one signature field"). Letting the user press Send would
        /// only produce a create, a refusal and a rollback. A signature placeholder for that
        /// signer's <c>rN</c> counts, since the server turns it into a field.
        /// </remarks>
        internal static ESignCheck Check(ESignSendSession s, IReadOnlyList<DocumensoPlaceholder> placeholders,
            Func<int, bool>? pageHasOffsetOrigin = null)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            if (string.IsNullOrWhiteSpace(s.Title)) errors.Add("Give the document a title.");

            if (s.Recipients.Count == 0)
            {
                errors.Add("Add at least one recipient.");
            }
            else
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < s.Recipients.Count; i++)
                {
                    var r = s.Recipients[i];
                    string email = r.Email.Trim();
                    if (email.Length == 0)
                        errors.Add($"Recipient {i + 1} needs an email address.");
                    else if (!IsValidEmail(email))
                        errors.Add($"\"{email}\" is not a valid email address.");
                    else if (!seen.Add(email))
                        errors.Add($"{email} is listed more than once.");
                }
            }

            int maxRef = placeholders.Count == 0 ? 0 : placeholders.Max(p => p.RecipientNumber);
            if (maxRef > s.Recipients.Count)
                errors.Add($"The document's placeholders go up to r{maxRef}, but only {s.Recipients.Count} " +
                           $"recipient{(s.Recipients.Count == 1 ? " is" : "s are")} listed. r1 is the first recipient, r2 the second, and so on.");

            for (int i = 0; i < s.Recipients.Count; i++)
            {
                var r = s.Recipients[i];
                if (r.Role != DocumensoRecipientRole.Signer) continue;
                bool placed = s.Fields.Any(f => f.RecipientKey == r.Key && IsSignatureType(f.Type));
                bool fromPlaceholder = placeholders.Any(p => p.IsSignature && p.RecipientNumber == i + 1);
                if (!placed && !fromPlaceholder)
                    errors.Add($"{Label(r, i)} has no Signature field. Every signer needs one. Place one on the page, " +
                               "or make them an Approver, Viewer or CC.");
            }

            foreach (var r in s.Recipients.Where(r => !TakesFields(r.Role)))
            {
                if (s.FieldCountFor(r.Key) > 0)
                    warnings.Add($"Fields placed for {Label(r, s.Recipients.IndexOf(r))} will be left out, because " +
                                 $"a {RoleLabel(r.Role)} is never asked to fill anything in.");
            }

            if (pageHasOffsetOrigin is not null)
            {
                var offsetPages = s.Fields.Select(f => f.PageIndex).Distinct().Where(pageHasOffsetOrigin).OrderBy(p => p).ToList();
                if (offsetPages.Count > 0)
                    warnings.Add($"Page{(offsetPages.Count == 1 ? "" : "s")} {string.Join(", ", offsetPages.Select(p => p + 1))} " +
                                 $"{(offsetPages.Count == 1 ? "is" : "are")} cropped. The signing service may draw fields there " +
                                 "slightly out of place in the final signed PDF; check it after signing.");
            }

            return new ESignCheck(errors, warnings);
        }

        internal static string RoleLabel(DocumensoRecipientRole role) => role switch
        {
            DocumensoRecipientRole.Signer => "Signer",
            DocumensoRecipientRole.Approver => "Approver",
            DocumensoRecipientRole.Viewer => "Viewer",
            DocumensoRecipientRole.Cc => "CC",
            _ => role.ToString(),
        };

        internal static string FieldLabel(DocumensoFieldType type) => type switch
        {
            DocumensoFieldType.Signature => "Signature",
            DocumensoFieldType.FreeSignature => "Free signature",
            DocumensoFieldType.Initials => "Initials",
            DocumensoFieldType.Name => "Name",
            DocumensoFieldType.Email => "Email",
            DocumensoFieldType.Date => "Date",
            DocumensoFieldType.Text => "Text",
            _ => type.ToString(),
        };

        /// <summary>
        /// The <c>/envelope/create</c> payload for a session that passed <see cref="Check"/>.
        /// Recipients go inside it, in list order, each with their placed fields — see
        /// <see cref="DocumensoCreateEnvelopeRequest.Recipients"/> for why that is not optional.
        /// </summary>
        /// <param name="timezone">IANA zone for date fields, or null to let the server decide.</param>
        internal static DocumensoCreateEnvelopeRequest BuildCreateRequest(ESignSendSession s, string? timezone = null)
        {
            var recipients = new List<DocumensoCreateRecipient>(s.Recipients.Count);
            int order = 0;
            foreach (var r in s.Recipients)
            {
                // Documenso numbers only the people who act; a CC has no place in the queue
                // (normalizeRecipientSigningOrders in packages/lib/utils/recipients.ts).
                int? signingOrder = s.SequentialOrder && r.Role != DocumensoRecipientRole.Cc ? ++order : null;
                var fields = TakesFields(r.Role)
                    ? s.Fields.Where(f => f.RecipientKey == r.Key).Select(ToInline).ToList()
                    : new List<DocumensoInlineField>();
                recipients.Add(new DocumensoCreateRecipient
                {
                    Email = r.Email.Trim(),
                    Name = Truncate(r.Name.Trim(), MaxName),
                    Role = r.Role,
                    SigningOrder = signingOrder,
                    Fields = fields.Count == 0 ? null : fields,
                });
            }

            string subject = Truncate(s.Subject.Trim(), MaxSubject);
            string message = Truncate(s.Message.Trim(), MaxMessage);
            return new DocumensoCreateEnvelopeRequest
            {
                Title = Truncate(s.Title.Trim(), MaxTitle),
                // A correlation id that says nothing about the document or the user.
                ExternalId = "tdpdf-" + Guid.NewGuid().ToString("N"),
                Meta = new DocumensoEnvelopeMeta
                {
                    Subject = subject.Length == 0 ? null : subject,
                    Message = message.Length == 0 ? null : message,
                    SigningOrder = s.SequentialOrder ? DocumensoSigningOrder.Sequential : DocumensoSigningOrder.Parallel,
                    Timezone = timezone,
                },
                Recipients = recipients,
            };
        }

        private static DocumensoInlineField ToInline(ESignFieldDraft f) => new()
        {
            Type = f.Type,
            Page = f.PageIndex + 1,
            PositionX = Math.Clamp(f.Rect.X, 0, 100),
            PositionY = Math.Clamp(f.Rect.Y, 0, 100),
            Width = Math.Clamp(f.Rect.Width, 0, 100),
            Height = Math.Clamp(f.Rect.Height, 0, 100),
        };

        /// <summary>
        /// Matches distribute's recipients back to the rows the user typed, by email (Documenso may
        /// add a team's default recipients, and does not promise to keep order). CCs are left out:
        /// their link opens nothing to do. Each server recipient is used at most once.
        /// </summary>
        internal static List<ESignSigningLink> MatchLinks(IReadOnlyList<ESignRecipientEntry> rows,
            IReadOnlyList<DocumensoDistributedRecipient> distributed)
        {
            var links = new List<ESignSigningLink>();
            var used = new HashSet<DocumensoDistributedRecipient>();
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Role == DocumensoRecipientRole.Cc) continue;
                var match = distributed.FirstOrDefault(d => !used.Contains(d)
                    && string.Equals(d.Email.Trim(), row.Email.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match is null || string.IsNullOrWhiteSpace(match.SigningUrl)) continue;
                used.Add(match);
                links.Add(new ESignSigningLink(row.Key, Label(row, i), match.SigningUrl));
            }
            return links;
        }

        /// <summary>The machine's zone as an IANA name for Documenso's date stamps, or null.</summary>
        internal static string? LocalIanaTimezone()
        {
            try
            {
                string id = TimeZoneInfo.Local.Id;
                if (id.Contains('/')) return id;   // already IANA (non-Windows)
                return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : null;
            }
            catch
            {
                return null;
            }
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
    }

    /// <summary>Where the pipeline is, for the progress line.</summary>
    internal enum DocumensoSendStage { Uploading, Sending, Withdrawing }

    /// <summary>How a send ended. Exactly one of Ok / Cancelled / failed holds.</summary>
    internal sealed record DocumensoSendResult
    {
        public string? EnvelopeId { get; init; }
        public IReadOnlyList<DocumensoDistributedRecipient> Recipients { get; init; } = Array.Empty<DocumensoDistributedRecipient>();
        /// <summary>The API failure, when the failure was one.</summary>
        public DocumensoApiException? Failure { get; init; }
        /// <summary>Any failure at all (including <see cref="Failure"/>); null on success or cancel.</summary>
        public Exception? Error { get; init; }
        public bool Cancelled { get; init; }
        /// <summary>
        /// Null when nothing had been created; true when the half-built envelope was deleted again;
        /// false when that delete failed too and a draft may be left on the server.
        /// </summary>
        public bool? RolledBack { get; init; }

        public bool Ok => Error is null && !Cancelled && EnvelopeId is not null;

        /// <summary>The telemetry <c>outcome</c> — a fixed vocabulary, nothing from the server.</summary>
        public string Outcome => Ok ? "ok"
            : Cancelled ? "cancelled"
            : Failure?.Failure switch
            {
                DocumensoFailure.AuthRejected => "authfailed",
                DocumensoFailure.Network or DocumensoFailure.Timeout => "network",
                DocumensoFailure.Server => "server",
                DocumensoFailure.RateLimited => "ratelimited",
                DocumensoFailure.Rejected or DocumensoFailure.NotFound => "rejected",
                DocumensoFailure.UnexpectedResponse => "unexpected",
                _ => "error",
            };
    }

    internal static class DocumensoSender
    {
        /// <summary>
        /// create (with recipients and their fields inline) → distribute. On ANY failure or
        /// cancellation after the envelope exists, it is deleted again, best effort, so a failed
        /// send never leaves a half-built draft behind.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two calls rather than the four the design sketched (create → recipients → fields →
        /// distribute): recipients must travel inside create for placeholders to bind to them (see
        /// <see cref="DocumensoCreateEnvelopeRequest.Recipients"/>), and once they do, their placed
        /// fields can travel with them. That also removes the id-mapping step and two places to
        /// fail between create and distribute.
        /// </para>
        /// <para>
        /// <b>Cancellation stops at the brink.</b> Esc is honoured up to the moment distribute is
        /// sent; distribute itself runs to completion on its own timeout. Abandoning it mid-flight
        /// could leave recipients emailed by a send TDPdf then reports as cancelled — and would
        /// delete an envelope they had just been invited to.
        /// </para>
        /// <para>
        /// One gap no client can close: if the connection drops (or Esc lands) while create's
        /// response is still arriving, the server may hold an envelope whose id TDPdf never
        /// learned. It is a DRAFT — never distributed, so nobody was emailed — and it is visible
        /// in Documenso's own list for the user to delete.
        /// </para>
        /// <para>
        /// The rollback is a plain delete with no caller token (the caller's may be the very thing
        /// that was cancelled). Documenso deletes a draft outright.
        /// </para>
        /// </remarks>
        internal static async Task<DocumensoSendResult> SendAsync(DocumensoClient client,
            DocumensoCreateEnvelopeRequest request, byte[] pdf, string fileName,
            IProgress<DocumensoSendStage>? progress, CancellationToken ct)
        {
            string? envelopeId = null;
            try
            {
                progress?.Report(DocumensoSendStage.Uploading);
                using (var stream = new System.IO.MemoryStream(pdf, writable: false))
                    envelopeId = await client.CreateEnvelopeAsync(request, stream, fileName, ct).ConfigureAwait(false);

                progress?.Report(DocumensoSendStage.Sending);
                // The brink: the last point at which Esc is honoured (see remarks).
                ct.ThrowIfCancellationRequested();
                var recipients = await client.DistributeAsync(envelopeId, null, CancellationToken.None).ConfigureAwait(false);
                return new DocumensoSendResult { EnvelopeId = envelopeId, Recipients = recipients };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                bool? rolledBack = envelopeId is null ? null : await TryWithdrawAsync(client, envelopeId, progress).ConfigureAwait(false);
                return new DocumensoSendResult { Cancelled = true, RolledBack = rolledBack };
            }
            catch (Exception ex)
            {
                bool? rolledBack = envelopeId is null ? null : await TryWithdrawAsync(client, envelopeId, progress).ConfigureAwait(false);
                return new DocumensoSendResult { Error = ex, Failure = ex as DocumensoApiException, RolledBack = rolledBack };
            }
        }

        private static async Task<bool> TryWithdrawAsync(DocumensoClient client, string envelopeId,
            IProgress<DocumensoSendStage>? progress)
        {
            try
            {
                progress?.Report(DocumensoSendStage.Withdrawing);
                await client.DeleteAsync(envelopeId, CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
