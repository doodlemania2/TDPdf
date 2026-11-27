namespace TDPdf.Services
{
    /// <summary>
    /// The command-line form of "open this document in a separate window": <c>--new-window</c>,
    /// <c>--read-only</c> and <c>--window-bounds=L,T,W,H</c>, in any order, plus the document path.
    /// Used by the tab tear-off, split view and a direct launch with the same switches. Pure, so
    /// tests/PdfCore can pin it.
    /// </summary>
    public sealed record WindowLaunch(bool NewWindow, bool ReadOnly, string? Path, WindowPlacementMath.Box? Bounds)
    {
        public const string NewWindowSwitch = "--new-window";
        public const string ReadOnlySwitch = "--read-only";

        /// <summary>
        /// A launch that must not be folded into a running window: a tear-off is separate by design,
        /// and a read-only launch would otherwise lose its flag when forwarded to the first window.
        /// </summary>
        public bool SeparateWindow => NewWindow || ReadOnly;

        /// <summary>
        /// Reads the switches from <paramref name="args"/> (the arguments after the exe path). Switches
        /// are exact, case-insensitive matches; an unknown <c>--</c> switch is ignored; the first other
        /// argument is the path; a malformed or implausible <c>--window-bounds</c> is dropped.
        /// </summary>
        public static WindowLaunch Parse(IEnumerable<string> args)
        {
            var list = args.Where(a => a is not null).ToList();
            bool newWindow = false, readOnly = false;
            string? path = null;
            foreach (var a in list)
            {
                if (string.Equals(a, NewWindowSwitch, StringComparison.OrdinalIgnoreCase)) newWindow = true;
                else if (string.Equals(a, ReadOnlySwitch, StringComparison.OrdinalIgnoreCase)) readOnly = true;
                else if (a.StartsWith("--", StringComparison.Ordinal)) { /* bounds below; anything else is ignored */ }
                else if (path is null && a.Length > 0) path = a;
            }
            WindowPlacementMath.Box? bounds = WindowPlacementMath.TryParseBoundsArg(list, out var b) ? b : null;
            return new WindowLaunch(newWindow, readOnly, path, bounds);
        }

        /// <summary>The argument list that starts a separate window on <paramref name="path"/>.</summary>
        public static List<string> BuildArgs(string path, bool readOnly, WindowPlacementMath.Box? bounds)
        {
            var args = new List<string> { NewWindowSwitch };
            if (readOnly) args.Add(ReadOnlySwitch);
            args.Add(path);
            if (bounds is { } b) args.Add(WindowPlacementMath.FormatBoundsArg(b));
            return args;
        }
    }
}
