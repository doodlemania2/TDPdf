using TDPdf.Services;

/// <summary>Pins the pairing, row layout and two-column list maths behind #246.</summary>
internal static class SpreadRowsTests
{
    private static string Rows(int count, bool book)
        => string.Join(" ", SpreadRows.Build(count, book).Select(r => r.Right < 0 ? $"{r.Left}" : $"{r.Left}-{r.Right}"));

    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("Spread rows and two-column list (#246)");

        // ── pairing ────────────────────────────────────────────────────────────────────────
        Check("rows: classic pairing of 5 pages", Rows(5, false) == "0-1 2-3 4", Rows(5, false));
        Check("rows: book layout of 5 pages keeps the cover alone", Rows(5, true) == "0 1-2 3-4", Rows(5, true));
        Check("rows: book layout of 4 pages ends on a lone page", Rows(4, true) == "0 1-2 3", Rows(4, true));
        Check("rows: one page", Rows(1, false) == "0" && Rows(1, true) == "0", $"{Rows(1, false)} {Rows(1, true)}");
        Check("rows: no pages", Rows(0, false) == "" && SpreadRows.RowCount(0, true) == 0, "");
        bool countsOk = true;
        for (int n = 0; n <= 40; n++)
            foreach (bool book in new[] { false, true })
                if (SpreadRows.RowCount(n, book) != SpreadRows.Build(n, book).Count) countsOk = false;
        Check("rows: RowCount agrees with Build for 0..40 pages", countsOk, "");

        bool rowOfOk = true;
        for (int n = 1; n <= 41; n++)
            foreach (bool book in new[] { false, true })
                for (int p = 0; p < n; p++)
                {
                    var row = SpreadRows.Row(SpreadRows.RowOf(p, book), n, book);
                    if (row.Left != p && row.Right != p) rowOfOk = false;
                }
        Check("rows: every page is in the row RowOf names", rowOfOk, "");

        // ── cells ──────────────────────────────────────────────────────────────────────────
        Check("cells: book cover is centred", SpreadRows.CellOf(0, 5, true, false) == SpreadCell.Centre, "");
        Check("cells: classic first page is left", SpreadRows.CellOf(0, 5, false, false) == SpreadCell.Left, "");
        Check("cells: second of a pair is right", SpreadRows.CellOf(3, 5, false, false) == SpreadCell.Right, "");
        Check("cells: lone last page is centred in the strip", SpreadRows.CellOf(4, 5, false, true) == SpreadCell.Centre, "");
        Check("cells: lone last page stays left in a list", SpreadRows.CellOf(4, 5, false, false) == SpreadCell.Left, "");
        Check("cells: left edge of a centred page", SpreadRows.CellLeft(SpreadCell.Centre, 100, 12) == 56, $"{SpreadRows.CellLeft(SpreadCell.Centre, 100, 12)}");
        Check("cells: left edge of the right page", SpreadRows.CellLeft(SpreadCell.Right, 100, 12) == 112, "");
        Check("cells: strip width", SpreadRows.StripWidth(100, 12) == 212, "");

        // ── tops with mixed sizes ──────────────────────────────────────────────────────────
        double[] heights = [100, 140, 90, 60, 80];
        var tops = SpreadRows.PageTops(heights, false, 10);
        Check("tops: both pages of a row share the row top", tops[0] == 0 && tops[1] == 0 && tops[2] == 150 && tops[3] == 150 && tops[4] == 250,
              string.Join(",", tops));
        var rowH = SpreadRows.RowHeights(heights, false);
        Check("tops: a mixed pair takes the taller page", rowH.Length == 3 && rowH[0] == 140 && rowH[1] == 90 && rowH[2] == 80, string.Join(",", rowH));
        var bookTops = SpreadRows.PageTops(heights, true, 10);
        Check("tops: book layout puts the cover on its own row", bookTops[0] == 0 && bookTops[1] == 110 && bookTops[2] == 110 && bookTops[3] == 260 && bookTops[4] == 260,
              string.Join(",", bookTops));
        Check("tops: bad heights count as zero", SpreadRows.RowHeights([double.NaN, -5], false)[0] == 0, "");
        Check("tops: empty", SpreadRows.PageTops([], false, 10).Length == 0, "");

        Check("nearest: the left page of the nearest row", SpreadRows.NearestPage(heights, false, 10, 160) == 2, $"{SpreadRows.NearestPage(heights, false, 10, 160)}");
        Check("nearest: top of the document", SpreadRows.NearestPage(heights, false, 10, 0) == 0, "");
        Check("nearest: past the end is the last row", SpreadRows.NearestPage(heights, false, 10, 5000) == 4, "");
        Check("nearest: nothing to find", SpreadRows.NearestPage([], false, 10, 0) == -1, "");

        // ── keyboard in the two-column list ────────────────────────────────────────────────
        Check("keys: down keeps the column", SpreadRows.Vertical(0, 6, false, 1) == 2 && SpreadRows.Vertical(1, 6, false, 1) == 3, "");
        Check("keys: up keeps the column", SpreadRows.Vertical(3, 6, false, -1) == 1 && SpreadRows.Vertical(2, 6, false, -1) == 0, "");
        Check("keys: down from the last row stays put", SpreadRows.Vertical(5, 6, false, 1) == 5, "");
        Check("keys: up from the first row stays put", SpreadRows.Vertical(1, 6, false, -1) == 1, "");
        Check("keys: down into a lone last row lands on it", SpreadRows.Vertical(3, 5, false, 1) == 4, $"{SpreadRows.Vertical(3, 5, false, 1)}");
        Check("keys: book cover down goes to the left page", SpreadRows.Vertical(0, 5, true, 1) == 1, "");
        Check("keys: right page up into the cover lands on it", SpreadRows.Vertical(2, 5, true, -1) == 0, "");

        // ── drop target ────────────────────────────────────────────────────────────────────
        const double W = 200, H = 100;
        var a = SpreadRows.DropTarget(20, 10, 6, false, W, H);
        Check("drop: left half of the left cell is before it", a == new DropAnchor(0, false) && a.Gap == 0, $"{a}");
        a = SpreadRows.DropTarget(80, 10, 6, false, W, H);
        Check("drop: right half of the left cell is after it", a == new DropAnchor(0, true) && a.Gap == 1, $"{a}");
        a = SpreadRows.DropTarget(120, 10, 6, false, W, H);
        Check("drop: left half of the right cell is before it", a == new DropAnchor(1, false) && a.Gap == 1, $"{a}");
        a = SpreadRows.DropTarget(190, 10, 6, false, W, H);
        Check("drop: right half of the right cell is after it", a == new DropAnchor(1, true) && a.Gap == 2, $"{a}");
        a = SpreadRows.DropTarget(20, 250, 6, false, W, H);
        Check("drop: the third row", a == new DropAnchor(4, false), $"{a}");
        a = SpreadRows.DropTarget(20, 9999, 6, false, W, H);
        Check("drop: below the list is the end", a.Gap == 6, $"{a}");
        a = SpreadRows.DropTarget(20, -30, 6, false, W, H);
        Check("drop: above the list is the start", a.Gap == 0, $"{a}");
        a = SpreadRows.DropTarget(150, 10, 5, true, W, H);
        Check("drop: right of a centred cover is after it", a == new DropAnchor(0, true) && a.Gap == 1, $"{a}");
        a = SpreadRows.DropTarget(60, 10, 5, true, W, H);
        Check("drop: left of a centred cover is before it", a == new DropAnchor(0, false), $"{a}");
        a = SpreadRows.DropTarget(150, 210, 5, false, W, H);
        Check("drop: right of a lone last page is after it", a == new DropAnchor(4, true) && a.Gap == 5, $"{a}");

        var line = SpreadRows.DropLine(new DropAnchor(1, false), 6, false, W, H);
        Check("line: before the right cell is the middle", line == (100, 0, 100), $"{line}");
        line = SpreadRows.DropLine(new DropAnchor(3, true), 6, false, W, H);
        Check("line: after the right cell is the right edge of its row", line == (200, 100, 100), $"{line}");
        line = SpreadRows.DropLine(new DropAnchor(0, false), 5, true, W, H);
        Check("line: before a centred cover", line == (50, 0, 100), $"{line}");

        // The line and the drop must agree: every point inside the list gives a line sitting at the gap.
        bool agree = true;
        for (int n = 1; n <= 9; n++)
            foreach (bool book in new[] { false, true })
                for (double y = 0; y < SpreadRows.RowCount(n, book) * H; y += 25)
                    for (double x = 0; x <= W; x += 10)
                    {
                        var t = SpreadRows.DropTarget(x, y, n, book, W, H);
                        if (t.Gap < 0 || t.Gap > n) agree = false;
                        var l = SpreadRows.DropLine(t, n, book, W, H);
                        if (l.X < 0 || l.X > W || l.Y < 0) agree = false;
                    }
        Check("drop: every point gives a gap in range and a line inside the list", agree, "");

        // ── sidebar widths ─────────────────────────────────────────────────────────────────
        Check("width: one column defaults to the old 180", SidebarWidth.Default(false) == 180, $"{SidebarWidth.Default(false)}");
        Check("width: two columns need more than one", SidebarWidth.Default(true) > SidebarWidth.Default(false)
              && SidebarWidth.Max(true) > SidebarWidth.Max(false), "");
        Check("width: the cap holds two full-size thumbnails", SidebarWidth.Max(true) >= SidebarWidth.Strip + 2 * (SidebarWidth.ThumbCap + SidebarWidth.ItemChrome), "");
        Check("width: a default-width column gives the default thumbnail", SidebarWidth.ThumbWidthFor(156) == 140, $"{SidebarWidth.ThumbWidthFor(156)}");
        Check("width: thumbnails stop at full size", SidebarWidth.ThumbWidthFor(900) == SidebarWidth.ThumbCap, "");
        Check("width: thumbnails stop shrinking", SidebarWidth.ThumbWidthFor(10) == SidebarWidth.ThumbMin && SidebarWidth.ThumbWidthFor(double.NaN) == SidebarWidth.ThumbDefault, "");
        Check("width: a cell is taller than its thumbnail", SidebarWidth.PairedCellHeight(140) > 140 * 1.4, "");
        // At the cap width each column is exactly wide enough for a full-size thumbnail.
        double colAtMax = (SidebarWidth.Max(true) - SidebarWidth.Strip - SidebarWidth.ScrollBar) / 2;
        Check("width: at the cap both thumbnails are full size", SidebarWidth.ThumbWidthFor(colAtMax) == SidebarWidth.ThumbCap, $"{colAtMax}");
    }
}
