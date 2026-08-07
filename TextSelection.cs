using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using TDPdf.Services;

namespace TDPdf
{
    // ============================================================
    // Flowing text selection and line-hugging markup (upstream KillerPDF v1.6.5, #127)
    // ============================================================
    //
    // A drag that STARTS on a text character tracks the actual run of characters in reading order,
    // browser-style, instead of laying down a rectangle marquee. Geometry comes from
    // TextRunService (PdfPig words -> letters, the same source the search highlights and the
    // rectangle region extractor use); the endpoints are caret positions over the page's flattened
    // character list; and the painted quads use the same PDF->canvas math the search highlights
    // use, so everything lands where search lands.
    //
    // What is deliberately preserved:
    //   * A plain CLICK still selects the annotation under the cursor.
    //   * A drag that starts on EMPTY page (no character under the press) keeps the classic box
    //     marquee — that is what annotation box-select, region copy, and OCR region capture ride
    //     on, and it is the only thing that works on a scan with no text layer.
    //   * Holding Shift forces the marquee even over text.
    //
    // SCOPE — view modes. TDPdf has exactly ONE annotation overlay (AnnotationCanvas) and it covers
    // the primary/selected page only: Grid and Two-Page render their extra pages as plain click-to-
    // navigate image tiles, and Continuous view collapses the overlay entirely (it is view +
    // navigate only). So flowing selection works on the active page in Single, Two-Page and Grid
    // view, and is not offered at all in Continuous view — exactly like every other annotation tool
    // in this app today. Anchor/focus still carry a page index and the helpers below are written
    // page-wise, so cross-page selection becomes a small change if per-page overlays ever land.
    public partial class MainWindow
    {
        // ── Per-document selection state (forwarded from the active tab's DocumentContext, exactly
        //    like _annotations / _undoStack: carets index into THIS document's pages). ──
        private TextRunService _textRuns => _ctx.TextRuns;
        private bool _txtSelActive { get => _ctx.TxtSelActive; set => _ctx.TxtSelActive = value; }
        private bool _txtSelHasRange { get => _ctx.TxtSelHasRange; set => _ctx.TxtSelHasRange = value; }
        private (int Page, int Caret) _txtSelAnchor { get => _ctx.TxtSelAnchor; set => _ctx.TxtSelAnchor = value; }
        private (int Page, int Caret) _txtSelFocus { get => _ctx.TxtSelFocus; set => _ctx.TxtSelFocus = value; }
        private Point _txtSelDownPos { get => _ctx.TxtSelDownPos; set => _ctx.TxtSelDownPos = value; }
        private bool _txtSelDragStarted { get => _ctx.TxtSelDragStarted; set => _ctx.TxtSelDragStarted = value; }
        private PageAnnotation? _txtSelClickAnnot { get => _ctx.TxtSelClickAnnot; set => _ctx.TxtSelClickAnnot = value; }
        private Rect _txtSelClickAnnotBounds { get => _ctx.TxtSelClickAnnotBounds; set => _ctx.TxtSelClickAnnotBounds = value; }
        /// <summary>Non-null while a Highlight / Strikethrough / Underline tool owns the flowing
        /// drag: the release commits markup annotations instead of copying text.</summary>
        private EditTool? _txtSelCommitTool { get => _ctx.TxtSelCommitTool; set => _ctx.TxtSelCommitTool = value; }

        /// <summary>Canvas children carrying this tag are selection quads, owned by this file.</summary>
        private const string TextSelQuadTag = "TextSelQuad";

        /// <summary>A drag has to move this far (canvas px) before it stops counting as a click.</summary>
        private const double TextSelDragThresholdPx = 4.0;

        /// <summary>
        /// Shown when Strikethrough or Underline is used on a page that carries no text layer at
        /// all (a scan). There is nothing for those two to follow and a rectangle strikethrough is
        /// meaningless, so they decline and point elsewhere. TDPdf has no tool number keys, so this
        /// names the Shape tool rather than a keystroke.
        /// </summary>
        private const string NoTextLayerHint =
            "No text layer on this page — use the Shape tool to draw a box over it.";

        /// <summary>
        /// Shown when the highlighter is used on a page with no text layer. Purely informational:
        /// the classic rectangle drag still happens, this just explains why the highlight is not
        /// hugging individual words.
        /// </summary>
        private const string NoTextLayerHighlightHint =
            "No text layer on this page — highlighting a rectangle instead.";

        /// <summary>
        /// Shown when a markup tool is pressed on a blank part of a page that does have text.
        /// </summary>
        private const string NoTextHereHint =
            "No text here — drag across words to mark them.";

        /// <summary>
        /// True when PdfPig finds selectable text on the page. Cheap after the first call: the
        /// result is served from the per-document character cache.
        /// </summary>
        private bool PageHasTextLayer(int pageIdx)
        {
            if (_currentFile is null || pageIdx < 0) return false;
            var runs = _textRuns.GetPage(_currentFile, pageIdx);
            return runs is not null && runs.Chars.Count > 0;
        }

        /// <summary>
        /// Canvas point to PDF space (points, bottom-left origin) — the inverse of the mapping the
        /// search highlights paint with, and the same one ExtractTextFromRegion uses.
        /// </summary>
        private static (double X, double Y) CanvasToPdf(Point pos, double renderW, double renderH, PageTextRuns runs)
            => (pos.X * runs.PdfWidth / renderW, runs.PdfHeight - pos.Y * runs.PdfHeight / renderH);

        /// <summary>
        /// Arms a flowing selection when the press lands ON text. Returns false for empty page,
        /// a page with no text layer, or a file PdfPig cannot read — the caller then falls back to
        /// whatever it did before (marquee for Select, status hint for the markup tools).
        /// </summary>
        private bool TryBeginTextSelection(int pageIdx, Point pos)
        {
            if (_currentFile is null || pageIdx < 0) return false;
            if (!_renderDims.TryGetValue(pageIdx, out var rd)) return false;
            if (rd.w <= 0 || rd.h <= 0) return false;

            var runs = _textRuns.GetPage(_currentFile, pageIdx);
            if (runs is null || runs.Chars.Count == 0) return false;

            var (px, py) = CanvasToPdf(pos, rd.w, rd.h, runs);
            if (!TextRunService.IsOverText(runs, px, py)) return false;

            ClearTextSelection();
            int caret = TextRunService.CaretFromPoint(runs, px, py);
            _txtSelAnchor = _txtSelFocus = (pageIdx, caret);
            _txtSelDownPos = pos;
            _txtSelDragStarted = false;
            _txtSelActive = true;
            return true;
        }

        /// <summary>
        /// Mouse-move while a flowing drag is live: move the focus caret and repaint the quads.
        /// <paramref name="pos"/> is already clamped to the canvas by the caller, so dragging past
        /// an edge clamps to the start/end of a line instead of losing the selection.
        /// </summary>
        private void UpdateTextSelectionDrag(Point pos)
        {
            if (_currentFile is null) return;

            // Below the threshold this is still a click (which selects the annotation under the
            // press, if any, on mouse-up) rather than a text drag.
            if (!_txtSelDragStarted)
            {
                if (Math.Abs(pos.X - _txtSelDownPos.X) < TextSelDragThresholdPx &&
                    Math.Abs(pos.Y - _txtSelDownPos.Y) < TextSelDragThresholdPx)
                    return;
                _txtSelDragStarted = true;
            }

            int page = _txtSelAnchor.Page;
            if (!_renderDims.TryGetValue(page, out var rd)) return;
            if (rd.w <= 0 || rd.h <= 0) return;
            // Cached after the first call for this (file, timestamp, page), so a mouse-move never
            // re-parses the page.
            var runs = _textRuns.GetPage(_currentFile, page);
            if (runs is null || runs.Chars.Count == 0) return;

            var (px, py) = CanvasToPdf(pos, rd.w, rd.h, runs);
            var focus = (page, TextRunService.CaretFromPoint(runs, px, py));
            if (focus == _txtSelFocus) return;
            _txtSelFocus = focus;
            RepaintTextSelection();
        }

        /// <summary>
        /// Mouse-up: settle the gesture. A click selects the annotation under the press; a markup
        /// tool's drag commits one grouped annotation; the Select tool's drag copies the run and
        /// leaves the quads on screen.
        /// </summary>
        private void FinishTextSelection()
        {
            _txtSelActive = false;
            var clickAnnot = _txtSelClickAnnot;
            var clickBounds = _txtSelClickAnnotBounds;
            var commitTool = _txtSelCommitTool;
            _txtSelClickAnnot = null;
            _txtSelCommitTool = null;

            if (!_txtSelDragStarted || _txtSelAnchor == _txtSelFocus)
            {
                // Plain click: whatever sat under the press (typically a paragraph-covering
                // highlight) gets selected, exactly as it did before flowing selection existed.
                // A markup tool's click just drops the empty gesture.
                ClearTextSelection();
                if (commitTool is null && clickAnnot is not null) SelectAnnotation(clickAnnot, clickBounds);
                return;
            }

            if (commitTool is EditTool markupTool)
            {
                CommitFlowingMarkup(markupTool);
                return;
            }

            _txtSelHasRange = true;
            _selectedText = BuildSelectedText(out int words);
            if (string.IsNullOrWhiteSpace(_selectedText))
            {
                ClearTextSelection();
                SetStatus("No text found in selection");
                return;
            }
            try { Clipboard.SetText(_selectedText); }
            catch { /* clipboard momentarily locked by another app */ }
            SetStatus($"Copied {words} word(s) to clipboard");
        }

        /// <summary>Anchor/focus ordered into (start, end) document order.</summary>
        private ((int Page, int Caret) Start, (int Page, int Caret) End) OrderedSelection()
        {
            var a = _txtSelAnchor;
            var f = _txtSelFocus;
            bool aFirst = a.Page < f.Page || (a.Page == f.Page && a.Caret <= f.Caret);
            return aFirst ? (a, f) : (f, a);
        }

        /// <summary>The caret slice of the selection that falls on one page, or (0,0) when none.</summary>
        private (int Start, int End) SelectionSliceForPage(int page, int charCount)
        {
            var (s, e) = OrderedSelection();
            if (page < s.Page || page > e.Page) return (0, 0);
            int start = page == s.Page ? s.Caret : 0;
            int end = page == e.Page ? e.Caret : charCount;
            return (start, end);
        }

        /// <summary>Selected text in reading order — spaces between words, newlines between lines
        /// and paragraphs.</summary>
        private string BuildSelectedText(out int wordCount)
        {
            wordCount = 0;
            if (_currentFile is null) return string.Empty;
            var (s, e) = OrderedSelection();
            var sb = new System.Text.StringBuilder();
            for (int p = s.Page; p <= e.Page; p++)
            {
                var runs = _textRuns.GetPage(_currentFile, p);
                if (runs is null || runs.Chars.Count == 0) continue;
                var (start, end) = SelectionSliceForPage(p, runs.Chars.Count);
                if (start >= end) continue;
                string t = TextRunService.TextForRange(runs, start, end, out int w);
                if (t.Length == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t);
                wordCount += w;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Per-line rects (canvas space) of the current selection on one page — one rect per line,
        /// first selected character to last, browser-style. Shared by the quad painter and the
        /// markup commit, so a committed highlight lands exactly where the drag preview showed it.
        /// </summary>
        private List<Rect> SelectionLineRectsForPage(int page)
        {
            var result = new List<Rect>();
            if (_currentFile is null) return result;
            var (s, e) = OrderedSelection();
            if (page < s.Page || page > e.Page) return result;
            if (!_renderDims.TryGetValue(page, out var rd)) return result;
            if (rd.w <= 0 || rd.h <= 0) return result;

            var runs = _textRuns.GetPage(_currentFile, page);
            if (runs is null || runs.Chars.Count == 0) return result;
            if (runs.PdfWidth <= 0 || runs.PdfHeight <= 0) return result;

            var (start, end) = SelectionSliceForPage(page, runs.Chars.Count);
            if (start >= end) return result;

            double sx = rd.w / runs.PdfWidth;
            double sy = rd.h / runs.PdfHeight;

            int i = start;
            while (i < end)
            {
                int lineIdx = runs.Chars[i].Line;
                if (lineIdx < 0 || lineIdx >= runs.Lines.Count) break;
                var line = runs.Lines[lineIdx];
                int segEnd = Math.Min(end, line.End);
                if (segEnd <= i) break;   // defensive: never spin on malformed geometry

                // A selected caret slice runs left-to-right on an LTR line and right-to-left on an
                // RTL one, so take the slice's physical extremes instead of assuming the first
                // glyph is the leftmost and the last the rightmost (#170).
                double left = double.MaxValue, right = double.MinValue;
                for (int ci = i; ci < segEnd; ci++)
                {
                    if (runs.Chars[ci].Left < left) left = runs.Chars[ci].Left;
                    if (runs.Chars[ci].Right > right) right = runs.Chars[ci].Right;
                }
                double h = (line.Top - line.Bottom) * sy;
                double pad = h * 0.12;   // a touch of breathing room around the glyph box

                result.Add(new Rect(left * sx,
                                    rd.h - line.Top * sy - pad,
                                    Math.Max((right - left) * sx, 2),
                                    Math.Max(h + pad * 2, 2)));
                i = segEnd;
            }
            return result;
        }

        /// <summary>
        /// Paints one page's selection quads onto the annotation overlay. Called while dragging and
        /// from the tail of RenderAllAnnotations, so the quads survive every re-render.
        /// </summary>
        private void ApplyTextSelectionQuads(int page)
        {
            if (!_txtSelActive && !_txtSelHasRange) return;
            if (_annotationCanvas is null) return;
            foreach (var r in SelectionLineRectsForPage(page))
            {
                var quad = new Rectangle
                {
                    Opacity = 60.0 / 255.0,
                    Width = r.Width,
                    Height = r.Height,
                    IsHitTestVisible = false,
                    Tag = TextSelQuadTag
                };
                // Live theme binding: a plain brush snapshot would not follow a theme switch, so
                // the quads recolour the moment the Dark / Light / High-Contrast theme changes.
                quad.SetResourceReference(Shape.FillProperty, "SelectionAccent");
                Canvas.SetLeft(quad, r.X);
                Canvas.SetTop(quad, r.Y);
                _annotationCanvas.Children.Add(quad);
            }
        }

        /// <summary>Drops and repaints the quads for the page the selection is on.</summary>
        private void RepaintTextSelection()
        {
            RemoveTextSelQuads();
            var (s, e) = OrderedSelection();
            for (int p = s.Page; p <= e.Page; p++)
                ApplyTextSelectionQuads(p);
        }

        private void RemoveTextSelQuads()
        {
            if (_annotationCanvas is null) return;
            for (int i = _annotationCanvas.Children.Count - 1; i >= 0; i--)
                if (_annotationCanvas.Children[i] is Rectangle r && (r.Tag as string) == TextSelQuadTag)
                    _annotationCanvas.Children.RemoveAt(i);
        }

        /// <summary>
        /// Turns the flowing selection into ONE <see cref="MarkupAnnotation"/> for the page: a rect
        /// per covered line inside a single annotation, so the gesture selects, moves, deletes and
        /// undoes as a single unit.
        /// </summary>
        private void CommitFlowingMarkup(EditTool tool)
        {
            int page = _txtSelAnchor.Page;
            var rects = SelectionLineRectsForPage(page);
            ClearTextSelection();
            if (rects.Count == 0)
            {
                SetStatus(NoTextHereHint);
                return;
            }

            var style = tool switch
            {
                EditTool.Strikethrough => MarkupStyle.Strikethrough,
                EditTool.Underline => MarkupStyle.Underline,
                _ => MarkupStyle.Highlight
            };

            var markup = new MarkupAnnotation { PageIndex = page, Style = style };
            markup.LineRects.AddRange(rects);
            markup.SyncBounds();
            markup.SetColor(style == MarkupStyle.Highlight ? _highlightColor : _markupLineColor);
            AddAnnotation(markup);   // pushes the page snapshot and marks the document dirty
            RenderAllAnnotations(page);

            string verb = style switch
            {
                MarkupStyle.Strikethrough => "Struck through",
                MarkupStyle.Underline => "Underlined",
                _ => "Highlighted"
            };
            SetStatus($"{verb} {rects.Count} line(s)");
        }

        /// <summary>The tool a selected markup annotation's style bar should be bound to.</summary>
        private static EditTool ToolForMarkupStyle(MarkupStyle style) => style switch
        {
            MarkupStyle.Strikethrough => EditTool.Strikethrough,
            MarkupStyle.Underline => EditTool.Underline,
            _ => EditTool.Highlight
        };

        /// <summary>Default colour a markup tool draws with.</summary>
        private System.Windows.Media.Color MarkupToolColor(EditTool tool) =>
            tool is EditTool.Strikethrough or EditTool.Underline ? _markupLineColor : _highlightColor;

        private void SetMarkupToolColor(EditTool tool, System.Windows.Media.Color c)
        {
            if (tool is EditTool.Strikethrough or EditTool.Underline) _markupLineColor = c;
            else _highlightColor = c;
        }

        /// <summary>
        /// Drops every page's cached character geometry for this document. Called wherever page
        /// geometry can change under a working file that might keep the same path and timestamp;
        /// the cache key (path + last-write time) already covers reload, save, and the temp-file
        /// repoint every structural edit goes through.
        /// </summary>
        private void InvalidateTextRunCache()
        {
            _textRuns.Clear();
            ClearTextSelection();
        }
    }
}
