using System.Collections.Generic;
using System.Windows;

namespace TDPdf.Services
{
    // Curated from upstream KillerPDF v1.7.4/v1.7.5 (#169), adapted to TDPdf's richer annotation
    // model. A plain page rotation used to reload with SaveTempAndReload's annotation-clearing
    // default, which destroyed every overlay annotation the moment a page was rotated — committed,
    // unsaved user work vanished. Rotation now keeps the annotations and maps their canvas
    // coordinates through the turn instead. Coordinates live in the page's render-dim space (the
    // visual frame the user drew on); rotating the page turns that frame, so the old (w, h) render
    // dims become (h, w) when the reload re-renders.
    internal static class AnnotationRotate
    {
        /// <summary>
        /// Remaps one page's annotations for an in-app rotation by <paramref name="delta"/> degrees
        /// (clockwise positive, matching the render path), where <paramref name="oldW"/> and
        /// <paramref name="oldH"/> are the page's render dims BEFORE the turn. Region-shaped
        /// annotations (highlights, markup line rects, ink, shapes, crop, edit bounds) turn with the
        /// content they mark; upright items — text boxes, placed signatures/images, replacement edit
        /// text — keep their own size and orientation and follow their centre to the same page spot,
        /// clamped inside the rotated page so nothing lands off-sheet where the user can't reach it.
        ///
        /// Every applicable TDPdf annotation type is handled; unmatched types are left untouched but
        /// remain in the caller's list, so a rotation never silently drops an annotation.
        /// </summary>
        public static void Remap(IEnumerable<PageAnnotation> annots, int delta, double oldW, double oldH)
        {
            int d = ((delta % 360) + 360) % 360;
            if (d == 0) return;
            double newW = d == 90 || d == 270 ? oldH : oldW;
            double newH = d == 90 || d == 270 ? oldW : oldH;

            Point MapPoint(Point p) => d switch
            {
                // Forward quarter-turn of the visual frame, same convention as the render path's
                // clockwise bitmap rotation.
                90  => new Point(oldH - p.Y, p.X),
                270 => new Point(p.Y, oldW - p.X),
                _   => new Point(oldW - p.X, oldH - p.Y),   // 180
            };
            Rect MapRect(Rect r)
            {
                if (r.IsEmpty) return r;
                var a = MapPoint(new Point(r.X, r.Y));
                var b = MapPoint(new Point(r.Right, r.Bottom));
                return new Rect(a, b);   // Rect(Point, Point) normalizes the corners
            }
            Point MapAnchor(double x, double y, double w, double h)
            {
                var c = MapPoint(new Point(x + w / 2, y + h / 2));
                // Text, images and signatures stay upright while their centre follows the sheet. A
                // tall item near the old long edge can therefore need more room on the new axis than
                // it did before the turn. Keep the complete item reachable rather than preserving an
                // off-page coordinate the user cannot recover (#169 follow-up).
                double px = c.X - w / 2;
                double py = c.Y - h / 2;
                return new Point(
                    System.Math.Max(0, System.Math.Min(px, System.Math.Max(0, newW - w))),
                    System.Math.Max(0, System.Math.Min(py, System.Math.Max(0, newH - h))));
            }

            foreach (var annot in annots)
            {
                switch (annot)
                {
                    // MarkupAnnotation subclasses HighlightAnnotation, so it must be matched first.
                    // It carries one rect per covered text line; map each and re-derive the union
                    // Bounds the inherited paths rely on.
                    case MarkupAnnotation ma:
                        if (ma.LineRects.Count == 0)
                        {
                            ma.Bounds = MapRect(ma.Bounds);
                        }
                        else
                        {
                            for (int i = 0; i < ma.LineRects.Count; i++)
                                ma.LineRects[i] = MapRect(ma.LineRects[i]);
                            ma.SyncBounds();
                        }
                        ma.Rotation = ((ma.Rotation + d) % 360 + 360) % 360;
                        break;

                    case HighlightAnnotation ha:
                        ha.Bounds = MapRect(ha.Bounds);
                        break;

                    case InkAnnotation ia:
                        for (int i = 0; i < ia.Points.Count; i++)
                            ia.Points[i] = MapPoint(ia.Points[i]);
                        break;

                    // Rectangle/Ellipse/Line use the two-point Start/End model; Polygon uses Points.
                    // The two never mix, so mapping all three keeps whichever a given shape uses and
                    // leaves the unused members at their (harmless) defaults.
                    case ShapeAnnotation sa:
                        sa.Start = MapPoint(sa.Start);
                        sa.End = MapPoint(sa.End);
                        for (int i = 0; i < sa.Points.Count; i++)
                            sa.Points[i] = MapPoint(sa.Points[i]);
                        break;

                    case TextAnnotation ta:
                        ta.Position = MapAnchor(ta.Position.X, ta.Position.Y, ta.Width, ta.Height);
                        break;

                    // Signature / image share PlacedAnnotation; both stay upright and follow centre.
                    case PlacedAnnotation pa:
                        pa.Position = MapAnchor(pa.Position.X, pa.Position.Y,
                                                pa.SourceWidth * pa.Scale, pa.SourceHeight * pa.Scale);
                        break;

                    // Existing-text edit: the white-out covers the (rotated) original glyphs, so it
                    // turns with the content; the replacement text renders upright, so its anchor
                    // follows the same page spot, sized from the original bounds it replaced.
                    case TextEditAnnotation te:
                        te.Position = MapAnchor(te.Position.X, te.Position.Y,
                                                te.OriginalBounds.Width, te.OriginalBounds.Height);
                        te.OriginalBounds = MapRect(te.OriginalBounds);
                        break;

                    // Existing-image edit: the original white-out turns with the page content. The
                    // replacement is a live upright WPF overlay, like ImageAnnotation, so its centre
                    // follows the sheet while its dimensions stay intact rather than stretching.
                    case ImageEditAnnotation ie:
                        ie.OriginalBounds = MapRect(ie.OriginalBounds);
                        Size targetSize = ie.TargetBounds.Size;
                        Point target = MapAnchor(
                            ie.TargetBounds.X,
                            ie.TargetBounds.Y,
                            targetSize.Width,
                            targetSize.Height);
                        ie.TargetBounds = new Rect(target, targetSize);
                        break;

                    // Transient crop overlay — normally consumed by the apply-crop reload rather than
                    // living through a rotation, but mapped for completeness so it is never dropped.
                    case CropAnnotation ca:
                        ca.Bounds = MapRect(ca.Bounds);
                        break;
                }
            }
        }
    }
}
