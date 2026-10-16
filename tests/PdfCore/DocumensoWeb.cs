using TDPdf.Services;

/// <summary>
/// Pins the rules behind the in-app signing window (<see cref="DocumensoWebNavigation"/>): what may
/// load inside TDPdf, what goes to the default browser, what is refused, how the window recognises
/// "you just signed", that the address bar never shows a signing token, and where the profile lives.
/// The WebView2 control itself cannot run off Windows; this is everything around it that can.
/// </summary>
internal static class DocumensoWeb
{
    private const string Base = "https://sign.tdpdf.com";

    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("Electronic Signature — in-app signing window rules");
        Scope(Check);
        Decisions(Check);
        Pages(Check);
        Display(Check);
        Misc(Check);
    }

    private static void Scope(Action<string, bool, string> Check)
    {
        void In(string label, string url, bool want, string b = Base)
            => Check(label, DocumensoWebNavigation.IsInScope(b, url) == want, url);

        In("the service's own pages are in scope", Base + "/sign/abc", true);
        In("host match ignores case", "https://SIGN.tdpdf.com/t/x/documents/1", true);
        In("a look-alike host is out", "https://sign.tdpdf.com.evil.example/sign/abc", false);
        In("a sub-domain is out", "https://evil.sign.tdpdf.com/", false);
        In("plain http to an https service is out", "http://sign.tdpdf.com/sign/abc", false);
        In("another port is out", "https://sign.tdpdf.com:8443/", false);
        In("credentials in the URL are out", "https://me@sign.tdpdf.com/", false);
        In("a relative address is out", "/sign/abc", false);
        In("under a base path: the path itself is in", "https://org.example/docs/sign/abc", true, "https://org.example/docs");
        In("under a base path: a sibling path is out", "https://org.example/docsx/sign/abc", false, "https://org.example/docs");
        In("under a base path: the root is out", "https://org.example/", false, "https://org.example/docs");
    }

    private static void Decisions(Action<string, bool, string> Check)
    {
        void D(string label, string? url, DocumensoNavigationDecision want)
        {
            var got = DocumensoWebNavigation.Decide(Base, url);
            Check(label, got == want, $"{url} -> {got}");
        }
        D("the service loads here", Base + "/signin?returnTo=%2Ft%2Fx", DocumensoNavigationDecision.Allow);
        D("WebView2's initial about:blank loads here", "about:blank", DocumensoNavigationDecision.Allow);
        D("a blob: download from the service loads here", "blob:https://sign.tdpdf.com/2b6f0c8e-1111-2222-3333-444455556666", DocumensoNavigationDecision.Allow);
        D("a blob: from elsewhere is refused", "blob:https://evil.example/2b6f0c8e", DocumensoNavigationDecision.Block);
        D("another website goes to the default browser", "https://documenso.com/pricing", DocumensoNavigationDecision.OpenExternally);
        D("plain http elsewhere goes to the default browser", "http://example.org/", DocumensoNavigationDecision.OpenExternally);
        D("mailto: goes to the mail app", "mailto:help@tdpdf.com", DocumensoNavigationDecision.OpenExternally);
        D("file: is refused", "file:///C:/Windows/System32/calc.exe", DocumensoNavigationDecision.Block);
        D("javascript: is refused", "javascript:alert(1)", DocumensoNavigationDecision.Block);
        D("a custom protocol is refused", "ms-settings:privacy", DocumensoNavigationDecision.Block);
        D("data: is refused", "data:text/html,<b>x</b>", DocumensoNavigationDecision.Block);
        D("nothing is refused", null, DocumensoNavigationDecision.Block);
    }

    private static void Pages(Action<string, bool, string> Check)
    {
        void P(string path, DocumensoWebPage want, string b = Base)
        {
            var got = DocumensoWebNavigation.Classify(b, (b == Base ? Base : b) + path);
            Check($"page {path} → {want}", got == want, got.ToString());
        }
        P("/sign/tok_123", DocumensoWebPage.Signing);
        P("/sign/tok_123/complete", DocumensoWebPage.SigningComplete);
        P("/sign/tok_123/rejected", DocumensoWebPage.SigningRejected);
        P("/sign/tok_123/expired", DocumensoWebPage.SigningExpired);
        P("/sign/tok_123/waiting", DocumensoWebPage.SigningWaiting);
        P("/sign/tok_123/somethingelse", DocumensoWebPage.Other);
        P("/t/personal_abc/documents/envelope_xyz", DocumensoWebPage.Document);
        P("/t/personal_abc/documents/envelope_xyz/logs", DocumensoWebPage.AuditLog);
        P("/t/personal_abc/documents/envelope_xyz/edit", DocumensoWebPage.Other);
        P("/signin", DocumensoWebPage.SignIn);
        P("/signup", DocumensoWebPage.SignIn);
        P("/", DocumensoWebPage.Other);
        P("/sign/tok/complete", DocumensoWebPage.SigningComplete, "https://org.example/docs");
        Check("a completed page on ANOTHER host is not completion",
              DocumensoWebNavigation.Classify(Base, "https://evil.example/sign/tok/complete") == DocumensoWebPage.Other, "");
        Check("finished pages: complete, rejected, expired — not waiting or signing",
              DocumensoWebNavigation.IsSigningFinished(DocumensoWebPage.SigningComplete)
              && DocumensoWebNavigation.IsSigningFinished(DocumensoWebPage.SigningRejected)
              && DocumensoWebNavigation.IsSigningFinished(DocumensoWebPage.SigningExpired)
              && !DocumensoWebNavigation.IsSigningFinished(DocumensoWebPage.SigningWaiting)
              && !DocumensoWebNavigation.IsSigningFinished(DocumensoWebPage.Signing), "");
    }

    private static void Display(Action<string, bool, string> Check)
    {
        const string secret = "Xq9_SECRET_recipient_token";
        string a = DocumensoWebNavigation.DisplayAddress(Base, $"{Base}/sign/{secret}/complete?x=1#y");
        Check("the address bar hides a signing token", !a.Contains(secret) && a == "sign.tdpdf.com/sign/…/complete", a);
        string b = DocumensoWebNavigation.DisplayAddress(Base, $"{Base}/t/team/documents/envelope_1?token=abc");
        Check("the address bar never shows a query", b == "sign.tdpdf.com/t/team/documents/envelope_1", b);
        string c = DocumensoWebNavigation.DisplayAddress(Base, "about:blank");
        Check("a non-web address shows the service's host", c == "sign.tdpdf.com", c);

        var req = new DocumensoWebRequest($"{Base}/sign/{secret}", "Sign — Lease.pdf", DocumensoWebPurpose.Sign, "envelope_1");
        Check("a page request's text never carries its URL (it may hold a signing token)", !req.ToString().Contains(secret), req.ToString());
    }

    private static void Misc(Action<string, bool, string> Check)
    {
        string root = OperatingSystem.IsWindows() ? @"C:\Users\me\AppData\Local" : "/home/me/.local/share";
        string f = DocumensoWebNavigation.UserDataFolder(root);
        Check("the browser profile is per user under TDPdf, never beside the EXE",
              f == Path.Combine(root, "TDPdf", "WebView2"), f);
        Check("the runtime link is Microsoft's Evergreen bootstrapper fwlink",
              DocumensoWebNavigation.RuntimeDownloadUrl == "https://go.microsoft.com/fwlink/p/?LinkId=2124703", DocumensoWebNavigation.RuntimeDownloadUrl);
        Check("status is polled no faster than every 10 seconds", DocumensoWebNavigation.StatusPollInterval >= TimeSpan.FromSeconds(10), "");
    }
}
