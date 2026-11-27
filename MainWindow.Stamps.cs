// MainWindow — the Stamp tool (3.0): placing a standard, custom text or picture stamp, its settings
// bar, and how a stamp is drawn on the canvas and in the page thumbnails. The saved form of a stamp
// (a real /Stamp annotation) lives in Services/StampWriter.cs; the standard stamps and the size and
// opacity rules in Services/StampLibrary.cs.

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        /// <summary>The Stamp tool's settings, kept for the life of the window like the other tools'.</summary>
        private readonly StampSettings _stamp = new();

        private Border? _stampBar;

        /// <summary>A picture stamp larger than this is refused rather than held in memory as base 64.</summary>
        private const long MaxStampImageBytes = 20L * 1024 * 1024;

        // ============================================================
        // Placing
        // ============================================================

        /// <summary>
        /// Stamp tool press. A press on a stamp already on the page picks it up (move, resize, group),
        /// exactly as the Select tool would; a press anywhere else stamps the page there, centred on the
        /// click, and leaves the new stamp selected so it can be nudged or resized at once.
        /// </summary>
        private void MouseDownStamp(MouseButtonEventArgs e, Point pos, int pageIdx)
        {
            if (_annotations.TryGetValue(pageIdx, out var list) &&
                list.Any(a => a is StampAnnotation && HitTestAnnotation(a, pos, out _)))
            {
                MouseDownSelect(e, pos, pageIdx);
                return;
            }

            e.Handled = true;
            ClearSelection();
            var stamp = BuildStampFromSettings(pos, pageIdx);
            if (stamp is null) return;

            AddAnnotation(stamp);
            RenderAllAnnotations(pageIdx);
            SelectAnnotation(stamp, PlacedVisualRect(stamp));
            SetStatus($"{StampDescription(stamp)} stamp placed. Drag to move, corner handle to resize, Delete to remove");
        }

        /// <summary>The stamp the bar describes, centred on <paramref name="center"/>; null (with a status line saying why) when it has nothing to draw.</summary>
        private StampAnnotation? BuildStampFromSettings(Point center, int pageIdx)
        {
            double size = StampSettings.ClampSize(_stamp.Size);
            double opacity = StampSettings.ClampOpacityPercent(_stamp.OpacityPercent) / 100.0;
            switch (_stamp.Choice)
            {
                case StampSettings.CustomTextChoice:
                {
                    var custom = StampLibrary.CreateCustom(_stamp.CustomText, _stamp.CustomR, _stamp.CustomG, _stamp.CustomB,
                                                           center, pageIdx, size, opacity);
                    if (custom is null) SetStatus("Type the words for the stamp in the tool bar first");
                    return custom;
                }
                case StampSettings.ImageChoice:
                {
                    if (_stamp.ImageData is null && !ChooseStampImage()) return null;
                    var pic = _stamp.ImageData is null ? null
                        : StampLibrary.CreateImage(_stamp.ImageData, _stamp.ImagePixelWidth, _stamp.ImagePixelHeight,
                                                   center, pageIdx, size, opacity);
                    if (pic is null) SetStatus("That picture has no size to stamp with");
                    return pic;
                }
                default:
                    return StampLibrary.Create(StampLibrary.Find(_stamp.Choice) ?? StampLibrary.Standard[0],
                                               center, pageIdx, size, opacity);
            }
        }

        private static string StampDescription(StampAnnotation s) => s.Kind switch
        {
            StampKind.Standard => StampLibrary.Find(s.StampId)?.Label ?? "Standard",
            StampKind.Image => "Picture",
            _ => "Custom"
        };

        /// <summary>Asks for a picture file and keeps it as the picture stamp. False when cancelled or unreadable.</summary>
        private bool ChooseStampImage()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Choose a picture for the stamp",
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*"
            };
            if (dlg.ShowDialog(this) != true) return false;
            try
            {
                if (new FileInfo(dlg.FileName).Length > MaxStampImageBytes)
                {
                    TdpDialog.Show(this, "That picture is too large to use as a stamp (over 20 MB).", "Stamp", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                byte[] bytes = File.ReadAllBytes(dlg.FileName);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                _stamp.ImageData = Convert.ToBase64String(bytes);
                _stamp.ImagePixelWidth = bmp.PixelWidth;
                _stamp.ImagePixelHeight = bmp.PixelHeight;
                _stamp.ImageName = System.IO.Path.GetFileName(dlg.FileName);
                return true;
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Could not load the picture:\n{ex.Message}", "Stamp", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // ============================================================
        // Drawing
        // ============================================================

        /// <summary>
        /// A stamp on the canvas: draws the stamp upright in its own box (the design box scaled by the
        /// annotation's Scale) and turns it about the box centre. Placed by Canvas.Left/Top at the
        /// annotation's Position.
        /// </summary>
        private sealed class StampVisual : FrameworkElement
        {
            private readonly StampAnnotation _stamp;
            public StampVisual(StampAnnotation stamp)
            {
                _stamp = stamp;
                Width = stamp.SourceWidth * stamp.Scale;
                Height = stamp.SourceHeight * stamp.Scale;
                IsHitTestVisible = false;
            }
            protected override void OnRender(DrawingContext dc) =>
                DrawStamp(dc, _stamp, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        /// <summary>
        /// Draws <paramref name="s"/> with its top-left corner at the origin. The same drawing serves the
        /// canvas and the page thumbnails, and mirrors what StampWriter writes: a rounded frame and bold
        /// capitals centred in it (shrunk to fit), or the picture filling the box.
        /// </summary>
        private static void DrawStamp(DrawingContext dc, StampAnnotation s, double dpi)
        {
            double w = s.SourceWidth, h = s.SourceHeight;
            if (!IsFinitePositive(w) || !IsFinitePositive(h) || !IsFinitePositive(s.Scale)) return;
            int turn = AnnotationFrame.Normalize(s.Rotation);
            dc.PushOpacity(Math.Clamp(s.Opacity, 0.0, 1.0));
            if (turn != 0) dc.PushTransform(new RotateTransform(turn, w * s.Scale / 2, h * s.Scale / 2));
            dc.PushTransform(new ScaleTransform(s.Scale, s.Scale));
            try
            {
                if (s.Kind == StampKind.Image)
                {
                    if (DecodeThumbImage(s.ImageData ?? "") is { } img) dc.DrawImage(img, new Rect(0, 0, w, h));
                    return;
                }
                var brush = TdpDialog.FrozenSolidColorBrush(Color.FromRgb(s.ColorR, s.ColorG, s.ColorB));
                const double border = 3.5, inset = border / 2 + 1;
                dc.DrawRoundedRectangle(null, new Pen(brush, border) { LineJoin = PenLineJoin.Round },
                                        new Rect(inset, inset, w - inset * 2, h - inset * 2), 8, 8);
                string family = FontCoverage.PickFamily("Arial", s.Label);
                double size = StampLibrary.DesignFont;
                var face = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                var ft = new FormattedText(s.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush, dpi);
                double room = w - 28;
                if (ft.Width > room && ft.Width > 0)
                {
                    size = Math.Max(6, size * room / ft.Width);
                    ft = new FormattedText(s.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush, dpi);
                }
                dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
            }
            finally
            {
                dc.Pop();
                if (turn != 0) dc.Pop();
                dc.Pop();
            }
        }

        /// <summary>The element that shows <paramref name="s"/> on the annotation canvas.</summary>
        private static FrameworkElement MakeStampElement(StampAnnotation s)
        {
            var el = new StampVisual(s);
            Canvas.SetLeft(el, s.Position.X);
            Canvas.SetTop(el, s.Position.Y);
            return el;
        }

        // ============================================================
        // The tool bar
        // ============================================================

        private void ShowStampSettings()
        {
            HideStampSettings();
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };
            var fg = (SolidColorBrush)FindResource("TextSecondary");

            // Stamp picker: the standard stamps, then a custom text stamp, then a picture.
            panel.Children.Add(MakeLabel("Stamp:"));
            var picker = new ComboBox { Width = 150, Height = 24, Style = (Style)FindResource("DarkComboBox"), VerticalContentAlignment = VerticalAlignment.Center };
            foreach (var def in StampLibrary.Standard) picker.Items.Add(new ComboBoxItem { Content = def.Label, Tag = def.Id });
            picker.Items.Add(new ComboBoxItem { Content = "Custom text…", Tag = StampSettings.CustomTextChoice });
            picker.Items.Add(new ComboBoxItem { Content = "Picture…", Tag = StampSettings.ImageChoice });
            foreach (ComboBoxItem item in picker.Items)
                if ((string)item.Tag == _stamp.Choice) picker.SelectedItem = item;
            if (picker.SelectedItem is null) picker.SelectedIndex = 0;
            System.Windows.Automation.AutomationProperties.SetName(picker, "Stamp");
            panel.Children.Add(picker);

            // Per-choice extras sit in one host so the choice can swap them without rebuilding the bar.
            var extras = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
            panel.Children.Add(extras);
            void BuildExtras()
            {
                extras.Children.Clear();
                if (_stamp.Choice == StampSettings.CustomTextChoice)
                {
                    var box = new TextBox
                    {
                        Width = 120, Height = 24, Text = _stamp.CustomText, VerticalContentAlignment = VerticalAlignment.Center,
                        Background = (SolidColorBrush)FindResource("BgPanel"), Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                        BorderBrush = (SolidColorBrush)FindResource("BorderDim"), CaretBrush = (SolidColorBrush)FindResource("TextPrimary"),
                        ToolTip = "The words on the stamp"
                    };
                    System.Windows.Automation.AutomationProperties.SetName(box, "Stamp text");
                    box.TextChanged += (_, _) => _stamp.CustomText = box.Text;
                    extras.Children.Add(box);
                    var swatches = new List<Border>();
                    Color Active() => Color.FromRgb(_stamp.CustomR, _stamp.CustomG, _stamp.CustomB);
                    void Apply(Color c)
                    {
                        _stamp.CustomR = c.R; _stamp.CustomG = c.G; _stamp.CustomB = c.B;
                        MarkActiveSwatch(swatches, Active());
                    }
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    foreach (var color in SwatchColors)
                    {
                        var sw = MakeColorSwatch(color, Apply);
                        swatches.Add(sw);
                        row.Children.Add(sw);
                    }
                    MarkActiveSwatch(swatches, Active());
                    row.Children.Add(MakeCustomColorSwatch(Active, Apply));
                    extras.Children.Add(row);
                }
                else if (_stamp.Choice == StampSettings.ImageChoice)
                {
                    var choose = new Button { Content = _stamp.ImageData is null ? "Choose picture…" : "Change…", Style = (Style)FindResource("ToolbarButton") };
                    var name = new TextBlock
                    {
                        Text = _stamp.ImageName, Foreground = fg, FontSize = 11, MaxWidth = 140, TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0)
                    };
                    choose.Click += (_, _) =>
                    {
                        if (ChooseStampImage()) { choose.Content = "Change…"; name.Text = _stamp.ImageName; }
                    };
                    extras.Children.Add(choose);
                    extras.Children.Add(name);
                }
            }
            BuildExtras();
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is ComboBoxItem it && it.Tag is string tag)
                {
                    _stamp.Choice = tag;
                    BuildExtras();
                }
            };

            panel.Children.Add(Separator());

            // Size and opacity: a slider and a typed value that agree. A typed number is clamped to the
            // slider's range and accepts either decimal separator; text that is not a number is put back.
            AddSliderRow(panel, "Size:", StampSettings.MinSize, StampSettings.MaxSize, _stamp.Size, "px",
                         v => _stamp.Size = v, StampSettings.TryParseSize, "Stamp size");
            panel.Children.Add(Separator());
            AddSliderRow(panel, "Opacity:", StampSettings.MinOpacityPercent, StampSettings.MaxOpacityPercent, _stamp.OpacityPercent, "%",
                         v => _stamp.OpacityPercent = v, StampSettings.TryParseOpacityPercent, "Stamp opacity");
            panel.Children.Add(Separator());

            var reset = new Button { Content = "Reset", Style = (Style)FindResource("ToolbarButton"), ToolTip = "Put the stamp, size, opacity, custom text and picture back to their defaults" };
            reset.Click += (_, _) =>
            {
                _stamp.Reset();
                ShowStampSettings();
            };
            panel.Children.Add(reset);

            _stampBar = new Border
            {
                Background = BrushResource("BgDark"),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Child = panel,
                Margin = new Thickness(8, 0, 0, 0)
            };
            if (PagePreviewPanel.Parent is Grid previewArea)
            {
                Panel.SetZIndex(_stampBar, 100);
                previewArea.Children.Add(_stampBar);
            }

            Rectangle Separator() => new()
            {
                Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"), Margin = new Thickness(8, 2, 8, 2)
            };
        }

        private delegate bool NumberParser(string? text, out double value);

        private void AddSliderRow(StackPanel panel, string label, double min, double max, double value, string unit,
                                  Action<double> apply, NumberParser parse, string automationName)
        {
            panel.Children.Add(MakeLabel(label));
            var slider = new Slider
            {
                Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
                Width = 80, VerticalAlignment = VerticalAlignment.Center
            };
            var typed = new TextBox
            {
                Width = 46, Height = 24, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
                Background = (SolidColorBrush)FindResource("BgPanel"), Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"), CaretBrush = (SolidColorBrush)FindResource("TextPrimary")
            };
            System.Windows.Automation.AutomationProperties.SetName(typed, automationName);
            string Show(double v) => v.ToString("0.#", CultureInfo.CurrentCulture);
            typed.Text = Show(slider.Value);

            slider.ValueChanged += (_, e) =>
            {
                typed.Text = Show(e.NewValue);
                apply(e.NewValue);
            };
            void Commit()
            {
                if (parse(typed.Text, out double v))
                {
                    slider.Value = v;           // ValueChanged writes the clamped number back into the box
                    typed.Text = Show(slider.Value);
                    apply(slider.Value);
                }
                else typed.Text = Show(slider.Value);   // not a number: keep what the slider says
            }
            typed.LostFocus += (_, _) => Commit();
            typed.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            };
            panel.Children.Add(slider);
            panel.Children.Add(typed);
            panel.Children.Add(new TextBlock
            {
                Text = unit, Foreground = (SolidColorBrush)FindResource("TextSecondary"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0)
            });
        }

        private void HideStampSettings()
        {
            if (_stampBar is not null)
            {
                (PagePreviewPanel.Parent as Grid)?.Children.Remove(_stampBar);
                _stampBar = null;
            }
        }
    }
}
