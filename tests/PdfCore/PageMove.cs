using TDPdf.Services;

/// <summary>
/// Pins the page-reorder arithmetic: <see cref="PageBlockMove"/>.
/// </summary>
/// <remarks>
/// Dragging pages in the sidebar is the one edit in TDPdf whose result the user cannot check by
/// looking at it: a block that lands one place late still looks like a plausible document, and the
/// mistake is only found later, by whoever reads it. The gap-to-index conversion is also where that
/// error lives — every selected page ABOVE the drop gap has to be subtracted back out, because the
/// gap is counted in the list the user is pointing at and the insert happens after those pages have
/// been lifted out of it.
///
/// So each case below asserts the whole resulting page ORDER, not just the insert index: the order
/// is what the document ends up with, and it is the only thing that catches an off-by-one that the
/// index arithmetic and the mutation happen to agree about. Compute and Apply are the same pair the
/// sidebar drag and the Move Page Up/Down rows go through.
/// </remarks>
internal static class PageMove
{
    /// <summary>The page order a drop of <paramref name="selection"/> into gap <paramref name="gap"/>
    /// produces, rendered as "0,2,1" — the source index of each page, in its new position.</summary>
    private static string Move(int pageCount, int[] selection, int gap)
    {
        var plan = PageBlockMove.Compute(pageCount, selection, gap);
        return string.Join(",", PageBlockMove.Apply(pageCount, plan));
    }

    private static bool NoOp(int pageCount, int[] selection, int gap)
        => PageBlockMove.Compute(pageCount, selection, gap).IsNoOp;

    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("Page block move (#135)");

        // ── One page, the case that worked before any of this ──────────────────────────────
        Check("a single page moves up one place",
              Move(5, [2], 1) == "0,2,1,3,4", Move(5, [2], 1));
        Check("a single page moves down one place",
              Move(5, [2], 4) == "0,1,3,2,4", Move(5, [2], 4));

        // ── A contiguous block ─────────────────────────────────────────────────────────────
        // Moving {2,3} up to gap 1 must land them at 1, keeping their relative order.
        Check("a contiguous block moves up, keeping its order",
              Move(6, [2, 3], 1) == "0,2,3,1,4,5", Move(6, [2, 3], 1));
        // Down to gap 5 (below page 4): two selected pages sit above it, so the insert index is 3.
        // Get that subtraction wrong and the block lands at 5, past the end of the shortened list.
        Check("a contiguous block moves down, keeping its order",
              Move(6, [2, 3], 5) == "0,1,4,2,3,5", Move(6, [2, 3], 5));
        Check("a block dropped at the very top lands first",
              Move(6, [3, 4], 0) == "3,4,0,1,2,5", Move(6, [3, 4], 0));
        Check("a block dropped past the last page lands last",
              Move(6, [1, 2], 6) == "0,3,4,5,1,2", Move(6, [1, 2], 6));

        // ── A non-contiguous selection ─────────────────────────────────────────────────────
        // The headline behaviour: 2, 5 and 9 arrive as one run, in ascending source order, at the
        // gap the insertion line promised.
        Check("a non-contiguous selection lands as one contiguous block, in source order",
              Move(10, [2, 5, 9], 1) == "0,2,5,9,1,3,4,6,7,8", Move(10, [2, 5, 9], 1));
        // Straddling the gap: 2 and 5 are above gap 7, so the block goes to index 5 — where the
        // line was — and page 9 is pulled up out of its old home to join it.
        Check("a selection straddling the drop point still lands at the line",
              Move(10, [2, 5, 9], 7) == "0,1,3,4,6,2,5,9,7,8", Move(10, [2, 5, 9], 7));
        Check("a non-contiguous selection dropped at the end gathers at the end",
              Move(6, [0, 3], 6) == "1,2,4,5,0,3", Move(6, [0, 3], 6));

        // ── No-ops ─────────────────────────────────────────────────────────────────────────
        // Dropping a block onto itself must cost nothing: the reorder rewrites and reloads the
        // document, which throws away the user's unsaved annotations.
        Check("a block dropped back where it started is a no-op", NoOp(6, [2, 3], 2), Move(6, [2, 3], 2));
        Check("a block dropped INSIDE itself is a no-op", NoOp(6, [2, 3, 4], 3), Move(6, [2, 3, 4], 3));
        Check("a block dropped into the gap just below itself is a no-op", NoOp(6, [2, 3], 4), Move(6, [2, 3], 4));
        Check("selecting every page makes any drop a no-op", NoOp(4, [0, 1, 2, 3], 2), Move(4, [0, 1, 2, 3], 2));
        Check("an empty selection is a no-op", NoOp(6, [], 3), Move(6, [], 3));
        Check("a real move is NOT reported as a no-op", !NoOp(6, [2, 3], 5), Move(6, [2, 3], 5));

        // ── Junk in the payload ────────────────────────────────────────────────────────────
        // The drag payload is a plain int[] off the clipboard and the page count can change under
        // it (another tab, a delete), so out-of-range and duplicate indices are filtered, not
        // trusted — an unfiltered index would throw mid-mutation, leaving the document half moved.
        Check("out-of-range indices are dropped",
              Move(4, [1, 9, -3], 4) == "0,2,3,1", Move(4, [1, 9, -3], 4));
        Check("duplicate indices move the page once",
              Move(4, [1, 1], 4) == "0,2,3,1", Move(4, [1, 1], 4));
        Check("a gap past the end is clamped, not an error",
              Move(4, [0], 99) == "1,2,3,0", Move(4, [0], 99));
        Check("a negative gap is clamped to the top",
              Move(4, [2], -5) == "2,0,1,3", Move(4, [2], -5));
    }
}
