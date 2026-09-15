using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Tokens;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf.Services
{
    /// <summary>How the bytes of a whole-page image are meant to reach the PDF.</summary>
    internal enum PageImageEncoding
    {
        /// <summary>
        /// PNG bytes handed to <see cref="XImage"/>. PdfSharpCore decodes them and re-emits
        /// 24-bit RGB <c>/FlateDecode</c>: lossless, correct for anything, and large.
        /// </summary>
        Png,

        /// <summary>JPEG bytes embedded verbatim as <c>/DCTDecode</c> — no decode, no re-encode.</summary>
        Jpeg,

        /// <summary>
        /// Rows packed 8 pixels to the byte and zlib-compressed, embedded as
        /// <c>/DeviceGray</c> <c>/BitsPerComponent 1</c> <c>/FlateDecode</c>.
        /// </summary>
        Bitonal,
    }

    /// <summary>A page-sized image, already encoded, with everything the PDF writer needs.</summary>
    internal sealed class EncodedPageImage
    {
        internal EncodedPageImage(byte[] bytes, PageImageEncoding encoding,
                                  int pixelWidth, int pixelHeight, int components = 3)
        {
            Bytes = bytes;
            Encoding = encoding;
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
            Components = components;
        }

        internal byte[] Bytes { get; }
        internal PageImageEncoding Encoding { get; }
        internal int PixelWidth { get; }
        internal int PixelHeight { get; }

        /// <summary>Colour components per sample: 1 = grey, 3 = RGB. Only read for JPEG.</summary>
        internal int Components { get; }
    }

    /// <summary>
    /// Picks — and writes — the image that stands in for a whole page.
    /// </summary>
    /// <remarks>
    /// Four places in TDPdf turn a page (or a source picture) into a single full-bleed image:
    /// Save Flattened, the raster repair fallback on the open path, image import, and redaction's
    /// <see cref="PdfPageRasterizer"/>. All of them used to funnel through PNG, which PdfSharpCore
    /// re-emits as 24-bit RGB FlateDecode — the worst possible container for the documents people
    /// actually flatten, which are photographs and scans. A 40-page colour scan that arrives as a
    /// 6 MB JPEG-per-page PDF leaves as 90 MB.
    ///
    /// Two escapes from that, and BOTH are gated, because the wrong one is far worse than a big
    /// file:
    ///
    ///   * <b>JPEG, only where the source was already JPEG.</b> Re-encoding a page of text or line
    ///     art as JPEG puts ringing around every glyph and edge, permanently, in a file the user
    ///     asked us to flatten precisely so it would stop changing. So the encoder is only reached
    ///     when the page's own image XObjects are all <c>/DCTDecode</c> and one of them covers the
    ///     page — i.e. it is a scan, and the pixels have already been through exactly this
    ///     transform once. See <see cref="ReadJpegPageHints"/>.
    ///   * <b>1-bit, only where the raster really is bitonal.</b> This one needs no source hint to
    ///     be safe, because it is confirmed against the rendered pixels and is LOSSLESS: every
    ///     pixel is literally pure black or pure white, so packing it to one bit throws nothing
    ///     away. It is typically a 20x reduction on a scanned text page.
    ///
    /// Note what is deliberately NOT used to get a JPEG into the file: <c>XImage.FromStream</c>
    /// over JPEG bytes. It does produce a <c>/DCTDecode</c> image, but not the one it was handed —
    /// PdfSharpCore's image source (<c>ImageSharpImageSource.SaveAsJpeg</c>) DECODES the stream and
    /// re-encodes it at quality 75. On the flatten path that is a second lossy generation on top of
    /// ours; on the import path it would turn a byte-exact copy of the user's own photo into a
    /// visibly worse one. So JPEG is written as a hand-built image XObject instead, which is also
    /// the only way to express 1-bit at all: <c>XImage</c>/<c>XGraphics</c> have no 1-bit path.
    /// </remarks>
    internal static class PdfPageImageEncoder
    {
        /// <summary>
        /// Quality for the pages this class encodes itself. High enough that a second generation
        /// over an already-JPEG scan is not visible at reading size.
        /// </summary>
        private const int JpegQuality = 90;

        /// <summary>
        /// How much of the page one <c>/DCTDecode</c> image must cover before the page counts as
        /// "already a JPEG".
        /// </summary>
        private const double ScanCoverage = 0.9;

        // ── Source hints ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Per-page: is this page a JPEG scan, such that re-encoding it as JPEG loses nothing it
        /// has not already lost? Index <c>i</c> is page <c>i</c>; never throws, and a file PdfPig
        /// cannot read comes back all-false, which routes every page down the lossless path.
        /// </summary>
        /// <remarks>
        /// PdfPig is TDPdf's structure reader (see CLAUDE.md) and the only one of the three
        /// libraries that will tell us what filter a page's image XObjects carry. Docnet/PDFium
        /// hands back pixels and PdfSharpCore would mean walking /Resources by hand.
        ///
        /// The whole document is read ONCE, here, on the caller's thread. Save Flattened rasterises
        /// pages in a <c>Parallel.For</c>, and neither PdfPig nor this file's callers want a second
        /// reader opened per page — so the hints are computed up front and only READ inside the
        /// loop, which is what makes them thread-safe.
        ///
        /// Two conditions, not one. "All images are DCTDecode" alone would also catch a page of
        /// body text with a photograph in the corner, and JPEG-ing that page damages the text to
        /// save nothing (the text is what makes it big). Requiring one image to cover the page as
        /// well narrows it to the case the fix is for: a scan, with or without an invisible OCR
        /// text layer over it.
        /// </remarks>
        internal static bool[] ReadJpegPageHints(string path, int pageCount)
        {
            var hints = new bool[Math.Max(0, pageCount)];
            if (hints.Length == 0) return hints;

            try
            {
                using var doc = PdfPigDoc.Open(path);
                int n = Math.Min(hints.Length, doc.NumberOfPages);
                for (int i = 0; i < n; i++)
                {
                    // One unreadable page must not cost the rest of the document its hint.
                    try { hints[i] = IsJpegScanPage(doc.GetPage(i + 1)); }
                    catch { hints[i] = false; }
                }
            }
            catch
            {
                // Encrypted, malformed, or simply not something PdfPig will open — including the
                // raster-repair path, where the file is broken by definition. All-false is the
                // right answer: it is the behaviour this fix replaced.
            }
            return hints;
        }

        private static bool IsJpegScanPage(Page page)
        {
            double pageArea = page.Width * page.Height;
            if (!(pageArea > 0)) return false;

            int images = 0;
            bool covering = false;
            foreach (IPdfImage img in page.GetImages())
            {
                images++;
                if (!IsDctDecode(img.ImageDictionary)) return false;

                var b = img.BoundingBox;
                double area = Math.Abs(b.Width * b.Height);
                if (double.IsFinite(area) && area >= pageArea * ScanCoverage) covering = true;
            }
            return images > 0 && covering;
        }

        /// <summary>
        /// True when the stream's LAST filter is DCTDecode, i.e. the bytes underneath really are a
        /// JPEG. A stream with no /Filter at all is raw samples, not a JPEG, and says no.
        /// </summary>
        /// <remarks>
        /// The last entry is the one that matters: <c>[/FlateDecode /DCTDecode]</c> is a JPEG that
        /// was additionally zipped (PdfSharpCore itself writes that pair), and it is still a JPEG.
        /// <c>/DCT</c> is the inline-image abbreviation for the same filter.
        /// </remarks>
        private static bool IsDctDecode(DictionaryToken? dict)
        {
            if (dict is null) return false;
            if (!dict.TryGet(NameToken.Filter, out IToken? token))
            {
                // Inline images abbreviate /Filter to /F.
                if (!dict.TryGet(NameToken.F, out token)) return false;
            }

            return token switch
            {
                NameToken name => IsDctName(name.Data),
                ArrayToken array => array.Data.Count > 0
                                    && array.Data[array.Data.Count - 1] is NameToken last
                                    && IsDctName(last.Data),
                _ => false,
            };
        }

        private static bool IsDctName(string name) =>
            string.Equals(name, "DCTDecode", StringComparison.Ordinal) ||
            string.Equals(name, "DCT", StringComparison.Ordinal);

        // ── Bitonal (1-bit) pages ──────────────────────────────────────────────────────────────

        /// <summary>
        /// True when every pixel of a PDFium BGRA raster is pure black or pure white, so the page
        /// can be stored at one bit per pixel with no loss whatsoever.
        /// </summary>
        /// <remarks>
        /// Strict on purpose, in both directions.
        ///
        /// <b>Colour.</b> A NEAR-black pixel (say 8,8,8) means the page is not bitonal and must take
        /// the normal path. Quantising it would be a visible change to the document, and "nearly
        /// all black and white" is exactly what an anti-aliased page of text looks like — accepting
        /// it would turn every flattened text page into a hard-thresholded one.
        ///
        /// <b>Alpha.</b> PDFium leaves unpainted background at alpha 0, so a fully transparent pixel
        /// is the page's white background and counts as white (source-over onto white gives white,
        /// whatever the colour bytes underneath say). PARTIAL alpha does not: compositing it over
        /// white produces a grey, and a grey means this is not a bitonal page. This is the same
        /// trap that makes an uncomposited buffer encode as solid black through a JPEG encoder —
        /// alpha has to be reasoned about before the pixels mean anything.
        /// </remarks>
        internal static bool IsBitonal(byte[] bgra, int width, int height)
        {
            if (bgra is null || width <= 0 || height <= 0) return false;

            long needed = (long)width * height * 4;
            if (bgra.LongLength < needed) return false;

            for (long i = 0; i < needed; i += 4)
            {
                byte a = bgra[i + 3];
                if (a == 0) continue;                 // unpainted background == white
                if (a != 255) return false;           // partial coverage composites to grey

                byte bl = bgra[i], gr = bgra[i + 1], rd = bgra[i + 2];
                if (bl == 0 && gr == 0 && rd == 0) continue;
                if (bl == 255 && gr == 255 && rd == 255) continue;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Packs a bitonal BGRA raster to PDF 1-bit <c>/DeviceGray</c> samples: 8 pixels per byte,
        /// most significant bit leftmost, <b>each row restarting on a byte boundary</b>.
        /// </summary>
        /// <remarks>
        /// THE ROW PADDING IS THE WHOLE TRICK, and getting it wrong is the classic bug in this
        /// code. PDF image data is a sequence of ROWS, not a bit stream: a row of 1275 pixels
        /// occupies ceil(1275/8) = 160 bytes, of which the last 5 bits are padding that the decoder
        /// ignores. Packing continuously instead — letting row 2 start in the leftover bits of
        /// row 1's last byte — produces a file that opens fine and renders every row shifted a
        /// little further right than the one above it: a page sheared into diagonal stripes. It is
        /// obvious once seen and invisible in any check that only counts bytes, which is why the
        /// test renders the result rather than measuring it.
        ///
        /// In <c>/DeviceGray</c> at 1 bit, sample 0 is black and 1 is white. The buffer starts
        /// zeroed (all black) and white bits are set, so the padding bits at the end of a row stay
        /// 0 — harmless, since /Width tells the decoder where the row really ends.
        ///
        /// A pixel is black only if it is opaque and all three colour bytes are 0; everything else
        /// is white. Callers are expected to have confirmed <see cref="IsBitonal"/> first, which
        /// makes that an exact classification rather than a threshold.
        /// </remarks>
        internal static byte[] PackBitonal(byte[] bgra, int width, int height)
        {
            int stride = (width + 7) / 8;
            var packed = new byte[checked(stride * height)];

            for (int y = 0; y < height; y++)
            {
                int rowStart = y * stride;
                long at = (long)y * width * 4;
                for (int x = 0; x < width; x++, at += 4)
                {
                    bool black = bgra[at + 3] != 0 && bgra[at] == 0 && bgra[at + 1] == 0 && bgra[at + 2] == 0;
                    if (!black) packed[rowStart + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
            return packed;
        }

        /// <summary>
        /// The 1-bit encoding of <paramref name="bgra"/>, or <c>null</c> when the raster is not
        /// bitonal and must take the lossless 24-bit path.
        /// </summary>
        internal static EncodedPageImage? TryEncodeBitonal(byte[] bgra, int width, int height)
        {
            if (!IsBitonal(bgra, width, height)) return null;
            byte[] deflated = Deflate(PackBitonal(bgra, width, height));
            return new EncodedPageImage(deflated, PageImageEncoding.Bitonal, width, height, components: 1);
        }

        /// <summary>
        /// zlib-wrapped deflate, which is what PDF's <c>/FlateDecode</c> expects (a raw deflate
        /// stream with no zlib header is rejected by strict readers).
        /// </summary>
        /// <remarks>
        /// CCITT G4 would compress a scanned page harder still, and
        /// <c>third_party/PdfSharpCore/Pdf.Advanced/PdfImage.FaxEncode.cs</c> even contains an
        /// implementation of it — 840 lines with no call site anywhere in the tree, i.e. never once
        /// exercised. Flate over packed 1-bit is simpler, predictable, and already ~20x smaller
        /// than the 24-bit RGB it replaces; the remaining margin is not worth commissioning dead
        /// code on the save path.
        /// </remarks>
        internal static byte[] Deflate(byte[] raw)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }

        // ── JPEG pages ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Encodes a PDFium BGRA page raster as a baseline JPEG, ready to embed as
        /// <c>/DCTDecode</c> <c>/DeviceRGB</c>.
        /// </summary>
        /// <remarks>
        /// ImageSharp rather than GDI+, matching <see cref="PdfPageRasterizer"/>: it runs on every
        /// platform this repository is checked out on, so the encode is actually exercised by the
        /// test suite instead of shipping unverified.
        ///
        /// The buffer is composited over white FIRST. PDFium leaves unpainted background at alpha 0
        /// and JPEG has no alpha channel, so an uncomposited raster encodes as a solid BLACK page —
        /// the single most spectacular way to get this wrong. Done in place, because a full-page
        /// raster is large enough that a second copy per page is worth avoiding.
        /// </remarks>
        internal static EncodedPageImage EncodeJpeg(byte[] bgra, int width, int height)
        {
            CompositeOverWhite(bgra);

            using var image = Image.LoadPixelData<Bgra32>(bgra, width, height);
            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms, new JpegEncoder { Quality = JpegQuality });
            return new EncodedPageImage(ms.ToArray(), PageImageEncoding.Jpeg, width, height, components: 3);
        }

        /// <summary>
        /// Composites a straight-alpha BGRA buffer over opaque white, in place, exactly
        /// (a = 0 gives white, a = 255 gives the source, and the partial coverage in between is
        /// interpolated rather than snapped).
        /// </summary>
        internal static void CompositeOverWhite(byte[] bgra)
        {
            for (int i = 0; i + 3 < bgra.Length; i += 4)
            {
                byte a = bgra[i + 3];
                if (a == 255) continue;                 // already opaque — the common case
                if (a == 0)
                {
                    bgra[i] = 255; bgra[i + 1] = 255; bgra[i + 2] = 255; bgra[i + 3] = 255;
                    continue;
                }
                int inv = 255 - a;
                bgra[i]     = (byte)((bgra[i]     * a + 255 * inv + 127) / 255);
                bgra[i + 1] = (byte)((bgra[i + 1] * a + 255 * inv + 127) / 255);
                bgra[i + 2] = (byte)((bgra[i + 2] * a + 255 * inv + 127) / 255);
                bgra[i + 3] = 255;
            }
        }

        /// <summary>
        /// The bytes of <paramref name="path"/> unchanged, when the file is a JPEG that PDF's
        /// <c>/DCTDecode</c> can consume verbatim; <c>null</c> for anything else, including a JPEG
        /// variant that is not safe to pass through.
        /// </summary>
        /// <remarks>
        /// Used by image import, where the source is the user's own file and the best thing we can
        /// possibly do with it is nothing at all: no decode, no re-encode, no generation loss, and
        /// the page ends up the size of the photo instead of several megabytes of 24-bit
        /// FlateDecode.
        ///
        /// The sniff is on the file's actual markers, not its extension, and it is deliberately
        /// narrow. <c>/DCTDecode</c> is specified over baseline and extended-sequential Huffman
        /// JPEG at 8 bits; a progressive (SOF2), arithmetic-coded (SOF9+), lossless or 12-bit file
        /// is out of scope and renders as a blank or garbled page in strict viewers. Four-component
        /// (CMYK/YCCK) files are refused too: they need <c>/DeviceCMYK</c> and, when Adobe's APP14
        /// marker says the data is inverted, a <c>/Decode</c> array to go with it — a guess that is
        /// wrong half the time and produces a photographic negative. All of those simply fall back
        /// to the existing lossless re-encode, which is slower and bigger but always right.
        /// </remarks>
        internal static EncodedPageImage? TryReadPassThroughJpeg(string path)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch { return null; }

            if (!TryReadJpegFrame(bytes, out int width, out int height, out int components)) return null;
            return new EncodedPageImage(bytes, PageImageEncoding.Jpeg, width, height, components);
        }

        /// <summary>
        /// Walks a JPEG's marker segments to its frame header. True only for a single-frame,
        /// 8-bit, Huffman-coded, sequential JPEG of 1 or 3 components.
        /// </summary>
        internal static bool TryReadJpegFrame(byte[] bytes, out int width, out int height, out int components)
        {
            width = height = components = 0;
            if (bytes is null || bytes.Length < 4) return false;
            if (bytes[0] != 0xFF || bytes[1] != 0xD8) return false;   // SOI

            int i = 2;
            while (i + 3 < bytes.Length)
            {
                if (bytes[i] != 0xFF) return false;                   // lost sync — do not guess
                byte marker = bytes[i + 1];

                if (marker == 0xFF) { i++; continue; }                // fill byte before a marker
                // Standalone markers carry no length field.
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    i += 2;
                    continue;
                }
                // Reached the entropy-coded scan (or the end) without an acceptable frame header.
                if (marker == 0xDA || marker == 0xD9) return false;

                int length = (bytes[i + 2] << 8) | bytes[i + 3];
                if (length < 2 || (long)i + 2 + length > bytes.Length) return false;

                // SOF0 baseline, SOF1 extended sequential — both Huffman, both in scope.
                if (marker == 0xC0 || marker == 0xC1)
                {
                    if (length < 8) return false;
                    int precision = bytes[i + 4];
                    height = (bytes[i + 5] << 8) | bytes[i + 6];
                    width = (bytes[i + 7] << 8) | bytes[i + 8];
                    components = bytes[i + 9];
                    return precision == 8 && (components == 1 || components == 3)
                           && width > 0 && height > 0;
                }

                // Any other SOFn — progressive, lossless, differential, arithmetic — is refused.
                // 0xC4 is DHT, 0xC8 is reserved and 0xCC is DAC; those are not frame headers.
                if (marker >= 0xC2 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    return false;

                i += 2 + length;
            }
            return false;
        }

        // ── Writing the page ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Paints <paramref name="image"/> across the whole of <paramref name="page"/>, by
        /// whichever route its encoding requires.
        /// </summary>
        /// <remarks>
        /// <paramref name="widthPt"/>/<paramref name="heightPt"/> are passed rather than read off
        /// the page so that a caller which has just rewritten the page boxes (redaction) and one
        /// which has just created the page (flatten, import) can both say plainly what the image is
        /// being stretched to.
        ///
        /// Must run on a single thread: PdfSharpCore document mutation is not thread-safe, so Save
        /// Flattened does its rasterising in parallel and every call to this in its ordered
        /// assembly pass.
        /// </remarks>
        internal static void PaintFullPage(PdfDocument doc, PdfPage page, EncodedPageImage image,
                                           double widthPt, double heightPt)
        {
            if (image.Encoding == PageImageEncoding.Png)
            {
                byte[] png = image.Bytes;
                using var xi = XImage.FromStream(() => new MemoryStream(png));
                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(xi, 0, 0, widthPt, heightPt);
                return;
            }

            string filter = image.Encoding == PageImageEncoding.Jpeg ? "/DCTDecode" : "/FlateDecode";
            string colorSpace = image.Components == 1 ? "/DeviceGray" : "/DeviceRGB";
            int bitsPerComponent = image.Encoding == PageImageEncoding.Bitonal ? 1 : 8;

            WriteImageXObject(doc, page, image.Bytes, filter, colorSpace, bitsPerComponent,
                              image.PixelWidth, image.PixelHeight, widthPt, heightPt);
        }

        /// <summary>
        /// Gives the page one full-bleed image XObject and a four-operator content stream that
        /// draws it.
        /// </summary>
        /// <remarks>
        /// Written by hand rather than through <c>XGraphics</c> because <c>XGraphics</c> cannot
        /// express either of the things this exists for: it re-encodes JPEG input (PdfSharpCore's
        /// image source decodes and saves again at quality 75) and it has no 1-bit image path at
        /// all. Handing it the bytes directly is also the only way the image in the file is the
        /// image we encoded.
        ///
        /// The stream is stored exactly as given and <c>/Filter</c> merely DESCRIBES it —
        /// PdfSharpCore's <c>CreateStream</c> does no compression of its own — so the caller owns
        /// deflating the 1-bit payload. The <c>cm</c> matrix maps the unit square to the whole
        /// page, which is what makes the image fill it exactly whatever its pixel dimensions.
        /// </remarks>
        private static void WriteImageXObject(
            PdfDocument doc, PdfPage page, byte[] data,
            string filter, string colorSpace, int bitsPerComponent,
            int pixelWidth, int pixelHeight, double widthPt, double heightPt)
        {
            var image = new PdfDictionary(doc);
            image.Elements["/Type"] = new PdfName("/XObject");
            image.Elements["/Subtype"] = new PdfName("/Image");
            image.Elements["/Width"] = new PdfInteger(pixelWidth);
            image.Elements["/Height"] = new PdfInteger(pixelHeight);
            image.Elements["/ColorSpace"] = new PdfName(colorSpace);
            image.Elements["/BitsPerComponent"] = new PdfInteger(bitsPerComponent);
            image.Elements["/Filter"] = new PdfName(filter);
            image.CreateStream(data);
            doc.Internals.AddObject(image);

            var xobjects = new PdfDictionary(doc);
            xobjects.Elements["/Im0"] = image.Reference;
            var resources = new PdfDictionary(doc);
            resources.Elements["/XObject"] = xobjects;
            page.Elements["/Resources"] = resources;

            string ops = string.Create(CultureInfo.InvariantCulture,
                $"q\n{widthPt:0.####} 0 0 {heightPt:0.####} 0 0 cm\n/Im0 Do\nQ\n");
            var content = new PdfDictionary(doc);
            content.CreateStream(Encoding.ASCII.GetBytes(ops));
            doc.Internals.AddObject(content);
            page.Elements["/Contents"] = content.Reference;
        }
    }
}
