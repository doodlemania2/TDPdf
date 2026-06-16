using System;
using System.Collections.Generic;
using System.Linq;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace TDPdf.Services
{
    /// <summary>
    /// TDPdf's own print dialog with a working preview. WPF's built-in PrintDialog
    /// reports "This app doesn't support print preview", so we rasterize the pages
    /// ourselves (via Docnet/PDFium), expose printer / orientation / copies /
    /// page-range settings, and drive the spooler via a non-UI PrintDialog when the
    /// user clicks Print.
    ///
    /// The caller is expected to hand us a flattened/printable PDF path (annotations
    /// already burned in) plus each page's size in points; we never touch the live
    /// document model.
    /// </summary>
    public sealed class PrintPreviewWindow : Window
    {
        private const double PointPerInch = 72.0;
        private const double RenderDpi = 200.0;

        private readonly string _pdfPath;
        private readonly IReadOnlyList<Size> _pageSizes;   // in points
        private readonly int _pageCount;
        private readonly BitmapSource?[] _cache;           // lazily rasterized previews

        private readonly List<PrintQueue> _queues = [];
        private PrintQueue? _queue;
        private LocalPrintServer? _server;   // kept alive: queues reference their server
        private bool _landscape;
        private int _previewIndex;

        // Printable area in DIPs for the currently selected printer + orientation.
        private double _areaW = 816;   // Letter portrait fallback (8.5in * 96)
        private double _areaH = 1056;  // (11in * 96)

        private readonly Grid _previewHost = new();
        private readonly TextBlock _pageLabel = new();
        private ComboBox _printerCombo = null!;
        private TextBox _copiesBox = null!;
        private TextBox _pagesBox = null!;

        // Segoe MDL2 Assets close glyph, matching the main window chrome close button.
        private const string CloseGlyph = "";

        /// <summary>Number of pages sent to the printer (set when the user prints).</summary>
        public int PrintedPageCount { get; private set; }

        public PrintPreviewWindow(Window? owner, string pdfPath, IReadOnlyList<Size> pageSizes)
        {
            _pdfPath    = pdfPath;
            _pageSizes  = pageSizes;
            _pageCount  = pageSizes.Count;
            _cache      = new BitmapSource?[_pageCount];

            Title  = "TDPdf - Print";
            Width  = 920;
            Height = 700;
            MinWidth  = 720;
            MinHeight = 480;
            WindowStyle           = WindowStyle.None;
            AllowsTransparency    = true;
            Background            = Brushes.Transparent;
            ResizeMode            = ResizeMode.CanResize;
            Owner                 = owner;
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen;

            // Borderless windows (WindowStyle.None) have no native resize border, so
            // WindowChrome restores edge resizing without showing the grip handle.
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                ResizeBorderThickness = new Thickness(8),
                CaptionHeight         = 0,
                GlassFrameThickness   = new Thickness(0),
                CornerRadius          = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });

            BuildUi();
            LoadPrinters();
            RefreshArea();
            UpdatePreview();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try { _server?.Dispose(); } catch { /* best effort */ }
        }

        // Resolves a themed brush, falling back to a sensible system brush when the
        // resource is unavailable (mirrors TdpDialog's lookup so theming stays
        // centralized on our palette).
        private static SolidColorBrush R(string key)
        {
            return Application.Current?.TryFindResource(key) as SolidColorBrush
                ?? SystemBrush(key);
        }

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

        // Pulls a named Style from the owning window so the dialog can reuse the
        // app's themed ComboBox / chrome-close-button styling when present.
        private Style? FindOwnerStyle(string key) => Owner?.TryFindResource(key) as Style;

        private void ApplyComboStyle(ComboBox combo)
        {
            if (FindOwnerStyle("DarkComboBox") is Style s)
            {
                combo.Style = s;
            }
            else
            {
                combo.Foreground  = R("TextPrimary");
                combo.BorderBrush  = R("BorderDim");
                combo.Background   = R("BgPanel");
            }
        }

        // ---- UI construction -------------------------------------------------

        private void BuildUi()
        {
            var outer = new Border
            {
                Background      = R("BgDark"),
                BorderBrush     = R("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Margin          = new Thickness(14),   // room for the drop shadow
                Effect          = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color       = Colors.Black,
                    BlurRadius  = 14,
                    ShadowDepth = 2,
                    Direction   = 270,
                    Opacity     = 0.5
                }
            };
            var root = new DockPanel();
            outer.Child = root;
            Content = outer;

            // Title bar
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
                Text       = "TDPdf - Print",
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
                closeBtn.Background       = Brushes.Transparent;
                closeBtn.BorderThickness  = new Thickness(0);
                closeBtn.Cursor          = Cursors.Hand;
            }
            closeBtn.Click += (_, _) => { DialogResult = false; Close(); };
            Grid.SetColumn(closeBtn, 1);

            titleGrid.Children.Add(titleText);
            titleGrid.Children.Add(closeBtn);
            titleBar.Child = titleGrid;
            root.Children.Add(titleBar);

            // Body: settings | preview
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(body);

            body.Children.Add(BuildSettingsColumn());
            body.Children.Add(BuildPreviewColumn());
        }

        private UIElement BuildSettingsColumn()
        {
            var panel = new StackPanel { Margin = new Thickness(16, 14, 12, 14) };
            Grid.SetColumn(panel, 0);

            panel.Children.Add(Label("Printer"));
            var printerCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(printerCombo);
            printerCombo.SelectionChanged += (s, _) =>
            {
                int i = ((ComboBox)s).SelectedIndex;
                if (i >= 0 && i < _queues.Count) { _queue = _queues[i]; RefreshArea(); UpdatePreview(); }
            };
            _printerCombo = printerCombo;
            panel.Children.Add(printerCombo);

            panel.Children.Add(Label("Orientation"));
            var orient = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(orient);
            orient.Items.Add("Portrait");
            orient.Items.Add("Landscape");
            orient.SelectedIndex = 0;
            orient.SelectionChanged += (s, _) =>
            {
                _landscape = ((ComboBox)s).SelectedIndex == 1;
                RefreshArea();
                UpdatePreview();
            };
            panel.Children.Add(orient);

            panel.Children.Add(Label("Copies"));
            _copiesBox = MakeTextBox("1");
            panel.Children.Add(_copiesBox);

            panel.Children.Add(Label("Pages"));
            _pagesBox = MakeTextBox("");
            _pagesBox.Margin = new Thickness(0, 4, 0, 2);
            panel.Children.Add(_pagesBox);
            panel.Children.Add(new TextBlock
            {
                Text         = "e.g. 1-3,5  (blank = all)",
                Foreground   = R("TextSecondary"),
                FontSize     = 11,
                Margin       = new Thickness(0, 0, 0, 16),
                TextWrapping = TextWrapping.Wrap
            });

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = MakeButton("Cancel", false);
            cancel.Click += (_, _) => { DialogResult = false; Close(); };
            var print = MakeButton("Print", true);
            print.Margin = new Thickness(8, 0, 0, 0);
            print.Click += (_, _) => DoPrint();
            btnRow.Children.Add(cancel);
            btnRow.Children.Add(print);
            panel.Children.Add(btnRow);

            return panel;
        }

        private TextBox MakeTextBox(string text) => new()
        {
            Text        = text,
            Margin      = new Thickness(0, 4, 0, 12),
            Background  = R("BgPanel"),
            Foreground  = R("TextPrimary"),
            BorderBrush = R("BorderDim"),
            CaretBrush  = R("TextPrimary"),
            Padding     = new Thickness(6, 4, 6, 4)
        };

        private UIElement BuildPreviewColumn()
        {
            var wrap = new Border
            {
                Background   = R("BgDark"),
                Margin       = new Thickness(0, 4, 8, 12),
                CornerRadius = new CornerRadius(4)
            };
            Grid.SetColumn(wrap, 1);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(_previewHost, 0);
            grid.Children.Add(_previewHost);

            var nav = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 6, 0, 8)
            };
            var prev = MakeButton("◀", false);   // left triangle
            prev.Click += (_, _) => { if (_previewIndex > 0) { _previewIndex--; UpdatePreview(); } };
            var next = MakeButton("▶", false);   // right triangle
            next.Click += (_, _) => { if (_previewIndex < _pageCount - 1) { _previewIndex++; UpdatePreview(); } };
            _pageLabel.Foreground = R("TextPrimary");
            _pageLabel.VerticalAlignment = VerticalAlignment.Center;
            _pageLabel.Margin = new Thickness(12, 0, 12, 0);
            _pageLabel.FontSize = 12;
            nav.Children.Add(prev);
            nav.Children.Add(_pageLabel);
            nav.Children.Add(next);
            Grid.SetRow(nav, 1);
            grid.Children.Add(nav);

            wrap.Child = grid;
            return wrap;
        }

        private static TextBlock Label(string text) => new()
        {
            Text       = text,
            Foreground = R("TextPrimary"),
            FontSize   = 12,
            FontWeight = FontWeights.SemiBold
        };

        // ---- Behavior --------------------------------------------------------

        private void LoadPrinters()
        {
            try
            {
                _server = new LocalPrintServer();
                var found = _server.GetPrintQueues(
                [
                    EnumeratedPrintQueueTypes.Local,
                    EnumeratedPrintQueueTypes.Connections
                ]);
                foreach (var q in found) _queues.Add(q);
            }
            catch { /* spooler unavailable; fall back to default below */ }

            PrintQueue? def = null;
            try { def = LocalPrintServer.GetDefaultPrintQueue(); } catch { /* no default */ }
            if (def != null && !_queues.Any(q => q.FullName == def.FullName))
                _queues.Insert(0, def);

            foreach (var q in _queues) _printerCombo.Items.Add(q.FullName);

            int sel = def != null ? _queues.FindIndex(q => q.FullName == def.FullName) : 0;
            if (_queues.Count > 0)
            {
                _printerCombo.SelectedIndex = sel >= 0 ? sel : 0;
                _queue = _queues[_printerCombo.SelectedIndex];
            }
        }

        private void RefreshArea()
        {
            double w = 816, h = 1056;   // Letter portrait fallback
            try
            {
                if (_queue != null)
                {
                    var pd = new PrintDialog { PrintQueue = _queue };
                    if (pd.PrintableAreaWidth > 0 && pd.PrintableAreaHeight > 0)
                    {
                        w = pd.PrintableAreaWidth;
                        h = pd.PrintableAreaHeight;
                    }
                }
            }
            catch { /* keep fallback */ }

            // Normalize to the requested orientation.
            if (_landscape) { if (w < h) (w, h) = (h, w); }
            else            { if (w > h) (w, h) = (h, w); }

            _areaW = w;
            _areaH = h;
        }

        // Rasterizes (and caches) a single page at preview resolution via PDFium.
        private BitmapSource? GetPageBitmap(int idx)
        {
            if (idx < 0 || idx >= _pageCount) return null;
            if (_cache[idx] is BitmapSource cached) return cached;

            try
            {
                using var docReader = DocLib.Instance.GetDocReader(
                    _pdfPath, new PageDimensions(RenderDpi / PointPerInch));
                using var pr = docReader.GetPageReader(idx);
                int w = pr.GetPageWidth();
                int h = pr.GetPageHeight();
                var raw = pr.GetImage();
                if (w <= 0 || h <= 0 || raw == null || raw.Length == 0) return null;

                var bmp = new WriteableBitmap(w, h, RenderDpi, RenderDpi, PixelFormats.Bgra32, null);
                bmp.WritePixels(new Int32Rect(0, 0, w, h), raw, w * 4, 0);
                bmp.Freeze();
                _cache[idx] = bmp;
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private void UpdatePreview()
        {
            _previewHost.Children.Clear();
            if (_pageCount == 0) { _pageLabel.Text = "No pages"; return; }

            int idx = Math.Max(0, Math.Min(_previewIndex, _pageCount - 1));
            _previewIndex = idx;

            var bmp = GetPageBitmap(idx);
            var paper = new Grid { Width = _areaW, Height = _areaH, Background = Brushes.White };
            if (bmp != null)
            {
                double scale = Math.Min(_areaW / bmp.PixelWidth, _areaH / bmp.PixelHeight);
                var img = new Image
                {
                    Source              = bmp,
                    Width               = bmp.PixelWidth * scale,
                    Height              = bmp.PixelHeight * scale,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                paper.Children.Add(img);
            }

            var vb = new Viewbox { Child = paper, Stretch = Stretch.Uniform, Margin = new Thickness(20) };
            _previewHost.Children.Add(vb);

            _pageLabel.Text = $"Page {idx + 1} of {_pageCount}";
        }

        private void DoPrint()
        {
            if (_queue == null)
            {
                TdpDialog.Show(this, "No printer is available.", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var indices = ParseRange(_pagesBox.Text, _pageCount);
            if (indices.Count == 0)
            {
                TdpDialog.Show(this, "No valid pages in that range.", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int.TryParse(_copiesBox.Text?.Trim(), out int copies);
            if (copies < 1) copies = 1;

            try
            {
                var pd = new PrintDialog { PrintQueue = _queue };
                var ticket = pd.PrintTicket;
                ticket.CopyCount       = copies;
                ticket.PageOrientation = _landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
                pd.PrintTicket = ticket;

                double aw = pd.PrintableAreaWidth, ah = pd.PrintableAreaHeight;
                if (_landscape) { if (aw < ah) (aw, ah) = (ah, aw); }
                else            { if (aw > ah) (aw, ah) = (ah, aw); }
                if (aw <= 0 || ah <= 0) { aw = _areaW; ah = _areaH; }

                var fixedDoc = new FixedDocument();
                foreach (int idx in indices)
                {
                    var bmp = GetPageBitmap(idx);
                    if (bmp == null) continue;

                    double scale = Math.Min(aw / bmp.PixelWidth, ah / bmp.PixelHeight);
                    double iw = bmp.PixelWidth * scale;
                    double ih = bmp.PixelHeight * scale;

                    var fp  = new FixedPage { Width = aw, Height = ah };
                    var img = new Image { Source = bmp, Width = iw, Height = ih };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    FixedPage.SetLeft(img, (aw - iw) / 2);
                    FixedPage.SetTop(img, (ah - ih) / 2);
                    fp.Children.Add(img);
                    fp.Measure(new Size(aw, ah));
                    fp.Arrange(new Rect(new Point(), new Size(aw, ah)));

                    var pc = new PageContent();
                    ((IAddChild)pc).AddChild(fp);
                    fixedDoc.Pages.Add(pc);
                }

                if (fixedDoc.Pages.Count == 0)
                {
                    TdpDialog.Show(this, "No pages could be rendered for printing.", "TDPdf",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                pd.PrintDocument(fixedDoc.DocumentPaginator, "TDPdf");
                PrintedPageCount = fixedDoc.Pages.Count;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Print failed:\n{ex.GetType().Name}: {ex.Message}",
                    "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Parses "1-3,5" style ranges into sorted 0-based indices. Blank/invalid = all pages.
        private static List<int> ParseRange(string? text, int count)
        {
            text = text?.Trim() ?? "";
            if (text.Length == 0) return [.. Enumerable.Range(0, count)];

            var set = new SortedSet<int>();
            foreach (var raw in text.Split(','))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                if (part.Contains('-'))
                {
                    var seg = part.Split('-');
                    if (seg.Length == 2 &&
                        int.TryParse(seg[0].Trim(), out int a) &&
                        int.TryParse(seg[1].Trim(), out int b))
                    {
                        if (a > b) (a, b) = (b, a);
                        for (int i = a; i <= b; i++)
                            if (i >= 1 && i <= count) set.Add(i - 1);
                    }
                }
                else if (int.TryParse(part, out int v))
                {
                    if (v >= 1 && v <= count) set.Add(v - 1);
                }
            }
            return set.Count == 0 ? [.. Enumerable.Range(0, count)] : [.. set];
        }

        // ---- Button factory (matches our themed dialog buttons, no blue hover chrome) ----

        private static ControlTemplate MakeBtnTemplate()
        {
            var bf = new FrameworkElementFactory(typeof(Border));
            bf.SetBinding(Border.BackgroundProperty,
                new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            bf.SetBinding(Border.BorderBrushProperty,
                new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            bf.SetBinding(Border.BorderThicknessProperty,
                new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            bf.SetBinding(Border.PaddingProperty,
                new Binding("Padding") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            bf.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bf.AppendChild(cp);
            return new ControlTemplate(typeof(Button)) { VisualTree = bf };
        }

        private static Button MakeButton(string label, bool accent)
        {
            var bgNorm = accent ? R("AccentGreenDim") : R("BgPanel");
            var bgHov  = accent ? R("BgPressed") : R("BgHover");
            var btn = new Button
            {
                Content         = label,
                Padding         = new Thickness(18, 6, 18, 6),
                Background      = bgNorm,
                Foreground      = accent ? R("AccentGreen") : R("TextPrimary"),
                BorderBrush     = accent ? R("AccentGreen") : R("BorderDim"),
                BorderThickness = new Thickness(1),
                FontSize        = 12,
                Cursor          = Cursors.Hand,
                Template        = MakeBtnTemplate()
            };
            btn.MouseEnter += (_, _) => btn.Background = bgHov;
            btn.MouseLeave += (_, _) => btn.Background = bgNorm;
            return btn;
        }
    }
}
