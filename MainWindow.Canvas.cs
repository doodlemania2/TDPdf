// MainWindow — canvas interaction.
// The pointer/mouse handling for the page canvas: panning, tool gestures, annotation
// hit-testing, drag and resize. Extracted verbatim from MainWindow.xaml.cs.

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
        // Canvas interaction
        // ============================================================

        private void Canvas_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Middle-mouse: modal pan in any tool. Start panning and swallow the event so
            // other handlers (Canvas_MouseLeftButtonDown, AnnotationCanvas children) don't run.
            if (_doc is null) return;
            if (e.ChangedButton == MouseButton.Right) { SelectMarkForContextMenu(e); return; }
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
            // Electronic Signature field placement owns the pointer while it lasts — ahead of form
            // fields, links and every tool (MainWindow.ESignatureFields.cs).
            if (_eSignPlacement is not null) { ESignPlacementMouseDown(e); return; }
            // Don't intercept clicks on form-field overlay controls (TextBox, checkbox, etc.)
            // — WPF must handle those natively so focus, toggling, and text entry work.
            // While a signature is being carried the click PLACES it, even over a fill-in field (the
            // field sits beneath the annotation layer): Handled keeps the field from also taking
            // the click, and PlaceSignature spends the pending signature first thing.
            if (e.OriginalSource is DependencyObject formSrc && IsFormFieldElement(formSrc))
            {
                if (_pendingSignature is null || _currentTool != EditTool.Signature)
                    return;
            }
            else if (Keyboard.FocusedElement is TextBox focusedField && IsFormFieldElement(focusedField))
            {
                // A click anywhere outside a form field lets go of the one being typed in.
                ReleaseFormFocus();
            }
            // If a middle-mouse pan started before WPF routed the left-button event, swallow it.
            if (_isPanning) { e.Handled = true; return; }
            // Don't intercept clicks on an active text editing box
            if (_activeTextBox is not null && e.OriginalSource is DependencyObject src &&
                IsDescendantOf(src, _activeTextBox))
                return;
            // Check if click lands inside a PDF link overlay.
            if (TryFollowLinkOverlay(e)) return;
            var pos = e.GetPosition(_annotationCanvas);
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;

            // A press on one of the resize handles starts that resize, whatever the tool.
            if (TryBeginHandleResize(e, pos)) return;

            switch (_currentTool)
            {
                case EditTool.Select:
                    MouseDownSelect(e, pos, pageIdx);
                    break;
                case EditTool.Text:
                    MouseDownText(e, pos, pageIdx);
                    break;
                case EditTool.EditText:
                    MouseDownEditText(e, pos, pageIdx);
                    break;
                case EditTool.EditImage:
                    MouseDownEditImage(e, pos, pageIdx);
                    break;
                case EditTool.Highlight:
                case EditTool.Strikethrough:
                case EditTool.Underline:
                    MouseDownMarkup(e, pos, pageIdx);
                    break;
                case EditTool.Form:
                    MouseDownForm(e, pos, pageIdx);
                    break;
                case EditTool.Redact:
                    MouseDownRedact(e, pos, pageIdx);
                    break;
                case EditTool.Measure:
                    MouseDownMeasure(e, pos, pageIdx);
                    break;
                case EditTool.Crop:
                    MouseDownCrop(e, pos, pageIdx);
                    break;
                case EditTool.Draw:
                    MouseDownDraw(e, pos, pageIdx);
                    break;
                case EditTool.Signature:
                    MouseDownSignature(e, pos, pageIdx);
                    break;
                case EditTool.Image:
                    MouseDownImage(e, pos, pageIdx);
                    break;
                case EditTool.Pan:
                    MouseDownPan(e, pos, pageIdx);
                    break;
                case EditTool.Erase:
                    MouseDownErase(e, pos, pageIdx);
                    break;
                case EditTool.Shape:
                    MouseDownShape(e, pos, pageIdx);
                    break;
                case EditTool.Stamp:
                    MouseDownStamp(e, pos, pageIdx);
                    break;
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

        /// <summary>
        /// Captured geometry of an <see cref="InkAnnotation"/>: its points and its stroke width at the
        /// start of the gesture. A resize scales the width as well as the points, and has to scale
        /// the ORIGINAL width — scaling the live one compounded on every mouse move, so a stroke got
        /// thicker the longer it was dragged.
        /// </summary>
        private sealed class InkGeom
        {
            public List<Point> Points = new();
            public double StrokeWidth;
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
            InkAnnotation i => new InkGeom { Points = new List<Point>(i.Points), StrokeWidth = i.StrokeWidth },
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
                case InkAnnotation ink when original is InkGeom ig:
                    if (ink.Points.Count != ig.Points.Count) return false;
                    for (int i = 0; i < ig.Points.Count; i++)
                        if (ink.Points[i] != ig.Points[i]) return false;
                    return ink.StrokeWidth == ig.StrokeWidth;
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
                case InkAnnotation ink when original is InkGeom ig:
                    ink.Points.Clear();
                    foreach (var p in ig.Points) ink.Points.Add(new Point(p.X + dx, p.Y + dy));
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
                case InkAnnotation ink when original is InkGeom ig:
                {
                    var pts = ig.Points;
                    if (pts.Count == 0) break;
                    // A dot has no extent to stretch: every scale factor below would be the 4px
                    // floor over the 1px clamp, and the stroke width would be multiplied by it on
                    // every mouse move. Its size is the Draw bar's Size slider instead.
                    if (InkDot.TryGetDot(pts.Select(p => (p.X, p.Y)), out _, out _)) break;
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
                    ink.StrokeWidth = Math.Max(0.5, ig.StrokeWidth * uniform);
                    break;
                }
                case TextAnnotation t when original is ValueTuple<Point, double, double> tp:
                {
                    // Anchor top-left; drag bottom-right to set the wrap Width and box Height.
                    if (t.Rotation == 0)
                    {
                        t.Width = Math.Max(32, tp.Item2 + (cur.X - start.X));
                        t.Height = Math.Max(t.FontSize + 6, tp.Item3 + (cur.Y - start.Y));
                        break;
                    }
                    // Turned box: the handle sits on the TURNED box's bottom-right corner, so the drag
                    // is applied to its on-screen size, converted back to the box's own width and
                    // height, and the on-screen top-left is kept where it was.
                    var (ovx, ovy, ovw, ovh) = AnnotationFrame.VisualRect(
                        tp.Item1.X, tp.Item1.Y, tp.Item2, tp.Item3, t.Rotation);
                    double nvw = ovw + (cur.X - start.X), nvh = ovh + (cur.Y - start.Y);
                    bool tq = AnnotationFrame.IsQuarterTurn(t.Rotation);
                    t.Width = Math.Max(32, tq ? nvh : nvw);
                    t.Height = Math.Max(t.FontSize + 6, tq ? nvw : nvh);
                    var (tnx, tny) = AnnotationFrame.PositionFromVisualTopLeft(ovx, ovy, t.Width, t.Height, t.Rotation);
                    t.Position = new Point(tnx, tny);
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
            if (_eSignPlacement is { Dragging: true }) { ESignPlacementMouseMove(e); return; }
            if (_pendingSignature is not null) UpdateSignatureGhost(e.GetPosition(_annotationCanvas));
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
            if ((_currentTool == EditTool.Select || IsTextMarkupTool(_currentTool)) &&
                e.LeftButton == MouseButtonState.Released && !IsPointerOperationActive)
                UpdateSelectHoverCursor(pos, overLink && _currentTool == EditTool.Select);

            // Shapes tool, freeform polygon: track the rubber band from the last placed vertex and
            // light the first-vertex snap ring. No button is held during placement, so this runs
            // ahead of every drag path below (and after the pan check, which owns middle-drag).
            if (_currentTool == EditTool.Shape && _polyVertices.Count > 0)
            {
                UpdateShapePolyRubber(pos);
                return;
            }

            // Several marks dragged together
            if (_isMovingMulti)
            {
                // Capture can be lost without a MouseUp ever arriving: settle the drag instead of
                // letting the marks keep following a button that is not held.
                if (e.LeftButton != MouseButtonState.Pressed) FinishMultiMove();
                else MultiMoveTo(pos);
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
                // A quarter-turned item is dragged along its VISUAL axes: its on-screen width is the
                // source height, so the drag is measured against that.
                bool sigQuarter = AnnotationFrame.IsQuarterTurn(_resizeSigAnnot.Rotation);
                double dragSrcW = sigQuarter && IsFinitePositive(_resizeSigAnnot.SourceHeight)
                    ? _resizeSigAnnot.SourceHeight : srcW;
                double newScale = Math.Max(0.05, startScale + delta / dragSrcW);
                _resizeSigAnnot.Scale = newScale;

                // Update selection border and handle position live. SourceHeight gets the same
                // treatment as SourceWidth above: newW/newH feed the border's Width/Height, which WPF
                // rejects outright when either is not a real number.
                double srcH = IsFinitePositive(_resizeSigAnnot.SourceHeight)
                    ? _resizeSigAnnot.SourceHeight : DefaultSigCanvasH;
                double newW = srcW * newScale;
                double newH = srcH * newScale;
                // The visual top-left stays put while the item grows; a turned item's unrotated
                // position is worked back from it. The border and handle follow the TURNED box.
                var (visW, visH) = AnnotationFrame.VisualSize(newW, newH, _resizeSigAnnot.Rotation);
                if (_resizeSigAnnot.Rotation != 0)
                {
                    var (npx, npy) = AnnotationFrame.PositionFromVisualTopLeft(
                        _resizeSigVisualTopLeft.X, _resizeSigVisualTopLeft.Y, newW, newH, _resizeSigAnnot.Rotation);
                    _resizeSigAnnot.Position = new Point(npx, npy);
                }
                if (_selectionBorder is not null)
                {
                    _selectionBorder.Width  = visW + 8;
                    _selectionBorder.Height = visH + 8;
                }
                if (_resizeHandle is not null)
                {
                    double hx = (_resizeSigAnnot.Rotation == 0 ? _resizeSigAnnot.Position.X : _resizeSigVisualTopLeft.X) + visW - 4 - _resizeHandle.Width / 2;
                    double hy = (_resizeSigAnnot.Rotation == 0 ? _resizeSigAnnot.Position.Y : _resizeSigVisualTopLeft.Y) + visH - 4 - _resizeHandle.Height / 2;
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
                var dragBox = PlacedVisualRect(_dragAnnot);   // the turned box, which is what is selected
                if (_selectionBorder is not null)
                {
                    Canvas.SetLeft(_selectionBorder, dragBox.X - 4);
                    Canvas.SetTop(_selectionBorder, dragBox.Y - 4);
                }
                if (_resizeHandle is not null)
                {
                    Canvas.SetLeft(_resizeHandle, dragBox.Right - 4 - _resizeHandle.Width / 2);
                    Canvas.SetTop(_resizeHandle, dragBox.Bottom - 4 - _resizeHandle.Height / 2);
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

            if (_pictureDrag != PictureDrag.None)
            {
                PictureDragMove(pos);
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

            if (_eSignPlacement is { Dragging: true }) { ESignPlacementMouseUp(e); return; }
            if (_eSignPlacement is not null) { e.Handled = true; return; }

            int pageIdx = PageList.SelectedIndex;

            // Finish moving or resizing a picture the PDF carries (#244).
            if (_pictureDrag != PictureDrag.None)
            {
                PictureDragEnd(e.GetPosition(_annotationCanvas));
                e.Handled = true;
                return;
            }

            // Finish dragging several marks together
            if (_isMovingMulti)
            {
                FinishMultiMove();
                e.Handled = true;
                return;
            }

            // Finish generic annotation move
            if (_isMovingAnnot)
            {
                FinishAnnotMove();
                return;
            }

            // Finish generic annotation resize
            if (_isResizingAnnot)
            {
                FinishAnnotResize();
                return;
            }

            // Finish annotation drag-to-move
            if (_isDraggingAnnot)
            {
                FinishAnnotDrag();
                return;
            }

            // Finish signature resize
            if (_isResizingSig)
            {
                FinishSignatureResize();
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
                FinishMarquee(e, pageIdx);
                return;
            }

            if (_isResizingImage && _resizingImageEdit is not null)
            {
                FinishImageResize();
                return;
            }

            // Finish a measurement. The ruler stays on the page after the button comes up — the
            // whole point is to be able to read it — and is replaced by the next drag, cleared by
            // a page change, and taken away with the tool (Esc included).
            if (_isMeasuring)
            {
                FinishMeasurement(e);
                return;
            }

            if (!_isDrawing) return;
            _isDrawing = false;
            _annotationCanvas.ReleaseMouseCapture();

            switch (_currentTool)
            {
                case EditTool.Highlight when _activePreview is Rectangle rect:
                    CommitHighlightDrag(rect, pageIdx);
                    break;

                case EditTool.Form when _activePreview is Rectangle frect:
                    CommitFormDrag(frect, pageIdx);
                    break;

                case EditTool.Redact when _activePreview is Rectangle rrect:
                    CommitRedactDrag(rrect, pageIdx);
                    break;

                case EditTool.Crop when _activePreview is Rectangle rect:
                    CommitCropDrag(rect, pageIdx);
                    break;

                case EditTool.Draw when _activeInk is not null:
                    CommitInkStroke(_activeInk);
                    break;

                case EditTool.Shape when _activePreview is Line lnCommit:
                    CommitLineShape(lnCommit, pageIdx);
                    break;

                case EditTool.Shape when _activePreview is FrameworkElement shapeCommit:
                    CommitBoxShape(shapeCommit, pageIdx);
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
                SetStatus("Crop cleared. Drag a new crop rectangle");
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
                UpdateRemoveCropEnabled();
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
