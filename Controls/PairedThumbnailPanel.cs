using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TDPdf.Services;

namespace TDPdf.Controls
{
    /// <summary>
    /// The two-column layout of the Pages sidebar (#246): a virtualising panel that puts two
    /// entries on a row, with the cover alone and centred when book layout is on. WPF has no
    /// virtualising wrap panel, and a plain <see cref="WrapPanel"/> would realise every thumbnail
    /// of a long document.
    /// </summary>
    /// <remarks>
    /// Every cell is the same size (<see cref="CellHeight"/> tall, half the panel wide), so which
    /// entries are on screen is arithmetic and only those are realised, plus a row of margin either
    /// side. The items are still one per PAGE, so the list's selection, multi-select, drag-reorder
    /// and keyboard handling are untouched: the panel only decides where each page's container is
    /// drawn. Scrolling is in pixels. The pairing and the drop target come from
    /// <see cref="SpreadRows"/>, which is covered by tests/PdfCore.
    /// </remarks>
    internal sealed class PairedThumbnailPanel : VirtualizingPanel, IScrollInfo
    {
        public static readonly DependencyProperty CellHeightProperty = DependencyProperty.Register(
            nameof(CellHeight), typeof(double), typeof(PairedThumbnailPanel),
            new FrameworkPropertyMetadata(220.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty BookLayoutProperty = DependencyProperty.Register(
            nameof(BookLayout), typeof(bool), typeof(PairedThumbnailPanel),
            new FrameworkPropertyMetadata(false,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        /// <summary>Raised (bubbling, from the panel) when the width a cell column gets changes, so the
        /// sidebar can size its thumbnails to fit.</summary>
        public static readonly RoutedEvent CellWidthChangedEvent = EventManager.RegisterRoutedEvent(
            "CellWidthChanged", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(PairedThumbnailPanel));

        /// <summary>Height of one row of cells.</summary>
        public double CellHeight
        {
            get => (double)GetValue(CellHeightProperty);
            set => SetValue(CellHeightProperty, value);
        }

        /// <summary>The first entry sits alone and centred, and the pairs run 2-3, 4-5…</summary>
        public bool BookLayout
        {
            get => (bool)GetValue(BookLayoutProperty);
            set => SetValue(BookLayoutProperty, value);
        }

        /// <summary>The width of one cell column, as of the last layout.</summary>
        public double CellWidth => _width / 2;

        private double _offset;
        private double _width;
        private Size _extent;
        private Size _viewport;
        private double _announcedWidth = -1;

        private int ItemCount => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

        // ── Layout ──────────────────────────────────────────────────────────────────────────────

        protected override Size MeasureOverride(Size availableSize)
        {
            _ = InternalChildren;   // the generator exists only once the children collection has been touched
            int count = ItemCount;
            bool book = BookLayout;
            double cellH = Math.Max(1, CellHeight);
            int rows = SpreadRows.RowCount(count, book);
            double width = double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width;
            double extentH = rows * cellH;
            // Under an unbounded height there is nothing to virtualise against: everything is realised.
            double viewH = double.IsInfinity(availableSize.Height) ? extentH : availableSize.Height;
            _width = width;
            SetScrollInfo(new Size(width, extentH), new Size(width, viewH));

            if (count == 0)
            {
                CleanUp(0, -1);
                return new Size(width, 0);
            }

            int firstRow = Math.Max(0, (int)Math.Floor(_offset / cellH) - 1);
            int lastRow = Math.Min(rows - 1, (int)Math.Ceiling((_offset + viewH) / cellH));
            int first = SpreadRows.Row(firstRow, count, book).First;
            var lastRowCells = SpreadRows.Row(lastRow, count, book);
            int last = lastRowCells.Right >= 0 ? lastRowCells.Right : lastRowCells.Left;

            var cellSize = new Size(width / 2, cellH);
            Realise(first, last, cellSize);
            CleanUp(first, last);

            if (Math.Abs(width - _announcedWidth) > 0.5)
            {
                _announcedWidth = width;
                // Not from inside this layout pass: the sidebar answers by changing item properties.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => RaiseEvent(new RoutedEventArgs(CellWidthChangedEvent, this))));
            }
            return new Size(width, viewH);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int count = ItemCount;
            bool book = BookLayout;
            double cellH = Math.Max(1, CellHeight);
            double width = finalSize.Width;
            var children = InternalChildren;
            for (int i = 0; i < children.Count; i++)
            {
                int index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
                if (index < 0 || index >= count) continue;
                var (x, w) = SpreadRows.ListCell(index, count, book, width);
                double y = SpreadRows.RowOf(index, book) * cellH - _offset;
                children[i].Arrange(new Rect(x, y, w, cellH));
            }
            return finalSize;
        }

        private void Realise(int first, int last, Size cellSize)
        {
            IItemContainerGenerator generator = ItemContainerGenerator;
            GeneratorPosition start = generator.GeneratorPositionFromIndex(first);
            int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
            {
                for (int i = first; i <= last; i++, childIndex++)
                {
                    var child = generator.GenerateNext(out bool isNew) as UIElement;
                    if (child is null) continue;
                    if (isNew)
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(cellSize);
                }
            }
        }

        /// <summary>Drops the containers of entries outside [first, last].</summary>
        private void CleanUp(int first, int last)
        {
            var children = InternalChildren;
            IItemContainerGenerator generator = ItemContainerGenerator;
            for (int i = children.Count - 1; i >= 0; i--)
            {
                var position = new GeneratorPosition(i, 0);
                int index = generator.IndexFromGeneratorPosition(position);
                if (index < first || index > last)
                {
                    generator.Remove(position, 1);
                    RemoveInternalChildRange(i, 1);
                }
            }
        }

        protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
        {
            switch (args.Action)
            {
                case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
                case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
                    RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                    break;
                case System.Collections.Specialized.NotifyCollectionChangedAction.Move:
                    RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                    break;
            }
            InvalidateMeasure();
        }

        protected override void BringIndexIntoView(int index) => EnsureVisible(index);

        // ── Hit testing for drag and drop ───────────────────────────────────────────────────────

        /// <summary>
        /// Where a drop at <paramref name="point"/> (in this panel's coordinates) lands: the gap it
        /// inserts at, and the vertical insertion line in the same coordinates.
        /// </summary>
        public (DropAnchor Anchor, Rect Line) DropAt(Point point)
        {
            int count = ItemCount;
            bool book = BookLayout;
            double cellH = Math.Max(1, CellHeight);
            var anchor = SpreadRows.DropTarget(point.X, point.Y + _offset, count, book, _width, cellH);
            if (count == 0) return (anchor, Rect.Empty);
            var (x, y, h) = SpreadRows.DropLine(anchor, count, book, _width, cellH);
            return (anchor, new Rect(x, y - _offset, 0, h));
        }

        // ── Scrolling (pixels) ──────────────────────────────────────────────────────────────────

        public ScrollViewer? ScrollOwner { get; set; }
        public bool CanHorizontallyScroll { get; set; }
        public bool CanVerticallyScroll { get; set; }
        public double ExtentWidth => _extent.Width;
        public double ExtentHeight => _extent.Height;
        public double ViewportWidth => _viewport.Width;
        public double ViewportHeight => _viewport.Height;
        public double HorizontalOffset => 0;
        public double VerticalOffset => _offset;

        private void SetScrollInfo(Size extent, Size viewport)
        {
            bool changed = extent != _extent || viewport != _viewport;
            _extent = extent;
            _viewport = viewport;
            double max = Math.Max(0, extent.Height - viewport.Height);
            if (_offset > max) { _offset = max; changed = true; }
            if (changed) ScrollOwner?.InvalidateScrollInfo();
        }

        public void SetVerticalOffset(double offset)
        {
            if (double.IsNaN(offset)) return;
            offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
            if (Math.Abs(offset - _offset) < 0.01) return;
            _offset = offset;
            ScrollOwner?.InvalidateScrollInfo();
            InvalidateMeasure();
        }

        public void SetHorizontalOffset(double offset) { }
        public void LineUp() => SetVerticalOffset(_offset - 16);
        public void LineDown() => SetVerticalOffset(_offset + 16);
        public void LineLeft() { }
        public void LineRight() { }
        public void MouseWheelUp() => SetVerticalOffset(_offset - 48);
        public void MouseWheelDown() => SetVerticalOffset(_offset + 48);
        public void MouseWheelLeft() { }
        public void MouseWheelRight() { }
        public void PageUp() => SetVerticalOffset(_offset - _viewport.Height);
        public void PageDown() => SetVerticalOffset(_offset + _viewport.Height);
        public void PageLeft() { }
        public void PageRight() { }

        public Rect MakeVisible(Visual visual, Rect rectangle)
        {
            DependencyObject? d = visual;
            while (d is not null && !ReferenceEquals(VisualTreeHelper.GetParent(d), this))
                d = VisualTreeHelper.GetParent(d);
            if (d is UIElement child)
            {
                int at = InternalChildren.IndexOf(child);
                if (at >= 0)
                    EnsureVisible(ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(at, 0)));
            }
            return rectangle;
        }

        /// <summary>Scrolls the least that shows the row of entry <paramref name="index"/> whole.</summary>
        private void EnsureVisible(int index)
        {
            int count = ItemCount;
            if (index < 0 || index >= count) return;
            double cellH = Math.Max(1, CellHeight);
            double top = SpreadRows.RowOf(index, BookLayout) * cellH;
            if (top < _offset) SetVerticalOffset(top);
            else if (top + cellH > _offset + _viewport.Height) SetVerticalOffset(top + cellH - _viewport.Height);
        }
    }
}
