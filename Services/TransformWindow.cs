using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TDPdf.Services
{
    /// <summary>
    /// TDPdf's themed "Transform" dialog. Renders the current page on its own preview canvas (so the
    /// main view mode is irrelevant) and lets the user apply an arbitrary fine rotation, scale, flip and
    /// straighten-by-line, with a live preview on the left and the controls in a right-hand column — the
    /// mirror of <see cref="PrintPreviewWindow"/>. Apply hands the chosen angle / scale / flip / page-mode
    /// back to the caller (MainWindow.ApplyPageTransform), which rasterizes the page at full resolution.
    ///
    /// The preview and the full-resolution Apply share the same bitmap composition helpers
    /// (<see cref="TDPdf.MainWindow.ComposeTransform"/>), so what you see is what Apply produces.
    /// </summary>
    public sealed class TransformWindow : Window
    {
        // ---- Result (read by the caller after ShowDialog) --------------------
        public bool Applied { get; private set; }
        public double Angle { get; private set; }      // total degrees = quarter turns + fine deskew
        public double Scale { get; private set; } = 1.0;
        public bool FixedPage { get; private set; }     // true = keep the page box; false = grow/shrink the page
        public bool FlipH { get; private set; }
        public bool FlipV { get; private set; }

        private const string CloseGlyph = "";     // Segoe MDL2 Assets close glyph

        private readonly BitmapSource _src;
        private readonly double _srcW;
        private readonly double _srcH;
        private readonly double _pageWpt;
        private readonly double _pageHpt;

        private readonly Image _preview = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.45 }
        };
        private Border _previewArea = null!;

        // Working state, mutated by the controls.
        private int _quarter;          // 0..3 quarter turns clockwise
        private double _fine;          // fine deskew, degrees (-45..45)
        private double _scale = 1.0;   // fraction (1.0 = 100%)
        private bool _fixedPage;       // false = resize page (expand), true = keep page size
        private bool _flipH;
        private bool _flipV;

        // Named controls / readouts filled by BuildUi.
        private Slider _rotSlider   = null!;
        private Slider _scaleSlider = null!;
        private TextBox _angleBox   = null!;
        private TextBlock _rotReadout   = null!;
        private TextBlock _scaleReadout = null!;
        private TextBlock _sizeReadout  = null!;
        private RadioButton _expandRadio = null!;
        private CheckBox _flipHCheck  = null!;
        private CheckBox _flipVCheck  = null!;
        private CheckBox _deskewCheck = null!;
        private TextBlock _lineCoords = null!;

        // Straighten-by-line overlay.
        private Canvas _lineCanvas = null!;
        private Line _alignLine     = null!;
        private bool _drawingLine;
        private Point _startPagePt;

        // Coalesces rapid slider changes: the heavy compose (scaling a page up makes a big bitmap) only
        // runs ~25x/sec on the latest value, so dragging stays smooth instead of queuing a backlog.
        private readonly DispatcherTimer _previewTimer;
        private bool _suppressAngleSync;   // guards the two-way slider <-> numeric-field binding

        public TransformWindow(Window? owner, BitmapSource src, double pageWpt, double pageHpt)
        {
            _src     = src;
            _srcW    = src.PixelWidth;
            _srcH    = src.PixelHeight;
            _pageWpt = pageWpt > 0 ? pageWpt : src.PixelWidth;
            _pageHpt = pageHpt > 0 ? pageHpt : src.PixelHeight;

            Title  = "TDPdf - Transform";
            Width  = 980;
            Height = 720;
            MinWidth  = 680;
            MinHeight = 480;
            WindowStyle           = WindowStyle.None;
            AllowsTransparency    = true;
            Background            = Brushes.Transparent;
            ResizeMode            = ResizeMode.CanResize;
            Owner                 = owner;
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen;

            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                ResizeBorderThickness = new Thickness(8),
                CaptionHeight         = 0,
                GlassFrameThickness   = new Thickness(0),
                CornerRadius          = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });

            if (owner?.TryFindResource(typeof(System.Windows.Controls.Primitives.ScrollBar)) is Style sbStyle)
                Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = sbStyle;

            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _previewTimer.Tick += (_, _2) => { _previewTimer.Stop(); UpdatePreview(); };

            BuildUi();
            UpdatePreview();   // seed the preview + output-size readout at the original dimensions

            KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitAndClose(); };
        }

        private double Total => _quarter * 90 + _fine;

        // ---- Theme helpers (mirror PrintPreviewWindow / TdpDialog so theming stays on our palette) ----

        private static SolidColorBrush R(string key) =>
            Application.Current?.TryFindResource(key) as SolidColorBrush ?? SystemBrush(key);

        private static SolidColorBrush SystemBrush(string key) => key switch
        {
            "AccentGreen"    => SystemColors.HighlightBrush,
            "AccentGreenDim" => SystemColors.HighlightBrush,
            "DangerRed"      => SystemColors.HighlightBrush,
            "BgDark"         => SystemColors.WindowBrush,
            "BgPanel"        => SystemColors.WindowBrush,
            "BgHover"        => SystemColors.ControlBrush,
            "BgPressed"      => SystemColors.ControlDarkBrush,
            "BorderDim"      => SystemColors.WindowTextBrush,
            "TextPrimary"    => SystemColors.WindowTextBrush,
            "TextSecondary"  => SystemColors.GrayTextBrush,
            _                => SystemColors.WindowTextBrush
        };

        private Style? FindOwnerStyle(string key) => Owner?.TryFindResource(key) as Style;

        // ---- UI construction -------------------------------------------------

        private void BuildUi()
        {
            var outer = new Border
            {
                Background      = R("BgDark"),
                BorderBrush     = R("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Margin          = new Thickness(14),
                Effect          = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = 0.5 }
            };
            var root = new DockPanel();
            outer.Child = root;
            Content = outer;

            // ---- Title bar ----
            var titleBar = new Border
            {
                Background   = R("BgPanel"),
                Padding      = new Thickness(0),
                CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            DockPanel.SetDock(titleBar, Dock.Top);
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleText = new TextBlock
            {
                Text       = "TDPdf - Transform",
                Foreground = R("AccentGreen"),
                FontWeight = FontWeights.SemiBold,
                FontSize   = 13,
                FontFamily = new FontFamily("Consolas"),
                Margin     = new Thickness(16, 10, 0, 10),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleText, 0);

            var closeBtn = new Button { Content = CloseGlyph };
            if (FindOwnerStyle("ChromeCloseButton") is Style chromeClose)
            {
                closeBtn.Style = chromeClose;
            }
            else
            {
                closeBtn.FontFamily      = new FontFamily("Segoe MDL2 Assets");
                closeBtn.FontSize        = 10;
                closeBtn.Width           = 40;
                closeBtn.Foreground      = R("DangerRed");
                closeBtn.Background      = Brushes.Transparent;
                closeBtn.BorderThickness = new Thickness(0);
                closeBtn.Cursor          = Cursors.Hand;
            }
            closeBtn.Click += (_, _) => { Applied = false; Close(); };
            Grid.SetColumn(closeBtn, 1);

            titleGrid.Children.Add(titleText);
            titleGrid.Children.Add(closeBtn);
            titleBar.Child = titleGrid;
            root.Children.Add(titleBar);

            // ---- Body: preview (fills) | settings column (fixed) ----
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(288) });
            root.Children.Add(body);

            body.Children.Add(BuildPreviewColumn());
            body.Children.Add(BuildSettingsColumn());
        }

        private UIElement BuildPreviewColumn()
        {
            var wrap = new Border
            {
                Background      = R("BgDark"),
                BorderBrush     = R("BorderDim"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Margin          = new Thickness(8, 6, 4, 12),
                ClipToBounds    = true
            };
            Grid.SetColumn(wrap, 0);

            var grid = new Grid();

            RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
            _preview.Source = _src;
            grid.Children.Add(_preview);

            // Straighten overlay: when "Draw a level line" is on, the user drags a reference line across the
            // page and it rotates so that line becomes level. Hit-testing is off until enabled so it never
            // interferes with the rest of the preview.
            _lineCanvas = new Canvas { Background = Brushes.Transparent, IsHitTestVisible = false, Cursor = Cursors.Cross };
            _alignLine = new Line
            {
                Stroke = R("AccentGreen"),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Visibility = Visibility.Collapsed,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.White, BlurRadius = 3, ShadowDepth = 0, Opacity = 0.8 }
            };
            _lineCanvas.Children.Add(_alignLine);
            _lineCanvas.MouseLeftButtonDown += LineCanvas_Down;
            _lineCanvas.MouseMove           += LineCanvas_Move;
            _lineCanvas.MouseLeftButtonUp   += LineCanvas_Up;
            grid.Children.Add(_lineCanvas);

            wrap.Child = grid;
            _previewArea = wrap;
            wrap.SizeChanged += (_, _2) => SizePreviewImage();
            return wrap;
        }

        private UIElement BuildSettingsColumn()
        {
            var panel = new StackPanel { Margin = new Thickness(14, 12, 14, 6) };

            // ---- Rotate ----
            panel.Children.Add(SectionHeader("ROTATE"));
            var turnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };
            var turnL = MakeButton("↺ 90°", false);
            turnL.Margin = new Thickness(0, 0, 6, 0);
            turnL.Click += (_, _2) => { _quarter = (_quarter + 3) % 4; UpdatePreview(); };
            var turnR = MakeButton("90° ↻", false);
            turnR.Click += (_, _2) => { _quarter = (_quarter + 1) % 4; UpdatePreview(); };
            turnRow.Children.Add(turnL);
            turnRow.Children.Add(turnR);
            panel.Children.Add(turnRow);

            _rotSlider = new Slider
            {
                Minimum = -45, Maximum = 45, Value = 0,
                TickFrequency = 1, SmallChange = 0.1, LargeChange = 1,
                Margin = new Thickness(0, 2, 0, 2),
                Foreground = R("AccentGreen")
            };
            _rotSlider.ValueChanged += (_, ev) =>
            {
                _fine = Math.Round(ev.NewValue, 1);
                _rotReadout.Text = $"{Total:0.0}°";
                if (!_suppressAngleSync)
                {
                    _suppressAngleSync = true;
                    _angleBox.Text = _fine.ToString("0.0", CultureInfo.InvariantCulture);
                    _suppressAngleSync = false;
                }
                SchedulePreview();
            };
            panel.Children.Add(_rotSlider);

            // Numeric fine-angle field (edits the slider's fine value directly) + live total readout + reset.
            var angleRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var angleReset = MakeButton("Reset", false);
            angleReset.Padding = new Thickness(8, 1, 8, 1);
            angleReset.FontSize = 11;
            angleReset.Click += (_, _2) => { _quarter = 0; _rotSlider.Value = 0; UpdatePreview(); };
            DockPanel.SetDock(angleReset, Dock.Right);
            angleRow.Children.Add(angleReset);

            _angleBox = MakeTextBox("0.0");
            _angleBox.Width = 56;
            _angleBox.Margin = new Thickness(0, 0, 8, 0);
            _angleBox.TextAlignment = TextAlignment.Right;
            void CommitAngleBox()
            {
                if (double.TryParse(_angleBox.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    v = Math.Max(-45, Math.Min(45, v));
                    _suppressAngleSync = true;
                    _rotSlider.Value = Math.Round(v, 1);   // drives _fine + preview
                    _suppressAngleSync = false;
                    _fine = Math.Round(v, 1);
                    _rotReadout.Text = $"{Total:0.0}°";
                    SchedulePreview();
                }
                _angleBox.Text = _fine.ToString("0.0", CultureInfo.InvariantCulture);
            }
            _angleBox.LostFocus += (_, _2) => CommitAngleBox();
            _angleBox.KeyDown += (_, ev) => { if (ev.Key == Key.Enter) { CommitAngleBox(); ev.Handled = true; } };
            DockPanel.SetDock(_angleBox, Dock.Right);
            angleRow.Children.Add(_angleBox);

            var angleLabel = new StackPanel { Orientation = Orientation.Horizontal };
            angleLabel.Children.Add(new TextBlock
            {
                Text = "Angle", Foreground = R("TextSecondary"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
            });
            _rotReadout = new TextBlock
            {
                Text = "0.0°", Foreground = R("TextPrimary"), FontFamily = new FontFamily("Consolas"),
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            };
            angleLabel.Children.Add(_rotReadout);
            angleRow.Children.Add(angleLabel);
            panel.Children.Add(angleRow);

            panel.Children.Add(Divider());

            // ---- Scale ----
            panel.Children.Add(SectionHeader("SCALE"));
            _scaleSlider = new Slider
            {
                Minimum = 10, Maximum = 400, Value = 100,
                TickFrequency = 5, SmallChange = 1, LargeChange = 10,
                Margin = new Thickness(0, 2, 0, 2),
                Foreground = R("AccentGreen")
            };
            _scaleSlider.ValueChanged += (_, ev) =>
            {
                _scale = Math.Round(ev.NewValue) / 100.0;
                _scaleReadout.Text = $"{ev.NewValue:0}%";
                SchedulePreview();
            };
            panel.Children.Add(_scaleSlider);

            var scaleRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var scaleReset = MakeButton("Reset", false);
            scaleReset.Padding = new Thickness(8, 1, 8, 1);
            scaleReset.FontSize = 11;
            scaleReset.Click += (_, _2) => _scaleSlider.Value = 100;
            DockPanel.SetDock(scaleReset, Dock.Right);
            scaleRow.Children.Add(scaleReset);
            _scaleReadout = new TextBlock
            {
                Text = "100%", Foreground = R("TextPrimary"), FontFamily = new FontFamily("Consolas"),
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 8, 0)
            };
            DockPanel.SetDock(_scaleReadout, Dock.Right);
            scaleRow.Children.Add(_scaleReadout);
            scaleRow.Children.Add(new TextBlock
            {
                Text = "Size", Foreground = R("TextSecondary"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(scaleRow);

            // Page-size mode: expand (resize the page) vs fixed (keep the page box; content pads/clips white).
            panel.Children.Add(new TextBlock
            {
                Text = "When rotating or scaling:", Foreground = R("TextSecondary"),
                FontSize = 11, Margin = new Thickness(0, 10, 0, 4)
            });
            _expandRadio = MakeRadio("Resize the page (expand to fit)", true);
            var fixedRadio = MakeRadio("Keep the page size (pad / clip)", false);
            _expandRadio.Checked += (_, _2) => { _fixedPage = false; UpdatePreview(); };
            fixedRadio.Checked   += (_, _2) => { _fixedPage = true;  UpdatePreview(); };
            panel.Children.Add(_expandRadio);
            panel.Children.Add(fixedRadio);

            _sizeReadout = new TextBlock
            {
                Foreground = R("TextSecondary"), FontFamily = new FontFamily("Consolas"),
                FontSize = 11, Margin = new Thickness(0, 8, 0, 0)
            };
            panel.Children.Add(_sizeReadout);

            panel.Children.Add(Divider());

            // ---- Flip ----
            panel.Children.Add(SectionHeader("FLIP"));
            _flipHCheck = MakeCheck("Flip horizontal");
            _flipHCheck.Checked   += (_, _2) => { _flipH = true;  UpdatePreview(); };
            _flipHCheck.Unchecked += (_, _2) => { _flipH = false; UpdatePreview(); };
            panel.Children.Add(_flipHCheck);
            _flipVCheck = MakeCheck("Flip vertical");
            _flipVCheck.Checked   += (_, _2) => { _flipV = true;  UpdatePreview(); };
            _flipVCheck.Unchecked += (_, _2) => { _flipV = false; UpdatePreview(); };
            panel.Children.Add(_flipVCheck);

            panel.Children.Add(Divider());

            // ---- Straighten ----
            panel.Children.Add(SectionHeader("STRAIGHTEN"));
            _deskewCheck = MakeCheck("Draw a level line");
            _deskewCheck.Checked   += (_, _2) => { _lineCanvas.IsHitTestVisible = true; };
            _deskewCheck.Unchecked += (_, _2) =>
            {
                _lineCanvas.IsHitTestVisible = false;
                _alignLine.Visibility = Visibility.Collapsed;
                _lineCoords.Text = "";
            };
            panel.Children.Add(_deskewCheck);
            panel.Children.Add(new TextBlock
            {
                Text = "Drag along something that should be level (or vertical); the page rotates to level it.",
                Foreground = R("TextSecondary"), FontSize = 10, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
            _lineCoords = new TextBlock
            {
                Text = "", Foreground = R("TextSecondary"), FontFamily = new FontFamily("Consolas"),
                FontSize = 11, LineHeight = 16, Margin = new Thickness(0, 6, 0, 0)
            };
            panel.Children.Add(_lineCoords);

            // ---- Buttons pinned below the scroller ----
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var reset = MakeButton("Reset all", false);
            reset.Margin = new Thickness(0, 0, 8, 0);
            reset.Click += (_, _2) =>
            {
                _quarter = 0; _rotSlider.Value = 0; _scaleSlider.Value = 100;
                _expandRadio.IsChecked = true; _flipHCheck.IsChecked = false; _flipVCheck.IsChecked = false;
                UpdatePreview();
            };
            var cancel = MakeButton("Cancel", false);
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (_, _2) => { Applied = false; Close(); };
            cancel.IsCancel = true;
            var apply = MakeButton("Apply", true);
            apply.Click += (_, _2) => CommitAndClose();
            apply.IsDefault = true;
            btnRow.Children.Add(reset);
            btnRow.Children.Add(cancel);
            btnRow.Children.Add(apply);

            var optionsScroller = new ScrollViewer
            {
                Content                       = panel,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(optionsScroller, 0);

            var btnHost = new Border { Child = btnRow, Padding = new Thickness(14, 8, 12, 12) };
            Grid.SetRow(btnHost, 1);

            var column = new Grid();
            column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            column.Children.Add(optionsScroller);
            column.Children.Add(btnHost);
            Grid.SetColumn(column, 1);
            return column;
        }

        private void CommitAndClose()
        {
            Applied   = true;
            Angle     = Total;
            Scale     = _scale;
            FixedPage = _fixedPage;
            FlipH     = _flipH;
            FlipV     = _flipV;
            Close();
        }

        // ---- Straighten-by-line: drag a line, release, and the page rotates to make that line level ----

        // Maps a point in the preview image to page coordinates in points (clamped to the page).
        private Point PreviewToPagePts(Point pInPreview)
        {
            double w = _preview.ActualWidth, h = _preview.ActualHeight;
            double fx = w > 0 ? Math.Max(0, Math.Min(1, pInPreview.X / w)) : 0;
            double fy = h > 0 ? Math.Max(0, Math.Min(1, pInPreview.Y / h)) : 0;
            return new Point(fx * _pageWpt, fy * _pageHpt);
        }

        private void ShowLineCoords(Point endPage)
            => _lineCoords.Text = $"Start  {_startPagePt.X:0}, {_startPagePt.Y:0} pt\nEnd    {endPage.X:0}, {endPage.Y:0} pt";

        private void LineCanvas_Down(object sender, MouseButtonEventArgs e)
        {
            _drawingLine = true;
            var p = e.GetPosition(_lineCanvas);
            _alignLine.X1 = _alignLine.X2 = p.X;
            _alignLine.Y1 = _alignLine.Y2 = p.Y;
            _alignLine.Visibility = Visibility.Visible;
            _startPagePt = PreviewToPagePts(e.GetPosition(_preview));
            ShowLineCoords(_startPagePt);
            _lineCanvas.CaptureMouse();
        }

        private void LineCanvas_Move(object sender, MouseEventArgs e)
        {
            if (!_drawingLine) return;
            var p = e.GetPosition(_lineCanvas);
            _alignLine.X2 = p.X;
            _alignLine.Y2 = p.Y;
            ShowLineCoords(PreviewToPagePts(e.GetPosition(_preview)));
        }

        private void LineCanvas_Up(object sender, MouseButtonEventArgs e)
        {
            if (!_drawingLine) return;
            _drawingLine = false;
            _lineCanvas.ReleaseMouseCapture();

            double dx = _alignLine.X2 - _alignLine.X1;
            double dy = _alignLine.Y2 - _alignLine.Y1;
            _alignLine.Visibility = Visibility.Collapsed;
            if (dx * dx + dy * dy < 100) return;   // ignore an accidental tap

            // Screen angle of the line (clockwise positive, since Y is down). Normalise to an undirected
            // (-90, 90], then snap to the nearest axis so a near-vertical drag deskews to vertical.
            double a = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            a %= 180.0;
            if (a > 90.0) a -= 180.0; else if (a < -90.0) a += 180.0;
            if (a > 45.0) a -= 90.0;  else if (a < -45.0) a += 90.0;

            // Rotate by -a (on top of the current fine angle) to level the line; the slider drives _fine.
            double newFine = Math.Max(-45.0, Math.Min(45.0, _fine - a));
            _rotSlider.Value = Math.Round(newFine, 1);
        }

        // ---- Preview compose (shares MainWindow.ComposeTransform with full-resolution Apply) ----

        private void SchedulePreview()
        {
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private void UpdatePreview()
        {
            double total = Total;
            _rotReadout.Text = $"{total:0.0}°";
            _preview.Source = (total == 0 && Math.Abs(_scale - 1.0) < 0.001 && !_flipH && !_flipV)
                ? _src
                : TDPdf.MainWindow.ComposeTransform(_src, total, _scale, _fixedPage, _flipH, _flipV);

            if (_preview.Source is BitmapSource b && _srcW > 0 && _srcH > 0 && _pageWpt > 0)
            {
                double outWin = b.PixelWidth  * (_pageWpt / _srcW) / 72.0;
                double outHin = b.PixelHeight * (_pageHpt / _srcH) / 72.0;
                _sizeReadout.Text = $"Output  {outWin:0.0} × {outHin:0.0} in";
            }
            SizePreviewImage();
        }

        // Sizes the page to its TRUE relative scale within the preview box, so "Resize the page" makes the
        // page visibly shrink and rotation visibly grows it. Clamps so the page never overflows the box.
        private void SizePreviewImage()
        {
            if (_previewArea is null || _preview.Source is not BitmapSource bmp || _srcW <= 0 || _srcH <= 0) return;
            const double m = 40;   // breathing room inside the box
            double areaW = Math.Max(1, _previewArea.ActualWidth - m);
            double areaH = Math.Max(1, _previewArea.ActualHeight - m);
            double baseFit = Math.Min(areaW / _srcW, areaH / _srcH);   // scale that fits the original page
            double dispW = bmp.PixelWidth * baseFit;
            double dispH = bmp.PixelHeight * baseFit;
            double clamp = Math.Min(1.0, Math.Min(areaW / dispW, areaH / dispH));   // never overflow the box
            _preview.Width  = dispW * clamp;
            _preview.Height = dispH * clamp;
        }

        // ---- Small themed control factory ------------------------------------

        private TextBlock SectionHeader(string text) => new()
        {
            Text = text, Foreground = R("TextSecondary"),
            FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4)
        };

        private Border Divider() => new()
        {
            Height = 1, Background = R("BorderDim"), Margin = new Thickness(0, 14, 0, 12)
        };

        private TextBox MakeTextBox(string text) => new()
        {
            Text        = text,
            Background  = R("BgPanel"),
            Foreground  = R("TextPrimary"),
            BorderBrush = R("BorderDim"),
            CaretBrush  = R("TextPrimary"),
            Padding     = new Thickness(6, 3, 6, 3)
        };

        private RadioButton MakeRadio(string text, bool isChecked)
        {
            var rb = new RadioButton
            {
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                IsChecked = isChecked,
                Foreground = R("TextPrimary"),
                FontSize = 12,
                Margin = new Thickness(0, 3, 0, 0)
            };
            return rb;
        }

        private CheckBox MakeCheck(string text) => new()
        {
            Content = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
            Foreground = R("TextPrimary"),
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0)
        };

        private static ControlTemplate FlatTemplate()
        {
            var bf = new FrameworkElementFactory(typeof(Border));
            bf.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            bf.SetBinding(Border.BorderBrushProperty,
                new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            bf.SetBinding(Border.BorderThicknessProperty,
                new System.Windows.Data.Binding("BorderThickness") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            bf.SetBinding(Border.PaddingProperty,
                new System.Windows.Data.Binding("Padding") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            bf.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bf.AppendChild(cp);
            return new ControlTemplate(typeof(Button)) { VisualTree = bf };
        }

        private Button MakeButton(string label, bool accent)
        {
            var bgNorm = accent ? R("AccentGreenDim") : R("BgPanel");
            var bgHov  = accent ? R("BgPressed") : R("BgHover");
            var btn = new Button
            {
                Content         = label,
                Padding         = new Thickness(16, 6, 16, 6),
                Background      = bgNorm,
                Foreground      = accent ? R("AccentGreen") : R("TextPrimary"),
                BorderBrush     = accent ? R("AccentGreen") : R("BorderDim"),
                BorderThickness = new Thickness(1),
                FontSize        = 12,
                Cursor          = Cursors.Hand,
                Template        = FlatTemplate()
            };
            btn.MouseEnter += (_, _2) => btn.Background = bgHov;
            btn.MouseLeave += (_, _2) => btn.Background = bgNorm;
            return btn;
        }
    }
}
