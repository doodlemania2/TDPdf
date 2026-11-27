// MainWindow — the Extract Image dialog (#244).
//
// Works on the picture's pixels at their own resolution, so what comes out is as sharp as what the PDF
// holds. Three ways to cut:
//   * Box only: drag a box on the preview; that box becomes the picture.
//   * Remove a colour: click the colour (the paper of a scan, say); every pixel close to it goes
//     transparent. Tolerance says how close; Soft edges feathers the boundary so a signature does not
//     come out with a hard white halo. "Only the connected area" leaves the same colour inside shapes alone.
//   * Keep a colour area: the opposite, keep only that area and clear the rest.
// A box dragged in either colour mode crops the result too. All the pixel maths is Services/ColorKeyMask.cs.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TDPdf.Services;
using ShapeRectangle = System.Windows.Shapes.Rectangle;

namespace TDPdf
{
    public partial class MainWindow
    {
        /// <summary>What the Extract Image dialog produced: straight BGRA pixels, and where they sat in the source.</summary>
        private sealed record PictureExtractResult(byte[] Bgra, int Width, int Height, int OffsetX, int OffsetY);

        private enum ExtractMode { Box, RemoveColour, KeepColour }

        private PictureExtractResult? ShowPictureExtractDialog(byte[] bgra, int w, int h)
        {
            var bgPanel = BrushResource("BgPanel");
            var bgDark = BrushResource("BgDark");
            var borderDim = BrushResource("BorderDim");
            var textPrimary = BrushResource("TextPrimary");
            var textSecondary = BrushResource("TextSecondary");
            var accent = BrushResource("AccentGreen");

            var (win, shellBody) = TdpDialog.CreateShell(this, "Extract Image", 580);
            win.ShowInTaskbar = false;
            win.Foreground = textPrimary;
            win.FontFamily = new FontFamily("Segoe UI");
            win.FontSize = 12;

            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock
            {
                Text = "Cut a piece out of this picture as a transparent picture mark. The page itself is not changed.",
                Foreground = textSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });

            // ── State ──
            var mode = ExtractMode.Box;
            ColorKeyMask.PixelBox? box = null;
            (int X, int Y)? seed = null;
            (byte B, byte G, byte R)? key = null;

            // A smaller copy for the live preview; the full-size pixels are only touched on Extract.
            double fit = Math.Min(1.0, 700.0 / Math.Max(w, h));
            int pw = Math.Max(1, (int)Math.Round(w * fit)), ph = Math.Max(1, (int)Math.Round(h * fit));
            var small = new byte[pw * ph * 4];
            for (int y = 0; y < ph; y++)
            {
                int sy = Math.Min(h - 1, (int)((y + 0.5) * h / ph));
                for (int x = 0; x < pw; x++)
                {
                    int sx = Math.Min(w - 1, (int)((x + 0.5) * w / pw));
                    Buffer.BlockCopy(bgra, (sy * w + sx) * 4, small, (y * pw + x) * 4, 4);
                }
            }

            double shown = Math.Min(Math.Min(520.0 / w, 320.0 / h), 6.0);
            double dispW = w * shown, dispH = h * shown;

            // ── Preview ──
            var checker = new DrawingBrush
            {
                TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, 16, 16), ViewboxUnits = BrushMappingMode.Absolute,
                Drawing = new DrawingGroup
                {
                    Children =
                    {
                        new GeometryDrawing(bgPanel, null, new RectangleGeometry(new Rect(0, 0, 16, 16))),
                        new GeometryDrawing(bgDark, null, new RectangleGeometry(new Rect(0, 0, 8, 8))),
                        new GeometryDrawing(bgDark, null, new RectangleGeometry(new Rect(8, 8, 8, 8))),
                    }
                }
            };
            var image = new Image { Width = dispW, Height = dispH, Stretch = Stretch.Fill, IsHitTestVisible = false };
            var overlay = new ShapeRectangle
            {
                Stroke = accent, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 },
                Visibility = Visibility.Collapsed, IsHitTestVisible = false
            };
            var marker = new ShapeRectangle
            {
                Width = 9, Height = 9, Stroke = accent, StrokeThickness = 2, Fill = Brushes.Transparent,
                Visibility = Visibility.Collapsed, IsHitTestVisible = false
            };
            var stage = new Canvas { Width = dispW, Height = dispH, Background = Brushes.Transparent, Cursor = Cursors.Cross };
            stage.Children.Add(image);
            stage.Children.Add(overlay);
            stage.Children.Add(marker);
            var frame = new Border
            {
                Background = checker, BorderBrush = borderDim, BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center, Child = stage, ClipToBounds = true
            };
            root.Children.Add(frame);

            // ── Controls ──
            var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 6) };
            var modeButtons = new List<(RadioButton Button, ExtractMode Mode)>();
            void Mode(string text, ExtractMode m, bool on)
            {
                var rb = new RadioButton { Content = text, IsChecked = on, Margin = new Thickness(0, 0, 18, 0), GroupName = "extractMode" };
                modes.Children.Add(rb);
                modeButtons.Add((rb, m));
            }
            Mode("Box only", ExtractMode.Box, true);
            Mode("Remove a colour", ExtractMode.RemoveColour, false);
            Mode("Keep a colour area", ExtractMode.KeepColour, false);
            root.Children.Add(modes);

            var hint = new TextBlock { Foreground = textSecondary, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            root.Children.Add(hint);

            (Slider Slider, TextBlock Value) AddSlider(string label, double max, double start)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
                var l = new TextBlock { Text = label, Foreground = textSecondary, VerticalAlignment = VerticalAlignment.Center };
                var s = new Slider { Minimum = 0, Maximum = max, Value = start, VerticalAlignment = VerticalAlignment.Center };
                var v = new TextBlock { Foreground = textSecondary, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(s, 1); Grid.SetColumn(v, 2);
                row.Children.Add(l); row.Children.Add(s); row.Children.Add(v);
                root.Children.Add(row);
                return (s, v);
            }
            var (tolerance, tolValue) = AddSlider("Tolerance", 100, 18);
            var (soft, softValue) = AddSlider("Soft edges", 100, 12);

            var connected = new CheckBox { Content = "Only the area connected to the clicked spot", IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
            var trim = new CheckBox { Content = "Trim the empty edges", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
            root.Children.Add(connected);
            root.Children.Add(trim);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancelBtn = new Button
            {
                Content = "Cancel", Width = 96, Height = 30, Margin = new Thickness(0, 0, 8, 0),
                Background = bgPanel, Foreground = textPrimary, BorderBrush = borderDim, Cursor = Cursors.Hand, IsCancel = true
            };
            var okBtn = new Button
            {
                Content = "Extract", Width = 96, Height = 30, Background = accent, Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold, BorderBrush = accent, Cursor = Cursors.Hand, IsDefault = true
            };
            buttons.Children.Add(cancelBtn);
            buttons.Children.Add(okBtn);
            root.Children.Add(buttons);
            shellBody.Children.Add(root);

            double Tol() => tolerance.Value / 100.0 * 255.0;
            double Soft() => soft.Value / 100.0 * 160.0;
            bool IsColour() => mode != ExtractMode.Box;

            void ShowBox(ColorKeyMask.PixelBox b)
            {
                overlay.Visibility = Visibility.Visible;
                overlay.Width = Math.Max(1, b.W * shown);
                overlay.Height = Math.Max(1, b.H * shown);
                Canvas.SetLeft(overlay, b.X * shown);
                Canvas.SetTop(overlay, b.Y * shown);
            }

            void Refresh()
            {
                tolValue.Text = $"{tolerance.Value:0}%";
                softValue.Text = $"{soft.Value:0}%";
                tolerance.IsEnabled = soft.IsEnabled = connected.IsEnabled = IsColour();

                byte[] preview = small;
                if (IsColour() && key is { } k && seed is { } sd)
                {
                    int px = Math.Clamp((int)(sd.X * fit), 0, pw - 1), py = Math.Clamp((int)(sd.Y * fit), 0, ph - 1);
                    preview = ColorKeyMask.Apply(small, pw, ph, k, Tol(), Soft(), mode == ExtractMode.KeepColour,
                        connected.IsChecked == true ? (px, py) : null) ?? small;
                }
                var bmp = BitmapSource.Create(pw, ph, 96, 96, PixelFormats.Bgra32, null, preview, pw * 4);
                bmp.Freeze();
                image.Source = bmp;

                if (box is { } b) ShowBox(b); else overlay.Visibility = Visibility.Collapsed;
                if (IsColour() && seed is { } m)
                {
                    marker.Visibility = Visibility.Visible;
                    Canvas.SetLeft(marker, m.X * shown - 4.5);
                    Canvas.SetTop(marker, m.Y * shown - 4.5);
                }
                else marker.Visibility = Visibility.Collapsed;

                hint.Text = mode switch
                {
                    ExtractMode.Box => "Drag a box around what you want.",
                    ExtractMode.RemoveColour => "Click the colour to remove. Drag a box to crop the result as well.",
                    _ => "Click inside the area to keep. Drag a box to crop the result as well.",
                };
                okBtn.IsEnabled = mode == ExtractMode.Box ? box is not null : key is not null;
            }

            // ── Pointer: a drag draws the box, a click picks the colour ──
            Point? down = null;
            stage.MouseLeftButtonDown += (_, e) => { down = e.GetPosition(stage); stage.CaptureMouse(); };
            stage.MouseMove += (_, e) =>
            {
                if (down is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(stage);
                if (Math.Abs(p.X - d.X) < 4 && Math.Abs(p.Y - d.Y) < 4) return;
                double x0 = Math.Clamp(Math.Min(p.X, d.X), 0, dispW), y0 = Math.Clamp(Math.Min(p.Y, d.Y), 0, dispH);
                double x1 = Math.Clamp(Math.Max(p.X, d.X), 0, dispW), y1 = Math.Clamp(Math.Max(p.Y, d.Y), 0, dispH);
                ShowBox(new ColorKeyMask.PixelBox((int)(x0 / shown), (int)(y0 / shown),
                    Math.Max(1, (int)((x1 - x0) / shown)), Math.Max(1, (int)((y1 - y0) / shown))));
            };
            stage.MouseLeftButtonUp += (_, e) =>
            {
                if (down is not { } d) return;
                down = null;
                stage.ReleaseMouseCapture();
                var p = e.GetPosition(stage);
                if (Math.Abs(p.X - d.X) >= 4 || Math.Abs(p.Y - d.Y) >= 4)
                {
                    double x0 = Math.Clamp(Math.Min(p.X, d.X), 0, dispW), y0 = Math.Clamp(Math.Min(p.Y, d.Y), 0, dispH);
                    double x1 = Math.Clamp(Math.Max(p.X, d.X), 0, dispW), y1 = Math.Clamp(Math.Max(p.Y, d.Y), 0, dispH);
                    box = new ColorKeyMask.PixelBox((int)(x0 / shown), (int)(y0 / shown),
                        Math.Max(1, (int)Math.Ceiling((x1 - x0) / shown)), Math.Max(1, (int)Math.Ceiling((y1 - y0) / shown)));
                }
                else if (IsColour())
                {
                    int sx = Math.Clamp((int)(p.X / shown), 0, w - 1), sy = Math.Clamp((int)(p.Y / shown), 0, h - 1);
                    seed = (sx, sy);
                    key = ColorKeyMask.ColourAt(bgra, w, h, sx, sy);
                }
                else box = null;
                Refresh();
            };
            tolerance.ValueChanged += (_, _) => Refresh();
            soft.ValueChanged += (_, _) => Refresh();
            connected.Click += (_, _) => Refresh();

            PictureExtractResult? result = null;
            cancelBtn.Click += (_, _) => win.DialogResult = false;
            okBtn.Click += (_, _) =>
            {
                result = BuildExtraction(bgra, w, h, mode, box, key, seed, Tol(), Soft(),
                    connected.IsChecked == true, trim.IsChecked == true);
                if (result is null)
                {
                    hint.Text = "Nothing is left to extract. Try a higher tolerance or a different spot.";
                    return;
                }
                win.DialogResult = true;
            };

            foreach (var (button, m) in modeButtons)
                button.Checked += (_, _) => { mode = m; Refresh(); };
            Refresh();
            return win.ShowDialog() == true ? result : null;
        }

        /// <summary>The extraction itself, on the full-size pixels. Null when nothing opaque is left.</summary>
        private static PictureExtractResult? BuildExtraction(
            byte[] bgra, int w, int h, ExtractMode mode, ColorKeyMask.PixelBox? box,
            (byte B, byte G, byte R)? key, (int X, int Y)? seed, double tolerance, double soft, bool connected, bool trim)
        {
            byte[] pixels = bgra;
            int pw = w, ph = h, ox = 0, oy = 0;

            if (mode != ExtractMode.Box)
            {
                if (key is not { } k || seed is not { } sd) return null;
                var keyed = ColorKeyMask.Apply(pixels, pw, ph, k, tolerance, soft, mode == ExtractMode.KeepColour,
                    connected ? (sd.X, sd.Y) : null);
                if (keyed is null) return null;
                pixels = keyed;
            }

            if (box is { } b)
            {
                int x0 = Math.Clamp(b.X, 0, pw), y0 = Math.Clamp(b.Y, 0, ph);
                if (ColorKeyMask.CropBox(pixels, pw, ph, b) is not { } crop) return null;
                pixels = crop.Bgra; pw = crop.W; ph = crop.H; ox = x0; oy = y0;
            }
            else if (mode == ExtractMode.Box) return null;

            if (trim && mode != ExtractMode.Box)
            {
                if (ColorKeyMask.OpaqueBounds(pixels, pw, ph) is not { } ob) return null;
                if (ColorKeyMask.CropBox(pixels, pw, ph, ob) is not { } t) return null;
                pixels = t.Bgra; pw = t.W; ph = t.H; ox += ob.X; oy += ob.Y;
            }
            return new PictureExtractResult(pixels, pw, ph, ox, oy);
        }
    }
}
