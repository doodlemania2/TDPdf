using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow
    {
        // ============================================================
        // File attachments        (#224)
        // ============================================================
        //
        // File ▸ Attachments… lists the files embedded in the open PDF (the embedded-files list and
        // file-attachment annotations) and saves one or all of them. Nothing here opens an attached
        // file: a PDF decides the name and the type, so the only thing done with the bytes is to write
        // them under a safe name, mark them as downloaded (Zone.Identifier) and, for programs and
        // scripts, ask first. Names, types and the list walk live in Services (AttachmentNames,
        // PdfAttachments), where they are tested. Read-only: the document and its dirty flag are untouched.

        private void Attachments_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();
            IReadOnlyList<AttachmentEntry> entries;
            try { entries = PdfAttachments.List(_doc); }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"The attachments could not be read:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (entries.Count == 0) { TdpDialog.Show(this, "This PDF has no attached files.", "Attachments"); return; }
            ShowAttachmentsDialog(entries);
        }

        private static string DescribeSize(long? bytes)
        {
            if (bytes is not { } b) return "unknown size";
            if (b < 1024) return $"{b} bytes";
            if (b < 1024 * 1024) return $"{b / 1024.0:0.#} KB";
            if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024):0.#} MB";
            return $"{b / (1024.0 * 1024 * 1024):0.##} GB";
        }

        private void ShowAttachmentsDialog(IReadOnlyList<AttachmentEntry> entries)
        {
            var bgPanel       = BrushResource("BgPanel");
            var borderDim     = BrushResource("BorderDim");
            var textPrimary   = BrushResource("TextPrimary");
            var textSecondary = BrushResource("TextSecondary");
            var accent        = BrushResource("AccentGreen");
            var accentDim     = BrushResource("AccentGreenDim");
            var danger        = BrushResource("DangerRed");

            var (win, shellBody) = TdpDialog.CreateShell(this, "Attachments", 520);
            win.ShowInTaskbar = false;
            win.Foreground = textPrimary;
            win.FontFamily = new FontFamily("Segoe UI");
            win.FontSize = 12;

            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock
            {
                Text = $"{entries.Count} attached file{(entries.Count == 1 ? "" : "s")}. They are only saved, never opened from here.",
                Foreground = textSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });

            // Selection look drawn from theme brushes (the stock ListBoxItem highlight is system blue).
            var itemStyle = new Style(typeof(ListBoxItem));
            var template = new ControlTemplate(typeof(ListBoxItem));
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Bd";
            border.SetValue(Border.PaddingProperty, new Thickness(8, 6, 8, 6));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
            border.SetValue(Border.BorderBrushProperty, borderDim);
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            template.VisualTree = border;
            var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Border.BackgroundProperty, accentDim, "Bd"));
            template.Triggers.Add(selected);
            itemStyle.Setters.Add(new Setter(Control.TemplateProperty, template));

            var list = new ListBox
            {
                Background = bgPanel, BorderBrush = borderDim, BorderThickness = new Thickness(1),
                Foreground = textPrimary, Height = 230, ItemContainerStyle = itemStyle,
                SelectionMode = SelectionMode.Extended,
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);

            foreach (var entry in entries)
            {
                var (safe, renamed) = AttachmentNames.Sanitise(entry.Name);
                bool risky = AttachmentNames.IsRisky(entry.Name);
                var row = new StackPanel();
                var title = new StackPanel { Orientation = Orientation.Horizontal };
                title.Children.Add(new TextBlock { Text = safe, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                if (risky)
                    title.Children.Add(new TextBlock { Text = "  program or script", Foreground = danger, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(title);

                var facts = new List<string> { DescribeSize(entry.Size), entry.Source };
                if (entry.Modified is { } m) facts.Add(m.ToLocalTime().ToString("d"));
                row.Children.Add(new TextBlock { Text = string.Join("  |  ", facts), Foreground = textSecondary, FontSize = 11 });
                if (!string.IsNullOrWhiteSpace(entry.Description))
                    row.Children.Add(new TextBlock { Text = entry.Description, Foreground = textSecondary, FontSize = 11, TextWrapping = TextWrapping.Wrap });
                if (renamed)
                    row.Children.Add(new TextBlock { Text = "Saved under a safe name; the PDF calls it \"" + entry.Name + "\".", Foreground = textSecondary, FontSize = 11, TextWrapping = TextWrapping.Wrap });
                list.Items.Add(new ListBoxItem { Content = row, Tag = entry });
            }
            list.SelectedIndex = 0;
            root.Children.Add(list);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            Button MakeButton(string text, double width, bool primary = false) => new()
            {
                Content = text, Width = width, Height = 30, Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
                Background = primary ? accent : bgPanel, Foreground = primary ? Brushes.White : textPrimary,
                BorderBrush = primary ? accent : borderDim, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            };
            var saveBtn = MakeButton("Save…", 90);
            var saveAllBtn = MakeButton("Save All…", 100, primary: true);
            var closeBtn = MakeButton("Close", 80);
            closeBtn.IsCancel = true;
            buttons.Children.Add(saveBtn);
            buttons.Children.Add(saveAllBtn);
            buttons.Children.Add(closeBtn);
            root.Children.Add(buttons);
            shellBody.Children.Add(root);

            saveBtn.Click += (_, _) =>
            {
                var picked = list.SelectedItems.OfType<ListBoxItem>().Select(i => (AttachmentEntry)i.Tag).ToList();
                if (picked.Count == 0) return;
                if (picked.Count == 1) SaveOneAttachment(win, picked[0]);
                else SaveAttachmentsToFolder(win, picked);
            };
            saveAllBtn.Click += (_, _) => SaveAttachmentsToFolder(win, entries);
            closeBtn.Click += (_, _) => win.DialogResult = false;
            list.MouseDoubleClick += (_, _) => saveBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            win.Loaded += (_, _) => list.Focus();
            win.ShowDialog();
        }

        /// <summary>True when it is fine to go on: nothing risky, or the user accepted the warning.</summary>
        private bool ConfirmRiskyAttachments(Window owner, IReadOnlyList<AttachmentEntry> risky)
        {
            if (risky.Count == 0) return true;
            string what = risky.Count == 1
                ? $"\"{AttachmentNames.Sanitise(risky[0].Name).Name}\" is a program or script."
                : $"{risky.Count} of these files are programs or scripts (for example \"{AttachmentNames.Sanitise(risky[0].Name).Name}\").";
            return TdpDialog.ShowYesNo(owner,
                what + "\n\nTDPdf will only save it, never run it, and Windows will treat it as a download from the internet. Only open it if you trust where this PDF came from.",
                "Save anyway", "Cancel", "Attachments", MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private void SaveOneAttachment(Window owner, AttachmentEntry entry)
        {
            if (!ConfirmRiskyAttachments(owner, AttachmentNames.IsRisky(entry.Name) ? [entry] : [])) return;
            var dlg = new SaveFileDialog
            {
                Title = "Save Attachment",
                FileName = AttachmentNames.Sanitise(entry.Name).Name,
                OverwritePrompt = true,   // never replace a file without asking
                CheckPathExists = true,
            };
            string ext = Path.GetExtension(dlg.FileName);
            if (ext.Length > 1) dlg.Filter = $"{ext[1..].ToUpperInvariant()} file|*{ext}|All files|*.*";
            if (!FileDialogs.ShowSave(dlg, FileDialogPurpose.Attachment, owner)) return;
            try
            {
                var bytes = entry.ReadBytes();
                File.WriteAllBytes(dlg.FileName, bytes);
                AttachmentNames.TryMarkDownloaded(dlg.FileName);
                SetStatus($"Saved attachment {Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(owner, $"The attachment could not be saved:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveAttachmentsToFolder(Window owner, IReadOnlyList<AttachmentEntry> entries)
        {
            var folderDlg = new OpenFolderDialog { Title = "Save attachments in" };
            if (!FileDialogs.ShowFolder(folderDlg, FileDialogPurpose.AttachmentsFolder, owner)) return;
            string folder = folderDlg.FolderName;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            var risky = entries.Where(en => AttachmentNames.IsRisky(en.Name)).ToList();
            if (!ConfirmRiskyAttachments(owner, risky)) return;

            // Names for this batch: safe, and different from one another (a PDF can attach two files
            // under one name).
            var plan = new List<(AttachmentEntry Entry, string Name)>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var en in entries)
            {
                string n = AttachmentNames.Unique(AttachmentNames.Sanitise(en.Name).Name, used.Contains);
                used.Add(n);
                plan.Add((en, n));
            }

            var existing = plan.Where(p => File.Exists(Path.Combine(folder, p.Name))).ToList();
            bool replace = false;
            if (existing.Count > 0)
            {
                string names = string.Join("\n", existing.Take(5).Select(p => "  " + p.Name));
                string more = existing.Count > 5 ? $"\n  ...and {existing.Count - 5} more" : string.Empty;
                var answer = TdpDialog.ShowYesNoCancel(owner,
                    $"{(existing.Count == 1 ? "1 file already exists" : $"{existing.Count} files already exist")} in {DescribeFolder(folder)}:\n{names}{more}",
                    "Replace", "Keep both", "Cancel", "Attachments", MessageBoxImage.Warning, defaultToCancel: true);
                if (answer == MessageBoxResult.Cancel) return;
                replace = answer == MessageBoxResult.Yes;
            }

            int saved = 0;
            var failures = new List<string>();
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                foreach (var (entry, name) in plan)
                {
                    try
                    {
                        string target = Path.Combine(folder, name);
                        if (!replace && File.Exists(target))
                            target = Path.Combine(folder, AttachmentNames.Unique(name, n => File.Exists(Path.Combine(folder, n))));
                        File.WriteAllBytes(target, entry.ReadBytes());
                        AttachmentNames.TryMarkDownloaded(target);
                        saved++;
                    }
                    catch (Exception ex) { failures.Add($"{name}: {ex.Message}"); }
                }
            }
            finally { Mouse.OverrideCursor = null; }

            SetStatus($"Saved {saved} attachment{(saved == 1 ? "" : "s")} to {DescribeFolder(folder)}");
            if (failures.Count > 0)
                TdpDialog.Show(owner, $"{failures.Count} file{(failures.Count == 1 ? "" : "s")} could not be saved:\n" + string.Join("\n", failures.Take(5)),
                    "TDPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
