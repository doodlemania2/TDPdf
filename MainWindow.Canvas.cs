// MainWindow — canvas interaction.
// The pointer/mouse handling for the page canvas: panning, tool gestures, annotation
// hit-testing, drag and resize. Extracted verbatim from MainWindow.xaml.cs.

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
        // Canvas interaction
        // ============================================================

        private void Canvas_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Middle-mouse: modal pan in any tool. Start panning and swallow the event so
            // other handlers (Canvas_MouseLeftButtonDown, AnnotationCanvas children) don't run.
            if (_doc is null) return;
            if (e.ChangedButton != MouseButton.Middle) return;
            if (IsPointerOperationActive) return;

            StartPan(e, MouseButton.Middle);
            e.Handled = true;
        }

        private void Canvas_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning && e.ChangedButton == _panButton)
            {
                EndPan();
                e.Handled = true;
            }
        }

        private void Canvas_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_isPanning)
                EndPan();
            // Don't reset other operations here — WPF can fire this for many reasons,
            // and the corresponding MouseUp handlers reset their own state.
        }

        /// <summary>
        /// The "closed hand" half of the grab affordance (#135 item 5): what the pointer shows while
        /// a surface is actually being carried, as against <see cref="Cursors.Hand"/> — the open
        /// hand — while merely hovering one.
        ///
        /// WPF ships no closed-hand cursor, and the only way to get a true one is a <c>.cur</c>
        /// binary. TDPdf deliberately does not ship one: it would be an unverifiable binary asset on
        /// a repo that builds and reviews on macOS (the same reasoning that made the measurement
        /// tool's icon a vector Path rather than a font glyph). <see cref="Cursors.ScrollAll"/> is
        /// the closest built-in — "this is being moved" — and is already what the pan drag has used
        /// since panning shipped, so this names an existing convention rather than inventing one.
        /// </summary>
        private static Cursor GrabbingCursor => Cursors.ScrollAll;

        private void StartPan(MouseButtonEventArgs e, MouseButton button)
        {
            _isPanning = true;
            _panButton = button;
            // Use ScrollViewer (viewer) coords so deltas don't scale with the page zoom transform.
            _panStartViewerPoint = e.GetPosition(PagePreviewPanel);
            _panStartHOffset = PagePreviewPanel.HorizontalOffset;
            _panStartVOffset = PagePreviewPanel.VerticalOffset;
            _cursorBeforePan ??= _annotationCanvas.Cursor;
            _annotationCanvas.Cursor = GrabbingCursor;
            _annotationCanvas.CaptureMouse();
        }

        private void EndPan()
        {
            _isPanning = false;
            _panButton = null;
            if (_annotationCanvas.IsMouseCaptured)
                _annotationCanvas.ReleaseMouseCapture();
            if (_cursorBeforePan != null)
            {
                _annotationCanvas.Cursor = _cursorBeforePan;
                _cursorBeforePan = null;
            }
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_doc is null) return;
            // Don't intercept clicks on form-field overlay controls (TextBox, checkbox, etc.)
            // — WPF must handle those natively so focus, toggling, and text entry work.
            if (e.OriginalSource is DependencyObject formSrc && IsFormFieldElement(formSrc))
                return;
            // If a middle-mouse pan started before WPF routed the left-button event, swallow it.
            if (_isPanning) { e.Handled = true; return; }
            // Don't intercept clicks on an active text editing box
            if (_activeTextBox is not null && e.OriginalSource is DependencyObject src &&
                IsDescendantOf(src, _activeTextBox))
                return;
            // Don't intercept clicks on the crop confirm bar (canvas uses Preview events which
            // tunnel before child Button clicks fire — we must not swallow them here).
            if (_cropConfirmBar is not null && e.OriginalSource is DependencyObject cropSrc &&
                IsDescendantOf(cropSrc, _cropConfirmBar))
                return;
            // Check if click lands inside a PDF link overlay.
            // We do an explicit bounds check rather than relying on WPF hit-testing through
            // nested transparent canvases, which is unreliable.
            if (_linkOverlays.Count > 0)
            {
                var clickPos = e.GetPosition(_annotationCanvas);
                foreach (var lo in _linkOverlays)
                {
                    double lx = Canvas.GetLeft(lo);
                    double ly = Canvas.GetTop(lo);
                    if (clickPos.X >= lx && clickPos.X <= lx + lo.Width &&
                        clickPos.Y >= ly && clickPos.Y <= ly + lo.Height)
                    {
                        var lTarget = lo.Tag is LinkAnnotInfo lai ? lai.Target : lo.Tag;
                        FollowLinkTarget(lTarget);
                        e.Handled = true;
                        return;
                    }
                }
            }
            var pos = e.GetPosition(_annotationCanvas);
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;

            // Crop corner handle — must be checked before the tool switch so the normal
            // Crop mousedown path (which calls HideCropConfirmBar) doesn't remove handles first.
            if (_cropHandles.Count > 0 && e.OriginalSource is Rectangle cropHandleRect &&
                _cropHandles.Contains(cropHandleRect))
            {
                _activeCropHandleTag = (string)cropHandleRect.Tag;
                _cropHandleDragStart = pos;
                _cropRectAtHandleDrag = _cropCanvasRect;
                _annotationCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (_currentTool == EditTool.EditImage && _imageResizeHandle is not null &&
                e.OriginalSource == _imageResizeHandle && _selectedAnnotation is ImageEditAnnotation selectedImage)
            {
                PushPageSnapshot(selectedImage.PageIndex);
                _isResizingImage = true;
                _resizingImageEdit = selectedImage;
                _imageResizeStart = pos;
                _imageResizeOriginalBounds = selectedImage.TargetBounds;
                _annotationCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            // Check if click is on the resize handle (signature or image annotation)
            if (_resizeHandle is not null && _selectedAnnotation is PlacedAnnotation rsa)
            {
                double hx = Canvas.GetLeft(_resizeHandle);
                double hy = Canvas.GetTop(_resizeHandle);
                if (pos.X >= hx && pos.X <= hx + _resizeHandle.Width &&
                    pos.Y >= hy && pos.Y <= hy + _resizeHandle.Height)
                {
                    PushPageSnapshot(rsa.PageIndex);
                    _isResizingSig = true;
                    _resizeSigStart = pos;
                    _resizeSigStartScale = rsa.Scale;
                    _resizeSigAnnot = rsa;
                    _annotationCanvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }

            // Check if click is on the generic-annotation resize handle (shape / highlight / ink)
            if (_annotResizeHandle is not null && _selectedAnnotation is not null
                && _selectedAnnotation is not PlacedAnnotation)
            {
                double hx = Canvas.GetLeft(_annotResizeHandle);
                double hy = Canvas.GetTop(_annotResizeHandle);
                if (pos.X >= hx && pos.X <= hx + _annotResizeHandle.Width &&
                    pos.Y >= hy && pos.Y <= hy + _annotResizeHandle.Height)
                {
                    BeginAnnotResize(_selectedAnnotation, pos);
                    e.Handled = true;
                    return;
                }
            }

            switch (_currentTool)
            {
                case EditTool.Select:
                    if (e.ClickCount == 2)
                    {
                        ClearSelection();
                        ClearTextSelection();
                        // Prefer re-editing a placed text box under the cursor; otherwise fall through to
                        // the existing-PDF-text white-out editor.
                        if (!TryReeditPlacedText(pos, pageIdx))
                            EditTextAtPosition(pos, pageIdx);
                        e.Handled = true;
                    }
                    else
                    {
                        // Resolve the topmost annotation under the press FIRST, in exactly the old
                        // order — placed annotations (signature / image) outrank the rest — so the
                        // flowing-selection decision below can be made without changing it.
                        PageAnnotation? underPress = null;
                        Rect underPressBounds = Rect.Empty;
                        if (_annotations.TryGetValue(pageIdx, out var pageAnnotsList))
                        {
                            for (int i = pageAnnotsList.Count - 1; i >= 0; i--)
                            {
                                if (pageAnnotsList[i] is PlacedAnnotation pa &&
                                    HitTestAnnotation(pa, pos, out Rect paBounds))
                                {
                                    underPress = pa;
                                    underPressBounds = paBounds;
                                    break;
                                }
                            }
                            // Then non-placed annotations (Shape, Highlight, Ink, Text).
                            if (underPress is null)
                            {
                                for (int i = pageAnnotsList.Count - 1; i >= 0; i--)
                                {
                                    var a = pageAnnotsList[i];
                                    if (a is PlacedAnnotation) continue;
                                    if (a is ShapeAnnotation or HighlightAnnotation or InkAnnotation or TextAnnotation or TextEditAnnotation
                                        && HitTestAnnotation(a, pos, out Rect aBounds))
                                    {
                                        underPress = a;
                                        underPressBounds = aBounds;
                                        break;
                                    }
                                }
                            }
                        }

                        // Flowing text selection (upstream KillerPDF v1.6.5, #127): when the press
                        // lands ON text the character run owns the DRAG, so a paragraph-covering
                        // highlight no longer makes the text underneath unselectable. A plain CLICK
                        // still selects that highlight — resolved on mouse-up via _txtSelClickAnnot.
                        // Everything else keeps drag priority exactly as before, so dragging a
                        // signature, image, shape, ink stroke, or text box that happens to sit over
                        // text still moves it on the first press. Shift and an armed OCR region
                        // capture both force the classic marquee.
                        bool marqueeForced = _ocrRegionMode
                            || (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                        bool textMayWin = underPress is null or HighlightAnnotation;
                        if (!marqueeForced && textMayWin && TryBeginTextSelection(pageIdx, pos))
                        {
                            ClearSelection();
                            RenderAllAnnotations(pageIdx);
                            _txtSelClickAnnot = underPress;
                            _txtSelClickAnnotBounds = underPressBounds;
                            _annotationCanvas.CaptureMouse();
                            e.Handled = true;
                            break;
                        }

                        if (underPress is PlacedAnnotation placed)
                        {
                            ClearSelection();
                            RenderAllAnnotations(pageIdx);
                            SelectAnnotation(placed, underPressBounds);
                            PushPageSnapshot(placed.PageIndex);
                            _isDraggingAnnot = true;
                            _dragAnnotStart = pos;
                            _dragAnnotOrigPos = placed.Position;
                            _dragAnnot = placed;
                            _annotationCanvas.CaptureMouse();
                            e.Handled = true;
                        }
                        else if (underPress is not null)
                        {
                            ClearSelection();
                            RenderAllAnnotations(pageIdx);
                            SelectAnnotation(underPress, underPressBounds);
                            BeginAnnotMove(underPress, pos);
                            e.Handled = true;
                        }
                        else
                        {
                            ClearSelection();
                            ClearTextSelection();
                            _isSelecting = true;
                            _selectStart = pos;
                            _selectRect = new Rectangle
                            {
                                StrokeThickness = 1,
                                Width = 0, Height = 0,
                                IsHitTestVisible = false
                            };
                            // Themed, not hardcoded: MarqueeFill / MarqueeStroke are the selection
                            // accent at marquee alphas, and a resource reference (rather than a
                            // brush snapshot) means the box follows a live theme switch.
                            _selectRect.SetResourceReference(Shape.FillProperty, "MarqueeFill");
                            _selectRect.SetResourceReference(Shape.StrokeProperty, "MarqueeStroke");
                            Canvas.SetLeft(_selectRect, pos.X);
                            Canvas.SetTop(_selectRect, pos.Y);
                            _annotationCanvas.Children.Add(_selectRect);
                            _annotationCanvas.CaptureMouse();
                            e.Handled = true;
                        }
                    }
                    break;

                case EditTool.Text:
                   if (TryRestoreActiveTextBoxFocus(pos))
                   {
                       e.Handled = true;
                       break;
                   }
                    CommitActiveTextBox();
                    // Clicking directly on a text box you already placed almost never means "stack
                    // a second, empty one exactly on top of it" — it means "let me get back into
                    // this one". Select-tool double-click already did this via TryReeditPlacedText;
                    // reported back as a real gap that the Text tool itself didn't, so placing was
                    // easy but coming back to fix a typo meant knowing to switch tools first.
                    if (!TryReeditPlacedText(pos, pageIdx))
                        PlaceTextBox(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.EditText:
                    CommitActiveTextBox();
                    // Same reasoning as the Text tool above: a TextAnnotation placed by Insert Text
                    // is TDPdf's own overlay, not yet part of the PDF's actual content stream (that
                    // only happens at Save), so EditTextAtPosition's PdfPig-based text-run search
                    // could never find it — "Edit Existing Text" is exactly where someone would
                    // naturally try to fix it, and every attempt fell through to "No text found at
                    // this position". Check TDPdf's own overlay first; only fall back to real PDF
                    // content when the click isn't on one of TDPdf's own placed boxes.
                    if (!TryReeditPlacedText(pos, pageIdx))
                        EditTextAtPosition(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.EditImage:
                    CommitActiveTextBox();
                    EditImageAtPosition(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.Highlight:
                case EditTool.Strikethrough:
                case EditTool.Underline:
                {
                    // Markup FLOWS along the character runs exactly like text selection; the release
                    // turns the covered lines into one grouped annotation (upstream KillerPDF
                    // v1.6.5, #127).
                    ClearSelection();
                    ClearTextSelection();
                    if (TryBeginTextSelection(pageIdx, pos))
                    {
                        _txtSelCommitTool = _currentTool;
                        _annotationCanvas.CaptureMouse();
                        e.Handled = true;
                        break;
                    }
                    // Nothing to flow along.
                    //
                    // Strikethrough and Underline are meaningless as a free rectangle, so they only
                    // ever hint — they are new tools with no prior behaviour to preserve.
                    //
                    // The highlighter is different: dragging a rectangle is what it has always done,
                    // and on a scan it is the ONLY thing that works. Upstream could drop that
                    // because their Shapes tool's Box sub-mode explicitly inherited the old
                    // highlighter gesture; ours inherited nothing, so dropping it here would be a
                    // straight capability loss. It therefore keeps the classic drag in both
                    // no-text cases and just SAYS why it is not hugging words.
                    bool hasText = PageHasTextLayer(pageIdx);
                    if (_currentTool != EditTool.Highlight)
                    {
                        SetStatus(hasText ? NoTextHereHint : NoTextLayerHint);
                        e.Handled = true;
                        break;
                    }
                    // Non-blocking explanation, only when the whole page has no text layer; missing
                    // the words on a page that does have text is self-evident and stays silent.
                    if (!hasText) SetStatus(NoTextLayerHighlightHint);
                    _isDrawing = true;
                    _drawStart = pos;
                    var rect = new Rectangle
                    {
                        Fill = FrozenSolidColorBrush(_highlightColor),
                        Width = 0, Height = 0
                    };
                    Canvas.SetLeft(rect, pos.X);
                    Canvas.SetTop(rect, pos.Y);
                    _annotationCanvas.Children.Add(rect);
                    _activePreview = rect;
                    _annotationCanvas.CaptureMouse();
                    break;
                }

                case EditTool.Form:
                {
                    ClearSelection();
                    // Clicking a field edits it; clicking empty page places a new one. Dragging
                    // FROM inside an existing field would otherwise create a second field on top
                    // of it, which is almost never what the gesture meant.
                    var hitWidget = FormWidgetAt(pageIdx, pos);
                    if (hitWidget is not null)
                    {
                        SelectFormWidget(hitWidget);
                        e.Handled = true;
                        break;
                    }
                    if (_selectedFormWidget is not null) SelectFormWidget(null);
                    _isDrawing = true;
                    _drawStart = pos;
                    var formRect = new Rectangle
                    {
                        Fill = new SolidColorBrush(Color.FromArgb(40, 74, 222, 128)),
                        Stroke = (SolidColorBrush)FindResource("AccentGreen"),
                        StrokeThickness = 1.5,
                        StrokeDashArray = new DoubleCollection { 4, 3 },
                        Width = 0, Height = 0,
                        IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(formRect, pos.X);
                    Canvas.SetTop(formRect, pos.Y);
                    _annotationCanvas.Children.Add(formRect);
                    _activePreview = formRect;
                    _annotationCanvas.CaptureMouse();
                    e.Handled = true;
                    break;
                }

                case EditTool.Redact:
                {
                    ClearSelection();
                    // A click on an existing mark takes it back off. Marks have no handles and no
                    // selection state: they are a list of areas, and the only two things worth
                    // doing to one are adding it and taking it away.
                    if (_redactionMarks.TryGetValue(pageIdx, out var existing))
                    {
                        int hit = existing.FindLastIndex(r => r.Contains(pos));
                        if (hit >= 0)
                        {
                            existing.RemoveAt(hit);
                            if (existing.Count == 0) _redactionMarks.Remove(pageIdx);
                            RenderAllAnnotations(pageIdx);
                            ShowRedactSettings();
                            SetStatus($"Redaction mark removed — {PendingRedactionCount} pending");
                            e.Handled = true;
                            break;
                        }
                    }
                    _isDrawing = true;
                    _drawStart = pos;
                    var redactRect = MakeRedactionVisual(0, 0);
                    Canvas.SetLeft(redactRect, pos.X);
                    Canvas.SetTop(redactRect, pos.Y);
                    _annotationCanvas.Children.Add(redactRect);
                    _activePreview = redactRect;
                    _annotationCanvas.CaptureMouse();
                    e.Handled = true;
                    break;
                }

                case EditTool.Measure:
                {
                    // A new drag replaces whatever was being read before. Deliberately not a
                    // second ruler: two measurements on one page with no way to tell which
                    // caption belongs to which line is worse than one you can re-take instantly.
                    ClearSelection();
                    ClearMeasurement();
                    _isMeasuring = true;
                    _hasMeasurement = true;
                    _measurePage = pageIdx;
                    _measureA = pos;
                    _measureB = pos;
                    RenderMeasurement();
                    _annotationCanvas.CaptureMouse();
                    e.Handled = true;
                    break;
                }

                case EditTool.Crop:
                    ClearSelection();
                    ClearCropSelection();
                    HideCropConfirmBar();
                    _isDrawing = true;
                    _drawStart = pos;
                    var cropRect = new Rectangle
                    {
                        Fill = new SolidColorBrush(Color.FromArgb(35, 74, 222, 128)),
                        Stroke = (SolidColorBrush)FindResource("AccentGreen"),
                        StrokeThickness = 2,
                        StrokeDashArray = new DoubleCollection { 6, 3 },
                        Width = 0,
                        Height = 0,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(cropRect, pos.X);
                    Canvas.SetTop(cropRect, pos.Y);
                    _annotationCanvas.Children.Add(cropRect);
                    _activePreview = cropRect;
                    _annotationCanvas.CaptureMouse();
                    e.Handled = true;
                    break;

                case EditTool.Draw:
                    ClearSelection();
                    _isDrawing = true;
                    _activeInk = new InkAnnotation { PageIndex = pageIdx, StrokeWidth = _drawWidth };
                    _activeInk.SetColor(_drawColor);
                    _activeInk.Points.Add(pos);
                    var poly = new Polyline
                    {
                        Stroke = FrozenSolidColorBrush(_drawColor),
                        StrokeThickness = _drawWidth,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    poly.Points.Add(pos);
                    _annotationCanvas.Children.Add(poly);
                    _activePreview = poly;
                    _annotationCanvas.CaptureMouse();
                    break;

                case EditTool.Signature:
                    if (_pendingSignature is not null)
                    {
                        PlaceSignature(pos, pageIdx);
                        e.Handled = true;
                    }
                    else
                    {
                        ShowSignaturePopup();
                    }
                    break;

                case EditTool.Image:
                    PlaceImageFromDialog(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.Pan:
                    StartPan(e, MouseButton.Left);
                    e.Handled = true;
                    break;

                case EditTool.Erase:
                {
                    ClearSelection();
                    if (_annotations.TryGetValue(pageIdx, out var erasePageList))
                    {
                        for (int i = erasePageList.Count - 1; i >= 0; i--)
                        {
                            if (HitTestAnnotation(erasePageList[i], pos, out _))
                            {
                                PushPageSnapshot(pageIdx);
                                erasePageList.RemoveAt(i);
                                RenderAllAnnotations(pageIdx);
                                MarkDirty();
                                SetStatus("Erased annotation");
                                break;
                            }
                        }
                    }
                    e.Handled = true;
                    break;
                }

                case EditTool.Shape:
                {
                    // Freeform polygon: vertices go down click by click instead of being dragged
                    // out, and a double-click on the third-or-later vertex closes the shape (the
                    // first click of the pair already placed a point there — CommitShapePolygon
                    // drops the duplicate).
                    if (_shapeKind == ShapeKind.Polygon)
                    {
                        if (e.ClickCount == 2 && _polyVertices.Count >= 3) CommitShapePolygon();
                        else ShapePolyClick(pageIdx, pos);
                        e.Handled = true;
                        break;
                    }

                    ClearSelection();
                    _isDrawing = true;
                    _drawStart = pos;
                    Shape preview = _shapeKind switch
                    {
                        ShapeKind.Rectangle => new Rectangle
                        {
                            Stroke = FrozenSolidColorBrush(_shapeStrokeColor),
                            StrokeThickness = _shapeStrokeWidth,
                            Fill = _shapeHasFill
                                ? FrozenSolidColorBrush(_shapeFillColor)
                                : (Brush)Brushes.Transparent,
                            Width = 0, Height = 0,
                            IsHitTestVisible = false
                        },
                        ShapeKind.Ellipse => new Ellipse
                        {
                            Stroke = FrozenSolidColorBrush(_shapeStrokeColor),
                            StrokeThickness = _shapeStrokeWidth,
                            Fill = _shapeHasFill
                                ? FrozenSolidColorBrush(_shapeFillColor)
                                : (Brush)Brushes.Transparent,
                            Width = 0, Height = 0,
                            IsHitTestVisible = false
                        },
                        ShapeKind.Line => new Line
                        {
                            Stroke = FrozenSolidColorBrush(_shapeStrokeColor),
                            StrokeThickness = _shapeStrokeWidth,
                            StrokeStartLineCap = PenLineCap.Round,
                            StrokeEndLineCap = PenLineCap.Round,
                            X1 = pos.X, Y1 = pos.Y, X2 = pos.X, Y2 = pos.Y,
                            IsHitTestVisible = false
                        },
                        _ => throw new InvalidOperationException()
                    };
                    if (_shapeKind != ShapeKind.Line)
                    {
                        Canvas.SetLeft(preview, pos.X);
                        Canvas.SetTop(preview, pos.Y);
                    }
                    _annotationCanvas.Children.Add(preview);
                    _activePreview = preview;
                    _annotationCanvas.CaptureMouse();
                    e.Handled = true;
                    break;
                }
            }
        }

        private void BeginAnnotMove(PageAnnotation annot, Point pos)
        {
            PushPageSnapshot(annot.PageIndex);
            _isMovingAnnot = true;
            _movingAnnot = annot;
            _moveStartCanvas = pos;
            _moveOriginalGeom = CaptureGeometry(annot);
            _annotationCanvas.CaptureMouse();
        }

        private void BeginAnnotResize(PageAnnotation annot, Point pos)
        {
            PushPageSnapshot(annot.PageIndex);
            // Seed a legacy (auto-sized) text box's fixed Width/Height from its current extent so the
            // resize drag has a concrete basis to grow/shrink from.
            if (annot is TextAnnotation t && (t.Width <= 0 || t.Height <= 0))
            {
                var sz = MeasureTextAnnotation(t);
                if (t.Width <= 0) t.Width = sz.Width;
                if (t.Height <= 0) t.Height = sz.Height;
            }
            _isResizingAnnot = true;
            _resizingAnnot = annot;
            _resizeStartCanvas = pos;
            _resizeOriginalGeom = CaptureGeometry(annot);
            _annotationCanvas.CaptureMouse();
        }

        /// <summary>
        /// Snapshot the geometric state of an annotation so a move or resize can be applied
        /// relative to the starting state without compounding rounding errors.
        /// </summary>
        /// <summary>
        /// Captured geometry of a <see cref="ShapeAnnotation"/>. Carries both geometry models:
        /// Start/End for rectangle / ellipse / line, and <see cref="Points"/> for a polygon
        /// (null for the others), so one capture type covers every shape kind.
        /// </summary>
        private sealed class ShapeGeom
        {
            public Point Start;
            public Point End;
            public double StrokeWidth;
            public List<Point>? Points;
        }

        /// <summary>
        /// Captured geometry of a <see cref="MarkupAnnotation"/>: the union bounds plus the per-line
        /// rects, so a move/resize can be applied relative to the start without the lines drifting
        /// out of step with the bounds.
        /// </summary>
        private sealed class MarkupGeom
        {
            public Rect Bounds;
            public List<Rect> Lines = new();
        }

        private static object CaptureGeometry(PageAnnotation annot) => annot switch
        {
            ShapeAnnotation s => new ShapeGeom
            {
                Start = s.Start, End = s.End, StrokeWidth = s.StrokeWidth,
                Points = s.Kind == ShapeKind.Polygon ? new List<Point>(s.Points) : null
            },
            // Markup must come before HighlightAnnotation — it is a subclass.
            MarkupAnnotation m => new MarkupGeom { Bounds = m.Bounds, Lines = new List<Rect>(m.LineRects) },
            HighlightAnnotation h => h.Bounds,
            InkAnnotation i => new List<Point>(i.Points),
            TextAnnotation t => (Position: t.Position, Width: t.Width, Height: t.Height),
            TextEditAnnotation tea => (Position: tea.Position, Bounds: tea.OriginalBounds),
            _ => 0
        };

        /// <summary>
        /// Returns true if the annotation's geometry matches the captured original — used to
        /// drop no-op snapshots when a click without drag triggered BeginAnnotMove/Resize.
        /// </summary>
        private static bool GeometryUnchanged(PageAnnotation annot, object? original)
        {
            if (original is null) return false;
            switch (annot)
            {
                case ShapeAnnotation s when original is ShapeGeom o:
                    if (o.Points is not null)
                    {
                        if (s.Points.Count != o.Points.Count) return false;
                        for (int i = 0; i < o.Points.Count; i++)
                            if (s.Points[i] != o.Points[i]) return false;
                        return true;
                    }
                    return s.Start == o.Start && s.End == o.End;
                case MarkupAnnotation m when original is MarkupGeom mo:
                    if (m.LineRects.Count != mo.Lines.Count) return false;
                    for (int i = 0; i < mo.Lines.Count; i++)
                        if (m.LineRects[i] != mo.Lines[i]) return false;
                    return m.Bounds == mo.Bounds;
                case HighlightAnnotation h when original is Rect r:
                    return h.Bounds == r;
                case InkAnnotation ink when original is List<Point> pts:
                    if (ink.Points.Count != pts.Count) return false;
                    for (int i = 0; i < pts.Count; i++)
                        if (ink.Points[i] != pts[i]) return false;
                    return true;
                case TextAnnotation t when original is ValueTuple<Point, double, double> tp:
                    return t.Position == tp.Item1 && t.Width == tp.Item2 && t.Height == tp.Item3;
                case TextEditAnnotation tea when original is ValueTuple<Point, Rect> teo:
                    return tea.Position == teo.Item1 && tea.OriginalBounds == teo.Item2;
                default:
                    return false;
            }
        }

        private void ApplyMoveTo(PageAnnotation annot, Point cur, Point start, object original)
        {
            double dx = cur.X - start.X;
            double dy = cur.Y - start.Y;
            switch (annot)
            {
                case ShapeAnnotation s when original is ShapeGeom o:
                    if (o.Points is not null)
                    {
                        s.Points.Clear();
                        foreach (var p in o.Points) s.Points.Add(new Point(p.X + dx, p.Y + dy));
                        break;
                    }
                    s.Start = new Point(o.Start.X + dx, o.Start.Y + dy);
                    s.End   = new Point(o.End.X + dx, o.End.Y + dy);
                    break;
                // Markup carries per-line rects as well as the union bounds; both move together.
                // Matched before HighlightAnnotation — it is a subclass.
                case MarkupAnnotation m when original is MarkupGeom mo:
                    m.LineRects.Clear();
                    foreach (var lr in mo.Lines)
                        m.LineRects.Add(new Rect(lr.X + dx, lr.Y + dy, lr.Width, lr.Height));
                    m.Bounds = new Rect(mo.Bounds.X + dx, mo.Bounds.Y + dy, mo.Bounds.Width, mo.Bounds.Height);
                    break;
                case HighlightAnnotation h when original is Rect r:
                    h.Bounds = new Rect(r.X + dx, r.Y + dy, r.Width, r.Height);
                    break;
                case InkAnnotation ink when original is List<Point> pts:
                    ink.Points.Clear();
                    foreach (var p in pts) ink.Points.Add(new Point(p.X + dx, p.Y + dy));
                    break;
                case TextAnnotation t when original is ValueTuple<Point, double, double> tp:
                    t.Position = new Point(tp.Item1.X + dx, tp.Item1.Y + dy);
                    break;
                // In-place text edits carry two anchors that must move together: Position (where the
                // replacement glyphs draw) and OriginalBounds (the whiteout + hit-test region). Moving
                // only one would desync the visible text from the box that hides the old content.
                case TextEditAnnotation tea when original is ValueTuple<Point, Rect> teo:
                    tea.Position = new Point(teo.Item1.X + dx, teo.Item1.Y + dy);
                    tea.OriginalBounds = new Rect(
                        teo.Item2.X + dx, teo.Item2.Y + dy, teo.Item2.Width, teo.Item2.Height);
                    break;
            }
        }

        private void ApplyResizeTo(PageAnnotation annot, Point cur, Point start, object original)
        {
            switch (annot)
            {
                case ShapeAnnotation s when original is ShapeGeom o:
                {
                    if (o.Points is not null)
                    {
                        // Polygon: scale the vertices about the bounding box's top-left, exactly
                        // like the ink path below, so the corner handle stretches the whole shape.
                        if (o.Points.Count == 0) break;
                        double pMinX = o.Points.Min(p => p.X), pMinY = o.Points.Min(p => p.Y);
                        double pMaxX = o.Points.Max(p => p.X), pMaxY = o.Points.Max(p => p.Y);
                        double pOrigW = Math.Max(1, pMaxX - pMinX), pOrigH = Math.Max(1, pMaxY - pMinY);
                        double pNewW = Math.Max(4, pOrigW + (cur.X - start.X));
                        double pNewH = Math.Max(4, pOrigH + (cur.Y - start.Y));
                        double psx = pNewW / pOrigW, psy = pNewH / pOrigH;
                        s.Points.Clear();
                        foreach (var p in o.Points)
                            s.Points.Add(new Point(pMinX + (p.X - pMinX) * psx, pMinY + (p.Y - pMinY) * psy));
                        break;
                    }
                    // Anchor to Start; drag End.
                    s.Start = o.Start;
                    s.End = new Point(o.End.X + (cur.X - start.X), o.End.Y + (cur.Y - start.Y));
                    break;
                }
                // Markup: stretch the union box from its top-left and carry every line rect with
                // it proportionally. Matched before HighlightAnnotation — it is a subclass.
                case MarkupAnnotation m when original is MarkupGeom mo:
                {
                    double origW = Math.Max(1, mo.Bounds.Width);
                    double origH = Math.Max(1, mo.Bounds.Height);
                    double newW = Math.Max(4, mo.Bounds.Width + (cur.X - start.X));
                    double newH = Math.Max(4, mo.Bounds.Height + (cur.Y - start.Y));
                    double msx = newW / origW, msy = newH / origH;
                    m.LineRects.Clear();
                    foreach (var lr in mo.Lines)
                        m.LineRects.Add(new Rect(
                            mo.Bounds.X + (lr.X - mo.Bounds.X) * msx,
                            mo.Bounds.Y + (lr.Y - mo.Bounds.Y) * msy,
                            Math.Max(1, lr.Width * msx),
                            Math.Max(1, lr.Height * msy)));
                    m.Bounds = new Rect(mo.Bounds.X, mo.Bounds.Y, newW, newH);
                    break;
                }
                case HighlightAnnotation h when original is Rect r:
                {
                    double newW = Math.Max(4, r.Width + (cur.X - start.X));
                    double newH = Math.Max(4, r.Height + (cur.Y - start.Y));
                    h.Bounds = new Rect(r.X, r.Y, newW, newH);
                    break;
                }
                case InkAnnotation ink when original is List<Point> pts:
                {
                    if (pts.Count == 0) break;
                    double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y);
                    double maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
                    double origW = Math.Max(1, maxX - minX), origH = Math.Max(1, maxY - minY);
                    double newW = Math.Max(4, origW + (cur.X - start.X));
                    double newH = Math.Max(4, origH + (cur.Y - start.Y));
                    double sx = newW / origW, sy = newH / origH;
                    ink.Points.Clear();
                    foreach (var p in pts)
                        ink.Points.Add(new Point(minX + (p.X - minX) * sx, minY + (p.Y - minY) * sy));
                    double uniform = (sx + sy) * 0.5;
                    ink.StrokeWidth = Math.Max(0.5, ink.StrokeWidth * uniform);
                    break;
                }
                case TextAnnotation t when original is ValueTuple<Point, double, double> tp:
                {
                    // Anchor top-left; drag bottom-right to set the wrap Width and box Height.
                    t.Width = Math.Max(32, tp.Item2 + (cur.X - start.X));
                    t.Height = Math.Max(t.FontSize + 6, tp.Item3 + (cur.Y - start.Y));
                    break;
                }
                case TextEditAnnotation tea when original is ValueTuple<Point, Rect> teo:
                {
                    // Anchor top-left; drag bottom-right to grow/shrink the whiteout + hit-test box —
                    // lets a default sized too generously (e.g. bleeding into a nearby table border)
                    // be pulled back in by hand. Position (where the replacement text draws) is left
                    // alone, matching the top-left anchor.
                    double newW = Math.Max(16, teo.Item2.Width + (cur.X - start.X));
                    double newH = Math.Max(tea.FontSize + 4, teo.Item2.Height + (cur.Y - start.Y));
                    tea.OriginalBounds = new Rect(teo.Item2.X, teo.Item2.Y, newW, newH);
                    break;
                }
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            // Don't interfere with mouse interaction inside form-field overlays.
            if (e.OriginalSource is DependencyObject moveSrc && IsFormFieldElement(moveSrc))
                return;

            // Link hover: surface the hovered link's target in the status bar. Only on button-up moves so it
            // never fights an in-progress drag (move/resize/pan all hold the left button). Bounds-tested like
            // the click path because transparent overlay canvases aren't reliable WPF hit-test targets.
            bool overLink = false;
            if (_linkOverlays.Count > 0 && e.LeftButton == MouseButtonState.Released)
            {
                var hp = e.GetPosition(_annotationCanvas);
                string? hoverTarget = null;
                foreach (var lo in _linkOverlays)
                {
                    double lx = Canvas.GetLeft(lo), ly = Canvas.GetTop(lo);
                    if (hp.X >= lx && hp.X <= lx + lo.Width && hp.Y >= ly && hp.Y <= ly + lo.Height)
                    {
                        object? t = lo.Tag is LinkAnnotInfo lai ? lai.Target : lo.Tag;
                        hoverTarget = t is int gp ? $"Go to page {gp + 1}" : t as string;
                        overLink = true;
                        break;
                    }
                }
                ShowLinkHoverStatus(hoverTarget);
            }

            // Pan first — uses viewer coords so deltas don't scale with the page transform.
            if (_isPanning)
            {
                var viewerPos = e.GetPosition(PagePreviewPanel);
                double dx = viewerPos.X - _panStartViewerPoint.X;
                double dy = viewerPos.Y - _panStartViewerPoint.Y;
                PagePreviewPanel.ScrollToHorizontalOffset(_panStartHOffset - dx);
                PagePreviewPanel.ScrollToVerticalOffset(_panStartVOffset - dy);
                return;
            }

            var pos = e.GetPosition(_annotationCanvas);
            pos.X = Math.Clamp(pos.X, 0, _annotationCanvas.ActualWidth);
            pos.Y = Math.Clamp(pos.Y, 0, _annotationCanvas.ActualHeight);

            // Select tool hover affordance (#135 item 5): hand over a link, I-beam over selectable
            // text, arrow over empty page — because the Select tool genuinely behaves differently in
            // each case and the cursor is the only warning before the drag starts. Gated on no
            // pointer operation being live so it can never fight a drag that owns the cursor (a
            // marquee, a pan, an annotation move); those paths all return above or below anyway,
            // but the guard keeps that from being an accident of ordering.
            if (_currentTool == EditTool.Select && e.LeftButton == MouseButtonState.Released &&
                !IsPointerOperationActive)
                UpdateSelectHoverCursor(pos, overLink);

            // Shapes tool, freeform polygon: track the rubber band from the last placed vertex and
            // light the first-vertex snap ring. No button is held during placement, so this runs
            // ahead of every drag path below (and after the pan check, which owns middle-drag).
            if (_currentTool == EditTool.Shape && _polyVertices.Count > 0)
            {
                UpdateShapePolyRubber(pos);
                return;
            }

            // Generic annotation move
            if (_isMovingAnnot && _movingAnnot is not null && _moveOriginalGeom is not null)
            {
                ApplyMoveTo(_movingAnnot, pos, _moveStartCanvas, _moveOriginalGeom);
                RenderAllAnnotations(_movingAnnot.PageIndex);
                if (HitTestAnnotation(_movingAnnot, GetAnyPointInside(_movingAnnot), out Rect mb))
                    RefreshSelectionVisuals(mb);
                MarkDirty();
                return;
            }

            // Generic annotation resize
            if (_isResizingAnnot && _resizingAnnot is not null && _resizeOriginalGeom is not null)
            {
                ApplyResizeTo(_resizingAnnot, pos, _resizeStartCanvas, _resizeOriginalGeom);
                RenderAllAnnotations(_resizingAnnot.PageIndex);
                if (HitTestAnnotation(_resizingAnnot, GetAnyPointInside(_resizingAnnot), out Rect rb))
                    RefreshSelectionVisuals(rb);
                MarkDirty();
                return;
            }

            // Signature resize drag
            if (_isResizingSig && _resizeSigAnnot is not null)
            {
                double dx = pos.X - _resizeSigStart.X;
                double dy = pos.Y - _resizeSigStart.Y;
                double delta = (Math.Abs(dx) > Math.Abs(dy) ? dx : dy);
                // #181: the divisor and the starting scale both come off the annotation, and an
                // annotation placed from a damaged signatures.json entry could carry 0 or a non-finite
                // value in either. Math.Max does not filter those out (it returns NaN for NaN and ∞
                // for ∞) and the result is written straight back onto the annotation below, so one bad
                // drag used to poison every later render. Substitute the standard canvas instead.
                double srcW = IsFinitePositive(_resizeSigAnnot.SourceWidth)
                    ? _resizeSigAnnot.SourceWidth : DefaultSigCanvasW;
                double startScale = IsFinitePositive(_resizeSigStartScale) ? _resizeSigStartScale : 0.5;
                double newScale = Math.Max(0.05, startScale + delta / srcW);
                _resizeSigAnnot.Scale = newScale;

                // Update selection border and handle position live. SourceHeight gets the same
                // treatment as SourceWidth above: newW/newH feed the border's Width/Height, which WPF
                // rejects outright when either is not a real number.
                double srcH = IsFinitePositive(_resizeSigAnnot.SourceHeight)
                    ? _resizeSigAnnot.SourceHeight : DefaultSigCanvasH;
                double newW = srcW * newScale;
                double newH = srcH * newScale;
                if (_selectionBorder is not null)
                {
                    _selectionBorder.Width  = newW + 8;
                    _selectionBorder.Height = newH + 8;
                }
                if (_resizeHandle is not null)
                {
                    double hx = _resizeSigAnnot.Position.X + newW - 4 - _resizeHandle.Width / 2;
                    double hy = _resizeSigAnnot.Position.Y + newH - 4 - _resizeHandle.Height / 2;
                    Canvas.SetLeft(_resizeHandle, hx);
                    Canvas.SetTop(_resizeHandle, hy);
                }

                // Re-render annotations to show updated size
                RenderAllAnnotations(_resizeSigAnnot.PageIndex);
                // Restore selection visuals (RenderAllAnnotations clears canvas children including our overlays)
                _annotationCanvas.Children.Add(_selectionBorder!);
                _annotationCanvas.Children.Add(_resizeHandle!);
                return;
            }

            // Annotation drag-to-move
            if (_isDraggingAnnot && _dragAnnot is not null)
            {
                double dx = pos.X - _dragAnnotStart.X;
                double dy = pos.Y - _dragAnnotStart.Y;
                _dragAnnot.Position = new Point(_dragAnnotOrigPos.X + dx, _dragAnnotOrigPos.Y + dy);
                double w = _dragAnnot.SourceWidth * _dragAnnot.Scale;
                double h = _dragAnnot.SourceHeight * _dragAnnot.Scale;
                if (_selectionBorder is not null)
                {
                    Canvas.SetLeft(_selectionBorder, _dragAnnot.Position.X - 4);
                    Canvas.SetTop(_selectionBorder, _dragAnnot.Position.Y - 4);
                }
                if (_resizeHandle is not null)
                {
                    Canvas.SetLeft(_resizeHandle, _dragAnnot.Position.X + w - 4 - _resizeHandle.Width / 2);
                    Canvas.SetTop(_resizeHandle, _dragAnnot.Position.Y + h - 4 - _resizeHandle.Height / 2);
                }
                RenderAllAnnotations(_dragAnnot.PageIndex);
                _annotationCanvas.Children.Add(_selectionBorder!);
                _annotationCanvas.Children.Add(_resizeHandle!);
                return;
            }

            // Flowing text selection drag (upstream KillerPDF v1.6.5, #127): move the focus caret
            // and repaint the per-line quads. Runs ahead of the rectangle marquee below — only one
            // of the two can ever be armed.
            if (_txtSelActive)
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    UpdateTextSelectionDrag(pos);
                    return;
                }
                // Capture can be lost without a MouseUp ever arriving (WPF drops it for plenty of
                // reasons). Settle the gesture here rather than letting the selection keep tracking
                // a button that is not held, then fall through to the normal move handling.
                if (_annotationCanvas.IsMouseCaptured) _annotationCanvas.ReleaseMouseCapture();
                FinishTextSelection();
            }

            // Text selection drag
            if (_isSelecting && _selectRect is not null)
            {
                Canvas.SetLeft(_selectRect, Math.Min(pos.X, _selectStart.X));
                Canvas.SetTop(_selectRect, Math.Min(pos.Y, _selectStart.Y));
                _selectRect.Width = Math.Abs(pos.X - _selectStart.X);
                _selectRect.Height = Math.Abs(pos.Y - _selectStart.Y);
                return;
            }

            if (_isResizingImage && _resizingImageEdit is not null)
            {
                ResizeImageEditPreview(pos);
                return;
            }

            // Measure drag. Ahead of the _isDrawing switch below and on its own flag, because the
            // ruler is several visuals rather than the one _activePreview that path assumes — and
            // because CancelActivePointerOperation(removePreview: true) removes _activePreview from
            // the canvas, which would strip the line and orphan the caps and the caption.
            if (_isMeasuring)
            {
                _measureB = pos;
                RenderMeasurement();
                return;
            }

            if (!_isDrawing || _activePreview is null) return;

            switch (_currentTool)
            {
                case EditTool.Highlight when _activePreview is Rectangle:
                case EditTool.Form when _activePreview is Rectangle:
                case EditTool.Redact when _activePreview is Rectangle:
                case EditTool.Crop when _activePreview is Rectangle:
                    var rect = (Rectangle)_activePreview;
                    Canvas.SetLeft(rect, Math.Min(pos.X, _drawStart.X));
                    Canvas.SetTop(rect, Math.Min(pos.Y, _drawStart.Y));
                    rect.Width = Math.Abs(pos.X - _drawStart.X);
                    rect.Height = Math.Abs(pos.Y - _drawStart.Y);
                    break;

                case EditTool.Draw when _activePreview is Polyline poly && _activeInk is not null:
                    _activeInk.Points.Add(pos);
                    poly.Points.Add(pos);
                    break;

                case EditTool.Shape when _activePreview is Line lnPrev:
                    lnPrev.X2 = pos.X;
                    lnPrev.Y2 = pos.Y;
                    break;

                case EditTool.Shape when _activePreview is FrameworkElement shapePrev:
                {
                    double sx = Math.Min(pos.X, _drawStart.X);
                    double sy = Math.Min(pos.Y, _drawStart.Y);
                    double sw = Math.Abs(pos.X - _drawStart.X);
                    double sh = Math.Abs(pos.Y - _drawStart.Y);
                    Canvas.SetLeft(shapePrev, sx);
                    Canvas.SetTop(shapePrev, sy);
                    shapePrev.Width = sw;
                    shapePrev.Height = sh;
                    break;
                }

                case EditTool.Crop when _activePreview is Rectangle crect:
                    Canvas.SetLeft(crect, Math.Min(pos.X, _drawStart.X));
                    Canvas.SetTop(crect, Math.Min(pos.Y, _drawStart.Y));
                    crect.Width = Math.Abs(pos.X - _drawStart.X);
                    crect.Height = Math.Abs(pos.Y - _drawStart.Y);
                    break;
            }

            // Crop corner handle drag — resize the crop rect live.
            if (_activeCropHandleTag is not null && _cropPreviewRect is not null)
            {
                double dx = pos.X - _cropHandleDragStart.X;
                double dy = pos.Y - _cropHandleDragStart.Y;
                var r = _cropRectAtHandleDrag;
                double newX = r.X, newY = r.Y, newW = r.Width, newH = r.Height;
                switch (_activeCropHandleTag)
                {
                    case "NW":
                        newX = Math.Min(r.Right - 10, r.X + dx);
                        newY = Math.Min(r.Bottom - 10, r.Y + dy);
                        newW = r.Right - newX;
                        newH = r.Bottom - newY;
                        break;
                    case "NE":
                        newY = Math.Min(r.Bottom - 10, r.Y + dy);
                        newW = Math.Max(10, r.Width + dx);
                        newH = r.Bottom - newY;
                        break;
                    case "SE":
                        newW = Math.Max(10, r.Width + dx);
                        newH = Math.Max(10, r.Height + dy);
                        break;
                    case "SW":
                        newX = Math.Min(r.Right - 10, r.X + dx);
                        newW = r.Right - newX;
                        newH = Math.Max(10, r.Height + dy);
                        break;
                }
                _cropCanvasRect = new Rect(newX, newY, newW, newH);
                UpdateCropRectVisuals();
            }
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Pan was started with left-click in Pan tool: release here.
            if (_isPanning && _panButton == MouseButton.Left)
            {
                EndPan();
                e.Handled = true;
                return;
            }

            // Don't process release events that originate inside the crop confirm bar.
            if (_cropConfirmBar is not null && e.OriginalSource is DependencyObject cropSrc &&
                IsDescendantOf(cropSrc, _cropConfirmBar))
                return;

            int pageIdx = PageList.SelectedIndex;

            // Finish crop handle drag
            if (_activeCropHandleTag is not null)
            {
                _activeCropHandleTag = null;
                if (_annotationCanvas.IsMouseCaptured) _annotationCanvas.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }

            // Finish generic annotation move
            if (_isMovingAnnot)
            {
                var ma = _movingAnnot;
                var origGeom = _moveOriginalGeom;
                _isMovingAnnot = false;
                _movingAnnot = null;
                _moveOriginalGeom = null;
                if (_annotationCanvas.IsMouseCaptured) _annotationCanvas.ReleaseMouseCapture();
                if (ma is not null)
                {
                    if (GeometryUnchanged(ma, origGeom))
                        DropTopSnapshotIfFor(ma.PageIndex);
                    RenderAllAnnotations(ma.PageIndex);
                    if (HitTestAnnotation(ma, GetAnyPointInside(ma), out Rect mb))
                        SelectAnnotation(ma, mb);
                }
                return;
            }

            // Finish generic annotation resize
            if (_isResizingAnnot)
            {
                var ra = _resizingAnnot;
                var origGeom = _resizeOriginalGeom;
                _isResizingAnnot = false;
                _resizingAnnot = null;
                _resizeOriginalGeom = null;
                if (_annotationCanvas.IsMouseCaptured) _annotationCanvas.ReleaseMouseCapture();
                if (ra is not null)
                {
                    if (GeometryUnchanged(ra, origGeom))
                        DropTopSnapshotIfFor(ra.PageIndex);
                    RenderAllAnnotations(ra.PageIndex);
                    if (HitTestAnnotation(ra, GetAnyPointInside(ra), out Rect rb))
                        SelectAnnotation(ra, rb);
                }
                return;
            }

            // Finish annotation drag-to-move
            if (_isDraggingAnnot)
            {
                _isDraggingAnnot = false;
                _annotationCanvas.ReleaseMouseCapture();
                if (_dragAnnot is not null)
                {
                    var da = _dragAnnot;
                    _dragAnnot = null;
                    if (da.Position == _dragAnnotOrigPos)
                        DropTopSnapshotIfFor(da.PageIndex);
                    else
                        MarkDirty();
                    RenderAllAnnotations(da.PageIndex);
                    double w = da.SourceWidth * da.Scale;
                    double h = da.SourceHeight * da.Scale;
                    SelectAnnotation(da, new Rect(da.Position.X, da.Position.Y, w, h));
                }
                return;
            }

            // Finish signature resize
            if (_isResizingSig)
            {
                _isResizingSig = false;
                _annotationCanvas.ReleaseMouseCapture();
                if (_resizeSigAnnot is not null)
                {
                    // Final re-render and re-select to reposition handle cleanly
                    var sa = _resizeSigAnnot;
                    _resizeSigAnnot = null;
                    if (sa.Scale == _resizeSigStartScale)
                        DropTopSnapshotIfFor(sa.PageIndex);
                    else
                        MarkDirty();
                    RenderAllAnnotations(sa.PageIndex);
                    double newW = sa.SourceWidth * sa.Scale;
                    double newH = sa.SourceHeight * sa.Scale;
                    SelectAnnotation(sa, new Rect(sa.Position.X, sa.Position.Y, newW, newH));
                    MarkDirty();
                }
                return;
            }

            // Flowing text selection release (upstream KillerPDF v1.6.5, #127): commit the run —
            // copy it and keep the quads on screen, or turn it into markup when a markup tool owns
            // the gesture. A click that never passed the drag threshold selects the annotation that
            // was under the press instead.
            if (_txtSelActive)
            {
                if (_annotationCanvas.IsMouseCaptured) _annotationCanvas.ReleaseMouseCapture();
                FinishTextSelection();
                e.Handled = true;
                return;
            }

            // Handle text selection release
            if (_isSelecting)
            {
                _isSelecting = false;
                _annotationCanvas.ReleaseMouseCapture();
                var pos = e.GetPosition(_annotationCanvas);
                double dragW = Math.Abs(pos.X - _selectStart.X);
                double dragH = Math.Abs(pos.Y - _selectStart.Y);

                // A pending "OCR Region" arm consumes this drag regardless of outcome.
                bool ocrRegion = _ocrRegionMode;
                _ocrRegionMode = false;

                if (dragW < 5 && dragH < 5)
                {
                    // Tiny drag = single click -> try annotation selection
                    ClearTextSelection();
                    if (pageIdx >= 0 && _annotations.ContainsKey(pageIdx))
                    {
                        for (int i = _annotations[pageIdx].Count - 1; i >= 0; i--)
                        {
                            if (HitTestAnnotation(_annotations[pageIdx][i], _selectStart, out Rect bounds))
                            {
                                SelectAnnotation(_annotations[pageIdx][i], bounds);
                                break;
                            }
                        }
                    }
                }
                else
                {
                    // Real drag -> extract text from the rectangle, or OCR it if the OCR-region tool is armed.
                    var selectBounds = new Rect(
                        Math.Min(pos.X, _selectStart.X), Math.Min(pos.Y, _selectStart.Y),
                        dragW, dragH);
                    if (ocrRegion) OcrRegion(pageIdx, selectBounds);
                    else ExtractTextFromRegion(pageIdx, selectBounds);
                }
                return;
            }

            if (_isResizingImage && _resizingImageEdit is not null)
            {
                _isResizingImage = false;
                _annotationCanvas.ReleaseMouseCapture();
                var resizing = _resizingImageEdit;
                if (resizing.TargetBounds == _imageResizeOriginalBounds)
                {
                    DropTopSnapshotIfFor(resizing.PageIndex);
                }
                else
                {
                    MarkDirty();
                }
                RenderAllAnnotations(resizing.PageIndex);
                SelectAnnotation(resizing, resizing.TargetBounds);
                SetStatus("Image resize committed - save to apply white-out + overdraw");
                _resizingImageEdit = null;
                return;
            }

            // Finish a measurement. The ruler stays on the page after the button comes up — the
            // whole point is to be able to read it — and is replaced by the next drag, cleared by
            // a page change, and taken away with the tool (Esc included).
            if (_isMeasuring)
            {
                _isMeasuring = false;
                if (_annotationCanvas.IsMouseCaptured) _annotationCanvas.ReleaseMouseCapture();
                // Clamped the same way Canvas_MouseMove clamps, so releasing past the edge of the
                // page settles on the number that was on screen rather than silently adding the
                // overshoot — the mapping would happily extrapolate off the page box.
                var endPos = e.GetPosition(_annotationCanvas);
                _measureB = new Point(
                    Math.Clamp(endPos.X, 0, _annotationCanvas.ActualWidth),
                    Math.Clamp(endPos.Y, 0, _annotationCanvas.ActualHeight));

                // A click that never moved is not a measurement; leaving a zero-length ruler and a
                // "0 in · 0 mm · 0 pt" caption behind on a stray click would just be litter. 3px
                // matches the highlighter's and redaction's stray-click threshold.
                double mdx = _measureB.X - _measureA.X, mdy = _measureB.Y - _measureA.Y;
                if (Math.Sqrt(mdx * mdx + mdy * mdy) < 3)
                {
                    ClearMeasurement();
                    SetStatus("Measure: drag from one point to another to measure the distance");
                }
                else
                {
                    RenderMeasurement();
                    string? caption = MeasurementCaption();
                    if (caption is not null) SetStatus($"Measured {caption}");
                }
                e.Handled = true;
                return;
            }

            if (!_isDrawing) return;
            _isDrawing = false;
            _annotationCanvas.ReleaseMouseCapture();

            switch (_currentTool)
            {
                case EditTool.Highlight when _activePreview is Rectangle rect:
                    if (rect.Width > 3 && rect.Height > 3)
                    {
                        var ha = new HighlightAnnotation
                        {
                            PageIndex = pageIdx,
                            Bounds = new Rect(Canvas.GetLeft(rect), Canvas.GetTop(rect), rect.Width, rect.Height)
                        };
                        ha.SetColor(_highlightColor);
                        AddAnnotation(ha);
                    }
                    else
                    {
                        _annotationCanvas.Children.Remove(rect);
                    }
                    break;

                case EditTool.Form when _activePreview is Rectangle frect:
                {
                    // A form field needs to be big enough to click and to hold a glyph. Below that
                    // it is a stray click, and silently creating an unusable 2pt field would be
                    // worse than doing nothing.
                    var placed = new Rect(Canvas.GetLeft(frect), Canvas.GetTop(frect), frect.Width, frect.Height);
                    _annotationCanvas.Children.Remove(frect);
                    if (placed.Width >= 8 && placed.Height >= 8) CreateFormField(pageIdx, placed);
                    else SetStatus("Form: drag a box at least a few millimetres across");
                    break;
                }

                case EditTool.Redact when _activePreview is Rectangle rrect:
                    // 3px, matching the highlighter: below that it is a stray click, not a mark.
                    if (rrect.Width > 3 && rrect.Height > 3)
                    {
                        if (!_redactionMarks.TryGetValue(pageIdx, out var marks))
                            _redactionMarks[pageIdx] = marks = new List<Rect>();
                        marks.Add(new Rect(Canvas.GetLeft(rrect), Canvas.GetTop(rrect), rrect.Width, rrect.Height));
                        RenderAllAnnotations(pageIdx);
                        ShowRedactSettings();
                        SetStatus($"{PendingRedactionCount} area{(PendingRedactionCount == 1 ? "" : "s")} marked — nothing is removed until you press Redact");
                    }
                    else
                    {
                        _annotationCanvas.Children.Remove(rrect);
                    }
                    break;

                case EditTool.Crop when _activePreview is Rectangle rect:
                    if (rect.Width > 5 && rect.Height > 5)
                    {
                        _activeCrop = new CropAnnotation
                        {
                            PageIndex = pageIdx,
                            Bounds = new Rect(Canvas.GetLeft(rect), Canvas.GetTop(rect), rect.Width, rect.Height)
                        };
                        ShowCropPopup();
                        SetStatus("Crop rectangle selected - choose Apply crop, Reset, or Cancel");
                    }
                    else
                    {
                        _annotationCanvas.Children.Remove(rect);
                        _activePreview = null;
                        _activeCrop = null;
                    }
                    break;

                case EditTool.Draw when _activeInk is not null:
                    if (_activeInk.Points.Count > 2)
                    {
                        AddAnnotation(_activeInk);
                    }
                    else
                    {
                        _annotationCanvas.Children.Remove(_activePreview);
                    }
                    _activeInk = null;
                    break;

                case EditTool.Shape when _activePreview is Line lnCommit:
                {
                    double dx = lnCommit.X2 - lnCommit.X1;
                    double dy = lnCommit.Y2 - lnCommit.Y1;
                    if (Math.Sqrt(dx * dx + dy * dy) >= 4)
                    {
                        var sa = new ShapeAnnotation
                        {
                            PageIndex = pageIdx,
                            Kind = ShapeKind.Line,
                            Start = new Point(lnCommit.X1, lnCommit.Y1),
                            End = new Point(lnCommit.X2, lnCommit.Y2),
                            StrokeWidth = _shapeStrokeWidth,
                            HasFill = false
                        };
                        sa.SetStrokeColor(_shapeStrokeColor);
                        sa.SetFillColor(_shapeFillColor);
                        AddAnnotation(sa);
                    }
                    else
                    {
                        _annotationCanvas.Children.Remove(lnCommit);
                    }
                    break;
                }

                case EditTool.Shape when _activePreview is FrameworkElement shapeCommit:
                {
                    double sx = Canvas.GetLeft(shapeCommit);
                    double sy = Canvas.GetTop(shapeCommit);
                    if (shapeCommit.Width >= 4 && shapeCommit.Height >= 4)
                    {
                        var sa = new ShapeAnnotation
                        {
                            PageIndex = pageIdx,
                            Kind = shapeCommit is Ellipse ? ShapeKind.Ellipse : ShapeKind.Rectangle,
                            Start = new Point(sx, sy),
                            End = new Point(sx + shapeCommit.Width, sy + shapeCommit.Height),
                            StrokeWidth = _shapeStrokeWidth,
                            HasFill = _shapeHasFill
                        };
                        sa.SetStrokeColor(_shapeStrokeColor);
                        sa.SetFillColor(_shapeFillColor);
                        AddAnnotation(sa);
                    }
                    else
                    {
                        _annotationCanvas.Children.Remove(shapeCommit);
                    }
                    break;
                }

                case EditTool.Crop when _activePreview is Rectangle cr:
                    if (cr.Width > 10 && cr.Height > 10)
                    {
                        _cropCanvasRect = new Rect(Canvas.GetLeft(cr), Canvas.GetTop(cr), cr.Width, cr.Height);
                        _cropPreviewRect = cr;
                        _activePreview = null; // keep the preview rect visible; don't null it
                        ShowCropConfirmBar();
                        return;
                    }
                    else
                    {
                        _annotationCanvas.Children.Remove(cr);
                        _cropPreviewRect = null;
                    }
                    break;
            }
            _activePreview = null;
        }

        private void ClearCropSelection()
        {
            bool hasCropSelection = _activeCrop is not null || _currentTool == EditTool.Crop;
            if (!hasCropSelection) return;

            if (_activePreview is Rectangle rect)
                _annotationCanvas.Children.Remove(rect);

            if (_activePreview is Rectangle)
                _activePreview = null;
            _activeCrop = null;
            if (_currentTool == EditTool.Crop)
                SetStatus("Crop cleared - drag a new crop rectangle");
        }

        private async void ApplyCrop_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null)
            {
                TdpDialog.Show(this, "Open a PDF first.");
                return;
            }

            if (_activeCrop is null)
            {
                TdpDialog.Show(this, "Drag a crop rectangle first.", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int pageIdx = _activeCrop.PageIndex;
            if (pageIdx < 0 || pageIdx >= _doc.PageCount || !_renderDims.ContainsKey(pageIdx))
            {
                TdpDialog.Show(this, "The selected crop page is no longer available.", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
                ClearCropSelection();
                return;
            }

            string sourcePath = _currentFile;
            int selectedIdx = PageList.SelectedIndex;
            bool applyToAll = _cropApplyAllCheck?.IsChecked == true;

            _openCancellationTokenSource?.Cancel();
            _renderCancellationTokenSource?.Cancel();
            _openCancellationTokenSource?.Dispose();
            _openCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _openCancellationTokenSource.Token;

            SetFileOperationBusy(true, applyToAll ? "Applying crop to all pages..." : $"Applying crop to page {pageIdx + 1}...");
            try
            {
                CommitActiveTextBox();
                var cropRect = CanvasRectToPdfCropRect(pageIdx, _activeCrop.Bounds);
                _doc.Close();
                _doc = null;

                string croppedPath = await Task.Run(() => CropService.Apply(sourcePath, pageIdx, cropRect, applyToAll), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var result = await OpenFileCoreAsync(croppedPath, null, cancellationToken);
                // Same document, reloaded from the crop working file — keep the tab's name, save
                // target, untitled/protected state and recents entry pointing at the user's file.
                await FinishOpenFileAsync(result, cancellationToken, internalReload: true);
                if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                    PageList.SelectedIndex = selectedIdx;
                else if (PageList.Items.Count > 0)
                    PageList.SelectedIndex = 0;
                ClearCropSelection();
                MarkDirty();
                SetStatus(applyToAll ? "Crop applied to all pages" : $"Crop applied to page {pageIdx + 1}");
            }
            catch (OperationCanceledException)
            {
                SetStatus("Crop canceled");
            }
            catch (Exception ex)
            {
                try
                {
                    if (_doc is null && System.IO.File.Exists(sourcePath))
                    {
                        var restoreResult = await OpenFileCoreAsync(sourcePath, null, CancellationToken.None);
                        await FinishOpenFileAsync(restoreResult, CancellationToken.None, internalReload: true);
                        if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                            PageList.SelectedIndex = selectedIdx;
                    }
                }
                catch { }
                SetFileOperationBusy(false);
                TdpDialog.Show(this, $"Crop failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetFileOperationBusy(false);
            }
        }

        /// <summary>
        /// Converts a rectangle on the annotation canvas into the absolute PDF user-space rectangle
        /// (lower-left origin, positive extents) that <see cref="CropService"/> writes as the page's
        /// /CropBox. The exact inverse of <see cref="PdfRectToCanvas"/>, including the page /Rotate.
        /// </summary>
        /// <remarks>
        /// This used to map as if /Rotate were always 0, so cropping a quarter-turned page cropped a
        /// different region than the one dragged (on a 90-rotated page the drag axes are swapped
        /// relative to user space, so a top-left drag cropped the bottom-left of the sheet).
        ///
        /// The RESULT stays unrotated user space, which is the right contract and needs no change:
        /// PDF 32000-1 7.7.3.3 defines /CropBox — like /MediaBox — in default user space, and /Rotate
        /// is applied by the viewer AFTER the box has been selected. So the page keeps its existing
        /// /Rotate untouched and the crop still lands where the user dragged.
        ///
        /// Inverting PdfRectToCanvas point by point, with fx = rx - box.X and fy = ry - box.Y:
        ///     0 : fx = cx*hs,                fy = box.Height - cy*vs
        ///    90 : fy = cx*hs,                fx = cy*vs
        ///   180 : fx = box.Width  - cx*hs,   fy = cy*vs
        ///   270 : fy = box.Height - cx*hs,   fx = box.Width - cy*vs
        /// where hs/vs are points-per-canvas-pixel on each canvas axis. For 90/270 the bitmap's axes
        /// are swapped — canvas width spans the box's HEIGHT — so the two scales swap with them.
        /// </remarks>
        private Rect CanvasRectToPdfCropRect(int pageIdx, Rect canvasBounds)
        {
            var (renderW, renderH) = _renderDims[pageIdx];
            var page = _doc!.Pages[pageIdx];
            var box = GetVisiblePageBox(page);
            int rot = PdfPageGeometry.Rotation(page);
            bool quarterTurn = rot is 90 or 270;

            double hs = (quarterTurn ? box.Height : box.Width) / renderW;
            double vs = (quarterTurn ? box.Width : box.Height) / renderH;

            // Map both canvas corners; which PDF edge each one becomes depends on the angle, so
            // normalize with min/max at the end rather than assuming an ordering.
            double fxA, fxB, fyA, fyB;
            switch (rot)
            {
                case 90:
                    fyA = canvasBounds.Left * hs;
                    fyB = canvasBounds.Right * hs;
                    fxA = canvasBounds.Top * vs;
                    fxB = canvasBounds.Bottom * vs;
                    break;
                case 180:
                    fxA = box.Width - canvasBounds.Left * hs;
                    fxB = box.Width - canvasBounds.Right * hs;
                    fyA = canvasBounds.Top * vs;
                    fyB = canvasBounds.Bottom * vs;
                    break;
                case 270:
                    fyA = box.Height - canvasBounds.Left * hs;
                    fyB = box.Height - canvasBounds.Right * hs;
                    fxA = box.Width - canvasBounds.Top * vs;
                    fxB = box.Width - canvasBounds.Bottom * vs;
                    break;
                default:
                    fxA = canvasBounds.Left * hs;
                    fxB = canvasBounds.Right * hs;
                    fyA = box.Height - canvasBounds.Top * vs;
                    fyB = box.Height - canvasBounds.Bottom * vs;
                    break;
            }

            double left = box.X + Math.Min(fxA, fxB);
            double right = box.X + Math.Max(fxA, fxB);
            double bottom = box.Y + Math.Min(fyA, fyB);
            double top = box.Y + Math.Max(fyA, fyB);
            return new Rect(left, bottom, right - left, top - bottom);
        }
    }
}
