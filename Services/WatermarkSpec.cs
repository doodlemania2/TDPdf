using System.Globalization;

namespace TDPdf.Services
{
    internal enum WatermarkKind { Text, Picture }

    /// <summary>Where a watermark sits on the page. Diagonal runs along the page's own diagonal.</summary>
    internal enum WatermarkPlace { Center, Diagonal, Top, Bottom }

    /// <summary>Whether the watermark is painted behind the page's own content or over it.</summary>
    internal enum WatermarkLayer { Behind, Over }

    internal enum PageNumberPlace { BottomCenter, BottomLeft, BottomRight, TopCenter, TopLeft, TopRight }

    /// <summary>
    /// What the Watermark dialog asks for (3.0): an optional text or picture watermark, optional page
    /// numbers, and the pages they go on. Pure data plus the rules for it, so the numbers the preview
    /// shows and the numbers the saved file gets cannot drift apart.
    /// </summary>
    internal sealed class WatermarkSpec
    {
        /// <summary>False for a page-numbers-only run.</summary>
        public bool HasWatermark { get; set; } = true;
        public WatermarkKind Kind { get; set; } = WatermarkKind.Text;
        public WatermarkLayer Layer { get; set; } = WatermarkLayer.Behind;
        public WatermarkPlace Place { get; set; } = WatermarkPlace.Diagonal;

        // Text
        public string Text { get; set; } = "CONFIDENTIAL";
        /// <summary>The font by NAME. Kept exactly as chosen or typed, whether or not it is installed.</summary>
        public string FontFamily { get; set; } = "Arial";
        public double FontSize { get; set; } = 64;
        public bool Bold { get; set; } = true;
        public byte ColorR { get; set; } = 128;
        public byte ColorG { get; set; } = 128;
        public byte ColorB { get; set; } = 128;

        /// <summary>Extra turn in degrees, counter-clockwise, on top of the place's own angle.</summary>
        public double RotationDegrees { get; set; }

        // Picture
        public byte[]? ImageBytes { get; set; }
        public double ImagePixelWidth { get; set; }
        public double ImagePixelHeight { get; set; }
        /// <summary>The picture's resolution in dots per inch; 0 or less means "not recorded" (96 is assumed).</summary>
        public double ImageDpiX { get; set; }
        public double ImageDpiY { get; set; }
        /// <summary>Percent of the picture's own size (its pixels at its resolution), 100 = as it is.</summary>
        public double ImageScalePercent { get; set; } = 100;

        /// <summary>0.05 to 1, for the watermark. Page numbers are always drawn fully opaque.</summary>
        public double Opacity { get; set; } = 0.3;

        // Page numbers
        public bool PageNumbers { get; set; }
        public string PageNumberFormat { get; set; } = WatermarkMath.DefaultPageNumberFormat;
        public PageNumberPlace NumberPlace { get; set; } = PageNumberPlace.BottomCenter;
        public double NumberFontSize { get; set; } = 10;

        /// <summary>The 0-based pages to mark.</summary>
        public IReadOnlyList<int> Pages { get; set; } = Array.Empty<int>();

        /// <summary>True when applying this would draw anything at all.</summary>
        public bool HasContent =>
            (HasWatermark && (Kind == WatermarkKind.Text ? !string.IsNullOrWhiteSpace(Text) : ImageBytes is { Length: > 0 }))
            || PageNumbers;
    }

    /// <summary>The rules behind a watermark: limits, numbers read off a typed value, and where things go.</summary>
    internal static class WatermarkMath
    {
        internal const string DefaultPageNumberFormat = "Page {n} of {N}";
        internal const double MinOpacityPercent = 5, MaxOpacityPercent = 100;
        internal const double MinFontSize = 6, MaxFontSize = 400;
        internal const double MinNumberFontSize = 6, MaxNumberFontSize = 48;
        internal const double MinScalePercent = 1, MaxScalePercent = 1000;
        internal const double PlaceMargin = 36, NumberMargin = 28;

        internal static double ClampOpacityPercent(double v) => double.IsNaN(v) ? 30 : Math.Clamp(v, MinOpacityPercent, MaxOpacityPercent);
        internal static double ClampFontSize(double v) => double.IsNaN(v) ? 64 : Math.Clamp(v, MinFontSize, MaxFontSize);
        internal static double ClampNumberFontSize(double v) => double.IsNaN(v) ? 10 : Math.Clamp(v, MinNumberFontSize, MaxNumberFontSize);
        internal static double ClampScalePercent(double v) => double.IsNaN(v) ? 100 : Math.Clamp(v, MinScalePercent, MaxScalePercent);

        /// <summary>A typed turn, wrapped into -180..180 so "370" and "10" are the same watermark.</summary>
        internal static double NormalizeDegrees(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
            double d = v % 360;
            if (d > 180) d -= 360;
            if (d <= -180) d += 360;
            return d;
        }

        /// <summary>
        /// The text of one page number: <c>{n}</c> is this page's number and <c>{N}</c> the number of pages
        /// in the document. A blank format is just the number.
        /// </summary>
        internal static string FormatPageNumber(string? format, int pageNumber, int pageCount)
        {
            if (string.IsNullOrWhiteSpace(format)) return pageNumber.ToString(CultureInfo.InvariantCulture);
            return format.Replace("{n}", pageNumber.ToString(CultureInfo.InvariantCulture))
                         .Replace("{N}", pageCount.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>A picture dimension in points: its pixels at its resolution. 96 dpi when none is recorded.</summary>
        internal static double NaturalPoints(double pixels, double dpi)
        {
            if (!(dpi > 0) || double.IsInfinity(dpi)) dpi = 96;
            return pixels * 72.0 / dpi;
        }

        /// <summary>The picture's size on the page in points: its own size from its resolution, times the scale percent.</summary>
        internal static (double W, double H) PictureSize(WatermarkSpec spec)
        {
            double k = ClampScalePercent(spec.ImageScalePercent) / 100.0;
            return (NaturalPoints(spec.ImagePixelWidth, spec.ImageDpiX) * k, NaturalPoints(spec.ImagePixelHeight, spec.ImageDpiY) * k);
        }

        /// <summary>The angle, counter-clockwise, of a page's diagonal from its lower left corner to its upper right.</summary>
        internal static double DiagonalAngle(double pageW, double pageH) =>
            pageW > 0 && pageH > 0 ? Math.Atan2(pageH, pageW) * 180.0 / Math.PI : 45;

        /// <summary>
        /// Where the watermark's centre goes on a page of <paramref name="pageW"/> by <paramref name="pageH"/>
        /// points, and the angle (counter-clockwise, degrees) it is turned. The centre is in the
        /// top-down frame of the page as shown (origin top left). Top and Bottom keep the turned box
        /// inside the page margin.
        /// </summary>
        internal static (double Cx, double Cy, double Angle) Place(WatermarkSpec spec, double pageW, double pageH, double contentW, double contentH)
        {
            double angle = NormalizeDegrees(spec.RotationDegrees);
            if (spec.Place == WatermarkPlace.Diagonal) angle = NormalizeDegrees(angle + DiagonalAngle(pageW, pageH));
            double rad = angle * Math.PI / 180.0;
            double halfH = (contentW * Math.Abs(Math.Sin(rad)) + contentH * Math.Abs(Math.Cos(rad))) / 2;
            double cx = pageW / 2, cy = pageH / 2;
            if (spec.Place == WatermarkPlace.Top) cy = Math.Min(pageH / 2, PlaceMargin + halfH);
            else if (spec.Place == WatermarkPlace.Bottom) cy = Math.Max(pageH / 2, pageH - PlaceMargin - halfH);
            return (cx, cy, angle);
        }

        /// <summary>The top-left of a page number's text box, in the top-down frame of the page as shown.</summary>
        internal static (double X, double Y) NumberAnchor(PageNumberPlace place, double pageW, double pageH, double textW, double textH)
        {
            double x = place switch
            {
                PageNumberPlace.BottomLeft or PageNumberPlace.TopLeft => NumberMargin,
                PageNumberPlace.BottomRight or PageNumberPlace.TopRight => pageW - NumberMargin - textW,
                _ => (pageW - textW) / 2,
            };
            double y = place is PageNumberPlace.TopLeft or PageNumberPlace.TopCenter or PageNumberPlace.TopRight
                ? NumberMargin : pageH - NumberMargin - textH;
            return (Math.Max(0, x), Math.Max(0, y));
        }
    }
}
