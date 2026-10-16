using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TDPdf.Services
{
    /// <summary>
    /// The themed Connect / Disconnect dialog for Electronic Signature. The normal way in is
    /// <b>Sign in with your browser</b>: the TDPdf Signing sign-in page opens, the user signs in (or
    /// creates an account), and TDPdf receives an API token through a loopback redirect with PKCE
    /// (<see cref="DocumensoBrowserSignIn"/>). Pasting a token by hand stays available under
    /// <b>Advanced</b>, so an instance without the sign-in endpoint still works exactly as before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built in code with the custom-chrome recipe every TDPdf dialog uses (borderless, rounded
    /// accent border, draggable Consolas title bar, MDL2 close glyph — see
    /// <c>MainWindow.OpenSignatureCreator</c> and <c>TdpDialog.CreateShell</c>).
    /// </para>
    /// <para>
    /// <b>Never stores an unvalidated token</b>, whichever way it arrived. A token from the browser
    /// sign-in is probed with the same cheap <c>GET /envelope</c> as a pasted one and stored only
    /// once it is accepted. Rejected or unreachable → inline red text, nothing stored; there is
    /// deliberately no "save anyway".
    /// </para>
    /// <para>
    /// <b>The token is never shown.</b> Every status line is fixed text plus the host; the browser
    /// sign-in reports progress in sentences that cannot carry it (see
    /// <see cref="DocumensoBrowserSignIn.RunAsync"/>).
    /// </para>
    /// </remarks>
    internal static class DocumensoConnectWindow
    {
        internal enum Outcome { Unchanged, Connected, Disconnected }

        /// <summary>Shows the dialog modally and reports what changed.</summary>
        internal static Outcome Show(Window? owner, string baseUrl)
        {
            string host = DocumensoConfig.HostOf(baseUrl);
            var existing = DocumensoCredentialStore.ReadMetadataFor(baseUrl);
            var outcome = Outcome.Unchanged;
            CancellationTokenSource? cts = null;
            bool busy = false;

            var win = new Window
            {
                Title = "Electronic Signature",
                Width = 470,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                Owner = owner,
                WindowStartupLocation = owner is not null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
            };

            var outer = new Border
            {
                Background = Brush("BgDark"),
                BorderBrush = Brush("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
            };
            var root = new StackPanel();
            outer.Child = root;
            win.Content = outer;

            // ── Title bar ──────────────────────────────────────────────────────────────────
            var titleBar = new Border
            {
                Background = Brush("BgPanel"),
                Padding = new Thickness(14, 8, 8, 8),
                CornerRadius = new CornerRadius(5, 5, 0, 0),
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleText = new TextBlock
            {
                Text = "Connect TDPdf Signing",
                Foreground = Brush("AccentGreen"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var closeBtn = new Button
            {
                Content = "\uE8BB",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Width = 28,
                Height = 28,
                Background = Brushes.Transparent,
                Foreground = Brush("TextSecondary"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Close",
            };
            System.Windows.Automation.AutomationProperties.SetName(closeBtn, "Close");
            closeBtn.MouseEnter += (_, _) => closeBtn.Foreground = Brush("DangerRed");
            closeBtn.MouseLeave += (_, _) => closeBtn.Foreground = Brush("TextSecondary");
            closeBtn.Click += (_, _) => win.Close();
            Grid.SetColumn(closeBtn, 1);
            titleGrid.Children.Add(titleText);
            titleGrid.Children.Add(closeBtn);
            titleBar.Child = titleGrid;
            root.Children.Add(titleBar);

            // ── Body: browser sign-in ──────────────────────────────────────────────────────
            var body = new StackPanel { Margin = new Thickness(20, 16, 20, 8) };
            root.Children.Add(body);

            body.Children.Add(Para(
                $"Electronic Signature uses TDPdf Signing at {host}. Sign in once and TDPdf can send " +
                "documents for signature and let you sign them here.",
                "TextPrimary", 13, new Thickness(0, 0, 0, 8)));
            body.Children.Add(Para(
                "Your browser opens the sign-in page. No account yet? You can create one there, then come back.",
                "TextSecondary", 12, new Thickness(0, 0, 0, 12)));

            if (existing is not null)
            {
                string who = existing.AccountEmail is { Length: > 0 } email ? $" as {email}" : "";
                body.Children.Add(Para(
                    $"Already connected to {host}{who}. Sign in again to replace it, or disconnect.",
                    "TextSecondary", 12, new Thickness(0, 0, 0, 10)));
            }

            var signInBtn = MakeButton("Sign in with your browser", primary: true);
            signInBtn.HorizontalAlignment = HorizontalAlignment.Left;
            signInBtn.IsDefault = true;
            signInBtn.Margin = new Thickness(0, 0, 0, 10);
            body.Children.Add(signInBtn);

            var status = Para("", "TextSecondary", 12, new Thickness(0, 0, 0, 8));
            status.Visibility = Visibility.Collapsed;
            body.Children.Add(status);

            // ── Advanced: paste a token (the pre-2.1 path, kept so nothing regresses) ───────
            var advancedToggle = MakeButton("Advanced: use an API token instead  \u25B8", primary: false);
            advancedToggle.HorizontalAlignment = HorizontalAlignment.Left;
            advancedToggle.Background = Brushes.Transparent;
            advancedToggle.BorderThickness = new Thickness(0);
            advancedToggle.Padding = new Thickness(0, 2, 0, 2);
            advancedToggle.Foreground = Brush("TextSecondary");
            advancedToggle.FontSize = 11;
            advancedToggle.Margin = new Thickness(0, 4, 0, 6);
            body.Children.Add(advancedToggle);

            var advanced = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4) };
            body.Children.Add(advanced);

            advanced.Children.Add(Para(
                $"1.  Open {host} and sign in.\n" +
                "2.  Open your team's Settings → API Tokens → Create token, then copy it here.",
                "TextSecondary", 12, new Thickness(0, 0, 0, 10)));

            // A team learned from an earlier connection lets us open the token page directly;
            // without one, the front page is the only address that is right for everybody.
            string browseUrl = existing?.TeamUrl is { Length: > 0 } team
                ? $"{baseUrl}/t/{Uri.EscapeDataString(team)}/settings/tokens"
                : baseUrl + "/";
            var browseBtn = MakeButton($"Open {host} in your browser", primary: false);
            browseBtn.HorizontalAlignment = HorizontalAlignment.Left;
            browseBtn.Margin = new Thickness(0, 0, 0, 12);
            advanced.Children.Add(browseBtn);

            advanced.Children.Add(Para("API token", "TextPrimary", 12, new Thickness(0, 0, 0, 4), semiBold: true));
            var tokenRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            tokenRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tokenRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tokenRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tokenBox = new PasswordBox
            {
                Background = Brush("BgPanel"),
                Foreground = Brush("TextPrimary"),
                BorderBrush = Brush("BorderDim"),
                BorderThickness = new Thickness(1),
                CaretBrush = Brush("TextPrimary"),
                Padding = new Thickness(6, 4, 6, 4),
                FontFamily = new FontFamily("Consolas"),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(tokenBox, "API token");
            var pasteBtn = MakeButton("Paste", primary: false);
            pasteBtn.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(pasteBtn, 1);
            var connectBtn = MakeButton("Connect", primary: false);
            connectBtn.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(connectBtn, 2);
            tokenRow.Children.Add(tokenBox);
            tokenRow.Children.Add(pasteBtn);
            tokenRow.Children.Add(connectBtn);
            advanced.Children.Add(tokenRow);

            advanced.Children.Add(Para(
                $"An API token gives TDPdf full access to your account on {host}. Set an expiry when you " +
                "create it. TDPdf keeps it encrypted, for your Windows account on this computer only.",
                "WarningOrange", 11, new Thickness(0, 0, 0, 4)));

            void SetAdvanced(bool open)
            {
                advanced.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                advancedToggle.Content = open ? "Advanced: use an API token instead  \u25BE" : "Advanced: use an API token instead  \u25B8";
            }
            advancedToggle.Click += (_, _) => SetAdvanced(advanced.Visibility != Visibility.Visible);

            // ── Buttons ────────────────────────────────────────────────────────────────────
            var buttons = new Grid { Margin = new Thickness(20, 8, 20, 16) };
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var disconnectBtn = MakeButton("Disconnect", primary: false);
            disconnectBtn.Visibility = existing is not null ? Visibility.Visible : Visibility.Collapsed;
            // Not IsCancel: in a ShowDialog window that would close the dialog on every click,
            // and while a sign-in is waiting this button stops the attempt instead. Esc is wired
            // below to do the same thing.
            var cancelBtn = MakeButton("Cancel", primary: false);
            Grid.SetColumn(disconnectBtn, 0);
            Grid.SetColumn(cancelBtn, 2);
            buttons.Children.Add(disconnectBtn);
            buttons.Children.Add(cancelBtn);
            root.Children.Add(buttons);

            void ShowStatus(string text, string brushKey)
            {
                status.Text = text;
                status.Foreground = Brush(brushKey);
                status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            }

            void SetBusy(bool on)
            {
                busy = on;
                signInBtn.IsEnabled = !on;
                connectBtn.IsEnabled = !on;
                pasteBtn.IsEnabled = !on;
                tokenBox.IsEnabled = !on;
                disconnectBtn.IsEnabled = !on;
                advancedToggle.IsEnabled = !on;
                // While a sign-in waits on the browser, Cancel stops the attempt rather than the dialog.
                cancelBtn.Content = on ? "Stop" : "Cancel";
            }

            // Validates a token from either path and keeps it only when the service accepts it.
            async Task<bool> ValidateAndStoreAsync(string token, DocumensoAccount? learned, CancellationToken ct)
            {
                ShowStatus($"Checking the sign-in with {host}…", "TextSecondary");
                var probed = await new DocumensoClient(baseUrl, token).ProbeAsync(ct);
                // Keep anything already learned about the account (e.g. the email after a send)
                // when the same team simply re-connects with a fresh token.
                var account = new DocumensoAccount(
                    learned?.Email ?? existing?.AccountEmail,
                    learned?.Name ?? existing?.AccountName,
                    learned?.TeamUrl ?? probed.TeamUrl ?? existing?.TeamUrl);
                if (!DocumensoCredentialStore.Save(baseUrl, token, account))
                {
                    ShowStatus("The sign-in was accepted, but TDPdf could not store it on this computer.", "DangerRed");
                    return false;
                }
                return true;
            }

            void Report(string method, string result)
            {
                try
                {
                    TDPdf.Diagnostics.Telemetry.TrackEvent("DocumensoConnect", new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["method"] = method,
                        ["outcome"] = result,
                    });
                }
                catch { /* telemetry never breaks a sign-in */ }
            }

            browseBtn.Click += (_, _) =>
            {
                if (!DocumensoUi.OpenInBrowser(browseUrl))
                    ShowStatus($"Could not open a browser. Go to {browseUrl} yourself.", "DangerRed");
            };

            pasteBtn.Click += (_, _) =>
            {
                try
                {
                    if (Clipboard.ContainsText())
                        tokenBox.Password = Clipboard.GetText().Trim();
                }
                catch
                {
                    // The clipboard is a shared resource another process may be holding open.
                    ShowStatus("The clipboard is busy. Try Paste again, or press Ctrl+V in the box.", "DangerRed");
                }
                tokenBox.Focus();
            };

            signInBtn.Click += async (_, _) =>
            {
                if (busy) return;
                SetBusy(true);
                cts?.Dispose();
                cts = new CancellationTokenSource();
                var ct = cts.Token;
                string result = "error";
                try
                {
                    ShowStatus($"Contacting {host}…", "TextSecondary");
                    bool supported = await new DocumensoClient(baseUrl, "").IsBrowserSignInSupportedAsync(ct);
                    if (!supported)
                    {
                        result = "unsupported";
                        ShowStatus($"{host} does not offer browser sign-in. Use an API token instead (below).", "WarningOrange");
                        SetAdvanced(true);
                        return;
                    }

                    var signIn = new DocumensoBrowserSignIn(baseUrl, new DocumensoHttpTokenExchange(baseUrl), DocumensoUi.OpenInBrowser);
                    var progress = new Progress<string>(text => { if (busy) ShowStatus(text + "  (Stop to cancel)", "TextSecondary"); });
                    var r = await signIn.RunAsync(progress, ct);
                    result = r.Outcome.ToString().ToLowerInvariant();

                    // The browser had the focus; bring the dialog back before saying anything.
                    try { if (win.IsVisible) win.Activate(); } catch { }

                    switch (r.Outcome)
                    {
                        case DocumensoSignInOutcome.Connected when r.Grant is { } grant:
                            if (!DocumensoClient.IsHeaderSafe(grant.Token)) { ShowStatus($"{host} returned a sign-in TDPdf cannot use. Nothing was stored.", "DangerRed"); break; }
                            if (await ValidateAndStoreAsync(grant.Token, new DocumensoAccount(grant.Email, grant.Name, grant.TeamUrl), ct))
                            {
                                outcome = Outcome.Connected;
                                win.Close();
                            }
                            break;
                        case DocumensoSignInOutcome.Cancelled:
                            ShowStatus("Sign-in stopped. Nothing was stored.", "TextSecondary");
                            break;
                        case DocumensoSignInOutcome.TimedOut:
                            ShowStatus("TDPdf stopped waiting for the browser. Choose Sign in to try again.", "WarningOrange");
                            break;
                        case DocumensoSignInOutcome.Denied:
                            ShowStatus("You didn't approve TDPdf in the browser, so nothing was connected.", "WarningOrange");
                            break;
                        case DocumensoSignInOutcome.StateMismatch:
                        case DocumensoSignInOutcome.Malformed:
                            ShowStatus("TDPdf received a sign-in response it did not ask for, and ignored it. Try again.", "DangerRed");
                            break;
                        case DocumensoSignInOutcome.ExchangeRejected:
                            ShowStatus($"{host} did not accept the sign-in{Suffix(r.Failure)}. Try again; nothing was stored.", "DangerRed");
                            break;
                        case DocumensoSignInOutcome.Unreachable:
                            ShowStatus($"Could not reach {host} to finish signing in. Check your connection and try again.", "DangerRed");
                            break;
                        case DocumensoSignInOutcome.BrowserFailed:
                            ShowStatus("TDPdf could not open your browser. Use an API token instead (below).", "DangerRed");
                            SetAdvanced(true);
                            break;
                        case DocumensoSignInOutcome.ListenerFailed:
                            ShowStatus("TDPdf could not receive the sign-in on this computer (a local port was refused). Use an API token instead (below).", "DangerRed");
                            SetAdvanced(true);
                            break;
                        default:
                            ShowStatus("Sign-in did not complete. Nothing was stored.", "DangerRed");
                            break;
                    }
                }
                catch (DocumensoApiException ex) when (ex.Failure == DocumensoFailure.AuthRejected)
                {
                    result = "rejected";
                    ShowStatus($"{host} did not accept the new sign-in. Nothing was stored.", "DangerRed");
                }
                catch (DocumensoApiException ex) when (ex.Failure is DocumensoFailure.Network or DocumensoFailure.Timeout)
                {
                    result = "network";
                    ShowStatus($"Could not reach {host}. Check your connection and try again. Nothing was stored.", "DangerRed");
                }
                catch (DocumensoApiException ex)
                {
                    ShowStatus($"{host} answered unexpectedly{Suffix(ex)}. Nothing was stored.", "DangerRed");
                }
                catch (OperationCanceledException)
                {
                    result = "cancelled";
                    if (win.IsVisible) ShowStatus("Sign-in stopped. Nothing was stored.", "TextSecondary");
                }
                finally
                {
                    Report("browser", result);
                    SetBusy(false);
                }
            };

            connectBtn.Click += async (_, _) =>
            {
                if (busy) return;
                string token = tokenBox.Password.Trim();
                if (token.Length == 0)
                {
                    ShowStatus("Paste a token first.", "DangerRed");
                    return;
                }
                if (!DocumensoClient.IsHeaderSafe(token))
                {
                    ShowStatus($"That token contains spaces or characters a token cannot have. Copy it again from {host}.", "DangerRed");
                    return;
                }

                SetBusy(true);
                cts?.Dispose();
                cts = new CancellationTokenSource();
                string result = "error";
                try
                {
                    if (await ValidateAndStoreAsync(token, null, cts.Token))
                    {
                        result = "connected";
                        outcome = Outcome.Connected;
                        win.Close();
                    }
                }
                catch (DocumensoApiException ex) when (ex.Failure == DocumensoFailure.AuthRejected)
                {
                    result = "rejected";
                    ShowStatus($"{host} rejected that token. Check that you copied all of it and that it has not expired.", "DangerRed");
                }
                catch (DocumensoApiException ex) when (ex.Failure is DocumensoFailure.Network or DocumensoFailure.Timeout)
                {
                    result = "network";
                    ShowStatus($"Could not reach {host}. Check your connection and try again. Nothing was stored.", "DangerRed");
                }
                catch (DocumensoApiException ex)
                {
                    ShowStatus($"{host} answered unexpectedly{Suffix(ex)}. Nothing was stored.", "DangerRed");
                }
                catch (OperationCanceledException)
                {
                    result = "cancelled";
                    // The window closed mid-check, or Stop was pressed; nothing was stored.
                    if (win.IsVisible) ShowStatus("Stopped. Nothing was stored.", "TextSecondary");
                }
                finally
                {
                    Report("token", result);
                    SetBusy(false);
                }
            };

            disconnectBtn.Click += (_, _) =>
            {
                var answer = TdpDialog.ShowYesNo(win,
                    $"Disconnect from {host}?\n\n" +
                    "This removes the sign-in from this computer. If you connected with an API token, it stays " +
                    $"valid on {host} until you delete it there, under Settings → API Tokens.",
                    "Disconnect", "Cancel", "Electronic Signature", MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
                if (DocumensoCredentialStore.Delete())
                {
                    outcome = Outcome.Disconnected;
                    win.Close();
                }
                else
                {
                    ShowStatus("TDPdf could not remove the stored sign-in. Close other TDPdf windows and try again.", "DangerRed");
                }
            };

            cancelBtn.Click += (_, _) =>
            {
                if (busy) { cts?.Cancel(); return; }
                win.Close();
            };
            win.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                if (busy) cts?.Cancel();
                else win.Close();
            };
            win.Closed += (_, _) => { cts?.Cancel(); };
            win.Loaded += (_, _) => signInBtn.Focus();
            win.ShowDialog();
            // cts is deliberately not disposed here: a sign-in cancelled by Closed may still be
            // unwinding through its token (and closing its loopback listener), and a
            // CancellationTokenSource needs no disposal when it has no CancelAfter timer.
            return outcome;
        }

        private static string Suffix(DocumensoApiException? ex)
            => ex?.StatusCode is int s ? $" (HTTP {s}{(ex.ErrorCode is null ? "" : " " + ex.ErrorCode)})" : "";

        private static TextBlock Para(string text, string brushKey, double size, Thickness margin, bool semiBold = false)
            => new()
            {
                Text = text,
                Foreground = Brush(brushKey),
                FontSize = size,
                TextWrapping = TextWrapping.Wrap,
                Margin = margin,
                FontWeight = semiBold ? FontWeights.SemiBold : FontWeights.Normal,
            };

        private static Button MakeButton(string text, bool primary)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(16, 6, 16, 6),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
            };
            if (Application.Current?.TryFindResource("DarkButton") is Style style) b.Style = style;
            if (primary)
            {
                b.Background = Brush("AccentGreenDim");
                b.Foreground = Brush("AccentGreen");
                b.BorderBrush = Brush("AccentGreen");
                b.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                b.Background = Brush("BgHover");
                b.Foreground = Brush("TextPrimary");
                b.BorderBrush = Brush("BorderDim");
            }
            return b;
        }

        // TdpDialog.Brush, copied: a missing theme key must leave the dialog legible, so every
        // lookup falls back to a system colour instead of throwing the way FindResource would.
        private static SolidColorBrush Brush(string key)
            => Application.Current?.TryFindResource(key) as SolidColorBrush ?? SystemBrush(key);

        private static SolidColorBrush SystemBrush(string key) => key switch
        {
            "AccentGreen" or "AccentGreenDim" or "DangerRed" or "WarningOrange" => SystemColors.HighlightBrush,
            "BgDark" or "BgPanel" => SystemColors.WindowBrush,
            "BgHover" => SystemColors.ControlBrush,
            "TextSecondary" or "TextMuted" => SystemColors.GrayTextBrush,
            _ => SystemColors.WindowTextBrush,
        };
    }
}
