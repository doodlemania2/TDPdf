using System.IO.Compression;
using System.Text;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using TDPdf.Services;
using PdfDocument = PdfSharpCore.Pdf.PdfDocument;

/// <summary>
/// Watermarks and page numbers (3.0): the content written, the page-number text, and that removal
/// finds exactly what was added and nothing else. Placement is judged on PDFium renders of the saved file.
/// </summary>
internal static class WatermarkTests
{
    private const double PageW = 612, PageH = 792;

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("== Watermarks ==");
        Rules(Check);
        Written(Check, tmp);
        Pictures(Check, tmp);
        PageNumbers(Check, tmp);
        Removal(Check, tmp);
    }

    private static bool Near(double a, double b, double eps = 0.01) => Math.Abs(a - b) <= eps;

    private static void Rules(Action<string, bool, string> Check)
    {
        Check("page number: {n} and {N} are the page and the total",
              WatermarkMath.FormatPageNumber("Page {n} of {N}", 3, 12) == "Page 3 of 12", "");
        Check("page number: a format may repeat or reorder them, and other braces stay",
              WatermarkMath.FormatPageNumber("{N} - {n} ({n}) {x}", 2, 9) == "9 - 2 (2) {x}", "");
        Check("page number: a blank format is just the number", WatermarkMath.FormatPageNumber("  ", 7, 9) == "7" && WatermarkMath.FormatPageNumber(null, 1, 1) == "1", "");
        Check("page number: digits do not follow the machine's culture",
              WatermarkMath.FormatPageNumber("{n}/{N}", 1234, 5678) == "1234/5678", "");

        Check("picture size: pixels at the picture's resolution are points (300 px at 150 dpi is 144 pt)", Near(WatermarkMath.NaturalPoints(300, 150), 144), "");
        Check("picture size: no recorded resolution means 96 dpi", Near(WatermarkMath.NaturalPoints(96, 0), 72) && Near(WatermarkMath.NaturalPoints(96, double.NaN), 72), "");
        var spec = new WatermarkSpec { Kind = WatermarkKind.Picture, ImagePixelWidth = 300, ImagePixelHeight = 150, ImageDpiX = 150, ImageDpiY = 150, ImageScalePercent = 50 };
        var (w, h) = WatermarkMath.PictureSize(spec);
        Check("picture size: scale is a percent of that natural size", Near(w, 72) && Near(h, 36), $"{w}x{h}");
        spec.ImageScalePercent = 100000;
        Check("picture size: scale is clamped to its range", Near(WatermarkMath.PictureSize(spec).W, 1440), "");

        Check("turn: any typed angle lands in -180..180", Near(WatermarkMath.NormalizeDegrees(370), 10) && Near(WatermarkMath.NormalizeDegrees(-190), 170)
              && Near(WatermarkMath.NormalizeDegrees(180), 180) && WatermarkMath.NormalizeDegrees(double.NaN) == 0, "");
        Check("limits: opacity percent, font size and scale clamp",
              WatermarkMath.ClampOpacityPercent(1) == 5 && WatermarkMath.ClampOpacityPercent(500) == 100
              && WatermarkMath.ClampFontSize(2) == 6 && WatermarkMath.ClampFontSize(9999) == 400
              && WatermarkMath.ClampScalePercent(0) == 1, "");

        var s2 = new WatermarkSpec { Place = WatermarkPlace.Center };
        var c = WatermarkMath.Place(s2, 612, 792, 200, 50);
        Check("place: centre is the middle of the page, unturned", Near(c.Cx, 306) && Near(c.Cy, 396) && Near(c.Angle, 0), "");
        s2.Place = WatermarkPlace.Diagonal;
        var d = WatermarkMath.Place(s2, 612, 792, 200, 50);
        Check("place: diagonal runs along the page's own diagonal", Near(d.Angle, Math.Atan2(792, 612) * 180 / Math.PI, 1e-6) && Near(d.Cx, 306), "");
        s2.RotationDegrees = 10;
        Check("place: an extra turn adds to the diagonal", Near(WatermarkMath.Place(s2, 612, 792, 200, 50).Angle, Math.Atan2(792, 612) * 180 / Math.PI + 10, 1e-6), "");
        s2.Place = WatermarkPlace.Top; s2.RotationDegrees = 0;
        var t = WatermarkMath.Place(s2, 612, 792, 200, 50);
        s2.Place = WatermarkPlace.Bottom;
        var b = WatermarkMath.Place(s2, 612, 792, 200, 50);
        Check("place: top and bottom keep the box inside the margin", Near(t.Cy, 36 + 25) && Near(b.Cy, 792 - 36 - 25), $"{t.Cy} {b.Cy}");
        var a1 = WatermarkMath.NumberAnchor(PageNumberPlace.BottomRight, 612, 792, 60, 12);
        var a2 = WatermarkMath.NumberAnchor(PageNumberPlace.TopCenter, 612, 792, 60, 12);
        Check("page number anchor: corners and centre sit inside the margin", Near(a1.X, 612 - 28 - 60) && Near(a1.Y, 792 - 28 - 12) && Near(a2.X, 276) && Near(a2.Y, 28), "");
    }

    // ---- fixtures -------------------------------------------------------------------------------

    private static string MakeDoc(string tmp, string name, int pages, int rotate = 0, bool compress = false)
    {
        var doc = new PdfDocument();
        if (compress) doc.Options.CompressContentStreams = true;
        for (int i = 0; i < pages; i++)
        {
            var page = doc.AddPage();
            page.Width = PageW; page.Height = PageH;
            if (rotate != 0) page.Rotate = rotate;
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"Original content {i + 1}", new XFont("Helvetica", 12), XBrushes.Black, new XPoint(40, 60));
        }
        string path = Path.Combine(tmp, name + ".pdf");
        doc.Save(path);
        return path;
    }

    private static PdfDocument Open(string path) => PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);

    private static string SaveTo(PdfDocument doc, string tmp, string name)
    {
        string path = Path.Combine(tmp, name + ".pdf");
        doc.Save(path);
        return path;
    }

    private static List<string> Streams(PdfPage page) =>
        page.Contents.Elements.Select(e => PdfItems.Deref(e) as PdfDictionary)
            .Where(d => d?.Stream is not null).Select(d => Encoding.Latin1.GetString(d!.Stream.UnfilteredValue)).ToList();

    private static string Joined(PdfPage page) => string.Join("\n", Streams(page));

    private static List<string> XObjectNames(PdfPage page) =>
        (page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject")?.Elements.Keys ?? new Dictionary<string, PdfItem>().Keys).OrderBy(k => k).ToList();

    internal static byte[] MakePng(int w, int h, byte r, byte g, byte b, double dpi)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < w; x++) { raw.WriteByte(r); raw.WriteByte(g); raw.WriteByte(b); }
        }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true)) zs.Write(raw.ToArray());
        var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes((uint)data.Length); Array.Reverse(len);
            png.Write(len);
            var td = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            png.Write(td);
            uint crc = 0xFFFFFFFF;
            foreach (byte by in td) { crc ^= by; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1; }
            var cb = BitConverter.GetBytes(~crc); Array.Reverse(cb);
            png.Write(cb);
        }
        byte[] BE(uint v) { var x = BitConverter.GetBytes(v); Array.Reverse(x); return x; }
        Chunk("IHDR", BE((uint)w).Concat(BE((uint)h)).Concat(new byte[] { 8, 2, 0, 0, 0 }).ToArray());
        uint ppm = (uint)Math.Round(dpi / 0.0254);
        Chunk("pHYs", BE(ppm).Concat(BE(ppm)).Concat(new byte[] { 1 }).ToArray());
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static WatermarkSpec PictureSpec(double scale, WatermarkPlace place, int[] pages) => new()
    {
        Kind = WatermarkKind.Picture, Place = place, Pages = pages, Opacity = 1,
        ImageBytes = MakePng(300, 150, 220, 20, 20, 150), ImagePixelWidth = 300, ImagePixelHeight = 150, ImageDpiX = 150, ImageDpiY = 150,
        ImageScalePercent = scale
    };

    private static (int L, int T, int R, int B)? RedInk(string path, int w, int h)
    {
        var bgra = PdfiumInterop.RenderPageWithAnnotations(path, 0, w, h);
        if (bgra is null) return null;
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (bgra[i + 2] > bgra[i + 1] + 80 && bgra[i + 2] > bgra[i] + 80)
                { l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y); }
            }
        return r < 0 ? null : (l, t, r + 1, b + 1);
    }

    // ---- tests ----------------------------------------------------------------------------------

    private static void Written(Action<string, bool, string> Check, string tmp)
    {
        string src = MakeDoc(tmp, "wm-src", 4);
        using var doc = Open(src);
        var before = doc.Pages.Cast<PdfPage>().Select(p => Joined(p)).ToList();
        var spec = new WatermarkSpec { Text = "DRAFT", Pages = new[] { 0, 2 }, Layer = WatermarkLayer.Behind };
        var res = WatermarkWriter.Apply(doc, spec, _ => true);
        Check("a watermark goes on exactly the pages asked for", res.Watermarks == 2 && res.Numbered == 0 && res.Notice is null && WatermarkWriter.Count(doc) == 2, $"{res.Watermarks} {WatermarkWriter.Count(doc)}");
        Check("other pages are untouched", Joined(doc.Pages[1]) == before[1] && Joined(doc.Pages[3]) == before[3], "");
        var s0 = Streams(doc.Pages[0]);
        Check("behind: the watermark's stream comes first and is a marked artifact of subtype Watermark",
              s0.Count == 2 && s0[0].Contains("/Artifact") && s0[0].Contains("/Subtype /Watermark") && s0[0].Contains("/TdpWmKind (watermark)") && s0[0].Contains("BDC") && s0[0].TrimEnd().EndsWith("EMC")
              && s0[1] == before[0], string.Join(" || ", s0.Select(x => x.Length > 60 ? x[..60] : x)));
        Check("behind: the page's own content is not rewritten", s0[^1] == before[0], "");
        Check("the form is registered on the page under a TdpWm name", XObjectNames(doc.Pages[0]).Any(n => n.StartsWith("/TdpWm")), string.Join(",", XObjectNames(doc.Pages[0])));

        string path = SaveTo(doc, tmp, "wm-text");
        using var pig = PdfDocument_Pig(path);
        Check("the saved file still reads text of the page", pig.GetPage(1).Text.Contains("Original content 1"), "");
        var bgra = PdfiumInterop.RenderPageWithAnnotations(path, 0, 612, 792);
        int ink = 0;
        if (bgra is not null) for (int i = 0; i + 3 < bgra.Length; i += 4) if (bgra[i] is > 170 and < 246 && Math.Abs(bgra[i] - bgra[i + 2]) < 6 && Math.Abs(bgra[i] - bgra[i + 1]) < 6) ink++;
        Check("PDFium draws the text watermark (pale grey ink, 30% of mid grey)", ink > 1500, ink.ToString());

        // Over: wrapped in q/Q and appended.
        using var doc2 = Open(src);
        var over = new WatermarkSpec { Text = "COPY", Pages = new[] { 0 }, Layer = WatermarkLayer.Over };
        WatermarkWriter.Apply(doc2, over, _ => true);
        var so = Streams(doc2.Pages[0]);
        Check("over: the page's content is wrapped in q and Q and the watermark follows it",
              so.Count == 4 && so[0].StartsWith("q") && so[2].StartsWith("Q") && so[3].Contains("/TdpWmKind (watermark)") && so[1] == before[0], string.Join(" || ", so.Select(x => x.Length > 20 ? x[..20] : x)));
        WatermarkWriter.Apply(doc2, over, _ => true);
        Check("over, applied twice: wrapped once", Streams(doc2.Pages[0]).Count(x => x.StartsWith("q %TdpWmWrap")) == 1 && WatermarkWriter.Count(doc2) == 2, "");

        // Font not installed: kept by name, drawn in a stand-in, with a notice.
        using var doc3 = Open(src);
        var fspec = new WatermarkSpec { Text = "NOTE", FontFamily = "Garamond Premier Pro", Pages = new[] { 0 } };
        var fres = WatermarkWriter.Apply(doc3, fspec, f => f != "Garamond Premier Pro");
        var form = PdfItems.Deref(doc3.Pages[0].Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/XObject")!.Elements[XObjectNames(doc3.Pages[0]).First(n => n.StartsWith("/TdpWm"))]) as PdfDictionary;
        Check("a font that is not installed: a notice names it, the spec still holds the name, and the form records it",
              fres.Watermarks == 1 && fres.Notice is { } n && n.Contains("Garamond Premier Pro") && fspec.FontFamily == "Garamond Premier Pro"
              && form?.Elements.GetString("/TdpWmFont") == "Garamond Premier Pro", fres.Notice ?? "(no notice)");
        Check("an installed font gives no notice", WatermarkWriter.Apply(Open(src), new WatermarkSpec { Text = "OK", Pages = new[] { 0 } }, _ => true).Notice is null, "");

        Check("nothing to draw, nothing written", WatermarkWriter.Apply(Open(src), new WatermarkSpec { Text = "  ", Pages = new[] { 0 } }, _ => true) is { Watermarks: 0, Numbered: 0 }
              && !new WatermarkSpec { Text = "" }.HasContent && new WatermarkSpec { Text = "x" }.HasContent, "");
        Check("pages outside the document are ignored", WatermarkWriter.Apply(Open(src), new WatermarkSpec { Text = "X", Pages = new[] { -1, 99 } }, _ => true).Watermarks == 0, "");
    }

    private static UglyToad.PdfPig.PdfDocument PdfDocument_Pig(string path) => UglyToad.PdfPig.PdfDocument.Open(path);

    private static void Pictures(Action<string, bool, string> Check, string tmp)
    {
        string src = MakeDoc(tmp, "wm-pic-src", 1);
        bool Box((int L, int T, int R, int B)? box, double cx, double cy, double w, double h, double eps = 3) =>
            box is { } b && Math.Abs((b.L + b.R) / 2.0 - cx) <= eps && Math.Abs((b.T + b.B) / 2.0 - cy) <= eps && Math.Abs((b.R - b.L) - w) <= eps && Math.Abs((b.B - b.T) - h) <= eps;

        using (var doc = Open(src))
        {
            WatermarkWriter.Apply(doc, PictureSpec(100, WatermarkPlace.Center, new[] { 0 }), _ => true);
            var box = RedInk(SaveTo(doc, tmp, "wm-pic-100"), 612, 792);
            Check("picture at 100%: its own size from its resolution (300 px at 150 dpi = 144 x 72 pt), centred", Box(box, 306, 396, 144, 72), box?.ToString() ?? "no ink");
        }
        using (var doc = Open(src))
        {
            WatermarkWriter.Apply(doc, PictureSpec(50, WatermarkPlace.Center, new[] { 0 }), _ => true);
            var box = RedInk(SaveTo(doc, tmp, "wm-pic-50"), 612, 792);
            Check("picture at 50%: half of that", Box(box, 306, 396, 72, 36), box?.ToString() ?? "no ink");
        }
        using (var doc = Open(src))
        {
            WatermarkWriter.Apply(doc, PictureSpec(100, WatermarkPlace.Top, new[] { 0 }), _ => true);
            var box = RedInk(SaveTo(doc, tmp, "wm-pic-top"), 612, 792);
            Check("picture at the top: its top edge sits one margin down", box is { } b && Math.Abs(b.T - 36) <= 3 && Math.Abs((b.L + b.R) / 2.0 - 306) <= 3, box?.ToString() ?? "no ink");
        }
        using (var doc = Open(src))
        {
            var s = PictureSpec(100, WatermarkPlace.Center, new[] { 0 });
            s.RotationDegrees = 90;
            WatermarkWriter.Apply(doc, s, _ => true);
            var box = RedInk(SaveTo(doc, tmp, "wm-pic-turn"), 612, 792);
            Check("picture turned 90 degrees swaps its width and height", Box(box, 306, 396, 72, 144), box?.ToString() ?? "no ink");
        }
        foreach (int rot in new[] { 90, 180, 270 })
        {
            string rsrc = MakeDoc(tmp, $"wm-pic-rot{rot}-src", 1, rotate: rot);
            using var doc = Open(rsrc);
            WatermarkWriter.Apply(doc, PictureSpec(100, WatermarkPlace.Center, new[] { 0 }), _ => true);
            bool sideways = rot is 90 or 270;
            int w = sideways ? 792 : 612, h = sideways ? 612 : 792;
            var box = RedInk(SaveTo(doc, tmp, $"wm-pic-rot{rot}"), w, h);
            Check($"page /Rotate {rot}: the picture is upright and centred on the page as shown", Box(box, w / 2.0, h / 2.0, 144, 72), box?.ToString() ?? "no ink");
        }
        using (var doc = Open(src))
        {
            var s = PictureSpec(100, WatermarkPlace.Center, new[] { 0 });
            s.Opacity = 0.4;
            WatermarkWriter.Apply(doc, s, _ => true);
            string path = SaveTo(doc, tmp, "wm-pic-faint");
            var bgra = PdfiumInterop.RenderPageWithAnnotations(path, 0, 612, 792);
            int i = ((396 * 612) + 306) * 4;
            Check("picture opacity: 40% red over white is pale", bgra is not null && bgra[i + 2] > 200 && bgra[i + 1] > 130 && bgra[i + 1] < 190, bgra is null ? "" : $"{bgra[i + 2]},{bgra[i + 1]},{bgra[i]}");
        }
    }

    private static void PageNumbers(Action<string, bool, string> Check, string tmp)
    {
        string src = MakeDoc(tmp, "wm-num-src", 4);
        using var doc = Open(src);
        var spec = new WatermarkSpec { HasWatermark = false, PageNumbers = true, Pages = new[] { 1, 2 }, NumberPlace = PageNumberPlace.BottomRight };
        var res = WatermarkWriter.Apply(doc, spec, _ => true);
        string path = SaveTo(doc, tmp, "wm-num");
        using var pig = PdfDocument_Pig(path);
        string p2 = pig.GetPage(2).Text, p3 = pig.GetPage(3).Text, p1 = pig.GetPage(1).Text;
        Check("page numbers: Page {n} of {N} reads the page's place in the document", res.Numbered == 2 && res.Watermarks == 0
              && p2.Contains("Page 2 of 4") && p3.Contains("Page 3 of 4") && !p1.Contains("Page ") , $"[{p2}] [{p3}]");
        Check("page numbers: a footer artifact is marked as such", Joined(doc.Pages[1]).Contains("/Subtype /Footer") && Joined(doc.Pages[1]).Contains("/TdpWmKind (numbers)"), "");
        var top = new WatermarkSpec { HasWatermark = false, PageNumbers = true, Pages = new[] { 0 }, NumberPlace = PageNumberPlace.TopLeft, PageNumberFormat = "{n}" };
        using var doc2 = Open(src);
        WatermarkWriter.Apply(doc2, top, _ => true);
        Check("page numbers at the top are a header artifact", Joined(doc2.Pages[0]).Contains("/Subtype /Header"), "");

        // A one-page preview of page 3 of 4 shows the real number.
        using var doc3 = Open(src);
        while (doc3.PageCount > 1) doc3.Pages.RemoveAt(0);
        WatermarkWriter.Apply(doc3, new WatermarkSpec { HasWatermark = false, PageNumbers = true, Pages = new[] { 0 } }, _ => true, _ => (4, 9));
        using var pig3 = PdfDocument_Pig(SaveTo(doc3, tmp, "wm-num-preview"));
        Check("page numbers: a preview of one page can show the real number and total", pig3.GetPage(1).Text.Contains("Page 4 of 9"), pig3.GetPage(1).Text);
    }

    private static void Removal(Action<string, bool, string> Check, string tmp)
    {
        string src = MakeDoc(tmp, "wm-rm-src", 3, compress: true);
        long srcSize = new FileInfo(src).Length;
        using var doc = Open(src);
        var before = doc.Pages.Cast<PdfPage>().Select(p => Joined(p)).ToList();
        var namesBefore = doc.Pages.Cast<PdfPage>().Select(p => string.Join(",", XObjectNames(p))).ToList();
        var countBefore = doc.Pages.Cast<PdfPage>().Select(p => p.Contents.Elements.Count).ToList();

        var behind = new WatermarkSpec { Text = "SECRET", Pages = new[] { 0, 1, 2 }, Layer = WatermarkLayer.Behind, PageNumbers = true };
        var over = PictureSpec(100, WatermarkPlace.Center, new[] { 1 });
        WatermarkWriter.Apply(doc, behind, _ => true);
        WatermarkWriter.Apply(doc, over, _ => true);
        int added = WatermarkWriter.Count(doc);
        Check("count: every block that was written is found", added == 3 + 3 + 1, added.ToString());

        // Save and reopen first: removal has to work on a file TDPdf wrote earlier, not only in memory.
        string mid = SaveTo(doc, tmp, "wm-rm-mid");
        using var reopened = Open(mid);
        Check("count: the blocks are found again after a save and reopen", WatermarkWriter.Count(reopened) == added, WatermarkWriter.Count(reopened).ToString());
        int removed = WatermarkWriter.Remove(reopened);
        Check("removal finds exactly what was added", removed == added && WatermarkWriter.Count(reopened) == 0, $"{removed} of {added}");
        Check("removal leaves each page's content exactly as it was",
              reopened.Pages.Cast<PdfPage>().Select(p => Joined(p)).SequenceEqual(before), "");
        Check("removal leaves each page's stream count and resources as they were",
              reopened.Pages.Cast<PdfPage>().Select(p => p.Contents.Elements.Count).SequenceEqual(countBefore)
              && reopened.Pages.Cast<PdfPage>().Select(p => string.Join(",", XObjectNames(p))).SequenceEqual(namesBefore), "");
        string after = SaveTo(reopened, tmp, "wm-rm-after");
        byte[] bytes = File.ReadAllBytes(after);
        Check("the saved file no longer holds the marks (no TdpWm anywhere, and about the size it was)",
              !Encoding.Latin1.GetString(bytes).Contains("TdpWm") && Math.Abs(bytes.Length - srcSize) < srcSize * 0.25 + 2000, $"{bytes.Length} vs {srcSize}");
        Check("removing again finds nothing", WatermarkWriter.Remove(Open(after)) == 0, "");

        // Somebody else's artifacts and marked content are not ours to remove.
        var other = new PdfDocument();
        var pg = other.AddPage();
        using (var g = XGraphics.FromPdfPage(pg)) g.DrawString("keep me", new XFont("Helvetica", 12), XBrushes.Black, new XPoint(40, 40));
        var extra = pg.Contents.AppendContent();
        extra.CreateStream(Encoding.Latin1.GetBytes("/Artifact <</Type /Pagination /Subtype /Footer>> BDC\n0 0 10 10 re f\nEMC\n/P <</MCID 0>> BDC\nEMC\n"));
        string foreignBefore = Joined(pg);
        WatermarkWriter.Apply(other, new WatermarkSpec { Text = "MINE", Pages = new[] { 0 }, Layer = WatermarkLayer.Over }, _ => true);
        int foreignRemoved = WatermarkWriter.Remove(other);
        Check("removal does not touch someone else's artifacts or marked content", foreignRemoved == 1 && Joined(pg) == foreignBefore, "");

        // A tool that merged the page's streams into one: the block is cut out of it, the rest stays.
        using var merged = Open(mid);
        var mp = merged.Pages[0];
        string all = Joined(mp);
        mp.Elements.Remove("/Contents");
        var one = mp.Contents.AppendContent();
        one.CreateStream(Encoding.Latin1.GetBytes(all));
        Check("a merged content stream still shows the mark", WatermarkWriter.Count(merged) >= 2, WatermarkWriter.Count(merged).ToString());
        WatermarkWriter.Remove(merged);
        string left = Joined(mp);
        Check("a merged content stream: the block is cut out and the page's own text stays",
              !left.Contains("TdpWm") && left.Contains("Original content") == before[0].Contains("Original content") && left.Replace("\n", "").Replace(" ", "").Contains(before[0].Replace("\n", "").Replace(" ", "")), left.Length > 80 ? left[..80] : left);
    }
}
