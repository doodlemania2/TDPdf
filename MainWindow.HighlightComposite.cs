// MainWindow — highlights as a multiply blend on screen.
//
// A highlight is saved and printed with the PDF Multiply blend, so the colour darkens the paper and
// the text under it stays dark at any opacity. WPF has no multiply blend for an element laid over
// the page, and a plain alpha fill on the annotation canvas washes dark text out under a strong
// highlight. So the multiply is done on the page bitmap itself (Services/HighlightMultiply): the
// page image on screen is the page raster with every highlight's colour multiplied into its
// rectangle, and the annotation canvas draws nothing for those highlights. Selection, moving and
// resizing never relied on the drawn rectangle (it is not hit-testable; hits are geometric), so
// they are unaffected.
//
// Only the primary page has an annotation canvas, in every view mode that shows annotations
// (Single, the selected page of Two-Page and Grid). Continuous view and the secondary tiles show
// the saved file's pages without unsaved annotations at all, so there is nothing to blend there.
//
// The composite is derived data, never the source of truth:
//   * It is built from the cached true-colour raster (_primaryPageBitmap stays unmodified, so the
//     image-edit tool and OCR keep reading true colours) and then goes through the same
//     display-only invert as any page. Night mode therefore shows the negative of the highlighted
//     page: yellow paper turns dark blue, and the text on it stays light and readable.
//   * RenderPage builds it with the page, so a page opens, zooms and re-inverts already highlighted.
//   * An edit to the page's highlights (place, move, resize, recolour, delete, undo, redo) puts the
//     plain bitmap back at once, draws the old plain-alpha highlight meanwhile, and builds the new
//     composite off the UI thread once the edits pause. Nothing is recomposited per mouse move,
//     and only the primary page is ever touched.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        private readonly record struct HighlightSpec(Rect Rect, byte R, byte G, byte B, byte A);

        /// <summary>What a composite was built from: how many rectangles, and a hash of them.</summary>
        private readonly record struct HighlightSig(int Count, int Hash);

        /// <summary>Marks the plain-alpha stand-ins on the canvas so they can be hidden once the composite lands.</summary>
        private static readonly object HighlightFallbackTag = new();

        // The state of PageImage.Source: it embeds the highlights in _hlSig, built from _hlBase.
        // (0, 0) means "plain page, nothing embedded".
        private int _hlPage = -1;
        private BitmapSource? _hlBase;
        private HighlightSig _hlSig;

        /// <summary>The primary page's picture-carve-out rectangles, kept to rebuild its display bitmap.</summary>
        private FracRect[] _primaryKeep = [];

        private DispatcherTimer? _hlTimer;
        private int _hlPendingPage = -1;
        private CancellationTokenSource? _hlCts;

        /// <summary>True for the annotations drawn with the multiply blend: highlights, not underline or strikethrough.</summary>
        private static bool IsMultiplied(PageAnnotation a)
            => a is MarkupAnnotation mk ? mk.Style == MarkupStyle.Highlight : a is HighlightAnnotation;

        /// <summary>The multiplied rectangles of a page, in canvas DIPs.</summary>
        private List<HighlightSpec> CollectHighlightSpecs(int pageIndex)
        {
            var specs = new List<HighlightSpec>();
            if (!_annotations.TryGetValue(pageIndex, out var list)) return specs;
            foreach (var annot in list)
            {
                if (!IsMultiplied(annot) || annot is not HighlightAnnotation ha) continue;
                var c = ha.GetColor();
                IEnumerable<Rect> rects = ha is MarkupAnnotation mk ? mk.PaintRects() : new[] { ha.Bounds };
                foreach (var r in rects)
                {
                    if (!IsFinitePositive(r.Width) || !IsFinitePositive(r.Height) || !IsFinite(r.X) || !IsFinite(r.Y)) continue;
                    specs.Add(new HighlightSpec(r, c.R, c.G, c.B, c.A));
                }
            }
            return specs;
        }

        private static HighlightSig SignatureOf(List<HighlightSpec> specs)
        {
            var h = new HashCode();
            foreach (var s in specs) h.Add(s);
            return new HighlightSig(specs.Count, specs.Count == 0 ? 0 : h.ToHashCode());
        }

        /// <summary>
        /// The page raster with the highlights multiplied in, as a frozen bitmap in the same format and
        /// density. Pure pixel work on a frozen source, so it is safe off the UI thread. Null when the
        /// source cannot be read; the caller then keeps the plain bitmap.
        /// </summary>
        private static BitmapSource? ComposeHighlights(BitmapSource source, List<HighlightSpec> specs, double dipW, double dipH)
        {
            try
            {
                var bgra = source.Format == PixelFormats.Bgra32
                    ? source
                    : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                int w = bgra.PixelWidth, h = bgra.PixelHeight;
                long len = (long)w * 4 * h;
                if (w <= 0 || h <= 0 || len > int.MaxValue) return null;

                var pixels = new byte[len];
                bgra.CopyPixels(pixels, w * 4, 0);
                foreach (var s in specs)
                {
                    if (CanvasPixels.Region(s.Rect.X, s.Rect.Y, s.Rect.Width, s.Rect.Height, w, h, dipW, dipH) is not { } region)
                        continue;
                    HighlightMultiply.Apply(pixels, w, h, region, s.R, s.G, s.B, s.A / 255.0);
                }
                double dpiX = source.DpiX > 0 ? source.DpiX : 96.0;
                double dpiY = source.DpiY > 0 ? source.DpiY : 96.0;
                return PageRaster.ToFrozenBitmap(pixels, w, h, dpiX, dpiY);
            }
            catch
            {
                return null;   // a display nicety must never break rendering
            }
        }

        /// <summary>
        /// For RenderPage: the bitmap to show for the primary page (before the night-mode invert), and
        /// the signature of the highlights embedded in it. Composites off the UI thread; the plain
        /// bitmap and an empty signature when the page has no highlights.
        /// </summary>
        private async Task<(BitmapSource Bitmap, HighlightSig Sig)> HighlightedPageAsync(
            int pageIndex, BitmapSource baseBitmap, double dipW, double dipH, CancellationToken ct)
        {
            var specs = CollectHighlightSpecs(pageIndex);
            if (specs.Count == 0) return (baseBitmap, default);
            var sig = SignatureOf(specs);
            var composed = baseBitmap.IsFrozen
                ? await Task.Run(() => ComposeHighlights(baseBitmap, specs, dipW, dipH), ct)
                : ComposeHighlights(baseBitmap, specs, dipW, dipH);
            return composed is null ? (baseBitmap, default) : (composed, sig);
        }

        /// <summary>RenderPage's one write to PageImage.Source, recording what it embeds.</summary>
        private void ShowPrimaryBitmap(int pageIndex, BitmapSource baseBitmap, BitmapSource shown, HighlightSig sig, FracRect[] keep)
        {
            _primaryKeep = keep;
            _hlPage = pageIndex;
            _hlBase = baseBitmap;
            _hlSig = sig;
            PageImage.Source = DisplayBitmap(shown, keep);
        }

        /// <summary>
        /// Called at the top of RenderAllAnnotations. Brings the page image in step with the page's
        /// highlights and returns true when they are multiplied into it (so the canvas must not draw
        /// them again). When they are not, the page image is the plain bitmap, the caller draws the
        /// plain-alpha stand-ins, and a rebuild is queued.
        /// </summary>
        private bool SyncHighlightComposite(int pageIndex)
        {
            if (PageImage.Tag is not int shown || shown != pageIndex || _primaryPageBitmap is null || PageImage.Source is null)
                return false;

            var sig = SignatureOf(CollectHighlightSpecs(pageIndex));
            bool known = _hlPage == pageIndex && ReferenceEquals(_hlBase, _primaryPageBitmap);
            if (known && sig == _hlSig) return sig.Count > 0;

            // The page's highlights changed (or the image was set without us): whatever is embedded
            // is stale, so put the plain page back now. Cheap — the inverted copy is memoized.
            if (!known || _hlSig.Count > 0)
                PageImage.Source = DisplayBitmap(_primaryPageBitmap, _primaryKeep);
            _hlPage = pageIndex;
            _hlBase = _primaryPageBitmap;
            _hlSig = default;
            if (sig.Count > 0) QueueHighlightRecomposite(pageIndex);
            return false;
        }

        private void QueueHighlightRecomposite(int pageIndex)
        {
            _hlPendingPage = pageIndex;
            if (_hlTimer is null)
            {
                _hlTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(90) };
                _hlTimer.Tick += (_, _) =>
                {
                    _hlTimer.Stop();
                    _ = RecompositeHighlightsAsync(_hlPendingPage);
                };
            }
            _hlTimer.Stop();   // restart: a burst of edits rebuilds once, after it
            _hlTimer.Start();
        }

        private async Task RecompositeHighlightsAsync(int pageIndex)
        {
            try
            {
                if (_doc is null || PageImage.Tag is not int shown || shown != pageIndex) return;
                var baseBitmap = _primaryPageBitmap;
                if (baseBitmap is null) return;
                var ctx = _ctx;
                double dipW = PageImage.Width, dipH = PageImage.Height;
                var specs = CollectHighlightSpecs(pageIndex);
                if (specs.Count == 0) return;   // SyncHighlightComposite already restored the plain page
                var sig = SignatureOf(specs);

                _hlCts?.Cancel();
                _hlCts = new CancellationTokenSource();
                var ct = _hlCts.Token;
                var composed = baseBitmap.IsFrozen
                    ? await Task.Run(() => ComposeHighlights(baseBitmap, specs, dipW, dipH), ct)
                    : ComposeHighlights(baseBitmap, specs, dipW, dipH);
                if (composed is null || ct.IsCancellationRequested) return;

                // The world may have moved on while the pixels were being made: another page, tab,
                // zoom, or more edits. Then this result is stale; a newer rebuild is already queued.
                if (!ReferenceEquals(ctx, _ctx) || _doc is null
                    || PageImage.Tag is not int nowShown || nowShown != pageIndex
                    || !ReferenceEquals(baseBitmap, _primaryPageBitmap)
                    || SignatureOf(CollectHighlightSpecs(pageIndex)) != sig) return;

                ShowPrimaryBitmap(pageIndex, baseBitmap, composed, sig, _primaryKeep);
                // The blended page now carries the colour; hide the plain stand-ins. Hidden rather
                // than removed so the canvas's children are not mutated from here.
                foreach (var child in _annotationCanvas.Children)
                    if (child is Rectangle r && ReferenceEquals(r.Tag, HighlightFallbackTag))
                        r.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) { }
            catch { /* the plain-alpha highlight stays; never fail an edit over a display nicety */ }
        }
    }
}
