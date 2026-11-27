namespace TDPdf.Services
{
    /// <summary>A rectangle of pixels.</summary>
    internal readonly record struct PixelRect(int X, int Y, int W, int H)
    {
        internal int Right => X + W;
        internal int Bottom => Y + H;
        internal long Area => (long)W * H;
    }

    /// <summary>
    /// The visual half of a comparison: finds the areas where two renderings of a page differ, on a grid of
    /// small tiles so anti-aliasing noise is ignored and neighbouring differences become one area.
    /// All BGRA, 4 bytes a pixel, no row padding.
    /// </summary>
    internal static class PdfCompareRaster
    {
        /// <summary>Side of one tile in pixels.</summary>
        internal const int TileSize = 16;
        /// <summary>A pixel differs when any colour channel is further apart than this (0 to 255).</summary>
        internal const int ChannelThreshold = 40;
        /// <summary>A tile differs when at least this many of its pixels do.</summary>
        internal const int MinPixelsPerTile = 3;
        /// <summary>Most areas kept for one page; more are joined until they fit.</summary>
        internal const int MaxRegions = 48;

        /// <summary>Tiles across and down for an image.</summary>
        internal static (int Across, int Down) GridSize(int width, int height, int tile = TileSize) =>
            (width <= 0 ? 0 : (width + tile - 1) / tile, height <= 0 ? 0 : (height + tile - 1) / tile);

        /// <summary>
        /// Puts a raster on a white canvas of <paramref name="canvasW"/> x <paramref name="canvasH"/>, top left
        /// aligned, so two pages of different sizes can be compared pixel to pixel.
        /// </summary>
        internal static byte[] PadToCanvas(byte[] bgra, int width, int height, int canvasW, int canvasH)
        {
            if (width == canvasW && height == canvasH) return bgra;
            var canvas = new byte[checked(canvasW * canvasH * 4)];
            Array.Fill(canvas, (byte)255);
            int copyW = Math.Min(width, canvasW), copyH = Math.Min(height, canvasH);
            for (int y = 0; y < copyH; y++)
                Buffer.BlockCopy(bgra, y * width * 4, canvas, y * canvasW * 4, copyW * 4);
            return canvas;
        }

        /// <summary>Which tiles hold a difference, row by row (index = tileY * across + tileX).</summary>
        internal static bool[] DifferingTiles(
            byte[] a, byte[] b, int width, int height,
            int channelThreshold = ChannelThreshold, int minPixels = MinPixelsPerTile, int tile = TileSize)
        {
            var (across, down) = GridSize(width, height, tile);
            var grid = new bool[across * down];
            if (across == 0 || down == 0) return grid;
            if (a.AsSpan(0, width * height * 4).SequenceEqual(b.AsSpan(0, width * height * 4))) return grid;

            var counts = new int[grid.Length];
            for (int y = 0; y < height; y++)
            {
                int row = y * width * 4;
                int tileRow = (y / tile) * across;
                for (int x = 0; x < width; x++)
                {
                    int p = row + x * 4;
                    if (Math.Abs(a[p] - b[p]) > channelThreshold
                        || Math.Abs(a[p + 1] - b[p + 1]) > channelThreshold
                        || Math.Abs(a[p + 2] - b[p + 2]) > channelThreshold)
                        counts[tileRow + x / tile]++;
                }
            }
            for (int i = 0; i < grid.Length; i++) grid[i] = counts[i] >= minPixels;
            return grid;
        }

        /// <summary>
        /// Joins differing tiles into rectangles: tiles within <paramref name="gap"/> tiles of each other (in either
        /// direction) belong to one area, which is the bounding box of its tiles clipped to the image.
        /// </summary>
        internal static List<PixelRect> MergeTiles(bool[] grid, int across, int down, int width, int height, int gap = 1, int tile = TileSize)
        {
            var regions = new List<PixelRect>();
            if (across == 0 || down == 0) return regions;
            var seen = new bool[grid.Length];
            var stack = new Stack<int>();
            for (int start = 0; start < grid.Length; start++)
            {
                if (!grid[start] || seen[start]) continue;
                int minX = across, minY = down, maxX = -1, maxY = -1;
                seen[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int cur = stack.Pop();
                    int cx = cur % across, cy = cur / across;
                    minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx);
                    minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
                    for (int dy = -gap; dy <= gap; dy++)
                    {
                        int ny = cy + dy;
                        if (ny < 0 || ny >= down) continue;
                        for (int dx = -gap; dx <= gap; dx++)
                        {
                            int nx = cx + dx;
                            if (nx < 0 || nx >= across) continue;
                            int ni = ny * across + nx;
                            if (grid[ni] && !seen[ni]) { seen[ni] = true; stack.Push(ni); }
                        }
                    }
                }
                int x0 = minX * tile, y0 = minY * tile;
                int x1 = Math.Min(width, (maxX + 1) * tile), y1 = Math.Min(height, (maxY + 1) * tile);
                regions.Add(new PixelRect(x0, y0, x1 - x0, y1 - y0));
            }
            return regions;
        }

        /// <summary>
        /// The areas where two same-size rasters differ. When there are more than <see cref="MaxRegions"/>,
        /// the tiles are re-joined with a wider gap until they fit (and in the end become one area).
        /// </summary>
        internal static List<PixelRect> Regions(byte[] a, byte[] b, int width, int height)
        {
            var grid = DifferingTiles(a, b, width, height);
            var (across, down) = GridSize(width, height);
            int gap = 1;
            while (true)
            {
                var regions = MergeTiles(grid, across, down, width, height, gap);
                if (regions.Count <= MaxRegions) return regions;
                if (gap >= Math.Max(across, down)) return regions;
                gap *= 2;
            }
        }

        /// <summary>
        /// Drops the areas that a text change already explains: an area goes when at least
        /// <paramref name="fraction"/> of it lies under the <paramref name="covers"/> rectangles, so the list
        /// shows each change once.
        /// </summary>
        internal static List<PixelRect> SubtractCovered(
            IReadOnlyList<PixelRect> regions, IReadOnlyList<PixelRect> covers, double fraction = 0.6)
        {
            var kept = new List<PixelRect>();
            foreach (var r in regions)
            {
                if (r.Area <= 0) continue;
                // The cover rectangles are text boxes and may overlap, so measure the union by row bands.
                long covered = CoveredArea(r, covers);
                if (covered < fraction * r.Area) kept.Add(r);
            }
            return kept;
        }

        private static long CoveredArea(PixelRect r, IReadOnlyList<PixelRect> covers)
        {
            var clipped = new List<PixelRect>();
            foreach (var c in covers)
            {
                int x0 = Math.Max(r.X, c.X), y0 = Math.Max(r.Y, c.Y);
                int x1 = Math.Min(r.Right, c.Right), y1 = Math.Min(r.Bottom, c.Bottom);
                if (x1 > x0 && y1 > y0) clipped.Add(new PixelRect(x0, y0, x1 - x0, y1 - y0));
            }
            if (clipped.Count == 0) return 0;
            var ys = clipped.SelectMany(c => new[] { c.Y, c.Bottom }).Distinct().OrderBy(y => y).ToList();
            long total = 0;
            for (int i = 0; i + 1 < ys.Count; i++)
            {
                int yTop = ys[i], yBot = ys[i + 1];
                var spans = clipped.Where(c => c.Y <= yTop && c.Bottom >= yBot)
                                   .Select(c => (c.X, c.Right)).OrderBy(s => s.X).ToList();
                long width = 0; int end = int.MinValue;
                foreach (var (s, e) in spans)
                {
                    if (e <= end) continue;
                    width += e - Math.Max(s, end);
                    end = e;
                }
                total += width * (yBot - yTop);
            }
            return total;
        }

        /// <summary>
        /// The "differences only" picture: white paper, ink both pages share in light grey, ink that is only on the
        /// left in red and ink that is only on the right in blue.
        /// </summary>
        internal static byte[] DifferenceImage(byte[] a, byte[] b, int width, int height)
        {
            var result = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int p = i * 4;
                int la = (a[p] * 29 + a[p + 1] * 150 + a[p + 2] * 77) >> 8;
                int lb = (b[p] * 29 + b[p + 1] * 150 + b[p + 2] * 77) >> 8;
                byte B, G, R;
                if (Math.Abs(la - lb) <= ChannelThreshold)
                {
                    // Shared content, faded so the differences stand out.
                    byte g = (byte)(255 - (255 - Math.Min(la, lb)) / 3);
                    B = G = R = g;
                }
                else if (la < lb)
                {   // ink on the left only
                    int s = 255 - la;
                    R = (byte)Math.Min(255, 200 + s / 5); G = (byte)(255 - s); B = (byte)(255 - s);
                }
                else
                {   // ink on the right only
                    int s = 255 - lb;
                    B = (byte)Math.Min(255, 200 + s / 5); G = (byte)(255 - s); R = (byte)(255 - s);
                }
                result[p] = B; result[p + 1] = G; result[p + 2] = R; result[p + 3] = 255;
            }
            return result;
        }
    }
}
