using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TDPdf.Services
{
    /// <summary>
    /// The themed Connect / Disconnect dialog for Electronic Signature: paste a Documenso API
    /// token, have it checked against the instance, and only then keep it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built in code with the custom-chrome recipe every TDPdf dialog uses (borderless, rounded
    /// accent border, draggable Consolas title bar, MDL2 close glyph — see
    /// <c>MainWindow.OpenSignatureCreator</c> and <c>TdpDialog.CreateShell</c>). Not
    /// <c>TdpDialog.PromptPassword</c>: that one is shaped around an encrypted file's name and
    /// has no room for the browser hand-off or the inline errors this needs.
    /// </para>
    /// <para>
    /// <b>Why a paste box and not a sign-in.</b> Documenso has no OAuth for its API; tokens are
    /// made only in its web UI, and since v2 they belong to a <em>team</em> whose URL is random
    /// (<c>/t/{teamUrl}/settings/tokens</c>). So the dialog opens the instance's front page and
    /// says where to click — or, when an earlier connection told us the team, the token page
    /// itself.
    /// </para>
    /// <para>
    /// <b>Never stores an unvalidated token.</b> Connect probes the instance first. Rejected →
    /// inline red text, nothing stored. Unreachable → inline red text, nothing stored; there is
    /// deliberately no "save anyway".
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

            var win = new Window
            {
                Title = "Electronic Signature",
                Width = 460,
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
                Text = "Connect Electronic Signature",
                Foreground = Brush("AccentGreen"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var closeBtn = new Button
            {
                Content = "",
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

            // ── Body ───────────────────────────────────────────────────────────────────────
            var body = new StackPanel { Margin = new Thickness(20, 16, 20, 8) };
            root.Children.Add(body);

            body.Children.Add(Para(
                $"TDPdf sends documents for electronic signature through the signing service at {host}. " +
                "Connect once with an API token from your account there.",
                "TextPrimary", 13, new Thickness(0, 0, 0, 10)));

            body.Children.Add(Para(
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
            browseBtn.Margin = new Thickness(0, 0, 0, 14);
            body.Children.Add(browseBtn);

            body.Children.Add(Para("API token", "TextPrimary", 12, new Thickness(0, 0, 0, 4), semiBold: true));
            var tokenRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            tokenRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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
            tokenRow.Children.Add(tokenBox);
            tokenRow.Children.Add(pasteBtn);
            body.Children.Add(tokenRow);

            body.Children.Add(Para(
                $"This token gives TDPdf full access to your account on {host}. Set an expiry when you " +
                "create it. TDPdf keeps it encrypted, for your Windows account on this computer only.",
                "WarningOrange", 11, new Thickness(0, 0, 0, 8)));

            if (existing is not null)
            {
                string who = existing.AccountEmail is { Length: > 0 } email ? $" as {email}" : "";
                body.Children.Add(Para(
                    $"Already connected to {host}{who}. Paste a new token to replace it, or disconnect.",
                    "TextSecondary", 12, new Thickness(0, 0, 0, 8)));
            }

            var status = Para("", "TextSecondary", 12, new Thickness(0, 2, 0, 4));
            status.Visibility = Visibility.Collapsed;
            body.Children.Add(status);

            // ── Buttons ────────────────────────────────────────────────────────────────────
            var buttons = new Grid { Margin = new Thickness(20, 8, 20, 16) };
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var disconnectBtn = MakeButton("Disconnect", primary: false);
            disconnectBtn.Visibility = existing is not null ? Visibility.Visible : Visibility.Collapsed;
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            var connectBtn = MakeButton("Connect", primary: true);
            connectBtn.IsDefault = true;
            var cancelBtn = MakeButton("Cancel", primary: false);
            cancelBtn.IsCancel = true;
            cancelBtn.Margin = new Thickness(8, 0, 0, 0);
            right.Children.Add(connectBtn);
            right.Children.Add(cancelBtn);
            Grid.SetColumn(disconnectBtn, 0);
            Grid.SetColumn(right, 2);
            buttons.Children.Add(disconnectBtn);
            buttons.Children.Add(right);
            root.Children.Add(buttons);

            void ShowStatus(string text, string brushKey)
            {
                status.Text = text;
                status.Foreground = Brush(brushKey);
                status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            }

            void SetBusy(bool busy)
            {
                connectBtn.IsEnabled = !busy;
                pasteBtn.IsEnabled = !busy;
                tokenBox.IsEnabled = !busy;
                disconnectBtn.IsEnabled = !busy;
                win.Cursor = busy ? Cursors.Wait : null;
            }

            browseBtn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(browseUrl) { UseShellExecute = true });
                }
                catch
                {
                    ShowStatus($"Could not open a browser. Go to {browseUrl} yourself.", "DangerRed");
                }
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

            connectBtn.Click += async (_, _) =>
            {
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
                ShowStatus($"Checking the token with {host}…", "TextSecondary");
                cts?.Dispose();
                cts = new CancellationTokenSource();
                try
                {
                    var account = await new DocumensoClient(baseUrl, token).ProbeAsync(cts.Token);
                    // Keep anything already learned about the account (e.g. the email after a
                    // send) when the same team simply re-connects with a fresh token.
                    if (existing is not null)
                        account = new DocumensoAccount(existing.AccountEmail, existing.AccountName,
                            account.TeamUrl ?? existing.TeamUrl);
                    if (!DocumensoCredentialStore.Save(baseUrl, token, account))
                    {
                        ShowStatus("The token was accepted, but TDPdf could not store it on this computer.", "DangerRed");
                        return;
                    }
                    outcome = Outcome.Connected;
                    win.Close();
                }
                catch (DocumensoApiException ex) when (ex.Failure == DocumensoFailure.AuthRejected)
                {
                    ShowStatus($"{host} rejected that token. Check that you copied all of it and that it has not expired.", "DangerRed");
                }
                catch (DocumensoApiException ex) when (ex.Failure is DocumensoFailure.Network or DocumensoFailure.Timeout)
                {
                    ShowStatus($"Could not reach {host}. Check your connection and try again. Nothing was stored.", "DangerRed");
                }
                catch (DocumensoApiException ex)
                {
                    string what = ex.StatusCode is int s ? $"HTTP {s}" : ex.Failure.ToString();
                    ShowStatus($"{host} answered unexpectedly ({what}). Nothing was stored.", "DangerRed");
                }
                catch (OperationCanceledException)
                {
                    // The window closed mid-check; nothing to report and nothing was stored.
                }
                finally
                {
                    SetBusy(false);
                }
            };

            disconnectBtn.Click += (_, _) =>
            {
                var answer = TdpDialog.ShowYesNo(win,
                    $"Disconnect from {host}?\n\n" +
                    "This removes the token from this computer. It stays valid on " + host +
                    " until you delete it there, under Settings → API Tokens.",
                    "Disconnect", "Cancel", "Electronic Signature", MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
                if (DocumensoCredentialStore.Delete())
                {
                    outcome = Outcome.Disconnected;
                    win.Close();
                }
                else
                {
                    ShowStatus("TDPdf could not remove the stored token. Close other TDPdf windows and try again.", "DangerRed");
                }
            };

            cancelBtn.Click += (_, _) => win.Close();
            win.Closed += (_, _) => { cts?.Cancel(); };
            win.Loaded += (_, _) => tokenBox.Focus();
            win.ShowDialog();
            // cts is deliberately not disposed here: a probe cancelled by Closed may still be
            // unwinding through its token, and a CancellationTokenSource needs no disposal when
            // it has no CancelAfter timer.
            return outcome;
        }

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
