using System;
using System.Windows;
using System.Windows.Controls;

namespace TDPdf.Controls
{
    /// <summary>
    /// A <see cref="Canvas"/> resilient to the layout race described on
    /// <see cref="LayoutRace"/>. On a <see cref="Canvas"/> the race surfaces as
    /// <see cref="InvalidOperationException"/> out of
    /// <c>VisualCollection.Enumerator.MoveNext</c>, because <c>Canvas.MeasureOverride</c>
    /// enumerates its children rather than indexing them.
    ///
    /// This is the annotation overlay (<c>AnnotationCanvas</c>), whose children are churned
    /// by annotation re-renders, selection chrome, link and form-field overlays, and inline
    /// text edit boxes — several of those from Dispatcher continuations. Placing a signature
    /// or a text box was killing the process outright (#115).
    /// </summary>
    public sealed class SafeCanvas : Canvas
    {
        private Size _lastMeasure;

        protected override Size MeasureOverride(Size constraint)
        {
            try
            {
                _lastMeasure = base.MeasureOverride(constraint);
                return _lastMeasure;
            }
            catch (Exception ex) when (LayoutRace.IsCanvasCollectionChangedDuringLayout(ex))
            {
                LayoutRace.QueueRelayout(this);
                return _lastMeasure;
            }
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            try
            {
                return base.ArrangeOverride(finalSize);
            }
            catch (Exception ex) when (LayoutRace.IsCanvasCollectionChangedDuringLayout(ex))
            {
                LayoutRace.QueueRelayout(this);
                return finalSize;
            }
        }
    }
}
