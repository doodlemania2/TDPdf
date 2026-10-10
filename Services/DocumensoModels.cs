using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TDPdf.Services
{
    // Data shapes for the Documenso v2 REST API, as served by the live instance's OpenAPI
    // document (GET {BaseUrl}/api/v2/openapi.json, Documenso v2.18.0, read 2026-10-10). Only the
    // fields TDPdf uses are declared; System.Text.Json ignores the rest, so a server that adds
    // properties never breaks a read.
    //
    // No WPF types here: tests/PdfCore compiles this file.

    // ── Enums ────────────────────────────────────────────────────────────────────────────────
    // Documenso spells these UPPER_SNAKE ("NOT_SIGNED"). DocumensoEnumConverter maps them to
    // PascalCase members and maps anything it does not recognise to Unknown = 0, so a status a
    // newer server invents shows as "unknown" instead of failing the whole list.

    internal enum DocumensoEnvelopeStatus { Unknown = 0, Draft, Pending, Completed, Rejected, Cancelled }

    internal enum DocumensoRecipientRole { Unknown = 0, Signer, Approver, Viewer, Cc, Assistant }

    internal enum DocumensoSigningStatus { Unknown = 0, NotSigned, Signed, Rejected }

    internal enum DocumensoReadStatus { Unknown = 0, NotOpened, Opened }

    internal enum DocumensoSendStatus { Unknown = 0, NotSent, Sent }

    internal enum DocumensoFieldType
    {
        Unknown = 0, Signature, FreeSignature, Initials, Name, Email, Date, Text, Number, Radio, Checkbox, Dropdown
    }

    /// <summary>The <c>version</c> query switch on the envelope-item download.</summary>
    internal enum DocumensoDownloadVersion
    {
        /// <summary>The sealed, completed document. The server's default.</summary>
        Signed,
        /// <summary>The PDF exactly as uploaded.</summary>
        Original,
        /// <summary>Fields inserted so far, rendered — explicitly NOT an executed document.</summary>
        Pending,
    }

    /// <summary>Sequential vs parallel signing, the envelope-meta <c>signingOrder</c>.</summary>
    internal enum DocumensoSigningOrder { Parallel, Sequential }

    /// <summary>
    /// Reads and writes Documenso's UPPER_SNAKE enum spelling for any of the enums above.
    /// </summary>
    internal sealed class DocumensoEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) { reader.Skip(); return default; }
            string? raw = reader.GetString();
            if (string.IsNullOrEmpty(raw)) return default;
            return Enum.TryParse<T>(raw.Replace("_", ""), ignoreCase: true, out var v) ? v : default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WriteStringValue(ToWire(value));

        /// <summary><c>NotSigned</c> → <c>NOT_SIGNED</c>; <c>Cc</c> → <c>CC</c>.</summary>
        internal static string ToWire(T value)
        {
            string name = value.ToString();
            var sb = new StringBuilder(name.Length + 4);
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i])) sb.Append('_');
                sb.Append(char.ToUpperInvariant(name[i]));
            }
            return sb.ToString();
        }
    }

    // ── What the client returns ─────────────────────────────────────────────────────────────

    /// <summary>
    /// What TDPdf knows about the account a token belongs to. Every member may be null.
    /// </summary>
    /// <remarks>
    /// Documenso v2 has no "who am I" endpoint — the live OpenAPI document was checked for one and
    /// there is none. What the token probe CAN see reliably is the team: tokens are issued per
    /// team, and every envelope a token lists carries <c>team.url</c>, which is what builds the
    /// token page link <c>/t/{teamUrl}/settings/tokens</c>. The account's email is only knowable
    /// from an envelope this token created (its <c>user</c> is the creator), so it is filled in
    /// after the first send rather than guessed from someone else's envelope at connect time.
    /// </remarks>
    internal sealed record DocumensoAccount(string? Email, string? Name, string? TeamUrl);

    internal sealed class DocumensoEnvelopePage
    {
        public List<DocumensoEnvelope> Data { get; set; } = new();
        public int Count { get; set; }
        public int CurrentPage { get; set; }
        public int PerPage { get; set; }
        public int TotalPages { get; set; }
    }

    internal sealed class DocumensoEnvelope
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public DocumensoEnvelopeStatus Status { get; set; }
        public string? ExternalId { get; set; }
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
        public string? CompletedAt { get; set; }
        public List<DocumensoRecipient> Recipients { get; set; } = new();
        /// <summary>Present on get / get-many; absent from the list endpoint.</summary>
        public List<DocumensoEnvelopeItem> EnvelopeItems { get; set; } = new();
        public DocumensoTeamRef? Team { get; set; }
        /// <summary>The envelope's owner — for an envelope this token created, the token's user.</summary>
        public DocumensoUserRef? User { get; set; }
    }

    internal sealed class DocumensoEnvelopeItem
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public int Order { get; set; }
    }

    internal sealed class DocumensoTeamRef
    {
        public long Id { get; set; }
        public string Url { get; set; } = "";
    }

    internal sealed class DocumensoUserRef
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
    }

    internal sealed class DocumensoRecipient
    {
        public long Id { get; set; }
        public string Email { get; set; } = "";
        public string Name { get; set; } = "";
        public DocumensoRecipientRole Role { get; set; }
        public DocumensoSigningStatus SigningStatus { get; set; }
        public DocumensoReadStatus ReadStatus { get; set; }
        public DocumensoSendStatus SendStatus { get; set; }
        public int? SigningOrder { get; set; }
        public string? SignedAt { get; set; }
        public string? RejectionReason { get; set; }
        /// <summary>
        /// The recipient's signing token — whoever holds it can sign as this recipient. Never
        /// log it, never put it in telemetry; it is here only to build a signing link.
        /// </summary>
        public string? Token { get; set; }
    }

    internal sealed class DocumensoField
    {
        public long Id { get; set; }
        public long RecipientId { get; set; }
        public string? EnvelopeItemId { get; set; }
        public DocumensoFieldType Type { get; set; }
        public int Page { get; set; }
    }

    /// <summary>One entry of the <c>/envelope/distribute</c> response.</summary>
    internal sealed class DocumensoDistributedRecipient
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public DocumensoRecipientRole Role { get; set; }
        public int? SigningOrder { get; set; }
        /// <summary>The link that opens Documenso's signing page for this recipient. Treat as a secret.</summary>
        public string SigningUrl { get; set; } = "";
    }

    /// <summary><c>GET {BaseUrl}/api/health</c>. Unauthenticated.</summary>
    internal sealed class DocumensoHealth
    {
        public string Status { get; set; } = "";
        public DocumensoHealthChecks? Checks { get; set; }
        public bool IsOk => string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class DocumensoHealthChecks
    {
        public DocumensoHealthCheck? Database { get; set; }
        public DocumensoHealthCheck? Certificate { get; set; }
    }

    internal sealed class DocumensoHealthCheck
    {
        public string Status { get; set; } = "";
    }

    /// <summary>
    /// <c>GET {BaseUrl}/api/certificate-status</c>. Unauthenticated. <c>isAvailable = false</c>
    /// means the service runs but cannot seal a completed document.
    /// </summary>
    internal sealed class DocumensoCertificateStatus
    {
        public bool IsAvailable { get; set; }
    }

    // ── What the client sends ───────────────────────────────────────────────────────────────

    /// <summary>Filters for <c>GET /envelope</c>.</summary>
    internal sealed record DocumensoEnvelopeQuery(
        int Page = 1,
        int PerPage = 20,
        DocumensoEnvelopeStatus? Status = null,
        string? Search = null,
        bool NewestFirst = true);

    /// <summary>One recipient for <c>/envelope/recipient/create-many</c>.</summary>
    internal sealed record DocumensoRecipientDraft(
        string Email,
        string Name,
        DocumensoRecipientRole Role,
        int? SigningOrder = null);

    /// <summary>
    /// One field for <c>/envelope/field/create-many</c>. Exactly one of the two shapes the API
    /// accepts: placed (<see cref="Page"/> plus the four percentages) or anchored to a text
    /// placeholder in the PDF (<see cref="Placeholder"/>, optional size).
    /// </summary>
    /// <remarks>
    /// Geometry is <b>percent of the page</b>, 0–100, origin <b>top-left</b>, and
    /// <see cref="Page"/> is <b>1-based</b>. The live API names the position
    /// <c>positionX</c>/<c>positionY</c> — the design document's <c>pageX</c>/<c>pageY</c> came
    /// from older docs and is wrong for v2.18.
    /// </remarks>
    internal sealed record DocumensoFieldDraft
    {
        public long RecipientId { get; init; }
        public DocumensoFieldType Type { get; init; }
        public string? EnvelopeItemId { get; init; }
        public int? Page { get; init; }
        public double? PositionX { get; init; }
        public double? PositionY { get; init; }
        public double? Width { get; init; }
        public double? Height { get; init; }
        public string? Placeholder { get; init; }
        public bool? MatchAll { get; init; }

        public static DocumensoFieldDraft Placed(long recipientId, DocumensoFieldType type, int page,
            double x, double y, double width, double height, string? envelopeItemId = null) => new()
        {
            RecipientId = recipientId, Type = type, EnvelopeItemId = envelopeItemId, Page = page,
            PositionX = Clamp(x), PositionY = Clamp(y), Width = Clamp(width), Height = Clamp(height),
        };

        public static DocumensoFieldDraft AtPlaceholder(long recipientId, DocumensoFieldType type,
            string placeholder, bool matchAll = false, string? envelopeItemId = null) => new()
        {
            RecipientId = recipientId, Type = type, EnvelopeItemId = envelopeItemId,
            Placeholder = placeholder, MatchAll = matchAll ? true : null,
        };

        // The API rejects anything outside 0–100; canvas rounding at a page edge can produce 100.0001.
        private static double Clamp(double v) => Math.Clamp(v, 0, 100);
    }

    /// <summary>The optional <c>meta</c> block shared by create and distribute.</summary>
    internal sealed record DocumensoEnvelopeMeta
    {
        public string? Subject { get; init; }
        public string? Message { get; init; }
        public DocumensoSigningOrder? SigningOrder { get; init; }
        /// <summary>IANA zone name, e.g. "America/Chicago"; Documenso stamps dates in it.</summary>
        public string? Timezone { get; init; }
    }

    /// <summary>The <c>payload</c> part of <c>/envelope/create</c>.</summary>
    internal sealed record DocumensoCreateEnvelopeRequest
    {
        public string Title { get; init; } = "";
        /// <summary>Our own correlation id, echoed back by the API. Never a file path.</summary>
        public string? ExternalId { get; init; }
        public DocumensoEnvelopeMeta? Meta { get; init; }
    }
}
