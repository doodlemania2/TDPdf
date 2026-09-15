// MainWindow — the page sidebar (PageList) and the page viewer, i.e. the
// "Multi-document tabs" region: switching the active DocumentContext and rebuilding
// the shared sidebar / viewer controls from it. Extracted verbatim from MainWindow.xaml.cs.

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
        // Multi-document tabs
        // ============================================================
        // The page sidebar (PageList), the page viewer (PagePreviewPanel),
        // and the annotation canvas are single shared controls. Switching tabs
        // swaps the active DocumentContext (_ctx) and rebuilds those controls
        // from it; the per-document model state follows automatically via the
        // forwarding properties (_doc, _annotations, _undoStack, …).

        private void EnsureActiveTabRegistered()
        {
            if (!_tabs.Contains(_ctx)) _tabs.Add(_ctx);
        }

        /// <summary>
        /// Single entry point for opening a PDF (Open dialog, drag-drop, command
        /// line, and cross-instance forwarding). Reuses the current tab when it
        /// holds no document yet, otherwise opens the file in a brand-new tab.
        /// </summary>
        private async Task OpenInTabAsync(string path)
        {
            EnsureActiveTabRegistered();
            var previous = _ctx;
            DocumentContext? created = null;
            if (_ctx.Doc is not null)
            {
                created = new DocumentContext();
                _tabs.Add(created);
                ActivateContext(created);
            }

            await OpenFileAsync(path);

            // If we spun up a brand-new tab but the open failed or was cancelled
            // (bad file, wrong password, …), drop the empty tab and return to the
            // previously active document instead of leaving a stray "Untitled" tab.
            if (created is not null && created.Doc is null)
            {
                _tabs.Remove(created);
                if (ReferenceEquals(_ctx, created))
                    ActivateContext(_tabs.Contains(previous) ? previous : _tabs[^1]);
            }
            RebuildTabStrip();
        }

        /// <summary>Captures the live view state of the active tab before switching away.</summary>
        private void CaptureViewState()
        {
            if (_ctx.Doc is null) return;
            _ctx.SelectedPageIndex = PageList.SelectedIndex;

            // #399: the rest of "where this tab was". The zoom is read off the app-global view
            // model because that IS this tab's zoom for as long as the tab is active; it stops
            // being shared the moment the tab goes into the background and its value is parked
            // here. Nothing recorded here is a preference — see the DocumentContext block.
            _ctx.ViewScrollH = PagePreviewPanel.HorizontalOffset;
            _ctx.ViewScrollV = PagePreviewPanel.VerticalOffset;
            _ctx.ViewZoomLevel = Zoom.ZoomLevel;
            _ctx.ViewFitMode = _zoomFitMode;
            _ctx.ViewManualZoomIntent = _manualZoomIntent;
            _ctx.ViewModeAtCapture = _viewMode;
            _ctx.ViewCaptured = true;
        }

        /// <summary>
        /// Re-applies the zoom a tab was left at (#399). A FIT is replayed as a fit and never as
        /// the number it once produced: the window may well have been resized — or dragged to
        /// another monitor — while this tab sat in the background, and replaying a raw number
        /// against different geometry is exactly the "opens enormous or microscopic" failure the
        /// #201 comment block in <see cref="ApplyViewModeOnOpen"/> exists to prevent. A deliberate
        /// manual zoom is window-independent by definition, so that one is replayed as a number.
        ///
        /// Grid is excluded for the same reason ApplyViewModeOnOpen excludes it: Grid's zoom is
        /// not a free number but a column count that RefreshPageView immediately snaps back, so
        /// replaying one only starts a fight it always loses.
        /// </summary>
        private void RestoreTabZoom(DocumentContext ctx)
        {
            if (_viewMode == ViewMode.Grid) return;
            _restoringTabZoom = true;
            try
            {
                if (ctx.ViewFitMode == ZoomFitMode.Width) FitToWidth();
                else if (ctx.ViewFitMode == ZoomFitMode.Page) FitToPage();
                else
                {
                    // The same three writes as ApplyRestoredManualZoom, except that the tab's own
                    // intent flag is carried back rather than forced true: resuming a tab must
                    // leave the zoom subsystem exactly as the user left it there, not stronger.
                    _zoomFitMode = ZoomFitMode.None;
                    _manualZoomIntent = ctx.ViewManualZoomIntent;
                    Zoom.SetZoomLevel(ctx.ViewZoomLevel);
                }
            }
            finally { _restoringTabZoom = false; }
        }

        /// <summary>
        /// Settles whatever a re-activated tab is still owed (#399). Called from the tail of the
        /// render that put that tab's page on screen.
        /// </summary>
        /// <remarks>
        /// The timing is the whole point of this method existing. A scroll offset handed to a
        /// ScrollViewer is clamped to the extent it knows about at that instant, and the extent of
        /// a page that has not been measured and arranged yet is zero — so an offset applied from
        /// ActivateContext, where the incoming page is still an un-rendered placeholder, does not
        /// fail loudly, it just silently becomes 0. It is applied here instead: after the bitmap,
        /// the canvases and the wrap panel have been sized, with an explicit UpdateLayout to force
        /// the pass rather than hope one has already run.
        ///
        /// Restoring a FIT re-renders, so the zoom is settled first and the scroll deliberately
        /// stays owed whenever the zoom actually moved — the next render's tail then applies it
        /// against the extent that zoom produced, instead of this one scrolling to an offset that
        /// is about to be wrong.
        /// </remarks>
        private void ApplyPendingViewResume()
        {
            // Continuous has its own anchor point — RenderContinuousPages re-scrolls once the slot
            // heights above the target page are final — so its offsets must not be applied here.
            if (_viewMode == ViewMode.Continuous) return;

            if (_resumeZoomFor is { } zoomCtx)
            {
                if (!ReferenceEquals(_ctx, zoomCtx)) { _resumeZoomFor = null; _resumeScroll = null; return; }
                _resumeZoomFor = null;
                double before = Zoom.ZoomLevel;
                RestoreTabZoom(zoomCtx);
                // A changed zoom means ApplyZoom has already queued a fresh render pass; leave the
                // scroll owed so it lands after that one rather than against this stale extent.
                if (Zoom.ZoomLevel != before) return;
            }

            if (_resumeScroll is not { } scroll) return;
            if (!ReferenceEquals(_ctx, scroll.Ctx)) { _resumeScroll = null; return; }
            _resumeScroll = null;
            PagePreviewPanel.UpdateLayout();
            PagePreviewPanel.ScrollToHorizontalOffset(scroll.H);
            PagePreviewPanel.ScrollToVerticalOffset(scroll.V);
        }

        /// <summary>
        /// Continuous-view half of <see cref="ApplyPendingViewResume"/>: puts a re-activated tab's
        /// own offset back in place of the "scroll to the top of the target page" anchor,
        /// suppressing the scroll→selection feedback loop exactly as ScrollContinuousToPageSuppressed
        /// does. Returns false when nothing is owed, so the caller falls back to the page anchor.
        /// </summary>
        private bool TryApplyContinuousResumeScroll()
        {
            if (_resumeScroll is not { } scroll) return false;
            if (!ReferenceEquals(_ctx, scroll.Ctx)) { _resumeScroll = null; return false; }
            _resumeScroll = null;
            _suppressContinuousScrollSync = true;
            PagePreviewPanel.UpdateLayout();
            PagePreviewPanel.ScrollToHorizontalOffset(scroll.H);
            PagePreviewPanel.ScrollToVerticalOffset(scroll.V);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                (Action)(() => _suppressContinuousScrollSync = false));
            return true;
        }

        /// <summary>Makes <paramref name="ctx"/> the active tab and rebuilds the shared UI from it.</summary>
        private void ActivateContext(DocumentContext ctx)
        {
            if (ReferenceEquals(_ctx, ctx)) { UpdateTabChrome(); return; }

            CommitActiveTextBox();
            CaptureViewState();
            CancelDocumentWork(cancelWindowOperation: false);
            ClearContinuousRenderState();

            // #399: anything the previous activation was still owed dies with it — the render it
            // was waiting on has just been cancelled.
            _resumeZoomFor = null;
            _resumeScroll = null;

            _ctx = ctx;

            // Tear down transient overlays/tools tied to the previous document.
            ClearSelection();
            CloseSearchBar();
            HideDrawSettings();
            HideTextSettings();
            HideSignaturePopup();
            HideCropPopup();
            ClearCropSelection();
            SetTool(EditTool.Select);
            _annotationCanvas.Children.Clear();
            _textEditorCanvas.Children.Clear();
            _activeTextBox = null;
            ClearSecondaryPages();

            if (_ctx.Doc is null)
            {
                // Empty tab → drop-zone state.
                PageList.Items.Clear();
                if (FindName("PageImage") is System.Windows.Controls.Image img)
                {
                    img.Source = null;
                    img.Width = double.NaN;
                    img.Height = double.NaN;
                }
                _primaryPageBitmap = null;
                FileNameLabel.Text = "";
                DropZone.Visibility = Visibility.Visible;
                PagePreviewPanel.Visibility = Visibility.Collapsed;
                HidePageBadgeNow();   // #197: never leave the badge floating over the start screen
                if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = false;
                _gridViewToggle.IsEnabled = false;
                _pageJumpBox.IsEnabled = false;
                _pageJumpBox.Text = "";
                _pageTotalLabel.Text = "/ –";
                UpdatePageControlsForDoc(false);   // hide the empty box + "/ –" outright
                LoadOutlines();           // no document → clears the tree and disables the tab
                RefreshRecentFilesUi();   // start screen is visible again; refresh the recent list
            }
            else
            {
                DropZone.Visibility = Visibility.Collapsed;
                PagePreviewPanel.Visibility = Visibility.Visible;
                // View mode is app-wide; make sure the correct layout host is visible for this tab.
                bool isContinuous = _viewMode == ViewMode.Continuous;
                _pageContentPanel.Visibility = isContinuous ? Visibility.Collapsed : Visibility.Visible;
                if (PageImage.Parent is FrameworkElement pgChild
                    && pgChild.Parent is FrameworkElement primBorder)
                    primBorder.Visibility = isContinuous ? Visibility.Collapsed : Visibility.Visible;
                _continuousPanel.Visibility = isContinuous ? Visibility.Visible : Visibility.Collapsed;
                FileNameLabel.Text = _ctx.DisplayName;
                if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = true;
                _gridViewToggle.IsEnabled = true;
                _pageJumpBox.IsEnabled = true;
                _pageTotalLabel.Text = $"/ {_ctx.Doc.PageCount}";
                UpdatePageControlsForDoc(true);
                RefreshPageList(_ctx.Thumbnails);
                LoadOutlines();   // rebuild the bookmark tree from this tab's live document

                int idx = _ctx.SelectedPageIndex;
                if (idx < 0 || idx >= PageList.Items.Count)
                    idx = PageList.Items.Count > 0 ? 0 : -1;

                // #399: a tab that has been looked at before resumes where it was left — its own
                // scroll offsets and its own zoom — rather than inheriting whatever the tab being
                // left behind happened to be showing. A tab with nothing captured (never switched
                // away from, or invalidated by a reload / a geometry-changing edit) falls through
                // to precisely the behaviour it had before: the render lands where it lands, at
                // the app-global zoom ApplyViewModeOnOpen set from the standing preference. This
                // is a RESUME only; nothing below ever writes that preference back.
                var resumeCtx = _ctx;
                bool resume = _ctx.ViewCaptured && _ctx.ViewModeAtCapture == _viewMode && idx >= 0;
                if (resume)
                {
                    _resumeScroll = (resumeCtx, _ctx.ViewScrollH, _ctx.ViewScrollV);
                    // Grid restores no zoom at all (RestoreTabZoom says why), and Continuous does
                    // its own below because it has to land after SetupContinuousView's FitToWidth.
                    // Everywhere else: a manual zoom is a bare number that needs no page under it,
                    // so it goes on NOW and the incoming page renders at the right size first
                    // time; a FIT measures whatever page is on screen — which at this instant is
                    // still the OUTGOING tab's — so it has to wait for this tab's render.
                    if (_viewMode != ViewMode.Grid && _viewMode != ViewMode.Continuous)
                    {
                        if (_ctx.ViewFitMode == ZoomFitMode.None) RestoreTabZoom(_ctx);
                        else _resumeZoomFor = resumeCtx;
                    }
                }

                if (idx >= 0)
                {
                    if (_viewMode == ViewMode.Continuous)
                    {
                        // View mode is app-wide but the continuous strip is per-document; rebuild
                        // it for the newly-activated tab. Set the index without firing a stale
                        // scroll, then SetupContinuousView scrolls to the right page.
                        _suppressContinuousScrollSync = true;
                        PageList.SelectedIndex = idx;
                        _suppressContinuousScrollSync = false;
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                            (Action)(() =>
                            {
                                if (!ReferenceEquals(_ctx, resumeCtx)) return;
                                SetupContinuousView(idx);
                                // #399: SetupContinuousView ends in FitToWidth (Continuous's own
                                // default) and only THEN defers its scroll, so this tab's zoom has
                                // to go back immediately after it returns — late enough that a fit
                                // measures this document's strip width, early enough that the
                                // deferred scroll and the slot heights are computed at the zoom we
                                // are resuming at rather than at fit-width.
                                if (resume) RestoreTabZoom(resumeCtx);
                            }));
                    }
                    // Setting SelectedIndex fires PageList_SelectionChanged (→ render).
                    // If the index is unchanged, render explicitly.
                    else if (PageList.SelectedIndex == idx) RerenderCurrentPage();
                    else PageList.SelectedIndex = idx;
                }
                else
                {
                    _resumeZoomFor = null;   // #399: no page, so no render will ever settle these
                    _resumeScroll = null;
                }
            }

            // Sync the save-button color with this tab's dirty state without
            // re-touching the model (MarkDirty(_ctx.IsDirty) is a no-op write).
            MarkDirty(_ctx.IsDirty);
            UpdateTabChrome();
        }

        // Lists every open tab by name in a dropdown, so a document doesn't have to be hunted for
        // by scrolling past a long run of same-width, ellipsis-truncated chips once many files are
        // open (the tab strip's ScrollViewer keeps every chip reachable, but not visible at once).
        private void TabOverflowBtn_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = (UIElement)sender,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
            };
            foreach (var ctx in _tabs)
            {
                string name = string.IsNullOrEmpty(ctx.DisplayName) ? "Untitled.pdf" : ctx.DisplayName;
                bool active = ReferenceEquals(ctx, _ctx);
                var c = ctx;
                var item = new MenuItem
                {
                    Header = (c.IsDirty ? "● " : "") + EscapeMenuHeader(name),
                    FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                    ToolTip = c.OriginalPath ?? name
                };
                if (active) item.SetResourceReference(MenuItem.ForegroundProperty, "AccentGreen");
                item.Click += (_, _) => { if (!ReferenceEquals(_ctx, c)) ActivateContext(c); };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        /// <summary>Rebuilds every tab chip and toggles strip visibility.</summary>
        private void RebuildTabStrip()
        {
            if (_tabStrip is null) return;
            _tabStrip.Children.Clear();
            foreach (var ctx in _tabs)
            {
                ctx.Chip = BuildTabChip(ctx);
                _tabStrip.Children.Add(ctx.Chip);
            }
            // Keep the single-document experience unchanged — only show the strip
            // once a second document is open.
            _tabStripBorder.Visibility = _tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            UpdateTabChrome();
        }

        private Border BuildTabChip(DocumentContext ctx)
        {
            var text = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 180
            };
            var close = new Button
            {
                Content = "", // Segoe MDL2 Assets close glyph
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 9,
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                Margin = new Thickness(8, 0, 0, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = BrushResource("TextSecondary"),
                Cursor = Cursors.Hand,
                ToolTip = "Close file (Ctrl+W)",
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false
            };
            close.Click += (_, e) => CloseTab(ctx);

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(text);
            panel.Children.Add(close);

            var chip = new Border
            {
                Child = panel,
                Padding = new Thickness(10, 5, 6, 5),
                Margin = new Thickness(0, 4, 4, 0),
                BorderThickness = new Thickness(1, 1, 1, 0),
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Cursor = Cursors.Hand,
                Tag = ctx
            };
            // Drag this chip onto another TDPdf window to move the document there, or onto empty
            // desktop / any other app to tear it off into a brand-new TDPdf window \u2014 see
            // BeginTabDrag/EndTabDrag. A plain click (no drag distance) still just activates the
            // tab, exactly as before.
            chip.PreviewMouseLeftButtonDown += (_, e) =>
            {
                // The close "x" is a child of this chip, so the tunneling Preview event reaches us
                // first; let it fall through untouched rather than arming a drag over top of it.
                if (e.OriginalSource is DependencyObject src && IsDescendantOf(src, close)) return;
                _tabDragCandidate = ctx;
                _tabDragStartScreen = chip.PointToScreen(e.GetPosition(chip));
            };
            chip.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || !ReferenceEquals(_tabDragCandidate, ctx)) return;
                var nowScreen = chip.PointToScreen(e.GetPosition(chip));
                if (!_isDraggingTab)
                {
                    if (Math.Abs(nowScreen.X - _tabDragStartScreen.X) < SystemParameters.MinimumHorizontalDragDistance &&
                        Math.Abs(nowScreen.Y - _tabDragStartScreen.Y) < SystemParameters.MinimumVerticalDragDistance)
                        return;
                    BeginTabDrag(ctx, chip);
                }
                UpdateTabDrag(nowScreen);
            };
            chip.PreviewMouseLeftButtonUp += (_, e) =>
            {
                if (_isDraggingTab && ReferenceEquals(_tabDragCandidate, ctx))
                {
                    EndTabDrag(chip.PointToScreen(e.GetPosition(chip)));
                    e.Handled = true;
                }
                else if (ReferenceEquals(_tabDragCandidate, ctx) && !ReferenceEquals(_ctx, ctx))
                {
                    ActivateContext(ctx);
                }
                _tabDragCandidate = null;
            };
            chip.LostMouseCapture += (_, _) =>
            {
                // Reached two ways: our own EndTabDrag already released capture (isDraggingTab is
                // already false by then, so this is a no-op) or capture was pulled out from under
                // us \u2014 Escape, Alt-Tab, a dialog stealing focus. Either way, drop the cancel.
                if (_isDraggingTab)
                {
                    _isDraggingTab = false;
                    _tabDragGhost?.Close();
                    _tabDragGhost = null;
                }
                _tabDragCandidate = null;
            };

            // Right-click menu. Rebuilt with the strip on every tab change, so "Close Other Tabs"
            // can be enabled purely from the current count with no live refresh to maintain.
            var chipMenu = new ContextMenu();
            chipMenu.Items.Add(MakeMenuItem("Close Tab", (_, _) => CloseTab(ctx), "Ctrl+W",
                "Close this document", "\uE8BB"));
            var closeOthers = MakeMenuItem("Close Other Tabs", (_, _) => CloseOtherTabs(ctx), "Ctrl+Shift+W",
                "Close every open document except this one", "\uE711");
            closeOthers.IsEnabled = _tabs.Count > 1;
            chipMenu.Items.Add(closeOthers);
            chipMenu.Items.Add(MakeMenuItem("Move to New Window", (_, _) => _ = TearOffTabToNewWindowAsync(ctx), null,
                "Open this document alone in a new TDPdf window", "\uE78B"));
            // OriginalPath, not the working path: after a decrypt-on-open or a structural edit the
            // working file is a temp copy, and revealing %TEMP% is not what "containing folder"
            // means. A document with no home on disk (a merge result, say) has nothing to show.
            var openFolder = MakeMenuItem("Open Containing Folder", (_, _) => RevealInExplorer(ctx.OriginalPath), null,
                "Show this document in File Explorer", "\uE8DA");
            openFolder.IsEnabled = ctx.OriginalPath is not null;
            chipMenu.Items.Add(openFolder);
            chip.ContextMenu = chipMenu;
            return chip;
        }

        /// <summary>
        /// Selects a file in File Explorer, opening its folder if it is not already showing.
        /// </summary>
        /// <remarks>
        /// The path is quoted but /select, is deliberately outside the quotes — that is the shape
        /// explorer.exe expects, and it is the reason this is a helper rather than an inline call
        /// waiting to be got wrong a second time. A file that has been deleted or moved since it
        /// was opened just falls back to its folder.
        /// </remarks>
        private void RevealInExplorer(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (System.IO.File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                    return;
                }
                string? folder = System.IO.Path.GetDirectoryName(path);
                if (System.IO.Directory.Exists(folder))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
                else
                    SetStatus("That folder is no longer available");
            }
            catch (Exception ex)
            {
                SetStatus($"Could not open the folder — {ex.Message}");
            }
        }

        /// <summary>Updates each chip's label (name + dirty marker) and active styling.</summary>
        private void UpdateTabChrome()
        {
            if (_tabStrip is null) return;
            UpdateWindowTitle();
            foreach (var ctx in _tabs)
            {
                if (ctx.Chip is null) continue;
                bool active = ReferenceEquals(ctx, _ctx);
                ctx.Chip.Background = active ? BrushResource("BgPanel") : BrushResource("BgDark");
                ctx.Chip.BorderBrush = active ? BrushResource("AccentGreen") : BrushResource("BorderDim");
                if (ctx.Chip.Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock tb)
                {
                    string name = string.IsNullOrEmpty(ctx.DisplayName) ? "Untitled.pdf" : ctx.DisplayName;
                    tb.Text = (ctx.IsDirty ? "● " : "") + name;
                    tb.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                    tb.Foreground = active ? BrushResource("TextPrimary") : BrushResource("TextSecondary");
                    // Chip text truncates at 180px (TextTrimming.CharacterEllipsis in BuildTabChip),
                    // so a long filename needs a hover to read in full — this is the only place that
                    // ever refreshes, so it also picks up a rename (e.g. Save As) after tab creation.
                    ctx.Chip.ToolTip = ctx.OriginalPath ?? name;
                }
            }
        }

        // The native window title — what Alt-Tab, the taskbar hover preview and the taskbar
        // thumbnail label actually show — stayed the static "TDPdf" from XAML forever, so every
        // open window/instance looked identical there even though the in-app title bar already
        // carries FileNameLabel. Reflect the active document (and how many other tabs share this
        // window) so windows become distinguishable at the OS level, not just inside the app.
        private void UpdateWindowTitle()
        {
            if (_ctx.Doc is null) { Title = "TDPdf"; return; }
            string name = string.IsNullOrEmpty(_ctx.DisplayName) ? "Untitled.pdf" : _ctx.DisplayName;
            Title = _tabs.Count > 1 ? $"{name} - TDPdf ({_tabs.Count} tabs)" : $"{name} - TDPdf";
        }

        /// <summary>Closes a tab (prompting if it has unsaved changes) and activates a neighbor.</summary>
        private void CloseTab(DocumentContext ctx)
        {
            EnsureActiveTabRegistered();
            if (!_tabs.Contains(ctx)) return;

            // A freeform polygon still being placed on the tab we are about to throw away has
            // nowhere to land, so it is discarded rather than committed — and discarding it here,
            // before the dirty prompt, keeps an abandoned gesture from asking about unsaved work.
            if (ReferenceEquals(_ctx, ctx))
            {
                ResolveShapePolygon(commit: false);
                CommitActiveTextBox();
            }

            if (ctx.Doc is not null && ctx.IsDirty)
            {
                if (!ReferenceEquals(_ctx, ctx)) ActivateContext(ctx);
                var res = TdpDialog.ShowYesNo(this,
                    "This file has unsaved changes.",
                    "Close Without Saving", "Cancel",
                    "TDPdf", MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
            }

            RemoveTabSilently(ctx);
            SetStatus("Ready");
        }

        /// <summary>
        /// The actual tab-removal mechanics, with no dirty-changes prompt — CloseTab gates on that
        /// itself before calling this; the cross-window transfer path (below) calls this too, once
        /// the document is already safely handed to the other window, so nothing is lost either way.
        /// </summary>
        private void RemoveTabSilently(DocumentContext ctx)
        {
            if (!_tabs.Contains(ctx)) return;

            int removedIndex = _tabs.IndexOf(ctx);
            bool closingActive = ReferenceEquals(_ctx, ctx);

            if (closingActive)
            {
                CancelDocumentWork(cancelWindowOperation: false);
                PageImage.Source = null;
                PageImage.Tag = null;
                _primaryPageBitmap = null;
            }

            try { ctx.Doc?.Close(); } catch { }
            ctx.Doc = null;
            ctx.Annotations.Clear();
            ctx.RedactionMarks.Clear();
            ctx.RenderCache.Clear();
            ctx.RenderDims.Clear();
            ctx.UndoStack.Clear();
            ctx.RedoStack.Clear();
            ctx.ContentEditor.ClearCache();
            ctx.AllSearchRects.Clear();
            ctx.SearchResultPages.Clear();
            ctx.Thumbnails = null;
            ctx.ViewCaptured = false;   // #399: nothing left to resume into
            _tabs.Remove(ctx);

            QueueReleasedDocumentCollection();

            if (_tabs.Count == 0)
            {
                var empty = new DocumentContext();
                _tabs.Add(empty);
                ActivateContext(empty);
                // The last document just closed — re-collapse the rail (animated) and hide the page
                // controls. Done here rather than in ActivateContext's empty branch on purpose: that
                // branch also runs for the throwaway tab OpenInTabAsync creates on the way to a second
                // document, which would collapse-then-expand for no reason.
                SyncSidebarToDocState(hasDoc: false, startup: false);
            }
            else if (closingActive)
            {
                int next = Math.Min(removedIndex, _tabs.Count - 1);
                ActivateContext(_tabs[next]);
            }
            RebuildTabStrip();
        }
    }
}
