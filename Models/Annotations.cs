using System.Windows;
using System.Windows.Media;

namespace TDPdf
{
    // Strikethrough / Underline are the text-markup siblings of Highlight (upstream KillerPDF
    // v1.6.5, #127). Appended rather than inserted so the numeric values of the existing members
    // never shift — the persisted view/tool settings round-trip through them.
    public enum EditTool { Select, Text, Highlight, Draw, Signature, Image, EditText, EditImage, Crop, Pan, Erase, Shape, Strikethrough, Underline }

    public enum ShapeKind { Rectangle, Ellipse, Line, Polygon }

    /// <summary>
    /// How a <see cref="MarkupAnnotation"/> paints over each of the text lines it covers
    /// (upstream KillerPDF v1.6.5, #127).
    /// </summary>
    public enum MarkupStyle { Highlight, Strikethrough, Underline }

    public enum ZoomFitMode { None, Width, Page }

    public abstract class PageAnnotation
    {
        public int PageIndex { get; set; }

        /// <summary>
        /// Deep-copies this annotation. Used by the snapshot-undo subsystem so that subsequent
        /// in-place mutations (move, resize, content edits) don't leak into the captured state.
        /// </summary>
        public abstract PageAnnotation Clone();
    }

    /// <summary>
    /// Base class for placed/resizable annotations (signature, image).
    /// Carries the shared position, scale, and source-dimension properties used by the resize handle.
    /// </summary>
    public abstract class PlacedAnnotation : PageAnnotation
    {
        public Point Position { get; set; }
        public double Scale { get; set; } = 0.5;
        public double SourceWidth { get; set; } = 400;
        public double SourceHeight { get; set; } = 150;
    }

    public class TextAnnotation : PageAnnotation
    {
        public Point Position { get; set; }
        public string Content { get; set; } = "";
        public double FontSize { get; set; } = 14;

        /// <summary>
        /// Family name from the curated picker in the text style bar (TextFontFamilies). Defaults to
        /// PdfFontStyle.DefaultFamily, so an annotation written before this existed deserializes to
        /// exactly the font it has always rendered in.
        /// </summary>
        public string FontName { get; set; } = TDPdf.Services.PdfFontStyle.DefaultFamily;

        /// <summary>
        /// Character styling. All three default to off, so an annotation written by any earlier
        /// build deserializes to exactly the appearance it has always had — the same forward
        /// compatibility rule <see cref="SavedSignature"/> relies on. #135.
        /// </summary>
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }

        public byte ColorR { get; set; } = 0;
        public byte ColorG { get; set; } = 0;
        public byte ColorB { get; set; } = 0;
        public byte ColorA { get; set; } = 255;

        /// <summary>
        /// Fixed wrap width of the text box, in canvas pixels; text wraps to this width.
        /// 0 = legacy auto-size (single-line / newline-split, no wrap) — backward compatible.
        /// </summary>
        public double Width { get; set; }

        /// <summary>
        /// Fixed box height, in canvas pixels. 0 = auto-grow to fit the wrapped text.
        /// </summary>
        public double Height { get; set; }

        /// <summary>Paint an opaque "whiteout" rectangle behind the text when true.</summary>
        public bool HasFill { get; set; }
        public byte FillR { get; set; } = 255;
        public byte FillG { get; set; } = 255;
        public byte FillB { get; set; } = 255;
        public byte FillA { get; set; } = 255;

        public Color GetColor() => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);
        public void SetColor(Color c) { ColorR = c.R; ColorG = c.G; ColorB = c.B; ColorA = c.A; }

        public Color GetFillColor() => Color.FromArgb(FillA, FillR, FillG, FillB);
        public void SetFillColor(Color c) { FillR = c.R; FillG = c.G; FillB = c.B; FillA = c.A; }

        public override PageAnnotation Clone() => new TextAnnotation
        {
            PageIndex = PageIndex, Position = Position, Content = Content, FontSize = FontSize,
            FontName = FontName, Bold = Bold, Italic = Italic, Underline = Underline,
            ColorR = ColorR, ColorG = ColorG, ColorB = ColorB, ColorA = ColorA,
            Width = Width, Height = Height,
            HasFill = HasFill, FillR = FillR, FillG = FillG, FillB = FillB, FillA = FillA
        };
    }

    public class InkAnnotation : PageAnnotation
    {
        public List<Point> Points { get; set; } = new();
        public double StrokeWidth { get; set; } = 2;
        public byte ColorR { get; set; } = 255;
        public byte ColorG { get; set; } = 0;
        public byte ColorB { get; set; } = 0;
        public byte ColorA { get; set; } = 255;

        public Color GetColor() => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);
        public void SetColor(Color c) { ColorR = c.R; ColorG = c.G; ColorB = c.B; ColorA = c.A; }

        public override PageAnnotation Clone() => new InkAnnotation
        {
            PageIndex = PageIndex,
            Points = new List<Point>(Points),
            StrokeWidth = StrokeWidth,
            ColorR = ColorR, ColorG = ColorG, ColorB = ColorB, ColorA = ColorA
        };
    }

    public class HighlightAnnotation : PageAnnotation
    {
        public Rect Bounds { get; set; }
        public byte ColorR { get; set; } = 255;
        public byte ColorG { get; set; } = 255;
        public byte ColorB { get; set; } = 0;
        public byte ColorA { get; set; } = 80;

        public Color GetColor() => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);
        public void SetColor(Color c) { ColorR = c.R; ColorG = c.G; ColorB = c.B; ColorA = c.A; }

        public override PageAnnotation Clone() => new HighlightAnnotation
        {
            PageIndex = PageIndex, Bounds = Bounds,
            ColorR = ColorR, ColorG = ColorG, ColorB = ColorB, ColorA = ColorA
        };
    }

    /// <summary>
    /// Text markup that hugs the individual lines of text it was dragged across: highlight,
    /// strikethrough, or underline (upstream KillerPDF v1.6.5, #127).
    ///
    /// One gesture produces exactly ONE of these per page, carrying a rect per covered line in
    /// <see cref="LineRects"/>, so the whole run selects, moves, resizes, deletes, and undoes as a
    /// single unit without needing any grouping machinery elsewhere.
    ///
    /// It subclasses <see cref="HighlightAnnotation"/> so it inherits every existing rect path for
    /// free (selection, move, resize, style bar, hit-test fallback), and <see cref="Bounds"/> is
    /// kept as the union of the line rects so those inherited paths stay correct. Plain
    /// <see cref="HighlightAnnotation"/>s — every highlight created before this existed, plus the
    /// eraser's rectangle — are a different runtime type and keep their exact old rendering, saving,
    /// and behaviour: every switch that handles markup matches <c>MarkupAnnotation</c> BEFORE
    /// <c>HighlightAnnotation</c> and leaves the latter's arm untouched.
    /// </summary>
    public class MarkupAnnotation : HighlightAnnotation
    {
        public MarkupStyle Style { get; set; } = MarkupStyle.Highlight;

        /// <summary>
        /// Clockwise page rotation applied since this markup was created. Highlights use full rects
        /// and ignore it; underline and strikethrough use it to keep their painted band aligned with
        /// text that became vertical after a page rotation.
        /// </summary>
        public int Rotation { get; set; }

        /// <summary>
        /// One rect per covered text line, in canvas pixels, in reading order. Each rect is the
        /// FULL line box; <see cref="PaintRects"/> narrows it to a band for strikethrough/underline.
        /// </summary>
        public List<Rect> LineRects { get; set; } = new();

        /// <summary>The union of <see cref="LineRects"/> — what <see cref="Bounds"/> is kept at.</summary>
        public Rect UnionBounds()
        {
            if (LineRects.Count == 0) return Bounds;
            Rect u = LineRects[0];
            for (int i = 1; i < LineRects.Count; i++) u.Union(LineRects[i]);
            return u;
        }

        /// <summary>Recomputes <see cref="Bounds"/> from the current line rects.</summary>
        public void SyncBounds() => Bounds = UnionBounds();

        /// <summary>
        /// The rectangles actually painted (on canvas and into the saved PDF): the whole line box
        /// for a highlight, a thin band at the vertical centre for strikethrough, and a thin band at
        /// the foot of the line for underline. Falls back to <see cref="Bounds"/> for a degenerate
        /// annotation with no line rects so it can never render as nothing.
        /// </summary>
        public IEnumerable<Rect> PaintRects()
        {
            var source = LineRects.Count > 0 ? LineRects : new List<Rect> { Bounds };
            foreach (var line in source)
            {
                int rotation = ((Rotation % 360) + 360) % 360;
                bool vertical = rotation is 90 or 270;
                double t = Math.Max(1.5, (vertical ? line.Width : line.Height) * 0.09);
                switch (Style)
                {
                    case MarkupStyle.Strikethrough:
                        yield return vertical
                            ? new Rect(line.X + line.Width / 2 - t / 2, line.Y, t, line.Height)
                            : new Rect(line.X, line.Y + line.Height / 2 - t / 2, line.Width, t);
                        break;
                    case MarkupStyle.Underline:
                        yield return rotation switch
                        {
                            90 => new Rect(line.X, line.Y, t, line.Height),
                            180 => new Rect(line.X, line.Y, line.Width, t),
                            270 => new Rect(line.X + Math.Max(0, line.Width - t), line.Y, t, line.Height),
                            _ => new Rect(line.X, line.Y + Math.Max(0, line.Height - t), line.Width, t),
                        };
                        break;
                    default:
                        yield return line;
                        break;
                }
            }
        }

        public override PageAnnotation Clone()
        {
            var copy = new MarkupAnnotation
            {
                PageIndex = PageIndex, Bounds = Bounds, Style = Style, Rotation = Rotation,
                ColorR = ColorR, ColorG = ColorG, ColorB = ColorB, ColorA = ColorA
            };
            copy.LineRects.AddRange(LineRects);
            return copy;
        }
    }

    /// <summary>
    /// Transient crop rectangle used only as an on-canvas UI overlay while applying a crop.
    /// </summary>
    public class CropAnnotation : PageAnnotation
    {
        public Rect Bounds { get; set; }

        public override PageAnnotation Clone() => new CropAnnotation { PageIndex = PageIndex, Bounds = Bounds };
    }

    /// <summary>
    /// Represents an edit to existing PDF text: whites out original bounds, draws replacement.
    /// </summary>
    public class TextEditAnnotation : PageAnnotation
    {
        public Rect OriginalBounds { get; set; }
        public Point Position { get; set; }
        public string NewContent { get; set; } = "";
        public string OriginalContent { get; set; } = "";
        public double FontSize { get; set; } = 14;
        public string FontName { get; set; } = "Segoe UI";
        /// <summary>
        /// Face styling detected on the replaced PDF text (#182). PDF font resources encode bold and
        /// italic in the font name rather than in separate metadata, so these carry that through the
        /// edit box, the canvas preview and the saved page. Both default to false, which is also what
        /// any data written before these existed deserializes to.
        /// </summary>
        public bool Bold { get; set; }
        public bool Italic { get; set; }

        /// <summary>
        /// Replacement-text color. Defaults to black, matching every edit made before this
        /// existed (the renderer and PDF writer both hardcoded black).
        /// </summary>
        public byte ColorR { get; set; } = 0;
        public byte ColorG { get; set; } = 0;
        public byte ColorB { get; set; } = 0;
        public byte ColorA { get; set; } = 255;

        public Color GetColor() => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);
        public void SetColor(Color c) { ColorR = c.R; ColorG = c.G; ColorB = c.B; ColorA = c.A; }

        public override PageAnnotation Clone() => new TextEditAnnotation
        {
            PageIndex = PageIndex, OriginalBounds = OriginalBounds, Position = Position,
            NewContent = NewContent, OriginalContent = OriginalContent,
            FontSize = FontSize, FontName = FontName, Bold = Bold, Italic = Italic,
            ColorR = ColorR, ColorG = ColorG, ColorB = ColorB, ColorA = ColorA
        };
    }

    /// <summary>
    /// Represents an edit to an existing PDF image: white-out original bounds, then optionally redraw.
    /// </summary>
    public class ImageEditAnnotation : PageAnnotation
    {
        public Rect OriginalBounds { get; set; }
        public Rect TargetBounds { get; set; }
        public int Rotation { get; set; }
        public string? OriginalImageData { get; set; }
        public string? ReplacementImagePath { get; set; }
        public bool IsDeleted { get; set; }

        public override PageAnnotation Clone() => new ImageEditAnnotation
        {
            PageIndex = PageIndex, OriginalBounds = OriginalBounds, TargetBounds = TargetBounds,
            Rotation = Rotation,
            OriginalImageData = OriginalImageData, ReplacementImagePath = ReplacementImagePath,
            IsDeleted = IsDeleted
        };
    }

    /// <summary>
    /// A signature placed on a PDF page: either ink strokes or an imported image.
    /// </summary>
    public class SignatureAnnotation : PlacedAnnotation
    {
        public List<List<Point>> Strokes { get; set; } = new();
        /// <summary>Base-64 encoded PNG. Non-null = image sig; null = drawn strokes.</summary>
        public string? ImageData { get; set; }

        public override PageAnnotation Clone()
        {
            var copy = new SignatureAnnotation
            {
                PageIndex = PageIndex, Position = Position, Scale = Scale,
                SourceWidth = SourceWidth, SourceHeight = SourceHeight, ImageData = ImageData
            };
            foreach (var stroke in Strokes) copy.Strokes.Add(new List<Point>(stroke));
            return copy;
        }
    }

    /// <summary>
    /// A geometric shape annotation: rectangle, ellipse, line, or free-form polygon.
    /// Rectangle / Ellipse / Line use the two-point model: Start/End endpoints, so a Line
    /// preserves direction (NW→SE vs NE→SW) and Bounds is the normalized rectangle spanning
    /// them — used for hit-testing and rendering.
    /// Polygon (upstream KillerPDF v1.6.5) instead carries its vertices in <see cref="Points"/>
    /// and leaves Start/End at their defaults; the two models never mix, so every shape created
    /// before polygons existed keeps exactly the geometry, rendering, and export it always had.
    /// </summary>
    public class ShapeAnnotation : PageAnnotation
    {
        public ShapeKind Kind { get; set; } = ShapeKind.Rectangle;
        public Point Start { get; set; }
        public Point End { get; set; }

        /// <summary>
        /// Vertices of a <see cref="ShapeKind.Polygon"/>, in canvas pixels, in placement order.
        /// The closing edge (last vertex → first) is implicit and never stored, so the list holds
        /// exactly the points the user clicked. Always empty for the three two-point kinds.
        /// (Compare <see cref="InkAnnotation.Points"/>, which is an open polyline.)
        /// </summary>
        public List<Point> Points { get; set; } = new();

        public byte StrokeR { get; set; } = 255;
        public byte StrokeG { get; set; } = 0;
        public byte StrokeB { get; set; } = 0;
        public byte StrokeA { get; set; } = 255;

        public byte FillR { get; set; } = 255;
        public byte FillG { get; set; } = 255;
        public byte FillB { get; set; } = 0;
        public byte FillA { get; set; } = 80;

        public bool HasFill { get; set; }
        public double StrokeWidth { get; set; } = 2;

        public Color GetStrokeColor() => Color.FromArgb(StrokeA, StrokeR, StrokeG, StrokeB);
        public void SetStrokeColor(Color c) { StrokeR = c.R; StrokeG = c.G; StrokeB = c.B; StrokeA = c.A; }
        public Color GetFillColor() => Color.FromArgb(FillA, FillR, FillG, FillB);
        public void SetFillColor(Color c) { FillR = c.R; FillG = c.G; FillB = c.B; FillA = c.A; }

        public Rect Bounds
        {
            get
            {
                // Polygon: the bounding box of the placed vertices. Falls through to the
                // Start/End box when the list is empty so a degenerate polygon is still Rect-safe.
                if (Kind == ShapeKind.Polygon && Points.Count > 0)
                {
                    double minX = Points[0].X, minY = Points[0].Y;
                    double maxX = minX, maxY = minY;
                    for (int i = 1; i < Points.Count; i++)
                    {
                        var p = Points[i];
                        if (p.X < minX) minX = p.X;
                        if (p.Y < minY) minY = p.Y;
                        if (p.X > maxX) maxX = p.X;
                        if (p.Y > maxY) maxY = p.Y;
                    }
                    return new Rect(minX, minY, maxX - minX, maxY - minY);
                }
                double x = System.Math.Min(Start.X, End.X);
                double y = System.Math.Min(Start.Y, End.Y);
                double w = System.Math.Abs(End.X - Start.X);
                double h = System.Math.Abs(End.Y - Start.Y);
                return new Rect(x, y, w, h);
            }
        }

        public override PageAnnotation Clone()
        {
            var copy = new ShapeAnnotation
            {
                PageIndex = PageIndex, Kind = Kind, Start = Start, End = End,
                StrokeR = StrokeR, StrokeG = StrokeG, StrokeB = StrokeB, StrokeA = StrokeA,
                FillR = FillR, FillG = FillG, FillB = FillB, FillA = FillA,
                HasFill = HasFill, StrokeWidth = StrokeWidth
            };
            // Deep-copy the vertex list so undo snapshots don't alias a later move/resize.
            copy.Points.AddRange(Points);
            return copy;
        }
    }

    /// <summary>
    /// An image placed on a PDF page as a resizable annotation.
    /// </summary>
    public class ImageAnnotation : PlacedAnnotation
    {
        /// <summary>Base-64 encoded image bytes (PNG, JPG, BMP, etc.).</summary>
        public string ImageData { get; set; } = "";

        public override PageAnnotation Clone() => new ImageAnnotation
        {
            PageIndex = PageIndex, Position = Position, Scale = Scale,
            SourceWidth = SourceWidth, SourceHeight = SourceHeight, ImageData = ImageData
        };
    }

    /// <summary>
    /// A point that can be serialized to JSON (WPF Point doesn't serialize well).
    /// </summary>
    public class SerializablePoint
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    /// <summary>
    /// A saved signature stored in the user's AppData for reuse.
    /// </summary>
    public class SavedSignature
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Signature";
        public List<List<SerializablePoint>> Strokes { get; set; } = new();
        public double CanvasWidth { get; set; } = 400;
        public double CanvasHeight { get; set; } = 150;
        /// <summary>Base-64 encoded PNG for imported image signatures. Null = drawn strokes.</summary>
        public string? ImageData { get; set; }
    }
}
