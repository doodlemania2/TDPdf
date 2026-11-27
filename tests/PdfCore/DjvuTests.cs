using System.IO;
using System.Text;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using TDPdf.Services;

/// <summary>A decoder that makes up pages, so the PDF pipeline is tested without any native code.</summary>
internal sealed class FakeDjvu : IDjvuDecoder
{
    public List<DjvuPageInfo> Pages { get; } = new();
    public Dictionary<int, DjvuRaster> Rasters { get; } = new();
    public Dictionary<int, List<DjvuWord>> Words { get; } = new();
    public HashSet<int> FailRender { get; } = new();
    public List<DjvuOutlineItem> Outline { get; } = new();
    public int PageCount => Pages.Count;
    public DjvuPageInfo GetPageInfo(int i) => Pages[i];
    public DjvuRaster Render(int i, long maxPixels, CancellationToken ct)
    {
        if (FailRender.Contains(i)) throw new InvalidDataException("ddjvu: broken page");
        return Rasters[i];
    }
    public IReadOnlyList<DjvuWord> GetWords(int i) => Words.TryGetValue(i, out var w) ? w : new List<DjvuWord>();
    public IReadOnlyList<DjvuOutlineItem> GetOutline() => Outline;
    public void Dispose() { }
}

/// <summary>
/// DjVu reading (#235): the text and outline parsers (against output captured from DjVuLibre 3.5), the PNM
/// reader, the process launcher's limits, the PDF pipeline with a fake decoder, and, where DjVuLibre is
/// installed, the same pipeline against the real programs.
/// </summary>
internal static class DjvuTests
{
    private static byte[] Pbm(int w, int h, Func<int, int, bool> black)
    {
        int stride = (w + 7) / 8;
        var rows = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (black(x, y)) rows[y * stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
        return rows;
    }

    private static string? Which(string exe)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            string p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        foreach (string dir in new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" })
        {
            string p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("── DjVu books (Services/Djvu*) ─────────────────────────────────");

        // ── s-expressions and text ──────────────────────────────────────────────────────────
        // Shapes below are what djvused 3.5 printed for a two-page book (octal escapes for non-ASCII, quotes and backslashes escaped).
        string page1 = "(page 0 0 300 200\n (line 10 150 200 180\n  (word 10 150 80 180 \"Caf\\303\\251\")\n  (word 90 150 200 180 \"a\\\"b\\\\c \\303\\251x\")))\n";
        var words = DjvuText.ParsePageText(page1, 200);
        Check("djvu text: words come out flipped to top-left coordinates",
            words.Count == 2 && words[0] == new DjvuWord("Café", 10, 20, 80, 50), string.Join("|", words));
        Check("djvu text: octal UTF-8 bytes, quotes and backslashes decode", words.Count == 2 && words[1].Text == "a\"b\\c éx", words.Count == 2 ? words[1].Text : "");
        var coarse = DjvuText.ParsePageText("(page 0 0 300 200 \"just page text\")", 200);
        Check("djvu text: a page with text but no word boxes is one block over the page",
            coarse.Count == 1 && coarse[0] == new DjvuWord("just page text", 0, 0, 300, 200), string.Join("|", coarse));
        var lines = DjvuText.ParsePageText("(page 0 0 300 200 (line 0 100 300 120 \"line one\") (line 0 60 300 80 \"line two\"))", 200);
        Check("djvu text: line zones are used when there are no words", lines.Count == 2 && lines[0].Text == "line one" && lines[0].Top == 80, string.Join("|", lines));
        var withChars = DjvuText.ParsePageText("(page 0 0 100 100 (word 0 0 50 10 \"ab\" (char 0 0 25 10 \"a\") (char 25 0 50 10 \"b\")))", 100);
        Check("djvu text: a word's own letters are not repeated", withChars.Count == 1 && withChars[0].Text == "ab", string.Join("|", withChars));
        Check("djvu text: empty, garbage and unbalanced text give nothing, not an exception",
            DjvuText.ParsePageText("", 100).Count == 0 && DjvuText.ParsePageText("(((", 100).Count == 0 &&
            DjvuText.ParsePageText("not text", 100).Count == 0 && DjvuText.ParsePageText("(page 0 0 10 10 \"unterminated", 100).Count == 0, "");
        Check("djvu text: nesting past the cap is refused, not a stack overflow",
            DjvuText.ParsePageText(new string('(', 100000) + new string(')', 100000), 100).Count == 0, "");
        Check("djvu text: inverted or empty boxes are dropped", DjvuText.ParsePageText("(page 0 0 10 10 (word 5 5 5 9 \"x\"))", 10).Count == 0, "");

        string all = "select; remove-txt\n# ------------------------- \nselect \"a.djvu\" # page 1\nset-txt\n" + page1 +
                     "\n.\n# ------------------------- \nselect \"b.djvu\" # page 2\nset-txt\n(page 0 0 300 200 \"just page text\")\n\n.\n";
        var per = DjvuText.ParseOutputTxt(all, id => id == "a.djvu" ? 0 : id == "b.djvu" ? 1 : null);
        Check("djvu text: output-txt splits into pages by their comments", per.Count == 2 && per[0].Contains("Caf") && per[1].Contains("just page text"), $"{per.Count}");
        var byName = DjvuText.ParseOutputTxt("select \"p7.djvu\"\nset-txt\n(page 0 0 1 1 \"x\")\n.\nselect 3\nset-txt\n(page 0 0 1 1 \"y\")\n.\nselect \"zz\"\nset-txt\n(page 0 0 1 1 \"z\")\n.\n",
            id => id == "p7.djvu" ? 6 : null);
        Check("djvu text: pages found by name, by number, and unknown names skipped", byName.Count == 2 && byName.ContainsKey(6) && byName.ContainsKey(2), string.Join(",", byName.Keys));

        // ── outline ─────────────────────────────────────────────────────────────────────────
        string outline = "(bookmarks\n (\"Chapter \\\"One\\\"\"\n  \"#1\"\n  (\"Sub \\303\\251\"\n   \"#b.djvu\" ) )\n (\"Two\"\n  \"#2\" )\n (\"Rel\" \"#+1\") (\"Web\" \"http://x\") (\"Far\" \"#99\") )";
        var ol = DjvuText.ParseOutline(outline, id => id == "b.djvu" ? 1 : null, 2);
        Check("djvu outline: titles (escapes), nesting, page numbers and page names",
            ol.Count == 5 && ol[0].Title == "Chapter \"One\"" && ol[0].PageIndex == 0 && ol[0].Children.Count == 1 && ol[0].Children[0].Title == "Sub é"
            && ol[0].Children[0].PageIndex == 1 && ol[1].PageIndex == 1, string.Join("|", ol.Select(o => o.Title)));
        Check("djvu outline: relative, external and out-of-range destinations give no page", ol[2].PageIndex is null && ol[3].PageIndex is null && ol[4].PageIndex is null, "");
        Check("djvu outline: nothing or nonsense is an empty outline", DjvuText.ParseOutline("", _ => null, 1).Count == 0 && DjvuText.ParseOutline("(notbookmarks)", _ => null, 1).Count == 0, "");

        // ── listings and PNM ────────────────────────────────────────────────────────────────
        string dump = "  FORM:DJVM [651] \n    DIRM [35]         Document directory (bundled, 2 files 2 pages)\n    FORM:DJVU [157] {a.djvu} [P1]\n      INFO [10]         DjVu 300x200, v24, 300 dpi, gamma=2.2\n      Sjbz [52]         JB2 bilevel data\n    FORM:DJVU [367] {b.djvu} [P2]\n      INFO [10]         DjVu 2550x3300, v24, 150 dpi, gamma=2.2\n";
        var infos = DjvuText.ParseDump(dump);
        Check("djvu dump: size and resolution of each page, in order", infos.Count == 2 && infos[0] == new DjvuPageInfo(300, 200, 300) && infos[1] == new DjvuPageInfo(2550, 3300, 150), string.Join("|", infos));
        var ls = DjvuText.ParseLs("   1 P       90  a.djvu\n   2 P      375  b.djvu\n     I      12  shared.djbz\n");
        Check("djvu ls: page names map to 0-based pages, shared files ignored", ls.Count == 2 && ls["a.djvu"] == 0 && ls["b.djvu"] == 1, string.Join(",", ls.Keys));

        var pbm = new List<byte>(Encoding.ASCII.GetBytes("P4\n# comment\n10 2\n")); pbm.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x40 });
        var r4 = DjvuPnm.Parse(pbm.ToArray());
        Check("djvu pnm: PBM with a comment and 2 bytes per row", r4.Kind == DjvuPageKind.Bitonal && r4.Width == 10 && r4.Height == 2 && r4.Data.Length == 4, "");
        var pgm = new List<byte>(Encoding.ASCII.GetBytes("P5\n3 1\n255\n")); pgm.AddRange(new byte[] { 1, 2, 3 });
        var ppm = new List<byte>(Encoding.ASCII.GetBytes("P6\n1 2\n255\n")); ppm.AddRange(new byte[] { 1, 2, 3, 4, 5, 6 });
        Check("djvu pnm: PGM and PPM", DjvuPnm.Parse(pgm.ToArray()).Kind == DjvuPageKind.Gray && DjvuPnm.Parse(ppm.ToArray()).Data.Length == 6, "");
        void BadPnm(string label, byte[] data)
        {
            try { DjvuPnm.Parse(data); Check(label, false, "accepted"); }
            catch (InvalidDataException) { Check(label, true, ""); }
            catch (Exception ex) { Check(label, false, ex.GetType().Name); }
        }
        BadPnm("djvu pnm hostile: truncated pixels", Encoding.ASCII.GetBytes("P6\n100 100\n255\nxx"));
        BadPnm("djvu pnm hostile: absurd size", Encoding.ASCII.GetBytes("P5\n2000000000 2000000000\n255\n"));
        BadPnm("djvu pnm hostile: not a PNM", Encoding.ASCII.GetBytes("GIF89a"));
        BadPnm("djvu pnm hostile: 16-bit samples", Encoding.ASCII.GetBytes("P5\n1 1\n65535\n\0\0"));
        BadPnm("djvu pnm hostile: empty", Array.Empty<byte>());

        // ── the process launcher ────────────────────────────────────────────────────────────
        if (!OperatingSystem.IsWindows())
        {
            var lim = new DjvuProcessLimits(TimeSpan.FromSeconds(10), 1_000_000, 0);
            var ok = DjvuProcess.Run("/bin/sh", new[] { "-c", "printf hi; printf oops 1>&2; exit 3" }, lim, CancellationToken.None);
            Check("djvu process: exit code, stdout and stderr come back", ok.ExitCode == 3 && Encoding.ASCII.GetString(ok.Stdout) == "hi" && ok.Stderr == "oops", $"{ok.ExitCode}");
            bool timedOut = false; var sw = System.Diagnostics.Stopwatch.StartNew();
            try { DjvuProcess.Run("/bin/sh", new[] { "-c", "sleep 30" }, new DjvuProcessLimits(TimeSpan.FromMilliseconds(500), 1000, 0), CancellationToken.None); }
            catch (TimeoutException) { timedOut = true; }
            Check("djvu process: a hang is killed at the time limit", timedOut && sw.Elapsed < TimeSpan.FromSeconds(8), $"{sw.Elapsed}");
            bool big = false;
            try { DjvuProcess.Run("/bin/sh", new[] { "-c", "head -c 5000000 /dev/zero" }, new DjvuProcessLimits(TimeSpan.FromSeconds(20), 100_000, 0), CancellationToken.None); }
            catch (InvalidDataException) { big = true; }
            Check("djvu process: output past the cap stops the process", big, "");
            using var cts = new CancellationTokenSource(300);
            bool canceled = false; sw.Restart();
            try { DjvuProcess.Run("/bin/sh", new[] { "-c", "sleep 30" }, new DjvuProcessLimits(TimeSpan.FromSeconds(60), 1000, 0), cts.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check("djvu process: cancelling kills the child", canceled && sw.Elapsed < TimeSpan.FromSeconds(8), $"{sw.Elapsed}");
            bool missing = false;
            try { DjvuProcess.Run("/no/such/ddjvu", Array.Empty<string>(), lim, CancellationToken.None); } catch (IOException) { missing = true; }
            Check("djvu process: a missing program is an IOException with a message", missing, "");
            var env = DjvuProcess.Run("/usr/bin/env", Array.Empty<string>(), lim, CancellationToken.None);
            string envText = Encoding.ASCII.GetString(env.Stdout);
            Check("djvu process: the child gets a nearly empty environment", !envText.Contains("HOME=") && !envText.Contains("USER="), envText.Replace('\n', ' '));
        }
        else Console.WriteLine("  (process launcher limits are exercised on macOS and Linux)");

        // ── the PDF pipeline, with a fake decoder ───────────────────────────────────────────
        {
            var fake = new FakeDjvu();
            fake.Pages.Add(new DjvuPageInfo(300, 200, 300));      // bitonal, 1 x 0.667 in -> 72 x 48 pt
            fake.Pages.Add(new DjvuPageInfo(300, 200, 150));      // gray  -> 144 x 96 pt
            fake.Pages.Add(new DjvuPageInfo(300, 200, 100));      // colour -> 216 x 144 pt
            fake.Pages.Add(new DjvuPageInfo(300, 200, 300));      // fails to render
            fake.Rasters[0] = new DjvuRaster(DjvuPageKind.Bitonal, 300, 200, Pbm(300, 200, (x, y) => x < 150));   // left half black
            fake.Rasters[1] = new DjvuRaster(DjvuPageKind.Gray, 300, 200, Enumerable.Range(0, 300 * 200).Select(i => (byte)(i % 300 < 150 ? 0 : 255)).ToArray());
            var rgb = new byte[300 * 200 * 3];
            for (int i = 0; i < 300 * 200; i++) { rgb[3 * i] = 255; rgb[3 * i + 1] = 0; rgb[3 * i + 2] = 0; }
            fake.Rasters[2] = new DjvuRaster(DjvuPageKind.Color, 300, 200, rgb);
            fake.FailRender.Add(3);
            fake.Words[0] = new List<DjvuWord> { new("Hello", 160, 20, 280, 60), new("World", 160, 80, 280, 120) };
            fake.Outline.Add(new DjvuOutlineItem { Title = "Start", PageIndex = 0 });
            var chap = new DjvuOutlineItem { Title = "Middle", PageIndex = 2 };
            chap.Children.Add(new DjvuOutlineItem { Title = "Nowhere", PageIndex = null });
            fake.Outline.Add(chap);

            string outPdf = Path.Combine(tmp, "djvu-fake.pdf");
            var progress = new List<int>();
            var res = DjvuToPdf.Convert(fake, outPdf, null, new Progress<(int Page, int Total)>(p => progress.Add(p.Page)), CancellationToken.None);
            Check("djvu pdf: one PDF page per DjVu page", res.Pages == 4, $"{res.Pages}");
            Check("djvu pdf: a page that will not render stays as a blank page and is named in the notice",
                res.Notices.Any(n => n.Contains("could not be read") && n.Contains("4")), string.Join("|", res.Notices));

            using (var back = PdfReader.Open(outPdf, PdfDocumentOpenMode.Import))
            {
                Check("djvu pdf: page sizes follow each page's resolution",
                    back.PageCount == 4 && Math.Abs(back.Pages[0].Width.Point - 72) < 0.01 && Math.Abs(back.Pages[1].Height.Point - 96) < 0.01
                    && Math.Abs(back.Pages[2].Width.Point - 216) < 0.01, $"{back.Pages[0].Width.Point} {back.Pages[1].Height.Point}");
                string FilterOf(int p)
                {
                    var xo = back.Pages[p].Resources.Elements.GetDictionary("/XObject");
                    var im = xo?.Elements.GetDictionary("/Im0");
                    return im is null ? "none" : (im.Elements.GetName("/Filter") + " " + im.Elements.GetName("/ColorSpace") + " " + im.Elements.GetInteger("/BitsPerComponent"));
                }
                Check("djvu pdf: bitonal is 1-bit Flate, grey and colour are JPEG",
                    FilterOf(0) == "/FlateDecode /DeviceGray 1" && FilterOf(1).StartsWith("/DCTDecode") && FilterOf(2) == "/DCTDecode /DeviceRGB 8", $"{FilterOf(0)} | {FilterOf(1)} | {FilterOf(2)}");
                Check("djvu pdf: outline has the book's bookmarks and a pageless one takes the page before it",
                    back.Outlines.Count == 2 && back.Outlines[0].Title == "Start" && back.Outlines[1].Title == "Middle"
                    && BookTestUtil.PageNo(back, back.Outlines[1].DestinationPage) == 2 && back.Outlines[1].Outlines.Count == 1
                    && BookTestUtil.PageNo(back, back.Outlines[1].Outlines[0].DestinationPage) == 2, "");
            }

            // Look at the pixels: the left half of page 1 must be BLACK (PBM's 1 is black, PDF gray's 1 is white).
            var bmp = TDPdf.Services.PdfiumInterop.RenderPageWithAnnotations(outPdf, 0, 288, 192);
            if (bmp is null) Check("djvu pdf: bitonal polarity", false, "render returned nothing");
            else
            {
                int Lum(int x, int y) { int i = (y * 288 + x) * 4; return (bmp[i] + bmp[i + 1] + bmp[i + 2]) / 3; }
                Check("djvu pdf: PBM black is drawn black, not inverted", Lum(40, 96) < 40 && Lum(250, 150) > 215, $"{Lum(40, 96)} {Lum(250, 150)}");
            }
            var bmp2 = TDPdf.Services.PdfiumInterop.RenderPageWithAnnotations(outPdf, 2, 216, 144);
            if (bmp2 is not null)
            {
                int i = (72 * 216 + 100) * 4;   // BGRA
                Check("djvu pdf: colour page keeps its colour (red)", bmp2[i + 2] > 200 && bmp2[i + 1] < 60 && bmp2[i] < 60, $"{bmp2[i + 2]},{bmp2[i + 1]},{bmp2[i]}");
            }

            // The hidden text: found by a text extractor, and never painted.
            using (var pig = UglyToad.PdfPig.PdfDocument.Open(outPdf))
            {
                var w = pig.GetPage(1).GetWords().ToList();
                Check("djvu pdf: the hidden words are real text a search can find", w.Any(x => x.Text == "Hello") && w.Any(x => x.Text == "World"), string.Join(",", w.Select(x => x.Text)));
                var hello = w.First(x => x.Text == "Hello");
                // Page is 72 x 48 pt for 300 x 200 px: x 160..280 px -> 38.4..67.2 pt; top 20..60 px -> y from the bottom 48 - 14.4 = 33.6 .. 48-4.8
                Check("djvu pdf: the words sit where the page's words are",
                    hello.BoundingBox.Left > 36 && hello.BoundingBox.Right < 70 && hello.BoundingBox.Bottom > 30 && hello.BoundingBox.Top < 46,
                    $"{hello.BoundingBox.Left:0.#},{hello.BoundingBox.Bottom:0.#},{hello.BoundingBox.Right:0.#},{hello.BoundingBox.Top:0.#}");
            }
            using (var re = PdfReader.Open(outPdf, PdfDocumentOpenMode.Import))
            {
                // Text render mode 3 (neither fill nor stroke) in the page's content.
                var contents = re.Pages[0].Contents;
                var sb = new StringBuilder();
                for (int i = 0; i < contents.Elements.Count; i++)
                    if (contents.Elements.GetDictionary(i)?.Stream is { } s) sb.Append(Encoding.Latin1.GetString(s.UnfilteredValue));
                Check("djvu pdf: the text layer is in render mode 3 (invisible)", sb.ToString().Contains("3 Tr"), "");
            }
            Check("djvu pdf: progress is reported for every page and at the end", progress.Count >= 4 && progress.Last() == 4, string.Join(",", progress));

            bool canceled = false;
            using var cts = new CancellationTokenSource(); cts.Cancel();
            try { DjvuToPdf.Convert(fake, Path.Combine(tmp, "djvu-cancel.pdf"), null, null, cts.Token); } catch (OperationCanceledException) { canceled = true; }
            Check("djvu pdf: cancelling writes no file", canceled && !File.Exists(Path.Combine(tmp, "djvu-cancel.pdf")), "");

            // A page whose raster came back sideways gets no text rather than text in the wrong place.
            var turned = new FakeDjvu();
            turned.Pages.Add(new DjvuPageInfo(300, 200, 300));
            turned.Rasters[0] = new DjvuRaster(DjvuPageKind.Bitonal, 200, 300, Pbm(200, 300, (x, y) => false));
            turned.Words[0] = new List<DjvuWord> { new("Side", 10, 10, 60, 30) };
            var tr = DjvuToPdf.Convert(turned, Path.Combine(tmp, "djvu-turned.pdf"), null, null, CancellationToken.None);
            using (var pig = UglyToad.PdfPig.PdfDocument.Open(Path.Combine(tmp, "djvu-turned.pdf")))
                Check("djvu pdf: a turned page is converted without its text and says so", !pig.GetPage(1).GetWords().Any() && tr.Notices.Any(n => n.Contains("turned")), string.Join("|", tr.Notices));

            // A page too big to render whole is stored smaller and the notice says so.
            var small = new FakeDjvu();
            small.Pages.Add(new DjvuPageInfo(1000, 1000, 100));
            small.Rasters[0] = new DjvuRaster(DjvuPageKind.Gray, 100, 100, new byte[100 * 100]);
            var sr = DjvuToPdf.Convert(small, Path.Combine(tmp, "djvu-small.pdf"), null, null, CancellationToken.None);
            Check("djvu pdf: a reduced page keeps its size on paper", sr.Notices.Any(n => n.Contains("lower resolution")), string.Join("|", sr.Notices));
            using (var back = PdfReader.Open(Path.Combine(tmp, "djvu-small.pdf"), PdfDocumentOpenMode.Import))
                Check("djvu pdf: ... 1000 px at 100 dpi is 720 pt wide", Math.Abs(back.Pages[0].Width.Point - 720) < 0.01, $"{back.Pages[0].Width.Point}");

            bool empty = false;
            try { DjvuToPdf.Convert(new FakeDjvu(), Path.Combine(tmp, "djvu-none.pdf"), null, null, CancellationToken.None); } catch (BookConversionException) { empty = true; }
            Check("djvu pdf: a book with no pages is refused", empty, "");
        }

        // ── against the real DjVuLibre, when this machine has it ───────────────────────────
        string? ddjvu = Which("ddjvu"), djvused = Which("djvused");
        string fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "book.djvu");
        if (ddjvu is null || djvused is null || !File.Exists(fixture))
        {
            Console.WriteLine("  SKIP  djvu real tools: DjVuLibre (ddjvu, djvused) not found on this machine; the checks below did not run");
        }
        else
        {
            using var dec = DjvuLibreDecoder.Open(new DjvuTools(ddjvu, djvused), fixture, CancellationToken.None);
            Check("djvu real: page count and sizes from djvused", dec.PageCount == 2 && dec.GetPageInfo(0) == new DjvuPageInfo(300, 200, 300) && dec.GetPageInfo(1).Dpi == 150, "");
            var r0 = dec.Render(0, 100_000_000, CancellationToken.None);
            var r1 = dec.Render(1, 100_000_000, CancellationToken.None);
            Check("djvu real: a bitonal page comes back as 1-bit, a colour page as RGB", r0.Kind == DjvuPageKind.Bitonal && r0.Width == 300 && r1.Kind == DjvuPageKind.Color && r1.Data.Length == 300 * 200 * 3, $"{r0.Kind} {r1.Kind}");
            var rs = dec.Render(0, 150 * 100, CancellationToken.None);
            Check("djvu real: a page over the pixel cap is rendered smaller", rs.Width * rs.Height <= 150 * 100 + 300 && rs.Width < 300, $"{rs.Width}x{rs.Height}");
            var w = dec.GetWords(0);
            Check("djvu real: hidden text with UTF-8 and escapes", w.Count == 2 && w[0].Text == "Café" && w[1].Text == "a\"b\\c éx", string.Join("|", w.Select(x => x.Text)));
            var o = dec.GetOutline();
            Check("djvu real: outline", o.Count == 2 && o[0].Title == "Chapter \"One\"" && o[0].Children[0].Title == "Sub é" && o[0].Children[0].PageIndex == 1 && o[1].PageIndex == 1, string.Join("|", o.Select(x => x.Title)));

            string outPdf = Path.Combine(tmp, "djvu-real.pdf");
            var res = DjvuToPdf.Convert(dec, outPdf, null, null, CancellationToken.None);
            using (var pig = UglyToad.PdfPig.PdfDocument.Open(outPdf))
                Check("djvu real: converts to a PDF with searchable text", res.Pages == 2 && pig.GetPage(1).GetWords().Any(x => x.Text == "Café"), "");

            // A file that is not DjVu is refused with a reason, and a truncated one does not hang or crash anything.
            string junk = Path.Combine(tmp, "junk.djvu");
            File.WriteAllText(junk, "garbage");
            bool refused = false;
            try { DjvuLibreDecoder.Open(new DjvuTools(ddjvu, djvused), junk, CancellationToken.None); } catch (BookConversionException) { refused = true; }
            Check("djvu real: a file that is not DjVu is refused", refused, "");
            string cut = Path.Combine(tmp, "cut.djvu");
            File.WriteAllBytes(cut, File.ReadAllBytes(fixture).Take(150).ToArray());
            bool survived;
            try
            {
                using var d2 = DjvuLibreDecoder.Open(new DjvuTools(ddjvu, djvused), cut, CancellationToken.None);
                var r = DjvuToPdf.Convert(d2, Path.Combine(tmp, "djvu-cut.pdf"), null, null, CancellationToken.None);
                survived = r.Pages >= 1;
            }
            catch (BookConversionException) { survived = true; }
            Check("djvu real: a truncated book either opens with blank pages or is refused, never hangs", survived, "");
        }
    }
}
