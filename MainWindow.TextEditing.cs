// MainWindow — text editing.
// Inline editing of existing PDF text (double-click) and the placed text-box tool.
// Both regions extracted verbatim from MainWindow.xaml.cs, in source order.

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
        // Inline text editing (double-click)
        // ============================================================

        private void EditTextAtPosition(Point canvasPos, int pageIdx)
        {
            if (_currentFile is null || !_renderDims.ContainsKey(pageIdx)) return;
            ClearSelection();

            // Commit any existing edit first
            if (_activeTextBox is not null)
            {
                CommitActiveTextBox();
                return;
            }

            // Re-edit an already-committed TextEditAnnotation without re-reading the PDF.
            // Without this check, a second double-click would read the original file, produce
            // a duplicate whiteout+text layer, and cause the "overlapping quasi-duplicates" bug.
            if (_annotations.TryGetValue(pageIdx, out var existingPage))
            {
                var existingEdit = existingPage.OfType<TextEditAnnotation>()
                    .FirstOrDefault(a => TextEditHitBounds(a).Contains(canvasPos));
                if (existingEdit is not null)
                {
                    // Seed the tool-default style fields from what's actually on this run, so the
                    // style bar (shown below) reflects its real style rather than stale leftovers
                    // from whatever was last edited — same as PlaceTextBox's re-edit path (#135).
                    _textFontFamily = existingEdit.FontName;
                    _textFontSize = existingEdit.FontSize;
                    _textBold = existingEdit.Bold;
                    _textItalic = existingEdit.Italic;
                    _textColor = existingEdit.GetColor();

                    var reb = existingEdit.OriginalBounds;
                    var retb = new TextBox
                    {
                        Text = existingEdit.NewContent,
                        Background = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
                        Foreground = new SolidColorBrush(_textColor),
                        BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                        BorderThickness = new Thickness(2),
                        FontFamily = new FontFamily(existingEdit.FontName),
                        FontSize = Math.Max(existingEdit.FontSize, 10),
                        FontWeight = existingEdit.Bold ? FontWeights.Bold : FontWeights.Normal,
                        FontStyle = existingEdit.Italic ? FontStyles.Italic : FontStyles.Normal,
                        MinWidth = Math.Max(reb.Width + 20, 100),
                        Height = Math.Max(reb.Height + 12, 24),
                        Padding = new Thickness(2, 0, 2, 0),
                        VerticalContentAlignment = VerticalAlignment.Center,
                        AcceptsReturn = false,
                        Tag = new TextEditContext
                        {
                            PageIndex = pageIdx,
                            OriginalText = existingEdit.OriginalContent,
                            CanvasBounds = reb,
                            Position = existingEdit.Position,
                            FontSize = existingEdit.FontSize,
                            FontName = existingEdit.FontName,
                            Bold = existingEdit.Bold,
                            Italic = existingEdit.Italic,
                            ExistingAnnotation = existingEdit
                        }
                    };
                    Canvas.SetLeft(retb, reb.X);
                    Canvas.SetTop(retb, reb.Y);
                    _textEditorCanvas.Children.Add(retb);
                    _activeTextBox = retb;
                    // Neither this re-edit branch nor the fresh-hit branch below ever called this —
                    // the style bar (font/color/bold/italic) never appeared for the Edit-Text tool
                    // at all, which is what made it look like it "didn't reappear" on reselect.
                    ShowTextSettings();
                    var rewo = new Rectangle
                    {
                        Fill = Brushes.White,
                        Width = reb.Width + 2,
                        Height = reb.Height + 2,
                        IsHitTestVisible = false,
                        Tag = "EditWhiteout"
                    };
                    Canvas.SetLeft(rewo, reb.X - 1);
                    Canvas.SetTop(rewo, reb.Y - 1);
                    _textEditorCanvas.Children.Insert(_textEditorCanvas.Children.IndexOf(retb), rewo);
                    retb.KeyDown += EditTextBox_KeyDown;
                    FocusTextEditorWhenLoaded(retb, selectAll: true, EditTextBox_LostFocus);
                    SetStatus("Re-editing text — Enter to save, Escape to cancel");
                    return;
                }
            }

            try
            {
                var (renderW, renderH) = _renderDims[pageIdx];
                var hit = _contentEditor.FindTextRunAt(_currentFile, pageIdx, canvasPos, renderW, renderH);
                if (hit is null) { SetStatus("No text found at this position"); return; }

                // Seed the tool-default style fields from what was detected on this run (see the
                // matching comment in the re-edit branch above). TextRunHit doesn't carry a
                // detected color — PDF text color isn't recovered here — so black, matching the
                // hardcoded Foreground this replaces.
                _textFontFamily = hit.FontName;
                _textFontSize = hit.FontSize;
                _textBold = hit.Bold;
                _textItalic = hit.Italic;
                _textColor = Colors.Black;

                // Show editable TextBox over the line
                var tb = new TextBox
                {
                    Text = hit.Text,
                    Background = FrozenSolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
                    Foreground = new SolidColorBrush(_textColor),
                    BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                    BorderThickness = new Thickness(2),
                    FontFamily = new FontFamily(hit.FontName),
                    FontSize = hit.FontSize,
                    // PDF fonts encode bold/italic in the font name; leaving WPF to default these to
                    // Normal made every styled line go plain the moment it was double-clicked (#182).
                    FontWeight = hit.Bold ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle = hit.Italic ? FontStyles.Italic : FontStyles.Normal,
                    MinWidth = Math.Max(hit.CanvasBounds.Width + 20, 100),
                    Height = Math.Max(hit.CanvasBounds.Height + 12, 24),
                    Padding = new Thickness(2, 0, 2, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    AcceptsReturn = false,
                    Tag = new TextEditContext
                    {
                        PageIndex = pageIdx,
                        OriginalText = hit.Text,
                        CanvasBounds = hit.CanvasBounds,
                        Position = hit.Position,
                        FontSize = hit.FontSize,
                        FontName = hit.FontName,
                        Bold = hit.Bold,
                        Italic = hit.Italic
                    }
                };
                Canvas.SetLeft(tb, hit.CanvasBounds.X);
                Canvas.SetTop(tb, hit.CanvasBounds.Y);
                _textEditorCanvas.Children.Add(tb);
                _activeTextBox = tb;
                ShowTextSettings();

                // Show white-out behind the edit box so original text is hidden
                var whiteout = new Rectangle
                {
                    Fill = Brushes.White,
                    Width = hit.CanvasBounds.Width + 2,
                    Height = hit.CanvasBounds.Height + 2,
                    IsHitTestVisible = false,
                    Tag = "EditWhiteout"
                };
                Canvas.SetLeft(whiteout, hit.CanvasBounds.X - 1);
                Canvas.SetTop(whiteout, hit.CanvasBounds.Y - 1);
                int tbIdx = _textEditorCanvas.Children.IndexOf(tb);
                _textEditorCanvas.Children.Insert(tbIdx, whiteout);

                tb.KeyDown += EditTextBox_KeyDown;
                FocusTextEditorWhenLoaded(tb, selectAll: true, EditTextBox_LostFocus);

                SetStatus("Editing text - Enter to save, Escape to cancel");
            }
            catch (Exception ex)
            {
                SetStatus($"Text edit error: {ex.Message}");
            }
        }

        /// <summary>Context data attached to an inline text edit TextBox via Tag.</summary>
        private class TextEditContext
        {
            public int PageIndex { get; set; }
            public string OriginalText { get; set; } = "";
            public Rect CanvasBounds { get; set; }
            public Point Position { get; set; }
            public double FontSize { get; set; }
            public string FontName { get; set; } = "Segoe UI";
            /// <summary>Face styling detected on the source PDF text (#182).</summary>
            public bool Bold { get; set; }
            public bool Italic { get; set; }
            /// <summary>Non-null when re-editing an already-committed annotation; update in place instead of adding a new one.</summary>
            public TextEditAnnotation? ExistingAnnotation { get; set; }
        }

        private void EditTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CancelTextEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                CommitTextEdit();
                e.Handled = true;
            }
        }

        private void EditTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb
                && ReferenceEquals(_activeTextBox, tb)
                && tb.Tag is TextEditContext)
            {
                _ = Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () =>
                    {
                        if (!ReferenceEquals(_activeTextBox, tb)) return;
                        // Same reasoning as TextBox_LostFocus: the style bar is part of this same
                        // edit, not a click-away.
                        if (Keyboard.FocusedElement is DependencyObject nf && _textSettingsBar is not null
                            && IsDescendantOf(nf, _textSettingsBar))
                            return;
                        CommitTextEdit();
                    });
            }
        }

        private void CancelTextEdit()
        {
            if (_activeTextBox is null) return;
            var tb = _activeTextBox;
            _activeTextBox = null;
            RemoveTextEditorElement(tb);
            // Remove the whiteout rectangle
            var whiteout = _textEditorCanvas.Children.OfType<Rectangle>()
                .FirstOrDefault(r => r.Tag is string s && s == "EditWhiteout");
            if (whiteout is not null)
               _textEditorCanvas.Children.Remove(whiteout);
            SetStatus("Text edit cancelled");
        }

        private void CommitTextEdit()
        {
            if (_activeTextBox is null || _activeTextBox.Tag is not TextEditContext ctx) return;
            var tb = _activeTextBox;
            _activeTextBox = null;
            string newText = tb.Text.Trim();
            RemoveTextEditorElement(tb);

            // Remove the whiteout rectangle
            var whiteout = _textEditorCanvas.Children.OfType<Rectangle>()
                .FirstOrDefault(r => r.Tag is string s && s == "EditWhiteout");
            if (whiteout is not null)
               _textEditorCanvas.Children.Remove(whiteout);

            if (string.IsNullOrEmpty(newText))
            {
                SetStatus("Text edit cancelled (empty)");
                return;
            }

            // The style bar can change font/size/color/bold/italic without the wording changing at
            // all (e.g. just recoloring existing text) — bailing out purely on unchanged TEXT used
            // to silently discard every style-only edit before it ever reached the code below.
            Color tbColor = tb.Foreground is SolidColorBrush scb ? scb.Color : Colors.Black;
            bool styleChanged = tb.FontFamily.Source != ctx.FontName
                || Math.Abs(tb.FontSize - ctx.FontSize) > 0.01
                || (tb.FontWeight == FontWeights.Bold) != ctx.Bold
                || (tb.FontStyle == FontStyles.Italic) != ctx.Italic
                || tbColor != (ctx.ExistingAnnotation?.GetColor() ?? Colors.Black);
            if (newText == ctx.OriginalText && !styleChanged)
            {
                SetStatus("No changes made");
                return;
            }

            string? whyNot = null;
            if (ctx.ExistingAnnotation is not null)
            {
                // Update the existing annotation in place — avoids duplicate whiteout layers.
                // Style is read back off the live box, not the pre-edit ctx values — the user may
                // have changed font/size/color/bold/italic in the style bar mid-edit, and the box
                // in front of them is the truth (same reasoning as CommitActiveTextBox's #135 fix).
                //
                // Re-editing an existing overlay stays an overlay: the original text is still under
                // it, and the run this annotation replaced is no longer the thing being changed.
                PushPageSnapshot(ctx.ExistingAnnotation.PageIndex);
                ctx.ExistingAnnotation.NewContent = newText;
                ctx.ExistingAnnotation.FontSize = tb.FontSize;
                ctx.ExistingAnnotation.FontName = tb.FontFamily.Source;
                ctx.ExistingAnnotation.Bold = tb.FontWeight == FontWeights.Bold;
                ctx.ExistingAnnotation.Italic = tb.FontStyle == FontStyles.Italic;
                ctx.ExistingAnnotation.SetColor(tbColor);
                MarkDirty();
            }
            // A style change cannot be done in place — see TryEditTextInPlace — so it goes straight
            // to the overlay without a round trip through PDFium that could only fail.
            else if (!styleChanged && TryEditTextInPlace(ctx, newText, out whyNot))
            {
                // Done for real: the original words are gone from the file, so there is nothing to
                // draw over and no annotation to keep.
                SetStatus($"Replaced \"{ctx.OriginalText}\" with \"{newText}\" in the document itself");
                return;
            }
            else
            {
                if (whyNot is not null) _pendingOverlayReason = whyNot;
                var edit = new TextEditAnnotation
                {
                    PageIndex = ctx.PageIndex,
                    OriginalBounds = ctx.CanvasBounds,
                    Position = ctx.Position,
                    NewContent = newText,
                    OriginalContent = ctx.OriginalText,
                    FontSize = tb.FontSize,
                    FontName = tb.FontFamily.Source,
                    Bold = tb.FontWeight == FontWeights.Bold,
                    Italic = tb.FontStyle == FontStyles.Italic
                };
                edit.SetColor(tbColor);
                AddAnnotation(edit);
            }
            RenderAllAnnotations(ctx.PageIndex);
            // Say WHICH kind of edit this was. The overlay leaves the original text in the file —
            // fine for a correction, not fine if the user thought the old words had gone — so the
            // difference is never left implicit.
            SetStatus(_pendingOverlayReason is null
                ? $"Text edited: \"{ctx.OriginalText}\" -> \"{newText}\" (drawn over the original)"
                : $"Text edited: \"{ctx.OriginalText}\" -> \"{newText}\" — drawn over the original because {_pendingOverlayReason}");
            _pendingOverlayReason = null;
            // #168: the in-place editor starts from whatever the PDF already says, so it is the path
            // most likely to carry non-Latin text. Warn on the family the burn will actually use.
            WarnIfGlyphsWillBeLost(ctx.ExistingAnnotation?.FontName ?? ctx.FontName, newText);
        }

        /// <summary>Why the last edit fell back to the overlay, for the status line.</summary>
        private string? _pendingOverlayReason;

        /// <summary>
        /// Replaces the run's text in the document itself, rather than drawing over it.
        /// </summary>
        /// <remarks>
        /// The real thing: the original words stop existing in the file. Everything TDPdf did
        /// before this — a white rectangle over the old text with the new text on top — left the
        /// original in place, selectable and extractable, which is fine for a correction and wrong
        /// if anyone believed the old wording had gone.
        ///
        /// It is NOT always possible, and the two reasons are worth keeping straight:
        ///
        ///   * <b>The font.</b> Most PDFs embed a subset, so a document that never contained a "Z"
        ///     has no "Z" to draw with. <see cref="TDPdf.Services.PdfTextEdit"/> checks the finished
        ///     file and refuses rather than shipping blanks.
        ///   * <b>A style change.</b> In-place editing keeps the run's own font, size and colour —
        ///     that is exactly why the result matches the rest of the line — so it cannot honour a
        ///     different font or colour picked in the style bar. Those keep the overlay, which can.
        ///
        /// Either way the caller falls back to the annotation, which is uglier and always works.
        /// </remarks>
        private bool TryEditTextInPlace(TextEditContext ctx, string newText, out string? whyNot)
        {
            whyNot = null;
            if (_doc is null || _currentFile is null) return false;
            if (ctx.PageIndex < 0 || ctx.PageIndex >= _doc.PageCount) return false;
            if (string.Equals(newText, ctx.OriginalText, StringComparison.Ordinal)) return false;
            if (string.IsNullOrWhiteSpace(ctx.OriginalText)) return false;
            if (!TDPdf.Services.PdfiumInterop.CanEditText) { whyNot = "this build cannot edit text in place"; return false; }
            if (!_renderDims.TryGetValue(ctx.PageIndex, out var dims) || dims.w <= 0 || dims.h <= 0) return false;

            var page = _doc.Pages[ctx.PageIndex];
            var bounds = TDPdf.Services.PdfPageGeometry.CanvasRectToPdf(
                page, ctx.CanvasBounds.X, ctx.CanvasBounds.Y,
                ctx.CanvasBounds.Width, ctx.CanvasBounds.Height, dims.w, dims.h);

            string source = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tdpdf_edit_src_{Guid.NewGuid():N}.pdf");
            string result = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tdpdf_edit_out_{Guid.NewGuid():N}.pdf");
            try
            {
                // Against the CURRENT document, structural edits and all — the file on disk may be
                // several rotations and page deletions behind what is on screen.
                NormalizeDocumentForSave(_doc);
                _doc.Save(source);

                var outcome = TDPdf.Services.PdfTextEdit.Apply(
                    source, result,
                    new[] { new TDPdf.Services.PdfiumInterop.TextEditRequest(ctx.PageIndex, bounds, ctx.OriginalText, newText) },
                    _doc);

                if (!outcome.Ok)
                {
                    whyNot = outcome.UnsafePages.Count > 0
                        ? "editing this page in place would damage other content on it"
                        : outcome.Error;
                    return false;
                }

                PushDocUndo();
                AdoptEditedFile(result);
                return true;
            }
            catch (Exception ex)
            {
                whyNot = ex.Message;
                return false;
            }
            finally
            {
                TryDeleteTemp(source);
                if (System.IO.File.Exists(result)
                    && !string.Equals(result, _currentFile, StringComparison.OrdinalIgnoreCase))
                    TryDeleteTemp(result);
            }
        }

        /// <summary>
        /// Makes an edited file the working document, keeping the overlay annotations.
        /// </summary>
        /// <remarks>
        /// Unlike the redaction and structural-edit paths, the annotations SURVIVE. Those clear
        /// them because the page geometry moved underneath them — a crop, a rotation, a deleted
        /// page — and a canvas coordinate no longer means what it meant. Replacing the text inside
        /// a run moves nothing: same pages, same boxes, same size. Throwing away the user's
        /// unsaved highlights to change one word would be a poor trade for no safety at all.
        /// </remarks>
        private void AdoptEditedFile(string editedPath)
        {
            int selectedIdx = PageList.SelectedIndex;

            _doc?.Close();
            _doc = PdfReader.Open(editedPath, PdfDocumentOpenMode.Modify);
            _currentFile = editedPath;

            InvalidateRenderCache();
            _contentEditor.ClearCache();
            InvalidateTextRunCache();
            ClearSelection();
            MarkDirty();

            RefreshPageList();
            if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                PageList.SelectedIndex = selectedIdx;
            else if (PageList.Items.Count > 0)
                PageList.SelectedIndex = 0;

            if (_viewMode == ViewMode.Continuous)
            {
                int contIdx = Math.Max(0, PageList.SelectedIndex);
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => SetupContinuousView(contIdx)));
            }
        }

        /// <summary>
        /// #168: the editor borrows glyphs from any installed font, so text ALWAYS looks right while
        /// it is being typed - but a PDF can only EMBED whole fonts, and a character no installed
        /// font carries becomes an empty box in the saved file. That used to be invisible until the
        /// user saved, closed and reopened. Say it at the moment the text is placed, while it can
        /// still be fixed.
        ///
        /// Only fires when the whole fallback chain comes up short (a box mixing two non-Latin
        /// scripts, or a script with no font installed at all), so it does not nag: ordinary
        /// Japanese, Chinese, Korean, Thai or Devanagari text resolves silently.
        /// </summary>
        private void WarnIfGlyphsWillBeLost(string preferredFamily, string? text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // FontCoverage.PickFamily/UncoveredChars are synchronous disk I/O on a cache miss (its
            // own comment: "a miss costs a full read of the font file, and a CJK collection is
            // tens of megabytes"). This is called from CommitActiveTextBox/CommitTextEdit, which
            // run directly on the UI thread from the mouse-click handler that committed the box —
            // so a cold-cache lookup blocked every click on the page for however long that read
            // took, felt like a hang, and any clicks made during it queued up and landed on
            // whatever the UI looked like once it unblocked. Off the UI thread entirely; only the
            // status text and dialog (already deferred below) touch it.
            Task.Run(() =>
            {
                string missing;
                try
                {
                    string family = FontCoverage.PickFamily(preferredFamily, text);
                    missing = FontCoverage.UncoveredChars(family, text);
                }
                catch { return; /* the warning must never be the thing that breaks placing text */ }
                if (missing.Length == 0) return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (!IsLoaded || PresentationSource.FromVisual(this) is null) return;
                        SetStatus($"No installed font can draw: {missing} - these will save as empty boxes");
                        // Deferred rather than shown inline: CommitActiveTextBox / CommitTextEdit
                        // are the app's "settle any in-progress edit" chokepoint and run from
                        // inside save, print, close, tool-switch and tab-switch paths. A modal
                        // dialog on that stack would block the operation that asked for the settle.
                        // Background priority lets the caller finish, then raises the warning.
                        TdpDialog.Show(this,
                            "Some characters in this text have no glyph in any installed font:\n\n" +
                            missing + "\n\n" +
                            "They look right while you type, because Windows borrows a glyph per " +
                            "character from across your whole font set. A PDF can only embed whole " +
                            "fonts, so these will save as empty boxes.\n\n" +
                            "Installing a font that covers this script will fix it.",
                            "TDPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    catch { /* window went away between the commit and the dispatch */ }
                }), System.Windows.Threading.DispatcherPriority.Background);
            });
        }

        private void EditImageAtPosition(Point canvasPos, int pageIdx)
        {
            if (_currentFile is null || !_renderDims.ContainsKey(pageIdx)) return;

            if (_annotations.TryGetValue(pageIdx, out var pageAnnots))
            {
                for (int i = pageAnnots.Count - 1; i >= 0; i--)
                {
                    if (pageAnnots[i] is ImageEditAnnotation existing && existing.TargetBounds.Contains(canvasPos))
                    {
                        SelectAnnotation(existing, existing.TargetBounds);
                        ShowImageEditMenu(existing);
                        return;
                    }
                }
            }

            var (renderW, renderH) = _renderDims[pageIdx];
            var hit = _contentEditor.FindImageAt(_currentFile, pageIdx, canvasPos, renderW, renderH);
            if (hit is null)
            {
                SetStatus("No image found at this position");
                return;
            }

            var edit = new ImageEditAnnotation
            {
                PageIndex = pageIdx,
                OriginalBounds = hit.CanvasBounds,
                TargetBounds = hit.CanvasBounds,
                OriginalImageData = CapturePageImageRegion(hit.CanvasBounds)
            };
            AddAnnotation(edit);
            RenderAllAnnotations(pageIdx);
            SelectAnnotation(edit, edit.TargetBounds);
            ShowImageEditMenu(edit);
            SetStatus("Image selected - replace, delete, or drag the green handle to resize");
        }

        private string? CapturePageImageRegion(Rect bounds)
        {
            // #135: deliberately NOT PageImage.Source — that may be the inverted display copy, and
            // this capture is baked into the saved PDF.
            if (_primaryPageBitmap is not BitmapSource source) return null;

            int x = Math.Max(0, (int)Math.Floor(bounds.X));
            int y = Math.Max(0, (int)Math.Floor(bounds.Y));
            int right = Math.Min(source.PixelWidth, (int)Math.Ceiling(bounds.Right));
            int bottom = Math.Min(source.PixelHeight, (int)Math.Ceiling(bounds.Bottom));
            if (right <= x || bottom <= y) return null;

            var crop = new CroppedBitmap(source, new Int32Rect(x, y, right - x, bottom - y));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(crop));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return Convert.ToBase64String(ms.ToArray());
        }

        private void ShowImageEditMenu(ImageEditAnnotation edit)
        {
            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem("Replace Image...", (s, e) => ReplaceImageEdit(edit), null, null, "\uE91B"));
            menu.Items.Add(MakeMenuItem("Delete Image", (s, e) => DeleteImageEdit(edit), null, null, "\uE74D"));
            menu.Items.Add(MakeMenuItem("Reset Size", (s, e) => ResetImageEditSize(edit), null, null, "\uE72C"));
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Resize: drag the green handle" });
            menu.PlacementTarget = _annotationCanvas;
            menu.IsOpen = true;
        }

        private void ReplaceImageEdit(ImageEditAnnotation edit)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
                Title = "Select replacement image"
            };
            if (dlg.ShowDialog() != true) return;

            PushPageSnapshot(edit.PageIndex);
            edit.ReplacementImagePath = dlg.FileName;
            edit.IsDeleted = false;
            RenderAllAnnotations(edit.PageIndex);
            SelectAnnotation(edit, edit.TargetBounds);
            MarkDirty();
            SetStatus("Replacement image selected - save to apply white-out + overdraw");
        }

        private void DeleteImageEdit(ImageEditAnnotation edit)
        {
            PushPageSnapshot(edit.PageIndex);
            edit.IsDeleted = true;
            RenderAllAnnotations(edit.PageIndex);
            SelectAnnotation(edit, edit.TargetBounds);
            MarkDirty();
            SetStatus("Image marked for deletion - save to apply white-out");
        }

        private void ResetImageEditSize(ImageEditAnnotation edit)
        {
            PushPageSnapshot(edit.PageIndex);
            edit.TargetBounds = edit.OriginalBounds;
            RenderAllAnnotations(edit.PageIndex);
            SelectAnnotation(edit, edit.TargetBounds);
            MarkDirty();
            SetStatus("Image size reset");
        }

        private void ResizeImageEditPreview(Point pos)
        {
            if (_resizingImageEdit is null) return;

            double newW = Math.Max(8, _imageResizeOriginalBounds.Width + (pos.X - _imageResizeStart.X));
            double newH = Math.Max(8, _imageResizeOriginalBounds.Height + (pos.Y - _imageResizeStart.Y));
            _resizingImageEdit.TargetBounds = new Rect(_imageResizeOriginalBounds.X, _imageResizeOriginalBounds.Y, newW, newH);

            if (_selectionBorder is not null)
            {
                _selectionBorder.Width = newW + 8;
                _selectionBorder.Height = newH + 8;
            }
            if (_imageResizeHandle is not null)
            {
                Canvas.SetLeft(_imageResizeHandle, _resizingImageEdit.TargetBounds.Right - 2);
                Canvas.SetTop(_imageResizeHandle, _resizingImageEdit.TargetBounds.Bottom - 2);
            }
        }

        // ============================================================
        // Text box handling
        // ============================================================

        /// <summary>
        /// If a placed <see cref="TextAnnotation"/> lies under <paramref name="pos"/>, re-open it in the
        /// in-place editor (topmost first) and return true; otherwise return false.
        /// </summary>
        private bool TryReeditPlacedText(Point pos, int pageIdx)
        {
            CommitActiveTextBox();
            if (pageIdx < 0 || !_annotations.TryGetValue(pageIdx, out var list)) return false;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is TextAnnotation ta && HitTestAnnotation(ta, pos, out _))
                {
                    PlaceTextBox(ta.Position, pageIdx, ta);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Default wrap width (canvas px) for a newly placed text box.</summary>
        private const double DefaultTextBoxWidth = 220;

        /// <summary>Context attached to a placed-text editing TextBox via its Tag.</summary>
        private sealed class PlacedTextContext
        {
            public int PageIndex { get; init; }
            /// <summary>Non-null when re-editing an existing box: it was pulled from the list at edit-start and is restored on cancel.</summary>
            public TextAnnotation? Existing { get; init; }
        }

        private bool TryRestoreActiveTextBoxFocus(Point pos)
        {
            if (_activeTextBox is null || !ReferenceEquals(_activeTextBox.Parent, _textEditorCanvas))
                return false;

            TextBox textBox = _activeTextBox;
            double x = Canvas.GetLeft(textBox);
            double y = Canvas.GetTop(textBox);
            if (!IsFinite(x) || !IsFinite(y)) return false;

            double width = textBox.ActualWidth > 0 ? textBox.ActualWidth : textBox.Width;
            double height = textBox.ActualHeight > 0 ? textBox.ActualHeight : Math.Max(textBox.MinHeight, 24);
            if (pos.X < x || pos.X > x + width || pos.Y < y || pos.Y > y + height)
                return false;

            textBox.Focus();
            Keyboard.Focus(textBox);
            int characterIndex = textBox.GetCharacterIndexFromPoint(
                new Point(pos.X - x, pos.Y - y),
                snapToText: true);
            if (characterIndex >= 0)
                textBox.CaretIndex = characterIndex;

            Telemetry.TrackEvent("Annotation.TextEditorFocusRestored",
                new Dictionary<string, string>
                {
                    ["Type"] = "Text",
                    ["Focused"] = textBox.IsKeyboardFocusWithin ? "true" : "false"
                });
            return true;
        }

        private void RemoveTextEditorElement(UIElement element)
        {
            if (element is FrameworkElement { Parent: Panel parent })
                parent.Children.Remove(element);
            // The move grip (see PlaceTextBox) only ever exists alongside the active placed-text
            // editor, so whichever path is tearing that editor down also owns tearing this down —
            // one blanket cleanup here instead of touching every call site individually.
            if (_activeTextBoxGrip is { Parent: Panel gripParent } grip)
                gripParent.Children.Remove(grip);
            _activeTextBoxGrip = null;
        }

        private static Dictionary<string, string> TextEditorTelemetry(string outcome, string? via = null)
        {
            var props = new Dictionary<string, string>
            {
                ["Type"] = "Text",
                ["Outcome"] = outcome
            };
            // #129: which method asked for the commit. Four releases (1.23.3 – 1.23.6) chased this
            // as a focus bug on the strength of PlaceCompleted{Focused=true} followed ~30ms later by
            // TextEditorClosed{Outcome=Empty}; focus was never the problem, an unidentified caller of
            // the commit chokepoint was. A C# method name is a compile-time constant, so this cannot
            // leak document or user content and needs no scrubbing.
            if (!string.IsNullOrEmpty(via)) props["Via"] = via;
            return props;
        }

        /// <summary>
        /// How long a freshly placed, still-untouched text editor is protected from an
        /// <em>incidental</em> commit. See <see cref="CommitActiveTextBox"/>.
        /// </summary>
        private const double UntouchedEditorGraceMs = 400;

        /// <summary>When the live placed-text editor was created, for the grace window above.</summary>
        private DateTime _activeTextBoxPlacedUtc = DateTime.MinValue;

        /// <summary>Set on the first <c>TextChanged</c>, i.e. once the user has actually typed.</summary>
        private bool _activeTextBoxTouched;

        private void FocusTextEditorWhenLoaded(
            TextBox textBox,
            bool selectAll,
            RoutedEventHandler lostFocusHandler,
            Action<bool, bool>? completed = null)
        {
            bool completionReported = false;
            void Activate()
            {
                bool attached = ReferenceEquals(_activeTextBox, textBox)
                    && ReferenceEquals(textBox.Parent, _textEditorCanvas);
                if (attached)
                {
                    textBox.Focus();
                    Keyboard.Focus(textBox);
                    if (selectAll) textBox.SelectAll();
                    else textBox.CaretIndex = textBox.Text.Length;
                }

                if (!completionReported)
                {
                    completionReported = true;
                    completed?.Invoke(attached, textBox.IsKeyboardFocusWithin);
                }
            }

            textBox.LostFocus += lostFocusHandler;
            RoutedEventHandler? loadedHandler = null;
            loadedHandler = (_, _) =>
            {
                textBox.Loaded -= loadedHandler;
                Activate();
            };
            textBox.Loaded += loadedHandler;
            // Loaded may already have fired by the time a dynamically-added editor is wired up.
            // The unconditional fallback is idempotent and guarantees every editor gets an
            // activation attempt after the current mouse/layout pass either way.
            _ = textBox.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                (Action)Activate);
        }

        /// <summary>
        /// Opens the in-place editing TextBox for a new text annotation, or (when <paramref name="existing"/>
        /// is supplied) re-opens an already-placed one seeded with its content/size/colour/width/fill.
        /// </summary>
        private void PlaceTextBox(Point pos, int pageIdx, TextAnnotation? existing = null)
        {
            // #131: every caller offers the live editor to the commit chokepoint before getting
            // here. If one is STILL live, the untouched-editor grace refused that commit — and the
            // assignment below is about to overwrite _activeTextBox, stranding the refused box on
            // TextEditorCanvas with nothing referencing it. Nothing reaps that canvas except a tab
            // switch, so it would sit on the page, visible and unusable, for the rest of the
            // session. A brand-new editor nobody typed into has nothing worth preserving; discard
            // it. (Only a placed-text box can be in that state — the grace never defers an inline
            // PDF-text edit, and the inline path returns rather than replacing the editor.)
            //
            // Discard EXACTLY what the grace refused, and settle anything else properly. The
            // second branch is unreachable today for the reason above, but "we are about to
            // overwrite the field" must never become a licence to throw away text somebody typed.
            if (_activeTextBox is { } stale)
            {
                if (!_activeTextBoxTouched
                    && stale.Tag is PlacedTextContext { Existing: null }
                    && string.IsNullOrWhiteSpace(stale.Text))
                {
                    _activeTextBox = null;
                    RemoveTextEditorElement(stale);
                    Telemetry.TrackEvent("Annotation.TextEditorClosed",
                        TextEditorTelemetry("Empty", nameof(PlaceTextBox)));
                }
                else
                {
                    CommitActiveTextBox();
                }
            }

            double width = DefaultTextBoxWidth;
            if (existing is not null)
            {
                // Adopt the box's style so the box (and the Text-tool settings bar, if visible) reflect it.
                _textColor = existing.GetColor();
                _textFontSize = existing.FontSize;
                _textFontFamily = existing.FontName;
                _textWhiteout = existing.HasFill;
                _textBold = existing.Bold;              // #135
                _textItalic = existing.Italic;
                _textUnderline = existing.Underline;
                // #135 item 2: carried through tool state, not through the editor, for the reason
                // spelled out on the TextBox below — so a re-edit cannot silently reset it to 0.
                _textLetterSpacing = existing.LetterSpacing;
                if (existing.HasFill) _textFillColor = existing.GetFillColor();
                if (existing.Width > 0) width = existing.Width;

                // Pull the original out of the model for the duration of the edit (restored on cancel).
                // The snapshot captures the pre-edit state so undo restores it whichever way the edit ends.
                PushPageSnapshot(pageIdx);
                if (_annotations.TryGetValue(pageIdx, out var l0)) l0.Remove(existing);
                RenderAllAnnotations(pageIdx);
                if (_currentTool == EditTool.Text && _textSettingsBar is not null) ShowTextSettings();
            }

            var tb = new TextBox
            {
                Foreground = new SolidColorBrush(_textColor),
                BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily(_textFontFamily),
                FontSize = _textFontSize,
                // #135: a WPF TextBox carries all three natively, so the editor shows the real thing
                // rather than a preview of it.
                //
                // #135 item 2 — LETTER SPACING IS THE EXCEPTION, AND IT IS NOT FIXABLE HERE.
                // An editable TextBox owns its own text layout, caret hit-testing and selection
                // geometry; WPF exposes no letter-spacing property on it, and there is no honest
                // way to fake one (drawing spaced glyphs over the top would put the caret and the
                // selection highlight in the wrong places, which is worse than not showing it).
                // So while you are TYPING, the text is shown at its natural spacing. The spacing
                // you set is live in the style bar, is applied the instant the box commits, and
                // from then on the committed annotation and the saved PDF agree exactly — the
                // commit is the ONE moment the text can visibly reflow, and saving is never
                // another one. That is the deliberate trade: reflow once, where the user is
                // looking and has just pressed a key, rather than silently at save time.
                FontWeight = _textBold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = _textItalic ? FontStyles.Italic : FontStyles.Normal,
                TextDecorations = _textUnderline ? TextDecorations.Underline : null,
                CaretBrush = new SolidColorBrush(_textColor),
                SelectionBrush = (SolidColorBrush)FindResource("AccentGreen"),
                Width = width,
                MinHeight = existing is not null && existing.Height > 24 ? existing.Height : 24,
                Padding = new Thickness(2),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Text = existing?.Content ?? "",
                Tag = new PlacedTextContext { PageIndex = pageIdx, Existing = existing }
            };
            tb.Background = _textWhiteout
                ? FrozenSolidColorBrush(_textFillColor)
                : FrozenSolidColorBrush(Color.FromArgb(230, 255, 255, 255));
            AutomationProperties.SetName(tb, "Annotation text");
            AutomationProperties.SetHelpText(tb, "Type annotation text. Press Enter to save or Escape to cancel.");
            double maxX = Math.Max(0, _textEditorCanvas.Width - width);
            double maxY = Math.Max(0, _textEditorCanvas.Height - Math.Max(tb.MinHeight, 24));
            Canvas.SetLeft(tb, Math.Clamp(pos.X, 0, maxX));
            Canvas.SetTop(tb, Math.Clamp(pos.Y, 0, maxY));
            Telemetry.TrackEvent("Annotation.PlaceStarted",
                new Dictionary<string, string> { ["Type"] = "Text" });
            _activeTextBox = tb;
            _activeTextBoxPlacedUtc = DateTime.UtcNow;
            _activeTextBoxTouched = false;
            tb.KeyDown += TextBox_KeyDown;
            tb.PreviewMouseLeftButtonDown += (_, _) =>
            {
                tb.Focus();
                Keyboard.Focus(tb);
            };
            bool inputStarted = false;
            tb.TextChanged += (_, _) =>
            {
                if (ReferenceEquals(_activeTextBox, tb)) _activeTextBoxTouched = true;
                if (inputStarted) return;
                inputStarted = true;
                Telemetry.TrackEvent("Annotation.TextEditorInputStarted",
                    new Dictionary<string, string> { ["Type"] = "Text" });
            };
            SetStatus("Type your text, then press Enter to place it (Shift+Enter for a new line)");
            FocusTextEditorWhenLoaded(
                tb,
                selectAll: existing is not null,
                TextBox_LostFocus,
                (attached, focused) =>
                {
                    Telemetry.TrackEvent("Annotation.PlaceCompleted",
                        new Dictionary<string, string>
                        {
                            ["Type"] = "Text",
                            ["Attached"] = attached ? "true" : "false",
                            ["Focused"] = focused ? "true" : "false"
                        });
                });
            _textEditorCanvas.Children.Add(tb);

            // Telemetry from the field (session 3d521d3552e14bb0b9853db69306e844, 1.29.2.0) showed
            // the actual failure: every TextEditorClosed while the box was still empty fired with
            // Via=Canvas_MouseLeftButtonDown, over and over, a couple of seconds apart — someone
            // repeatedly clicking near a just-placed, still-empty box trying to reposition it before
            // typing. Clicking a TextBox only moves the caret; there was and is no way to drag it
            // while it's still the live editor, so every attempt discarded the box (a click while
            // EditTool.Text is active commits-then-places-a-new-one) and started over in the same
            // spot. This grip is a real drag target for exactly that moment, before Select-tool
            // auto-select (see CommitActiveTextBox) ever gets a chance to help.
            var grip = new Border
            {
                Width = 16,
                Height = 16,
                Background = (SolidColorBrush)FindResource("AccentGreen"),
                CornerRadius = new CornerRadius(8),
                // Grab affordance (#135 item 5): the open hand says "pick this up", and the drag
                // below swaps in GrabbingCursor for as long as it is held. SizeAll — which reads as
                // "stretch this" — is kept for the resize handles, which is what it means there.
                Cursor = Cursors.Hand,
                ToolTip = "Drag to move this box"
            };
            void PositionGrip(double left, double top)
            {
                Canvas.SetLeft(grip, left - 8);
                Canvas.SetTop(grip, top - 8);
            }
            PositionGrip(Canvas.GetLeft(tb), Canvas.GetTop(tb));
            bool draggingBox = false;
            Point dragAnchorScreen = default;
            double dragStartLeft = 0, dragStartTop = 0;
            // The closed hand is set on the GRIP rather than globally: the drag runs under a mouse
            // capture, so the grip owns the cursor for the whole gesture even once the pointer has
            // been carried off it. EndGripDrag is wired to LostMouseCapture as well as to button-up
            // so a capture WPF drops for its own reasons cannot strand the closed hand on screen.
            void EndGripDrag()
            {
                draggingBox = false;
                grip.Cursor = Cursors.Hand;
            }
            grip.PreviewMouseLeftButtonDown += (_, ev) =>
            {
                draggingBox = true;
                dragAnchorScreen = ev.GetPosition(_textEditorCanvas);
                dragStartLeft = Canvas.GetLeft(tb);
                dragStartTop = Canvas.GetTop(tb);
                grip.Cursor = GrabbingCursor;
                grip.CaptureMouse();
                ev.Handled = true;
            };
            grip.PreviewMouseMove += (_, ev) =>
            {
                if (!draggingBox) return;
                var now = ev.GetPosition(_textEditorCanvas);
                double gMaxX = Math.Max(0, _textEditorCanvas.Width - tb.Width);
                double gMaxY = Math.Max(0, _textEditorCanvas.Height - Math.Max(tb.MinHeight, 24));
                double newLeft = Math.Clamp(dragStartLeft + (now.X - dragAnchorScreen.X), 0, gMaxX);
                double newTop = Math.Clamp(dragStartTop + (now.Y - dragAnchorScreen.Y), 0, gMaxY);
                Canvas.SetLeft(tb, newLeft);
                Canvas.SetTop(tb, newTop);
                PositionGrip(newLeft, newTop);
            };
            grip.PreviewMouseLeftButtonUp += (_, _) =>
            {
                EndGripDrag();
                grip.ReleaseMouseCapture();
            };
            grip.LostMouseCapture += (_, _) => EndGripDrag();
            _activeTextBoxGrip = grip;
            _textEditorCanvas.Children.Add(grip);
        }

        /// <summary>Reflects the current whiteout setting onto the live placed-text editing box, if any.</summary>
        private void UpdateActiveTextBoxFill()
        {
            if (_activeTextBox is null || _activeTextBox.Tag is not PlacedTextContext) return;
            _activeTextBox.Background = _textWhiteout
                ? FrozenSolidColorBrush(_textFillColor)
                : FrozenSolidColorBrush(Color.FromArgb(230, 255, 255, 255));
        }

        /// <summary>
        /// Reflects font/size/color/bold/italic/underline onto the live editing box, if any — for
        /// BOTH a freshly-placed box (PlacedTextContext) and an in-progress PDF-text edit
        /// (TextEditContext), since the style bar applies to both tools identically. Without this,
        /// the settings bar only ever updated the NEXT box's defaults while a currently-open box
        /// sat on screen unchanged.
        /// </summary>
        private void UpdateActiveTextBoxStyle()
        {
            if (_activeTextBox is not { } tb || tb.Tag is not (PlacedTextContext or TextEditContext)) return;
            tb.FontFamily = new FontFamily(_textFontFamily);
            tb.FontSize = _textFontSize;
            tb.FontWeight = _textBold ? FontWeights.Bold : FontWeights.Normal;
            tb.FontStyle = _textItalic ? FontStyles.Italic : FontStyles.Normal;
            tb.TextDecorations = _textUnderline ? TextDecorations.Underline : null;
            tb.Foreground = new SolidColorBrush(_textColor);
            tb.CaretBrush = new SolidColorBrush(_textColor);
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            // #135 (upstream KillerPDF v1.7.5): bold / italic / underline while editing. Applied to
            // the live TextBox AND mirrored onto the tool state, so the next box you place inherits
            // what you last chose — the same way size, colour and fill already behave.
            // Ctrl+I is also the window's Invert Colors binding (MainWindow.xaml). That resolves
            // correctly and on purpose: KeyDown bubbles from the TextBox outward, so this runs and
            // marks the event handled before it ever reaches the Window's InputBindings. Inside a
            // text box Ctrl+I means italic; everywhere else it still means night mode.
            if (Keyboard.Modifiers == ModifierKeys.Control && sender is TextBox styled)
            {
                switch (e.Key)
                {
                    case Key.B:
                        _textBold = styled.FontWeight != FontWeights.Bold;
                        styled.FontWeight = _textBold ? FontWeights.Bold : FontWeights.Normal;
                        e.Handled = true;
                        return;
                    case Key.I:
                        _textItalic = styled.FontStyle != FontStyles.Italic;
                        styled.FontStyle = _textItalic ? FontStyles.Italic : FontStyles.Normal;
                        e.Handled = true;
                        return;
                    case Key.U:
                        _textUnderline = styled.TextDecorations is not { Count: > 0 };
                        styled.TextDecorations = _textUnderline ? TextDecorations.Underline : null;
                        e.Handled = true;
                        return;
                }
            }

            if (e.Key == Key.Escape)
            {
                CancelActiveTextBox();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                CommitActiveTextBox();
                e.Handled = true;
            }
        }

        private void TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb || !ReferenceEquals(_activeTextBox, tb)) return;
            Telemetry.TrackEvent("Annotation.TextEditorFocusLost",
                new Dictionary<string, string> { ["Type"] = "Text" });
            // Commit on blur when there's content, or always when re-editing (so clearing the box deletes it).
            bool reediting = tb.Tag is PlacedTextContext { Existing: not null };
            if (reediting || !string.IsNullOrWhiteSpace(tb.Text))
            {
                _ = Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () =>
                    {
                        if (!ReferenceEquals(_activeTextBox, tb)) return;
                        // Clicking Font/Size/Bold/Italic in the style bar moves keyboard focus off
                        // this box, which used to read as "the user clicked away" and silently
                        // committed mid-edit — ending the session and switching to Select the
                        // instant someone tried to tweak a style. Interacting with the bar is the
                        // SAME editing session, not leaving it.
                        if (Keyboard.FocusedElement is DependencyObject nf && _textSettingsBar is not null
                            && IsDescendantOf(nf, _textSettingsBar))
                            return;
                        CommitActiveTextBox();
                    });
            }
        }

        /// <summary>Cancels the active placed-text edit, restoring the original annotation if re-editing.</summary>
        private void CancelActiveTextBox()
        {
            if (_activeTextBox is null) return;
            var tb = _activeTextBox;
            _activeTextBox = null;
            RemoveTextEditorElement(tb);
            if (tb.Tag is PlacedTextContext { Existing: { } original } ctx)
            {
                if (!_annotations.TryGetValue(ctx.PageIndex, out var list))
                    _annotations[ctx.PageIndex] = list = [];
                list.Add(original);
                DropTopSnapshotIfFor(ctx.PageIndex);   // no net change — discard the edit-start snapshot
                RenderAllAnnotations(ctx.PageIndex);
            }
            Telemetry.TrackEvent("Annotation.TextEditorClosed", TextEditorTelemetry("Canceled"));
        }

        /// <param name="via">
        /// Compile-time name of the calling method, supplied by the compiler. Recorded on the
        /// resulting <c>Annotation.TextEditorClosed</c> event — see <see cref="TextEditorTelemetry"/>.
        /// </param>
        private void CommitActiveTextBox([CallerMemberName] string? via = null)
        {
            // This is the app's single "settle any in-progress canvas edit" chokepoint — every
            // save / flatten / print / close / tool switch / tab switch / page change routes
            // through it — so an unfinished freeform polygon is settled here too rather than being
            // left dangling on a canvas that is about to be rebuilt. See ShapePolyClick.
            ResolveShapePolygon(commit: true);
            if (_activeTextBox is null) return;

            // #129: a text box the user has not typed into yet is not a finished edit, and tearing
            // it down is only ever right as a response to user intent. Refuse that specific case:
            // brand new, never typed into, still empty, and not a re-edit (a re-edit MUST commit —
            // an emptied box there means "delete this annotation"). Worst case if a genuine commit
            // lands inside the window, an empty editor outlives it by a few hundred milliseconds
            // and produces no annotation either way.
            //
            // #131: this is now a BACKSTOP, not the fix. The caller that was destroying every
            // editor in production was ApplyZoom (54/54 destructions, `Via=ApplyZoom`), and it has
            // been removed from that path entirely — a zoom never needed to settle the editor. What
            // this block is still worth is the same thing it was worth then: an unforeseen
            // incidental caller shows up as a NAMED deferral in telemetry instead of as another
            // silent "Insert Text Box does nothing" report. It buys 400 ms and a name; it is not
            // load-bearing, and no future fix should lean on it as though it were.
            //
            // The page check is load-bearing and was missing. PageList_SelectionChanged routes
            // through here, and deferring it left the empty editor parented to TextEditorCanvas —
            // which no re-render clears — so it floated over the page the user had just navigated
            // TO while still carrying the PageIndex of the page it was placed ON. Typing into it
            // then put the text on a page nobody was looking at. Before this release the ~23 Hz
            // ApplyZoom commit destroyed the box milliseconds later and hid that; it does not now.
            if (!_activeTextBoxTouched
                && _activeTextBox.Tag is PlacedTextContext { Existing: null } graceCtx
                && graceCtx.PageIndex == PageList.SelectedIndex
                && string.IsNullOrWhiteSpace(_activeTextBox.Text)
                && (DateTime.UtcNow - _activeTextBoxPlacedUtc).TotalMilliseconds < UntouchedEditorGraceMs)
            {
                Telemetry.TrackEvent("Annotation.TextEditorCommitDeferred",
                    TextEditorTelemetry("UntouchedGrace", via));
                return;
            }
            // If it's an inline (existing-PDF-text) edit, use the dedicated commit path
            if (_activeTextBox.Tag is TextEditContext)
            {
                CommitTextEdit();
                return;
            }
            var tb = _activeTextBox;
            _activeTextBox = null;

            var ctx = tb.Tag as PlacedTextContext;
            int pageIdx = ctx?.PageIndex ?? (tb.Tag is int idx ? idx : PageList.SelectedIndex);
            bool reediting = ctx?.Existing is not null;   // original already removed + snapshot taken

            string content = tb.Text.Trim();
            double x = Canvas.GetLeft(tb);
            double y = Canvas.GetTop(tb);
            double width = tb.Width;
            double height = tb.ActualHeight;

            RemoveTextEditorElement(tb);

            if (!string.IsNullOrEmpty(content))
            {
                var ta = new TextAnnotation
                {
                    PageIndex = pageIdx,
                    Position = new Point(x, y),
                    Content = content,
                    FontSize = tb.FontSize,
                    // Same reasoning as Bold/Italic/Underline below: read back off the editor.
                    FontName = tb.FontFamily.Source,
                    // #135: read back off the editor, not off the tool state — the user may have
                    // toggled Ctrl+B mid-sentence and the box in front of them is the truth.
                    Bold = tb.FontWeight == FontWeights.Bold,
                    Italic = tb.FontStyle == FontStyles.Italic,
                    Underline = tb.TextDecorations is { Count: > 0 },
                    // #135 item 2: off the tool state, NOT off the editor — a TextBox has no
                    // letter spacing to read back (see the comment where it is built). This is the
                    // value the style bar has been showing all along, so it is what the user set.
                    LetterSpacing = _textLetterSpacing,
                    Width = double.IsNaN(width) || width <= 0 ? 0 : width,
                    HasFill = _textWhiteout
                };
                ta.Height = ta.Width > 0 && height > 0 ? height : 0;
                ta.SetColor(tb.Foreground is SolidColorBrush scb ? scb.Color : Colors.Black);
                if (_textWhiteout) ta.SetFillColor(_textFillColor);

                if (reediting)
                {
                    if (!_annotations.TryGetValue(pageIdx, out var list))
                        _annotations[pageIdx] = list = [];
                    list.Add(ta);
                    MarkDirty();
                    RenderAllAnnotations(pageIdx);
                }
                else
                {
                    AddAnnotation(ta);        // pushes its own snapshot
                    RenderTextAnnotation(ta);
                }

                // Auto-select so the user can immediately drag it off whatever it landed on top of,
                // or resize it, without first having to know to switch to the Select tool — Image
                // and Signature placement already do this (see PlaceImageFromDialog / the signature
                // "Reddit/KillerPDF feedback" comment); Text was the one placement flow that didn't,
                // and a text box with no visible border once committed gave no hint that dragging
                // it required a tool switch at all. SetTool's own CommitActiveTextBox() re-entry
                // is a no-op here (_activeTextBox is already null by this point) — and if this
                // commit was itself triggered by the user clicking a DIFFERENT tool, that tool wins:
                // SetTool always finishes by assigning _currentTool to what it was actually called
                // with, after this nested call returns.
                SetTool(EditTool.Select);
                var placedSize = MeasureTextAnnotation(ta);
                SelectAnnotation(ta, new Rect(ta.Position.X, ta.Position.Y, placedSize.Width, placedSize.Height));

                // #168: say it NOW, not after saving and reopening. The burn resolves the same
                // family this checks (DrawAnnotationsOnDocument), so the two never disagree.
                WarnIfGlyphsWillBeLost(PdfFontStyle.DefaultFamily, ta.Content);
                Telemetry.TrackEvent("Annotation.TextEditorClosed", TextEditorTelemetry("Committed", via));
            }
            else if (reediting)
            {
                // Box emptied while re-editing: original was already removed at edit-start → commit as a delete.
                MarkDirty();
                RenderAllAnnotations(pageIdx);
                Telemetry.TrackEvent("Annotation.TextEditorClosed", TextEditorTelemetry("Deleted", via));
            }
            else
            {
                Telemetry.TrackEvent("Annotation.TextEditorClosed", TextEditorTelemetry("Empty", via));
            }
        }
    }
}
