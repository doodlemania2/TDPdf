using System;
using System.Diagnostics;
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
        /// True only when the throw originated in the guarded panel's own layout method.
        /// The exception type alone is far too broad — an
        /// <see cref="InvalidOperationException"/> raised by a child's own measure is a real
        /// bug and must keep crashing — so the first non-helper stack frame is the
        /// discriminator. It is also culture-independent, unlike matching on the message.
        ///
        /// Two shapes are known:
        ///   * <c>VisualCollection.Enumerator.MoveNext</c> throwing
        ///     <see cref="InvalidOperationException"/> ("the enumerator is not valid because
        ///     the collection changed") — panels that enumerate, e.g. <c>Canvas</c>.
        ///   * <c>VisualCollection.get_Item</c> throwing
        ///     <see cref="ArgumentOutOfRangeException"/> ("index ('2') must be less than
        ///     '1'") — panels that index, e.g. <c>WrapPanel</c>.
        /// </summary>
        public static bool IsCanvasCollectionChangedDuringLayout(Exception ex)
        {
            return ex is InvalidOperationException
                && OriginatesInPanelLayout(ex, "System.Windows.Controls.Canvas");
        }

        public static bool IsWrapPanelCollectionChangedDuringLayout(Exception ex)
        {
            return ex is (ArgumentOutOfRangeException or IndexOutOfRangeException)
                && OriginatesInPanelLayout(ex, "System.Windows.Controls.WrapPanel");
        }

        private static bool OriginatesInPanelLayout(Exception ex, string panelType)
        {
            if (IsPanelLayoutMethod(
                    ex.TargetSite?.DeclaringType?.FullName,
                    ex.TargetSite?.Name,
                    panelType))
            {
                return true;
            }

            StackFrame[]? frames = new StackTrace(ex, false).GetFrames();
            if (frames is null)
                return false;

            // Optimized WPF builds may omit VisualCollection.get_Item /
            // VisualCollection.Enumerator.MoveNext entirely. Walk past runtime throw helpers and
            // any collection frames that remain; the guarded panel's MeasureOverride or
            // ArrangeOverride must be the first real caller. A child-control exception therefore
            // stays visible instead of being swallowed merely because its later stack contains
            // Canvas or WrapPanel.
            foreach (StackFrame frame in frames)
            {
                var method = frame.GetMethod();
                if (method is null)
                    continue;

                string? declaringType = method.DeclaringType?.FullName;
                if (IsThrowHelperMethod(declaringType, method.Name)
                    || (declaringType is not null && MentionsVisualCollection(declaringType)))
                {
                    continue;
                }

                return IsPanelLayoutMethod(declaringType, method.Name, panelType);
            }

            return false;
        }

        private static bool IsPanelLayoutMethod(string? declaringType, string? methodName, string panelType) =>
            string.Equals(declaringType, panelType, StringComparison.Ordinal)
            && methodName is "MeasureOverride" or "ArrangeOverride";

        private static bool IsThrowHelperMethod(string? declaringType, string methodName) =>
            string.Equals(declaringType, "System.ThrowHelper", StringComparison.Ordinal)
            || (declaringType is "System.ArgumentOutOfRangeException" or "System.InvalidOperationException"
                && methodName.StartsWith("Throw", StringComparison.Ordinal));

        private static bool MentionsVisualCollection(string text) =>
            text.Contains("VisualCollection", StringComparison.Ordinal)
            || text.Contains("UIElementCollection", StringComparison.Ordinal);

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
