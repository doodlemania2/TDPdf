using TDPdf.Services;

/// <summary>
/// Pins <see cref="MarkArrange"/>: where a pasted copy lands (#226), and how selected marks line up
/// (#248). Pure geometry, no WPF.
/// </summary>
internal static class MarkArrangeTests
{
    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

    public static void Run(Action<string, bool, string> Check)
    {
        // ── Paste offset ────────────────────────────────────────────────────────────────
        var first = MarkArrange.PasteShift((100, 100, 50, 20), 600, 800, 1);
        Check("paste: first paste is 12 DIP down and right", first == (12, 12), $"{first}");

        var third = MarkArrange.PasteShift((100, 100, 50, 20), 600, 800, 3);
        Check("paste: each repeat adds another 12", third == (36, 36), $"{third}");

        var right = MarkArrange.PasteShift((540, 100, 50, 20), 600, 800, 1);
        Check("paste: clamped at the right edge, still offset down", Near(right.Dx, 10) && Near(right.Dy, 12), $"{right}");

        var corner = MarkArrange.PasteShift((550, 780, 50, 20), 600, 800, 1);
        Check("paste: a mark already in the corner stays on the page", corner == (0, 0), $"{corner}");

        var left = MarkArrange.PasteShift((-30, -5, 50, 20), 600, 800, 1);
        Check("paste: a mark hanging off the left is pulled back on", Near(left.Dx, 30) && Near(left.Dy, 12), $"{left}");

        var huge = MarkArrange.PasteShift((10, 10, 900, 900), 600, 800, 1);
        Check("paste: a group bigger than the page pins to the top-left", Near(huge.Dx, -10) && Near(huge.Dy, -10), $"{huge}");

        var nan = MarkArrange.PasteShift((double.NaN, 0, 5, 5), 600, 800, 1);
        Check("paste: nonsense input shifts nothing", nan == (0, 0), $"{nan}");

        var step0 = MarkArrange.PasteShift((10, 10, 5, 5), 600, 800, 0);
        Check("paste: step 0 shifts nothing", step0 == (0, 0), $"{step0}");

        // ── Align ───────────────────────────────────────────────────────────────────────
        var boxes = new List<MarkArrange.Box> { new(10, 10, 20, 20), new(50, 40, 40, 10), new(30, 100, 10, 30) };
        // union: x 10..90, y 10..130
        var aLeft = MarkArrange.Align(boxes, MarkArrange.AlignEdge.Left);
        Check("align left: every left edge lands on the selection's left", aLeft[0] == (0, 0) && aLeft[1] == (-40, 0) && aLeft[2] == (-20, 0), string.Join(" ", aLeft));
        var aRight = MarkArrange.Align(boxes, MarkArrange.AlignEdge.Right);
        Check("align right: every right edge lands on the selection's right", aRight[0] == (60, 0) && aRight[1] == (0, 0) && aRight[2] == (50, 0), string.Join(" ", aRight));
        var aCenter = MarkArrange.Align(boxes, MarkArrange.AlignEdge.Center);
        Check("align centre: centres meet at the middle of the selection", aCenter[0] == (30, 0) && aCenter[1] == (-20, 0) && aCenter[2] == (15, 0), string.Join(" ", aCenter));
        var aTop = MarkArrange.Align(boxes, MarkArrange.AlignEdge.Top);
        Check("align top: tops meet, nothing moves sideways", aTop[0] == (0, 0) && aTop[1] == (0, -30) && aTop[2] == (0, -90), string.Join(" ", aTop));
        var aBottom = MarkArrange.Align(boxes, MarkArrange.AlignEdge.Bottom);
        Check("align bottom: bottoms meet", aBottom[0] == (0, 100) && aBottom[1] == (0, 80) && aBottom[2] == (0, 0), string.Join(" ", aBottom));
        var aMiddle = MarkArrange.Align(boxes, MarkArrange.AlignEdge.Middle);
        Check("align middle: vertical centres meet", aMiddle[0] == (0, 50) && aMiddle[1] == (0, 25) && aMiddle[2] == (0, -45), string.Join(" ", aMiddle));
        Check("align: one box moves nothing", MarkArrange.Align(new[] { new MarkArrange.Box(5, 5, 5, 5) }, MarkArrange.AlignEdge.Left)[0] == (0, 0), "");
        Check("align: non-finite input moves nothing",
              MarkArrange.Align(new[] { new MarkArrange.Box(double.NaN, 0, 5, 5), new MarkArrange.Box(1, 1, 5, 5) }, MarkArrange.AlignEdge.Left).All(s => s == (0, 0)), "");

        // ── Distribute ──────────────────────────────────────────────────────────────────
        // Listed out of left-to-right order on purpose: shifts come back in INPUT order.
        var dist = new List<MarkArrange.Box> { new(80, 0, 20, 10), new(0, 0, 10, 10), new(20, 5, 30, 10) };
        // sorted: (0,w10) (20,w30) (80,w20): span 100, sizes 60, gap 20 -> x = 0, 30, 80
        var dh = MarkArrange.Distribute(dist, horizontal: true);
        Check("distribute horizontally: ends stay, the middle one moves to even the gaps",
              dh[0] == (0, 0) && dh[1] == (0, 0) && dh[2] == (10, 0), string.Join(" ", dh));
        var dv = MarkArrange.Distribute(new List<MarkArrange.Box> { new(0, 0, 10, 10), new(0, 5, 10, 10), new(0, 60, 10, 20) }, horizontal: false);
        // span 80, sizes 40, gap 20 -> y = 0, 30, 60
        Check("distribute vertically: gaps become equal", dv[0] == (0, 0) && dv[1] == (0, 25) && dv[2] == (0, 0), string.Join(" ", dv));
        Check("distribute: two boxes move nothing", MarkArrange.Distribute(boxes.Take(2).ToList(), true).All(s => s == (0, 0)), "");
        var dsum = MarkArrange.Distribute(dist, true);
        double lastRight = 0;
        for (int i = 0; i < dist.Count; i++) lastRight = Math.Max(lastRight, dist[i].X + dsum[i].Dx + dist[i].W);
        Check("distribute: the outer edges of the group do not move", Near(lastRight, 100), $"{lastRight}");

        // ── Same size ───────────────────────────────────────────────────────────────────
        var sz = MarkArrange.MatchSize(boxes, new[] { true, true, false }, width: true);
        Check("same width: others take the first mark's width, a fixed-size mark is left alone",
              sz[0] is null && sz[1] == 20 && sz[2] is null, string.Join(" ", sz.Select(v => v?.ToString() ?? "-")));
        var sh = MarkArrange.MatchSize(new List<MarkArrange.Box> { new(0, 0, 5, 12), new(0, 0, 9, 12), new(0, 0, 9, 3) }, new[] { true, true, true }, width: false);
        Check("same height: one that already matches is not touched", sh[1] is null && sh[2] == 12, string.Join(" ", sh.Select(v => v?.ToString() ?? "-")));
        Check("same size: a first mark with no size changes nothing",
              MarkArrange.MatchSize(new List<MarkArrange.Box> { new(0, 0, 0, 0), new(0, 0, 9, 9) }, new[] { true, true }, true).All(v => v is null), "");

        // ── Groups ──────────────────────────────────────────────────────────────────────
        string?[] ids = { "a", null, "a", "b", null, "b", "a" };
        Check("group: a member picks up its whole group, picked first",
              string.Join(",", MarkGroups.Expand(ids, new[] { 2 })) == "2,0,6", string.Join(",", MarkGroups.Expand(ids, new[] { 2 })));
        Check("group: an ungrouped mark is just itself",
              string.Join(",", MarkGroups.Expand(ids, new[] { 1 })) == "1", "");
        Check("group: two ungrouped marks do not pull each other in",
              string.Join(",", MarkGroups.Expand(ids, new[] { 1, 4 })) == "1,4", "");
        Check("group: two groups together, in pick order then list order",
              string.Join(",", MarkGroups.Expand(ids, new[] { 3, 0 })) == "3,0,2,5,6", string.Join(",", MarkGroups.Expand(ids, new[] { 3, 0 })));
        Check("group: a repeated or out-of-range pick is ignored",
              string.Join(",", MarkGroups.Expand(ids, new[] { 3, 3, 99, -1 })) == "3,5", "");
    }
}
