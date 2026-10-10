// MainWindow — Electronic Signature (Documenso).
// File ▸ Electronic Signature menu handlers and enablement, the Settings section, and the
// connect-on-demand path every action goes through. Design: docs/design/esignature-documenso.md.
//
// Named ESignature, not Signing, on purpose: MainWindow.Signatures.cs is the OTHER feature — the
// Signature Picture image overlay — and the two must stay impossible to confuse, for users and
// for whoever opens this directory next. Every identifier here is prefixed ESign/Documenso.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow
    {
        // ============================================================
        // Menu
        // ============================================================

        /// <summary>
        /// Called from <c>FileMenu_SubmenuOpened</c>. Absent, not disabled, when the feature does
        /// not exist on this device; <see cref="DocumensoConfig.HasEndpoint"/> is the only gate and
        /// does no network or file I/O, so this is cheap on every open.
        /// </summary>
        private void UpdateESignatureMenu()
        {
            bool configured = DocumensoConfig.HasEndpoint();
            _eSignatureMenuItem.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
            if (!configured) return;

            bool hasDoc = _doc is not null && _currentFile is not null;
            _eSignSendMenuItem.IsEnabled = hasDoc;
            _eSignSelfMenuItem.IsEnabled = hasDoc;
            _eSignVerifyMenuItem.IsEnabled = hasDoc;
            _eSignRequestsMenuItem.IsEnabled = true;   // useful with no document open
        }

        private async void ESignSend_Click(object sender, RoutedEventArgs e) => await RunESignAsync(ESignSendAsync);
        private async void ESignSelf_Click(object sender, RoutedEventArgs e) => await RunESignAsync(ESignSelfAsync);
        private async void ESignRequests_Click(object sender, RoutedEventArgs e) => await RunESignAsync(ESignRequestsAsync);
        private async void ESignVerify_Click(object sender, RoutedEventArgs e) => await RunESignAsync(ESignVerifyAsync);

        /// <summary>
        /// The one place an Electronic Signature action can fail into. A click handler is
        /// <c>async void</c>, where an escaping exception takes the whole app down; a signing
        /// service being down is never worth that, and never worth a modal either (design §5.3) —
        /// it is a status line. The message is fixed text: exception text could carry a host or
        /// an envelope id, neither of which belongs on screen unasked.
        /// </summary>
        private async Task RunESignAsync(System.Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (System.OperationCanceledException)
            {
                SetStatus("Electronic Signature: cancelled.");
            }
            catch (DocumensoApiException ex)
            {
                SetStatus(ESignDescribeFailure(ex));
            }
            catch (System.Exception)
            {
                SetStatus("Electronic Signature: something went wrong. Nothing was sent.");
            }
        }

        /// <summary>One status-bar sentence per coarse failure. Never the response body.</summary>
        private static string ESignDescribeFailure(DocumensoApiException ex) => ex.Failure switch
        {
            DocumensoFailure.AuthRejected => "Your Electronic Signature sign-in has expired or was revoked. Connect again to continue.",
            DocumensoFailure.Network or DocumensoFailure.Timeout => "Could not reach the signing service. Check your connection and try again.",
            DocumensoFailure.RateLimited => "The signing service is busy. Wait a minute and try again.",
            DocumensoFailure.NotFound => "The signing service no longer has that document.",
            _ => "The signing service answered unexpectedly. Nothing was changed.",
        };

        // Phase 2 — Send for Signature: DocumensoSendWindow (recipients, {{…}} placeholders and
        // drag-to-place fields), then create → recipients → fields → distribute with rollback.
        private Task ESignSendAsync()
        {
            if (!ESignRequireDocument()) return Task.CompletedTask;
            if (ESignConnectedClient() is not { } client) return Task.CompletedTask;
            SetStatus($"Electronic Signature: connected to {DocumensoConfig.HostOf(client.BaseUrl)}.");
            return Task.CompletedTask;
        }

        // Phase 4 — Sign This Document: one recipient (you), distribute, open the signing link
        // in the browser, then Refresh → download the signed copy.
        private Task ESignSelfAsync()
        {
            if (!ESignRequireDocument()) return Task.CompletedTask;
            if (ESignConnectedClient() is not { } client) return Task.CompletedTask;
            SetStatus($"Electronic Signature: connected to {DocumensoConfig.HostOf(client.BaseUrl)}.");
            return Task.CompletedTask;
        }

        // Phase 3 — Signature Requests: DocumensoRequestsWindow (list + get-many refresh,
        // download, certificate, cancel / delete behind a confirm).
        private Task ESignRequestsAsync()
        {
            if (ESignConnectedClient() is not { } client) return Task.CompletedTask;
            SetStatus($"Electronic Signature: connected to {DocumensoConfig.HostOf(client.BaseUrl)}.");
            return Task.CompletedTask;
        }

        // Phase 5 — Verify Signatures: informational only (observed, never "valid"), plus the
        // certificate of completion and audit log for envelopes we can match.
        private Task ESignVerifyAsync()
        {
            if (!ESignRequireDocument()) return Task.CompletedTask;
            if (ESignConnectedClient() is not { } client) return Task.CompletedTask;
            SetStatus($"Electronic Signature: connected to {DocumensoConfig.HostOf(client.BaseUrl)}.");
            return Task.CompletedTask;
        }

        private bool ESignRequireDocument()
        {
            if (_doc is not null && _currentFile is not null) return true;
            SetStatus("Open a PDF first.");
            return false;
        }

        /// <summary>
        /// A client for the configured instance, connecting first when there is no usable stored
        /// token — absent, issued for a different instance, or undecryptable all read as Not
        /// connected and route here, never to an error. <c>null</c> means the user did not connect.
        /// </summary>
        private DocumensoClient? ESignConnectedClient()
        {
            if (DocumensoConfig.TryResolveBaseUrl() is not { } resolved)
            {
                SetStatus("Electronic Signature is not available on this device.");
                return null;
            }

            string? token = DocumensoCredentialStore.TryLoadToken(resolved.BaseUrl);
            if (token is null)
            {
                if (DocumensoConnectWindow.Show(this, resolved.BaseUrl) != DocumensoConnectWindow.Outcome.Connected)
                {
                    SetStatus("Electronic Signature: not connected.");
                    return null;
                }
                token = DocumensoCredentialStore.TryLoadToken(resolved.BaseUrl);
                if (token is null)
                {
                    SetStatus("Electronic Signature: the sign-in could not be read back on this computer.");
                    return null;
                }
                SetStatus($"Connected to {DocumensoConfig.HostOf(resolved.BaseUrl)}.");
            }
            return new DocumensoClient(resolved.BaseUrl, token);
        }

        // ============================================================
        // Settings section
        // ============================================================

        /// <summary>
        /// The "Electronic signature" section of Settings (design §3.4): which service, whether
        /// you are connected to it, and a Test connection that says inline what is wrong. Built
        /// into its own panel so Connect / Disconnect / an address change can rebuild just this
        /// section in place.
        /// </summary>
        /// <remarks>
        /// This section is also the consent surface. Sending is always an explicit, per-document
        /// action, so a standing "allow sending" checkbox would be noise; what the user needs is
        /// to see where documents go and to be able to change or disconnect it — which is this.
        /// </remarks>
        private void AddESignatureSettingsSection(StackPanel panel)
        {
            var section = new StackPanel();
            panel.Children.Add(section);
            BuildESignatureSettings(section);
        }

        private void BuildESignatureSettings(StackPanel section)
        {
            section.Children.Clear();
            section.Children.Add(new TextBlock
            {
                Text = "Electronic signature",
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushResource("TextPrimary"),
                Margin = new Thickness(0, 0, 0, 8)
            });

            if (!DocumensoConfig.IsEnabledByPolicy())
            {
                section.Children.Add(ESignSettingsNote("Electronic signature has been turned off by your organisation."));
                return;
            }

            var resolved = DocumensoConfig.TryResolveBaseUrl();
            bool fromEnvironment = resolved?.From == DocumensoConfig.Source.Environment;
            bool locked = !DocumensoConfig.IsUserOverrideAllowed();
            bool readOnly = fromEnvironment || locked;

            string userUrl = "";
            try { userUrl = TDPdf.Properties.Settings.Default.DocumensoBaseUrl ?? ""; } catch { }

            var urlBox = new TextBox
            {
                Text = resolved?.BaseUrl ?? userUrl,
                IsReadOnly = readOnly,
                Background = BrushResource("BgDark"),
                Foreground = BrushResource(readOnly ? "TextSecondary" : "TextPrimary"),
                BorderBrush = BrushResource("BorderDim"),
                CaretBrush = BrushResource("TextPrimary"),
                Padding = new Thickness(4, 3, 4, 3),
                Margin = new Thickness(0, 0, 0, 4),
                ToolTip = "The Documenso signing service TDPdf sends documents to."
            };
            AutomationProperties.SetName(urlBox, "Signing service address");
            section.Children.Add(urlBox);

            string sourceNote = resolved?.From switch
            {
                DocumensoConfig.Source.Environment => "Set by the TDPDF_DOCUMENSO_BASEURL environment variable.",
                DocumensoConfig.Source.Policy when locked => "Managed by your organisation.",
                DocumensoConfig.Source.Policy => "Set by your organisation. You can change it.",
                DocumensoConfig.Source.User => "Your own setting. Clear it to go back to the default.",
                DocumensoConfig.Source.Default => "The Doodle Project's signing service. Enter your organisation's Documenso address to use that instead.",
                _ => "The signing service address configured on this device is not valid, so electronic signature is unavailable."
            };
            section.Children.Add(ESignSettingsNote(sourceNote));

            var urlError = ESignSettingsNote("");
            urlError.Foreground = BrushResource("DangerRed");
            urlError.Visibility = Visibility.Collapsed;
            section.Children.Add(urlError);

            if (!readOnly)
            {
                void Commit()
                {
                    string raw = urlBox.Text.Trim();
                    if (resolved is { } r && raw == r.BaseUrl) { urlError.Visibility = Visibility.Collapsed; return; }

                    string stored;
                    if (raw.Length == 0)
                    {
                        stored = "";
                    }
                    else if (DocumensoConfig.Normalise(raw) is { } normalised)
                    {
                        // Typing the address the device would use anyway stores nothing, so the
                        // user keeps following the organisation's (or the default) address if it
                        // ever moves, instead of pinning today's copy of it.
                        string fallback = DocumensoConfig.Normalise(DocumensoConfig.PolicyBaseUrl()) ?? DocumensoConfig.DefaultBaseUrl;
                        stored = normalised == fallback ? "" : normalised;
                    }
                    else
                    {
                        urlError.Text = "Enter a full https:// address, for example https://sign.example.com.";
                        urlError.Visibility = Visibility.Visible;
                        return;
                    }

                    try
                    {
                        TDPdf.Properties.Settings.Default.DocumensoBaseUrl = stored;
                        TDPdf.Properties.Settings.Default.Save();
                    }
                    catch
                    {
                        urlError.Text = "TDPdf could not save that setting.";
                        urlError.Visibility = Visibility.Visible;
                        return;
                    }

                    if (DocumensoConfig.TryResolveBaseUrl() is { } now)
                        SetStatus($"Electronic signature will use {DocumensoConfig.HostOf(now.BaseUrl)}.");
                    // Rebuild after the focus change that triggered this has finished.
                    Dispatcher.InvokeAsync(() => BuildESignatureSettings(section));
                }

                urlBox.LostKeyboardFocus += (_, _) => Commit();
                urlBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Commit(); } };
            }

            if (resolved is not { } live) return;
            string host = DocumensoConfig.HostOf(live.BaseUrl);

            // Connection state from metadata only — no decryption just to draw Settings.
            var meta = DocumensoCredentialStore.ReadMetadataFor(live.BaseUrl);
            string state;
            if (meta is not null)
                state = meta.AccountEmail is { Length: > 0 } email
                    ? $"Connected to {host} as {email}."
                    : $"Connected to {host}.";
            else if (DocumensoCredentialStore.ReadMetadata() is { } other)
                state = $"Not connected. The saved sign-in is for {DocumensoConfig.HostOf(other.BaseUrl)}.";
            else
                state = "Not connected.";
            var stateText = ESignSettingsNote(state);
            stateText.Margin = new Thickness(0, 6, 0, 6);
            section.Children.Add(stateText);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var connectBtn = new Button
            {
                Content = meta is null ? "Connect…" : "Disconnect…",
                Style = (Style)FindResource("DarkButton"),
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0)
            };
            var testBtn = new Button
            {
                Content = "Test connection",
                Style = (Style)FindResource("DarkButton"),
                Padding = new Thickness(12, 4, 12, 4)
            };
            row.Children.Add(connectBtn);
            row.Children.Add(testBtn);
            section.Children.Add(row);

            var results = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            section.Children.Add(results);

            connectBtn.Click += (_, _) =>
            {
                Window? owner = Window.GetWindow(section);
                if (meta is null)
                {
                    if (DocumensoConnectWindow.Show(owner, live.BaseUrl) == DocumensoConnectWindow.Outcome.Connected)
                        SetStatus($"Connected to {host}.");
                }
                else
                {
                    var answer = TdpDialog.ShowYesNo(owner,
                        $"Disconnect from {host}?\n\n" +
                        $"This removes the token from this computer. It stays valid on {host} until you " +
                        "delete it there, under Settings → API Tokens.",
                        "Disconnect", "Cancel", "Electronic Signature", MessageBoxImage.Question);
                    if (answer == MessageBoxResult.Yes)
                        SetStatus(DocumensoCredentialStore.Delete()
                            ? $"Disconnected from {host}."
                            : "TDPdf could not remove the stored token.");
                }
                BuildESignatureSettings(section);
            };

            testBtn.Click += async (_, _) =>
            {
                testBtn.IsEnabled = false;
                connectBtn.IsEnabled = false;
                results.Children.Clear();
                results.Children.Add(ESignSettingsNote($"Checking {host}…"));
                try
                {
                    var lines = await ESignTestConnectionAsync(live.BaseUrl, CancellationToken.None);
                    results.Children.Clear();
                    foreach (var (text, brushKey) in lines)
                    {
                        var line = ESignSettingsNote(text);
                        line.Foreground = BrushResource(brushKey);
                        results.Children.Add(line);
                    }
                }
                finally
                {
                    testBtn.IsEnabled = true;
                    connectBtn.IsEnabled = true;
                }
            };
        }

        /// <summary>
        /// Test connection (design §3.6): service health, sealing certificate, and the stored
        /// sign-in, as three plain lines. This is the only place a reachability failure is
        /// reported, and it is reported inline — never as a popup. Each probe is independent so
        /// one failing does not hide what the others found, except that an unreachable service
        /// skips the rest rather than waiting out two more timeouts to say the same thing.
        /// </summary>
        private static async Task<List<(string Text, string BrushKey)>> ESignTestConnectionAsync(string baseUrl, CancellationToken ct)
        {
            string host = DocumensoConfig.HostOf(baseUrl);
            var lines = new List<(string, string)>();
            var anonymous = new DocumensoClient(baseUrl, "");

            try
            {
                var health = await anonymous.GetHealthAsync(ct);
                lines.Add(health.IsOk
                    ? ($"Service: {host} is up.", "AccentGreen")
                    : ($"Service: {host} is reachable but reports a problem.", "WarningOrange"));
            }
            catch (DocumensoApiException ex) when (ex.Failure is DocumensoFailure.Network or DocumensoFailure.Timeout)
            {
                lines.Add(($"Service: could not reach {host}.", "DangerRed"));
                return lines;
            }
            catch (DocumensoApiException ex)
            {
                lines.Add(($"Service: {host} answered unexpectedly{ESignStatusSuffix(ex)}.", "DangerRed"));
            }

            try
            {
                var cert = await anonymous.GetCertificateStatusAsync(ct);
                lines.Add(cert.IsAvailable
                    ? ("Sealing: ready — completed documents are digitally sealed.", "AccentGreen")
                    : ("Sealing: the service is reachable but cannot yet seal documents — signing requests will not complete.", "WarningOrange"));
            }
            catch (DocumensoApiException ex)
            {
                lines.Add(($"Sealing: could not be checked{ESignStatusSuffix(ex)}.", "DangerRed"));
            }

            string? token = DocumensoCredentialStore.TryLoadToken(baseUrl);
            if (token is null)
            {
                lines.Add(("Your sign-in: not connected — connect to check it.", "TextSecondary"));
                return lines;
            }
            try
            {
                var account = await new DocumensoClient(baseUrl, token).ProbeAsync(ct);
                if (account.TeamUrl is not null) DocumensoCredentialStore.RememberAccount(baseUrl, account);
                lines.Add(("Your sign-in: accepted.", "AccentGreen"));
            }
            catch (DocumensoApiException ex) when (ex.Failure == DocumensoFailure.AuthRejected)
            {
                lines.Add(($"Your sign-in: {host} rejected it — it may have expired. Connect again to continue.", "DangerRed"));
            }
            catch (DocumensoApiException ex)
            {
                lines.Add(($"Your sign-in: could not be checked{ESignStatusSuffix(ex)}.", "DangerRed"));
            }
            return lines;
        }

        private static string ESignStatusSuffix(DocumensoApiException ex)
            => ex.StatusCode is int s ? $" (HTTP {s})" : ex.Failure == DocumensoFailure.Timeout ? " (timed out)" : "";

        private TextBlock ESignSettingsNote(string text) => new()
        {
            Text = text,
            Foreground = BrushResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4)
        };
    }
}
