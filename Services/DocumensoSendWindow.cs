using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using static TDPdf.Services.DocumensoUi;

namespace TDPdf.Services
{
    /// <summary>
    /// File ▸ Electronic Signature ▸ Send for Signature: title, recipients, signing order, an
    /// optional message, and the fields — placed on the page or found as <c>{{…}}</c>
    /// placeholders — then Send. Design §5.4(a).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This window only edits an <see cref="ESignSendSession"/> and shows its
    /// <see cref="DocumensoSendPlan.Check"/>; what is actually sent is decided in
    /// <c>DocumensoSendPlan</c> and done by the caller's <see cref="Inputs.SendAsync"/>, so the
    /// whole of it can be reasoned about (and tested) without a window.
    /// </para>
    /// <para>
    /// <b>Place fields on the page</b> closes the dialog with <see cref="Outcome.PlaceFields"/>; the
    /// caller runs the canvas placement mode and shows the dialog again on the same session, so
    /// nothing typed is lost on the way out and back. A modal window cannot simply hide while the
    /// canvas is used — hiding a WPF dialog ends its modal loop — and this shape avoids pretending
    /// otherwise.
    /// </para>
    /// <para>
    /// <b>Failures are inline, never modal</b> (design §5.3): the send runs with the dialog open,
    /// its progress on a line at the bottom, Esc or Stop cancels it, and a failure leaves the dialog
    /// open with the reason in red and everything still filled in, ready to try again.
    /// </para>
    /// </remarks>
    internal static class DocumensoSendWindow
    {
        internal enum Outcome { Cancelled, PlaceFields, Sent }

        /// <summary>What one press of Send produced, as the dialog needs it.</summary>
        internal sealed record SendAttempt(bool Ok, string? Error, IReadOnlyList<ESignSigningLink> Links, int RecipientCount,
            string? EnvelopeId = null);

        /// <summary>What the caller supplies besides the session.</summary>
        internal sealed class Inputs
        {
            public string Host { get; init; } = "";
            /// <summary>The connected account, when known (it is learned from the first send).</summary>
            public string? AccountEmail { get; init; }
            public string? AccountName { get; init; }
            public IReadOnlyList<DocumensoPlaceholder> Placeholders { get; init; } = Array.Empty<DocumensoPlaceholder>();
            /// <summary>0-based page → its visible box starts away from 0,0 (the seal-offset warning).</summary>
            public Func<int, bool>? PageHasOffsetOrigin { get; init; }
            /// <summary>Runs the send. Must not throw; failures come back as an unsuccessful attempt.</summary>
            public Func<IProgress<string>, CancellationToken, Task<SendAttempt>> SendAsync { get; init; }
                = (_, _) => Task.FromResult(new SendAttempt(false, "Not available.", Array.Empty<ESignSigningLink>(), 0));
        }

        private static readonly DocumensoRecipientRole[] Roles =
            { DocumensoRecipientRole.Signer, DocumensoRecipientRole.Approver, DocumensoRecipientRole.Viewer, DocumensoRecipientRole.Cc };

        internal static (Outcome Outcome, SendAttempt? Result) Show(Window? owner, ESignSendSession s, Inputs inputs)
        {
            var outcome = Outcome.Cancelled;
            SendAttempt? sent = null;
            CancellationTokenSource? cts = null;
            bool busy = false;
            bool showErrors = false;   // errors stay quiet until the first press of Send

            var (win, root, closeBtn) = CreateShell(owner, "Send for Electronic Signature", 640);

            var body = new StackPanel { Margin = new Thickness(20, 14, 20, 6) };
            var scroller = new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 220),
            };
            root.Children.Add(scroller);

            body.Children.Add(Para(
                $"Each recipient gets an email from {inputs.Host} with a link to the document. " +
                "Signing happens there, in their browser; the signed copy is sealed by the signing service.",
                "TextSecondary", 12, new Thickness(0, 0, 0, 12)));

            // ── Title ──────────────────────────────────────────────────────────────────────
            body.Children.Add(Heading("Document title", new Thickness(0, 0, 0, 4)));
            var titleBox = MakeTextBox(s.Title, "Document title", DocumensoSendPlan.MaxTitle);
            titleBox.Margin = new Thickness(0, 0, 0, 12);
            body.Children.Add(titleBox);

            // ── Recipients ─────────────────────────────────────────────────────────────────
            body.Children.Add(Heading("Recipients", new Thickness(0, 0, 0, 4)));
            var recipientsPanel = new StackPanel();
            body.Children.Add(recipientsPanel);

            var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 4) };
            var addBtn = MakeButton(owner, "Add recipient", primary: false);
            var addMeBtn = MakeButton(owner, "Add me", primary: false);
            addMeBtn.Margin = new Thickness(8, 0, 0, 0);
            addMeBtn.ToolTip = inputs.AccountEmail is { Length: > 0 } me
                ? $"Add yourself ({me})"
                : "Add yourself — type your address; TDPdf remembers it after your first send";
            addRow.Children.Add(addBtn);
            addRow.Children.Add(addMeBtn);
            body.Children.Add(addRow);
            var addMeNote = Para("", "TextSecondary", 11, new Thickness(0, 0, 0, 4));
            addMeNote.Visibility = Visibility.Collapsed;
            body.Children.Add(addMeNote);

            var orderCheck = MakeCheckBox("Signers sign in order (each is emailed when the one before has signed)", s.SequentialOrder);
            orderCheck.Margin = new Thickness(0, 4, 0, 12);
            body.Children.Add(orderCheck);

            // ── Message ────────────────────────────────────────────────────────────────────
            body.Children.Add(Heading("Email to recipients (optional)", new Thickness(0, 0, 0, 4)));
            var subjectBox = MakeTextBox(s.Subject, "Email subject", DocumensoSendPlan.MaxSubject);
            subjectBox.ToolTip = "Subject — leave empty for the signing service's default";
            subjectBox.Margin = new Thickness(0, 0, 0, 4);
            body.Children.Add(subjectBox);
            var messageBox = MakeTextBox(s.Message, "Email message", DocumensoSendPlan.MaxMessage);
            messageBox.ToolTip = "Message — leave empty for the signing service's default";
            messageBox.AcceptsReturn = true;
            messageBox.TextWrapping = TextWrapping.Wrap;
            messageBox.Height = 64;
            messageBox.VerticalContentAlignment = VerticalAlignment.Top;
            messageBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            messageBox.Margin = new Thickness(0, 0, 0, 12);
            body.Children.Add(messageBox);

            // ── Fields ─────────────────────────────────────────────────────────────────────
            body.Children.Add(Heading("Fields", new Thickness(0, 0, 0, 4)));
            var fieldsPanel = new StackPanel();
            body.Children.Add(fieldsPanel);
            var placeBtn = MakeButton(owner, "Place fields on the page", primary: false);
            placeBtn.HorizontalAlignment = HorizontalAlignment.Left;
            placeBtn.Margin = new Thickness(0, 6, 0, 10);
            body.Children.Add(placeBtn);

            var issuesPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            body.Children.Add(issuesPanel);

            // ── Footer ─────────────────────────────────────────────────────────────────────
            var footer = new Grid { Margin = new Thickness(20, 6, 20, 16) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var status = Para("", "TextSecondary", 12, new Thickness(0, 0, 12, 0));
            status.VerticalAlignment = VerticalAlignment.Center;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var sendBtn = MakeButton(owner, "Send", primary: true);
            var cancelBtn = MakeButton(owner, "Cancel", primary: false);
            cancelBtn.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(sendBtn);
            buttons.Children.Add(cancelBtn);
            Grid.SetColumn(buttons, 1);
            footer.Children.Add(status);
            footer.Children.Add(buttons);
            root.Children.Add(footer);

            void ShowStatus(string text, string brushKey)
            {
                status.Text = text;
                status.Foreground = Brush(brushKey);
            }

            // ── Behaviour ──────────────────────────────────────────────────────────────────

            void Refresh()
            {
                // Fields summary: per recipient who fills anything in, then the placeholders.
                fieldsPanel.Children.Clear();
                bool anyTaker = false;
                for (int i = 0; i < s.Recipients.Count; i++)
                {
                    var r = s.Recipients[i];
                    if (!DocumensoSendPlan.TakesFields(r.Role)) continue;
                    anyTaker = true;
                    var mine = s.Fields.Where(f => f.RecipientKey == r.Key).ToList();
                    int sigs = mine.Count(f => DocumensoSendPlan.IsSignatureType(f.Type));
                    string what = mine.Count == 0 ? "no fields placed"
                        : $"{mine.Count} field{(mine.Count == 1 ? "" : "s")} placed" + (sigs > 0 ? $" ({sigs} Signature)" : "");
                    fieldsPanel.Children.Add(SwatchLine(i, $"{DocumensoSendPlan.Label(r, i)} — {what}"));
                }
                if (!anyTaker)
                    fieldsPanel.Children.Add(Para("Add a signer or approver to place fields for.", "TextSecondary", 12, new Thickness(0, 0, 0, 2)));

                int n = inputs.Placeholders.Count;
                if (n > 0)
                {
                    fieldsPanel.Children.Add(Para(
                        $"This document contains {n} signature placeholder{(n == 1 ? "" : "s")} — they will be used automatically. " +
                        "In them, r1 is the first recipient listed above, r2 the second, and so on.",
                        "AccentGreen", 12, new Thickness(0, 4, 0, 2)));
                }
                placeBtn.IsEnabled = anyTaker && !busy;

                // Issues.
                var check = DocumensoSendPlan.Check(s, inputs.Placeholders, inputs.PageHasOffsetOrigin);
                issuesPanel.Children.Clear();
                if (showErrors)
                    foreach (var e in check.Errors)
                        issuesPanel.Children.Add(Para(e, "DangerRed", 12, new Thickness(0, 0, 0, 3)));
                foreach (var w in check.Warnings)
                    issuesPanel.Children.Add(Para(w, "WarningOrange", 12, new Thickness(0, 0, 0, 3)));
            }

            void RebuildRecipients()
            {
                recipientsPanel.Children.Clear();
                bool showMove = s.SequentialOrder || inputs.Placeholders.Count > 0;
                if (s.Recipients.Count == 0)
                {
                    recipientsPanel.Children.Add(Para("No recipients yet.", "TextSecondary", 12, new Thickness(0, 0, 0, 2)));
                }
                else
                {
                    var header = RowGrid(showMove);
                    AddCell(header, Para("Name", "TextMuted", 11, new Thickness(2, 0, 0, 2)), 2);
                    AddCell(header, Para("Email", "TextMuted", 11, new Thickness(2, 0, 0, 2)), 3);
                    AddCell(header, Para("Role", "TextMuted", 11, new Thickness(2, 0, 0, 2)), 4);
                    recipientsPanel.Children.Add(header);
                }

                for (int i = 0; i < s.Recipients.Count; i++)
                {
                    int index = i;
                    var r = s.Recipients[i];
                    var row = RowGrid(showMove);
                    row.Margin = new Thickness(0, 0, 0, 4);

                    var swatch = new Rectangle { Width = 4, Fill = RecipientBrush(i), Margin = new Thickness(0, 2, 6, 2) };
                    AddCell(row, swatch, 0);
                    var num = Para($"{i + 1}.", "TextSecondary", 12, new Thickness(0, 0, 4, 0));
                    num.VerticalAlignment = VerticalAlignment.Center;
                    num.ToolTip = $"r{i + 1} in placeholders" + (s.SequentialOrder ? $"; signs {Ordinal(i + 1)}" : "");
                    AddCell(row, num, 1);

                    var name = MakeTextBox(r.Name, $"Recipient {i + 1} name", DocumensoSendPlan.MaxName);
                    name.Margin = new Thickness(0, 0, 4, 0);
                    name.TextChanged += (_, _) => { r.Name = name.Text; Refresh(); };
                    AddCell(row, name, 2);

                    var email = MakeTextBox(r.Email, $"Recipient {i + 1} email", DocumensoSendPlan.MaxEmail);
                    email.Margin = new Thickness(0, 0, 4, 0);
                    email.TextChanged += (_, _) =>
                    {
                        r.Email = email.Text;
                        email.BorderBrush = Brush(email.Text.Trim().Length == 0 || DocumensoSendPlan.IsValidEmail(email.Text) ? "BorderDim" : "DangerRed");
                        Refresh();
                    };
                    AddCell(row, email, 3);

                    var role = MakeCombo(owner, $"Recipient {i + 1} role");
                    foreach (var opt in Roles)
                        role.Items.Add(new ComboBoxItem { Content = DocumensoSendPlan.RoleLabel(opt), Tag = opt });
                    role.SelectedIndex = Math.Max(0, Array.IndexOf(Roles, r.Role));
                    role.Margin = new Thickness(0, 0, 4, 0);
                    role.VerticalContentAlignment = VerticalAlignment.Center;
                    role.SelectionChanged += (_, _) =>
                    {
                        if (role.SelectedItem is ComboBoxItem { Tag: DocumensoRecipientRole picked }) r.Role = picked;
                        Refresh();
                    };
                    AddCell(row, role, 4);

                    int col = 5;
                    if (showMove)
                    {
                        var up = GlyphButton(owner, "", $"Move recipient {i + 1} up");
                        up.IsEnabled = i > 0;
                        up.Click += (_, _) => { if (s.Move(index, -1)) { RebuildRecipients(); Refresh(); } };
                        var down = GlyphButton(owner, "", $"Move recipient {i + 1} down");
                        down.IsEnabled = i < s.Recipients.Count - 1;
                        down.Margin = new Thickness(2, 0, 2, 0);
                        down.Click += (_, _) => { if (s.Move(index, +1)) { RebuildRecipients(); Refresh(); } };
                        AddCell(row, up, col++);
                        AddCell(row, down, col++);
                    }
                    var remove = GlyphButton(owner, "", $"Remove recipient {i + 1}");
                    remove.Click += (_, _) =>
                    {
                        int dropped = s.RemoveRecipient(r.Key);
                        RebuildRecipients();
                        Refresh();
                        if (dropped > 0) ShowStatus($"Removed {dropped} field{(dropped == 1 ? "" : "s")} placed for that recipient.", "TextSecondary");
                    };
                    AddCell(row, remove, col);
                    recipientsPanel.Children.Add(row);
                }
            }

            TextBox? LastEmailBox() => recipientsPanel.Children.OfType<Grid>().LastOrDefault()?
                .Children.OfType<TextBox>().Skip(1).FirstOrDefault();

            titleBox.TextChanged += (_, _) => { s.Title = titleBox.Text; Refresh(); };
            subjectBox.TextChanged += (_, _) => s.Subject = subjectBox.Text;
            messageBox.TextChanged += (_, _) => s.Message = messageBox.Text;

            addBtn.Click += (_, _) =>
            {
                s.Recipients.Add(new ESignRecipientEntry());
                RebuildRecipients();
                Refresh();
                var first = recipientsPanel.Children.OfType<Grid>().LastOrDefault()?.Children.OfType<TextBox>().FirstOrDefault();
                first?.Focus();
            };

            addMeBtn.Click += (_, _) =>
            {
                string? myEmail = inputs.AccountEmail;
                if (myEmail is { Length: > 0 } && s.Recipients.Any(r => string.Equals(r.Email.Trim(), myEmail, StringComparison.OrdinalIgnoreCase)))
                {
                    ShowStatus("You are already on the list.", "TextSecondary");
                    return;
                }
                s.Recipients.Add(new ESignRecipientEntry { Name = inputs.AccountName ?? "", Email = myEmail ?? "" });
                RebuildRecipients();
                Refresh();
                if (myEmail is not { Length: > 0 })
                {
                    addMeNote.Text = "TDPdf does not know your address yet — type it in. It is remembered after your first send.";
                    addMeNote.Visibility = Visibility.Visible;
                    LastEmailBox()?.Focus();
                }
            };

            orderCheck.Checked += (_, _) => OrderChanged(true);
            orderCheck.Unchecked += (_, _) => OrderChanged(false);
            void OrderChanged(bool sequential)
            {
                s.SequentialOrder = sequential;
                // The tick is remembered as the default for the next send (design §3.3).
                try
                {
                    TDPdf.Properties.Settings.Default.DocumensoDefaultSigningOrder = sequential;
                    TDPdf.Properties.Settings.Default.Save();
                }
                catch { /* a preference; the send itself is unaffected */ }
                RebuildRecipients();
                Refresh();
            }

            placeBtn.Click += (_, _) =>
            {
                outcome = Outcome.PlaceFields;
                win.Close();
            };

            void SetBusy(bool on)
            {
                busy = on;
                body.IsEnabled = !on;
                sendBtn.IsEnabled = !on;
                cancelBtn.Content = on ? "Stop" : "Cancel";
                win.Cursor = on ? Cursors.AppStarting : null;
                Refresh();
            }

            sendBtn.Click += async (_, _) =>
            {
                if (busy) return;
                showErrors = true;
                var check = DocumensoSendPlan.Check(s, inputs.Placeholders, inputs.PageHasOffsetOrigin);
                Refresh();
                if (!check.CanSend)
                {
                    ShowStatus(check.Errors.Count == 1 ? "Fix the problem above to send." : $"Fix the {check.Errors.Count} problems above to send.", "DangerRed");
                    return;
                }

                cts = new CancellationTokenSource();
                SetBusy(true);
                ShowStatus("Sending…  (Esc to cancel)", "TextSecondary");
                var progress = new Progress<string>(text => ShowStatus(text, "TextSecondary"));
                SendAttempt attempt;
                try
                {
                    attempt = await inputs.SendAsync(progress, cts.Token);
                }
                catch (Exception)
                {
                    // SendAsync promises not to throw; this keeps the dialog alive if it ever does.
                    attempt = new SendAttempt(false, "Something went wrong. Nothing was sent.", Array.Empty<ESignSigningLink>(), 0);
                }
                SetBusy(false);
                cts.Dispose();
                cts = null;

                if (attempt.Ok)
                {
                    outcome = Outcome.Sent;
                    sent = attempt;
                    win.Close();
                }
                else
                {
                    ShowStatus(attempt.Error ?? "Nothing was sent.", "DangerRed");
                }
            };

            cancelBtn.Click += (_, _) =>
            {
                if (busy) { cts?.Cancel(); ShowStatus("Stopping…", "TextSecondary"); return; }
                win.Close();
            };
            closeBtn.Click += (_, _) =>
            {
                if (busy) { cts?.Cancel(); return; }
                win.Close();
            };
            win.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                if (busy) { cts?.Cancel(); ShowStatus("Stopping…", "TextSecondary"); }
                else win.Close();
            };
            // A send in flight finishes (or rolls back) before the window may go: closing under it
            // would leave the user not knowing whether anything went out.
            win.Closing += (_, e) =>
            {
                if (!busy) return;
                e.Cancel = true;
                cts?.Cancel();
            };

            RebuildRecipients();
            Refresh();
            win.Loaded += (_, _) =>
            {
                if (s.Recipients.Count == 0) addBtn.Focus();
                else titleBox.Focus();
            };
            win.ShowDialog();
            return (outcome, sent);
        }

        /// <summary>
        /// After a successful send: who it went to, and a Copy signing link per recipient.
        /// </summary>
        /// <remarks>
        /// A signing link lets whoever holds it sign as that recipient, so it is shown as a button,
        /// not as text, and the window says so. It is never logged.
        /// </remarks>
        internal static void ShowSent(Window? owner, string host, SendAttempt result)
        {
            var (win, root, closeBtn) = CreateShell(owner, "Sent for Electronic Signature", 480);
            var body = new StackPanel { Margin = new Thickness(20, 14, 20, 6) };
            root.Children.Add(body);

            int n = result.RecipientCount;
            body.Children.Add(Para($"Sent to {n} recipient{(n == 1 ? "" : "s")} for signature. {host} emails each of them a link.",
                "TextPrimary", 13, new Thickness(0, 0, 0, 10)));

            var feedback = Para("", "AccentGreen", 12, new Thickness(0, 6, 0, 0));
            if (result.Links.Count > 0)
            {
                body.Children.Add(Para("To hand someone their link yourself:", "TextSecondary", 12, new Thickness(0, 0, 0, 6)));
                foreach (var link in result.Links)
                {
                    var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var label = Para(link.Label, "TextPrimary", 12, new Thickness(0));
                    label.VerticalAlignment = VerticalAlignment.Center;
                    label.TextTrimming = TextTrimming.CharacterEllipsis;
                    label.TextWrapping = TextWrapping.NoWrap;
                    var copy = MakeButton(owner, "Copy signing link", primary: false);
                    System.Windows.Automation.AutomationProperties.SetName(copy, $"Copy signing link for {link.Label}");
                    copy.Click += (_, _) =>
                    {
                        try
                        {
                            Clipboard.SetText(link.SigningUrl);
                            feedback.Foreground = Brush("AccentGreen");
                            feedback.Text = $"Copied {link.Label}'s signing link.";
                        }
                        catch
                        {
                            // The clipboard is shared; another process may be holding it open.
                            feedback.Foreground = Brush("DangerRed");
                            feedback.Text = "The clipboard is busy. Try again.";
                        }
                    };
                    Grid.SetColumn(copy, 1);
                    row.Children.Add(label);
                    row.Children.Add(copy);
                    body.Children.Add(row);
                }
                body.Children.Add(Para("A signing link lets whoever has it sign as that person — give it only to them.",
                    "WarningOrange", 11, new Thickness(0, 6, 0, 0)));
                body.Children.Add(feedback);
            }

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 10, 20, 16) };
            var ok = MakeButton(owner, "Close", primary: true);
            ok.IsDefault = true;
            ok.IsCancel = true;
            ok.Click += (_, _) => win.Close();
            footer.Children.Add(ok);
            root.Children.Add(footer);
            closeBtn.Click += (_, _) => win.Close();
            win.ShowDialog();
        }

        // ── Helpers ────────────────────────────────────────────────────────────────────────

        private static Grid RowGrid(bool showMove)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // swatch
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });                   // number
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });// email
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });                   // role
            if (showMove)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // remove
            return g;
        }

        private static void AddCell(Grid g, UIElement e, int column)
        {
            Grid.SetColumn(e, Math.Min(column, g.ColumnDefinitions.Count - 1));
            g.Children.Add(e);
        }

        private static FrameworkElement SwatchLine(int index, string text)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            line.Children.Add(new Rectangle { Width = 10, Height = 10, Fill = RecipientBrush(index), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(Para(text, "TextPrimary", 12, new Thickness(0)));
            return line;
        }

        private static string Ordinal(int n) => n switch
        {
            1 => "first", 2 => "second", 3 => "third", 4 => "fourth", 5 => "fifth",
            _ => $"{n}th",
        };
    }
}
