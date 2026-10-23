using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TDPdf.Services
{
    // Electronic Signature, Phase 6: "Sign in with your browser" instead of pasting an API token.
    //
    // The shape is the native-app OAuth pattern (RFC 8252 loopback redirect + RFC 7636 PKCE),
    // reduced to the one thing TDPdf needs back — a Documenso API token for the signed-in user:
    //
    //   1. TDPdf listens on http://127.0.0.1:{random port}/cb and makes a random state and a PKCE
    //      verifier.
    //   2. It opens {BaseUrl}/connect/tdpdf?code_challenge=…&code_challenge_method=S256&state=…
    //      &redirect_uri=http://127.0.0.1:{port}/cb in the default browser. The user signs in (or
    //      signs up — sign.tdpdf.com is open to anyone) and approves TDPdf.
    //   3. The service redirects to the loopback address with ?code=…&state=….
    //   4. TDPdf checks the state, then POSTs {"code","code_verifier"} to
    //      {BaseUrl}/api/connect/token and gets {"token"} back — an ordinary API token for the team
    //      the user picked, named "TDPdf (date)" and valid for a year.
    //
    // The server half is our Documenso fork's patch 0004 (fork/patches/0004-tdpdf-browser-sign-in.patch
    // in the documenso repo: /connect/tdpdf and /api/connect/token); this client is built against
    // that, and docs/design/esignature-documenso.md ("Phase 6 as built") records the contract. Everything in
    // this file is WPF-free so tests/PdfCore can drive the whole flow, including a real loopback
    // listener, against a stub of that contract.
    //
    // THE TOKEN: it exists only inside DocumensoConnectGrant, whose ToString() is redacted, and is
    // never put into a progress message, an exception, a log line or the page served to the
    // browser. The code is treated the same way: it never goes back out on the loopback page.

    /// <summary>What the token exchange returns. <see cref="Token"/> is the user's whole account.</summary>
    internal sealed record DocumensoConnectGrant(string Token, string? Email, string? Name, string? TeamUrl, DateTime? ExpiresUtc)
    {
        /// <summary>Redacted: a grant that reaches a log or an exception message must not carry the token.</summary>
        public override string ToString()
            => $"DocumensoConnectGrant {{ Token = (redacted), Email = {Email ?? "-"}, TeamUrl = {TeamUrl ?? "-"} }}";
    }

    /// <summary>
    /// RFC 7636 PKCE, S256 only. <see cref="Verify"/> is the reference for the server side: the
    /// fork must accept exactly the pairs it accepts.
    /// </summary>
    internal static class DocumensoPkce
    {
        /// <summary>A new code verifier: 32 random bytes, base64url — 43 characters, the RFC's minimum length and its recommended construction.</summary>
        internal static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

        /// <summary>A new <c>state</c>: 32 random bytes, base64url. Unguessable, single use.</summary>
        internal static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(32));

        /// <summary><c>BASE64URL(SHA256(ASCII(verifier)))</c>, no padding.</summary>
        internal static string Challenge(string verifier)
            => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        /// <summary>RFC 7636 §4.1: 43–128 characters of <c>[A-Z a-z 0-9 - . _ ~]</c>.</summary>
        internal static bool IsValidVerifier(string? v)
        {
            if (v is null || v.Length < 43 || v.Length > 128) return false;
            foreach (char c in v)
                if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' or '~')) return false;
            return true;
        }

        /// <summary>
        /// The server's check, in fixed time: the verifier is well formed and hashes to the challenge
        /// the authorize request carried. False for any malformed input — never an exception.
        /// </summary>
        internal static bool Verify(string? verifier, string? challenge)
        {
            if (!IsValidVerifier(verifier) || string.IsNullOrEmpty(challenge)) return false;
            byte[] want = Encoding.ASCII.GetBytes(Challenge(verifier!));
            byte[] got = Encoding.ASCII.GetBytes(challenge);
            return CryptographicOperations.FixedTimeEquals(want, got);
        }

        internal static string Base64Url(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>What a request to the loopback listener turned out to be.</summary>
    internal enum DocumensoCallbackKind
    {
        /// <summary>The redirect, with a code and the right state.</summary>
        Code,
        /// <summary>The redirect, reporting that the user declined (or the service refused): <c>?error=…</c> with the right state.</summary>
        Denied,
        /// <summary>The redirect path, but the state is missing or not ours. Never exchanged.</summary>
        StateMismatch,
        /// <summary>The redirect path with the right state but neither a code nor an error.</summary>
        Malformed,
        /// <summary>Any other path (a favicon request, a stray probe). Answered 404 and ignored.</summary>
        NotCallback,
    }

    internal sealed record DocumensoCallback(DocumensoCallbackKind Kind, string? Code = null, string? Error = null)
    {
        public override string ToString() => $"DocumensoCallback {{ Kind = {Kind}, Code = {(Code is null ? "-" : "(redacted)")}, Error = {Error ?? "-"} }}";
    }

    /// <summary>
    /// The client side of the TDPdf Signing connect contract: paths, the authorize URL, the
    /// redirect URI and how a callback is judged. Pure; the design doc carries the server side.
    /// </summary>
    internal static class DocumensoConnectContract
    {
        /// <summary>
        /// The page the browser is sent to. A web page of the service, not an API route. Also the
        /// support probe: without parameters it answers non-404 exactly when the fork's patch is in.
        /// </summary>
        internal const string AuthorizePath = "/connect/tdpdf";

        /// <summary>The code-for-token exchange: POST <c>{"code","code_verifier"}</c> → <c>{"token"}</c>.</summary>
        internal const string TokenPath = "/api/connect/token";

        /// <summary>The loopback path the service redirects to.</summary>
        internal const string CallbackPath = "/cb";

        /// <summary>How long TDPdf waits for the browser before giving up and closing the listener.</summary>
        internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

        /// <summary><c>http://{host}:{port}/cb</c> — host is <c>127.0.0.1</c>, or <c>localhost</c> when the loopback IP cannot be bound.</summary>
        internal static string RedirectUri(string host, int port) => $"http://{host}:{port}{CallbackPath}";

        /// <summary>The page the default browser opens. Every value is escaped; nothing secret is in it (the challenge is a hash).</summary>
        internal static string AuthorizeUrl(string baseUrl, string codeChallenge, string state, string redirectUri)
        {
            string b = DocumensoConfig.Normalise(baseUrl) ?? throw new ArgumentException("Not an acceptable address.", nameof(baseUrl));
            return b + AuthorizePath
                + "?code_challenge=" + Uri.EscapeDataString(codeChallenge)
                + "&code_challenge_method=S256"
                + "&state=" + Uri.EscapeDataString(state)
                + "&redirect_uri=" + Uri.EscapeDataString(redirectUri);
        }

        /// <summary>
        /// Judges one request that reached the listener. The state is compared in fixed time and
        /// <b>before</b> anything else is believed: a request without our state is never a code.
        /// </summary>
        internal static DocumensoCallback ParseCallback(Uri requestUrl, string expectedState)
        {
            if (!string.Equals(requestUrl.AbsolutePath, CallbackPath, StringComparison.Ordinal))
                return new(DocumensoCallbackKind.NotCallback);

            var q = ParseQuery(requestUrl.Query);
            q.TryGetValue("state", out var state);
            if (string.IsNullOrEmpty(state) || !FixedTimeEquals(state, expectedState))
                return new(DocumensoCallbackKind.StateMismatch);

            if (q.TryGetValue("error", out var error) && !string.IsNullOrEmpty(error))
                return new(DocumensoCallbackKind.Denied, Error: SafeErrorCode(error));
            if (q.TryGetValue("code", out var code) && !string.IsNullOrWhiteSpace(code))
                return new(DocumensoCallbackKind.Code, Code: code.Trim());
            return new(DocumensoCallbackKind.Malformed);
        }

        /// <summary>
        /// An <c>error</c> value as something safe to show and log: a short <c>[a-z0-9_]</c>
        /// identifier (<c>access_denied</c>), or <c>"error"</c> when it is anything else.
        /// </summary>
        internal static string SafeErrorCode(string raw)
        {
            string s = raw.Trim().ToLowerInvariant();
            if (s.Length is 0 or > 64) return "error";
            foreach (char c in s)
                if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) return "error";
            return s;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                string k = Unescape(eq < 0 ? part : part[..eq]);
                string v = eq < 0 ? "" : Unescape(part[(eq + 1)..]);
                d.TryAdd(k, v);   // first wins: a repeated parameter cannot override the first
            }
            return d;
        }

        private static string Unescape(string s)
        {
            try { return Uri.UnescapeDataString(s.Replace('+', ' ')); }
            catch { return ""; }
        }

        private static bool FixedTimeEquals(string a, string b)
            => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }

    /// <summary>Trades a sign-in code for a token. The seam the flow is tested through.</summary>
    internal interface IDocumensoTokenExchange
    {
        /// <summary>May throw <see cref="DocumensoApiException"/>; the caller's cancellation stays an <see cref="OperationCanceledException"/>.</summary>
        Task<DocumensoConnectGrant> ExchangeAsync(string code, string codeVerifier, string redirectUri, CancellationToken ct);
    }

    /// <summary>The real exchange: <see cref="DocumensoClient.ExchangeConnectCodeAsync"/>.</summary>
    internal sealed class DocumensoHttpTokenExchange : IDocumensoTokenExchange
    {
        private readonly DocumensoClient _anonymous;

        internal DocumensoHttpTokenExchange(string baseUrl, Func<System.Net.Http.HttpMessageHandler>? handlerFactory = null)
            => _anonymous = new DocumensoClient(baseUrl, "", handlerFactory);

        public Task<DocumensoConnectGrant> ExchangeAsync(string code, string codeVerifier, string redirectUri, CancellationToken ct)
            => _anonymous.ExchangeConnectCodeAsync(code, codeVerifier, redirectUri, ct);
    }

    internal enum DocumensoSignInOutcome
    {
        /// <summary>A token came back. Not yet validated or stored — the caller does both.</summary>
        Connected,
        Cancelled,
        TimedOut,
        /// <summary>The user declined on the service's page (or it refused): <c>?error=</c>.</summary>
        Denied,
        /// <summary>The callback did not carry our state. Nothing was exchanged.</summary>
        StateMismatch,
        /// <summary>The callback had our state but no code.</summary>
        Malformed,
        /// <summary>The service refused the code/verifier (400 INVALID_GRANT, or any other HTTP refusal).</summary>
        ExchangeRejected,
        /// <summary>The service could not be reached for the exchange.</summary>
        Unreachable,
        /// <summary>No browser could be opened. The authorize URL is in the result so the UI can offer it.</summary>
        BrowserFailed,
        /// <summary>No loopback port could be listened on.</summary>
        ListenerFailed,
    }

    /// <summary>How a sign-in ended. <see cref="ToString"/> never shows the token.</summary>
    internal sealed record DocumensoSignInResult(DocumensoSignInOutcome Outcome, DocumensoConnectGrant? Grant = null,
        DocumensoApiException? Failure = null, string? DeniedReason = null)
    {
        public override string ToString()
            => $"DocumensoSignInResult {{ Outcome = {Outcome}, Grant = {(Grant is null ? "-" : "(redacted)")}, Failure = {Failure?.Failure.ToString() ?? "-"}, Denied = {DeniedReason ?? "-"} }}";
    }

    /// <summary>
    /// One browser sign-in: listen on loopback, open the browser, wait for the one redirect, check
    /// it, exchange the code. Single use — make a new instance per attempt (the state and verifier
    /// are per attempt by construction).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The listener is closed on every path out</b> — success, refusal, Cancel, timeout, an
    /// exception — in one <c>finally</c>. <see cref="IsListening"/> lets tests prove it.
    /// </para>
    /// <para>
    /// <b>Loopback only.</b> The prefix is <c>http://127.0.0.1:{port}/</c>, never <c>+</c> or
    /// <c>*</c>, so nothing off this computer can reach it. Some Windows configurations refuse to
    /// register the loopback IP with http.sys for a non-administrator; then
    /// <c>http://localhost:{port}/</c> is used instead (the redirect URI says which, and the
    /// contract accepts both).
    /// </para>
    /// <para>
    /// <b>Requests to other paths</b> (a browser's favicon fetch, a port scanner) get a 404 and are
    /// otherwise ignored; only the callback path ends the wait. A callback without our state ends it
    /// as <see cref="DocumensoSignInOutcome.StateMismatch"/> and is never exchanged.
    /// </para>
    /// </remarks>
    internal sealed class DocumensoBrowserSignIn
    {
        private readonly string _baseUrl;
        private readonly IDocumensoTokenExchange _exchange;
        private readonly Func<string, bool> _openBrowser;
        private readonly TimeSpan _timeout;
        private HttpListener? _listener;
        private int _used;

        /// <param name="openBrowser">Opens the authorize URL; false when no browser could be started.</param>
        /// <param name="timeout">How long to wait for the redirect; <see cref="DocumensoConnectContract.DefaultTimeout"/> by default.</param>
        internal DocumensoBrowserSignIn(string baseUrl, IDocumensoTokenExchange exchange, Func<string, bool> openBrowser, TimeSpan? timeout = null)
        {
            _baseUrl = DocumensoConfig.Normalise(baseUrl) ?? throw new ArgumentException("Not an acceptable address.", nameof(baseUrl));
            _exchange = exchange;
            _openBrowser = openBrowser;
            _timeout = timeout ?? DocumensoConnectContract.DefaultTimeout;
        }

        /// <summary>The loopback port, once listening. For tests and diagnostics.</summary>
        internal int? Port { get; private set; }

        /// <summary>The authorize URL this attempt opened, once built. Holds no secret (the challenge is a hash).</summary>
        internal string? AuthorizeUrl { get; private set; }

        /// <summary>True only between the listener starting and the flow ending.</summary>
        internal bool IsListening => _listener?.IsListening == true;

        /// <summary>
        /// Runs the whole sign-in. Never throws for a flow outcome — every ending is a
        /// <see cref="DocumensoSignInResult"/>; Cancel via <paramref name="ct"/> is
        /// <see cref="DocumensoSignInOutcome.Cancelled"/>.
        /// </summary>
        /// <param name="progress">Plain sentences for the dialog. Never carries the code or the token.</param>
        internal async Task<DocumensoSignInResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _used, 1) != 0)
                throw new InvalidOperationException("A sign-in attempt runs once; start a new one.");

            string verifier = DocumensoPkce.NewVerifier();
            string state = DocumensoPkce.NewState();

            if (!TryStartListener(out var listener, out string host, out int port))
                return new(DocumensoSignInOutcome.ListenerFailed);
            _listener = listener;
            Port = port;

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(_timeout);
            // GetContextAsync takes no token; stopping the listener is what unblocks it.
            using var stop = wait.Token.Register(() => { try { listener.Stop(); } catch { } });
            try
            {
                string redirectUri = DocumensoConnectContract.RedirectUri(host, port);
                AuthorizeUrl = DocumensoConnectContract.AuthorizeUrl(_baseUrl, DocumensoPkce.Challenge(verifier), state, redirectUri);

                if (!_openBrowser(AuthorizeUrl))
                    return new(DocumensoSignInOutcome.BrowserFailed);
                progress?.Report("Waiting for you to sign in with your browser…");

                DocumensoCallback callback;
                while (true)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (Exception) when (wait.IsCancellationRequested)
                    {
                        return new(ct.IsCancellationRequested ? DocumensoSignInOutcome.Cancelled : DocumensoSignInOutcome.TimedOut);
                    }
                    catch (Exception)
                    {
                        // The listener failed under us (http.sys torn down, a network stack
                        // reset). Not a sign-in outcome the user caused; say it plainly.
                        return new(DocumensoSignInOutcome.ListenerFailed);
                    }

                    var url = context.Request.Url;
                    callback = url is null
                        ? new DocumensoCallback(DocumensoCallbackKind.NotCallback)
                        : DocumensoConnectContract.ParseCallback(url, state);
                    if (callback.Kind == DocumensoCallbackKind.NotCallback)
                    {
                        Reply(context, 404, "Not found", "There is nothing here.");
                        continue;
                    }

                    // Answer the browser before anything slow happens, so its tab never hangs
                    // waiting on the exchange. The page never echoes the code.
                    if (callback.Kind == DocumensoCallbackKind.Code)
                        Reply(context, 200, "Signed in", "You're signed in. You can close this tab and go back to TDPdf.");
                    else if (callback.Kind == DocumensoCallbackKind.Denied)
                        Reply(context, 200, "Not signed in", "TDPdf was not connected. You can close this tab.");
                    else
                        Reply(context, 400, "Sign-in not accepted",
                            "This sign-in response did not come from the attempt TDPdf started, so it was ignored. Close this tab and try again from TDPdf.");
                    break;
                }

                switch (callback.Kind)
                {
                    case DocumensoCallbackKind.StateMismatch: return new(DocumensoSignInOutcome.StateMismatch);
                    case DocumensoCallbackKind.Malformed: return new(DocumensoSignInOutcome.Malformed);
                    case DocumensoCallbackKind.Denied: return new(DocumensoSignInOutcome.Denied, DeniedReason: callback.Error);
                }

                // The redirect is in: the listener has done its job, close it now rather than
                // while the exchange runs.
                CloseListener();
                progress?.Report("Signing in…");
                try
                {
                    var grant = await _exchange.ExchangeAsync(callback.Code!, verifier, redirectUri, ct).ConfigureAwait(false);
                    return new(DocumensoSignInOutcome.Connected, grant);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return new(DocumensoSignInOutcome.Cancelled);
                }
                catch (DocumensoApiException ex) when (ex.Failure is DocumensoFailure.Network or DocumensoFailure.Timeout)
                {
                    return new(DocumensoSignInOutcome.Unreachable, Failure: ex);
                }
                catch (DocumensoApiException ex)
                {
                    return new(DocumensoSignInOutcome.ExchangeRejected, Failure: ex);
                }
            }
            finally
            {
                CloseListener();
            }
        }

        private void CloseListener()
        {
            var l = _listener;
            if (l is null) return;
            try { if (l.IsListening) l.Stop(); } catch { }
            try { l.Close(); } catch { }
        }

        /// <summary>
        /// A free loopback port, found by binding port 0, then registered with HttpListener. The
        /// port can in principle be taken between the two steps, so a few attempts are made.
        /// </summary>
        private static bool TryStartListener(out HttpListener listener, out string host, out int port)
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                port = FreeLoopbackPort();
                foreach (var h in new[] { "127.0.0.1", "localhost" })
                {
                    var l = new HttpListener();
                    try
                    {
                        l.Prefixes.Add($"http://{h}:{port}/");
                        l.Start();
                        listener = l;
                        host = h;
                        return true;
                    }
                    catch (Exception ex) when (ex is HttpListenerException or SocketException or InvalidOperationException or PlatformNotSupportedException)
                    {
                        try { l.Close(); } catch { }
                    }
                }
            }
            listener = null!;
            host = "";
            port = 0;
            return false;
        }

        private static int FreeLoopbackPort()
        {
            var t = new TcpListener(IPAddress.Loopback, 0);
            t.Start();
            try { return ((IPEndPoint)t.LocalEndpoint).Port; }
            finally { t.Stop(); }
        }

        /// <summary>A minimal, script-free page. Fixed text only — nothing from the request is reflected.</summary>
        private static void Reply(HttpListenerContext context, int status, string title, string message)
        {
            try
            {
                string html = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>TDPdf: " + title + "</title>"
                    + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
                    + "<style>body{font-family:Segoe UI,system-ui,sans-serif;background:#1e1e1e;color:#e6e6e6;display:flex;"
                    + "align-items:center;justify-content:center;min-height:100vh;margin:0}main{max-width:28rem;padding:2rem}"
                    + "h1{font-size:1.25rem;color:#9ccc65;margin:0 0 .5rem}p{line-height:1.5;margin:0}</style></head>"
                    + "<body><main><h1>" + title + "</h1><p>" + message + "</p></main></body></html>";
                byte[] body = Encoding.UTF8.GetBytes(html);
                var r = context.Response;
                r.StatusCode = status;
                r.ContentType = "text/html; charset=utf-8";
                r.Headers["Cache-Control"] = "no-store";
                r.Headers["Referrer-Policy"] = "no-referrer";
                r.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'";
                r.ContentLength64 = body.Length;
                r.OutputStream.Write(body, 0, body.Length);
                r.Close();
            }
            catch
            {
                // The browser went away; the flow does not depend on it reading the page.
            }
        }
    }
}
