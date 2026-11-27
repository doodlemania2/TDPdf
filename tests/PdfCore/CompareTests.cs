using PdfSharpCore.Drawing;
using TDPdf.Services;

/// <summary>
/// #239 Compare two PDFs: the word diff, the page pairing, the tile maths of the picture diff, and the
/// whole thing end to end on small generated documents.
/// </summary>
internal static class CompareTests
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nCompare two PDFs (#239)");
        Diff(Check);
        Words(Check);
        Align(Check);
        Raster(Check);
        EndToEnd(Check, tmp);
    }

    // ── Myers ───────────────────────────────────────────────────────────────────────────

    private static int[] Apply(int[] a, int[] b, List<DiffRun> runs)
    {
        // Rebuild b from a and the runs: equal runs copy from a, inserts take from b.
        var outp = new List<int>();
        foreach (var r in runs)
        {
            if (r.Op == DiffOp.Equal) for (int i = 0; i < r.ALength; i++) outp.Add(a[r.AStart + i]);
            else if (r.Op == DiffOp.Insert) for (int i = 0; i < r.BLength; i++) outp.Add(b[r.BStart + i]);
        }
        return outp.ToArray();
    }

    private static int Lcs(int[] a, int[] b)
    {
        var t = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                t[i, j] = a[i] == b[j] ? t[i + 1, j + 1] + 1 : Math.Max(t[i + 1, j], t[i, j + 1]);
        return t[0, 0];
    }

    private static void Diff(Action<string, bool, string> Check)
    {
        var same = MyersDiff.Diff(new[] { 1, 2, 3 }, new[] { 1, 2, 3 }, 100);
        Check("diff: identical sequences are one equal run", same is { Count: 1 } && same[0].Op == DiffOp.Equal && same[0].ALength == 3, "");
        var empty = MyersDiff.Diff(Array.Empty<int>(), Array.Empty<int>(), 10);
        Check("diff: two empty sequences have no runs", empty is { Count: 0 }, "");

        var ins = MyersDiff.Diff(new[] { 1, 2, 4 }, new[] { 1, 2, 3, 4 }, 100)!;
        var h = MyersDiff.Hunks(ins);
        Check("diff: one inserted word is one hunk", h.Count == 1 && h[0].ALength == 0 && h[0].BLength == 1 && h[0].BStart == 2, string.Join(",", h));

        var del = MyersDiff.Hunks(MyersDiff.Diff(new[] { 1, 2, 3, 4 }, new[] { 1, 4 }, 100)!);
        Check("diff: two removed words are one hunk", del.Count == 1 && del[0].ALength == 2 && del[0].BLength == 0 && del[0].AStart == 1, string.Join(",", del));

        var rep = MyersDiff.Hunks(MyersDiff.Diff(new[] { 1, 2, 3 }, new[] { 1, 9, 3 }, 100)!);
        Check("diff: a replaced word is one hunk with both sides", rep.Count == 1 && rep[0].ALength == 1 && rep[0].BLength == 1, string.Join(",", rep));

        var two = MyersDiff.Hunks(MyersDiff.Diff(new[] { 1, 2, 3, 4, 5 }, new[] { 9, 2, 3, 4, 8 }, 100)!);
        Check("diff: two separate edits are two hunks", two.Count == 2, string.Join(",", two));

        Check("diff: nothing in common is cut off by the edit cap",
            MyersDiff.Diff(Enumerable.Range(0, 50).ToArray(), Enumerable.Range(100, 50).ToArray(), 20) is null, "");
        Check("diff: nothing in common within the cap still works",
            MyersDiff.Diff(Enumerable.Range(0, 50).ToArray(), Enumerable.Range(100, 50).ToArray(), 100) is { }, "");

        // Fuzz: the script rebuilds b exactly, and it is minimal (edits = n + m - 2 * LCS).
        var rnd = new Random(239);
        bool allOk = true; string bad = "";
        for (int round = 0; round < 300 && allOk; round++)
        {
            int[] a = Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => rnd.Next(0, 6)).ToArray();
            int[] b = Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => rnd.Next(0, 6)).ToArray();
            var runs = MyersDiff.Diff(a, b, 1000);
            if (runs is null) { allOk = false; bad = "null"; break; }
            int edits = runs.Where(r => r.Op != DiffOp.Equal).Sum(r => r.ALength + r.BLength);
            if (!Apply(a, b, runs).SequenceEqual(b) || edits != a.Length + b.Length - 2 * Lcs(a, b))
            { allOk = false; bad = $"a=[{string.Join(",", a)}] b=[{string.Join(",", b)}]"; }
        }
        Check("diff: 300 random pairs rebuild the target with a minimal script", allOk, bad);

        // A long document with one edit stays cheap: the shared head and tail are never diffed.
        var big = Enumerable.Range(0, 400_000).ToArray();
        var big2 = (int[])big.Clone(); big2[200_000] = -1;
        var bigRuns = MyersDiff.Diff(big, big2, 50);
        Check("diff: 400,000 tokens with one edit", bigRuns is { } && MyersDiff.Hunks(bigRuns).Count == 1, "");
    }

    // ── Words and boxes ─────────────────────────────────────────────────────────────────

    private static CompareWord W(string t, int page, double x, double y, double w = 30, double h = 10) => new(t, page, x, y, w, h);

    private static void Words(Action<string, bool, string> Check)
    {
        Check("words: ligatures fold to letters", PdfCompareText.Token("ﬁnal") == "final", PdfCompareText.Token("ﬁnal"));
        Check("words: soft hyphens and zero-width spaces are ignored", PdfCompareText.Token("ex­am​ple") == "example", "");
        Check("words: case is kept", PdfCompareText.Token("Total") != PdfCompareText.Token("total"), "");

        var a = new[] { W("The", 0, 0, 0), W("quick", 0, 40, 0), W("fox", 0, 80, 0) };
        var b = new[] { W("The", 0, 0, 0), W("slow", 0, 40, 0), W("fox", 0, 80, 0) };
        var hunks = PdfCompareText.Compare(a, b)!;
        Check("words: a changed word is found", hunks.Count == 1 && hunks[0].AStart == 1 && hunks[0].ALength == 1 && hunks[0].BLength == 1, "");

        var lig = new[] { W("ﬁnal", 0, 0, 0) };
        var plain = new[] { W("final", 0, 0, 0) };
        Check("words: a ligature is not a change", PdfCompareText.Compare(lig, plain)!.Count == 0, "");

        // One line, close words: one box. Another line: another box. Another page: another box.
        var line = new[]
        {
            W("a", 0, 10, 100, 20, 10), W("b", 0, 35, 100, 20, 10), W("c", 0, 60, 100, 20, 10),
            W("d", 0, 10, 130, 20, 10),
            W("e", 1, 10, 130, 20, 10),
        };
        var boxes = PdfCompareText.LineBoxes(line, 0, line.Length);
        Check("words: neighbours on a line become one box, new lines and pages new boxes", boxes.Count == 3, $"{boxes.Count}");
        Check("words: the line box spans its words", boxes[0] is { X: 10, W: 70 }, boxes[0].ToString());
        var far = new[] { W("a", 0, 10, 100, 20, 10), W("b", 0, 300, 100, 20, 10) };
        Check("words: words far apart on a line stay apart", PdfCompareText.LineBoxes(far, 0, 2).Count == 2, "");
        Check("words: a bad box is skipped, not thrown on",
            PdfCompareText.LineBoxes(new[] { W("a", 0, double.NaN, 0) }, 0, 1).Count == 0, "");
        Check("words: an excerpt is shortened",
            PdfCompareText.Excerpt(Enumerable.Range(0, 40).Select(i => W("word" + i, 0, 0, 0)).ToArray(), 0, 40, 30).Length <= 30, "");
    }

    // ── Page pairing ────────────────────────────────────────────────────────────────────

    private static PageFingerprint Page(params string[] words) => PdfCompareAlign.Fingerprint(words, null);
    private static PageFingerprint Text(string seed) =>
        PdfCompareAlign.Fingerprint(Enumerable.Range(0, 40).Select(i => seed + i), null);
    private static string Rows(IEnumerable<PagePair> p) =>
        string.Join(" ", p.Select(x => $"{(x.Left?.ToString() ?? "-")}{(x.Kind switch { PairKind.Matched => "=", PairKind.Added => "+", PairKind.Removed => "x", _ => ">" })}{(x.Right?.ToString() ?? "-")}"));

    private static void Align(Action<string, bool, string> Check)
    {
        var a = new[] { Text("a"), Text("b"), Text("c"), Text("d") };
        var rows = PdfCompareAlign.Align(a, a);
        Check("pages: the same document pairs page for page", Rows(rows) == "0=0 1=1 2=2 3=3", Rows(rows));

        var ins = new[] { Text("a"), Text("b"), Text("new"), Text("c"), Text("d") };
        rows = PdfCompareAlign.Align(a, ins);
        Check("pages: an inserted page is Added and the rest still pair", Rows(rows) == "0=0 1=1 -+2 2=3 3=4", Rows(rows));

        var rem = new[] { Text("a"), Text("c"), Text("d") };
        rows = PdfCompareAlign.Align(a, rem);
        Check("pages: a removed page is Removed", Rows(rows) == "0=0 1x- 2=1 3=2", Rows(rows));

        var mov = new[] { Text("a"), Text("c"), Text("d"), Text("b") };
        rows = PdfCompareAlign.Align(a, mov);
        Check("pages: a page moved later is Moved, not removed and added",
            rows.Count(r => r.Kind == PairKind.Moved) == 1 && rows.All(r => r.Kind is PairKind.Matched or PairKind.Moved), Rows(rows));
        Check("pages: the moved page keeps both numbers", rows.Any(r => r.Kind == PairKind.Moved && r.Left == 1 && r.Right == 3), Rows(rows));

        // A page edited a little is still the same page.
        var edit = new[] { Text("a"), PdfCompareAlign.Fingerprint(Enumerable.Range(0, 40).Select(i => i < 30 ? "b" + i : "z" + i), null), Text("c"), Text("d") };
        rows = PdfCompareAlign.Align(a, edit);
        Check("pages: a mostly unchanged page still pairs", Rows(rows) == "0=0 1=1 2=2 3=3", Rows(rows));

        // A page rewritten completely is replaced: one removed, one added.
        var rewrite = new[] { Text("a"), Text("q"), Text("c"), Text("d") };
        rows = PdfCompareAlign.Align(a, rewrite);
        Check("pages: a rewritten page is a removal plus an addition", rows.Count(r => r.Kind == PairKind.Removed) == 1 && rows.Count(r => r.Kind == PairKind.Added) == 1, Rows(rows));

        Check("pages: nothing against something is all Added",
            Rows(PdfCompareAlign.Align(Array.Empty<PageFingerprint>(), a)) == "-+0 -+1 -+2 -+3", "");
        Check("pages: something against nothing is all Removed",
            Rows(PdfCompareAlign.Align(a, Array.Empty<PageFingerprint>())) == "0x- 1x- 2x- 3x-", "");

        // Pages with no text are told apart by a picture.
        byte[] dark = Enumerable.Repeat((byte)20, 256).ToArray(), light = Enumerable.Repeat((byte)240, 256).ToArray();
        var scanA = new[] { new PageFingerprint(Array.Empty<int>(), dark), new PageFingerprint(Array.Empty<int>(), light) };
        var scanB = new[] { new PageFingerprint(Array.Empty<int>(), light) };
        rows = PdfCompareAlign.Align(scanA, scanB);
        Check("pages: textless pages are paired by their thumbnail", Rows(rows) == "0x- 1=0", Rows(rows));
        Check("pages: two blank pages without pictures are the same page",
            PdfCompareAlign.Score(Page(), Page()) > 0, "");
        Check("pages: thumbnail distance is 0 for the same picture and 1 for opposite ones",
            PdfCompareAlign.ThumbnailDistance(dark, dark) == 0 && Math.Abs(PdfCompareAlign.ThumbnailDistance(Enumerable.Repeat((byte)0, 4).ToArray(), Enumerable.Repeat((byte)255, 4).ToArray()) - 1) < 1e-9, "");

        // Text similarity.
        Check("pages: identical word sets score 1", Math.Abs(PdfCompareAlign.TextSimilarity(Text("a"), Text("a")) - 1) < 1e-9, "");
        Check("pages: disjoint word sets score 0", PdfCompareAlign.TextSimilarity(Text("a"), Text("b")) == 0, "");
        Check("pages: word order does not matter to the score", PdfCompareAlign.TextSimilarity(Page("x", "y", "z"), Page("z", "y", "x")) == 1, "");

        var thumb = PdfCompareAlign.Thumbnail16(Enumerable.Repeat((byte)128, 100 * 80 * 4).ToArray(), 100, 80);
        Check("pages: a 16 x 16 thumbnail of a flat picture is flat", thumb.Length == 256 && thumb.All(t => t is >= 126 and <= 130), "");

        // 600 x 600 pages, mostly alike, stay inside the table cap and finish quickly.
        var big = Enumerable.Range(0, 600).Select(i => Text("p" + i)).ToArray();
        var bigB = big.Take(300).Concat(new[] { Text("extra") }).Concat(big.Skip(300)).ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        rows = PdfCompareAlign.Align(big, bigB);
        Check("pages: 600 pages with one inserted page pair in order", rows.Count(r => r.Kind == PairKind.Added) == 1 && rows.Count(r => r.Kind == PairKind.Matched) == 600 && sw.Elapsed.TotalSeconds < 10, $"{sw.ElapsedMilliseconds} ms");
    }

    // ── Picture diff ────────────────────────────────────────────────────────────────────

    private static byte[] Paper(int w, int h) { var b = new byte[w * h * 4]; Array.Fill(b, (byte)255); return b; }
    private static void Ink(byte[] img, int w, int x0, int y0, int x1, int y1, byte v = 0)
    {
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int p = (y * w + x) * 4; img[p] = img[p + 1] = img[p + 2] = v; }
    }

    private static void Raster(Action<string, bool, string> Check)
    {
        Check("tiles: a 100 x 33 image is 7 x 3 tiles", PdfCompareRaster.GridSize(100, 33) == (7, 3), "");
        Check("tiles: an empty image has no tiles", PdfCompareRaster.GridSize(0, 0) == (0, 0), "");

        int w = 200, h = 160;
        var a = Paper(w, h); var b = Paper(w, h);
        Check("tiles: identical pictures have no regions", PdfCompareRaster.Regions(a, b, w, h).Count == 0, "");

        Ink(b, w, 40, 40, 60, 50);
        var one = PdfCompareRaster.Regions(a, b, w, h);
        Check("tiles: one added mark is one region around it", one.Count == 1 && one[0].X <= 40 && one[0].Right >= 60 && one[0].Y <= 40 && one[0].Bottom >= 50, string.Join(";", one));
        Check("tiles: the region stays close to the mark", one.Count == 1 && one[0].W <= 40 && one[0].H <= 32, string.Join(";", one));

        // Noise: two stray pixels and a faint shade do not count.
        var noisy = Paper(w, h);
        Ink(noisy, w, 100, 100, 102, 101);                 // two pixels
        Ink(noisy, w, 20, 20, 60, 60, 235);                 // 20 grey levels: under the threshold
        Check("tiles: anti-aliasing noise is ignored", PdfCompareRaster.Regions(a, noisy, w, h).Count == 0, "");

        // Two marks near each other join; far apart they stay two.
        var c = Paper(w, h);
        Ink(c, w, 20, 20, 40, 30); Ink(c, w, 48, 20, 70, 30);
        Check("tiles: marks a tile apart are one region", PdfCompareRaster.Regions(a, c, w, h).Count == 1, "");
        var d = Paper(w, h);
        Ink(d, w, 10, 10, 30, 20); Ink(d, w, 160, 130, 190, 150);
        Check("tiles: marks far apart are two regions", PdfCompareRaster.Regions(a, d, w, h).Count == 2, "");

        // A busy page does not return hundreds of regions.
        var busy = Paper(800, 800); var empty = Paper(800, 800);
        for (int gy = 0; gy < 800; gy += 48) for (int gx = 0; gx < 800; gx += 48) Ink(busy, 800, gx, gy, gx + 8, gy + 8);
        Check("tiles: a busy page is capped to a manageable number of regions",
            PdfCompareRaster.Regions(empty, busy, 800, 800) is { Count: > 0 and <= PdfCompareRaster.MaxRegions }, "");

        // Regions clip to the image.
        var tiny = Paper(20, 20); var tiny2 = Paper(20, 20); Ink(tiny2, 20, 2, 2, 12, 12);
        var tr = PdfCompareRaster.Regions(tiny, tiny2, 20, 20);
        Check("tiles: a region is clipped to a small image", tr.Count == 1 && tr[0].Right <= 20 && tr[0].Bottom <= 20, string.Join(";", tr));

        // Different page sizes are compared on a white canvas.
        var small = Paper(50, 50);
        var padded = PdfCompareRaster.PadToCanvas(small, 50, 50, 80, 60);
        Check("canvas: the small page sits top left on white", padded.Length == 80 * 60 * 4 && padded.All(x => x == 255), "");
        var pbl = Paper(50, 50); Ink(pbl, 50, 0, 0, 5, 5);
        var pad2 = PdfCompareRaster.PadToCanvas(pbl, 50, 50, 80, 60);
        Check("canvas: the page keeps its place", pad2[0] == 0 && pad2[(10 * 80 + 60) * 4] == 255 && pad2[(55 * 80 + 2) * 4] == 255, "");
        Check("canvas: the same size is returned as it is", ReferenceEquals(small, PdfCompareRaster.PadToCanvas(small, 50, 50, 50, 50)), "");

        // Text that explains a region removes it.
        var regions = new List<PixelRect> { new(0, 0, 100, 20), new(0, 100, 100, 20) };
        var covers = new List<PixelRect> { new(0, 0, 60, 20), new(40, 0, 60, 20) };   // overlapping, together cover the first fully
        var kept = PdfCompareRaster.SubtractCovered(regions, covers);
        Check("covered: a region under text boxes is dropped, the other kept", kept.Count == 1 && kept[0].Y == 100, string.Join(";", kept));
        Check("covered: overlapping covers are not counted twice",
            PdfCompareRaster.SubtractCovered(new[] { new PixelRect(0, 0, 100, 10) }, new[] { new PixelRect(0, 0, 50, 10), new PixelRect(0, 0, 50, 10) }).Count == 1, "");
        Check("covered: a small corner under text keeps the region",
            PdfCompareRaster.SubtractCovered(new[] { new PixelRect(0, 0, 100, 100) }, new[] { new PixelRect(0, 0, 10, 10) }).Count == 1, "");

        // The difference picture colours.
        var left = Paper(4, 1); var right = Paper(4, 1);
        Ink(left, 4, 0, 0, 1, 1); Ink(right, 4, 1, 0, 2, 1); Ink(left, 4, 2, 0, 3, 1); Ink(right, 4, 2, 0, 3, 1);
        var diff = PdfCompareRaster.DifferenceImage(left, right, 4, 1);
        bool leftOnlyRed = diff[2] > diff[0] + 100;                      // pixel 0: R high, B low
        bool rightOnlyBlue = diff[4 + 0] > diff[4 + 2] + 100;            // pixel 1: B high, R low
        bool sharedGrey = diff[8] == diff[10] && diff[8] > 120 && diff[8] < 255;   // pixel 2: grey
        bool paper = diff[12] == 255 && diff[13] == 255 && diff[14] == 255;
        Check("difference image: left-only ink is red, right-only blue, shared grey, paper white", leftOnlyRed && rightOnlyBlue && sharedGrey && paper, $"{diff[0]},{diff[1]},{diff[2]} | {diff[4]},{diff[5]},{diff[6]} | {diff[8]},{diff[9]},{diff[10]}");
    }

    // ── Whole comparisons ───────────────────────────────────────────────────────────────

    private static string Make(string dir, string name, IReadOnlyList<string[]> pages, Action<XGraphics, int>? extra = null)
    {
        string path = Path.Combine(dir, name);
        var doc = new PdfSharpCore.Pdf.PdfDocument();
        for (int i = 0; i < pages.Count; i++)
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            var font = new XFont("Helvetica", 12);
            for (int l = 0; l < pages[i].Length; l++)
                gfx.DrawString(pages[i][l], font, XBrushes.Black, 60, 80 + l * 22);
            extra?.Invoke(gfx, i);
        }
        doc.Save(path);
        return path;
    }

    private static string[] Lines(string seed) =>
        Enumerable.Range(0, 6).Select(l => string.Join(" ", Enumerable.Range(0, 8).Select(w => $"{seed}{l}x{w}"))).ToArray();

    private static void EndToEnd(Action<string, bool, string> Check, string tmp)
    {
        string dir = Path.Combine(tmp, "compare");
        Directory.CreateDirectory(dir);

        var basePages = new List<string[]> { Lines("alpha"), Lines("bravo"), Lines("charlie") };
        string left = Make(dir, "left.pdf", basePages);

        var same = PdfCompareEngine.Analyze(left, left, null, CancellationToken.None);
        Check("compare: a document against itself has no changes", same.Changes.Count == 0 && same.Pairs.All(p => p.Kind == PairKind.Matched) && same.Pairs.Count == 3, $"{same.Changes.Count} changes");

        // One word changed on page 2, a page added at the end.
        var editedLines = (string[])Lines("bravo").Clone();
        editedLines[2] = editedLines[2].Replace("bravo2x3", "bravo2x9");
        var rightPages = new List<string[]> { Lines("alpha"), editedLines, Lines("charlie"), Lines("delta") };
        string right = Make(dir, "right.pdf", rightPages);
        var r = PdfCompareEngine.Analyze(left, right, null, CancellationToken.None);
        var text = r.Changes.Where(c => c.Kind is ChangeKind.TextChanged or ChangeKind.TextAdded or ChangeKind.TextRemoved).ToList();
        Check("compare: one changed word is one text change", text.Count == 1 && text[0].Kind == ChangeKind.TextChanged, string.Join("; ", r.Changes.Select(c => c.Kind + " " + c.Summary)));
        Check("compare: the change names both words", text.Count == 1 && text[0].Summary.Contains("bravo2x3") && text[0].Summary.Contains("bravo2x9"), text.FirstOrDefault()?.Summary ?? "");
        Check("compare: the change is on page 2 on both sides", text.Count == 1 && text[0].LeftBoxes.All(b => b.Page == 1) && text[0].RightBoxes.All(b => b.Page == 1) && text[0].LeftBoxes.Count > 0, "");
        Check("compare: the changed word's box sits on the third line",
            text.Count == 1 && text[0].LeftBoxes.Count == 1 && text[0].LeftBoxes[0].Y > 100 && text[0].LeftBoxes[0].Y < 135, text.Count == 1 && text[0].LeftBoxes.Count > 0 ? text[0].LeftBoxes[0].ToString() : "");
        Check("compare: the extra page is Added, once", r.Changes.Count(c => c.Kind == ChangeKind.PageAdded) == 1 && r.Pairs[^1].Kind == PairKind.Added, "");
        Check("compare: the added page's text is not also reported as text", text.Count == 1, "");

        // A paragraph that flows onto the next page is not a change at the page break.
        var tail = Lines("zulu");
        var flowA = new List<string[]> { new[] { "one two three four five six", "seven eight nine ten eleven twelve" }, new[] { "thirteen fourteen fifteen sixteen seventeen eighteen" }.Concat(tail).ToArray() };
        var flowB = new List<string[]> { new[] { "one two three four five six", "seven eight nine ten eleven twelve", "thirteen fourteen fifteen" }, new[] { "sixteen seventeen eighteen", "nineteen twenty twentyone twentytwo twentythree" }.Concat(tail).ToArray() };
        string fa = Make(dir, "flowa.pdf", flowA), fb = Make(dir, "flowb.pdf", flowB);
        var flow = PdfCompareEngine.Analyze(fa, fb, null, CancellationToken.None);
        var flowText = flow.Changes.Where(c => c.Kind != ChangeKind.Visual).ToList();
        Check("compare: re-flowed words across a page break are one added run, nothing else",
            flowText.Count == 1 && flowText[0].Kind == ChangeKind.TextAdded && flowText[0].Summary.Contains("nineteen"), string.Join("; ", flowText.Select(c => c.Kind + " " + c.Summary)));

        // Removed and moved pages.
        string moved = Make(dir, "moved.pdf", new List<string[]> { Lines("bravo"), Lines("charlie"), Lines("alpha") });
        var mv = PdfCompareEngine.Analyze(left, moved, null, CancellationToken.None);
        Check("compare: a page moved to the end is reported as moved, with no text changes",
            mv.Changes.Count(c => c.Kind == ChangeKind.PageMoved) == 1 && mv.Changes.All(c => c.Kind == ChangeKind.PageMoved), string.Join("; ", mv.Changes.Select(c => c.Kind + " " + c.Summary)));
        string gone = Make(dir, "gone.pdf", new List<string[]> { Lines("alpha"), Lines("charlie") });
        var rm = PdfCompareEngine.Analyze(left, gone, null, CancellationToken.None);
        Check("compare: a missing page is Removed", rm.Changes.Count == 1 && rm.Changes[0].Kind == ChangeKind.PageRemoved && rm.Changes[0].Summary == "Page 2 removed", string.Join("; ", rm.Changes.Select(c => c.Kind + " " + c.Summary)));

        // Cancel.
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            bool cancelled = false;
            try { PdfCompareEngine.Analyze(left, right, null, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check("compare: a cancelled comparison stops", cancelled, "");
        }

        // Progress reaches the end.
        var seen = new List<CompareProgress>();
        PdfCompareEngine.Analyze(left, right, new SyncProgress(seen.Add), CancellationToken.None);
        Check("compare: progress is reported and ends at 1", seen.Count > 2 && seen[^1].Fraction == 1 && seen.Zip(seen.Skip(1)).All(p => p.Second.Fraction >= p.First.Fraction - 1e-9), $"{seen.Count} reports");

        // A drawing only on the right is a visual change; a text change is not also a visual one.
        string drawn = Make(dir, "drawn.pdf", basePages, (g, i) => { if (i == 0) g.DrawRectangle(XBrushes.Black, 300, 400, 120, 60); });
        var vis = PdfCompareEngine.Analyze(left, drawn, null, CancellationToken.None);
        var found = PdfCompareEngine.FindVisual(vis, 0, CancellationToken.None);
        Check("compare: text is the same, so only the picture diff finds the drawing", vis.Changes.Count == 0 && found.Count == 1 && found[0].Kind == ChangeKind.Visual, $"{vis.Changes.Count} text, {found.Count} visual");
        Check("compare: the visual change covers the drawn rectangle (in points, from the top)",
            found.Count == 1 && found[0].RightBoxes[0].X <= 300 && found[0].RightBoxes[0].X + found[0].RightBoxes[0].W >= 420
            && found[0].RightBoxes[0].Y <= 400 && found[0].RightBoxes[0].Y + found[0].RightBoxes[0].H >= 460,
            found.Count == 1 ? found[0].RightBoxes[0].ToString() : "");
        Check("compare: a page with no picture change has no visual change", PdfCompareEngine.FindVisual(vis, 1, CancellationToken.None).Count == 0, "");

        var vis2 = PdfCompareEngine.Analyze(left, right, null, CancellationToken.None);
        var onEdited = PdfCompareEngine.FindVisual(vis2, 1, CancellationToken.None);
        Check("compare: a changed word is a text change only, not also a visual one", onEdited.Count == 0, string.Join(";", onEdited.Select(c => c.RightBoxes[0].ToString())) + " | " + string.Join(";", vis2.BaseChanges.SelectMany(c => c.RightBoxes)));
        Check("compare: an added page has no visual pass", PdfCompareEngine.FindVisual(vis2, 3, CancellationToken.None).Count == 0, "");

        Check("compare: render scale keeps the longest side within the pixel cap",
            PdfCompareEngine.RenderScale(5000, 3000) * 5000 <= PdfCompareEngine.MaxRenderPixels + 0.5 && PdfCompareEngine.RenderScale(612, 792) == PdfCompareEngine.VisualDpi / 72, "");
    }

    private sealed class SyncProgress : IProgress<CompareProgress>
    {
        private readonly Action<CompareProgress> _a;
        public SyncProgress(Action<CompareProgress> a) { _a = a; }
        public void Report(CompareProgress value) => _a(value);
    }
}
