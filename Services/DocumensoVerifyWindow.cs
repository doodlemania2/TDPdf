using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using static TDPdf.Services.DocumensoUi;

namespace TDPdf.Services
{
    /// <summary>
    /// File ▸ Electronic Signature ▸ Verify Signatures (design §6 Phase 5): what the open file's
    /// signatures state, the server's evidence for a request this device sent, and a way to the
    /// user's PDF reader for an actual check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Informational only</b> (design §7.1, decision Q4). No green check, no "valid", no trust
    /// decision: every line comes from <see cref="SignatureObservationText"/>, which frames what it
    /// shows as stated by the file, and the window says plainly what TDPdf does not check. The real
    /// evidence is the signing service's: the certificate of completion and the audit log.
    /// </para>
    /// <para>
    /// Modal: it is a read of one document at one moment, with nothing to wait for.
    /// </para>
    /// </remarks>
    internal static class DocumensoVerifyWindow
    {
        internal sealed class Inputs
        {
            public string DocumentName { get; init; } = "";
            public SignatureObservations Observations { get; init; } = SignatureObservations.None;
            /// <summary>The tab has unsaved changes; what is described is the file on disk.</summary>
            public bool Dirty { get; init; }
            /// <summary>The configured instance's host, when Electronic Signature is available.</summary>
            public string? Host { get; init; }
            /// <summary>The tracked request this file matched, when one did.</summary>
            public DocumensoRequestStore.Entry? Match { get; init; }
            public bool MatchIsExact { get; init; }
            /// <summary>Saves (and opens) the matched request's certificate or audit log. The sentence to show, or null.</summary>
            public Func<Window, DocumensoArtifact, Task<string?>>? DownloadAsync { get; init; }
            /// <summary>Opens the file in the user's PDF reader; the sentence to show.</summary>
            public Func<Window, string>? OpenInReader { get; init; }
        }

        internal static void Show(Window? owner, Inputs inputs)
        {
            var (win, root, closeBtn) = CreateShell(owner, "Verify Signatures", 620);
            var body = new StackPanel { Margin = new Thickness(20, 12, 20, 4) };
            var scroller = new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 200),
            };
            root.Children.Add(scroller);

            body.Children.Add(Para($"What the digital signatures in “{inputs.DocumentName}” state.", "TextPrimary", 13, new Thickness(0, 0, 0, 6)));
            body.Children.Add(Para(SignatureObservationText.Disclaimer, "TextSecondary", 12, new Thickness(0, 0, 0, 10)));
            if (inputs.Dirty)
                body.Children.Add(Para("This describes the file as saved on disk, not your unsaved changes.", "WarningOrange", 12, new Thickness(0, 0, 0, 10)));

            body.Children.Add(BuildObservations(inputs.Observations, compact: false));

            var status = Para("", "TextSecondary", 12, new Thickness(0, 8, 0, 0));
            status.Visibility = Visibility.Collapsed;
            void Say(string text, string brushKey)
            {
                status.Text = text;
                status.Foreground = Brush(brushKey);
                status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            }

            // ── The signing service's evidence ───────────────────────────────────────────────
            if (inputs.Match is { } m && inputs.DownloadAsync is { } download && inputs.Host is { } host)
            {
                body.Children.Add(Heading($"Evidence from {host}", new Thickness(0, 14, 0, 4)));
                string when = DocumensoRequestView.FormatDate(m.SentUtc) is { } d ? $" on {d}" : "";
                body.Children.Add(Para(inputs.MatchIsExact
                        ? $"This is the signed copy of “{m.Title}”, sent from this computer{when}."
                        : $"This looks like the signed copy of “{m.Title}”, sent from this computer{when} (matched by its name and the signing service it names).",
                    "TextSecondary", 12, new Thickness(0, 0, 0, 6)));
                body.Children.Add(Para($"The certificate of completion and the audit log are {host}'s own record of who signed, when, and from where.",
                    "TextSecondary", 12, new Thickness(0, 0, 0, 6)));
                var row = new WrapPanel();
                var cert = SmallButton(owner, "Download certificate of completion…");
                var audit = SmallButton(owner, "Download audit log…");
                async Task Run(DocumensoArtifact what)
                {
                    cert.IsEnabled = audit.IsEnabled = false;
                    try
                    {
                        string? said = await download(win, what);
                        if (said is not null) Say(said, "AccentGreen");
                    }
                    catch (DocumensoApiException ex)
                    {
                        Say(ex.Failure switch
                        {
                            DocumensoFailure.AuthRejected => $"{host} rejected your sign-in — it may have expired. Connect again, then try again.",
                            DocumensoFailure.Network or DocumensoFailure.Timeout => $"Could not reach {host}. Check your connection and try again.",
                            DocumensoFailure.NotFound => $"{host} no longer has that request.",
                            DocumensoFailure.RateLimited => $"{host} is busy. Wait a minute and try again.",
                            _ => $"{host} answered unexpectedly{(ex.StatusCode is int s ? $" (HTTP {s})" : "")}.",
                        }, "DangerRed");
                    }
                    catch (Exception)
                    {
                        Say("The download did not complete.", "DangerRed");
                    }
                    finally
                    {
                        cert.IsEnabled = audit.IsEnabled = true;
                    }
                }
                cert.Click += async (_, _) => await Run(DocumensoArtifact.Certificate);
                audit.Click += async (_, _) => await Run(DocumensoArtifact.AuditLog);
                row.Children.Add(cert);
                row.Children.Add(audit);
                body.Children.Add(row);
            }
            body.Children.Add(status);

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 10, 20, 16) };
            if (inputs.OpenInReader is { } openInReader)
            {
                var reader = MakeButton(owner, "Open in your PDF reader for full validation", primary: false);
                reader.ToolTip = "A PDF reader that verifies signatures (such as Adobe Acrobat Reader) checks the signature, the certificate chain and the timestamp.";
                reader.Click += (_, _) => Say(openInReader(win), "TextSecondary");
                footer.Children.Add(reader);
            }
            var close = MakeButton(owner, "Close", primary: true);
            close.Margin = new Thickness(8, 0, 0, 0);
            close.IsCancel = true;
            close.IsDefault = true;
            close.Click += (_, _) => win.Close();
            footer.Children.Add(close);
            root.Children.Add(footer);
            closeBtn.Click += (_, _) => win.Close();
            win.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; win.Close(); } };
            win.ShowDialog();
        }

        /// <summary>
        /// The per-signature list and the whole-file summary line, shared with Document Info.
        /// <paramref name="compact"/> drops the reason and location lines.
        /// </summary>
        internal static FrameworkElement BuildObservations(SignatureObservations r, bool compact)
        {
            var panel = new StackPanel();
            if (!r.Any)
            {
                panel.Children.Add(Para(SignatureObservationText.Summary(r), "TextSecondary", 12, new Thickness(0, 0, 0, 4)));
                return panel;
            }
            for (int i = 0; i < r.Signatures.Count; i++)
            {
                var s = r.Signatures[i];
                panel.Children.Add(Heading(SignatureObservationText.Heading(s, i), new Thickness(0, i == 0 ? 0 : 8, 0, 2)));
                foreach (var line in SignatureObservationText.Lines(s))
                {
                    if (compact && (line.StartsWith("Reason", StringComparison.Ordinal) || line.StartsWith("Location", StringComparison.Ordinal))) continue;
                    panel.Children.Add(Para(line, "TextSecondary", 12, new Thickness(10, 0, 0, 1)));
                }
            }
            string brush = r.BytesAfterLastSignature > 0 ? "WarningOrange" : "TextSecondary";
            panel.Children.Add(Para(SignatureObservationText.Summary(r), brush, 12, new Thickness(0, 8, 0, 0)));
            return panel;
        }
    }
}
