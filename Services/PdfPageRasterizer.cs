using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using PdfSharpCore.Pdf.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace TDPdf.Services
{
    /// <summary>
    /// Replaces a page with a picture of itself, with chosen areas painted out.
    /// </summary>
    /// <remarks>
    /// The fallback half of redaction, and the only thing that works for the two cases object
    /// removal cannot serve:
    ///
    ///   * <b>Scanned pages.</b> The page is one page-sized image and the mark merely straddles it.
    ///     Deleting the image would blank the page; there is nothing smaller to delete.
    ///   * <b>Pages PDFium's content generator would damage</b> — spot colour, gradients, inline
    ///     images, soft masks. See <see cref="PdfContentInspector"/>. Rewriting the stream there
    ///     silently recolours or drops content the user never marked.
    ///
    /// What it costs is real and has to be said out loud to the user rather than buried: the page
    /// stops being text. It cannot be searched, selected, copied, or read by a screen reader
    /// afterwards. That is a genuine loss, and it is why this is offered rather than applied
    /// silently.
    ///
    /// What it buys is certainty. The output page contains one JPEG and nothing else — no text
    /// objects, no image XObjects from the original, no leftover content stream — so there is no
    /// object left holding the redacted content, whatever the original page was made of.
    ///
    /// TWO DELIBERATE DEPARTURES FROM Save Flattened, which also rasterises pages:
    ///   * <b>ImageSharp, not System.Drawing.</b> The flatten path encodes through GDI+, which is
    ///     Windows-only, so it cannot be exercised on the machines this repository lives on.
    ///     Redaction is the one feature where an untested encode is unacceptable, and ImageSharp is
    ///     already in the tree (PdfSharpCore depends on it) and runs anywhere.
    ///   * <b>JPEG, not PNG.</b> Flatten converts a whole document at 150 dpi; this converts the
    ///     one or two affected pages at 200, where PNG on a colour scan runs to several megabytes a
    ///     page. Lossy is also not a drawback here: the point is that the original pixels are gone.
    /// </remarks>
    internal static class PdfPageRasterizer
    {
        /// <summary>Enough to keep 8pt text legible without turning a page into megabytes.</summary>
        internal const int DefaultDpi = 200;
        private const int JpegQuality = 90;

        /// <summary>
        /// Copies <paramref name="srcPath"/> to <paramref name="destPath"/>, replacing each page in
        /// <paramref name="pages"/> with a rasterised copy that has its <paramref name="blackout"/>
        /// rectangles painted solid black.
        /// </summary>
        internal static bool TryReplacePages(
            string srcPath, string destPath,
            IReadOnlyDictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> blackout,
            IReadOnlyCollection<int> pages,
            int dpi,
            out string? error)
        {
            error = null;
            if (pages.Count == 0)
            {
                File.Copy(srcPath, destPath, overwrite: true);
                return true;
            }

            try
            {
                using var doc = PdfReader.Open(srcPath, PdfDocumentOpenMode.Modify);

                foreach (int pageIndex in pages.Distinct().OrderBy(i => i))
                {
                    if (pageIndex < 0 || pageIndex >= doc.PageCount) continue;
                    var page = doc.Pages[pageIndex];

                    var (wPt, hPt) = PdfPageGeometry.DisplaySize(page);
                    if (wPt <= 0 || hPt <= 0)
                    {
                        error = $"page {pageIndex + 1} has no usable size";
                        return false;
                    }

                    int pxW = Math.Max(1, (int)Math.Round(wPt * dpi / 72.0));
                    int pxH = Math.Max(1, (int)Math.Round(hPt * dpi / 72.0));

                    // Render from the FILE rather than from the in-memory document: this is the same
                    // renderer that drew the page the user was looking at when they placed the
                    // marks, so what gets painted out is what they saw.
                    byte[]? bgra = PdfiumInterop.RenderPageWithAnnotations(srcPath, pageIndex, pxW, pxH);
                    if (bgra is null || bgra.Length < checked(pxW * pxH * 4))
                    {
                        error = $"page {pageIndex + 1} could not be rendered";
                        return false;
                    }

                    if (blackout.TryGetValue(pageIndex, out var rects))
                        foreach (var r in rects)
                        {
                            var (x, y, w, h) = PdfPageGeometry.PdfRectToCanvas(page, r, pxW, pxH);
                            PaintBlack(bgra, pxW, pxH, x, y, w, h);
                        }

                    byte[] jpeg = EncodeJpeg(bgra, pxW, pxH);
                    ReplacePageContent(doc, page, jpeg, pxW, pxH, wPt, hPt);
                }

                doc.Save(destPath);
                return File.Exists(destPath);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void PaintBlack(byte[] bgra, int width, int height, int x, int y, int w, int h)
        {
            int x0 = Math.Clamp(x, 0, width);
            int y0 = Math.Clamp(y, 0, height);
            int x1 = Math.Clamp(x + w, 0, width);
            int y1 = Math.Clamp(y + h, 0, height);

            for (int row = y0; row < y1; row++)
            {
                int at = (row * width + x0) * 4;
                for (int col = x0; col < x1; col++)
                {
                    bgra[at] = 0; bgra[at + 1] = 0; bgra[at + 2] = 0; bgra[at + 3] = 255;
                    at += 4;
                }
            }
        }

        private static byte[] EncodeJpeg(byte[] bgra, int width, int height)
        {
            // PDFium leaves unpainted background at alpha 0; JPEG has no alpha, so anything not
            // composited first encodes as black. The render above fills opaque white before
            // drawing, so the buffer is already composited — but a page can still carry
            // transparent regions of its own, and a redaction is not the place to discover that.
            for (int i = 3; i < bgra.Length; i += 4)
            {
                if (bgra[i] == 255) continue;
                double a = bgra[i] / 255.0;
                bgra[i - 3] = (byte)(bgra[i - 3] * a + 255 * (1 - a));
                bgra[i - 2] = (byte)(bgra[i - 2] * a + 255 * (1 - a));
                bgra[i - 1] = (byte)(bgra[i - 1] * a + 255 * (1 - a));
                bgra[i] = 255;
            }

            using var image = Image.LoadPixelData<Bgra32>(bgra, width, height);
            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms, new JpegEncoder { Quality = JpegQuality });
            return ms.ToArray();
        }

        /// <summary>
        /// Throws away everything the page was made of and gives it a single full-bleed image.
        /// </summary>
        /// <remarks>
        /// The content stream is written by hand — four operators — rather than through XGraphics,
        /// for two reasons that both matter here.
        ///
        /// First, PdfSharpCore's <c>XGraphicsPdfPageOptions.Replace</c> does not replace: the value
        /// is stored on the renderer and never acted on, so it APPENDS. On this page that would
        /// leave the original text sitting underneath an opaque picture — a redaction that is
        /// perfectly readable to anyone who opens the file in anything but a viewer. That is the
        /// exact failure this feature exists to prevent, so the replacement is done here, by
        /// deleting the keys.
        ///
        /// Second, deleting /Resources is what actually removes the old content from the FILE. The
        /// original text lives in the content stream and the original scan in an image XObject
        /// under /Resources; orphaning both is what lets PdfSharpCore's save-time compaction (which
        /// keeps only what is reachable from the trailer) drop them. Clearing /Contents alone would
        /// leave the scan in the file, still holding everything that was redacted.
        ///
        /// /Rotate goes too, and the boxes are rewritten to the DISPLAYED size: the raster already
        /// has the rotation baked in, so leaving /Rotate would turn the page a second time.
        /// </remarks>
        private static void ReplacePageContent(
            PdfDocument doc, PdfPage page, byte[] jpeg, int pxW, int pxH, double wPt, double hPt)
        {
            foreach (string key in new[]
                     { "/Contents", "/Resources", "/Annots", "/Group", "/Rotate",
                       "/CropBox", "/BleedBox", "/TrimBox", "/ArtBox", "/Metadata", "/PieceInfo" })
                page.Elements.Remove(key);

            page.Elements["/MediaBox"] = Box(0, 0, wPt, hPt);

            var image = new PdfDictionary(doc);
            image.Elements["/Type"] = new PdfName("/XObject");
            image.Elements["/Subtype"] = new PdfName("/Image");
            image.Elements["/Width"] = new PdfInteger(pxW);
            image.Elements["/Height"] = new PdfInteger(pxH);
            image.Elements["/ColorSpace"] = new PdfName("/DeviceRGB");
            image.Elements["/BitsPerComponent"] = new PdfInteger(8);
            image.Elements["/Filter"] = new PdfName("/DCTDecode");
            image.CreateStream(jpeg);
            doc.Internals.AddObject(image);

            var xobjects = new PdfDictionary(doc);
            xobjects.Elements["/Im0"] = image.Reference;
            var resources = new PdfDictionary(doc);
            resources.Elements["/XObject"] = xobjects;
            page.Elements["/Resources"] = resources;

            // The unit square maps to the whole page, so the image fills it exactly.
            string ops = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"q\n{wPt:0.####} 0 0 {hPt:0.####} 0 0 cm\n/Im0 Do\nQ\n");
            var content = new PdfDictionary(doc);
            content.CreateStream(Encoding.ASCII.GetBytes(ops));
            doc.Internals.AddObject(content);
            page.Elements["/Contents"] = content.Reference;
        }

        private static PdfArray Box(double x1, double y1, double x2, double y2)
        {
            var a = new PdfArray();
            a.Elements.Add(new PdfReal(x1));
            a.Elements.Add(new PdfReal(y1));
            a.Elements.Add(new PdfReal(x2));
            a.Elements.Add(new PdfReal(y2));
            return a;
        }
    }
}
