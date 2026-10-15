using TDPdf.Services;

/// <summary>
/// Pins the page-renumbering maps: <see cref="PageIndexRemap"/>.
/// </summary>
/// <remarks>
/// Every structural page edit — delete, insert, reorder, duplicate — renumbers the unsaved overlay
/// annotations and the page-snapshot undo history through one of these maps. A wrong map does not
/// crash anything: it moves a user's annotations onto the neighbouring page, where they look
/// entirely plausible until someone reads the saved file. So each case asserts the whole map, and
/// the re-keying cases assert WHICH value ends up on which page, not just how many survive.
/// </remarks>
internal static class PageRemap
{
    private static string S(int[] map) => string.Join(",", map);

    private static string Keys<T>(Dictionary<int, T> d) =>
        string.Join(",", d.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));

    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("Page index remap (#429)");

        // ── Delete ─────────────────────────────────────────────────────────────────────────
        // Unsorted, with a duplicate: the map must not care how the selection arrived.
        Check("delete: removed pages map to Removed, survivors close up",
              S(PageIndexRemap.ForDelete(6, [4, 1, 1])) == "0,-1,1,2,-1,3", S(PageIndexRemap.ForDelete(6, [4, 1, 1])));
        Check("delete: pages BEFORE the deleted range keep their numbers",
              S(PageIndexRemap.ForDelete(5, [3])) == "0,1,2,-1,3", S(PageIndexRemap.ForDelete(5, [3])));
        Check("delete: out-of-range indices are ignored",
              S(PageIndexRemap.ForDelete(3, [-1, 7, 1])) == "0,-1,1", S(PageIndexRemap.ForDelete(3, [-1, 7, 1])));
        Check("delete: nothing selected is the identity",
              S(PageIndexRemap.ForDelete(3, [])) == "0,1,2", S(PageIndexRemap.ForDelete(3, [])));

        // ── Insert / duplicate / append ────────────────────────────────────────────────────
        Check("insert one page at 2: pages from 2 on move up one",
              S(PageIndexRemap.ForInsert(5, 2, 1)) == "0,1,3,4,5", S(PageIndexRemap.ForInsert(5, 2, 1)));
        // Duplicating pages 1 and 2 of 5 inserts two copies at 3: only pages 3 and 4 move.
        Check("duplicate: copies inserted as a run shift only the pages after them",
              S(PageIndexRemap.ForInsert(5, 3, 2)) == "0,1,2,5,6", S(PageIndexRemap.ForInsert(5, 3, 2)));
        Check("append at the end is the identity (merge / drop)",
              S(PageIndexRemap.ForInsert(4, 4, 3)) == "0,1,2,3", S(PageIndexRemap.ForInsert(4, 4, 3)));
        Check("insert at the very top moves every page",
              S(PageIndexRemap.ForInsert(3, 0, 1)) == "1,2,3", S(PageIndexRemap.ForInsert(3, 0, 1)));

        // ── Reorder, through the same PageBlockMove model the sidebar drag uses ────────────
        // {2,3} dropped below page 4 of 6 gives the order 0,1,4,2,3,5 (pinned in PageMove), so the
        // pages that WERE 2 and 3 are now 3 and 4, and the page that was 4 is now 2.
        var plan = PageBlockMove.Compute(6, [2, 3], 5);
        var order = PageBlockMove.Apply(6, plan);
        var moveMap = PageIndexRemap.ForOrder(6, order);
        Check("reorder: each page's map entry is where the move put it",
              S(moveMap) == "0,1,3,4,2,5", S(moveMap));
        bool roundTrips = true;
        for (int old = 0; old < 6; old++) roundTrips &= order[moveMap[old]] == old;
        Check("reorder: the map is the exact inverse of the new order", roundTrips, S(moveMap));
        var scattered = PageBlockMove.Apply(10, PageBlockMove.Compute(10, [2, 5, 9], 7));
        var scatteredMap = PageIndexRemap.ForOrder(10, scattered);
        bool scatteredRoundTrips = true;
        for (int old = 0; old < 10; old++) scatteredRoundTrips &= scattered[scatteredMap[old]] == old;
        Check("reorder: a straddling non-contiguous move inverts too", scatteredRoundTrips, S(scatteredMap));
        Check("reorder: an order naming a page twice is rejected",
              Throws(() => PageIndexRemap.ForOrder(3, [0, 0, 2])), "");

        // ── Re-keying the annotation dictionary ────────────────────────────────────────────
        // The upstream #429 scenario: pages 1 and 4 deleted out of six.
        var annotations = new Dictionary<int, string>
        {
            [0] = "before", [1] = "deletedA", [2] = "between", [4] = "deletedB", [5] = "after",
        };
        var afterDelete = PageIndexRemap.RemapKeys(annotations, PageIndexRemap.ForDelete(6, [4, 1]));
        Check("delete: the deleted pages' annotations go, the rest follow their pages",
              Keys(afterDelete) == "0:before,1:between,3:after", Keys(afterDelete));
        Check("delete: the source dictionary is left untouched (the caller commits)",
              annotations.Count == 5 && annotations[4] == "deletedB", Keys(annotations));

        var afterMove = PageIndexRemap.RemapKeys(
            new Dictionary<int, string> { [2] = "two", [4] = "four" }, moveMap);
        Check("reorder: annotations travel with their pages",
              Keys(afterMove) == "2:four,3:two", Keys(afterMove));

        var stale = PageIndexRemap.RemapKeys(new Dictionary<int, string> { [1] = "ok", [9] = "stale" },
                                             PageIndexRemap.ForInsert(3, 0, 1));
        Check("a key for a page the document never had is dropped, not misplaced",
              Keys(stale) == "2:ok", Keys(stale));

        // Two survivors onto one page would silently lose one of them; that must be an error.
        Check("a map sending two pages to one index is rejected",
              Throws(() => PageIndexRemap.RemapKeys(new Dictionary<int, string> { [0] = "a", [1] = "b" }, [0, 0])), "");
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; }
        catch (ArgumentException) { return true; }
    }
}
