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
            //
            // A resume still OWED to this tab means the live values are not its own yet: leaving
            // the tab before its render tail ran (fast Ctrl+Tab through several tabs) would read
            // the offsets of a page that has not been laid out — typically 0 — and a fit zoom
            // that is still the outgoing tab's, and park THOSE as where this tab was, so the
            // next visit resumed somewhere the user never left it. Whatever is still owed is by
            // definition the tab's real state, so it is kept as captured. (Owed state only ever
            // exists in the mode it was captured in — a view-mode switch drops it — so
            // ViewModeAtCapture stays consistent with the offsets either way.)
            bool scrollOwed = _resumeScroll is { } owed && ReferenceEquals(owed.Ctx, _ctx);
            bool zoomOwed = ReferenceEquals(_resumeZoomFor, _ctx);
            if (!scrollOwed)
            {
                _ctx.ViewScrollH = PagePreviewPanel.HorizontalOffset;
                _ctx.ViewScrollV = PagePreviewPanel.VerticalOffset;
            }
            if (!zoomOwed)
            {
                _ctx.ViewZoomLevel = Zoom.ZoomLevel;
                _ctx.ViewFitMode = _zoomFitMode;
                _ctx.ViewManualZoomIntent = _manualZoomIntent;
            }
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

            // Field placement belongs to the tab it started on; switching away ends it (its fields
            // stay on that tab's session) before anything below can draw this tab's canvas.
            if (_eSignPlacement is not null) EndESignPlacement(ESignPlacementEnd.Aborted);
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

        private const string TabSignedChipTag = "esign-signed";

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

            // Electronic Signature's "Signed" chip (MainWindow.ESignatureRequests.cs): shown by
            // UpdateTabChrome when this file is the signed copy of a completed request.
            var signedChip = new Border
            {
                Tag = TabSignedChipTag,
                Visibility = Visibility.Collapsed,
                Background = BrushResource("AccentGreenDim"),
                BorderBrush = BrushResource("AccentGreen"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "The signed copy of a completed signature request, unchanged since it was downloaded. Saving it in TDPdf removes the signature.",
                Child = new TextBlock
                {
                    Text = "Signed",
                    FontSize = 10,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = BrushResource("AccentGreen"),
                },
            };
            System.Windows.Automation.AutomationProperties.SetName(signedChip, "Signed");

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(text);
            panel.Children.Add(signedChip);
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
                SetStatus($"Could not open the folder: {ex.Message}");
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
                    foreach (var child in sp.Children)
                        if (child is Border b && ReferenceEquals(b.Tag, TabSignedChipTag))
                            b.Visibility = ctx.Doc is not null && ctx.HasDigitalSignature && ctx.ESignSignedCopy ? Visibility.Visible : Visibility.Collapsed;
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

            // An inactive tab is asked about where it stands — it is never activated first. Its
            // dirty flag lives on its own DocumentContext and its edits were already committed when
            // it was switched away from, so there is nothing to capture; activating it would only
            // tear down the tab the user is working in, and strand them on this one on Cancel.
            // The prompt names the file because the document it is about may not be on screen.
            if (ctx.Doc is not null && ctx.IsDirty)
            {
                string name = string.IsNullOrEmpty(ctx.DisplayName) ? "Untitled.pdf" : ctx.DisplayName;
                var res = TdpDialog.ShowYesNo(this,
                    $"\"{name}\" has unsaved changes.",
                    "Close Without Saving", "Cancel",
                    "TDPdf", MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
                if (!_tabs.Contains(ctx)) return;   // the modal pumped messages; it may be gone
            }

            RemoveTabSilently(ctx);
            SetStatus("Ready");
        }

        /// <summary>
        /// The actual tab-removal mechanics, with no dirty-changes prompt — CloseTab gates on that
        /// itself before calling this; the cross-window transfer path (below) calls this too, once
        /// the document is already safely handed to the other window, so nothing is lost either way.
        /// </summary>
        /// <param name="keepFile">
        /// A working file that was handed to another window (cross-window drag, tear-off): it is the
        /// receiver's now and must outlive this tab. Everything else the tab wrote to %TEMP% is deleted.
        /// </param>
        private void RemoveTabSilently(DocumentContext ctx, string? keepFile = null)
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
            ctx.TempFiles.DeleteAll(keepFile);   // decrypted and pre-edit copies must not outlive the tab
            ctx.Annotations.Clear();
            ctx.RedactionMarks.Clear();
            if (_eSignPlacement is { } placing && ReferenceEquals(placing.Ctx, ctx)) EndESignPlacement(ESignPlacementEnd.Aborted);
            ctx.ESignSession = null;
            ctx.RenderCache.Clear();
            ctx.RenderDims.Clear();
            ctx.UndoStack.Clear();
            ctx.RedoStack.Clear();
            ctx.ContentEditor.ClearCache();
            ctx.AllSearchRects.Clear();
            ctx.SearchResultPages.Clear();
            CancelSearch(ctx);
            ctx.SearchIndex = null;
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

        // ============================================================
        // Drag/drop: page reorder
        // ============================================================

        private void PageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            _pageClickCollapseIndex = -1;

            // #135: WPF's Extended-mode ListBox collapses a multi-page selection to the one clicked
            // page on mouse DOWN (ListBox.NotifyListItemClicked -> MakeSingleSelection), which is
            // one gesture too early for dragging: by the time the pointer has moved far enough to
            // start a drag, the block the user selected is already gone and only one page travels.
            // So when the press lands on a page that is already part of a multi-page selection with
            // no modifier held, swallow the event to keep the selection intact, and defer the
            // collapse to mouse-up — where it only happens if no drag followed, which is exactly
            // how Explorer and every other multi-select list behave.
            if (e.ClickCount != 1
                || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0
                || PageList.SelectedItems.Count <= 1)
                return;

            if (ContainerUnderMouse(e.OriginalSource as DependencyObject) is not ListBoxItem row) return;
            int idx = PageList.ItemContainerGenerator.IndexFromContainer(row);
            if (idx < 0 || !row.IsSelected) return;

            _pageClickCollapseIndex = idx;
            // The ListBox's own handler would also have focused the row; do it here since it will
            // never run, or the sidebar silently stops answering the keyboard after this click.
            row.Focus();
            e.Handled = true;
        }

        /// <summary>The <see cref="ListBoxItem"/> a hit-tested element sits inside, if any.</summary>
        private static ListBoxItem? ContainerUnderMouse(DependencyObject? hit)
        {
            while (hit is not null and not ListBoxItem)
                hit = VisualTreeHelper.GetParent(hit);
            return hit as ListBoxItem;
        }

        private void PageList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // The press was on an already-selected page and no drag followed it, so it was a plain
            // click after all: apply the selection collapse that mouse-down deferred.
            if (_pageClickCollapseIndex >= 0 && _pageClickCollapseIndex < PageList.Items.Count)
                PageList.SelectedIndex = _pageClickCollapseIndex;
            _pageClickCollapseIndex = -1;
        }

        private void PageList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var diff = _dragStartPoint - e.GetPosition(null);
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                // #135 (upstream KillerPDF #233): drag every selected page as one ordered block,
                // not just the one under the cursor. The list has been SelectionMode="Extended"
                // all along, so people could already select a run of pages and then discovered
                // that dragging moved exactly one of them.
                int[] block = SelectedPageIndices();
                if (block.Length > 0)
                {
                    // A drag is starting, so the press was not a plain click: nothing to collapse.
                    _pageClickCollapseIndex = -1;
                    // DoDragDrop blocks until the drag ends ANY way it can — dropped, Escape,
                    // capture lost to Alt-Tab or a dialog — so this finally is the single teardown
                    // point, the same role LostMouseCapture plays for the tab-strip drag.
                    try { DragDrop.DoDragDrop(PageList, block, DragDropEffects.Move); }
                    finally { HidePageDropLine(); }   // the drag can end anywhere, including nowhere
                }
            }
        }

        /// <summary>The selected pages' indices, ascending and distinct; empty when nothing is
        /// selected. Unlike <see cref="SelectedPageIndicesForOcr"/> this does NOT fall back to the
        /// current page — the callers here mutate the document, so "nothing selected" must mean
        /// nothing happens rather than an unasked-for edit to page one.</summary>
        private int[] SelectedPageIndices()
            => PageList.SelectedItems.Cast<object>()
                .Select(o => PageList.Items.IndexOf(o))
                .Where(i => i >= 0)
                .Distinct()
                .OrderBy(i => i)
                .ToArray();

        private void PageList_DragOver(object sender, DragEventArgs e)
        {
            // #172 (upstream KillerPDF): the sidebar takes a FileDrop of PDFs/images as well as its own
            // page-reorder payload. The internal payload keeps first refusal so a page drag is never
            // mistaken for a file drop.
            if (e.Data.GetDataPresent(typeof(int[])))
            {
                e.Effects = DragDropEffects.Move;
                ShowPageDropLine(DropTargetIndex(e.GetPosition(PageList)));
            }
            else if (_doc is not null && DroppedOpenablePaths(e).Length > 0)
            {
                e.Effects = DragDropEffects.Copy;
                HidePageDropLine();   // a file drop appends; there is no insertion point to promise
            }
            else
            {
                e.Effects = DragDropEffects.None;
                HidePageDropLine();
            }
            e.Handled = true;
        }

        private void PageList_DragLeave(object sender, DragEventArgs e) => HidePageDropLine();

        /// <summary>
        /// The index a drop at <paramref name="pos"/> would insert BEFORE — i.e. the first page
        /// whose midpoint is below the cursor, or one past the end.
        /// </summary>
        /// <remarks>
        /// #135: one helper so the line drawn during the drag and the reorder performed on the drop
        /// can never disagree. They were separate arithmetic before, which is the standard way a
        /// drop indicator ends up pointing somewhere the page does not go.
        /// </remarks>
        private int DropTargetIndex(Point pos)
        {
            for (int i = 0; i < PageList.Items.Count; i++)
            {
                if (PageList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item)
                {
                    var mid = item.TranslatePoint(new Point(0, item.ActualHeight / 2), PageList);
                    if (pos.Y < mid.Y) return i;
                }
            }
            return PageList.Items.Count;
        }

        private void ShowPageDropLine(int beforeIndex)
        {
            if (PageList.Items.Count == 0) { HidePageDropLine(); return; }

            double y;
            if (beforeIndex < PageList.Items.Count
                && PageList.ItemContainerGenerator.ContainerFromIndex(beforeIndex) is ListBoxItem at)
            {
                y = at.TranslatePoint(new Point(0, 0), _sidebarListHost).Y;
            }
            else if (PageList.ItemContainerGenerator.ContainerFromIndex(PageList.Items.Count - 1)
                     is ListBoxItem last)
            {
                y = last.TranslatePoint(new Point(0, last.ActualHeight), _sidebarListHost).Y;
            }
            else { HidePageDropLine(); return; }

            // Virtualisation can put a container off-screen; a line drawn outside the host is
            // meaningless, so say nothing rather than something wrong.
            if (!IsFinite(y) || y < 0 || y > _sidebarListHost.ActualHeight) { HidePageDropLine(); return; }

            _pageDropLine.Margin = new Thickness(6, y - 1, 6, 0);
            _pageDropLine.Visibility = Visibility.Visible;
        }

        private void HidePageDropLine()
        {
            if (_pageDropLine is not null) _pageDropLine.Visibility = Visibility.Collapsed;
        }

        /// <summary>The dropped FileDrop paths this sidebar can append, using the same PDF/image
        /// classification as the start-screen drop zone (<see cref="IsOpenablePath"/>). Folders and .zip
        /// archives are deliberately NOT expanded here — that is the start screen's open flow, which asks
        /// merge-vs-separate; a drop onto an open document's page list only ever appends.</summary>
        private static string[] DroppedOpenablePaths(DragEventArgs e)
            => e.Data.GetDataPresent(DataFormats.FileDrop)
               && e.Data.GetData(DataFormats.FileDrop) is string[] paths
                ? paths.Where(IsOpenablePath).ToArray()
                : Array.Empty<string>();

        /// <summary>
        /// #172: appends the dropped files' pages to the open document. Upstream appends rather than
        /// inserting at the drop point on purpose — appending leaves every existing page index untouched,
        /// so annotations, rotations and the undo stack need no remapping. Reuses the two importers we
        /// already have: <see cref="AppendPdfFileToDoc"/> (File ▸ Merge, including named-destination link
        /// rewriting) and <see cref="AddImagePagesFromFile"/> (one page per image frame).
        /// </summary>
        private async void AppendFilesToCurrentDoc(string[] files)
        {
            if (_doc is null) return;
            var doc = _doc;
            // The rollback point (which also commits the active text box): the appends below change
            // _doc across awaits, so this cannot go through RunPageOperation's single Action.
            var snapshot = BeginPageOperation();
            int before = doc.PageCount;
            foreach (var f in files)
            {
                // A single bad file skips rather than aborting the drop: a folder-full drag routinely
                // carries one encrypted or truncated file and the rest are still worth appending.
                try
                {
                    if (IsPdfPath(f)) AppendPdfFileToDoc(doc, f);
                    else              AddImagePagesFromFile(doc, f);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Sidebar drop skipped {f}: {ex}");
                    // #203 (upstream v1.7.4): a damaged PDF used to be swallowed here — nothing was
                    // appended and nothing was said, so the file simply vanished from the drop.
                    // Offer the same repair the open path offers.
                    if (IsPdfPath(f)) await TryAppendRepairedPdfAsync(doc, f);
                }
                if (_doc is null || !ReferenceEquals(_doc, doc))   // document swapped mid-await
                {
                    EndPageOperation(snapshot);
                    return;
                }
            }
            if (doc.PageCount == before)
            {
                EndPageOperation(snapshot);
                SetStatus("Nothing could be appended because the dropped files could not be read.");
                return;
            }
            int added = doc.PageCount - before;
            try
            {
                // Same persist-and-repaint path as the page reorder below; it marks the document dirty.
                // Appending renumbers nothing (see the summary), so the unsaved annotations are kept
                // as they are rather than taking the reload's default clear.
                SaveTempAndReload(keepAnnotations: true);
                EndPageOperation(snapshot);
                SetStatus($"Appended {added} page{(added == 1 ? "" : "s")} from {files.Length} file{(files.Length == 1 ? "" : "s")}");
            }
            catch (Exception ex)
            {
                RollBackPageOperation(snapshot, "Append", ex);
            }
        }

        /// <summary>
        /// #203: offers to repair a dropped PDF that PdfSharpCore could not read, and appends the
        /// repaired copy on success. The original file is never written to.
        /// </summary>
        /// <remarks>
        /// Only the lossless strategy is offered here, deliberately narrower than upstream's three.
        /// <see cref="TDPdf.Services.PdfiumInterop.TryPdfiumRepair"/> re-saves through PDFium and
        /// keeps text, forms and bookmarks. The open path's raster recovery is NOT chained on:
        /// opening a rasterized document tells the user the whole file was recovered from pixels,
        /// whereas appending one would bury a run of flattened, unsearchable pages in the middle of
        /// a live document with nothing to distinguish them. If PDFium cannot read it either, say so
        /// and leave the document alone.
        /// </remarks>
        private async Task TryAppendRepairedPdfAsync(PdfDocument doc, string path)
        {
            string name = System.IO.Path.GetFileName(path);
            var ask = TdpDialog.Show(this,
                $"\"{name}\" has a damaged structure and could not be appended.\n\n" +
                "Attempt a repair? A repaired copy is used, and the original file is not changed.\n\n" +
                "A repaired file may be missing bookmarks, forms and other interactive features.",
                "TDPdf", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ask != MessageBoxResult.Yes) return;

            string fixedPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"tdpdf_fixed_{Guid.NewGuid():N}.pdf");
            SetBusy(true, $"Repairing {name}...");
            try
            {
                bool repaired = await Task.Run(
                    () => TDPdf.Services.PdfiumInterop.TryPdfiumRepair(path, fixedPath));
                if (repaired)
                {
                    try
                    {
                        AppendPdfFileToDoc(doc, fixedPath);
                        return;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Repaired append failed {path}: {ex}");
                    }
                }
                TdpDialog.Show(this, $"\"{name}\" could not be repaired.", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
                try { if (File.Exists(fixedPath)) File.Delete(fixedPath); } catch { /* temp file */ }
            }
        }

        private void PageList_Drop(object sender, DragEventArgs e)
        {
            HidePageDropLine();
            // #172: a FileDrop appends; anything carrying the internal page payload falls through to
            // the reorder below, unchanged.
            if (_doc is not null && !e.Data.GetDataPresent(typeof(int[])))
            {
                var files = DroppedOpenablePaths(e);
                if (files.Length > 0) { AppendFilesToCurrentDoc(files); e.Handled = true; }
                return;
            }
            if (_doc is null || e.Data.GetData(typeof(int[])) is not int[] block || block.Length == 0)
                return;

            // The insertion line the user was just looking at and the move performed here read the
            // same DropTargetIndex, and the same PageBlockMove arithmetic converts that gap into an
            // insert position — so the line can never promise a place the pages do not go.
            MovePageBlock(TDPdf.Services.PageBlockMove.Compute(
                _doc.PageCount, block, DropTargetIndex(e.GetPosition(PageList))));
        }

        /// <summary>
        /// Carries out a page-reorder <see cref="TDPdf.Services.PageBlockMove.Plan"/> against the
        /// open document. The one page-reorder mutation in the app: the thumbnail drag and the
        /// Move Page Up / Down rows both come here, so the two cannot drift apart.
        /// </summary>
        private void MovePageBlock(TDPdf.Services.PageBlockMove.Plan plan)
        {
            // IsNoOp covers a block dropped back onto itself, or into a gap inside itself. It has to
            // be honoured rather than performed harmlessly: a reorder rewrites and reloads the
            // document — a full re-render, a dirty flag and a lost scroll position — in exchange
            // for nothing at all.
            if (_doc is null || plan.IsNoOp || plan.From.Length == 0) return;
            // It had no error handling at all: a failed reload escaped to the crash handler with
            // the pages already moved in memory. RunPageOperation (which also commits the active
            // text box) shows the error and puts the document back.
            RunPageOperation("Move pages", () => MovePageBlockCore(plan));
        }

        private void MovePageBlockCore(TDPdf.Services.PageBlockMove.Plan plan)
        {
            var doc = _doc!;

            // Each page's annotations travel with it (upstream KillerPDF #429 widened to reorder).
            // PageBlockMove.Apply is the model of exactly the mutation below, so inverting the order
            // it produces gives every page's new number; the map is taken against the document as
            // it stands and applied once the pages have moved.
            var map = TDPdf.Services.PageIndexRemap.ForOrder(
                doc.PageCount, TDPdf.Services.PageBlockMove.Apply(doc.PageCount, plan));

            // Lift the pages in document order, then remove from the end so the earlier indices
            // stay valid while we do it. Each PdfPage carries its own /Rotate, so a page's rotation
            // travels with the page object itself and needs no separate bookkeeping.
            var moving = plan.From.Select(i => doc.Pages[i]).ToList();
            for (int k = plan.From.Length - 1; k >= 0; k--) doc.Pages.RemoveAt(plan.From[k]);

            int insertAt = Math.Clamp(plan.InsertAt, 0, doc.PageCount);
            for (int k = 0; k < moving.Count; k++) doc.Pages.Insert(insertAt + k, moving[k]);

            // Persists, reloads, repaints and marks the document dirty. A reorder moves pages but
            // changes no page's geometry, so the annotations — renumbered with their pages — are
            // still valid where they are drawn, and are kept.
            ApplyPageIndexMap(map);
            SaveTempAndReload(keepAnnotations: true);

            // Leave the block selected where it landed, so a second drag (or a second Move Down)
            // continues from where the eye already is rather than from wherever the rebuilt list
            // decided to put the selection.
            PageList.SelectedItems.Clear();
            if (insertAt >= PageList.Items.Count) return;
            // SelectedIndex FIRST, then the rest of the block: on a multi-select ListBox the
            // SelectedIndex setter means "select just this one", so assigning it afterwards would
            // quietly throw away every page but the first. It is also what drives
            // PageList_SelectionChanged, and therefore what puts the page in the viewer.
            PageList.SelectedIndex = insertAt;
            for (int k = 1; k < moving.Count && insertAt + k < PageList.Items.Count; k++)
                PageList.SelectedItems.Add(PageList.Items[insertAt + k]);
        }

        /// <summary>
        /// Duplicate Page(s): inserts a copy of every selected page as one block immediately after
        /// the last of them, and leaves the copies selected.
        /// </summary>
        /// <remarks>
        /// The copy itself is <see cref="TDPdf.Services.PdfPageDuplicate"/>'s problem, and it is a
        /// real problem — inserting a page back into the document it already belongs to shares one
        /// object rather than copying it, and PdfSharpCore has no in-document copy to offer. That
        /// file's header explains the mechanism; tests/PdfCore proves the two pages are genuinely
        /// independent by mutating one across a save round trip and checking the other.
        ///
        /// Everything after the copy is the ordinary structural-edit path: mutate _doc.Pages,
        /// renumber the unsaved annotations to match (ApplyPageIndexMap), then SaveTempAndReload,
        /// which is what marks the document dirty. The COPIES start clean: they are made from the
        /// saved page content, which the unsaved overlay is not part of, and cloning the overlay
        /// onto them would be guessing at intent. But the originals — and every other page in the
        /// document — keep theirs. This used to take the reload's default clear, which bought
        /// "no two identical-looking pages where only one is annotated" by throwing away every
        /// unsaved annotation in the document, including on pages the duplicate never touched.
        /// </remarks>
        private void DuplicatePages_Click()
        {
            if (_doc is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            var doc = _doc;
            int[] pages = SelectedPageIndices();
            // The menu row is disabled with an empty selection, so this only catches a caller that
            // arrives some other way; it must not fall back to page one and edit the document unasked.
            if (pages.Length == 0) { SetStatus("Select the pages to duplicate first."); return; }
            RunPageOperation("Duplicate", () =>
            {
                doc = _doc!;
                int oldCount = doc.PageCount;
                int insertAt = TDPdf.Services.PdfPageDuplicate.Duplicate(doc, pages);
                if (insertAt < 0) return;
                // The copies arrive as one run at insertAt, so the map is an insertion of however
                // many pages were actually added; nothing maps onto the copies themselves.
                ApplyPageIndexMap(TDPdf.Services.PageIndexRemap.ForInsert(oldCount, insertAt, doc.PageCount - oldCount));
                SaveTempAndReload(keepAnnotations: true);

                // #135 item 5: the copies end up selected, and therefore shown in the viewer — the
                // whole point of the request. Same order of operations as MovePageBlock above, for
                // the same reason: on a multi-select ListBox the SelectedIndex setter means "select
                // just this one", so it has to go FIRST and the rest of the block be added after,
                // or every copy but the first is silently dropped from the selection.
                PageList.SelectedItems.Clear();
                if (insertAt < PageList.Items.Count)
                {
                    PageList.SelectedIndex = insertAt;
                    for (int k = 1; k < pages.Length && insertAt + k < PageList.Items.Count; k++)
                        PageList.SelectedItems.Add(PageList.Items[insertAt + k]);
                }
                SetStatus($"Duplicated {pages.Length} page{(pages.Length == 1 ? "" : "s")}");
            });
        }

    }
}
