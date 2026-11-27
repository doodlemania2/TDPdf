namespace TDPdf.Services
{
    /// <summary>
    /// What a read-only window ("Read-only view", the second window of a split view on a document
    /// the first window is editing) may do. Safe by default: a menu command, tool or key chord that
    /// is not listed here is refused, so a command added later is blocked until someone decides it
    /// is harmless. Pure, so tests/PdfCore can pin the table.
    /// </summary>
    public static class ReadOnlyGate
    {
        /// <summary>
        /// True for the whole process once it was started read-only. A window is a process, so this is
        /// per window. Set once at startup by MainWindow.
        /// </summary>
        public static bool ProcessIsReadOnly { get; set; }

        /// <summary>Tools that only look: nothing that adds, changes or removes content.</summary>
        private static readonly HashSet<string> AllowedTools = new(StringComparer.Ordinal) { "Select", "Pan" };

        public static bool IsToolAllowed(string toolName) => AllowedTools.Contains(toolName);

        /// <summary>Menu headers (normalised) that may stay enabled in a read-only window. Literal file names always may.</summary>
        private static readonly HashSet<string> AllowedHeaders = new(StringComparer.Ordinal)
        {
            // File
            "open", "close", "reopen closed tab", "print", "export pages as images", "export tables to csv",
            "extract pages", "document info", "attachments", "exit", "electronic signature", "verify signatures",
            // Edit
            "find", "copy", "select all",
            // Tools
            "select", "pan / hand", "compare pdfs", "settings",
            // Context menus
            "copy url", "copy email address", "close tab", "close other tabs", "move to new window",
            "open containing folder", "open in a new window", "open in new window",
            // Split view
            "split view", "view side by side", "view side by side (read-only)", "end split view", "close split view",
        };

        /// <summary>Menu top-level headers whose whole submenu is view-only and never gated.</summary>
        public static bool IsViewOnlyMenu(string header)
        {
            string h = Normalize(header);
            return h == "view" || h == "help";
        }

        /// <summary>Strips the access-key underscores and the ellipsis so "Save _As…" and "Save As" compare equal.</summary>
        public static string Normalize(string? header)
        {
            if (string.IsNullOrWhiteSpace(header)) return "";
            return header.Replace("_", "").Replace("…", "").Replace("...", "").Trim().ToLowerInvariant();
        }

        public static bool IsMenuCommandAllowed(string? header) => AllowedHeaders.Contains(Normalize(header));

        /// <summary>
        /// True for a key chord that edits the document or the file. <paramref name="key"/> is the
        /// WPF <c>Key</c> name. Chords that only look (zoom, find, copy, navigation) are not listed.
        /// </summary>
        public static bool IsChordBlocked(string key, bool ctrl, bool shift, bool alt)
        {
            if (alt) return false;
            if (!ctrl) return key is "Delete" or "Back";
            if (!shift) return key is "X" or "V" or "Z" or "Y" or "S" or "N" or "R";
            return key is "S" or "Z" or "R" or "U" or "O";
        }

        /// <summary>The line shown when something is refused.</summary>
        public const string RefusedStatus = "This window is a read-only view. Make changes in the other window.";

        /// <summary>
        /// The delay before reloading after the file changed on disk: a save writes in a few steps, so
        /// the window waits for them to settle.
        /// </summary>
        public const int ReloadDebounceMs = 500;

        /// <summary>How many times a reload that finds the file locked or half-written is retried.</summary>
        public const int ReloadMaxRetries = 20;
    }
}
