// MainWindow — Electronic Signature field placement.
// The canvas mode Send for Signature drops into from "Place fields on the page": a small floating
// palette (recipient + field type, Done / Cancel), drag a box — or click — to place a field, click
// a placed field to take it off, Esc to cancel. Design: docs/design/esignature-documenso.md §5.4(a).
//
// These fields are NOT annotations. They live on the tab's ESignSendSession, never in _annotations,
// never on the undo stack and never in the saved PDF: they are instructions to the signing service
// about where to ask for a signature, and they leave the machine only inside /envelope/create.
// Their coordinates are kept in Documenso's own frame (percent of the displayed page) rather than
// canvas DIPs, so a re-render at another DPI cannot move them; see DocumensoFieldGeometry for why
// that frame and the canvas frame are the same frame.

using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow
    {
        /// <summary>How placement ended: Done keeps the fields, Cancelled restores what was there
        /// before, Aborted means the document or the mode was taken away (tab switch, tool change,
        /// page-layout change) and the send flow should stop rather than reopen its dialog.</summary>
        private enum ESignPlacementEnd { Done, Cancelled, Aborted }

        private sealed class ESignPlacement
        {
            public required DocumentContext Ctx { get; init; }
            public required ESignSendSession Session { get; init; }
            /// <summary>The fields as they were on entry — what Cancel puts back.</summary>
            public required List<ESignFieldDraft> Snapshot { get; init; }
            public Guid RecipientKey { get; set; }
            public DocumensoFieldType Type { get; set; } = DocumensoFieldType.Signature;
            /// <summary>The view to go back to; placement switches to Single page (see ESignPlaceFieldsAsync).</summary>
            public ViewMode? RestoreView { get; set; }
            public Window? Palette { get; set; }
            public Action? RefreshPalette { get; set; }
            public TaskCompletionSource<ESignPlacementEnd> Ended { get; } = new();
            public bool Dragging { get; set; }
            public Point Start { get; set; }
            public int DragPage { get; set; } = -1;
            public FrameworkElement? Preview { get; set; }
        }

        private ESignPlacement? _eSignPlacement;

        /// <summary>
        /// True while the Send for Signature dialog is up, so the fields placed so far are drawn on
        /// the page behind it. Outside the dialog and placement they are not drawn at all: pending
        /// fields with no dialog in sight would look like part of the document.
        /// </summary>
        private bool _eSignDialogOpen;

        private static readonly DocumensoFieldType[] ESignPaletteTypes =
        {
            DocumensoFieldType.Signature, DocumensoFieldType.Initials, DocumensoFieldType.Name,
            DocumensoFieldType.Email, DocumensoFieldType.Date, DocumensoFieldType.Text,
        };

        /// <summary>
        /// Enters placement mode on the active tab and completes when it ends.
        /// </summary>
        /// <remarks>
        /// Placement runs in <b>Single page view</b>, switching to it on entry and back on exit.
        /// Continuous view is view-and-navigate only in TDPdf — no tool edits there, by design (see
        /// SetupContinuousView) — and Two-Page / Grid tiles other than the selected page have no
        /// interactive canvas. One page with one canvas is the frame every field is measured in.
        /// </remarks>
        private Task<ESignPlacementEnd> ESignPlaceFieldsAsync(ESignSendSession session)
        {
            if (_doc is null) return Task.FromResult(ESignPlacementEnd.Aborted);
            if (_eSignPlacement is not null) EndESignPlacement(ESignPlacementEnd.Aborted);

            var first = session.Recipients.FirstOrDefault(r => DocumensoSendPlan.TakesFields(r.Role));
            if (first is null) return Task.FromResult(ESignPlacementEnd.Done);

            // Leave whatever tool was active before taking the pointer over: a half-drawn shape or
            // an open text box must not be left dangling under the placement gesture.
            SetTool(EditTool.Select);
            ClearSelection();

            var p = new ESignPlacement
            {
                Ctx = _ctx,
                Session = session,
                Snapshot = session.Fields.ToList(),
                RecipientKey = first.Key,
            };
            if (_viewMode != ViewMode.Single)
            {
                p.RestoreView = _viewMode;
                SetViewMode(ViewMode.Single);
            }
            _eSignPlacement = p;
            _annotationCanvas.Cursor = Cursors.Cross;
            p.Palette = BuildESignPalette(p);
            p.Palette.Show();

            int page = PageList.SelectedIndex;
            if (page >= 0) RenderAllAnnotations(page);
            SetStatus("Placing fields: drag a box on the page, or click to drop one. Click a field to remove it. Esc cancels.");
            return p.Ended.Task;
        }

        /// <summary>Leaves placement mode. Safe to call when it is not active.</summary>
        private void EndESignPlacement(ESignPlacementEnd how)
        {
            var p = _eSignPlacement;
            if (p is null) return;
            _eSignPlacement = null;

            if (p.Preview is not null) _annotationCanvas.Children.Remove(p.Preview);
            if (_annotationCanvas.IsMouseCaptured) _annotationCanvas.ReleaseMouseCapture();
            if (how == ESignPlacementEnd.Cancelled)
            {
                p.Session.Fields.Clear();
                p.Session.Fields.AddRange(p.Snapshot);
            }

            if (p.Palette is { } palette)
            {
                p.Palette = null;   // so its Closed handler does not re-enter
                palette.Close();
            }
            _annotationCanvas.Cursor = Cursors.Arrow;

            // Only put the view back if nobody changed it in the meantime.
            if (p.RestoreView is { } v && _viewMode == ViewMode.Single) SetViewMode(v);

            int page = PageList.SelectedIndex;
            if (_doc is not null && page >= 0 && ReferenceEquals(_ctx, p.Ctx)) RenderAllAnnotations(page);

            if (how == ESignPlacementEnd.Aborted && p.Session.Fields.Count > 0 && ReferenceEquals(_ctx, p.Ctx)
                && ReferenceEquals(p.Session, p.Ctx.ESignSession))
                SetStatus("Field placement ended. Your fields are kept — choose Send for Signature again to finish.");
            p.Ended.TrySetResult(how);
        }

        /// <summary>
        /// The fields placed on the page change with the document's layout, so anything that moves,
        /// deletes, crops or replaces pages drops them (recipients are kept). The same rule, and the
        /// same reasoning, as pending redaction marks in SaveTempAndReload: a field on a page index
        /// would otherwise point at whatever now occupies that spot.
        /// </summary>
        private void ESignDropFieldsForLayoutChange(DocumentContext ctx)
        {
            if (_eSignPlacement is { } p && ReferenceEquals(p.Ctx, ctx)) EndESignPlacement(ESignPlacementEnd.Aborted);
            if (ctx.ESignSession is { Fields.Count: > 0 } s)
            {
                s.Fields.Clear();
                SetStatusHeld("Electronic Signature fields were cleared — the page layout changed. Place them again.", 4000);
            }
        }

        // ============================================================
        // Pointer
        // ============================================================

        /// <summary>
        /// Placement's press: on a placed field it takes that field off; anywhere else it starts a
        /// drag. Called first from Canvas_MouseLeftButtonDown while placement is active, ahead of
        /// every tool, link and form-field branch — the pointer belongs to placement until it ends.
        /// </summary>
        private void ESignPlacementMouseDown(MouseButtonEventArgs e)
        {
            var p = _eSignPlacement!;
            e.Handled = true;
            if (_viewMode != ViewMode.Single || !ReferenceEquals(_ctx, p.Ctx)) return;
            int page = PageList.SelectedIndex;
            var (cw, ch) = EnsureRenderDims(page);
            if (page < 0 || cw <= 0 || ch <= 0) return;

            var pos = e.GetPosition(_annotationCanvas);
            double px = pos.X / cw * 100, py = pos.Y / ch * 100;
            int hit = p.Session.Fields.FindLastIndex(f => f.PageIndex == page && f.Rect.Contains(px, py));
            if (hit >= 0)
            {
                var removed = p.Session.Fields[hit];
                p.Session.Fields.RemoveAt(hit);
                RenderAllAnnotations(page);
                p.RefreshPalette?.Invoke();
                SetStatus($"{DocumensoSendPlan.FieldLabel(removed.Type)} field removed.");
                return;
            }

            p.Dragging = true;
            p.Start = pos;
            p.DragPage = page;
            p.Preview = MakeESignFieldVisual(p.Session, p.RecipientKey, p.Type, 0, 0);
            Canvas.SetLeft(p.Preview, pos.X);
            Canvas.SetTop(p.Preview, pos.Y);
            _annotationCanvas.Children.Add(p.Preview);
            _annotationCanvas.CaptureMouse();
        }

        private void ESignPlacementMouseMove(MouseEventArgs e)
        {
            var p = _eSignPlacement!;
            if (p.Preview is null) return;
            var pos = e.GetPosition(_annotationCanvas);
            Canvas.SetLeft(p.Preview, Math.Min(pos.X, p.Start.X));
            Canvas.SetTop(p.Preview, Math.Min(pos.Y, p.Start.Y));
            p.Preview.Width = Math.Abs(pos.X - p.Start.X);
            p.Preview.Height = Math.Abs(pos.Y - p.Start.Y);
        }

        /// <summary>
        /// Placement's release: a drag places a field of the dragged size, a click (under 4 DIPs of
        /// movement either way) places a default-size one at the click.
        /// </summary>
        private void ESignPlacementMouseUp(MouseButtonEventArgs e)
        {
            var p = _eSignPlacement!;
            e.Handled = true;
            p.Dragging = false;
            if (_annotationCanvas.IsMouseCaptured) _annotationCanvas.ReleaseMouseCapture();
            if (p.Preview is not null) _annotationCanvas.Children.Remove(p.Preview);
            p.Preview = null;

            int page = p.DragPage;
            if (page < 0 || page != PageList.SelectedIndex || !ReferenceEquals(_ctx, p.Ctx)) return;
            var (cw, ch) = EnsureRenderDims(page);
            if (cw <= 0 || ch <= 0) return;

            var pos = e.GetPosition(_annotationCanvas);
            double dx = pos.X - p.Start.X, dy = pos.Y - p.Start.Y;
            DocumensoFieldRect? rect = Math.Abs(dx) < 4 && Math.Abs(dy) < 4
                ? DocumensoFieldGeometry.DefaultAt(p.Type, p.Start.X, p.Start.Y, cw, ch)
                : DocumensoFieldGeometry.CanvasToPercent(p.Start.X, p.Start.Y, dx, dy, cw, ch);
            if (rect is not { } r || r.Width <= 0.05 || r.Height <= 0.05) return;

            p.Session.Fields.Add(new ESignFieldDraft(p.RecipientKey, p.Type, page, r));
            RenderAllAnnotations(page);
            p.RefreshPalette?.Invoke();
            int idx = p.Session.Recipients.FindIndex(x => x.Key == p.RecipientKey);
            string who = idx >= 0 ? DocumensoSendPlan.Label(p.Session.Recipients[idx], idx) : "";
            SetStatus($"{DocumensoSendPlan.FieldLabel(p.Type)} field placed for {who} on page {page + 1}.");
        }

        // ============================================================
        // Drawing
        // ============================================================

        /// <summary>
        /// Draws the active tab's pending fields for <paramref name="pageIndex"/>. Called from
        /// RenderAllAnnotations next to the redaction marks, because the canvas clear there wipes
        /// these too. Draws nothing unless placement or the Send dialog is active.
        /// </summary>
        private void RenderESignFieldDrafts(int pageIndex)
        {
            if (_eSignPlacement is null && !_eSignDialogOpen) return;
            // Placement may be working on a session other than the tab's Send session — Sign This
            // Document places your signature on a session of its own — and what is being placed is
            // what has to be drawn.
            var drawn = _eSignPlacement is { } live && ReferenceEquals(live.Ctx, _ctx) ? live.Session : _ctx.ESignSession;
            if (drawn is not { Fields.Count: > 0 } session) return;
            var (cw, ch) = EnsureRenderDims(pageIndex);
            if (cw <= 0 || ch <= 0) return;

            foreach (var f in session.Fields)
            {
                if (f.PageIndex != pageIndex) continue;
                var (x, y, w, h) = DocumensoFieldGeometry.PercentToCanvas(f.Rect, cw, ch);
                if (!IsFinitePositive(w) || !IsFinitePositive(h)) continue;
                var vis = MakeESignFieldVisual(session, f.RecipientKey, f.Type, w, h);
                Canvas.SetLeft(vis, x);
                Canvas.SetTop(vis, y);
                _annotationCanvas.Children.Add(vis);
            }
        }

        /// <summary>
        /// A placed field: dashed outline and faint fill in the recipient's colour, labelled
        /// "Type · recipient". Dashed and labelled so it reads as an instruction, not as content —
        /// the same reason a pending redaction mark is not a solid box. Not hit-testable: removal is
        /// resolved against the stored rectangles, so the visual must not swallow the press.
        /// </summary>
        private FrameworkElement MakeESignFieldVisual(ESignSendSession session, Guid recipientKey,
            DocumensoFieldType type, double w, double h)
        {
            int idx = session.Recipients.FindIndex(r => r.Key == recipientKey);
            var brush = DocumensoUi.RecipientBrush(Math.Max(0, idx));
            string who = idx >= 0 ? DocumensoSendPlan.Label(session.Recipients[idx], idx) : "?";

            var grid = new Grid { Width = w, Height = h, IsHitTestVisible = false, ClipToBounds = true };
            grid.Children.Add(new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(40, brush.Color.R, brush.Color.G, brush.Color.B)),
                Stroke = brush,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 3 },
            });
            grid.Children.Add(new TextBlock
            {
                Text = $"{DocumensoSendPlan.FieldLabel(type)} · {who}",
                Foreground = brush,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(5, 2, 5, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Top,
            });
            return grid;
        }

        // ============================================================
        // Palette
        // ============================================================

        /// <summary>
        /// The floating palette: who the next field is for, what kind of field, how many are placed,
        /// and Done / Cancel. A small owned window rather than a toolbar: placement is a mode the
        /// Send dialog lends out, and the toolbar is the editor's.
        /// </summary>
        private Window BuildESignPalette(ESignPlacement p)
        {
            var (win, root, closeBtn) = DocumensoUi.CreateShell(this, "Place fields", 300);
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.ShowActivated = true;
            closeBtn.ToolTip = "Done";
            System.Windows.Automation.AutomationProperties.SetName(closeBtn, "Done placing fields");

            var body = new StackPanel { Margin = new Thickness(14, 10, 14, 6) };
            root.Children.Add(body);

            body.Children.Add(DocumensoUi.Heading("For", new Thickness(0, 0, 0, 3)));
            var who = DocumensoUi.MakeCombo(this, "Recipient for the next field");
            who.Margin = new Thickness(0, 0, 0, 8);
            body.Children.Add(who);

            body.Children.Add(DocumensoUi.Heading("Field", new Thickness(0, 0, 0, 3)));
            var types = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            body.Children.Add(types);
            var typeButtons = new List<(Button Btn, DocumensoFieldType Type)>();
            // Declared ahead of the buttons: their handlers call Paint, which writes it.
            var count = DocumensoUi.Para("", "TextSecondary", 11, new Thickness(0, 0, 0, 4));
            foreach (var t in ESignPaletteTypes)
            {
                var b = DocumensoUi.MakeButton(this, DocumensoSendPlan.FieldLabel(t), primary: false);
                b.Padding = new Thickness(8, 3, 8, 3);
                b.Margin = new Thickness(0, 0, 4, 4);
                var type = t;
                b.Click += (_, _) => { p.Type = type; Paint(); };
                types.Children.Add(b);
                typeButtons.Add((b, t));
            }

            body.Children.Add(count);
            body.Children.Add(DocumensoUi.Para("Drag a box on the page, or click to drop one. Click a field to remove it. " +
                "Change page as usual.", "TextMuted", 11, new Thickness(0, 0, 0, 4)));

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 4, 14, 12) };
            var done = DocumensoUi.MakeButton(this, "Done", primary: true);
            var cancel = DocumensoUi.MakeButton(this, "Cancel", primary: false);
            cancel.Margin = new Thickness(8, 0, 0, 0);
            cancel.ToolTip = "Put the fields back the way they were and return (Esc)";
            footer.Children.Add(done);
            footer.Children.Add(cancel);
            root.Children.Add(footer);

            void FillRecipients()
            {
                who.Items.Clear();
                for (int i = 0; i < p.Session.Recipients.Count; i++)
                {
                    var r = p.Session.Recipients[i];
                    if (!DocumensoSendPlan.TakesFields(r.Role)) continue;
                    var line = new StackPanel { Orientation = Orientation.Horizontal };
                    line.Children.Add(new Rectangle { Width = 10, Height = 10, Fill = DocumensoUi.RecipientBrush(i), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                    line.Children.Add(new TextBlock { Text = DocumensoSendPlan.Label(r, i), VerticalAlignment = VerticalAlignment.Center });
                    var item = new ComboBoxItem { Content = line, Tag = r.Key };
                    System.Windows.Automation.AutomationProperties.SetName(item, DocumensoSendPlan.Label(r, i));
                    who.Items.Add(item);
                    if (r.Key == p.RecipientKey) who.SelectedItem = item;
                }
                if (who.SelectedIndex < 0 && who.Items.Count > 0) who.SelectedIndex = 0;
            }

            void Paint()
            {
                foreach (var (b, t) in typeButtons)
                {
                    bool on = t == p.Type;
                    b.Background = DocumensoUi.Brush(on ? "AccentGreenDim" : "BgHover");
                    b.Foreground = DocumensoUi.Brush(on ? "AccentGreen" : "TextPrimary");
                    b.BorderBrush = DocumensoUi.Brush(on ? "AccentGreen" : "BorderDim");
                }
                int n = p.Session.Fields.Count;
                count.Text = n == 0 ? "No fields placed yet." : $"{n} field{(n == 1 ? "" : "s")} placed.";
            }

            who.SelectionChanged += (_, _) =>
            {
                if (who.SelectedItem is ComboBoxItem { Tag: Guid key }) p.RecipientKey = key;
            };
            done.Click += (_, _) => EndESignPlacement(ESignPlacementEnd.Done);
            closeBtn.Click += (_, _) => EndESignPlacement(ESignPlacementEnd.Done);
            cancel.Click += (_, _) => EndESignPlacement(ESignPlacementEnd.Cancelled);
            win.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                EndESignPlacement(ESignPlacementEnd.Cancelled);
            };
            // Closed from outside (the main window going away takes its owned windows with it).
            win.Closed += (_, _) =>
            {
                if (_eSignPlacement is { } live && ReferenceEquals(live, p) && p.Palette is not null)
                {
                    p.Palette = null;
                    EndESignPlacement(ESignPlacementEnd.Aborted);
                }
            };
            // Top-right of the editor, clear of the toolbar, where it covers the least of a page.
            win.Loaded += (_, _) =>
            {
                try
                {
                    var topRight = PointToScreen(new Point(ActualWidth, 0));
                    var dpi = VisualTreeHelper.GetDpi(this);
                    win.Left = topRight.X / dpi.DpiScaleX - win.ActualWidth - 24;
                    win.Top = topRight.Y / dpi.DpiScaleY + 120;
                }
                catch { /* leave it where WPF put it */ }
            };

            p.RefreshPalette = Paint;
            FillRecipients();
            Paint();
            return win;
        }
    }
}
