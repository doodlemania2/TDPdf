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

        [UserScopedSetting]
        [DefaultSettingValue("1")]
        public double LastZoomLevel
        {
            get => (double)this[nameof(LastZoomLevel)];
            set => this[nameof(LastZoomLevel)] = value;
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
