using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace TDPdf.Services
{
    /// <summary>
    /// Lays an EPUB out as pages and writes them as a PDF, using the WebView2 engine that already ships for
    /// the signing pages. The book is never given to the engine as a file: one document built by
    /// <see cref="EpubBook"/> is served from the zip at a made-up host, scripts are off, and every request
    /// for any other host is answered 403 before it leaves the process.
    /// </summary>
    /// <remarks>
    /// <b>Printing.</b> The DevTools <c>Page.printToPDF</c> method is used rather than
    /// <c>CoreWebView2.PrintToPdfAsync</c>: it honours the document's own <c>@page</c> size
    /// (<c>preferCSSPageSize</c>), writes tagged PDF, and returns the bytes directly. Both use Chromium's
    /// print path, so both keep text as text, subset the fonts, and write links as link annotations and the
    /// targets of in-document links as named destinations. The outline is not asked of Chromium: it is
    /// built from the book's own contents (<see cref="EpubOutline"/>) and written afterwards with PdfSharpCore.
    /// Chromium only writes a named destination for an id that a link in the document points at, which is why
    /// <see cref="EpubBook"/> adds a hidden list of links to every place the outline needs.
    /// Must be called on the UI thread.
    /// </remarks>
    internal static class EpubToPdf
    {
        private static readonly TimeSpan LayoutTimeout = TimeSpan.FromMinutes(3);

        internal static async Task<BookConversionResult> ConvertAsync(
            string epubPath, string outPdf, BookLayoutOptions options, CancellationToken ct)
        {
            if (!WebView2Bootstrap.EnsureLoader() || WebView2Bootstrap.RuntimeVersion() is null)
                throw new BookConversionException(
                    "Opening an EPUB needs the Microsoft WebView2 Runtime, and it was not found on this PC. " +
                    "It comes with Windows 11 and with Microsoft Edge. DjVu books do not need it.");

            ZipEpubArchive archive;
            EpubPackage package;
            EpubBook book;
            try
            {
                (archive, package, book) = await Task.Run(() =>
                {
                    var a = ZipEpubArchive.Open(epubPath);
                    try
                    {
                        var p = EpubPackage.Read(a);
                        return (a, p, EpubBook.Build(p, a, options));
                    }
                    catch { a.Dispose(); throw; }
                }, ct);
            }
            catch (InvalidDataException ex) { throw new BookConversionException("This EPUB could not be opened: " + ex.Message + ".", ex); }

            using (archive)
            {
                var server = new EpubResourceServer(archive, package, book);
                string rawPdf = TempFilePolicy.NewPath("epubraw");
                try
                {
                    await PrintAsync(server, rawPdf, ct);
                    var notices = new List<string>(book.Notices);
                    int pages = await Task.Run(() => Finish(rawPdf, outPdf, book, package, notices), ct);
                    int missing = server.Failed.Count;
                    if (missing > 0) notices.Add($"{missing} file(s) the book refers to could not be loaded (missing, protected or too large).");
                    return new BookConversionResult(pages, notices);
                }
                finally { TempFilePolicy.TryDelete(rawPdf); }
            }
        }

        private static async Task PrintAsync(EpubResourceServer server, string rawPdf, CancellationToken ct)
        {
            string profile = Path.Combine(Path.GetTempPath(), "tdpdf-epub-" + Guid.NewGuid().ToString("N")[..12]);
            var host = new Window
            {
                Width = 1, Height = 1, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                ShowActivated = false, Left = -10000, Top = -10000,
            };
            CoreWebView2Controller? controller = null;
            try
            {
                IntPtr hwnd = new WindowInteropHelper(host).EnsureHandle();
                var env = await CoreWebView2Environment.CreateAsync(null, profile, new CoreWebView2EnvironmentOptions
                {
                    AllowSingleSignOnUsingOSPrimaryAccount = false,
                });
                ct.ThrowIfCancellationRequested();
                controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
                controller.Bounds = new System.Drawing.Rectangle(0, 0, 900, 1200);
                var core = controller.CoreWebView2;

                var s = core.Settings;
                s.IsScriptEnabled = false;
                s.AreDefaultScriptDialogsEnabled = false;
                s.AreDevToolsEnabled = false;
                s.AreHostObjectsAllowed = false;
                s.IsWebMessageEnabled = false;
                s.AreDefaultContextMenusEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;
                s.IsStatusBarEnabled = false;
                s.IsPasswordAutosaveEnabled = false;
                s.IsGeneralAutofillEnabled = false;

                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                core.DownloadStarting += (_, e) => e.Cancel = true;
                core.NavigationStarting += (_, e) =>
                {
                    if (!IsBookUri(e.Uri)) e.Cancel = true;
                };

                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += async (_, e) =>
                {
                    var deferral = e.GetDeferral();
                    try
                    {
                        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || !IsBookUri(e.Request.Uri))
                        {
                            e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "Content-Security-Policy: default-src 'none'");
                            return;
                        }
                        var r = await Task.Run(() => server.Get(uri.AbsolutePath));
                        e.Response = env.CreateWebResourceResponse(new MemoryStream(r.Body, writable: false), r.Status,
                            r.Status == 200 ? "OK" : "Not Found",
                            $"Content-Type: {r.ContentType}\r\nContent-Security-Policy: {EpubResourceServer.ContentSecurityPolicy}\r\n" +
                            "X-Content-Type-Options: nosniff\r\nCache-Control: no-store");
                    }
                    finally { deferral.Complete(); }
                };

                var navigated = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
                core.NavigationCompleted += (_, e) => navigated.TrySetResult(e);
                core.Navigate($"https://{EpubResourceServer.Host}{EpubResourceServer.BookPath}");

                var winner = await Task.WhenAny(navigated.Task, Task.Delay(LayoutTimeout, ct));
                ct.ThrowIfCancellationRequested();
                if (winner != navigated.Task) throw new BookConversionException("Laying out the book took too long.");
                var nav = await navigated.Task;
                if (!nav.IsSuccess) throw new BookConversionException($"The book could not be laid out ({nav.WebErrorStatus}).");

                // Web fonts load lazily; ask the engine to wait for them. Script is off for the page, and
                // whether DevTools evaluation is allowed with it off depends on the runtime, so this is
                // best effort and followed by a short pause.
                try
                {
                    await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                        "{\"expression\":\"document.fonts.ready.then(function(){return true})\",\"awaitPromise\":true}");
                }
                catch { /* the pause below still gives them time */ }
                await Task.Delay(400, ct);

                string args = JsonSerializer.Serialize(new
                {
                    printBackground = true,
                    preferCSSPageSize = true,
                    displayHeaderFooter = false,
                    generateTaggedPDF = true,
                    transferMode = "ReturnAsBase64",
                });
                string result = await core.CallDevToolsProtocolMethodAsync("Page.printToPDF", args);
                ct.ThrowIfCancellationRequested();
                using var json = JsonDocument.Parse(result);
                string? b64 = json.RootElement.TryGetProperty("data", out var d) ? d.GetString() : null;
                if (string.IsNullOrEmpty(b64)) throw new BookConversionException("The book could not be printed to pages.");
                await File.WriteAllBytesAsync(rawPdf, Convert.FromBase64String(b64), ct);
            }
            finally
            {
                try { controller?.Close(); } catch { }
                try { host.Close(); } catch { }
                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < 6; i++)
                    {
                        await Task.Delay(1500);
                        try { if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true); else return; return; }
                        catch { /* the engine may still be shutting down */ }
                    }
                });
            }
        }

        private static bool IsBookUri(string uri) =>
            Uri.TryCreate(uri, UriKind.Absolute, out var u) &&
            u.Scheme == Uri.UriSchemeHttps && u.Host.Equals(EpubResourceServer.Host, StringComparison.OrdinalIgnoreCase);

        /// <summary>Adds the bookmarks and the title to the printed pages and writes the finished PDF.</summary>
        private static int Finish(string rawPdf, string outPdf, EpubBook book, EpubPackage package, List<string> notices)
        {
            using var doc = PdfReader.Open(rawPdf, PdfDocumentOpenMode.Modify);
            if (doc.PageCount == 0) throw new BookConversionException("The book printed no pages.");

            EpubOutline.ApplyTo(doc, book, package, notices);

            if (!string.IsNullOrWhiteSpace(book.Title)) doc.Info.Title = book.Title;
            if (!string.IsNullOrWhiteSpace(book.Author)) doc.Info.Author = book.Author;
            doc.Save(outPdf);
            return doc.PageCount;
        }
    }
}
