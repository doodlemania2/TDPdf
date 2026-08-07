using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TDPdf.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf
{
    /// <summary>
    /// One image's placement on a page, as FRACTIONS of the unrotated page with a top-left origin,
    /// so a single cached set serves every render resolution. Produced by
    /// <see cref="TDPdf.Services.PdfImages.GetFracRects"/> and consumed by
    /// <c>MainWindow.InvertBgraInPlaceExcept</c>. Lives here, next to its only consumer, because
    /// TDPdf has no BitmapHelpers class to hang it off (upstream's home for it).
    /// </summary>
    internal readonly record struct FracRect(double L, double T, double R, double B);

    public partial class MainWindow
    {
        // ============================================================
        // Invert document colors       (upstream KillerPDF v1.6.5, #135, thanks dmantisk)
        // Night mode that keeps pictures their real colors  (#135 follow-up, upstream v1.7.0)
        // ============================================================
        //
        // A moon toggle at the bottom of the sidebar rail (and Ctrl+I, and View ▸ Invert Colors)
        // renders the document with inverted colors for dark-mode reading. The icon lights in the
        // accent while active and the choice is remembered across launches.
        //
        // WHERE THE FLIP HAPPENS — and why nothing else can see it.
        // Services/PdfDocumentService.RenderPageBitmap is the single bitmap producer, and it is
        // shared by the main viewer AND by RenderThumbnailsAsync (the sidebar). Inverting there
        // would invert the thumbnails too, and every other consumer of the PDF (save, Save
        // Flattened, print, Export Pages as Images, OCR, Transform) re-rasterizes from the file
        // through its own path. So the flip is applied strictly DOWNSTREAM, in the display layer:
        // at the exact points where a bitmap is handed to an Image inside PageContentGrid (the
        // primary PageImage, the Grid / Two-Page secondary tiles, and the three Continuous-view
        // slot writers). Everything that reads the document rather than the viewer is therefore
        // true-color by construction — there is no per-feature opt-out to remember.
        //
        // Two entry points, one rule:
        //   * DisplayBitmap(BitmapSource, FracRect[]) — for the cached RenderedPage bitmaps, which
        //     must stay true-color in _renderCache (they are shared with the tab cache); it returns
        //     a separate inverted copy, memoized per source in a weak table so toggling back and
        //     forth is free and the copies die with their source.
        //   * InvertBgraInPlaceExcept(byte[], w, h, FracRect[]) — for the paths that own a freshly
        //     rasterized BGRA buffer and are about to WritePixels it into a throwaway
        //     WriteableBitmap. No extra copy.
        //
        // Annotations, links, form fields, search highlights and the selection chrome are WPF
        // vector overlays drawn on top, so they keep their real colors — same as upstream.
        //
        // PICTURES KEEP THEIR REAL COLORS (default). A photo or a chart read as a negative is
        // worse than useless, so night mode carves the page's image boxes back out of the
        // inversion. "Invert images too" (moon right-click, Shift+N, View ▸ Invert Images Too)
        // restores the old whole-page behavior — which is exactly what a SCANNED document needs,
        // because there the entire page is one image and the carve-out would make night mode a
        // no-op. Default off, matching upstream.
        //
        // APP-WIDE, NOT PER-TAB. This is a reading-comfort preference of the person, not a
        // property of any one document, and it matches how TDPdf already treats its sibling view
        // state (_viewMode is app-wide and persisted; ActivateTab even re-asserts it per tab).
        // Keeping it global means switching tabs never silently changes how a document looks, and
        // because the flip is applied at display time the per-tab render caches hold true-color
        // bitmaps and need no flushing when it is toggled.
        // ============================================================

        private bool _docInvert;

        // True = night mode inverts pictures along with everything else (the pre-carve-out
        // behavior, opt-in from the moon's right-click menu / Shift+N; default off).
        private bool _docInvertImages;

        // Source bitmap -> its inverted display copy. Weak on the key, so an inverted copy is
        // collected as soon as the render cache drops the page it belongs to. The entries bake in
        // the images-too choice, so ToggleDocInvertImages flushes the table — without that, a
        // toggle would keep showing the copy made under the old setting.
        private readonly ConditionalWeakTable<BitmapSource, BitmapSource> _invertedBitmaps = new();

        // The "Invert images too" row on the moon's right-click menu (BuildDocInvertRailMenu). Its
        // twin in the View menu is _invertImagesMenuItem, resolved from XAML like the other manual
        // element refs; both are kept in step by SyncDocInvertUi.
        private MenuItem _invertImagesRailItem = null!;

        // ── Image regions excluded from the inversion (#135 follow-up) ────────────────────────
        // Keyed "page|file" so a tab switch, a save (which re-points _currentFile at a fresh temp
        // copy) or a reload can never serve another document's rects. Cleared alongside the render
        // caches by InvalidateRenderCache. Concurrent because the continuous / secondary-tile
        // workers fill it off the UI thread.
        private readonly ConcurrentDictionary<string, FracRect[]> _pageImageRects = new();

        /// <summary>
        /// Scoped owner for the ONE PdfPig document a render loop opens on demand — `using var` at
        /// the top of the loop, and the handle is gone the moment the loop ends. TDPdf works against
        /// a temp copy of the file and the save path swaps that file out, so a PdfPig handle left
        /// open would block the swap; making the lifetime a `using` is what guarantees it isn't
        /// forgotten. (Upstream threads a `ref PdfPigDoc?` through instead, which a ref local can't
        /// be in a lambda — and three of TDPdf's five render sites are lambdas.)
        /// </summary>
        private sealed class PigScope : IDisposable
        {
            internal PdfPigDoc? Doc;
            public void Dispose() { Doc?.Dispose(); Doc = null; }
        }

        /// <summary>
        /// The page's image boxes for the night-mode carve-out, cached per (file, page). On a miss
        /// it opens PdfPig into <paramref name="pig"/>, so one worker loop pays ONE open however
        /// many pages it fills. Encrypted or unparsable pages cache an empty set, which falls back
        /// to inverting everything (the pre-carve-out behavior) rather than failing the render.
        /// </summary>
        private FracRect[] ImageRectsFor(string file, int page, PigScope pig)
        {
            // Opt-in full inversion: no carve-out, and no PdfPig open paid for rects nobody uses.
            if (_docInvertImages) return [];
            string key = page + "|" + file;
            if (_pageImageRects.TryGetValue(key, out var hit)) return hit;
            FracRect[] rects;
            try
            {
                pig.Doc ??= PdfPigDoc.Open(file);
                rects = PdfImages.GetFracRects(pig.Doc, page);
            }
            catch { rects = []; }
            _pageImageRects[key] = rects;
            return rects;
        }

        /// <summary>One-page convenience overload that owns the PdfPig lifetime itself.</summary>
        private FracRect[] ImageRectsFor(string file, int page)
        {
            using var pig = new PigScope();
            return ImageRectsFor(file, page, pig);
        }

        /// <summary>
        /// Rects for the primary tile. The parse runs on the thread pool because RenderPage is on
        /// the UI thread there; an already-cached page (every render after the first, including the
        /// re-sharpen passes) completes synchronously, so the await adds no latency to the common
        /// path and none at all when night mode is off.
        /// </summary>
        private Task<FracRect[]> ImageRectsForAsync(string file, int page, CancellationToken ct)
        {
            if (!_docInvert || _docInvertImages) return Task.FromResult(Array.Empty<FracRect>());
            if (_pageImageRects.TryGetValue(page + "|" + file, out var hit)) return Task.FromResult(hit);
            return Task.Run(() => ImageRectsFor(file, page), ct);
        }

        /// <summary>Restores the persisted preferences and syncs the rail toggle. Called from the ctor.</summary>
        private void InitDocInvert()
        {
            try
            {
                _docInvert = TDPdf.Properties.Settings.Default.InvertDocumentColors;
                _docInvertImages = TDPdf.Properties.Settings.Default.DocInvertImages;
            }
            catch { _docInvert = false; _docInvertImages = false; }   // corrupt user.config: true color
            BuildDocInvertRailMenu();
            SyncDocInvertUi();
        }

        /// <summary>
        /// Flips the display-only dark mode, persists it, and rebuilds the visible pages so the new
        /// colors take effect. Deliberately does NOT touch _isDirty — a view preference is not a
        /// document mutation.
        /// </summary>
        private void ToggleDocInvert(bool on)
        {
            if (_docInvert == on) return;
            _docInvert = on;
            try
            {
                TDPdf.Properties.Settings.Default.InvertDocumentColors = on;
                TDPdf.Properties.Settings.Default.Save();
            }
            catch { /* non-critical user preference */ }
            SyncDocInvertUi();
            RefreshInvertedView();
        }

        /// <summary>
        /// Flips whether night mode inverts pictures too. Repaints only when night mode is actually
        /// showing; otherwise the choice simply takes effect the next time the moon goes on.
        /// </summary>
        private void ToggleDocInvertImages(bool on)
        {
            if (_docInvertImages == on) return;
            _docInvertImages = on;
            try
            {
                TDPdf.Properties.Settings.Default.DocInvertImages = on;
                TDPdf.Properties.Settings.Default.Save();
            }
            catch { /* non-critical user preference */ }
            // The memoized copies were made under the old setting — drop them or the repaint below
            // hands back the same pixels. The rect cache itself stays: it is a property of the
            // document, not of the choice, and ImageRectsFor short-circuits when images invert too.
            _invertedBitmaps.Clear();
            SyncDocInvertUi();
            if (_docInvert) RefreshInvertedView();
        }

        private void DocInvert_Click(object sender, RoutedEventArgs e) => ToggleDocInvert(!_docInvert);

        private void DocInvertImages_Click(object sender, RoutedEventArgs e)
            => ToggleDocInvertImages(!_docInvertImages);

        /// <summary>
        /// The rail moon's right-click menu: night-mode options, currently the single "Invert images
        /// too" row. Attached to the button's ContextMenu property rather than opened by hand in a
        /// MouseRightButtonUp handler, which (a) puts it in the button's logical tree so the window's
        /// ContextMenu / MenuItem styles and DynamicResource theme brushes resolve, and (b) gets the
        /// Menu key / Shift+F10 opening for free when the button has focus. IsCheckable is left off
        /// so WPF cannot auto-toggle the row out from under the handler; the themed MenuItem template
        /// draws the check from IsChecked either way, and SyncDocInvertUi keeps it in step.
        /// </summary>
        private void BuildDocInvertRailMenu()
        {
            _invertImagesRailItem = new MenuItem
            {
                Header = "Invert _Images Too",
                InputGestureText = "Shift+N",
            };
            _invertImagesRailItem.Click += DocInvertImages_Click;
            AutomationProperties.SetName(_invertImagesRailItem, "Invert images too");

            var menu = new ContextMenu();
            menu.Items.Add(_invertImagesRailItem);
            _invertColorsBtn.ContextMenu = menu;
        }

        /// <summary>Spelled out once; shown on the rail tooltip, both menu rows and to a screen reader.</summary>
        private string InvertImagesHelpText => _docInvertImages
            ? "Night mode inverts pictures too. Turn this off (Shift+N) to let photos and charts keep their real colors."
            : "Pictures keep their real colors. Turn this on (Shift+N) for a scanned document, where the whole page is one image.";

        // Tag drives the rail button's Style trigger (DynamicResource accent brush, so all three
        // themes repaint live); the tooltips spell the current state out.
        private void SyncDocInvertUi()
        {
            _invertColorsBtn.Tag = _docInvert ? "on" : null;
            string hint = _docInvert
                ? "Restore the document's true colors (Ctrl+I)"
                : "Invert document colors for dark reading — display only (Ctrl+I)";
            // The rail button is the only place the carve-out is discoverable by mouse alone, so
            // its tooltip advertises the right-click menu and says which way the option is set.
            string railHint = hint + "\nRight-click for night-mode options. " + InvertImagesHelpText;
            _invertColorsBtn.ToolTip = railHint;
            AutomationProperties.SetHelpText(_invertColorsBtn, railHint);
            // The menu rows keep a fixed header and show state as a check mark: the 1.19 menu
            // template reserves an icon/check gutter, so the old header-text flip ("Restore True
            // Colors") is no longer needed. IsChecked is set directly rather than via IsCheckable
            // so WPF never auto-toggles it out from under DocInvert_Click.
            _invertColorsMenuItem.IsChecked = _docInvert;
            AutomationProperties.SetHelpText(_invertColorsMenuItem, hint);
            _invertImagesMenuItem.IsChecked = _docInvertImages;
            AutomationProperties.SetHelpText(_invertImagesMenuItem, InvertImagesHelpText);
            _invertImagesRailItem.IsChecked = _docInvertImages;
            _invertImagesRailItem.ToolTip = InvertImagesHelpText;
            AutomationProperties.SetHelpText(_invertImagesRailItem, InvertImagesHelpText);
        }

        /// <summary>
        /// Re-renders whatever is currently on screen so the pages pick up (or drop) the inversion.
        /// Covers all four view modes: Continuous rebuilds its strip, Single / Two-Page / Grid go
        /// through RenderPage, which re-renders the primary page and re-runs the secondary-tile and
        /// overlay pass for the mode.
        /// </summary>
        private void RefreshInvertedView()
        {
            if (_doc is null) return;
            int idx = Math.Max(0, PageList.SelectedIndex);
            if (_viewMode == ViewMode.Continuous)
            {
                SetupContinuousView(idx);
                return;
            }
            RenderPage(idx);
        }

        /// <summary>
        /// The display form of a cached page bitmap: the bitmap itself when true color, otherwise a
        /// memoized inverted copy. The original is left untouched so the render cache — and anything
        /// that reads back from it — keeps the document's real colors.
        /// </summary>
        private BitmapSource? DisplayBitmap(BitmapSource? source, FracRect[] keep)
        {
            if (source is null || !_docInvert) return source;
            return _invertedBitmaps.GetValue(source, s => InvertedCopy(s, keep));
        }

        /// <summary>Frozen inverted copy of a bitmap. Falls back to the original if it can't be read.</summary>
        private static BitmapSource InvertedCopy(BitmapSource source, FracRect[] keep)
        {
            try
            {
                var bgra = source.Format == PixelFormats.Bgra32
                    ? source
                    : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

                int w = bgra.PixelWidth, h = bgra.PixelHeight;
                long len = (long)w * 4 * h;
                if (w <= 0 || h <= 0 || len > int.MaxValue) return source;

                int stride = w * 4;
                var pixels = new byte[len];
                bgra.CopyPixels(pixels, stride, 0);
                InvertBgraInPlaceExcept(pixels, w, h, keep);

                double dpiX = source.DpiX > 0 ? source.DpiX : 96.0;
                double dpiY = source.DpiY > 0 ? source.DpiY : 96.0;
                var inverted = new WriteableBitmap(w, h, dpiX, dpiY, PixelFormats.Bgra32, null);
                inverted.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
                inverted.Freeze();
                return inverted;
            }
            catch
            {
                return source;   // never let a display preference break rendering
            }
        }

        /// <summary>
        /// In-place inversion of a straight-alpha BGRA buffer for the display dark mode, applied at
        /// the render sites BEFORE any pixel-buffer rotation (the two commute for a whole page, and
        /// it keeps the carve-out rects in the unrotated page space PdfPig measured them in).
        ///
        /// Composite over white and invert in ONE step: out = a*(255-c)/255, alpha forced opaque.
        /// A plain RGB flip that leaves alpha alone is wrong for a PDF, because a page usually
        /// paints NO background — the "paper" is transparent pixels compositing over the white page
        /// slot behind them — so flipping only the color channels left the paper white and merely
        /// faded the ink. This operator turns white (or unpainted) paper black, dark ink light, and
        /// gives an opaque image a true negative.
        ///
        /// It also makes the two rasterizers agree. PdfiumInterop.RenderPageWithAnnotations fills
        /// the buffer with opaque white before rendering, so its pixels are already a=255 and the
        /// operator reduces to 255-c; Docnet's parameterless GetImage() — the `?? ` fallback at
        /// every render site — leaves the background at 0,0,0,0. Under the old flip the same page
        /// looked different depending on which of the two served it.
        /// </summary>
        private static void InvertBgraInPlace(byte[] bgra)
        {
            for (int i = 0; i + 3 < bgra.Length; i += 4)
            {
                int a = bgra[i + 3];
                bgra[i]     = (byte)(a * (255 - bgra[i])     / 255);   // B
                bgra[i + 1] = (byte)(a * (255 - bgra[i + 1]) / 255);   // G
                bgra[i + 2] = (byte)(a * (255 - bgra[i + 2]) / 255);   // R
                bgra[i + 3] = 255;
            }
        }

        /// <summary>
        /// #135 follow-up: night mode that does NOT invert pictures. Inverts the whole page with the
        /// operator above, then applies the SAME operator once more over the image regions. That
        /// second pass is exact, not approximate: the first pass leaves every pixel opaque, so for
        /// an already-inverted pixel the second is out = 255 - (a*(255-c)/255) = (a*c + (255-a)*255)/255
        /// — the ORIGINAL pixel composited over white, which is precisely what the image looked like
        /// on the normal white page. Overlapping image boxes are merged per scanline so no pixel
        /// gets the operator twice (which would invert it right back).
        ///
        /// An empty <paramref name="keep"/> is the plain full-page inversion, so this is also the
        /// single entry point when "Invert images too" is on or the page's images can't be read.
        /// </summary>
        private static void InvertBgraInPlaceExcept(byte[] bgra, int width, int height, FracRect[] keep)
        {
            InvertBgraInPlace(bgra);
            if (keep is null || keep.Length == 0 || width <= 0 || height <= 0) return;

            // Fractions -> pixel boxes, clamped. Floor / ceiling so a box never leaves a 1px
            // inverted sliver of the image at its edge.
            var px = new List<(int x0, int y0, int x1, int y1)>(keep.Length);
            foreach (var r in keep)
            {
                int x0 = Math.Max(0, (int)Math.Floor(r.L * width));
                int x1 = Math.Min(width, (int)Math.Ceiling(r.R * width));
                int y0 = Math.Max(0, (int)Math.Floor(r.T * height));
                int y1 = Math.Min(height, (int)Math.Ceiling(r.B * height));
                if (x1 > x0 && y1 > y0) px.Add((x0, y0, x1, y1));
            }
            if (px.Count == 0) return;

            var spans = new List<(int x0, int x1)>(px.Count);
            for (int y = 0; y < height; y++)
            {
                spans.Clear();
                foreach (var b in px)
                    if (y >= b.y0 && y < b.y1) spans.Add((b.x0, b.x1));
                if (spans.Count == 0) continue;
                spans.Sort((a, b) => a.x0.CompareTo(b.x0));

                int row = y * width * 4;
                int curStart = spans[0].x0, curEnd = spans[0].x1;
                for (int s = 1; s <= spans.Count; s++)
                {
                    if (s < spans.Count && spans[s].x0 <= curEnd)
                    {
                        if (spans[s].x1 > curEnd) curEnd = spans[s].x1;
                        continue;
                    }
                    for (int x = curStart; x < curEnd; x++)
                    {
                        int i = row + x * 4;
                        if (i + 3 >= bgra.Length) break;
                        int a = bgra[i + 3];
                        bgra[i]     = (byte)(a * (255 - bgra[i])     / 255);
                        bgra[i + 1] = (byte)(a * (255 - bgra[i + 1]) / 255);
                        bgra[i + 2] = (byte)(a * (255 - bgra[i + 2]) / 255);
                        bgra[i + 3] = 255;
                    }
                    if (s < spans.Count) { curStart = spans[s].x0; curEnd = spans[s].x1; }
                }
            }
        }
    }
}
