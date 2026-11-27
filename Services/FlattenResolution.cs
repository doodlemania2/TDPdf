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

        /// <summary>The resolutions the Save Flattened options offer besides Automatic (0).</summary>
        public static readonly double[] FixedChoices = [72, 150, 200, 300];

        /// <summary>Normalises a remembered or typed choice: 0 (Automatic) or one of <see cref="FixedChoices"/>.</summary>
        public static double NormaliseChoice(double choice) =>
            Array.IndexOf(FixedChoices, choice) >= 0 ? choice : 0;

        /// <summary>
        /// DPI for one page under a user choice. 0 means Automatic and returns <paramref name="autoDpi"/>
        /// untouched; a fixed choice is used as asked, lowered only so the page stays within
        /// <see cref="MaxPagePixels"/> (the same memory bound Automatic obeys).
        /// </summary>
        public static double ForChoice(double choice, double autoDpi, double pageWidthPoint, double pageHeightPoint)
        {
            if (!(choice > 0)) return autoDpi;
            double dpi = choice;
            double area = pageWidthPoint * pageHeightPoint;
            if (double.IsFinite(area) && area > 0)
            {
                double cap = Math.Floor(72.0 * Math.Sqrt(MaxPagePixels / area));
                if (dpi > cap) dpi = Math.Max(1, cap);
            }
            return dpi;
        }

        /// <summary>
        /// Pixel size a page is rasterised to at <paramref name="dpi"/>: the same truncating
        /// arithmetic the flatten loop uses, never below 1 x 1.
        /// </summary>
        public static (int Width, int Height) PixelSize(double pageWidthPoint, double pageHeightPoint, double dpi) =>
            (Math.Max(1, (int)(pageWidthPoint * dpi / 72.0)), Math.Max(1, (int)(pageHeightPoint * dpi / 72.0)));

        /// <summary>The scale against the page's own size, in percent (72 dpi is 100%).</summary>
        public static int ScalePercent(double dpi) => (int)Math.Round(dpi / 72.0 * 100.0);

        /// <summary>Index of the page with the largest area (the first on a tie); -1 for none.</summary>
        public static int LargestPage(IReadOnlyList<(double WidthPoint, double HeightPoint)> pageSizes)
        {
            int best = -1; double bestArea = -1;
            for (int i = 0; i < pageSizes.Count; i++)
            {
                double a = pageSizes[i].WidthPoint * pageSizes[i].HeightPoint;
                if (double.IsFinite(a) && a > bestArea) { bestArea = a; best = i; }
            }
            return best;
        }

        /// <summary>
        /// DPI per page for <paramref name="path"/> (Automatic, or <paramref name="fixedChoice"/> when above 0;
        /// <paramref name="only"/> limits the PdfPig reads to those pages for a quick preview). Never throws: a file PdfPig cannot read, or a
        /// page it cannot parse, gets the 150 default (the behaviour before this existed).
        /// </summary>
        /// <remarks>
        /// Read once on the caller's thread before the parallel raster loop, like the JPEG hints.
        /// PdfPig reports image boxes in the displayed frame, which is the frame the placed size
        /// is wanted in.
        /// </remarks>
        public static double[] ReadPageDpis(string path, IReadOnlyList<(double WidthPoint, double HeightPoint)> pageSizes,
            double fixedChoice = 0, IReadOnlyCollection<int>? only = null)
        {
            var result = new double[pageSizes.Count];
            for (int i = 0; i < result.Length; i++)
                result[i] = ChooseDpi(pageSizes[i].WidthPoint, pageSizes[i].HeightPoint, []);

            // A fixed choice never looks at the images, so PdfPig is not opened at all.
            if (fixedChoice > 0)
            {
                for (int i = 0; i < result.Length; i++)
                    result[i] = ForChoice(fixedChoice, result[i], pageSizes[i].WidthPoint, pageSizes[i].HeightPoint);
                return result;
            }

            try
            {
                using var doc = PdfPigDoc.Open(path);
                int n = Math.Min(result.Length, doc.NumberOfPages);
                for (int i = 0; i < n; i++)
                {
                    if (only is not null && !only.Contains(i)) continue;
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
