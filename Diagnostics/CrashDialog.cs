using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TDPdf.Diagnostics
{
    public sealed class CrashDialog : Window
    {
        private static int _showing;
        private readonly CrashReport _report;
        private bool _continueRequested;

        private CrashDialog(CrashReport report)
        {
            _report = report;
            Title = "TDPdf crash report";
            Width = 720;
            Height = 520;
            MinWidth = 560;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            Background = Brush(0x1a, 0x1a, 0x1a);
            Foreground = Brushes.White;
            Content = BuildContent();
        }

        public bool ContinueRequested => _continueRequested;

        public static bool ShowCrash(CrashReport report)
        {
            if (Interlocked.Exchange(ref _showing, 1) != 0)
                return false;

            try
            {
                if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                    return ShowCrashOnCurrentThread(report);

                bool shouldContinue = false;
                var thread = new Thread(() => shouldContinue = ShowCrashOnCurrentThread(report));
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                thread.Join();
                return shouldContinue;
            }
            finally
            {
                Interlocked.Exchange(ref _showing, 0);
            }
        }

        private static bool ShowCrashOnCurrentThread(CrashReport report)
        {
            try
            {
                var dialog = new CrashDialog(report);
                dialog.ShowDialog();
                return dialog.ContinueRequested;
            }
            catch
            {
                return false;
            }
        }

        private UIElement BuildContent()
        {
            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = _report.Recoverable ? "TDPdf hit an error" : "TDPdf must close",
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Foreground = Brush(0x4a, 0xde, 0x80),
                Margin = new Thickness(0, 0, 0, 8)
            };
            root.Children.Add(title);

            var summary = new TextBlock
            {
                Text = _report.Summary,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(summary, 1);
            root.Children.Add(summary);

            var details = new Expander
            {
                Header = "Details",
                IsExpanded = true,
                Foreground = Brushes.White,
                Background = Brush(0x25, 0x25, 0x25),
                Content = new TextBox
                {
                    Text = _report.Details,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    AcceptsTab = true,
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Background = Brush(0x10, 0x10, 0x10),
                    Foreground = Brushes.White,
                    BorderBrush = Brush(0x44, 0x44, 0x44)
                }
            };
            Grid.SetRow(details, 2);
            root.Children.Add(details);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };

            buttons.Children.Add(MakeButton("Copy report", CopyReport, true));
            buttons.Children.Add(MakeButton("Open log folder", OpenLogFolder, true));
            buttons.Children.Add(MakeButton("Continue", Continue, _report.Recoverable));
            buttons.Children.Add(MakeButton("Quit", Quit, true));

            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            return root;
        }

        private Button MakeButton(string text, RoutedEventHandler handler, bool isEnabled)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 96,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(10, 6, 10, 6),
                IsEnabled = isEnabled,
                Background = text == "Quit" ? Brush(0xc4, 0x2b, 0x1c) : Brush(0x30, 0x30, 0x30),
                Foreground = Brushes.White,
                BorderBrush = Brush(0x55, 0x55, 0x55),
                Cursor = Cursors.Hand
            };
            button.Click += handler;
            return button;
        }

        private void CopyReport(object sender, RoutedEventArgs e)
        {
            try
            {
                string logPath = _report.LogPath ?? "(log file unavailable)";
                Clipboard.SetText($"Log: {logPath}{Environment.NewLine}{_report.Summary}");
            }
            catch
            {
            }
        }

        private void OpenLogFolder(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(_report.LogDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = _report.LogDirectory,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }

        private void Continue(object sender, RoutedEventArgs e)
        {
            if (!_report.Recoverable)
                return;

            _continueRequested = true;
            Close();
        }

        private void Quit(object sender, RoutedEventArgs e)
        {
            try
            {
                var application = Application.Current;
                if (application?.Dispatcher is not null && !application.Dispatcher.CheckAccess())
                    application.Dispatcher.BeginInvoke(new Action(() => application.Shutdown(1)));
                else
                    application?.Shutdown(1);
            }
            catch
            {
            }

            Close();
        }

        private static SolidColorBrush Brush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
