// MainWindow — one handler per edit tool for the page canvas.
// Canvas_MouseLeftButtonDown and Canvas_MouseLeftButtonUp (MainWindow.Canvas.cs) decide whether a
// press or release is theirs to handle and then dispatch on the current tool to the methods below.
// The bodies are the old switch cases, moved without change: a `break` that left the switch is a
// `return` here, and the order the dispatcher tries things in is unchanged.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        // ============================================================
        // Left-button press, per tool
        // ============================================================

        /// <summary>Select tool press: double-click re-edits text; otherwise picks up a mark, starts a text selection or opens a marquee.</summary>
        private void MouseDownSelect(MouseButtonEventArgs e, Point pos, int pageIdx)
        {
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
                bool addToSelection = (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0;

                // Shift+click / Ctrl+click on a mark adds it to the selection or takes it out
                // (#228). Decided before the text-selection check so a modified click on a
                // highlight that covers text still toggles the highlight.
                if (underPress is not null && addToSelection && !_ocrRegionMode)
                {
                    ToggleMarkSelection(underPress);
                    e.Handled = true;
                    return;
                }

                bool textMayWin = underPress is null or HighlightAnnotation;
                if (!marqueeForced && textMayWin && TryBeginTextSelection(pageIdx, pos))
                {
                    ClearSelection();
                    RenderAllAnnotations(pageIdx);
                    _txtSelClickAnnot = underPress;
                    _txtSelClickAnnotBounds = underPressBounds;
                    _annotationCanvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }

                // A press on a mark that is already part of a multi-selection drags the whole
                // selection as one undo step.
                if (underPress is not null && IsMultiSelection && _markSelection.Contains(underPress))
                {
                    BeginMultiMove(pos, underPress);
                    e.Handled = true;
                    return;
                }

                // Any member of a group picks up the whole group, and drags it.
                if (underPress?.GroupId is not null)
                {
                    var members = ExpandToGroups(pageIdx, new[] { underPress });
                    if (members.Count > 1)
                    {
                        ClearSelection();
                        RenderAllAnnotations(pageIdx);
                        SelectMarks(members);
                        BeginMultiMove(pos, underPress);
                        e.Handled = true;
                        return;
                    }
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
                    // Shift / Ctrl keep the current selection under the box: what the box
                    // touches is added to it on release (see the marquee release below).
                    _marqueeBase = addToSelection && !_ocrRegionMode
                        ? new List<PageAnnotation>(_markSelection) : null;
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
        }

        /// <summary>Text tool press: gets back into a box already being typed in, or a placed one, or places a new box.</summary>
        private void MouseDownText(MouseButtonEventArgs e, Point pos, int pageIdx)
        {
            if (TryRestoreActiveTextBoxFocus(pos))
            {
                e.Handled = true;
                return;
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
        }

        /// <summary>Edit Existing Text press: TDPdf's own text boxes first, then the PDF's own text.</summary>
        private void MouseDownEditText(MouseButtonEventArgs e, Point pos, int pageIdx)
        {
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
        }

        /// <summary>Edit Existing Image press.</summary>
        private void MouseDownEditImage(MouseButtonEventArgs e, Point pos, int pageIdx)
        {
            CommitActiveTextBox();
            EditImageAtPosition(pos, pageIdx);
            e.Handled = true;
        }

        /// <summary>Highlight, Strikethrough and Underline press: flow along the text, or (highlight only) drag a rectangle.</summary>
        private void MouseDownMarkup(MouseButtonEventArgs e, Point pos, int pageIdx)
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
                return;
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
                return;
            }
            // Non-blocking explanation, only when the whole page has no text layer; missing
            // the words on a page that does have text is self-evident and stays silent.
            if (!hasText) SetStatus(NoTextLayerHighlightHint);
            _isDrawing = true;
            _drawStart = pos;
            var rect = new Rectangle
            {
                Fill = TdpDialog.FrozenSolidColorBrush(_highlightColor),
                Width = 0, Height = 0
            };
            Canvas.SetLeft(rect, pos.X);
            Canvas.SetTop(rect, pos.Y);
            _annotationCanvas.Children.Add(rect);
            _activePreview = rect;
            _annotationCanvas.CaptureMouse();
        }

        /// <summary>Form tool press: select the field under the pointer, or start dragging a new one out.</summary>
        private void MouseDownForm(MouseButtonEventArgs e, Point pos, int pageIdx)
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
                return;
            }
            if (_selectedFormWidget is not null) SelectFormWidget(null);
            _isDrawing = true;
            _drawStart = pos;
            var formRect = new Rectangle
            {
                Fill = AccentWash(40),
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
        }

        /// <summary>Redact tool press: take back the mark under the pointer, or start a new one.</summary>
        private void MouseDownRedact(MouseButtonEventArgs e, Point pos, int pageIdx)
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
                    SetStatus($"Redaction mark removed, {PendingRedactionCount} pending");
                    e.Handled = true;
                    return;
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
        }

        /// <summary>Measure tool press: a new drag replaces the ruler.</summary>
        private void MouseDownMeasure(MouseButtonEventArgs e, Point pos, int pageIdx)
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
        }

        /// <summary>Crop tool press: start dragging the crop rectangle.</summary>
        private void MouseDownCrop(MouseButtonEventArgs e, Point pos, int pageIdx)
        {
            ClearSelection();
            ClearCropSelection();
            _isDrawing = true;
            _drawStart = pos;
            var cropRect = new Rectangle
            {
                Fill = AccentWash(35),
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
        }

        /// <summary>Draw tool press: start an ink stroke.</summary>
        private void MouseDownDraw(MouseButtonEventArgs e, Point pos, int pageIdx)
        {
            ClearSelection();
            _isDrawing = true;
            _activeInk = new InkAnnotation { PageIndex = pageIdx, StrokeWidth = _drawWidth };
            _activeInk.SetColor(_drawColor);
            _activeInk.Points.Add(pos);
            var poly = new Polyline
            {
                Stroke = TdpDialog.FrozenSolidColorBrush(_drawColor),
                StrokeThickness = _drawWidth,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            poly.Points.Add(pos);
            _annotationCanvas.Children.Add(poly);
            _activePreview = poly;
            _annotationCanvas.CaptureMouse();
        }

        /// <summary>Signature tool press: place the signature being carried, or open the signature popup.</summary>
        private void MouseDownSignature(MouseButtonEventArgs e, Point pos, int pageIdx)
        {
            if (_pendingSignature is not null)
            {
                PlaceSignature(pos, pageIdx);
                e.Handled = true;
            }
            else
            {
                ShowSignaturePopup();
            }
        }

        /// <summary>Image tool press: place an image from the file dialog.</summary>
        private void MouseDownImage(MouseButtonEventArgs e, Point pos, int pageIdx)
        {
            PlaceImageFromDialog(pos, pageIdx);
            e.Handled = true;
        }

        /// <summary>Pan tool press.</summary>
        private void MouseDownPan(MouseButtonEventArgs e, Point pos, int pageIdx)
        {
            StartPan(e, MouseButton.Left);
            e.Handled = true;
        }

        /// <summary>Erase tool press: removes the topmost annotation under the pointer.</summary>
        private void MouseDownErase(MouseButtonEventArgs e, Point pos, int pageIdx)
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
        }

        /// <summary>Shape tool press: polygon vertices go down click by click; the other kinds start a drag.</summary>
        private void MouseDownShape(MouseButtonEventArgs e, Point pos, int pageIdx)
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
                return;
            }

            ClearSelection();
            _isDrawing = true;
            _drawStart = pos;
            Shape preview = _shapeKind switch
            {
                ShapeKind.Rectangle => new Rectangle
                {
                    Stroke = TdpDialog.FrozenSolidColorBrush(_shapeStrokeColor),
                    StrokeThickness = _shapeStrokeWidth,
                    Fill = _shapeHasFill
                        ? TdpDialog.FrozenSolidColorBrush(_shapeFillColor)
                        : (Brush)Brushes.Transparent,
                    Width = 0, Height = 0,
                    IsHitTestVisible = false
                },
                ShapeKind.Ellipse => new Ellipse
                {
                    Stroke = TdpDialog.FrozenSolidColorBrush(_shapeStrokeColor),
                    StrokeThickness = _shapeStrokeWidth,
                    Fill = _shapeHasFill
                        ? TdpDialog.FrozenSolidColorBrush(_shapeFillColor)
                        : (Brush)Brushes.Transparent,
                    Width = 0, Height = 0,
                    IsHitTestVisible = false
                },
                ShapeKind.Line => new Line
                {
                    Stroke = TdpDialog.FrozenSolidColorBrush(_shapeStrokeColor),
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
        }

        // ============================================================
        // Left-button press, before the tool is consulted
        // ============================================================

        /// <summary>
        /// A press that lands inside a PDF link overlay follows the link. We do an explicit bounds check rather than relying on WPF hit-testing through nested transparent canvases, which is unreliable.
        /// </summary>
        private bool TryFollowLinkOverlay(MouseButtonEventArgs e)
        {
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
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// A press on a resize handle (replaced image, signature or picture, or the generic handle on
        /// a shape, highlight or ink stroke) starts that resize. True when the press was one.
        /// </summary>
        private bool TryBeginHandleResize(MouseButtonEventArgs e, Point pos)
        {
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
                return true;
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
                    var rsaVisual = PlacedVisualRect(rsa);
                    _resizeSigVisualTopLeft = new Point(rsaVisual.X, rsaVisual.Y);
                    _annotationCanvas.CaptureMouse();
                    e.Handled = true;
                    return true;
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
                    return true;
                }
            }
            return false;
        }

        // ============================================================
        // Left-button release, per gesture
        // ============================================================

        /// <summary>Releases a generic annotation move: keeps it if it went anywhere, otherwise drops the undo snapshot.</summary>
        private void FinishAnnotMove()
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
        }

        /// <summary>Releases a generic annotation resize.</summary>
        private void FinishAnnotResize()
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
        }

        /// <summary>Releases a drag of a signature or picture.</summary>
        private void FinishAnnotDrag()
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
                SelectAnnotation(da, PlacedVisualRect(da));
            }
        }

        /// <summary>Releases a signature or picture resize.</summary>
        private void FinishSignatureResize()
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
                SelectAnnotation(sa, PlacedVisualRect(sa));
                MarkDirty();
            }
        }

        /// <summary>Releases the select tool's marquee: a click selects the mark under it, a drag selects marks or extracts text (or OCRs the region).</summary>
        private void FinishMarquee(MouseButtonEventArgs e, int pageIdx)
        {
            _isSelecting = false;
            _annotationCanvas.ReleaseMouseCapture();
            var pos = e.GetPosition(_annotationCanvas);
            double dragW = Math.Abs(pos.X - _selectStart.X);
            double dragH = Math.Abs(pos.Y - _selectStart.Y);

            // A pending "OCR Region" arm consumes this drag regardless of outcome.
            bool ocrRegion = _ocrRegionMode;
            _ocrRegionMode = false;

            var marqueeBase = _marqueeBase;
            _marqueeBase = null;

            if (dragW < 5 && dragH < 5)
            {
                // Tiny drag = single click -> try annotation selection
                ClearTextSelection();
                if (marqueeBase is { Count: > 0 })
                {
                    // A Shift/Ctrl click on empty page changes nothing: put the selection back.
                    RenderAllAnnotations(marqueeBase[0].PageIndex);
                    SelectMarks(marqueeBase);
                }
                else if (pageIdx >= 0 && _annotations.ContainsKey(pageIdx))
                {
                    for (int i = _annotations[pageIdx].Count - 1; i >= 0; i--)
                    {
                        if (HitTestAnnotation(_annotations[pageIdx][i], _selectStart, out Rect bounds))
                        {
                            if (_annotations[pageIdx][i].GroupId is not null)
                                SelectMarks(ExpandToGroups(pageIdx, new[] { _annotations[pageIdx][i] }));
                            else
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
                if (ocrRegion) ObserveOcr(OcrRegion(pageIdx, selectBounds));
                else if (TrySelectMarksInBox(pageIdx, selectBounds, marqueeBase)) { }
                else
                {
                    if (marqueeBase is { Count: > 0 })
                    {
                        RenderAllAnnotations(marqueeBase[0].PageIndex);
                        SelectMarks(marqueeBase);
                    }
                    ExtractTextFromRegion(pageIdx, selectBounds);
                }
            }
        }

        /// <summary>Releases a replaced-image resize.</summary>
        private void FinishImageResize()
        {
            _isResizingImage = false;
            _annotationCanvas.ReleaseMouseCapture();
            var resizing = _resizingImageEdit;
            if (resizing is null) return;   // the caller only gets here with one
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
            SetStatus("Image resize committed. Save to apply white-out + overdraw");
            _resizingImageEdit = null;
        }

        /// <summary>Releases the Measure tool: the ruler stays on the page unless the drag never moved.</summary>
        private void FinishMeasurement(MouseButtonEventArgs e)
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
        }

        /// <summary>Highlight tool release: a rectangle bigger than a stray click becomes a highlight.</summary>
        private void CommitHighlightDrag(Rectangle rect, int pageIdx)
        {
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
        }

        /// <summary>Form tool release: a box big enough to click becomes a form field.</summary>
        private void CommitFormDrag(Rectangle frect, int pageIdx)
        {
            // A form field needs to be big enough to click and to hold a glyph. Below that
            // it is a stray click, and silently creating an unusable 2pt field would be
            // worse than doing nothing.
            var placed = new Rect(Canvas.GetLeft(frect), Canvas.GetTop(frect), frect.Width, frect.Height);
            _annotationCanvas.Children.Remove(frect);
            if (placed.Width >= 8 && placed.Height >= 8) CreateFormField(pageIdx, placed);
            else SetStatus("Form: drag a box at least a few millimetres across");
        }

        /// <summary>Redact tool release: a rectangle bigger than a stray click becomes a pending redaction mark.</summary>
        private void CommitRedactDrag(Rectangle rrect, int pageIdx)
        {
            // 3px, matching the highlighter: below that it is a stray click, not a mark.
            if (rrect.Width > 3 && rrect.Height > 3)
            {
                if (!_redactionMarks.TryGetValue(pageIdx, out var marks))
                    _redactionMarks[pageIdx] = marks = new List<Rect>();
                marks.Add(new Rect(Canvas.GetLeft(rrect), Canvas.GetTop(rrect), rrect.Width, rrect.Height));
                RenderAllAnnotations(pageIdx);
                ShowRedactSettings();
                SetStatus($"{PendingRedactionCount} area{(PendingRedactionCount == 1 ? "" : "s")} marked. Nothing is removed until you press Redact");
            }
            else
            {
                _annotationCanvas.Children.Remove(rrect);
            }
        }

        /// <summary>Crop tool release: a rectangle bigger than a stray click becomes the crop selection.</summary>
        private void CommitCropDrag(Rectangle rect, int pageIdx)
        {
            if (rect.Width > 5 && rect.Height > 5)
            {
                _activeCrop = new CropAnnotation
                {
                    PageIndex = pageIdx,
                    Bounds = new Rect(Canvas.GetLeft(rect), Canvas.GetTop(rect), rect.Width, rect.Height)
                };
                ShowCropPopup();
                SetStatus("Crop rectangle selected. Choose Apply crop, Reset, or Cancel");
            }
            else
            {
                _annotationCanvas.Children.Remove(rect);
                _activePreview = null;
                _activeCrop = null;
            }
        }

        /// <summary>Draw tool release: the stroke is kept, down to a single click.</summary>
        private void CommitInkStroke(InkAnnotation ink)
        {
            // Every stroke is kept, down to a single click (upstream KillerPDF v1.8.72). This
            // used to discard anything of two points or fewer, which swallowed a deliberate
            // dot — and a quick flick that only ever reported one move. A stroke that never
            // left InkDot.MaxExtent becomes a round dot; the preview polyline cannot show one
            // (WPF draws nothing for a single point), so it is swapped for the dot itself.
            if (ink.Points.Count > 0)
            {
                AddAnnotation(ink);
                if (MakeInkDotVisual(ink) is { } dot)
                {
                    _annotationCanvas.Children.Remove(_activePreview);
                    _annotationCanvas.Children.Add(dot);
                }
            }
            else
            {
                _annotationCanvas.Children.Remove(_activePreview);
            }
            _activeInk = null;
        }

        /// <summary>Shape tool release for a line.</summary>
        private void CommitLineShape(Line lnCommit, int pageIdx)
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
        }

        /// <summary>Shape tool release for a rectangle or ellipse.</summary>
        private void CommitBoxShape(FrameworkElement shapeCommit, int pageIdx)
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
        }
    }
}
