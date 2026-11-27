using System.Globalization;
using System.Windows;

namespace TDPdf.Services
{
    /// <summary>One of the standard rubber stamps: its name, the words on it, and its fixed colour.</summary>
    internal sealed record StampDef(string Id, string Label, byte R, byte G, byte B);

    /// <summary>
    /// The standard stamps and the geometry every stamp shares (3.0). A stamp is drawn in a fixed
    /// design box (<see cref="DesignHeight"/> tall, as wide as its text needs), and its
    /// <see cref="PlacedAnnotation.Scale"/> turns design units into canvas pixels, so one number
    /// sizes the canvas drawing, the hit box and the saved appearance alike.
    /// </summary>
    internal static class StampLibrary
    {
        /// <summary>The design font size, in design units. The Size slider is this, in canvas pixels.</summary>
        internal const double DesignFont = 30;

        /// <summary>Height of every stamp's design box.</summary>
        internal const double DesignHeight = 60;

        private const double MinDesignWidth = 110;

        /// <summary>The picker's entries, in order. Colours are fixed per stamp, as in common PDF editors.</summary>
        internal static readonly IReadOnlyList<StampDef> Standard = new StampDef[]
        {
            new("Approved",     "Approved",     30, 138,  60),
            new("Draft",        "Draft",        43,  92, 168),
            new("Confidential", "Confidential", 192, 40,  30),
            new("Final",        "Final",        106, 63, 160),
            new("NotApproved",  "Not Approved", 192, 40,  30),
            new("Received",     "Received",      14, 124, 134),
            new("Reviewed",     "Reviewed",     194, 106,   0),
            new("ForComment",   "For Comment",   43,  92, 168),
        };

        internal static StampDef? Find(string? id) =>
            Standard.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>The words as they are drawn: upper case, one line.</summary>
        internal static string DisplayText(string label) =>
            string.Join(" ", label.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim().ToUpperInvariant();

        /// <summary>
        /// The design width of a text stamp. Bold capitals in a sans face average a little under three
        /// quarters of an em, so this is the text plus a margin; the drawing centres the text in it and
        /// shrinks it if it still overflows.
        /// </summary>
        internal static double DesignWidth(string displayText) =>
            Math.Max(MinDesignWidth, Math.Round(displayText.Length * DesignFont * 0.72 + 44));

        /// <summary>The Scale for a text size in canvas pixels.</summary>
        internal static double ScaleForSize(double size) => size / DesignFont;

        /// <summary>A standard stamp, centred on <paramref name="center"/>.</summary>
        internal static StampAnnotation Create(StampDef def, Point center, int pageIndex, double size, double opacity)
        {
            string text = DisplayText(def.Label);
            return Place(new StampAnnotation
            {
                PageIndex = pageIndex, Kind = StampKind.Standard, StampId = def.Id, Label = text,
                ColorR = def.R, ColorG = def.G, ColorB = def.B,
                SourceWidth = DesignWidth(text), SourceHeight = DesignHeight
            }, center, size, opacity);
        }

        /// <summary>A custom text stamp in the colour given, or null when there is no text to draw.</summary>
        internal static StampAnnotation? CreateCustom(string? text, byte r, byte g, byte b,
                                                      Point center, int pageIndex, double size, double opacity)
        {
            string shown = DisplayText(text ?? "");
            if (shown.Length == 0) return null;
            return Place(new StampAnnotation
            {
                PageIndex = pageIndex, Kind = StampKind.CustomText, Label = shown,
                ColorR = r, ColorG = g, ColorB = b,
                SourceWidth = DesignWidth(shown), SourceHeight = DesignHeight
            }, center, size, opacity);
        }

        /// <summary>
        /// A picture stamp. The design box is <see cref="DesignHeight"/> tall and as wide as the
        /// picture's aspect ratio makes it, so Size sets the picture's height in the same units as a
        /// text stamp's. Null when the picture has no usable size.
        /// </summary>
        internal static StampAnnotation? CreateImage(string base64, double pixelWidth, double pixelHeight,
                                                     Point center, int pageIndex, double size, double opacity)
        {
            if (!(pixelWidth >= 1) || !(pixelHeight >= 1) || string.IsNullOrEmpty(base64)) return null;
            double aspect = Math.Clamp(pixelWidth / pixelHeight, 0.05, 20);
            return Place(new StampAnnotation
            {
                PageIndex = pageIndex, Kind = StampKind.Image, ImageData = base64,
                SourceWidth = DesignHeight * aspect, SourceHeight = DesignHeight
            }, center, size, opacity);
        }

        private static StampAnnotation Place(StampAnnotation s, Point center, double size, double opacity)
        {
            s.Scale = ScaleForSize(StampSettings.ClampSize(size));
            s.Opacity = StampSettings.ClampOpacity(opacity);
            s.Position = new Point(center.X - s.SourceWidth * s.Scale / 2, center.Y - s.SourceHeight * s.Scale / 2);
            return s;
        }
    }

    /// <summary>
    /// The Stamp tool's settings: the ranges the bar's sliders cover, how typed numbers are read, and
    /// the defaults that Reset restores. Pure so the rules are tested.
    /// </summary>
    internal sealed class StampSettings
    {
        internal const double MinSize = 12, MaxSize = 120, DefaultSize = 36;
        internal const double MinOpacityPercent = 10, MaxOpacityPercent = 100, DefaultOpacityPercent = 100;
        internal const string DefaultStampId = "Approved";

        /// <summary>The picker's choice: a standard id, <see cref="CustomTextChoice"/> or <see cref="ImageChoice"/>.</summary>
        internal string Choice { get; set; } = DefaultStampId;
        internal const string CustomTextChoice = "#custom";
        internal const string ImageChoice = "#image";

        internal double Size { get; set; } = DefaultSize;
        internal double OpacityPercent { get; set; } = DefaultOpacityPercent;
        internal string CustomText { get; set; } = "";
        internal byte CustomR { get; set; } = 192;
        internal byte CustomG { get; set; } = 40;
        internal byte CustomB { get; set; } = 30;
        internal string? ImageData { get; set; }
        internal double ImagePixelWidth { get; set; }
        internal double ImagePixelHeight { get; set; }
        internal string ImageName { get; set; } = "";

        /// <summary>Puts every setting back to its default, including the custom text, colour and picture.</summary>
        internal void Reset()
        {
            var d = new StampSettings();
            Choice = d.Choice; Size = d.Size; OpacityPercent = d.OpacityPercent;
            CustomText = d.CustomText; CustomR = d.CustomR; CustomG = d.CustomG; CustomB = d.CustomB;
            ImageData = null; ImagePixelWidth = 0; ImagePixelHeight = 0; ImageName = "";
        }

        internal static double ClampSize(double v) =>
            double.IsNaN(v) ? DefaultSize : Math.Clamp(v, MinSize, MaxSize);

        internal static double ClampOpacityPercent(double v) =>
            double.IsNaN(v) ? DefaultOpacityPercent : Math.Clamp(v, MinOpacityPercent, MaxOpacityPercent);

        /// <summary>An opacity as a 0 to 1 fraction, kept inside the slider's range.</summary>
        internal static double ClampOpacity(double fraction) =>
            ClampOpacityPercent(fraction * 100) / 100;

        /// <summary>
        /// Reads a typed number. Either decimal separator works ("32.5" and "32,5"); when both appear
        /// the last one is the decimal point and the other is a thousands mark ("1,234.5", "1.234,5").
        /// A trailing unit such as "px" or "%" is ignored. False for anything else.
        /// </summary>
        internal static bool TryParseNumber(string? text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim().TrimEnd('%').TrimEnd();
            if (t.EndsWith("px", StringComparison.OrdinalIgnoreCase) || t.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
                t = t[..^2].TrimEnd();
            t = t.Replace(" ", "").Replace(" ", "");
            int dot = t.LastIndexOf('.'), comma = t.LastIndexOf(',');
            if (dot >= 0 && comma >= 0)
            {
                char decimalMark = dot > comma ? '.' : ',';
                char thousands = decimalMark == '.' ? ',' : '.';
                t = t.Replace(thousands.ToString(), "").Replace(decimalMark, '.');
            }
            else if (comma >= 0)
            {
                if (t.IndexOf(',') != comma) return false;   // "1,2,3" is not a number
                t = t.Replace(',', '.');
            }
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>A typed size, clamped to the slider's range. False (value untouched) when it is not a number.</summary>
        internal static bool TryParseSize(string? text, out double size)
        {
            size = DefaultSize;
            if (!TryParseNumber(text, out double v)) return false;
            size = ClampSize(v);
            return true;
        }

        /// <summary>A typed opacity in percent, clamped to the slider's range.</summary>
        internal static bool TryParseOpacityPercent(string? text, out double percent)
        {
            percent = DefaultOpacityPercent;
            if (!TryParseNumber(text, out double v)) return false;
            percent = ClampOpacityPercent(v);
            return true;
        }
    }
}
