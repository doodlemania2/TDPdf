using System.IO;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace TDPdf.Services
{
    /// <summary>
    /// What the bake needs from the window that only WPF can answer: how big a text annotation's
    /// box is on the canvas, and where its text wraps. Everything else the bake does is
    /// PdfSharpCore, which is why the rest of this file runs under tests/PdfCore.
    /// </summary>
    /// <param name="Measure">The canvas size of a text annotation (the window's
    /// <c>MeasureTextAnnotation</c>).</param>
    /// <param name="Wrap">The lines a text annotation wraps to at <c>maxWidth</c> canvas px, in the
    /// typeface the on-screen preview lays it out in (the window's <c>WrapTextToWidth</c>).</param>
    internal sealed record AnnotationTextLayout(
        Func<TextAnnotation, (double Width, double Height)> Measure,
        Func<TextAnnotation, double, List<string>> Wrap);

    /// <summary>
    /// Burns the window's pending annotations into a PdfSharpCore document: one method per
    /// annotation kind, driven by <see cref="Bake"/>. Moved out of <c>MainWindow</c>
    /// (<c>DrawAnnotationsOnDocument</c>) without changing what is drawn; the page frame and the
    /// two WPF text measurements arrive as inputs.
    /// </summary>
    internal static class AnnotationBaker
    {
        /// <summary>
        /// Burns <paramref name="annotations"/> into <paramref name="doc"/>.
        /// </summary>
        /// <param name="doc">The document being saved.</param>
        /// <param name="annotations">Pending annotations keyed by 0-based page index.</param>
        /// <param name="pageFrame">The DIP canvas each page was drawn on (the window's
        /// <c>EnsureRenderDims</c>); a zero or negative size skips the page.</param>
        /// <param name="text">The WPF-backed text measurements.</param>
        internal static void Bake(PdfDocument doc,
                                  IEnumerable<KeyValuePair<int, List<PageAnnotation>>> annotations,
                                  Func<int, (int w, int h)> pageFrame,
                                  AnnotationTextLayout text)
        {
            // Strip link annotation borders so they don't render as colored rectangles
            // (e.g. strikethrough-like lines) in other PDF viewers.
            StripLinkAnnotationBorders(doc);

            foreach (var kvp in annotations)
            {
                int pageIdx = kvp.Key;
                var annots = kvp.Value;
                if (annots.Count == 0 || pageIdx < 0 || pageIdx >= doc.PageCount) continue;

                var page = doc.Pages[pageIdx];
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
                var (renderW, renderH) = pageFrame(pageIdx);
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
                // RasterToXGraphics is the one place that turns a PDFium pixel into a visual-frame
                // point (scale against DisplaySize) and supplies the matrix back into XGraphics space;
                // the OCR text layer uses it too. tests/PdfCore pins it against the old inline maths.
                var (sx, sy, visualToPage) = PdfPageGeometry.RasterToXGraphics(page, renderW, renderH);

                using (var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append))
                {
                    // ...then turn the whole drawing back into the frame XGraphics writes in, so every
                    // draw call below keeps working in canvas-scaled coordinates exactly as it always has.
                    if (visualToPage is XMatrix m)
                        gfx.MultiplyTransform(m, XMatrixOrder.Prepend);

                    foreach (var annot in InPaintOrder(annots))
                        BakeOne(gfx, annot, sx, sy, text);
                }

                // Stamps are not burned into the page: each is written as a real /Stamp annotation with
                // its own appearance stream (Services/StampWriter.cs), in the same page frame as above.
                foreach (var annot in annots)
                    if (annot is StampAnnotation stamp)
                        StampWriter.Write(doc, page, stamp, sx, sy, visualToPage);
            }
        }

        /// <summary>
        /// Draws one annotation, turned about its box centre first when it is a rotated text box,
        /// signature or picture, and puts the page transform back afterwards.
        /// </summary>
        private static void BakeOne(XGraphics gfx, PageAnnotation annot, double sx, double sy,
                                    AnnotationTextLayout text)
        {
            // Text boxes, signatures and pictures can be turned in quarter turns: draw them
            // upright in their own box, turned about its centre (the same centre the canvas
            // uses), and put the page transform back afterwards.
            var turnState = default(XGraphicsState);
            bool turned = false;
            if (BoxTurn(annot, text) is { } turn && turn.Rotation != 0)
            {
                turnState = gfx.Save();
                gfx.RotateAtTransform(turn.Rotation, new XPoint(turn.CenterX * sx, turn.CenterY * sy));
                turned = true;
            }
            try
            {
                switch (annot)
                {
                    case TextAnnotation ta:
                        BakeText(gfx, ta, sx, sy, text);
                        break;

                    // Markup: one filled band per covered line. Matched before
                    // HighlightAnnotation — it is a subclass, and a plain highlight must keep
                    // writing exactly the single rectangle it always did.
                    case MarkupAnnotation mk:
                        BakeMarkup(gfx, mk, sx, sy);
                        break;

                    case HighlightAnnotation ha:
                        BakeHighlight(gfx, ha, sx, sy);
                        break;

                    case ShapeAnnotation shp:
                        BakeShape(gfx, shp, sx, sy);
                        break;

                    case InkAnnotation ia:
                        BakeInk(gfx, ia, sx, sy);
                        break;

                    case TextEditAnnotation tea:
                        BakeTextEdit(gfx, tea, sx, sy);
                        break;

                    case ImageEditAnnotation iea:
                        BakeImageEdit(gfx, iea, sx, sy);
                        break;

                    case SignatureAnnotation sa:
                        BakeSignature(gfx, sa, sx, sy);
                        break;

                    case ImageAnnotation img:
                        BakeImage(gfx, img, sx, sy);
                        break;

                    // A stamp is a real annotation, written after this pass (see Bake).
                    case StampAnnotation:
                        break;
                }
            }
            finally
            {
                if (turned) gfx.Restore(turnState);
            }
        }

        // ============================================================
        // One method per annotation kind
        // ============================================================

        private static void BakeText(XGraphics gfx, TextAnnotation ta, double sx, double sy,
                                     AnnotationTextLayout text)
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
            if (font is null) return;
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
                var wrapped = text.Wrap(ta, ta.Width - pad * 2);
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
                    var sz = text.Measure(ta);
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
        }

        private static void BakeMarkup(XGraphics gfx, MarkupAnnotation mk, double sx, double sy)
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
        }

        private static void BakeHighlight(XGraphics gfx, HighlightAnnotation ha, double sx, double sy)
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
        }

        private static void BakeShape(XGraphics gfx, ShapeAnnotation shp, double sx, double sy)
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
        }

        private static void BakeInk(XGraphics gfx, InkAnnotation ia, double sx, double sy)
        {
            var ic = ia.GetColor();
            // A pen dot is a filled circle, never a zero-length stroke — see InkDot.
            // Its diameter scales with sx, exactly as the pen width below does.
            if (InkDot.TryGetDot(ia.Points.Select(p => (p.X, p.Y)), out double dotX, out double dotY))
            {
                InkDot.Burn(gfx, XColor.FromArgb(ic.A, ic.R, ic.G, ic.B),
                    dotX * sx, dotY * sy, ia.StrokeWidth * sx);
                return;
            }
            if (ia.Points.Count < 2) return;
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
        }

        private static void BakeTextEdit(XGraphics gfx, TextEditAnnotation tea, double sx, double sy)
        {
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
            if (editFont is null) return;
            // White unless the edit was made over a scan, where the cover takes the paper's colour (#229).
            var whiteRect = new XSolidBrush(XColor.FromArgb(tea.CoverR, tea.CoverG, tea.CoverB));
            gfx.DrawRectangle(whiteRect,
                (tea.OriginalBounds.X - 1) * sx, (tea.OriginalBounds.Y - 1) * sy,
                (tea.OriginalBounds.Width + 2) * sx, (tea.OriginalBounds.Height + 2) * sy);
            double ety = tea.Position.Y * sy + tea.FontSize * sy;
            var teaColor = tea.GetColor();
            var teaBrush = new XSolidBrush(XColor.FromArgb(teaColor.A, teaColor.R, teaColor.G, teaColor.B));
            // #435: a Hebrew edit would save backwards through DrawString,
            // which does no bidi. One line only — the edit is drawn as a single
            // string, so a line break here is not a layout this can reorder.
            if (tea.NewContent.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            {
                // A paragraph edit (#229): one DrawString per line, at the line height the page had,
                // because DrawString does not break lines. Centred and right-aligned paragraphs are
                // placed inside the box they replaced.
                var paragraphLines = tea.NewContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                double lineStep = (tea.LineHeight > 0 ? tea.LineHeight : tea.FontSize * 1.2) * sy;
                double boxLeft = tea.OriginalBounds.X * sx, boxWidth = tea.OriginalBounds.Width * sx;
                for (int li = 0; li < paragraphLines.Length; li++)
                {
                    string text = paragraphLines[li];
                    if (text.Length == 0) continue;
                    double lineWidth = gfx.MeasureString(text, editFont).Width;
                    double lineX = tea.Align switch
                    {
                        1 => boxLeft + Math.Max(0, (boxWidth - lineWidth) / 2),
                        2 => boxLeft + Math.Max(0, boxWidth - lineWidth),
                        _ => tea.Position.X * sx,
                    };
                    gfx.DrawString(text, editFont, teaBrush, lineX, ety + li * lineStep);
                }
            }
            else if (RtlTextLayout.IsHebrewRightToLeftLine(tea.NewContent))
                DrawRightToLeft(gfx, tea.NewContent, 0, tea.NewContent.Length, editFont, teaBrush,
                                tea.Position.X * sx, ety, 0);
            else
                gfx.DrawString(tea.NewContent, editFont, teaBrush, tea.Position.X * sx, ety);
        }

        private static void BakeImageEdit(XGraphics gfx, ImageEditAnnotation iea, double sx, double sy)
        {
            var imageWhiteRect = new XSolidBrush(XColors.White);
            gfx.DrawRectangle(imageWhiteRect,
                (iea.OriginalBounds.X - 2) * sx, (iea.OriginalBounds.Y - 2) * sy,
                (iea.OriginalBounds.Width + 4) * sx, (iea.OriginalBounds.Height + 4) * sy);
            if (iea.IsDeleted) return;
            try
            {
                XImage? xImg = null;
                if (!string.IsNullOrWhiteSpace(iea.ReplacementImagePath) && File.Exists(iea.ReplacementImagePath))
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
                    int rotation = Angles.Mod360(iea.Rotation);
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

        private static void BakeSignature(XGraphics gfx, SignatureAnnotation sa, double sx, double sy)
        {
            // The date line, when there is one, takes the bottom DateStrip units of the box; the
            // signature itself fills the rest.
            double strip = SignatureStrip(sa);
            if (sa.ImageData is not null)
            {
                try
                {
                    var imgBytes = Convert.FromBase64String(sa.ImageData);
                    var xImg = XImage.FromStream(() => new MemoryStream(imgBytes));
                    double imgX = sa.Position.X * sx;
                    double imgY = sa.Position.Y * sy;
                    double imgW = sa.SourceWidth * sa.Scale * sx;
                    double imgH = (sa.SourceHeight - strip) * sa.Scale * sy;
                    gfx.DrawImage(xImg, imgX, imgY, imgW, imgH);
                }
                catch { /* skip broken image */ }
            }
            else
            {
                var (ir, ig, ib) = SignatureInk.ResolveColor(sa.InkColor);
                var sigPen = new XPen(XColor.FromArgb(255, ir, ig, ib), SignatureInk.ResolvePenWidth(sa.PenWidth) * sa.Scale * sx)
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
            BakeSignatureDate(gfx, sa, strip, sx, sy);
        }

        /// <summary>The date strip height of a signature, or 0 when it has no usable date line.</summary>
        private static double SignatureStrip(SignatureAnnotation sa) =>
            SignatureInk.EffectiveDateStrip(sa.DateText, sa.DateStrip, sa.SourceHeight);

        private static void BakeSignatureDate(XGraphics gfx, SignatureAnnotation sa, double strip, double sx, double sy)
        {
            if (strip <= 0 || string.IsNullOrEmpty(sa.DateText)) return;
            try
            {
                double fontUnits = SignatureInk.DateFontSize(strip, sa.SourceWidth);
                var font = TdpFontResolver.TryCreate(PdfFontStyle.DefaultFamily, fontUnits * sa.Scale * sy, XFontStyle.Regular);
                if (font is null) return;
                // Ink colour for a drawn signature; a picture's colour is inside the picture, so black.
                var (ir, ig, ib) = sa.ImageData is null ? SignatureInk.ResolveColor(sa.InkColor) : ((byte)0, (byte)0, (byte)0);
                var brush = new XSolidBrush(XColor.FromArgb(255, ir, ig, ib));
                double x = sa.Position.X * sx;
                double baseline = (sa.Position.Y + (sa.SourceHeight - strip) * sa.Scale + strip * sa.Scale * 0.78) * sy;
                gfx.DrawString(sa.DateText, font, brush, x, baseline);
            }
            catch { /* a date line that cannot be written must not lose the signature */ }
        }

        private static void BakeImage(XGraphics gfx, ImageAnnotation ia, double sx, double sy)
        {
            try
            {
                var iaBytes = Convert.FromBase64String(ia.ImageData);
                var xia = XImage.FromStream(() => new MemoryStream(iaBytes));
                double iaX = ia.Position.X * sx;
                double iaY = ia.Position.Y * sy;
                double iaW = ia.SourceWidth * ia.Scale * sx;
                double iaH = ia.SourceHeight * ia.Scale * sy;
                gfx.DrawImage(xia, iaX, iaY, iaW, iaH);
            }
            catch { /* skip broken image */ }
        }

        // ============================================================
        // Shared helpers
        // ============================================================

        /// <summary>
        /// A whiteout is a background layer, not a peer, so it is ordered as one: the in-place text
        /// and image edits first, everything else after, each group in its existing relative order.
        /// </summary>
        internal static IEnumerable<PageAnnotation> InPaintOrder(IList<PageAnnotation> annots)
        {
            for (int i = 0; i < annots.Count; i++)
                if (annots[i] is TextEditAnnotation or ImageEditAnnotation) yield return annots[i];
            for (int i = 0; i < annots.Count; i++)
                if (annots[i] is not (TextEditAnnotation or ImageEditAnnotation)) yield return annots[i];
        }

        /// <summary>
        /// Strips visual styling (border, color, appearance stream) from all Link annotations
        /// in the document so they render as invisible clickable areas rather than colored
        /// rectangles that can look like strikethroughs in other PDF viewers.
        /// </summary>
        internal static void StripLinkAnnotationBorders(PdfDocument doc)
        {
            foreach (var pdfPage in doc.Pages)
            {
                var annotsArr = pdfPage.Elements.GetArray("/Annots");
                if (annotsArr is null) continue;
                for (int i = 0; i < annotsArr.Elements.Count; i++)
                {
                    PdfItem? elem = annotsArr.Elements[i];
                    PdfDictionary? ann = elem as PdfDictionary ?? PdfItems.Deref(elem) as PdfDictionary;
                    if (ann is null) continue;
                    var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                    if (!subtype.Contains("Link")) continue;
                    // Remove appearance stream and color; set /Border [0 0 0] for invisible border.
                    ann.Elements.Remove("/AP");
                    ann.Elements.Remove("/C");
                    ann.Elements.Remove("/BS");
                    var borderArr = new PdfArray();
                    borderArr.Elements.Add(new PdfInteger(0));
                    borderArr.Elements.Add(new PdfInteger(0));
                    borderArr.Elements.Add(new PdfInteger(0));
                    ann.Elements["/Border"] = borderArr;
                }
            }
        }

        /// <summary>
        /// The turn of a text box, signature or picture and the canvas point it turns about (the
        /// centre of its unrotated box), or null for anything that cannot be turned.
        /// </summary>
        internal static (int Rotation, double CenterX, double CenterY)? BoxTurn(PageAnnotation annot,
                                                                               AnnotationTextLayout text)
        {
            switch (annot)
            {
                case TextAnnotation ta:
                {
                    int r = AnnotationFrame.Normalize(ta.Rotation);
                    if (r == 0) return (0, 0, 0);
                    var sz = text.Measure(ta);
                    return (r, ta.Position.X + sz.Width / 2, ta.Position.Y + sz.Height / 2);
                }
                case PlacedAnnotation pa:
                {
                    int r = AnnotationFrame.Normalize(pa.Rotation);
                    if (r == 0) return (0, 0, 0);
                    return (r, pa.Position.X + pa.SourceWidth * pa.Scale / 2, pa.Position.Y + pa.SourceHeight * pa.Scale / 2);
                }
                default:
                    return null;
            }
        }

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
        /// why the window's <c>TextMeasurers</c> has to hand <see cref="TextLetterSpacing.Width"/> two
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
    }
}
