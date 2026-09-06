using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Docnet.Core;
using TDPdf.Diagnostics;

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

        // UTF-8, not LPStr. Ported from upstream KillerPDF ("Direct PDFium loading now uses
        // explicit UTF-8 marshalling for document paths and passwords"). PDFium takes both of these
        // as UTF-8 byte strings on every platform, but UnmanagedType.LPStr marshals through the
        // machine's ANSI code page — so any character the local code page cannot represent was
        // mangled before PDFium ever saw it. The document then failed to load with no error worth
        // showing: a filename or a password outside CP1252 simply did not work, and the page came
        // back blank. LPUTF8Str is the whole fix.
        [DllImport("pdfium.dll", EntryPoint = "FPDF_LoadDocument", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadDocumentRaw(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
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

        // ---- Content editing: page objects ----------------------------------------------------
        // Raw externs only. Every caller goes through RemoveObjectsIntersecting below, which owns
        // the document/page lifetime and holds PdfiumLock for the whole operation.

        internal const int PageObjText    = 1;   // FPDF_PAGEOBJ_TEXT
        internal const int PageObjPath    = 2;
        internal const int PageObjImage   = 3;
        internal const int PageObjShading = 4;
        internal const int PageObjForm    = 5;

        /// <summary>Text rendering mode 3: neither fill nor stroke. How an OCR layer is spotted.</summary>
        internal const int TextRenderInvisible = 3;

        [DllImport("pdfium.dll", EntryPoint = "FPDFPage_CountObjects", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFPage_CountObjectsRaw(IntPtr page);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPage_GetObject", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFPage_GetObjectRaw(IntPtr page, int index);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPageObj_GetType", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFPageObj_GetTypeRaw(IntPtr pageObject);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPageObj_GetBounds", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFPageObj_GetBoundsRaw(
            IntPtr pageObject, out float left, out float bottom, out float right, out float top);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPage_RemoveObject", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFPage_RemoveObjectRaw(IntPtr page, IntPtr pageObject);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPageObj_Destroy", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFPageObj_DestroyRaw(IntPtr pageObject);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPage_GenerateContent", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFPage_GenerateContentRaw(IntPtr page);

        [DllImport("pdfium.dll", EntryPoint = "FPDFTextObj_GetTextRenderMode", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFTextObj_GetTextRenderModeRaw(IntPtr textObject);

        [DllImport("pdfium.dll", EntryPoint = "FPDFFormObj_CountObjects", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFFormObj_CountObjectsRaw(IntPtr formObject);

        [DllImport("pdfium.dll", EntryPoint = "FPDFFormObj_GetObject", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFFormObj_GetObjectRaw(IntPtr formObject, uint index);

        [DllImport("pdfium.dll", EntryPoint = "FPDFFormObj_RemoveObject", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFFormObj_RemoveObjectRaw(IntPtr formObject, IntPtr pageObject);

        // ---- Content editing: text objects ----------------------------------------------------
        // FPDFText_SetText replaces the string of an EXISTING text object, which is the whole
        // reason real text editing is possible at all: the object keeps its font, size, matrix,
        // fill colour and render mode, so the edited words sit exactly where the old ones did and
        // look the same. Creating a replacement object from scratch would keep none of that.

        [DllImport("pdfium.dll", EntryPoint = "FPDFText_LoadPage", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFText_LoadPageRaw(IntPtr page);

        [DllImport("pdfium.dll", EntryPoint = "FPDFText_ClosePage", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFText_ClosePageRaw(IntPtr textPage);

        [DllImport("pdfium.dll", EntryPoint = "FPDFTextObj_GetText", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FPDFTextObj_GetTextRaw(
            IntPtr textObject, IntPtr textPage, IntPtr buffer, uint length);

        [DllImport("pdfium.dll", EntryPoint = "FPDFText_SetText", CallingConvention = CallingConvention.Cdecl,
                   CharSet = CharSet.Unicode)]
        private static extern bool FPDFText_SetTextRaw(IntPtr textObject, string text);

        [DllImport("pdfium.dll", EntryPoint = "FPDFTextObj_GetFont", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFTextObj_GetFontRaw(IntPtr textObject);

        [DllImport("pdfium.dll", EntryPoint = "FPDFTextObj_GetFontSize", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFTextObj_GetFontSizeRaw(IntPtr textObject, out float size);

        [DllImport("pdfium.dll", EntryPoint = "FPDFFont_GetIsEmbedded", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFFont_GetIsEmbeddedRaw(IntPtr font);

        [DllImport("pdfium.dll", EntryPoint = "FPDFFont_GetFontData", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFFont_GetFontDataRaw(
            IntPtr font, byte[]? buffer, UIntPtr buflen, out UIntPtr outBuflen);

        [DllImport("pdfium.dll", EntryPoint = "FPDFFont_GetBaseFontName", CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr FPDFFont_GetBaseFontNameRaw(IntPtr font, byte[]? buffer, UIntPtr length);

        /// <summary>A rectangle in PDF page space: points, origin bottom-left.</summary>
        internal readonly record struct PdfRect(double Left, double Bottom, double Right, double Top)
        {
            internal bool Contains(double l, double b, double r, double t) =>
                l >= Left && r <= Right && b >= Bottom && t <= Top;

            internal bool Intersects(double l, double b, double r, double t) =>
                l < Right && r > Left && b < Top && t > Bottom;
        }

        /// <summary>An object that overlapped a redaction rectangle without being inside it.</summary>
        /// <remarks>
        /// Reported rather than silently handled, because both available answers are wrong in
        /// different directions: removing the whole object deletes content the user meant to keep,
        /// and leaving it leaves content the user meant to destroy. Only the caller knows which
        /// failure is tolerable — and for redaction the second one is never tolerable silently.
        /// </remarks>
        internal readonly record struct RedactionOverlap(
            int PageIndex, int ObjectType, bool InvisibleText,
            double Left, double Bottom, double Right, double Top);

        internal sealed class RedactionOutcome
        {
            internal bool Ok { get; set; }
            internal string? Error { get; set; }
            internal int ObjectsRemoved { get; set; }
            internal int InvisibleTextRemoved { get; set; }
            internal List<RedactionOverlap> Partial { get; } = new();
        }

        /// <summary>
        /// Removes every page object falling inside one of <paramref name="rectsByPage"/>,
        /// regenerates the affected pages' content streams, and writes the result to
        /// <paramref name="destPath"/>. Objects are REMOVED, not covered.
        /// </summary>
        /// <param name="removePartialOverlaps">
        /// What to do with an object that overlaps a rectangle without being contained by it.
        /// False leaves it and reports it; true removes the whole object, over-deleting rather
        /// than under-deleting. IMAGES are exempt and are always reported rather than removed —
        /// see the note in RedactPageObjects.
        /// </param>
        /// <remarks>
        /// LIFETIME ORDERING, which PDFium's own embedder tests pin and which is easy to get
        /// wrong: remove, THEN generate content, THEN save, and only then destroy the removed
        /// objects. FPDFPage_RemoveObject hands ownership back to the caller and discards its
        /// return value internally, so skipping the destroy leaks unconditionally — but
        /// destroying before the save frees objects the generator still refers to.
        ///
        /// Objects are walked BACKWARDS so removing one cannot shift the index of another not
        /// yet visited.
        ///
        /// Form XObjects get one level of descent. That is not a nicety: an OCR'd scan keeps its
        /// invisible text layer inside a Form XObject, and it is the single most common thing
        /// anyone needs redacted. It is also exactly what the previously bundled PDFium could not
        /// do, for want of FPDFFormObj_RemoveObject.
        /// </remarks>
        internal static RedactionOutcome RemoveObjectsIntersecting(
            string srcPath, string destPath,
            IReadOnlyDictionary<int, IReadOnlyList<PdfRect>> rectsByPage,
            bool removePartialOverlaps)
        {
            var outcome = new RedactionOutcome();
            if (!CanEditPageContent)
            {
                outcome.Error = DescribeEditApi();
                return outcome;
            }

            try { _ = DocLib.Instance; } catch { /* force PDFium init */ }

            lock (PdfiumLock)
            {
                IntPtr doc = FPDF_LoadDocumentRaw(srcPath, null);
                if (doc == IntPtr.Zero) { outcome.Error = "the document could not be opened"; return outcome; }

                // Ownership passes to us on removal; freed only after the save.
                var orphaned = new List<IntPtr>();
                try
                {
                    foreach (var (pageIndex, rects) in rectsByPage)
                    {
                        if (rects is null || rects.Count == 0) continue;

                        IntPtr page = FPDF_LoadPageRaw(doc, pageIndex);
                        if (page == IntPtr.Zero) continue;
                        try
                        {
                            bool touched = RedactPageObjects(
                                page, pageIndex, rects, removePartialOverlaps, outcome, orphaned);

                            if (touched && !FPDFPage_GenerateContentRaw(page))
                            {
                                outcome.Error = $"content could not be regenerated for page {pageIndex + 1}";
                                return outcome;
                            }
                        }
                        finally { FPDF_ClosePageRaw(page); }
                    }

                    if (!PdfiumSave(doc, destPath))
                    {
                        outcome.Error = "the edited document could not be written";
                        return outcome;
                    }
                    outcome.Ok = true;
                    return outcome;
                }
                finally
                {
                    // After the save, never before.
                    foreach (var o in orphaned) { try { FPDFPageObj_DestroyRaw(o); } catch { } }
                    FPDF_CloseDocumentRaw(doc);
                }
            }
        }

        /// <summary>What happened to one requested text replacement.</summary>
        internal enum TextEditOutcome
        {
            Replaced,
            /// <summary>No text object matched the position and original text given.</summary>
            NotFound,
            /// <summary>The run's own font has no glyph for something in the replacement.</summary>
            FontCannotRender,
            /// <summary>PDFium accepted the string but the finished file does not hold it.</summary>
            NotApplied,
        }

        internal readonly record struct TextEditRequest(
            int PageIndex, PdfRect Bounds, string OriginalText, string NewText);

        internal sealed class TextEditResult
        {
            internal bool Ok { get; set; }
            internal string? Error { get; set; }
            internal List<(TextEditRequest Request, TextEditOutcome Outcome, string Detail)> Items { get; } = new();
            internal int Replaced => Items.Count(i => i.Outcome == TextEditOutcome.Replaced);
        }

        /// <summary>
        /// Replaces the text of existing text objects, in place, keeping their font and position.
        /// </summary>
        /// <remarks>
        /// This is real editing rather than the overlay TDPdf has always drawn: no white rectangle
        /// covering the old words, no second copy of the text on top. The object keeps its font,
        /// size, matrix, fill colour and render mode, so the replacement sits exactly where the
        /// original sat and looks like the rest of the line.
        ///
        /// THE THING THAT MAKES IT HARD is the font. Most PDFs embed a SUBSET — only the glyphs the
        /// document actually used — so a document that never contained a "Z" has no "Z" to draw
        /// with, and asking for one produces a blank or a notdef box with no error anywhere. Every
        /// replacement is therefore checked against the font's own cmap first (see
        /// <see cref="CmapCoverage"/>), and checked AGAIN afterwards by reading the object's text
        /// back, because a font that has the glyph can still lack a character code that reaches it.
        ///
        /// Callers must run <see cref="PdfContentInspector"/> over the pages first. Regenerating a
        /// content stream discards non-device colour, shadings, inline images and soft masks, which
        /// on an invoice or a chart means editing one word silently damages something else.
        /// </remarks>
        internal static TextEditResult ReplaceText(
            string srcPath, string destPath, IReadOnlyList<TextEditRequest> edits)
        {
            var result = new TextEditResult();
            if (!CanEditText)
            {
                result.Error = AvailableEditApi.HasFlag(EditApi.SetText)
                    ? DescribeEditApi()
                    : "this build's PDF engine cannot replace text in place";
                return result;
            }
            if (edits.Count == 0) { result.Ok = true; return result; }

            try { _ = DocLib.Instance; } catch { /* force PDFium init */ }

            lock (PdfiumLock)
            {
                IntPtr doc = FPDF_LoadDocumentRaw(srcPath, null);
                if (doc == IntPtr.Zero) { result.Error = "the document could not be opened"; return result; }
                try
                {
                    foreach (var group in edits.GroupBy(e => e.PageIndex))
                    {
                        IntPtr page = FPDF_LoadPageRaw(doc, group.Key);
                        if (page == IntPtr.Zero)
                        {
                            foreach (var e in group)
                                result.Items.Add((e, TextEditOutcome.NotFound, "the page could not be opened"));
                            continue;
                        }
                        // The text page is a SNAPSHOT: CPDF_TextPage builds its character list when
                        // it is loaded, and FPDFTextObj_GetText reads from that list. So every
                        // lookup has to happen before anything changes, and reading an object back
                        // through the same text page afterwards returns the text it used to have.
                        // Whether an edit actually took is a question about the finished file, and
                        // it is answered there — see PdfTextEdit.
                        IntPtr textPage = FPDFText_LoadPageRaw(page);
                        var pending = new List<(TextEditRequest Request, IntPtr Obj)>();
                        try
                        {
                            foreach (var e in group)
                            {
                                IntPtr obj = FindTextObject(page, textPage, e);
                                if (obj == IntPtr.Zero)
                                {
                                    result.Items.Add((e, TextEditOutcome.NotFound, "no matching text was found there"));
                                    continue;
                                }
                                string missing = UnrenderableCharacters(obj, e.NewText);
                                if (missing.Length > 0)
                                {
                                    result.Items.Add((e, TextEditOutcome.FontCannotRender, missing));
                                    continue;
                                }
                                pending.Add((e, obj));
                            }
                        }
                        finally
                        {
                            if (textPage != IntPtr.Zero) FPDFText_ClosePageRaw(textPage);
                        }

                        try
                        {
                            bool touched = false;
                            foreach (var (request, obj) in pending)
                            {
                                if (!FPDFText_SetTextRaw(obj, request.NewText))
                                {
                                    result.Items.Add((request, TextEditOutcome.NotApplied,
                                                      "the engine refused the replacement"));
                                    continue;
                                }
                                result.Items.Add((request, TextEditOutcome.Replaced, ""));
                                touched = true;
                            }

                            if (touched && !FPDFPage_GenerateContentRaw(page))
                            {
                                result.Error = $"content could not be regenerated for page {group.Key + 1}";
                                return result;
                            }
                        }
                        finally { FPDF_ClosePageRaw(page); }
                    }

                    if (!PdfiumSave(doc, destPath))
                    {
                        result.Error = "the edited document could not be written";
                        return result;
                    }
                    result.Ok = true;
                    return result;
                }
                finally { FPDF_CloseDocumentRaw(doc); }
            }
        }

        /// <summary>
        /// The text object at the requested position, preferring one whose text matches.
        /// </summary>
        /// <remarks>
        /// Position alone is not enough on a dense page — two runs can share a bounding box after
        /// rounding — and text alone is not enough either, since the same word appears many times.
        /// Requiring both is what keeps an edit from landing on the wrong line.
        /// </remarks>
        private static IntPtr FindTextObject(IntPtr page, IntPtr textPage, TextEditRequest edit)
        {
            IntPtr best = IntPtr.Zero;
            double bestScore = double.MaxValue;
            string want = Squash(edit.OriginalText);

            int count = FPDFPage_CountObjectsRaw(page);
            for (int i = 0; i < count; i++)
            {
                IntPtr obj = FPDFPage_GetObjectRaw(page, i);
                if (obj == IntPtr.Zero || FPDFPageObj_GetTypeRaw(obj) != PageObjText) continue;
                if (!FPDFPageObj_GetBoundsRaw(obj, out float l, out float b, out float r, out float t)) continue;
                if (!edit.Bounds.Intersects(l, b, r, t)) continue;

                string text = Squash(ReadTextObject(obj, textPage));
                if (want.Length > 0 && text != want && !text.Contains(want, StringComparison.Ordinal)) continue;

                // Closest centre wins among the candidates that survive both filters.
                double dx = (l + r) / 2 - (edit.Bounds.Left + edit.Bounds.Right) / 2;
                double dy = (b + t) / 2 - (edit.Bounds.Bottom + edit.Bounds.Top) / 2;
                double score = dx * dx + dy * dy;
                if (score < bestScore) { bestScore = score; best = obj; }
            }
            return best;
        }

        private static string ReadTextObject(IntPtr obj, IntPtr textPage)
        {
            if (textPage == IntPtr.Zero) return "";
            uint bytes = FPDFTextObj_GetTextRaw(obj, textPage, IntPtr.Zero, 0);
            if (bytes < 2) return "";
            IntPtr buf = Marshal.AllocHGlobal((int)bytes);
            try
            {
                uint written = FPDFTextObj_GetTextRaw(obj, textPage, buf, bytes);
                if (written < 2) return "";
                // UTF-16LE including a terminating NUL, which is not part of the string.
                return Marshal.PtrToStringUni(buf, (int)(written / 2) - 1) ?? "";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>
        /// Why <see cref="UnrenderableCharacters"/> answered as it did, for the test harness.
        /// </summary>
        /// <remarks>
        /// Kept because "the coverage check passed, so why did the edit come out full of holes?"
        /// is a question that will be asked again. It reports the three things that decide the
        /// answer — whether the font is embedded, whether its file could be read, and whether its
        /// cmap could be parsed — none of which is visible from the outcome alone.
        /// </remarks>
        internal static string DescribeFontCoverage(string srcPath, TextEditRequest edit)
        {
            try { _ = DocLib.Instance; } catch { }
            lock (PdfiumLock)
            {
                IntPtr doc = FPDF_LoadDocumentRaw(srcPath, null);
                if (doc == IntPtr.Zero) return "document would not open";
                try
                {
                    IntPtr page = FPDF_LoadPageRaw(doc, edit.PageIndex);
                    if (page == IntPtr.Zero) return "page would not open";
                    IntPtr textPage = FPDFText_LoadPageRaw(page);
                    try
                    {
                        IntPtr obj = FindTextObject(page, textPage, edit);
                        if (obj == IntPtr.Zero) return "no matching text object";
                        IntPtr font = FPDFTextObj_GetFontRaw(obj);
                        if (font == IntPtr.Zero) return "object has no font";

                        int embedded = FPDFFont_GetIsEmbeddedRaw(font);
                        bool sized = FPDFFont_GetFontDataRaw(font, null, UIntPtr.Zero, out UIntPtr needed);
                        int size = (int)needed;
                        string cmap = "not read";
                        if (sized && size > 0 && size < 32 * 1024 * 1024)
                        {
                            var data = new byte[size];
                            if (FPDFFont_GetFontDataRaw(font, data, (UIntPtr)size, out _))
                                cmap = CmapCoverage.Parse(data) is { } cov
                                    ? "parsed, missing \"" + new string(edit.NewText
                                        .Where(c => !char.IsWhiteSpace(c) && !cov.Covers(c)).Distinct().ToArray()) + "\""
                                    : "unparseable";
                        }
                        return $"embedded={embedded} fontBytes={size} cmap={cmap}";
                    }
                    finally
                    {
                        if (textPage != IntPtr.Zero) FPDFText_ClosePageRaw(textPage);
                        FPDF_ClosePageRaw(page);
                    }
                }
                finally { FPDF_CloseDocumentRaw(doc); }
            }
        }

        /// <summary>
        /// Characters in <paramref name="text"/> the object's own font has no glyph for.
        /// </summary>
        /// <remarks>
        /// Only embedded fonts are checked, and only because they are the ones that can fail: a
        /// subset carries the glyphs the document already used and nothing else. A NON-embedded
        /// font names a face the viewer substitutes from its own system fonts, so there is no font
        /// file to interrogate and no useful answer to give — checking it would mean refusing edits
        /// that work perfectly.
        /// </remarks>
        private static string UnrenderableCharacters(IntPtr obj, string text)
        {
            try
            {
                IntPtr font = FPDFTextObj_GetFontRaw(obj);
                if (font == IntPtr.Zero) return "";
                if (FPDFFont_GetIsEmbeddedRaw(font) != 1) return "";

                if (!FPDFFont_GetFontDataRaw(font, null, UIntPtr.Zero, out UIntPtr needed)) return "";
                int size = (int)needed;
                if (size <= 0 || size > 32 * 1024 * 1024) return "";
                var data = new byte[size];
                if (!FPDFFont_GetFontDataRaw(font, data, (UIntPtr)size, out _)) return "";

                var coverage = CmapCoverage.Parse(data);
                // No readable cmap is "cannot promise coverage", not "covers nothing" — refusing
                // every edit on a font we simply could not parse would be worse than letting the
                // read-back check below have the final word.
                if (coverage is null) return "";

                var missing = new List<char>();
                foreach (char c in text)
                {
                    if (char.IsControl(c) || c == ' ') continue;
                    if (!coverage.Covers(c) && !missing.Contains(c)) missing.Add(c);
                }
                return new string(missing.ToArray());
            }
            catch { return ""; }
        }

        /// <summary>
        /// Normalises whitespace for comparison.
        /// </summary>
        /// <remarks>
        /// PDF text is positioned, not spaced: a run may hold no space characters at all and get
        /// its gaps from the text matrix, so PDFium's extraction can differ from the original
        /// string by exactly the whitespace. Comparing on the non-space characters is what makes
        /// "did this actually take" answerable.
        /// </remarks>
        private static string Squash(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>One page's worth of removal. Returns true if anything was removed.</summary>
        private static bool RedactPageObjects(
            IntPtr page, int pageIndex, IReadOnlyList<PdfRect> rects,
            bool removePartial, RedactionOutcome outcome, List<IntPtr> orphaned)
        {
            bool touched = false;
            int count = FPDFPage_CountObjectsRaw(page);

            for (int i = count - 1; i >= 0; i--)
            {
                IntPtr obj = FPDFPage_GetObjectRaw(page, i);
                if (obj == IntPtr.Zero) continue;
                if (!FPDFPageObj_GetBoundsRaw(obj, out float l, out float b, out float r, out float t))
                    continue;

                int type = FPDFPageObj_GetTypeRaw(obj);
                var hit = Classify(rects, l, b, r, t);
                if (hit == Hit.None) continue;

                // A form can straddle a rectangle while only some of its children are inside it,
                // so descend rather than judging the wrapper by its bounding box.
                if (type == PageObjForm && CanEditFormXObjectContent)
                {
                    if (RedactFormObjects(obj, pageIndex, rects, removePartial, outcome, orphaned))
                        touched = true;
                    continue;
                }

                // An IMAGE is never removed on a partial overlap, whatever the caller asked for.
                // Over-deleting a text object costs a line, which is visible and can be re-marked.
                // Over-deleting an image costs whatever the image was — and on a scanned page that
                // is the ENTIRE PAGE, since the scan is one page-sized image that any mark merely
                // straddles. Blanking the page and reporting success is the exact failure this
                // feature exists to prevent, so the overlap is reported and the caller decides.
                if (hit == Hit.Partial && (type == PageObjImage || !removePartial))
                {
                    outcome.Partial.Add(new RedactionOverlap(
                        pageIndex, type, IsInvisibleText(obj, type), l, b, r, t));
                    continue;
                }

                if (FPDFPage_RemoveObjectRaw(page, obj))
                {
                    if (IsInvisibleText(obj, type)) outcome.InvisibleTextRemoved++;
                    orphaned.Add(obj);
                    outcome.ObjectsRemoved++;
                    touched = true;
                }
            }
            return touched;
        }

        private static bool RedactFormObjects(
            IntPtr form, int pageIndex, IReadOnlyList<PdfRect> rects,
            bool removePartial, RedactionOutcome outcome, List<IntPtr> orphaned)
        {
            bool touched = false;
            int count = FPDFFormObj_CountObjectsRaw(form);

            for (int i = count - 1; i >= 0; i--)
            {
                IntPtr child = FPDFFormObj_GetObjectRaw(form, (uint)i);
                if (child == IntPtr.Zero) continue;
                if (!FPDFPageObj_GetBoundsRaw(child, out float l, out float b, out float r, out float t))
                    continue;

                int type = FPDFPageObj_GetTypeRaw(child);
                var hit = Classify(rects, l, b, r, t);
                if (hit == Hit.None) continue;

                // Same rule as on the page itself: an image straddling a mark is reported, never
                // removed. See RedactPageObjects.
                if (hit == Hit.Partial && (type == PageObjImage || !removePartial))
                {
                    outcome.Partial.Add(new RedactionOverlap(
                        pageIndex, type, IsInvisibleText(child, type), l, b, r, t));
                    continue;
                }

                if (FPDFFormObj_RemoveObjectRaw(form, child))
                {
                    if (IsInvisibleText(child, type)) outcome.InvisibleTextRemoved++;
                    orphaned.Add(child);
                    outcome.ObjectsRemoved++;
                    touched = true;
                }
            }
            return touched;
        }

        private enum Hit { None, Partial, Inside }

        private static Hit Classify(IReadOnlyList<PdfRect> rects, double l, double b, double r, double t)
        {
            var best = Hit.None;
            for (int i = 0; i < rects.Count; i++)
            {
                if (rects[i].Contains(l, b, r, t)) return Hit.Inside;
                if (rects[i].Intersects(l, b, r, t)) best = Hit.Partial;
            }
            return best;
        }

        private static bool IsInvisibleText(IntPtr obj, int type)
        {
            if (type != PageObjText || !AvailableEditApi.HasFlag(EditApi.TextRenderMode)) return false;
            try { return FPDFTextObj_GetTextRenderModeRaw(obj) == TextRenderInvisible; }
            catch { return false; }
        }

        // ---- Content editing: runtime capability probe -----------------------------------------
        //
        // Redaction and real text editing both need to REMOVE objects from a page and have PDFium
        // regenerate the content stream. Whether that is possible at all depends entirely on which
        // pdfium.dll is loaded, and TDPdf does not build its own — it uses the one Docnet.Core
        // ships. Docnet 2.6.0 last shipped in September 2023, and its build is missing several
        // fpdf_edit.h entry points, most consequentially FPDFFormObj_RemoveObject: an object inside
        // a Form XObject can be enumerated but not removed. OCR'd scans routinely wrap their text
        // layer in exactly such a Form XObject, which is the document people most want to redact.
        //
        // So capability is PROBED rather than assumed. Swapping the native (for a current bblanchon
        // build, or PDFiumCore) then lights the surgical path up on its own; an older native
        // degrades to rasterising instead of throwing EntryPointNotFoundException in the middle of
        // a save; and the reason a given machine took the fallback becomes a reportable fact rather
        // than a guess.
        //
        // NOTE for whoever does swap it: never ship two natives with the simple name "pdfium". The
        // first LoadLibrary wins and BOTH DllImport sets bind to it, and under PublishSingleFile
        // with IncludeNativeLibrariesForSelfExtract you get whichever copy lands in the extraction
        // directory. That failure is silent and machine-dependent.

        [Flags]
        internal enum EditApi
        {
            None = 0,
            /// <summary>Enumerate, remove and regenerate objects on a page.</summary>
            PageObjects = 1 << 0,
            /// <summary>Descend into a Form XObject and remove objects from it.</summary>
            FormObjects = 1 << 1,
            /// <summary>Read a text object's rendering mode — how an OCR layer is identified.</summary>
            TextRenderMode = 1 << 2,
            /// <summary>Read a page object's bounding box.</summary>
            ObjectBounds = 1 << 3,
            /// <summary>Mark an object inactive rather than removing it: no ownership dance.</summary>
            SetIsActive = 1 << 4,
            /// <summary>Read and replace the string of an existing text object, keeping its font.</summary>
            SetText = 1 << 5,
        }

        /// <summary>
        /// What the pdfium.dll actually loaded in this process can do. Probed once, on first use —
        /// deliberately not at startup, since Docnet does not load pdfium until the first document
        /// is opened and forcing that load early would slow cold start for no benefit.
        /// </summary>
        internal static EditApi AvailableEditApi => s_editApi.Value;

        private static readonly Lazy<EditApi> s_editApi = new(ProbeEditApi);

        /// <summary>
        /// The minimum needed to remove glyphs from a page at all. False means redaction and text
        /// editing have no surgical path on this build and must rasterise instead.
        /// </summary>
        internal static bool CanEditPageContent =>
            AvailableEditApi.HasFlag(EditApi.PageObjects) && AvailableEditApi.HasFlag(EditApi.ObjectBounds);

        /// <summary>
        /// True when an object nested inside a Form XObject can be removed — the OCR'd-scan case,
        /// and the specific thing Docnet 2.6.0's build cannot do.
        /// </summary>
        internal static bool CanEditFormXObjectContent =>
            CanEditPageContent && AvailableEditApi.HasFlag(EditApi.FormObjects);

        /// <summary>Whether this build can replace the text of an existing text object in place.</summary>
        internal static bool CanEditText =>
            CanEditPageContent && AvailableEditApi.HasFlag(EditApi.SetText);

        private static EditApi ProbeEditApi()
        {
            var api = EditApi.None;
            IntPtr handle;
            try
            {
                // Resolves the module already in the process rather than loading a second copy:
                // by the time anything asks this, Docnet has pdfium loaded.
                if (!NativeLibrary.TryLoad("pdfium.dll", typeof(PdfiumInterop).Assembly, null, out handle))
                    handle = IntPtr.Zero;
            }
            catch { handle = IntPtr.Zero; }

            if (handle != IntPtr.Zero)
            {
                bool Has(params string[] names)
                {
                    foreach (var n in names)
                    {
                        try { if (!NativeLibrary.TryGetExport(handle, n, out _)) return false; }
                        catch { return false; }
                    }
                    return true;
                }

                if (Has("FPDFPage_CountObjects", "FPDFPage_GetObject", "FPDFPageObj_GetType",
                        "FPDFPage_RemoveObject", "FPDFPage_GenerateContent", "FPDFPageObj_Destroy"))
                    api |= EditApi.PageObjects;
                if (Has("FPDFFormObj_CountObjects", "FPDFFormObj_GetObject", "FPDFFormObj_RemoveObject"))
                    api |= EditApi.FormObjects;
                if (Has("FPDFTextObj_GetTextRenderMode")) api |= EditApi.TextRenderMode;
                if (Has("FPDFText_SetText", "FPDFTextObj_GetText", "FPDFTextObj_GetFont",
                        "FPDFFont_GetFontData", "FPDFFont_GetIsEmbedded", "FPDFText_LoadPage"))
                    api |= EditApi.SetText;
                if (Has("FPDFPageObj_GetBounds")) api |= EditApi.ObjectBounds;
                if (Has("FPDFPageObj_SetIsActive")) api |= EditApi.SetIsActive;
            }

            // Reported once per session, and only once something actually cares. This is the
            // question "does the native we ship support surgical editing?" answered from the fleet
            // rather than from a changelog.
            try
            {
                Telemetry.TrackEvent("Pdfium.EditCapability", new Dictionary<string, string>
                {
                    ["Api"] = api.ToString(),
                    ["CanEditPageContent"] = (api.HasFlag(EditApi.PageObjects) &&
                                              api.HasFlag(EditApi.ObjectBounds)) ? "true" : "false",
                    ["CanEditFormXObject"] = api.HasFlag(EditApi.FormObjects) ? "true" : "false",
                    ["Loaded"] = handle != IntPtr.Zero ? "true" : "false",
                });
            }
            catch { /* telemetry is opt-in and best-effort; never let it decide whether we can edit */ }

            return api;
        }

        /// <summary>
        /// A short reason, fit to show a user, why surgical editing is unavailable on this build.
        /// </summary>
        internal static string DescribeEditApi()
        {
            var api = AvailableEditApi;
            if (api == EditApi.None) return "this build's PDF engine cannot edit page content";
            if (!CanEditPageContent) return "this build's PDF engine cannot remove page objects";
            if (!CanEditFormXObjectContent)
                return "this build's PDF engine cannot edit inside form objects, which is where scanned pages keep their text";
            return "full";
        }
    }
}
