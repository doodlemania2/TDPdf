using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Docnet.Core;
using Docnet.Core.Models;
using TDPdf.Diagnostics;
using TDPdf.Services;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        public static readonly RoutedUICommand ZoomInRoutedCommand = new("Zoom In", "ZoomIn", typeof(MainWindow));
        public static readonly RoutedUICommand ZoomOutRoutedCommand = new("Zoom Out", "ZoomOut", typeof(MainWindow));
        public static readonly RoutedUICommand ZoomResetRoutedCommand = new("Reset Zoom", "ZoomReset", typeof(MainWindow));
        public static readonly RoutedUICommand NewDocumentCommand = new("New Document", "NewDocument", typeof(MainWindow));
        public static readonly RoutedUICommand CloseFileCommand = new("Close File", "CloseFile", typeof(MainWindow));
        public static readonly RoutedUICommand UndoCommand = new("Undo", "Undo", typeof(MainWindow));
        public static readonly RoutedUICommand RedoCommand = new("Redo", "Redo", typeof(MainWindow));
        public static readonly RoutedUICommand SaveAsCommand = new("Save As", "SaveAs", typeof(MainWindow));
        public static readonly RoutedUICommand AboutCommand = new("About TDPdf", "About", typeof(MainWindow));

        public ZoomViewModel Zoom { get; } = new();

        private readonly PdfDocumentService _pdfDocumentService = new();
        private CancellationTokenSource? _openCancellationTokenSource;
        private CancellationTokenSource? _renderCancellationTokenSource;
        private CancellationTokenSource? _secondaryRenderCts;
        private int _busyDepth;
        private bool _isFileOperationBusy;
        // ── Multi-document tabs ─────────────────────────────────────────────
        // Every open PDF lives in its own DocumentContext. The window always has
        // exactly one "active" context (_ctx); _tabs holds them all. The legacy
        // field names below (_doc, _currentFile, _annotations, _undoStack, …) are
        // kept as thin properties that forward to the active context, so the
        // thousands of existing references throughout this file keep working
        // unchanged while the underlying state becomes per-tab.
        private readonly List<DocumentContext> _tabs = new();
        private DocumentContext _ctx = new();
        private Border _tabStripBorder = null!;
        private StackPanel _tabStrip = null!;

        private PdfDocument? _doc { get => _ctx.Doc; set => _ctx.Doc = value; }
        private string? _currentFile { get => _ctx.CurrentFile; set => _ctx.CurrentFile = value; }
        private Point _dragStartPoint;

        // Editing
        private EditTool _currentTool = EditTool.Select;
        private Dictionary<int, List<PageAnnotation>> _annotations => _ctx.Annotations;
        private Dictionary<int, (int w, int h)> _renderDims => _ctx.RenderDims;
        private Dictionary<(int pageIndex, int dpiX), RenderedPage> _renderCache => _ctx.RenderCache;

        // Interactive form-field state — forwarded from the active DocumentContext so it
        // survives tab switches (see the PDF Form Field Overlays region).
        private Dictionary<int, string>    _formTextValues  => _ctx.FormTextValues;
        private Dictionary<int, bool>      _formCheckValues => _ctx.FormCheckValues;
        private Dictionary<string, string> _formRadioValues => _ctx.FormRadioValues;
        private const string FormOverlayTag = "FormFieldOverlay";
        private double _currentDpiScale = 1.0;

        // Snapshot-based undo/redo.
        // PageSnapshot: deep-cloned annotation list for one page captured BEFORE the mutation.
        // Document: full PDF byte snapshot for crop/insert/delete/reorder; acts as a history barrier
        //   (clears mixed-kind entries to avoid restoring page snapshots onto a re-ordered document).
        private enum UndoKind { PageSnapshot, Document }
        private readonly record struct UndoEntry(
            UndoKind Kind,
            int PageIdx = -1,
            byte[]? DocBytes = null,
            List<PageAnnotation>? PageAnnotations = null);
        private const int MaxUndoEntries = 100;
        private LinkedList<UndoEntry> _undoStack => _ctx.UndoStack;
        private LinkedList<UndoEntry> _redoStack => _ctx.RedoStack;
        private bool _isDrawing;
        private Point _drawStart;
        private UIElement? _activePreview;
        private InkAnnotation? _activeInk;
        private CropAnnotation? _activeCrop;
        private TextBox? _activeTextBox;
        private PageAnnotation? _selectedAnnotation;
        private Border? _selectionBorder;
        private Rectangle? _imageResizeHandle;
        private bool _isResizingImage;
        private ImageEditAnnotation? _resizingImageEdit;
        private Point _imageResizeStart;
        private Rect _imageResizeOriginalBounds;
        private PdfContentEditor _contentEditor => _ctx.ContentEditor;

        // Draw/Highlight settings
        private Color _drawColor = Colors.Red;
        private double _drawWidth = 3;
        private byte _drawOpacity = 255;
        private Color _highlightColor = Color.FromArgb(80, 255, 255, 0);
        private Border? _drawSettingsBar;

        // Text (typewriter) tool settings
        private double _textFontSize = 14;
        private Color _textColor = Colors.Black;
        private bool _textWhiteout;
        private Color _textFillColor = Colors.White;
        private Border? _textSettingsBar;

        // When a settings bar is opened bound to a selected annotation (restyle-in-place),
        // this points at that annotation; null means the bar edits the tool defaults only.
        private PageAnnotation? _styleTarget;

        // Signature / image resize
        private bool _isResizingSig;
        private Point _resizeSigStart;
        private double _resizeSigStartScale;
        private PlacedAnnotation? _resizeSigAnnot;
        private Rectangle? _resizeHandle;

        // Placed annotation drag-to-move
        private bool _isDraggingAnnot;
        private Point _dragAnnotStart;
        private Point _dragAnnotOrigPos;
        private PlacedAnnotation? _dragAnnot;

        // Crop tool
        private Rect _cropCanvasRect;
        private Rectangle? _cropPreviewRect;
        private Border? _cropConfirmBar;
        private readonly Button _toolCropBtn = null!;
        private readonly List<Rectangle> _cropHandles = new();
        private string? _activeCropHandleTag;
        private Point _cropHandleDragStart;
        private Rect _cropRectAtHandleDrag;

        // Pan tool / middle-mouse pan
        private bool _isPanning;
        private MouseButton? _panButton;
        private Point _panStartViewerPoint;
        private double _panStartHOffset;
        private double _panStartVOffset;
        private Cursor? _cursorBeforePan;

        // Shape tool
        private ShapeKind _shapeKind = ShapeKind.Rectangle;
        private Color _shapeStrokeColor = Colors.Red;
        private Color _shapeFillColor = Color.FromArgb(80, 255, 255, 0);
        private bool _shapeHasFill;
        private double _shapeStrokeWidth = 2;
        private Border? _shapeSettingsBar;

        // Zoom fit-mode tracking (for auto-refit on window resize)
        private ZoomFitMode _zoomFitMode = ZoomFitMode.None;
        private bool _applyingFitZoom;
        private bool _fitResizePending;

        // Selection move/resize for non-placed annotations
        private bool _isMovingAnnot;
        private PageAnnotation? _movingAnnot;
        private Point _moveStartCanvas;
        private object? _moveOriginalGeom;
        private bool _isResizingAnnot;
        private PageAnnotation? _resizingAnnot;
        private Point _resizeStartCanvas;
        private object? _resizeOriginalGeom;
        private Rectangle? _annotResizeHandle;

        // PDF link overlays (rendered on top of the annotation canvas)
        private readonly List<Canvas> _linkOverlays = [];

        // Sidebar + multi-page view
        private bool _sidebarCollapsed;
        private readonly Button _sidebarToggleBtn = null!;
        private readonly Border _sidebarBorder = null!;
        private readonly ColumnDefinition _sidebarCol = null!;
        private readonly WrapPanel _pageContentPanel = null!;

        // Text selection
        private bool _isSelecting;
        private Point _selectStart;
        private Rectangle? _selectRect;
        private string? _selectedText;

        // Search
        private Border? _searchBar;
        private TextBox? _searchBox;
        private TextBlock? _searchStatus;
        private readonly List<Rect> _searchHighlights = [];

        // Signatures
        private List<SavedSignature> _savedSignatures = [];
        private SavedSignature? _pendingSignature;
        private Border? _signaturePopup;
        private Border? _cropPopup;
        private CheckBox? _cropApplyAllCheck;
        // Signatures are stored under %LocalAppData%\TDPdf\ so the file stays
        // writable when the EXE is installed machine-wide under %ProgramFiles%
        // (no admin rights on that directory). A legacy file next to the EXE
        // from older builds is migrated on first read.
        private static readonly string SignatureDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TDPdf");
        private static readonly string SignatureFile = System.IO.Path.Combine(SignatureDir, "signatures.json");
        private static readonly string LegacySignatureFile = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "signatures.json");
        private static readonly SolidColorBrush SignatureBorderBrush = FrozenSolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
        private static readonly SolidColorBrush DialogCloseNormalBrush = FrozenSolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        private static readonly SolidColorBrush DialogCloseHoverBrush = FrozenSolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));

        // Manual element refs (XAML codegen doesn't resolve these)
        private Canvas _annotationCanvas = null!;
        private Grid _pageContentGrid = null!;
        private Button _toolSelectBtn = null!;
        private Button _toolTextBtn = null!;
        private Button _toolEditTextBtn = null!;
        private Button _toolEditImageBtn = null!;
        private Button _toolHighlightBtn = null!;
        private Button _toolDrawBtn = null!;
        private Button _toolSignatureBtn = null!;
        private Button _toolImageBtn = null!;
        private Button _toolPanBtn = null!;
        private Button _toolEraseBtn = null!;
        private Button _toolShapeBtn = null!;
        private Button _saveAsBtnRef = null!;
        private Button _closeFileBtnRef = null!;
        private System.Windows.Controls.Primitives.ToggleButton _gridViewToggle = null!;
        private Border _recentFilesBox = null!;
        private StackPanel _recentFilesList = null!;

        // ============================================================
        // View mode (app-wide). Single and Grid behave exactly as the original
        // single-page / grid layouts did; Continuous and TwoPage are additive.
        // ============================================================
        private enum ViewMode { Single, Continuous, TwoPage, Grid }
        private ViewMode _viewMode = ViewMode.Grid;

        // Continuous-view state.
        private StackPanel _continuousPanel = null!;
        private CancellationTokenSource? _continuousRenderCts;
        private readonly List<double> _continuousTops = [];
        private int _continuousScrollTarget = -1; // re-scroll here once true heights are known
        private double _continuousPageW;
        private bool _suppressContinuousScrollSync;

        // Continuous-view zoom/high-DPI re-sharpen state (#85). The base pass renders every page at a
        // fixed fit-width budget, so at deep zoom or on hi-DPI displays the upscaled bitmap goes soft.
        // ResharpenContinuousVisible re-renders ONLY the pages near the viewport at a higher budget and
        // swaps them into their slots; pages that scroll away are restored to their captured base bitmap
        // so hi-res bitmaps never accumulate beyond the visible window. Debounced + cancellable.
        private CancellationTokenSource? _continuousSharpenCts;
        private System.Windows.Threading.DispatcherTimer? _continuousSharpenTimer;
        private readonly HashSet<int> _continuousSharpPages = new();
        private readonly Dictionary<int, BitmapSource> _continuousBaseBitmaps = new();
        private int _continuousSharpW;   // hi-res pixel budget the sharpened slots were rendered at

        // Back-compat shim: the bulk of the grid layout code was written against a
        // boolean. Grid mode is now one of the ViewMode values; keep this read-only
        // alias so those code paths stay byte-for-byte identical for Single/Grid.
        private bool _gridViewEnabled => _viewMode == ViewMode.Grid;
        private ComboBox _zoomBox = null!;
        private StackPanel _portableBadge = null!;
        private TextBox _pageJumpBox = null!;
        private TextBlock _pageTotalLabel = null!;
        private Border _customTitleBar = null!;
        private RowDefinition _titleBarRow = null!;

        // Full-screen (F11) chrome refs — root grid + the two rows that are fixed-height
        // (title, footer) so they can be zeroed when all chrome is hidden.
        private Grid _rootGrid = null!;
        private Border _toolbarBorder = null!;
        private Border _statusBarBorder = null!;

        // Outline / bookmarks sidebar tab (manual refs — XAML codegen doesn't resolve these)
        private ListBox _outlineList = null!;
        private ScrollViewer _outlineScrollViewer = null!;
        private RadioButton _sidebarPagesTab = null!;
        private RadioButton _sidebarOutlinesTab = null!;
        private DockPanel _pageControlsRow = null!;

        private readonly bool _useNativeWindowFrame = TDPdf.Properties.Settings.Default.UseNativeWindowFrame;
        private HwndSource? _hwndSource;

        // Dirty / unsaved-change tracking (per active document)
        private bool _isDirty { get => _ctx.IsDirty; set => _ctx.IsDirty = value; }

        // Whole-document search results (PDF-space rects per page)
        private Dictionary<int, List<(double left, double bottom, double right, double top)>> _allSearchRects => _ctx.AllSearchRects;
        private List<int> _searchResultPages => _ctx.SearchResultPages;
        private int _searchPageCursor { get => _ctx.SearchPageCursor; set => _ctx.SearchPageCursor = value; }

        public MainWindow()
        {
            ApplyInitialWindowChromeSettings();
            InitializeComponent();
            _tabs.Add(_ctx);
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (v != null) VersionLabel.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
            _annotationCanvas = (Canvas)FindName("AnnotationCanvas")!;
            _pageContentGrid = (Grid)FindName("PageContentGrid")!;
            _toolSelectBtn = (Button)FindName("ToolSelectBtn")!;
            _toolTextBtn = (Button)FindName("ToolTextBtn")!;
            _toolEditTextBtn = (Button)FindName("ToolEditTextBtn")!;
            _toolEditImageBtn = (Button)FindName("ToolEditImageBtn")!;
            _toolHighlightBtn = (Button)FindName("ToolHighlightBtn")!;
            _toolDrawBtn = (Button)FindName("ToolDrawBtn")!;
            _toolSignatureBtn = (Button)FindName("ToolSignatureBtn")!;
            _toolImageBtn = (Button)FindName("ToolImageBtn")!;
            _toolCropBtn = (Button)FindName("ToolCropBtn")!;
            _toolPanBtn = (Button)FindName("ToolPanBtn")!;
            _toolEraseBtn = (Button)FindName("ToolEraseBtn")!;
            _toolShapeBtn = (Button)FindName("ToolShapeBtn")!;
            _sidebarToggleBtn = (Button)FindName("SidebarToggleBtn")!;
            _sidebarBorder = (Border)FindName("SidebarBorder")!;
            _sidebarCol = (ColumnDefinition)FindName("SidebarCol")!;
            _pageContentPanel = (WrapPanel)FindName("PageContentPanel")!;
            _saveAsBtnRef = (Button)FindName("SaveAsBtn")!;
            _closeFileBtnRef = (Button)FindName("CloseFileBtn")!;
            _gridViewToggle = (System.Windows.Controls.Primitives.ToggleButton)FindName("GridViewToggle")!;
            _recentFilesBox = (Border)FindName("RecentFilesBox")!;
            _recentFilesList = (StackPanel)FindName("RecentFilesList")!;
            _continuousPanel = (StackPanel)FindName("ContinuousPanel")!;
            // Restore the persisted view mode (defaults to Grid, matching the original layout).
            if (Enum.TryParse<ViewMode>(TDPdf.Properties.Settings.Default.ViewMode, out var savedVm))
                _viewMode = savedVm;
            _gridViewToggle.IsChecked = _viewMode == ViewMode.Grid;
            PagePreviewPanel.ScrollChanged += PagePreviewPanel_ScrollChanged;
            _zoomBox = (ComboBox)FindName("ZoomBox")!;
            _portableBadge = (StackPanel)FindName("PortableBadge")!;
            _pageJumpBox = (TextBox)FindName("PageJumpBox")!;
            _pageTotalLabel = (TextBlock)FindName("PageTotalLabel")!;
            _customTitleBar = (Border)FindName("CustomTitleBar")!;
            _titleBarRow = (RowDefinition)FindName("TitleBarRow")!;
            _outlineList = (ListBox)FindName("OutlineList")!;
            _outlineScrollViewer = (ScrollViewer)FindName("OutlineScrollViewer")!;
            _sidebarPagesTab = (RadioButton)FindName("SidebarPagesTab")!;
            _sidebarOutlinesTab = (RadioButton)FindName("SidebarOutlinesTab")!;
            _pageControlsRow = (DockPanel)FindName("PageControlsRow")!;
            _tabStripBorder = (Border)FindName("TabStripBorder")!;
            _tabStrip = (StackPanel)FindName("TabStrip")!;
            _rootGrid = (Grid)FindName("RootGrid")!;
            _toolbarBorder = (Border)FindName("ToolbarBorder")!;
            _statusBarBorder = (Border)FindName("StatusBarBorder")!;
            RebuildTabStrip();
            ApplyCustomChromeVisibility();
            ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
            Zoom.SetZoomLevel(TDPdf.Properties.Settings.Default.LastZoomLevel);
            Zoom.PropertyChanged += Zoom_PropertyChanged;
            CommandBindings.Add(new CommandBinding(ZoomInRoutedCommand, (_, _) => ChangeZoomByCommand(ZoomChange.In)));
            CommandBindings.Add(new CommandBinding(ZoomOutRoutedCommand, (_, _) => ChangeZoomByCommand(ZoomChange.Out)));
            CommandBindings.Add(new CommandBinding(ZoomResetRoutedCommand, (_, _) => ChangeZoomByCommand(ZoomChange.Reset)));
            LoadSignatures();
            BuildContextMenu();
            SetTool(EditTool.Select);
            ApplyGrainTexture();
            SourceInitialized += MainWindow_SourceInitialized;
            DpiChanged += (_, _) => ApplyZoom();

            // Open a file passed via command-line / file association (e.g. double-clicking a .pdf)
            // Also show the portable badge when running outside the install location.
            Loaded += async (_, _) =>
            {
                RefreshRecentFilesUi();

                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1 && System.IO.File.Exists(args[1]))
                    await OpenInTabAsync(args[1]);

                if (App.IsPortable())
                    _portableBadge.Visibility = Visibility.Visible;
            };
        }

        private static SolidColorBrush FrozenSolidColorBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }

        private bool ShouldIgnoreGlobalShortcut() => _activeTextBox is not null && _activeTextBox.IsFocused;

        private void OpenCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (ShouldIgnoreGlobalShortcut()) return;
            Open_Click(sender, e);
        }

        private void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (ShouldIgnoreGlobalShortcut()) return;
            SaveInPlace_Click(sender, e);
        }

        private void PrintCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (ShouldIgnoreGlobalShortcut()) return;
            Print_Click(sender, e);
        }

        private void FindCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (ShouldIgnoreGlobalShortcut()) return;
            ToggleSearchBar();
        }

        private void NewCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (ShouldIgnoreGlobalShortcut()) return;
            New_Click(sender, e);
        }

        private void CloseFileCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (ShouldIgnoreGlobalShortcut()) return;
            CloseFile();
        }

        private void UndoCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (ShouldIgnoreGlobalShortcut()) return;
            Undo_Click(sender, e);
        }

        private void SaveAsCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (ShouldIgnoreGlobalShortcut()) return;
            SaveAs_Click(sender, e);
        }

        private void AboutCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (ShouldIgnoreGlobalShortcut()) return;
            ShowAboutDialog();
        }

        private void DropZone_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                Open_Click(sender, e);
                e.Handled = true;
            }
        }

        // ============================================================
        // Window message hook composition
        // ============================================================

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            UpdateCurrentDpiScale();
            var hwnd = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);
            ApplyNativeTitleBarTheme(hwnd);
        }

        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_DPICHANGED = 0x02E0;
        private const int WM_MOUSEHWHEEL = 0x020E;
        private const int WM_KEYDOWN = 0x0100;
        private const int VK_ESCAPE = 0x1B;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO && !_useNativeWindowFrame)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            else if (msg == WM_DPICHANGED)
            {
                WmDpiChanged(hwnd, wParam, lParam);
                handled = true;
            }
            else if (msg == WM_MOUSEHWHEEL)
            {
                if (WmMouseHWheel(wParam, lParam))
                    handled = true;
            }
            else if (msg == WM_KEYDOWN && (int)wParam == VK_ESCAPE && _ocrCts is { IsCancellationRequested: false })
            {
                // SetFileOperationBusy disables the WPF content during OCR, so Esc never reaches
                // OnPreviewKeyDown. The native HWND stays Win32-enabled, though, so we catch it here and
                // cancel the in-flight OCR / language download cooperatively.
                _ocrCts.Cancel();
                SetStatus("Cancelling...");
                handled = true;
            }
            return IntPtr.Zero;
        }

        private bool WmMouseHWheel(IntPtr wParam, IntPtr lParam)
        {
            if (PagePreviewPanel is null || PagePreviewPanel.Visibility != Visibility.Visible)
                return false;
            // wParam HIWORD = signed wheel delta (positive == right tilt on most hardware).
            // lParam LOWORD = screen X, HIWORD = screen Y.
            int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            int sx = (short)(lParam.ToInt64() & 0xFFFF);
            int sy = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            try
            {
                var local = PagePreviewPanel.PointFromScreen(new Point(sx, sy));
                if (local.X < 0 || local.Y < 0 ||
                    local.X > PagePreviewPanel.ActualWidth ||
                    local.Y > PagePreviewPanel.ActualHeight)
                    return false; // cursor not over the viewer
            }
            catch { return false; }

            if (PagePreviewPanel.ScrollableWidth <= 0) return false;
            // Same boosted speed as the vertical wheel (WheelScrollFactor); delta is ±120 per notch.
            PagePreviewPanel.ScrollToHorizontalOffset(
                PagePreviewPanel.HorizontalOffset + delta * (48.0 / 120.0) * WheelScrollFactor);
            return true;
        }

        private void WmDpiChanged(IntPtr hwnd, IntPtr wParam, IntPtr lParam)
        {
            var rect = Marshal.PtrToStructure<RECT>(lParam);
            SetWindowPos(hwnd, IntPtr.Zero, rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top,
                SWP_NOZORDER | SWP_NOACTIVATE);

            int dpiX = wParam.ToInt32() & 0xFFFF;
            _currentDpiScale = dpiX > 0 ? dpiX / 96.0 : GetCurrentDpiScaleFromVisual();
            InvalidateRenderCache();
            RerenderCurrentPage();
        }

        private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                GetMonitorInfo(monitor, ref info);
                RECT work = info.rcWork;
                RECT mon = info.rcMonitor;
                mmi.ptMaxPosition.x = Math.Abs(work.left - mon.left);
                mmi.ptMaxPosition.y = Math.Abs(work.top - mon.top);
                mmi.ptMaxSize.x = Math.Abs(work.right - work.left);
                mmi.ptMaxSize.y = Math.Abs(work.bottom - work.top);
                mmi.ptMaxTrackSize.x = mmi.ptMaxSize.x;
                mmi.ptMaxTrackSize.y = mmi.ptMaxSize.y;
                Marshal.StructureToPtr(mmi, lParam, true);
            }
        }

        private void ApplyInitialWindowChromeSettings()
        {
            WindowStyle = _useNativeWindowFrame ? WindowStyle.SingleBorderWindow : WindowStyle.None;
            AllowsTransparency = !_useNativeWindowFrame;
        }

        private void ApplyCustomChromeVisibility()
        {
            _customTitleBar.Visibility = _useNativeWindowFrame ? Visibility.Collapsed : Visibility.Visible;
            _titleBarRow.Height = _useNativeWindowFrame ? new GridLength(0) : new GridLength(36);
            // Native frames provide a built-in resize border; only the frameless mode needs the grip.
            ResizeMode = _useNativeWindowFrame ? ResizeMode.CanResize : ResizeMode.CanResizeWithGrip;
        }

        private void ThemeManager_ThemeChanged(object? sender, EventArgs e)
        {
            if (_useNativeWindowFrame)
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    ApplyNativeTitleBarTheme(hwnd);
            }

            SetTool(_currentTool);
        }

        private void ApplyNativeTitleBarTheme(IntPtr hwnd)
        {
            if (!_useNativeWindowFrame || hwnd == IntPtr.Zero)
                return;

            int useDark = ThemeManager.EffectiveTheme == Theme.Dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDark, sizeof(int));
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        // ============================================================
        // Full-screen mode (F11)
        // ============================================================
        // Distraction-free mode: hide every piece of chrome (title bar, menu, toolbar, tab strip,
        // sidebar, status bar) and grow the window to cover the ENTIRE monitor — taskbar included —
        // so only the document pane fills the screen. F11 toggles; Esc also exits (handled first in
        // OnPreviewKeyDown, before search-bar / overlay Esc). We deliberately enter with Normal +
        // Topmost + explicit monitor bounds rather than WindowState.Maximized: the WM_GETMINMAXINFO
        // hook (MainWindow_SourceInitialized) clamps maximized windows to the work area so they never
        // cover the taskbar — the opposite of what full screen needs. That hook is left untouched;
        // full screen simply bypasses it by not maximizing.
        private bool _fullScreen;
        private GridLength _fsTitleRow, _fsFooterRow, _fsSidebarWidth;
        private double _fsSidebarMin;
        private Visibility _fsTitleVis, _fsMenuVis, _fsToolbarVis, _fsTabStripVis, _fsFooterVis, _fsSidebarVis, _fsSidebarToggleVis;
        private WindowState _fsPrevState;
        private bool _fsPrevTopmost;
        private ResizeMode _fsPrevResize;
        private double _fsPrevLeft, _fsPrevTop, _fsPrevW, _fsPrevH;

        private void ToggleFullScreen()
        {
            bool entering = !_fullScreen;
            _fullScreen = entering;

            if (entering)
            {
                // Snapshot the current chrome visibility / sizing so exit restores the exact prior
                // state (sidebar collapsed-or-expanded, tab strip shown-or-hidden, window placement).
                _fsTitleVis         = _customTitleBar.Visibility;
                _fsMenuVis          = MainMenu.Visibility;
                _fsToolbarVis       = _toolbarBorder.Visibility;
                _fsTabStripVis      = _tabStripBorder.Visibility;
                _fsFooterVis        = _statusBarBorder.Visibility;
                _fsSidebarVis       = _sidebarBorder.Visibility;
                _fsSidebarToggleVis = _sidebarToggleBtn.Visibility;
                _fsTitleRow         = _rootGrid.RowDefinitions[0].Height;
                _fsFooterRow        = _rootGrid.RowDefinitions[5].Height;
                _fsSidebarWidth     = _sidebarCol.Width;
                _fsSidebarMin       = _sidebarCol.MinWidth;

                _customTitleBar.Visibility   = Visibility.Collapsed;
                MainMenu.Visibility          = Visibility.Collapsed;
                _toolbarBorder.Visibility    = Visibility.Collapsed;
                _tabStripBorder.Visibility   = Visibility.Collapsed;
                _statusBarBorder.Visibility  = Visibility.Collapsed;
                _sidebarBorder.Visibility    = Visibility.Collapsed;
                _sidebarToggleBtn.Visibility = Visibility.Collapsed;
                // Rows 0 (title) and 5 (footer) are fixed-height; zero them. Rows 1-3 (menu/toolbar/
                // tab strip) are Auto and collapse to 0 on their own once their content is hidden.
                _rootGrid.RowDefinitions[0].Height = new GridLength(0);
                _rootGrid.RowDefinitions[5].Height = new GridLength(0);
                // MinWidth floors the sidebar column at 24 otherwise, leaving a strip beside the page.
                _sidebarCol.MinWidth = 0;
                _sidebarCol.Width = new GridLength(0);

                // Cover the whole monitor with explicit bounds. Capture placement first so exit can
                // restore it, then go Normal + Topmost + full-monitor rect. Setting the bounds both
                // before and after the (possible) Maximized->Normal switch keeps the window from
                // momentarily restoring to its old normal rect on another screen.
                _fsPrevState   = WindowState;
                _fsPrevTopmost = Topmost;
                _fsPrevResize  = ResizeMode;
                _fsPrevLeft = Left; _fsPrevTop = Top; _fsPrevW = Width; _fsPrevH = Height;

                var b = CurrentMonitorBoundsDip();
                Topmost = true;
                ResizeMode = ResizeMode.NoResize;
                Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height;
                if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height;
            }
            else
            {
                _customTitleBar.Visibility   = _fsTitleVis;
                MainMenu.Visibility          = _fsMenuVis;
                _toolbarBorder.Visibility    = _fsToolbarVis;
                _tabStripBorder.Visibility   = _fsTabStripVis;
                _statusBarBorder.Visibility  = _fsFooterVis;
                _sidebarBorder.Visibility    = _fsSidebarVis;
                _sidebarToggleBtn.Visibility = _fsSidebarToggleVis;
                _rootGrid.RowDefinitions[0].Height = _fsTitleRow;
                _rootGrid.RowDefinitions[5].Height = _fsFooterRow;
                _sidebarCol.MinWidth = _fsSidebarMin;
                _sidebarCol.Width = _fsSidebarWidth;

                // Drop topmost and restore the pre-full-screen placement. Restore the normal bounds
                // first, then re-maximize if the window was maximized before entering.
                Topmost = _fsPrevTopmost;
                ResizeMode = _fsPrevResize;
                WindowState = WindowState.Normal;
                Left = _fsPrevLeft; Top = _fsPrevTop; Width = _fsPrevW; Height = _fsPrevH;
                if (_fsPrevState == WindowState.Maximized) WindowState = WindowState.Maximized;
            }
        }

        // Full bounds (taskbar included) of the monitor the window is currently on, in WPF
        // device-independent units. MonitorFromWindow / GetMonitorInfo / MONITORINFO / RECT are
        // declared above alongside the window-chrome P/Invokes.
        private Rect CurrentMonitorBoundsDip()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            GetMonitorInfo(mon, ref info);
            var r = info.rcMonitor;
            var dpi = VisualTreeHelper.GetDpi(this);
            return new Rect(r.left / dpi.DpiScaleX, r.top / dpi.DpiScaleY,
                            (r.right - r.left) / dpi.DpiScaleX, (r.bottom - r.top) / dpi.DpiScaleY);
        }

        // All state belonging to one open PDF (one tab). The shared UI controls
        // (page sidebar, viewer, annotation canvas) are rebuilt from the active
        // context when the user switches tabs.
        private sealed class DocumentContext
        {
            public PdfDocument? Doc;
            public string? CurrentFile;          // working path (may be a temp copy after edits)
            public string DisplayName = "";      // shown in the tab header and title bar
            public bool IsDirty;

            // True for docs with no real on-disk home yet (merged-on-drop, imported images).
            // The working path is a temp file, so Ctrl+S must route to Save As instead of
            // silently overwriting the temp copy.
            public bool IsUntitled;

            public readonly Dictionary<int, List<PageAnnotation>> Annotations = new();
            public readonly Dictionary<int, (int w, int h)> RenderDims = new();

            // Pending interactive form-field values (AcroForm). Text & dropdowns are keyed
            // by widget object number, checkboxes by widget object number, radios by field
            // name (shared across the widgets in a group). Persisted into the PDF on save.
            public readonly Dictionary<int, string>    FormTextValues  = new();
            public readonly Dictionary<int, bool>      FormCheckValues = new();
            public readonly Dictionary<string, string> FormRadioValues = new();
            public readonly Dictionary<(int pageIndex, int dpiX), RenderedPage> RenderCache = new();
            public readonly LinkedList<UndoEntry> UndoStack = new();
            public readonly LinkedList<UndoEntry> RedoStack = new();
            public readonly PdfContentEditor ContentEditor = new();

            public readonly Dictionary<int, List<(double left, double bottom, double right, double top)>> AllSearchRects = new();
            public readonly List<int> SearchResultPages = new();
            public int SearchPageCursor = -1;

            // Document outline / bookmarks (null = not yet loaded, empty = none).
            public List<OutlineEntry>? Outline;


            // View state restored when this tab is re-activated.
            public IReadOnlyList<BitmapSource?>? Thumbnails;
            public int SelectedPageIndex = -1;

            // The clickable tab-header chip (built lazily by RebuildTabStrip).
            public Border? Chip;
        }

        /// <summary>A single flattened bookmark/outline entry: title, nesting depth, 0-based target page.</summary>
        private sealed record OutlineEntry(string Title, int Depth, int Page);


        private sealed class RenderedPage
        {
            public RenderedPage(BitmapSource bitmap, double displayWidth, double displayHeight, int pixelWidth, int pixelHeight)
            {
                Bitmap = bitmap;
                DisplayWidth = displayWidth;
                DisplayHeight = displayHeight;
                PixelWidth = pixelWidth;
                PixelHeight = pixelHeight;
            }

            public BitmapSource Bitmap { get; }
            public double DisplayWidth { get; }
            public double DisplayHeight { get; }
            public int PixelWidth { get; }
            public int PixelHeight { get; }
        }

        // ============================================================
        // Window chrome
        // ============================================================

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeBtn_Click(sender, e);
                return;
            }
            // Delegate drag to Windows via WM_NCLBUTTONDOWN(HTCAPTION).
            // This gives native restore-from-maximized-and-drag behavior:
            // if the window is maximized, Windows restores it and follows the cursor
            // exactly as a native title bar would.
            e.Handled = true;
            var hwnd = new WindowInteropHelper(this).Handle;
            SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            var res = TdpDialog.Show(this,
                "Install TDPdf to your user profile?\n\n" +
                "• Start Menu shortcut\n" +
                "• Added to \"Open with\" for .pdf files\n" +
                "• Appears in Add/Remove Programs",
                "Install TDPdf", MessageBoxButton.OKCancel);
            if (res != MessageBoxResult.OK) return;

            // Hide the badge immediately so it doesn't flash if relaunch is slow
            _portableBadge.Visibility = Visibility.Collapsed;

            App.InstallAndRelaunch(_currentFile, wantDesktop: true);
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void MaximizeBtn_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        // ============================================================
        // Menu handlers
        // ============================================================

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

        private void HelpGitHub_Click(object sender, RoutedEventArgs e) =>
            OpenExternalUrl("https://github.com/doodlemania2/TDPdf");

        private void HelpReportIssue_Click(object sender, RoutedEventArgs e) =>
            OpenExternalUrl("https://github.com/doodlemania2/TDPdf/issues/new");

        private void HelpChangelog_Click(object sender, RoutedEventArgs e) =>
            OpenExternalUrl("https://github.com/doodlemania2/TDPdf/blob/main/CHANGELOG.md");

        private void OpenExternalUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Could not open the URL:\n{ex.Message}", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowAboutDialog()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var versionLine = v != null ? $"Version {v.Major}.{v.Minor}.{v.Build}" : "Version unknown";
            var message =
                $"TDPdf — A Windows PDF editor by The Doodle Project.\n\n" +
                $"{versionLine}\n\n" +
                "Released under the GNU General Public License v3.0.\n" +
                "Forked from SteveTheKiller/KillerPDF.\n\n" +
                "https://github.com/doodlemania2/TDPdf";
            TdpDialog.Show(this, message, "About TDPdf", MessageBoxButton.OK, MessageBoxImage.Information);
        }


        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            CaptureViewState();
            int dirtyCount = _tabs.Count(t => t.Doc is not null && t.IsDirty);
            if (dirtyCount > 0)
            {
                var msg = dirtyCount == 1
                    ? "You have unsaved changes. Close TDPdf without saving?"
                    : $"You have unsaved changes in {dirtyCount} open files. Close TDPdf without saving?";
                var res = TdpDialog.Show(this, msg,
                    "TDPdf", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
            _hwndSource?.RemoveHook(WndProc);
            base.OnClosing(e);
        }


        // ============================================================
        // Settings
        // ============================================================

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            ShowSettingsDialog();
        }

        private void ShowSettingsDialog()
        {
            var win = new Window
            {
                Title = "Settings",
                Width = 360,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = BrushResource("BgPanel"),
                Foreground = BrushResource("TextPrimary")
            };

            var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            panel.Children.Add(new TextBlock
            {
                Text = "Theme",
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushResource("TextPrimary"),
                Margin = new Thickness(0, 0, 0, 8)
            });

            var current = ParseThemeSetting(TDPdf.Properties.Settings.Default.Theme);
            foreach (var theme in new[] { Theme.Light, Theme.Dark, Theme.System })
            {
                var radio = new RadioButton
                {
                    Content = theme == Theme.System ? "System (Default)" : theme.ToString(),
                    Tag = theme,
                    IsChecked = theme == current,
                    Foreground = BrushResource("TextPrimary"),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                radio.Checked += (_, _) =>
                {
                    var selected = (Theme)radio.Tag;
                    TDPdf.Properties.Settings.Default.Theme = selected.ToString();
                    TDPdf.Properties.Settings.Default.Save();
                    ThemeManager.Apply(selected);
                    SetStatus($"Theme set to {radio.Content}");
                };
                panel.Children.Add(radio);
            }

            panel.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 12) });

            panel.Children.Add(new TextBlock
            {
                Text = "Page view",
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushResource("TextPrimary"),
                Margin = new Thickness(0, 0, 0, 8)
            });
            foreach (var (vm, labelText) in new[]
            {
                (ViewMode.Single,     "Single page"),
                (ViewMode.Continuous, "Continuous scroll"),
                (ViewMode.TwoPage,    "Two pages"),
                (ViewMode.Grid,       "Grid"),
            })
            {
                var vmRadio = new RadioButton
                {
                    Content = labelText,
                    GroupName = "ViewMode",
                    Tag = vm,
                    IsChecked = vm == _viewMode,
                    Foreground = BrushResource("TextPrimary"),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                vmRadio.Checked += (_, _) =>
                {
                    if (vmRadio.Tag is ViewMode m) SetViewMode(m);
                };
                panel.Children.Add(vmRadio);
            }

            panel.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 12) });

            var nativeFrame = new CheckBox
            {
                Content = "Use native window frame (requires restart)",
                IsChecked = TDPdf.Properties.Settings.Default.UseNativeWindowFrame,
                Foreground = BrushResource("TextPrimary"),
                Margin = new Thickness(0, 0, 0, 12)
            };
            nativeFrame.Checked += NativeFrameSettingChanged;
            nativeFrame.Unchecked += NativeFrameSettingChanged;
            panel.Children.Add(nativeFrame);

            var singleInstance = new CheckBox
            {
                Content = "Open PDFs as tabs in a single window",
                IsChecked = TDPdf.Properties.Settings.Default.SingleInstanceTabs,
                Foreground = BrushResource("TextPrimary"),
                Margin = new Thickness(0, 0, 0, 12)
            };
            singleInstance.Checked += SingleInstanceSettingChanged;
            singleInstance.Unchecked += SingleInstanceSettingChanged;
            panel.Children.Add(singleInstance);

            var note = new TextBlock
            {
                Text = "Tab changes take effect after restarting TDPdf. Native frame changes are applied after restarting TDPdf. Themes update immediately.",
                Foreground = BrushResource("TextSecondary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            };
            panel.Children.Add(note);

            var close = new Button
            {
                Content = "Close",
                Style = (Style)FindResource("DarkButton"),
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(18, 6, 18, 6)
            };
            close.Click += (_, _) => win.Close();
            panel.Children.Add(close);

            win.Content = panel;
            win.ShowDialog();
        }

        private void SingleInstanceSettingChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb)
                return;

            bool requested = cb.IsChecked == true;
            if (TDPdf.Properties.Settings.Default.SingleInstanceTabs == requested)
                return;

            TDPdf.Properties.Settings.Default.SingleInstanceTabs = requested;
            TDPdf.Properties.Settings.Default.Save();
            TdpDialog.Show(this,
                "Restart required for the single-window tabs setting to take effect.",
                "TDPdf", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void NativeFrameSettingChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb)
                return;

            bool requested = cb.IsChecked == true;
            if (TDPdf.Properties.Settings.Default.UseNativeWindowFrame == requested)
                return;

            TDPdf.Properties.Settings.Default.UseNativeWindowFrame = requested;
            TDPdf.Properties.Settings.Default.Save();
            TdpDialog.Show(this,
                "Restart required for the native window frame setting to take effect.",
                "TDPdf", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static Theme ParseThemeSetting(string? value)
        {
            return Enum.TryParse(value, ignoreCase: true, out Theme theme) ? theme : Theme.System;
        }

        private SolidColorBrush BrushResource(string key)
        {
            return (SolidColorBrush)FindResource(key);
        }

        // ============================================================
        // Context menu
        // ============================================================

        private void ApplyGrainTexture()
        {
            // Sparse bright-speck film grain — same style as the first pass,
            // tuned so the texture is visible without being chunky.
            const int size = 256;
            var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
            var pixels = new byte[size * size * 4]; // start fully transparent
            var rng = new Random(1337);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (rng.Next(4) != 0) continue;       // ~25% pixel density
                byte v = (byte)rng.Next(160, 255);     // bright specks
                byte a = (byte)rng.Next(30, 80);       // low-ish alpha for subtlety
                pixels[i]     = v;
                pixels[i + 1] = v;
                pixels[i + 2] = v;
                pixels[i + 3] = a;
            }
            bmp.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), pixels, size * 4, 0);
            GrainBrush.ImageSource = bmp;
        }

        private void BuildContextMenu()
        {
            var menu = new ContextMenu();

            menu.Items.Add(MakeMenuItem("_Copy Text", (s, e) => CopySelectedText(), "Ctrl+C", "Copy selected text to the clipboard"));
            menu.Items.Add(MakeMenuItem("OCR Page to Clip_board", (s, e) => OcrPageToClipboard(Math.Max(0, PageList.SelectedIndex)),
                "Ctrl+Shift+O", "Recognize the current page's text with OCR and copy it to the clipboard"));
            menu.Items.Add(MakeMenuItem("_Print", (s, e) => Print_Click(s!, e), "Ctrl+P", "Print the current PDF"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("_Select Tool", (s, e) => SetTool(EditTool.Select), null, "Switch to the select tool"));
            menu.Items.Add(MakeMenuItem("_Text Tool", (s, e) => SetTool(EditTool.Text), null, "Switch to the text tool"));
            menu.Items.Add(MakeMenuItem("Edit Existing Text", (s, e) => SetTool(EditTool.EditText), null, "Switch to the existing text edit tool"));
            menu.Items.Add(MakeMenuItem("Edit Existing Image", (s, e) => SetTool(EditTool.EditImage), null, "Switch to the existing image edit tool"));
            menu.Items.Add(MakeMenuItem("_Highlight Tool", (s, e) => SetTool(EditTool.Highlight), null, "Switch to the highlight tool"));
            menu.Items.Add(MakeMenuItem("_Draw Tool", (s, e) => SetTool(EditTool.Draw), null, "Switch to the draw tool"));
            menu.Items.Add(MakeMenuItem("_Crop Tool", (s, e) => SetTool(EditTool.Crop), null, "Switch to the crop tool"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("De_lete Selected", (s, e) => DeleteSelected(), "Delete", "Delete the selected annotation"));
            menu.Items.Add(MakeMenuItem("_Undo Last", (s, e) => Undo_Click(s!, e), "Ctrl+Z", "Undo the last annotation change"));
            menu.Items.Add(MakeMenuItem("Cle_ar Page Annotations", (s, e) => ClearAnnotations_Click(s!, e), null, "Clear all annotations on this page"));

            _annotationCanvas.ContextMenu = menu;
        }

        private void PageList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_doc is null) return;
            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem("Insert Blank Page After", (s, ev) => InsertBlankPage_Click(s!, ev)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("Rotate CW",  (s, ev) => RotatePages_Click(90)));
            menu.Items.Add(MakeMenuItem("Rotate CCW", (s, ev) => RotatePages_Click(-90)));
            menu.Items.Add(MakeMenuItem("Transform…", (s, ev) => ToolTransform_Click(s!, ev), null,
                "Rotate by a fine angle, scale, flip, or straighten the page (rasterizes it to an image)"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("Move Page Up",   (s, ev) => MoveUp_Click(s!, ev)));
            menu.Items.Add(MakeMenuItem("Move Page Down", (s, ev) => MoveDown_Click(s!, ev)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("Extract Page(s)", (s, ev) => Split_Click(s!, ev)));
            menu.Items.Add(MakeMenuItem("Delete Page(s)", (s, ev) => Delete_Click(s!, ev)));
            menu.PlacementTarget = PageList;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void RotatePages_Click(int delta)
        {
            if (_doc is null) return;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) return;
            try
            {
                var indices = new List<int>();
                foreach (var item in selected) indices.Add(PageList.Items.IndexOf(item));
                foreach (var idx in indices)
                    _doc.Pages[idx].Rotate = ((_doc.Pages[idx].Rotate + delta) % 360 + 360) % 360;
                int restoreIdx = PageList.SelectedIndex;
                SaveTempAndReload();
                PageList.SelectedIndex = Math.Min(restoreIdx, PageList.Items.Count - 1);
                // After a rotation the page aspect ratio changes; always fit-to-page so the
                // full rotated page is visible regardless of the previous zoom level. Re-fit
                // again at Loaded priority once the new page bitmap has laid out.
                FitToPage();
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)FitToPage);
                SetStatus($"Rotated {indices.Count} page(s)");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Rotate failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static MenuItem MakeMenuItem(string header, RoutedEventHandler click, string? gesture = null, string? helpText = null)
        {
            var item = new MenuItem { Header = header };
            item.Click += click;
            if (gesture != null)
                item.InputGestureText = gesture;
            var automationName = header.Replace("_", string.Empty);
            AutomationProperties.SetName(item, automationName);
            AutomationProperties.SetHelpText(item, helpText ?? automationName);
            return item;
        }

        // ============================================================
        // File operations
        // ============================================================

        private async Task OpenFileAsync(string path)
        {
            _openCancellationTokenSource?.Cancel();
            _renderCancellationTokenSource?.Cancel();
            _openCancellationTokenSource?.Dispose();
            _openCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _openCancellationTokenSource.Token;

            SetFileOperationBusy(true, $"Opening {System.IO.Path.GetFileName(path)}...");
            var openOp = Telemetry.StartOperation("OpenFile");
            try
            {
                var result = await OpenFileCoreAsync(path, null, cancellationToken);
                await FinishOpenFileAsync(result, cancellationToken);
                openOp.With("Recovered", result.RecoveredFromRaster ? "true" : "false");
            }
            catch (OperationCanceledException)
            {
                openOp.With("Canceled", "true");
                SetStatus("Open canceled");
            }
            catch (Exception ex) when (IsPasswordException(ex))
            {
                SetFileOperationBusy(false);
                string? pw = PromptForPassword(path);
                if (pw is null)
                {
                    openOp.With("Canceled", "true");
                    SetStatus("Open canceled");
                    return;
                }
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        SetStatus("Open canceled");
                        return;
                    }
                    SetFileOperationBusy(true, $"Opening {System.IO.Path.GetFileName(path)}...");
                    _openCancellationTokenSource?.Dispose();
                    _openCancellationTokenSource = new CancellationTokenSource();
                    var retryCancellationToken = _openCancellationTokenSource.Token;
                    var result = await OpenFileCoreAsync(path, pw, retryCancellationToken);
                    await FinishOpenFileAsync(result, retryCancellationToken);
                    openOp.With("Encrypted", "true");
                }
                catch (OperationCanceledException)
                {
                    SetStatus("Open canceled");
                }
                catch (Exception ex2)
                {
                    openOp.Fail(ex2);
                    Telemetry.TrackEvent("File.OpenFailed", new Dictionary<string, string>
                    {
                        ["ExceptionType"] = ex2.GetType().FullName ?? "Unknown",
                        ["Stage"]         = "AfterPassword",
                    });
                    SetFileOperationBusy(false);
                    TdpDialog.Show(this, $"Failed to open PDF:\n{ex2.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                openOp.Fail(ex);
                Telemetry.TrackEvent("File.OpenFailed", new Dictionary<string, string>
                {
                    ["ExceptionType"] = ex.GetType().FullName ?? "Unknown",
                    ["Stage"]         = "Initial",
                });
                SetFileOperationBusy(false);
                TdpDialog.Show(this, $"Failed to open PDF:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                openOp.Dispose();
                SetFileOperationBusy(false);
            }
        }

        private async Task<PdfOpenResult> OpenFileCoreAsync(string path, string? password, CancellationToken cancellationToken)
        {
            var result = await _pdfDocumentService.OpenAsync(path, password, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        private async Task FinishOpenFileAsync(PdfOpenResult result, CancellationToken cancellationToken)
        {
            bool assignedDocument = false;
            try
            {
                int pageCount = result.Document.PageCount;
                var thumbnails = await _pdfDocumentService.RenderThumbnailsAsync(result.WorkingPath, pageCount, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                if (_doc is not null) { _doc.Close(); _doc = null; }
                _doc = result.Document;
                assignedDocument = true;
                _currentFile = result.WorkingPath;
                SetDisplayName(System.IO.Path.GetFileName(result.DisplayPath));
                _annotations.Clear();
                ClearFormState();
                _undoStack.Clear();
                _redoStack.Clear();
                _renderDims.Clear();
                InvalidateRenderCache();
                _contentEditor.ClearCache();
                _allSearchRects.Clear();
                _searchResultPages.Clear();
                _searchPageCursor = -1;
                ClearSecondaryPages();
                ClearSelection();
                RefreshPageList(thumbnails);
                LoadOutlines();
                _ctx.Thumbnails = thumbnails;
                DropZone.Visibility = Visibility.Collapsed;
                PagePreviewPanel.Visibility = Visibility.Visible;
                if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = true;
                _gridViewToggle.IsEnabled = true;
                _pageJumpBox.IsEnabled = true;
                _pageTotalLabel.Text = $"/ {_doc.PageCount}";
                MarkDirty(false);
                _ctx.IsUntitled = false;   // a real on-disk open; merged/imported callers set this true afterward
                if (_doc.PageCount > 0)
                {
                    PageList.SelectedIndex = 0;
                    // Apply the persisted view mode's layout + open-fit rule once the first page
                    // has rendered and layout has settled. DispatcherPriority.Background fires after
                    // all pending RenderPage / RefreshPageView callbacks have completed.
                    _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                        (Action)ApplyViewModeOnOpen);
                }
                var readOnlySuffix = result.OpenedReadOnly ? " (read-only - owner restrictions)" : string.Empty;
                if (result.RecoveredFromRaster)
                    readOnlySuffix = " (recovered - pages rasterized, text not selectable)";
                SetStatus($"Opened {System.IO.Path.GetFileName(result.DisplayPath)}{readOnlySuffix} - {_doc.PageCount} page(s)");
                UpdateTabChrome();

                // Record real, on-disk user files in the recent list. Skip recovered docs (rasterized
                // rebuilds) and temp working files (New / merged-on-drop / imported images), whose
                // DisplayPath lives under the temp dir and has no lasting saved location.
                if (!result.RecoveredFromRaster && IsRecentEligiblePath(result.DisplayPath))
                    AddRecentFile(result.DisplayPath);
            }
            catch
            {
                if (!assignedDocument) result.Document.Close();
                throw;
            }
        }

        private static bool IsPasswordException(Exception ex) =>
            ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("protected", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("encrypted", StringComparison.OrdinalIgnoreCase) >= 0;

        private string? PromptForPassword(string filename)
        {
            string? result = null;
            var win = new Window
            {
                Title = "Password Required",
                Width = 360,
                Height = 165,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = BrushResource("BgPanel")
            };
            var sp = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            sp.Children.Add(new TextBlock
            {
                Text = $"\"{System.IO.Path.GetFileName(filename)}\" is password protected.",
                Foreground = BrushResource("TextPrimary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
            var pwBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 14) };
            sp.Children.Add(pwBox);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = "Open", Width = 76, Margin = new Thickness(0, 0, 8, 0) };
            var cancelBtn = new Button { Content = "Cancel", Width = 76 };
            okBtn.Click += (s, ev) => { result = pwBox.Password; win.DialogResult = true; };
            cancelBtn.Click += (s, ev) => { win.DialogResult = false; };
            cancelBtn.IsCancel = true;   // Esc cancels the prompt (Enter is handled on pwBox below)
            pwBox.KeyDown += (s, ev) => { if (ev.Key == Key.Enter) { result = pwBox.Password; win.DialogResult = true; } };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);
            sp.Children.Add(btnRow);
            win.Content = sp;
            return win.ShowDialog() == true ? result : null;
        }

        private void RefreshPageList(IReadOnlyList<BitmapSource?>? thumbnails = null)
        {
            PageList.Items.Clear();
            if (_doc is null) return;

            for (int i = 0; i < _doc.PageCount; i++)
            {
                BitmapSource? thumb = thumbnails is not null && i < thumbnails.Count ? thumbnails[i] : null;
                var img = new Image
                {
                    Source = thumb,
                    Width = 140,
                    Height = thumb is not null ? 140.0 * thumb.PixelHeight / thumb.PixelWidth : 100,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 0, 0, 2)
                };

                var label = new TextBlock
                {
                    Text = $"Page {i + 1}",
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                if (thumb is not null)
                {
                    var border = new Border
                    {
                        Background = Brushes.White,
                        BorderBrush = BrushResource("BorderDim"),
                        BorderThickness = new Thickness(1),
                        Child = img
                    };
                    panel.Children.Add(border);
                }
                else
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = $"Page {i + 1}",
                        Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 13,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 20, 0, 20)
                    });
                }
                panel.Children.Add(label);
                PageList.Items.Add(panel);
            }
        }

        private void UpdateCurrentDpiScale()
        {
            _currentDpiScale = GetCurrentDpiScaleFromVisual();
        }

        private double GetCurrentDpiScaleFromVisual()
        {
            var source = PresentationSource.FromVisual(this);
            var transform = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            return transform.M11 > 0 ? transform.M11 : 1.0;
        }

        private int GetCurrentDpiX()
        {
            return Math.Max(1, (int)Math.Round(_currentDpiScale * Zoom.ZoomLevel * 96.0));
        }

        private void InvalidateRenderCache()
        {
            _renderCache.Clear();
            _renderDims.Clear();
        }

        private void RerenderCurrentPage()
        {
            int pageIndex = PageList.SelectedIndex;
            if (pageIndex < 0 || _doc is null) return;

            RenderPage(pageIndex);
            ApplyZoom();
            if (_searchBar is not null && _searchBar.Visibility == Visibility.Visible
                && _allSearchRects.Count > 0)
            {
                HighlightSearchResultsOnCurrentPage();
            }
        }

        private async void RenderPage(int pageIndex)
        {
            if (_currentFile is null || _doc is null) return;
            var currentFile = _currentFile;
            _renderCancellationTokenSource?.Cancel();
            _renderCancellationTokenSource?.Dispose();
            _renderCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _renderCancellationTokenSource.Token;
            try
            {
                int dpiX = GetCurrentDpiX();
                SetBusy(true, $"Rendering page {pageIndex + 1}...");
                if (!_renderCache.TryGetValue((pageIndex, dpiX), out var renderedPage))
                {
                    var result = await _pdfDocumentService.RenderPageAsync(currentFile, pageIndex, dpiX, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (result.Bitmap is null || result.Width <= 0 || result.Height <= 0)
                    {
                        PageImage.Source = null;
                        SetStatus($"Page {pageIndex + 1} - could not render");
                        return;
                    }

                    renderedPage = new RenderedPage(result.Bitmap, result.DipWidth, result.DipHeight, result.Width, result.Height);
                    _renderCache[(pageIndex, dpiX)] = renderedPage;
                }

                if (_doc is null) return;

                _renderDims[pageIndex] = ((int)Math.Round(renderedPage.DisplayWidth), (int)Math.Round(renderedPage.DisplayHeight));
                PageImage.Source = renderedPage.Bitmap;
                PageImage.Width = renderedPage.DisplayWidth;
                PageImage.Height = renderedPage.DisplayHeight;
                _annotationCanvas.Width = renderedPage.DisplayWidth;
                _annotationCanvas.Height = renderedPage.DisplayHeight;
                ClearSelection();
                ClearSecondaryPages();
                RenderAllAnnotations(pageIndex);
                SetStatus($"Page {pageIndex + 1} of {_doc.PageCount} - {Zoom.DisplayText}");
                // Defer additional pages until layout has settled so ActualWidth is valid.
                // RenderPageLinks runs AFTER RenderAdditionalPages so ClearSecondaryPages
                // inside RenderAdditionalPages doesn't wipe the overlays we just added.
                int linkBitmapW = renderedPage.PixelWidth;
                int linkBitmapH = renderedPage.PixelHeight;
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                {
                    RenderAdditionalPages(pageIndex);
                    RenderPageLinks(pageIndex, linkBitmapW, linkBitmapH);
                    RenderFormFields(pageIndex, linkBitmapW, linkBitmapH);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                PageImage.Source = null;
                SetStatus($"Render error: {ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>
        /// Clears all dynamically-added secondary page borders from the panel,
        /// leaving only the first child (the primary page border).
        /// </summary>
        private void ClearSecondaryPages()
        {
            if (_pageContentPanel is null) return;
            while (_pageContentPanel.Children.Count > 1)
            {
                int last = _pageContentPanel.Children.Count - 1;
                // Null Image.Source before remove so the WriteableBitmap (often several MB on
                // HiDPI) can be collected promptly instead of lingering until WPF’s next GC.
                if (_pageContentPanel.Children[last] is Border border && border.Child is Grid grid)
                {
                    foreach (var child in grid.Children)
                    {
                        if (child is Image img) img.Source = null;
                    }
                }
                _pageContentPanel.Children.RemoveAt(last);
            }
            // NOTE: do NOT reset _pageContentPanel.Width here.  Width is managed exclusively
            // by RenderAdditionalPages (which runs only via Dispatcher) so that no synchronous
            // call to ClearSecondaryPages triggers an intermediate layout pass that would cause
            // the primary page to flash centered and then jerk back to left-aligned.
            // Clear any link overlays from the annotation canvas.
            foreach (var lo in _linkOverlays)
                _annotationCanvas.Children.Remove(lo);
            _linkOverlays.Clear();
        }

        /// <summary>
        /// Renders all remaining pages as a grid that wraps based on available viewport width.
        /// The WrapPanel's Width is set to viewport/zoom so WPF handles row-breaking automatically.
        /// Each secondary page is click-to-navigate; annotation tools only work on the primary page.
        /// </summary>
        private async void RenderAdditionalPages(int primaryPageIdx)
        {
            if (_currentFile is null || _doc is null) return;

            // Cancel any in-flight secondary render so stale pages from the previous run
            // don’t land on the panel after the user has navigated or re-zoomed.
            _secondaryRenderCts?.Cancel();
            _secondaryRenderCts = new CancellationTokenSource();
            var ct = _secondaryRenderCts.Token;

            ClearSecondaryPages();

            // Only Grid and Two-Page render secondary tiles into the wrap panel. Single and
            // Continuous never do (Continuous uses its own ContinuousPanel).
            bool twoPage = _viewMode == ViewMode.TwoPage;
            if (!_gridViewEnabled && !twoPage)
            {
                _pageContentPanel.Width = double.NaN;
                return;
            }

            double viewportW = PagePreviewPanel.ActualWidth;
            if (viewportW <= 0 || _doc.PageCount <= 1)
            {
                // Single-page document or viewport not yet measured: free the explicit width
                // so the WrapPanel sizes to content and the page stays centred.
                _pageContentPanel.Width = double.NaN;
                return;
            }

            // Snap the WrapPanel width to a whole number of page-width slots.
            // This guarantees panelW * zoomLevel + 24 <= viewportW, so the surrounding
            // Border always has room to be centered by HorizontalAlignment="Center".
            // (Using viewportW / zoom - pad fills the viewport exactly and leaves no room.)
            double primaryPageW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 595;
            double pageSlotW = primaryPageW + 12; // page width + right-gutter margin
            double availablePreZoom = (viewportW - 24) / Zoom.ZoomLevel; // inner space in pre-zoom coords
            // Two-Page always shows exactly two columns; Grid wraps to fit the viewport.
            int pagesPerRow = twoPage ? 2 : Math.Max(1, (int)(availablePreZoom / pageSlotW));
            double panelW = pagesPerRow * pageSlotW;
            if (panelW > 0) _pageContentPanel.Width = panelW;

            var dpiInfo = VisualTreeHelper.GetDpi(this);
            double dpiScaleX = dpiInfo.DpiScaleX;
            double dpiScaleY = dpiInfo.DpiScaleY;
            int scaledMax = (int)(1536 * Math.Max(dpiScaleX, dpiScaleY));

            // Cap how many secondary pages we render at once. Long documents otherwise
            // allocate a (potentially multi-MB) bitmap per page on first grid display.
            // Two-Page renders just the single page to the right of the primary.
            int maxSecondaryPages = twoPage ? 1 : 25;
            int lastPage = Math.Min(_doc.PageCount - 1, primaryPageIdx + maxSecondaryPages);
            string currentFile = _currentFile;

            List<(int pi, int w, int h, byte[] rawBytes)> pages;
            try
            {
                pages = await Task.Run(() =>
                {
                    var result = new List<(int pi, int w, int h, byte[] rawBytes)>();
                    using var docReader = DocLib.Instance.GetDocReader(currentFile, new PageDimensions(scaledMax, scaledMax));
                    for (int i = primaryPageIdx + 1; i <= lastPage; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        using var pageReader = docReader.GetPageReader(i);
                        int w = pageReader.GetPageWidth();
                        int h = pageReader.GetPageHeight();
                        var rawBytes = pageReader.GetImage();
                        if (w <= 0 || h <= 0 || rawBytes is null) continue;
                        result.Add((i, w, h, rawBytes));
                    }
                    return result;
                }, ct);
            }
            catch (OperationCanceledException) { return; }
            catch { return; /* non-critical; primary page already visible */ }

            if (ct.IsCancellationRequested) return;

            foreach (var (pi, w, h, rawBytes) in pages)
            {
                if (ct.IsCancellationRequested) return;

                _renderDims[pi] = (w, h);
                var bitmap = new WriteableBitmap(w, h, 96.0 * dpiScaleX, 96.0 * dpiScaleY, PixelFormats.Bgra32, null);
                bitmap.WritePixels(new Int32Rect(0, 0, w, h), rawBytes, w * 4, 0);

                var img = new Image { Source = bitmap, Stretch = Stretch.None };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

                var overlay = new Canvas
                {
                    Width = w, Height = h,
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    ToolTip = $"Page {pi + 1} — click to navigate"
                };
                overlay.PreviewMouseLeftButtonDown += (_, _) => PageList.SelectedIndex = pi;

                var pageGrid = new Grid();
                pageGrid.Children.Add(img);
                pageGrid.Children.Add(overlay);
                // Add link overlays on top of the full-page nav overlay so PDF links
                // in secondary pages are clickable and navigate to their targets directly.
                AddSecondaryPageLinks(pi, pageGrid, w, h);

                // Uniform right+bottom margin gives consistent gutters in both dimensions.
                _pageContentPanel.Children.Add(new Border
                {
                    Background = Brushes.White,
                    Margin = new Thickness(0, 0, 12, 12),
                    Child = pageGrid
                });
            }
        }

        private void SetStatus(string text) => StatusText.Text = text;

        private void SetBusy(bool isBusy, string? status = null)
        {
            _busyDepth = isBusy ? _busyDepth + 1 : Math.Max(0, _busyDepth - 1);
            Mouse.OverrideCursor = _busyDepth > 0 ? Cursors.Wait : null;
            if (!string.IsNullOrEmpty(status)) SetStatus(status);
        }

        private void SetFileOperationBusy(bool isBusy, string? status = null)
        {
            if (_isFileOperationBusy == isBusy)
            {
                if (!string.IsNullOrEmpty(status)) SetStatus(status);
                return;
            }

            _isFileOperationBusy = isBusy;
            IsEnabled = !isBusy;
            SetBusy(isBusy, status);
        }

        /// <summary>
        /// Re-renders secondary pages and then link overlays for the current page.
        /// Must be called via Dispatcher so layout is settled before RenderAdditionalPages
        /// reads ActualWidth. All zoom-change and sidebar-toggle dispatch sites use this
        /// instead of a bare RenderAdditionalPages call so link overlays are never left
        /// cleared without being re-added.
        /// </summary>
        private void RefreshPageView(int pageIndex)
        {
            // Continuous manages its own rendering through SetupContinuousView; nothing to do here.
            if (_viewMode == ViewMode.Continuous) return;

            // Grid wraps to fit the viewport (no horizontal scrollbar); other modes use Auto.
            PagePreviewPanel.HorizontalScrollBarVisibility =
                _viewMode == ViewMode.Grid ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

            if (_viewMode == ViewMode.Grid || _viewMode == ViewMode.TwoPage)
            {
                RenderAdditionalPages(pageIndex);
            }
            else
            {
                ClearSecondaryPages();
                _pageContentPanel.Width = double.NaN;
            }
            if (_renderDims.TryGetValue(pageIndex, out var dims))
            {
                RenderPageLinks(pageIndex, dims.w, dims.h);
                RenderFormFields(pageIndex, dims.w, dims.h);
            }
        }

        // The toolbar toggle is a quick switch between Grid and Single views.
        private void GridViewToggle_Click(object sender, RoutedEventArgs e)
        {
            SetViewMode(_gridViewToggle.IsChecked == true ? ViewMode.Grid : ViewMode.Single);
        }

        // ============================================================
        // View mode switching
        // ============================================================

        /// <summary>
        /// Switches the app-wide page view mode, persists it, swaps the visible layout host
        /// (wrap panel vs. continuous strip), and applies the open-fit rule for the new mode:
        /// Single/Two-Page → fit page, Continuous → fit width, Grid → existing column-fit.
        /// </summary>
        private void SetViewMode(ViewMode mode)
        {
            if (_viewMode == mode)
            {
                // Keep the toolbar toggle in sync even on a no-op (e.g. settings re-selecting
                // the active mode) so it never drifts out of step with _viewMode.
                _gridViewToggle.IsChecked = mode == ViewMode.Grid;
                return;
            }
            _viewMode = mode;
            _gridViewToggle.IsChecked = mode == ViewMode.Grid;
            try
            {
                TDPdf.Properties.Settings.Default.ViewMode = mode.ToString();
                TDPdf.Properties.Settings.Default.Save();
            }
            catch { /* non-critical user preference */ }

            bool isContinuous = mode == ViewMode.Continuous;
            _pageContentPanel.Visibility = isContinuous ? Visibility.Collapsed : Visibility.Visible;
            // The primary page's parent Border (first child of PageContentGrid) must also hide
            // so its leftover bitmap/overlay doesn't show behind the continuous strip.
            if (PageImage.Parent is FrameworkElement pageGridChild
                && pageGridChild.Parent is FrameworkElement primaryBorder)
                primaryBorder.Visibility = isContinuous ? Visibility.Collapsed : Visibility.Visible;
            _continuousPanel.Visibility = isContinuous ? Visibility.Visible : Visibility.Collapsed;

            if (!isContinuous)
            {
                _continuousRenderCts?.Cancel();
                // #85: stop any in-flight re-sharpen and drop its slot/bitmap bookkeeping.
                _continuousSharpenTimer?.Stop();
                _continuousSharpenCts?.Cancel();
                _continuousSharpPages.Clear();
                _continuousBaseBitmaps.Clear();
                _continuousSharpW = 0;
                _continuousPanel.Children.Clear();
                _continuousTops.Clear();
            }

            if (_doc is null) return;
            int idx = Math.Max(0, PageList.SelectedIndex);

            if (mode == ViewMode.Continuous)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => SetupContinuousView(idx));
                return;
            }

            // Leaving continuous (or switching between wrap-panel modes): re-render the primary
            // page and its secondary tiles, then apply the per-mode open-fit rule.
            _secondaryRenderCts?.Cancel();
            ClearSecondaryPages();
            _pageContentPanel.Width = double.NaN;
            RenderPage(mode == ViewMode.Grid ? 0 : idx);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                if (mode == ViewMode.Single || mode == ViewMode.TwoPage) FitToPage();
                // Grid keeps its existing column-fit default (applied via RefreshPageView).
                RefreshPageView(mode == ViewMode.Grid ? 0 : idx);
            });
        }

        /// <summary>
        /// Applies the persisted view mode's layout and open-fit rule when a document is first
        /// opened (the mode itself doesn't change, so SetViewMode's no-op guard would skip this).
        /// </summary>
        private void ApplyViewModeOnOpen()
        {
            if (_doc is null) return;
            bool isContinuous = _viewMode == ViewMode.Continuous;
            _pageContentPanel.Visibility = isContinuous ? Visibility.Collapsed : Visibility.Visible;
            if (PageImage.Parent is FrameworkElement pageGridChild
                && pageGridChild.Parent is FrameworkElement primaryBorder)
                primaryBorder.Visibility = isContinuous ? Visibility.Collapsed : Visibility.Visible;
            _continuousPanel.Visibility = isContinuous ? Visibility.Visible : Visibility.Collapsed;

            if (isContinuous)
            {
                SetupContinuousView(Math.Max(0, PageList.SelectedIndex));
                return;
            }

            // Single / Two-Page open fit-to-page; Grid keeps its column-fit default (fit-width
            // is a sensible neutral starting zoom that RefreshPageView then column-snaps).
            if (_viewMode == ViewMode.Single || _viewMode == ViewMode.TwoPage) FitToPage();
            else FitToWidth();
            RefreshPageView(_viewMode == ViewMode.Grid ? 0 : Math.Max(0, PageList.SelectedIndex));
        }

        // ============================================================
        // Continuous (vertical-strip) view
        // ============================================================

        /// <summary>
        /// Builds the continuous strip: one placeholder slot per page sized from the PDF's
        /// natural aspect ratio, then kicks off progressive background rendering. Pages fill
        /// in asynchronously so even very long documents never block the UI thread.
        /// Continuous view is view + navigate only — annotation editing happens in Single,
        /// Two-Page, or Grid view.
        /// </summary>
        private void SetupContinuousView(int initialPage)
        {
            if (_doc is null) return;
            _continuousRenderCts?.Cancel();
            _continuousPanel.Children.Clear();
            _continuousTops.Clear();

            // PDF natural page width in WPF DIPs (96 DIP/in, 72 pt/in). Zoom-independent so
            // FitToWidth (= viewportW / _continuousPageW) doesn't cancel against the zoom level.
            var refPage = _doc.Pages[0];
            _continuousPageW = Math.Max(200.0, refPage.Width.Point * (96.0 / 72.0));

            double y = 0;
            for (int i = 0; i < _doc.PageCount; i++)
            {
                _continuousTops.Add(y);
                var pdfPage = _doc.Pages[i];
                double pw = pdfPage.Width.Point, ph = pdfPage.Height.Point;
                // PdfSharpCore reports the un-rotated box; swap for quarter rotations so the
                // placeholder aspect matches what Docnet will rasterize.
                int rot = ((pdfPage.Rotate % 360) + 360) % 360;
                if (rot == 90 || rot == 270) (pw, ph) = (ph, pw);
                double ratio = Math.Max(0.1, ph / Math.Max(1, pw));
                double slotH = _continuousPageW * ratio;

                var pageImg = new Image { Stretch = Stretch.None, Width = _continuousPageW, Height = slotH };
                RenderOptions.SetBitmapScalingMode(pageImg, BitmapScalingMode.HighQuality);

                int capturedI = i;
                var placeholder = new Border
                {
                    Width = _continuousPageW,
                    Height = slotH,
                    Margin = new Thickness(0, 0, 0, 12),
                    Background = BrushResource("BgPanel"),
                    Tag = i,
                    Child = pageImg
                };
                placeholder.PreviewMouseLeftButtonDown += (_, _) => SelectContinuousPage(capturedI);
                _continuousPanel.Children.Add(placeholder);
                y += slotH + 12;
            }

            // Continuous opens fit-to-width per the open-fit rules.
            FitToWidth();

            _continuousScrollTarget = initialPage;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                () => ScrollContinuousToPageSuppressed(initialPage));

            _ = RenderContinuousPages();
        }

        /// <summary>Selects a page in continuous view without re-triggering a scroll loop.</summary>
        private void SelectContinuousPage(int pageIndex)
        {
            if (pageIndex < 0 || PageList.SelectedIndex == pageIndex) return;
            _suppressContinuousScrollSync = true;
            PageList.SelectedIndex = pageIndex;
            _suppressContinuousScrollSync = false;
        }

        /// <summary>
        /// Progressively rasterizes every page on a background thread and streams each bitmap
        /// into its placeholder slot as soon as it is ready. Slot heights are corrected from the
        /// actual rendered bitmap so cropped/rotated pages fit cleanly, and scroll offsets are
        /// recomputed so the initial scroll target lands on the right page.
        /// </summary>
        private async System.Threading.Tasks.Task RenderContinuousPages()
        {
            if (_doc is null || _currentFile is null) return;
            _continuousRenderCts?.Cancel();
            _continuousRenderCts = new CancellationTokenSource();
            var cts = _continuousRenderCts;

            // A full base pass repaints every slot, so any hi-res re-sharpen state is now stale (#85):
            // cancel in-flight sharpening and forget which slots were sharpened / their base bitmaps.
            _continuousSharpenCts?.Cancel();
            _continuousSharpPages.Clear();
            _continuousBaseBitmaps.Clear();
            _continuousSharpW = 0;

            string currentFile = _currentFile;
            int pageCount = _doc.PageCount;
            double targetW = _continuousPageW;
            int renderW = Math.Max(800, Math.Min(2048, (int)(targetW * 2)));

            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    using var docReader = DocLib.Instance.GetDocReader(
                        currentFile, new PageDimensions(renderW, renderW * 2));

                    for (int i = 0; i < pageCount; i++)
                    {
                        if (cts.IsCancellationRequested) return;
                        using var pr = docReader.GetPageReader(i);
                        int w = pr.GetPageWidth();
                        int h = pr.GetPageHeight();
                        var raw = pr.GetImage();
                        if (w <= 0 || h <= 0 || raw is null) continue;

                        int fi = i, fw = w, fh = h;
                        byte[] bytes = raw;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (cts.IsCancellationRequested || _viewMode != ViewMode.Continuous) return;
                            if (fi >= _continuousPanel.Children.Count) return;
                            if (_continuousPanel.Children[fi] is not Border slot) return;

                            double dipW = slot.Width;
                            double dipH = dipW * fh / fw;
                            double dpiX = 96.0 * fw / dipW;
                            double dpiY = 96.0 * fh / dipH;

                            var bmp = new WriteableBitmap(fw, fh, dpiX, dpiY, PixelFormats.Bgra32, null);
                            bmp.WritePixels(new Int32Rect(0, 0, fw, fh), bytes, fw * 4, 0);
                            bmp.Freeze();

                            if (slot.Child is Image pageImg)
                            {
                                pageImg.Source = bmp;
                                pageImg.Width = dipW;
                                pageImg.Height = dipH;
                                slot.Background = Brushes.White;
                                slot.Height = dipH;
                            }

                            // Pages render strictly top-to-bottom, so when page fi finishes every
                            // page above it already has its final height and top. Update only this
                            // page's top from the previous page's finalized bottom (O(1) per page,
                            // avoiding an O(n^2) full rebuild on long documents). Pages below fi are
                            // still placeholders; they correct their own tops as they render.
                            if (fi < _continuousTops.Count)
                            {
                                if (fi == 0)
                                {
                                    _continuousTops[0] = 0;
                                }
                                else
                                {
                                    double prevH = ((FrameworkElement)_continuousPanel.Children[fi - 1]).Height;
                                    if (double.IsNaN(prevH)) prevH = 0;
                                    _continuousTops[fi] = _continuousTops[fi - 1] + prevH + 12;
                                }
                            }

                            // Pages render in order, so once the target page is reached every page
                            // above it has its final height; re-scroll so we land precisely on it.
                            if (_continuousScrollTarget >= 0 && fi >= _continuousScrollTarget)
                            {
                                int tgt = _continuousScrollTarget;
                                _continuousScrollTarget = -1;
                                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                                    (Action)(() => ScrollContinuousToPageSuppressed(tgt)));
                            }
                        });
                    }
                }, cts.Token);
            }
            catch { /* render cancelled or doc closed */ }
        }

        // ── Continuous zoom / high-DPI re-sharpen (#85) ───────────────────────────────────────────
        // Debounced trigger: restart a 250 ms timer on every zoom change or scroll event so the
        // re-sharpen runs once the view settles. Cheap when there's nothing to do (a restore-only
        // pass over an almost-always-empty set below the hi-res threshold).
        private void StartContinuousResharpen()
        {
            if (_viewMode != ViewMode.Continuous) return;
            if (_continuousSharpenTimer is null)
            {
                _continuousSharpenTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(250) };
                _continuousSharpenTimer.Tick += (_, _) =>
                {
                    _continuousSharpenTimer!.Stop();
                    if (_viewMode == ViewMode.Continuous) ResharpenContinuousVisible();
                };
            }
            _continuousSharpenTimer.Stop();
            _continuousSharpenTimer.Start();
        }

        // Re-renders ONLY the pages near the viewport at a DPI- and zoom-aware budget and swaps them
        // into their slots; pages that scrolled away (or aren't worth sharpening at this zoom) are
        // restored to their captured base bitmap so the hi-res bitmaps are released. The base render
        // cache is deliberately NOT touched — hi-res bitmaps must never accumulate there. Fully guarded;
        // re-checks _viewMode and cancellation after every await/dispatch.
        private void ResharpenContinuousVisible()
        {
            if (_viewMode != ViewMode.Continuous || _doc is null || _currentFile is null) return;
            if (_continuousTops.Count == 0 || _continuousPanel.Children.Count == 0) return;

            double zoom = Zoom.ZoomLevel;
            double targetW = _continuousPageW;
            int baseW = Math.Max(800, Math.Min(2048, (int)(targetW * 2)));   // same budget as RenderContinuousPages
            var dpiInfo = VisualTreeHelper.GetDpi(this);
            double dpiScale = Math.Max(dpiInfo.DpiScaleX, dpiInfo.DpiScaleY);
            int hiW = (int)Math.Min(4096, targetW * 2 * dpiScale * Math.Max(1.0, zoom));

            // Visible slot range. Slot space is zoom-independent (the shared ScaleTransform supplies
            // the zoom), so divide the scroll offsets back down — the same mapping ScrollChanged uses.
            double viewTop = PagePreviewPanel.VerticalOffset / Math.Max(0.01, zoom);
            double viewBot = (PagePreviewPanel.VerticalOffset + PagePreviewPanel.ViewportHeight) / Math.Max(0.01, zoom);
            var visible = new List<int>();
            for (int i = 0; i < _continuousTops.Count && i < _continuousPanel.Children.Count; i++)
            {
                double top = _continuousTops[i];
                double h = ((FrameworkElement)_continuousPanel.Children[i]).Height;
                if (double.IsNaN(h)) continue;
                if (top + h >= viewTop && top <= viewBot) visible.Add(i);
            }
            if (visible.Count > 0)
            {
                // One page of margin either side so a small scroll stays sharp.
                if (visible[0] > 0) visible.Insert(0, visible[0] - 1);
                if (visible[^1] < _continuousTops.Count - 1) visible.Add(visible[^1] + 1);
            }

            // Below ~1.25× the base budget the re-raster isn't visibly sharper: restore-only pass.
            bool wantHi = hiW >= (int)(baseW * 1.25);

            _continuousSharpenCts?.Cancel();
            _continuousSharpenCts = new CancellationTokenSource();
            var cts = _continuousSharpenCts;

            // Restore pages that were sharpened earlier but have scrolled away (or aren't wanted at
            // this zoom) to their captured base bitmap, releasing their hi-res bitmaps.
            foreach (int p in _continuousSharpPages.ToList())
            {
                if (wantHi && visible.Contains(p)) continue;
                RestoreContinuousBase(p);
                _continuousSharpPages.Remove(p);
            }
            if (!wantHi) return;

            // Zoom changed since the last pass: every sharpened slot is at the wrong budget — redo them.
            bool budgetChanged = hiW != _continuousSharpW;
            _continuousSharpW = hiW;
            var work = visible.Where(p => budgetChanged || !_continuousSharpPages.Contains(p)).ToList();
            if (work.Count == 0) return;

            string currentFile = _currentFile;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                Docnet.Core.Readers.IDocReader? docReader = null;
                try
                {
                    foreach (int p in work)
                    {
                        if (cts.IsCancellationRequested) return;
                        docReader ??= DocLib.Instance.GetDocReader(currentFile, new PageDimensions(hiW, hiW * 2));
                        using var pr = docReader.GetPageReader(p);
                        int w = pr.GetPageWidth(), h = pr.GetPageHeight();
                        var raw = pr.GetImage();
                        if (w <= 0 || h <= 0 || raw is null) continue;

                        int fp = p, fw = w, fh = h;
                        byte[] bytes = raw;
                        if (cts.IsCancellationRequested) return;
                        Dispatcher.Invoke(() =>
                        {
                            if (cts.IsCancellationRequested || _viewMode != ViewMode.Continuous) return;
                            SharpenContinuousSlot(fp, fw, fh, bytes);
                        });
                    }
                }
                catch { /* cancelled or doc closed */ }
                finally { docReader?.Dispose(); }
            }, cts.Token);
        }

        // Swaps a freshly-rendered hi-res bitmap into slot pageIndex, keeping the slot's on-screen size
        // (the shared ScaleTransform still supplies the zoom). Captures the slot's current base bitmap
        // once so RestoreContinuousBase can put it back when the page scrolls away. Only sharpens slots
        // that already carry a base bitmap, so it never fights the streaming base pass.
        private void SharpenContinuousSlot(int pageIndex, int pxW, int pxH, byte[] bgra)
        {
            if (pageIndex < 0 || pageIndex >= _continuousPanel.Children.Count) return;
            if (_continuousPanel.Children[pageIndex] is not Border slot) return;
            if (slot.Child is not Image img) return;
            if (img.Source is not BitmapSource baseSrc) return;   // base not rendered yet — leave it

            double dipW = img.Width;
            if (double.IsNaN(dipW) || dipW <= 0) dipW = slot.Width;
            if (double.IsNaN(dipW) || dipW <= 0) return;
            double dipH = dipW * pxH / pxW;
            double dpiX = 96.0 * pxW / dipW;
            double dpiY = 96.0 * pxH / dipH;

            if (!_continuousBaseBitmaps.ContainsKey(pageIndex))
                _continuousBaseBitmaps[pageIndex] = baseSrc;

            var bmp = new WriteableBitmap(pxW, pxH, dpiX, dpiY, PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, pxW, pxH), bgra, pxW * 4, 0);
            bmp.Freeze();
            img.Source = bmp;
            img.Width = dipW;
            img.Height = dipH;
            _continuousSharpPages.Add(pageIndex);
        }

        // Restores a previously-sharpened slot to its captured base bitmap so the hi-res bitmap is
        // released, then forgets the capture. No capture (page never sharpened) = no-op.
        private void RestoreContinuousBase(int pageIndex)
        {
            if (!_continuousBaseBitmaps.TryGetValue(pageIndex, out var baseBmp)) return;
            if (pageIndex >= 0 && pageIndex < _continuousPanel.Children.Count
                && _continuousPanel.Children[pageIndex] is Border slot
                && slot.Child is Image img)
            {
                img.Source = baseBmp;
                img.Width = baseBmp.Width;
                img.Height = baseBmp.Height;
            }
            _continuousBaseBitmaps.Remove(pageIndex);
        }

        private void ScrollContinuousToPage(int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= _continuousTops.Count) return;
            double target = _continuousTops[pageIndex] * Zoom.ZoomLevel;
            PagePreviewPanel.ScrollToVerticalOffset(target);
        }

        /// <summary>
        /// Programmatically scrolls to a page while suppressing the scroll→selection feedback
        /// loop. ScrollToVerticalOffset raises ScrollChanged on a later layout pass, so the
        /// suppression flag is held until after that callback (cleared at Loaded priority).
        /// </summary>
        private void ScrollContinuousToPageSuppressed(int pageIndex)
        {
            _suppressContinuousScrollSync = true;
            ScrollContinuousToPage(pageIndex);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                (Action)(() => _suppressContinuousScrollSync = false));
        }

        /// <summary>
        /// Tracks scroll position in continuous view: updates the page-number box and the sidebar
        /// thumbnail selection to whichever page is nearest the viewport center.
        /// </summary>
        private void PagePreviewPanel_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_viewMode != ViewMode.Continuous || _continuousTops.Count == 0) return;
            // #85: once scrolling settles, sharpen the pages now in view and release the ones that
            // left. Debounced, so streaming base render / rapid scroll just keeps resetting the timer;
            // programmatic scrolls count too (their offset change still moves the visible window).
            StartContinuousResharpen();
            // Ignore scroll events caused by our own programmatic scrolls (sidebar selection,
            // zoom re-anchor, setup) so they don't bounce back into a selection change.
            if (_suppressContinuousScrollSync) return;

            double viewportCenter = (PagePreviewPanel.VerticalOffset + PagePreviewPanel.ViewportHeight * 0.5)
                                    / Math.Max(0.01, Zoom.ZoomLevel);
            int nearest = 0;
            double minDist = double.MaxValue;
            for (int i = 0; i < _continuousTops.Count && i < _continuousPanel.Children.Count; i++)
            {
                double h = ((FrameworkElement)_continuousPanel.Children[i]).Height;
                if (double.IsNaN(h)) h = 0;
                double center = _continuousTops[i] + h * 0.5;
                double dist = Math.Abs(center - viewportCenter);
                if (dist < minDist) { minDist = dist; nearest = i; }
            }

            if (PageList.SelectedIndex != nearest)
            {
                _pageJumpBox.Text = (nearest + 1).ToString();
                // Update the sidebar selection without re-scrolling the strip back.
                _suppressContinuousScrollSync = true;
                PageList.SelectedIndex = nearest;
                _suppressContinuousScrollSync = false;
            }
        }

        // ============================================================
        // PDF Link Annotation Overlays
        // ============================================================

        private readonly record struct LinkInfo(double Cx, double Cy, double Cw, double Ch, object Tag, string Tip, int AnnotIndex);

        /// <summary>
        /// Carries the link target (page index or URI string) plus the annotation's location in
        /// the PDF so the overlay can be used to remove the native annotation on demand.
        /// </summary>
        private sealed class LinkAnnotInfo(object target, int pageIndex, int annotIndex)
        {
            public object Target     { get; } = target;      // int pageIndex or string URI
            public int    PageIndex  { get; } = pageIndex;    // 0-based page in _doc
            public int    AnnotIndex { get; } = annotIndex;   // index inside page /Annots array
        }

        /// <summary>
        /// Parses all link annotations from a PDF page and converts them to canvas-space
        /// rectangles. Works for both primary and secondary page renders.
        /// </summary>
        private List<LinkInfo> GetPageLinks(int pageIndex, int bitmapW, int bitmapH)
        {
            var links = new List<LinkInfo>();
            if (_doc is null) return links;
            try
            {
                var pdfPage = _doc.Pages[pageIndex];
                var annotsArr = pdfPage.Elements.GetArray("/Annots");
                if (annotsArr is null || annotsArr.Elements.Count == 0) return links;

                double pageWidthPt  = pdfPage.Width.Point;
                double pageHeightPt = pdfPage.Height.Point;
                if (pageWidthPt  <= 0) pageWidthPt  = 595.28;
                if (pageHeightPt <= 0) pageHeightPt = 841.89;

                for (int i = 0; i < annotsArr.Elements.Count; i++)
                {
                    PdfItem? elem = annotsArr.Elements[i];
                    PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                    if (ann is null) continue;

                    var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                    if (!subtype.Contains("Link")) continue;

                    var rectArr = ann.Elements.GetArray("/Rect");
                    if (rectArr is null || rectArr.Elements.Count < 4) continue;
                    double rx1 = rectArr.Elements.GetReal(0);
                    double ry1 = rectArr.Elements.GetReal(1);
                    double rx2 = rectArr.Elements.GetReal(2);
                    double ry2 = rectArr.Elements.GetReal(3);
                    if (rx1 > rx2) (rx1, rx2) = (rx2, rx1);
                    if (ry1 > ry2) (ry1, ry2) = (ry2, ry1);

                    double cx = rx1 / pageWidthPt  * bitmapW;
                    double cy = (pageHeightPt - ry2) / pageHeightPt * bitmapH;
                    double cw = (rx2 - rx1) / pageWidthPt  * bitmapW;
                    double ch = (ry2 - ry1) / pageHeightPt * bitmapH;
                    if (cw < 1 || ch < 1) continue;

                    int? targetPage = null;
                    string? uri = null;

                    var actionDict = ann.Elements.GetDictionary("/A");
                    if (actionDict != null)
                    {
                        var s = actionDict.Elements["/S"]?.ToString() ?? "";
                        if (s.Contains("GoTo"))
                            targetPage = ResolveDest(actionDict.Elements["/D"]);
                        else if (s.Contains("URI"))
                            uri = actionDict.Elements.GetString("/URI");
                    }
                    else
                    {
                        targetPage = ResolveDest(ann.Elements["/Dest"]);
                    }

                    if (targetPage is null && uri is null) continue;

                    object tag = targetPage.HasValue ? (object)targetPage.Value : uri!;
                    string tip = targetPage.HasValue ? $"Go to page {targetPage.Value + 1}" : uri!;
                    links.Add(new LinkInfo(cx, cy, cw, ch, tag, tip, i));
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GetPageLinks: {ex}"); }
            return links;
        }

        /// <summary>
        /// Renders link overlays for the primary page onto the annotation canvas.
        /// Uses a manual bounds-check in Canvas_MouseLeftButtonDown for hit detection
        /// (transparent Canvas children are unreliable for WPF hit-testing alone).
        /// </summary>
        private void RenderPageLinks(int pageIndex, int bitmapW, int bitmapH)
        {
            if (_doc is null || _currentFile is null) return;

            var links = GetPageLinks(pageIndex, bitmapW, bitmapH);
            foreach (var lnk in links)
            {
                var info = new LinkAnnotInfo(lnk.Tag, pageIndex, lnk.AnnotIndex);
                var overlay = new Canvas
                {
                    Width            = lnk.Cw,
                    Height           = lnk.Ch,
                    Background       = Brushes.Transparent,
                    Cursor           = Cursors.Hand,
                    ToolTip          = lnk.Tip,
                    Tag              = info,
                    IsHitTestVisible = true,
                };
                Canvas.SetLeft(overlay, lnk.Cx);
                Canvas.SetTop(overlay, lnk.Cy);

                // Right-click context menu: copy the target or remove the native PDF annotation.
                var cm = new ContextMenu();
                if (lnk.Tag is string uriTag && uriTag.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    cm.Items.Add(MakeMenuItem("Copy Email Address", (_, _) =>
                        Clipboard.SetText(uriTag["mailto:".Length..])));
                else if (lnk.Tag is string httpTag)
                    cm.Items.Add(MakeMenuItem("Copy URL", (_, _) => Clipboard.SetText(httpTag)));
                cm.Items.Add(MakeMenuItem("Remove Link from PDF", (_, _) =>
                    RemoveLinkAnnotation(info.PageIndex, info.AnnotIndex)));
                overlay.ContextMenu = cm;

                _annotationCanvas.Children.Add(overlay);
                _linkOverlays.Add(overlay);
            }

            if (links.Count > 0)
                SetStatus($"Page {pageIndex + 1} of {_doc.PageCount}  ({links.Count} link{(links.Count == 1 ? "" : "s")})");
        }

        /// <summary>
        /// Adds link overlays to a secondary-page Grid so PDF links within that page are
        /// clickable even when the page is visible only in the multi-page grid view.
        ///
        /// Canvas.SetLeft/Top attached properties ONLY take effect when the element's
        /// direct parent is a Canvas.  Adding link elements straight into the Grid (as
        /// siblings of the page-nav overlay) would leave them all at (0,0), causing every
        /// click to hit the wrong element.  Instead we create a transparent Canvas
        /// container the same size as the page and use it as the coordinate space.
        ///
        /// The container uses Background=null so non-link areas are hit-test-transparent
        /// and clicks fall through to the full-page nav overlay beneath it.  Link
        /// overlays inside the container use Background=Transparent so they ARE hit-
        /// testable and receive clicks.  The container is added last → topmost z-order.
        /// </summary>
        private void AddSecondaryPageLinks(int pageIndex, Grid pageGrid, int bitmapW, int bitmapH)
        {
            var links = GetPageLinks(pageIndex, bitmapW, bitmapH);
            if (links.Count == 0) return;

            // Container: not hit-testable itself (Background=null), but its children are.
            var linkCanvas = new Canvas { Width = bitmapW, Height = bitmapH, Background = null };

            foreach (var lnk in links)
            {
                var lo = new Canvas
                {
                    Width            = lnk.Cw,
                    Height           = lnk.Ch,
                    Background       = Brushes.Transparent,   // must be non-null to be hittable
                    Cursor           = Cursors.Hand,
                    ToolTip          = lnk.Tip,
                    IsHitTestVisible = true,
                };
                Canvas.SetLeft(lo, lnk.Cx);   // works because parent IS a Canvas
                Canvas.SetTop(lo, lnk.Cy);

                var capturedTag = lnk.Tag;
                lo.PreviewMouseLeftButtonDown += (_, args) =>
                {
                    if (capturedTag is int tp)
                        PageList.SelectedIndex = tp;
                    else if (capturedTag is string u)
                        try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
                    args.Handled = true;
                };

                linkCanvas.Children.Add(lo);
            }

            // Add container last so it is topmost in z-order; non-link areas fall through.
            pageGrid.Children.Add(linkCanvas);
        }

        /// <summary>
        /// Removes a native PDF link annotation from the page /Annots array and persists the change.
        /// Called from the "Remove Link from PDF" context-menu item on link overlays.
        /// </summary>
        private void RemoveLinkAnnotation(int pageIndex, int annotIndex)
        {
            if (_doc is null || pageIndex >= _doc.PageCount) return;
            try
            {
                var pdfPage = _doc.Pages[pageIndex];
                var annotsArr = pdfPage.Elements.GetArray("/Annots");
                if (annotsArr is null || annotIndex >= annotsArr.Elements.Count) return;
                annotsArr.Elements.RemoveAt(annotIndex);
                MarkDirty();
                SaveTempAndReload();
                // Refresh the current page view so the overlay disappears.
                int sel = PageList.SelectedIndex;
                PageList.SelectedIndex = -1;
                PageList.SelectedIndex = sel;
                SetStatus("Link removed.");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Remove link failed:\n{ex.Message}", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Strips visual styling (border, color, appearance stream) from all Link annotations
        /// in the document so they render as invisible clickable areas rather than colored
        /// rectangles that can look like strikethroughs in other PDF viewers.
        /// </summary>
        private static void StripLinkAnnotationBorders(PdfDocument doc)
        {
            foreach (var pdfPage in doc.Pages)
            {
                var annotsArr = pdfPage.Elements.GetArray("/Annots");
                if (annotsArr is null) continue;
                for (int i = 0; i < annotsArr.Elements.Count; i++)
                {
                    PdfItem? elem = annotsArr.Elements[i];
                    PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                    if (ann is null) continue;
                    var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                    if (!subtype.Contains("Link")) continue;
                    // Remove appearance stream and color; set /Border [0 0 0] for invisible border.
                    ann.Elements.Remove("/AP");
                    ann.Elements.Remove("/C");
                    ann.Elements.Remove("/BS");
                    var borderArr = new PdfArray();
                    borderArr.Elements.Add(new PdfInteger(0));
                    borderArr.Elements.Add(new PdfInteger(0));
                    borderArr.Elements.Add(new PdfInteger(0));
                    ann.Elements["/Border"] = borderArr;
                }
            }
        }

        /// <summary>
        /// Resolves a /Dest value (PdfArray, PdfString, or PdfName) to a 0-based page index.
        /// Returns null if the destination cannot be resolved.
        /// Note: PdfReference is internal to PdfSharpCore so we use reflection for ObjectNumber
        /// and var-inferred types instead of the type name.
        /// </summary>
        private int? ResolveDest(PdfItem? destItem)
        {
            if (destItem is null || _doc is null) return null;

            // Dereference indirect object if needed (PdfReference is internal, use duck-typing).
            destItem = DerefItem(destItem);

            PdfArray? arr = null;

            if (destItem is PdfArray a)
            {
                arr = a;
            }
            else if (destItem is PdfString || destItem is PdfName)
            {
                // Named destination — look up in the document catalog
                arr = ResolveNamedDest(destItem);
            }

            if (arr is null || arr.Elements.Count == 0) return null;

            // First element of the destination array is an indirect page reference.
            // PdfReference.ObjectNumber is public but its type is internal; use reflection.
            var pageRefItem = arr.Elements[0];
            int elemObjNum = GetObjectNumber(pageRefItem);
            if (elemObjNum > 0)
            {
                for (int i = 0; i < _doc.PageCount; i++)
                {
                    // PdfPage.Reference (public) gives us access to ObjectNumber
                    var pgRef = _doc.Pages[i].Reference;
                    if (pgRef != null && pgRef.ObjectNumber == elemObjNum)
                        return i;
                }
            }
            else if (pageRefItem is PdfInteger pageInt)
            {
                int pn = pageInt.Value;
                if (pn >= 0 && pn < _doc.PageCount) return pn;
            }

            return null;
        }

        /// <summary>
        /// Dereferences a PdfItem if it is an indirect reference (PdfReference is internal;
        /// we detect it by looking for a public "Value" property returning PdfObject).
        /// </summary>
        private static PdfItem DerefItem(PdfItem item)
        {
            var valueProp = item.GetType().GetProperty("Value",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (valueProp?.GetValue(item) is PdfObject resolved)
                return resolved;
            return item;
        }

        /// <summary>
        /// Returns the PDF object number of a PdfItem that is an indirect reference, or -1.
        /// Handles the internal PdfReference type via reflection.
        /// </summary>
        private static int GetObjectNumber(PdfItem? item)
        {
            if (item is null) return -1;
            var prop = item.GetType().GetProperty("ObjectNumber",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            return prop?.GetValue(item) is int n ? n : -1;
        }

        /// <summary>
        /// Resolves a named destination (string or name) to a destination array using the
        /// catalog's /Dests dictionary or /Names /Dests name tree.
        /// </summary>
        private PdfArray? ResolveNamedDest(PdfItem nameItem)
        {
            if (_doc is null) return null;
            string name = nameItem switch
            {
                PdfString s => s.Value,
                PdfName   n => n.Value.TrimStart('/'),
                _           => ""
            };
            if (string.IsNullOrEmpty(name)) return null;

            var catalog = _doc.Internals.Catalog;

            // Legacy /Dests dictionary (direct mapping)
            var dests = catalog.Elements.GetDictionary("/Dests");
            if (dests != null)
            {
                PdfItem? val = DerefItem(dests.Elements[name] ?? dests.Elements["/" + name] ?? new PdfInteger(-1));
                if (val is PdfArray da) return da;
                if (val is PdfDictionary dd) return dd.Elements.GetArray("/D");
            }

            // Modern /Names /Dests name tree
            var names = catalog.Elements.GetDictionary("/Names");
            var destTree = names?.Elements.GetDictionary("/Dests");
            if (destTree != null)
                return ResolveNameTree(destTree, name);

            return null;
        }

        /// <summary>
        /// Walks a PDF name tree to find the destination array for the given name.
        /// </summary>
        private static PdfArray? ResolveNameTree(PdfDictionary node, string name)
        {
            // Leaf node: flat /Names array [key val key val ...]
            var namesArr = node.Elements.GetArray("/Names");
            if (namesArr != null)
            {
                for (int i = 0; i + 1 < namesArr.Elements.Count; i += 2)
                {
                    var key = namesArr.Elements[i];
                    string keyStr = key is PdfString ks ? ks.Value : key?.ToString() ?? "";
                    if (keyStr == name)
                    {
                        PdfItem? val = DerefItem(namesArr.Elements[i + 1]);
                        if (val is PdfArray va) return va;
                        if (val is PdfDictionary vd) return vd.Elements.GetArray("/D");
                    }
                }
            }

            // Intermediate node: recurse into /Kids
            var kids = node.Elements.GetArray("/Kids");
            if (kids != null)
            {
                for (int i = 0; i < kids.Elements.Count; i++)
                {
                    PdfItem? kid = DerefItem(kids.Elements[i]);
                    if (kid is PdfDictionary kd)
                    {
                        var result = ResolveNameTree(kd, name);
                        if (result != null) return result;
                    }
                }
            }

            return null;
        }

        // ============================================================
        // PDF Form Field Overlays (interactive AcroForm filling)
        // ============================================================
        // Ported from upstream KillerPDF v1.4.2 form filling, adapted to TDPdf's
        // multi-tab DocumentContext: pending values live on the active context
        // (_formTextValues/_formCheckValues/_formRadioValues) so they survive tab
        // switches, and the overlay controls reuse the same PDF-point → canvas
        // coordinate conversion as the link overlays (GetPageLinks/RenderPageLinks).
        // Supported field types: text (/Tx), checkbox & radio (/Btn), dropdown (/Ch).
        // On save the values are baked into the PDF field dictionaries with
        // regenerated /AP /N appearance streams (and /NeedAppearances as a fallback)
        // so other viewers display them. All parsing is wrapped in try/catch so a
        // malformed AcroForm can never crash open or save.

        private readonly record struct FormFieldInfo(
            int    ObjNum,        // widget annotation object number (used as key)
            string FieldType,     // /Tx, /Btn, /Ch
            bool   IsCheckBox,
            bool   IsRadio,
            bool   IsMultiLine,   // /Tx with Multiline flag (bit 12)
            string FieldName,
            string CurrentValue,
            string OnValue,       // radio/checkbox on-state value (e.g. "/Yes")
            bool   IsReadOnly,
            double Cx, double Cy, double Cw, double Ch,
            List<string> Options);

        /// <summary>
        /// Scans the current page's /Annots for Widget subtypes and overlays interactive
        /// WPF controls on the annotation canvas so the user can fill in form fields.
        /// Removes any stale form overlays first (tagged <see cref="FormOverlayTag"/>)
        /// without wiping non-form children, so it is safe to call repeatedly.
        /// </summary>
        private void RenderFormFields(int pageIndex, int canvasW, int canvasH)
        {
            if (_doc is null || _currentFile is null) return;
            if (pageIndex < 0 || pageIndex >= _doc.PageCount) return;
            if (canvasW <= 0 || canvasH <= 0) return;

            // Remove stale overlays without wiping the entire canvas.
            for (int i = _annotationCanvas.Children.Count - 1; i >= 0; i--)
                if (_annotationCanvas.Children[i] is FrameworkElement fe && fe.Tag as string == FormOverlayTag)
                    _annotationCanvas.Children.RemoveAt(i);

            List<FormFieldInfo> fields;
            try { fields = GetPageFormFields(pageIndex, canvasW, canvasH); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RenderFormFields: {ex}"); return; }
            if (fields.Count == 0) return;

            var greenBrush = BrushResource("AccentGreen");
            var darkBrush  = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
            // Fixed light field appearance: these controls overlay the (white) rendered
            // PDF page and represent document content, so they stay light regardless of
            // the app theme rather than using chrome brushes.
            var fieldBg    = new SolidColorBrush(Color.FromArgb(200, 255, 253, 231));
            var focusBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));

            // Collect radio buttons per group so we can wire mutual exclusion after the loop.
            var radioGroups = new Dictionary<string, List<(Ellipse dot, string onVal)>>();

            bool anyField = false;
            foreach (var f in fields)
            {
                UIElement? ctrl = null;

                // -- Text field ----------------------------------------------------
                if (!f.IsCheckBox && !f.IsRadio && f.FieldType != "/Ch")
                {
                    string cur = _formTextValues.TryGetValue(f.ObjNum, out var tv) ? tv : f.CurrentValue;
                    // Use the shorter canvas dimension as the font size reference so that
                    // rotated fields (where Cw and Ch are swapped vs. portrait) don't blow up.
                    double fieldShort = Math.Min(f.Cw, f.Ch);
                    double fontSize = f.IsMultiLine ? fieldShort * 0.18 : fieldShort * 0.65;
                    fontSize = Math.Max(10, fontSize);
                    var tb = new TextBox
                    {
                        Tag             = FormOverlayTag,
                        Width           = f.Cw,
                        Height          = f.Ch,
                        Text            = cur,
                        IsReadOnly      = f.IsReadOnly,
                        AcceptsReturn   = f.IsMultiLine,
                        TextWrapping    = f.IsMultiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
                        VerticalScrollBarVisibility = f.IsMultiLine ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
                        Background      = fieldBg,
                        Foreground      = Brushes.Black,
                        CaretBrush      = Brushes.Black,
                        BorderBrush     = greenBrush,
                        BorderThickness = new Thickness(1),
                        FontSize        = fontSize,
                        Padding         = new Thickness(3, 0, 3, 0),
                        VerticalContentAlignment = f.IsMultiLine ? VerticalAlignment.Top : VerticalAlignment.Center,
                        ToolTip         = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    tb.GotFocus  += (_, _) => tb.BorderBrush = focusBrush;
                    tb.LostFocus += (_, _) => tb.BorderBrush = greenBrush;
                    int capturedKey = f.ObjNum;
                    tb.TextChanged += (_, _) => { _formTextValues[capturedKey] = tb.Text; MarkDirty(); };
                    ctrl = tb;
                }
                // -- Dropdown / choice --------------------------------------------
                else if (f.FieldType == "/Ch" && f.Options.Count > 0)
                {
                    string cur = _formTextValues.TryGetValue(f.ObjNum, out var tv) ? tv : f.CurrentValue;
                    var combo = new ComboBox
                    {
                        Tag        = FormOverlayTag,
                        Width      = f.Cw,
                        Height     = f.Ch,
                        IsEnabled  = !f.IsReadOnly,
                        Foreground = Brushes.Black,
                        FontSize   = Math.Max(10, Math.Min(f.Cw, f.Ch) * 0.65),
                        ToolTip    = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    foreach (var opt in f.Options) combo.Items.Add(opt);
                    combo.SelectedItem = cur;
                    int capturedKey = f.ObjNum;
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (combo.SelectedItem is string s) { _formTextValues[capturedKey] = s; MarkDirty(); }
                    };
                    ctrl = combo;
                }
                // -- Checkbox ------------------------------------------------------
                else if (f.IsCheckBox)
                {
                    bool isChecked = _formCheckValues.TryGetValue(f.ObjNum, out var cv) ? cv
                        : !string.IsNullOrEmpty(f.CurrentValue)
                          && f.CurrentValue != "/Off" && f.CurrentValue != "Off";

                    // Custom border-based checkbox — WPF's built-in CheckBox indicator
                    // doesn't scale with Width/Height, so we draw it ourselves.
                    double checkFs = Math.Min(f.Cw, f.Ch) * 0.72;
                    var checkMark = new TextBlock
                    {
                        Text       = "✓",
                        FontSize   = checkFs,
                        FontWeight = FontWeights.Bold,
                        Foreground = darkBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed,
                    };
                    var box = new Border
                    {
                        Tag             = FormOverlayTag,
                        Width           = f.Cw,
                        Height          = f.Ch,
                        Background      = fieldBg,
                        BorderBrush     = greenBrush,
                        BorderThickness = new Thickness(1.5),
                        CornerRadius    = new CornerRadius(2),
                        Cursor          = f.IsReadOnly ? Cursors.Arrow : Cursors.Hand,
                        Child           = checkMark,
                        ToolTip         = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    if (!f.IsReadOnly)
                    {
                        int capturedKey = f.ObjNum;
                        box.MouseLeftButtonDown += (_, e) =>
                        {
                            bool now = !(_formCheckValues.TryGetValue(capturedKey, out var v) ? v : isChecked);
                            _formCheckValues[capturedKey] = now;
                            checkMark.Visibility = now ? Visibility.Visible : Visibility.Collapsed;
                            MarkDirty();
                            e.Handled = true;
                        };
                    }
                    ctrl = box;
                }
                // -- Radio button --------------------------------------------------
                else if (f.IsRadio)
                {
                    string groupSelected = _formRadioValues.TryGetValue(f.FieldName, out var rv) ? rv
                        : f.CurrentValue; // CurrentValue = parent /V = currently selected on-value
                    bool isSelected = groupSelected == f.OnValue;

                    double size  = Math.Min(f.Cw, f.Ch) * 0.88;
                    double inner = size * 0.52;

                    var dot = new Ellipse
                    {
                        Width  = inner,
                        Height = inner,
                        Fill   = darkBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed,
                    };
                    var ring = new Ellipse
                    {
                        Width           = size,
                        Height          = size,
                        Stroke          = greenBrush,
                        StrokeThickness = 1.5,
                        Fill            = fieldBg,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                    };
                    var grid = new Grid { Width = f.Cw, Height = f.Ch };
                    grid.Children.Add(ring);
                    grid.Children.Add(dot);

                    var radioBorder = new Border
                    {
                        Tag        = FormOverlayTag,
                        Width      = f.Cw,
                        Height     = f.Ch,
                        Background = Brushes.Transparent,
                        Cursor     = f.IsReadOnly ? Cursors.Arrow : Cursors.Hand,
                        Child      = grid,
                        ToolTip    = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };

                    if (!radioGroups.TryGetValue(f.FieldName, out var groupList))
                        radioGroups[f.FieldName] = groupList = new();
                    groupList.Add((dot, f.OnValue));

                    if (!f.IsReadOnly)
                    {
                        string capturedGroup = f.FieldName;
                        string capturedOn    = f.OnValue;
                        radioBorder.MouseLeftButtonDown += (_, e) =>
                        {
                            _formRadioValues[capturedGroup] = capturedOn;
                            if (radioGroups.TryGetValue(capturedGroup, out var gl))
                                foreach (var (d, ov) in gl)
                                    d.Visibility = ov == capturedOn ? Visibility.Visible : Visibility.Collapsed;
                            MarkDirty();
                            e.Handled = true;
                        };
                    }
                    ctrl = radioBorder;
                }

                if (ctrl is null) continue;
                Canvas.SetLeft(ctrl, f.Cx);
                Canvas.SetTop(ctrl, f.Cy);
                _annotationCanvas.Children.Add(ctrl);
                anyField = true;
            }

            if (anyField)
                SetStatus($"Page {pageIndex + 1} of {_doc.PageCount} - contains fillable form fields");
        }

        /// <summary>
        /// Parses Widget annotations from the given page into field descriptors with canvas
        /// coordinates. Walks the parent chain for each widget to resolve inherited
        /// /FT, /T, /V, /Ff, and /Opt, and maps the widget /Rect (PDF point space,
        /// bottom-left origin, unrotated) to canvas space accounting for page /Rotate —
        /// the same coordinate model as the link overlays.
        /// </summary>
        private List<FormFieldInfo> GetPageFormFields(int pageIndex, int canvasW, int canvasH)
        {
            var result = new List<FormFieldInfo>();
            if (_doc is null || pageIndex < 0 || pageIndex >= _doc.PageCount) return result;

            var page = _doc.Pages[pageIndex];
            // Use the MediaBox directly — PdfSharpCore swaps page.Width/Height for 90/270
            // rotated pages to return visual dimensions, but field /Rect coords are always
            // in the unrotated MediaBox coordinate space.
            var mediaBox = page.MediaBox;
            double pageW = mediaBox.Width  > 0 ? mediaBox.Width  : 595.28;
            double pageH = mediaBox.Height > 0 ? mediaBox.Height : 841.89;
            int rotation = ((page.Rotate % 360) + 360) % 360;

            try
            {
                var annotsArr = page.Elements.GetArray("/Annots");
                if (annotsArr is null || annotsArr.Elements.Count == 0) return result;

                for (int i = 0; i < annotsArr.Elements.Count; i++)
                {
                    PdfItem? elem = annotsArr.Elements[i];
                    PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                    if (ann is null) continue;

                    var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                    if (!subtype.Contains("Widget")) continue;

                    var rectArr = ann.Elements.GetArray("/Rect");
                    if (rectArr is null || rectArr.Elements.Count < 4) continue;
                    double rx1 = rectArr.Elements.GetReal(0);
                    double ry1 = rectArr.Elements.GetReal(1);
                    double rx2 = rectArr.Elements.GetReal(2);
                    double ry2 = rectArr.Elements.GetReal(3);
                    if (rx1 > rx2) (rx1, rx2) = (rx2, rx1);
                    if (ry1 > ry2) (ry1, ry2) = (ry2, ry1);

                    // Map PDF rect (bottom-left origin, unrotated) to canvas coords. The
                    // canvas matches the Docnet-rendered bitmap, which already applied the
                    // page rotation, so we transform accordingly.
                    double cx, cy, cw, ch;
                    switch (rotation)
                    {
                        case 90:  // 90 CW: canvas is pageH-wide x pageW-tall
                            cx = ry1         / pageH * canvasW;
                            cy = rx1         / pageW * canvasH;
                            cw = (ry2 - ry1) / pageH * canvasW;
                            ch = (rx2 - rx1) / pageW * canvasH;
                            break;
                        case 180:
                            cx = (pageW - rx2) / pageW * canvasW;
                            cy = ry1           / pageH * canvasH;
                            cw = (rx2 - rx1)   / pageW * canvasW;
                            ch = (ry2 - ry1)   / pageH * canvasH;
                            break;
                        case 270: // 270 CW: canvas is pageH-wide x pageW-tall
                            cx = (pageH - ry2) / pageH * canvasW;
                            cy = (pageW - rx2) / pageW * canvasH;
                            cw = (ry2 - ry1)   / pageH * canvasW;
                            ch = (rx2 - rx1)   / pageW * canvasH;
                            break;
                        default:  // 0 — standard bottom-left PDF -> top-left canvas
                            cx = rx1           / pageW * canvasW;
                            cy = (pageH - ry2) / pageH * canvasH;
                            cw = (rx2 - rx1)   / pageW * canvasW;
                            ch = (ry2 - ry1)   / pageH * canvasH;
                            break;
                    }
                    if (cw < 2 || ch < 2) continue;

                    // Walk the parent chain to resolve inherited attributes.
                    string ft = "", name = "", curVal = "";
                    int flags = 0;
                    var options = new List<string>();

                    PdfDictionary? node = ann;
                    while (node is not null)
                    {
                        if (string.IsNullOrEmpty(ft) && node.Elements["/FT"] is not null)
                            ft = node.Elements["/FT"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(name) && node.Elements["/T"] is PdfString ts)
                            name = ts.Value;
                        if (string.IsNullOrEmpty(curVal) && node.Elements["/V"] is not null)
                        {
                            var vElem = node.Elements["/V"];
                            curVal = vElem is PdfString vs ? vs.Value : vElem?.ToString() ?? "";
                        }
                        if (flags == 0 && node.Elements["/Ff"] is PdfInteger fi)
                            flags = fi.Value;
                        if (options.Count == 0 && node.Elements.GetArray("/Opt") is PdfArray optArr)
                        {
                            for (int j = 0; j < optArr.Elements.Count; j++)
                            {
                                var o = optArr.Elements[j];
                                if (o is PdfString ps2) options.Add(ps2.Value);
                                else if (o is PdfArray pa2 && pa2.Elements.Count >= 2)
                                    options.Add((pa2.Elements[1] as PdfString)?.Value ?? "");
                            }
                        }

                        var parentItem = node.Elements["/Parent"];
                        if (parentItem is null) break;
                        node = parentItem as PdfDictionary ?? DerefItem(parentItem) as PdfDictionary;
                    }

                    if (string.IsNullOrEmpty(ft)) ft = "/Tx";

                    bool isReadOnly  = (flags & 1) != 0;
                    bool isMultiLine = ft.Contains("Tx") && (flags & 4096) != 0;
                    bool isPushBtn   = ft.Contains("Btn") && (flags & (1 << 16)) != 0;
                    bool isRadio     = ft.Contains("Btn") && !isPushBtn && (flags & (1 << 15)) != 0;
                    bool isCheckBox  = ft.Contains("Btn") && !isPushBtn && !isRadio;

                    // The "on" value for this widget (radio/checkbox selected state) is the
                    // /AP /N key that is not /Off.
                    string onValue = "/Yes";
                    try
                    {
                        var apDict = ann.Elements.GetDictionary("/AP");
                        var nDict  = apDict?.Elements.GetDictionary("/N");
                        if (nDict is not null)
                            foreach (var k in nDict.Elements.Keys)
                                if (k != "/Off") { onValue = k; break; }
                    }
                    catch { }

                    int objNum = GetObjectNumber(elem);
                    if (objNum < 0)
                        objNum = -(pageIndex * 10000 + i); // synthetic key for inline dicts

                    result.Add(new FormFieldInfo(objNum, ft, isCheckBox, isRadio, isMultiLine,
                        name, curVal, onValue, isReadOnly, cx, cy, cw, ch, options));
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GetPageFormFields: {ex}"); }

            return result;
        }

        /// <summary>
        /// Writes all pending form values back into the PDF document's AcroForm field
        /// dictionaries. Called from <see cref="DrawAnnotationsOnDocument"/> just before
        /// saving so values are persisted. Sets /V (and /AS for buttons) and regenerates
        /// /AP /N appearance streams; also sets /NeedAppearances as a fallback.
        /// </summary>
        private void WriteFormValuesToDocument()
        {
            if (_doc is null) return;
            if (_formTextValues.Count == 0 && _formCheckValues.Count == 0 && _formRadioValues.Count == 0) return;

            try
            {
                for (int p = 0; p < _doc.PageCount; p++)
                {
                    var page = _doc.Pages[p];
                    var annotsArr = page.Elements.GetArray("/Annots");
                    if (annotsArr is null) continue;

                    for (int i = 0; i < annotsArr.Elements.Count; i++)
                    {
                        PdfItem? elem = annotsArr.Elements[i];
                        PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                        if (ann is null) continue;

                        var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                        if (!subtype.Contains("Widget")) continue;

                        int objNum = GetObjectNumber(elem);
                        if (objNum < 0) objNum = -(p * 10000 + i);

                        // Walk parent chain to find the canonical field dict (owns /FT).
                        PdfDictionary? fieldDict = ann;
                        PdfDictionary? node = ann;
                        while (node is not null)
                        {
                            if (node.Elements["/FT"] is not null) { fieldDict = node; break; }
                            var pi = node.Elements["/Parent"];
                            if (pi is null) break;
                            node = pi as PdfDictionary ?? DerefItem(pi) as PdfDictionary;
                        }

                        // Field rect for AP stream sizing.
                        var rectArr = ann.Elements.GetArray("/Rect");
                        double fieldW = 100, fieldH = 20;
                        if (rectArr?.Elements.Count >= 4)
                        {
                            double rx1 = rectArr.Elements.GetReal(0), ry1 = rectArr.Elements.GetReal(1);
                            double rx2 = rectArr.Elements.GetReal(2), ry2 = rectArr.Elements.GetReal(3);
                            fieldW = Math.Abs(rx2 - rx1);
                            fieldH = Math.Abs(ry2 - ry1);
                        }

                        // Resolve /DA for font name/size (walk parent chain).
                        string? daStr = null;
                        node = ann;
                        while (node is not null && daStr is null)
                        {
                            if (node.Elements["/DA"] is PdfString ds) daStr = ds.Value;
                            var pi = node.Elements["/Parent"];
                            if (pi is null) break;
                            node = pi as PdfDictionary ?? DerefItem(pi) as PdfDictionary;
                        }

                        if (_formTextValues.TryGetValue(objNum, out var textVal) && fieldDict is not null)
                        {
                            fieldDict.Elements["/V"] = new PdfString(textVal);
                            GenerateTextFieldAppearance(ann, textVal, daStr, fieldW, fieldH);
                        }
                        else if (_formCheckValues.TryGetValue(objNum, out var checkVal) && fieldDict is not null)
                        {
                            string onVal = WidgetOnValue(ann);
                            fieldDict.Elements["/V"]  = new PdfName(checkVal ? onVal : "/Off");
                            fieldDict.Elements["/AS"] = new PdfName(checkVal ? onVal : "/Off");
                            ann.Elements["/AS"]       = new PdfName(checkVal ? onVal : "/Off");
                            GenerateCheckBoxAppearance(ann, checkVal, onVal, fieldW, fieldH);
                        }
                        else if (_formRadioValues.Count > 0 && fieldDict is not null)
                        {
                            string ft2 = fieldDict.Elements["/FT"]?.ToString() ?? "";
                            if (ft2.Contains("Btn"))
                            {
                                // Find /T on the parent field node.
                                string fieldName2 = "";
                                var n2 = fieldDict;
                                while (n2 is not null && string.IsNullOrEmpty(fieldName2))
                                {
                                    if (n2.Elements["/T"] is PdfString ts2) fieldName2 = ts2.Value;
                                    var pi2 = n2.Elements["/Parent"];
                                    if (pi2 is null) break;
                                    n2 = pi2 as PdfDictionary ?? DerefItem(pi2) as PdfDictionary;
                                }
                                if (_formRadioValues.TryGetValue(fieldName2, out var radioSel))
                                {
                                    fieldDict.Elements["/V"] = new PdfName(radioSel);
                                    string onVal2 = WidgetOnValue(ann);
                                    ann.Elements["/AS"] = new PdfName(onVal2 == radioSel ? onVal2 : "/Off");
                                }
                            }
                        }
                    }
                }

                // Belt-and-suspenders: also set NeedAppearances in case any AP generation failed.
                try
                {
                    var acroForm = _doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
                    if (acroForm is not null)
                        acroForm.Elements["/NeedAppearances"] = new PdfBoolean(true);
                }
                catch { }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WriteFormValuesToDocument: {ex}"); }
        }

        /// <summary>Returns the on-state value (/AP /N key that is not /Off) for a button widget.</summary>
        private static string WidgetOnValue(PdfDictionary widgetAnn)
        {
            try
            {
                var apDict = widgetAnn.Elements.GetDictionary("/AP");
                var nDict  = apDict?.Elements.GetDictionary("/N");
                if (nDict is not null)
                    foreach (var k in nDict.Elements.Keys)
                        if (k != "/Off") return k;
            }
            catch { }
            return "/Yes";
        }

        /// <summary>
        /// Generates a /AP /N form XObject appearance stream for a text field and sets it
        /// on the widget annotation, so the typed value shows in other viewers.
        /// </summary>
        private void GenerateTextFieldAppearance(PdfDictionary widgetAnn, string text, string? da, double fieldW, double fieldH)
        {
            try
            {
                var (fontName, fontSize) = ParseDaString(da);
                if (fontSize <= 0) fontSize = Math.Max(6, Math.Min(fieldH * 0.65, 12));
                fontSize = Math.Max(6, Math.Min(fontSize, fieldH * 0.85));

                // PDF baseline is measured from the bottom of the field rect.
                double textY = (fieldH - fontSize) / 2 + fontSize * 0.2;
                if (textY < 1) textY = 1;

                string escaped = EscapePdfString(text);
                string content =
                    $"/Tx BMC\nq\n0 0 {fieldW:F2} {fieldH:F2} re W n\n" +
                    $"BT\n{fontName} {fontSize:F2} Tf\n0 g\n2 {textY:F2} Td\n({escaped}) Tj\nET\nQ\nEMC";

                var xobj = BuildFormXObject(fontName, fieldW, fieldH, content);
                if (xobj is null) return;
                AttachAppearance(widgetAnn, xobj);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GenerateTextFieldAppearance: {ex}"); }
        }

        /// <summary>
        /// Generates /AP /N (checked) and /Off (unchecked) appearance streams for a
        /// checkbox/radio widget and sets them on the annotation. Both states are always
        /// generated; /AS selects the active one.
        /// </summary>
        private void GenerateCheckBoxAppearance(PdfDictionary widgetAnn, bool isChecked, string onVal, double fieldW, double fieldH)
        {
            _ = isChecked; // both AP states always generated; /AS selects the active one
            try
            {
                double m  = Math.Min(fieldW, fieldH) * 0.1;
                double iw = fieldW - m * 2;
                double ih = fieldH - m * 2;

                // Checked: ZapfDingbats "4" = check mark, centred in the field.
                double fs = Math.Min(iw, ih) * 0.85;
                double tx = (fieldW - fs * 0.6) / 2;
                double ty = (fieldH - fs) / 2 + fs * 0.15;

                string checkedContent = $"q\nBT\n/ZaDb {fs:F2} Tf\n0 g\n{tx:F2} {ty:F2} Td\n(4) Tj\nET\nQ";
                string offContent     = "q\nQ"; // empty — just clears

                var checkedXobj = BuildFormXObject("/ZaDb", fieldW, fieldH, checkedContent, isZaDb: true);
                var offXobj     = BuildFormXObject("/ZaDb", fieldW, fieldH, offContent,     isZaDb: true);
                if (checkedXobj is null || offXobj is null) return;

                var nDict = new PdfDictionary(_doc);
                nDict.Elements[onVal]  = checkedXobj.Reference;
                nDict.Elements["/Off"] = offXobj.Reference;

                var apDict = new PdfDictionary(_doc);
                apDict.Elements["/N"] = nDict;
                widgetAnn.Elements["/AP"] = apDict;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GenerateCheckBoxAppearance: {ex}"); }
        }

        /// <summary>
        /// Creates an indirect PdfDictionary stream object representing a Form XObject,
        /// suitable for use as an /AP /N appearance stream.
        /// </summary>
        private PdfDictionary? BuildFormXObject(string fontName, double w, double h, string content, bool isZaDb = false)
        {
            if (_doc is null) return null;

            byte[] bytes = System.Text.Encoding.GetEncoding("iso-8859-1").GetBytes(content);

            var xobj = new PdfDictionary(_doc);
            xobj.Elements["/Type"]     = new PdfName("/XObject");
            xobj.Elements["/Subtype"]  = new PdfName("/Form");
            xobj.Elements["/FormType"] = new PdfInteger(1);

            var bbox = new PdfArray(_doc);
            bbox.Elements.Add(new PdfReal(0));
            bbox.Elements.Add(new PdfReal(0));
            bbox.Elements.Add(new PdfReal(w));
            bbox.Elements.Add(new PdfReal(h));
            xobj.Elements["/BBox"] = bbox;

            // Inline font resource — avoids adding top-level objects for every field.
            var fontEntry = new PdfDictionary(_doc);
            fontEntry.Elements["/Type"]     = new PdfName("/Font");
            fontEntry.Elements["/Subtype"]  = new PdfName("/Type1");
            fontEntry.Elements["/BaseFont"] = isZaDb ? new PdfName("/ZapfDingbats") : new PdfName("/Helvetica");
            if (!isZaDb)
                fontEntry.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");

            var fontDict = new PdfDictionary(_doc);
            fontDict.Elements[fontName] = fontEntry;

            var res = new PdfDictionary(_doc);
            res.Elements["/Font"] = fontDict;
            xobj.Elements["/Resources"] = res;

            if (!TryAttachStreamBytes(xobj, bytes)) return null;

            _doc.Internals.AddObject(xobj);
            return xobj;
        }

        /// <summary>Sets /AP /N on a widget annotation to the given form XObject (indirect ref).</summary>
        private static void AttachAppearance(PdfDictionary widgetAnn, PdfDictionary xobj)
        {
            var apDict = new PdfDictionary();
            apDict.Elements["/N"] = xobj.Reference;
            widgetAnn.Elements["/AP"] = apDict;
        }

        /// <summary>
        /// Attaches raw content bytes to a PdfDictionary as a stream. Accesses
        /// PdfDictionary.PdfStream via reflection because its constructor is internal.
        /// </summary>
        private static bool TryAttachStreamBytes(PdfDictionary dict, byte[] bytes)
        {
            try
            {
                var dictType   = typeof(PdfDictionary);
                var streamType = dictType.GetNestedType("PdfStream",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (streamType is null) return false;

                System.Reflection.ConstructorInfo? ctor =
                    streamType.GetConstructor(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                        null, new[] { typeof(byte[]), typeof(PdfDictionary) }, null) ??
                    streamType.GetConstructor(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                        null, new[] { typeof(byte[]) }, null);
                if (ctor is null) return false;

                object streamObj = ctor.GetParameters().Length == 2
                    ? ctor.Invoke(new object[] { bytes, dict })
                    : ctor.Invoke(new object[] { bytes });

                var prop = dictType.GetProperty("Stream",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (prop?.CanWrite == true)
                {
                    prop.SetValue(dict, streamObj);
                    return true;
                }

                var field = dictType.GetField("_stream",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field is not null)
                {
                    field.SetValue(dict, streamObj);
                    return true;
                }

                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Parses a PDF Default Appearance string ("/Helv 12 Tf 0 g") to extract the font
        /// resource name and point size.
        /// </summary>
        private static (string fontName, double fontSize) ParseDaString(string? da)
        {
            string fontName = "/Helv";
            double fontSize = 0;
            if (string.IsNullOrWhiteSpace(da)) return (fontName, fontSize);

            var tokens = da.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 2 < tokens.Length; i++)
            {
                if (tokens[i + 2] == "Tf" &&
                    double.TryParse(tokens[i + 1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double fs))
                {
                    fontName = tokens[i];
                    fontSize = fs;
                    break;
                }
            }
            return (fontName, fontSize);
        }

        /// <summary>Escapes a string for use in a PDF literal string (parentheses syntax).</summary>
        private static string EscapePdfString(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '(':  sb.Append("\\(");  break;
                    case ')':  sb.Append("\\)");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\n': sb.Append("\\n");  break;
                    default:
                        sb.Append(c < 256 ? c : '?'); // keep Latin-1 range
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Returns true if <paramref name="element"/> is inside a form-field overlay control
        /// (tagged <see cref="FormOverlayTag"/>). Used to let WPF handle mouse events for the
        /// TextBox / checkbox / radio / ComboBox controls natively instead of the canvas tools.
        /// </summary>
        private static bool IsFormFieldElement(DependencyObject? element)
        {
            var current = element;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.Tag as string == FormOverlayTag)
                    return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        // ============================================================
        // Document outline / bookmarks (sidebar OUTLINES tab)
        // ============================================================

        /// <summary>
        /// Walks the document catalog's /Outlines tree into a flat list of
        /// (title, depth, target page) entries and refreshes the sidebar UI.
        /// Destinations are resolved with the same helpers used for link annotations.
        /// </summary>
        private void LoadOutlines()
        {
            var entries = new List<OutlineEntry>();
            try
            {
                if (_doc is not null)
                {
                    var root = _doc.Internals.Catalog.Elements.GetDictionary("/Outlines");
                    var first = DerefItem(root?.Elements["/First"] ?? new PdfInteger(0)) as PdfDictionary;
                    if (first is not null)
                    {
                        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                        AddOutlineNodes(first, 0, entries, visited);
                    }
                }
            }
            catch
            {
                // A malformed outline tree must never break opening a document.
                entries.Clear();
            }
            _ctx.Outline = entries;
            RefreshOutlineUi();
        }

        /// <summary>
        /// Recursively appends an outline node and its siblings (via /Next) and
        /// children (via /First). Guarded against cycles and runaway trees.
        /// </summary>
        private void AddOutlineNodes(PdfDictionary? node, int depth,
            List<OutlineEntry> entries, HashSet<PdfDictionary> visited)
        {
            int guard = 0;
            while (node is not null && guard++ < 10000 && depth < 32)
            {
                if (!visited.Add(node)) break;        // cycle protection
                if (entries.Count >= 10000) break;    // sanity cap

                string title = node.Elements.GetString("/Title") ?? string.Empty;

                // Destination may be a direct /Dest or a /GoTo action's /D.
                PdfItem? destItem = node.Elements["/Dest"];
                if (destItem is null)
                {
                    var action = node.Elements.GetDictionary("/A");
                    if (action is not null &&
                        (action.Elements.GetName("/S") == "/GoTo" || action.Elements.ContainsKey("/D")))
                    {
                        destItem = action.Elements["/D"];
                    }
                }
                int page = ResolveDest(destItem) ?? -1;
                entries.Add(new OutlineEntry(title, depth, page));

                // Descend into children, then continue with the next sibling.
                var child = DerefItem(node.Elements["/First"] ?? new PdfInteger(0)) as PdfDictionary;
                if (child is not null)
                    AddOutlineNodes(child, depth + 1, entries, visited);

                node = DerefItem(node.Elements["/Next"] ?? new PdfInteger(0)) as PdfDictionary;
            }
        }

        /// <summary>Rebuilds the OUTLINES list from the active document's cached outline.</summary>
        private void RefreshOutlineUi()
        {
            _outlineList.Items.Clear();
            var entries = _ctx.Outline;
            bool has = entries is { Count: > 0 };
            _sidebarOutlinesTab.IsEnabled = has;

            if (!has)
            {
                // Don't leave the user stranded on an empty/disabled outline tab.
                if (_sidebarOutlinesTab.IsChecked == true) _sidebarPagesTab.IsChecked = true;
                return;
            }

            foreach (var entry in entries!)
            {
                string text = string.IsNullOrWhiteSpace(entry.Title) ? "(untitled)" : entry.Title;
                var tb = new TextBlock
                {
                    Text = text,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = BrushResource(entry.Page >= 0 ? "TextPrimary" : "TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    Margin = new Thickness(8 + entry.Depth * 14, 0, 4, 0)
                };
                var item = new ListBoxItem
                {
                    Content = tb,
                    Tag = entry.Page,
                    ToolTip = text,
                    IsEnabled = entry.Page >= 0
                };
                _outlineList.Items.Add(item);
            }
        }

        private void OutlineList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_outlineList.SelectedItem is ListBoxItem { Tag: int page }
                && page >= 0 && _doc is not null && page < _doc.PageCount
                && PageList.SelectedIndex != page)
            {
                PageList.SelectedIndex = page;
            }
        }

        private void SidebarPagesTab_Checked(object sender, RoutedEventArgs e)
        {
            // Fires during XAML load before manual refs are assigned — guard.
            if (_outlineScrollViewer is null) return;
            _outlineScrollViewer.Visibility = Visibility.Collapsed;
            SidebarScrollViewer.Visibility = Visibility.Visible;
            _pageControlsRow.Visibility = Visibility.Visible;
        }

        private void SidebarOutlinesTab_Checked(object sender, RoutedEventArgs e)
        {
            if (_outlineScrollViewer is null) return;
            SidebarScrollViewer.Visibility = Visibility.Collapsed;
            _outlineScrollViewer.Visibility = Visibility.Visible;
            _pageControlsRow.Visibility = Visibility.Collapsed;
        }

        // ============================================================
        // Tool selection
        // ============================================================

        private void SetTool(EditTool tool)
        {
            Telemetry.TrackEvent("Tool.Selected", new Dictionary<string, string> { ["Tool"] = tool.ToString() });
            CommitActiveTextBox();
            ClearTextSelection();
            CancelActivePointerOperation(removePreview: true);
            _currentTool = tool;
            // Tool bars edit tool defaults, not a selected annotation.
            _styleTarget = null;

            var map = new (Button btn, EditTool t)[]
            {
                (_toolSelectBtn, EditTool.Select),
                (_toolTextBtn, EditTool.Text),
                (_toolEditTextBtn, EditTool.EditText),
                (_toolEditImageBtn, EditTool.EditImage),
                (_toolHighlightBtn, EditTool.Highlight),
                (_toolDrawBtn, EditTool.Draw),
                (_toolSignatureBtn, EditTool.Signature),
                (_toolImageBtn, EditTool.Image),
                (_toolCropBtn, EditTool.Crop),
                (_toolPanBtn, EditTool.Pan),
                (_toolEraseBtn, EditTool.Erase),
                (_toolShapeBtn, EditTool.Shape)
            };
            var green = (SolidColorBrush)FindResource("AccentGreen");
            var greenDim = (SolidColorBrush)FindResource("AccentGreenDim");
            var text = (SolidColorBrush)FindResource("TextPrimary");

            foreach (var (btn, t) in map)
            {
                btn.Background = t == tool ? greenDim : Brushes.Transparent;
                btn.Foreground = t == tool ? green : text;
            }

            _annotationCanvas.Cursor = tool switch
            {
                EditTool.Text => Cursors.IBeam,
                EditTool.EditText => Cursors.IBeam,
                EditTool.EditImage => Cursors.Hand,
                EditTool.Highlight => Cursors.Cross,
                EditTool.Draw => Cursors.Pen,
                EditTool.Signature => Cursors.Hand,
                EditTool.Image => Cursors.Hand,
                EditTool.Crop => Cursors.Cross,
                EditTool.Pan => Cursors.Hand,
                EditTool.Erase => Cursors.Cross,
                EditTool.Shape => Cursors.Cross,
                _ => Cursors.Arrow
            };

            // Show/hide draw settings bar
            if (tool == EditTool.Draw || tool == EditTool.Highlight)
                ShowDrawSettings(tool);
            else
                HideDrawSettings();

            // Show/hide text tool settings bar
            if (tool == EditTool.Text)
                ShowTextSettings();
            else
                HideTextSettings();

            // Show/hide shape tool settings bar
            if (tool == EditTool.Shape)
                ShowShapeSettings();
            else
                HideShapeSettings();

            // Hide signature popup when switching away
            if (tool != EditTool.Signature)
            {
                HideSignaturePopup();
                _pendingSignature = null;
            }

            if (tool != EditTool.Crop)
            {
                HideCropPopup();
                ClearCropSelection();
                // Dismiss crop confirm bar when switching away from Crop
                HideCropConfirmBar();
            }
        }

        /// <summary>
        /// Reset any active pointer-driven canvas operation (drawing, selecting, dragging,
        /// resizing, panning). Used when switching tools, before delete/erase, or when
        /// capture is forcibly lost. <paramref name="removePreview"/> also tears down any
        /// transient preview visuals (rubber-band rect, in-progress ink polyline, etc.).
        /// </summary>
        private void CancelActivePointerOperation(bool removePreview)
        {
            if (_annotationCanvas?.IsMouseCaptured == true)
                _annotationCanvas.ReleaseMouseCapture();

            _isDrawing = false;
            _isSelecting = false;
            _isDraggingAnnot = false;
            _isResizingSig = false;
            _isResizingImage = false;
            _isMovingAnnot = false;
            _isResizingAnnot = false;
            _isPanning = false;
            _panButton = null;

            _activeInk = null;
            _resizeSigAnnot = null;
            _dragAnnot = null;
            _resizingImageEdit = null;
            _movingAnnot = null;
            _resizingAnnot = null;
            _moveOriginalGeom = null;
            _resizeOriginalGeom = null;

            if (removePreview)
            {
                if (_activePreview is not null && _annotationCanvas?.Children.Contains(_activePreview) == true)
                    _annotationCanvas.Children.Remove(_activePreview);
                if (_selectRect is not null && _annotationCanvas?.Children.Contains(_selectRect) == true)
                    _annotationCanvas.Children.Remove(_selectRect);
                _activePreview = null;
                _selectRect = null;
            }

            if (_cursorBeforePan != null && _annotationCanvas != null)
            {
                _annotationCanvas.Cursor = _cursorBeforePan;
                _cursorBeforePan = null;
            }
        }

        private bool IsPointerOperationActive =>
            _isDrawing || _isSelecting || _isDraggingAnnot || _isResizingSig ||
            _isResizingImage || _isMovingAnnot || _isResizingAnnot || _isPanning;

        private void SidebarToggle_Click(object sender, RoutedEventArgs e)
        {
            _sidebarCollapsed = !_sidebarCollapsed;
            if (_sidebarCollapsed)
            {
                _sidebarBorder.Visibility = Visibility.Collapsed;
                _sidebarCol.Width = new GridLength(24);
                _sidebarCol.MinWidth = 24;
                _sidebarToggleBtn.Content = "\uE76C"; // ChevronRight (Segoe MDL2)
                _sidebarToggleBtn.ToolTip = "Expand sidebar";
            }
            else
            {
                _sidebarBorder.Visibility = Visibility.Visible;
                _sidebarCol.Width = new GridLength(180);
                _sidebarCol.MinWidth = 24;
                _sidebarToggleBtn.Content = "\uE76B"; // ChevronLeft (Segoe MDL2)
                _sidebarToggleBtn.ToolTip = "Collapse sidebar";
            }
            if (PageList.SelectedIndex >= 0)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => RefreshPageView(PageList.SelectedIndex));
        }

        private void ToolSelect_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Select);
        private void ToolText_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Text);
        private void ToolEditText_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.EditText);
        private void ToolEditImage_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.EditImage);
        private void ToolHighlight_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Highlight);
        private void ToolDraw_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Draw);
        private void ToolImage_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Image);
        private void ToolPan_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Pan);
        private void ToolErase_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Erase);
        private void ToolShape_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Shape);
        private void ToolCrop_Click(object sender, RoutedEventArgs e)
        {
            SetTool(EditTool.Crop);
            ShowCropPopup();
        }
        private void ToolSignature_Click(object sender, RoutedEventArgs e)
        {
            if (_signaturePopup is not null)
            {
                HideSignaturePopup();
                if (_currentTool == EditTool.Signature && _pendingSignature is null)
                    SetTool(EditTool.Select);
                return;
            }
            SetTool(EditTool.Signature);
            ShowSignaturePopup();
        }

        // ============================================================
        // Crop tool
        // ============================================================

        private void ShowCropConfirmBar()
        {
            HideCropConfirmBar();
            if (_doc is null) return;

            int currentPage = PageList.SelectedIndex;
            bool multiPage = _doc.PageCount > 1;

            var bar = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 30, 30, 30)),
                BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6)
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            var label = new TextBlock
            {
                Text = "Apply crop to:",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            panel.Children.Add(label);

            var btnStyle = new Style(typeof(Button));
            btnStyle.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromArgb(40, 74, 222, 128))));
            btnStyle.Setters.Add(new Setter(Button.ForegroundProperty, (SolidColorBrush)FindResource("AccentGreen")));
            btnStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
            btnStyle.Setters.Add(new Setter(Button.BorderBrushProperty, (SolidColorBrush)FindResource("AccentGreen")));
            btnStyle.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(10, 4, 10, 4)));
            btnStyle.Setters.Add(new Setter(Button.MarginProperty, new Thickness(0, 0, 6, 0)));
            btnStyle.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
            btnStyle.Setters.Add(new Setter(Button.FontFamilyProperty, new FontFamily("Segoe UI")));
            btnStyle.Setters.Add(new Setter(Button.FontSizeProperty, 12.0));

            var thisPageBtn = new Button { Content = "This Page", Style = btnStyle, ToolTip = "Crop this page (Enter)" };
            thisPageBtn.Click += (_, _) => ApplyCrop([currentPage]);
            panel.Children.Add(thisPageBtn);

            if (multiPage)
            {
                var allPagesBtn = new Button { Content = "All Pages", Style = btnStyle, ToolTip = "Crop all pages" };
                allPagesBtn.Click += (_, _) => ApplyCrop([..Enumerable.Range(0, _doc.PageCount)]);
                panel.Children.Add(allPagesBtn);
            }

            // "Remove Crop" — only shown if current page already has a CropBox
            bool hasCropBox = _doc.Pages[currentPage].Elements.ContainsKey("/CropBox");
            if (hasCropBox)
            {
                var dimBtnStyle = new Style(typeof(Button), btnStyle);
                dimBtnStyle.Setters.Add(new Setter(Button.ForegroundProperty,
                    new SolidColorBrush(Color.FromRgb(0xff, 0x80, 0x80))));
                dimBtnStyle.Setters.Add(new Setter(Button.BorderBrushProperty,
                    new SolidColorBrush(Color.FromRgb(0xff, 0x80, 0x80))));

                var removeBtn = new Button
                {
                    Content = "Remove Crop",
                    Style = dimBtnStyle,
                    ToolTip = multiPage ? "Remove CropBox from this page" : "Remove existing CropBox"
                };
                removeBtn.Click += (_, _) => RemoveCropBox([currentPage]);
                panel.Children.Add(removeBtn);

                if (multiPage)
                {
                    var removeAllBtn = new Button
                    {
                        Content = "Remove All",
                        Style = dimBtnStyle,
                        ToolTip = "Remove CropBox from all pages"
                    };
                    removeAllBtn.Click += (_, _) => RemoveCropBox([..Enumerable.Range(0, _doc.PageCount)]);
                    panel.Children.Add(removeAllBtn);
                }
            }

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Style = btnStyle,
                ToolTip = "Cancel (Escape)",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                BorderBrush = (SolidColorBrush)FindResource("TextSecondary"),
                Background = Brushes.Transparent
            };
            cancelBtn.Click += (_, _) => HideCropConfirmBar();
            panel.Children.Add(cancelBtn);

            bar.Child = panel;
            _cropConfirmBar = bar;

            // Position below the crop rect; if near the bottom, flip above it instead.
            const double barHeight = 38; // approximate height of the confirm bar
            double barLeft = Math.Max(4, _cropCanvasRect.X);
            double barTopBelow = _cropCanvasRect.Y + _cropCanvasRect.Height + 8;
            double barTopAbove = _cropCanvasRect.Y - barHeight - 8;
            double barTop = barTopBelow + barHeight < _annotationCanvas.ActualHeight
                ? barTopBelow
                : Math.Max(4, barTopAbove);
            Canvas.SetLeft(bar, barLeft);
            Canvas.SetTop(bar, barTop);
            _annotationCanvas.Children.Add(bar);
            AddCropHandles();
        }

        private void HideCropConfirmBar()
        {
            if (_cropConfirmBar is not null)
            {
                _annotationCanvas.Children.Remove(_cropConfirmBar);
                _cropConfirmBar = null;
            }
            if (_cropPreviewRect is not null)
            {
                _annotationCanvas.Children.Remove(_cropPreviewRect);
                _cropPreviewRect = null;
            }
            RemoveCropHandles();
        }

        private void AddCropHandles()
        {
            RemoveCropHandles();
            const double hSize = 8;
            var tags = new[] { "NW", "NE", "SE", "SW" };
            var cursors = new[] { Cursors.SizeNWSE, Cursors.SizeNESW, Cursors.SizeNWSE, Cursors.SizeNESW };
            var green = (SolidColorBrush)FindResource("AccentGreen");

            for (int i = 0; i < 4; i++)
            {
                var h = new Rectangle
                {
                    Width = hSize,
                    Height = hSize,
                    Fill = green,
                    Stroke = Brushes.White,
                    StrokeThickness = 1,
                    Tag = tags[i],
                    Cursor = cursors[i],
                };
                _cropHandles.Add(h);
                _annotationCanvas.Children.Add(h);
            }
            PositionCropHandles();
        }

        private void RemoveCropHandles()
        {
            foreach (var h in _cropHandles)
                _annotationCanvas.Children.Remove(h);
            _cropHandles.Clear();
            _activeCropHandleTag = null;
        }

        private void PositionCropHandles()
        {
            if (_cropHandles.Count < 4) return;
            const double hSize = 8;
            var corners = new (double x, double y)[]
            {
                (_cropCanvasRect.X - hSize / 2,     _cropCanvasRect.Y - hSize / 2),
                (_cropCanvasRect.Right - hSize / 2, _cropCanvasRect.Y - hSize / 2),
                (_cropCanvasRect.Right - hSize / 2, _cropCanvasRect.Bottom - hSize / 2),
                (_cropCanvasRect.X - hSize / 2,     _cropCanvasRect.Bottom - hSize / 2),
            };
            for (int i = 0; i < 4; i++)
            {
                Canvas.SetLeft(_cropHandles[i], corners[i].x);
                Canvas.SetTop(_cropHandles[i], corners[i].y);
            }
        }

        private void UpdateCropRectVisuals()
        {
            if (_cropPreviewRect is null) return;
            Canvas.SetLeft(_cropPreviewRect, _cropCanvasRect.X);
            Canvas.SetTop(_cropPreviewRect, _cropCanvasRect.Y);
            _cropPreviewRect.Width = _cropCanvasRect.Width;
            _cropPreviewRect.Height = _cropCanvasRect.Height;
            PositionCropHandles();
            RepositionCropConfirmBar();
        }

        private void RepositionCropConfirmBar()
        {
            if (_cropConfirmBar is null) return;
            const double barHeight = 38;
            double barLeft = Math.Max(4, _cropCanvasRect.X);
            double barTopBelow = _cropCanvasRect.Y + _cropCanvasRect.Height + 8;
            double barTopAbove = _cropCanvasRect.Y - barHeight - 8;
            double barTop = barTopBelow + barHeight < _annotationCanvas.ActualHeight
                ? barTopBelow : Math.Max(4, barTopAbove);
            Canvas.SetLeft(_cropConfirmBar, barLeft);
            Canvas.SetTop(_cropConfirmBar, barTop);
        }

        private void RemoveCropBox(int[] pageIndices)
        {
            if (_doc is null || _currentFile is null) return;
            try
            {
                PushDocUndo();
                foreach (int pi in pageIndices)
                {
                    if (pi < 0 || pi >= _doc.PageCount) continue;
                    _doc.Pages[pi].Elements.Remove("/CropBox");
                }
                HideCropConfirmBar();
                SetTool(EditTool.Select);
                SaveTempAndReload();
                SetStatus($"Removed CropBox from {pageIndices.Length} page{(pageIndices.Length == 1 ? "" : "s")}");
            }
            catch (Exception ex)
            {
                SetStatus($"Remove crop failed: {ex.Message}");
            }
        }

        private void ApplyCrop(int[] pageIndices)
        {
            if (_doc is null || _currentFile is null) { SetStatus("Crop: no document open"); return; }
            int currentPage = PageList.SelectedIndex;
            if (currentPage < 0) { SetStatus("Crop: no page selected"); return; }
            if (!_renderDims.ContainsKey(currentPage)) { SetStatus("Crop: page dimensions unavailable"); return; }

            try
            {
                PushDocUndo();

                var (renderW, renderH) = _renderDims[currentPage];
                var cr = _cropCanvasRect;

                foreach (int pi in pageIndices)
                {
                    if (pi < 0 || pi >= _doc.PageCount) continue;
                    var page = _doc.Pages[pi];
                    double pdfW = page.Width.Point;
                    double pdfH = page.Height.Point;

                    // Convert canvas rect (top-left origin) to PDF rect (bottom-left origin, points)
                    double x1 = cr.X * pdfW / renderW;
                    double y1 = pdfH - (cr.Y + cr.Height) * pdfH / renderH;
                    double x2 = (cr.X + cr.Width) * pdfW / renderW;
                    double y2 = pdfH - cr.Y * pdfH / renderH;

                    // Clamp to media box
                    x1 = Math.Max(0, x1); y1 = Math.Max(0, y1);
                    x2 = Math.Min(pdfW, x2); y2 = Math.Min(pdfH, y2);

                    // Write CropBox directly into the page dictionary — more reliable than the
                    // CropBox property setter across PdfSharpCore versions.
                    var arr = new PdfSharpCore.Pdf.PdfArray();
                    arr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(x1));
                    arr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(y1));
                    arr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(x2));
                    arr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(y2));
                    page.Elements["/CropBox"] = arr;
                }

                HideCropConfirmBar();
                SetTool(EditTool.Select);
                SaveTempAndReload();
                SetStatus($"Cropped {pageIndices.Length} page{(pageIndices.Length == 1 ? "" : "s")}");
            }
            catch (Exception ex)
            {
                SetStatus($"Crop failed: {ex.Message}");
            }
        }

        // ============================================================
        // Draw/Highlight settings bar
        // ============================================================

        private static readonly Color[] SwatchColors =
        [
            Colors.Red, Colors.SaddleBrown, Colors.Orange, Colors.Gold,
            Colors.LimeGreen, Colors.DodgerBlue, Colors.MediumPurple,
            Colors.DeepPink, Colors.White, Colors.Black
        ];

        // ── Custom color picker plumbing (shared by every annotation swatch row) ──

        private const int MaxRecentColors = 8;

        // The recently picked custom colors, most-recent first, parsed from the persisted setting.
        private static List<Color> LoadCustomColors()
        {
            var raw = TDPdf.Properties.Settings.Default.CustomColors;
            var list = new List<Color>();
            if (string.IsNullOrWhiteSpace(raw)) return list;
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (TdpColorPicker.TryParseHex(part.Trim(), out Color c))
                    list.Add(c);
            return list;
        }

        // Record a freshly chosen color at the head of the recent list (RGB-deduped, capped) and persist.
        private static void RememberCustomColor(Color c)
        {
            var opaque = Color.FromRgb(c.R, c.G, c.B);
            var list = LoadCustomColors();
            list.RemoveAll(x => x.R == opaque.R && x.G == opaque.G && x.B == opaque.B);
            list.Insert(0, opaque);
            if (list.Count > MaxRecentColors) list.RemoveRange(MaxRecentColors, list.Count - MaxRecentColors);
            TDPdf.Properties.Settings.Default.CustomColors =
                string.Join(",", list.Select(x => $"#{x.R:X2}{x.G:X2}{x.B:X2}"));
            try { TDPdf.Properties.Settings.Default.Save(); } catch { /* persistence is best-effort */ }
        }

        // Open the themed RGB picker seeded with <paramref name="initial"/>. On OK the opaque pick is
        // returned and pushed onto the recent-colors palette; on Cancel returns false.
        private bool TryPickCustomColor(Color initial, out Color picked)
        {
            if (TdpColorPicker.TryPickColor(this, initial, LoadCustomColors(), out picked))
            {
                RememberCustomColor(picked);
                return true;
            }
            return false;
        }

        // A trailing "custom color" swatch for an annotation settings bar: a rainbow "+" tile that opens
        // the picker (seeded with <paramref name="current"/>) and hands the pick to <paramref name="apply"/>
        // exactly as clicking a fixed swatch would set the row's active color.
        private Border MakeCustomColorSwatch(Color current, Action<Color> apply)
        {
            var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            Color[] stops =
            [
                Color.FromRgb(0xFF, 0x00, 0x00), Color.FromRgb(0xFF, 0x7F, 0x00), Color.FromRgb(0xFF, 0xFF, 0x00),
                Color.FromRgb(0x00, 0xC8, 0x00), Color.FromRgb(0x00, 0xC8, 0xFF), Color.FromRgb(0x00, 0x40, 0xFF),
                Color.FromRgb(0x9C, 0x00, 0xFF)
            ];
            for (int i = 0; i < stops.Length; i++)
                rainbow.GradientStops.Add(new GradientStop(stops[i], i / (double)(stops.Length - 1)));

            var plus = new TextBlock
            {
                Text = "", // Segoe MDL2 "Add"
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 9,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, BlurRadius = 2, ShadowDepth = 0, Opacity = 0.9 }
            };

            var swatch = new Border
            {
                Width = 18, Height = 18,
                Background = rainbow,
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Custom color…",
                Child = plus
            };
            swatch.MouseLeftButtonDown += (_, _) =>
            {
                if (TryPickCustomColor(current, out var pickedColor))
                    apply(pickedColor);
            };
            return swatch;
        }

        private void ShowDrawSettings(EditTool tool)
        {
            if (_drawSettingsBar is not null)
            {
                var previewGrid = PagePreviewPanel.Parent as Grid;
                previewGrid?.Children.Remove(_drawSettingsBar);
                _drawSettingsBar = null;
            }

            // Restyle a selected ink/highlight, or edit tool defaults when nothing is selected.
            var inkTarget = _styleTarget as InkAnnotation;
            var hlTarget = _styleTarget as HighlightAnnotation;

            void ApplyColor(Color c)
            {
                if (inkTarget is not null) { inkTarget.SetColor(Color.FromArgb(inkTarget.ColorA, c.R, c.G, c.B)); RestyleReselect(inkTarget); return; }
                if (hlTarget is not null) { hlTarget.SetColor(Color.FromArgb(hlTarget.ColorA, c.R, c.G, c.B)); RestyleReselect(hlTarget); return; }
                if (tool == EditTool.Draw) _drawColor = Color.FromArgb(_drawOpacity, c.R, c.G, c.B);
                else _highlightColor = Color.FromArgb(_highlightColor.A, c.R, c.G, c.B);
                ShowDrawSettings(tool);
            }
            void ApplyOpacity(byte a)
            {
                if (inkTarget is not null) { inkTarget.ColorA = a; RestyleLive(inkTarget); return; }
                if (hlTarget is not null) { hlTarget.ColorA = a; RestyleLive(hlTarget); return; }
                if (tool == EditTool.Draw) { _drawOpacity = a; _drawColor = Color.FromArgb(a, _drawColor.R, _drawColor.G, _drawColor.B); }
                else _highlightColor = Color.FromArgb(a, _highlightColor.R, _highlightColor.G, _highlightColor.B);
            }
            void ApplyDrawWidth(double w)
            {
                if (inkTarget is not null) { inkTarget.StrokeWidth = w; RestyleLive(inkTarget); return; }
                _drawWidth = w;
            }

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };

            // Color label
            panel.Children.Add(new TextBlock
            {
                Text = "Color:",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });

            // Color swatches
            Color activeColor =
                inkTarget is not null ? Color.FromRgb(inkTarget.ColorR, inkTarget.ColorG, inkTarget.ColorB) :
                hlTarget is not null ? Color.FromRgb(hlTarget.ColorR, hlTarget.ColorG, hlTarget.ColorB) :
                tool == EditTool.Draw ? _drawColor : Color.FromRgb(_highlightColor.R, _highlightColor.G, _highlightColor.B);
            foreach (var color in SwatchColors)
            {
                var swatch = new Border
                {
                    Width = 18, Height = 18,
                    Background = FrozenSolidColorBrush(color),
                    BorderBrush = color == activeColor
                        ? (SolidColorBrush)FindResource("AccentGreen")
                        : BrushResource("BorderDim"),
                    BorderThickness = new Thickness(color == activeColor ? 2 : 1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Tag = color
                };
                swatch.MouseLeftButtonDown += (s, e) => ApplyColor((Color)((Border)s!).Tag);
                panel.Children.Add(swatch);
            }

            // Custom color: opens the full RGB picker, applied like a fixed swatch (opacity preserved).
            panel.Children.Add(MakeCustomColorSwatch(activeColor, ApplyColor));

            // Separator
            panel.Children.Add(new Rectangle
            {
                Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(8, 2, 8, 2)
            });

            // Size slider (draw only)
            if (tool == EditTool.Draw)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Size:",
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                });
                double curDrawWidth = inkTarget?.StrokeWidth ?? _drawWidth;
                var sizeSlider = new Slider
                {
                    Minimum = 1, Maximum = 20, Value = curDrawWidth,
                    Width = 80, VerticalAlignment = VerticalAlignment.Center,
                    TickFrequency = 1, IsSnapToTickEnabled = true
                };
                var sizeLabel = new TextBlock
                {
                    Text = $"{curDrawWidth:F0}px",
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0)
                };
                sizeSlider.ValueChanged += (s, e) => { sizeLabel.Text = $"{e.NewValue:F0}px"; ApplyDrawWidth(e.NewValue); };
                panel.Children.Add(sizeSlider);
                panel.Children.Add(sizeLabel);

                // Separator
                panel.Children.Add(new Rectangle
                {
                    Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                    Margin = new Thickness(8, 2, 8, 2)
                });
            }

            // Opacity slider
            panel.Children.Add(new TextBlock
            {
                Text = "Opacity:",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });
            byte currentOpacity = inkTarget?.ColorA ?? hlTarget?.ColorA ?? (tool == EditTool.Draw ? _drawOpacity : _highlightColor.A);
            var opacitySlider = new Slider
            {
                Minimum = 10, Maximum = 255, Value = currentOpacity,
                Width = 80, VerticalAlignment = VerticalAlignment.Center
            };
            var opacityLabel = new TextBlock
            {
                Text = $"{(int)(currentOpacity / 255.0 * 100)}%",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0)
            };
            opacitySlider.ValueChanged += (s, e) =>
            {
                byte a = (byte)e.NewValue;
                opacityLabel.Text = $"{(int)(a / 255.0 * 100)}%";
                ApplyOpacity(a);
            };
            panel.Children.Add(opacitySlider);
            panel.Children.Add(opacityLabel);

            _drawSettingsBar = new Border
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

            var previewArea = PagePreviewPanel.Parent as Grid;
            if (previewArea is not null)
            {
                Panel.SetZIndex(_drawSettingsBar, 100);
                previewArea.Children.Add(_drawSettingsBar);
            }
        }

        private void HideDrawSettings()
        {
            if (_drawSettingsBar is not null)
            {
                var previewGrid = PagePreviewPanel.Parent as Grid;
                previewGrid?.Children.Remove(_drawSettingsBar);
                _drawSettingsBar = null;
            }
        }

        // ============================================================
        // Crop settings bar
        // ============================================================

        private void ShowCropPopup()
        {
            if (_cropPopup is not null)
            {
                _cropPopup.Visibility = Visibility.Visible;
                return;
            }

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };
            panel.Children.Add(new TextBlock
            {
                Text = "Drag a crop rectangle",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });

            _cropApplyAllCheck = new CheckBox
            {
                Content = "Apply to all pages",
                Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            panel.Children.Add(_cropApplyAllCheck);

            var applyBtn = new Button
            {
                Content = "Apply crop",
                Style = (Style)FindResource("ToolbarButtonAccent"),
                ToolTip = "Apply the selected crop rectangle"
            };
            applyBtn.Click += ApplyCrop_Click;
            panel.Children.Add(applyBtn);

            var resetBtn = new Button
            {
                Content = "Reset",
                Style = (Style)FindResource("ToolbarButton"),
                ToolTip = "Clear the current crop rectangle"
            };
            resetBtn.Click += (s, e) => ClearCropSelection();
            panel.Children.Add(resetBtn);

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Style = (Style)FindResource("ToolbarButton"),
                ToolTip = "Cancel cropping"
            };
            cancelBtn.Click += (s, e) => SetTool(EditTool.Select);
            panel.Children.Add(cancelBtn);

            _cropPopup = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Child = panel,
                Margin = new Thickness(8, 0, 0, 0)
            };

            var previewArea = PagePreviewPanel.Parent as Grid;
            if (previewArea is not null)
            {
                Panel.SetZIndex(_cropPopup, 101);
                previewArea.Children.Add(_cropPopup);
            }
        }

        private void HideCropPopup()
        {
            if (_cropPopup is not null)
                _cropPopup.Visibility = Visibility.Collapsed;
        }

        // ============================================================
        // Text tool settings bar
        // ============================================================

        private static readonly double[] TextFontSizes = [8, 10, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 64, 72];

        private void ShowTextSettings()
        {
            HideTextSettings();

            // When a text annotation is selected, the bar restyles THAT box; otherwise it sets tool defaults.
            var target = _styleTarget as TextAnnotation;
            double curSize = target?.FontSize ?? _textFontSize;
            Color curColor = target?.GetColor() ?? _textColor;
            bool curFill = target?.HasFill ?? _textWhiteout;
            Color curFillColor = target is { HasFill: true } ? target.GetFillColor() : _textFillColor;

            void ApplySize(double v)
            {
                _textFontSize = v;
                if (target is not null) { target.FontSize = v; RestyleLive(target); }
            }
            void ApplyColor(Color c)
            {
                _textColor = c;
                if (target is not null) { target.SetColor(c); RestyleReselect(target); }
                else ShowTextSettings();
            }
            void ApplyFill(bool on)
            {
                _textWhiteout = on;
                if (target is not null)
                {
                    target.HasFill = on;
                    if (on) target.SetFillColor(_textFillColor);
                    RestyleReselect(target);
                }
                else { UpdateActiveTextBoxFill(); ShowTextSettings(); }
            }
            void ApplyFillColor(Color c)
            {
                _textFillColor = c;
                if (target is not null) { target.HasFill = true; target.SetFillColor(c); RestyleReselect(target); }
                else { _textWhiteout = true; UpdateActiveTextBoxFill(); ShowTextSettings(); }
            }

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };

            // Font size label
            panel.Children.Add(new TextBlock
            {
                Text = "Size:",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });

            // Font size dropdown
            var sizeBox = new ComboBox
            {
                Width = 64, Height = 24,
                Style = (Style)FindResource("DarkComboBox"),
                IsEditable = true,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            foreach (var size in TextFontSizes)
                sizeBox.Items.Add(size.ToString("0"));
            sizeBox.Text = curSize.ToString("0");
            sizeBox.SelectionChanged += (_, _) =>
            {
                if (sizeBox.SelectedItem is string s && double.TryParse(s, out double v) && v > 0)
                    ApplySize(v);
            };
            sizeBox.LostFocus += (_, _) =>
            {
                if (double.TryParse(sizeBox.Text, out double v) && v > 0)
                    ApplySize(v);
            };
            panel.Children.Add(sizeBox);

            // Separator
            panel.Children.Add(new Rectangle
            {
                Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(8, 2, 8, 2)
            });

            // Color label
            panel.Children.Add(new TextBlock
            {
                Text = "Color:",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });

            // Color swatches (reuse same palette as draw tool)
            foreach (var color in SwatchColors)
            {
                var c = color;
                var swatch = new Border
                {
                    Width = 18, Height = 18,
                    Background = new SolidColorBrush(c),
                    BorderBrush = (c.R == curColor.R && c.G == curColor.G && c.B == curColor.B)
                        ? (SolidColorBrush)FindResource("AccentGreen")
                        : new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                    BorderThickness = new Thickness(
                        (c.R == curColor.R && c.G == curColor.G && c.B == curColor.B) ? 2 : 1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(1),
                    Cursor = Cursors.Hand
                };
                swatch.MouseLeftButtonDown += (_, _) => ApplyColor(c);
                panel.Children.Add(swatch);
            }

            // Custom color: opens the full RGB picker, applied like a fixed text-color swatch.
            panel.Children.Add(MakeCustomColorSwatch(
                Color.FromRgb(curColor.R, curColor.G, curColor.B), ApplyColor));

            // Separator
            panel.Children.Add(new Rectangle
            {
                Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(8, 2, 8, 2)
            });

            // Whiteout fill toggle (+ fill color swatches when on).
            panel.Children.Add(MakeLabel("Fill:"));
            var fillToggle = new CheckBox
            {
                Content = "On",
                IsChecked = curFill,
                Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Paint an opaque background behind the text (whiteout)"
            };
            fillToggle.Checked += (_, _) => ApplyFill(true);
            fillToggle.Unchecked += (_, _) => ApplyFill(false);
            panel.Children.Add(fillToggle);

            if (curFill)
            {
                foreach (var color in SwatchColors)
                {
                    var c = color;
                    bool selected = c.R == curFillColor.R && c.G == curFillColor.G && c.B == curFillColor.B;
                    var swatch = new Border
                    {
                        Width = 18, Height = 18,
                        Background = new SolidColorBrush(c),
                        BorderBrush = selected
                            ? (SolidColorBrush)FindResource("AccentGreen")
                            : new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                        BorderThickness = new Thickness(selected ? 2 : 1),
                        CornerRadius = new CornerRadius(3),
                        Margin = new Thickness(1),
                        Cursor = Cursors.Hand
                    };
                    swatch.MouseLeftButtonDown += (_, _) => ApplyFillColor(c);
                    panel.Children.Add(swatch);
                }
                panel.Children.Add(MakeCustomColorSwatch(
                    Color.FromRgb(curFillColor.R, curFillColor.G, curFillColor.B), ApplyFillColor));
            }

            _textSettingsBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Child = panel,
                Margin = new Thickness(8, 0, 0, 0)
            };

            var previewArea = PagePreviewPanel.Parent as Grid;
            if (previewArea is not null)
            {
                Panel.SetZIndex(_textSettingsBar, 100);
                previewArea.Children.Add(_textSettingsBar);
            }
        }

        private void HideTextSettings()
        {
            if (_textSettingsBar is not null)
            {
                (PagePreviewPanel.Parent as Grid)?.Children.Remove(_textSettingsBar);
                _textSettingsBar = null;
            }
        }

        // ============================================================
        // Shape tool settings bar
        // ============================================================

        private static readonly Color[] ShapeStrokeColors =
        {
            Color.FromRgb(0, 0, 0),
            Color.FromRgb(255, 255, 255),
            Color.FromRgb(220, 50, 50),
            Color.FromRgb(50, 130, 220),
            Color.FromRgb(40, 170, 70),
            Color.FromRgb(245, 200, 60),
            Color.FromRgb(170, 90, 220),
            Color.FromRgb(255, 140, 40)
        };

        private void ShowShapeSettings()
        {
            HideShapeSettings();

            // Restyle a selected shape, or edit tool defaults when nothing is selected.
            var target = _styleTarget as ShapeAnnotation;
            ShapeKind curKind = target?.Kind ?? _shapeKind;
            Color curStroke = target?.GetStrokeColor() ?? _shapeStrokeColor;
            bool curHasFill = target?.HasFill ?? _shapeHasFill;
            Color curFillColor = target?.GetFillColor() ?? _shapeFillColor;
            double curWidth = target?.StrokeWidth ?? _shapeStrokeWidth;

            void ApplyKind(ShapeKind k)
            {
                _shapeKind = k;
                if (target is not null) { target.Kind = k; RestyleReselect(target); }
                else ShowShapeSettings();
            }
            void ApplyStroke(Color c)
            {
                _shapeStrokeColor = c;
                if (target is not null) { target.SetStrokeColor(c); RestyleReselect(target); }
                else ShowShapeSettings();
            }
            void ApplyFill(bool on)
            {
                _shapeHasFill = on;
                if (target is not null) { target.HasFill = on; if (on) target.SetFillColor(_shapeFillColor); RestyleReselect(target); }
                else ShowShapeSettings();
            }
            void ApplyFillColor(Color c)
            {
                _shapeFillColor = c;
                if (target is not null) { target.HasFill = true; target.SetFillColor(c); RestyleReselect(target); }
                else { _shapeHasFill = true; ShowShapeSettings(); }
            }
            void ApplyWidth(double w)
            {
                _shapeStrokeWidth = w;
                if (target is not null) { target.StrokeWidth = w; RestyleLive(target); }
            }

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };

            // Shape kind toggle
            panel.Children.Add(MakeLabel("Shape:"));
            void AddKindToggle(string glyph, ShapeKind kind, string toolTip)
            {
                var btn = new Button
                {
                    Content = glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 14,
                    Width = 26, Height = 24,
                    Margin = new Thickness(2, 0, 2, 0),
                    ToolTip = toolTip,
                    Cursor = Cursors.Hand,
                    Background = curKind == kind
                        ? (SolidColorBrush)FindResource("AccentGreenDim")
                        : Brushes.Transparent,
                    Foreground = curKind == kind
                        ? (SolidColorBrush)FindResource("AccentGreen")
                        : (SolidColorBrush)FindResource("TextPrimary"),
                    BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                    BorderThickness = new Thickness(1)
                };
                btn.Click += (_, _) => ApplyKind(kind);
                panel.Children.Add(btn);
            }
            AddKindToggle("\uE91A", ShapeKind.Rectangle, "Rectangle");
            AddKindToggle("\uEA3A", ShapeKind.Ellipse, "Ellipse");
            AddKindToggle("\uE739", ShapeKind.Line, "Line");

            panel.Children.Add(new Rectangle
            {
                Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(8, 2, 8, 2)
            });

            // Stroke color
            panel.Children.Add(MakeLabel("Stroke:"));
            foreach (var color in ShapeStrokeColors)
            {
                var c = color;
                bool selected = c.R == curStroke.R && c.G == curStroke.G && c.B == curStroke.B;
                var swatch = new Border
                {
                    Width = 18, Height = 18,
                    Background = new SolidColorBrush(c),
                    BorderBrush = selected
                        ? (SolidColorBrush)FindResource("AccentGreen")
                        : new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                    BorderThickness = new Thickness(selected ? 2 : 1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(1),
                    Cursor = Cursors.Hand
                };
                swatch.MouseLeftButtonDown += (_, _) => ApplyStroke(c);
                panel.Children.Add(swatch);
            }

            // Custom stroke color: opens the full RGB picker, applied like a fixed stroke swatch.
            panel.Children.Add(MakeCustomColorSwatch(
                Color.FromRgb(curStroke.R, curStroke.G, curStroke.B), ApplyStroke));

            panel.Children.Add(new Rectangle
            {
                Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(8, 2, 8, 2)
            });

            // Fill toggle + color
            panel.Children.Add(MakeLabel("Fill:"));
            var fillToggle = new CheckBox
            {
                Content = "On",
                IsChecked = curHasFill,
                Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            fillToggle.Checked += (_, _) => ApplyFill(true);
            fillToggle.Unchecked += (_, _) => ApplyFill(false);
            panel.Children.Add(fillToggle);

            if (curHasFill)
            {
                foreach (var color in ShapeStrokeColors)
                {
                    var c = color;
                    bool selected = c.R == curFillColor.R && c.G == curFillColor.G && c.B == curFillColor.B;
                    var swatch = new Border
                    {
                        Width = 18, Height = 18,
                        Background = new SolidColorBrush(c),
                        BorderBrush = selected
                            ? (SolidColorBrush)FindResource("AccentGreen")
                            : new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                        BorderThickness = new Thickness(selected ? 2 : 1),
                        CornerRadius = new CornerRadius(3),
                        Margin = new Thickness(1),
                        Cursor = Cursors.Hand
                    };
                    swatch.MouseLeftButtonDown += (_, _) => ApplyFillColor(c);
                    panel.Children.Add(swatch);
                }

                // Custom fill color: opens the full RGB picker, applied like a fixed fill swatch.
                panel.Children.Add(MakeCustomColorSwatch(
                    Color.FromRgb(curFillColor.R, curFillColor.G, curFillColor.B), ApplyFillColor));
            }

            panel.Children.Add(new Rectangle
            {
                Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(8, 2, 8, 2)
            });

            // Stroke width
            panel.Children.Add(MakeLabel("Width:"));
            var widthSlider = new Slider
            {
                Minimum = 1, Maximum = 12,
                Value = curWidth,
                Width = 90, VerticalAlignment = VerticalAlignment.Center,
                TickFrequency = 1, IsSnapToTickEnabled = true
            };
            var widthLabel = new TextBlock
            {
                Text = $"{curWidth:0}px",
                Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0), MinWidth = 32
            };
            widthSlider.ValueChanged += (_, _) =>
            {
                widthLabel.Text = $"{widthSlider.Value:0}px";
                ApplyWidth(widthSlider.Value);
            };
            panel.Children.Add(widthSlider);
            panel.Children.Add(widthLabel);

            _shapeSettingsBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Child = panel,
                Margin = new Thickness(8, 0, 0, 0)
            };

            var previewArea = PagePreviewPanel.Parent as Grid;
            if (previewArea is not null)
            {
                Panel.SetZIndex(_shapeSettingsBar, 100);
                previewArea.Children.Add(_shapeSettingsBar);
            }
        }

        private TextBlock MakeLabel(string text) => new TextBlock
        {
            Text = text,
            Foreground = (SolidColorBrush)FindResource("TextSecondary"),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        private void HideShapeSettings()
        {
            if (_shapeSettingsBar is not null)
            {
                (PagePreviewPanel.Parent as Grid)?.Children.Remove(_shapeSettingsBar);
                _shapeSettingsBar = null;
            }
        }

        // ============================================================
        // Signatures
        // ============================================================

        private void LoadSignatures()
        {
            try
            {
                // One-shot migration from the legacy beside-EXE location.
                if (!File.Exists(SignatureFile) && File.Exists(LegacySignatureFile))
                {
                    try
                    {
                        Directory.CreateDirectory(SignatureDir);
                        File.Copy(LegacySignatureFile, SignatureFile, overwrite: false);
                    }
                    catch { /* best effort */ }
                }

                if (File.Exists(SignatureFile))
                {
                    var json = File.ReadAllText(SignatureFile);
                    _savedSignatures = JsonSerializer.Deserialize<List<SavedSignature>>(json) ?? [];
                }
            }
            catch { _savedSignatures = []; }
        }

        private void PersistSignatures()
        {
            try
            {
                Directory.CreateDirectory(SignatureDir);
                var json = JsonSerializer.Serialize(_savedSignatures, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SignatureFile, json);
            }
            catch { /* best effort */ }
        }

        private void ShowSignaturePopup()
        {
            HideSignaturePopup();

            var stack = new StackPanel { Margin = new Thickness(4) };

            // Title
            stack.Children.Add(new TextBlock
            {
                Text = "Signatures",
                Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(4, 2, 4, 6)
            });

            // Saved signatures
            if (_savedSignatures.Count > 0)
            {
                var scroll = new ScrollViewer
                {
                    MaxHeight = 260,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                var listPanel = new StackPanel();

                foreach (var sig in _savedSignatures)
                {
                    var sigCopy = sig; // capture for lambda
                    var item = new Border
                    {
                        Background = Brushes.White,
                        BorderBrush = BrushResource("BorderDim"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Margin = new Thickness(4, 2, 4, 2),
                        Padding = new Thickness(4),
                        Cursor = Cursors.Hand,
                        Height = 60,
                        Width = 220
                    };

                    // Render mini signature preview
                    if (sigCopy.ImageData is not null)
                    {
                        try
                        {
                            var imgBytes = Convert.FromBase64String(sigCopy.ImageData);
                            var bmpImg = new System.Windows.Media.Imaging.BitmapImage();
                            using (var imageStream = new System.IO.MemoryStream(imgBytes))
                            {
                                bmpImg.BeginInit();
                                bmpImg.StreamSource = imageStream;
                                bmpImg.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                bmpImg.EndInit();
                            }
                            if (bmpImg.CanFreeze) bmpImg.Freeze();
                            item.Child = new System.Windows.Controls.Image
                            {
                                Source = bmpImg,
                                Width = 210, Height = 50,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                IsHitTestVisible = false
                            };
                        }
                        catch { item.Child = new TextBlock { Text = "(image)", IsHitTestVisible = false }; }
                    }
                    else
                    {
                        var canvas = new Canvas
                        {
                            Width = 210, Height = 50,
                            Background = Brushes.Transparent,
                            IsHitTestVisible = false
                        };
                        RenderSignaturePreview(canvas, sigCopy, 210, 50);
                        item.Child = canvas;
                    }

                    item.MouseLeftButtonDown += (s, e) =>
                    {
                        _pendingSignature = sigCopy;
                        HideSignaturePopup();
                        _annotationCanvas.Cursor = Cursors.Cross;
                        SetStatus("Click on the page to place your signature");
                    };
                    item.MouseEnter += (s, e) =>
                        ((Border)s!).BorderBrush = (SolidColorBrush)FindResource("AccentGreen");
                    item.MouseLeave += (s, e) =>
                        ((Border)s!).BorderBrush = BrushResource("BorderDim");

                    // Wrap in grid with delete button
                    var itemGrid = new Grid();
                    itemGrid.Children.Add(item);

                    var delBtn = new Button
                    {
                        Content = "\ue711",
                        FontSize = 10,
                        Width = 18, Height = 18,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 0, 2, 0),
                        Background = BrushResource("BgHover"),
                        Foreground = (SolidColorBrush)FindResource("DangerRed"),
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        Padding = new Thickness(0),
                        Style = (Style)FindResource("ToolbarButton")
                    };
                    delBtn.Click += (s, e) =>
                    {
                        _savedSignatures.Remove(sigCopy);
                        PersistSignatures();
                        ShowSignaturePopup(); // refresh
                    };
                    itemGrid.Children.Add(delBtn);
                    listPanel.Children.Add(itemGrid);
                }
                scroll.Content = listPanel;
                stack.Children.Add(scroll);
            }
            else
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "No saved signatures",
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(4, 4, 4, 8),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            // Separator
            stack.Children.Add(new Rectangle
            {
                Height = 1,
                Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(4, 4, 4, 4)
            });

            // Create Signature button
            var createBtn = new Button
            {
                Content = "Create Signature",
                Style = (Style)FindResource("DarkButton"),
                Background = (SolidColorBrush)FindResource("AccentGreenDim"),
                Foreground = (SolidColorBrush)FindResource("AccentGreen"),
                BorderBrush = (SolidColorBrush)FindResource("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            createBtn.Click += (s, e) =>
            {
                HideSignaturePopup();
                OpenSignatureCreator();
            };
            stack.Children.Add(createBtn);

            // Type Signature button — renders typed text in a handwriting font.
            var typeBtn = new Button
            {
                Content = "Type Signature",
                Style = (Style)FindResource("DarkButton"),
                Background = BrushResource("AccentGreenDim"),
                Foreground = (SolidColorBrush)FindResource("AccentGreen"),
                BorderBrush = (SolidColorBrush)FindResource("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(4, 2, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            typeBtn.Click += (s, e) =>
            {
                HideSignaturePopup();
                OpenTypedSignatureCreator();
            };
            stack.Children.Add(typeBtn);

            // Import image button
            var importBtn = new Button
            {
                Content = "Import Image",
                Style = (Style)FindResource("DarkButton"),
                Background = BrushResource("AccentGreenDim"),
                Foreground = (SolidColorBrush)FindResource("AccentGreen"),
                BorderBrush = (SolidColorBrush)FindResource("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(4, 2, 4, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            importBtn.Click += (s, e) =>
            {
                HideSignaturePopup();
                ImportImageSignature();
            };
            stack.Children.Add(importBtn);

            _signaturePopup = new Border
            {
                Background = BrushResource("BgPanel"),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Child = stack,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 80, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 12, Opacity = 0.5, ShadowDepth = 4
                }
            };

            var previewGrid = PagePreviewPanel.Parent as Grid;
            if (previewGrid is not null)
            {
                Panel.SetZIndex(_signaturePopup, 200);
                previewGrid.Children.Add(_signaturePopup);
            }
        }

        private void HideSignaturePopup()
        {
            if (_signaturePopup is not null)
            {
                var previewGrid = PagePreviewPanel.Parent as Grid;
                previewGrid?.Children.Remove(_signaturePopup);
                _signaturePopup = null;
            }
        }

        private void RenderSignaturePreview(Canvas canvas, SavedSignature sig, double targetW, double targetH)
        {
            double scaleX = targetW / sig.CanvasWidth;
            double scaleY = targetH / sig.CanvasHeight;
            double scale = Math.Min(scaleX, scaleY) * 0.9;

            double offsetX = (targetW - sig.CanvasWidth * scale) / 2;
            double offsetY = (targetH - sig.CanvasHeight * scale) / 2;

            foreach (var stroke in sig.Strokes)
            {
                if (stroke.Count < 2) continue;
                var poly = new Polyline
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = 1.5,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                foreach (var pt in stroke)
                    poly.Points.Add(new Point(pt.X * scale + offsetX, pt.Y * scale + offsetY));
                canvas.Children.Add(poly);
            }
        }

        private void OpenSignatureCreator()
        {
            var win = new Window
            {
                Title = "Create Signature",
                Width = 460, Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent
            };

            // Outer chrome
            var outerChrome = new Border
            {
                Background      = BrushResource("BgDark"),
                BorderBrush     = BrushResource("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6)
            };
            var rootStack = new StackPanel();

            // Title bar
            var titleBar = new Border
            {
                Background   = BrushResource("BgPanel"),
                Padding      = new Thickness(14, 8, 8, 8),
                CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleText = new TextBlock
            {
                Text       = "Create Signature",
                Foreground = BrushResource("AccentGreen"),
                FontWeight = FontWeights.SemiBold,
                FontSize   = 13,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleText, 0);
            var closeWinBtn = new Button
            {
                Content         = "",
                FontFamily      = new FontFamily("Segoe MDL2 Assets"),
                FontSize        = 10,
                Width           = 28, Height = 28,
                Background      = System.Windows.Media.Brushes.Transparent,
                Foreground      = BrushResource("TextSecondary"),
                BorderThickness = new Thickness(0),
                Cursor          = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeWinBtn.MouseEnter += (_, _2) => closeWinBtn.Foreground = BrushResource("DangerRed");
            closeWinBtn.MouseLeave += (_, _2) => closeWinBtn.Foreground = BrushResource("TextSecondary");
            closeWinBtn.Click += (_, _2) => win.Close();
            Grid.SetColumn(closeWinBtn, 1);
            titleGrid.Children.Add(titleText);
            titleGrid.Children.Add(closeWinBtn);
            titleBar.Child = titleGrid;
            rootStack.Children.Add(titleBar);

            var contentArea = new StackPanel();

            // Drawing canvas
            var canvasBorder = new Border
            {
                Background = Brushes.White,
                Margin = new Thickness(12, 12, 12, 4),
                CornerRadius = new CornerRadius(4),
                Height = 170
            };
            var drawCanvas = new Canvas
            {
                Background = Brushes.White,
                ClipToBounds = true,
                Cursor = Cursors.Pen
            };
            canvasBorder.Child = drawCanvas;

            // Placeholder text
            var placeholder = new TextBlock
            {
                Text = "Draw your signature here",
                Foreground = BrushResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14, FontStyle = FontStyles.Italic,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            drawCanvas.Children.Add(placeholder);

            // Drawing state
            var strokes = new List<List<Point>>();
            List<Point>? currentStroke = null;
            Polyline? currentPoly = null;

            drawCanvas.MouseLeftButtonDown += (s, e) =>
            {
                if (placeholder.Visibility == Visibility.Visible)
                    placeholder.Visibility = Visibility.Collapsed;
                currentStroke = [];
                var pos = e.GetPosition(drawCanvas);
                currentStroke.Add(pos);
                currentPoly = new Polyline
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                currentPoly.Points.Add(pos);
                drawCanvas.Children.Add(currentPoly);
                drawCanvas.CaptureMouse();
            };

            drawCanvas.MouseMove += (s, e) =>
            {
                if (currentStroke is null || currentPoly is null) return;
                var pos = e.GetPosition(drawCanvas);
                pos.X = Math.Clamp(pos.X, 0, drawCanvas.ActualWidth);
                pos.Y = Math.Clamp(pos.Y, 0, drawCanvas.ActualHeight);
                currentStroke.Add(pos);
                currentPoly.Points.Add(pos);
            };

            drawCanvas.MouseLeftButtonUp += (s, e) =>
            {
                if (currentStroke is not null && currentStroke.Count > 1)
                    strokes.Add(currentStroke);
                else if (currentPoly is not null)
                    drawCanvas.Children.Remove(currentPoly);
                currentStroke = null;
                currentPoly = null;
                drawCanvas.ReleaseMouseCapture();
            };

            contentArea.Children.Add(canvasBorder);

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 4, 12, 12)
            };

            var clearBtn = new Button
            {
                Content = "Clear",
                Style = (Style)FindResource("DarkButton"),
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = BrushResource("BgHover"),
                Foreground = BrushResource("TextPrimary"),
                BorderBrush = BrushResource("BorderDim"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas")
            };
            clearBtn.Click += (s, e) =>
            {
                strokes.Clear();
                drawCanvas.Children.Clear();
                placeholder.Visibility = Visibility.Visible;
                drawCanvas.Children.Add(placeholder);
            };

            var saveBtn = new Button
            {
                Content = "Save Signature",
                Style = (Style)FindResource("DarkButton"),
                Padding = new Thickness(16, 6, 16, 6),
                Background = BrushResource("AccentGreenDim"),
                Foreground = BrushResource("AccentGreen"),
                BorderBrush = BrushResource("AccentGreen"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold
            };
            saveBtn.Click += (s, e) =>
            {
                if (strokes.Count == 0)
                {
                    TdpDialog.Show(this, "Draw a signature first.", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                double cw = drawCanvas.ActualWidth > 0 ? drawCanvas.ActualWidth : 400;
                double ch = drawCanvas.ActualHeight > 0 ? drawCanvas.ActualHeight : 150;

                var saved = new SavedSignature
                {
                    CanvasWidth = cw,
                    CanvasHeight = ch,
                    Name = $"Signature {_savedSignatures.Count + 1}"
                };
                foreach (var stroke in strokes)
                {
                    var sPts = stroke.Select(p => new SerializablePoint { X = p.X, Y = p.Y }).ToList();
                    saved.Strokes.Add(sPts);
                }
                _savedSignatures.Add(saved);
                PersistSignatures();

                // Auto-select the new signature for placement
                _pendingSignature = saved;
                _annotationCanvas.Cursor = Cursors.Cross;
                SetStatus("Signature saved - click on the page to place it");

                win.Close();
            };

            btnPanel.Children.Add(clearBtn);
            btnPanel.Children.Add(saveBtn);
            contentArea.Children.Add(btnPanel);

            rootStack.Children.Add(contentArea);
            outerChrome.Child = rootStack;
            win.Content = outerChrome;
            win.ShowDialog();
        }

        // "Type a signature": the user types their name, picks a handwriting font and
        // ink color, and we rasterize it to a transparent PNG. That PNG is stored as a
        // SavedSignature.ImageData, so it flows through the exact same persistence,
        // placement, on-canvas render, and PDF-bake paths as an imported-image signature.
        private void OpenTypedSignatureCreator()
        {
            // Curated handwriting fonts that ship with Windows; keep only those installed.
            var preferred = new[] { "Segoe Script", "Segoe Print", "Gabriola", "Ink Free", "Lucida Handwriting", "Brush Script MT", "Monotype Corsiva" };
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fam in Fonts.SystemFontFamilies)
            {
                if (!string.IsNullOrEmpty(fam.Source)) installed.Add(fam.Source);
                foreach (var n in fam.FamilyNames.Values) installed.Add(n);
            }
            var available = preferred.Where(installed.Contains).ToList();
            if (available.Count == 0) available.Add("Segoe Script"); // best-effort; WPF substitutes if absent

            string selectedFont = available[0];
            var blackInk = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
            var blueInk = new SolidColorBrush(Color.FromRgb(0x12, 0x2A, 0x88));
            blackInk.Freeze(); blueInk.Freeze();
            SolidColorBrush inkBrush = blackInk;

            var win = new Window
            {
                Title = "Type Signature",
                Width = 480,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent
            };

            var outerChrome = new Border
            {
                Background = BrushResource("BgDark"),
                BorderBrush = BrushResource("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };
            var rootStack = new StackPanel();

            // Title bar (draggable)
            var titleBar = new Border
            {
                Background = BrushResource("BgPanel"),
                Padding = new Thickness(14, 8, 8, 8),
                CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleText = new TextBlock
            {
                Text = "Type Signature",
                Foreground = BrushResource("AccentGreen"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleText, 0);
            var closeWinBtn = new Button
            {
                Content = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Width = 28, Height = 28,
                Background = Brushes.Transparent,
                Foreground = BrushResource("TextSecondary"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeWinBtn.MouseEnter += (_, _2) => closeWinBtn.Foreground = BrushResource("DangerRed");
            closeWinBtn.MouseLeave += (_, _2) => closeWinBtn.Foreground = BrushResource("TextSecondary");
            closeWinBtn.Click += (_, _2) => win.Close();
            Grid.SetColumn(closeWinBtn, 1);
            titleGrid.Children.Add(titleText);
            titleGrid.Children.Add(closeWinBtn);
            titleBar.Child = titleGrid;
            rootStack.Children.Add(titleBar);

            var contentArea = new StackPanel();

            contentArea.Children.Add(new TextBlock
            {
                Text = "Type your name, then choose a style:",
                Foreground = BrushResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                Margin = new Thickness(12, 12, 12, 4)
            });

            var nameBox = new TextBox
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 16,
                Background = BrushResource("BgPanel"),
                Foreground = BrushResource("TextPrimary"),
                CaretBrush = BrushResource("TextPrimary"),
                BorderBrush = BrushResource("BorderDim"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(12, 0, 12, 8)
            };
            contentArea.Children.Add(nameBox);

            // Live preview
            var previewBorder = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(4),
                Height = 110,
                Margin = new Thickness(12, 0, 12, 8)
            };
            var previewBox = new Viewbox { Stretch = Stretch.Uniform, Margin = new Thickness(16, 8, 16, 8) };
            var previewText = new TextBlock
            {
                Text = "Your name",
                FontFamily = new FontFamily(selectedFont),
                FontSize = 64,
                Foreground = BrushResource("TextSecondary")
            };
            previewBox.Child = previewText;
            previewBorder.Child = previewBox;
            contentArea.Children.Add(previewBorder);

            // Style (font) picker
            contentArea.Children.Add(new TextBlock
            {
                Text = "Style",
                Foreground = BrushResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                Margin = new Thickness(12, 0, 12, 2)
            });
            var fontPanel = new WrapPanel { Margin = new Thickness(8, 0, 8, 8) };
            var fontButtons = new List<(Button btn, TextBlock label, string font)>();
            foreach (var font in available)
            {
                var lbl = new TextBlock
                {
                    Text = "Abc",
                    FontFamily = new FontFamily(font),
                    FontSize = 22,
                    Foreground = Brushes.Black
                };
                var b = new Button
                {
                    Content = lbl,
                    Style = (Style)FindResource("DarkButton"),
                    Background = Brushes.White,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 2, 10, 2),
                    Margin = new Thickness(4),
                    Cursor = Cursors.Hand,
                    ToolTip = font
                };
                fontButtons.Add((b, lbl, font));
                fontPanel.Children.Add(b);
            }
            contentArea.Children.Add(fontPanel);

            // Ink color picker
            var inkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 8) };
            inkRow.Children.Add(new TextBlock
            {
                Text = "Ink",
                Foreground = BrushResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 6, 0)
            });
            var inkButtons = new List<(Button btn, SolidColorBrush brush)>();
            Button MakeInkButton(string text, SolidColorBrush brush)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(new Border
                {
                    Width = 14, Height = 14,
                    CornerRadius = new CornerRadius(7),
                    Background = brush,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                sp.Children.Add(new TextBlock
                {
                    Text = text,
                    Foreground = BrushResource("TextPrimary"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                });
                var b = new Button
                {
                    Content = sp,
                    Style = (Style)FindResource("DarkButton"),
                    Background = BrushResource("BgHover"),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(4, 0, 0, 0),
                    Cursor = Cursors.Hand
                };
                return b;
            }
            var blackBtn = MakeInkButton("Black", blackInk);
            var blueBtn = MakeInkButton("Blue", blueInk);
            inkButtons.Add((blackBtn, blackInk));
            inkButtons.Add((blueBtn, blueInk));
            inkRow.Children.Add(blackBtn);
            inkRow.Children.Add(blueBtn);
            contentArea.Children.Add(inkRow);

            // --- shared refresh helpers (closures capture selectedFont / inkBrush) ---
            void RefreshPreview()
            {
                var name = nameBox.Text ?? "";
                bool empty = string.IsNullOrWhiteSpace(name);
                previewText.Text = empty ? "Your name" : name;
                previewText.FontFamily = new FontFamily(selectedFont);
                previewText.Foreground = empty ? BrushResource("TextSecondary") : inkBrush;
                foreach (var (b, lbl, font) in fontButtons)
                {
                    lbl.Text = empty ? "Abc" : name;
                    bool sel = font == selectedFont;
                    b.BorderBrush = sel ? BrushResource("AccentGreen") : BrushResource("BorderDim");
                    b.BorderThickness = new Thickness(sel ? 2 : 1);
                }
                foreach (var (b, brush) in inkButtons)
                {
                    bool sel = ReferenceEquals(brush, inkBrush);
                    b.BorderBrush = sel ? BrushResource("AccentGreen") : BrushResource("BorderDim");
                    b.BorderThickness = new Thickness(sel ? 2 : 1);
                }
            }

            foreach (var (b, _, font) in fontButtons)
                b.Click += (_, _2) => { selectedFont = font; RefreshPreview(); };
            blackBtn.Click += (_, _2) => { inkBrush = blackInk; RefreshPreview(); };
            blueBtn.Click += (_, _2) => { inkBrush = blueInk; RefreshPreview(); };
            nameBox.TextChanged += (_, _2) => RefreshPreview();
            RefreshPreview();

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 4, 12, 12)
            };
            var cancelBtn = new Button
            {
                Content = "Cancel",
                Style = (Style)FindResource("DarkButton"),
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = BrushResource("BgHover"),
                Foreground = BrushResource("TextPrimary"),
                BorderBrush = BrushResource("BorderDim"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas")
            };
            cancelBtn.Click += (_, _2) => win.Close();
            cancelBtn.IsCancel = true;   // Esc cancels (Enter is handled on nameBox below)
            var saveBtn = new Button
            {
                Content = "Save Signature",
                Style = (Style)FindResource("DarkButton"),
                Padding = new Thickness(16, 6, 16, 6),
                Background = BrushResource("AccentGreenDim"),
                Foreground = BrushResource("AccentGreen"),
                BorderBrush = BrushResource("AccentGreen"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold
            };

            void DoSave()
            {
                var text = (nameBox.Text ?? "").Trim();
                if (text.Length == 0)
                {
                    TdpDialog.Show(this, "Type your name first.", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var (base64, w, h) = RenderTypedSignaturePng(text, new FontFamily(selectedFont), inkBrush);
                var saved = new SavedSignature
                {
                    Name = text.Length > 40 ? text.Substring(0, 40) : text,
                    CanvasWidth = w,
                    CanvasHeight = h,
                    ImageData = base64
                };
                _savedSignatures.Add(saved);
                PersistSignatures();

                _pendingSignature = saved;
                _annotationCanvas.Cursor = Cursors.Cross;
                SetStatus("Signature saved - click on the page to place it");
                win.Close();
            }
            saveBtn.Click += (_, _2) => DoSave();
            nameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; DoSave(); } };

            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(saveBtn);
            contentArea.Children.Add(btnPanel);

            rootStack.Children.Add(contentArea);
            outerChrome.Child = rootStack;
            win.Content = outerChrome;
            win.Loaded += (_, _2) => nameBox.Focus();
            win.ShowDialog();
        }

        /// <summary>
        /// Rasterizes typed text in the given handwriting font and ink color to a
        /// transparent PNG, rendered at 2× for crisp placement/print. Returns the base-64
        /// PNG plus its logical width/height (used as the signature's source dimensions
        /// so its aspect ratio is preserved when placed and resized).
        /// </summary>
        private static (string base64, double width, double height) RenderTypedSignaturePng(string text, FontFamily fontFamily, Brush inkBrush)
        {
            const double fontSize = 96;
            const double pad = 24;
            const double scale = 2.0;

            var typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                inkBrush,
                1.0);

            double w = Math.Max(ft.WidthIncludingTrailingWhitespace, 1) + pad * 2;
            double h = Math.Max(ft.Height, 1) + pad * 2;

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawText(ft, new Point(pad, pad));

            var rtb = new RenderTargetBitmap(
                (int)Math.Ceiling(w * scale),
                (int)Math.Ceiling(h * scale),
                96 * scale, 96 * scale,
                PixelFormats.Pbgra32);
            rtb.Render(dv);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return (Convert.ToBase64String(ms.ToArray()), w, h);
        }

        private void ImportImageSignature()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
                Title = "Import Signature Image"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(dlg.FileName);
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                if (bmp.CanFreeze) bmp.Freeze();
                byte[] pngBytes;
                using (var ms = new System.IO.MemoryStream())
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    encoder.Save(ms);
                    pngBytes = ms.ToArray();
                }

                var saved = new SavedSignature
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName),
                    CanvasWidth = bmp.PixelWidth,
                    CanvasHeight = bmp.PixelHeight,
                    ImageData = Convert.ToBase64String(pngBytes)
                };
                _savedSignatures.Add(saved);
                PersistSignatures();

                _pendingSignature = saved;
                _annotationCanvas.Cursor = Cursors.Cross;
                SetStatus("Image loaded - click on the page to place it");
                ShowSignaturePopup(); // refresh to show the new entry
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Failed to import image:\n{ex.Message}", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PlaceSignature(Point pos, int pageIdx)
        {
            if (_pendingSignature is null) return;

            var sig = _pendingSignature;
            double scale = 0.5;

            var annot = new SignatureAnnotation
            {
                PageIndex = pageIdx,
                Position = pos,
                Scale = scale,
                SourceWidth = sig.CanvasWidth,
                SourceHeight = sig.CanvasHeight,
                ImageData = sig.ImageData
            };

            // Drawn signature — convert serializable points to WPF points
            if (sig.ImageData is null)
            {
                foreach (var stroke in sig.Strokes)
                    annot.Strokes.Add([..stroke.Select(p => new Point(p.X, p.Y))]);
            }

            AddAnnotation(annot);
            RenderAllAnnotations(pageIdx);
            // Auto-select so the user can immediately drag/resize/delete the new signature
            // without having to switch to Select first (Reddit/KillerPDF feedback).
            double sigW = annot.SourceWidth * annot.Scale;
            double sigH = annot.SourceHeight * annot.Scale;
            SetTool(EditTool.Select);
            SelectAnnotation(annot, new Rect(annot.Position.X, annot.Position.Y, sigW, sigH));
            SetStatus("Signature placed — drag the corner handle to resize, or Delete to remove");
        }

        private void PlaceImageFromDialog(Point pos, int pageIdx)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Insert Image",
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff;*.tif|All files|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var imgBytes = File.ReadAllBytes(dlg.FileName);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(imgBytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();

                double srcW = bmp.PixelWidth > 0 ? bmp.PixelWidth : 400;
                double srcH = bmp.PixelHeight > 0 ? bmp.PixelHeight : 300;

                // Default scale: fit within 250 canvas pixels on the longest axis
                const double MaxCanvasDim = 250;
                double scale = Math.Min(1.0, Math.Min(MaxCanvasDim / srcW, MaxCanvasDim / srcH));

                var imgAnnot = new ImageAnnotation
                {
                    PageIndex = pageIdx,
                    Position = pos,
                    Scale = scale,
                    SourceWidth = srcW,
                    SourceHeight = srcH,
                    ImageData = Convert.ToBase64String(imgBytes)
                };

                AddAnnotation(imgAnnot);
                RenderAllAnnotations(pageIdx);
                double w = srcW * scale;
                double h = srcH * scale;
                SelectAnnotation(imgAnnot, new Rect(pos.X, pos.Y, w, h));
                SetStatus("Image placed - drag the corner handle to resize, switch to Select to move/delete");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Could not load image:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ============================================================
        // Canvas interaction
        // ============================================================

        private void Canvas_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Middle-mouse: modal pan in any tool. Start panning and swallow the event so
            // other handlers (Canvas_MouseLeftButtonDown, AnnotationCanvas children) don't run.
            if (_doc is null) return;
            if (e.ChangedButton != MouseButton.Middle) return;
            if (IsPointerOperationActive) return;

            StartPan(e, MouseButton.Middle);
            e.Handled = true;
        }

        private void Canvas_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning && e.ChangedButton == _panButton)
            {
                EndPan();
                e.Handled = true;
            }
        }

        private void Canvas_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_isPanning)
                EndPan();
            // Don't reset other operations here — WPF can fire this for many reasons,
            // and the corresponding MouseUp handlers reset their own state.
        }

        private void StartPan(MouseButtonEventArgs e, MouseButton button)
        {
            _isPanning = true;
            _panButton = button;
            // Use ScrollViewer (viewer) coords so deltas don't scale with the page zoom transform.
            _panStartViewerPoint = e.GetPosition(PagePreviewPanel);
            _panStartHOffset = PagePreviewPanel.HorizontalOffset;
            _panStartVOffset = PagePreviewPanel.VerticalOffset;
            _cursorBeforePan ??= _annotationCanvas.Cursor;
            _annotationCanvas.Cursor = Cursors.ScrollAll;
            _annotationCanvas.CaptureMouse();
        }

        private void EndPan()
        {
            _isPanning = false;
            _panButton = null;
            if (_annotationCanvas.IsMouseCaptured)
                _annotationCanvas.ReleaseMouseCapture();
            if (_cursorBeforePan != null)
            {
                _annotationCanvas.Cursor = _cursorBeforePan;
                _cursorBeforePan = null;
            }
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_doc is null) return;
            // Don't intercept clicks on form-field overlay controls (TextBox, checkbox, etc.)
            // — WPF must handle those natively so focus, toggling, and text entry work.
            if (e.OriginalSource is DependencyObject formSrc && IsFormFieldElement(formSrc))
                return;
            // If a middle-mouse pan started before WPF routed the left-button event, swallow it.
            if (_isPanning) { e.Handled = true; return; }
            // Don't intercept clicks on an active text editing box
            if (_activeTextBox is not null && e.OriginalSource is DependencyObject src &&
                IsDescendantOf(src, _activeTextBox))
                return;
            // Don't intercept clicks on the crop confirm bar (canvas uses Preview events which
            // tunnel before child Button clicks fire — we must not swallow them here).
            if (_cropConfirmBar is not null && e.OriginalSource is DependencyObject cropSrc &&
                IsDescendantOf(cropSrc, _cropConfirmBar))
                return;
            // Check if click lands inside a PDF link overlay.
            // We do an explicit bounds check rather than relying on WPF hit-testing through
            // nested transparent canvases, which is unreliable.
            if (_linkOverlays.Count > 0)
            {
                var clickPos = e.GetPosition(_annotationCanvas);
                foreach (var lo in _linkOverlays)
                {
                    double lx = Canvas.GetLeft(lo);
                    double ly = Canvas.GetTop(lo);
                    if (clickPos.X >= lx && clickPos.X <= lx + lo.Width &&
                        clickPos.Y >= ly && clickPos.Y <= ly + lo.Height)
                    {
                        var lTarget = lo.Tag is LinkAnnotInfo lai ? lai.Target : lo.Tag;
                        if (lTarget is int tp)
                            PageList.SelectedIndex = tp;
                        else if (lTarget is string u)
                            try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
                        e.Handled = true;
                        return;
                    }
                }
            }
            var pos = e.GetPosition(_annotationCanvas);
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;

            // Crop corner handle — must be checked before the tool switch so the normal
            // Crop mousedown path (which calls HideCropConfirmBar) doesn't remove handles first.
            if (_cropHandles.Count > 0 && e.OriginalSource is Rectangle cropHandleRect &&
                _cropHandles.Contains(cropHandleRect))
            {
                _activeCropHandleTag = (string)cropHandleRect.Tag;
                _cropHandleDragStart = pos;
                _cropRectAtHandleDrag = _cropCanvasRect;
                _annotationCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (_currentTool == EditTool.EditImage && _imageResizeHandle is not null &&
                e.OriginalSource == _imageResizeHandle && _selectedAnnotation is ImageEditAnnotation selectedImage)
            {
                PushPageSnapshot(selectedImage.PageIndex);
                _isResizingImage = true;
                _resizingImageEdit = selectedImage;
                _imageResizeStart = pos;
                _imageResizeOriginalBounds = selectedImage.TargetBounds;
                _annotationCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            // Check if click is on the resize handle (signature or image annotation)
            if (_resizeHandle is not null && _selectedAnnotation is PlacedAnnotation rsa)
            {
                double hx = Canvas.GetLeft(_resizeHandle);
                double hy = Canvas.GetTop(_resizeHandle);
                if (pos.X >= hx && pos.X <= hx + _resizeHandle.Width &&
                    pos.Y >= hy && pos.Y <= hy + _resizeHandle.Height)
                {
                    PushPageSnapshot(rsa.PageIndex);
                    _isResizingSig = true;
                    _resizeSigStart = pos;
                    _resizeSigStartScale = rsa.Scale;
                    _resizeSigAnnot = rsa;
                    _annotationCanvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }

            // Check if click is on the generic-annotation resize handle (shape / highlight / ink)
            if (_annotResizeHandle is not null && _selectedAnnotation is not null
                && _selectedAnnotation is not PlacedAnnotation)
            {
                double hx = Canvas.GetLeft(_annotResizeHandle);
                double hy = Canvas.GetTop(_annotResizeHandle);
                if (pos.X >= hx && pos.X <= hx + _annotResizeHandle.Width &&
                    pos.Y >= hy && pos.Y <= hy + _annotResizeHandle.Height)
                {
                    BeginAnnotResize(_selectedAnnotation, pos);
                    e.Handled = true;
                    return;
                }
            }

            switch (_currentTool)
            {
                case EditTool.Select:
                    if (e.ClickCount == 2)
                    {
                        ClearSelection();
                        ClearTextSelection();
                        // Prefer re-editing a placed text box under the cursor; otherwise fall through to
                        // the existing-PDF-text white-out editor.
                        if (!TryReeditPlacedText(pos, pageIdx))
                            EditTextAtPosition(pos, pageIdx);
                        e.Handled = true;
                    }
                    else
                    {
                        // Single click: check if hitting a PlacedAnnotation first — select and drag
                        bool hitPlaced = false;
                        if (_annotations.TryGetValue(pageIdx, out var pageAnnotsList))
                        {
                            for (int i = pageAnnotsList.Count - 1; i >= 0; i--)
                            {
                                if (pageAnnotsList[i] is PlacedAnnotation pa &&
                                    HitTestAnnotation(pa, pos, out Rect paBounds))
                                {
                                    ClearSelection();
                                    RenderAllAnnotations(pageIdx);
                                    SelectAnnotation(pa, paBounds);
                                    PushPageSnapshot(pa.PageIndex);
                                    _isDraggingAnnot = true;
                                    _dragAnnotStart = pos;
                                    _dragAnnotOrigPos = pa.Position;
                                    _dragAnnot = pa;
                                    _annotationCanvas.CaptureMouse();
                                    e.Handled = true;
                                    hitPlaced = true;
                                    break;
                                }
                            }
                            // Then try non-placed annotations (Shape, Highlight, Ink, Text) — select and move
                            if (!hitPlaced)
                            {
                                for (int i = pageAnnotsList.Count - 1; i >= 0; i--)
                                {
                                    var a = pageAnnotsList[i];
                                    if (a is PlacedAnnotation) continue;
                                    if (a is ShapeAnnotation or HighlightAnnotation or InkAnnotation or TextAnnotation
                                        && HitTestAnnotation(a, pos, out Rect aBounds))
                                    {
                                        ClearSelection();
                                        RenderAllAnnotations(pageIdx);
                                        SelectAnnotation(a, aBounds);
                                        BeginAnnotMove(a, pos);
                                        e.Handled = true;
                                        hitPlaced = true;
                                        break;
                                    }
                                }
                            }
                        }
                        if (!hitPlaced)
                        {
                            ClearSelection();
                            ClearTextSelection();
                            _isSelecting = true;
                            _selectStart = pos;
                            _selectRect = new Rectangle
                            {
                                Fill = FrozenSolidColorBrush(Color.FromArgb(40, 74, 130, 255)),
                                Stroke = FrozenSolidColorBrush(Color.FromArgb(120, 74, 130, 255)),
                                StrokeThickness = 1,
                                Width = 0, Height = 0,
                                IsHitTestVisible = false
                            };
                            Canvas.SetLeft(_selectRect, pos.X);
                            Canvas.SetTop(_selectRect, pos.Y);
                            _annotationCanvas.Children.Add(_selectRect);
                            _annotationCanvas.CaptureMouse();
                            e.Handled = true;
                        }
                    }
                    break;

                case EditTool.Text:
                    CommitActiveTextBox();
                    PlaceTextBox(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.EditText:
                    CommitActiveTextBox();
                    EditTextAtPosition(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.EditImage:
                    CommitActiveTextBox();
                    EditImageAtPosition(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.Highlight:
                    ClearSelection();
                    _isDrawing = true;
                    _drawStart = pos;
                    var rect = new Rectangle
                    {
                        Fill = FrozenSolidColorBrush(_highlightColor),
                        Width = 0, Height = 0
                    };
                    Canvas.SetLeft(rect, pos.X);
                    Canvas.SetTop(rect, pos.Y);
                    _annotationCanvas.Children.Add(rect);
                    _activePreview = rect;
                    _annotationCanvas.CaptureMouse();
                    break;

                case EditTool.Crop:
                    ClearSelection();
                    ClearCropSelection();
                    HideCropConfirmBar();
                    _isDrawing = true;
                    _drawStart = pos;
                    var cropRect = new Rectangle
                    {
                        Fill = new SolidColorBrush(Color.FromArgb(35, 74, 222, 128)),
                        Stroke = (SolidColorBrush)FindResource("AccentGreen"),
                        StrokeThickness = 2,
                        StrokeDashArray = new DoubleCollection { 6, 3 },
                        Width = 0,
                        Height = 0,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(cropRect, pos.X);
                    Canvas.SetTop(cropRect, pos.Y);
                    _annotationCanvas.Children.Add(cropRect);
                    _activePreview = cropRect;
                    _annotationCanvas.CaptureMouse();
                    e.Handled = true;
                    break;

                case EditTool.Draw:
                    ClearSelection();
                    _isDrawing = true;
                    _activeInk = new InkAnnotation { PageIndex = pageIdx, StrokeWidth = _drawWidth };
                    _activeInk.SetColor(_drawColor);
                    _activeInk.Points.Add(pos);
                    var poly = new Polyline
                    {
                        Stroke = FrozenSolidColorBrush(_drawColor),
                        StrokeThickness = _drawWidth,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    poly.Points.Add(pos);
                    _annotationCanvas.Children.Add(poly);
                    _activePreview = poly;
                    _annotationCanvas.CaptureMouse();
                    break;

                case EditTool.Signature:
                    if (_pendingSignature is not null)
                    {
                        PlaceSignature(pos, pageIdx);
                        e.Handled = true;
                    }
                    else
                    {
                        ShowSignaturePopup();
                    }
                    break;

                case EditTool.Image:
                    PlaceImageFromDialog(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.Pan:
                    StartPan(e, MouseButton.Left);
                    e.Handled = true;
                    break;

                case EditTool.Erase:
                {
                    ClearSelection();
                    if (_annotations.TryGetValue(pageIdx, out var erasePageList))
                    {
                        for (int i = erasePageList.Count - 1; i >= 0; i--)
                        {
                            if (HitTestAnnotation(erasePageList[i], pos, out _))
                            {
                                PushPageSnapshot(pageIdx);
                                erasePageList.RemoveAt(i);
                                RenderAllAnnotations(pageIdx);
                                MarkDirty();
                                SetStatus("Erased annotation");
                                break;
                            }
                        }
                    }
                    e.Handled = true;
                    break;
                }

                case EditTool.Shape:
                {
                    ClearSelection();
                    _isDrawing = true;
                    _drawStart = pos;
                    Shape preview = _shapeKind switch
                    {
                        ShapeKind.Rectangle => new Rectangle
                        {
                            Stroke = FrozenSolidColorBrush(_shapeStrokeColor),
                            StrokeThickness = _shapeStrokeWidth,
                            Fill = _shapeHasFill
                                ? FrozenSolidColorBrush(_shapeFillColor)
                                : (Brush)Brushes.Transparent,
                            Width = 0, Height = 0,
                            IsHitTestVisible = false
                        },
                        ShapeKind.Ellipse => new Ellipse
                        {
                            Stroke = FrozenSolidColorBrush(_shapeStrokeColor),
                            StrokeThickness = _shapeStrokeWidth,
                            Fill = _shapeHasFill
                                ? FrozenSolidColorBrush(_shapeFillColor)
                                : (Brush)Brushes.Transparent,
                            Width = 0, Height = 0,
                            IsHitTestVisible = false
                        },
                        ShapeKind.Line => new Line
                        {
                            Stroke = FrozenSolidColorBrush(_shapeStrokeColor),
                            StrokeThickness = _shapeStrokeWidth,
                            StrokeStartLineCap = PenLineCap.Round,
                            StrokeEndLineCap = PenLineCap.Round,
                            X1 = pos.X, Y1 = pos.Y, X2 = pos.X, Y2 = pos.Y,
                            IsHitTestVisible = false
                        },
                        _ => throw new InvalidOperationException()
                    };
                    if (_shapeKind != ShapeKind.Line)
                    {
                        Canvas.SetLeft(preview, pos.X);
                        Canvas.SetTop(preview, pos.Y);
                    }
                    _annotationCanvas.Children.Add(preview);
                    _activePreview = preview;
                    _annotationCanvas.CaptureMouse();
                    e.Handled = true;
                    break;
                }
            }
        }

        private void BeginAnnotMove(PageAnnotation annot, Point pos)
        {
            PushPageSnapshot(annot.PageIndex);
            _isMovingAnnot = true;
            _movingAnnot = annot;
            _moveStartCanvas = pos;
            _moveOriginalGeom = CaptureGeometry(annot);
            _annotationCanvas.CaptureMouse();
        }

        private void BeginAnnotResize(PageAnnotation annot, Point pos)
        {
            PushPageSnapshot(annot.PageIndex);
            // Seed a legacy (auto-sized) text box's fixed Width/Height from its current extent so the
            // resize drag has a concrete basis to grow/shrink from.
            if (annot is TextAnnotation t && (t.Width <= 0 || t.Height <= 0))
            {
                var sz = MeasureTextAnnotation(t);
                if (t.Width <= 0) t.Width = sz.Width;
                if (t.Height <= 0) t.Height = sz.Height;
            }
            _isResizingAnnot = true;
            _resizingAnnot = annot;
            _resizeStartCanvas = pos;
            _resizeOriginalGeom = CaptureGeometry(annot);
            _annotationCanvas.CaptureMouse();
        }

        /// <summary>
        /// Snapshot the geometric state of an annotation so a move or resize can be applied
        /// relative to the starting state without compounding rounding errors.
        /// </summary>
        private static object CaptureGeometry(PageAnnotation annot) => annot switch
        {
            ShapeAnnotation s => (Start: s.Start, End: s.End, StrokeWidth: s.StrokeWidth),
            HighlightAnnotation h => h.Bounds,
            InkAnnotation i => new List<Point>(i.Points),
            TextAnnotation t => (Position: t.Position, Width: t.Width, Height: t.Height),
            _ => 0
        };

        /// <summary>
        /// Returns true if the annotation's geometry matches the captured original — used to
        /// drop no-op snapshots when a click without drag triggered BeginAnnotMove/Resize.
        /// </summary>
        private static bool GeometryUnchanged(PageAnnotation annot, object? original)
        {
            if (original is null) return false;
            switch (annot)
            {
                case ShapeAnnotation s when original is ValueTuple<Point, Point, double> o:
                    return s.Start == o.Item1 && s.End == o.Item2;
                case HighlightAnnotation h when original is Rect r:
                    return h.Bounds == r;
                case InkAnnotation ink when original is List<Point> pts:
                    if (ink.Points.Count != pts.Count) return false;
                    for (int i = 0; i < pts.Count; i++)
                        if (ink.Points[i] != pts[i]) return false;
                    return true;
                case TextAnnotation t when original is ValueTuple<Point, double, double> tp:
                    return t.Position == tp.Item1 && t.Width == tp.Item2 && t.Height == tp.Item3;
                default:
                    return false;
            }
        }

        private void ApplyMoveTo(PageAnnotation annot, Point cur, Point start, object original)
        {
            double dx = cur.X - start.X;
            double dy = cur.Y - start.Y;
            switch (annot)
            {
                case ShapeAnnotation s when original is ValueTuple<Point, Point, double> o:
                    s.Start = new Point(o.Item1.X + dx, o.Item1.Y + dy);
                    s.End   = new Point(o.Item2.X + dx, o.Item2.Y + dy);
                    break;
                case HighlightAnnotation h when original is Rect r:
                    h.Bounds = new Rect(r.X + dx, r.Y + dy, r.Width, r.Height);
                    break;
                case InkAnnotation ink when original is List<Point> pts:
                    ink.Points.Clear();
                    foreach (var p in pts) ink.Points.Add(new Point(p.X + dx, p.Y + dy));
                    break;
                case TextAnnotation t when original is ValueTuple<Point, double, double> tp:
                    t.Position = new Point(tp.Item1.X + dx, tp.Item1.Y + dy);
                    break;
            }
        }

        private void ApplyResizeTo(PageAnnotation annot, Point cur, Point start, object original)
        {
            switch (annot)
            {
                case ShapeAnnotation s when original is ValueTuple<Point, Point, double> o:
                {
                    // Anchor to Start; drag End.
                    s.Start = o.Item1;
                    s.End = new Point(o.Item2.X + (cur.X - start.X), o.Item2.Y + (cur.Y - start.Y));
                    break;
                }
                case HighlightAnnotation h when original is Rect r:
                {
                    double newW = Math.Max(4, r.Width + (cur.X - start.X));
                    double newH = Math.Max(4, r.Height + (cur.Y - start.Y));
                    h.Bounds = new Rect(r.X, r.Y, newW, newH);
                    break;
                }
                case InkAnnotation ink when original is List<Point> pts:
                {
                    if (pts.Count == 0) break;
                    double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y);
                    double maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
                    double origW = Math.Max(1, maxX - minX), origH = Math.Max(1, maxY - minY);
                    double newW = Math.Max(4, origW + (cur.X - start.X));
                    double newH = Math.Max(4, origH + (cur.Y - start.Y));
                    double sx = newW / origW, sy = newH / origH;
                    ink.Points.Clear();
                    foreach (var p in pts)
                        ink.Points.Add(new Point(minX + (p.X - minX) * sx, minY + (p.Y - minY) * sy));
                    double uniform = (sx + sy) * 0.5;
                    ink.StrokeWidth = Math.Max(0.5, ink.StrokeWidth * uniform);
                    break;
                }
                case TextAnnotation t when original is ValueTuple<Point, double, double> tp:
                {
                    // Anchor top-left; drag bottom-right to set the wrap Width and box Height.
                    t.Width = Math.Max(32, tp.Item2 + (cur.X - start.X));
                    t.Height = Math.Max(t.FontSize + 6, tp.Item3 + (cur.Y - start.Y));
                    break;
                }
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            // Don't interfere with mouse interaction inside form-field overlays.
            if (e.OriginalSource is DependencyObject moveSrc && IsFormFieldElement(moveSrc))
                return;
            // Pan first — uses viewer coords so deltas don't scale with the page transform.
            if (_isPanning)
            {
                var viewerPos = e.GetPosition(PagePreviewPanel);
                double dx = viewerPos.X - _panStartViewerPoint.X;
                double dy = viewerPos.Y - _panStartViewerPoint.Y;
                PagePreviewPanel.ScrollToHorizontalOffset(_panStartHOffset - dx);
                PagePreviewPanel.ScrollToVerticalOffset(_panStartVOffset - dy);
                return;
            }

            var pos = e.GetPosition(_annotationCanvas);
            pos.X = Math.Clamp(pos.X, 0, _annotationCanvas.ActualWidth);
            pos.Y = Math.Clamp(pos.Y, 0, _annotationCanvas.ActualHeight);

            // Generic annotation move
            if (_isMovingAnnot && _movingAnnot is not null && _moveOriginalGeom is not null)
            {
                ApplyMoveTo(_movingAnnot, pos, _moveStartCanvas, _moveOriginalGeom);
                RenderAllAnnotations(_movingAnnot.PageIndex);
                if (HitTestAnnotation(_movingAnnot, GetAnyPointInside(_movingAnnot), out Rect mb))
                    RefreshSelectionVisuals(mb);
                MarkDirty();
                return;
            }

            // Generic annotation resize
            if (_isResizingAnnot && _resizingAnnot is not null && _resizeOriginalGeom is not null)
            {
                ApplyResizeTo(_resizingAnnot, pos, _resizeStartCanvas, _resizeOriginalGeom);
                RenderAllAnnotations(_resizingAnnot.PageIndex);
                if (HitTestAnnotation(_resizingAnnot, GetAnyPointInside(_resizingAnnot), out Rect rb))
                    RefreshSelectionVisuals(rb);
                MarkDirty();
                return;
            }

            // Signature resize drag
            if (_isResizingSig && _resizeSigAnnot is not null)
            {
                double dx = pos.X - _resizeSigStart.X;
                double dy = pos.Y - _resizeSigStart.Y;
                double delta = (Math.Abs(dx) > Math.Abs(dy) ? dx : dy);
                double newScale = Math.Max(0.05, _resizeSigStartScale + delta / _resizeSigAnnot.SourceWidth);
                _resizeSigAnnot.Scale = newScale;

                // Update selection border and handle position live
                double newW = _resizeSigAnnot.SourceWidth * newScale;
                double newH = _resizeSigAnnot.SourceHeight * newScale;
                if (_selectionBorder is not null)
                {
                    _selectionBorder.Width  = newW + 8;
                    _selectionBorder.Height = newH + 8;
                }
                if (_resizeHandle is not null)
                {
                    double hx = _resizeSigAnnot.Position.X + newW - 4 - _resizeHandle.Width / 2;
                    double hy = _resizeSigAnnot.Position.Y + newH - 4 - _resizeHandle.Height / 2;
                    Canvas.SetLeft(_resizeHandle, hx);
                    Canvas.SetTop(_resizeHandle, hy);
                }

                // Re-render annotations to show updated size
                RenderAllAnnotations(_resizeSigAnnot.PageIndex);
                // Restore selection visuals (RenderAllAnnotations clears canvas children including our overlays)
                _annotationCanvas.Children.Add(_selectionBorder!);
                _annotationCanvas.Children.Add(_resizeHandle!);
                return;
            }

            // Annotation drag-to-move
            if (_isDraggingAnnot && _dragAnnot is not null)
            {
                double dx = pos.X - _dragAnnotStart.X;
                double dy = pos.Y - _dragAnnotStart.Y;
                _dragAnnot.Position = new Point(_dragAnnotOrigPos.X + dx, _dragAnnotOrigPos.Y + dy);
                double w = _dragAnnot.SourceWidth * _dragAnnot.Scale;
                double h = _dragAnnot.SourceHeight * _dragAnnot.Scale;
                if (_selectionBorder is not null)
                {
                    Canvas.SetLeft(_selectionBorder, _dragAnnot.Position.X - 4);
                    Canvas.SetTop(_selectionBorder, _dragAnnot.Position.Y - 4);
                }
                if (_resizeHandle is not null)
                {
                    Canvas.SetLeft(_resizeHandle, _dragAnnot.Position.X + w - 4 - _resizeHandle.Width / 2);
                    Canvas.SetTop(_resizeHandle, _dragAnnot.Position.Y + h - 4 - _resizeHandle.Height / 2);
                }
                RenderAllAnnotations(_dragAnnot.PageIndex);
                _annotationCanvas.Children.Add(_selectionBorder!);
                _annotationCanvas.Children.Add(_resizeHandle!);
                return;
            }

            // Text selection drag
            if (_isSelecting && _selectRect is not null)
            {
                Canvas.SetLeft(_selectRect, Math.Min(pos.X, _selectStart.X));
                Canvas.SetTop(_selectRect, Math.Min(pos.Y, _selectStart.Y));
                _selectRect.Width = Math.Abs(pos.X - _selectStart.X);
                _selectRect.Height = Math.Abs(pos.Y - _selectStart.Y);
                return;
            }

            if (_isResizingImage && _resizingImageEdit is not null)
            {
                ResizeImageEditPreview(pos);
                return;
            }

            if (!_isDrawing || _activePreview is null) return;

            switch (_currentTool)
            {
                case EditTool.Highlight when _activePreview is Rectangle:
                case EditTool.Crop when _activePreview is Rectangle:
                    var rect = (Rectangle)_activePreview;
                    Canvas.SetLeft(rect, Math.Min(pos.X, _drawStart.X));
                    Canvas.SetTop(rect, Math.Min(pos.Y, _drawStart.Y));
                    rect.Width = Math.Abs(pos.X - _drawStart.X);
                    rect.Height = Math.Abs(pos.Y - _drawStart.Y);
                    break;

                case EditTool.Draw when _activePreview is Polyline poly && _activeInk is not null:
                    _activeInk.Points.Add(pos);
                    poly.Points.Add(pos);
                    break;

                case EditTool.Shape when _activePreview is Line lnPrev:
                    lnPrev.X2 = pos.X;
                    lnPrev.Y2 = pos.Y;
                    break;

                case EditTool.Shape when _activePreview is FrameworkElement shapePrev:
                {
                    double sx = Math.Min(pos.X, _drawStart.X);
                    double sy = Math.Min(pos.Y, _drawStart.Y);
                    double sw = Math.Abs(pos.X - _drawStart.X);
                    double sh = Math.Abs(pos.Y - _drawStart.Y);
                    Canvas.SetLeft(shapePrev, sx);
                    Canvas.SetTop(shapePrev, sy);
                    shapePrev.Width = sw;
                    shapePrev.Height = sh;
                    break;
                }

                case EditTool.Crop when _activePreview is Rectangle crect:
                    Canvas.SetLeft(crect, Math.Min(pos.X, _drawStart.X));
                    Canvas.SetTop(crect, Math.Min(pos.Y, _drawStart.Y));
                    crect.Width = Math.Abs(pos.X - _drawStart.X);
                    crect.Height = Math.Abs(pos.Y - _drawStart.Y);
                    break;
            }

            // Crop corner handle drag — resize the crop rect live.
            if (_activeCropHandleTag is not null && _cropPreviewRect is not null)
            {
                double dx = pos.X - _cropHandleDragStart.X;
                double dy = pos.Y - _cropHandleDragStart.Y;
                var r = _cropRectAtHandleDrag;
                double newX = r.X, newY = r.Y, newW = r.Width, newH = r.Height;
                switch (_activeCropHandleTag)
                {
                    case "NW":
                        newX = Math.Min(r.Right - 10, r.X + dx);
                        newY = Math.Min(r.Bottom - 10, r.Y + dy);
                        newW = r.Right - newX;
                        newH = r.Bottom - newY;
                        break;
                    case "NE":
                        newY = Math.Min(r.Bottom - 10, r.Y + dy);
                        newW = Math.Max(10, r.Width + dx);
                        newH = r.Bottom - newY;
                        break;
                    case "SE":
                        newW = Math.Max(10, r.Width + dx);
                        newH = Math.Max(10, r.Height + dy);
                        break;
                    case "SW":
                        newX = Math.Min(r.Right - 10, r.X + dx);
                        newW = r.Right - newX;
                        newH = Math.Max(10, r.Height + dy);
                        break;
                }
                _cropCanvasRect = new Rect(newX, newY, newW, newH);
                UpdateCropRectVisuals();
            }
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Pan was started with left-click in Pan tool: release here.
            if (_isPanning && _panButton == MouseButton.Left)
            {
                EndPan();
                e.Handled = true;
                return;
            }

            // Don't process release events that originate inside the crop confirm bar.
            if (_cropConfirmBar is not null && e.OriginalSource is DependencyObject cropSrc &&
                IsDescendantOf(cropSrc, _cropConfirmBar))
                return;

            int pageIdx = PageList.SelectedIndex;

            // Finish crop handle drag
            if (_activeCropHandleTag is not null)
            {
                _activeCropHandleTag = null;
                if (_annotationCanvas.IsMouseCaptured) _annotationCanvas.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }

            // Finish generic annotation move
            if (_isMovingAnnot)
            {
                var ma = _movingAnnot;
                var origGeom = _moveOriginalGeom;
                _isMovingAnnot = false;
                _movingAnnot = null;
                _moveOriginalGeom = null;
                if (_annotationCanvas.IsMouseCaptured) _annotationCanvas.ReleaseMouseCapture();
                if (ma is not null)
                {
                    if (GeometryUnchanged(ma, origGeom))
                        DropTopSnapshotIfFor(ma.PageIndex);
                    RenderAllAnnotations(ma.PageIndex);
                    if (HitTestAnnotation(ma, GetAnyPointInside(ma), out Rect mb))
                        SelectAnnotation(ma, mb);
                }
                return;
            }

            // Finish generic annotation resize
            if (_isResizingAnnot)
            {
                var ra = _resizingAnnot;
                var origGeom = _resizeOriginalGeom;
                _isResizingAnnot = false;
                _resizingAnnot = null;
                _resizeOriginalGeom = null;
                if (_annotationCanvas.IsMouseCaptured) _annotationCanvas.ReleaseMouseCapture();
                if (ra is not null)
                {
                    if (GeometryUnchanged(ra, origGeom))
                        DropTopSnapshotIfFor(ra.PageIndex);
                    RenderAllAnnotations(ra.PageIndex);
                    if (HitTestAnnotation(ra, GetAnyPointInside(ra), out Rect rb))
                        SelectAnnotation(ra, rb);
                }
                return;
            }

            // Finish annotation drag-to-move
            if (_isDraggingAnnot)
            {
                _isDraggingAnnot = false;
                _annotationCanvas.ReleaseMouseCapture();
                if (_dragAnnot is not null)
                {
                    var da = _dragAnnot;
                    _dragAnnot = null;
                    if (da.Position == _dragAnnotOrigPos)
                        DropTopSnapshotIfFor(da.PageIndex);
                    else
                        MarkDirty();
                    RenderAllAnnotations(da.PageIndex);
                    double w = da.SourceWidth * da.Scale;
                    double h = da.SourceHeight * da.Scale;
                    SelectAnnotation(da, new Rect(da.Position.X, da.Position.Y, w, h));
                }
                return;
            }

            // Finish signature resize
            if (_isResizingSig)
            {
                _isResizingSig = false;
                _annotationCanvas.ReleaseMouseCapture();
                if (_resizeSigAnnot is not null)
                {
                    // Final re-render and re-select to reposition handle cleanly
                    var sa = _resizeSigAnnot;
                    _resizeSigAnnot = null;
                    if (sa.Scale == _resizeSigStartScale)
                        DropTopSnapshotIfFor(sa.PageIndex);
                    else
                        MarkDirty();
                    RenderAllAnnotations(sa.PageIndex);
                    double newW = sa.SourceWidth * sa.Scale;
                    double newH = sa.SourceHeight * sa.Scale;
                    SelectAnnotation(sa, new Rect(sa.Position.X, sa.Position.Y, newW, newH));
                    MarkDirty();
                }
                return;
            }

            // Handle text selection release
            if (_isSelecting)
            {
                _isSelecting = false;
                _annotationCanvas.ReleaseMouseCapture();
                var pos = e.GetPosition(_annotationCanvas);
                double dragW = Math.Abs(pos.X - _selectStart.X);
                double dragH = Math.Abs(pos.Y - _selectStart.Y);

                // A pending "OCR Region" arm consumes this drag regardless of outcome.
                bool ocrRegion = _ocrRegionMode;
                _ocrRegionMode = false;

                if (dragW < 5 && dragH < 5)
                {
                    // Tiny drag = single click -> try annotation selection
                    ClearTextSelection();
                    if (pageIdx >= 0 && _annotations.ContainsKey(pageIdx))
                    {
                        for (int i = _annotations[pageIdx].Count - 1; i >= 0; i--)
                        {
                            if (HitTestAnnotation(_annotations[pageIdx][i], _selectStart, out Rect bounds))
                            {
                                SelectAnnotation(_annotations[pageIdx][i], bounds);
                                break;
                            }
                        }
                    }
                }
                else
                {
                    // Real drag -> extract text from the rectangle, or OCR it if the OCR-region tool is armed.
                    var selectBounds = new Rect(
                        Math.Min(pos.X, _selectStart.X), Math.Min(pos.Y, _selectStart.Y),
                        dragW, dragH);
                    if (ocrRegion) OcrRegion(pageIdx, selectBounds);
                    else ExtractTextFromRegion(pageIdx, selectBounds);
                }
                return;
            }

            if (_isResizingImage && _resizingImageEdit is not null)
            {
                _isResizingImage = false;
                _annotationCanvas.ReleaseMouseCapture();
                var resizing = _resizingImageEdit;
                if (resizing.TargetBounds == _imageResizeOriginalBounds)
                {
                    DropTopSnapshotIfFor(resizing.PageIndex);
                }
                else
                {
                    MarkDirty();
                }
                RenderAllAnnotations(resizing.PageIndex);
                SelectAnnotation(resizing, resizing.TargetBounds);
                SetStatus("Image resize committed - save to apply white-out + overdraw");
                _resizingImageEdit = null;
                return;
            }

            if (!_isDrawing) return;
            _isDrawing = false;
            _annotationCanvas.ReleaseMouseCapture();

            switch (_currentTool)
            {
                case EditTool.Highlight when _activePreview is Rectangle rect:
                    if (rect.Width > 3 && rect.Height > 3)
                    {
                        var ha = new HighlightAnnotation
                        {
                            PageIndex = pageIdx,
                            Bounds = new Rect(Canvas.GetLeft(rect), Canvas.GetTop(rect), rect.Width, rect.Height)
                        };
                        ha.SetColor(_highlightColor);
                        AddAnnotation(ha);
                    }
                    else
                    {
                        _annotationCanvas.Children.Remove(rect);
                    }
                    break;

                case EditTool.Crop when _activePreview is Rectangle rect:
                    if (rect.Width > 5 && rect.Height > 5)
                    {
                        _activeCrop = new CropAnnotation
                        {
                            PageIndex = pageIdx,
                            Bounds = new Rect(Canvas.GetLeft(rect), Canvas.GetTop(rect), rect.Width, rect.Height)
                        };
                        ShowCropPopup();
                        SetStatus("Crop rectangle selected - choose Apply crop, Reset, or Cancel");
                    }
                    else
                    {
                        _annotationCanvas.Children.Remove(rect);
                        _activePreview = null;
                        _activeCrop = null;
                    }
                    break;

                case EditTool.Draw when _activeInk is not null:
                    if (_activeInk.Points.Count > 2)
                    {
                        AddAnnotation(_activeInk);
                    }
                    else
                    {
                        _annotationCanvas.Children.Remove(_activePreview);
                    }
                    _activeInk = null;
                    break;

                case EditTool.Shape when _activePreview is Line lnCommit:
                {
                    double dx = lnCommit.X2 - lnCommit.X1;
                    double dy = lnCommit.Y2 - lnCommit.Y1;
                    if (Math.Sqrt(dx * dx + dy * dy) >= 4)
                    {
                        var sa = new ShapeAnnotation
                        {
                            PageIndex = pageIdx,
                            Kind = ShapeKind.Line,
                            Start = new Point(lnCommit.X1, lnCommit.Y1),
                            End = new Point(lnCommit.X2, lnCommit.Y2),
                            StrokeWidth = _shapeStrokeWidth,
                            HasFill = false
                        };
                        sa.SetStrokeColor(_shapeStrokeColor);
                        sa.SetFillColor(_shapeFillColor);
                        AddAnnotation(sa);
                    }
                    else
                    {
                        _annotationCanvas.Children.Remove(lnCommit);
                    }
                    break;
                }

                case EditTool.Shape when _activePreview is FrameworkElement shapeCommit:
                {
                    double sx = Canvas.GetLeft(shapeCommit);
                    double sy = Canvas.GetTop(shapeCommit);
                    if (shapeCommit.Width >= 4 && shapeCommit.Height >= 4)
                    {
                        var sa = new ShapeAnnotation
                        {
                            PageIndex = pageIdx,
                            Kind = shapeCommit is Ellipse ? ShapeKind.Ellipse : ShapeKind.Rectangle,
                            Start = new Point(sx, sy),
                            End = new Point(sx + shapeCommit.Width, sy + shapeCommit.Height),
                            StrokeWidth = _shapeStrokeWidth,
                            HasFill = _shapeHasFill
                        };
                        sa.SetStrokeColor(_shapeStrokeColor);
                        sa.SetFillColor(_shapeFillColor);
                        AddAnnotation(sa);
                    }
                    else
                    {
                        _annotationCanvas.Children.Remove(shapeCommit);
                    }
                    break;
                }

                case EditTool.Crop when _activePreview is Rectangle cr:
                    if (cr.Width > 10 && cr.Height > 10)
                    {
                        _cropCanvasRect = new Rect(Canvas.GetLeft(cr), Canvas.GetTop(cr), cr.Width, cr.Height);
                        _cropPreviewRect = cr;
                        _activePreview = null; // keep the preview rect visible; don't null it
                        ShowCropConfirmBar();
                        return;
                    }
                    else
                    {
                        _annotationCanvas.Children.Remove(cr);
                        _cropPreviewRect = null;
                    }
                    break;
            }
            _activePreview = null;
        }

        private void ClearCropSelection()
        {
            bool hasCropSelection = _activeCrop is not null || _currentTool == EditTool.Crop;
            if (!hasCropSelection) return;

            if (_activePreview is Rectangle rect)
                _annotationCanvas.Children.Remove(rect);

            if (_activePreview is Rectangle)
                _activePreview = null;
            _activeCrop = null;
            if (_currentTool == EditTool.Crop)
                SetStatus("Crop cleared - drag a new crop rectangle");
        }

        private async void ApplyCrop_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null)
            {
                TdpDialog.Show(this, "Open a PDF first.");
                return;
            }

            if (_activeCrop is null)
            {
                TdpDialog.Show(this, "Drag a crop rectangle first.", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int pageIdx = _activeCrop.PageIndex;
            if (pageIdx < 0 || pageIdx >= _doc.PageCount || !_renderDims.ContainsKey(pageIdx))
            {
                TdpDialog.Show(this, "The selected crop page is no longer available.", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
                ClearCropSelection();
                return;
            }

            string sourcePath = _currentFile;
            int selectedIdx = PageList.SelectedIndex;
            bool applyToAll = _cropApplyAllCheck?.IsChecked == true;

            _openCancellationTokenSource?.Cancel();
            _renderCancellationTokenSource?.Cancel();
            _openCancellationTokenSource?.Dispose();
            _openCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _openCancellationTokenSource.Token;

            SetFileOperationBusy(true, applyToAll ? "Applying crop to all pages..." : $"Applying crop to page {pageIdx + 1}...");
            try
            {
                CommitActiveTextBox();
                var cropRect = CanvasRectToPdfCropRect(pageIdx, _activeCrop.Bounds);
                _doc.Close();
                _doc = null;

                string croppedPath = await Task.Run(() => CropService.Apply(sourcePath, pageIdx, cropRect, applyToAll), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var result = await OpenFileCoreAsync(croppedPath, null, cancellationToken);
                await FinishOpenFileAsync(result, cancellationToken);
                if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                    PageList.SelectedIndex = selectedIdx;
                else if (PageList.Items.Count > 0)
                    PageList.SelectedIndex = 0;
                ClearCropSelection();
                MarkDirty();
                SetStatus(applyToAll ? "Crop applied to all pages" : $"Crop applied to page {pageIdx + 1}");
            }
            catch (OperationCanceledException)
            {
                SetStatus("Crop canceled");
            }
            catch (Exception ex)
            {
                try
                {
                    if (_doc is null && System.IO.File.Exists(sourcePath))
                    {
                        var restoreResult = await OpenFileCoreAsync(sourcePath, null, CancellationToken.None);
                        await FinishOpenFileAsync(restoreResult, CancellationToken.None);
                        if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                            PageList.SelectedIndex = selectedIdx;
                    }
                }
                catch { }
                SetFileOperationBusy(false);
                TdpDialog.Show(this, $"Crop failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetFileOperationBusy(false);
            }
        }

        private Rect CanvasRectToPdfCropRect(int pageIdx, Rect canvasBounds)
        {
            var (renderW, renderH) = _renderDims[pageIdx];
            var page = _doc!.Pages[pageIdx];
            var box = GetVisiblePageBox(page);
            double boxLeft = Math.Min(box.X1, box.X2);
            double boxRight = Math.Max(box.X1, box.X2);
            double boxBottom = Math.Min(box.Y1, box.Y2);
            double boxTop = Math.Max(box.Y1, box.Y2);
            double sx = (boxRight - boxLeft) / renderW;
            double sy = (boxTop - boxBottom) / renderH;

            double left = boxLeft + canvasBounds.Left * sx;
            double right = boxLeft + canvasBounds.Right * sx;
            double top = boxTop - canvasBounds.Top * sy;
            double bottom = boxTop - canvasBounds.Bottom * sy;
            return new Rect(left, bottom, right - left, top - bottom);
        }

        private static PdfRectangle GetVisiblePageBox(PdfPage page)
        {
            var crop = page.CropBox;
            if (Math.Abs(crop.X2 - crop.X1) > 0.1 && Math.Abs(crop.Y2 - crop.Y1) > 0.1)
                return crop;
            return page.MediaBox;
        }

        // ============================================================
        // Selection
        // ============================================================

        private bool HitTestAnnotation(PageAnnotation annot, Point pos, out Rect bounds)
        {
            switch (annot)
            {
                case HighlightAnnotation ha:
                    bounds = ha.Bounds;
                    return bounds.Contains(pos);

                case TextAnnotation ta:
                    var taSize = MeasureTextAnnotation(ta);
                    bounds = new Rect(ta.Position.X, ta.Position.Y, taSize.Width, taSize.Height);
                    return bounds.Contains(pos);

                case InkAnnotation ia when ia.Points.Count > 0:
                    bool near = ia.Points.Any(p =>
                        Math.Sqrt((p.X - pos.X) * (p.X - pos.X) + (p.Y - pos.Y) * (p.Y - pos.Y)) < 15);
                    if (near)
                    {
                        double minX = ia.Points.Min(p => p.X);
                        double minY = ia.Points.Min(p => p.Y);
                        double maxX = ia.Points.Max(p => p.X);
                        double maxY = ia.Points.Max(p => p.Y);
                        bounds = new Rect(minX, minY, Math.Max(maxX - minX, 4), Math.Max(maxY - minY, 4));
                        return true;
                    }
                    bounds = Rect.Empty;
                    return false;

                case TextEditAnnotation tea:
                    bounds = tea.OriginalBounds;
                    return bounds.Contains(pos);

                case ImageEditAnnotation iea:
                    bounds = iea.TargetBounds;
                    return bounds.Contains(pos);

                case SignatureAnnotation sa:
                    double sigW = sa.SourceWidth * sa.Scale;
                    double sigH = sa.SourceHeight * sa.Scale;
                    bounds = new Rect(sa.Position.X, sa.Position.Y, sigW, sigH);
                    return bounds.Contains(pos);

                case ImageAnnotation ia:
                    double iaW = ia.SourceWidth * ia.Scale;
                    double iaH = ia.SourceHeight * ia.Scale;
                    bounds = new Rect(ia.Position.X, ia.Position.Y, iaW, iaH);
                    return bounds.Contains(pos);

                case ShapeAnnotation shp:
                    bounds = shp.Bounds;
                    if (shp.Kind == ShapeKind.Line)
                    {
                        // Distance from point to line segment, threshold = max(6, strokeWidth+4) px.
                        double d = DistancePointToSegment(pos, shp.Start, shp.End);
                        return d <= Math.Max(6.0, shp.StrokeWidth + 4);
                    }
                    if (shp.Kind == ShapeKind.Ellipse)
                    {
                        // Hit-test ellipse mathematically (rectangle bounds would be too generous).
                        double rx = bounds.Width / 2.0;
                        double ry = bounds.Height / 2.0;
                        if (rx <= 0 || ry <= 0) return false;
                        double nx = (pos.X - (bounds.X + rx)) / rx;
                        double ny = (pos.Y - (bounds.Y + ry)) / ry;
                        return nx * nx + ny * ny <= 1.0;
                    }
                    return bounds.Contains(pos);

                default:
                    bounds = Rect.Empty;
                    return false;
            }
        }

        private void SelectAnnotation(PageAnnotation annot, Rect bounds)
        {
            ClearSelection();
            _selectedAnnotation = annot;
            _selectionBorder = new Border
            {
                BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                BorderThickness = new Thickness(2),
                Background = FrozenSolidColorBrush(Color.FromArgb(20, 74, 222, 128)),
                Width = bounds.Width + 8,
                Height = bounds.Height + 8,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_selectionBorder, bounds.X - 4);
            Canvas.SetTop(_selectionBorder, bounds.Y - 4);
            _annotationCanvas.Children.Add(_selectionBorder);
            if (annot is ImageEditAnnotation)
            {
                _imageResizeHandle = new Rectangle
                {
                    Width = 12,
                    Height = 12,
                    Fill = (SolidColorBrush)FindResource("AccentGreen"),
                    Stroke = Brushes.White,
                    StrokeThickness = 1,
                    Cursor = Cursors.SizeNWSE
                };
                Canvas.SetLeft(_imageResizeHandle, bounds.Right - 2);
                Canvas.SetTop(_imageResizeHandle, bounds.Bottom - 2);
                _annotationCanvas.Children.Add(_imageResizeHandle);
            }

            // Add resize handle for placed annotations (signature, image) — bottom-right corner
            if (annot is PlacedAnnotation)
            {
                const double hSize = 10;
                _resizeHandle = new Rectangle
                {
                    Width = hSize, Height = hSize,
                    Fill = (SolidColorBrush)FindResource("AccentGreen"),
                    Stroke = Brushes.White, StrokeThickness = 1,
                    Cursor = Cursors.SizeNWSE,
                    IsHitTestVisible = true
                };
                Canvas.SetLeft(_resizeHandle, bounds.X + bounds.Width - 4 - hSize / 2);
                Canvas.SetTop(_resizeHandle, bounds.Y + bounds.Height - 4 - hSize / 2);
                _annotationCanvas.Children.Add(_resizeHandle);
                string label = annot is SignatureAnnotation ? "Signature" : "Image";
                SetStatus($"{label} selected — drag corner handle to resize, Delete to remove");
            }
            else if (annot is ShapeAnnotation or HighlightAnnotation or InkAnnotation or TextAnnotation)
            {
                const double hSize = 10;
                _annotResizeHandle = new Rectangle
                {
                    Width = hSize, Height = hSize,
                    Fill = (SolidColorBrush)FindResource("AccentGreen"),
                    Stroke = Brushes.White, StrokeThickness = 1,
                    Cursor = Cursors.SizeNWSE,
                    IsHitTestVisible = true
                };
                Canvas.SetLeft(_annotResizeHandle, bounds.X + bounds.Width - 4 - hSize / 2);
                Canvas.SetTop(_annotResizeHandle, bounds.Y + bounds.Height - 4 - hSize / 2);
                _annotationCanvas.Children.Add(_annotResizeHandle);
                string kind = annot switch
                {
                    ShapeAnnotation s => s.Kind switch
                    {
                        ShapeKind.Rectangle => "rectangle",
                        ShapeKind.Ellipse => "ellipse",
                        ShapeKind.Line => "line",
                        _ => "shape"
                    },
                    HighlightAnnotation => "highlight",
                    InkAnnotation => "drawing",
                    TextAnnotation => "text box",
                    _ => "annotation"
                };
                SetStatus($"Selected {kind} — drag to move, corner handle to resize, Delete to remove");
            }
            else
            {
                SetStatus($"Selected {annot.GetType().Name.Replace("Annotation", "").ToLower()} annotation - drag to move, press Delete to remove");
            }

            // Restyle-in-place: reopen the matching settings bar bound to this annotation.
            _styleTarget = annot;
            ShowStyleBarForSelection(annot);
        }

        /// <summary>
        /// Shows the settings bar matching the selected annotation, bound to it (restyle-in-place).
        /// Placed annotations (signature/image) have no style bar.
        /// </summary>
        private void ShowStyleBarForSelection(PageAnnotation annot)
        {
            switch (annot)
            {
                case TextAnnotation:
                    HideShapeSettings(); HideDrawSettings(); ShowTextSettings();
                    break;
                case ShapeAnnotation:
                    HideTextSettings(); HideDrawSettings(); ShowShapeSettings();
                    break;
                case InkAnnotation:
                    HideTextSettings(); HideShapeSettings(); ShowDrawSettings(EditTool.Draw);
                    break;
                case HighlightAnnotation:
                    HideTextSettings(); HideShapeSettings(); ShowDrawSettings(EditTool.Highlight);
                    break;
            }
        }

        /// <summary>Re-render a page after a restyle and re-establish the selection visuals + bound bar.</summary>
        private void RestyleReselect(PageAnnotation annot)
        {
            MarkDirty();
            RenderAllAnnotations(annot.PageIndex);
            if (HitTestAnnotation(annot, GetAnyPointInside(annot), out Rect b))
                SelectAnnotation(annot, b);
        }

        /// <summary>
        /// Re-render a page after a restyle and refresh the selection visuals WITHOUT rebuilding the
        /// settings bar. Used by continuous slider drags so the slider keeps its mouse capture.
        /// </summary>
        private void RestyleLive(PageAnnotation annot)
        {
            MarkDirty();
            RenderAllAnnotations(annot.PageIndex);
            if (HitTestAnnotation(annot, GetAnyPointInside(annot), out Rect b))
                RefreshSelectionVisuals(b);
        }

        private static bool IsDescendantOf(DependencyObject child, DependencyObject parent)
        {
            var current = child;
            while (current != null)
            {
                if (current == parent) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        /// <summary>
        /// Return a representative point on/inside an annotation that the corresponding
        /// HitTest case will accept as a hit — used after move/resize to refresh the
        /// stored bounds without re-hit-testing the original cursor position.
        /// </summary>
        private static Point GetAnyPointInside(PageAnnotation annot) => annot switch
        {
            ShapeAnnotation s => s.Kind == ShapeKind.Line
                ? new Point((s.Start.X + s.End.X) * 0.5, (s.Start.Y + s.End.Y) * 0.5)
                : new Point(s.Bounds.X + s.Bounds.Width * 0.5, s.Bounds.Y + s.Bounds.Height * 0.5),
            HighlightAnnotation h => new Point(h.Bounds.X + 1, h.Bounds.Y + 1),
            InkAnnotation i when i.Points.Count > 0 => i.Points[0],
            TextAnnotation t => new Point(t.Position.X + 1, t.Position.Y + 1),
            SignatureAnnotation sg => new Point(sg.Position.X + 1, sg.Position.Y + 1),
            ImageAnnotation ig => new Point(ig.Position.X + 1, ig.Position.Y + 1),
            _ => new Point(0, 0)
        };

        /// <summary>
        /// After an in-progress move/resize, re-render replaced the canvas children.
        /// Re-add the selection border and handle on top with the new bounds.
        /// </summary>
        private void RefreshSelectionVisuals(Rect bounds)
        {
            if (_selectionBorder is not null)
            {
                _selectionBorder.Width = bounds.Width + 8;
                _selectionBorder.Height = bounds.Height + 8;
                Canvas.SetLeft(_selectionBorder, bounds.X - 4);
                Canvas.SetTop(_selectionBorder, bounds.Y - 4);
                _annotationCanvas.Children.Add(_selectionBorder);
            }
            if (_annotResizeHandle is not null)
            {
                Canvas.SetLeft(_annotResizeHandle, bounds.X + bounds.Width - 4 - _annotResizeHandle.Width / 2);
                Canvas.SetTop(_annotResizeHandle, bounds.Y + bounds.Height - 4 - _annotResizeHandle.Height / 2);
                _annotationCanvas.Children.Add(_annotResizeHandle);
            }
        }

        private void ClearSelection()
        {
            if (_selectionBorder is not null)
            {
                _annotationCanvas.Children.Remove(_selectionBorder);
                _selectionBorder = null;
            }
            if (_imageResizeHandle is not null)
            {
                _annotationCanvas.Children.Remove(_imageResizeHandle);
                _imageResizeHandle = null;
            }
            if (_resizeHandle is not null)
            {
                _annotationCanvas.Children.Remove(_resizeHandle);
                _resizeHandle = null;
            }
            if (_annotResizeHandle is not null)
            {
                _annotationCanvas.Children.Remove(_annotResizeHandle);
                _annotResizeHandle = null;
            }
            _isResizingSig = false;
            _resizeSigAnnot = null;
            _isDraggingAnnot = false;
            _dragAnnot = null;
            _isMovingAnnot = false;
            _movingAnnot = null;
            _moveOriginalGeom = null;
            _isResizingAnnot = false;
            _resizingAnnot = null;
            _resizeOriginalGeom = null;
            _selectedAnnotation = null;

            // Tear down any restyle-in-place binding. In Select mode the bound bar exists only for the
            // selection, so hide it; in other tools the visible bar belongs to the active tool — leave it.
            _styleTarget = null;
            if (_currentTool == EditTool.Select)
            {
                HideTextSettings();
                HideShapeSettings();
                HideDrawSettings();
            }
        }

        private void DeleteSelected()
        {
            if (_selectedAnnotation is null) return;
            int pageIdx = _selectedAnnotation.PageIndex;
            if (_annotations.ContainsKey(pageIdx))
            {
                PushPageSnapshot(pageIdx);
                _annotations[pageIdx].Remove(_selectedAnnotation);
            }
            ClearSelection();
            RenderAllAnnotations(pageIdx);
            MarkDirty();
            SetStatus("Deleted selected annotation");
        }

        private void SelectAllText()
        {
            if (_currentFile is null) return;
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;

            try
            {
                using var pigDoc = PdfPigDoc.Open(_currentFile);
                if (pageIdx >= pigDoc.NumberOfPages) return;
                var page = pigDoc.GetPage(pageIdx + 1);
                _selectedText = WordsToText(page.GetWords());
                if (string.IsNullOrWhiteSpace(_selectedText))
                {
                    SetStatus("No text found on this page");
                    return;
                }
                Clipboard.SetText(_selectedText);
                // Visual feedback: highlight entire canvas
                ClearTextSelection();
                _selectRect = new Rectangle
                {
                    Fill = FrozenSolidColorBrush(Color.FromArgb(30, 74, 130, 255)),
                    Stroke = FrozenSolidColorBrush(Color.FromArgb(80, 74, 130, 255)),
                    StrokeThickness = 1,
                    Width = _annotationCanvas.Width,
                    Height = _annotationCanvas.Height,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(_selectRect, 0);
                Canvas.SetTop(_selectRect, 0);
                _annotationCanvas.Children.Add(_selectRect);
                SetStatus($"Selected all text - copied to clipboard");
            }
            catch (Exception ex)
            {
                SetStatus($"Select all error: {ex.Message}");
            }
        }

        private void CopySelectedText()
        {
            if (!string.IsNullOrEmpty(_selectedText))
            {
                Clipboard.SetText(_selectedText);
                SetStatus($"Copied to clipboard");
            }
            else
            {
                SetStatus("No text selected - drag to select text");
            }
        }

        private void ClearTextSelection()
        {
            if (_selectRect is not null)
            {
                _annotationCanvas.Children.Remove(_selectRect);
                _selectRect = null;
            }
            _selectedText = null;
        }

        private void ExtractTextFromRegion(int pageIdx, Rect canvasBounds)
        {
            if (_currentFile is null || pageIdx < 0) return;
            if (!_renderDims.ContainsKey(pageIdx)) return;

            try
            {
                var (renderW, renderH) = _renderDims[pageIdx];

                using var pigDoc = PdfPigDoc.Open(_currentFile);
                if (pageIdx >= pigDoc.NumberOfPages) return;
                var page = pigDoc.GetPage(pageIdx + 1); // PdfPig is 1-based

                double pdfW = page.Width;
                double pdfH = page.Height;
                double sx = pdfW / renderW;
                double sy = pdfH / renderH;

                // Convert canvas rect to PDF coordinates (flip Y - PDF origin is bottom-left)
                double pdfLeft = canvasBounds.Left * sx;
                double pdfRight = canvasBounds.Right * sx;
                double pdfTop = pdfH - (canvasBounds.Top * sy);
                double pdfBottom = pdfH - (canvasBounds.Bottom * sy);
                // pdfTop > pdfBottom because of Y flip
                double pdfMinY = Math.Min(pdfTop, pdfBottom);
                double pdfMaxY = Math.Max(pdfTop, pdfBottom);

                var words = page.GetWords()
                    .Where(w =>
                    {
                        var bb = w.BoundingBox;
                        double cx = (bb.Left + bb.Right) / 2;
                        double cy = (bb.Bottom + bb.Top) / 2;
                        return cx >= pdfLeft && cx <= pdfRight && cy >= pdfMinY && cy <= pdfMaxY;
                    })
                    .ToList();

                if (words.Count == 0)
                {
                    SetStatus("No text found in selection");
                    ClearTextSelection();
                    return;
                }

                _selectedText = WordsToText(words);

                Clipboard.SetText(_selectedText);
                int wordCount = words.Count;
                SetStatus($"Copied {wordCount} word(s) to clipboard");
            }
            catch (Exception ex)
            {
                SetStatus($"Text extraction error: {ex.Message}");
                ClearTextSelection();
            }
        }

        /// <summary>
        /// Converts a collection of PdfPig words to a properly ordered string.
        /// Sorts top-to-bottom then left-to-right, groups into lines using a
        /// dynamic threshold (~40% of average word height) so words at slightly
        /// different baselines still land on the correct line.
        /// </summary>
        private static string WordsToText(IEnumerable<UglyToad.PdfPig.Content.Word> source)
        {
            var words = source
                .OrderByDescending(w => w.BoundingBox.Top)
                .ThenBy(w => w.BoundingBox.Left)
                .ToList();
            if (words.Count == 0) return string.Empty;

            // Dynamic threshold: 40% of average word height, minimum 4 PDF units
            double avgH = words.Average(w => w.BoundingBox.Height);
            double thresh = Math.Max(4.0, avgH * 0.4);

            var lines = new List<List<UglyToad.PdfPig.Content.Word>>();
            double lineY = double.MaxValue;
            foreach (var w in words)
            {
                if (Math.Abs(w.BoundingBox.Top - lineY) > thresh)
                {
                    lines.Add([]);
                    lineY = w.BoundingBox.Top;
                }
                lines[^1].Add(w);
            }

            // Re-sort each line by X in case the top-Y sort caused any grouping
            // to pull words into the wrong order within a line.
            return string.Join("\n", lines.Select(l =>
                string.Join(" ", l.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text))));
        }

        // ============================================================
        // Search (Ctrl+F)
        // ============================================================

        private void ToggleSearchBar()
        {
            if (_searchBar is not null && _searchBar.Visibility == Visibility.Visible)
            {
                CloseSearchBar();
                return;
            }
            ShowSearchBar();
        }

        private void ShowSearchBar()
        {
            if (_searchBar is null)
            {
                // Build search bar programmatically and inject into the preview area grid
                _searchBox = new TextBox
                {
                    Width = 260,
                    Height = 28,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 13,
                    Background = BrushResource("BgPanel"),
                    Foreground = BrushResource("TextPrimary"),
                    BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                AutomationProperties.SetName(_searchBox, "Search text");
                AutomationProperties.SetHelpText(_searchBox, "Enter text to find in the current PDF. Press Enter for next result and Shift Enter for previous result.");
                _searchBox.KeyDown += SearchBox_KeyDown;
                _searchBox.TextChanged += SearchBox_TextChanged;

                _searchStatus = new TextBlock
                {
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                AutomationProperties.SetName(_searchStatus, "Search status");
                AutomationProperties.SetHelpText(_searchStatus, "Search result status");
                AutomationProperties.SetLiveSetting(_searchStatus, AutomationLiveSetting.Polite);

                var closeBtn = new Button
                {
                    Content = "\ue711",  // MDL2 Cancel glyph \u2014 matches ToolbarButton font
                    Margin = new Thickness(4, 0, 0, 0),
                    Style = (Style)FindResource("ToolbarButton"),
                    ToolTip = "Close search (Esc)"
                };
                AutomationProperties.SetName(closeBtn, "Close search");
                AutomationProperties.SetHelpText(closeBtn, "Close the search bar. Shortcut Escape.");
                closeBtn.Click += (s, e) => CloseSearchBar();

                var searchIcon = new TextBlock
                {
                    Text = "",  // Segoe MDL2 Search / magnifying glass
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    IsHitTestVisible = false
                };

                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(8)
                };
                panel.Children.Add(searchIcon);
                panel.Children.Add(_searchBox);
                panel.Children.Add(_searchStatus);
                panel.Children.Add(closeBtn);

                _searchBar = new Border
                {
                    Background = BrushResource("BgDark"),
                    BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    CornerRadius = new CornerRadius(0, 0, 4, 4),
                    Padding = new Thickness(4),
                    Child = panel,
                    Margin = new Thickness(0, 0, 16, 0)
                };

                // Add to the preview area grid (parent of ScrollViewer)
                var previewGrid = PagePreviewPanel.Parent as Grid;
                if (previewGrid is not null)
                {
                    Panel.SetZIndex(_searchBar, 100);
                    previewGrid.Children.Add(_searchBar);
                }
            }

            _searchBar.Visibility = Visibility.Visible;
            _searchBox!.Text = "";
            if (_searchStatus != null) _searchStatus.Text = "Enter = next  Shift+Enter = prev";
            _searchBox.Focus();
            Keyboard.Focus(_searchBox);
        }

        private void CloseSearchBar()
        {
            if (_searchBar is not null)
                _searchBar.Visibility = Visibility.Collapsed;
            ClearSearchHighlights();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                    SearchPrevResult();
                else
                    SearchNextResult();
                e.Handled = true;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = _searchBox?.Text ?? "";
            if (text.Length >= 2)
                RunSearch(text);
            else
            {
                ClearSearchHighlights();
                _allSearchRects.Clear();
                _searchResultPages.Clear();
                _searchPageCursor = -1;
            }
        }

        private void RunSearch(string query)
        {
            ClearSearchHighlights();
            _allSearchRects.Clear();
            _searchResultPages.Clear();
            _searchPageCursor = -1;

            if (string.IsNullOrWhiteSpace(query) || _currentFile is null)
            {
                if (_searchStatus != null) _searchStatus.Text = "";
                return;
            }

            try
            {
                string lowerQuery = query.ToLowerInvariant();
                int totalHits = 0;

                using var pigDoc = PdfPigDoc.Open(_currentFile);
                for (int pi = 0; pi < pigDoc.NumberOfPages; pi++)
                {
                    var page = pigDoc.GetPage(pi + 1);
                    var hits = FindMatchesOnPage(page, lowerQuery);
                    if (hits.Count > 0)
                    {
                        _allSearchRects[pi] = hits;
                        _searchResultPages.Add(pi);
                        totalHits += hits.Count;
                    }
                }

                if (_searchResultPages.Count == 0)
                {
                    if (_searchStatus != null) _searchStatus.Text = "No matches";
                    return;
                }

                // Start from current page or the first page with results
                int startPage = PageList.SelectedIndex;
                _searchPageCursor = _searchResultPages.FindIndex(p => p >= startPage);
                if (_searchPageCursor < 0) _searchPageCursor = 0;

                if (_searchStatus != null)
                    _searchStatus.Text = totalHits == 1
                        ? $"1 match ({_searchResultPages.Count} page)"
                        : $"{totalHits} matches ({_searchResultPages.Count} page{(_searchResultPages.Count != 1 ? "s" : "")})";

                int targetPage = _searchResultPages[_searchPageCursor];
                if (PageList.SelectedIndex != targetPage)
                    PageList.SelectedIndex = targetPage;  // triggers SelectionChanged -> HighlightSearchResultsOnCurrentPage
                else
                    HighlightSearchResultsOnCurrentPage();
            }
            catch
            {
                if (_searchStatus != null) _searchStatus.Text = "Search error";
            }
        }

        private static List<(double left, double bottom, double right, double top)> FindMatchesOnPage(
            UglyToad.PdfPig.Content.Page page, string lowerQuery)
        {
            var result = new List<(double left, double bottom, double right, double top)>();
            var words = page.GetWords().ToList();

            for (int i = 0; i < words.Count; i++)
            {
                if (words[i].Text.ToLowerInvariant().Contains(lowerQuery))
                {
                    var bb = words[i].BoundingBox;
                    result.Add((bb.Left, bb.Bottom, bb.Right, bb.Top));
                    continue;
                }

                // Multi-word match
                string combined = words[i].Text;
                for (int j = i + 1; j < words.Count && combined.Length < lowerQuery.Length + 20; j++)
                {
                    combined += " " + words[j].Text;
                    if (combined.ToLowerInvariant().Contains(lowerQuery))
                    {
                        double minX = double.MaxValue, minY = double.MaxValue;
                        double maxX = double.MinValue, maxY = double.MinValue;
                        for (int k = i; k <= j; k++)
                        {
                            var wbb = words[k].BoundingBox;
                            minX = Math.Min(minX, wbb.Left);
                            minY = Math.Min(minY, wbb.Bottom);
                            maxX = Math.Max(maxX, wbb.Right);
                            maxY = Math.Max(maxY, wbb.Top);
                        }
                        result.Add((minX, minY, maxX, maxY));
                        break;
                    }
                }
            }
            return result;
        }

        private void HighlightSearchResultsOnCurrentPage()
        {
            ClearSearchHighlights();
            int curPage = PageList.SelectedIndex;
            if (!_allSearchRects.ContainsKey(curPage)) return;
            if (!_renderDims.ContainsKey(curPage)) return;

            var (renderW, renderH) = _renderDims[curPage];

            try
            {
                using var pigDoc = PdfPigDoc.Open(_currentFile!);
                var page = pigDoc.GetPage(curPage + 1);
                double pdfW = page.Width;
                double pdfH = page.Height;
                double sx = renderW / pdfW;
                double sy = renderH / pdfH;

                foreach (var (left, bottom, right, top) in _allSearchRects[curPage])
                    AddSearchHighlight(left, bottom, right, top, sx, sy, renderH);
            }
            catch { }
        }

        private void SearchNextResult()
        {
            if (_searchResultPages.Count == 0) return;
            _searchPageCursor = (_searchPageCursor + 1) % _searchResultPages.Count;
            int targetPage = _searchResultPages[_searchPageCursor];
            if (PageList.SelectedIndex != targetPage)
                PageList.SelectedIndex = targetPage;
            else
                HighlightSearchResultsOnCurrentPage();
        }

        private void SearchPrevResult()
        {
            if (_searchResultPages.Count == 0) return;
            _searchPageCursor = (_searchPageCursor - 1 + _searchResultPages.Count) % _searchResultPages.Count;
            int targetPage = _searchResultPages[_searchPageCursor];
            if (PageList.SelectedIndex != targetPage)
                PageList.SelectedIndex = targetPage;
            else
                HighlightSearchResultsOnCurrentPage();
        }

        private void AddSearchHighlight(double left, double bottom, double right, double top,
            double sx, double sy, double renderH)
        {
            double cx = left  * sx;
            double cy = renderH - (top * sy);
            double cw = (right - left) * sx;
            double ch = (top - bottom) * sy;
            var rect = new Rectangle
            {
                Fill = FrozenSolidColorBrush(Color.FromArgb(80, 255, 165, 0)),
                Stroke = FrozenSolidColorBrush(Color.FromArgb(160, 255, 165, 0)),
                StrokeThickness = 1,
                Width = Math.Max(cw, 4),
                Height = Math.Max(ch, 4),
                IsHitTestVisible = false,
                Tag = "SearchHighlight"
            };
            Canvas.SetLeft(rect, cx);
            Canvas.SetTop(rect, cy);
            _annotationCanvas.Children.Add(rect);
        }

        private void ClearSearchHighlights()
        {
            var toRemove = _annotationCanvas.Children.OfType<Rectangle>()
                .Where(r => r.Tag is string s && s == "SearchHighlight").ToList();
            foreach (var r in toRemove)
                _annotationCanvas.Children.Remove(r);
            if (_searchStatus is not null)
                _searchStatus.Text = "";
        }

        // ============================================================
        // Inline text editing (double-click)
        // ============================================================

        private void EditTextAtPosition(Point canvasPos, int pageIdx)
        {
            if (_currentFile is null || !_renderDims.ContainsKey(pageIdx)) return;
            ClearSelection();

            // Commit any existing edit first
            if (_activeTextBox is not null)
            {
                CommitActiveTextBox();
                return;
            }

            // Re-edit an already-committed TextEditAnnotation without re-reading the PDF.
            // Without this check, a second double-click would read the original file, produce
            // a duplicate whiteout+text layer, and cause the "overlapping quasi-duplicates" bug.
            if (_annotations.TryGetValue(pageIdx, out var existingPage))
            {
                var existingEdit = existingPage.OfType<TextEditAnnotation>()
                    .FirstOrDefault(a => a.OriginalBounds.Contains(canvasPos));
                if (existingEdit is not null)
                {
                    var reb = existingEdit.OriginalBounds;
                    var retb = new TextBox
                    {
                        Text = existingEdit.NewContent,
                        Background = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
                        Foreground = Brushes.Black,
                        BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                        BorderThickness = new Thickness(2),
                        FontFamily = new FontFamily(existingEdit.FontName),
                        FontSize = Math.Max(existingEdit.FontSize, 10),
                        MinWidth = Math.Max(reb.Width + 20, 100),
                        Height = Math.Max(reb.Height + 12, 24),
                        Padding = new Thickness(2, 0, 2, 0),
                        VerticalContentAlignment = VerticalAlignment.Center,
                        AcceptsReturn = false,
                        Tag = new TextEditContext
                        {
                            PageIndex = pageIdx,
                            OriginalText = existingEdit.OriginalContent,
                            CanvasBounds = reb,
                            Position = existingEdit.Position,
                            FontSize = existingEdit.FontSize,
                            FontName = existingEdit.FontName,
                            ExistingAnnotation = existingEdit
                        }
                    };
                    Canvas.SetLeft(retb, reb.X);
                    Canvas.SetTop(retb, reb.Y);
                    _annotationCanvas.Children.Add(retb);
                    _activeTextBox = retb;
                    var rewo = new Rectangle
                    {
                        Fill = Brushes.White,
                        Width = reb.Width + 4,
                        Height = reb.Height + 4,
                        IsHitTestVisible = false,
                        Tag = "EditWhiteout"
                    };
                    Canvas.SetLeft(rewo, reb.X - 2);
                    Canvas.SetTop(rewo, reb.Y - 2);
                    _annotationCanvas.Children.Insert(_annotationCanvas.Children.IndexOf(retb), rewo);
                    retb.KeyDown += EditTextBox_KeyDown;
                    retb.Loaded += (s, ev) => { retb.Focus(); Keyboard.Focus(retb); retb.SelectAll(); retb.LostFocus += EditTextBox_LostFocus; };
                    SetStatus("Re-editing text — Enter to save, Escape to cancel");
                    return;
                }
            }

            try
            {
                var (renderW, renderH) = _renderDims[pageIdx];
                var hit = _contentEditor.FindTextRunAt(_currentFile, pageIdx, canvasPos, renderW, renderH);
                if (hit is null) { SetStatus("No text found at this position"); return; }

                // Show editable TextBox over the line
                var tb = new TextBox
                {
                    Text = hit.Text,
                    Background = FrozenSolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
                    Foreground = Brushes.Black,
                    BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                    BorderThickness = new Thickness(2),
                    FontFamily = new FontFamily(hit.FontName),
                    FontSize = hit.FontSize,
                    MinWidth = Math.Max(hit.CanvasBounds.Width + 20, 100),
                    Height = Math.Max(hit.CanvasBounds.Height + 12, 24),
                    Padding = new Thickness(2, 0, 2, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    AcceptsReturn = false,
                    Tag = new TextEditContext
                    {
                        PageIndex = pageIdx,
                        OriginalText = hit.Text,
                        CanvasBounds = hit.CanvasBounds,
                        Position = hit.Position,
                        FontSize = hit.FontSize,
                        FontName = hit.FontName
                    }
                };
                Canvas.SetLeft(tb, hit.CanvasBounds.X);
                Canvas.SetTop(tb, hit.CanvasBounds.Y);
                _annotationCanvas.Children.Add(tb);
                _activeTextBox = tb;

                // Show white-out behind the edit box so original text is hidden
                var whiteout = new Rectangle
                {
                    Fill = Brushes.White,
                    Width = hit.CanvasBounds.Width + 4,
                    Height = hit.CanvasBounds.Height + 4,
                    IsHitTestVisible = false,
                    Tag = "EditWhiteout"
                };
                Canvas.SetLeft(whiteout, hit.CanvasBounds.X - 2);
                Canvas.SetTop(whiteout, hit.CanvasBounds.Y - 2);
                int tbIdx = _annotationCanvas.Children.IndexOf(tb);
                _annotationCanvas.Children.Insert(tbIdx, whiteout);

                tb.KeyDown += EditTextBox_KeyDown;
                tb.Loaded += (s, ev) =>
                {
                    tb.Focus();
                    Keyboard.Focus(tb);
                    tb.SelectAll();
                    tb.LostFocus += EditTextBox_LostFocus;
                };

                SetStatus("Editing text - Enter to save, Escape to cancel");
            }
            catch (Exception ex)
            {
                SetStatus($"Text edit error: {ex.Message}");
            }
        }

        /// <summary>Context data attached to an inline text edit TextBox via Tag.</summary>
        private class TextEditContext
        {
            public int PageIndex { get; set; }
            public string OriginalText { get; set; } = "";
            public Rect CanvasBounds { get; set; }
            public Point Position { get; set; }
            public double FontSize { get; set; }
            public string FontName { get; set; } = "Segoe UI";
            /// <summary>Non-null when re-editing an already-committed annotation; update in place instead of adding a new one.</summary>
            public TextEditAnnotation? ExistingAnnotation { get; set; }
        }

        private void EditTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CancelTextEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                CommitTextEdit();
                e.Handled = true;
            }
        }

        private void EditTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_activeTextBox is not null && _activeTextBox.Tag is TextEditContext)
            {
                Dispatcher.BeginInvoke(new Action(CommitTextEdit),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void CancelTextEdit()
        {
            if (_activeTextBox is null) return;
            var tb = _activeTextBox;
            _activeTextBox = null;
            _annotationCanvas.Children.Remove(tb);
            // Remove the whiteout rectangle
            var whiteout = _annotationCanvas.Children.OfType<Rectangle>()
                .FirstOrDefault(r => r.Tag is string s && s == "EditWhiteout");
            if (whiteout is not null)
                _annotationCanvas.Children.Remove(whiteout);
            SetStatus("Text edit cancelled");
        }

        private void CommitTextEdit()
        {
            if (_activeTextBox is null || _activeTextBox.Tag is not TextEditContext ctx) return;
            var tb = _activeTextBox;
            _activeTextBox = null;
            string newText = tb.Text.Trim();
            _annotationCanvas.Children.Remove(tb);

            // Remove the whiteout rectangle
            var whiteout = _annotationCanvas.Children.OfType<Rectangle>()
                .FirstOrDefault(r => r.Tag is string s && s == "EditWhiteout");
            if (whiteout is not null)
                _annotationCanvas.Children.Remove(whiteout);

            if (string.IsNullOrEmpty(newText) || newText == ctx.OriginalText)
            {
                SetStatus(newText == ctx.OriginalText ? "No changes made" : "Text edit cancelled (empty)");
                return;
            }

            if (ctx.ExistingAnnotation is not null)
            {
                // Update the existing annotation in place — avoids duplicate whiteout layers
                PushPageSnapshot(ctx.ExistingAnnotation.PageIndex);
                ctx.ExistingAnnotation.NewContent = newText;
                MarkDirty();
            }
            else
            {
                var edit = new TextEditAnnotation
                {
                    PageIndex = ctx.PageIndex,
                    OriginalBounds = ctx.CanvasBounds,
                    Position = ctx.Position,
                    NewContent = newText,
                    OriginalContent = ctx.OriginalText,
                    FontSize = ctx.FontSize,
                    FontName = ctx.FontName
                };
                AddAnnotation(edit);
            }
            RenderAllAnnotations(ctx.PageIndex);
            SetStatus($"Text edited: \"{ctx.OriginalText}\" -> \"{newText}\"");
        }

        private void EditImageAtPosition(Point canvasPos, int pageIdx)
        {
            if (_currentFile is null || !_renderDims.ContainsKey(pageIdx)) return;

            if (_annotations.TryGetValue(pageIdx, out var pageAnnots))
            {
                for (int i = pageAnnots.Count - 1; i >= 0; i--)
                {
                    if (pageAnnots[i] is ImageEditAnnotation existing && existing.TargetBounds.Contains(canvasPos))
                    {
                        SelectAnnotation(existing, existing.TargetBounds);
                        ShowImageEditMenu(existing);
                        return;
                    }
                }
            }

            var (renderW, renderH) = _renderDims[pageIdx];
            var hit = _contentEditor.FindImageAt(_currentFile, pageIdx, canvasPos, renderW, renderH);
            if (hit is null)
            {
                SetStatus("No image found at this position");
                return;
            }

            var edit = new ImageEditAnnotation
            {
                PageIndex = pageIdx,
                OriginalBounds = hit.CanvasBounds,
                TargetBounds = hit.CanvasBounds,
                OriginalImageData = CapturePageImageRegion(hit.CanvasBounds)
            };
            AddAnnotation(edit);
            RenderAllAnnotations(pageIdx);
            SelectAnnotation(edit, edit.TargetBounds);
            ShowImageEditMenu(edit);
            SetStatus("Image selected - replace, delete, or drag the green handle to resize");
        }

        private string? CapturePageImageRegion(Rect bounds)
        {
            if (PageImage.Source is not BitmapSource source) return null;

            int x = Math.Max(0, (int)Math.Floor(bounds.X));
            int y = Math.Max(0, (int)Math.Floor(bounds.Y));
            int right = Math.Min(source.PixelWidth, (int)Math.Ceiling(bounds.Right));
            int bottom = Math.Min(source.PixelHeight, (int)Math.Ceiling(bounds.Bottom));
            if (right <= x || bottom <= y) return null;

            var crop = new CroppedBitmap(source, new Int32Rect(x, y, right - x, bottom - y));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(crop));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return Convert.ToBase64String(ms.ToArray());
        }

        private void ShowImageEditMenu(ImageEditAnnotation edit)
        {
            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem("Replace Image...", (s, e) => ReplaceImageEdit(edit)));
            menu.Items.Add(MakeMenuItem("Delete Image", (s, e) => DeleteImageEdit(edit)));
            menu.Items.Add(MakeMenuItem("Reset Size", (s, e) => ResetImageEditSize(edit)));
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Resize: drag the green handle" });
            menu.PlacementTarget = _annotationCanvas;
            menu.IsOpen = true;
        }

        private void ReplaceImageEdit(ImageEditAnnotation edit)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
                Title = "Select replacement image"
            };
            if (dlg.ShowDialog() != true) return;

            PushPageSnapshot(edit.PageIndex);
            edit.ReplacementImagePath = dlg.FileName;
            edit.IsDeleted = false;
            RenderAllAnnotations(edit.PageIndex);
            SelectAnnotation(edit, edit.TargetBounds);
            MarkDirty();
            SetStatus("Replacement image selected - save to apply white-out + overdraw");
        }

        private void DeleteImageEdit(ImageEditAnnotation edit)
        {
            PushPageSnapshot(edit.PageIndex);
            edit.IsDeleted = true;
            RenderAllAnnotations(edit.PageIndex);
            SelectAnnotation(edit, edit.TargetBounds);
            MarkDirty();
            SetStatus("Image marked for deletion - save to apply white-out");
        }

        private void ResetImageEditSize(ImageEditAnnotation edit)
        {
            PushPageSnapshot(edit.PageIndex);
            edit.TargetBounds = edit.OriginalBounds;
            RenderAllAnnotations(edit.PageIndex);
            SelectAnnotation(edit, edit.TargetBounds);
            MarkDirty();
            SetStatus("Image size reset");
        }

        private void ResizeImageEditPreview(Point pos)
        {
            if (_resizingImageEdit is null) return;

            double newW = Math.Max(8, _imageResizeOriginalBounds.Width + (pos.X - _imageResizeStart.X));
            double newH = Math.Max(8, _imageResizeOriginalBounds.Height + (pos.Y - _imageResizeStart.Y));
            _resizingImageEdit.TargetBounds = new Rect(_imageResizeOriginalBounds.X, _imageResizeOriginalBounds.Y, newW, newH);

            if (_selectionBorder is not null)
            {
                _selectionBorder.Width = newW + 8;
                _selectionBorder.Height = newH + 8;
            }
            if (_imageResizeHandle is not null)
            {
                Canvas.SetLeft(_imageResizeHandle, _resizingImageEdit.TargetBounds.Right - 2);
                Canvas.SetTop(_imageResizeHandle, _resizingImageEdit.TargetBounds.Bottom - 2);
            }
        }

        // ============================================================
        // Text box handling
        // ============================================================

        /// <summary>
        /// If a placed <see cref="TextAnnotation"/> lies under <paramref name="pos"/>, re-open it in the
        /// in-place editor (topmost first) and return true; otherwise return false.
        /// </summary>
        private bool TryReeditPlacedText(Point pos, int pageIdx)
        {
            if (pageIdx < 0 || !_annotations.TryGetValue(pageIdx, out var list)) return false;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is TextAnnotation ta && HitTestAnnotation(ta, pos, out _))
                {
                    PlaceTextBox(ta.Position, pageIdx, ta);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Default wrap width (canvas px) for a newly placed text box.</summary>
        private const double DefaultTextBoxWidth = 220;

        /// <summary>Context attached to a placed-text editing TextBox via its Tag.</summary>
        private sealed class PlacedTextContext
        {
            public int PageIndex { get; init; }
            /// <summary>Non-null when re-editing an existing box: it was pulled from the list at edit-start and is restored on cancel.</summary>
            public TextAnnotation? Existing { get; init; }
        }

        /// <summary>
        /// Opens the in-place editing TextBox for a new text annotation, or (when <paramref name="existing"/>
        /// is supplied) re-opens an already-placed one seeded with its content/size/colour/width/fill.
        /// </summary>
        private void PlaceTextBox(Point pos, int pageIdx, TextAnnotation? existing = null)
        {
            double width = DefaultTextBoxWidth;
            if (existing is not null)
            {
                // Adopt the box's style so the box (and the Text-tool settings bar, if visible) reflect it.
                _textColor = existing.GetColor();
                _textFontSize = existing.FontSize;
                _textWhiteout = existing.HasFill;
                if (existing.HasFill) _textFillColor = existing.GetFillColor();
                if (existing.Width > 0) width = existing.Width;

                // Pull the original out of the model for the duration of the edit (restored on cancel).
                // The snapshot captures the pre-edit state so undo restores it whichever way the edit ends.
                PushPageSnapshot(pageIdx);
                if (_annotations.TryGetValue(pageIdx, out var l0)) l0.Remove(existing);
                RenderAllAnnotations(pageIdx);
                if (_currentTool == EditTool.Text && _textSettingsBar is not null) ShowTextSettings();
            }

            var tb = new TextBox
            {
                Foreground = new SolidColorBrush(_textColor),
                BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = _textFontSize,
                Width = width,
                MinHeight = existing is not null && existing.Height > 24 ? existing.Height : 24,
                Padding = new Thickness(2),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Text = existing?.Content ?? "",
                Tag = new PlacedTextContext { PageIndex = pageIdx, Existing = existing }
            };
            tb.Background = _textWhiteout
                ? FrozenSolidColorBrush(_textFillColor)
                : FrozenSolidColorBrush(Color.FromArgb(230, 255, 255, 255));
            AutomationProperties.SetName(tb, "Annotation text");
            AutomationProperties.SetHelpText(tb, "Type annotation text. Press Enter to save or Escape to cancel.");
            Canvas.SetLeft(tb, pos.X);
            Canvas.SetTop(tb, pos.Y);
            _annotationCanvas.Children.Add(tb);
            _activeTextBox = tb;
            tb.KeyDown += TextBox_KeyDown;
            // Defer focus until the TextBox is actually rendered
            tb.Loaded += (s, e) =>
            {
                tb.Focus();
                Keyboard.Focus(tb);
                if (existing is not null) tb.SelectAll();
                tb.LostFocus += TextBox_LostFocus;
            };
        }

        /// <summary>Reflects the current whiteout setting onto the live placed-text editing box, if any.</summary>
        private void UpdateActiveTextBoxFill()
        {
            if (_activeTextBox is null || _activeTextBox.Tag is not PlacedTextContext) return;
            _activeTextBox.Background = _textWhiteout
                ? FrozenSolidColorBrush(_textFillColor)
                : FrozenSolidColorBrush(Color.FromArgb(230, 255, 255, 255));
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CancelActiveTextBox();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                CommitActiveTextBox();
                e.Handled = true;
            }
        }

        private void TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_activeTextBox is null) return;
            // Commit on blur when there's content, or always when re-editing (so clearing the box deletes it).
            bool reediting = _activeTextBox.Tag is PlacedTextContext { Existing: not null };
            if (reediting || !string.IsNullOrWhiteSpace(_activeTextBox.Text))
            {
                Dispatcher.BeginInvoke(new Action(CommitActiveTextBox),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        /// <summary>Cancels the active placed-text edit, restoring the original annotation if re-editing.</summary>
        private void CancelActiveTextBox()
        {
            if (_activeTextBox is null) return;
            var tb = _activeTextBox;
            _activeTextBox = null;
            _annotationCanvas.Children.Remove(tb);
            if (tb.Tag is PlacedTextContext { Existing: { } original } ctx)
            {
                if (!_annotations.TryGetValue(ctx.PageIndex, out var list))
                    _annotations[ctx.PageIndex] = list = [];
                list.Add(original);
                DropTopSnapshotIfFor(ctx.PageIndex);   // no net change — discard the edit-start snapshot
                RenderAllAnnotations(ctx.PageIndex);
            }
        }

        private void CommitActiveTextBox()
        {
            if (_activeTextBox is null) return;
            // If it's an inline (existing-PDF-text) edit, use the dedicated commit path
            if (_activeTextBox.Tag is TextEditContext)
            {
                CommitTextEdit();
                return;
            }
            var tb = _activeTextBox;
            _activeTextBox = null;

            var ctx = tb.Tag as PlacedTextContext;
            int pageIdx = ctx?.PageIndex ?? (tb.Tag is int idx ? idx : PageList.SelectedIndex);
            bool reediting = ctx?.Existing is not null;   // original already removed + snapshot taken

            string content = tb.Text.Trim();
            double x = Canvas.GetLeft(tb);
            double y = Canvas.GetTop(tb);
            double width = tb.Width;
            double height = tb.ActualHeight;

            _annotationCanvas.Children.Remove(tb);

            if (!string.IsNullOrEmpty(content))
            {
                var ta = new TextAnnotation
                {
                    PageIndex = pageIdx,
                    Position = new Point(x, y),
                    Content = content,
                    FontSize = tb.FontSize,
                    Width = double.IsNaN(width) || width <= 0 ? 0 : width,
                    HasFill = _textWhiteout
                };
                ta.Height = ta.Width > 0 && height > 0 ? height : 0;
                ta.SetColor(tb.Foreground is SolidColorBrush scb ? scb.Color : Colors.Black);
                if (_textWhiteout) ta.SetFillColor(_textFillColor);

                if (reediting)
                {
                    if (!_annotations.TryGetValue(pageIdx, out var list))
                        _annotations[pageIdx] = list = [];
                    list.Add(ta);
                    MarkDirty();
                    RenderAllAnnotations(pageIdx);
                }
                else
                {
                    AddAnnotation(ta);        // pushes its own snapshot
                    RenderTextAnnotation(ta);
                }
            }
            else if (reediting)
            {
                // Box emptied while re-editing: original was already removed at edit-start → commit as a delete.
                MarkDirty();
                RenderAllAnnotations(pageIdx);
            }
        }

        // ============================================================
        // Keyboard shortcuts
        // ============================================================

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            // Don't intercept keys when typing in a TextBox
            if (_activeTextBox is not null && _activeTextBox.IsFocused) return;

            // Standard shortcuts (Ctrl+N/O/S/W/Z, Ctrl+Shift+S, Ctrl+P, Ctrl+F, F1, Alt+F/E/V/T/H)
            // are routed via CommandBindings and the Menu's access keys — no need to intercept
            // them here. We still handle the genuinely context-sensitive keys below.

            // Full-screen (F11) and Document Info (F12) toggles. Esc leaves full-screen first,
            // before any other Esc behaviour (search bar / shortcut overlay), so the very first
            // Esc always drops the user back to the normal windowed layout.
            if (e.Key == Key.F11)
            {
                ToggleFullScreen();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && _fullScreen)
            {
                ToggleFullScreen();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F12)
            {
                ShowDocumentInfoDialog();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.O && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && _doc is not null)
            {
                OcrPageToClipboard(Math.Max(0, PageList.SelectedIndex));
                e.Handled = true;
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CopySelectedText();
                e.Handled = true;
            }
            else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SelectAllText();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _searchBar is not null && _searchBar.Visibility == Visibility.Visible)
            {
                CloseSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && ShortcutOverlay.Visibility == Visibility.Visible)
            {
                ShortcutOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            else if (e.Key == Key.OemQuestion && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ShortcutOverlay.Visibility = ShortcutOverlay.Visibility == Visibility.Visible
                    ? Visibility.Collapsed : Visibility.Visible;
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && _selectedAnnotation is not null)
            {
                DeleteSelected();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.None && _doc is not null && PageList.Items.Count > 1
                     && (e.Key == Key.Left || e.Key == Key.Up || e.Key == Key.Right || e.Key == Key.Down))
            {
                int cur = PageList.SelectedIndex;
                if (cur < 0) cur = 0;
                int next = (e.Key == Key.Left || e.Key == Key.Up) ? cur - 1 : cur + 1;
                if (next >= 0 && next < PageList.Items.Count)
                {
                    PageList.SelectedIndex = next;
                    PageList.ScrollIntoView(PageList.SelectedItem);
                }
                e.Handled = true;
            }
        }

        // ============================================================
        // Annotation management
        // ============================================================

        // ============================================================
        // Snapshot-based undo helpers
        // ============================================================

        private void PushUndo(UndoEntry entry)
        {
            _undoStack.AddLast(entry);
            while (_undoStack.Count > MaxUndoEntries)
                _undoStack.RemoveFirst();
            _redoStack.Clear();
        }

        /// <summary>
        /// Deep-clone the current annotation list for <paramref name="pageIdx"/> and push it
        /// onto the undo stack. Must be called BEFORE the mutation. Clears redo and trims to cap.
        /// </summary>
        private void PushPageSnapshot(int pageIdx)
        {
            if (pageIdx < 0) return;
            var snapshot = _annotations.TryGetValue(pageIdx, out var list)
                ? list.Select(a => a.Clone()).ToList()
                : new List<PageAnnotation>();
            PushUndo(new UndoEntry(UndoKind.PageSnapshot, pageIdx, PageAnnotations: snapshot));
        }

        /// <summary>
        /// Pops the top entry if (and only if) it is a PageSnapshot for the given page.
        /// Used to discard no-op snapshots when a move/resize gesture ended without movement.
        /// </summary>
        private void DropTopSnapshotIfFor(int pageIdx)
        {
            if (_undoStack.Count == 0) return;
            var top = _undoStack.Last!.Value;
            if (top.Kind == UndoKind.PageSnapshot && top.PageIdx == pageIdx)
                _undoStack.RemoveLast();
        }

        private static List<PageAnnotation> CloneList(List<PageAnnotation>? src) =>
            src is null ? new List<PageAnnotation>() : src.Select(a => a.Clone()).ToList();

        // ============================================================

        private void AddAnnotation(PageAnnotation annotation)
        {
            PushPageSnapshot(annotation.PageIndex);
            if (!_annotations.ContainsKey(annotation.PageIndex))
                _annotations[annotation.PageIndex] = [];
            _annotations[annotation.PageIndex].Add(annotation);
            MarkDirty();
        }

        /// <summary>
        /// Saves the current in-memory document bytes onto the undo stack so that
        /// document-level operations (crop, delete page, merge, reorder) can be undone.
        /// Document edits are a hard history barrier: page-level snapshots from before this
        /// edit refer to a different document layout, so we clear the stacks here.
        /// Must be called BEFORE modifying _doc.
        /// </summary>
        private void PushDocUndo()
        {
            if (_doc is null) return;
            using var ms = new System.IO.MemoryStream();
            _doc.Save(ms);
            _undoStack.Clear();
            _redoStack.Clear();
            _undoStack.AddLast(new UndoEntry(UndoKind.Document, DocBytes: ms.ToArray()));
        }

        private void RenderTextAnnotation(TextAnnotation ta)
        {
            var tb = new TextBlock
            {
                Text = ta.Content,
                Foreground = new SolidColorBrush(ta.GetColor()),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = ta.FontSize,
                Padding = new Thickness(2)
            };
            // Width > 0: fixed-width, word-wrapping box. Width == 0: legacy auto-size (no wrap).
            if (ta.Width > 0)
            {
                tb.Width = ta.Width;
                tb.TextWrapping = TextWrapping.Wrap;
                if (ta.Height > 0) tb.Height = ta.Height;
            }
            // Optional opaque whiteout painted behind the text.
            if (ta.HasFill)
                tb.Background = FrozenSolidColorBrush(ta.GetFillColor());
            Canvas.SetLeft(tb, ta.Position.X);
            Canvas.SetTop(tb, ta.Position.Y);
            _annotationCanvas.Children.Add(tb);
        }

        /// <summary>
        /// Bounding size (canvas px, including padding) of a text annotation: the fixed Width/Height when
        /// set, otherwise the measured extent of the (optionally wrapped) content.
        /// </summary>
        private Size MeasureTextAnnotation(TextAnnotation ta)
        {
            double dpi = VisualTreeHelper.GetDpi(_annotationCanvas).PixelsPerDip;
            var ft = new FormattedText(
                string.IsNullOrEmpty(ta.Content) ? " " : ta.Content,
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), ta.FontSize, Brushes.Black, dpi);
            if (ta.Width > 0) ft.MaxTextWidth = Math.Max(1, ta.Width - 4);
            double w = ta.Width > 0 ? ta.Width : ft.Width + 8;
            double h = ta.Height > 0 ? ta.Height : ft.Height + 8;
            return new Size(w, h);
        }

        /// <summary>
        /// Greedy word-wrap of <paramref name="text"/> to <paramref name="maxWidth"/> canvas px at the
        /// given font size, using the same WPF font metrics as the on-screen TextBlock so the baked PDF
        /// breaks at the same points. Over-long single words are hard-broken by character.
        /// </summary>
        private List<string> WrapTextToWidth(string text, double fontSize, double maxWidth)
        {
            var lines = new List<string>();
            if (maxWidth <= 0) { lines.Add(text); return lines; }
            double dpi = VisualTreeHelper.GetDpi(_annotationCanvas).PixelsPerDip;
            var typeface = new Typeface("Segoe UI");
            double W(string s) => new FormattedText(
                string.IsNullOrEmpty(s) ? " " : s,
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, fontSize, Brushes.Black, dpi).Width;

            // Appends a word to the current line, hard-breaking it across lines if it alone overflows.
            string HardBreakAppend(string cur, string word)
            {
                if (W(word) <= maxWidth || word.Length <= 1) return word;
                string chunk = "";
                foreach (char ch in word)
                {
                    string next = chunk + ch;
                    if (chunk.Length > 0 && W(next) > maxWidth)
                    {
                        lines.Add(chunk);
                        chunk = ch.ToString();
                    }
                    else chunk = next;
                }
                return chunk;
            }

            foreach (var para in text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
            {
                string cur = "";
                foreach (var word in para.Split(' '))
                {
                    if (cur.Length == 0)
                    {
                        cur = HardBreakAppend("", word);
                    }
                    else if (W(cur + " " + word) <= maxWidth)
                    {
                        cur += " " + word;
                    }
                    else
                    {
                        lines.Add(cur);
                        cur = HardBreakAppend("", word);
                    }
                }
                lines.Add(cur);
            }
            return lines;
        }

        private void RenderAllAnnotations(int pageIndex)
        {
            _annotationCanvas.Children.Clear();
            // Clearing the canvas also drops form-field overlays — restore them so they
            // survive every annotation re-render (edits, undo, selection, …).
            if (!_annotations.ContainsKey(pageIndex)) { RestoreFormOverlays(pageIndex); return; }

            foreach (var annot in _annotations[pageIndex])
            {
                switch (annot)
                {
                    case TextAnnotation ta:
                        RenderTextAnnotation(ta);
                        break;
                    case HighlightAnnotation ha:
                        var rect = new Rectangle
                        {
                            Fill = FrozenSolidColorBrush(ha.GetColor()),
                            Width = ha.Bounds.Width,
                            Height = ha.Bounds.Height
                        };
                        Canvas.SetLeft(rect, ha.Bounds.X);
                        Canvas.SetTop(rect, ha.Bounds.Y);
                        _annotationCanvas.Children.Add(rect);
                        break;
                    case ShapeAnnotation shp:
                        RenderShapeAnnotation(shp);
                        break;
                    case InkAnnotation ia:
                        if (ia.Points.Count < 2) continue;
                        var poly = new Polyline
                        {
                            Stroke = FrozenSolidColorBrush(ia.GetColor()),
                            StrokeThickness = ia.StrokeWidth,
                            StrokeLineJoin = PenLineJoin.Round,
                            StrokeStartLineCap = PenLineCap.Round,
                            StrokeEndLineCap = PenLineCap.Round
                        };
                        foreach (var pt in ia.Points) poly.Points.Add(pt);
                        _annotationCanvas.Children.Add(poly);
                        break;
                    case TextEditAnnotation tea:
                        // White-out original text
                        var wo = new Rectangle
                        {
                            Fill = Brushes.White,
                            Width = tea.OriginalBounds.Width + 4,
                            Height = tea.OriginalBounds.Height + 4,
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(wo, tea.OriginalBounds.X - 2);
                        Canvas.SetTop(wo, tea.OriginalBounds.Y - 2);
                        _annotationCanvas.Children.Add(wo);
                        // Draw replacement text
                        var etb = new TextBlock
                        {
                            Text = tea.NewContent,
                            Foreground = Brushes.Black,
                            FontFamily = new FontFamily(tea.FontName),
                            FontSize = tea.FontSize,
                            Padding = new Thickness(0)
                        };
                        Canvas.SetLeft(etb, tea.Position.X);
                        Canvas.SetTop(etb, tea.Position.Y);
                        _annotationCanvas.Children.Add(etb);
                        break;

                    case ImageEditAnnotation iea:
                        var imageWhiteout = new Rectangle
                        {
                            Fill = Brushes.White,
                            Width = iea.OriginalBounds.Width + 4,
                            Height = iea.OriginalBounds.Height + 4,
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(imageWhiteout, iea.OriginalBounds.X - 2);
                        Canvas.SetTop(imageWhiteout, iea.OriginalBounds.Y - 2);
                        _annotationCanvas.Children.Add(imageWhiteout);

                        if (!iea.IsDeleted)
                        {
                            var source = LoadImageEditBitmap(iea);
                            if (source is not null)
                            {
                                var imgCtrl = new System.Windows.Controls.Image
                                {
                                    Source = source,
                                    Width = iea.TargetBounds.Width,
                                    Height = iea.TargetBounds.Height,
                                    Stretch = Stretch.Fill,
                                    IsHitTestVisible = false
                                };
                                Canvas.SetLeft(imgCtrl, iea.TargetBounds.X);
                                Canvas.SetTop(imgCtrl, iea.TargetBounds.Y);
                                _annotationCanvas.Children.Add(imgCtrl);
                            }
                        }
                        break;

                    case SignatureAnnotation sa:
                        if (sa.ImageData is not null)
                        {
                            // Image-based signature
                            try
                            {
                                var imgBytes = Convert.FromBase64String(sa.ImageData);
                                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                                using (var imageStream = new System.IO.MemoryStream(imgBytes))
                                {
                                    bmp.BeginInit();
                                    bmp.StreamSource = imageStream;
                                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                    bmp.EndInit();
                                }
                                if (bmp.CanFreeze) bmp.Freeze();
                                var imgCtrl = new System.Windows.Controls.Image
                                {
                                    Source = bmp,
                                    Width = sa.SourceWidth * sa.Scale,
                                    Height = sa.SourceHeight * sa.Scale,
                                    Stretch = System.Windows.Media.Stretch.Uniform,
                                    IsHitTestVisible = false
                                };
                                Canvas.SetLeft(imgCtrl, sa.Position.X);
                                Canvas.SetTop(imgCtrl, sa.Position.Y);
                                _annotationCanvas.Children.Add(imgCtrl);
                            }
                            catch { /* skip broken image */ }
                        }
                        else
                        {
                            foreach (var stroke in sa.Strokes)
                            {
                                if (stroke.Count < 2) continue;
                                var sigPoly = new Polyline
                                {
                                    Stroke = Brushes.Black,
                                    StrokeThickness = 2 * sa.Scale,
                                    StrokeLineJoin = PenLineJoin.Round,
                                    StrokeStartLineCap = PenLineCap.Round,
                                    StrokeEndLineCap = PenLineCap.Round
                                };
                                foreach (var pt in stroke)
                                    sigPoly.Points.Add(new Point(
                                        sa.Position.X + pt.X * sa.Scale,
                                        sa.Position.Y + pt.Y * sa.Scale));
                                _annotationCanvas.Children.Add(sigPoly);
                            }
                        }
                        break;

                    case ImageAnnotation ia:
                        try
                        {
                            var iaBytes = Convert.FromBase64String(ia.ImageData);
                            var iaBmp = new System.Windows.Media.Imaging.BitmapImage();
                            iaBmp.BeginInit();
                            iaBmp.StreamSource = new System.IO.MemoryStream(iaBytes);
                            iaBmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            iaBmp.EndInit();
                            var iaCtrl = new System.Windows.Controls.Image
                            {
                                Source = iaBmp,
                                Width = ia.SourceWidth * ia.Scale,
                                Height = ia.SourceHeight * ia.Scale,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                IsHitTestVisible = false
                            };
                            Canvas.SetLeft(iaCtrl, ia.Position.X);
                            Canvas.SetTop(iaCtrl, ia.Position.Y);
                            _annotationCanvas.Children.Add(iaCtrl);
                        }
                        catch { /* skip broken image */ }
                        break;
                }
            }

            // The canvas was cleared above, so restore any form-field overlays.
            RestoreFormOverlays(pageIndex);
        }

        /// <summary>
        /// Re-adds the interactive form-field overlays for a page after the annotation
        /// canvas has been cleared. Uses the page's last render dimensions.
        /// </summary>
        private void RestoreFormOverlays(int pageIndex)
        {
            if (_renderDims.TryGetValue(pageIndex, out var dims))
                RenderFormFields(pageIndex, dims.w, dims.h);
        }

        private void RenderShapeAnnotation(ShapeAnnotation shp)
        {
            var stroke = FrozenSolidColorBrush(shp.GetStrokeColor());
            SolidColorBrush? fill = shp.HasFill ? FrozenSolidColorBrush(shp.GetFillColor()) : null;
            switch (shp.Kind)
            {
                case ShapeKind.Rectangle:
                {
                    var b = shp.Bounds;
                    var r = new Rectangle
                    {
                        Width = Math.Max(1, b.Width),
                        Height = Math.Max(1, b.Height),
                        Stroke = stroke,
                        StrokeThickness = shp.StrokeWidth,
                        Fill = fill ?? Brushes.Transparent,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(r, b.X);
                    Canvas.SetTop(r, b.Y);
                    _annotationCanvas.Children.Add(r);
                    break;
                }
                case ShapeKind.Ellipse:
                {
                    var b = shp.Bounds;
                    var e = new Ellipse
                    {
                        Width = Math.Max(1, b.Width),
                        Height = Math.Max(1, b.Height),
                        Stroke = stroke,
                        StrokeThickness = shp.StrokeWidth,
                        Fill = fill ?? Brushes.Transparent,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(e, b.X);
                    Canvas.SetTop(e, b.Y);
                    _annotationCanvas.Children.Add(e);
                    break;
                }
                case ShapeKind.Line:
                {
                    var ln = new Line
                    {
                        X1 = shp.Start.X, Y1 = shp.Start.Y,
                        X2 = shp.End.X,   Y2 = shp.End.Y,
                        Stroke = stroke,
                        StrokeThickness = shp.StrokeWidth,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        IsHitTestVisible = false
                    };
                    _annotationCanvas.Children.Add(ln);
                    break;
                }
            }
        }

        private static double DistancePointToSegment(Point p, Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-6)
                return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
            t = Math.Clamp(t, 0.0, 1.0);
            double cx = a.X + t * dx, cy = a.Y + t * dy;
            return Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
        }

        private static BitmapSource? LoadImageEditBitmap(ImageEditAnnotation edit)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                if (!string.IsNullOrWhiteSpace(edit.ReplacementImagePath) && System.IO.File.Exists(edit.ReplacementImagePath))
                {
                    bmp.UriSource = new Uri(edit.ReplacementImagePath, UriKind.Absolute);
                }
                else if (!string.IsNullOrEmpty(edit.OriginalImageData))
                {
                    bmp.StreamSource = new MemoryStream(Convert.FromBase64String(edit.OriginalImageData));
                }
                else
                {
                    return null;
                }
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            ApplyUndoRedoStep(_undoStack, _redoStack, "undo");
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            ApplyUndoRedoStep(_redoStack, _undoStack, "redo");
        }

        private void RedoCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            e.Handled = true;
            Redo_Click(sender, e);
        }

        /// <summary>
        /// Pops one entry from <paramref name="source"/>, captures the current state matching
        /// that entry's kind into <paramref name="target"/> (so the inverse step is available),
        /// and applies the popped entry to the document/annotation state.
        /// </summary>
        private void ApplyUndoRedoStep(LinkedList<UndoEntry> source, LinkedList<UndoEntry> target, string label)
        {
            if (source.Count == 0)
            {
                SetStatus(label == "undo" ? "Nothing to undo" : "Nothing to redo");
                return;
            }

            // Cancel any active transient gesture/edit before mutating state.
            CancelActiveGesture();

            var entry = source.Last!.Value;
            source.RemoveLast();

            if (entry.Kind == UndoKind.PageSnapshot)
            {
                int pageIdx = entry.PageIdx;
                // Capture the inverse step for redo (or re-undo)
                var current = _annotations.TryGetValue(pageIdx, out var curList)
                    ? curList.Select(a => a.Clone()).ToList()
                    : new List<PageAnnotation>();
                target.AddLast(new UndoEntry(UndoKind.PageSnapshot, pageIdx, PageAnnotations: current));

                var restored = CloneList(entry.PageAnnotations);
                foreach (var a in restored) a.PageIndex = pageIdx;
                _annotations[pageIdx] = restored;
                ClearSelection();
                RenderAllAnnotations(pageIdx);
                MarkDirty();
                SetStatus(label == "undo" ? "Undid annotation change" : "Redid annotation change");
            }
            else // Document
            {
                if (entry.DocBytes is null) return;
                // Capture current document bytes for the inverse step.
                byte[]? currentBytes = null;
                if (_doc is not null)
                {
                    using var ms = new System.IO.MemoryStream();
                    _doc.Save(ms);
                    currentBytes = ms.ToArray();
                }
                if (currentBytes is not null)
                    target.AddLast(new UndoEntry(UndoKind.Document, DocBytes: currentBytes));

                int selectedIdx = PageList.SelectedIndex;
                var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    $"tdpdf_undo_{Guid.NewGuid():N}.pdf");
                System.IO.File.WriteAllBytes(tempPath, entry.DocBytes);
                _doc?.Close();
                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
                _currentFile = tempPath;
                // Document edits are a history barrier: page snapshots from before/after refer
                // to a different page layout. Drop them on both stacks (except the just-captured
                // inverse Document entry on `target`).
                ClearPageSnapshotsOnly(source);
                ClearPageSnapshotsExceptLast(target);
                _annotations.Clear();
                ClearFormState();
                _renderDims.Clear();
                ClearSelection();
                MarkDirty();
                RefreshPageList();
                if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                    PageList.SelectedIndex = selectedIdx;
                else if (PageList.Items.Count > 0)
                    PageList.SelectedIndex = 0;
                SetStatus(label == "undo" ? "Undid document change" : "Redid document change");
            }
        }

        private static void ClearPageSnapshotsOnly(LinkedList<UndoEntry> list)
        {
            var node = list.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.Kind == UndoKind.PageSnapshot)
                    list.Remove(node);
                node = next;
            }
        }

        private static void ClearPageSnapshotsExceptLast(LinkedList<UndoEntry> list)
        {
            // Preserve the most recently added Document inverse entry (always the tail) but
            // strip any prior PageSnapshot entries that would refer to the wrong layout.
            var tail = list.Last;
            var node = list.First;
            while (node is not null && node != tail)
            {
                var next = node.Next;
                if (node.Value.Kind == UndoKind.PageSnapshot)
                    list.Remove(node);
                node = next;
            }
        }

        /// <summary>
        /// Cancels any in-flight drag/resize/move/inline-edit so undo/redo can safely replace
        /// the underlying annotation instances.
        /// </summary>
        private void CancelActiveGesture()
        {
            if (_activeTextBox is not null) CancelTextEdit();
            if (_isPanning) EndPan();
            _isDrawing = false;
            _isSelecting = false;
            _isDraggingAnnot = false;
            _isResizingSig = false;
            _isResizingImage = false;
            _isMovingAnnot = false;
            _isResizingAnnot = false;
            _movingAnnot = null;
            _moveOriginalGeom = null;
            _resizingAnnot = null;
            _resizeOriginalGeom = null;
            _dragAnnot = null;
            _resizeSigAnnot = null;
            _resizingImageEdit = null;
            if (_annotationCanvas != null && _annotationCanvas.IsMouseCaptured)
                _annotationCanvas.ReleaseMouseCapture();
        }


        private void ClearAnnotations_Click(object sender, RoutedEventArgs e)
        {
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;
            if (_annotations.ContainsKey(pageIdx) && _annotations[pageIdx].Count > 0)
            {
                PushPageSnapshot(pageIdx);
                _annotations[pageIdx].Clear();
                MarkDirty();
            }
            ClearSelection();
            _annotationCanvas.Children.Clear();
            SetStatus("Cleared annotations on this page");
        }

        // ============================================================
        // Dirty / unsaved-change tracking
        // ============================================================

        private void MarkDirty(bool dirty = true)
        {
            _isDirty = dirty;
            if (_saveAsBtnRef != null)
            {
                _saveAsBtnRef.Foreground = dirty
                    ? BrushResource("WarningOrange")
                    : BrushResource("AccentGreen");
            }
            UpdateTabChrome();
        }

        // ============================================================
        // Multi-document tabs
        // ============================================================
        // The page sidebar (PageList), the page viewer (PagePreviewPanel),
        // and the annotation canvas are single shared controls. Switching tabs
        // swaps the active DocumentContext (_ctx) and rebuilds those controls
        // from it; the per-document model state follows automatically via the
        // forwarding properties (_doc, _annotations, _undoStack, …).

        private void EnsureActiveTabRegistered()
        {
            if (!_tabs.Contains(_ctx)) _tabs.Add(_ctx);
        }

        /// <summary>
        /// Single entry point for opening a PDF (Open dialog, drag-drop, command
        /// line, and cross-instance forwarding). Reuses the current tab when it
        /// holds no document yet, otherwise opens the file in a brand-new tab.
        /// </summary>
        private async Task OpenInTabAsync(string path)
        {
            EnsureActiveTabRegistered();
            var previous = _ctx;
            DocumentContext? created = null;
            if (_ctx.Doc is not null)
            {
                created = new DocumentContext();
                _tabs.Add(created);
                ActivateContext(created);
            }

            await OpenFileAsync(path);

            // If we spun up a brand-new tab but the open failed or was cancelled
            // (bad file, wrong password, …), drop the empty tab and return to the
            // previously active document instead of leaving a stray "Untitled" tab.
            if (created is not null && created.Doc is null)
            {
                _tabs.Remove(created);
                if (ReferenceEquals(_ctx, created))
                    ActivateContext(_tabs.Contains(previous) ? previous : _tabs[^1]);
            }
            RebuildTabStrip();
        }

        /// <summary>Captures the live view state of the active tab before switching away.</summary>
        private void CaptureViewState()
        {
            if (_ctx.Doc is not null)
                _ctx.SelectedPageIndex = PageList.SelectedIndex;
        }

        /// <summary>Makes <paramref name="ctx"/> the active tab and rebuilds the shared UI from it.</summary>
        private void ActivateContext(DocumentContext ctx)
        {
            if (ReferenceEquals(_ctx, ctx)) { UpdateTabChrome(); return; }

            CommitActiveTextBox();
            CaptureViewState();
            _renderCancellationTokenSource?.Cancel();

            _ctx = ctx;

            // Tear down transient overlays/tools tied to the previous document.
            ClearSelection();
            CloseSearchBar();
            HideDrawSettings();
            HideTextSettings();
            HideSignaturePopup();
            HideCropPopup();
            ClearCropSelection();
            SetTool(EditTool.Select);
            _annotationCanvas.Children.Clear();
            ClearSecondaryPages();

            if (_ctx.Doc is null)
            {
                // Empty tab → drop-zone state.
                PageList.Items.Clear();
                if (FindName("PageImage") is System.Windows.Controls.Image img)
                {
                    img.Source = null;
                    img.Width = double.NaN;
                    img.Height = double.NaN;
                }
                FileNameLabel.Text = "";
                DropZone.Visibility = Visibility.Visible;
                PagePreviewPanel.Visibility = Visibility.Collapsed;
                if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = false;
                _gridViewToggle.IsEnabled = false;
                _pageJumpBox.IsEnabled = false;
                _pageJumpBox.Text = "";
                _pageTotalLabel.Text = "/ –";
                _ctx.Outline = null;
                RefreshOutlineUi();
                RefreshRecentFilesUi();   // start screen is visible again; refresh the recent list
            }
            else
            {
                DropZone.Visibility = Visibility.Collapsed;
                PagePreviewPanel.Visibility = Visibility.Visible;
                // View mode is app-wide; make sure the correct layout host is visible for this tab.
                bool isContinuous = _viewMode == ViewMode.Continuous;
                _pageContentPanel.Visibility = isContinuous ? Visibility.Collapsed : Visibility.Visible;
                if (PageImage.Parent is FrameworkElement pgChild
                    && pgChild.Parent is FrameworkElement primBorder)
                    primBorder.Visibility = isContinuous ? Visibility.Collapsed : Visibility.Visible;
                _continuousPanel.Visibility = isContinuous ? Visibility.Visible : Visibility.Collapsed;
                FileNameLabel.Text = _ctx.DisplayName;
                if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = true;
                _gridViewToggle.IsEnabled = true;
                _pageJumpBox.IsEnabled = true;
                _pageTotalLabel.Text = $"/ {_ctx.Doc.PageCount}";
                RefreshPageList(_ctx.Thumbnails);
                if (_ctx.Outline is null) LoadOutlines(); else RefreshOutlineUi();

                int idx = _ctx.SelectedPageIndex;
                if (idx < 0 || idx >= PageList.Items.Count)
                    idx = PageList.Items.Count > 0 ? 0 : -1;
                if (idx >= 0)
                {
                    if (_viewMode == ViewMode.Continuous)
                    {
                        // View mode is app-wide but the continuous strip is per-document; rebuild
                        // it for the newly-activated tab. Set the index without firing a stale
                        // scroll, then SetupContinuousView scrolls to the right page.
                        _suppressContinuousScrollSync = true;
                        PageList.SelectedIndex = idx;
                        _suppressContinuousScrollSync = false;
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                            (Action)(() => SetupContinuousView(idx)));
                    }
                    // Setting SelectedIndex fires PageList_SelectionChanged (→ render).
                    // If the index is unchanged, render explicitly.
                    else if (PageList.SelectedIndex == idx) RerenderCurrentPage();
                    else PageList.SelectedIndex = idx;
                }
            }

            // Sync the save-button color with this tab's dirty state without
            // re-touching the model (MarkDirty(_ctx.IsDirty) is a no-op write).
            MarkDirty(_ctx.IsDirty);
            UpdateTabChrome();
        }

        /// <summary>Rebuilds every tab chip and toggles strip visibility.</summary>
        private void RebuildTabStrip()
        {
            if (_tabStrip is null) return;
            _tabStrip.Children.Clear();
            foreach (var ctx in _tabs)
            {
                ctx.Chip = BuildTabChip(ctx);
                _tabStrip.Children.Add(ctx.Chip);
            }
            // Keep the single-document experience unchanged — only show the strip
            // once a second document is open.
            _tabStripBorder.Visibility = _tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            UpdateTabChrome();
        }

        private Border BuildTabChip(DocumentContext ctx)
        {
            var text = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 180
            };
            var close = new Button
            {
                Content = "", // Segoe MDL2 Assets close glyph
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 9,
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                Margin = new Thickness(8, 0, 0, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = BrushResource("TextSecondary"),
                Cursor = Cursors.Hand,
                ToolTip = "Close file (Ctrl+W)",
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false
            };
            close.Click += (_, e) => CloseTab(ctx);

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(text);
            panel.Children.Add(close);

            var chip = new Border
            {
                Child = panel,
                Padding = new Thickness(10, 5, 6, 5),
                Margin = new Thickness(0, 4, 4, 0),
                BorderThickness = new Thickness(1, 1, 1, 0),
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Cursor = Cursors.Hand,
                Tag = ctx
            };
            chip.MouseLeftButtonUp += (_, e) =>
            {
                if (!ReferenceEquals(_ctx, ctx)) ActivateContext(ctx);
            };
            return chip;
        }

        /// <summary>Updates each chip's label (name + dirty marker) and active styling.</summary>
        private void UpdateTabChrome()
        {
            if (_tabStrip is null) return;
            foreach (var ctx in _tabs)
            {
                if (ctx.Chip is null) continue;
                bool active = ReferenceEquals(ctx, _ctx);
                ctx.Chip.Background = active ? BrushResource("BgPanel") : BrushResource("BgDark");
                ctx.Chip.BorderBrush = active ? BrushResource("AccentGreen") : BrushResource("BorderDim");
                if (ctx.Chip.Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock tb)
                {
                    string name = string.IsNullOrEmpty(ctx.DisplayName) ? "Untitled.pdf" : ctx.DisplayName;
                    tb.Text = (ctx.IsDirty ? "● " : "") + name;
                    tb.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                    tb.Foreground = active ? BrushResource("TextPrimary") : BrushResource("TextSecondary");
                }
            }
        }

        /// <summary>Closes a tab (prompting if it has unsaved changes) and activates a neighbor.</summary>
        private void CloseTab(DocumentContext ctx)
        {
            EnsureActiveTabRegistered();
            if (!_tabs.Contains(ctx)) return;

            if (ctx.Doc is not null && ctx.IsDirty)
            {
                if (!ReferenceEquals(_ctx, ctx)) ActivateContext(ctx);
                var res = TdpDialog.Show(this,
                    "You have unsaved changes. Close this file without saving?",
                    "TDPdf", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
            }

            int removedIndex = _tabs.IndexOf(ctx);
            bool closingActive = ReferenceEquals(_ctx, ctx);

            if (closingActive)
            {
                _openCancellationTokenSource?.Cancel();
                _renderCancellationTokenSource?.Cancel();
            }

            try { ctx.Doc?.Close(); } catch { }
            ctx.Doc = null;
            ctx.Annotations.Clear();
            ctx.RenderCache.Clear();
            ctx.RenderDims.Clear();
            ctx.UndoStack.Clear();
            ctx.RedoStack.Clear();
            ctx.ContentEditor.ClearCache();
            ctx.AllSearchRects.Clear();
            ctx.SearchResultPages.Clear();
            ctx.Thumbnails = null;
            _tabs.Remove(ctx);

            if (_tabs.Count == 0)
            {
                var empty = new DocumentContext();
                _tabs.Add(empty);
                ActivateContext(empty);
            }
            else if (closingActive)
            {
                int next = Math.Min(removedIndex, _tabs.Count - 1);
                ActivateContext(_tabs[next]);
            }
            RebuildTabStrip();
            SetStatus("Ready");
        }

        /// <summary>Sets the active document's display name (tab header + title bar).</summary>
        private void SetDisplayName(string name)
        {
            _ctx.DisplayName = name;
            FileNameLabel.Text = name;
            UpdateTabChrome();
        }

        /// <summary>
        /// Called when a second process forwarded a file to this (primary) window
        /// via the single-instance pipe. Brings the window forward and opens the
        /// file in a new tab.
        /// </summary>
        public void OpenPathFromAnotherInstance(string? path)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                _ = OpenInTabAsync(path);
        }

        // ============================================================
        // Close file (Ctrl+W) — returns to drop-zone state
        // ============================================================

        // Ctrl+W / toolbar close: close the active tab. With multiple tabs open
        // this closes only the current document and activates a neighbor; with a
        // single document it returns the window to the drop-zone state.
        private void CloseFile()
        {
            // Nothing to do when the only tab is already empty.
            if (_ctx.Doc is null && _tabs.Count <= 1) return;
            CloseTab(_ctx);
        }

        private void CloseFile_Click(object sender, RoutedEventArgs e) => CloseFile();

        // ============================================================
        // File toolbar handlers
        // ============================================================

        private void New_Click(object sender, RoutedEventArgs e)
        {
            Telemetry.TrackEvent("File.New");
            _ = NewDocumentAsync();
        }

        private void NewDocument() => _ = NewDocumentAsync();

        private async Task NewDocumentAsync()
        {
            // Opens in a new tab — no need to discard the current document.
            string? tempPath = null;
            try
            {
                var newDoc = new PdfDocument();
                newDoc.AddPage(); // one blank A4 page

                tempPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"tdpdf_new_{Guid.NewGuid():N}.pdf");
                newDoc.Save(tempPath);
                newDoc.Close();

                await OpenInTabAsync(tempPath);
                SetDisplayName("Untitled.pdf");
                SetStatus("New blank document");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Could not create new document:\n{ex.Message}",
                    "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void Open_Click(object sender, RoutedEventArgs e)
        {
            Telemetry.TrackEvent("File.Open");
            var dlg = new OpenFileDialog { Filter = "PDF files|*.pdf", Title = "Open PDF", Multiselect = true };
            if (dlg.ShowDialog() == true)
                foreach (var file in dlg.FileNames)
                    await OpenInTabAsync(file);
        }

        private void Merge_Click(object sender, RoutedEventArgs e)
        {
            Telemetry.TrackEvent("File.Merge");
            if (_doc is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            var doc = _doc;
            var dlg = new OpenFileDialog { Filter = "PDF files|*.pdf", Title = "Select PDF to merge", Multiselect = true };
            if (dlg.ShowDialog() != true) return;
            try
            {
                foreach (var file in dlg.FileNames)
                {
                    int pageOffset = doc.PageCount;

                    // Open twice: Import mode for AddPage, ReadOnly for catalog access.
                    using var srcRead = PdfReader.Open(file, PdfDocumentOpenMode.ReadOnly);
                    var namedDestMap = BuildNamedDestMap(srcRead);

                    using var src = PdfReader.Open(file, PdfDocumentOpenMode.Import);
                    for (int i = 0; i < src.PageCount; i++)
                        doc.AddPage(src.Pages[i]);

                    // Rewrite named-destination links in the newly added pages so they
                    // resolve correctly after the catalog is not imported.
                    if (namedDestMap.Count > 0)
                        RewriteNamedDestLinks(doc, pageOffset, namedDestMap);
                }
                SaveTempAndReload();
                SetStatus($"Merged {dlg.FileNames.Length} file(s) - {_doc?.PageCount} total pages");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Merge failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Builds a map of named destination string → 0-based page index from a source document's
        /// /Dests dictionary and /Names /Dests name tree.
        /// </summary>
        private Dictionary<string, int> BuildNamedDestMap(PdfDocument src)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                var catalog = src.Internals.Catalog;

                // Legacy flat /Dests dictionary
                var destsDict = catalog.Elements.GetDictionary("/Dests");
                if (destsDict != null)
                {
                    foreach (var key in destsDict.Elements.Keys)
                    {
                        PdfItem? val = DerefItem(destsDict.Elements[key] ?? new PdfInteger(-1));
                        int? idx = ResolveDestPageIndexInDoc(src, val);
                        if (idx.HasValue) map[key.TrimStart('/')] = idx.Value;
                    }
                }

                // Modern /Names /Dests name tree
                var namesDict = catalog.Elements.GetDictionary("/Names");
                var destTree  = namesDict?.Elements.GetDictionary("/Dests");
                if (destTree != null)
                    WalkNameTree(src, destTree, map);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"BuildNamedDestMap: {ex}"); }
            return map;
        }

        private void WalkNameTree(PdfDocument src, PdfDictionary node, Dictionary<string, int> map)
        {
            var namesArr = node.Elements.GetArray("/Names");
            if (namesArr != null)
            {
                for (int i = 0; i + 1 < namesArr.Elements.Count; i += 2)
                {
                    var keyItem = namesArr.Elements[i];
                    string key  = keyItem is PdfString ks ? ks.Value : keyItem?.ToString()?.TrimStart('/') ?? "";
                    if (string.IsNullOrEmpty(key)) continue;
                    PdfItem? val = DerefItem(namesArr.Elements[i + 1]);
                    int? idx = ResolveDestPageIndexInDoc(src, val);
                    if (idx.HasValue) map[key] = idx.Value;
                }
            }

            var kids = node.Elements.GetArray("/Kids");
            if (kids != null)
            {
                for (int i = 0; i < kids.Elements.Count; i++)
                {
                    if (DerefItem(kids.Elements[i]) is PdfDictionary kid)
                        WalkNameTree(src, kid, map);
                }
            }
        }

        /// <summary>
        /// Resolves a destination value (PdfArray or PdfDictionary with /D) to a page index
        /// within the given source document by matching the page object number.
        /// </summary>
        private static int? ResolveDestPageIndexInDoc(PdfDocument src, PdfItem? val)
        {
            PdfArray? arr = val as PdfArray;
            if (arr is null && val is PdfDictionary vd)
                arr = vd.Elements.GetArray("/D");
            if (arr is null || arr.Elements.Count == 0) return null;

            var first = arr.Elements[0];
            int objNum = GetObjectNumber(first);
            if (objNum > 0)
            {
                for (int i = 0; i < src.PageCount; i++)
                {
                    var pgRef = src.Pages[i].Reference;
                    if (pgRef != null && pgRef.ObjectNumber == objNum) return i;
                }
            }
            else if (first is PdfInteger pi && pi.Value >= 0 && pi.Value < src.PageCount)
            {
                return pi.Value;
            }
            return null;
        }

        /// <summary>
        /// Walks all link annotations in pages [pageOffset, doc.PageCount) and rewrites any
        /// named-destination /D values to explicit [pageRef /Fit] arrays using the merged
        /// document's page references. This is needed because PdfSharpCore's import does not
        /// copy the source document's /Names /Dests catalog entries.
        /// </summary>
        private static void RewriteNamedDestLinks(PdfDocument doc, int pageOffset,
            Dictionary<string, int> namedDestMap)
        {
            for (int pi = pageOffset; pi < doc.PageCount; pi++)
            {
                try
                {
                    var page    = doc.Pages[pi];
                    var annotsArr = page.Elements.GetArray("/Annots");
                    if (annotsArr is null) continue;

                    for (int ai = 0; ai < annotsArr.Elements.Count; ai++)
                    {
                        PdfItem? elem = annotsArr.Elements[ai];
                        PdfDictionary? ann = elem as PdfDictionary
                            ?? (DerefItemStatic(elem) as PdfDictionary);
                        if (ann is null) continue;

                        var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                        if (!subtype.Contains("Link")) continue;

                        // Check /A /D (GoTo action)
                        var actionDict = ann.Elements.GetDictionary("/A");
                        if (actionDict != null)
                        {
                            var s = actionDict.Elements["/S"]?.ToString() ?? "";
                            if (s.Contains("GoTo"))
                            {
                                var destItem = actionDict.Elements["/D"];
                                string? name = ExtractDestName(destItem);
                                if (name != null && namedDestMap.TryGetValue(name, out int srcIdx))
                                {
                                    int targetIdx = pageOffset + srcIdx;
                                    if (targetIdx < doc.PageCount)
                                        actionDict.Elements["/D"] = MakeExplicitDest(doc, targetIdx);
                                }
                            }
                        }
                        else
                        {
                            // Bare /Dest on annotation
                            var destItem = ann.Elements["/Dest"];
                            string? name = ExtractDestName(destItem);
                            if (name != null && namedDestMap.TryGetValue(name, out int srcIdx))
                            {
                                int targetIdx = pageOffset + srcIdx;
                                if (targetIdx < doc.PageCount)
                                    ann.Elements["/Dest"] = MakeExplicitDest(doc, targetIdx);
                            }
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RewriteNamedDestLinks p{pi}: {ex}"); }
            }
        }

        private static string? ExtractDestName(PdfItem? item)
        {
            if (item is null) return null;
            if (item is PdfString ps) return ps.Value;
            if (item is PdfName   pn) return pn.Value.TrimStart('/');
            return null;
        }

        private static PdfArray MakeExplicitDest(PdfDocument doc, int pageIndex)
        {
            var arr = new PdfArray(doc);
            arr.Elements.Add(doc.Pages[pageIndex].Reference);
            arr.Elements.Add(new PdfName("/Fit"));
            return arr;
        }

        // Static version of DerefItem for use in static helpers.
        private static PdfItem DerefItemStatic(PdfItem item)
        {
            var valueProp = item.GetType().GetProperty("Value",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (valueProp?.GetValue(item) is PdfObject resolved) return resolved;
            return item;
        }

        private void Split_Click(object sender, RoutedEventArgs e)
        {
            Telemetry.TrackEvent("File.Split");
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            var currentFile = _currentFile;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { TdpDialog.Show(this, "Select pages to extract."); return; }
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save extracted pages as" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var indices = new List<int>();
                foreach (var item in selected) indices.Add(PageList.Items.IndexOf(item));
                using var importDoc = PdfReader.Open(currentFile, PdfDocumentOpenMode.Import);
                var newDoc = new PdfDocument();
                foreach (var idx in indices.OrderBy(i => i))
                    newDoc.AddPage(importDoc.Pages[idx]);
                newDoc.Save(dlg.FileName);
                SetStatus($"Extracted {indices.Count} page(s) to {System.IO.Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Split failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            var doc = _doc;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { TdpDialog.Show(this, "Select pages to delete."); return; }
            var result = TdpDialog.Show(this, $"Delete {selected.Count} {(selected.Count == 1 ? "page" : "pages")}?", "TDPdf",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            try
            {
                var indices = new List<int>();
                foreach (var item in selected) indices.Add(PageList.Items.IndexOf(item));
                foreach (var idx in indices.OrderByDescending(i => i))
                    doc.Pages.RemoveAt(idx);
                SaveTempAndReload();
                SetStatus($"Deleted {indices.Count} page(s) - {_doc?.PageCount} remaining");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Delete failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InsertBlankPage_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            var doc = _doc;
            int insertAfter = PageList.SelectedIndex >= 0 ? PageList.SelectedIndex : doc.PageCount - 1;

            double currentW = insertAfter >= 0 && insertAfter < doc.PageCount
                ? doc.Pages[insertAfter].Width.Point
                : 612;
            double currentH = insertAfter >= 0 && insertAfter < doc.PageCount
                ? doc.Pages[insertAfter].Height.Point
                : 792;

            var picked = ShowInsertPageDialog(currentW, currentH);
            if (picked is null) return;
            var (wPt, hPt) = picked.Value;

            try
            {
                var blank = new PdfPage { Width = XUnit.FromPoint(wPt), Height = XUnit.FromPoint(hPt) };
                doc.Pages.Insert(insertAfter + 1, blank);
                SaveTempAndReload();
                PageList.SelectedIndex = insertAfter + 1;
                SetStatus($"Inserted blank page at position {insertAfter + 2}");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Insert failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private (double WidthPt, double HeightPt)? ShowInsertPageDialog(double currentWPt, double currentHPt)
        {
            // (Display name, width pt, height pt). Sizes are in PostScript points (72/in).
            var sizes = new (string Name, double W, double H)[]
            {
                ($"Same as current page ({currentWPt:0}×{currentHPt:0} pt)", currentWPt, currentHPt),
                ("Letter (8.5 × 11 in)",   612, 792),
                ("Legal (8.5 × 14 in)",    612, 1008),
                ("Tabloid (11 × 17 in)",   792, 1224),
                ("A3 (297 × 420 mm)",      842, 1191),
                ("A4 (210 × 297 mm)",      595, 842),
                ("A5 (148 × 210 mm)",      420, 595)
            };

            var bgDark   = (SolidColorBrush)FindResource("BgDark");
            var bgPanel  = (SolidColorBrush)FindResource("BgPanel");
            var borderDim = (SolidColorBrush)FindResource("BorderDim");
            var textPrimary = (SolidColorBrush)FindResource("TextPrimary");
            var textSecondary = (SolidColorBrush)FindResource("TextSecondary");
            var accent = (SolidColorBrush)FindResource("AccentGreen");

            var win = new Window
            {
                Title = "Insert Blank Page",
                Width = 380, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = bgDark,
                Foreground = textPrimary,
                ShowInTaskbar = false,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12
            };

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock
            {
                Text = "Page size",
                Foreground = textSecondary,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var sizeBox = new ComboBox
            {
                Style = (Style)FindResource("DarkComboBox"),
                Height = 28
            };
            foreach (var s in sizes) sizeBox.Items.Add(s.Name);
            sizeBox.SelectedIndex = 0;
            root.Children.Add(sizeBox);

            root.Children.Add(new TextBlock
            {
                Text = "Orientation",
                Foreground = textSecondary,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 14, 0, 6)
            });

            var orient = new StackPanel { Orientation = Orientation.Horizontal };
            var rbPortrait = new RadioButton
            {
                Content = "Portrait", IsChecked = currentWPt <= currentHPt,
                Foreground = textPrimary, Margin = new Thickness(0, 0, 16, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            var rbLandscape = new RadioButton
            {
                Content = "Landscape", IsChecked = currentWPt > currentHPt,
                Foreground = textPrimary,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            orient.Children.Add(rbPortrait);
            orient.Children.Add(rbLandscape);
            root.Children.Add(orient);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 96, Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                Background = bgPanel,
                Foreground = textPrimary,
                BorderBrush = borderDim,
                Cursor = Cursors.Hand,
                IsCancel = true
            };
            var okBtn = new Button
            {
                Content = "Insert",
                Width = 96, Height = 30,
                Background = accent,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                BorderBrush = accent,
                Cursor = Cursors.Hand,
                IsDefault = true
            };
            buttons.Children.Add(cancelBtn);
            buttons.Children.Add(okBtn);
            root.Children.Add(buttons);

            win.Content = new Border
            {
                Background = bgPanel,
                BorderBrush = borderDim,
                BorderThickness = new Thickness(1),
                Child = root
            };

            bool ok = false;
            okBtn.Click += (_, _) => { ok = true; win.DialogResult = true; };
            cancelBtn.Click += (_, _) => { ok = false; win.DialogResult = false; };

            win.ShowDialog();
            if (!ok) return null;

            var selected = sizes[sizeBox.SelectedIndex];
            double w = selected.W;
            double h = selected.H;
            if (rbLandscape.IsChecked == true && h > w) (w, h) = (h, w);
            if (rbPortrait.IsChecked == true && w > h) (w, h) = (h, w);
            return (w, h);
        }

        private void DocumentInfo_Click(object sender, RoutedEventArgs e) => ShowDocumentInfoDialog();

        // F12 / File ▸ Document Info… — view and edit the PDF's Document Information dictionary
        // (Title, Author, Subject, Keywords, Creator) plus a read-only structure summary. Edits are
        // applied to the live PdfSharpCore _doc.Info and the document is marked dirty, so they are
        // written by the normal save pipeline (doc.Save) the next time the user saves.
        private void ShowDocumentInfoDialog()
        {
            var doc = _doc;
            if (doc is null) { TdpDialog.Show(this, "Open a PDF first."); return; }

            var bgDark        = BrushResource("BgDark");
            var bgPanel       = BrushResource("BgPanel");
            var borderDim     = BrushResource("BorderDim");
            var textPrimary   = BrushResource("TextPrimary");
            var textSecondary = BrushResource("TextSecondary");
            var accent        = BrushResource("AccentGreen");

            var win = new Window
            {
                Title = "Document Info",
                Width = 460, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = bgDark,
                Foreground = textPrimary,
                ShowInTaskbar = false,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12
            };

            var root = new StackPanel { Margin = new Thickness(16) };

            // Editable metadata field. Every value is a single-line metadata string (Enter is not a
            // newline), but it wraps and grows up to a cap, then scrolls — so long titles / keyword
            // lists aren't cramped. `tall` gives the keyword field more room.
            TextBox AddField(string label, string? value, bool tall = false)
            {
                root.Children.Add(new TextBlock
                {
                    Text = label,
                    Foreground = textSecondary,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 4)
                });
                var box = new TextBox
                {
                    Text = value ?? "",
                    Foreground = textPrimary,
                    Background = bgPanel,
                    BorderBrush = borderDim,
                    BorderThickness = new Thickness(1),
                    CaretBrush = accent,
                    Padding = new Thickness(6, 4, 6, 4),
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = false,
                    VerticalContentAlignment = VerticalAlignment.Top,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = tall ? 110 : 72,
                    Margin = new Thickness(0, 0, 0, 12)
                };
                root.Children.Add(box);
                return box;
            }

            var titleBox    = AddField("Title",    doc.Info.Title);
            var authorBox   = AddField("Author",   doc.Info.Author);
            var subjectBox  = AddField("Subject",  doc.Info.Subject);
            var keywordsBox = AddField("Keywords", doc.Info.Keywords, tall: true);
            var creatorBox  = AddField("Creator",  doc.Info.Creator);

            root.Children.Add(new TextBlock
            {
                Text = BuildDocumentInfoSummary(doc, _currentFile),
                Foreground = textSecondary,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 96, Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                Background = bgPanel,
                Foreground = textPrimary,
                BorderBrush = borderDim,
                Cursor = Cursors.Hand,
                IsCancel = true
            };
            var saveBtn = new Button
            {
                Content = "Save",
                Width = 96, Height = 30,
                Background = accent,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                BorderBrush = accent,
                Cursor = Cursors.Hand,
                IsDefault = true
            };
            buttons.Children.Add(cancelBtn);
            buttons.Children.Add(saveBtn);
            root.Children.Add(buttons);

            win.Content = new Border
            {
                Background = bgPanel,
                BorderBrush = borderDim,
                BorderThickness = new Thickness(1),
                Child = root
            };

            cancelBtn.Click += (_, _) => { win.DialogResult = false; };
            saveBtn.Click += (_, _) =>
            {
                doc.Info.Title    = titleBox.Text;
                doc.Info.Author   = authorBox.Text;
                doc.Info.Subject  = subjectBox.Text;
                doc.Info.Keywords = keywordsBox.Text;
                doc.Info.Creator  = creatorBox.Text;
                MarkDirty(true);
                win.DialogResult = true;
            };

            win.Loaded += (_, _) => titleBox.Focus();
            win.ShowDialog();
        }

        // Read-only structure summary for the Document Info dialog: Producer (may throw — guarded),
        // page count, PDF version, creation date (if present — guarded), and file size in KB.
        private static string BuildDocumentInfoSummary(PdfDocument doc, string? filePath)
        {
            var parts = new List<string>();
            string producer = ""; try { producer = doc.Info.Producer ?? ""; } catch { }
            if (producer.Length > 0) parts.Add($"Producer: {producer}");
            parts.Add($"{doc.PageCount} pages");
            parts.Add($"PDF {doc.Version / 10}.{doc.Version % 10}");
            try { var d = doc.Info.CreationDate; if (d != default) parts.Add($"created {d:yyyy-MM-dd HH:mm}"); } catch { }
            try { if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath)) parts.Add($"{new FileInfo(filePath).Length / 1024.0:N0} KB"); } catch { }
            return string.Join("\n", parts);
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || PageList.SelectedIndex <= 0) return;
            var doc = _doc;
            int idx = PageList.SelectedIndex;
            var page = doc.Pages[idx];
            doc.Pages.RemoveAt(idx);
            doc.Pages.Insert(idx - 1, page);
            SaveTempAndReload();
            PageList.SelectedIndex = idx - 1;
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || PageList.SelectedIndex < 0 || PageList.SelectedIndex >= _doc.PageCount - 1) return;
            var doc = _doc;
            int idx = PageList.SelectedIndex;
            var page = doc.Pages[idx];
            doc.Pages.RemoveAt(idx);
            doc.Pages.Insert(idx + 1, page);
            SaveTempAndReload();
            PageList.SelectedIndex = idx + 1;
        }

        private async void SaveInPlace_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            // Merged-on-drop / imported-image docs have no real on-disk home (their working file is a
            // temp copy), so an in-place save would silently write to %TEMP%. Route them to Save As.
            if (_ctx.IsUntitled) { SaveAs_Click(sender, e); return; }
            await SaveInPlaceAsync();
        }

        private async Task SaveInPlaceAsync()
        {
            using var op = Telemetry.StartOperation("SaveInPlace");
            if (_doc is null || _currentFile is null) return;
            CommitActiveTextBox();
            // Capture the destination once: a #106 repair may repoint _currentFile at a temp copy,
            // but the in-place save must always target the file the user actually opened.
            string targetFile = _currentFile;
            string status = "";

            // The unit of work retried by RunSaveWithRecoveryAsync. Reads _doc fresh each call so a
            // repair (which swaps _doc for a rebuilt copy) is picked up, and re-bakes annotations from
            // _annotations every time, so a retried save keeps all of the user's edits.
            async Task DoSaveAsync()
            {
                var doc = _doc!;
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0) || HasPendingFormValues;

                if (hasAnnotations)
                {
                    var tempClean = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        $"tdpdf_clean_{Guid.NewGuid():N}.pdf");
                    await _pdfDocumentService.SaveAsync(() => doc.Save(tempClean), CancellationToken.None);
                    DrawAnnotationsOnDocument();
                    ExceptionDispatchInfo? saveError = null;
                    try
                    {
                        await _pdfDocumentService.SaveAsync(() => doc.Save(targetFile), CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        saveError = ExceptionDispatchInfo.Capture(ex);
                    }

                    doc = await RestoreDocumentAsync(doc, tempClean, CancellationToken.None);
                    saveError?.Throw();
                    status = $"Saved — {System.IO.Path.GetFileName(targetFile)}";
                }
                else
                {
                    await _pdfDocumentService.SaveAsync(() => doc.Save(targetFile), CancellationToken.None);
                    status = $"Saved — {System.IO.Path.GetFileName(targetFile)}";
                }
            }

            try
            {
                SetFileOperationBusy(true, "Saving...");
                await RunSaveWithRecoveryAsync(DoSaveAsync);
                MarkDirty(false);
                SetStatus(status);
            }
            catch (Exception ex)
            {
                op.Fail(ex);
                Telemetry.TrackEvent("File.SaveFailed", new Dictionary<string, string>
                {
                    ["Operation"]     = "SaveInPlace",
                    ["ExceptionType"] = ex.GetType().FullName ?? "Unknown",
                });
                SetFileOperationBusy(false);
                TdpDialog.Show(this, $"Save failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetFileOperationBusy(false);
            }
        }

        private async void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save PDF as" };
            // #112: seed the dialog with the document's display name so Save As pre-fills the real
            // filename (not the tdpdf_temp_… working path). Guard every path call: for a merged/
            // imported doc the seed can be null/empty, and Path.GetFileName/GetFileNameWithoutExtension
            // throw on some runtimes — a crash before the dialog opens. A bad seed just opens defaults.
            try
            {
                string? seed = _ctx.DisplayName;
                if (string.IsNullOrWhiteSpace(seed)) seed = _currentFile;
                if (!string.IsNullOrWhiteSpace(seed))
                    dlg.FileName = System.IO.Path.GetFileName(seed);
            }
            catch { /* malformed seed path — just open the dialog with its defaults */ }
            if (dlg.ShowDialog() != true) return;
            using var op = Telemetry.StartOperation("SaveAs");
            string targetFile = dlg.FileName;
            string status = "";

            // Retryable unit of work (see RunSaveWithRecoveryAsync / #106): reads _doc fresh and
            // re-bakes annotations each call so a repaired retry keeps every edit.
            async Task DoSaveAsync()
            {
                var doc = _doc!;
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0) || HasPendingFormValues;

                if (hasAnnotations)
                {
                    var tempClean = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        $"tdpdf_clean_{Guid.NewGuid():N}.pdf");
                    await _pdfDocumentService.SaveAsync(() => doc.Save(tempClean), CancellationToken.None);
                    DrawAnnotationsOnDocument();
                    ExceptionDispatchInfo? saveError = null;
                    try
                    {
                        await _pdfDocumentService.SaveAsync(() => doc.Save(targetFile), CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        saveError = ExceptionDispatchInfo.Capture(ex);
                    }

                    doc = await RestoreDocumentAsync(doc, tempClean, CancellationToken.None);
                    saveError?.Throw();
                    status = $"Saved with annotations to {System.IO.Path.GetFileName(targetFile)}";
                }
                else
                {
                    await _pdfDocumentService.SaveAsync(() => doc.Save(targetFile), CancellationToken.None);
                    status = $"Saved to {System.IO.Path.GetFileName(targetFile)}";
                }
            }

            try
            {
                SetFileOperationBusy(true, "Saving...");
                await RunSaveWithRecoveryAsync(DoSaveAsync);
                MarkDirty(false);
                SetStatus(status);
            }
            catch (Exception ex)
            {
                op.Fail(ex);
                Telemetry.TrackEvent("File.SaveFailed", new Dictionary<string, string>
                {
                    ["Operation"]     = "SaveAs",
                    ["ExceptionType"] = ex.GetType().FullName ?? "Unknown",
                });
                SetFileOperationBusy(false);
                TdpDialog.Show(this, $"Save failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetFileOperationBusy(false);
            }
        }

        private async void SaveFlattened_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save Flattened PDF" };
            if (dlg.ShowDialog() != true) return;
            using var op = Telemetry.StartOperation("SaveFlattened");
            SetFileOperationBusy(true, "Flattening...");
            string targetFile = dlg.FileName;

            // Retryable unit of work (see RunSaveWithRecoveryAsync / #106): the fragile part is the
            // PdfSharpCore doc.Save that produces the flatten source; the raster flatten itself runs
            // through Docnet. Reads _doc fresh and re-bakes annotations each call.
            async Task DoSaveAsync()
            {
                var doc = _doc!;
                var pageSizes = GetPageSizes(doc);
                string sourcePath;
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0) || HasPendingFormValues;
                if (hasAnnotations)
                {
                    var tempClean = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tdpdf_clean_{Guid.NewGuid():N}.pdf");
                    var tempBurned = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tdpdf_burned_{Guid.NewGuid():N}.pdf");
                    await _pdfDocumentService.SaveAsync(() => doc.Save(tempClean), CancellationToken.None);
                    DrawAnnotationsOnDocument();
                    ExceptionDispatchInfo? saveError = null;
                    try
                    {
                        await _pdfDocumentService.SaveAsync(() => doc.Save(tempBurned), CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        saveError = ExceptionDispatchInfo.Capture(ex);
                    }

                    doc = await RestoreDocumentAsync(doc, tempClean, CancellationToken.None);
                    saveError?.Throw();
                    sourcePath = tempBurned;
                }
                else
                {
                    var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tdpdf_src_{Guid.NewGuid():N}.pdf");
                    await _pdfDocumentService.SaveAsync(() => doc.Save(temp), CancellationToken.None);
                    sourcePath = temp;
                }

                await _pdfDocumentService.SaveFlattenedAsync(sourcePath, targetFile, pageSizes, CancellationToken.None);
            }

            try
            {
                await RunSaveWithRecoveryAsync(DoSaveAsync);
                MarkDirty(false);
                SetStatus($"Flattened PDF saved to {System.IO.Path.GetFileName(targetFile)}");
            }
            catch (Exception ex)
            {
                op.Fail(ex);
                Telemetry.TrackEvent("File.SaveFailed", new Dictionary<string, string>
                {
                    ["Operation"]     = "SaveFlattened",
                    ["ExceptionType"] = ex.GetType().FullName ?? "Unknown",
                });
                SetFileOperationBusy(false);
                TdpDialog.Show(this, $"Flatten failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetFileOperationBusy(false);
            }
        }

        // Export tabular text from every page to a single CSV (which Excel opens
        // directly). Read-only: it never mutates the document, so the dirty flag is
        // untouched. Table detection is heuristic — see TableExtractor.
        private async void ExportTablesCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();

            var baseName = System.IO.Path.GetFileNameWithoutExtension(_ctx.DisplayName);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "tables";
            var dlg = new SaveFileDialog
            {
                Filter = "CSV (Comma delimited)|*.csv",
                Title = "Export Tables to CSV",
                FileName = baseName + ".csv"
            };
            if (dlg.ShowDialog() != true) return;

            using var op = Telemetry.StartOperation("ExportTablesCsv");
            SetFileOperationBusy(true, "Exporting tables...");
            try
            {
                string sourcePath = _currentFile;
                var (csv, pages) = await Task.Run(() => TableExtractor.ExtractAllPagesCsv(sourcePath));
                if (pages == 0)
                {
                    SetFileOperationBusy(false);
                    TdpDialog.Show(this, "No extractable text was found to export.\n\nScanned/image-only PDFs have no selectable text to pull into a table.",
                        "TDPdf", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // UTF-8 with BOM so Excel renders accented characters correctly.
                await Task.Run(() => File.WriteAllText(dlg.FileName, csv, new System.Text.UTF8Encoding(true)));
                SetStatus($"Exported {pages} page(s) of tables to {System.IO.Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                op.Fail(ex);
                Telemetry.TrackEvent("File.ExportFailed", new Dictionary<string, string>
                {
                    ["Operation"]     = "ExportTablesCsv",
                    ["ExceptionType"] = ex.GetType().FullName ?? "Unknown",
                });
                SetFileOperationBusy(false);
                TdpDialog.Show(this, $"Export failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetFileOperationBusy(false);
            }
        }

        private async Task<PdfDocument> RestoreDocumentAsync(PdfDocument currentDoc, string cleanPath, CancellationToken cancellationToken)
        {
            var restoredDoc = await _pdfDocumentService.OpenPdfSharpAsync(cleanPath, PdfDocumentOpenMode.Modify, cancellationToken);
            currentDoc.Close();
            _doc = restoredDoc;
            _currentFile = cleanPath;
            return restoredDoc;
        }

        // #106: Runs a save operation and, if it fails with a recoverable PdfSharpCore parse/serialize
        // error ("Cannot retrieve stream length.", "File streams are not yet implemented", a broken
        // xref, ...), repairs the current document through PDFium and retries the save exactly once.
        // The caller's saveAction re-bakes the in-memory annotations/edits every time it runs (via
        // DrawAnnotationsOnDocument, which reads _annotations — never cleared here), so the retried
        // file preserves all of the user's work. If the repair fails or the retry throws, the (final)
        // exception propagates to the caller's themed "Save failed" handler. Recovery is fully guarded
        // and can never itself crash the save.
        private async Task RunSaveWithRecoveryAsync(Func<Task> saveAction)
        {
            try
            {
                await saveAction();
            }
            catch (Exception ex) when (TDPdf.Services.PdfDocumentService.IsXRefException(ex))
            {
                Telemetry.TrackEvent("File.SaveRecoveryAttempt");
                if (!await TryRepairCurrentDocumentForSaveAsync()) throw;   // PDFium couldn't help — surface original
                await saveAction();                                        // retry once against the repaired source
            }
        }

        // #106: Rebuilds the current document through PDFium (which emits clean stream/xref structures)
        // and reopens it in place so a failed save can be retried against a repaired source. Reuses the
        // shared TryPdfiumRepair helper (no second repair implementation). Fully guarded: returns false
        // — never throws — when repair is not possible, leaving the original failure to surface.
        private async Task<bool> TryRepairCurrentDocumentForSaveAsync()
        {
            var current = _currentFile;
            if (_doc is null || string.IsNullOrEmpty(current)) return false;
            try
            {
                var fixedPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    $"tdpdf_fixed_{Guid.NewGuid():N}.pdf");
                bool ok = await System.Threading.Tasks.Task.Run(
                    () => TDPdf.Services.PdfDocumentService.TryPdfiumRepair(current!, fixedPath));
                if (!ok) return false;
                var repaired = await _pdfDocumentService.OpenPdfSharpAsync(
                    fixedPath, PdfDocumentOpenMode.Modify, CancellationToken.None);
                _doc?.Close();
                _doc = repaired;
                _currentFile = fixedPath;
                return true;
            }
            catch { return false; }
        }

        private static IReadOnlyList<PdfPageSize> GetPageSizes(PdfDocument doc)
        {
            var pageSizes = new List<PdfPageSize>(doc.PageCount);
            for (int i = 0; i < doc.PageCount; i++)
                pageSizes.Add(new PdfPageSize(doc.Pages[i].Width.Point, doc.Pages[i].Height.Point));
            return pageSizes;
        }

        private void Print_Click(object sender, RoutedEventArgs e)
        {
            Telemetry.TrackEvent("File.Print");
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();

            // Burn any pending annotations into a temp printable copy (mirroring the
            // old PrintService flow), preview/print from that, then reload the clean
            // document afterward so the on-screen editing state is preserved.
            string? restorePath = null;
            string? printablePath = null;
            try
            {
                var pageSizes = new List<Size>(_doc.PageCount);
                for (int i = 0; i < _doc.PageCount; i++)
                    pageSizes.Add(new Size(_doc.Pages[i].Width.Point, _doc.Pages[i].Height.Point));

                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);

                if (hasAnnotations)
                {
                    string cleanPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tdpdf_clean_{Guid.NewGuid():N}.pdf");
                    _doc.Save(cleanPath);
                    restorePath = cleanPath;

                    DrawAnnotationsOnDocument();
                    printablePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tdpdf_print_{Guid.NewGuid():N}.pdf");
                    _doc.Save(printablePath);
                }
                else
                {
                    // No annotations: save a throwaway printable copy so the preview
                    // window never reads the live file out from under us.
                    string tempPrint = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tdpdf_print_{Guid.NewGuid():N}.pdf");
                    try { _doc.Save(tempPrint); printablePath = tempPrint; }
                    catch { printablePath = _currentFile; }
                }

                var preview = new TDPdf.Services.PrintPreviewWindow(this, printablePath, pageSizes);
                bool? printed = preview.ShowDialog();
                SetStatus(printed == true ? $"Sent {preview.PrintedPageCount} page(s) to printer" : "Print canceled");
            }
            catch (Exception ex)
            {
                Telemetry.TrackEvent("File.PrintFailed", new Dictionary<string, string>
                {
                    ["ExceptionType"] = ex.GetType().FullName ?? "Unknown",
                });
                TdpDialog.Show(this, $"Print failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (restorePath is not null)
                    ReloadPrintedDocument(restorePath);

                // Clean up the temp printable copy (the clean/reload copy is now the
                // live document, so never delete that one).
                if (printablePath is not null && printablePath != _currentFile && printablePath != restorePath)
                    try { File.Delete(printablePath); } catch { /* best effort */ }
            }
        }

        private void ReloadPrintedDocument(string path)
        {
            var previous = _doc;
            PdfDocument reopened;
            try
            {
                reopened = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            }
            catch (Exception ex) when (TDPdf.Services.PdfDocumentService.IsOwnerPasswordException(ex))
            {
                reopened = PdfReader.Open(path, PdfDocumentOpenMode.ReadOnly);
            }
            _doc = reopened;
            _currentFile = path;
            previous?.Close();
        }

        // ============================================================
        // Save annotations to PDF
        // ============================================================

        /// <summary>
        /// Drops all pending form-field values for the active document. Called when the
        /// document object is swapped/reloaded in place (structural edit, undo/redo of a
        /// document change, or open into the current context) because the values are keyed
        /// by widget object number, which is invalidated by a reload.
        /// </summary>
        private void ClearFormState()
        {
            _formTextValues.Clear();
            _formCheckValues.Clear();
            _formRadioValues.Clear();
        }

        /// <summary>True if the user has entered any interactive form-field values pending save.</summary>
        private bool HasPendingFormValues =>
            _formTextValues.Count > 0 || _formCheckValues.Count > 0 || _formRadioValues.Count > 0;

        private void DrawAnnotationsOnDocument()
        {
            if (_doc is null) return;

            // Persist interactive form-field values into the AcroForm before baking
            // annotations, so filled fields are saved alongside drawn annotations.
            WriteFormValuesToDocument();

            // Strip link annotation borders so they don't render as colored rectangles
            // (e.g. strikethrough-like lines) in other PDF viewers.
            StripLinkAnnotationBorders(_doc);

            foreach (var kvp in _annotations)
            {
                int pageIdx = kvp.Key;
                var annots = kvp.Value;
                if (annots.Count == 0 || pageIdx >= _doc.PageCount) continue;
                if (!_renderDims.ContainsKey(pageIdx)) continue;

                var page = _doc.Pages[pageIdx];
                var (renderW, renderH) = _renderDims[pageIdx];
                double sx = page.Width.Point / renderW;
                double sy = page.Height.Point / renderH;

                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

                foreach (var annot in annots)
                {
                    switch (annot)
                    {
                        case TextAnnotation ta:
                        {
                            const double pad = 2;
                            var font = new XFont("Segoe UI", ta.FontSize * sy);
                            double lineH = ta.FontSize * sy * 1.2;
                            var taColor = ta.GetColor();
                            var taBrush = new XSolidBrush(XColor.FromArgb(taColor.A, taColor.R, taColor.G, taColor.B));

                            if (ta.Width > 0)
                            {
                                // Fixed-width wrapping box: mirror the on-screen wrap (same font metrics)
                                // and the whiteout fill so the saved PDF matches the screen.
                                var wrapped = WrapTextToWidth(ta.Content, ta.FontSize, ta.Width - pad * 2);
                                double boxH = ta.Height > 0 ? ta.Height : wrapped.Count * (ta.FontSize * 1.2) + pad * 2;
                                if (ta.HasFill)
                                {
                                    var fc = ta.GetFillColor();
                                    gfx.DrawRectangle(
                                        new XSolidBrush(XColor.FromArgb(fc.A, fc.R, fc.G, fc.B)),
                                        ta.Position.X * sx, ta.Position.Y * sy,
                                        ta.Width * sx, boxH * sy);
                                }
                                double ty = ta.Position.Y * sy + pad * sy + ta.FontSize * sy;
                                foreach (var line in wrapped)
                                {
                                    if (!string.IsNullOrEmpty(line))
                                        gfx.DrawString(line, font, taBrush, (ta.Position.X + pad) * sx, ty);
                                    ty += lineH;
                                }
                            }
                            else
                            {
                                // Legacy auto-size: unchanged newline-split rendering (with optional fill).
                                var lines = ta.Content.Split('\n');
                                if (ta.HasFill)
                                {
                                    var sz = MeasureTextAnnotation(ta);
                                    var fc = ta.GetFillColor();
                                    gfx.DrawRectangle(
                                        new XSolidBrush(XColor.FromArgb(fc.A, fc.R, fc.G, fc.B)),
                                        ta.Position.X * sx, ta.Position.Y * sy,
                                        sz.Width * sx, sz.Height * sy);
                                }
                                double ty = ta.Position.Y * sy + ta.FontSize * sy;
                                foreach (var line in lines)
                                {
                                    if (!string.IsNullOrEmpty(line))
                                        gfx.DrawString(line, font, taBrush, ta.Position.X * sx, ty);
                                    ty += lineH;
                                }
                            }
                            break;
                        }

                        case HighlightAnnotation ha:
                            var hc = ha.GetColor();
                            var hBrush = new XSolidBrush(XColor.FromArgb(hc.A, hc.R, hc.G, hc.B));
                            gfx.DrawRectangle(hBrush,
                                ha.Bounds.X * sx, ha.Bounds.Y * sy,
                                ha.Bounds.Width * sx, ha.Bounds.Height * sy);
                            break;

                        case ShapeAnnotation shp:
                        {
                            var stk = shp.GetStrokeColor();
                            var shpPen = new XPen(XColor.FromArgb(stk.A, stk.R, stk.G, stk.B), shp.StrokeWidth * sx)
                            {
                                LineJoin = XLineJoin.Round,
                                LineCap = XLineCap.Round
                            };
                            XSolidBrush? shpFill = null;
                            if (shp.HasFill)
                            {
                                var fc = shp.GetFillColor();
                                shpFill = new XSolidBrush(XColor.FromArgb(fc.A, fc.R, fc.G, fc.B));
                            }
                            switch (shp.Kind)
                            {
                                case ShapeKind.Rectangle:
                                {
                                    var b = shp.Bounds;
                                    double rx = b.X * sx, ry = b.Y * sy;
                                    double rw = b.Width * sx, rh = b.Height * sy;
                                    if (shpFill is not null) gfx.DrawRectangle(shpFill, rx, ry, rw, rh);
                                    gfx.DrawRectangle(shpPen, rx, ry, rw, rh);
                                    break;
                                }
                                case ShapeKind.Ellipse:
                                {
                                    var b = shp.Bounds;
                                    double rx = b.X * sx, ry = b.Y * sy;
                                    double rw = b.Width * sx, rh = b.Height * sy;
                                    if (shpFill is not null) gfx.DrawEllipse(shpFill, rx, ry, rw, rh);
                                    gfx.DrawEllipse(shpPen, rx, ry, rw, rh);
                                    break;
                                }
                                case ShapeKind.Line:
                                    gfx.DrawLine(shpPen,
                                        shp.Start.X * sx, shp.Start.Y * sy,
                                        shp.End.X * sx,   shp.End.Y * sy);
                                    break;
                            }
                            break;
                        }

                        case InkAnnotation ia:
                            if (ia.Points.Count < 2) break;
                            var ic = ia.GetColor();
                            var pen = new XPen(XColor.FromArgb(ic.A, ic.R, ic.G, ic.B), ia.StrokeWidth * sx)
                            {
                                LineJoin = XLineJoin.Round,
                                LineCap = XLineCap.Round
                            };
                            for (int i = 0; i < ia.Points.Count - 1; i++)
                            {
                                gfx.DrawLine(pen,
                                    ia.Points[i].X * sx, ia.Points[i].Y * sy,
                                    ia.Points[i + 1].X * sx, ia.Points[i + 1].Y * sy);
                            }
                            break;

                        case TextEditAnnotation tea:
                            // PdfSharpCore cannot surgically edit existing PDF content streams here.
                            // Existing text/image edits are approximated by painting a white rectangle
                            // over the original region, then drawing replacement content on top.
                            var whiteRect = new XSolidBrush(XColors.White);
                            gfx.DrawRectangle(whiteRect,
                                (tea.OriginalBounds.X - 2) * sx, (tea.OriginalBounds.Y - 2) * sy,
                                (tea.OriginalBounds.Width + 4) * sx, (tea.OriginalBounds.Height + 4) * sy);
                            // Draw replacement text
                            var editFont = new XFont(tea.FontName, tea.FontSize * sy);
                            double ety = tea.Position.Y * sy + tea.FontSize * sy;
                            gfx.DrawString(tea.NewContent, editFont, XBrushes.Black, tea.Position.X * sx, ety);
                            break;

                        case ImageEditAnnotation iea:
                            var imageWhiteRect = new XSolidBrush(XColors.White);
                            gfx.DrawRectangle(imageWhiteRect,
                                (iea.OriginalBounds.X - 2) * sx, (iea.OriginalBounds.Y - 2) * sy,
                                (iea.OriginalBounds.Width + 4) * sx, (iea.OriginalBounds.Height + 4) * sy);
                            if (!iea.IsDeleted)
                            {
                                try
                                {
                                    XImage? xImg = null;
                                    if (!string.IsNullOrWhiteSpace(iea.ReplacementImagePath) && System.IO.File.Exists(iea.ReplacementImagePath))
                                    {
                                        xImg = XImage.FromFile(iea.ReplacementImagePath);
                                    }
                                    else if (!string.IsNullOrEmpty(iea.OriginalImageData))
                                    {
                                        var imageBytes = Convert.FromBase64String(iea.OriginalImageData);
                                        xImg = XImage.FromStream(() => new MemoryStream(imageBytes));
                                    }

                                    if (xImg is not null)
                                    {
                                        gfx.DrawImage(xImg,
                                            iea.TargetBounds.X * sx, iea.TargetBounds.Y * sy,
                                            iea.TargetBounds.Width * sx, iea.TargetBounds.Height * sy);
                                    }
                                }
                                catch { /* skip broken image edit */ }
                            }
                            break;

                        case SignatureAnnotation sa:
                            if (sa.ImageData is not null)
                            {
                                try
                                {
                                    var imgBytes = Convert.FromBase64String(sa.ImageData);
                                    var xImg = XImage.FromStream(() => new System.IO.MemoryStream(imgBytes));
                                    double imgX = sa.Position.X * sx;
                                    double imgY = sa.Position.Y * sy;
                                    double imgW = sa.SourceWidth * sa.Scale * sx;
                                    double imgH = sa.SourceHeight * sa.Scale * sy;
                                    gfx.DrawImage(xImg, imgX, imgY, imgW, imgH);
                                }
                                catch { /* skip broken image */ }
                            }
                            else
                            {
                                var sigPen = new XPen(XColors.Black, 2 * sa.Scale * sx)
                                {
                                    LineJoin = XLineJoin.Round,
                                    LineCap = XLineCap.Round
                                };
                                foreach (var stroke in sa.Strokes)
                                {
                                    for (int i = 0; i < stroke.Count - 1; i++)
                                    {
                                        double x1 = (sa.Position.X + stroke[i].X * sa.Scale) * sx;
                                        double y1 = (sa.Position.Y + stroke[i].Y * sa.Scale) * sy;
                                        double x2 = (sa.Position.X + stroke[i + 1].X * sa.Scale) * sx;
                                        double y2 = (sa.Position.Y + stroke[i + 1].Y * sa.Scale) * sy;
                                        gfx.DrawLine(sigPen, x1, y1, x2, y2);
                                    }
                                }
                            }
                            break;

                        case ImageAnnotation ia:
                            try
                            {
                                var iaBytes = Convert.FromBase64String(ia.ImageData);
                                var xia = XImage.FromStream(() => new System.IO.MemoryStream(iaBytes));
                                double iaX = ia.Position.X * sx;
                                double iaY = ia.Position.Y * sy;
                                double iaW = ia.SourceWidth * ia.Scale * sx;
                                double iaH = ia.SourceHeight * ia.Scale * sy;
                                gfx.DrawImage(xia, iaX, iaY, iaW, iaH);
                            }
                            catch { /* skip broken image */ }
                            break;
                    }
                }
            }
        }

        // ============================================================
        // Temp save/reload
        // ============================================================

        private void SaveTempAndReload()
        {
            if (_doc is null || _currentFile is null) return;
            _annotations.Clear();
            ClearFormState();
            InvalidateRenderCache();
            _contentEditor.ClearCache();
            ClearSelection();
            MarkDirty();
            var doc = _doc;
            int selectedIdx = PageList.SelectedIndex;
            var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"tdpdf_temp_{Guid.NewGuid():N}.pdf");
            doc.Save(tempPath);
            doc.Close();
            // PdfSharpCore can silently produce a broken cross-reference entry when re-saving an
            // encrypted (owner-restricted RC4) PDF after a modification such as a page rotation:
            // the save succeeds but re-opening the result in Modify mode then throws "Unexpected
            // token 'xref'". Catch that reopen failure and pipe the saved file (which already
            // carries the new /Rotate values) through PDFium, which rebuilds a valid xref and
            // strips encryption while preserving the rotation, then retry the open.
            try
            {
                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
            }
            catch (Exception openEx) when (TDPdf.Services.PdfDocumentService.IsXRefException(openEx))
            {
                var fixedPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    $"tdpdf_fixed_{Guid.NewGuid():N}.pdf");
                if (!TDPdf.Services.PdfDocumentService.TryPdfiumRepair(tempPath, fixedPath))
                    throw; // PDFium also failed — re-throw the original reopen error
                tempPath = fixedPath;
                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
            }
            _currentFile = tempPath;
            RefreshPageList();
            if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                PageList.SelectedIndex = selectedIdx;
            else if (PageList.Items.Count > 0)
                PageList.SelectedIndex = 0;

            // Continuous view caches one slot per page; the page set may have changed (rotate,
            // delete, reorder, crop) so rebuild the strip from the reloaded document.
            if (_viewMode == ViewMode.Continuous)
            {
                int contIdx = Math.Max(0, PageList.SelectedIndex);
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => SetupContinuousView(contIdx)));
            }
        }

        // ============================================================
        // Zoom
        // ============================================================

        private enum ZoomChange
        {
            In,
            Out,
            Reset
        }

        private void PagePreview_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                _zoomFitMode = ZoomFitMode.None;
                if (e.Delta > 0) Zoom.ZoomIn();
                else Zoom.ZoomOut();
                return;
            }

            // Shift+wheel scrolls horizontally (industry-standard), at the boosted speed.
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                if (PagePreviewPanel.ScrollableWidth > 0)
                    PagePreviewPanel.ScrollToHorizontalOffset(
                        PagePreviewPanel.HorizontalOffset - e.Delta * (48.0 / 120.0) * WheelScrollFactor);
                e.Handled = true;
                return;
            }

            // Grid and Continuous are a single scroll over the WHOLE document, so the wheel must
            // never be hijacked for page navigation there — it always just scrolls (boosted).
            if (_viewMode == ViewMode.Grid || _viewMode == ViewMode.Continuous)
            {
                ScrollWheel(e);
                return;
            }

            // Single / Two-Page: a page often fits the viewport, so at the scroll boundary fall
            // through to page navigation so the user can reach adjacent pages without the sidebar.
            if (PagePreviewPanel.ScrollableHeight <= 0)
            {
                // No scrollable content — wheel navigates pages directly.
                e.Handled = true;
                NavigatePageByWheel(e.Delta);
                return;
            }

            bool atTop    = PagePreviewPanel.VerticalOffset <= 0;
            bool atBottom = PagePreviewPanel.VerticalOffset >= PagePreviewPanel.ScrollableHeight - 1;
            if ((atTop && e.Delta > 0) || (atBottom && e.Delta < 0))
            {
                e.Handled = true;
                NavigatePageByWheel(e.Delta);
                return;
            }
            ScrollWheel(e);   // normal scroll, boosted
        }

        // The ScrollViewer default (3 lines = 48 DIP per wheel notch) feels slow on tall documents,
        // so scroll WheelScrollFactor times that instead. e.Delta is ±120 per notch on a standard
        // wheel (precision touchpads send smaller, more frequent deltas, which scale the same way).
        // ScrollToVerticalOffset clamps to the valid range itself.
        private const double WheelScrollFactor = 3.0;

        private void ScrollWheel(MouseWheelEventArgs e)
        {
            e.Handled = true;
            PagePreviewPanel.ScrollToVerticalOffset(
                PagePreviewPanel.VerticalOffset - e.Delta * (48.0 / 120.0) * WheelScrollFactor);
        }

        private void NavigatePageByWheel(int delta)
        {
            if (_doc is null) return;
            int cur = PageList.SelectedIndex;
            if (delta > 0 && cur > 0)
                PageList.SelectedIndex = cur - 1;
            else if (delta < 0 && cur < _doc.PageCount - 1)
                PageList.SelectedIndex = cur + 1;
        }

        private void Zoom_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ZoomViewModel.ZoomLevel))
                ApplyZoom();
        }

        private void ChangeZoomByCommand(ZoomChange change)
        {
            switch (change)
            {
                case ZoomChange.In:
                    Zoom.ZoomIn();
                    break;
                case ZoomChange.Out:
                    Zoom.ZoomOut();
                    break;
                case ZoomChange.Reset:
                    Zoom.Reset();
                    break;
            }
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e) { _zoomFitMode = ZoomFitMode.None; Zoom.ZoomIn(); }

        private void ZoomOut_Click(object sender, RoutedEventArgs e) { _zoomFitMode = ZoomFitMode.None; Zoom.ZoomOut(); }

        private void ResetZoom_Click(object sender, RoutedEventArgs e) { _zoomFitMode = ZoomFitMode.None; Zoom.Reset(); }

        private void ApplyZoom()
        {
            if (_pageContentGrid.LayoutTransform is ScaleTransform st)
            {
                st.ScaleX = Zoom.ZoomLevel;
                st.ScaleY = Zoom.ZoomLevel;
            }
            CommitActiveTextBox();
            SaveZoomSetting();

            // Continuous view is scaled entirely by the shared ScaleTransform above; it must not
            // re-render the (hidden) primary page. Re-anchor the scroll to the current page so the
            // view doesn't jump when zooming.
            if (_viewMode == ViewMode.Continuous)
            {
                int curIdx = PageList.SelectedIndex;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => ScrollContinuousToPageSuppressed(curIdx)));
                // #85: once the zoom settles, sharpen the visible pages (zoom-in) or restore their
                // base bitmaps so hi-res memory is released (zoom-out). Debounced + cancellable.
                StartContinuousResharpen();
                return;
            }

            // Recalculate how many pages fit after zoom changes.
            // Use RefreshPageView so link overlays are re-added after RenderAdditionalPages
            // calls ClearSecondaryPages (which wipes them).
            int applyIdx = PageList.SelectedIndex;
            if (applyIdx >= 0 && _doc != null)
            {
                RenderPage(applyIdx);
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => RefreshPageView(applyIdx));
            }
        }

        private void SaveZoomSetting()
        {
            try
            {
                TDPdf.Properties.Settings.Default.LastZoomLevel = Zoom.ZoomLevel;
                TDPdf.Properties.Settings.Default.Save();
            }
            catch
            {
                // Non-critical user preference; rendering should continue even if settings cannot be saved.
            }
        }

        private void ZoomBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_zoomBox?.SelectedItem is not ZoomLevelOption option) return;
            if (option.IsFitWidth) { FitToWidth(); return; }
            if (option.IsFitPage) { FitToPage(); return; }
            // User picked an explicit zoom level — stop tracking the window size.
            _zoomFitMode = ZoomFitMode.None;
            if (option.ZoomLevel is double zoom) Zoom.SetZoomLevel(zoom);
        }

        private void FitToWidth()
        {
            double viewW = PagePreviewPanel.ActualWidth - 40;
            if (viewW <= 0) return;
            // Continuous view fits against the strip's natural page width (zoom-independent), not
            // the hidden primary PageImage.
            if (_viewMode == ViewMode.Continuous)
            {
                if (_continuousPageW <= 0) return;
                _zoomFitMode = ZoomFitMode.Width;
                _applyingFitZoom = true;
                try { Zoom.SetZoomLevel(viewW / _continuousPageW); }
                finally { _applyingFitZoom = false; }
                return;
            }
            if (PageImage.Source is null || PageImage.ActualWidth <= 0) return;
            _zoomFitMode = ZoomFitMode.Width;
            _applyingFitZoom = true;
            try { Zoom.SetZoomLevel(viewW / PageImage.ActualWidth); }
            finally { _applyingFitZoom = false; }
        }

        private void FitToPage()
        {
            if (PageImage.Source is null || PageImage.ActualWidth <= 0 || PageImage.ActualHeight <= 0) return;
            double viewW = PagePreviewPanel.ActualWidth - 40;
            double viewH = PagePreviewPanel.ActualHeight - 40;
            if (viewW <= 0 || viewH <= 0) return;
            _zoomFitMode = ZoomFitMode.Page;
            _applyingFitZoom = true;
            try { Zoom.SetZoomLevel(Math.Min(viewW / PageImage.ActualWidth, viewH / PageImage.ActualHeight)); }
            finally { _applyingFitZoom = false; }
        }

        private void PagePreviewPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_zoomFitMode == ZoomFitMode.None) return;
            // Debounce: coalesce a burst of size-changed events into one re-fit at Loaded
            // priority so we don't fight the WPF layout pass.
            if (_fitResizePending) return;
            _fitResizePending = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                _fitResizePending = false;
                if (_zoomFitMode == ZoomFitMode.Width) FitToWidth();
                else if (_zoomFitMode == ZoomFitMode.Page) FitToPage();
            });
        }

        private void PageContentGrid_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
        {
            double scale = (e.DeltaManipulation.Scale.X + e.DeltaManipulation.Scale.Y) / 2.0;
            if (Math.Abs(scale - 1.0) < 0.01) return;
            Zoom.SetZoomLevel(Zoom.ZoomLevel * scale);
            e.Handled = true;
        }

        // ============================================================
        // Drag/drop: file open
        // ============================================================

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DropHasOpenableContent(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        // A drop is accepted if it contains at least one openable file (PDF/image), a folder,
        // or a .zip archive — so the copy cursor shows for folders/zips/images/multi-file drops,
        // not just a single .pdf.
        private static bool DropHasOpenableContent(IDataObject data)
        {
            if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
            if (data.GetData(DataFormats.FileDrop) is not string[] paths) return false;
            foreach (var p in paths)
            {
                if (Directory.Exists(p)) return true;
                if (p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return true;
                if (IsOpenablePath(p)) return true;
            }
            return false;
        }

        private async void DropZone_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                await OnPathsDropped(paths);
        }

        private void DropZone_Click(object sender, MouseButtonEventArgs e) => Open_Click(sender, e);

        // ============================================================
        // Drag/drop: folders, .zip archives, images, and multi-file drops
        // ============================================================
        // Entry point for any file/folder/archive drop. Expands dropped folders (recursively)
        // and .zip archives, then opens the collected PDFs/images — asking merge-vs-separate
        // when there is more than one.

        private static readonly string[] DropImageExt = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };
        private static bool IsPdfPath(string p)      => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        private static bool IsImagePath(string p)    => DropImageExt.Any(ext => p.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        private static bool IsOpenablePath(string p) => IsPdfPath(p) || IsImagePath(p);

        private async Task OnPathsDropped(string[] paths)
        {
            var found    = new List<string>();
            var tempDirs = new List<string>();   // extracted-zip temp dirs we may need to clean up
            bool expanded = false;               // a folder or archive was expanded
            try
            {
                foreach (var p in paths)
                {
                    if (Directory.Exists(p)) { expanded = true; CollectOpenable(p, found); }
                    else if (p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        var dir = ExtractZipToTemp(p);
                        if (dir != null) { expanded = true; tempDirs.Add(dir); CollectOpenable(dir, found); }
                    }
                    else if (IsOpenablePath(p)) found.Add(p);
                }
            }
            catch (Exception ex)
            {
                CleanupDirs(tempDirs);
                TdpDialog.Show(this, $"Could not read the dropped items:\n{ex.Message}",
                    "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (found.Count == 0)
            {
                CleanupDirs(tempDirs);
                SetStatus("Nothing to open — drop PDFs, images, folders, or .zip archives");
                return;
            }

            // Guard against a folder/archive holding a huge number of files: opening or merging them
            // all could exhaust memory. Cap to a sane maximum, opening only the first N (name-sorted).
            const int MaxDropFiles = 50;
            if (found.Count > MaxDropFiles)
            {
                int total = found.Count;
                var proceed = TdpDialog.Show(this,
                    $"The drop contains {total} openable files. Open only the first {MaxDropFiles} (sorted by name)?",
                    "TDPdf", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (proceed != MessageBoxResult.OK) { CleanupDirs(tempDirs); SetStatus("Drop cancelled"); return; }
                found.Sort(StringComparer.OrdinalIgnoreCase);
                found = found.GetRange(0, MaxDropFiles);
                SetStatus($"Opening the first {MaxDropFiles} of {total} dropped files");
            }

            // A single dropped file (no folder/zip expansion) opens directly, preserving today's
            // single-PDF-drop behavior.
            if (!expanded && found.Count == 1) { await OpenDroppedAsync(found[0]); return; }

            var choice = TdpDialog.Show(this,
                $"Open {found.Count} items?\n\nYes = merge them into one PDF\nNo = open each in its own tab",
                "TDPdf", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (choice == MessageBoxResult.Yes)
                await OpenMergedAsync(found, tempDirs);       // async: builds on a background thread, then owns temp cleanup
            else if (choice == MessageBoxResult.No)
                await OpenSeparatelyAsync(found, tempDirs);   // keep temp for the session; opened docs may reference extracted files
            else
                CleanupDirs(tempDirs);                        // cancelled
        }

        private async Task OpenDroppedAsync(string path)
        {
            if (IsPdfPath(path)) await OpenInTabAsync(path);
            else await OpenImagesAsImportedTabAsync(new[] { path }, System.IO.Path.GetFileName(path));
        }

        private async Task OpenSeparatelyAsync(List<string> found, List<string> tempDirs)
        {
            foreach (var f in found)
            {
                if (IsPdfPath(f)) await OpenInTabAsync(f);
                else await OpenImagesAsImportedTabAsync(new[] { f }, System.IO.Path.GetFileName(f));
            }
            SetStatus($"Opened {found.Count} item(s) in separate tabs");
            // Extracted-zip temp dirs are intentionally NOT deleted here: an imported-image tab keeps
            // no handle, but a PDF opened directly from an extracted folder is read lazily, so the
            // extracted files must survive for the session. The OS clears %TEMP% eventually.
        }

        // Builds ONE combined PDF from the dropped files on a background thread, then opens it as an
        // unsaved tab (Save routes to Save As). Owns cleanup of the extracted-zip temp dirs.
        private async Task OpenMergedAsync(List<string> found, List<string> tempDirs)
        {
            SetStatus($"Merging {found.Count} dropped items…");
            string? tempPath;
            try
            {
                // Build off the UI thread so the window stays responsive while it works.
                tempPath = await Task.Run(() => BuildCombinedPdf(found));
            }
            catch (Exception ex)
            {
                CleanupDirs(tempDirs);
                TdpDialog.Show(this, $"Could not merge the dropped files:\n{ex.Message}",
                    "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (tempPath is null)
            {
                CleanupDirs(tempDirs);
                SetStatus("Nothing could be read from the dropped files");
                return;
            }

            await OpenInTabAsync(tempPath);
            FinalizeUnsavedTab(tempPath, "Combined.pdf", $"Merged {found.Count} item(s) into one PDF");
            CleanupDirs(tempDirs);
        }

        // Opens the given image(s) as a single unsaved imported-PDF tab (same UNSAVED flow as New:
        // dirty orange Save icon, Save routes to Save As, Close warns).
        private async Task OpenImagesAsImportedTabAsync(string[] images, string displayName)
        {
            string tempPath;
            try { tempPath = BuildPdfFromImages(images); }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Could not import the image(s):\n{ex.Message}",
                    "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            await OpenInTabAsync(tempPath);
            FinalizeUnsavedTab(tempPath, displayName, $"Imported {images.Length} image(s)");
        }

        // After OpenInTabAsync has loaded a temp working file, mark the active tab as unsaved work.
        // Guarded so a failed/reverted open (which returns to the previous tab) is left untouched.
        private void FinalizeUnsavedTab(string tempPath, string displayName, string status)
        {
            if (_doc is null || !string.Equals(_currentFile, tempPath, StringComparison.OrdinalIgnoreCase))
                return;   // open failed and reverted to a different tab — don't clobber it
            SetDisplayName(displayName);
            _ctx.IsUntitled = true;   // no real on-disk home yet → Ctrl+S routes to Save As
            MarkDirty(true);
            SetStatus(status);
            RebuildTabStrip();
        }

        // Recursively gathers the PDFs and images under a folder, in a stable name order.
        private static void CollectOpenable(string dir, List<string> found)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories); }
            catch { return; }
            foreach (var f in files.Where(IsOpenablePath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                found.Add(f);
        }

        private static string? ExtractZipToTemp(string zipPath)
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tdpdf-zip-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(dir);
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, dir);
                return dir;
            }
            catch { try { Directory.Delete(dir, true); } catch { } return null; }
        }

        private static void CleanupDirs(List<string> dirs)
        {
            foreach (var d in dirs) { try { Directory.Delete(d, true); } catch { } }
        }

        // Builds one PDF from a mix of PDFs (pages imported in order) and images (one page each).
        // Unreadable / encrypted entries are skipped rather than aborting the whole merge. Returns
        // a temp PDF path, or null if nothing could be read.
        private static string? BuildCombinedPdf(List<string> files)
        {
            using var outPdf = new PdfDocument();
            foreach (var f in files)
            {
                if (IsPdfPath(f))
                {
                    try
                    {
                        using var src = PdfReader.Open(f, PdfDocumentOpenMode.Import);
                        for (int i = 0; i < src.PageCount; i++) outPdf.AddPage(src.Pages[i]);
                    }
                    catch { /* skip an unreadable/encrypted PDF */ }
                }
                else
                {
                    try { AddImagePagesFromFile(outPdf, f); } catch { /* skip an unreadable image */ }
                }
            }

            if (outPdf.PageCount == 0) return null;
            string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tdpdf_combined_{Guid.NewGuid():N}.pdf");
            outPdf.Save(outPath);
            return outPath;
        }

        // Builds a PDF where each page is exactly one source image (multi-frame TIFF/GIF expand to one
        // page per frame). Page size matches the image's physical size at its own DPI (96 if none).
        private static string BuildPdfFromImages(string[] imagePaths)
        {
            using var pdf = new PdfDocument();
            foreach (var path in imagePaths) AddImagePagesFromFile(pdf, path);
            if (pdf.PageCount == 0) throw new InvalidOperationException("No images could be read.");
            string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tdpdf_imported_{Guid.NewGuid():N}.pdf");
            pdf.Save(outPath);
            return outPath;
        }

        // Appends one page per image frame. Page size matches the image's physical size at its own DPI.
        private static void AddImagePagesFromFile(PdfDocument pdf, string path)
        {
            using var img = System.Drawing.Image.FromFile(path);
            var dim = new System.Drawing.Imaging.FrameDimension(img.FrameDimensionsList[0]);
            int frameCount = Math.Max(1, img.GetFrameCount(dim));

            for (int f = 0; f < frameCount; f++)
            {
                img.SelectActiveFrame(dim, f);

                int wpx = img.Width, hpx = img.Height;
                double dpiX = img.HorizontalResolution > 0 ? img.HorizontalResolution : 96.0;
                double dpiY = img.VerticalResolution   > 0 ? img.VerticalResolution   : 96.0;
                double wPt = wpx * 72.0 / dpiX;
                double hPt = hpx * 72.0 / dpiY;

                // Copy the active frame to a fresh 32bpp bitmap, then encode PNG (XImage reads that).
                byte[] png;
                using (var frame = new System.Drawing.Bitmap(wpx, hpx,
                           System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(frame))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(img, 0, 0, wpx, hpx);
                    }
                    using var ms = new MemoryStream();
                    frame.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    png = ms.ToArray();
                }

                var page = pdf.AddPage();
                page.Width  = wPt;   // XUnit implicitly treats a double as points
                page.Height = hPt;

                using var gfx  = XGraphics.FromPdfPage(page);
                using var xImg = XImage.FromStream(() => new MemoryStream(png));
                gfx.DrawImage(xImg, 0, 0, wPt, hPt);
            }
        }

        // ============================================================
        // Recent files (MRU) — most-recent first, capped at 10
        // ============================================================
        private const int RecentFilesMax = 10;

        // A path is eligible for the recent list only if it is a genuine, existing on-disk file that
        // does NOT live under the temp directory — that is where New / merged-on-drop / imported-image
        // working copies live, and those have no lasting saved location worth remembering.
        private static bool IsRecentEligiblePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                if (!System.IO.File.Exists(path)) return false;
                string full = System.IO.Path.GetFullPath(path);
                string temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
                return !full.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // Reads the stored list, dropping any entry that no longer exists on disk.
        private static List<string> GetRecentFiles()
        {
            var list = new List<string>();
            var raw = TDPdf.Properties.Settings.Default.RecentFiles;
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (var p in raw.Split('|'))
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try { if (System.IO.File.Exists(p)) list.Add(p); } catch { }
            }
            return list;
        }

        private void AddRecentFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string normalized;
            try { normalized = System.IO.Path.GetFullPath(path); } catch { normalized = path; }

            var list = GetRecentFiles();
            list.RemoveAll(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, normalized);
            while (list.Count > RecentFilesMax) list.RemoveAt(list.Count - 1);

            TDPdf.Properties.Settings.Default.RecentFiles = string.Join("|", list);   // '|' is illegal in Windows paths
            TDPdf.Properties.Settings.Default.Save();
            RefreshRecentFilesUi();
        }

        private void ClearRecentFiles()
        {
            TDPdf.Properties.Settings.Default.RecentFiles = "";
            TDPdf.Properties.Settings.Default.Save();
            RefreshRecentFilesUi();
        }

        private void ClearRecent_Click(object sender, RoutedEventArgs e) => ClearRecentFiles();

        // Rebuilds the start-screen recent-files list (hidden when there are none).
        private void RefreshRecentFilesUi()
        {
            _recentFilesList.Children.Clear();
            var recents = GetRecentFiles();
            if (recents.Count == 0) { _recentFilesBox.Visibility = Visibility.Collapsed; return; }
            _recentFilesBox.Visibility = Visibility.Visible;
            foreach (var path in recents)
                _recentFilesList.Children.Add(MakeRecentRow(path));
        }

        // A single clickable recent-files row: PDF glyph + filename (primary) + dimmed full path.
        private Button MakeRecentRow(string path)
        {
            string fileName = System.IO.Path.GetFileName(path);

            var glyph = new TextBlock
            {
                Text       = "\uE8A5",   // Segoe MDL2 "Document"
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize   = 18,
                Foreground = BrushResource("AccentGreen"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin     = new Thickness(0, 0, 10, 0)
            };

            var nameText = new TextBlock
            {
                Text         = fileName,
                FontFamily   = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize     = 13,
                Foreground   = BrushResource("TextPrimary"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var pathText = new TextBlock
            {
                Text         = path,
                FontFamily   = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize     = 11,
                Foreground   = BrushResource("TextSecondary"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textCol.Children.Add(nameText);
            textCol.Children.Add(pathText);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(glyph, 0);
            Grid.SetColumn(textCol, 1);
            grid.Children.Add(glyph);
            grid.Children.Add(textCol);

            var normal = (System.Windows.Media.Brush)System.Windows.Media.Brushes.Transparent;
            var hover  = BrushResource("BgHover");
            var btn = new Button
            {
                Content                    = grid,
                Background                 = normal,
                BorderThickness            = new Thickness(0),
                Padding                    = new Thickness(8, 6, 8, 6),
                Cursor                     = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Template                   = MakeRecentRowTemplate(),
                ToolTip                    = path
            };
            AutomationProperties.SetName(btn, "Open " + fileName);
            btn.MouseEnter += (_, _2) => btn.Background = hover;
            btn.MouseLeave += (_, _2) => btn.Background = normal;
            btn.Click      += async (_, _2) => await OpenRecentAsync(path);
            return btn;
        }

        // Minimal chrome-free button template (Border + ContentPresenter) so the row shows only our
        // hover background — mirrors the pattern used by TdpDialog's buttons.
        private static ControlTemplate MakeRecentRowTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetBinding(Border.PaddingProperty,
                new System.Windows.Data.Binding("Padding")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }

        private async Task OpenRecentAsync(string path)
        {
            if (System.IO.File.Exists(path)) { await OpenInTabAsync(path); return; }
            TdpDialog.Show(this, $"That file is no longer available:\n{path}",
                "TDPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshRecentFilesUi();   // drop the now-missing entry
        }

        // Recent-files dropdown on the Open toolbar button (right-click).
        private void OpenBtn_RightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var menu = new ContextMenu();
            var recents = GetRecentFiles();
            if (recents.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = "No recent files", IsEnabled = false });
            }
            else
            {
                foreach (var p in recents)
                {
                    string path = p;   // capture
                    var item = MakeMenuItem(System.IO.Path.GetFileName(path), async (_, _2) => await OpenRecentAsync(path));
                    item.ToolTip = path;
                    menu.Items.Add(item);
                }
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem("Clear List", (_, _2) => ClearRecentFiles()));
            }
            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // ============================================================
        // Drag/drop: page reorder
        // ============================================================

        private void PageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
            _dragStartPoint = e.GetPosition(null);

        private void PageList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var diff = _dragStartPoint - e.GetPosition(null);
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (PageList.SelectedIndex >= 0)
                    DragDrop.DoDragDrop(PageList, PageList.SelectedIndex, DragDropEffects.Move);
            }
        }

        private void PageList_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(int)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void PageList_Drop(object sender, DragEventArgs e)
        {
            if (_doc is null || !e.Data.GetDataPresent(typeof(int))) return;
            var doc = _doc;
            int fromIdx = (int)e.Data.GetData(typeof(int))!;
            var pos = e.GetPosition(PageList);
            int toIdx = PageList.Items.Count - 1;
            for (int i = 0; i < PageList.Items.Count; i++)
            {
                if (PageList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item)
                {
                    var itemPos = item.TranslatePoint(new Point(0, item.ActualHeight / 2), PageList);
                    if (pos.Y < itemPos.Y) { toIdx = i; break; }
                }
            }
            if (fromIdx == toIdx) return;
            var page = doc.Pages[fromIdx];
            doc.Pages.RemoveAt(fromIdx);
            if (toIdx > fromIdx) toIdx--;
            doc.Pages.Insert(toIdx, page);
            SaveTempAndReload();
            PageList.SelectedIndex = toIdx;
        }

        // ============================================================
        // Page selection handler
        // ============================================================

        private void PageList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // The ListBox's internal ScrollViewer is disabled, so wheel events don't
            // scroll anything. Forward them to the outer SidebarScrollViewer manually,
            // at the same boosted speed as the document viewport (WheelScrollFactor).
            SidebarScrollViewer.ScrollToVerticalOffset(
                SidebarScrollViewer.VerticalOffset - e.Delta * (48.0 / 120.0) * WheelScrollFactor);
            e.Handled = true;
        }

        private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PageList.SelectedIndex >= 0)
            {
                CommitActiveTextBox();
                ClearSelection();
                ClearTextSelection();
                ClearCropSelection();
                _pageJumpBox.Text = (PageList.SelectedIndex + 1).ToString();

                // Continuous view: the whole document is one scroll, so a sidebar selection
                // scrolls the strip rather than re-rendering a single page. The scroll-sync
                // suppression flag avoids a feedback loop with PagePreviewPanel_ScrollChanged.
                if (_viewMode == ViewMode.Continuous)
                {
                    if (!_suppressContinuousScrollSync)
                        ScrollContinuousToPageSuppressed(PageList.SelectedIndex);
                    return;
                }

                PagePreviewPanel.ScrollToTop();
                RenderPage(PageList.SelectedIndex);
                // Re-highlight search results on this page if a search is active
                if (_searchBar is not null && _searchBar.Visibility == Visibility.Visible
                    && _allSearchRects.Count > 0)
                    HighlightSearchResultsOnCurrentPage();
            }
        }

        private void PageJumpBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || _doc is null) return;
            e.Handled = true;
            if (int.TryParse(_pageJumpBox.Text, out int pg))
            {
                int idx = Math.Clamp(pg - 1, 0, _doc.PageCount - 1);
                PageList.SelectedIndex = idx;
            }
            else
            {
                _pageJumpBox.Text = (PageList.SelectedIndex + 1).ToString();
            }
            Keyboard.ClearFocus();
        }

        private void PageJumpBox_GotFocus(object sender, RoutedEventArgs e) => _pageJumpBox.SelectAll();

        private void ShortcutHelp_Click(object sender, RoutedEventArgs e)
        {
            ShortcutOverlay.Visibility = ShortcutOverlay.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShortcutOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            ShortcutOverlay.Visibility = Visibility.Collapsed;
        }

        private void ShortcutOverlayCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private void ShortcutOverlayClose_Click(object sender, RoutedEventArgs e)
        {
            ShortcutOverlay.Visibility = Visibility.Collapsed;
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }

    // ============================================================
    // Themed dialog — replaces MessageBox for dark-UI consistency
    // ============================================================
    internal static class TdpDialog
    {
        private static SolidColorBrush Brush(string key)
        {
            return Application.Current?.TryFindResource(key) as SolidColorBrush
                ?? SystemBrush(key);
        }

        private static SolidColorBrush SystemBrush(string key)
        {
            return key switch
            {
                "AccentGreen" => SystemColors.HighlightBrush,
                "AccentGreenDim" => SystemColors.HighlightBrush,
                "DangerRed" => SystemColors.HighlightBrush,
                "BgDark" => SystemColors.WindowBrush,
                "BgPanel" => SystemColors.WindowBrush,
                "BgHover" => SystemColors.ControlBrush,
                "BgPressed" => SystemColors.ControlDarkBrush,
                "BorderDim" => SystemColors.WindowTextBrush,
                "TextSecondary" => SystemColors.WindowTextBrush,
                _ => SystemColors.WindowTextBrush
            };
        }

        private static SolidColorBrush FrozenSolidColorBrush(System.Windows.Media.Color color)
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }

        public static MessageBoxResult Show(
            Window? owner,
            string message,
            string title = "TDPdf",
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage image = MessageBoxImage.None)
        {
            var result = MessageBoxResult.OK;
            var green = Brush("AccentGreen");
            var dark = Brush("BgDark");
            var panel = Brush("BgPanel");
            var text = Brush("TextPrimary");
            var border = Brush("BorderDim");
            var greenDim = Brush("AccentGreenDim");
            var greenHov = Brush("BgPressed");
            var hover = Brush("BgHover");
            var danger = Brush("DangerRed");
            var warning = Brush("WarningOrange");

            var win = new Window
            {
                Title = title,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize
            };

            var outerBorder = new Border
            {
                Background      = dark,
                BorderBrush     = greenDim,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6)
            };

            var root = new StackPanel();

            var titleBar = new Border
            {
                Background   = panel,
                Padding      = new Thickness(16, 10, 16, 10),
                CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            titleBar.Child = new TextBlock
            {
                Text       = title,
                Foreground = green,
                FontWeight = FontWeights.SemiBold,
                FontSize   = 13,
                FontFamily = new System.Windows.Media.FontFamily("Consolas")
            };
            root.Children.Add(titleBar);

            // Message body: icon column + wrapped message text.
            var msgGrid = new Grid { Margin = new Thickness(20, 16, 20, 8) };
            msgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            msgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (TryGetMessageBoxGlyph(image, green, warning, danger, out var glyphChar, out var glyphBrush))
            {
                var glyph = new TextBlock
                {
                    Text       = glyphChar,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize   = 28,
                    Foreground = glyphBrush,
                    VerticalAlignment   = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin              = new Thickness(0, 0, 14, 0)
                };
                Grid.SetColumn(glyph, 0);
                msgGrid.Children.Add(glyph);
            }

            var msgText = new TextBlock
            {
                Text         = message,
                Foreground   = text,
                FontSize     = 13,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(msgText, 1);
            msgGrid.Children.Add(msgText);
            root.Children.Add(msgGrid);

            var btnPanel = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            static ControlTemplate MakeBtnTemplate()
            {
                var bf = new FrameworkElementFactory(typeof(Border));
                bf.SetBinding(Border.BackgroundProperty,
                    new System.Windows.Data.Binding("Background")
                    { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
                bf.SetBinding(Border.BorderBrushProperty,
                    new System.Windows.Data.Binding("BorderBrush")
                    { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
                bf.SetBinding(Border.BorderThicknessProperty,
                    new System.Windows.Data.Binding("BorderThickness")
                    { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
                bf.SetBinding(Border.PaddingProperty,
                    new System.Windows.Data.Binding("Padding")
                    { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
                bf.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
                var cp = new FrameworkElementFactory(typeof(ContentPresenter));
                cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                bf.AppendChild(cp);
                return new ControlTemplate(typeof(Button)) { VisualTree = bf };
            }

            Button MakeBtn(string label, MessageBoxResult res, bool accent = false, bool isDefault = false, bool isCancel = false)
            {
                var bgNorm = accent ? greenDim : panel;
                var bgHov  = accent ? greenHov : hover;
                var btn = new Button
                {
                    Content         = label,
                    Padding         = new Thickness(18, 6, 18, 6),
                    Margin          = new Thickness(8, 0, 0, 0),
                    Background      = bgNorm,
                    Foreground      = accent ? green : text,
                    BorderBrush     = accent ? green : border,
                    BorderThickness = new Thickness(1),
                    Cursor          = Cursors.Hand,
                    FontSize        = 12,
                    Template        = MakeBtnTemplate(),
                    IsDefault       = isDefault,
                    IsCancel        = isCancel
                };
                btn.Click      += (_, _2) => { result = res; win.Close(); };
                btn.MouseEnter += (_, _2) => btn.Background = bgHov;
                btn.MouseLeave += (_, _2) => btn.Background = bgNorm;
                return btn;
            }

            Button? defaultBtn = null;
            switch (buttons)
            {
                case MessageBoxButton.OK:
                    defaultBtn = MakeBtn("OK", MessageBoxResult.OK, accent: true, isDefault: true, isCancel: true);
                    btnPanel.Children.Add(defaultBtn);
                    break;
                case MessageBoxButton.OKCancel:
                    defaultBtn = MakeBtn("OK", MessageBoxResult.OK, accent: true, isDefault: true);
                    btnPanel.Children.Add(defaultBtn);
                    btnPanel.Children.Add(MakeBtn("Cancel", MessageBoxResult.Cancel, isCancel: true));
                    break;
                case MessageBoxButton.YesNo:
                    defaultBtn = MakeBtn("Yes", MessageBoxResult.Yes, accent: true, isDefault: true);
                    btnPanel.Children.Add(defaultBtn);
                    btnPanel.Children.Add(MakeBtn("No", MessageBoxResult.No, isCancel: true));
                    break;
                case MessageBoxButton.YesNoCancel:
                    defaultBtn = MakeBtn("Yes", MessageBoxResult.Yes, accent: true, isDefault: true);
                    btnPanel.Children.Add(defaultBtn);
                    btnPanel.Children.Add(MakeBtn("No", MessageBoxResult.No));
                    btnPanel.Children.Add(MakeBtn("Cancel", MessageBoxResult.Cancel, isCancel: true));
                    break;
            }

            root.Children.Add(new Border
            {
                Padding = new Thickness(16, 8, 16, 16),
                Child   = btnPanel
            });

            outerBorder.Child = root;
            win.Content = outerBorder;
            if (defaultBtn != null)
            {
                var toFocus = defaultBtn;
                win.Loaded += (_, _2) => toFocus.Focus();
            }
            win.ShowDialog();
            return result;
        }

        private static bool TryGetMessageBoxGlyph(
            MessageBoxImage image,
            System.Windows.Media.Brush accent,
            System.Windows.Media.Brush warning,
            System.Windows.Media.Brush danger,
            out string glyph,
            out System.Windows.Media.Brush brush)
        {
            switch (image)
            {
                case MessageBoxImage.Information:
                    glyph = "\uE946"; brush = accent; return true;
                case MessageBoxImage.Warning:
                    glyph = "\uE7BA"; brush = warning; return true;
                case MessageBoxImage.Error:
                    glyph = "\uEA39"; brush = danger; return true;
                case MessageBoxImage.Question:
                    glyph = "\uE9CE"; brush = accent; return true;
                default:
                    glyph = string.Empty; brush = accent; return false;
            }
        }
    }
}
