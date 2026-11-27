using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace TDPdf.Services
{
    /// <summary>Where the two documents of a comparison come from, and how to take (and later drop) a fresh snapshot of them.</summary>
    internal sealed class CompareSources
    {
        public CompareSources(string leftName, string rightName, Func<(string Left, string Right)> take, Action release)
        {
            LeftName = leftName; RightName = rightName; Take = take; Release = release;
        }
        public string LeftName { get; }
        public string RightName { get; }
        /// <summary>Makes snapshot files of both documents (as they are now) and returns their paths. Called on the UI thread.</summary>
        public Func<(string Left, string Right)> Take { get; }
        /// <summary>Deletes the snapshot files made by the last <see cref="Take"/>.</summary>
        public Action Release { get; }
    }

    /// <summary>
    /// The read-only compare window (#239): the two documents side by side or on top of each other, every
    /// difference highlighted on the pages, and a list to step through them. It works on snapshots, never
    /// on the open documents, so it cannot change, dirty or undo anything.
    /// </summary>
    internal sealed class CompareWindow : Window
    {
        private enum ViewMode { SideBySide, Blend, Difference }

        private sealed record RowImages(BitmapSource? Left, BitmapSource? Right, BitmapSource? Diff);

        private sealed class RowView
        {
            public int Pair;
            public double Top, Height;
            public Grid Host = null!;
            public Image? ImgA, ImgB;
            public Canvas? CvA, CvB;
            public bool Realized;
        }

        private const double DipPerPoint = 96.0 / 72.0;
        private const double RowGap = 14;
        private const double CellHeader = 22;
        private const string CloseGlyph = "";

        private readonly CompareSources _sources;
        private bool _swapped;

        private CompareResult? _result;
        private CancellationTokenSource? _cts;
        private int _gen;                    // bumped whenever the rows are rebuilt, so late renders are dropped
        private bool _working;

        private ViewMode _mode = ViewMode.SideBySide;
        private double _zoom = 0.7;
        private bool _zoomFitted;
        private double _blend = 0.5;

        private readonly List<RowView> _rows = new();
        private readonly LruCache<(int Pair, int Dpi, bool Diff), RowImages> _cache = new(6);
        private readonly HashSet<(int Pair, int Dpi, bool Diff)> _pending = new();
        private readonly Dictionary<int, List<(CompareChange Change, PageBox Box)>> _leftBoxes = new();
        private readonly Dictionary<int, List<(CompareChange Change, PageBox Box)>> _rightBoxes = new();
        private CompareChange? _selected;
        private bool _suppressSelection;

        private ListBox _list = null!;
        private ScrollViewer _scroll = null!;
        private StackPanel _strip = null!;
        private TextBlock _status = null!;
        private ProgressBar _progress = null!;
        private Button _cancelBtn = null!;
        private ComboBox _viewCombo = null!;
        private Slider _blendSlider = null!;
        private CheckBox _showText = null!, _showPictures = null!, _showPages = null!;
        private TextBlock _zoomLabel = null!;
        private TextBlock _leftTitle = null!, _rightTitle = null!;

        internal CompareWindow(Window? owner, CompareSources sources)
        {
            _sources = sources;
            Title = "TDPdf: Compare";
            Width = 1180;
            Height = 780;
            MinWidth = 760;
            MinHeight = 480;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanResize;
            Owner = owner;
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                ResizeBorderThickness = new Thickness(8),
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
            if (owner?.TryFindResource(typeof(System.Windows.Controls.Primitives.ScrollBar)) is Style sb)
                Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = sb;

            BuildUi();
            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += (_, _) => Start();
            Closed += (_, _) =>
            {
                CancelWork();
                _sources.Release();
            };
        }

        private static SolidColorBrush R(string key) => ThemeBrushes.Get(key);

        private static SolidColorBrush WithOpacity(SolidColorBrush b, double opacity)
        {
            var c = b.Clone();
            c.Opacity = opacity;
            c.Freeze();
            return c;
        }

        // ── Layout ────────────────────────────────────────────────────────────────────────

        private Button Btn(string text, string tip, Action click, bool glyph = false)
        {
            var b = new Button
            {
                Content = text,
                ToolTip = tip,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = glyph ? new Thickness(8, 4, 8, 4) : new Thickness(10, 4, 10, 4),
                Foreground = R("TextPrimary"),
                Background = R("BgPanel"),
                BorderBrush = R("BorderDim"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                MinHeight = 26,
                Focusable = false
            };
            if (glyph) b.FontFamily = new FontFamily("Segoe MDL2 Assets");
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            b.Click += (_, _) => click();
            return b;
        }

        private CheckBox Check(string text, bool on, Action changed)
        {
            var c = new CheckBox
            {
                Content = text,
                IsChecked = on,
                Foreground = R("TextPrimary"),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false
            };
            c.Click += (_, _) => changed();
            return c;
        }

        private void BuildUi()
        {
            var outer = new Border
            {
                Background = R("BgDark"),
                BorderBrush = R("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(14),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = 0.5
                }
            };
            var root = new DockPanel();
            outer.Child = root;
            Content = outer;

            // Title bar
            var titleBar = new Border { Background = R("BgPanel"), CornerRadius = new CornerRadius(5, 5, 0, 0) };
            DockPanel.SetDock(titleBar, Dock.Top);
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            var tg = new Grid();
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tg.Children.Add(new TextBlock
            {
                Text = "TDPdf: Compare",
                Foreground = R("AccentGreen"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(16, 10, 0, 10),
                VerticalAlignment = VerticalAlignment.Center
            });
            var close = new Button { Content = CloseGlyph };
            if (Owner?.TryFindResource("ChromeCloseButton") is Style chromeClose) close.Style = chromeClose;
            else
            {
                close.FontFamily = new FontFamily("Segoe MDL2 Assets");
                close.FontSize = 10; close.Width = 40;
                close.Foreground = R("DangerRed");
                close.Background = Brushes.Transparent;
                close.BorderThickness = new Thickness(0);
                close.Cursor = Cursors.Hand;
            }
            close.Click += (_, _) => Close();
            System.Windows.Automation.AutomationProperties.SetName(close, "Close");
            Grid.SetColumn(close, 1);
            tg.Children.Add(close);
            titleBar.Child = tg;
            root.Children.Add(titleBar);

            // Status bar (bottom)
            var statusBar = new Border { Background = R("StatusBarBackground"), Padding = new Thickness(12, 6, 12, 6), CornerRadius = new CornerRadius(0, 0, 5, 5) };
            DockPanel.SetDock(statusBar, Dock.Bottom);
            var sg = new Grid();
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _status = new TextBlock { Foreground = R("TextSecondary"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            _progress = new ProgressBar { Height = 8, Minimum = 0, Maximum = 1, Margin = new Thickness(10, 0, 10, 0), Foreground = R("AccentGreen"), Background = R("BgPanel"), BorderBrush = R("BorderDim"), Visibility = Visibility.Collapsed };
            _cancelBtn = Btn("Cancel", "Stop comparing (Esc)", CancelWork);
            _cancelBtn.Margin = new Thickness(0);
            _cancelBtn.Visibility = Visibility.Collapsed;
            Grid.SetColumn(_progress, 1); Grid.SetColumn(_cancelBtn, 2);
            sg.Children.Add(_status); sg.Children.Add(_progress); sg.Children.Add(_cancelBtn);
            statusBar.Child = sg;
            root.Children.Add(statusBar);

            // Toolbar
            var bar = new WrapPanel { Margin = new Thickness(10, 8, 10, 4) };
            _viewCombo = new ComboBox { Width = 170, Margin = new Thickness(0, 0, 8, 0), MinHeight = 26 };
            if (Owner?.TryFindResource("DarkComboBox") is Style cs) _viewCombo.Style = cs;
            else { _viewCombo.Foreground = R("TextPrimary"); _viewCombo.Background = R("BgPanel"); _viewCombo.BorderBrush = R("BorderDim"); }
            _viewCombo.Items.Add("Side by side");
            _viewCombo.Items.Add("Overlay: blend");
            _viewCombo.Items.Add("Overlay: differences only");
            _viewCombo.SelectedIndex = 0;
            _viewCombo.SelectionChanged += (_, _) =>
            {
                _mode = (ViewMode)Math.Max(0, _viewCombo.SelectedIndex);
                _blendSlider.Visibility = _mode == ViewMode.Blend ? Visibility.Visible : Visibility.Collapsed;
                if (_result is not null) FitWidth();
            };
            System.Windows.Automation.AutomationProperties.SetName(_viewCombo, "View");
            bar.Children.Add(_viewCombo);

            _blendSlider = new Slider { Minimum = 0, Maximum = 1, Value = _blend, Width = 110, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, ToolTip = "Left to right" };
            _blendSlider.ValueChanged += (_, e) =>
            {
                _blend = e.NewValue;
                foreach (var row in _rows) if (row.ImgB is not null) row.ImgB.Opacity = _blend;
            };
            System.Windows.Automation.AutomationProperties.SetName(_blendSlider, "Blend between the two versions");
            bar.Children.Add(_blendSlider);

            bar.Children.Add(Btn("", "Zoom out", () => SetZoom(_zoom / 1.25), glyph: true));
            _zoomLabel = new TextBlock { Foreground = R("TextSecondary"), Width = 44, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            bar.Children.Add(_zoomLabel);
            bar.Children.Add(Btn("", "Zoom in", () => SetZoom(_zoom * 1.25), glyph: true));
            bar.Children.Add(Btn("", "Swap the two documents", () => { _swapped = !_swapped; Recompare(takeFresh: false); }, glyph: true));
            bar.Children.Add(Btn("", "Compare again with the documents as they are now", () => Recompare(takeFresh: true), glyph: true));
            bar.Children.Add(Btn("", "Previous change (Shift+F8)", () => Step(-1), glyph: true));
            bar.Children.Add(Btn("", "Next change (F8)", () => Step(+1), glyph: true));
            _showText = Check("Text", true, RefreshList);
            _showPictures = Check("Pictures", true, RefreshList);
            _showPages = Check("Pages", true, RefreshList);
            bar.Children.Add(_showText); bar.Children.Add(_showPictures); bar.Children.Add(_showPages);
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);

            // Which side is which
            var names = new Grid { Margin = new Thickness(10, 0, 10, 4) };
            names.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            names.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _leftTitle = new TextBlock { Foreground = R("DangerRed"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            _rightTitle = new TextBlock { Foreground = R("AccentGreen"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(_rightTitle, 1);
            names.Children.Add(_leftTitle); names.Children.Add(_rightTitle);
            DockPanel.SetDock(names, Dock.Top);
            root.Children.Add(names);

            // Body: change list | pages
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _list = new ListBox
            {
                Background = R("SidebarBackground"),
                BorderBrush = R("BorderDim"),
                BorderThickness = new Thickness(0, 1, 1, 0),
                Foreground = R("TextPrimary"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            System.Windows.Automation.AutomationProperties.SetName(_list, "Changes");
            _list.SelectionChanged += (_, _) =>
            {
                if (_suppressSelection) return;
                _selected = (_list.SelectedItem as ListBoxItem)?.Tag as CompareChange;
                if (_selected is not null) GoTo(_selected);
                RedrawHighlights();
            };
            body.Children.Add(_list);

            _strip = new StackPanel { Margin = new Thickness(10, 6, 10, 10) };
            _scroll = new ScrollViewer
            {
                Content = _strip,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = R("BgDark"),
                Focusable = false
            };
            _scroll.ScrollChanged += (_, e) =>
            {
                if (e.VerticalChange != 0 || e.ViewportHeightChange != 0) UpdateVisible();
            };
            _scroll.SizeChanged += (_, _) =>
            {
                if (!_zoomFitted && _result is not null && _scroll.ViewportWidth > 50) FitWidth();
            };
            Grid.SetColumn(_scroll, 1);
            body.Children.Add(_scroll);
            root.Children.Add(body);
        }

        // ── Running a comparison ──────────────────────────────────────────────────────────

        private void Start() => Recompare(takeFresh: true);

        private void Recompare(bool takeFresh)
        {
            CancelWork();
            (string Left, string Right) paths;
            if (takeFresh || _paths is null)
            {
                try
                {
                    _sources.Release();
                    _paths = _sources.Take();
                }
                catch (Exception ex)
                {
                    _status.Text = "Could not read the documents: " + ex.Message;
                    return;
                }
            }
            paths = _paths.Value;
            if (_swapped) paths = (paths.Right, paths.Left);
            _leftTitle.Text = _swapped ? _sources.RightName : _sources.LeftName;
            _rightTitle.Text = _swapped ? _sources.LeftName : _sources.RightName;

            _result = null;
            _gen++;
            _cache.Clear();
            _pending.Clear();
            _rows.Clear();
            _strip.Children.Clear();
            _list.Items.Clear();
            _selected = null;
            _zoomFitted = false;
            SetWorking(true, "Comparing…");

            var cts = _cts = new CancellationTokenSource();
            var ct = cts.Token;
            var progress = new Progress<CompareProgress>(p =>
            {
                if (cts.IsCancellationRequested) return;
                _status.Text = p.Stage + "…";
                _progress.Value = Math.Clamp(p.Fraction, 0, 1);
            });
            string l = paths.Left, r = paths.Right;
            Task.Run(() => PdfCompareEngine.Analyze(l, r, progress, ct), ct).ContinueWith(t =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (cts != _cts) return;
                    if (t.IsCanceled || (t.IsFaulted && t.Exception?.GetBaseException() is OperationCanceledException))
                    {
                        SetWorking(false, "Comparison stopped.");
                        return;
                    }
                    if (t.IsFaulted)
                    {
                        SetWorking(false, "These documents could not be compared: " + (t.Exception?.GetBaseException().Message ?? "unknown error"));
                        return;
                    }
                    _result = t.Result;
                    OnAnalysed(cts);
                });
            }, TaskScheduler.Default);
        }

        private (string Left, string Right)? _paths;

        private void OnAnalysed(CancellationTokenSource cts)
        {
            var result = _result!;
            RefreshList();
            FitWidth();                       // builds the rows
            SetWorking(false, Summary());
            StartVisualPass(result, cts);
        }

        private void StartVisualPass(CompareResult result, CancellationTokenSource cts)
        {
            var todo = Enumerable.Range(0, result.Pairs.Count)
                .Where(i => result.Pairs[i].Kind is PairKind.Matched or PairKind.Moved).ToList();
            if (todo.Count == 0) return;
            SetWorking(true, "Checking pictures…");
            var ct = cts.Token;
            int done = 0;
            Task.Run(() =>
            {
                foreach (int i in todo)
                {
                    ct.ThrowIfCancellationRequested();
                    List<CompareChange> found;
                    try { found = PdfCompareEngine.FindVisual(result, i, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { found = new(); }
                    int n = Interlocked.Increment(ref done);
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (cts != _cts || ct.IsCancellationRequested) return;
                        _progress.Value = (double)n / todo.Count;
                        _status.Text = $"Checking pictures… page pair {n} of {todo.Count}";
                        if (found.Count > 0)
                        {
                            result.Changes.AddRange(found);
                            result.SortChanges();
                            RefreshList();
                            RedrawHighlights();
                        }
                    });
                }
            }, ct).ContinueWith(t =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (cts != _cts) return;
                    SetWorking(false, t.IsCanceled || t.IsFaulted ? "Picture check stopped. " + Summary() : Summary());
                });
            }, TaskScheduler.Default);
        }

        private void CancelWork()
        {
            var cts = _cts;
            if (cts is null) return;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        private void SetWorking(bool working, string status)
        {
            _working = working;
            _progress.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
            _cancelBtn.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
            if (!working) _progress.Value = 0;
            _status.Text = status;
        }

        private string Summary()
        {
            var r = _result;
            if (r is null) return "";
            int text = r.Changes.Count(c => c.Kind is ChangeKind.TextAdded or ChangeKind.TextRemoved or ChangeKind.TextChanged);
            int pic = r.Changes.Count(c => c.Kind == ChangeKind.Visual);
            int pages = r.Changes.Count(c => c.Kind is ChangeKind.PageAdded or ChangeKind.PageRemoved or ChangeKind.PageMoved);
            if (text + pic + pages == 0) return "No differences found.";
            string s = $"{text + pic + pages} changes: {text} text, {pic} pictures, {pages} pages.";
            if (r.DiffedPerPage) s += " The documents differ a lot, so each page was compared on its own.";
            return s;
        }

        // ── The change list ───────────────────────────────────────────────────────────────

        private bool Shown(CompareChange c) => c.Kind switch
        {
            ChangeKind.Visual => _showPictures.IsChecked == true,
            ChangeKind.PageAdded or ChangeKind.PageRemoved or ChangeKind.PageMoved => _showPages.IsChecked == true,
            _ => _showText.IsChecked == true,
        };

        private void RefreshList()
        {
            var r = _result;
            _suppressSelection = true;
            try
            {
                _list.Items.Clear();
                _leftBoxes.Clear(); _rightBoxes.Clear();
                if (r is null) return;
                foreach (var c in r.Changes.Where(Shown))
                {
                    int page = r.Pairs[c.PairIndex].Left ?? r.Pairs[c.PairIndex].Right ?? 0;
                    var line = new StackPanel { Orientation = Orientation.Horizontal };
                    line.Children.Add(new TextBlock
                    {
                        Text = c.Kind switch
                        {
                            ChangeKind.TextAdded or ChangeKind.PageAdded => "+",
                            ChangeKind.TextRemoved or ChangeKind.PageRemoved => "−",
                            ChangeKind.Visual => "▣",
                            ChangeKind.PageMoved => "⇅",
                            _ => "≠",
                        },
                        Foreground = KindBrush(c.Kind, left: c.Kind is ChangeKind.TextRemoved or ChangeKind.PageRemoved),
                        FontWeight = FontWeights.Bold,
                        Width = 18
                    });
                    line.Children.Add(new TextBlock { Text = $"p.{page + 1}  ", Foreground = R("TextSecondary"), Width = 40 });
                    line.Children.Add(new TextBlock { Text = c.Summary, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190, Foreground = R("TextPrimary") });
                    var item = new ListBoxItem { Content = line, Tag = c, Padding = new Thickness(6, 3, 6, 3), ToolTip = c.Summary };
                    _list.Items.Add(item);
                    if (ReferenceEquals(c, _selected)) item.IsSelected = true;

                    foreach (var b in c.LeftBoxes) Add(_leftBoxes, b.Page, c, b);
                    foreach (var b in c.RightBoxes) Add(_rightBoxes, b.Page, c, b);
                }
            }
            finally { _suppressSelection = false; }
        }

        private static void Add(Dictionary<int, List<(CompareChange, PageBox)>> d, int page, CompareChange c, PageBox b)
        {
            if (!d.TryGetValue(page, out var list)) d[page] = list = new();
            list.Add((c, b));
        }

        private SolidColorBrush KindBrush(ChangeKind kind, bool left) => kind switch
        {
            ChangeKind.Visual => R("WarningOrange"),
            ChangeKind.TextAdded or ChangeKind.PageAdded => R("AccentGreen"),
            ChangeKind.TextRemoved or ChangeKind.PageRemoved => R("DangerRed"),
            ChangeKind.PageMoved => R("WarningOrange"),
            _ => left ? R("DangerRed") : R("AccentGreen"),
        };

        private void Step(int delta)
        {
            if (_list.Items.Count == 0) return;
            int i = _list.SelectedIndex;
            i = i < 0 ? (delta > 0 ? 0 : _list.Items.Count - 1) : (i + delta + _list.Items.Count) % _list.Items.Count;
            _list.SelectedIndex = i;
            _list.ScrollIntoView(_list.SelectedItem);
        }

        private void GoTo(CompareChange c)
        {
            if (_result is null || c.PairIndex < 0 || c.PairIndex >= _rows.Count) return;
            var row = _rows[c.PairIndex];
            var p = _result.Pairs[c.PairIndex];
            double y = row.Top;
            // The first box of this change that is on this row's pages (a change can run on to the next row).
            foreach (var b in c.LeftBoxes)
                if (b.Page == p.Left) { y += CellHeader + b.Y * DipPerPoint * _zoom; goto scroll; }
            foreach (var b in c.RightBoxes)
                if (b.Page == p.Right) { y += CellHeader + b.Y * DipPerPoint * _zoom; break; }
        scroll:
            _scroll.ScrollToVerticalOffset(Math.Max(0, y - 60));
            UpdateVisible();
        }

        // ── Rows ──────────────────────────────────────────────────────────────────────────

        private void FitWidth()
        {
            var r = _result;
            if (r is null) return;
            double avail = Math.Max(200, (_scroll.ViewportWidth > 50 ? _scroll.ViewportWidth : 760) - 40);
            double maxW = 1;
            foreach (var s in r.Left.PageSizes) maxW = Math.Max(maxW, s.W);
            foreach (var s in r.Right.PageSizes) maxW = Math.Max(maxW, s.W);
            double cells = _mode == ViewMode.SideBySide ? 2 : 1;
            _zoom = Math.Clamp((avail - (cells - 1) * 16) / cells / (maxW * DipPerPoint), 0.2, 3);
            _zoomFitted = true;
            RebuildRows();
        }

        private void SetZoom(double zoom)
        {
            _zoom = Math.Clamp(zoom, 0.2, 4);
            _zoomFitted = true;
            RebuildRows();
        }

        private void RebuildRows()
        {
            _zoomLabel.Text = $"{Math.Round(_zoom * 100)}%";
            var r = _result;
            if (r is null) return;

            // Stay on the same row when the layout changes.
            int anchor = _rows.Count > 0 ? RowAt(_scroll.VerticalOffset) : 0;
            _gen++;
            _pending.Clear();
            _rows.Clear();
            _strip.Children.Clear();

            double zs = DipPerPoint * _zoom;
            double top = 0;
            for (int i = 0; i < r.Pairs.Count; i++)
            {
                var p = r.Pairs[i];
                var ls = p.Left is { } li ? r.Left.PageSizes[li] : (W: 0.0, H: 0.0);
                var rs = p.Right is { } ri ? r.Right.PageSizes[ri] : (W: 0.0, H: 0.0);
                if (p.Left is null) ls = rs;      // a gap is as big as the page it stands for
                if (p.Right is null) rs = ls;
                double h = (_mode == ViewMode.SideBySide ? Math.Max(ls.H, rs.H) : Math.Max(ls.H, rs.H)) * zs + CellHeader + RowGap;

                var row = new RowView { Pair = i, Top = top, Height = h };
                row.Host = BuildRowShell(row, p, ls, rs, zs, h);
                _rows.Add(row);
                _strip.Children.Add(row.Host);
                top += h;
            }
            UpdateLayout();
            if (anchor > 0 && anchor < _rows.Count) _scroll.ScrollToVerticalOffset(_rows[anchor].Top);
            RefreshList();
            UpdateVisible();
        }

        private int RowAt(double offset)
        {
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].Top + _rows[i].Height > offset) return i;
            return Math.Max(0, _rows.Count - 1);
        }

        private Grid BuildRowShell(RowView row, PagePair p, (double W, double H) ls, (double W, double H) rs, double zs, double height)
        {
            var host = new Grid { Height = height };
            if (_mode == ViewMode.SideBySide)
            {
                host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var a = BuildCell(p.Left, ls, zs, Label(p, left: true), out var imgA, out var cvA, out _);
                var b = BuildCell(p.Right, rs, zs, Label(p, left: false), out var imgB, out var cvB, out _);
                Grid.SetColumn(b, 1);
                host.Children.Add(a); host.Children.Add(b);
                row.ImgA = imgA; row.CvA = cvA; row.ImgB = imgB; row.CvB = cvB;
            }
            else
            {
                var size = (W: Math.Max(ls.W, rs.W), H: Math.Max(ls.H, rs.H));
                string label = p.Kind switch
                {
                    PairKind.Added => $"Page {p.Right + 1} added",
                    PairKind.Removed => $"Page {p.Left + 1} removed",
                    PairKind.Moved => $"Page {p.Left + 1} is now page {p.Right + 1}",
                    _ => $"Page {p.Left + 1}" + (p.Right != p.Left ? $" / {p.Right + 1}" : ""),
                };
                bool gap = p.Kind is PairKind.Added or PairKind.Removed;
                var cell = BuildCell(gap ? null : p.Left ?? p.Right, size, zs, label, out var img, out var cv, out var grid);
                if (!gap && _mode == ViewMode.Blend)
                {
                    var second = new Image { Stretch = Stretch.Fill, Opacity = _blend, IsHitTestVisible = false };
                    grid.Children.Insert(1, second);
                    row.ImgB = second;
                }
                row.ImgA = img; row.CvA = cv;
                host.Children.Add(cell);
            }
            return host;
        }

        private static string Label(PagePair p, bool left)
        {
            int? page = left ? p.Left : p.Right;
            return p.Kind switch
            {
                PairKind.Added when page is null => "No page here",
                PairKind.Removed when page is null => "No page here",
                PairKind.Added => $"Page {page + 1} (added)",
                PairKind.Removed => $"Page {page + 1} (removed)",
                PairKind.Moved => $"Page {page + 1} (moved)",
                _ => page is { } n ? $"Page {n + 1}" : "",
            };
        }

        private FrameworkElement BuildCell(
            int? page, (double W, double H) size, double zs, string label,
            out Image img, out Canvas canvas, out Grid grid)
        {
            var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            cell.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = R("TextSecondary"),
                FontSize = 11,
                Height = CellHeader,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            grid = new Grid { Width = Math.Max(1, size.W * zs), Height = Math.Max(1, size.H * zs) };
            img = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
            canvas = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
            if (page is null)
            {
                grid.Background = R("BgPanel");
                var hatch = new Border { BorderBrush = R("BorderDim"), BorderThickness = new Thickness(1), Opacity = 0.9 };
                grid.Children.Add(hatch);
                grid.Children.Add(new TextBlock
                {
                    Text = label,
                    Foreground = R("TextSecondary"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
                cell.Children[0] = new TextBlock { Height = CellHeader };
            }
            else
            {
                grid.Background = Brushes.White;
                grid.Children.Add(img);
                grid.Children.Add(canvas);
            }
            cell.Children.Add(grid);
            return cell;
        }

        // ── Pictures ──────────────────────────────────────────────────────────────────────

        private int DisplayDpi => (int)Math.Clamp(Math.Round(96 * _zoom / 8) * 8, 48, 200);

        private void UpdateVisible()
        {
            if (_rows.Count == 0) return;
            double top = _scroll.VerticalOffset, bottom = top + Math.Max(_scroll.ViewportHeight, 200);
            double margin = _scroll.ViewportHeight;
            foreach (var row in _rows)
            {
                bool near = row.Top + row.Height >= top - margin && row.Top <= bottom + margin;
                if (near) Realize(row);
                else if (row.Realized) Unrealize(row);
            }
        }

        private void Realize(RowView row)
        {
            if (row.Realized) return;
            var r = _result;
            if (r is null) return;
            var pair = r.Pairs[row.Pair];
            if (pair.Kind is PairKind.Added or PairKind.Removed && _mode != ViewMode.SideBySide)
            {
                row.Realized = true;
                return;
            }
            bool needDiff = _mode == ViewMode.Difference && pair.Left is not null && pair.Right is not null;
            var key = (row.Pair, DisplayDpi, needDiff);
            if (_cache.TryGet(key, out var cached)) { Apply(row, cached); return; }
            if (!_pending.Add(key)) return;

            int gen = _gen;
            int dpi = DisplayDpi;
            Task.Run(() => RenderRow(r, row.Pair, dpi, needDiff)).ContinueWith(t =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    _pending.Remove(key);
                    if (gen != _gen || t.IsFaulted || t.IsCanceled) return;
                    _cache.Set(key, t.Result);
                    if (row.Pair < _rows.Count && ReferenceEquals(_rows[row.Pair], row) && !row.Realized
                        && IsNear(row)) Apply(row, t.Result);
                });
            }, TaskScheduler.Default);
        }

        private bool IsNear(RowView row)
        {
            double top = _scroll.VerticalOffset, margin = _scroll.ViewportHeight;
            return row.Top + row.Height >= top - margin && row.Top <= top + 2 * margin + 200;
        }

        private static RowImages RenderRow(CompareResult r, int pairIndex, int dpi, bool needDiff)
        {
            var p = r.Pairs[pairIndex];
            BitmapSource? left = null, right = null, diff = null;
            byte[]? lb = null, rb = null; int lw = 0, lh = 0, rw = 0, rh = 0;
            if (p.Left is { } li)
            {
                var sz = r.Left.PageSizes[li];
                double scale = PdfCompareEngine.RenderScale(sz.W, sz.H, dpi);
                if (PdfCompareEngine.RenderPage(r.Left.Path, li, sz, scale) is { } pg)
                {
                    lb = pg.Bgra; lw = pg.Width; lh = pg.Height;
                    left = PageRaster.ToFrozenBitmap(lb, lw, lh, 96, 96);
                }
            }
            if (p.Right is { } ri)
            {
                var sz = r.Right.PageSizes[ri];
                double scale = PdfCompareEngine.RenderScale(sz.W, sz.H, dpi);
                if (PdfCompareEngine.RenderPage(r.Right.Path, ri, sz, scale) is { } pg)
                {
                    rb = pg.Bgra; rw = pg.Width; rh = pg.Height;
                    right = PageRaster.ToFrozenBitmap(rb, rw, rh, 96, 96);
                }
            }
            if (needDiff && lb is not null && rb is not null)
            {
                // Both pages on a common canvas, scaled to the larger page's size in points.
                int cw = Math.Max(lw, rw), ch = Math.Max(lh, rh);
                var a = PdfCompareRaster.PadToCanvas(lb, lw, lh, cw, ch);
                var b = PdfCompareRaster.PadToCanvas(rb, rw, rh, cw, ch);
                diff = PageRaster.ToFrozenBitmap(PdfCompareRaster.DifferenceImage(a, b, cw, ch), cw, ch, 96, 96);
            }
            return new RowImages(left, right, diff);
        }

        private void Apply(RowView row, RowImages imgs)
        {
            row.Realized = true;
            switch (_mode)
            {
                case ViewMode.SideBySide:
                    if (row.ImgA is not null) row.ImgA.Source = imgs.Left;
                    if (row.ImgB is not null) row.ImgB.Source = imgs.Right;
                    break;
                case ViewMode.Blend:
                    if (row.ImgA is not null) row.ImgA.Source = imgs.Left ?? imgs.Right;
                    if (row.ImgB is not null) row.ImgB.Source = imgs.Right;
                    break;
                default:
                    if (row.ImgA is not null) row.ImgA.Source = imgs.Diff ?? imgs.Left ?? imgs.Right;
                    break;
            }
            DrawHighlights(row);
        }

        private void Unrealize(RowView row)
        {
            row.Realized = false;
            if (row.ImgA is not null) row.ImgA.Source = null;
            if (row.ImgB is not null) row.ImgB.Source = null;
            row.CvA?.Children.Clear();
            row.CvB?.Children.Clear();
        }

        // ── Highlights ────────────────────────────────────────────────────────────────────

        private void RedrawHighlights()
        {
            foreach (var row in _rows) if (row.Realized) DrawHighlights(row);
        }

        private void DrawHighlights(RowView row)
        {
            var r = _result;
            if (r is null) return;
            var pair = r.Pairs[row.Pair];
            row.CvA?.Children.Clear();
            row.CvB?.Children.Clear();
            double zs = DipPerPoint * _zoom;
            if (_mode == ViewMode.SideBySide)
            {
                if (pair.Left is { } l && row.CvA is not null) Draw(row.CvA, _leftBoxes, l, zs, left: true);
                if (pair.Right is { } rr && row.CvB is not null) Draw(row.CvB, _rightBoxes, rr, zs, left: false);
            }
            else if (row.CvA is not null)
            {
                if (pair.Left is { } l) Draw(row.CvA, _leftBoxes, l, zs, left: true);
                if (pair.Right is { } rr) Draw(row.CvA, _rightBoxes, rr, zs, left: false);
            }
        }

        private void Draw(Canvas canvas, Dictionary<int, List<(CompareChange Change, PageBox Box)>> boxes, int page, double zs, bool left)
        {
            if (!boxes.TryGetValue(page, out var list)) return;
            foreach (var (change, box) in list)
            {
                bool selected = ReferenceEquals(change, _selected);
                var brush = KindBrush(change.Kind, left);
                var rect = new Rectangle
                {
                    Width = Math.Max(3, box.W * zs + 4),
                    Height = Math.Max(3, box.H * zs + 2),
                    Fill = WithOpacity(brush, selected ? 0.45 : 0.28),
                    Stroke = selected ? R("TextPrimary") : brush,
                    StrokeThickness = selected ? 2 : 1,
                    RadiusX = 2, RadiusY = 2,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(rect, box.X * zs - 2);
                Canvas.SetTop(rect, box.Y * zs - 1);
                canvas.Children.Add(rect);
            }
        }

        // ── Keys ──────────────────────────────────────────────────────────────────────────

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (_working) CancelWork(); else Close();
            }
            else if (e.Key == Key.F8)
            {
                e.Handled = true;
                Step((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : +1);
            }
            else if (e.Key == Key.OemPlus || e.Key == Key.Add)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; SetZoom(_zoom * 1.25); }
            }
            else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; SetZoom(_zoom / 1.25); }
            }
        }
    }
}
