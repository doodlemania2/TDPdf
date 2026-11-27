using PdfSharpCore.Drawing;
using TDPdf.Services;

/// <summary>
/// Editing a whole paragraph (#229): grouping lines into paragraphs from geometry, breaking the
/// reflowed text into lines, carrying highlights onto the new lines, the refuse / ask decision
/// table, and a PDFium round trip that rewrites a real paragraph and reads it back.
/// </summary>
internal static class ParagraphTests
{
    private const string Body =
        "The quick brown fox jumps over the lazy dog while a wizard quietly mixes five dozen jugs of " +
        "liquid for the vexed king. Pack my box with five dozen liquor jugs and send them to the farm " +
        "before nightfall because the weather will turn and nobody wants wet boxes.";

    private static List<string> Wrap(XGraphics gfx, XFont font, string text, double width)
    {
        var lines = new List<string>();
        string cur = "";
        foreach (var word in text.Split(' '))
        {
            string trial = cur.Length == 0 ? word : cur + " " + word;
            if (cur.Length > 0 && gfx.MeasureString(trial, font).Width > width) { lines.Add(cur); cur = word; }
            else cur = trial;
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }

    private static string Make(string path, Action<XGraphics, XFont> draw, double fontSize = 11)
    {
        var doc = new PdfSharpCore.Pdf.PdfDocument();
        var page = doc.AddPage();
        using (var gfx = XGraphics.FromPdfPage(page))
            draw(gfx, new XFont("Helvetica", fontSize));
        doc.Save(path);
        return path;
    }

    private static ParagraphPage Read(string path) =>
        ParagraphPageReader.Read(path, 0) ?? throw new InvalidOperationException("page did not read");

    private static int IndexOf(ParagraphPage page, string startsWith) =>
        page.Lines.Select((l, i) => (l, i)).First(t => t.l.Text.StartsWith(startsWith, StringComparison.Ordinal)).i;

    private static string Joined(ParagraphBlock? b) => b is null ? "<null>" : string.Join("|", b.Lines.Select(l => l.Text));

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nParagraphs (#229)");
        GroupingTests(Check, tmp);
        ReflowTests(Check);
        HighlightTests(Check);
        PolicyTests(Check);
        PaperTests(Check);
        EngineTests(Check, tmp);
    }

    // ── Grouping ──────────────────────────────────────────────────────────────────────────

    private static void GroupingTests(Action<string, bool, string> Check, string tmp)
    {
        // Single column, one paragraph.
        {
            string path = Make(Path.Combine(tmp, "para-single.pdf"), (g, f) =>
            {
                var lines = Wrap(g, f, Body, 260);
                for (int i = 0; i < lines.Count; i++) g.DrawString(lines[i], f, XBrushes.Black, 72, 100 + i * 14);
                g.DrawString("An unrelated heading far below", f, XBrushes.Black, 72, 400);
            });
            var page = Read(path);
            int seed = IndexOf(page, "The quick");
            var b = ParagraphFinder.Find(page.Lines, seed + 2);
            int heading = IndexOf(page, "An unrelated");
            Check("paragraph: a single column groups every line of the block", b is not null && b.Lines.Count == page.Lines.Count - 1, Joined(b));
            Check("paragraph: the line far below is not part of it", b is not null && !b.LineIndices.Contains(heading), "");
            Check("paragraph: seeding on any line gives the same paragraph",
                  b is not null && ParagraphFinder.Find(page.Lines, seed)?.Text == b.Text
                  && ParagraphFinder.Find(page.Lines, seed + b.Lines.Count - 1)?.Text == b.Text, "");
            Check("paragraph: left aligned text is read as Left or Justified",
                  b is not null && b.Alignment is ParagraphAlignment.Left or ParagraphAlignment.Justified, $"{b?.Alignment}");
            Check("paragraph: line spacing is read from the baselines", b is not null && Math.Abs(b.Pitch - 14) < 0.6, $"{b?.Pitch}");
            Check("paragraph: the text reads as one string of words", b is not null && b.Text.StartsWith("The quick brown fox", StringComparison.Ordinal)
                  && b.Text.EndsWith("wet boxes.", StringComparison.Ordinal), b?.Text ?? "");
        }

        // Two paragraphs one blank line apart are two paragraphs.
        {
            string path = Make(Path.Combine(tmp, "para-two.pdf"), (g, f) =>
            {
                var a = Wrap(g, f, Body, 240);
                var c = Wrap(g, f, "Nobody wants wet boxes because the weather will turn. Pack my box with five dozen liquor jugs and send them to the farm.", 240);
                for (int i = 0; i < a.Count; i++) g.DrawString(a[i], f, XBrushes.Black, 72, 100 + i * 14);
                double y2 = 100 + a.Count * 14 + 14 * 2;
                for (int i = 0; i < c.Count; i++) g.DrawString(c[i], f, XBrushes.Black, 72, y2 + i * 14);
            });
            var page = Read(path);
            var first = ParagraphFinder.Find(page.Lines, IndexOf(page, "The quick"));
            var second = ParagraphFinder.Find(page.Lines, IndexOf(page, "Nobody"));
            Check("paragraph: a blank line between blocks ends the paragraph",
                  first is not null && second is not null && first.Lines.Count + second.Lines.Count == page.Lines.Count
                  && !first.LineIndices.Intersect(second.LineIndices).Any(), $"{first?.Lines.Count}+{second?.Lines.Count} of {page.Lines.Count}");
        }

        // Two columns on the same lines: each is its own paragraph and neither crosses the gutter.
        {
            string path = Make(Path.Combine(tmp, "para-columns.pdf"), (g, f) =>
            {
                var left = Wrap(g, f, Body, 200);
                var right = Wrap(g, f, "Nobody wants wet boxes because the weather will turn and the lazy dog quietly jumps over the vexed king. Pack my box.", 200);
                for (int i = 0; i < left.Count; i++) g.DrawString(left[i], f, XBrushes.Black, 60, 100 + i * 14);
                for (int i = 0; i < right.Count; i++) g.DrawString(right[i], f, XBrushes.Black, 330, 100 + i * 14);
            });
            var page = Read(path);
            var l = ParagraphFinder.Find(page.Lines, IndexOf(page, "The quick"));
            var r = ParagraphFinder.Find(page.Lines, IndexOf(page, "Nobody"));
            Check("paragraph: two columns are two paragraphs",
                  l is not null && r is not null && l.Right < r.Left && l.Lines.Count >= 4 && r.Lines.Count >= 3
                  && l.Lines.Count + r.Lines.Count == page.Lines.Count, $"{l?.Lines.Count}/{r?.Lines.Count} of {page.Lines.Count}");
            Check("paragraph: a column keeps its own words", l is not null && !l.Text.Contains("Nobody") && r is not null && !r.Text.Contains("quick"), "");
        }

        // First line indented.
        {
            string path = Make(Path.Combine(tmp, "para-indent.pdf"), (g, f) =>
            {
                var a = Wrap(g, f, Body, 260);
                for (int i = 0; i < a.Count; i++) g.DrawString(a[i], f, XBrushes.Black, i == 0 ? 72 + 24 : 72, 100 + i * 14);
                // The next paragraph begins indented straight after, with no blank line.
                var c = Wrap(g, f, "Nobody wants wet boxes because the weather will turn. Pack my box with five dozen liquor jugs and send them to the farm.", 260);
                for (int i = 0; i < c.Count; i++) g.DrawString(c[i], f, XBrushes.Black, i == 0 ? 72 + 24 : 72, 100 + (a.Count + i) * 14);
            });
            var page = Read(path);
            var first = ParagraphFinder.Find(page.Lines, IndexOf(page, "The quick"));
            var second = ParagraphFinder.Find(page.Lines, IndexOf(page, "Nobody"));
            Check("paragraph: an indented first line starts a new paragraph even with no gap",
                  first is not null && second is not null && first.Lines.Count + second.Lines.Count == page.Lines.Count
                  && !first.Text.Contains("Nobody"), $"{first?.Lines.Count}+{second?.Lines.Count} of {page.Lines.Count}");
            Check("paragraph: the indent is reported", first is not null && Math.Abs(first.FirstLineOffset - 24) < 2, $"{first?.FirstLineOffset}");
            Check("paragraph: the body edge is the column edge", first is not null && Math.Abs(first.ColumnLeft - 72) < 2, $"{first?.ColumnLeft}");
        }

        // Hanging indent: first line at the margin, the rest pushed in.
        {
            string path = Make(Path.Combine(tmp, "para-hanging.pdf"), (g, f) =>
            {
                var a = Wrap(g, f, Body, 240);
                for (int i = 0; i < a.Count; i++) g.DrawString(a[i], f, XBrushes.Black, i == 0 ? 72 : 72 + 28, 100 + i * 14);
            });
            var page = Read(path);
            var b = ParagraphFinder.Find(page.Lines, IndexOf(page, "The quick") + 2);
            Check("paragraph: a hanging indent stays one paragraph", b is not null && b.Lines.Count == page.Lines.Count, $"{b?.Lines.Count} of {page.Lines.Count}");
            Check("paragraph: the hanging first line is reported as an outdent", b is not null && b.FirstLineOffset < -20, $"{b?.FirstLineOffset}");
        }

        // Centred.
        {
            string path = Make(Path.Combine(tmp, "para-centred.pdf"), (g, f) =>
            {
                var a = Wrap(g, f, "The quick brown fox jumps over the lazy dog while a wizard quietly mixes five dozen jugs of liquid for the vexed king.", 220);
                for (int i = 0; i < a.Count; i++)
                {
                    double w = g.MeasureString(a[i], f).Width;
                    g.DrawString(a[i], f, XBrushes.Black, 306 - w / 2, 100 + i * 14);
                }
            });
            var page = Read(path);
            var b = ParagraphFinder.Find(page.Lines, 1);
            Check("paragraph: centred lines are read as Centered", b is not null && b.Alignment == ParagraphAlignment.Centered && b.Lines.Count == page.Lines.Count,
                  $"{b?.Alignment} {b?.Lines.Count}/{page.Lines.Count}");
        }

        // Right aligned.
        {
            string path = Make(Path.Combine(tmp, "para-right.pdf"), (g, f) =>
            {
                var a = Wrap(g, f, Body, 220);
                for (int i = 0; i < a.Count; i++)
                {
                    double w = g.MeasureString(a[i], f).Width;
                    g.DrawString(a[i], f, XBrushes.Black, 500 - w, 100 + i * 14);
                }
            });
            var page = Read(path);
            var b = ParagraphFinder.Find(page.Lines, 1);
            Check("paragraph: lines with a common right edge are read as Right", b is not null && b.Alignment == ParagraphAlignment.Right, $"{b?.Alignment}");
        }

        // A heading in a larger size stays out of the body below it.
        {
            string path = Make(Path.Combine(tmp, "para-heading.pdf"), (g, f) =>
            {
                g.DrawString("The quick brown fox", new XFont("Helvetica", 20), XBrushes.Black, 72, 90);
                var a = Wrap(g, f, Body, 260);
                for (int i = 0; i < a.Count; i++) g.DrawString(a[i], f, XBrushes.Black, 72, 120 + i * 14);
            });
            var page = Read(path);
            var body = ParagraphFinder.Find(page.Lines, 1);
            Check("paragraph: a heading in another size is not swallowed", body is not null && body.Lines.Count == page.Lines.Count - 1
                  && Math.Abs(body.FontSize - 11) < 0.6, $"{body?.Lines.Count} of {page.Lines.Count}");
        }

        // Plain geometry: the finder needs no PDF at all.
        {
            ParagraphLine L(string text, double left, double right, double baseline, double size = 12, string font = "F") =>
                new() { Text = text, Left = left, Right = right, Baseline = baseline, Top = baseline + size * 0.8, Bottom = baseline - size * 0.2, FontSize = size, FontName = font };
            var lines = new List<ParagraphLine>
            {
                L("one", 100, 300, 700), L("two", 100, 300, 686), L("three", 100, 250, 672),
                L("other font", 100, 300, 658, 12, "G"),
            };
            var b = ParagraphFinder.Find(lines, 0);
            Check("paragraph: a different font ends the paragraph", b is not null && b.Lines.Count == 3, Joined(b));
            Check("paragraph: an out-of-range seed finds nothing", ParagraphFinder.Find(lines, 9) is null, "");
            var lone = ParagraphFinder.Find(new List<ParagraphLine> { L("alone", 100, 160, 700) }, 0);
            Check("paragraph: a lone line is a one-line paragraph with no spacing to read", lone is not null && lone.Lines.Count == 1 && lone.Pitch == 0, "");
            var hyph = ParagraphFinder.Find(new List<ParagraphLine> { L("a co-", 100, 300, 700), L("operate now", 100, 300, 686) }, 0);
            Check("paragraph: a hyphen at a line end joins the next line without a space", hyph?.Text == "a co-operate now", hyph?.Text ?? "");
        }
    }

    // ── Reflow ────────────────────────────────────────────────────────────────────────────

    // A monospaced "font": every character is 6 points wide, so the expected breaks are countable.
    private static double Mono(string s) => s.Length * 6.0;

    private static void ReflowTests(Action<string, bool, string> Check)
    {
        var lines = ParagraphReflow.Break("the quick brown fox jumps over the lazy dog", 90, 90, Mono);
        Check("reflow: lines never exceed the width", lines.All(l => Mono(l.Text) <= 90), string.Join("|", lines.Select(l => l.Text)));
        Check("reflow: breaks fall between words, greedily",
              string.Join("|", lines.Select(l => l.Text)) == "the quick brown|fox jumps over|the lazy dog", string.Join("|", lines.Select(l => l.Text)));
        Check("reflow: every character is accounted for by offsets",
              lines.All(l => "the quick brown fox jumps over the lazy dog".Substring(l.Start, l.Length) == l.Text), "");

        var narrow = ParagraphReflow.Break("the quick brown fox", 60, 60, Mono);
        Check("reflow: a narrower column makes more lines", narrow.Count >= 2 && narrow.All(l => Mono(l.Text) <= 60), string.Join("|", narrow.Select(l => l.Text)));

        var hard = ParagraphReflow.Break("first line\nsecond line", 600, 600, Mono);
        Check("reflow: a typed newline is a hard break", hard.Count == 2 && hard[0].Text == "first line" && hard[1].Text == "second line", string.Join("|", hard.Select(l => l.Text)));
        var blank = ParagraphReflow.Break("a\n\nb", 600, 600, Mono);
        Check("reflow: a blank typed line is kept", blank.Count == 3 && blank[1].Text == "", string.Join("|", blank.Select(l => l.Text)));

        var indent = ParagraphReflow.Break("aaaa bbbb cccc dddd", 60, 120, Mono);
        Check("reflow: the first line can be shorter than the rest (an indent)", Mono(indent[0].Text) <= 60 && indent.Skip(1).All(l => Mono(l.Text) <= 120), string.Join("|", indent.Select(l => l.Text)));
        Check("reflow: the indent costs a line when the words no longer fit", indent[0].Text == "aaaa bbbb" && indent[1].Text == "cccc dddd", string.Join("|", indent.Select(l => l.Text)));

        var longWord = ParagraphReflow.Break("ab abcdefghijklmnopqrstuvwxyz cd", 60, 60, Mono);
        Check("reflow: a word wider than the column is split rather than overflowing", longWord.All(l => Mono(l.Text) <= 60), string.Join("|", longWord.Select(l => l.Text)));
        Check("reflow: nothing is lost when a word is split",
              string.Concat(longWord.Select(l => l.Text.Replace(" ", ""))) == "ababcdefghijklmnopqrstuvwxyzcd", string.Join("|", longWord.Select(l => l.Text)));

        Check("reflow: centred lines are offset by half the slack", Math.Abs(ParagraphReflow.LeftOffset(ParagraphAlignment.Centered, 60, 100) - 20) < 1e-9, "");
        Check("reflow: right lines sit at the right edge", Math.Abs(ParagraphReflow.LeftOffset(ParagraphAlignment.Right, 60, 100) - 40) < 1e-9, "");
        Check("reflow: left and justified lines sit at the left edge",
              ParagraphReflow.LeftOffset(ParagraphAlignment.Left, 60, 100) == 0 && ParagraphReflow.LeftOffset(ParagraphAlignment.Justified, 60, 100) == 0, "");
        Check("reflow: a line wider than the column is not pushed left of it", ParagraphReflow.LeftOffset(ParagraphAlignment.Centered, 120, 100) == 0, "");
    }

    // ── Highlights ────────────────────────────────────────────────────────────────────────

    private static void HighlightTests(Action<string, bool, string> Check)
    {
        // Old layout: two lines of 6pt characters, 14pt apart.
        const string oldText = "aaaa bbbb cccc dddd";
        HighlightLine Line(int start, string text, double top) =>
            ParagraphHighlights.LayOut(start, text, 100, top, top + 12, Mono);
        var oldLines = new List<HighlightLine> { Line(0, "aaaa bbbb", 100), Line(10, "cccc dddd", 114) };

        // A box over "bbbb" on the first line: chars 5..9.
        var box = new CanvasBox(100 + 5 * 6, 100, 4 * 6, 12);
        var cov = ParagraphHighlights.CoveredRange(oldLines, box);
        Check("highlight: a box maps back to the characters it covered", cov == (5, 9), $"{cov}");

        // Same text, reflowed narrower: "aaaa|bbbb cccc|dddd" at 60pt wide.
        string same = oldText;
        var newLinesSame = new List<HighlightLine> { Line(0, "aaaa", 200), Line(5, "bbbb cccc", 214), Line(15, "dddd", 228) };
        var r1 = ParagraphHighlights.Remap(oldText, oldLines, same, newLinesSame, new[] { box });
        Check("highlight: unchanged text keeps its highlight on the same words",
              r1[0].Kept && r1[0].Boxes.Count == 1 && Math.Abs(r1[0].Boxes[0].Y - 214) < 1e-6 && Math.Abs(r1[0].Boxes[0].X - 100) < 1e-6
              && Math.Abs(r1[0].Boxes[0].Width - 24) < 1e-6, $"{r1[0].Boxes.Count} {(r1[0].Boxes.Count > 0 ? r1[0].Boxes[0].ToString() : "")}");

        // A highlight over "cccc dddd" that now spans a line break gets one box per line.
        var wide = new CanvasBox(100, 114, 9 * 6, 12);
        var r2 = ParagraphHighlights.Remap(oldText, oldLines, same, newLinesSame, new[] { wide });
        Check("highlight: a highlight that now crosses a line break splits into one box per line",
              r2[0].Kept && r2[0].Boxes.Count == 2, $"{r2[0].Boxes.Count}");

        // Text typed before the highlight shifts it along.
        string longer = "xx " + oldText;
        var newLinesLonger = new List<HighlightLine> { Line(0, "xx aaaa bbbb", 200), Line(13, "cccc dddd", 214) };
        var r3 = ParagraphHighlights.Remap(oldText, oldLines, longer, newLinesLonger, new[] { box });
        Check("highlight: inserting words before it moves it with its words",
              r3[0].Kept && r3[0].Boxes.Count == 1 && Math.Abs(r3[0].Boxes[0].X - (100 + 8 * 6)) < 1e-6, r3[0].Boxes.Count > 0 ? r3[0].Boxes[0].ToString() : "none");

        // The words it covered were deleted: it goes, and says it touched the paragraph.
        string deleted = "aaaa  cccc dddd";
        var newLinesDeleted = new List<HighlightLine> { Line(0, "aaaa  cccc dddd", 200) };
        var r4 = ParagraphHighlights.Remap(oldText, oldLines, deleted, newLinesDeleted, new[] { box });
        Check("highlight: a highlight whose words were deleted is dropped, and reported as touched", !r4[0].Kept && r4[0].Touched, "");

        // A highlight elsewhere on the page is none of the paragraph's business.
        var far = new CanvasBox(400, 500, 50, 12);
        var r5 = ParagraphHighlights.Remap(oldText, oldLines, same, newLinesSame, new[] { far });
        Check("highlight: a highlight off the paragraph is not touched", !r5[0].Touched && !r5[0].Kept, "");

        // Covering the whole paragraph covers the whole of the new text.
        var all = new CanvasBox(100, 100, 9 * 6, 14 + 12);
        string rewritten = "something entirely different now";
        var newLinesRewritten = new List<HighlightLine> { Line(0, "something entirely", 200), Line(19, "different now", 214) };
        var r6 = ParagraphHighlights.Remap(oldText, oldLines, rewritten, newLinesRewritten, new[] { all });
        Check("highlight: a highlight over the whole paragraph covers all of the new text",
              r6[0].Kept && r6[0].Boxes.Count == 2, $"{r6[0].Boxes.Count}");

        // MapRange on its own.
        Check("highlight: a range after an insertion shifts by its length", ParagraphHighlights.MapRange("abc def", "abc xxx def", 4, 7) == (8, 11), $"{ParagraphHighlights.MapRange("abc def", "abc xxx def", 4, 7)}");
        Check("highlight: a range inside replaced text clamps to the replacement",
              ParagraphHighlights.MapRange("abc def ghi", "abc XYZW ghi", 5, 6) is { } m && m.Start >= 4 && m.End <= 8, $"{ParagraphHighlights.MapRange("abc def ghi", "abc XYZW ghi", 5, 6)}");
        Check("highlight: an empty range maps to nothing", ParagraphHighlights.MapRange("abc", "abc", 2, 2) is null, "");
    }

    // ── The decision table ────────────────────────────────────────────────────────────────

    private static void PolicyTests(Action<string, bool, string> Check)
    {
        ParagraphVerdict V(Func<ParagraphFacts> f) => ParagraphEditPolicy.Before(f());
        var clean = new ParagraphFacts();

        Check("policy: a clean paragraph is rewritten in place", ParagraphEditPolicy.Before(clean).Plan == ParagraphPlan.InPlace, "");
        Check("policy: an opaque cover is refused", V(() => new ParagraphFacts { OpaqueCover = true }).Plan == ParagraphPlan.Refuse, "");
        Check("policy: an opaque cover is refused even when the user said not to ask",
              V(() => new ParagraphFacts { OpaqueCover = true, DontAsk = true }).Plan == ParagraphPlan.Refuse, "");
        Check("policy: an opaque cover beats everything else, scans included",
              V(() => new ParagraphFacts { OpaqueCover = true, ScanText = true, PageUnsafe = true }).Plan == ParagraphPlan.Refuse, "");
        Check("policy: a refusal carries its reason", V(() => new ParagraphFacts { OpaqueCover = true }).Reason.Length > 10, "");
        Check("policy: a right-to-left paragraph is refused", V(() => new ParagraphFacts { RightToLeft = true }).Plan == ParagraphPlan.Refuse, "");
        Check("policy: recognised text over a scan takes the scan path", V(() => new ParagraphFacts { ScanText = true }).Plan == ParagraphPlan.ScanOverlay, "");
        Check("policy: the scan path is not asked about", V(() => new ParagraphFacts { ScanText = true, PageUnsafe = true }).Plan == ParagraphPlan.ScanOverlay, "");

        foreach (var (name, facts) in new (string, ParagraphFacts)[]
        {
            ("an unsafe page", new ParagraphFacts { PageUnsafe = true }),
            ("mixed styles", new ParagraphFacts { MixedStyles = true }),
            ("a missing engine", new ParagraphFacts { EngineMissing = true }),
            ("turned text", new ParagraphFacts { Unsupported = true }),
        })
        {
            Check($"policy: {name} asks before covering", ParagraphEditPolicy.Before(facts).Plan == ParagraphPlan.AskThenCover, "");
        }
        Check("policy: with Don't ask again set, an unsafe page covers without asking",
              V(() => new ParagraphFacts { PageUnsafe = true, DontAsk = true }).Plan == ParagraphPlan.Cover, "");
        Check("policy: changing the style in the style bar goes straight to the overlay",
              V(() => new ParagraphFacts { StyleChanged = true }).Plan == ParagraphPlan.Cover, "");

        Check("policy: a font that cannot draw the new text asks first", ParagraphEditPolicy.AfterFailure("no glyph for Z", dontAsk: false, scanText: false).Plan == ParagraphPlan.AskThenCover, "");
        Check("policy: a font that cannot draw the new text covers when told not to ask", ParagraphEditPolicy.AfterFailure("no glyph for Z", dontAsk: true, scanText: false).Plan == ParagraphPlan.Cover, "");
        Check("policy: a failed rewrite under a scan covers without asking (the scan has no font to be asked about)",
              ParagraphEditPolicy.AfterFailure("x", dontAsk: false, scanText: true).Plan == ParagraphPlan.Cover, "");
        Check("policy: the question names the reason", ParagraphEditPolicy.AfterFailure("no glyph for Z", false, false).Reason == "no glyph for Z", "");
    }

    private static void PaperTests(Action<string, bool, string> Check)
    {
        // A 20 x 20 cream page with a dark block in the middle: the ring around the block is cream.
        int w = 20, h = 20, stride = w * 4;
        var px = new byte[h * stride];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool ink = x >= 8 && x <= 11 && y >= 8 && y <= 11;
                int p = y * stride + x * 4;
                px[p] = ink ? (byte)10 : (byte)200;       // B
                px[p + 1] = ink ? (byte)10 : (byte)230;   // G
                px[p + 2] = ink ? (byte)10 : (byte)245;   // R
                px[p + 3] = 255;
            }
        var c = PaperColor.Sample(px, w, h, stride, 8, 8, 11, 11, margin: 3);
        Check("paper: the colour around the text is the paper, not the ink", c == (245, 230, 200), $"{c}");
        var none = PaperColor.Sample(Array.Empty<byte>(), 0, 0, 0, 0, 0, 0, 0);
        Check("paper: an empty page reads as white", none == (255, 255, 255), $"{none}");
    }

    // ── The engine, on real files ─────────────────────────────────────────────────────────

    private static PdfiumInterop.ParagraphRequest RequestFor(ParagraphBlock b, string newText) => new()
    {
        PageIndex = 0,
        Bounds = new PdfiumInterop.PdfRect(b.Left - 4, b.Bottom - 4, b.Right + 4, b.Top + 4),
        NewText = newText,
        FirstLineLeft = b.Lines[0].Left,
        ColumnLeft = b.ColumnLeft,
        ColumnWidth = b.ColumnRight - b.ColumnLeft,
        FirstLineOffset = b.FirstLineOffset,
        Alignment = b.Alignment,
        Pitch = b.Pitch,
    };

    private static string PageText(string path)
    {
        using var d = UglyToad.PdfPig.PdfDocument.Open(path);
        return d.GetPage(1).Text;
    }

    private static void EngineTests(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine($"  (paragraph engine: CanRewriteParagraphs={PdfiumInterop.CanRewriteParagraphs})");
        if (!PdfiumInterop.CanRewriteParagraphs)
        {
            Check("paragraph engine: this PDFium cannot rewrite paragraphs, so the rewrite tests do not run (the app then covers instead)", true, "");
            return;
        }

        string src = Make(Path.Combine(tmp, "para-engine.pdf"), (g, f) =>
        {
            var a = Wrap(g, f, Body, 260);
            for (int i = 0; i < a.Count; i++) g.DrawString(a[i], f, XBrushes.Black, 72, 100 + i * 14);
            g.DrawString("An unrelated heading far below", f, XBrushes.Black, 72, 400);
        });
        var page = Read(src);
        int seed = IndexOf(page, "The quick");
        var block = ParagraphFinder.Find(page.Lines, seed)!;
        var scan = PdfiumInterop.ScanParagraph(src, 0,
            new PdfiumInterop.PdfRect(block.Left - 4, block.Bottom - 4, block.Right + 4, block.Top + 4));
        Check("paragraph engine: the scan finds one text object per line", scan.TextObjects == block.Lines.Count, $"{scan.TextObjects} vs {block.Lines.Count}");
        Check("paragraph engine: a plain paragraph has one style, no cover and is not an OCR layer",
              !scan.MixedStyles && !scan.OpaqueCover && !scan.InvisibleOnly && !scan.Unsupported && !scan.InForm,
              $"mixed={scan.MixedStyles} cover={scan.OpaqueCover} invisible={scan.InvisibleOnly} unsupported={scan.Unsupported} form={scan.InForm}");

        // ── Rewrite: reorder, add a hard break, make it longer ──
        const string newText =
            "pack my box with five dozen liquor jugs. the quick brown fox jumps over the lazy dog while a wizard quietly mixes\n" +
            "nobody wants wet boxes because the weather will turn and the vexed king sends them to the farm before nightfall.";
        string dest = Path.Combine(tmp, "para-engine-out.pdf");
        if (File.Exists(dest)) File.Delete(dest);
        var res = PdfParagraphEdit.Apply(src, dest, RequestFor(block, newText));
        Check("paragraph engine: the rewrite succeeds", res.Ok, res.Error ?? "");
        if (res.Ok)
        {
            var after = Read(dest);
            var nb = ParagraphFinder.Find(after.Lines, IndexOf(after, "pack my box"))!;
            string text = PageText(dest);
            Check("paragraph engine: the new words are in the file", text.Replace("\n", " ").Contains("pack my box with five dozen"), text);
            Check("paragraph engine: the old first line is gone, not covered", !text.Contains("The quick brown fox jumps over the lazy dog while"), text);
            Check("paragraph engine: the heading below was left alone", text.Contains("An unrelated heading far below"), "");
            Check("paragraph engine: the rewrite reflowed to a different number of lines", nb.Lines.Count != block.Lines.Count, $"{block.Lines.Count} -> {nb.Lines.Count}");
            Check("paragraph engine: every new line fits the old column",
                  nb.Lines.All(l => l.Right <= block.ColumnRight + 1.5), $"right edge {nb.Right:F1} vs {block.ColumnRight:F1}");
            Check("paragraph engine: the new lines start at the old left edge", nb.Lines.All(l => Math.Abs(l.Left - block.ColumnLeft) < 1.5), $"{nb.Lines.Min(l => l.Left):F1}..{nb.Lines.Max(l => l.Left):F1}");
            Check("paragraph engine: the first line is where the old first line was", Math.Abs(nb.Lines[0].Baseline - block.Lines[0].Baseline) < 1.0, $"{nb.Lines[0].Baseline:F1} vs {block.Lines[0].Baseline:F1}");
            Check("paragraph engine: line spacing is kept", Math.Abs(nb.Pitch - block.Pitch) < 0.8, $"{nb.Pitch:F1} vs {block.Pitch:F1}");
            Check("paragraph engine: the font size is kept", Math.Abs(nb.FontSize - block.FontSize) < 0.3, $"{nb.FontSize:F1} vs {block.FontSize:F1}");
            Check("paragraph engine: the font is the document's own", nb.Lines.All(l => l.FontName == block.Lines[0].FontName), $"{nb.Lines[0].FontName} vs {block.Lines[0].FontName}");
            Check("paragraph engine: the engine reports where each line went", res.Lines.Count == nb.Lines.Count && res.Lines.All(l => l.Edges.Length == l.Text.Length + 1), $"{res.Lines.Count} vs {nb.Lines.Count}");
        }

        // ── Centre and right alignment survive a rewrite ──
        foreach (var (align, name) in new[] { (ParagraphAlignment.Centered, "centred"), (ParagraphAlignment.Right, "right-aligned") })
        {
            string p = Make(Path.Combine(tmp, $"para-{name}-engine.pdf"), (g, f) =>
            {
                var a = Wrap(g, f, Body, 220);
                for (int i = 0; i < a.Count; i++)
                {
                    double w = g.MeasureString(a[i], f).Width;
                    g.DrawString(a[i], f, XBrushes.Black, align == ParagraphAlignment.Centered ? 306 - w / 2 : 500 - w, 100 + i * 14);
                }
            });
            var pg = Read(p);
            var blk = ParagraphFinder.Find(pg.Lines, 1)!;
            string d = Path.Combine(tmp, $"para-{name}-engine-out.pdf");
            var r = PdfParagraphEdit.Apply(p, d, RequestFor(blk, "the quick brown fox jumps over the lazy dog while a wizard quietly mixes five dozen jugs"));
            Check($"paragraph engine: a {name} paragraph is rewritten", r.Ok, r.Error ?? "");
            if (r.Ok)
            {
                var after = Read(d);
                var nb = ParagraphFinder.Find(after.Lines, 0)!;
                bool kept = align == ParagraphAlignment.Centered
                    ? nb.Lines.All(l => Math.Abs((l.Left + l.Right) / 2 - (blk.ColumnLeft + blk.ColumnRight) / 2) < 3)
                    : nb.Lines.All(l => Math.Abs(l.Right - blk.ColumnRight) < 3);
                Check($"paragraph engine: the {name} alignment is kept", kept, string.Join(" ", nb.Lines.Select(l => $"{l.Left:F0}-{l.Right:F0}")));
            }
        }

        // ── The refusals the engine itself makes ──
        {
            string d = Path.Combine(tmp, "para-engine-refused.pdf");
            if (File.Exists(d)) File.Delete(d);
            var r = PdfParagraphEdit.Apply(src, d, RequestFor(block, "the quick brown fox ZEBRA QUARTZ"));
            Check("paragraph engine: a letter the embedded font cannot have is refused", !r.Ok && r.MissingCharacters.Contains('Z'), r.Error ?? "");
            Check("paragraph engine: a refusal leaves no output file", !File.Exists(d), "");
        }
        {
            string d = Path.Combine(tmp, "para-engine-gone.pdf");
            var req = RequestFor(block, "the quick");
            var elsewhere = new PdfiumInterop.ParagraphRequest
            {
                PageIndex = 0, Bounds = new PdfiumInterop.PdfRect(10, 600, 50, 650), NewText = "x",
                FirstLineLeft = 10, ColumnLeft = 10, ColumnWidth = 40, Pitch = 12,
            };
            var r = PdfParagraphEdit.Apply(src, d, elsewhere);
            Check("paragraph engine: a box with no text in it is not found", !r.Ok && r.Outcome == PdfiumInterop.ParagraphOutcome.NotFound, r.Error ?? "");
            _ = req;
        }

        // ── Covers ──
        {
            string covered = Make(Path.Combine(tmp, "para-covered.pdf"), (g, f) =>
            {
                var a = Wrap(g, f, Body, 260);
                for (int i = 0; i < a.Count; i++) g.DrawString(a[i], f, XBrushes.Black, 72, 100 + i * 14);
                g.DrawRectangle(XBrushes.White, 60, 90, 300, a.Count * 14 + 12);
            });
            var cp = Read(covered);
            var cb = ParagraphFinder.Find(cp.Lines, 0)!;
            var cs = PdfiumInterop.ScanParagraph(covered, 0, new PdfiumInterop.PdfRect(cb.Left - 4, cb.Bottom - 4, cb.Right + 4, cb.Top + 4));
            Check("paragraph engine: an opaque rectangle painted over the text is seen as a cover", cs.OpaqueCover, $"objects={cs.TextObjects}");
            string d = Path.Combine(tmp, "para-covered-out.pdf");
            var r = PdfParagraphEdit.Apply(covered, d, RequestFor(cb, "the quick brown fox"));
            Check("paragraph engine: the rewrite itself also declines to run under a cover", !r.Ok && !File.Exists(d), r.Error ?? "");
        }
        {
            string tinted = Make(Path.Combine(tmp, "para-highlighted.pdf"), (g, f) =>
            {
                var a = Wrap(g, f, Body, 260);
                for (int i = 0; i < a.Count; i++) g.DrawString(a[i], f, XBrushes.Black, 72, 100 + i * 14);
                g.DrawRectangle(new XSolidBrush(XColor.FromArgb(80, 255, 255, 0)), 60, 90, 300, a.Count * 14 + 12);
            });
            var tp = Read(tinted);
            var tb = ParagraphFinder.Find(tp.Lines, 0)!;
            var ts = PdfiumInterop.ScanParagraph(tinted, 0, new PdfiumInterop.PdfRect(tb.Left - 4, tb.Bottom - 4, tb.Right + 4, tb.Top + 4));
            Check("paragraph engine: a see-through highlight painted over the text is not a cover", !ts.OpaqueCover, "");
        }
        {
            string outlined = Make(Path.Combine(tmp, "para-outlined.pdf"), (g, f) =>
            {
                var a = Wrap(g, f, Body, 260);
                for (int i = 0; i < a.Count; i++) g.DrawString(a[i], f, XBrushes.Black, 72, 100 + i * 14);
                g.DrawRectangle(XPens.Black, 60, 90, 300, a.Count * 14 + 12);
            });
            var op = Read(outlined);
            var ob = ParagraphFinder.Find(op.Lines, 0)!;
            var os = PdfiumInterop.ScanParagraph(outlined, 0, new PdfiumInterop.PdfRect(ob.Left - 4, ob.Bottom - 4, ob.Right + 4, ob.Top + 4));
            Check("paragraph engine: a box outline round the text is not a cover", !os.OpaqueCover, "");
        }

        // ── Recognised text over a scan: the layer is invisible (text render mode 3) ──
        {
            string visible = Make(Path.Combine(tmp, "para-ocr-src.pdf"), (g, f) =>
            {
                var a = Wrap(g, f, Body, 260);
                for (int i = 0; i < a.Count; i++) g.DrawString(a[i], f, XBrushes.Black, 72, 100 + i * 14);
            });
            string ocr = Path.Combine(tmp, "para-ocr.pdf");
            {
                // Turn every text object invisible, the way an OCR layer is: "3 Tr" at the start of each text block.
                var doc = PdfSharpCore.Pdf.IO.PdfReader.Open(visible, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);
                foreach (var item in doc.Pages[0].Contents.Elements)
                {
                    var stream = (PdfSharpCore.Pdf.PdfDictionary)((PdfSharpCore.Pdf.Advanced.PdfReference)item).Value;
                    string text = System.Text.Encoding.Latin1.GetString(stream.Stream.UnfilteredValue);
                    stream.Elements.Remove("/Filter");
                    stream.Stream.Value = System.Text.Encoding.Latin1.GetBytes(
                        text.Replace("BT\n", "BT\n3 Tr\n").Replace("BT\r\n", "BT\r\n3 Tr\r\n"));
                }
                doc.Save(ocr);
            }
            var op = Read(ocr);
            var ob = ParagraphFinder.Find(op.Lines, 0);
            Check("paragraph engine: the invisible layer is still found as a paragraph", ob is not null && ob.Lines.Count == 5, $"{ob?.Lines.Count}");
            if (ob is not null)
            {
                var os = PdfiumInterop.ScanParagraph(ocr, 0, new PdfiumInterop.PdfRect(ob.Left - 4, ob.Bottom - 4, ob.Right + 4, ob.Top + 4));
                Check("paragraph engine: text with render mode 3 is reported as an OCR layer", os.InvisibleOnly && os.TextObjects == 5, $"invisible={os.InvisibleOnly} objects={os.TextObjects}");

                var preset = new List<PdfiumInterop.ParagraphPresetLine>
                {
                    new("pack my box with five dozen", ob.Lines[0].Left, ob.Lines[0].Baseline, 11),
                    new("liquor jugs and nobody", ob.Lines[0].Left, ob.Lines[0].Baseline - 14, 11),
                };
                string d = Path.Combine(tmp, "para-ocr-out.pdf");
                if (File.Exists(d)) File.Delete(d);
                var r = PdfParagraphEdit.Apply(ocr, d, new PdfiumInterop.ParagraphRequest
                {
                    PageIndex = 0,
                    Bounds = new PdfiumInterop.PdfRect(ob.Left - 4, ob.Bottom - 4, ob.Right + 4, ob.Top + 4),
                    NewText = "pack my box with five dozen liquor jugs and nobody",
                    Invisible = preset,
                });
                Check("paragraph engine: the invisible layer is rewritten", r.Ok && File.Exists(d), r.Error ?? "");
                if (r.Ok)
                {
                    string text = PageText(d);
                    Check("paragraph engine: the searchable text now says the new words", text.Contains("pack my box with five dozen") && text.Contains("liquor jugs and nobody"), text);
                    Check("paragraph engine: the old recognised words are gone", !text.Contains("quick brown fox"), text);
                    var after = PdfiumInterop.ScanParagraph(d, 0, new PdfiumInterop.PdfRect(ob.Left - 4, ob.Bottom - 20, ob.Right + 4, ob.Top + 4));
                    Check("paragraph engine: the rewritten layer is still invisible", after.TextObjects == 2 && after.InvisibleOnly, $"objects={after.TextObjects} invisible={after.InvisibleOnly}");
                }
            }
        }

        // ── Mixed styles ──
        {
            string mixed = Make(Path.Combine(tmp, "para-mixed.pdf"), (g, f) =>
            {
                var big = new XFont("Helvetica", 15);
                g.DrawString("The quick brown fox jumps over", f, XBrushes.Black, 72, 100);
                g.DrawString("the lazy dog while a wizard", big, XBrushes.Black, 72, 114);
                g.DrawString("quietly mixes five dozen jugs", f, XBrushes.Black, 72, 128);
            });
            var mp = Read(mixed);
            var scanMixed = PdfiumInterop.ScanParagraph(mixed, 0, new PdfiumInterop.PdfRect(
                mp.Lines.Min(l => l.Left) - 4, mp.Lines.Min(l => l.Bottom) - 4, mp.Lines.Max(l => l.Right) + 4, mp.Lines.Max(l => l.Top) + 4));
            Check("paragraph engine: lines in two sizes are reported as mixed styles", scanMixed.TextObjects == 3 && scanMixed.MixedStyles, $"objects={scanMixed.TextObjects} mixed={scanMixed.MixedStyles}");
        }
    }
}
