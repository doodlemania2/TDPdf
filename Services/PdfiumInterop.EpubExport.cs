using System.Runtime.InteropServices;
using System.Text;
using Docnet.Core;

namespace TDPdf.Services
{
    // The PDFium half of "Export as EPUB": the tagged-PDF structure tree, the PDF's embedded font
    // files, and page rasters for pictures and scanned pages. One session holds the document open
    // for the whole export; every call takes PdfiumLock for its own duration, so a long export does
    // not starve the viewer's renders between pages.
    internal static partial class PdfiumInterop
    {
        [DllImport("pdfium.dll", EntryPoint = "FPDF_StructTree_GetForPage", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_StructTree_GetForPageRaw(IntPtr page);

        [DllImport("pdfium.dll", EntryPoint = "FPDF_StructTree_Close", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_StructTree_CloseRaw(IntPtr tree);

        [DllImport("pdfium.dll", EntryPoint = "FPDF_StructTree_CountChildren", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDF_StructTree_CountChildrenRaw(IntPtr tree);

        [DllImport("pdfium.dll", EntryPoint = "FPDF_StructTree_GetChildAtIndex", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_StructTree_GetChildAtIndexRaw(IntPtr tree, int index);

        [DllImport("pdfium.dll", EntryPoint = "FPDF_StructElement_CountChildren", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDF_StructElement_CountChildrenRaw(IntPtr element);

        [DllImport("pdfium.dll", EntryPoint = "FPDF_StructElement_GetChildAtIndex", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_StructElement_GetChildAtIndexRaw(IntPtr element, int index);

        [DllImport("pdfium.dll", EntryPoint = "FPDF_StructElement_GetChildMarkedContentID", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDF_StructElement_GetChildMarkedContentIDRaw(IntPtr element, int index);

        [DllImport("pdfium.dll", EntryPoint = "FPDF_StructElement_GetMarkedContentID", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDF_StructElement_GetMarkedContentIDRaw(IntPtr element);

        [DllImport("pdfium.dll", EntryPoint = "FPDF_StructElement_GetType", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FPDF_StructElement_GetTypeRaw(IntPtr element, byte[]? buffer, uint buflen);

        [DllImport("pdfium.dll", EntryPoint = "FPDF_StructElement_GetAltText", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FPDF_StructElement_GetAltTextRaw(IntPtr element, byte[]? buffer, uint buflen);

        [DllImport("pdfium.dll", EntryPoint = "FPDF_StructElement_GetActualText", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FPDF_StructElement_GetActualTextRaw(IntPtr element, byte[]? buffer, uint buflen);

        [DllImport("pdfium.dll", EntryPoint = "FPDFFont_GetBaseFontName", CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr FPDFFont_GetBaseFontNameRaw(IntPtr font, byte[]? buffer, UIntPtr length);

        /// <summary>One element of a page's structure tree. Children are <see cref="EpubStructNode"/>s or boxed int MCIDs.</summary>
        internal sealed class EpubStructNode
        {
            internal string Type = "";
            internal string? Alt;
            internal string? ActualText;
            internal readonly List<object> Children = new();
        }

        /// <summary>The document, held open for an export.</summary>
        internal sealed class EpubSession : IDisposable
        {
            private IntPtr _doc;
            private const int MaxNodes = 200_000, MaxDepth = 64;

            private EpubSession(IntPtr doc) { _doc = doc; }

            internal static EpubSession? Open(string path)
            {
                try { _ = DocLib.Instance; } catch { }
                lock (PdfiumLock)
                {
                    IntPtr d = FPDF_LoadDocumentRaw(path, null);
                    return d == IntPtr.Zero ? null : new EpubSession(d);
                }
            }

            public void Dispose()
            {
                lock (PdfiumLock)
                {
                    if (_doc != IntPtr.Zero) FPDF_CloseDocumentRaw(_doc);
                    _doc = IntPtr.Zero;
                }
            }

            /// <summary>The page's structure tree, or null when it has none (an untagged page).</summary>
            internal List<EpubStructNode>? ReadStructure(int pageIndex)
            {
                lock (PdfiumLock)
                {
                    IntPtr page = FPDF_LoadPageRaw(_doc, pageIndex);
                    if (page == IntPtr.Zero) return null;
                    try
                    {
                        IntPtr tree = FPDF_StructTree_GetForPageRaw(page);
                        if (tree == IntPtr.Zero) return null;
                        try
                        {
                            int n = FPDF_StructTree_CountChildrenRaw(tree);
                            if (n <= 0) return null;
                            int budget = MaxNodes;
                            var roots = new List<EpubStructNode>();
                            for (int i = 0; i < n; i++)
                            {
                                IntPtr el = FPDF_StructTree_GetChildAtIndexRaw(tree, i);
                                if (el != IntPtr.Zero) roots.Add(ReadNode(el, 0, ref budget));
                            }
                            return roots;
                        }
                        finally { FPDF_StructTree_CloseRaw(tree); }
                    }
                    finally { FPDF_ClosePageRaw(page); }
                }
            }

            private static EpubStructNode ReadNode(IntPtr el, int depth, ref int budget)
            {
                var node = new EpubStructNode
                {
                    Type = Utf16(FPDF_StructElement_GetTypeRaw, el) ?? "",
                    Alt = Utf16(FPDF_StructElement_GetAltTextRaw, el),
                    ActualText = Utf16(FPDF_StructElement_GetActualTextRaw, el),
                };
                if (--budget <= 0 || depth >= MaxDepth) return node;
                int n = FPDF_StructElement_CountChildrenRaw(el);
                for (int i = 0; i < n && budget > 0; i++)
                {
                    IntPtr child = FPDF_StructElement_GetChildAtIndexRaw(el, i);
                    if (child != IntPtr.Zero) { node.Children.Add(ReadNode(child, depth + 1, ref budget)); continue; }
                    int mcid = FPDF_StructElement_GetChildMarkedContentIDRaw(el, i);
                    if (mcid >= 0) node.Children.Add(mcid);
                }
                if (n <= 0)
                {
                    int mcid = FPDF_StructElement_GetMarkedContentIDRaw(el);
                    if (mcid >= 0) node.Children.Add(mcid);
                }
                return node;
            }

            private static string? Utf16(Func<IntPtr, byte[]?, uint, uint> get, IntPtr el)
            {
                uint need = get(el, null, 0);
                if (need < 2 || need > 1_000_000) return null;
                var buf = new byte[need];
                uint got = get(el, buf, need);
                if (got < 2) return null;
                return Encoding.Unicode.GetString(buf, 0, (int)got - 2);
            }

            /// <summary>
            /// Collects the embedded font files used by text on a page into <paramref name="bag"/>, keyed
            /// by font name without its subset tag. Only EMBEDDED fonts are read: for any other font
            /// PDFium would hand back a system substitute, which is not the PDF's to give away.
            /// </summary>
            internal void CollectFonts(int pageIndex, Dictionary<string, List<byte[]>> bag, HashSet<string> seenFullNames)
            {
                lock (PdfiumLock)
                {
                    IntPtr page = FPDF_LoadPageRaw(_doc, pageIndex);
                    if (page == IntPtr.Zero) return;
                    try
                    {
                        int count = FPDFPage_CountObjectsRaw(page);
                        for (int i = 0; i < count; i++)
                        {
                            IntPtr obj = FPDFPage_GetObjectRaw(page, i);
                            if (obj == IntPtr.Zero || FPDFPageObj_GetTypeRaw(obj) != PageObjText) continue;
                            IntPtr font = FPDFTextObj_GetFontRaw(obj);
                            if (font == IntPtr.Zero) continue;
                            string full = BaseFontName(font);
                            if (full.Length == 0 || !seenFullNames.Add(full)) continue;
                            if (FPDFFont_GetIsEmbeddedRaw(font) != 1) continue;
                            if (!FPDFFont_GetFontDataRaw(font, null, UIntPtr.Zero, out UIntPtr needed)) continue;
                            long size = (long)needed;
                            if (size <= 0 || size > 32 * 1024 * 1024) continue;
                            var data = new byte[size];
                            if (!FPDFFont_GetFontDataRaw(font, data, (UIntPtr)(ulong)size, out _)) continue;
                            string key = EpubFontPolicy.BaseName(full);
                            if (!bag.TryGetValue(key, out var list)) bag[key] = list = new List<byte[]>();
                            list.Add(data);
                        }
                    }
                    finally { FPDF_ClosePageRaw(page); }
                }
            }

            private static string BaseFontName(IntPtr font)
            {
                var need = (ulong)FPDFFont_GetBaseFontNameRaw(font, null, UIntPtr.Zero);
                if (need < 2 || need > 4096) return "";
                var buf = new byte[need];
                FPDFFont_GetBaseFontNameRaw(font, buf, (UIntPtr)need);
                return Encoding.UTF8.GetString(buf, 0, (int)need - 1);
            }

            /// <summary>Renders the page as PDFium shows it (own /Rotate applied, no annotations) onto white, as BGRA.</summary>
            internal byte[]? RenderPage(int pageIndex, int width, int height)
            {
                if (width < 1 || height < 1 || (long)width * height > 80_000_000L) return null;
                lock (PdfiumLock)
                {
                    IntPtr page = FPDF_LoadPageRaw(_doc, pageIndex);
                    if (page == IntPtr.Zero) return null;
                    var pixels = new byte[width * height * 4];
                    var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                    try
                    {
                        IntPtr bmp = FPDFBitmap_CreateExRaw(width, height, FPDFBitmapBgra, pin.AddrOfPinnedObject(), width * 4);
                        if (bmp == IntPtr.Zero) return null;
                        try
                        {
                            FPDFBitmap_FillRectRaw(bmp, 0, 0, width, height, OpaqueWhite);
                            FPDF_RenderPageBitmapRaw(bmp, page, 0, 0, width, height, 0, 0);
                        }
                        finally { FPDFBitmap_DestroyRaw(bmp); }
                        return pixels;
                    }
                    finally { pin.Free(); FPDF_ClosePageRaw(page); }
                }
            }
        }
    }
}
