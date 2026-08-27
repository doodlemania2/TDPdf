using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
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
        /// #131: this is now a DISPLAY tolerance and nothing else. There used to be a second one:
        /// <c>FindPreset</c> matched at 0.005 while <c>OnSelectedLevelChanged</c> wrote the matched
        /// preset back into the zoom at 0.0001, so a computed Fit Width of 0.998 selected the 100%
        /// entry for display and was then forced to exactly 1.00 — changing the fit the user asked
        /// for, and re-entering the whole zoom pipeline to do it. That write-back is gone.
        /// Displaying a preset must never move the zoom to it.
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

        /// <summary>
        /// The method that most recently asked for a zoom. #131/#132: every zoom write in the app
        /// funnels through <see cref="ZoomLevel"/>'s <c>PropertyChanged</c> into one handler, so
        /// <c>[CallerMemberName]</c> on <c>MainWindow.ApplyZoom</c> can only ever report that
        /// handler — the fan-in point, never the originator. This is the frame above it, and it is
        /// what makes the <c>Zoom.Churn</c> diagnostic able to name a real culprit.
        /// </summary>
        public string? LastZoomOrigin { get; private set; }

        public void SetZoomLevel(double value, [CallerMemberName] string? origin = null)
        {
            LastZoomOrigin = origin;
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
            {
                // Mark it BEFORE the write: the ComboBox's TwoWay binding pushes this straight into
                // Selector.SelectedItem, which raises SelectionChanged, and the handler must be able
                // to tell that echo from a person choosing a zoom. See ConsumeMirrorEcho.
                _pendingMirror = newPreset;
                SelectedLevel = newPreset;
            }
        }

        /// <summary>The selection this view model pushed to mirror the zoom, until it is consumed.</summary>
        private ZoomLevelOption? _pendingMirror;

        /// <summary>
        /// True when <paramref name="option"/> is the selection this view model just pushed onto
        /// <see cref="SelectedLevel"/> to MIRROR the current zoom — the ComboBox's TwoWay binding
        /// handing the application its own update back, not a person picking a zoom.
        /// </summary>
        /// <remarks>
        /// #131: this is identity, not arithmetic, and it is deliberately single-use. Comparing the
        /// picked value against the current zoom cannot work, because by the time
        /// <c>SelectionChanged</c> is raised the two agree either way. Clearing the mark on the
        /// first check means a later, genuine pick of the same entry is never mistaken for an echo.
        /// Nothing here assumes the binding's push is synchronous with the write that caused it.
        /// </remarks>
        public bool ConsumeMirrorEcho(ZoomLevelOption? option)
        {
            if (_pendingMirror is null || !ReferenceEquals(option, _pendingMirror)) return false;
            _pendingMirror = null;
            return true;
        }

        // #131: OnSelectedLevelChanged deliberately does not exist. It used to write the selected
        // preset back into ZoomLevel, so merely DISPLAYING a preset moved the zoom to it — a
        // computed Fit Width of 0.998 was force-snapped to exactly 1.00, changing the fit the user
        // asked for and re-entering the whole zoom pipeline to do it. SelectedLevel is display
        // state; MainWindow.ZoomBox_SelectionChanged is the one owner of a user's pick.

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
