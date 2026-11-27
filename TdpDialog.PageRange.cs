// TdpDialog — page-range dialogs: Extract Pages, Delete Pages, and the cancellable progress window
// Extract Pages shows while it writes. Same shell and button chrome as the other TdpDialog prompts.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using TDPdf.Services;

namespace TDPdf
{
    /// <summary>What the Extract Pages dialog asked for.</summary>
    internal sealed record ExtractChoice(List<int> Pages, ExtractMode Mode, int EveryN, string Folder);

    internal static partial class TdpDialog
    {
        private static Button MakeThemedButton(string label, bool accent)
        {
            var bgNorm = accent ? Brush("AccentGreenDim") : Brush("BgPanel");
            var bgHov = accent ? Brush("BgPressed") : Brush("BgHover");
            var btn = new Button
            {
                Content = label,
                Padding = new Thickness(18, 6, 18, 6),
                Margin = new Thickness(8, 0, 0, 0),
                Background = bgNorm,
                Foreground = accent ? Brush("AccentGreen") : Brush("TextPrimary"),
                BorderBrush = accent ? Brush("AccentGreen") : Brush("BorderDim"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                FontSize = 12,
                Template = MakeBtnTemplate(),
            };
            btn.MouseEnter += (_, _2) => { if (btn.IsEnabled) btn.Background = bgHov; };
            btn.MouseLeave += (_, _2) => btn.Background = bgNorm;
            return btn;
        }

        private static TextBox MakeThemedBox(string text, string automationName)
        {
            var box = new TextBox
            {
                Text = text,
                FontSize = 12,
                Background = Brush("BgPanel"),
                Foreground = Brush("TextPrimary"),
                BorderBrush = Brush("BorderDim"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 5, 6, 5),
                CaretBrush = Brush("TextPrimary"),
            };
            UseThemedTextMenu(box);
            AutomationProperties.SetName(box, automationName);
            return box;
        }

        private static TextBlock MakeLabel(string text, bool secondary = false) => new()
        {
            Text = text,
            Foreground = Brush(secondary ? "TextSecondary" : "TextPrimary"),
            FontSize = secondary ? 11 : 12,
            TextWrapping = TextWrapping.Wrap,
        };

        private static Border Pad(UIElement child, double top = 0, double bottom = 4) =>
            new() { Padding = new Thickness(20, top, 20, bottom), Child = child };

        /// <summary>
        /// Extract Pages: which pages (a typed list such as 1-3,7), whether they go into one PDF, one
        /// PDF per page, or one PDF every N pages, and the folder to write them in. Null when cancelled.
        /// </summary>
        public static ExtractChoice? ShowExtractPages(Window? owner, int pageCount, string initialPages, string initialFolder)
        {
            ExtractChoice? result = null;
            var (win, root) = CreateShell(owner, "Extract Pages");
            win.Width = 440 * CurrentScale(owner);

            root.Children.Add(Pad(MakeLabel($"Pages to extract (this document has {pageCount}):"), top: 16));
            var pagesBox = MakeThemedBox(initialPages, "Pages to extract");
            root.Children.Add(Pad(pagesBox));
            root.Children.Add(Pad(MakeLabel("For example 1-3,7", secondary: true), bottom: 10));

            RadioButton Radio(string label, bool on)
            {
                var rb = new RadioButton
                {
                    Content = label, IsChecked = on, GroupName = "extractMode",
                    Foreground = Brush("TextPrimary"), FontSize = 12, Margin = new Thickness(0, 2, 0, 2),
                };
                AutomationProperties.SetName(rb, label);
                return rb;
            }
            var oneFile = Radio("One PDF with all of these pages", true);
            var perPage = Radio("One PDF for each page", false);
            var everyN = Radio("One PDF every", false);
            var nBox = MakeThemedBox("5", "Pages per PDF");
            nBox.Width = 48;
            nBox.IsEnabled = false;
            everyN.Checked += (_, _2) => nBox.IsEnabled = true;
            everyN.Unchecked += (_, _2) => nBox.IsEnabled = false;
            var everyRow = new StackPanel { Orientation = Orientation.Horizontal };
            everyRow.Children.Add(everyN);
            everyRow.Children.Add(new Border { Margin = new Thickness(6, 0, 6, 0), Child = nBox });
            everyRow.Children.Add(new TextBlock { Text = "pages", Foreground = Brush("TextPrimary"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var modes = new StackPanel();
            modes.Children.Add(oneFile);
            modes.Children.Add(perPage);
            modes.Children.Add(everyRow);
            root.Children.Add(Pad(modes, bottom: 10));

            root.Children.Add(Pad(MakeLabel("Save in this folder:")));
            var folderBox = MakeThemedBox(initialFolder, "Folder");
            var browse = MakeThemedButton("Browse...", accent: false);
            browse.Click += (_, _2) =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Save extracted pages in", InitialDirectory = folderBox.Text };
                if (dlg.ShowDialog(win) == true) folderBox.Text = dlg.FolderName;
            };
            var folderRow = new Grid();
            folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            folderRow.Children.Add(folderBox);
            Grid.SetColumn(browse, 1);
            folderRow.Children.Add(browse);
            root.Children.Add(Pad(folderRow));
            root.Children.Add(Pad(MakeLabel("Files are named after the document and never replace one that already exists.", secondary: true), bottom: 6));

            var error = new TextBlock { Foreground = Brush("DangerRed"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            root.Children.Add(Pad(error, bottom: 0));

            var okBtn = MakeThemedButton("Extract", accent: true);
            okBtn.IsDefault = true;
            var cancelBtn = MakeThemedButton("Cancel", accent: false);
            cancelBtn.IsCancel = true;
            cancelBtn.Click += (_, _2) => win.Close();
            okBtn.Click += (_, _2) =>
            {
                string? problem = null;
                if (!PageRangeSpec.TryParse(pagesBox.Text, pageCount, out var pages, out var perr)) problem = perr;
                int n = 0;
                if (problem is null && everyN.IsChecked == true &&
                    (!int.TryParse(nBox.Text.Trim(), out n) || n < 1))
                    problem = "Type how many pages go in each PDF (1 or more).";
                string folder = folderBox.Text.Trim();
                if (problem is null && (folder.Length == 0 || !System.IO.Directory.Exists(folder)))
                    problem = "Choose a folder that exists.";
                if (problem is not null)
                {
                    error.Text = problem;
                    error.Visibility = Visibility.Visible;
                    return;
                }
                var mode = perPage.IsChecked == true ? ExtractMode.OnePerPage
                         : everyN.IsChecked == true ? ExtractMode.EveryN : ExtractMode.OneFile;
                result = new ExtractChoice(pages, mode, n, folder);
                win.Close();
            };
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(new Border { Padding = new Thickness(16, 12, 16, 16), Child = btnPanel });

            win.Loaded += (_, _2) => { pagesBox.Focus(); pagesBox.SelectAll(); };
            win.ShowDialog();
            return result;
        }

        /// <summary>
        /// Delete Pages: a typed page list, and one button that says exactly what it will do
        /// ("Delete 3 pages"). It stays off for a list that is not valid and for a list that would
        /// delete every page. Null when cancelled.
        /// </summary>
        public static List<int>? ShowDeletePages(Window? owner, int pageCount, string initialPages)
        {
            List<int>? result = null;
            var (win, root) = CreateShell(owner, "Delete Pages");

            root.Children.Add(Pad(MakeLabel($"Pages to delete (this document has {pageCount}):"), top: 16));
            var pagesBox = MakeThemedBox(initialPages, "Pages to delete");
            root.Children.Add(Pad(pagesBox));
            var hint = MakeLabel("For example 1-3,7", secondary: true);
            root.Children.Add(Pad(hint, bottom: 6));

            var okBtn = MakeThemedButton("Delete pages", accent: true);
            okBtn.IsDefault = true;
            var cancelBtn = MakeThemedButton("Cancel", accent: false);
            cancelBtn.IsCancel = true;
            cancelBtn.Click += (_, _2) => win.Close();
            var pending = new List<int>();

            void Refresh()
            {
                bool valid = PageRangeSpec.TryParse(pagesBox.Text, pageCount, out var pages, out var err);
                pending = pages;
                if (valid && pages.Count >= pageCount)
                {
                    valid = false;
                    err = "A PDF must keep at least one page. To discard the whole document, close it instead.";
                }
                okBtn.IsEnabled = valid;
                okBtn.Content = valid ? $"Delete {pages.Count} {(pages.Count == 1 ? "page" : "pages")}" : "Delete pages";
                bool empty = string.IsNullOrWhiteSpace(pagesBox.Text);
                hint.Text = valid ? "The pages can be brought back with Undo."
                          : empty ? "For example 1-3,7" : err;
                hint.Foreground = Brush(valid || empty ? "TextSecondary" : "DangerRed");
            }
            pagesBox.TextChanged += (_, _2) => Refresh();
            okBtn.Click += (_, _2) =>
            {
                if (!okBtn.IsEnabled) return;
                result = pending;
                win.Close();
            };
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(new Border { Padding = new Thickness(16, 12, 16, 16), Child = btnPanel });
            Refresh();

            win.Loaded += (_, _2) => { pagesBox.Focus(); pagesBox.SelectAll(); };
            win.ShowDialog();
            return result;
        }

        /// <summary>
        /// Runs <paramref name="work"/> off the UI thread behind a small window that shows its
        /// progress text and a Cancel button (Esc does the same). Returns true when the work finished
        /// and false when it was cancelled; the work reports through the progress callback and must
        /// watch the token. An exception from the work is rethrown to the caller.
        /// </summary>
        public static async Task<bool> RunCancellable(Window? owner, string title,
            Func<IProgress<string>, CancellationToken, Task> work)
        {
            var (win, root) = CreateShell(owner, title);
            var cts = new CancellationTokenSource();
            var status = MakeLabel("Starting...");
            root.Children.Add(Pad(status, top: 16, bottom: 6));
            root.Children.Add(Pad(MakeLabel("Press Esc to cancel.", secondary: true), bottom: 6));
            var cancelBtn = MakeThemedButton("Cancel", accent: false);
            cancelBtn.IsCancel = true;
            cancelBtn.Click += (_, _2) =>
            {
                cts.Cancel();
                cancelBtn.IsEnabled = false;
                status.Text = "Cancelling...";
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(cancelBtn);
            root.Children.Add(new Border { Padding = new Thickness(16, 8, 16, 16), Child = buttons });

            var progress = new Progress<string>(s => { if (!cts.IsCancellationRequested) status.Text = s; });
            bool finished = false;
            win.Closing += (_, e) =>
            {
                // Closing is only allowed once the work is over; anything else is a cancel request.
                if (!finished) { e.Cancel = true; cts.Cancel(); cancelBtn.IsEnabled = false; status.Text = "Cancelling..."; }
            };
            var task = Task.Run(() => work(progress, cts.Token));
            _ = task.ContinueWith(_ => win.Dispatcher.BeginInvoke(new Action(() => { finished = true; win.Close(); })),
                TaskScheduler.Default);
            win.ShowDialog();
            try
            {
                await task;
                return !cts.IsCancellationRequested;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
