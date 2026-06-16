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

    public partial class ZoomViewModel : ObservableObject
    {
        public const double MinZoomLevel = 0.05;
        public const double MaxZoomLevel = 4.0;

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
            if (value?.ZoomLevel is double preset && System.Math.Abs(preset - ZoomLevel) > 0.0001)
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
                if (option.ZoomLevel is double preset && System.Math.Abs(preset - value) < 0.005)
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
