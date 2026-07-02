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
    }
}
