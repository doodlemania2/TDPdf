using System.Globalization;
using System.IO;
using System.Text;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace TDPdf.Services
{
    internal sealed record DjvuConvertOptions
    {
        /// <summary>Pages larger than this are rendered smaller (the page keeps its size on paper).</summary>
        internal long MaxPagePixels { get; init; } = 100_000_000;
        internal int JpegQuality { get; init; } = 85;
    }

    /// <summary>
    /// Writes a DjVu book as a PDF: one page per DjVu page holding the rendered picture full-bleed, the hidden
    /// text as invisible text over it (the way the OCR export does it, so search and copy work), and the
    /// book's bookmarks as the outline. Bitonal pages are kept as 1-bit Flate, grey and colour pages as JPEG.
    /// </summary>
    internal static class DjvuToPdf
    {
        internal static BookConversionResult Convert(IDjvuDecoder decoder, string outPath, DjvuConvertOptions? options,
            IProgress<(int Page, int Total)>? progress, CancellationToken ct)
        {
            options ??= new DjvuConvertOptions();
            int total = decoder.PageCount;
            if (total < 1) throw new BookConversionException("This DjVu book has no pages.");

            var notices = new List<string>();
            var failed = new List<int>();
            int rotatedNoText = 0, pagesWithText = 0, reduced = 0;
            var fonts = new Dictionary<int, XFont>();
            XFont FontFor(double size)
            {
                int key = Math.Max(10, (int)Math.Round(size * 10));
                if (!fonts.TryGetValue(key, out var f))
                {
                    try { f = new XFont("Arial", key / 10.0, XFontStyle.Regular); }
                    catch { f = new XFont("Segoe UI", key / 10.0, XFontStyle.Regular); }
                    fonts[key] = f;
                }
                return f;
            }
            var invisible = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));

            using var doc = new PdfDocument();
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((i, total));
                var info = decoder.GetPageInfo(i);
                int dpi = info.Dpi is >= 50 and <= 2400 ? info.Dpi : 300;
                double wPt = Math.Clamp(info.Width * 72.0 / dpi, 36, 14400);
                double hPt = Math.Clamp(info.Height * 72.0 / dpi, 36, 14400);

                DjvuRaster? raster = null;
                try { raster = decoder.Render(i, options.MaxPagePixels, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is InvalidDataException or IOException or TimeoutException)
                {
                    failed.Add(i + 1);
                }

                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(wPt);
                page.Height = XUnit.FromPoint(hPt);
                if (raster is null) continue;   // a blank page of the right size keeps the numbering true
                if ((long)raster.Width * raster.Height < (long)info.Width * info.Height) reduced++;

                WriteImage(doc, page, raster, wPt, hPt, options.JpegQuality);

                // Text. DjVu words are in pixels of the page at its own size; a page the renderer turned
                // (rotated in the file) comes back with its sides swapped and gets no text, because the text
                // boxes are not rotated with it.
                IReadOnlyList<DjvuWord> words;
                try { words = decoder.GetWords(i); } catch (Exception ex) when (ex is InvalidDataException or IOException) { words = Array.Empty<DjvuWord>(); }
                if (words.Count == 0) continue;
                bool swapped = (raster.Width > raster.Height) != (info.Width > info.Height) && info.Width != info.Height
                               && Math.Abs((double)raster.Width / raster.Height - (double)info.Height / info.Width) < 0.02;
                if (swapped) { rotatedNoText++; continue; }

                double sx = wPt / info.Width, sy = hPt / info.Height;
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                var state = gfx.Save();
                gfx.SetPdfTextRenderMode(3);
                bool any = false;
                foreach (var w in words)
                {
                    try
                    {
                        double top = w.Top * sy, bottom = w.Bottom * sy, left = w.Left * sx, right = w.Right * sx;
                        var probe = FontFor(10);
                        double ascent = probe.Metrics.Ascent * 1000.0 / Math.Max(1, probe.Metrics.UnitsPerEm);
                        double descent = probe.Metrics.Descent * 1000.0 / Math.Max(1, probe.Metrics.UnitsPerEm);
                        double size = OcrTextFit.FontSizeFor(Math.Max(1, bottom - top), ascent, descent);
                        var font = FontFor(size);
                        double baseline = OcrTextFit.BaselineY(bottom, font.Size, descent);
                        double hScale = OcrTextFit.HScale(right - left, gfx.MeasureString(w.Text, font).Width);
                        var ws = gfx.Save();
                        gfx.TranslateTransform(left, baseline);
                        gfx.ScaleTransform(hScale, 1);
                        gfx.DrawString(w.Text, font, invisible, 0, 0, XStringFormats.BaseLineLeft);
                        gfx.Restore(ws);
                        any = true;
                    }
                    catch { /* one word that will not lay out does not cost the page its text */ }
                }
                gfx.Restore(state);
                if (any) pagesWithText++;
            }
            progress?.Report((total, total));

            // Bookmarks.
            try
            {
                var nodes = ToNodes(decoder.GetOutline(), total);
                if (nodes.Count > 0) BookOutline.Apply(doc, nodes);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { /* a book without readable bookmarks opens without them */ }

            ct.ThrowIfCancellationRequested();
            doc.Save(outPath);

            if (failed.Count > 0)
                notices.Add($"{failed.Count} page(s) could not be read and are blank: " + string.Join(", ", failed.Take(12)) + (failed.Count > 12 ? ", ..." : "") + ".");
            if (rotatedNoText > 0) notices.Add($"{rotatedNoText} turned page(s) have no searchable text.");
            if (reduced > 0) notices.Add($"{reduced} very large page(s) were stored at a lower resolution.");
            return new BookConversionResult(total, notices);
        }

        private static List<OutlineNode> ToNodes(IReadOnlyList<DjvuOutlineItem> items, int pageCount)
        {
            int last = 0;
            var result = new List<OutlineNode>();
            void Fill(IEnumerable<DjvuOutlineItem> src, List<OutlineNode> into, int depth)
            {
                foreach (var it in src)
                {
                    if (depth > 12) return;
                    int page = Math.Clamp(it.PageIndex ?? last, 0, pageCount - 1);
                    last = page;
                    string title = string.IsNullOrWhiteSpace(it.Title) ? "Untitled" : it.Title.Trim();
                    var node = new OutlineNode { Title = title.Length > 200 ? title[..200] : title, PageIndex = page };
                    Fill(it.Children, node.Children, depth + 1);
                    into.Add(node);
                }
            }
            Fill(items, result, 0);
            return result;
        }

        // ── the page picture ───────────────────────────────────────────────────────────────────

        private static void WriteImage(PdfDocument doc, PdfPage page, DjvuRaster r, double wPt, double hPt, int quality)
        {
            var image = new PdfDictionary(doc);
            image.Elements["/Type"] = new PdfName("/XObject");
            image.Elements["/Subtype"] = new PdfName("/Image");
            image.Elements["/Width"] = new PdfInteger(r.Width);
            image.Elements["/Height"] = new PdfInteger(r.Height);
            byte[] bytes;
            switch (r.Kind)
            {
                case DjvuPageKind.Bitonal:
                    // PBM has 1 = black; PDF's DeviceGray has 1 = white. /Decode [1 0] says so without
                    // touching the bits (and leaves the padding bits at the end of each row harmless).
                    image.Elements["/ColorSpace"] = new PdfName("/DeviceGray");
                    image.Elements["/BitsPerComponent"] = new PdfInteger(1);
                    image.Elements["/Decode"] = new PdfArray(doc, new PdfInteger(1), new PdfInteger(0));
                    image.Elements["/Filter"] = new PdfName("/FlateDecode");
                    bytes = PdfPageImageEncoder.Deflate(r.Data);
                    break;
                case DjvuPageKind.Gray:
                {
                    // Grey is stored as an RGB JPEG with three equal channels: the encoder's one-channel mode
                    // is not used, and the chroma planes of a grey picture cost next to nothing.
                    var rgb = new byte[checked(r.Data.Length * 3)];
                    for (int k = 0; k < r.Data.Length; k++) rgb[3 * k] = rgb[3 * k + 1] = rgb[3 * k + 2] = r.Data[k];
                    image.Elements["/ColorSpace"] = new PdfName("/DeviceRGB");
                    image.Elements["/BitsPerComponent"] = new PdfInteger(8);
                    image.Elements["/Filter"] = new PdfName("/DCTDecode");
                    using var img = Image.LoadPixelData<Rgb24>(rgb, r.Width, r.Height);
                    using var ms = new MemoryStream();
                    img.SaveAsJpeg(ms, new JpegEncoder { Quality = quality });
                    bytes = ms.ToArray();
                    break;
                }
                default:
                    image.Elements["/ColorSpace"] = new PdfName("/DeviceRGB");
                    image.Elements["/BitsPerComponent"] = new PdfInteger(8);
                    image.Elements["/Filter"] = new PdfName("/DCTDecode");
                    using (var img = Image.LoadPixelData<Rgb24>(r.Data, r.Width, r.Height))
                    using (var ms = new MemoryStream())
                    {
                        img.SaveAsJpeg(ms, new JpegEncoder { Quality = quality });
                        bytes = ms.ToArray();
                    }
                    break;
            }
            image.CreateStream(bytes);
            doc.Internals.AddObject(image);

            var xobjects = new PdfDictionary(doc);
            xobjects.Elements["/Im0"] = image.Reference;
            var resources = new PdfDictionary(doc);
            resources.Elements["/XObject"] = xobjects;
            page.Elements["/Resources"] = resources;

            string ops = string.Create(CultureInfo.InvariantCulture, $"q\n{wPt:0.####} 0 0 {hPt:0.####} 0 0 cm\n/Im0 Do\nQ\n");
            var content = new PdfDictionary(doc);
            content.CreateStream(Encoding.ASCII.GetBytes(ops));
            doc.Internals.AddObject(content);
            page.Elements["/Contents"] = content.Reference;
        }
    }
}
