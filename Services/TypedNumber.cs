using System.Globalization;

namespace TDPdf.Services
{
    /// <summary>
    /// Reads a number a person typed into a tool bar or dialog box. Shared by the Stamp tool and the
    /// Watermark dialog so every typed value follows one rule, whatever the machine's culture.
    /// </summary>
    internal static class TypedNumber
    {
        /// <summary>
        /// Either decimal separator works ("32.5" and "32,5"); when both appear the last one is the
        /// decimal point and the other is a thousands mark ("1,234.5", "1.234,5"). A trailing "%", "px"
        /// or "pt" is ignored. False for anything else, including NaN and infinity.
        /// </summary>
        internal static bool TryParse(string? text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim().TrimEnd('%').TrimEnd();
            if (t.EndsWith("px", StringComparison.OrdinalIgnoreCase) || t.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
                t = t[..^2].TrimEnd();
            t = t.Replace(" ", "").Replace("\u00A0", "");
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
    }
}
