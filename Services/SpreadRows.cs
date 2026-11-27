namespace TDPdf.Services
{
    /// <summary>One row of facing pages: the page index on the left and on the right, -1 when there is none.</summary>
    internal readonly record struct SpreadRow(int Left, int Right)
    {
        /// <summary>The left page, or the only page of a row that has just one.</summary>
        public int First => Left >= 0 ? Left : Right;
        public bool IsAlone => Left < 0 || Right < 0;
        public int Count => (Left >= 0 ? 1 : 0) + (Right >= 0 ? 1 : 0);
    }

    /// <summary>Where a page sits inside its row.</summary>
    internal enum SpreadCell { Left, Right, Centre }

    /// <summary>The page a drop at a point lands next to, and which side of it.</summary>
    internal readonly record struct DropAnchor(int Index, bool After)
    {
        /// <summary>The insertion gap: the page index the dropped pages go BEFORE.</summary>
        public int Gap => After ? Index + 1 : Index;
    }

    /// <summary>
    /// Pure layout maths for pages shown two to a row: the Two pages, continuous view and the
    /// two-column Pages sidebar (#246). Nothing here touches WPF, so the pairing rule, the lone
    /// cover, the row tops of mixed page sizes and the drop target of a two-dimensional list are
    /// all pinned by tests/PdfCore.
    /// </summary>
    /// <remarks>
    /// The pairing is the same one Two-Page view uses: pages (0,1), (2,3)… or, with book layout,
    /// the cover alone and then (1,2), (3,4)…
    /// </remarks>
    internal static class SpreadRows
    {
        public static int RowCount(int pageCount, bool book)
            => pageCount <= 0 ? 0 : book ? 1 + pageCount / 2 : (pageCount + 1) / 2;

        /// <summary>The row holding <paramref name="page"/> (negative pages are row 0).</summary>
        public static int RowOf(int page, bool book)
            => page <= 0 ? 0 : book ? (page + 1) / 2 : page / 2;

        public static SpreadRow Row(int row, int pageCount, bool book)
        {
            if (row < 0 || row >= RowCount(pageCount, book)) return new SpreadRow(-1, -1);
            if (book)
            {
                if (row == 0) return new SpreadRow(0, -1);
                int left = 2 * row - 1;
                return new SpreadRow(left, left + 1 < pageCount ? left + 1 : -1);
            }
            int l = 2 * row;
            return new SpreadRow(l, l + 1 < pageCount ? l + 1 : -1);
        }

        public static IReadOnlyList<SpreadRow> Build(int pageCount, bool book)
        {
            int rows = RowCount(pageCount, book);
            var list = new List<SpreadRow>(rows);
            for (int r = 0; r < rows; r++) list.Add(Row(r, pageCount, book));
            return list;
        }

        /// <summary>
        /// Where <paramref name="page"/> sits in its row. A row of one page is centred when it is the
        /// book cover, or when <paramref name="centreLoneLast"/> is set (the strip centres a lone final
        /// page; a thumbnail list keeps it on the left).
        /// </summary>
        public static SpreadCell CellOf(int page, int pageCount, bool book, bool centreLoneLast)
        {
            var row = Row(RowOf(page, book), pageCount, book);
            if (row.IsAlone)
            {
                bool cover = book && page == 0;
                return cover || centreLoneLast ? SpreadCell.Centre : SpreadCell.Left;
            }
            return page == row.Left ? SpreadCell.Left : SpreadCell.Right;
        }

        /// <summary>Row heights: the taller page of each row, so a mixed pair is sized to the taller one.</summary>
        public static double[] RowHeights(IReadOnlyList<double> pageHeights, bool book)
        {
            int count = pageHeights.Count;
            var heights = new double[RowCount(count, book)];
            for (int r = 0; r < heights.Length; r++)
            {
                var row = Row(r, count, book);
                double h = 0;
                if (row.Left >= 0) h = Math.Max(h, Clean(pageHeights[row.Left]));
                if (row.Right >= 0) h = Math.Max(h, Clean(pageHeights[row.Right]));
                heights[r] = h;
            }
            return heights;
        }

        /// <summary>
        /// The top of the row each page sits in (both pages of a row share it), with
        /// <paramref name="gap"/> between rows.
        /// </summary>
        public static double[] PageTops(IReadOnlyList<double> pageHeights, bool book, double gap)
        {
            int count = pageHeights.Count;
            var rowH = RowHeights(pageHeights, book);
            var tops = new double[count];
            double y = 0;
            for (int r = 0; r < rowH.Length; r++)
            {
                var row = Row(r, count, book);
                if (row.Left >= 0) tops[row.Left] = y;
                if (row.Right >= 0) tops[row.Right] = y;
                y += rowH[r] + gap;
            }
            return tops;
        }

        /// <summary>Width of a strip of two facing pages of <paramref name="pageWidth"/> with <paramref name="gap"/> between them.</summary>
        public static double StripWidth(double pageWidth, double gap) => 2 * pageWidth + gap;

        /// <summary>Left edge of a page inside a strip row of <see cref="StripWidth"/>.</summary>
        public static double CellLeft(SpreadCell cell, double pageWidth, double gap) => cell switch
        {
            SpreadCell.Left => 0,
            SpreadCell.Right => pageWidth + gap,
            _ => (StripWidth(pageWidth, gap) - pageWidth) / 2,
        };

        /// <summary>
        /// The page whose row centre is nearest <paramref name="centreY"/>; the row's left page when
        /// it has two. The first of equally near rows wins.
        /// </summary>
        public static int NearestPage(IReadOnlyList<double> pageHeights, bool book, double gap, double centreY)
        {
            int count = pageHeights.Count;
            if (count == 0) return -1;
            var rowH = RowHeights(pageHeights, book);
            double y = 0;
            int best = 0;
            double bestDist = double.MaxValue;
            for (int r = 0; r < rowH.Length; r++)
            {
                double dist = Math.Abs(y + rowH[r] / 2 - centreY);
                if (dist < bestDist) { bestDist = dist; best = Row(r, count, book).First; }
                y += rowH[r] + gap;
            }
            return best;
        }

        // ── Two-column list (the Pages sidebar) ─────────────────────────────────────────────────
        // Cells are all the same size: `width` is the panel width (each column takes half), `cellH`
        // the height of one row. A lone cover sits centred; a lone last item stays on the left.

        /// <summary>Keyboard step up or down: the same column of the neighbouring row, else its last item.</summary>
        public static int Vertical(int index, int pageCount, bool book, int direction)
        {
            if (pageCount <= 0) return -1;
            index = Math.Clamp(index, 0, pageCount - 1);
            int row = RowOf(index, book);
            int target = row + Math.Sign(direction);
            if (target < 0 || target >= RowCount(pageCount, book)) return index;
            var from = Row(row, pageCount, book);
            var to = Row(target, pageCount, book);
            bool wantRight = !from.IsAlone && index == from.Right;
            return wantRight && to.Right >= 0 ? to.Right : to.Left;
        }

        /// <summary>Left edge and width of the cell of <paramref name="index"/> in a list of <paramref name="width"/>.</summary>
        public static (double X, double W) ListCell(int index, int pageCount, bool book, double width)
        {
            double w = width / 2;
            return CellOf(index, pageCount, book, centreLoneLast: false) switch
            {
                SpreadCell.Left => (0, w),
                SpreadCell.Right => (w, w),
                _ => ((width - w) / 2, w),
            };
        }

        /// <summary>
        /// The page a drop at (<paramref name="x"/>, <paramref name="y"/>) lands next to. The point is in
        /// the list's own coordinates (y already includes the scroll offset). Within a cell the
        /// horizontal midpoint decides before or after; below the last row is after the last page.
        /// </summary>
        public static DropAnchor DropTarget(double x, double y, int pageCount, bool book, double width, double cellH)
        {
            if (pageCount <= 0) return new DropAnchor(0, false);
            if (y < 0) return new DropAnchor(0, false);
            int row = cellH > 0 ? (int)Math.Floor(y / cellH) : 0;
            if (row >= RowCount(pageCount, book)) return new DropAnchor(pageCount - 1, true);

            var r = Row(row, pageCount, book);
            int anchor = r.First;
            if (r.Count == 2)
            {
                // Two cells: the item under the pointer, by halves of the row.
                anchor = x < width / 2 ? r.Left : r.Right;
            }
            var (cx, cw) = ListCell(anchor, pageCount, book, width);
            return new DropAnchor(anchor, x >= cx + cw / 2);
        }

        /// <summary>The vertical insertion line for a drop: its x, top y (in list coordinates, scroll offset removed by the caller) and height.</summary>
        public static (double X, double Y, double H) DropLine(DropAnchor anchor, int pageCount, bool book, double width, double cellH)
        {
            int index = Math.Clamp(anchor.Index, 0, Math.Max(0, pageCount - 1));
            var (cx, cw) = ListCell(index, pageCount, book, width);
            double x = anchor.After ? cx + cw : cx;
            return (x, RowOf(index, book) * cellH, cellH);
        }

        private static double Clean(double v) => double.IsNaN(v) || double.IsInfinity(v) || v < 0 ? 0 : v;
    }

    /// <summary>
    /// The Pages sidebar's width limits and thumbnail sizing (#246). Widths are logical pixels
    /// (before the app-wide UI scale). One column holds a 140 px thumbnail at the default width;
    /// the thumbnails grow with the sidebar up to <see cref="ThumbCap"/>, the size they are rendered
    /// at, and the sidebar cannot be dragged wider than that needs.
    /// </summary>
    internal static class SidebarWidth
    {
        /// <summary>Full thumbnail width: the size the sidebar thumbnails are rendered at.</summary>
        public const double ThumbCap = 256;
        public const double ThumbDefault = 140;
        public const double ThumbMin = 100;
        /// <summary>Padding and border around a thumbnail inside its list entry.</summary>
        public const double ItemChrome = 16;
        /// <summary>The collapse strip on the sidebar's right edge.</summary>
        public const double Strip = 24;
        /// <summary>Room kept for the list's scroll bar.</summary>
        public const double ScrollBar = 14;
        /// <summary>The cell is this much taller than the thumbnail is wide (an A4 page).</summary>
        public const double PageAspect = 1.4143;

        public static int Columns(bool paired) => paired ? 2 : 1;

        public static double Default(bool paired)
            => paired ? Strip + ScrollBar + 2 * (ThumbDefault + ItemChrome) : Strip + ThumbDefault + ItemChrome;

        public static double Max(bool paired) => Strip + (paired ? ScrollBar : 0) + Columns(paired) * (ThumbCap + ItemChrome);

        /// <summary>The thumbnail width for a column that is <paramref name="columnWidth"/> wide.</summary>
        public static double ThumbWidthFor(double columnWidth)
        {
            if (double.IsNaN(columnWidth) || double.IsInfinity(columnWidth)) return ThumbDefault;
            return Math.Clamp(Math.Floor(columnWidth - ItemChrome), ThumbMin, ThumbCap);
        }

        /// <summary>Height of one entry of the two-column list for thumbnails <paramref name="thumbWidth"/> wide.</summary>
        public static double PairedCellHeight(double thumbWidth)
            => Math.Ceiling(thumbWidth * PageAspect) + 40;
    }
}
