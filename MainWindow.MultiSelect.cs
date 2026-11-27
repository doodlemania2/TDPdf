// MainWindow — selecting several marks at once (#228).
//
// The page keeps ONE selection set, _markSelection. A set of one is the classic single selection and
// goes through the original SelectAnnotation path untouched (resize handle, style bar, status text),
// so nothing about picking one mark changed. A set of two or more has no resize handle: it draws an
// outline per mark plus a group box, moves together, deletes together, and restyles together.
//
// How the Select tool tells a text drag from a box drag (decided here, documented for the next
// reader):
//   * A press that lands ON a text character starts the flowing text selection, exactly as before.
//   * A press on EMPTY page (no character, no mark) starts the box. Releasing it selects every mark
//     the box touches. Only when the box touches no mark does it fall back to what it always did:
//     copy the text inside the box (or OCR it when the OCR-region tool is armed).
//   * Holding Shift or Ctrl while dragging the box ADDS to the existing selection instead of
//     replacing it. Shift and Ctrl also toggle a single mark in or out with a plain click.
//   * A press on a mark never starts a box, so moving a mark is unchanged.

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
        // Selection set
        // ============================================================

        /// <summary>Every selected mark on the current page. Count 1 mirrors <c>_selectedAnnotation</c>.</summary>
        private readonly List<PageAnnotation> _markSelection = new();

        /// <summary>Outline and group-box visuals for a selection of two or more.</summary>
        private readonly List<UIElement> _multiChrome = new();

        private bool HasMarkSelection => _markSelection.Count > 0;
        private bool IsMultiSelection => _markSelection.Count > 1;

        /// <summary>The kinds the Select tool can pick up. Crop rectangles and image edits are not marks.</summary>
        private static bool IsSelectableMark(PageAnnotation a) =>
            a is ShapeAnnotation or HighlightAnnotation or InkAnnotation or TextAnnotation
              or TextEditAnnotation or PlacedAnnotation;

        /// <summary>The box a mark occupies on the canvas, the one its selection outline wraps.</summary>
        private Rect MarkBounds(PageAnnotation a)
        {
            switch (a)
            {
                case MarkupAnnotation mk: return mk.Bounds;
                case HighlightAnnotation ha: return ha.Bounds;
                case TextAnnotation ta: return TextVisualRect(ta);
                case TextEditAnnotation tea: return tea.OriginalBounds;
                case ImageEditAnnotation iea: return iea.TargetBounds;
                case PlacedAnnotation pa: return PlacedVisualRect(pa);
                case ShapeAnnotation shp: return shp.Bounds;
                case InkAnnotation ia when ia.Points.Count > 0:
                {
                    if (InkDot.TryGetDot(ia.Points.Select(p => (p.X, p.Y)), out double dx, out double dy))
                    {
                        double side = Math.Max(ia.StrokeWidth, 4);
                        return new Rect(dx - side / 2, dy - side / 2, side, side);
                    }
                    double minX = ia.Points.Min(p => p.X), minY = ia.Points.Min(p => p.Y);
                    double maxX = ia.Points.Max(p => p.X), maxY = ia.Points.Max(p => p.Y);
                    return new Rect(minX, minY, Math.Max(maxX - minX, 4), Math.Max(maxY - minY, 4));
                }
                default: return Rect.Empty;
            }
        }

        private static bool IsUsableRect(Rect r) =>
            !r.IsEmpty && IsFinite(r.X) && IsFinite(r.Y) && IsFinite(r.Width) && IsFinite(r.Height);

        private Rect UnionOfMarks(IEnumerable<PageAnnotation> marks)
        {
            var u = Rect.Empty;
            foreach (var m in marks)
            {
                var b = MarkBounds(m);
                if (!IsUsableRect(b)) continue;
                u = u.IsEmpty ? b : Rect.Union(u, b);
            }
            return u;
        }

        /// <summary>Every selectable mark on the page whose box the drag box touches, in z-order.</summary>
        private List<PageAnnotation> MarksTouching(int pageIdx, Rect box)
        {
            var hits = new List<PageAnnotation>();
            if (!_annotations.TryGetValue(pageIdx, out var list)) return hits;
            foreach (var a in list)
            {
                if (!IsSelectableMark(a)) continue;
                var b = MarkBounds(a);
                if (IsUsableRect(b) && b.IntersectsWith(box)) hits.Add(a);
            }
            return ExpandToGroups(pageIdx, hits);
        }

        /// <summary>Adds the other members of any group a mark belongs to (none yet: grouping lands later).</summary>
        private List<PageAnnotation> ExpandToGroups(int pageIdx, IEnumerable<PageAnnotation> marks) =>
            marks.ToList();

        /// <summary>The topmost selectable mark under a canvas point: placed marks outrank the rest, as in the click path.</summary>
        private PageAnnotation? TopMarkAt(int pageIdx, Point pos)
        {
            if (!_annotations.TryGetValue(pageIdx, out var list)) return null;
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i] is PlacedAnnotation pa && HitTestAnnotation(pa, pos, out _)) return pa;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var a = list[i];
                if (a is PlacedAnnotation || !IsSelectableMark(a)) continue;
                if (HitTestAnnotation(a, pos, out _)) return a;
            }
            return null;
        }

        /// <summary>
        /// Right-click on a mark selects it first (unless it is already part of the selection) so the
        /// menu's Cut, Copy, Group and Align act on what was clicked.
        /// </summary>
        private void SelectMarkForContextMenu(MouseButtonEventArgs e)
        {
            if (_currentTool != EditTool.Select || IsPointerOperationActive) return;
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;
            var hit = TopMarkAt(pageIdx, e.GetPosition(_annotationCanvas));
            if (hit is null || _markSelection.Contains(hit)) return;
            var members = ExpandToGroups(pageIdx, new[] { hit });
            RenderAllAnnotations(pageIdx);
            SelectMarks(members);
        }

        private MenuItem? _ctxCut, _ctxCopy, _ctxPaste;

        /// <summary>Greys the mark commands in the right-click menu that have nothing to act on.</summary>
        private void UpdateMarkMenuState()
        {
            if (_ctxCut is not null) _ctxCut.IsEnabled = HasMarkSelection;
            if (_ctxPaste is not null) _ctxPaste.IsEnabled = CanPasteNow();
        }

        /// <summary>The selection a Shift/Ctrl box drag adds to; null for a plain box drag.</summary>
        private List<PageAnnotation>? _marqueeBase;

        /// <summary>
        /// Box release in the Select tool: selects every mark the box touches (plus the selection the
        /// drag started from when Shift/Ctrl was held). False when the box touched no mark, in which
        /// case the caller does what a box always did and copies the text inside it.
        /// </summary>
        private bool TrySelectMarksInBox(int pageIdx, Rect box, List<PageAnnotation>? additiveBase)
        {
            var hits = MarksTouching(pageIdx, box);
            if (hits.Count == 0) return false;
            var all = new List<PageAnnotation>();
            if (additiveBase is not null) all.AddRange(additiveBase.Where(a => a.PageIndex == pageIdx));
            foreach (var h in hits) if (!all.Contains(h)) all.Add(h);
            if (_selectRect is not null) { _annotationCanvas.Children.Remove(_selectRect); _selectRect = null; }
            RenderAllAnnotations(pageIdx);
            SelectMarks(all);
            return true;
        }

        /// <summary>Selects exactly these marks (replacing any selection). One mark takes the classic path.</summary>
        private void SelectMarks(IReadOnlyList<PageAnnotation> marks)
        {
            var distinct = new List<PageAnnotation>();
            foreach (var m in marks) if (!distinct.Contains(m)) distinct.Add(m);

            if (distinct.Count == 0) { ClearSelection(); return; }
            if (distinct.Count == 1)
            {
                SelectAnnotation(distinct[0], MarkBounds(distinct[0]));
                return;
            }

            ClearSelection();
            ClearTextSelection();
            _markSelection.AddRange(distinct);
            _selectedAnnotation = null;   // no single selection: handles and the Delete shortcut key off the set

            // The style bar edits every selected mark through ONE of them (the first that has a bar);
            // RestyleLive carries each change across to the rest.
            var rep = distinct.FirstOrDefault(m => m is not PlacedAnnotation);
            if (rep is not null)
            {
                _styleTarget = rep;
                _restylePre = rep.Clone();
                _restyleStepOpen = false;
                _restylePreMulti.Clear();
                foreach (var m in distinct) _restylePreMulti[m] = m.Clone();
                ShowStyleBarForSelection(rep);
            }
            RebuildMultiChrome();
            SetStatus($"{distinct.Count} marks selected. Drag to move, arrow keys to nudge, Delete to remove, Esc to deselect");
        }

        /// <summary>Shift+click / Ctrl+click: adds the mark (and its group) to the selection, or takes it out.</summary>
        private void ToggleMarkSelection(PageAnnotation mark)
        {
            var members = ExpandToGroups(mark.PageIndex, new[] { mark });
            var next = new List<PageAnnotation>(_markSelection);
            bool removing = _markSelection.Contains(mark);
            foreach (var m in members)
            {
                if (removing) next.Remove(m);
                else if (!next.Contains(m)) next.Add(m);
            }
            // Render FIRST: it wipes the canvas, and a single mark's border is added on top of it.
            RenderAllAnnotations(mark.PageIndex);
            SelectMarks(next);
        }

        private void ClearMultiChrome()
        {
            foreach (var el in _multiChrome) _annotationCanvas.Children.Remove(el);
            _multiChrome.Clear();
        }

        /// <summary>Draws an outline per selected mark and the box around all of them.</summary>
        private void RebuildMultiChrome()
        {
            ClearMultiChrome();
            if (_markSelection.Count < 2) return;

            foreach (var m in _markSelection)
            {
                var b = MarkBounds(m);
                if (!IsUsableRect(b)) continue;
                var outline = new Rectangle
                {
                    Width = b.Width + 4, Height = b.Height + 4,
                    Stroke = BrushResource("AccentGreen"),
                    StrokeThickness = 1.5,
                    Fill = AccentWash(20),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(outline, b.X - 2);
                Canvas.SetTop(outline, b.Y - 2);
                _annotationCanvas.Children.Add(outline);
                _multiChrome.Add(outline);
            }

            var u = UnionOfMarks(_markSelection);
            if (IsUsableRect(u))
            {
                var box = new Rectangle
                {
                    Width = u.Width + 12, Height = u.Height + 12,
                    Stroke = BrushResource("AccentGreen"),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(box, u.X - 6);
                Canvas.SetTop(box, u.Y - 6);
                _annotationCanvas.Children.Add(box);
                _multiChrome.Add(box);
            }
        }

        /// <summary>RenderAllAnnotations wipes the canvas; this puts a multi-selection's chrome back.</summary>
        private void RestoreMultiSelectionChrome(int pageIndex)
        {
            if (!IsMultiSelection || _markSelection[0].PageIndex != pageIndex) return;
            RebuildMultiChrome();
        }

        // ============================================================
        // Moving the selection together
        // ============================================================

        private bool _isMovingMulti;
        private Point _multiMoveStart;
        private PageAnnotation? _multiMoveClicked;
        private List<PageAnnotation> _multiMoveOrig = new();
        private List<PageAnnotation> _multiMoveMarks = new();
        private bool _multiMoveMoved;

        /// <summary>One undo step for the whole drag: the page is snapshotted once, here.</summary>
        private void BeginMultiMove(Point pos, PageAnnotation clicked)
        {
            if (_markSelection.Count == 0) return;
            int page = _markSelection[0].PageIndex;
            PushPageSnapshot(page);
            _isMovingMulti = true;
            _multiMoveMoved = false;
            _multiMoveStart = pos;
            _multiMoveClicked = clicked;
            _multiMoveMarks = new List<PageAnnotation>(_markSelection);
            _multiMoveOrig = _multiMoveMarks.Select(m => m.Clone()).ToList();
            _annotationCanvas.CaptureMouse();
        }

        private void MultiMoveTo(Point pos)
        {
            double dx = pos.X - _multiMoveStart.X, dy = pos.Y - _multiMoveStart.Y;
            if (dx == 0 && dy == 0 && !_multiMoveMoved) return;
            _multiMoveMoved = true;
            for (int i = 0; i < _multiMoveMarks.Count; i++)
                TranslateMark(_multiMoveMarks[i], _multiMoveOrig[i], dx, dy);
            RenderAllAnnotations(_multiMoveMarks[0].PageIndex);
            MarkDirty();
        }

        private void FinishMultiMove()
        {
            _isMovingMulti = false;
            if (_annotationCanvas.IsMouseCaptured) _annotationCanvas.ReleaseMouseCapture();
            var marks = _multiMoveMarks;
            var clicked = _multiMoveClicked;
            _multiMoveMarks = new List<PageAnnotation>();
            _multiMoveOrig = new List<PageAnnotation>();
            _multiMoveClicked = null;
            if (marks.Count == 0) return;
            int page = marks[0].PageIndex;
            if (!_multiMoveMoved)
            {
                // A click without a drag on one member of a multi-selection narrows the selection to
                // it, the way every file manager does (a grouped member keeps its whole group).
                DropTopSnapshotIfFor(page);
                if (clicked is not null && HasMarkSelection)
                {
                    var keep = ExpandToGroups(page, new[] { clicked });
                    RenderAllAnnotations(page);
                    SelectMarks(keep);
                }
                return;
            }
            RenderAllAnnotations(page);
        }

        /// <summary>
        /// Moves <paramref name="target"/> to where <paramref name="orig"/> (a clone taken before the
        /// gesture) would be after shifting by (dx, dy). Working from the clone each time means a drag
        /// never accumulates rounding.
        /// </summary>
        private static void TranslateMark(PageAnnotation target, PageAnnotation orig, double dx, double dy)
        {
            switch (target)
            {
                case MarkupAnnotation m when orig is MarkupAnnotation o:
                    m.LineRects.Clear();
                    foreach (var lr in o.LineRects)
                        m.LineRects.Add(new Rect(lr.X + dx, lr.Y + dy, lr.Width, lr.Height));
                    m.Bounds = new Rect(o.Bounds.X + dx, o.Bounds.Y + dy, o.Bounds.Width, o.Bounds.Height);
                    break;
                case HighlightAnnotation h when orig is HighlightAnnotation o:
                    h.Bounds = new Rect(o.Bounds.X + dx, o.Bounds.Y + dy, o.Bounds.Width, o.Bounds.Height);
                    break;
                case InkAnnotation ink when orig is InkAnnotation o:
                    ink.Points.Clear();
                    foreach (var p in o.Points) ink.Points.Add(new Point(p.X + dx, p.Y + dy));
                    break;
                case ShapeAnnotation s when orig is ShapeAnnotation o:
                    s.Start = new Point(o.Start.X + dx, o.Start.Y + dy);
                    s.End = new Point(o.End.X + dx, o.End.Y + dy);
                    s.Points.Clear();
                    foreach (var p in o.Points) s.Points.Add(new Point(p.X + dx, p.Y + dy));
                    break;
                case TextAnnotation t when orig is TextAnnotation o:
                    t.Position = new Point(o.Position.X + dx, o.Position.Y + dy);
                    break;
                case TextEditAnnotation tea when orig is TextEditAnnotation o:
                    tea.Position = new Point(o.Position.X + dx, o.Position.Y + dy);
                    tea.OriginalBounds = new Rect(o.OriginalBounds.X + dx, o.OriginalBounds.Y + dy,
                        o.OriginalBounds.Width, o.OriginalBounds.Height);
                    break;
                case PlacedAnnotation pa when orig is PlacedAnnotation o:
                    pa.Position = new Point(o.Position.X + dx, o.Position.Y + dy);
                    break;
            }
        }

        // Arrow-key nudging. A run of presses is ONE undo step: the page is snapshotted on the first
        // press and later presses join it while that snapshot is still the newest undo entry and the
        // keys keep coming (key repeat), the same shape as the style bar's coalescing.
        private static readonly TimeSpan NudgeStepIdle = TimeSpan.FromSeconds(1);
        private LinkedListNode<UndoEntry>? _nudgeNode;
        private DateTime _nudgeLast;

        private void NudgeSelection(double dx, double dy)
        {
            if (!HasMarkSelection) return;
            int page = _markSelection[0].PageIndex;
            var now = DateTime.UtcNow;
            bool joins = _nudgeNode is not null && ReferenceEquals(_undoStack.Last, _nudgeNode)
                         && now - _nudgeLast <= NudgeStepIdle;
            if (!joins)
            {
                PushPageSnapshot(page);
                _nudgeNode = _undoStack.Last;
            }
            _nudgeLast = now;

            var marks = new List<PageAnnotation>(_markSelection);
            foreach (var m in marks) TranslateMark(m, m.Clone(), dx, dy);
            MarkDirty();
            RenderAllAnnotations(page);
            // Multi: the render rebuilt the chrome. Single: rebuild the classic border and handle.
            if (marks.Count == 1) SelectAnnotation(marks[0], MarkBounds(marks[0]));
        }

        // ============================================================
        // Deleting
        // ============================================================

        private void DeleteSelected()
        {
            if (_markSelection.Count == 0) return;
            var marks = new List<PageAnnotation>(_markSelection);
            int pageIdx = marks[0].PageIndex;
            if (_annotations.TryGetValue(pageIdx, out var list))
            {
                PushPageSnapshot(pageIdx);
                foreach (var m in marks) list.Remove(m);
            }
            ClearSelection();
            RenderAllAnnotations(pageIdx);
            MarkDirty();
            SetStatus(marks.Count == 1 ? "Deleted selected annotation" : $"Deleted {marks.Count} marks");
        }

        // ============================================================
        // Styling the selection together
        // ============================================================

        /// <summary>For each undo step, what every selected mark looked like before the step's first change.</summary>
        private readonly Dictionary<PageAnnotation, PageAnnotation> _restylePreMulti = new();

        /// <summary>
        /// What the user just changed on the representative mark, as a set of property overrides that can
        /// be carried to the other marks. Only the properties that differ between the before and after
        /// clones are carried, so changing the colour of a text box and an ink stroke together leaves
        /// each one's own size and font alone.
        /// </summary>
        private sealed class StyleDelta
        {
            public byte? R, G, B, A;
            public string? FontName;
            public double? FontSize;
            public bool? Bold, Italic, Underline;
            public double? Spacing;
            public double? StrokeWidth;
            public bool? HasFill;
            public byte? FR, FG, FB, FA;

            private static Color? MainColor(PageAnnotation a) => a switch
            {
                TextAnnotation t => t.GetColor(),
                TextEditAnnotation e => e.GetColor(),
                InkAnnotation i => i.GetColor(),
                HighlightAnnotation h => h.GetColor(),
                ShapeAnnotation s => s.GetStrokeColor(),
                _ => null
            };

            public static StyleDelta Between(PageAnnotation pre, PageAnnotation post)
            {
                var d = new StyleDelta();
                if (MainColor(pre) is { } c0 && MainColor(post) is { } c1)
                {
                    if (c0.R != c1.R) d.R = c1.R;
                    if (c0.G != c1.G) d.G = c1.G;
                    if (c0.B != c1.B) d.B = c1.B;
                    if (c0.A != c1.A) d.A = c1.A;
                }
                switch (pre)
                {
                    case TextAnnotation a when post is TextAnnotation b:
                        if (a.FontName != b.FontName) d.FontName = b.FontName;
                        if (a.FontSize != b.FontSize) d.FontSize = b.FontSize;
                        if (a.Bold != b.Bold) d.Bold = b.Bold;
                        if (a.Italic != b.Italic) d.Italic = b.Italic;
                        if (a.Underline != b.Underline) d.Underline = b.Underline;
                        if (a.LetterSpacing != b.LetterSpacing) d.Spacing = b.LetterSpacing;
                        if (a.HasFill != b.HasFill) d.HasFill = b.HasFill;
                        if (a.FillR != b.FillR) d.FR = b.FillR;
                        if (a.FillG != b.FillG) d.FG = b.FillG;
                        if (a.FillB != b.FillB) d.FB = b.FillB;
                        if (a.FillA != b.FillA) d.FA = b.FillA;
                        break;
                    case TextEditAnnotation a when post is TextEditAnnotation b:
                        if (a.FontName != b.FontName) d.FontName = b.FontName;
                        if (a.FontSize != b.FontSize) d.FontSize = b.FontSize;
                        if (a.Bold != b.Bold) d.Bold = b.Bold;
                        if (a.Italic != b.Italic) d.Italic = b.Italic;
                        break;
                    case InkAnnotation a when post is InkAnnotation b:
                        if (a.StrokeWidth != b.StrokeWidth) d.StrokeWidth = b.StrokeWidth;
                        break;
                    case ShapeAnnotation a when post is ShapeAnnotation b:
                        if (a.StrokeWidth != b.StrokeWidth) d.StrokeWidth = b.StrokeWidth;
                        if (a.HasFill != b.HasFill) d.HasFill = b.HasFill;
                        if (a.FillR != b.FillR) d.FR = b.FillR;
                        if (a.FillG != b.FillG) d.FG = b.FillG;
                        if (a.FillB != b.FillB) d.FB = b.FillB;
                        if (a.FillA != b.FillA) d.FA = b.FillA;
                        break;
                }
                return d;
            }

            private Color Mix(Color c) => Color.FromArgb(A ?? c.A, R ?? c.R, G ?? c.G, B ?? c.B);

            public void ApplyTo(PageAnnotation dst)
            {
                switch (dst)
                {
                    case TextAnnotation t:
                        t.SetColor(Mix(t.GetColor()));
                        if (FontName is not null) t.FontName = FontName;
                        if (FontSize is { } fs) t.FontSize = fs;
                        if (Bold is { } b) t.Bold = b;
                        if (Italic is { } i) t.Italic = i;
                        if (Underline is { } u) t.Underline = u;
                        if (Spacing is { } sp) t.LetterSpacing = sp;
                        if (HasFill is { } thf) t.HasFill = thf;
                        ApplyFillColor(t.GetFillColor(), c => t.SetFillColor(c));
                        break;
                    case TextEditAnnotation e:
                        e.SetColor(Mix(e.GetColor()));
                        if (FontName is not null) e.FontName = FontName;
                        if (FontSize is { } efs) e.FontSize = efs;
                        if (Bold is { } eb) e.Bold = eb;
                        if (Italic is { } ei) e.Italic = ei;
                        break;
                    case InkAnnotation ink:
                        ink.SetColor(Mix(ink.GetColor()));
                        if (StrokeWidth is { } w) ink.StrokeWidth = w;
                        break;
                    case HighlightAnnotation h:
                        h.SetColor(Mix(h.GetColor()));
                        break;
                    case ShapeAnnotation s:
                        s.SetStrokeColor(Mix(s.GetStrokeColor()));
                        if (StrokeWidth is { } sw) s.StrokeWidth = sw;
                        if (HasFill is { } shf) s.HasFill = shf;
                        ApplyFillColor(s.GetFillColor(), c => s.SetFillColor(c));
                        break;
                }
            }

            private void ApplyFillColor(Color current, Action<Color> set)
            {
                if (FR is not null || FG is not null || FB is not null || FA is not null)
                    set(Color.FromArgb(FA ?? current.A, FR ?? current.R, FG ?? current.G, FB ?? current.B));
            }
        }
    }
}
