using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TDPdf
{
    public partial class MainWindow
    {
        // ============================================================
        // Invert document colors       (upstream KillerPDF v1.6.5, #135, thanks dmantisk)
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
        //   * DisplayBitmap(BitmapSource) — for the cached RenderedPage bitmaps, which must stay
        //     true-color in _renderCache (they are shared with the tab cache); it returns a
        //     separate inverted copy, memoized per source in a weak table so toggling back and
        //     forth is free and the copies die with their source.
        //   * InvertBgraInPlace(byte[]) — for the paths that own a freshly rasterized BGRA buffer
        //     and are about to WritePixels it into a throwaway WriteableBitmap. No extra copy.
        //
        // Annotations, links, form fields, search highlights and the selection chrome are WPF
        // vector overlays drawn on top, so they keep their real colors — same as upstream.
        //
        // APP-WIDE, NOT PER-TAB. This is a reading-comfort preference of the person, not a
        // property of any one document, and it matches how TDPdf already treats its sibling view
        // state (_viewMode is app-wide and persisted; ActivateTab even re-asserts it per tab).
        // Keeping it global means switching tabs never silently changes how a document looks, and
        // because the flip is applied at display time the per-tab render caches hold true-color
        // bitmaps and need no flushing when it is toggled.
        // ============================================================

        private bool _docInvert;

        // Source bitmap -> its inverted display copy. Weak on the key, so an inverted copy is
        // collected as soon as the render cache drops the page it belongs to.
        private readonly ConditionalWeakTable<BitmapSource, BitmapSource> _invertedBitmaps = new();

        /// <summary>Restores the persisted preference and syncs the rail toggle. Called from the ctor.</summary>
        private void InitDocInvert()
        {
            try { _docInvert = TDPdf.Properties.Settings.Default.InvertDocumentColors; }
            catch { _docInvert = false; }   // corrupt user.config: fall back to true color
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

        private void DocInvert_Click(object sender, RoutedEventArgs e) => ToggleDocInvert(!_docInvert);

        // Tag drives the rail button's Style trigger (DynamicResource accent brush, so all three
        // themes repaint live); the tooltips spell the current state out.
        private void SyncDocInvertUi()
        {
            _invertColorsBtn.Tag = _docInvert ? "on" : null;
            string hint = _docInvert
                ? "Restore the document's true colors (Ctrl+I)"
                : "Invert document colors for dark reading — display only (Ctrl+I)";
            _invertColorsBtn.ToolTip = hint;
            AutomationProperties.SetHelpText(_invertColorsBtn, hint);
            // The menu row keeps a fixed header and shows state as a check mark: the 1.19 menu
            // template reserves an icon/check gutter, so the old header-text flip ("Restore True
            // Colors") is no longer needed. IsChecked is set directly rather than via IsCheckable
            // so WPF never auto-toggles it out from under DocInvert_Click.
            _invertColorsMenuItem.IsChecked = _docInvert;
            AutomationProperties.SetHelpText(_invertColorsMenuItem, hint);
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
        private BitmapSource? DisplayBitmap(BitmapSource? source)
        {
            if (source is null || !_docInvert) return source;
            return _invertedBitmaps.GetValue(source, static s => InvertedCopy(s));
        }

        /// <summary>Frozen inverted copy of a bitmap. Falls back to the original if it can't be read.</summary>
        private static BitmapSource InvertedCopy(BitmapSource source)
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
                InvertBgraInPlace(pixels);

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
        /// Inverts the color channels of a straight-alpha BGRA buffer in place, leaving alpha alone.
        /// Docnet hands us exactly this layout, so the raster paths can flip their own buffer just
        /// before WritePixels with no extra allocation.
        /// </summary>
        private static void InvertBgraInPlace(byte[] bgra)
        {
            for (int i = 0; i + 3 < bgra.Length; i += 4)
            {
                bgra[i]     = (byte)(255 - bgra[i]);       // B
                bgra[i + 1] = (byte)(255 - bgra[i + 1]);   // G
                bgra[i + 2] = (byte)(255 - bgra[i + 2]);   // R
            }
        }
    }
}
