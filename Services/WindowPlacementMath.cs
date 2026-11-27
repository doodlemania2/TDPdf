using System.Globalization;

namespace TDPdf.Services
{
    /// <summary>
    /// Pure rules for remembering where the main window was and putting it back: clamping a saved
    /// rectangle onto a monitor that still exists, cascading a torn-off tab's new window beside its
    /// source, and the command-line form that carries a rectangle to that new process. All values
    /// are DIPs and the monitors' work areas are passed in, so tests/PdfCore can pin the rules
    /// without a screen.
    /// </summary>
    public static class WindowPlacementMath
    {
        /// <summary>Visible part of a saved window below which it counts as off-screen.</summary>
        public const double MinVisibleWidth = 100;
        public const double MinVisibleHeight = 50;
        /// <summary>How far a torn-off window sits from its source on each axis.</summary>
        public const double CascadeOffset = 32;
        /// <summary>Command-line switch carrying a window rectangle, as <c>--window-bounds=L,T,W,H</c>.</summary>
        public const string BoundsSwitch = "--window-bounds=";

        public readonly record struct Box(double X, double Y, double Width, double Height);

        private static double Overlap(Box a, Box b)
        {
            double w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
            double h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
            return w > 0 && h > 0 ? w * h : 0;
        }

        private static bool IsSane(Box b) =>
            double.IsFinite(b.X) && double.IsFinite(b.Y) && double.IsFinite(b.Width) && double.IsFinite(b.Height)
            && b.Width > 0 && b.Height > 0 && Math.Abs(b.X) < 1e6 && Math.Abs(b.Y) < 1e6 && b.Width < 1e5 && b.Height < 1e5;

        /// <summary>Shrinks to fit the area (but not below the minimums, unless the area is smaller) and slides inside it.</summary>
        private static Box ClampInto(Box w, Box area, double minWidth, double minHeight)
        {
            double width = Math.Min(w.Width, area.Width);
            double height = Math.Min(w.Height, area.Height);
            width = Math.Max(width, Math.Min(minWidth, area.Width));
            height = Math.Max(height, Math.Min(minHeight, area.Height));
            double x = Math.Clamp(w.X, area.X, area.X + area.Width - width);
            double y = Math.Clamp(w.Y, area.Y, area.Y + area.Height - height);
            return new Box(x, y, width, height);
        }

        /// <summary>
        /// Where a window saved at <paramref name="saved"/> should open now, or null when it would
        /// not be reachable on any current monitor (the caller then falls back to centring).
        /// </summary>
        public static Box? Restore(Box saved, IReadOnlyList<Box> workAreas, double minWidth, double minHeight)
        {
            if (!IsSane(saved)) return null;
            Box? best = null;
            double bestOverlap = 0;
            foreach (var area in workAreas)
            {
                double o = Overlap(saved, area);
                if (o > bestOverlap) { bestOverlap = o; best = area; }
            }
            if (best is not Box target) return null;
            double visW = Math.Min(saved.X + saved.Width, target.X + target.Width) - Math.Max(saved.X, target.X);
            double visH = Math.Min(saved.Y + saved.Height, target.Y + target.Height) - Math.Max(saved.Y, target.Y);
            if (visW < Math.Min(MinVisibleWidth, saved.Width) || visH < Math.Min(MinVisibleHeight, saved.Height)) return null;
            return ClampInto(saved, target, minWidth, minHeight);
        }

        /// <summary>
        /// The rectangle for a window opened from <paramref name="source"/>: the same size, nudged
        /// down and right by <see cref="CascadeOffset"/>, kept inside the work area the source is on.
        /// Null when there is no usable monitor.
        /// </summary>
        public static Box? Cascade(Box source, IReadOnlyList<Box> workAreas, double minWidth, double minHeight)
        {
            if (!IsSane(source) || workAreas.Count == 0) return null;
            Box area = workAreas[0];
            double best = -1;
            foreach (var a in workAreas)
            {
                double o = Overlap(source, a);
                if (o > best) { best = o; area = a; }
            }
            var moved = source with { X = source.X + CascadeOffset, Y = source.Y + CascadeOffset };
            // Past the right or bottom edge: start the cascade over from the area's top-left rather
            // than pinning the new window exactly on top of its source.
            if (moved.X + moved.Width > area.X + area.Width || moved.Y + moved.Height > area.Y + area.Height)
                moved = moved with { X = area.X + CascadeOffset, Y = area.Y + CascadeOffset };
            return ClampInto(moved, area, minWidth, minHeight);
        }

        public static string FormatBoundsArg(Box b) => BoundsSwitch + string.Join(",",
            new[] { b.X, b.Y, b.Width, b.Height }.Select(v => Math.Round(v).ToString("0", CultureInfo.InvariantCulture)));

        /// <summary>Reads the first <c>--window-bounds=L,T,W,H</c> among <paramref name="args"/>; false when absent or malformed.</summary>
        public static bool TryParseBoundsArg(IEnumerable<string> args, out Box bounds)
        {
            bounds = default;
            foreach (var arg in args)
            {
                if (!arg.StartsWith(BoundsSwitch, StringComparison.OrdinalIgnoreCase)) continue;
                var parts = arg.Substring(BoundsSwitch.Length).Split(',');
                if (parts.Length != 4) return false;
                var v = new double[4];
                for (int i = 0; i < 4; i++)
                    if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return false;
                var candidate = new Box(v[0], v[1], v[2], v[3]);
                if (!IsSane(candidate)) return false;
                bounds = candidate;
                return true;
            }
            return false;
        }
    }
}
