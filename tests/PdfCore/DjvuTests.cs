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

        // ── the wire protocol between TDPdf and its DjVu worker ─────────────────────────────
        {
            var ms = new MemoryStream();
            DjvuWorkerProtocol.WriteFrame(ms, DjvuFrame.Open, new byte[] { 1, 2, 3 });
            ms.Position = 0;
            var (ft, fp) = DjvuWorkerProtocol.ReadFrame(ms, 100);
            Check("djvu protocol: a frame round-trips", ft == DjvuFrame.Open && fp.SequenceEqual(new byte[] { 1, 2, 3 }) && ms.Length == 6 + 3, "");

            var emptyFrame = new MemoryStream();
            DjvuWorkerProtocol.WriteFrame(emptyFrame, DjvuFrame.Quit, ReadOnlySpan<byte>.Empty);
            emptyFrame.Position = 0;
            var (qt, qp) = DjvuWorkerProtocol.ReadFrame(emptyFrame, 0);
            Check("djvu protocol: an empty frame round-trips", qt == DjvuFrame.Quit && qp.Length == 0, "");

            static bool Bad<T>(Func<T> f) where T : notnull
            {
                try { f(); return false; } catch (InvalidDataException) { return true; } catch (EndOfStreamException) { return true; }
            }
            Check("djvu protocol hostile: a frame without the magic byte is refused", Bad(() => DjvuWorkerProtocol.ReadFrame(new MemoryStream(new byte[] { 0x00, 0x4F, 0, 0, 0, 0 }), 100)), "");
            Check("djvu protocol hostile: a length over the cap is refused before anything is read or allocated",
                Bad(() => DjvuWorkerProtocol.ReadFrame(new MemoryStream(new byte[] { 0xD7, 0x50, 0xFF, 0xFF, 0xFF, 0x7F }), 1000)), "");
            Check("djvu protocol hostile: a length of 4 GB is refused", Bad(() => DjvuWorkerProtocol.ReadFrame(new MemoryStream(new byte[] { 0xD7, 0x50, 0xFF, 0xFF, 0xFF, 0xFF }), long.MaxValue)), "");
            Check("djvu protocol hostile: a frame cut short is an end of stream", Bad(() => DjvuWorkerProtocol.ReadFrame(new MemoryStream(new byte[] { 0xD7, 0x50, 9, 0, 0, 0, 1 }), 100)), "");
            Check("djvu protocol hostile: a pipe that ends is an end of stream", Bad(() => DjvuWorkerProtocol.ReadFrame(new MemoryStream(), 100)), "");

            DjvuWorkerProtocol.CheckHello(DjvuFrame.Hello, DjvuWorkerProtocol.EncodeHello());
            Check("djvu protocol: the hello says who and which version", Bad(() => { DjvuWorkerProtocol.CheckHello(DjvuFrame.Hello, new byte[8]); return 0; })
                  && Bad(() => { DjvuWorkerProtocol.CheckHello(DjvuFrame.Open, DjvuWorkerProtocol.EncodeHello()); return 0; })
                  && Bad(() => { var h = DjvuWorkerProtocol.EncodeHello(); h[4] = 99; DjvuWorkerProtocol.CheckHello(DjvuFrame.Hello, h); return 0; }), "");

            var header = new DjvuBookHeader(new[] { new DjvuPageInfo(300, 200, 300), new DjvuPageInfo(2550, 3300, 150) },
                new Dictionary<string, int> { ["a.djvu"] = 0, ["b é.djvu"] = 1 });
            var back = DjvuWorkerProtocol.DecodeOpenReply(DjvuWorkerProtocol.EncodeOpenReply(header));
            Check("djvu protocol: the open reply (page sizes and names) round-trips",
                back.Pages.SequenceEqual(header.Pages) && back.PageIds["a.djvu"] == 0 && back.PageIds["b é.djvu"] == 1 && back.PageIds.Count == 2, "");
            Check("djvu protocol: the open request round-trips a path with non-ASCII characters", DjvuWorkerProtocol.DecodeOpenRequest(DjvuWorkerProtocol.EncodeOpenRequest(@"C:\Bücher\書.djvu")) == @"C:\Bücher\書.djvu", "");

            byte[] ValidOpen() => DjvuWorkerProtocol.EncodeOpenReply(header);
            var zeroPages = new byte[8]; // 0 pages, 0 ids
            var tooMany = new byte[8]; BitConverter.TryWriteBytes(tooMany, DjvuWorkerProtocol.MaxPages + 1);
            var lying = new byte[8]; BitConverter.TryWriteBytes(lying, 50_000);   // says 50000 pages, has none
            var trailing = ValidOpen().Concat(new byte[] { 1 }).ToArray();
            var cutOpen = ValidOpen().Take(20).ToArray();
            var absurdSize = DjvuWorkerProtocol.EncodeOpenReply(new DjvuBookHeader(new[] { new DjvuPageInfo(int.MaxValue, int.MaxValue, 300) }, new Dictionary<string, int>()));
            Check("djvu protocol hostile: an open reply with no pages, too many, a lying count, trailing bytes, a cut-short list or an absurd page is refused",
                Bad(() => DjvuWorkerProtocol.DecodeOpenReply(zeroPages)) && Bad(() => DjvuWorkerProtocol.DecodeOpenReply(tooMany)) &&
                Bad(() => DjvuWorkerProtocol.DecodeOpenReply(lying)) && Bad(() => DjvuWorkerProtocol.DecodeOpenReply(trailing)) &&
                Bad(() => DjvuWorkerProtocol.DecodeOpenReply(cutOpen)) && Bad(() => DjvuWorkerProtocol.DecodeOpenReply(absurdSize)), "");
            Check("djvu protocol hostile: an empty path is refused", Bad(() => DjvuWorkerProtocol.DecodeOpenRequest(Array.Empty<byte>())), "");

            var (pi, pmax) = DjvuWorkerProtocol.DecodePageRequest(DjvuWorkerProtocol.EncodePageRequest(3, 1_000_000), 5);
            Check("djvu protocol: the page request round-trips and its pixel cap is clamped", pi == 3 && pmax == 1_000_000 &&
                DjvuWorkerProtocol.DecodePageRequest(DjvuWorkerProtocol.EncodePageRequest(0, long.MaxValue), 5).MaxPixels == DjvuPnm.MaxPixels, "");
            Check("djvu protocol hostile: a page request out of range or with no pixels is refused",
                Bad(() => DjvuWorkerProtocol.DecodePageRequest(DjvuWorkerProtocol.EncodePageRequest(5, 10), 5)) &&
                Bad(() => DjvuWorkerProtocol.DecodePageRequest(DjvuWorkerProtocol.EncodePageRequest(-1, 10), 5)) &&
                Bad(() => DjvuWorkerProtocol.DecodePageRequest(DjvuWorkerProtocol.EncodePageRequest(0, 0), 5)), "");
            Check("djvu protocol: the text request round-trips and is range checked", DjvuWorkerProtocol.DecodeTextRequest(DjvuWorkerProtocol.EncodeTextRequest(4), 5) == 4 &&
                Bad(() => DjvuWorkerProtocol.DecodeTextRequest(DjvuWorkerProtocol.EncodeTextRequest(5), 5)), "");

            foreach (var (kind, w, h) in new[] { (DjvuPageKind.Bitonal, 10, 3), (DjvuPageKind.Gray, 7, 2), (DjvuPageKind.Color, 5, 4) })
            {
                var data = new byte[DjvuWorkerProtocol.RasterBytes(kind, w, h)];
                for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 7 + 1);
                var r = DjvuWorkerProtocol.DecodePageReply(DjvuWorkerProtocol.EncodePageReply(new DjvuRaster(kind, w, h, data)));
                Check($"djvu protocol: a {kind} page picture round-trips", r.Kind == kind && r.Width == w && r.Height == h && r.Data.SequenceEqual(data), "");
                var streamed = new MemoryStream();
                DjvuWorkerProtocol.WritePageReply(streamed, kind, w, h, data);
                streamed.Position = 0;
                var (sft, sfp) = DjvuWorkerProtocol.ReadFrame(streamed, 10_000);
                Check($"djvu protocol: a {kind} picture written straight from its buffer reads back the same", sft == DjvuFrame.Page && DjvuWorkerProtocol.DecodePageReply(sfp).Data.SequenceEqual(data), "");
            }
            var okPic = DjvuWorkerProtocol.EncodePageReply(new DjvuRaster(DjvuPageKind.Gray, 2, 2, new byte[4]));
            var shortPic = okPic.Take(okPic.Length - 1).ToArray();
            var bigPic = okPic.Concat(new byte[] { 0 }).ToArray();
            var badKind = (byte[])okPic.Clone(); badKind[0] = 9;
            var huge = (byte[])okPic.Clone(); BitConverter.TryWriteBytes(huge.AsSpan(1), 100_000); BitConverter.TryWriteBytes(huge.AsSpan(5), 100_000);
            var neg = (byte[])okPic.Clone(); BitConverter.TryWriteBytes(neg.AsSpan(1), -4);
            Check("djvu protocol hostile: a picture that is short, long, of an unknown kind, absurdly large or of negative size is refused",
                Bad(() => DjvuWorkerProtocol.DecodePageReply(shortPic)) && Bad(() => DjvuWorkerProtocol.DecodePageReply(bigPic)) && Bad(() => DjvuWorkerProtocol.DecodePageReply(badKind)) &&
                Bad(() => DjvuWorkerProtocol.DecodePageReply(huge)) && Bad(() => DjvuWorkerProtocol.DecodePageReply(neg)) && Bad(() => DjvuWorkerProtocol.DecodePageReply(new byte[3])), "");

            string reason = DjvuWorkerProtocol.DecodeError(Encoding.UTF8.GetBytes("bad\u0007 thing\r\nsecond line"));
            Check("djvu protocol: an error reason has its control characters taken out and is bounded",
                reason == "bad  thing  second line" && DjvuWorkerProtocol.DecodeError(Encoding.UTF8.GetBytes(new string('x', 5000))).Length == DjvuWorkerProtocol.MaxErrorChars
                && DjvuWorkerProtocol.EncodeError(new string('y', 5000)).Length == DjvuWorkerProtocol.MaxErrorChars, reason);

            // What the worker sends for hidden text is the library's own printout, UTF-8 bytes; the parent's parsers read it as before.
            byte[] sexpBytes = Encoding.UTF8.GetBytes("(page 0 0 300 200 (word 10 150 80 180 \"Café\"))");
            var wordsFromWorker = DjvuText.ParsePageText(DjvuWorkerProtocol.DecodeSexp(sexpBytes), 200);
            Check("djvu protocol: text printed by the library as UTF-8 reads back with its accents", wordsFromWorker.Count == 1 && wordsFromWorker[0].Text == "Café", string.Join("|", wordsFromWorker));
        }

        // ── the worker's command line ───────────────────────────────────────────────────────
        Check("djvu worker launch: exactly '--djvu-worker 1' is a worker launch", DjvuWorkerProtocol.IsWorkerLaunch(new[] { "--djvu-worker", "1" }) &&
              DjvuWorkerProtocol.IsWorkerLaunch(DjvuWorkerProtocol.WorkerArguments()), "");
        Check("djvu worker launch: anything else is the normal app (no arguments, a file, another version, extra arguments, wrong case, a typo)",
              !DjvuWorkerProtocol.IsWorkerLaunch(Array.Empty<string>()) && !DjvuWorkerProtocol.IsWorkerLaunch(new[] { "--djvu-worker" }) &&
              !DjvuWorkerProtocol.IsWorkerLaunch(new[] { "--djvu-worker", "2" }) && !DjvuWorkerProtocol.IsWorkerLaunch(new[] { "--djvu-worker", "1", "x" }) &&
              !DjvuWorkerProtocol.IsWorkerLaunch(new[] { "--DJVU-WORKER", "1" }) && !DjvuWorkerProtocol.IsWorkerLaunch(new[] { "--djvu-worker=1" }) &&
              !DjvuWorkerProtocol.IsWorkerLaunch(new[] { "book.djvu", "1" }) && !DjvuWorkerProtocol.IsWorkerLaunch(new[] { "1", "--djvu-worker" }), "");

        // ── the bundled DLLs: pinned, the right set, nothing else, and self-contained ──────
        {
            string? root = AppContext.BaseDirectory;
            while (root is not null && !File.Exists(Path.Combine(root, "TDPdf.csproj"))) root = Path.GetDirectoryName(root);
            string dir = root is null ? "" : Path.Combine(root, "third_party", "djvulibre");
            string win = Path.Combine(dir, "win-x64");
            if (root is null || !Directory.Exists(win)) Check("djvu bundle: third_party/djvulibre/win-x64 is in the source tree", false, "not found");
            else
            {
                var files = Directory.GetFiles(win).Select(Path.GetFileName).Select(f => f!).OrderBy(f => f, StringComparer.Ordinal).ToList();
                Check("djvu bundle: the folder holds exactly the five library DLLs, no programs",
                    files.SequenceEqual(DjvuNativeBootstrap.LibraryNames.OrderBy(f => f, StringComparer.Ordinal)) && !files.Any(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)), string.Join(",", files));
                var pins = File.ReadAllLines(Path.Combine(dir, "SHA256SUMS")).Where(l => l.Trim().Length > 0)
                    .Select(l => (Hash: l[..64], Name: l[66..])).ToDictionary(p => p.Name, p => p.Hash, StringComparer.Ordinal);
                bool pinned = pins.Count == files.Count && files.All(f => pins.TryGetValue(f, out var h) &&
                    string.Equals(h, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(win, f)))), StringComparison.OrdinalIgnoreCase));
                Check("djvu bundle: every file matches its SHA-256 in SHA256SUMS, and nothing else is pinned", pinned, "");

                // The import closure: every DLL a bundled DLL imports is another bundled DLL or part of Windows.
                var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
                var problems = new List<string>();
                foreach (string f in files)
                {
                    var pe = PeImports.Read(File.ReadAllBytes(Path.Combine(win, f)));
                    if (pe.Machine != 0x8664) problems.Add(f + " is not x64");
                    foreach (string imp in pe.Imports)
                        if (!set.Contains(imp) && !PeImports.IsWindowsSystemLibrary(imp)) problems.Add($"{f} imports {imp}");
                }
                Check("djvu bundle: the DLLs are x64 and import only each other and Windows system libraries", problems.Count == 0, string.Join("; ", problems));
                var main = PeImports.Read(File.ReadAllBytes(Path.Combine(win, DjvuNativeBootstrap.MainLibrary)));
                Check("djvu bundle: the library's own imports include the runtime DLLs the bundle carries", main.Imports.Contains("libjpeg-8.dll", StringComparer.OrdinalIgnoreCase) &&
                      main.Imports.Contains("libstdc++-6.dll", StringComparer.OrdinalIgnoreCase) && main.Imports.Contains("libgcc_s_seh-1.dll", StringComparer.OrdinalIgnoreCase), string.Join(",", main.Imports));
                var lic = Path.Combine(dir, "licenses");
                Check("djvu bundle: SOURCES.md and the licence texts are present",
                    File.Exists(Path.Combine(dir, "SOURCES.md")) && new[] { "djvulibre/COPYING", "libjpeg-turbo/LICENSE.md", "gcc-runtime/COPYING3", "gcc-runtime/COPYING.RUNTIME", "winpthreads/COPYING" }
                        .All(p => File.Exists(Path.Combine(lic, p))), "");
                string sources = File.ReadAllText(Path.Combine(dir, "SOURCES.md"));
                Check("djvu bundle: SOURCES.md names every bundled DLL", DjvuNativeBootstrap.LibraryNames.All(n => sources.Contains(n, StringComparison.Ordinal)), "");
            }
            var hostile = new byte[] { (byte)'M', (byte)'Z', 0, 0 };
            bool refused = false;
            try { PeImports.Read(hostile); } catch (InvalidDataException) { refused = true; }
            Check("djvu bundle: the import reader refuses a file that is not a PE image", refused, "");
        }

        // ── the worker process: a stand-in for TDPdf.exe --djvu-worker (this program, --fake-djvu-worker) ──
        {
            DjvuWorkerLaunch Fake(string mode)
            {
                string exe = Environment.ProcessPath!;
                var args = new List<string>();
                if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) args.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
                args.Add("--fake-djvu-worker"); args.Add(mode);
                return new DjvuWorkerLaunch(exe, args, TestHostEnvironment());
            }
            var quick = new DjvuProcessLimits(TimeSpan.FromSeconds(30), 10_000_000, 0);
            var hangs = new DjvuProcessLimits(TimeSpan.FromMilliseconds(1500), 10_000_000, 0);
            var capped = new DjvuProcessLimits(TimeSpan.FromSeconds(30), 2_000, 0);

            using (var d = DjvuLibreDecoder.Open(Fake("ok"), "/books/a.djvu", CancellationToken.None, quick, quick))
            {
                Check("djvu worker: pages, sizes and names come from the worker", d.PageCount == 3 && d.GetPageInfo(0) == new DjvuPageInfo(160, 40, 300) && d.GetPageInfo(2).Dpi == 100, "");
                var bit = d.Render(0, 1_000_000, CancellationToken.None);
                var col = d.Render(1, 1_000_000, CancellationToken.None);
                Check("djvu worker: a page renders (bitonal and colour)", bit.Kind == DjvuPageKind.Bitonal && bit.Width == 160 && bit.Data.Length == 20 * 40 && bit.Data[0] == 0xAA &&
                      col.Kind == DjvuPageKind.Color && col.Data.Length == 160 * 40 * 3 && col.Data[0] == 200, $"{bit.Kind} {col.Kind}");
                var w = d.GetWords(0);
                Check("djvu worker: the hidden text arrives and parses", w.Count == 1 && w[0].Text == "Café" && w[0].Top == 10, string.Join("|", w));
                var o = d.GetOutline();
                Check("djvu worker: the outline arrives, with a page name resolved to its page", o.Count == 2 && o[0].Title == "Start" && o[0].PageIndex == 0 && o[1].PageIndex == 2, string.Join("|", o.Select(x => x.Title)));
                bool outOfRange = false;
                try { d.Render(3, 1000, CancellationToken.None); } catch (InvalidDataException) { outOfRange = true; }
                Check("djvu worker: a request the worker refuses (page out of range) fails alone, and the same worker still renders", outOfRange && d.Render(0, 1_000_000, CancellationToken.None).Width == 160, "");
                var res = DjvuToPdf.Convert(d, Path.Combine(tmp, "djvu-worker.pdf"), null, null, CancellationToken.None);
                Check("djvu worker: a whole book converts to a PDF through the worker", res.Pages == 3 && File.Exists(Path.Combine(tmp, "djvu-worker.pdf")), "");
            }

            using (var d = DjvuLibreDecoder.Open(Fake("error-on-page:1"), "/books/a.djvu", CancellationToken.None, quick, quick))
            {
                bool failed = false;
                try { d.Render(1, 1_000_000, CancellationToken.None); } catch (InvalidDataException ex) { failed = ex.Message.Contains("cannot decode"); }
                var next = d.Render(0, 1_000_000, CancellationToken.None);
                Check("djvu worker: a page the worker reports as undecodable fails that page only, and the worker stays", failed && next.Width == 160, "");
            }

            using (var d = DjvuLibreDecoder.Open(Fake("die-on-page:1"), "/books/a.djvu", CancellationToken.None, quick, quick))
            {
                bool died = false;
                try { d.Render(1, 1_000_000, CancellationToken.None); } catch (IOException) { died = true; }
                var next = d.Render(0, 1_000_000, CancellationToken.None);
                Check("djvu worker: a crash fails that page; the next request starts a fresh worker and reopens the book", died && next.Width == 160, "");
                var conv = DjvuToPdf.Convert(d, Path.Combine(tmp, "djvu-crashpage.pdf"), null, null, CancellationToken.None);
                Check("djvu worker: a book with a page that crashes the worker converts with that page blank and says so", conv.Pages == 3 && conv.Notices.Any(n => n.Contains("could not be read")), string.Join("|", conv.Notices));
            }

            using (var d = DjvuLibreDecoder.Open(Fake("hang-on-page:1"), "/books/a.djvu", CancellationToken.None, hangs, quick))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool timedOut = false;
                try { d.Render(1, 1_000_000, CancellationToken.None); } catch (TimeoutException) { timedOut = true; }
                var el = sw.Elapsed;
                var next = d.Render(0, 1_000_000, CancellationToken.None);
                Check("djvu worker: a hang is killed at the time limit and the next page works", timedOut && el < TimeSpan.FromSeconds(15) && next.Width == 160, $"{el}");
            }

            using (var cts = new CancellationTokenSource())
            using (var d = DjvuLibreDecoder.Open(Fake("hang-on-page:1"), "/books/a.djvu", CancellationToken.None, quick, quick))
            {
                cts.CancelAfter(500);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool canceled = false;
                try { d.Render(1, 1_000_000, cts.Token); } catch (OperationCanceledException) { canceled = true; }
                Check("djvu worker: cancelling kills the worker at once", canceled && sw.Elapsed < TimeSpan.FromSeconds(15), $"{sw.Elapsed}");
            }

            using (var d = DjvuLibreDecoder.Open(Fake("big-on-page:1"), "/books/a.djvu", CancellationToken.None, capped, quick))
            {
                bool refused = false;
                try { d.Render(1, 1_000_000, CancellationToken.None); } catch (IOException) { refused = true; }
                Check("djvu worker: an answer bigger than the cap is refused without reading it", refused, "");
            }

            using (var d = DjvuLibreDecoder.Open(Fake("junk-on-page:1"), "/books/a.djvu", CancellationToken.None, quick, quick))
            {
                bool refused = false;
                try { d.Render(1, 1_000_000, CancellationToken.None); } catch (IOException) { refused = true; }
                Check("djvu worker: output that is not the protocol fails the request", refused, "");
            }

            using (var d = DjvuLibreDecoder.Open(Fake("die-on-every-page"), "/books/a.djvu", CancellationToken.None, quick, quick))
            {
                int failedPages = 0; bool opened = false;
                for (int i = 0; i < 3; i++) { try { d.Render(i, 1_000_000, CancellationToken.None); } catch (IOException) { failedPages++; } catch (BookConversionException) { opened = true; } }
                bool sticky = false;
                try { d.Render(0, 1_000_000, CancellationToken.None); } catch (BookConversionException) { sticky = true; }
                Check("djvu worker: three crashes in a row make the whole book unreadable, with the plain message, and it stays that way",
                      failedPages == 2 && opened && sticky, $"{failedPages} {opened} {sticky}");
                bool abortsConversion = false; string msg = "";
                try { DjvuToPdf.Convert(d, Path.Combine(tmp, "djvu-allcrash.pdf"), null, null, CancellationToken.None); } catch (BookConversionException ex) { abortsConversion = true; msg = ex.Message; }
                Check("djvu worker: a book that keeps crashing the worker stops the conversion with 'couldn't be opened'", abortsConversion && msg.Contains("couldn't be opened") && !File.Exists(Path.Combine(tmp, "djvu-allcrash.pdf")), msg);
            }

            string OpenMessage(string mode, DjvuProcessLimits? lim = null)
            {
                try { using var d = DjvuLibreDecoder.Open(Fake(mode), "/books/a.djvu", CancellationToken.None, quick, lim ?? quick); return "opened"; }
                catch (BookConversionException ex) { return ex.Message; }
                catch (Exception ex) { return "WRONG " + ex.GetType().Name; }
            }
            Check("djvu worker: a worker that dies at once is 'this DjVu file couldn't be opened'", OpenMessage("die-at-start") == DjvuLibreDecoder.CouldNotOpen, OpenMessage("die-at-start"));
            Check("djvu worker: a worker that never says hello is given up on at the time limit",
                OpenMessage("silent-at-start", new DjvuProcessLimits(TimeSpan.FromMilliseconds(800), 1000, 0)) == DjvuLibreDecoder.CouldNotOpen, "");
            Check("djvu worker: a worker that says something other than hello is refused", OpenMessage("bad-hello") == DjvuLibreDecoder.CouldNotOpen, OpenMessage("bad-hello"));
            Check("djvu worker: junk instead of the open reply is refused", OpenMessage("junk-on-open") == DjvuLibreDecoder.CouldNotOpen, OpenMessage("junk-on-open"));
            string notDjvu = OpenMessage("error-on-open");
            Check("djvu worker: the library's own reason is added to the plain message", notDjvu.StartsWith(DjvuLibreDecoder.CouldNotOpen, StringComparison.Ordinal) && notDjvu.Contains("not a DjVu"), notDjvu);
            bool noExe = false;
            try { DjvuLibreDecoder.Open(new DjvuWorkerLaunch("/no/such/worker", new[] { "x" }), "/books/a.djvu", CancellationToken.None); } catch (BookConversionException) { noExe = true; }
            Check("djvu worker: a worker that cannot be started is 'couldn't be opened'", noExe, "");
            using var preCancel = new CancellationTokenSource(); preCancel.Cancel();
            bool preCanceled = false;
            try { DjvuLibreDecoder.Open(Fake("ok"), "/books/a.djvu", preCancel.Token); } catch (OperationCanceledException) { preCanceled = true; }
            Check("djvu worker: opening an already cancelled request starts nothing", preCanceled, "");
        }

        // The test program is a framework-dependent app, which finds its runtime through DOTNET_ROOT; the real worker is TDPdf.exe, self-contained.
        static Dictionary<string, string> TestHostEnvironment()
        {
            string rt = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar);
            string? root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(rt)));
            return root is null ? new() : new() { ["DOTNET_ROOT"] = root };
        }

        // The worker's environment: a nearly empty one, so a book (or a bug) cannot reach the user's.
        if (!OperatingSystem.IsWindows())
        {
            string exe = Environment.ProcessPath!;
            var eargs = new List<string>();
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) eargs.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
            eargs.Add("--fake-djvu-worker"); eargs.Add("ok");
            var lim = new DjvuProcessLimits(TimeSpan.FromSeconds(30), 10_000_000, 0);
            using var d = DjvuLibreDecoder.Open(new DjvuWorkerLaunch(exe, eargs, TestHostEnvironment()), "/books/a.djvu", CancellationToken.None, lim, lim);
            string envText = d.GetWords(2).FirstOrDefault().Text ?? "";
            Check("djvu worker: the child gets a nearly empty environment (no HOME, no USER)", envText.StartsWith("home=unset user=unset", StringComparison.Ordinal) && envText.Contains("temp=set"), envText);
        }
        else Console.WriteLine("  (the worker process is exercised on macOS and Linux)");

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

        // ── the real library ────────────────────────────────────────────────────────────────
        // DjVuLibre is bundled as Windows DLLs (third_party/djvulibre/win-x64), loaded only by the worker
        // process (TDPdf.exe --djvu-worker), so no check here opens a real book: on this machine the real
        // decode cannot run. What is checked above is everything around it: the wire protocol, the
        // launcher's limits and recovery, and that the bundled files are the pinned, self-contained set.
        // tests/PdfCore/fixtures/book.djvu is a two-page book for a run on Windows.
        Console.WriteLine("  SKIP  djvu real decode: the library is Windows-only; the checks above cover the protocol, the launcher and the bundle");
    }
}

/// <summary>
/// A stand-in for <c>TDPdf.exe --djvu-worker</c>: this test program started with <c>--fake-djvu-worker &lt;mode&gt;</c>.
/// It speaks the real protocol (<see cref="DjvuWorkerProtocol"/>) over stdin and stdout with made-up pages, and
/// can misbehave on cue, so the parent's limits and recovery are tested with real child processes.
/// </summary>
internal static class DjvuFakeWorker
{
    public static int Run(string mode)
    {
        string kind = mode.Split(':')[0];
        int target = mode.Contains(':') ? int.Parse(mode.Split(':')[1]) : -1;
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        if (kind == "die-at-start") return 9;
        if (kind == "silent-at-start") { Thread.Sleep(60_000); return 0; }
        if (kind == "bad-hello")
        {
            var h = DjvuWorkerProtocol.EncodeHello(); h[4] = 99;
            DjvuWorkerProtocol.WriteFrame(output, DjvuFrame.Hello, h);
            Thread.Sleep(60_000);
            return 0;
        }
        DjvuWorkerProtocol.WriteFrame(output, DjvuFrame.Hello, DjvuWorkerProtocol.EncodeHello());
        var pages = new[] { new DjvuPageInfo(160, 40, 300), new DjvuPageInfo(160, 40, 150), new DjvuPageInfo(160, 40, 100) };
        while (true)
        {
            (DjvuFrame type, byte[] payload) f;
            try { f = DjvuWorkerProtocol.ReadFrame(input, DjvuWorkerProtocol.MaxRequestBytes); } catch (EndOfStreamException) { return 0; }
            switch (f.type)
            {
                case DjvuFrame.Quit: return 0;
                case DjvuFrame.Open:
                    DjvuWorkerProtocol.DecodeOpenRequest(f.payload);
                    if (kind == "junk-on-open") { output.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }); output.Flush(); break; }
                    if (kind == "error-on-open") { DjvuWorkerProtocol.WriteFrame(output, DjvuFrame.Error, DjvuWorkerProtocol.EncodeError("this is not a DjVu document")); break; }
                    DjvuWorkerProtocol.WriteFrame(output, DjvuFrame.Open, DjvuWorkerProtocol.EncodeOpenReply(new DjvuBookHeader(pages,
                        new Dictionary<string, int> { ["a.djvu"] = 0, ["b.djvu"] = 1, ["c.djvu"] = 2 })));
                    break;
                case DjvuFrame.Page:
                {
                    int i;
                    try { (i, _) = DjvuWorkerProtocol.DecodePageRequest(f.payload, pages.Length); }
                    catch (InvalidDataException ex) { DjvuWorkerProtocol.WriteFrame(output, DjvuFrame.Error, DjvuWorkerProtocol.EncodeError(ex.Message)); break; }
                    if (kind == "die-on-every-page" || (kind == "die-on-page" && i == target)) return 7;
                    if (kind == "hang-on-page" && i == target) { Thread.Sleep(60_000); return 0; }
                    if (kind == "error-on-page" && i == target) { DjvuWorkerProtocol.WriteFrame(output, DjvuFrame.Error, DjvuWorkerProtocol.EncodeError("cannot decode page")); break; }
                    if (kind == "big-on-page" && i == target) { output.Write(new byte[] { 0xD7, (byte)DjvuFrame.Page, 0x40, 0x4B, 0x4C, 0x00 }); output.Flush(); Thread.Sleep(60_000); return 0; }
                    if (kind == "junk-on-page" && i == target) { output.Write(new byte[100]); output.Flush(); break; }
                    if (i == 0)
                    {
                        var bits = new byte[20 * 40]; Array.Fill(bits, (byte)0xAA);
                        DjvuWorkerProtocol.WritePageReply(output, DjvuPageKind.Bitonal, 160, 40, bits);
                    }
                    else
                    {
                        var rgb = new byte[160 * 40 * 3]; Array.Fill(rgb, (byte)200);
                        DjvuWorkerProtocol.WritePageReply(output, DjvuPageKind.Color, 160, 40, rgb);
                    }
                    break;
                }
                case DjvuFrame.Text:
                {
                    int i = DjvuWorkerProtocol.DecodeTextRequest(f.payload, pages.Length);
                    string word = i == 2
                        ? $"home={(Environment.GetEnvironmentVariable("HOME") is null ? "unset" : "set")} user={(Environment.GetEnvironmentVariable("USER") is null ? "unset" : "set")} temp={(Environment.GetEnvironmentVariable("TEMP") is null ? "unset" : "set")}"
                        : "Café";
                    DjvuWorkerProtocol.WriteFrame(output, DjvuFrame.Text, Encoding.UTF8.GetBytes($"(page 0 0 160 40 (word 10 10 80 30 \"{word}\"))"));
                    break;
                }
                case DjvuFrame.Outline:
                    DjvuWorkerProtocol.WriteFrame(output, DjvuFrame.Outline, Encoding.UTF8.GetBytes("(bookmarks (\"Start\" \"#1\") (\"End\" \"#c.djvu\"))"));
                    break;
                default: return 8;
            }
        }
    }
}

/// <summary>
/// A small reader for the import table of a Windows DLL (PE32+), used to check that the bundled DjVuLibre
/// DLLs import only each other and Windows system libraries. Reads normal and delay-load imports.
/// </summary>
internal static class PeImports
{
    internal sealed record Result(int Machine, IReadOnlyList<string> Imports);

    internal static bool IsWindowsSystemLibrary(string name)
    {
        string n = name.ToLowerInvariant();
        return n.StartsWith("api-ms-win-", StringComparison.Ordinal) || n is "kernel32.dll" or "advapi32.dll" or "user32.dll" or "ws2_32.dll"
            or "msvcrt.dll" or "ucrtbase.dll" or "ntdll.dll" or "shell32.dll" or "ole32.dll" or "oleaut32.dll" or "bcrypt.dll" or "crypt32.dll";
    }

    internal static Result Read(byte[] d)
    {
        try
        {
            if (d.Length < 0x40 || d[0] != 'M' || d[1] != 'Z') throw new InvalidDataException("not an MZ image");
            int pe = BitConverter.ToInt32(d, 0x3C);
            if (pe < 0 || pe + 24 > d.Length || d[pe] != 'P' || d[pe + 1] != 'E' || d[pe + 2] != 0 || d[pe + 3] != 0) throw new InvalidDataException("not a PE image");
            int machine = BitConverter.ToUInt16(d, pe + 4), nsec = BitConverter.ToUInt16(d, pe + 6), optSize = BitConverter.ToUInt16(d, pe + 20);
            int opt = pe + 24;
            if (BitConverter.ToUInt16(d, opt) != 0x20B) throw new InvalidDataException("not PE32+");
            int dirs = opt + 112;
            var secs = new List<(uint Va, uint Size, uint Raw)>();
            for (int i = 0; i < nsec; i++)
            {
                int o = opt + optSize + i * 40;
                uint vs = BitConverter.ToUInt32(d, o + 8), va = BitConverter.ToUInt32(d, o + 12), rawSize = BitConverter.ToUInt32(d, o + 16), raw = BitConverter.ToUInt32(d, o + 20);
                secs.Add((va, Math.Max(vs, rawSize), raw));
            }
            int Rva(uint r)
            {
                foreach (var (va, size, raw) in secs) if (r >= va && r < va + size) return (int)(raw + (r - va));
                throw new InvalidDataException("bad RVA");
            }
            string Str(int o) { int e = Array.IndexOf(d, (byte)0, o); return Encoding.ASCII.GetString(d, o, e - o); }
            var names = new List<string>();
            uint imp = BitConverter.ToUInt32(d, dirs + 8);
            if (imp != 0)
                for (int o = Rva(imp); BitConverter.ToUInt32(d, o + 12) != 0; o += 20) names.Add(Str(Rva(BitConverter.ToUInt32(d, o + 12))));
            uint delay = BitConverter.ToUInt32(d, dirs + 13 * 8);
            if (delay != 0)
                for (int o = Rva(delay); BitConverter.ToUInt32(d, o + 4) != 0; o += 32) names.Add(Str(Rva(BitConverter.ToUInt32(d, o + 4))));
            return new Result(machine, names.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException("not a readable PE image", ex);
        }
    }
}
