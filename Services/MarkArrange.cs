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

        // ── Align, distribute, same size (#248) ─────────────────────────────────────────

        public enum AlignEdge { Left, Center, Right, Top, Middle, Bottom }

        /// <summary>A box in canvas DIPs.</summary>
        public readonly record struct Box(double X, double Y, double W, double H)
        {
            public double Right => X + W;
            public double Bottom => Y + H;
        }

        private static Box Union(IReadOnlyList<Box> boxes)
        {
            double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
            foreach (var x in boxes)
            {
                l = Math.Min(l, x.X); t = Math.Min(t, x.Y);
                r = Math.Max(r, x.Right); b = Math.Max(b, x.Bottom);
            }
            return new Box(l, t, r - l, b - t);
        }

        /// <summary>
        /// How far each box moves (dx, dy, in input order) to line up on the selection's overall
        /// bounds: its left edge, centre, right edge, top, middle or bottom. Fewer than two boxes, or
        /// non-finite input, moves nothing.
        /// </summary>
        public static (double Dx, double Dy)[] Align(IReadOnlyList<Box> boxes, AlignEdge edge)
        {
            var shifts = new (double, double)[boxes.Count];
            if (boxes.Count < 2 || !AllFinite(boxes)) return shifts;
            var u = Union(boxes);
            for (int i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                shifts[i] = edge switch
                {
                    AlignEdge.Left => (u.X - b.X, 0),
                    AlignEdge.Center => (u.X + u.W / 2 - (b.X + b.W / 2), 0),
                    AlignEdge.Right => (u.Right - b.Right, 0),
                    AlignEdge.Top => (0, u.Y - b.Y),
                    AlignEdge.Middle => (0, u.Y + u.H / 2 - (b.Y + b.H / 2)),
                    _ => (0, u.Bottom - b.Bottom),
                };
            }
            return shifts;
        }

        /// <summary>
        /// How far each box moves so the gaps between neighbours are equal, with the two outermost boxes
        /// staying where they are. Needs three boxes or more; fewer moves nothing. "Neighbours" are
        /// taken in left-to-right (or top-to-bottom) order of their near edges.
        /// </summary>
        public static (double Dx, double Dy)[] Distribute(IReadOnlyList<Box> boxes, bool horizontal)
        {
            var shifts = new (double, double)[boxes.Count];
            if (boxes.Count < 3 || !AllFinite(boxes)) return shifts;

            var order = Enumerable.Range(0, boxes.Count)
                .OrderBy(i => horizontal ? boxes[i].X : boxes[i].Y).ThenBy(i => i).ToList();
            var u = Union(boxes);
            double span = horizontal ? u.W : u.H;
            double sizes = order.Sum(i => horizontal ? boxes[i].W : boxes[i].H);
            double gap = (span - sizes) / (boxes.Count - 1);

            double cursor = horizontal ? u.X : u.Y;
            foreach (int i in order)
            {
                double now = horizontal ? boxes[i].X : boxes[i].Y;
                double d = cursor - now;
                shifts[i] = horizontal ? (d, 0) : (0, d);
                cursor += (horizontal ? boxes[i].W : boxes[i].H) + gap;
            }
            return shifts;
        }

        /// <summary>
        /// The width (or height) each box should end up with so they all match the FIRST box, or null
        /// for a box that must not change: one that cannot be resized, or that already matches. Fewer
        /// than two boxes, or a first box that is not a usable size, changes nothing.
        /// </summary>
        public static double?[] MatchSize(IReadOnlyList<Box> boxes, IReadOnlyList<bool> resizable, bool width)
        {
            var targets = new double?[boxes.Count];
            if (boxes.Count < 2 || resizable.Count != boxes.Count || !AllFinite(boxes)) return targets;
            double want = width ? boxes[0].W : boxes[0].H;
            if (!(want > 0)) return targets;
            for (int i = 1; i < boxes.Count; i++)
            {
                double have = width ? boxes[i].W : boxes[i].H;
                if (resizable[i] && Math.Abs(have - want) > 1e-6) targets[i] = want;
            }
            return targets;
        }

        private static bool AllFinite(IReadOnlyList<Box> boxes) =>
            boxes.All(b => Finite(b.X) && Finite(b.Y) && Finite(b.W) && Finite(b.H));

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
