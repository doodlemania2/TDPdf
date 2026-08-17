using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;

namespace TDPdf.Services
{
    /// <summary>
    /// The one page-raster → image-file pipeline, shared by the CLI's <c>--to-image</c> command
    /// and the GUI's File ▸ Export Pages as Images… (upstream KillerPDF #132).
    /// </summary>
    /// <remarks>
    /// The two entry points differ only in how they produce the render source: the CLI reads the
    /// file on disk (optionally decrypting it first), while the GUI first burns pending
    /// annotations and in-app rotations into a temp copy. Everything after that — the accepted
    /// DPI window, the PDFium rasterize, the composite-over-white encode and the
    /// <c>&lt;base&gt;-page-NNN.&lt;ext&gt;</c> naming — lives here, so a fix on one side can never
    /// leave the other behind.
    /// </remarks>
    internal static class PageImageExporter
    {
        /// <summary>Lowest accepted render resolution (below this, pages are unreadable).</summary>
        internal const double MinDpi = 24;

        /// <summary>Highest accepted render resolution (a Letter page is ~140 MP of BGRA here).</summary>
        internal const double MaxDpi = 1200;

        /// <summary>Screen-quality default, matching Save Flattened.</summary>
        internal const double DefaultDpi = 150;

        internal const string PngFormat = "png";
        internal const string JpgFormat = "jpg";

        /// <summary>
        /// Parses a user- or CLI-supplied DPI. Returns false (leaving <paramref name="dpi"/> at
        /// <see cref="DefaultDpi"/>) when the text is unparsable or outside [24, 1200].
        /// </summary>
        internal static bool TryParseDpi(string? text, out double dpi)
        {
            if (text != null && double.TryParse(text.Trim(), out double parsed) &&
                parsed >= MinDpi && parsed <= MaxDpi)
            {
                dpi = parsed;
                return true;
            }
            dpi = DefaultDpi;
            return false;
        }

        /// <summary>
        /// Normalizes a format name to <c>png</c> or <c>jpg</c> ("jpeg" is accepted as an alias).
        /// Returns false for anything else.
        /// </summary>
        internal static bool TryNormalizeFormat(string? raw, out string format)
        {
            format = (raw ?? PngFormat).ToLowerInvariant();
            if (format == "jpeg") format = JpgFormat;
            return format == PngFormat || format == JpgFormat;
        }

        /// <summary>Page count of a PDF, read through the same PDFium reader the export uses.</summary>
        internal static int GetPageCount(string path)
        {
            using var docReader = DocLib.Instance.GetDocReader(path, new PageDimensions(1));
            return docReader.GetPageCount();
        }

        /// <summary>Zero-padding width for page numbers: at least 3 digits, more for huge documents.</summary>
        internal static int PageNumberDigits(int pageCount) => Math.Max(3, pageCount.ToString().Length);

        /// <summary>Builds the per-page output name, e.g. <c>report-page-001.png</c>.</summary>
        internal static string PageFileName(string baseName, int pageIndex, int digits, string format)
            => $"{baseName}-page-{(pageIndex + 1).ToString().PadLeft(digits, '0')}.{format}";

        /// <summary>
        /// Encodes a raw PDFium BGRA page buffer to PNG or JPEG via WPF's codecs.
        /// </summary>
        /// <remarks>
        /// PDFium hands back unpainted background as BGRA 0,0,0,0. Unless the caller explicitly
        /// asked to keep that alpha (<c>--transparent</c>, PNG only), the buffer is composited
        /// over white in place and described to WIC as Bgr32 — same 4-byte layout, alpha channel
        /// ignored — so the encoded image carries no alpha at all. Mutates
        /// <paramref name="bgra"/> when <paramref name="transparent"/> is false.
        ///
        /// TDPdf patch (#188, from upstream KillerPDF): <paramref name="dpi"/> is written into the
        /// file's resolution metadata (PNG pHYs / JPEG JFIF density), so an image exported at, say,
        /// 300 dpi no longer claims 96 dpi and prints or places at the wrong physical size. It does
        /// not change the pixel dimensions — the caller already rendered at that resolution. The
        /// 96 default keeps non-export callers (the CLI flatten, which redraws the PNG at an
        /// explicit point size) byte-identical.
        /// </remarks>
        internal static byte[] Encode(byte[] bgra, int width, int height, string format, bool transparent,
            double dpi = 96)
        {
            if (!transparent) PdfDocumentService.CompositeBgraOverWhite(bgra);

            var bmp = BitmapSource.Create(width, height, dpi, dpi,
                transparent ? PixelFormats.Bgra32 : PixelFormats.Bgr32, null, bgra, width * 4);
            BitmapEncoder encoder = format == JpgFormat
                ? new JpegBitmapEncoder { QualityLevel = 90 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        /// <summary>
        /// Rasterizes the requested pages of <paramref name="renderPath"/> at
        /// <paramref name="dpi"/> and writes one image file per page into
        /// <paramref name="outputDir"/>. Returns the number of files written.
        /// </summary>
        /// <param name="pageIndices">
        /// 0-based page indices to export, or null for every page. Out-of-range entries are skipped.
        /// </param>
        /// <param name="report">
        /// Optional progress callback invoked as (pageNumberInJob, totalPagesInJob) before each
        /// page is rendered. Called on the calling thread — marshal to the UI thread yourself.
        /// </param>
        /// <remarks>
        /// Cancellation is cooperative and non-throwing: the loop stops at the next page boundary
        /// and the already-written files are left in place, so callers can report "N of M written".
        /// PDFium is not thread-safe, so this renders pages serially on the calling thread; run it
        /// off the UI thread.
        /// </remarks>
        internal static int Export(
            string renderPath,
            string outputDir,
            string baseName,
            IReadOnlyList<int>? pageIndices,
            double dpi,
            string format,
            bool transparent,
            Action<int, int>? report,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(outputDir);

            using var docReader = DocLib.Instance.GetDocReader(renderPath, new PageDimensions(dpi / 72.0));
            int pageCount = docReader.GetPageCount();
            var pages = pageIndices ?? [.. Enumerable.Range(0, pageCount)];
            int digits = PageNumberDigits(pageCount);

            int written = 0, position = 0;
            foreach (int idx in pages)
            {
                if (cancellationToken.IsCancellationRequested) break;
                position++;
                if (idx < 0 || idx >= pageCount) continue;
                report?.Invoke(position, pages.Count);

                byte[] raw; int w, h;
                using (var pageReader = docReader.GetPageReader(idx))
                {
                    w = pageReader.GetPageWidth();
                    h = pageReader.GetPageHeight();
                    // #141: with annotations — an exported image should show the markup the file
                    // carries, the same as the page does on screen. The background policy is the
                    // caller's: PDFium fills white unless --transparent asked for the alpha, which
                    // is exactly what Encode() below then expects (it composites only when opaque).
                    raw = PdfiumInterop.RenderPageWithAnnotations(renderPath, idx, w, h, transparent)
                          ?? pageReader.GetImage();
                }
                if (raw is null || raw.Length == 0 || w <= 0 || h <= 0) continue;

                var bytes = Encode(raw, w, h, format, transparent, dpi);
                File.WriteAllBytes(Path.Combine(outputDir, PageFileName(baseName, idx, digits, format)), bytes);
                written++;
            }
            return written;
        }
    }
}
