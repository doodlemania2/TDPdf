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
        // #135: the page whose selection collapse was deferred from mouse-down to mouse-up so a
        // multi-page drag can start. -1 when nothing is deferred. See
        // PageList_PreviewMouseLeftButtonDown.
        private int _pageClickCollapseIndex = -1;

        // Editing
        private EditTool _currentTool = EditTool.Select;
        private Dictionary<int, List<PageAnnotation>> _annotations => _ctx.Annotations;
        private Dictionary<int, List<Rect>> _redactionMarks => _ctx.RedactionMarks;
        private Dictionary<int, (int w, int h)> _renderDims => _ctx.RenderDims;
        private Dictionary<(int pageIndex, int dpiX), RenderedPage> _renderCache => _ctx.RenderCache;

        // Interactive form-field state — forwarded from the active DocumentContext so it
        // survives tab switches (see the PDF Form Field Overlays region).
        private Dictionary<int, string>    _formTextValues  => _ctx.FormTextValues;
        private Dictionary<int, string>    _formCheckValues => _ctx.FormCheckValues;
        private Dictionary<string, string> _formRadioValues => _ctx.FormRadioValues;
        private const string FormOverlayTag = "FormFieldOverlay";
        private double _currentDpiScale = 1.0;

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
        // #135 item 2: letter spacing, in canvas px, inherited by the next text box placed. Unlike
        // bold/italic/underline this is NOT read back off the editor on commit, because a WPF
        // TextBox cannot render letter spacing and therefore never carries it — the tool state IS
        // the value while a box is open. See PlaceTextBox and CommitActiveTextBox.
        private double _textLetterSpacing;
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
        private readonly Button _toolCropBtn = null!;

        // Form field tool
        private readonly Button _toolFormBtn = null!;
        private Border? _formSettingsBar;
        private FormFieldMode _formMode = FormFieldMode.Text;

        /// <summary>The field the Form tool is acting on, or null.</summary>
        /// <remarks>
        /// A live reference into _doc, so it is dropped whenever the tool changes (which is also
        /// what a tab switch does, via ActivateContext) and whenever the document is reopened. A
        /// stale one would point at a dictionary belonging to a document that is no longer open.
        /// </remarks>
        private PdfDictionary? _selectedFormWidget;

        // Redaction tool
        //
        // The pending marks are deliberately NOT PageAnnotations. An annotation is something the
        // save path bakes into the PDF, and a redaction is the opposite of that: nothing is drawn,
        // content is destroyed, and it happens through PDFium rather than PdfSharpCore. Putting a
        // mark in _annotations would make every save write a file with the mark silently dropped
        // and nothing redacted — which the user has every reason to read as "it worked".
        private readonly Button _toolRedactBtn = null!;
        private Border? _redactSettingsBar;

        /// <summary>Whether Apply over-deletes objects that only straddle a mark. See
        /// <see cref="TDPdf.Services.PdfRedaction.Request.RemovePartialOverlaps"/>.</summary>
        private bool _redactRemovePartial = true;
        private bool _redactScrubMetadata = true;

        // ── Measure tool ───────────────────────────────────────────────────────────────────────
        //
        // A ruler, and nothing else. It is the only tool in this file that mutates NOTHING: no
        // PageAnnotation subclass, nothing in _annotations, no MarkDirty(), no _isDirty. Measuring
        // a margin is a question about the document, not an edit to it, and a tool that quietly
        // armed the "unsaved changes" prompt for asking would be a bug in its own right.
        //
        // That is also why the state lives here as plain window fields rather than in
        // DocumentContext: there is nothing to preserve across a tab switch. ActivateContext calls
        // SetTool(Select), which clears the measurement, and a ruler the user has to re-drag after
        // switching documents is exactly what a ruler should do.
        private readonly Button _toolMeasureBtn = null!;

        /// <summary>True only between mouse-down and mouse-up on the Measure tool.</summary>
        private bool _isMeasuring;

        /// <summary>True once a measurement exists on screen, drag finished or not.</summary>
        private bool _hasMeasurement;

        /// <summary>The page the measurement was drawn on; it is never shown on any other.</summary>
        private int _measurePage = -1;

        // Both ends in ANNOTATION-CANVAS coordinates — the same space every annotation and every
        // redaction mark is stored in. Zoom is an ancestor LayoutTransform on PageContentGrid, so
        // these are already zoom-independent; they are converted to PDF points only at the moment
        // the caption is built. See MeasureGeometry.
        private Point _measureA;
        private Point _measureB;

        /// <summary>The ruler's visuals, so a re-render can put back exactly what it wiped.</summary>
        private readonly List<UIElement> _measureVisuals = new();

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

        // ── Per-tab view resume (#399) ─────────────────────────────────────────────────────────
        // True only while a tab switch is putting a tab's own zoom back. A restored zoom is a
        // RESUME, not a preference: the user already made that decision on that tab, and it was
        // already persisted at the moment they made it. SaveZoomSetting consults this so
        // re-entering a tab can never rewrite Settings.LastManualZoom / Settings.DefaultFitMode —
        // otherwise merely clicking back to an older tab that happens to hold a manual zoom would
        // silently retire a Fit Page the user had just chosen on the tab they came from.
        private bool _restoringTabZoom;
        // Work owed to a tab that is being re-activated, held until the thing it depends on
        // exists. A FIT has to be measured against THIS tab's page, and a scroll offset is
        // silently clamped to zero unless the new content is already laid out, so both wait for
        // the render rather than being applied against the outgoing tab's geometry. See
        // ApplyPendingViewResume.
        private DocumentContext? _resumeZoomFor;
        private (DocumentContext Ctx, double H, double V)? _resumeScroll;

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
        private Button? _searchRedactBtn;

        // Signatures
        private List<SavedSignature> _savedSignatures = [];
        private SavedSignature? _pendingSignature;
        private Border? _signaturePopup;
        private Border? _cropPopup;
        private CheckBox? _cropApplyAllCheck;
        private Button? _removeCropBtn;
        // Signatures are stored under %LocalAppData%\TDPdf\ so the file stays
        // writable when the EXE is installed machine-wide under %ProgramFiles%
        // (no admin rights on that directory). A legacy file next to the EXE
        // from older builds is migrated on first read.
        private static readonly string SignatureDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TDPdf");
        private static readonly string SignatureFile = System.IO.Path.Combine(SignatureDir, "signatures.json");
        private static readonly string LegacySignatureFile = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "signatures.json");

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
        // Electronic Signature submenu (MainWindow.ESignature.cs); visibility and enablement are
        // set in FileMenu_SubmenuOpened.
        private MenuItem _eSignatureMenuItem = null!;
        private MenuItem _eSignSendMenuItem = null!;
        private MenuItem _eSignSelfMenuItem = null!;
        private MenuItem _eSignRequestsMenuItem = null!;
        private MenuItem _eSignVerifyMenuItem = null!;
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
        private Button _installBtn = null!;
        private TextBox _pageJumpBox = null!;
        private TextBlock _pageTotalLabel = null!;
        private Border _customTitleBar = null!;
        private RowDefinition _titleBarRow = null!;
        private Button _maximizeBtn = null!;
        private StackPanel _footerRightGroup = null!;
        private TextBlock _footerCopyright = null!;

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
        private Button _pageSizeButton = null!;   // footer page-size chip (see UpdatePageSizeReadout)
        private Button? _eSignSignedBadge;       // footer "Digitally signed" indicator (MainWindow.ESignatureRequests.cs)

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
            _toolRedactBtn = (Button)FindName("ToolRedactBtn")!;
            _toolFormBtn = (Button)FindName("ToolFormBtn")!;
            _toolMeasureBtn = (Button)FindName("ToolMeasureBtn")!;
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
            _eSignatureMenuItem = (MenuItem)FindName("ESignatureMenuItem")!;
            _eSignSendMenuItem = (MenuItem)FindName("ESignSendMenuItem")!;
            _eSignSelfMenuItem = (MenuItem)FindName("ESignSelfMenuItem")!;
            _eSignRequestsMenuItem = (MenuItem)FindName("ESignRequestsMenuItem")!;
            _eSignVerifyMenuItem = (MenuItem)FindName("ESignVerifyMenuItem")!;
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
            _installBtn = (Button)FindName("InstallBtn")!;
            _pageJumpBox = (TextBox)FindName("PageJumpBox")!;
            _pageTotalLabel = (TextBlock)FindName("PageTotalLabel")!;
            _customTitleBar = (Border)FindName("CustomTitleBar")!;
            _titleBarRow = (RowDefinition)FindName("TitleBarRow")!;
            _maximizeBtn = (Button)FindName("MaximizeBtn")!;
            _footerRightGroup = (StackPanel)FindName("FooterRightGroup")!;
            _footerCopyright = (TextBlock)FindName("FooterCopyright")!;
            StateChanged += (_, _) => UpdateMaximizeButton();
            UpdateMaximizeButton();
            _customTitleBar.MouseRightButtonUp += TitleBar_MouseRightButtonUp;
            ShortcutOverlay.IsVisibleChanged += ShortcutOverlay_IsVisibleChanged;
            TDPdf.Controls.RadioGroupKeyboard.Apply((Grid)FindName("SidebarTabRow")!);
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
            _statusBarBorder.SizeChanged += (_, _) => FitFooterToWidth();
            _footerRightGroup.SizeChanged += (_, _) => FitFooterToWidth();
            _sidebarOuterGrid = (Grid)FindName("SidebarOuterGrid")!;
            _appScaleButton = (Button)FindName("AppScaleButton")!;
            _pageSizeButton = (Button)FindName("PageSizeButton")!;
            _eSignSignedBadge = (Button)FindName("ESignSignedBadge")!;
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
            InitPageSizeReadout();   // upstream v1.8.5: restore the footer page-size chip's unit
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
                // Only the first TDPdf window owns (and restores) the saved session; see SaveSession.
                bool ownsSession = RegisterSessionWindow() && !isTearOffLaunch;
                if (ownsSession) ClaimSavedSession();
                if (args.Length > fileArgIndex && System.IO.File.Exists(args[fileArgIndex]))
                    await OpenInTabAsync(args[fileArgIndex]);   // an explicitly-requested file wins over the saved session
                else if (ownsSession)
                    await RestoreSessionAsync();     // otherwise reopen last session's tabs when enabled

                if (App.IsPortable())
                {
                    RefreshPortableBadge();
                    _portableBadge.Visibility = Visibility.Visible;
                }

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
                    SetStatusHeld("An update has been installed. Restart TDPdf to use it.", 10000);
                    Telemetry.TrackEvent("Update.RestartPending");
                }
            };
        }

        /// <summary>
        /// The one typeface every text-annotation measurement goes through. #135: bold and italic
        /// change glyph advances, so if the on-screen TextBlock, <see cref="MeasureTextAnnotation"/>,
        /// <see cref="WrapTextToWidth"/> and the PDF burn-in do not all ask the same question, a
        /// styled box wraps in one place and not the other and the text moves when you save.
        ///
        /// #135 item 2 extends that same chain to letter spacing rather than adding spacing to the
        /// burn-in alone — see <see cref="TextLayoutTypeface"/> and
        /// <see cref="TDPdf.Services.TextLetterSpacing"/>, and note that the on-screen half becomes
        /// <c>SpacedTextVisual</c> rather than a TextBlock once spacing is non-zero, because WPF
        /// has no letter-spacing primitive for a TextBlock to use.
        /// </summary>
        private static Typeface TextTypeface(bool bold, bool italic) =>
            TextTypeface(PdfFontStyle.DefaultFamily, bold, italic);

        /// <summary>
        /// The same typeface, but in a named family rather than the pinned default.
        /// </summary>
        /// <remarks>
        /// Only <see cref="TextLayoutTypeface"/> reaches for this, and only for a letter-spaced
        /// annotation. Everything else keeps going through the two-argument overload above, which
        /// pins <see cref="PdfFontStyle.DefaultFamily"/> exactly as it always has — changing the
        /// family a measurement is taken in changes every wrap point, so an existing annotation in
        /// Arial or Courier must not start being measured differently because this overload now
        /// exists.
        /// </remarks>
        private static Typeface TextTypeface(string family, bool bold, bool italic) =>
            new(new FontFamily(string.IsNullOrWhiteSpace(family) ? PdfFontStyle.DefaultFamily : family),
                italic ? FontStyles.Italic : FontStyles.Normal,
                bold ? FontWeights.Bold : FontWeights.Normal,
                FontStretches.Normal);

        /// <summary>
        /// The typeface a text annotation's LAYOUT — its wrap points, its measured box and, when
        /// spaced, its on-screen glyphs — is computed in. #135 item 2.
        /// </summary>
        /// <remarks>
        /// Two models, and which one an annotation is in is decided solely by its
        /// <see cref="TextAnnotation.LetterSpacing"/>:
        ///
        ///   * <b>Unspaced</b> keeps pinning <see cref="PdfFontStyle.DefaultFamily"/>. That has
        ///     always been a slight fiction — the on-screen TextBlock renders in the annotation's
        ///     OWN family (see <c>TextEditHitBounds</c>, which says so) — but it is the fiction
        ///     every annotation in every saved document was wrapped and measured under, so
        ///     correcting it here would silently reflow them all. It stays.
        ///   * <b>Spaced</b> uses the annotation's own family, because in this model the same
        ///     advances that size the box also position each glyph on screen. Measuring in one
        ///     family and drawing in another would put visible ink where the box says there is
        ///     none. It is also what the burn-in already does (<c>FontCoverage.PickFamily</c>
        ///     starts from <c>ta.FontName</c>), so the spaced path is the more honest of the two.
        ///
        /// One residual gap, inherited rather than introduced: for text <c>ta.FontName</c> cannot
        /// cover — CJK, Arabic — <c>PickFamily</c> silently upgrades the burn to a family that can,
        /// while this measures in the chosen one. The advances then differ, so a spaced CJK
        /// annotation can burn a little wider or narrower than it previews. That is exactly as true
        /// of the unspaced path's wrapping today, it needs PickFamily to be reachable from a
        /// measurement rather than only from the burn, and it is not this change's to close.
        /// </remarks>
        private static Typeface TextLayoutTypeface(TextAnnotation ta) =>
            TextLetterSpacing.IsNone(ta.LetterSpacing)
                ? TextTypeface(ta.Bold, ta.Italic)
                : TextTypeface(ta.FontName, ta.Bold, ta.Italic);

        /// <summary>
        /// The two measuring functions <see cref="TextLetterSpacing.Width"/> needs, bound to one
        /// WPF typeface and size.
        /// </summary>
        /// <remarks>
        /// They differ, and the difference is the whole reason there are two. A whole string is
        /// measured with <c>FormattedText.Width</c>, which is what every pre-spacing measurement in
        /// this file has always used — keeping it is what makes spacing == 0 byte-identical. A
        /// single character is measured with <c>WidthIncludingTrailingWhitespace</c>, because
        /// <c>Width</c> drops trailing whitespace: a lone space would measure ZERO, and every space
        /// in a spaced line would collapse to nothing but the spacing gap itself.
        /// </remarks>
        private static (Func<string, double> Whole, Func<string, double> Cluster) TextMeasurers(
            Typeface typeface, double fontSize, double dpi)
        {
            FormattedText Ft(string s) => new(
                string.IsNullOrEmpty(s) ? " " : s,
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, fontSize, Brushes.Black, dpi);
            return (s => Ft(s).Width, s => Ft(s).WidthIncludingTrailingWhitespace);
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
            FitToStartupMonitor();
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);
            InitializeTaskbarIcons(hwnd);
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

        // The XAML size (1000 x 700) is taller than the work area of a 1920 x 1080 screen at 150%
        // (about 690 DIPs once the taskbar is out), so the footer opened off the bottom edge.
        // Before the window is first shown, shrink it to the work area of the monitor WPF will
        // center it on (the one under the pointer) and re-center it there. Runs before WPF applies
        // CenterScreen, so WPF's own centering sees the new size too. Idea from upstream KillerPDF
        // 1.8.92 (WorkAreaFit); best-effort, a window size is never worth failing startup over.
        private void FitToStartupMonitor()
        {
            if (WindowState != WindowState.Normal) return;
            try
            {
                double sx = _currentDpiScale > 0 ? _currentDpiScale : 1.0;
                if (!GetCursorPos(out POINT cursor)) return;
                IntPtr monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
                if (monitor == IntPtr.Zero) return;
                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (!GetMonitorInfo(monitor, ref info)) return;
                RECT work = info.rcWork;

                // Window coordinates follow the window's own DPI; the work area's SIZE follows the
                // target monitor's, which differs only when that monitor is scaled differently.
                double toDip = 1 / sx;
                try
                {
                    if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                        toDip = 96.0 / dpiX;
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }

                double areaW = (work.right - work.left) * toDip;
                double areaH = (work.bottom - work.top) * toDip;
                if (areaW <= 0 || areaH <= 0) return;

                // A work area smaller than the minimum lowers the minimum, or WPF would coerce
                // the size straight back past the screen edge.
                if (MinWidth > areaW) MinWidth = Math.Floor(areaW);
                if (MinHeight > areaH) MinHeight = Math.Floor(areaH);

                double w = double.IsNaN(Width) ? ActualWidth : Width;
                double h = double.IsNaN(Height) ? ActualHeight : Height;
                if (w <= 0 || h <= 0) return;
                var fit = WindowFitMath.FitCentered(w, h, work.left / sx, work.top / sx, areaW, areaH,
                                                    MinWidth, MinHeight);
                if (Math.Abs(fit.Width - w) <= 0.5 && Math.Abs(fit.Height - h) <= 0.5) return;
                Width = fit.Width;
                Height = fit.Height;
                Left = fit.Left;
                Top = fit.Top;
            }
            catch { /* best-effort */ }
        }

        private const int MDT_EFFECTIVE_DPI = 0;

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

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
        private const int WM_ENTERSIZEMOVE = 0x0231;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int WM_SETICON = 0x0080;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;
        // The apphost (single-file bundle included) carries <ApplicationIcon> as its first icon
        // group, which the SDK writes at resource id 32512 — the IDI_APPLICATION slot.
        private const int APPLICATION_ICON_RESOURCE_ID = 32512;
        private const uint IMAGE_ICON = 1;
        private const uint LR_SHARED = 0x00008000;

        // True between WM_ENTERSIZEMOVE and WM_EXITSIZEMOVE, i.e. while the user is dragging or
        // resizing the window with the modal move/size loop. WmGetMinMaxInfo reads it.
        private bool _inWindowSizeMove;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_ENTERSIZEMOVE)
            {
                _inWindowSizeMove = true; // observe only; WPF still needs to see the message
            }
            else if (msg == WM_EXITSIZEMOVE)
            {
                _inWindowSizeMove = false;
            }
            else if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_KEYMENU && lParam.ToInt64() == ' '
                     && !_useNativeWindowFrame && !_fullScreen)
            {
                // Alt+Space that reached Windows (OnPreviewKeyDown normally takes it first): the
                // frameless window has no system menu of its own to open, so show ours.
                ShowWindowSystemMenuFromKeyboard();
                handled = true;
            }
            else if (msg == WM_GETMINMAXINFO && !_useNativeWindowFrame)
            {
                WmGetMinMaxInfo(hwnd, lParam, _inWindowSizeMove, MinWidth, MinHeight, WindowDpiScale(hwnd));
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

        // Windows 11 can create the taskbar button before WPF has published the XAML Icon= to the
        // HWND, and our startup is slow enough (single-file self-extract, splash, first render) for
        // that race to be lost — the button then keeps the generic placeholder icon. Hand the shell
        // the exe's own icon resource directly via WM_SETICON as soon as the HWND exists, and again
        // once startup work has gone idle in case Explorer created the button in between.
        // LR_SHARED: the system owns and caches these handles, so they are never destroyed here.
        // If the resource isn't there (LoadImage returns zero) this is a no-op and WPF's icon stands.
        // Ported from upstream KillerPDF v1.8.70.
        private void InitializeTaskbarIcons(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            IntPtr module = GetModuleHandle(null);
            if (module == IntPtr.Zero) return;

            IntPtr largeIcon = LoadImage(module, new IntPtr(APPLICATION_ICON_RESOURCE_ID), IMAGE_ICON, 32, 32, LR_SHARED);
            IntPtr smallIcon = LoadImage(module, new IntPtr(APPLICATION_ICON_RESOURCE_ID), IMAGE_ICON, 16, 16, LR_SHARED);
            if (largeIcon == IntPtr.Zero && smallIcon == IntPtr.Zero) return;

            ApplyTaskbarIcons(hwnd, largeIcon, smallIcon);
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                (Action)(() => ApplyTaskbarIcons(hwnd, largeIcon, smallIcon)));
        }

        // Clearing first (lParam = 0) forces the shell to treat the second send as a change even
        // when the handle equals what it already has, which is what refreshes a stale button.
        private static void ApplyTaskbarIcons(IntPtr hwnd, IntPtr largeIcon, IntPtr smallIcon)
        {
            if (largeIcon != IntPtr.Zero)
            {
                SendMessage(hwnd, WM_SETICON, new IntPtr(ICON_BIG), IntPtr.Zero);
                SendMessage(hwnd, WM_SETICON, new IntPtr(ICON_BIG), largeIcon);
            }
            if (smallIcon != IntPtr.Zero)
            {
                SendMessage(hwnd, WM_SETICON, new IntPtr(ICON_SMALL), IntPtr.Zero);
                SendMessage(hwnd, WM_SETICON, new IntPtr(ICON_SMALL), smallIcon);
            }
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

        // The HWND's own DPI (it can lead WPF's during a cross-monitor drag); falls back to the
        // last scale WPF reported on systems without GetDpiForWindow (before Windows 10 1607).
        private double WindowDpiScale(IntPtr hwnd)
        {
            try
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi > 0) return dpi / 96.0;
            }
            catch (EntryPointNotFoundException) { }
            return _currentDpiScale > 0 ? _currentDpiScale : 1.0;
        }

        private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam, bool inSizeMove,
                                            double minWidth, double minHeight, double dpiScale)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            // During a cross-monitor drag (e.g. drag-to-top Aero Snap maximize) the HWND can still
            // belong to the SOURCE monitor at the moment Windows asks for the maximize bounds, so
            // the first maximize after the move came out sized for the monitor the window just
            // left. The pointer is already on the target monitor, so inside the move/size loop use
            // the cursor's monitor; every other request keeps the HWND's own monitor.
            // Ported from upstream KillerPDF #363.
            IntPtr monitor = inSizeMove && GetCursorPos(out POINT cursor)
                ? MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST)
                : MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
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
                // ptMaxSize is the MAXIMIZED size and is correctly the work area, so a maximized
                // window never covers the taskbar. ptMaxTrackSize is different: it caps how large
                // the window can EVER be sized, by any means. Pinning it to this monitor's work
                // area was wrong twice over.
                //
                // Windows arrives here with desktop-wide tracking limits already filled in. Shrink
                // them to the source monitor and a drag that begins on a small monitor and
                // maximizes onto a larger one in the same gesture is silently clamped to the small
                // one's dimensions. It also clamps the explicit whole-monitor bounds full screen
                // sets (ToggleFullScreen deliberately avoids maximizing for exactly this reason),
                // so the taskbar could survive F11.
                //
                // Math.Max keeps whatever Windows supplied and only ever raises the ceiling.
                // Ported from upstream KillerPDF v1.8.4 (#363).
                mmi.ptMaxTrackSize.x = Math.Max(mmi.ptMaxTrackSize.x, mmi.ptMaxSize.x);
                mmi.ptMaxTrackSize.y = Math.Max(mmi.ptMaxTrackSize.y, mmi.ptMaxSize.y);
                // Handling this message means WPF never applies MinWidth / MinHeight to a drag
                // resize, so the hook does it, in device pixels. Capped at the work area so a small
                // or heavily scaled screen can still fit the window (upstream KillerPDF 1.8.92).
                mmi.ptMinTrackSize.x = WindowFitMath.MinTrackPixels(minWidth, dpiScale, mmi.ptMaxSize.x, mmi.ptMinTrackSize.x);
                mmi.ptMinTrackSize.y = WindowFitMath.MinTrackPixels(minHeight, dpiScale, mmi.ptMaxSize.y, mmi.ptMinTrackSize.y);
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
        private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT pt);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr hInst, IntPtr name, uint type, int cx, int cy, uint flags);

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
            // Working path (may be a temp copy after edits). Setting it to one of TDPdf's own
            // %TEMP% files makes this tab the owner of that file: it is deleted with the tab (or the
            // window) instead of being left behind with the document's decrypted content.
            public string? CurrentFile
            {
                get => _workingFile;
                set
                {
                    _workingFile = value;
                    if (TDPdf.Services.TempFilePolicy.IsOurs(value)) TempFiles.Track(value);
                }
            }
            private string? _workingFile;
            // Every %TEMP% file this tab wrote and still depends on; see TempFileSet.
            public readonly TDPdf.Services.TempFileSet TempFiles = new();
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
            // What OriginalPath held when it was opened or last saved (hashed off the UI thread),
            // and the path that fingerprint is for. A save in place compares the file against it to
            // catch another program having changed it meanwhile. Null when there is nothing to
            // compare against.
            public (string Path, Task<TDPdf.Services.FileFingerprint?> Fingerprint)? OriginalFingerprint;
            public string DisplayName = "";      // shown in the tab header and title bar
            public bool IsDirty;

            // True when the source file needed a password, or carried owner restrictions, to open.
            // TDPdf rewrites the whole file through PdfSharpCore on every save and PdfSharpCore
            // emits no /Encrypt unless a password is set on the document, so saving a document that
            // was protected necessarily produces an UNPROTECTED file. The save says so instead of
            // dropping the protection silently, and this gates File ▸ Remove Password. Cleared once
            // a save has written this tab's file unprotected.
            public bool WasProtected;

            // The protection the source file carried (user password, permissions, strength), when it
            // can be written back. While set, saves of this tab's own file re-apply it, so Save and
            // Save As keep the password instead of dropping it; File > Remove Password clears it.
            // Null for an unprotected file, and for a protected one whose protection could not be
            // reproduced (those saves are unprotected and say so).
            public PdfSharpCore.Pdf.Security.PdfProtection? Protection;

            // The user has already agreed, this session, to an in-place save that removes or weakens
            // this file's protection (ProtectedSave.Assess), so Ctrl+S does not ask again. Per tab:
            // another document gets its own question.
            public bool ProtectionChangeAccepted;

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

            // Pending redaction marks, in canvas coordinates, keyed by page. Per-document for the
            // same reason as the polygon vertices below: these are coordinates on THIS document's
            // pages, and a mark leaking across a tab switch would redact the wrong file.
            public readonly Dictionary<int, List<Rect>> RedactionMarks = new();

            // Send for Signature's recipients and placed fields (MainWindow.ESignature*.cs). Per-tab
            // for the same reason as the marks above: the fields are positions on THIS document's
            // pages. Never baked into the PDF, never in Annotations; null until Send is first opened.
            public TDPdf.Services.ESignSendSession? ESignSession;

            // True when the document in this tab carries a digital signature (a signed /FT /Sig
            // field — PdfSignatureScan, no cryptography). Set when it opens and recomputed after
            // every save, because a TDPdf save strips it (ScrubDeadSignatures). Drives the footer's
            // "Digitally signed" indicator (MainWindow.ESignatureRequests.cs).
            public bool HasDigitalSignature;

            // True when a tool has already stripped this tab's digital signature from the LIVE
            // document (Redact and in-place text edit normalize _doc before running) while the file
            // on disk still carries it. The save warning counts signatures in the live document, so
            // without this Ctrl+S would overwrite the signed original without a word. Cleared by a
            // successful save, after which the file on disk is unsigned too.
            public bool SignatureStrippedSinceSave;

            // True when this tab's file is, byte for byte, the signed copy TDPdf downloaded for a
            // completed signature request (SHA-256 against documenso-requests.json). Drives the
            // tab's "Signed" chip and the footer's "Signed · TDPdf Signing". Where the file came
            // from, never a validity claim. Cleared whenever HasDigitalSignature is recomputed.
            public bool ESignSignedCopy;

            // Pending interactive form-field values (AcroForm). Text, dropdowns and checkboxes
            // are keyed by FIELD (FormFieldSiblings.FieldKey: the field's object number, or the
            // widget's own key when the widget is its field), so every widget of one field shares
            // a value; a checkbox stores the field's state name ("/Off" or an on-state). Radios
            // are keyed by field name. Persisted into the PDF on save.
            public readonly Dictionary<int, string>    FormTextValues  = new();
            public readonly Dictionary<int, string>    FormCheckValues = new();
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

            // Background search (upstream 1.9.1). Per tab: the query running for THIS document,
            // which result set is current, and the extracted page text kept between queries.
            public CancellationTokenSource? SearchCts;
            public int SearchGeneration;
            public TDPdf.Services.PdfTextSearch.Index? SearchIndex;

            // View state restored when this tab is re-activated.
            public IReadOnlyList<BitmapSource?>? Thumbnails;
            public int SelectedPageIndex = -1;

            // ── Per-tab scroll + zoom (#399, adapted from upstream KillerPDF) ──────────────────
            // Scroll and zoom used to be purely app-global, so switching tabs dragged one
            // document's zoom onto the next and dropped you wherever the re-render happened to
            // land. These fields make an ALREADY-OPEN tab resume where it was left.
            //
            // They are deliberately NOT a second standing preference. The app-global preference
            // (Settings.DefaultFitMode / Settings.LastManualZoom, read by ApplyViewModeOnOpen)
            // still decides where a NEWLY OPENED document starts — see the long #201 comment
            // there — and nothing on this block is ever written back to it. ViewCaptured=false
            // means "this tab has never been switched away from", which is exactly what makes a
            // brand-new tab fall through to ApplyViewModeOnOpen unchanged; it is cleared again
            // whenever the page geometry moves under the offsets (a reload, or a
            // rotate/crop/transform via SaveTempAndReload), because an offset measured against
            // page heights that no longer exist points at nothing in particular.
            public bool ViewCaptured;
            public double ViewScrollH;
            public double ViewScrollV;
            public double ViewZoomLevel = 1.0;
            public ZoomFitMode ViewFitMode = ZoomFitMode.None;
            public bool ViewManualZoomIntent;   // mirrors MainWindow._manualZoomIntent (#201)
            // The view mode the offsets were captured in. View mode is app-wide, so it can change
            // while a tab sits in the background, and an offset into the continuous strip means
            // nothing in Grid — a mismatch drops the resume instead of scrolling somewhere
            // arbitrary.
            public ViewMode ViewModeAtCapture = ViewMode.Single;

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

        // The custom title bar is client area, so Windows never offers its system menu on a
        // right-click there. Show it ourselves, at the pointer, as a native caption would.
        private void TitleBar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_useNativeWindowFrame || _fullScreen) return;
            e.Handled = true;
            ShowWindowSystemMenu(PointToScreen(e.GetPosition(this)));
        }

        // Alt+Space: the same menu, at the window's top-left corner just under the title bar.
        // GetWindowRect rather than Left/Top, which keep the restored position while maximized.
        private void ShowWindowSystemMenuFromKeyboard()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r)) return;
            double titlePx = _customTitleBar.ActualHeight * WindowDpiScale(hwnd);
            ShowWindowSystemMenu(new Point(r.left, r.top + titlePx));
        }

        // Win32 system menu at a SCREEN point in device pixels. The menu's items are enabled by
        // hand: with WindowStyle=None Windows does not update them for this window, and Restore,
        // Move and Size only make sense for one state each. The chosen command goes back through
        // WM_SYSCOMMAND, so Move / Size get Windows' own keyboard loops and Close the dirty prompt.
        private void ShowWindowSystemMenu(Point screenPx)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            IntPtr menu = GetSystemMenu(hwnd, false);
            if (menu == IntPtr.Zero) return;

            bool maximized = WindowState == WindowState.Maximized;
            bool minimized = WindowState == WindowState.Minimized;
            bool normal = !maximized && !minimized;
            bool canResize = ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
            void Enable(int cmd, bool on) =>
                EnableMenuItem(menu, (uint)cmd, MF_BYCOMMAND | (on ? MF_ENABLED : MF_GRAYED));
            Enable(SC_RESTORE, !normal);
            Enable(SC_MOVE, normal);
            Enable(SC_SIZE, normal && canResize);
            Enable(SC_MINIMIZE, !minimized && ResizeMode != ResizeMode.NoResize);
            Enable(SC_MAXIMIZE, !maximized && canResize);
            Enable(SC_CLOSE, true);
            SetMenuDefaultItem(menu, (uint)(maximized ? SC_RESTORE : SC_CLOSE), 0);

            int cmdChosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_LEFTALIGN | TPM_TOPALIGN | TPM_RIGHTBUTTON,
                (int)Math.Round(screenPx.X), (int)Math.Round(screenPx.Y), hwnd, IntPtr.Zero);
            if (cmdChosen != 0)
                PostMessage(hwnd, WM_SYSCOMMAND, new IntPtr(cmdChosen), IntPtr.Zero);
        }

        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_SIZE = 0xF000;
        private const int SC_MOVE = 0xF010;
        private const int SC_MINIMIZE = 0xF020;
        private const int SC_MAXIMIZE = 0xF030;
        private const int SC_CLOSE = 0xF060;
        private const int SC_KEYMENU = 0xF100;
        private const int SC_RESTORE = 0xF120;
        private const uint MF_BYCOMMAND = 0x0000;
        private const uint MF_ENABLED = 0x0000;
        private const uint MF_GRAYED = 0x0001;
        private const uint TPM_LEFTALIGN = 0x0000;
        private const uint TPM_TOPALIGN = 0x0000;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_RETURNCMD = 0x0100;

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("user32.dll")]
        private static extern int EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

        [DllImport("user32.dll")]
        private static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        // Maximize shows a single box, restore two overlapping ones; the name and ToolTip match
        // the Windows caption button for the state.
        private void UpdateMaximizeButton()
        {
            bool maximized = WindowState == WindowState.Maximized;
            string name = maximized ? "Restore Down" : "Maximize";
            _maximizeBtn.Content = maximized ? "\u2750" : "\u25A1";
            _maximizeBtn.ToolTip = name;
            AutomationProperties.SetName(_maximizeBtn, name);
            if (FindName("MaximizeButtonLabel") is TextBlock label) label.Text = name;
        }

        // Status text keeps at least this much of the footer before labels start to collapse.
        private const double FooterStatusReserve = 120;
        private double _footerCopyrightWidth, _footerVersionWidth;

        // The footer's right-hand group is one Grid cell shared with the status text, so in a
        // narrow window it would clip from the left, taking the zoom and page-size chips with it.
        // Drop the least useful labels first instead: the copyright, then the version.
        private void FitFooterToWidth()
        {
            if (_footerRightGroup is null || _footerCopyright is null) return;
            // A collapsed element measures zero, so remember each label's width while it shows.
            if (_footerCopyright.IsVisible && _footerCopyright.DesiredSize.Width > 0)
                _footerCopyrightWidth = _footerCopyright.DesiredSize.Width;
            if (VersionLabel.IsVisible && VersionLabel.DesiredSize.Width > 0)
                _footerVersionWidth = VersionLabel.DesiredSize.Width;

            double others = 0;
            foreach (UIElement child in _footerRightGroup.Children)
                if (child != _footerCopyright && child != VersionLabel) others += child.DesiredSize.Width;

            double avail = _statusBarBorder.ActualWidth - 32 - FooterStatusReserve;   // 32: the Grid's 16 DIP side margins
            if (avail <= 0) return;
            bool showVersion = others + _footerVersionWidth <= avail;
            bool showCopyright = showVersion && others + _footerVersionWidth + _footerCopyrightWidth <= avail;
            var vVersion = showVersion ? Visibility.Visible : Visibility.Collapsed;
            var vCopyright = showCopyright ? Visibility.Visible : Visibility.Collapsed;
            if (VersionLabel.Visibility != vVersion) VersionLabel.Visibility = vVersion;
            if (_footerCopyright.Visibility != vCopyright) _footerCopyright.Visibility = vCopyright;
        }

        /// <summary>
        /// Points the portable badge's button at what is actually on this machine: the first-run
        /// "Install TDPdf..." when nothing is installed, otherwise the installed version and a way
        /// to manage it. Returns the current offer so a click acts on fresh state, not on launch-time.
        /// </summary>
        private (PortableInstallOffer Offer, Version? Installed) RefreshPortableBadge()
        {
            var (existing, installed) = App.DetectExistingInstall();
            var running = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            var offer = PortableInstallPolicy.Decide(existing, installed, running);

            string label = PortableInstallPolicy.BadgeLabel(offer, installed);
            _installBtn.Content = label;
            AutomationProperties.SetName(_installBtn, label.TrimEnd('.'));
            AutomationProperties.SetHelpText(_installBtn, offer switch
            {
                PortableInstallOffer.FreshInstall =>
                    "Install TDPdf to your user profile and relaunch from the installed location.",
                PortableInstallOffer.Managed =>
                    "TDPdf is installed for all users and managed by your organisation.",
                _ => "TDPdf is already installed. Upgrade, reinstall or uninstall it.",
            });
            return (offer, installed);
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            // Re-read on every click: the install may have changed since launch (an Intune push, an
            // uninstall from Settings, another copy installing).
            var (offer, installed) = RefreshPortableBadge();
            var running = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            string thisVer = PortableInstallPolicy.Display(running);
            string installedVer = installed is null ? "" : $" v{PortableInstallPolicy.Display(installed)}";

            switch (offer)
            {
                case PortableInstallOffer.FreshInstall:
                {
                    // The desktop shortcut is the user's choice, unticked by default: it used to be
                    // created silently although this prompt never mentioned it.
                    var (res, wantDesktop) = TdpDialog.ShowWithCheckbox(this,
                        "Install TDPdf to your user profile?\n\n" +
                        "• Start Menu shortcut\n" +
                        "• Added to \"Open with\" for .pdf files\n" +
                        "• Appears in Add/Remove Programs",
                        "Also add a desktop shortcut",
                        "Install TDPdf", MessageBoxButton.OKCancel);
                    if (res != MessageBoxResult.OK) return;

                    // Hide the badge immediately so it doesn't flash if relaunch is slow
                    _portableBadge.Visibility = Visibility.Collapsed;

                    // The ORIGINAL path, not _currentFile: after a decrypt-on-open _currentFile points at a
                    // working copy in %TEMP% that this process deletes on exit, so the installed build would
                    // relaunch onto a file that is already gone.
                    App.InstallAndRelaunch(_ctx.OriginalPath ?? _currentFile, wantDesktop);
                    return;
                }

                case PortableInstallOffer.Managed:
                    // A machine-wide install is the organisation's: Intune put it there and keeps it
                    // current. This copy runs as the user, so it could not overwrite or remove it without
                    // elevation — and must not try to, or it would be fighting the management channel.
                    TdpDialog.Show(this,
                        $"TDPdf{installedVer} is installed on this computer for all users and is " +
                        "managed by your organisation, which keeps it up to date.\n\n" +
                        $"This portable copy (v{thisVer}) can't replace or remove it. You can keep " +
                        "using this copy, or open the installed TDPdf from the Start menu.",
                        "TDPdf", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
            }

            string comparison = offer switch
            {
                PortableInstallOffer.Upgrade =>
                    $"This portable copy is v{thisVer}, which is newer.\n\n" +
                    "Upgrade replaces the installed copy with this one and relaunches from it.",
                PortableInstallOffer.Downgrade =>
                    $"This portable copy is v{thisVer}, which is OLDER.\n\n" +
                    "\"Replace with older\" downgrades the installed TDPdf to this version and " +
                    "relaunches from it.",
                _ when installed is null =>
                    $"Its version could not be read. This portable copy is v{thisVer}.\n\n" +
                    "Reinstall repairs the installed copy with this one and relaunches from it.",
                _ =>
                    $"This portable copy is the same version (v{thisVer}).\n\n" +
                    "Reinstall repairs the installed copy (shortcuts and file associations) and " +
                    "relaunches from it.",
            };

            var choice = TdpDialog.ShowYesNoCancel(this,
                $"TDPdf{installedVer} is already installed for your user account. {comparison}\n\n" +
                "Uninstall removes the installed copy; this portable copy keeps running.",
                yesLabel: PortableInstallPolicy.ActionLabel(offer)!,
                noLabel: "Uninstall",
                title: "TDPdf is installed",
                image: offer == PortableInstallOffer.Downgrade ? MessageBoxImage.Warning : MessageBoxImage.Question,
                // An older copy replacing a newer install has to be a deliberate click, never Enter.
                defaultToCancel: offer == PortableInstallOffer.Downgrade);

            if (choice == MessageBoxResult.Yes)
            {
                _portableBadge.Visibility = Visibility.Collapsed;
                // ORIGINAL path for the same reason as the fresh install above.
                if (!App.ReinstallAndRelaunch(_ctx.OriginalPath ?? _currentFile))
                {
                    // The install failed (already reported) or was refused. Show the real state.
                    RefreshPortableBadge();
                    _portableBadge.Visibility = Visibility.Visible;
                }
            }
            else if (choice == MessageBoxResult.No)
            {
                App.UninstallFromPortable(this);
                RefreshPortableBadge();
            }
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

        // Points at the rendered copy on tdpdf.com, not the repository blob: the Microsoft Store
        // requires a privacy policy at a working URL and the developer agreement wants a link to
        // it from inside the app as well. the tdpdf-site repo renders it from PRIVACY.md,
        // which stays the single source of the text. The GitHub Pages copy
        // (.github/workflows/pages.yml) stays up because builds up to 2.0.0.0 link there.
        private void HelpPrivacy_Click(object sender, RoutedEventArgs e) =>
            OpenExternalUrl("https://tdpdf.com/privacy/");

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
                $"TDPdf is a Windows PDF editor by The Doodle Project.\n\n" +
                $"{versionLine}\n\n" +
                "Released under the GNU General Public License v3.0.\n\n" +
                // The upstream-project credit moved to Help > Third-Party Licenses, alongside the
                // GPLv3 section 5(a) modification statement. TDPdf has diverged materially — its
                // own multi-document architecture, telemetry, themes and settings — and upstream
                // has since replaced its PDF engine wholesale, so "forked from" no longer
                // describes the relationship usefully on a one-line About box. The attribution
                // itself is untouched: it is required, and it is still in the app, in NOTICE, and
                // in THIRD-PARTY-NOTICES.md.
                //
                // Named here because these are the components a reader would expect to see
                // credited; the full notices, which several of these licences require be
                // distributed with the binary, are under Help > Third-Party Licenses.
                "Built with PDFium, PdfSharpCore, PdfPig, Tesseract, ImageSharp\n" +
                "and OpenTelemetry. See Help > Third-Party Licenses for the\n" +
                "full notices.\n\n" +
                "https://github.com/doodlemania2/TDPdf";
            TdpDialog.Show(this, message, "About TDPdf", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void HelpThirdPartyLicenses_Click(object sender, RoutedEventArgs e) =>
            ThirdPartyLicensesWindow.Show(this);


        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string pwszReason);

        [DllImport("user32.dll")]
        private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

        /// <summary>
        /// Windows sign-out, restart or shutdown (App.OnSessionEnding). WPF does not raise Closing
        /// for that, so without this the dirty-document prompt never ran, unsaved tabs were lost
        /// and the restore-on-launch session was never written. Returns true when the session end
        /// must be refused: Windows then shows "TDPdf is preventing sign out" with the reason set
        /// here, and the user can come back and save. Otherwise saves the session and returns false.
        /// </summary>
        internal bool BlockSessionEndForUnsavedWork()
        {
            CommitActiveTextBox();   // typed but uncommitted text counts as unsaved work
            int dirtyCount = _tabs.Count(t => t.Doc is not null && t.IsDirty);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (dirtyCount > 0)
            {
                if (hwnd != IntPtr.Zero)
                {
                    try
                    {
                        ShutdownBlockReasonCreate(hwnd, dirtyCount == 1
                            ? "You have unsaved changes in TDPdf."
                            : $"You have unsaved changes in {dirtyCount} documents in TDPdf.");
                    }
                    catch { /* the refusal still works without a reason */ }
                }
                Telemetry.TrackEvent("App.SessionEndBlocked");
                return true;
            }
            if (hwnd != IntPtr.Zero) { try { ShutdownBlockReasonDestroy(hwnd); } catch { } }
            // Nothing unsaved: remember the open documents so they come back after the restart.
            // No prompt can be shown while Windows is signing out, so "Ask" saves the session as
            // "Yes" would; "No" still saves nothing.
            CaptureViewState();
            try
            {
                if (TDPdf.Properties.Settings.Default.ReopenSession != "No")
                    SaveSession(OpenSessionFiles());
            }
            catch { /* never hold up the sign-out over the session list */ }
            return false;
        }

        protected override void OnClosed(EventArgs e)
        {
            // The window is gone for good (OnClosing was not cancelled): take every tab's working
            // copies with it. Locked leftovers are collected by the next launch's sweep.
            foreach (var tab in _tabs.ToList())
            {
                try { tab.Doc?.Close(); } catch { }
                tab.TempFiles.DeleteAll();
            }
            _ctx.TempFiles.DeleteAll();   // the active tab is not always in _tabs yet
            base.OnClosed(e);
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

        // ── The saved session is shared by every TDPdf window ───────────────────────────────────
        // Each window is its own process, and they all save to one SessionFiles setting. Each
        // window therefore MERGES on close instead of overwriting: it re-reads the saved list,
        // removes the entries it is responsible for (_sessionContributed) and appends its current
        // tabs, leaving every other window's entries alone (FilePathPolicy.MergeSession). The first
        // window of a login session is responsible for the list it found at startup, which it
        // restores; later windows (tear-offs, or a second window when single-instance forwarding is
        // off or fails) neither restore it nor own it. A per-user named mutex serialises the
        // read-modify-write; a second one, held open for the life of the process, is how a window
        // knows whether it is the first.
        private static readonly string SessionLockName = "Local\\TDPdf.Session.Lock." + Environment.UserName;
        private static readonly string SessionWindowName = "Local\\TDPdf.Session.Window." + Environment.UserName;
        private static Mutex? s_sessionWindowHandle;
        private List<string> _sessionContributed = new();

        /// <summary>
        /// Registers this window as alive and returns true when no other TDPdf window is. The handle
        /// stays open until the process exits; ownership is never taken, so nothing is abandoned.
        /// </summary>
        private static bool RegisterSessionWindow()
        {
            try
            {
                if (s_sessionWindowHandle is not null) return false;   // already registered
                s_sessionWindowHandle = new Mutex(false, SessionWindowName, out bool createdNew);
                return createdNew;
            }
            catch
            {
                return true;   // no way to tell: behave as a lone window did before
            }
        }

        private static void WithSessionLock(Action action)
        {
            Mutex? gate = null;
            bool owned = false;
            try
            {
                gate = new Mutex(false, SessionLockName);
                try { owned = gate.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { owned = true; }   // a window died holding it
            }
            catch { /* proceed unlocked rather than lose the session */ }
            try { action(); }
            finally
            {
                if (owned) { try { gate!.ReleaseMutex(); } catch { } }
                gate?.Dispose();
            }
        }

        /// <summary>
        /// The saved session as it is on disk now. Settings.Default caches what this process read at
        /// startup, so a fresh instance is read to see what other windows have written since.
        /// </summary>
        private static (List<string> Files, string Active) ReadSavedSession()
        {
            string files, active;
            try
            {
                var fresh = new TDPdf.Properties.Settings();
                files = fresh.SessionFiles;
                active = fresh.SessionActiveFile;
            }
            catch
            {
                files = TDPdf.Properties.Settings.Default.SessionFiles;
                active = TDPdf.Properties.Settings.Default.SessionActiveFile;
            }
            return (TDPdf.Services.FilePathPolicy.NormalizeDistinct(
                        (files ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                    active ?? "");
        }

        /// <summary>
        /// First window only: takes responsibility for the session left by the last run, so that
        /// closing this window replaces it (as a lone window always did) rather than adding to it.
        /// </summary>
        private void ClaimSavedSession()
        {
            WithSessionLock(() => _sessionContributed = ReadSavedSession().Files);
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
        // "remember my choice" that locks the answer). Forgetting drops this window's documents;
        // turning the preference to "No" clears the saved paths entirely, for privacy.
        private void HandleSessionOnClose()
        {
            var openFiles = OpenSessionFiles();
            if (openFiles.Count == 0) { SaveSession(openFiles); return; }

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
            else if (TDPdf.Properties.Settings.Default.ReopenSession == "No") ClearSavedSession();
            else SaveSession(new List<string>());   // declined this once: forget only this window's
        }

        /// <summary>Merges this window's documents into the saved session; see the block comment above.</summary>
        private void SaveSession(List<string> openFiles)
        {
            WithSessionLock(() =>
            {
                var (saved, savedActive) = ReadSavedSession();
                var merged = TDPdf.Services.FilePathPolicy.MergeSession(saved, _sessionContributed, openFiles);
                string? mine = TDPdf.Services.FilePathPolicy.Normalize(_ctx.OriginalPath);
                string active =
                    mine is not null && openFiles.Any(f => TDPdf.Services.FilePathPolicy.SamePath(f, mine)) ? mine
                    : merged.Any(f => TDPdf.Services.FilePathPolicy.SamePath(f, savedActive)) ? savedActive
                    : "";
                _sessionContributed = TDPdf.Services.FilePathPolicy.NormalizeDistinct(openFiles);
                if (merged.SequenceEqual(saved, StringComparer.OrdinalIgnoreCase)
                    && string.Equals(active, savedActive, StringComparison.OrdinalIgnoreCase))
                    return;   // nothing changed: leave user.config alone
                var s = TDPdf.Properties.Settings.Default;
                s.SessionFiles = string.Join("|", merged);
                s.SessionActiveFile = active;
                s.Save();
            });
        }

        private static void ClearSavedSession()
        {
            var s = TDPdf.Properties.Settings.Default;
            if (s.SessionFiles.Length == 0 && s.SessionActiveFile.Length == 0
                && ReadSavedSession().Files.Count == 0) return;   // nothing to clear
            s.SessionFiles = "";
            s.SessionActiveFile = "";
            s.Save();
        }

        /// <summary>
        /// Reopens the documents saved from the previous session as tabs, then re-selects the tab that was
        /// active last time. Skipped when the user opted out ("No"), when nothing was saved, or (by the
        /// caller) when a file was passed on the command line — that file takes precedence over the session —
        /// or when this is not the first TDPdf window (the first one already restored it).
        /// </summary>
        private async Task RestoreSessionAsync()
        {
            if (TDPdf.Properties.Settings.Default.ReopenSession == "No") return;

            // The list ClaimSavedSession just read under the lock, rather than Settings.Default's
            // startup copy.
            var files = _sessionContributed
                .Where(System.IO.File.Exists)
                .ToList();
            if (files.Count == 0) return;

            foreach (var f in files)
                await OpenInTabAsync(f);

            string active = ReadSavedSession().Active;
            if (!string.IsNullOrEmpty(active))
            {
                var target = _tabs.FirstOrDefault(t =>
                    TDPdf.Services.FilePathPolicy.SamePath(t.OriginalPath, active));
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
            // Each radio group in its own panel: one Tab stop, arrow keys between the options.
            var themeGroup = TDPdf.Controls.RadioGroupKeyboard.Apply(new StackPanel());
            panel.Children.Add(themeGroup);
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
                themeGroup.Children.Add(radio);
            }

            panel.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 12) });

            panel.Children.Add(new TextBlock
            {
                Text = "Page view",
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushResource("TextPrimary"),
                Margin = new Thickness(0, 0, 0, 8)
            });
            var viewModeGroup = TDPdf.Controls.RadioGroupKeyboard.Apply(new StackPanel());
            panel.Children.Add(viewModeGroup);
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
                viewModeGroup.Children.Add(vmRadio);
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

            panel.Children.Add(new Separator { Margin = new Thickness(0, 0, 0, 12) });
            AddESignatureSettingsSection(panel);   // MainWindow.ESignature.cs

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

        /// <summary>
        /// The theme's AccentGreen at the given alpha, for translucent selection / drag washes. Looked up
        /// at creation time like every other overlay brush, so a wash built after a theme switch uses the
        /// new theme's accent (a hardcoded dark-theme green is unreadable on the Light theme).
        /// </summary>
        private SolidColorBrush AccentWash(byte alpha)
        {
            var c = BrushResource("AccentGreen").Color;
            return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
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
            menu.Items.Add(MakeMenuItem("_Redact", (s, e) => SetTool(EditTool.Redact), "R",
                "Mark areas whose content is permanently removed from the file", "\uE73B"));
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
            int[] selectedPages = SelectedPageIndices();
            menu.Items.Add(MakeMenuItem("Insert Blank Page After", (s, ev) => InsertBlankPage_Click(s!, ev), null, null, "\uE7C3"));
            // Pluralised on the plain count, unlike the Move rows below: every selected page is
            // duplicated whether or not the selection is contiguous, so there is no equivalent of
            // movesBlock's "does this actually move more than one page" question to ask.
            var duplicate = MakeMenuItem(selectedPages.Length > 1 ? "Duplicate Pages" : "Duplicate Page",
                (s, ev) => DuplicatePages_Click(), null,
                "Insert a copy of each selected page after the last of them", "\uE8C8");
            // Greyed rather than falling back to the current page. Duplicating is a structural edit:
            // it rewrites and reloads the document and takes the unsaved overlay annotations with it
            // (see SaveTempAndReload), so guessing at what the user meant is expensive to be wrong
            // about. In practice the list always has a selection \u2014 opening a document and every
            // reload select a page \u2014 so this state is the genuinely ambiguous one, and a greyed row
            // says "choose the pages first" without spending anything to say it.
            duplicate.IsEnabled = selectedPages.Length > 0;
            menu.Items.Add(duplicate);
            menu.Items.Add(new Separator());
            // One Rotate glyph serves both directions: the counter-clockwise row draws it mirrored,
            // so the pair reads as a matched set instead of two unrelated icons.
            menu.Items.Add(MakeMenuItem("Rotate CW",  (s, ev) => RotatePages_Click(90), null, null, RotateGlyph));
            menu.Items.Add(MakeMenuItem("Rotate CCW", (s, ev) => RotatePages_Click(-90), null, null, RotateGlyph, mirrorGlyph: true));
            menu.Items.Add(MakeMenuItem("Transform…", (s, ev) => ToolTransform_Click(s!, ev), null,
                "Rotate by a fine angle, scale, flip, or straighten the page (rasterizes it to an image)", "\uE90F"));
            menu.Items.Add(new Separator());
            // #135: a contiguous multi-page selection now moves as a block, so say so \u2014 the rows
            // used to move exactly one page whatever was selected.
            bool movesBlock = selectedPages.Length > 1
                              && selectedPages[^1] - selectedPages[0] == selectedPages.Length - 1;
            menu.Items.Add(MakeMenuItem(movesBlock ? "Move Pages Up" : "Move Page Up",
                (s, ev) => MoveUp_Click(s!, ev), null, null, "\uE74A"));
            menu.Items.Add(MakeMenuItem(movesBlock ? "Move Pages Down" : "Move Page Down",
                (s, ev) => MoveDown_Click(s!, ev), null, null, "\uE74B"));
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
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) return;
            var indices = new List<int>();
            foreach (var item in selected) indices.Add(PageList.Items.IndexOf(item));
            // RunPageOperation commits the active text box, and on failure puts back the pages and
            // the annotations (and undo snapshots) remapped through the turn below.
            RunPageOperation("Rotate", () =>
            {
                var doc = _doc!;
                // #169: a rotation must not destroy the overlay annotations. SaveTempAndReload's
                // default clears them all, so a second rotation after placing an annotation would
                // lose committed, unsaved work. Remap each rotated page's annotations through the
                // turn first — the render dims here are still the pre-turn frame the user drew on;
                // the reload will re-render at the swapped frame — then keep them across the reload.
                foreach (var idx in indices)
                {
                    // Always derive the canonical DIP canvas frame from page geometry rather than
                    // _renderDims, which may be missing for this page (emptied by a DPI change or an
                    // adopted text edit) or come from a secondary Grid tile's rounding rather than
                    // the primary canvas the user drew on.
                    var dims = AnnotationCanvasSize(doc.Pages[idx]);
                    if (_annotations.TryGetValue(idx, out var anns))
                        TDPdf.Services.AnnotationRotate.Remap(anns, delta, dims.w, dims.h);
                    RemapAnnotationSnapshots(_undoStack, idx, delta, dims.w, dims.h);
                    RemapAnnotationSnapshots(_redoStack, idx, delta, dims.w, dims.h);
                }
                foreach (var idx in indices)
                    doc.Pages[idx].Rotate = ((doc.Pages[idx].Rotate + delta) % 360 + 360) % 360;
                int restoreIdx = PageList.SelectedIndex;
                SaveTempAndReload(keepAnnotations: true);
                PageList.SelectedIndex = Math.Min(restoreIdx, PageList.Items.Count - 1);
                // After a rotation the page aspect ratio changes; always fit-to-page so the
                // full rotated page is visible regardless of the previous zoom level. Re-fit
                // again at Loaded priority once the new page bitmap has laid out.
                FitToPage();
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)FitToPage);
                SetStatus($"Rotated {indices.Count} page(s)");
            });
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

        /// <summary>
        /// Moves unsaved annotations — and the page-snapshot undo history — to their pages' new
        /// numbers after a structural page edit described by <paramref name="map"/> (see
        /// <see cref="TDPdf.Services.PageIndexRemap"/>: map[old] is the new index, or Removed).
        /// </summary>
        /// <remarks>
        /// The companion to <see cref="RemapAnnotationSnapshots"/>, which handles the other
        /// structural edit that keeps its annotations: a rotation changes a page's geometry but not
        /// its number, so it remaps coordinates; insert / delete / reorder / duplicate change the
        /// number but not the geometry, so this remaps indices and leaves the coordinates alone.
        ///
        /// The undo stacks matter as much as the live dictionary. A PageSnapshot addresses its
        /// page by index, so leaving one behind at its old number means a later Ctrl+Z quietly
        /// restores a page's annotations onto its neighbour. A snapshot of a deleted page goes with
        /// the page — there is nothing left for it to restore onto. Document entries carry whole-file
        /// bytes and no page index, so they need nothing here.
        ///
        /// Everything is computed before anything is committed, so a map that fails validation
        /// (two pages onto one) throws with the annotations and both stacks exactly as they were.
        /// Callers apply the map AFTER mutating _doc.Pages and before SaveTempAndReload, so the
        /// annotations always describe whichever page list _doc actually holds.
        /// </remarks>
        private void ApplyPageIndexMap(int[] map)
        {
            var annotations = TDPdf.Services.PageIndexRemap.RemapKeys(_annotations, map);
            var undo = RemapPageSnapshotIndices(_undoStack, map);
            var redo = RemapPageSnapshotIndices(_redoStack, map);

            _annotations.Clear();
            foreach (var (page, list) in annotations)
            {
                foreach (var annotation in list) annotation.PageIndex = page;
                _annotations[page] = list;
            }
            ReplaceHistory(_undoStack, undo);
            ReplaceHistory(_redoStack, redo);
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
                                             string? helpText = null, string? glyph = null, bool mirrorGlyph = false,
                                             bool literalHeader = false)
        {
            var item = new MenuItem { Header = literalHeader ? EscapeMenuHeader(header) : header };
            item.Click += click;
            if (gesture != null)
                item.InputGestureText = gesture;
            if (glyph != null)
                item.Icon = MakeMenuGlyph(glyph, mirrorGlyph);
            // A literal header is a NAME, not a label: its underscores are part of the text and the
            // screen reader should hear them. Only a label's accelerator marker gets stripped.
            var automationName = literalHeader ? header : header.Replace("_", string.Empty);
            AutomationProperties.SetName(item, automationName);
            AutomationProperties.SetHelpText(item, helpText ?? automationName);
            return item;
        }

        /// <summary>
        /// Doubles the underscores in text that is a name rather than a menu label.
        /// </summary>
        /// <remarks>
        /// Our MenuItem ControlTemplate sets RecognizesAccessKey="True" (MainWindow.xaml), so a
        /// lone underscore in a Header is eaten and underlines the following letter instead:
        /// "My_Report.pdf" shows as "MyReport.pdf". Every surface that displays a file name as a
        /// menu header has to come through here. The surfaces that use a TextBlock — the title bar,
        /// the tab chips, the status line, the start-page recents — are unaffected and must NOT.
        /// </remarks>
        private static string EscapeMenuHeader(string text) => text.Replace("_", "__");

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
            // #399: an offset still owed to a tab switch was measured in the mode we are leaving
            // — Continuous's strip and Grid's wrap panel are different scroll surfaces entirely —
            // so it is dropped here rather than being applied against the new one. The tab's
            // stored ViewModeAtCapture makes the same call for its captured state on the next
            // activation.
            _resumeZoomFor = null;
            _resumeScroll = null;
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
            SetStatus(on ? "Book layout on. The cover shows alone" : "Book layout off. Pages pair from the cover");
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
                int rotation = PdfPageGeometry.Rotation(pdfPage);

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
            RunPageOperation("Remove link", () =>
            {
                var pdfPage = _doc!.Pages[pageIndex];
                var annotsArr = pdfPage.Elements.GetArray("/Annots");
                if (annotsArr is null || annotIndex >= annotsArr.Elements.Count) return;
                annotsArr.Elements.RemoveAt(annotIndex);
                MarkDirty();
                // Removing a link changes neither page geometry nor page numbering, so every
                // overlay annotation is still valid where it is (upstream 1.8.90 found the same
                // loss: the default reload threw away every unsaved annotation in the document).
                SaveTempAndReload(keepAnnotations: true);
                // Refresh the current page view so the overlay disappears.
                int sel = PageList.SelectedIndex;
                PageList.SelectedIndex = -1;
                PageList.SelectedIndex = sel;
                SetStatus("Link removed.");
            });
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
                return PageIndexByObjectNumber(_doc).TryGetValue(elemObjNum, out int idx) ? idx : null;
            }
            else if (pageRefItem is PdfInteger pageInt)
            {
                int pn = pageInt.Value;
                if (pn >= 0 && pn < _doc.PageCount) return pn;
            }

            return null;
        }

        // Page object number -> page index for _linkPageMapDoc; see PageIndexByObjectNumber.
        // The document is held weakly: a closed tab or a replaced _doc must not be kept alive by
        // a cache that is only ever consulted for the current one.
        private Dictionary<int, int>? _linkPageMap;
        private WeakReference<PdfDocument>? _linkPageMapDoc;
        private int _linkPageMapCount;

        /// <summary>
        /// The page index each page object number lives at in <paramref name="doc"/>, built once and
        /// reused for every link on every page. Upstream KillerPDF #438.
        /// </summary>
        /// <remarks>
        /// <see cref="ResolveDest"/> used to walk every page of the document comparing object
        /// numbers, once per link — so drawing the links of one page cost links × pages, and a
        /// table-of-contents page in a long document paid it on every render.
        ///
        /// The map is keyed on the document INSTANCE and its page count, which is what makes it
        /// safe without a hook at every edit: a tab switch, an undo restore and every structural
        /// edit (insert, delete, reorder, duplicate, rotate, crop, transform, merge) end by
        /// assigning a freshly reopened <see cref="PdfDocument"/> to <c>_doc</c>, so the key no
        /// longer matches and the map is rebuilt on next use. <see cref="InvalidateLinkPageMap"/>
        /// is also called from SaveTempAndReload so the invalidation is explicit, not incidental.
        /// First index wins on a duplicate object number, which is what the old forward scan
        /// returned.
        /// </remarks>
        private Dictionary<int, int> PageIndexByObjectNumber(PdfDocument doc)
        {
            if (_linkPageMap is not null && _linkPageMapDoc is not null
                && _linkPageMapDoc.TryGetTarget(out var cached) && ReferenceEquals(cached, doc)
                && _linkPageMapCount == doc.PageCount)
                return _linkPageMap;

            var map = new Dictionary<int, int>(doc.PageCount);
            for (int i = 0; i < doc.PageCount; i++)
            {
                // PdfPage.Reference (public) gives us access to ObjectNumber
                var pgRef = doc.Pages[i].Reference;
                if (pgRef != null) map.TryAdd(pgRef.ObjectNumber, i);
            }
            _linkPageMap = map;
            _linkPageMapDoc = new WeakReference<PdfDocument>(doc);
            _linkPageMapCount = doc.PageCount;
            return map;
        }

        /// <summary>Drops the link-target page map; the next <see cref="ResolveDest"/> rebuilds it.</summary>
        private void InvalidateLinkPageMap()
        {
            _linkPageMap = null;
            _linkPageMapDoc = null;
            _linkPageMapCount = 0;
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
        // Tool selection
        // ============================================================

        private void SetTool(EditTool tool)
        {
            // Picking a tool means leaving Electronic Signature field placement; the fields placed so
            // far stay on the tab for the next Send for Signature.
            if (_eSignPlacement is not null) EndESignPlacement(ESignPlacementEnd.Aborted);
            Telemetry.TrackEvent("Tool.Selected", new Dictionary<string, string> { ["Tool"] = tool.ToString() });
            var previousTool = _currentTool;
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
                (_toolRedactBtn, EditTool.Redact),
                (_toolFormBtn, EditTool.Form),
                (_toolMeasureBtn, EditTool.Measure),
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
                EditTool.Redact => Cursors.Cross,
                EditTool.Form => Cursors.Cross,
                EditTool.Measure => Cursors.Cross,
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

            // The redaction bar is the only place the pending marks can be applied or cleared, so
            // it stays up for as long as any mark exists — switching tools must not strand marks
            // on the page with no way to act on them. Leaving the tool is not abandoning the work.
            if (tool == EditTool.Redact || PendingRedactionCount > 0)
                ShowRedactSettings();
            else
                HideRedactSettings();

            // Dropped before the bar is built, so leaving and returning to the tool starts clean
            // and a reference into a document that may since have been reopened cannot survive.
            if (tool != EditTool.Form) _selectedFormWidget = null;

            if (tool == EditTool.Form)
                ShowFormSettings();
            else
                HideFormSettings();

            // Entering or leaving authoring mode changes what the existing fields look like — live
            // controls you can type in, or outlines you can draw over — so the overlays have to be
            // rebuilt, not just left as they were.
            if ((tool == EditTool.Form) != (previousTool == EditTool.Form) && PageList.SelectedIndex >= 0)
                RenderAllAnnotations(PageList.SelectedIndex);

            // Hide signature popup when switching away
            if (tool != EditTool.Signature)
            {
                HideSignaturePopup();
                _pendingSignature = null;
                RemoveSignatureGhost();
            }

            if (tool != EditTool.Crop)
            {
                HideCropPopup();
                ClearCropSelection();
            }

            // The ruler is transient by definition, so leaving the tool takes it with it — that is
            // also what makes Esc (which steps down to Select) the way to dismiss one. Re-selecting
            // Measure while already on it deliberately does NOT clear: SetTool runs on every
            // toolbar click and on the M key, and having the reading vanish because the user
            // pressed the tool's own shortcut again would read as a glitch.
            if (tool != EditTool.Measure) ClearMeasurement();
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
            // Only the DRAG is cancelled here; a finished measurement is left on screen. SetTool
            // calls this before it decides whether the ruler survives, and tearing the visuals
            // down here would make re-selecting the Measure tool wipe its own reading.
            _isMeasuring = false;
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
            _txtSelActive || _isMeasuring;

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
            System.Windows.Automation.AutomationProperties.SetName(_sidebarToggleBtn, (string)_sidebarToggleBtn.ToolTip);
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
            // The footer page-size chip follows the same rule, and this is the one place both the
            // open and the close paths — and every tab switch — pass through.
            UpdatePageSizeReadout();
            ESignUpdateSignedBadge();   // so does the "Digitally signed" indicator (per tab)
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
        private void ToolRedact_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Redact);
        private void ToolForm_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Form);
        private void ToolMeasure_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Measure);
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
        // Form field tool (AcroForm authoring)
        // ============================================================
        //
        // TDPdf has always been able to FILL forms — read the fields, put live controls over them,
        // write the values back. This is the other half: making the fields in the first place.
        //
        // Unlike redaction, a placed field is an ordinary document edit. It goes straight into
        // _doc, it is undoable, and the existing overlay code picks it up on the next render with
        // no special case — GetPageFormFields walks the page's /Annots, and a freshly written
        // widget is just another entry there. The structure itself is built by
        // Services/PdfFormBuilder.cs, which is where the PDF details live and where they are tested.

        private static readonly (FormFieldMode Mode, string Label, string Tip)[] FormModes =
        [
            (FormFieldMode.Text,      "Text",      "A single-line text box"),
            (FormFieldMode.Multiline, "Paragraph", "A text box that wraps onto several lines"),
            (FormFieldMode.CheckBox,  "Check",     "A single check box"),
            (FormFieldMode.Radio,     "Radio",     "A group of mutually exclusive buttons. Drag a box around where they should sit and give the choices"),
            (FormFieldMode.Dropdown,  "Dropdown",  "A list of choices"),
            (FormFieldMode.Signature, "Signature", "An empty signature field for someone to sign later"),
        ];

        private void ShowFormSettings()
        {
            HideFormSettings();

            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            // Two faces, like the shape and text bars: place new fields, or act on the one that is
            // selected. Which one is showing is the answer to "what will the next click do".
            if (_selectedFormWidget is not null)
            {
                BuildSelectedFieldBar(panel);
                MountFormBar(panel);
                return;
            }

            panel.Children.Add(MakeLabel("Field:"));

            foreach (var (mode, label, tip) in FormModes)
            {
                bool active = mode == _formMode;
                var btn = new Button
                {
                    // ToolbarButton for the same reason the shape kind toggles use it: a bare
                    // Button ignores Background/Foreground and wears the OS chrome instead.
                    Style = (Style)FindResource("ToolbarButton"),
                    Content = label,
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(0, 0, 3, 0),
                    Height = 24,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    Cursor = Cursors.Hand,
                    ToolTip = tip,
                    Background = active ? (SolidColorBrush)FindResource("AccentGreenDim")
                                        : (SolidColorBrush)FindResource("BgHover"),
                    Foreground = active ? (SolidColorBrush)FindResource("AccentGreen")
                                        : (SolidColorBrush)FindResource("TextPrimary"),
                    BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                    BorderThickness = new Thickness(1),
                };
                AutomationProperties.SetName(btn, label + " field");
                var chosen = mode;
                btn.Click += (_, _) => { _formMode = chosen; ShowFormSettings(); };
                panel.Children.Add(btn);
            }

            panel.Children.Add(new Rectangle
            {
                Width = 1,
                Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(6, 4, 8, 4),
            });
            panel.Children.Add(MakeLabel("Drag to place \u2014 click a field to edit it"));
            MountFormBar(panel);
        }

        private void MountFormBar(StackPanel panel)
        {
            _formSettingsBar = new Border
            {
                Background = (SolidColorBrush)FindResource("BgPanel"),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Child = panel,
                Margin = new Thickness(8, 0, 0, 0),
            };

            if (PagePreviewPanel.Parent is Grid previewArea)
            {
                Panel.SetZIndex(_formSettingsBar, 100);
                previewArea.Children.Add(_formSettingsBar);
            }
        }

        private void BuildSelectedFieldBar(StackPanel panel)
        {
            var widget = _selectedFormWidget!;
            string name = TDPdf.Services.PdfFormBuilder.NameOf(widget);

            panel.Children.Add(MakeLabel("Field:"));
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(name) ? "(unnamed)" : name,
                Foreground = (SolidColorBrush)FindResource("AccentGreen"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            });

            Button Chip(string label, string tip, bool danger = false)
            {
                var fg = danger ? (SolidColorBrush)FindResource("DangerRed")
                                : (SolidColorBrush)FindResource("TextPrimary");
                var btn = new Button
                {
                    Style = (Style)FindResource("ToolbarButton"),
                    Content = label,
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(0, 0, 3, 0),
                    Height = 24,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    Cursor = Cursors.Hand,
                    ToolTip = tip,
                    Background = (SolidColorBrush)FindResource("BgHover"),
                    Foreground = fg,
                    BorderBrush = danger ? fg : (SolidColorBrush)FindResource("BorderDim"),
                    BorderThickness = new Thickness(1),
                };
                AutomationProperties.SetName(btn, label);
                panel.Children.Add(btn);
                return btn;
            }

            Chip("Rename", "Change the field's name, which is what appears in exported form data")
                .Click += (_, _) => RenameSelectedFormField();

            void Toggle(string label, bool required, string tip)
            {
                bool on = TDPdf.Services.PdfFormBuilder.GetFlag(widget, required);
                var btn = Chip((on ? "\u2713 " : "") + label, tip);
                if (on)
                {
                    btn.Background = (SolidColorBrush)FindResource("AccentGreenDim");
                    btn.Foreground = (SolidColorBrush)FindResource("AccentGreen");
                }
                btn.Click += (_, _) =>
                {
                    PushDocUndo();
                    TDPdf.Services.PdfFormBuilder.SetFlag(widget, required, !on);
                    MarkDirty();
                    ShowFormSettings();
                };
            }
            Toggle("Required", true, "The form cannot be submitted until this field is filled in");
            Toggle("Read-only", false, "Shown but not editable");

            Chip("Delete", "Remove this field from the document", danger: true)
                .Click += (_, _) => DeleteSelectedFormField();

            var done = Chip("Done", "Stop editing this field and go back to placing new ones");
            done.Click += (_, _) => SelectFormWidget(null);
        }

        /// <summary>Selects a field (or clears the selection) and rebuilds everything that shows it.</summary>
        private void SelectFormWidget(PdfDictionary? widget)
        {
            _selectedFormWidget = widget;
            ShowFormSettings();
            if (PageList.SelectedIndex >= 0) RenderAllAnnotations(PageList.SelectedIndex);
        }

        /// <summary>The widget annotation under a canvas point, or null.</summary>
        /// <remarks>
        /// Hit-tested against the same rectangles the outlines are drawn from rather than through
        /// WPF, because those outlines are deliberately not hit-testable — they must not swallow
        /// the press that places a new field. Last match wins, so the topmost of two overlapping
        /// fields is the one selected, which is the one drawn last and therefore the one the user
        /// can see.
        /// </remarks>
        private PdfDictionary? FormWidgetAt(int pageIndex, Point pos)
        {
            if (_doc is null || pageIndex < 0 || pageIndex >= _doc.PageCount) return null;
            if (!_renderDims.TryGetValue(pageIndex, out var dims)) return null;

            List<FormFieldInfo> fields;
            try { fields = GetPageFormFields(pageIndex, dims.w, dims.h); }
            catch { return null; }

            int hitObj = -1;
            foreach (var f in fields)
                if (pos.X >= f.Cx && pos.X <= f.Cx + f.Cw && pos.Y >= f.Cy && pos.Y <= f.Cy + f.Ch)
                    hitObj = f.ObjNum;
            if (hitObj < 0) return null;

            var annots = _doc.Pages[pageIndex].Elements.GetArray("/Annots");
            if (annots is null) return null;
            for (int i = 0; i < annots.Elements.Count; i++)
            {
                var ann = annots.Elements[i] as PdfDictionary ?? DerefItem(annots.Elements[i]) as PdfDictionary;
                if (ann is not null && GetObjectNumber(annots.Elements[i]) == hitObj) return ann;
            }
            return null;
        }

        /// <summary>
        /// Delete / Backspace while the Form tool has a field selected. True when it was handled.
        /// </summary>
        private bool TryDeleteSelectedFormField()
        {
            if (_currentTool != EditTool.Form || _selectedFormWidget is null) return false;
            DeleteSelectedFormField();
            return true;
        }

        private void RenameSelectedFormField()
        {
            if (_doc is null || _selectedFormWidget is null) return;
            string current = TDPdf.Services.PdfFormBuilder.NameOf(_selectedFormWidget);
            string? entered = TdpDialog.PromptText(this, "Rename Field", "Field name:", current);
            if (entered is null) return;

            PushDocUndo();
            if (!TDPdf.Services.PdfFormBuilder.TryRename(_doc, _selectedFormWidget, entered, out string? error))
            {
                TdpDialog.Show(this, $"The field was not renamed: {error}.", "Rename Field",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            MarkDirty();
            SelectFormWidget(_selectedFormWidget);
            SetStatus($"Renamed to \"{entered.Trim()}\"");
        }

        private void DeleteSelectedFormField()
        {
            if (_doc is null || _selectedFormWidget is null) return;
            string name = TDPdf.Services.PdfFormBuilder.NameOf(_selectedFormWidget);

            // Work out which pending values belong to this field BEFORE it is removed, while its
            // widgets can still be found.
            var pendingKeys = FormKeysOfField(_selectedFormWidget);
            string radioName = NearestPartialName(_selectedFormWidget);

            PushDocUndo();   // writes pending values into the document first, so Undo brings them back
            bool removed = TDPdf.Services.PdfFormBuilder.RemoveField(_doc, _selectedFormWidget);
            if (!removed) { SetStatus("Form: that field could not be removed"); return; }

            // The field is gone, so its typed values must not linger: they would be re-applied to a
            // reused object number, and a radio group's name would keep a selection for nothing.
            foreach (int k in pendingKeys) { _formTextValues.Remove(k); _formCheckValues.Remove(k); }
            if (radioName.Length > 0 && !AnyWidgetNamed(radioName)) _formRadioValues.Remove(radioName);

            MarkDirty();
            SelectFormWidget(null);
            SetStatus(string.IsNullOrEmpty(name) ? "Field deleted" : $"Deleted \"{name}\"");
        }

        /// <summary>The pending-value keys (FormFieldSiblings.FieldKey) of every widget of the widget's field.</summary>
        private HashSet<int> FormKeysOfField(PdfSharpCore.Pdf.PdfDictionary widget)
        {
            var keys = new HashSet<int>();
            if (_doc is null) return keys;
            var field = TDPdf.Services.PdfFormBuilder.FieldOf(widget);
            for (int p = 0; p < _doc.PageCount; p++)
            {
                var annots = _doc.Pages[p].Elements.GetArray("/Annots");
                if (annots is null) continue;
                for (int i = 0; i < annots.Elements.Count; i++)
                {
                    var elem = annots.Elements[i];
                    var w = elem as PdfSharpCore.Pdf.PdfDictionary ?? (elem as PdfSharpCore.Pdf.Advanced.PdfReference)?.Value as PdfSharpCore.Pdf.PdfDictionary;
                    if (w is null || !ReferenceEquals(TDPdf.Services.PdfFormBuilder.FieldOf(w), field)) continue;
                    int n = GetObjectNumber(elem);
                    if (n < 0) n = -(p * 10000 + i);
                    keys.Add(TDPdf.Services.FormFieldSiblings.FieldKey(w, n));
                }
            }
            return keys;
        }

        /// <summary>The first /T found walking up from the widget (a radio group's name).</summary>
        private static string NearestPartialName(PdfSharpCore.Pdf.PdfDictionary widget)
        {
            PdfSharpCore.Pdf.PdfDictionary? node = widget;
            for (int d = 0; node is not null && d < 64; d++)
            {
                if (node.Elements["/T"] is PdfSharpCore.Pdf.PdfString t && t.Value.Length > 0) return t.Value;
                var pi = node.Elements["/Parent"];
                node = pi as PdfSharpCore.Pdf.PdfDictionary ?? (pi as PdfSharpCore.Pdf.Advanced.PdfReference)?.Value as PdfSharpCore.Pdf.PdfDictionary;
            }
            return "";
        }

        /// <summary>True when any remaining widget's nearest partial name is <paramref name="name"/>.</summary>
        private bool AnyWidgetNamed(string name)
        {
            if (_doc is null) return false;
            for (int p = 0; p < _doc.PageCount; p++)
            {
                var annots = _doc.Pages[p].Elements.GetArray("/Annots");
                if (annots is null) continue;
                for (int i = 0; i < annots.Elements.Count; i++)
                {
                    var elem = annots.Elements[i];
                    var w = elem as PdfSharpCore.Pdf.PdfDictionary ?? (elem as PdfSharpCore.Pdf.Advanced.PdfReference)?.Value as PdfSharpCore.Pdf.PdfDictionary;
                    if (w is null || w.Elements["/Subtype"]?.ToString()?.Contains("Widget") != true) continue;
                    if (NearestPartialName(w) == name) return true;
                }
            }
            return false;
        }

        private void HideFormSettings()
        {
            if (_formSettingsBar is not null)
            {
                (PagePreviewPanel.Parent as Grid)?.Children.Remove(_formSettingsBar);
                _formSettingsBar = null;
            }
        }

        /// <summary>
        /// Creates the field the user just dragged out.
        /// </summary>
        /// <remarks>
        /// The rectangle arrives in canvas pixels and has to reach the PDF in user-space points,
        /// which is the same conversion redaction makes and therefore the same shared code — see
        /// PdfPageGeometry. A field placed a quarter turn out on a rotated page would be just as
        /// wrong here, and rather more visible.
        /// </remarks>
        private void CreateFormField(int pageIndex, Rect canvasRect)
        {
            if (_doc is null || pageIndex < 0 || pageIndex >= _doc.PageCount) return;
            if (!_renderDims.TryGetValue(pageIndex, out var dims) || dims.w <= 0 || dims.h <= 0)
            {
                SetStatus("Form: this page has not finished rendering yet");
                return;
            }

            var page = _doc.Pages[pageIndex];
            var rect = TDPdf.Services.PdfPageGeometry.CanvasRectToPdf(
                page, canvasRect.X, canvasRect.Y, canvasRect.Width, canvasRect.Height, dims.w, dims.h);

            // Both kinds that hold a list of choices are useless without one, so they ask before
            // anything is written. Everything else places immediately and can be renamed later —
            // placing twenty fields should not mean twenty dialogs.
            IReadOnlyList<string> options = Array.Empty<string>();
            if (_formMode is FormFieldMode.Dropdown or FormFieldMode.Radio)
            {
                string? entered = PromptForOptions(_formMode);
                if (entered is null) return;                       // cancelled
                options = entered.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                 .Select(s => s.Trim())
                                 .Where(s => s.Length > 0)
                                 .ToList();
                if (options.Count == 0) { SetStatus("Form: no choices given, nothing was added"); return; }
            }

            var spec = new TDPdf.Services.PdfFormBuilder.FieldSpec
            {
                Kind = _formMode switch
                {
                    FormFieldMode.CheckBox  => TDPdf.Services.PdfFormBuilder.FieldKind.CheckBox,
                    FormFieldMode.Radio     => TDPdf.Services.PdfFormBuilder.FieldKind.RadioGroup,
                    FormFieldMode.Dropdown  => TDPdf.Services.PdfFormBuilder.FieldKind.Dropdown,
                    FormFieldMode.Signature => TDPdf.Services.PdfFormBuilder.FieldKind.Signature,
                    _                       => TDPdf.Services.PdfFormBuilder.FieldKind.Text,
                },
                Multiline = _formMode == FormFieldMode.Multiline,
                Options = options,
            };

            var rects = _formMode == FormFieldMode.Radio
                ? SplitForRadioButtons(rect, options.Count)
                : new[] { rect };

            try
            {
                PushDocUndo();
                var field = TDPdf.Services.PdfFormBuilder.Add(_doc, page, rects, spec);
                if (field is null) { SetStatus("Form: the field could not be created"); return; }

                MarkDirty();
                // No render-cache invalidation: the page bitmap comes from the file on disk, which
                // does not have this field yet, so re-rasterising would cost a render and show
                // exactly the same pixels. The field is visible through the overlay — an outline
                // while the Form tool is out, a live control the moment it is not — which is the
                // same way filled-in values have always shown before a save.
                RenderAllAnnotations(pageIndex);

                string name = (field.Elements["/T"] as PdfString)?.Value ?? "field";
                Telemetry.TrackEvent("Form.FieldAdded", new Dictionary<string, string> { ["Kind"] = _formMode.ToString() });
                SetStatus(_formMode == FormFieldMode.Radio
                    ? $"Added \"{name}\" with {rects.Count} buttons. Save to write it to the file."
                    : $"Added \"{name}\". Save to write it to the file.");
            }
            catch (Exception ex)
            {
                SetStatus($"Form: the field could not be created ({ex.Message})");
            }
        }

        /// <summary>
        /// Lays one radio button per choice inside the rectangle the user dragged.
        /// </summary>
        /// <remarks>
        /// A radio group is several widgets sharing one value, so one drag has to become several
        /// rectangles. The dragged box is read as the area the group occupies, and the buttons are
        /// spread along its longer axis — which is how someone drawing a row of options across a
        /// page, or a column down one, expects it to come out. Each button is square and no bigger
        /// than the box is thick, so they stay round rather than stretching into ovals.
        /// </remarks>
        private static IReadOnlyList<TDPdf.Services.PdfiumInterop.PdfRect> SplitForRadioButtons(
            TDPdf.Services.PdfiumInterop.PdfRect area, int count)
        {
            count = Math.Max(1, count);
            double w = area.Right - area.Left, h = area.Top - area.Bottom;
            bool horizontal = w >= h;

            double span = horizontal ? w : h;
            double thickness = horizontal ? h : w;
            double size = Math.Max(8, Math.Min(thickness, span / count * 0.6));
            double step = count == 1 ? 0 : (span - size) / (count - 1);

            var rects = new List<TDPdf.Services.PdfiumInterop.PdfRect>(count);
            for (int i = 0; i < count; i++)
            {
                double offset = count == 1 ? (span - size) / 2 : i * step;
                if (horizontal)
                {
                    double l = area.Left + offset;
                    double b = area.Bottom + (h - size) / 2;
                    rects.Add(new TDPdf.Services.PdfiumInterop.PdfRect(l, b, l + size, b + size));
                }
                else
                {
                    // Top-down, so the first choice is the topmost button rather than the bottom one.
                    double t = area.Top - offset;
                    double l = area.Left + (w - size) / 2;
                    rects.Add(new TDPdf.Services.PdfiumInterop.PdfRect(l, t - size, l + size, t));
                }
            }
            return rects;
        }

        /// <summary>Asks for the choices, one per line. Null means cancelled.</summary>
        private string? PromptForOptions(FormFieldMode mode)
        {
            string title = mode == FormFieldMode.Radio ? "Radio Buttons" : "Dropdown";
            return TdpDialog.PromptMultiline(
                this, title,
                "One choice per line:",
                mode == FormFieldMode.Radio ? "Small\nMedium\nLarge" : "Yes\nNo");
        }

        // ============================================================
        // Redaction tool
        // ============================================================
        //
        // Two halves, deliberately kept apart. Marking is reversible, costs nothing, and lives
        // entirely on the canvas. Applying is irreversible, rewrites the file through PDFium, and
        // happens only when the user says so in as many words. Nothing in the marking half ever
        // touches the document.
        //
        // The pipeline that does the work is Services/PdfRedaction.cs: it removes the objects,
        // scrubs the metadata, and then proves the result before anything is written. If it cannot
        // prove it, no file is produced and this method reports exactly what survived. See the file
        // header there for why that is fail-closed rather than best-effort.

        /// <summary>Redaction marks pending across the whole document.</summary>
        private int PendingRedactionCount => _redactionMarks.Values.Sum(list => list.Count);

        /// <summary>
        /// The on-canvas look of a redaction mark: translucent red under a dashed red border.
        /// </summary>
        /// <remarks>
        /// Emphatically NOT a solid black box. A pending mark is a statement of intent, and a mark
        /// that already looks like a finished redaction invites exactly the mistake this whole
        /// feature exists to prevent — printing, screenshotting, or sending a document whose
        /// content is still entirely in the file. It has to read as "marked", not as "gone".
        /// </remarks>
        private Rectangle MakeRedactionVisual(double w, double h)
        {
            var danger = (SolidColorBrush)FindResource("DangerRed");
            return new Rectangle
            {
                Width = w,
                Height = h,
                Fill = new SolidColorBrush(Color.FromArgb(46, danger.Color.R, danger.Color.G, danger.Color.B)),
                Stroke = danger,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                // Like every other overlay on this canvas. Clicking a mark to remove it is
                // resolved against the stored rectangles, not by WPF hit-testing, so a mark must
                // not swallow the press that the drag-to-mark gesture needs.
                IsHitTestVisible = false
            };
        }

        private void RenderRedactionMarks(int pageIndex)
        {
            if (!_redactionMarks.TryGetValue(pageIndex, out var marks)) return;
            foreach (var m in marks)
            {
                if (!IsFinitePositive(m.Width) || !IsFinitePositive(m.Height)) continue;
                var vis = MakeRedactionVisual(m.Width, m.Height);
                Canvas.SetLeft(vis, m.X);
                Canvas.SetTop(vis, m.Y);
                _annotationCanvas.Children.Add(vis);
            }
        }

        private void ClearRedactionMarks()
        {
            if (PendingRedactionCount == 0) return;
            _redactionMarks.Clear();
            int page = PageList.SelectedIndex;
            if (page >= 0) RenderAllAnnotations(page);
            // The bar stays while the tool is still selected — it is the tool's own settings bar,
            // and clearing the marks is not leaving the tool.
            if (_currentTool == EditTool.Redact) ShowRedactSettings(); else HideRedactSettings();
            SetStatus("Redaction marks cleared. The document was not changed");
        }

        // ── Bulk marking from a search ───────────────────────────────────────────────────

        /// <summary>
        /// The canvas size a page will be drawn at, computed for a page that has not been drawn.
        /// </summary>
        /// <remarks>
        /// The renderer fits a page's longest side into a <see cref="Services.PdfDocumentService.RenderBoxDip"/>
        /// box and records the result in DIPs, which is what makes overlay coordinates survive a
        /// zoom. That part is pure geometry, so it can be known for a page nobody has looked at —
        /// and it has to be, because the marks a document-wide search produces are mostly on pages
        /// nobody has looked at. Rasterising three hundred pages to learn how big they are would be
        /// an absurd price for a number the page box already contains.
        ///
        /// The value is stored, so the mark that is about to be expressed in it and the apply pass
        /// that reads it back are working in the same space even if a later real render rounds a
        /// DIP differently.
        /// </remarks>
        private (int w, int h) EnsureRenderDims(int pageIndex)
        {
            if (_renderDims.TryGetValue(pageIndex, out var known) && known.w > 0 && known.h > 0)
                return known;
            if (_doc is null || pageIndex < 0 || pageIndex >= _doc.PageCount) return (0, 0);

            var (wPt, hPt) = TDPdf.Services.PdfPageGeometry.DisplaySize(_doc.Pages[pageIndex]);
            double longest = Math.Max(wPt, hPt);
            if (!IsFinitePositive(longest) || !IsFinitePositive(wPt) || !IsFinitePositive(hPt)) return (0, 0);

            double scale = TDPdf.Services.PdfDocumentService.RenderBoxDip / longest;
            var dims = ((int)Math.Round(wPt * scale), (int)Math.Round(hPt * scale));
            if (dims.Item1 <= 0 || dims.Item2 <= 0) return (0, 0);

            _renderDims[pageIndex] = dims;
            return dims;
        }

        /// <summary>Two marks that land on the same words, allowing for a rounded DIP.</summary>
        private static bool SameMark(Rect a, Rect b) =>
            Math.Abs(a.X - b.X) < 1 && Math.Abs(a.Y - b.Y) < 1 &&
            Math.Abs(a.Width - b.Width) < 1 && Math.Abs(a.Height - b.Height) < 1;

        /// <summary>
        /// Turns every hit from the current search into a redaction mark, across the whole document.
        /// </summary>
        /// <remarks>
        /// It stops at marking, deliberately. A search match is not a decision: "Smith" finds the
        /// name being removed and the Smith who signed the letter, and the half of this feature
        /// that cannot tell them apart must not be the half that destroys them. The hits become
        /// ordinary marks, the user reviews them and can click any one of them off, and Redact
        /// Permanently is still a separate, deliberate press.
        ///
        /// Two things make it more than a loop over the hits:
        ///
        ///   * The hits are PdfPig glyph boxes (one per matched line, from
        ///     <see cref="Services.PdfTextSearch"/>; #187) and a mark is a canvas rectangle. PdfPig's boxes are
        ///     NOT user space: they are the page as displayed, crop origin subtracted and /Rotate
        ///     applied. Each hit is carried into user space by
        ///     <see cref="Services.PdfPageGeometry.TextFrameToPdf(PdfSharpCore.Pdf.PdfPage, Services.PdfiumInterop.PdfRect)"/>
        ///     and only then onto the canvas through the same table the dragged marks are
        ///     converted back through on apply, so the mark and the apply agree on the words.
        ///     Feeding the raw box to the user-space table — as this once did — put every mark on
        ///     a cropped page off by the crop inset and on a turned page a quarter or half turn
        ///     away, over whatever happened to be there rather than over the matched words.
        ///     tests/PdfCore RedactVerify.cs pins both halves.
        ///   * Most hits are on pages that have never been displayed, so they have no render
        ///     dimensions and no canvas space to be a rectangle in. Those are computed here rather
        ///     than left for <see cref="ApplyRedactionsAsync"/> to refuse the whole operation over.
        /// </remarks>
        private void RedactAllSearchMatches()
        {
            if (_doc is null || _currentFile is null) { SetStatus("Redact: no document open"); return; }
            if (_allSearchRects.Count == 0) { SetStatus("Redact: search for something first"); return; }

            int added = 0, unplaceable = 0;
            var touched = new HashSet<int>();

            foreach (var (pageIndex, hits) in _allSearchRects)
            {
                if (pageIndex < 0 || pageIndex >= _doc.PageCount) continue;
                var (rw, rh) = EnsureRenderDims(pageIndex);
                if (rw <= 0 || rh <= 0) { unplaceable += hits.Count; continue; }

                var page = _doc.Pages[pageIndex];
                if (!_redactionMarks.TryGetValue(pageIndex, out var marks))
                    _redactionMarks[pageIndex] = marks = new List<Rect>();

                foreach (var (left, bottom, right, top) in hits)
                {
                    var user = TDPdf.Services.PdfPageGeometry.TextFrameToPdf(
                        page,
                        new TDPdf.Services.PdfiumInterop.PdfRect(Left: left, Bottom: bottom, Right: right, Top: top));
                    var (x, y, w, h) = TDPdf.Services.PdfPageGeometry.PdfRectToCanvas(page, user, rw, rh);
                    if (w <= 0 || h <= 0) { unplaceable++; continue; }

                    var rect = new Rect(x, y, w, h);
                    // Pressing the button twice is a plausible thing to do, and the second mark
                    // would sit invisibly on top of the first while doubling every count the
                    // confirm dialog quotes.
                    if (marks.Any(m => SameMark(m, rect))) continue;

                    marks.Add(rect);
                    touched.Add(pageIndex);
                    added++;
                }

                if (marks.Count == 0) _redactionMarks.Remove(pageIndex);
            }

            if (added == 0)
            {
                SetStatus(unplaceable > 0
                    ? "Redact: those matches could not be placed on their pages, so nothing was marked"
                    : "Redact: every match is already marked");
                return;
            }

            // The orange search highlights and the red marks cover the same words, and two colours
            // of box on one page is a poor thing to review a destructive edit through. The marks
            // are the point from here on.
            CloseSearchBar();

            SetTool(EditTool.Redact);
            int current = PageList.SelectedIndex;
            if (current >= 0) RenderAllAnnotations(current);

            string tail = unplaceable > 0
                ? $"; {unplaceable} could not be placed"
                : "";
            SetStatus($"{added} match{(added == 1 ? "" : "es")} marked on {touched.Count} " +
                      $"page{(touched.Count == 1 ? "" : "s")}. Review them, then press Redact Permanently{tail}");
        }

        // ── The settings / confirm bar ───────────────────────────────────────────────────

        private void ShowRedactSettings()
        {
            HideRedactSettings();

            int marks = PendingRedactionCount;
            int pages = _redactionMarks.Count(kv => kv.Value.Count > 0);
            var danger = (SolidColorBrush)FindResource("DangerRed");

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(MakeLabel(marks == 0
                ? "Redact: drag over the content to destroy"
                : $"{marks} area{(marks == 1 ? "" : "s")} marked on {pages} page{(pages == 1 ? "" : "s")}"));

            var partial = new CheckBox
            {
                Content = "Include partly covered objects",
                IsChecked = _redactRemovePartial,
                Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 10, 0),
                ToolTip = "An object that straddles the edge of a mark is removed whole. Off leaves it in place, " +
                          "which can leave part of what you meant to destroy, so the redaction is then refused rather than written."
            };
            partial.Checked += (_, _) => _redactRemovePartial = true;
            partial.Unchecked += (_, _) => _redactRemovePartial = false;
            panel.Children.Add(partial);

            var scrub = new CheckBox
            {
                Content = "Scrub document metadata",
                IsChecked = _redactScrubMetadata,
                Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                ToolTip = "Clears the title, author and every other Info entry, and drops the XMP packet. " +
                          "Redacting a name from the page while leaving it in the document title is the classic miss."
            };
            scrub.Checked += (_, _) => _redactScrubMetadata = true;
            scrub.Unchecked += (_, _) => _redactScrubMetadata = false;
            panel.Children.Add(scrub);

            var btnStyle = new Style(typeof(Button));
            btnStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
            btnStyle.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(10, 3, 10, 3)));
            btnStyle.Setters.Add(new Setter(Button.MarginProperty, new Thickness(0, 0, 6, 0)));
            btnStyle.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
            btnStyle.Setters.Add(new Setter(Button.FontFamilyProperty, new FontFamily("Segoe UI")));
            btnStyle.Setters.Add(new Setter(Button.FontSizeProperty, 12.0));

            var applyBtn = new Button
            {
                Content = "Redact Permanently",
                Style = btnStyle,
                IsEnabled = marks > 0,
                Background = new SolidColorBrush(Color.FromArgb(40, danger.Color.R, danger.Color.G, danger.Color.B)),
                Foreground = danger,
                BorderBrush = danger,
                ToolTip = "Delete the marked content from the file. This cannot be undone."
            };
            applyBtn.Click += (_, _) => _ = ApplyRedactionsAsync();
            panel.Children.Add(applyBtn);

            var clearBtn = new Button
            {
                Content = "Clear Marks",
                Style = btnStyle,
                IsEnabled = marks > 0,
                Background = Brushes.Transparent,
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                BorderBrush = (SolidColorBrush)FindResource("TextSecondary"),
                ToolTip = "Discard the marks. The document is not changed."
            };
            clearBtn.Click += (_, _) => ClearRedactionMarks();
            panel.Children.Add(clearBtn);

            _redactSettingsBar = new Border
            {
                Background = (SolidColorBrush)FindResource("BgPanel"),
                BorderBrush = danger,
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
                Panel.SetZIndex(_redactSettingsBar, 100);
                previewArea.Children.Add(_redactSettingsBar);
            }
        }

        private void HideRedactSettings()
        {
            if (_redactSettingsBar is not null)
            {
                (PagePreviewPanel.Parent as Grid)?.Children.Remove(_redactSettingsBar);
                _redactSettingsBar = null;
            }
        }

        // ── Apply ────────────────────────────────────────────────────────────────────────

        private async Task ApplyRedactionsAsync()
        {
            if (_doc is null || _currentFile is null) { SetStatus("Redact: no document open"); return; }
            if (PendingRedactionCount == 0) { SetStatus("Redact: nothing marked"); return; }
            CommitActiveTextBox();

            // Build the PDF-space rectangles first: a page we cannot map must stop the whole
            // operation rather than be quietly skipped — skipping it would leave content the user
            // marked for destruction in the file. The frame comes from EnsureRenderDims, not a raw
            // _renderDims lookup: that map is emptied by a monitor DPI change or an adopted text
            // edit, which used to refuse the redaction until the user revisited every marked page,
            // although page geometry alone gives the same frame the marks were drawn in. Only a
            // degenerate page box, which has no frame at all, still stops it.
            var rects = new Dictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>>();
            foreach (var (pageIndex, marks) in _redactionMarks)
            {
                if (marks.Count == 0) continue;
                if (pageIndex < 0 || pageIndex >= _doc.PageCount) continue;
                var dims = EnsureRenderDims(pageIndex);
                if (dims.w <= 0 || dims.h <= 0)
                {
                    TdpDialog.Show(this,
                        $"Page {pageIndex + 1} has marks, but its page size could not be read, so TDPdf cannot " +
                        "map them onto the page. Nothing was redacted.",
                        "Redact", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var page = _doc.Pages[pageIndex];
                // The canvas-to-PDF mapping lives in the service beside the pipeline that consumes
                // it, not here: it is page geometry, it is where a wrong rectangle would silently
                // redact the wrong thing, and out there it is reachable by the tests.
                rects[pageIndex] = marks
                    .Select(m => TDPdf.Services.PdfPageGeometry.CanvasRectToPdf(
                        page, m.X, m.Y, m.Width, m.Height, dims.w, dims.h))
                    .ToList();
            }
            if (rects.Count == 0) { SetStatus("Redact: nothing marked"); return; }

            // Pre-flight. PDFium's content generator silently discards non-device colour, shadings,
            // inline images and soft masks from any stream it rewrites, so removing one object from
            // a page carrying those can recolour or delete something else on it. Those pages can
            // only be redacted by rasterising, which the user has to agree to.
            var raster = new HashSet<int>();
            var reasons = new List<string>();
            foreach (int pageIndex in rects.Keys)
            {
                var hazard = TDPdf.Services.PdfContentInspector.Inspect(_doc.Pages[pageIndex]);
                if (hazard == TDPdf.Services.ContentHazard.None) continue;
                raster.Add(pageIndex);
                reasons.Add($"Page {pageIndex + 1}: {TDPdf.Services.PdfContentInspector.Describe(hazard)}");
            }
            if (raster.Count > 0 && !ConfirmRasterize(raster, reasons)) return;

            int markCount = PendingRedactionCount;

            // Redaction reloads the document from the redacted file, and unsaved annotations are
            // overlay state tied to the old one — the same reason a crop or a page delete drops
            // them. Say so first rather than letting the user find out afterwards.
            int pendingAnnotations = _annotations.Values.Sum(list => list.Count);
            string annotationNote = pendingAnnotations == 0 ? "" :
                $"\n\n{pendingAnnotations} unsaved annotation{(pendingAnnotations == 1 ? "" : "s")} " +
                "will be discarded. Save the document first if you want to keep them.";

            var confirm = TdpDialog.ShowYesNo(this,
                $"Permanently remove the content under {markCount} marked area{(markCount == 1 ? "" : "s")}?\n\n" +
                "The text and images underneath are deleted from the PDF, not covered over. " +
                "This cannot be undone, and it clears the undo history." + annotationNote,
                "Redact Permanently", "Cancel", "Redact", MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            using var op = Telemetry.StartOperation("Redact");
            SetFileOperationBusy(true, "Redacting...");
            string source = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tdpdf_redact_src_{Guid.NewGuid():N}.pdf");
            string result = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tdpdf_redact_out_{Guid.NewGuid():N}.pdf");
            try
            {
                // Redact the CURRENT document, structural edits and all — not the file on disk,
                // which may be several rotations and page deletions behind what is on screen.
                NormalizeLiveDocumentForTool();
                await _pdfDocumentService.SaveAsync(() => _doc!.Save(source), CancellationToken.None);

                TDPdf.Services.PdfRedaction.Result Run() =>
                    TDPdf.Services.PdfRedaction.Apply(source, result, new TDPdf.Services.PdfRedaction.Request
                    {
                        RectsByPage = rects,
                        RemovePartialOverlaps = _redactRemovePartial,
                        ScrubMetadata = _redactScrubMetadata,
                        RasterizePages = raster.ToList(),
                    });

                var outcome = await Task.Run(Run);

                // The scanned-page case, which only the engine can see: the marks fall inside an
                // image, so there is no object to remove. Nothing was written, so asking now and
                // running again costs one render and loses nothing.
                if (!outcome.Ok && outcome.PagesNeedingRaster.Count > 0)
                {
                    SetFileOperationBusy(false);
                    var pages = outcome.PagesNeedingRaster;
                    bool agreed = ConfirmRasterize(pages,
                        pages.Select(p => $"Page {p + 1}: the marked area is part of an image, so there is " +
                                          "nothing smaller to delete (this is what a scanned page looks like)")
                             .ToList());
                    if (!agreed) { SetStatus("Redaction cancelled. The document was not changed"); return; }
                    foreach (int p in pages) raster.Add(p);
                    SetFileOperationBusy(true, "Redacting...");
                    outcome = await Task.Run(Run);
                }

                if (!outcome.Ok)
                {
                    // Nothing was written and the open document is untouched, so the marks stay
                    // exactly where they were and the user can widen them and try again.
                    string detail = outcome.Survivors.Count > 0
                        ? "\n\nStill inside a marked area: " +
                          string.Join(", ", outcome.Survivors.Take(8)) +
                          (outcome.Survivors.Count > 8 ? ", …" : "") +
                          (outcome.SurvivorsAreAnnotations
                              ? ""
                              : "\n\nWiden the marks, or switch on \"Include partly covered objects\".")
                        : "";
                    TdpDialog.Show(this,
                        $"The redaction was not applied: {outcome.Error}{detail}\n\n" +
                        "Your document has not been changed.",
                        "Redact", MessageBoxButton.OK, MessageBoxImage.Warning);
                    SetStatus("Redaction refused. The document was not changed");
                    return;
                }

                AdoptRedactedFile(result);

                string summary = $"Redacted {markCount} area{(markCount == 1 ? "" : "s")}: " +
                                 $"{outcome.ObjectsRemoved} object{(outcome.ObjectsRemoved == 1 ? "" : "s")} removed";
                if (outcome.InvisibleTextRemoved > 0)
                    summary += $", including {outcome.InvisibleTextRemoved} invisible OCR run{(outcome.InvisibleTextRemoved == 1 ? "" : "s")}";
                if (outcome.AnnotationsRemoved > 0)
                    summary += $"; {outcome.AnnotationsRemoved} annotation{(outcome.AnnotationsRemoved == 1 ? "" : "s")} or form field{(outcome.AnnotationsRemoved == 1 ? "" : "s")} removed";
                if (outcome.Rasterized.Count > 0)
                    summary += $"; page{(outcome.Rasterized.Count == 1 ? "" : "s")} " +
                               string.Join(", ", outcome.Rasterized.Select(p => p + 1)) +
                               $" rasterised and no longer contain{(outcome.Rasterized.Count == 1 ? "s" : "")} text";
                SetStatus(summary + ". Save to write it to disk.");
            }
            catch (Exception ex)
            {
                Telemetry.TrackCrash(ex, "Redact", recoverable: true);
                TdpDialog.Show(this, $"The redaction was not applied: {ex.Message}\n\nYour document has not been changed.",
                    "Redact", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Redaction failed. The document was not changed");
            }
            finally
            {
                SetFileOperationBusy(false);
                TDPdf.Services.TempFilePolicy.TryDelete(source);
                if (System.IO.File.Exists(result) && !string.Equals(result, _currentFile, StringComparison.OrdinalIgnoreCase))
                    TDPdf.Services.TempFilePolicy.TryDelete(result);
            }
        }

        /// <summary>
        /// Asks whether to redact pages by rasterising them, and says what that costs.
        /// </summary>
        /// <remarks>
        /// Rasterising is the only thing that works on a scan or on a page whose content stream
        /// cannot be safely rewritten, and it does remove the content completely — the output page
        /// is one picture and nothing else. But it takes the page's text with it: no searching, no
        /// selecting, no copying, no screen reader. That is a real loss to a real person, so it is
        /// spelled out and agreed to rather than done quietly because it happened to be the only
        /// path left.
        /// </remarks>
        private bool ConfirmRasterize(IReadOnlyCollection<int> pages, IReadOnlyList<string> reasons)
        {
            bool one = pages.Count == 1;
            var answer = TdpDialog.ShowYesNo(this,
                $"{pages.Count} page{(one ? "" : "s")} cannot be redacted by removing objects:\n\n" +
                string.Join("\n", reasons.Take(6)) +
                (reasons.Count > 6 ? $"\n…and {reasons.Count - 6} more" : "") +
                $"\n\nTDPdf can redact {(one ? "it" : "them")} by replacing the page with an image of " +
                "itself, with the marked areas painted out. The marked content is genuinely gone, " +
                $"but so is the page's text: {(one ? "it" : "they")} will no longer be searchable, " +
                "selectable, or readable by a screen reader.\n\n" +
                "Other pages, if any, are redacted normally and keep their text.",
                "Rasterise and Redact", "Cancel",
                "Redact", MessageBoxImage.Warning);
            return answer == MessageBoxResult.Yes;
        }

        /// <summary>
        /// Makes the redacted file the working document.
        /// </summary>
        /// <remarks>
        /// Mirrors the tail of <see cref="SaveTempAndReload"/>, with one deliberate difference: the
        /// undo history is DROPPED rather than extended. A document-level undo entry holds the
        /// pre-edit bytes, so an undoable redaction would put the removed content straight back —
        /// and the user, having been told the content was destroyed, would have no reason to look.
        /// The confirmation says this cannot be undone; this is what makes that true.
        /// </remarks>
        private void AdoptRedactedFile(string redactedPath)
        {
            int selectedIdx = PageList.SelectedIndex;

            _doc?.Close();
            _doc = PdfReader.Open(redactedPath, PdfDocumentOpenMode.Modify);
            _currentFile = redactedPath;

            _annotations.Clear();
            _redactionMarks.Clear();
            ESignDropFieldsForLayoutChange(_ctx);
            ClearMeasurement();
            ClearFormState();
            InvalidateRenderCache();
            _contentEditor.ClearCache();
            InvalidateTextRunCache();
            ClearSelection();
            _undoStack.Clear();
            _redoStack.Clear();
            HideRedactSettings();
            MarkDirty();

            RefreshPageList();
            if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                PageList.SelectedIndex = selectedIdx;
            else if (PageList.Items.Count > 0)
                PageList.SelectedIndex = 0;

            if (_viewMode == ViewMode.Continuous)
            {
                int contIdx = Math.Max(0, PageList.SelectedIndex);
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => SetupContinuousView(contIdx)));
            }
        }

        /// <summary>
        /// A fresh <c>%TEMP%</c> path for a working copy, owned by the active tab: it is deleted when
        /// that tab (or the window) closes. See <see cref="TDPdf.Services.TempFileSet"/>.
        /// </summary>
        private string NewTempPath(string tag)
        {
            string path = TDPdf.Services.TempFilePolicy.NewPath(tag);
            _ctx.TempFiles.Track(path);
            return path;
        }

        /// <summary>
        /// Gate every save on unapplied redaction marks.
        /// </summary>
        /// <remarks>
        /// A mark is not an annotation and the save path writes nothing for it, so a save with
        /// marks pending produces a file that looks redacted on screen and is not redacted at all.
        /// That is the single worst outcome this feature can produce, so it is the one thing the
        /// save path is not allowed to do silently. Returns false to abandon the save.
        /// </remarks>
        private bool ConfirmSaveWithPendingRedactions()
        {
            int marks = PendingRedactionCount;
            if (marks == 0) return true;

            var answer = TdpDialog.ShowYesNo(this,
                $"{marks} redaction mark{(marks == 1 ? " has" : "s have")} not been applied.\n\n" +
                "Saving now writes a file that still contains all of the marked content, and the marks " +
                "themselves are not saved. Use Redact Permanently to remove the content first.",
                "Save anyway", "Cancel",
                "Unapplied Redactions", MessageBoxImage.Warning);
            return answer == MessageBoxResult.Yes;
        }

        // ============================================================
        // Page boxes (/MediaBox, /CropBox)
        // ============================================================

        // PageBox, ReadInheritedPageBox, GetVisiblePageBox and PdfRectToCanvas now live in
        // Services/PdfPageGeometry.cs. They moved because redaction needed the same canvas↔PDF
        // mapping and briefly grew a second copy of it — two implementations of one rotation table
        // is exactly how a later fix lands in one of them and an overlay quietly drifts a quarter
        // turn out of place. See the file header there.
        private static PdfPageGeometry.PageBox GetVisiblePageBox(PdfPage page) =>
            PdfPageGeometry.VisibleBox(page);

        private static (double cx, double cy, double cw, double ch) PdfRectToCanvas(
            PdfPageGeometry.PageBox box, int rotation, double canvasW, double canvasH,
            double rx1, double ry1, double rx2, double ry2) =>
            PdfPageGeometry.RectToCanvas(box, rotation, canvasW, canvasH, rx1, ry1, rx2, ry2);

        /// <summary>
        /// The visual-frame → XGraphics matrix for a page. Lives in
        /// <see cref="PdfPageGeometry.VisualToXGraphics"/> (see the full derivation there) so the
        /// OCR text layer and the tests can share it; this name is kept for the annotation bake.
        /// </summary>
        private static XMatrix? VisualToPageMatrix(int rotation, PdfPageGeometry.PageBox box, double pageHeightPt) =>
            PdfPageGeometry.VisualToXGraphics(rotation, box, pageHeightPt);

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
                    if (near && InkDot.TryGetDot(ia.Points.Select(p => (p.X, p.Y)), out double dotX, out double dotY))
                    {
                        // A dot's box is the dot, centred — not a zero-size box hung off its one point.
                        double side = Math.Max(ia.StrokeWidth, 4);
                        bounds = new Rect(dotX - side / 2, dotY - side / 2, side, side);
                        return true;
                    }
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
                    // bounds stays OriginalBounds — it drives the selection rect and the resize
                    // handles, which size the whiteout, so widening it here would silently grow
                    // the whiteout on every resize. Only the HIT is widened, so a click on
                    // replacement glyphs that overhang the original run still lands. See
                    // TextEditHitBounds.
                    bounds = tea.OriginalBounds;
                    return TextEditHitBounds(tea).Contains(pos);

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
                        return MeasureGeometry.HitTestPolygon(shp.Points.Select(q => (q.X, q.Y)).ToList(), (pos.X, pos.Y),
                                              Math.Max(6.0, shp.StrokeWidth + 4), shp.HasFill);
                    }
                    if (shp.Kind == ShapeKind.Line)
                    {
                        // Distance from point to line segment, threshold = max(6, strokeWidth+4) px.
                        double d = MeasureGeometry.DistancePointToSegment((pos.X, pos.Y), (shp.Start.X, shp.Start.Y), (shp.End.X, shp.End.Y));
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
                Background = AccentWash(20),
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
                string label = annot is SignatureAnnotation ? "Signature picture" : "Image";
                SetStatus($"{label} selected. Drag the corner handle to resize, Delete to remove");
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
                SetStatus($"Selected {kind}. Drag to move, corner handle to resize, Delete to remove");
            }
            else
            {
                SetStatus($"Selected {annot.GetType().Name.Replace("Annotation", "").ToLower()} annotation. Drag to move, press Delete to remove");
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

        /// <summary>
        /// Re-render a page after a restyle and refresh the selection visuals WITHOUT rebuilding the
        /// settings bar. Every style-bar control uses it: slider drags so the slider keeps its mouse
        /// capture, and (upstream KillerPDF v1.8.80) toggles, checkboxes and swatches so the bar does
        /// not flash — those repaint their own control in place instead. This replaced a
        /// RestyleReselect that re-ran SelectAnnotation and so tore the bar down and rebuilt it.
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
        /// True when keyboard focus has moved into the text style bar — i.e. the user is restyling
        /// the live text box, not leaving it. The text editors' LostFocus guards use this so that
        /// touching the bar does not commit the box mid-edit.
        /// </summary>
        /// <remarks>
        /// The visual-descendant test alone is not enough (upstream KillerPDF #428): a ComboBox's
        /// drop-down is a Popup with its own visual tree, so while the Font or Size list is open
        /// the focused ComboBoxItem is NOT a visual descendant of the bar. Opening the font list
        /// therefore committed the box, and the family you then picked landed on nothing but the
        /// next box's defaults. An open ComboBox anywhere in the bar means the user is mid-pick.
        /// </remarks>
        private bool IsTextSettingsBarInteraction()
        {
            if (_textSettingsBar is null) return false;
            if (Keyboard.FocusedElement is DependencyObject focused && IsDescendantOf(focused, _textSettingsBar))
                return true;
            return ContainsOpenComboBox(_textSettingsBar);
        }

        private static bool ContainsOpenComboBox(DependencyObject root)
        {
            if (root is ComboBox { IsDropDownOpen: true }) return true;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
                if (ContainsOpenComboBox(VisualTreeHelper.GetChild(root, i))) return true;
            return false;
        }

        /// <summary>
        /// After a pick from a style-bar drop-down, hand keyboard focus back to the live text box so
        /// the user can keep typing in the new font/size without clicking back into it. Deferred so
        /// the ComboBox finishes its own close-and-refocus first; no-op when no editor is live.
        /// </summary>
        private void ReturnFocusToActiveTextBox()
        {
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                if (_activeTextBox is not { } tb || !ReferenceEquals(tb.Parent, _textEditorCanvas)) return;
                tb.Focus();
                Keyboard.Focus(tb);
            });
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
                SetStatus("Selected all text and copied it to the clipboard");
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
                SetStatus("No text selected. Drag to select text");
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
            // Every way out of here that copies nothing must also drop the marquee, as the no-words
            // and exception paths below always have: a rectangle left on screen with nothing on the
            // clipboard reads as "copied" when it was not (upstream KillerPDF 41b5c142 class).
            if (_currentFile is null || pageIdx < 0 || !_renderDims.ContainsKey(pageIdx))
            {
                ClearTextSelection();
                return;
            }

            try
            {
                var (renderW, renderH) = _renderDims[pageIdx];
                if (renderW <= 0 || renderH <= 0) { ClearTextSelection(); return; }

                using var pigDoc = PdfPigDoc.Open(_currentFile);
                if (pageIdx >= pigDoc.NumberOfPages) { ClearTextSelection(); return; }
                var page = pigDoc.GetPage(pageIdx + 1); // PdfPig is 1-based

                // A plain scale and Y flip against PdfPig's own page size, and deliberately NOT the
                // user-space table (PdfPageGeometry.CanvasRectToPdf) the crop and redaction tools
                // use. PdfPig reports words in the page AS DISPLAYED — crop origin subtracted,
                // /Rotate applied — which is exactly the view PDFium rendered onto this canvas; the
                // user-space table would shift a cropped page's marquee by the inset and turn a
                // rotated one a quarter turn. tests/PdfCore RegionCopy.cs renders cropped pages at
                // every quarter turn and pins both halves of that, so a PdfPig upgrade that changed
                // its convention would fail there rather than here.
                var region = PdfPageGeometry.CanvasRectToTextFrame(
                    canvasBounds.X, canvasBounds.Y, canvasBounds.Width, canvasBounds.Height,
                    renderW, renderH, page.Width, page.Height);

                var words = page.GetWords()
                    .Where(w =>
                    {
                        var b = PdfBoxExtent.Of(w.BoundingBox);
                        return PdfPageGeometry.ContainsCenter(region, b.Left, b.Bottom, b.Right, b.Top);
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
            // Corner min/max, not PdfPig's raw Top/Left/Height: those are wrong for quarter-turned,
            // upside-down or mirrored text (negative heights, lines that never group).
            var words = source
                .Select(w => (Word: w, Box: PdfBoxExtent.Of(w.BoundingBox)))
                .OrderByDescending(x => x.Box.Top)
                .ThenBy(x => x.Box.Left)
                .ToList();
            if (words.Count == 0) return string.Empty;

            // Dynamic threshold: 40% of average word height, minimum 4 PDF units
            double avgH = words.Average(x => x.Box.Top - x.Box.Bottom);
            double thresh = Math.Max(4.0, avgH * 0.4);

            var lines = new List<List<(UglyToad.PdfPig.Content.Word Word, (double Left, double Bottom, double Right, double Top) Box)>>();
            double lineY = double.MaxValue;
            foreach (var x in words)
            {
                if (Math.Abs(x.Box.Top - lineY) > thresh)
                {
                    lines.Add([]);
                    lineY = x.Box.Top;
                }
                lines[^1].Add(x);
            }

            // Re-sort each line by X in case the top-Y sort caused any grouping
            // to pull words into the wrong order within a line.
            return string.Join("\n", lines.Select(l =>
                string.Join(" ", l.OrderBy(x => x.Box.Left).Select(x => x.Word.Text))));
        }

        // ============================================================
        // Annotation management
        // ============================================================

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
        /// document-level operations (crop, transform, text-content edits, bookmark and form-field
        /// authoring) can be undone. Page delete / reorder / merge do not call this.
        /// Document edits are a hard history barrier: page-level snapshots from before this
        /// edit refer to a different document layout, so we clear the stacks here.
        /// Must be called BEFORE modifying _doc.
        /// </summary>
        private void PushDocUndo()
        {
            if (_doc is null) return;
            // Undoing a document change reloads the document and drops every pending form value
            // (ClearFormState), so what has been typed but not yet written has to go INTO the
            // snapshot or Undo would silently empty those fields.
            if (HasPendingFormValues) WriteFormValuesToDocument();
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

            // #135 item 2: WPF has no letter-spacing primitive — not a TextBlock property, not a
            // TextDecoration, not a Typography flag — so a spaced annotation cannot be previewed by
            // a TextBlock at all. It is drawn character by character instead, from the SAME
            // per-character advances MeasureTextAnnotation, WrapTextToWidth and the PDF burn-in
            // use, which is the only reason the preview and the saved file can be trusted to agree.
            // Spacing of exactly zero never reaches this branch and stays on the TextBlock below,
            // byte-identically. See TDPdf.Services.TextLetterSpacing.
            if (!TextLetterSpacing.IsNone(ta.LetterSpacing) && BuildSpacedTextVisual(ta) is { } spaced)
            {
                Canvas.SetLeft(spaced, ta.Position.X);
                Canvas.SetTop(spaced, ta.Position.Y);
                _annotationCanvas.Children.Add(spaced);
                return;
            }

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
                tb.Background = TdpDialog.FrozenSolidColorBrush(ta.GetFillColor());
            Canvas.SetLeft(tb, ta.Position.X);
            Canvas.SetTop(tb, ta.Position.Y);
            _annotationCanvas.Children.Add(tb);
        }

        /// <summary>
        /// The on-screen preview of a letter-spaced text annotation: one <c>DrawText</c> per
        /// character, at offsets handed in already computed. #135 item 2.
        /// </summary>
        /// <remarks>
        /// It measures nothing itself. Every offset and every line width arrives from
        /// <see cref="BuildSpacedTextVisual"/>, which got them from <see cref="LayOutSpacedText"/>,
        /// which is the same code path <see cref="MeasureTextAnnotation"/> uses for the selection
        /// box and <see cref="WrapTextToWidth"/> uses for the burn-in's line breaks. A private
        /// measurement in here would be a fourth opinion, and a fourth opinion is exactly how the
        /// text ends up moving when you save.
        ///
        /// It deliberately does NOT clip to its own bounds, even when the annotation has a fixed
        /// Height. The burn-in draws every wrapped line regardless of Height, so clipping here
        /// would hide on screen what the saved PDF contains — the wrong direction for a preview
        /// whose entire job is to be honest about the file.
        /// </remarks>
        private sealed class SpacedTextVisual : FrameworkElement
        {
            private readonly IReadOnlyList<(IReadOnlyList<(string Cluster, double X)> Placed, double Width)> _lines;
            private readonly Typeface _face;
            private readonly Brush _ink;
            private readonly Brush? _fill;
            private readonly double _emSize, _pad, _lineHeight, _baseline, _dpi;
            private readonly bool _underline;

            internal SpacedTextVisual(
                IReadOnlyList<(IReadOnlyList<(string Cluster, double X)> Placed, double Width)> lines,
                Typeface face, double emSize, Brush ink, Brush? fill,
                double pad, double lineHeight, double baseline, double dpi, bool underline)
            {
                _lines = lines;
                _face = face;
                _emSize = emSize;
                _ink = ink;
                _fill = fill;
                _pad = pad;
                _lineHeight = lineHeight;
                _baseline = baseline;
                _dpi = dpi;
                _underline = underline;
                // Same rule as the TextBlock this replaces (#156): annotation visuals never
                // intercept the mouse — selection hit-tests the _annotations data, not the visuals.
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext dc)
            {
                if (_fill is not null)
                    dc.DrawRectangle(_fill, null, new Rect(0, 0, ActualWidth, ActualHeight));

                double y = _pad;
                foreach (var (placed, lineWidth) in _lines)
                {
                    foreach (var (cluster, x) in placed)
                    {
                        dc.DrawText(
                            new FormattedText(cluster,
                                System.Globalization.CultureInfo.CurrentCulture,
                                FlowDirection.LeftToRight, _face, _emSize, _ink, _dpi),
                            new Point(_pad + x, y));
                    }

                    // The underline is drawn, not decorated. TextDecorations.Underline applies per
                    // FormattedText, and each of these is ONE character, so it would come out as a
                    // dashed rule with a gap at every spacing unit. One rectangle spanning the
                    // line's spaced width instead — deliberately the same geometry
                    // DrawTextUnderline burns into the PDF, so the two match.
                    if (_underline && lineWidth > 0)
                        dc.DrawRectangle(_ink, null, new Rect(
                            _pad, y + _baseline + _emSize * 0.12,
                            lineWidth, Math.Max(0.5, _emSize * 0.06)));

                    y += _lineHeight;
                }
            }
        }

        /// <summary>
        /// Builds the per-character preview for a letter-spaced text annotation, or null when the
        /// annotation measures to something WPF will not accept on a Width/Height.
        /// </summary>
        private SpacedTextVisual? BuildSpacedTextVisual(TextAnnotation ta)
        {
            double dpi = VisualTreeHelper.GetDpi(_annotationCanvas).PixelsPerDip;
            var layout = LayOutSpacedText(ta, dpi);
            // #181's guard, applied to the computed box rather than the persisted one: a
            // non-finite Width or Height on a FrameworkElement takes the whole viewer down on the
            // next repaint. Returning null drops this ONE annotation back onto the plain TextBlock.
            if (!IsFinite(layout.Size.Width) || !IsFinite(layout.Size.Height)) return null;

            var typeface = TextLayoutTypeface(ta);
            var (whole, cluster) = TextMeasurers(typeface, ta.FontSize, dpi);

            var lines = new List<(IReadOnlyList<(string Cluster, double X)> Placed, double Width)>(layout.Lines.Count);
            foreach (var line in layout.Lines)
            {
                lines.Add((
                    TextLetterSpacing.Layout(line, ta.LetterSpacing, cluster),
                    TextLetterSpacing.Width(line, ta.LetterSpacing, whole, cluster)));
            }

            const double pad = 2;   // the TextBlock path's Padding, and the burn-in's own pad
            return new SpacedTextVisual(
                lines, typeface, ta.FontSize,
                TdpDialog.FrozenSolidColorBrush(ta.GetColor()),
                ta.HasFill ? TdpDialog.FrozenSolidColorBrush(ta.GetFillColor()) : null,
                pad, layout.LineHeight, layout.Baseline, dpi, ta.Underline)
            {
                Width = layout.Size.Width,
                Height = layout.Size.Height,
            };
        }

        /// <summary>
        /// Bounding size (canvas px, including padding) of a text annotation: the fixed Width/Height when
        /// set, otherwise the measured extent of the (optionally wrapped) content.
        /// </summary>
        /// <summary>
        /// The region a <see cref="TextEditAnnotation"/> actually occupies on the canvas: its
        /// whiteout box unioned with the replacement text drawn from <c>Position</c>.
        /// </summary>
        /// <remarks>
        /// <c>OriginalBounds</c> alone is NOT the hit region. The replacement TextBlock is drawn
        /// from <c>Position</c> with no Width and no Clip, so as soon as the replacement is longer
        /// than the text it replaced, its glyphs extend past <c>OriginalBounds.Right</c>.
        ///
        /// Hit-testing on <c>OriginalBounds</c> then misses exactly the glyphs the user can see and
        /// is aiming at. In <see cref="EditTextAtPosition"/> that miss is expensive: the re-edit
        /// guard falls through to the fresh branch, which re-reads the ORIGINAL pdf and appends a
        /// SECOND TextEditAnnotation. Being last in the list, its whiteout paints over anything
        /// underneath — which is the "changing #4 to #3 removes the strikethrough on #5" report.
        /// </remarks>
        private Rect TextEditHitBounds(TextEditAnnotation tea)
        {
            var bounds = tea.OriginalBounds;
            if (string.IsNullOrEmpty(tea.NewContent)) return bounds;
            if (!IsFinite(bounds.Width) || !IsFinite(bounds.Height)) return bounds;

            double dpi = VisualTreeHelper.GetDpi(_annotationCanvas).PixelsPerDip;
            // Measured with the annotation's OWN family, unlike MeasureTextAnnotation, which pins
            // PdfFontStyle.DefaultFamily — a text edit inherits the font of the run it replaced and
            // renders with it, so measuring anything else would under-read the overhang.
            var ft = new FormattedText(
                tea.NewContent,
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily(tea.FontName),
                             tea.Italic ? FontStyles.Italic : FontStyles.Normal,
                             tea.Bold ? FontWeights.Bold : FontWeights.Normal,
                             FontStretches.Normal),
                tea.FontSize, Brushes.Black, dpi);

            if (!IsFinite(ft.Width) || !IsFinite(ft.Height)) return bounds;
            return Rect.Union(bounds, new Rect(tea.Position.X, tea.Position.Y, ft.Width, ft.Height));
        }

        /// <summary>
        /// A page's annotations in paint order: whiteout-bearing edits first, everything else after.
        /// </summary>
        /// <remarks>
        /// Z-order is otherwise plain list insertion order, on screen and in the saved PDF alike,
        /// and a <see cref="TextEditAnnotation"/>'s first act is to paint an OPAQUE white rectangle
        /// over its OriginalBounds. So any annotation added BEFORE the edit — in practice a
        /// strikethrough on the very text being replaced — was erased by it in both places.
        ///
        /// A whiteout is a background layer, not a peer, so it is ordered as one. Both passes walk
        /// the list in order, so annotations within each group keep their existing relative z-order
        /// and already-annotated documents render unchanged apart from the bug being fixed.
        /// </remarks>
        private static IEnumerable<PageAnnotation> InPaintOrder(IList<PageAnnotation> annots)
        {
            for (int i = 0; i < annots.Count; i++)
                if (annots[i] is TextEditAnnotation or ImageEditAnnotation) yield return annots[i];
            for (int i = 0; i < annots.Count; i++)
                if (annots[i] is not (TextEditAnnotation or ImageEditAnnotation)) yield return annots[i];
        }

        private Size MeasureTextAnnotation(TextAnnotation ta)
        {
            double dpi = VisualTreeHelper.GetDpi(_annotationCanvas).PixelsPerDip;

            // #135 item 2: a spaced annotation is measured from per-character advances, because
            // that is how it is drawn — on screen and in the PDF alike. A whole-string
            // FormattedText would report the KERNED width, which is a different (usually smaller)
            // number than the ink actually occupies once the characters are pushed apart one at a
            // time, and this Size is also the selection/hit box.
            if (!TextLetterSpacing.IsNone(ta.LetterSpacing))
                return LayOutSpacedText(ta, dpi).Size;

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
        /// Everything a letter-spaced text annotation needs laid out once: the lines it breaks
        /// into, the metrics to stack them by, and the box they occupy.
        /// </summary>
        /// <remarks>
        /// Computed in ONE place so <see cref="MeasureTextAnnotation"/> (the selection box) and
        /// <see cref="BuildSpacedTextVisual"/> (the pixels) cannot answer differently — the same
        /// single-source-of-truth rule <see cref="TextTypeface"/> exists to enforce, extended to
        /// spacing.
        /// </remarks>
        private readonly record struct SpacedTextLayout(
            List<string> Lines, double LineHeight, double Baseline, Size Size);

        private SpacedTextLayout LayOutSpacedText(TextAnnotation ta, double dpi)
        {
            var typeface = TextLayoutTypeface(ta);
            var (whole, cluster) = TextMeasurers(typeface, ta.FontSize, dpi);
            double Spaced(string s) => TextLetterSpacing.Width(s, ta.LetterSpacing, whole, cluster);

            // Vertical metrics come off a single-line probe: spacing only ever moves the pen
            // sideways, so line height and baseline are whatever this typeface and size give for
            // any one line.
            //
            // This is WPF's line height, NOT the burn-in's FontSize * 1.2. The two have always
            // disagreed slightly — a TextBlock stacks lines by the font's own line spacing and
            // DrawAnnotationsOnDocument stacks them by 1.2 em — and that divergence is older than
            // letter spacing, applies to every unspaced multi-line annotation already in the
            // fleet, and is not this change's to fix: switching the spaced path to 1.2 em would
            // make the lines visibly jump closer together the moment you moved the spacing slider
            // off zero, for a reason that has nothing to do with spacing. Horizontal placement —
            // the part spacing actually governs — does agree, exactly.
            var probe = new FormattedText(
                " ", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, ta.FontSize, Brushes.Black, dpi);

            // ta.Width - 4 is the burn-in's own wrap width (Width minus its 2px pad on each side).
            // Using the identical expression here, rather than MeasureTextAnnotation's historical
            // Math.Max(1, ...), is what makes the on-screen line breaks the SAME breaks the saved
            // PDF gets. Wrap() already treats a non-positive width as "do not wrap".
            var lines = ta.Width > 0
                ? WrapTextToWidth(ta.Content, ta.FontSize, ta.Width - 4, typeface, ta.LetterSpacing)
                : new List<string>((string.IsNullOrEmpty(ta.Content) ? " " : ta.Content).Split('\n'));

            double widest = 0;
            foreach (var line in lines) widest = Math.Max(widest, Spaced(line));

            double w = ta.Width > 0 ? ta.Width : widest + 8;
            double h = ta.Height > 0 ? ta.Height : lines.Count * probe.Height + 8;
            return new SpacedTextLayout(lines, probe.Height, probe.Baseline, new Size(w, h));
        }

        /// <summary>
        /// Greedy word-wrap of <paramref name="text"/> to <paramref name="maxWidth"/> canvas px at the
        /// given font size, using the same WPF font metrics as the on-screen annotation so the baked PDF
        /// breaks at the same points. Over-long single words are hard-broken by character.
        /// </summary>
        /// <remarks>
        /// #135 item 2: <paramref name="letterSpacing"/> is what makes the wrapping spacing-aware.
        /// Pushing characters apart makes a line wider, so a break that fell after the eighth word
        /// has to fall after the sixth — and it has to fall there on screen and in the PDF alike,
        /// or the text reflows at the moment you save. At spacing 0 the width function below is the
        /// same whole-string <c>FormattedText.Width</c> call this method has always made, so
        /// existing annotations break exactly where they always have; see
        /// <see cref="TextMeasurers"/> and <see cref="TextLetterSpacing"/>.
        ///
        /// The loop itself now lives in <see cref="TextLetterSpacing.Wrap"/>, unchanged, where
        /// tests/PdfCore can reach it.
        /// </remarks>
        private List<string> WrapTextToWidth(string text, double fontSize, double maxWidth,
                                             Typeface typeface, double letterSpacing)
        {
            double dpi = VisualTreeHelper.GetDpi(_annotationCanvas).PixelsPerDip;
            var (whole, cluster) = TextMeasurers(typeface, fontSize, dpi);
            return TextLetterSpacing.Wrap(text, maxWidth,
                s => TextLetterSpacing.Width(s, letterSpacing, whole, cluster));
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

        /// <summary>
        /// The on-screen form of a pen dot (see <see cref="InkDot"/>): a filled circle one stroke
        /// width across. Null when the stroke is not a dot, or its width is unusable as a size.
        /// </summary>
        private static Ellipse? MakeInkDotVisual(InkAnnotation ia)
        {
            if (!InkDot.TryGetDot(ia.Points.Select(p => (p.X, p.Y)), out double cx, out double cy)) return null;
            double d = ia.StrokeWidth;
            if (!IsFinitePositive(d)) return null;
            var dot = new Ellipse
            {
                Fill = TdpDialog.FrozenSolidColorBrush(ia.GetColor()),
                Width = d,
                Height = d,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(dot, cx - d / 2);
            Canvas.SetTop(dot, cy - d / 2);
            return dot;
        }

        private void RenderAllAnnotations(int pageIndex)
        {
            _annotationCanvas.Children.Clear();
            // Clearing the canvas also drops form-field overlays — restore them so they
            // survive every annotation re-render (edits, undo, selection, …).
            if (!_annotations.ContainsKey(pageIndex))
            {
                RenderRedactionMarks(pageIndex);
                RenderESignFieldDrafts(pageIndex);
                RestoreFormOverlays(pageIndex);
                RestorePolyPreview(pageIndex);
                RestoreMeasurement(pageIndex);
                ApplyTextSelectionQuads(pageIndex);
                return;
            }

            foreach (var annot in InPaintOrder(_annotations[pageIndex]))
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
                        var mkBrush = TdpDialog.FrozenSolidColorBrush(mk.GetColor());
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
                            Fill = TdpDialog.FrozenSolidColorBrush(ha.GetColor()),
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
                        if (MakeInkDotVisual(ia) is { } inkDot) { _annotationCanvas.Children.Add(inkDot); break; }
                        if (ia.Points.Count < 2) continue;
                        var poly = new Polyline
                        {
                            Stroke = TdpDialog.FrozenSolidColorBrush(ia.GetColor()),
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
                            Foreground = TdpDialog.FrozenSolidColorBrush(tea.GetColor()),
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

            // Pending redaction marks are not annotations and so are not in the loop above; they
            // are repainted here for the same reason the overlays below are — the clear wiped them.
            // Electronic Signature fields are the same kind of thing and go with them.
            RenderRedactionMarks(pageIndex);
            RenderESignFieldDrafts(pageIndex);

            // The canvas was cleared above, so restore any form-field overlays.
            RestoreFormOverlays(pageIndex);
            RestorePolyPreview(pageIndex);
            RestoreMeasurement(pageIndex);
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

        // ============================================================
        // Measure tool — a transient ruler. Nothing here writes to the document.
        // ============================================================

        /// <summary>
        /// The caption for the current measurement, or null when there is nothing to say yet.
        /// </summary>
        /// <remarks>
        /// <b>This is where the accuracy lives.</b> The two ends are canvas coordinates; they reach
        /// PDF points through <see cref="TDPdf.Services.MeasureGeometry"/>, which routes every step
        /// through <see cref="TDPdf.Services.PdfPageGeometry"/> — the single home for the
        /// canvas↔PDF mapping that the link overlays, the form-field overlays, redaction and the
        /// rasteriser all share. Going through it, rather than dividing by a page width here, is
        /// what makes the reading survive all four of the things that would otherwise break it:
        ///
        ///   * <b>Zoom.</b> Zoom is an ancestor LayoutTransform on PageContentGrid, so a point
        ///     taken with GetPosition(_annotationCanvas) is already in the unscaled canvas frame.
        ///   * <b>Render resolution / HiDPI.</b> The canvas frame is derived from page geometry
        ///     (RenderBoxDip over the longest side), not from a bitmap's pixel count, so the
        ///     denominator below is DPI-normalised DIPs on every monitor.
        ///   * <b>/Rotate, inherited or not.</b> PdfPageGeometry resolves the angle by walking
        ///     /Parent and applies the matching quarter-turn table, so a 90° page's swapped axes
        ///     are handled rather than silently scaling the distance by the aspect ratio.
        ///   * <b>A CropBox that differs from the MediaBox.</b> VisibleBox picks the box PDFium
        ///     actually rasterised and keeps its origin, so an inset or offset crop does not
        ///     stretch the ruler.
        ///
        /// EnsureRenderDims rather than a raw _renderDims lookup for the same reason redaction uses
        /// it: the frame is pure geometry and is knowable even if this page's bitmap has not landed.
        /// </remarks>
        private string? MeasurementCaption()
        {
            if (!_hasMeasurement || _doc is null) return null;
            if (_measurePage < 0 || _measurePage >= _doc.PageCount) return null;

            var dims = EnsureRenderDims(_measurePage);
            if (dims.w <= 0 || dims.h <= 0) return null;

            double points = TDPdf.Services.MeasureGeometry.DistancePoints(
                _doc.Pages[_measurePage],
                _measureA.X, _measureA.Y, _measureB.X, _measureB.Y,
                dims.w, dims.h);
            return TDPdf.Services.MeasureGeometry.Format(points);
        }

        /// <summary>
        /// Draws (or redraws) the ruler: the line, a tick at each end, and the reading.
        /// </summary>
        /// <remarks>
        /// Rebuilt from scratch on every mouse-move rather than mutated in place. It is four small
        /// visuals on a canvas that already re-renders every annotation on far less provocation,
        /// and "rebuild from the two endpoints" is the only version of this that cannot leave a
        /// stale cap or a caption from a previous drag behind.
        ///
        /// Everything goes on _annotationCanvas, so the ruler scales with the page exactly as the
        /// annotations do — a line drawn between two points on the page has to stay between those
        /// two points when the page is zoomed. The caption rides along with it; the same reading is
        /// also pushed to the status bar on release, which is the copy that stays legible at 25%.
        /// </remarks>
        private void RenderMeasurement()
        {
            ClearMeasurementVisuals();
            if (!_hasMeasurement || _measurePage != PageList.SelectedIndex) return;
            if (!IsFinite(_measureA.X) || !IsFinite(_measureA.Y)) return;
            if (!IsFinite(_measureB.X) || !IsFinite(_measureB.Y)) return;

            var accent = (SolidColorBrush)FindResource("AccentGreen");

            AddMeasureVisual(new Line
            {
                X1 = _measureA.X, Y1 = _measureA.Y,
                X2 = _measureB.X, Y2 = _measureB.Y,
                Stroke = accent,
                StrokeThickness = 1.5,
                // Like every other overlay on this canvas: the ruler must never swallow the press
                // that starts the next measurement.
                IsHitTestVisible = false
            });

            // End caps, drawn as a short tick perpendicular to the ruler at each end — the way a
            // dimension line is drawn on a drawing, and the thing that makes it unambiguous which
            // two points the number refers to when the line runs over dense content.
            double dx = _measureB.X - _measureA.X, dy = _measureB.Y - _measureA.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 0.5)
            {
                const double capHalfLength = 5;
                double nx = -dy / len * capHalfLength, ny = dx / len * capHalfLength;
                foreach (var end in new[] { _measureA, _measureB })
                    AddMeasureVisual(new Line
                    {
                        X1 = end.X - nx, Y1 = end.Y - ny,
                        X2 = end.X + nx, Y2 = end.Y + ny,
                        Stroke = accent,
                        StrokeThickness = 1.5,
                        IsHitTestVisible = false
                    });
            }

            string? caption = MeasurementCaption();
            if (caption is null) return;

            var readout = new Border
            {
                Background = (SolidColorBrush)FindResource("BgPanel"),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2, 6, 2),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = caption,
                    Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11
                }
            };
            // Offset from the midpoint rather than centred on it, so the reading never sits on top
            // of the line it is reporting. Clamped at the canvas origin only — a ruler dragged to
            // the very edge would otherwise put its own caption off the left/top of the page.
            Canvas.SetLeft(readout, Math.Max(0, (_measureA.X + _measureB.X) / 2 + 10));
            Canvas.SetTop(readout, Math.Max(0, (_measureA.Y + _measureB.Y) / 2 - 26));
            AddMeasureVisual(readout);
        }

        private void AddMeasureVisual(UIElement visual)
        {
            _annotationCanvas.Children.Add(visual);
            _measureVisuals.Add(visual);
        }

        /// <summary>Takes the ruler's visuals off the canvas but keeps the measurement itself.</summary>
        private void ClearMeasurementVisuals()
        {
            foreach (var v in _measureVisuals)
                if (_annotationCanvas.Children.Contains(v)) _annotationCanvas.Children.Remove(v);
            _measureVisuals.Clear();
        }

        /// <summary>
        /// Forgets the measurement entirely. Cheap and idempotent, so every path that should not
        /// leave a ruler behind can simply call it.
        /// </summary>
        private void ClearMeasurement()
        {
            ClearMeasurementVisuals();
            _isMeasuring = false;
            _hasMeasurement = false;
            _measurePage = -1;
        }

        /// <summary>
        /// Puts the ruler back after <see cref="RenderAllAnnotations"/> cleared the canvas — the
        /// same contract as <see cref="RestorePolyPreview"/>. Without it, anything that re-renders
        /// the page mid-measurement (an undo, a restyle, a redaction mark going down on a page the
        /// ruler is sitting on) would wipe the line while the drag is still live, and the next
        /// mouse-move would be drawing into a detached visual.
        /// </summary>
        private void RestoreMeasurement(int pageIndex)
        {
            if (!_hasMeasurement || _measurePage != pageIndex) return;
            RenderMeasurement();
        }

        // ============================================================
        // Overlay restore and shape/image render helpers
        // ============================================================

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
            var stroke = TdpDialog.FrozenSolidColorBrush(shp.GetStrokeColor());
            SolidColorBrush? fill = shp.HasFill ? TdpDialog.FrozenSolidColorBrush(shp.GetFillColor()) : null;
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
        // Temp save/reload
        // ============================================================

        /// <summary>
        /// Writes the in-memory document to a fresh working temp file and reopens it, after a page
        /// edit (rotate, delete, insert, reorder, duplicate, merge, crop, transform, remove link).
        /// </summary>
        /// <remarks>
        /// Upstream KillerPDF 1.9.0 (111406b8, b070350d): the save and reopen happen FIRST, and only
        /// once the new document is open is <c>_doc</c> swapped and the per-document state cleared.
        /// It used to clear marks, fields, form state and selection, mark dirty and close
        /// <c>_doc</c> before saving, so a failed save or reopen left the tab holding a closed
        /// document. Now a failure throws with <c>_doc</c> untouched and still usable; callers go
        /// through <see cref="RunPageOperation"/>, which also undoes their in-memory changes.
        /// </remarks>
        /// <param name="selectIndex">Page to select afterwards; default is the current selection.
        /// Either way it is clamped to the new page count, so deleting the last pages keeps the
        /// view at the end rather than jumping to page 1 (upstream a6035013).</param>
        private void SaveTempAndReload(bool keepAnnotations = false, int? selectIndex = null)
        {
            if (_doc is null || _currentFile is null) return;
            var doc = _doc;
            int selectedIdx = selectIndex ?? PageList.SelectedIndex;
            // Typed form values are keyed by object number, which the temp save below renumbers,
            // so they cannot survive as pending state. Write them into the document instead, where
            // the reload reads them back as the fields' values — otherwise rotating, deleting or
            // reordering a page, or removing a link, silently emptied every field filled in since
            // the last save. The document is already dirty, so nothing reaches disk until a save.
            // A widget that is a direct object has no object number and is keyed by its page and
            // /Annots position instead — which a delete or reorder has just moved, so that key could
            // now name a different field. Only object-number keys are carried over. (RunPageOperation
            // writes them all before the edit, while every key is still right, so this is normally
            // a no-op.)
            if (HasPendingFormValues)
            {
                foreach (var k in _formTextValues.Keys.Where(k => k < 0).ToList()) _formTextValues.Remove(k);
                foreach (var k in _formCheckValues.Keys.Where(k => k < 0).ToList()) _formCheckValues.Remove(k);
                WriteFormValuesToDocument();
            }
            var tempPath = NewTempPath("temp");
            doc.Save(tempPath);
            var reopened = OpenWorkingCopy(ref tempPath);

            // The new document is open: from here on nothing can leave the tab without one.
            try { doc.Close(); } catch { /* the old instance is discarded either way */ }
            _doc = reopened;
            _currentFile = tempPath;

            // Edits that change page GEOMETRY (crop / transform) invalidate the overlay
            // annotations, whose canvas coordinates are tied to the pre-edit frame, so they are
            // cleared by default. Every caller that can keep them passes keepAnnotations: true
            // having first made them valid for the new layout: rotation remaps its pages'
            // annotations through the turn (RotatePages_Click); insert / delete / reorder /
            // duplicate renumber them with their pages (ApplyPageIndexMap); merge and append-drop
            // only add pages at the end, so nothing needs to move.
            if (!keepAnnotations) _annotations.Clear();
            // Unconditional, for the same reason the redaction marks below are: every caller has
            // just changed page geometry or page numbering, so a surviving ruler would be two
            // canvas points measured against a page that is no longer the page they were taken on.
            ClearMeasurement();
            // Redaction marks go unconditionally, keepAnnotations or not. A mark is a rectangle on
            // a page index, and every caller here has just moved, deleted, reordered or resized
            // pages — so a surviving mark would point at whatever now occupies that spot. Rotation
            // remaps its annotations through the turn and keeps them; there is no equivalent for a
            // mark, and quietly redacting the wrong area is not a risk worth carrying to save the
            // user a re-drag.
            if (PendingRedactionCount > 0)
            {
                _redactionMarks.Clear();
                HideRedactSettings();
                // Held: every caller of this sets its own status line immediately afterwards
                // ("Cropped 1 page", "Deleted 2 pages"), and losing the notice would leave the
                // marks silently gone.
                SetStatusHeld("Redaction marks were cleared because the page layout changed. Mark the areas again.", 4000);
            }
            // Placed Electronic Signature fields are positions on pages for exactly the same reason.
            ESignDropFieldsForLayoutChange(_ctx);
            ClearFormState();
            // #399: rotate / delete / reorder / crop / transform all move the page geometry, so
            // this tab's captured scroll offset now measures against a layout that no longer
            // exists. Drop the resume point and let the next activation place the view the way it
            // did before the feature, rather than restoring a number that means nothing.
            _ctx.ViewCaptured = false;
            ClearSelection();
            MarkDirty();
            ShowReloadedDocument(selectedIdx);
        }

        /// <summary>
        /// Opens a working temp file for editing. PdfSharpCore can silently produce a broken
        /// cross-reference entry when re-saving an encrypted (owner-restricted RC4) PDF after a
        /// modification such as a page rotation: the save succeeds but re-opening the result in
        /// Modify mode then throws "Unexpected token 'xref'". That reopen failure is piped through
        /// PDFium, which rebuilds a valid xref and strips encryption while preserving the edit, and
        /// the open is retried; <paramref name="path"/> then names the repaired file.
        /// </summary>
        private static PdfDocument OpenWorkingCopy(ref string path)
        {
            try
            {
                return PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            }
            catch (Exception openEx) when (TDPdf.Services.PdfDocumentService.IsXRefException(openEx))
            {
                var fixedPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    $"tdpdf_fixed_{Guid.NewGuid():N}.pdf");
                if (!TDPdf.Services.PdfiumInterop.TryPdfiumRepair(path, fixedPath))
                    throw; // PDFium also failed — re-throw the original reopen error
                path = fixedPath;
                return PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            }
        }

        /// <summary>
        /// Drops the caches tied to the old document instance and re-renders the page list after
        /// <c>_doc</c> was swapped for a reopened copy. <paramref name="selectedIdx"/> is clamped to
        /// the new page count.
        /// </summary>
        private void ShowReloadedDocument(int selectedIdx)
        {
            InvalidateRenderCache();
            _contentEditor.ClearCache();
            // Rotate / delete / reorder / crop / transform all change page geometry, so the
            // flowing-selection character cache goes with the render cache. (The cache key already
            // changes — the working path was repointed at a fresh temp file — but this keeps the
            // invalidation explicit rather than incidental.)
            InvalidateTextRunCache();
            // Same reasoning for the link-target page map (#438): keyed on the document instance,
            // which the reload replaced, but dropped here explicitly all the same.
            InvalidateLinkPageMap();
            RefreshPageList();
            if (PageList.Items.Count > 0)
                PageList.SelectedIndex = Math.Clamp(selectedIdx, 0, PageList.Items.Count - 1);

            // Continuous view caches one slot per page; the page set may have changed (rotate,
            // delete, reorder, crop) so rebuild the strip from the reloaded document.
            if (_viewMode == ViewMode.Continuous)
            {
                int contIdx = Math.Max(0, PageList.SelectedIndex);
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => SetupContinuousView(contIdx)));
            }

            // Rotate / crop / transform change the current page's geometry without necessarily
            // changing WHICH page it is, so the chip is refreshed here rather than left to a
            // selection change that may well restore the very same index.
            UpdatePageSizeReadout();
        }

        /// <summary>
        /// Runs a page edit so that a failure leaves the document exactly as it was, and tells the
        /// user. <paramref name="operation"/> mutates <c>_doc</c> and the annotation map, then calls
        /// <see cref="SaveTempAndReload"/>. Returns false when it failed (the dialog has been shown).
        /// </summary>
        /// <remarks>
        /// Upstream KillerPDF 1.9.0 (1b9e9d5d, e1ea224a). Callers change the in-memory document
        /// and remap annotations and undo history BEFORE the reload, so a throw part-way used to
        /// leave a half-edited document, annotations on the wrong pages, or a closed document. The
        /// rollback point is taken here, before any of that:
        ///   * the document is written to a temp file. Typed form values are written into it first,
        ///     while their object-number and position keys still name the right widgets, because the
        ///     save renumbers objects (so this also keeps position-keyed values a reorder used to drop);
        ///   * the annotation map and both undo stacks are deep-cloned (rotation remaps annotation
        ///     coordinates in place).
        /// If even the snapshot cannot be written, the working file on disk is the fallback. It
        /// holds every page edit made so far, but not unsaved bookmark or text edits made in memory
        /// since the last reload.
        /// </remarks>
        private bool RunPageOperation(string failureTitle, Action operation)
        {
            if (_doc is null) return false;
            var snapshot = BeginPageOperation();
            try
            {
                operation();
                EndPageOperation(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                RollBackPageOperation(snapshot, failureTitle, ex);
                return false;
            }
        }

        /// <summary>The rollback point <see cref="RunPageOperation"/> takes; see its remarks.</summary>
        private sealed record PageOperationSnapshot(
            string? DocumentPath,
            string? WorkingFile,
            int SelectedIndex,
            Dictionary<int, List<PageAnnotation>> Annotations,
            List<UndoEntry> Undo,
            List<UndoEntry> Redo);

        /// <summary>
        /// Takes the rollback point for a page edit. Split from <see cref="RunPageOperation"/> for the
        /// one caller (the sidebar file drop) whose edit spans awaits and so cannot be one Action.
        /// </summary>
        private PageOperationSnapshot BeginPageOperation()
        {
            CommitActiveTextBox();   // a half-typed box is part of the unsaved work kept below
            if (HasPendingFormValues)
            {
                WriteFormValuesToDocument();
                ClearFormState();
            }
            string? docPath = null;
            if (_doc is not null)
            {
                docPath = NewTempPath("temp");
                try { _doc.Save(docPath); }
                catch { TDPdf.Services.TempFilePolicy.TryDelete(docPath); _ctx.TempFiles.Release(docPath); docPath = null; }
            }
            return new PageOperationSnapshot(
                docPath,
                _currentFile,
                PageList.SelectedIndex,
                _annotations.ToDictionary(kv => kv.Key, kv => kv.Value.Select(a => a.Clone()).ToList()),
                CloneHistory(_undoStack),
                CloneHistory(_redoStack));
        }

        /// <summary>The edit succeeded (or changed nothing): drop the rollback copy.</summary>
        private static void EndPageOperation(PageOperationSnapshot snapshot)
        {
            if (snapshot.DocumentPath is not null) TDPdf.Services.TempFilePolicy.TryDelete(snapshot.DocumentPath);
        }

        /// <summary>
        /// Puts the document, annotations and undo history back as <paramref name="snapshot"/> took
        /// them, then shows the error.
        /// </summary>
        private void RollBackPageOperation(PageOperationSnapshot snapshot, string failureTitle, Exception ex)
        {
            Telemetry.TrackEvent("Page.OperationFailed", new Dictionary<string, string>
            {
                ["Operation"]     = failureTitle,
                ["ExceptionType"] = ex.GetType().FullName ?? "Unknown",
            });
            bool restored = false;
            foreach (var candidate in new[] { snapshot.DocumentPath, snapshot.WorkingFile })
            {
                if (candidate is null || !System.IO.File.Exists(candidate)) continue;
                try
                {
                    string path = candidate;
                    var restoredDoc = OpenWorkingCopy(ref path);
                    try { _doc?.Close(); } catch { /* replaced either way */ }
                    _doc = restoredDoc;
                    _currentFile = path;
                    _annotations.Clear();
                    foreach (var (page, list) in snapshot.Annotations) _annotations[page] = list;
                    ReplaceHistory(_undoStack, snapshot.Undo);
                    ReplaceHistory(_redoStack, snapshot.Redo);
                    ClearFormState();
                    ClearSelection();
                    ShowReloadedDocument(snapshot.SelectedIndex);
                    restored = true;
                    break;
                }
                catch { /* try the next candidate */ }
            }
            TdpDialog.Show(this,
                $"{failureTitle} failed:\n{ex.Message}" +
                (restored ? "\n\nThe document was left as it was before." : ""),
                "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
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
            // Carrying a signature: the plain wheel sizes it (Ctrl+wheel below still zooms).
            if (TryResizeSignatureGhost(e)) { e.Handled = true; return; }

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                // Proportional to the delta and anchored on the pointer (see ZoomAnchorMath). This
                // used to be one whole preset per wheel MESSAGE, so a precision-touchpad pinch —
                // a burst of small-delta messages — skipped several presets at a time. Keyboard and
                // toolbar zoom still step through the presets; only the analogue inputs glide.
                BeginManualZoom();
                ContinueZoomGesture(e.GetPosition(PagePreviewPanel),
                    TDPdf.Services.ZoomAnchorMath.ZoomForWheel(Zoom.ZoomLevel, e.Delta,
                        ZoomViewModel.MinZoomLevel, ZoomViewModel.MaxZoomLevel));
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
        //    AnimateSidebarWidth) and dragging a window edge does the same. Both clear any
        //    threshold worth setting. (Ctrl+wheel and pinch used to be a third — one flick walked
        //    nine presets, a full pass each — but a gesture now runs its full pass once, on
        //    settle, and bypasses this detector while live; see ContinueZoomGesture.) What was
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
            // A zoom gesture in flight (Ctrl+wheel / pinch): the transform above is the whole live
            // update — the bitmap on screen is scaled, not re-rendered — plus the scroll that keeps
            // the pointer's point still. Everything below runs once, from SettleZoomGesture. The
            // churn detector is skipped too: a long touchpad pinch is sustained by design, and what
            // that detector exists to catch is repeated FULL passes, which this is not.
            if (_applyingGestureZoom)
            {
                ScheduleZoomAnchorCorrection();
                return;
            }
            // Any other zoom source (toolbar, keyboard, a fit, a tab switch) supersedes a gesture
            // still inside its settle window; drop it so it cannot settle a second pass on top.
            if (!_settlingZoomGesture && _zoomGestureAnchor is not null) CancelZoomGesture();
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
                // ...except after a zoom gesture, which has already scrolled so the point under the
                // pointer stayed put; snapping to the page top now would undo exactly that.
                if (!_settlingZoomGesture)
                {
                    int curIdx = PageList.SelectedIndex;
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        (Action)(() => ScrollContinuousToPageSuppressed(curIdx)));
                }
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
                //
                // #399: _restoringTabZoom excludes the one kind of zoom change that is not a
                // decision at all — a tab switch putting back the zoom that tab was already on.
                // That zoom was recorded here when the user chose it; letting it write again
                // would mean clicking back to an older tab could retire the preference set on the
                // tab just left, so a per-tab RESUME deliberately writes nothing but LastZoomLevel
                // (which only tracks what is on screen).
                if (_manualZoomIntent && !_restoringTabZoom)
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
            if (!double.IsFinite(scale) || scale <= 0) return;
            // A one-finger drag reports a scale of exactly 1. The START threshold is small because
            // a slow pinch arrives as many frames of ~1.002: the old 0.01 floor on every frame
            // silently dropped those, so a gentle pinch did nothing at all. Once a gesture is
            // running every frame counts, or the slow tail of a pinch would be lost the same way.
            if (_zoomGestureAnchor is null && Math.Abs(scale - 1.0) < 0.002) return;
            e.Handled = true;
            if (_doc is null) return;
            // #131: a pinch is as explicit as Ctrl+wheel and stops the view tracking the window,
            // the same as every other manual path. It never did so on its own — it only ever
            // reached BeginManualZoom by accident, through the zoom combo reading its own update
            // back as a user pick, and then only when the pinch happened to land on a preset.
            BeginManualZoom();
            // ManipulationOrigin is in the manipulation container's coordinates, which is this grid
            // unless a ManipulationStarting handler says otherwise; translate from whichever it is
            // so the anchor is right either way. The grid carries the zoom as a LayoutTransform,
            // so its own coordinates are NOT viewport coordinates.
            Point viewportOrigin = e.ManipulationContainer is UIElement container
                ? container.TranslatePoint(e.ManipulationOrigin, PagePreviewPanel)
                : _pageContentGrid.TranslatePoint(e.ManipulationOrigin, PagePreviewPanel);
            ContinueZoomGesture(viewportOrigin,
                TDPdf.Services.ZoomAnchorMath.ZoomForPinch(Zoom.ZoomLevel, scale,
                    ZoomViewModel.MinZoomLevel, ZoomViewModel.MaxZoomLevel));
        }

        // ============================================================
        // Zoom gestures: Ctrl+wheel, precision-touchpad pinch, touchscreen pinch
        // ============================================================
        //
        // A gesture is a run of zoom messages with no gap longer than ZoomGestureSettleMs. While
        // one is running, each message only moves the ScaleTransform — the page bitmap on screen is
        // simply scaled, which is instant — and scrolls so the document point that was under the
        // pointer when the gesture began stays under it. The expensive half of ApplyZoom (the
        // re-render at the new DPI, secondary-page reflow, link/form overlays, Continuous
        // re-sharpen) and the user.config write run ONCE, when the gesture settles. Previously
        // every wheel message did all of that, including a Settings.Save() disk write.
        //
        // The anchor is captured once per gesture, not per message, because layout lags the
        // messages: re-reading "the point under the pointer" between a scale change and the layout
        // pass that applies it reads a stale mapping, and the zoom creeps sideways over a long
        // pinch. It is stored in PageContentGrid's own (pre-LayoutTransform) coordinates, which do
        // not change with zoom — nor with the settle re-render, since the page tile is sized from
        // the fixed RenderBoxDip and only its pixel density follows the zoom.

        /// <summary>Quiet time after the last zoom message that ends a gesture.</summary>
        private const int ZoomGestureSettleMs = 200;
        private System.Windows.Threading.DispatcherTimer? _zoomGestureTimer;
        /// <summary>The document point being held still, in PageContentGrid coordinates; null when no gesture is running.</summary>
        private Point? _zoomGestureAnchor;
        /// <summary>Where in the viewport the anchor has to stay: the pointer position the gesture began at.</summary>
        private Point _zoomGestureTarget;
        /// <summary>The tab the gesture belongs to, so a tab switch inside the settle window cannot settle it onto another document.</summary>
        private DocumentContext? _zoomGestureCtx;
        /// <summary>Raised only around a gesture's own zoom write, so ApplyZoom can tell it from every other zoom source.</summary>
        private bool _applyingGestureZoom;
        /// <summary>Raised only while the settled gesture runs the full ApplyZoom, so Continuous keeps the anchored scroll instead of snapping to the page top.</summary>
        private bool _settlingZoomGesture;
        /// <summary>The zoom the gesture began at, so a gesture that never moved it can settle to nothing.</summary>
        private double _zoomGestureStartZoom;
        /// <summary>Bumped per scheduled anchor correction; only the newest one runs.</summary>
        private long _zoomAnchorRevision;

        /// <summary>
        /// Applies one zoom message of a gesture: <paramref name="newZoom"/>, held about
        /// <paramref name="viewportPoint"/> if this message starts the gesture.
        /// </summary>
        private void ContinueZoomGesture(Point viewportPoint, double newZoom)
        {
            if (_zoomGestureAnchor is null || !ReferenceEquals(_zoomGestureCtx, _ctx))
            {
                _zoomGestureTarget = viewportPoint;
                _zoomGestureCtx = _ctx;
                _zoomGestureStartZoom = Zoom.ZoomLevel;
                try { _zoomGestureAnchor = PagePreviewPanel.TranslatePoint(viewportPoint, _pageContentGrid); }
                catch (InvalidOperationException) { _zoomGestureAnchor = null; }   // not in a common visual tree yet
            }

            if (_zoomGestureTimer is null)
            {
                _zoomGestureTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(ZoomGestureSettleMs) };
                _zoomGestureTimer.Tick += (_, _) => SettleZoomGesture();
            }
            _zoomGestureTimer.Stop();
            _zoomGestureTimer.Start();

            _applyingGestureZoom = true;
            try { Zoom.SetZoomLevel(newZoom); }
            finally { _applyingGestureZoom = false; }
        }

        /// <summary>
        /// The gesture has gone quiet: run the full zoom pass once — re-render at the final DPI,
        /// reflow, re-sharpen, and persist the zoom (the one SaveZoomSetting of the gesture).
        /// </summary>
        private void SettleZoomGesture()
        {
            _zoomGestureTimer?.Stop();
            bool sameTab = ReferenceEquals(_zoomGestureCtx, _ctx);
            // Cleared first: the anchor correction the last message queued has its own copy, and
            // ApplyZoom below must not mistake this pass for a new zoom superseding a live gesture.
            _zoomGestureAnchor = null;
            _zoomGestureCtx = null;
            // A different tab is on screen now; its activation ran its own full zoom pass, and
            // settling here would re-render and persist the wrong document's view.
            if (!sameTab || _doc is null) return;
            // A gesture spent entirely against the 5% / 400% clamp changed nothing, and the old
            // per-preset path did no pass for it either (an equal SetZoomLevel raises no change).
            if (Zoom.ZoomLevel == _zoomGestureStartZoom) return;
            _settlingZoomGesture = true;
            try { ApplyZoom(); }
            finally { _settlingZoomGesture = false; }
        }

        /// <summary>Drops a gesture in flight without settling it — another zoom source has taken over.</summary>
        private void CancelZoomGesture()
        {
            _zoomGestureTimer?.Stop();
            _zoomGestureAnchor = null;
            _zoomGestureCtx = null;
            _zoomAnchorRevision++;   // and any anchor correction still queued for it
        }

        /// <summary>
        /// After the layout pass for the new scale, scrolls so the gesture's anchor is back under
        /// the pointer. Loaded priority runs after layout (Render); only the newest request runs,
        /// so a burst of messages faster than layout costs one scroll, not one per message.
        /// </summary>
        private void ScheduleZoomAnchorCorrection()
        {
            if (_zoomGestureAnchor is not Point anchor) return;
            Point target = _zoomGestureTarget;
            long revision = ++_zoomAnchorRevision;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
            {
                if (revision != _zoomAnchorRevision || PagePreviewPanel.Visibility != Visibility.Visible) return;
                // Force the pass rather than trust one has run: the extent the offsets are clamped
                // against, and the anchor's new position, are both products of it.
                PagePreviewPanel.UpdateLayout();
                Point now;
                try { now = _pageContentGrid.TranslatePoint(anchor, PagePreviewPanel); }
                catch (InvalidOperationException) { return; }
                var (h, v) = TDPdf.Services.ZoomAnchorMath.OffsetsKeepingAnchor(
                    PagePreviewPanel.HorizontalOffset, PagePreviewPanel.VerticalOffset,
                    now.X, now.Y, target.X, target.Y,
                    PagePreviewPanel.ScrollableWidth, PagePreviewPanel.ScrollableHeight);
                // In Continuous this is deliberately NOT suppressed: the scroll moves the view like
                // a user scroll does, so the page counter and sidebar should follow it the same way.
                PagePreviewPanel.ScrollToHorizontalOffset(h);
                PagePreviewPanel.ScrollToVerticalOffset(v);
            }));
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
                UpdateRemoveCropEnabled();
                // A measurement belongs to the page it was taken on — pages can differ in size, so
                // carrying one across would be a number about a page nobody is looking at.
                ClearMeasurement();
                _pageJumpBox.Text = (PageList.SelectedIndex + 1).ToString();
                UpdatePageSizeReadout();   // pages can differ in size, so the chip follows the page

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

        // The overlay is modal in effect, so keyboard focus belongs inside it while it shows
        // (Tab cycles there, see the XAML) and goes back where it was when it closes. Driven by
        // IsVisibleChanged so every open and close path is covered. Idea from upstream KillerPDF
        // 1.8.92 (OverlayFocus).
        private IInputElement? _shortcutOverlayReturnFocus;

        private void ShortcutOverlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if ((bool)e.NewValue)
            {
                var focused = Keyboard.FocusedElement;
                _shortcutOverlayReturnFocus = focused is Visual v && ShortcutOverlay.IsAncestorOf(v) ? null : focused;
                // After layout, so the card's buttons exist and are focusable.
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, (Action)(() =>
                {
                    if (ShortcutOverlay.IsVisible)
                        ShortcutOverlay.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                }));
            }
            else
            {
                var back = _shortcutOverlayReturnFocus;
                _shortcutOverlayReturnFocus = null;
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, (Action)(() =>
                {
                    // Leave focus alone if something else already took it on purpose.
                    var now = Keyboard.FocusedElement;
                    bool stranded = now is null || now == this || (now is Visual nv && ShortcutOverlay.IsAncestorOf(nv))
                                    || (now is UIElement u && !u.IsVisible);
                    if (!stranded) return;
                    if (back is UIElement target && target.IsVisible && target.IsEnabled && target.Focusable)
                        Keyboard.Focus(target);
                    else if (back is ContentElement ce && ce.IsEnabled && ce.Focusable)
                        Keyboard.Focus(ce);
                }));
            }
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
}
