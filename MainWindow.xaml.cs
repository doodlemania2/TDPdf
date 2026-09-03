using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
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
        public static readonly RoutedUICommand CloseOtherTabsCommand = new("Close Other Tabs", "CloseOtherTabs", typeof(MainWindow));
        public static readonly RoutedUICommand UndoCommand = new("Undo", "Undo", typeof(MainWindow));
        public static readonly RoutedUICommand RedoCommand = new("Redo", "Redo", typeof(MainWindow));
        public static readonly RoutedUICommand SaveAsCommand = new("Save As", "SaveAs", typeof(MainWindow));
        public static readonly RoutedUICommand AboutCommand = new("About TDPdf", "About", typeof(MainWindow));
        public static readonly RoutedUICommand InvertColorsCommand = new("Invert Colors", "InvertColors", typeof(MainWindow));
        // App-wide UI scale (AppScale.cs) — the chrome, not the document pane. Separate commands
        // from the Zoom* trio above so the two can never be wired to the same gesture by accident.
        public static readonly RoutedUICommand AppScaleUpCommand = new("App Size Larger", "AppScaleUp", typeof(MainWindow));
        public static readonly RoutedUICommand AppScaleDownCommand = new("App Size Smaller", "AppScaleDown", typeof(MainWindow));
        public static readonly RoutedUICommand AppScaleResetCommand = new("Reset App Size", "AppScaleReset", typeof(MainWindow));

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

        // Cross-window tab drag (see Services/WindowTransfer.cs). _windowTransferServer answers
        // another TDPdf window's drop of a tab onto ours; the rest track a drag started FROM one of
        // our own chips.
        private WindowTransferServer? _windowTransferServer;
        private DocumentContext? _tabDragCandidate;
        private Point _tabDragStartScreen;
        private bool _isDraggingTab;
        private TabDragGhost? _tabDragGhost;

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
        private Stack<int> _navBack => _ctx.NavBack;         // jump history back stack (per tab)
        private Stack<int> _navForward => _ctx.NavForward;   // jump history forward stack (per tab)
        private bool _isDrawing;
        private Point _drawStart;
        private UIElement? _activePreview;
        private InkAnnotation? _activeInk;
        private CropAnnotation? _activeCrop;
        private TextBox? _activeTextBox;
        private Border? _activeTextBoxGrip;
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
        // Strikethrough / Underline draw a thin opaque band rather than a translucent wash, so they
        // get their own default colour (upstream KillerPDF v1.6.5, #127).
        private Color _markupLineColor = Color.FromArgb(255, 220, 38, 38);
        private Border? _drawSettingsBar;

        // Text (typewriter) tool settings
        private double _textFontSize = 14;
        private string _textFontFamily = PdfFontStyle.DefaultFamily;
        private Color _textColor = Colors.Black;
        private bool _textWhiteout;
        // #135 (upstream KillerPDF v1.7.5): the tool's current character styling, inherited by the
        // next text box placed. Upstream's own note on shipping these is worth keeping: they "were
        // listed in the shortcuts for weeks without ever being wired up" — and ours were too.
        private bool _textBold;
        private bool _textItalic;
        private bool _textUnderline;
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

        // In-progress free-form polygon — per-document state, forwarded from the active tab's
        // DocumentContext exactly like _annotations / _undoStack. Non-empty = placement is live.
        private List<Point> _polyVertices => _ctx.PolyVertices;
        private int _polyPage { get => _ctx.PolyPage; set => _ctx.PolyPage = value; }
        private Polyline? _polyPreview { get => _ctx.PolyPreview; set => _ctx.PolyPreview = value; }
        private Polyline? _polyRubber { get => _ctx.PolyRubber; set => _ctx.PolyRubber = value; }
        private Ellipse? _polySnapDot { get => _ctx.PolySnapDot; set => _ctx.PolySnapDot = value; }

        /// <summary>A click within this many canvas px of the first vertex closes the polygon.</summary>
        private const double ShapePolySnapPx = 9;

        // Zoom fit-mode tracking (for auto-refit on window resize)
        private ZoomFitMode _zoomFitMode = ZoomFitMode.None;
        // #201: true while the zoom on screen is one the USER asked for by hand, rather than one
        // the app computed. Only the explicit manual paths raise it (BeginManualZoom); the two fits
        // lower it. SaveZoomSetting persists the level as Settings.LastManualZoom only while it is
        // up, which is what stops an app-applied fit from ever being replayed as a raw number on
        // the next open — and what stops a remembered Fit Width being quietly overwritten when a
        // fit early-returns and leaves _zoomFitMode reading None.
        private bool _manualZoomIntent;
        // #131: _applyingFitZoom is gone. It was vestigial until 1.23.7.0 read it to keep automatic
        // re-fits out of the text-editor commit chokepoint; the fleet then proved that guard both
        // unnecessary (a zoom never needed to settle the editor — see ApplyZoom) and ineffective
        // (a plain bool cannot survive the re-entrancy the zoom writes actually produce). Removing
        // the ONE read makes it an unread field again, so the field and its four writes go too
        // rather than coming back as a CS0414.
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
        private readonly DockPanel _sidebarContentPanel = null!;
        private readonly WrapPanel _pageContentPanel = null!;

        // Sidebar collapse/expand glide (upstream KillerPDF v1.6.5). SidebarStripWidth is the
        // permanent toggle strip the column shrinks to; it never reaches 0.
        // The two constants are LOGICAL px (what the sidebar content lays out in); SidebarCol
        // itself is outside the app-scale LayoutTransform, so its widths are SCREEN px. SbPx()
        // (AppScale.cs) converts, and the two fields below are already screen px — ApplyAppScale
        // rescales them whenever the app scale changes.
        private const double SidebarStripWidth = 24;
        private const double SidebarDefaultWidth = 180;   // matches SidebarCol's XAML width
        private double _sidebarExpandedWidth = SidebarDefaultWidth;   // width the next expand restores
        private double _sidebarAnimTarget = SidebarDefaultWidth;      // where the running glide lands
        private Action? _sidebarAnimDone;                             // finish work for the running glide

        // The document-present state the auto collapse/expand rule was last applied for; null until
        // the first sync. Used to fire the rule only on the empty <-> document transition.
        private bool? _sidebarSyncedHasDoc;

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
        private Canvas _textEditorCanvas = null!;
        private Grid _sidebarListHost = null!;   // #135: host for the page-drop insertion line
        private Border _pageDropLine = null!;
        /// <summary>False until Loaded has run; guards paths reachable from the single-instance pipe thread.</summary>
        private bool _uiReady;
        private Grid _pageContentGrid = null!;
        private Button _toolSelectBtn = null!;
        private Button _toolTextBtn = null!;
        private Button _toolEditTextBtn = null!;
        private Button _toolEditImageBtn = null!;
        private Button _toolHighlightBtn = null!;
        private Button _toolStrikeBtn = null!;
        private Button _toolUnderlineBtn = null!;
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
        private MenuItem _removePasswordMenuItem = null!;
        private Button _invertColorsBtn = null!;
        private MenuItem _invertColorsMenuItem = null!;
        private MenuItem _invertImagesMenuItem = null!;
        private MenuItem _twoPageBookMenuItem = null!;
        private Border _pageBadge = null!;
        private TextBlock _pageBadgeText = null!;
        private TranslateTransform _pageBadgeSlide = null!;

        // The primary page's TRUE-color bitmap, mirroring whatever RenderPage put in PageImage.
        // PageImage.Source may be the display-only inverted copy (#135), and the image-edit tool
        // encodes a region of the page into the annotation that gets baked into the saved PDF, so
        // that capture must read this, never the Image.
        private BitmapSource? _primaryPageBitmap;

        // ============================================================
        // View mode (app-wide). Single and Grid behave exactly as the original
        // single-page / grid layouts did; Continuous and TwoPage are additive.
        // ============================================================
        private enum ViewMode { Single, Continuous, TwoPage, Grid }
        private ViewMode _viewMode = ViewMode.Grid;

        // ── Two-Page "book layout" (upstream KillerPDF #193) ───────────────────────────────────
        // Off (default) is the pairing Two-Page has always used: the primary page plus the one
        // after it. On, the COVER displays ALONE, so paging forward runs 1 | 2-3 | 4-5 … the way a
        // physical book falls open. App-wide reading preference, persisted like ViewMode itself.
        private bool _twoPageBook;

        /// <summary>
        /// Index of the LEFT page of the spread containing <paramref name="page"/> under the active
        /// Two-Page pairing. Book layout leaves the cover on its own, so its spreads start on ODD
        /// indices; the classic pairing starts on even ones.
        /// </summary>
        private int SpreadStart(int page) =>
            page <= 0 ? 0
            : _twoPageBook ? page - ((page + 1) % 2)
            : page - page % 2;

        /// <summary>
        /// True when the tile at <paramref name="primaryPageIdx"/> is a book layout's lone cover —
        /// a ONE-page row with no facing page, so it must not be sized for two slots and must drop
        /// the spread gap, or it hangs left of an empty half and reads as left-aligned.
        /// </summary>
        private bool IsBookCoverRow(int primaryPageIdx) =>
            _viewMode == ViewMode.TwoPage && _twoPageBook && primaryPageIdx == 0;

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

        // #122 (upstream v1.6.3): continuous view used to keep a rendered bitmap for EVERY page for the
        // life of the document — a 243-page image PDF pinned gigabytes. Now only a window of pages
        // around the viewport holds real bitmaps: the initial pass fills the window around the opening
        // page, and MaintainContinuousWindow (on scroll-settle) renders pages coming into range and
        // releases those leaving it. Slot HEIGHTS never change on release/re-render, so releasing a
        // page never reflows the document (no scroll jump); the white scaffold shows until re-rendered.
        private const int ContinuousBaseWindow = 8;   // pages each side of the viewport kept as bitmaps
        private CancellationTokenSource? _continuousWindowCts;

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

        // Sidebar column's inner grid — one of the four hosts the app-wide UI scale's
        // LayoutTransform is applied to (AppScale.cs); the others are MainMenu, _toolbarBorder,
        // and _tabStripBorder.
        private Grid _sidebarOuterGrid = null!;

        // Footer chip that shows and drives the app scale (never itself scaled — the footer is
        // fixed so the chip holds still under the cursor while the wheel steps the size).
        private Button _appScaleButton = null!;

        // Outline / bookmarks sidebar tab (manual refs — XAML codegen doesn't resolve these)
        private TreeView _outlineTree = null!;
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
            // Always on, regardless of the SingleInstanceTabs setting: that one only governs
            // whether a second LAUNCH folds into this window, but once two windows exist (via a
            // tab tear-off, or SingleInstanceTabs being off) either one can be a drop target.
            _windowTransferServer = new WindowTransferServer(Dispatcher, ImportTabFromAnotherWindowAsync);
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (v != null) VersionLabel.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
            _annotationCanvas = (Canvas)FindName("AnnotationCanvas")!;
            _textEditorCanvas = (Canvas)FindName("TextEditorCanvas")!;
            _sidebarListHost = (Grid)FindName("SidebarListHost")!;
            _pageDropLine = (Border)FindName("PageDropLine")!;
            _pageContentGrid = (Grid)FindName("PageContentGrid")!;
            _toolSelectBtn = (Button)FindName("ToolSelectBtn")!;
            _toolTextBtn = (Button)FindName("ToolTextBtn")!;
            _toolEditTextBtn = (Button)FindName("ToolEditTextBtn")!;
            _toolEditImageBtn = (Button)FindName("ToolEditImageBtn")!;
            _toolHighlightBtn = (Button)FindName("ToolHighlightBtn")!;
            _toolStrikeBtn = (Button)FindName("ToolStrikeBtn")!;
            _toolUnderlineBtn = (Button)FindName("ToolUnderlineBtn")!;
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
            _sidebarContentPanel = (DockPanel)FindName("SidebarContentPanel")!;
            _pageContentPanel = (WrapPanel)FindName("PageContentPanel")!;
            _saveAsBtnRef = (Button)FindName("SaveAsBtn")!;
            _closeFileBtnRef = (Button)FindName("CloseFileBtn")!;
            _gridViewToggle = (System.Windows.Controls.Primitives.ToggleButton)FindName("GridViewToggle")!;
            _recentFilesBox = (Border)FindName("RecentFilesBox")!;
            _recentFilesList = (StackPanel)FindName("RecentFilesList")!;
            _removePasswordMenuItem = (MenuItem)FindName("RemovePasswordMenuItem")!;
            _invertColorsBtn = (Button)FindName("InvertColorsBtn")!;
            _invertColorsMenuItem = (MenuItem)FindName("InvertColorsMenuItem")!;
            _invertImagesMenuItem = (MenuItem)FindName("InvertImagesMenuItem")!;
            _twoPageBookMenuItem = (MenuItem)FindName("TwoPageBookMenuItem")!;
            _pageBadge = (Border)FindName("PageBadge")!;
            _pageBadgeText = (TextBlock)FindName("PageBadgeText")!;
            _pageBadgeSlide = (TranslateTransform)FindName("PageBadgeSlide")!;
            _continuousPanel = (StackPanel)FindName("ContinuousPanel")!;
            // Restore the persisted view mode (defaults to Grid, matching the original layout).
            if (Enum.TryParse<ViewMode>(TDPdf.Properties.Settings.Default.ViewMode, out var savedVm))
                _viewMode = savedVm;
            _gridViewToggle.IsChecked = _viewMode == ViewMode.Grid;
            // #193: restore the persisted Two-Page book layout and light its View-menu check.
            try { _twoPageBook = TDPdf.Properties.Settings.Default.TwoPageBookLayout; }
            catch { /* non-critical user preference */ }
            _twoPageBookMenuItem.IsChecked = _twoPageBook;
            PagePreviewPanel.ScrollChanged += PagePreviewPanel_ScrollChanged;
            PreviewMouseDown += NavHistory_PreviewMouseDown;   // mouse back/forward buttons = jump history
            _zoomBox = (ComboBox)FindName("ZoomBox")!;
            _portableBadge = (StackPanel)FindName("PortableBadge")!;
            _pageJumpBox = (TextBox)FindName("PageJumpBox")!;
            _pageTotalLabel = (TextBlock)FindName("PageTotalLabel")!;
            _customTitleBar = (Border)FindName("CustomTitleBar")!;
            _titleBarRow = (RowDefinition)FindName("TitleBarRow")!;
            _outlineTree = (TreeView)FindName("OutlineTree")!;
            // Expanded/Collapsed bubble from the items, which are rebuilt on every tab switch and
            // reload — so the recorder hangs off the tree, once, rather than off the items.
            _outlineTree.AddHandler(TreeViewItem.ExpandedEvent,
                new RoutedEventHandler(OutlineTree_ItemExpandChanged));
            _outlineTree.AddHandler(TreeViewItem.CollapsedEvent,
                new RoutedEventHandler(OutlineTree_ItemExpandChanged));
            _outlineScrollViewer = (ScrollViewer)FindName("OutlineScrollViewer")!;
            _sidebarPagesTab = (RadioButton)FindName("SidebarPagesTab")!;
            _sidebarOutlinesTab = (RadioButton)FindName("SidebarOutlinesTab")!;
            _pageControlsRow = (DockPanel)FindName("PageControlsRow")!;
            _tabStripBorder = (Border)FindName("TabStripBorder")!;
            _tabStrip = (StackPanel)FindName("TabStrip")!;
            _rootGrid = (Grid)FindName("RootGrid")!;
            _toolbarBorder = (Border)FindName("ToolbarBorder")!;
            _statusBarBorder = (Border)FindName("StatusBarBorder")!;
            _sidebarOuterGrid = (Grid)FindName("SidebarOuterGrid")!;
            _appScaleButton = (Button)FindName("AppScaleButton")!;
            RebuildTabStrip();
            ApplyCustomChromeVisibility();
            ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
            // #132: named, for the same reason as the DpiChanged handler below — the compiler would
            // otherwise stamp LastZoomOrigin with ".ctor" and leave it there until something else
            // sets a zoom, making every Zoom.Churn until then indistinguishable from that handler.
            Zoom.SetZoomLevel(TDPdf.Properties.Settings.Default.LastZoomLevel, origin: "StartupZoomRestore");
            Zoom.PropertyChanged += Zoom_PropertyChanged;
            CommandBindings.Add(new CommandBinding(ZoomInRoutedCommand, (_, _) => ChangeZoomByCommand(ZoomChange.In)));
            CommandBindings.Add(new CommandBinding(ZoomOutRoutedCommand, (_, _) => ChangeZoomByCommand(ZoomChange.Out)));
            CommandBindings.Add(new CommandBinding(ZoomResetRoutedCommand, (_, _) => ChangeZoomByCommand(ZoomChange.Reset)));
            CommandBindings.Add(new CommandBinding(InvertColorsCommand, (_, _) => ToggleDocInvert(!_docInvert)));
            CommandBindings.Add(new CommandBinding(AppScaleUpCommand, (_, _) => AppScaleUp()));
            CommandBindings.Add(new CommandBinding(AppScaleDownCommand, (_, _) => AppScaleDown()));
            CommandBindings.Add(new CommandBinding(AppScaleResetCommand, (_, _) => AppScaleReset()));
            CommandBindings.Add(new CommandBinding(CloseOtherTabsCommand, (_, _) => CloseOtherTabs(_ctx)));
            InitDocInvert();   // #135: restore the persisted display-only dark mode + light the rail moon
            InitAppScale();    // upstream v1.6.5: restore the persisted app-wide chrome scale
            ApplyLayoutShortcutLabels();   // #153: spell the zoom chords for THIS keyboard layout
            LoadSignatures();
            BuildContextMenu();
            SetTool(EditTool.Select);
            ApplyGrainTexture();
            SourceInitialized += MainWindow_SourceInitialized;
            // #132: THIS FIRES, and the note that used to sit here saying it does not was the
            // whole bug. It claimed WndProc's WM_DPICHANGED hook (handled = true) preempts WPF's
            // internal HwndTarget hook, so WPF never raises DpiChanged and this handler was
            // "harmless and idempotent" dead code. Production disagreed: once 1.25.0.0 named the
            // caller, Zoom.Churn came back Via=DpiChanged, ViaCount 12 of 13, on two unrelated
            // machines — a VM and a laptop — each time ~13 ApplyZoom calls a second held for two
            // seconds. WPF raises this for more than the one message we intercept.
            //
            // It was never harmless either. Every pass ran a full ApplyZoom: cancel and restart the
            // page render, re-fit, and write user.config to disk. Twelve times a second.
            //
            // So the handler stays — it IS a live DPI path on some machines, and deleting it would
            // trade a storm for a stale raster — but it now does something only when the DPI has
            // ACTUALLY changed. A repeat notification carrying the scale we already applied is
            // exactly the thing to drop, and dropping it is what makes the "idempotent" claim true
            // rather than merely hoped for. WmDpiChanged remains the authority on the value itself.
            DpiChanged += (_, e) =>
            {
                double scale = e.NewDpi.DpiScaleX;
                if (scale <= 0 || Math.Abs(scale - _currentDpiScale) < 0.001) return;
                _currentDpiScale = scale;
                InvalidateRenderCache();   // every raster budget is measured in device pixels
                ApplyZoom(via: nameof(DpiChanged));
            };

            // Open a file passed via command-line / file association (e.g. double-clicking a .pdf)
            // Also show the portable badge when running outside the install location.
            Loaded += async (_, _) =>
            {
                _uiReady = true;
                RefreshRecentFilesUi();

                // Nothing is open yet, so start on the collapsed rail (instant — no glide before the
                // first paint). A file from the command line or the restored session expands it below,
                // via FinishOpenFileAsync.
                SyncSidebarToDocState(hasDoc: _doc is not null, startup: true);

                var args = Environment.GetCommandLineArgs();
                // GetCommandLineArgs()[0] is always the exe path; --new-window (tab tear-off /
                // "Move to New Window") shifts the file argument over by one and, being a
                // single-purpose window spun up around one specific document, must not then pull in
                // whatever multi-tab session was last saved from some unrelated window.
                bool isTearOffLaunch = args.Length > 1 &&
                    string.Equals(args[1], "--new-window", StringComparison.OrdinalIgnoreCase);
                int fileArgIndex = isTearOffLaunch ? 2 : 1;
                if (args.Length > fileArgIndex && System.IO.File.Exists(args[fileArgIndex]))
                    await OpenInTabAsync(args[fileArgIndex]);   // an explicitly-requested file wins over the saved session
                else if (!isTearOffLaunch)
                    await RestoreSessionAsync();     // otherwise reopen last session's tabs when enabled

                if (App.IsPortable())
                    _portableBadge.Visibility = Visibility.Visible;

                FlushPendingExternalOpen();   // #202: a forward that landed before we were wired up

                // #132: last, and off the UI thread. Ask whether a newer TDPdf has been released
                // and, on a managed device, ask Intune to come and get it rather than waiting up to
                // eight hours for its own check-in — the gap that left one machine six releases
                // behind, still hitting a bug every one of those releases had fixed.
                var running = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                              ?? new Version(0, 0, 0, 0);
                TDPdf.Services.UpdateCheck.StartBackgroundCheck(running);

                // #134: an update CAN land under a running instance — Windows refuses to overwrite
                // an executing file but permits renaming it aside, which is exactly what the
                // installer does. It works, and the person carries on in the old build none the
                // wiser. Say so.
                if (App.InstalledExePath is { } installedExe
                    && TDPdf.Services.UpdateCheck.IsRestartPending(installedExe, running))
                {
                    SetStatusHeld("An update has been installed - restart TDPdf to use it.", 10000);
                    Telemetry.TrackEvent("Update.RestartPending");
                }
            };
        }

        /// <summary>
        /// The one typeface every text-annotation measurement goes through. #135: bold and italic
        /// change glyph advances, so if the on-screen TextBlock, <see cref="MeasureTextAnnotation"/>,
        /// <see cref="WrapTextToWidth"/> and the PDF burn-in do not all ask the same question, a
        /// styled box wraps in one place and not the other and the text moves when you save.
        /// </summary>
        private static Typeface TextTypeface(bool bold, bool italic) =>
            new(new FontFamily(PdfFontStyle.DefaultFamily),
                italic ? FontStyles.Italic : FontStyles.Normal,
                bold ? FontWeights.Bold : FontWeights.Normal,
                FontStretches.Normal);

        private static SolidColorBrush FrozenSolidColorBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }

        /// <summary>
        /// True when a DOCUMENT-level undo must stand aside for the text box the user is typing in.
        /// </summary>
        /// <remarks>
        /// Ported from upstream KillerPDF #237, and narrowed. This used to gate nine commands —
        /// Open, Save, Save As, New, Close, Print, Find, About and Undo — so none of them worked
        /// while an annotation text box had focus. Typing an annotation and reaching for Ctrl+S did
        /// nothing at all, silently.
        ///
        /// That was invisible until 1.24.1.0, because until then the editor was destroyed a few
        /// hundred milliseconds after it appeared (#131) and nobody was ever typing in one long
        /// enough to reach for a shortcut. Fixing the text box is what made this reachable, so it
        /// is fixed in the same breath.
        ///
        /// Undo is the one that genuinely must not pass. Ctrl+Z inside a TextBox means "undo my
        /// typing", which WPF already handles; letting the document handler win would silently
        /// revert an annotation, a crop or a page rotation while the user believed they were
        /// correcting a word. The rest are application commands with no text-editing meaning, and
        /// each already routes through the CommitActiveTextBox chokepoint, so the box in progress
        /// is settled rather than lost.
        /// </remarks>
        private bool ShouldDeferUndoToTextBox() => _activeTextBox is not null && _activeTextBox.IsFocused;

        private void OpenCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            Open_Click(sender, e);
        }

        private void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            SaveInPlace_Click(sender, e);
        }

        private void PrintCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            Print_Click(sender, e);
        }

        private void FindCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            ToggleSearchBar();
        }

        private void NewCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            New_Click(sender, e);
        }

        private void CloseFileCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            CloseFile();
        }

        private void UndoCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (ShouldDeferUndoToTextBox()) return;
            Undo_Click(sender, e);
        }

        private void SaveAsCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            SaveAs_Click(sender, e);
        }

        private void AboutCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
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

            // Custom chrome (AllowsTransparency=True, see ApplyInitialWindowChromeSettings) makes
            // this a layered window, and DWM cannot generate its own live taskbar thumbnail /
            // Aero Peek preview for a layered window — every TDPdf window shows the same blank/
            // generic image there instead of its own content, which is indistinguishable from "only
            // one window's preview is showing" when several are open. Opting into iconic
            // representation hands DWM a bitmap WE render on request instead (WM_DWMSENDICONICTHUMBNAIL
            // / WM_DWMSENDICONICLIVEPREVIEWBITMAP below), which is the sanctioned way apps with custom/
            // layered chrome restore this. Not needed under the native frame — that window isn't
            // layered, so DWM already has a real live thumbnail for it.
            if (!_useNativeWindowFrame && hwnd != IntPtr.Zero)
            {
                int trueVal = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_FORCE_ICONIC_REPRESENTATION, ref trueVal, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_HAS_ICONIC_BITMAP, ref trueVal, sizeof(int));
            }
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
        private const int DWMWA_FORCE_ICONIC_REPRESENTATION = 7;
        private const int DWMWA_HAS_ICONIC_BITMAP = 10;
        private const int WM_DWMSENDICONICTHUMBNAIL = 0x0323;
        private const int WM_DWMSENDICONICLIVEPREVIEWBITMAP = 0x0326;

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
            else if (msg == WM_KEYDOWN && (int)wParam == VK_ESCAPE && _cancellableOpCts is { IsCancellationRequested: false })
            {
                // SetFileOperationBusy disables the WPF content during these operations, so Esc never
                // reaches OnPreviewKeyDown. The native HWND stays Win32-enabled, though, so we catch it
                // here and cancel the in-flight OCR / language download / image export cooperatively.
                _cancellableOpCts.Cancel();
                SetStatus("Cancelling...");
                handled = true;
            }
            else if (msg == WM_DWMSENDICONICTHUMBNAIL && !_useNativeWindowFrame)
            {
                // lParam: HIWORD = requested max width, LOWORD = requested max height.
                long l = lParam.ToInt64();
                int maxW = (int)((l >> 16) & 0xFFFF);
                int maxH = (int)(l & 0xFFFF);
                SendIconicThumbnail(hwnd, maxW, maxH);
                handled = true;
            }
            else if (msg == WM_DWMSENDICONICLIVEPREVIEWBITMAP && !_useNativeWindowFrame)
            {
                SendIconicLivePreview(hwnd);
                handled = true;
            }
            return IntPtr.Zero;
        }

        // Renders the window's current content to an HBITMAP DWM can copy for a taskbar hover
        // thumbnail or Aero Peek preview. Best-effort and purely visual: any failure here means the
        // fallback is what every window already showed before this existed (a blank/generic image),
        // never a functional problem, so failures are swallowed rather than surfaced anywhere.
        private void SendIconicThumbnail(IntPtr hwnd, int maxWidth, int maxHeight)
        {
            try
            {
                if (maxWidth <= 0 || maxHeight <= 0) return;
                double w = ActualWidth, h = ActualHeight;
                if (w <= 0 || h <= 0) return;
                double scale = Math.Min(maxWidth / w, maxHeight / h);
                IntPtr hBitmap = RenderToHBitmap(scale);
                if (hBitmap == IntPtr.Zero) return;
                try { DwmSetIconicThumbnail(hwnd, hBitmap, 0); }
                finally { DeleteObject(hBitmap); }
            }
            catch { /* best-effort visual only */ }
        }

        private void SendIconicLivePreview(IntPtr hwnd)
        {
            try
            {
                IntPtr hBitmap = RenderToHBitmap(1.0);
                if (hBitmap == IntPtr.Zero) return;
                try
                {
                    var origin = new POINT { x = 0, y = 0 };
                    DwmSetIconicLivePreviewBitmap(hwnd, hBitmap, ref origin, 0);
                }
                finally { DeleteObject(hBitmap); }
            }
            catch { /* best-effort visual only */ }
        }

        // Rounds through a BMP encode/decode rather than a raw CreateDIBSection: the window's
        // content is always fully opaque (it paints its own themed background), so nothing here
        // needs an alpha channel, and going through System.Drawing.Bitmap.GetHbitmap avoids hand
        // building a DIB section and BITMAPINFO by hand for a purely cosmetic feature.
        private IntPtr RenderToHBitmap(double scale)
        {
            int pw = Math.Max(1, (int)(ActualWidth * scale));
            int ph = Math.Max(1, (int)(ActualHeight * scale));
            var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(this);
            var frame = BitmapFrame.Create(rtb);
            var encoder = new BmpBitmapEncoder();
            encoder.Frames.Add(frame);
            using var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Position = 0;
            using var gdiBmp = new System.Drawing.Bitmap(ms);
            return gdiBmp.GetHbitmap();
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

            // #189 (upstream KillerPDF commit "Re-render on DPI change"): every raster budget is
            // measured in DEVICE pixels, so moving the window to a monitor at a different scale
            // factor changes how many pixels a page needs WITHOUT changing its size in DIPs. In a
            // fit mode the move also changes the window's DIP size, so the resize path re-fits and
            // re-renders; at a manual zoom nothing else fires and the page would sit upscaled from
            // the old monitor's render. That was survivable while the re-sharpen budget carried a 2×
            // supersample, which happened to cover a 100% → 200% jump; at the device-native budget
            // this cluster introduces there is no headroom left.
            //
            // Upstream overrides OnDpiChanged for this; here that would be dead code, because the
            // WndProc above claims WM_DPICHANGED (handled = true) and so WPF's DpiChanged never
            // fires (see CurrentRenderDpiScale). This IS our DPI-change entry point, so it owns the
            // re-render. RerenderCurrentPage repaints the primary tile at the new GetCurrentDpiX and
            // refills _renderDims (which the crop / annotation / form paths key off, and which
            // InvalidateRenderCache just dropped); it then ends in ApplyZoom, the single fan-out to
            // the per-mode render paths — StartContinuousResharpen for Continuous, and the deferred
            // RefreshPageView → RenderAdditionalPages that re-rasterizes the Grid / Two-Page tiles.
            //
            // Deferred to DispatcherPriority.Loaded so the SetWindowPos above has been laid out
            // first: the tile pass measures PagePreviewPanel.ActualWidth and _annotationCanvas.Width,
            // which are still the pre-move values while we are on the WndProc stack.
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                (Action)(() => { if (_doc is not null) RerenderCurrentPage(); }));
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

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetIconicThumbnail(IntPtr hwnd, IntPtr hbitmap, uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetIconicLivePreviewBitmap(IntPtr hwnd, IntPtr hbitmap, ref POINT ptClient, uint flags);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

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
            // Full screen snapshots and then overwrites the sidebar column outright. A glide still in
            // flight holds an animation on ColumnDefinition.Width, which outranks the local values set
            // below — land it first so the snapshot is of a settled sidebar and the writes stick.
            FinishSidebarAnimation();

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
            // The user's real on-disk document: what session restore reopens, what goes in the
            // recent list, and — crucially — what a plain (in-place) Save writes to.
            // This is NOT CurrentFile: the WORKING path gets repointed at a copy under %TEMP% by the
            // decrypt-on-open of a password-protected file, by SaveTempAndReload after any structural
            // edit (rotate / delete / reorder / crop), and by the #106 PDFium repair. Saving to
            // CurrentFile in those states writes into %TEMP% — which is deleted on exit — and the
            // user's document is never updated. Null when there is no such home yet (New /
            // merged-on-drop / imported images / raster-recovered / a file inside %TEMP%), in which
            // case Ctrl+S routes to Save As. Retargeted by a successful Save As.
            public string? OriginalPath;
            public string DisplayName = "";      // shown in the tab header and title bar
            public bool IsDirty;

            // True when the source file needed a password, or carried owner restrictions, to open.
            // TDPdf rewrites the whole file through PdfSharpCore on every save and PdfSharpCore
            // emits no /Encrypt unless a password is set on the document, so saving a document that
            // was protected necessarily produces an UNPROTECTED file. The save says so instead of
            // dropping the protection silently, and this gates File ▸ Remove Password. Cleared once
            // a save has written this tab's file unprotected.
            public bool WasProtected;

            // True for docs with no real on-disk home yet (merged-on-drop, imported images).
            // The working path is a temp file, so Ctrl+S must route to Save As instead of
            // silently overwriting the temp copy.
            public bool IsUntitled;

            // Which OUTLINES nodes this document has expanded, keyed by index path ("2/0/1", the ghost
            // add-row excluded). Per-tab and not per-app: it dies with the tab, so it can neither leak
            // between documents nor grow past this document's own bookmark count. Paths rather than
            // PdfOutline references because SaveTempAndReload reopens the document and every outline
            // object is a new instance afterwards. Seen=false means "never built a tree for this
            // document yet", which is what makes the depth default apply exactly once.
            public readonly HashSet<string> OutlineExpanded = new(StringComparer.Ordinal);
            public bool OutlineExpandSeen;

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
            public readonly Stack<int> NavBack = new();      // jump history (#122-adjacent, upstream v1.6.4)
            public readonly Stack<int> NavForward = new();
            public readonly PdfContentEditor ContentEditor = new();

            public readonly Dictionary<int, List<(double left, double bottom, double right, double top)>> AllSearchRects = new();
            public readonly List<int> SearchResultPages = new();
            public int SearchPageCursor = -1;

            // View state restored when this tab is re-activated.
            public IReadOnlyList<BitmapSource?>? Thumbnails;
            public int SelectedPageIndex = -1;

            // The clickable tab-header chip (built lazily by RebuildTabStrip).
            public Border? Chip;

            // ── Flowing text selection (upstream KillerPDF v1.6.5, #127) ──
            // Per-document, like Chip above: the character cache is keyed on THIS document's
            // working path, and a caret is an index into one of ITS pages, so neither may ever leak
            // across a tab switch. See TextSelection.cs.
            public readonly TDPdf.Services.TextRunService TextRuns = new();
            public bool TxtSelActive;                      // a flowing drag is in progress
            public bool TxtSelHasRange;                    // a committed selection is on screen
            public (int Page, int Caret) TxtSelAnchor;
            public (int Page, int Caret) TxtSelFocus;
            public Point TxtSelDownPos;                    // press point, canvas coords
            public bool TxtSelDragStarted;                 // movement has passed the click threshold
            public PageAnnotation? TxtSelClickAnnot;       // annotation under the press; selected on a plain click
            public Rect TxtSelClickAnnotBounds;
            public EditTool? TxtSelCommitTool;             // set while a markup tool owns the drag

            // ── In-progress free-form polygon (Shapes tool, upstream KillerPDF v1.6.5) ──
            // Per-document too: the vertices are canvas coordinates on THIS document's page. A
            // non-empty PolyVertices means a polygon is being placed; ResolveShapePolygon settles
            // it (committing or discarding) and tears the preview visuals back down, and that
            // always runs before _ctx is swapped, so only one context can hold a live polygon.
            public readonly List<Point> PolyVertices = new();
            public int PolyPage = -1;
            public Polyline? PolyPreview;    // the committed vertices
            public Polyline? PolyRubber;     // last vertex → cursor, dashed
            public Ellipse? PolySnapDot;     // ring over the first vertex, lit when a click would close
        }

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
            CommitActiveTextBox();
            CaptureViewState();
            int dirtyCount = _tabs.Count(t => t.Doc is not null && t.IsDirty);
            if (dirtyCount > 0)
            {
                var msg = dirtyCount == 1
                    ? "You have unsaved changes. Close TDPdf without saving?"
                    : $"You have unsaved changes in {dirtyCount} open files. Close TDPdf without saving?";
                var res = TdpDialog.ShowYesNo(this, msg,
                    "Close Without Saving", "Cancel",
                    "TDPdf", MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            CancelDocumentWork(cancelWindowOperation: true);
            // Closing is now committed (any dirty prompt was accepted). Decide whether to remember the
            // open documents for next launch, then persist or clear the saved session accordingly.
            HandleSessionOnClose();
            ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
            _hwndSource?.RemoveHook(WndProc);
            _windowTransferServer?.Dispose();
            base.OnClosing(e);
        }

        // The real, on-disk documents currently open, in tab order (temp / untitled / merged / imported /
        // recovered docs have a null OriginalPath and are excluded — they have no lasting home to reopen).
        // IsRecentEligiblePath also drops documents opened from under %TEMP% (e.g. a mail attachment):
        // those ARE a valid in-place save target, but they are not a location worth reopening next launch.
        private List<string> OpenSessionFiles() => _tabs
            .Where(t => t.Doc is not null && IsRecentEligiblePath(t.OriginalPath))
            .Select(t => t.OriginalPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // On close, remember (or forget) the open documents so the next launch can restore them.
        // Preference "Yes" always remembers, "No" never does, "Ask" prompts once (with an optional
        // "remember my choice" that locks the answer). Forgetting clears the saved paths for privacy.
        private void HandleSessionOnClose()
        {
            var openFiles = OpenSessionFiles();
            if (openFiles.Count == 0) { ClearSavedSession(); return; }

            bool reopen;
            switch (TDPdf.Properties.Settings.Default.ReopenSession)
            {
                case "Yes": reopen = true;  break;
                case "No":  reopen = false; break;
                default:
                    string msg = openFiles.Count == 1
                        ? "Reopen this document next time you open TDPdf?"
                        : $"Reopen these {openFiles.Count} documents next time you open TDPdf?";
                    var (res, remember) = TdpDialog.ShowWithCheckbox(
                        this, msg, "Remember my choice", "TDPdf",
                        MessageBoxButton.YesNo, MessageBoxImage.Question);
                    reopen = res == MessageBoxResult.Yes;
                    if (remember)
                    {
                        TDPdf.Properties.Settings.Default.ReopenSession = reopen ? "Yes" : "No";
                        TDPdf.Properties.Settings.Default.Save();
                    }
                    break;
            }

            if (reopen) SaveSession(openFiles);
            else        ClearSavedSession();
        }

        private void SaveSession(List<string> openFiles)
        {
            var s = TDPdf.Properties.Settings.Default;
            s.SessionFiles = string.Join("|", openFiles);
            string? active = _ctx.OriginalPath;
            s.SessionActiveFile = !string.IsNullOrEmpty(active)
                && openFiles.Contains(active, StringComparer.OrdinalIgnoreCase) ? active : "";
            s.Save();
        }

        private static void ClearSavedSession()
        {
            var s = TDPdf.Properties.Settings.Default;
            if (s.SessionFiles.Length == 0 && s.SessionActiveFile.Length == 0) return;   // nothing to clear
            s.SessionFiles = "";
            s.SessionActiveFile = "";
            s.Save();
        }

        /// <summary>
        /// Reopens the documents saved from the previous session as tabs, then re-selects the tab that was
        /// active last time. Skipped when the user opted out ("No"), when nothing was saved, or (by the
        /// caller) when a file was passed on the command line — that file takes precedence over the session.
        /// </summary>
        private async Task RestoreSessionAsync()
        {
            if (TDPdf.Properties.Settings.Default.ReopenSession == "No") return;

            var files = TDPdf.Properties.Settings.Default.SessionFiles
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(System.IO.File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0) return;

            foreach (var f in files)
                await OpenInTabAsync(f);

            string active = TDPdf.Properties.Settings.Default.SessionActiveFile;
            if (!string.IsNullOrEmpty(active))
            {
                var target = _tabs.FirstOrDefault(t =>
                    string.Equals(t.OriginalPath, active, StringComparison.OrdinalIgnoreCase));
                if (target is not null)
                {
                    ActivateContext(target);
                    RebuildTabStrip();
                }
            }
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

            panel.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 12) });

            panel.Children.Add(new TextBlock
            {
                Text = "Privacy",
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushResource("TextPrimary"),
                Margin = new Thickness(0, 0, 0, 8)
            });

            // #146: the write-side companion to the two "clear the list" affordances (the start
            // screen's Clear recent files button and the Open button's right-click Clear List).
            var forgetRecent = new CheckBox
            {
                Content = "Don't remember recently opened files",
                IsChecked = TDPdf.Properties.Settings.Default.DontRememberRecentFiles,
                Foreground = BrushResource("TextPrimary"),
                ToolTip = "Stops TDPdf recording the documents you open. Turning this on also clears the list already stored.",
                Margin = new Thickness(0, 0, 0, 12)
            };
            forgetRecent.Checked += ForgetRecentFilesSettingChanged;
            forgetRecent.Unchecked += ForgetRecentFilesSettingChanged;
            panel.Children.Add(forgetRecent);

            // The read-side companion to the link confirmation's "Don't ask again" checkbox. Without
            // this the opt-out is a one-way trapdoor: ConfirmOpenLink is the only writer of
            // SkipLinkConfirm and it only ever sets it, so nothing in the UI could turn the
            // confirmation back on. Checkbox reads the inverse of the stored setting.
            var confirmLinks = new CheckBox
            {
                Content = "Confirm before opening links",
                IsChecked = !TDPdf.Properties.Settings.Default.SkipLinkConfirm,
                Foreground = BrushResource("TextPrimary"),
                ToolTip = "Ask for confirmation before a link in a PDF opens in your browser. Ticking \"Don't ask again\" in that prompt clears this.",
                Margin = new Thickness(0, 0, 0, 12)
            };
            confirmLinks.Checked += ConfirmOpenLinksSettingChanged;
            confirmLinks.Unchecked += ConfirmOpenLinksSettingChanged;
            panel.Children.Add(confirmLinks);

            // Consent for usage and crash reporting. Sits with the other privacy switches rather
            // than in About, matching where every user preference already lives.
            var telemetry = new CheckBox
            {
                Content = "Send anonymous usage and crash reports",
                IsChecked = TDPdf.Properties.Settings.Default.TelemetryEnabled,
                Foreground = BrushResource("TextPrimary"),
                ToolTip = "Event names, tool names, app and Windows version, and sanitised crash reports. "
                        + "Never document contents, file names or file paths. See PRIVACY.md.",
                Margin = new Thickness(0, 0, 0, 4)
            };
            telemetry.Checked += TelemetrySettingChanged;
            telemetry.Unchecked += TelemetrySettingChanged;
            panel.Children.Add(telemetry);

            // Tell the truth about what the checkbox is actually doing. With no destination
            // configured the setting is inert whichever way it is set, and saying so is better
            // than letting someone believe they have turned something off that was never on.
            panel.Children.Add(new TextBlock
            {
                Text = TelemetryConfig.HasDestination()
                    ? "Reporting to your organisation's collector."
                    : "No reporting destination is configured on this device, so nothing is sent.",
                Foreground = BrushResource("TextSecondary"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(22, 0, 0, 12)
            });

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

        /// <summary>
        /// Per-user consent for usage and crash reporting. Takes effect on the next launch:
        /// Telemetry.Initialize runs once at startup and builds the client there, so flipping this
        /// mid-session cannot retroactively un-send what has already gone. Turning it OFF stops the
        /// live client immediately as well, so the rest of this session is silent either way.
        /// Preference only — never touches _isDirty.
        /// </summary>
        private void TelemetrySettingChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb) return;

            bool requested = cb.IsChecked == true;
            if (TDPdf.Properties.Settings.Default.TelemetryEnabled == requested) return;

            TDPdf.Properties.Settings.Default.TelemetryEnabled = requested;
            TDPdf.Properties.Settings.Default.Save();

            if (!requested)
            {
                // Honour the withdrawal now rather than at next launch. Flush first so events
                // already queued from this session are not silently discarded mid-flight.
                Telemetry.Flush();
                Telemetry.Shutdown();
                SetStatus("Usage and crash reporting turned off.");
            }
            else
            {
                SetStatus(TelemetryConfig.HasDestination()
                    ? "Usage and crash reporting will resume when TDPdf restarts."
                    : "Usage reporting is on, but no destination is configured on this device.");
            }
        }

        // #146 (upstream KillerPDF v1.6.5): "Don't remember recently opened files". Upstream parks
        // this in its About window beside a "Clear all Data" button; TDPdf's About is a plain
        // TdpDialog text box with no controls, so the toggle lives here in the Settings dialog
        // where every other user preference already lives — no new window for one checkbox.
        // Switching it ON also empties the stored list: the point of the setting is that nothing
        // about the user's documents lingers on a shared machine, which a write-side gate alone
        // would not deliver. Preference only — never touches _isDirty.
        private void ForgetRecentFilesSettingChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb)
                return;

            bool requested = cb.IsChecked == true;
            if (TDPdf.Properties.Settings.Default.DontRememberRecentFiles == requested)
                return;

            TDPdf.Properties.Settings.Default.DontRememberRecentFiles = requested;
            TDPdf.Properties.Settings.Default.Save();
            if (requested)
                ClearRecentFiles();   // forget what is already there, not just what comes next
            SetStatus(requested
                ? "Recently opened files are no longer remembered"
                : "Recently opened files will be remembered");
        }

        // Makes the link-confirmation opt-out two-way. The stored setting is the negative
        // (SkipLinkConfirm) because ConfirmOpenLink's "Don't ask again" wrote it that way; the name
        // is kept so an existing opt-out still reads correctly — a rename would need a migration
        // read of the old key for no user-visible gain. Preference only — never touches _isDirty.
        private void ConfirmOpenLinksSettingChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb)
                return;

            bool confirm = cb.IsChecked == true;
            if (TDPdf.Properties.Settings.Default.SkipLinkConfirm == !confirm)
                return;

            TDPdf.Properties.Settings.Default.SkipLinkConfirm = !confirm;
            TDPdf.Properties.Settings.Default.Save();
            SetStatus(confirm
                ? "Links will ask before opening"
                : "Links will open without asking");
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

            menu.Items.Add(MakeMenuItem("_Copy Text", (s, e) => CopySelectedText(), "Ctrl+C", "Copy selected text to the clipboard", "\uE8C8"));
            menu.Items.Add(MakeMenuItem(
                PageList.SelectedItems.Count > 1 ? "OCR Selected Pages to Clip_board" : "OCR Page to Clip_board",
                (s, e) => OcrPagesToClipboard(SelectedPageIndicesForOcr()),
                "Ctrl+Shift+O", "Recognize the current page's text with OCR and copy it to the clipboard", "\uEE6F"));
            menu.Items.Add(MakeMenuItem("_Print", (s, e) => Print_Click(s!, e), "Ctrl+P", "Print the current PDF", "\uE749"));
            menu.Items.Add(new Separator());
            // Tool rows carry the toolbar's glyph and the tool's single-key shortcut (see OnPreviewKeyDown).
            menu.Items.Add(MakeMenuItem("_Select Tool", (s, e) => SetTool(EditTool.Select), "V", "Switch to the select tool", "\uE8B3"));
            menu.Items.Add(MakeMenuItem("_Text Tool", (s, e) => SetTool(EditTool.Text), "1", "Switch to the text tool", "\uE8D2"));
            menu.Items.Add(MakeMenuItem("Edit Existing Text", (s, e) => SetTool(EditTool.EditText), "2", "Switch to the existing text edit tool", "\uE104"));
            menu.Items.Add(MakeMenuItem("Edit Existing Image", (s, e) => SetTool(EditTool.EditImage), "3", "Switch to the existing image edit tool", "\uEB9F"));
            menu.Items.Add(MakeMenuItem("_Highlight Tool", (s, e) => SetTool(EditTool.Highlight), "5", "Switch to the highlight tool", "\uED56"));
            menu.Items.Add(MakeMenuItem("_Draw Tool", (s, e) => SetTool(EditTool.Draw), "9", "Switch to the draw tool", "\uED63"));
            menu.Items.Add(MakeMenuItem("_Crop Tool", (s, e) => SetTool(EditTool.Crop), "C", "Switch to the crop tool", "\uE7A8"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("De_lete Selected", (s, e) => DeleteSelected(), "Delete", "Delete the selected annotation", "\uE74D"));
            menu.Items.Add(MakeMenuItem("_Undo Last", (s, e) => Undo_Click(s!, e), "Ctrl+Z", "Undo the last annotation change", "\uE7A7"));
            menu.Items.Add(MakeMenuItem("Cle_ar Page Annotations", (s, e) => ClearAnnotations_Click(s!, e), null, "Clear all annotations on this page", "\uED62"));

            _annotationCanvas.ContextMenu = menu;
        }

        private void PageList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_doc is null) return;
            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem("Insert Blank Page After", (s, ev) => InsertBlankPage_Click(s!, ev), null, null, "\uE7C3"));
            menu.Items.Add(new Separator());
            // One Rotate glyph serves both directions: the counter-clockwise row draws it mirrored,
            // so the pair reads as a matched set instead of two unrelated icons.
            menu.Items.Add(MakeMenuItem("Rotate CW",  (s, ev) => RotatePages_Click(90), null, null, RotateGlyph));
            menu.Items.Add(MakeMenuItem("Rotate CCW", (s, ev) => RotatePages_Click(-90), null, null, RotateGlyph, mirrorGlyph: true));
            menu.Items.Add(MakeMenuItem("Transform…", (s, ev) => ToolTransform_Click(s!, ev), null,
                "Rotate by a fine angle, scale, flip, or straighten the page (rasterizes it to an image)", "\uE90F"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("Move Page Up",   (s, ev) => MoveUp_Click(s!, ev), null, null, "\uE74A"));
            menu.Items.Add(MakeMenuItem("Move Page Down", (s, ev) => MoveDown_Click(s!, ev), null, null, "\uE74B"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("Extract Page(s)", (s, ev) => Split_Click(s!, ev), null, null, "\uE8B1"));
            menu.Items.Add(MakeMenuItem("Delete Page(s)", (s, ev) => Delete_Click(s!, ev), null, null, "\uE8C6"));
            menu.PlacementTarget = PageList;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void RotatePages_Click(int delta)
        {
            if (_doc is null) return;
            CommitActiveTextBox();
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) return;
            try
            {
                var indices = new List<int>();
                foreach (var item in selected) indices.Add(PageList.Items.IndexOf(item));
                // #169: a rotation must not destroy the overlay annotations. SaveTempAndReload's
                // default clears them all, so a second rotation after placing an annotation would
                // lose committed, unsaved work. Remap each rotated page's annotations through the
                // turn first — the render dims here are still the pre-turn frame the user drew on;
                // the reload will re-render at the swapped frame — then keep them across the reload.
                foreach (var idx in indices)
                {
                    // Always derive the canonical DIP canvas frame from page geometry. _renderDims can
                    // also be filled by secondary grid rendering, whose w/h are device pixels on a
                    // HiDPI monitor rather than the primary annotation canvas's DPI-normalized DIPs.
                    var dims = AnnotationCanvasSize(_doc.Pages[idx]);
                    if (_annotations.TryGetValue(idx, out var anns))
                        TDPdf.Services.AnnotationRotate.Remap(anns, delta, dims.w, dims.h);
                    RemapAnnotationSnapshots(_undoStack, idx, delta, dims.w, dims.h);
                    RemapAnnotationSnapshots(_redoStack, idx, delta, dims.w, dims.h);
                }
                foreach (var idx in indices)
                    _doc.Pages[idx].Rotate = ((_doc.Pages[idx].Rotate + delta) % 360 + 360) % 360;
                int restoreIdx = PageList.SelectedIndex;
                SaveTempAndReload(keepAnnotations: true);
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

        // Rotate was previously reachable only via right-click on a page thumbnail in the Pages
        // panel — reported back as "the rotate command is missing" when someone went looking for it
        // on the toolbar instead. Left-click here does the common case (clockwise); right-click
        // opens the same CW / CCW / Transform menu the page-list context menu already offers, so
        // the two entry points stay in sync rather than drifting into two different feature sets.
        private void RotateBtn_Click(object sender, RoutedEventArgs e) => RotatePages_Click(90);

        private void RotateBtn_RightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem("Rotate CW",  (s, ev) => RotatePages_Click(90), null, null, RotateGlyph));
            menu.Items.Add(MakeMenuItem("Rotate CCW", (s, ev) => RotatePages_Click(-90), null, null, RotateGlyph, mirrorGlyph: true));
            menu.Items.Add(MakeMenuItem("Transform…", (s, ev) => ToolTransform_Click(s!, ev), null,
                "Rotate by a fine angle, scale, flip, or straighten the page (rasterizes it to an image)", ""));
            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private static (int w, int h) AnnotationCanvasSize(PdfPage page)
        {
            var (wpt, hpt) = VisiblePageSize(page);
            double longest = Math.Max(wpt, hpt);
            if (longest <= 0) return (1, 1);
            double scale = TDPdf.Services.PdfDocumentService.RenderBoxDip / longest;
            return (
                Math.Max(1, (int)Math.Round(wpt * scale)),
                Math.Max(1, (int)Math.Round(hpt * scale)));
        }

        private static void RemapAnnotationSnapshots(
            LinkedList<UndoEntry> history,
            int pageIndex,
            int delta,
            double oldW,
            double oldH)
        {
            for (var node = history.First; node is not null; node = node.Next)
            {
                if (node.Value.Kind == UndoKind.PageSnapshot
                    && node.Value.PageIdx == pageIndex
                    && node.Value.PageAnnotations is { } annotations)
                {
                    TDPdf.Services.AnnotationRotate.Remap(annotations, delta, oldW, oldH);
                }
            }
        }

        /// <summary>
        /// Builds a themed context-menu row. <paramref name="glyph"/> is an optional Segoe MDL2
        /// codepoint painted in the menu's 20px left gutter — the same column the check mark uses
        /// (see the MenuItem ControlTemplate in MainWindow.xaml). Glyphs mirror the toolbar button
        /// for the same action so the two surfaces read alike. <paramref name="mirrorGlyph"/> flips
        /// it horizontally, which is how the counter-clockwise rotate row gets a true mirrored
        /// partner for the clockwise one out of a single codepoint.
        /// </summary>
        private static MenuItem MakeMenuItem(string header, RoutedEventHandler click, string? gesture = null,
                                             string? helpText = null, string? glyph = null, bool mirrorGlyph = false)
        {
            var item = new MenuItem { Header = header };
            item.Click += click;
            if (gesture != null)
                item.InputGestureText = gesture;
            if (glyph != null)
                item.Icon = MakeMenuGlyph(glyph, mirrorGlyph);
            var automationName = header.Replace("_", string.Empty);
            AutomationProperties.SetName(item, automationName);
            AutomationProperties.SetHelpText(item, helpText ?? automationName);
            return item;
        }

        /// <summary>Segoe MDL2 "Rotate". Used as-is for clockwise and mirrored for counter-clockwise
        /// so the two rotate rows are a matched pair rather than two unrelated icons.</summary>
        private const string RotateGlyph = "\uE7AD";

        /// <summary>Code-side twin of the MenuGlyph style in MainWindow.xaml: an MDL2 TextBlock for
        /// a MenuItem.Icon, themed by resource reference so Dark / Light / HighContrast repaint it.</summary>
        private static TextBlock MakeMenuGlyph(string glyph, bool mirror = false)
        {
            var tb = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            if (mirror) tb.RenderTransform = new ScaleTransform(-1, 1);
            return tb;
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

        /// <summary>
        /// Installs an opened <see cref="PdfOpenResult"/> into the active tab.
        /// </summary>
        /// <param name="internalReload">
        /// True when this is TDPdf re-loading the SAME document from a working file it just wrote
        /// (the crop pipeline, and its failure-restore), rather than the user opening a file. Such a
        /// reload must not touch the tab's identity — OriginalPath, IsUntitled, WasProtected and the
        /// recent list — because DisplayPath is then a temp path: claiming it as the document would
        /// re-introduce the "Ctrl+S writes into %TEMP%" bug this whole change exists to fix.
        /// </param>
        private async Task FinishOpenFileAsync(PdfOpenResult result, CancellationToken cancellationToken,
            bool internalReload = false)
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
                // Same reason as the identity block below: a crop reload's DisplayPath is the
                // "<name>.crop-<guid>.pdf" working file, which must not become the tab's name.
                if (!internalReload) SetDisplayName(System.IO.Path.GetFileName(result.DisplayPath));
                // A genuinely different document in this tab starts from the depth default again — the
                // sticky OUTLINES expansion belongs to the file that was open, not to the tab. An
                // internal reload (crop's "<name>.crop-<guid>.pdf") is the SAME document, so it keeps
                // its state, as do SaveTempAndReload and the reopen-after-save, which never come here.
                if (!internalReload) { _ctx.OutlineExpanded.Clear(); _ctx.OutlineExpandSeen = false; }
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
                SyncSidebarToDocState(hasDoc: true, startup: false);   // a document is up: open the rail
                MarkDirty(false);
                if (!internalReload)
                {
                    _ctx.IsUntitled = false;   // a real on-disk open; merged/imported callers set this true afterward
                    _ctx.WasProtected = result.WasProtected;
                }
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
                // An owner-restricted file PdfSharpCore could not parse comes back as a PDFium-repaired,
                // decrypted copy: editable, so it must NOT claim read-only, but say the restriction went.
                if (result.RestrictionsRemoved)
                    readOnlySuffix = " (owner restrictions removed)";
                if (result.RecoveredFromRaster)
                    readOnlySuffix = " (recovered - pages rasterized, text not selectable)";
                SetStatus($"Opened {System.IO.Path.GetFileName(result.DisplayPath)}{readOnlySuffix} - {_doc.PageCount} page(s)");
                UpdateTabChrome();

                // OriginalPath is the user's document: the in-place save target and the session
                // entry. A document has one unless WE rebuilt it (raster recovery writes a lossy
                // reconstruction into %TEMP%) or the path simply is not on disk.
                //
                // Living under %TEMP% is deliberately NOT disqualifying: a PDF opened from an email
                // attachment extracts to a temp folder and is still a real document the user expects
                // Ctrl+S to update. The working files TDPdf creates ITSELF (New, merge-on-drop,
                // imported images, zip extraction) are classified where they are created — see
                // FinalizeUnsavedTab and OpenSeparatelyAsync — not by where they happen to live.
                //
                // Assigned unconditionally so reopening into a context that already held a document
                // can never leave the previous file's path behind. Skipped entirely for an internal
                // reload, which keeps the tab pointed at the document the user actually opened.
                if (!internalReload)
                {
                    bool hasRealHome = !result.RecoveredFromRaster && System.IO.File.Exists(result.DisplayPath);
                    _ctx.OriginalPath = hasRealHome ? result.DisplayPath : null;
                    // The recent list keeps its own stricter gate — temp paths are correctly excluded
                    // from it even when they are a perfectly good save target.
                    if (hasRealHome && IsRecentEligiblePath(result.DisplayPath)) AddRecentFile(result.DisplayPath);
                }
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

        // Themed "Password Required" prompt. The old inline dialog kept the native OS title bar and
        // used stock PasswordBox/Button chrome, which rendered as light Aero controls on a dark
        // panel; TdpDialog gives it the same borderless wordmark chrome as every other TDPdf dialog.
        // Enter/Esc and the Open/Cancel semantics (null == cancelled) are unchanged.
        private string? PromptForPassword(string filename) => TdpDialog.PromptPassword(this, filename);

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
            // LayoutZoomScale, not the true zoom: this sizes the RASTER, which has to match the
            // tile's on-screen pixels 1:1. (Rendering at the true zoom would over-sample by the
            // display factor and, at 400%, allocate a needlessly larger bitmap.)
            return Math.Max(1, (int)Math.Round(_currentDpiScale * LayoutZoomScale * 96.0));
        }

        // #189 (upstream KillerPDF PR #194): the one device scale every RASTER budget is measured
        // against. It has to be _currentDpiScale and not VisualTreeHelper.GetDpi(this): both
        // GetDpi and CompositionTarget.TransformToDevice read WPF's HwndTarget.CurrentDpiScale,
        // which is only refreshed when WPF's own internal hook processes WM_DPICHANGED — and our
        // WndProc claims that message (handled = true) so it can apply Windows' suggested rect
        // against the custom chrome. Public HwndSource hooks run BEFORE the internal HwndTarget
        // hook, so handling it there ends the chain and WPF's DPI state never moves. _currentDpiScale
        // is seeded from the visual at SourceInitialized and then taken straight from the message's
        // own wParam in WmDpiChanged, so it is the one value that is right after a monitor move.
        // GetCurrentDpiX (the primary tile) already reads it; the continuous re-sharpen budget and
        // the Grid / Two-Page tile budget go through here so all three rasterize at one density.
        // Windows per-monitor DPI is isotropic, so collapsing X/Y to a single scalar loses nothing.
        private double CurrentRenderDpiScale() => _currentDpiScale > 0 ? _currentDpiScale : 1.0;

        private void InvalidateRenderCache()
        {
            _renderCache.Clear();
            _renderDims.Clear();
            // #135 follow-up: the night-mode image boxes are measured from the document, so they go
            // stale with it (a page rotated, cropped, transformed, or the file re-saved to a fresh
            // temp copy). They are keyed by file path as well, so this is belt and braces.
            _pageImageRects.Clear();
        }

        // #122 (upstream v1.6.3): the per-tab rendered-page cache used to grow without bound — a page
        // was added on every visit and never evicted, so paging through a long document pinned a
        // bitmap per page. Cap it and, when over, drop the entries whose page is FARTHEST from the one
        // just rendered: renders cluster around the viewport, so the farthest are least likely next.
        private const int RenderCachePageCap = 48;

        // #189 (upstream KillerPDF v1.7.2): the count cap alone was not enough. An entry's size
        // scales with the page and the render budget, so 48 cached Letter pages can hold ~630 MB in
        // ONE tab — and every open tab keeps its own cache. Budget the cache in BYTES as well, with
        // a floor of nearby pages so the moving window around the viewport still serves instantly.
        private const long RenderCacheByteBudget = 160L << 20;   // ~160 MB per tab
        private const int RenderCacheMinPages = 6;

        // Bytes held by this tab's cached page bitmaps.
        //
        // Upstream needs a parallel size dictionary written on the producing thread, because ITS
        // cache is a ConcurrentDictionary filled from background render threads and reading
        // bmp.PixelWidth during eviction was a cross-thread touch on the BitmapSource. We do NOT
        // have that problem: _renderCache is a plain Dictionary written only from RenderPage, whose
        // awaits resume on the UI thread, and our RenderedPage record already carries PixelWidth /
        // PixelHeight as plain ints captured at render time. So measure straight off the record and
        // never touch the BitmapSource — do not "restore" upstream's parallel dictionary here.
        private long RenderCacheBytes()
        {
            long total = 0;
            foreach (var entry in _renderCache.Values)
                total += 4L * entry.PixelWidth * entry.PixelHeight;   // Bgra32
            return total;
        }

        private bool OverRenderCacheBudget()
        {
            int count = _renderCache.Count;
            if (count > RenderCachePageCap) return true;
            return count > RenderCacheMinPages && RenderCacheBytes() > RenderCacheByteBudget;
        }

        private void CapRenderCache(int aroundPage)
        {
            if (!OverRenderCacheBudget()) return;
            var keys = _renderCache.Keys.ToList();
            // Farthest page first.
            keys.Sort((a, b) => Math.Abs(b.pageIndex - aroundPage).CompareTo(Math.Abs(a.pageIndex - aroundPage)));
            foreach (var k in keys)
            {
                if (!OverRenderCacheBudget()) break;
                // Distance 0: this is the page we just rendered (the cache is keyed by DPI bucket
                // too, so it can hold more than one entry for it) and, because the sort put the
                // farthest first, so is every key after it. Nothing sane left to evict. Upstream
                // guards its rescanning eviction loop the same way with `if (bestDist <= 0) break`;
                // ours walks a fixed sorted list, so it terminates whatever the budgets say — this
                // only stops it throwing away the page on screen to chase a budget it cannot meet.
                if (k.pageIndex == aroundPage) break;
                _renderCache.Remove(k);
            }
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
            DocumentContext renderContext = _ctx;
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
                        _primaryPageBitmap = null;
                        PageImage.Source = null;
                        SetStatus($"Page {pageIndex + 1} - could not render");
                        return;
                    }

                    renderedPage = new RenderedPage(result.Bitmap, result.DipWidth, result.DipHeight, result.Width, result.Height);
                    _renderCache[(pageIndex, dpiX)] = renderedPage;
                    CapRenderCache(pageIndex);
                }

                if (_doc is null) return;

                // #135 follow-up: this page's image boxes, so night mode can carve the pictures back
                // out of the inversion. Off the UI thread on the first inverted render of the page
                // (one PdfPig open, disposed there); a no-op afterwards and whenever night mode or
                // the carve-out is off, so it costs the common path nothing.
                var keepRects = await ImageRectsForAsync(currentFile, pageIndex, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (_doc is null) return;

                _renderDims[pageIndex] = ((int)Math.Round(renderedPage.DisplayWidth), (int)Math.Round(renderedPage.DisplayHeight));
                PageImage.Tag = pageIndex;   // page identity for Grid scroll tracking (nearest-tile counter)
                // #135: DisplayBitmap returns the cached bitmap untouched unless the display-only
                // invert is on, in which case it returns a separate inverted copy. _renderCache (and
                // _primaryPageBitmap, which the image-edit tool bakes into the saved PDF) keep the
                // document's true colors.
                _primaryPageBitmap = renderedPage.Bitmap;
                PageImage.Source = DisplayBitmap(renderedPage.Bitmap, keepRects);
                PageImage.Width = renderedPage.DisplayWidth;
                PageImage.Height = renderedPage.DisplayHeight;
                _annotationCanvas.Width = renderedPage.DisplayWidth;
                _annotationCanvas.Height = renderedPage.DisplayHeight;
                _textEditorCanvas.Width = renderedPage.DisplayWidth;
                _textEditorCanvas.Height = renderedPage.DisplayHeight;
                // #197: the cursor-trailing page tooltip added by #151 is gone — the viewport-corner
                // badge announces the page instead, in one fixed place, for every view mode.
                ShowPageBadge(pageIndex);
                // The display factor is per page, so a document of mixed page sizes has to
                // re-derive the transform when the primary tile changes, at the same true zoom.
                SyncLayoutZoom();
                ClearSelection();
                ClearSecondaryPages();
                RenderAllAnnotations(pageIndex);
                SetStatus($"Page {pageIndex + 1} of {_doc.PageCount} - {Zoom.DisplayText}");
                // Defer additional pages until layout has settled so ActualWidth is valid.
                // RenderPageLinks runs AFTER RenderAdditionalPages so ClearSecondaryPages
                // inside RenderAdditionalPages doesn't wipe the overlays we just added.
                // #115: Background, NOT Loaded. All three of these mutate AnnotationCanvas.Children,
                // and Loaded outranks Render, so the continuation can be dispatched while the layout
                // pass it is meant to follow is still in flight — which is what was tearing the
                // canvas out from under Canvas.MeasureOverride. Background runs strictly after
                // layout completes, which is all "settled" ever meant here.
                int linkBitmapW = renderedPage.PixelWidth;
                int linkBitmapH = renderedPage.PixelHeight;
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    if (cancellationToken.IsCancellationRequested
                        || !ReferenceEquals(_ctx, renderContext)
                        || !string.Equals(_currentFile, currentFile, StringComparison.Ordinal))
                    {
                        return;
                    }
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
                _primaryPageBitmap = null;
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
        /// Keeps the primary page tile's margin in step with the pairing (#193). Every mode but a
        /// book layout's lone cover keeps the XAML default 0,0,12,12: the right 12px is the spread
        /// gutter between the two pages of a Two-Page spread (and the column gutter in Grid), and
        /// the bottom 12px is the row gutter. The cover has nothing to its right, so the gutter
        /// would make it hang left of an empty half.
        /// </summary>
        private void SyncPrimaryTileMargin(bool bookCover)
        {
            if (_pageContentPanel.Children.Count > 0 && _pageContentPanel.Children[0] is Border primaryBorder)
                primaryBorder.Margin = bookCover ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 12, 12);
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
            // #193 pairing site 1 of 4: the primary tile's own margin. Book layout's cover has no
            // facing page, so it drops the 12px spread gap and centres like a single page instead
            // of hanging left of an empty slot. Set before the early return below so leaving the
            // cover (or leaving Two-Page altogether) always puts the gap back.
            bool bookCover = IsBookCoverRow(primaryPageIdx);
            SyncPrimaryTileMargin(bookCover);
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
            // #193 pairing site 2 of 4: the slot width. A book cover is a ONE-page row, and its
            // tile carries no right margin (SyncPrimaryTileMargin above), so its slot is the bare
            // page — otherwise the 12px gutter is counted into a one-slot panel and the cover sits
            // 6px left of centre.
            double pageSlotW = primaryPageW + (bookCover ? 0 : 12); // page width + right-gutter margin
            // Cap how many secondary pages we render at once. Long documents otherwise
            // allocate a (potentially multi-MB) bitmap per page on first grid display.
            // Two-Page renders just the single page to the right of the primary — except a book
            // layout's cover (#193 pairing site 3 of 4), which has no partner at all.
            int maxSecondaryPages = twoPage ? (bookCover ? 0 : 1) : 25;

            // Inner space in pre-zoom (tile-layout) coords, so it is the LAYOUT scale that divides
            // out here, not the true zoom — primaryPageW above is a tile width.
            double availablePreZoom = (viewportW - 24) / Math.Max(0.0001, LayoutZoomScale);
            // Two-Page always shows exactly two columns; Grid wraps to fit the viewport, but never
            // claims more columns than there are tiles to put in them. Without that ceiling a very
            // small page (whose tile is many times its natural size, so its layout scale at the
            // 5% floor is tiny) would ask the WrapPanel for a width of hundreds of thousands of
            // DIPs to hold at most 26 pages.
            // #193 pairing site 4 of 4: sizing the panel for TWO slots parks a lone book cover in
            // the left half of a centred two-slot panel, which reads as left-aligned. One page in
            // the row means one slot.
            int pagesPerRow = twoPage
                ? (bookCover ? 1 : 2)
                : Math.Clamp((int)(availablePreZoom / pageSlotW), 1, maxSecondaryPages + 1);
            double panelW = pagesPerRow * pageSlotW;
            if (panelW > 0) _pageContentPanel.Width = panelW;

            // #189: one authoritative device scale (see CurrentRenderDpiScale) rather than
            // VisualTreeHelper.GetDpi, which does not survive a monitor move here. Both the box
            // below and the bitmap DPI further down have to use the SAME number — scaledMax scales
            // the pixel width up by it and the bitmap DPI divides it back out, so a mismatch would
            // resize the tiles rather than just re-sharpen them. This is density-only: the tile's
            // DIP width works out to RenderBoxDip either way, matching the primary tile, which
            // already sizes its raster off _currentDpiScale via GetCurrentDpiX.
            double dpiScaleX = CurrentRenderDpiScale();
            double dpiScaleY = dpiScaleX;
            // Same square box as the primary tile (PdfDocumentService.RenderBoxDip), in device
            // pixels, so grid tiles land on the same DIP size as the primary and the display
            // factor is one number for the whole wrap panel.
            int scaledMax = (int)(TDPdf.Services.PdfDocumentService.RenderBoxDip * Math.Max(dpiScaleX, dpiScaleY));
            int lastPage = Math.Min(_doc.PageCount - 1, primaryPageIdx + maxSecondaryPages);
            string currentFile = _currentFile;

            List<(int pi, int w, int h, byte[] rawBytes, FracRect[] keep)> pages;
            try
            {
                pages = await Task.Run(() =>
                {
                    var result = new List<(int pi, int w, int h, byte[] rawBytes, FracRect[] keep)>();
                    using var docReader = DocLib.Instance.GetDocReader(currentFile, new PageDimensions(scaledMax, scaledMax));
                    // #135 follow-up: one PdfPig open serves every uncached page in this loop and is
                    // released with the loop (see PigScope).
                    using var pig = new PigScope();
                    for (int i = primaryPageIdx + 1; i <= lastPage; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        using var pageReader = docReader.GetPageReader(i);
                        int w = pageReader.GetPageWidth();
                        int h = pageReader.GetPageHeight();
                        // #141: with the annotations the file carries (see PdfiumInterop).
                        // Form fields stay BAKED here, unlike the primary tile: TDPdf's live
                        // form overlays (RenderFormFields) exist only on _annotationCanvas, so
                        // this surface has nothing to draw the values with. Hiding the widgets
                        // would blank every filled field instead of un-ghosting it.
                        var rawBytes = TDPdf.Services.PdfiumInterop.RenderPageWithAnnotations(currentFile, i, w, h)
                                       ?? pageReader.GetImage();
                        if (w <= 0 || h <= 0 || rawBytes is null) continue;
                        // Measured here rather than on the UI thread below, so the parse never
                        // stalls the tile pass.
                        result.Add((i, w, h, rawBytes, _docInvert ? ImageRectsFor(currentFile, i, pig) : []));
                    }
                    return result;
                }, ct);
            }
            catch (OperationCanceledException) { return; }
            catch { return; /* non-critical; primary page already visible */ }

            if (ct.IsCancellationRequested) return;

            foreach (var (pi, w, h, rawBytes, keep) in pages)
            {
                if (ct.IsCancellationRequested) return;

                _renderDims[pi] = (w, h);
                // #135: display-only invert, with the page's pictures carved back out (empty keep =
                // the plain full-page flip). The buffer is ours and is about to become a throwaway
                // display bitmap, so flip it in place — nothing else ever sees these bytes.
                if (_docInvert) InvertBgraInPlaceExcept(rawBytes, w, h, keep);
                var bitmap = new WriteableBitmap(w, h, 96.0 * dpiScaleX, 96.0 * dpiScaleY, PixelFormats.Bgra32, null);
                bitmap.WritePixels(new Int32Rect(0, 0, w, h), rawBytes, w * 4, 0);

                var img = new Image { Source = bitmap, Stretch = Stretch.None };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

                // #197: no per-tile page tooltip anymore — it trailed the cursor across the tiles
                // and read as noise; the corner badge is the page indicator now. The name stays as
                // an AutomationProperties value so a screen reader can still identify the tile,
                // which is all the tooltip ever contributed to the accessibility tree.
                var overlay = new Canvas
                {
                    Width = w, Height = h,
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand
                };
                AutomationProperties.SetName(overlay, $"Page {pi + 1}");
                AutomationProperties.SetHelpText(overlay, "Click to make this the current page.");
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
                    Child = pageGrid,
                    Tag = pi   // page identity for Grid scroll tracking (nearest-tile counter)
                });
            }
        }

        // A "held" status briefly outranks routine ones. Scrolling the wheel over the logo to
        // resize the app has to show "App size N%", but the chrome resize immediately re-runs the
        // fit pipeline, whose "Page x of y - 100%" would stomp the readout on the very next
        // layout pass. While a hold is live, plain SetStatus calls are dropped; the hold refreshes
        // on every wheel notch and expires on its own, after which normal statuses flow again.
        // The hold is short and covers only that stomp — the readout's own five-second lifetime is
        // ShowScaleReadout's job (AppScale.cs). Put here rather than at the ~200 SetStatus callers
        // because SetBusy / SetFileOperationBusy / SetWorkerStatus all funnel through this line.
        private DateTime _statusHoldUntil = DateTime.MinValue;

        private void SetStatus(string text)
        {
            if (DateTime.UtcNow < _statusHoldUntil) return;   // a held message is showing
            StatusText.Text = text;
        }

        /// <summary>
        /// Writes a status that plain <see cref="SetStatus"/> calls cannot overwrite for
        /// <paramref name="holdMs"/> milliseconds.
        /// </summary>
        private void SetStatusHeld(string text, int holdMs = 1200)
        {
            _statusHoldUntil = DateTime.UtcNow.AddMilliseconds(holdMs);
            StatusText.Text = text;
        }

        // ---- Transient status readouts -------------------------------------------------------
        // One snapshot / hold / restore for every "flash something on the status line, then put back
        // what was there" caller: the app-scale readout (AppScale.cs) and the #status-line file size
        // below. Whatever was showing before the FIRST flash of a burst is snapshotted and put back,
        // but only if the readout is still the text on screen — so a real status written after the
        // hold lapsed is never replaced by a stale one. The restore assigns StatusText directly
        // rather than going through SetStatus: this is putting a line back, not reporting something
        // new. Normal priority rather than DispatcherTimer's default Background, so a busy render
        // cannot leave the readout parked on the footer.
        private System.Windows.Threading.DispatcherTimer? _statusFlashTimer;
        private string _statusFlashWas  = string.Empty;
        private string _statusFlashText = string.Empty;

        /// <param name="holdMs">How long plain <see cref="SetStatus"/> calls are suppressed.</param>
        /// <param name="life">How long the readout stays on screen before the old line comes back.</param>
        private void FlashStatus(string text, int holdMs, TimeSpan life)
        {
            if (_statusFlashTimer is null)
            {
                _statusFlashTimer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Normal);
                _statusFlashTimer.Tick += (_, _) =>
                {
                    _statusFlashTimer!.Stop();
                    if (StatusText.Text == _statusFlashText) StatusText.Text = _statusFlashWas;
                };
            }

            // Only the first flash of a burst snapshots; the rest would capture our own readout.
            if (!_statusFlashTimer.IsEnabled) _statusFlashWas = StatusText.Text;
            _statusFlashTimer.Stop();
            _statusFlashTimer.Interval = life;
            _statusFlashText = text;
            SetStatusHeld(text, holdMs);
            _statusFlashTimer.Start();
        }

        // Clicking the status line (or Shift+F4) flashes the open document's file size for a beat and
        // then puts back whatever was showing — upstream KillerPDF v1.7.2. Held so page-change chatter
        // cannot overwrite it mid-read.
        private void StatusText_Click(object sender, MouseButtonEventArgs e) => ShowCurrentFileSize();

        private void ShowCurrentFileSize()
        {
            // The user's real document, not the %TEMP% working copy a structural edit repoints us at;
            // falls back to the working path for a never-saved (New / merged-on-drop) document.
            string? path = _ctx.OriginalPath ?? _currentFile;
            if (_doc is null || string.IsNullOrEmpty(path)) return;
            long bytes;
            try
            {
                if (!File.Exists(path)) return;
                bytes = new FileInfo(path).Length;
            }
            catch { return; }   // a vanished / unreadable file is not worth a dialog
            FlashStatus($"{System.IO.Path.GetFileName(path)} — {FormatFileSize(bytes)}",
                        holdMs: 2500, life: TimeSpan.FromMilliseconds(2600));
        }

        /// <summary>Human-readable file size. Shared by the Document Info summary and the status-line
        /// flash so the two never disagree about how big a document is.</summary>
        private static string FormatFileSize(long bytes)
            => bytes >= 1L << 20 ? $"{bytes / (double)(1 << 20):N1} MB"
             : bytes >= 1L << 10 ? $"{bytes / (double)(1 << 10):N0} KB"
             : $"{bytes} bytes";

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

        // ============================================================
        // Cancellable long-running operations (OCR, image export)
        // ============================================================

        // Non-null only while such an operation is in flight. Window state rather than
        // per-document state on purpose: BeginCancellableOp disables the whole window for the
        // duration, so exactly one of these can ever be running. The WM_KEYDOWN hook in WndProc
        // cancels it on Esc even though the WPF content is disabled, because the native HWND
        // stays Win32-enabled and still receives key messages.
        private CancellationTokenSource? _cancellableOpCts;

        /// <summary>Registers a cancellable operation, shows the busy state, and returns its token.</summary>
        private CancellationToken BeginCancellableOp(string startStatus)
        {
            _cancellableOpCts?.Dispose();
            _cancellableOpCts = new CancellationTokenSource();
            SetFileOperationBusy(true, startStatus);
            return _cancellableOpCts.Token;
        }

        private void EndCancellableOp()
        {
            SetFileOperationBusy(false);
            _cancellableOpCts?.Dispose();
            _cancellableOpCts = null;
        }

        /// <summary>Updates the status line from any thread (this work runs on a background Task).</summary>
        private void SetWorkerStatus(string msg)
        {
            if (Dispatcher.CheckAccess()) SetStatus(msg);
            else Dispatcher.Invoke(() => SetStatus(msg));
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

            // Row-wrapping in Grid/TwoPage comes from the explicit _pageContentPanel.Width set in
            // RenderAdditionalPages, not from constraining the ScrollViewer's available width — so
            // Auto never breaks the wrap. Disabling it here used to hide real overflow whenever a
            // page's rendered width (pagesPerRow is clamped to a minimum of 1) exceeded the shrunk
            // viewport at a manual zoom, clipping content with no way to scroll to it.
            PagePreviewPanel.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;

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
            // #131: a view-mode switch is an explicit gesture and belongs on the commit
            // chokepoint alongside tool / tab / page switches. It only ever reached it
            // incidentally, through ApplyZoom, and then only when the switch's fit happened to
            // change the zoom VALUE — an equal SetZoomLevel raises no PropertyChanged. Entering
            // Continuous collapses the page panel, so without this the live editor simply went
            // invisible while still being the active one.
            CommitActiveTextBox();
            _viewMode = mode;
            // The scroll surface underneath the wheel just changed, so a half-accumulated
            // page-flip gesture from the previous mode must not complete against the new one.
            _wheelFlipGate.Reset();
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
                _continuousWindowCts?.Cancel();   // #122: stop in-flight window-maintenance render
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

        // ── Two-Page book layout (upstream KillerPDF #193) ─────────────────────────────────────

        private void TwoPageBook_Click(object sender, RoutedEventArgs e) => ToggleTwoPageBook(!_twoPageBook);

        /// <summary>
        /// Turns the Two-Page book layout on or off, persists it, and re-pairs the spread that is
        /// on screen. Available from any view mode — it is a standing preference for how Two-Page
        /// pairs, so it can be set before switching into Two-Page — but it only changes pixels
        /// while Two-Page is the active mode.
        /// </summary>
        private void ToggleTwoPageBook(bool on)
        {
            _twoPageBook = on;
            _twoPageBookMenuItem.IsChecked = on;
            try
            {
                TDPdf.Properties.Settings.Default.TwoPageBookLayout = on;
                TDPdf.Properties.Settings.Default.Save();
            }
            catch { /* non-critical user preference */ }
            SetStatus(on ? "Book layout on - the cover shows alone" : "Book layout off - pages pair from the cover");
            if (_doc is null || _viewMode != ViewMode.TwoPage) return;
            // Re-pair what is on screen. Toggling is an explicit request about pairing, so unlike
            // every other navigation path it is allowed to move the selection onto the new spread's
            // left page; the selection IS the primary tile here (see NavigatePageStep).
            int cur = Math.Max(0, PageList.SelectedIndex);
            int start = SpreadStart(cur);
            if (start != cur) { PageList.SelectedIndex = start; return; }   // SelectionChanged re-renders
            RenderPage(start);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                () => RefreshPageView(start));
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

            // A fit the user explicitly chose is a standing preference and outranks the
            // per-view-mode default below (upstream v1.7.1): someone reading on a small screen
            // should not have to pick Fit Page again for every manual they open.
            ZoomFitMode preferred = ReadDefaultFitMode();
            // #201 (upstream KillerPDF): a manual zoom is just as much a standing preference as a
            // fit, and until now every open threw it away. ReadLastManualZoom is non-zero only when
            // the user's LAST EXPLICIT zoom decision was a manual one — picking Fit Width or Fit
            // Page clears it (SaveDefaultFitMode) and the fits the app applies for you never write
            // it, so the number here is always one a person typed, picked or wheeled, never one
            // derived from a window we no longer have. That is what keeps the old "raw zoom saved
            // at a different window or monitor size opens the document enormous or microscopic"
            // failure from coming back: a FIT is replayed as a fit against the CURRENT window
            // (window-relative by definition), and only a deliberate manual zoom is replayed as a
            // number — clamped to ZoomViewModel's 5%-400% range on the way in.
            double manualZoom = ReadLastManualZoom();
            bool restoreManual = preferred == ZoomFitMode.None && manualZoom > 0;

            if (isContinuous)
            {
                // SetupContinuousView ends in FitToWidth, which is also Continuous's own default;
                // only a remembered Fit Page — or a remembered manual zoom — has to override it,
                // once the strip exists.
                SetupContinuousView(Math.Max(0, PageList.SelectedIndex));
                if (preferred == ZoomFitMode.Page) FitToPage();
                else if (restoreManual) ApplyRestoredManualZoom(manualZoom);
                FocusDocumentSurface();
                return;
            }

            // Otherwise Single / Two-Page open fit-to-page and Grid keeps its column-fit default
            // (fit-width is a sensible neutral starting zoom that RefreshPageView then
            // column-snaps). Grid is excluded from the manual restore: its zoom is not a free
            // number but a column count RefreshPageView immediately snaps back, so replaying one
            // would only produce a fight it always loses.
            if (preferred == ZoomFitMode.Width) FitToWidth();
            else if (preferred == ZoomFitMode.Page) FitToPage();
            else if (restoreManual && _viewMode != ViewMode.Grid) ApplyRestoredManualZoom(manualZoom);
            else if (_viewMode == ViewMode.Single || _viewMode == ViewMode.TwoPage) FitToPage();
            else FitToWidth();
            RefreshPageView(_viewMode == ViewMode.Grid ? 0 : Math.Max(0, PageList.SelectedIndex));
            FocusDocumentSurface();
        }

        /// <summary>
        /// Restores a remembered manual zoom (#201). Clears the fit tracking first, so the very
        /// next window resize does not snap the restored zoom away to a fit the user never asked
        /// for; <see cref="ZoomViewModel.SetZoomLevel"/> coerces the value into the 5%-400% range.
        /// </summary>
        private void ApplyRestoredManualZoom(double zoom)
        {
            _zoomFitMode = ZoomFitMode.None;
            _manualZoomIntent = true;   // the restored zoom IS the standing manual preference
            Zoom.SetZoomLevel(zoom);
        }

        /// <summary>
        /// Puts keyboard focus on the document surface after a document opens (#196, upstream
        /// KillerPDF). Without it focus sat on the Open button — most visibly when TDPdf was
        /// launched from Explorer or the command line — so the ScrollViewer's OWN keyboard
        /// scrolling (Space / Shift+Space, and the arrow keys inside a page zoomed past the
        /// viewport) did nothing until the user clicked the page. This is narrower than it sounds:
        /// arrow and PgUp/PgDn PAGING already worked from anywhere, because OnPreviewKeyDown claims
        /// those at the window level. Never steals the caret from someone already typing — the find
        /// bar, the page-jump box, a form field or the editable zoom combo all keep focus.
        /// </summary>
        private void FocusDocumentSurface()
        {
            if (_doc is null || PagePreviewPanel.Visibility != Visibility.Visible) return;
            // Same three surfaces OnPreviewKeyDown steps aside for, in the same order: a live
            // annotation text box, an inline bookmark rename, and any other caret-bearing control.
            if (_activeTextBox is { IsFocused: true } || _bmRenaming || IsTypingTarget()) return;
            PagePreviewPanel.Focus();
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
            // #130 (upstream v1.6.4): a PDF whose page tree parses to zero pages must not reach the
            // Pages[0] deref below — Continuous view crashed with an out-of-range index. Nothing to
            // lay out, so bail after clearing any stale tiles.
            if (_doc.PageCount == 0) return;

            // Upstream v1.6.3: entering Continuous must restore its own scrollbar setup, since
            // RefreshPageView (which now always leaves this Auto) early-returns for Continuous and
            // never gets a chance to set it. Explicit here so Continuous doesn't inherit whatever a
            // prior mode left behind.
            PagePreviewPanel.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            PagePreviewPanel.VerticalScrollBarVisibility   = ScrollBarVisibility.Auto;

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
                // #197: the #151 slot tooltip is gone with the rest of them — in a continuous strip
                // a tooltip that follows the cursor down the whole document was the worst offender.
                // The accessible name stays so the slot is still identifiable to a screen reader.
                AutomationProperties.SetName(placeholder, $"Page {i + 1}");
                AutomationProperties.SetHelpText(placeholder, "Click to make this the current page.");
                placeholder.PreviewMouseLeftButtonDown += (_, _) => SelectContinuousPage(capturedI);
                _continuousPanel.Children.Add(placeholder);
                y += slotH + 12;
            }

            // Entering Continuous flips the display factor to 1, so the layout scale for the zoom
            // already in force changes even though the zoom itself has not.
            SyncLayoutZoom();

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
            _continuousWindowCts?.Cancel();   // #122: also stop any in-flight window-maintenance render
            _continuousSharpPages.Clear();
            _continuousBaseBitmaps.Clear();
            _continuousSharpW = 0;

            string currentFile = _currentFile;
            int pageCount = _doc.PageCount;
            double targetW = _continuousPageW;
            int renderW = Math.Max(800, Math.Min(2048, (int)(targetW * 2)));

            // #122: render only the window of pages around the page we're opening at; the rest stay as
            // white scaffold and are filled by MaintainContinuousWindow as they scroll into range. This
            // is what keeps a long image-heavy document from materializing every page bitmap at once.
            int center = _continuousScrollTarget >= 0 ? Math.Min(_continuousScrollTarget, pageCount - 1) : 0;
            int winLo = Math.Max(0, center - ContinuousBaseWindow);
            int winHi = Math.Min(pageCount - 1, center + ContinuousBaseWindow);

            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    using var docReader = DocLib.Instance.GetDocReader(
                        currentFile, new PageDimensions(renderW, renderW * 2));
                    // #135 follow-up: one PdfPig open covers every uncached page this pass fills and
                    // is released with the pass (see PigScope).
                    using var pig = new PigScope();

                    for (int i = winLo; i <= winHi; i++)
                    {
                        if (cts.IsCancellationRequested) return;
                        using var pr = docReader.GetPageReader(i);
                        int w = pr.GetPageWidth();
                        int h = pr.GetPageHeight();
                        // #141: with the annotations the file carries (see PdfiumInterop).
                        // Form fields stay BAKED here, unlike the primary tile: TDPdf's live
                        // form overlays (RenderFormFields) exist only on _annotationCanvas, so
                        // this surface has nothing to draw the values with. Hiding the widgets
                        // would blank every filled field instead of un-ghosting it.
                        var raw = TDPdf.Services.PdfiumInterop.RenderPageWithAnnotations(currentFile, i, w, h)
                                  ?? pr.GetImage();
                        if (w <= 0 || h <= 0 || raw is null) continue;

                        int fi = i, fw = w, fh = h;
                        byte[] bytes = raw;
                        // Measured off the UI thread, so the marshal below stays a pure blit.
                        FracRect[] keep = _docInvert ? ImageRectsFor(currentFile, i, pig) : [];
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (cts.IsCancellationRequested || _viewMode != ViewMode.Continuous) return;
                            if (fi >= _continuousPanel.Children.Count) return;
                            if (_continuousPanel.Children[fi] is not Border slot) return;

                            double dipW = slot.Width;
                            double dipH = dipW * fh / fw;
                            double dpiX = 96.0 * fw / dipW;
                            double dpiY = 96.0 * fh / dipH;

                            // #135: display-only invert, pictures carved back out (empty keep = the
                            // plain full-page flip, which is also what "invert images too" wants).
                            if (_docInvert) InvertBgraInPlaceExcept(bytes, fw, fh, keep);
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
                    if (_viewMode != ViewMode.Continuous) return;
                    MaintainContinuousWindow();   // #122: render pages entering the window, release those leaving
                    ResharpenContinuousVisible();
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
            // #189 (upstream KillerPDF PR #194): targetW * zoom * dpiScale already IS the page's
            // on-screen size in device pixels, so the extra * 2 this used to carry was a 2× linear
            // supersample on top of an already-correct budget — 4× the pixels and 4× the bytes for
            // detail the display cannot resolve. Because fit-width zoom is viewportW / targetW,
            // targetW cancels and the old hiW reduced to twice the viewport width, which is why the
            // cost tracked window size and display resolution rather than anything about the file.
            // Render at the size we actually draw at. (baseW above is the BASE render budget and is
            // deliberately left alone — upstream did not change it either.)
            double dpiScale = CurrentRenderDpiScale();
            int hiW = (int)Math.Min(4096, targetW * dpiScale * Math.Max(1.0, zoom));

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

            // #189: hiW is now a true device-pixel width, so the trigger is simply "has the base
            // render run out of pixels for the size we are drawing it at". The old 1.25× margin was
            // calibrated against a hiW that was inflated 2×; leaving it here would stop the pass
            // firing where it is still needed and pages would be upscaled from the base render.
            // 1.05 is hysteresis only, so a page sitting on the boundary doesn't re-raster on a nudge.
            bool wantHi = hiW >= (int)(baseW * 1.05);

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
                // #135 follow-up: one PdfPig open for the whole re-sharpen pass (see PigScope).
                using var pig = new PigScope();
                try
                {
                    foreach (int p in work)
                    {
                        if (cts.IsCancellationRequested) return;
                        docReader ??= DocLib.Instance.GetDocReader(currentFile, new PageDimensions(hiW, hiW * 2));
                        using var pr = docReader.GetPageReader(p);
                        int w = pr.GetPageWidth(), h = pr.GetPageHeight();
                        // #141: with the annotations the file carries (see PdfiumInterop).
                        // Form fields stay BAKED here, unlike the primary tile: TDPdf's live
                        // form overlays (RenderFormFields) exist only on _annotationCanvas, so
                        // this surface has nothing to draw the values with. Hiding the widgets
                        // would blank every filled field instead of un-ghosting it.
                        var raw = TDPdf.Services.PdfiumInterop.RenderPageWithAnnotations(currentFile, p, w, h)
                                  ?? pr.GetImage();
                        if (w <= 0 || h <= 0 || raw is null) continue;

                        int fp = p, fw = w, fh = h;
                        byte[] bytes = raw;
                        // Measured here (off the UI thread); the rects are fractional, so the same
                        // cached set serves this hi-res raster and the base one it replaces.
                        FracRect[] keep = _docInvert ? ImageRectsFor(currentFile, p, pig) : [];
                        if (cts.IsCancellationRequested) return;
                        Dispatcher.Invoke(() =>
                        {
                            if (cts.IsCancellationRequested || _viewMode != ViewMode.Continuous) return;
                            SharpenContinuousSlot(fp, fw, fh, bytes, keep);
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
        private void SharpenContinuousSlot(int pageIndex, int pxW, int pxH, byte[] bgra, FracRect[] keep)
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

            // #135: display-only invert, pictures carved back out (empty keep = full-page flip).
            if (_docInvert) InvertBgraInPlaceExcept(bgra, pxW, pxH, keep);
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

        // #122 (upstream v1.6.3): scroll-settle maintenance for the virtualized Continuous view. Keeps
        // a window of base bitmaps around the viewport: releases slots that have left the window
        // (Image.Source = null; the slot keeps its height, so nothing reflows) and renders base bitmaps
        // for slots that have entered it and are still bare. The generous ±ContinuousBaseWindow margin
        // means ordinary scrolling always finds a rendered page; only sustained scrolling through a
        // long document trims the far pages. Runs on the UI thread; the render itself is off-thread.
        private void MaintainContinuousWindow()
        {
            if (_viewMode != ViewMode.Continuous || _doc is null || _currentFile is null) return;
            int slotCount = _continuousPanel.Children.Count;
            if (slotCount == 0 || _continuousTops.Count == 0) return;

            double zoom = Math.Max(0.01, Zoom.ZoomLevel);
            double viewTop = PagePreviewPanel.VerticalOffset / zoom;
            double viewBot = (PagePreviewPanel.VerticalOffset + PagePreviewPanel.ViewportHeight) / zoom;
            int firstVis = -1, lastVis = -1;
            for (int i = 0; i < _continuousTops.Count && i < slotCount; i++)
            {
                double top = _continuousTops[i];
                double h = ((FrameworkElement)_continuousPanel.Children[i]).Height;
                if (double.IsNaN(h)) h = 0;
                if (top + h >= viewTop && top <= viewBot) { if (firstVis < 0) firstVis = i; lastVis = i; }
            }
            if (firstVis < 0) { firstVis = 0; lastVis = 0; }   // before first layout: treat the top as visible
            int lo = Math.Max(0, firstVis - ContinuousBaseWindow);
            int hi = Math.Min(slotCount - 1, lastVis + ContinuousBaseWindow);

            // Release every rendered slot outside the window (heights stay, so no reflow / scroll jump).
            for (int i = 0; i < slotCount; i++)
            {
                if (i >= lo && i <= hi) continue;
                if (_continuousPanel.Children[i] is not Border slot || slot.Child is not Image img) continue;
                if (img.Source is null) continue;
                img.Source = null;
                slot.Background = BrushResource("BgPanel");
                _continuousSharpPages.Remove(i);
                _continuousBaseBitmaps.Remove(i);
            }

            // Collect in-window slots that still need a base bitmap.
            var need = new List<int>();
            for (int i = lo; i <= hi; i++)
                if (_continuousPanel.Children[i] is Border slot && slot.Child is Image img && img.Source is null)
                    need.Add(i);
            if (need.Count == 0) return;

            _continuousWindowCts?.Cancel();
            _continuousWindowCts = new CancellationTokenSource();
            var ct = _continuousWindowCts.Token;
            string currentFile = _currentFile;
            int renderW = Math.Max(800, Math.Min(2048, (int)(_continuousPageW * 2)));   // same budget as the base pass

            _ = System.Threading.Tasks.Task.Run(() =>
            {
                Docnet.Core.Readers.IDocReader? docReader = null;
                // #135 follow-up: one PdfPig open for the whole window-fill pass (see PigScope).
                using var pig = new PigScope();
                try
                {
                    foreach (int i in need)
                    {
                        if (ct.IsCancellationRequested) return;
                        docReader ??= DocLib.Instance.GetDocReader(currentFile, new PageDimensions(renderW, renderW * 2));
                        using var pr = docReader.GetPageReader(i);
                        int w = pr.GetPageWidth(), h = pr.GetPageHeight();
                        // #141: with the annotations the file carries (see PdfiumInterop).
                        // Form fields stay BAKED here, unlike the primary tile: TDPdf's live
                        // form overlays (RenderFormFields) exist only on _annotationCanvas, so
                        // this surface has nothing to draw the values with. Hiding the widgets
                        // would blank every filled field instead of un-ghosting it.
                        var raw = TDPdf.Services.PdfiumInterop.RenderPageWithAnnotations(currentFile, i, w, h)
                                  ?? pr.GetImage();
                        if (w <= 0 || h <= 0 || raw is null) continue;
                        int fi = i, fw = w, fh = h; byte[] bytes = raw;
                        FracRect[] keep = _docInvert ? ImageRectsFor(currentFile, i, pig) : [];
                        if (ct.IsCancellationRequested) return;
                        Dispatcher.Invoke(() =>
                        {
                            if (ct.IsCancellationRequested || _viewMode != ViewMode.Continuous) return;
                            ApplyContinuousBaseStable(fi, fw, fh, bytes, keep);
                        });
                    }
                }
                catch { /* cancelled or doc closed */ }
                finally { docReader?.Dispose(); }
            }, ct);
        }

        // Applies a base bitmap into a continuous slot WITHOUT changing the slot's height, so pages
        // below it never move (no scroll jump). Used only by window maintenance; the placeholder height
        // set at layout already matches the page aspect, so the natural-size bitmap fills the slot.
        private void ApplyContinuousBaseStable(int fi, int fw, int fh, byte[] bytes, FracRect[] keep)
        {
            if (fi < 0 || fi >= _continuousPanel.Children.Count) return;
            if (_continuousPanel.Children[fi] is not Border slot || slot.Child is not Image img) return;
            if (img.Source is not null) return;   // already rendered (or sharpened) — don't clobber
            double dipW = slot.Width;
            if (double.IsNaN(dipW) || dipW <= 0) return;
            double dipH = dipW * fh / fw;
            double dpiX = 96.0 * fw / dipW;
            double dpiY = 96.0 * fh / dipH;
            // #135: display-only invert, pictures carved back out (empty keep = full-page flip).
            if (_docInvert) InvertBgraInPlaceExcept(bytes, fw, fh, keep);
            var bmp = new WriteableBitmap(fw, fh, dpiX, dpiY, PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, fw, fh), bytes, fw * 4, 0);
            bmp.Freeze();
            img.Source = bmp;
            img.Width = dipW;
            img.Height = dipH;
            slot.Background = Brushes.White;
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

        // ── Current-page badge (upstream KillerPDF #197) ───────────────────────────────────────
        // One "page / total" chip in the viewport's bottom-right corner, replacing the per-tile
        // page tooltips (#151) that trailed the cursor and read as noise. It slides up on real
        // scrolling and on a page change, then slides back down once the view has been still for a
        // moment. Suppressed entirely for a one-page document, where it would only ever say
        // "1 / 1". The badge lives outside the page tiles and is IsHitTestVisible="False", so it
        // can never intercept a page click.
        private System.Windows.Threading.DispatcherTimer? _pageBadgeTimer;

        private const double PageBadgeHiddenY = 46;

        private void ShowPageBadge(int page)
        {
            if (_doc is null || _doc.PageCount < 2) return;
            if (page < 0 || page >= _doc.PageCount) return;
            _pageBadgeText.Text = $"{page + 1} / {_doc.PageCount}";
            _pageBadgeSlide.BeginAnimation(TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                        { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                });
            _pageBadge.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            if (_pageBadgeTimer is null)
            {
                _pageBadgeTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(900) };
                _pageBadgeTimer.Tick += (_, _) =>
                {
                    _pageBadgeTimer?.Stop();
                    _pageBadgeSlide.BeginAnimation(TranslateTransform.YProperty,
                        new System.Windows.Media.Animation.DoubleAnimation(
                            PageBadgeHiddenY, TimeSpan.FromMilliseconds(220))
                        {
                            EasingFunction = new System.Windows.Media.Animation.CubicEase
                                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
                        });
                    _pageBadge.BeginAnimation(OpacityProperty,
                        new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
                };
            }
            _pageBadgeTimer.Stop();
            _pageBadgeTimer.Start();
        }

        /// <summary>
        /// Drops the badge immediately, without the slide-out. Used when the document goes away:
        /// the idle timer would otherwise leave it hanging over the start screen for up to a
        /// second. Clearing the animations first is what lets the plain property assignments take
        /// effect — a running animation outranks a local value.
        /// </summary>
        private void HidePageBadgeNow()
        {
            _pageBadgeTimer?.Stop();
            _pageBadgeSlide.BeginAnimation(TranslateTransform.YProperty, null);
            _pageBadge.BeginAnimation(OpacityProperty, null);
            _pageBadgeSlide.Y = PageBadgeHiddenY;
            _pageBadge.Opacity = 0;
        }

        /// <summary>
        /// Tracks scroll position in continuous view: updates the page-number box and the sidebar
        /// thumbnail selection to whichever page is nearest the viewport center.
        /// </summary>
        private void PagePreviewPanel_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // #115: ScrollChanged BUBBLES. Every nested ScrollViewer under the preview panel raises
            // it here — multi-line form-field TextBoxes (VerticalScrollBarVisibility=Auto) and
            // ComboBoxes that RenderFormFields parents into AnnotationCanvas, the signature popup's
            // own scroller, the sidebar. Those fire during their layout, and the Continuous branch
            // below assigns PageList.SelectedIndex, whose handler removes selection chrome from
            // AnnotationCanvas — a synchronous child mutation inside a measure pass. It also meant
            // scrolling a form field could move the current page. Only the panel's own scrolling
            // counts.
            if (!ReferenceEquals(e.OriginalSource, PagePreviewPanel)) return;

            // Grid view (upstream v1.6.4): follow the tile nearest the viewport center so the
            // statusbar page counter tracks scrolling instead of pointing at the last-clicked page.
            // We update only the counter, NOT PageList.SelectedIndex — in Grid a selection change
            // scroll-jumps and re-renders (PageList_SelectionChanged), which would fight the scroll.
            // #197: a real vertical scroll surfaces the corner badge too. Grid's nearest-tile search
            // already runs here, so it reports the page rather than repeating the hunt.
            if (_viewMode == ViewMode.Grid) { UpdateGridCurrentPageCounter(e.VerticalChange != 0); return; }

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

            // #197: surface the position badge on real scrolling, whichever page ends up nearest.
            if (e.VerticalChange != 0) ShowPageBadge(nearest);

            if (PageList.SelectedIndex != nearest)
            {
                _pageJumpBox.Text = (nearest + 1).ToString();
                // Update the sidebar selection without re-scrolling the strip back.
                _suppressContinuousScrollSync = true;
                PageList.SelectedIndex = nearest;
                _suppressContinuousScrollSync = false;
            }
        }

        // Grid scroll tracking (upstream v1.6.4): sets the statusbar page counter to the tile whose
        // center is nearest the viewport center. Each tile carries its page index in its Tag (the
        // primary PageImage tagged in RenderPage, secondaries when appended). Uses TranslatePoint on
        // both tile edges so any grid zoom transform is accounted for. Deliberately leaves
        // PageList.SelectedIndex untouched (a Grid selection change scroll-jumps and re-renders).
        private void UpdateGridCurrentPageCounter(bool showBadge = false)
        {
            if (_doc is null || _pageContentPanel.Children.Count == 0) return;
            double viewportCenterY = PagePreviewPanel.ViewportHeight * 0.5;
            int nearestPage = -1;
            double minDist = double.MaxValue;
            foreach (UIElement child in _pageContentPanel.Children)
            {
                if (child is not FrameworkElement fe || fe.Tag is not int pageIdx || fe.ActualHeight <= 0)
                    continue;
                try
                {
                    double topY    = fe.TranslatePoint(new Point(0, 0), PagePreviewPanel).Y;
                    double bottomY = fe.TranslatePoint(new Point(0, fe.ActualHeight), PagePreviewPanel).Y;
                    double dist = Math.Abs((topY + bottomY) * 0.5 - viewportCenterY);
                    if (dist < minDist) { minDist = dist; nearestPage = pageIdx; }
                }
                catch { /* transform can fail mid-layout; skip this tile */ }
            }
            if (nearestPage >= 0)
            {
                _pageJumpBox.Text = (nearestPage + 1).ToString();
                if (showBadge) ShowPageBadge(nearestPage);   // #197
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

        // Schemes we will hand to the OS shell when a PDF link is clicked. A PDF can embed ANY URI, and
        // Process.Start(UseShellExecute = true) would happily launch file:// paths, UNC shares, javascript:,
        // or registered protocol handlers (ms-msdt:/search-ms: — real malware vectors). Anything outside
        // this allow-list is refused. http/https = web links; mailto = email links.
        private static readonly HashSet<string> AllowedLinkSchemes =
            new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

        // True only for an absolute URI in an allowed scheme. Rejects scheme-less / relative URIs (a bare
        // "www.example.com" is normalised first, below), plus file:, javascript:, and custom protocols.
        private static bool IsAllowedLinkUri(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && AllowedLinkSchemes.Contains(uri.Scheme);

        // A PDF can store a scheme-less link like "www.example.com" or "example.com/page". Treat a domain-
        // shaped target as https so it still opens; anything with an explicit scheme, a backslash (UNC/path),
        // or whitespace is left untouched (and thus refused by IsAllowedLinkUri unless it's http/https/mailto).
        private static string NormalizeLinkUri(string raw)
        {
            raw = raw.Trim();
            if (raw.Length == 0) return raw;
            if (raw.Contains('\\') || raw.Contains(' ')) return raw;    // Windows path / UNC / junk — don't touch
            if (raw.Contains("://")) return raw;                        // already scheme://...
            int colon = raw.IndexOf(':');
            int slash = raw.IndexOf('/');
            if (colon >= 0 && (slash < 0 || colon < slash)) return raw; // "scheme:" (mailto:, file:, C:) — don't touch
            string host = slash >= 0 ? raw[..slash] : raw;              // host part before any path
            return host.Contains('.') ? "https://" + raw : raw;         // dotted host => assume https
        }

        // Confirms before opening an external link in the browser, unless the user opted out via the
        // "Don't ask again" checkbox (persisted in SkipLinkConfirm). Returns true to proceed. Internal
        // go-to-page links never call this.
        private bool ConfirmOpenLink(string url)
        {
            if (TDPdf.Properties.Settings.Default.SkipLinkConfirm) return true;
            var (result, dontAsk) = TdpDialog.ShowWithCheckbox(
                this,
                $"Open this link outside TDPdf?\n\n{url}",
                "Don't ask again",
                "Open Link",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.OK) return false;
            if (dontAsk)
            {
                TDPdf.Properties.Settings.Default.SkipLinkConfirm = true;
                TDPdf.Properties.Settings.Default.Save();
            }
            return true;
        }

        /// <summary>
        /// Follows a resolved link target: an int page index navigates within the document; a string URI is
        /// scheme-checked, confirmed, then opened via the shell. Single choke point for both the single-page
        /// (<see cref="_linkOverlays"/>) and grid-tile click paths, so the safety checks can't be bypassed by
        /// one route and a failed open is always reported instead of silent.
        /// </summary>
        private void FollowLinkTarget(object? target)
        {
            if (target is int pageIndex)
            {
                if (_doc != null && pageIndex >= 0 && pageIndex < _doc.PageCount)
                {
                    RecordNavJump();   // internal link jump — retraceable via Alt+Left
                    PageList.SelectedIndex = pageIndex;
                }
                return;
            }

            if (target is not string raw || string.IsNullOrWhiteSpace(raw)) return;

            // Scheme-less but domain-shaped targets (e.g. "www.example.com") become https:// here.
            string url = NormalizeLinkUri(raw);
            if (!IsAllowedLinkUri(url))
            {
                SetStatus($"Blocked an unsafe link: {raw}");
                return;
            }

            if (!ConfirmOpenLink(url)) return;

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Open link failed: {ex}");
                SetStatus("Could not open the link.");
            }
        }

        // Status-bar hover feedback: shows the hovered link's target, restoring the prior status on exit.
        private string? _preHoverStatus;
        private void ShowLinkHoverStatus(string? target)
        {
            if (target != null)
            {
                _preHoverStatus ??= StatusText.Text;
                StatusText.Text = target;
            }
            else if (_preHoverStatus != null)
            {
                StatusText.Text = _preHoverStatus;
                _preHoverStatus = null;
            }
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

                // Same page-box resolution as the form-field overlays: the box PDFium actually
                // rasterized (CropBox over MediaBox, inherited through the page tree, origin honoured).
                // Never pdfPage.Width/Height — those read through the create-on-read MediaBox getter,
                // which both PLANTS a degenerate /MediaBox [0 0 0 0] into the page dictionary (Adobe
                // then rejects the saved page as "dimensions out-of-range") and returns 0 for a page
                // whose box is only inherited, which used to fall back to a hardcoded A4 size and
                // misplace every link on non-A4 documents.
                var box = GetVisiblePageBox(pdfPage);
                int rotation = ((pdfPage.Rotate % 360) + 360) % 360;

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

                    // The bitmap already has the page /Rotate applied; link /Rect coords do not.
                    var (cx, cy, cw, ch) = PdfRectToCanvas(box, rotation, bitmapW, bitmapH, rx1, ry1, rx2, ry2);
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
                        Clipboard.SetText(uriTag["mailto:".Length..]), null, null, "\uE715"));
                else if (lnk.Tag is string httpTag)
                    cm.Items.Add(MakeMenuItem("Copy URL", (_, _) => Clipboard.SetText(httpTag), null, null, "\uE71B"));
                cm.Items.Add(MakeMenuItem("Remove Link from PDF", (_, _) =>
                    RemoveLinkAnnotation(info.PageIndex, info.AnnotIndex), null, null, "\uE74D"));
                overlay.ContextMenu = cm;

                // #156 pushed the form-field overlays to ZIndex -1 so annotations paint above them.
                // Links are added to this same canvas BEFORE the fields on the initial page render, so
                // leaving them at the default 0 would flip the two: a widget overlapped by a /Link
                // annotation would stop receiving its own clicks. -2 keeps the field on top of the link
                // exactly as before. Link hit detection is a manual bounds check in
                // Canvas_MouseLeftButtonDown, so nothing here depends on the overlay being topmost.
                Panel.SetZIndex(overlay, -2);
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
                    FollowLinkTarget(capturedTag);
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
            List<string> Options,
            // Upstream KillerPDF #158: a comb field is /Tx with the Comb flag (/Ff bit 25) AND a
            // /MaxLen — the printed row of equal-width boxes forms are so fond of. GetPageFormFields
            // only ever sets IsComb together with MaxLen > 0 (and never with IsMultiLine, which the
            // spec makes mutually exclusive), so anything downstream may divide by MaxLen whenever
            // IsComb is true. MaxLen is also the typing cap.
            bool   IsComb,
            int    MaxLen,
            // A /Btn with the Pushbutton flag (/Ff bit 17) holds no value and must never get a
            // fill-in control. Without this it fell through to the text-field branch and a form's
            // Submit / Print / Reset button became an editable box that wrote a /V on save.
            bool   IsPushButton = false,
            // /Opt entries may be [export, display] pairs: the list shows the display string but
            // /V must carry the EXPORT value. Options holds what the user sees, OptionExports the
            // value at the same index that gets written back. For a plain string entry the two are
            // identical, which is why every existing single-string form still behaves the same.
            List<string>? OptionExports = null,
            // #242: true when the field's /AA additional-action JavaScript formats it as a number
            // (Acrobat and LiveCycle both write AFNumber_*). Form-aware OCR uses it to restrict
            // recognition to digits and separators, where O/l/S are the usual misreads.
            bool   IsNumeric = false);

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

                // -- Push button ---------------------------------------------------
                // Holds no value, so there is nothing to fill in and no overlay to draw. Left to
                // the rendered page, where its own appearance stream already shows the button.
                // This must come FIRST: a pushbutton is neither checkbox nor radio nor /Ch, so it
                // otherwise satisfied the text-field test below and became an editable box.
                if (f.IsPushButton) continue;

                // -- Text field ----------------------------------------------------
                if (!f.IsCheckBox && !f.IsRadio && f.FieldType != "/Ch")
                {
                    string cur = _formTextValues.TryGetValue(f.ObjNum, out var tv) ? tv : f.CurrentValue;
                    // Use the shorter canvas dimension as the font size reference so that
                    // rotated fields (where Cw and Ch are swapped vs. portrait) don't blow up.
                    double fieldShort = Math.Min(f.Cw, f.Ch);
                    double fontSize = f.IsMultiLine ? fieldShort * 0.18 : fieldShort * 0.65;
                    fontSize = Math.Max(10, fontSize);
                    // #158: a comb field types one character per printed cell. WPF has no comb
                    // TextBox, so the overlay approximates the cell walk: a monospace face (Consolas'
                    // advance is ~0.55em) sized so one advance is at most one cell wide, capped at
                    // MaxLen characters, and left-padded by half a cell minus half a glyph so the
                    // first character lands in the middle of cell 0 rather than against its left
                    // wall. It is an approximation on screen only — the SAVED appearance stream
                    // below places each glyph exactly at its cell centre. IsComb guarantees
                    // MaxLen > 0 (see FormFieldInfo), so the division is safe.
                    double combCellW = f.IsComb ? f.Cw / f.MaxLen : 0;
                    if (f.IsComb) fontSize = Math.Max(9, Math.Min(fontSize, combCellW / 0.55));
                    var tb = new TextBox
                    {
                        Tag             = FormOverlayTag,
                        Width           = f.Cw,
                        Height          = f.Ch,
                        Text            = cur,
                        MaxLength       = f.IsComb ? f.MaxLen : 0,   // 0 = unlimited (WPF default)
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
                        Padding         = f.IsComb
                            ? new Thickness(Math.Max(0, combCellW / 2 - fontSize * 0.275), 0, 0, 0)
                            : new Thickness(3, 0, 3, 0),
                        VerticalContentAlignment = f.IsMultiLine ? VerticalAlignment.Top : VerticalAlignment.Center,
                        ToolTip         = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    if (f.IsComb) tb.FontFamily = new FontFamily("Consolas");
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

                    // The list shows display strings; /V carries export values. Select by INDEX so
                    // the two never have to be equal: match the stored/current value against the
                    // exports first, then fall back to the display text for files whose /V already
                    // holds the label (and for plain-string /Opt, where the two are the same).
                    var exports = f.OptionExports ?? f.Options;
                    int selIdx = exports.IndexOf(cur);
                    if (selIdx < 0) selIdx = f.Options.IndexOf(cur);
                    combo.SelectedIndex = selIdx;   // -1 when /V matches nothing: leave it unset

                    int capturedKey = f.ObjNum;
                    combo.SelectionChanged += (_, _) =>
                    {
                        int i = combo.SelectedIndex;
                        if (i < 0) return;
                        // Write the export value, which is what other viewers read back.
                        _formTextValues[capturedKey] = i < exports.Count ? exports[i] : f.Options[i];
                        MarkDirty();
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
                // Upstream v1.7.0 (#156): field overlays sit BELOW the annotation layer. TDPdf puts
                // annotations and form overlays on the SAME canvas, and RenderAllAnnotations paints
                // the annotations and then calls RestoreFormOverlays — a Canvas paints later children
                // on top, so a signature dropped on a fill-in field disappeared behind the field's own
                // near-opaque control. Ordering cannot fix it here (this method removes and re-adds
                // every stale overlay on each call, so the fields always end up last), but ZIndex can:
                // annotations render at the default 0, so -1 puts the fields under them without
                // touching a single annotation path. Clicking a covered field still works — every
                // annotation visual is IsHitTestVisible=false, so none of them swallows the click that
                // reaches the field beneath, and the selection chrome (borders, resize handles) stays
                // at 0 and therefore still outranks the fields.
                Panel.SetZIndex(ctrl, -1);
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
            // Resolve the box the overlay's bitmap was actually rendered from: PDFium rasterizes the
            // CropBox (falling back to the MediaBox), so field /Rect coordinates must be mapped
            // relative to THAT box's own lower-left origin and size. GetVisiblePageBox also walks the
            // page tree for an inherited box and never touches the create-on-read page.MediaBox /
            // page.CropBox / page.Width getters — reading those returned an empty rectangle for an
            // inherited box, which used to drop the page onto a hardcoded A4 size and shift every
            // field overlay (worst near the top of the page) on US Letter and other non-A4 documents.
            //
            // The box is deliberately NOT rotated here: field /Rect coords live in unrotated user
            // space, and PdfRectToCanvas maps them onto the already-rotated bitmap. (That is also why
            // page.Width/Height are unusable — PdfSharpCore swaps them for 90/270 pages.)
            var box = GetVisiblePageBox(page);
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
                    // Map the widget rect onto the Docnet/PDFium bitmap the canvas mirrors — the same
                    // conversion the link overlays use.
                    var (cx, cy, cw, ch) = PdfRectToCanvas(box, rotation, canvasW, canvasH, rx1, ry1, rx2, ry2);
                    // Upstream v1.7.1 (#181): a malformed widget rectangle must not reach a WPF Width
                    // or Height property — WPF throws for NaN and infinity, which took the viewer down
                    // when a page click rebuilt the form overlay. "cw < 2" is no filter for those:
                    // "∞ < 2" is false and every comparison with NaN is false, so both used to sail
                    // straight through into the TextBox / ComboBox / checkbox / radio sizes below.
                    if (!IsFinite(cx) || !IsFinite(cy) ||
                        !IsFinitePositive(cw) || !IsFinitePositive(ch) ||
                        cw < 2 || ch < 2) continue;

                    // Walk the parent chain to resolve inherited attributes.
                    string ft = "", name = "", curVal = "";
                    int flags = 0;
                    int maxLen = 0;   // #158: /MaxLen, the comb cell count
                    var options = new List<string>();
                    var optionExports = new List<string>();

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
                        // #158: /MaxLen is inheritable exactly like /Ff, so it gets the same
                        // parent-chain walk — a comb field very often carries its /Ff and /MaxLen on
                        // the parent field node and only the /Rect on the widget.
                        if (maxLen == 0 && node.Elements["/MaxLen"] is PdfInteger ml)
                            maxLen = ml.Value;
                        if (options.Count == 0 && node.Elements.GetArray("/Opt") is PdfArray optArr)
                        {
                            for (int j = 0; j < optArr.Elements.Count; j++)
                            {
                                var o = optArr.Elements[j];
                                // A pair is [export, display]: show the second, save the first.
                                // A bare string is both at once.
                                if (o is PdfString ps2) { options.Add(ps2.Value); optionExports.Add(ps2.Value); }
                                else if (o is PdfArray pa2 && pa2.Elements.Count >= 2)
                                {
                                    string export  = (pa2.Elements[0] as PdfString)?.Value ?? "";
                                    string display = (pa2.Elements[1] as PdfString)?.Value ?? "";
                                    options.Add(display);
                                    optionExports.Add(export);
                                }
                            }
                        }

                        var parentItem = node.Elements["/Parent"];
                        if (parentItem is null) break;
                        node = parentItem as PdfDictionary ?? DerefItem(parentItem) as PdfDictionary;
                    }

                    if (string.IsNullOrEmpty(ft)) ft = "/Tx";

                    bool isReadOnly  = (flags & 1) != 0;
                    bool isMultiLine = ft.Contains("Tx") && (flags & 4096) != 0;
                    // #158: Comb is /Ff bit 25 (1 << 24) and only means anything on a /Tx field that
                    // also declares how many cells it has. The spec makes comb and multiline mutually
                    // exclusive, so a field that (wrongly) sets both stays on the ordinary multiline
                    // path. Requiring maxLen > 0 here is what lets every consumer divide by MaxLen.
                    bool isComb      = ft.Contains("Tx") && (flags & (1 << 24)) != 0
                                       && maxLen > 0 && !isMultiLine;
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

                    // #242: the field's format action, walked up the parent chain like /Ff and
                    // /MaxLen because /AA is inherited the same way. Read only as a signal — the
                    // JavaScript itself is never executed.
                    bool isNumeric = false;
                    try
                    {
                        node = ann;
                        while (node is not null && !isNumeric)
                        {
                            var aaDict = node.Elements.GetDictionary("/AA");
                            var fmtDict = aaDict?.Elements.GetDictionary("/F");
                            if (fmtDict?.Elements["/JS"] is PdfString jsStr)
                                isNumeric = FormOcrPolicy.LooksNumeric(jsStr.Value);
                            var api = node.Elements["/Parent"];
                            if (api is null) break;
                            node = api as PdfDictionary ?? DerefItem(api) as PdfDictionary;
                        }
                    }
                    catch { /* a malformed /AA must never break field parsing */ }

                    int objNum = GetObjectNumber(elem);
                    if (objNum < 0)
                        objNum = -(pageIndex * 10000 + i); // synthetic key for inline dicts

                    result.Add(new FormFieldInfo(objNum, ft, isCheckBox, isRadio, isMultiLine,
                        name, curVal, onValue, isReadOnly, cx, cy, cw, ch, options,
                        isComb, maxLen, isPushBtn, optionExports, isNumeric));
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

                        // Upstream v1.7.1 (#180): /Ff is inheritable exactly like /DA, and bit 13
                        // (4096) is Multiline. The overlay already decoded it (GetPageFormFields),
                        // but the appearance writer never saw it: a multiline field has to lay its
                        // value out in lines from the top of the box, a single-line one draws one
                        // vertically centred line.
                        //
                        // #158: /MaxLen rides along on the same walk — it is inheritable in exactly
                        // the same way, and a comb field's appearance needs both it and bit 25.
                        // The loop now stops only once BOTH have been found (or the chain runs out);
                        // the per-value `== 0` guards mean the extra iterations can never change
                        // which /Ff wins, so non-comb fields resolve identically to before.
                        int fieldFlags = 0;
                        int combLen = 0;
                        node = ann;
                        while (node is not null && (fieldFlags == 0 || combLen == 0))
                        {
                            if (fieldFlags == 0 && node.Elements["/Ff"] is PdfInteger fi) fieldFlags = fi.Value;
                            if (combLen == 0 && node.Elements["/MaxLen"] is PdfInteger ml) combLen = ml.Value;
                            var pi = node.Elements["/Parent"];
                            if (pi is null) break;
                            node = pi as PdfDictionary ?? DerefItem(pi) as PdfDictionary;
                        }
                        // Multiline is a /Tx-only flag — a /Ch choice field uses that bit position
                        // for nothing — so gate on the field type the way GetPageFormFields does,
                        // including its "missing /FT means /Tx" default.
                        string ffType = fieldDict?.Elements["/FT"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(ffType)) ffType = "/Tx";
                        bool isMultiLine = ffType.Contains("Tx") && (fieldFlags & 4096) != 0;
                        // #158: same gating as GetPageFormFields — /Tx only, needs a positive
                        // /MaxLen, and never together with multiline.
                        bool isComb = ffType.Contains("Tx") && (fieldFlags & (1 << 24)) != 0
                                      && combLen > 0 && !isMultiLine;

                        if (_formTextValues.TryGetValue(objNum, out var textVal) && fieldDict is not null)
                        {
                            fieldDict.Elements["/V"] = new PdfString(textVal);
                            GenerateTextFieldAppearance(ann, textVal, daStr, fieldW, fieldH, isMultiLine,
                                isComb ? combLen : 0);
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
        /// <param name="combLen">
        /// Upstream KillerPDF #158. Cell count of a comb field (/Ff bit 25 with a /MaxLen), which
        /// draws one character per evenly-spaced cell instead of one continuous run. 0 — the default
        /// every existing caller keeps — means "not a comb field" and leaves the ordinary
        /// single-line / multiline path below completely untouched.
        /// </param>
        private void GenerateTextFieldAppearance(PdfDictionary widgetAnn, string text, string? da, double fieldW, double fieldH, bool isMultiLine,
            int combLen = 0)
        {
            try
            {
                const double pad = 2;   // left/right inset, matching the Td origin below

                // #140: the shared path below writes the value as a WinAnsi literal against the
                // field's /DA base font, which is NOT embedded — so anything WinAnsi cannot express
                // was folded to '?' and saved that way. /V kept the real text, so the value looked
                // correct in TDPdf and was wrong in every other viewer, in print, and in flatten.
                // A value that needs more than WinAnsi is drawn instead through PdfSharpCore, which
                // embeds a covering font. Everything representable keeps the byte-identical output
                // it has always produced, so comb (#158) and multiline (#180) are untouched.
                if (NeedsEmbeddedFont(text)
                    && TryGenerateUnicodeFieldAppearance(widgetAnn, text, da, fieldW, fieldH,
                                                        isMultiLine, combLen, pad))
                    return;

                var (fontName, fontSize) = ParseDaString(da);
                if (fontSize <= 0) fontSize = Math.Max(6, Math.Min(fieldH * 0.65, 12));
                // The "no taller than 85% of the box" clamp is a single-line rule: a multiline
                // field is as tall as it needs to be for several lines, so applying it there
                // blows the text up to the height of the whole box.
                fontSize = isMultiLine ? Math.Max(6, fontSize)
                                       : Math.Max(6, Math.Min(fontSize, fieldH * 0.85));

                // #158: comb — one character per evenly-spaced cell, the way Acrobat fills the
                // printed boxes. Each glyph gets its OWN text matrix placing it at its cell's
                // centre: Tm sets the absolute position, so no leading/advance accumulates between
                // them and the run cannot drift out of the cells. The horizontal offset backs off
                // half a glyph from the cell centre (Helvetica-class average advance is ~0.55em, so
                // half is ~0.275em), and the width cap keeps a wide glyph from spilling into its
                // neighbour. Spaces are skipped: they paint nothing and would only cost operators.
                // The shared single-run path below cannot express this — it would bunch the whole
                // value into the left of cell 0. Note this returns before that path, and every
                // non-comb caller passes combLen == 0, so nothing here can affect them.
                if (combLen > 0)
                {
                    double cellW = fieldW / combLen;
                    fontSize = Math.Max(6, Math.Min(fontSize, Math.Min(fieldH * 0.85, cellW * 1.4)));
                    string oneLine = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
                    if (oneLine.Length > combLen) oneLine = oneLine[..combLen];
                    double combY = (fieldH - fontSize) / 2 + fontSize * 0.2;
                    if (combY < 1) combY = 1;

                    // Invariant, like every number written into a content stream below: string
                    // interpolation formats with the OS culture, and the comma decimal separator of
                    // de-DE and most European locales is not a valid PDF number token — the whole
                    // appearance stream then fails to execute in a strict viewer (upstream v1.7.4).
                    var csb = new System.Text.StringBuilder();
                    csb.Append(FormattableString.Invariant($"/Tx BMC\nq\n0 0 {fieldW:F2} {fieldH:F2} re W n\n"));
                    csb.Append(FormattableString.Invariant($"BT\n{fontName} {fontSize:F2} Tf\n0 g\n"));
                    for (int i = 0; i < oneLine.Length; i++)
                    {
                        if (oneLine[i] == ' ') continue;
                        double gx = i * cellW + cellW / 2 - fontSize * 0.275;
                        // Same EscapePdfString the run path uses, so WinAnsi folding and (, ), \
                        // escaping are identical for a comb cell and an ordinary field.
                        csb.Append(FormattableString.Invariant(
                            $"1 0 0 1 {gx:F2} {combY:F2} Tm\n({EscapePdfString(oneLine[i].ToString())}) Tj\n"));
                    }
                    csb.Append("ET\nQ\nEMC");

                    var combXobj = BuildFormXObject(fontName, fieldW, fieldH, csb.ToString());
                    if (combXobj is null) return;
                    AttachAppearance(widgetAnn, combXobj);
                    return;
                }

                // Tj shows a string; it has no concept of a line break, so a value with newlines
                // in it drew as one run with the breaks swallowed (they survived into the literal
                // as \r / \n escapes and painted nothing). Lay the value out into lines and show
                // each one, moving down by the leading between them.
                List<string> lines = isMultiLine
                    ? WrapFieldText(text, Math.Max(1, fieldW - pad * 2), fontSize)
                    : new List<string> { text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ') };

                double leading = fontSize * 1.16;
                // PDF baselines are measured from the bottom of the field rect. Multiline text
                // starts at the top and runs down; a single line stays vertically centred.
                double textY = isMultiLine ? fieldH - fontSize
                                           : (fieldH - fontSize) / 2 + fontSize * 0.2;
                if (textY < 1) textY = 1;

                // Invariant: a comma decimal from the OS culture is not a valid PDF number token.
                var sb = new System.Text.StringBuilder();
                sb.Append(FormattableString.Invariant($"/Tx BMC\nq\n0 0 {fieldW:F2} {fieldH:F2} re W n\n"));
                sb.Append(FormattableString.Invariant(
                    $"BT\n{fontName} {fontSize:F2} Tf\n0 g\n{leading:F2} TL\n{pad:F2} {textY:F2} Td\n"));
                for (int i = 0; i < lines.Count; i++)
                {
                    if (i > 0) sb.Append("T*\n");   // down one leading, back to the left inset
                    sb.Append($"({EscapePdfString(lines[i])}) Tj\n");
                }
                sb.Append("ET\nQ\nEMC");

                // Lines past the bottom of the box are clipped by the "re W n" above, the same
                // way a viewer clips an over-full field.
                var xobj = BuildFormXObject(fontName, fieldW, fieldH, sb.ToString());
                if (xobj is null) return;
                AttachAppearance(widgetAnn, xobj);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GenerateTextFieldAppearance: {ex}"); }
        }

        /// <summary>
        /// Splits a multiline field's value into the lines its appearance should draw: the value's
        /// own line breaks first, then greedy word-wrap to the field's inner width.
        /// </summary>
        /// <remarks>
        /// Measured with Arial, which is metric-compatible with the Helvetica the generated
        /// appearance stream asks for, so the wrap lands where the drawn glyphs do.
        /// </remarks>
        private static List<string> WrapFieldText(string text, double innerWidth, double fontSize)
        {
            var typeface = new Typeface("Arial");
            double Width(string s) => new FormattedText(
                s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, fontSize, Brushes.Black, 1.0).Width;

            var lines = new List<string>();
            foreach (var para in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string current = string.Empty;
                foreach (var word in para.Split(' '))
                {
                    string candidate = current.Length == 0 ? word : current + " " + word;
                    // A single word wider than the field can't be broken any further — let it
                    // run on and be clipped rather than dropping it onto an empty line.
                    if (current.Length > 0 && Width(candidate) > innerWidth)
                    {
                        lines.Add(current);
                        current = word;
                    }
                    else current = candidate;
                }
                lines.Add(current);
            }
            return lines;
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

                // Invariant: a comma decimal from the OS culture is not a valid PDF number token.
                string checkedContent = FormattableString.Invariant(
                    $"q\nBT\n/ZaDb {fs:F2} Tf\n0 g\n{tx:F2} {ty:F2} Td\n(4) Tj\nET\nQ");
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
        /// <summary>
        /// True when <paramref name="text"/> contains a character the WinAnsi appearance path
        /// cannot represent, and would therefore write as '?'. Deliberately mirrors
        /// <see cref="EscapePdfString"/>'s decision exactly — anything below U+0100 passes straight
        /// through, and above that only what <c>WinAnsiHighMap</c> folds — so the two can never
        /// disagree about which values are safe for the literal path.
        /// </summary>
        private static bool NeedsEmbeddedFont(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
                if (c >= 256 && !WinAnsiHighMap.ContainsKey(c)) return true;
            return false;
        }

        /// <summary>
        /// Draws a form field's /AP /N appearance through PdfSharpCore so the value is set in a real
        /// EMBEDDED font (Type0 / Identity-H with a subset and a /ToUnicode map) instead of a WinAnsi
        /// literal. Used only for values the literal path would mangle (#140).
        /// </summary>
        /// <returns>
        /// False when no covering font could be resolved, which lets the caller fall through to the
        /// legacy path — a '?' appearance is poor, but it beats a field with no appearance at all.
        /// </returns>
        private bool TryGenerateUnicodeFieldAppearance(PdfDictionary widgetAnn, string text, string? da,
            double fieldW, double fieldH, bool isMultiLine, int combLen, double pad)
        {
            if (_doc is null || fieldW <= 0 || fieldH <= 0) return false;
            try
            {
                var (_, fontSize) = ParseDaString(da);
                if (fontSize <= 0) fontSize = Math.Max(6, Math.Min(fieldH * 0.65, 12));
                fontSize = isMultiLine ? Math.Max(6, fontSize)
                                       : Math.Max(6, Math.Min(fontSize, fieldH * 0.85));

                // Same family choice the annotation burn-in makes, so a value rendered here and the
                // same text placed as an annotation resolve to one face rather than two.
                var font = TdpFontResolver.TryCreate(
                    FontCoverage.PickFamily(PdfFontStyle.DefaultFamily, text), fontSize, XFontStyle.Regular);
                if (font is null) return false;

                var form = new XForm(_doc, new XSize(fieldW, fieldH));
                using (var gfx = XGraphics.FromForm(form))
                {
                    // XGraphics is top-down where the hand-written stream is bottom-up, so the
                    // layout is expressed as rectangles plus an alignment rather than baselines.
                    // The clip matches the legacy path's "re W n": an over-full value is cut off at
                    // the field edge exactly as a viewer would cut it.
                    gfx.IntersectClip(new XRect(0, 0, fieldW, fieldH));

                    if (combLen > 0)
                    {
                        // #158: one character per printed cell. Centre each in its own cell so the
                        // run cannot drift, matching the per-glyph Tm the legacy comb path uses.
                        double cellW = fieldW / combLen;
                        fontSize = Math.Max(6, Math.Min(fontSize, Math.Min(fieldH * 0.85, cellW * 1.4)));
                        var combFont = TdpFontResolver.TryCreate(
                            FontCoverage.PickFamily(PdfFontStyle.DefaultFamily, text), fontSize, XFontStyle.Regular);
                        if (combFont is null) return false;

                        string oneLine = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
                        if (oneLine.Length > combLen) oneLine = oneLine[..combLen];
                        for (int i = 0; i < oneLine.Length; i++)
                        {
                            if (oneLine[i] == ' ') continue;
                            gfx.DrawString(oneLine[i].ToString(), combFont, XBrushes.Black,
                                new XRect(i * cellW, 0, cellW, fieldH), XStringFormats.Center);
                        }
                    }
                    else if (isMultiLine)
                    {
                        // Wrapping and the top-down start are XTextFormatter's job here; the legacy
                        // path does the same with WrapFieldText plus TL/T*.
                        new PdfSharpCore.Drawing.Layout.XTextFormatter(gfx).DrawString(text, font, XBrushes.Black,
                            new XRect(pad, 0, Math.Max(1, fieldW - pad * 2), fieldH));
                    }
                    else
                    {
                        gfx.DrawString(text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' '),
                            font, XBrushes.Black,
                            new XRect(pad, 0, Math.Max(1, fieldW - pad * 2), fieldH),
                            XStringFormats.CenterLeft);
                    }
                }

                // Read the XObject only after the XGraphics is disposed: the content stream is
                // finalized on dispose, and an XForm must not be touched once it has been drawn.
                AttachAppearance(widgetAnn, form.PdfForm);
                return true;
            }
            catch (Exception ex)
            {
                // Never let this fail the save — fall back to the legacy literal path.
                System.Diagnostics.Debug.WriteLine($"TryGenerateUnicodeFieldAppearance: {ex}");
                return false;
            }
        }

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

            // Upstream v1.7.1 (#180): CreateStream, not a hand-attached PdfStream. It is the only
            // path that also writes /Length, which every PDF stream is required to carry. The old
            // reflection helper built PdfDictionary.PdfStream directly and assigned it through the
            // Stream property, which skips that one line — so every /AP /N appearance TDPdf
            // generated for a text field or checkbox went out with no /Length and the saved file
            // was structurally invalid. PdfSharpCore's own parser refuses such a stream ("Cannot
            // retrieve stream length."), and strict viewers report a damaged structure; PDFium-based
            // viewers scan on to endstream and cope, which is why it went unnoticed on screen. The
            // Debug.Assert in PdfDictionary.WriteObject that would have caught it is compiled out of
            // Release builds.
            xobj.CreateStream(bytes);

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

        // Upstream v1.7.1 (#180): the generated appearance streams declare /WinAnsiEncoding, but the
        // escape that fed them replaced every character above U+00FF with '?'. WinAnsi is code page
        // 1252, whose 0x80-0x9F block holds exactly the characters that were being thrown away —
        // curly quotes and apostrophes, en/em dashes, bullets, ellipses — so a field pasted in from
        // a word processor came out full of question marks ("Hunter?s Mark").
        //
        // Upstream calls Encoding.GetEncoding(1252). We deliberately do NOT: on .NET (Core) code
        // page 1252 is not built in, and GetEncoding(1252) throws ArgumentException unless
        // CodePagesEncodingProvider is registered — which this app does not do anywhere (see the
        // remark on PdfDocumentService.FileHasEncryption, which avoids 1252 for the same reason).
        // In a static initializer that throw would surface as a TypeInitializationException on the
        // save path. This table is the complete set of CP1252 code points above U+00FF, so it is
        // equivalent for every character WinAnsi can actually represent, needs no provider
        // registration, and cannot throw. Below U+0100 CP1252 and Latin-1 agree, so those pass
        // through untouched exactly as before; anything with no WinAnsi slot (CJK and the like)
        // still falls back to '?', the same as the old behaviour.
        private static readonly Dictionary<char, char> WinAnsiHighMap = new()
        {
            ['\u20AC'] = '\u0080',   // euro sign
            ['\u201A'] = '\u0082',   // single low-9 quotation mark
            ['\u0192'] = '\u0083',   // latin small letter f with hook
            ['\u201E'] = '\u0084',   // double low-9 quotation mark
            ['\u2026'] = '\u0085',   // horizontal ellipsis
            ['\u2020'] = '\u0086',   // dagger
            ['\u2021'] = '\u0087',   // double dagger
            ['\u02C6'] = '\u0088',   // modifier letter circumflex accent
            ['\u2030'] = '\u0089',   // per mille sign
            ['\u0160'] = '\u008A',   // capital S with caron
            ['\u2039'] = '\u008B',   // single left-pointing angle quotation mark
            ['\u0152'] = '\u008C',   // capital ligature OE
            ['\u017D'] = '\u008E',   // capital Z with caron
            ['\u2018'] = '\u0091',   // left single quotation mark
            ['\u2019'] = '\u0092',   // right single quotation mark (the curly apostrophe)
            ['\u201C'] = '\u0093',   // left double quotation mark
            ['\u201D'] = '\u0094',   // right double quotation mark
            ['\u2022'] = '\u0095',   // bullet
            ['\u2013'] = '\u0096',   // en dash
            ['\u2014'] = '\u0097',   // em dash
            ['\u02DC'] = '\u0098',   // small tilde
            ['\u2122'] = '\u0099',   // trade mark sign
            ['\u0161'] = '\u009A',   // small s with caron
            ['\u203A'] = '\u009B',   // single right-pointing angle quotation mark
            ['\u0153'] = '\u009C',   // small ligature oe
            ['\u017E'] = '\u009E',   // small z with caron
            ['\u0178'] = '\u009F',   // capital Y with diaeresis
            // No WinAnsi slot of their own, but these are the folds the platform's own best-fit
            // table applies, and word processors emit them constantly.
            ['\u2010'] = '-',        // hyphen
            ['\u2011'] = '-',        // non-breaking hyphen
            ['\u2012'] = '-',        // figure dash
            ['\u2015'] = '\u0097',   // horizontal bar -> em dash
            ['\u2032'] = '\'',       // prime -> apostrophe
            ['\u2033'] = '"',        // double prime -> quotation mark
        };

        /// <summary>Escapes a string for use in a PDF literal string (parentheses syntax).</summary>
        private static string EscapePdfString(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char raw in s)
            {
                // Fold to the single WinAnsi byte the appearance stream's /WinAnsiEncoding will read
                // BEFORE escaping, so a mapped character that happens to need escaping still gets it.
                char c = raw < 256 ? raw
                       : WinAnsiHighMap.TryGetValue(raw, out var mapped) ? mapped
                       : '?';
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '(':  sb.Append("\\(");  break;
                    case ')':  sb.Append("\\)");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\n': sb.Append("\\n");  break;
                    default:
                        sb.Append(c);
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
        // Document outline / bookmarks (sidebar OUTLINES tab)  —  #133
        //
        // Editable bookmark tree ported from upstream KillerPDF v1.6.4. TDPdf keeps its own
        // multi-tab document model, TdpDialog dark dialogs, theme brushes, and (crucially) its
        // full-document-snapshot undo (PushDocUndo), so this is an adaptation, not a copy:
        //   • ListBox → themed TreeView (OutlineTreeStyle / OutlineItemStyle / OutlineExpander).
        //   • Edits mutate the live PdfSharpCore outline object model (_doc.Outlines) so they are
        //     written on save; page-index for display still resolves through TDPdf's ResolveDest so
        //     named / GoTo destinations navigate exactly as before.
        //   • Every mutation rides the existing document-snapshot undo (PushDocUndo) — one Ctrl+Z
        //     restores the whole edit — rather than a bookmark-specific undo stack.
        // ============================================================

        /// <summary>
        /// (Re)builds the OUTLINES tree from the active document's live PdfSharpCore outline
        /// collection. Safe to call on a null / read-only / outline-less document.
        /// </summary>
        private void LoadOutlines()
        {
            // A rebuild drives IsExpanded itself; the live Expanded/Collapsed recorder must only ever
            // hear the USER, or a rebuild would be recorded as a user choice against whichever tab
            // happens to be active at the time.
            _outlineRebuilding = true;
            try { LoadOutlinesCore(); }
            finally { _outlineRebuilding = false; }
        }

        private void LoadOutlinesCore()
        {
            _bmExtraSel.Clear();          // outlines may be gone after a rebuild / undo
            _outlineTree.Items.Clear();
            if (_doc is null)
            {
                _sidebarOutlinesTab.IsEnabled = false;
                if (_sidebarOutlinesTab.IsChecked == true) _sidebarPagesTab.IsChecked = true;
                return;
            }
            try
            {
                // #103: _doc.Outlines lazily CREATES an empty outlines object on documents that have
                // none, and PdfSharpCore then emits a dangling /Outlines reference the reopen rejects.
                // Peek at the catalog read-only and only touch .Outlines when one really exists.
                bool hasOutlines = _doc.Internals.Catalog.Elements.ContainsKey("/Outlines");
                var outlines = hasOutlines ? _doc.Outlines : null;
                if (outlines is null || outlines.Count == 0)
                {
                    // Stay enabled on an editable document so the user can open the panel and add a
                    // first bookmark (the ghost add-row is then the only entry); read-only documents
                    // keep the old "disabled when empty" gating.
                    _sidebarOutlinesTab.IsEnabled = CanEditBookmarks;
                    if (CanEditBookmarks) _outlineTree.Items.Add(BuildAddBookmarkGhostRow());
                    else if (_sidebarOutlinesTab.IsChecked == true) _sidebarPagesTab.IsChecked = true;
                    return;
                }
                _sidebarOutlinesTab.IsEnabled = true;
                if (CanEditBookmarks) _outlineTree.Items.Add(BuildAddBookmarkGhostRow());
                AddOutlineItems(_outlineTree.Items, outlines);
                // Sticky expand/collapse per tab. LoadOutlines rebuilds from scratch on every tab
                // switch and temp-reload, which used to re-expand everything the user had folded.
                if (_ctx.OutlineExpandSeen) ApplyOutlineExpandState();
                else { CaptureOutlineExpandState(); _ctx.OutlineExpandSeen = true; }
            }
            catch
            {
                // A malformed outline tree must never break opening a document.
                _outlineTree.Items.Clear();
                _sidebarOutlinesTab.IsEnabled = false;
                if (_sidebarOutlinesTab.IsChecked == true) _sidebarPagesTab.IsChecked = true;
            }
        }

        /// <summary>
        /// #133 (upstream v1.6.4): PdfSharpCore's lexer decodes UTF-16 bookmark titles by their BOM,
        /// but strings it decrypts AFTER parsing (owner-password protected files) never get that BOM
        /// re-check, so the title arrives as raw bytes widened to chars: a U+00FE U+00FF prefix (the
        /// BOM bytes) followed by one char per byte (mojibake, most visible on Chinese outlines).
        /// Detect the widened BOM, re-pack the chars into bytes, and decode as UTF-16. Titles that
        /// parsed correctly don't start with those two chars and pass through untouched.
        /// </summary>
        private static string FixRawUnicodeTitle(string s)
        {
            if (s.Length < 2) return s;
            bool be = s[0] == 'þ' && s[1] == 'ÿ';   // UTF-16BE BOM as raw chars
            bool le = s[0] == 'ÿ' && s[1] == 'þ';   // UTF-16LE (Adobe tolerance)
            if (!be && !le) return s;
            foreach (char c in s)
                if (c > 'ÿ') return s;   // not byte-widened data - a real (odd) title, leave it
            var sb = new System.Text.StringBuilder((s.Length - 2) / 2);
            for (int i = 2; i + 1 < s.Length; i += 2)   // a trailing odd byte is dropped rather than corrupting the pairs
                sb.Append(be ? (char)((s[i] << 8) | s[i + 1])
                             : (char)((s[i + 1] << 8) | s[i]));
            return sb.ToString();
        }

        /// <summary>Builds a TreeViewItem per outline (recursing into children) tied to its live
        /// PdfOutline via <see cref="OutlineNodeRef"/>.</summary>
        private void AddOutlineItems(ItemCollection target, PdfSharpCore.Pdf.PdfOutlineCollection outlines,
                                     int depth = 0)
        {
            foreach (PdfSharpCore.Pdf.PdfOutline outline in outlines)
            {
                int pageIdx = GetOutlinePageIndex(outline);
                string title = FixRawUnicodeTitle(outline.Title ?? string.Empty);
                var item = new TreeViewItem
                {
                    Header = string.IsNullOrEmpty(title) ? "(untitled)" : title,
                    // Top level starts open, deeper levels start folded (the Acrobat default) — a deep
                    // outline was otherwise a wall of text on open, since this used to expand every
                    // node at every depth. ApplyOutlineExpandState overrides this with the user's own
                    // choices once this tab has built its tree once.
                    IsExpanded = depth == 0,
                    Tag = new OutlineNodeRef(outline, outlines, pageIdx),
                    ToolTip = pageIdx >= 0 ? $"Page {pageIdx + 1}" : null,
                    // Items are added as ready-made containers, so ItemContainerStyle doesn't apply;
                    // set the themed style explicitly.
                    Style = (Style)FindResource("OutlineItemStyle"),
                };
                if (outline.Outlines is not null && outline.Outlines.Count > 0)
                    AddOutlineItems(item.Items, outline.Outlines, depth + 1);
                target.Add(item);
            }
        }

        // ---- Sticky OUTLINES expand/collapse (per tab; state lives on DocumentContext) ------------
        // Nodes are keyed by index path ("2/0/1"), ghost add-row excluded, because SaveTempAndReload
        // reopens the document and hands back brand-new PdfOutline instances — an object-keyed set
        // would go stale on every structural edit. RefreshOutlines still keys ITS capture on the live
        // PdfOutline objects, which survive a bookmark edit and stay correct when indices shift.
        private bool _outlineRebuilding;

        /// <summary>Walks the on-screen outline items in index order, ghost add-row skipped, handing
        /// each one its index path. The single definition of a node's key.</summary>
        private static void WalkOutlinePaths(ItemCollection items, string prefix,
                                             Action<TreeViewItem, string> visit)
        {
            int i = 0;
            foreach (var o in items)
            {
                if (o is not TreeViewItem it || it.Tag is not OutlineNodeRef) continue;
                string path = prefix.Length == 0 ? i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                                 : prefix + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                visit(it, path);
                WalkOutlinePaths(it.Items, path, visit);
                i++;
            }
        }

        /// <summary>Records which outline nodes are expanded in the tree currently on screen, against
        /// the active tab. Runs once per tab, right after its first tree is built, so the depth
        /// defaults become the baseline the live recorder then edits.</summary>
        private void CaptureOutlineExpandState()
        {
            var expanded = _ctx.OutlineExpanded;
            expanded.Clear();
            WalkOutlinePaths(_outlineTree.Items, "", (it, path) => { if (it.IsExpanded) expanded.Add(path); });
        }

        /// <summary>Restores this tab's recorded expand/collapse state onto a freshly built tree.</summary>
        private void ApplyOutlineExpandState()
        {
            var expanded = _ctx.OutlineExpanded;
            WalkOutlinePaths(_outlineTree.Items, "", (it, path) => it.IsExpanded = expanded.Contains(path));
        }

        /// <summary>Live recorder for the user's own expand/collapse clicks, attached once to the tree
        /// (the items themselves are thrown away and rebuilt constantly). Keeps the state current no
        /// matter which of the many LoadOutlines callers rebuilds next, so nothing has to remember to
        /// capture first.</summary>
        private void OutlineTree_ItemExpandChanged(object sender, RoutedEventArgs e)
        {
            if (_outlineRebuilding || e.OriginalSource is not TreeViewItem it
                || it.Tag is not OutlineNodeRef) return;
            string? path = null;
            WalkOutlinePaths(_outlineTree.Items, "", (node, p) => { if (ReferenceEquals(node, it)) path = p; });
            if (path is null) return;
            if (it.IsExpanded) _ctx.OutlineExpanded.Add(path);
            else               _ctx.OutlineExpanded.Remove(path);
        }

        /// <summary>Resolves an outline's 0-based target page. Prefers PdfSharpCore's parsed
        /// destination page, then falls back to TDPdf's richer resolver (named destinations,
        /// /GoTo actions) so navigation matches the pre-edit behaviour.</summary>
        private int GetOutlinePageIndex(PdfSharpCore.Pdf.PdfOutline outline)
        {
            if (_doc is null) return -1;
            if (outline.DestinationPage is PdfSharpCore.Pdf.PdfPage destPage)
            {
                for (int i = 0; i < _doc.PageCount; i++)
                    if (ReferenceEquals(_doc.Pages[i], destPage)) return i;
            }
            // Fall back to TDPdf's resolver on the outline's own dictionary.
            PdfItem? destItem = outline.Elements["/Dest"];
            if (destItem is null)
            {
                var action = outline.Elements.GetDictionary("/A");
                if (action is not null &&
                    (action.Elements.GetName("/S") == "/GoTo" || action.Elements.ContainsKey("/D")))
                {
                    destItem = action.Elements["/D"];
                }
            }
            return ResolveDest(destItem) ?? -1;
        }

        private void OutlineTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_suppressOutlineNav) return;   // programmatic re-select (e.g. after a move) must not jump the view
            if (e.NewValue is TreeViewItem item && item.Tag is OutlineNodeRef nref
                && nref.PageIndex >= 0 && _doc is not null && nref.PageIndex < _doc.PageCount
                && PageList.SelectedIndex != nref.PageIndex)
            {
                RecordNavJump();   // bookmark jump — retraceable via Alt+Left
                PageList.SelectedIndex = nref.PageIndex;
            }
        }

        // The TreeView's own scroll viewer swallows the wheel before the outer one sees it, so the
        // Outlines panel wouldn't scroll. Forward the wheel to the outer scroll viewer.
        private void OutlineScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            _outlineScrollViewer.ScrollToVerticalOffset(_outlineScrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        // ============================================================
        // Bookmark editing (#133): add / rename / child / reorder / retarget / delete
        // ============================================================

        /// <summary>Ties a TreeViewItem to its live PdfOutline, the collection that contains it, and
        /// the resolved target page (for click-to-navigate).</summary>
        private sealed class OutlineNodeRef
        {
            public readonly PdfSharpCore.Pdf.PdfOutline Outline;
            public readonly PdfSharpCore.Pdf.PdfOutlineCollection Parent;
            public readonly int PageIndex;
            public OutlineNodeRef(PdfSharpCore.Pdf.PdfOutline outline,
                                  PdfSharpCore.Pdf.PdfOutlineCollection parent, int pageIndex)
            { Outline = outline; Parent = parent; PageIndex = pageIndex; }
        }

        // PdfSharpCore cannot save a document opened read-only (owner-password / XRef-fallback opens),
        // so bookmark editing is hidden there rather than failing at save time.
        private bool CanEditBookmarks => _doc is not null && !_doc.IsReadOnly;

        // Multi-select. WPF's TreeView is hard single-select, so its built-in selection stays the
        // "primary" item and Ctrl/Shift clicks maintain this extra set on top. Keyed by PdfOutline so
        // the selection survives tree rebuilds within one document.
        private readonly HashSet<PdfSharpCore.Pdf.PdfOutline> _bmExtraSel = new();
        private bool _suppressOutlineNav;
        private bool _bmRenaming;   // an inline rename box owns the keyboard — window paging keys stand down

        /// <summary>All bookmark rows in visual order (optionally only rows currently visible, i.e.
        /// with every ancestor expanded). The ghost add-row is never included.</summary>
        private static void FlattenBookmarkItems(ItemCollection items, bool visibleOnly,
                                                 List<(TreeViewItem Item, OutlineNodeRef Ref)> into)
        {
            foreach (TreeViewItem it in items)
            {
                if (it.Tag is OutlineNodeRef r) into.Add((it, r));
                if (!visibleOnly || it.IsExpanded)
                    FlattenBookmarkItems(it.Items, visibleOnly, into);
            }
        }

        /// <summary>Paints/clears the extra-selection look. The item template's IsSelected trigger
        /// drives Bd.Background/BorderBrush + Foreground; extras set the same three locally (local
        /// values outrank template triggers) and ClearValue restores normal styling.</summary>
        private void ApplyExtraSelectionVisuals()
        {
            var all = new List<(TreeViewItem Item, OutlineNodeRef Ref)>();
            FlattenBookmarkItems(_outlineTree.Items, visibleOnly: false, all);
            foreach (var (it, r) in all)
            {
                it.ApplyTemplate();
                var bd = it.Template?.FindName("Bd", it) as Border;
                if (_bmExtraSel.Contains(r.Outline))
                {
                    if (bd is not null)
                    {
                        bd.Background = BrushResource("AccentGreenDim");
                        bd.BorderBrush = BrushResource("AccentGreen");
                    }
                    it.Foreground = Brushes.White;   // matches the IsSelected trigger
                }
                else
                {
                    if (bd is not null)
                    {
                        bd.ClearValue(Border.BackgroundProperty);
                        bd.ClearValue(Border.BorderBrushProperty);
                    }
                    it.ClearValue(ForegroundProperty);
                }
            }
        }

        private void ClearBookmarkMultiSelection()
        {
            if (_bmExtraSel.Count == 0) return;
            _bmExtraSel.Clear();
            ApplyExtraSelectionVisuals();
        }

        // True when the click landed on the expand/collapse toggle - those pass through untouched.
        private static bool IsExpanderClick(DependencyObject? d)
        {
            while (d is not null && d is not TreeViewItem)
            {
                if (d is System.Windows.Controls.Primitives.ToggleButton) return true;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private void OutlineTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsExpanderClick(e.OriginalSource as DependencyObject)) return;
            var tvi = OutlineItemAt(e.OriginalSource as DependencyObject);
            bool ctrl  = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (tvi?.Tag is not OutlineNodeRef nref || !CanEditBookmarks || (!ctrl && !shift))
            {
                // Plain click, ghost row, or empty space: default single-selection behaviour.
                ClearBookmarkMultiSelection();
                return;
            }
            if (ctrl)
            {
                // Fold the primary into the set so the whole selection lives in one place, then toggle.
                if (_outlineTree.SelectedItem is TreeViewItem prim && prim.Tag is OutlineNodeRef pr)
                    _bmExtraSel.Add(pr.Outline);
                if (!_bmExtraSel.Add(nref.Outline)) _bmExtraSel.Remove(nref.Outline);
            }
            else
            {
                // Shift: range from the primary to the clicked row, in visible order.
                _bmExtraSel.Clear();
                var flat = new List<(TreeViewItem Item, OutlineNodeRef Ref)>();
                FlattenBookmarkItems(_outlineTree.Items, visibleOnly: true, flat);
                var primary = (_outlineTree.SelectedItem as TreeViewItem)?.Tag as OutlineNodeRef;
                int ia = primary is null ? -1 : flat.FindIndex(t => ReferenceEquals(t.Ref, primary));
                int ib = flat.FindIndex(t => ReferenceEquals(t.Item, tvi));
                if (ib < 0) return;
                if (ia < 0) ia = ib;
                for (int k = Math.Min(ia, ib); k <= Math.Max(ia, ib); k++)
                    _bmExtraSel.Add(flat[k].Ref.Outline);
            }
            ApplyExtraSelectionVisuals();
            e.Handled = true;   // keep the built-in primary selection where it is
        }

        private void OutlineTree_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!CanEditBookmarks) return;
            if (e.OriginalSource is TextBox) return;   // inline rename in progress: keys edit text, not bookmarks
            var primary = (_outlineTree.SelectedItem as TreeViewItem)?.Tag as OutlineNodeRef;
            if (e.Key == Key.Delete && (primary is not null || _bmExtraSel.Count > 0))
            {
                e.Handled = true;
                DeleteSelectedBookmarks(primary);
            }
            else if (e.Key == Key.F2 && primary is not null && _outlineTree.SelectedItem is TreeViewItem tvi)
            {
                e.Handled = true;
                BeginInlineRename(tvi, primary);
            }
        }

        /// <summary>The add action lives as a dim first row inside the tree itself: a + glyph and
        /// "Add bookmark", brightening on hover. Tag stays null so the selection handler, context
        /// menu, and refresh walks all treat it as a non-bookmark row.</summary>
        private TreeViewItem BuildAddBookmarkGhostRow()
        {
            var icon = new TextBlock
            {
                Text = "\uE710",   // Segoe MDL2 Add
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            };
            var text = new TextBlock { Text = "Add bookmark", VerticalAlignment = VerticalAlignment.Center };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0.55 };
            panel.Children.Add(icon);
            panel.Children.Add(text);
            var item = new TreeViewItem
            {
                Header = panel,
                ToolTip = "Add a bookmark pointing at the current page",
                Style = (Style)FindResource("OutlineItemStyle"),
            };
            item.MouseEnter += (_, _2) => panel.Opacity = 1.0;
            item.MouseLeave += (_, _2) => panel.Opacity = 0.55;
            item.PreviewMouseLeftButtonUp += (_, ev) => { ev.Handled = true; AddBookmarkInto(null); };
            return item;
        }

        /// <summary>Adds a bookmark pointing at the current page - to the root list, or as a child of
        /// <paramref name="parent"/> - titled "Page N", then drops straight into an inline rename of
        /// the new entry (no dialog). Esc keeps the default title.</summary>
        private void AddBookmarkInto(OutlineNodeRef? parent)
        {
            if (!CanEditBookmarks || _doc is null) return;
            if (parent is not null && !ReferenceEquals(parent.Outline.Owner, _doc)) { LoadOutlines(); return; }   // stale ref
            int page = Math.Max(0, PageList.SelectedIndex);
            if (page >= _doc.PageCount) page = _doc.PageCount - 1;
            if (page < 0) return;
            PushDocUndo();   // bookmark ops ride the document-snapshot undo like crop / page ops do
            var col = parent is null ? _doc.Outlines : parent.Outline.Outlines;
            var added = col.Add($"Page {page + 1}", _doc.Pages[page], true);
            ScrubStaleOutlineLinkKeys();
            MarkDirty();
            RefreshOutlines();
            if (FindOutlineItem(_outlineTree.Items, added) is { } tvi && tvi.Tag is OutlineNodeRef nref)
            {
                tvi.BringIntoView();
                BeginInlineRename(tvi, nref);
            }
        }

        /// <summary>Swaps a tree item's header for an inline TextBox (rename-in-place; also used right
        /// after adding). Enter or clicking elsewhere commits, Esc cancels.</summary>
        private void BeginInlineRename(TreeViewItem tvi, OutlineNodeRef nref)
        {
            if (!CanEditBookmarks) return;
            if (!ReferenceEquals(nref.Outline.Owner, _doc)) { LoadOutlines(); return; }   // stale ref
            string current = FixRawUnicodeTitle(nref.Outline.Title ?? string.Empty);
            var box = new TextBox
            {
                Text = current,
                MinWidth = 110,
                FontSize = _outlineTree.FontSize,
                FontFamily = new FontFamily("Segoe UI"),
                Padding = new Thickness(3, 1, 3, 1),
                Background = BrushResource("BgPanel"),
                Foreground = BrushResource("TextPrimary"),
                BorderBrush = BrushResource("AccentGreen"),   // accent border = active in-place edit
                BorderThickness = new Thickness(1),
                CaretBrush = BrushResource("AccentGreen"),
                SelectionBrush = BrushResource("AccentGreenDim"),
                FocusVisualStyle = null,
            };
            bool done = false;
            void Commit()
            {
                if (done) return;
                done = true;
                _bmRenaming = false;
                string t = box.Text.Trim();
                if (t.Length > 0 && t != current)
                {
                    PushDocUndo();
                    nref.Outline.Title = t;   // the setter writes a proper Unicode string, healing mojibake entries
                    MarkDirty();
                    RefreshOutlines();
                }
                else
                    tvi.Header = string.IsNullOrEmpty(current) ? "(untitled)" : current;
            }
            void Cancel()
            {
                if (done) return;
                done = true;
                _bmRenaming = false;
                tvi.Header = string.IsNullOrEmpty(current) ? "(untitled)" : current;
            }
            box.PreviewKeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter)  { ke.Handled = true; Commit(); }
                if (ke.Key == Key.Escape) { ke.Handled = true; Cancel(); }
            };
            box.LostFocus += (_, _2) => Commit();
            _bmRenaming = true;
            tvi.Header = box;
            // The box can't take focus until it has been laid out - focus it after render.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                (Action)(() => { box.Focus(); box.SelectAll(); }));
        }

        /// <summary>Finds the tree item for a PdfOutline, expanding collapsed ancestors on the way.</summary>
        private static TreeViewItem? FindOutlineItem(ItemCollection items, object outline)
        {
            foreach (TreeViewItem it in items)
            {
                if (it.Tag is OutlineNodeRef r && ReferenceEquals(r.Outline, outline)) return it;
                if (FindOutlineItem(it.Items, outline) is { } hit) { it.IsExpanded = true; return hit; }
            }
            return null;
        }

        /// <summary>Deletes the multi-selection if one exists, plus the clicked/primary item. One
        /// confirm covers the whole set; one undo entry restores it.</summary>
        private void DeleteSelectedBookmarks(OutlineNodeRef? clicked)
        {
            if (!CanEditBookmarks) return;
            if (clicked is not null && !ReferenceEquals(clicked.Outline.Owner, _doc)) { LoadOutlines(); return; }   // stale ref

            // Gather targets: the extra set, the primary, and the clicked item, deduplicated.
            var all = new List<(TreeViewItem Item, OutlineNodeRef Ref)>();
            FlattenBookmarkItems(_outlineTree.Items, visibleOnly: false, all);
            var targets = new List<OutlineNodeRef>();
            foreach (var (_, r) in all)
                if (_bmExtraSel.Contains(r.Outline)) targets.Add(r);
            void AddTarget(OutlineNodeRef? r)
            {
                if (r is not null && !targets.Any(t => ReferenceEquals(t.Outline, r.Outline))) targets.Add(r);
            }
            AddTarget((_outlineTree.SelectedItem as TreeViewItem)?.Tag as OutlineNodeRef);
            AddTarget(clicked);
            if (targets.Count == 0) return;

            // A target with a selected ancestor is covered by deleting the ancestor - drop it so the
            // remaining targets are independent (their parent collections stay valid during removal).
            var chosen = new HashSet<object>(targets.Select(t => (object)t.Outline));
            bool Covered(PdfSharpCore.Pdf.PdfOutline o)
            {
                for (var p = o.Parent; p is not null; p = p.Parent)
                    if (chosen.Contains(p)) return true;
                return false;
            }
            targets = targets.Where(t => !Covered(t.Outline)).ToList();

            int total = targets.Sum(t => 1 + CountOutlines(t.Outline.Outlines));
            if (total > 1)
            {
                string msg = targets.Count == 1
                    ? $"Delete this bookmark and its {total - 1} child bookmark(s)?"
                    : $"Delete {total} bookmarks?";
                var r = TdpDialog.Show(this, msg, "TDPdf", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }
            PushDocUndo();   // one Ctrl+Z restores the whole set
            foreach (var t in targets)
                RemoveOutlineRecursive(t.Parent, t.Outline);
            ScrubStaleOutlineLinkKeys();
            MarkDirty();
            RefreshOutlines();   // also clears _bmExtraSel via LoadOutlines
        }

        /// <summary>Moves a bookmark one position up or down among its siblings.</summary>
        private void MoveBookmark(OutlineNodeRef nref, int delta)
        {
            if (!CanEditBookmarks) return;
            if (!ReferenceEquals(nref.Outline.Owner, _doc)) { LoadOutlines(); return; }   // stale ref
            int i = nref.Parent.IndexOf(nref.Outline);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= nref.Parent.Count) return;
            PushDocUndo();
            // RemoveAt drops the object from the xref table; Insert/Add puts it straight back.
            nref.Parent.RemoveAt(i);
            if (j >= nref.Parent.Count) nref.Parent.Add(nref.Outline);
            else nref.Parent.Insert(j, nref.Outline);
            ScrubStaleOutlineLinkKeys();
            MarkDirty();
            RefreshOutlines();
            // Keep the moved item selected, without the page-jump side effect.
            if (FindOutlineItem(_outlineTree.Items, nref.Outline) is { } moved)
            {
                _suppressOutlineNav = true;
                try { moved.IsSelected = true; moved.BringIntoView(); }
                finally { _suppressOutlineNav = false; }
            }
        }

        /// <summary>Repoints a bookmark at the current page as a plain go-to-page destination.</summary>
        private void SetBookmarkDestination(OutlineNodeRef nref)
        {
            if (!CanEditBookmarks || _doc is null) return;
            if (!ReferenceEquals(nref.Outline.Owner, _doc)) { LoadOutlines(); return; }   // stale ref
            int page = Math.Max(0, PageList.SelectedIndex);
            if (page >= _doc.PageCount) page = _doc.PageCount - 1;
            if (page < 0) return;
            PushDocUndo();
            nref.Outline.DestinationPage = _doc.Pages[page];
            // Plain jump: /XYZ null null null keeps the reader's current zoom / position behaviour.
            nref.Outline.PageDestinationType = PdfSharpCore.Pdf.PdfPageDestinationType.Xyz;
            nref.Outline.Left = double.NaN;
            nref.Outline.Top = double.NaN;
            nref.Outline.Zoom = double.NaN;
            MarkDirty();
            RefreshOutlines();
        }

        /// <summary>Removes every bookmark in the document (one confirm, one undo entry).</summary>
        private void DeleteAllBookmarks()
        {
            if (!CanEditBookmarks || _doc is null) return;
            if (!_doc.Internals.Catalog.Elements.ContainsKey("/Outlines")) return;   // nothing to do, and never plant one
            if (_doc.Outlines.Count == 0) return;
            var r = TdpDialog.Show(this, "Delete all bookmarks in this document?", "TDPdf",
                                   MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            PushDocUndo();
            while (_doc.Outlines.Count > 0)
                RemoveOutlineRecursive(_doc.Outlines, _doc.Outlines[_doc.Outlines.Count - 1]);
            ScrubStaleOutlineLinkKeys();
            MarkDirty();
            RefreshOutlines();
        }

        private static int CountOutlines(PdfSharpCore.Pdf.PdfOutlineCollection col)
        {
            int n = 0;
            foreach (PdfSharpCore.Pdf.PdfOutline o in col) n += 1 + CountOutlines(o.Outlines);
            return n;
        }

        // Bottom-up: Collection.Remove() drops the removed object from the document's reference table,
        // so deleting the whole branch leaf-first leaves no orphaned outline objects (with dangling
        // /Parent refs) behind in the saved file.
        private static void RemoveOutlineRecursive(PdfSharpCore.Pdf.PdfOutlineCollection parent,
                                                   PdfSharpCore.Pdf.PdfOutline outline)
        {
            while (outline.Outlines.Count > 0)
                RemoveOutlineRecursive(outline.Outlines, outline.Outlines[outline.Outlines.Count - 1]);
            parent.Remove(outline);
        }

        // PdfSharpCore's PrepareForSave rebuilds outline linkage keys (/First /Last /Next /Prev
        // /Parent /Count) from the in-memory collections on save, but never REMOVES entries that no
        // longer apply (an item that became last keeps its old /Next, an emptied parent keeps
        // /First /Last). After any bookmark edit, strip those keys on the CHILD nodes so the writer
        // rebuilds them cleanly. (Deviation from upstream: we deliberately do NOT strip the root
        // outline dict's /First — the save-time ScrubEmptyOutlines uses root /First to decide whether
        // to drop a dangling /Outlines, and the writer rewrites the root's linkage anyway.) When the
        // tree has been fully emptied we remove the catalog /Outlines entry outright, so the raw
        // in-memory saves the snapshot-undo takes never serialize a dangling reference (#103).
        private void ScrubStaleOutlineLinkKeys()
        {
            if (_doc is null) return;
            try
            {
                if (!_doc.Internals.Catalog.Elements.ContainsKey("/Outlines")) return;
                if (_doc.Outlines.Count == 0)
                {
                    _doc.Internals.Catalog.Elements.Remove("/Outlines");
                    return;
                }
                ScrubOutlineLinkKeys(_doc.Outlines);
            }
            catch { /* malformed outline tree - the save-time scrubs are the backstop */ }
        }

        private static void ScrubOutlineLinkKeys(PdfSharpCore.Pdf.PdfOutlineCollection col)
        {
            foreach (PdfSharpCore.Pdf.PdfOutline o in col)
            {
                o.Elements.Remove("/First");
                o.Elements.Remove("/Last");
                o.Elements.Remove("/Next");
                o.Elements.Remove("/Prev");
                o.Elements.Remove("/Parent");
                o.Elements.Remove("/Count");
                ScrubOutlineLinkKeys(o.Outlines);
            }
        }

        /// <summary>Rebuilds the outline panel after an edit, keeping collapsed branches collapsed
        /// (the PdfOutline objects survive the rebuild, so they key the state).</summary>
        private void RefreshOutlines()
        {
            var collapsed = new HashSet<object>();
            void Capture(ItemCollection items)
            {
                foreach (TreeViewItem it in items)
                {
                    if (!it.IsExpanded && it.Tag is OutlineNodeRef r) collapsed.Add(r.Outline);
                    Capture(it.Items);
                }
            }
            Capture(_outlineTree.Items);
            LoadOutlines();
            if (collapsed.Count > 0)
            {
                void Restore(ItemCollection items)
                {
                    foreach (TreeViewItem it in items)
                    {
                        if (it.Tag is OutlineNodeRef r && collapsed.Contains(r.Outline)) it.IsExpanded = false;
                        Restore(it.Items);
                    }
                }
                Restore(_outlineTree.Items);
            }
            // A bookmark edit shifts index paths, so the object-keyed restore above is the authority
            // here — re-baseline the path-keyed session state from the tree it just produced, or the
            // next tab switch would replay stale paths over the edited outline.
            CaptureOutlineExpandState();
        }

        /// <summary>Right-click on the outline panel: bookmark menu for the item under the cursor, or
        /// the add-bookmark menu on empty space. Hidden entirely on read-only documents.</summary>
        private void OutlineTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!CanEditBookmarks) return;
            var tvi = OutlineItemAt(e.OriginalSource as DependencyObject);
            var menu = new ContextMenu();
            TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(menu, TextRenderingMode.Grayscale);
            if (tvi?.Tag is OutlineNodeRef nref)
            {
                // Right-click outside the multi-selection collapses it to the clicked item (the
                // file-explorer convention); inside it, the menu acts on the whole set.
                bool inMulti = _bmExtraSel.Contains(nref.Outline);
                if (!inMulti) ClearBookmarkMultiSelection();
                _suppressOutlineNav = true;
                try { tvi.IsSelected = true; }   // WPF doesn't select on right-click by itself
                finally { _suppressOutlineNav = false; }

                if (inMulti && _bmExtraSel.Count > 1)
                {
                    menu.Items.Add(MakeMenuItem($"Delete ({_bmExtraSel.Count})",
                                                (_, _2) => DeleteSelectedBookmarks(nref), "Delete", null, "\uE74D"));
                }
                else
                {
                    menu.Items.Add(MakeMenuItem("_Rename", (_, _2) => BeginInlineRename(tvi, nref), "F2", null, "\uE8AC"));
                    menu.Items.Add(MakeMenuItem("Add _child bookmark", (_, _2) => AddBookmarkInto(nref), null, null, "\uE710"));
                    menu.Items.Add(MakeMenuItem("Set destination to current page", (_, _2) => SetBookmarkDestination(nref), null, null, "\uE718"));
                    menu.Items.Add(new Separator());
                    int idx = nref.Parent.IndexOf(nref.Outline);
                    var up = MakeMenuItem("Move _up", (_, _2) => MoveBookmark(nref, -1), null, null, "\uE74A");
                    up.IsEnabled = idx > 0;
                    menu.Items.Add(up);
                    var down = MakeMenuItem("Move _down", (_, _2) => MoveBookmark(nref, +1), null, null, "\uE74B");
                    down.IsEnabled = idx >= 0 && idx < nref.Parent.Count - 1;
                    menu.Items.Add(down);
                    menu.Items.Add(new Separator());
                    menu.Items.Add(MakeMenuItem("_Delete", (_, _2) => DeleteSelectedBookmarks(nref), "Delete", null, "\uE74D"));
                }
            }
            else
            {
                menu.Items.Add(MakeMenuItem("_Add bookmark", (_, _2) => AddBookmarkInto(null), null, null, "\uE710"));
                bool hasAny = _doc?.Internals.Catalog.Elements.ContainsKey("/Outlines") == true
                              && _outlineTree.Items.Count > 1;   // ghost row + at least one real entry
                if (hasAny)
                {
                    menu.Items.Add(new Separator());
                    menu.Items.Add(MakeMenuItem("Delete all bookmarks", (_, _2) => DeleteAllBookmarks(), null, null, "\uE74D"));
                }
            }
            menu.PlacementTarget = _outlineTree;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private static TreeViewItem? OutlineItemAt(DependencyObject? d)
        {
            while (d is not null && d is not TreeViewItem)
                d = d is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);   // e.g. a Run inside the header
            return d as TreeViewItem;
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
                (_toolStrikeBtn, EditTool.Strikethrough),
                (_toolUnderlineBtn, EditTool.Underline),
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
                EditTool.Strikethrough => Cursors.Cross,
                EditTool.Underline => Cursors.Cross,
                EditTool.Draw => Cursors.Pen,
                EditTool.Signature => Cursors.Hand,
                EditTool.Image => Cursors.Hand,
                EditTool.Crop => Cursors.Cross,
                EditTool.Pan => Cursors.Hand,
                EditTool.Erase => Cursors.Cross,
                EditTool.Shape => Cursors.Cross,
                _ => Cursors.Arrow
            };

            // Show/hide draw settings bar (the markup tools share it — colour + opacity)
            if (tool is EditTool.Draw or EditTool.Highlight or EditTool.Strikethrough or EditTool.Underline)
                ShowDrawSettings(tool);
            else
                HideDrawSettings();

            // Show/hide text tool settings bar. Edit Existing Text shares it with the Text tool —
            // it used to never show it at all outside of an active SelectAnnotation binding, which
            // read as the bar "not reappearing" when switching into or back onto that tool.
            if (tool is EditTool.Text or EditTool.EditText)
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
            _isResizingImage || _isMovingAnnot || _isResizingAnnot || _isPanning ||
            _txtSelActive;

        // Sidebar toggle strip button and the View menu's "Toggle Sidebar" both land here.
        private void SidebarToggle_Click(object sender, RoutedEventArgs e) =>
            SetSidebarCollapsed(!_sidebarCollapsed, animate: true);

        /// <summary>
        /// Collapses the page sidebar to the 24px toggle strip, or expands it back to the last open
        /// width. With <paramref name="animate"/> the column glides over a quarter second instead of
        /// snapping (upstream KillerPDF v1.6.5); the content panel is pinned at a fixed width for
        /// the duration and the border clips it, so thumbnails hold their size and slide out of view
        /// rather than reflowing narrower every frame. View state only \u2014 never marks the document dirty.
        /// </summary>
        private void SetSidebarCollapsed(bool collapse, bool animate)
        {
            FinishSidebarAnimation();   // land any glide already in flight before starting another

            // Remember the width we are collapsing from so the next expand restores what the user
            // was looking at rather than a hardcoded default. (Today the column is a fixed 180 with
            // no splitter, so this is belt-and-braces \u2014 but it costs nothing and survives a splitter
            // being added later.)
            // SidebarCol is in the UNSCALED outer grid, so every width here is SCREEN px while the
            // constants below are LOGICAL px; SbPx bridges the two at the current app scale
            // (AppScale.cs). _sidebarExpandedWidth is read from ActualWidth, so it is already
            // screen px and ApplyAppScale rescales it when the scale changes.
            if (collapse && _sidebarCol.ActualWidth > SbPx(SidebarStripWidth))
                _sidebarExpandedWidth = _sidebarCol.ActualWidth;

            _sidebarCollapsed = collapse;
            _sidebarToggleBtn.Content = collapse ? "\uE76C" : "\uE76B";   // ChevronRight / ChevronLeft (Segoe MDL2)
            _sidebarToggleBtn.ToolTip = collapse ? "Expand sidebar" : "Collapse sidebar";
            double target = collapse ? SbPx(SidebarStripWidth) : _sidebarExpandedWidth;

            // Full screen owns the live column while it is active (it zeroes width AND MinWidth to
            // get the strip out of the way). Writing to the column here would either be undone on
            // exit or fight the restore, so instead re-point the snapshot ToggleFullScreen restores
            // from \u2014 leaving F11 to land on the new sidebar state when it exits. Reached when the
            // last document is closed with Ctrl+W while full screen.
            if (_fullScreen)
            {
                _fsSidebarVis   = collapse ? Visibility.Collapsed : Visibility.Visible;
                _fsSidebarWidth = new GridLength(target);
                _fsSidebarMin   = SbPx(SidebarStripWidth);
                return;
            }

            if (!animate || !IsLoaded)
            {
                _sidebarBorder.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
                _sidebarCol.Width = new GridLength(target);
                _sidebarCol.MinWidth = SbPx(SidebarStripWidth);
                SettleSidebarPageView();
                return;
            }

            // The border is what does the clipping, so it stays visible for the whole glide; on a
            // collapse it is hidden only once the strip width has been reached.
            _sidebarBorder.Visibility = Visibility.Visible;
            _sidebarCol.MinWidth = SbPx(SidebarStripWidth);
            // The pinned content width is LOGICAL — the panel lives inside the scaled grid — so the
            // screen-px column measurements are divided back out by the app scale.
            BeginSidebarSlide(collapse
                // Hold the open size and clip it away. The column-derived width is a fallback for the
                // case where layout has not run since the panel was last resized.
                ? Math.Max(_sidebarContentPanel.ActualWidth,
                           (_sidebarCol.ActualWidth - SbPx(SidebarStripWidth)) / _appScale)
                // Expanding: full size from the very first frame, revealed by the growing border.
                : Math.Max(0, (target - SbPx(SidebarStripWidth)) / _appScale));
            AnimateSidebarWidth(target, () =>
            {
                if (collapse) _sidebarBorder.Visibility = Visibility.Collapsed;
                EndSidebarSlide();
                SettleSidebarPageView();
            });
        }

        // Pins the sidebar content at a fixed width, anchored to the column's left edge (the sidebar
        // lives on the left, so the far edge is the one that gets clipped away), and turns on
        // clipping so the overflow is cut rather than drawn outside the border.
        private void BeginSidebarSlide(double contentWidth)
        {
            if (contentWidth <= 0) return;
            _sidebarContentPanel.Width = contentWidth;
            _sidebarContentPanel.HorizontalAlignment = HorizontalAlignment.Left;
            _sidebarBorder.ClipToBounds = true;
        }

        // Hands the content back to normal stretch layout once the glide lands.
        private void EndSidebarSlide()
        {
            _sidebarContentPanel.Width = double.NaN;
            _sidebarContentPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            _sidebarBorder.ClipToBounds = false;
        }

        // Glides the sidebar column to a new width. Completion clears the animation and writes the
        // target as a plain local value, so full screen and any future splitter can keep assigning
        // Width directly (an animation left in place outranks a local value and would swallow them).
        private void AnimateSidebarWidth(double target, Action onDone)
        {
            _sidebarAnimTarget = target;
            _sidebarAnimDone = onDone;
            var anim = new TDPdf.Controls.GridLengthAnimation
            {
                From     = new GridLength(Math.Max(0, _sidebarCol.ActualWidth)),
                To       = new GridLength(target),
                Duration = TimeSpan.FromMilliseconds(250),
                Easing   = new System.Windows.Media.Animation.CubicEase
                           { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
            };
            anim.Completed += (_, _) => FinishSidebarAnimation();
            _sidebarCol.BeginAnimation(ColumnDefinition.WidthProperty, anim);
        }

        // Lands the running glide immediately: drop the animation, write the target width as a local
        // value, and run the pending finish work exactly once. Safe to call when nothing is running.
        // Called both from the animation's Completed handler and from anything that needs the column
        // back under direct control right now (ToggleFullScreen, a second toggle mid-glide).
        private void FinishSidebarAnimation()
        {
            if (_sidebarAnimDone is null) return;
            var done = _sidebarAnimDone;
            _sidebarAnimDone = null;
            _sidebarCol.BeginAnimation(ColumnDefinition.WidthProperty, null);
            _sidebarCol.Width = new GridLength(_sidebarAnimTarget);
            done();
        }

        // One crisp re-render after the pane has finished moving. During the glide every frame fires
        // PagePreviewPanel_SizeChanged, whose debounced fit keeps the page tracking the pane; this is
        // the single full pass that settles it afterwards.
        private void SettleSidebarPageView()
        {
            if (PageList.SelectedIndex >= 0)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => RefreshPageView(PageList.SelectedIndex));
        }

        /// <summary>
        /// Keeps the sidebar in step with whether a document is open (upstream KillerPDF v1.6.6): an
        /// empty workspace has no thumbnails to show, so the rail gets out of the way and the page
        /// jump box / "/ \u2013" total hide with it; opening a document brings them back.
        /// </summary>
        /// <remarks>
        /// The rule fires only on the empty \u2194 document transition, and only from the two places that
        /// actually make that transition (a document finished opening; the last tab closed) \u2014 never
        /// on a tab switch, and never on the transient empty tab OpenInTabAsync creates on its way to
        /// a second document. And it only moves a sidebar that is not already where the rule wants
        /// it, so a manual toggle sticks: collapse the rail with a document open and opening another
        /// document leaves it collapsed. An explicit choice is only reset by the workspace going
        /// empty and filling again.
        /// </remarks>
        private void SyncSidebarToDocState(bool hasDoc, bool startup)
        {
            UpdatePageControlsForDoc(hasDoc);

            if (_sidebarSyncedHasDoc == hasDoc) return;   // not a transition
            _sidebarSyncedHasDoc = hasDoc;

            bool wantCollapsed = !hasDoc;
            if (_sidebarCollapsed == wantCollapsed) return;   // user already put it there
            SetSidebarCollapsed(wantCollapsed, animate: !startup);   // instant at launch, glides at runtime
        }

        // The page jump box and its "/ \u2013" total mean nothing with no document open. The row they sit
        // in (PageControlsRow) is owned by the PAGES / OUTLINES tab selection, so this has to be
        // per-control \u2014 the PAGES header stays put and the row keeps its shape.
        private void UpdatePageControlsForDoc(bool hasDoc)
        {
            var vis = hasDoc ? Visibility.Visible : Visibility.Collapsed;
            _pageJumpBox.Visibility = vis;
            _pageTotalLabel.Visibility = vis;
            // The status line only does something (flash the file size) with a document open, so it only
            // looks clickable then — an empty workspace keeps the plain arrow.
            StatusText.Cursor = hasDoc ? Cursors.Hand : null;
        }

        private void ToolSelect_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Select);
        private void ToolText_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Text);
        private void ToolEditText_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.EditText);
        private void ToolEditImage_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.EditImage);
        private void ToolHighlight_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Highlight);
        private void ToolStrike_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Strikethrough);
        private void ToolUnderline_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Underline);
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
                else SetMarkupToolColor(tool, Color.FromArgb(MarkupToolColor(tool).A, c.R, c.G, c.B));
                ShowDrawSettings(tool);
            }
            void ApplyOpacity(byte a)
            {
                if (inkTarget is not null) { inkTarget.ColorA = a; RestyleLive(inkTarget); return; }
                if (hlTarget is not null) { hlTarget.ColorA = a; RestyleLive(hlTarget); return; }
                if (tool == EditTool.Draw) { _drawOpacity = a; _drawColor = Color.FromArgb(a, _drawColor.R, _drawColor.G, _drawColor.B); }
                else
                {
                    var mc = MarkupToolColor(tool);
                    SetMarkupToolColor(tool, Color.FromArgb(a, mc.R, mc.G, mc.B));
                }
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
                tool == EditTool.Draw ? _drawColor
                : Color.FromRgb(MarkupToolColor(tool).R, MarkupToolColor(tool).G, MarkupToolColor(tool).B);
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
            byte currentOpacity = inkTarget?.ColorA ?? hlTarget?.ColorA
                ?? (tool == EditTool.Draw ? _drawOpacity : MarkupToolColor(tool).A);
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
                // Was a hardcoded near-black (#1a1a1a) - darker than every other surface in the
                // app's own dark palette and dead wrong in Light/HighContrast (never actually
                // themed). BgPanel is the same resource already used for inputs/panels elsewhere.
                Background = (SolidColorBrush)FindResource("BgPanel"),
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

        // Curated, not "every installed font": each of these ships with Windows and is already a
        // known-good entry in PdfFontStyle's PS-name map / FontCoverage's fallback chains, so picking
        // one here can never produce a family that fails to embed or that a colleague's machine lacks
        // when they later reopen the same file. FontCoverage.PickFamily still silently upgrades the
        // choice for a script it can't cover (CJK, Arabic, etc.) — this list is what a Latin-script
        // annotation actually gets held to, not a hard ceiling on what the saved PDF can contain.
        private static readonly string[] TextFontFamilies =
            [PdfFontStyle.DefaultFamily, "Arial", "Times New Roman", "Courier New", "Calibri"];

        private void ShowTextSettings()
        {
            HideTextSettings();

            // When a text annotation is selected, the bar restyles THAT box; otherwise it sets tool defaults.
            // TextEditAnnotation (the Edit-Text tool, for existing PDF text) shares the Size/Color
            // controls below but has no whiteout toggle — the whiteout there isn't optional, it's
            // what hides the original text — so editTarget skips the Fill section entirely.
            var target = _styleTarget as TextAnnotation;
            var editTarget = _styleTarget as TextEditAnnotation;
            double curSize = target?.FontSize ?? editTarget?.FontSize ?? _textFontSize;
            string curFont = target?.FontName ?? editTarget?.FontName ?? _textFontFamily;
            Color curColor = target?.GetColor() ?? editTarget?.GetColor() ?? _textColor;
            bool curBold = target?.Bold ?? editTarget?.Bold ?? _textBold;
            bool curItalic = target?.Italic ?? editTarget?.Italic ?? _textItalic;
            bool curFill = target?.HasFill ?? _textWhiteout;
            Color curFillColor = target is { HasFill: true } ? target.GetFillColor() : _textFillColor;

            // None of target/editTarget selected means there's either nothing placed yet (tool
            // defaults only) OR a live, uncommitted TextBox is open (Text or Edit-Text tool) —
            // _styleTarget is null for the whole duration of a live edit (ClearSelection sets it
            // so on entry). Without pushing onto the live box too, every one of these silently
            // changed only the NEXT box's defaults while leaving the box on screen untouched.
            void ApplyFont(string f)
            {
                _textFontFamily = f;
                if (target is not null) { target.FontName = f; RestyleLive(target); }
                else if (editTarget is not null) { editTarget.FontName = f; RestyleLive(editTarget); }
                else UpdateActiveTextBoxStyle();
            }
            void ApplySize(double v)
            {
                _textFontSize = v;
                if (target is not null) { target.FontSize = v; RestyleLive(target); }
                else if (editTarget is not null) { editTarget.FontSize = v; RestyleLive(editTarget); }
                else UpdateActiveTextBoxStyle();
            }
            void ApplyColor(Color c)
            {
                _textColor = c;
                if (target is not null) { target.SetColor(c); RestyleReselect(target); }
                else if (editTarget is not null) { editTarget.SetColor(c); RestyleReselect(editTarget); }
                else { UpdateActiveTextBoxStyle(); ShowTextSettings(); }
            }
            void ApplyBold(bool on)
            {
                _textBold = on;
                if (target is not null) { target.Bold = on; RestyleReselect(target); }
                else if (editTarget is not null) { editTarget.Bold = on; RestyleReselect(editTarget); }
                else { UpdateActiveTextBoxStyle(); ShowTextSettings(); }
            }
            void ApplyItalic(bool on)
            {
                _textItalic = on;
                if (target is not null) { target.Italic = on; RestyleReselect(target); }
                else if (editTarget is not null) { editTarget.Italic = on; RestyleReselect(editTarget); }
                else { UpdateActiveTextBoxStyle(); ShowTextSettings(); }
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

            // Bold / italic toggle, styled like the shape-kind toggles in ShowShapeSettings.
            void AddStyleToggle(string glyph, bool active, Action<bool> apply, string tip, FontWeight fw, FontStyle fs)
            {
                var btn = new Button
                {
                    // A bare Button with no Style falls back to the OS default chrome, which does
                    // not reliably respect Background/Foreground on this theme — it rendered as an
                    // unreadable solid block. ToolbarButton's Template is a plain
                    // Border+ContentPresenter bound to Background/BorderBrush/Foreground via
                    // TemplateBinding, so it actually shows what's set below.
                    //
                    // Inactive state deliberately does NOT use a literal Transparent background —
                    // it read as an illegible solid black square against the settings bar's own
                    // near-black fill even after the Style fix above. Every other button in the app
                    // that reliably shows its glyph (the main toolbar, ColorPicker's OK/Cancel) uses
                    // a real, if subtle, panel color at rest instead of true transparency.
                    Style = (Style)FindResource("ToolbarButton"),
                    Padding = new Thickness(0),
                    Content = glyph,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontWeight = fw,
                    FontStyle = fs,
                    FontSize = 13,
                    Width = 26, Height = 24,
                    Margin = new Thickness(2, 0, 2, 0),
                    ToolTip = tip,
                    Cursor = Cursors.Hand,
                    // BgHover, not BgPanel: the settings bar's own background is BgPanel now, so
                    // matching it here would blend this control back into the bar.
                    Background = active
                        ? (SolidColorBrush)FindResource("AccentGreenDim")
                        : (SolidColorBrush)FindResource("BgHover"),
                    Foreground = active
                        ? (SolidColorBrush)FindResource("AccentGreen")
                        : (SolidColorBrush)FindResource("TextPrimary"),
                    BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                    BorderThickness = new Thickness(1)
                };
                btn.Click += (_, _) => apply(!active);
                panel.Children.Add(btn);
            }

            // Font family label
            panel.Children.Add(new TextBlock
            {
                Text = "Font:",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });

            // Font family dropdown — a curated, always-installed list (see TextFontFamilies) rather
            // than every system font: every option here is a known-good entry in PdfFontStyle's
            // PS-name map and FontCoverage's fallback chains, so nothing pickable here can fail to
            // embed, and a colleague reopening the same file on a different machine always has it.
            var fontBox = new ComboBox
            {
                Width = 130, Height = 24,
                Style = (Style)FindResource("DarkComboBox"),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            foreach (var family in TextFontFamilies)
                fontBox.Items.Add(family);
            fontBox.SelectedItem = TextFontFamilies.Contains(curFont) ? curFont : TextFontFamilies[0];
            fontBox.SelectionChanged += (_, _) =>
            {
                if (fontBox.SelectedItem is string f) ApplyFont(f);
            };
            panel.Children.Add(fontBox);

            // Separator
            panel.Children.Add(new Rectangle
            {
                Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(8, 2, 8, 2)
            });

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

            // Bold / Italic
            AddStyleToggle("B", curBold, ApplyBold, "Bold", FontWeights.Bold, FontStyles.Normal);
            AddStyleToggle("I", curItalic, ApplyItalic, "Italic", FontWeights.Normal, FontStyles.Italic);

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

            if (editTarget is null)
            {
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
            }

            _textSettingsBar = new Border
            {
                // Was a hardcoded near-black (#1a1a1a) - darker than every other surface in the
                // app's own dark palette and dead wrong in Light/HighContrast (never actually
                // themed). BgPanel is the same resource already used for inputs/panels elsewhere.
                Background = (SolidColorBrush)FindResource("BgPanel"),
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
                // Leaving the Polygon sub-mode mid-placement would strand the vertices; settle
                // them first (see the unfinished-polygon rule above ShapePolyClick).
                if (k != ShapeKind.Polygon) ResolveShapePolygon(commit: true);
                _shapeKind = k;
                // Restyling an existing shape can't convert between the two-point kinds and a
                // polygon — the geometry models are different — so the kind toggles only retarget
                // the tool default when a polygon is (or would become) involved.
                if (target is not null && k != ShapeKind.Polygon && target.Kind != ShapeKind.Polygon)
                { target.Kind = k; RestyleReselect(target); }
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
                    // See the matching comment on ShowTextSettings' AddStyleToggle: a bare Button
                    // with no Style uses the OS default chrome, not this Background/Foreground.
                    Style = (Style)FindResource("ToolbarButton"),
                    Padding = new Thickness(0),
                    Content = glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 14,
                    Width = 26, Height = 24,
                    Margin = new Thickness(2, 0, 2, 0),
                    ToolTip = toolTip,
                    Cursor = Cursors.Hand,
                    // BgHover, not Transparent: the settings bar's own background is BgPanel, so a
                    // literal-transparent button would blend back into it (see ShowTextSettings'
                    // AddStyleToggle, which hit exactly this).
                    Background = curKind == kind
                        ? (SolidColorBrush)FindResource("AccentGreenDim")
                        : (SolidColorBrush)FindResource("BgHover"),
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
            AddKindToggle("\uE734", ShapeKind.Polygon,
                "Freeform polygon \u2014 click to place points, click the first point or double-click to close");

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
                // Was a hardcoded near-black (#1a1a1a) - darker than every other surface in the
                // app's own dark palette and dead wrong in Light/HighContrast (never actually
                // themed). BgPanel is the same resource already used for inputs/panels elsewhere.
                Background = (SolidColorBrush)FindResource("BgPanel"),
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
        // Free-form polygon placement (Shapes tool)
        // ============================================================
        // Upstream KillerPDF v1.6.5's fourth shape sub-mode. Vertices go down click by click on
        // the page the first click landed on; the shape closes when a click lands on the first
        // vertex's snap target (which lights up as the cursor nears it) or on a double-click.
        // Esc abandons the shape, Backspace removes the last vertex.
        //
        // UNFINISHED-POLYGON RULE: anything that takes the document out from under the gesture —
        // switching tool or shape sub-mode, switching tab, changing page, saving / flattening /
        // printing, closing the document — COMMITS a polygon that already has 3+ vertices and
        // DISCARDS anything smaller (which is not a shape yet). Only Esc discards outright. This
        // mirrors how an open text box is committed rather than thrown away (CommitActiveTextBox,
        // which is the chokepoint ResolveShapePolygon hangs off).

        /// <summary>
        /// One Shapes-tool click while the Polygon sub-mode is active: start the shape, add a
        /// vertex, or close it when the click lands on the first vertex's snap target.
        /// </summary>
        private void ShapePolyClick(int pageIdx, Point pos)
        {
            if (_polyVertices.Count == 0)
            {
                ClearSelection();
                _polyPage = pageIdx;

                _polyPreview = new Polyline
                {
                    Stroke = FrozenSolidColorBrush(_shapeStrokeColor),
                    StrokeThickness = _shapeStrokeWidth,
                    StrokeLineJoin = PenLineJoin.Round,
                    IsHitTestVisible = false
                };
                _polyPreview.Points.Add(pos);

                // Rubber band from the last placed vertex to the cursor: same hue, half weight,
                // dashed, so it reads as provisional next to the committed edges.
                _polyRubber = new Polyline
                {
                    Stroke = FrozenSolidColorBrush(Color.FromArgb(
                        (byte)Math.Max(70, _shapeStrokeColor.A / 2),
                        _shapeStrokeColor.R, _shapeStrokeColor.G, _shapeStrokeColor.B)),
                    StrokeThickness = Math.Max(1, _shapeStrokeWidth / 2),
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    IsHitTestVisible = false
                };
                _polyRubber.Points.Add(pos);
                _polyRubber.Points.Add(pos);

                // Snap ring over the first vertex — hidden until a click there would actually close.
                _polySnapDot = new Ellipse
                {
                    Width = 14, Height = 14,
                    StrokeThickness = 2,
                    Stroke = (SolidColorBrush)FindResource("AccentGreen"),
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed
                };
                Canvas.SetLeft(_polySnapDot, pos.X - 7);
                Canvas.SetTop(_polySnapDot, pos.Y - 7);

                _annotationCanvas.Children.Add(_polyPreview);
                _annotationCanvas.Children.Add(_polyRubber);
                _annotationCanvas.Children.Add(_polySnapDot);
                _polyVertices.Add(pos);
                SetStatus("Polygon: click to add points — click the first point or double-click to close, "
                          + "Backspace removes the last point, Esc cancels");
                return;
            }

            if (pageIdx != _polyPage) return;   // the shape stays on the page it started on

            if (_polyVertices.Count >= 3 && (pos - _polyVertices[0]).Length <= ShapePolySnapPx)
            {
                CommitShapePolygon();
                return;
            }

            _polyVertices.Add(pos);
            _polyPreview?.Points.Add(pos);
            if (_polyRubber is not null) _polyRubber.Points[0] = pos;
        }

        /// <summary>
        /// Mouse-move while a polygon is being placed: track the rubber band and light the
        /// first-vertex snap ring once a click there would close the shape.
        /// </summary>
        private void UpdateShapePolyRubber(Point pos)
        {
            if (_polyRubber is not null && _polyRubber.Points.Count > 0)
                _polyRubber.Points[_polyRubber.Points.Count - 1] = pos;
            if (_polySnapDot is not null)
                _polySnapDot.Visibility =
                    _polyVertices.Count >= 3 && (pos - _polyVertices[0]).Length <= ShapePolySnapPx
                        ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Turns the in-progress vertices into a real <see cref="ShapeAnnotation"/>.</summary>
        private void CommitShapePolygon()
        {
            int page = _polyPage;
            var pts = new List<Point>(_polyVertices);
            ResetShapePolyState();   // clears the preview visuals BEFORE the page re-render below

            // A double-click close leaves a duplicate of the vertex the first click placed; drop
            // any trailing point that sits on top of its predecessor.
            while (pts.Count >= 2 && (pts[^1] - pts[^2]).Length < 2) pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3 || page < 0) return;

            var sa = new ShapeAnnotation
            {
                PageIndex = page,
                Kind = ShapeKind.Polygon,
                StrokeWidth = _shapeStrokeWidth,
                HasFill = _shapeHasFill
            };
            sa.Points.AddRange(pts);
            sa.SetStrokeColor(_shapeStrokeColor);
            sa.SetFillColor(_shapeFillColor);
            AddAnnotation(sa);       // pushes the undo snapshot and marks the document dirty
            RenderAllAnnotations(page);
            SetStatus($"Polygon added ({pts.Count} points)");
        }

        /// <summary>
        /// Settles an in-progress polygon so it can never be left dangling. With
        /// <paramref name="commit"/> a shape that already has 3+ vertices is kept; everything
        /// else is discarded. Safe no-op when no polygon is being placed.
        /// </summary>
        private void ResolveShapePolygon(bool commit)
        {
            if (_polyVertices.Count == 0) return;
            if (commit && _polyVertices.Count >= 3) CommitShapePolygon();
            else
            {
                ResetShapePolyState();
                SetStatus("Polygon cancelled");
            }
        }

        /// <summary>Backspace: drop the last placed vertex; removing the only one cancels.</summary>
        private void ShapePolyBackspace()
        {
            if (_polyVertices.Count == 0) return;
            if (_polyVertices.Count == 1) { ResolveShapePolygon(commit: false); return; }
            _polyVertices.RemoveAt(_polyVertices.Count - 1);
            if (_polyPreview is not null && _polyPreview.Points.Count > 0)
                _polyPreview.Points.RemoveAt(_polyPreview.Points.Count - 1);
            if (_polyRubber is not null) _polyRubber.Points[0] = _polyVertices[^1];
            if (_polySnapDot is not null && _polyVertices.Count < 3)
                _polySnapDot.Visibility = Visibility.Collapsed;
        }

        /// <summary>Removes the preview visuals and clears the per-document placement state.</summary>
        private void ResetShapePolyState()
        {
            if (_polyPreview is not null) _annotationCanvas.Children.Remove(_polyPreview);
            if (_polyRubber is not null) _annotationCanvas.Children.Remove(_polyRubber);
            if (_polySnapDot is not null) _annotationCanvas.Children.Remove(_polySnapDot);
            _polyVertices.Clear();
            _polyPreview = null;
            _polyRubber = null;
            _polySnapDot = null;
            _polyPage = -1;
        }

        /// <summary>
        /// True when <paramref name="pos"/> is inside the polygon (even-odd rule) or within
        /// <paramref name="edgeTol"/> of one of its edges, closing edge included.
        /// </summary>
        private static bool HitTestPolygon(IReadOnlyList<Point> pts, Point pos, double edgeTol, bool filled)
        {
            if (pts.Count < 2) return false;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
                if (DistancePointToSegment(pos, pts[j], pts[i]) <= edgeTol) return true;
            if (!filled) return false;
            bool inside = false;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            {
                if (pts[i].Y > pos.Y != pts[j].Y > pos.Y &&
                    pos.X < (pts[j].X - pts[i].X) * (pos.Y - pts[i].Y) / (pts[j].Y - pts[i].Y) + pts[i].X)
                    inside = !inside;
            }
            return inside;
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

        /// <summary>Fallback drawn-signature canvas, mirroring <see cref="SavedSignature"/>'s initializers.</summary>
        private const double DefaultSigCanvasW = 400;
        private const double DefaultSigCanvasH = 150;

        // Upstream v1.7.1 (#181): signatures.json is plain JSON on disk and an explicit 0 in it
        // OVERRIDES SavedSignature's property initializers, so a legacy or hand-edited entry can carry
        // a zero (or non-finite) canvas size. Everything downstream divides by it — the preview scale,
        // the placed annotation's SourceWidth, the resize drag — and ±∞/NaN then gets persisted onto
        // the annotation, after which every later render crashes WPF. Read the dimensions through
        // these so the standard canvas stands in wherever the stored value is unusable.
        private static double SigCanvasW(SavedSignature sig)
            => IsFinitePositive(sig.CanvasWidth) ? sig.CanvasWidth : DefaultSigCanvasW;

        private static double SigCanvasH(SavedSignature sig)
            => IsFinitePositive(sig.CanvasHeight) ? sig.CanvasHeight : DefaultSigCanvasH;

        private void RenderSignaturePreview(Canvas canvas, SavedSignature sig, double targetW, double targetH)
        {
            double sigW = SigCanvasW(sig), sigH = SigCanvasH(sig);
            double scaleX = targetW / sigW;
            double scaleY = targetH / sigH;
            double scale = Math.Min(scaleX, scaleY) * 0.9;

            double offsetX = (targetW - sigW * scale) / 2;
            double offsetY = (targetH - sigH * scale) / 2;

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

            Telemetry.TrackEvent("Annotation.PlaceStarted",
                new Dictionary<string, string> { ["Type"] = "Signature" });
            var sig = _pendingSignature;
            double scale = 0.5;

            var annot = new SignatureAnnotation
            {
                PageIndex = pageIdx,
                Position = pos,
                Scale = scale,
                // #181: never copy an unusable stored canvas size onto the annotation — SourceWidth
                // is a divisor in the resize drag, and a 0 there produces an infinite Scale that is
                // then saved on the annotation and crashes every subsequent render.
                SourceWidth = SigCanvasW(sig),
                SourceHeight = SigCanvasH(sig),
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
            Telemetry.TrackEvent("Annotation.PlaceCompleted",
                new Dictionary<string, string> { ["Type"] = "Signature" });
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
                        FollowLinkTarget(lTarget);
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
                        // Resolve the topmost annotation under the press FIRST, in exactly the old
                        // order — placed annotations (signature / image) outrank the rest — so the
                        // flowing-selection decision below can be made without changing it.
                        PageAnnotation? underPress = null;
                        Rect underPressBounds = Rect.Empty;
                        if (_annotations.TryGetValue(pageIdx, out var pageAnnotsList))
                        {
                            for (int i = pageAnnotsList.Count - 1; i >= 0; i--)
                            {
                                if (pageAnnotsList[i] is PlacedAnnotation pa &&
                                    HitTestAnnotation(pa, pos, out Rect paBounds))
                                {
                                    underPress = pa;
                                    underPressBounds = paBounds;
                                    break;
                                }
                            }
                            // Then non-placed annotations (Shape, Highlight, Ink, Text).
                            if (underPress is null)
                            {
                                for (int i = pageAnnotsList.Count - 1; i >= 0; i--)
                                {
                                    var a = pageAnnotsList[i];
                                    if (a is PlacedAnnotation) continue;
                                    if (a is ShapeAnnotation or HighlightAnnotation or InkAnnotation or TextAnnotation or TextEditAnnotation
                                        && HitTestAnnotation(a, pos, out Rect aBounds))
                                    {
                                        underPress = a;
                                        underPressBounds = aBounds;
                                        break;
                                    }
                                }
                            }
                        }

                        // Flowing text selection (upstream KillerPDF v1.6.5, #127): when the press
                        // lands ON text the character run owns the DRAG, so a paragraph-covering
                        // highlight no longer makes the text underneath unselectable. A plain CLICK
                        // still selects that highlight — resolved on mouse-up via _txtSelClickAnnot.
                        // Everything else keeps drag priority exactly as before, so dragging a
                        // signature, image, shape, ink stroke, or text box that happens to sit over
                        // text still moves it on the first press. Shift and an armed OCR region
                        // capture both force the classic marquee.
                        bool marqueeForced = _ocrRegionMode
                            || (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                        bool textMayWin = underPress is null or HighlightAnnotation;
                        if (!marqueeForced && textMayWin && TryBeginTextSelection(pageIdx, pos))
                        {
                            ClearSelection();
                            RenderAllAnnotations(pageIdx);
                            _txtSelClickAnnot = underPress;
                            _txtSelClickAnnotBounds = underPressBounds;
                            _annotationCanvas.CaptureMouse();
                            e.Handled = true;
                            break;
                        }

                        if (underPress is PlacedAnnotation placed)
                        {
                            ClearSelection();
                            RenderAllAnnotations(pageIdx);
                            SelectAnnotation(placed, underPressBounds);
                            PushPageSnapshot(placed.PageIndex);
                            _isDraggingAnnot = true;
                            _dragAnnotStart = pos;
                            _dragAnnotOrigPos = placed.Position;
                            _dragAnnot = placed;
                            _annotationCanvas.CaptureMouse();
                            e.Handled = true;
                        }
                        else if (underPress is not null)
                        {
                            ClearSelection();
                            RenderAllAnnotations(pageIdx);
                            SelectAnnotation(underPress, underPressBounds);
                            BeginAnnotMove(underPress, pos);
                            e.Handled = true;
                        }
                        else
                        {
                            ClearSelection();
                            ClearTextSelection();
                            _isSelecting = true;
                            _selectStart = pos;
                            _selectRect = new Rectangle
                            {
                                StrokeThickness = 1,
                                Width = 0, Height = 0,
                                IsHitTestVisible = false
                            };
                            // Themed, not hardcoded: MarqueeFill / MarqueeStroke are the selection
                            // accent at marquee alphas, and a resource reference (rather than a
                            // brush snapshot) means the box follows a live theme switch.
                            _selectRect.SetResourceReference(Shape.FillProperty, "MarqueeFill");
                            _selectRect.SetResourceReference(Shape.StrokeProperty, "MarqueeStroke");
                            Canvas.SetLeft(_selectRect, pos.X);
                            Canvas.SetTop(_selectRect, pos.Y);
                            _annotationCanvas.Children.Add(_selectRect);
                            _annotationCanvas.CaptureMouse();
                            e.Handled = true;
                        }
                    }
                    break;

                case EditTool.Text:
                   if (TryRestoreActiveTextBoxFocus(pos))
                   {
                       e.Handled = true;
                       break;
                   }
                    CommitActiveTextBox();
                    // Clicking directly on a text box you already placed almost never means "stack
                    // a second, empty one exactly on top of it" — it means "let me get back into
                    // this one". Select-tool double-click already did this via TryReeditPlacedText;
                    // reported back as a real gap that the Text tool itself didn't, so placing was
                    // easy but coming back to fix a typo meant knowing to switch tools first.
                    if (!TryReeditPlacedText(pos, pageIdx))
                        PlaceTextBox(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.EditText:
                    CommitActiveTextBox();
                    // Same reasoning as the Text tool above: a TextAnnotation placed by Insert Text
                    // is TDPdf's own overlay, not yet part of the PDF's actual content stream (that
                    // only happens at Save), so EditTextAtPosition's PdfPig-based text-run search
                    // could never find it — "Edit Existing Text" is exactly where someone would
                    // naturally try to fix it, and every attempt fell through to "No text found at
                    // this position". Check TDPdf's own overlay first; only fall back to real PDF
                    // content when the click isn't on one of TDPdf's own placed boxes.
                    if (!TryReeditPlacedText(pos, pageIdx))
                        EditTextAtPosition(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.EditImage:
                    CommitActiveTextBox();
                    EditImageAtPosition(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.Highlight:
                case EditTool.Strikethrough:
                case EditTool.Underline:
                {
                    // Markup FLOWS along the character runs exactly like text selection; the release
                    // turns the covered lines into one grouped annotation (upstream KillerPDF
                    // v1.6.5, #127).
                    ClearSelection();
                    ClearTextSelection();
                    if (TryBeginTextSelection(pageIdx, pos))
                    {
                        _txtSelCommitTool = _currentTool;
                        _annotationCanvas.CaptureMouse();
                        e.Handled = true;
                        break;
                    }
                    // Nothing to flow along.
                    //
                    // Strikethrough and Underline are meaningless as a free rectangle, so they only
                    // ever hint — they are new tools with no prior behaviour to preserve.
                    //
                    // The highlighter is different: dragging a rectangle is what it has always done,
                    // and on a scan it is the ONLY thing that works. Upstream could drop that
                    // because their Shapes tool's Box sub-mode explicitly inherited the old
                    // highlighter gesture; ours inherited nothing, so dropping it here would be a
                    // straight capability loss. It therefore keeps the classic drag in both
                    // no-text cases and just SAYS why it is not hugging words.
                    bool hasText = PageHasTextLayer(pageIdx);
                    if (_currentTool != EditTool.Highlight)
                    {
                        SetStatus(hasText ? NoTextHereHint : NoTextLayerHint);
                        e.Handled = true;
                        break;
                    }
                    // Non-blocking explanation, only when the whole page has no text layer; missing
                    // the words on a page that does have text is self-evident and stays silent.
                    if (!hasText) SetStatus(NoTextLayerHighlightHint);
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
                }

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
                    // Freeform polygon: vertices go down click by click instead of being dragged
                    // out, and a double-click on the third-or-later vertex closes the shape (the
                    // first click of the pair already placed a point there — CommitShapePolygon
                    // drops the duplicate).
                    if (_shapeKind == ShapeKind.Polygon)
                    {
                        if (e.ClickCount == 2 && _polyVertices.Count >= 3) CommitShapePolygon();
                        else ShapePolyClick(pageIdx, pos);
                        e.Handled = true;
                        break;
                    }

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
        /// <summary>
        /// Captured geometry of a <see cref="ShapeAnnotation"/>. Carries both geometry models:
        /// Start/End for rectangle / ellipse / line, and <see cref="Points"/> for a polygon
        /// (null for the others), so one capture type covers every shape kind.
        /// </summary>
        private sealed class ShapeGeom
        {
            public Point Start;
            public Point End;
            public double StrokeWidth;
            public List<Point>? Points;
        }

        /// <summary>
        /// Captured geometry of a <see cref="MarkupAnnotation"/>: the union bounds plus the per-line
        /// rects, so a move/resize can be applied relative to the start without the lines drifting
        /// out of step with the bounds.
        /// </summary>
        private sealed class MarkupGeom
        {
            public Rect Bounds;
            public List<Rect> Lines = new();
        }

        private static object CaptureGeometry(PageAnnotation annot) => annot switch
        {
            ShapeAnnotation s => new ShapeGeom
            {
                Start = s.Start, End = s.End, StrokeWidth = s.StrokeWidth,
                Points = s.Kind == ShapeKind.Polygon ? new List<Point>(s.Points) : null
            },
            // Markup must come before HighlightAnnotation — it is a subclass.
            MarkupAnnotation m => new MarkupGeom { Bounds = m.Bounds, Lines = new List<Rect>(m.LineRects) },
            HighlightAnnotation h => h.Bounds,
            InkAnnotation i => new List<Point>(i.Points),
            TextAnnotation t => (Position: t.Position, Width: t.Width, Height: t.Height),
            TextEditAnnotation tea => (Position: tea.Position, Bounds: tea.OriginalBounds),
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
                case ShapeAnnotation s when original is ShapeGeom o:
                    if (o.Points is not null)
                    {
                        if (s.Points.Count != o.Points.Count) return false;
                        for (int i = 0; i < o.Points.Count; i++)
                            if (s.Points[i] != o.Points[i]) return false;
                        return true;
                    }
                    return s.Start == o.Start && s.End == o.End;
                case MarkupAnnotation m when original is MarkupGeom mo:
                    if (m.LineRects.Count != mo.Lines.Count) return false;
                    for (int i = 0; i < mo.Lines.Count; i++)
                        if (m.LineRects[i] != mo.Lines[i]) return false;
                    return m.Bounds == mo.Bounds;
                case HighlightAnnotation h when original is Rect r:
                    return h.Bounds == r;
                case InkAnnotation ink when original is List<Point> pts:
                    if (ink.Points.Count != pts.Count) return false;
                    for (int i = 0; i < pts.Count; i++)
                        if (ink.Points[i] != pts[i]) return false;
                    return true;
                case TextAnnotation t when original is ValueTuple<Point, double, double> tp:
                    return t.Position == tp.Item1 && t.Width == tp.Item2 && t.Height == tp.Item3;
                case TextEditAnnotation tea when original is ValueTuple<Point, Rect> teo:
                    return tea.Position == teo.Item1 && tea.OriginalBounds == teo.Item2;
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
                case ShapeAnnotation s when original is ShapeGeom o:
                    if (o.Points is not null)
                    {
                        s.Points.Clear();
                        foreach (var p in o.Points) s.Points.Add(new Point(p.X + dx, p.Y + dy));
                        break;
                    }
                    s.Start = new Point(o.Start.X + dx, o.Start.Y + dy);
                    s.End   = new Point(o.End.X + dx, o.End.Y + dy);
                    break;
                // Markup carries per-line rects as well as the union bounds; both move together.
                // Matched before HighlightAnnotation — it is a subclass.
                case MarkupAnnotation m when original is MarkupGeom mo:
                    m.LineRects.Clear();
                    foreach (var lr in mo.Lines)
                        m.LineRects.Add(new Rect(lr.X + dx, lr.Y + dy, lr.Width, lr.Height));
                    m.Bounds = new Rect(mo.Bounds.X + dx, mo.Bounds.Y + dy, mo.Bounds.Width, mo.Bounds.Height);
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
                // In-place text edits carry two anchors that must move together: Position (where the
                // replacement glyphs draw) and OriginalBounds (the whiteout + hit-test region). Moving
                // only one would desync the visible text from the box that hides the old content.
                case TextEditAnnotation tea when original is ValueTuple<Point, Rect> teo:
                    tea.Position = new Point(teo.Item1.X + dx, teo.Item1.Y + dy);
                    tea.OriginalBounds = new Rect(
                        teo.Item2.X + dx, teo.Item2.Y + dy, teo.Item2.Width, teo.Item2.Height);
                    break;
            }
        }

        private void ApplyResizeTo(PageAnnotation annot, Point cur, Point start, object original)
        {
            switch (annot)
            {
                case ShapeAnnotation s when original is ShapeGeom o:
                {
                    if (o.Points is not null)
                    {
                        // Polygon: scale the vertices about the bounding box's top-left, exactly
                        // like the ink path below, so the corner handle stretches the whole shape.
                        if (o.Points.Count == 0) break;
                        double pMinX = o.Points.Min(p => p.X), pMinY = o.Points.Min(p => p.Y);
                        double pMaxX = o.Points.Max(p => p.X), pMaxY = o.Points.Max(p => p.Y);
                        double pOrigW = Math.Max(1, pMaxX - pMinX), pOrigH = Math.Max(1, pMaxY - pMinY);
                        double pNewW = Math.Max(4, pOrigW + (cur.X - start.X));
                        double pNewH = Math.Max(4, pOrigH + (cur.Y - start.Y));
                        double psx = pNewW / pOrigW, psy = pNewH / pOrigH;
                        s.Points.Clear();
                        foreach (var p in o.Points)
                            s.Points.Add(new Point(pMinX + (p.X - pMinX) * psx, pMinY + (p.Y - pMinY) * psy));
                        break;
                    }
                    // Anchor to Start; drag End.
                    s.Start = o.Start;
                    s.End = new Point(o.End.X + (cur.X - start.X), o.End.Y + (cur.Y - start.Y));
                    break;
                }
                // Markup: stretch the union box from its top-left and carry every line rect with
                // it proportionally. Matched before HighlightAnnotation — it is a subclass.
                case MarkupAnnotation m when original is MarkupGeom mo:
                {
                    double origW = Math.Max(1, mo.Bounds.Width);
                    double origH = Math.Max(1, mo.Bounds.Height);
                    double newW = Math.Max(4, mo.Bounds.Width + (cur.X - start.X));
                    double newH = Math.Max(4, mo.Bounds.Height + (cur.Y - start.Y));
                    double msx = newW / origW, msy = newH / origH;
                    m.LineRects.Clear();
                    foreach (var lr in mo.Lines)
                        m.LineRects.Add(new Rect(
                            mo.Bounds.X + (lr.X - mo.Bounds.X) * msx,
                            mo.Bounds.Y + (lr.Y - mo.Bounds.Y) * msy,
                            Math.Max(1, lr.Width * msx),
                            Math.Max(1, lr.Height * msy)));
                    m.Bounds = new Rect(mo.Bounds.X, mo.Bounds.Y, newW, newH);
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
                case TextEditAnnotation tea when original is ValueTuple<Point, Rect> teo:
                {
                    // Anchor top-left; drag bottom-right to grow/shrink the whiteout + hit-test box —
                    // lets a default sized too generously (e.g. bleeding into a nearby table border)
                    // be pulled back in by hand. Position (where the replacement text draws) is left
                    // alone, matching the top-left anchor.
                    double newW = Math.Max(16, teo.Item2.Width + (cur.X - start.X));
                    double newH = Math.Max(tea.FontSize + 4, teo.Item2.Height + (cur.Y - start.Y));
                    tea.OriginalBounds = new Rect(teo.Item2.X, teo.Item2.Y, newW, newH);
                    break;
                }
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            // Don't interfere with mouse interaction inside form-field overlays.
            if (e.OriginalSource is DependencyObject moveSrc && IsFormFieldElement(moveSrc))
                return;

            // Link hover: surface the hovered link's target in the status bar. Only on button-up moves so it
            // never fights an in-progress drag (move/resize/pan all hold the left button). Bounds-tested like
            // the click path because transparent overlay canvases aren't reliable WPF hit-test targets.
            if (_linkOverlays.Count > 0 && e.LeftButton == MouseButtonState.Released)
            {
                var hp = e.GetPosition(_annotationCanvas);
                string? hoverTarget = null;
                foreach (var lo in _linkOverlays)
                {
                    double lx = Canvas.GetLeft(lo), ly = Canvas.GetTop(lo);
                    if (hp.X >= lx && hp.X <= lx + lo.Width && hp.Y >= ly && hp.Y <= ly + lo.Height)
                    {
                        object? t = lo.Tag is LinkAnnotInfo lai ? lai.Target : lo.Tag;
                        hoverTarget = t is int gp ? $"Go to page {gp + 1}" : t as string;
                        break;
                    }
                }
                ShowLinkHoverStatus(hoverTarget);
            }

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

            // Shapes tool, freeform polygon: track the rubber band from the last placed vertex and
            // light the first-vertex snap ring. No button is held during placement, so this runs
            // ahead of every drag path below (and after the pan check, which owns middle-drag).
            if (_currentTool == EditTool.Shape && _polyVertices.Count > 0)
            {
                UpdateShapePolyRubber(pos);
                return;
            }

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
                // #181: the divisor and the starting scale both come off the annotation, and an
                // annotation placed from a damaged signatures.json entry could carry 0 or a non-finite
                // value in either. Math.Max does not filter those out (it returns NaN for NaN and ∞
                // for ∞) and the result is written straight back onto the annotation below, so one bad
                // drag used to poison every later render. Substitute the standard canvas instead.
                double srcW = IsFinitePositive(_resizeSigAnnot.SourceWidth)
                    ? _resizeSigAnnot.SourceWidth : DefaultSigCanvasW;
                double startScale = IsFinitePositive(_resizeSigStartScale) ? _resizeSigStartScale : 0.5;
                double newScale = Math.Max(0.05, startScale + delta / srcW);
                _resizeSigAnnot.Scale = newScale;

                // Update selection border and handle position live. SourceHeight gets the same
                // treatment as SourceWidth above: newW/newH feed the border's Width/Height, which WPF
                // rejects outright when either is not a real number.
                double srcH = IsFinitePositive(_resizeSigAnnot.SourceHeight)
                    ? _resizeSigAnnot.SourceHeight : DefaultSigCanvasH;
                double newW = srcW * newScale;
                double newH = srcH * newScale;
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

            // Flowing text selection drag (upstream KillerPDF v1.6.5, #127): move the focus caret
            // and repaint the per-line quads. Runs ahead of the rectangle marquee below — only one
            // of the two can ever be armed.
            if (_txtSelActive)
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    UpdateTextSelectionDrag(pos);
                    return;
                }
                // Capture can be lost without a MouseUp ever arriving (WPF drops it for plenty of
                // reasons). Settle the gesture here rather than letting the selection keep tracking
                // a button that is not held, then fall through to the normal move handling.
                if (_annotationCanvas.IsMouseCaptured) _annotationCanvas.ReleaseMouseCapture();
                FinishTextSelection();
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

            // Flowing text selection release (upstream KillerPDF v1.6.5, #127): commit the run —
            // copy it and keep the quads on screen, or turn it into markup when a markup tool owns
            // the gesture. A click that never passed the drag threshold selects the annotation that
            // was under the press instead.
            if (_txtSelActive)
            {
                if (_annotationCanvas.IsMouseCaptured) _annotationCanvas.ReleaseMouseCapture();
                FinishTextSelection();
                e.Handled = true;
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
                // Same document, reloaded from the crop working file — keep the tab's name, save
                // target, untitled/protected state and recents entry pointing at the user's file.
                await FinishOpenFileAsync(result, cancellationToken, internalReload: true);
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
                        await FinishOpenFileAsync(restoreResult, CancellationToken.None, internalReload: true);
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

        /// <summary>
        /// Converts a rectangle on the annotation canvas into the absolute PDF user-space rectangle
        /// (lower-left origin, positive extents) that <see cref="CropService"/> writes as the page's
        /// /CropBox. The exact inverse of <see cref="PdfRectToCanvas"/>, including the page /Rotate.
        /// </summary>
        /// <remarks>
        /// This used to map as if /Rotate were always 0, so cropping a quarter-turned page cropped a
        /// different region than the one dragged (on a 90-rotated page the drag axes are swapped
        /// relative to user space, so a top-left drag cropped the bottom-left of the sheet).
        ///
        /// The RESULT stays unrotated user space, which is the right contract and needs no change:
        /// PDF 32000-1 7.7.3.3 defines /CropBox — like /MediaBox — in default user space, and /Rotate
        /// is applied by the viewer AFTER the box has been selected. So the page keeps its existing
        /// /Rotate untouched and the crop still lands where the user dragged.
        ///
        /// Inverting PdfRectToCanvas point by point, with fx = rx - box.X and fy = ry - box.Y:
        ///     0 : fx = cx*hs,                fy = box.Height - cy*vs
        ///    90 : fy = cx*hs,                fx = cy*vs
        ///   180 : fx = box.Width  - cx*hs,   fy = cy*vs
        ///   270 : fy = box.Height - cx*hs,   fx = box.Width - cy*vs
        /// where hs/vs are points-per-canvas-pixel on each canvas axis. For 90/270 the bitmap's axes
        /// are swapped — canvas width spans the box's HEIGHT — so the two scales swap with them.
        /// </remarks>
        private Rect CanvasRectToPdfCropRect(int pageIdx, Rect canvasBounds)
        {
            var (renderW, renderH) = _renderDims[pageIdx];
            var page = _doc!.Pages[pageIdx];
            var box = GetVisiblePageBox(page);
            int rot = ((page.Rotate % 360) + 360) % 360;
            bool quarterTurn = rot is 90 or 270;

            double hs = (quarterTurn ? box.Height : box.Width) / renderW;
            double vs = (quarterTurn ? box.Width : box.Height) / renderH;

            // Map both canvas corners; which PDF edge each one becomes depends on the angle, so
            // normalize with min/max at the end rather than assuming an ordering.
            double fxA, fxB, fyA, fyB;
            switch (rot)
            {
                case 90:
                    fyA = canvasBounds.Left * hs;
                    fyB = canvasBounds.Right * hs;
                    fxA = canvasBounds.Top * vs;
                    fxB = canvasBounds.Bottom * vs;
                    break;
                case 180:
                    fxA = box.Width - canvasBounds.Left * hs;
                    fxB = box.Width - canvasBounds.Right * hs;
                    fyA = canvasBounds.Top * vs;
                    fyB = canvasBounds.Bottom * vs;
                    break;
                case 270:
                    fyA = box.Height - canvasBounds.Left * hs;
                    fyB = box.Height - canvasBounds.Right * hs;
                    fxA = box.Width - canvasBounds.Top * vs;
                    fxB = box.Width - canvasBounds.Bottom * vs;
                    break;
                default:
                    fxA = canvasBounds.Left * hs;
                    fxB = canvasBounds.Right * hs;
                    fyA = box.Height - canvasBounds.Top * vs;
                    fyB = box.Height - canvasBounds.Bottom * vs;
                    break;
            }

            double left = box.X + Math.Min(fxA, fxB);
            double right = box.X + Math.Max(fxA, fxB);
            double bottom = box.Y + Math.Min(fyA, fyB);
            double top = box.Y + Math.Max(fyA, fyB);
            return new Rect(left, bottom, right - left, top - bottom);
        }

        // ============================================================
        // Page boxes (/MediaBox, /CropBox)
        // ============================================================

        /// <summary>
        /// A page box in PDF user space: lower-left origin (<see cref="X"/>, <see cref="Y"/>) plus a
        /// size, always normalized so Width/Height are positive. The origin matters — [0 0 612 792] is
        /// the common case but [9 9 621 801] is legal, and content/annotation coordinates are absolute
        /// in user space, so anything mapping into the rendered bitmap must subtract the box origin
        /// rather than assume (0,0). /Rotate is NOT applied; see Transform.cs VisiblePageSize.
        /// </summary>
        private readonly record struct PageBox(double X, double Y, double Width, double Height)
        {
            public double Right => X + Width;
            public double Top   => Y + Height;
        }

        /// <summary>
        /// Reads an inheritable page-tree box (/MediaBox or /CropBox) for a page, walking the /Parent
        /// chain. Both are inheritable page attributes (PDF 32000-1 7.7.3.3): they may live on any
        /// ancestor /Pages node instead of the page itself, and our vendored PdfSharpCore never resolves
        /// inheritance (PdfPage.InheritValues / PdfPages.FlattenPageTree have no callers). Returns null
        /// when no node in the chain carries a usable box.
        /// </summary>
        /// <remarks>
        /// CRITICAL: this reads the RAW dictionary entry and must never be "simplified" to
        /// page.MediaBox / page.CropBox / page.Width. Those getters route through
        /// PdfDictionary.GetRectangle(key, create: true), which (a) returns an EMPTY rectangle for a
        /// box that is only inherited — the caller then falls back to some hardcoded page size and every
        /// overlay on the page is misplaced — and (b) PLANTS an empty /MediaBox|/CropBox [0 0 0 0] into
        /// the page dictionary, which saves to disk and makes Adobe reject the page as "dimensions
        /// out-of-range". That is the same lazy-getter trap as the phantom /Outlines (#103) and the
        /// degenerate /CropBox fixed in v1.18.0.0; see ScrubDegeneratePageBoxes.
        ///
        /// The entry can be a parsed PdfArray (as loaded from disk), a PdfRectangle (GetRectangle stores
        /// its conversion back into the dictionary — "this[key] = value" — so one earlier property read
        /// anywhere in the app replaces the array), or an indirect reference to either. Handle all three.
        /// </remarks>
        private static PageBox? ReadInheritedPageBox(PdfDictionary? node, string key)
        {
            // Depth cap: a malformed file can have a cyclic /Parent chain.
            for (int depth = 0; node is not null && depth < 32; depth++)
            {
                PdfItem? item = node.Elements[key];
                if (item is not null and not PdfArray and not PdfRectangle)
                    item = DerefItemStatic(item);

                if (item is PdfRectangle pr)
                    return Normalize(pr.X1, pr.Y1, pr.X2, pr.Y2);
                if (item is PdfArray { Elements.Count: 4 } arr)
                    return Normalize(arr.Elements.GetReal(0), arr.Elements.GetReal(1),
                                     arr.Elements.GetReal(2), arr.Elements.GetReal(3));

                var parent = node.Elements["/Parent"];
                node = parent is null ? null
                     : parent as PdfDictionary ?? DerefItemStatic(parent) as PdfDictionary;
            }
            return null;

            static PageBox Normalize(double x1, double y1, double x2, double y2) =>
                new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        }

        /// <summary>
        /// The page box a renderer actually draws, and therefore the box every overlay and every
        /// canvas↔PDF mapping must use: the /CropBox when present and usable, otherwise the /MediaBox.
        /// Inheritance-aware and origin-preserving. Mirrors PDFium's own CPDF_Page rules — clip the crop
        /// box to the media box, and fall back to US Letter when a page carries no usable box at all —
        /// because Docnet/PDFium produced the bitmap our overlays sit on, so our geometry must agree
        /// with it rather than with some other notion of "the page size".
        /// </summary>
        private static PageBox GetVisiblePageBox(PdfPage page)
        {
            var media = ReadInheritedPageBox(page, "/MediaBox");
            var crop  = ReadInheritedPageBox(page, "/CropBox");

            // Sub-1pt boxes are degenerate (typically a [0 0 0 0] planted by the lazy getter), never a
            // real page; treat them as absent.
            if (crop is { Width: > 1, Height: > 1 } c)
            {
                if (media is { Width: > 1, Height: > 1 } m)
                {
                    double x1 = Math.Max(c.X, m.X), y1 = Math.Max(c.Y, m.Y);
                    double x2 = Math.Min(c.Right, m.Right), y2 = Math.Min(c.Top, m.Top);
                    if (x2 - x1 > 1 && y2 - y1 > 1) return new PageBox(x1, y1, x2 - x1, y2 - y1);
                    return m;   // crop lies outside the media box: bogus, ignore it
                }
                return c;
            }
            if (media is { Width: > 1, Height: > 1 } mb) return mb;

            // No usable box anywhere in the page tree — a malformed document. PDFium, which rendered the
            // bitmap we are aligning to, substitutes US Letter in exactly this case, so match that instead
            // of inventing a size (in particular A4) that the render never used.
            return new PageBox(0, 0, 612, 792);
        }

        /// <summary>
        /// Maps an annotation /Rect — absolute PDF user-space coordinates, bottom-left origin, always
        /// UNROTATED — onto the canvas/bitmap PDFium rendered for the page, which has the page /Rotate
        /// already applied. Shared by the link and form-field overlays so the two can never drift apart.
        /// </summary>
        /// <param name="box">The rendered page box from <see cref="GetVisiblePageBox"/> (unrotated).</param>
        /// <param name="rotation">Page /Rotate, already normalized to 0/90/180/270.</param>
        private static (double cx, double cy, double cw, double ch) PdfRectToCanvas(
            PageBox box, int rotation, double canvasW, double canvasH,
            double rx1, double ry1, double rx2, double ry2)
        {
            if (rx1 > rx2) (rx1, rx2) = (rx2, rx1);
            if (ry1 > ry2) (ry1, ry2) = (ry2, ry1);

            // Re-express the rect relative to the rendered box's lower-left corner, so a box with a
            // non-zero origin (or a CropBox inset from the MediaBox) doesn't shift every overlay off
            // the drawn page. fx/fy are now in [0, box.Width] x [0, box.Height].
            double fx1 = rx1 - box.X, fy1 = ry1 - box.Y;
            double fx2 = rx2 - box.X, fy2 = ry2 - box.Y;
            double pageW = box.Width, pageH = box.Height;

            // For 90/270 the bitmap's axes are swapped: canvasW spans the box's HEIGHT and canvasH
            // its WIDTH, so the box dimension each canvas axis is divided by swaps with it.
            switch (rotation)
            {
                case 90:  // 90 CW: PDF (x,y) -> canvas (y, x); canvas is pageH-wide x pageW-tall
                    return (fy1         / pageH * canvasW,
                            fx1         / pageW * canvasH,
                            (fy2 - fy1) / pageH * canvasW,
                            (fx2 - fx1) / pageW * canvasH);
                case 180: // both axes flipped; the PDF->canvas y-flip cancels out
                    return ((pageW - fx2) / pageW * canvasW,
                            fy1           / pageH * canvasH,
                            (fx2 - fx1)   / pageW * canvasW,
                            (fy2 - fy1)   / pageH * canvasH);
                case 270: // 270 CW: PDF (x,y) -> canvas (pageH - y, pageW - x)
                    return ((pageH - fy2) / pageH * canvasW,
                            (pageW - fx2) / pageW * canvasH,
                            (fy2 - fy1)   / pageH * canvasW,
                            (fx2 - fx1)   / pageW * canvasH);
                default:  // 0 — standard bottom-left PDF -> top-left canvas
                    return (fx1           / pageW * canvasW,
                            (pageH - fy2) / pageH * canvasH,
                            (fx2 - fx1)   / pageW * canvasW,
                            (fy2 - fy1)   / pageH * canvasH);
            }
        }

        /// <summary>
        /// The exact inverse of <see cref="PdfRectToCanvas"/>, expressed as a matrix to PREPEND to an
        /// <see cref="XGraphics"/> transform: it maps VISUAL-frame points — canvas coordinates scaled to
        /// points, top-left origin, y down, laid out on the box PDFium actually rendered with /Rotate
        /// already applied — onto the frame XGraphics draws in. Prepend it and every subsequent draw call
        /// can keep passing canvas-scaled coordinates unchanged. Null when there is nothing to apply.
        /// </summary>
        /// <param name="rotation">Page /Rotate, already normalized to 0/90/180/270.</param>
        /// <param name="box">The rendered page box from <see cref="GetVisiblePageBox"/> (UNROTATED, and
        /// with its real origin — a /CropBox inset from or offset within the /MediaBox is why the
        /// mapping is not simply a rotation about (0,0)).</param>
        /// <param name="pageHeightPt">
        /// <c>page.Height.Point</c> — the height XGraphics flips about: its Initialize builds
        /// DefaultViewMatrix = [1 0 0 -1 0 pageHeight] from the page size, so a draw at (X, Y) lands at
        /// user-space (X, pageHeightPt - Y). It is passed in rather than derived because PdfSharpCore
        /// reports the SWAPPED media-box dimensions for a page whose /Rotate is 90/270 (PdfPage's
        /// dictionary ctor sets _orientation = Landscape), so "page height" there is really the visual
        /// height. Every case below is written as "pageHeightPt minus the user-space y we want", so the
        /// value cancels out of the result: a page whose /MediaBox is unreadable — the empty [0 0 0 0]
        /// the lazy getter plants — still burns in the right place.
        /// </param>
        private static XMatrix? VisualToPageMatrix(int rotation, PageBox box, double pageHeightPt)
        {
            // Inverting PdfRectToCanvas point-by-point gives visual (vx, vy) -> PDF user space:
            //    0 : (box.X + vx,            box.Y + box.Height - vy)
            //   90 : (box.X + vy,            box.Y + vx)
            //  180 : (box.X + box.Width - vx, box.Y + vy)
            //  270 : (box.X + box.Width - vy, box.Y + box.Height - vx)
            // XGraphics then applies (X, Y) -> (X, pageHeightPt - Y), so this matrix has to produce
            // X = user x and Y = pageHeightPt - user y. XMatrix is (m11, m12, m21, m22, dx, dy) with
            // x' = x*m11 + y*m21 + dx and y' = x*m12 + y*m22 + dy.
            double atTop    = pageHeightPt - box.Top;   // Y for a user-space y at the box's top edge
            double atBottom = pageHeightPt - box.Y;     // ...and at its bottom edge
            switch (rotation)
            {
                case 90:  return new XMatrix(0, -1, 1, 0, box.X,     atBottom);
                case 180: return new XMatrix(-1, 0, 0, -1, box.Right, atBottom);
                case 270: return new XMatrix(0, 1, -1, 0, box.Right, atTop);
                default:
                    // Unrotated page whose rendered box is the whole media box at the origin: the
                    // matrix is the identity XGraphics already applies, so emit nothing and keep the
                    // content stream byte-identical to what earlier builds wrote.
                    return box.X == 0 && atTop == 0 ? null : new XMatrix(1, 0, 0, 1, box.X, atTop);
            }
        }

        // ============================================================
        // Selection
        // ============================================================

        private bool HitTestAnnotation(PageAnnotation annot, Point pos, out Rect bounds)
        {
            switch (annot)
            {
                // Markup is grabbed by its individual LINE rects, not the union box, so the gaps a
                // multi-line run leaves in the margins stay click-through. Matched before
                // HighlightAnnotation — it is a subclass. The reported bounds are still the union,
                // so the selection border and resize handle wrap the whole run.
                case MarkupAnnotation mk:
                    bounds = mk.Bounds;
                    if (mk.LineRects.Count == 0) return bounds.Contains(pos);
                    foreach (var lr in mk.LineRects)
                        if (lr.Contains(pos)) return true;
                    return false;

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
                    if (shp.Kind == ShapeKind.Polygon)
                    {
                        // Outline polygons are grabbed by their edges; filled ones anywhere inside.
                        return HitTestPolygon(shp.Points, pos,
                                              Math.Max(6.0, shp.StrokeWidth + 4), shp.HasFill);
                    }
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
            // #181: the selection chrome is sized from these bounds and WPF rejects NaN/infinity on
            // Width/Height, so a caller that passed geometry derived from a malformed annotation — or
            // Rect.Empty, which WPF itself defines as (+∞, +∞, -∞, -∞) — used to crash here rather
            // than merely draw nothing. Collapse anything non-finite to a degenerate box: the
            // annotation still becomes the selection, so Delete and the style bar keep working.
            if (!IsFinite(bounds.X) || !IsFinite(bounds.Y) ||
                !IsFinite(bounds.Width) || !IsFinite(bounds.Height))
                bounds = new Rect(IsFinite(bounds.X) ? bounds.X : 0,
                                  IsFinite(bounds.Y) ? bounds.Y : 0, 0, 0);
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
            else if (annot is ShapeAnnotation or HighlightAnnotation or InkAnnotation or TextAnnotation or TextEditAnnotation)
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
                        ShapeKind.Polygon => "polygon",
                        _ => "shape"
                    },
                    MarkupAnnotation mk => mk.Style switch
                    {
                        MarkupStyle.Strikethrough => "strikethrough",
                        MarkupStyle.Underline => "underline",
                        _ => "highlight"
                    },
                    HighlightAnnotation => "highlight",
                    InkAnnotation => "drawing",
                    TextAnnotation => "text box",
                    TextEditAnnotation => "edited text",
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
                case TextEditAnnotation:
                    HideShapeSettings(); HideDrawSettings(); ShowTextSettings();
                    break;
                case ShapeAnnotation:
                    HideTextSettings(); HideDrawSettings(); ShowShapeSettings();
                    break;
                case InkAnnotation:
                    HideTextSettings(); HideShapeSettings(); ShowDrawSettings(EditTool.Draw);
                    break;
                // Markup must be matched before HighlightAnnotation — it is a subclass.
                case MarkupAnnotation mk:
                    HideTextSettings(); HideShapeSettings(); ShowDrawSettings(ToolForMarkupStyle(mk.Style));
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
            // Polygon: a vertex is always ON the outline, so it satisfies the edge test whether or
            // not the shape is filled (the box centre would miss a concave outline polygon).
            ShapeAnnotation s when s.Kind == ShapeKind.Polygon && s.Points.Count > 0 => s.Points[0],
            ShapeAnnotation s => s.Kind == ShapeKind.Line
                ? new Point((s.Start.X + s.End.X) * 0.5, (s.Start.Y + s.End.Y) * 0.5)
                : new Point(s.Bounds.X + s.Bounds.Width * 0.5, s.Bounds.Y + s.Bounds.Height * 0.5),
            // Markup: the union box's top-left can fall in a margin gap between lines, so use the
            // first line rect instead. Matched before HighlightAnnotation — it is a subclass.
            MarkupAnnotation m when m.LineRects.Count > 0
                => new Point(m.LineRects[0].X + 1, m.LineRects[0].Y + 1),
            HighlightAnnotation h => new Point(h.Bounds.X + 1, h.Bounds.Y + 1),
            InkAnnotation i when i.Points.Count > 0 => i.Points[0],
            TextAnnotation t => new Point(t.Position.X + 1, t.Position.Y + 1),
            TextEditAnnotation tea => new Point(tea.OriginalBounds.X + 1, tea.OriginalBounds.Y + 1),
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

        /// <summary>
        /// Ctrl+A: select every character on the current page. Since upstream KillerPDF v1.6.5
        /// (#127) this paints REAL per-line quads through the flowing-selection model instead of
        /// one canvas-sized rectangle, so what is shown is exactly what was copied.
        /// </summary>
        private void SelectAllText()
        {
            if (_currentFile is null) return;
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;

            try
            {
                var runs = _textRuns.GetPage(_currentFile, pageIdx);
                if (runs is null || runs.Chars.Count == 0)
                {
                    SetStatus("No text found on this page");
                    return;
                }

                ClearTextSelection();
                _txtSelAnchor = (pageIdx, 0);
                _txtSelFocus = (pageIdx, runs.Chars.Count);
                _txtSelHasRange = true;
                RepaintTextSelection();

                _selectedText = TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _);
                if (string.IsNullOrWhiteSpace(_selectedText))
                {
                    ClearTextSelection();
                    SetStatus("No text found on this page");
                    return;
                }
                Clipboard.SetText(_selectedText);
                SetStatus("Selected all text - copied to clipboard");
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
            // Flowing selection (upstream KillerPDF v1.6.5, #127) shares this teardown, so every
            // existing caller — tool switch, page change, tab switch, double-click, Escape — drops
            // the quads and the caret range too.
            _txtSelActive = false;
            _txtSelHasRange = false;
            _txtSelDragStarted = false;
            _txtSelCommitTool = null;
            _txtSelClickAnnot = null;
            RemoveTextSelQuads();
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
        ///
        /// Deliberately NOT column-aware, unlike the flowing selection (#185). This serves the
        /// rectangle marquee, where the user has already drawn the region by hand: the words are an
        /// arbitrary geometric subset, so there is no page text width to measure "spans most of the
        /// width" against and no way to tell a marquee that deliberately crossed a gutter from one
        /// that did not. Drag inside a single column and a pure row sweep is already the right
        /// answer; to read a whole two-column page in order, use the flowing selection instead.
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
                    // Seed the tool-default style fields from what's actually on this run, so the
                    // style bar (shown below) reflects its real style rather than stale leftovers
                    // from whatever was last edited — same as PlaceTextBox's re-edit path (#135).
                    _textFontFamily = existingEdit.FontName;
                    _textFontSize = existingEdit.FontSize;
                    _textBold = existingEdit.Bold;
                    _textItalic = existingEdit.Italic;
                    _textColor = existingEdit.GetColor();

                    var reb = existingEdit.OriginalBounds;
                    var retb = new TextBox
                    {
                        Text = existingEdit.NewContent,
                        Background = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
                        Foreground = new SolidColorBrush(_textColor),
                        BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                        BorderThickness = new Thickness(2),
                        FontFamily = new FontFamily(existingEdit.FontName),
                        FontSize = Math.Max(existingEdit.FontSize, 10),
                        FontWeight = existingEdit.Bold ? FontWeights.Bold : FontWeights.Normal,
                        FontStyle = existingEdit.Italic ? FontStyles.Italic : FontStyles.Normal,
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
                            Bold = existingEdit.Bold,
                            Italic = existingEdit.Italic,
                            ExistingAnnotation = existingEdit
                        }
                    };
                    Canvas.SetLeft(retb, reb.X);
                    Canvas.SetTop(retb, reb.Y);
                    _textEditorCanvas.Children.Add(retb);
                    _activeTextBox = retb;
                    // Neither this re-edit branch nor the fresh-hit branch below ever called this —
                    // the style bar (font/color/bold/italic) never appeared for the Edit-Text tool
                    // at all, which is what made it look like it "didn't reappear" on reselect.
                    ShowTextSettings();
                    var rewo = new Rectangle
                    {
                        Fill = Brushes.White,
                        Width = reb.Width + 2,
                        Height = reb.Height + 2,
                        IsHitTestVisible = false,
                        Tag = "EditWhiteout"
                    };
                    Canvas.SetLeft(rewo, reb.X - 1);
                    Canvas.SetTop(rewo, reb.Y - 1);
                    _textEditorCanvas.Children.Insert(_textEditorCanvas.Children.IndexOf(retb), rewo);
                    retb.KeyDown += EditTextBox_KeyDown;
                    FocusTextEditorWhenLoaded(retb, selectAll: true, EditTextBox_LostFocus);
                    SetStatus("Re-editing text — Enter to save, Escape to cancel");
                    return;
                }
            }

            try
            {
                var (renderW, renderH) = _renderDims[pageIdx];
                var hit = _contentEditor.FindTextRunAt(_currentFile, pageIdx, canvasPos, renderW, renderH);
                if (hit is null) { SetStatus("No text found at this position"); return; }

                // Seed the tool-default style fields from what was detected on this run (see the
                // matching comment in the re-edit branch above). TextRunHit doesn't carry a
                // detected color — PDF text color isn't recovered here — so black, matching the
                // hardcoded Foreground this replaces.
                _textFontFamily = hit.FontName;
                _textFontSize = hit.FontSize;
                _textBold = hit.Bold;
                _textItalic = hit.Italic;
                _textColor = Colors.Black;

                // Show editable TextBox over the line
                var tb = new TextBox
                {
                    Text = hit.Text,
                    Background = FrozenSolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
                    Foreground = new SolidColorBrush(_textColor),
                    BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                    BorderThickness = new Thickness(2),
                    FontFamily = new FontFamily(hit.FontName),
                    FontSize = hit.FontSize,
                    // PDF fonts encode bold/italic in the font name; leaving WPF to default these to
                    // Normal made every styled line go plain the moment it was double-clicked (#182).
                    FontWeight = hit.Bold ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle = hit.Italic ? FontStyles.Italic : FontStyles.Normal,
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
                        FontName = hit.FontName,
                        Bold = hit.Bold,
                        Italic = hit.Italic
                    }
                };
                Canvas.SetLeft(tb, hit.CanvasBounds.X);
                Canvas.SetTop(tb, hit.CanvasBounds.Y);
                _textEditorCanvas.Children.Add(tb);
                _activeTextBox = tb;
                ShowTextSettings();

                // Show white-out behind the edit box so original text is hidden
                var whiteout = new Rectangle
                {
                    Fill = Brushes.White,
                    Width = hit.CanvasBounds.Width + 2,
                    Height = hit.CanvasBounds.Height + 2,
                    IsHitTestVisible = false,
                    Tag = "EditWhiteout"
                };
                Canvas.SetLeft(whiteout, hit.CanvasBounds.X - 1);
                Canvas.SetTop(whiteout, hit.CanvasBounds.Y - 1);
                int tbIdx = _textEditorCanvas.Children.IndexOf(tb);
                _textEditorCanvas.Children.Insert(tbIdx, whiteout);

                tb.KeyDown += EditTextBox_KeyDown;
                FocusTextEditorWhenLoaded(tb, selectAll: true, EditTextBox_LostFocus);

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
            /// <summary>Face styling detected on the source PDF text (#182).</summary>
            public bool Bold { get; set; }
            public bool Italic { get; set; }
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
            if (sender is TextBox tb
                && ReferenceEquals(_activeTextBox, tb)
                && tb.Tag is TextEditContext)
            {
                _ = Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () =>
                    {
                        if (!ReferenceEquals(_activeTextBox, tb)) return;
                        // Same reasoning as TextBox_LostFocus: the style bar is part of this same
                        // edit, not a click-away.
                        if (Keyboard.FocusedElement is DependencyObject nf && _textSettingsBar is not null
                            && IsDescendantOf(nf, _textSettingsBar))
                            return;
                        CommitTextEdit();
                    });
            }
        }

        private void CancelTextEdit()
        {
            if (_activeTextBox is null) return;
            var tb = _activeTextBox;
            _activeTextBox = null;
            RemoveTextEditorElement(tb);
            // Remove the whiteout rectangle
            var whiteout = _textEditorCanvas.Children.OfType<Rectangle>()
                .FirstOrDefault(r => r.Tag is string s && s == "EditWhiteout");
            if (whiteout is not null)
               _textEditorCanvas.Children.Remove(whiteout);
            SetStatus("Text edit cancelled");
        }

        private void CommitTextEdit()
        {
            if (_activeTextBox is null || _activeTextBox.Tag is not TextEditContext ctx) return;
            var tb = _activeTextBox;
            _activeTextBox = null;
            string newText = tb.Text.Trim();
            RemoveTextEditorElement(tb);

            // Remove the whiteout rectangle
            var whiteout = _textEditorCanvas.Children.OfType<Rectangle>()
                .FirstOrDefault(r => r.Tag is string s && s == "EditWhiteout");
            if (whiteout is not null)
               _textEditorCanvas.Children.Remove(whiteout);

            if (string.IsNullOrEmpty(newText))
            {
                SetStatus("Text edit cancelled (empty)");
                return;
            }

            // The style bar can change font/size/color/bold/italic without the wording changing at
            // all (e.g. just recoloring existing text) — bailing out purely on unchanged TEXT used
            // to silently discard every style-only edit before it ever reached the code below.
            Color tbColor = tb.Foreground is SolidColorBrush scb ? scb.Color : Colors.Black;
            bool styleChanged = tb.FontFamily.Source != ctx.FontName
                || Math.Abs(tb.FontSize - ctx.FontSize) > 0.01
                || (tb.FontWeight == FontWeights.Bold) != ctx.Bold
                || (tb.FontStyle == FontStyles.Italic) != ctx.Italic
                || tbColor != (ctx.ExistingAnnotation?.GetColor() ?? Colors.Black);
            if (newText == ctx.OriginalText && !styleChanged)
            {
                SetStatus("No changes made");
                return;
            }

            if (ctx.ExistingAnnotation is not null)
            {
                // Update the existing annotation in place — avoids duplicate whiteout layers.
                // Style is read back off the live box, not the pre-edit ctx values — the user may
                // have changed font/size/color/bold/italic in the style bar mid-edit, and the box
                // in front of them is the truth (same reasoning as CommitActiveTextBox's #135 fix).
                PushPageSnapshot(ctx.ExistingAnnotation.PageIndex);
                ctx.ExistingAnnotation.NewContent = newText;
                ctx.ExistingAnnotation.FontSize = tb.FontSize;
                ctx.ExistingAnnotation.FontName = tb.FontFamily.Source;
                ctx.ExistingAnnotation.Bold = tb.FontWeight == FontWeights.Bold;
                ctx.ExistingAnnotation.Italic = tb.FontStyle == FontStyles.Italic;
                ctx.ExistingAnnotation.SetColor(tbColor);
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
                    FontSize = tb.FontSize,
                    FontName = tb.FontFamily.Source,
                    Bold = tb.FontWeight == FontWeights.Bold,
                    Italic = tb.FontStyle == FontStyles.Italic
                };
                edit.SetColor(tbColor);
                AddAnnotation(edit);
            }
            RenderAllAnnotations(ctx.PageIndex);
            SetStatus($"Text edited: \"{ctx.OriginalText}\" -> \"{newText}\"");
            // #168: the in-place editor starts from whatever the PDF already says, so it is the path
            // most likely to carry non-Latin text. Warn on the family the burn will actually use.
            WarnIfGlyphsWillBeLost(ctx.ExistingAnnotation?.FontName ?? ctx.FontName, newText);
        }

        /// <summary>
        /// #168: the editor borrows glyphs from any installed font, so text ALWAYS looks right while
        /// it is being typed - but a PDF can only EMBED whole fonts, and a character no installed
        /// font carries becomes an empty box in the saved file. That used to be invisible until the
        /// user saved, closed and reopened. Say it at the moment the text is placed, while it can
        /// still be fixed.
        ///
        /// Only fires when the whole fallback chain comes up short (a box mixing two non-Latin
        /// scripts, or a script with no font installed at all), so it does not nag: ordinary
        /// Japanese, Chinese, Korean, Thai or Devanagari text resolves silently.
        /// </summary>
        private void WarnIfGlyphsWillBeLost(string preferredFamily, string? text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // FontCoverage.PickFamily/UncoveredChars are synchronous disk I/O on a cache miss (its
            // own comment: "a miss costs a full read of the font file, and a CJK collection is
            // tens of megabytes"). This is called from CommitActiveTextBox/CommitTextEdit, which
            // run directly on the UI thread from the mouse-click handler that committed the box —
            // so a cold-cache lookup blocked every click on the page for however long that read
            // took, felt like a hang, and any clicks made during it queued up and landed on
            // whatever the UI looked like once it unblocked. Off the UI thread entirely; only the
            // status text and dialog (already deferred below) touch it.
            Task.Run(() =>
            {
                string missing;
                try
                {
                    string family = FontCoverage.PickFamily(preferredFamily, text);
                    missing = FontCoverage.UncoveredChars(family, text);
                }
                catch { return; /* the warning must never be the thing that breaks placing text */ }
                if (missing.Length == 0) return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (!IsLoaded || PresentationSource.FromVisual(this) is null) return;
                        SetStatus($"No installed font can draw: {missing} - these will save as empty boxes");
                        // Deferred rather than shown inline: CommitActiveTextBox / CommitTextEdit
                        // are the app's "settle any in-progress edit" chokepoint and run from
                        // inside save, print, close, tool-switch and tab-switch paths. A modal
                        // dialog on that stack would block the operation that asked for the settle.
                        // Background priority lets the caller finish, then raises the warning.
                        TdpDialog.Show(this,
                            "Some characters in this text have no glyph in any installed font:\n\n" +
                            missing + "\n\n" +
                            "They look right while you type, because Windows borrows a glyph per " +
                            "character from across your whole font set. A PDF can only embed whole " +
                            "fonts, so these will save as empty boxes.\n\n" +
                            "Installing a font that covers this script will fix it.",
                            "TDPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    catch { /* window went away between the commit and the dispatch */ }
                }), System.Windows.Threading.DispatcherPriority.Background);
            });
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
            // #135: deliberately NOT PageImage.Source — that may be the inverted display copy, and
            // this capture is baked into the saved PDF.
            if (_primaryPageBitmap is not BitmapSource source) return null;

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
            menu.Items.Add(MakeMenuItem("Replace Image...", (s, e) => ReplaceImageEdit(edit), null, null, "\uE91B"));
            menu.Items.Add(MakeMenuItem("Delete Image", (s, e) => DeleteImageEdit(edit), null, null, "\uE74D"));
            menu.Items.Add(MakeMenuItem("Reset Size", (s, e) => ResetImageEditSize(edit), null, null, "\uE72C"));
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
            CommitActiveTextBox();
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

        private bool TryRestoreActiveTextBoxFocus(Point pos)
        {
            if (_activeTextBox is null || !ReferenceEquals(_activeTextBox.Parent, _textEditorCanvas))
                return false;

            TextBox textBox = _activeTextBox;
            double x = Canvas.GetLeft(textBox);
            double y = Canvas.GetTop(textBox);
            if (!IsFinite(x) || !IsFinite(y)) return false;

            double width = textBox.ActualWidth > 0 ? textBox.ActualWidth : textBox.Width;
            double height = textBox.ActualHeight > 0 ? textBox.ActualHeight : Math.Max(textBox.MinHeight, 24);
            if (pos.X < x || pos.X > x + width || pos.Y < y || pos.Y > y + height)
                return false;

            textBox.Focus();
            Keyboard.Focus(textBox);
            int characterIndex = textBox.GetCharacterIndexFromPoint(
                new Point(pos.X - x, pos.Y - y),
                snapToText: true);
            if (characterIndex >= 0)
                textBox.CaretIndex = characterIndex;

            Telemetry.TrackEvent("Annotation.TextEditorFocusRestored",
                new Dictionary<string, string>
                {
                    ["Type"] = "Text",
                    ["Focused"] = textBox.IsKeyboardFocusWithin ? "true" : "false"
                });
            return true;
        }

        private void RemoveTextEditorElement(UIElement element)
        {
            if (element is FrameworkElement { Parent: Panel parent })
                parent.Children.Remove(element);
            // The move grip (see PlaceTextBox) only ever exists alongside the active placed-text
            // editor, so whichever path is tearing that editor down also owns tearing this down —
            // one blanket cleanup here instead of touching every call site individually.
            if (_activeTextBoxGrip is { Parent: Panel gripParent } grip)
                gripParent.Children.Remove(grip);
            _activeTextBoxGrip = null;
        }

        private static Dictionary<string, string> TextEditorTelemetry(string outcome, string? via = null)
        {
            var props = new Dictionary<string, string>
            {
                ["Type"] = "Text",
                ["Outcome"] = outcome
            };
            // #129: which method asked for the commit. Four releases (1.23.3 – 1.23.6) chased this
            // as a focus bug on the strength of PlaceCompleted{Focused=true} followed ~30ms later by
            // TextEditorClosed{Outcome=Empty}; focus was never the problem, an unidentified caller of
            // the commit chokepoint was. A C# method name is a compile-time constant, so this cannot
            // leak document or user content and needs no scrubbing.
            if (!string.IsNullOrEmpty(via)) props["Via"] = via;
            return props;
        }

        /// <summary>
        /// How long a freshly placed, still-untouched text editor is protected from an
        /// <em>incidental</em> commit. See <see cref="CommitActiveTextBox"/>.
        /// </summary>
        private const double UntouchedEditorGraceMs = 400;

        /// <summary>When the live placed-text editor was created, for the grace window above.</summary>
        private DateTime _activeTextBoxPlacedUtc = DateTime.MinValue;

        /// <summary>Set on the first <c>TextChanged</c>, i.e. once the user has actually typed.</summary>
        private bool _activeTextBoxTouched;

        private void FocusTextEditorWhenLoaded(
            TextBox textBox,
            bool selectAll,
            RoutedEventHandler lostFocusHandler,
            Action<bool, bool>? completed = null)
        {
            bool completionReported = false;
            void Activate()
            {
                bool attached = ReferenceEquals(_activeTextBox, textBox)
                    && ReferenceEquals(textBox.Parent, _textEditorCanvas);
                if (attached)
                {
                    textBox.Focus();
                    Keyboard.Focus(textBox);
                    if (selectAll) textBox.SelectAll();
                    else textBox.CaretIndex = textBox.Text.Length;
                }

                if (!completionReported)
                {
                    completionReported = true;
                    completed?.Invoke(attached, textBox.IsKeyboardFocusWithin);
                }
            }

            textBox.LostFocus += lostFocusHandler;
            RoutedEventHandler? loadedHandler = null;
            loadedHandler = (_, _) =>
            {
                textBox.Loaded -= loadedHandler;
                Activate();
            };
            textBox.Loaded += loadedHandler;
            // Loaded may already have fired by the time a dynamically-added editor is wired up.
            // The unconditional fallback is idempotent and guarantees every editor gets an
            // activation attempt after the current mouse/layout pass either way.
            _ = textBox.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                (Action)Activate);
        }

        /// <summary>
        /// Opens the in-place editing TextBox for a new text annotation, or (when <paramref name="existing"/>
        /// is supplied) re-opens an already-placed one seeded with its content/size/colour/width/fill.
        /// </summary>
        private void PlaceTextBox(Point pos, int pageIdx, TextAnnotation? existing = null)
        {
            // #131: every caller offers the live editor to the commit chokepoint before getting
            // here. If one is STILL live, the untouched-editor grace refused that commit — and the
            // assignment below is about to overwrite _activeTextBox, stranding the refused box on
            // TextEditorCanvas with nothing referencing it. Nothing reaps that canvas except a tab
            // switch, so it would sit on the page, visible and unusable, for the rest of the
            // session. A brand-new editor nobody typed into has nothing worth preserving; discard
            // it. (Only a placed-text box can be in that state — the grace never defers an inline
            // PDF-text edit, and the inline path returns rather than replacing the editor.)
            //
            // Discard EXACTLY what the grace refused, and settle anything else properly. The
            // second branch is unreachable today for the reason above, but "we are about to
            // overwrite the field" must never become a licence to throw away text somebody typed.
            if (_activeTextBox is { } stale)
            {
                if (!_activeTextBoxTouched
                    && stale.Tag is PlacedTextContext { Existing: null }
                    && string.IsNullOrWhiteSpace(stale.Text))
                {
                    _activeTextBox = null;
                    RemoveTextEditorElement(stale);
                    Telemetry.TrackEvent("Annotation.TextEditorClosed",
                        TextEditorTelemetry("Empty", nameof(PlaceTextBox)));
                }
                else
                {
                    CommitActiveTextBox();
                }
            }

            double width = DefaultTextBoxWidth;
            if (existing is not null)
            {
                // Adopt the box's style so the box (and the Text-tool settings bar, if visible) reflect it.
                _textColor = existing.GetColor();
                _textFontSize = existing.FontSize;
                _textFontFamily = existing.FontName;
                _textWhiteout = existing.HasFill;
                _textBold = existing.Bold;              // #135
                _textItalic = existing.Italic;
                _textUnderline = existing.Underline;
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
                FontFamily = new FontFamily(_textFontFamily),
                FontSize = _textFontSize,
                // #135: a WPF TextBox carries all three natively, so the editor shows the real thing
                // rather than a preview of it.
                FontWeight = _textBold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = _textItalic ? FontStyles.Italic : FontStyles.Normal,
                TextDecorations = _textUnderline ? TextDecorations.Underline : null,
                CaretBrush = new SolidColorBrush(_textColor),
                SelectionBrush = (SolidColorBrush)FindResource("AccentGreen"),
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
            double maxX = Math.Max(0, _textEditorCanvas.Width - width);
            double maxY = Math.Max(0, _textEditorCanvas.Height - Math.Max(tb.MinHeight, 24));
            Canvas.SetLeft(tb, Math.Clamp(pos.X, 0, maxX));
            Canvas.SetTop(tb, Math.Clamp(pos.Y, 0, maxY));
            Telemetry.TrackEvent("Annotation.PlaceStarted",
                new Dictionary<string, string> { ["Type"] = "Text" });
            _activeTextBox = tb;
            _activeTextBoxPlacedUtc = DateTime.UtcNow;
            _activeTextBoxTouched = false;
            tb.KeyDown += TextBox_KeyDown;
            tb.PreviewMouseLeftButtonDown += (_, _) =>
            {
                tb.Focus();
                Keyboard.Focus(tb);
            };
            bool inputStarted = false;
            tb.TextChanged += (_, _) =>
            {
                if (ReferenceEquals(_activeTextBox, tb)) _activeTextBoxTouched = true;
                if (inputStarted) return;
                inputStarted = true;
                Telemetry.TrackEvent("Annotation.TextEditorInputStarted",
                    new Dictionary<string, string> { ["Type"] = "Text" });
            };
            SetStatus("Type your text, then press Enter to place it (Shift+Enter for a new line)");
            FocusTextEditorWhenLoaded(
                tb,
                selectAll: existing is not null,
                TextBox_LostFocus,
                (attached, focused) =>
                {
                    Telemetry.TrackEvent("Annotation.PlaceCompleted",
                        new Dictionary<string, string>
                        {
                            ["Type"] = "Text",
                            ["Attached"] = attached ? "true" : "false",
                            ["Focused"] = focused ? "true" : "false"
                        });
                });
            _textEditorCanvas.Children.Add(tb);

            // Telemetry from the field (session 3d521d3552e14bb0b9853db69306e844, 1.29.2.0) showed
            // the actual failure: every TextEditorClosed while the box was still empty fired with
            // Via=Canvas_MouseLeftButtonDown, over and over, a couple of seconds apart — someone
            // repeatedly clicking near a just-placed, still-empty box trying to reposition it before
            // typing. Clicking a TextBox only moves the caret; there was and is no way to drag it
            // while it's still the live editor, so every attempt discarded the box (a click while
            // EditTool.Text is active commits-then-places-a-new-one) and started over in the same
            // spot. This grip is a real drag target for exactly that moment, before Select-tool
            // auto-select (see CommitActiveTextBox) ever gets a chance to help.
            var grip = new Border
            {
                Width = 16,
                Height = 16,
                Background = (SolidColorBrush)FindResource("AccentGreen"),
                CornerRadius = new CornerRadius(8),
                Cursor = Cursors.SizeAll,
                ToolTip = "Drag to move this box"
            };
            void PositionGrip(double left, double top)
            {
                Canvas.SetLeft(grip, left - 8);
                Canvas.SetTop(grip, top - 8);
            }
            PositionGrip(Canvas.GetLeft(tb), Canvas.GetTop(tb));
            bool draggingBox = false;
            Point dragAnchorScreen = default;
            double dragStartLeft = 0, dragStartTop = 0;
            grip.PreviewMouseLeftButtonDown += (_, ev) =>
            {
                draggingBox = true;
                dragAnchorScreen = ev.GetPosition(_textEditorCanvas);
                dragStartLeft = Canvas.GetLeft(tb);
                dragStartTop = Canvas.GetTop(tb);
                grip.CaptureMouse();
                ev.Handled = true;
            };
            grip.PreviewMouseMove += (_, ev) =>
            {
                if (!draggingBox) return;
                var now = ev.GetPosition(_textEditorCanvas);
                double gMaxX = Math.Max(0, _textEditorCanvas.Width - tb.Width);
                double gMaxY = Math.Max(0, _textEditorCanvas.Height - Math.Max(tb.MinHeight, 24));
                double newLeft = Math.Clamp(dragStartLeft + (now.X - dragAnchorScreen.X), 0, gMaxX);
                double newTop = Math.Clamp(dragStartTop + (now.Y - dragAnchorScreen.Y), 0, gMaxY);
                Canvas.SetLeft(tb, newLeft);
                Canvas.SetTop(tb, newTop);
                PositionGrip(newLeft, newTop);
            };
            grip.PreviewMouseLeftButtonUp += (_, _) =>
            {
                draggingBox = false;
                grip.ReleaseMouseCapture();
            };
            grip.LostMouseCapture += (_, _) => draggingBox = false;
            _activeTextBoxGrip = grip;
            _textEditorCanvas.Children.Add(grip);
        }

        /// <summary>Reflects the current whiteout setting onto the live placed-text editing box, if any.</summary>
        private void UpdateActiveTextBoxFill()
        {
            if (_activeTextBox is null || _activeTextBox.Tag is not PlacedTextContext) return;
            _activeTextBox.Background = _textWhiteout
                ? FrozenSolidColorBrush(_textFillColor)
                : FrozenSolidColorBrush(Color.FromArgb(230, 255, 255, 255));
        }

        /// <summary>
        /// Reflects font/size/color/bold/italic/underline onto the live editing box, if any — for
        /// BOTH a freshly-placed box (PlacedTextContext) and an in-progress PDF-text edit
        /// (TextEditContext), since the style bar applies to both tools identically. Without this,
        /// the settings bar only ever updated the NEXT box's defaults while a currently-open box
        /// sat on screen unchanged.
        /// </summary>
        private void UpdateActiveTextBoxStyle()
        {
            if (_activeTextBox is not { } tb || tb.Tag is not (PlacedTextContext or TextEditContext)) return;
            tb.FontFamily = new FontFamily(_textFontFamily);
            tb.FontSize = _textFontSize;
            tb.FontWeight = _textBold ? FontWeights.Bold : FontWeights.Normal;
            tb.FontStyle = _textItalic ? FontStyles.Italic : FontStyles.Normal;
            tb.TextDecorations = _textUnderline ? TextDecorations.Underline : null;
            tb.Foreground = new SolidColorBrush(_textColor);
            tb.CaretBrush = new SolidColorBrush(_textColor);
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            // #135 (upstream KillerPDF v1.7.5): bold / italic / underline while editing. Applied to
            // the live TextBox AND mirrored onto the tool state, so the next box you place inherits
            // what you last chose — the same way size, colour and fill already behave.
            // Ctrl+I is also the window's Invert Colors binding (MainWindow.xaml). That resolves
            // correctly and on purpose: KeyDown bubbles from the TextBox outward, so this runs and
            // marks the event handled before it ever reaches the Window's InputBindings. Inside a
            // text box Ctrl+I means italic; everywhere else it still means night mode.
            if (Keyboard.Modifiers == ModifierKeys.Control && sender is TextBox styled)
            {
                switch (e.Key)
                {
                    case Key.B:
                        _textBold = styled.FontWeight != FontWeights.Bold;
                        styled.FontWeight = _textBold ? FontWeights.Bold : FontWeights.Normal;
                        e.Handled = true;
                        return;
                    case Key.I:
                        _textItalic = styled.FontStyle != FontStyles.Italic;
                        styled.FontStyle = _textItalic ? FontStyles.Italic : FontStyles.Normal;
                        e.Handled = true;
                        return;
                    case Key.U:
                        _textUnderline = styled.TextDecorations is not { Count: > 0 };
                        styled.TextDecorations = _textUnderline ? TextDecorations.Underline : null;
                        e.Handled = true;
                        return;
                }
            }

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
            if (sender is not TextBox tb || !ReferenceEquals(_activeTextBox, tb)) return;
            Telemetry.TrackEvent("Annotation.TextEditorFocusLost",
                new Dictionary<string, string> { ["Type"] = "Text" });
            // Commit on blur when there's content, or always when re-editing (so clearing the box deletes it).
            bool reediting = tb.Tag is PlacedTextContext { Existing: not null };
            if (reediting || !string.IsNullOrWhiteSpace(tb.Text))
            {
                _ = Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () =>
                    {
                        if (!ReferenceEquals(_activeTextBox, tb)) return;
                        // Clicking Font/Size/Bold/Italic in the style bar moves keyboard focus off
                        // this box, which used to read as "the user clicked away" and silently
                        // committed mid-edit — ending the session and switching to Select the
                        // instant someone tried to tweak a style. Interacting with the bar is the
                        // SAME editing session, not leaving it.
                        if (Keyboard.FocusedElement is DependencyObject nf && _textSettingsBar is not null
                            && IsDescendantOf(nf, _textSettingsBar))
                            return;
                        CommitActiveTextBox();
                    });
            }
        }

        /// <summary>Cancels the active placed-text edit, restoring the original annotation if re-editing.</summary>
        private void CancelActiveTextBox()
        {
            if (_activeTextBox is null) return;
            var tb = _activeTextBox;
            _activeTextBox = null;
            RemoveTextEditorElement(tb);
            if (tb.Tag is PlacedTextContext { Existing: { } original } ctx)
            {
                if (!_annotations.TryGetValue(ctx.PageIndex, out var list))
                    _annotations[ctx.PageIndex] = list = [];
                list.Add(original);
                DropTopSnapshotIfFor(ctx.PageIndex);   // no net change — discard the edit-start snapshot
                RenderAllAnnotations(ctx.PageIndex);
            }
            Telemetry.TrackEvent("Annotation.TextEditorClosed", TextEditorTelemetry("Canceled"));
        }

        /// <param name="via">
        /// Compile-time name of the calling method, supplied by the compiler. Recorded on the
        /// resulting <c>Annotation.TextEditorClosed</c> event — see <see cref="TextEditorTelemetry"/>.
        /// </param>
        private void CommitActiveTextBox([CallerMemberName] string? via = null)
        {
            // This is the app's single "settle any in-progress canvas edit" chokepoint — every
            // save / flatten / print / close / tool switch / tab switch / page change routes
            // through it — so an unfinished freeform polygon is settled here too rather than being
            // left dangling on a canvas that is about to be rebuilt. See ShapePolyClick.
            ResolveShapePolygon(commit: true);
            if (_activeTextBox is null) return;

            // #129: a text box the user has not typed into yet is not a finished edit, and tearing
            // it down is only ever right as a response to user intent. Refuse that specific case:
            // brand new, never typed into, still empty, and not a re-edit (a re-edit MUST commit —
            // an emptied box there means "delete this annotation"). Worst case if a genuine commit
            // lands inside the window, an empty editor outlives it by a few hundred milliseconds
            // and produces no annotation either way.
            //
            // #131: this is now a BACKSTOP, not the fix. The caller that was destroying every
            // editor in production was ApplyZoom (54/54 destructions, `Via=ApplyZoom`), and it has
            // been removed from that path entirely — a zoom never needed to settle the editor. What
            // this block is still worth is the same thing it was worth then: an unforeseen
            // incidental caller shows up as a NAMED deferral in telemetry instead of as another
            // silent "Insert Text Box does nothing" report. It buys 400 ms and a name; it is not
            // load-bearing, and no future fix should lean on it as though it were.
            //
            // The page check is load-bearing and was missing. PageList_SelectionChanged routes
            // through here, and deferring it left the empty editor parented to TextEditorCanvas —
            // which no re-render clears — so it floated over the page the user had just navigated
            // TO while still carrying the PageIndex of the page it was placed ON. Typing into it
            // then put the text on a page nobody was looking at. Before this release the ~23 Hz
            // ApplyZoom commit destroyed the box milliseconds later and hid that; it does not now.
            if (!_activeTextBoxTouched
                && _activeTextBox.Tag is PlacedTextContext { Existing: null } graceCtx
                && graceCtx.PageIndex == PageList.SelectedIndex
                && string.IsNullOrWhiteSpace(_activeTextBox.Text)
                && (DateTime.UtcNow - _activeTextBoxPlacedUtc).TotalMilliseconds < UntouchedEditorGraceMs)
            {
                Telemetry.TrackEvent("Annotation.TextEditorCommitDeferred",
                    TextEditorTelemetry("UntouchedGrace", via));
                return;
            }
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

            RemoveTextEditorElement(tb);

            if (!string.IsNullOrEmpty(content))
            {
                var ta = new TextAnnotation
                {
                    PageIndex = pageIdx,
                    Position = new Point(x, y),
                    Content = content,
                    FontSize = tb.FontSize,
                    // Same reasoning as Bold/Italic/Underline below: read back off the editor.
                    FontName = tb.FontFamily.Source,
                    // #135: read back off the editor, not off the tool state — the user may have
                    // toggled Ctrl+B mid-sentence and the box in front of them is the truth.
                    Bold = tb.FontWeight == FontWeights.Bold,
                    Italic = tb.FontStyle == FontStyles.Italic,
                    Underline = tb.TextDecorations is { Count: > 0 },
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

                // Auto-select so the user can immediately drag it off whatever it landed on top of,
                // or resize it, without first having to know to switch to the Select tool — Image
                // and Signature placement already do this (see PlaceImageFromDialog / the signature
                // "Reddit/KillerPDF feedback" comment); Text was the one placement flow that didn't,
                // and a text box with no visible border once committed gave no hint that dragging
                // it required a tool switch at all. SetTool's own CommitActiveTextBox() re-entry
                // is a no-op here (_activeTextBox is already null by this point) — and if this
                // commit was itself triggered by the user clicking a DIFFERENT tool, that tool wins:
                // SetTool always finishes by assigning _currentTool to what it was actually called
                // with, after this nested call returns.
                SetTool(EditTool.Select);
                var placedSize = MeasureTextAnnotation(ta);
                SelectAnnotation(ta, new Rect(ta.Position.X, ta.Position.Y, placedSize.Width, placedSize.Height));

                // #168: say it NOW, not after saving and reopening. The burn resolves the same
                // family this checks (DrawAnnotationsOnDocument), so the two never disagree.
                WarnIfGlyphsWillBeLost(PdfFontStyle.DefaultFamily, ta.Content);
                Telemetry.TrackEvent("Annotation.TextEditorClosed", TextEditorTelemetry("Committed", via));
            }
            else if (reediting)
            {
                // Box emptied while re-editing: original was already removed at edit-start → commit as a delete.
                MarkDirty();
                RenderAllAnnotations(pageIdx);
                Telemetry.TrackEvent("Annotation.TextEditorClosed", TextEditorTelemetry("Deleted", via));
            }
            else
            {
                Telemetry.TrackEvent("Annotation.TextEditorClosed", TextEditorTelemetry("Empty", via));
            }
        }

        // ============================================================
        // Keyboard shortcuts
        // ============================================================

        /// <summary>
        /// #153: the four chords whose key is identified by a PUNCTUATION character — document
        /// zoom in / out (Ctrl) and app-wide chrome size up / down (Ctrl+Shift). A
        /// <c>KeyBinding</c> can only match a virtual key, i.e. a key position, which is a US
        /// layout assumption; these match the character the key actually types on whatever layout
        /// is active. The numpad twins stay as KeyBindings in MainWindow.xaml — the numpad is
        /// layout-independent — and reaching them here first simply runs the same action.
        /// </summary>
        /// <remarks>
        /// The ORDER is load-bearing and must not be rearranged: <see cref="KeyLayout.IsCtrlChar"/>
        /// deliberately ignores Shift (on most layouts Shift is how "+" is produced), so on a US
        /// keyboard Ctrl+Shift+= types "+" and would be swallowed as a zoom-in if the app-size
        /// chords were not tested first.
        /// </remarks>
        private bool TryPunctuationShortcut(KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            const ModifierKeys ctrlShift = ModifierKeys.Control | ModifierKeys.Shift;

            if (KeyLayout.IsCtrlShiftChar(e.Key, '+', '=')
                || ((e.Key == Key.OemPlus || e.Key == Key.Add) && mods == ctrlShift))
            {
                AppScaleUp();
                return true;
            }
            if (KeyLayout.IsCtrlShiftChar(e.Key, '-')
                || ((e.Key == Key.OemMinus || e.Key == Key.Subtract) && mods == ctrlShift))
            {
                AppScaleDown();
                return true;
            }
            if (KeyLayout.IsCtrlChar(e.Key, '+', '=')
                || ((e.Key == Key.OemPlus || e.Key == Key.Add) && mods == ModifierKeys.Control))
            {
                ChangeZoomByCommand(ZoomChange.In);
                return true;
            }
            if (KeyLayout.IsCtrlChar(e.Key, '-')
                || ((e.Key == Key.OemMinus || e.Key == Key.Subtract) && mods == ModifierKeys.Control))
            {
                ChangeZoomByCommand(ZoomChange.Out);
                return true;
            }
            return false;
        }

        /// <summary>
        /// #153: rewrites the shortcut spellings that depend on the keyboard layout. "=" is a plain
        /// keypress on US but needs Shift on German, where "+" is the unshifted one instead — and
        /// since the bindings above accept whichever key TYPES the character, the labels have to
        /// follow suit or the app advertises a chord that does nothing on that machine. Called once
        /// at startup; a layout switched mid-session is rare enough to leave alone.
        /// </summary>
        private void ApplyLayoutShortcutLabels()
        {
            string zin = KeyLayout.ZoomInChar(), zout = KeyLayout.ZoomOutChar();
            if (FindName("ZoomInMenuItem") is MenuItem zoomIn) zoomIn.InputGestureText = $"Ctrl+{zin}";
            if (FindName("AppSizeLargerMenuItem") is MenuItem bigger) bigger.InputGestureText = $"Ctrl+Shift+{zin}";
            if (FindName("AppSizeSmallerMenuItem") is MenuItem smaller) smaller.InputGestureText = $"Ctrl+Shift+{zout}";
            if (FindName("ZoomInButton") is Button zoomInBtn) zoomInBtn.ToolTip = $"Zoom In (Ctrl+{zin})";
            if (FindName("KsAppSizeKeys") is TextBlock appSizeKeys) appSizeKeys.Text = $"Ctrl+Shift+{zin} / {zout}";
            // The rendered keyboard board relabels its own punctuation caps as it is built
            // (KbCapText in KeyboardMapOverlay.cs) — it is created lazily, on first use.
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            // A manual (non-OLE) mouse-captured drag doesn't get Escape-to-cancel for free the way
            // DragDrop.DoDragDrop would — release capture ourselves; LostMouseCapture on the chip
            // does the rest (drops the ghost window, clears drag state, no transfer attempted).
            if (_isDraggingTab && e.Key == Key.Escape)
            {
                Mouse.Capture(null);
                e.Handled = true;
                return;
            }

            // While the visual keyboard is showing, holding Ctrl / Shift / Alt previews that
            // modifier layer on the board. (upstream KillerPDF v1.6.4)
            KbSyncLayerFromModifiers();

            // #153: the punctuation zoom / app-size chords, matched by the character the key TYPES
            // on the active layout rather than by its US position. Handled here, ABOVE the typing
            // and caret guards, because these were window-level KeyBindings until now and those
            // fired wherever the focus was — moving them into this handler must not quietly
            // narrow that.
            if (TryPunctuationShortcut(e))
            {
                e.Handled = true;
                return;
            }

            // Don't intercept keys when typing in a TextBox
            if (_activeTextBox is not null && _activeTextBox.IsFocused) return;
            // An inline bookmark rename (#133) owns the keyboard - let arrows / Delete / Home / End
            // edit the text rather than page the document or delete the bookmark.
            if (_bmRenaming) return;
            // Any other caret-bearing surface (find bar, sidebar page-jump box, form-field overlay,
            // editable zoom combo) owns its unmodified keys: a bare "d" must type a "d", never swap
            // to the Draw tool, and Delete / arrows / Home / End must edit the text rather than page
            // the document. Escape and the function keys are the deliberate exceptions - they stay
            // global so Esc can still close the find bar or leave full screen from inside it, and
            // F11 / F12 keep working. Modified chords (Ctrl+...) always fall through as before.
            // The check is structural rather than a list of fields, so a text surface added later
            // is covered by default.
            if (IsTypingTarget() && Keyboard.Modifiers == ModifierKeys.None
                && e.Key != Key.Escape && (e.Key < Key.F1 || e.Key > Key.F12))
                return;

            // The Pages panel owns Ctrl+A and Delete while it has keyboard focus (upstream
            // KillerPDF #289, #296). This has to be decided here rather than on the ListBox: this
            // handler is a PREVIEW on the window, so it tunnels down and reaches Ctrl+A first —
            // the list never saw the key, and Ctrl+A in the sidebar selected the document's TEXT
            // instead of its pages. Both keys are structural to the panel, so they are claimed
            // ahead of the general shortcut chain rather than added to it.
            if (_doc is not null && PageList.IsKeyboardFocusWithin
                && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Control)
            {
                if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    PageList.SelectAll();
                    SetStatus($"Selected all {PageList.Items.Count} page(s)");
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None
                    && PageList.SelectedItems.Count > 0)
                {
                    // Same confirmed, reload-backed path as the context menu's Delete Page(s).
                    Delete_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                }
            }

            // Standard shortcuts (Ctrl+N/O/S/W/Z, Ctrl+Shift+S, Ctrl+P, Ctrl+F, F1, Alt+F/E/V/T/H)
            // are routed via CommandBindings and the Menu's access keys — no need to intercept
            // them here. We still handle the genuinely context-sensitive keys below.

            // An in-progress freeform polygon is the innermost thing Esc can back out of, so it
            // gets first refusal on the key — ahead of full screen, the search bar, the shortcut
            // overlay, and any tool step-down. Backspace walks the vertices back one at a time.
            // Both consume the key only when a polygon is actually being placed.
            if (e.Key == Key.Escape && _polyVertices.Count > 0)
            {
                ResolveShapePolygon(commit: false);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Back && _polyVertices.Count > 0)
            {
                ShapePolyBackspace();
                e.Handled = true;
                return;
            }

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
            // Shift+F4: same file-size flash as clicking the status line (upstream KillerPDF v1.7.2).
            // Bare F4 is left free, and Alt+F4 never reaches here — Alt arrives as Key.System.
            if (e.Key == Key.F4 && Keyboard.Modifiers == ModifierKeys.Shift)
            {
                ShowCurrentFileSize();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.O && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && _doc is not null)
            {
                OcrPagesToClipboard(SelectedPageIndicesForOcr());
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
            // Esc steps DOWN rather than straight out (upstream v1.6.6, adapted): the in-progress
            // polygon, full screen, the find bar and the shortcuts overlay each get first refusal
            // above; with nothing left to cancel, the last step drops back to the Select tool,
            // Acrobat-style. Unlike upstream we stop there - a further Esc does nothing, because
            // TDPdf's Esc has never quit the app and making it do so would be destructive.
            else if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None
                     && _doc is not null && _currentTool != EditTool.Select)
            {
                SetTool(EditTool.Select);
                e.Handled = true;
            }
            // #153: matched by the character the key TYPES, not its position. Typing "?" holds
            // Shift on every layout, so the old exact-equality modifier test meant this only ever
            // fired for Ctrl+/ — even on a US keyboard. The positional check is kept as a fast
            // path (and as the Ctrl+/ spelling) for where it already worked.
            else if (KeyLayout.IsCtrlChar(e.Key, '?')
                     || (e.Key == Key.OemQuestion && Keyboard.Modifiers == ModifierKeys.Control))
            {
                ShortcutOverlay.Visibility = ShortcutOverlay.Visibility == Visibility.Visible
                    ? Visibility.Collapsed : Visibility.Visible;
                if (ShortcutOverlay.Visibility == Visibility.Visible) ApplyPersistedShortcutView();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && _selectedAnnotation is not null)
            {
                DeleteSelected();
                e.Handled = true;
            }
            // Home / End jump to the first / last page (the Acrobat / Sumatra convention). Recorded on
            // the jump history so Alt+Left retraces the hop. (Upstream v1.6.4)
            else if (e.Key == Key.Home && Keyboard.Modifiers == ModifierKeys.None && _doc is not null)
            {
                RecordNavJump();
                PageList.SelectedIndex = 0;
                e.Handled = true;
            }
            else if (e.Key == Key.End && Keyboard.Modifiers == ModifierKeys.None && _doc is not null)
            {
                RecordNavJump();
                PageList.SelectedIndex = _doc.PageCount - 1;
                e.Handled = true;
            }
            // Ctrl+1 / Ctrl+2 / Ctrl+3 = actual size / fit width / fit page (Acrobat/Foxit). Ctrl+0
            // stays the existing 100% reset. (Upstream v1.6.4)
            else if (e.Key == Key.D1 && Keyboard.Modifiers == ModifierKeys.Control && _doc is not null)
            {
                BeginManualZoom();
                // Actual size — a true 100% in every view mode, because Zoom.ZoomLevel is true
                // zoom and MainWindow converts to the tile's layout scale (DisplayZoomFactor).
                Zoom.SetZoomLevel(1.0);
                e.Handled = true;
            }
            // Ctrl+2 / Ctrl+3 are an explicit fit choice, so they are remembered for the next
            // document opened (upstream v1.7.1).
            else if (e.Key == Key.D2 && Keyboard.Modifiers == ModifierKeys.Control && _doc is not null)
            {
                SaveDefaultFitMode(ZoomFitMode.Width);
                FitToWidth();
                e.Handled = true;
            }
            else if (e.Key == Key.D3 && Keyboard.Modifiers == ModifierKeys.Control && _doc is not null)
            {
                SaveDefaultFitMode(ZoomFitMode.Page);
                FitToPage();
                e.Handled = true;
            }
            // Jump history: Alt+Left / Alt+Right retrace bookmark / link / jump-box / Home-End hops,
            // browser-style. Alt makes the key arrive as Key.System with the real key in SystemKey.
            else if (e.Key == Key.System && e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)
            {
                NavHistoryGo(back: true);
                e.Handled = true;
            }
            else if (e.Key == Key.System && e.SystemKey == Key.Right && Keyboard.Modifiers == ModifierKeys.Alt)
            {
                NavHistoryGo(back: false);
                e.Handled = true;
            }
            // Menu key / Shift+F10 opens the right-click menu at the current selection (Windows
            // keyboard-accessibility convention). (Upstream v1.6.4)
            else if ((e.Key == Key.Apps
                      || (e.Key == Key.System && e.SystemKey == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
                     && _doc is not null)
            {
                OpenContextMenuAtSelection();
                e.Handled = true;
            }
            // Arrow keys and PgUp/PgDn navigate to the previous / next page. Handled here at the window
            // (Preview) level with e.Handled so paging works the same whether the page canvas or a sidebar
            // thumbnail has focus — otherwise a focused PageList would page its own selection instead. PgUp/
            // PgDn never reorder pages (that stays on the toolbar Move Up / Down buttons).
            else if (Keyboard.Modifiers == ModifierKeys.None && _doc is not null && PageList.Items.Count > 1
                     && (e.Key == Key.Left || e.Key == Key.Up || e.Key == Key.Right || e.Key == Key.Down
                         || e.Key == Key.PageUp || e.Key == Key.PageDown))
            {
                int dir = (e.Key == Key.Left || e.Key == Key.Up || e.Key == Key.PageUp) ? -1 : 1;
                if (NavigatePageStep(dir))   // one spread at a time in Two-Page mode (#120)
                    PageList.ScrollIntoView(PageList.SelectedItem);
                e.Handled = true;
            }
            // Shift+N pairs with Ctrl+I: it toggles whether night mode inverts PICTURES too — the
            // option on the moon's right-click menu, and the thing that makes a scanned page (one
            // full-page image) usable in night mode. TDPdf keeps Ctrl+I for the mode itself rather
            // than taking upstream's bare N, because our single-letter keys are all tool shortcuts.
            // Shift+N is free: TrySelectToolByKey is gated on ModifierKeys.None, and nothing else
            // binds a Shift+letter. The typing guard above only returns for unmodified keys, so the
            // caret check is repeated here — a capital N in the find box must type an N.
            else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Shift && _doc is not null
                     && !IsTypingTarget()
                     && ShortcutOverlay.Visibility != Visibility.Visible)
            {
                ToggleDocInvertImages(!_docInvertImages);
                e.Handled = true;
            }
            // B toggles the Two-Page book layout (#193, upstream binds the same bare key). Verified
            // free against TrySelectToolByKey and every other branch in this handler before taking
            // it — our single-key space is dense (V, P, digits, T/X/I/H/K/U/S/D/E/G/C) but B was
            // unclaimed. Same shape as the Shift+N row above: gated on a document being open, the
            // shortcut overlay being closed, and the caret not sitting in a text surface (the
            // unmodified-key typing guard at the top of this method already returned in that case,
            // but the check is repeated so the branch stands on its own).
            else if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.None && _doc is not null
                     && !IsTypingTarget()
                     && Keyboard.FocusedElement is not ComboBox
                     && ShortcutOverlay.Visibility != Visibility.Visible)
            {
                ToggleTwoPageBook(!_twoPageBook);
                e.Handled = true;
            }
            // Unmodified tool keys, last so every context-sensitive key above keeps priority.
            // Only while a document is open and no overlay owns the keyboard; the typing guard at
            // the top of this method already made sure the caret isn't in a text surface.
            // A focused drop-down (the zoom combo, a PDF choice field) is excluded on top of that:
            // it isn't a caret surface, but WPF gives it letter type-ahead, and jumping to an option
            // by typing must keep working.
            else if (Keyboard.Modifiers == ModifierKeys.None && _doc is not null
                     && ShortcutOverlay.Visibility != Visibility.Visible
                     && Keyboard.FocusedElement is not ComboBox
                     && TrySelectToolByKey(e.Key))
            {
                e.Handled = true;
            }
        }

        /// <summary>
        /// TDPdf's single-key tool map. Adapted from upstream KillerPDF v1.6.6, not copied: our tool
        /// set differs (no Stamp, Line is a Shape sub-mode, Transform is a dialog, and we add Pan,
        /// Erase, Edit Text, Edit Image, Strikethrough and Underline), so only the *principles*
        /// carry over — V for Select (the Photoshop / Illustrator / Figma convention) and digits
        /// that mirror the toolbar left to right.
        ///
        /// The toolbar runs Select, Pan, Text, Edit Text, Edit Image, Insert Image, Highlight,
        /// Strikethrough, Underline, Shape, Draw, Erase | Signature, Crop. The ten mark-making tools
        /// between Pan and that separator take 1-9 then 0, in exactly that order; the two navigation
        /// modes before them (Select, Pan) and the two tools past the separator (Signature, Crop)
        /// are letter-only, so the digit run has a principled start and end instead of stopping
        /// arbitrarily. Mnemonic letters double up wherever one is free. Number-row and numpad
        /// digits both map.
        ///
        /// Returns true when the key selected a tool, so the caller can mark the event handled.
        /// </summary>
        private bool TrySelectToolByKey(Key key)
        {
            switch (key)
            {
                case Key.V: SetTool(EditTool.Select); return true;
                case Key.P: SetTool(EditTool.Pan); return true;
                case Key.T: case Key.D1: case Key.NumPad1: SetTool(EditTool.Text); return true;
                case Key.X: case Key.D2: case Key.NumPad2: SetTool(EditTool.EditText); return true;
                case Key.D3: case Key.NumPad3: SetTool(EditTool.EditImage); return true;
                case Key.I: case Key.D4: case Key.NumPad4: SetTool(EditTool.Image); return true;
                case Key.H: case Key.D5: case Key.NumPad5: SetTool(EditTool.Highlight); return true;
                case Key.K: case Key.D6: case Key.NumPad6: SetTool(EditTool.Strikethrough); return true;
                case Key.U: case Key.D7: case Key.NumPad7: SetTool(EditTool.Underline); return true;
                case Key.S: case Key.D8: case Key.NumPad8: SetTool(EditTool.Shape); return true;
                case Key.D: case Key.D9: case Key.NumPad9: SetTool(EditTool.Draw); return true;
                case Key.E: case Key.D0: case Key.NumPad0: SetTool(EditTool.Erase); return true;
                // Signature routes through the button handler so the saved-signature popup opens,
                // exactly as clicking the toolbar button does.
                case Key.G: ToolSignature_Click(this, new RoutedEventArgs()); return true;
                case Key.C: ToolCrop_Click(this, new RoutedEventArgs()); return true;
                default: return false;
            }
        }

        /// <summary>
        /// True when the keyboard focus is sitting in a text-entry surface, so the unmodified keys
        /// belong to whatever is being typed. Covers every typing target hosted by the main window:
        /// the annotation text box and the inline PDF-text editor (both also tracked by
        /// _activeTextBox), the find bar's search box, the sidebar page-jump box, the bookmark
        /// inline-rename box (also tracked by _bmRenaming), the interactive form-field overlays, and
        /// the editable zoom combo. Checked by control type rather than by field identity so a new
        /// text surface is protected the moment it exists.
        /// </summary>
        private static bool IsTypingTarget() =>
            Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.PasswordBox
            || Keyboard.FocusedElement is ComboBox { IsEditable: true };

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
            // Upstream v1.7.1 (#181): WPF refuses NaN and infinity on Width/Height outright. The
            // "> 0" tests below are no guard at all — "∞ > 0" is true — so a malformed persisted
            // box used to take the whole viewer down on the next repaint. Width/Height of exactly 0
            // is the legacy auto-size mode and stays legal, so only NON-FINITE values bail out.
            if (!IsFinite(ta.Width) || !IsFinite(ta.Height)) return;

            var tb = new TextBlock
            {
                Text = ta.Content,
                Foreground = new SolidColorBrush(ta.GetColor()),
                FontFamily = new FontFamily(ta.FontName),
                FontSize = ta.FontSize,
                FontWeight = ta.Bold ? FontWeights.Bold : FontWeights.Normal,        // #135
                FontStyle = ta.Italic ? FontStyles.Italic : FontStyles.Normal,
                TextDecorations = ta.Underline ? TextDecorations.Underline : null,
                Padding = new Thickness(2),
                // #156: annotation visuals must never intercept the mouse — selection and dragging
                // hit-test the _annotations data, not the visuals, and the form-field overlays now
                // sit UNDER this layer and still have to receive their own clicks.
                IsHitTestVisible = false
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
                TextTypeface(ta.Bold, ta.Italic), ta.FontSize, Brushes.Black, dpi);   // #135
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
        private List<string> WrapTextToWidth(string text, double fontSize, double maxWidth,
                                             bool bold = false, bool italic = false)
        {
            var lines = new List<string>();
            if (maxWidth <= 0) { lines.Add(text); return lines; }
            double dpi = VisualTreeHelper.GetDpi(_annotationCanvas).PixelsPerDip;
            var typeface = TextTypeface(bold, italic);   // #135: bold/italic change the advances
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

        // ------------------------------------------------------------------
        // Upstream v1.7.1 (#181): WPF throws "'∞' is not a valid value for property 'Height'" (and
        // the same for NaN) the moment a FrameworkElement is given a non-finite size, so malformed
        // persisted geometry could take the viewer down during an ordinary repaint. Every sized
        // visual below is checked through these before it reaches WPF.
        //
        // Note that Rect.Empty is itself non-finite — WPF defines it as (+∞, +∞, -∞, -∞) — so these
        // also catch a bounds value that came from a hit test that found nothing.
        // ------------------------------------------------------------------

        private static bool IsFinite(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value);

        private static bool IsFinitePositive(double value)
            => IsFinite(value) && value > 0;

        /// <summary>
        /// The on-canvas size of a placed (signature / image) annotation, or false when the stored
        /// source size and scale do not multiply out to something WPF will accept.
        /// </summary>
        private static bool TryGetPlacedSize(PlacedAnnotation annot, out double width, out double height)
        {
            width = annot.SourceWidth * annot.Scale;
            height = annot.SourceHeight * annot.Scale;
            return IsFinitePositive(width) && IsFinitePositive(height);
        }

        private void RenderAllAnnotations(int pageIndex)
        {
            _annotationCanvas.Children.Clear();
            // Clearing the canvas also drops form-field overlays — restore them so they
            // survive every annotation re-render (edits, undo, selection, …).
            if (!_annotations.ContainsKey(pageIndex))
            {
                RestoreFormOverlays(pageIndex);
                RestorePolyPreview(pageIndex);
                ApplyTextSelectionQuads(pageIndex);
                return;
            }

            foreach (var annot in _annotations[pageIndex])
            {
                switch (annot)
                {
                    case TextAnnotation ta:
                        RenderTextAnnotation(ta);
                        break;
                    // Markup paints one band per covered line. Matched before HighlightAnnotation —
                    // it is a subclass, and plain highlights must keep their single-rect render.
                    case MarkupAnnotation mk:
                    {
                        var mkBrush = FrozenSolidColorBrush(mk.GetColor());
                        foreach (var pr in mk.PaintRects())
                        {
                            if (!IsFinitePositive(pr.Width) || !IsFinitePositive(pr.Height)) continue;
                            var band = new Rectangle
                            {
                                Fill = mkBrush,
                                Width = pr.Width,
                                Height = pr.Height,
                                IsHitTestVisible = false
                            };
                            Canvas.SetLeft(band, pr.X);
                            Canvas.SetTop(band, pr.Y);
                            _annotationCanvas.Children.Add(band);
                        }
                        break;
                    }
                    case HighlightAnnotation ha:
                        if (!IsFinitePositive(ha.Bounds.Width) || !IsFinitePositive(ha.Bounds.Height)) continue;
                        var rect = new Rectangle
                        {
                            Fill = FrozenSolidColorBrush(ha.GetColor()),
                            Width = ha.Bounds.Width,
                            Height = ha.Bounds.Height,
                            IsHitTestVisible = false
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
                            StrokeEndLineCap = PenLineCap.Round,
                            IsHitTestVisible = false
                        };
                        foreach (var pt in ia.Points) poly.Points.Add(pt);
                        _annotationCanvas.Children.Add(poly);
                        break;
                    case TextEditAnnotation tea:
                        if (!IsFinite(tea.OriginalBounds.Width) || !IsFinite(tea.OriginalBounds.Height)) continue;
                        // White-out original text. The overhang used to be +4/-2 (2px each side), which
                        // was enough to bleed into a table border sitting close to the text — trimmed
                        // to a 1px overhang, and the box is now user-resizable (SelectAnnotation /
                        // ApplyResizeTo) so a still-too-generous default can be shrunk by hand.
                        var wo = new Rectangle
                        {
                            Fill = Brushes.White,
                            Width = tea.OriginalBounds.Width + 2,
                            Height = tea.OriginalBounds.Height + 2,
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(wo, tea.OriginalBounds.X - 1);
                        Canvas.SetTop(wo, tea.OriginalBounds.Y - 1);
                        _annotationCanvas.Children.Add(wo);
                        // Draw replacement text
                        var etb = new TextBlock
                        {
                            Text = tea.NewContent,
                            Foreground = FrozenSolidColorBrush(tea.GetColor()),
                            FontFamily = new FontFamily(tea.FontName),
                            FontSize = tea.FontSize,
                            FontWeight = tea.Bold ? FontWeights.Bold : FontWeights.Normal,
                            FontStyle = tea.Italic ? FontStyles.Italic : FontStyles.Normal,
                            Padding = new Thickness(0),
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(etb, tea.Position.X);
                        Canvas.SetTop(etb, tea.Position.Y);
                        _annotationCanvas.Children.Add(etb);
                        break;

                    case ImageEditAnnotation iea:
                        if (!IsFinite(iea.OriginalBounds.Width) || !IsFinite(iea.OriginalBounds.Height)) continue;
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
                            if (source is not null &&
                                IsFinitePositive(iea.TargetBounds.Width) &&
                                IsFinitePositive(iea.TargetBounds.Height))
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
                        // Guards BOTH variants: the drawn one scales its stroke width and every point
                        // by the same Scale, so a signature whose placed size is not a real number is
                        // unrenderable either way.
                        if (!TryGetPlacedSize(sa, out double sigW, out double sigH)) continue;
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
                                    Width = sigW,
                                    Height = sigH,
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
                                    StrokeEndLineCap = PenLineCap.Round,
                                    IsHitTestVisible = false
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
                        if (!TryGetPlacedSize(ia, out double iaW, out double iaH)) continue;
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
                                Width = iaW,
                                Height = iaH,
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
            RestorePolyPreview(pageIndex);
            // Flowing text-selection quads live on this canvas too and were wiped by the clear;
            // repaint them last so they sit on top and survive every re-render.
            ApplyTextSelectionQuads(pageIndex);
        }

        /// <summary>
        /// Puts the in-progress freeform polygon's preview visuals back on the canvas after
        /// <see cref="RenderAllAnnotations"/> cleared it. Without this a re-render during
        /// placement (undo, a restyle, a move on another annotation) would detach the very
        /// polyline the next vertex click writes to, leaving the shape invisible until commit.
        /// </summary>
        private void RestorePolyPreview(int pageIndex)
        {
            if (_polyVertices.Count == 0 || _polyPage != pageIndex) return;
            if (_polyPreview is not null && !_annotationCanvas.Children.Contains(_polyPreview))
                _annotationCanvas.Children.Add(_polyPreview);
            if (_polyRubber is not null && !_annotationCanvas.Children.Contains(_polyRubber))
                _annotationCanvas.Children.Add(_polyRubber);
            if (_polySnapDot is not null && !_annotationCanvas.Children.Contains(_polySnapDot))
                _annotationCanvas.Children.Add(_polySnapDot);
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
                    // Math.Max(1, x) is not a guard: it returns NaN for NaN and ∞ for ∞, both of
                    // which WPF rejects on Width/Height (#181).
                    if (!IsFinite(b.Width) || !IsFinite(b.Height)) break;
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
                    if (!IsFinite(b.Width) || !IsFinite(b.Height)) break;
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
                case ShapeKind.Polygon:
                {
                    if (shp.Points.Count < 2) break;
                    // WPF's Polygon closes the outline itself, so the stored vertices go in as-is.
                    var pg = new System.Windows.Shapes.Polygon
                    {
                        Stroke = stroke,
                        StrokeThickness = shp.StrokeWidth,
                        StrokeLineJoin = PenLineJoin.Round,
                        Fill = fill ?? Brushes.Transparent,
                        IsHitTestVisible = false
                    };
                    foreach (var p in shp.Points) pg.Points.Add(p);
                    _annotationCanvas.Children.Add(pg);
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
                int rotation = ((edit.Rotation % 360) + 360) % 360;
                if (rotation == 0) return bmp;
                var rotated = new TransformedBitmap(bmp, new RotateTransform(rotation));
                rotated.Freeze();
                return rotated;
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
            bool canceledTextEditor = _activeTextBox is not null;
            CancelActiveGesture();
            if (canceledTextEditor)
            {
                SetStatus("Text edit canceled");
                return;
            }

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
                LoadOutlines();   // the reopened _doc has its own outline tree; rebuild the panel (#133)
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
            if (_activeTextBox?.Tag is TextEditContext)
                CancelTextEdit();
            else if (_activeTextBox is not null)
                CancelActiveTextBox();
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
            // Wiping the page is the opposite of keeping an unfinished shape: drop it (and its
            // preview visuals, which the canvas clear below would otherwise strand).
            CancelActiveGesture();
            ResolveShapePolygon(commit: false);
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
            CancelDocumentWork(cancelWindowOperation: false);
            ClearContinuousRenderState();

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
            _textEditorCanvas.Children.Clear();
            _activeTextBox = null;
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
                _primaryPageBitmap = null;
                FileNameLabel.Text = "";
                DropZone.Visibility = Visibility.Visible;
                PagePreviewPanel.Visibility = Visibility.Collapsed;
                HidePageBadgeNow();   // #197: never leave the badge floating over the start screen
                if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = false;
                _gridViewToggle.IsEnabled = false;
                _pageJumpBox.IsEnabled = false;
                _pageJumpBox.Text = "";
                _pageTotalLabel.Text = "/ –";
                UpdatePageControlsForDoc(false);   // hide the empty box + "/ –" outright
                LoadOutlines();           // no document → clears the tree and disables the tab
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
                UpdatePageControlsForDoc(true);
                RefreshPageList(_ctx.Thumbnails);
                LoadOutlines();   // rebuild the bookmark tree from this tab's live document

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

        // Lists every open tab by name in a dropdown, so a document doesn't have to be hunted for
        // by scrolling past a long run of same-width, ellipsis-truncated chips once many files are
        // open (the tab strip's ScrollViewer keeps every chip reachable, but not visible at once).
        private void TabOverflowBtn_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = (UIElement)sender,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
            };
            foreach (var ctx in _tabs)
            {
                string name = string.IsNullOrEmpty(ctx.DisplayName) ? "Untitled.pdf" : ctx.DisplayName;
                bool active = ReferenceEquals(ctx, _ctx);
                var c = ctx;
                var item = new MenuItem
                {
                    Header = (c.IsDirty ? "● " : "") + name,
                    FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                    ToolTip = c.OriginalPath ?? name
                };
                if (active) item.SetResourceReference(MenuItem.ForegroundProperty, "AccentGreen");
                item.Click += (_, _) => { if (!ReferenceEquals(_ctx, c)) ActivateContext(c); };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
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
            // Drag this chip onto another TDPdf window to move the document there, or onto empty
            // desktop / any other app to tear it off into a brand-new TDPdf window \u2014 see
            // BeginTabDrag/EndTabDrag. A plain click (no drag distance) still just activates the
            // tab, exactly as before.
            chip.PreviewMouseLeftButtonDown += (_, e) =>
            {
                // The close "x" is a child of this chip, so the tunneling Preview event reaches us
                // first; let it fall through untouched rather than arming a drag over top of it.
                if (e.OriginalSource is DependencyObject src && IsDescendantOf(src, close)) return;
                _tabDragCandidate = ctx;
                _tabDragStartScreen = chip.PointToScreen(e.GetPosition(chip));
            };
            chip.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || !ReferenceEquals(_tabDragCandidate, ctx)) return;
                var nowScreen = chip.PointToScreen(e.GetPosition(chip));
                if (!_isDraggingTab)
                {
                    if (Math.Abs(nowScreen.X - _tabDragStartScreen.X) < SystemParameters.MinimumHorizontalDragDistance &&
                        Math.Abs(nowScreen.Y - _tabDragStartScreen.Y) < SystemParameters.MinimumVerticalDragDistance)
                        return;
                    BeginTabDrag(ctx, chip);
                }
                UpdateTabDrag(nowScreen);
            };
            chip.PreviewMouseLeftButtonUp += (_, e) =>
            {
                if (_isDraggingTab && ReferenceEquals(_tabDragCandidate, ctx))
                {
                    EndTabDrag(chip.PointToScreen(e.GetPosition(chip)));
                    e.Handled = true;
                }
                else if (ReferenceEquals(_tabDragCandidate, ctx) && !ReferenceEquals(_ctx, ctx))
                {
                    ActivateContext(ctx);
                }
                _tabDragCandidate = null;
            };
            chip.LostMouseCapture += (_, _) =>
            {
                // Reached two ways: our own EndTabDrag already released capture (isDraggingTab is
                // already false by then, so this is a no-op) or capture was pulled out from under
                // us \u2014 Escape, Alt-Tab, a dialog stealing focus. Either way, drop the cancel.
                if (_isDraggingTab)
                {
                    _isDraggingTab = false;
                    _tabDragGhost?.Close();
                    _tabDragGhost = null;
                }
                _tabDragCandidate = null;
            };

            // Right-click menu. Rebuilt with the strip on every tab change, so "Close Other Tabs"
            // can be enabled purely from the current count with no live refresh to maintain.
            var chipMenu = new ContextMenu();
            chipMenu.Items.Add(MakeMenuItem("Close Tab", (_, _) => CloseTab(ctx), "Ctrl+W",
                "Close this document", "\uE8BB"));
            var closeOthers = MakeMenuItem("Close Other Tabs", (_, _) => CloseOtherTabs(ctx), "Ctrl+Shift+W",
                "Close every open document except this one", "\uE711");
            closeOthers.IsEnabled = _tabs.Count > 1;
            chipMenu.Items.Add(closeOthers);
            chipMenu.Items.Add(MakeMenuItem("Move to New Window", (_, _) => _ = TearOffTabToNewWindowAsync(ctx), null,
                "Open this document alone in a new TDPdf window", "\uE78B"));
            chip.ContextMenu = chipMenu;
            return chip;
        }

        /// <summary>Updates each chip's label (name + dirty marker) and active styling.</summary>
        private void UpdateTabChrome()
        {
            if (_tabStrip is null) return;
            UpdateWindowTitle();
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
                    // Chip text truncates at 180px (TextTrimming.CharacterEllipsis in BuildTabChip),
                    // so a long filename needs a hover to read in full — this is the only place that
                    // ever refreshes, so it also picks up a rename (e.g. Save As) after tab creation.
                    ctx.Chip.ToolTip = ctx.OriginalPath ?? name;
                }
            }
        }

        // The native window title — what Alt-Tab, the taskbar hover preview and the taskbar
        // thumbnail label actually show — stayed the static "TDPdf" from XAML forever, so every
        // open window/instance looked identical there even though the in-app title bar already
        // carries FileNameLabel. Reflect the active document (and how many other tabs share this
        // window) so windows become distinguishable at the OS level, not just inside the app.
        private void UpdateWindowTitle()
        {
            if (_ctx.Doc is null) { Title = "TDPdf"; return; }
            string name = string.IsNullOrEmpty(_ctx.DisplayName) ? "Untitled.pdf" : _ctx.DisplayName;
            Title = _tabs.Count > 1 ? $"{name} - TDPdf ({_tabs.Count} tabs)" : $"{name} - TDPdf";
        }

        /// <summary>Closes a tab (prompting if it has unsaved changes) and activates a neighbor.</summary>
        private void CloseTab(DocumentContext ctx)
        {
            EnsureActiveTabRegistered();
            if (!_tabs.Contains(ctx)) return;

            // A freeform polygon still being placed on the tab we are about to throw away has
            // nowhere to land, so it is discarded rather than committed — and discarding it here,
            // before the dirty prompt, keeps an abandoned gesture from asking about unsaved work.
            if (ReferenceEquals(_ctx, ctx))
            {
                ResolveShapePolygon(commit: false);
                CommitActiveTextBox();
            }

            if (ctx.Doc is not null && ctx.IsDirty)
            {
                if (!ReferenceEquals(_ctx, ctx)) ActivateContext(ctx);
                var res = TdpDialog.ShowYesNo(this,
                    "This file has unsaved changes.",
                    "Close Without Saving", "Cancel",
                    "TDPdf", MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
            }

            RemoveTabSilently(ctx);
            SetStatus("Ready");
        }

        /// <summary>
        /// The actual tab-removal mechanics, with no dirty-changes prompt — CloseTab gates on that
        /// itself before calling this; the cross-window transfer path (below) calls this too, once
        /// the document is already safely handed to the other window, so nothing is lost either way.
        /// </summary>
        private void RemoveTabSilently(DocumentContext ctx)
        {
            if (!_tabs.Contains(ctx)) return;

            int removedIndex = _tabs.IndexOf(ctx);
            bool closingActive = ReferenceEquals(_ctx, ctx);

            if (closingActive)
            {
                CancelDocumentWork(cancelWindowOperation: false);
                PageImage.Source = null;
                PageImage.Tag = null;
                _primaryPageBitmap = null;
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

            QueueReleasedDocumentCollection();

            if (_tabs.Count == 0)
            {
                var empty = new DocumentContext();
                _tabs.Add(empty);
                ActivateContext(empty);
                // The last document just closed — re-collapse the rail (animated) and hide the page
                // controls. Done here rather than in ActivateContext's empty branch on purpose: that
                // branch also runs for the throwaway tab OpenInTabAsync creates on the way to a second
                // document, which would collapse-then-expand for no reason.
                SyncSidebarToDocState(hasDoc: false, startup: false);
            }
            else if (closingActive)
            {
                int next = Math.Min(removedIndex, _tabs.Count - 1);
                ActivateContext(_tabs[next]);
            }
            RebuildTabStrip();
        }

        // ============================================================
        // Cross-window tab drag (Services/WindowTransfer.cs)
        // ============================================================
        // Each running TDPdf.exe is its own window AND its own process — MainWindow is only ever
        // constructed once per process (see App.xaml.cs). So "move a tab to another window" and
        // "tear a tab off into a new window" both mean handing a FILE PATH to a different OS
        // process, not reparenting a live in-memory document — there is no such thing as a live
        // TextAnnotation object crossing a process boundary. A dirty tab is therefore saved to its
        // real file first (prompting, exactly like closing a dirty tab already does) so the target
        // window opens the same on-disk content the source was showing; what does NOT survive the
        // move is in-progress undo/redo history, which is an acceptable, explicit trade for the
        // alternative of either silently flattening annotations behind the user's back or building
        // a full cross-process document-object serializer.

        private void BeginTabDrag(DocumentContext ctx, Border chip)
        {
            _isDraggingTab = true;
            chip.CaptureMouse();
            string label = string.IsNullOrEmpty(ctx.DisplayName) ? "Untitled.pdf" : ctx.DisplayName;
            _tabDragGhost = new TabDragGhost(label);
            _tabDragGhost.Show();
            Telemetry.TrackEvent("TabDrag.Started");
        }

        private void UpdateTabDrag(Point screenPos)
        {
            _tabDragGhost?.MoveTo(new Point(screenPos.X + 14, screenPos.Y + 18));
        }

        private void EndTabDrag(Point screenPos)
        {
            var ctx = _tabDragCandidate;
            _isDraggingTab = false;
            _tabDragGhost?.Close();
            _tabDragGhost = null;
            Mouse.Capture(null);
            if (ctx is null) return;

            int? pid = WindowHitTest.ProcessIdAtScreenPoint(screenPos);
            if (pid is int ownPid && ownPid == Environment.ProcessId)
            {
                // Dropped back on this same window. No in-strip reordering yet — just leave it.
                Telemetry.TrackEvent("TabDrag.DroppedSameWindow");
                return;
            }
            if (pid is int otherPid && WindowHitTest.IsOtherTdpdfProcess(otherPid))
                _ = TransferTabToWindowAsync(ctx, otherPid);
            else
                _ = TearOffTabToNewWindowAsync(ctx);
        }

        /// <summary>
        /// Resolves a portable, on-disk path for ctx's CURRENT content, saving first if needed.
        /// Returns null — having left ctx untouched and said why via SetStatus/a dialog — when the
        /// move should not proceed: an untitled tab with nowhere to save to, a declined save
        /// prompt, or a save failure.
        /// </summary>
        private async Task<string?> ResolveTransferPathAsync(DocumentContext ctx)
        {
            // Tab-switch chokepoint: settle whatever the CURRENTLY active tab has in flight before
            // touching _ctx, exactly like every other tab-switch path in the app.
            CommitActiveTextBox();
            ResolveShapePolygon(commit: true);
            if (!ReferenceEquals(_ctx, ctx)) ActivateContext(ctx);

            if (!ctx.IsDirty)
            {
                string? clean = ctx.OriginalPath ?? ctx.CurrentFile;
                if (clean is not null && File.Exists(clean)) return clean;
            }
            if (ctx.OriginalPath is null)
            {
                SetStatus("Save this document (Ctrl+Shift+S) before moving it to another window.");
                Telemetry.TrackEvent("TabDrag.Blocked", new Dictionary<string, string> { ["Reason"] = "Untitled" });
                return null;
            }
            var res = TdpDialog.ShowYesNo(this,
                "This document has unsaved changes. Save it and move it to the other window?",
                "Save && Move", "Cancel",
                "TDPdf", MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes)
            {
                Telemetry.TrackEvent("TabDrag.Blocked", new Dictionary<string, string> { ["Reason"] = "Declined" });
                return null;
            }
            await SaveInPlaceAsync();
            // SaveInPlaceAsync already reported the failure via its own dialog/status.
            if (ctx.IsDirty)
            {
                Telemetry.TrackEvent("TabDrag.Blocked", new Dictionary<string, string> { ["Reason"] = "SaveFailed" });
                return null;
            }
            return ctx.OriginalPath;
        }

        private async Task TransferTabToWindowAsync(DocumentContext ctx, int destPid)
        {
            using var op = Telemetry.StartOperation("TabDrag.TransferToWindow");
            string? path = await ResolveTransferPathAsync(ctx);
            if (path is null) { op.Fail(); return; }   // ResolveTransferPathAsync already tracked why
            string displayName = ctx.DisplayName;
            var (ok, err) = await Task.Run(() =>
            {
                bool success = WindowTransferServer.TryImport(destPid, path!, out string? error);
                return (success, error);
            });
            if (ok)
            {
                SetStatus($"Moved \"{displayName}\" to another window.");
                RemoveTabSilently(ctx);
            }
            else
            {
                // err is a pipe/IO failure string (timeout, broken pipe, "no response") — never a
                // path or document name — and TrackOperation's properties are scrubbed regardless.
                op.With("PipeError", err ?? "no response").Fail();
                SetStatus($"Could not move \"{displayName}\" to that window ({err ?? "no response"}).");
            }
        }

        private async Task TearOffTabToNewWindowAsync(DocumentContext ctx)
        {
            using var op = Telemetry.StartOperation("TabDrag.TearOff");
            string? path = await ResolveTransferPathAsync(ctx);
            if (path is null) { op.Fail(); return; }   // ResolveTransferPathAsync already tracked why
            string displayName = ctx.DisplayName;
            try
            {
                // Assembly.Location always reads empty for a single-file-published exe (IL3000),
                // so this has to be ProcessPath, not a Location fallback — and it needs no fallback
                // of its own since ProcessPath is reliably set for the process that is running us.
                string exePath = Environment.ProcessPath!;
                var psi = new ProcessStartInfo(exePath) { UseShellExecute = false };
                psi.ArgumentList.Add("--new-window");
                psi.ArgumentList.Add(path!);
                Process.Start(psi);
                SetStatus($"Opened \"{displayName}\" in a new window.");
                RemoveTabSilently(ctx);
            }
            catch (Exception ex)
            {
                // Spawning our own already-installed exe should never fail — if it does, that is
                // worth a real crash record (sanitized), not just a status line nobody sees again.
                Telemetry.TrackCrash(ex, "TabDrag.TearOff", recoverable: true);
                op.Fail(ex);
                SetStatus($"Could not open a new window: {ex.Message}");
            }
        }

        /// <summary>Handles an IMPORT request arriving on this window's WindowTransferServer pipe —
        /// i.e. another TDPdf window's drag landed on us. Runs on the UI thread (dispatched there by
        /// the server); the pipe thread blocks on the returned Task so it only replies OK once the
        /// tab genuinely exists here.</summary>
        private async Task<bool> ImportTabFromAnotherWindowAsync(string path)
        {
            using var op = Telemetry.StartOperation("TabDrag.Import");
            if (!_uiReady || !File.Exists(path))
            {
                op.With("Reason", !_uiReady ? "NotReady" : "PathMissing").Fail();
                return false;
            }
            try
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                await OpenInTabAsync(path);
                bool ok = _ctx.Doc is not null;
                if (!ok) op.Fail();
                return ok;
            }
            catch (Exception ex)
            {
                Telemetry.TrackCrash(ex, "TabDrag.Import", recoverable: true);
                op.Fail(ex);
                return false;
            }
        }

        private void CancelDocumentWork(bool cancelWindowOperation)
        {
            _openCancellationTokenSource?.Cancel();
            _renderCancellationTokenSource?.Cancel();
            _secondaryRenderCts?.Cancel();
            _continuousRenderCts?.Cancel();
            _continuousSharpenCts?.Cancel();
            _continuousWindowCts?.Cancel();
            _continuousSharpenTimer?.Stop();
            if (cancelWindowOperation)
                _cancellableOpCts?.Cancel();
        }

        private void ClearContinuousRenderState()
        {
            _continuousSharpenTimer?.Stop();
            _continuousSharpPages.Clear();
            _continuousBaseBitmaps.Clear();
            _continuousSharpW = 0;
            _continuousTops.Clear();

            foreach (UIElement child in _continuousPanel.Children)
            {
                if (child is Border { Child: Image image })
                    image.Source = null;
            }
            _continuousPanel.Children.Clear();
        }

        private bool _releasedDocumentCollectionPending;

        private void QueueReleasedDocumentCollection()
        {
            if (_releasedDocumentCollectionPending) return;
            _releasedDocumentCollectionPending = true;
            _ = Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                () =>
                {
                    _releasedDocumentCollectionPending = false;
                    GC.Collect(
                        GC.MaxGeneration,
                        GCCollectionMode.Optimized,
                        blocking: false,
                        compacting: false);
                });
        }

        /// <summary>
        /// Ctrl+Shift+W / tab right-click: closes every tab except <paramref name="keep"/>.
        /// Deliberately routed through <see cref="CloseTab"/> one tab at a time rather than
        /// shortcutting the teardown, so each unsaved document still gets the same prompt — and a
        /// "No" leaves that one tab open instead of aborting the whole sweep. CloseTab activates a
        /// dirty tab before prompting, so the kept tab is re-activated at the end.
        /// </summary>
        private void CloseOtherTabs(DocumentContext keep)
        {
            EnsureActiveTabRegistered();
            if (!_tabs.Contains(keep)) return;

            var others = _tabs.Where(t => !ReferenceEquals(t, keep)).ToList();
            if (others.Count == 0) return;

            foreach (var other in others)
            {
                CloseTab(other);
                if (!_tabs.Contains(keep)) return;   // defensive: the kept tab went away somehow
            }

            if (!ReferenceEquals(_ctx, keep)) ActivateContext(keep);
            RebuildTabStrip();

            int closed = others.Count - _tabs.Count + 1;
            SetStatus(closed <= 0
                ? "No other tabs were closed"
                : closed == 1 ? "Closed 1 other tab" : $"Closed {closed} other tabs");
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
        /// <summary>A forwarded path that arrived before the window was wired up, replayed from Loaded.</summary>
        private string? _pendingExternalPath;

        public void OpenPathFromAnotherInstance(string? path)
        {
            // Upstream v1.7.4 (#202): the second launch forwards off the named-pipe thread and
            // marshals here, but WPF sets Application.Current.MainWindow from the Window
            // constructor — so this can run while ours is still executing, against fields the
            // manual-element-refs block has not assigned yet. Hold the path and let Loaded replay
            // it rather than dereferencing null and losing the file the user double-clicked.
            if (!_uiReady)
            {
                _pendingExternalPath = path;
                return;
            }

            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                _ = OpenInTabAsync(path);
        }

        /// <summary>Replays whatever arrived during startup. A no-op in the normal case.</summary>
        private void FlushPendingExternalOpen()
        {
            if (_pendingExternalPath is null) return;
            var path = _pendingExternalPath;
            _pendingExternalPath = null;
            OpenPathFromAnotherInstance(path);
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
                // The working file is a blank PDF TDPdf just wrote to %TEMP%, not a document with a
                // home: Ctrl+S must route to Save As instead of updating a temp copy that is deleted
                // on exit. Not marked dirty — a fresh blank page holds no unsaved work.
                FinalizeUnsavedTab(tempPath, "Untitled.pdf", "New blank document", markDirty: false);
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
            if (dlg.ShowDialog() != true) return;

            // OpenFileAsync (inside OpenInTabAsync) already handles the common failure cases itself
            // (bad file, wrong password) with its own dialog and keeps going. But OpenInTabAsync's
            // OWN bookkeeping around it — EnsureActiveTabRegistered / ActivateContext /
            // RebuildTabStrip — has no try/catch of its own, so an exception from any of those for
            // file N used to silently abort the whole batch: a multi-select Open of five files could
            // open one and never attempt the other four, with nothing telling the user why.
            int failed = 0;
            foreach (var file in dlg.FileNames)
            {
                try
                {
                    await OpenInTabAsync(file);
                }
                catch (Exception ex)
                {
                    failed++;
                    Telemetry.TrackCrash(ex, "Open.MultiSelect", recoverable: true);
                }
            }
            if (failed > 0)
                SetStatus(dlg.FileNames.Length == 1
                    ? "That file could not be opened."
                    : $"Opened {dlg.FileNames.Length - failed} of {dlg.FileNames.Length} selected file(s) — {failed} failed.");
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
                    AppendPdfFileToDoc(doc, file);
                SaveTempAndReload();
                SetStatus($"Merged {dlg.FileNames.Length} file(s) - {_doc?.PageCount} total pages");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Merge failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Appends every page of <paramref name="file"/> to <paramref name="doc"/>, carrying its
        /// named-destination links across. Shared by File ▸ Merge and the #172 Pages-sidebar file drop, so
        /// there is exactly one PDF-append path and both get the link rewriting. Throws on an unreadable /
        /// encrypted source; callers decide whether that aborts the batch or just skips the file.
        /// </summary>
        private void AppendPdfFileToDoc(PdfDocument doc, string file)
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

        /// <summary>
        /// Builds a map of named destination string → 0-based page index from a source document's
        /// /Dests dictionary and /Names /Dests name tree.
        /// </summary>
        private static Dictionary<string, int> BuildNamedDestMap(PdfDocument src)
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

        private static void WalkNameTree(PdfDocument src, PdfDictionary node, Dictionary<string, int> map)
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
            // A PDF cannot have zero pages, and Ctrl+A followed by Delete now makes that a single
            // gesture. Refuse rather than write a document nothing can reopen.
            if (selected.Count >= doc.PageCount)
            {
                TdpDialog.Show(this, "A PDF must keep at least one page.\n\nTo discard the whole document, close it instead.",
                    "TDPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
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
            try { if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath)) parts.Add(FormatFileSize(new FileInfo(filePath).Length)); } catch { }
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
            // New / merged-on-drop / imported-image / raster-recovered docs have no real on-disk home
            // (their working file is a temp copy), so an in-place save would silently write to
            // %TEMP%. Route them to Save As. OriginalPath — not _currentFile — is the destination.
            if (_ctx.IsUntitled || string.IsNullOrEmpty(_ctx.OriginalPath)) { SaveAs_Click(sender, e); return; }
            await SaveInPlaceAsync();
        }

        // Pre-save document normalization (ports upstream KillerPDF v1.6.3/v1.6.4 conformance
        // fixes). Every TDPdf save fully rewrites the file through PdfSharpCore, so we scrub three
        // classes of structural corruption immediately before writing. All three are semantic
        // no-ops on healthy documents and also HEAL files damaged by other tools/older builds when
        // re-saved. Called on the UI thread before dispatching any doc.Save(...).
        private static void NormalizeDocumentForSave(PdfDocument doc)
        {
            ScrubEmptyOutlines(doc);         // #103: never write a dangling /Outlines reference
            ScrubDegeneratePageBoxes(doc);   // never write a zero-size /CropBox or /MediaBox (Adobe out-of-range)
            ScrubDeadSignatures(doc);        // a rewrite voids signatures; never ship a dead one (PDF/A 6.4.3)
        }

        private static double RectNum(PdfItem item) =>
            item is PdfReal r ? r.Value : item is PdfInteger n ? n.Value : 0;

        // #103 (upstream v1.6.3): PdfSharpCore's writer can emit the catalog's /Outlines reference
        // without ever writing the (empty, lazily created) outlines object itself - a dangling xref
        // entry that strict parsers, including PdfSharpCore on reopen, refuse. An outlines dictionary
        // with no /First contains no bookmarks, so dropping the entry is a semantic no-op that keeps
        // the file consistent. Real bookmark trees (/First present) are left untouched.
        private static void ScrubEmptyOutlines(PdfDocument doc)
        {
            try
            {
                var cat = doc.Internals.Catalog;
                var item = cat.Elements["/Outlines"];
                if (item is null) return;
                if (DerefItemStatic(item) is not PdfDictionary o || o.Elements["/First"] is null)
                    cat.Elements.Remove("/Outlines");
            }
            catch { /* malformed catalog - leave the save as-is */ }
        }

        // Upstream v1.6.3 (/CropBox), extended here to /MediaBox: PdfSharpCore's PdfPage.MediaBox and
        // .CropBox property GETTERS have create-on-read semantics, so touching page.CropBox - or
        // page.Width/page.Height, which read MediaBox - on a page that carries no such entry plants an
        // empty [0 0 0 0] box into the page dictionary. A zero-size page box saves to disk and Adobe
        // then rejects the page as "dimensions out-of-range" (Chrome falls back to another box, which is
        // why such files still open there). Both boxes are INHERITABLE page attributes, so the pages
        // that get one planted are exactly the ones whose real box lives on an ancestor /Pages node.
        //
        // /CropBox: dropping a degenerate one is a semantic no-op - the page falls back to its MediaBox.
        // /MediaBox: every page needs one, so drop the degenerate entry and re-plant the box the page
        // tree really specifies; only when nothing usable is inheritable do we leave it absent, which at
        // least renders (viewers substitute a default page size) where a zero-size box does not.
        // Both HEAL files damaged by other tools or older builds when re-saved; real boxes are untouched.
        private static void ScrubDegeneratePageBoxes(PdfDocument doc)
        {
            try
            {
                for (int i = 0; i < doc.PageCount; i++)
                {
                    var page = doc.Pages[i];
                    var elements = page.Elements;

                    if (IsDegenerateBox(elements["/CropBox"]))
                        elements.Remove("/CropBox");

                    if (IsDegenerateBox(elements["/MediaBox"]))
                    {
                        elements.Remove("/MediaBox");
                        // With the bad entry gone, ask the page tree what this page's box actually is.
                        // Re-planting it explicitly keeps the page valid no matter how the writer treats
                        // the inherited attribute, and is identical in meaning to inheriting it.
                        if (ReadInheritedPageBox(page, "/MediaBox") is { Width: > 1, Height: > 1 } box)
                            elements.SetRectangle("/MediaBox",
                                new PdfRectangle(new XPoint(box.X, box.Y), new XPoint(box.Right, box.Top)));
                    }

                    // Upstream v1.7.1 (#169): PDF 32000-1 14.11.2 requires /CropBox to lie INSIDE
                    // /MediaBox. A rotated page could be written with a portrait media box and a
                    // landscape crop box, a malformed combination that strict validators reject and
                    // that leaves renderers disagreeing about the page size. Removing the invalid crop
                    // is lossless: the page falls back to its complete media box rather than clipping
                    // content away. Done AFTER the media-box healing above so the comparison is against
                    // a box that is actually usable, and skipped entirely when it is not — a bad media
                    // box must never be a reason to delete a good crop box.
                    const double outsideTol = 0.01;
                    if (ReadOwnPageBox(elements["/CropBox"]) is { } crop &&
                        ReadInheritedPageBox(page, "/MediaBox") is { Width: > 1, Height: > 1 } media &&
                        (crop.X     < media.X     - outsideTol || crop.Y   < media.Y   - outsideTol ||
                         crop.Right > media.Right + outsideTol || crop.Top > media.Top + outsideTol))
                        elements.Remove("/CropBox");
                }
            }
            catch { /* malformed page tree - leave the save as-is */ }
        }

        // Reads a page's OWN /MediaBox or /CropBox entry — no /Parent walk — as a normalized PageBox,
        // or null when the entry is absent or cannot be interpreted with certainty. Deliberately
        // stricter than ReadInheritedPageBox, which reads for geometry: this one feeds the DESTRUCTIVE
        // scrub decisions above, so anything ambiguous reads as null and is then left alone rather than
        // deleted. The box can be a parsed PdfArray (loaded from disk), a PdfRectangle (planted in
        // memory by the lazy getter or by GetRectangle writing its conversion back), or an indirect
        // reference to either.
        private static PageBox? ReadOwnPageBox(PdfItem? item)
        {
            if (item is null) return null;
            if (item is not PdfArray and not PdfRectangle) item = DerefItemStatic(item);

            if (item is PdfRectangle rect)
                return Normalize(rect.X1, rect.Y1, rect.X2, rect.Y2);
            if (item is PdfArray arr && arr.Elements.Count == 4 &&
                arr.Elements[0] is PdfReal or PdfInteger && arr.Elements[1] is PdfReal or PdfInteger &&
                arr.Elements[2] is PdfReal or PdfInteger && arr.Elements[3] is PdfReal or PdfInteger)
                return Normalize(RectNum(arr.Elements[0]), RectNum(arr.Elements[1]),
                                 RectNum(arr.Elements[2]), RectNum(arr.Elements[3]));
            return null;

            static PageBox Normalize(double x1, double y1, double x2, double y2) =>
                new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        }

        // True when the entry is present, readable as a rectangle, and zero/sub-point sized. Anything we
        // cannot interpret returns false so it is left alone rather than destroyed.
        private static bool IsDegenerateBox(PdfItem? item) =>
            ReadOwnPageBox(item) is { } box && (box.Width < 1 || box.Height < 1);

        // Upstream v1.6.4: a TDPdf save fully REWRITES the file, which mathematically invalidates any
        // existing digital signature: its /ByteRange and digest describe the old bytes (ISO 19005-2,
        // 6.4.3 requires the digest to cover the entire file). Carrying the dead signature forward
        // misleads viewers and fails PDF/A validation, so strip signature VALUES (/V) from signature
        // fields and the catalog's /Perms certification (DocMDP / usage rights) that references them.
        // The empty fields stay and can be re-signed.
        private static void ScrubDeadSignatures(PdfDocument doc)
        {
            try
            {
                var cat = doc.Internals.Catalog;
                cat.Elements.Remove("/Perms");
                var acroItem = cat.Elements["/AcroForm"];
                if (acroItem is null || DerefItemStatic(acroItem) is not PdfDictionary acro) return;
                var fieldsItem = acro.Elements["/Fields"];
                if (fieldsItem is not null && DerefItemStatic(fieldsItem) is PdfArray fields)
                    ScrubSigFieldValues(fields, 0);
            }
            catch { /* malformed catalog - leave the save as-is */ }
        }

        private static void ScrubSigFieldValues(PdfArray fields, int depth)
        {
            if (depth > 8) return;   // defensive: malformed circular /Kids
            foreach (var item in fields.Elements)
            {
                if (item is null || DerefItemStatic(item) is not PdfDictionary field) continue;
                if (field.Elements.GetName("/FT") == "/Sig" && field.Elements["/V"] is not null)
                    field.Elements.Remove("/V");
                var kidsItem = field.Elements["/Kids"];
                if (kidsItem is not null && DerefItemStatic(kidsItem) is PdfArray kids)
                    ScrubSigFieldValues(kids, depth + 1);
            }
        }

        /// <summary>
        /// Saves the active document back over the file the user opened. <paramref name="removingPassword"/>
        /// only changes the wording of the success status: the write itself IS the password removal,
        /// because the working document is already decrypted and PdfSharpCore never re-encrypts.
        /// </summary>
        private async Task SaveInPlaceAsync(bool removingPassword = false)
        {
            using var op = Telemetry.StartOperation("SaveInPlace");
            if (_doc is null || _currentFile is null) return;
            CommitActiveTextBox();
            // Capture the destination once. This is the user's real document (OriginalPath), NOT the
            // working path: _currentFile points into %TEMP% after a decrypt-on-open, after any
            // structural edit (SaveTempAndReload) and after a #106 repair, and saving there would
            // update a temp file that is then deleted. Callers with no on-disk home route to Save As
            // before getting here; the fallback keeps this method total.
            string targetFile = _ctx.OriginalPath ?? _currentFile;
            string status = "";

            // The unit of work retried by RunSaveWithRecoveryAsync. Reads _doc fresh each call so a
            // repair (which swaps _doc for a rebuilt copy) is picked up, and re-bakes annotations from
            // _annotations every time, so a retried save keeps all of the user's edits.
            async Task DoSaveAsync()
            {
                var doc = _doc!;
                NormalizeDocumentForSave(doc);   // strip dangling /Outlines, zero-size /CropBox, dead signatures
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
                if (_ctx.WasProtected)
                {
                    // #149: the file on disk no longer carries its password — PdfSharpCore writes no
                    // /Encrypt unless a password is set on the document, and TDPdf cannot re-encrypt.
                    // Say so rather than dropping the protection silently, and clear the flag: from
                    // here on this tab's file is unprotected.
                    _ctx.WasProtected = false;
                    status = removingPassword
                        ? $"Password protection removed — {System.IO.Path.GetFileName(targetFile)}"
                        : status + " (password protection removed)";
                }
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

        // #149 (upstream KillerPDF v1.6.6): saves the open document back over the user's file with
        // its password protection dropped. There is nothing to strip at save time — the working
        // document has been decrypted since it was opened — so this IS an in-place save; what the
        // command adds is an explicit, named way to ask for it (and the confirmation, because it
        // rewrites the user's file irreversibly). Routed through SaveInPlaceAsync so it gets the
        // same NormalizeDocumentForSave scrubs and #106 repair retry as every other save.
        private async void RemovePassword_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            if (!_ctx.WasProtected)
            {
                TdpDialog.Show(this, "This document is not password protected.",
                    "TDPdf", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // No on-disk home to write back over (merged / imported / raster-recovered): the user
            // has to say where the unprotected copy goes. Save As drops the protection just the same.
            if (_ctx.IsUntitled || string.IsNullOrEmpty(_ctx.OriginalPath)) { SaveAs_Click(sender, e); return; }

            var res = TdpDialog.Show(this,
                $"Save \"{System.IO.Path.GetFileName(_ctx.OriginalPath)}\" without its password protection?\n\n" +
                "The file is rewritten in place and anyone will be able to open it. TDPdf cannot put the password back.",
                "Remove Password", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (res != MessageBoxResult.OK) return;

            await SaveInPlaceAsync(removingPassword: true);
        }

        // Remove Password stays visible for discoverability but is only actionable when the ACTIVE
        // document actually came from a protected file. Recomputed every time the menu opens: the
        // flag is per tab and is cleared by the save that drops the protection.
        private void FileMenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            _removePasswordMenuItem.IsEnabled = _doc is not null && _ctx.WasProtected;
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
                NormalizeDocumentForSave(doc);   // strip dangling /Outlines, zero-size /CropBox, dead signatures
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

                // The copy the user just chose is this tab's document from here on: point the tab
                // name and OriginalPath (the in-place save target, the session entry and the recent
                // list) at it, so a following Ctrl+S updates THAT file rather than the one
                // originally opened. The WORKING path (_currentFile) is deliberately left alone:
                // with pending annotations the saved file already has them baked in while
                // _annotations still holds them, so re-rendering from it would draw them twice.
                // OriginalPath is retargeted unconditionally — unlike an OPEN from %TEMP% (an
                // attachment or working artifact with no lasting home), a Save As INTO it is a
                // destination the user explicitly picked, and Ctrl+S must never silently fall back
                // to writing the file they saved away from. Recents keeps its own eligibility gate.
                _ctx.IsUntitled   = false;
                _ctx.OriginalPath = targetFile;
                SetDisplayName(System.IO.Path.GetFileName(targetFile));
                if (IsRecentEligiblePath(targetFile)) AddRecentFile(targetFile);
                if (_ctx.WasProtected)
                {
                    // #149: the saved copy carries no password — PdfSharpCore cannot re-encrypt it.
                    _ctx.WasProtected = false;
                    status += " (password protection removed)";
                }
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
                NormalizeDocumentForSave(doc);   // strip dangling /Outlines, zero-size /CropBox, dead signatures
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
                // #149: a flatten always writes a brand-new rasterized document, so the export is
                // unprotected even when the source was. Say so — but do NOT clear WasProtected:
                // this wrote to a file the user picked, and THIS tab's own document is untouched.
                var flattenNote = _ctx.WasProtected ? " (password protection removed)" : string.Empty;
                SetStatus($"Flattened PDF saved to {System.IO.Path.GetFileName(targetFile)}{flattenNote}");
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
        // shared PdfiumInterop.TryPdfiumRepair helper (no second repair implementation). Fully guarded: returns false
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
                    () => TDPdf.Services.PdfiumInterop.TryPdfiumRepair(current!, fixedPath));
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

            // Burn any pending annotations into a temp printable copy, preview/print from
            // that, then reload the clean document afterward so the on-screen editing
            // state is preserved.
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
            PdfDocument? reopened = null;
            string reopenedPath = path;
            try
            {
                reopened = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            }
            catch (Exception ex) when (TDPdf.Services.PdfDocumentService.IsOwnerPasswordException(ex))
            {
                // Same trap as PdfDocumentService.OpenCore: PdfSharpCore's ReadOnly parser walks into
                // a broken hint table on a malformed linearized file and throws an array-index error.
                // The throw happens INSIDE this catch clause, so nothing on this try could catch it —
                // and this method runs from Print_Click's finally block, where an escaping exception
                // replaces whatever was already in flight. Contain it, then try a PDFium-repaired copy.
                try
                {
                    reopened = PdfReader.Open(path, PdfDocumentOpenMode.ReadOnly);
                }
                catch
                {
                    var fixedPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        $"tdpdf_fixed_{Guid.NewGuid():N}.pdf");
                    try
                    {
                        if (TDPdf.Services.PdfiumInterop.TryPdfiumRepair(path, fixedPath))
                        {
                            reopened = PdfReader.Open(fixedPath, PdfDocumentOpenMode.Modify);
                            reopenedPath = fixedPath;
                        }
                    }
                    catch { reopened = null; }
                }
            }

            if (reopened is null)
            {
                // Nothing could reopen the pre-print copy. Keep the live document (which now has the
                // annotations burned in) rather than throwing out of Print_Click's finally block.
                SetStatus("Printed - the pre-print copy could not be reloaded; use Save As to keep your work");
                return;
            }

            _doc = reopened;
            _currentFile = reopenedPath;
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

        /// <summary>
        /// Draws the underline for a burned text annotation, when it has one.
        /// </summary>
        /// <remarks>
        /// #135: <c>XFontStyle.Underline</c> exists in the enum but PdfSharpCore's
        /// <c>DrawString</c> does not act on it — an underlined annotation would have looked right
        /// on screen and saved without the line, which is the same class of silent screen/PDF
        /// divergence that <c>WrapTextToWidth</c> exists to prevent. So draw it: one thin filled
        /// rectangle just under the baseline, scaled with the font so it stays proportional at any
        /// size or render resolution.
        /// </remarks>
        private static void DrawTextUnderline(XGraphics gfx, TextAnnotation ta, string line,
                                              XFont font, XBrush brush, double x, double baselineY,
                                              double sy)
        {
            if (!ta.Underline || string.IsNullOrEmpty(line)) return;
            double width = gfx.MeasureString(line, font).Width;
            if (width <= 0) return;
            double em = ta.FontSize * sy;
            gfx.DrawRectangle(brush, x, baselineY + em * 0.12, width, Math.Max(0.5, em * 0.06));
        }

        /// <summary>Bold/italic flags as the PdfSharpCore font style flags used when burning text (#182).</summary>
        private static XFontStyle ToXFontStyle(bool bold, bool italic) =>
            (bold ? XFontStyle.Bold : XFontStyle.Regular) | (italic ? XFontStyle.Italic : XFontStyle.Regular);

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

                // Annotation coordinates are positions on the bitmap PDFium rasterized, and PDFium
                // renders the page's VISIBLE box (/CropBox over /MediaBox, inheritance-aware) with
                // /Rotate applied. Scale and place against that same frame:
                //  * the raw page.Width/Height used before are media-box derived with an implicit
                //    (0,0) origin, so an inset or offset /CropBox displaced every annotation;
                //  * nothing here handled /Rotate at all, so on a quarter-turned page annotations
                //    were burned a quarter turn out of place and scaled on swapped axes (#169).
                // TDPdf keeps the angle on the page itself (RotatePages_Click writes /Rotate and
                // reloads), so the page dictionary is the authority — there is no separate map.
                int rot = ((page.Rotate % 360) + 360) % 360;
                var box = GetVisiblePageBox(page);
                bool quarterTurn = rot is 90 or 270;   // visual extent is the box's dims swapped
                double sx = (quarterTurn ? box.Height : box.Width) / renderW;
                double sy = (quarterTurn ? box.Width : box.Height) / renderH;

                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                // ...then turn the whole drawing back into the frame XGraphics writes in, so every
                // draw call below keeps working in canvas-scaled coordinates exactly as it always has.
                if (VisualToPageMatrix(rot, box, page.Height.Point) is XMatrix visualToPage)
                    gfx.MultiplyTransform(visualToPage, XMatrixOrder.Prepend);

                foreach (var annot in annots)
                {
                    switch (annot)
                    {
                        case TextAnnotation ta:
                        {
                            const double pad = 2;
                            // #168: the editor is a WPF TextBox and falls back per CHARACTER across
                            // every installed font, so anything typed looks right on screen;
                            // PdfSharpCore resolves ONE face and emits an empty box for every
                            // codepoint that face lacks. Pick a family that actually covers THIS
                            // annotation's text; PickFamily keeps the annotation's own chosen family
                            // whenever it can carry it, which is every Latin annotation, so nothing
                            // existing moves.
                            var font = TdpFontResolver.TryCreate(
                                FontCoverage.PickFamily(ta.FontName, ta.Content),
                                ta.FontSize * sy, ToXFontStyle(ta.Bold, ta.Italic));   // #135
                            // Nothing resolvable at all (no readable font directory, or a degenerate
                            // size): skip this ONE annotation rather than throw out of the save.
                            if (font is null) break;
                            double lineH = ta.FontSize * sy * 1.2;
                            var taColor = ta.GetColor();
                            var taBrush = new XSolidBrush(XColor.FromArgb(taColor.A, taColor.R, taColor.G, taColor.B));

                            if (ta.Width > 0)
                            {
                                // Fixed-width wrapping box: mirror the on-screen wrap (same font metrics)
                                // and the whiteout fill so the saved PDF matches the screen.
                                var wrapped = WrapTextToWidth(ta.Content, ta.FontSize, ta.Width - pad * 2,
                                                              ta.Bold, ta.Italic);
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
                                    {
                                        gfx.DrawString(line, font, taBrush, (ta.Position.X + pad) * sx, ty);
                                        DrawTextUnderline(gfx, ta, line, font, taBrush,
                                                          (ta.Position.X + pad) * sx, ty, sy);
                                    }
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
                                    {
                                        gfx.DrawString(line, font, taBrush, ta.Position.X * sx, ty);
                                        DrawTextUnderline(gfx, ta, line, font, taBrush,
                                                          ta.Position.X * sx, ty, sy);
                                    }
                                    ty += lineH;
                                }
                            }
                            break;
                        }

                        // Markup: one filled band per covered line. Matched before
                        // HighlightAnnotation — it is a subclass, and a plain highlight must keep
                        // writing exactly the single rectangle it always did.
                        case MarkupAnnotation mk:
                        {
                            var mkc = mk.GetColor();
                            var mkBrush = new XSolidBrush(XColor.FromArgb(mkc.A, mkc.R, mkc.G, mkc.B));
                            // TDPdf patch (#200, from upstream KillerPDF): a highlight burns with the
                            // Multiply blend mode so the colour darkens the paper and the text under
                            // it stays crisp instead of being washed out by an opaque overlay.
                            // Strikethrough and underline stay normal draws — a thin dark band
                            // multiplied over text would disappear.
                            XGraphicsState? mkState = null;
                            if (mk.Style == MarkupStyle.Highlight)
                            {
                                mkState = gfx.Save();
                                gfx.SetPdfBlendMode("Multiply");
                            }
                            try
                            {
                                foreach (var pr in mk.PaintRects())
                                    gfx.DrawRectangle(mkBrush,
                                        pr.X * sx, pr.Y * sy, pr.Width * sx, pr.Height * sy);
                            }
                            finally
                            {
                                // The grestore returns the blend mode to Normal.
                                if (mkState is not null) gfx.Restore(mkState);
                            }
                            break;
                        }

                        case HighlightAnnotation ha:
                        {
                            var hc = ha.GetColor();
                            var hBrush = new XSolidBrush(XColor.FromArgb(hc.A, hc.R, hc.G, hc.B));
                            // TDPdf patch (#200, from upstream KillerPDF): burn with Multiply so the
                            // highlight darkens the paper rather than covering the text.
                            var hState = gfx.Save();
                            gfx.SetPdfBlendMode("Multiply");
                            try
                            {
                                gfx.DrawRectangle(hBrush,
                                    ha.Bounds.X * sx, ha.Bounds.Y * sy,
                                    ha.Bounds.Width * sx, ha.Bounds.Height * sy);
                            }
                            finally
                            {
                                // The grestore returns the blend mode to Normal.
                                gfx.Restore(hState);
                            }
                            break;
                        }

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
                                case ShapeKind.Polygon:
                                {
                                    // PdfSharpCore's DrawPolygon closes the outline for us, so the
                                    // stored vertices (no repeated first point) map straight over.
                                    if (shp.Points.Count < 3) break;
                                    var poly = shp.Points
                                        .Select(p => new XPoint(p.X * sx, p.Y * sy)).ToArray();
                                    if (shpFill is not null)
                                        gfx.DrawPolygon(shpFill, poly, XFillMode.Alternate);
                                    gfx.DrawPolygon(shpPen, poly);
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
                            //
                            // Resolve the font FIRST: if nothing can be resolved the whole edit is
                            // skipped, because covering the original line and then drawing nothing on
                            // top would erase the page's own text (#168 safety).
                            //
                            // Keep the face styling detected on the original line (#182) so a
                            // bold/italic run does not save back as plain, and run the family through
                            // the same coverage check as placed text (#168) — the in-place editor is
                            // the path most likely to carry non-Latin content, since it starts from
                            // whatever the PDF already says.
                            var editFont = TdpFontResolver.TryCreate(
                                FontCoverage.PickFamily(tea.FontName, tea.NewContent),
                                tea.FontSize * sy, ToXFontStyle(tea.Bold, tea.Italic));
                            if (editFont is null) break;
                            var whiteRect = new XSolidBrush(XColors.White);
                            gfx.DrawRectangle(whiteRect,
                                (tea.OriginalBounds.X - 1) * sx, (tea.OriginalBounds.Y - 1) * sy,
                                (tea.OriginalBounds.Width + 2) * sx, (tea.OriginalBounds.Height + 2) * sy);
                            double ety = tea.Position.Y * sy + tea.FontSize * sy;
                            var teaColor = tea.GetColor();
                            var teaBrush = new XSolidBrush(XColor.FromArgb(teaColor.A, teaColor.R, teaColor.G, teaColor.B));
                            gfx.DrawString(tea.NewContent, editFont, teaBrush, tea.Position.X * sx, ety);
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
                                        int rotation = ((iea.Rotation % 360) + 360) % 360;
                                        if (rotation == 0)
                                        {
                                            gfx.DrawImage(xImg,
                                                iea.TargetBounds.X * sx, iea.TargetBounds.Y * sy,
                                                iea.TargetBounds.Width * sx, iea.TargetBounds.Height * sy);
                                        }
                                        else
                                        {
                                            double cx = (iea.TargetBounds.X + iea.TargetBounds.Width / 2) * sx;
                                            double cy = (iea.TargetBounds.Y + iea.TargetBounds.Height / 2) * sy;
                                            bool imageQuarterTurn = rotation is 90 or 270;
                                            double drawW = (imageQuarterTurn ? iea.TargetBounds.Height : iea.TargetBounds.Width) * sx;
                                            double drawH = (imageQuarterTurn ? iea.TargetBounds.Width : iea.TargetBounds.Height) * sy;
                                            var state = gfx.Save();
                                            try
                                            {
                                                gfx.RotateAtTransform(rotation, new XPoint(cx, cy));
                                                gfx.DrawImage(xImg, cx - drawW / 2, cy - drawH / 2, drawW, drawH);
                                            }
                                            finally
                                            {
                                                gfx.Restore(state);
                                            }
                                        }
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

        private void SaveTempAndReload(bool keepAnnotations = false)
        {
            if (_doc is null || _currentFile is null) return;
            // Structural edits (delete / reorder / crop / transform) invalidate the overlay
            // annotations, whose canvas coordinates are tied to the pre-edit page geometry, so they
            // are cleared by default. Rotation is the one exception: it remaps its pages'
            // annotations through the turn beforehand (see RotatePages_Click) and passes
            // keepAnnotations: true so that unsaved work survives the reload.
            if (!keepAnnotations) _annotations.Clear();
            ClearFormState();
            InvalidateRenderCache();
            _contentEditor.ClearCache();
            // Rotate / delete / reorder / crop / transform all land here and all change page
            // geometry, so the flowing-selection character cache goes with the render cache. (The
            // cache key already changes — the working path is repointed at a fresh temp file just
            // below — but this keeps the invalidation explicit rather than incidental.)
            InvalidateTextRunCache();
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
                if (!TDPdf.Services.PdfiumInterop.TryPdfiumRepair(tempPath, fixedPath))
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
                BeginManualZoom();
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
                // No scrollable content — the wheel navigates pages directly. Still gated, so a
                // touchpad flick over a fitted page advances one page per deliberate gesture
                // instead of one per delta event (#205).
                e.Handled = true;
                if (_wheelFlipGate.TryConfirm(e.Delta, DateTime.UtcNow))
                    NavigatePageByWheel(e.Delta);
                return;
            }

            bool atTop    = PagePreviewPanel.VerticalOffset <= 0;
            bool atBottom = PagePreviewPanel.VerticalOffset >= PagePreviewPanel.ScrollableHeight - 1;
            if ((atTop && e.Delta > 0) || (atBottom && e.Delta < 0))
            {
                // Arriving at the boundary is not the same as asking to leave the page: a fast
                // scroll gets here with notches still in flight. The gate drops that momentum and
                // asks for one further deliberate gesture (#205).
                e.Handled = true;
                if (_wheelFlipGate.TryConfirm(e.Delta, DateTime.UtcNow))
                    NavigatePageByWheel(e.Delta);
                return;
            }
            ScrollWheel(e);   // normal scroll, boosted
        }

        // Separates a fast scroll's momentum tail from a deliberate page turn at the scroll
        // boundary (#205). See Services/WheelPageFlipGate.cs for the two conditions it applies.
        private readonly Services.WheelPageFlipGate _wheelFlipGate = new();

        // Horizontal scrolling and the page sidebar keep the established speed multiplier: the
        // ScrollViewer default (3 lines = 48 DIP per notch) is slow on tall documents, so those
        // scroll WheelScrollFactor times that instead. e.Delta is ±120 per notch on a standard
        // wheel (precision touchpads send smaller, more frequent deltas, which scale the same way).
        private const double WheelScrollFactor = 3.0;

        private void ScrollWheel(MouseWheelEventArgs e)
        {
            e.Handled = true;

            // Document scrolling follows the Wheel tab in Windows Mouse Properties rather than a
            // fixed distance, so a user who has set 1 line or "one screen at a time" — including
            // for accessibility — gets what they asked for. -1 is the sentinel for one screen.
            // Scaling by the raw delta keeps precision-touchpad movement smooth.
            int lines = SystemParameters.WheelScrollLines;
            double distance = lines < 0 ? PagePreviewPanel.ViewportHeight : lines * 16.0;
            PagePreviewPanel.ScrollToVerticalOffset(
                PagePreviewPanel.VerticalOffset - e.Delta * (distance / 120.0));

            // Any real content scroll starts the quiet period, so this gesture's tail cannot flip
            // the page when it reaches the boundary.
            _wheelFlipGate.NoteContentScroll(DateTime.UtcNow);
        }

        private void NavigatePageByWheel(int delta)
            => NavigatePageStep(delta > 0 ? -1 : 1);

        // Moves the page selection by one page — or one full SPREAD in Two-Page mode (#120, upstream
        // v1.6.3) — landing on the spread's left page so a press always advances to the NEXT spread
        // instead of re-showing the current one from its right page. direction: -1 = back, +1 =
        // forward. Returns true when the selection actually moved. Shared by the wheel edge-flip and
        // the keyboard page keys.
        private bool NavigatePageStep(int direction)
        {
            if (_doc is null) return false;
            int cur = PageList.SelectedIndex;
            if (cur < 0) cur = 0;
            int count = _doc.PageCount;
            if (_viewMode == ViewMode.TwoPage)
            {
                int baseIdx = SpreadStart(Math.Max(0, cur));   // left page of the current spread
                // #193: book layout steps cover → 1 → 3 → 5…, because the cover is a row of its
                // own; the classic pairing just walks the even indices two at a time.
                int target = _twoPageBook
                    ? (direction > 0 ? (baseIdx == 0 ? 1 : baseIdx + 2)
                                     : (baseIdx <= 1 ? 0 : baseIdx - 2))
                    : baseIdx + direction * 2;
                if (target == baseIdx || target < 0 || target >= count) return false;
                PageList.SelectedIndex = target;
                return true;
            }
            int t = cur + direction;
            if (t < 0 || t >= count) return false;
            PageList.SelectedIndex = t;
            return true;
        }

        // ── Jump history (Alt+Left / Alt+Right / mouse back-forward buttons) — upstream v1.6.4 ──────
        // Page-granular: recorded at the long-jump sites (bookmark click, internal link, the page jump
        // box, Home/End) so a reader thrown 30 pages by a bookmark can retrace the hop. Per tab.

        /// <summary>Records the CURRENT page onto the back stack. Call BEFORE performing a jump.</summary>
        private void RecordNavJump()
        {
            if (_doc is null) return;
            int cur = Math.Max(0, PageList.SelectedIndex);
            if (_navBack.Count > 0 && _navBack.Peek() == cur) { _navForward.Clear(); return; }
            _navBack.Push(cur);
            _navForward.Clear();   // a fresh jump invalidates the forward chain, like a browser
        }

        private void NavHistoryGo(bool back)
        {
            if (_doc is null) return;
            var from = back ? _navBack : _navForward;
            var to   = back ? _navForward : _navBack;
            if (from.Count == 0) return;
            int cur = Math.Max(0, PageList.SelectedIndex);
            int target = from.Pop();
            to.Push(cur);
            if (target >= 0 && target < _doc.PageCount)
                PageList.SelectedIndex = target;
        }

        // Mouse back / forward buttons (XButton1 / XButton2) retrace the same history, like a browser.
        // Registered on the window in the constructor.
        private void NavHistory_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.XButton1)      { NavHistoryGo(back: true);  e.Handled = true; }
            else if (e.ChangedButton == MouseButton.XButton2) { NavHistoryGo(back: false); e.Handled = true; }
        }

        // Keyboard access to the right-click menu (Menu key / Shift+F10): opens the annotation canvas
        // context menu centered on the page rather than at the mouse. Placement is set to Center just
        // for this open and restored on close, so the mouse path keeps its open-at-cursor behavior.
        private void OpenContextMenuAtSelection()
        {
            if (_doc is null || _annotationCanvas.ContextMenu is not ContextMenu cm) return;
            cm.PlacementTarget = _annotationCanvas;
            var prevPlacement = cm.Placement;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
            void Restore(object? s, RoutedEventArgs a) { cm.Placement = prevPlacement; cm.Closed -= Restore; }
            cm.Closed += Restore;
            cm.IsOpen = true;
        }

        private void Zoom_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ZoomViewModel.ZoomLevel))
                ApplyZoom();
        }

        /// <summary>
        /// Marks the zoom about to be set as an EXPLICIT manual one (#201): it stops the view
        /// tracking the window size, exactly as before, and tells <see cref="SaveZoomSetting"/> to
        /// remember the resulting level so the next document opens at it. Every deliberate manual
        /// path goes through here — the zoom dropdown's fixed levels, Ctrl+0 / Ctrl+1, Zoom In /
        /// Out / Reset, and Ctrl+wheel.
        /// </summary>
        private void BeginManualZoom()
        {
            _zoomFitMode = ZoomFitMode.None;
            _manualZoomIntent = true;
        }

        private void ChangeZoomByCommand(ZoomChange change)
        {
            // An explicit zoom stops the view tracking the window size, exactly as the equivalent
            // toolbar buttons do. Without this, Ctrl+0 landed on 100% and then the next window
            // resize snapped straight back to the fit it was on.
            BeginManualZoom();
            switch (change)
            {
                case ZoomChange.In:
                    Zoom.ZoomIn();
                    break;
                case ZoomChange.Out:
                    Zoom.ZoomOut();
                    break;
                case ZoomChange.Reset:
                    // True 100%, in every view mode: Zoom.ZoomLevel is true zoom (see
                    // DisplayZoomFactor), so this needs no per-view-mode conversion.
                    Zoom.Reset();
                    break;
            }
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e) { BeginManualZoom(); Zoom.ZoomIn(); }

        private void ZoomOut_Click(object sender, RoutedEventArgs e) { BeginManualZoom(); Zoom.ZoomOut(); }

        private void ResetZoom_Click(object sender, RoutedEventArgs e) { BeginManualZoom(); Zoom.Reset(); }

        // ============================================================
        // True zoom vs. layout scale        (#154, upstream "true 100%")
        // ============================================================
        //
        // Zoom.ZoomLevel is TRUE zoom: 1.0 = the page at natural size, 1 PDF point = 1/72".
        // The thing the ScaleTransform actually scales, though, is the page TILE, and outside
        // Continuous view that tile is the render-dimension bitmap, not the natural page:
        //
        //   PdfDocumentService.RenderPageAsync rasterizes into a square box of
        //   RenderBoxDip * renderScale pixels, so the page's LONGEST side lands on RenderBoxDip
        //   DIPs and the tile's DIP width is RenderBoxDip * widthPt / longestPt. The natural DIP
        //   width is widthPt * 96/72, so
        //
        //       tileDip / naturalDip = RenderBoxDip * 72/96 / longestPt = 1152 / longestPt
        //
        //   which is ~1.37 for A4 (longest 841.89 pt) and ~1.45 for US Letter (792 pt). Note it
        //   depends only on the LONGEST side, so a 90/270 page rotation cannot change it.
        //
        // Continuous view is exempt: SetupContinuousView lays its slots out against the natural
        // width (_continuousPageW = Width.Point * 96/72), so its factor is 1 and its zoom already
        // read as true zoom — which is why 100% used to mean two different sizes depending on the
        // view mode. Everything user-facing (presets, the readout, the min/max clamp) lives in
        // true zoom; only the three places that touch layout convert, via LayoutZoomScale.
        private double DisplayZoomFactor()
        {
            if (_viewMode == ViewMode.Continuous || _doc is null) return 1.0;
            // The page in the PRIMARY tile, which is what the transform is being sized against.
            // RenderPage stamps it on PageImage.Tag; before the first render fall back to the
            // sidebar selection.
            int idx = PageImage.Tag is int tagged ? tagged : PageList.SelectedIndex;
            if (idx < 0 || idx >= _doc.PageCount) return 1.0;
            var page = _doc.Pages[idx];
            double longestPt = Math.Max(page.Width.Point, page.Height.Point);
            if (longestPt <= 0) return 1.0;
            return TDPdf.Services.PdfDocumentService.RenderBoxDip * (72.0 / 96.0) / longestPt;
        }

        /// <summary>
        /// The current true zoom expressed as the scale the page tile's layout box needs — the
        /// value for the ScaleTransform, the render DPI, and any arithmetic in pre-zoom
        /// (tile-layout) coordinates. Identity with <see cref="ZoomViewModel.ZoomLevel"/> in
        /// Continuous view.
        /// </summary>
        private double LayoutZoomScale
        {
            get
            {
                double factor = DisplayZoomFactor();
                return factor > 0 ? Zoom.ZoomLevel / factor : Zoom.ZoomLevel;
            }
        }

        /// <summary>
        /// Pushes the current true zoom onto the page transform. Separate from
        /// <see cref="ApplyZoom"/> because the conversion depends on the page in the primary tile
        /// and the view mode, so it also has to run when THOSE change at a constant zoom (a
        /// document of mixed page sizes, or entering Continuous) — and unlike ApplyZoom it neither
        /// re-renders nor persists, so RenderPage can call it without recursing.
        /// </summary>
        private void SyncLayoutZoom()
        {
            if (_pageContentGrid.LayoutTransform is ScaleTransform st)
            {
                double scale = LayoutZoomScale;
                st.ScaleX = scale;
                st.ScaleY = scale;
            }
        }

        // ---- Zoom churn detector (#131) ------------------------------------------------------
        //
        // The text-editor bug above was only ever VISIBLE because an editor happened to be alive
        // while ApplyZoom repeated; TextEditorCommitDeferred fires nowhere else, so the fleet data
        // could not say whether placing a box STARTED the repetition or merely exposed a loop that
        // was always running. With the commit gone, that signal disappears entirely — and a UI
        // re-rendering the page ~23 times a second (each pass cancelling a render, re-fitting, and
        // writing user.config) is a defect whether or not anything is being destroyed by it.
        //
        // So report the loop directly instead: count ApplyZoom calls in a rolling window and emit
        // ONE event per burst naming the caller that dominated it. A C# method name is a
        // compile-time constant, so this cannot carry document or user content.
        //
        // TWO details this gets wrong if written naively, both of which make the event worthless:
        //
        // 1. The name must be the ORIGINATOR, not ApplyZoom's caller. ApplyZoom has three call
        //    sites and every zoom write in the app arrives through one of them —
        //    Zoom_PropertyChanged — so [CallerMemberName] here reports the fan-in point on
        //    essentially every event. A re-fit loop, a pinch and Ctrl+wheel would be
        //    indistinguishable. ZoomViewModel.LastZoomOrigin is the frame above, and is what
        //    separates FitToWidth from a person turning a wheel.
        // 2. It must fire on SUSTAINED churn only. A rate alone is not a defect signature: the
        //    sidebar's 250 ms width animation re-fits on every frame by design (see the comment on
        //    AnimateSidebarWidth), dragging a window edge does the same, and one flick of
        //    Ctrl+wheel walks nine presets. All three clear any threshold worth setting. What was
        //    pathological in production was that the rate held with no one touching anything, so
        //    require the window to STAY above threshold for ZoomChurnSustainMs before reporting.
        private const int ZoomChurnThreshold = 8;
        private static readonly TimeSpan ZoomChurnWindow = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ZoomChurnSustain = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ZoomChurnReportInterval = TimeSpan.FromMinutes(15);
        private readonly Queue<(DateTime At, string Via)> _zoomApplyLog = new();
        private DateTime _churnAboveThresholdSinceUtc = DateTime.MinValue;
        private DateTime _lastZoomChurnReportUtc = DateTime.MinValue;

        private void NoteZoomApplied(string? via)
        {
            var now = DateTime.UtcNow;
            // The originator, when there is one to have. RerenderCurrentPage and the (dead) DpiChanged
            // handler reach ApplyZoom without going through the view model at all, and for those the
            // compiler-supplied name IS the answer.
            string origin = (via == nameof(Zoom_PropertyChanged) ? Zoom.LastZoomOrigin : via) ?? via ?? "unknown";
            _zoomApplyLog.Enqueue((now, origin));
            while (_zoomApplyLog.Count > 0 && now - _zoomApplyLog.Peek().At > ZoomChurnWindow)
                _zoomApplyLog.Dequeue();

            if (_zoomApplyLog.Count < ZoomChurnThreshold)
            {
                _churnAboveThresholdSinceUtc = DateTime.MinValue;   // the burst broke; start over
                return;
            }
            if (_churnAboveThresholdSinceUtc == DateTime.MinValue) _churnAboveThresholdSinceUtc = now;
            if (now - _churnAboveThresholdSinceUtc < ZoomChurnSustain) return;
            if (now - _lastZoomChurnReportUtc < ZoomChurnReportInterval) return;
            _lastZoomChurnReportUtc = now;

            // The caller that contributed most of the burst — that is the one worth naming.
            var tally = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (_, caller) in _zoomApplyLog)
                tally[caller] = tally.TryGetValue(caller, out int c) ? c + 1 : 1;
            string dominant = "unknown";
            int best = 0;
            foreach (var pair in tally)
                if (pair.Value > best) { best = pair.Value; dominant = pair.Key; }

            Telemetry.TrackEvent("Zoom.Churn", new Dictionary<string, string>
            {
                ["Count"] = _zoomApplyLog.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Via"] = dominant,
                ["ViaCount"] = best.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["SustainedMs"] = ((int)(now - _churnAboveThresholdSinceUtc).TotalMilliseconds)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["FitMode"] = _zoomFitMode.ToString(),
                ["ViewMode"] = _viewMode.ToString()
            });
        }

        /// <param name="via">
        /// Compile-time name of the calling method, supplied by the compiler, for the churn
        /// detector below. See <see cref="NoteZoomApplied"/>.
        /// </param>
        private void ApplyZoom([CallerMemberName] string? via = null)
        {
            SyncLayoutZoom();
            NoteZoomApplied(via);
            // #131: a zoom change must NOT settle the live text editor, and never needed to.
            //
            // 1.23.7.0 tried to keep only the AUTOMATIC re-fits out of the commit chokepoint
            // (`if (!_applyingFitZoom) CommitActiveTextBox();`). Production said no: 54 of 54
            // editor destructions across the fleet arrived here, `Via=ApplyZoom`, with the flag
            // false — and at ~23 Hz, sustained, so the 400 ms grace added alongside it merely
            // moved every death to 409-456 ms. Upstream KillerPDF has never had this call on any
            // automatic path; its chokepoint is reached only from tool/tab/page switches, save,
            // print, close, Enter and blur.
            //
            // The premise was wrong, not the guard. The page tile, AnnotationCanvas and
            // TextEditorCanvas are all sized from PdfDocumentService's fixed RenderBoxDip, and the
            // zoom is an ancestor LayoutTransform on PageContentGrid — so the editor's
            // Canvas.Left/Top and the TextAnnotation.Position they become are the same numbers at
            // every zoom. Nothing about a zoom needs the box settled; the box scales with the page.
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

        // Persists TRUE zoom (see DisplayZoomFactor), which is also what the ctor restores, so the
        // stored number means the percentage the user last saw. An older user.config holds the old
        // layout-scale value; reinterpreting it is harmless because the two spaces share the same
        // 5%-400% range and the layout scale was always the LARGER of the pair, so the worst an
        // upgrade can do is open ~27-31% smaller once — and ApplyViewModeOnOpen re-fits on the
        // first document anyway.
        private void SaveZoomSetting()
        {
            try
            {
                TDPdf.Properties.Settings.Default.LastZoomLevel = Zoom.ZoomLevel;
                // #201: the other half of the remembered fit. _manualZoomIntent is raised only by
                // BeginManualZoom — the zoom dropdown's fixed levels, Ctrl+0 / Ctrl+1, the zoom
                // buttons and Ctrl+wheel — and lowered by both fits, so this records zooms a person
                // chose and never one the app computed for a window that no longer exists.
                // Recording it also retires any remembered fit: the last explicit zoom decision
                // wins, whichever kind it was, which is the same single-preference model the fit
                // side already uses (SaveDefaultFitMode clears this in return).
                if (_manualZoomIntent)
                {
                    TDPdf.Properties.Settings.Default.LastManualZoom = Zoom.ZoomLevel;
                    TDPdf.Properties.Settings.Default.DefaultFitMode = ZoomFitMode.None.ToString();
                }
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
            // #131: this handler cannot assume it was called by a person. ZoomBox.SelectedItem is
            // TwoWay-bound to Zoom.SelectedLevel, and ZoomViewModel.OnZoomLevelChanged rewrites that
            // property on EVERY zoom change from every source, so the binding echoes each
            // programmatic zoom straight back here as a SelectionChanged. Acting on the echo made
            // the app zoom itself: it called BeginManualZoom — silently cancelling the fit the user
            // had chosen — and wrote a zoom nobody asked for, re-entering ApplyZoom with no user
            // behind it.
            //
            // The view model marks the selection it pushes, so the echo is identified by IDENTITY.
            // Do NOT try to identify it arithmetically by comparing the picked preset against the
            // current zoom: whatever the ordering, the two agree by the time this runs, so such a
            // test cannot separate an echo from a real pick and swallows both.
            if (Zoom.ConsumeMirrorEcho(option)) return;
            //
            // The two fit entries can never be echoes anyway — FindPreset only ever returns an
            // option with a numeric ZoomLevel, so a fit reaches this handler only because a person
            // picked it. Picking a fit from the dropdown is an explicit preference, so it is
            // remembered and applied to the next document opened (upstream v1.7.1).
            if (option.IsFitWidth) { SaveDefaultFitMode(ZoomFitMode.Width); FitToWidth(); return; }
            if (option.IsFitPage) { SaveDefaultFitMode(ZoomFitMode.Page); FitToPage(); return; }
            if (option.ZoomLevel is not double zoom) return;
            // User picked an explicit zoom level — stop tracking the window size.
            BeginManualZoom();
            Zoom.SetZoomLevel(zoom);
        }

        // ============================================================
        // Remembered fit mode              (upstream v1.7.1)
        // ============================================================
        //
        // Only the two EXPLICIT ways to ask for a fit write this — the zoom dropdown and
        // Ctrl+2 / Ctrl+3. The fits the app applies for you (on open, on a view-mode switch, on a
        // window resize) deliberately do not, or the preference could never differ from whatever
        // the last view mode happened to want.
        private static void SaveDefaultFitMode(ZoomFitMode mode)
        {
            try
            {
                TDPdf.Properties.Settings.Default.DefaultFitMode = mode.ToString();
                // #201: asking for a fit retires the remembered manual zoom. The two are one
                // preference — "what the user last explicitly asked the zoom to be" — so exactly
                // one of them can be live at a time, and ApplyViewModeOnOpen only reaches the
                // manual branch when the fit side reads None.
                TDPdf.Properties.Settings.Default.LastManualZoom = 0;
                TDPdf.Properties.Settings.Default.Save();
            }
            catch { /* non-critical user preference */ }
        }

        /// <summary>The remembered fit preference, or <c>None</c> when the user has never set one.</summary>
        private static ZoomFitMode ReadDefaultFitMode()
        {
            try
            {
                return Enum.TryParse<ZoomFitMode>(TDPdf.Properties.Settings.Default.DefaultFitMode, out var mode)
                    ? mode : ZoomFitMode.None;
            }
            catch { return ZoomFitMode.None; }
        }

        /// <summary>
        /// The remembered manual zoom (#201), or 0 when the user's last explicit zoom decision was
        /// a fit — or when they have never made one. Out-of-range values (a hand-edited or
        /// half-written user.config) read as 0 rather than being clamped into something plausible:
        /// a number that was never a legal zoom is not a preference worth honouring.
        /// </summary>
        private static double ReadLastManualZoom()
        {
            try
            {
                double z = TDPdf.Properties.Settings.Default.LastManualZoom;
                return z >= ZoomViewModel.MinZoomLevel && z <= ZoomViewModel.MaxZoomLevel ? z : 0;
            }
            catch { return 0; }
        }

        // The fits are computed against the LAYOUT boxes on screen (the tile, or the continuous
        // slot) and then multiplied back through DisplayZoomFactor, because Zoom.SetZoomLevel now
        // takes true zoom. Equivalently: viewW / naturalWidthInDips.
        private void FitToWidth()
        {
            double viewW = PagePreviewPanel.ActualWidth - 40;
            if (viewW <= 0) return;
            // Continuous view fits against the strip's natural page width (zoom-independent), not
            // the hidden primary PageImage. Its display factor is 1, so no conversion.
            if (_viewMode == ViewMode.Continuous)
            {
                if (_continuousPageW <= 0) return;
                _zoomFitMode = ZoomFitMode.Width;
                _manualZoomIntent = false;   // #201: a fit retires the remembered manual zoom
                Zoom.SetZoomLevel(viewW / _continuousPageW);
                return;
            }
            if (PageImage.Source is null || PageImage.ActualWidth <= 0) return;
            _zoomFitMode = ZoomFitMode.Width;
            _manualZoomIntent = false;   // #201
            Zoom.SetZoomLevel(viewW / PageImage.ActualWidth * DisplayZoomFactor());
        }

        private void FitToPage()
        {
            double viewW = PagePreviewPanel.ActualWidth - 40;
            double viewH = PagePreviewPanel.ActualHeight - 40;
            if (viewW <= 0 || viewH <= 0) return;
            // Continuous: the primary PageImage is collapsed there, so its ActualWidth is 0 and
            // this used to silently do nothing (Ctrl+3 and a remembered Fit Page were both dropped
            // in Continuous). Fit against the strip's own slot geometry instead — natural width by
            // the current page's natural height, both already in true-zoom space.
            if (_viewMode == ViewMode.Continuous)
            {
                if (_doc is null || _doc.PageCount == 0 || _continuousPageW <= 0) return;
                int idx = Math.Clamp(PageList.SelectedIndex, 0, _doc.PageCount - 1);
                var slotPage = _doc.Pages[idx];
                double pw = slotPage.Width.Point, ph = slotPage.Height.Point;
                int rot = ((slotPage.Rotate % 360) + 360) % 360;
                if (rot == 90 || rot == 270) (pw, ph) = (ph, pw);
                double slotH = _continuousPageW * Math.Max(0.1, ph / Math.Max(1, pw));
                _zoomFitMode = ZoomFitMode.Page;
                _manualZoomIntent = false;   // #201
                Zoom.SetZoomLevel(Math.Min(viewW / _continuousPageW, viewH / slotH));
                return;
            }
            if (PageImage.Source is null || PageImage.ActualWidth <= 0 || PageImage.ActualHeight <= 0) return;
            _zoomFitMode = ZoomFitMode.Page;
            _manualZoomIntent = false;   // #201
            Zoom.SetZoomLevel(Math.Min(viewW / PageImage.ActualWidth, viewH / PageImage.ActualHeight)
                              * DisplayZoomFactor());
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
            // #131: a pinch is as explicit as Ctrl+wheel and stops the view tracking the window,
            // the same as every other manual path. It never did so on its own — it only ever
            // reached BeginManualZoom by accident, through the zoom combo reading its own update
            // back as a user pick, and then only when the pinch happened to land on a preset.
            BeginManualZoom();
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
            // Same reasoning as Open_Click: OpenInTabAsync's own bookkeeping has no try/catch of its
            // own, so one bad item in the drop used to be able to abort every item after it with no
            // indication why fewer tabs than expected showed up.
            int failed = 0;
            foreach (var f in found)
            {
                try
                {
                    if (IsPdfPath(f))
                    {
                        await OpenInTabAsync(f);
                        // A PDF that came out of a dropped .zip lives in an extraction folder TDPdf made
                        // under %TEMP% and that the OS eventually clears — a working file, not the user's
                        // document, so it must never be an in-place save target. Dropping OriginalPath
                        // routes Ctrl+S to Save As. Files dropped directly keep their real path and save
                        // normally, including the ones a user genuinely keeps under %TEMP%.
                        if (IsUnderAnyDirectory(f, tempDirs) && _doc is not null &&
                            string.Equals(_currentFile, f, StringComparison.OrdinalIgnoreCase))
                            _ctx.OriginalPath = null;
                    }
                    else await OpenImagesAsImportedTabAsync(new[] { f }, System.IO.Path.GetFileName(f));
                }
                catch (Exception ex)
                {
                    failed++;
                    Telemetry.TrackCrash(ex, "Open.DroppedSeparately", recoverable: true);
                }
            }
            SetStatus(failed == 0
                ? $"Opened {found.Count} item(s) in separate tabs"
                : $"Opened {found.Count - failed} of {found.Count} item(s) in separate tabs — {failed} failed");
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

        // After OpenInTabAsync has loaded a working file TDPdf itself created, mark the active tab as
        // having no on-disk home. Guarded so a failed/reverted open (which returns to the previous
        // tab) is left untouched. <paramref name="markDirty"/> is false for a blank New document:
        // it has nothing unsaved in it yet, so closing it must not prompt.
        private void FinalizeUnsavedTab(string tempPath, string displayName, string status, bool markDirty = true)
        {
            if (_doc is null || !string.Equals(_currentFile, tempPath, StringComparison.OrdinalIgnoreCase))
                return;   // open failed and reverted to a different tab — don't clobber it
            SetDisplayName(displayName);
            _ctx.IsUntitled = true;     // no real on-disk home yet → Ctrl+S routes to Save As
            _ctx.OriginalPath = null;   // the working file is a TDPdf-created temp, never a save target
            if (markDirty) MarkDirty(true);
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

        // True when path sits inside any of dirs. Compares full paths with a trailing separator so a
        // sibling folder whose name merely starts with the same text cannot match.
        private static bool IsUnderAnyDirectory(string path, List<string> dirs)
        {
            try
            {
                string full = System.IO.Path.GetFullPath(path);
                foreach (var d in dirs)
                {
                    if (string.IsNullOrWhiteSpace(d)) continue;
                    string dir = System.IO.Path.GetFullPath(d);
                    if (!dir.EndsWith(System.IO.Path.DirectorySeparatorChar))
                        dir += System.IO.Path.DirectorySeparatorChar;
                    if (full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { /* malformed path — treat as not extracted */ }
            return false;
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
            // #146: the privacy toggle stops the list growing at the single write path. Reads are
            // left alone deliberately — turning the toggle on empties the store, so there is
            // nothing left to read, and turning it back off starts an empty list rather than
            // resurrecting the old one.
            if (TDPdf.Properties.Settings.Default.DontRememberRecentFiles) return;
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
                    var item = MakeMenuItem(System.IO.Path.GetFileName(path), async (_, _2) => await OpenRecentAsync(path),
                                            null, null, "\uE8A5");
                    item.ToolTip = path;
                    menu.Items.Add(item);
                }
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem("Clear List", (_, _2) => ClearRecentFiles(), null, null, "\uE74D"));
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
                // #135 (upstream KillerPDF #233): drag every selected page as one ordered block,
                // not just the one under the cursor. The list has been SelectionMode="Extended"
                // all along, so people could already select a run of pages and then discovered
                // that dragging moved exactly one of them.
                int[] block = PageList.SelectedItems.Count > 0
                    ? PageList.SelectedItems.Cast<object>()
                        .Select(o => PageList.Items.IndexOf(o))
                        .Where(i => i >= 0)
                        .OrderBy(i => i)
                        .ToArray()
                    : Array.Empty<int>();
                if (block.Length > 0)
                {
                    try { DragDrop.DoDragDrop(PageList, block, DragDropEffects.Move); }
                    finally { HidePageDropLine(); }   // the drag can end anywhere, including nowhere
                }
            }
        }

        private void PageList_DragOver(object sender, DragEventArgs e)
        {
            // #172 (upstream KillerPDF): the sidebar takes a FileDrop of PDFs/images as well as its own
            // page-reorder payload. The internal payload keeps first refusal so a page drag is never
            // mistaken for a file drop.
            if (e.Data.GetDataPresent(typeof(int[])))
            {
                e.Effects = DragDropEffects.Move;
                ShowPageDropLine(DropTargetIndex(e.GetPosition(PageList)));
            }
            else if (_doc is not null && DroppedOpenablePaths(e).Length > 0)
            {
                e.Effects = DragDropEffects.Copy;
                HidePageDropLine();   // a file drop appends; there is no insertion point to promise
            }
            else
            {
                e.Effects = DragDropEffects.None;
                HidePageDropLine();
            }
            e.Handled = true;
        }

        private void PageList_DragLeave(object sender, DragEventArgs e) => HidePageDropLine();

        /// <summary>
        /// The index a drop at <paramref name="pos"/> would insert BEFORE — i.e. the first page
        /// whose midpoint is below the cursor, or one past the end.
        /// </summary>
        /// <remarks>
        /// #135: one helper so the line drawn during the drag and the reorder performed on the drop
        /// can never disagree. They were separate arithmetic before, which is the standard way a
        /// drop indicator ends up pointing somewhere the page does not go.
        /// </remarks>
        private int DropTargetIndex(Point pos)
        {
            for (int i = 0; i < PageList.Items.Count; i++)
            {
                if (PageList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item)
                {
                    var mid = item.TranslatePoint(new Point(0, item.ActualHeight / 2), PageList);
                    if (pos.Y < mid.Y) return i;
                }
            }
            return PageList.Items.Count;
        }

        private void ShowPageDropLine(int beforeIndex)
        {
            if (PageList.Items.Count == 0) { HidePageDropLine(); return; }

            double y;
            if (beforeIndex < PageList.Items.Count
                && PageList.ItemContainerGenerator.ContainerFromIndex(beforeIndex) is ListBoxItem at)
            {
                y = at.TranslatePoint(new Point(0, 0), _sidebarListHost).Y;
            }
            else if (PageList.ItemContainerGenerator.ContainerFromIndex(PageList.Items.Count - 1)
                     is ListBoxItem last)
            {
                y = last.TranslatePoint(new Point(0, last.ActualHeight), _sidebarListHost).Y;
            }
            else { HidePageDropLine(); return; }

            // Virtualisation can put a container off-screen; a line drawn outside the host is
            // meaningless, so say nothing rather than something wrong.
            if (!IsFinite(y) || y < 0 || y > _sidebarListHost.ActualHeight) { HidePageDropLine(); return; }

            _pageDropLine.Margin = new Thickness(6, y - 1, 6, 0);
            _pageDropLine.Visibility = Visibility.Visible;
        }

        private void HidePageDropLine()
        {
            if (_pageDropLine is not null) _pageDropLine.Visibility = Visibility.Collapsed;
        }

        /// <summary>The dropped FileDrop paths this sidebar can append, using the same PDF/image
        /// classification as the start-screen drop zone (<see cref="IsOpenablePath"/>). Folders and .zip
        /// archives are deliberately NOT expanded here — that is the start screen's open flow, which asks
        /// merge-vs-separate; a drop onto an open document's page list only ever appends.</summary>
        private static string[] DroppedOpenablePaths(DragEventArgs e)
            => e.Data.GetDataPresent(DataFormats.FileDrop)
               && e.Data.GetData(DataFormats.FileDrop) is string[] paths
                ? paths.Where(IsOpenablePath).ToArray()
                : Array.Empty<string>();

        /// <summary>
        /// #172: appends the dropped files' pages to the open document. Upstream appends rather than
        /// inserting at the drop point on purpose — appending leaves every existing page index untouched,
        /// so annotations, rotations and the undo stack need no remapping. Reuses the two importers we
        /// already have: <see cref="AppendPdfFileToDoc"/> (File ▸ Merge, including named-destination link
        /// rewriting) and <see cref="AddImagePagesFromFile"/> (one page per image frame).
        /// </summary>
        private async void AppendFilesToCurrentDoc(string[] files)
        {
            if (_doc is null) return;
            CommitActiveTextBox();
            var doc = _doc;
            int before = doc.PageCount;
            foreach (var f in files)
            {
                // A single bad file skips rather than aborting the drop: a folder-full drag routinely
                // carries one encrypted or truncated file and the rest are still worth appending.
                try
                {
                    if (IsPdfPath(f)) AppendPdfFileToDoc(doc, f);
                    else              AddImagePagesFromFile(doc, f);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Sidebar drop skipped {f}: {ex}");
                    // #203 (upstream v1.7.4): a damaged PDF used to be swallowed here — nothing was
                    // appended and nothing was said, so the file simply vanished from the drop.
                    // Offer the same repair the open path offers.
                    if (IsPdfPath(f)) await TryAppendRepairedPdfAsync(doc, f);
                }
                if (_doc is null || !ReferenceEquals(_doc, doc)) return;   // document swapped mid-await
            }
            if (doc.PageCount == before)
            {
                SetStatus("Nothing could be appended — the dropped files could not be read.");
                return;
            }
            int added = doc.PageCount - before;
            // Same persist-and-repaint path as the page reorder below; it marks the document dirty.
            SaveTempAndReload();
            SetStatus($"Appended {added} page{(added == 1 ? "" : "s")} from {files.Length} file{(files.Length == 1 ? "" : "s")}");
        }

        /// <summary>
        /// #203: offers to repair a dropped PDF that PdfSharpCore could not read, and appends the
        /// repaired copy on success. The original file is never written to.
        /// </summary>
        /// <remarks>
        /// Only the lossless strategy is offered here, deliberately narrower than upstream's three.
        /// <see cref="TDPdf.Services.PdfiumInterop.TryPdfiumRepair"/> re-saves through PDFium and
        /// keeps text, forms and bookmarks. The open path's raster recovery is NOT chained on:
        /// opening a rasterized document tells the user the whole file was recovered from pixels,
        /// whereas appending one would bury a run of flattened, unsearchable pages in the middle of
        /// a live document with nothing to distinguish them. If PDFium cannot read it either, say so
        /// and leave the document alone.
        /// </remarks>
        private async Task TryAppendRepairedPdfAsync(PdfDocument doc, string path)
        {
            string name = System.IO.Path.GetFileName(path);
            var ask = TdpDialog.Show(this,
                $"\"{name}\" has a damaged structure and could not be appended.\n\n" +
                "Attempt a repair? A repaired copy is used — the original file is not changed.\n\n" +
                "A repaired file may be missing bookmarks, forms and other interactive features.",
                "TDPdf", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ask != MessageBoxResult.Yes) return;

            string fixedPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"tdpdf_fixed_{Guid.NewGuid():N}.pdf");
            SetBusy(true, $"Repairing {name}...");
            try
            {
                bool repaired = await Task.Run(
                    () => TDPdf.Services.PdfiumInterop.TryPdfiumRepair(path, fixedPath));
                if (repaired)
                {
                    try
                    {
                        AppendPdfFileToDoc(doc, fixedPath);
                        return;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Repaired append failed {path}: {ex}");
                    }
                }
                TdpDialog.Show(this, $"\"{name}\" could not be repaired.", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
                try { if (File.Exists(fixedPath)) File.Delete(fixedPath); } catch { /* temp file */ }
            }
        }

        private void PageList_Drop(object sender, DragEventArgs e)
        {
            HidePageDropLine();
            // #172: a FileDrop appends; anything carrying the internal page payload falls through to
            // the reorder below, unchanged.
            if (_doc is not null && !e.Data.GetDataPresent(typeof(int[])))
            {
                var files = DroppedOpenablePaths(e);
                if (files.Length > 0) { AppendFilesToCurrentDoc(files); e.Handled = true; }
                return;
            }
            if (_doc is null || e.Data.GetData(typeof(int[])) is not int[] block || block.Length == 0)
                return;

            var doc = _doc;
            int[] from = block.Where(i => i >= 0 && i < doc.PageCount).Distinct().OrderBy(i => i).ToArray();
            if (from.Length == 0) return;

            int target = DropTargetIndex(e.GetPosition(PageList));

            // #135: dropping a block back onto itself is not a move. Without this a drag that ends
            // where it began still rewrites and reloads the document, which costs the user their
            // unsaved annotations (SaveTempAndReload clears them for every structural edit) in
            // exchange for nothing at all.
            int insertAt = target - from.Count(i => i < target);
            bool contiguous = from[^1] - from[0] == from.Length - 1;
            if (contiguous && insertAt == from[0]) return;

            // Lift the pages in document order, then remove from the end so the earlier indices
            // stay valid while we do it. Each PdfPage carries its own /Rotate, so a page's rotation
            // travels with it and needs no separate bookkeeping.
            var moving = from.Select(i => doc.Pages[i]).ToList();
            foreach (int i in from.OrderByDescending(i => i)) doc.Pages.RemoveAt(i);

            insertAt = Math.Clamp(insertAt, 0, doc.PageCount);
            for (int k = 0; k < moving.Count; k++) doc.Pages.Insert(insertAt + k, moving[k]);

            SaveTempAndReload();

            // Leave the block selected where it landed, so a second drag continues from where the
            // eye already is rather than from wherever the list decided to put the selection.
            PageList.SelectedItems.Clear();
            for (int k = 0; k < moving.Count && insertAt + k < PageList.Items.Count; k++)
                PageList.SelectedItems.Add(PageList.Items[insertAt + k]);
            if (PageList.SelectedItems.Count > 0) PageList.SelectedIndex = insertAt;
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
                    // #197: Continuous never re-renders a primary tile, and the scroll it is about
                    // to run is suppressed, so this is the only place a page change can raise the
                    // badge here.
                    ShowPageBadge(PageList.SelectedIndex);
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
                if (idx != PageList.SelectedIndex) RecordNavJump();   // jump-box hop — retraceable via Alt+Left
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
            if (ShortcutOverlay.Visibility == Visibility.Visible) ApplyPersistedShortcutView();
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

        /// <summary>
        /// The shared TDPdf dialog shell: a borderless, transparent-background window (no OS title
        /// bar) holding a rounded panel with an accent border and a draggable Consolas wordmark
        /// title bar. Returns the window plus the vertical stack each dialog fills with its body.
        /// </summary>
        private static (Window Window, StackPanel Body) CreateShell(Window? owner, string title)
        {
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
                Background      = Brush("BgDark"),
                BorderBrush     = Brush("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6)
            };

            var root = new StackPanel();

            var titleBar = new Border
            {
                Background   = Brush("BgPanel"),
                Padding      = new Thickness(16, 10, 16, 10),
                CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            titleBar.Child = new TextBlock
            {
                Text       = title,
                Foreground = Brush("AccentGreen"),
                FontWeight = FontWeights.SemiBold,
                FontSize   = 13,
                FontFamily = new System.Windows.Media.FontFamily("Consolas")
            };
            root.Children.Add(titleBar);

            outerBorder.Child = root;
            win.Content = outerBorder;
            return (win, root);
        }

        // Flat, themed button chrome. Replaces the stock WPF template so no default blue Aero
        // hover/focus chrome bleeds through onto a dark dialog.
        private static ControlTemplate MakeBtnTemplate()
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

        // Themed PasswordBox chrome: our panel fill and dim border instead of the OS white box
        // with its blue focus ring. PART_ContentHost is the contract name WPF looks for.
        private static ControlTemplate MakePasswordFieldTemplate()
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
            bf.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            host.SetValue(Control.PaddingProperty, new Thickness(0));
            host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            bf.AppendChild(host);
            return new ControlTemplate(typeof(PasswordBox)) { VisualTree = bf };
        }

        /// <summary>
        /// Themed "Password Required" prompt: the family dialog chrome around a themed PasswordBox.
        /// Returns the entered password, or <c>null</c> if the user cancelled or closed the dialog.
        /// </summary>
        public static string? PromptPassword(Window? owner, string filename)
        {
            string? result = null;
            var text  = Brush("TextPrimary");
            var green = Brush("AccentGreen");

            var (win, root) = CreateShell(owner, "TDPdf");

            var message = new TextBlock
            {
                Foreground   = text,
                FontSize     = 13,
                TextWrapping = TextWrapping.Wrap
            };
            message.Inlines.Add(new System.Windows.Documents.Run(
                $"“{System.IO.Path.GetFileName(filename)}” ") { FontWeight = FontWeights.SemiBold });
            message.Inlines.Add(new System.Windows.Documents.Run("is password protected."));
            root.Children.Add(new Border { Padding = new Thickness(20, 16, 20, 10), Child = message });

            var pwBox = new PasswordBox
            {
                FontSize        = 12,
                Background      = Brush("PanelBackground"),
                Foreground      = text,
                BorderBrush     = Brush("BorderDim"),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(6, 5, 6, 5),
                CaretBrush      = text,
                Template        = MakePasswordFieldTemplate()
            };
            AutomationProperties.SetName(pwBox, "Password");
            AutomationProperties.SetHelpText(pwBox, "Password for the protected PDF");
            root.Children.Add(new Border { Padding = new Thickness(20, 0, 20, 4), Child = pwBox });

            var btnPanel = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            Button MakeBtn(string label, bool accent)
            {
                var bgNorm = accent ? Brush("AccentGreenDim") : Brush("BgPanel");
                var bgHov  = accent ? Brush("BgPressed") : Brush("BgHover");
                var btn = new Button
                {
                    Content         = label,
                    Padding         = new Thickness(18, 6, 18, 6),
                    Margin          = new Thickness(8, 0, 0, 0),
                    Background      = bgNorm,
                    Foreground      = accent ? green : text,
                    BorderBrush     = accent ? green : Brush("BorderDim"),
                    BorderThickness = new Thickness(1),
                    Cursor          = Cursors.Hand,
                    FontSize        = 12,
                    Template        = MakeBtnTemplate()
                };
                btn.MouseEnter += (_, _2) => btn.Background = bgHov;
                btn.MouseLeave += (_, _2) => btn.Background = bgNorm;
                return btn;
            }

            var openBtn = MakeBtn("Open", accent: true);
            openBtn.IsDefault = true;
            openBtn.Click += (_, _2) => { result = pwBox.Password; win.Close(); };
            var cancelBtn = MakeBtn("Cancel", accent: false);
            cancelBtn.IsCancel = true;   // Esc closes the prompt, leaving result null
            cancelBtn.Click += (_, _2) => { result = null; win.Close(); };
            btnPanel.Children.Add(openBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(new Border { Padding = new Thickness(16, 12, 16, 16), Child = btnPanel });

            // Enter submits from inside the field as well (IsDefault covers the rest of the dialog).
            pwBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { result = pwBox.Password; win.Close(); } };

            win.Loaded += (_, _2) => pwBox.Focus();
            win.ShowDialog();
            return result;
        }

        public static MessageBoxResult Show(
            Window? owner,
            string message,
            string title = "TDPdf",
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage image = MessageBoxImage.None)
            => ShowCore(owner, message, title, buttons, image, null, null, null).result;

        public static MessageBoxResult ShowYesNo(
            Window? owner,
            string message,
            string yesLabel,
            string noLabel,
            string title = "TDPdf",
            MessageBoxImage image = MessageBoxImage.None)
            => ShowCore(
                owner,
                message,
                title,
                MessageBoxButton.YesNo,
                image,
                null,
                yesLabel,
                noLabel).result;

        // Same themed dialog as Show, plus a single opt-out checkbox below the message (e.g. "Don't ask
        // again"). Returns the button result together with whether the checkbox was ticked.
        public static (MessageBoxResult result, bool ticked) ShowWithCheckbox(
            Window? owner,
            string message,
            string checkboxLabel,
            string title = "TDPdf",
            MessageBoxButton buttons = MessageBoxButton.OKCancel,
            MessageBoxImage image = MessageBoxImage.None)
            => ShowCore(owner, message, title, buttons, image, checkboxLabel, null, null);

        private static (MessageBoxResult result, bool ticked) ShowCore(
            Window? owner,
            string message,
            string title,
            MessageBoxButton buttons,
            MessageBoxImage image,
            string? checkboxLabel,
            string? yesLabel,
            string? noLabel)
        {
            var result = MessageBoxResult.OK;
            bool ticked = false;
            var green = Brush("AccentGreen");
            var panel = Brush("BgPanel");
            var text = Brush("TextPrimary");
            var border = Brush("BorderDim");
            var greenDim = Brush("AccentGreenDim");
            var greenHov = Brush("BgPressed");
            var hover = Brush("BgHover");
            var danger = Brush("DangerRed");
            var warning = Brush("WarningOrange");

            var (win, root) = CreateShell(owner, title);

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

            // Optional opt-out checkbox, aligned under the message text (past the icon column).
            if (checkboxLabel != null)
            {
                var check = new CheckBox
                {
                    Content    = checkboxLabel,
                    Foreground = text,
                    FontSize   = 12,
                    Margin     = new Thickness(20, 4, 20, 4)
                };
                check.Checked   += (_, _2) => ticked = true;
                check.Unchecked += (_, _2) => ticked = false;
                root.Children.Add(check);
            }

            var btnPanel = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

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
                    defaultBtn = MakeBtn(yesLabel ?? "Yes", MessageBoxResult.Yes, accent: true, isDefault: true);
                    btnPanel.Children.Add(defaultBtn);
                    btnPanel.Children.Add(MakeBtn(noLabel ?? "No", MessageBoxResult.No, isCancel: true));
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

            if (defaultBtn != null)
            {
                var toFocus = defaultBtn;
                win.Loaded += (_, _2) => toFocus.Focus();
            }
            win.ShowDialog();
            return (result, ticked);
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
