using System.Windows;
using System.Windows.Media;

namespace TDPdf.Services
{
    /// <summary>
    /// The one lookup for the theme's brushes from code-built UI, and the one way to take a theme
    /// colour at a given alpha. Eight windows each carried their own copy of the lookup, with slightly
    /// different fallbacks; a missing theme key must leave a dialog legible rather than throw the way
    /// <c>FindResource</c> does, so the fallback is a system colour.
    /// </summary>
    internal static class ThemeBrushes
    {
        /// <summary>
        /// The theme brush for <paramref name="key"/> (looked up at the application level, where the
        /// theme dictionaries live), or a system-colour stand-in when the theme lacks it.
        /// </summary>
        internal static SolidColorBrush Get(string key)
            => Application.Current?.TryFindResource(key) as SolidColorBrush ?? SystemBrush(key);

        /// <summary>As <see cref="Get(string)"/>, but a missing key gives <paramref name="fallback"/>.</summary>
        internal static SolidColorBrush Get(string key, SolidColorBrush fallback)
            => Application.Current?.TryFindResource(key) as SolidColorBrush ?? fallback;

        /// <summary>
        /// As <see cref="Get(string)"/>, but a missing key (or a lookup before the dictionaries have
        /// loaded) gives a frozen brush of <paramref name="fallback"/>.
        /// </summary>
        internal static SolidColorBrush Get(string key, Color fallback)
        {
            try
            {
                if (Application.Current?.TryFindResource(key) is SolidColorBrush b) return b;
            }
            catch { /* resource lookup before the dictionaries load */ }
            var brush = new SolidColorBrush(fallback);
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// The theme colour <paramref name="key"/> at <paramref name="alpha"/>, for translucent washes.
        /// Resolved when called, so a wash built after a theme switch follows the new theme. Not frozen
        /// unless asked, so callers that adjust a brush afterwards keep working.
        /// </summary>
        internal static SolidColorBrush Wash(string key, byte alpha, bool freeze = false)
            => Wash(Get(key).Color, alpha, freeze);

        /// <summary>Any colour at <paramref name="alpha"/> (its own alpha is replaced).</summary>
        internal static SolidColorBrush Wash(Color color, byte alpha, bool freeze = false)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            if (freeze) brush.Freeze();
            return brush;
        }

        /// <summary>
        /// What each theme key stands in for when the theme has no such brush. One table for every
        /// window, so a missing key reads the same wherever it is hit.
        /// </summary>
        private static SolidColorBrush SystemBrush(string key) => key switch
        {
            "AccentGreen" or "AccentGreenDim" or "DangerRed" or "WarningOrange" => SystemColors.HighlightBrush,
            "BgDark" or "BgPanel" => SystemColors.WindowBrush,
            "BgHover" => SystemColors.ControlBrush,
            "BgPressed" => SystemColors.ControlDarkBrush,
            "TextSecondary" or "TextMuted" => SystemColors.GrayTextBrush,
            _ => SystemColors.WindowTextBrush,   // BorderDim, TextPrimary and anything unknown
        };
    }
}
