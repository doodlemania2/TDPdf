// MainWindow — pictures that are already part of the page (#244).
//
// A picture the PDF itself carries (a logo, a photo, a scan) is edited IN THE FILE: PDFium's page-object
// API finds the image object, and move, resize, delete and replace change that object (Services/
// PdfImageObjects.cs), after which the file is adopted as the working copy with one undo step, the same
// way an in-place text edit is. Nothing is drawn over the old picture.
//
// Pictures have their OWN selection slot, _pictureSel, beside the marks (_markSelection). The two never
// hold something at the same time: selecting one clears the other. Esc, Delete, the arrow keys, Cut and
// Copy act on whichever slot is filled.
//
// What the user does (Select Picture tool, key 3):
//   * Click a picture: it gets a box with four corner handles.
//   * Drag from a selected picture to move it; drag a corner to resize (the proportions are kept, Shift
//     frees them). Arrow keys nudge (1 DIP, 10 with Shift); a run of presses is one edit.
//   * Delete removes it. Ctrl+C / Ctrl+X lift it into a picture mark on the clipboard only: the page is
//     not changed by a copy, and a paste is a normal picture mark (MainWindow.MarkClipboard.cs).
//   * Right-click: Save Picture As (the original JPEG bytes when it is a plain JPEG, else PNG at its own
//     resolution), Replace Picture (a NEW image object at the old place and width; the old image data is
//     left alone so any other use of it is intact), Extract Image (MainWindow.PictureExtract.cs).
//
// WHEN IT REFUSES. PDFium rewrites the page's content stream when it saves, and drops non-device colour,
// gradients, inline images and soft masks (Services/PdfContentInspector.cs). A page that has any of them,
// or a picture inside a form object, is not edited in place: the status line says why and a dialog offers
// the older cover-and-redraw edit (EditImageAtPosition) instead. Copy and Save Picture As still work.
//
// Everything is per tab: the selection remembers the document it was made on and is dropped if the tab's
// document is no longer that one.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PdfSharpCore.Pdf;
using TDPdf.Services;
using Corner = TDPdf.Services.PicturePlacement.Corner;
using PageBox = TDPdf.Services.PdfPageGeometry.PageBox;
using PdfRect = TDPdf.Services.PdfiumInterop.PdfRect;
using ShapeRectangle = System.Windows.Shapes.Rectangle;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        private readonly record struct PictureBox(double X, double Y, double W, double H)
        {
            internal Rect ToRect() => new(X, Y, W, H);
            internal (double X, double Y, double W, double H) ToTuple() => (X, Y, W, H);
            internal static PictureBox From((double X, double Y, double W, double H) t) => new(t.X, t.Y, t.W, t.H);
        }

        private enum PictureDrag { None, Move, Resize }

        /// <summary>The selected picture, or null. Valid only while <see cref="_pictureSelDoc"/> is still the tab's document.</summary>
        private PagePicture? _pictureSel;
        private PdfDocument? _pictureSelDoc;
        private PictureGate.Decision _pictureGate;
        private PictureBox _pictureBox;
        private PictureBox? _pictureLive;
        private readonly List<UIElement> _pictureChrome = new();
        private Image? _pictureGhost;
        private BitmapSource? _pictureGhostSource;

        private PictureDrag _pictureDrag;
        private Point _pictureDragStart;
        private PictureBox _pictureDragBox;
        private Corner _pictureDragCorner;
        private bool _pictureDragMoved;

        private (double Dx, double Dy) _pictureNudge;
        private System.Windows.Threading.DispatcherTimer? _pictureNudgeTimer;

        private (PdfDocument Doc, DocumentContext Ctx, string Path)? _pictureView;
        private (PdfDocument Doc, int Page, List<PagePicture> Pictures)? _pictureList;

        private const double PictureHandleSize = 9;
        private const double PictureHandleGrab = 8;
        private const double PictureDragSlop = 3;

        private bool HasPictureSelection =>
            _pictureSel is not null && _doc is not null && ReferenceEquals(_pictureSelDoc, _doc)
            && PageList.SelectedIndex == _pictureSel.PageIndex;

        // ============================================================
        // The file the pictures are read from
        // ============================================================

        /// <summary>
        /// A file holding the document exactly as it is on screen, for PDFium to read. The file on disk
        /// can be several structural edits behind the live document, so reading it would select pictures
        /// that are no longer where they are drawn. Written once per document instance, not per click.
        /// </summary>
        private string? PictureViewFile()
        {
            if (_doc is null) return null;
            if (_pictureView is { } v && ReferenceEquals(v.Doc, _doc) && File.Exists(v.Path)) return v.Path;
            DropPictureView();
            try
            {
                string path = NewTempPath("pic_view");
                _doc.Save(path);
                _pictureView = (_doc, _ctx, path);
                return path;
            }
            catch { return null; }
        }

        private void DropPictureView()
        {
            if (_pictureView is not { } v) return;
            _pictureView = null;
            TDPdf.Services.TempFilePolicy.TryDelete(v.Path);
            try { v.Ctx.TempFiles.Release(v.Path); } catch { /* tab already gone */ }
        }

        private bool PictureFrame(int pageIdx, out PageBox box, out int rotation, out double renderW, out double renderH)
        {
            box = default; rotation = 0; renderW = renderH = 0;
            if (_doc is null || pageIdx < 0 || pageIdx >= _doc.PageCount) return false;
            var (w, h) = EnsureRenderDims(pageIdx);
            if (w <= 0 || h <= 0) return false;
            var page = _doc.Pages[pageIdx];
            box = PdfPageGeometry.VisibleBox(page);
            rotation = PdfPageGeometry.Rotation(page);
            renderW = w; renderH = h;
            return true;
        }

        private PictureBox? PictureCanvasBox(PagePicture pic) =>
            PictureFrame(pic.PageIndex, out var box, out int rot, out double rw, out double rh)
                ? PictureBox.From(PicturePlacement.ToCanvas(box, rot, rw, rh, pic.Bounds))
                : null;

        // ============================================================
        // Selecting
        // ============================================================

        /// <summary>
        /// Selects the topmost picture under the press. False when there is none (the caller falls back to
        /// the cover-and-redraw finder, which also knows about pictures nested deeper than PDFium lists).
        /// </summary>
        private bool TrySelectPictureAt(Point pos, int pageIdx)
        {
            if (!PdfiumInterop.CanEditPictures || _doc is null) return false;
            if (!PictureFrame(pageIdx, out var box, out int rot, out double rw, out double rh)) return false;
            string? file = PictureViewFile();
            if (file is null) return false;

            // One listing per document instance and page: the file is only re-read after an edit adopts a new one.
            if (_pictureList is not { } cached || !ReferenceEquals(cached.Doc, _doc) || cached.Page != pageIdx)
                _pictureList = cached = (_doc, pageIdx, PdfImageObjects.List(file, pageIdx));
            var pictures = cached.Pictures;
            if (pictures.Count == 0) return false;

            var pt = PdfPageGeometry.CanvasRectToPdf(box, rot, pos.X, pos.Y, 0, 0, rw, rh);
            var hit = PdfImageObjects.TopmostAt(pictures, pt.Left, pt.Bottom);
            if (hit is null) return false;

            SelectPicture(hit);
            return true;
        }

        private void SelectPicture(PagePicture pic)
        {
            FlushPictureNudge();
            ClearSelection();
            ClearTextSelection();
            var canvasBox = PictureCanvasBox(pic);
            if (canvasBox is null) return;

            _pictureSel = pic;
            _pictureSelDoc = _doc;
            _pictureGate = PdfImageObjects.Gate(_doc, pic);
            _pictureBox = canvasBox.Value;
            _pictureLive = null;
            DrawPictureChrome();

            SetStatus(_pictureGate.Allowed
                ? $"Picture selected ({pic.PixelWidth} x {pic.PixelHeight} px). Drag to move, drag a corner to resize (Shift frees the shape), Delete to remove, Esc to deselect"
                : $"Picture selected. It can be copied or saved, but not changed in place because {_pictureGate.Reason}. Editing it will offer the cover-and-redraw method instead");
        }

        /// <summary>Drops the picture selection and everything drawn for it. Safe to call at any time.</summary>
        private void ClearPictureSelection()
        {
            _pictureNudgeTimer?.Stop();
            _pictureNudge = (0, 0);
            if (_pictureDrag != PictureDrag.None && _annotationCanvas.IsMouseCaptured) _annotationCanvas.ReleaseMouseCapture();
            _pictureDrag = PictureDrag.None;
            _pictureSel = null;
            _pictureSelDoc = null;
            _pictureLive = null;
            _pictureGhostSource = null;
            RemovePictureChrome();
        }

        private void RemovePictureChrome()
        {
            foreach (var el in _pictureChrome) _annotationCanvas.Children.Remove(el);
            _pictureChrome.Clear();
            _pictureGhost = null;
        }

        /// <summary>RenderAllAnnotations wipes the canvas; this puts the picture's box and handles back.</summary>
        private void RestorePictureChrome(int pageIndex)
        {
            if (_pictureSel is null) return;
            if (!HasPictureSelection) { ClearPictureSelection(); return; }
            if (_pictureSel.PageIndex != pageIndex) return;
            _pictureChrome.Clear();   // the wipe already removed the elements
            _pictureGhost = null;
            if (PictureCanvasBox(_pictureSel) is { } b) _pictureBox = b;
            DrawPictureChrome();
        }

        private PictureBox ChromeBox => _pictureLive ?? _pictureBox;

        private void DrawPictureChrome()
        {
            RemovePictureChrome();
            if (!HasPictureSelection) return;
            var b = ChromeBox;

            if (_pictureLive is not null && _pictureGhostSource is not null)
            {
                _pictureGhost = new Image
                {
                    Source = _pictureGhostSource, Width = b.W, Height = b.H, Stretch = Stretch.Fill,
                    Opacity = 0.7, IsHitTestVisible = false
                };
                Canvas.SetLeft(_pictureGhost, b.X);
                Canvas.SetTop(_pictureGhost, b.Y);
                _annotationCanvas.Children.Add(_pictureGhost);
                _pictureChrome.Add(_pictureGhost);
            }

            var outline = new ShapeRectangle
            {
                Width = b.W + 2, Height = b.H + 2,
                Stroke = BrushResource("AccentGreen"), StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 5, 3 },
                IsHitTestVisible = false
            };
            Canvas.SetLeft(outline, b.X - 1);
            Canvas.SetTop(outline, b.Y - 1);
            _annotationCanvas.Children.Add(outline);
            _pictureChrome.Add(outline);

            foreach (Corner c in Enum.GetValues<Corner>())
            {
                var (cx, cy) = CornerPoint(b, c);
                var h = new ShapeRectangle
                {
                    Width = PictureHandleSize, Height = PictureHandleSize,
                    Fill = BrushResource("AccentGreen"), Stroke = BrushResource("BgDark"), StrokeThickness = 1,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(h, cx - PictureHandleSize / 2);
                Canvas.SetTop(h, cy - PictureHandleSize / 2);
                _annotationCanvas.Children.Add(h);
                _pictureChrome.Add(h);
            }
        }

        private static (double X, double Y) CornerPoint(PictureBox b, Corner c) => c switch
        {
            Corner.TopLeft => (b.X, b.Y),
            Corner.TopRight => (b.X + b.W, b.Y),
            Corner.BottomRight => (b.X + b.W, b.Y + b.H),
            _ => (b.X, b.Y + b.H),
        };

        private Corner? PictureCornerAt(Point pos)
        {
            var b = ChromeBox;
            foreach (Corner c in Enum.GetValues<Corner>())
            {
                var (cx, cy) = CornerPoint(b, c);
                if (Math.Abs(pos.X - cx) <= PictureHandleGrab && Math.Abs(pos.Y - cy) <= PictureHandleGrab) return c;
            }
            return null;
        }

        /// <summary>A snapshot of what the page shows inside the picture's box, for the drag preview.</summary>
        private BitmapSource? CaptureGhost(PictureBox box)
        {
            if (_primaryPageBitmap is not BitmapSource source) return null;
            if (CanvasPixels.Region(box.X, box.Y, box.W, box.H,
                    source.PixelWidth, source.PixelHeight, source.Width, source.Height) is not { } px)
                return null;
            var crop = new CroppedBitmap(source, new Int32Rect(px.X, px.Y, px.W, px.H));
            crop.Freeze();
            return crop;
        }

        // ============================================================
        // Pointer
        // ============================================================

        /// <summary>
        /// Press in the Select Picture tool. True when the press was a picture's (select, move or resize);
        /// false hands it to the cover-and-redraw finder, which also owns the overlay edits already made.
        /// </summary>
        private bool TryPictureMouseDown(Point pos, int pageIdx)
        {
            if (_doc is null || _currentFile is null || !PdfiumInterop.CanEditPictures) return false;

            // An overlay image edit already on the page keeps working exactly as before.
            if (_annotations.TryGetValue(pageIdx, out var marks)
                && marks.Any(a => a is ImageEditAnnotation iea && iea.TargetBounds.Contains(pos)))
            {
                ClearPictureSelection();
                return false;
            }

            if (HasPictureSelection && _pictureSel!.PageIndex == pageIdx)
            {
                if (PictureCornerAt(pos) is { } corner)
                {
                    BeginPictureDrag(PictureDrag.Resize, pos, corner);
                    return true;
                }
                if (ChromeBox.ToRect().Contains(pos))
                {
                    BeginPictureDrag(PictureDrag.Move, pos, Corner.TopLeft);
                    return true;
                }
            }

            if (TrySelectPictureAt(pos, pageIdx)) return true;
            ClearPictureSelection();
            return false;
        }

        private void BeginPictureDrag(PictureDrag kind, Point pos, Corner corner)
        {
            FlushPictureNudge();
            _pictureDrag = kind;
            _pictureDragStart = pos;
            _pictureDragBox = _pictureBox;
            _pictureDragCorner = corner;
            _pictureDragMoved = false;
            _pictureGhostSource ??= CaptureGhost(_pictureBox);
            _annotationCanvas.CaptureMouse();
        }

        private void PictureDragMove(Point pos)
        {
            if (_pictureDrag == PictureDrag.None || !HasPictureSelection) return;
            double dx = pos.X - _pictureDragStart.X, dy = pos.Y - _pictureDragStart.Y;
            if (!_pictureDragMoved && Math.Sqrt(dx * dx + dy * dy) < PictureDragSlop) return;
            _pictureDragMoved = true;

            _pictureLive = _pictureDrag == PictureDrag.Move
                ? _pictureDragBox with { X = _pictureDragBox.X + dx, Y = _pictureDragBox.Y + dy }
                : PictureBox.From(PicturePlacement.ResizeCanvasBox(
                    _pictureDragBox.ToTuple(), _pictureDragCorner, dx, dy,
                    keepAspect: (Keyboard.Modifiers & ModifierKeys.Shift) == 0));
            DrawPictureChrome();
        }

        private void PictureDragEnd(Point pos)
        {
            var kind = _pictureDrag;
            _pictureDrag = PictureDrag.None;
            if (_annotationCanvas.IsMouseCaptured) _annotationCanvas.ReleaseMouseCapture();
            if (kind == PictureDrag.None || !HasPictureSelection) return;

            if (!_pictureDragMoved || _pictureLive is null)
            {
                _pictureLive = null;
                DrawPictureChrome();
                return;
            }

            var pic = _pictureSel!;
            var live = _pictureLive.Value;
            if (!PictureFrame(pic.PageIndex, out var box, out int rot, out double rw, out double rh)) return;

            PdfRect target = kind == PictureDrag.Move
                ? PicturePlacement.MovedBounds(box, rot, rw, rh, pic.Bounds, live.X - _pictureBox.X, live.Y - _pictureBox.Y)
                : PicturePlacement.ToPdf(box, rot, rw, rh, live.ToTuple());
            CommitPictureReposition(pic, target, kind == PictureDrag.Move ? "Picture moved" : "Picture resized");
        }

        // ============================================================
        // Arrow keys
        // ============================================================

        private void NudgePicture(double dx, double dy)
        {
            if (!HasPictureSelection) return;
            _pictureNudge = (_pictureNudge.Dx + dx, _pictureNudge.Dy + dy);
            _pictureGhostSource ??= CaptureGhost(_pictureBox);
            _pictureLive = _pictureBox with { X = _pictureBox.X + _pictureNudge.Dx, Y = _pictureBox.Y + _pictureNudge.Dy };
            DrawPictureChrome();

            // Each commit rewrites and reopens the file, so a run of presses waits for a pause and is one edit.
            _pictureNudgeTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
            _pictureNudgeTimer.Tick -= PictureNudgeTick;
            _pictureNudgeTimer.Tick += PictureNudgeTick;
            _pictureNudgeTimer.Stop();
            _pictureNudgeTimer.Start();
        }

        private void PictureNudgeTick(object? sender, EventArgs e) => FlushPictureNudge();

        /// <summary>Applies any arrow-key nudges that are still waiting for their pause.</summary>
        private void FlushPictureNudge()
        {
            _pictureNudgeTimer?.Stop();
            var (dx, dy) = _pictureNudge;
            _pictureNudge = (0, 0);
            if ((dx == 0 && dy == 0) || !HasPictureSelection) return;
            var pic = _pictureSel!;
            if (!PictureFrame(pic.PageIndex, out var box, out int rot, out double rw, out double rh)) return;
            CommitPictureReposition(pic, PicturePlacement.MovedBounds(box, rot, rw, rh, pic.Bounds, dx, dy), "Picture moved");
        }

        // ============================================================
        // Edits
        // ============================================================

        private void CommitPictureReposition(PagePicture pic, PdfRect target, string done)
        {
            if (ApplyPictureEdit(pic, (src, dest, open) => PdfImageObjects.Reposition(src, dest, pic, target, open)))
            {
                ReselectPicture(pic.At(target));
                SetStatus(done);
            }
            else if (HasPictureSelection)
            {
                _pictureLive = null;
                DrawPictureChrome();
            }
        }

        private void DeletePicture()
        {
            FlushPictureNudge();
            if (!HasPictureSelection) return;
            var pic = _pictureSel!;
            if (ApplyPictureEdit(pic, (src, dest, open) => PdfImageObjects.Delete(src, dest, pic, open)))
            {
                RenderAllAnnotations(pic.PageIndex);
                SetStatus("Picture deleted");
            }
        }

        /// <summary>
        /// Runs one in-place edit the way every document-level edit runs: the live document is saved to a
        /// working file, edited there, and the result adopted as the working copy with ONE undo step.
        /// Overlay marks survive (nothing about the page geometry moves). False when nothing changed, and
        /// then the user has been told why.
        /// </summary>
        private bool ApplyPictureEdit(PagePicture pic, Func<string, string, PdfDocument?, PdfImageObjects.EditResult> edit)
        {
            if (_doc is null || _currentFile is null) return false;
            if (ReadOnlyGate.ProcessIsReadOnly) { SetStatus(ReadOnlyGate.RefusedStatus); return false; }

            // Refused BEFORE anything is saved or normalised, so a refusal leaves the document untouched.
            var gate = PdfImageObjects.Gate(_doc, pic);
            if (!gate.Allowed)
            {
                OfferOverlayEdit(pic, gate.Reason ?? "it cannot be edited in place");
                return false;
            }

            string source = NewTempPath("pic_src");
            string result = NewTempPath("pic_out");
            try
            {
                // Against the CURRENT document, structural edits and all.
                NormalizeLiveDocumentForTool();
                _doc.Save(source);

                var outcome = edit(source, result, _doc);
                if (!outcome.Ok)
                {
                    if (outcome.ShouldOfferOverlay) OfferOverlayEdit(pic, outcome.Error ?? "the change did not take effect");
                    else SetStatus($"The picture was not changed: {outcome.Error}");
                    return false;
                }

                PushDocUndo();
                AdoptEditedFile(result);
                return true;
            }
            catch (Exception ex)
            {
                SetStatus($"The picture was not changed: {ex.Message}");
                return false;
            }
            finally
            {
                TDPdf.Services.TempFilePolicy.TryDelete(source);
                if (File.Exists(result) && !string.Equals(result, _currentFile, StringComparison.OrdinalIgnoreCase))
                    TDPdf.Services.TempFilePolicy.TryDelete(result);
            }
        }

        /// <summary>Selects the picture again on the freshly adopted document.</summary>
        private void ReselectPicture(PagePicture pic)
        {
            if (_doc is null) return;
            _pictureSel = pic;
            _pictureSelDoc = _doc;
            _pictureGate = PdfImageObjects.Gate(_doc, pic);
            _pictureLive = null;
            _pictureGhostSource = null;
            if (PictureCanvasBox(pic) is { } b) _pictureBox = b;
            DrawPictureChrome();
        }

        /// <summary>
        /// Tells the user why a picture cannot be changed in place and offers the cover-and-redraw edit,
        /// which paints over it and draws a copy on top. The original stays in the file underneath.
        /// </summary>
        private void OfferOverlayEdit(PagePicture pic, string reason)
        {
            SetStatus($"The picture was not changed: {reason}");
            var answer = TdpDialog.ShowYesNo(this,
                $"This picture can't be changed in place because {reason}.\n\n" +
                "TDPdf can instead cover it and draw a copy on top. The original picture stays in the file underneath. " +
                "Use that method?",
                "Cover and redraw", "Cancel", "Edit picture", MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            int page = pic.PageIndex;
            var b = PictureCanvasBox(pic);
            ClearPictureSelection();
            RenderAllAnnotations(page);
            if (b is { } box) EditImageAtPosition(new Point(box.X + box.W / 2, box.Y + box.H / 2), page);
        }

        // ============================================================
        // Copy, cut, save
        // ============================================================

        /// <summary>The picture's pixels the way they are SHOWN: flips and quarter turns, the page's own turn included.</summary>
        private (byte[] Bgra, int W, int H) ShownPixels(PagePicture pic, byte[] bgra, int w, int h)
        {
            if (!PictureFrame(pic.PageIndex, out var box, out int rot, out double rw, out double rh)) return (bgra, w, h);
            var m = pic.Matrix;
            (double X, double Y) At(double ux, double uy)
            {
                double x = m.A * ux + m.C * uy + m.E, y = m.B * ux + m.D * uy + m.F;
                var c = PicturePlacement.ToCanvas(box, rot, rw, rh, new PdfRect(x, y, x, y));
                return (c.X, c.Y);
            }
            // The picture's first row is its top edge, which is y = 1 in the unit square.
            var tl = At(0, 1); var tr = At(1, 1); var bl = At(0, 0);
            return PictureOrientation.Apply(bgra, w, h, tr.X - tl.X, tr.Y - tl.Y, bl.X - tl.X, bl.Y - tl.Y) ?? (bgra, w, h);
        }

        private static byte[] EncodePng(byte[] bgra, int w, int h)
        {
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
            bmp.Freeze();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        private PdfImageObjects.LiftResult? LiftSelectedPicture(string verb)
        {
            if (!HasPictureSelection) return null;
            string? file = PictureViewFile();
            if (file is null) { SetStatus($"This picture can't be {verb}: the page could not be read"); return null; }
            var lift = PdfImageObjects.Lift(file, _pictureSel!);
            if (!lift.Ok) { SetStatus($"This picture can't be {verb}: {lift.Error}"); return null; }
            return lift;
        }

        private void CopyPicture(bool cut)
        {
            FlushPictureNudge();
            if (!HasPictureSelection) return;
            var pic = _pictureSel!;

            // A cut that cannot remove the picture is refused up front rather than leaving it on the page
            // AND on the clipboard.
            if (cut && !_pictureGate.Allowed)
            {
                OfferOverlayEdit(pic, _pictureGate.Reason ?? "it cannot be edited in place");
                return;
            }

            var lift = LiftSelectedPicture("copied");
            if (lift is null) return;

            var shown = ShownPixels(pic, lift.Bgra!, lift.Width, lift.Height);
            byte[] png;
            try { png = EncodePng(shown.Bgra, shown.W, shown.H); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or OutOfMemoryException)
            { SetStatus("This picture can't be copied: it could not be encoded"); return; }

            var b = ChromeBox;
            var mark = new ImageAnnotation
            {
                PageIndex = pic.PageIndex,
                Position = new Point(b.X, b.Y),
                Scale = b.W / shown.W,
                SourceWidth = shown.W,
                SourceHeight = shown.H,
                ImageData = Convert.ToBase64String(png)
            };
            PublishMarkClip(new MarkClip { Marks = new List<PageAnnotation> { mark } },
                data => data.SetData("PNG", new MemoryStream(png)));

            if (!cut) { SetStatus("Picture copied. Paste puts it on any page as a picture mark"); return; }
            if (ApplyPictureEdit(pic, (src, dest, open) => PdfImageObjects.Delete(src, dest, pic, open)))
            {
                RenderAllAnnotations(pic.PageIndex);
                SetStatus("Picture cut. Paste puts it on any page as a picture mark");
            }
        }

        private void SavePictureAs()
        {
            FlushPictureNudge();
            var lift = LiftSelectedPicture("saved");
            if (lift is null) return;

            var dlg = new SaveFileDialog
            {
                Title = "Save Picture As",
                FileName = "Picture",
                AddExtension = true,
                Filter = lift.Jpeg is null
                    ? "PNG image (*.png)|*.png"
                    : "JPEG image (*.jpg)|*.jpg|PNG image (*.png)|*.png"
            };
            if (!FileDialogs.ShowSave(dlg, FileDialogPurpose.ExportImages, this)) return;

            try
            {
                bool png = lift.Jpeg is null
                    || string.Equals(System.IO.Path.GetExtension(dlg.FileName), ".png", StringComparison.OrdinalIgnoreCase);
                File.WriteAllBytes(dlg.FileName, png ? EncodePng(lift.Bgra!, lift.Width, lift.Height) : lift.Jpeg!);
                SetStatus(png
                    ? $"Picture saved as PNG ({lift.Width} x {lift.Height} px)"
                    : $"Picture saved as it is stored in the PDF ({lift.Width} x {lift.Height} px JPEG)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                TdpDialog.Show(this, $"The picture could not be saved:\n{ex.Message}", "Save Picture As", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ============================================================
        // Replace and extract
        // ============================================================

        private void ReplacePicture()
        {
            FlushPictureNudge();
            if (!HasPictureSelection) return;
            var pic = _pictureSel!;
            if (!_pictureGate.Allowed) { OfferOverlayEdit(pic, _pictureGate.Reason ?? "it cannot be edited in place"); return; }

            var dlg = new OpenFileDialog { Filter = FileDialogFilters.ImagesBasic, Title = "Select replacement picture" };
            if (!FileDialogs.ShowOpen(dlg, FileDialogPurpose.Picture, this)) return;

            byte[] bgra;
            int w, h;
            try
            {
                var frame = BitmapDecoder.Create(new Uri(dlg.FileName), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                if ((long)frame.PixelWidth * frame.PixelHeight > PdfImageObjects.MaxLiftPixels)
                {
                    SetStatus("That picture is too large to place");
                    return;
                }
                var conv = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                w = conv.PixelWidth; h = conv.PixelHeight;
                bgra = new byte[w * h * 4];
                conv.CopyPixels(bgra, w * 4, 0);
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException
                                           or ArgumentException or FileFormatException or UnauthorizedAccessException)
            {
                SetStatus("That file could not be read as a picture");
                return;
            }

            if (ApplyPictureEdit(pic, (src, dest, open) => PdfImageObjects.Replace(src, dest, pic, bgra, w, h, open)))
            {
                var matrix = PicturePlacement.ReplacementMatrix(pic.Matrix, w, h);
                ReselectPicture(pic with { Matrix = matrix, Bounds = matrix.Bounds(), PixelWidth = w, PixelHeight = h, Filters = Array.Empty<string>() });
                SetStatus("Picture replaced. It keeps the old width and top edge");
            }
        }

        private void ExtractPictureImage()
        {
            FlushPictureNudge();
            var lift = LiftSelectedPicture("extracted");
            if (lift is null) return;
            var pic = _pictureSel!;

            var shown = ShownPixels(pic, lift.Bgra!, lift.Width, lift.Height);
            var extracted = ShowPictureExtractDialog(shown.Bgra, shown.W, shown.H);
            if (extracted is null) return;

            int page = pic.PageIndex;
            var b = ChromeBox;
            double scale = b.W / shown.W;   // same size on the page as the source's pixels have there
            byte[] png = EncodePng(extracted.Bgra, extracted.Width, extracted.Height);
            var mark = new ImageAnnotation
            {
                PageIndex = page,
                Position = new Point(b.X + 16 + extracted.OffsetX * scale, b.Y + 16 + extracted.OffsetY * scale),
                Scale = scale,
                SourceWidth = extracted.Width,
                SourceHeight = extracted.Height,
                ImageData = Convert.ToBase64String(png)
            };
            AddAnnotation(mark);
            ClearPictureSelection();
            RenderAllAnnotations(page);
            SetTool(EditTool.Select);
            SelectAnnotation(mark, MarkBounds(mark));
            SetStatus("Image extracted as a picture mark. Drag it where you want it");
        }

    }
}
