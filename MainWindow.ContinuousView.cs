// MainWindow — continuous (vertical-strip) view.
// The scrolling multi-page strip: building it, virtualising it, and keeping the current
// page and zoom in sync with it. Extracted verbatim from MainWindow.xaml.cs.

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
        // Continuous (vertical-strip) view
        // ============================================================

        /// <summary>
        /// Builds the continuous strip: one placeholder slot per page sized from the PDF's
        /// natural aspect ratio, then kicks off progressive background rendering. Pages fill
        /// in asynchronously so even very long documents never block the UI thread.
        /// Continuous view is view + navigate only — annotation editing happens in Single,
        /// Two-Page, or Grid view.
        /// </summary>
        private void SetupContinuousView(int initialPage)
        {
            if (_doc is null) return;
            _continuousRenderCts?.Cancel();
            _continuousPanel.Children.Clear();
            _continuousTops.Clear();
            // #130 (upstream v1.6.4): a PDF whose page tree parses to zero pages must not reach the
            // Pages[0] deref below — Continuous view crashed with an out-of-range index. Nothing to
            // lay out, so bail after clearing any stale tiles.
            if (_doc.PageCount == 0) return;

            // Upstream v1.6.3: entering Continuous must restore its own scrollbar setup, since
            // RefreshPageView (which now always leaves this Auto) early-returns for Continuous and
            // never gets a chance to set it. Explicit here so Continuous doesn't inherit whatever a
            // prior mode left behind.
            PagePreviewPanel.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            PagePreviewPanel.VerticalScrollBarVisibility   = ScrollBarVisibility.Auto;

            // PDF natural page width in WPF DIPs (96 DIP/in, 72 pt/in). Zoom-independent so
            // FitToWidth (= viewportW / _continuousPageW) doesn't cancel against the zoom level.
            var refPage = _doc.Pages[0];
            _continuousPageW = Math.Max(200.0, refPage.Width.Point * (96.0 / 72.0));

            double y = 0;
            for (int i = 0; i < _doc.PageCount; i++)
            {
                _continuousTops.Add(y);
                var pdfPage = _doc.Pages[i];
                double pw = pdfPage.Width.Point, ph = pdfPage.Height.Point;
                // PdfSharpCore reports the un-rotated box; swap for quarter rotations so the
                // placeholder aspect matches what Docnet will rasterize.
                int rot = PdfPageGeometry.Rotation(pdfPage);
                if (rot == 90 || rot == 270) (pw, ph) = (ph, pw);
                double ratio = Math.Max(0.1, ph / Math.Max(1, pw));
                double slotH = _continuousPageW * ratio;

                var pageImg = new Image { Stretch = Stretch.None, Width = _continuousPageW, Height = slotH };
                RenderOptions.SetBitmapScalingMode(pageImg, BitmapScalingMode.HighQuality);

                int capturedI = i;
                var placeholder = new Border
                {
                    Width = _continuousPageW,
                    Height = slotH,
                    Margin = new Thickness(0, 0, 0, 12),
                    Background = BrushResource("BgPanel"),
                    Tag = i,
                    Child = pageImg
                };
                // #197: the #151 slot tooltip is gone with the rest of them — in a continuous strip
                // a tooltip that follows the cursor down the whole document was the worst offender.
                // The accessible name stays so the slot is still identifiable to a screen reader.
                AutomationProperties.SetName(placeholder, $"Page {i + 1}");
                AutomationProperties.SetHelpText(placeholder, "Click to make this the current page.");
                placeholder.PreviewMouseLeftButtonDown += (_, _) => SelectContinuousPage(capturedI);
                _continuousPanel.Children.Add(placeholder);
                y += slotH + 12;
            }

            // Entering Continuous flips the display factor to 1, so the layout scale for the zoom
            // already in force changes even though the zoom itself has not.
            SyncLayoutZoom();

            // Continuous opens fit-to-width per the open-fit rules.
            FitToWidth();

            _continuousScrollTarget = initialPage;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                () => ScrollContinuousToPageSuppressed(initialPage));

            _ = RenderContinuousPages();
        }

        /// <summary>Selects a page in continuous view without re-triggering a scroll loop.</summary>
        private void SelectContinuousPage(int pageIndex)
        {
            if (pageIndex < 0 || PageList.SelectedIndex == pageIndex) return;
            _suppressContinuousScrollSync = true;
            PageList.SelectedIndex = pageIndex;
            _suppressContinuousScrollSync = false;
        }

        /// <summary>
        /// Progressively rasterizes every page on a background thread and streams each bitmap
        /// into its placeholder slot as soon as it is ready. Slot heights are corrected from the
        /// actual rendered bitmap so cropped/rotated pages fit cleanly, and scroll offsets are
        /// recomputed so the initial scroll target lands on the right page.
        /// </summary>
        private async System.Threading.Tasks.Task RenderContinuousPages()
        {
            if (_doc is null || _currentFile is null) return;
            _continuousRenderCts?.Cancel();
            _continuousRenderCts = new CancellationTokenSource();
            var cts = _continuousRenderCts;

            // A full base pass repaints every slot, so any hi-res re-sharpen state is now stale (#85):
            // cancel in-flight sharpening and forget which slots were sharpened / their base bitmaps.
            _continuousSharpenCts?.Cancel();
            _continuousWindowCts?.Cancel();   // #122: also stop any in-flight window-maintenance render
            _continuousSharpPages.Clear();
            _continuousBaseBitmaps.Clear();
            _continuousSharpW = 0;

            string currentFile = _currentFile;
            int pageCount = _doc.PageCount;
            double targetW = _continuousPageW;
            int renderW = Math.Max(800, Math.Min(2048, (int)(targetW * 2)));

            // #122: render only the window of pages around the page we're opening at; the rest stay as
            // white scaffold and are filled by MaintainContinuousWindow as they scroll into range. This
            // is what keeps a long image-heavy document from materializing every page bitmap at once.
            int center = _continuousScrollTarget >= 0 ? Math.Min(_continuousScrollTarget, pageCount - 1) : 0;
            int winLo = Math.Max(0, center - ContinuousBaseWindow);
            int winHi = Math.Min(pageCount - 1, center + ContinuousBaseWindow);

            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    using var docReader = DocLib.Instance.GetDocReader(
                        currentFile, new PageDimensions(renderW, renderW * 2));
                    // #135 follow-up: one PdfPig open covers every uncached page this pass fills and
                    // is released with the pass (see PigScope).
                    using var pig = new PigScope();

                    for (int i = winLo; i <= winHi; i++)
                    {
                        if (cts.IsCancellationRequested) return;
                        using var pr = docReader.GetPageReader(i);
                        int w = pr.GetPageWidth();
                        int h = pr.GetPageHeight();
                        // #141: with the annotations the file carries (see PdfiumInterop).
                        // Form fields stay BAKED here, unlike the primary tile: TDPdf's live
                        // form overlays (RenderFormFields) exist only on _annotationCanvas, so
                        // this surface has nothing to draw the values with. Hiding the widgets
                        // would blank every filled field instead of un-ghosting it.
                        var raw = TDPdf.Services.PdfiumInterop.RenderPageWithAnnotations(currentFile, i, w, h)
                                  ?? pr.GetImage();
                        if (w <= 0 || h <= 0 || raw is null) continue;

                        int fi = i, fw = w, fh = h;
                        byte[] bytes = raw;
                        // Measured off the UI thread, so the marshal below stays a pure blit.
                        FracRect[] keep = _docInvert ? ImageRectsFor(currentFile, i, pig) : [];
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (cts.IsCancellationRequested || _viewMode != ViewMode.Continuous) return;
                            if (fi >= _continuousPanel.Children.Count) return;
                            if (_continuousPanel.Children[fi] is not Border slot) return;

                            double dipW = slot.Width;
                            double dipH = dipW * fh / fw;
                            double dpiX = 96.0 * fw / dipW;
                            double dpiY = 96.0 * fh / dipH;

                            // #135: display-only invert, pictures carved back out (empty keep = the
                            // plain full-page flip, which is also what "invert images too" wants).
                            if (_docInvert) InvertBgraInPlaceExcept(bytes, fw, fh, keep);
                            var bmp = new WriteableBitmap(fw, fh, dpiX, dpiY, PixelFormats.Bgra32, null);
                            bmp.WritePixels(new Int32Rect(0, 0, fw, fh), bytes, fw * 4, 0);
                            bmp.Freeze();

                            if (slot.Child is Image pageImg)
                            {
                                pageImg.Source = bmp;
                                pageImg.Width = dipW;
                                pageImg.Height = dipH;
                                slot.Background = Brushes.White;
                                slot.Height = dipH;
                            }

                            // Pages render strictly top-to-bottom, so when page fi finishes every
                            // page above it already has its final height and top. Update only this
                            // page's top from the previous page's finalized bottom (O(1) per page,
                            // avoiding an O(n^2) full rebuild on long documents). Pages below fi are
                            // still placeholders; they correct their own tops as they render.
                            if (fi < _continuousTops.Count)
                            {
                                if (fi == 0)
                                {
                                    _continuousTops[0] = 0;
                                }
                                else
                                {
                                    double prevH = ((FrameworkElement)_continuousPanel.Children[fi - 1]).Height;
                                    if (double.IsNaN(prevH)) prevH = 0;
                                    _continuousTops[fi] = _continuousTops[fi - 1] + prevH + 12;
                                }
                            }

                            // Pages render in order, so once the target page is reached every page
                            // above it has its final height; re-scroll so we land precisely on it.
                            if (_continuousScrollTarget >= 0 && fi >= _continuousScrollTarget)
                            {
                                int tgt = _continuousScrollTarget;
                                _continuousScrollTarget = -1;
                                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                                    (Action)(() =>
                                    {
                                        // #399: a re-activated tab's own offset is finer than the
                                        // page top and was captured against these very slot tops
                                        // at this very zoom, so it wins here — and this is the
                                        // first moment those tops are final, which is exactly why
                                        // the offset could not simply be applied on activation.
                                        if (!TryApplyContinuousResumeScroll())
                                            ScrollContinuousToPageSuppressed(tgt);
                                    }));
                            }
                        });
                    }
                }, cts.Token);
            }
            catch { /* render cancelled or doc closed */ }
        }

        // ── Continuous zoom / high-DPI re-sharpen (#85) ───────────────────────────────────────────
        // Debounced trigger: restart a 250 ms timer on every zoom change or scroll event so the
        // re-sharpen runs once the view settles. Cheap when there's nothing to do (a restore-only
        // pass over an almost-always-empty set below the hi-res threshold).
        private void StartContinuousResharpen()
        {
            if (_viewMode != ViewMode.Continuous) return;
            if (_continuousSharpenTimer is null)
            {
                _continuousSharpenTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(250) };
                _continuousSharpenTimer.Tick += (_, _) =>
                {
                    _continuousSharpenTimer!.Stop();
                    if (_viewMode != ViewMode.Continuous) return;
                    MaintainContinuousWindow();   // #122: render pages entering the window, release those leaving
                    ResharpenContinuousVisible();
                };
            }
            _continuousSharpenTimer.Stop();
            _continuousSharpenTimer.Start();
        }

        // Re-renders ONLY the pages near the viewport at a DPI- and zoom-aware budget and swaps them
        // into their slots; pages that scrolled away (or aren't worth sharpening at this zoom) are
        // restored to their captured base bitmap so the hi-res bitmaps are released. The base render
        // cache is deliberately NOT touched — hi-res bitmaps must never accumulate there. Fully guarded;
        // re-checks _viewMode and cancellation after every await/dispatch.
        private void ResharpenContinuousVisible()
        {
            if (_viewMode != ViewMode.Continuous || _doc is null || _currentFile is null) return;
            if (_continuousTops.Count == 0 || _continuousPanel.Children.Count == 0) return;

            double zoom = Zoom.ZoomLevel;
            double targetW = _continuousPageW;
            int baseW = Math.Max(800, Math.Min(2048, (int)(targetW * 2)));   // same budget as RenderContinuousPages
            // #189 (upstream KillerPDF PR #194): targetW * zoom * dpiScale already IS the page's
            // on-screen size in device pixels, so the extra * 2 this used to carry was a 2× linear
            // supersample on top of an already-correct budget — 4× the pixels and 4× the bytes for
            // detail the display cannot resolve. Because fit-width zoom is viewportW / targetW,
            // targetW cancels and the old hiW reduced to twice the viewport width, which is why the
            // cost tracked window size and display resolution rather than anything about the file.
            // Render at the size we actually draw at. (baseW above is the BASE render budget and is
            // deliberately left alone — upstream did not change it either.)
            double dpiScale = CurrentRenderDpiScale();
            int hiW = (int)Math.Min(4096, targetW * dpiScale * Math.Max(1.0, zoom));

            // Visible slot range. Slot space is zoom-independent (the shared ScaleTransform supplies
            // the zoom), so divide the scroll offsets back down — the same mapping ScrollChanged uses.
            double viewTop = PagePreviewPanel.VerticalOffset / Math.Max(0.01, zoom);
            double viewBot = (PagePreviewPanel.VerticalOffset + PagePreviewPanel.ViewportHeight) / Math.Max(0.01, zoom);
            var visible = new List<int>();
            for (int i = 0; i < _continuousTops.Count && i < _continuousPanel.Children.Count; i++)
            {
                double top = _continuousTops[i];
                double h = ((FrameworkElement)_continuousPanel.Children[i]).Height;
                if (double.IsNaN(h)) continue;
                if (top + h >= viewTop && top <= viewBot) visible.Add(i);
            }
            if (visible.Count > 0)
            {
                // One page of margin either side so a small scroll stays sharp.
                if (visible[0] > 0) visible.Insert(0, visible[0] - 1);
                if (visible[^1] < _continuousTops.Count - 1) visible.Add(visible[^1] + 1);
            }

            // #189: hiW is now a true device-pixel width, so the trigger is simply "has the base
            // render run out of pixels for the size we are drawing it at". The old 1.25× margin was
            // calibrated against a hiW that was inflated 2×; leaving it here would stop the pass
            // firing where it is still needed and pages would be upscaled from the base render.
            // 1.05 is hysteresis only, so a page sitting on the boundary doesn't re-raster on a nudge.
            bool wantHi = hiW >= (int)(baseW * 1.05);

            _continuousSharpenCts?.Cancel();
            _continuousSharpenCts = new CancellationTokenSource();
            var cts = _continuousSharpenCts;

            // Restore pages that were sharpened earlier but have scrolled away (or aren't wanted at
            // this zoom) to their captured base bitmap, releasing their hi-res bitmaps.
            foreach (int p in _continuousSharpPages.ToList())
            {
                if (wantHi && visible.Contains(p)) continue;
                RestoreContinuousBase(p);
                _continuousSharpPages.Remove(p);
            }
            if (!wantHi) return;

            // Zoom changed since the last pass: every sharpened slot is at the wrong budget — redo them.
            bool budgetChanged = hiW != _continuousSharpW;
            _continuousSharpW = hiW;
            var work = visible.Where(p => budgetChanged || !_continuousSharpPages.Contains(p)).ToList();
            if (work.Count == 0) return;

            string currentFile = _currentFile;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                Docnet.Core.Readers.IDocReader? docReader = null;
                // #135 follow-up: one PdfPig open for the whole re-sharpen pass (see PigScope).
                using var pig = new PigScope();
                try
                {
                    foreach (int p in work)
                    {
                        if (cts.IsCancellationRequested) return;
                        docReader ??= DocLib.Instance.GetDocReader(currentFile, new PageDimensions(hiW, hiW * 2));
                        using var pr = docReader.GetPageReader(p);
                        int w = pr.GetPageWidth(), h = pr.GetPageHeight();
                        // #141: with the annotations the file carries (see PdfiumInterop).
                        // Form fields stay BAKED here, unlike the primary tile: TDPdf's live
                        // form overlays (RenderFormFields) exist only on _annotationCanvas, so
                        // this surface has nothing to draw the values with. Hiding the widgets
                        // would blank every filled field instead of un-ghosting it.
                        var raw = TDPdf.Services.PdfiumInterop.RenderPageWithAnnotations(currentFile, p, w, h)
                                  ?? pr.GetImage();
                        if (w <= 0 || h <= 0 || raw is null) continue;

                        int fp = p, fw = w, fh = h;
                        byte[] bytes = raw;
                        // Measured here (off the UI thread); the rects are fractional, so the same
                        // cached set serves this hi-res raster and the base one it replaces.
                        FracRect[] keep = _docInvert ? ImageRectsFor(currentFile, p, pig) : [];
                        if (cts.IsCancellationRequested) return;
                        Dispatcher.Invoke(() =>
                        {
                            if (cts.IsCancellationRequested || _viewMode != ViewMode.Continuous) return;
                            SharpenContinuousSlot(fp, fw, fh, bytes, keep);
                        });
                    }
                }
                catch { /* cancelled or doc closed */ }
                finally { docReader?.Dispose(); }
            }, cts.Token);
        }

        // Swaps a freshly-rendered hi-res bitmap into slot pageIndex, keeping the slot's on-screen size
        // (the shared ScaleTransform still supplies the zoom). Captures the slot's current base bitmap
        // once so RestoreContinuousBase can put it back when the page scrolls away. Only sharpens slots
        // that already carry a base bitmap, so it never fights the streaming base pass.
        private void SharpenContinuousSlot(int pageIndex, int pxW, int pxH, byte[] bgra, FracRect[] keep)
        {
            if (pageIndex < 0 || pageIndex >= _continuousPanel.Children.Count) return;
            if (_continuousPanel.Children[pageIndex] is not Border slot) return;
            if (slot.Child is not Image img) return;
            if (img.Source is not BitmapSource baseSrc) return;   // base not rendered yet — leave it

            double dipW = img.Width;
            if (double.IsNaN(dipW) || dipW <= 0) dipW = slot.Width;
            if (double.IsNaN(dipW) || dipW <= 0) return;
            double dipH = dipW * pxH / pxW;
            double dpiX = 96.0 * pxW / dipW;
            double dpiY = 96.0 * pxH / dipH;

            if (!_continuousBaseBitmaps.ContainsKey(pageIndex))
                _continuousBaseBitmaps[pageIndex] = baseSrc;

            // #135: display-only invert, pictures carved back out (empty keep = full-page flip).
            if (_docInvert) InvertBgraInPlaceExcept(bgra, pxW, pxH, keep);
            var bmp = new WriteableBitmap(pxW, pxH, dpiX, dpiY, PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, pxW, pxH), bgra, pxW * 4, 0);
            bmp.Freeze();
            img.Source = bmp;
            img.Width = dipW;
            img.Height = dipH;
            _continuousSharpPages.Add(pageIndex);
        }

        // Restores a previously-sharpened slot to its captured base bitmap so the hi-res bitmap is
        // released, then forgets the capture. No capture (page never sharpened) = no-op.
        private void RestoreContinuousBase(int pageIndex)
        {
            if (!_continuousBaseBitmaps.TryGetValue(pageIndex, out var baseBmp)) return;
            if (pageIndex >= 0 && pageIndex < _continuousPanel.Children.Count
                && _continuousPanel.Children[pageIndex] is Border slot
                && slot.Child is Image img)
            {
                img.Source = baseBmp;
                img.Width = baseBmp.Width;
                img.Height = baseBmp.Height;
            }
            _continuousBaseBitmaps.Remove(pageIndex);
        }

        // #122 (upstream v1.6.3): scroll-settle maintenance for the virtualized Continuous view. Keeps
        // a window of base bitmaps around the viewport: releases slots that have left the window
        // (Image.Source = null; the slot keeps its height, so nothing reflows) and renders base bitmaps
        // for slots that have entered it and are still bare. The generous ±ContinuousBaseWindow margin
        // means ordinary scrolling always finds a rendered page; only sustained scrolling through a
        // long document trims the far pages. Runs on the UI thread; the render itself is off-thread.
        private void MaintainContinuousWindow()
        {
            if (_viewMode != ViewMode.Continuous || _doc is null || _currentFile is null) return;
            int slotCount = _continuousPanel.Children.Count;
            if (slotCount == 0 || _continuousTops.Count == 0) return;

            double zoom = Math.Max(0.01, Zoom.ZoomLevel);
            double viewTop = PagePreviewPanel.VerticalOffset / zoom;
            double viewBot = (PagePreviewPanel.VerticalOffset + PagePreviewPanel.ViewportHeight) / zoom;
            int firstVis = -1, lastVis = -1;
            for (int i = 0; i < _continuousTops.Count && i < slotCount; i++)
            {
                double top = _continuousTops[i];
                double h = ((FrameworkElement)_continuousPanel.Children[i]).Height;
                if (double.IsNaN(h)) h = 0;
                if (top + h >= viewTop && top <= viewBot) { if (firstVis < 0) firstVis = i; lastVis = i; }
            }
            if (firstVis < 0) { firstVis = 0; lastVis = 0; }   // before first layout: treat the top as visible
            int lo = Math.Max(0, firstVis - ContinuousBaseWindow);
            int hi = Math.Min(slotCount - 1, lastVis + ContinuousBaseWindow);

            // Release every rendered slot outside the window (heights stay, so no reflow / scroll jump).
            for (int i = 0; i < slotCount; i++)
            {
                if (i >= lo && i <= hi) continue;
                if (_continuousPanel.Children[i] is not Border slot || slot.Child is not Image img) continue;
                if (img.Source is null) continue;
                img.Source = null;
                slot.Background = BrushResource("BgPanel");
                _continuousSharpPages.Remove(i);
                _continuousBaseBitmaps.Remove(i);
            }

            // Collect in-window slots that still need a base bitmap.
            var need = new List<int>();
            for (int i = lo; i <= hi; i++)
                if (_continuousPanel.Children[i] is Border slot && slot.Child is Image img && img.Source is null)
                    need.Add(i);
            if (need.Count == 0) return;

            _continuousWindowCts?.Cancel();
            _continuousWindowCts = new CancellationTokenSource();
            var ct = _continuousWindowCts.Token;
            string currentFile = _currentFile;
            int renderW = Math.Max(800, Math.Min(2048, (int)(_continuousPageW * 2)));   // same budget as the base pass

            _ = System.Threading.Tasks.Task.Run(() =>
            {
                Docnet.Core.Readers.IDocReader? docReader = null;
                // #135 follow-up: one PdfPig open for the whole window-fill pass (see PigScope).
                using var pig = new PigScope();
                try
                {
                    foreach (int i in need)
                    {
                        if (ct.IsCancellationRequested) return;
                        docReader ??= DocLib.Instance.GetDocReader(currentFile, new PageDimensions(renderW, renderW * 2));
                        using var pr = docReader.GetPageReader(i);
                        int w = pr.GetPageWidth(), h = pr.GetPageHeight();
                        // #141: with the annotations the file carries (see PdfiumInterop).
                        // Form fields stay BAKED here, unlike the primary tile: TDPdf's live
                        // form overlays (RenderFormFields) exist only on _annotationCanvas, so
                        // this surface has nothing to draw the values with. Hiding the widgets
                        // would blank every filled field instead of un-ghosting it.
                        var raw = TDPdf.Services.PdfiumInterop.RenderPageWithAnnotations(currentFile, i, w, h)
                                  ?? pr.GetImage();
                        if (w <= 0 || h <= 0 || raw is null) continue;
                        int fi = i, fw = w, fh = h; byte[] bytes = raw;
                        FracRect[] keep = _docInvert ? ImageRectsFor(currentFile, i, pig) : [];
                        if (ct.IsCancellationRequested) return;
                        Dispatcher.Invoke(() =>
                        {
                            if (ct.IsCancellationRequested || _viewMode != ViewMode.Continuous) return;
                            ApplyContinuousBaseStable(fi, fw, fh, bytes, keep);
                        });
                    }
                }
                catch { /* cancelled or doc closed */ }
                finally { docReader?.Dispose(); }
            }, ct);
        }

        // Applies a base bitmap into a continuous slot WITHOUT changing the slot's height, so pages
        // below it never move (no scroll jump). Used only by window maintenance; the placeholder height
        // set at layout already matches the page aspect, so the natural-size bitmap fills the slot.
        private void ApplyContinuousBaseStable(int fi, int fw, int fh, byte[] bytes, FracRect[] keep)
        {
            if (fi < 0 || fi >= _continuousPanel.Children.Count) return;
            if (_continuousPanel.Children[fi] is not Border slot || slot.Child is not Image img) return;
            if (img.Source is not null) return;   // already rendered (or sharpened) — don't clobber
            double dipW = slot.Width;
            if (double.IsNaN(dipW) || dipW <= 0) return;
            double dipH = dipW * fh / fw;
            double dpiX = 96.0 * fw / dipW;
            double dpiY = 96.0 * fh / dipH;
            // #135: display-only invert, pictures carved back out (empty keep = full-page flip).
            if (_docInvert) InvertBgraInPlaceExcept(bytes, fw, fh, keep);
            var bmp = new WriteableBitmap(fw, fh, dpiX, dpiY, PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, fw, fh), bytes, fw * 4, 0);
            bmp.Freeze();
            img.Source = bmp;
            img.Width = dipW;
            img.Height = dipH;
            slot.Background = Brushes.White;
        }

        private void ScrollContinuousToPage(int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= _continuousTops.Count) return;
            double target = _continuousTops[pageIndex] * Zoom.ZoomLevel;
            PagePreviewPanel.ScrollToVerticalOffset(target);
        }

        /// <summary>
        /// Programmatically scrolls to a page while suppressing the scroll→selection feedback
        /// loop. ScrollToVerticalOffset raises ScrollChanged on a later layout pass, so the
        /// suppression flag is held until after that callback (cleared at Loaded priority).
        /// </summary>
        private void ScrollContinuousToPageSuppressed(int pageIndex)
        {
            _suppressContinuousScrollSync = true;
            ScrollContinuousToPage(pageIndex);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                (Action)(() => _suppressContinuousScrollSync = false));
        }

        // ── Current-page badge (upstream KillerPDF #197) ───────────────────────────────────────
        // One "page / total" chip in the viewport's bottom-right corner, replacing the per-tile
        // page tooltips (#151) that trailed the cursor and read as noise. It slides up on real
        // scrolling and on a page change, then slides back down once the view has been still for a
        // moment. Suppressed entirely for a one-page document, where it would only ever say
        // "1 / 1". The badge lives outside the page tiles and is IsHitTestVisible="False", so it
        // can never intercept a page click.
        private System.Windows.Threading.DispatcherTimer? _pageBadgeTimer;

        private const double PageBadgeHiddenY = 46;

        private void ShowPageBadge(int page)
        {
            if (_doc is null || _doc.PageCount < 2) return;
            if (page < 0 || page >= _doc.PageCount) return;
            _pageBadgeText.Text = $"{page + 1} / {_doc.PageCount}";
            _pageBadgeSlide.BeginAnimation(TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                        { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                });
            _pageBadge.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            if (_pageBadgeTimer is null)
            {
                _pageBadgeTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(900) };
                _pageBadgeTimer.Tick += (_, _) =>
                {
                    _pageBadgeTimer?.Stop();
                    _pageBadgeSlide.BeginAnimation(TranslateTransform.YProperty,
                        new System.Windows.Media.Animation.DoubleAnimation(
                            PageBadgeHiddenY, TimeSpan.FromMilliseconds(220))
                        {
                            EasingFunction = new System.Windows.Media.Animation.CubicEase
                                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
                        });
                    _pageBadge.BeginAnimation(OpacityProperty,
                        new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
                };
            }
            _pageBadgeTimer.Stop();
            _pageBadgeTimer.Start();
        }

        /// <summary>
        /// Drops the badge immediately, without the slide-out. Used when the document goes away:
        /// the idle timer would otherwise leave it hanging over the start screen for up to a
        /// second. Clearing the animations first is what lets the plain property assignments take
        /// effect — a running animation outranks a local value.
        /// </summary>
        private void HidePageBadgeNow()
        {
            _pageBadgeTimer?.Stop();
            _pageBadgeSlide.BeginAnimation(TranslateTransform.YProperty, null);
            _pageBadge.BeginAnimation(OpacityProperty, null);
            _pageBadgeSlide.Y = PageBadgeHiddenY;
            _pageBadge.Opacity = 0;
        }

        /// <summary>
        /// Tracks scroll position in continuous view: updates the page-number box and the sidebar
        /// thumbnail selection to whichever page is nearest the viewport center.
        /// </summary>
        private void PagePreviewPanel_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // #115: ScrollChanged BUBBLES. Every nested ScrollViewer under the preview panel raises
            // it here — multi-line form-field TextBoxes (VerticalScrollBarVisibility=Auto) and
            // ComboBoxes that RenderFormFields parents into AnnotationCanvas, the signature popup's
            // own scroller, the sidebar. Those fire during their layout, and the Continuous branch
            // below assigns PageList.SelectedIndex, whose handler removes selection chrome from
            // AnnotationCanvas — a synchronous child mutation inside a measure pass. It also meant
            // scrolling a form field could move the current page. Only the panel's own scrolling
            // counts.
            if (!ReferenceEquals(e.OriginalSource, PagePreviewPanel)) return;

            // Grid view (upstream v1.6.4): follow the tile nearest the viewport center so the
            // statusbar page counter tracks scrolling instead of pointing at the last-clicked page.
            // We update only the counter, NOT PageList.SelectedIndex — in Grid a selection change
            // scroll-jumps and re-renders (PageList_SelectionChanged), which would fight the scroll.
            // #197: a real vertical scroll surfaces the corner badge too. Grid's nearest-tile search
            // already runs here, so it reports the page rather than repeating the hunt.
            if (_viewMode == ViewMode.Grid) { UpdateGridCurrentPageCounter(e.VerticalChange != 0); return; }

            if (_viewMode != ViewMode.Continuous || _continuousTops.Count == 0) return;
            // #85: once scrolling settles, sharpen the pages now in view and release the ones that
            // left. Debounced, so streaming base render / rapid scroll just keeps resetting the timer;
            // programmatic scrolls count too (their offset change still moves the visible window).
            StartContinuousResharpen();
            // Ignore scroll events caused by our own programmatic scrolls (sidebar selection,
            // zoom re-anchor, setup) so they don't bounce back into a selection change.
            if (_suppressContinuousScrollSync) return;

            double viewportCenter = (PagePreviewPanel.VerticalOffset + PagePreviewPanel.ViewportHeight * 0.5)
                                    / Math.Max(0.01, Zoom.ZoomLevel);
            int nearest = 0;
            double minDist = double.MaxValue;
            for (int i = 0; i < _continuousTops.Count && i < _continuousPanel.Children.Count; i++)
            {
                double h = ((FrameworkElement)_continuousPanel.Children[i]).Height;
                if (double.IsNaN(h)) h = 0;
                double center = _continuousTops[i] + h * 0.5;
                double dist = Math.Abs(center - viewportCenter);
                if (dist < minDist) { minDist = dist; nearest = i; }
            }

            // #197: surface the position badge on real scrolling, whichever page ends up nearest.
            if (e.VerticalChange != 0) ShowPageBadge(nearest);

            if (PageList.SelectedIndex != nearest)
            {
                _pageJumpBox.Text = (nearest + 1).ToString();
                // Update the sidebar selection without re-scrolling the strip back.
                _suppressContinuousScrollSync = true;
                PageList.SelectedIndex = nearest;
                _suppressContinuousScrollSync = false;
            }
        }

        // Grid scroll tracking (upstream v1.6.4): sets the statusbar page counter to the tile whose
        // center is nearest the viewport center. Each tile carries its page index in its Tag (the
        // primary PageImage tagged in RenderPage, secondaries when appended). Uses TranslatePoint on
        // both tile edges so any grid zoom transform is accounted for. Deliberately leaves
        // PageList.SelectedIndex untouched (a Grid selection change scroll-jumps and re-renders).
        private void UpdateGridCurrentPageCounter(bool showBadge = false)
        {
            if (_doc is null || _pageContentPanel.Children.Count == 0) return;
            double viewportCenterY = PagePreviewPanel.ViewportHeight * 0.5;
            int nearestPage = -1;
            double minDist = double.MaxValue;
            foreach (UIElement child in _pageContentPanel.Children)
            {
                if (child is not FrameworkElement fe || fe.Tag is not int pageIdx || fe.ActualHeight <= 0)
                    continue;
                try
                {
                    double topY    = fe.TranslatePoint(new Point(0, 0), PagePreviewPanel).Y;
                    double bottomY = fe.TranslatePoint(new Point(0, fe.ActualHeight), PagePreviewPanel).Y;
                    double dist = Math.Abs((topY + bottomY) * 0.5 - viewportCenterY);
                    if (dist < minDist) { minDist = dist; nearestPage = pageIdx; }
                }
                catch { /* transform can fail mid-layout; skip this tile */ }
            }
            if (nearestPage >= 0)
            {
                _pageJumpBox.Text = (nearestPage + 1).ToString();
                UpdatePageSizeReadout();   // Grid leaves the selection alone, so this is its only hook
                if (showBadge) ShowPageBadge(nearestPage);   // #197
            }
        }
    }
}
