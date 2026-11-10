using UglyToad.PdfPig.Content;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf.Services
{
    /// <summary>
    /// Picks the raster resolution Save Flattened uses for each page (upstream #462: a flatten
    /// should not throw away the detail a 300 or 600 dpi scan already carries). Pure choice logic
    /// plus a thin PdfPig reader; WPF-free so tests/PdfCore compiles the real file.
    /// </summary>
    internal static class FlattenResolution
    {
        /// <summary>The resolution used for a page with no raster image, and the floor for the rest.</summary>
        public const double MinDpi = 150;

        /// <summary>No page is rasterised above this, however sharp its image is.</summary>
        public const double MaxDpi = 600;

        /// <summary>Per-page pixel budget (width x height), which bounds the raster's memory.</summary>
        public const double MaxPagePixels = 60_000_000;

        /// <summary>One raster image on a page: its pixel size and its placed size in points.</summary>
        public readonly record struct PlacedImage(int PixelWidth, int PixelHeight, double PlacedWidthPoint, double PlacedHeightPoint);

        /// <summary>
        /// DPI for one page: the effective resolution of its largest image (by pixel count),
        /// clamped to [<see cref="MinDpi"/>, <see cref="MaxDpi"/>] and then lowered, if need be,
        /// so the whole page stays within <see cref="MaxPagePixels"/>. Whole numbers only.
        /// </summary>
        public static double ChooseDpi(double pageWidthPoint, double pageHeightPoint, IEnumerable<PlacedImage> images)
        {
            double dpi = MinDpi;

            PlacedImage? largest = null;
            double largestPixels = 0;
            foreach (var img in images)
            {
                if (img.PixelWidth <= 0 || img.PixelHeight <= 0) continue;
                double placed = img.PlacedWidthPoint * img.PlacedHeightPoint;
                if (!double.IsFinite(placed) || placed <= 0) continue;
                double pixels = (double)img.PixelWidth * img.PixelHeight;
                if (pixels > largestPixels) { largestPixels = pixels; largest = img; }
            }

            if (largest is { } big)
            {
                // Axis-agnostic: a turned page or a rotated image swaps width and height, but the
                // ratio of areas does not care. 72 points per inch.
                double eff = 72.0 * Math.Sqrt(largestPixels / (big.PlacedWidthPoint * big.PlacedHeightPoint));
                if (double.IsFinite(eff)) dpi = Math.Clamp(Math.Round(eff), MinDpi, MaxDpi);
            }

            double area = pageWidthPoint * pageHeightPoint;
            if (double.IsFinite(area) && area > 0)
            {
                double cap = Math.Floor(72.0 * Math.Sqrt(MaxPagePixels / area));
                // The cap wins over the 150 floor only when the page itself is huge.
                if (dpi > cap) dpi = Math.Max(1, cap);
            }
            return dpi;
        }

        /// <summary>
        /// DPI per page for <paramref name="path"/>. Never throws: a file PdfPig cannot read, or a
        /// page it cannot parse, gets the 150 default (the behaviour before this existed).
        /// </summary>
        /// <remarks>
        /// Read once on the caller's thread before the parallel raster loop, like the JPEG hints.
        /// PdfPig reports image boxes in the displayed frame, which is the frame the placed size
        /// is wanted in.
        /// </remarks>
        public static double[] ReadPageDpis(string path, IReadOnlyList<(double WidthPoint, double HeightPoint)> pageSizes)
        {
            var result = new double[pageSizes.Count];
            for (int i = 0; i < result.Length; i++)
                result[i] = ChooseDpi(pageSizes[i].WidthPoint, pageSizes[i].HeightPoint, []);

            try
            {
                using var doc = PdfPigDoc.Open(path);
                int n = Math.Min(result.Length, doc.NumberOfPages);
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        var placed = new List<PlacedImage>();
                        foreach (IPdfImage img in doc.GetPage(i + 1).GetImages())
                        {
                            var b = img.BoundingBox;
                            placed.Add(new PlacedImage(img.WidthInSamples, img.HeightInSamples,
                                                       Math.Abs(b.Width), Math.Abs(b.Height)));
                        }
                        result[i] = ChooseDpi(pageSizes[i].WidthPoint, pageSizes[i].HeightPoint, placed);
                    }
                    catch { /* this page keeps the default */ }
                }
            }
            catch { /* unreadable by PdfPig: every page keeps the default */ }
            return result;
        }
    }
}
