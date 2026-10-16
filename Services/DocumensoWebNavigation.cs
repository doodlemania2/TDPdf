using System;
using System.IO;

namespace TDPdf.Services
{
    // Electronic Signature, Phase 6: the WPF- and WebView2-free rules behind the in-app signing
    // window (DocumensoWebWindow). Which addresses may load inside TDPdf, which go to the default
    // browser, which are refused outright, which page of the signing service is showing (so the
    // window can tell "you just signed" from "you are reading the audit log"), and where the
    // per-user browser profile lives. Pure, so tests/PdfCore pins all of it.
    //
    // WHY TOP-LEVEL PAGES AND NOT THE EMBED: Documenso's iframe "embed" authoring is
    // Enterprise-licensed. Loading the service's ordinary pages as top-level navigations in a
    // browser control is just a browser visiting a website — no embed licence, no Documenso code
    // in TDPdf (GPLv3 vs AGPL), HTTP and a browser view only.

    /// <summary>What the in-app window was opened to do.</summary>
    internal enum DocumensoWebPurpose
    {
        /// <summary>A recipient's signing page, <c>/sign/{token}</c> — yours (Sign This Document) or a recipient's at this computer.</summary>
        Sign,
        /// <summary>The owner's page for one request, <c>/t/{team}/documents/{id}</c>.</summary>
        Document,
        /// <summary>The owner's audit log page, <c>/t/{team}/documents/{id}/logs</c>, which also offers the certificate.</summary>
        AuditLog,
    }

    /// <summary>
    /// One page to show. <see cref="Url"/> may contain a recipient's signing token — whoever holds
    /// it can sign — so <see cref="ToString"/> leaves it out and nothing logs it.
    /// </summary>
    internal sealed record DocumensoWebRequest(string Url, string Title, DocumensoWebPurpose Purpose, string? EnvelopeId)
    {
        public override string ToString() => $"DocumensoWebRequest {{ Purpose = {Purpose}, Title = {Title} }}";
    }

    /// <summary>Which page of the signing service is showing. Decided from the path alone.</summary>
    internal enum DocumensoWebPage
    {
        Other,
        /// <summary><c>/sign/{token}</c>: the signing page itself.</summary>
        Signing,
        /// <summary><c>/sign/{token}/complete</c>: Documenso's "Document signed" page.</summary>
        SigningComplete,
        /// <summary><c>/sign/{token}/rejected</c>.</summary>
        SigningRejected,
        /// <summary><c>/sign/{token}/expired</c>.</summary>
        SigningExpired,
        /// <summary><c>/sign/{token}/waiting</c>: sequential signing, not this recipient's turn yet.</summary>
        SigningWaiting,
        /// <summary><c>/t/{team}/documents/{id}</c>.</summary>
        Document,
        /// <summary><c>/t/{team}/documents/{id}/logs</c>.</summary>
        AuditLog,
        /// <summary><c>/signin</c>, <c>/signup</c> and friends: the owner pages need a web sign-in first.</summary>
        SignIn,
    }

    /// <summary>What to do with a navigation the page asked for.</summary>
    internal enum DocumensoNavigationDecision
    {
        /// <summary>Same service: load it here.</summary>
        Allow,
        /// <summary>Somewhere else on the web (help pages, a mailto:): hand it to the default browser / mail app.</summary>
        OpenExternally,
        /// <summary>Anything that is not web (<c>file:</c>, <c>javascript:</c>, custom protocols): refused.</summary>
        Block,
    }

    internal static class DocumensoWebNavigation
    {
        /// <summary>
        /// Microsoft's Evergreen WebView2 Runtime bootstrapper. A Microsoft fwlink, so it keeps
        /// pointing at the current installer; shown in the status bar when the runtime is missing.
        /// </summary>
        internal const string RuntimeDownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        /// <summary>How often an open window asks the service whether its request has completed.</summary>
        internal static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The persistent per-user browser profile: <c>%LOCALAPPDATA%\TDPdf\WebView2</c>. Persistent so
        /// a web sign-in to the service survives restarts; per user, never beside the EXE (a
        /// Program Files install cannot write there, and a portable copy must not carry a session).
        /// </summary>
        internal static string UserDataFolder(string localAppData) => Path.Combine(localAppData, "TDPdf", "WebView2");

        /// <summary>
        /// True when <paramref name="url"/> is on the configured service: same scheme, same host
        /// (case-insensitive), same port, and under the base URL's path. A look-alike host
        /// (<c>sign.tdpdf.com.evil.example</c>), a sibling path (<c>/signx</c> under a <c>/sign</c>
        /// base) and plain <c>http</c> to an <c>https</c> service are all out of scope.
        /// </summary>
        internal static bool IsInScope(string baseUrl, string? url)
        {
            string? b = DocumensoConfig.Normalise(baseUrl);
            if (b is null || !Uri.TryCreate(b, UriKind.Absolute, out var root)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            if (u.Scheme != root.Scheme) return false;
            if (!string.Equals(u.Host, root.Host, StringComparison.OrdinalIgnoreCase) || u.Port != root.Port) return false;
            if (!string.IsNullOrEmpty(u.UserInfo)) return false;
            string basePath = root.AbsolutePath.TrimEnd('/');
            if (basePath.Length == 0) return true;
            string path = u.AbsolutePath;
            return path.Equals(basePath, StringComparison.Ordinal) || path.StartsWith(basePath + "/", StringComparison.Ordinal);
        }

        /// <summary>
        /// Where a requested navigation goes. In scope → here. Other <c>http(s)</c> and
        /// <c>mailto:</c> → the system. <c>about:blank</c> (WebView2's own initial page) → here.
        /// A <c>blob:</c> URL is judged by the origin it belongs to (Documenso builds downloads that
        /// way). Everything else → refused.
        /// </summary>
        internal static DocumensoNavigationDecision Decide(string baseUrl, string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return DocumensoNavigationDecision.Block;
            string s = url.Trim();
            if (s.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return DocumensoNavigationDecision.Allow;
            if (s.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                return IsInScope(baseUrl, s[5..]) ? DocumensoNavigationDecision.Allow : DocumensoNavigationDecision.Block;
            if (IsInScope(baseUrl, s)) return DocumensoNavigationDecision.Allow;
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return DocumensoNavigationDecision.Block;
            if (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeMailto)
                return DocumensoNavigationDecision.OpenExternally;
            return DocumensoNavigationDecision.Block;
        }

        /// <summary>Which page of the service <paramref name="url"/> is. <see cref="DocumensoWebPage.Other"/> for anything off the service.</summary>
        internal static DocumensoWebPage Classify(string baseUrl, string? url)
        {
            if (!IsInScope(baseUrl, url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return DocumensoWebPage.Other;
            string basePath = new Uri(DocumensoConfig.Normalise(baseUrl)!).AbsolutePath.TrimEnd('/');
            string rest = u.AbsolutePath[basePath.Length..];
            var seg = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (seg.Length >= 2 && seg[0] == "sign")
            {
                if (seg.Length == 2) return DocumensoWebPage.Signing;
                if (seg.Length == 3)
                    return seg[2] switch
                    {
                        "complete" => DocumensoWebPage.SigningComplete,
                        "rejected" => DocumensoWebPage.SigningRejected,
                        "expired" => DocumensoWebPage.SigningExpired,
                        "waiting" => DocumensoWebPage.SigningWaiting,
                        _ => DocumensoWebPage.Other,
                    };
                return DocumensoWebPage.Other;
            }
            if (seg.Length >= 4 && seg[0] == "t" && seg[2] == "documents")
            {
                if (seg.Length == 4) return DocumensoWebPage.Document;
                if (seg.Length == 5 && seg[4] == "logs") return DocumensoWebPage.AuditLog;
                return DocumensoWebPage.Other;
            }
            if (seg.Length >= 1 && seg[0] is "signin" or "signup" or "forgot-password" or "reset-password" or "verify-email")
                return DocumensoWebPage.SignIn;
            return DocumensoWebPage.Other;
        }

        /// <summary>
        /// The address bar's text: host and path, never the query, and never a signing token —
        /// <c>/sign/{token}/…</c> is shown as <c>/sign/…</c>, because the window can be on screen
        /// while someone else looks at it.
        /// </summary>
        internal static string DisplayAddress(string baseUrl, string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
                return DocumensoConfig.HostOf(baseUrl);
            string path = u.AbsolutePath;
            var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int i = Array.IndexOf(seg, "sign");
            if (i >= 0 && i + 1 < seg.Length)
            {
                seg[i + 1] = "…";
                path = "/" + string.Join("/", seg);
            }
            return u.Authority + (path == "/" ? "" : path);
        }

        /// <summary>True for a page after which nothing more can be signed on it.</summary>
        internal static bool IsSigningFinished(DocumensoWebPage p)
            => p is DocumensoWebPage.SigningComplete or DocumensoWebPage.SigningRejected or DocumensoWebPage.SigningExpired;
    }
}
