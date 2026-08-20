using System;
using System.Windows;
using System.Windows.Threading;

namespace TDPdf.Controls
{
    /// <summary>
    /// Shared handling for a long-standing WPF framework race: a panel's internal
    /// <c>VisualCollection</c> is walked during a measure/arrange pass while children are
    /// being added or removed — typically from a Dispatcher continuation that landed inside
    /// a layout pass already in flight.
    ///
    /// The collection is consistent again once the add/remove completes, so the right
    /// response is to abandon the failed pass and queue a fresh one, not to let the
    /// exception reach <see cref="AppDomain.UnhandledException"/> and take the process down.
    /// See <see cref="SafeCanvas"/> and <see cref="SafeWrapPanel"/>.
    /// </summary>
    internal static class LayoutRace
    {
        /// <summary>
        /// True only when the throw came out of the visual/UI element collection itself.
        /// The exception type alone is far too broad — an
        /// <see cref="InvalidOperationException"/> raised by a child's own measure is a real
        /// bug and must keep crashing — so the throw site is the discriminator. It is also
        /// culture-independent, unlike matching on the message.
        ///
        /// Two shapes are known:
        ///   * <c>VisualCollection.Enumerator.MoveNext</c> throwing
        ///     <see cref="InvalidOperationException"/> ("the enumerator is not valid because
        ///     the collection changed") — panels that enumerate, e.g. <c>Canvas</c>.
        ///   * <c>VisualCollection.get_Item</c> throwing
        ///     <see cref="ArgumentOutOfRangeException"/> ("index ('2') must be less than
        ///     '1'") — panels that index, e.g. <c>WrapPanel</c>.
        /// </summary>
        public static bool IsCollectionChangedDuringLayout(Exception ex)
        {
            if (ex is not (InvalidOperationException or ArgumentOutOfRangeException or IndexOutOfRangeException))
                return false;

            // TargetSite is null for exceptions that have been rethrown across a boundary
            // that dropped it; fall back to the declaring type recorded on the stack trace.
            string? declaringType = ex.TargetSite?.DeclaringType?.FullName;
            if (declaringType is not null)
                return declaringType.Contains("VisualCollection", StringComparison.Ordinal)
                    || declaringType.Contains("UIElementCollection", StringComparison.Ordinal);

            return ex.StackTrace is { } st
                && (st.Contains("VisualCollection", StringComparison.Ordinal)
                    || st.Contains("UIElementCollection", StringComparison.Ordinal));
        }

        /// <summary>
        /// Re-runs layout once the children collection has settled. Background priority
        /// guarantees we run after the current (failed) layout pass and after any pending
        /// child add/remove operations queued on the Dispatcher.
        /// </summary>
        public static void QueueRelayout(FrameworkElement element)
        {
            element.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                element.InvalidateMeasure();
                element.InvalidateArrange();
            }));
        }
    }
}
