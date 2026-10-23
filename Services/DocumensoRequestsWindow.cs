using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using static TDPdf.Services.DocumensoUi;

namespace TDPdf.Services
{
    /// <summary>
    /// File ▸ Electronic Signature ▸ Signature Requests: what this account has out for signature,
    /// this device's sends first, with per-row Open and Audit log &amp; certificate (inside TDPdf
    /// since Phase 6), Sign on this computer, Copy signing link, Resend reminder, the three
    /// downloads, Cancel and Delete. Design §5.4(c).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Modeless, one at a time.</b> It is a status board: something glanced at while working,
    /// left open while someone signs, and refreshed every 30 seconds. A modal window would hold the
    /// editor hostage for exactly as long as it is useful. Opening it again brings the open one
    /// forward instead of stacking a second poller.
    /// </para>
    /// <para>
    /// <b>Polling only while open</b> (design §1.4 / §7.3): the <see cref="DispatcherTimer"/> is
    /// created with the window and stopped when it closes; there is no background refresh anywhere
    /// else. A 429 doubles the interval up to <see cref="DocumensoRequestView.MaxRefreshInterval"/>.
    /// </para>
    /// <para>
    /// <b>Failures are inline</b>, in a line above the list, and the last good list stays on screen.
    /// A rejected token says so and offers Connect right there. Only the two irreversible actions,
    /// Cancel and Delete, ask first — through <see cref="TdpDialog"/>, which is themed.
    /// </para>
    /// <para>
    /// Everything that decides what a row says or offers is in <see cref="DocumensoRequestView"/>;
    /// downloads are the caller's (<see cref="Inputs.DownloadAsync"/>), because saving and opening
    /// the file in a tab belong to MainWindow.
    /// </para>
    /// </remarks>
    internal static class DocumensoRequestsWindow
    {
        internal sealed class Inputs
        {
            public string BaseUrl { get; init; } = "";
            public string Host { get; init; } = "";
            /// <summary>The team URL stored at connect time — the fallback for "Open in browser".</summary>
            public string? StoredTeamUrl { get; init; }
            /// <summary>A client from the stored sign-in without prompting, or null when not connected.</summary>
            public Func<DocumensoClient?> TryClient { get; init; } = () => null;
            /// <summary>Runs the Connect dialog over this window; the new client, or null.</summary>
            public Func<Window, DocumensoClient?> Connect { get; init; } = _ => null;
            /// <summary>
            /// Saves one artifact of an envelope where the user chooses and (for a signed copy)
            /// opens it. Returns the sentence to show, or null when the user cancelled the Save
            /// dialog. May throw <see cref="DocumensoApiException"/>.
            /// </summary>
            public Func<Window, DocumensoClient, DocumensoEnvelope, DocumensoArtifact, Task<string?>> DownloadAsync { get; init; }
                = (_, _, _, _) => Task.FromResult<string?>(null);
            /// <summary>
            /// Shows a page of the service — inside TDPdf when it can, else in the browser (the
            /// caller decides and explains). False when it could not be shown anywhere. Null: the
            /// default browser, as before Phase 6.
            /// </summary>
            public Func<Window, DocumensoClient, DocumensoWebRequest, bool>? OpenPage { get; init; }
        }

        private static Window? _open;

        internal static void Show(Window? owner, Inputs inputs)
        {
            if (_open is { } existing)
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
                return;
            }
            var win = Build(owner, inputs);
            _open = win;
            win.Closed += (_, _) => { if (ReferenceEquals(_open, win)) _open = null; };
            win.Show();
        }

        private static Window Build(Window? owner, Inputs inputs)
        {
            var (win, root, closeBtn) = CreateShell(owner, "Signature Requests", 780);
            win.ShowInTaskbar = false;
            DocumensoClient? client = inputs.TryClient();
            int page = 1, totalPages = 1;
            bool refreshing = false, acting = false;
            string lastFingerprint = "\0";
            TimeSpan interval = DocumensoRequestView.RefreshInterval;
            var cts = new CancellationTokenSource();

            var body = new StackPanel { Margin = new Thickness(20, 12, 20, 4) };
            root.Children.Add(body);
            body.Children.Add(Para(
                $"Documents sent for Electronic Signature on {inputs.Host}. Signing happens there; " +
                "this list refreshes every 30 seconds while it is open.",
                "TextSecondary", 12, new Thickness(0, 0, 0, 10)));

            // ── Toolbar: Refresh · last updated · paging ─────────────────────────────────────
            var bar = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var refreshBtn = MakeButton(owner, "Refresh", primary: false);
            refreshBtn.ToolTip = "Refresh now (F5)";
            var updated = Para("", "TextMuted", 11, new Thickness(10, 0, 0, 0));
            updated.VerticalAlignment = VerticalAlignment.Center;
            var paging = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var prevBtn = GlyphButton(owner, "", "Previous page");
            var pageLabel = Para("", "TextSecondary", 11, new Thickness(8, 0, 8, 0));
            pageLabel.VerticalAlignment = VerticalAlignment.Center;
            var nextBtn = GlyphButton(owner, "", "Next page");
            paging.Children.Add(prevBtn);
            paging.Children.Add(pageLabel);
            paging.Children.Add(nextBtn);
            Grid.SetColumn(updated, 1);
            Grid.SetColumn(paging, 2);
            bar.Children.Add(refreshBtn);
            bar.Children.Add(updated);
            bar.Children.Add(paging);
            body.Children.Add(bar);

            // ── The inline message line (+ Connect when the sign-in is gone) ─────────────────
            var messageRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
            var message = Para("", "DangerRed", 12, new Thickness(0, 0, 10, 0));
            message.VerticalAlignment = VerticalAlignment.Center;
            message.MaxWidth = 600;
            var connectBtn = MakeButton(owner, "Connect…", primary: true);
            connectBtn.Visibility = Visibility.Collapsed;
            messageRow.Children.Add(message);
            messageRow.Children.Add(connectBtn);
            body.Children.Add(messageRow);

            var list = new StackPanel();
            var scroller = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 280),
                MinHeight = 160,
            };
            body.Children.Add(scroller);
            list.Children.Add(Para("Loading…", "TextSecondary", 12, new Thickness(0, 6, 0, 6)));

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 8, 20, 14) };
            var closeFooter = MakeButton(owner, "Close", primary: true);
            closeFooter.IsCancel = true;
            footer.Children.Add(closeFooter);
            root.Children.Add(footer);

            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };

            void Say(string text, string brushKey = "DangerRed", bool offerConnect = false)
            {
                message.Text = text;
                message.Foreground = Brush(brushKey);
                connectBtn.Visibility = offerConnect ? Visibility.Visible : Visibility.Collapsed;
                messageRow.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            }

            void Expired() => Say("Your Electronic Signature sign-in has expired or was revoked. Connect again to continue.", offerConnect: true);

            string Describe(DocumensoApiException ex) => ex.Failure switch
            {
                DocumensoFailure.Network or DocumensoFailure.Timeout => $"Could not reach {inputs.Host}. Showing what was last loaded.",
                DocumensoFailure.RateLimited => $"{inputs.Host} is busy, so TDPdf will check less often for a while.",
                DocumensoFailure.NotFound => $"{inputs.Host} no longer has that document.",
                DocumensoFailure.Server => $"{inputs.Host} had a problem (HTTP {ex.StatusCode}). Try again shortly.",
                DocumensoFailure.Rejected => $"{inputs.Host} refused the request (HTTP {ex.StatusCode}{(ex.ErrorCode is null ? "" : " " + ex.ErrorCode)}).",
                _ => $"{inputs.Host} answered unexpectedly.",
            };

            // ── Refresh ───────────────────────────────────────────────────────────────────────
            async Task RefreshAsync()
            {
                if (refreshing || acting) return;
                if (client is null) { Expired(); return; }
                refreshing = true;
                refreshBtn.IsEnabled = false;
                var c = client;
                DocumensoFailure? failure = null;
                TimeSpan? retryAfter = null;
                try
                {
                    var tracked = DocumensoRequestStore.Load().Take(DocumensoRequestView.TrackedRefreshLimit).ToList();
                    IReadOnlyList<DocumensoEnvelope> trackedEnvs = Array.Empty<DocumensoEnvelope>();
                    if (tracked.Count > 0)
                    {
                        try
                        {
                            trackedEnvs = await c.GetManyAsync(tracked.Select(t => t.EnvelopeId).ToList(), cts.Token);
                        }
                        catch (DocumensoApiException ex) when (ex.Failure is DocumensoFailure.Rejected or DocumensoFailure.NotFound or DocumensoFailure.UnexpectedResponse)
                        {
                            // A tracked id the server no longer knows must not hide the whole list;
                            // this device's sends simply show up in the server-wide list instead.
                        }
                    }
                    var listed = await c.ListEnvelopesAsync(new DocumensoEnvelopeQuery(Page: page, PerPage: DocumensoRequestView.PageSize), cts.Token);
                    totalPages = Math.Max(1, listed.TotalPages);
                    if (page > totalPages) page = totalPages;
                    var (mine, others) = DocumensoRequestView.Merge(tracked, trackedEnvs, listed.Data);
                    string fp = page + "\n" + DocumensoRequestView.Fingerprint(mine.Concat(others));
                    if (fp != lastFingerprint)
                    {
                        lastFingerprint = fp;
                        Render(mine, others);
                    }
                    updated.Text = $"Updated {DateTime.Now:t}";
                    Say("");
                }
                catch (OperationCanceledException)
                {
                    return;   // the window closed mid-call
                }
                catch (DocumensoApiException ex)
                {
                    failure = ex.Failure;
                    retryAfter = ex.RetryAfter;
                    if (ex.Failure == DocumensoFailure.AuthRejected) Expired();
                    else Say(Describe(ex), ex.Failure == DocumensoFailure.RateLimited ? "WarningOrange" : "DangerRed");
                }
                finally
                {
                    refreshing = false;
                    refreshBtn.IsEnabled = true;
                    UpdatePaging();
                }

                interval = DocumensoRequestView.NextRefreshInterval(interval, failure, retryAfter);
                if (failure == DocumensoFailure.AuthRejected) timer.Stop();
                else if (timer.Interval != interval) timer.Interval = interval;
            }

            void UpdatePaging()
            {
                pageLabel.Text = $"Page {page} of {totalPages}";
                prevBtn.IsEnabled = page > 1 && !refreshing;
                nextBtn.IsEnabled = page < totalPages && !refreshing;
                paging.Visibility = totalPages > 1 ? Visibility.Visible : Visibility.Collapsed;
            }

            // ── Rows ──────────────────────────────────────────────────────────────────────────
            void Render(List<DocumensoRequestRow> mine, List<DocumensoRequestRow> others)
            {
                list.Children.Clear();
                if (mine.Count > 0)
                {
                    list.Children.Add(Heading("Sent from this computer", new Thickness(0, 4, 0, 4)));
                    foreach (var r in mine) list.Children.Add(BuildRow(r));
                }
                list.Children.Add(Heading(mine.Count > 0 ? $"Everything else on {inputs.Host}" : $"Signature requests on {inputs.Host}",
                    new Thickness(0, mine.Count > 0 ? 12 : 4, 0, 4)));
                if (others.Count == 0)
                    list.Children.Add(Para(mine.Count > 0 || page > 1 ? "Nothing else on this page." : "No signature requests yet. Send one with File ▸ Electronic Signature ▸ Send for Signature.",
                        "TextSecondary", 12, new Thickness(0, 2, 0, 6)));
                foreach (var r in others) list.Children.Add(BuildRow(r));
            }

            FrameworkElement BuildRow(DocumensoRequestRow row)
            {
                var env = row.Envelope;
                string title = string.IsNullOrWhiteSpace(env.Title) ? "(untitled)" : env.Title.Trim();
                var box = new Border
                {
                    BorderBrush = Brush("BorderDim"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 6, 0, 6),
                };
                var stack = new StackPanel();
                box.Child = stack;

                var titleText = Para(title, "TextPrimary", 13, new Thickness(0), semiBold: true);
                titleText.TextWrapping = TextWrapping.NoWrap;
                titleText.TextTrimming = TextTrimming.CharacterEllipsis;
                titleText.ToolTip = title;
                stack.Children.Add(titleText);

                var meta = new TextBlock { FontSize = 11, Margin = new Thickness(0, 1, 0, 3), TextWrapping = TextWrapping.Wrap };
                meta.Inlines.Add(new System.Windows.Documents.Run(DocumensoRequestView.StatusLabel(env.Status))
                { Foreground = Brush(DocumensoRequestView.StatusBrushKey(env.Status)), FontWeight = FontWeights.SemiBold });
                string? sent = DocumensoRequestView.FormatDate(row.SentUtc);
                string? done = env.Status == DocumensoEnvelopeStatus.Completed
                    ? DocumensoRequestView.FormatDate(DocumensoRequestView.ParseApiDate(env.CompletedAt)) : null;
                string tail = (sent is null ? "" : $" · sent {sent}") + (done is null ? "" : $" · completed {done}");
                if (tail.Length > 0) meta.Inlines.Add(new System.Windows.Documents.Run(tail) { Foreground = Brush("TextSecondary") });
                stack.Children.Add(meta);

                foreach (var r in env.Recipients.OrderBy(r => r.SigningOrder ?? int.MaxValue))
                {
                    var line = new Grid { Margin = new Thickness(12, 0, 0, 1) };
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var who = Para($"{DocumensoRequestView.RecipientLabel(r)}: {DocumensoRequestView.RecipientStatus(r, env.Status)}",
                        "TextSecondary", 11, new Thickness(0));
                    who.VerticalAlignment = VerticalAlignment.Center;
                    line.Children.Add(who);
                    if (DocumensoRequestView.CanCopySigningLink(env.Status, r)
                        && DocumensoRequestView.SigningUrl(inputs.BaseUrl, r.Token) is { } link)
                    {
                        string label = DocumensoRequestView.RecipientLabel(r);
                        var linkButtons = new StackPanel { Orientation = Orientation.Horizontal };
                        // For a recipient sitting at this computer — the sender's colleague at the
                        // same desk, or the sender signing their own part.
                        var signHere = SmallButton(owner, "Sign on this computer", $"Sign on this computer as {label}");
                        signHere.Margin = new Thickness(6, 0, 0, 2);
                        signHere.ToolTip = $"Opens {label}'s signing page here. Only for {label}: whoever uses it signs as them.";
                        signHere.Click += (_, _) =>
                        {
                            if (client is null) { Expired(); return; }
                            var req = new DocumensoWebRequest(link, $"Sign — {title}", DocumensoWebPurpose.Sign, env.Id);
                            if (!ShowPage(client, req)) Say("Could not open the signing page.");
                        };
                        linkButtons.Children.Add(signHere);
                        var copy = SmallButton(owner, "Copy signing link", $"Copy signing link for {label}");
                        copy.Margin = new Thickness(6, 0, 0, 2);
                        copy.ToolTip = "Whoever has this link can sign as this person, so give it only to them.";
                        copy.Click += (_, _) =>
                        {
                            try
                            {
                                Clipboard.SetText(link);
                                Say($"Copied the signing link for {label}. Give it only to them, because it lets whoever has it sign.", "AccentGreen");
                            }
                            catch
                            {
                                Say("The clipboard is busy. Try again.");
                            }
                        };
                        linkButtons.Children.Add(copy);
                        Grid.SetColumn(linkButtons, 1);
                        line.Children.Add(linkButtons);
                    }
                    stack.Children.Add(line);
                }

                var actions = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
                var can = DocumensoRequestView.Actions(env.Status);

                var open = SmallButton(owner, "Open", $"Open {title} on {inputs.Host}");
                open.ToolTip = $"Shows this request's page on {inputs.Host}";
                open.Click += (_, _) =>
                {
                    string url = DocumensoRequestView.WebUrl(inputs.BaseUrl, env.Team?.Url ?? inputs.StoredTeamUrl, env.Id);
                    if (client is null || !ShowPage(client, new DocumensoWebRequest(url, title, DocumensoWebPurpose.Document, env.Id)))
                        Say($"Could not open the page. The document is at {url}");
                };
                actions.Children.Add(open);

                if (can.AuditLogPage
                    && DocumensoRequestView.LogsUrl(inputs.BaseUrl, env.Team?.Url ?? inputs.StoredTeamUrl, env.Id) is { } logsUrl)
                {
                    var logs = SmallButton(owner, "Audit log & certificate", $"Show the audit log and certificate for {title}");
                    logs.ToolTip = $"Who did what and when, from {inputs.Host}. The certificate of completion can be downloaded there once everyone has signed.";
                    logs.Click += (_, _) =>
                    {
                        if (client is null) { Expired(); return; }
                        if (!ShowPage(client, new DocumensoWebRequest(logsUrl, $"Audit log — {title}", DocumensoWebPurpose.AuditLog, env.Id)))
                            Say($"Could not open the page. The audit log is at {logsUrl}");
                    };
                    actions.Children.Add(logs);
                }

                var remindIds = can.Remind ? DocumensoRequestView.ReminderRecipientIds(env) : Array.Empty<long>();
                if (remindIds.Count > 0)
                {
                    int n = remindIds.Count;
                    var remind = SmallButton(owner, "Resend reminder", $"Resend the signing email for {title}");
                    remind.ToolTip = $"{inputs.Host} emails the {n} recipient{(n == 1 ? "" : "s")} who still have to act, and renews any expired signing link.";
                    remind.Click += async (_, _) =>
                    {
                        await ActAsync(async c =>
                        {
                            await c.RedistributeAsync(env.Id, remindIds, CancellationToken.None);
                            Say($"Reminder sent to {n} recipient{(n == 1 ? "" : "s")} for “{title}”.", "AccentGreen");
                        });
                    };
                    actions.Children.Add(remind);
                }

                if (can.DownloadSignedCopy) actions.Children.Add(DownloadButton("Download signed copy…", env, DocumensoArtifact.SignedCopy));
                if (can.DownloadCertificate) actions.Children.Add(DownloadButton("Download certificate…", env, DocumensoArtifact.Certificate));
                if (can.DownloadAuditLog) actions.Children.Add(DownloadButton("Download audit log…", env, DocumensoArtifact.AuditLog));

                if (can.Cancel)
                {
                    var cancel = SmallButton(owner, "Cancel request…", $"Cancel the signature request for {title}");
                    cancel.Foreground = Brush("DangerRed");
                    cancel.Click += async (_, _) => await CancelAsync(env, title);
                    actions.Children.Add(cancel);
                }
                if (can.Delete)
                {
                    var delete = SmallButton(owner, "Delete…", $"Delete {title}");
                    delete.Foreground = Brush("DangerRed");
                    delete.Click += async (_, _) => await DeleteAsync(env, title);
                    actions.Children.Add(delete);
                }
                stack.Children.Add(actions);
                return box;
            }

            // In TDPdf's signing window when the caller can, else the default browser.
            bool ShowPage(DocumensoClient c, DocumensoWebRequest req)
                => inputs.OpenPage is { } show ? show(win, c, req) : OpenInBrowser(req.Url);

            Button DownloadButton(string text, DocumensoEnvelope env, DocumensoArtifact what)
            {
                var b = SmallButton(owner, text);
                b.Click += async (_, _) =>
                {
                    await ActAsync(async c =>
                    {
                        string? said = await inputs.DownloadAsync(win, c, env, what);
                        if (said is not null) Say(said, "AccentGreen");
                    });
                };
                return b;
            }

            async Task CancelAsync(DocumensoEnvelope env, string title)
            {
                var answer = TdpDialog.ShowYesNo(win,
                    $"Cancel the signature request for “{title}”?\n\n" +
                    "This cannot be undone. Nobody will be able to sign it any more, including anyone part-way through, " +
                    $"and {inputs.Host} emails the recipients to tell them it was cancelled.",
                    "Cancel request", "Keep it", "Electronic Signature", MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
                await ActAsync(async c =>
                {
                    await c.CancelAsync(env.Id, null, CancellationToken.None);
                    Say($"Cancelled “{title}”.", "AccentGreen");
                    lastFingerprint = "\0";
                });
                await RefreshAsync();
            }

            async Task DeleteAsync(DocumensoEnvelope env, string title)
            {
                string who = env.Status == DocumensoEnvelopeStatus.Draft
                    ? "It was never sent, so nobody is emailed."
                    : $"Recipients it was sent to may get an email from {inputs.Host} saying it was deleted.";
                var answer = TdpDialog.ShowYesNo(win,
                    $"Delete “{title}” from {inputs.Host}?\n\nThis cannot be undone. {who}",
                    "Delete", "Keep it", "Electronic Signature", MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
                await ActAsync(async c =>
                {
                    await c.DeleteAsync(env.Id, CancellationToken.None);
                    DocumensoRequestStore.Remove(env.Id);
                    Say($"Deleted “{title}”.", "AccentGreen");
                    lastFingerprint = "\0";
                });
                await RefreshAsync();
            }

            // One action at a time, never alongside a refresh, every failure inline.
            async Task ActAsync(Func<DocumensoClient, Task> action)
            {
                if (acting) return;
                if (client is null) { Expired(); return; }
                while (refreshing) await Task.Delay(50);
                acting = true;
                win.Cursor = Cursors.Wait;
                list.IsEnabled = false;
                try
                {
                    await action(client);
                }
                catch (DocumensoApiException ex) when (ex.Failure == DocumensoFailure.AuthRejected)
                {
                    Expired();
                }
                catch (DocumensoApiException ex)
                {
                    Say(Describe(ex));
                }
                catch (OperationCanceledException)
                {
                    Say("Cancelled.", "TextSecondary");
                }
                catch (Exception)
                {
                    Say("Something went wrong. Nothing was changed.");
                }
                finally
                {
                    acting = false;
                    list.IsEnabled = true;
                    win.Cursor = null;
                }
            }

            // ── Wiring ────────────────────────────────────────────────────────────────────────
            refreshBtn.Click += async (_, _) => await RefreshAsync();
            prevBtn.Click += async (_, _) => { if (page > 1) { page--; await RefreshAsync(); } };
            nextBtn.Click += async (_, _) => { if (page < totalPages) { page++; await RefreshAsync(); } };
            connectBtn.Click += async (_, _) =>
            {
                var c = inputs.Connect(win);
                if (c is null) return;
                client = c;
                interval = DocumensoRequestView.RefreshInterval;
                timer.Interval = interval;
                timer.Start();
                Say("");
                await RefreshAsync();
            };
            closeBtn.Click += (_, _) => win.Close();
            closeFooter.Click += (_, _) => win.Close();
            win.PreviewKeyDown += async (_, e) =>
            {
                if (e.Key == Key.Escape) { e.Handled = true; win.Close(); }
                else if (e.Key == Key.F5) { e.Handled = true; await RefreshAsync(); }
            };
            timer.Tick += async (_, _) => await RefreshAsync();
            win.Loaded += async (_, _) =>
            {
                UpdatePaging();
                timer.Start();
                await RefreshAsync();
            };
            win.Closed += (_, _) =>
            {
                timer.Stop();
                cts.Cancel();   // not disposed: a handler still unwinding may read its token
            };
            return win;
        }
    }
}
