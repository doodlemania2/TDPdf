using System;
using System.Windows;
using System.Windows.Controls;

namespace TDPdf.Controls
{
    /// <summary>
    /// A <see cref="WrapPanel"/> resilient to the layout race described on
    /// <see cref="LayoutRace"/>. On a <see cref="WrapPanel"/> the race surfaces as
    /// <see cref="ArgumentOutOfRangeException"/> out of <c>VisualCollection.get_Item</c>
    /// (observed signature: <c>index ('2') must be less than '1'</c>), because the panel
    /// indexes its children rather than enumerating them.
    ///
    /// This is the page-grid panel (<c>PageContentPanel</c>) whose secondary pages are
    /// added and removed from async Dispatcher continuations.
    /// </summary>
    public sealed class SafeWrapPanel : WrapPanel
    {
        private Size _lastMeasure;

        protected override Size MeasureOverride(Size constraint)
        {
            try
            {
                _lastMeasure = base.MeasureOverride(constraint);
                return _lastMeasure;
            }
            catch (Exception ex) when (LayoutRace.IsWrapPanelCollectionChangedDuringLayout(ex))
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
            catch (Exception ex) when (LayoutRace.IsWrapPanelCollectionChangedDuringLayout(ex))
            {
                LayoutRace.QueueRelayout(this);
                return finalSize;
            }
        }
    }
}
