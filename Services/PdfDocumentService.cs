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
                        rw = pageReader.GetPageWidth();
                        rh = pageReader.GetPageHeight();
                        // #141: with annotations — a flatten builds a NEW document out of these pixels,
                        // so anything Docnet's flag-0 GetImage leaves out (the file's own sticky notes,
                        // highlights, stamps, and filled form values) was silently dropped from the
                        // output. Falls back to the old raster if PDFium can't take the direct path.
                        bgra = PdfiumInterop.RenderPageWithAnnotations(sourcePath, i, rw, rh)
                               ?? pageReader.GetImage();
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

        /// <summary>
        /// Side of the square bounding box, in WPF DIPs at layout scale 1, that a page is
        /// rasterized into for on-screen display. PDFium maps the page's LONGEST side to this and
        /// scales the other proportionally, so the resulting tile is NOT the page at its natural
        /// size: it is <c>RenderBoxDip * 72/96 / longestSideInPoints</c> times natural (~1.37x for
        /// A4, ~1.45x for US Letter). <c>MainWindow.DisplayZoomFactor</c> is that ratio, and is
        /// what converts the user-facing zoom into the layout scale applied to this tile.
        /// </summary>
        internal const int RenderBoxDip = 1536;

        public Task<PdfRenderResult> RenderPageAsync(string path, int pageIndex, int dpiX, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                int safeDpiX = Math.Max(1, dpiX);
                double renderScale = safeDpiX / 96.0;
                int renderMax = Math.Max(1, (int)Math.Round(RenderBoxDip * renderScale));
                using (var docReader = DocLib.Instance.GetDocReader(path, new PageDimensions(renderMax, renderMax)))
                using (var pageReader = docReader.GetPageReader(pageIndex))
                {
                    int width = pageReader.GetPageWidth();
                    int height = pageReader.GetPageHeight();
                    // #141: with annotations, so the viewer shows the markup the file carries the way
                    // Firefox and SumatraPDF do. White background matches the page's white Border.
                    var rawBytes = PdfiumInterop.RenderPageWithAnnotations(path, pageIndex, width, height)
                                   ?? pageReader.GetImage();
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
                        // A file encrypted with an EMPTY user password opens without a prompt but is
                        // still protected, so the trailer scan is the only reliable signal here.
                        return new PdfOpenResult(document, path, path, false,
                            wasProtected: FileHasEncryption(path));
                    }
                    catch (Exception ex) when (IsOwnerPasswordException(ex))
                    {
                        document?.Close();
                        document = null;

                        // An EMPTY user password opens the file while the owner password still forbids
                        // modification. Preferred outcome (unchanged): reopen through PdfSharpCore's
                        // ReadOnly parser so the restriction is honoured and the UI can say so.
                        //
                        // Upstream v1.7.1: that retry is NOT safe on a malformed linearized file — it
                        // reaches a broken hint table and throws an array-index error. Because the
                        // throw happens INSIDE this catch clause, the sibling catch clauses of the same
                        // try cannot see it: the exception escaped OpenCore entirely, the raster
                        // recovery net never ran, and the user got "Failed to open PDF". Contain it
                        // here so the same fallbacks every other parse failure gets still apply.
                        try
                        {
                            document = PdfReader.Open(path, PdfDocumentOpenMode.ReadOnly);
                            PreloadDocnet(path, cancellationToken);
                            return new PdfOpenResult(document, path, path, true, wasProtected: true);
                        }
                        catch (OperationCanceledException)
                        {
                            document?.Close();
                            document = null;   // already closed; keep the outer handler off it
                            throw;
                        }
                        catch
                        {
                            document?.Close();
                            document = null;
                        }

                        // PDFium's tolerant parser can rewrite the file, repairing the tables
                        // PdfSharpCore choked on. That copy is necessarily DECRYPTED, so it is reported
                        // as editable rather than read-only — see TryOpenViaPdfiumRepair.
                        PdfOpenResult? unlocked = TryOpenViaPdfiumRepair(path, cancellationToken);
                        if (unlocked is not null)
                            return unlocked;

                        // Last resort, same as any other unparseable file: rasterize through PDFium.
                        PdfOpenResult? rasterized = TryRecoverByRaster(path, ex, cancellationToken);
                        if (rasterized is not null)
                            return rasterized;

                        throw;   // nothing worked - surface the original owner-password error
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
                // The working copy is DECRYPTED: PdfSharpCore writes no /Encrypt unless a password is
                // set on the document, so every later save of this session is unprotected by
                // construction. WasProtected lets the UI say so instead of dropping it silently.
                return new PdfOpenResult(document, path, tempDec, false, wasProtected: true);
            }
            catch
            {
                document?.Close();
                throw;
            }
        }

        /// <summary>
        /// True if the PDF file has an /Encrypt entry in its trailer. Scans the last 2 KB so it is
        /// fast, and works regardless of how PdfSharpCore reports security state after
        /// authenticating with an empty password — a file whose user password is empty (owner
        /// restrictions only) opens with no prompt at all yet is still encrypted.
        /// </summary>
        /// <remarks>
        /// Latin-1 rather than Windows-1252 so no <c>CodePagesEncodingProvider</c> registration is
        /// required (the app registers none, and a throw here would silently read as "not
        /// encrypted"); the marker is pure ASCII, so the two decode it identically.
        /// Shared with the batch/CLI resave harness — see <c>BatchMode.BatchResaveOne</c>.
        /// </remarks>
        internal static bool FileHasEncryption(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                long scan = Math.Min(2048, fs.Length);
                fs.Seek(-scan, SeekOrigin.End);
                var buf = new byte[scan];
                _ = fs.Read(buf, 0, buf.Length);
                return System.Text.Encoding.Latin1.GetString(buf).Contains("/Encrypt");
            }
            catch { return false; }
        }

        // Heuristic: does this look like a password / encryption failure
        // (which the caller handles by prompting) rather than a malformed
        // file (which we try to recover by rasterizing)?
        private static bool IsPasswordException(Exception ex) =>
            ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("protected", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("encrypted", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Opens an owner-restricted file that PdfSharpCore cannot parse, by way of a PDFium-rewritten
        /// working copy in %TEMP% (<see cref="PdfiumInterop.TryPdfiumRepair"/> — <c>FPDF_SaveWithVersion</c> with
        /// <c>FPDF_REMOVE_SECURITY</c>, the same path CLI <c>--decrypt</c> and the post-save xref
        /// repair use). Unlike the raster fallback this is lossless: text stays selectable.
        /// </summary>
        /// <remarks>
        /// The rewritten copy carries no <c>/Encrypt</c>, so the document really is editable and is
        /// reported that way — <c>OpenedReadOnly: false</c>. Claiming read-only here would be a lie the
        /// save path would immediately contradict. <c>WasProtected</c> and <c>RestrictionsRemoved</c>
        /// are both true so the UI can say the protection is gone rather than dropping it silently.
        /// Returns <c>null</c> — never throws, except on cancellation — when PDFium cannot help either,
        /// leaving the caller to try the raster net and then surface the original error.
        /// </remarks>
        private static PdfOpenResult? TryOpenViaPdfiumRepair(string path, CancellationToken cancellationToken)
        {
            string repairedPath = Path.Combine(Path.GetTempPath(), $"tdpdf_unlock_{Guid.NewGuid():N}.pdf");
            PdfDocument? document = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!PdfiumInterop.TryPdfiumRepair(path, repairedPath))
                {
                    TryDeleteQuiet(repairedPath);
                    return null;
                }

                cancellationToken.ThrowIfCancellationRequested();
                document = PdfReader.Open(repairedPath, PdfDocumentOpenMode.Modify);
                PreloadDocnet(repairedPath, cancellationToken);

                Telemetry.TrackEvent("File.OpenUnlockedByPdfium");

                return new PdfOpenResult(document, path, repairedPath, openedReadOnly: false,
                    wasProtected: true, restrictionsRemoved: true);
            }
            catch (OperationCanceledException)
            {
                document?.Close();
                TryDeleteQuiet(repairedPath);
                throw;
            }
            catch
            {
                document?.Close();
                TryDeleteQuiet(repairedPath);
                return null;
            }
        }

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
                            // #141: with annotations — this rebuilds the document from its pixels, so
                            // markup the source carried must survive the recovery.
                            var bgra = PdfiumInterop.RenderPageWithAnnotations(path, i, pw, ph)
                                       ?? pageReader.GetImage();
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

                return new PdfOpenResult(document, path, tempPath, openedReadOnly: false,
                    recoveredFromRaster: true, wasProtected: FileHasEncryption(path));
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

        /// <summary>
        /// Renders one sidebar thumbnail from an already-open reader.
        /// </summary>
        /// <remarks>
        /// NOT annotation-aware, deliberately (#141) — the one rasterize in the app that still uses
        /// the plain flag-0 <c>GetImage()</c> apart from OCR. <see cref="RenderThumbnailsAsync"/>
        /// runs this eagerly over EVERY page of the document, awaited on the file-open path, reusing
        /// a single reader. Routing it through <c>PdfiumInterop.RenderPageWithAnnotations</c> would
        /// add one <c>FPDF_LoadDocument</c> plus a form-fill environment init/teardown per page —
        /// each taking <c>PdfiumLock</c> and so contending with the foreground page render — for an
        /// open-time cost that grows without bound with page count. The payoff would be
        /// file-carried markup on a 256 px thumbnail, where it is barely legible. If thumbnails ever
        /// become lazy/virtualized per page, revisit this; until then do not "fix" the
        /// inconsistency with the other rasterize sites.
        /// </remarks>
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


        /// <summary>
        /// Composites a straight-alpha BGRA buffer over opaque white, in place.
        /// </summary>
        /// <remarks>
        /// PDFium leaves every unpainted background pixel at BGRA 0,0,0,0. Handing that
        /// straight to an encoder means JPEG (which has no alpha channel) renders the
        /// background solid BLACK, while PNG and the flatten path carry a needless
        /// full-page alpha channel that PdfSharpCore re-emits as an /SMask.
        ///
        /// PDFium's ARGB buffers are non-premultiplied, so this is plain source-over
        /// against white: out = src * a + 255 * (1 - a), evaluated in fixed point as
        /// (src * a + 255 * (255 - a) + 127) / 255. That is exact at both ends
        /// (a = 0 -> white, a = 255 -> src) and correct for the partial coverage in
        /// between, so it stays right even where a "replace fully transparent pixels"
        /// shortcut would leave anti-aliased edges dark.
        ///
        /// Every pixel ends fully opaque, so callers can hand the same buffer to a
        /// 32-bit alpha-free pixel format (GDI+ Format32bppRgb, WPF Bgr32) and get an
        /// image with no alpha channel at all. Done in place with no second buffer:
        /// a full-page raster is ~100 MB at 1200 dpi.
        /// </remarks>
        internal static void CompositeBgraOverWhite(byte[] bgra)
        {
            for (int i = 0; i + 3 < bgra.Length; i += 4)
            {
                byte a = bgra[i + 3];
                if (a == 255) continue;          // already opaque - the common case
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

        // Encodes a PDFium BGRA page raster to PNG. The buffer is composited over white
        // first and written through Format32bppRgb (same 4-byte layout, alpha ignored),
        // so GDI+ emits a 24-bit PNG with no alpha channel. Mutates <paramref name="bgra"/>.
        private static byte[] EncodeBgraToPng(byte[] bgra, int width, int height)
        {
            CompositeBgraOverWhite(bgra);

            using (var bitmap = new DrawingBitmap(width, height, PixelFormat.Format32bppRgb))
            {
                var rect = new DrawingRectangle(0, 0, width, height);
                var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
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
            ex.Message.IndexOf("Invalid PDF file", StringComparison.OrdinalIgnoreCase) >= 0 ||
            // #106: "Cannot retrieve stream length." — a stream whose /Length is indirect or broken;
            // "File streams are not yet implemented" — an embedded file stream PdfSharpCore can't
            // round-trip. Both are recoverable by piping the source through the PDFium repair path,
            // which rebuilds clean stream structures. Used at save time as well as reopen time.
            ex.Message.IndexOf("stream length", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("File streams are not yet implemented", StringComparison.OrdinalIgnoreCase) >= 0;

        // Direct pdfium.dll calls (the lossless repair / encryption strip, and the
        // annotation-aware page renderer) live in Services/PdfiumInterop.cs — one home, one lock.
    }

    internal sealed class PdfOpenResult
    {
        public PdfOpenResult(PdfDocument document, string displayPath, string workingPath, bool openedReadOnly,
            bool recoveredFromRaster = false, bool wasProtected = false, bool restrictionsRemoved = false)
        {
            Document = document;
            DisplayPath = displayPath;
            WorkingPath = workingPath;
            OpenedReadOnly = openedReadOnly;
            RecoveredFromRaster = recoveredFromRaster;
            WasProtected = wasProtected;
            RestrictionsRemoved = restrictionsRemoved;
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

        /// <summary>
        /// True when the source file was encrypted: it needed a password to open, it refused
        /// Modify mode because of owner restrictions, or it carries an /Encrypt trailer entry with
        /// an empty user password. TDPdf always rewrites through PdfSharpCore, which cannot
        /// re-encrypt, so any save of such a document produces an UNPROTECTED file — the UI uses
        /// this to say so, and to offer File ▸ Remove Password.
        /// </summary>
        public bool WasProtected { get; }

        /// <summary>
        /// True when an owner-restricted file that PdfSharpCore could not parse was opened through a
        /// PDFium-rewritten, decrypted working copy. The document IS editable — never report it as
        /// read-only — but the restriction the source carried is gone, so the UI says so on open.
        /// Lossless, unlike <see cref="RecoveredFromRaster"/>: the text layer survives.
        /// </summary>
        public bool RestrictionsRemoved { get; }
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
