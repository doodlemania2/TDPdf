using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace TDPdf.Controls
{
    /// <summary>
    /// Animates a <see cref="GridLength"/> in pixel units, so a grid row/column can glide between
    /// two widths instead of snapping. WPF ships no such timeline (DoubleAnimation cannot target
    /// <c>ColumnDefinition.Width</c>), and the sidebar collapse needs one.
    /// </summary>
    /// <remarks>
    /// From/To/Easing MUST be dependency properties: starting the clock freezes and clones the
    /// timeline, and the clone carries only DPs — as plain CLR properties they would be lost on the
    /// clone and the "animation" would hold a constant until the completion snap.
    /// Adapted from upstream KillerPDF v1.6.5 (GPLv3).
    /// </remarks>
    internal sealed class GridLengthAnimation : AnimationTimeline
    {
        public static readonly DependencyProperty FromProperty =
            DependencyProperty.Register(nameof(From), typeof(GridLength), typeof(GridLengthAnimation));
        public static readonly DependencyProperty ToProperty =
            DependencyProperty.Register(nameof(To), typeof(GridLength), typeof(GridLengthAnimation));
        public static readonly DependencyProperty EasingProperty =
            DependencyProperty.Register(nameof(Easing), typeof(IEasingFunction), typeof(GridLengthAnimation));

        public GridLength From
        {
            get => (GridLength)GetValue(FromProperty);
            set => SetValue(FromProperty, value);
        }

        public GridLength To
        {
            get => (GridLength)GetValue(ToProperty);
            set => SetValue(ToProperty, value);
        }

        public IEasingFunction? Easing
        {
            get => (IEasingFunction?)GetValue(EasingProperty);
            set => SetValue(EasingProperty, value);
        }

        public override Type TargetPropertyType => typeof(GridLength);

        protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

        public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue,
            AnimationClock animationClock)
        {
            double progress = animationClock.CurrentProgress ?? 0.0;
            if (Easing is { } ease) progress = ease.Ease(progress);
            var from = From;
            var to = To;
            return new GridLength(from.Value + (to.Value - from.Value) * progress);
        }
    }
}
