using System.Configuration;

namespace TDPdf.Properties
{
    internal sealed class Settings : ApplicationSettingsBase
    {
        private static readonly Settings defaultInstance = (Settings)Synchronized(new Settings());

        public static Settings Default => defaultInstance;

        [UserScopedSetting]
        [DefaultSettingValue("System")]
        public string Theme
        {
            get => (string)this[nameof(Theme)];
            set => this[nameof(Theme)] = value;
        }

        [UserScopedSetting]
        [DefaultSettingValue("True")]
        public bool UseNativeWindowFrame
        {
            get => (bool)this[nameof(UseNativeWindowFrame)];
            set => this[nameof(UseNativeWindowFrame)] = value;
        }

        // Last page zoom, as TRUE zoom (1 = the page at natural size, which is the percentage the
        // status bar and the zoom box show). See MainWindow.DisplayZoomFactor for why that is not
        // the same number as the ScaleTransform outside Continuous view.
        [UserScopedSetting]
        [DefaultSettingValue("1")]
        public double LastZoomLevel
        {
            get => (double)this[nameof(LastZoomLevel)];
            set => this[nameof(LastZoomLevel)] = value;
        }

        // Remembered fit preference (upstream v1.7.1). One of the ZoomFitMode enum names:
        // "None" (default), "Width", "Page". Written only when the user explicitly picks Fit Width
        // or Fit Page — the zoom dropdown, or Ctrl+2 / Ctrl+3 — never by a fit the app applies on
        // its own. When set it wins over the per-view-mode open rule in ApplyViewModeOnOpen, so
        // someone on a small screen does not have to choose Fit Page again for every document.
        [UserScopedSetting]
        [DefaultSettingValue("None")]
        public string DefaultFitMode
        {
            get => (string)this[nameof(DefaultFitMode)];
            set => this[nameof(DefaultFitMode)] = value;
        }

        // When true (default), launching TDPdf while it is already running
        // (e.g. double-clicking another PDF in Explorer) forwards the file to
        // the existing window as a new tab instead of opening a second window.
        [UserScopedSetting]
        [DefaultSettingValue("True")]
        public bool SingleInstanceTabs
        {
            get => (bool)this[nameof(SingleInstanceTabs)];
            set => this[nameof(SingleInstanceTabs)] = value;
        }

        // Persisted page view mode (app-wide). One of the ViewMode enum names:
        // "Single", "Continuous", "TwoPage", "Grid". Defaults to Grid, which is
        // the layout the app opened in before view modes were introduced.
        [UserScopedSetting]
        [DefaultSettingValue("Grid")]
        public string ViewMode
        {
            get => (string)this[nameof(ViewMode)];
            set => this[nameof(ViewMode)] = value;
        }

        // Recent-files list (most-recent first, capped at 10). Full paths joined by '|',
        // which is illegal in Windows paths so it can never collide with a real path.
        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string RecentFiles
        {
            get => (string)this[nameof(RecentFiles)];
            set => this[nameof(RecentFiles)] = value;
        }

        // Privacy switch for the recent-files list (#146, upstream v1.6.5). When true, opening a
        // document no longer records its path, so nothing about the user's documents accumulates on
        // a shared machine. Turning it on in the Settings dialog also empties the stored list.
        // Default False keeps the recent list working for everyone who has not asked for otherwise.
        [UserScopedSetting]
        [DefaultSettingValue("False")]
        public bool DontRememberRecentFiles
        {
            get => (bool)this[nameof(DontRememberRecentFiles)];
            set => this[nameof(DontRememberRecentFiles)] = value;
        }

        // --- Print dialog: remember the last device-level choices across sessions. ---

        // Full name of the last-used print queue; empty falls back to the OS default printer.
        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string PrintPrinter
        {
            get => (string)this[nameof(PrintPrinter)];
            set => this[nameof(PrintPrinter)] = value;
        }

        // Last print orientation: "Portrait" or "Landscape".
        [UserScopedSetting]
        [DefaultSettingValue("Portrait")]
        public string PrintOrientation
        {
            get => (string)this[nameof(PrintOrientation)];
            set => this[nameof(PrintOrientation)] = value;
        }

        // Last color mode: "Color" or "Grayscale".
        [UserScopedSetting]
        [DefaultSettingValue("Color")]
        public string PrintColor
        {
            get => (string)this[nameof(PrintColor)];
            set => this[nameof(PrintColor)] = value;
        }

        // Last two-sided (duplex) choice; only applied when the printer supports it.
        [UserScopedSetting]
        [DefaultSettingValue("False")]
        public bool PrintDuplex
        {
            get => (bool)this[nameof(PrintDuplex)];
            set => this[nameof(PrintDuplex)] = value;
        }

        // Recently picked custom annotation colors (most-recent first, capped in code).
        // Comma-separated #RRGGBB hex values; surfaced as the "Recent" row in the color picker.
        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string CustomColors
        {
            get => (string)this[nameof(CustomColors)];
            set => this[nameof(CustomColors)] = value;
        }

        // When true, clicking a URL link in a PDF opens it in the browser without the
        // safety confirmation prompt. Set when the user ticks "Don't ask again" in that
        // prompt; the scheme allow-list (http/https/mailto only) still applies regardless.
        [UserScopedSetting]
        [DefaultSettingValue("False")]
        public bool SkipLinkConfirm
        {
            get => (bool)this[nameof(SkipLinkConfirm)];
            set => this[nameof(SkipLinkConfirm)] = value;
        }

        // --- Reopen documents on next launch (session restore) ---

        // Whether to reopen the last session's documents on the next launch. One of:
        // "Ask" (default) prompts on quit when documents are open; "Yes" always reopens
        // silently; "No" never reopens and never persists the file list (privacy).
        [UserScopedSetting]
        [DefaultSettingValue("Ask")]
        public string ReopenSession
        {
            get => (string)this[nameof(ReopenSession)];
            set => this[nameof(ReopenSession)] = value;
        }

        // Full paths of the documents open at last exit, in tab order, joined by '|' (illegal in
        // Windows paths, so it can't collide with a real path). Restored on launch. Untitled /
        // merged / imported / recovered docs (no lasting on-disk home) are excluded.
        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string SessionFiles
        {
            get => (string)this[nameof(SessionFiles)];
            set => this[nameof(SessionFiles)] = value;
        }

        // Full path of the tab that was active at last exit, so it is re-selected after restore.
        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string SessionActiveFile
        {
            get => (string)this[nameof(SessionActiveFile)];
            set => this[nameof(SessionActiveFile)] = value;
        }

        // Which face of the keyboard-shortcuts overlay to show: "list" (the static two-column
        // reference, default) or "keyboard" (the rendered visual keyboard). Remembered across
        // sessions so the overlay reopens in the view the user last chose.
        [UserScopedSetting]
        [DefaultSettingValue("list")]
        public string ShortcutView
        {
            get => (string)this[nameof(ShortcutView)];
            set => this[nameof(ShortcutView)] = value;
        }

        // Display-only "night mode" for the document itself (#135): when true the rendered page
        // pixels are shown with inverted colors in the main viewer. Never touches the file — save,
        // flatten, print, image export, OCR and the sidebar thumbnails all stay true-color. Like
        // ViewMode this is an app-wide reading preference, so it applies to every tab. Default False
        // means a reset user.config (App.EnsureSettingsHealthy) simply comes back true-color.
        [UserScopedSetting]
        [DefaultSettingValue("False")]
        public bool InvertDocumentColors
        {
            get => (bool)this[nameof(InvertDocumentColors)];
            set => this[nameof(InvertDocumentColors)] = value;
        }

        // Whether the night mode above inverts PICTURES along with the rest of the page (#135
        // follow-up). Default False, so photos and charts keep their real colors — a negative of a
        // photograph is worse than useless. True restores the whole-page inversion, which is what a
        // SCANNED document needs: there the entire page is one image, so the carve-out would make
        // night mode do nothing. Companion to InvertDocumentColors and equally display-only.
        [UserScopedSetting]
        [DefaultSettingValue("False")]
        public bool DocInvertImages
        {
            get => (bool)this[nameof(DocInvertImages)];
            set => this[nameof(DocInvertImages)] = value;
        }

        // App-wide UI scale (upstream v1.6.5): the LayoutTransform factor applied to the chrome —
        // menu, toolbar, tab strip, and sidebar. Never touches the document pane, so app size and
        // page zoom stay two separate controls (LastZoomLevel above is the page one). Clamped to
        // 0.7–2.5 by ApplyAppScale on read as well as on write, so a hand-edited or partially
        // written user.config can't leave the chrome unusable. Like ViewMode / InvertDocumentColors
        // this is an app-wide preference; the default of 1 means a reset user.config
        // (App.EnsureSettingsHealthy) simply comes back at 100%.
        [UserScopedSetting]
        [DefaultSettingValue("1")]
        public double AppScale
        {
            get => (double)this[nameof(AppScale)];
            set => this[nameof(AppScale)] = value;
        }

        // --- OCR (Tesseract) ---

        // Chosen OCR languages as a '+'-joined list of Tesseract codes (e.g. "eng" or "eng+spa").
        // English is the floor; language data is downloaded on demand into the tessdata folder.
        [UserScopedSetting]
        [DefaultSettingValue("eng")]
        public string OcrLanguages
        {
            get => (string)this[nameof(OcrLanguages)];
            set => this[nameof(OcrLanguages)] = value;
        }

        // When true, OCR downloads pull the larger, more accurate tessdata_best models instead of the
        // smaller tessdata_fast ones.
        [UserScopedSetting]
        [DefaultSettingValue("False")]
        public bool OcrHighQuality
        {
            get => (bool)this[nameof(OcrHighQuality)];
            set => this[nameof(OcrHighQuality)] = value;
        }

        // Tracks which installed languages currently hold the high-quality (best) model, as a '+'-joined
        // list of codes, so toggling HQ off then on doesn't re-download ones that are already HQ.
        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string OcrHqLanguages
        {
            get => (string)this[nameof(OcrHqLanguages)];
            set => this[nameof(OcrHqLanguages)] = value;
        }
    }
}
