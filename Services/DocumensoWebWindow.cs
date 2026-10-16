using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using static TDPdf.Services.DocumensoUi;

namespace TDPdf.Services
{
    /// <summary>
    /// The signing service's own pages, inside TDPdf: a recipient's signing page
    /// (<c>/sign/{token}</c>), a request's page (<c>/t/{team}/documents/{id}</c>) and its audit log
    /// (<c>…/logs</c>, which also offers the certificate). Modeless and single instance, like
    /// Signature Requests: opening another page reuses the open window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Top-level navigation, not the embed.</b> Documenso's iframe embed is Enterprise-licensed;
    /// this is a browser control visiting the service's normal pages, nothing more. No Documenso
    /// code runs in TDPdf and nothing is injected into the page — no host objects, no web messages,
    /// no scripts.
    /// </para>
    /// <para>
    /// <b>Fenced to the service.</b> Every navigation, including pop-ups
    /// (<c>NewWindowRequested</c>), goes through <see cref="DocumensoWebNavigation.Decide"/>: the
    /// configured service loads here, other web addresses open in the default browser, and
    /// anything that is not web is refused.
    /// </para>
    /// <para>
    /// <b>A persistent per-user profile</b> at <see cref="DocumensoWebNavigation.UserDataFolder"/>, so
    /// a web sign-in to the service (needed for the owner pages, not for signing links) survives a
    /// restart. It is a browser profile — cookies and site storage for the service — and nothing
    /// else; the API token is not in it.
    /// </para>
    /// <para>
    /// <b>Completion</b> is noticed two ways: Documenso's own "signed" page (<c>/sign/{token}/complete</c>)
    /// prompts an immediate status check, and while the window is open with a request in it the
    /// request's status is asked for every <see cref="DocumensoWebNavigation.StatusPollInterval"/>.
    /// Only a COMPLETED envelope (sealed by the service) offers <b>Download signed copy</b>, through the
    /// same downloader Signature Requests uses. Polling stops when the window closes.
    /// </para>
    /// <para>
    /// <b>Not transparent.</b> Every other TDPdf dialog is a layered window (<c>AllowsTransparency</c>),
    /// which an HWND-hosted control such as WebView2 cannot draw into. This one keeps the same dark
    /// chrome — title bar, accent border, MDL2 close — on an opaque, resizable borderless window
    /// (<see cref="WindowChrome"/>), so it looks like the rest and the page actually renders.
    /// </para>
    /// </remarks>
    internal static class DocumensoWebWindow
    {
        internal sealed class Inputs
        {
            public string BaseUrl { get; init; } = "";
            public string Host { get; init; } = "";
            public DocumensoWebRequest Request { get; init; } = new("about:blank", "", DocumensoWebPurpose.Document, null);
            /// <summary>GET /envelope/{id}. May throw <see cref="DocumensoApiException"/>. Null: no status polling.</summary>
            public Func<string, CancellationToken, Task<DocumensoEnvelope>>? GetEnvelopeAsync { get; init; }
            /// <summary>Save dialog + download of the signed copy: the saved path (null if cancelled) and a sentence. May throw.</summary>
            public Func<Window, DocumensoEnvelope, Task<(string? Path, string Message)>>? DownloadSignedAsync { get; init; }
            /// <summary>Opens a saved file in a new TDPdf tab.</summary>
            public Func<string, Task>? OpenAsync { get; init; }
            /// <summary>
            /// The browser control could not start (runtime removed, profile folder unwritable).
            /// Called with the page's address; the caller opens it in the default browser and says
            /// why in the status bar. The window has already closed.
            /// </summary>
            public Action<string>? Fallback { get; init; }
            /// <summary>Called once when the request is seen completed (e.g. so Sign This Document can refresh).</summary>
            public Action? Completed { get; init; }
        }

        private static Window? _open;
        private static Action<Inputs>? _reuse;

        /// <summary>Opens the window on <paramref name="inputs"/>, or points the open one at it.</summary>
        internal static void Show(Window? owner, Inputs inputs)
        {
            if (_open is { } existing && _reuse is { } reuse)
            {
                reuse(inputs);
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
                return;
            }
            Build(owner, inputs);
        }

        private static void Build(Window? owner, Inputs first)
        {
            var area = SystemParameters.WorkArea;
            var win = new Window
            {
                Title = string.IsNullOrWhiteSpace(first.Request.Title) ? "TDPdf Signing" : first.Request.Title,
                Width = Math.Min(1040, area.Width * 0.9),
                Height = Math.Min(820, area.Height * 0.9),
                MinWidth = 520,
                MinHeight = 420,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = false,
                ResizeMode = ResizeMode.CanResize,
                Background = Brush("BgDark"),
                Owner = owner,
                WindowStartupLocation = owner is not null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
            };
            WindowChrome.SetWindowChrome(win, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });

            var outer = new Border { BorderBrush = Brush("AccentGreenDim"), BorderThickness = new Thickness(1), Background = Brush("BgDark") };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // title bar
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // toolbar
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // page
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // status bar
            outer.Child = grid;
            win.Content = outer;

            // ── Title bar (the CreateShell recipe, opaque) ──────────────────────────────────
            var titleBar = new Border { Background = Brush("BgPanel"), Padding = new Thickness(14, 6, 6, 6) };
            titleBar.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) { win.WindowState = win.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; return; }
                if (e.ButtonState == MouseButtonState.Pressed) { try { win.DragMove(); } catch (InvalidOperationException) { } }
            };
            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleText = new TextBlock
            {
                Foreground = Brush("AccentGreen"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var maxBtn = TitleGlyph("", "Maximize");
            var closeBtn = TitleGlyph("", "Close");
            closeBtn.MouseEnter += (_, _) => closeBtn.Foreground = Brush("DangerRed");
            closeBtn.MouseLeave += (_, _) => closeBtn.Foreground = Brush("TextSecondary");
            Grid.SetColumn(maxBtn, 1);
            Grid.SetColumn(closeBtn, 2);
            tg.Children.Add(titleText);
            tg.Children.Add(maxBtn);
            tg.Children.Add(closeBtn);
            titleBar.Child = tg;
            grid.Children.Add(titleBar);

            // ── Toolbar: Back · Reload · address · Open in browser ─────────────────────────
            var bar = new Grid { Margin = new Thickness(10, 6, 10, 6) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var backBtn = GlyphButton(owner, "", "Back");
            var reloadBtn = GlyphButton(owner, "", "Reload");
            reloadBtn.Margin = new Thickness(6, 0, 0, 0);
            var address = Para("", "TextSecondary", 11, new Thickness(10, 0, 10, 0));
            address.VerticalAlignment = VerticalAlignment.Center;
            address.TextWrapping = TextWrapping.NoWrap;
            address.TextTrimming = TextTrimming.CharacterEllipsis;
            address.FontFamily = new FontFamily("Consolas");
            var externalBtn = SmallButton(owner, "Open in browser", "Open this page in your browser");
            externalBtn.Margin = new Thickness(0);
            Grid.SetColumn(reloadBtn, 1);
            Grid.SetColumn(address, 2);
            Grid.SetColumn(externalBtn, 3);
            bar.Children.Add(backBtn);
            bar.Children.Add(reloadBtn);
            bar.Children.Add(address);
            bar.Children.Add(externalBtn);
            Grid.SetRow(bar, 1);
            grid.Children.Add(bar);

            // ── The page ────────────────────────────────────────────────────────────────────
            var pageHost = new Grid { Background = Brush("BgDark") };
            var loading = Para("Loading…", "TextSecondary", 12, new Thickness(0));
            loading.HorizontalAlignment = HorizontalAlignment.Center;
            loading.VerticalAlignment = VerticalAlignment.Center;
            pageHost.Children.Add(loading);
            var web = new WebView2 { Visibility = Visibility.Hidden, DefaultBackgroundColor = ToDrawing(Brush("BgDark").Color) };
            pageHost.Children.Add(web);
            Grid.SetRow(pageHost, 2);
            grid.Children.Add(pageHost);

            // ── Status bar: what happened + Download signed copy / Open it ────────────────
            var foot = new Border { Background = Brush("BgPanel"), Padding = new Thickness(12, 8, 12, 8), BorderBrush = Brush("BorderDim"), BorderThickness = new Thickness(0, 1, 0, 0) };
            var fg = new Grid();
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var status = Para("", "TextSecondary", 12, new Thickness(0, 0, 10, 0));
            status.VerticalAlignment = VerticalAlignment.Center;
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var downloadBtn = MakeButton(owner, "Download signed copy…", primary: true);
            var openBtn = MakeButton(owner, "Open it", primary: false);
            openBtn.Margin = new Thickness(8, 0, 0, 0);
            openBtn.ToolTip = "Opens the downloaded signed copy in a new tab";
            actions.Children.Add(downloadBtn);
            actions.Children.Add(openBtn);
            Grid.SetColumn(actions, 1);
            fg.Children.Add(status);
            fg.Children.Add(actions);
            foot.Child = fg;
            Grid.SetRow(foot, 3);
            grid.Children.Add(foot);

            // ── State ───────────────────────────────────────────────────────────────────────
            var inputs = first;
            var cts = new CancellationTokenSource();
            var poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = DocumensoWebNavigation.StatusPollInterval };
            DocumensoEnvelope? completed = null;
            string? savedPath = null;
            bool checking = false, downloading = false, notified = false, closed = false, ready = false;
            DocumensoWebPage lastPage = DocumensoWebPage.Other;

            void Say(string text, string brushKey = "TextSecondary")
            {
                status.Text = text;
                status.Foreground = Brush(brushKey);
            }

            void Reset(Inputs next)
            {
                inputs = next;
                completed = null;
                savedPath = null;
                notified = false;
                lastPage = DocumensoWebPage.Other;
                titleText.Text = string.IsNullOrWhiteSpace(next.Request.Title) ? $"TDPdf Signing — {next.Host}" : next.Request.Title;
                win.Title = titleText.Text;
                downloadBtn.Visibility = Visibility.Collapsed;
                openBtn.Visibility = Visibility.Collapsed;
                Say(next.Request.Purpose switch
                {
                    DocumensoWebPurpose.Sign => $"Signing happens on {next.Host}, shown here. TDPdf does not sign anything itself.",
                    DocumensoWebPurpose.AuditLog => "The audit log and certificate of completion, from the signing service.",
                    _ => $"This request on {next.Host}.",
                });
                poll.Stop();
                if (next.Request.EnvelopeId is not null && next.GetEnvelopeAsync is not null) poll.Start();
            }

            async Task CheckStatusAsync(bool fromCompletePage)
            {
                var id = inputs.Request.EnvelopeId;
                var get = inputs.GetEnvelopeAsync;
                if (checking || closed || completed is not null || id is null || get is null) return;
                checking = true;
                try
                {
                    var env = await get(id, cts.Token);
                    if (closed) return;
                    switch (env.Status)
                    {
                        case DocumensoEnvelopeStatus.Completed:
                            ShowCompleted(env);
                            break;
                        case DocumensoEnvelopeStatus.Rejected:
                            poll.Stop();
                            Say("This request was declined. Nothing was signed.", "WarningOrange");
                            break;
                        case DocumensoEnvelopeStatus.Cancelled:
                            poll.Stop();
                            Say("This request was cancelled. Nothing was signed.", "WarningOrange");
                            break;
                        default:
                            if (fromCompletePage)
                                Say($"Your part is done. {inputs.Host} seals the signed copy once everyone has signed — TDPdf will notice while this window is open.", "AccentGreen");
                            break;
                    }
                }
                catch (OperationCanceledException) { }
                catch (DocumensoApiException ex) when (ex.Failure == DocumensoFailure.AuthRejected)
                {
                    poll.Stop();   // the page still works; only TDPdf's status check lost its sign-in
                }
                catch (DocumensoApiException ex) when (ex.Failure == DocumensoFailure.RateLimited)
                {
                    poll.Interval = TimeSpan.FromTicks(Math.Min(poll.Interval.Ticks * 2, TimeSpan.FromMinutes(5).Ticks));
                }
                catch (Exception) { /* a missed check is retried on the next tick */ }
                finally
                {
                    checking = false;
                }
            }

            void ShowCompleted(DocumensoEnvelope env)
            {
                completed = env;
                poll.Stop();
                Say($"Signed. {inputs.Host} has sealed the signed copy. Keep the downloaded file as your signed original — saving it again in TDPdf removes the signature.", "AccentGreen");
                downloadBtn.Visibility = inputs.DownloadSignedAsync is null ? Visibility.Collapsed : Visibility.Visible;
                openBtn.Visibility = inputs.DownloadSignedAsync is null || inputs.OpenAsync is null ? Visibility.Collapsed : Visibility.Visible;
                openBtn.IsEnabled = savedPath is not null;
                if (!notified)
                {
                    notified = true;
                    try { inputs.Completed?.Invoke(); } catch { /* the window's job is done either way */ }
                }
            }

            void OnPage(string? url)
            {
                address.Text = DocumensoWebNavigation.DisplayAddress(inputs.BaseUrl, url);
                backBtn.IsEnabled = web.CoreWebView2?.CanGoBack == true;
                var page = DocumensoWebNavigation.Classify(inputs.BaseUrl, url);
                if (page == lastPage) return;
                lastPage = page;
                switch (page)
                {
                    case DocumensoWebPage.SigningComplete:
                        if (completed is null) Say("Signed on the page. Checking with the signing service…", "AccentGreen");
                        _ = CheckStatusAsync(fromCompletePage: true);
                        break;
                    case DocumensoWebPage.SigningRejected:
                        Say("You declined to sign. Nothing was signed.", "WarningOrange");
                        _ = CheckStatusAsync(fromCompletePage: false);
                        break;
                    case DocumensoWebPage.SigningExpired:
                        Say("This signing link has expired. Ask the sender to resend it.", "WarningOrange");
                        break;
                    case DocumensoWebPage.SigningWaiting:
                        Say("It isn't your turn to sign yet — someone earlier in the signing order still has to.", "TextSecondary");
                        break;
                    case DocumensoWebPage.SignIn when inputs.Request.Purpose != DocumensoWebPurpose.Sign:
                        if (completed is null)
                            Say($"Sign in to {inputs.Host} on this page to see the request. TDPdf remembers this web sign-in on this computer.", "TextSecondary");
                        break;
                }
            }

            void FallBack()
            {
                if (closed) return;
                string url = inputs.Request.Url;
                var fallback = inputs.Fallback;
                win.Close();
                fallback?.Invoke(url);
            }

            // ── WebView2 ───────────────────────────────────────────────────────────────────
            async Task InitAsync()
            {
                try
                {
                    if (!WebView2Bootstrap.EnsureLoader()) { FallBack(); return; }
                    string folder = DocumensoWebNavigation.UserDataFolder(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                    System.IO.Directory.CreateDirectory(folder);
                    var env = await CoreWebView2Environment.CreateAsync(null, folder, new CoreWebView2EnvironmentOptions
                    {
                        AllowSingleSignOnUsingOSPrimaryAccount = false,
                    });
                    if (closed) return;
                    await web.EnsureCoreWebView2Async(env);
                    if (closed) return;
                }
                catch (Exception)
                {
                    FallBack();
                    return;
                }

                var core = web.CoreWebView2;
                var s = core.Settings;
                s.AreDevToolsEnabled = false;
                s.AreHostObjectsAllowed = false;
                s.IsWebMessageEnabled = false;
                s.IsStatusBarEnabled = false;
                s.IsZoomControlEnabled = true;
                try
                {
                    // Follow TDPdf's theme where the service honours prefers-color-scheme.
                    var c = Brush("BgDark").Color;
                    core.Profile.PreferredColorScheme = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128
                        ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
                }
                catch { /* an older runtime without profiles: the site's default */ }

                core.NavigationStarting += (_, e) =>
                {
                    switch (DocumensoWebNavigation.Decide(inputs.BaseUrl, e.Uri))
                    {
                        case DocumensoNavigationDecision.Allow: return;
                        case DocumensoNavigationDecision.OpenExternally:
                            e.Cancel = true;
                            if (!OpenInBrowser(e.Uri) && !OpenMail(e.Uri)) Say("That link leads off the signing service, and no browser could be opened for it.", "WarningOrange");
                            return;
                        default:
                            e.Cancel = true;
                            return;
                    }
                };
                core.NewWindowRequested += (_, e) =>
                {
                    // Never a second browser window: the service's own links load here, anything
                    // else goes to the default browser.
                    e.Handled = true;
                    switch (DocumensoWebNavigation.Decide(inputs.BaseUrl, e.Uri))
                    {
                        case DocumensoNavigationDecision.Allow: core.Navigate(e.Uri); break;
                        case DocumensoNavigationDecision.OpenExternally: if (!OpenInBrowser(e.Uri)) OpenMail(e.Uri); break;
                    }
                };
                core.SourceChanged += (_, _) => OnPage(core.Source);
                core.NavigationCompleted += (_, e) =>
                {
                    if (!ready)
                    {
                        ready = true;
                        loading.Visibility = Visibility.Collapsed;
                        web.Visibility = Visibility.Visible;
                    }
                    if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                        Say($"Could not load the page from {inputs.Host}. Check your connection, then Reload.", "DangerRed");
                    OnPage(core.Source);
                };
                core.DocumentTitleChanged += (_, _) =>
                {
                    if (string.IsNullOrWhiteSpace(inputs.Request.Title) && !string.IsNullOrWhiteSpace(core.DocumentTitle))
                        titleText.Text = core.DocumentTitle;
                };
                core.ProcessFailed += (_, e) =>
                {
                    if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) FallBack();
                    else Say("The page stopped responding. Press Reload.", "DangerRed");
                };

                Navigate(inputs.Request.Url);
            }

            void Navigate(string url)
            {
                if (DocumensoWebNavigation.Decide(inputs.BaseUrl, url) != DocumensoNavigationDecision.Allow) return;
                try { web.CoreWebView2?.Navigate(url); }
                catch (Exception) { Say("TDPdf could not open that page.", "DangerRed"); }
            }

            _reuse = next =>
            {
                Reset(next);
                if (web.CoreWebView2 is not null) Navigate(next.Request.Url);
                // Before the control is ready, InitAsync navigates to inputs.Request.Url itself.
            };

            // ── Wiring ──────────────────────────────────────────────────────────────────────
            backBtn.Click += (_, _) => { if (web.CoreWebView2?.CanGoBack == true) web.CoreWebView2.GoBack(); };
            reloadBtn.Click += (_, _) => web.CoreWebView2?.Reload();
            externalBtn.Click += (_, _) =>
            {
                string? url = web.CoreWebView2?.Source ?? inputs.Request.Url;
                if (DocumensoWebNavigation.IsInScope(inputs.BaseUrl, url) && OpenInBrowser(url)) return;
                Say("Could not open a browser.", "DangerRed");
            };
            downloadBtn.Click += async (_, _) =>
            {
                var download = inputs.DownloadSignedAsync;
                if (downloading || completed is null || download is null) return;
                downloading = true;
                downloadBtn.IsEnabled = false;
                try
                {
                    var (path, message) = await download(win, completed);
                    if (path is not null)
                    {
                        savedPath = path;
                        openBtn.IsEnabled = true;
                        Say(message, "AccentGreen");
                    }
                    else if (message.Length > 0)
                    {
                        Say(message, "DangerRed");
                    }
                }
                catch (DocumensoApiException ex)
                {
                    Say(ex.Failure switch
                    {
                        DocumensoFailure.AuthRejected => "Your Electronic Signature sign-in has expired. Connect again, then download it from Signature Requests.",
                        DocumensoFailure.Network or DocumensoFailure.Timeout => $"Could not reach {inputs.Host}. Try again.",
                        _ => "The download did not complete.",
                    }, "DangerRed");
                }
                catch (Exception)
                {
                    Say("The download did not complete.", "DangerRed");
                }
                finally
                {
                    downloading = false;
                    downloadBtn.IsEnabled = true;
                }
            };
            openBtn.Click += async (_, _) =>
            {
                if (savedPath is null || inputs.OpenAsync is null) return;
                await inputs.OpenAsync(savedPath);
                win.Close();
            };
            maxBtn.Click += (_, _) => win.WindowState = win.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            closeBtn.Click += (_, _) => win.Close();
            win.StateChanged += (_, _) =>
            {
                maxBtn.Content = win.WindowState == WindowState.Maximized ? "" : "";
                // WindowChrome with no glass frame lets a maximised borderless window overhang the
                // screen by the resize border; pad it back in.
                outer.Margin = win.WindowState == WindowState.Maximized ? new Thickness(6) : new Thickness(0);
            };
            win.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { e.Handled = true; win.Close(); }
                else if (e.Key == Key.F5) { e.Handled = true; web.CoreWebView2?.Reload(); }
            };
            poll.Tick += async (_, _) => await CheckStatusAsync(fromCompletePage: false);
            win.Loaded += async (_, _) => await InitAsync();
            win.Closed += (_, _) =>
            {
                closed = true;
                poll.Stop();
                cts.Cancel();   // not disposed: a check still unwinding may read its token
                if (ReferenceEquals(_open, win)) { _open = null; _reuse = null; }
                // Releases the browser process and the profile folder's lock.
                try { web.Dispose(); } catch { }
            };

            _open = win;
            Reset(first);
            win.Show();
        }

        private static Button TitleGlyph(string glyph, string name)
        {
            var b = new Button
            {
                Content = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Width = 28,
                Height = 28,
                Background = Brushes.Transparent,
                Foreground = Brush("TextSecondary"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = name,
            };
            System.Windows.Automation.AutomationProperties.SetName(b, name);
            return b;
        }

        private static bool OpenMail(string? uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeMailto) return false;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static System.Drawing.Color ToDrawing(Color c) => System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
    }
}
