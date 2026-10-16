using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TDPdf.Services;

/// <summary>
/// Pins Phase 6's browser sign-in (<see cref="DocumensoBrowserSignIn"/>): PKCE, the authorize URL,
/// how a callback is judged, and the whole loopback flow end to end — a real HttpListener on
/// 127.0.0.1, a scripted "browser" that follows the redirect, and a stub of the server contract —
/// including that a wrong state or a wrong verifier gets nothing, that the listener is closed on
/// every way out, and that the token never reaches a progress line, a result's text or the page
/// served to the browser.
/// </summary>
internal static class DocumensoConnect
{
    private const string Base = "https://sign.tdpdf.com";
    private const string Token = "api_CONNECTtoken0123456789abcdef";

    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("Electronic Signature — browser sign-in (PKCE + loopback)");
        Pkce(Check);
        Contract(Check);
        Callbacks(Check);
        Flow(Check).GetAwaiter().GetResult();
        Exchange(Check).GetAwaiter().GetResult();
    }

    // ── PKCE ────────────────────────────────────────────────────────────────────────────────

    private static void Pkce(Action<string, bool, string> Check)
    {
        // RFC 7636 Appendix B.
        const string v = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        string c = DocumensoPkce.Challenge(v);
        Check("S256 challenge matches the RFC 7636 Appendix B vector", c == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", c);
        Check("the matching verifier verifies", DocumensoPkce.Verify(v, c), "");

        string other = DocumensoPkce.NewVerifier();
        Check("a different verifier is rejected", !DocumensoPkce.Verify(other, c), "");
        Check("a verifier hashed with one character changed is rejected", !DocumensoPkce.Verify(v[..^1] + "Y", c), "");
        Check("the challenge itself is not accepted as a verifier", !DocumensoPkce.Verify(c, c), "");
        Check("a too-short verifier is rejected", !DocumensoPkce.Verify("short", DocumensoPkce.Challenge("short")), "");
        Check("a verifier with a forbidden character is rejected",
              !DocumensoPkce.Verify(new string('a', 42) + "/", DocumensoPkce.Challenge(new string('a', 42) + "/")), "");
        Check("null and empty are rejected, not thrown", !DocumensoPkce.Verify(null, c) && !DocumensoPkce.Verify(v, null) && !DocumensoPkce.Verify(v, ""), "");

        var verifiers = Enumerable.Range(0, 50).Select(_ => DocumensoPkce.NewVerifier()).ToList();
        Check("new verifiers are well formed (43 chars of the RFC alphabet)",
              verifiers.All(x => x.Length == 43 && DocumensoPkce.IsValidVerifier(x)), verifiers[0].Length.ToString());
        Check("new verifiers are distinct", verifiers.Distinct().Count() == verifiers.Count, "");
        var states = Enumerable.Range(0, 50).Select(_ => DocumensoPkce.NewState()).ToList();
        Check("new states are distinct and URL-safe", states.Distinct().Count() == 50 && states.All(s => s.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')), "");
    }

    // ── Contract ────────────────────────────────────────────────────────────────────────────

    private static void Contract(Action<string, bool, string> Check)
    {
        string redirect = DocumensoConnectContract.RedirectUri("127.0.0.1", 50123);
        Check("redirect URI is the loopback callback", redirect == "http://127.0.0.1:50123/cb", redirect);

        string url = DocumensoConnectContract.AuthorizeUrl(Base + "/", "CHAL-_lenge", "st+te/=", redirect);
        var u = new Uri(url);
        var q = Query(u);
        Check("authorize URL is {BaseUrl}/connect/tdpdf", u.GetLeftPart(UriPartial.Path) == Base + "/connect/tdpdf", u.GetLeftPart(UriPartial.Path));
        Check("authorize URL carries the challenge, S256, the state and the redirect, escaped",
              q["code_challenge"] == "CHAL-_lenge" && q["code_challenge_method"] == "S256"
              && q["state"] == "st+te/=" && q["redirect_uri"] == redirect, url);
        Check("authorize URL carries no verifier and no token", !url.Contains("verifier", StringComparison.OrdinalIgnoreCase) && !url.Contains("token", StringComparison.OrdinalIgnoreCase), url);
        Check("token path is /api/connect/token", DocumensoConnectContract.TokenPath == "/api/connect/token", "");
    }

    private static void Callbacks(Action<string, bool, string> Check)
    {
        const string st = "expected_state_value";
        DocumensoCallback P(string pathAndQuery) => DocumensoConnectContract.ParseCallback(new Uri("http://127.0.0.1:5000" + pathAndQuery), st);

        var ok = P($"/cb?code=abc123&state={st}");
        Check("right state + code → Code", ok.Kind == DocumensoCallbackKind.Code && ok.Code == "abc123", ok.ToString());
        Check("a callback's text redacts the code", !ok.ToString().Contains("abc123"), ok.ToString());

        var wrong = P("/cb?code=abc123&state=someone_elses");
        Check("STATE MISMATCH: a wrong state is rejected even with a code", wrong.Kind == DocumensoCallbackKind.StateMismatch && wrong.Code is null, wrong.ToString());
        var missing = P("/cb?code=abc123");
        Check("STATE MISMATCH: a missing state is rejected", missing.Kind == DocumensoCallbackKind.StateMismatch, missing.ToString());
        var prefix = P($"/cb?code=abc123&state={st[..^1]}");
        Check("STATE MISMATCH: a prefix of the state is rejected", prefix.Kind == DocumensoCallbackKind.StateMismatch, prefix.ToString());
        var dup = P($"/cb?code=abc123&state=attacker&state={st}");
        Check("STATE MISMATCH: a repeated state cannot override the first", dup.Kind == DocumensoCallbackKind.StateMismatch, dup.ToString());

        var denied = P($"/cb?error=access_denied&state={st}");
        Check("error=access_denied with our state → Denied", denied.Kind == DocumensoCallbackKind.Denied && denied.Error == "access_denied", denied.ToString());
        var junk = P($"/cb?error=%3Cscript%3E&state={st}");
        Check("an error value that is not an identifier is reduced to 'error'", junk.Error == "error", junk.ToString());
        var empty = P($"/cb?state={st}");
        Check("our state with neither code nor error → Malformed", empty.Kind == DocumensoCallbackKind.Malformed, empty.ToString());
        var favicon = P("/favicon.ico");
        Check("any other path is not a callback", favicon.Kind == DocumensoCallbackKind.NotCallback, favicon.ToString());
    }

    // ── The flow, over a real loopback listener ─────────────────────────────────────────────

    /// <summary>Plays the server's half of the contract: remembers the challenge, checks the verifier.</summary>
    private sealed class StubServer : IDocumensoTokenExchange
    {
        public string? Challenge;
        public string? RedirectUri;
        public int Calls;
        public string Code = "code_" + Guid.NewGuid().ToString("N");

        public void Authorized(string authorizeUrl)
        {
            var q = Query(new Uri(authorizeUrl));
            Challenge = q["code_challenge"];
            RedirectUri = q["redirect_uri"];
        }

        public Task<DocumensoConnectGrant> ExchangeAsync(string code, string codeVerifier, string redirectUri, CancellationToken ct)
        {
            Calls++;
            if (code != Code || redirectUri != RedirectUri || !DocumensoPkce.Verify(codeVerifier, Challenge))
                throw new DocumensoApiException(DocumensoFailure.Rejected, 400, "INVALID_GRANT", "POST", DocumensoConnectContract.TokenPath);
            return Task.FromResult(new DocumensoConnectGrant(Token, "me@example.com", "Me", "personal_abc", null));
        }
    }

    private sealed class Lines : IProgress<string>
    {
        public readonly List<string> All = new();
        public void Report(string value) { lock (All) All.Add(value); }
    }

    /// <summary>A browser: follows the authorize URL's redirect to the loopback listener with the given query.</summary>
    private static Func<string, bool> Browser(Func<string, string> queryFor, List<string> pages, StubServer? server = null, string path = "/cb")
        => authorizeUrl =>
        {
            server?.Authorized(authorizeUrl);
            string redirect = Query(new Uri(authorizeUrl))["redirect_uri"];
            var cb = new Uri(redirect);
            string target = $"{cb.Scheme}://{cb.Authority}{path}?{queryFor(authorizeUrl)}";
            _ = Task.Run(async () =>
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    string body = await http.GetStringAsync(target).ConfigureAwait(false);
                    lock (pages) pages.Add(body);
                }
                catch (HttpRequestException ex) { lock (pages) pages.Add("HTTP-ERROR " + ex.StatusCode); }
                catch (Exception ex) { lock (pages) pages.Add("ERROR " + ex.GetType().Name); }
            });
            return true;
        };

    private static async Task Flow(Action<string, bool, string> Check)
    {
        // Happy path.
        {
            var server = new StubServer();
            var pages = new List<string>();
            var lines = new Lines();
            var flow = new DocumensoBrowserSignIn(Base, server,
                Browser(a => $"code={server.Code}&state={Uri.EscapeDataString(Query(new Uri(a))["state"])}", pages, server),
                TimeSpan.FromSeconds(20));
            var r = await flow.RunAsync(lines, CancellationToken.None);
            await Settle(pages, 1);
            Check("happy path: state checked, code + verifier exchanged → Connected",
                  r.Outcome == DocumensoSignInOutcome.Connected && r.Grant?.Token == Token && server.Calls == 1, r.ToString());
            Check("happy path: the listener bound loopback only", flow.AuthorizeUrl!.Contains(Uri.EscapeDataString("http://127.0.0.1:")) || flow.AuthorizeUrl.Contains(Uri.EscapeDataString("http://localhost:")), flow.AuthorizeUrl);
            Check("happy path: the listener is closed afterwards", !flow.IsListening && !PortAccepts(flow.Port!.Value), $"port {flow.Port}");
            Check("happy path: the browser was told it worked, and not shown the code",
                  pages.Count == 1 && pages[0].Contains("signed in", StringComparison.OrdinalIgnoreCase) && !pages[0].Contains(server.Code), Trim(pages));
            NoSecrets(Check, "happy path", Token, server.Code, lines, r, pages);
        }

        // A favicon request first must not end the wait.
        {
            var server = new StubServer();
            var pages = new List<string>();
            var flow = new DocumensoBrowserSignIn(Base, server, a =>
            {
                Browser(_ => "", pages, server, "/favicon.ico")(a);
                Thread.Sleep(150);
                return Browser(x => $"code={server.Code}&state={Uri.EscapeDataString(Query(new Uri(x))["state"])}", pages)(a);
            }, TimeSpan.FromSeconds(20));
            var r = await flow.RunAsync(null, CancellationToken.None);
            Check("a stray request to another path is answered 404 and ignored", r.Outcome == DocumensoSignInOutcome.Connected, r.ToString());
        }

        // State mismatch.
        {
            var server = new StubServer();
            var pages = new List<string>();
            var lines = new Lines();
            var flow = new DocumensoBrowserSignIn(Base, server,
                Browser(_ => $"code={server.Code}&state=forged_state_from_elsewhere", pages, server), TimeSpan.FromSeconds(20));
            var r = await flow.RunAsync(lines, CancellationToken.None);
            await Settle(pages, 1);
            Check("STATE MISMATCH: the flow ends rejected", r.Outcome == DocumensoSignInOutcome.StateMismatch && r.Grant is null, r.ToString());
            Check("STATE MISMATCH: the code is never exchanged", server.Calls == 0, $"{server.Calls} exchange call(s)");
            Check("STATE MISMATCH: the browser gets a 400, not a success page", pages.Count == 1 && pages[0].StartsWith("HTTP-ERROR BadRequest"), Trim(pages));
            Check("STATE MISMATCH: the listener is closed", !flow.IsListening && !PortAccepts(flow.Port!.Value), "");
        }

        // Denied in the browser.
        {
            var server = new StubServer();
            var pages = new List<string>();
            var flow = new DocumensoBrowserSignIn(Base, server,
                Browser(a => $"error=access_denied&state={Uri.EscapeDataString(Query(new Uri(a))["state"])}", pages, server), TimeSpan.FromSeconds(20));
            var r = await flow.RunAsync(null, CancellationToken.None);
            Check("declined in the browser → Denied, nothing exchanged", r.Outcome == DocumensoSignInOutcome.Denied && r.DeniedReason == "access_denied" && server.Calls == 0, r.ToString());
        }

        // Verifier mismatch: the service holds a challenge from a different verifier — as when a
        // code is intercepted and replayed by someone who started their own attempt.
        {
            var server = new StubServer();
            var pages = new List<string>();
            var flow = new DocumensoBrowserSignIn(Base, server, a =>
            {
                bool opened = Browser(x => $"code={server.Code}&state={Uri.EscapeDataString(Query(new Uri(x))["state"])}", pages, server)(a);
                server.Challenge = DocumensoPkce.Challenge(DocumensoPkce.NewVerifier());   // not ours
                return opened;
            }, TimeSpan.FromSeconds(20));
            var r = await flow.RunAsync(null, CancellationToken.None);
            Check("VERIFIER MISMATCH: the exchange is refused → ExchangeRejected, no grant",
                  r.Outcome == DocumensoSignInOutcome.ExchangeRejected && r.Grant is null && r.Failure?.ErrorCode == "INVALID_GRANT", r.ToString());
            Check("VERIFIER MISMATCH: the listener is closed", !flow.IsListening && !PortAccepts(flow.Port!.Value), "");
        }

        // Cancel.
        {
            var server = new StubServer();
            using var cts = new CancellationTokenSource();
            var flow = new DocumensoBrowserSignIn(Base, server, _ => true, TimeSpan.FromSeconds(30));
            var run = flow.RunAsync(null, cts.Token);
            await Task.Delay(300);
            bool listeningBefore = flow.IsListening && PortAccepts(flow.Port!.Value);
            cts.Cancel();
            var r = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Check("CANCEL: the listener was up while waiting", listeningBefore, $"port {flow.Port}");
            Check("CANCEL: the flow ends Cancelled", r.Outcome == DocumensoSignInOutcome.Cancelled && server.Calls == 0, r.ToString());
            Check("CANCEL: the listener is closed and the port refuses connections", !flow.IsListening && !PortAccepts(flow.Port!.Value), $"port {flow.Port}");
        }

        // Timeout.
        {
            var server = new StubServer();
            var flow = new DocumensoBrowserSignIn(Base, server, _ => true, TimeSpan.FromMilliseconds(400));
            var r = await flow.RunAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Check("TIMEOUT: the flow ends TimedOut", r.Outcome == DocumensoSignInOutcome.TimedOut && server.Calls == 0, r.ToString());
            Check("TIMEOUT: the listener is closed and the port refuses connections", !flow.IsListening && !PortAccepts(flow.Port!.Value), $"port {flow.Port}");
        }

        // No browser.
        {
            var flow = new DocumensoBrowserSignIn(Base, new StubServer(), _ => false, TimeSpan.FromSeconds(30));
            var r = await flow.RunAsync(null, CancellationToken.None);
            Check("no browser → BrowserFailed, listener closed", r.Outcome == DocumensoSignInOutcome.BrowserFailed && !flow.IsListening && !PortAccepts(flow.Port!.Value), r.ToString());
            bool threw = false;
            try { await flow.RunAsync(null, CancellationToken.None); } catch (InvalidOperationException) { threw = true; }
            Check("an attempt runs once (state and verifier are never reused)", threw, "");
        }
    }

    // ── The real exchange over HTTP, against a stub of the server contract ───────────────────

    private sealed class ContractHandler : HttpMessageHandler
    {
        public string? Challenge;
        public string? ExpectedRedirect;
        public string Code = "one_time_code";
        public readonly List<(string Method, string Url, string? Auth, string Body)> Seen = new();
        public Func<HttpRequestMessage, HttpResponseMessage>? Override;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request.Method.Method, request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("Authorization", out var a) ? string.Join(",", a) : null, body));
            if (Override is { } o) return o(request);
            if (request.RequestUri.AbsolutePath != "/api/connect/token") return Json(HttpStatusCode.NotFound, "{}");
            if (request.Method == HttpMethod.Get) return Json(HttpStatusCode.MethodNotAllowed, "{}");
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? Str(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool ok = Str("grantType") == "authorization_code" && Str("code") == Code && Str("redirectUri") == ExpectedRedirect
                      && DocumensoPkce.Verify(Str("codeVerifier"), Challenge);
            return ok
                ? Json(HttpStatusCode.OK, $"{{\"token\":\"{Token}\",\"expiresAt\":\"2027-01-01T00:00:00.000Z\",\"email\":\"me@example.com\",\"name\":\"Me\",\"teamUrl\":\"personal_abc\"}}")
                : Json(HttpStatusCode.BadRequest, "{\"code\":\"INVALID_GRANT\",\"message\":\"The sign-in code is invalid or expired.\"}");
        }

        public static HttpResponseMessage Json(HttpStatusCode code, string json)
            => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        protected override void Dispose(bool disposing) { }
    }

    private static async Task Exchange(Action<string, bool, string> Check)
    {
        var h = new ContractHandler();
        var client = new DocumensoClient(Base, "", () => h);

        Check("browser sign-in is detected when the token route answers GET with 405", await client.IsBrowserSignInSupportedAsync(CancellationToken.None), "");
        h.Override = _ => ContractHandler.Json(HttpStatusCode.NotFound, "{}");
        Check("…and reported absent on a 404 (an instance without the fork's endpoint)", !await client.IsBrowserSignInSupportedAsync(CancellationToken.None), "");
        h.Override = null;

        string verifier = DocumensoPkce.NewVerifier();
        h.Challenge = DocumensoPkce.Challenge(verifier);
        h.ExpectedRedirect = "http://127.0.0.1:50000/cb";
        h.Seen.Clear();
        var grant = await new DocumensoHttpTokenExchange(Base, () => h).ExchangeAsync(h.Code, verifier, h.ExpectedRedirect, CancellationToken.None);
        var sent = h.Seen.Single();
        Check("exchange: POST {BaseUrl}/api/connect/token", sent.Method == "POST" && sent.Url == Base + "/api/connect/token", sent.Url);
        Check("exchange: unauthenticated — no Authorization header", sent.Auth is null, sent.Auth ?? "");
        Check("exchange: body is {grantType, code, codeVerifier, redirectUri}",
              sent.Body.Contains("\"grantType\":\"authorization_code\"") && sent.Body.Contains("\"codeVerifier\":\"" + verifier + "\"")
              && sent.Body.Contains("\"redirectUri\":\"http://127.0.0.1:50000/cb\""), sent.Body);
        Check("exchange: the grant carries the token, account and expiry",
              grant.Token == Token && grant.Email == "me@example.com" && grant.TeamUrl == "personal_abc" && grant.ExpiresUtc?.Year == 2027, grant.ToString());
        Check("exchange: the grant's text never shows the token", !grant.ToString().Contains(Token), grant.ToString());

        // Verifier mismatch, over HTTP.
        try
        {
            await new DocumensoHttpTokenExchange(Base, () => h).ExchangeAsync(h.Code, DocumensoPkce.NewVerifier(), h.ExpectedRedirect, CancellationToken.None);
            Check("VERIFIER MISMATCH over HTTP: refused", false, "accepted");
        }
        catch (DocumensoApiException ex)
        {
            Check("VERIFIER MISMATCH over HTTP: 400 INVALID_GRANT → Rejected", ex.Failure == DocumensoFailure.Rejected && ex.StatusCode == 400 && ex.ErrorCode == "INVALID_GRANT", ex.Message);
            Check("…and the exception names no code, verifier or body text",
                  !ex.Message.Contains(h.Code) && !ex.Message.Contains("invalid or expired"), ex.Message);
        }

        // A 200 whose token is unusable is not a sign-in.
        foreach (var (label, json) in new[] { ("empty token", "{\"token\":\"\"}"), ("token with a space", "{\"token\":\"api_has space\"}"), ("no token", "{}") })
        {
            h.Override = _ => ContractHandler.Json(HttpStatusCode.OK, json);
            try
            {
                await client.ExchangeConnectCodeAsync("c", verifier, "http://127.0.0.1:1/cb", CancellationToken.None);
                Check($"a 200 with {label} is refused", false, "accepted");
            }
            catch (DocumensoApiException ex)
            {
                Check($"a 200 with {label} is refused as UnexpectedResponse", ex.Failure == DocumensoFailure.UnexpectedResponse, ex.Message);
            }
        }
        h.Override = null;

        // The full flow through the real HTTP exchange.
        {
            var pages = new List<string>();
            var lines = new Lines();
            var flow = new DocumensoBrowserSignIn(Base, new DocumensoHttpTokenExchange(Base, () => h), a =>
            {
                var q = Query(new Uri(a));
                h.Challenge = q["code_challenge"];
                h.ExpectedRedirect = q["redirect_uri"];
                return Browser(_ => $"code={h.Code}&state={Uri.EscapeDataString(q["state"])}", pages)(a);
            }, TimeSpan.FromSeconds(20));
            var r = await flow.RunAsync(lines, CancellationToken.None);
            await Settle(pages, 1);
            Check("end to end over HTTP: Connected with the service's token", r.Outcome == DocumensoSignInOutcome.Connected && r.Grant?.Token == Token, r.ToString());
            NoSecrets(Check, "end to end", Token, h.Code, lines, r, pages);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private static void NoSecrets(Action<string, bool, string> Check, string label, string token, string code,
        Lines lines, DocumensoSignInResult r, List<string> pages)
    {
        var said = new List<string>(lines.All) { r.ToString() };
        if (r.Grant is { } g) said.Add(g.ToString());
        if (r.Failure is { } f) said.Add(f.Message);
        said.AddRange(pages);
        Check($"{label}: the token appears in no progress line, result text or browser page",
              said.All(s => !s.Contains(token)), string.Join(" | ", lines.All));
        Check($"{label}: nor does the one-time code", said.All(s => !s.Contains(code)), "");
        Check($"{label}: progress was reported in plain sentences", lines.All.Count > 0, string.Join(" | ", lines.All));
    }

    private static Dictionary<string, string> Query(Uri u)
    {
        var d = new Dictionary<string, string>();
        foreach (var p in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = p.IndexOf('=');
            d[Uri.UnescapeDataString(p[..i])] = Uri.UnescapeDataString(p[(i + 1)..]);
        }
        return d;
    }

    private static bool PortAccepts(int port)
    {
        try
        {
            using var c = new TcpClient();
            var t = c.ConnectAsync(IPAddress.Loopback, port);
            return t.Wait(TimeSpan.FromSeconds(2)) && c.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static async Task Settle(List<string> pages, int want)
    {
        for (int i = 0; i < 100; i++)
        {
            lock (pages) if (pages.Count >= want) return;
            await Task.Delay(50);
        }
    }

    private static string Trim(List<string> pages)
        => string.Join(" | ", pages.Select(p => p.Length > 80 ? p[..80] + "…" : p));
}
