namespace TDPdf.Services
{
    /// <summary>Which half of the work area the window being split keeps.</summary>
    public enum SplitSide { Left, Right }

    /// <summary>How the two windows of a split view are laid out.</summary>
    public enum SplitAxis
    {
        /// <summary>One window left, one right.</summary>
        SideBySide,
        /// <summary>One above the other, used only when the monitor is too narrow for two windows.</summary>
        Stacked
    }

    /// <summary>Where the window being split ends up and where its partner opens.</summary>
    public readonly record struct SplitPlan(WindowPlacementMath.Box This, WindowPlacementMath.Box Partner, SplitAxis Axis);

    /// <summary>
    /// Pure layout for split view (two TDPdf windows snapped to halves of one monitor). Values are
    /// DIPs and the monitors' work areas are passed in, so tests/PdfCore can pin the rules without a
    /// screen.
    /// </summary>
    public static class SplitLayout
    {
        /// <summary>
        /// Splits the work area of the monitor the window is mostly on. Side by side when both halves
        /// can be at least <paramref name="minWidth"/> wide; otherwise stacked top and bottom when both
        /// can be at least <paramref name="minHeight"/> tall; otherwise null (no room for two windows).
        /// The halves tile the work area exactly, with no gap and no overlap.
        /// </summary>
        public static SplitPlan? Compute(WindowPlacementMath.Box window, IReadOnlyList<WindowPlacementMath.Box> workAreas,
            double minWidth, double minHeight, SplitSide thisSide = SplitSide.Left)
        {
            if (workAreas.Count == 0) return null;
            if (!double.IsFinite(minWidth) || !double.IsFinite(minHeight)) return null;
            var area = PickArea(window, workAreas);
            if (!(area.Width > 0) || !(area.Height > 0)) return null;

            if (area.Width >= 2 * minWidth)
            {
                double leftW = Math.Floor(area.Width / 2);
                var left = new WindowPlacementMath.Box(area.X, area.Y, leftW, area.Height);
                var right = new WindowPlacementMath.Box(area.X + leftW, area.Y, area.Width - leftW, area.Height);
                return thisSide == SplitSide.Left
                    ? new SplitPlan(left, right, SplitAxis.SideBySide)
                    : new SplitPlan(right, left, SplitAxis.SideBySide);
            }
            if (area.Height >= 2 * minHeight)
            {
                double topH = Math.Floor(area.Height / 2);
                var top = new WindowPlacementMath.Box(area.X, area.Y, area.Width, topH);
                var bottom = new WindowPlacementMath.Box(area.X, area.Y + topH, area.Width, area.Height - topH);
                return thisSide == SplitSide.Left
                    ? new SplitPlan(top, bottom, SplitAxis.Stacked)
                    : new SplitPlan(bottom, top, SplitAxis.Stacked);
            }
            return null;
        }

        /// <summary>The work area with the largest overlap with the window; the first one when the window is off every monitor.</summary>
        private static WindowPlacementMath.Box PickArea(WindowPlacementMath.Box window, IReadOnlyList<WindowPlacementMath.Box> areas)
        {
            var best = areas[0];
            double bestOverlap = -1;
            foreach (var a in areas)
            {
                double w = Math.Min(window.X + window.Width, a.X + a.Width) - Math.Max(window.X, a.X);
                double h = Math.Min(window.Y + window.Height, a.Y + a.Height) - Math.Max(window.Y, a.Y);
                double o = w > 0 && h > 0 ? w * h : 0;
                if (o > bestOverlap) { bestOverlap = o; best = a; }
            }
            return best;
        }
    }
}
