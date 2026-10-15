using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TDPdf.Diagnostics;
using TDPdf.Services;

/// <summary>
/// Pins Electronic Signature's WPF-free core: <see cref="DocumensoConfig"/> resolution and
/// normalisation, the requests <see cref="DocumensoClient"/> builds, how it maps failures, and
/// the <see cref="Sanitizer"/> token redaction.
/// </summary>
/// <remarks>
/// The client is driven through a recording <see cref="HttpMessageHandler"/> rather than mocks of
/// its own methods, so what is asserted is the request that would actually leave the machine:
/// the Authorization header is the bare token, the token is never in a URL or an exception, and
/// the multipart parts carry the names the live OpenAPI document requires. Shapes were checked
/// against https://sign.tdpdf.com/api/v2/openapi.json (Documenso v2.18.0) on 2026-10-10.
/// </remarks>
internal static class Documenso
{
    private const string Token = "api_TESTtoken0123456789abcdef";
    private const string Base = "https://sign.tdpdf.com";

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("Electronic Signature — configuration");
        Normalisation(Check);
        Resolution(Check);
        EndToEndResolution(Check);

        Console.WriteLine();
        Console.WriteLine("Electronic Signature — client requests");
        Requests(Check).GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine("Electronic Signature — failures");
        Failures(Check).GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine("Electronic Signature — downloads");
        Downloads(Check, tmp).GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine("Electronic Signature — wire details");
        Wire(Check);
    }

    // ── Config ──────────────────────────────────────────────────────────────────────────────

    private static void Normalisation(Action<string, bool, string> Check)
    {
        void N(string label, string? raw, string? want)
        {
            string? got = DocumensoConfig.Normalise(raw);
            Check(label, got == want, $"'{raw}' -> '{got ?? "null"}'");
        }

        N("trailing slash is stripped", "https://sign.tdpdf.com/", "https://sign.tdpdf.com");
        N("whitespace, case and doubled slashes normalise", "  HTTPS://Sign.TDPdf.com//  ", "https://sign.tdpdf.com");
        N("a default port is dropped", "https://sign.tdpdf.com:443", "https://sign.tdpdf.com");
        N("a non-default port and a sub-path survive", "https://docs.example.org:8443/sign/", "https://docs.example.org:8443/sign");
        N("plain http to a real host is refused", "http://sign.tdpdf.com", null);
        N("plain http to localhost is allowed (development)", "http://localhost:3000/", "http://localhost:3000");
        N("plain http to 127.0.0.1 is allowed (development)", "http://127.0.0.1:3000", "http://127.0.0.1:3000");
        N("a bare host name is not an absolute URL", "sign.tdpdf.com", null);
        N("credentials in the URL are refused", "https://me:secret@sign.tdpdf.com", null);
        N("a query string is refused", "https://sign.tdpdf.com/?team=x", null);
        N("another scheme is refused", "ftp://sign.tdpdf.com", null);
        N("empty is no URL", "   ", null);
    }

    private static void Resolution(Action<string, bool, string> Check)
    {
        static string Show((string BaseUrl, DocumensoConfig.Source From)? r)
            => r is { } v ? $"{v.From}:{v.BaseUrl}" : "none";

        var nothing = new DocumensoConfig.Inputs(true, null, null, true, null);
        var r = DocumensoConfig.Resolve(nothing);
        Check("with nothing configured the built-in default is used (2026-10-10 decision)",
              r is { From: DocumensoConfig.Source.Default, BaseUrl: "https://sign.tdpdf.com" }, Show(r));

        r = DocumensoConfig.Resolve(new(false, "https://dev.example", "https://org.example", true, "https://me.example"));
        Check("policy Enabled=0 removes the feature even with every tier set", r is null, Show(r));

        r = DocumensoConfig.Resolve(new(true, "https://dev.example", "https://org.example", true, "https://me.example"));
        Check("the environment variable outranks everything", r is { From: DocumensoConfig.Source.Environment, BaseUrl: "https://dev.example" }, Show(r));

        r = DocumensoConfig.Resolve(new(true, null, "https://org.example", true, "https://me.example"));
        Check("a user URL wins over policy when the policy allows overriding",
              r is { From: DocumensoConfig.Source.User, BaseUrl: "https://me.example" }, Show(r));

        r = DocumensoConfig.Resolve(new(true, null, "https://org.example", false, "https://me.example"));
        Check("AllowUserOverride=0 pins the policy URL", r is { From: DocumensoConfig.Source.Policy, BaseUrl: "https://org.example" }, Show(r));

        r = DocumensoConfig.Resolve(new(true, null, "https://org.example", true, null));
        Check("a policy URL beats the default", r is { From: DocumensoConfig.Source.Policy }, Show(r));

        r = DocumensoConfig.Resolve(new(true, null, null, true, "https://me.example/"));
        Check("a user URL alone is used, normalised", r is { From: DocumensoConfig.Source.User, BaseUrl: "https://me.example" }, Show(r));

        // Fail closed: a typo'd tier must never fall through to the public default and send an
        // organisation's documents somewhere it did not choose.
        r = DocumensoConfig.Resolve(new(true, null, "http://org.example", true, null));
        Check("a malformed policy URL resolves to NO endpoint, not the default", r is null, Show(r));
        r = DocumensoConfig.Resolve(new(true, "not a url", null, true, null));
        Check("a malformed environment URL resolves to NO endpoint", r is null, Show(r));
        r = DocumensoConfig.Resolve(new(true, null, null, true, "sign.example.com"));
        Check("a malformed user URL resolves to NO endpoint", r is null, Show(r));
        r = DocumensoConfig.Resolve(new(true, "  ", null, true, ""));
        Check("blank values count as unset", r is { From: DocumensoConfig.Source.Default }, Show(r));
    }

    private static void EndToEndResolution(Action<string, bool, string> Check)
    {
        // Through the real readers. There is no registry off Windows, so the policy tiers read as
        // absent here — the truthful answer for a machine with no policy.
        string? savedEnv = Environment.GetEnvironmentVariable(DocumensoConfig.BaseUrlEnvironmentVariableName);
        string savedUser = TDPdf.Properties.Settings.Default.DocumensoBaseUrl;
        try
        {
            Environment.SetEnvironmentVariable(DocumensoConfig.BaseUrlEnvironmentVariableName, null);
            TDPdf.Properties.Settings.Default.DocumensoBaseUrl = "";
            var r = DocumensoConfig.TryResolveBaseUrl();
            Check("a clean machine resolves to the default and HasEndpoint() is true",
                  r?.From == DocumensoConfig.Source.Default && DocumensoConfig.HasEndpoint(), $"{r?.From}");

            TDPdf.Properties.Settings.Default.DocumensoBaseUrl = "https://me.example/";
            r = DocumensoConfig.TryResolveBaseUrl();
            Check("the Settings value is read", r is { From: DocumensoConfig.Source.User, BaseUrl: "https://me.example" }, $"{r?.From}");

            Environment.SetEnvironmentVariable(DocumensoConfig.BaseUrlEnvironmentVariableName, "http://localhost:3000");
            r = DocumensoConfig.TryResolveBaseUrl();
            Check("TDPDF_DOCUMENSO_BASEURL is read and outranks Settings",
                  r is { From: DocumensoConfig.Source.Environment, BaseUrl: "http://localhost:3000" }, $"{r?.From}");

            Check("the user may override when no policy is set", DocumensoConfig.IsUserOverrideAllowed(), "");
        }
        finally
        {
            Environment.SetEnvironmentVariable(DocumensoConfig.BaseUrlEnvironmentVariableName, savedEnv);
            TDPdf.Properties.Settings.Default.DocumensoBaseUrl = savedUser;
        }
    }

    // ── Requests ────────────────────────────────────────────────────────────────────────────

    private static async Task Requests(Action<string, bool, string> Check)
    {
        var h = new Recorder();
        var client = new DocumensoClient(Base + "/", Token, () => h);

        // Probe.
        h.Respond(HttpStatusCode.OK,
            """{"data":[{"id":"envelope_1","title":"T","status":"PENDING","team":{"id":7,"url":"personal_abcd"},"recipients":[]}],"count":1,"currentPage":1,"perPage":1,"totalPages":1}""");
        var account = await client.ProbeAsync(CancellationToken.None);
        var probe = h.Last;
        Check("probe is GET /api/v2/envelope?perPage=1", probe.Method == "GET" && probe.Url == Base + "/api/v2/envelope?perPage=1", probe.Url);
        Check("Authorization is the bare token — no Bearer scheme", probe.Authorization == Token, probe.Authorization ?? "(none)");
        Check("the token is not in the URL", !probe.Url.Contains(Token), probe.Url);
        Check("User-Agent identifies TDPdf", probe.UserAgent.StartsWith("TDPdf", StringComparison.Ordinal), probe.UserAgent);
        Check("the probe learns the team (for the token page link)", account.TeamUrl == "personal_abcd", account.TeamUrl ?? "null");
        Check("the probe claims no email it cannot know", account.Email is null, account.Email ?? "null");

        // Unauthenticated health endpoints carry no token.
        h.Respond(HttpStatusCode.OK, """{"status":"ok","checks":{"database":{"status":"ok"},"certificate":{"status":"ok"}}}""");
        var health = await client.GetHealthAsync(CancellationToken.None);
        Check("health is GET {base}/api/health, outside /api/v2", h.Last.Url == Base + "/api/health", h.Last.Url);
        Check("health carries NO Authorization header", h.Last.Authorization is null, h.Last.Authorization ?? "(none)");
        Check("health parses ok", health.IsOk && health.Checks?.Certificate?.Status == "ok", health.Status);
        h.Respond(HttpStatusCode.OK, """{"isAvailable":false,"timestamp":"2026-10-10T23:35:27.998Z"}""");
        var cert = await client.GetCertificateStatusAsync(CancellationToken.None);
        Check("certificate-status: no token, isAvailable parsed",
              h.Last.Authorization is null && h.Last.Url == Base + "/api/certificate-status" && !cert.IsAvailable, h.Last.Url);

        // Create (multipart).
        h.Respond(HttpStatusCode.OK, """{"id":"envelope_new"}""");
        byte[] pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%fixture\n");
        using var pdfStream = new MemoryStream(pdf);
        string id = await client.CreateEnvelopeAsync(new DocumensoCreateEnvelopeRequest
        {
            Title = "Lease",
            ExternalId = "tdpdf-1",
            Meta = new DocumensoEnvelopeMeta { Subject = "Please sign", SigningOrder = DocumensoSigningOrder.Sequential },
        }, pdfStream, @"C:\Users\someone\Documents\Lease.pdf", CancellationToken.None);
        var create = h.Last;
        string body = Encoding.UTF8.GetString(create.Body);
        Check("create is POST /api/v2/envelope/create", create.Method == "POST" && create.Url == Base + "/api/v2/envelope/create", create.Url);
        Check("create is multipart/form-data", create.ContentType?.StartsWith("multipart/form-data") == true, create.ContentType ?? "");
        Check("create has a 'payload' part", System.Text.RegularExpressions.Regex.IsMatch(body, "name=\"?payload\"?"), "");
        Check("create has a 'files' part named with the bare file name",
              System.Text.RegularExpressions.Regex.IsMatch(body, "name=\"?files\"?") && body.Contains("Lease.pdf") && !body.Contains("someone"), "");
        Check("the file part is application/pdf and carries the bytes",
              body.Contains("Content-Type: application/pdf") && body.Contains("%PDF-1.7"), "");
        var payloadJson = ExtractJsonPart(body);
        Check("payload: title, type DOCUMENT, externalId, meta.signingOrder SEQUENTIAL",
              payloadJson is { } pj
              && pj.GetProperty("title").GetString() == "Lease"
              && pj.GetProperty("type").GetString() == "DOCUMENT"
              && pj.GetProperty("externalId").GetString() == "tdpdf-1"
              && pj.GetProperty("meta").GetProperty("signingOrder").GetString() == "SEQUENTIAL",
              payloadJson?.ToString() ?? "(no JSON part)");
        Check("create returns the envelope id", id == "envelope_new", id);
        Check("the caller's stream is left open", pdfStream.CanRead, "");

        // Recipients.
        h.Respond(HttpStatusCode.OK,
            """{"data":[{"id":41,"envelopeId":"envelope_new","email":"a@example.com","name":"A","role":"SIGNER","readStatus":"NOT_OPENED","signingStatus":"NOT_SIGNED","sendStatus":"NOT_SENT","signingOrder":1,"token":"rtok"}]}""");
        var recips = await client.CreateRecipientsAsync("envelope_new",
            new[] { new DocumensoRecipientDraft("a@example.com", "A", DocumensoRecipientRole.Signer, 1),
                    new DocumensoRecipientDraft("c@example.com", "C", DocumensoRecipientRole.Cc) }, CancellationToken.None);
        var rb = JsonDocument.Parse(h.Last.Body).RootElement;
        Check("recipients: POST /envelope/recipient/create-many", h.Last.Url == Base + "/api/v2/envelope/recipient/create-many", h.Last.Url);
        Check("recipients body: envelopeId + data[], roles SIGNER and CC, unset signingOrder omitted",
              rb.GetProperty("envelopeId").GetString() == "envelope_new"
              && rb.GetProperty("data")[0].GetProperty("role").GetString() == "SIGNER"
              && rb.GetProperty("data")[0].GetProperty("signingOrder").GetInt32() == 1
              && rb.GetProperty("data")[1].GetProperty("role").GetString() == "CC"
              && !rb.GetProperty("data")[1].TryGetProperty("signingOrder", out _),
              Encoding.UTF8.GetString(h.Last.Body));
        Check("recipients response: id and enums parsed",
              recips.Count == 1 && recips[0].Id == 41 && recips[0].SigningStatus == DocumensoSigningStatus.NotSigned
              && recips[0].Role == DocumensoRecipientRole.Signer, "");

        // Fields.
        h.Respond(HttpStatusCode.OK, """{"data":[{"id":9,"recipientId":41,"type":"SIGNATURE","page":1,"envelopeItemId":"item_1"}]}""");
        await client.CreateFieldsAsync("envelope_new", new[]
        {
            DocumensoFieldDraft.Placed(41, DocumensoFieldType.Signature, 1, 10, 80, 30, 100.0004),
            DocumensoFieldDraft.AtPlaceholder(41, DocumensoFieldType.Date, "{{date, r1}}"),
        }, CancellationToken.None);
        var fb = JsonDocument.Parse(h.Last.Body).RootElement.GetProperty("data");
        Check("fields: POST /envelope/field/create-many", h.Last.Url == Base + "/api/v2/envelope/field/create-many", h.Last.Url);
        Check("placed field uses positionX/positionY (not pageX/pageY), page, type SIGNATURE, recipientId",
              fb[0].GetProperty("positionX").GetDouble() == 10 && fb[0].GetProperty("positionY").GetDouble() == 80
              && fb[0].GetProperty("page").GetInt32() == 1 && fb[0].GetProperty("type").GetString() == "SIGNATURE"
              && fb[0].GetProperty("recipientId").GetInt64() == 41 && !fb[0].TryGetProperty("pageX", out _),
              fb[0].ToString());
        Check("percentages are clamped to the API's 0–100", fb[0].GetProperty("height").GetDouble() == 100, fb[0].GetProperty("height").ToString());
        Check("placeholder field sends placeholder and no geometry",
              fb[1].GetProperty("placeholder").GetString() == "{{date, r1}}" && fb[1].GetProperty("type").GetString() == "DATE"
              && !fb[1].TryGetProperty("page", out _) && !fb[1].TryGetProperty("positionX", out _), fb[1].ToString());

        // Distribute.
        h.Respond(HttpStatusCode.OK,
            """{"success":true,"id":"envelope_new","recipients":[{"id":41,"name":"A","email":"a@example.com","token":"rtok","role":"SIGNER","signingOrder":1,"signingUrl":"https://sign.tdpdf.com/sign/rtok"}]}""");
        var dist = await client.DistributeAsync("envelope_new", null, CancellationToken.None);
        var db = JsonDocument.Parse(h.Last.Body).RootElement;
        Check("distribute: POST /envelope/distribute with envelopeId, null meta omitted",
              h.Last.Url == Base + "/api/v2/envelope/distribute" && db.GetProperty("envelopeId").GetString() == "envelope_new"
              && !db.TryGetProperty("meta", out _), Encoding.UTF8.GetString(h.Last.Body));
        Check("distribute returns each recipient's signingUrl", dist.Count == 1 && dist[0].SigningUrl.EndsWith("/sign/rtok"), "");

        // Get / list / get-many.
        h.Respond(HttpStatusCode.OK,
            """{"id":"envelope/x y","title":"T","status":"COMPLETED","envelopeItems":[{"id":"item_1","title":"Lease","order":1,"envelopeId":"e","documentDataId":"d"}],"recipients":[],"user":{"id":3,"name":"Derek","email":"d@example.com"},"team":{"id":7,"url":"t"}}""");
        var env = await client.GetEnvelopeAsync("envelope/x y", CancellationToken.None);
        Check("get: the id is escaped into the path", h.Last.Url == Base + "/api/v2/envelope/envelope%2Fx%20y", h.Last.Url);
        Check("get: status, items and owner parsed",
              env.Status == DocumensoEnvelopeStatus.Completed && env.EnvelopeItems.Single().Id == "item_1" && env.User?.Email == "d@example.com", "");

        h.Respond(HttpStatusCode.OK, """{"data":[],"count":0,"currentPage":2,"perPage":100,"totalPages":0}""");
        var page = await client.ListEnvelopesAsync(new DocumensoEnvelopeQuery(Page: 2, PerPage: 500,
            Status: DocumensoEnvelopeStatus.Pending, Search: "a&b"), CancellationToken.None);
        Check("list: documents only, perPage clamped to 100, status and search escaped",
              h.Last.Url == Base + "/api/v2/envelope?page=2&perPage=100&type=DOCUMENT&orderByColumn=createdAt&orderByDirection=desc&status=PENDING&query=a%26b",
              h.Last.Url);
        Check("list: page parsed", page.CurrentPage == 2 && page.Data.Count == 0, "");

        h.Respond(HttpStatusCode.OK, """{"data":[{"id":"e1","title":"x","status":"PENDING"}]}""");
        var many = await client.GetManyAsync(new[] { "e1" }, CancellationToken.None);
        var gb = JsonDocument.Parse(h.Last.Body).RootElement.GetProperty("ids");
        Check("get-many: ids.type envelopeId",
              h.Last.Url == Base + "/api/v2/envelope/get-many" && gb.GetProperty("type").GetString() == "envelopeId"
              && gb.GetProperty("ids")[0].GetString() == "e1" && many.Count == 1, Encoding.UTF8.GetString(h.Last.Body));
        bool threw = false;
        try { await client.GetManyAsync(Enumerable.Range(0, 21).Select(i => "e" + i).ToArray(), CancellationToken.None); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Check("get-many refuses more than 20 ids before sending", threw, "");

        // Cancel / delete.
        h.Respond(HttpStatusCode.OK, """{"success":true}""");
        await client.CancelAsync("e1", "  ", CancellationToken.None);
        var cb = JsonDocument.Parse(h.Last.Body).RootElement;
        Check("cancel: POST /envelope/cancel, blank reason omitted",
              h.Last.Url == Base + "/api/v2/envelope/cancel" && cb.GetProperty("envelopeId").GetString() == "e1"
              && !cb.TryGetProperty("reason", out _), Encoding.UTF8.GetString(h.Last.Body));
        h.Respond(HttpStatusCode.OK, """{"success":true}""");
        await client.DeleteAsync("e1", CancellationToken.None);
        Check("delete: POST /envelope/delete", h.Last.Url == Base + "/api/v2/envelope/delete", h.Last.Url);

        Check("every authenticated call carried the bare token, and no URL ever did",
              h.All.Where(r => !r.Url.EndsWith("/api/health") && !r.Url.EndsWith("/api/certificate-status")).All(r => r.Authorization == Token)
              && h.All.All(r => !r.Url.Contains(Token)), $"{h.All.Count} requests");
    }

    // ── Failures ────────────────────────────────────────────────────────────────────────────

    private static async Task Failures(Action<string, bool, string> Check)
    {
        var h = new Recorder();
        var client = new DocumensoClient(Base, Token, () => h);

        // The body the live instance returned for a bad token on 2026-10-10, stack trace and all.
        h.Respond(HttpStatusCode.Unauthorized,
            """{"message":"Invalid token","code":"INTERNAL_SERVER_ERROR","data":{"code":"UNAUTHORIZED","httpStatus":401,"stack":"AppError: Invalid token\n    at getApiTokenByToken (file:///app/x.js:46:11)","path":"envelope.find"}}""");
        var ex = await Catch(() => client.ProbeAsync(CancellationToken.None));
        Check("401 maps to AuthRejected", ex?.Failure == DocumensoFailure.AuthRejected && ex.StatusCode == 401, ex?.Message ?? "no exception");
        Check("the nested data.code wins over the misleading top-level code", ex?.ErrorCode == "UNAUTHORIZED", ex?.ErrorCode ?? "null");
        Check("the exception names the path TEMPLATE and method", ex?.PathTemplate == "/envelope" && ex.Method == "GET", ex?.PathTemplate ?? "");
        Check("the exception carries no body text (message, stack) and no token",
              ex is not null && !ex.Message.Contains("Invalid token") && !ex.Message.Contains("stack")
              && !ex.Message.Contains("getApiTokenByToken") && !ex.Message.Contains(Token) && ex.InnerException is null, ex?.Message ?? "");

        h.Respond(HttpStatusCode.Forbidden, """{"message":"no","code":"FORBIDDEN"}""");
        ex = await Catch(() => client.GetEnvelopeAsync("abc", CancellationToken.None));
        Check("403 maps to AuthRejected; template has no interpolated id",
              ex?.Failure == DocumensoFailure.AuthRejected && ex.PathTemplate == "/envelope/{envelopeId}" && !ex.Message.Contains("abc"),
              ex?.Message ?? "");

        h.Respond(HttpStatusCode.NotFound, """{"message":"Envelope not found","code":"NOT_FOUND"}""");
        ex = await Catch(() => client.GetEnvelopeAsync("abc", CancellationToken.None));
        Check("404 maps to NotFound", ex?.Failure == DocumensoFailure.NotFound && ex.ErrorCode == "NOT_FOUND", ex?.Message ?? "");

        var tooMany = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{}") };
        tooMany.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        h.Respond(tooMany);
        ex = await Catch(() => client.ListEnvelopesAsync(new DocumensoEnvelopeQuery(), CancellationToken.None));
        Check("429 maps to RateLimited with Retry-After", ex?.Failure == DocumensoFailure.RateLimited && ex.RetryAfter == TimeSpan.FromSeconds(30), ex?.Message ?? "");

        h.Respond(HttpStatusCode.BadRequest, """{"message":"Invalid input","code":"BAD REQUEST <script>"}""");
        ex = await Catch(() => client.DeleteAsync("x", CancellationToken.None));
        Check("400 maps to Rejected; a code that is not an identifier is discarded",
              ex?.Failure == DocumensoFailure.Rejected && ex.ErrorCode is null, ex?.Message ?? "");

        h.Respond(HttpStatusCode.BadGateway, "<html>bad gateway</html>");
        ex = await Catch(() => client.DeleteAsync("x", CancellationToken.None));
        Check("5xx with an HTML body maps to Server, no code", ex?.Failure == DocumensoFailure.Server && ex.ErrorCode is null && !ex.Message.Contains("html"), ex?.Message ?? "");

        h.Respond(HttpStatusCode.OK, "not json");
        ex = await Catch(() => client.ProbeAsync(CancellationToken.None));
        Check("a 200 that is not JSON maps to UnexpectedResponse", ex?.Failure == DocumensoFailure.UnexpectedResponse, ex?.Message ?? "");

        h.Throw(new HttpRequestException("No such host is known. (sign.tdpdf.com:443)"));
        ex = await Catch(() => client.ProbeAsync(CancellationToken.None));
        Check("a transport failure maps to Network, without the host-bearing inner message",
              ex?.Failure == DocumensoFailure.Network && !ex.Message.Contains("sign.tdpdf.com") && ex.InnerException is null, ex?.Message ?? "");

        // The caller's own cancellation is not an error and must not be reported as one.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        h.Respond(HttpStatusCode.OK, "{}");
        bool cancelled = false;
        try { await client.ProbeAsync(cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        catch (DocumensoApiException) { }
        Check("caller cancellation stays OperationCanceledException", cancelled, "");

        bool refused = false;
        try { _ = new DocumensoClient(Base, "api_abc\r\nX-Evil: 1"); }
        catch (ArgumentException ae) { refused = !ae.Message.Contains("api_abc"); }
        Check("a token with CR/LF is refused, and the refusal does not echo it", refused, "");

        bool badUrl = false;
        try { _ = new DocumensoClient("http://sign.tdpdf.com", Token); }
        catch (ArgumentException) { badUrl = true; }
        Check("the client refuses a non-https instance", badUrl, "");
    }

    // ── Downloads ───────────────────────────────────────────────────────────────────────────

    private static async Task Downloads(Action<string, bool, string> Check, string tmp)
    {
        var h = new Recorder();
        var client = new DocumensoClient(Base, Token, () => h);
        string dest = Path.Combine(tmp, "esign-signed.pdf");
        File.WriteAllText(dest, "stale copy");

        h.Respond(Pdf("%PDF-1.7\nsigned\n%%EOF"));
        await client.DownloadItemAsync("item_1", DocumensoDownloadVersion.Signed, dest, CancellationToken.None);
        Check("item download: GET /envelope/item/{id}/download?version=signed",
              h.Last.Url == Base + "/api/v2/envelope/item/item_1/download?version=signed", h.Last.Url);
        Check("the file is replaced with the downloaded PDF and no .part is left",
              File.ReadAllText(dest).StartsWith("%PDF-1.7") && !File.Exists(dest + ".part"), "");

        h.Respond(Pdf("%PDF-1.7\noriginal"));
        await client.DownloadItemAsync("item_1", DocumensoDownloadVersion.Original, dest, CancellationToken.None);
        Check("version=original is sent lower-case", h.Last.Url.EndsWith("?version=original"), h.Last.Url);

        h.Respond(Pdf("%PDF-1.7\ncert"));
        await client.DownloadCertificateAsync("env_1", dest, CancellationToken.None);
        Check("certificate: GET /envelope/{id}/certificate/download", h.Last.Url == Base + "/api/v2/envelope/env_1/certificate/download", h.Last.Url);

        h.Respond(Pdf("%PDF-1.7\naudit"));
        await client.DownloadAuditLogAsync("env_1", dest, CancellationToken.None);
        Check("audit log: GET /envelope/{id}/audit-log/download", h.Last.Url == Base + "/api/v2/envelope/env_1/audit-log/download", h.Last.Url);

        string fresh = Path.Combine(tmp, "esign-never.pdf");
        if (File.Exists(fresh)) File.Delete(fresh);
        h.Respond(HttpStatusCode.OK, """{"error":"this is not a pdf"}""");
        var ex = await Catch(() => client.DownloadItemAsync("item_1", DocumensoDownloadVersion.Signed, fresh, CancellationToken.None));
        Check("a 200 that is not a PDF is refused and nothing is written",
              ex?.Failure == DocumensoFailure.UnexpectedResponse && !File.Exists(fresh) && !File.Exists(fresh + ".part"), ex?.Message ?? "no exception");

        h.Respond(HttpStatusCode.NotFound, """{"code":"NOT_FOUND"}""");
        ex = await Catch(() => client.DownloadCertificateAsync("gone", fresh, CancellationToken.None));
        Check("a failed download leaves no file behind", ex?.Failure == DocumensoFailure.NotFound && !File.Exists(fresh) && !File.Exists(fresh + ".part"), "");

        static HttpResponseMessage Pdf(string text)
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes(text)) };
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            return r;
        }
    }

    // ── Wire details ────────────────────────────────────────────────────────────────────────

    private static void Wire(Action<string, bool, string> Check)
    {
        Check("enum wire names: NotSigned → NOT_SIGNED, Cc → CC, FreeSignature → FREE_SIGNATURE",
              DocumensoEnumConverter<DocumensoSigningStatus>.ToWire(DocumensoSigningStatus.NotSigned) == "NOT_SIGNED"
              && DocumensoEnumConverter<DocumensoRecipientRole>.ToWire(DocumensoRecipientRole.Cc) == "CC"
              && DocumensoEnumConverter<DocumensoFieldType>.ToWire(DocumensoFieldType.FreeSignature) == "FREE_SIGNATURE", "");

        var e = JsonSerializer.Deserialize<DocumensoEnvelope>("""{"id":"x","title":"t","status":"ARCHIVED_IN_FUTURE"}""", DocumensoClient.Json)!;
        Check("a status a newer server invents reads as Unknown instead of failing", e.Status == DocumensoEnvelopeStatus.Unknown, e.Status.ToString());

        Check("error code: top-level used when there is no nested one",
              DocumensoClient.ExtractErrorCode(Encoding.UTF8.GetBytes("""{"code":"NOT_FOUND"}""")) == "NOT_FOUND", "");
        Check("error code: non-JSON gives none", DocumensoClient.ExtractErrorCode(Encoding.UTF8.GetBytes("<html/>")) is null, "");

        Check("upload name: directories stripped", DocumensoClient.SafeFileName(@"C:\Users\x\Lease.pdf") == "Lease.pdf", DocumensoClient.SafeFileName(@"C:\Users\x\Lease.pdf"));
        Check("upload name: quotes neutralised, .pdf ensured", DocumensoClient.SafeFileName("a\"b") == "a_b.pdf", DocumensoClient.SafeFileName("a\"b"));

        string scrubbed = Sanitizer.Scrub($"Authorization: {Token} failed");
        Check("Sanitizer redacts an api_ token", !scrubbed.Contains(Token) && scrubbed.Contains("api_[redacted]"), scrubbed);
        Check("Sanitizer leaves a short api_ word alone", Sanitizer.Scrub("api_short") == "api_short", Sanitizer.Scrub("api_short"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private static async Task<DocumensoApiException?> Catch(Func<Task> f)
    {
        try { await f(); return null; }
        catch (DocumensoApiException ex) { return ex; }
    }

    private static JsonElement? ExtractJsonPart(string multipart)
    {
        int start = multipart.IndexOf("{\"title\"", StringComparison.Ordinal);
        if (start < 0) return null;
        int end = multipart.IndexOf("\r\n--", start, StringComparison.Ordinal);
        return JsonDocument.Parse(multipart[start..end]).RootElement;
    }

    internal sealed record Captured(string Method, string Url, string? Authorization, string UserAgent, string? ContentType, byte[] Body);

    /// <summary>
    /// Records each request as it would leave the machine and answers from a queue. A fresh
    /// HttpClient is built per operation by the client under test, which disposes the handler it
    /// is given, so disposal is a no-op here and one recorder serves the whole suite.
    /// </summary>
    internal sealed class Recorder : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _replies = new();
        public List<Captured> All { get; } = new();
        public Captured Last => All[^1];

        public void Respond(HttpStatusCode code, string json)
            => _replies.Enqueue(() => new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        public void Respond(HttpResponseMessage r) => _replies.Enqueue(() => r);
        public void Throw(Exception ex) => _replies.Enqueue(() => throw ex);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            byte[] body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct);
            All.Add(new Captured(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("Authorization", out var a) ? string.Join(",", a) : null,
                request.Headers.UserAgent.ToString(),
                request.Content?.Headers.ContentType?.ToString(),
                body));
            return _replies.Dequeue()();
        }

        protected override void Dispose(bool disposing) { /* reused across per-operation clients */ }
    }
}

/// <summary>
/// The same client against the real instance. Runs only when TDPDF_DOCUMENSO_TEST_TOKEN holds a
/// token (TDPDF_DOCUMENSO_TEST_BASEURL optionally picks another instance); otherwise it prints a
/// SKIP line, so CI and every machine without a token stay green and offline. Read-only on
/// purpose: it creates no envelope and changes nothing on the account.
/// </summary>
internal static class DocumensoLive
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("Electronic Signature — live instance");
        string? token = Environment.GetEnvironmentVariable("TDPDF_DOCUMENSO_TEST_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("  SKIP  TDPDF_DOCUMENSO_TEST_TOKEN is not set");
            return;
        }
        string baseUrl = Environment.GetEnvironmentVariable("TDPDF_DOCUMENSO_TEST_BASEURL") is { Length: > 0 } b
            ? b : DocumensoConfig.DefaultBaseUrl;
        Console.WriteLine($"  against {baseUrl}");
        RunAsync(Check, baseUrl, token.Trim()).GetAwaiter().GetResult();
    }

    private static async Task RunAsync(Action<string, bool, string> Check, string baseUrl, string token)
    {
        var anonymous = new DocumensoClient(baseUrl, "");
        await Try(Check, "health reports ok", async () =>
        {
            var h = await anonymous.GetHealthAsync(CancellationToken.None);
            return (h.IsOk, h.Status);
        });
        await Try(Check, "certificate-status answers (and says whether it can seal)", async () =>
        {
            var c = await anonymous.GetCertificateStatusAsync(CancellationToken.None);
            return (true, $"isAvailable={c.IsAvailable}");
        });

        var client = new DocumensoClient(baseUrl, token);
        await Try(Check, "the token is accepted by the probe", async () =>
        {
            var a = await client.ProbeAsync(CancellationToken.None);
            return (true, a.TeamUrl is null ? "no envelopes yet, team unknown" : "team learned");
        });
        await Try(Check, "listing envelopes returns a page", async () =>
        {
            var p = await client.ListEnvelopesAsync(new DocumensoEnvelopeQuery(PerPage: 5), CancellationToken.None);
            return (p.PerPage == 5 && p.Data.Count <= 5, $"count={p.Count} onPage={p.Data.Count}");
        });

        // A token that cannot exist must come back AuthRejected, through the real server's error
        // shape — this is what pins the nested data.code mapping against reality.
        var bogus = new DocumensoClient(baseUrl, "api_tdpdfLiveTestNotARealToken000");
        try
        {
            await bogus.ProbeAsync(CancellationToken.None);
            Check("a bogus token is rejected", false, "it was accepted");
        }
        catch (DocumensoApiException ex)
        {
            Check("a bogus token is rejected as AuthRejected", ex.Failure == DocumensoFailure.AuthRejected,
                  $"{ex.StatusCode} {ex.ErrorCode}");
        }
    }

    private static async Task Try(Action<string, bool, string> Check, string label, Func<Task<(bool ok, string detail)>> f)
    {
        try
        {
            var (ok, detail) = await f();
            Check(label, ok, detail);
        }
        catch (DocumensoApiException ex)
        {
            Check(label, false, $"{ex.Failure} {ex.StatusCode} {ex.ErrorCode}");
        }
    }
}
