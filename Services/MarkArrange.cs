namespace TDPdf.Services
{
    /// <summary>
    /// Pure geometry for placing several marks on a page: where a pasted copy lands, and (further
    /// down) how selected marks line up. Boxes are plain (X, Y, Width, Height) tuples in canvas DIPs
    /// so the whole of it can be tested without WPF.
    /// </summary>
    internal static class MarkArrange
    {
        /// <summary>How far each repeated paste moves from the original, in canvas DIPs.</summary>
        public const double PasteStep = 12;

        /// <summary>
        /// The shift (dx, dy) that puts a pasted group at <paramref name="step"/> offsets from its
        /// source and keeps it on the page. <paramref name="box"/> is the group's bounding box as
        /// copied; the page is (0, 0) to (<paramref name="pageW"/>, <paramref name="pageH"/>).
        /// A group wider or taller than the page cannot fit, and is pinned to the top-left instead.
        /// Anything non-finite shifts nothing.
        /// </summary>
        public static (double Dx, double Dy) PasteShift(
            (double X, double Y, double W, double H) box, double pageW, double pageH, int step)
        {
            if (!Finite(box.X) || !Finite(box.Y) || !Finite(box.W) || !Finite(box.H) || step < 1)
                return (0, 0);
            double want = PasteStep * step;
            return (ClampAxis(box.X, box.W, pageW, want), ClampAxis(box.Y, box.H, pageH, want));
        }

        private static double ClampAxis(double pos, double size, double page, double want)
        {
            if (!Finite(page) || page <= 0) return want;
            double target = pos + want;
            double max = page - size;           // furthest the near edge may sit
            if (target > max) target = max;
            if (target < 0) target = 0;         // too big for the page: pin to the edge
            return target - pos;
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}

namespace TDPdf.Services
{
    /// <summary>Which marks a click on one member of a group picks up (#227), without WPF.</summary>
    internal static class MarkGroups
    {
        /// <summary>
        /// The indices to select when the marks at <paramref name="picked"/> are clicked: the picked
        /// ones first, in the order given, then every other index that shares a group id with one of
        /// them, in list order. A null id is "not grouped" and never matches another null.
        /// </summary>
        public static List<int> Expand(IReadOnlyList<string?> groupIds, IEnumerable<int> picked)
        {
            var result = new List<int>();
            var ids = new HashSet<string>();
            foreach (int i in picked)
            {
                if (i < 0 || i >= groupIds.Count || result.Contains(i)) continue;
                result.Add(i);
                if (groupIds[i] is { } id) ids.Add(id);
            }
            if (ids.Count == 0) return result;
            for (int i = 0; i < groupIds.Count; i++)
                if (groupIds[i] is { } id && ids.Contains(id) && !result.Contains(i)) result.Add(i);
            return result;
        }
    }
}
