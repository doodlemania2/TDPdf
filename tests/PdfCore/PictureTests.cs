using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using TDPdf.Services;
using PageBox = TDPdf.Services.PdfPageGeometry.PageBox;
using PdfRect = TDPdf.Services.PdfiumInterop.PdfRect;

/// <summary>
/// Selecting, moving, resizing, deleting, replacing and lifting pictures in the file itself (#244).
/// </summary>
/// <remarks>
/// Three layers, cheapest first. The matrix maths and the pixel maths are pure and are pinned on cropped
/// and turned pages, where a mapping that is only right at /Rotate 0 with the crop at the origin would
/// look fine and be wrong everywhere else. The gate is one pure function. And because PDFium runs here,
/// the page-object edits themselves are done against a real file and READ BACK: a picture that did not
/// move, a shared image that changed under its other use, or a mirrored picture that stood up straight
/// would all pass a test that only looked at what was asked for.
/// </remarks>
internal static class PictureTests
{
    private const double Eps = 1e-6;

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nPictures: placement maths, gate, pixel maths, and edits in a real file");
        PlacementMaths(Check);
        ReplacementMaths(Check);
        GateDecisions(Check);
        JpegHeaders(Check);
        ColourKey(Check);
        Orientation(Check);
        EditsInAFile(Check, tmp);
        RotatedPageEdit(Check, tmp);
        HazardPageIsRefused(Check, tmp);
    }

    // ── placement ────────────────────────────────────────────────────────────────────────────

    private static void PlacementMaths(Action<string, bool, string> Check)
    {
        var upright = new Affine(60, 0, 0, 80, 150, 300);
        var oldB = upright.Bounds();
        Check("pictures: an upright matrix's bounds are its box",
              oldB == new PdfRect(150, 300, 210, 380), $"{oldB}");

        var moved = PicturePlacement.FitBounds(upright, oldB, PicturePlacement.Translate(oldB, 25, -10));
        Check("pictures: a move changes e and f and nothing else",
              moved == new Affine(60, 0, 0, 80, 175, 290), $"{moved}");

        var grown = PicturePlacement.FitBounds(upright, oldB, new PdfRect(100, 280, 220, 440));
        Check("pictures: a resize scales the matrix to the new box",
              Math.Abs(grown.A - 120) < Eps && Math.Abs(grown.D - 160) < Eps && grown.Bounds() == new PdfRect(100, 280, 220, 440),
              $"{grown}");

        // A picture drawn mirrored (a < 0) must still be mirrored after a resize, with the bounds right.
        var mirrored = new Affine(-60, 0, 0, 80, 210, 300);
        Check("pictures: the fixture matrix is mirrored", mirrored.IsMirrored && mirrored.Bounds() == oldB, $"{mirrored.Bounds()}");
        var mGrown = PicturePlacement.FitBounds(mirrored, mirrored.Bounds(), new PdfRect(100, 280, 220, 440));
        Check("pictures: a resize keeps a mirrored picture mirrored",
              mGrown.IsMirrored && mGrown.A < 0 && mGrown.D > 0 && mGrown.Bounds() == new PdfRect(100, 280, 220, 440), $"{mGrown}");

        // Turned a quarter (a = d = 0): the matrix has to follow the box the same way.
        var turned = new Affine(0, 60, -80, 0, 230, 300);
        var tMoved = PicturePlacement.FitBounds(turned, turned.Bounds(), PicturePlacement.Translate(turned.Bounds(), 40, 15));
        Check("pictures: a turned picture moves and stays turned",
              tMoved.A == 0 && tMoved.D == 0 && Math.Abs(tMoved.B - 60) < Eps && Math.Abs(tMoved.C + 80) < Eps
              && Math.Abs(tMoved.E - 270) < Eps && Math.Abs(tMoved.F - 315) < Eps, $"{tMoved}");

        // The canvas side: every quarter turn, on a crop that does not start at the origin.
        var box = new PageBox(100, 200, 400, 500);
        var picture = new PdfRect(150, 300, 250, 420);
        foreach (int rot in new[] { 0, 90, 180, 270 })
        {
            double rw = rot is 90 or 270 ? 1000 : 800, rh = rot is 90 or 270 ? 800 : 1000;
            string tag = $"/Rotate {rot}, crop at (100,200)";

            var c0 = PicturePlacement.ToCanvas(box, rot, rw, rh, picture);
            var back = PicturePlacement.ToPdf(box, rot, rw, rh, c0);
            Check($"pictures: canvas box round-trips ({tag})",
                  Near(back, picture, 1e-6), $"{back}");

            var mv = PicturePlacement.MovedBounds(box, rot, rw, rh, picture, 30, -20);
            var mvCanvas = PicturePlacement.ToCanvas(box, rot, rw, rh, mv);
            Check($"pictures: a canvas move of (30, -20) lands there ({tag})",
                  Math.Abs(mvCanvas.X - (c0.X + 30)) < 1e-6 && Math.Abs(mvCanvas.Y - (c0.Y - 20)) < 1e-6, $"{mvCanvas} from {c0}");
            Check($"pictures: a move keeps the size exactly ({tag})",
                  Math.Abs((mv.Right - mv.Left) - 100) < 1e-9 && Math.Abs((mv.Top - mv.Bottom) - 120) < 1e-9,
                  $"{mv.Right - mv.Left} x {mv.Top - mv.Bottom}");

            // Resize the bottom-right corner by (40, 10), aspect kept: the top-left stays put on the canvas.
            var rs = PicturePlacement.ResizeCanvasBox(c0, PicturePlacement.Corner.BottomRight, 40, 10, keepAspect: true);
            var rsPdf = PicturePlacement.ToPdf(box, rot, rw, rh, rs);
            var rsCanvas = PicturePlacement.ToCanvas(box, rot, rw, rh, rsPdf);
            double aspectBefore = c0.W / c0.H, aspectAfter = rs.W / rs.H;
            Check($"pictures: an aspect-kept resize keeps the proportions and the opposite corner ({tag})",
                  Math.Abs(aspectBefore - aspectAfter) < 1e-9 && Math.Abs(rs.X - c0.X) < 1e-9 && Math.Abs(rs.Y - c0.Y) < 1e-9
                  && Math.Abs(rsCanvas.W - rs.W) < 1e-6 && Math.Abs(rsCanvas.H - rs.H) < 1e-6, $"{rs}");

            // And the matrix that results fills exactly that page box, mirrored or not.
            var m = new Affine(picture.Right - picture.Left, 0, 0, picture.Top - picture.Bottom, picture.Left, picture.Bottom);
            var fit = PicturePlacement.FitBounds(m, picture, rsPdf);
            Check($"pictures: the resized matrix fills the dragged box ({tag})", Near(fit.Bounds(), rsPdf, 1e-6), $"{fit.Bounds()}");
        }

        var start = (X: 100.0, Y: 100.0, W: 200.0, H: 100.0);
        var free = PicturePlacement.ResizeCanvasBox(start, PicturePlacement.Corner.BottomRight, 100, 0, keepAspect: false);
        Check("pictures: Shift frees the proportions", free == (100, 100, 300, 100), $"{free}");
        var tl = PicturePlacement.ResizeCanvasBox(start, PicturePlacement.Corner.TopLeft, -40, -20, keepAspect: true);
        Check("pictures: the top-left corner holds the bottom-right in place",
              Math.Abs(tl.X + tl.W - 300) < 1e-9 && Math.Abs(tl.Y + tl.H - 200) < 1e-9, $"{tl}");
        var tiny = PicturePlacement.ResizeCanvasBox(start, PicturePlacement.Corner.BottomRight, -5000, -5000, keepAspect: true);
        Check("pictures: a drag past the opposite corner stops at the minimum size",
              tiny.W >= PicturePlacement.MinCanvasSize - 1e-9 && tiny.H >= PicturePlacement.MinCanvasSize - 1e-9, $"{tiny}");
    }

    private static void ReplacementMaths(Action<string, bool, string> Check)
    {
        var old = new Affine(60, 0, 0, 80, 150, 300);   // 60 wide, 80 tall, hanging from y = 380
        var r = PicturePlacement.ReplacementMatrix(old, 40, 20);
        Check("pictures: a replacement keeps the old width", Math.Abs(r.A - 60) < Eps && Math.Abs(r.B) < Eps, $"{r}");
        Check("pictures: a replacement takes the new picture's proportions", Math.Abs(r.D - 30) < Eps, $"{r}");
        Check("pictures: a replacement keeps the old top edge", Math.Abs(r.F + r.D - 380) < Eps && Math.Abs(r.E - 150) < Eps, $"{r}");

        var flipped = new Affine(-60, 0, 0, -80, 210, 380);
        var rf = PicturePlacement.ReplacementMatrix(flipped, 30, 30);
        Check("pictures: a replacement of a flipped picture stays flipped",
              rf.A < 0 && rf.D < 0, $"{rf}");
        Check("pictures: a degenerate replacement size leaves the matrix alone",
              PicturePlacement.ReplacementMatrix(old, 0, 10) == old, "");
    }

    // ── gate ─────────────────────────────────────────────────────────────────────────────────

    private static void GateDecisions(Action<string, bool, string> Check)
    {
        var ok = PictureGate.Decide(true, ContentHazard.None, false);
        Check("pictures gate: a plain page is allowed", ok.Allowed && ok.Reason is null && !ok.OfferOverlay, "");

        foreach (var (hazard, word) in new[]
        {
            (ContentHazard.NonDeviceColor, "non-RGB colour"), (ContentHazard.Shading, "gradient"),
            (ContentHazard.SoftMask, "soft mask"), (ContentHazard.InlineImage, "inline image"),
            (ContentHazard.Unparseable, "cannot read"),
        })
        {
            var d = PictureGate.Decide(true, hazard, false);
            Check($"pictures gate: {hazard} is refused, says why and offers the overlay",
                  !d.Allowed && d.OfferOverlay && d.Reason is { } r && r.Contains(word, StringComparison.OrdinalIgnoreCase), d.Reason ?? "");
        }

        var form = PictureGate.Decide(true, ContentHazard.None, insideForm: true);
        Check("pictures gate: a picture inside a form object is refused", !form.Allowed && form.OfferOverlay, form.Reason ?? "");
        var engine = PictureGate.Decide(false, ContentHazard.None, false);
        Check("pictures gate: an engine that cannot edit is refused", !engine.Allowed && engine.OfferOverlay, engine.Reason ?? "");
    }

    // ── JPEG ─────────────────────────────────────────────────────────────────────────────────

    private static byte[] Jpeg(int components) => new byte[]
    {
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,   // APP0
        0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x10, 0x00, 0x10, (byte)components, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        0xFF, 0xD9,
    };

    private static void JpegHeaders(Action<string, bool, string> Check)
    {
        Check("pictures: a colour JPEG reports three components", JpegProbe.Components(Jpeg(3)) == 3, "");
        Check("pictures: a grey JPEG reports one", JpegProbe.Components(Jpeg(1)) == 1, "");
        Check("pictures: a colour or grey JPEG can leave as it is", JpegProbe.IsPlainColour(Jpeg(3)) && JpegProbe.IsPlainColour(Jpeg(1)), "");
        Check("pictures: a CMYK JPEG cannot (viewers show it wrong)", !JpegProbe.IsPlainColour(Jpeg(4)), "");
        Check("pictures: bytes that are not a JPEG are not read", JpegProbe.Components(new byte[] { 1, 2, 3, 4, 5 }) is null, "");
        Check("pictures: a truncated JPEG does not throw", JpegProbe.Components(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00 }) is null, "");
    }

    // ── colour key ───────────────────────────────────────────────────────────────────────────

    private static byte[] Image(int w, int h, Func<int, int, (byte B, byte G, byte R, byte A)> px)
    {
        var o = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (b, g, r, a) = px(x, y);
                int i = (y * w + x) * 4;
                o[i] = b; o[i + 1] = g; o[i + 2] = r; o[i + 3] = a;
            }
        return o;
    }

    private static void ColourKey(Action<string, bool, string> Check)
    {
        // 7 x 7: white paper, a black ring (x/y 2..4 except the middle), and a white pixel in the ring's eye.
        const int N = 7;
        bool Ring(int x, int y) => x is >= 2 and <= 4 && y is >= 2 and <= 4 && !(x == 3 && y == 3);
        var img = Image(N, N, (x, y) => Ring(x, y) ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255));
        byte A(byte[] b, int x, int y) => b[(y * N + x) * 4 + 3];

        var white = ColorKeyMask.ColourAt(img, N, N, 0, 0);
        Check("colour key: the colour under a pixel is read", white == (255, 255, 255), $"{white}");
        Check("colour key: off the picture there is no colour", ColorKeyMask.ColourAt(img, N, N, 9, 0) is null, "");

        var everywhere = ColorKeyMask.Apply(img, N, N, (255, 255, 255), 10, 0, keepArea: false, contiguousFrom: null)!;
        Check("colour key: the key colour goes transparent everywhere (paper and the ring's eye)",
              A(everywhere, 0, 0) == 0 && A(everywhere, 3, 3) == 0 && A(everywhere, 2, 2) == 255, "");

        var contiguous = ColorKeyMask.Apply(img, N, N, (255, 255, 255), 10, 0, keepArea: false, contiguousFrom: (0, 0))!;
        Check("colour key: a connected area leaves the same colour enclosed elsewhere alone",
              A(contiguous, 0, 0) == 0 && A(contiguous, 6, 6) == 0 && A(contiguous, 3, 3) == 255, $"eye alpha {A(contiguous, 3, 3)}");

        var keep = ColorKeyMask.Apply(img, N, N, (0, 0, 0), 10, 0, keepArea: true, contiguousFrom: (2, 2))!;
        Check("colour key: keep-only-this-area clears everything else",
              A(keep, 2, 2) == 255 && A(keep, 0, 0) == 0 && A(keep, 3, 3) == 0, "");

        // Soft edges: a mid-grey sits inside the soft band between tolerance and tolerance + soft.
        var greyImg = Image(2, 1, (x, y) => x == 0 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)100, (byte)100, (byte)100, (byte)255));
        var hard = ColorKeyMask.Apply(greyImg, 2, 1, (0, 0, 0), 20, 0, keepArea: false, contiguousFrom: null)!;
        var softened = ColorKeyMask.Apply(greyImg, 2, 1, (0, 0, 0), 20, 200, keepArea: false, contiguousFrom: null)!;
        Check("colour key: with no soft band a pixel outside the tolerance stays fully opaque", hard[7] == 255 && hard[3] == 0, $"{hard[7]}");
        Check("colour key: a soft band gives a partly transparent edge",
              softened[7] is > 0 and < 255 && softened[3] == 0, $"alpha {softened[7]}");
        Check("colour key: the input is never changed", greyImg[3] == 255 && greyImg[7] == 255, "");

        // An already transparent pixel stays at most as opaque as it was.
        var semi = Image(1, 1, (x, y) => ((byte)10, (byte)10, (byte)10, (byte)128));
        var kept = ColorKeyMask.Apply(semi, 1, 1, (255, 255, 255), 5, 0, keepArea: false, contiguousFrom: null)!;
        Check("colour key: existing transparency is kept", kept[3] == 128, $"{kept[3]}");

        Check("colour key: a seed off the picture does nothing", ColorKeyMask.Apply(img, N, N, (0, 0, 0), 10, 0, false, (50, 50)) is null, "");
        Check("colour key: a buffer of the wrong size is refused", ColorKeyMask.Apply(new byte[10], N, N, (0, 0, 0), 10, 0, false, null) is null, "");

        var opaque = ColorKeyMask.OpaqueBounds(everywhere, N, N);
        Check("colour key: the opaque bounds hug what is left", opaque == new ColorKeyMask.PixelBox(2, 2, 3, 3), $"{opaque}");
        Check("colour key: nothing opaque has no bounds",
              ColorKeyMask.OpaqueBounds(new byte[16], 2, 2) is null, "");

        var crop = ColorKeyMask.CropBox(img, N, N, new ColorKeyMask.PixelBox(1, 1, 3, 3));
        Check("colour key: a box crop copies those pixels",
              crop is { W: 3, H: 3 } c && c.Bgra[(1 * 3 + 1) * 4] == 0 && c.Bgra[0] == 255, "");
        var clamped = ColorKeyMask.CropBox(img, N, N, new ColorKeyMask.PixelBox(-5, -5, 7, 7));
        Check("colour key: a box past the edge is clamped", clamped is { W: 2, H: 2 }, $"{clamped?.W}x{clamped?.H}");
        Check("colour key: a box off the picture is nothing", ColorKeyMask.CropBox(img, N, N, new ColorKeyMask.PixelBox(20, 20, 3, 3)) is null, "");

        var colour = Image(2, 2, (x, y) => ((byte)10, (byte)20, (byte)30, (byte)255));
        var mask = Image(1, 1, (x, y) => ((byte)0, (byte)0, (byte)0, (byte)64));
        var combined = ColorKeyMask.WithAlphaFrom(colour, 2, 2, mask, 1, 1)!;
        Check("colour key: a mask read separately is laid over the colours",
              combined[3] == 64 && combined[15] == 64 && combined[0] == 10, "");
    }

    // ── orientation ──────────────────────────────────────────────────────────────────────────

    private static void Orientation(Action<string, bool, string> Check)
    {
        // A 3 x 2 picture whose pixels carry their own coordinates in the blue channel: 10 * row + column.
        var src = Image(3, 2, (x, y) => ((byte)(10 * y + x), (byte)0, (byte)0, (byte)255));
        string Dump((byte[] Bgra, int W, int H)? r) => r is null ? "null" :
            $"{r.Value.W}x{r.Value.H}:" + string.Join(",", Enumerable.Range(0, r.Value.W * r.Value.H).Select(i => r.Value.Bgra[i * 4]));

        Check("orientation: upright is returned untouched", Dump(PictureOrientation.Apply(src, 3, 2, 1, 0, 0, 1)) == "3x2:0,1,2,10,11,12", "");
        Check("orientation: a mirrored picture is flipped back as shown", Dump(PictureOrientation.Apply(src, 3, 2, -1, 0, 0, 1)) == "3x2:2,1,0,12,11,10", "");
        Check("orientation: an upside-down picture is flipped as shown", Dump(PictureOrientation.Apply(src, 3, 2, 1, 0, 0, -1)) == "3x2:10,11,12,0,1,2", "");
        Check("orientation: a half turn", Dump(PictureOrientation.Apply(src, 3, 2, -1, 0, 0, -1)) == "3x2:12,11,10,2,1,0", "");
        // Turned a quarter clockwise on screen: the picture's left-to-right now runs DOWN the canvas and its
        // top-to-bottom runs LEFT, so the first column of the shown picture is the source's last row.
        Check("orientation: a quarter turn clockwise swaps the size and reads the rows as columns",
              Dump(PictureOrientation.Apply(src, 3, 2, 0, 1, -1, 0)) == "2x3:10,0,11,1,12,2", "");
        Check("orientation: a quarter turn counter-clockwise",
              Dump(PictureOrientation.Apply(src, 3, 2, 0, -1, 1, 0)) == "2x3:2,12,1,11,0,10", "");
        Check("orientation: a buffer of the wrong size is refused", PictureOrientation.Apply(new byte[8], 3, 2, 1, 0, 0, 1) is null, "");
    }

    // ── edits against a real file ────────────────────────────────────────────────────────────

    private static string Fixture(string tmp, string name, Action<XGraphics, PdfPage, string> draw, string? jpegPath = null)
    {
        string png = Path.Combine(tmp, "pic-red.png");
        if (!File.Exists(png)) File.WriteAllBytes(png, ImageGuard.Png(20, 20, 255, 0, 0));
        string path = Path.Combine(tmp, name);
        var d = new PdfDocument();
        var p = d.AddPage();   // 612 x 792
        using (var g = XGraphics.FromPdfPage(p)) draw(g, p, jpegPath ?? png);
        d.Save(path);
        return path;
    }

    private static void EditsInAFile(Action<string, bool, string> Check, string tmp)
    {
        if (!PdfiumInterop.CanEditPictures)
        {
            Check("pictures: this PDFium can edit pictures", false, PdfiumInterop.DescribeEditApi());
            return;
        }

        // Two draws of ONE image (a shared XObject), plus a third, mirrored, picture of its own.
        string src = Fixture(tmp, "pictures.pdf", (g, p, png) =>
        {
            var shared = XImage.FromFile(png);
            g.DrawImage(shared, 100, 100, 60, 80);    // user space x 100..160, y 612..692
            g.DrawImage(shared, 300, 300, 60, 80);    // user space x 300..360, y 412..492
            g.DrawString("caption", new XFont("Helvetica", 12), XBrushes.Black, 100, 60);
            g.Save();
            g.TranslateTransform(560, 0);
            g.ScaleTransform(-1, 1);                  // mirror: the next picture reads right to left
            g.DrawImage(XImage.FromFile(png), 100, 500, 50, 50);
            g.Restore();
        });

        var pics = PdfImageObjects.List(src, 0);
        Check("pictures: all three draws are listed", pics.Count == 3, $"{pics.Count}");
        if (pics.Count != 3) return;

        var first = pics[0];
        Check("pictures: the first picture's bounds are where it was drawn",
              Near(first.Bounds, new PdfRect(100, 612, 160, 692), 0.6), $"{first.Bounds}");
        Check("pictures: its pixel size is read", first.PixelWidth == 20 && first.PixelHeight == 20, $"{first.PixelWidth}x{first.PixelHeight}");
        var hit = PdfImageObjects.TopmostAt(pics, 330, 450);
        Check("pictures: a click lands on the topmost picture under it", hit is not null && hit.ObjectIndex == pics[1].ObjectIndex, "");
        Check("pictures: a click on empty page lands on nothing", PdfImageObjects.TopmostAt(pics, 20, 20) is null, "");

        using var open = PdfReader.Open(src, PdfDocumentOpenMode.Import);
        var gate = PdfImageObjects.Gate(open, first);
        Check("pictures: an RGB page passes the gate", gate.Allowed, gate.Reason ?? "");

        var mirrored = pics.First(p => p.IsMirrored);
        Check("pictures: the mirrored draw is recognised", mirrored.Matrix.A < 0, $"{mirrored.Matrix}");

        // Move the first picture; the shared image's other draw must be untouched.
        string moved = Path.Combine(tmp, "pictures-moved.pdf");
        var target = PicturePlacement.Translate(first.Bounds, 50, -30);
        var mv = PdfImageObjects.Reposition(src, moved, first, target, open);
        Check("pictures: a move succeeds", mv.Ok, mv.Error ?? "");
        var afterMove = PdfImageObjects.List(moved, 0);
        Check("pictures: the moved picture is where it was sent",
              afterMove.Any(p => Near(p.Bounds, target, 1.0)), string.Join(" | ", afterMove.Select(p => p.Bounds)));
        Check("pictures: the other draw of the same image did not move",
              afterMove.Any(p => Near(p.Bounds, pics[1].Bounds, 0.6)), "");
        Check("pictures: still three pictures", afterMove.Count == 3, $"{afterMove.Count}");
        var otherLift = PdfImageObjects.Lift(moved, afterMove.First(p => Near(p.Bounds, pics[1].Bounds, 0.6)));
        Check("pictures: the shared image's data is intact after the move",
              otherLift.Ok && otherLift.Width == 20 && otherLift.Height == 20 && otherLift.Bgra![2] == 255 && otherLift.Bgra[1] == 0,
              otherLift.Error ?? "");
        Check("pictures: the caption text survives the content rewrite", PageText(moved).Contains("caption"), PageText(moved));

        // Resize the mirrored picture: it must stay mirrored and fill the new box.
        string resized = Path.Combine(tmp, "pictures-resized.pdf");
        var bigger = new PdfRect(mirrored.Bounds.Left - 20, mirrored.Bounds.Bottom - 20, mirrored.Bounds.Right + 40, mirrored.Bounds.Top + 40);
        var rs = PdfImageObjects.Reposition(src, resized, mirrored, bigger, open);
        Check("pictures: a resize succeeds", rs.Ok, rs.Error ?? "");
        var afterResize = PdfImageObjects.List(resized, 0);
        var big = afterResize.FirstOrDefault(p => Near(p.Bounds, bigger, 1.0));
        Check("pictures: the resized picture fills the dragged box", big is not null, string.Join(" | ", afterResize.Select(p => p.Bounds)));
        Check("pictures: a resized mirrored picture is still mirrored", big is not null && big.IsMirrored, $"{big?.Matrix}");

        // Delete one draw: two remain, and the shared image still draws.
        string deleted = Path.Combine(tmp, "pictures-deleted.pdf");
        var del = PdfImageObjects.Delete(src, deleted, first, open);
        Check("pictures: a delete succeeds", del.Ok, del.Error ?? "");
        var afterDelete = PdfImageObjects.List(deleted, 0);
        Check("pictures: the deleted picture is gone and the others stay",
              afterDelete.Count == 2 && !afterDelete.Any(p => Near(p.Bounds, first.Bounds, 0.6)), $"{afterDelete.Count}");
        var stillThere = afterDelete.FirstOrDefault(p => Near(p.Bounds, pics[1].Bounds, 0.6));
        Check("pictures: the other draw of the deleted picture's image still reads",
              stillThere is not null && PdfImageObjects.Lift(deleted, stillThere).Ok, "");

        // Replace: a 40 x 20 half-transparent blue picture goes where the first one was.
        string replaced = Path.Combine(tmp, "pictures-replaced.pdf");
        var blue = Image(40, 20, (x, y) => x < 20 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)0, (byte)0, (byte)0));
        var rp = PdfImageObjects.Replace(src, replaced, first, blue, 40, 20, open);
        Check("pictures: a replace succeeds", rp.Ok, rp.Error ?? "");
        var afterReplace = PdfImageObjects.List(replaced, 0);
        var expectBounds = PicturePlacement.ReplacementMatrix(first.Matrix, 40, 20).Bounds();
        var swapped = afterReplace.FirstOrDefault(p => Near(p.Bounds, expectBounds, 1.5));
        Check("pictures: the replacement keeps the old width and top edge and takes its own proportions",
              swapped is not null && Math.Abs((expectBounds.Right - expectBounds.Left) - 60) < 0.01
              && Math.Abs((expectBounds.Top - expectBounds.Bottom) - 30) < 0.01 && Math.Abs(expectBounds.Top - first.Bounds.Top) < 0.01,
              string.Join(" | ", afterReplace.Select(p => p.Bounds)));
        Check("pictures: replace leaves the count alone", afterReplace.Count == 3, $"{afterReplace.Count}");
        Check("pictures: replace keeps the stacking position (same object index)",
              swapped is not null && swapped.ObjectIndex == first.ObjectIndex, $"{swapped?.ObjectIndex} vs {first.ObjectIndex}");
        var lifted = swapped is null ? null : PdfImageObjects.Lift(replaced, swapped);
        Check("pictures: the replacement reads back at its own size", lifted is { Ok: true, Width: 40, Height: 20 }, lifted?.Error ?? "");
        Check("pictures: the replacement's pixels and transparency survive",
              lifted is { Ok: true } l && l.Bgra![0] == 255 && l.Bgra[2] == 0
              && l.Bgra.Where((v, i) => i % 4 == 3).Any(a => a < 255), "");
        var sharedLift = PdfImageObjects.Lift(replaced, afterReplace.First(p => Near(p.Bounds, pics[1].Bounds, 0.6)));
        Check("pictures: the shared image is untouched by a replace of its other draw",
              sharedLift.Ok && sharedLift.Bgra![2] == 255 && sharedLift.Bgra[0] == 0, sharedLift.Error ?? "");

        // A selection that no longer matches the page is noticed, not edited blindly.
        var stale = first with { Bounds = new PdfRect(400, 400, 440, 440), ObjectIndex = 0 };
        var staleResult = PdfImageObjects.Delete(src, Path.Combine(tmp, "pictures-stale.pdf"), stale, open);
        Check("pictures: a stale selection is refused", !staleResult.Ok && !staleResult.ShouldOfferOverlay, staleResult.Error ?? "");

        // JPEG: the original bytes come back as they were stored.
        string jpg = Path.Combine(tmp, "pic.jpg");
        using (var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(32, 24, new SixLabors.ImageSharp.PixelFormats.Rgb24(10, 200, 10)))
            SixLabors.ImageSharp.ImageExtensions.SaveAsJpeg(img, jpg);
        string jsrc = Fixture(tmp, "pictures-jpeg.pdf", (g, p, _) => g.DrawImage(XImage.FromFile(jpg), 100, 100, 64, 48));
        var jp = PdfImageObjects.List(jsrc, 0);
        var jl = jp.Count == 1 ? PdfImageObjects.Lift(jsrc, jp[0]) : null;
        Check("pictures: a JPEG picture offers its original bytes",
              jl is { Ok: true, Jpeg: not null } && jl.Jpeg![0] == 0xFF && jl.Jpeg[1] == 0xD8 && jl.Width == 32 && jl.Height == 24,
              $"filters [{string.Join(",", jp.FirstOrDefault()?.Filters ?? Array.Empty<string>())}] {jl?.Error}");
        Check("pictures: a Flate picture has no original JPEG to offer",
              PdfImageObjects.Lift(src, pics[1]) is { Ok: true, Jpeg: null }, "");
    }

    private static void RotatedPageEdit(Action<string, bool, string> Check, string tmp)
    {
        if (!PdfiumInterop.CanEditPictures) return;

        foreach (int rot in new[] { 90, 270 })
        {
            string src = Fixture(tmp, $"pictures-rot{rot}.pdf", (g, p, png) =>
            {
                g.DrawImage(XImage.FromFile(png), 200, 300, 60, 80);
                var arr = new PdfArray();
                foreach (double v in new[] { 100.0, 200.0, 500.0, 700.0 }) arr.Elements.Add(new PdfReal(v));
                p.Elements["/CropBox"] = arr;
                p.Rotate = rot;
            });

            using var open = PdfReader.Open(src, PdfDocumentOpenMode.Import);
            var pic = PdfImageObjects.List(src, 0).Single();
            var box = PdfPageGeometry.VisibleBox(open.Pages[0]);
            int rotation = PdfPageGeometry.Rotation(open.Pages[0]);
            // The canvas the page would be drawn on: 2 px per point.
            double rw = (rot is 90 or 270 ? box.Height : box.Width) * 2, rh = (rot is 90 or 270 ? box.Width : box.Height) * 2;

            var before = PicturePlacement.ToCanvas(box, rotation, rw, rh, pic.Bounds);
            var target = PicturePlacement.MovedBounds(box, rotation, rw, rh, pic.Bounds, 40, 24);
            string dest = Path.Combine(tmp, $"pictures-rot{rot}-moved.pdf");
            var res = PdfImageObjects.Reposition(src, dest, pic, target, open);
            Check($"pictures: a move on a /Rotate {rot}, cropped page succeeds", res.Ok, res.Error ?? "");
            var after = PdfImageObjects.List(dest, 0).FirstOrDefault();
            if (after is null) { Check($"pictures: the moved picture is found (/Rotate {rot})", false, ""); continue; }
            var landed = PicturePlacement.ToCanvas(box, rotation, rw, rh, after.Bounds);
            Check($"pictures: it lands 40 right and 24 down on the canvas (/Rotate {rot})",
                  Math.Abs(landed.X - (before.X + 40)) < 1.0 && Math.Abs(landed.Y - (before.Y + 24)) < 1.0
                  && Math.Abs(landed.W - before.W) < 0.5 && Math.Abs(landed.H - before.H) < 0.5,
                  $"{landed} from {before}");
        }
    }

    private static void HazardPageIsRefused(Action<string, bool, string> Check, string tmp)
    {
        if (!PdfiumInterop.CanEditPictures) return;

        // A CMYK fill on the page: rewriting the content would turn it black, so the edit is refused.
        string png = Path.Combine(tmp, "pic-red.png");
        if (!File.Exists(png)) File.WriteAllBytes(png, ImageGuard.Png(20, 20, 255, 0, 0));
        string src = Path.Combine(tmp, "pictures-cmyk.pdf");
        {
            var d = new PdfDocument();
            d.Options.ColorMode = PdfColorMode.Cmyk;
            var p = d.AddPage();
            using (var g = XGraphics.FromPdfPage(p))
            {
                g.DrawImage(XImage.FromFile(png), 100, 100, 60, 80);
                g.DrawRectangle(new XSolidBrush(XColor.FromCmyk(1, 0.2, 0, 0.1)), 300, 300, 100, 50);
            }
            d.Save(src);
        }
        using var open = PdfReader.Open(src, PdfDocumentOpenMode.Import);
        var pic = PdfImageObjects.List(src, 0).Single();
        string dest = Path.Combine(tmp, "pictures-cmyk-moved.pdf");
        var res = PdfImageObjects.Reposition(src, dest, pic, PicturePlacement.Translate(pic.Bounds, 10, 10), open);
        Check("pictures: a CMYK page refuses an in-place edit, says why and offers the overlay",
              !res.Ok && res.ShouldOfferOverlay && res.Error is { } e && e.Contains("colour", StringComparison.OrdinalIgnoreCase),
              res.Error ?? "");
        Check("pictures: a refused edit writes nothing", !File.Exists(dest), "");

        // The picture can still be read (copy and Save Picture As do not need the gate).
        var lift = PdfImageObjects.Lift(src, pic);
        Check("pictures: a refused page's picture can still be lifted", lift.Ok && lift.Width == 20, lift.Error ?? "");
    }

    private static string PageText(string path)
    {
        using var d = UglyToad.PdfPig.PdfDocument.Open(path);
        return d.GetPage(1).Text;
    }

    private static bool Near(PdfRect a, PdfRect b, double tol) =>
        Math.Abs(a.Left - b.Left) <= tol && Math.Abs(a.Bottom - b.Bottom) <= tol
        && Math.Abs(a.Right - b.Right) <= tol && Math.Abs(a.Top - b.Top) <= tol;
}
