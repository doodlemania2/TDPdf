using System.IO;
using System.Text;

namespace TDPdf.Services
{
    /// <summary>
    /// Everything about an attached file's NAME that decides whether it is safe to write to disk:
    /// a name a PDF supplies is hostile input. Pure, WPF-free, covered by tests/PdfCore.
    /// </summary>
    internal static class AttachmentNames
    {
        /// <summary>Longest file name (with extension) that is written.</summary>
        internal const int MaxNameLength = 120;

        private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
            "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
            "CONIN$", "CONOUT$",
        };

        /// <summary>Programs, scripts, shortcuts and macro-carrying documents: warned about, never opened.</summary>
        private static readonly HashSet<string> RiskyExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "exe", "dll", "bat", "cmd", "com", "ps1", "psm1", "psd1", "ps1xml", "vbs", "vbe", "js", "jse", "wsf", "wsh", "wsc", "ws",
            "msi", "msp", "mst", "lnk", "scr", "pif", "cpl", "hta", "jar", "reg", "url", "appref-ms", "application", "gadget",
            "inf", "ins", "isp", "job", "ocx", "sys", "scf", "sct", "msc", "chm", "jnlp", "vb", "xnk", "diagcab", "settingcontent-ms",
            "msix", "msixbundle", "appx", "appxbundle", "appinstaller", "iso", "img", "vhd", "vhdx", "sh", "py", "pl", "rb",
            "docm", "dotm", "xlsm", "xltm", "xlam", "xlsb", "pptm", "potm", "ppam", "ppsm", "sldm", "xll", "wll", "slk", "iqy", "hlp",
            "bin", "run", "apk", "dmg", "pkg", "app",
        };

        /// <summary>
        /// A name that can be written as a plain file in a chosen folder. Path parts are dropped
        /// (only the last segment survives), characters Windows forbids and ':' (which would open an
        /// alternate data stream) become '_', trailing dots and spaces go, and a reserved device
        /// name (CON, NUL, COM1...; with any extension) gets a '_' in front. Never empty, never
        /// longer than <see cref="MaxNameLength"/>.
        /// </summary>
        /// <returns>The safe name and whether it differs from <paramref name="raw"/>.</returns>
        internal static (string Name, bool Changed) Sanitise(string? raw)
        {
            string input = raw ?? string.Empty;
            string s = input;

            int cut = s.LastIndexOfAny(['/', '\\']);
            if (cut >= 0) s = s[(cut + 1)..];

            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c < 0x20 || c == 0x7F || c == '<' || c == '>' || c == ':' || c == '"' || c == '|' || c == '?' || c == '*'
                    || (c >= 0x200B && c <= 0x200F) || (c >= 0x202A && c <= 0x202E) || (c >= 0x2066 && c <= 0x2069) || c == 0xFEFF)
                    sb.Append('_');
                else
                    sb.Append(c);
            }
            s = sb.ToString().TrimStart(' ').TrimEnd('.', ' ');

            if (s.Length == 0 || s.All(ch => ch == '_' || ch == '.')) s = "attachment";

            // Device names are reserved with or without an extension ("nul.txt" opens the NUL device).
            int dot = s.IndexOf('.');
            string stem = dot >= 0 ? s[..dot] : s;
            if (Reserved.Contains(stem.TrimEnd(' '))) s = "_" + s;

            if (s.Length > MaxNameLength)
            {
                string ext = Path.GetExtension(s);
                if (ext.Length > 16) ext = ext[..16];
                s = s[..(MaxNameLength - ext.Length)].TrimEnd('.', ' ') + ext;
            }
            return (s, !string.Equals(s, input, StringComparison.Ordinal));
        }

        /// <summary>The extension without the dot, lower case, of the name as it would be saved.</summary>
        internal static string ExtensionOf(string? name)
        {
            string safe = Sanitise(name).Name;
            string ext = Path.GetExtension(safe);
            return ext.Length > 1 ? ext[1..].ToLowerInvariant() : string.Empty;
        }

        /// <summary>True for a program, script, shortcut, disk image or macro-carrying document.</summary>
        internal static bool IsRisky(string? name) => RiskyExtensions.Contains(ExtensionOf(name));

        /// <summary>
        /// A name no file in the folder has yet: <c>report.txt</c> becomes <c>report (2).txt</c>,
        /// <c>report (3).txt</c>... <paramref name="exists"/> answers for one candidate name.
        /// </summary>
        internal static string Unique(string name, Func<string, bool> exists)
        {
            if (!exists(name)) return name;
            string ext = Path.GetExtension(name);
            string stem = name[..^ext.Length];
            for (int n = 2; n < 10_000; n++)
            {
                string candidate = $"{stem} ({n}){ext}";
                if (!exists(candidate)) return candidate;
            }
            return $"{stem} ({Guid.NewGuid():N}){ext}";
        }

        /// <summary>
        /// Content of the NTFS <c>Zone.Identifier</c> stream that marks a file as downloaded
        /// (ZoneId 3, the Internet zone), so SmartScreen and Office Protected View treat it so.
        /// </summary>
        internal static string ZoneIdentifierText() => "[ZoneTransfer]\r\nZoneId=3\r\n";

        /// <summary>
        /// Marks <paramref name="path"/> as downloaded. Windows and NTFS only: anywhere else (or on a
        /// volume with no alternate streams) it returns false and the file is left as written.
        /// </summary>
        internal static bool TryMarkDownloaded(string path)
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                File.WriteAllText(path + ":Zone.Identifier", ZoneIdentifierText(), Encoding.ASCII);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
