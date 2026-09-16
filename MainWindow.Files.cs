// MainWindow — file operations and the file toolbar.
// Open / save / save-as / save-flattened / merge / split and the toolbar buttons that
// drive them. Both regions extracted verbatim from MainWindow.xaml.cs, in source order.

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
        // File operations
        // ============================================================

        private async Task OpenFileAsync(string path)
        {
            _openCancellationTokenSource?.Cancel();
            _renderCancellationTokenSource?.Cancel();
            _openCancellationTokenSource?.Dispose();
            _openCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _openCancellationTokenSource.Token;

            SetFileOperationBusy(true, $"Opening {System.IO.Path.GetFileName(path)}...");
            var openOp = Telemetry.StartOperation("OpenFile");
            try
            {
                var result = await OpenFileCoreAsync(path, null, cancellationToken);
                await FinishOpenFileAsync(result, cancellationToken);
                openOp.With("Recovered", result.RecoveredFromRaster ? "true" : "false");
            }
            catch (OperationCanceledException)
            {
                openOp.With("Canceled", "true");
                SetStatus("Open canceled");
            }
            catch (Exception ex) when (IsPasswordException(ex))
            {
                SetFileOperationBusy(false);
                string? pw = PromptForPassword(path);
                if (pw is null)
                {
                    openOp.With("Canceled", "true");
                    SetStatus("Open canceled");
                    return;
                }
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        SetStatus("Open canceled");
                        return;
                    }
                    SetFileOperationBusy(true, $"Opening {System.IO.Path.GetFileName(path)}...");
                    _openCancellationTokenSource?.Dispose();
                    _openCancellationTokenSource = new CancellationTokenSource();
                    var retryCancellationToken = _openCancellationTokenSource.Token;
                    var result = await OpenFileCoreAsync(path, pw, retryCancellationToken);
                    await FinishOpenFileAsync(result, retryCancellationToken);
                    openOp.With("Encrypted", "true");
                }
                catch (OperationCanceledException)
                {
                    SetStatus("Open canceled");
                }
                catch (Exception ex2)
                {
                    openOp.Fail(ex2);
                    Telemetry.TrackEvent("File.OpenFailed", new Dictionary<string, string>
                    {
                        ["ExceptionType"] = ex2.GetType().FullName ?? "Unknown",
                        ["Stage"]         = "AfterPassword",
                    });
                    SetFileOperationBusy(false);
                    TdpDialog.Show(this, $"Failed to open PDF:\n{ex2.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                openOp.Fail(ex);
                Telemetry.TrackEvent("File.OpenFailed", new Dictionary<string, string>
                {
                    ["ExceptionType"] = ex.GetType().FullName ?? "Unknown",
                    ["Stage"]         = "Initial",
                });
                SetFileOperationBusy(false);
                TdpDialog.Show(this, $"Failed to open PDF:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                openOp.Dispose();
                SetFileOperationBusy(false);
            }
        }

        private async Task<PdfOpenResult> OpenFileCoreAsync(string path, string? password, CancellationToken cancellationToken)
        {
            var result = await _pdfDocumentService.OpenAsync(path, password, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        /// <summary>
        /// Installs an opened <see cref="PdfOpenResult"/> into the active tab.
        /// </summary>
        /// <param name="internalReload">
        /// True when this is TDPdf re-loading the SAME document from a working file it just wrote
        /// (the crop pipeline, and its failure-restore), rather than the user opening a file. Such a
        /// reload must not touch the tab's identity — OriginalPath, IsUntitled, WasProtected and the
        /// recent list — because DisplayPath is then a temp path: claiming it as the document would
        /// re-introduce the "Ctrl+S writes into %TEMP%" bug this whole change exists to fix.
        /// </param>
        private async Task FinishOpenFileAsync(PdfOpenResult result, CancellationToken cancellationToken,
            bool internalReload = false)
        {
            bool assignedDocument = false;
            try
            {
                int pageCount = result.Document.PageCount;
                var thumbnails = await _pdfDocumentService.RenderThumbnailsAsync(result.WorkingPath, pageCount, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                if (_doc is not null) { _doc.Close(); _doc = null; }
                _doc = result.Document;
                assignedDocument = true;
                _currentFile = result.WorkingPath;
                // Same reason as the identity block below: a crop reload's DisplayPath is the
                // "<name>.crop-<guid>.pdf" working file, which must not become the tab's name.
                if (!internalReload) SetDisplayName(System.IO.Path.GetFileName(result.DisplayPath));
                // A genuinely different document in this tab starts from the depth default again — the
                // sticky OUTLINES expansion belongs to the file that was open, not to the tab. An
                // internal reload (crop's "<name>.crop-<guid>.pdf") is the SAME document, so it keeps
                // its state, as do SaveTempAndReload and the reopen-after-save, which never come here.
                if (!internalReload) { _ctx.OutlineExpanded.Clear(); _ctx.OutlineExpandSeen = false; }
                _annotations.Clear();
                ClearFormState();
                ClearMeasurement();   // a reading about the document that just went away
                _undoStack.Clear();
                _redoStack.Clear();
                _renderDims.Clear();
                InvalidateRenderCache();
                _contentEditor.ClearCache();
                _allSearchRects.Clear();
                _searchResultPages.Clear();
                _searchPageCursor = -1;
                ClearSecondaryPages();
                ClearSelection();
                RefreshPageList(thumbnails);
                LoadOutlines();
                _ctx.Thumbnails = thumbnails;
                // #399: a different document in this tab — or the same one reloaded at a new
                // geometry, as the crop path does — has no resume point. ApplyViewModeOnOpen
                // below places it from the app-global standing preference, exactly as always.
                _ctx.ViewCaptured = false;
                DropZone.Visibility = Visibility.Collapsed;
                PagePreviewPanel.Visibility = Visibility.Visible;
                if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = true;
                _gridViewToggle.IsEnabled = true;
                _pageJumpBox.IsEnabled = true;
                _pageTotalLabel.Text = $"/ {_doc.PageCount}";
                SyncSidebarToDocState(hasDoc: true, startup: false);   // a document is up: open the rail
                MarkDirty(false);
                if (!internalReload)
                {
                    _ctx.IsUntitled = false;   // a real on-disk open; merged/imported callers set this true afterward
                    _ctx.WasProtected = result.WasProtected;
                }
                if (_doc.PageCount > 0)
                {
                    PageList.SelectedIndex = 0;
                    // Apply the persisted view mode's layout + open-fit rule once the first page
                    // has rendered and layout has settled. DispatcherPriority.Background fires after
                    // all pending RenderPage / RefreshPageView callbacks have completed.
                    _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                        (Action)ApplyViewModeOnOpen);
                }
                var readOnlySuffix = result.OpenedReadOnly ? " (read-only - owner restrictions)" : string.Empty;
                // An owner-restricted file PdfSharpCore could not parse comes back as a PDFium-repaired,
                // decrypted copy: editable, so it must NOT claim read-only, but say the restriction went.
                if (result.RestrictionsRemoved)
                    readOnlySuffix = " (owner restrictions removed)";
                if (result.RecoveredFromRaster)
                    readOnlySuffix = " (recovered - pages rasterized, text not selectable)";
                SetStatus($"Opened {System.IO.Path.GetFileName(result.DisplayPath)}{readOnlySuffix} - {_doc.PageCount} page(s)");
                UpdateTabChrome();

                // OriginalPath is the user's document: the in-place save target and the session
                // entry. A document has one unless WE rebuilt it (raster recovery writes a lossy
                // reconstruction into %TEMP%) or the path simply is not on disk.
                //
                // Living under %TEMP% is deliberately NOT disqualifying: a PDF opened from an email
                // attachment extracts to a temp folder and is still a real document the user expects
                // Ctrl+S to update. The working files TDPdf creates ITSELF (New, merge-on-drop,
                // imported images, zip extraction) are classified where they are created — see
                // FinalizeUnsavedTab and OpenSeparatelyAsync — not by where they happen to live.
                //
                // Assigned unconditionally so reopening into a context that already held a document
                // can never leave the previous file's path behind. Skipped entirely for an internal
                // reload, which keeps the tab pointed at the document the user actually opened.
                if (!internalReload)
                {
                    bool hasRealHome = !result.RecoveredFromRaster && System.IO.File.Exists(result.DisplayPath);
                    _ctx.OriginalPath = hasRealHome ? result.DisplayPath : null;
                    // The recent list keeps its own stricter gate — temp paths are correctly excluded
                    // from it even when they are a perfectly good save target.
                    if (hasRealHome && IsRecentEligiblePath(result.DisplayPath)) AddRecentFile(result.DisplayPath);
                }
            }
            catch
            {
                if (!assignedDocument) result.Document.Close();
                throw;
            }
        }

        private static bool IsPasswordException(Exception ex) =>
            ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("protected", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("encrypted", StringComparison.OrdinalIgnoreCase) >= 0;

        // Themed "Password Required" prompt. The old inline dialog kept the native OS title bar and
        // used stock PasswordBox/Button chrome, which rendered as light Aero controls on a dark
        // panel; TdpDialog gives it the same borderless wordmark chrome as every other TDPdf dialog.
        // Enter/Esc and the Open/Cancel semantics (null == cancelled) are unchanged.
        private string? PromptForPassword(string filename) => TdpDialog.PromptPassword(this, filename);

        private void RefreshPageList(IReadOnlyList<BitmapSource?>? thumbnails = null)
        {
            PageList.Items.Clear();
            if (_doc is null) return;

            for (int i = 0; i < _doc.PageCount; i++)
            {
                BitmapSource? thumb = thumbnails is not null && i < thumbnails.Count ? thumbnails[i] : null;
                var img = new Image
                {
                    Source = thumb,
                    Width = 140,
                    Height = thumb is not null ? 140.0 * thumb.PixelHeight / thumb.PixelWidth : 100,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 0, 0, 2)
                };

                var label = new TextBlock
                {
                    Text = $"Page {i + 1}",
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                if (thumb is not null)
                {
                    var border = new Border
                    {
                        Background = Brushes.White,
                        BorderBrush = BrushResource("BorderDim"),
                        BorderThickness = new Thickness(1),
                        Child = img
                    };
                    panel.Children.Add(border);
                }
                else
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = $"Page {i + 1}",
                        Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 13,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 20, 0, 20)
                    });
                }
                panel.Children.Add(label);
                PageList.Items.Add(panel);
            }
        }

        private void UpdateCurrentDpiScale()
        {
            _currentDpiScale = GetCurrentDpiScaleFromVisual();
        }

        private double GetCurrentDpiScaleFromVisual()
        {
            var source = PresentationSource.FromVisual(this);
            var transform = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            return transform.M11 > 0 ? transform.M11 : 1.0;
        }

        private int GetCurrentDpiX()
        {
            // LayoutZoomScale, not the true zoom: this sizes the RASTER, which has to match the
            // tile's on-screen pixels 1:1. (Rendering at the true zoom would over-sample by the
            // display factor and, at 400%, allocate a needlessly larger bitmap.)
            return Math.Max(1, (int)Math.Round(_currentDpiScale * LayoutZoomScale * 96.0));
        }

        // #189 (upstream KillerPDF PR #194): the one device scale every RASTER budget is measured
        // against. It has to be _currentDpiScale and not VisualTreeHelper.GetDpi(this): both
        // GetDpi and CompositionTarget.TransformToDevice read WPF's HwndTarget.CurrentDpiScale,
        // which is only refreshed when WPF's own internal hook processes WM_DPICHANGED — and our
        // WndProc claims that message (handled = true) so it can apply Windows' suggested rect
        // against the custom chrome. Public HwndSource hooks run BEFORE the internal HwndTarget
        // hook, so handling it there ends the chain and WPF's DPI state never moves. _currentDpiScale
        // is seeded from the visual at SourceInitialized and then taken straight from the message's
        // own wParam in WmDpiChanged, so it is the one value that is right after a monitor move.
        // GetCurrentDpiX (the primary tile) already reads it; the continuous re-sharpen budget and
        // the Grid / Two-Page tile budget go through here so all three rasterize at one density.
        // Windows per-monitor DPI is isotropic, so collapsing X/Y to a single scalar loses nothing.
        private double CurrentRenderDpiScale() => _currentDpiScale > 0 ? _currentDpiScale : 1.0;

        private void InvalidateRenderCache()
        {
            _renderCache.Clear();
            _renderDims.Clear();
            // #135 follow-up: the night-mode image boxes are measured from the document, so they go
            // stale with it (a page rotated, cropped, transformed, or the file re-saved to a fresh
            // temp copy). They are keyed by file path as well, so this is belt and braces.
            _pageImageRects.Clear();
        }

        // #122 (upstream v1.6.3): the per-tab rendered-page cache used to grow without bound — a page
        // was added on every visit and never evicted, so paging through a long document pinned a
        // bitmap per page. Cap it and, when over, drop the entries whose page is FARTHEST from the one
        // just rendered: renders cluster around the viewport, so the farthest are least likely next.
        private const int RenderCachePageCap = 48;

        // #189 (upstream KillerPDF v1.7.2): the count cap alone was not enough. An entry's size
        // scales with the page and the render budget, so 48 cached Letter pages can hold ~630 MB in
        // ONE tab — and every open tab keeps its own cache. Budget the cache in BYTES as well, with
        // a floor of nearby pages so the moving window around the viewport still serves instantly.
        private const long RenderCacheByteBudget = 160L << 20;   // ~160 MB per tab
        private const int RenderCacheMinPages = 6;

        // Bytes held by this tab's cached page bitmaps.
        //
        // Upstream needs a parallel size dictionary written on the producing thread, because ITS
        // cache is a ConcurrentDictionary filled from background render threads and reading
        // bmp.PixelWidth during eviction was a cross-thread touch on the BitmapSource. We do NOT
        // have that problem: _renderCache is a plain Dictionary written only from RenderPage, whose
        // awaits resume on the UI thread, and our RenderedPage record already carries PixelWidth /
        // PixelHeight as plain ints captured at render time. So measure straight off the record and
        // never touch the BitmapSource — do not "restore" upstream's parallel dictionary here.
        private long RenderCacheBytes()
        {
            long total = 0;
            foreach (var entry in _renderCache.Values)
                total += 4L * entry.PixelWidth * entry.PixelHeight;   // Bgra32
            return total;
        }

        private bool OverRenderCacheBudget()
        {
            int count = _renderCache.Count;
            if (count > RenderCachePageCap) return true;
            return count > RenderCacheMinPages && RenderCacheBytes() > RenderCacheByteBudget;
        }

        private void CapRenderCache(int aroundPage)
        {
            if (!OverRenderCacheBudget()) return;
            var keys = _renderCache.Keys.ToList();
            // Farthest page first.
            keys.Sort((a, b) => Math.Abs(b.pageIndex - aroundPage).CompareTo(Math.Abs(a.pageIndex - aroundPage)));
            foreach (var k in keys)
            {
                if (!OverRenderCacheBudget()) break;
                // Distance 0: this is the page we just rendered (the cache is keyed by DPI bucket
                // too, so it can hold more than one entry for it) and, because the sort put the
                // farthest first, so is every key after it. Nothing sane left to evict. Upstream
                // guards its rescanning eviction loop the same way with `if (bestDist <= 0) break`;
                // ours walks a fixed sorted list, so it terminates whatever the budgets say — this
                // only stops it throwing away the page on screen to chase a budget it cannot meet.
                if (k.pageIndex == aroundPage) break;
                _renderCache.Remove(k);
            }
        }

        private void RerenderCurrentPage()
        {
            int pageIndex = PageList.SelectedIndex;
            if (pageIndex < 0 || _doc is null) return;

            RenderPage(pageIndex);
            ApplyZoom();
            if (_searchBar is not null && _searchBar.Visibility == Visibility.Visible
                && _allSearchRects.Count > 0)
            {
                HighlightSearchResultsOnCurrentPage();
            }
        }

        private async void RenderPage(int pageIndex)
        {
            if (_currentFile is null || _doc is null) return;
            DocumentContext renderContext = _ctx;
            var currentFile = _currentFile;
            _renderCancellationTokenSource?.Cancel();
            _renderCancellationTokenSource?.Dispose();
            _renderCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _renderCancellationTokenSource.Token;
            try
            {
                int dpiX = GetCurrentDpiX();
                SetBusy(true, $"Rendering page {pageIndex + 1}...");
                if (!_renderCache.TryGetValue((pageIndex, dpiX), out var renderedPage))
                {
                    var result = await _pdfDocumentService.RenderPageAsync(currentFile, pageIndex, dpiX, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (result.Bitmap is null || result.Width <= 0 || result.Height <= 0)
                    {
                        _primaryPageBitmap = null;
                        PageImage.Source = null;
                        SetStatus($"Page {pageIndex + 1} - could not render");
                        return;
                    }

                    renderedPage = new RenderedPage(result.Bitmap, result.DipWidth, result.DipHeight, result.Width, result.Height);
                    _renderCache[(pageIndex, dpiX)] = renderedPage;
                    CapRenderCache(pageIndex);
                }

                if (_doc is null) return;

                // #135 follow-up: this page's image boxes, so night mode can carve the pictures back
                // out of the inversion. Off the UI thread on the first inverted render of the page
                // (one PdfPig open, disposed there); a no-op afterwards and whenever night mode or
                // the carve-out is off, so it costs the common path nothing.
                var keepRects = await ImageRectsForAsync(currentFile, pageIndex, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (_doc is null) return;

                _renderDims[pageIndex] = ((int)Math.Round(renderedPage.DisplayWidth), (int)Math.Round(renderedPage.DisplayHeight));
                PageImage.Tag = pageIndex;   // page identity for Grid scroll tracking (nearest-tile counter)
                // #135: DisplayBitmap returns the cached bitmap untouched unless the display-only
                // invert is on, in which case it returns a separate inverted copy. _renderCache (and
                // _primaryPageBitmap, which the image-edit tool bakes into the saved PDF) keep the
                // document's true colors.
                _primaryPageBitmap = renderedPage.Bitmap;
                PageImage.Source = DisplayBitmap(renderedPage.Bitmap, keepRects);
                PageImage.Width = renderedPage.DisplayWidth;
                PageImage.Height = renderedPage.DisplayHeight;
                _annotationCanvas.Width = renderedPage.DisplayWidth;
                _annotationCanvas.Height = renderedPage.DisplayHeight;
                _textEditorCanvas.Width = renderedPage.DisplayWidth;
                _textEditorCanvas.Height = renderedPage.DisplayHeight;
                // #197: the cursor-trailing page tooltip added by #151 is gone — the viewport-corner
                // badge announces the page instead, in one fixed place, for every view mode.
                ShowPageBadge(pageIndex);
                // The display factor is per page, so a document of mixed page sizes has to
                // re-derive the transform when the primary tile changes, at the same true zoom.
                SyncLayoutZoom();
                ClearSelection();
                ClearSecondaryPages();
                RenderAllAnnotations(pageIndex);
                SetStatus($"Page {pageIndex + 1} of {_doc.PageCount} - {Zoom.DisplayText}");
                // Defer additional pages until layout has settled so ActualWidth is valid.
                // RenderPageLinks runs AFTER RenderAdditionalPages so ClearSecondaryPages
                // inside RenderAdditionalPages doesn't wipe the overlays we just added.
                // #115: Background, NOT Loaded. All three of these mutate AnnotationCanvas.Children,
                // and Loaded outranks Render, so the continuation can be dispatched while the layout
                // pass it is meant to follow is still in flight — which is what was tearing the
                // canvas out from under Canvas.MeasureOverride. Background runs strictly after
                // layout completes, which is all "settled" ever meant here.
                int linkBitmapW = renderedPage.PixelWidth;
                int linkBitmapH = renderedPage.PixelHeight;
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    if (cancellationToken.IsCancellationRequested
                        || !ReferenceEquals(_ctx, renderContext)
                        || !string.Equals(_currentFile, currentFile, StringComparison.Ordinal))
                    {
                        return;
                    }
                    RenderAdditionalPages(pageIndex);
                    RenderPageLinks(pageIndex, linkBitmapW, linkBitmapH);
                    RenderFormFields(pageIndex, linkBitmapW, linkBitmapH);
                    // #399: the page bitmap is on screen and the panel is sized, which is the
                    // earliest moment the ScrollViewer's extent is real — so this is where a
                    // re-activated tab's own zoom and scroll offsets are put back.
                    ApplyPendingViewResume();
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _primaryPageBitmap = null;
                PageImage.Source = null;
                SetStatus($"Render error: {ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>
        /// Clears all dynamically-added secondary page borders from the panel,
        /// leaving only the first child (the primary page border).
        /// </summary>
        private void ClearSecondaryPages()
        {
            if (_pageContentPanel is null) return;
            while (_pageContentPanel.Children.Count > 1)
            {
                int last = _pageContentPanel.Children.Count - 1;
                // Null Image.Source before remove so the WriteableBitmap (often several MB on
                // HiDPI) can be collected promptly instead of lingering until WPF’s next GC.
                if (_pageContentPanel.Children[last] is Border border && border.Child is Grid grid)
                {
                    foreach (var child in grid.Children)
                    {
                        if (child is Image img) img.Source = null;
                    }
                }
                _pageContentPanel.Children.RemoveAt(last);
            }
            // NOTE: do NOT reset _pageContentPanel.Width here.  Width is managed exclusively
            // by RenderAdditionalPages (which runs only via Dispatcher) so that no synchronous
            // call to ClearSecondaryPages triggers an intermediate layout pass that would cause
            // the primary page to flash centered and then jerk back to left-aligned.
            // Clear any link overlays from the annotation canvas.
            foreach (var lo in _linkOverlays)
                _annotationCanvas.Children.Remove(lo);
            _linkOverlays.Clear();
        }

        /// <summary>
        /// Keeps the primary page tile's margin in step with the pairing (#193). Every mode but a
        /// book layout's lone cover keeps the XAML default 0,0,12,12: the right 12px is the spread
        /// gutter between the two pages of a Two-Page spread (and the column gutter in Grid), and
        /// the bottom 12px is the row gutter. The cover has nothing to its right, so the gutter
        /// would make it hang left of an empty half.
        /// </summary>
        private void SyncPrimaryTileMargin(bool bookCover)
        {
            if (_pageContentPanel.Children.Count > 0 && _pageContentPanel.Children[0] is Border primaryBorder)
                primaryBorder.Margin = bookCover ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 12, 12);
        }

        /// <summary>
        /// Renders all remaining pages as a grid that wraps based on available viewport width.
        /// The WrapPanel's Width is set to viewport/zoom so WPF handles row-breaking automatically.
        /// Each secondary page is click-to-navigate; annotation tools only work on the primary page.
        /// </summary>
        private async void RenderAdditionalPages(int primaryPageIdx)
        {
            if (_currentFile is null || _doc is null) return;

            // Cancel any in-flight secondary render so stale pages from the previous run
            // don’t land on the panel after the user has navigated or re-zoomed.
            _secondaryRenderCts?.Cancel();
            _secondaryRenderCts = new CancellationTokenSource();
            var ct = _secondaryRenderCts.Token;

            ClearSecondaryPages();

            // Only Grid and Two-Page render secondary tiles into the wrap panel. Single and
            // Continuous never do (Continuous uses its own ContinuousPanel).
            bool twoPage = _viewMode == ViewMode.TwoPage;
            // #193 pairing site 1 of 4: the primary tile's own margin. Book layout's cover has no
            // facing page, so it drops the 12px spread gap and centres like a single page instead
            // of hanging left of an empty slot. Set before the early return below so leaving the
            // cover (or leaving Two-Page altogether) always puts the gap back.
            bool bookCover = IsBookCoverRow(primaryPageIdx);
            SyncPrimaryTileMargin(bookCover);
            if (!_gridViewEnabled && !twoPage)
            {
                _pageContentPanel.Width = double.NaN;
                return;
            }

            double viewportW = PagePreviewPanel.ActualWidth;
            if (viewportW <= 0 || _doc.PageCount <= 1)
            {
                // Single-page document or viewport not yet measured: free the explicit width
                // so the WrapPanel sizes to content and the page stays centred.
                _pageContentPanel.Width = double.NaN;
                return;
            }

            // Snap the WrapPanel width to a whole number of page-width slots.
            // This guarantees panelW * zoomLevel + 24 <= viewportW, so the surrounding
            // Border always has room to be centered by HorizontalAlignment="Center".
            // (Using viewportW / zoom - pad fills the viewport exactly and leaves no room.)
            double primaryPageW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 595;
            // #193 pairing site 2 of 4: the slot width. A book cover is a ONE-page row, and its
            // tile carries no right margin (SyncPrimaryTileMargin above), so its slot is the bare
            // page — otherwise the 12px gutter is counted into a one-slot panel and the cover sits
            // 6px left of centre.
            double pageSlotW = primaryPageW + (bookCover ? 0 : 12); // page width + right-gutter margin
            // Cap how many secondary pages we render at once. Long documents otherwise
            // allocate a (potentially multi-MB) bitmap per page on first grid display.
            // Two-Page renders just the single page to the right of the primary — except a book
            // layout's cover (#193 pairing site 3 of 4), which has no partner at all.
            int maxSecondaryPages = twoPage ? (bookCover ? 0 : 1) : 25;

            // Inner space in pre-zoom (tile-layout) coords, so it is the LAYOUT scale that divides
            // out here, not the true zoom — primaryPageW above is a tile width.
            double availablePreZoom = (viewportW - 24) / Math.Max(0.0001, LayoutZoomScale);
            // Two-Page always shows exactly two columns; Grid wraps to fit the viewport, but never
            // claims more columns than there are tiles to put in them. Without that ceiling a very
            // small page (whose tile is many times its natural size, so its layout scale at the
            // 5% floor is tiny) would ask the WrapPanel for a width of hundreds of thousands of
            // DIPs to hold at most 26 pages.
            // #193 pairing site 4 of 4: sizing the panel for TWO slots parks a lone book cover in
            // the left half of a centred two-slot panel, which reads as left-aligned. One page in
            // the row means one slot.
            int pagesPerRow = twoPage
                ? (bookCover ? 1 : 2)
                : Math.Clamp((int)(availablePreZoom / pageSlotW), 1, maxSecondaryPages + 1);
            double panelW = pagesPerRow * pageSlotW;
            if (panelW > 0) _pageContentPanel.Width = panelW;

            // #189: one authoritative device scale (see CurrentRenderDpiScale) rather than
            // VisualTreeHelper.GetDpi, which does not survive a monitor move here. Both the box
            // below and the bitmap DPI further down have to use the SAME number — scaledMax scales
            // the pixel width up by it and the bitmap DPI divides it back out, so a mismatch would
            // resize the tiles rather than just re-sharpen them. This is density-only: the tile's
            // DIP width works out to RenderBoxDip either way, matching the primary tile, which
            // already sizes its raster off _currentDpiScale via GetCurrentDpiX.
            double dpiScaleX = CurrentRenderDpiScale();
            double dpiScaleY = dpiScaleX;
            // Same square box as the primary tile (PdfDocumentService.RenderBoxDip), in device
            // pixels, so grid tiles land on the same DIP size as the primary and the display
            // factor is one number for the whole wrap panel.
            int scaledMax = (int)(TDPdf.Services.PdfDocumentService.RenderBoxDip * Math.Max(dpiScaleX, dpiScaleY));
            int lastPage = Math.Min(_doc.PageCount - 1, primaryPageIdx + maxSecondaryPages);
            string currentFile = _currentFile;

            List<(int pi, int w, int h, byte[] rawBytes, FracRect[] keep)> pages;
            try
            {
                pages = await Task.Run(() =>
                {
                    var result = new List<(int pi, int w, int h, byte[] rawBytes, FracRect[] keep)>();
                    using var docReader = DocLib.Instance.GetDocReader(currentFile, new PageDimensions(scaledMax, scaledMax));
                    // #135 follow-up: one PdfPig open serves every uncached page in this loop and is
                    // released with the loop (see PigScope).
                    using var pig = new PigScope();
                    for (int i = primaryPageIdx + 1; i <= lastPage; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        using var pageReader = docReader.GetPageReader(i);
                        int w = pageReader.GetPageWidth();
                        int h = pageReader.GetPageHeight();
                        // #141: with the annotations the file carries (see PdfiumInterop).
                        // Form fields stay BAKED here, unlike the primary tile: TDPdf's live
                        // form overlays (RenderFormFields) exist only on _annotationCanvas, so
                        // this surface has nothing to draw the values with. Hiding the widgets
                        // would blank every filled field instead of un-ghosting it.
                        var rawBytes = TDPdf.Services.PdfiumInterop.RenderPageWithAnnotations(currentFile, i, w, h)
                                       ?? pageReader.GetImage();
                        if (w <= 0 || h <= 0 || rawBytes is null) continue;
                        // Measured here rather than on the UI thread below, so the parse never
                        // stalls the tile pass.
                        result.Add((i, w, h, rawBytes, _docInvert ? ImageRectsFor(currentFile, i, pig) : []));
                    }
                    return result;
                }, ct);
            }
            catch (OperationCanceledException) { return; }
            catch { return; /* non-critical; primary page already visible */ }

            if (ct.IsCancellationRequested) return;

            foreach (var (pi, w, h, rawBytes, keep) in pages)
            {
                if (ct.IsCancellationRequested) return;

                _renderDims[pi] = (w, h);
                // #135: display-only invert, with the page's pictures carved back out (empty keep =
                // the plain full-page flip). The buffer is ours and is about to become a throwaway
                // display bitmap, so flip it in place — nothing else ever sees these bytes.
                if (_docInvert) InvertBgraInPlaceExcept(rawBytes, w, h, keep);
                var bitmap = new WriteableBitmap(w, h, 96.0 * dpiScaleX, 96.0 * dpiScaleY, PixelFormats.Bgra32, null);
                bitmap.WritePixels(new Int32Rect(0, 0, w, h), rawBytes, w * 4, 0);

                var img = new Image { Source = bitmap, Stretch = Stretch.None };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

                // #197: no per-tile page tooltip anymore — it trailed the cursor across the tiles
                // and read as noise; the corner badge is the page indicator now. The name stays as
                // an AutomationProperties value so a screen reader can still identify the tile,
                // which is all the tooltip ever contributed to the accessibility tree.
                var overlay = new Canvas
                {
                    Width = w, Height = h,
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand
                };
                AutomationProperties.SetName(overlay, $"Page {pi + 1}");
                AutomationProperties.SetHelpText(overlay, "Click to make this the current page.");
                overlay.PreviewMouseLeftButtonDown += (_, _) => PageList.SelectedIndex = pi;

                var pageGrid = new Grid();
                pageGrid.Children.Add(img);
                pageGrid.Children.Add(overlay);
                // Add link overlays on top of the full-page nav overlay so PDF links
                // in secondary pages are clickable and navigate to their targets directly.
                AddSecondaryPageLinks(pi, pageGrid, w, h);

                // Uniform right+bottom margin gives consistent gutters in both dimensions.
                _pageContentPanel.Children.Add(new Border
                {
                    Background = Brushes.White,
                    Margin = new Thickness(0, 0, 12, 12),
                    Child = pageGrid,
                    Tag = pi   // page identity for Grid scroll tracking (nearest-tile counter)
                });
            }
        }

        // A "held" status briefly outranks routine ones. Scrolling the wheel over the logo to
        // resize the app has to show "App size N%", but the chrome resize immediately re-runs the
        // fit pipeline, whose "Page x of y - 100%" would stomp the readout on the very next
        // layout pass. While a hold is live, plain SetStatus calls are dropped; the hold refreshes
        // on every wheel notch and expires on its own, after which normal statuses flow again.
        // The hold is short and covers only that stomp — the readout's own five-second lifetime is
        // ShowScaleReadout's job (AppScale.cs). Put here rather than at the ~200 SetStatus callers
        // because SetBusy / SetFileOperationBusy / SetWorkerStatus all funnel through this line.
        private DateTime _statusHoldUntil = DateTime.MinValue;

        private void SetStatus(string text)
        {
            if (DateTime.UtcNow < _statusHoldUntil) return;   // a held message is showing
            StatusText.Text = text;
        }

        /// <summary>
        /// Writes a status that plain <see cref="SetStatus"/> calls cannot overwrite for
        /// <paramref name="holdMs"/> milliseconds.
        /// </summary>
        private void SetStatusHeld(string text, int holdMs = 1200)
        {
            _statusHoldUntil = DateTime.UtcNow.AddMilliseconds(holdMs);
            StatusText.Text = text;
        }

        // ---- Transient status readouts -------------------------------------------------------
        // One snapshot / hold / restore for every "flash something on the status line, then put back
        // what was there" caller: the app-scale readout (AppScale.cs) and the #status-line file size
        // below. Whatever was showing before the FIRST flash of a burst is snapshotted and put back,
        // but only if the readout is still the text on screen — so a real status written after the
        // hold lapsed is never replaced by a stale one. The restore assigns StatusText directly
        // rather than going through SetStatus: this is putting a line back, not reporting something
        // new. Normal priority rather than DispatcherTimer's default Background, so a busy render
        // cannot leave the readout parked on the footer.
        private System.Windows.Threading.DispatcherTimer? _statusFlashTimer;
        private string _statusFlashWas  = string.Empty;
        private string _statusFlashText = string.Empty;

        /// <param name="holdMs">How long plain <see cref="SetStatus"/> calls are suppressed.</param>
        /// <param name="life">How long the readout stays on screen before the old line comes back.</param>
        private void FlashStatus(string text, int holdMs, TimeSpan life)
        {
            if (_statusFlashTimer is null)
            {
                _statusFlashTimer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Normal);
                _statusFlashTimer.Tick += (_, _) =>
                {
                    _statusFlashTimer!.Stop();
                    if (StatusText.Text == _statusFlashText) StatusText.Text = _statusFlashWas;
                };
            }

            // Only the first flash of a burst snapshots; the rest would capture our own readout.
            if (!_statusFlashTimer.IsEnabled) _statusFlashWas = StatusText.Text;
            _statusFlashTimer.Stop();
            _statusFlashTimer.Interval = life;
            _statusFlashText = text;
            SetStatusHeld(text, holdMs);
            _statusFlashTimer.Start();
        }

        // Clicking the status line (or Shift+F4) flashes the open document's file size for a beat and
        // then puts back whatever was showing — upstream KillerPDF v1.7.2. Held so page-change chatter
        // cannot overwrite it mid-read.
        private void StatusText_Click(object sender, MouseButtonEventArgs e) => ShowCurrentFileSize();

        private void ShowCurrentFileSize()
        {
            // The user's real document, not the %TEMP% working copy a structural edit repoints us at;
            // falls back to the working path for a never-saved (New / merged-on-drop) document.
            string? path = _ctx.OriginalPath ?? _currentFile;
            if (_doc is null || string.IsNullOrEmpty(path)) return;
            long bytes;
            try
            {
                if (!File.Exists(path)) return;
                bytes = new FileInfo(path).Length;
            }
            catch { return; }   // a vanished / unreadable file is not worth a dialog
            FlashStatus($"{System.IO.Path.GetFileName(path)} — {FormatFileSize(bytes)}",
                        holdMs: 2500, life: TimeSpan.FromMilliseconds(2600));
        }

        /// <summary>Human-readable file size. Shared by the Document Info summary and the status-line
        /// flash so the two never disagree about how big a document is.</summary>
        private static string FormatFileSize(long bytes)
            => bytes >= 1L << 20 ? $"{bytes / (double)(1 << 20):N1} MB"
             : bytes >= 1L << 10 ? $"{bytes / (double)(1 << 10):N0} KB"
             : $"{bytes} bytes";

        // ---- Footer page-size chip (upstream v1.8.5) ----------------------------------------
        // The current page's dimensions, parked next to the zoom and app-size chips and cycling
        // units on each click. Deliberately its own control rather than more work for the status
        // line: the file-size flash above answers a question you ask once and then want gone, this
        // is a number you want sitting in the corner of your eye while you lay a page out. The
        // click gesture on StatusText is untouched.
        //
        // Like everything else down there it lives in the UNSCALED footer — AppScale.cs leaves the
        // title bar and status bar alone on purpose, so the chip holds still under the cursor while
        // it is being clicked through the units — and it never touches the document, so no dirty
        // flag is involved.
        private enum PageSizeUnit { Pixels, Inches, Millimetres, Points }

        private PageSizeUnit _pageSizeUnit;

        /// <summary>Restores the persisted unit. Called from the constructor, beside InitAppScale.</summary>
        private void InitPageSizeReadout()
        {
            try
            {
                // An unrecognised or missing value simply leaves the field at its default (Pixels).
                if (Enum.TryParse(TDPdf.Properties.Settings.Default.PageSizeUnit, out PageSizeUnit saved))
                    _pageSizeUnit = saved;
            }
            catch { /* non-critical user preference */ }
            UpdatePageSizeReadout();
        }

        private void PageSizeReadout_Click(object sender, RoutedEventArgs e)
        {
            _pageSizeUnit = _pageSizeUnit switch
            {
                PageSizeUnit.Pixels      => PageSizeUnit.Inches,
                PageSizeUnit.Inches      => PageSizeUnit.Millimetres,
                PageSizeUnit.Millimetres => PageSizeUnit.Points,
                _                        => PageSizeUnit.Pixels
            };
            try
            {
                TDPdf.Properties.Settings.Default.PageSizeUnit = _pageSizeUnit.ToString();
                TDPdf.Properties.Settings.Default.Save();
            }
            catch { /* persistence is best-effort */ }
            UpdatePageSizeReadout();
        }

        /// <summary>
        /// Repaints the chip from whatever page is current, and hides it outright when no document
        /// is open — an empty workspace has no page to have a size, and a stale "8.5 × 11 in" left
        /// over the start screen would be worse than nothing. Cheap and idempotent, so every
        /// page-change path can simply call it.
        /// </summary>
        private void UpdatePageSizeReadout()
        {
            if (_pageSizeButton is null) return;   // a page change before the constructor's FindName pass
            int idx = CurrentReadoutPage();
            if (_doc is null || idx < 0 || idx >= _doc.PageCount)
            {
                _pageSizeButton.Content = string.Empty;
                _pageSizeButton.Visibility = Visibility.Collapsed;
                return;
            }

            // VisiblePageSize, never PdfPage.Width/Height: it resolves CropBox over MediaBox, walks
            // the page tree for an inherited box, and applies /Rotate exactly once. A cropped or a
            // rotated page therefore reports what the viewer is actually showing.
            var (wPt, hPt) = VisiblePageSize(_doc.Pages[idx]);
            _pageSizeButton.Content = FormatPageSize(wPt, hPt, _pageSizeUnit);
            _pageSizeButton.Visibility = Visibility.Visible;
        }

        // The page the footer is talking about. The page-jump box is the one number every view mode
        // keeps current — Grid tracks the nearest tile into it and deliberately does NOT move
        // PageList.SelectedIndex (a selection change there scroll-jumps and re-renders) — so it is
        // read first, with the sidebar selection as the fallback before it has been filled in.
        private int CurrentReadoutPage()
            => int.TryParse(_pageJumpBox.Text, out int oneBased) ? oneBased - 1 : PageList.SelectedIndex;

        /// <summary>The page's size in the chosen unit, as it reads on the footer chip.</summary>
        /// <remarks>
        /// "Pixels" needs a resolution before it means anything, and the honest one here is 96 DPI:
        /// the page at TRUE 100% zoom (1 pt = 1/72 in, 1 px = 1/96 in), which is exactly what the
        /// zoom chip sitting beside it means by 100%. Deliberately NOT the size of the bitmap
        /// currently on screen — that moves with the zoom, the monitor's DPI scaling and
        /// PdfDocumentService.RenderBoxDip, so one unchanged page would flicker between three
        /// numbers and every one of them would describe this machine rather than the document.
        /// </remarks>
        private static string FormatPageSize(double wPt, double hPt, PageSizeUnit unit) => unit switch
        {
            PageSizeUnit.Pixels      => $"{wPt * 96.0 / 72.0:0} × {hPt * 96.0 / 72.0:0} px",
            PageSizeUnit.Inches      => $"{wPt / 72.0:0.##} × {hPt / 72.0:0.##} in",
            PageSizeUnit.Millimetres => $"{wPt / 72.0 * 25.4:0} × {hPt / 72.0 * 25.4:0} mm",
            _                        => $"{wPt:0} × {hPt:0} pt"
        };

        private void SetBusy(bool isBusy, string? status = null)
        {
            _busyDepth = isBusy ? _busyDepth + 1 : Math.Max(0, _busyDepth - 1);
            Mouse.OverrideCursor = _busyDepth > 0 ? Cursors.Wait : null;
            if (!string.IsNullOrEmpty(status)) SetStatus(status);
        }

        private void SetFileOperationBusy(bool isBusy, string? status = null)
        {
            if (_isFileOperationBusy == isBusy)
            {
                if (!string.IsNullOrEmpty(status)) SetStatus(status);
                return;
            }

            _isFileOperationBusy = isBusy;
            IsEnabled = !isBusy;
            SetBusy(isBusy, status);
        }


        // ============================================================
        // File toolbar handlers
        // ============================================================

        private void New_Click(object sender, RoutedEventArgs e)
        {
            Telemetry.TrackEvent("File.New");
            _ = NewDocumentAsync();
        }

        private void NewDocument() => _ = NewDocumentAsync();

        private async Task NewDocumentAsync()
        {
            // Opens in a new tab — no need to discard the current document.
            string? tempPath = null;
            try
            {
                var newDoc = new PdfDocument();
                newDoc.AddPage(); // one blank A4 page

                tempPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"tdpdf_new_{Guid.NewGuid():N}.pdf");
                newDoc.Save(tempPath);
                newDoc.Close();

                await OpenInTabAsync(tempPath);
                // The working file is a blank PDF TDPdf just wrote to %TEMP%, not a document with a
                // home: Ctrl+S must route to Save As instead of updating a temp copy that is deleted
                // on exit. Not marked dirty — a fresh blank page holds no unsaved work.
                FinalizeUnsavedTab(tempPath, "Untitled.pdf", "New blank document", markDirty: false);
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Could not create new document:\n{ex.Message}",
                    "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void Open_Click(object sender, RoutedEventArgs e)
        {
            Telemetry.TrackEvent("File.Open");
            var dlg = new OpenFileDialog { Filter = "PDF files|*.pdf", Title = "Open PDF", Multiselect = true };
            if (dlg.ShowDialog() != true) return;

            // OpenFileAsync (inside OpenInTabAsync) already handles the common failure cases itself
            // (bad file, wrong password) with its own dialog and keeps going. But OpenInTabAsync's
            // OWN bookkeeping around it — EnsureActiveTabRegistered / ActivateContext /
            // RebuildTabStrip — has no try/catch of its own, so an exception from any of those for
            // file N used to silently abort the whole batch: a multi-select Open of five files could
            // open one and never attempt the other four, with nothing telling the user why.
            int failed = 0;
            foreach (var file in dlg.FileNames)
            {
                try
                {
                    await OpenInTabAsync(file);
                }
                catch (Exception ex)
                {
                    failed++;
                    Telemetry.TrackCrash(ex, "Open.MultiSelect", recoverable: true);
                }
            }
            if (failed > 0)
                SetStatus(dlg.FileNames.Length == 1
                    ? "That file could not be opened."
                    : $"Opened {dlg.FileNames.Length - failed} of {dlg.FileNames.Length} selected file(s) — {failed} failed.");
        }

        private void Merge_Click(object sender, RoutedEventArgs e)
        {
            Telemetry.TrackEvent("File.Merge");
            if (_doc is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            var doc = _doc;
            var dlg = new OpenFileDialog { Filter = "PDF files|*.pdf", Title = "Select PDF to merge", Multiselect = true };
            if (dlg.ShowDialog() != true) return;
            try
            {
                foreach (var file in dlg.FileNames)
                    AppendPdfFileToDoc(doc, file);
                SaveTempAndReload();
                SetStatus($"Merged {dlg.FileNames.Length} file(s) - {_doc?.PageCount} total pages");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Merge failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Appends every page of <paramref name="file"/> to <paramref name="doc"/>, carrying its
        /// named-destination links across. Shared by File ▸ Merge and the #172 Pages-sidebar file drop, so
        /// there is exactly one PDF-append path and both get the link rewriting. Throws on an unreadable /
        /// encrypted source; callers decide whether that aborts the batch or just skips the file.
        /// </summary>
        private void AppendPdfFileToDoc(PdfDocument doc, string file)
        {
            int pageOffset = doc.PageCount;

            // Open twice: Import mode for AddPage, ReadOnly for catalog access.
            using var srcRead = PdfReader.Open(file, PdfDocumentOpenMode.ReadOnly);
            var namedDestMap = BuildNamedDestMap(srcRead);

            using var src = PdfReader.Open(file, PdfDocumentOpenMode.Import);
            for (int i = 0; i < src.PageCount; i++)
                doc.AddPage(src.Pages[i]);

            // Rewrite named-destination links in the newly added pages so they
            // resolve correctly after the catalog is not imported.
            if (namedDestMap.Count > 0)
                RewriteNamedDestLinks(doc, pageOffset, namedDestMap);
        }

        /// <summary>
        /// Builds a map of named destination string → 0-based page index from a source document's
        /// /Dests dictionary and /Names /Dests name tree.
        /// </summary>
        private static Dictionary<string, int> BuildNamedDestMap(PdfDocument src)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                var catalog = src.Internals.Catalog;

                // Legacy flat /Dests dictionary
                var destsDict = catalog.Elements.GetDictionary("/Dests");
                if (destsDict != null)
                {
                    foreach (var key in destsDict.Elements.Keys)
                    {
                        PdfItem? val = DerefItem(destsDict.Elements[key] ?? new PdfInteger(-1));
                        int? idx = ResolveDestPageIndexInDoc(src, val);
                        if (idx.HasValue) map[key.TrimStart('/')] = idx.Value;
                    }
                }

                // Modern /Names /Dests name tree
                var namesDict = catalog.Elements.GetDictionary("/Names");
                var destTree  = namesDict?.Elements.GetDictionary("/Dests");
                if (destTree != null)
                    WalkNameTree(src, destTree, map);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"BuildNamedDestMap: {ex}"); }
            return map;
        }

        private static void WalkNameTree(PdfDocument src, PdfDictionary node, Dictionary<string, int> map)
        {
            var namesArr = node.Elements.GetArray("/Names");
            if (namesArr != null)
            {
                for (int i = 0; i + 1 < namesArr.Elements.Count; i += 2)
                {
                    var keyItem = namesArr.Elements[i];
                    string key  = keyItem is PdfString ks ? ks.Value : keyItem?.ToString()?.TrimStart('/') ?? "";
                    if (string.IsNullOrEmpty(key)) continue;
                    PdfItem? val = DerefItem(namesArr.Elements[i + 1]);
                    int? idx = ResolveDestPageIndexInDoc(src, val);
                    if (idx.HasValue) map[key] = idx.Value;
                }
            }

            var kids = node.Elements.GetArray("/Kids");
            if (kids != null)
            {
                for (int i = 0; i < kids.Elements.Count; i++)
                {
                    if (DerefItem(kids.Elements[i]) is PdfDictionary kid)
                        WalkNameTree(src, kid, map);
                }
            }
        }

        /// <summary>
        /// Resolves a destination value (PdfArray or PdfDictionary with /D) to a page index
        /// within the given source document by matching the page object number.
        /// </summary>
        private static int? ResolveDestPageIndexInDoc(PdfDocument src, PdfItem? val)
        {
            PdfArray? arr = val as PdfArray;
            if (arr is null && val is PdfDictionary vd)
                arr = vd.Elements.GetArray("/D");
            if (arr is null || arr.Elements.Count == 0) return null;

            var first = arr.Elements[0];
            int objNum = GetObjectNumber(first);
            if (objNum > 0)
            {
                for (int i = 0; i < src.PageCount; i++)
                {
                    var pgRef = src.Pages[i].Reference;
                    if (pgRef != null && pgRef.ObjectNumber == objNum) return i;
                }
            }
            else if (first is PdfInteger pi && pi.Value >= 0 && pi.Value < src.PageCount)
            {
                return pi.Value;
            }
            return null;
        }

        /// <summary>
        /// Walks all link annotations in pages [pageOffset, doc.PageCount) and rewrites any
        /// named-destination /D values to explicit [pageRef /Fit] arrays using the merged
        /// document's page references. This is needed because PdfSharpCore's import does not
        /// copy the source document's /Names /Dests catalog entries.
        /// </summary>
        private static void RewriteNamedDestLinks(PdfDocument doc, int pageOffset,
            Dictionary<string, int> namedDestMap)
        {
            for (int pi = pageOffset; pi < doc.PageCount; pi++)
            {
                try
                {
                    var page    = doc.Pages[pi];
                    var annotsArr = page.Elements.GetArray("/Annots");
                    if (annotsArr is null) continue;

                    for (int ai = 0; ai < annotsArr.Elements.Count; ai++)
                    {
                        PdfItem? elem = annotsArr.Elements[ai];
                        PdfDictionary? ann = elem as PdfDictionary
                            ?? (DerefItemStatic(elem) as PdfDictionary);
                        if (ann is null) continue;

                        var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                        if (!subtype.Contains("Link")) continue;

                        // Check /A /D (GoTo action)
                        var actionDict = ann.Elements.GetDictionary("/A");
                        if (actionDict != null)
                        {
                            var s = actionDict.Elements["/S"]?.ToString() ?? "";
                            if (s.Contains("GoTo"))
                            {
                                var destItem = actionDict.Elements["/D"];
                                string? name = ExtractDestName(destItem);
                                if (name != null && namedDestMap.TryGetValue(name, out int srcIdx))
                                {
                                    int targetIdx = pageOffset + srcIdx;
                                    if (targetIdx < doc.PageCount)
                                        actionDict.Elements["/D"] = MakeExplicitDest(doc, targetIdx);
                                }
                            }
                        }
                        else
                        {
                            // Bare /Dest on annotation
                            var destItem = ann.Elements["/Dest"];
                            string? name = ExtractDestName(destItem);
                            if (name != null && namedDestMap.TryGetValue(name, out int srcIdx))
                            {
                                int targetIdx = pageOffset + srcIdx;
                                if (targetIdx < doc.PageCount)
                                    ann.Elements["/Dest"] = MakeExplicitDest(doc, targetIdx);
                            }
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RewriteNamedDestLinks p{pi}: {ex}"); }
            }
        }

        private static string? ExtractDestName(PdfItem? item)
        {
            if (item is null) return null;
            if (item is PdfString ps) return ps.Value;
            if (item is PdfName   pn) return pn.Value.TrimStart('/');
            return null;
        }

        private static PdfArray MakeExplicitDest(PdfDocument doc, int pageIndex)
        {
            var arr = new PdfArray(doc);
            arr.Elements.Add(doc.Pages[pageIndex].Reference);
            arr.Elements.Add(new PdfName("/Fit"));
            return arr;
        }

        // Static version of DerefItem for use in static helpers.
        private static PdfItem DerefItemStatic(PdfItem item)
        {
            var valueProp = item.GetType().GetProperty("Value",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (valueProp?.GetValue(item) is PdfObject resolved) return resolved;
            return item;
        }

        private void Split_Click(object sender, RoutedEventArgs e)
        {
            Telemetry.TrackEvent("File.Split");
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            var currentFile = _currentFile;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { TdpDialog.Show(this, "Select pages to extract."); return; }
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save extracted pages as" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var indices = new List<int>();
                foreach (var item in selected) indices.Add(PageList.Items.IndexOf(item));
                using var importDoc = PdfReader.Open(currentFile, PdfDocumentOpenMode.Import);
                var newDoc = new PdfDocument();
                foreach (var idx in indices.OrderBy(i => i))
                    newDoc.AddPage(importDoc.Pages[idx]);
                newDoc.Save(dlg.FileName);
                SetStatus($"Extracted {indices.Count} page(s) to {System.IO.Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Split failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            var doc = _doc;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { TdpDialog.Show(this, "Select pages to delete."); return; }
            // A PDF cannot have zero pages, and Ctrl+A followed by Delete now makes that a single
            // gesture. Refuse rather than write a document nothing can reopen.
            if (selected.Count >= doc.PageCount)
            {
                TdpDialog.Show(this, "A PDF must keep at least one page.\n\nTo discard the whole document, close it instead.",
                    "TDPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var result = TdpDialog.Show(this, $"Delete {selected.Count} {(selected.Count == 1 ? "page" : "pages")}?", "TDPdf",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            try
            {
                var indices = new List<int>();
                foreach (var item in selected) indices.Add(PageList.Items.IndexOf(item));
                foreach (var idx in indices.OrderByDescending(i => i))
                    doc.Pages.RemoveAt(idx);
                SaveTempAndReload();
                SetStatus($"Deleted {indices.Count} page(s) - {_doc?.PageCount} remaining");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Delete failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InsertBlankPage_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            var doc = _doc;
            int insertAfter = PageList.SelectedIndex >= 0 ? PageList.SelectedIndex : doc.PageCount - 1;

            double currentW = insertAfter >= 0 && insertAfter < doc.PageCount
                ? doc.Pages[insertAfter].Width.Point
                : 612;
            double currentH = insertAfter >= 0 && insertAfter < doc.PageCount
                ? doc.Pages[insertAfter].Height.Point
                : 792;

            var picked = ShowInsertPageDialog(currentW, currentH);
            if (picked is null) return;
            var (wPt, hPt) = picked.Value;

            try
            {
                var blank = new PdfPage { Width = XUnit.FromPoint(wPt), Height = XUnit.FromPoint(hPt) };
                doc.Pages.Insert(insertAfter + 1, blank);
                // Inserting renumbers the pages after the insertion point but does not change the
                // geometry of any of them, so the annotations on those pages are still valid where
                // they are drawn — they just belong to a page one further along. Renumber them and
                // keep them, rather than taking the default clear and losing unsaved work to a page
                // added somewhere else in the document entirely.
                ShiftAnnotationPagesForInsert(insertAfter + 1);
                SaveTempAndReload(keepAnnotations: true);
                PageList.SelectedIndex = insertAfter + 1;
                SetStatus($"Inserted blank page at position {insertAfter + 2}");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Insert failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private (double WidthPt, double HeightPt)? ShowInsertPageDialog(double currentWPt, double currentHPt)
        {
            // (Display name, width pt, height pt). Sizes are in PostScript points (72/in).
            var sizes = new (string Name, double W, double H)[]
            {
                ($"Same as current page ({currentWPt:0}×{currentHPt:0} pt)", currentWPt, currentHPt),
                ("Letter (8.5 × 11 in)",   612, 792),
                ("Legal (8.5 × 14 in)",    612, 1008),
                ("Tabloid (11 × 17 in)",   792, 1224),
                ("A3 (297 × 420 mm)",      842, 1191),
                ("A4 (210 × 297 mm)",      595, 842),
                ("A5 (148 × 210 mm)",      420, 595)
            };

            var bgDark   = (SolidColorBrush)FindResource("BgDark");
            var bgPanel  = (SolidColorBrush)FindResource("BgPanel");
            var borderDim = (SolidColorBrush)FindResource("BorderDim");
            var textPrimary = (SolidColorBrush)FindResource("TextPrimary");
            var textSecondary = (SolidColorBrush)FindResource("TextSecondary");
            var accent = (SolidColorBrush)FindResource("AccentGreen");
            var danger = (SolidColorBrush)FindResource("DangerRed");

            var win = new Window
            {
                Title = "Insert Blank Page",
                Width = 380, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = bgDark,
                Foreground = textPrimary,
                ShowInTaskbar = false,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12
            };

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock
            {
                Text = "Page size",
                Foreground = textSecondary,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var sizeBox = new ComboBox
            {
                Style = (Style)FindResource("DarkComboBox"),
                Height = 28
            };
            foreach (var s in sizes) sizeBox.Items.Add(s.Name);
            // "Custom…" sits one past the end of the presets, so its combo index IS sizes.Length —
            // every custom-only branch below tests that rather than a magic number.
            int customIndex = sizes.Length;
            sizeBox.Items.Add("Custom…");
            sizeBox.SelectedIndex = 0;
            root.Children.Add(sizeBox);

            // ---- Custom size (revealed only while "Custom…" is the selection) ----
            // Points are the PDF's own unit, but nobody buys paper in points, so the entry is a
            // width/height pair plus a unit picker and the conversion to points happens on the way
            // out. The floor and ceiling are the format's, not ours: PDF 32000-1 puts a hard 14400
            // pt (200 in) limit on a page side, and a page thinner than a few points is a file no
            // viewer will draw anything on.
            const double MinSidePt = 3.0;
            const double MaxSidePt = 14400.0;

            // (display name, points per unit, format for the seeded value)
            var units = new (string Name, double PtPer, string Fmt)[]
            {
                ("inches",      72.0,        "0.##"),
                ("millimetres", 72.0 / 25.4, "0.#"),
                ("points",      1.0,         "0.#")
            };

            TextBox NumBox() => new()
            {
                Width = 74,
                Height = 28,
                Foreground = textPrimary,
                Background = bgPanel,
                BorderBrush = borderDim,
                BorderThickness = new Thickness(1),
                CaretBrush = accent,
                Padding = new Thickness(6, 4, 6, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right
            };

            var customPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            var customRow = new StackPanel { Orientation = Orientation.Horizontal };
            var widthBox = NumBox();
            var heightBox = NumBox();
            var unitBox = new ComboBox
            {
                Style = (Style)FindResource("DarkComboBox"),
                Height = 28,
                Width = 120,
                Margin = new Thickness(8, 0, 0, 0)
            };
            foreach (var u in units) unitBox.Items.Add(u.Name);
            unitBox.SelectedIndex = 0;

            customRow.Children.Add(widthBox);
            customRow.Children.Add(new TextBlock
            {
                Text = "×",
                Foreground = textSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0)
            });
            customRow.Children.Add(heightBox);
            customRow.Children.Add(unitBox);
            customPanel.Children.Add(customRow);

            // Doubles as the inline validation message (DangerRed) and, once the numbers are good,
            // the point equivalent — so the user can see what the PDF is actually going to get.
            // Deliberately not a second dialog: a modal on top of a modal to say "that is not a
            // number" is the kind of thing this app's dialogs exist to avoid.
            var customNote = new TextBlock
            {
                Foreground = textSecondary,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            };
            customPanel.Children.Add(customNote);
            root.Children.Add(customPanel);

            root.Children.Add(new TextBlock
            {
                Text = "Orientation",
                Foreground = textSecondary,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 14, 0, 6)
            });

            var orient = new StackPanel { Orientation = Orientation.Horizontal };
            var rbPortrait = new RadioButton
            {
                Content = "Portrait", IsChecked = currentWPt <= currentHPt,
                Foreground = textPrimary, Margin = new Thickness(0, 0, 16, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            var rbLandscape = new RadioButton
            {
                Content = "Landscape", IsChecked = currentWPt > currentHPt,
                Foreground = textPrimary,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            orient.Children.Add(rbPortrait);
            orient.Children.Add(rbLandscape);
            root.Children.Add(orient);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 96, Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                Background = bgPanel,
                Foreground = textPrimary,
                BorderBrush = borderDim,
                Cursor = Cursors.Hand,
                IsCancel = true
            };
            var okBtn = new Button
            {
                Content = "Insert",
                Width = 96, Height = 30,
                Background = accent,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                BorderBrush = accent,
                Cursor = Cursors.Hand,
                IsDefault = true
            };
            buttons.Children.Add(cancelBtn);
            buttons.Children.Add(okBtn);
            root.Children.Add(buttons);

            win.Content = new Border
            {
                Background = bgPanel,
                BorderBrush = borderDim,
                BorderThickness = new Thickness(1),
                Child = root
            };

            // ---- Custom-size validation and state ----
            // Reads both boxes in the selected unit. Rejects anything that is not a number, plus
            // zero / negative / absurd sizes, and names the first problem it finds.
            bool TryReadCustom(out double wPt, out double hPt, out string problem)
            {
                double ptPer = units[Math.Max(0, unitBox.SelectedIndex)].PtPer;
                wPt = hPt = 0;
                if (!double.TryParse(widthBox.Text.Trim(), out double w) ||
                    !double.TryParse(heightBox.Text.Trim(), out double h))
                {
                    problem = "Enter a number for both the width and the height.";
                    return false;
                }
                wPt = w * ptPer;
                hPt = h * ptPer;
                if (double.IsNaN(wPt) || double.IsNaN(hPt) || double.IsInfinity(wPt) || double.IsInfinity(hPt))
                {
                    problem = "Those dimensions are not a usable page size.";
                    return false;
                }
                if (wPt < MinSidePt || hPt < MinSidePt)
                {
                    problem = $"Too small — each side must be at least {MinSidePt / ptPer:0.###} {units[Math.Max(0, unitBox.SelectedIndex)].Name}.";
                    return false;
                }
                if (wPt > MaxSidePt || hPt > MaxSidePt)
                {
                    problem = $"Too large — a PDF page cannot exceed {MaxSidePt / ptPer:0.##} {units[Math.Max(0, unitBox.SelectedIndex)].Name} (14400 pt) on a side.";
                    return false;
                }
                problem = string.Empty;
                return true;
            }

            void ValidateCustom()
            {
                if (sizeBox.SelectedIndex != customIndex) return;
                bool valid = TryReadCustom(out double wPt, out double hPt, out string problem);
                customNote.Text = valid ? $"= {wPt:0.#} × {hPt:0.#} pt" : problem;
                customNote.Foreground = valid ? textSecondary : danger;
                okBtn.IsEnabled = valid;
            }

            void SyncCustomState()
            {
                bool custom = sizeBox.SelectedIndex == customIndex;
                customPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
                // Portrait/Landscape is disabled for a custom size, deliberately. The two boxes
                // already say which way round the page is; swapping the numbers someone just typed
                // in — and having the dialog decide 5 × 7 really meant 7 × 5 — reads as the entry
                // being ignored. The radios come back the moment a preset is selected again.
                rbPortrait.IsEnabled = !custom;
                rbLandscape.IsEnabled = !custom;
                if (custom) ValidateCustom(); else okBtn.IsEnabled = true;
            }

            // Re-expresses whatever is in the boxes when the unit changes, so picking "millimetres"
            // after typing 8.5 x 11 inches gives 215.9 x 279.4 rather than a 8.5 mm page. Unparsable
            // text is left exactly as typed for the user to fix.
            int lastUnit = unitBox.SelectedIndex;
            unitBox.SelectionChanged += (_, _) =>
            {
                int now = Math.Max(0, unitBox.SelectedIndex);
                double from = units[Math.Max(0, lastUnit)].PtPer;
                double to = units[now].PtPer;
                if (double.TryParse(widthBox.Text.Trim(), out double w))
                    widthBox.Text = (w * from / to).ToString(units[now].Fmt);
                if (double.TryParse(heightBox.Text.Trim(), out double h))
                    heightBox.Text = (h * from / to).ToString(units[now].Fmt);
                lastUnit = now;
                ValidateCustom();
            };

            // Seed the boxes from the page being inserted after, in the starting unit, so Custom
            // opens on something real to edit rather than two empty boxes.
            widthBox.Text = (currentWPt / units[0].PtPer).ToString(units[0].Fmt);
            heightBox.Text = (currentHPt / units[0].PtPer).ToString(units[0].Fmt);
            widthBox.TextChanged += (_, _) => ValidateCustom();
            heightBox.TextChanged += (_, _) => ValidateCustom();
            sizeBox.SelectionChanged += (_, _) => SyncCustomState();
            SyncCustomState();

            bool ok = false;
            okBtn.Click += (_, _) => { ok = true; win.DialogResult = true; };
            cancelBtn.Click += (_, _) => { ok = false; win.DialogResult = false; };

            win.ShowDialog();
            if (!ok) return null;

            if (sizeBox.SelectedIndex == customIndex)
            {
                // Insert is disabled while the boxes are invalid, so this cannot fail from the UI;
                // the guard stays so a later change to the enable rule fails closed rather than
                // inserting a zero-size page. The orientation radios are disabled here (see
                // SyncCustomState), so the typed numbers are used exactly as entered.
                if (!TryReadCustom(out double customW, out double customH, out _)) return null;
                return (customW, customH);
            }

            var selected = sizes[sizeBox.SelectedIndex];
            double presetW = selected.W;
            double presetH = selected.H;
            if (rbLandscape.IsChecked == true && presetH > presetW) (presetW, presetH) = (presetH, presetW);
            if (rbPortrait.IsChecked == true && presetW > presetH) (presetW, presetH) = (presetH, presetW);
            return (presetW, presetH);
        }

        private void DocumentInfo_Click(object sender, RoutedEventArgs e) => ShowDocumentInfoDialog();

        // F12 / File ▸ Document Info… — view and edit the PDF's Document Information dictionary
        // (Title, Author, Subject, Keywords, Creator) plus a read-only structure summary. Edits are
        // applied to the live PdfSharpCore _doc.Info and the document is marked dirty, so they are
        // written by the normal save pipeline (doc.Save) the next time the user saves.
        private void ShowDocumentInfoDialog()
        {
            var doc = _doc;
            if (doc is null) { TdpDialog.Show(this, "Open a PDF first."); return; }

            var bgDark        = BrushResource("BgDark");
            var bgPanel       = BrushResource("BgPanel");
            var borderDim     = BrushResource("BorderDim");
            var textPrimary   = BrushResource("TextPrimary");
            var textSecondary = BrushResource("TextSecondary");
            var accent        = BrushResource("AccentGreen");

            var win = new Window
            {
                Title = "Document Info",
                Width = 460, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = bgDark,
                Foreground = textPrimary,
                ShowInTaskbar = false,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12
            };

            var root = new StackPanel { Margin = new Thickness(16) };

            // Editable metadata field. Every value is a single-line metadata string (Enter is not a
            // newline), but it wraps and grows up to a cap, then scrolls — so long titles / keyword
            // lists aren't cramped. `tall` gives the keyword field more room.
            TextBox AddField(string label, string? value, bool tall = false)
            {
                root.Children.Add(new TextBlock
                {
                    Text = label,
                    Foreground = textSecondary,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 4)
                });
                var box = new TextBox
                {
                    Text = value ?? "",
                    Foreground = textPrimary,
                    Background = bgPanel,
                    BorderBrush = borderDim,
                    BorderThickness = new Thickness(1),
                    CaretBrush = accent,
                    Padding = new Thickness(6, 4, 6, 4),
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = false,
                    VerticalContentAlignment = VerticalAlignment.Top,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = tall ? 110 : 72,
                    Margin = new Thickness(0, 0, 0, 12)
                };
                root.Children.Add(box);
                return box;
            }

            var titleBox    = AddField("Title",    doc.Info.Title);
            var authorBox   = AddField("Author",   doc.Info.Author);
            var subjectBox  = AddField("Subject",  doc.Info.Subject);
            var keywordsBox = AddField("Keywords", doc.Info.Keywords, tall: true);
            var creatorBox  = AddField("Creator",  doc.Info.Creator);

            root.Children.Add(new TextBlock
            {
                Text = BuildDocumentInfoSummary(doc, _currentFile),
                Foreground = textSecondary,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 96, Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                Background = bgPanel,
                Foreground = textPrimary,
                BorderBrush = borderDim,
                Cursor = Cursors.Hand,
                IsCancel = true
            };
            var saveBtn = new Button
            {
                Content = "Save",
                Width = 96, Height = 30,
                Background = accent,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                BorderBrush = accent,
                Cursor = Cursors.Hand,
                IsDefault = true
            };
            buttons.Children.Add(cancelBtn);
            buttons.Children.Add(saveBtn);
            root.Children.Add(buttons);

            win.Content = new Border
            {
                Background = bgPanel,
                BorderBrush = borderDim,
                BorderThickness = new Thickness(1),
                Child = root
            };

            cancelBtn.Click += (_, _) => { win.DialogResult = false; };
            saveBtn.Click += (_, _) =>
            {
                doc.Info.Title    = titleBox.Text;
                doc.Info.Author   = authorBox.Text;
                doc.Info.Subject  = subjectBox.Text;
                doc.Info.Keywords = keywordsBox.Text;
                doc.Info.Creator  = creatorBox.Text;
                MarkDirty(true);
                win.DialogResult = true;
            };

            win.Loaded += (_, _) => titleBox.Focus();
            win.ShowDialog();
        }

        // Read-only structure summary for the Document Info dialog: Producer (may throw — guarded),
        // page count, PDF version, creation date (if present — guarded), and file size in KB.
        private static string BuildDocumentInfoSummary(PdfDocument doc, string? filePath)
        {
            var parts = new List<string>();
            string producer = ""; try { producer = doc.Info.Producer ?? ""; } catch { }
            if (producer.Length > 0) parts.Add($"Producer: {producer}");
            parts.Add($"{doc.PageCount} pages");
            parts.Add($"PDF {doc.Version / 10}.{doc.Version % 10}");
            try { var d = doc.Info.CreationDate; if (d != default) parts.Add($"created {d:yyyy-MM-dd HH:mm}"); } catch { }
            try { if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath)) parts.Add(FormatFileSize(new FileInfo(filePath).Length)); } catch { }
            return string.Join("\n", parts);
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelectedPages(-1);

        private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelectedPages(+1);

        /// <summary>
        /// Moves the selected pages one place up (<paramref name="direction"/> -1) or down (+1),
        /// through the same block-move path the thumbnail drag uses.
        /// </summary>
        /// <remarks>
        /// #135: a contiguous run travels as a block, so the menu rows agree with the drag rather
        /// than being a second, one-page-only way to reorder. A NON-contiguous selection keeps the
        /// old single-page behaviour on purpose: gathering pages 2, 5 and 9 into a run is a real
        /// reorder and a reasonable thing to ask for by dragging to a visible insertion line, but it
        /// is not what a menu row reading "Move Page Up" promises, and it would be unrecoverable in
        /// one step.
        /// </remarks>
        private void MoveSelectedPages(int direction)
        {
            if (_doc is null) return;
            int[] from = SelectedPageIndices();
            if (from.Length == 0) return;
            if (from[^1] - from[0] != from.Length - 1)
            {
                if (PageList.SelectedIndex < 0) return;
                from = new[] { PageList.SelectedIndex };
            }

            // The drop gap is counted in the list as it stands, block included — so moving up one
            // place is the gap ABOVE the page before the block (from[0] - 1), and moving down one is
            // the gap BELOW the page after it (from[^1] + 2), which is one further than it looks.
            // PageBlockMove.Compute then subtracts the block itself back out. Out-of-range gaps are
            // exactly the cases with nowhere left to go, and Compute reports them as no-ops.
            int gap = direction < 0 ? from[0] - 1 : from[^1] + 2;
            if (gap < 0 || gap > _doc.PageCount) return;
            MovePageBlock(TDPdf.Services.PageBlockMove.Compute(_doc.PageCount, from, gap));
        }

        private async void SaveInPlace_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            // New / merged-on-drop / imported-image / raster-recovered docs have no real on-disk home
            // (their working file is a temp copy), so an in-place save would silently write to
            // %TEMP%. Route them to Save As. OriginalPath — not _currentFile — is the destination.
            if (_ctx.IsUntitled || string.IsNullOrEmpty(_ctx.OriginalPath)) { SaveAs_Click(sender, e); return; }
            if (!ConfirmSaveWithPendingRedactions()) return;
            if (!ConfirmSaveOverDigitalSignature()) return;
            await SaveInPlaceAsync();
        }

        // Pre-save document normalization (ports upstream KillerPDF v1.6.3/v1.6.4 conformance
        // fixes). Every TDPdf save fully rewrites the file through PdfSharpCore, so we scrub three
        // classes of structural corruption immediately before writing. All three are semantic
        // no-ops on healthy documents and also HEAL files damaged by other tools/older builds when
        // re-saved. Called on the UI thread before dispatching any doc.Save(...).
        private static void NormalizeDocumentForSave(PdfDocument doc)
        {
            ScrubEmptyOutlines(doc);         // #103: never write a dangling /Outlines reference
            ScrubDegeneratePageBoxes(doc);   // never write a zero-size /CropBox or /MediaBox (Adobe out-of-range)
            ScrubDeadSignatures(doc);        // a rewrite voids signatures; never ship a dead one (PDF/A 6.4.3)
        }

        private static double RectNum(PdfItem item) =>
            item is PdfReal r ? r.Value : item is PdfInteger n ? n.Value : 0;

        // #103 (upstream v1.6.3): PdfSharpCore's writer can emit the catalog's /Outlines reference
        // without ever writing the (empty, lazily created) outlines object itself - a dangling xref
        // entry that strict parsers, including PdfSharpCore on reopen, refuse. An outlines dictionary
        // with no /First contains no bookmarks, so dropping the entry is a semantic no-op that keeps
        // the file consistent. Real bookmark trees (/First present) are left untouched.
        private static void ScrubEmptyOutlines(PdfDocument doc)
        {
            try
            {
                var cat = doc.Internals.Catalog;
                var item = cat.Elements["/Outlines"];
                if (item is null) return;
                if (DerefItemStatic(item) is not PdfDictionary o || o.Elements["/First"] is null)
                    cat.Elements.Remove("/Outlines");
            }
            catch { /* malformed catalog - leave the save as-is */ }
        }

        // Upstream v1.6.3 (/CropBox), extended here to /MediaBox: PdfSharpCore's PdfPage.MediaBox and
        // .CropBox property GETTERS have create-on-read semantics, so touching page.CropBox - or
        // page.Width/page.Height, which read MediaBox - on a page that carries no such entry plants an
        // empty [0 0 0 0] box into the page dictionary. A zero-size page box saves to disk and Adobe
        // then rejects the page as "dimensions out-of-range" (Chrome falls back to another box, which is
        // why such files still open there). Both boxes are INHERITABLE page attributes, so the pages
        // that get one planted are exactly the ones whose real box lives on an ancestor /Pages node.
        //
        // /CropBox: dropping a degenerate one is a semantic no-op - the page falls back to its MediaBox.
        // /MediaBox: every page needs one, so drop the degenerate entry and re-plant the box the page
        // tree really specifies; only when nothing usable is inheritable do we leave it absent, which at
        // least renders (viewers substitute a default page size) where a zero-size box does not.
        // Both HEAL files damaged by other tools or older builds when re-saved; real boxes are untouched.
        private static void ScrubDegeneratePageBoxes(PdfDocument doc)
        {
            try
            {
                for (int i = 0; i < doc.PageCount; i++)
                {
                    var page = doc.Pages[i];
                    var elements = page.Elements;

                    if (IsDegenerateBox(elements["/CropBox"]))
                        elements.Remove("/CropBox");

                    if (IsDegenerateBox(elements["/MediaBox"]))
                    {
                        elements.Remove("/MediaBox");
                        // With the bad entry gone, ask the page tree what this page's box actually is.
                        // Re-planting it explicitly keeps the page valid no matter how the writer treats
                        // the inherited attribute, and is identical in meaning to inheriting it.
                        if (PdfPageGeometry.ReadInheritedPageBox(page, "/MediaBox") is { Width: > 1, Height: > 1 } box)
                            elements.SetRectangle("/MediaBox",
                                new PdfRectangle(new XPoint(box.X, box.Y), new XPoint(box.Right, box.Top)));
                    }

                    // Upstream v1.7.1 (#169): PDF 32000-1 14.11.2 requires /CropBox to lie INSIDE
                    // /MediaBox. A rotated page could be written with a portrait media box and a
                    // landscape crop box, a malformed combination that strict validators reject and
                    // that leaves renderers disagreeing about the page size. Removing the invalid crop
                    // is lossless: the page falls back to its complete media box rather than clipping
                    // content away. Done AFTER the media-box healing above so the comparison is against
                    // a box that is actually usable, and skipped entirely when it is not — a bad media
                    // box must never be a reason to delete a good crop box.
                    const double outsideTol = 0.01;
                    if (ReadOwnPageBox(elements["/CropBox"]) is { } crop &&
                        PdfPageGeometry.ReadInheritedPageBox(page, "/MediaBox") is { Width: > 1, Height: > 1 } media &&
                        (crop.X     < media.X     - outsideTol || crop.Y   < media.Y   - outsideTol ||
                         crop.Right > media.Right + outsideTol || crop.Top > media.Top + outsideTol))
                        elements.Remove("/CropBox");
                }
            }
            catch { /* malformed page tree - leave the save as-is */ }
        }

        // Reads a page's OWN /MediaBox or /CropBox entry — no /Parent walk — as a normalized PageBox,
        // or null when the entry is absent or cannot be interpreted with certainty. Deliberately
        // stricter than ReadInheritedPageBox, which reads for geometry: this one feeds the DESTRUCTIVE
        // scrub decisions above, so anything ambiguous reads as null and is then left alone rather than
        // deleted. The box can be a parsed PdfArray (loaded from disk), a PdfRectangle (planted in
        // memory by the lazy getter or by GetRectangle writing its conversion back), or an indirect
        // reference to either.
        private static PdfPageGeometry.PageBox? ReadOwnPageBox(PdfItem? item)
        {
            if (item is null) return null;
            if (item is not PdfArray and not PdfRectangle) item = DerefItemStatic(item);

            if (item is PdfRectangle rect)
                return Normalize(rect.X1, rect.Y1, rect.X2, rect.Y2);
            if (item is PdfArray arr && arr.Elements.Count == 4 &&
                arr.Elements[0] is PdfReal or PdfInteger && arr.Elements[1] is PdfReal or PdfInteger &&
                arr.Elements[2] is PdfReal or PdfInteger && arr.Elements[3] is PdfReal or PdfInteger)
                return Normalize(RectNum(arr.Elements[0]), RectNum(arr.Elements[1]),
                                 RectNum(arr.Elements[2]), RectNum(arr.Elements[3]));
            return null;

            static PdfPageGeometry.PageBox Normalize(double x1, double y1, double x2, double y2) =>
                new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        }

        // True when the entry is present, readable as a rectangle, and zero/sub-point sized. Anything we
        // cannot interpret returns false so it is left alone rather than destroyed.
        private static bool IsDegenerateBox(PdfItem? item) =>
            ReadOwnPageBox(item) is { } box && (box.Width < 1 || box.Height < 1);

        // Upstream v1.6.4: a TDPdf save fully REWRITES the file, which mathematically invalidates any
        // existing digital signature: its /ByteRange and digest describe the old bytes (ISO 19005-2,
        // 6.4.3 requires the digest to cover the entire file). Carrying the dead signature forward
        // misleads viewers and fails PDF/A validation, so strip signature VALUES (/V) from signature
        // fields and the catalog's /Perms certification (DocMDP / usage rights) that references them.
        // The empty fields stay and can be re-signed.
        private static void ScrubDeadSignatures(PdfDocument doc)
        {
            try
            {
                doc.Internals.Catalog.Elements.Remove("/Perms");
                // Same walk the warning counts with (PdfSignatureScan). Sharing it is the point:
                // when these were two walks, the warning could go stale the first time one learned
                // about a field shape the other did not — and the failure mode is staying silent
                // while still removing the signature.
                foreach (var field in TDPdf.Services.PdfSignatureScan.SignedFields(doc))
                    field.Elements.Remove("/V");
            }
            catch { /* malformed catalog - leave the save as-is */ }
        }

        /// <summary>
        /// Gate every save on the digital signature it is about to destroy.
        /// </summary>
        /// <remarks>
        /// A TDPdf save rewrites the entire file, so any signature in it no longer covers the bytes
        /// it was made over. <see cref="ScrubDeadSignatures"/> removes it rather than writing one
        /// that fails validation, which is the right call — a broken signature reads as tampering,
        /// where an unsigned document merely reads as unsigned.
        ///
        /// The defect this fixes is not the removal, it is the silence. Opening a signed PDF and
        /// pressing Ctrl+S destroyed the signature with nothing on screen to say so, and the file
        /// on disk was already overwritten by the time anyone could notice. Returns false to
        /// abandon the save, in the shape of <see cref="ConfirmSaveWithPendingRedactions"/>.
        /// </remarks>
        private bool ConfirmSaveOverDigitalSignature()
        {
            if (_doc is null) return true;
            int signed = TDPdf.Services.PdfSignatureScan.SignedFields(_doc).Count;
            bool rights = TDPdf.Services.PdfSignatureScan.HasUsageRights(_doc);
            if (signed == 0 && !rights) return true;

            string what = signed > 0
                ? $"This PDF carries {signed} digital signature{(signed == 1 ? "" : "s")}"
                : "This PDF carries usage rights";
            if (signed > 0 && rights) what += " and usage rights";

            var answer = TdpDialog.ShowYesNo(this,
                $"{what}.\n\n" +
                "Saving rewrites the whole file, which leaves any signature covering bytes that no " +
                "longer exist. TDPdf removes it rather than writing one that fails to validate — a " +
                "broken signature looks like the document was tampered with, an unsigned one only " +
                "looks unsigned.\n\n" +
                "The original file on disk is unchanged until you continue. To keep the signature, " +
                "cancel and work on a copy instead.",
                "Save anyway", "Cancel",
                "Saving Removes the Signature", MessageBoxImage.Warning);
            return answer == MessageBoxResult.Yes;
        }

        // ScrubSigFieldValues used to live here. It is gone rather than left unused: it was the
        // second copy of the /Kids walk, and PdfSignatureScan.SignedFields — which both the scrub
        // and the warning now share — is the first. Keeping a dead duplicate is how the two drift
        // back apart the next time someone edits "the" walker and picks the wrong one.

        /// <summary>
        /// Saves the active document back over the file the user opened. <paramref name="removingPassword"/>
        /// only changes the wording of the success status: the write itself IS the password removal,
        /// because the working document is already decrypted and PdfSharpCore never re-encrypts.
        /// </summary>
        private async Task SaveInPlaceAsync(bool removingPassword = false)
        {
            using var op = Telemetry.StartOperation("SaveInPlace");
            if (_doc is null || _currentFile is null) return;
            CommitActiveTextBox();
            // Capture the destination once. This is the user's real document (OriginalPath), NOT the
            // working path: _currentFile points into %TEMP% after a decrypt-on-open, after any
            // structural edit (SaveTempAndReload) and after a #106 repair, and saving there would
            // update a temp file that is then deleted. Callers with no on-disk home route to Save As
            // before getting here; the fallback keeps this method total.
            string targetFile = _ctx.OriginalPath ?? _currentFile;
            string status = "";

            // The unit of work retried by RunSaveWithRecoveryAsync. Reads _doc fresh each call so a
            // repair (which swaps _doc for a rebuilt copy) is picked up, and re-bakes annotations from
            // _annotations every time, so a retried save keeps all of the user's edits.
            async Task DoSaveAsync()
            {
                var doc = _doc!;
                NormalizeDocumentForSave(doc);   // strip dangling /Outlines, zero-size /CropBox, dead signatures
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0) || HasPendingFormValues;

                if (hasAnnotations)
                {
                    var tempClean = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        $"tdpdf_clean_{Guid.NewGuid():N}.pdf");
                    await _pdfDocumentService.SaveAsync(() => doc.Save(tempClean), CancellationToken.None);
                    DrawAnnotationsOnDocument();
                    ExceptionDispatchInfo? saveError = null;
                    try
                    {
                        await _pdfDocumentService.SaveAtomicAsync(doc.Save, targetFile, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        saveError = ExceptionDispatchInfo.Capture(ex);
                    }

                    doc = await RestoreDocumentAsync(doc, tempClean, CancellationToken.None);
                    saveError?.Throw();
                    status = $"Saved — {System.IO.Path.GetFileName(targetFile)}";
                }
                else
                {
                    await _pdfDocumentService.SaveAtomicAsync(doc.Save, targetFile, CancellationToken.None);
                    status = $"Saved — {System.IO.Path.GetFileName(targetFile)}";
                }
            }

            try
            {
                SetFileOperationBusy(true, "Saving...");
                await RunSaveWithRecoveryAsync(DoSaveAsync);
                MarkDirty(false);
                if (_ctx.WasProtected)
                {
                    // #149: the file on disk no longer carries its password — PdfSharpCore writes no
                    // /Encrypt unless a password is set on the document, and TDPdf cannot re-encrypt.
                    // Say so rather than dropping the protection silently, and clear the flag: from
                    // here on this tab's file is unprotected.
                    _ctx.WasProtected = false;
                    status = removingPassword
                        ? $"Password protection removed — {System.IO.Path.GetFileName(targetFile)}"
                        : status + " (password protection removed)";
                }
                SetStatus(status);
            }
            catch (Exception ex)
            {
                op.Fail(ex);
                Telemetry.TrackEvent("File.SaveFailed", new Dictionary<string, string>
                {
                    ["Operation"]     = "SaveInPlace",
                    ["ExceptionType"] = ex.GetType().FullName ?? "Unknown",
                });
                SetFileOperationBusy(false);
                TdpDialog.Show(this, $"Save failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetFileOperationBusy(false);
            }
        }

        // #149 (upstream KillerPDF v1.6.6): saves the open document back over the user's file with
        // its password protection dropped. There is nothing to strip at save time — the working
        // document has been decrypted since it was opened — so this IS an in-place save; what the
        // command adds is an explicit, named way to ask for it (and the confirmation, because it
        // rewrites the user's file irreversibly). Routed through SaveInPlaceAsync so it gets the
        // same NormalizeDocumentForSave scrubs and #106 repair retry as every other save.
        private async void RemovePassword_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            if (!_ctx.WasProtected)
            {
                TdpDialog.Show(this, "This document is not password protected.",
                    "TDPdf", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // No on-disk home to write back over (merged / imported / raster-recovered): the user
            // has to say where the unprotected copy goes. Save As drops the protection just the same.
            if (_ctx.IsUntitled || string.IsNullOrEmpty(_ctx.OriginalPath)) { SaveAs_Click(sender, e); return; }

            var res = TdpDialog.Show(this,
                $"Save \"{System.IO.Path.GetFileName(_ctx.OriginalPath)}\" without its password protection?\n\n" +
                "The file is rewritten in place and anyone will be able to open it. TDPdf cannot put the password back.",
                "Remove Password", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (res != MessageBoxResult.OK) return;

            await SaveInPlaceAsync(removingPassword: true);
        }

        // Remove Password stays visible for discoverability but is only actionable when the ACTIVE
        // document actually came from a protected file. Recomputed every time the menu opens: the
        // flag is per tab and is cleared by the save that drops the protection.
        private void FileMenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            _removePasswordMenuItem.IsEnabled = _doc is not null && _ctx.WasProtected;
        }

        private async void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            if (!ConfirmSaveWithPendingRedactions()) return;
            if (!ConfirmSaveOverDigitalSignature()) return;
            CommitActiveTextBox();
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save PDF as" };
            // #112: seed the dialog with the document's display name so Save As pre-fills the real
            // filename (not the tdpdf_temp_… working path). Guard every path call: for a merged/
            // imported doc the seed can be null/empty, and Path.GetFileName/GetFileNameWithoutExtension
            // throw on some runtimes — a crash before the dialog opens. A bad seed just opens defaults.
            try
            {
                string? seed = _ctx.DisplayName;
                if (string.IsNullOrWhiteSpace(seed)) seed = _currentFile;
                if (!string.IsNullOrWhiteSpace(seed))
                    dlg.FileName = System.IO.Path.GetFileName(seed);
            }
            catch { /* malformed seed path — just open the dialog with its defaults */ }
            if (dlg.ShowDialog() != true) return;
            using var op = Telemetry.StartOperation("SaveAs");
            string targetFile = dlg.FileName;
            string status = "";

            // Retryable unit of work (see RunSaveWithRecoveryAsync / #106): reads _doc fresh and
            // re-bakes annotations each call so a repaired retry keeps every edit.
            async Task DoSaveAsync()
            {
                var doc = _doc!;
                NormalizeDocumentForSave(doc);   // strip dangling /Outlines, zero-size /CropBox, dead signatures
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0) || HasPendingFormValues;

                if (hasAnnotations)
                {
                    var tempClean = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        $"tdpdf_clean_{Guid.NewGuid():N}.pdf");
                    await _pdfDocumentService.SaveAsync(() => doc.Save(tempClean), CancellationToken.None);
                    DrawAnnotationsOnDocument();
                    ExceptionDispatchInfo? saveError = null;
                    try
                    {
                        await _pdfDocumentService.SaveAtomicAsync(doc.Save, targetFile, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        saveError = ExceptionDispatchInfo.Capture(ex);
                    }

                    doc = await RestoreDocumentAsync(doc, tempClean, CancellationToken.None);
                    saveError?.Throw();
                    status = $"Saved with annotations to {System.IO.Path.GetFileName(targetFile)}";
                }
                else
                {
                    await _pdfDocumentService.SaveAtomicAsync(doc.Save, targetFile, CancellationToken.None);
                    status = $"Saved to {System.IO.Path.GetFileName(targetFile)}";
                }
            }

            try
            {
                SetFileOperationBusy(true, "Saving...");
                await RunSaveWithRecoveryAsync(DoSaveAsync);
                MarkDirty(false);

                // The copy the user just chose is this tab's document from here on: point the tab
                // name and OriginalPath (the in-place save target, the session entry and the recent
                // list) at it, so a following Ctrl+S updates THAT file rather than the one
                // originally opened. The WORKING path (_currentFile) is deliberately left alone:
                // with pending annotations the saved file already has them baked in while
                // _annotations still holds them, so re-rendering from it would draw them twice.
                // OriginalPath is retargeted unconditionally — unlike an OPEN from %TEMP% (an
                // attachment or working artifact with no lasting home), a Save As INTO it is a
                // destination the user explicitly picked, and Ctrl+S must never silently fall back
                // to writing the file they saved away from. Recents keeps its own eligibility gate.
                _ctx.IsUntitled   = false;
                _ctx.OriginalPath = targetFile;
                SetDisplayName(System.IO.Path.GetFileName(targetFile));
                if (IsRecentEligiblePath(targetFile)) AddRecentFile(targetFile);
                if (_ctx.WasProtected)
                {
                    // #149: the saved copy carries no password — PdfSharpCore cannot re-encrypt it.
                    _ctx.WasProtected = false;
                    status += " (password protection removed)";
                }
                SetStatus(status);
            }
            catch (Exception ex)
            {
                op.Fail(ex);
                Telemetry.TrackEvent("File.SaveFailed", new Dictionary<string, string>
                {
                    ["Operation"]     = "SaveAs",
                    ["ExceptionType"] = ex.GetType().FullName ?? "Unknown",
                });
                SetFileOperationBusy(false);
                TdpDialog.Show(this, $"Save failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetFileOperationBusy(false);
            }
        }

        private async void SaveFlattened_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            // Flatten is not redaction, and is the easiest of the three saves to mistake for it.
            // It rasterises the page, so the marked words stop being selectable text — but they are
            // still there, in full, as pixels. Ask before writing a file the user has every reason
            // to believe is safe.
            if (!ConfirmSaveWithPendingRedactions()) return;
            if (!ConfirmSaveOverDigitalSignature()) return;
            CommitActiveTextBox();
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save Flattened PDF" };
            if (dlg.ShowDialog() != true) return;
            using var op = Telemetry.StartOperation("SaveFlattened");
            SetFileOperationBusy(true, "Flattening...");
            string targetFile = dlg.FileName;

            // Retryable unit of work (see RunSaveWithRecoveryAsync / #106): the fragile part is the
            // PdfSharpCore doc.Save that produces the flatten source; the raster flatten itself runs
            // through Docnet. Reads _doc fresh and re-bakes annotations each call.
            async Task DoSaveAsync()
            {
                var doc = _doc!;
                NormalizeDocumentForSave(doc);   // strip dangling /Outlines, zero-size /CropBox, dead signatures
                var pageSizes = GetPageSizes(doc);
                string sourcePath;
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0) || HasPendingFormValues;
                if (hasAnnotations)
                {
                    var tempClean = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tdpdf_clean_{Guid.NewGuid():N}.pdf");
                    var tempBurned = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tdpdf_burned_{Guid.NewGuid():N}.pdf");
                    await _pdfDocumentService.SaveAsync(() => doc.Save(tempClean), CancellationToken.None);
                    DrawAnnotationsOnDocument();
                    ExceptionDispatchInfo? saveError = null;
                    try
                    {
                        await _pdfDocumentService.SaveAsync(() => doc.Save(tempBurned), CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        saveError = ExceptionDispatchInfo.Capture(ex);
                    }

                    doc = await RestoreDocumentAsync(doc, tempClean, CancellationToken.None);
                    saveError?.Throw();
                    sourcePath = tempBurned;
                }
                else
                {
                    var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tdpdf_src_{Guid.NewGuid():N}.pdf");
                    await _pdfDocumentService.SaveAsync(() => doc.Save(temp), CancellationToken.None);
                    sourcePath = temp;
                }

                await _pdfDocumentService.SaveFlattenedAsync(sourcePath, targetFile, pageSizes, CancellationToken.None);
            }

            try
            {
                await RunSaveWithRecoveryAsync(DoSaveAsync);
                MarkDirty(false);
                // #149: a flatten always writes a brand-new rasterized document, so the export is
                // unprotected even when the source was. Say so — but do NOT clear WasProtected:
                // this wrote to a file the user picked, and THIS tab's own document is untouched.
                var flattenNote = _ctx.WasProtected ? " (password protection removed)" : string.Empty;
                SetStatus($"Flattened PDF saved to {System.IO.Path.GetFileName(targetFile)}{flattenNote}");
            }
            catch (Exception ex)
            {
                op.Fail(ex);
                Telemetry.TrackEvent("File.SaveFailed", new Dictionary<string, string>
                {
                    ["Operation"]     = "SaveFlattened",
                    ["ExceptionType"] = ex.GetType().FullName ?? "Unknown",
                });
                SetFileOperationBusy(false);
                TdpDialog.Show(this, $"Flatten failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetFileOperationBusy(false);
            }
        }

        // Export tabular text from every page to a single CSV (which Excel opens
        // directly). Read-only: it never mutates the document, so the dirty flag is
        // untouched. Table detection is heuristic — see TableExtractor.
        private async void ExportTablesCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();

            var baseName = System.IO.Path.GetFileNameWithoutExtension(_ctx.DisplayName);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "tables";
            var dlg = new SaveFileDialog
            {
                Filter = "CSV (Comma delimited)|*.csv",
                Title = "Export Tables to CSV",
                FileName = baseName + ".csv"
            };
            if (dlg.ShowDialog() != true) return;

            using var op = Telemetry.StartOperation("ExportTablesCsv");
            SetFileOperationBusy(true, "Exporting tables...");
            try
            {
                string sourcePath = _currentFile;
                var (csv, pages) = await Task.Run(() => TableExtractor.ExtractAllPagesCsv(sourcePath));
                if (pages == 0)
                {
                    SetFileOperationBusy(false);
                    TdpDialog.Show(this, "No extractable text was found to export.\n\nScanned/image-only PDFs have no selectable text to pull into a table.",
                        "TDPdf", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // UTF-8 with BOM so Excel renders accented characters correctly.
                await Task.Run(() => File.WriteAllText(dlg.FileName, csv, new System.Text.UTF8Encoding(true)));
                SetStatus($"Exported {pages} page(s) of tables to {System.IO.Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                op.Fail(ex);
                Telemetry.TrackEvent("File.ExportFailed", new Dictionary<string, string>
                {
                    ["Operation"]     = "ExportTablesCsv",
                    ["ExceptionType"] = ex.GetType().FullName ?? "Unknown",
                });
                SetFileOperationBusy(false);
                TdpDialog.Show(this, $"Export failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetFileOperationBusy(false);
            }
        }

        private async Task<PdfDocument> RestoreDocumentAsync(PdfDocument currentDoc, string cleanPath, CancellationToken cancellationToken)
        {
            var restoredDoc = await _pdfDocumentService.OpenPdfSharpAsync(cleanPath, PdfDocumentOpenMode.Modify, cancellationToken);
            currentDoc.Close();
            _doc = restoredDoc;
            _currentFile = cleanPath;
            return restoredDoc;
        }

        // #106: Runs a save operation and, if it fails with a recoverable PdfSharpCore parse/serialize
        // error ("Cannot retrieve stream length.", "File streams are not yet implemented", a broken
        // xref, ...), repairs the current document through PDFium and retries the save exactly once.
        // The caller's saveAction re-bakes the in-memory annotations/edits every time it runs (via
        // DrawAnnotationsOnDocument, which reads _annotations — never cleared here), so the retried
        // file preserves all of the user's work. If the repair fails or the retry throws, the (final)
        // exception propagates to the caller's themed "Save failed" handler. Recovery is fully guarded
        // and can never itself crash the save.
        private async Task RunSaveWithRecoveryAsync(Func<Task> saveAction)
        {
            try
            {
                await saveAction();
            }
            catch (Exception ex) when (TDPdf.Services.PdfDocumentService.IsXRefException(ex))
            {
                Telemetry.TrackEvent("File.SaveRecoveryAttempt");
                if (!await TryRepairCurrentDocumentForSaveAsync()) throw;   // PDFium couldn't help — surface original
                await saveAction();                                        // retry once against the repaired source
            }
        }

        // #106: Rebuilds the current document through PDFium (which emits clean stream/xref structures)
        // and reopens it in place so a failed save can be retried against a repaired source. Reuses the
        // shared PdfiumInterop.TryPdfiumRepair helper (no second repair implementation). Fully guarded: returns false
        // — never throws — when repair is not possible, leaving the original failure to surface.
        private async Task<bool> TryRepairCurrentDocumentForSaveAsync()
        {
            var current = _currentFile;
            if (_doc is null || string.IsNullOrEmpty(current)) return false;
            try
            {
                var fixedPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    $"tdpdf_fixed_{Guid.NewGuid():N}.pdf");
                bool ok = await System.Threading.Tasks.Task.Run(
                    () => TDPdf.Services.PdfiumInterop.TryPdfiumRepair(current!, fixedPath));
                if (!ok) return false;
                var repaired = await _pdfDocumentService.OpenPdfSharpAsync(
                    fixedPath, PdfDocumentOpenMode.Modify, CancellationToken.None);
                _doc?.Close();
                _doc = repaired;
                _currentFile = fixedPath;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Per-page sizes for the flatten pass, in points, as the page is DISPLAYED.
        /// </summary>
        /// <remarks>
        /// These sizes become the page boxes of the rebuilt document, so they have to agree with
        /// what PDFium rasterised — and PDFium rasterises the CropBox, rotated. PdfPage.Width/Height
        /// agree on neither: they are MediaBox-derived, and their landscape swap reads /Rotate from
        /// the page's own dictionary, so a quarter turn INHERITED from a /Pages node reads as
        /// portrait. Either mismatch stretches a landscape raster onto a portrait page. Going
        /// through PdfPageGeometry.DisplaySize — the single home for this mapping, and the one the
        /// render, link, form-field and redaction paths already share — fixes both at once.
        /// </remarks>
        private static IReadOnlyList<PdfPageSize> GetPageSizes(PdfDocument doc)
        {
            var pageSizes = new List<PdfPageSize>(doc.PageCount);
            for (int i = 0; i < doc.PageCount; i++)
            {
                var (w, h) = PdfPageGeometry.DisplaySize(doc.Pages[i]);
                pageSizes.Add(new PdfPageSize(w, h));
            }
            return pageSizes;
        }

        private void Print_Click(object sender, RoutedEventArgs e)
        {
            Telemetry.TrackEvent("File.Print");
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();

            // Burn any pending annotations into a temp printable copy, preview/print from
            // that, then reload the clean document afterward so the on-screen editing
            // state is preserved.
            string? restorePath = null;
            string? printablePath = null;
            try
            {
                var pageSizes = new List<Size>(_doc.PageCount);
                for (int i = 0; i < _doc.PageCount; i++)
                    pageSizes.Add(new Size(_doc.Pages[i].Width.Point, _doc.Pages[i].Height.Point));

                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);

                if (hasAnnotations)
                {
                    string cleanPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tdpdf_clean_{Guid.NewGuid():N}.pdf");
                    _doc.Save(cleanPath);
                    restorePath = cleanPath;

                    DrawAnnotationsOnDocument();
                    printablePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tdpdf_print_{Guid.NewGuid():N}.pdf");
                    _doc.Save(printablePath);
                }
                else
                {
                    // No annotations: save a throwaway printable copy so the preview
                    // window never reads the live file out from under us.
                    string tempPrint = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tdpdf_print_{Guid.NewGuid():N}.pdf");
                    try { _doc.Save(tempPrint); printablePath = tempPrint; }
                    catch { printablePath = _currentFile; }
                }

                var preview = new TDPdf.Services.PrintPreviewWindow(this, printablePath, pageSizes);
                bool? printed = preview.ShowDialog();
                SetStatus(printed == true ? $"Sent {preview.PrintedPageCount} page(s) to printer" : "Print canceled");
            }
            catch (Exception ex)
            {
                Telemetry.TrackEvent("File.PrintFailed", new Dictionary<string, string>
                {
                    ["ExceptionType"] = ex.GetType().FullName ?? "Unknown",
                });
                TdpDialog.Show(this, $"Print failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (restorePath is not null)
                    ReloadPrintedDocument(restorePath);

                // Clean up the temp printable copy (the clean/reload copy is now the
                // live document, so never delete that one).
                if (printablePath is not null && printablePath != _currentFile && printablePath != restorePath)
                    try { File.Delete(printablePath); } catch { /* best effort */ }
            }
        }

        private void ReloadPrintedDocument(string path)
        {
            var previous = _doc;
            PdfDocument? reopened = null;
            string reopenedPath = path;
            try
            {
                reopened = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            }
            catch (Exception ex) when (TDPdf.Services.PdfDocumentService.IsOwnerPasswordException(ex))
            {
                // Same trap as PdfDocumentService.OpenCore: PdfSharpCore's ReadOnly parser walks into
                // a broken hint table on a malformed linearized file and throws an array-index error.
                // The throw happens INSIDE this catch clause, so nothing on this try could catch it —
                // and this method runs from Print_Click's finally block, where an escaping exception
                // replaces whatever was already in flight. Contain it, then try a PDFium-repaired copy.
                try
                {
                    reopened = PdfReader.Open(path, PdfDocumentOpenMode.ReadOnly);
                }
                catch
                {
                    var fixedPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        $"tdpdf_fixed_{Guid.NewGuid():N}.pdf");
                    try
                    {
                        if (TDPdf.Services.PdfiumInterop.TryPdfiumRepair(path, fixedPath))
                        {
                            reopened = PdfReader.Open(fixedPath, PdfDocumentOpenMode.Modify);
                            reopenedPath = fixedPath;
                        }
                    }
                    catch { reopened = null; }
                }
            }

            if (reopened is null)
            {
                // Nothing could reopen the pre-print copy. Keep the live document (which now has the
                // annotations burned in) rather than throwing out of Print_Click's finally block.
                SetStatus("Printed - the pre-print copy could not be reloaded; use Save As to keep your work");
                return;
            }

            _doc = reopened;
            _currentFile = reopenedPath;
            previous?.Close();
        }
    }
}
