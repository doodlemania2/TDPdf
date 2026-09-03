using System;
using System.Collections.Generic;
using System.Linq;
using System.Printing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
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
    /// ourselves (via Docnet/PDFium), expose printer / orientation / color / two-sided /
    /// scale / position / margins / pages-per-sheet / copies / page-range settings, and
    /// drive the spooler via a non-UI PrintDialog when the user clicks Print.
    ///
    /// The on-screen preview rasterizes at <see cref="RenderDpi"/> (kept light so large
    /// files stay responsive); the spooled output is re-rasterized fresh at
    /// <see cref="PrintDpi"/> (300 DPI) at print time, one page at a time and only for the
    /// pages actually printed, so memory stays bounded even on large documents.
    ///
    /// The caller is expected to hand us a flattened/printable PDF path (annotations
    /// already burned in) plus each page's size in points; we never touch the live
    /// document model.
    /// </summary>
    public sealed class PrintPreviewWindow : Window
    {
        private const double PointPerInch = 72.0;
        private const double DipPerInch   = 96.0;
        private const double RenderDpi    = 200.0;   // on-screen preview resolution
        private const double PrintDpi     = 300.0;   // spooled output resolution (rendered at print time)

        private readonly string _pdfPath;
        private readonly IReadOnlyList<Size> _pageSizes;   // in points
        private readonly int _pageCount;
        private readonly BitmapSource?[] _cache;           // lazily rasterized previews (RenderDpi)

        private readonly List<PrintQueue> _queues = [];
        private PrintQueue? _queue;
        private LocalPrintServer? _server;   // kept alive: queues reference their server
        private bool _landscape;
        private int _previewIndex;           // index of the sheet currently shown in the preview

        // Layout options shared by the preview and the print path (what you see is what prints).
        private bool _grayscale;             // send the job as grayscale/B&W rather than color
        private bool _duplex;                // two-sided printing (when the printer supports it)
        private int _scaleMode;              // 0 = fit to page, 1 = custom percentage
        private double _customPct = 100;     // custom scale % (clamped 25-400)
        private int _alignH = 1;             // horizontal page position: 0 = left, 1 = center, 2 = right
        private int _alignV = 1;             // vertical page position:   0 = top,  1 = center, 2 = bottom
        private double _marginPx;            // extra inset inside the printable area (DIPs)
        private int _nUp = 1;                // pages per sheet (1, 2, 4, 6, 9)
        private int _subset;                 // page subset: 0 = all, 1 = odd page numbers only, 2 = even only

        // Printable area in DIPs for the currently selected printer + orientation.
        private double _areaW = 816;   // Letter portrait fallback (8.5in * 96)
        private double _areaH = 1056;  // (11in * 96)

        private readonly Grid _previewHost = new();
        private readonly TextBlock _pageLabel = new();
        private ComboBox _printerCombo = null!;
        private ComboBox _duplexCombo  = null!;

        // Manual paper pick (upstream KillerPDF #186). Index 0 is "Match document" — the automatic
        // behavior we've always had, where the driver's own default media decides the sheet and the
        // ticket is left untouched. Anything below it is a size the selected driver reported, and it
        // overrides BOTH the preview sheet (via RefreshArea) and the spooled ticket (via DoPrint).
        // The list belongs to the driver, so it is rebuilt whenever the printer changes.
        private ComboBox _paperCombo = null!;
        private readonly List<PageMediaSize> _paperSizes = [];   // parallel to _paperCombo items 1..n
        private PageMediaSize? _paperOverride;                   // null = match document

        // Paper source / input tray (upstream KillerPDF #186 follow-up). Index 0 is "Printer default"
        // and leaves the ticket alone; the rest are the driver's reported input bins. WPF's InputBin
        // enum is deliberately coarse (AutoSelect / Cassette / Tractor / AutoSheetFeeder / Manual) —
        // reaching an actual named tray ("Tray 3", "Envelope feeder") would mean hand-writing raw
        // PrintTicket PrintCapabilities XML per driver, which is driver roulette for very little gain.
        private ComboBox _sourceCombo = null!;
        private readonly List<InputBin> _sourceBins = [];        // parallel to _sourceCombo items 1..n
        private InputBin? _sourceOverride;                       // null = printer default
        private TextBox _copiesBox = null!;
        private TextBox _scaleBox  = null!;
        private TextBox _pagesBox  = null!;
        private Func<int> _copiesGet = () => 1;   // reads the copies stepper (clamped, min 1)
        private Grid _rootGrid = null!;           // wraps the card content so the print scrim can overlay it
        private Button _printBtn = null!;         // disabled while a print job rasterizes + spools
        // Set while a job is rasterizing and spooling. The print scrim blocks the mouse but takes no
        // keyboard focus, so a keystroke in the Pages box (or an arrow key in a combo) still re-runs
        // UpdatePreview mid-job; without this it would re-enable Print and Enter (IsDefault) would
        // spool a second copy behind the scrim. Only the failure path clears it — success closes.
        private bool _printing;

        // Segoe MDL2 Assets close glyph, matching the main window chrome close button.
        private const string CloseGlyph = "";

        // Segoe MDL2 Assets chevrons for the collapsible settings sections, same pair
        // TransformWindow uses for its section headers (E70D / E76C).
        private const string ChevronDown  = "";
        private const string ChevronRight = "";

        /// <summary>Number of pages sent to the printer (set when the user prints).</summary>
        public int PrintedPageCount { get; private set; }

        public PrintPreviewWindow(Window? owner, string pdfPath, IReadOnlyList<Size> pageSizes)
        {
            _pdfPath    = pdfPath;
            _pageSizes  = pageSizes;
            _pageCount  = pageSizes.Count;
            _cache      = new BitmapSource?[_pageCount];

            Title  = "TDPdf - Print";
            Width  = 940;
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

            // Reuse the main window's themed scrollbar for the settings scroller when present.
            if (owner?.TryFindResource(typeof(System.Windows.Controls.Primitives.ScrollBar)) is Style sbStyle)
                Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = sbStyle;

            BuildUi();
            LoadPrinters();
            UpdateDuplexAvailability();
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

        // ---- Numeric field + up/down stepper (reusable, used for Copies and Custom scale %) ----

        // Wires a TextBox as a positive-integer field: digits only, clamped to [min,max], steppable with
        // the Up/Down arrow keys and the mouse wheel. Returns get/set so a spinner can drive the same value.
        private static (Func<int> Get, Action<int> Set) NumericField(TextBox box, int min, int max)
        {
            int Get() => int.TryParse(box.Text?.Trim(), out int n) ? Math.Min(Math.Max(n, min), max) : min;
            void Set(int n)
            {
                n = Math.Min(Math.Max(n, min), max);
                box.Text = n.ToString();
                box.CaretIndex = box.Text.Length;
            }
            box.PreviewTextInput += (_, ev) => ev.Handled = !ev.Text.All(char.IsDigit);
            DataObject.AddPastingHandler(box, (_, ev) =>
            {
                if (ev.DataObject.GetData(typeof(string)) is string s && !s.All(char.IsDigit))
                    ev.CancelCommand();
            });
            box.PreviewKeyDown += (_, ev) =>
            {
                if (ev.Key == Key.Up)   { Set(Get() + 1); ev.Handled = true; }
                if (ev.Key == Key.Down) { Set(Get() - 1); ev.Handled = true; }
            };
            box.PreviewMouseWheel += (_, ev) => { Set(Get() + (ev.Delta > 0 ? 1 : -1)); ev.Handled = true; };
            box.LostFocus += (_, _) => Set(Get());
            return (Get, Set);
        }

        // Two stacked up/down stepper buttons (each half the field height) bound to the given get/set,
        // sized to sit flush against the right edge of a field inside a DockPanel row.
        private static Grid BuildStepper(Func<int> get, Action<int> set)
        {
            var g = new Grid { Width = 20, Margin = new Thickness(-1, 0, 0, 0) };
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            System.Windows.Controls.Primitives.RepeatButton Step(string glyph, int delta, int row)
            {
                var b = new System.Windows.Controls.Primitives.RepeatButton
                {
                    Content         = glyph,
                    Padding         = new Thickness(0),
                    FontSize        = 7,
                    Foreground      = R("TextPrimary"),
                    Background      = R("BgPanel"),
                    BorderBrush     = R("BorderDim"),
                    BorderThickness = new Thickness(1),
                    Cursor          = Cursors.Hand,
                    Focusable       = false,
                    Template        = FlatTemplate(typeof(System.Windows.Controls.Primitives.RepeatButton))
                };
                b.Click += (_, _) => set(get() + delta);
                Grid.SetRow(b, row);
                return b;
            }
            g.Children.Add(Step("▲", +1, 0));
            g.Children.Add(Step("▼", -1, 1));
            return g;
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
            // Host the card content in a single-cell Grid so the print progress scrim can be layered on
            // top of everything (a DockPanel can't overlap its children).
            _rootGrid = new Grid();
            _rootGrid.Children.Add(root);
            outer.Child = _rootGrid;
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
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(268) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(body);

            body.Children.Add(BuildSettingsColumn());
            body.Children.Add(BuildPreviewColumn());
        }

        private UIElement BuildSettingsColumn()
        {
            // Options live in a scroller (buttons are pinned below), so the growing list of
            // controls never pushes Print/Cancel off the bottom on a short window.
            var panel = new StackPanel { Margin = new Thickness(16, 12, 12, 6) };

            // The controls below are added flat and then retro-wrapped into three collapsible
            // sections by WrapSection (the TransformWindow pattern), so each section's body still
            // reads top-to-bottom in source order. PRINTER and OUTPUT open; LAYOUT starts folded
            // because its geometry options are set-and-forget for most jobs, and folding it keeps
            // the 268px column short enough that Copies/Pages are visible without scrolling.

            // --- PRINTER: which device, what paper, which tray ------------------
            int secPrinter = panel.Children.Count;

            panel.Children.Add(Label("Printer"));
            var printerCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 0), Height = 26 };
            ApplyComboStyle(printerCombo);
            printerCombo.SelectionChanged += (s, _) =>
            {
                int i = ((ComboBox)s).SelectedIndex;
                if (i >= 0 && i < _queues.Count)
                {
                    _queue = _queues[i];
                    // A different driver reports a different paper list and different input bins, so
                    // both combos are rebuilt (and reset to their automatic entry) before the preview
                    // recomputes. Both fields are assigned a few lines below, and the first selection
                    // change can only happen later, from LoadPrinters().
                    PopulatePaperSizes();
                    PopulatePaperSources();
                    RefreshArea();
                    UpdateDuplexAvailability();
                    UpdatePreview();
                }
            };
            _printerCombo = printerCombo;

            // Opens the selected printer's own driver UI (paper/quality/color/tray settings) —
            // the same "Properties"/"Preferences" dialog every native Windows print dialog exposes.
            // We don't model that UI ourselves; it's entirely the driver's, reached via DocumentProperties.
            var printerPropsBtn = MakeButton("Properties…", false);
            printerPropsBtn.Padding = new Thickness(8, 4, 8, 4);
            printerPropsBtn.FontSize = 11;
            printerPropsBtn.Margin = new Thickness(6, 0, 0, 0);
            printerPropsBtn.Click += (_, _) => ShowPrinterProperties();

            var printerRow = new DockPanel { Margin = new Thickness(0, 4, 0, 12), LastChildFill = true };
            DockPanel.SetDock(printerPropsBtn, Dock.Right);
            printerRow.Children.Add(printerPropsBtn);
            printerRow.Children.Add(printerCombo);
            panel.Children.Add(printerRow);

            // Paper size. "Match document" keeps today's automatic behavior (driver default media);
            // any other choice drives both the preview sheet and the spooled ticket.
            panel.Children.Add(Label("Paper size"));
            var paper = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(paper);
            _paperCombo = paper;
            PopulatePaperSizes();
            paper.SelectionChanged += (s, _) =>
            {
                int i = ((ComboBox)s).SelectedIndex;
                _paperOverride = i > 0 && i - 1 < _paperSizes.Count ? _paperSizes[i - 1] : null;
                // Same path a printer change takes, so the preview sheet always shows the paper the
                // job will actually land on.
                RefreshArea();
                UpdatePreview();
            };
            panel.Children.Add(paper);

            // Paper source / input tray. Nothing here affects the preview — the sheet is the same
            // size whichever bin it is pulled from — so this only rides along on the print ticket.
            panel.Children.Add(Label("Paper source"));
            var source = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(source);
            _sourceCombo = source;
            PopulatePaperSources();
            source.SelectionChanged += (s, _) =>
            {
                int i = ((ComboBox)s).SelectedIndex;
                _sourceOverride = i > 0 && i - 1 < _sourceBins.Count ? _sourceBins[i - 1] : null;
            };
            panel.Children.Add(source);

            WrapSection(panel, secPrinter, "PRINTER", expanded: true);

            // --- LAYOUT: how the pages sit on the sheet -------------------------
            int secLayout = panel.Children.Count;

            panel.Children.Add(Label("Orientation"));
            var orient = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(orient);
            orient.Items.Add("Portrait");
            orient.Items.Add("Landscape");
            _landscape = TDPdf.Properties.Settings.Default.PrintOrientation == "Landscape";
            orient.SelectedIndex = _landscape ? 1 : 0;
            orient.SelectionChanged += (s, _) =>
            {
                _landscape = ((ComboBox)s).SelectedIndex == 1;
                RefreshArea();
                UpdatePreview();
            };
            panel.Children.Add(orient);

            panel.Children.Add(Label("Scale"));
            var scale = new ComboBox { Margin = new Thickness(0, 4, 0, 6), Height = 26 };
            ApplyComboStyle(scale);
            scale.Items.Add("Fit to page");
            scale.Items.Add("Custom %");
            scale.SelectedIndex = 0;
            panel.Children.Add(scale);

            // Custom percentage: a compact box with a "%" suffix and a stepper, revealed only when
            // "Custom" is chosen so it doesn't take space in the default (fit) layout.
            _scaleBox = MakeTextBox("100");
            _scaleBox.Margin = new Thickness(0);
            _scaleBox.VerticalContentAlignment = VerticalAlignment.Center;
            var (getScale, setScale) = NumericField(_scaleBox, 25, 400);   // same numeric treatment as Copies
            _scaleBox.TextChanged += (s, _) =>
            {
                if (int.TryParse(((TextBox)s).Text?.Trim(), out int p) && p > 0)
                {
                    _customPct = p;
                    if (_scaleMode == 1) UpdatePreview();
                }
            };
            var scaleSpin = BuildStepper(getScale, setScale);
            var scalePct  = new TextBlock
            {
                Text = "%", Foreground = R("TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0)
            };
            var scaleRow = new DockPanel
            {
                Margin        = new Thickness(0, 0, 0, 12),
                LastChildFill = true,
                Visibility    = Visibility.Collapsed
            };
            DockPanel.SetDock(scalePct, Dock.Right);
            DockPanel.SetDock(scaleSpin, Dock.Right);
            scaleRow.Children.Add(scalePct);    // rightmost
            scaleRow.Children.Add(scaleSpin);   // left of %
            scaleRow.Children.Add(_scaleBox);   // fills the rest of the column width
            scale.SelectionChanged += (s, _) =>
            {
                _scaleMode = ((ComboBox)s).SelectedIndex;   // 0 = fit, 1 = custom
                scaleRow.Visibility = _scaleMode == 1 ? Visibility.Visible : Visibility.Collapsed;
                UpdatePreview();
            };
            panel.Children.Add(scaleRow);

            // Position of the page within the printable area (1-up only).
            panel.Children.Add(Label("Position"));
            var position = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(position);
            var positions = new (string name, int h, int v)[]
            {
                ("Center", 1, 1), ("Top", 1, 0), ("Bottom", 1, 2),
                ("Left", 0, 1), ("Right", 2, 1),
                ("Top-left", 0, 0), ("Top-right", 2, 0),
                ("Bottom-left", 0, 2), ("Bottom-right", 2, 2)
            };
            foreach (var (name, _, _) in positions) position.Items.Add(name);
            position.SelectedIndex = 0;
            position.SelectionChanged += (s, _) =>
            {
                int i = ((ComboBox)s).SelectedIndex;
                if (i >= 0 && i < positions.Length)
                {
                    _alignH = positions[i].h;
                    _alignV = positions[i].v;
                    UpdatePreview();
                }
            };
            panel.Children.Add(position);

            // Margins: an extra inset applied inside the printable area.
            panel.Children.Add(Label("Margins"));
            var margins = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(margins);
            var marginOpts = new (string name, double inches)[]
            {
                ("None", 0),
                ("Narrow (0.25\")", 0.25),
                ("Normal (0.5\")", 0.5),
                ("Wide (1\")", 1.0)
            };
            foreach (var (name, _) in marginOpts) margins.Items.Add(name);
            margins.SelectedIndex = 0;
            margins.SelectionChanged += (s, _) =>
            {
                int i = ((ComboBox)s).SelectedIndex;
                if (i >= 0 && i < marginOpts.Length) { _marginPx = marginOpts[i].inches * DipPerInch; UpdatePreview(); }
            };
            panel.Children.Add(margins);

            // Pages per sheet (N-up): we compose the tiled sheet ourselves.
            panel.Children.Add(Label("Pages per sheet"));
            var nup = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(nup);
            foreach (var n in new[] { "1", "2", "4", "6", "9" }) nup.Items.Add(n);
            nup.SelectedIndex = 0;
            nup.SelectionChanged += (s, _) =>
            {
                _nUp = int.TryParse((string)((ComboBox)s).SelectedItem, out int n) && n > 0 ? n : 1;
                _previewIndex = 0;
                UpdatePreview();
            };
            panel.Children.Add(nup);

            WrapSection(panel, secLayout, "LAYOUT", expanded: false);

            // --- OUTPUT: what comes out of the machine, and how much of it ------
            int secOutput = panel.Children.Count;

            // Color vs black & white. Sent on the print ticket so color-restricted print policies
            // see the job correctly instead of treating a B&W job as color.
            panel.Children.Add(Label("Color"));
            var colorMode = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(colorMode);
            colorMode.Items.Add("Color");
            colorMode.Items.Add("Black and white");
            _grayscale = TDPdf.Properties.Settings.Default.PrintColor == "Grayscale";
            colorMode.SelectedIndex = _grayscale ? 1 : 0;
            colorMode.SelectionChanged += (s, _) => _grayscale = ((ComboBox)s).SelectedIndex == 1;
            panel.Children.Add(colorMode);

            // Two-sided: the printer does the flipping; we just set the ticket when it's supported.
            panel.Children.Add(Label("Two-sided"));
            var duplex = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(duplex);
            duplex.Items.Add("One-sided");
            duplex.Items.Add("Two-sided (long edge)");
            _duplex = TDPdf.Properties.Settings.Default.PrintDuplex;
            duplex.SelectedIndex = _duplex ? 1 : 0;
            duplex.SelectionChanged += (s, _) => _duplex = ((ComboBox)s).SelectedIndex == 1;
            _duplexCombo = duplex;
            panel.Children.Add(duplex);

            panel.Children.Add(Label("Copies"));
            _copiesBox = MakeTextBox("1");
            _copiesBox.Margin = new Thickness(0);
            _copiesBox.VerticalContentAlignment = VerticalAlignment.Center;
            var (getCopies, setCopies) = NumericField(_copiesBox, 1, 9999);
            _copiesGet = getCopies;
            var copiesSpin = BuildStepper(getCopies, setCopies);
            var copiesRow  = new DockPanel { Margin = new Thickness(0, 4, 0, 12), LastChildFill = true };
            DockPanel.SetDock(copiesSpin, Dock.Right);
            copiesRow.Children.Add(copiesSpin);   // docked right, full field height
            copiesRow.Children.Add(_copiesBox);   // fills the rest of the column width
            panel.Children.Add(copiesRow);

            panel.Children.Add(Label("Pages"));
            _pagesBox = MakeTextBox("");
            _pagesBox.Margin = new Thickness(0, 4, 0, 2);
            // Typing a range re-filters the preview to just those pages (jump back to the first one), so the
            // preview always shows exactly what the Print button will send (type "6" → preview shows page 6).
            _pagesBox.TextChanged += (_, _) => { _previewIndex = 0; UpdatePreview(); };
            panel.Children.Add(_pagesBox);
            panel.Children.Add(new TextBlock
            {
                Text         = "e.g. 1-3,5  (blank = all)",
                Foreground   = R("TextSecondary"),
                FontSize     = 11,
                Margin       = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap
            });

            // Odd/even subset: manual duplex for printers without a duplexer - print the odd pages,
            // flip the stack, print the even pages. Applies on top of the Pages range above, and the
            // preview follows because everything reads SelectedIndices().
            var subset = new ComboBox { Margin = new Thickness(0, 0, 0, 12), Height = 26 };
            ApplyComboStyle(subset);
            subset.Items.Add("All pages");
            subset.Items.Add("Odd pages only");
            subset.Items.Add("Even pages only");
            subset.SelectedIndex = 0;
            subset.SelectionChanged += (s, _) =>
            {
                _subset = Math.Max(0, ((ComboBox)s).SelectedIndex);
                _previewIndex = 0;
                UpdatePreview();
            };
            panel.Children.Add(subset);

            WrapSection(panel, secOutput, "OUTPUT", expanded: true);

            // Buttons pinned below the scroller so they stay visible on a short window.
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = MakeButton("Cancel", false);
            cancel.Click += (_, _) =>
            {
                if (_printing) return;
                DialogResult = false;
                Close();
            };
            cancel.IsCancel = true;
            _printBtn = MakeButton("Print", true);
            _printBtn.Margin = new Thickness(8, 0, 0, 0);
            _printBtn.Click += (_, _) => DoPrint();
            _printBtn.IsDefault = true;
            btnRow.Children.Add(cancel);
            btnRow.Children.Add(_printBtn);

            var optionsScroller = new ScrollViewer
            {
                Content                       = panel,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(optionsScroller, 0);

            var btnHost = new Border { Child = btnRow, Padding = new Thickness(16, 8, 12, 12) };
            Grid.SetRow(btnHost, 1);

            var column = new Grid();
            column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            column.Children.Add(optionsScroller);
            column.Children.Add(btnHost);
            Grid.SetColumn(column, 0);
            return column;
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
            next.Click += (_, _) => { if (_previewIndex < SheetCount() - 1) { _previewIndex++; UpdatePreview(); } };
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

        // Small dimmed all-caps heading for a collapsible section, matching TransformWindow's.
        private static TextBlock SectionHeader(string text) => new()
        {
            Text       = text,
            Foreground = R("TextSecondary"),
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 6, 0, 4)
        };

        /// <summary>
        /// Retro-wraps the children already appended to <paramref name="host"/> from index
        /// <paramref name="start"/> onward into a collapsible body under a clickable chevron header.
        /// Building each section flat and wrapping afterwards keeps the section bodies readable in
        /// source order instead of nesting every control inside a panel declaration. Mirrors
        /// <c>TransformWindow.WrapSection</c> so the two dialogs collapse identically.
        /// </summary>
        private static void WrapSection(StackPanel host, int start, string title, bool expanded)
        {
            var children = host.Children.Cast<UIElement>().Skip(start).ToList();
            while (host.Children.Count > start) host.Children.RemoveAt(start);

            var body = new StackPanel { Visibility = expanded ? Visibility.Visible : Visibility.Collapsed };
            foreach (var child in children) body.Children.Add(child);

            var chevron = new TextBlock
            {
                Text       = expanded ? ChevronDown : ChevronRight,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize   = 9,
                Width      = 16,
                Foreground = R("TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center
            };
            var label = SectionHeader(title);
            label.Margin = new Thickness(0);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(chevron);
            row.Children.Add(label);

            var header = new Border
            {
                Background = Brushes.Transparent,   // hit-testable across the whole row
                Cursor     = Cursors.Hand,
                Padding    = new Thickness(0, 5, 0, 5),
                Child      = row
            };
            header.MouseLeftButtonUp += (_, _2) =>
            {
                bool open = body.Visibility != Visibility.Visible;
                body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                chevron.Text = open ? ChevronDown : ChevronRight;
            };

            host.Children.Add(header);
            host.Children.Add(body);
        }

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

            // Restore the last-used printer; fall back to the OS default if it's gone.
            string savedPrinter = TDPdf.Properties.Settings.Default.PrintPrinter;
            int sel = !string.IsNullOrEmpty(savedPrinter) ? _queues.FindIndex(q => q.FullName == savedPrinter) : -1;
            if (sel < 0) sel = def != null ? _queues.FindIndex(q => q.FullName == def.FullName) : 0;
            if (_queues.Count > 0)
            {
                _printerCombo.SelectedIndex = sel >= 0 ? sel : 0;
                _queue = _queues[_printerCombo.SelectedIndex];
            }
        }

        // Enables the two-sided dropdown only when the selected printer reports duplex support.
        private void UpdateDuplexAvailability()
        {
            if (_duplexCombo is null) return;
            bool ok = false;
            try
            {
                var caps = _queue?.GetPrintCapabilities();
                ok = caps?.DuplexingCapability?.Contains(Duplexing.TwoSidedLongEdge) == true;
            }
            catch { /* capability query not supported: leave disabled */ }

            _duplexCombo.IsEnabled = ok;
            _duplexCombo.Opacity   = ok ? 1.0 : 0.5;
            _duplexCombo.ToolTip   = ok ? null : "The selected printer doesn't report two-sided support.";
            if (!ok) { _duplexCombo.SelectedIndex = 0; _duplex = false; }
        }

        // ---- Printer driver "Properties" dialog -------------------------------
        // The driver-specific preferences dialog (paper/quality/color/tray, an "Advanced..."
        // button inside it) — not the shell's multi-tab "Printer Properties" from Devices &
        // Printers. This is what every native print dialog's own "Properties"/"Preferences"
        // button opens, via the classic winspool DocumentProperties round-trip: query the
        // devmode buffer size, allocate it, then prompt with DM_IN_PROMPT | DM_OUT_BUFFER.

        [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

        [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int DocumentProperties(
            IntPtr hwnd, IntPtr hPrinter, string pDeviceName,
            IntPtr pDevModeOutput, IntPtr pDevModeInput, int fMode);

        private const int DM_IN_PROMPT  = 4;
        private const int DM_OUT_BUFFER = 2;

        private void ShowPrinterProperties()
        {
            if (_queue == null) return;
            string printerName = _queue.FullName;

            if (!OpenPrinter(printerName, out IntPtr hPrinter, IntPtr.Zero)) return;
            try
            {
                IntPtr owner = new WindowInteropHelper(this).Handle;
                int size = DocumentProperties(owner, hPrinter, printerName, IntPtr.Zero, IntPtr.Zero, 0);
                if (size <= 0) return;

                IntPtr devMode = Marshal.AllocHGlobal(size);
                try
                {
                    DocumentProperties(owner, hPrinter, printerName, devMode, devMode, DM_IN_PROMPT | DM_OUT_BUFFER);
                }
                finally { Marshal.FreeHGlobal(devMode); }
            }
            finally { ClosePrinter(hPrinter); }
        }

        // Fills the paper combo from the current queue's capabilities. Index 0 is always the
        // automatic "Match document" entry; a driver that reports nothing usable leaves only that.
        // Called once while the combo is built and again on every printer change, so the selection
        // resets to automatic rather than pointing at a size the new driver may not stock.
        private void PopulatePaperSizes()
        {
            _paperSizes.Clear();
            _paperCombo.Items.Clear();
            _paperCombo.Items.Add("Match document");
            try
            {
                // Same defensive shape as UpdateDuplexAvailability: the capability query throws on
                // some drivers, and a size with no reported dimensions can't drive a preview sheet.
                if (_queue?.GetPrintCapabilities().PageMediaSizeCapability is { } sizes)
                {
                    foreach (var ms in sizes)
                    {
                        if (ms is null || !ms.Width.HasValue || !ms.Height.HasValue) continue;
                        _paperSizes.Add(ms);
                        _paperCombo.Items.Add(PaperDisplayName(ms));
                    }
                }
            }
            catch { /* driver quirk - automatic entry only */ }
            _paperCombo.SelectedIndex = 0;
            _paperOverride = null;
        }

        // Fills the paper-source combo from the driver's reported input bins. "Unknown" bins are
        // noise (they can't be requested meaningfully) and are dropped.
        private void PopulatePaperSources()
        {
            _sourceBins.Clear();
            _sourceCombo.Items.Clear();
            _sourceCombo.Items.Add("Printer default");
            try
            {
                if (_queue?.GetPrintCapabilities().InputBinCapability is { } bins)
                {
                    foreach (var bin in bins)
                    {
                        if (bin == InputBin.Unknown) continue;
                        _sourceBins.Add(bin);
                        // "AutoSheetFeeder" -> "Auto Sheet Feeder".
                        _sourceCombo.Items.Add(Regex.Replace(bin.ToString(), "(?<=[a-z])(?=[A-Z])", " "));
                    }
                }
            }
            catch { /* driver quirk - default entry only */ }
            _sourceCombo.SelectedIndex = 0;
            _sourceOverride = null;
        }

        // Turns a PageMediaSizeName into something readable: "NorthAmericaLetter" becomes
        // "North America Letter (8.5 x 11 in)" and "ISOA4" becomes "ISO A4 (210 x 297 mm)".
        // North American stocks are shown in inches, everything else in millimeters. Custom sizes
        // report no name at all, so those fall back to the dimensions alone.
        private static string PaperDisplayName(PageMediaSize ms)
        {
            string raw  = ms.PageMediaSizeName?.ToString() ?? "";
            string name = raw;
            if (name.StartsWith("ISO", StringComparison.Ordinal))      name = "ISO " + name[3..];
            else if (name.StartsWith("JIS", StringComparison.Ordinal)) name = "JIS " + name[3..];
            // Split camel case, and also before a capital that follows a digit ("A4Rotated").
            name = Regex.Replace(name, @"(?<=[a-z])(?=[A-Z])|(?<=\d)(?=[A-Z])", " ");

            // PageMediaSize dimensions are in DIPs (1/96 inch), same unit as the printable area.
            double wDip = ms.Width ?? 0, hDip = ms.Height ?? 0;
            string dims = raw.StartsWith("NorthAmerica", StringComparison.Ordinal)
                ? $"{wDip / DipPerInch:0.##} x {hDip / DipPerInch:0.##} in"
                : $"{wDip / DipPerInch * 25.4:0} x {hDip / DipPerInch * 25.4:0} mm";
            return name.Length > 0 ? $"{name} ({dims})" : dims;
        }

        private void RefreshArea()
        {
            double w = 816, h = 1056;   // Letter portrait fallback
            try
            {
                if (_queue != null)
                {
                    var pd = new PrintDialog { PrintQueue = _queue };
                    // A manual paper pick has to reach the preview too, not just the spooled job:
                    // pushing it onto the ticket makes PrintableAreaWidth/Height report that stock's
                    // imageable area, which is what the preview sheet is sized from below. With no
                    // pick the ticket is left alone and the driver's default media decides, exactly
                    // as it did before the paper combo existed.
                    if (_paperOverride != null)
                    {
                        var t = pd.PrintTicket;
                        t.PageMediaSize = _paperOverride;
                        pd.PrintTicket  = t;
                    }
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
                // #141: with the annotations the file carries — what the preview shows has to match
                // what the spooled sheet prints. White background matches the white sheet.
                var raw = PdfiumInterop.RenderPageWithAnnotations(_pdfPath, idx, w, h)
                          ?? pr.GetImage();
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

        // ---- Sheet composition (shared by the preview and the print path) ----

        private readonly record struct PrintLayout(
            int NUp,
            bool Landscape,
            double MarginPx,
            int ScaleMode,
            double CustomPercent,
            int AlignH,
            int AlignV);

        private PrintLayout CapturePrintLayout() =>
            new(_nUp, _landscape, _marginPx, _scaleMode, _customPct, _alignH, _alignV);

        // The page indices the preview walks AND the Print button sends — whatever range is typed in the
        // Pages box (blank = every page; a range that matches no page = empty, which the preview and the
        // print guard both surface), narrowed by the odd/even selector. Driving the preview, the sheet
        // count and the spool path off this one list is what keeps the printed output from ever drifting
        // from what the preview showed.
        //
        // The subset filters on the 1-based page NUMBER the user sees, so 0-based index 0 is page 1 = odd.
        // Unlike ParseRange, an empty result here is meaningful (e.g. a 1-page document with "Even pages
        // only") and is NOT widened back to every page — callers treat it as "nothing to print".
        private List<int> SelectedIndices()
        {
            var list = ParseRange(_pagesBox.Text, _pageCount);
            if (_subset == 0) return list;

            bool wantOdd = _subset == 1;
            var filtered = new List<int>(list.Count);
            foreach (int i in list)
                if ((i % 2 == 0) == wantOdd) filtered.Add(i);   // 0-based even index == odd page number
            return filtered;
        }

        private int SheetCount()
        {
            int sel = SelectedIndices().Count;
            return sel == 0 ? 0 : (sel + _nUp - 1) / _nUp;
        }

        // Builds one sheet (aw x ah DIPs, white) holding the given source pages. 1-up honours the
        // scale mode + position + margin; N-up fits each page into its grid cell. Returns null when
        // none of the requested pages could be fetched. Shared by the preview and the print path so
        // what you see is what prints. The `fetch` delegate supplies the page bitmap (preview cache
        // for the preview; a fresh 300 DPI render for print).
        private Grid? ComposeSheet(
            List<int> idxs,
            double aw,
            double ah,
            Func<int, BitmapSource?> fetch,
            PrintLayout? frozenLayout = null)
        {
            PrintLayout layout = frozenLayout ?? CapturePrintLayout();
            var sheet = new Grid
            {
                Width = aw, Height = ah, Background = Brushes.White, ClipToBounds = true,
                UseLayoutRounding = true, SnapsToDevicePixels = true
            };
            var canvas = new Canvas();
            double m = layout.MarginPx;
            bool any = false;

            if (layout.NUp <= 1)
            {
                if (idxs.Count > 0 && fetch(idxs[0]) is BitmapSource bmp)
                {
                    int idx = idxs[0];
                    double availW = aw - 2 * m, availH = ah - 2 * m;
                    double s = layout.ScaleMode == 1
                        ? (_pageSizes[idx].Width / PointPerInch * DipPerInch / Math.Max(1, bmp.PixelWidth))
                            * (Math.Clamp(layout.CustomPercent, 25, 400) / 100.0)
                        : Math.Min(availW / bmp.PixelWidth, availH / bmp.PixelHeight);
                    double iw = bmp.PixelWidth * s, ih = bmp.PixelHeight * s;
                    // In fit mode, snap to the printable area when within a pixel of filling it so the
                    // white sheet doesn't peek through as a 1px hairline at the page edge.
                    if (layout.ScaleMode == 0)
                    {
                        if (iw >= availW - 1.5) iw = availW + 1;
                        if (ih >= availH - 1.5) ih = availH + 1;
                    }
                    var img = new Image { Source = bmp, Width = iw, Height = ih };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    double offsetH = layout.AlignH == 0 ? 0 : layout.AlignH == 2 ? availW - iw : (availW - iw) / 2;
                    double offsetV = layout.AlignV == 0 ? 0 : layout.AlignV == 2 ? availH - ih : (availH - ih) / 2;
                    Canvas.SetLeft(img, m + offsetH);
                    Canvas.SetTop(img, m + offsetV);
                    canvas.Children.Add(img);
                    any = true;
                }
            }
            else
            {
                var (cols, rows) = layout.NUp switch
                {
                    2 => layout.Landscape ? (2, 1) : (1, 2),
                    4 => (2, 2),
                    6 => layout.Landscape ? (3, 2) : (2, 3),
                    9 => (3, 3),
                    _ => (1, 1)
                };
                const double gap = 6;
                double cellW = (aw - 2 * m) / cols, cellH = (ah - 2 * m) / rows;
                for (int i = 0; i < idxs.Count && i < cols * rows; i++)
                {
                    int idx = idxs[i];
                    if (fetch(idx) is not BitmapSource bmp) continue;
                    int row = i / cols, col = i % cols;
                    double availW = Math.Max(1, cellW - gap), availH = Math.Max(1, cellH - gap);
                    double s  = Math.Min(availW / bmp.PixelWidth, availH / bmp.PixelHeight);
                    double iw = bmp.PixelWidth * s, ih = bmp.PixelHeight * s;
                    var img = new Image { Source = bmp, Width = iw, Height = ih };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    Canvas.SetLeft(img, m + col * cellW + (cellW - iw) / 2);
                    Canvas.SetTop(img, m + row * cellH + (cellH - ih) / 2);
                    canvas.Children.Add(img);
                    any = true;
                }
            }

            if (!any) return null;
            sheet.Children.Add(canvas);
            return sheet;
        }

        private void UpdatePreview()
        {
            _previewHost.Children.Clear();
            if (_pageCount == 0) { _pageLabel.Text = "No pages"; UpdatePrintEnabled(false); return; }

            var selected = SelectedIndices();
            if (selected.Count == 0)
            {
                // The typed range and the odd/even selector can intersect to nothing (a one-page document
                // with "Even pages only", say). Show an empty preview that says so rather than a stale
                // sheet, and disable Print so we never spool a blank job.
                _previewIndex   = 0;
                _pageLabel.Text = "No pages selected";
                UpdatePrintEnabled(false);
                return;
            }
            UpdatePrintEnabled(true);

            int sheets = Math.Max(1, (selected.Count + _nUp - 1) / _nUp);
            int sheet  = Math.Max(0, Math.Min(_previewIndex, sheets - 1));
            _previewIndex = sheet;

            // Source pages on this sheet, taken from the SELECTED set (one for 1-up, up to _nUp for N-up).
            var idxs = new List<int>();
            for (int i = sheet * _nUp; i < Math.Min(selected.Count, sheet * _nUp + _nUp); i++)
                idxs.Add(selected[i]);

            var paper = ComposeSheet(idxs, _areaW, _areaH, GetPageBitmap);
            if (paper != null)
            {
                var vb = new Viewbox { Child = paper, Stretch = Stretch.Uniform, Margin = new Thickness(20) };
                _previewHost.Children.Add(vb);
            }

            // 1-up shows the real page number (so a filtered preview reads "Page 6 of 108"); N-up shows the
            // sheet position within the selected set.
            _pageLabel.Text = _nUp > 1
                ? $"Sheet {sheet + 1} of {sheets}"
                : $"Page {(idxs.Count > 0 ? idxs[0] + 1 : 1)} of {_pageCount}";
        }

        // Greys out Print when the page range + odd/even selector leave nothing to send, and holds it
        // down while a job is in flight so a keystroke behind the scrim can't revive it. The flat button
        // template has no disabled visual of its own, so opacity carries the state.
        private void UpdatePrintEnabled(bool anySelected)
        {
            bool enable = anySelected && !_printing;
            _printBtn.IsEnabled = enable;
            _printBtn.Opacity   = enable ? 1.0 : 0.5;
            _printBtn.ToolTip   = anySelected
                ? null
                : "No pages match the current page range and odd/even selection.";
        }

        // Persists the device-level print choices so the dialog reopens with the user's last setup.
        private void SavePrintPrefs()
        {
            try
            {
                var s = TDPdf.Properties.Settings.Default;
                if (_queue != null) s.PrintPrinter = _queue.FullName;
                s.PrintOrientation = _landscape ? "Landscape" : "Portrait";
                s.PrintColor       = _grayscale ? "Grayscale" : "Color";
                s.PrintDuplex      = _duplex;
                s.Save();
            }
            catch { /* settings are best-effort */ }
        }

        private async void DoPrint()
        {
            if (_queue == null)
            {
                TdpDialog.Show(this, "No printer is available.", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Exactly the list the preview walks (typed range narrowed by the odd/even selector) — going
            // through the same choke point is what guarantees the spooled job matches what was shown.
            var indices = SelectedIndices();
            if (indices.Count == 0)
            {
                TdpDialog.Show(this, "No pages match the current page range and odd/even selection.", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int copies = _copiesGet();
            if (copies < 1) copies = 1;
            PrintLayout printLayout = CapturePrintLayout();
            PrintQueue selectedQueue = _queue;
            string queueName = selectedQueue.FullName;
            SavePrintPrefs();

            // The 300 DPI re-rasterize below runs long enough on real documents that the window used to
            // freeze with no feedback - it read as a crash ("click Print and nothing happens"). Cover the
            // card with a progress scrim, push the heavy rasterization onto a background thread, and only
            // return to the PDF once the job is handed to the spooler. The Print button is disabled and the
            // scrim swallows clicks so the job can't be double-triggered mid-print.
            var overlay = ShowPrintOverlay(out TextBlock statusText);
            _printing = true;
            _printBtn.IsEnabled = false;

            try
            {
                // Give the dispatcher one pass to actually paint the scrim BEFORE the work below. Building
                // the PrintDialog and reading PrintableAreaWidth queries the printer driver and can stall
                // for a beat; resuming at Background priority guarantees the scrim's render pass ran first.
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

                var pd = new PrintDialog { PrintQueue = selectedQueue };
                var ticket = pd.PrintTicket;
                // Copies are handled at the driver level via the single ticket count (no manual copy
                // loop, which double-printed on some drivers); color and duplex ride the ticket too.
                ticket.CopyCount       = copies;
                ticket.PageOrientation = _landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
                ticket.OutputColor     = _grayscale ? OutputColor.Grayscale : OutputColor.Color;
                ticket.Duplexing       = _duplex ? Duplexing.TwoSidedLongEdge : Duplexing.OneSided;
                // Paper size and input tray, when the user picked something other than the automatic
                // entry. Both are left off the ticket otherwise so the driver's own defaults stand.
                // The paper size must go on before PrintableArea* is read below, so the sheet we
                // compose matches the stock the job prints on — the same coupling RefreshArea relies
                // on for the preview. (upstream KillerPDF #186)
                if (_paperOverride != null) ticket.PageMediaSize = _paperOverride;
                if (_sourceOverride is { } bin) ticket.InputBin  = bin;
                pd.PrintTicket = ticket;

                double aw = pd.PrintableAreaWidth, ah = pd.PrintableAreaHeight;
                if (_landscape) { if (aw < ah) (aw, ah) = (ah, aw); }
                else            { if (aw > ah) (aw, ah) = (ah, aw); }
                if (aw <= 0 || ah <= 0) { aw = _areaW; ah = _areaH; }

                // Re-rasterize ONLY the selected pages fresh at a true 300 DPI (never the lighter preview
                // cache), off the UI thread. Frozen bitmaps cross threads freely, so the whole loop runs in
                // Task.Run and reports "Preparing page X of N" back to the scrim, keeping the window painting
                // throughout. Only the pages actually printed are rendered, so memory stays bounded.
                var hi = new BitmapSource?[_pageCount];
                int total = indices.Count;
                await System.Threading.Tasks.Task.Run(() =>
                {
                    using var dr = DocLib.Instance.GetDocReader(_pdfPath, new PageDimensions(PrintDpi / PointPerInch));
                    int done = 0;
                    foreach (int idx in indices)
                    {
                        done++;
                        if (idx < 0 || idx >= _pageCount) continue;
                        try
                        {
                            using var pr = dr.GetPageReader(idx);
                            int w = pr.GetPageWidth(), h = pr.GetPageHeight();
                            // #141: with the annotations the file carries — printing used to omit them.
                            var raw = PdfiumInterop.RenderPageWithAnnotations(_pdfPath, idx, w, h)
                                      ?? pr.GetImage();
                            if (w <= 0 || h <= 0 || raw == null || raw.Length == 0) continue;
                            var bmp = new WriteableBitmap(w, h, PrintDpi, PrintDpi, PixelFormats.Bgra32, null);
                            bmp.WritePixels(new Int32Rect(0, 0, w, h), raw, w * 4, 0);
                            bmp.Freeze();
                            hi[idx] = bmp;
                        }
                        catch { /* skip an unrenderable page rather than fail the whole job */ }
                        int shown = done;
                        try { statusText.Dispatcher.Invoke(() => statusText.Text = $"Preparing page {shown} of {total}…"); }
                        catch { /* window closing */ }
                    }
                });

                statusText.Text = "Sending to printer…";

                // Compose the sheets and spool them from a dedicated STA print thread — see
                // SpoolOnPrintThreadAsync for why this cannot stay on the UI thread. The single ticket
                // still carries copies/color/duplex and orientation, so the output is identical to the
                // old UI-thread path (copies stay driver-level via ticket.CopyCount).
                bool? ok = await SpoolOnPrintThreadAsync(
                   indices, aw, ah, hi, ticket, queueName, printLayout);

                if (ok == null)
                {
                    // No sheet composed — every selected page failed to render.
                    RemoveOverlay(overlay);
                    _printing = false;
                    UpdatePreview();
                    TdpDialog.Show(this, "No pages could be rendered for printing.", "TDPdf",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                PrintedPageCount = ok.Value ? indices.Count : 0;
                _printing = false;
                DialogResult = ok.Value;
                Close();
            }
            catch (Exception ex)
            {
                RemoveOverlay(overlay);   // drop the scrim so the error dialog isn't stuck behind it
                _printing = false;
                // Re-derive Print rather than switching it straight back on: the Pages box could have
                // been retyped behind the scrim, and a range that now matches nothing must stay disabled.
                UpdatePreview();
                TdpDialog.Show(this, $"Print failed:\n{ex.GetType().Name}: {ex.Message}",
                    "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Composes the sheet sequence and spools it from a dedicated STA thread that owns its own
        /// Dispatcher. Returns true once the job reaches the spooler, false if it was cancelled, and
        /// null when no sheet could be composed (every selected page failed to render).
        /// </summary>
        /// <remarks>
        /// XpsDocumentWriter.WriteAsync is asynchronous only in that it returns straight away: the
        /// serialization itself runs as dispatcher work items on the CALLING thread, and every selected
        /// page has to be encoded into the XPS package there. Driven from the UI thread that starved
        /// input — the scrim included, since the scrim needs that same thread to paint — for the whole
        /// spool, long enough for Windows to mark the window Not Responding on a big job.
        ///
        /// Frozen BitmapSources cross threads freely and ComposeSheet only reads plain layout fields, so
        /// the FixedPages are built on, and stay on, this thread. Output is unaffected: same sheets, same
        /// ticket, same spooler.
        /// </remarks>
        private Task<bool?> SpoolOnPrintThreadAsync(
            List<int> indices, double aw, double ah,
            BitmapSource?[] hi, PrintTicket ticket, string queueName, PrintLayout layout)
        {
            var done = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new System.Threading.Thread(() =>
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                // WriteAsync completes back on this dispatcher, so it has to be pumping before the work
                // starts — queue the job, then run the loop.
                dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        done.TrySetResult(await ComposeAndSpool(
                            indices, aw, ah, hi, ticket, queueName, layout));
                    }
                    catch (Exception ex) { done.TrySetException(ex); }
                    finally { dispatcher.InvokeShutdown(); }
                }));
                System.Windows.Threading.Dispatcher.Run();
            });
            // STA: both the XPS serializer and the spooler reach apartment-bound COM underneath.
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "TDPdf print spool";
            thread.Start();
            return done.Task;
        }

        /// <summary>
        /// Builds the sheet sequence and hands it to the spooler. Runs entirely on the print thread.
        /// Returns null when no sheet could be composed.
        /// </summary>
        private async Task<bool?> ComposeAndSpool(
            List<int> indices, double aw, double ah,
            BitmapSource?[] hi, PrintTicket ticket, string queueName, PrintLayout layout)
        {
            var fixedDoc = new FixedDocument();
            // Group the selected pages into sheets of the frozen N-up setting and compose each from
            // the pre-rendered bitmaps
            // (margins + position + scale + tiling all handled inside ComposeSheet, shared with the
            // preview). Copies/color/duplex ride the single ticket, so there is no manual copy loop here —
            // the driver applies ticket.CopyCount, matching the previous UI-thread path.
            for (int start = 0; start < indices.Count; start += layout.NUp)
            {
                var chunk = indices.Skip(start).Take(layout.NUp).ToList();
                var sheet = ComposeSheet(
                    chunk,
                    aw,
                    ah,
                    i => i >= 0 && i < _pageCount ? hi[i] : null,
                    layout);
                if (sheet == null) continue;

                var fp = new FixedPage { Width = aw, Height = ah };
                FixedPage.SetLeft(sheet, 0);
                FixedPage.SetTop(sheet, 0);
                fp.Children.Add(sheet);
                fp.Measure(new Size(aw, ah));
                fp.Arrange(new Rect(new Point(), new Size(aw, ah)));

                var pc = new PageContent();
                ((IAddChild)pc).AddChild(fp);
                fixedDoc.Pages.Add(pc);
            }

            if (fixedDoc.Pages.Count == 0) return null;

            // A PrintQueue holds a spooler handle opened by whichever thread created it, so this thread
            // opens its own by name rather than borrowing the one the dialog is holding.
            using var server = new LocalPrintServer();
            using var queue  = ResolveQueue(server, queueName);

            var writer  = PrintQueue.CreateXpsDocumentWriter(queue);
            var spooled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            writer.WritingCompleted += (_, ev) =>
            {
                if (ev.Error is not null)  spooled.TrySetException(ev.Error);
                else if (ev.Cancelled)     spooled.TrySetResult(false);
                else                       spooled.TrySetResult(true);
            };
            // Write the FixedDocument itself, NOT its DocumentPaginator: the paginator path makes the XPS
            // serializer wrap each page's Visual in a fresh FixedPage, but the Visual already IS a
            // FixedPage — "FixedPage cannot contain another FixedPage". The FixedDocument overload
            // serializes the existing FixedPages directly.
            writer.WriteAsync(fixedDoc, ticket);
            return await spooled.Task;
        }

        /// <summary>
        /// Reopens a print queue by name on the calling thread. LoadPrinters enumerates local queues and
        /// connections off the local server, so its FullName resolves here too; fall back to matching
        /// that same enumeration for the names the spooler will not take directly.
        /// </summary>
        private static PrintQueue ResolveQueue(LocalPrintServer server, string fullName)
        {
            try { return server.GetPrintQueue(fullName); }
            catch
            {
                var match = server
                    .GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
                    .FirstOrDefault(q => q.FullName == fullName);
                if (match != null) return match;
                throw;
            }
        }

        // Full-card scrim with a spinner + live status line, shown while a print job rasterizes and spools
        // so the window shows progress instead of freezing silently. Added over _rootGrid and painted last,
        // so it sits on top and its Background swallows clicks - the buttons underneath can't be re-triggered
        // mid-print. Returns the scrim; `status` is its message line, updated as the job progresses.
        private Border ShowPrintOverlay(out TextBlock status)
        {
            var ring = new System.Windows.Shapes.Ellipse
            {
                Width = 40, Height = 40, StrokeThickness = 3,
                Stroke = R("TextSecondary"),
                StrokeDashArray = [24, 200],
                StrokeDashCap = PenLineCap.Round,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var rot = new RotateTransform();
            ring.RenderTransform = rot;
            rot.BeginAnimation(RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9)))
                { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });

            status = new TextBlock
            {
                Text                = "Preparing to print…",
                Foreground          = R("TextPrimary"),
                FontSize            = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 14, 0, 0)
            };

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(ring);
            stack.Children.Add(status);

            // Veil in the card's own panel colour at high opacity, so the scrim reads on either theme.
            var veil = R("BgPanel").Color;
            var overlay = new Border
            {
                Background   = new SolidColorBrush(Color.FromArgb(232, veil.R, veil.G, veil.B)),
                CornerRadius = new CornerRadius(6),
                Child        = stack
            };
            Panel.SetZIndex(overlay, 99);
            _rootGrid.Children.Add(overlay);
            return overlay;
        }

        private void RemoveOverlay(Border overlay) => _rootGrid.Children.Remove(overlay);

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_printing)
            {
                e.Cancel = true;
                return;
            }
            base.OnClosing(e);
        }

        // Parses "1-3,5" style ranges into sorted 0-based indices. Blank = all pages; a range that
        // matches no page returns empty and the callers surface it (never widened back to every page).
        // Internal because it is the app's one page-range syntax: the print dialog and the
        // image export (File ▸ Export Pages as Images…) share it, hint text included.
        internal static List<int> ParseRange(string? text, int count)
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
                        // Clamp the ends rather than testing each i inside the loop. With the test
                        // inside, "1-2147483647" ran i++ past int.MaxValue, wrapped to int.MinValue,
                        // and i <= b was true again — the loop never ended. The Pages box drives the
                        // preview live, so that froze the app on a keystroke. Same output either way.
                        if (a < 1) a = 1;
                        if (b > count) b = count;
                        for (int i = a; i <= b; i++) set.Add(i - 1);
                    }
                }
                else if (int.TryParse(part, out int v))
                {
                    if (v >= 1 && v <= count) set.Add(v - 1);
                }
            }
            // A blank box already returned every page above, so reaching here with nothing resolved
            // means the text matched no page — a number past the end, or a typo. Return the empty set
            // and let the callers surface it (the preview says "No pages selected", the print guard
            // blocks). Falling back to every page here meant a slipped keystroke in the Pages box
            // silently spooled the whole document.
            return [.. set];
        }

        // ---- Button / control factory (matches our themed dialog buttons, no blue hover chrome) ----

        // Flat border-only template usable for Button and RepeatButton, so the OS default gradient
        // chrome (which ignores Background) doesn't show over our dark theme.
        private static ControlTemplate FlatTemplate(Type targetType)
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
            return new ControlTemplate(targetType) { VisualTree = bf };
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
                Template        = FlatTemplate(typeof(Button))
            };
            btn.MouseEnter += (_, _) => btn.Background = bgHov;
            btn.MouseLeave += (_, _) => btn.Background = bgNorm;
            return btn;
        }
    }
}
