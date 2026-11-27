// MainWindow — the two-column Pages sidebar (#246).
// An optional layout of the Pages list that puts two thumbnails on a row. The list still holds one
// entry per PAGE, so selection, Shift/Ctrl multi-select, Split / Delete / Rotate, drag-reorder and
// RecordNavJump are all unchanged: only the panel that places the entries differs
// (Controls/PairedThumbnailPanel.cs), plus the keyboard step and the drop target, which are now
// two-dimensional. The single column stays the default, and its VirtualizingStackPanel is only
// swapped out while this is on.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TDPdf.Controls;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        private bool _pairedSidebar;
        private double _thumbWidth = SidebarWidth.ThumbDefault;
        private PairedThumbnailPanel? _pairedPanel;

        /// <summary>Restores the persisted choice and starts following the sidebar's size. Called from the constructor after the app scale.</summary>
        private void InitPairedSidebar()
        {
            PageList.AddHandler(PairedThumbnailPanel.CellWidthChangedEvent, new RoutedEventHandler(PairedPanel_CellWidthChanged));
            _sidebarListHost.SizeChanged += (_, e) =>
            {
                // The single column shows one thumbnail per row, as wide as the sidebar allows. (A
                // sidebar squeezed to its strip says nothing about the thumbnails' size.)
                if (!_pairedSidebar && e.NewSize.Width >= 60) UpdateThumbWidth(e.NewSize.Width);
            };
            bool on = false;
            try { on = TDPdf.Properties.Settings.Default.SidebarTwoColumns; }
            catch { /* non-critical user preference */ }
            // Always run it: even with the single column the width cap is now the thumbnails' full size.
            SetPairedSidebar(on, persist: false);
        }

        private void PairedSidebar_Click(object sender, RoutedEventArgs e) => SetPairedSidebar(!_pairedSidebar, persist: true);

        /// <summary>Turns the two-column Pages sidebar on or off.</summary>
        private void SetPairedSidebar(bool on, bool persist)
        {
            bool was = _pairedSidebar;
            _pairedSidebar = on;
            if (FindName("PairedSidebarMenuItem") is MenuItem menu) menu.IsChecked = on;
            if (persist)
            {
                try
                {
                    TDPdf.Properties.Settings.Default.SidebarTwoColumns = on;
                    TDPdf.Properties.Settings.Default.Save();
                }
                catch { /* non-critical user preference */ }
            }

            // The width the sidebar may be dragged to: two full-size thumbnails, or one.
            _sidebarCol.MaxWidth = SbPx(SidebarWidth.Max(on));
            if (on != was || !_sidebarWidthPrimed)
            {
                _sidebarWidthPrimed = true;
                double want = SbPx(SidebarWidth.Default(on));
                if (on)
                {
                    // Room for two thumbnails at the usual size, but never narrower than it already is.
                    // The width the rail comes back to is raised too, whether it is open or not.
                    _sidebarExpandedWidth = Math.Max(_sidebarExpandedWidth, want);
                    if (!_sidebarCollapsed && (_sidebarCol.ActualWidth < want || !IsLoaded))
                    {
                        FinishSidebarAnimation();
                        _sidebarCol.Width = new GridLength(Math.Max(want, IsLoaded ? _sidebarCol.ActualWidth : 0));
                    }
                }
                else if (was)
                {
                    double cap = SbPx(SidebarWidth.Max(false));
                    if (_sidebarCollapsed) _sidebarExpandedWidth = Math.Min(_sidebarExpandedWidth, cap);
                    else
                    {
                        FinishSidebarAnimation();
                        _sidebarCol.Width = new GridLength(Math.Min(_sidebarCol.ActualWidth > 0 ? _sidebarCol.ActualWidth : want, cap));
                    }
                }
            }

            if (_ctx.Thumbs.Session is { } session) session.PairedCells = on;
            if (on)
            {
                var factory = new FrameworkElementFactory(typeof(PairedThumbnailPanel));
                factory.SetValue(PairedThumbnailPanel.CellHeightProperty, SidebarWidth.PairedCellHeight(_thumbWidth));
                factory.SetValue(PairedThumbnailPanel.BookLayoutProperty, _twoPageBook);
                PageList.ItemsPanel = new ItemsPanelTemplate(factory);
            }
            else if (was)
            {
                _pairedPanel = null;
                PageList.ClearValue(ItemsControl.ItemsPanelProperty);   // back to the style's VirtualizingStackPanel
            }
            if (on != was) RefreshThumbLayout();
            // Back to one column: the sidebar may not change size at all, so nothing else would tell
            // the thumbnails they can use the whole width again.
            if (!on && was)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
                {
                    if (!_pairedSidebar && _sidebarListHost.ActualWidth >= 60) UpdateThumbWidth(_sidebarListHost.ActualWidth);
                }));

            // The new panel starts at the top; put the current page back in view once it has laid out.
            if (on != was && PageList.SelectedItem is { } selected)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => { if (PageList.SelectedItem is not null) PageList.ScrollIntoView(selected); }));
        }

        private bool _sidebarWidthPrimed;

        /// <summary>Book layout changed: the sidebar pairs its entries the same way.</summary>
        private void SyncPairedSidebarBook()
        {
            if (_pairedPanel is { } panel) panel.BookLayout = _twoPageBook;
        }

        private void PairedPanel_CellWidthChanged(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not PairedThumbnailPanel panel) return;
            _pairedPanel = panel;
            panel.BookLayout = _twoPageBook;
            if (_pairedSidebar) UpdateThumbWidth(panel.CellWidth);
        }

        /// <summary>
        /// Sizes the sidebar thumbnails to the column they sit in, between their default and full
        /// size. <paramref name="columnWidth"/> is the logical width of one column of entries.
        /// </summary>
        private void UpdateThumbWidth(double columnWidth)
        {
            double w = SidebarWidth.ThumbWidthFor(columnWidth);
            if (Math.Abs(w - _thumbWidth) < 1) return;
            _thumbWidth = w;
            RefreshThumbLayout();
        }

        /// <summary>Pushes the current thumbnail width to the live list and its panel.</summary>
        private void RefreshThumbLayout()
        {
            if (_ctx.Thumbs.Session is { } session)
            {
                session.ThumbWidth = _thumbWidth;
                session.PairedCells = _pairedSidebar;
            }
            if (_thumbItems is not null)
                foreach (var item in _thumbItems)
                    if (item.Realised) item.RefreshLayout();
            if (_pairedSidebar && _pairedPanel is { } panel)
                panel.CellHeight = SidebarWidth.PairedCellHeight(_thumbWidth);
        }

        // ── Keyboard ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// With the sidebar focused, Up and Down move a row (the same column of the next pair of
        /// pages) instead of one page; Left and Right keep stepping one page. True when handled.
        /// </summary>
        private bool TryPairedSidebarKeys(KeyEventArgs e)
        {
            if (!_pairedSidebar || _doc is null || !PageList.IsKeyboardFocusWithin) return false;
            if (Keyboard.Modifiers != ModifierKeys.None || (e.Key != Key.Up && e.Key != Key.Down)) return false;
            int count = PageList.Items.Count;
            if (count < 2) return false;
            int from = Math.Max(0, PageList.SelectedIndex);
            int to = SpreadRows.Vertical(from, count, _twoPageBook, e.Key == Key.Up ? -1 : 1);
            if (to != from && to >= 0)
            {
                PageList.SelectedIndex = to;
                PageList.ScrollIntoView(PageList.SelectedItem);
            }
            e.Handled = true;
            return true;
        }

        // ── Drag and drop ───────────────────────────────────────────────────────────────────────

        /// <summary>The panel to hit-test drops against, when the two-column layout is the live one.</summary>
        private PairedThumbnailPanel? LivePairedPanel()
            => _pairedSidebar && _pairedPanel is { IsLoaded: true } p ? p : null;

        /// <summary>
        /// The gap a drop at <paramref name="listPoint"/> (in PageList coordinates) inserts at, in the
        /// two-column list; null when it is not the live layout. The same call draws the line and
        /// performs the drop, so the two cannot disagree (#135).
        /// </summary>
        private int? PairedDropGap(Point listPoint)
        {
            if (LivePairedPanel() is not { } panel) return null;
            return panel.DropAt(PageList.TranslatePoint(listPoint, panel)).Anchor.Gap;
        }

        /// <summary>Draws the vertical insertion bar for a drop in the two-column list.</summary>
        private bool ShowPairedDropLine(Point listPoint)
        {
            if (LivePairedPanel() is not { } panel || PageList.Items.Count == 0) return false;
            var (_, line) = panel.DropAt(PageList.TranslatePoint(listPoint, panel));
            if (line.IsEmpty) { HidePageDropLine(); return true; }
            var top = panel.TranslatePoint(new Point(line.X, line.Y), _sidebarListHost);
            if (!IsFinite(top.X) || !IsFinite(top.Y) || top.Y + line.Height < 0 || top.Y > _sidebarListHost.ActualHeight)
            {
                HidePageDropLine();
                return true;
            }
            _pageDropLine.Width = 2;
            _pageDropLine.Height = line.Height;
            _pageDropLine.HorizontalAlignment = HorizontalAlignment.Left;
            _pageDropLine.VerticalAlignment = VerticalAlignment.Top;
            _pageDropLine.Margin = new Thickness(top.X - 1, top.Y, 0, 0);
            _pageDropLine.Visibility = Visibility.Visible;
            return true;
        }

        /// <summary>Puts the insertion line back to the horizontal bar the single column uses.</summary>
        private void ResetDropLineShape()
        {
            _pageDropLine.Width = double.NaN;
            _pageDropLine.Height = 2;
            _pageDropLine.HorizontalAlignment = HorizontalAlignment.Stretch;
            _pageDropLine.VerticalAlignment = VerticalAlignment.Top;
        }
    }
}
