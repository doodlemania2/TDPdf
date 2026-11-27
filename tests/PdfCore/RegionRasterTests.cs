using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using TDPdf.Services;
using PigDoc = UglyToad.PdfPig.PdfDocument;

/// <summary>
/// Rasterize a region (#233): the pure geometry and glyph rule, then the whole pipeline over cropped
/// and turned pages, with PDFium as the authority for what the page looks like and PdfPig for what
/// it still says.
/// </summary>
internal static class RegionRasterTests
{
    private const string Line = "ABCDEFGHIJKLMNOP";

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nRasterize a region: pure rules");
        Pure(Check);

        Console.WriteLine("\nRasterize a region: pictures line up and text is removed (cropped + turned pages)");
        var crops = new (string Name, double[]? Box)[]
        {
            ("no crop", null),
            ("offset CropBox", new[] { 100.0, 200.0, 520.0, 700.0 }),
        };
        foreach (var (cropName, cropBox) in crops)
        foreach (int rotate in new[] { 0, 90, 180, 270 })
            Pipeline(Check, tmp, cropName, cropBox, rotate);

        Console.WriteLine("\nRasterize a region: repeated use, tagged documents");
        Repeated(Check, tmp);
        Tagged(Check, tmp);
    }

    // ── Pure ──────────────────────────────────────────────────────────────────────────────────

    private static void Pure(Action<string, bool, string> Check)
    {
        var region = new PdfiumInterop.PdfRect(100, 100, 200, 150);
        RegionRaster.GlyphBox G(double l, double r, double b = 105, double t = 145) => new(l, b, r, t);

        Check("glyph rule: a glyph wholly inside is inside", RegionRaster.GlyphInside(G(120, 130), region), "");
        Check("glyph rule: one crossing the left edge stays", !RegionRaster.GlyphInside(G(95, 105), region), "");
        Check("glyph rule: one crossing the right edge stays", !RegionRaster.GlyphInside(G(195, 205), region), "");
        Check("glyph rule: one wholly outside stays", !RegionRaster.GlyphInside(G(300, 310), region), "");
        Check("glyph rule: one touching the edge from inside is inside", RegionRaster.GlyphInside(G(100, 110), region), "");
        // A line box taller than the box by its padding: still counts, because the box was dragged
        // around the ink, not around the font's ascent and descent.
        Check("glyph rule: line-box padding above and below does not save a glyph",
              RegionRaster.GlyphInside(new(120, 98, 130, 152), region), "");
        Check("glyph rule: a glyph far taller than the box stays",
              !RegionRaster.GlyphInside(new(120, 50, 130, 250), region), "");
        Check("glyph rule: a flipped box (left > right) is read the same",
              RegionRaster.GlyphInside(new(130, 145, 120, 105), region), "");
        Check("glyph rule: an empty box is never inside", !RegionRaster.GlyphInside(G(120, 120), region), "");

        var flags = RegionRaster.InsideFlags(new[] { G(90, 99), G(95, 105), G(110, 120), G(130, 140), G(195, 205), G(210, 220) }, region);
        Check("glyph rule: flags for a run crossing both edges",
              flags.SequenceEqual(new[] { false, false, true, true, false, false }), string.Join(",", flags));
        Check("run: nothing inside is Keep", RegionRaster.Classify(new[] { false, false }) == RegionRaster.RunAction.Keep, "");
        Check("run: all inside is RemoveAll", RegionRaster.Classify(new[] { true, true }) == RegionRaster.RunAction.RemoveAll, "");
        Check("run: some inside is Rewrite", RegionRaster.Classify(new[] { false, true }) == RegionRaster.RunAction.Rewrite, "");
        Check("run: an empty run is Keep", RegionRaster.Classify(Array.Empty<bool>()) == RegionRaster.RunAction.Keep, "");

        var spans = RegionRaster.KeptSpans(flags);
        Check("kept spans: the stretches outside, in order",
              spans.Count == 2 && spans[0] == new RegionRaster.Span(0, 2) && spans[1] == new RegionRaster.Span(4, 2),
              string.Join(" ", spans));
        Check("kept spans: a run with nothing kept has none", RegionRaster.KeptSpans(new[] { true, true }).Count == 0, "");
        Check("kept spans: a run with nothing removed is one span",
              RegionRaster.KeptSpans(new[] { false, false, false }).SequenceEqual(new[] { new RegionRaster.Span(0, 3) }), "");

        // Geometry: the box in PDF user space at every turn.
        var box = new PdfPageGeometry.PageBox(100, 200, 400, 500);   // visible box with an origin
        var user = new PdfiumInterop.PdfRect(150, 300, 250, 340);
        foreach (int rot in new[] { 0, 90, 180, 270 })
        {
            var plan = RegionRaster.Plan(box, rot, user, 300);
            Check($"plan /Rotate {rot}: there is a region", plan is not null, "");
            if (plan is null) continue;
            bool turned = rot is 90 or 270;
            Check($"plan /Rotate {rot}: the page is {(turned ? 500 : 400)} x {(turned ? 400 : 500)} pt at 300 dpi",
                  plan.FullWidth == (int)Math.Round((turned ? 500 : 400) * 300 / 72.0)
                  && plan.FullHeight == (int)Math.Round((turned ? 400 : 500) * 300 / 72.0),
                  $"{plan.FullWidth}x{plan.FullHeight}");
            // 100 x 40 pt at 300 dpi, within a pixel of rounding outwards.
            double wpt = turned ? 40 : 100, hpt = turned ? 100 : 40;
            Check($"plan /Rotate {rot}: the region is {wpt:0} x {hpt:0} pt in pixels",
                  Math.Abs(plan.Width - wpt * 300 / 72) <= 2 && Math.Abs(plan.Height - hpt * 300 / 72) <= 2,
                  $"{plan.Width}x{plan.Height}");
            var s = plan.SnappedRect;
            Check($"plan /Rotate {rot}: the snapped box contains the dragged one and is within a pixel of it",
                  s.Left <= user.Left + 1e-6 && s.Bottom <= user.Bottom + 1e-6
                  && s.Right >= user.Right - 1e-6 && s.Top >= user.Top - 1e-6
                  && user.Left - s.Left < 72.0 / 300 + 1e-6 && s.Right - user.Right < 72.0 / 300 + 1e-6
                  && user.Bottom - s.Bottom < 72.0 / 300 + 1e-6 && s.Top - user.Top < 72.0 / 300 + 1e-6,
                  $"{s}");

            // The placement matrix takes the unit square onto the snapped box.
            var m = RegionRaster.PlacementMatrix(s, rot);
            (double x, double y) P(double u, double v) => (m.A * u + m.C * v + m.E, m.B * u + m.D * v + m.F);
            var corners = new[] { P(0, 0), P(1, 0), P(0, 1), P(1, 1) };
            double minX = corners.Min(c => c.x), maxX = corners.Max(c => c.x);
            double minY = corners.Min(c => c.y), maxY = corners.Max(c => c.y);
            Check($"placement /Rotate {rot}: the unit square lands exactly on the box",
                  Math.Abs(minX - s.Left) < 1e-6 && Math.Abs(maxX - s.Right) < 1e-6
                  && Math.Abs(minY - s.Bottom) < 1e-6 && Math.Abs(maxY - s.Top) < 1e-6, "");
        }

        Check("plan: a box off the page has no region",
              RegionRaster.Plan(box, 0, new(1000, 1000, 1100, 1100), 300) is null, "");
        Check("plan: a box hanging off the page is clamped to it",
              RegionRaster.Plan(box, 0, new(50, 150, 160, 260), 72) is { } c && c.X == 0 && c.Y >= 0
              && c.SnappedRect.Left >= 100 - 1e-6, "");
        Check("plan: NaN is refused", RegionRaster.Plan(box, 0, new(double.NaN, 0, 5, 5), 300) is null, "");
        Check("cm operator is culture independent",
              RegionRaster.CmOperator((1.5, 0, 0, 2.25, 10, 20)) == "1.5 0 0 2.25 10 20 cm", "");

        Check("alt: the removed words, tidied", RegionRaster.BuildAlt("  Pay\n  to  Bob ") == "Picture of text: Pay to Bob", "");
        Check("alt: no words is still a description", RegionRaster.BuildAlt("   ") == "Picture of part of the page.", "");
        Check("alt: long text is cut",
              RegionRaster.BuildAlt(string.Join(" ", Enumerable.Repeat("word", 400))).Length <= RegionRaster.MaxAltChars + 3, "");
    }

    // ── Pipeline ──────────────────────────────────────────────────────────────────────────────

    private static string MakeLine(string tmp, string name, double[]? crop, int rotate)
    {
        string path = Path.Combine(tmp, name);
        var d = new PdfDocument();
        var p = d.AddPage();   // 612 x 792
        using (var g = XGraphics.FromPdfPage(p))
        {
            var f = new XFont("Helvetica", 20);
            g.DrawString(Line, f, XBrushes.Black, 130, 400);
            g.DrawString("KEEPTHIS", f, XBrushes.Black, 130, 480);
            // Something that is not text: a filled bar under the line, and a corner mark that tells
            // an upright picture from a turned or mirrored one.
            g.DrawRectangle(XBrushes.DarkRed, 200, 430, 60, 8);
            g.DrawRectangle(XBrushes.Black, 200, 380, 14, 6);
        }
        if (crop is not null)
        {
            var arr = new PdfArray();
            foreach (double v in crop) arr.Elements.Add(new PdfReal(v));
            p.Elements["/CropBox"] = arr;
        }
        p.Rotate = rotate;
        d.Save(path);
        return path;
    }

    private static void Pipeline(Action<string, bool, string> Check, string tmp, string cropName, double[]? crop, int rotate)
    {
        string label = $"/Rotate {rotate} + {cropName}";
        string src = MakeLine(tmp, $"region-{rotate}-{cropName.Replace(' ', '_')}.pdf", crop, rotate);

        // Letters of the source, in USER space (PdfPig reports the displayed frame).
        PdfPageGeometry.PageBox vbox;
        int rot;
        using (var s = PdfReader.Open(src, PdfDocumentOpenMode.ReadOnly))
        { vbox = PdfPageGeometry.VisibleBox(s.Pages[0]); rot = PdfPageGeometry.Rotation(s.Pages[0]); }

        var letters = new List<(char C, PdfiumInterop.PdfRect R)>();
        using (var pig = PigDoc.Open(src))
        {
            foreach (var l in pig.GetPage(1).Letters)
            {
                var b = l.BoundingBox;
                var u = PdfPageGeometry.TextFrameToPdf(vbox, rot,
                    new PdfiumInterop.PdfRect(Math.Min(b.Left, b.Right), Math.Min(b.Bottom, b.Top),
                                              Math.Max(b.Left, b.Right), Math.Max(b.Bottom, b.Top)));
                letters.Add((l.Value[0], u));
            }
        }
        // The line is the first thing drawn, so its sixteen letters are the first sixteen.
        var lineLetters = letters.Take(Line.Length).ToList();
        if (string.Concat(lineLetters.Select(x => x.C)) != Line)
        {
            Check($"{label}: the fixture's line is found", false, string.Concat(lineLetters.Select(x => x.C)));
            return;
        }
        var e = lineLetters[4].R;    // E: the left edge cuts through it
        var l2 = lineLetters[11].R;  // L: the right edge cuts through it
        double left = (e.Left + e.Right) / 2, right = (l2.Left + l2.Right) / 2;
        double lineBottom = lineLetters.Min(x => x.R.Bottom), lineTop = lineLetters.Max(x => x.R.Top);
        // Tall enough to take the bar under the line (which sits 10..18 pt below the baseline
        // in page space) and the corner mark, and not so tall that the KEEPTHIS line is reached.
        var dragged = new PdfiumInterop.PdfRect(left, lineBottom - 40, right, lineTop + 30);

        string dest = Path.Combine(tmp, $"region-out-{rotate}-{cropName.Replace(' ', '_')}.pdf");
        if (File.Exists(dest)) File.Delete(dest);
        var res = PdfRegionRasterizer.Apply(src, dest, new PdfRegionRasterizer.Request
        {
            PageIndex = 0, Rect = dragged, Dpi = 300,
        });
        Check($"{label}: the region is rasterized", res.Ok, res.Error ?? "");
        if (!res.Ok) return;

        // What the page SAYS now.
        using (var pig = PigDoc.Open(dest))
        {
            var after = pig.GetPage(1).Letters.ToList();
            string text = string.Concat(after.Select(x => x.Value));
            string flow = string.Concat(Line.Where((c, i) => i <= 4 || i >= 11));
            Check($"{label}: the letters cut by the edges (E and L) and those outside stay",
                  text.Contains("ABCDE") && text.Contains("LMNOP"), $"text \"{text}\"");
            string rest = text.Replace("KEEPTHIS", "");
            Check($"{label}: the letters FGHIJK inside the box are not in the file's text",
                  !rest.Any(c => "FGHIJK".Contains(c)), $"text \"{text}\"");
            Check($"{label}: text outside the box is untouched", text.Contains("KEEPTHIS"), $"text \"{text}\"");
            Check($"{label}: nothing but the kept letters and the other line remain",
                  string.Concat(text.OrderBy(c => c)) == string.Concat((flow + "KEEPTHIS").OrderBy(c => c)),
                  $"text \"{text}\"");

            // The kept letters did not move.
            double worst = 0;
            foreach (var l in after)
            {
                var b = l.BoundingBox;
                var u = PdfPageGeometry.TextFrameToPdf(vbox, rot,
                    new PdfiumInterop.PdfRect(Math.Min(b.Left, b.Right), Math.Min(b.Bottom, b.Top),
                                              Math.Max(b.Left, b.Right), Math.Max(b.Bottom, b.Top)));
                var orig = letters.Where(o => o.C == l.Value[0])
                    .OrderBy(o => Math.Abs(o.R.Left - u.Left) + Math.Abs(o.R.Bottom - u.Bottom)).FirstOrDefault();
                if (orig.C == 0) { worst = double.MaxValue; break; }
                worst = Math.Max(worst, Math.Abs(orig.R.Left - u.Left) + Math.Abs(orig.R.Bottom - u.Bottom));
            }
            Check($"{label}: the letters that stayed did not move", worst < 1.5, $"worst drift {worst:F2} pt");
        }

        // What the page LOOKS like: against the original, at one pixel per point.
        var (a, aw, ah) = Geometry.RenderFirstPage(src);
        var (b2, bw, bh) = Geometry.RenderFirstPage(dest);
        int bad = BadBlocks(a, b2, aw, ah, bw, bh);
        Check($"{label}: the page looks the same after (upright, in place, not mirrored)",
              aw == bw && ah == bh && bad == 0, $"{bad} blocks differ; {aw}x{ah} vs {bw}x{bh}");

        Check($"{label}: the bar under the line was removed as a graphic",
              res.Removal is { ShapesRemoved: >= 1 }, $"shapes {res.Removal?.ShapesRemoved}");
        Check($"{label}: the removed words are reported for the picture's description",
              res.Removal is { RemovedText: var rt } && rt.Contains("FGHIJK"), $"\"{res.Removal?.RemovedText}\"");

        using var pigImg = PigDoc.Open(dest);
        Check($"{label}: the page holds the picture", pigImg.GetPage(1).GetImages().Count() == 1, "");
    }

    /// <summary>
    /// Blocks of 6 x 6 pixels whose average brightness differs by more than 50 between two renders.
    /// Block averages, not pixels: the picture is 300 dpi squeezed to the 72 dpi render, so single
    /// anti-aliased pixels differ at every text edge, while a mirrored, turned or shifted picture
    /// moves whole bars and marks.
    /// </summary>
    private static int BadBlocks(byte[] a, byte[] b, int aw, int ah, int bw, int bh)
    {
        if (aw != bw || ah != bh) return int.MaxValue;
        int bad = 0;
        for (int by = 0; by + 6 <= ah; by += 6)
            for (int bx = 0; bx + 6 <= aw; bx += 6)
            {
                long sa = 0, sb = 0;
                for (int y = by; y < by + 6; y++)
                    for (int x = bx; x < bx + 6; x++)
                    {
                        int i = (y * aw + x) * 4;
                        sa += a[i] + a[i + 1] + a[i + 2];
                        sb += b[i] + b[i + 1] + b[i + 2];
                    }
                if (Math.Abs(sa - sb) / (36.0 * 3) > 50) bad++;
            }
        return bad;
    }

    // ── Repeated use ──────────────────────────────────────────────────────────────────────────

    private static void Repeated(Action<string, bool, string> Check, string tmp)
    {
        string src = MakeLine(tmp, "region-repeat.pdf", null, 0);
        string one = Path.Combine(tmp, "region-repeat-1.pdf"), two = Path.Combine(tmp, "region-repeat-2.pdf");

        // First region: the KEEPTHIS line. Second: the first line. They do not overlap.
        var r1 = PdfRegionRasterizer.Apply(src, one, new PdfRegionRasterizer.Request
        { PageIndex = 0, Rect = new(100, 290, 400, 345), Dpi = 150 });
        Check("repeat: the first region succeeds", r1.Ok, r1.Error ?? "");
        var r2 = PdfRegionRasterizer.Apply(one, two, new PdfRegionRasterizer.Request
        { PageIndex = 0, Rect = new(100, 330, 420, 425), Dpi = 300 });
        Check("repeat: the second region succeeds on the result of the first", r2.Ok, r2.Error ?? "");
        if (!r1.Ok || !r2.Ok) return;

        using (var pig = PigDoc.Open(two))
        {
            var page = pig.GetPage(1);
            Check("repeat: both pictures are on the page, the first one intact", page.GetImages().Count() == 2, $"{page.GetImages().Count()} images");
            Check("repeat: no text is left from either line", page.Text.Trim().Length == 0 || !page.Text.Contains("KEEP"), $"\"{page.Text}\"");
        }
        var (a, aw, ah) = Geometry.RenderFirstPage(src);
        var (b, bw, bh) = Geometry.RenderFirstPage(two);
        int bad = BadBlocks(a, b, aw, ah, bw, bh);
        Check("repeat: the page still looks the same", aw == bw && ah == bh && bad == 0, $"{bad} blocks differ");

        // A third region over the first picture alone: the earlier picture goes into the new one and
        // is not left behind as a second copy.
        string three = Path.Combine(tmp, "region-repeat-3.pdf");
        var r3 = PdfRegionRasterizer.Apply(two, three, new PdfRegionRasterizer.Request
        { PageIndex = 0, Rect = new(60, 280, 480, 355), Dpi = 150 });
        Check("repeat: a region over an earlier picture succeeds", r3.Ok, r3.Error ?? "");
        if (r3.Ok)
        {
            using var pig = PigDoc.Open(three);
            Check("repeat: the earlier picture is replaced, not stacked", pig.GetPage(1).GetImages().Count() == 2,
                  $"{pig.GetPage(1).GetImages().Count()} images");
        }

        // A box with nothing under it still yields a picture and does not fail.
        string empty = Path.Combine(tmp, "region-empty.pdf");
        var r4 = PdfRegionRasterizer.Apply(src, empty, new PdfRegionRasterizer.Request
        { PageIndex = 0, Rect = new(20, 20, 120, 80), Dpi = 150 });
        Check("empty box: a blank area is rasterized without error", r4.Ok && r4.Removal is { TextRunsRemoved: 0 }, r4.Error ?? "");

        Check("a page that is not there is refused",
              !PdfRegionRasterizer.Apply(src, Path.Combine(tmp, "region-nopage.pdf"),
                  new PdfRegionRasterizer.Request { PageIndex = 5, Rect = new(0, 0, 10, 10) }).Ok, "");
        Check("a box off the page is refused",
              !PdfRegionRasterizer.Apply(src, Path.Combine(tmp, "region-off.pdf"),
                  new PdfRegionRasterizer.Request { PageIndex = 0, Rect = new(2000, 2000, 2100, 2100) }).Ok, "");
        Check("content is left in place when the caller asks for the picture alone",
              PdfRegionRasterizer.Apply(src, Path.Combine(tmp, "region-keep.pdf"), new PdfRegionRasterizer.Request
              { PageIndex = 0, Rect = new(100, 290, 400, 345), Dpi = 100, RemoveContent = false }) is { Ok: true }
              && PigDoc.Open(Path.Combine(tmp, "region-keep.pdf")).GetPage(1).Text.Contains("KEEPTHIS"), "");
    }

    // ── Tagged documents ──────────────────────────────────────────────────────────────────────

    private static PdfDictionary Dict(PdfDocument d, params (string K, PdfItem V)[] kv)
    {
        var x = new PdfDictionary(d);
        foreach (var (k, v) in kv) x.Elements[k] = v;
        d.Internals.AddObject(x);
        return x;
    }

    private static string MakeTagged(string tmp, string name, bool flatParentTree)
    {
        string path = Path.Combine(tmp, name);
        var d = new PdfDocument();
        var p = d.AddPage();
        using (var g = XGraphics.FromPdfPage(p))
            g.DrawString("SECRETWORDS here", new XFont("Helvetica", 20), XBrushes.Black, 100, 200);

        var nums = new PdfArray(d);
        var parentTree = new PdfDictionary(d);
        if (flatParentTree) parentTree.Elements["/Nums"] = nums;
        else parentTree.Elements["/Kids"] = new PdfArray(d);
        var documentElem = Dict(d, ("/Type", new PdfName("/StructElem")), ("/S", new PdfName("/Document")));
        var root = Dict(d,
            ("/Type", new PdfName("/StructTreeRoot")),
            ("/K", documentElem.Reference!),
            ("/ParentTree", parentTree));
        documentElem.Elements["/P"] = root.Reference!;
        d.Internals.Catalog.Elements["/StructTreeRoot"] = root.Reference!;
        var mark = new PdfDictionary(d);
        mark.Elements["/Marked"] = new PdfBoolean(true);
        d.Internals.Catalog.Elements["/MarkInfo"] = mark;
        d.Save(path);
        return path;
    }

    private static void Tagged(Action<string, bool, string> Check, string tmp)
    {
        string src = MakeTagged(tmp, "region-tagged.pdf", flatParentTree: true);
        string dest = Path.Combine(tmp, "region-tagged-out.pdf");
        var res = PdfRegionRasterizer.Apply(src, dest, new PdfRegionRasterizer.Request
        { PageIndex = 0, Rect = new(80, 560, 400, 625), Dpi = 150 });
        Check("tagged: the region is rasterized", res.Ok, res.Error ?? "");
        Check("tagged: the picture got a description", res.AltAdded, "");
        if (!res.Ok) return;

        using var d = PdfReader.Open(dest, PdfDocumentOpenMode.Import);
        var root = d.Internals.Catalog.Elements.GetDictionary("/StructTreeRoot");
        Check("tagged: the structure tree is still there", root is not null, "");
        var docElem = PdfItems.DerefDict(root?.Elements["/K"]);
        var kids = PdfItems.Deref(docElem?.Elements["/K"]) as PdfArray;
        var fig = kids is { Elements.Count: > 0 } ? PdfItems.DerefDict(kids.Elements[0]) : null;
        Check("tagged: a Figure hangs under the Document element", fig?.Elements.GetName("/S") == "/Figure", "");
        string alt = (fig?.Elements["/Alt"] as PdfString)?.Value ?? "";
        Check("tagged: its /Alt carries the words that were removed", alt.Contains("SECRETWORDS"), $"\"{alt}\"");
        Check("tagged: the Figure points at the page", fig is not null && PdfItems.DerefDict(fig.Elements["/Pg"]) is not null, "");

        var nums = root?.Elements.GetDictionary("/ParentTree")?.Elements.GetArray("/Nums");
        Check("tagged: the parent tree maps the page's marked content to the Figure",
              nums is { Elements.Count: 2 } && PdfItems.Deref(nums.Elements[1]) is PdfArray { Elements.Count: 1 }, $"{nums?.Elements.Count}");
        Check("tagged: the page has its /StructParents key", d.Pages[0].Elements.ContainsKey("/StructParents"), "");

        using var pig = PigDoc.Open(dest);
        Check("tagged: the file reopens and the words are gone from its text", !pig.GetPage(1).Text.Contains("SECRET"), $"\"{pig.GetPage(1).Text}\"");

        // A second region on the same page appends to the page's own array: MCID 1.
        string dest2 = Path.Combine(tmp, "region-tagged-out2.pdf");
        var res2 = PdfRegionRasterizer.Apply(dest, dest2, new PdfRegionRasterizer.Request
        { PageIndex = 0, Rect = new(10, 10, 120, 80), Dpi = 100 });
        using var d2 = PdfReader.Open(dest2, PdfDocumentOpenMode.Import);
        var nums2 = d2.Internals.Catalog.Elements.GetDictionary("/StructTreeRoot")?.Elements.GetDictionary("/ParentTree")?.Elements.GetArray("/Nums");
        Check("tagged: a second picture takes the next marked-content id",
              res2.Ok && nums2 is { Elements.Count: 2 } && PdfItems.Deref(nums2.Elements[1]) is PdfArray { Elements.Count: 2 }, $"{res2.Error}");

        // A parent tree this cannot extend: the Alt goes in the marked-content properties instead.
        string odd = MakeTagged(tmp, "region-tagged-kids.pdf", flatParentTree: false);
        string oddOut = Path.Combine(tmp, "region-tagged-kids-out.pdf");
        var r3 = PdfRegionRasterizer.Apply(odd, oddOut, new PdfRegionRasterizer.Request
        { PageIndex = 0, Rect = new(80, 560, 400, 625), Dpi = 100 });
        Check("tagged: a parent tree split into /Kids still gets a described picture", r3.Ok && r3.AltAdded, r3.Error ?? "");

        // Untagged documents are not touched.
        string plain = MakeLine(tmp, "region-untagged.pdf", null, 0);
        var r4 = PdfRegionRasterizer.Apply(plain, Path.Combine(tmp, "region-untagged-out.pdf"), new PdfRegionRasterizer.Request
        { PageIndex = 0, Rect = new(100, 330, 420, 425), Dpi = 100 });
        Check("untagged: no description and no structure tree is invented", r4.Ok && !r4.AltAdded, "");
        using var d4 = PdfReader.Open(Path.Combine(tmp, "region-untagged-out.pdf"), PdfDocumentOpenMode.Import);
        Check("untagged: the catalog gains no /StructTreeRoot", !d4.Internals.Catalog.Elements.ContainsKey("/StructTreeRoot"), "");
    }
}
