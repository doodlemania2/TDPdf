using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TDPdf
{
    /// <summary>
    /// A themed RGB color picker in the TDPdf house style: a saturation/value square + a hue strip
    /// (both drag-to-pick), two-way-synced R/G/B and hex inputs with a live preview swatch, a
    /// desktop-wide crosshair eyedropper, and an optional "recent" palette row. Opaque RGB only —
    /// per-tool opacity stays on the annotation settings bars, so the picker never touches alpha.
    /// </summary>
    /// <remarks>
    /// This is a from-scratch TDPdf dialog; chrome uses the shared theme brushes (BgDark, BgPanel,
    /// AccentGreen, …) so it tracks the active Dark/Light/HighContrast theme. Swatch/preview fills
    /// are literal colors on purpose — that is the thing being edited.
    /// </remarks>
    internal sealed class TdpColorPicker : Window
    {
        /// <summary>The color chosen when OK is pressed; the seed color otherwise.</summary>
        public Color SelectedColor { get; private set; }

        private double _h, _s = 1, _v = 1;      // HSV working state (h 0..360, s/v 0..1)
        private bool _updating;                 // guards the field <-> thumb <-> preview sync loop
        private readonly IReadOnlyList<Color> _recent;

        // Controls populated by BuildUi (invoked from the constructor before any handler can fire,
        // so these are guaranteed non-null by the time anything reads them — matches the app's
        // established "assigned-in-ctor" null! convention for code-built UI).
        private Rectangle _svHue = null!;
        private Ellipse _svDot = null!;
        private Border _hueThumb = null!;
        private TextBox _rBox = null!, _gBox = null!, _bBox = null!, _hexBox = null!;
        private Border _preview = null!;

        private const int SvW = 216, SvH = 160, HueW = 16;

        // Entry point: modal pick. Returns true on OK (picked = chosen opaque color), false on Cancel.
        public static bool TryPickColor(Window? owner, Color initial, IReadOnlyList<Color> recent, out Color picked)
        {
            var dlg = new TdpColorPicker(owner, initial, recent);
            bool ok = dlg.ShowDialog() == true;
            picked = dlg.SelectedColor;
            return ok;
        }

        private TdpColorPicker(Window? owner, Color initial, IReadOnlyList<Color> recent)
        {
            _recent = recent;
            Title = "TDPdf - Color";
            Width = 300;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Owner = owner;
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;
            UseLayoutRounding = true;

            SelectedColor = Color.FromRgb(initial.R, initial.G, initial.B);
            (_h, _s, _v) = RgbToHsv(SelectedColor);

            BuildUi();
            SyncFromHsv();

            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { DialogResult = false; Close(); }
                else if (e.Key == Key.Enter) Accept();
            };
        }

        private static SolidColorBrush Brush(string key) =>
            Application.Current?.TryFindResource(key) as SolidColorBrush ?? SystemColors.ControlBrush;

        // ── UI ──────────────────────────────────────────────────────────────────
        private void BuildUi()
        {
            var outer = new Border
            {
                Background = Brush("BgDark"),
                BorderBrush = Brush("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };
            var root = new StackPanel();

            // Draggable title bar (custom chrome, matching TdpDialog).
            var titleBar = new Border
            {
                Background = Brush("BgPanel"),
                Padding = new Thickness(16, 10, 16, 10),
                CornerRadius = new CornerRadius(5, 5, 0, 0),
                Child = new TextBlock
                {
                    Text = "Pick a color",
                    Foreground = Brush("AccentGreen"),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 13,
                    FontFamily = new FontFamily("Consolas")
                }
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            root.Children.Add(titleBar);

            var body = new StackPanel { Margin = new Thickness(16, 14, 16, 8) };

            // SV square + hue strip.
            var pickRow = new StackPanel { Orientation = Orientation.Horizontal };

            _svHue = new Rectangle { Width = SvW, Height = SvH };
            var svWhite = new Rectangle
            {
                Width = SvW, Height = SvH, IsHitTestVisible = false,
                Fill = new LinearGradientBrush(Color.FromArgb(255, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 0)
            };
            var svBlack = new Rectangle
            {
                Width = SvW, Height = SvH, IsHitTestVisible = false,
                Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(255, 0, 0, 0), 90)
            };
            var thumbLayer = new Canvas { Width = SvW, Height = SvH, IsHitTestVisible = false };
            _svDot = new Ellipse
            {
                Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, Fill = Brushes.Transparent,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 2, ShadowDepth = 0, Opacity = 0.8 }
            };
            thumbLayer.Children.Add(_svDot);
            var svGrid = new Grid { Width = SvW, Height = SvH };
            svGrid.Children.Add(_svHue);
            svGrid.Children.Add(svWhite);
            svGrid.Children.Add(svBlack);
            svGrid.Children.Add(thumbLayer);
            var svArea = new Border
            {
                Width = SvW, Height = SvH, CornerRadius = new CornerRadius(3), ClipToBounds = false,
                BorderBrush = Brush("BorderDim"), BorderThickness = new Thickness(1), Child = svGrid, Cursor = Cursors.Cross
            };
            svArea.MouseLeftButtonDown += (s, e) => { svArea.CaptureMouse(); SvPick(e.GetPosition(svGrid)); };
            svArea.MouseMove += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) SvPick(e.GetPosition(svGrid)); };
            svArea.MouseLeftButtonUp += (s, e) => svArea.ReleaseMouseCapture();
            pickRow.Children.Add(svArea);

            var hueRect = new Rectangle { Width = HueW, Height = SvH, Fill = HueStripBrush() };
            _hueThumb = new Border
            {
                Width = HueW + 6, Height = 5, BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5),
                Background = Brush("AccentGreen"), CornerRadius = new CornerRadius(2), IsHitTestVisible = false
            };
            var hueThumbLayer = new Canvas { Width = HueW + 6, Height = SvH };
            Canvas.SetLeft(_hueThumb, -3);
            hueThumbLayer.Children.Add(_hueThumb);
            var hueGrid = new Grid { Margin = new Thickness(8, 0, 0, 0) };
            hueGrid.Children.Add(hueRect);
            hueGrid.Children.Add(hueThumbLayer);
            var hueArea = new Border
            {
                Child = hueGrid, Cursor = Cursors.SizeNS, CornerRadius = new CornerRadius(3),
                BorderBrush = Brush("BorderDim"), BorderThickness = new Thickness(1)
            };
            hueArea.MouseLeftButtonDown += (s, e) => { hueArea.CaptureMouse(); HuePick(e.GetPosition(hueRect)); };
            hueArea.MouseMove += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) HuePick(e.GetPosition(hueRect)); };
            hueArea.MouseLeftButtonUp += (s, e) => hueArea.ReleaseMouseCapture();
            pickRow.Children.Add(hueArea);
            body.Children.Add(pickRow);

            // Preview + R/G/B fields + eyedropper.
            var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            _preview = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(3),
                BorderBrush = Brush("BorderDim"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0)
            };
            inputRow.Children.Add(_preview);
            _rBox = NumBox(); _gBox = NumBox(); _bBox = NumBox();
            inputRow.Children.Add(FieldGroup("R", _rBox));
            inputRow.Children.Add(FieldGroup("G", _gBox));
            inputRow.Children.Add(FieldGroup("B", _bBox));
            var eyedrop = new Button
            {
                Width = 30, Height = 24, Margin = new Thickness(8, 14, 0, 0),
                Background = Brush("BgPanel"), Foreground = Brush("TextPrimary"),
                BorderBrush = Brush("BorderDim"), BorderThickness = new Thickness(1),
                Content = CrosshairIcon(), ToolTip = "Pick a color from anywhere on screen",
                Cursor = Cursors.Cross, Template = MakeButtonTemplate()
            };
            eyedrop.MouseEnter += (_, _) => eyedrop.Background = Brush("BgHover");
            eyedrop.MouseLeave += (_, _) => eyedrop.Background = Brush("BgPanel");
            eyedrop.Click += (_, _) => RunEyedropper();
            inputRow.Children.Add(eyedrop);
            body.Children.Add(inputRow);

            // Hex row.
            var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            hexRow.Children.Add(new TextBlock
            {
                Text = "Hex", Foreground = Brush("TextSecondary"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });
            _hexBox = MakeTextBox(96);
            _hexBox.MaxLength = 7;
            _hexBox.LostFocus += (_, _) => CommitHex();
            _hexBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitHex(); };
            hexRow.Children.Add(_hexBox);
            body.Children.Add(hexRow);

            // Recent palette row (optional): quick re-pick of colors chosen in earlier sessions.
            if (_recent.Count > 0)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "Recent", Foreground = Brush("TextSecondary"), FontSize = 11,
                    Margin = new Thickness(0, 12, 0, 4)
                });
                var recentRow = new WrapPanel { Width = SvW };
                foreach (var rc in _recent)
                {
                    var c = rc;
                    var sw = new Border
                    {
                        Width = 20, Height = 20, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 4, 4),
                        Background = new SolidColorBrush(c), BorderBrush = Brush("BorderDim"), BorderThickness = new Thickness(1),
                        Cursor = Cursors.Hand, ToolTip = $"#{c.R:X2}{c.G:X2}{c.B:X2}"
                    };
                    sw.MouseLeftButtonUp += (_, _) => SetFromColor(c);
                    recentRow.Children.Add(sw);
                }
                body.Children.Add(recentRow);
            }

            root.Children.Add(body);

            // OK / Cancel.
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 6, 16, 16)
            };
            var cancel = MakeButton("Cancel", accent: false);
            cancel.IsCancel = true;
            cancel.Click += (_, _) => { DialogResult = false; Close(); };
            var ok = MakeButton("OK", accent: true);
            ok.IsDefault = true;
            ok.Margin = new Thickness(8, 0, 0, 0);
            ok.Click += (_, _) => Accept();
            btnRow.Children.Add(cancel);
            btnRow.Children.Add(ok);
            root.Children.Add(btnRow);

            outer.Child = root;
            Content = outer;
        }

        private void Accept() { SelectedColor = HsvToRgb(_h, _s, _v); DialogResult = true; Close(); }

        // ── Interaction ─────────────────────────────────────────────────────────
        private void SvPick(Point p) { _s = Clamp01(p.X / SvW); _v = Clamp01(1 - p.Y / SvH); SyncFromHsv(); }
        private void HuePick(Point p) { _h = Clamp01(p.Y / SvH) * 360; SyncFromHsv(); }
        private void CommitHex() { if (TryParseHex(_hexBox.Text, out Color c)) SetFromColor(c); else SyncFromHsv(); }

        private void CommitRgb()
        {
            if (byte.TryParse(_rBox.Text, out byte r) && byte.TryParse(_gBox.Text, out byte g) && byte.TryParse(_bBox.Text, out byte b))
                SetFromColor(Color.FromRgb(r, g, b));
            else
                SyncFromHsv();
        }

        private void SetFromColor(Color c) { (_h, _s, _v) = RgbToHsv(c); SyncFromHsv(); }

        // Push the current HSV out to every control (hue base, thumbs, RGB, hex, preview).
        private void SyncFromHsv()
        {
            if (_updating) return;
            _updating = true;
            var c = HsvToRgb(_h, _s, _v);
            _svHue.Fill = new SolidColorBrush(HsvToRgb(_h, 1, 1));
            Canvas.SetLeft(_svDot, _s * SvW - 6);
            Canvas.SetTop(_svDot, (1 - _v) * SvH - 6);
            Canvas.SetTop(_hueThumb, Math.Max(0, Math.Min(SvH - 5, _h / 360.0 * SvH - 2.5)));
            _rBox.Text = c.R.ToString(); _gBox.Text = c.G.ToString(); _bBox.Text = c.B.ToString();
            _hexBox.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            _preview.Background = new SolidColorBrush(c);
            _updating = false;
        }

        // ── Eyedropper (desktop-wide) ───────────────────────────────────────────
        private void RunEyedropper()
        {
            try
            {
                // A near-invisible full-desktop overlay captures the next click anywhere on screen.
                var capture = new Window
                {
                    WindowStyle = WindowStyle.None, AllowsTransparency = true,
                    Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                    ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Cursor = Cursors.Cross,
                    Left = SystemParameters.VirtualScreenLeft, Top = SystemParameters.VirtualScreenTop,
                    Width = SystemParameters.VirtualScreenWidth, Height = SystemParameters.VirtualScreenHeight, Owner = this
                };
                capture.MouseLeftButtonDown += (_, _) =>
                {
                    // GetCursorPos and the desktop DC's GetPixel both work in physical screen pixels,
                    // so the sample is correct regardless of per-monitor DPI scaling.
                    if (TrySamplePixelAtCursor(out Color sampled))
                    {
                        capture.DialogResult = true; capture.Close();
                        SetFromColor(sampled);
                        return;
                    }
                    capture.DialogResult = false; capture.Close();
                };
                capture.KeyDown += (_, e) => { if (e.Key == Key.Escape) { capture.DialogResult = false; capture.Close(); } };
                capture.ShowDialog();
            }
            catch
            {
                // Screen capture is best-effort; a failure must never take down the picker.
            }
        }

        private static bool TrySamplePixelAtCursor(out Color color)
        {
            color = Colors.Black;
            IntPtr dc = IntPtr.Zero;
            try
            {
                if (!GetCursorPos(out POINT pt)) return false;
                dc = GetDC(IntPtr.Zero);
                if (dc == IntPtr.Zero) return false;
                uint cref = GetPixel(dc, pt.X, pt.Y);
                if (cref == 0xFFFFFFFF) return false;   // CLR_INVALID
                color = Color.FromRgb((byte)(cref & 0xFF), (byte)((cref >> 8) & 0xFF), (byte)((cref >> 16) & 0xFF));
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc);
            }
        }

        // ── Small themed control builders ───────────────────────────────────────
        private StackPanel FieldGroup(string label, TextBox box)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
            sp.Children.Add(new TextBlock { Text = label, Foreground = Brush("TextSecondary"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center });
            sp.Children.Add(box);
            return sp;
        }

        private TextBox NumBox()
        {
            var b = MakeTextBox(34);
            b.MaxLength = 3;
            b.TextAlignment = TextAlignment.Center;
            b.LostFocus += (_, _) => CommitRgb();
            b.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitRgb(); };
            return b;
        }

        private TextBox MakeTextBox(double width) => new()
        {
            Width = width, Height = 22, VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brush("BgPanel"), Foreground = Brush("TextPrimary"),
            BorderBrush = Brush("BorderDim"), BorderThickness = new Thickness(1),
            CaretBrush = Brush("TextPrimary"), SelectionBrush = Brush("AccentGreen"),
            Padding = new Thickness(4, 0, 4, 0), Template = MakeTextBoxTemplate()
        };

        // Vector crosshair, drawn in the current text color so it reads in every theme.
        private UIElement CrosshairIcon()
        {
            var fg = Brush("TextPrimary");
            var g = new Grid { Width = 14, Height = 14 };
            g.Children.Add(new Rectangle { Width = 1.4, Fill = fg, HorizontalAlignment = HorizontalAlignment.Center });
            g.Children.Add(new Rectangle { Height = 1.4, Fill = fg, VerticalAlignment = VerticalAlignment.Center });
            g.Children.Add(new Ellipse
            {
                Width = 8, Height = 8, Stroke = fg, StrokeThickness = 1.4, Fill = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
            return g;
        }

        private Button MakeButton(string text, bool accent)
        {
            var normal = accent ? Brush("AccentGreenDim") : Brush("BgPanel");
            var hover = accent ? Brush("BgPressed") : Brush("BgHover");
            var btn = new Button
            {
                Content = text, Height = 28, MinWidth = 74, Padding = new Thickness(14, 0, 14, 0),
                Background = normal, Foreground = accent ? Brush("AccentGreen") : Brush("TextPrimary"),
                BorderBrush = accent ? Brush("AccentGreen") : Brush("BorderDim"), BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand, Template = MakeButtonTemplate()
            };
            btn.MouseEnter += (_, _) => btn.Background = hover;
            btn.MouseLeave += (_, _) => btn.Background = normal;
            return btn;
        }

        private static ControlTemplate MakeTextBoxTemplate()
        {
            var b = new FrameworkElementFactory(typeof(Border));
            foreach (var (dp, prop) in new[] { (Border.BackgroundProperty, "Background"), (Border.BorderBrushProperty, "BorderBrush"), (Border.BorderThicknessProperty, "BorderThickness") })
                b.SetBinding(dp, new Binding(prop) { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            var sv = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
            sv.SetValue(ScrollViewer.VerticalAlignmentProperty, VerticalAlignment.Center);
            b.AppendChild(sv);
            return new ControlTemplate(typeof(TextBox)) { VisualTree = b };
        }

        private static ControlTemplate MakeButtonTemplate()
        {
            var bf = new FrameworkElementFactory(typeof(Border));
            foreach (var (dp, prop) in new[] { (Border.BackgroundProperty, "Background"), (Border.BorderBrushProperty, "BorderBrush"), (Border.BorderThicknessProperty, "BorderThickness"), (Border.PaddingProperty, "Padding") })
                bf.SetBinding(dp, new Binding(prop) { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            bf.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bf.AppendChild(cp);
            return new ControlTemplate(typeof(Button)) { VisualTree = bf };
        }

        private static LinearGradientBrush HueStripBrush()
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            for (int i = 0; i <= 6; i++) g.GradientStops.Add(new GradientStop(HsvToRgb(i * 60, 1, 1), i / 6.0));
            return g;
        }

        // ── Color math / parsing ─────────────────────────────────────────────────
        private static double Clamp01(double v) => Math.Max(0, Math.Min(1, v));

        internal static bool TryParseHex(string? s, out Color c)
        {
            c = Colors.Black;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().TrimStart('#');
            if (s.Length == 3) s = string.Concat(s.Select(ch => $"{ch}{ch}"));
            if (s.Length != 6) return false;
            if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
            c = Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
            return true;
        }

        private static (double h, double s, double v) RgbToHsv(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            double h = 0;
            if (d > 0.00001)
            {
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * (((b - r) / d) + 2);
                else h = 60 * (((r - g) / d) + 4);
            }
            if (h < 0) h += 360;
            double s = max <= 0 ? 0 : d / max;
            return (h, s, max);
        }

        private static Color HsvToRgb(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s, x = c * (1 - Math.Abs((h / 60.0 % 2) - 1)), m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr hdc, int x, int y);
    }
}
