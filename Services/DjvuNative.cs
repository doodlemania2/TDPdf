using System.Runtime.InteropServices;

namespace TDPdf.Services
{
    /// <summary>
    /// DjVuLibre's C API (<c>libdjvu/ddjvuapi.h</c>, <c>libdjvu/miniexp.h</c>, DjVuLibre 3.5.30), called from the
    /// DjVu worker process only. Everything is <c>cdecl</c>, 64-bit (the bundled DLLs are x64); handles are
    /// opaque pointers. The library is found through the resolver <see cref="Install"/> sets, which loads the
    /// bundled copy by full path (<see cref="DjvuNativeBootstrap"/>); nothing here uses the DLL search path.
    /// </summary>
    internal static class DjvuNative
    {
        private const string Lib = "djvulibre";

        // ddjvu_status_t
        internal const int JobStarted = 1, JobOk = 2;
        // ddjvu_message_tag_t
        internal const int MessageError = 0;
        // ddjvu_page_type_t
        internal const int PageTypeBitonal = 1;
        // ddjvu_render_mode_t
        internal const int RenderColor = 0, RenderBlack = 1;
        // ddjvu_format_style_t
        internal const int FormatRgb24 = 1, FormatMsbToLsb = 6;
        // miniexp_dummy is the address 2 and miniexp_nil is 0.
        internal static readonly IntPtr MiniexpDummy = new(2);
        internal static readonly IntPtr MiniexpNil = IntPtr.Zero;
        /// <summary>Offset of <c>message</c> in <c>ddjvu_message_error_s</c> on x64: tag, padding, four pointers.</summary>
        internal const int ErrorMessageOffset = 40;

        /// <summary>struct ddjvu_pageinfo_s { int width, height, dpi, rotation, version; }</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct PageInfo { public int Width, Height, Dpi, Rotation, Version; }

        /// <summary>struct ddjvu_fileinfo_s { char type; int pageno; int size; const char *id, *name, *title; }</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileInfo { public byte Type; public int PageNo; public int Size; public IntPtr Id, Name, Title; }

        /// <summary>struct ddjvu_rect_s { int x, y; unsigned int w, h; }</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { public int X, Y; public uint W, H; }

        private static bool _installed;

        /// <summary>Makes <c>DllImport("djvulibre")</c> resolve to the bundled, hash-checked library.</summary>
        internal static void Install()
        {
            if (_installed) return;
            NativeLibrary.SetDllImportResolver(typeof(DjvuNative).Assembly,
                (name, _, _) => name == Lib ? DjvuNativeBootstrap.LoadLibraries() : IntPtr.Zero);
            _installed = true;
        }

        // ── ddjvuapi.h ─────────────────────────────────────────────────────────────────────────

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr ddjvu_context_create([MarshalAs(UnmanagedType.LPUTF8Str)] string programName);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ddjvu_context_release(IntPtr context);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr ddjvu_message_peek(IntPtr context);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr ddjvu_message_wait(IntPtr context);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ddjvu_message_pop(IntPtr context);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ddjvu_job_status(IntPtr job);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ddjvu_job_release(IntPtr job);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr ddjvu_document_create_by_filename_utf8(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string filename, int cache);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr ddjvu_document_job(IntPtr document);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ddjvu_document_get_pagenum(IntPtr document);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ddjvu_document_get_filenum(IntPtr document);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ddjvu_document_get_fileinfo_imp(IntPtr document, int fileno, out FileInfo info, uint infosz);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ddjvu_document_get_pageinfo_imp(IntPtr document, int pageno, out PageInfo info, uint infosz);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr ddjvu_page_create_by_pageno(IntPtr document, int pageno);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr ddjvu_page_job(IntPtr page);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ddjvu_page_get_width(IntPtr page);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ddjvu_page_get_height(IntPtr page);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ddjvu_page_get_resolution(IntPtr page);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ddjvu_page_get_type(IntPtr page);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr ddjvu_format_create(int style, int nargs, IntPtr args);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ddjvu_format_set_row_order(IntPtr format, int topToBottom);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ddjvu_format_release(IntPtr format);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ddjvu_page_render(IntPtr page, int mode, ref Rect pageRect, ref Rect renderRect, IntPtr format, nuint rowSize, IntPtr imageBuffer);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr ddjvu_document_get_pagetext(IntPtr document, int pageno, IntPtr maxDetail);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr ddjvu_document_get_outline(IntPtr document);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ddjvu_miniexp_release(IntPtr document, IntPtr expr);

        // ── miniexp.h ──────────────────────────────────────────────────────────────────────────

        /// <summary>The expression printed as text on one line, as a miniexp string object.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr miniexp_pname(IntPtr expr, int width);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint miniexp_to_lstr(IntPtr expr, out IntPtr text);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr minilisp_acquire_gc_lock(IntPtr expr);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr minilisp_release_gc_lock(IntPtr expr);
    }
}
