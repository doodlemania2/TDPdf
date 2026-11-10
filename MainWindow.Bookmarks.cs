// MainWindow — bookmark editing (#133).
// Add / rename / child / reorder / retarget / delete for the document outline.
// Extracted verbatim from MainWindow.xaml.cs.

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Docnet.Core;
using Docnet.Core.Models;
using TDPdf.Diagnostics;
using TDPdf.Services;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        // ============================================================
        // Bookmark editing (#133): add / rename / child / reorder / retarget / delete
        // ============================================================

        /// <summary>Ties a TreeViewItem to its live PdfOutline, the collection that contains it, and
        /// the resolved target page (for click-to-navigate).</summary>
        private sealed class OutlineNodeRef
        {
            public readonly PdfSharpCore.Pdf.PdfOutline Outline;
            public readonly PdfSharpCore.Pdf.PdfOutlineCollection Parent;
            public readonly int PageIndex;
            public OutlineNodeRef(PdfSharpCore.Pdf.PdfOutline outline,
                                  PdfSharpCore.Pdf.PdfOutlineCollection parent, int pageIndex)
            { Outline = outline; Parent = parent; PageIndex = pageIndex; }
        }

        // PdfSharpCore cannot save a document opened read-only (owner-password / XRef-fallback opens),
        // so bookmark editing is hidden there rather than failing at save time.
        private bool CanEditBookmarks => _doc is not null && !_doc.IsReadOnly;

        // Multi-select. WPF's TreeView is hard single-select, so its built-in selection stays the
        // "primary" item and Ctrl/Shift clicks maintain this extra set on top. Keyed by PdfOutline so
        // the selection survives tree rebuilds within one document.
        private readonly HashSet<PdfSharpCore.Pdf.PdfOutline> _bmExtraSel = new();
        private bool _suppressOutlineNav;
        private bool _bmRenaming;   // an inline rename box owns the keyboard — window paging keys stand down

        /// <summary>All bookmark rows in visual order (optionally only rows currently visible, i.e.
        /// with every ancestor expanded). The ghost add-row is never included.</summary>
        private static void FlattenBookmarkItems(ItemCollection items, bool visibleOnly,
                                                 List<(TreeViewItem Item, OutlineNodeRef Ref)> into)
        {
            foreach (TreeViewItem it in items)
            {
                if (it.Tag is OutlineNodeRef r) into.Add((it, r));
                if (!visibleOnly || it.IsExpanded)
                    FlattenBookmarkItems(it.Items, visibleOnly, into);
            }
        }

        /// <summary>Paints/clears the extra-selection look. The item template's IsSelected trigger
        /// drives Bd.Background/BorderBrush + Foreground; extras set the same three locally (local
        /// values outrank template triggers) and ClearValue restores normal styling.</summary>
        private void ApplyExtraSelectionVisuals()
        {
            var all = new List<(TreeViewItem Item, OutlineNodeRef Ref)>();
            FlattenBookmarkItems(_outlineTree.Items, visibleOnly: false, all);
            foreach (var (it, r) in all)
            {
                it.ApplyTemplate();
                var bd = it.Template?.FindName("Bd", it) as Border;
                if (_bmExtraSel.Contains(r.Outline))
                {
                    if (bd is not null)
                    {
                        bd.Background = BrushResource("AccentGreenDim");
                        bd.BorderBrush = BrushResource("AccentGreen");
                    }
                    it.Foreground = Brushes.White;   // matches the IsSelected trigger
                }
                else
                {
                    if (bd is not null)
                    {
                        bd.ClearValue(Border.BackgroundProperty);
                        bd.ClearValue(Border.BorderBrushProperty);
                    }
                    it.ClearValue(ForegroundProperty);
                }
            }
        }

        private void ClearBookmarkMultiSelection()
        {
            if (_bmExtraSel.Count == 0) return;
            _bmExtraSel.Clear();
            ApplyExtraSelectionVisuals();
        }

        // True when the click landed on the expand/collapse toggle - those pass through untouched.
        private static bool IsExpanderClick(DependencyObject? d)
        {
            while (d is not null && d is not TreeViewItem)
            {
                if (d is System.Windows.Controls.Primitives.ToggleButton) return true;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private void OutlineTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsExpanderClick(e.OriginalSource as DependencyObject)) return;
            var tvi = OutlineItemAt(e.OriginalSource as DependencyObject);
            bool ctrl  = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (tvi?.Tag is not OutlineNodeRef nref || !CanEditBookmarks || (!ctrl && !shift))
            {
                // Plain click, ghost row, or empty space: default single-selection behaviour.
                ClearBookmarkMultiSelection();
                return;
            }
            if (ctrl)
            {
                // Fold the primary into the set so the whole selection lives in one place, then toggle.
                if (_outlineTree.SelectedItem is TreeViewItem prim && prim.Tag is OutlineNodeRef pr)
                    _bmExtraSel.Add(pr.Outline);
                if (!_bmExtraSel.Add(nref.Outline)) _bmExtraSel.Remove(nref.Outline);
            }
            else
            {
                // Shift: range from the primary to the clicked row, in visible order.
                _bmExtraSel.Clear();
                var flat = new List<(TreeViewItem Item, OutlineNodeRef Ref)>();
                FlattenBookmarkItems(_outlineTree.Items, visibleOnly: true, flat);
                var primary = (_outlineTree.SelectedItem as TreeViewItem)?.Tag as OutlineNodeRef;
                int ia = primary is null ? -1 : flat.FindIndex(t => ReferenceEquals(t.Ref, primary));
                int ib = flat.FindIndex(t => ReferenceEquals(t.Item, tvi));
                if (ib < 0) return;
                if (ia < 0) ia = ib;
                for (int k = Math.Min(ia, ib); k <= Math.Max(ia, ib); k++)
                    _bmExtraSel.Add(flat[k].Ref.Outline);
            }
            ApplyExtraSelectionVisuals();
            e.Handled = true;   // keep the built-in primary selection where it is
        }

        private void OutlineTree_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!CanEditBookmarks) return;
            if (e.OriginalSource is TextBox) return;   // inline rename in progress: keys edit text, not bookmarks
            var primary = (_outlineTree.SelectedItem as TreeViewItem)?.Tag as OutlineNodeRef;
            if (e.Key == Key.Delete && (primary is not null || _bmExtraSel.Count > 0))
            {
                e.Handled = true;
                DeleteSelectedBookmarks(primary);
            }
            else if (e.Key == Key.F2 && primary is not null && _outlineTree.SelectedItem is TreeViewItem tvi)
            {
                e.Handled = true;
                BeginInlineRename(tvi, primary);
            }
        }

        /// <summary>The add action lives as a dim first row inside the tree itself: a + glyph and
        /// "Add bookmark", brightening on hover. Tag stays null so the selection handler, context
        /// menu, and refresh walks all treat it as a non-bookmark row.</summary>
        private TreeViewItem BuildAddBookmarkGhostRow()
        {
            var icon = new TextBlock
            {
                Text = "\uE710",   // Segoe MDL2 Add
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            };
            var text = new TextBlock { Text = "Add bookmark", VerticalAlignment = VerticalAlignment.Center };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0.55 };
            panel.Children.Add(icon);
            panel.Children.Add(text);
            var item = new TreeViewItem
            {
                Header = panel,
                ToolTip = "Add a bookmark pointing at the current page",
                Style = (Style)FindResource("OutlineItemStyle"),
            };
            item.MouseEnter += (_, _2) => panel.Opacity = 1.0;
            item.MouseLeave += (_, _2) => panel.Opacity = 0.55;
            item.PreviewMouseLeftButtonUp += (_, ev) => { ev.Handled = true; AddBookmarkInto(null); };
            return item;
        }

        /// <summary>Adds a bookmark pointing at the current page - to the root list, or as a child of
        /// <paramref name="parent"/> - titled "Page N", then drops straight into an inline rename of
        /// the new entry (no dialog). Esc keeps the default title.</summary>
        private void AddBookmarkInto(OutlineNodeRef? parent)
        {
            if (!CanEditBookmarks || _doc is null) return;
            if (parent is not null && !ReferenceEquals(parent.Outline.Owner, _doc)) { LoadOutlines(); return; }   // stale ref
            int page = Math.Max(0, PageList.SelectedIndex);
            if (page >= _doc.PageCount) page = _doc.PageCount - 1;
            if (page < 0) return;
            PushDocUndo();   // bookmark ops ride the document-snapshot undo like crop / page ops do
            var col = parent is null ? _doc.Outlines : parent.Outline.Outlines;
            var added = col.Add($"Page {page + 1}", _doc.Pages[page], true);
            ScrubStaleOutlineLinkKeys();
            MarkDirty();
            RefreshOutlines();
            if (FindOutlineItem(_outlineTree.Items, added) is { } tvi && tvi.Tag is OutlineNodeRef nref)
            {
                tvi.BringIntoView();
                BeginInlineRename(tvi, nref);
            }
        }

        /// <summary>Swaps a tree item's header for an inline TextBox (rename-in-place; also used right
        /// after adding). Enter or clicking elsewhere commits, Esc cancels.</summary>
        private void BeginInlineRename(TreeViewItem tvi, OutlineNodeRef nref)
        {
            if (!CanEditBookmarks) return;
            if (!ReferenceEquals(nref.Outline.Owner, _doc)) { LoadOutlines(); return; }   // stale ref
            string current = FixRawUnicodeTitle(nref.Outline.Title ?? string.Empty);
            var box = new TextBox
            {
                Text = current,
                MinWidth = 110,
                FontSize = _outlineTree.FontSize,
                FontFamily = new FontFamily("Segoe UI"),
                Padding = new Thickness(3, 1, 3, 1),
                Background = BrushResource("BgPanel"),
                Foreground = BrushResource("TextPrimary"),
                BorderBrush = BrushResource("AccentGreen"),   // accent border = active in-place edit
                BorderThickness = new Thickness(1),
                CaretBrush = BrushResource("AccentGreen"),
                SelectionBrush = BrushResource("AccentGreenDim"),
                FocusVisualStyle = null,
            };
            bool done = false;
            void Commit()
            {
                if (done) return;
                done = true;
                _bmRenaming = false;
                string t = box.Text.Trim();
                if (t.Length > 0 && t != current)
                {
                    PushDocUndo();
                    nref.Outline.Title = t;   // the setter writes a proper Unicode string, healing mojibake entries
                    MarkDirty();
                    RefreshOutlines();
                }
                else
                    tvi.Header = string.IsNullOrEmpty(current) ? "(untitled)" : current;
            }
            void Cancel()
            {
                if (done) return;
                done = true;
                _bmRenaming = false;
                tvi.Header = string.IsNullOrEmpty(current) ? "(untitled)" : current;
            }
            box.PreviewKeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter)  { ke.Handled = true; Commit(); }
                if (ke.Key == Key.Escape) { ke.Handled = true; Cancel(); }
            };
            box.LostFocus += (_, _2) => Commit();
            _bmRenaming = true;
            tvi.Header = box;
            // The box can't take focus until it has been laid out - focus it after render.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                (Action)(() => { box.Focus(); box.SelectAll(); }));
        }

        /// <summary>Finds the tree item for a PdfOutline, expanding collapsed ancestors on the way.</summary>
        private static TreeViewItem? FindOutlineItem(ItemCollection items, object outline)
        {
            foreach (TreeViewItem it in items)
            {
                if (it.Tag is OutlineNodeRef r && ReferenceEquals(r.Outline, outline)) return it;
                if (FindOutlineItem(it.Items, outline) is { } hit) { it.IsExpanded = true; return hit; }
            }
            return null;
        }

        /// <summary>Deletes the multi-selection if one exists, plus the clicked/primary item. One
        /// confirm covers the whole set; one undo entry restores it.</summary>
        private void DeleteSelectedBookmarks(OutlineNodeRef? clicked)
        {
            if (!CanEditBookmarks) return;
            if (clicked is not null && !ReferenceEquals(clicked.Outline.Owner, _doc)) { LoadOutlines(); return; }   // stale ref

            // Gather targets: the extra set, the primary, and the clicked item, deduplicated.
            var all = new List<(TreeViewItem Item, OutlineNodeRef Ref)>();
            FlattenBookmarkItems(_outlineTree.Items, visibleOnly: false, all);
            var targets = new List<OutlineNodeRef>();
            foreach (var (_, r) in all)
                if (_bmExtraSel.Contains(r.Outline)) targets.Add(r);
            void AddTarget(OutlineNodeRef? r)
            {
                if (r is not null && !targets.Any(t => ReferenceEquals(t.Outline, r.Outline))) targets.Add(r);
            }
            AddTarget((_outlineTree.SelectedItem as TreeViewItem)?.Tag as OutlineNodeRef);
            AddTarget(clicked);
            if (targets.Count == 0) return;

            // A target with a selected ancestor is covered by deleting the ancestor - drop it so the
            // remaining targets are independent (their parent collections stay valid during removal).
            var chosen = new HashSet<object>(targets.Select(t => (object)t.Outline));
            bool Covered(PdfSharpCore.Pdf.PdfOutline o)
            {
                for (var p = o.Parent; p is not null; p = p.Parent)
                    if (chosen.Contains(p)) return true;
                return false;
            }
            targets = targets.Where(t => !Covered(t.Outline)).ToList();

            int total = targets.Sum(t => 1 + CountOutlines(t.Outline.Outlines));
            if (total > 1)
            {
                string msg = targets.Count == 1
                    ? $"Delete this bookmark and its {total - 1} child bookmark(s)?"
                    : $"Delete {total} bookmarks?";
                var r = TdpDialog.Show(this, msg, "TDPdf", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }
            PushDocUndo();   // one Ctrl+Z restores the whole set
            foreach (var t in targets)
                RemoveOutlineRecursive(t.Parent, t.Outline);
            ScrubStaleOutlineLinkKeys();
            MarkDirty();
            RefreshOutlines();   // also clears _bmExtraSel via LoadOutlines
        }

        /// <summary>Moves a bookmark one position up or down among its siblings.</summary>
        private void MoveBookmark(OutlineNodeRef nref, int delta)
        {
            if (!CanEditBookmarks) return;
            if (!ReferenceEquals(nref.Outline.Owner, _doc)) { LoadOutlines(); return; }   // stale ref
            int i = nref.Parent.IndexOf(nref.Outline);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= nref.Parent.Count) return;
            PushDocUndo();
            // RemoveAt drops the object from the xref table; Insert/Add puts it straight back.
            nref.Parent.RemoveAt(i);
            if (j >= nref.Parent.Count) nref.Parent.Add(nref.Outline);
            else nref.Parent.Insert(j, nref.Outline);
            ScrubStaleOutlineLinkKeys();
            MarkDirty();
            RefreshOutlines();
            // Keep the moved item selected, without the page-jump side effect.
            if (FindOutlineItem(_outlineTree.Items, nref.Outline) is { } moved)
            {
                _suppressOutlineNav = true;
                try { moved.IsSelected = true; moved.BringIntoView(); }
                finally { _suppressOutlineNav = false; }
            }
        }

        /// <summary>Repoints a bookmark at the current page as a plain go-to-page destination.</summary>
        private void SetBookmarkDestination(OutlineNodeRef nref)
        {
            if (!CanEditBookmarks || _doc is null) return;
            if (!ReferenceEquals(nref.Outline.Owner, _doc)) { LoadOutlines(); return; }   // stale ref
            int page = Math.Max(0, PageList.SelectedIndex);
            if (page >= _doc.PageCount) page = _doc.PageCount - 1;
            if (page < 0) return;
            PushDocUndo();
            nref.Outline.DestinationPage = _doc.Pages[page];
            // Plain jump: /XYZ null null null keeps the reader's current zoom / position behaviour.
            nref.Outline.PageDestinationType = PdfSharpCore.Pdf.PdfPageDestinationType.Xyz;
            nref.Outline.Left = double.NaN;
            nref.Outline.Top = double.NaN;
            nref.Outline.Zoom = double.NaN;
            MarkDirty();
            RefreshOutlines();
        }

        /// <summary>Removes every bookmark in the document (one confirm, one undo entry).</summary>
        private void DeleteAllBookmarks()
        {
            if (!CanEditBookmarks || _doc is null) return;
            if (!_doc.Internals.Catalog.Elements.ContainsKey("/Outlines")) return;   // nothing to do, and never plant one
            if (_doc.Outlines.Count == 0) return;
            var r = TdpDialog.Show(this, "Delete all bookmarks in this document?", "TDPdf",
                                   MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            PushDocUndo();
            while (_doc.Outlines.Count > 0)
                RemoveOutlineRecursive(_doc.Outlines, _doc.Outlines[_doc.Outlines.Count - 1]);
            ScrubStaleOutlineLinkKeys();
            MarkDirty();
            RefreshOutlines();
        }

        private static int CountOutlines(PdfSharpCore.Pdf.PdfOutlineCollection col)
        {
            int n = 0;
            foreach (PdfSharpCore.Pdf.PdfOutline o in col) n += 1 + CountOutlines(o.Outlines);
            return n;
        }

        // Bottom-up: Collection.Remove() drops the removed object from the document's reference table,
        // so deleting the whole branch leaf-first leaves no orphaned outline objects (with dangling
        // /Parent refs) behind in the saved file.
        private static void RemoveOutlineRecursive(PdfSharpCore.Pdf.PdfOutlineCollection parent,
                                                   PdfSharpCore.Pdf.PdfOutline outline)
        {
            while (outline.Outlines.Count > 0)
                RemoveOutlineRecursive(outline.Outlines, outline.Outlines[outline.Outlines.Count - 1]);
            parent.Remove(outline);
        }

        // PdfSharpCore's PrepareForSave rebuilds outline linkage keys (/First /Last /Next /Prev
        // /Parent /Count) from the in-memory collections on save, but never REMOVES entries that no
        // longer apply (an item that became last keeps its old /Next, an emptied parent keeps
        // /First /Last). After any bookmark edit, strip those keys on the CHILD nodes so the writer
        // rebuilds them cleanly. (Deviation from upstream: we deliberately do NOT strip the root
        // outline dict's /First — the save-time ScrubEmptyOutlines uses root /First to decide whether
        // to drop a dangling /Outlines, and the writer rewrites the root's linkage anyway.) When the
        // tree has been fully emptied we remove the catalog /Outlines entry outright, so the raw
        // in-memory saves the snapshot-undo takes never serialize a dangling reference (#103).
        private void ScrubStaleOutlineLinkKeys()
        {
            if (_doc is null) return;
            try
            {
                if (!_doc.Internals.Catalog.Elements.ContainsKey("/Outlines")) return;
                if (_doc.Outlines.Count == 0)
                {
                    _doc.Internals.Catalog.Elements.Remove("/Outlines");
                    return;
                }
                ScrubOutlineLinkKeys(_doc.Outlines);
            }
            catch { /* malformed outline tree - the save-time scrubs are the backstop */ }
        }

        private static void ScrubOutlineLinkKeys(PdfSharpCore.Pdf.PdfOutlineCollection col)
        {
            foreach (PdfSharpCore.Pdf.PdfOutline o in col)
            {
                o.Elements.Remove("/First");
                o.Elements.Remove("/Last");
                o.Elements.Remove("/Next");
                o.Elements.Remove("/Prev");
                o.Elements.Remove("/Parent");
                o.Elements.Remove("/Count");
                ScrubOutlineLinkKeys(o.Outlines);
            }
        }

        /// <summary>Rebuilds the outline panel after an edit, keeping collapsed branches collapsed
        /// (the PdfOutline objects survive the rebuild, so they key the state).</summary>
        private void RefreshOutlines()
        {
            var collapsed = new HashSet<object>();
            void Capture(ItemCollection items)
            {
                foreach (TreeViewItem it in items)
                {
                    if (!it.IsExpanded && it.Tag is OutlineNodeRef r) collapsed.Add(r.Outline);
                    Capture(it.Items);
                }
            }
            Capture(_outlineTree.Items);
            LoadOutlines();
            if (collapsed.Count > 0)
            {
                void Restore(ItemCollection items)
                {
                    foreach (TreeViewItem it in items)
                    {
                        if (it.Tag is OutlineNodeRef r && collapsed.Contains(r.Outline)) it.IsExpanded = false;
                        Restore(it.Items);
                    }
                }
                Restore(_outlineTree.Items);
            }
            // A bookmark edit shifts index paths, so the object-keyed restore above is the authority
            // here — re-baseline the path-keyed session state from the tree it just produced, or the
            // next tab switch would replay stale paths over the edited outline.
            CaptureOutlineExpandState();
        }

        /// <summary>Right-click on the outline panel: bookmark menu for the item under the cursor, or
        /// the add-bookmark menu on empty space. Hidden entirely on read-only documents.</summary>
        private void OutlineTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!CanEditBookmarks) return;
            var tvi = OutlineItemAt(e.OriginalSource as DependencyObject);
            var menu = new ContextMenu();
            TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(menu, TextRenderingMode.Grayscale);
            if (tvi?.Tag is OutlineNodeRef nref)
            {
                // Right-click outside the multi-selection collapses it to the clicked item (the
                // file-explorer convention); inside it, the menu acts on the whole set.
                bool inMulti = _bmExtraSel.Contains(nref.Outline);
                if (!inMulti) ClearBookmarkMultiSelection();
                _suppressOutlineNav = true;
                try { tvi.IsSelected = true; }   // WPF doesn't select on right-click by itself
                finally { _suppressOutlineNav = false; }

                if (inMulti && _bmExtraSel.Count > 1)
                {
                    menu.Items.Add(MakeMenuItem($"Delete ({_bmExtraSel.Count})",
                                                (_, _2) => DeleteSelectedBookmarks(nref), "Delete", null, "\uE74D"));
                }
                else
                {
                    menu.Items.Add(MakeMenuItem("_Rename", (_, _2) => BeginInlineRename(tvi, nref), "F2", null, "\uE8AC"));
                    menu.Items.Add(MakeMenuItem("Add _child bookmark", (_, _2) => AddBookmarkInto(nref), null, null, "\uE710"));
                    menu.Items.Add(MakeMenuItem("Set destination to current page", (_, _2) => SetBookmarkDestination(nref), null, null, "\uE718"));
                    menu.Items.Add(new Separator());
                    int idx = nref.Parent.IndexOf(nref.Outline);
                    var up = MakeMenuItem("Move _up", (_, _2) => MoveBookmark(nref, -1), null, null, "\uE74A");
                    up.IsEnabled = idx > 0;
                    menu.Items.Add(up);
                    var down = MakeMenuItem("Move _down", (_, _2) => MoveBookmark(nref, +1), null, null, "\uE74B");
                    down.IsEnabled = idx >= 0 && idx < nref.Parent.Count - 1;
                    menu.Items.Add(down);
                    menu.Items.Add(new Separator());
                    menu.Items.Add(MakeMenuItem("_Delete", (_, _2) => DeleteSelectedBookmarks(nref), "Delete", null, "\uE74D"));
                }
            }
            else
            {
                menu.Items.Add(MakeMenuItem("_Add bookmark", (_, _2) => AddBookmarkInto(null), null, null, "\uE710"));
                bool hasAny = _doc?.Internals.Catalog.Elements.ContainsKey("/Outlines") == true
                              && _outlineTree.Items.Count > 1;   // ghost row + at least one real entry
                if (hasAny)
                {
                    menu.Items.Add(new Separator());
                    menu.Items.Add(MakeMenuItem("Delete all bookmarks", (_, _2) => DeleteAllBookmarks(), null, null, "\uE74D"));
                }
            }
            menu.PlacementTarget = _outlineTree;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private static TreeViewItem? OutlineItemAt(DependencyObject? d)
        {
            while (d is not null && d is not TreeViewItem)
                d = d is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);   // e.g. a Run inside the header
            return d as TreeViewItem;
        }

        private void SidebarPagesTab_Checked(object sender, RoutedEventArgs e)
        {
            // Fires during XAML load before manual refs are assigned — guard.
            if (_outlineScrollViewer is null) return;
            _outlineScrollViewer.Visibility = Visibility.Collapsed;
            SidebarScrollViewer.Visibility = Visibility.Visible;
            _pageControlsRow.Visibility = Visibility.Visible;
        }

        private void SidebarOutlinesTab_Checked(object sender, RoutedEventArgs e)
        {
            if (_outlineScrollViewer is null) return;
            SidebarScrollViewer.Visibility = Visibility.Collapsed;
            _outlineScrollViewer.Visibility = Visibility.Visible;
            _pageControlsRow.Visibility = Visibility.Collapsed;
        }

        // ============================================================
        // Document outline / bookmarks (sidebar OUTLINES tab)  —  #133
        //
        // Editable bookmark tree ported from upstream KillerPDF v1.6.4. TDPdf keeps its own
        // multi-tab document model, TdpDialog dark dialogs, theme brushes, and (crucially) its
        // full-document-snapshot undo (PushDocUndo), so this is an adaptation, not a copy:
        //   • ListBox → themed TreeView (OutlineTreeStyle / OutlineItemStyle / OutlineExpander).
        //   • Edits mutate the live PdfSharpCore outline object model (_doc.Outlines) so they are
        //     written on save; page-index for display still resolves through TDPdf's ResolveDest so
        //     named / GoTo destinations navigate exactly as before.
        //   • Every mutation rides the existing document-snapshot undo (PushDocUndo) — one Ctrl+Z
        //     restores the whole edit — rather than a bookmark-specific undo stack.
        // ============================================================

        /// <summary>
        /// (Re)builds the OUTLINES tree from the active document's live PdfSharpCore outline
        /// collection. Safe to call on a null / read-only / outline-less document.
        /// </summary>
        private void LoadOutlines()
        {
            // A rebuild drives IsExpanded itself; the live Expanded/Collapsed recorder must only ever
            // hear the USER, or a rebuild would be recorded as a user choice against whichever tab
            // happens to be active at the time.
            _outlineRebuilding = true;
            try { LoadOutlinesCore(); }
            finally { _outlineRebuilding = false; }
        }

        private void LoadOutlinesCore()
        {
            _bmExtraSel.Clear();          // outlines may be gone after a rebuild / undo
            _outlineTree.Items.Clear();
            if (_doc is null)
            {
                _sidebarOutlinesTab.IsEnabled = false;
                if (_sidebarOutlinesTab.IsChecked == true) _sidebarPagesTab.IsChecked = true;
                return;
            }
            try
            {
                // #103: _doc.Outlines lazily CREATES an empty outlines object on documents that have
                // none, and PdfSharpCore then emits a dangling /Outlines reference the reopen rejects.
                // Peek at the catalog read-only and only touch .Outlines when one really exists.
                bool hasOutlines = _doc.Internals.Catalog.Elements.ContainsKey("/Outlines");
                var outlines = hasOutlines ? _doc.Outlines : null;
                if (outlines is null || outlines.Count == 0)
                {
                    // Stay enabled on an editable document so the user can open the panel and add a
                    // first bookmark (the ghost add-row is then the only entry); read-only documents
                    // keep the old "disabled when empty" gating.
                    _sidebarOutlinesTab.IsEnabled = CanEditBookmarks;
                    if (CanEditBookmarks) _outlineTree.Items.Add(BuildAddBookmarkGhostRow());
                    else if (_sidebarOutlinesTab.IsChecked == true) _sidebarPagesTab.IsChecked = true;
                    return;
                }
                _sidebarOutlinesTab.IsEnabled = true;
                if (CanEditBookmarks) _outlineTree.Items.Add(BuildAddBookmarkGhostRow());
                AddOutlineItems(_outlineTree.Items, outlines);
                // Sticky expand/collapse per tab. LoadOutlines rebuilds from scratch on every tab
                // switch and temp-reload, which used to re-expand everything the user had folded.
                if (_ctx.OutlineExpandSeen) ApplyOutlineExpandState();
                else { CaptureOutlineExpandState(); _ctx.OutlineExpandSeen = true; }
            }
            catch
            {
                // A malformed outline tree must never break opening a document.
                _outlineTree.Items.Clear();
                _sidebarOutlinesTab.IsEnabled = false;
                if (_sidebarOutlinesTab.IsChecked == true) _sidebarPagesTab.IsChecked = true;
            }
        }

        /// <summary>
        /// #133 (upstream v1.6.4): PdfSharpCore's lexer decodes UTF-16 bookmark titles by their BOM,
        /// but strings it decrypts AFTER parsing (owner-password protected files) never get that BOM
        /// re-check, so the title arrives as raw bytes widened to chars: a U+00FE U+00FF prefix (the
        /// BOM bytes) followed by one char per byte (mojibake, most visible on Chinese outlines).
        /// Detect the widened BOM, re-pack the chars into bytes, and decode as UTF-16. Titles that
        /// parsed correctly don't start with those two chars and pass through untouched.
        /// </summary>
        private static string FixRawUnicodeTitle(string s)
        {
            if (s.Length < 2) return s;
            bool be = s[0] == 'þ' && s[1] == 'ÿ';   // UTF-16BE BOM as raw chars
            bool le = s[0] == 'ÿ' && s[1] == 'þ';   // UTF-16LE (Adobe tolerance)
            if (!be && !le) return s;
            foreach (char c in s)
                if (c > 'ÿ') return s;   // not byte-widened data - a real (odd) title, leave it
            var sb = new System.Text.StringBuilder((s.Length - 2) / 2);
            for (int i = 2; i + 1 < s.Length; i += 2)   // a trailing odd byte is dropped rather than corrupting the pairs
                sb.Append(be ? (char)((s[i] << 8) | s[i + 1])
                             : (char)((s[i + 1] << 8) | s[i]));
            return sb.ToString();
        }

        /// <summary>Builds a TreeViewItem per outline (recursing into children) tied to its live
        /// PdfOutline via <see cref="OutlineNodeRef"/>.</summary>
        private void AddOutlineItems(ItemCollection target, PdfSharpCore.Pdf.PdfOutlineCollection outlines,
                                     int depth = 0)
        {
            foreach (PdfSharpCore.Pdf.PdfOutline outline in outlines)
            {
                int pageIdx = GetOutlinePageIndex(outline);
                string title = FixRawUnicodeTitle(outline.Title ?? string.Empty);
                var item = new TreeViewItem
                {
                    Header = string.IsNullOrEmpty(title) ? "(untitled)" : title,
                    // Top level starts open, deeper levels start folded (the Acrobat default) — a deep
                    // outline was otherwise a wall of text on open, since this used to expand every
                    // node at every depth. ApplyOutlineExpandState overrides this with the user's own
                    // choices once this tab has built its tree once.
                    IsExpanded = depth == 0,
                    Tag = new OutlineNodeRef(outline, outlines, pageIdx),
                    ToolTip = pageIdx >= 0 ? $"Page {pageIdx + 1}" : null,
                    // Items are added as ready-made containers, so ItemContainerStyle doesn't apply;
                    // set the themed style explicitly.
                    Style = (Style)FindResource("OutlineItemStyle"),
                };
                if (outline.Outlines is not null && outline.Outlines.Count > 0)
                    AddOutlineItems(item.Items, outline.Outlines, depth + 1);
                target.Add(item);
            }
        }

        // ---- Sticky OUTLINES expand/collapse (per tab; state lives on DocumentContext) ------------
        // Nodes are keyed by index path ("2/0/1"), ghost add-row excluded, because SaveTempAndReload
        // reopens the document and hands back brand-new PdfOutline instances — an object-keyed set
        // would go stale on every structural edit. RefreshOutlines still keys ITS capture on the live
        // PdfOutline objects, which survive a bookmark edit and stay correct when indices shift.
        private bool _outlineRebuilding;

        /// <summary>Walks the on-screen outline items in index order, ghost add-row skipped, handing
        /// each one its index path. The single definition of a node's key.</summary>
        private static void WalkOutlinePaths(ItemCollection items, string prefix,
                                             Action<TreeViewItem, string> visit)
        {
            int i = 0;
            foreach (var o in items)
            {
                if (o is not TreeViewItem it || it.Tag is not OutlineNodeRef) continue;
                string path = prefix.Length == 0 ? i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                                 : prefix + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                visit(it, path);
                WalkOutlinePaths(it.Items, path, visit);
                i++;
            }
        }

        /// <summary>Records which outline nodes are expanded in the tree currently on screen, against
        /// the active tab. Runs once per tab, right after its first tree is built, so the depth
        /// defaults become the baseline the live recorder then edits.</summary>
        private void CaptureOutlineExpandState()
        {
            var expanded = _ctx.OutlineExpanded;
            expanded.Clear();
            WalkOutlinePaths(_outlineTree.Items, "", (it, path) => { if (it.IsExpanded) expanded.Add(path); });
        }

        /// <summary>Restores this tab's recorded expand/collapse state onto a freshly built tree.</summary>
        private void ApplyOutlineExpandState()
        {
            var expanded = _ctx.OutlineExpanded;
            WalkOutlinePaths(_outlineTree.Items, "", (it, path) => it.IsExpanded = expanded.Contains(path));
        }

        /// <summary>Live recorder for the user's own expand/collapse clicks, attached once to the tree
        /// (the items themselves are thrown away and rebuilt constantly). Keeps the state current no
        /// matter which of the many LoadOutlines callers rebuilds next, so nothing has to remember to
        /// capture first.</summary>
        private void OutlineTree_ItemExpandChanged(object sender, RoutedEventArgs e)
        {
            if (_outlineRebuilding || e.OriginalSource is not TreeViewItem it
                || it.Tag is not OutlineNodeRef) return;
            string? path = null;
            WalkOutlinePaths(_outlineTree.Items, "", (node, p) => { if (ReferenceEquals(node, it)) path = p; });
            if (path is null) return;
            if (it.IsExpanded) _ctx.OutlineExpanded.Add(path);
            else               _ctx.OutlineExpanded.Remove(path);
        }

        /// <summary>Resolves an outline's 0-based target page. Prefers PdfSharpCore's parsed
        /// destination page, then falls back to TDPdf's richer resolver (named destinations,
        /// /GoTo actions) so navigation matches the pre-edit behaviour.</summary>
        private int GetOutlinePageIndex(PdfSharpCore.Pdf.PdfOutline outline)
        {
            if (_doc is null) return -1;
            if (outline.DestinationPage is PdfSharpCore.Pdf.PdfPage destPage)
            {
                for (int i = 0; i < _doc.PageCount; i++)
                    if (ReferenceEquals(_doc.Pages[i], destPage)) return i;
            }
            // Fall back to TDPdf's resolver on the outline's own dictionary.
            PdfItem? destItem = outline.Elements["/Dest"];
            if (destItem is null)
            {
                var action = outline.Elements.GetDictionary("/A");
                if (action is not null &&
                    (action.Elements.GetName("/S") == "/GoTo" || action.Elements.ContainsKey("/D")))
                {
                    destItem = action.Elements["/D"];
                }
            }
            return ResolveDest(destItem) ?? -1;
        }

        private void OutlineTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_suppressOutlineNav) return;   // programmatic re-select (e.g. after a move) must not jump the view
            if (e.NewValue is TreeViewItem item && item.Tag is OutlineNodeRef nref
                && nref.PageIndex >= 0 && _doc is not null && nref.PageIndex < _doc.PageCount
                && PageList.SelectedIndex != nref.PageIndex)
            {
                RecordNavJump();   // bookmark jump — retraceable via Alt+Left
                PageList.SelectedIndex = nref.PageIndex;
            }
        }

        // The TreeView's own scroll viewer swallows the wheel before the outer one sees it, so the
        // Outlines panel wouldn't scroll. Forward the wheel to the outer scroll viewer.
        private void OutlineScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            _outlineScrollViewer.ScrollToVerticalOffset(_outlineScrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }

    }
}
