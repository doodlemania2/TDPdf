using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TDPdf.Services;

/// <summary>
/// The two ways a flattened or imported page avoids becoming 24-bit RGB FlateDecode: 1-bit for a
/// genuinely black-and-white page, and pass-through JPEG for a page that was already one.
/// </summary>
/// <remarks>
/// Both are size optimisations, and the only interesting question about a size optimisation is
/// whether it was applied where it should NOT have been. So the negative cases carry the weight
/// here: a near-black pixel, a half-transparent pixel, a page whose picture covers a corner rather
/// than the sheet, a JPEG variant PDF's /DCTDecode does not cover. Each of those must take the old
/// lossless path, because the alternative is a document that is permanently, silently worse.
///
/// The 1-bit round trip is checked by RENDERING the output, not by reading the dictionary back.
/// Packing rows continuously instead of restarting each one on a byte boundary produces a file that
/// is structurally perfect and visually sheared into diagonal stripes — the padding bug this exists
/// to catch is invisible to any check that only counts bytes. The fixture is 101 pixels wide for
/// exactly that reason: 101 is not a multiple of 8, so every row carries 3 bits of padding and the
/// error compounds down the page.
/// </remarks>
internal static class Flatten
{
    public static void Run(Action<string, bool, string> Check, string tmp, Func<string, (byte[] bgra, int w, int h)> render)
    {
        Console.WriteLine("\nBitonal detection");

        const int W = 101, H = 64, Split = 40;

        // Black on the left, white on the right, every row identical.
        byte[] Stripes()
        {
            var b = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int at = (y * W + x) * 4;
                    byte v = x < Split ? (byte)0 : (byte)255;
                    b[at] = v; b[at + 1] = v; b[at + 2] = v; b[at + 3] = 255;
                }
            return b;
        }

        Check("a pure black-and-white raster is bitonal",
              PdfPageImageEncoder.IsBitonal(Stripes(), W, H), "");

        // The case that matters most. 8,8,8 looks black and is not, and a page of anti-aliased
        // text is made almost entirely of pixels like it.
        var nearBlack = Stripes();
        int probe = ((H / 2) * W + 5) * 4;
        nearBlack[probe] = 8; nearBlack[probe + 1] = 8; nearBlack[probe + 2] = 8;
        Check("ONE near-black pixel (8,8,8) disqualifies the whole page",
              !PdfPageImageEncoder.IsBitonal(nearBlack, W, H), "");

        var nearWhite = Stripes();
        int probe2 = ((H / 2) * W + 90) * 4;
        nearWhite[probe2] = 250; nearWhite[probe2 + 1] = 250; nearWhite[probe2 + 2] = 250;
        Check("one near-white pixel (250,250,250) disqualifies it too",
              !PdfPageImageEncoder.IsBitonal(nearWhite, W, H), "");

        // Alpha, both ends. PDFium leaves unpainted background fully transparent, which IS white;
        // partial coverage composites to a grey, which is not.
        var transparent = Stripes();
        for (int x = Split; x < W; x++) { int at = ((H / 2) * W + x) * 4; transparent[at + 3] = 0; }
        Check("fully transparent background counts as white (PDFium leaves it at alpha 0)",
              PdfPageImageEncoder.IsBitonal(transparent, W, H), "");

        var halfAlpha = Stripes();
        halfAlpha[probe + 3] = 128;
        Check("a half-transparent pixel does not (it composites to grey)",
              !PdfPageImageEncoder.IsBitonal(halfAlpha, W, H), "");

        Check("a buffer shorter than width*height*4 is refused rather than read past",
              !PdfPageImageEncoder.IsBitonal(new byte[W * H * 4 - 1], W, H), "");

        // ── Row packing ────────────────────────────────────────────────────────────────────────
        Console.WriteLine("\n1-bit row packing");

        // 9 pixels wide: 2 bytes a row, 7 bits of padding, and a second row that must start at
        // byte 2 rather than at bit 9.
        var tiny = new byte[9 * 2 * 4];
        for (int i = 0; i < 9 * 2; i++) { tiny[i * 4 + 3] = 255; }       // opaque, all black
        for (int x = 1; x < 9; x++) { int at = x * 4; tiny[at] = tiny[at + 1] = tiny[at + 2] = 255; }
        // Row 1 is entirely white.
        for (int x = 0; x < 9; x++) { int at = (9 + x) * 4; tiny[at] = tiny[at + 1] = tiny[at + 2] = 255; }

        byte[] packed = PdfPageImageEncoder.PackBitonal(tiny, 9, 2);
        Check("a 9-pixel row occupies 2 bytes, so 2 rows are 4 bytes — not ceil(18/8)=3",
              packed.Length == 4, $"{packed.Length} bytes");
        Check("row 0: black at x=0, white for x=1..7, MSB first", packed[0] == 0x7F, $"0x{packed[0]:X2}");
        Check("row 0: the 9th pixel is white and the 7 padding bits stay 0",
              packed[1] == 0x80, $"0x{packed[1]:X2}");
        Check("row 1 restarts on a byte boundary — all white", packed[2] == 0xFF, $"0x{packed[2]:X2}");
        Check("row 1's padding is clear too", packed[3] == 0x80, $"0x{packed[3]:X2}");

        // ── The round trip ─────────────────────────────────────────────────────────────────────
        Console.WriteLine("\n1-bit page round trip");

        var encoded = PdfPageImageEncoder.TryEncodeBitonal(Stripes(), W, H);
        Check("a bitonal raster encodes", encoded is not null, "");
        if (encoded is null) return;

        string onebit = Path.Combine(tmp, "flatten-1bit.pdf");
        {
            var doc = new PdfDocument();
            var page = doc.AddPage();
            page.Width = W;          // 1 point per pixel, so the render below is 1:1
            page.Height = H;
            PdfPageImageEncoder.PaintFullPage(doc, page, encoded, page.Width.Point, page.Height.Point);
            doc.Save(onebit);
        }

        using (var d = PdfSharpCore.Pdf.IO.PdfReader.Open(onebit, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify))
        {
            PdfDictionary? image = null;
            foreach (var obj in d.Internals.GetAllObjects())
                if (obj is PdfDictionary dict && dict.Elements.GetName("/Subtype") == "/Image")
                    image = dict;

            Check("the page carries exactly one image XObject", image is not null, "");
            if (image is not null)
            {
                Check("stored as 1-bit DeviceGray FlateDecode",
                      image.Elements.GetInteger("/BitsPerComponent") == 1
                      && image.Elements.GetName("/ColorSpace") == "/DeviceGray"
                      && image.Elements.GetName("/Filter") == "/FlateDecode",
                      $"{image.Elements.GetInteger("/BitsPerComponent")} bpc "
                      + $"{image.Elements.GetName("/ColorSpace")} {image.Elements.GetName("/Filter")}");
                Check("at the raster's own pixel dimensions",
                      image.Elements.GetInteger("/Width") == W && image.Elements.GetInteger("/Height") == H,
                      $"{image.Elements.GetInteger("/Width")}x{image.Elements.GetInteger("/Height")}");
            }
        }

        Console.WriteLine($"        {W}x{H} page: {encoded.Bytes.Length} bytes of image data "
                          + $"(24-bit RGB would be {W * H * 3})");
        Check("the 1-bit payload is a fraction of the 24-bit one it replaces",
              encoded.Bytes.Length < W * H * 3 / 8, $"{encoded.Bytes.Length} bytes");

        // The shear test. Sampling the LAST rows at the far right is what a row-padding bug fails:
        // 3 bits of drift per row, 60 rows down, puts the stripe boundary nowhere near x=40.
        var (bgra, rw, rh) = render(onebit);
        Check("rendered at the expected size", rw == W && rh == H, $"{rw}x{rh}");
        if (rw == W && rh == H)
        {
            bool Black(int x, int y)
            {
                int at = (y * rw + x) * 4;
                return bgra[at] < 64 && bgra[at + 1] < 64 && bgra[at + 2] < 64;
            }

            Check("row 5 is black on the left", Black(10, 5), "");
            Check("row 5 is white on the right", !Black(90, 5), "");
            Check("row 60 is STILL black on the left (rows did not drift)", Black(10, 60), "");
            Check("row 60 is STILL white on the right", !Black(90, 60), "");
            Check("the stripe boundary is where it was packed, on the last row",
                  Black(Split - 4, H - 2) && !Black(Split + 4, H - 2), "");
        }

        // ── JPEG sniffing and page hints ───────────────────────────────────────────────────────
        Console.WriteLine("\nJPEG source hints");

        // A photographic fixture — a gradient, so the JPEG has something to compress and the
        // encoder cannot collapse it to a degenerate stream.
        var photo = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int at = (y * W + x) * 4;
                photo[at] = (byte)(x * 255 / W); photo[at + 1] = (byte)(y * 255 / H);
                photo[at + 2] = 128; photo[at + 3] = 255;
            }

        var jpegImage = PdfPageImageEncoder.EncodeJpeg((byte[])photo.Clone(), W, H);
        string jpegPath = Path.Combine(tmp, "flatten-source.jpg");
        File.WriteAllBytes(jpegPath, jpegImage.Bytes);

        Check("our own encoder produces a JPEG the pass-through sniff accepts",
              PdfPageImageEncoder.TryReadJpegFrame(jpegImage.Bytes, out int jw, out int jh, out int jc)
              && jw == W && jh == H && jc == 3, $"{jw}x{jh} x{jc}");

        string pngPath = Path.Combine(tmp, "flatten-source.png");
        using (var im = Image.LoadPixelData<Bgra32>(photo, W, H)) im.SaveAsPng(pngPath);
        Check("a PNG is not mistaken for a JPEG", PdfPageImageEncoder.TryReadPassThroughJpeg(pngPath) is null, "");

        // Progressive JPEG: same file, SOF0 turned into SOF2. /DCTDecode is specified over
        // sequential Huffman data, so this must be refused rather than embedded and hoped for.
        var progressive = (byte[])jpegImage.Bytes.Clone();
        int sof = -1;
        for (int i = 2; i + 1 < progressive.Length; i++)
            if (progressive[i] == 0xFF && progressive[i + 1] == 0xC0) { sof = i + 1; break; }
        Check("the fixture really is a baseline (SOF0) JPEG", sof > 0, $"marker at {sof}");
        if (sof > 0)
        {
            progressive[sof] = 0xC2;
            Check("a progressive (SOF2) JPEG is refused, not passed through",
                  !PdfPageImageEncoder.TryReadJpegFrame(progressive, out _, out _, out _), "");
        }

        Check("a truncated file is refused", !PdfPageImageEncoder.TryReadJpegFrame(new byte[] { 0xFF, 0xD8 }, out _, out _, out _), "");

        // Three pages: a full-page JPEG, a full-page PNG, and a JPEG covering one corner.
        string mixed = Path.Combine(tmp, "flatten-hints.pdf");
        {
            var doc = new PdfDocument();

            var scan = doc.AddPage();
            scan.Width = W; scan.Height = H;
            PdfPageImageEncoder.PaintFullPage(doc, scan, jpegImage, scan.Width.Point, scan.Height.Point);

            var lossless = doc.AddPage();
            lossless.Width = W; lossless.Height = H;
            using (var gfx = XGraphics.FromPdfPage(lossless))
                gfx.DrawImage(XImage.FromFile(pngPath), 0, 0, W, H);

            var corner = doc.AddPage();
            corner.Width = W * 4; corner.Height = H * 4;
            PdfPageImageEncoder.PaintFullPage(doc, corner, jpegImage, W, H);

            doc.Save(mixed);
        }

        bool[] hints = PdfPageImageEncoder.ReadJpegPageHints(mixed, 3);
        Console.WriteLine($"        hints: [{string.Join(", ", hints)}]");
        Check("a page that is one full-bleed JPEG is hinted as a scan", hints[0], "");
        Check("a page whose image is FlateDecode is not", !hints[1], "");
        Check("a JPEG covering a corner of the page is not either — a photo in a text "
              + "document must not cost the text its fidelity", !hints[2], "");

        Check("a file PdfPig cannot open yields all-false rather than throwing",
              PdfPageImageEncoder.ReadJpegPageHints(pngPath, 3) is [false, false, false], "");
    }
}
