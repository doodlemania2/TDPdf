// MainWindow — unsaved annotations on the Pages sidebar's thumbnails.
//
// Annotations are WPF overlays on the page canvas (_annotations, per page) until the file is
// saved, so a thumbnail rendered from the file alone never shows them. This draws the page's
// annotations, from the annotation model, into a small frozen DrawingImage in the page canvas's
// own coordinate frame, and the sidebar stretches it over the thumbnail. It is deliberately
// lightweight: no PDF is re-baked, and nothing here touches PDFium. It is a preview, so it is not
// pixel-exact (no letter-spacing, no multiply blend on highlights); the saved file is the
// authority.

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        private List<PageThumbItem>? _thumbItems;
        private readonly HashSet<int> _thumbOverlayDirty = new();
        private DispatcherTimer? _thumbOverlayTimer;

        /// <summary>
        /// The annotations on a page changed: rebuild that page's thumbnail overlay shortly, once
        /// however many changes land in a burst (a drag fires many). Called from the one place every
        /// annotation edit funnels through, <see cref="RenderAllAnnotations"/>.
        /// </summary>
        private void QueueThumbOverlayRefresh(int pageIndex)
        {
            if (_thumbItems is null || pageIndex < 0 || pageIndex >= _thumbItems.Count) return;
            _thumbOverlayDirty.Add(pageIndex);
            if (_thumbOverlayTimer is null)
            {
                _thumbOverlayTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
                _thumbOverlayTimer.Tick += (_, _) =>
                {
                    _thumbOverlayTimer.Stop();
                    var pages = _thumbOverlayDirty.ToArray();
                    _thumbOverlayDirty.Clear();
                    foreach (int p in pages) RefreshThumbOverlay(p);
                };
            }
            _thumbOverlayTimer.Stop();
            _thumbOverlayTimer.Start();
        }

        private void RefreshThumbOverlay(int pageIndex)
        {
            if (_thumbItems is null || pageIndex < 0 || pageIndex >= _thumbItems.Count) return;
            var item = _thumbItems[pageIndex];
            // An entry that is not on screen gets its overlay when it is shown.
            item.Overlay = item.Realised ? BuildThumbOverlay(pageIndex) : null;
        }

        /// <summary>The page's annotations as a drawing in canvas coordinates, or null when it has none.</summary>
        private ImageSource? BuildThumbOverlay(int pageIndex)
        {
            if (!_annotations.TryGetValue(pageIndex, out var list) || list.Count == 0) return null;
            var (w, h) = EnsureRenderDims(pageIndex);
            if (w <= 0 || h <= 0) return null;

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var frame = new Rect(0, 0, w, h);
            var group = new DrawingGroup { ClipGeometry = new RectangleGeometry(frame) };
            using (var dc = group.Open())
            {
                // Pins the drawing's extent to the whole page so Stretch=Fill maps it 1:1 onto the thumbnail.
                dc.DrawRectangle(Brushes.Transparent, null, frame);
                foreach (var annot in InPaintOrder(list))
                {
                    try { DrawThumbAnnotation(dc, annot, dpi); }
                    catch { /* one unrenderable annotation must not blank the rest */ }
                }
            }
            group.Freeze();
            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }

        private static Pen? ThumbPen(Color c, double width)
        {
            if (!IsFinitePositive(width)) return null;
            var pen = new Pen(TdpDialog.FrozenSolidColorBrush(c), width)
            {
                LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round
            };
            pen.Freeze();
            return pen;
        }

        private static StreamGeometry ThumbPolyline(IReadOnlyList<Point> pts, bool close)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(pts[0], close, close);
                for (int i = 1; i < pts.Count; i++) ctx.LineTo(pts[i], true, true);
            }
            g.Freeze();
            return g;
        }

        private void DrawThumbAnnotation(DrawingContext dc, PageAnnotation annot, double dpi)
        {
            switch (annot)
            {
                case TextAnnotation ta:
                {
                    if (!IsFinite(ta.Width) || !IsFinite(ta.Height) || !IsFinitePositive(ta.FontSize)) break;
                    var ft = new FormattedText(
                        string.IsNullOrEmpty(ta.Content) ? " " : ta.Content,
                        System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        TextTypeface(ta.FontName, ta.Bold, ta.Italic), ta.FontSize,
                        TdpDialog.FrozenSolidColorBrush(ta.GetColor()), dpi);
                    if (ta.Width > 0) ft.MaxTextWidth = Math.Max(1, ta.Width - 4);
                    if (ta.Underline) ft.SetTextDecorations(TextDecorations.Underline);
                    if (ta.HasFill)
                    {
                        double bw = ta.Width > 0 ? ta.Width : ft.WidthIncludingTrailingWhitespace + 4;
                        double bh = ta.Height > 0 ? ta.Height : ft.Height + 4;
                        dc.DrawRectangle(TdpDialog.FrozenSolidColorBrush(ta.GetFillColor()), null,
                                         new Rect(ta.Position.X, ta.Position.Y, bw, bh));
                    }
                    dc.DrawText(ft, new Point(ta.Position.X + 2, ta.Position.Y + 2));   // the on-screen box pads by 2
                    break;
                }
                case MarkupAnnotation mk:
                {
                    var brush = TdpDialog.FrozenSolidColorBrush(mk.GetColor());
                    foreach (var pr in mk.PaintRects())
                        if (IsFinitePositive(pr.Width) && IsFinitePositive(pr.Height)) dc.DrawRectangle(brush, null, pr);
                    break;
                }
                case HighlightAnnotation ha:
                    if (IsFinitePositive(ha.Bounds.Width) && IsFinitePositive(ha.Bounds.Height))
                        dc.DrawRectangle(TdpDialog.FrozenSolidColorBrush(ha.GetColor()), null, ha.Bounds);
                    break;
                case ShapeAnnotation shp:
                {
                    var pen = ThumbPen(shp.GetStrokeColor(), shp.StrokeWidth);
                    Brush? fill = shp.HasFill ? TdpDialog.FrozenSolidColorBrush(shp.GetFillColor()) : null;
                    var b = shp.Bounds;
                    switch (shp.Kind)
                    {
                        case ShapeKind.Rectangle:
                            if (IsFinite(b.Width) && IsFinite(b.Height))
                                dc.DrawRectangle(fill, pen, new Rect(b.X, b.Y, Math.Max(1, b.Width), Math.Max(1, b.Height)));
                            break;
                        case ShapeKind.Ellipse:
                            if (IsFinite(b.Width) && IsFinite(b.Height))
                                dc.DrawEllipse(fill, pen, new Point(b.X + b.Width / 2, b.Y + b.Height / 2),
                                               Math.Max(1, b.Width) / 2, Math.Max(1, b.Height) / 2);
                            break;
                        case ShapeKind.Polygon:
                            if (shp.Points.Count >= 2) dc.DrawGeometry(fill, pen, ThumbPolyline(shp.Points, true));
                            break;
                        case ShapeKind.Line:
                            dc.DrawLine(pen ?? new Pen(Brushes.Black, 1), shp.Start, shp.End);
                            break;
                    }
                    break;
                }
                case InkAnnotation ia:
                {
                    if (InkDot.TryGetDot(ia.Points.Select(p => (p.X, p.Y)), out double cx, out double cy)
                        && IsFinitePositive(ia.StrokeWidth))
                    {
                        dc.DrawEllipse(TdpDialog.FrozenSolidColorBrush(ia.GetColor()), null,
                                       new Point(cx, cy), ia.StrokeWidth / 2, ia.StrokeWidth / 2);
                        break;
                    }
                    if (ia.Points.Count >= 2)
                        dc.DrawGeometry(null, ThumbPen(ia.GetColor(), ia.StrokeWidth), ThumbPolyline(ia.Points, false));
                    break;
                }
                case TextEditAnnotation tea:
                {
                    if (!IsFinite(tea.OriginalBounds.Width) || !IsFinite(tea.OriginalBounds.Height)) break;
                    dc.DrawRectangle(Brushes.White, null, new Rect(tea.OriginalBounds.X - 1, tea.OriginalBounds.Y - 1,
                                     tea.OriginalBounds.Width + 2, tea.OriginalBounds.Height + 2));
                    if (IsFinitePositive(tea.FontSize) && !string.IsNullOrEmpty(tea.NewContent))
                    {
                        var ft = new FormattedText(tea.NewContent, System.Globalization.CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight, TextTypeface(tea.FontName, tea.Bold, tea.Italic), tea.FontSize,
                            TdpDialog.FrozenSolidColorBrush(tea.GetColor()), dpi);
                        dc.DrawText(ft, tea.Position);
                    }
                    break;
                }
                case ImageEditAnnotation iea:
                {
                    if (!IsFinite(iea.OriginalBounds.Width) || !IsFinite(iea.OriginalBounds.Height)) break;
                    dc.DrawRectangle(Brushes.White, null, new Rect(iea.OriginalBounds.X - 2, iea.OriginalBounds.Y - 2,
                                     iea.OriginalBounds.Width + 4, iea.OriginalBounds.Height + 4));
                    if (!iea.IsDeleted && LoadImageEditBitmap(iea) is { } src
                        && IsFinitePositive(iea.TargetBounds.Width) && IsFinitePositive(iea.TargetBounds.Height))
                        dc.DrawImage(src, iea.TargetBounds);
                    break;
                }
                case SignatureAnnotation sa:
                {
                    if (!TryGetPlacedSize(sa, out double sw, out double sh)) break;
                    if (sa.ImageData is not null)
                    {
                        if (DecodeThumbImage(sa.ImageData) is { } img)
                            dc.DrawImage(img, new Rect(sa.Position.X, sa.Position.Y, sw, sh - SigStrip(sa) * sa.Scale));
                        break;
                    }
                    var (tr, tg, tb) = SignatureInk.ResolveColor(sa.InkColor);
                    var pen = ThumbPen(Color.FromRgb(tr, tg, tb), SignatureInk.ResolvePenWidth(sa.PenWidth) * sa.Scale);
                    foreach (var stroke in sa.Strokes)
                    {
                        if (stroke.Count < 2) continue;
                        var pts = stroke.Select(pt => new Point(sa.Position.X + pt.X * sa.Scale,
                                                                sa.Position.Y + pt.Y * sa.Scale)).ToList();
                        dc.DrawGeometry(null, pen, ThumbPolyline(pts, false));
                    }
                    break;
                }
                case StampAnnotation stamp:
                {
                    if (!TryGetPlacedSize(stamp, out _, out _)) break;
                    dc.PushTransform(new TranslateTransform(stamp.Position.X, stamp.Position.Y));
                    DrawStamp(dc, stamp, dpi);
                    dc.Pop();
                    break;
                }
                case ImageAnnotation pic:
                {
                    if (!TryGetPlacedSize(pic, out double pw, out double ph)) break;
                    if (DecodeThumbImage(pic.ImageData) is { } img)
                        dc.DrawImage(img, new Rect(pic.Position.X, pic.Position.Y, pw, ph));
                    break;
                }
            }
        }

        /// <summary>A placed image decoded small: the thumbnail is ~140 px wide, so full resolution would only cost memory.</summary>
        private static BitmapSource? DecodeThumbImage(string base64)
        {
            try
            {
                var bytes = Convert.FromBase64String(base64);
                var bmp = new BitmapImage();
                using (var ms = new System.IO.MemoryStream(bytes))
                {
                    bmp.BeginInit();
                    bmp.StreamSource = ms;
                    bmp.DecodePixelWidth = 400;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                }
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }
}
