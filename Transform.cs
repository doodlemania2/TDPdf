using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Docnet.Core;
using Docnet.Core.Models;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using TDPdf.Services;

namespace TDPdf
{
    // ============================================================
    // Transform tool (fine-angle rotate + scale + flip + straighten).
    //
    // The Tools menu / page context-menu entry opens a themed TransformWindow that renders the page on its
    // own preview canvas (so the main view mode is irrelevant). Apply rasterizes the page at full resolution
    // through PDFium, composes the chosen flip/scale/rotate onto it, and swaps a one-page image PDF in for the
    // original page — with a whole-document undo snapshot, exactly like Crop / Rotate / Delete.
    //
    // Rasterization caveat: a transformed page becomes an IMAGE. Its text is no longer selectable and any
    // annotations on it are baked into the raster (they "follow" the transform). This matches upstream and our
    // own Crop behaviour; the user is warned once per session before the first transform.
    // ============================================================
    public partial class MainWindow
    {
        private bool _transformWarnShown;   // session-once rasterization warning

        private void ToolTransform_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { SetStatus("Open a PDF first."); return; }
            OpenTransformWindow();
        }

        private void OpenTransformWindow()
        {
            if (_doc is null || _currentFile is null) return;
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) pageIdx = 0;
            if (pageIdx >= _doc.PageCount) return;

            // First-use warning that a transform rasterizes the page (kept once per session; simple and clear).
            if (!_transformWarnShown)
            {
                var res = TdpDialog.Show(this,
                    "Transforming a page rasterizes it: the page becomes an image, so its text is no longer " +
                    "selectable and any annotations on it are baked into the picture (they follow the transform). " +
                    "This is the same as how Crop works.\n\nContinue?",
                    "TDPdf - Transform", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (res != MessageBoxResult.OK) return;
                _transformWarnShown = true;
            }

            // Seed the preview from a copy with annotations baked in, so the preview matches what Apply
            // produces (otherwise annotations are invisible in the Transform window). Kept at a modest
            // resolution — the preview only shows at a few hundred px — so the live compose stays fast; Apply
            // re-renders at full resolution independently.
            string? burned = BurnAllAnnotationsToTemp();
            var src = RenderPageBitmap(pageIdx, 1100, burned);
            if (src is null) { SetStatus("Transform: could not render the page."); return; }

            var page = _doc.Pages[pageIdx];
            var (visW, visH) = VisiblePageSize(page);   // CropBox- and /Rotate-aware, so the readout matches
            var win = new TransformWindow(this, src, visW, visH);
            win.ShowDialog();
            if (!win.Applied) return;
            if (Math.Abs(win.Angle) < 0.01 && Math.Abs(win.Scale - 1.0) < 0.001 && !win.FlipH && !win.FlipV)
            {
                SetStatus("Transform: nothing to apply.");
                return;
            }

            // Apply the same transform to every selected page (defaults to the previewed page).
            var indices = new List<int>();
            foreach (var item in PageList.SelectedItems)
            {
                int i = PageList.Items.IndexOf(item);
                if (i >= 0 && i < _doc.PageCount) indices.Add(i);
            }
            if (indices.Count == 0) indices.Add(pageIdx);
            indices.Sort();

            ApplyPageTransform(indices, win.Angle, win.Scale, win.FixedPage, win.FlipH, win.FlipV);
        }

        // Rasterizes the given pages with the chosen flip/scale/rotate and swaps each in for the original
        // (undoable as one whole-document step).
        private void ApplyPageTransform(List<int> pageIndices, double angleDeg, double scale, bool fixedPage, bool flipH, bool flipV)
        {
            if (_doc is null || _currentFile is null) return;
            if (pageIndices.Count == 0) return;

            try
            {
                // Snapshot the whole document for undo BEFORE mutating, so one Ctrl+Z reverts the transform
                // (same document-level snapshot as Crop / Rotate / Delete).
                PushDocUndo();

                // Bake annotations into a temp copy so the transformed pages' annotations follow the transform
                // (the pages are rasterized anyway, and the user was warned). Non-destructive: _doc is restored
                // to a clean state and we render the target pages from the burned copy. Every render reads this
                // one fixed file, so the page swaps into _doc below never disturb subsequent renders.
                string? burned = BurnAllAnnotationsToTemp();
                string renderSrc = burned ?? _currentFile;

                int restoreIdx = PageList.SelectedIndex;
                int done = 0;
                foreach (int pageIdx in pageIndices)
                {
                    if (pageIdx < 0 || pageIdx >= _doc.PageCount) continue;
                    var src = RenderPageBitmap(pageIdx, 2200, renderSrc);
                    if (src is null) continue;

                    var composed = ComposeTransform(src, angleDeg, scale, fixedPage, flipH, flipV);
                    byte[] png = EncodePng(composed);

                    // Map rendered pixels back to page points via the page's VISIBLE size (CropBox + /Rotate
                    // aware), so the swapped-in image keeps the page's real physical dimensions.
                    var oldPage = _doc.Pages[pageIdx];
                    var (visW, visH) = VisiblePageSize(oldPage);
                    double sx = visW / src.PixelWidth;
                    double sy = visH / src.PixelHeight;
                    double newWpt = composed.PixelWidth * sx;
                    double newHpt = composed.PixelHeight * sy;

                    // Build a one-page PDF holding the transformed image (the proven image-page pattern).
                    string tmp = TempPdfPath("xfpage");
                    using (var one = new PdfDocument())
                    {
                        var np = one.AddPage();
                        np.Width  = newWpt;   // XUnit implicitly treats a double as points
                        np.Height = newHpt;
                        using (var xi = XImage.FromStream(() => new MemoryStream(png)))
                        using (var gfx = XGraphics.FromPdfPage(np))
                            gfx.DrawImage(xi, 0, 0, np.Width.Point, np.Height.Point);
                        one.Save(tmp);
                    }

                    // Import that page and swap it in for the original (mirrors the duplicate-page index dance:
                    // AddPage clones into _doc at the end, then we reposition the clone and drop the original).
                    using (var srcDoc = PdfReader.Open(tmp, PdfDocumentOpenMode.Import))
                    {
                        var imported = _doc.AddPage(srcDoc.Pages[0]);
                        _doc.Pages.RemoveAt(_doc.PageCount - 1);
                        _doc.Pages.Insert(pageIdx, imported);
                        _doc.Pages.RemoveAt(pageIdx + 1);
                    }
                    done++;
                }

                if (done == 0) { SetStatus("Transform: no pages could be rendered."); return; }

                // Persist + re-render. SaveTempAndReload clears the in-app annotation overlay (as Crop/Rotate
                // do); the transformed pages' annotations already live in the raster, so nothing is lost there.
                SaveTempAndReload();
                if (restoreIdx >= 0 && restoreIdx < PageList.Items.Count)
                    PageList.SelectedIndex = restoreIdx;

                // A transform changes the page aspect ratio; fit-to-page so the full result is visible, and
                // re-fit once the new page bitmap has laid out.
                FitToPage();
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)FitToPage);
                MarkDirty(true);
                SetStatus(done == 1
                    ? $"Transformed page {pageIndices[0] + 1} (rasterized to an image)"
                    : $"Transformed {done} pages (rasterized to images)");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Transform failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // The page's visible size in points, honouring both a CropBox (a cropped page reports its real, smaller
        // size) and /Rotate (a 90/270-rotated page has its width/height swapped when rendered by PDFium).
        private static (double wpt, double hpt) VisiblePageSize(PdfPage page)
        {
            // GetVisiblePageBox (MainWindow.xaml.cs) is the one place that resolves a page's rendered
            // box: CropBox-over-MediaBox, page-tree inheritance, and no create-on-read side effects.
            // It reports the UNROTATED box, so the /Rotate swap below is applied exactly once — the
            // old local helper read page.Width/Height, which PdfSharpCore already swaps for 90/270
            // pages, so a rotated page with no CropBox got swapped twice and reported the wrong size.
            var box = GetVisiblePageBox(page);
            int rot = ((page.Rotate % 360) + 360) % 360;
            return (rot == 90 || rot == 270) ? (box.Height, box.Width) : (box.Width, box.Height);
        }

        // Saves the document with ALL rendered pages' annotations burned in, to a temp PDF, and returns its
        // path (null when nothing is pending — the caller then renders the normal source). Non-destructive:
        // _doc is restored to its pre-burn state by reopening a clean snapshot (mirrors the Save-Flattened /
        // Save-As pattern), so this is safe for the preview as well as Apply. Since the transform reads only
        // the target pages from the result, baking every page is harmless and reuses proven machinery.
        private string? BurnAllAnnotationsToTemp()
        {
            if (_doc is null) return null;
            bool anything = _annotations.Values.Any(list => list.Count > 0) || HasPendingFormValues;
            if (!anything) return null;

            var tempClean  = TempPdfPath("xfclean");
            var tempBurned = TempPdfPath("xfburn");
            _doc.Save(tempClean);
            DrawAnnotationsOnDocument();
            _doc.Save(tempBurned);
            _doc.Close();
            try
            {
                _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
            }
            catch (Exception xrefEx) when (PdfDocumentService.IsXRefException(xrefEx))
            {
                var fixedPath = TempPdfPath("xffixed");
                if (!PdfDocumentService.TryPdfiumRepair(tempClean, fixedPath)) throw;
                tempClean = fixedPath;
                _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
            }
            _currentFile = tempClean;
            return tempBurned;
        }

        // Renders one page of a PDF to a white-backed bitmap (transparent page backgrounds show white, not the
        // dark canvas) via PDFium. PDFium already applies the page's /Rotate, so no extra rotation is needed.
        private BitmapSource? RenderPageBitmap(int pageIdx, int maxPx, string? sourceOverride = null)
        {
            if (_doc is null || _currentFile is null) return null;
            if (pageIdx < 0 || pageIdx >= _doc.PageCount) return null;
            try
            {
                string srcPath = sourceOverride ?? _currentFile;
                using var docReader = DocLib.Instance.GetDocReader(srcPath, new PageDimensions(maxPx, maxPx));
                using var pr = docReader.GetPageReader(pageIdx);
                int w = pr.GetPageWidth();
                int h = pr.GetPageHeight();
                byte[] bgra = pr.GetImage();
                if (bgra is null || bgra.Length == 0 || w <= 0 || h <= 0) return null;

                var raw = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                    dc.DrawImage(raw, new Rect(0, 0, w, h));
                }
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }
        }

        // ---- Self-contained WPF bitmap ops, shared by the window preview and full-resolution Apply ----

        // Flip -> scale -> rotate. The single entry point used by both the preview compose and Apply.
        internal static BitmapSource ComposeTransform(BitmapSource src, double angleDeg, double scale, bool fixedPage, bool flipH, bool flipV)
        {
            var flipped = ApplyFlip(src, flipH, flipV);
            var scaled  = Math.Abs(scale - 1.0) < 0.001 ? flipped : ScaleCompose(flipped, scale, fixedPage);
            return Math.Abs(angleDeg) < 0.001 ? scaled : RotateExpand(scaled, angleDeg);
        }

        private static BitmapSource ApplyFlip(BitmapSource src, bool flipH, bool flipV)
        {
            if (!flipH && !flipV) return src;
            var tb = new TransformedBitmap(src, new ScaleTransform(flipH ? -1 : 1, flipV ? -1 : 1));
            tb.Freeze();
            return tb;
        }

        // fixedPage=true: keep the canvas size, shrink/grow the content with white padding (content may clip
        // above 100%). false: resize the page (fewer/more pixels at the same points-per-pixel = a physically
        // smaller/larger page).
        private static BitmapSource ScaleCompose(BitmapSource src, double scale, bool fixedPage)
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            int sw = Math.Max(1, (int)Math.Round(w * scale));
            int sh = Math.Max(1, (int)Math.Round(h * scale));

            var dv = new DrawingVisual();
            if (fixedPage)
            {
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                    dc.DrawImage(src, new Rect((w - sw) / 2.0, (h - sh) / 2.0, sw, sh));
                }
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            else
            {
                using (var dc = dv.RenderOpen())
                    dc.DrawImage(src, new Rect(0, 0, sw, sh));
                var rtb = new RenderTargetBitmap(sw, sh, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
        }

        // Rotates a bitmap by angleDeg about its centre into a canvas grown to the rotated bounding box, with
        // the new corners filled white.
        internal static BitmapSource RotateExpand(BitmapSource src, double angleDeg)
        {
            double w = src.PixelWidth, h = src.PixelHeight;
            double rad = angleDeg * Math.PI / 180.0;
            double cos = Math.Abs(Math.Cos(rad));
            double sin = Math.Abs(Math.Sin(rad));
            int nw = (int)Math.Ceiling(w * cos + h * sin);
            int nh = (int)Math.Ceiling(w * sin + h * cos);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, nw, nh));
                dc.PushTransform(new TranslateTransform(nw / 2.0, nh / 2.0));
                dc.PushTransform(new RotateTransform(angleDeg));
                dc.DrawImage(src, new Rect(-w / 2.0, -h / 2.0, w, h));
                dc.Pop();
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(nw, nh, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        private static byte[] EncodePng(BitmapSource bmp)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        private static string TempPdfPath(string tag) =>
            Path.Combine(Path.GetTempPath(), $"tdpdf_{tag}_{Guid.NewGuid():N}.pdf");
    }
}
