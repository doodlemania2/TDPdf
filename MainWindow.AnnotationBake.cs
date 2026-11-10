// MainWindow — baking annotations into the saved PDF.
// Moved verbatim from MainWindow.xaml.cs.

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
        // Save annotations to PDF
        // ============================================================

        /// <summary>
        /// Drops all pending form-field values for the active document. Called when the
        /// document object is swapped/reloaded in place (structural edit, undo/redo of a
        /// document change, or open into the current context) because the values are keyed
        /// by field object number, which is invalidated by a reload.
        /// </summary>
        private void ClearFormState()
        {
            _formTextValues.Clear();
            _formCheckValues.Clear();
            _formRadioValues.Clear();
        }

        /// <summary>True if the user has entered any interactive form-field values pending save.</summary>
        private bool HasPendingFormValues =>
            _formTextValues.Count > 0 || _formCheckValues.Count > 0 || _formRadioValues.Count > 0;

        /// <summary>
        /// Draws the underline for a burned text annotation, when it has one.
        /// </summary>
        /// <remarks>
        /// #135: <c>XFontStyle.Underline</c> exists in the enum but PdfSharpCore's
        /// <c>DrawString</c> does not act on it — an underlined annotation would have looked right
        /// on screen and saved without the line, which is the same class of silent screen/PDF
        /// divergence that <c>WrapTextToWidth</c> exists to prevent. So draw it: one thin filled
        /// rectangle just under the baseline, scaled with the font so it stays proportional at any
        /// size or render resolution.
        /// </remarks>
        private static void DrawTextUnderline(XGraphics gfx, TextAnnotation ta, string line,
                                              XFont font, XBrush brush, double x, double baselineY,
                                              double sx, double sy)
        {
            if (!ta.Underline || string.IsNullOrEmpty(line)) return;
            // #135 item 2: a spaced line is wider than its kerned whole-string measurement, so the
            // rule has to span the SPACED width or it stops short of the last character or two.
            // At spacing 0 this is the same gfx.MeasureString call it has always been.
            double width = SpacedStringWidth(gfx, font, line, ta.LetterSpacing * sx);
            if (width <= 0) return;
            double em = ta.FontSize * sy;
            gfx.DrawRectangle(brush, x, baselineY + em * 0.12, width, Math.Max(0.5, em * 0.06));
        }

        /// <summary>
        /// How wide <paramref name="line"/> is once burned at <paramref name="spacing"/>, in the
        /// PDF-space units <paramref name="gfx"/> is drawing in. #135 item 2.
        /// </summary>
        /// <remarks>
        /// PdfSharpCore's MeasureString is a plain sum of per-glyph advances (see
        /// <c>FontHelper.MeasureString</c>) — it neither kerns nor trims whitespace — so the whole
        /// string and the sum of its characters agree on this side of the fence, and both
        /// delegates below can be the same call. That is NOT true of WPF's FormattedText, which is
        /// why <see cref="TextMeasurers"/> has to hand <see cref="TextLetterSpacing.Width"/> two
        /// different functions.
        /// </remarks>
        private static double SpacedStringWidth(XGraphics gfx, XFont font, string line, double spacing)
        {
            double W(string s) => gfx.MeasureString(s, font).Width;
            return TextLetterSpacing.Width(line, spacing, W, W);
        }

        /// <summary>
        /// Burns one line of a text annotation at <paramref name="x"/>, honouring letter spacing.
        /// </summary>
        /// <remarks>
        /// At spacing 0 this is the single <c>DrawString</c> the burn-in has always emitted, glyph
        /// for glyph and kern for kern — the one thing that must not change for the documents
        /// already in the fleet. Otherwise the line is drawn one character at a time at the offsets
        /// <see cref="TextLetterSpacing.Layout"/> computes, which is exactly what
        /// <c>SpacedTextVisual</c> draws on screen from the same arithmetic.
        ///
        /// Spacing scales by <paramref name="sx"/>: it is a horizontal offset in canvas px, and x
        /// here is canvas px times sx. (The glyph advances it accumulates alongside come from a
        /// font sized by sy; the two scales are equal for any page rendered at its own aspect
        /// ratio, which is every page PDFium rasterises, so this is a distinction without a
        /// difference in practice — but sx is the right one to name for a horizontal quantity.)
        /// </remarks>
        private static void DrawTextLine(XGraphics gfx, TextAnnotation ta, string line,
                                         XFont font, XBrush brush, double x, double baselineY, double sx,
                                         string? paragraph = null, int lineStart = 0)
        {
            double spacing = ta.LetterSpacing * sx;
            // Upstream KillerPDF #435: PdfSharpCore does no bidi, so a Hebrew line drawn as one
            // string saves in logical order — backwards. Draw it element by element in visual
            // order instead, inside the same span the left-to-right path would cover. A wrapped
            // line passes its hard paragraph, whose directions decide the order as on screen.
            if (RtlTextLayout.IsHebrewRightToLeftLine(line))
            {
                DrawRightToLeft(gfx, paragraph ?? line, paragraph is null ? 0 : lineStart, line.Length,
                                font, brush, x, baselineY, spacing);
                return;
            }
            if (TextLetterSpacing.IsNone(spacing))
            {
                gfx.DrawString(line, font, brush, x, baselineY);
                return;
            }
            foreach (var (cluster, dx) in
                     TextLetterSpacing.Layout(line, spacing, s => gfx.MeasureString(s, font).Width))
            {
                gfx.DrawString(cluster, font, brush, x + dx, baselineY);
            }
        }

        /// <summary>
        /// Burns a Hebrew line in visual order: one text element per <c>DrawString</c>, at the
        /// offsets <see cref="RtlTextLayout.Layout(string, int, int, double, Func{string, double}, bool)"/>
        /// computes for the line at <paramref name="start"/> in <paramref name="paragraph"/>. Upstream KillerPDF #435.
        /// </summary>
        /// <remarks>
        /// The line spans [x, x + width] exactly as its left-to-right draw would — PdfSharpCore's
        /// MeasureString is a plain sum of advances (see <see cref="SpacedStringWidth"/>), so the
        /// per-element sum here and the whole-string width the underline and fill use agree.
        /// </remarks>
        private static void DrawRightToLeft(XGraphics gfx, string paragraph, int start, int length,
                                            XFont font, XBrush brush, double x, double baselineY, double spacing)
        {
            foreach (var (cluster, dx) in
                     RtlTextLayout.Layout(paragraph, start, length, spacing, s => gfx.MeasureString(s, font).Width))
            {
                gfx.DrawString(cluster, font, brush, x + dx, baselineY);
            }
        }

        /// <summary>Bold/italic flags as the PdfSharpCore font style flags used when burning text (#182).</summary>
        private static XFontStyle ToXFontStyle(bool bold, bool italic) =>
            (bold ? XFontStyle.Bold : XFontStyle.Regular) | (italic ? XFontStyle.Italic : XFontStyle.Regular);

        private void DrawAnnotationsOnDocument()
        {
            if (_doc is null) return;

            // Persist interactive form-field values into the AcroForm before baking
            // annotations, so filled fields are saved alongside drawn annotations.
            WriteFormValuesToDocument();
            // Named to the user once the save in progress has finished, not mid-write.
            if (_formWriteFailures.Count > 0)
                Dispatcher.BeginInvoke(new Action(ReportFormWriteFailures), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // Strip link annotation borders so they don't render as colored rectangles
            // (e.g. strikethrough-like lines) in other PDF viewers.
            StripLinkAnnotationBorders(_doc);

            foreach (var kvp in _annotations)
            {
                int pageIdx = kvp.Key;
                var annots = kvp.Value;
                if (annots.Count == 0 || pageIdx < 0 || pageIdx >= _doc.PageCount) continue;

                var page = _doc.Pages[pageIdx];
                // The annotation frame: the DIP canvas the page was drawn on. This used to be a raw
                // _renderDims lookup that SKIPPED the page when the entry was missing, and the entry
                // goes missing far more often than "never rendered" — InvalidateRenderCache empties
                // the whole map on a monitor DPI change and on AdoptEditedFile, both of which keep
                // the annotations, and only pages the user then revisits get it back. Every other
                // page's unsaved annotations were silently left out of the save / print / export
                // (upstream KillerPDF 689e9f6b). EnsureRenderDims returns the rendered frame when
                // there is one — so the common path is unchanged to the pixel — and otherwise
                // derives the same RenderBoxDip frame from page geometry, which is the frame the
                // annotations were placed in (see AnnotationCanvasSize). Only a degenerate page box
                // yields zero, and there is nothing a scale factor could map onto then.
                var (renderW, renderH) = EnsureRenderDims(pageIdx);
                if (renderW <= 0 || renderH <= 0) continue;

                // Annotation coordinates are positions on the bitmap PDFium rasterized, and PDFium
                // renders the page's VISIBLE box (/CropBox over /MediaBox, inheritance-aware) with
                // /Rotate applied. Scale and place against that same frame:
                //  * the raw page.Width/Height used before are media-box derived with an implicit
                //    (0,0) origin, so an inset or offset /CropBox displaced every annotation;
                //  * nothing here handled /Rotate at all, so on a quarter-turned page annotations
                //    were burned a quarter turn out of place and scaled on swapped axes (#169).
                // TDPdf keeps the angle on the page itself (RotatePages_Click writes /Rotate and
                // reloads), so the page dictionary is the authority — there is no separate map.
                int rot = PdfPageGeometry.Rotation(page);
                var box = GetVisiblePageBox(page);
                bool quarterTurn = rot is 90 or 270;   // visual extent is the box's dims swapped
                double sx = (quarterTurn ? box.Height : box.Width) / renderW;
                double sy = (quarterTurn ? box.Width : box.Height) / renderH;

                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                // ...then turn the whole drawing back into the frame XGraphics writes in, so every
                // draw call below keeps working in canvas-scaled coordinates exactly as it always has.
                if (VisualToPageMatrix(rot, box, page.Height.Point) is XMatrix visualToPage)
                    gfx.MultiplyTransform(visualToPage, XMatrixOrder.Prepend);

                foreach (var annot in InPaintOrder(annots))
                {
                    switch (annot)
                    {
                        case TextAnnotation ta:
                        {
                            const double pad = 2;
                            // #168: the editor is a WPF TextBox and falls back per CHARACTER across
                            // every installed font, so anything typed looks right on screen;
                            // PdfSharpCore resolves ONE face and emits an empty box for every
                            // codepoint that face lacks. Pick a family that actually covers THIS
                            // annotation's text; PickFamily keeps the annotation's own chosen family
                            // whenever it can carry it, which is every Latin annotation, so nothing
                            // existing moves.
                            var font = TdpFontResolver.TryCreate(
                                FontCoverage.PickFamily(ta.FontName, ta.Content),
                                ta.FontSize * sy, ToXFontStyle(ta.Bold, ta.Italic));   // #135
                            // Nothing resolvable at all (no readable font directory, or a degenerate
                            // size): skip this ONE annotation rather than throw out of the save.
                            if (font is null) break;
                            double lineH = ta.FontSize * sy * 1.2;
                            var taColor = ta.GetColor();
                            var taBrush = new XSolidBrush(XColor.FromArgb(taColor.A, taColor.R, taColor.G, taColor.B));

                            if (ta.Width > 0)
                            {
                                // Fixed-width wrapping box: mirror the on-screen wrap (same font metrics)
                                // and the whiteout fill so the saved PDF matches the screen.
                                // #135: the wrap is spacing-aware, and TextLayoutTypeface is the
                                // same typeface the on-screen preview laid this annotation out in,
                                // so the breaks cannot come out anywhere else here.
                                var wrapped = WrapTextToWidth(ta.Content, ta.FontSize, ta.Width - pad * 2,
                                                              TextLayoutTypeface(ta), ta.LetterSpacing);
                                double boxH = ta.Height > 0 ? ta.Height : wrapped.Count * (ta.FontSize * 1.2) + pad * 2;
                                if (ta.HasFill)
                                {
                                    var fc = ta.GetFillColor();
                                    gfx.DrawRectangle(
                                        new XSolidBrush(XColor.FromArgb(fc.A, fc.R, fc.G, fc.B)),
                                        ta.Position.X * sx, ta.Position.Y * sy,
                                        ta.Width * sx, boxH * sy);
                                }
                                double ty = ta.Position.Y * sy + pad * sy + ta.FontSize * sy;
                                // #435: bidi directions come from the whole hard paragraph.
                                var sources = RtlTextLayout.LocateWrappedLines(ta.Content, wrapped);
                                for (int li = 0; li < wrapped.Count; li++)
                                {
                                    string line = wrapped[li];
                                    if (!string.IsNullOrEmpty(line))
                                    {
                                        DrawTextLine(gfx, ta, line, font, taBrush,
                                                     (ta.Position.X + pad) * sx, ty, sx,
                                                     sources?[li].Paragraph, sources?[li].Start ?? 0);
                                        DrawTextUnderline(gfx, ta, line, font, taBrush,
                                                          (ta.Position.X + pad) * sx, ty, sx, sy);
                                    }
                                    ty += lineH;
                                }
                            }
                            else
                            {
                                // Legacy auto-size: unchanged newline-split rendering (with optional fill).
                                var lines = ta.Content.Split('\n');
                                if (ta.HasFill)
                                {
                                    var sz = MeasureTextAnnotation(ta);
                                    var fc = ta.GetFillColor();
                                    gfx.DrawRectangle(
                                        new XSolidBrush(XColor.FromArgb(fc.A, fc.R, fc.G, fc.B)),
                                        ta.Position.X * sx, ta.Position.Y * sy,
                                        sz.Width * sx, sz.Height * sy);
                                }
                                double ty = ta.Position.Y * sy + ta.FontSize * sy;
                                foreach (var line in lines)
                                {
                                    if (!string.IsNullOrEmpty(line))
                                    {
                                        DrawTextLine(gfx, ta, line, font, taBrush,
                                                     ta.Position.X * sx, ty, sx);
                                        DrawTextUnderline(gfx, ta, line, font, taBrush,
                                                          ta.Position.X * sx, ty, sx, sy);
                                    }
                                    ty += lineH;
                                }
                            }
                            break;
                        }

                        // Markup: one filled band per covered line. Matched before
                        // HighlightAnnotation — it is a subclass, and a plain highlight must keep
                        // writing exactly the single rectangle it always did.
                        case MarkupAnnotation mk:
                        {
                            var mkc = mk.GetColor();
                            var mkBrush = new XSolidBrush(XColor.FromArgb(mkc.A, mkc.R, mkc.G, mkc.B));
                            // TDPdf patch (#200, from upstream KillerPDF): a highlight burns with the
                            // Multiply blend mode so the colour darkens the paper and the text under
                            // it stays crisp instead of being washed out by an opaque overlay.
                            // Strikethrough and underline stay normal draws — a thin dark band
                            // multiplied over text would disappear.
                            XGraphicsState? mkState = null;
                            if (mk.Style == MarkupStyle.Highlight)
                            {
                                mkState = gfx.Save();
                                gfx.SetPdfBlendMode("Multiply");
                            }
                            try
                            {
                                foreach (var pr in mk.PaintRects())
                                    gfx.DrawRectangle(mkBrush,
                                        pr.X * sx, pr.Y * sy, pr.Width * sx, pr.Height * sy);
                            }
                            finally
                            {
                                // The grestore returns the blend mode to Normal.
                                if (mkState is not null) gfx.Restore(mkState);
                            }
                            break;
                        }

                        case HighlightAnnotation ha:
                        {
                            var hc = ha.GetColor();
                            var hBrush = new XSolidBrush(XColor.FromArgb(hc.A, hc.R, hc.G, hc.B));
                            // TDPdf patch (#200, from upstream KillerPDF): burn with Multiply so the
                            // highlight darkens the paper rather than covering the text.
                            var hState = gfx.Save();
                            gfx.SetPdfBlendMode("Multiply");
                            try
                            {
                                gfx.DrawRectangle(hBrush,
                                    ha.Bounds.X * sx, ha.Bounds.Y * sy,
                                    ha.Bounds.Width * sx, ha.Bounds.Height * sy);
                            }
                            finally
                            {
                                // The grestore returns the blend mode to Normal.
                                gfx.Restore(hState);
                            }
                            break;
                        }

                        case ShapeAnnotation shp:
                        {
                            var stk = shp.GetStrokeColor();
                            var shpPen = new XPen(XColor.FromArgb(stk.A, stk.R, stk.G, stk.B), shp.StrokeWidth * sx)
                            {
                                LineJoin = XLineJoin.Round,
                                LineCap = XLineCap.Round
                            };
                            XSolidBrush? shpFill = null;
                            if (shp.HasFill)
                            {
                                var fc = shp.GetFillColor();
                                shpFill = new XSolidBrush(XColor.FromArgb(fc.A, fc.R, fc.G, fc.B));
                            }
                            switch (shp.Kind)
                            {
                                case ShapeKind.Rectangle:
                                {
                                    var b = shp.Bounds;
                                    double rx = b.X * sx, ry = b.Y * sy;
                                    double rw = b.Width * sx, rh = b.Height * sy;
                                    if (shpFill is not null) gfx.DrawRectangle(shpFill, rx, ry, rw, rh);
                                    gfx.DrawRectangle(shpPen, rx, ry, rw, rh);
                                    break;
                                }
                                case ShapeKind.Ellipse:
                                {
                                    var b = shp.Bounds;
                                    double rx = b.X * sx, ry = b.Y * sy;
                                    double rw = b.Width * sx, rh = b.Height * sy;
                                    if (shpFill is not null) gfx.DrawEllipse(shpFill, rx, ry, rw, rh);
                                    gfx.DrawEllipse(shpPen, rx, ry, rw, rh);
                                    break;
                                }
                                case ShapeKind.Polygon:
                                {
                                    // PdfSharpCore's DrawPolygon closes the outline for us, so the
                                    // stored vertices (no repeated first point) map straight over.
                                    if (shp.Points.Count < 3) break;
                                    var poly = shp.Points
                                        .Select(p => new XPoint(p.X * sx, p.Y * sy)).ToArray();
                                    if (shpFill is not null)
                                        gfx.DrawPolygon(shpFill, poly, XFillMode.Alternate);
                                    gfx.DrawPolygon(shpPen, poly);
                                    break;
                                }
                                case ShapeKind.Line:
                                    gfx.DrawLine(shpPen,
                                        shp.Start.X * sx, shp.Start.Y * sy,
                                        shp.End.X * sx,   shp.End.Y * sy);
                                    break;
                            }
                            break;
                        }

                        case InkAnnotation ia:
                        {
                            var ic = ia.GetColor();
                            // A pen dot is a filled circle, never a zero-length stroke — see InkDot.
                            // Its diameter scales with sx, exactly as the pen width below does.
                            if (InkDot.TryGetDot(ia.Points.Select(p => (p.X, p.Y)), out double dotX, out double dotY))
                            {
                                InkDot.Burn(gfx, XColor.FromArgb(ic.A, ic.R, ic.G, ic.B),
                                    dotX * sx, dotY * sy, ia.StrokeWidth * sx);
                                break;
                            }
                            if (ia.Points.Count < 2) break;
                            var pen = new XPen(XColor.FromArgb(ic.A, ic.R, ic.G, ic.B), ia.StrokeWidth * sx)
                            {
                                LineJoin = XLineJoin.Round,
                                LineCap = XLineCap.Round
                            };
                            for (int i = 0; i < ia.Points.Count - 1; i++)
                            {
                                gfx.DrawLine(pen,
                                    ia.Points[i].X * sx, ia.Points[i].Y * sy,
                                    ia.Points[i + 1].X * sx, ia.Points[i + 1].Y * sy);
                            }
                            break;
                        }

                        case TextEditAnnotation tea:
                            // PdfSharpCore cannot surgically edit existing PDF content streams here.
                            // Existing text/image edits are approximated by painting a white rectangle
                            // over the original region, then drawing replacement content on top.
                            //
                            // Resolve the font FIRST: if nothing can be resolved the whole edit is
                            // skipped, because covering the original line and then drawing nothing on
                            // top would erase the page's own text (#168 safety).
                            //
                            // Keep the face styling detected on the original line (#182) so a
                            // bold/italic run does not save back as plain, and run the family through
                            // the same coverage check as placed text (#168) — the in-place editor is
                            // the path most likely to carry non-Latin content, since it starts from
                            // whatever the PDF already says.
                            var editFont = TdpFontResolver.TryCreate(
                                FontCoverage.PickFamily(tea.FontName, tea.NewContent),
                                tea.FontSize * sy, ToXFontStyle(tea.Bold, tea.Italic));
                            if (editFont is null) break;
                            var whiteRect = new XSolidBrush(XColors.White);
                            gfx.DrawRectangle(whiteRect,
                                (tea.OriginalBounds.X - 1) * sx, (tea.OriginalBounds.Y - 1) * sy,
                                (tea.OriginalBounds.Width + 2) * sx, (tea.OriginalBounds.Height + 2) * sy);
                            double ety = tea.Position.Y * sy + tea.FontSize * sy;
                            var teaColor = tea.GetColor();
                            var teaBrush = new XSolidBrush(XColor.FromArgb(teaColor.A, teaColor.R, teaColor.G, teaColor.B));
                            // #435: a Hebrew edit would save backwards through DrawString,
                            // which does no bidi. One line only — the edit is drawn as a single
                            // string, so a line break here is not a layout this can reorder.
                            if (tea.NewContent.IndexOfAny(new[] { '\r', '\n' }) < 0
                                && RtlTextLayout.IsHebrewRightToLeftLine(tea.NewContent))
                                DrawRightToLeft(gfx, tea.NewContent, 0, tea.NewContent.Length, editFont, teaBrush,
                                                tea.Position.X * sx, ety, 0);
                            else
                                gfx.DrawString(tea.NewContent, editFont, teaBrush, tea.Position.X * sx, ety);
                            break;

                        case ImageEditAnnotation iea:
                            var imageWhiteRect = new XSolidBrush(XColors.White);
                            gfx.DrawRectangle(imageWhiteRect,
                                (iea.OriginalBounds.X - 2) * sx, (iea.OriginalBounds.Y - 2) * sy,
                                (iea.OriginalBounds.Width + 4) * sx, (iea.OriginalBounds.Height + 4) * sy);
                            if (!iea.IsDeleted)
                            {
                                try
                                {
                                    XImage? xImg = null;
                                    if (!string.IsNullOrWhiteSpace(iea.ReplacementImagePath) && System.IO.File.Exists(iea.ReplacementImagePath))
                                    {
                                        xImg = XImage.FromFile(iea.ReplacementImagePath);
                                    }
                                    else if (!string.IsNullOrEmpty(iea.OriginalImageData))
                                    {
                                        var imageBytes = Convert.FromBase64String(iea.OriginalImageData);
                                        xImg = XImage.FromStream(() => new MemoryStream(imageBytes));
                                    }

                                    if (xImg is not null)
                                    {
                                        int rotation = ((iea.Rotation % 360) + 360) % 360;
                                        if (rotation == 0)
                                        {
                                            gfx.DrawImage(xImg,
                                                iea.TargetBounds.X * sx, iea.TargetBounds.Y * sy,
                                                iea.TargetBounds.Width * sx, iea.TargetBounds.Height * sy);
                                        }
                                        else
                                        {
                                            double cx = (iea.TargetBounds.X + iea.TargetBounds.Width / 2) * sx;
                                            double cy = (iea.TargetBounds.Y + iea.TargetBounds.Height / 2) * sy;
                                            bool imageQuarterTurn = rotation is 90 or 270;
                                            double drawW = (imageQuarterTurn ? iea.TargetBounds.Height : iea.TargetBounds.Width) * sx;
                                            double drawH = (imageQuarterTurn ? iea.TargetBounds.Width : iea.TargetBounds.Height) * sy;
                                            var state = gfx.Save();
                                            try
                                            {
                                                gfx.RotateAtTransform(rotation, new XPoint(cx, cy));
                                                gfx.DrawImage(xImg, cx - drawW / 2, cy - drawH / 2, drawW, drawH);
                                            }
                                            finally
                                            {
                                                gfx.Restore(state);
                                            }
                                        }
                                    }
                                }
                                catch { /* skip broken image edit */ }
                            }
                            break;

                        case SignatureAnnotation sa:
                            if (sa.ImageData is not null)
                            {
                                try
                                {
                                    var imgBytes = Convert.FromBase64String(sa.ImageData);
                                    var xImg = XImage.FromStream(() => new System.IO.MemoryStream(imgBytes));
                                    double imgX = sa.Position.X * sx;
                                    double imgY = sa.Position.Y * sy;
                                    double imgW = sa.SourceWidth * sa.Scale * sx;
                                    double imgH = sa.SourceHeight * sa.Scale * sy;
                                    gfx.DrawImage(xImg, imgX, imgY, imgW, imgH);
                                }
                                catch { /* skip broken image */ }
                            }
                            else
                            {
                                var sigPen = new XPen(XColors.Black, 2 * sa.Scale * sx)
                                {
                                    LineJoin = XLineJoin.Round,
                                    LineCap = XLineCap.Round
                                };
                                foreach (var stroke in sa.Strokes)
                                {
                                    for (int i = 0; i < stroke.Count - 1; i++)
                                    {
                                        double x1 = (sa.Position.X + stroke[i].X * sa.Scale) * sx;
                                        double y1 = (sa.Position.Y + stroke[i].Y * sa.Scale) * sy;
                                        double x2 = (sa.Position.X + stroke[i + 1].X * sa.Scale) * sx;
                                        double y2 = (sa.Position.Y + stroke[i + 1].Y * sa.Scale) * sy;
                                        gfx.DrawLine(sigPen, x1, y1, x2, y2);
                                    }
                                }
                            }
                            break;

                        case ImageAnnotation ia:
                            try
                            {
                                var iaBytes = Convert.FromBase64String(ia.ImageData);
                                var xia = XImage.FromStream(() => new System.IO.MemoryStream(iaBytes));
                                double iaX = ia.Position.X * sx;
                                double iaY = ia.Position.Y * sy;
                                double iaW = ia.SourceWidth * ia.Scale * sx;
                                double iaH = ia.SourceHeight * ia.Scale * sy;
                                gfx.DrawImage(xia, iaX, iaY, iaW, iaH);
                            }
                            catch { /* skip broken image */ }
                            break;
                    }
                }
            }
        }
    }
}
