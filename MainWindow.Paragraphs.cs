// MainWindow — editing a whole paragraph of existing text (#229).
//
// The line editor (MainWindow.TextEditing.cs) swaps one run. This opens the whole block: the
// paragraph is found from the text geometry (Services/ParagraphFinder), shown in one wrapped box at
// its own width, and on commit it is reflowed into the document's own font where that font can draw
// what was typed. When it cannot, the user is asked before the paragraph is covered and redrawn.
//
// Gesture: hold Ctrl with the gesture that edits a line. In the Edit Existing Text tool that is
// Ctrl+click; in the Select tool it is Ctrl+double-click. The right-click menu has Edit Paragraph.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PdfSharpCore.Pdf;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        /// <summary>Where the last right-click landed on the canvas, for the Edit Paragraph menu item.</summary>
        private Point? _ctxClickPos;

        /// <summary>Everything about the paragraph being edited that is fixed when its box opens.</summary>
        private sealed class ParagraphEditState
        {
            public ParagraphPage Page = null!;
            public ParagraphBlock Block = null!;
            /// <summary>Canvas pixels per point, across and down.</summary>
            public double Sx, Sy;
            public int RenderW, RenderH;
            /// <summary>The paragraph's extent on the canvas.</summary>
            public Rect CanvasBox;
            public ParagraphFacts Facts = null!;
            public Color InitialColor = Colors.Black;
        }

        // ============================================================
        // Opening the box
        // ============================================================

        private void EditParagraphFromMenu()
        {
            int pageIdx = PageList.SelectedIndex;
            if (_ctxClickPos is not { } pos || pageIdx < 0)
            {
                SetStatus("Right-click the paragraph you want to edit");
                return;
            }
            EditParagraphAtPosition(pos, pageIdx);
        }

        private void EditParagraphAtPosition(Point canvasPos, int pageIdx)
        {
            if (_currentFile is null || _doc is null || !_renderDims.TryGetValue(pageIdx, out var dims)
                || dims.w <= 0 || dims.h <= 0) return;
            ClearSelection();

            if (_activeTextBox is not null)
            {
                CommitActiveTextBox();
                return;
            }

            // A paragraph already redrawn over the page re-opens through the line editor's own
            // re-edit path, which knows how to hand it back.
            if (_annotations.TryGetValue(pageIdx, out var marks)
                && marks.OfType<TextEditAnnotation>().Any(a => a.LineHeight > 0 && TextEditHitBounds(a).Contains(canvasPos)))
            {
                EditTextAtPosition(canvasPos, pageIdx);
                return;
            }

            try
            {
                var pg = ParagraphPageReader.Read(_currentFile, pageIdx);
                if (pg is null || pg.Width <= 0 || pg.Height <= 0) { SetStatus("No text found at this position"); return; }

                double sx = dims.w / pg.Width, sy = dims.h / pg.Height;
                int li = ParagraphPageReader.LineAt(pg.Lines, canvasPos.X / sx, (dims.h - canvasPos.Y) / sy);
                var block = li < 0 ? null : ParagraphFinder.Find(pg.Lines, li);
                if (block is null) { SetStatus("No text found at this position"); return; }

                var st = new ParagraphEditState
                {
                    Page = pg, Block = block, Sx = sx, Sy = sy, RenderW = dims.w, RenderH = dims.h,
                    CanvasBox = CanvasRectOf(block.Left, block.Top, block.Right, block.Bottom, sx, sy, dims.h),
                };
                st.Facts = GatherParagraphFacts(pageIdx, st);
                var first = block.Lines[0];
                st.InitialColor = Color.FromRgb((byte)(first.Color >> 16), (byte)(first.Color >> 8), (byte)first.Color);

                var verdict = ParagraphEditPolicy.Before(st.Facts);
                if (verdict.Plan == ParagraphPlan.Refuse)
                {
                    SetStatus($"This paragraph can't be edited: {verdict.Reason}");
                    TdpDialog.Show(this, $"This paragraph can't be edited: {verdict.Reason}.",
                        "Edit Paragraph", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                OpenParagraphEditor(pageIdx, st, block.Text, firstOpen: true);
            }
            catch (Exception ex)
            {
                SetStatus($"Paragraph edit error: {ex.Message}");
            }
        }

        private static Rect CanvasRectOf(double left, double top, double right, double bottom, double sx, double sy, double renderH) =>
            new(left * sx, renderH - top * sy, Math.Max((right - left) * sx, 1), Math.Max((top - bottom) * sy, 1));

        private ParagraphFacts GatherParagraphFacts(int pageIdx, ParagraphEditState st)
        {
            var block = st.Block;
            var page = _doc!.Pages[pageIdx];

            var scan = PdfiumInterop.CanRewriteParagraphs
                ? PdfiumInterop.ScanParagraph(_currentFile!, pageIdx, ParagraphPdfBox(page, block))
                : new PdfiumInterop.ParagraphScan();

            bool unsafePage;
            try { unsafePage = PdfContentInspector.Inspect(page) != ContentHazard.None; }
            catch { unsafePage = true; }

            return new ParagraphFacts
            {
                OpaqueCover = scan.OpaqueCover || PendingOpaqueMarkOver(pageIdx, st),
                ScanText = scan.TextObjects > 0 && scan.InvisibleOnly,
                RightToLeft = block.RightToLeft,
                MixedStyles = block.MixedStyles || scan.MixedStyles,
                PageUnsafe = unsafePage,
                EngineMissing = !PdfiumInterop.CanRewriteParagraphs,
                Unsupported = scan.TextObjects == 0 || scan.Unsupported || (scan.InForm && !scan.InvisibleOnly),
                DontAsk = TDPdf.Properties.Settings.Default.SkipParagraphCoverPrompt,
            };
        }

        private static PdfiumInterop.PdfRect ParagraphPdfBox(PdfPage page, ParagraphBlock b, double pad = 3) =>
            PdfPageGeometry.TextFrameToPdf(page, new PdfiumInterop.PdfRect(b.Left - pad, b.Bottom - pad, b.Right + pad, b.Top + pad));

        /// <summary>
        /// True when one of TDPdf's own unsaved marks hides most of a line of the paragraph: a filled
        /// shape or eraser, a picture, a white-out text box, or an earlier text edit's cover.
        /// </summary>
        private bool PendingOpaqueMarkOver(int pageIdx, ParagraphEditState st)
        {
            if (!_annotations.TryGetValue(pageIdx, out var marks)) return false;
            var lines = st.Block.Lines
                .Select(l => CanvasRectOf(l.Left, l.Top, l.Right, l.Bottom, st.Sx, st.Sy, st.RenderH)).ToList();

            bool Hides(Rect cover) => lines.Any(line =>
            {
                var i = Rect.Intersect(cover, line);
                return !i.IsEmpty && i.Width * i.Height >= 0.5 * line.Width * line.Height;
            });

            foreach (var m in marks)
            {
                bool opaque = m switch
                {
                    MarkupAnnotation => false,
                    HighlightAnnotation h => h.ColorA >= 250 && Hides(h.Bounds),
                    ShapeAnnotation s => s.HasFill && s.FillA >= 250 && Hides(s.Bounds),
                    TextAnnotation t => t.HasFill && t.FillA >= 250 && Hides(TextVisualRect(t)),
                    TextEditAnnotation e => Hides(e.OriginalBounds),
                    ImageEditAnnotation ie => Hides(ie.OriginalBounds),
                    ImageAnnotation or StampAnnotation => m is PlacedAnnotation pa && Hides(PlacedVisualRect(pa)),
                    _ => false,
                };
                if (opaque) return true;
            }
            return false;
        }

        private void OpenParagraphEditor(int pageIdx, ParagraphEditState st, string text, bool firstOpen)
        {
            var block = st.Block;
            var first = block.Lines[0];
            var detected = PdfFontStyle.FromPdfName(first.FontName);
            double sizePx = Math.Max(block.FontSize * st.Sy, 8);

            RememberTextDefaultsForEdit();
            _textFontFamily = detected.Family;
            _textFontSize = sizePx;
            _textBold = detected.Bold;
            _textItalic = detected.Italic;
            _textColor = st.InitialColor;

            var box = st.CanvasBox;
            var tb = new TextBox
            {
                Text = text,
                Background = TdpDialog.FrozenSolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
                Foreground = new SolidColorBrush(_textColor),
                BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                BorderThickness = new Thickness(2),
                FontFamily = new FontFamily(detected.Family),
                FontSize = sizePx,
                FontWeight = detected.Bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = detected.Italic ? FontStyles.Italic : FontStyles.Normal,
                Padding = new Thickness(2, 0, 2, 0),
                Tag = new TextEditContext
                {
                    PageIndex = pageIdx,
                    OriginalText = block.Text,
                    CanvasBounds = box,
                    Position = new Point(box.X, box.Y),
                    FontSize = sizePx,
                    FontName = detected.Family,
                    Bold = detected.Bold,
                    Italic = detected.Italic,
                    Paragraph = st,
                },
            };
            ConfigureParagraphEditBox(tb, box);
            tb.TextAlignment = block.Alignment switch
            {
                ParagraphAlignment.Centered => TextAlignment.Center,
                ParagraphAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            };

            Canvas.SetLeft(tb, box.X - 2);
            Canvas.SetTop(tb, box.Y);
            _textEditorCanvas.Children.Add(tb);
            _activeTextBox = tb;
            ShowTextSettings();

            // The old words are hidden while the box is open, as with the line editor.
            var whiteout = new Rectangle
            {
                Fill = Brushes.White,
                Width = box.Width + 6,
                Height = box.Height + 2,
                IsHitTestVisible = false,
                Tag = "EditWhiteout",
            };
            Canvas.SetLeft(whiteout, box.X - 3);
            Canvas.SetTop(whiteout, box.Y - 1);
            _textEditorCanvas.Children.Insert(_textEditorCanvas.Children.IndexOf(tb), whiteout);

            tb.KeyDown += EditTextBox_KeyDown;
            FocusTextEditorWhenLoaded(tb, selectAll: false, EditTextBox_LostFocus);
            SetStatus(firstOpen
                ? "Editing paragraph. Ctrl+Enter to save, Escape to cancel"
                : "Back to editing the paragraph. Ctrl+Enter to save, Escape to cancel");
        }

        /// <summary>
        /// Makes a text box a paragraph box: wrapped to the paragraph's width, Enter for a new line,
        /// Ctrl+Enter to save.
        /// </summary>
        private void ConfigureParagraphEditBox(TextBox tb, Rect box)
        {
            tb.AcceptsReturn = true;
            tb.TextWrapping = TextWrapping.Wrap;
            tb.VerticalContentAlignment = VerticalAlignment.Top;
            tb.Width = Math.Max(box.Width + 8, 80);
            tb.MinHeight = Math.Max(box.Height + 12, 24);
            tb.Height = double.NaN;
            tb.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    CommitTextEdit();
                    e.Handled = true;
                }
            };
        }

        // ============================================================
        // Saving it
        // ============================================================

        private void CommitParagraphEdit(TextEditContext ctx, TextBox tb, string newText)
        {
            var st = ctx.Paragraph!;
            int pageIdx = ctx.PageIndex;
            Color tbColor = tb.Foreground is SolidColorBrush scb ? scb.Color : Colors.Black;
            bool styleChanged = tb.FontFamily.Source != ctx.FontName
                || Math.Abs(tb.FontSize - ctx.FontSize) > 0.01
                || (tb.FontWeight == FontWeights.Bold) != ctx.Bold
                || (tb.FontStyle == FontStyles.Italic) != ctx.Italic
                || tbColor != st.InitialColor;
            if (newText == ctx.OriginalText && !styleChanged)
            {
                SetStatus("No changes made");
                return;
            }

            bool dontAsk = TDPdf.Properties.Settings.Default.SkipParagraphCoverPrompt;
            var facts = new ParagraphFacts
            {
                OpaqueCover = st.Facts.OpaqueCover, ScanText = st.Facts.ScanText, RightToLeft = st.Facts.RightToLeft,
                MixedStyles = st.Facts.MixedStyles, PageUnsafe = st.Facts.PageUnsafe, EngineMissing = st.Facts.EngineMissing,
                Unsupported = st.Facts.Unsupported, StyleChanged = styleChanged, DontAsk = dontAsk,
            };
            var verdict = ParagraphEditPolicy.Before(facts);

            try
            {
                if (verdict.Plan == ParagraphPlan.Refuse)
                {
                    SetStatus($"This paragraph can't be edited: {verdict.Reason}");
                    return;
                }

                if (verdict.Plan == ParagraphPlan.ScanOverlay)
                {
                    CommitScanParagraph(ctx, st, tb, newText, tbColor);
                    return;
                }

                if (verdict.Plan == ParagraphPlan.InPlace)
                {
                    if (TryRewriteParagraph(ctx, st, newText, out string? why))
                        return;
                    verdict = ParagraphEditPolicy.AfterFailure(why ?? "the engine could not write it", dontAsk, scanText: false);
                }

                if (verdict.Plan == ParagraphPlan.AskThenCover && !AskCoverParagraph(verdict.Reason))
                {
                    // Nothing was changed; give the typing back rather than lose it.
                    OpenParagraphEditor(pageIdx, st, newText, firstOpen: false);
                    return;
                }

                var edit = BuildParagraphOverlay(pageIdx, st, tb, newText, tbColor, paper: null, out var newLines);
                AddAnnotation(edit);
                int moved = RemapParagraphMarks(pageIdx, st, newText, newLines, out int dropped);
                RenderAllAnnotations(pageIdx);
                SetStatus(DescribeOverlay(edit, verdict.Reason, moved, dropped));
                WarnIfGlyphsWillBeLost(edit.FontName, newText);
            }
            catch (Exception ex)
            {
                SetStatus($"Paragraph edit error: {ex.Message}");
            }
            finally
            {
                if (_activeTextBox is null) RestoreTextDefaultsAfterEdit();
            }
        }

        private static string DescribeOverlay(TextEditAnnotation edit, string reason, int moved, int dropped)
        {
            string text = reason.Length > 0
                ? $"Paragraph redrawn over the original because {reason}"
                : "Paragraph redrawn over the original";
            if (moved > 0) text += $"; {moved} highlight{(moved == 1 ? "" : "s")} moved with it";
            if (dropped > 0) text += $"; {dropped} lost the words they covered";
            return text;
        }

        /// <summary>"Cover it and draw the new text over it?" with a Don't ask again box.</summary>
        private bool AskCoverParagraph(string reason)
        {
            string detail = reason.Length > 0 ? $"\n\nWhy: {reason}." : "";
            var (result, ticked) = TdpDialog.ShowWithCheckbox(
                this,
                "This paragraph can't be rewritten in its own font. Cover it and draw the new text over it?" + detail +
                "\n\nThe original words stay in the file under the cover.",
                "Don't ask again",
                "Edit Paragraph",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return false;
            if (ticked)
            {
                TDPdf.Properties.Settings.Default.SkipParagraphCoverPrompt = true;
                TDPdf.Properties.Settings.Default.Save();
            }
            return true;
        }

        // ---- in place ----------------------------------------------------------------------------

        private PdfiumInterop.ParagraphRequest BuildParagraphRequest(PdfPage page, ParagraphEditState st, int pageIdx, string newText)
        {
            var b = st.Block;
            double X(double x) => PdfPageGeometry.TextFrameToPdf(page, new PdfiumInterop.PdfRect(x, 0, x + 1, 1)).Left;
            return new PdfiumInterop.ParagraphRequest
            {
                PageIndex = pageIdx,
                Bounds = ParagraphPdfBox(page, b),
                NewText = newText,
                FirstLineLeft = X(b.Lines[0].Left),
                ColumnLeft = X(b.ColumnLeft),
                ColumnWidth = Math.Max(b.ColumnRight - b.ColumnLeft, 1),
                FirstLineOffset = b.FirstLineOffset,
                Alignment = b.Alignment,
                Pitch = b.Pitch > 0 ? b.Pitch : b.FontSize * 1.2,
            };
        }

        private bool TryRewriteParagraph(TextEditContext ctx, ParagraphEditState st, string newText, out string? why)
        {
            why = null;
            if (_doc is null || _currentFile is null) return false;
            int pageIdx = ctx.PageIndex;
            if (pageIdx < 0 || pageIdx >= _doc.PageCount) return false;

            string source = NewTempPath("para_src");
            string result = NewTempPath("para_out");
            try
            {
                // Against the CURRENT document, structural edits and all, like the line editor.
                NormalizeLiveDocumentForTool();
                _doc.Save(source);
                var page = _doc.Pages[pageIdx];
                var request = BuildParagraphRequest(page, st, pageIdx, newText);
                var outcome = PdfParagraphEdit.Apply(source, result, request, _doc);
                if (!outcome.Ok) { why = outcome.Error; return false; }

                // One undo step: the document, and the highlights that move with the paragraph.
                var newLines = LayOutRewrittenLines(page, st, outcome.Lines);
                PushDocUndoWithPageMarks(pageIdx);
                var before = SnapshotPageMarks(pageIdx);
                int moved = 0, dropped = 0;
                try
                {
                    moved = RemapMarksOnto(pageIdx, st, newText, newLines, out dropped);
                    AdoptEditedFile(result);
                }
                catch
                {
                    RestorePageMarks(pageIdx, before);
                    throw;
                }
                RenderAllAnnotations(pageIdx);

                string text = outcome.Lines.Count == st.Block.Lines.Count
                    ? "Paragraph rewritten in the document itself"
                    : $"Paragraph rewritten in the document itself ({st.Block.Lines.Count} lines to {outcome.Lines.Count})";
                if (st.Block.Alignment == ParagraphAlignment.Justified)
                    text += "; its lines are set flush left, not justified";
                if (moved > 0) text += $"; {moved} highlight{(moved == 1 ? "" : "s")} moved with it";
                if (dropped > 0) text += $"; {dropped} lost the words they covered";
                SetStatus(text);
                return true;
            }
            catch (Exception ex)
            {
                why = ex.Message;
                return false;
            }
            finally
            {
                TempFilePolicy.TryDelete(source);
                if (File.Exists(result) && !string.Equals(result, _currentFile, StringComparison.OrdinalIgnoreCase))
                    TempFilePolicy.TryDelete(result);
            }
        }

        /// <summary>The old lines of the paragraph as a highlight layout, in canvas pixels.</summary>
        private static List<HighlightLine> OldHighlightLines(ParagraphEditState st)
        {
            var list = new List<HighlightLine>();
            for (int i = 0; i < st.Block.Lines.Count; i++)
            {
                var l = st.Block.Lines[i];
                list.Add(new HighlightLine(
                    st.Block.LineStarts[i], l.Left * st.Sx, st.RenderH - l.Top * st.Sy,
                    l.Right * st.Sx, st.RenderH - l.Bottom * st.Sy,
                    l.Chars.Select(c => c.Left * st.Sx).ToArray(),
                    l.Chars.Select(c => c.Right * st.Sx).ToArray()));
            }
            return list;
        }

        /// <summary>The lines the engine wrote, as a highlight layout in canvas pixels.</summary>
        private List<HighlightLine> LayOutRewrittenLines(PdfPage page, ParagraphEditState st,
            IReadOnlyList<PdfiumInterop.ParagraphLineResult> lines)
        {
            var first = st.Block.Lines[0];
            double top0 = st.RenderH - first.Top * st.Sy;
            double height = (first.Top - first.Bottom) * st.Sy;
            double pitch = (st.Block.Pitch > 0 ? st.Block.Pitch : st.Block.FontSize * 1.2) * st.Sy;
            var list = new List<HighlightLine>();
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                // PDF x back to the displayed frame, then to the canvas.
                double leftTf = PdfPageGeometry.PdfToTextFrame(page, new PdfiumInterop.PdfRect(l.Left, 0, l.Left + 1, 1)).Left;
                double left = leftTf * st.Sx;
                var cl = new double[l.Text.Length];
                var cr = new double[l.Text.Length];
                for (int k = 0; k < l.Text.Length; k++)
                {
                    cl[k] = left + l.Edges[k] * st.Sx;
                    cr[k] = left + l.Edges[k + 1] * st.Sx;
                }
                double top = top0 + i * pitch;
                list.Add(new HighlightLine(l.Start, left, top, cr.Length > 0 ? cr[^1] : left, top + height, cl, cr));
            }
            return list;
        }

        // ---- drawn over ----------------------------------------------------------------------------

        private Func<string, double> WpfMeasure(string family, double sizePx, bool bold, bool italic)
        {
            double dpi = VisualTreeHelper.GetDpi(_annotationCanvas).PixelsPerDip;
            var typeface = new Typeface(new FontFamily(family),
                italic ? FontStyles.Italic : FontStyles.Normal,
                bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            return s => s.Length == 0 ? 0 : new FormattedText(
                s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, sizePx, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;
        }

        /// <summary>
        /// The cover-and-redraw version of a paragraph edit: a text edit mark with one line per
        /// reflowed line, set in an installed font that can draw what was typed.
        /// </summary>
        private TextEditAnnotation BuildParagraphOverlay(
            int pageIdx, ParagraphEditState st, TextBox tb, string newText, Color color,
            (byte R, byte G, byte B)? paper, out List<HighlightLine> layout)
        {
            var b = st.Block;
            string family = FontCoverage.PickFamily(tb.FontFamily.Source, newText);
            bool bold = tb.FontWeight == FontWeights.Bold, italic = tb.FontStyle == FontStyles.Italic;
            double sizePx = tb.FontSize;
            var measure = WpfMeasure(family, sizePx, bold, italic);

            double colLeft = b.ColumnLeft * st.Sx;
            double colWidth = Math.Max((b.ColumnRight - b.ColumnLeft) * st.Sx, 8);
            var lines = ParagraphReflow.Break(newText, colWidth, colWidth, measure);

            var first = b.Lines[0];
            double top0 = st.RenderH - first.Top * st.Sy;
            double lineBox = Math.Max((first.Top - first.Bottom) * st.Sy, sizePx);
            double pitch = (b.Pitch > 0 ? b.Pitch : b.FontSize * 1.2) * st.Sy;

            int align = b.Alignment switch { ParagraphAlignment.Centered => 1, ParagraphAlignment.Right => 2, _ => 0 };

            layout = new List<HighlightLine>();
            for (int i = 0; i < lines.Count; i++)
            {
                double w = measure(lines[i].Text);
                double left = colLeft + ParagraphReflow.LeftOffset(b.Alignment, w, colWidth);
                layout.Add(ParagraphHighlights.LayOut(lines[i].Start, lines[i].Text, left, top0 + i * pitch, top0 + i * pitch + lineBox, measure));
            }

            var edit = new TextEditAnnotation
            {
                PageIndex = pageIdx,
                OriginalBounds = st.CanvasBox,
                Position = new Point(colLeft, top0),
                NewContent = string.Join("\n", lines.Select(l => l.Text)),
                OriginalContent = b.Text,
                FontSize = sizePx,
                FontName = family,
                Bold = bold,
                Italic = italic,
                LineHeight = pitch,
                Align = align,
            };
            edit.SetColor(color);
            if (paper is { } p) edit.SetCoverColor(p.R, p.G, p.B);
            return edit;
        }

        /// <summary>Re-breaks a paragraph overlay's text into its box after it was edited again.</summary>
        private string ReflowParagraphOverlay(TextEditAnnotation tea, string text)
        {
            var measure = WpfMeasure(tea.FontName, tea.FontSize, tea.Bold, tea.Italic);
            double width = Math.Max(tea.OriginalBounds.Width, 8);
            return string.Join("\n", ParagraphReflow.Break(text, width, width, measure).Select(l => l.Text));
        }

        // ---- recognised text over a scan --------------------------------------------------------------

        /// <summary>
        /// The words on a scan are pixels; the text layer over them is invisible. The change is drawn
        /// over the picture with a cover in the paper's colour, and the invisible layer is rewritten
        /// so search and copy find the new words.
        /// </summary>
        private void CommitScanParagraph(TextEditContext ctx, ParagraphEditState st, TextBox tb, string newText, Color color)
        {
            int pageIdx = ctx.PageIndex;
            var paper = SamplePaper(pageIdx, st);
            var edit = BuildParagraphOverlay(pageIdx, st, tb, newText, color, paper, out var layout);

            string? layerNote = null;
            bool layerDone = false;
            if (_doc is not null && _currentFile is not null && PdfiumInterop.CanRewriteParagraphs && !st.Facts.PageUnsafe)
            {
                string source = NewTempPath("scan_src");
                string result = NewTempPath("scan_out");
                try
                {
                    NormalizeLiveDocumentForTool();
                    _doc.Save(source);
                    var page = _doc.Pages[pageIdx];
                    if (PdfPageGeometry.Rotation(page) != 0)
                        layerNote = "the page is turned";
                    else
                    {
                        var b = st.Block;
                        var preset = new List<PdfiumInterop.ParagraphPresetLine>();
                        var edited = edit.NewContent.Split('\n');
                        for (int i = 0; i < edited.Length; i++)
                        {
                            var hl = layout[i];
                            double leftTf = hl.Left / st.Sx;
                            double baselineTf = b.Lines[0].Baseline - i * (b.Pitch > 0 ? b.Pitch : b.FontSize * 1.2);
                            var pos = PdfPageGeometry.TextFrameToPdf(page,
                                new PdfiumInterop.PdfRect(leftTf, baselineTf, leftTf + 1, baselineTf + 1));
                            preset.Add(new PdfiumInterop.ParagraphPresetLine(edited[i], pos.Left, pos.Bottom, b.FontSize));
                        }
                        var request = new PdfiumInterop.ParagraphRequest
                        {
                            PageIndex = pageIdx,
                            Bounds = ParagraphPdfBox(page, b),
                            NewText = newText,
                            Invisible = preset,
                        };
                        var outcome = PdfParagraphEdit.Apply(source, result, request, _doc);
                        if (outcome.Ok)
                        {
                            PushDocUndoWithPageMarks(pageIdx);
                            var before = SnapshotPageMarks(pageIdx);
                            try
                            {
                                if (!_annotations.ContainsKey(pageIdx)) _annotations[pageIdx] = [];
                                _annotations[pageIdx].Add(edit);
                                RemapMarksOnto(pageIdx, st, newText, layout, out _);
                                AdoptEditedFile(result);
                                layerDone = true;
                            }
                            catch
                            {
                                RestorePageMarks(pageIdx, before);
                                throw;
                            }
                        }
                        else layerNote = outcome.Error;
                    }
                }
                catch (Exception ex)
                {
                    layerNote = ex.Message;
                }
                finally
                {
                    TempFilePolicy.TryDelete(source);
                    if (File.Exists(result) && !string.Equals(result, _currentFile, StringComparison.OrdinalIgnoreCase))
                        TempFilePolicy.TryDelete(result);
                }
            }
            else if (st.Facts.PageUnsafe) layerNote = "rewriting this page would damage other content on it";
            else layerNote = "this build cannot rewrite text";

            if (!layerDone)
            {
                AddAnnotation(edit);
                RemapParagraphMarks(pageIdx, st, newText, layout, out _);
            }
            RenderAllAnnotations(pageIdx);

            SetStatus(layerDone
                ? "Paragraph drawn over the scan, and the searchable text under it updated"
                : $"Paragraph drawn over the scan; the searchable text under it was not changed because {layerNote}");
            WarnIfGlyphsWillBeLost(edit.FontName, newText);
        }

        /// <summary>The paper's colour just outside the paragraph, from the page as rendered; white when it cannot be read.</summary>
        private (byte R, byte G, byte B) SamplePaper(int pageIdx, ParagraphEditState st)
        {
            try
            {
                // Rendered afresh at canvas size, not read from what is on screen: the screen copy may be the
                // inverted night-mode picture, and which page it holds depends on the view mode.
                var px = PdfiumInterop.RenderPageWithAnnotations(
                    _currentFile!, pageIdx, st.RenderW, st.RenderH, includeFormFields: false);
                if (px is null || px.Length < st.RenderW * st.RenderH * 4) return (255, 255, 255);
                return PaperColor.Sample(px, st.RenderW, st.RenderH, st.RenderW * 4,
                    (int)Math.Floor(st.CanvasBox.Left), (int)Math.Floor(st.CanvasBox.Top),
                    (int)Math.Ceiling(st.CanvasBox.Right), (int)Math.Ceiling(st.CanvasBox.Bottom));
            }
            catch
            {
                return (255, 255, 255);
            }
        }

        // ---- the marks that follow the paragraph --------------------------------------------------------

        private List<PageAnnotation> SnapshotPageMarks(int pageIdx) =>
            CloneList(_annotations.TryGetValue(pageIdx, out var l) ? l : null);

        private void RestorePageMarks(int pageIdx, List<PageAnnotation> snapshot) => _annotations[pageIdx] = snapshot;

        /// <summary>A document undo entry that also holds the page's marks as they are now.</summary>
        private void PushDocUndoWithPageMarks(int pageIdx)
        {
            if (_doc is null) return;
            if (HasPendingFormValues) WriteFormValuesToDocument();
            using var ms = new MemoryStream();
            _doc.Save(ms);
            _undoStack.Clear();
            _redoStack.Clear();
            _undoStack.AddLast(new UndoEntry(UndoKind.Document, pageIdx, DocBytes: ms.ToArray(), KeepMarks: true,
                PageAnnotations: SnapshotPageMarks(pageIdx)));
        }

        /// <summary>Same as <see cref="RemapMarksOnto"/>, for callers that snapshot the marks themselves.</summary>
        private int RemapParagraphMarks(int pageIdx, ParagraphEditState st, string newText,
            IReadOnlyList<HighlightLine> newLines, out int dropped) =>
            RemapMarksOnto(pageIdx, st, newText, newLines, out dropped);

        /// <summary>
        /// Moves the page's unsaved highlights, underlines and strikethroughs that lie on the
        /// paragraph onto its new lines. Returns how many moved; <paramref name="dropped"/> counts the
        /// ones whose words are gone.
        /// </summary>
        private int RemapMarksOnto(int pageIdx, ParagraphEditState st, string newText,
            IReadOnlyList<HighlightLine> newLines, out int dropped)
        {
            dropped = 0;
            if (!_annotations.TryGetValue(pageIdx, out var list)) return 0;
            var targets = list.OfType<HighlightAnnotation>().Where(h => h is MarkupAnnotation || h.ColorA < 250).ToList();
            if (targets.Count == 0) return 0;

            var oldLines = OldHighlightLines(st);
            int moved = 0;
            foreach (var h in targets)
            {
                var boxes = h is MarkupAnnotation m && m.LineRects.Count > 0
                    ? m.LineRects.Select(r => new CanvasBox(r.X, r.Y, r.Width, r.Height)).ToList()
                    : new List<CanvasBox> { new(h.Bounds.X, h.Bounds.Y, h.Bounds.Width, h.Bounds.Height) };

                var remapped = ParagraphHighlights.Remap(st.Block.Text, oldLines, newText, newLines, boxes);
                if (!remapped.Any(r => r.Touched)) continue;

                var rects = remapped.Where(r => r.Kept).SelectMany(r => r.Boxes)
                    .Select(b => new Rect(b.X, b.Y, b.Width, b.Height)).ToList();
                if (rects.Count == 0)
                {
                    list.Remove(h);
                    dropped++;
                    continue;
                }

                if (h is MarkupAnnotation markup)
                {
                    markup.LineRects = rects;
                    markup.SyncBounds();
                }
                else if (rects.Count == 1)
                {
                    h.Bounds = rects[0];
                }
                else
                {
                    // A rectangle highlight that now runs over several lines becomes a line-hugging one.
                    var replacement = new MarkupAnnotation
                    {
                        PageIndex = h.PageIndex, Style = MarkupStyle.Highlight,
                        ColorR = h.ColorR, ColorG = h.ColorG, ColorB = h.ColorB, ColorA = h.ColorA,
                        GroupId = h.GroupId,
                    };
                    replacement.LineRects.AddRange(rects);
                    replacement.SyncBounds();
                    int at = list.IndexOf(h);
                    if (at >= 0) list[at] = replacement;
                }
                moved++;
            }
            return moved;
        }
    }
}
