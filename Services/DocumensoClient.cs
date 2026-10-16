using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TDPdf.Services
{
    /// <summary>
    /// How a Documenso call failed, coarse enough for the UI to pick a sentence from and for
    /// telemetry's <c>outcome</c> property, and nothing finer.
    /// </summary>
    internal enum DocumensoFailure
    {
        /// <summary>DNS, TLS, refused, reset — the service could not be reached at all.</summary>
        Network,
        /// <summary>The call exceeded its time budget (15 s metadata, 120 s transfer).</summary>
        Timeout,
        /// <summary>401 / 403: the token is wrong, expired, revoked, or not allowed this.</summary>
        AuthRejected,
        NotFound,
        /// <summary>429. Back off; never retry in a loop — self-hosted limits are undocumented.</summary>
        RateLimited,
        /// <summary>400 and other 4xx: the request was refused as invalid.</summary>
        Rejected,
        /// <summary>5xx.</summary>
        Server,
        /// <summary>2xx, but not the shape promised (unparseable JSON, a "PDF" that is not one).</summary>
        UnexpectedResponse,
    }

    /// <summary>
    /// A failed Documenso call. Carries the HTTP status, Documenso's error <em>code</em>, the
    /// method and the <em>path template</em> — and deliberately nothing else.
    /// </summary>
    /// <remarks>
    /// Never the response body (Documenso's error bodies are not ours to control: the live
    /// instance returns a full server stack trace on a 401), never the interpolated URL (it holds
    /// envelope ids), never the token, and no inner exception (an <c>HttpRequestException</c>
    /// message names the host, which telemetry may not carry). The message is therefore safe to
    /// reach a crash report even before <c>Sanitizer</c> sees it.
    /// </remarks>
    internal sealed class DocumensoApiException : Exception
    {
        public DocumensoFailure Failure { get; }
        public int? StatusCode { get; }
        /// <summary>Documenso's machine code (e.g. <c>UNAUTHORIZED</c>), restricted to <c>[A-Z0-9_]</c>.</summary>
        public string? ErrorCode { get; }
        public string Method { get; }
        public string PathTemplate { get; }
        /// <summary>From a 429's <c>Retry-After</c>, when the server sent one.</summary>
        public TimeSpan? RetryAfter { get; }

        public DocumensoApiException(DocumensoFailure failure, int? statusCode, string? errorCode,
            string method, string pathTemplate, TimeSpan? retryAfter = null)
            : base(Describe(failure, statusCode, errorCode, method, pathTemplate))
        {
            Failure = failure;
            StatusCode = statusCode;
            ErrorCode = errorCode;
            Method = method;
            PathTemplate = pathTemplate;
            RetryAfter = retryAfter;
        }

        private static string Describe(DocumensoFailure f, int? status, string? code, string method, string path)
        {
            string detail = status is int s ? $"HTTP {s}{(code is null ? "" : " " + code)}" : f.ToString().ToLowerInvariant();
            return $"Documenso {method} {path} failed: {detail} ({f})";
        }

        /// <summary>Maps an HTTP status to the coarse failure the UI speaks in.</summary>
        internal static DocumensoFailure Classify(int status) => status switch
        {
            401 or 403 => DocumensoFailure.AuthRejected,
            404 => DocumensoFailure.NotFound,
            429 => DocumensoFailure.RateLimited,
            >= 500 => DocumensoFailure.Server,
            >= 400 => DocumensoFailure.Rejected,
            _ => DocumensoFailure.UnexpectedResponse,
        };
    }

    /// <summary>
    /// A thin client over Documenso's v2 REST API. No UI, no WPF types, no state beyond the
    /// instance and the token. Every endpoint, method and shape here was checked against the live
    /// instance's OpenAPI document (<c>{BaseUrl}/api/v2/openapi.json</c>, v2.18.0, 2026-10-10).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>HTTP shape</b> follows the two existing call sites (<c>Ocr.cs</c>'s download client and
    /// <c>UpdateCheck</c>): a short-lived <see cref="HttpClient"/> per operation, an explicit
    /// timeout, an explicit User-Agent, <c>System.Text.Json</c>, and a
    /// <see cref="CancellationToken"/> on everything. No shared client, no factory, no retry
    /// library.
    /// </para>
    /// <para>
    /// <b>The token</b> is attached per request with
    /// <c>TryAddWithoutValidation("Authorization", token)</c> — bare, with no <c>Bearer</c> scheme,
    /// which is what Documenso expects and why the typed <c>Headers.Authorization</c> cannot be
    /// used. It is never put on <c>DefaultRequestHeaders</c> (so a client can never carry it to
    /// another host), never in a URL, and never in an exception. The two unauthenticated health
    /// endpoints are called without it.
    /// </para>
    /// </remarks>
    internal sealed class DocumensoClient
    {
        internal static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(15);
        internal static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(120);

        /// <summary>Documenso caps <c>get-many</c> at 20 ids per call.</summary>
        internal const int GetManyLimit = 20;

        /// <summary>
        /// Error bodies are read only to fish out the machine code, and only this much of them.
        /// </summary>
        private const int MaxErrorBodyBytes = 64 * 1024;

        internal static readonly JsonSerializerOptions Json = CreateJsonOptions();

        private readonly string _baseUrl;    // normalised, no trailing slash
        private readonly string _apiRoot;    // _baseUrl + "/api/v2"
        private readonly string _token;      // never logged, never in a URL, never in an exception
        private readonly Func<HttpMessageHandler>? _handlerFactory;

        /// <param name="baseUrl">The instance, e.g. <c>https://sign.tdpdf.com</c>. Normalised here.</param>
        /// <param name="token">The user's API token. Pass <c>""</c> for the unauthenticated health calls only.</param>
        /// <param name="handlerFactory">Test seam: supplies the handler for each per-operation client.</param>
        internal DocumensoClient(string baseUrl, string token, Func<HttpMessageHandler>? handlerFactory = null)
        {
            _baseUrl = DocumensoConfig.Normalise(baseUrl)
                ?? throw new ArgumentException("Not an acceptable Documenso address.", nameof(baseUrl));
            _apiRoot = _baseUrl + "/api/v2";
            _token = (token ?? "").Trim();
            if (!IsHeaderSafe(_token))
                // Deliberately does not echo the value: it may be most of a real token.
                throw new ArgumentException("The token contains characters that cannot be sent.", nameof(token));
            _handlerFactory = handlerFactory;
        }

        internal string BaseUrl => _baseUrl;

        /// <summary>
        /// True when every character is visible ASCII — no spaces, no CR/LF (header injection),
        /// nothing a paste from a rich-text source could have smuggled in.
        /// </summary>
        internal static bool IsHeaderSafe(string token)
            => token.All(c => c >= '!' && c <= '~');

        internal static string UserAgent
        {
            get
            {
                var v = typeof(DocumensoClient).Assembly.GetName().Version;
                return v is null ? "TDPdf" : $"TDPdf/{v}";
            }
        }

        // ── Account / health ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Validates the token with the cheapest authenticated call there is,
        /// <c>GET /envelope?perPage=1</c>. Throws <see cref="DocumensoApiException"/>
        /// (<see cref="DocumensoFailure.AuthRejected"/> for a bad token) rather than returning null,
        /// so the caller can tell "rejected" from "unreachable".
        /// </summary>
        /// <remarks>See <see cref="DocumensoAccount"/> for why only the team is known here.</remarks>
        internal async Task<DocumensoAccount> ProbeAsync(CancellationToken ct)
        {
            var page = await GetJsonAsync<DocumensoEnvelopePage>("/envelope", "/envelope?perPage=1", ct).ConfigureAwait(false);
            string? teamUrl = page.Data.FirstOrDefault()?.Team?.Url;
            return new DocumensoAccount(null, null, string.IsNullOrWhiteSpace(teamUrl) ? null : teamUrl);
        }

        /// <summary><c>GET {BaseUrl}/api/health</c> — unauthenticated container health.</summary>
        internal Task<DocumensoHealth> GetHealthAsync(CancellationToken ct)
            => SendForJsonAsync<DocumensoHealth>(HttpMethod.Get, "/api/health", _baseUrl + "/api/health",
                null, authenticated: false, MetadataTimeout, ct);

        /// <summary><c>GET {BaseUrl}/api/certificate-status</c> — can the instance seal documents?</summary>
        internal Task<DocumensoCertificateStatus> GetCertificateStatusAsync(CancellationToken ct)
            => SendForJsonAsync<DocumensoCertificateStatus>(HttpMethod.Get, "/api/certificate-status",
                _baseUrl + "/api/certificate-status", null, authenticated: false, MetadataTimeout, ct);

        // ── Sending ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>POST /envelope/create</c> (multipart): a <c>payload</c> JSON part and one
        /// <c>files</c> part. Returns the new envelope id. Recipients (and their fields) travel
        /// inline when <see cref="DocumensoCreateEnvelopeRequest.Recipients"/> is set; otherwise
        /// they are added by the follow-up <c>create-many</c> calls.
        /// </summary>
        /// <remarks>
        /// Documenso scans an uploaded PDF for <c>{{signature, r1}}</c>-style placeholders on its
        /// own, whites the text out, and creates the fields in this same call — which is why Send
        /// for Signature passes its recipients here rather than afterwards (see
        /// <see cref="DocumensoCreateEnvelopeRequest.Recipients"/>). <paramref name="pdf"/> is read
        /// to the end and left open — the caller owns it.
        /// </remarks>
        internal async Task<string> CreateEnvelopeAsync(DocumensoCreateEnvelopeRequest request, Stream pdf,
            string fileName, CancellationToken ct)
        {
            using var multipart = BuildCreateEnvelopeContent(request, pdf, fileName);
            var created = await SendForJsonAsync<IdResponse>(HttpMethod.Post, "/envelope/create",
                _apiRoot + "/envelope/create", multipart, authenticated: true, TransferTimeout, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(created.Id))
                throw new DocumensoApiException(DocumensoFailure.UnexpectedResponse, 200, null, "POST", "/envelope/create");
            return created.Id;
        }

        /// <summary>The multipart body for <see cref="CreateEnvelopeAsync"/>. Separate so tests can inspect it.</summary>
        internal static MultipartFormDataContent BuildCreateEnvelopeContent(DocumensoCreateEnvelopeRequest request,
            Stream pdf, string fileName)
        {
            var payload = new
            {
                title = request.Title,
                type = "DOCUMENT",
                externalId = request.ExternalId,
                meta = request.Meta,
                recipients = request.Recipients,
            };

            // Copied rather than wrapped: StreamContent disposes its stream, and this one is the
            // caller's.
            var buffer = new MemoryStream();
            pdf.CopyTo(buffer);

            var multipart = new MultipartFormDataContent();
            var json = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");
            multipart.Add(json, "payload");
            var file = new ByteArrayContent(buffer.ToArray());
            file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            multipart.Add(file, "files", SafeFileName(fileName));
            return multipart;
        }

        /// <summary>
        /// <c>POST /envelope/recipient/create-many</c>. Returns the created recipients — their
        /// <c>id</c>s are what every field must reference.
        /// </summary>
        internal async Task<IReadOnlyList<DocumensoRecipient>> CreateRecipientsAsync(string envelopeId,
            IReadOnlyList<DocumensoRecipientDraft> recipients, CancellationToken ct)
        {
            var body = new { envelopeId, data = recipients };
            var r = await PostJsonAsync<DataList<DocumensoRecipient>>("/envelope/recipient/create-many", body, ct).ConfigureAwait(false);
            return r.Data;
        }

        /// <summary>
        /// <c>POST /envelope/field/create-many</c>. Fields cannot be changed once the envelope is
        /// distributed, so this must complete before <see cref="DistributeAsync"/>.
        /// </summary>
        internal async Task<IReadOnlyList<DocumensoField>> CreateFieldsAsync(string envelopeId,
            IReadOnlyList<DocumensoFieldDraft> fields, CancellationToken ct)
        {
            var body = new { envelopeId, data = fields };
            var r = await PostJsonAsync<DataList<DocumensoField>>("/envelope/field/create-many", body, ct).ConfigureAwait(false);
            return r.Data;
        }

        /// <summary>
        /// <c>POST /envelope/distribute</c>: sends it. The response is the only place the API
        /// hands back each recipient's <c>signingUrl</c>, which is what self-signing opens.
        /// </summary>
        internal async Task<IReadOnlyList<DocumensoDistributedRecipient>> DistributeAsync(string envelopeId,
            DocumensoEnvelopeMeta? meta, CancellationToken ct)
        {
            var body = new { envelopeId, meta };
            var r = await PostJsonAsync<DistributeResponse>("/envelope/distribute", body, ct).ConfigureAwait(false);
            return r.Recipients;
        }

        // ── Reading ────────────────────────────────────────────────────────────────────────

        /// <summary><c>GET /envelope/{envelopeId}</c> — status, recipients and envelope items.</summary>
        internal Task<DocumensoEnvelope> GetEnvelopeAsync(string envelopeId, CancellationToken ct)
            => GetJsonAsync<DocumensoEnvelope>("/envelope/{envelopeId}", "/envelope/" + Uri.EscapeDataString(envelopeId), ct);

        /// <summary><c>GET /envelope</c> — documents only (templates excluded).</summary>
        internal Task<DocumensoEnvelopePage> ListEnvelopesAsync(DocumensoEnvelopeQuery q, CancellationToken ct)
            => GetJsonAsync<DocumensoEnvelopePage>("/envelope", "/envelope" + BuildListQuery(q), ct);

        internal static string BuildListQuery(DocumensoEnvelopeQuery q)
        {
            var parts = new List<string>
            {
                "page=" + Math.Max(1, q.Page),
                "perPage=" + Math.Clamp(q.PerPage, 1, 100),   // the API's own bounds
                "type=DOCUMENT",
                "orderByColumn=createdAt",
                "orderByDirection=" + (q.NewestFirst ? "desc" : "asc"),
            };
            if (q.Status is { } s && s != DocumensoEnvelopeStatus.Unknown)
                parts.Add("status=" + DocumensoEnumConverter<DocumensoEnvelopeStatus>.ToWire(s));
            if (!string.IsNullOrWhiteSpace(q.Search))
                parts.Add("query=" + Uri.EscapeDataString(q.Search.Trim()));
            return "?" + string.Join("&", parts);
        }

        /// <summary><c>POST /envelope/get-many</c> by envelope id, 1–<see cref="GetManyLimit"/> at a time.</summary>
        internal async Task<IReadOnlyList<DocumensoEnvelope>> GetManyAsync(IReadOnlyList<string> envelopeIds, CancellationToken ct)
        {
            if (envelopeIds.Count == 0) return Array.Empty<DocumensoEnvelope>();
            if (envelopeIds.Count > GetManyLimit)
                throw new ArgumentOutOfRangeException(nameof(envelopeIds), $"At most {GetManyLimit} ids per call.");
            var body = new { ids = new { type = "envelopeId", ids = envelopeIds } };
            var r = await PostJsonAsync<DataList<DocumensoEnvelope>>("/envelope/get-many", body, ct).ConfigureAwait(false);
            return r.Data;
        }

        // ── Downloads ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>GET /envelope/item/{envelopeItemId}/download?version=…</c> into
        /// <paramref name="destFile"/>, via <c>.part</c> then a move, so a half-written signed PDF
        /// never appears under the real name.
        /// </summary>
        internal Task DownloadItemAsync(string envelopeItemId, DocumensoDownloadVersion version, string destFile, CancellationToken ct)
            => DownloadAsync("/envelope/item/{envelopeItemId}/download",
                $"/envelope/item/{Uri.EscapeDataString(envelopeItemId)}/download?version={version.ToString().ToLowerInvariant()}",
                destFile, ct);

        /// <summary><c>GET /envelope/{envelopeId}/certificate/download</c> — the certificate of completion.</summary>
        internal Task DownloadCertificateAsync(string envelopeId, string destFile, CancellationToken ct)
            => DownloadAsync("/envelope/{envelopeId}/certificate/download",
                $"/envelope/{Uri.EscapeDataString(envelopeId)}/certificate/download", destFile, ct);

        /// <summary><c>GET /envelope/{envelopeId}/audit-log/download</c> — the audit trail as a PDF.</summary>
        internal Task DownloadAuditLogAsync(string envelopeId, string destFile, CancellationToken ct)
            => DownloadAsync("/envelope/{envelopeId}/audit-log/download",
                $"/envelope/{Uri.EscapeDataString(envelopeId)}/audit-log/download", destFile, ct);

        // ── Browser sign-in (TDPdf Signing connect contract) ──────────────────────────────

        /// <summary>
        /// Whether this instance offers browser sign-in at all: an unauthenticated
        /// <c>GET {BaseUrl}/connect/tdpdf</c> with no parameters. The fork's page answers that with
        /// a redirect to its sign-in page (or, signed in, "this link is not valid"); an instance
        /// without the page answers <b>404</b>. So 404 is "no", any other HTTP answer is "yes", and
        /// an unreachable service throws as every other call does. Redirects are followed by the
        /// handler, which is harmless — nothing about the user is sent and nothing is changed.
        /// </summary>
        internal async Task<bool> IsBrowserSignInSupportedAsync(CancellationToken ct)
        {
            try
            {
                await SendForJsonAsync<JsonElement>(HttpMethod.Get, DocumensoConnectContract.AuthorizePath,
                    _baseUrl + DocumensoConnectContract.AuthorizePath, null, authenticated: false, MetadataTimeout, ct).ConfigureAwait(false);
                return true;
            }
            catch (DocumensoApiException ex) when (ex.StatusCode is int status)
            {
                // A 200 HTML page lands here too, as UnexpectedResponse (it is not JSON).
                return status != 404;
            }
        }

        /// <summary>
        /// Trades the one-time sign-in <paramref name="code"/> and its PKCE
        /// <paramref name="codeVerifier"/> for an API token: <c>POST {BaseUrl}/api/connect/token</c>
        /// with <c>{"code","code_verifier"}</c>, answered <c>{"token"}</c> — the contract of our
        /// Documenso fork's patch 0004 (<c>apps/remix/server/api/connect/connect.ts</c>).
        /// Unauthenticated — the code and verifier are the credential. Every refusal (wrong
        /// verifier, expired, tampered or reused code) is one answer, 400
        /// <c>{"error":"invalid_grant"}</c>, which surfaces as <see cref="DocumensoFailure.Rejected"/>
        /// with <see cref="DocumensoApiException.ErrorCode"/> <c>INVALID_GRANT</c>. The returned token
        /// has not been validated yet; the caller probes it before storing it.
        /// </summary>
        /// <param name="redirectUri">
        /// Not sent: the fork binds the redirect into the code itself when it issues it, and its
        /// token endpoint takes only the code and the verifier. Kept in the signature so the flow
        /// (and a stricter server later) has it.
        /// </param>
        internal async Task<DocumensoConnectGrant> ExchangeConnectCodeAsync(string code, string codeVerifier,
            string redirectUri, CancellationToken ct)
        {
            _ = redirectUri;
            var body = new Dictionary<string, string> { ["code"] = code, ["code_verifier"] = codeVerifier };
            var content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
            var r = await SendForJsonAsync<ConnectTokenResponse>(HttpMethod.Post, DocumensoConnectContract.TokenPath,
                _baseUrl + DocumensoConnectContract.TokenPath, content, authenticated: false, MetadataTimeout, ct).ConfigureAwait(false);
            string token = (r.Token ?? "").Trim();
            if (token.Length == 0 || !IsHeaderSafe(token))
                throw new DocumensoApiException(DocumensoFailure.UnexpectedResponse, 200, null, "POST", DocumensoConnectContract.TokenPath);
            // The fork returns the token alone; the account's team comes from the probe that
            // follows, and its email from the first request sent (as with a pasted token). Extra
            // fields are read if a later server adds them.
            static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            return new DocumensoConnectGrant(token, Clean(r.Email), Clean(r.Name), Clean(r.TeamUrl),
                DocumensoRequestView.ParseApiDate(r.ExpiresAt));
        }

        // ── Destructive ────────────────────────────────────────────────────────────────────

        /// <summary><c>POST /envelope/cancel</c>. Recipients mid-signature lose access; confirm first.</summary>
        internal Task CancelAsync(string envelopeId, string? reason, CancellationToken ct)
            => PostJsonAsync<SuccessResponse>("/envelope/cancel",
                new { envelopeId, reason = string.IsNullOrWhiteSpace(reason) ? null : reason }, ct);

        /// <summary><c>POST /envelope/delete</c>. The server refuses a completed envelope.</summary>
        internal Task DeleteAsync(string envelopeId, CancellationToken ct)
            => PostJsonAsync<SuccessResponse>("/envelope/delete", new { envelopeId }, ct);

        // ── Plumbing ───────────────────────────────────────────────────────────────────────

        private Task<T> GetJsonAsync<T>(string pathTemplate, string relative, CancellationToken ct)
            => SendForJsonAsync<T>(HttpMethod.Get, pathTemplate, _apiRoot + relative, null, authenticated: true, MetadataTimeout, ct);

        private Task<T> PostJsonAsync<T>(string path, object body, CancellationToken ct)
        {
            var content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
            return SendForJsonAsync<T>(HttpMethod.Post, path, _apiRoot + path, content, authenticated: true, MetadataTimeout, ct);
        }

        private async Task<T> SendForJsonAsync<T>(HttpMethod method, string pathTemplate, string url,
            HttpContent? content, bool authenticated, TimeSpan timeout, CancellationToken ct)
        {
            using var http = NewHttpClient(timeout);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(timeout);
            using var resp = await SendAsync(http, method, pathTemplate, url, content, authenticated,
                HttpCompletionOption.ResponseContentRead, ct, budget.Token).ConfigureAwait(false);
            try
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
                var value = await JsonSerializer.DeserializeAsync<T>(stream, Json, budget.Token).ConfigureAwait(false);
                return value ?? throw new JsonException("empty body");
            }
            catch (JsonException)
            {
                throw new DocumensoApiException(DocumensoFailure.UnexpectedResponse, (int)resp.StatusCode, null,
                    method.Method, pathTemplate);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DocumensoApiException(DocumensoFailure.Timeout, null, null, method.Method, pathTemplate);
            }
        }

        private async Task DownloadAsync(string pathTemplate, string relative, string destFile, CancellationToken ct)
        {
            using var http = NewHttpClient(TransferTimeout);
            // HttpClient.Timeout stops at the headers when streaming, so the body needs its own budget.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TransferTimeout);
            using var resp = await SendAsync(http, HttpMethod.Get, pathTemplate, _apiRoot + relative, null,
                authenticated: true, HttpCompletionOption.ResponseHeadersRead, ct, budget.Token).ConfigureAwait(false);

            string part = destFile + ".part";
            try
            {
                await using (var src = await resp.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false))
                await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    // The spec promises application/pdf; check the bytes rather than the header, so
                    // an error page served with a 200 can never be saved as "the signed copy".
                    byte[] buf = new byte[81920];
                    bool first = true;
                    int n;
                    while ((n = await src.ReadAsync(buf, budget.Token).ConfigureAwait(false)) > 0)
                    {
                        if (first)
                        {
                            first = false;
                            if (n < 5 || buf[0] != '%' || buf[1] != 'P' || buf[2] != 'D' || buf[3] != 'F' || buf[4] != '-')
                                throw new DocumensoApiException(DocumensoFailure.UnexpectedResponse, (int)resp.StatusCode,
                                    null, "GET", pathTemplate);
                        }
                        await dst.WriteAsync(buf.AsMemory(0, n), budget.Token).ConfigureAwait(false);
                    }
                    if (first)
                        throw new DocumensoApiException(DocumensoFailure.UnexpectedResponse, (int)resp.StatusCode,
                            null, "GET", pathTemplate);
                }
                MoveIntoPlace(part, destFile);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryDelete(part);
                throw new DocumensoApiException(DocumensoFailure.Timeout, null, null, "GET", pathTemplate);
            }
            catch
            {
                TryDelete(part);
                throw;
            }
        }

        /// <summary>
        /// Puts a finished <c>.part</c> file under its real name. Never <c>File.Replace</c>:
        /// cloud-sync folders (OneDrive) refuse it — see the note in PdfDocumentService — and
        /// saving a signed PDF into OneDrive is the common case. A plain overwriting move first,
        /// then an explicit delete-then-move when the destination is locked against overwrite.
        /// </summary>
        internal static void MoveIntoPlace(string part, string dest)
        {
            try
            {
                File.Move(part, dest, overwrite: true);
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && File.Exists(dest))
            {
                File.Delete(dest);
                File.Move(part, dest);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }

        private HttpClient NewHttpClient(TimeSpan timeout)
        {
            var http = _handlerFactory is null ? new HttpClient() : new HttpClient(_handlerFactory(), disposeHandler: true);
            http.Timeout = timeout;
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return http;
        }

        /// <summary>
        /// Builds and sends one request, and turns every failure into a
        /// <see cref="DocumensoApiException"/> — except the caller's own cancellation, which stays
        /// an <see cref="OperationCanceledException"/> so "you pressed Esc" is never reported as
        /// an error.
        /// </summary>
        private async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpMethod method, string pathTemplate,
            string url, HttpContent? content, bool authenticated, HttpCompletionOption completion,
            CancellationToken callerToken, CancellationToken budgetToken)
        {
            using var req = BuildRequest(method, url, content, authenticated);
            HttpResponseMessage resp;
            try
            {
                resp = await http.SendAsync(req, completion, budgetToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new DocumensoApiException(DocumensoFailure.Timeout, null, null, method.Method, pathTemplate);
            }
            catch (HttpRequestException)
            {
                throw new DocumensoApiException(DocumensoFailure.Network, null, null, method.Method, pathTemplate);
            }

            if (resp.IsSuccessStatusCode) return resp;

            using (resp)
            {
                int status = (int)resp.StatusCode;
                string? code = await TryReadErrorCodeAsync(resp, budgetToken).ConfigureAwait(false);
                TimeSpan? retryAfter = resp.Headers.RetryAfter?.Delta;
                if (retryAfter is null && resp.Headers.RetryAfter?.Date is DateTimeOffset at)
                    retryAfter = at - DateTimeOffset.UtcNow;
                throw new DocumensoApiException(DocumensoApiException.Classify(status), status, code,
                    method.Method, pathTemplate, retryAfter);
            }
        }

        /// <summary>The request, token attached per request. Internal so tests can inspect one without sending it.</summary>
        internal HttpRequestMessage BuildRequest(HttpMethod method, string url, HttpContent? content, bool authenticated)
        {
            var req = new HttpRequestMessage(method, url) { Content = content };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (authenticated)
                req.Headers.TryAddWithoutValidation("Authorization", _token);
            return req;
        }

        /// <summary>
        /// Documenso's machine-readable error code, and only that. The live instance wraps a 401
        /// as <c>{"code":"INTERNAL_SERVER_ERROR","data":{"code":"UNAUTHORIZED",…}}</c>, so the
        /// nested <c>data.code</c> is the truthful one and wins; an OAuth-style <c>error</c> is the
        /// last resort. Anything that is not a short
        /// UPPER_SNAKE identifier is discarded, which is what guarantees no body text can ride out
        /// in an exception.
        /// </summary>
        internal static async Task<string?> TryReadErrorCodeAsync(HttpResponseMessage resp, CancellationToken ct)
        {
            try
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var buffer = new MemoryStream();
                byte[] chunk = new byte[8192];
                int n;
                while (buffer.Length < MaxErrorBodyBytes && (n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                    buffer.Write(chunk, 0, n);
                return ExtractErrorCode(buffer.ToArray());
            }
            catch
            {
                return null;
            }
        }

        internal static string? ExtractErrorCode(byte[] body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                string? nested = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                                 && data.TryGetProperty("code", out var dc) && dc.ValueKind == JsonValueKind.String
                    ? dc.GetString() : null;
                string? top = root.TryGetProperty("code", out var tc) && tc.ValueKind == JsonValueKind.String
                    ? tc.GetString() : null;
                // OAuth-style {"error":"invalid_grant"}, which the fork's sign-in token endpoint
                // answers with; upper-cased into the same UPPER_SNAKE vocabulary.
                string? oauth = root.TryGetProperty("error", out var te) && te.ValueKind == JsonValueKind.String
                    ? te.GetString()?.ToUpperInvariant() : null;
                return SafeCode(nested) ?? SafeCode(top) ?? SafeCode(oauth);
            }
            catch
            {
                return null;
            }
        }

        private static string? SafeCode(string? code)
            => !string.IsNullOrEmpty(code) && code.Length <= 64 && code.All(c => (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')
                ? code : null;

        /// <summary>
        /// The upload's file name — it becomes the envelope item's title on the server, so it is
        /// reduced to the bare name: never a directory, never anything a header cannot carry.
        /// </summary>
        internal static string SafeFileName(string fileName)
        {
            string name = Path.GetFileName((fileName ?? "").Replace('\\', '/').Split('/').Last());
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(char.IsControl(c) || c == '"' ? '_' : c);
            string s = sb.ToString().Trim();
            if (s.Length == 0) s = "document.pdf";
            if (!s.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) s += ".pdf";
            return s;
        }

        private static JsonSerializerOptions CreateJsonOptions()
        {
            // Web defaults: camelCase names, case-insensitive reads, and numbers accepted as
            // strings — the OpenAPI types every id as "number", which is not a promise about JSON.
            var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            o.Converters.Add(new DocumensoEnumConverter<DocumensoEnvelopeStatus>());
            o.Converters.Add(new DocumensoEnumConverter<DocumensoRecipientRole>());
            o.Converters.Add(new DocumensoEnumConverter<DocumensoSigningStatus>());
            o.Converters.Add(new DocumensoEnumConverter<DocumensoReadStatus>());
            o.Converters.Add(new DocumensoEnumConverter<DocumensoSendStatus>());
            o.Converters.Add(new DocumensoEnumConverter<DocumensoFieldType>());
            o.Converters.Add(new DocumensoEnumConverter<DocumensoSigningOrder>());
            return o;
        }

        // Response envelopes that exist only to be unwrapped.
        private sealed class IdResponse { public string Id { get; set; } = ""; }
        private sealed class SuccessResponse { public bool Success { get; set; } }
        private sealed class DataList<T> { public List<T> Data { get; set; } = new(); }
        private sealed class ConnectTokenResponse
        {
            public string? Token { get; set; }
            public string? ExpiresAt { get; set; }
            public string? Email { get; set; }
            public string? Name { get; set; }
            public string? TeamUrl { get; set; }
        }
        private sealed class DistributeResponse
        {
            public bool Success { get; set; }
            public string Id { get; set; } = "";
            public List<DocumensoDistributedRecipient> Recipients { get; set; } = new();
        }
    }
}
