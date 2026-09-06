using System.Text;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using TDPdf.Services;

/// <summary>
/// The rasterise fallback: the page becomes a picture of itself with the marks painted out.
/// </summary>
/// <remarks>
/// Two things have to be true at once here, and each is easy to get without the other.
///
/// The marked area has to actually be black IN THE OUTPUT — which is checked by rendering the
/// result through PDFium and looking at the pixels, not by trusting the arithmetic that put them
/// there. And the original content has to be GONE FROM THE FILE, not merely covered: a picture
/// pasted over a page whose text is still underneath is the exact fake redaction this whole
/// feature exists to refuse, and it is what PdfSharpCore's XGraphicsPdfPageOptions.Replace would
/// have produced, since it stores that option and never acts on it. So the output's streams are
/// decompressed and searched.
/// </remarks>
internal static class Raster
{
    private const string Secret = "SECRETSCAN9999";

    public static void Run(Action<string, bool, string> Check, string tmp, Func<string, (byte[] bgra, int w, int h)> render)
    {
        Console.WriteLine("\nRasterise fallback");

        // A stand-in for a scan: a page-sized image with text on top of it.
        string imgPath = Path.Combine(tmp, "page-scan.png");
        File.WriteAllBytes(imgPath, MakePng(160, 200, 235, 235, 225));

        string src = Path.Combine(tmp, "raster-src.pdf");
        double pw, ph;
        {
            var doc = new PdfSharpCore.Pdf.PdfDocument();
            var page = doc.AddPage();
            pw = page.Width.Point; ph = page.Height.Point;
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawImage(XImage.FromFile(imgPath), 0, 0, pw, ph);
            gfx.DrawString(Secret, new XFont("Helvetica", 22), XBrushes.Black, 80, 150);
            gfx.DrawString("KEEPTHISONE", new XFont("Helvetica", 22), XBrushes.Black, 80, 500);
            doc.Save(src);
        }

        int opsBefore = Streams.TextShowingOps(src);
        int imagesBefore = Streams.Images(src).Count;
        Check("the fixture really does draw text (or the absence test below proves nothing)",
              opsBefore >= 2, $"{opsBefore} text-showing operators");

        // The mark, derived from the glyph box so it cannot drift.
        (double L, double B, double R, double T) box;
        using (var d = UglyToad.PdfPig.PdfDocument.Open(src))
        {
            var w = d.GetPage(1).GetWords().First(x => x.Text.Contains(Secret, StringComparison.Ordinal));
            box = (w.BoundingBox.Left, w.BoundingBox.Bottom, w.BoundingBox.Right, w.BoundingBox.Top);
        }
        var mark = new PdfiumInterop.PdfRect(box.L - 6, box.B - 5, box.R + 6, box.T + 5);

        string dest = Path.Combine(tmp, "raster-out.pdf");
        if (File.Exists(dest)) File.Delete(dest);

        var res = PdfRedaction.Apply(src, dest, new PdfRedaction.Request
        {
            RectsByPage = new Dictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> { [0] = new[] { mark } },
            RasterizePages = new[] { 0 },
            ScrubMetadata = true,
        });

        Console.WriteLine($"        ok={res.Ok} rasterized={string.Join(",", res.Rasterized)} survivors={res.Survivors.Count} {res.Error}");
        Check("succeeded", res.Ok, res.Error ?? "");
        Check("reported the page as rasterised", res.Rasterized.Count == 1 && res.Rasterized[0] == 0, "");
        if (!res.Ok || !File.Exists(dest)) { Console.WriteLine("  (skipping the rest — no output)"); return; }

        long srcKb = new FileInfo(src).Length / 1024, outKb = new FileInfo(dest).Length / 1024;
        Console.WriteLine($"        {srcKb} KB -> {outKb} KB");

        using (var d = UglyToad.PdfPig.PdfDocument.Open(dest))
        {
            var page = d.GetPage(1);
            Check("the page carries no text at all any more", page.GetWords().Count() == 0,
                  page.Text.Length > 60 ? page.Text[..60] : page.Text);
            Check("the page kept its size", Math.Abs(page.Width - pw) < 1 && Math.Abs(page.Height - ph) < 1,
                  $"{page.Width:F0}x{page.Height:F0} vs {pw:F0}x{ph:F0}");
        }

        // The two that would catch a picture pasted over content that is still there. Both look at
        // the file rather than at what a viewer chooses to render.
        int opsAfter = Streams.TextShowingOps(dest);
        var imagesAfter = Streams.Images(dest);
        Console.WriteLine($"        text-showing operators {opsBefore} -> {opsAfter}; image XObjects {imagesBefore} -> {imagesAfter.Count}");
        Check("no text-showing operator survives anywhere in the file", opsAfter == 0, $"{opsAfter} left");
        Check("the original page image is gone, replaced by exactly one JPEG",
              imagesAfter.Count == 1 && imagesAfter[0].Filter == "/DCTDecode",
              string.Join(", ", imagesAfter.Select(i => $"{i.W}x{i.H} {i.Filter}")));

        // And the one that would catch a blackout painted in the wrong place.
        var (bgra, rw, rh) = render(dest);
        var (mx, my, mw, mh) = PdfPageGeometry.PdfRectToCanvas(
            PdfSharpCore.Pdf.IO.PdfReader.Open(dest, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import).Pages[0],
            mark, rw, rh);

        double Darkness(int x0, int y0, int w, int h)
        {
            int dark = 0, total = 0;
            for (int y = Math.Max(0, y0); y < Math.Min(rh, y0 + h); y++)
                for (int x = Math.Max(0, x0); x < Math.Min(rw, x0 + w); x++)
                {
                    int at = (y * rw + x) * 4;
                    if (bgra[at] < 40 && bgra[at + 1] < 40 && bgra[at + 2] < 40) dark++;
                    total++;
                }
            return total == 0 ? 0 : (double)dark / total;
        }

        double inside = Darkness(mx + 2, my + 2, Math.Max(1, mw - 4), Math.Max(1, mh - 4));
        double elsewhere = Darkness(0, my + mh + 40, rw, 60);
        Console.WriteLine($"        marked area {inside:P0} black, a strip well below it {elsewhere:P0} black");
        Check("the marked area is solid black in the rendered output", inside > 0.98, $"{inside:P1}");
        Check("the rest of the page was left alone", elsewhere < 0.10, $"{elsewhere:P1}");
    }

    /// <summary>A minimal RGB PNG (see ImageGuard for why the harness builds its own).</summary>
    private static byte[] MakePng(int w, int h, byte r, byte g, byte b)
    {
        var raw = new List<byte>();
        for (int y = 0; y < h; y++)
        {
            raw.Add(0);
            for (int x = 0; x < w; x++) { raw.Add(r); raw.Add(g); raw.Add(b); }
        }
        byte[] Deflate(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
                z.Write(data);
            return ms.ToArray();
        }
        static uint Crc(byte[] d)
        {
            uint c = 0xFFFFFFFF;
            foreach (byte x in d) { c ^= x; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
            return c ^ 0xFFFFFFFF;
        }
        static byte[] Be(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        void Chunk(List<byte> into, string type, byte[] body)
        {
            var payload = Encoding.ASCII.GetBytes(type).Concat(body).ToArray();
            into.AddRange(Be(body.Length)); into.AddRange(payload);
            into.AddRange(Be(unchecked((int)Crc(payload))));
        }
        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Chunk(png, "IHDR", Be(w).Concat(Be(h)).Concat(new byte[] { 8, 2, 0, 0, 0 }).ToArray());
        Chunk(png, "IDAT", Deflate(raw.ToArray()));
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }
}
