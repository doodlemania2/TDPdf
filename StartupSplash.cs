using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TDPdf
{
    /// <summary>
    /// Lightweight startup splash shown the instant an interactive launch
    /// begins, so the user gets immediate feedback while <see cref="MainWindow"/>
    /// — which has a heavy first render — is constructed.
    ///
    /// The splash runs on its OWN dedicated UI thread with its own
    /// <see cref="Dispatcher"/>. That is the whole point: it stays painted and
    /// its progress animation keeps moving even while the main UI thread is
    /// blocked inside the MainWindow constructor / first layout pass. A splash
    /// shown on the main thread would freeze during exactly that slow window.
    ///
    /// Lifetime: <see cref="Close"/> is wired to <c>MainWindow.ContentRendered</c>
    /// so the splash disappears the moment the real window has painted. A hard
    /// max-lifetime timer on the splash thread is a belt-and-braces fallback so
    /// a crash or hang during window construction can never strand it on screen.
    /// </summary>
    internal sealed class StartupSplash
    {
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly object _gate = new();
        private Dispatcher? _dispatcher;
        private Window? _window;
        private bool _closed;

        // Brand palette is intentionally hard-coded here rather than pulled from
        // the themed ResourceDictionaries (BgPanel / AccentGreen / ...): the
        // splash lives on a separate UI thread and must not touch
        // Application.Current.Resources, which are owned by the main thread.
        // A splash is brand-colored, not theme-colored anyway — it shows the
        // same TDPdf identity regardless of Dark/Light/HighContrast.
        private static readonly Color PanelColor  = Color.FromRgb(0x14, 0x16, 0x1B); // near-black card
        private static readonly Color AccentColor = Color.FromRgb(0x1E, 0xA5, 0x4C); // AccentGreen
        private static readonly Color MutedColor  = Color.FromRgb(0xA0, 0xA0, 0xA0); // secondary text

        /// <summary>Spin up the splash on its own STA UI thread and return immediately.</summary>
        public static StartupSplash Show()
        {
            var splash = new StartupSplash();
            var t = new Thread(splash.ThreadProc)
            {
                IsBackground = true, // never keep the process alive on its own
                Name = "TDPdf Splash",
            };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return splash;
        }

        private void ThreadProc()
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                _window = BuildWindow();
                _window.Show();

                // Failsafe: if ContentRendered never fires (e.g. MainWindow
                // construction throws), force the splash down anyway.
                var failsafe = new DispatcherTimer(
                    TimeSpan.FromSeconds(12), DispatcherPriority.Normal,
                    (_, _) => CloseOnSplashThread(), _dispatcher);
                failsafe.Start();

                _ready.Set();
                Dispatcher.Run();
            }
            catch
            {
                // The splash is pure nicety — never let it take down startup.
                _ready.Set();
            }
        }

        /// <summary>Begin closing the splash (safe to call from any thread, including before it has finished showing).</summary>
        public void Close()
        {
            // Bounded wait so we never deadlock if the splash thread is still
            // spinning up. If it never readies there is nothing to close.
            _ready.Wait(2000);
            var d = _dispatcher;
            if (d == null) return;
            d.BeginInvoke(new Action(CloseOnSplashThread));
        }

        private void CloseOnSplashThread()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
            }

            var d = _dispatcher;
            var w = _window;
            if (w == null) { d?.InvokeShutdown(); return; }

            // Brief fade-out, then close the window and end this thread's
            // message loop so the thread exits cleanly.
            var fade = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(160));
            fade.Completed += (_, _) =>
            {
                try { w.Close(); }
                finally { d?.InvokeShutdown(); }
            };
            w.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private static Window BuildWindow()
        {
            var content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // Rounded logo tile (reuses the brand crest shipped as a resource).
            var logo = TryLoadLogo();
            if (logo != null)
            {
                var brush = new ImageBrush(logo) { Stretch = Stretch.UniformToFill };
                brush.Freeze();
                content.Children.Add(new Border
                {
                    Width = 76,
                    Height = 76,
                    CornerRadius = new CornerRadius(16),
                    Background = brush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
            }

            content.Children.Add(new TextBlock
            {
                Text = "TDPdf",
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 0),
            });

            content.Children.Add(new TextBlock
            {
                Text = "Loading…",
                FontSize = 12,
                Foreground = new SolidColorBrush(MutedColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            });

            content.Children.Add(new ProgressBar
            {
                IsIndeterminate = true,
                Width = 180,
                Height = 3,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(AccentColor),
                Margin = new Thickness(0, 18, 0, 0),
            });

            var card = new Border
            {
                Background = new SolidColorBrush(PanelColor),
                CornerRadius = new CornerRadius(14),
                BorderBrush = new SolidColorBrush(AccentColor),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(40, 34, 40, 30),
                Child = content,
                // Soft drop shadow. The outer margin below gives it room to
                // render without being clipped by the (content-sized) window.
                Effect = new DropShadowEffect
                {
                    BlurRadius = 26,
                    ShadowDepth = 0,
                    Opacity = 0.55,
                    Color = Colors.Black,
                },
            };

            var root = new Grid { Margin = new Thickness(26) };
            root.Children.Add(card);

            return new Window
            {
                Title = "TDPdf",
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,   // don't steal focus from the main window
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = root,
            };
        }

        private static ImageSource? TryLoadLogo()
        {
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri("pack://application:,,,/Resources/splash-logo.png", UriKind.Absolute);
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.DecodePixelWidth = 160; // displayed at 76px; decode small
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch
            {
                return null; // logo is optional; text + spinner still inform the user
            }
        }
    }
}
