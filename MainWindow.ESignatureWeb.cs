// MainWindow — Electronic Signature: the signing service's pages inside TDPdf (Phase 6).
// Design: docs/design/esignature-documenso.md, "Phase 6 as built".
//
// One entry point, ESignOpenWeb, used by Sign This Document (your signing page), Signature
// Requests (Open, Sign on this computer, Audit log & certificate) and anything later. It shows
// Services/DocumensoWebWindow when the WebView2 Runtime is present and otherwise opens the same
// page in the default browser, saying why in the status bar — never a modal, and never the page's
// address (a signing link is as good as the signature).

using System.Collections.Generic;
using System.Windows;
using TDPdf.Diagnostics;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow
    {
        /// <summary>
        /// Shows <paramref name="request"/> in the in-app signing window, or in the default browser
        /// when the WebView2 Runtime is missing. Returns true when the page is showing somewhere.
        /// </summary>
        /// <param name="completed">Called once when the request is seen completed (in-app only).</param>
        private bool ESignOpenWeb(Window? owner, DocumensoClient client, DocumensoWebRequest request, System.Action? completed = null)
        {
            if (WebView2Bootstrap.RuntimeVersion() is null)
            {
                ESignTrackWeb(request.Purpose, "noruntime");
                return ESignWebFallback(request.Url, runtimeMissing: true);
            }

            ESignTrackWeb(request.Purpose, "inapp");
            DocumensoWebWindow.Show(owner ?? this, new DocumensoWebWindow.Inputs
            {
                BaseUrl = client.BaseUrl,
                Host = DocumensoConfig.HostOf(client.BaseUrl),
                Request = request,
                GetEnvelopeAsync = (id, ct) => client.GetEnvelopeAsync(id, ct),
                DownloadSignedAsync = async (w, env) =>
                {
                    var r = await ESignDownloadArtifactAsync(w, client, env, DocumensoArtifact.SignedCopy, openAfter: false);
                    return r is null ? (null, "") : (r.Path, r.Message);
                },
                OpenAsync = OpenInTabAsync,
                Fallback = url =>
                {
                    ESignTrackWeb(request.Purpose, "failed");
                    ESignWebFallback(url, runtimeMissing: false);
                },
                Completed = completed,
            });
            return true;
        }

        /// <summary>
        /// The default-browser path. The status line says why, and how to get the in-app view; it
        /// never names the address.
        /// </summary>
        private bool ESignWebFallback(string url, bool runtimeMissing)
        {
            bool opened = DocumensoUi.OpenInBrowser(url);
            if (runtimeMissing)
                SetStatus(opened
                    ? $"Electronic Signature opened in your browser. To sign inside TDPdf, install the Microsoft Edge WebView2 Runtime: {DocumensoWebNavigation.RuntimeDownloadUrl}"
                    : $"Electronic Signature needs the Microsoft Edge WebView2 Runtime ({DocumensoWebNavigation.RuntimeDownloadUrl}) or a default browser, and neither could be started.");
            else
                SetStatus(opened
                    ? "Electronic Signature: the in-app view could not start, so the page opened in your browser."
                    : "Electronic Signature: the page could not be opened here or in a browser.");
            return opened;
        }

        /// <summary>Which way a page was shown. The allow-list of design §2.4: no address, no id, no title.</summary>
        private static void ESignTrackWeb(DocumensoWebPurpose purpose, string outcome)
        {
            try
            {
                Telemetry.TrackEvent("DocumensoWebView", new Dictionary<string, string>
                {
                    ["purpose"] = purpose.ToString().ToLowerInvariant(),
                    ["outcome"] = outcome,
                });
            }
            catch { /* never in the way */ }
        }
    }
}
