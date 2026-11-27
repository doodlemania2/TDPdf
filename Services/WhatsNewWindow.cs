using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace TDPdf.Services
{
    /// <summary>
    /// The themed "What's new in TDPdf" window: one or more versions' release notes, rendered from
    /// the Markdown embedded in the exe (see <see cref="ReleaseNotesMarkdown"/>), with the
    /// "Show what's new after updates" box. Custom chrome like the other TDPdf dialogs, but
    /// resizable, with a scrolling body.
    /// </summary>
    internal static class WhatsNewWindow
    {
        private static Brush B(string key) =>
            Application.Current?.TryFindResource(key) as Brush ?? SystemColors.WindowTextBrush;

        /// <summary>
        /// Shows the notes modally over <paramref name="owner"/>: each of <paramref name="versions"/>,
        /// newest first (after an update that skipped versions), or the running version's alone.
        /// </summary>
        internal static void Show(Window? owner, IReadOnlyList<Version>? versions = null)
        {
            var running = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var wanted = versions is { Count: > 0 } ? versions
                : running is not null ? new[] { running } : Array.Empty<Version>();
            var notes = wanted.Select(WhatsNewState.LoadEmbeddedNotes)
                .Where(m => m is not null).Select(m => m!).ToList();

            var win = new Window
            {
                Title = "What's new in TDPdf",
                Width = 560,
                Height = 640,
                MinWidth = 380,
                MinHeight = 320,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.CanResize,
                Background = B("BgDark"),
                Owner = owner,
                WindowStartupLocation = owner is not null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
            };
            WindowChrome.SetWindowChrome(win, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
            });

            var frame = new Border { BorderBrush = B("AccentGreenDim"), BorderThickness = new Thickness(1), Background = B("BgDark") };
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            frame.Child = root;
            win.Content = frame;

            // Title bar
            var titleBar = new Border { Background = B("BgPanel"), Padding = new Thickness(14, 8, 8, 8) };
            titleBar.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) { try { win.DragMove(); } catch (InvalidOperationException) { } }
            };
            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleGrid.Children.Add(new TextBlock
            {
                Text = "What's new in TDPdf",
                Foreground = B("AccentGreen"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var closeX = new Button
            {
                Content = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Width = 28,
                Height = 28,
                Background = Brushes.Transparent,
                Foreground = B("TextSecondary"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Close",
            };
            System.Windows.Automation.AutomationProperties.SetName(closeX, "Close");
            WindowChrome.SetIsHitTestVisibleInChrome(closeX, true);
            closeX.MouseEnter += (_, _) => closeX.Foreground = B("DangerRed");
            closeX.MouseLeave += (_, _) => closeX.Foreground = B("TextSecondary");
            closeX.Click += (_, _) => win.Close();
            Grid.SetColumn(closeX, 1);
            titleGrid.Children.Add(closeX);
            titleBar.Child = titleGrid;
            Grid.SetRow(titleBar, 0);
            root.Children.Add(titleBar);

            // Body
            var body = new StackPanel { Margin = new Thickness(22, 16, 22, 16) };
            if (notes.Count == 0)
            {
                body.Children.Add(Para("This build does not include release notes. They are published at https://tdpdf.com/changelog/."));
            }
            else
            {
                for (int i = 0; i < notes.Count; i++)
                {
                    var blocks = ReleaseNotesMarkdown.Parse(notes[i]);
                    for (int j = 0; j < blocks.Count; j++)
                    {
                        var element = Render(blocks[j]);
                        // Space each older version's notes off from the one above it.
                        if (i > 0 && j == 0 && element is FrameworkElement fe)
                            fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top + 28, fe.Margin.Right, fe.Margin.Bottom);
                        body.Children.Add(element);
                    }
                }
            }
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = body,
                Focusable = false,
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            // Footer
            var footer = new DockPanel { Margin = new Thickness(18, 10, 18, 14), LastChildFill = false };
            var closeBtn = new Button
            {
                Content = "Close",
                MinWidth = 88,
                Padding = new Thickness(14, 6, 14, 6),
                IsDefault = true,
                IsCancel = true,
                Background = B("BgHover"),
                Foreground = B("TextPrimary"),
                BorderBrush = B("BorderDim"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            closeBtn.Click += (_, _) => win.Close();
            DockPanel.SetDock(closeBtn, Dock.Right);
            footer.Children.Add(closeBtn);
            var auto = new CheckBox
            {
                Content = "Show what's new after updates",
                IsChecked = WhatsNewState.ShowAfterUpdates,
                Foreground = B("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            auto.Checked += (_, _) => WhatsNewState.ShowAfterUpdates = true;
            auto.Unchecked += (_, _) => WhatsNewState.ShowAfterUpdates = false;
            if (!WhatsNewState.IsAllowedByPolicy())
            {
                auto.IsEnabled = false;
                auto.ToolTip = "Your organisation turned off the automatic what's new window.";
            }
            DockPanel.SetDock(auto, Dock.Left);
            footer.Children.Add(auto);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
            win.ShowDialog();
        }

        private static TextBlock Para(string text) => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("TextPrimary"),
            Margin = new Thickness(0, 0, 0, 10),
        };

        private static UIElement Render(NotesBlock block)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = B("TextPrimary") };
            AddRuns(tb.Inlines, block.Runs);
            switch (block.Kind)
            {
                case NotesBlockKind.Title:
                    tb.FontSize = 22;
                    tb.FontWeight = FontWeights.SemiBold;
                    tb.Foreground = B("AccentGreen");
                    tb.Margin = new Thickness(0, 0, 0, 8);
                    break;
                case NotesBlockKind.Heading:
                    tb.FontSize = 15;
                    tb.FontWeight = FontWeights.SemiBold;
                    tb.Margin = new Thickness(0, 14, 0, 6);
                    System.Windows.Automation.AutomationProperties.SetHeadingLevel(tb, System.Windows.Automation.AutomationHeadingLevel.Level2);
                    break;
                case NotesBlockKind.Bullet:
                    var grid = new Grid { Margin = new Thickness(4, 0, 0, 5) };
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.Children.Add(new TextBlock { Text = "•", Foreground = B("AccentGreen") });
                    Grid.SetColumn(tb, 1);
                    grid.Children.Add(tb);
                    return grid;
                default:
                    tb.Foreground = B("TextSecondary");
                    tb.Margin = new Thickness(0, 0, 0, 8);
                    break;
            }
            return tb;
        }

        private static void AddRuns(InlineCollection inlines, IReadOnlyList<NotesRun> runs)
        {
            foreach (var r in runs)
            {
                var run = new Run(r.Text);
                if (r.Bold) run.FontWeight = FontWeights.SemiBold;
                if (r.Code)
                {
                    run.FontFamily = new FontFamily("Consolas");
                    run.Background = B("BgPanel");
                }
                inlines.Add(run);
            }
        }
    }
}
