using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Docnet.Core;

namespace TDPdf.Services
{
    // ── Direct PDFium P/Invoke ───────────────────────────────────────────────────────────────
    // The ONE home for every direct pdfium.dll call in TDPdf (extracted from
    // Services/PdfDocumentService.cs). pdfium.dll ships with Docnet.Core; PDFium is already
    // initialised by Docnet, which we force via DocLib.Instance before any direct P/Invoke.
    //
    // THREADING (upstream KillerPDF v1.6.4): PDFium is single-threaded. Docnet serializes every
    // native call it makes on an internal static lock (Docnet.Core.DocLib.Lock). Our DIRECT
    // pdfium.dll calls below must hold that SAME lock, or a background Docnet render and a direct
    // call can be inside PDFium at once — native heap corruption (exit code 0xc0000374).
    // Reflected once; the `?? new object()` fallback keeps us safe (self-serialized) even if
    // Docnet ever renames the field. The raw externs are suffixed Raw; only the lock-holding
    // wrappers may be called, and keeping every extern in this one class is what makes that
    // discipline auditable.
    //
    // NATIVE LIFETIME ORDERING (upstream KillerPDF #141/#179): holding the lock is necessary but
    // not sufficient. Docnet's annotation-aware GetImage overload builds a form-fill environment
    // (FPDFDOC_InitFormFillEnvironment) per call, draws, and destroys it in its own finally —
    // while the PAGE it drew is still open, because PageReader closes the page later in its
    // Dispose. Tearing a form-fill environment down out of order corrupts PDFium's internal state
    // and the damage surfaces on the NEXT native call as an AccessViolationException, so that
    // overload is unusable (upstream shipped it, crashed, and reverted). RenderPageWithAnnotations
    // below therefore owns the entire document → form → page lifetime itself and releases it in
    // the order this PDFium build expects: form (while its page is still alive), then page, then
    // document.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    internal static class PdfiumInterop
    {
        internal static readonly object PdfiumLock =
            typeof(DocLib).GetField("Lock",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?.GetValue(null) ?? new object();

        // ---- Document / page lifecycle ------------------------------------------------------

        [DllImport("pdfium.dll", EntryPoint = "FPDF_LoadDocument", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadDocumentRaw(
            [MarshalAs(UnmanagedType.LPStr)] string filePath,
            [MarshalAs(UnmanagedType.LPStr)] string? password);
        private static IntPtr FPDF_LoadDocument(string filePath, string? password)
        { lock (PdfiumLock) return FPDF_LoadDocumentRaw(filePath, password); }

        [DllImport("pdfium.dll", EntryPoint = "FPDF_CloseDocument", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_CloseDocumentRaw(IntPtr document);
        private static void FPDF_CloseDocument(IntPtr document)
        { lock (PdfiumLock) FPDF_CloseDocumentRaw(document); }

        [DllImport("pdfium.dll", EntryPoint = "FPDF_LoadPage", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadPageRaw(IntPtr document, int pageIndex);

        [DllImport("pdfium.dll", EntryPoint = "FPDF_ClosePage", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_ClosePageRaw(IntPtr page);

        // ---- Page rendering -----------------------------------------------------------------

        [DllImport("pdfium.dll", EntryPoint = "FPDFBitmap_CreateEx", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFBitmap_CreateExRaw(
            int width, int height, int format, IntPtr firstScan, int stride);

        [DllImport("pdfium.dll", EntryPoint = "FPDFBitmap_Destroy", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFBitmap_DestroyRaw(IntPtr bitmap);

        [DllImport("pdfium.dll", EntryPoint = "FPDFBitmap_FillRect", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFBitmap_FillRectRaw(
            IntPtr bitmap, int left, int top, int width, int height, uint color);

        [DllImport("pdfium.dll", EntryPoint = "FPDF_RenderPageBitmap", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_RenderPageBitmapRaw(
            IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY,
            int rotate, int flags);

        [DllImport("pdfium.dll", EntryPoint = "FPDFDOC_InitFormFillEnvironment", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFDOC_InitFormFillEnvironmentRaw(IntPtr document, IntPtr formInfo);

        [DllImport("pdfium.dll", EntryPoint = "FPDFDOC_ExitFormFillEnvironment", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFDOC_ExitFormFillEnvironmentRaw(IntPtr formHandle);

        [DllImport("pdfium.dll", EntryPoint = "FPDF_FFLDraw", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_FFLDrawRaw(
            IntPtr formHandle, IntPtr bitmap, IntPtr page, int startX, int startY,
            int sizeX, int sizeY, int rotate, int flags);

        // ---- Annotation inspection (upstream KillerPDF 1.7.2) --------------------------------
        // Only used by HideWidgetAnnotations below, which already runs inside the PdfiumLock the
        // render path takes — hence no lock-holding wrappers here. Nothing else may call these.

        [DllImport("pdfium.dll", EntryPoint = "FPDFPage_GetAnnotCount", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFPage_GetAnnotCountRaw(IntPtr page);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPage_GetAnnot", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFPage_GetAnnotRaw(IntPtr page, int index);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPage_CloseAnnot", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFPage_CloseAnnotRaw(IntPtr annot);

        [DllImport("pdfium.dll", EntryPoint = "FPDFAnnot_GetSubtype", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFAnnot_GetSubtypeRaw(IntPtr annot);

        [DllImport("pdfium.dll", EntryPoint = "FPDFAnnot_GetFlags", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFAnnot_GetFlagsRaw(IntPtr annot);

        [DllImport("pdfium.dll", EntryPoint = "FPDFAnnot_SetFlags", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFAnnot_SetFlagsRaw(IntPtr annot, int flags);

        private const int FPDFBitmapBgra = 4;   // FPDFBitmap_BGRA — the layout Docnet's GetImage returns
        private const int FpdfAnnot = 0x01;     // paint the annotation appearance streams the file carries
        private const int FpdfLcdText = 0x02;   // subpixel text antialiasing
        private const int FpdfAnnotSubtypeWidget = 20;   // fpdf_annot.h FPDF_ANNOT_WIDGET
        private const int FpdfAnnotFlagHidden = 1 << 1;  // fpdf_annot.h FPDF_ANNOT_FLAG_HIDDEN

        private const uint OpaqueWhite = 0xFFFFFFFFu;
        private const uint FullyTransparent = 0x00000000u;

        /// <summary>
        /// Marks every WIDGET (form-field) annotation on the loaded page hidden, so that the
        /// FPDF_ANNOT render pass does not paint a field appearance into the bitmap, and returns
        /// each widget's original flags keyed by annotation index so
        /// <see cref="RestoreWidgetAnnotationFlags"/> can put them back before FFLDraw runs.
        /// Returns <c>null</c> when the flags could not be read at all.
        /// </summary>
        /// <remarks>
        /// In-memory only, and safe because of it: this renderer's document is a one-shot load that
        /// is closed immediately afterwards and is never saved, so the flag never reaches the file.
        /// Called from inside the <c>PdfiumLock</c> section of
        /// <see cref="RenderPageWithAnnotations"/> — do NOT call it from anywhere that does not
        /// already hold the lock. Every annot handle opened here is closed in a <c>finally</c>, in
        /// the same "release what you opened, innermost first" discipline the rest of this file
        /// follows; a leaked annot handle would keep page objects alive past FPDF_ClosePage.
        /// An <c>EntryPointNotFoundException</c> (an older bundled PDFium with no annot API) is
        /// swallowed so the render degrades to leaving the fields baked in rather than crashing.
        /// </remarks>
        private static Dictionary<int, int>? HideWidgetAnnotations(IntPtr page)
        {
            try
            {
                var saved = new Dictionary<int, int>();
                int count = FPDFPage_GetAnnotCountRaw(page);
                for (int i = 0; i < count; i++)
                {
                    IntPtr annot = FPDFPage_GetAnnotRaw(page, i);
                    if (annot == IntPtr.Zero) continue;
                    try
                    {
                        if (FPDFAnnot_GetSubtypeRaw(annot) == FpdfAnnotSubtypeWidget)
                        {
                            int flags = FPDFAnnot_GetFlagsRaw(annot);
                            saved[i] = flags;
                            FPDFAnnot_SetFlagsRaw(annot, flags | FpdfAnnotFlagHidden);
                        }
                    }
                    finally { FPDFPage_CloseAnnotRaw(annot); }
                }
                return saved;
            }
            catch { return null; /* annot API unavailable: fields stay baked, no crash */ }
        }

        /// <summary>
        /// Puts back the widget flags <see cref="HideWidgetAnnotations"/> saved, so FFLDraw sees the
        /// file's own visibility: a field the document genuinely marks hidden stays hidden, and
        /// everything else is painted exactly once, by FFLDraw.
        /// </summary>
        /// <remarks>
        /// Same locking and handle discipline as <see cref="HideWidgetAnnotations"/> — call only
        /// from inside the <c>PdfiumLock</c> section, and close every annot handle that is opened.
        /// </remarks>
        private static void RestoreWidgetAnnotationFlags(IntPtr page, Dictionary<int, int>? saved)
        {
            if (saved is null) return;
            try
            {
                foreach (var kv in saved)
                {
                    IntPtr annot = FPDFPage_GetAnnotRaw(page, kv.Key);
                    if (annot == IntPtr.Zero) continue;
                    try { FPDFAnnot_SetFlagsRaw(annot, kv.Value); }
                    finally { FPDFPage_CloseAnnotRaw(annot); }
                }
            }
            catch { /* best-effort: the document is a one-shot load that is never saved */ }
        }

        /// <summary>
        /// Renders one page of <paramref name="sourcePath"/> to a BGRA buffer with the annotation
        /// appearance streams the file already carries (sticky notes, highlights, stamps, ink drawn
        /// in another app, filled form values) painted in — the thing Docnet's parameterless
        /// <c>GetImage()</c> leaves out because it passes render flags 0. Returns <c>null</c> on any
        /// failure so every caller can fall back to <c>GetImage()</c> rather than show a blank page.
        /// </summary>
        /// <param name="transparentBackground">
        /// Keep PDFium's unpainted background as BGRA 0,0,0,0 instead of compositing over opaque
        /// white. Only the CLI's <c>--to-image --transparent</c> (PNG) wants this; everything else
        /// wants white — see the fill comment below.
        /// </param>
        /// <param name="includeFormFields">
        /// Upstream KillerPDF 1.7.2. False for the one tile that carries the live WPF form-field
        /// overlays (the primary page): those overlays already show the field values, and their
        /// backgrounds are only partly opaque, so a baked appearance underneath showed through as a
        /// second, slightly offset copy of the same text — a "drop shadow" ghost. True everywhere
        /// the pixels ARE the output (print, flatten, image export, page transforms, raster
        /// recovery) and on every on-screen surface that has no live overlay of its own (the
        /// Grid/Two-Page secondary tiles and the Continuous strip), where dropping the baked
        /// appearance would simply erase the filled-in values.
        /// </param>
        /// <remarks>
        /// One-shot by design: owning the whole document → form → page lifetime here is what makes
        /// the native teardown ordering safe (see the class header), but it does mean an extra
        /// FPDF_LoadDocument per page. PDFium parses the xref lazily so that is small next to the
        /// raster itself; the place to watch is any loop that renders many pages of one file — the
        /// continuous-view base / window / re-sharpen passes.
        /// Two rasterize paths deliberately do NOT use this, both for reasons written down where
        /// they live: OCR (see the note at the top of Ocr.cs) and the sidebar thumbnails (see
        /// PdfDocumentService.RenderPageBitmap), which are eager over the whole document on the
        /// file-open path and would pay this per-page cost unbounded.
        /// </remarks>
        internal static byte[]? RenderPageWithAnnotations(
            string sourcePath, int pageIndex, int width, int height,
            bool transparentBackground = false, bool includeFormFields = true)
        {
            if (width <= 0 || height <= 0) return null;
            try
            {
                try { _ = DocLib.Instance; } catch { /* force PDFium init */ }

                int stride = checked(width * 4);
                var bytes = new byte[checked(stride * height)];
                // PDFium writes straight into the managed buffer, so it must not move underneath it.
                var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try
                {
                    lock (PdfiumLock)
                    {
                        IntPtr doc = FPDF_LoadDocumentRaw(sourcePath, null);
                        if (doc == IntPtr.Zero) return null;

                        IntPtr formInfo = IntPtr.Zero;
                        IntPtr form = IntPtr.Zero;
                        try
                        {
                            // PDFium retains this pointer until FPDFDOC_ExitFormFillEnvironment, so it
                            // has to be stable unmanaged memory: a buffer produced by the P/Invoke
                            // marshaller is freed the moment the call returns, and the environment then
                            // reads freed memory on teardown. The bundled ABI is one 32-bit version
                            // field plus 31 pointer slots; every slot is zeroed, and PDFium null-checks
                            // each callback before using it.
                            int formInfoSize = IntPtr.Size == 8 ? 256 : 128;
                            formInfo = Marshal.AllocHGlobal(formInfoSize);
                            for (int offset = 0; offset < formInfoSize; offset += 4)
                                Marshal.WriteInt32(formInfo, offset, 0);
                            for (int version = 1; version <= 2 && form == IntPtr.Zero; version++)
                            {
                                Marshal.WriteInt32(formInfo, 0, version);
                                form = FPDFDOC_InitFormFillEnvironmentRaw(doc, formInfo);
                            }

                            IntPtr page = FPDF_LoadPageRaw(doc, pageIndex);
                            if (page == IntPtr.Zero) return null;
                            try
                            {
                                // Must happen after the page loads and before either draw call: both
                                // the FPDF_ANNOT pass and FFLDraw honour the hidden flag.
                                //
                                // Upstream v1.7.4: widgets are hidden for the static FPDF_ANNOT pass
                                // in BOTH modes. The on-screen viewer replaces them with live
                                // overlays; the output path paints them once via FFLDraw below, and
                                // letting the static pass draw the /AP as well painted every field
                                // twice wherever the stored /AP layout and FFLDraw's (NeedAppearances)
                                // layout disagreed — the slightly-offset ghost on filled forms, this
                                // time in print / flatten / export rather than on screen. When the
                                // output path has no form environment to draw with, the widgets stay
                                // visible so the static pass is still the one thing showing them.
                                var savedWidgetFlags = includeFormFields && form == IntPtr.Zero
                                    ? null
                                    : HideWidgetAnnotations(page);
                                IntPtr bitmap = FPDFBitmap_CreateExRaw(
                                    width, height, FPDFBitmapBgra, pinned.AddrOfPinnedObject(), stride);
                                if (bitmap == IntPtr.Zero) return null;
                                try
                                {
                                    // Fill FIRST: FPDF_RenderPageBitmap composites onto whatever the
                                    // buffer already holds. Opaque white unless the caller explicitly
                                    // asked to keep the alpha — v1.19.0.0 (#148) fixed exported JPEGs
                                    // coming out black and flattened PDFs carrying a full-page /SMask
                                    // by never letting PDFium's 0,0,0,0 background reach an encoder,
                                    // and every on-screen page sits on a white Border, so white is
                                    // also pixel-identical to what the viewer shows today.
                                    // (A 0-alpha fill is a no-op in PDFium; the freshly allocated
                                    // buffer is already zeroed, which is the same thing.)
                                    FPDFBitmap_FillRectRaw(bitmap, 0, 0, width, height,
                                        transparentBackground ? FullyTransparent : OpaqueWhite);
                                    FPDF_RenderPageBitmapRaw(bitmap, page, 0, 0, width, height, 0,
                                        FpdfAnnot | FpdfLcdText);
                                    // Widget (form field) appearances live in the form-fill layer.
                                    // Skipped entirely when the caller does not want them: the
                                    // hidden flag set above already suppresses them, but FFLDraw is
                                    // then pure cost with nothing left to paint.
                                    if (includeFormFields && form != IntPtr.Zero)
                                    {
                                        // Restore first: FFLDraw honours the hidden flag too, so
                                        // leaving it set would suppress the very pass that is meant
                                        // to be the single painter of these fields.
                                        RestoreWidgetAnnotationFlags(page, savedWidgetFlags);
                                        FPDF_FFLDrawRaw(form, bitmap, page, 0, 0, width, height, 0,
                                            FpdfAnnot | FpdfLcdText);
                                    }
                                }
                                finally { FPDFBitmap_DestroyRaw(bitmap); }
                            }
                            finally
                            {
                                // This PDFium build expects the form environment to be released while
                                // its page is still alive — the exact ordering Docnet gets wrong. The
                                // page and the one-shot document are closed immediately afterwards, so
                                // no half-torn-down native state is ever reused.
                                if (form != IntPtr.Zero)
                                {
                                    FPDFDOC_ExitFormFillEnvironmentRaw(form);
                                    form = IntPtr.Zero;
                                }
                                FPDF_ClosePageRaw(page);
                            }
                        }
                        finally
                        {
                            // Only reached when the page never loaded (or a managed throw beat us here):
                            // release the environment before the document it belongs to, and never leak
                            // the document handle or the unmanaged struct.
                            if (form != IntPtr.Zero) FPDFDOC_ExitFormFillEnvironmentRaw(form);
                            if (formInfo != IntPtr.Zero) Marshal.FreeHGlobal(formInfo);
                            FPDF_CloseDocumentRaw(doc);
                        }
                    }
                    return bytes;
                }
                finally { pinned.Free(); }
            }
            catch { return null; }
        }

        // ---- Save ---------------------------------------------------------------------------

        [DllImport("pdfium.dll", EntryPoint = "FPDF_SaveWithVersion", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDF_SaveWithVersionRaw(
            IntPtr document, ref FPDF_FILEWRITE fileWrite, uint flags, int fileVersion);
        private static bool FPDF_SaveWithVersion(IntPtr document, ref FPDF_FILEWRITE fileWrite, uint flags, int fileVersion)
        { lock (PdfiumLock) return FPDF_SaveWithVersionRaw(document, ref fileWrite, flags, fileVersion); }

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
}
