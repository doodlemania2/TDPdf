using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using TDPdf.Services;
using static TDPdf.Services.WindowPlacementMath;

namespace TDPdf
{
    // Window placement: remember the main window's position, size and maximized state across
    // launches, and open a torn-off tab's window beside its source instead of on top of it. The
    // rules live in Services/WindowPlacementMath.cs; this file only gathers monitors and applies them.
    public partial class MainWindow
    {
        /// <summary>Tallest a menu may be (DIPs): the work area less room for its border and shadow. Used by the menu templates in MainWindow.xaml so a long menu scrolls.</summary>
        public static double MenuMaxHeight => Math.Max(200, SystemParameters.WorkArea.Height - 24);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        /// <summary>True once the constructor placed the window, so FitToStartupMonitor must not re-centre it.</summary>
        private bool _placementApplied;
        /// <summary>A tear-off window does not own the saved placement: it would overwrite the main window's with its cascaded one.</summary>
        private bool _isTearOffWindow;

        /// <summary>Every monitor's work area in DIPs, using the system DPI scale (the window has no handle yet).</summary>
        private List<Box> MonitorWorkAreasDip()
        {
            double sx = 1.0;
            try { sx = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
            if (!(sx > 0)) sx = 1.0;
            var areas = new List<Box>();
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr hdc, ref RECT rc, IntPtr data) =>
                {
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    if (GetMonitorInfo(mon, ref info))
                    {
                        var w = RectToDip(info.rcWork, sx, sx);
                        areas.Add(new Box(w.X, w.Y, w.Width, w.Height));
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { /* best-effort: no monitors known means the default centring applies */ }
            return areas;
        }

        /// <summary>
        /// Called from the constructor, before the window is first shown. A tear-off window takes the
        /// rectangle its source passed on the command line; anything else takes the saved placement.
        /// If neither yields a rectangle that is reachable on a current monitor, nothing changes and
        /// the XAML's CenterScreen applies.
        /// </summary>
        private void ApplyStartupPlacement()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                _isTearOffWindow = args.Length > 1 && string.Equals(args[1], "--new-window", StringComparison.OrdinalIgnoreCase);
                var areas = MonitorWorkAreasDip();
                if (areas.Count == 0) return;

                Box? target = null;
                bool maximize = false;
                if (_isTearOffWindow)
                {
                    // Never maximized: the point is a window you can see next to the one you tore from.
                    if (TryParseBoundsArg(args.Skip(2), out var requested))
                        target = Restore(requested, areas, MinWidth, MinHeight);
                }
                else
                {
                    var s = TDPdf.Properties.Settings.Default;
                    if (s.WindowPlacementSaved)
                    {
                        target = Restore(new Box(s.WindowLeft, s.WindowTop, s.WindowWidth, s.WindowHeight), areas, MinWidth, MinHeight);
                        maximize = target is not null && s.WindowMaximized;
                    }
                }
                if (target is not Box b) return;

                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = b.X; Top = b.Y; Width = b.Width; Height = b.Height;
                // Setting Maximized before the first show makes the Left/Top/Width/Height above the
                // restore bounds; the WM_GETMINMAXINFO hook still clamps the maximized size.
                if (maximize) WindowState = WindowState.Maximized;
                _placementApplied = true;
            }
            catch { /* a window position is never worth failing startup over */ }
        }

        /// <summary>Remembers where the window is for next launch. Not minimized, not full screen.</summary>
        private void SaveWindowPlacement()
        {
            if (_isTearOffWindow || _fullScreen || WindowState == WindowState.Minimized) return;
            try
            {
                Rect r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
                if (r.IsEmpty || !(r.Width > 0) || !(r.Height > 0)) return;
                var s = TDPdf.Properties.Settings.Default;
                s.WindowLeft = r.Left; s.WindowTop = r.Top;
                s.WindowWidth = r.Width; s.WindowHeight = r.Height;
                s.WindowMaximized = WindowState == WindowState.Maximized;
                s.WindowPlacementSaved = true;
                s.Save();
            }
            catch { /* non-critical user preference */ }
        }

        /// <summary>The <c>--window-bounds=</c> argument that places a torn-off tab's window beside this one, or null.</summary>
        private string? TearOffBoundsArg()
        {
            try
            {
                Rect r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
                if (r.IsEmpty) return null;
                var cascaded = Cascade(new Box(r.Left, r.Top, r.Width, r.Height), MonitorWorkAreasDip(), MinWidth, MinHeight);
                return cascaded is Box b ? FormatBoundsArg(b) : null;
            }
            catch { return null; }
        }
    }
}
