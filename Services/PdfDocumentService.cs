using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using TDPdf.Diagnostics;

using DrawingBitmap = System.Drawing.Bitmap;
using DrawingRectangle = System.Drawing.Rectangle;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace TDPdf.Services
{
    internal sealed class PdfDocumentService
    {
        public Task<PdfOpenResult> OpenAsync(string path, string? password, CancellationToken cancellationToken)
        {
            return Task.Run(() => OpenCore(path, password, cancellationToken), cancellationToken);
        }

        public Task SaveAsync(Action saveAction, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                saveAction();
                cancellationToken.ThrowIfCancellationRequested();
            }, cancellationToken);
        }

        public Task<PdfDocument> OpenPdfSharpAsync(string path, PdfDocumentOpenMode mode, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = PdfReader.Open(path, mode);
                cancellationToken.ThrowIfCancellationRequested();
                return document;
            }, cancellationToken);
        }

        public Task SaveFlattenedAsync(string sourcePath, string destinationPath, IReadOnlyList<PdfPageSize> pageSizes, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                int pageCount = pageSizes.Count;

                // Rasterize pages across CPU cores. Docnet/PDFium (pdfium.dll) is NOT
                // thread-safe, so the page render is serialized behind a lock; the
                // CPU-bound PNG encode (GDI+) runs in parallel. Each page's encoded
                // bytes are stored by index so the PDF can be assembled in order.
                var pngPages = new byte[pageCount][];
                var docGate = new object();
                var po = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
                    CancellationToken = cancellationToken,
                };

                Parallel.For(0, pageCount, po, i =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Per-page pixel dimensions at 150 DPI, sized to the page box.
                    int pw = Math.Max(1, (int)(pageSizes[i].WidthPoint * 150 / 72.0));
                    int ph = Math.Max(1, (int)(pageSizes[i].HeightPoint * 150 / 72.0));
                    // PageDimensions requires dimOne <= dimTwo (short-edge, long-edge).
                    int dimMin = Math.Min(pw, ph);
                    int dimMax = Math.Max(pw, ph);

                    byte[] bgra; int rw, rh;
                    lock (docGate)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var pageDocReader = DocLib.Instance.GetDocReader(sourcePath, new PageDimensions(dimMin, dimMax));
                        using var pageReader = pageDocReader.GetPageReader(i);
                        bgra = pageReader.GetImage();
                        rw = pageReader.GetPageWidth();
                        rh = pageReader.GetPageHeight();
                    }

                    if (bgra == null || bgra.Length == 0 || rw <= 0 || rh <= 0) return;

                    // Encode BGRA to PNG (GDI+) outside the lock so it parallelizes.
                    pngPages[i] = EncodeBgraToPng(bgra, rw, rh);
                });

                cancellationToken.ThrowIfCancellationRequested();

                // Assemble the output PDF in page order (PdfSharpCore is single-threaded).
                using (var outDoc = new PdfDocument())
                {
                    for (int i = 0; i < pageCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var pngBytes = pngPages[i];
                        if (pngBytes == null) continue;

                        var newPage = outDoc.AddPage();
                        newPage.Width = pageSizes[i].WidthPoint;
                        newPage.Height = pageSizes[i].HeightPoint;
                        using (var xi = XImage.FromStream(() => new MemoryStream(pngBytes)))
                        using (var gfx = XGraphics.FromPdfPage(newPage))
                        {
                            gfx.DrawImage(xi, 0, 0, newPage.Width.Point, newPage.Height.Point);
                        }
                    }

                    outDoc.Save(destinationPath);
                }
            }, cancellationToken);
        }

        public Task<IReadOnlyList<BitmapSource?>> RenderThumbnailsAsync(string path, int pageCount, CancellationToken cancellationToken)
        {
            return Task.Run<IReadOnlyList<BitmapSource?>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var thumbnails = new List<BitmapSource?>(pageCount);
                using (var docReader = DocLib.Instance.GetDocReader(path, new PageDimensions(256, 256)))
                {
                    for (int i = 0; i < pageCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        thumbnails.Add(RenderPageBitmap(docReader, i));
                    }
                }
                return thumbnails;
            }, cancellationToken);
        }

        public Task<PdfRenderResult> RenderPageAsync(string path, int pageIndex, int dpiX, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                int safeDpiX = Math.Max(1, dpiX);
                double renderScale = safeDpiX / 96.0;
                int renderMax = Math.Max(1, (int)Math.Round(1536 * renderScale));
                using (var docReader = DocLib.Instance.GetDocReader(path, new PageDimensions(renderMax, renderMax)))
                using (var pageReader = docReader.GetPageReader(pageIndex))
                {
                    int width = pageReader.GetPageWidth();
                    int height = pageReader.GetPageHeight();
                    var rawBytes = pageReader.GetImage();
                    cancellationToken.ThrowIfCancellationRequested();

                    if (width <= 0 || height <= 0 || rawBytes == null || rawBytes.Length == 0)
                    {
                        return new PdfRenderResult(null, width, height, 0, 0);
                    }

                    // Round DIP dims once and derive bitmap DPI from them so the displayed
                    // canvas width is exactly dipW/dipH DIPs. Without this, PDFium's per-zoom
                    // pixel-rounding makes pixel/renderScale drift by ±1 DIP between zoom
                    // levels and placed annotations creep on every re-render.
                    int dipW = Math.Max(1, (int)Math.Round(width / renderScale));
                    int dipH = Math.Max(1, (int)Math.Round(height / renderScale));
                    double bitmapDpiX = 96.0 * width / dipW;
                    double bitmapDpiY = 96.0 * height / dipH;

                    var bitmap = new WriteableBitmap(width, height, bitmapDpiX, bitmapDpiY, PixelFormats.Bgra32, null);
                    bitmap.WritePixels(new Int32Rect(0, 0, width, height), rawBytes, width * 4, 0);
                    bitmap.Freeze();
                    return new PdfRenderResult(bitmap, width, height, dipW, dipH);
                }
            }, cancellationToken);
        }

        private static PdfOpenResult OpenCore(string path, string? password, CancellationToken cancellationToken)
        {
            PdfDocument? document = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (password is null)
                {
                    try
                    {
                        document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
                        PreloadDocnet(path, cancellationToken);
                        return new PdfOpenResult(document, path, path, false);
                    }
                    catch (Exception ex) when (IsOwnerPasswordException(ex))
                    {
                        document?.Close();
                        document = PdfReader.Open(path, PdfDocumentOpenMode.ReadOnly);
                        PreloadDocnet(path, cancellationToken);
                        return new PdfOpenResult(document, path, path, true);
                    }
                    catch (Exception ex) when (!IsPasswordException(ex))
                    {
                        // PdfSharpCore is a strict parser and rejects files
                        // that PDFium can still render — typically scanner /
                        // MFP output with minor spec deviations. Rather than
                        // hard-failing with "not a valid PDF document", try to
                        // recover by rasterizing every page through PDFium into
                        // a fresh, well-formed document the user can view, print
                        // and annotate. Re-throw the original error if PDFium
                        // can't read it either (genuinely corrupt / not a PDF).
                        document?.Close();
                        document = null;

                        PdfOpenResult? recovered = TryRecoverByRaster(path, ex, cancellationToken);
                        if (recovered is not null)
                            return recovered;
                        throw;
                    }
                }

                document = PdfReader.Open(path, password ?? string.Empty, PdfDocumentOpenMode.Modify);
                var tempDec = Path.Combine(Path.GetTempPath(), $"tdpdf_dec_{Guid.NewGuid():N}.pdf");
                document.Save(tempDec);
                document.Close();
                document = PdfReader.Open(tempDec, PdfDocumentOpenMode.Modify);
                PreloadDocnet(tempDec, cancellationToken);
                return new PdfOpenResult(document, path, tempDec, false);
            }
            catch
            {
                document?.Close();
                throw;
            }
        }

        // Heuristic: does this look like a password / encryption failure
        // (which the caller handles by prompting) rather than a malformed
        // file (which we try to recover by rasterizing)?
        private static bool IsPasswordException(Exception ex) =>
            ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("protected", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("encrypted", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Last-resort open path: PDFium reads the file and we re-emit each
        /// page as a 150-DPI image inside a new PdfSharpCore document. Returns
        /// <c>null</c> (so the caller re-throws the original parse error) when
        /// PDFium also fails to read the file.
        /// </summary>
        private static PdfOpenResult? TryRecoverByRaster(string path, Exception originalError, CancellationToken cancellationToken)
        {
            string? tempPath = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                const double dpi = 150.0;
                double scale = dpi / 72.0;

                tempPath = Path.Combine(Path.GetTempPath(), $"tdpdf_recover_{Guid.NewGuid():N}.pdf");

                using (var outDoc = new PdfDocument())
                using (var docReader = DocLib.Instance.GetDocReader(path, new PageDimensions(scale)))
                {
                    int pageCount = docReader.GetPageCount();
                    if (pageCount <= 0)
                        return null;

                    for (int i = 0; i < pageCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using (var pageReader = docReader.GetPageReader(i))
                        {
                            int pw = pageReader.GetPageWidth();
                            int ph = pageReader.GetPageHeight();
                            var bgra = pageReader.GetImage();
                            if (bgra == null || bgra.Length == 0 || pw <= 0 || ph <= 0)
                                continue;

                            var pngBytes = EncodeBgraToPng(bgra, pw, ph);
                            var newPage = outDoc.AddPage();
                            newPage.Width = pw / scale;   // px -> points
                            newPage.Height = ph / scale;
                            using (var xi = XImage.FromStream(() => new MemoryStream(pngBytes)))
                            using (var gfx = XGraphics.FromPdfPage(newPage))
                            {
                                gfx.DrawImage(xi, 0, 0, newPage.Width.Point, newPage.Height.Point);
                            }
                        }
                    }

                    if (outDoc.PageCount == 0)
                        return null;

                    outDoc.Save(tempPath);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var document = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
                PreloadDocnet(tempPath, cancellationToken);

                Telemetry.TrackEvent("File.OpenRecovered", new Dictionary<string, string>
                {
                    ["OriginalError"] = originalError.GetType().Name,
                    ["PageCount"]     = document.PageCount.ToString(),
                });

                return new PdfOpenResult(document, path, tempPath, openedReadOnly: false, recoveredFromRaster: true);
            }
            catch (OperationCanceledException)
            {
                TryDeleteQuiet(tempPath);
                throw;
            }
            catch
            {
                // PDFium couldn't read it either — clean up and let the
                // caller surface the original "not a valid PDF" error.
                TryDeleteQuiet(tempPath);
                return null;
            }
        }

        private static void TryDeleteQuiet(string? file)
        {
            if (string.IsNullOrEmpty(file)) return;
            try { if (File.Exists(file)) File.Delete(file); }
            catch { /* best-effort temp cleanup */ }
        }

        private static void PreloadDocnet(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (DocLib.Instance.GetDocReader(path, new PageDimensions(256, 256)))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        private static BitmapSource? RenderPageBitmap(dynamic docReader, int pageIndex)
        {
            try
            {
                using (var pageReader = docReader.GetPageReader(pageIndex))
                {
                    int width = pageReader.GetPageWidth();
                    int height = pageReader.GetPageHeight();
                    var raw = pageReader.GetImage();
                    if (width <= 0 || height <= 0 || raw == null || raw.Length == 0) return null;

                    var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                    bitmap.WritePixels(new Int32Rect(0, 0, width, height), raw, width * 4, 0);
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch
            {
                return null;
            }
        }


        private static byte[] EncodeBgraToPng(byte[] bgra, int width, int height)
        {
            using (var bitmap = new DrawingBitmap(width, height, PixelFormat.Format32bppArgb))
            {
                var rect = new DrawingRectangle(0, 0, width, height);
                var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    Marshal.Copy(bgra, 0, data.Scan0, Math.Min(bgra.Length, Math.Abs(data.Stride) * height));
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                using (var stream = new MemoryStream())
                {
                    bitmap.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            }
        }

        internal static bool IsOwnerPasswordException(Exception ex) =>
            ex.Message.IndexOf("owner", StringComparison.OrdinalIgnoreCase) >= 0 &&
            ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Heuristic for the broken cross-reference / "Unexpected token 'xref'" errors that
        /// PdfSharpCore can throw when it re-saves or re-opens an encrypted (owner-restricted
        /// RC4) PDF after modification — e.g. rotating pages. The save/reload path uses this to
        /// decide when to fall back to a PDFium-based lossless repair.
        /// </summary>
        internal static bool IsXRefException(Exception ex) =>
            ex.Message.IndexOf("xref", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("cross-reference", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("trailer", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("startxref", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("Unexpected token", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("Invalid PDF file", StringComparison.OrdinalIgnoreCase) >= 0;

        // ── PDFium P/Invoke ──────────────────────────────────────────────────────────
        // pdfium.dll ships with Docnet. We use it here to losslessly re-serialize a PDF
        // (stripping encryption / page rotations) when PdfSharpCore produces a broken xref
        // after modifying an encrypted document. PDFium is already initialised by Docnet,
        // which we force via DocLib.Instance before any direct P/Invoke.

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadDocument(
            [MarshalAs(UnmanagedType.LPStr)] string filePath,
            [MarshalAs(UnmanagedType.LPStr)] string? password);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_CloseDocument(IntPtr document);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDF_SaveWithVersion(
            IntPtr document, ref FPDF_FILEWRITE fileWrite, uint flags, int fileVersion);

        [StructLayout(LayoutKind.Sequential)]
        private struct FPDF_FILEWRITE
        {
            public int version;       // must be 1
            public IntPtr WriteBlock; // cdecl: int WriteBlock(FPDF_FILEWRITE*, const void*, unsigned long)
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PdfWriteBlockDelegate(IntPtr pThis, IntPtr pData, uint size);

        private const uint FPDF_REMOVE_SECURITY = 3;

        /// <summary>
        /// Losslessly re-serializes <paramref name="sourcePath"/> through PDFium to
        /// <paramref name="destPath"/>, rebuilding a valid cross-reference table and stripping
        /// encryption. Page rotations (/Rotate), text, and other content are preserved — this is a
        /// pure repair, NOT a flatten. Called from the rotate→save→reopen xref-error fallback when
        /// PdfSharpCore emits a file whose xref PdfSharpCore itself then refuses to re-open.
        /// PDFium is guaranteed initialised by then because the page preview already rendered via
        /// Docnet. Returns true on success.
        /// </summary>
        internal static bool TryPdfiumRepair(string sourcePath, string destPath)
        {
            try
            {
                try { _ = DocLib.Instance; } catch { /* force PDFium init */ }

                var doc = FPDF_LoadDocument(sourcePath, null);
                if (doc == IntPtr.Zero) return false;
                try
                {
                    return PdfiumSave(doc, destPath);
                }
                finally { FPDF_CloseDocument(doc); }
            }
            catch { return false; }
        }

        private static bool PdfiumSave(IntPtr doc, string destPath)
        {
            using var ms = new MemoryStream();
            PdfWriteBlockDelegate cb = (_, pData, size) =>
            {
                var buf = new byte[size];
                Marshal.Copy(pData, buf, 0, (int)size);
                ms.Write(buf, 0, (int)size);
                return 1;
            };
            var gch = GCHandle.Alloc(cb);
            try
            {
                var fw = new FPDF_FILEWRITE
                {
                    version = 1,
                    WriteBlock = Marshal.GetFunctionPointerForDelegate(cb),
                };
                if (!FPDF_SaveWithVersion(doc, ref fw, FPDF_REMOVE_SECURITY, 0))
                    return false;
            }
            finally { gch.Free(); }
            File.WriteAllBytes(destPath, ms.ToArray());
            return true;
        }
    }

    internal sealed class PdfOpenResult
    {
        public PdfOpenResult(PdfDocument document, string displayPath, string workingPath, bool openedReadOnly, bool recoveredFromRaster = false)
        {
            Document = document;
            DisplayPath = displayPath;
            WorkingPath = workingPath;
            OpenedReadOnly = openedReadOnly;
            RecoveredFromRaster = recoveredFromRaster;
        }

        public PdfDocument Document { get; }
        public string DisplayPath { get; }
        public string WorkingPath { get; }
        public bool OpenedReadOnly { get; }

        /// <summary>
        /// True when the original file could not be parsed by PdfSharpCore
        /// and was recovered by rasterizing each page through PDFium into a
        /// fresh, well-formed document. The pages are images (no selectable
        /// text); the original vector content is not preserved.
        /// </summary>
        public bool RecoveredFromRaster { get; }
    }

    internal sealed class PdfPageSize
    {
        public PdfPageSize(double widthPoint, double heightPoint)
        {
            WidthPoint = widthPoint;
            HeightPoint = heightPoint;
        }

        public double WidthPoint { get; }
        public double HeightPoint { get; }
    }

    internal sealed class PdfRenderResult
    {
        public PdfRenderResult(BitmapSource? bitmap, int width, int height, int dipWidth, int dipHeight)
        {
            Bitmap = bitmap;
            Width = width;
            Height = height;
            DipWidth = dipWidth;
            DipHeight = dipHeight;
        }

        public BitmapSource? Bitmap { get; }
        public int Width { get; }
        public int Height { get; }
        public int DipWidth { get; }
        public int DipHeight { get; }
    }
}
