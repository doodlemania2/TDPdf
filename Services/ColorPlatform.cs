using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TDPdf.Services
{
    /// <summary>
    /// The Windows side of colour management: which ICC profile Windows reports for the monitor a window
    /// is on, whether that monitor is in Advanced Color (HDR) mode, and the profiles associated with a
    /// printer. Everything is best-effort: any failure reads as "no profile", which means no transform.
    /// </summary>
    internal static class ColorPlatform
    {
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int MaxProfileBytes = 64 * 1024 * 1024;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public int left, top, right, bottom, wleft, wtop, wright, wbottom;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ENUMTYPEW
        {
            public uint dwSize, dwVersion, dwFields;
            public IntPtr pDeviceName;
            public uint dwMediaType, dwDitheringMode, dwResolution0, dwResolution1, dwCMMType, dwClass,
                        dwDataColorSpace, dwConnectionSpace, dwSignature, dwPlatform, dwProfileFlags,
                        dwManufacturer, dwModel, dwAttributes0, dwAttributes1, dwRenderingIntent, dwCreator, dwDeviceClass;
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX info);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DISPLAY_DEVICE dd, uint flags);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateDCW(string driver, string device, string? output, IntPtr initData);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetICMProfileW(IntPtr hdc, ref uint size, StringBuilder? name);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
        private static extern bool WcsGetDefaultColorProfileSize(int scope, string? device, int type, int subType, uint id, out uint bytes);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
        private static extern bool WcsGetDefaultColorProfile(int scope, string? device, int type, int subType, uint id, uint bytes, StringBuilder name);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumColorProfilesW(string? machine, ref ENUMTYPEW type, byte[]? buffer, ref uint size, out uint count);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint paths, byte[] pathInfo, ref uint modes, byte[] modeInfo, IntPtr topology);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(IntPtr packet);

        private static string ColorDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color");

        /// <summary>The handle of the monitor <paramref name="hwnd"/> is on, a cheap check for "did the window change monitor".</summary>
        public static long MonitorOf(IntPtr hwnd)
        {
            try { return MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST).ToInt64(); }
            catch { return 0; }
        }

        /// <summary>
        /// What Windows reports for the monitor <paramref name="hwnd"/> is on. The profile path is null
        /// when Windows reports none.
        /// </summary>
        public static MonitorColorState ReadMonitor(IntPtr hwnd)
        {
            long id = 0;
            string? path = null;
            bool advanced = false;
            try
            {
                IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                id = mon.ToInt64();
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
                if (mon != IntPtr.Zero && GetMonitorInfoW(mon, ref info))
                {
                    path = ProfileFromDc(info.szDevice) ?? ProfileFromWcs(info.szDevice);
                    advanced = IsAdvancedColor(info.szDevice);
                }
            }
            catch { /* no profile, no transform */ }
            string? profileId = null;
            if (path is not null)
            {
                try
                {
                    var fi = new FileInfo(path);
                    profileId = fi.Exists ? $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}" : null;
                    if (profileId is null) path = null;
                }
                catch { path = null; }
            }
            return new MonitorColorState(id, path, profileId, advanced);
        }

        private static string? Qualify(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string p = name.Trim();
            return Path.IsPathRooted(p) ? p : Path.Combine(ColorDirectory, p);
        }

        private static string? ProfileFromDc(string device)
        {
            IntPtr dc = IntPtr.Zero;
            try
            {
                dc = CreateDCW("DISPLAY", device, null, IntPtr.Zero);
                if (dc == IntPtr.Zero) return null;
                uint size = 0;
                GetICMProfileW(dc, ref size, null);
                if (size == 0 || size > 4096) return null;
                var sb = new StringBuilder((int)size);
                return GetICMProfileW(dc, ref size, sb) ? Qualify(sb.ToString()) : null;
            }
            catch { return null; }
            finally { if (dc != IntPtr.Zero) DeleteDC(dc); }
        }

        private static string? ProfileFromWcs(string device)
        {
            try
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>(), DeviceName = "", DeviceString = "", DeviceID = "", DeviceKey = "" };
                if (!EnumDisplayDevicesW(device, 0, ref dd, 0) || string.IsNullOrEmpty(dd.DeviceID)) return null;
                // Scope 1: the current user's association, which is what Colour Management shows.
                const int CurrentUser = 1, Icc = 0, None = 4;
                if (!WcsGetDefaultColorProfileSize(CurrentUser, dd.DeviceID, Icc, None, 0, out uint bytes) || bytes < 2 || bytes > 8192)
                    return null;
                var sb = new StringBuilder((int)bytes / 2 + 1);
                return WcsGetDefaultColorProfile(CurrentUser, dd.DeviceID, Icc, None, 0, bytes, sb) ? Qualify(sb.ToString()) : null;
            }
            catch { return null; }
        }

        /// <summary>True when the display behind GDI device <paramref name="gdiDevice"/> has Advanced Color (HDR) switched on.</summary>
        private static bool IsAdvancedColor(string gdiDevice)
        {
            const uint QDC_ONLY_ACTIVE_PATHS = 2;
            const int PathSize = 72, ModeSize = 64;
            try
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pn, out uint mn) != 0 || pn == 0 || pn > 64) return false;
                var paths = new byte[pn * PathSize];
                var modes = new byte[mn * ModeSize];
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pn, paths, ref mn, modes, IntPtr.Zero) != 0) return false;
                for (int i = 0; i < pn; i++)
                {
                    int o = i * PathSize;
                    // Source name request (type 1): adapter LUID at +0, source id at +8.
                    string? name = SourceName(paths, o);
                    if (!string.Equals(name, gdiDevice, StringComparison.OrdinalIgnoreCase)) continue;
                    // Advanced colour request (type 9) against the target: adapter at +20, id at +28.
                    return TargetAdvancedColorEnabled(paths, o + 20);
                }
            }
            catch { /* unknown reads as not HDR */ }
            return false;
        }

        private static string? SourceName(byte[] paths, int o)
        {
            const int Size = 20 + 64;
            IntPtr p = Marshal.AllocHGlobal(Size);
            try
            {
                var buf = new byte[Size];
                BitConverter.GetBytes(1).CopyTo(buf, 0);        // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                BitConverter.GetBytes((uint)Size).CopyTo(buf, 4);
                Array.Copy(paths, o, buf, 8, 8);                // adapterId
                Array.Copy(paths, o + 8, buf, 16, 4);           // id
                Marshal.Copy(buf, 0, p, Size);
                if (DisplayConfigGetDeviceInfo(p) != 0) return null;
                Marshal.Copy(p, buf, 0, Size);
                return Encoding.Unicode.GetString(buf, 20, 64).TrimEnd('\0');
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        private static bool TargetAdvancedColorEnabled(byte[] paths, int o)
        {
            const int Size = 20 + 12;
            IntPtr p = Marshal.AllocHGlobal(Size);
            try
            {
                var buf = new byte[Size];
                BitConverter.GetBytes(9).CopyTo(buf, 0);        // DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO
                BitConverter.GetBytes((uint)Size).CopyTo(buf, 4);
                Array.Copy(paths, o, buf, 8, 8);
                Array.Copy(paths, o + 8, buf, 16, 4);
                Marshal.Copy(buf, 0, p, Size);
                if (DisplayConfigGetDeviceInfo(p) != 0) return false;
                Marshal.Copy(p, buf, 0, Size);
                uint value = BitConverter.ToUInt32(buf, 20);
                return (value & 0x2) != 0;                      // advancedColorEnabled
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        /// <summary>Reads and parses an ICC file, refusing anything oversized. Null when unreadable.</summary>
        public static IccProfile? LoadProfile(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return null;
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < 132 || fi.Length > MaxProfileBytes) return null;
                return IccProfile.Parse(File.ReadAllBytes(path));
            }
            catch { return null; }
        }

        /// <summary>The ICC profiles Windows has associated with the printer named <paramref name="printer"/>.</summary>
        public static IReadOnlyList<string> PrinterProfiles(string? printer)
        {
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(printer)) return found;
            IntPtr name = IntPtr.Zero;
            try
            {
                name = Marshal.StringToHGlobalUni(printer);
                var t = new ENUMTYPEW
                {
                    dwSize = (uint)Marshal.SizeOf<ENUMTYPEW>(),
                    dwVersion = 0x0300,
                    dwFields = 0x1,   // ET_DEVICENAME
                    pDeviceName = name,
                };
                uint size = 0;
                EnumColorProfilesW(null, ref t, null, ref size, out _);
                if (size < 4 || size > 1 << 20) return found;
                var buf = new byte[size];
                if (!EnumColorProfilesW(null, ref t, buf, ref size, out _)) return found;
                foreach (string s in Encoding.Unicode.GetString(buf, 0, (int)size).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                    if (Qualify(s) is { } full && File.Exists(full)) found.Add(full);
            }
            catch { /* none found: the user can still browse */ }
            finally { if (name != IntPtr.Zero) Marshal.FreeHGlobal(name); }
            return found;
        }
    }

    /// <summary>
    /// Keeps <see cref="DisplayColor"/> in step with the monitor the main window is on. Call
    /// <see cref="Refresh"/> at startup, on WM_DISPLAYCHANGE, on a DPI change and when the window moves
    /// to another monitor; it re-reads what Windows reports and rebuilds the transform only when the
    /// monitor, the profile file or the setting actually changed.
    /// </summary>
    internal sealed class MonitorColorController
    {
        private MonitorColorState? _last;
        private bool _lastSetting;

        public MonitorColorDecision Decision { get; private set; } = MonitorColorDecision.SettingOff;
        public string? ProfileName { get; private set; }
        public string? TransformProblem { get; private set; }

        /// <summary>True when the screen's colours changed, so visible pages need redrawing.</summary>
        public bool Refresh(IntPtr hwnd, bool settingOn)
        {
            var now = settingOn ? ColorPlatform.ReadMonitor(hwnd) : new MonitorColorState(0, null, null, false);
            if (_last is not null && _lastSetting == settingOn && !ColorMonitorPolicy.NeedsRefresh(_last, now)) return false;
            _last = now;
            _lastSetting = settingOn;

            bool wasActive = DisplayColor.IsActive;
            var profile = ColorPlatform.LoadProfile(now.ProfilePath);
            Decision = ColorMonitorPolicy.Decide(settingOn, now.AdvancedColor, now.ProfilePath, profile);
            ProfileName = profile is null ? null : (string.IsNullOrWhiteSpace(profile.Description) ? Path.GetFileName(now.ProfilePath) : profile.Description);
            TransformProblem = null;

            IColorTransform? transform = null;
            if (Decision == MonitorColorDecision.Match && profile is not null)
            {
                transform = ColorTransformCache.Get(ColorEngines.Current, IccProfile.Srgb, profile,
                                                   ColorIntent.RelativeColorimetric, bpc: false, out string? why);
                TransformProblem = why;
            }
            DisplayColor.Set(transform);
            return wasActive || DisplayColor.IsActive;
        }

        public string Summary()
        {
            if (Decision == MonitorColorDecision.Match && !DisplayColor.IsActive)
                return $"This monitor's profile{(string.IsNullOrWhiteSpace(ProfileName) ? "" : " (" + ProfileName + ")")} could not be used: "
                       + (TransformProblem ?? "the conversion could not be built.") + " Pages are shown as plain sRGB.";
            return ColorMonitorPolicy.Describe(Decision, ProfileName, TransformProblem);
        }
    }
}
