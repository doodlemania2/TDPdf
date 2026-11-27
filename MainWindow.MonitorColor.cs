// MainWindow — match the monitor's colour profile on screen.
//
// PDFium renders sRGB. On a monitor whose profile is not sRGB (a wide-gamut panel, a calibrated
// display) those pixels look wrong, so the displayed bitmap is converted from sRGB to that profile.
// It is on by default and acts only when Windows reports a non-sRGB profile for the monitor the window
// is on; a plain sRGB monitor, an HDR monitor, or no profile means no pixel is touched at all (see
// Services/ColorMonitorPolicy). Settings has a switch to turn it off.
//
// Display only. The render cache and _primaryPageBitmap keep the document's true sRGB, because the
// image tool and OCR read them. The conversion happens at the display step: DisplayBitmap for the
// primary page (after the highlight multiply, before night mode) and on the freshly rendered bytes
// for tiles, the continuous strip and thumbnails. Print, export, flatten and redaction never see it.
//
// Re-checked at start-up, on WM_DISPLAYCHANGE, on a DPI change, when the window moves to another
// monitor, and when the window is activated (a profile may have been changed in Windows meanwhile).

using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        private readonly MonitorColorController _monitorColor = new();
        private long _monitorColorMonitor;

        private sealed class ColoredBitmap(int version, BitmapSource bitmap)
        {
            public readonly int Version = version;
            public readonly BitmapSource Bitmap = bitmap;
        }

        private readonly ConditionalWeakTable<BitmapSource, ColoredBitmap> _colorBitmaps = new();

        private static bool MatchMonitorColorSetting()
        {
            try { return TDPdf.Properties.Settings.Default.MatchMonitorColor; }
            catch { return true; }
        }

        private void InitMonitorColor()
        {
            LocationChanged += (_, _) => MaybeRefreshMonitorColorOnMove();
            Activated += (_, _) => RefreshMonitorColor();
            RefreshMonitorColor();
        }

        private void MaybeRefreshMonitorColorOnMove()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            long mon = ColorPlatform.MonitorOf(hwnd);
            if (mon == _monitorColorMonitor) return;
            RefreshMonitorColor();
        }

        /// <summary>Re-reads the monitor's profile and, if the screen's colours changed, redraws what is showing.</summary>
        private void RefreshMonitorColor()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                _monitorColorMonitor = ColorPlatform.MonitorOf(hwnd);
                if (!_monitorColor.Refresh(hwnd, MatchMonitorColorSetting())) return;

                _colorBitmaps.Clear();
                _invertedBitmaps.Clear();
                if (_doc is null) return;
                RefreshPageList(ThumbRefresh.Reset);
                RefreshInvertedView();
            }
            catch { /* colour matching is a nicety: never fail the window over it */ }
        }

        /// <summary>The page bitmap converted to the monitor's profile, memoised per bitmap; the bitmap itself when nothing applies.</summary>
        private BitmapSource MonitorColored(BitmapSource source)
        {
            if (!DisplayColor.IsActive) return source;
            int version = DisplayColor.Version;
            if (_colorBitmaps.TryGetValue(source, out var hit) && hit.Version == version) return hit.Bitmap;
            var made = ColoredCopy(source);
            _colorBitmaps.AddOrUpdate(source, new ColoredBitmap(version, made));
            return made;
        }

        private static BitmapSource ColoredCopy(BitmapSource source)
        {
            try
            {
                var bgra = source.Format == PixelFormats.Bgra32
                    ? source
                    : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                int w = bgra.PixelWidth, h = bgra.PixelHeight;
                long len = (long)w * 4 * h;
                if (w <= 0 || h <= 0 || len > int.MaxValue) return source;
                var pixels = new byte[len];
                bgra.CopyPixels(pixels, w * 4, 0);
                if (!DisplayColor.Apply(pixels, w, h)) return source;
                double dpiX = source.DpiX > 0 ? source.DpiX : 96.0;
                double dpiY = source.DpiY > 0 ? source.DpiY : 96.0;
                return PageRaster.ToFrozenBitmap(pixels, w, h, dpiX, dpiY) ?? source;
            }
            catch { return source; }
        }

        /// <summary>Settings: the switch and a line saying what is in effect on this monitor.</summary>
        private void AddColorSettingsSection(System.Windows.Controls.StackPanel panel)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Color",
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushResource("TextPrimary"),
                Margin = new Thickness(0, 0, 0, 8)
            });

            var summary = new System.Windows.Controls.TextBlock
            {
                Text = _monitorColor.Summary(),
                Foreground = BrushResource("TextSecondary"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(22, 0, 0, 12)
            };
            var match = new System.Windows.Controls.CheckBox
            {
                Content = "Match my monitor's color profile",
                IsChecked = MatchMonitorColorSetting(),
                Foreground = BrushResource("TextPrimary"),
                ToolTip = "Shows pages in their real colors on a monitor with its own color profile. Does nothing on a standard sRGB monitor, and never changes the file or what prints.",
                Margin = new Thickness(0, 0, 0, 4)
            };
            void Changed(object? s, RoutedEventArgs e)
            {
                try
                {
                    TDPdf.Properties.Settings.Default.MatchMonitorColor = match.IsChecked == true;
                    TDPdf.Properties.Settings.Default.Save();
                }
                catch { /* the choice still holds for this session */ }
                RefreshMonitorColor();
                summary.Text = _monitorColor.Summary();
            }
            match.Checked += Changed;
            match.Unchecked += Changed;
            panel.Children.Add(match);
            panel.Children.Add(summary);
            panel.Children.Add(new System.Windows.Controls.Separator { Margin = new Thickness(0, 0, 0, 12) });
        }
    }
}
