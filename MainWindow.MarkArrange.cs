// MainWindow — align, space and size several marks (#248).
//
// The geometry is Services/MarkArrange (pure, tested); this file reads the selected marks' boxes,
// asks it where each should go, moves or resizes them, and shows the buttons. One command is one
// undo step, and a command that would change nothing leaves no undo step behind.
//
// Same width / same height match the FIRST mark selected (the anchor, as in most editors), and only
// reach the kinds that can be resized here: text boxes, plain highlights, shapes, and placed
// signatures and pictures (resized in proportion, so matching one side sets the other too).
// Hugging text markup, ink strokes and edited PDF text are moved by the align and distribute
// commands but never stretched.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        private Border? _arrangeBar;
        private readonly List<(Button Button, int MinMarks)> _arrangeButtons = new();

        private static bool CanResizeMark(PageAnnotation a) =>
            a is TextAnnotation or PlacedAnnotation or ShapeAnnotation
            || (a is HighlightAnnotation and not MarkupAnnotation);

        private List<MarkArrange.Box> SelectionBoxes() =>
            _markSelection.Select(m =>
            {
                var b = MarkBounds(m);
                return IsUsableRect(b) ? new MarkArrange.Box(b.X, b.Y, b.Width, b.Height)
                                       : new MarkArrange.Box(double.NaN, double.NaN, double.NaN, double.NaN);
            }).ToList();

        /// <summary>Moves each selected mark by its own shift, as one undo step. No shift at all: nothing is recorded.</summary>
        private void ApplyShifts((double Dx, double Dy)[] shifts, string status)
        {
            if (!IsMultiSelection || shifts.Length != _markSelection.Count) return;
            if (shifts.All(s => Math.Abs(s.Dx) < 1e-9 && Math.Abs(s.Dy) < 1e-9))
            {
                SetStatus("Already in place");
                return;
            }
            var marks = new List<PageAnnotation>(_markSelection);
            int page = marks[0].PageIndex;
            PushPageSnapshot(page);
            for (int i = 0; i < marks.Count; i++)
                TranslateMark(marks[i], marks[i].Clone(), shifts[i].Dx, shifts[i].Dy);
            MarkDirty();
            RenderAllAnnotations(page);
            SelectMarks(marks);
            SetStatus(status);
        }

        private void AlignSelected(MarkArrange.AlignEdge edge)
        {
            if (!IsMultiSelection) return;
            string what = edge switch
            {
                MarkArrange.AlignEdge.Left => "left",
                MarkArrange.AlignEdge.Center => "centre",
                MarkArrange.AlignEdge.Right => "right",
                MarkArrange.AlignEdge.Top => "top",
                MarkArrange.AlignEdge.Middle => "middle",
                _ => "bottom"
            };
            ApplyShifts(MarkArrange.Align(SelectionBoxes(), edge), $"Aligned {_markSelection.Count} marks to the {what}");
        }

        private void DistributeSelected(bool horizontal)
        {
            if (_markSelection.Count < 3)
            {
                SetStatus("Select three or more marks to space them evenly");
                return;
            }
            ApplyShifts(MarkArrange.Distribute(SelectionBoxes(), horizontal),
                horizontal ? "Spaced the marks evenly across" : "Spaced the marks evenly down");
        }

        private void MatchSizeSelected(bool width)
        {
            if (!IsMultiSelection) return;
            var boxes = SelectionBoxes();
            var targets = MarkArrange.MatchSize(boxes, _markSelection.Select(CanResizeMark).ToList(), width);
            if (targets.All(t => t is null))
            {
                SetStatus(_markSelection.Skip(1).Any(CanResizeMark)
                    ? "Already the same size"
                    : "None of the other selected marks can be resized");
                return;
            }
            var marks = new List<PageAnnotation>(_markSelection);
            int page = marks[0].PageIndex;
            PushPageSnapshot(page);
            for (int i = 0; i < marks.Count; i++)
                if (targets[i] is { } t) ResizeMarkVisual(marks[i], width ? t : null, width ? null : t);
            MarkDirty();
            RenderAllAnnotations(page);
            SelectMarks(marks);
            SetStatus(width ? "Matched the width of the first mark" : "Matched the height of the first mark");
        }

        /// <summary>
        /// Sets a mark's ON-SCREEN width and/or height, keeping its top-left corner where it is. A turned
        /// text box or picture is sized along its visual axes, as dragging its handle does.
        /// </summary>
        private void ResizeMarkVisual(PageAnnotation mark, double? w, double? h)
        {
            switch (mark)
            {
                case MarkupAnnotation:
                    return;
                case HighlightAnnotation ha:
                {
                    var b = ha.Bounds;
                    ha.Bounds = new Rect(b.X, b.Y, Math.Max(4, w ?? b.Width), Math.Max(4, h ?? b.Height));
                    return;
                }
                case ShapeAnnotation s:
                {
                    var b = s.Bounds;
                    // A flat line has no extent along one axis to stretch; that axis is left alone.
                    double sx = w is { } nw && b.Width > 0 ? Math.Max(4, nw) / b.Width : 1;
                    double sy = h is { } nh && b.Height > 0 ? Math.Max(4, nh) / b.Height : 1;
                    Point Map(Point p) => new(b.X + (p.X - b.X) * sx, b.Y + (p.Y - b.Y) * sy);
                    if (s.Kind == ShapeKind.Polygon)
                    {
                        var mapped = s.Points.Select(Map).ToList();
                        s.Points.Clear();
                        s.Points.AddRange(mapped);
                    }
                    else
                    {
                        s.Start = Map(s.Start);
                        s.End = Map(s.End);
                    }
                    return;
                }
                case TextAnnotation t:
                {
                    // A legacy auto-sized box has no fixed Width/Height yet; seed them from its extent.
                    if (t.Width <= 0 || t.Height <= 0)
                    {
                        var sz = MeasureTextAnnotation(t);
                        if (t.Width <= 0) t.Width = sz.Width;
                        if (t.Height <= 0) t.Height = sz.Height;
                    }
                    var vis = TextVisualRect(t);
                    double vw = w ?? vis.Width, vh = h ?? vis.Height;
                    bool quarter = AnnotationFrame.IsQuarterTurn(t.Rotation);
                    t.Width = Math.Max(32, quarter ? vh : vw);
                    t.Height = Math.Max(t.FontSize + 6, quarter ? vw : vh);
                    var (nx, ny) = AnnotationFrame.PositionFromVisualTopLeft(vis.X, vis.Y, t.Width, t.Height, t.Rotation);
                    t.Position = new Point(nx, ny);
                    return;
                }
                case PlacedAnnotation p:
                {
                    var vis = PlacedVisualRect(p);
                    bool quarter = AnnotationFrame.IsQuarterTurn(p.Rotation);
                    double srcAlongWidth = quarter ? p.SourceHeight : p.SourceWidth;
                    double srcAlongHeight = quarter ? p.SourceWidth : p.SourceHeight;
                    double scale = w is { } pw && srcAlongWidth > 0 ? pw / srcAlongWidth
                                 : h is { } ph && srcAlongHeight > 0 ? ph / srcAlongHeight
                                 : p.Scale;
                    if (!IsFinitePositive(scale)) return;
                    p.Scale = Math.Max(0.05, scale);
                    var (px, py) = AnnotationFrame.PositionFromVisualTopLeft(
                        vis.X, vis.Y, p.SourceWidth * p.Scale, p.SourceHeight * p.Scale, p.Rotation);
                    p.Position = new Point(px, py);
                    return;
                }
            }
        }

        // ============================================================
        // The buttons: a small bar under the style bar, and a submenu on the right-click menu
        // ============================================================

        private void ShowArrangeBar()
        {
            HideArrangeBar();
            if (!IsMultiSelection) return;

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };
            panel.Children.Add(new TextBlock
            {
                Text = "Arrange:",
                Foreground = BrushResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });

            // The paragraph-alignment glyphs, turned a quarter for the vertical set: lines flush left
            // read as flush top once turned, centred as middle, flush right as bottom.
            AddArrangeButton(panel, "", 0, "Align left", () => AlignSelected(MarkArrange.AlignEdge.Left), 2);
            AddArrangeButton(panel, "", 0, "Align centres", () => AlignSelected(MarkArrange.AlignEdge.Center), 2);
            AddArrangeButton(panel, "", 0, "Align right", () => AlignSelected(MarkArrange.AlignEdge.Right), 2);
            AddArrangeSeparator(panel);
            AddArrangeButton(panel, "", 90, "Align top", () => AlignSelected(MarkArrange.AlignEdge.Top), 2);
            AddArrangeButton(panel, "", 90, "Align middles", () => AlignSelected(MarkArrange.AlignEdge.Middle), 2);
            AddArrangeButton(panel, "", 90, "Align bottom", () => AlignSelected(MarkArrange.AlignEdge.Bottom), 2);
            AddArrangeSeparator(panel);
            AddArrangeIconButton(panel, "M2,2 V14 M14,2 V14 M6,4 H10 V12 H6 Z", false, "Space evenly across (3 or more marks)",
                () => DistributeSelected(true), 3);
            AddArrangeIconButton(panel, "M2,2 V14 M14,2 V14 M6,4 H10 V12 H6 Z", true, "Space evenly down (3 or more marks)",
                () => DistributeSelected(false), 3);
            AddArrangeSeparator(panel);
            AddArrangeIconButton(panel, "M2,8 H14 M2,4 V12 M14,4 V12 M5,6 L2,8 L5,10 M11,6 L14,8 L11,10", false,
                "Same width as the first mark", () => MatchSizeSelected(true), 2);
            AddArrangeIconButton(panel, "M2,8 H14 M2,4 V12 M14,4 V12 M5,6 L2,8 L5,10 M11,6 L14,8 L11,10", true,
                "Same height as the first mark", () => MatchSizeSelected(false), 2);

            // Sits under whichever style bar the selection shows, or at the top when there is none.
            bool styleBarShown = _textSettingsBar is not null || _shapeSettingsBar is not null || _drawSettingsBar is not null;
            _arrangeBar = new Border
            {
                Background = BrushResource("BgPanel"),
                BorderBrush = BrushResource("BorderDim"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Child = panel,
                Margin = new Thickness(8, styleBarShown ? 40 : 0, 0, 0)
            };
            AutomationProperties.SetName(_arrangeBar, "Arrange selected marks");
            if (PagePreviewPanel.Parent is Grid grid)
            {
                Panel.SetZIndex(_arrangeBar, 99);
                grid.Children.Add(_arrangeBar);
            }
            UpdateArrangeButtons();
        }

        private void HideArrangeBar()
        {
            if (_arrangeBar is not null)
            {
                (PagePreviewPanel.Parent as Grid)?.Children.Remove(_arrangeBar);
                _arrangeBar = null;
            }
            _arrangeButtons.Clear();
        }

        /// <summary>Greys the buttons that need more marks than are selected (spacing needs three).</summary>
        private void UpdateArrangeButtons()
        {
            foreach (var (button, min) in _arrangeButtons) button.IsEnabled = _markSelection.Count >= min;
        }

        private Button MakeArrangeButton(object content, string tip, Action click, int minMarks)
        {
            var btn = new Button
            {
                Style = (Style)FindResource("ToolbarButton"),
                Padding = new Thickness(0),
                Content = content,
                Width = 26, Height = 24,
                Margin = new Thickness(2, 0, 2, 0),
                ToolTip = tip,
                Cursor = Cursors.Hand,
                Background = BrushResource("BgHover"),
                Foreground = BrushResource("TextPrimary"),
                BorderBrush = BrushResource("BorderDim"),
                BorderThickness = new Thickness(1)
            };
            AutomationProperties.SetName(btn, tip);
            btn.Click += (_, _) => click();
            _arrangeButtons.Add((btn, minMarks));
            return btn;
        }

        private void AddArrangeButton(StackPanel panel, string glyph, double turn, string tip, Action click, int minMarks)
        {
            var text = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(turn)
            };
            panel.Children.Add(MakeArrangeButton(text, tip, click, minMarks));
        }

        /// <summary>The distribute and same-size buttons have no glyph in the icon font, so they are small vector icons.</summary>
        private void AddArrangeIconButton(StackPanel panel, string pathData, bool turned, string tip, Action click, int minMarks)
        {
            var path = new Path
            {
                Data = Geometry.Parse(pathData),
                Stroke = BrushResource("TextPrimary"),
                StrokeThickness = 1.4,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 16, Height = 16,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(turned ? 90 : 0)
            };
            panel.Children.Add(MakeArrangeButton(path, tip, click, minMarks));
        }

        private void AddArrangeSeparator(StackPanel panel) =>
            panel.Children.Add(new Rectangle
            {
                Width = 1, Fill = BrushResource("BorderDim"),
                Margin = new Thickness(6, 2, 6, 2)
            });

        private MenuItem BuildArrangeSubmenu()
        {
            var arrange = MakeMenuItem("Arran_ge", (s, e) => { }, null, "Align, space evenly or match the size of the selected marks");
            arrange.Items.Add(MakeMenuItem("Align _Left", (s, e) => AlignSelected(MarkArrange.AlignEdge.Left)));
            arrange.Items.Add(MakeMenuItem("Align _Centres", (s, e) => AlignSelected(MarkArrange.AlignEdge.Center)));
            arrange.Items.Add(MakeMenuItem("Align _Right", (s, e) => AlignSelected(MarkArrange.AlignEdge.Right)));
            arrange.Items.Add(new Separator());
            arrange.Items.Add(MakeMenuItem("Align _Top", (s, e) => AlignSelected(MarkArrange.AlignEdge.Top)));
            arrange.Items.Add(MakeMenuItem("Align _Middles", (s, e) => AlignSelected(MarkArrange.AlignEdge.Middle)));
            arrange.Items.Add(MakeMenuItem("Align _Bottom", (s, e) => AlignSelected(MarkArrange.AlignEdge.Bottom)));
            arrange.Items.Add(new Separator());
            _ctxDistH = MakeMenuItem("Space Evenly _Across", (s, e) => DistributeSelected(true));
            _ctxDistV = MakeMenuItem("Space Evenly _Down", (s, e) => DistributeSelected(false));
            arrange.Items.Add(_ctxDistH);
            arrange.Items.Add(_ctxDistV);
            arrange.Items.Add(new Separator());
            arrange.Items.Add(MakeMenuItem("Same _Width", (s, e) => MatchSizeSelected(true)));
            arrange.Items.Add(MakeMenuItem("Same _Height", (s, e) => MatchSizeSelected(false)));
            return arrange;
        }

        private MenuItem? _ctxArrange, _ctxDistH, _ctxDistV;
    }
}
