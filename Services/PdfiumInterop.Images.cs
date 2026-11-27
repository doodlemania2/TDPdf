using System.Runtime.InteropServices;
using System.Text;
using Docnet.Core;

namespace TDPdf.Services
{
    // The PDFium half of editing pictures on a page (#244): list the image objects, read one at its own
    // resolution, and move, resize, delete or replace one in the file itself.
    //
    // Same discipline as RemoveObjectsIntersecting in PdfiumInterop.cs: the whole document / page
    // lifetime lives inside one method that holds PdfiumLock, objects taken off a page are destroyed
    // only after the save, and every capability is probed before use.
    //
    // A picture is edited by changing the image OBJECT on the page (its matrix), never the image data it
    // points at: the same XObject can be drawn several times, and rewriting its pixels would change every
    // use. Replace therefore builds a NEW image object and takes the old one off the page.
    internal static partial class PdfiumInterop
    {
        // ---- Raw externs -------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct FpdfImageMetadata
        {
            public uint Width, Height;
            public float HorizontalDpi, VerticalDpi;
            public uint BitsPerPixel;
            public int ColorSpace;
            public int MarkedContentId;
        }

        [DllImport("pdfium.dll", EntryPoint = "FPDFImageObj_GetImagePixelSize", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFImageObj_GetImagePixelSizeRaw(IntPtr imageObject, out uint width, out uint height);

        [DllImport("pdfium.dll", EntryPoint = "FPDFImageObj_GetImageMetadata", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFImageObj_GetImageMetadataRaw(IntPtr imageObject, IntPtr page, out FpdfImageMetadata metadata);

        [DllImport("pdfium.dll", EntryPoint = "FPDFImageObj_GetImageFilterCount", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFImageObj_GetImageFilterCountRaw(IntPtr imageObject);

        [DllImport("pdfium.dll", EntryPoint = "FPDFImageObj_GetImageFilter", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FPDFImageObj_GetImageFilterRaw(IntPtr imageObject, int index, byte[]? buffer, uint length);

        [DllImport("pdfium.dll", EntryPoint = "FPDFImageObj_GetImageDataRaw", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FPDFImageObj_GetImageDataRawRaw(IntPtr imageObject, byte[]? buffer, uint length);

        [DllImport("pdfium.dll", EntryPoint = "FPDFImageObj_GetBitmap", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFImageObj_GetBitmapRaw(IntPtr imageObject);

        [DllImport("pdfium.dll", EntryPoint = "FPDFImageObj_GetRenderedBitmap", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFImageObj_GetRenderedBitmapRaw(IntPtr document, IntPtr page, IntPtr imageObject);

        [DllImport("pdfium.dll", EntryPoint = "FPDFImageObj_SetBitmap", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFImageObj_SetBitmapRaw(IntPtr pages, int count, IntPtr imageObject, IntPtr bitmap);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPageObj_NewImageObj", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFPageObj_NewImageObjRaw(IntPtr document);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPage_InsertObjectAtIndex", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFPage_InsertObjectAtIndexRaw(IntPtr page, IntPtr pageObject, nuint index);

        [DllImport("pdfium.dll", EntryPoint = "FPDFBitmap_GetBuffer", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFBitmap_GetBufferRaw(IntPtr bitmap);

        [DllImport("pdfium.dll", EntryPoint = "FPDFBitmap_GetWidth", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFBitmap_GetWidthRaw(IntPtr bitmap);

        [DllImport("pdfium.dll", EntryPoint = "FPDFBitmap_GetHeight", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFBitmap_GetHeightRaw(IntPtr bitmap);

        [DllImport("pdfium.dll", EntryPoint = "FPDFBitmap_GetStride", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFBitmap_GetStrideRaw(IntPtr bitmap);

        [DllImport("pdfium.dll", EntryPoint = "FPDFBitmap_GetFormat", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFBitmap_GetFormatRaw(IntPtr bitmap);

        private const int BitmapGray = 1, BitmapBgr = 2, BitmapBgrx = 3, BitmapBgra = 4, BitmapBgraPremul = 5;

        // FPDF_COLORSPACE_* from fpdf_edit.h. Only the ones that matter to "can these bytes leave as a plain JPEG".
        private const int ColorSpaceDeviceCmyk = 3;

        // ---- Capability --------------------------------------------------------------------------

        private static readonly Lazy<bool> s_canEditPictures = new(ProbeEditPictures);

        /// <summary>
        /// Whether the PDFium in this process can list, read, move, remove and replace image objects.
        /// Probed like <see cref="CanEditPageContent"/>: an older native degrades to the overlay edit
        /// instead of throwing in the middle of a save.
        /// </summary>
        internal static bool CanEditPictures => CanEditPageContent && s_canEditPictures.Value;

        private static bool ProbeEditPictures()
        {
            try
            {
                if (!NativeLibrary.TryLoad("pdfium.dll", typeof(PdfiumInterop).Assembly, null, out var h)) return false;
                foreach (var n in new[]
                {
                    "FPDFImageObj_GetImagePixelSize", "FPDFImageObj_GetImageMetadata", "FPDFImageObj_GetImageFilterCount",
                    "FPDFImageObj_GetImageFilter", "FPDFImageObj_GetImageDataRaw", "FPDFImageObj_GetBitmap",
                    "FPDFImageObj_GetRenderedBitmap", "FPDFImageObj_SetBitmap", "FPDFPageObj_NewImageObj",
                    "FPDFPage_InsertObjectAtIndex", "FPDFPage_InsertObject", "FPDFPageObj_GetMatrix",
                    "FPDFPageObj_SetMatrix", "FPDFBitmap_GetBuffer", "FPDFBitmap_GetStride", "FPDFBitmap_GetFormat",
                    "FPDFBitmap_GetWidth", "FPDFBitmap_GetHeight",
                })
                    if (!NativeLibrary.TryGetExport(h, n, out _)) return false;
                return true;
            }
            catch { return false; }
        }

        // ---- Listing -----------------------------------------------------------------------------

        /// <summary>One picture on a page, as PDFium sees it.</summary>
        /// <param name="ObjectIndex">Index among the page's top-level objects. For a picture inside a
        /// form object it is the form's index, and <paramref name="InForm"/> is true.</param>
        internal sealed record ImageObjectInfo(
            int ObjectIndex, PdfRect Bounds, Affine Matrix, int PixelWidth, int PixelHeight,
            IReadOnlyList<string> Filters, int ColorSpace, bool InForm);

        /// <summary>
        /// Every picture on the page, in drawing order (the last is the topmost): the page's own image
        /// objects, and the images one level inside its form objects, flagged <c>InForm</c> because they
        /// can be seen but not edited in place. Null when the file or page cannot be opened.
        /// </summary>
        internal static List<ImageObjectInfo>? ListImageObjects(string path, int pageIndex)
        {
            if (!CanEditPictures) return null;
            try { _ = DocLib.Instance; } catch { /* force PDFium init */ }

            lock (PdfiumLock)
            {
                IntPtr doc = FPDF_LoadDocumentRaw(path, null);
                if (doc == IntPtr.Zero) return null;
                try
                {
                    IntPtr page = FPDF_LoadPageRaw(doc, pageIndex);
                    if (page == IntPtr.Zero) return null;
                    try
                    {
                        var list = new List<ImageObjectInfo>();
                        int count = FPDFPage_CountObjectsRaw(page);
                        for (int i = 0; i < count; i++)
                        {
                            IntPtr obj = FPDFPage_GetObjectRaw(page, i);
                            if (obj == IntPtr.Zero) continue;
                            int type = FPDFPageObj_GetTypeRaw(obj);
                            if (type == PageObjImage)
                            {
                                if (Describe(obj, page, i, inForm: false) is { } info) list.Add(info);
                            }
                            else if (type == PageObjForm && AvailableEditApi.HasFlag(EditApi.FormObjects)
                                     && FormHoldsImage(obj)
                                     && FPDFPageObj_GetBoundsRaw(obj, out float l, out float b, out float r, out float t))
                            {
                                list.Add(new ImageObjectInfo(i, new PdfRect(l, b, r, t),
                                    new Affine(r - l, 0, 0, t - b, l, b), 0, 0, Array.Empty<string>(), 0, true));
                            }
                        }
                        return list;
                    }
                    finally { FPDF_ClosePageRaw(page); }
                }
                finally { FPDF_CloseDocumentRaw(doc); }
            }
        }

        private static bool FormHoldsImage(IntPtr form)
        {
            int n = FPDFFormObj_CountObjectsRaw(form);
            for (int i = 0; i < n; i++)
            {
                IntPtr child = FPDFFormObj_GetObjectRaw(form, (uint)i);
                if (child != IntPtr.Zero && FPDFPageObj_GetTypeRaw(child) == PageObjImage) return true;
            }
            return false;
        }

        private static ImageObjectInfo? Describe(IntPtr obj, IntPtr page, int index, bool inForm)
        {
            if (!FPDFPageObj_GetBoundsRaw(obj, out float l, out float b, out float r, out float t)) return null;
            if (!FPDFPageObj_GetMatrixRaw(obj, out FsMatrix m)) return null;

            FPDFImageObj_GetImagePixelSizeRaw(obj, out uint pw, out uint ph);
            int cs = FPDFImageObj_GetImageMetadataRaw(obj, page, out var meta) ? meta.ColorSpace : 0;

            var filters = new List<string>();
            int fc = Math.Max(0, FPDFImageObj_GetImageFilterCountRaw(obj));
            for (int f = 0; f < fc; f++)
            {
                uint need = FPDFImageObj_GetImageFilterRaw(obj, f, null, 0);
                if (need == 0 || need > 256) continue;
                var buf = new byte[need];
                uint got = FPDFImageObj_GetImageFilterRaw(obj, f, buf, need);
                if (got > 0) filters.Add(Encoding.ASCII.GetString(buf, 0, (int)Math.Min(got, need)).TrimEnd('\0'));
            }

            return new ImageObjectInfo(index, new PdfRect(l, b, r, t),
                new Affine(m.A, m.B, m.C, m.D, m.E, m.F),
                (int)Math.Min(pw, int.MaxValue), (int)Math.Min(ph, int.MaxValue), filters, cs, inForm);
        }

        /// <summary>
        /// Finds the image object a selection meant: the one at <paramref name="index"/> when its bounds
        /// are still where they were, else the picture on the page whose bounds are closest and within
        /// half a point. Null when the page no longer has it. Caller holds PdfiumLock.
        /// </summary>
        private static IntPtr ResolveImage(IntPtr page, int index, PdfRect expected, out int foundIndex)
        {
            const double Tolerance = 0.75;
            foundIndex = -1;
            int count = FPDFPage_CountObjectsRaw(page);

            bool Matches(IntPtr o) =>
                FPDFPageObj_GetTypeRaw(o) == PageObjImage
                && FPDFPageObj_GetBoundsRaw(o, out float l, out float b, out float r, out float t)
                && Math.Abs(l - expected.Left) <= Tolerance && Math.Abs(b - expected.Bottom) <= Tolerance
                && Math.Abs(r - expected.Right) <= Tolerance && Math.Abs(t - expected.Top) <= Tolerance;

            if (index >= 0 && index < count)
            {
                IntPtr o = FPDFPage_GetObjectRaw(page, index);
                if (o != IntPtr.Zero && Matches(o)) { foundIndex = index; return o; }
            }
            for (int i = count - 1; i >= 0; i--)
            {
                IntPtr o = FPDFPage_GetObjectRaw(page, i);
                if (o != IntPtr.Zero && Matches(o)) { foundIndex = i; return o; }
            }
            return IntPtr.Zero;
        }

        // ---- Reading a picture at its own resolution --------------------------------------------

        /// <summary>A picture lifted off the page.</summary>
        internal sealed class LiftedImage
        {
            internal string? Error { get; set; }

            /// <summary>The picture's original JPEG bytes, when it is a plain colour or grey JPEG left as it was.</summary>
            internal byte[]? Jpeg { get; set; }

            /// <summary>Straight BGRA pixels at the picture's own resolution (always set when there is no error).</summary>
            internal byte[]? Bgra { get; set; }
            internal int Width { get; set; }
            internal int Height { get; set; }
        }

        /// <summary>
        /// Reads one picture at native resolution. <paramref name="maxPixels"/> caps what is decoded: a
        /// 600 dpi page scan is hundreds of megabytes, and refusing it with a reason beats exhausting memory.
        /// </summary>
        internal static LiftedImage LiftImageObject(
            string path, int pageIndex, int objectIndex, PdfRect expected, long maxPixels)
        {
            var result = new LiftedImage();
            if (!CanEditPictures) { result.Error = "this build's PDF engine cannot read pictures"; return result; }
            try { _ = DocLib.Instance; } catch { /* force PDFium init */ }

            lock (PdfiumLock)
            {
                IntPtr doc = FPDF_LoadDocumentRaw(path, null);
                if (doc == IntPtr.Zero) { result.Error = "the document could not be opened"; return result; }
                try
                {
                    IntPtr page = FPDF_LoadPageRaw(doc, pageIndex);
                    if (page == IntPtr.Zero) { result.Error = "the page could not be opened"; return result; }
                    try
                    {
                        IntPtr obj = ResolveImage(page, objectIndex, expected, out _);
                        if (obj == IntPtr.Zero) { result.Error = "the picture is no longer where it was"; return result; }

                        if (!FPDFImageObj_GetImagePixelSizeRaw(obj, out uint pw, out uint ph) || pw == 0 || ph == 0)
                        { result.Error = "this picture's size cannot be read"; return result; }
                        if ((long)pw * ph > maxPixels)
                        { result.Error = $"this picture is too large to lift ({pw} x {ph} pixels)"; return result; }

                        TryReadOriginalJpeg(obj, page, result);

                        IntPtr bmp = FPDFImageObj_GetBitmapRaw(obj);
                        if (bmp == IntPtr.Zero)
                        { result.Error = "this picture uses a format that cannot be lifted"; return result; }
                        try
                        {
                            var (bgra, w, h, hadAlpha) = ReadBitmap(bmp);
                            if (bgra is null) { result.Error = "this picture's pixels cannot be read"; return result; }

                            // A soft-masked picture's colours and its mask are separate in the file, and
                            // GetBitmap returns only the colours. Take the mask from PDFium's own
                            // rendering of the object, where it can be done without a turn or flip to undo.
                            if (!hadAlpha && FPDFPageObj_GetMatrixRaw(obj, out FsMatrix m)
                                && m.A > 0 && m.D > 0 && Math.Abs(m.B) < 1e-4 && Math.Abs(m.C) < 1e-4)
                            {
                                IntPtr rendered = FPDFImageObj_GetRenderedBitmapRaw(doc, page, obj);
                                if (rendered != IntPtr.Zero)
                                {
                                    try
                                    {
                                        var (rb, rw, rh, renderedAlpha) = ReadBitmap(rendered);
                                        if (rb is not null && renderedAlpha && HasTransparency(rb))
                                            bgra = ColorKeyMask.WithAlphaFrom(bgra, w, h, rb, rw, rh) ?? bgra;
                                    }
                                    finally { FPDFBitmap_DestroyRaw(rendered); }
                                }
                            }

                            result.Bgra = bgra;
                            result.Width = w;
                            result.Height = h;
                            return result;
                        }
                        finally { FPDFBitmap_DestroyRaw(bmp); }
                    }
                    finally { FPDF_ClosePageRaw(page); }
                }
                finally { FPDF_CloseDocumentRaw(doc); }
            }
        }

        /// <summary>
        /// Sets <see cref="LiftedImage.Jpeg"/> when the picture's only filter is DCTDecode and the bytes
        /// are a grey or RGB JPEG that any viewer shows the way the PDF does. CMYK and Adobe-inverted
        /// JPEGs are left to the decoded path: their raw bytes look wrong outside a PDF.
        /// </summary>
        private static void TryReadOriginalJpeg(IntPtr obj, IntPtr page, LiftedImage result)
        {
            if (FPDFImageObj_GetImageFilterCountRaw(obj) != 1) return;
            uint need = FPDFImageObj_GetImageFilterRaw(obj, 0, null, 0);
            if (need == 0 || need > 256) return;
            var name = new byte[need];
            uint got = FPDFImageObj_GetImageFilterRaw(obj, 0, name, need);
            if (got == 0 || Encoding.ASCII.GetString(name, 0, (int)Math.Min(got, need)).TrimEnd('\0') != "DCTDecode") return;
            if (FPDFImageObj_GetImageMetadataRaw(obj, page, out var meta) && meta.ColorSpace == ColorSpaceDeviceCmyk) return;

            uint len = FPDFImageObj_GetImageDataRawRaw(obj, null, 0);
            if (len < 4 || len > 256u * 1024 * 1024) return;
            var data = new byte[len];
            if (FPDFImageObj_GetImageDataRawRaw(obj, data, len) != len) return;
            if (JpegProbe.IsPlainColour(data)) result.Jpeg = data;
        }

        private static bool HasTransparency(byte[] bgra)
        {
            for (int i = 3; i < bgra.Length; i += 4)
                if (bgra[i] < 255) return true;
            return false;
        }

        /// <summary>Copies a PDFium bitmap out as straight BGRA, whatever its native layout.</summary>
        private static (byte[]? Bgra, int W, int H, bool HasAlpha) ReadBitmap(IntPtr bmp)
        {
            int w = FPDFBitmap_GetWidthRaw(bmp), h = FPDFBitmap_GetHeightRaw(bmp);
            int stride = FPDFBitmap_GetStrideRaw(bmp);
            int fmt = FPDFBitmap_GetFormatRaw(bmp);
            IntPtr buf = FPDFBitmap_GetBufferRaw(bmp);
            if (w <= 0 || h <= 0 || stride <= 0 || buf == IntPtr.Zero || (long)w * h * 4 > int.MaxValue)
                return (null, 0, 0, false);

            int bytesPer = fmt switch { BitmapGray => 1, BitmapBgr => 3, BitmapBgrx or BitmapBgra or BitmapBgraPremul => 4, _ => 0 };
            if (bytesPer == 0 || stride < w * bytesPer) return (null, 0, 0, false);

            var row = new byte[stride];
            var o = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(new IntPtr(buf.ToInt64() + (long)y * stride), row, 0, stride);
                int oi = y * w * 4;
                for (int x = 0; x < w; x++, oi += 4)
                {
                    switch (fmt)
                    {
                        case BitmapGray:
                            o[oi] = o[oi + 1] = o[oi + 2] = row[x]; o[oi + 3] = 255;
                            break;
                        case BitmapBgr:
                            o[oi] = row[x * 3]; o[oi + 1] = row[x * 3 + 1]; o[oi + 2] = row[x * 3 + 2]; o[oi + 3] = 255;
                            break;
                        case BitmapBgrx:
                            o[oi] = row[x * 4]; o[oi + 1] = row[x * 4 + 1]; o[oi + 2] = row[x * 4 + 2]; o[oi + 3] = 255;
                            break;
                        case BitmapBgra:
                            o[oi] = row[x * 4]; o[oi + 1] = row[x * 4 + 1]; o[oi + 2] = row[x * 4 + 2]; o[oi + 3] = row[x * 4 + 3];
                            break;
                        default:   // premultiplied
                        {
                            int a = row[x * 4 + 3];
                            o[oi + 3] = (byte)a;
                            if (a == 0) { o[oi] = o[oi + 1] = o[oi + 2] = 0; }
                            else
                            {
                                o[oi] = (byte)Math.Min(255, row[x * 4] * 255 / a);
                                o[oi + 1] = (byte)Math.Min(255, row[x * 4 + 1] * 255 / a);
                                o[oi + 2] = (byte)Math.Min(255, row[x * 4 + 2] * 255 / a);
                            }
                            break;
                        }
                    }
                }
            }
            return (o, w, h, fmt is BitmapBgra or BitmapBgraPremul);
        }

        // ---- Editing -----------------------------------------------------------------------------

        internal enum PictureOp { SetMatrix, Remove, Replace }

        internal sealed class PictureEditOutcome
        {
            internal bool Ok { get; set; }
            internal string? Error { get; set; }
        }

        /// <summary>
        /// Changes one picture on one page and writes the whole file to <paramref name="destPath"/>.
        /// </summary>
        /// <param name="matrix">The new placement, for <see cref="PictureOp.SetMatrix"/>.</param>
        /// <param name="bgra">The replacement pixels (straight BGRA), for <see cref="PictureOp.Replace"/>.</param>
        /// <remarks>
        /// LIFETIME ORDERING is the one redaction uses: change the page, THEN regenerate its content,
        /// THEN save, and only then destroy anything taken off the page. A replaced picture is inserted
        /// at the old object's index so the stacking order does not move.
        /// </remarks>
        internal static PictureEditOutcome EditImageObject(
            string srcPath, string destPath, int pageIndex, int objectIndex, PdfRect expected,
            PictureOp op, Affine? matrix, byte[]? bgra, int pixelWidth, int pixelHeight)
        {
            var outcome = new PictureEditOutcome();
            if (!CanEditPictures) { outcome.Error = "this build's PDF engine cannot change pictures in place"; return outcome; }
            if (op == PictureOp.SetMatrix && matrix is null) { outcome.Error = "no placement given"; return outcome; }
            if (op == PictureOp.Replace
                && (bgra is null || pixelWidth <= 0 || pixelHeight <= 0 || (long)pixelWidth * pixelHeight * 4 != bgra.Length))
            { outcome.Error = "the replacement picture is not usable"; return outcome; }
            try { _ = DocLib.Instance; } catch { /* force PDFium init */ }

            lock (PdfiumLock)
            {
                IntPtr doc = FPDF_LoadDocumentRaw(srcPath, null);
                if (doc == IntPtr.Zero) { outcome.Error = "the document could not be opened"; return outcome; }

                var orphaned = new List<IntPtr>();
                IntPtr newBitmap = IntPtr.Zero;
                try
                {
                    IntPtr page = FPDF_LoadPageRaw(doc, pageIndex);
                    if (page == IntPtr.Zero) { outcome.Error = "the page could not be opened"; return outcome; }
                    try
                    {
                        IntPtr obj = ResolveImage(page, objectIndex, expected, out int index);
                        if (obj == IntPtr.Zero) { outcome.Error = "the picture is no longer where it was"; return outcome; }

                        switch (op)
                        {
                            case PictureOp.SetMatrix:
                            {
                                var m = matrix!.Value;
                                var fs = new FsMatrix { A = (float)m.A, B = (float)m.B, C = (float)m.C, D = (float)m.D, E = (float)m.E, F = (float)m.F };
                                if (!FPDFPageObj_SetMatrixRaw(obj, ref fs)) { outcome.Error = "the picture could not be moved"; return outcome; }
                                break;
                            }
                            case PictureOp.Remove:
                                if (!FPDFPage_RemoveObjectRaw(page, obj)) { outcome.Error = "the picture could not be removed"; return outcome; }
                                orphaned.Add(obj);
                                break;
                            case PictureOp.Replace:
                            {
                                if (!FPDFPageObj_GetMatrixRaw(obj, out FsMatrix old)) { outcome.Error = "the picture's placement cannot be read"; return outcome; }
                                var placed = PicturePlacement.ReplacementMatrix(
                                    new Affine(old.A, old.B, old.C, old.D, old.E, old.F), pixelWidth, pixelHeight);

                                bool opaque = true;
                                for (int i = 3; i < bgra!.Length && opaque; i += 4) if (bgra[i] != 255) opaque = false;
                                newBitmap = FPDFBitmap_CreateExRaw(pixelWidth, pixelHeight, opaque ? BitmapBgrx : BitmapBgra, IntPtr.Zero, 0);
                                if (newBitmap == IntPtr.Zero) { outcome.Error = "memory for the replacement picture could not be reserved"; return outcome; }
                                IntPtr dst = FPDFBitmap_GetBufferRaw(newBitmap);
                                int stride = FPDFBitmap_GetStrideRaw(newBitmap);
                                if (dst == IntPtr.Zero || stride < pixelWidth * 4) { outcome.Error = "the replacement picture could not be prepared"; return outcome; }
                                for (int y = 0; y < pixelHeight; y++)
                                    Marshal.Copy(bgra, y * pixelWidth * 4, new IntPtr(dst.ToInt64() + (long)y * stride), pixelWidth * 4);

                                IntPtr fresh = FPDFPageObj_NewImageObjRaw(doc);
                                if (fresh == IntPtr.Zero) { outcome.Error = "the replacement picture could not be created"; return outcome; }
                                bool inserted = false;
                                try
                                {
                                    if (!FPDFImageObj_SetBitmapRaw(IntPtr.Zero, 0, fresh, newBitmap))
                                    { outcome.Error = "the replacement picture could not be attached"; return outcome; }
                                    var fm = new FsMatrix { A = (float)placed.A, B = (float)placed.B, C = (float)placed.C, D = (float)placed.D, E = (float)placed.E, F = (float)placed.F };
                                    if (!FPDFPageObj_SetMatrixRaw(fresh, ref fm))
                                    { outcome.Error = "the replacement picture could not be placed"; return outcome; }

                                    if (!FPDFPage_RemoveObjectRaw(page, obj)) { outcome.Error = "the old picture could not be removed"; return outcome; }
                                    orphaned.Add(obj);

                                    if (FPDFPage_InsertObjectAtIndexRaw(page, fresh, (nuint)index)) inserted = true;
                                    else { FPDFPage_InsertObjectRaw(page, fresh); inserted = true; }
                                }
                                finally { if (!inserted) FPDFPageObj_DestroyRaw(fresh); }
                                break;
                            }
                        }

                        if (!FPDFPage_GenerateContentRaw(page))
                        { outcome.Error = "the page's content could not be regenerated"; return outcome; }
                    }
                    finally { FPDF_ClosePageRaw(page); }

                    if (!PdfiumSave(doc, destPath)) { outcome.Error = "the edited document could not be written"; return outcome; }
                    outcome.Ok = true;
                    return outcome;
                }
                finally
                {
                    // After the save, never before.
                    foreach (var o in orphaned) { try { FPDFPageObj_DestroyRaw(o); } catch { } }
                    if (newBitmap != IntPtr.Zero) { try { FPDFBitmap_DestroyRaw(newBitmap); } catch { } }
                    FPDF_CloseDocumentRaw(doc);
                }
            }
        }
    }
}
