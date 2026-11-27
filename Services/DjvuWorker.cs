using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TDPdf.Services
{
    /// <summary>
    /// The DjVu worker: <c>TDPdf.exe --djvu-worker 1</c>. It is the only process that ever loads DjVuLibre. It
    /// reads requests from stdin and answers on stdout (<see cref="DjvuWorkerProtocol"/>), opens one book,
    /// and exits when stdin closes or it is told to quit. A hostile book can crash or hang it; the parent
    /// kills it on a time limit and cap, and the person's tabs are not in this process.
    /// <para>It starts before any window or setting is touched (App.OnStartup) and refuses to run unless
    /// stdin is a pipe, so double-clicking a copy of the exe with the switch does nothing.</para>
    /// </summary>
    internal static class DjvuWorker
    {
        private const int FILE_TYPE_PIPE = 3;
        private const int STD_INPUT_HANDLE = -10;

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int which);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32", SetLastError = true)]
        private static extern int GetFileType(IntPtr handle);

        /// <summary>True when the process's stdin is a pipe (the parent made it one), not a console, a file or nothing.</summary>
        private static bool StdinIsPipe()
        {
            try
            {
                IntPtr h = GetStdHandle(STD_INPUT_HANDLE);
                return h != IntPtr.Zero && h != new IntPtr(-1) && GetFileType(h) == FILE_TYPE_PIPE;
            }
            catch { return false; }
        }

        /// <summary>Runs the worker to the end and returns the process exit code.</summary>
        internal static int Run()
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || !StdinIsPipe()) return 3;
            try
            {
                using var input = Console.OpenStandardInput();
                using var output = Console.OpenStandardOutput();
                return Serve(input, output);
            }
            catch { return 4; }
        }

        private static int Serve(Stream input, Stream output)
        {
            DjvuWorkerProtocol.WriteFrame(output, DjvuFrame.Hello, DjvuWorkerProtocol.EncodeHello());
            Book? book = null;
            try
            {
                while (true)
                {
                    (DjvuFrame type, byte[] payload) frame;
                    try { frame = DjvuWorkerProtocol.ReadFrame(input, DjvuWorkerProtocol.MaxRequestBytes); }
                    catch (EndOfStreamException) { return 0; }
                    catch (InvalidDataException) { return 5; }

                    if (frame.type == DjvuFrame.Quit) return 0;
                    try
                    {
                        switch (frame.type)
                        {
                            case DjvuFrame.Open:
                                if (book is not null) throw new InvalidDataException("a book is already open");
                                DjvuNative.Install();
                                book = Book.Open(DjvuWorkerProtocol.DecodeOpenRequest(frame.payload));
                                DjvuWorkerProtocol.WriteFrame(output, DjvuFrame.Open, DjvuWorkerProtocol.EncodeOpenReply(book.Header));
                                break;
                            case DjvuFrame.Page:
                            {
                                if (book is null) throw new InvalidDataException("no book is open");
                                var (page, max) = DjvuWorkerProtocol.DecodePageRequest(frame.payload, book.Header.Pages.Count);
                                book.RenderPage(page, max, output);
                                break;
                            }
                            case DjvuFrame.Text:
                            {
                                if (book is null) throw new InvalidDataException("no book is open");
                                int page = DjvuWorkerProtocol.DecodeTextRequest(frame.payload, book.Header.Pages.Count);
                                DjvuWorkerProtocol.WriteFrame(output, DjvuFrame.Text, book.PageText(page));
                                break;
                            }
                            case DjvuFrame.Outline:
                                if (book is null) throw new InvalidDataException("no book is open");
                                DjvuWorkerProtocol.WriteFrame(output, DjvuFrame.Outline, book.Outline());
                                break;
                            default:
                                throw new InvalidDataException("unknown request");
                        }
                    }
                    catch (Exception ex) when (ex is not IOException)
                    {
                        if (frame.type == DjvuFrame.Open) { book?.Dispose(); book = null; }
                        DjvuWorkerProtocol.WriteFrame(output, DjvuFrame.Error, DjvuWorkerProtocol.EncodeError(ex.Message));
                    }
                }
            }
            catch (IOException) { return 6; }   // the parent went away
            finally { book?.Dispose(); }
        }

        /// <summary>One open book: a DjVuLibre context, its document, and the message pump that drives them.</summary>
        private sealed class Book : IDisposable
        {
            private IntPtr _ctx, _doc;
            private string? _lastError;
            internal DjvuBookHeader Header { get; private set; } = new(Array.Empty<DjvuPageInfo>(), new Dictionary<string, int>());

            private Book() { }

            internal static Book Open(string path)
            {
                DjvuNativeBootstrap.LoadLibraries();
                var b = new Book();
                try
                {
                    b._ctx = DjvuNative.ddjvu_context_create("TDPdf");
                    if (b._ctx == IntPtr.Zero) throw new InvalidDataException("DjVuLibre could not start");
                    b._doc = DjvuNative.ddjvu_document_create_by_filename_utf8(b._ctx, path, 0);
                    if (b._doc == IntPtr.Zero) throw new InvalidDataException("DjVuLibre could not open the file");
                    IntPtr job = DjvuNative.ddjvu_document_job(b._doc);
                    b.WaitUntil(() => DjvuNative.ddjvu_job_status(job) >= DjvuNative.JobOk);
                    if (DjvuNative.ddjvu_job_status(job) != DjvuNative.JobOk)
                        throw new InvalidDataException(b._lastError ?? "this is not a DjVu document");
                    b.Header = b.ReadHeader();
                    return b;
                }
                catch { b.Dispose(); throw; }
            }

            private DjvuBookHeader ReadHeader()
            {
                int count = DjvuNative.ddjvu_document_get_pagenum(_doc);
                if (count < 1) throw new InvalidDataException("this DjVu book has no pages");
                if (count > DjvuWorkerProtocol.MaxPages) throw new InvalidDataException($"this DjVu book has {count:N0} pages, more than TDPdf opens");

                // A page whose size cannot be read is taken as the one before it (the first: 300 dpi letter);
                // rendering reports the true size.
                var pages = new List<DjvuPageInfo>(count);
                var last = new DjvuPageInfo(2550, 3300, 300);
                for (int i = 0; i < count; i++)
                {
                    DjvuNative.PageInfo info = default;
                    int st = 0;
                    WaitUntil(() => (st = DjvuNative.ddjvu_document_get_pageinfo_imp(_doc, i, out info, (uint)Marshal.SizeOf<DjvuNative.PageInfo>())) != DjvuNative.JobStarted);
                    if (st == DjvuNative.JobOk && info.Width > 0 && info.Height > 0 && info.Dpi > 0)
                        last = new DjvuPageInfo(info.Width, info.Height, info.Dpi);
                    pages.Add(last);
                }

                var ids = new Dictionary<string, int>(StringComparer.Ordinal);
                int files = Math.Min(DjvuNative.ddjvu_document_get_filenum(_doc), DjvuWorkerProtocol.MaxIds);
                for (int f = 0; f < files; f++)
                {
                    DjvuNative.FileInfo fi = default;
                    int st = 0;
                    WaitUntil(() => (st = DjvuNative.ddjvu_document_get_fileinfo_imp(_doc, f, out fi, (uint)Marshal.SizeOf<DjvuNative.FileInfo>())) != DjvuNative.JobStarted);
                    if (st == DjvuNative.JobOk && fi.Type == (byte)'P' && fi.PageNo >= 0 && fi.PageNo < count && fi.Id != IntPtr.Zero)
                    {
                        string? id = Marshal.PtrToStringUTF8(fi.Id);
                        if (!string.IsNullOrEmpty(id)) ids.TryAdd(id, fi.PageNo);
                    }
                }
                return new DjvuBookHeader(pages, ids);
            }

            // ── the pump ───────────────────────────────────────────────────────────────────────

            /// <summary>Takes every queued message, keeping the last error text. DjVuLibre posts all progress here.</summary>
            private void Drain()
            {
                IntPtr m;
                while ((m = DjvuNative.ddjvu_message_peek(_ctx)) != IntPtr.Zero)
                {
                    if (Marshal.ReadInt32(m) == DjvuNative.MessageError)
                    {
                        IntPtr text = Marshal.ReadIntPtr(m, DjvuNative.ErrorMessageOffset);
                        string? s = text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text);
                        if (!string.IsNullOrWhiteSpace(s)) _lastError = FirstLine(s);
                    }
                    DjvuNative.ddjvu_message_pop(_ctx);
                }
            }

            /// <summary>Drains, tests, and only then waits for the next message, so a finish that already happened is never waited for.</summary>
            private void WaitUntil(Func<bool> done)
            {
                while (true)
                {
                    Drain();
                    if (done()) return;
                    DjvuNative.ddjvu_message_wait(_ctx);
                }
            }

            private static string FirstLine(string s)
            {
                foreach (string l in s.Split('\n'))
                {
                    string t = l.Trim().TrimStart('*').Trim();
                    if (t.Length > 0) return t.Length > 200 ? t[..200] : t;
                }
                return "";
            }

            // ── a page ─────────────────────────────────────────────────────────────────────────

            internal void RenderPage(int index, long maxPixels, Stream output)
            {
                IntPtr page = DjvuNative.ddjvu_page_create_by_pageno(_doc, index);
                if (page == IntPtr.Zero) throw new InvalidDataException("DjVuLibre could not start the page");
                IntPtr job = DjvuNative.ddjvu_page_job(page);
                IntPtr format = IntPtr.Zero;
                GCHandle pin = default;
                try
                {
                    _lastError = null;
                    WaitUntil(() => DjvuNative.ddjvu_job_status(job) >= DjvuNative.JobOk);
                    if (DjvuNative.ddjvu_job_status(job) != DjvuNative.JobOk)
                        throw new InvalidDataException(_lastError ?? "the page could not be decoded");

                    int w = DjvuNative.ddjvu_page_get_width(page), h = DjvuNative.ddjvu_page_get_height(page);
                    if (w < 1 || h < 1) throw new InvalidDataException("the page has no size");
                    long pixels = (long)w * h;
                    int rw = w, rh = h;
                    if (pixels > maxPixels)
                    {
                        double f = Math.Sqrt((double)maxPixels / pixels);
                        rw = Math.Max(1, (int)(w * f));
                        rh = Math.Max(1, (int)(h * f));
                    }
                    if ((long)rw * rh > DjvuPnm.MaxPixels) throw new InvalidDataException("the page is too large");

                    bool bitonal = DjvuNative.ddjvu_page_get_type(page) == DjvuNative.PageTypeBitonal;
                    var kind = bitonal ? DjvuPageKind.Bitonal : DjvuPageKind.Color;
                    long rowBytes = bitonal ? (rw + 7) / 8 : (long)rw * 3;
                    long total = rowBytes * rh;
                    if (total > int.MaxValue / 2) throw new InvalidDataException("the page is too large");

                    format = DjvuNative.ddjvu_format_create(bitonal ? DjvuNative.FormatMsbToLsb : DjvuNative.FormatRgb24, 0, IntPtr.Zero);
                    if (format == IntPtr.Zero) throw new InvalidDataException("DjVuLibre could not set up the picture");
                    DjvuNative.ddjvu_format_set_row_order(format, 1);

                    var buffer = new byte[total];
                    pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    var rect = new DjvuNative.Rect { X = 0, Y = 0, W = (uint)rw, H = (uint)rh };
                    var rect2 = rect;
                    int ok = DjvuNative.ddjvu_page_render(page, bitonal ? DjvuNative.RenderBlack : DjvuNative.RenderColor,
                        ref rect, ref rect2, format, (nuint)rowBytes, pin.AddrOfPinnedObject());
                    if (ok == 0) throw new InvalidDataException(_lastError ?? "the page could not be drawn");
                    DjvuWorkerProtocol.WritePageReply(output, kind, rw, rh, buffer);
                }
                finally
                {
                    if (pin.IsAllocated) pin.Free();
                    if (format != IntPtr.Zero) DjvuNative.ddjvu_format_release(format);
                    DjvuNative.ddjvu_job_release(job);
                    Drain();
                }
            }

            // ── text and outline ───────────────────────────────────────────────────────────────

            internal byte[] PageText(int index) =>
                Print(() => DjvuNative.ddjvu_document_get_pagetext(_doc, index, IntPtr.Zero));

            internal byte[] Outline() =>
                Print(() => DjvuNative.ddjvu_document_get_outline(_doc));

            /// <summary>
            /// Asks for an s-expression, waits until DjVuLibre has it, and returns its text exactly as
            /// <c>miniexp_pname</c> prints it (the parent parses it; see <see cref="DjvuText"/>). "Nothing
            /// there" is an empty answer. The document keeps the expression alive until it is released.
            /// </summary>
            private byte[] Print(Func<IntPtr> get)
            {
                IntPtr expr = IntPtr.Zero;
                _lastError = null;
                WaitUntil(() => (expr = get()) != DjvuNative.MiniexpDummy);
                if (expr == DjvuNative.MiniexpNil) return Array.Empty<byte>();
                // Symbols ((expr & 3) == 2) are DjVuLibre's "failed" and "stopped"; a list is a pair (low bits 0).
                if (((long)expr & 3) != 0) throw new InvalidDataException(_lastError ?? "DjVuLibre could not read it");
                DjvuNative.minilisp_acquire_gc_lock(IntPtr.Zero);
                try
                {
                    IntPtr text = DjvuNative.miniexp_pname(expr, 0);
                    nuint len = DjvuNative.miniexp_to_lstr(text, out IntPtr chars);
                    if (chars == IntPtr.Zero || len == 0) return Array.Empty<byte>();
                    if (len > (nuint)DjvuProcessLimits.ForListing.MaxOutputBytes) throw new InvalidDataException("the text is too large");
                    var bytes = new byte[(int)len];
                    Marshal.Copy(chars, bytes, 0, bytes.Length);
                    return bytes;
                }
                finally
                {
                    DjvuNative.minilisp_release_gc_lock(IntPtr.Zero);
                    DjvuNative.ddjvu_miniexp_release(_doc, expr);
                }
            }

            public void Dispose()
            {
                try
                {
                    if (_doc != IntPtr.Zero) { DjvuNative.ddjvu_job_release(DjvuNative.ddjvu_document_job(_doc)); _doc = IntPtr.Zero; }
                    if (_ctx != IntPtr.Zero) { DjvuNative.ddjvu_context_release(_ctx); _ctx = IntPtr.Zero; }
                }
                catch { /* the process is about to exit */ }
            }
        }
    }
}
