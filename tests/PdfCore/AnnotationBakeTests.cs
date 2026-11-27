using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using PdfSharpCore.Pdf;
using TDPdf;
using TDPdf.Services;

/// <summary>
/// Round-trips every annotation kind through the real <see cref="AnnotationBaker"/>: bake into a
/// synthetic PdfSharpCore page, save, reopen, and judge the saved file (the page's content stream,
/// its blend-mode resources, its embedded fonts, what PdfPig reads back).
/// </summary>
/// <remarks>
/// Models/Annotations.cs and the baker are the shipping files, compiled in; only the WPF value types
/// (Point, Rect, Color) are stand-ins, in Shims.cs. The baker's two WPF-backed inputs (text box size
/// and word wrap) are replaced by fixed fakes, so a text annotation here is laid out by arithmetic
/// the test can predict.
///
/// What this cannot see: pixels on screen, and WPF's own measuring. It reads operators and operands
/// out of the saved content stream instead, which is what a PDF viewer follows.
/// </remarks>
internal static class AnnotationBakeTests
{
    // 1x1 PNG, valid and decodable by ImageSharp.
    private const string Png1x1 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private const double PageW = 600, PageH = 800;

    // The fake text layout: a box 100 wide and 28 tall for anything unwrapped, and a wrap that breaks
    // on spaces, one word per line.
    private static readonly AnnotationTextLayout Layout = new(
        ta => (100, 28),
        (ta, maxWidth) => ta.Content.Split(' ').ToList());

    private sealed record Baked(string Path, string Content, string Resources);

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static Baked Bake(string tmp, string name, double frameW, double frameH,
                              IReadOnlyList<PageAnnotation> annots, Action<PdfPage>? prepare = null)
    {
        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = PageW; page.Height = PageH;
        prepare?.Invoke(page);
        var dict = new Dictionary<int, List<PageAnnotation>> { [0] = annots.ToList() };
        AnnotationBaker.Bake(doc, dict, _ => ((int)frameW, (int)frameH), Layout);
        string path = Path.Combine(tmp, name + ".pdf");
        doc.Save(path);
        using var re = PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);
        var p = re.Pages[0];
        string content = p.Contents.Elements.Count == 0
            ? ""
            : Encoding.Latin1.GetString(p.Contents.CreateSingleContent().Stream.UnfilteredValue);
        return new Baked(path, content, p.Elements["/Resources"]?.ToString() ?? "");
    }

    private static Baked Bake(string tmp, string name, params PageAnnotation[] annots) =>
        Bake(tmp, name, PageW, PageH, annots);

    /// <summary>The numbers that precede <paramref name="op"/> each time it appears.</summary>
    private static List<double[]> Operands(string content, string op, int count)
    {
        var found = new List<double[]>();
        var tok = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = count; i < tok.Length; i++)
        {
            if (tok[i] != op) continue;
            var nums = new double[count];
            bool ok = true;
            for (int k = 0; k < count && ok; k++)
                ok = double.TryParse(tok[i - count + k], NumberStyles.Float, CultureInfo.InvariantCulture, out nums[k]);
            if (ok) found.Add(nums);
        }
        return found;
    }

    private static int Count(string content, string op) =>
        content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(t => t == op);


    /// <summary>
    /// Where each image ends up on the page: the bounding box (PDF space, y up) of the unit square
    /// after every <c>cm</c> in force at its <c>Do</c>, tracking q/Q. This is what a viewer does, so
    /// it judges the placement and any turn together instead of pinning one matrix's spelling.
    /// </summary>
    private static List<(double L, double B, double R, double T)> ImageFootprints(string content)
    {
        var found = new List<(double, double, double, double)>();
        var stack = new Stack<double[]>();
        double[] ctm = { 1, 0, 0, 1, 0, 0 };
        var tok = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tok.Length; i++)
        {
            switch (tok[i])
            {
                case "q": stack.Push((double[])ctm.Clone()); break;
                case "Q": if (stack.Count > 0) ctm = stack.Pop(); break;
                case "cm" when i >= 6:
                {
                    var m = new double[6];
                    for (int k = 0; k < 6; k++) m[k] = double.Parse(tok[i - 6 + k], CultureInfo.InvariantCulture);
                    // row-vector convention: new CTM = m x CTM
                    ctm = new[]
                    {
                        m[0] * ctm[0] + m[1] * ctm[2], m[0] * ctm[1] + m[1] * ctm[3],
                        m[2] * ctm[0] + m[3] * ctm[2], m[2] * ctm[1] + m[3] * ctm[3],
                        m[4] * ctm[0] + m[5] * ctm[2] + ctm[4], m[4] * ctm[1] + m[5] * ctm[3] + ctm[5],
                    };
                    break;
                }
                case "Do":
                {
                    double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                    foreach (var (u, v) in new[] { (0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0) })
                    {
                        double x = u * ctm[0] + v * ctm[2] + ctm[4], y = u * ctm[1] + v * ctm[3] + ctm[5];
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    }
                    found.Add((minX, minY, maxX, maxY));
                    break;
                }
            }
        }
        return found;
    }

    private static bool FootIs(string content, double l, double b, double r, double t)
    {
        var f = ImageFootprints(content);
        return f.Count == 1 && Near(f[0].L, l) && Near(f[0].B, b) && Near(f[0].R, r) && Near(f[0].T, t);
    }

    private static string FootText(string content) =>
        string.Join(" | ", ImageFootprints(content).Select(f => $"{Fmt(f.L)},{Fmt(f.B)}..{Fmt(f.R)},{Fmt(f.T)}"));

    private static bool Near(double a, double b, double eps = 0.01) => Math.Abs(a - b) <= eps;

    private static bool HasRect(string content, double x, double y, double w, double h) =>
        Operands(content, "re", 4).Any(r => Near(r[0], x) && Near(r[1], y) && Near(r[2], w) && Near(r[3], h));

    private static int BlendModeCount(string path, string mode)
    {
        using var re = PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);
        int n = 0;
        foreach (var obj in re.Internals.GetAllObjects())
            if (obj is PdfDictionary d && d.Elements.GetName("/BM") == "/" + mode) n++;
        return n;
    }

    private static int FontFileCount(string path)
    {
        using var re = PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);
        int n = 0;
        foreach (var obj in re.Internals.GetAllObjects())
            if (obj is PdfDictionary d && (d.Elements.ContainsKey("/FontFile2") || d.Elements.ContainsKey("/FontFile3"))) n++;
        return n;
    }

    private static List<UglyToad.PdfPig.Content.Word> Words(string path)
    {
        using var d = UglyToad.PdfPig.PdfDocument.Open(path);
        return d.GetPage(1).GetWords().ToList();
    }

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("── annotation bake (Services/AnnotationBaker) ─────────────────");

        var dump = Environment.GetEnvironmentVariable("TDPDF_DUMP_BAKE") == "1";
        void Dump(string title, Baked b) { if (dump) Console.WriteLine($"--- {title}\n{b.Content}\n{b.Resources}"); }

        // ── Highlight: Multiply, restored afterwards ────────────────────────────────────────
        {
            var b = Bake(tmp, "hl",
                new HighlightAnnotation { Bounds = new Rect(10, 20, 100, 30), ColorR = 255, ColorG = 255, ColorB = 0, ColorA = 255 },
                new ShapeAnnotation { Kind = ShapeKind.Rectangle, Start = new Point(200, 200), End = new Point(260, 240), StrokeR = 0, StrokeG = 0, StrokeB = 255 });
            Dump("highlight", b);
            Check("highlight: the band is one filled rect, canvas y flipped into PDF space",
                  HasRect(b.Content, 10, 750, 100, 30) && b.Content.Contains("1 1 0 rg"), "");
            Check("highlight: burns with the Multiply blend mode", BlendModeCount(b.Path, "Multiply") == 1,
                  BlendModeCount(b.Path, "Multiply").ToString());
            Check("highlight: the blend mode is saved and restored around the band (q/Q balanced)",
                  Count(b.Content, "q") == Count(b.Content, "Q"), $"{Count(b.Content, "q")} q, {Count(b.Content, "Q")} Q");
            int gsAt = b.Content.IndexOf(" gs", StringComparison.Ordinal);
            int rectAt = b.Content.IndexOf("10 750 100 30 re", StringComparison.Ordinal);
            int restoreAt = b.Content.IndexOf("\nQ", rectAt, StringComparison.Ordinal);
            int nextRectAt = b.Content.IndexOf("200 560 60 40 re", StringComparison.Ordinal);
            Check("highlight: the shape drawn after it is outside the Multiply state",
                  gsAt >= 0 && gsAt < rectAt && restoreAt > rectAt && nextRectAt > restoreAt,
                  $"gs={gsAt} rect={rectAt} Q={restoreAt} next={nextRectAt}");
        }

        // ── Markup: highlight multiplies, underline/strikethrough do not ──────────────────────
        {
            var line = new Rect(100, 300, 200, 20);
            var hi = Bake(tmp, "mk-hi", new MarkupAnnotation { Style = MarkupStyle.Highlight, LineRects = { line }, Bounds = line, ColorA = 255 });
            var ul = Bake(tmp, "mk-ul", new MarkupAnnotation { Style = MarkupStyle.Underline, LineRects = { line }, Bounds = line, ColorR = 255, ColorG = 0, ColorB = 0, ColorA = 255 });
            var st = Bake(tmp, "mk-st", new MarkupAnnotation { Style = MarkupStyle.Strikethrough, LineRects = { line }, Bounds = line, ColorR = 255, ColorG = 0, ColorB = 0, ColorA = 255 });
            Dump("underline", ul); Dump("strike", st);
            Check("markup highlight: the whole line box, multiplied",
                  HasRect(hi.Content, 100, 480, 200, 20) && BlendModeCount(hi.Path, "Multiply") == 1, "");
            // Underline is a band 9% of the line tall (min 1.5) at the foot of the line box.
            double t = Math.Max(1.5, 20 * 0.09);
            Check("markup underline: a thin band at the foot of the line, no blend mode",
                  HasRect(ul.Content, 100, 800 - 320, 200, t) && BlendModeCount(ul.Path, "Multiply") == 0 && ul.Content.Contains("1 0 0 rg"),
                  string.Join(" | ", Operands(ul.Content, "re", 4).Select(r => string.Join(" ", r.Select(Fmt)))));
            Check("markup strikethrough: a thin band through the middle of the line, no blend mode",
                  HasRect(st.Content, 100, 800 - 310 - t / 2, 200, t) && BlendModeCount(st.Path, "Multiply") == 0,
                  string.Join(" | ", Operands(st.Content, "re", 4).Select(r => string.Join(" ", r.Select(Fmt)))));
            var two = Bake(tmp, "mk-two", new MarkupAnnotation
            {
                Style = MarkupStyle.Highlight, ColorA = 255,
                LineRects = { new Rect(10, 10, 50, 10), new Rect(10, 30, 70, 10) }, Bounds = new Rect(10, 10, 70, 30)
            });
            Check("markup: one band per covered line", Count(two.Content, "re") == 2 && HasRect(two.Content, 10, 780, 50, 10) && HasRect(two.Content, 10, 760, 70, 10), "");
        }

        // ── Shapes ─────────────────────────────────────────────────────────────────────────────
        {
            var rect = Bake(tmp, "shape-rect", new ShapeAnnotation
            {
                Kind = ShapeKind.Rectangle, Start = new Point(100, 100), End = new Point(180, 140), StrokeWidth = 3,
                StrokeR = 255, StrokeG = 0, StrokeB = 0, StrokeA = 255, HasFill = true, FillR = 0, FillG = 255, FillB = 0, FillA = 255
            });
            Dump("rect", rect);
            Check("shape rectangle: fill colour, stroke colour and stroke width are written",
                  rect.Content.Contains("0 1 0 rg") && rect.Content.Contains("1 0 0 RG") && rect.Content.Contains("3 w"), "");
            Check("shape rectangle: filled and stroked at the same box", Operands(rect.Content, "re", 4).Count == 2
                  && HasRect(rect.Content, 100, 660, 80, 40), "");

            var plain = Bake(tmp, "shape-rect-nofill", new ShapeAnnotation { Kind = ShapeKind.Rectangle, Start = new Point(100, 100), End = new Point(180, 140), StrokeA = 255 });
            Check("shape rectangle without a fill: only the outline", Operands(plain.Content, "re", 4).Count == 1 && !plain.Content.Contains(" rg"), "");

            var ell = Bake(tmp, "shape-ell", new ShapeAnnotation { Kind = ShapeKind.Ellipse, Start = new Point(100, 100), End = new Point(180, 140), StrokeA = 255 });
            Check("shape ellipse: drawn as curves", Count(ell.Content, "c") >= 4 && Count(ell.Content, "re") == 0, $"{Count(ell.Content, "c")} curves");

            var poly = Bake(tmp, "shape-poly", new ShapeAnnotation
            {
                Kind = ShapeKind.Polygon, StrokeA = 255, HasFill = true,
                Points = { new Point(100, 100), new Point(200, 100), new Point(150, 180) }
            });
            Dump("poly", poly);
            var moves = Operands(poly.Content, "m", 2);
            Check("shape polygon: starts at the first vertex, flipped into PDF space",
                  moves.Count >= 1 && Near(moves[0][0], 100) && Near(moves[0][1], 700), "");
            Check("shape polygon: three vertices make two line segments and a close, filled then stroked",
                  Operands(poly.Content, "l", 2).Count >= 2 && Count(poly.Content, "h") >= 1, "");

            var tiny = Bake(tmp, "shape-poly2", new ShapeAnnotation { Kind = ShapeKind.Polygon, Points = { new Point(1, 1), new Point(5, 5) } });
            Check("shape polygon with fewer than three points draws nothing", Count(tiny.Content, "m") == 0 && Count(tiny.Content, "l") == 0, "");

            var ln = Bake(tmp, "shape-line", new ShapeAnnotation { Kind = ShapeKind.Line, Start = new Point(50, 60), End = new Point(250, 90), StrokeA = 255 });
            var lm = Operands(ln.Content, "m", 2); var ll = Operands(ln.Content, "l", 2);
            Check("shape line: from Start to End (direction kept)",
                  lm.Count == 1 && ll.Count == 1 && Near(lm[0][0], 50) && Near(lm[0][1], 740) && Near(ll[0][0], 250) && Near(ll[0][1], 710), "");
        }

        // ── Ink ─────────────────────────────────────────────────────────────────────────────────
        {
            var ink = Bake(tmp, "ink", new InkAnnotation
            {
                Points = { new Point(10, 10), new Point(60, 10), new Point(60, 50) }, StrokeWidth = 4,
                ColorR = 0, ColorG = 0, ColorB = 255, ColorA = 255
            });
            Dump("ink", ink);
            Check("ink: one segment per pair of points, in the pen colour and width",
                  Operands(ink.Content, "l", 2).Count == 2 && ink.Content.Contains("0 0 1 RG") && ink.Content.Contains("4 w"), "");
            var one = Bake(tmp, "ink-one", new InkAnnotation { Points = { new Point(30, 30) } });
            Check("ink: an unmoved pen is a filled dot, not a stroked line",
                  Count(one.Content, "c") >= 4 && Count(one.Content, "l") == 0, $"{Count(one.Content, "c")} curves");
            var longEnough = Bake(tmp, "ink-short", new InkAnnotation { Points = { new Point(30, 30), new Point(30, 60) } });
            Check("ink: a stroke longer than a dot stays a stroke", Count(longEnough.Content, "l") == 1, "");
        }

        // ── Text ────────────────────────────────────────────────────────────────────────────────
        {
            var t = Bake(tmp, "text",
                new TextAnnotation { Position = new Point(50, 100), Content = "Hello", FontSize = 20, ColorR = 255, ColorG = 0, ColorB = 0, ColorA = 255 });
            Dump("text", t);
            Check("text: drawn once in the annotation colour", Count(t.Content, "Tj") == 1 && t.Content.Contains("1 0 0 rg"), "");
            var words = Words(t.Path);
            var hello = words.FirstOrDefault(w => w.Text == "Hello");
            Check("text: PdfPig reads the baked word back", hello is not null, string.Join(",", words.Select(w => w.Text)));
            if (hello is not null)
            {
                // Baseline is canvas y + font size (100 + 20), flipped: 680 up from the page foot.
                Check("text: at the annotation's x, on the baseline one font size below its top",
                      Near(hello.BoundingBox.Left, 50, 1.5) && hello.BoundingBox.Bottom < 681 && hello.BoundingBox.Top > 680,
                      $"L={hello.BoundingBox.Left:0.##} B={hello.BoundingBox.Bottom:0.##} T={hello.BoundingBox.Top:0.##}");
            }

            // The page scale: a frame half the page size doubles every coordinate.
            var scaled = Bake(tmp, "text-scaled", PageW / 2, PageH / 2,
                new[] { (PageAnnotation)new TextAnnotation { Position = new Point(25, 50), Content = "Scaled", FontSize = 10 } });
            var sw = Words(scaled.Path).FirstOrDefault(w => w.Text == "Scaled");
            Check("text: a half-size render frame scales position and size by two",
                  sw is not null && Near(sw.BoundingBox.Left, 50, 1.5) && sw.BoundingBox.Bottom < 681 && sw.BoundingBox.Top > 680,
                  sw is null ? "missing" : $"L={sw.BoundingBox.Left:0.##}");

            // Wrapped box: fill first, then one Tj per wrapped line, 1.2 em apart.
            var box = Bake(tmp, "text-box", new TextAnnotation
            {
                Position = new Point(40, 300), Content = "alpha beta gamma", FontSize = 10, Width = 120, Height = 80,
                HasFill = true, FillR = 255, FillG = 255, FillB = 0, FillA = 255
            });
            Dump("box", box);
            Check("text box: the fill is the box itself, behind the text",
                  HasRect(box.Content, 40, 800 - 300 - 80, 120, 80)
                  && box.Content.IndexOf("1 1 0 rg", StringComparison.Ordinal) < box.Content.IndexOf("Tj", StringComparison.Ordinal), "");
            Check("text box: one Tj per wrapped line", Count(box.Content, "Tj") == 3, $"{Count(box.Content, "Tj")}");
            var bw = Words(box.Path);
            var a = bw.FirstOrDefault(w => w.Text == "alpha"); var g = bw.FirstOrDefault(w => w.Text == "gamma");
            Check("text box: lines step down by 1.2 em from a 2px inset",
                  a is not null && g is not null && Near(a.BoundingBox.Left, 42, 1.5)
                  && Near(a.BoundingBox.Bottom - g.BoundingBox.Bottom, 24, 1.5),
                  a is null || g is null ? "missing" : $"{a.BoundingBox.Bottom:0.##} -> {g.BoundingBox.Bottom:0.##}");

            // Auto-size with a fill: the fill is the measured box.
            var auto = Bake(tmp, "text-auto-fill", new TextAnnotation { Position = new Point(40, 400), Content = "Auto", HasFill = true, FontSize = 12 });
            Check("auto-size text with a fill: the fill is the measured box (100x28)", HasRect(auto.Content, 40, 800 - 400 - 28, 100, 28), "");

            // Underline: one extra thin filled rect under each line.
            var plainT = Bake(tmp, "text-plain", new TextAnnotation { Position = new Point(50, 100), Content = "Under", FontSize = 20 });
            var ulT = Bake(tmp, "text-ul", new TextAnnotation { Position = new Point(50, 100), Content = "Under", FontSize = 20, Underline = true });
            Check("text underline: one thin filled rect under the baseline, none without it",
                  Count(plainT.Content, "re") == 0 && Count(ulT.Content, "re") == 1, $"{Count(plainT.Content, "re")} -> {Count(ulT.Content, "re")}");
            var ur = Operands(ulT.Content, "re", 4);
            Check("text underline: sits just below the baseline and is 6% of the em tall",
                  ur.Count == 1 && ur[0][1] < 680 && ur[0][1] > 670 && Near(ur[0][3], 20 * 0.06) && ur[0][2] > 20,
                  ur.Count == 1 ? string.Join(" ", ur[0].Select(Fmt)) : "");

            // Letter spacing: one Tj per character instead of one per line.
            var tight = Bake(tmp, "text-tracked", new TextAnnotation { Position = new Point(50, 100), Content = "SPACE", FontSize = 20, LetterSpacing = 6 });
            Check("letter spacing: drawn a character at a time (5 Tj) instead of as one string",
                  Count(tight.Content, "Tj") == 5 && Count(t.Content, "Tj") == 1, $"{Count(tight.Content, "Tj")}");
            var tw = Words(tight.Path);
            Check("letter spacing: the spaced glyphs sit wider than the same word unspaced",
                  tw.Count >= 1, string.Join(",", tw.Select(w => w.Text)));

            // Empty and degenerate text.
            var empty = Bake(tmp, "text-empty", new TextAnnotation { Position = new Point(50, 100), Content = "", FontSize = 20 });
            Check("empty text: nothing is drawn", Count(empty.Content, "Tj") == 0, "");
            var zero = Bake(tmp, "text-zero", new TextAnnotation { Position = new Point(50, 100), Content = "Gone", FontSize = 0 });
            Check("a zero font size skips that one annotation without throwing", Count(zero.Content, "Tj") == 0, "");
        }

        // ── Rotation: text boxes, signatures and pictures ───────────────────────────────────────
        {
            // The turn is a cm about the centre of the UNROTATED box (100x28 for the fake layout).
            // Text origin before the turn is (50, 220 - 0) canvas -> PDF (50, 580); after turning
            // clockwise 90 degrees about (100, 586) it must land at (94, 636).
            foreach (var (rot, ea, eb, ec, ed) in new[] { (90, 0, -1, 1, 0), (180, -1, 0, 0, -1), (270, 0, 1, -1, 0) })
            {
                var r = Bake(tmp, $"text-rot{rot}",
                    new TextAnnotation { Position = new Point(50, 200), Content = "Turned", FontSize = 20, Rotation = rot });
                Dump($"rot{rot}", r);
                var cm = Operands(r.Content, "cm", 6);
                var td = Operands(r.Content, "Td", 2);
                bool matrix = cm.Count >= 1 && Near(cm[^1][0], ea) && Near(cm[^1][1], eb) && Near(cm[^1][2], ec) && Near(cm[^1][3], ed);
                Check($"text rotated {rot}: the turn is the expected cm matrix", matrix,
                      cm.Count > 0 ? string.Join(" ", cm[^1].Select(Fmt)) : "no cm");
                if (matrix && td.Count >= 1)
                {
                    var m = cm[^1];
                    double px = m[0] * td[0][0] + m[2] * td[0][1] + m[4];
                    double py = m[1] * td[0][0] + m[3] * td[0][1] + m[5];
                    // Origin relative to the centre (100, 586), turned clockwise by rot.
                    double rx = 50 - 100, ry = 580 - 586;
                    (double ox, double oy) = rot switch { 90 => (ry, -rx), 180 => (-rx, -ry), _ => (-ry, rx) };
                    Check($"text rotated {rot}: the text origin lands where a turn about the box centre puts it",
                          Near(px, 100 + ox, 0.5) && Near(py, 586 + oy, 0.5), $"({Fmt(px)}, {Fmt(py)}) want ({Fmt(100 + ox)}, {Fmt(586 + oy)})");
                }
            }
            var upright = Bake(tmp, "text-rot0", new TextAnnotation { Position = new Point(50, 200), Content = "Straight", FontSize = 20, Rotation = 0 });
            Check("text not rotated: no turn matrix", Operands(upright.Content, "cm", 6).Count == 0, "");

            // Rotation 360 normalises to 0; negatives to the equivalent quarter turn.
            var wrap360 = Bake(tmp, "text-rot360", new TextAnnotation { Position = new Point(50, 200), Content = "Full", FontSize = 20, Rotation = 360 });
            Check("text rotated 360 is not turned at all", Operands(wrap360.Content, "cm", 6).Count == 0, "");

            // Signature (image) and picture: the box is SourceWidth*Scale x SourceHeight*Scale.
            var sig = new SignatureAnnotation { Position = new Point(100, 100), Scale = 0.5, SourceWidth = 200, SourceHeight = 80, ImageData = Png1x1 };
            var sigB = Bake(tmp, "sig-img", sig);
            Dump("sig", sigB);
            Check("signature image: drawn once, in its scaled box (SourceWidth*Scale x SourceHeight*Scale)",
                  Streams.Images(sigB.Path).Count >= 1 && FootIs(sigB.Content, 100, 800 - 100 - 40, 200, 800 - 100), FootText(sigB.Content));

            sig.Rotation = 90;
            var sigR = Bake(tmp, "sig-img-rot", sig);
            // The 100x40 box is centred on (150, 120) canvas = (150, 680) PDF; a quarter turn about
            // that centre is a 40x100 footprint around the same point.
            Check("signature image rotated 90: the same centre, width and height exchanged",
                  FootIs(sigR.Content, 130, 630, 170, 730) && Count(sigR.Content, "Do") == 1, FootText(sigR.Content));
            Check("signature image rotated 90: the turn is a clockwise quarter turn",
                  Operands(sigR.Content, "cm", 6).Any(c => Near(c[0], 0) && Near(c[1], -1) && Near(c[2], 1) && Near(c[3], 0)), "");

            var strokes = new SignatureAnnotation
            {
                Position = new Point(100, 100), Scale = 2, SourceWidth = 200, SourceHeight = 80,
                Strokes = { new List<Point> { new Point(0, 0), new Point(10, 0), new Point(10, 10) }, new List<Point> { new Point(50, 50) } }
            };
            var strokeB = Bake(tmp, "sig-strokes", strokes);
            Dump("sig-strokes", strokeB);
            Check("signature strokes: two segments from the first stroke, width 2 x scale, none from a lone point",
                  strokeB.Content.Contains("4 w") && Operands(strokeB.Content, "l", 2).Count == 2, "");
            var sm = Operands(strokeB.Content, "m", 2);
            Check("signature strokes: offset by the position and scaled", sm.Count >= 1 && Near(sm[0][0], 100) && Near(sm[0][1], 700), "");

            var pic = new ImageAnnotation { Position = new Point(50, 300), Scale = 1, SourceWidth = 120, SourceHeight = 60, ImageData = Png1x1, Rotation = 180 };
            var picB = Bake(tmp, "pic-rot180", pic);
            Dump("pic", picB);
            // Box 120x60 at (50,300): centre (110,330) canvas = (110,470) PDF. A half turn keeps the
            // footprint and flips the matrix.
            Check("picture rotated 180: the same footprint, turned half way round",
                  FootIs(picB.Content, 50, 440, 170, 500)
                  && Operands(picB.Content, "cm", 6).Any(c => Near(c[0], -1) && Near(c[1], 0) && Near(c[2], 0) && Near(c[3], -1)),
                  FootText(picB.Content));
            Check("picture: drawn once", Count(picB.Content, "Do") == 1 && Streams.Images(picB.Path).Count >= 1, "");

            var broken = Bake(tmp, "pic-broken", new ImageAnnotation { Position = new Point(10, 10), ImageData = "not base64 at all !!" },
                              new SignatureAnnotation { Position = new Point(10, 10), ImageData = "%%%" },
                              new TextAnnotation { Position = new Point(50, 100), Content = "After", FontSize = 12 });
            Check("an undecodable picture or signature is skipped and the rest of the page still bakes",
                  Count(broken.Content, "Do") == 0 && Count(broken.Content, "Tj") == 1, "");
        }

        // ── In-place edits ──────────────────────────────────────────────────────────────────────
        {
            var te = Bake(tmp, "textedit", new TextEditAnnotation
            {
                OriginalBounds = new Rect(100, 200, 80, 16), Position = new Point(100, 200), NewContent = "Edited",
                FontSize = 12, ColorR = 0, ColorG = 0, ColorB = 255, ColorA = 255
            });
            Dump("textedit", te);
            Check("text edit: the original line is whited out with a 1px margin", HasRect(te.Content, 99, 800 - 200 - 16 - 1, 82, 18) && te.Content.Contains("1 1 1 rg"),
                  string.Join(" | ", Operands(te.Content, "re", 4).Select(r => string.Join(" ", r.Select(Fmt)))));
            var ew = Words(te.Path).FirstOrDefault(w => w.Text == "Edited");
            Check("text edit: the replacement is drawn on top, in its colour", ew is not null && te.Content.Contains("0 0 1 rg")
                  && te.Content.IndexOf("1 1 1 rg", StringComparison.Ordinal) < te.Content.IndexOf("Tj", StringComparison.Ordinal), "");
            Check("text edit: at the edit's position", ew is not null && Near(ew.BoundingBox.Left, 100, 1.5) && ew.BoundingBox.Bottom < 589 && ew.BoundingBox.Top > 588,
                  ew is null ? "missing" : $"L={ew.BoundingBox.Left:0.##} B={ew.BoundingBox.Bottom:0.##} T={ew.BoundingBox.Top:0.##}");

            var noFont = Bake(tmp, "textedit-nofont", new TextEditAnnotation { OriginalBounds = new Rect(100, 200, 80, 16), NewContent = "X", FontSize = 0 });
            Check("text edit with an unusable font size skips the whole edit, so the original is not whited out for nothing",
                  Count(noFont.Content, "re") == 0 && Count(noFont.Content, "Tj") == 0, "");

            var ie = Bake(tmp, "imageedit", new ImageEditAnnotation
            {
                OriginalBounds = new Rect(100, 100, 60, 40), TargetBounds = new Rect(100, 100, 60, 40), OriginalImageData = Png1x1
            });
            Dump("imageedit", ie);
            Check("image edit: whited out with a 2px margin, then the image drawn over its target box",
                  HasRect(ie.Content, 98, 800 - 100 - 40 - 2, 64, 44) && FootIs(ie.Content, 100, 660, 160, 700)
                  && Streams.Images(ie.Path).Count >= 1, FootText(ie.Content));
            var del = Bake(tmp, "imageedit-del", new ImageEditAnnotation
            {
                OriginalBounds = new Rect(100, 100, 60, 40), TargetBounds = new Rect(100, 100, 60, 40), OriginalImageData = Png1x1, IsDeleted = true
            });
            Check("image edit, deleted: only the whiteout", HasRect(del.Content, 98, 800 - 100 - 40 - 2, 64, 44) && Count(del.Content, "Do") == 0, "");
            var rotIe = Bake(tmp, "imageedit-rot", new ImageEditAnnotation
            {
                OriginalBounds = new Rect(100, 100, 60, 40), TargetBounds = new Rect(100, 100, 60, 40), OriginalImageData = Png1x1, Rotation = 90
            });
            // Unlike a signature or picture, a replaced image keeps its target box as the footprint:
            // the quarter turn is applied to an image drawn with width and height exchanged, so the
            // turned picture fills the 60x40 box (centre (130,120) canvas = (130,680) PDF) exactly.
            Check("image edit rotated 90: the turned image still fills the target box",
                  FootIs(rotIe.Content, 100, 660, 160, 700), FootText(rotIe.Content));
            var rc = Operands(rotIe.Content, "cm", 6);
            Check("image edit rotated 90: a clockwise quarter turn, the image drawn 40 wide by 60 tall inside it",
                  rc.Any(c => Near(c[0], 0) && Near(c[1], -1) && Near(c[2], 1) && Near(c[3], 0))
                  && rc.Any(c => Near(c[0], 40) && Near(c[3], 60)), "");
            var missing = Bake(tmp, "imageedit-missing", new ImageEditAnnotation
            {
                OriginalBounds = new Rect(100, 100, 60, 40), TargetBounds = new Rect(100, 100, 60, 40), ReplacementImagePath = Path.Combine(tmp, "no-such.png")
            });
            Check("image edit with a missing replacement file: whiteout only, no throw", Count(missing.Content, "Do") == 0 && Count(missing.Content, "re") == 1, "");
        }

        // ── Paint order: edits first, whatever the list order ──────────────────────────────────
        {
            var b = Bake(tmp, "order",
                new ShapeAnnotation { Kind = ShapeKind.Rectangle, Start = new Point(10, 10), End = new Point(30, 30), HasFill = true, FillR = 0, FillG = 255, FillB = 0, FillA = 255, StrokeA = 255 },
                new TextEditAnnotation { OriginalBounds = new Rect(100, 200, 80, 16), Position = new Point(100, 200), NewContent = "Over", FontSize = 12 });
            int white = b.Content.IndexOf("1 1 1 rg", StringComparison.Ordinal), green = b.Content.IndexOf("0 1 0 rg", StringComparison.Ordinal);
            Check("paint order: a whiteout edit is drawn before a shape listed ahead of it", white >= 0 && green > white, $"white={white} green={green}");
            var order = AnnotationBaker.InPaintOrder(new PageAnnotation[]
            {
                new InkAnnotation(), new TextEditAnnotation(), new ShapeAnnotation(), new ImageEditAnnotation()
            }).Select(x => x.GetType().Name).ToList();
            Check("InPaintOrder: edits first, each group in its own order",
                  string.Join(",", order) == "TextEditAnnotation,ImageEditAnnotation,InkAnnotation,ShapeAnnotation", string.Join(",", order));
        }

        // ── Fonts ───────────────────────────────────────────────────────────────────────────────
        {
            var two = Bake(tmp, "fonts-two",
                new TextAnnotation { Position = new Point(50, 100), Content = "One", FontSize = 14 },
                new TextAnnotation { Position = new Point(50, 200), Content = "Two", FontSize = 14 },
                new TextAnnotation { Position = new Point(50, 300), Content = "Three", FontSize = 14 });
            Check("fonts: three annotations in one face embed that face once", FontFileCount(two.Path) == 1, $"{FontFileCount(two.Path)} font files");
        }

        // ── Page selection and the link-border strip ───────────────────────────────────────────
        {
            var skipped = Bake(tmp, "frame-zero", 0, 0, new[] { (PageAnnotation)new HighlightAnnotation { Bounds = new Rect(1, 1, 5, 5) } });
            Check("a page with no usable render frame is skipped, not stretched", Count(skipped.Content, "re") == 0, "");

            var doc = new PdfDocument();
            doc.AddPage();
            AnnotationBaker.Bake(doc, new Dictionary<int, List<PageAnnotation>>
            {
                [5] = new() { new HighlightAnnotation { Bounds = new Rect(1, 1, 5, 5) } },
                [-1] = new() { new HighlightAnnotation { Bounds = new Rect(1, 1, 5, 5) } },
                [0] = new()
            }, _ => (600, 800), Layout);
            Check("annotations keyed beyond the document, below zero, or an empty list are ignored",
                  doc.Pages[0].Contents.Elements.Count == 0, "");

            var linked = Bake(tmp, "link", PageW, PageH, new List<PageAnnotation>(), page =>
            {
                var link = new PdfDictionary(page.Owner);
                link.Elements["/Type"] = new PdfName("/Annot");
                link.Elements["/Subtype"] = new PdfName("/Link");
                link.Elements["/C"] = new PdfArray(page.Owner, new PdfInteger(1), new PdfInteger(0), new PdfInteger(0));
                link.Elements["/BS"] = new PdfDictionary(page.Owner);
                var arr = new PdfArray(page.Owner);
                arr.Elements.Add(link);
                page.Elements["/Annots"] = arr;
            });
            using var re = PdfSharpCore.Pdf.IO.PdfReader.Open(linked.Path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);
            var annotsArr = re.Pages[0].Elements.GetArray("/Annots");
            var first = annotsArr?.Elements[0];
            var ld = first as PdfDictionary ?? PdfItems.Deref(first) as PdfDictionary;
            var border = ld?.Elements.GetArray("/Border");
            Check("link annotations lose their colour and border style and get a zero /Border",
                  ld is not null && !ld.Elements.ContainsKey("/C") && !ld.Elements.ContainsKey("/BS")
                  && border is not null && border.Elements.Count == 3 && border.Elements.Items.All(i => i is PdfInteger { Value: 0 }), "");
        }
    }
}
