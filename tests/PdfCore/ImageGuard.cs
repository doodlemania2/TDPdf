using System.IO.Compression;
using PdfSharpCore.Drawing;
using TDPdf.Services;

/// <summary>
/// A mark inside an image must never take the image with it.
/// </summary>
/// <remarks>
/// This is the scanned-document case, and it is the one where over-deleting is catastrophic rather
/// than merely annoying. A scan is a single page-sized image; any mark the user draws on it
/// straddles that image rather than containing it. Treating "straddles" as "delete the whole
/// object" — which is the right answer for a line of text — deletes the entire page, and because
/// the page then contains no text, a text-based verification pass sees nothing wrong and the file
/// is written. Blank page, reported as a success.
///
/// So the engine refuses to remove an image on a partial overlap regardless of the caller's
/// preference, the pipeline stops when one is reported, and no file is produced. The pages come
/// back in PagesNeedingRaster, which is what a caller needs in order to offer rasterising instead.
/// </remarks>
internal static class ImageGuard
{
    /// <summary>A minimal RGB PNG, built by hand so the harness needs no imaging library.</summary>
    private static byte[] Png(int w, int h, byte r, byte g, byte b)
    {
        var raw = new List<byte>();
        for (int y = 0; y < h; y++)
        {
            raw.Add(0);                                   // filter: none
            for (int x = 0; x < w; x++) { raw.Add(r); raw.Add(g); raw.Add(b); }
        }

        byte[] Deflate(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
            return ms.ToArray();
        }

        static uint Crc(byte[] d)
        {
            uint c = 0xFFFFFFFF;
            foreach (byte x in d)
            {
                c ^= x;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            return c ^ 0xFFFFFFFF;
        }

        static byte[] Be(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

        void Chunk(List<byte> into, string type, byte[] body)
        {
            var payload = System.Text.Encoding.ASCII.GetBytes(type).Concat(body).ToArray();
            into.AddRange(Be(body.Length));
            into.AddRange(payload);
            into.AddRange(Be(unchecked((int)Crc(payload))));
        }

        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Chunk(png, "IHDR", Be(w).Concat(Be(h)).Concat(new byte[] { 8, 2, 0, 0, 0 }).ToArray());
        Chunk(png, "IDAT", Deflate(raw.ToArray()));
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nA mark inside an image (the scanned-page case)");

        string imgPath = Path.Combine(tmp, "scan.png");
        File.WriteAllBytes(imgPath, Png(120, 160, 210, 210, 200));

        string src = Path.Combine(tmp, "scan.pdf");
        {
            var doc = new PdfSharpCore.Pdf.PdfDocument();
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            // Page-sized, exactly like a scan.
            gfx.DrawImage(XImage.FromFile(imgPath), 0, 0, page.Width.Point, page.Height.Point);
            doc.Save(src);
        }

        string dest = Path.Combine(tmp, "scan-redacted.pdf");
        if (File.Exists(dest)) File.Delete(dest);

        // A small rectangle in the middle of the page: inside the image, nowhere near containing it.
        var mark = new PdfiumInterop.PdfRect(Left: 200, Bottom: 400, Right: 380, Top: 430);
        var res = PdfRedaction.Apply(src, dest, new PdfRedaction.Request
        {
            RectsByPage = new Dictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> { [0] = new[] { mark } },
            // The dangerous setting, deliberately: this is the default, and it is what would
            // otherwise delete the whole scan.
            RemovePartialOverlaps = true,
            ScrubMetadata = true,
        });

        Console.WriteLine($"        ok={res.Ok} removed={res.ObjectsRemoved} partial={res.Partial.Count} raster={string.Join(",", res.PagesNeedingRaster)}");
        Check("the image was NOT deleted", res.ObjectsRemoved == 0, $"{res.ObjectsRemoved} objects removed");
        Check("reported not-ok", !res.Ok, res.Error ?? "(no error given)");
        Check("named the page as needing rasterising",
              res.PagesNeedingRaster.Count == 1 && res.PagesNeedingRaster[0] == 0,
              string.Join(",", res.PagesNeedingRaster));
        Check("NO output file was written", !File.Exists(dest), "");

        // The other half of the rule: an image the mark FULLY contains is still removed, so a small
        // photo or a signature stamp inside a text page redacts exactly as it should.
        string src2 = Path.Combine(tmp, "stamp.pdf");
        {
            var doc = new PdfSharpCore.Pdf.PdfDocument();
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawImage(XImage.FromFile(imgPath), 200, 300, 60, 80);   // top-left origin
            doc.Save(src2);
        }
        string dest2 = Path.Combine(tmp, "stamp-redacted.pdf");
        if (File.Exists(dest2)) File.Delete(dest2);

        double h2;
        using (var d = UglyToad.PdfPig.PdfDocument.Open(src2)) h2 = d.GetPage(1).Height;
        // The stamp occupies PDF y = h-380 .. h-300; take a rectangle comfortably around it.
        var around = new PdfiumInterop.PdfRect(Left: 180, Bottom: h2 - 400, Right: 280, Top: h2 - 280);
        var res2 = PdfRedaction.Apply(src2, dest2, new PdfRedaction.Request
        {
            RectsByPage = new Dictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> { [0] = new[] { around } },
            RemovePartialOverlaps = true,
            ScrubMetadata = true,
        });
        Console.WriteLine($"        ok={res2.Ok} removed={res2.ObjectsRemoved} partial={res2.Partial.Count}");
        Check("a fully contained image IS removed", res2.Ok && res2.ObjectsRemoved == 1,
              res2.Error ?? $"{res2.ObjectsRemoved} objects removed");
    }
}
