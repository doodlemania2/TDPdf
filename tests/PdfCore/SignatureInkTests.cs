using System.Text.Json;
using TDPdf;
using TDPdf.Services;

/// <summary>
/// #249: the signature editor's pixel work (threshold, feather, recolour, strokes), its colour and
/// width rules, the date line text, and the guarantee that signatures.json stays readable both ways.
/// </summary>
internal static class SignatureInkTests
{
    // One BGRA pixel.
    private static byte[] Px(byte r, byte g, byte b, byte a = 255) => [b, g, r, a];

    private static byte[] Join(params byte[][] px) => px.SelectMany(p => p).ToArray();

    public static void Run(Action<string, bool, string> Check)
    {
        // ---- colour and width
        Check("signature ink: #RRGGBB parses", SignatureInk.TryParseColor("#1A2B3C", out var r, out var g, out var b) && r == 0x1A && g == 0x2B && b == 0x3C, "");
        Check("signature ink: no hash parses", SignatureInk.TryParseColor("ff0000", out r, out g, out b) && r == 255 && g == 0 && b == 0, "");
        Check("signature ink: junk does not parse", !SignatureInk.TryParseColor("red", out _, out _, out _) && !SignatureInk.TryParseColor("#12345", out _, out _, out _)
              && !SignatureInk.TryParseColor(null, out _, out _, out _) && !SignatureInk.TryParseColor("#GG0000", out _, out _, out _), "");
        Check("signature ink: a missing colour is black", SignatureInk.ResolveColor(null) == (0, 0, 0) && SignatureInk.ResolveColor("x") == (0, 0, 0), "");
        Check("signature ink: colour round-trips", SignatureInk.FormatColor(0x12, 0x2A, 0x88) == "#122A88", SignatureInk.FormatColor(0x12, 0x2A, 0x88));
        Check("signature ink: a missing pen width is the old 2", SignatureInk.ResolvePenWidth(null) == 2 && SignatureInk.ResolvePenWidth(double.NaN) == 2
              && SignatureInk.ResolvePenWidth(0) == 2 && SignatureInk.ResolvePenWidth(-3) == 2, "");
        Check("signature ink: a pen width is kept within range", SignatureInk.ResolvePenWidth(4) == 4 && SignatureInk.ResolvePenWidth(500) == SignatureInk.MaxPenWidth
              && SignatureInk.ResolvePenWidth(0.01) == SignatureInk.MinPenWidth, "");

        // ---- date line
        Check("signature date: yyyy-MM-dd", SignatureInk.FormatDate(new DateTime(2026, 3, 9)) == "2026-03-09", SignatureInk.FormatDate(new DateTime(2026, 3, 9)));
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Check("signature date: not changed by the regional calendar", SignatureInk.FormatDate(new DateTime(2026, 3, 9)) == "2026-03-09", SignatureInk.FormatDate(new DateTime(2026, 3, 9)));
        }
        catch (System.Globalization.CultureNotFoundException) { }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
        Check("signature date: strip is in range", SignatureInk.DateStripFor(150) == 30 && SignatureInk.DateStripFor(10) == 14 && SignatureInk.DateStripFor(1000) == 60
              && SignatureInk.DateStripFor(double.NaN) == 28, "");
        Check("signature date: a strip is used only with date text and when it fits the box",
              SignatureInk.EffectiveDateStrip("2026-01-01", 20, 100) == 20 && SignatureInk.EffectiveDateStrip(null, 20, 100) == 0
              && SignatureInk.EffectiveDateStrip("", 20, 100) == 0 && SignatureInk.EffectiveDateStrip("x", 100, 100) == 0
              && SignatureInk.EffectiveDateStrip("x", double.NaN, 100) == 0 && SignatureInk.EffectiveDateStrip("x", -1, 100) == 0, "");
        Check("signature date: the font fits a narrow box", SignatureInk.DateFontSize(30, 40) <= 40 / 6.5 + 1e-9 && SignatureInk.DateFontSize(30, 1000) > 10, "");

        // ---- threshold
        var photo = Join(Px(10, 10, 10), Px(250, 250, 250), Px(120, 120, 120));
        var t = (byte[])photo.Clone();
        SignatureInk.ThresholdAlpha(t, 3, 1, 128);
        Check("signature threshold: dark ink stays", t[3] == 255, $"{t[3]}");
        Check("signature threshold: paper goes transparent", t[7] == 0, $"{t[7]}");
        Check("signature threshold: a pixel just under the threshold is kept at least in part", t[11] > 0, $"{t[11]}");
        var off = (byte[])photo.Clone();
        SignatureInk.ThresholdAlpha(off, 3, 1, 0);
        Check("signature threshold: 0 changes nothing", off.SequenceEqual(photo), "");
        var low = (byte[])photo.Clone();
        SignatureInk.ThresholdAlpha(low, 3, 1, 5);
        Check("signature threshold: a lower threshold keeps less ink", low[3] == 0 && low[11] == 0, "");
        var semi = Join(Px(0, 0, 0, 100));
        SignatureInk.ThresholdAlpha(semi, 1, 1, 200);
        Check("signature threshold: existing alpha is never raised", semi[3] <= 100, $"{semi[3]}");

        // ---- recolour
        var rc = Join(Px(0, 0, 0, 200), Px(255, 255, 255, 0));
        SignatureInk.Recolor(rc, 2, 1, 10, 20, 30);
        Check("signature recolour: sets colour, keeps alpha", rc[0] == 30 && rc[1] == 20 && rc[2] == 10 && rc[3] == 200 && rc[7] == 0, "");

        // ---- feather
        int W = 21, H = 21;
        var line = new byte[W * H * 4];
        for (int x = 0; x < W; x++) { int o = (10 * W + x) * 4; line[o + 2] = 200; line[o + 3] = 255; }   // a 1px line, red
        var core = (byte[])line.Clone();
        SignatureInk.Feather(core, W, H, 2);
        Check("signature feather: the core of a thin line is not faded", core[(10 * W + 10) * 4 + 3] == 255, $"{core[(10 * W + 10) * 4 + 3]}");
        Check("signature feather: the edge grows a soft halo", core[(12 * W + 10) * 4 + 3] > 0 && core[(12 * W + 10) * 4 + 3] < 255, $"{core[(12 * W + 10) * 4 + 3]}");
        Check("signature feather: the halo fades with distance", core[(11 * W + 10) * 4 + 3] >= core[(12 * W + 10) * 4 + 3] && core[(18 * W + 10) * 4 + 3] == 0, "");
        Check("signature feather: the halo takes the ink's colour, not paper's", core[(11 * W + 10) * 4 + 2] == 200 && core[(11 * W + 10) * 4] == 0, $"{core[(11 * W + 10) * 4 + 2]}");
        var nof = (byte[])line.Clone();
        SignatureInk.Feather(nof, W, H, 0);
        Check("signature feather: radius 0 changes nothing", nof.SequenceEqual(line), "");
        Check("signature feather: strength maps to a radius that grows with size",
              SignatureInk.FeatherRadius(0, 1000, 500) == 0 && SignatureInk.FeatherRadius(1, 100, 100) == 1
              && SignatureInk.FeatherRadius(10, 1000, 500) == 20 && SignatureInk.FeatherRadius(50, 1000, 500) == 20, "");
        var one = Join(Px(0, 0, 0));
        SignatureInk.Feather(one, 1, 1, 3);
        Check("signature feather: a one pixel image is fine", one[3] == 255, "");

        // ---- the pipeline
        var src = Join(Px(5, 5, 5), Px(250, 250, 250), Px(5, 5, 5), Px(250, 250, 250));
        var srcCopy = (byte[])src.Clone();
        var done = SignatureInk.Process(src, 2, 2, 128, 0, "#0000FF");
        Check("signature process: the source is left alone", src.SequenceEqual(srcCopy), "");
        Check("signature process: ink is recoloured, paper is clear",
              done[0] == 255 && done[1] == 0 && done[2] == 0 && done[3] == 255 && done[7] == 0, "");
        var keep = SignatureInk.Process(src, 2, 2, 128, 0, null);
        Check("signature process: no colour keeps the picture's own", keep[0] == 5 && keep[3] == 255, "");
        Check("signature process: a photo is opaque, a cut-out is not",
              SignatureInk.IsFullyOpaque(src, 2, 2) && !SignatureInk.IsFullyOpaque(done, 2, 2), "");

        // ---- bounds and crop
        var big = new byte[10 * 8 * 4];
        foreach (var (x, y) in new[] { (3, 2), (6, 5) }) big[(y * 10 + x) * 4 + 3] = 255;
        var bb = SignatureInk.InkBounds(big, 10, 8);
        Check("signature bounds: the box of the ink", bb is { X: 3, Y: 2, W: 4, H: 4 }, $"{bb}");
        Check("signature bounds: nothing is null", SignatureInk.InkBounds(new byte[16], 2, 2) is null, "");
        var cropped = SignatureInk.Crop(big, 10, 8, bb!.Value, 1, out int cw, out int ch);
        Check("signature crop: padded size", cw == 6 && ch == 6 && cropped.Length == 6 * 6 * 4, $"{cw}x{ch}");
        Check("signature crop: ink lands inside the padding", cropped[(1 * 6 + 1) * 4 + 3] == 255 && cropped[(4 * 6 + 4) * 4 + 3] == 255 && cropped[3] == 0, "");

        // ---- strokes
        var strokes = new List<IReadOnlyList<(double X, double Y)>>
        {
            new List<(double X, double Y)> { (5, 10), (35, 10) },
            new List<(double X, double Y)> { (20, 20) }   // a lone point draws nothing
        };
        var ras = SignatureInk.RasterizeStrokes(strokes, 40, 30, 4, 1, 255, 0, 0, out int rw, out int rh);
        Check("signature strokes: buffer is the canvas size", rw == 40 && rh == 30 && ras.Length == 40 * 30 * 4, $"{rw}x{rh}");
        Check("signature strokes: the line is solid in its middle, in the ink colour",
              ras[(10 * 40 + 20) * 4 + 3] == 255 && ras[(10 * 40 + 20) * 4 + 2] == 255 && ras[(10 * 40 + 20) * 4] == 0, "");
        Check("signature strokes: round cap reaches past the end point", ras[(10 * 40 + 36) * 4 + 3] > 0, "");
        Check("signature strokes: far from the line is clear, and a lone point draws nothing", ras[(25 * 40 + 20) * 4 + 3] == 0 && ras[(20 * 40 + 20) * 4 + 3] == 0, "");
        var thin = SignatureInk.RasterizeStrokes(strokes, 40, 30, 1, 1, 0, 0, 0, out _, out _);
        int Cover(byte[] a) { int n = 0; for (int i = 3; i < a.Length; i += 4) if (a[i] > 0) n++; return n; }
        Check("signature strokes: a wider pen covers more pixels", Cover(ras) > Cover(thin), $"{Cover(ras)} vs {Cover(thin)}");
        var tri = SignatureInk.RasterizeStrokes(strokes, 40, 30, 4, 3, 0, 0, 0, out int tw, out int th);
        Check("signature strokes: scale multiplies the buffer", tw == 120 && th == 90, $"{tw}x{th}");

        // ---- signatures.json compatibility
        string old = """
            [ { "Id": "abc", "Name": "Old", "Strokes": [[{"X":1,"Y":2},{"X":3,"Y":4}]], "CanvasWidth": 400, "CanvasHeight": 150, "ImageData": null },
              { "Id": "img", "Name": "Pic", "Strokes": [], "CanvasWidth": 10, "CanvasHeight": 5, "ImageData": "AAAA" } ]
            """;
        var list = JsonSerializer.Deserialize<List<SavedSignature>>(old);
        Check("signature file: a pre-editor file loads", list is { Count: 2 } && list[0].Strokes.Count == 1 && list[1].ImageData == "AAAA", "");
        Check("signature file: its new fields are all unset, so it draws black and 2 wide",
              list![0].InkColor is null && list[0].PenWidth is null && list[1].SourceImageData is null && list[1].Threshold is null && list[1].Feather is null
              && SignatureInk.ResolveColor(list[0].InkColor) == (0, 0, 0) && SignatureInk.ResolvePenWidth(list[0].PenWidth) == 2, "");

        var withNew = new SavedSignature { Id = "n", InkColor = "#122A88", PenWidth = 3.5, SourceImageData = "QQ==", Threshold = 140, Feather = 2, ImageData = "AAAA" };
        string json = JsonSerializer.Serialize(new List<SavedSignature> { withNew });
        var back = JsonSerializer.Deserialize<List<SavedSignature>>(json)![0];
        Check("signature file: the editor's fields round-trip",
              back.InkColor == "#122A88" && back.PenWidth == 3.5 && back.SourceImageData == "QQ==" && back.Threshold == 140 && back.Feather == 2, json);

        // An older TDPdf has none of the new properties: reading the new file must skip them, not throw.
        var oldReader = JsonSerializer.Deserialize<List<OldSavedSignature>>(json);
        Check("signature file: an older reader skips the new fields", oldReader is { Count: 1 } && oldReader[0].Id == "n" && oldReader[0].ImageData == "AAAA", "");

        // An edit keeps the same id, so a merge with another window's copy takes the edited one.
        var edited = new SavedSignature { Id = "a", InkColor = "#B00020" };
        var m = SignatureStoreFile.Merge(new[] { edited }, new[] { "a" }, new[] { new SavedSignature { Id = "a" } }, x => x.Id);
        Check("signature file: an edited signature wins the merge by id", m.Count == 1 && m[0].InkColor == "#B00020", "");
    }

    // What the signature class looked like before the editor.
    private sealed class OldSavedSignature
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<List<SerializablePoint>> Strokes { get; set; } = new();
        public double CanvasWidth { get; set; } = 400;
        public double CanvasHeight { get; set; } = 150;
        public string? ImageData { get; set; }
    }
}
