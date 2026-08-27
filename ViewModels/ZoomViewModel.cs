using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TDPdf
{
    public sealed class ZoomLevelOption
    {
        public ZoomLevelOption(string displayText, double? zoomLevel = null, bool fitWidth = false, bool fitPage = false)
        {
            DisplayText = displayText;
            ZoomLevel = zoomLevel;
            IsFitWidth = fitWidth;
            IsFitPage = fitPage;
        }

        public string DisplayText { get; }
        public double? ZoomLevel { get; }
        public bool IsFitWidth { get; }
        public bool IsFitPage { get; }
    }

    /// <summary>
    /// The user-facing zoom. <see cref="ZoomLevel"/> is TRUE zoom throughout: 1.0 is the page at
    /// its natural size (1 PDF point = 1/72 inch on screen), which is exactly what the presets,
    /// <see cref="DisplayText"/> and the clamp below all mean.
    /// </summary>
    /// <remarks>
    /// It is deliberately NOT the layout scale of the page tile. Outside Continuous view the tile
    /// is the render-dimension bitmap — about 1.37x natural for A4, 1.45x for US Letter — so
    /// <c>MainWindow.LayoutZoomScale</c> divides this by <c>MainWindow.DisplayZoomFactor()</c>
    /// before it reaches the <c>ScaleTransform</c> and the render DPI. Keeping the view model in
    /// true zoom is what makes 100% mean 100% in every view mode, makes the dropdown presets and
    /// the status readout honest, and makes the min/max clamp below mean the same thing
    /// everywhere instead of ~5.8x in Single and exactly 4x in Continuous.
    /// </remarks>
    public partial class ZoomViewModel : ObservableObject
    {
        // True-zoom bounds: 5% to 400% of natural size, in every view mode.
        public const double MinZoomLevel = 0.05;
        public const double MaxZoomLevel = 4.0;

        /// <summary>
        /// How close a zoom has to be to a dropdown preset for the dropdown to show that preset.
        /// </summary>
        /// <remarks>
        /// #131: ONE tolerance, used by every comparison in this pair. It was two — <c>FindPreset</c>
        /// matched at 0.005 while <c>OnSelectedLevelChanged</c> wrote back at 0.0001 — and the gap
        /// between them was a self-driving loop: a computed fit of, say, 0.998 selected the 100%
        /// preset for display, and the write-back then forced the zoom itself to 1.00, changing the
        /// view the user had asked to fit and re-entering the whole zoom pipeline to do it.
        /// Displaying a preset must never move the zoom to it. <c>MainWindow.ZoomBox_SelectionChanged</c>
        /// reads this too, so the combo cannot read its own mirror back as a user pick.
        /// </remarks>
        public const double PresetMatchTolerance = 0.005;

        public ZoomViewModel()
        {
            AvailableLevels = new ObservableCollection<ZoomLevelOption>
            {
                new("5%", 0.05),
                new("10%", 0.10),
                new("25%", 0.25),
                new("50%", 0.50),
                new("75%", 0.75),
                new("100%", 1.00),
                new("125%", 1.25),
                new("150%", 1.50),
                new("200%", 2.00),
                new("400%", 4.00),
                new("Fit Width", fitWidth: true),
                new("Fit Page", fitPage: true),
            };

            selectedLevel = AvailableLevels[5];
        }

        public ObservableCollection<ZoomLevelOption> AvailableLevels { get; }

        [ObservableProperty]
        private double zoomLevel = 1.0;

        [ObservableProperty]
        private string displayText = "100%";

        [ObservableProperty]
        private ZoomLevelOption? selectedLevel;

        public void SetZoomLevel(double value)
        {
            ZoomLevel = Coerce(value);
        }

        public void ZoomIn()
        {
            SetZoomLevel(NextPreset(ZoomLevel, forward: true));
        }

        public void ZoomOut()
        {
            SetZoomLevel(NextPreset(ZoomLevel, forward: false));
        }

        public void Reset()
        {
            SetZoomLevel(1.0);
        }

        partial void OnZoomLevelChanged(double value)
        {
            var coerced = Coerce(value);
            if (System.Math.Abs(coerced - value) > 0.0001)
            {
                ZoomLevel = coerced;
                return;
            }

            DisplayText = FormatPercent(coerced);
            var newPreset = FindPreset(coerced);
            if (!ReferenceEquals(newPreset, SelectedLevel))
                SelectedLevel = newPreset;
        }

        partial void OnSelectedLevelChanged(ZoomLevelOption? value)
        {
            // #131: only a selection that asks for a DIFFERENT zoom moves the zoom. Anything inside
            // PresetMatchTolerance is the selection this view model just made to mirror the zoom
            // that is already in force — see the remarks on that constant.
            if (value?.ZoomLevel is double preset
                && System.Math.Abs(preset - ZoomLevel) >= PresetMatchTolerance)
            {
                SetZoomLevel(preset);
            }
        }

        private static double Coerce(double value) => System.Math.Max(MinZoomLevel, System.Math.Min(MaxZoomLevel, value));

        private static string FormatPercent(double value) => (value * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";

        private ZoomLevelOption? FindPreset(double value)
        {
            foreach (var option in AvailableLevels)
            {
                if (option.ZoomLevel is double preset
                    && System.Math.Abs(preset - value) < PresetMatchTolerance)
                    return option;
            }

            return null;
        }

        private double NextPreset(double current, bool forward)
        {
            double fallback = current + (forward ? 0.25 : -0.25);
            foreach (var option in forward ? AvailableLevels : ReverseAvailableLevels())
            {
                if (option.ZoomLevel is not double preset) continue;
                if (forward && preset > current + 0.005) return preset;
                if (!forward && preset < current - 0.005) return preset;
            }

            return fallback;
        }

        private System.Collections.Generic.IEnumerable<ZoomLevelOption> ReverseAvailableLevels()
        {
            for (int i = AvailableLevels.Count - 1; i >= 0; i--)
                yield return AvailableLevels[i];
        }
    }
}
