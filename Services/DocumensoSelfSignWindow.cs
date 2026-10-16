using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using static TDPdf.Services.DocumensoUi;

namespace TDPdf.Services
{
    /// <summary>
    /// File ▸ Electronic Signature ▸ Sign This Document (design §5.4b, Phase 6).
    /// Confirm → upload and send to yourself → open your signing page — inside TDPdf
    /// (<see cref="DocumensoWebWindow"/>) when the WebView2 Runtime is present, else in the default
    /// browser → wait, with Refresh → on completion, Download signed copy and Open it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The copy is honest about who signs.</b> TDPdf never signs: Documenso has no public signing
    /// endpoint, so the signature is made on the signing service's own page (shown in TDPdf's
    /// signing window, or the browser) and the signing service seals the result. Every sentence here
    /// says so.
    /// </para>
    /// <para>
    /// <b>Modeless, one at a time</b>, like Signature Requests: the user's attention is in the
    /// browser for as long as this is open, and TDPdf should stay usable meanwhile. Closing it never
    /// loses anything — the request is tracked and Signature Requests picks it up.
    /// </para>
    /// <para>
    /// No polling of its own: Refresh is pressed by the person who just signed (design §7.3). When
    /// the page is shown in TDPdf's signing window, that window notices completion and refreshes
    /// this one.
    /// </para>
    /// </remarks>
    internal static class DocumensoSelfSignWindow
    {
        /// <summary>What the send produced. <see cref="SigningUrl"/> is a secret: whoever holds it can sign.</summary>
        internal sealed record SendOutcome(bool Ok, string? Error, string? EnvelopeId, string? SigningUrl);

        internal sealed class Inputs
        {
            public string Host { get; init; } = "";
            public string DocumentName { get; init; } = "";
            public string Email { get; init; } = "";
            /// <summary>One sentence saying where the signature will go.</summary>
            public string FieldNote { get; init; } = "";
            /// <summary>Runs the send. Must not throw; failures come back as an unsuccessful outcome.</summary>
            public Func<IProgress<string>, CancellationToken, Task<SendOutcome>> SendAsync { get; init; }
                = (_, _) => Task.FromResult(new SendOutcome(false, "Not available.", null, null));
            /// <summary>GET /envelope/{id}. May throw <see cref="DocumensoApiException"/>.</summary>
            public Func<string, CancellationToken, Task<DocumensoEnvelope>> GetEnvelopeAsync { get; init; }
                = (_, _) => Task.FromException<DocumensoEnvelope>(new InvalidOperationException());
            /// <summary>Save dialog + download. The saved path, or null when the user cancelled. May throw.</summary>
            public Func<Window, DocumensoEnvelope, Task<(string? Path, string Message)>> DownloadSignedAsync { get; init; }
                = (_, _) => Task.FromResult<(string?, string)>((null, ""));
            /// <summary>Opens a saved file in a new tab.</summary>
            public Func<string, Task> OpenAsync { get; init; } = _ => Task.CompletedTask;
            /// <summary>
            /// True when the signing page will open inside TDPdf (the WebView2 Runtime is present);
            /// only changes what the window says.
            /// </summary>
            public bool InApp { get; init; }
            /// <summary>
            /// Shows the signing page: (signing URL, envelope id, called-when-completed) → whether it
            /// is showing anywhere. Null: the default browser, as before Phase 6.
            /// </summary>
            public Func<string, string, Action, bool>? OpenSigningPage { get; init; }
        }

        private static Window? _open;

        /// <summary>True while a Sign This Document window is up (brought forward instead of a second one).</summary>
        internal static bool TryActivate()
        {
            if (_open is not { } w) return false;
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
            return true;
        }

        // ── "What is your address?" ─────────────────────────────────────────────────────────

        /// <summary>
        /// Asks for the account's email address, which Documenso v2 cannot tell TDPdf before the
        /// first envelope exists (it has no "who am I"). Starts empty. Null when cancelled.
        /// </summary>
        internal static string? AskEmail(Window? owner, string host)
        {
            string? result = null;
            var (win, root, closeBtn) = CreateShell(owner, "Sign This Document", 440);
            var body = new StackPanel { Margin = new Thickness(20, 14, 20, 6) };
            root.Children.Add(body);
            body.Children.Add(Para($"Which email address do you sign in to {host} with?", "TextPrimary", 13, new Thickness(0, 0, 0, 6)));
            body.Children.Add(Para("You are added as the one signer. TDPdf remembers the address for next time.",
                "TextSecondary", 12, new Thickness(0, 0, 0, 10)));
            var box = MakeTextBox("", "Your email address", DocumensoSendPlan.MaxEmail);
            body.Children.Add(box);
            var error = Para("", "DangerRed", 11, new Thickness(0, 4, 0, 0));
            error.Visibility = Visibility.Collapsed;
            body.Children.Add(error);

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 12, 20, 16) };
            var ok = MakeButton(owner, "Continue", primary: true);
            ok.IsDefault = true;
            var cancel = MakeButton(owner, "Cancel", primary: false);
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(8, 0, 0, 0);
            footer.Children.Add(ok);
            footer.Children.Add(cancel);
            root.Children.Add(footer);

            ok.Click += (_, _) =>
            {
                string e = box.Text.Trim();
                if (!DocumensoSendPlan.IsValidEmail(e))
                {
                    error.Text = "Enter a valid email address, for example you@example.com.";
                    error.Visibility = Visibility.Visible;
                    box.Focus();
                    return;
                }
                result = e;
                win.Close();
            };
            cancel.Click += (_, _) => win.Close();
            closeBtn.Click += (_, _) => win.Close();
            win.Loaded += (_, _) => box.Focus();
            win.ShowDialog();
            return result;
        }

        // ── The handoff window ───────────────────────────────────────────────────────────────

        internal static void Show(Window? owner, Inputs inputs)
        {
            if (TryActivate()) return;
            var (win, root, closeBtn) = CreateShell(owner, "Sign This Document", 520);
            _open = win;
            win.Closed += (_, _) => { if (ReferenceEquals(_open, win)) _open = null; };

            var body = new StackPanel { Margin = new Thickness(20, 14, 20, 4) };
            root.Children.Add(body);
            var status = Para("", "TextSecondary", 12, new Thickness(20, 4, 20, 0));
            status.Visibility = Visibility.Collapsed;
            root.Children.Add(status);
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 10, 20, 16) };
            root.Children.Add(footer);

            CancellationTokenSource? cts = null;
            bool busy = false;
            string? envelopeId = null, signingUrl = null, savedPath = null;
            DocumensoEnvelope? completed = null;

            void Say(string text, string brushKey = "TextSecondary")
            {
                status.Text = text;
                status.Foreground = Brush(brushKey);
                status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            }

            Button Add(string text, bool primary, Action onClick)
            {
                var b = MakeButton(owner, text, primary);
                if (footer.Children.Count > 0) b.Margin = new Thickness(8, 0, 0, 0);
                b.Click += (_, _) => onClick();
                footer.Children.Add(b);
                return b;
            }

            void Body(params string[] paragraphs)
            {
                body.Children.Clear();
                for (int i = 0; i < paragraphs.Length; i++)
                    body.Children.Add(Para(paragraphs[i], i == 0 ? "TextPrimary" : "TextSecondary", i == 0 ? 13 : 12, new Thickness(0, 0, 0, 8)));
            }

            string Describe(DocumensoApiException ex) => ex.Failure switch
            {
                DocumensoFailure.AuthRejected => $"{inputs.Host} rejected your sign-in — it may have expired. Connect again in Settings ▸ Electronic signature, then Refresh.",
                DocumensoFailure.Network or DocumensoFailure.Timeout => $"Could not reach {inputs.Host}. Check your connection and try again.",
                DocumensoFailure.RateLimited => $"{inputs.Host} is busy. Wait a minute and try again.",
                DocumensoFailure.NotFound => $"{inputs.Host} no longer has this request.",
                _ => $"{inputs.Host} answered unexpectedly{(ex.StatusCode is int s ? $" (HTTP {s})" : "")}.",
            };

            // ── States ──────────────────────────────────────────────────────────────────────
            void ShowConfirm()
            {
                Body($"Sign “{inputs.DocumentName}” electronically on {inputs.Host}.",
                    $"TDPdf uploads this document to {inputs.Host}, sends it for signature to you alone ({inputs.Email}), " +
                    (inputs.InApp
                        ? "and opens the signing page here in TDPdf. You sign on that page, and the signing service seals the signed copy — "
                        : "and opens the signing page in your browser. You sign there, and the signing service seals the signed copy — ") +
                    "TDPdf does not sign anything itself.",
                    inputs.FieldNote);
                footer.Children.Clear();
                var go = Add(inputs.InApp ? "Continue to sign" : "Continue in browser", primary: true, async () => await StartAsync());
                go.IsDefault = true;
                Add("Cancel", primary: false, () => win.Close());
            }

            async Task StartAsync()
            {
                if (busy) return;
                busy = true;
                footer.Children.Clear();
                Add("Stop", primary: false, () => cts?.Cancel());
                cts = new CancellationTokenSource();
                Say("Sending…  (Esc to cancel)");
                var progress = new Progress<string>(t => Say(t));
                SendOutcome outcome;
                try
                {
                    outcome = await inputs.SendAsync(progress, cts.Token);
                }
                catch (Exception)
                {
                    outcome = new SendOutcome(false, "Something went wrong. Nothing was sent.", null, null);
                }
                finally
                {
                    busy = false;
                    cts.Dispose();
                    cts = null;
                }

                if (!outcome.Ok || outcome.EnvelopeId is null)
                {
                    Say(outcome.Error ?? "Nothing was sent.", "DangerRed");
                    ShowConfirm();
                    return;
                }
                envelopeId = outcome.EnvelopeId;
                signingUrl = outcome.SigningUrl;
                Say("");
                ShowWaiting(openBrowser: true);
            }

            void ShowWaiting(bool openBrowser)
            {
                Body(inputs.InApp ? $"Sign on the {inputs.Host} page that opened in TDPdf." : $"Signing happens on {inputs.Host} in your browser.",
                    (inputs.InApp ? "TDPdf notices when you're done; you can also press Refresh. " : "When you're done, press Refresh. ") +
                    "You can close this window and pick the document up later from Signature Requests.");
                footer.Children.Clear();
                if (signingUrl is not null)
                    Add("Open signing page again", primary: false, () => OpenSigningPage());
                var refresh = Add("Refresh", primary: true, async () => await RefreshAsync());
                refresh.IsDefault = true;
                Add("Close", primary: false, () => win.Close());
                if (openBrowser) OpenSigningPage();
            }

            void OpenSigningPage()
            {
                if (signingUrl is null)
                {
                    Say($"{inputs.Host} did not return a signing link. Open the document from Signature Requests or from your email.", "WarningOrange");
                    return;
                }
                if (inputs.OpenSigningPage is { } open && envelopeId is not null)
                {
                    // TDPdf's signing window, or — when it cannot run — the browser, as decided by
                    // the caller, which also puts any explanation in the status bar.
                    if (open(signingUrl, envelopeId, () => _ = RefreshAsync())) Say("Opened the signing page.");
                    else Say("Could not open the signing page. Open the document from your email from the signing service instead.", "DangerRed");
                    return;
                }
                if (OpenInBrowser(signingUrl)) Say($"Opened the signing page in your browser.");
                else Say("Could not open a browser. Open the document from your email from the signing service instead.", "DangerRed");
            }

            async Task RefreshAsync()
            {
                if (busy || envelopeId is null) return;
                busy = true;
                Say("Checking…");
                try
                {
                    var env = await inputs.GetEnvelopeAsync(envelopeId, CancellationToken.None);
                    switch (DocumensoSelfSignPlan.StateOf(env.Status))
                    {
                        case ESignSelfState.Completed:
                            completed = env;
                            Say("");
                            ShowCompleted();
                            break;
                        case ESignSelfState.Rejected:
                            Body("You declined to sign this document.", "Nothing was signed. The request is closed on the signing service.");
                            footer.Children.Clear();
                            Add("Close", primary: true, () => win.Close());
                            Say("");
                            break;
                        case ESignSelfState.Cancelled:
                            Body("This signature request was cancelled.", "Nothing was signed.");
                            footer.Children.Clear();
                            Add("Close", primary: true, () => win.Close());
                            Say("");
                            break;
                        default:
                            Say("Not signed yet. If you have just finished, the signing service may still be sealing it — Refresh again in a few seconds.", "WarningOrange");
                            break;
                    }
                }
                catch (DocumensoApiException ex)
                {
                    Say(Describe(ex), "DangerRed");
                }
                catch (Exception)
                {
                    Say("Something went wrong. Try Refresh again.", "DangerRed");
                }
                finally
                {
                    busy = false;
                }
            }

            void ShowCompleted()
            {
                Body($"Signed. {inputs.Host} has sealed the signed copy.",
                    "Download it to keep it. Note that saving it again in TDPdf would remove the digital signature, " +
                    "so keep the downloaded file as your signed original.");
                footer.Children.Clear();
                Button? open = null;
                var download = Add("Download signed copy…", primary: true, async () =>
                {
                    if (busy || completed is null) return;
                    busy = true;
                    try
                    {
                        var (path, message) = await inputs.DownloadSignedAsync(win, completed);
                        if (path is not null)
                        {
                            savedPath = path;
                            if (open is not null) open.IsEnabled = true;
                            Say(message, "AccentGreen");
                        }
                        else if (message.Length > 0)
                        {
                            Say(message, "DangerRed");
                        }
                    }
                    catch (DocumensoApiException ex)
                    {
                        Say(Describe(ex), "DangerRed");
                    }
                    catch (Exception)
                    {
                        Say("The download did not complete.", "DangerRed");
                    }
                    finally
                    {
                        busy = false;
                    }
                });
                download.IsDefault = true;
                open = Add("Open it", primary: false, async () =>
                {
                    if (savedPath is null) return;
                    await inputs.OpenAsync(savedPath);
                    win.Close();
                });
                open.IsEnabled = savedPath is not null;
                open.ToolTip = "Opens the downloaded signed copy in a new tab";
                Add("Close", primary: false, () => win.Close());
            }

            closeBtn.Click += (_, _) => win.Close();
            win.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                if (busy) { cts?.Cancel(); Say("Stopping…"); }
                else win.Close();
            };
            win.Closing += (_, e) =>
            {
                // Never abandon a send half-way: Esc / Stop cancels it cleanly (and rolls back).
                if (busy && cts is not null) { e.Cancel = true; cts.Cancel(); Say("Stopping…"); }
            };

            ShowConfirm();
            win.Show();
        }
    }
}
