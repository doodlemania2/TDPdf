using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf.IO;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow
    {
        // ============================================================
        // OCR (Tesseract) - recognize text on a rendered page/region.
        //
        // Three PDF libraries stay in their lanes: Docnet/PDFium rasterizes the
        // page for the OCR engine, PdfSharpCore writes the invisible text layer
        // for the searchable-PDF export. Language data is downloaded on demand.
        //
        // NOT annotation-aware, deliberately (#141). Every other rasterize in the app now goes
        // through PdfiumInterop.RenderPageWithAnnotations so the markup a file carries reaches the
        // screen, the printer and the exported pixels. OCR is the exception: it rasterizes in order
        // to recognize the PAGE's own text, and a reviewer's sticky note, highlight or stamp is not
        // page content — feeding it to Tesseract would inject foreign words into the text layer and
        // let a highlight band obscure the words underneath it. These four call sites stay on the
        // plain GetImage() render on purpose; do not "fix" them to match the others.
        // ============================================================

        // Longest-side pixel budget for the OCR render. ~300 DPI on a Letter page, which is the sweet
        // spot for Tesseract: high enough for small body text, not so high it wastes time/memory.
        private const int OcrRenderMax = 2600;

        // Cancellation, the busy state and the cross-thread status line are shared with the other
        // long-running operations (see BeginCancellableOp / EndCancellableOp / SetWorkerStatus in
        // MainWindow.xaml.cs); Esc cancels whichever one is in flight.

        // ============================================================
        // OCR languages (multi-select, on-demand download)
        // ============================================================

        // Tesseract code -> display name. English is the default; every language (including English) is
        // downloaded on demand into OcrNativeBootstrap.TessDataDir, so nothing is bundled in the exe.
        private static readonly (string Code, string Name)[] OcrLanguageCatalog =
        {
            ("eng", "English"),
            ("spa", "Spanish"),
            ("fra", "French"),
            ("deu", "German"),
            ("ita", "Italian"),
            ("por", "Portuguese"),
            ("nld", "Dutch"),
            ("tur", "Turkish"),
            ("rus", "Russian"),
            ("jpn", "Japanese"),
            ("kor", "Korean"),
            ("chi_sim", "Chinese (Simplified)"),
            ("chi_tra", "Chinese (Traditional)"),
        };

        // True if <code>.traineddata exists in the tessdata folder. Nothing is bundled, so this is a pure
        // file-presence check.
        private static bool IsLanguageInstalled(string code) =>
            File.Exists(Path.Combine(OcrNativeBootstrap.TessDataDir, code + ".traineddata"));

        // The user's chosen OCR languages, persisted as a '+'-joined setting. Filtered to those actually
        // installed (a deleted pack can't be passed to Tesseract) and never empty - English is the floor.
        private static List<string> GetSelectedOcrLanguages()
        {
            var stored = (Properties.Settings.Default.OcrLanguages ?? "eng")
                .Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            var sel = new List<string>();
            foreach (var c in stored)
                if (IsLanguageInstalled(c) && !sel.Contains(c)) sel.Add(c);
            if (sel.Count == 0) sel.Add("eng");
            return sel;
        }

        private static void SetSelectedOcrLanguages(List<string> langs)
        {
            Properties.Settings.Default.OcrLanguages = string.Join("+", langs);
            Properties.Settings.Default.Save();
        }

        // The language string handed to Tesseract, e.g. "eng" or "eng+spa".
        private static string CurrentOcrLanguageString() => string.Join("+", GetSelectedOcrLanguages());

        // High-quality (tessdata_best) vs standard model preference, persisted. When on, downloads pull the
        // larger, more accurate "best" models and new languages keep using them.
        private static bool OcrHighQuality
        {
            get => Properties.Settings.Default.OcrHighQuality;
            set { Properties.Settings.Default.OcrHighQuality = value; Properties.Settings.Default.Save(); }
        }

        // Download URL for a language's traineddata, honoring the HQ preference.
        // Standard tier uses tessdata_fast: the same integer LSTM model as the full "tessdata" repo but without
        // the unused legacy-engine data, so it is ~4MB with identical LSTM accuracy. HQ uses tessdata_best
        // (float LSTM): larger but the most accurate.
        private static string LanguageDataUrl(string code) => OcrHighQuality
            ? $"https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/main/{code}.traineddata"
            : $"https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/{code}.traineddata";

        private static string NameForCode(string code)
        {
            foreach (var (c, n) in OcrLanguageCatalog) if (c == code) return n;
            return code;
        }

        // Tracks which installed languages currently hold the high-quality (best) model, so toggling HQ off
        // then on again doesn't re-download ones that are already HQ.
        private static HashSet<string> GetHqLanguages()
        {
            var set = new HashSet<string>();
            foreach (var c in (Properties.Settings.Default.OcrHqLanguages ?? "")
                     .Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries))
                set.Add(c);
            return set;
        }

        private static void MarkLanguageHq(string code, bool isHq)
        {
            var set = GetHqLanguages();
            if (isHq) set.Add(code); else set.Remove(code);
            Properties.Settings.Default.OcrHqLanguages = string.Join("+", set);
            Properties.Settings.Default.Save();
        }

        // Live rebuild of the Tools > OCR submenu each time it opens, so it reflects the current document and
        // the set of installed languages. The root MenuItem carries a placeholder child in XAML so the submenu
        // arrow shows and this event fires.
        private void OcrMenu_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            var root = (MenuItem)sender;
            root.Items.Clear();
            if (_doc is null)
            {
                root.Items.Add(new MenuItem { Header = "Open a PDF to use OCR", IsEnabled = false });
                return;
            }

            int selectedForOcr = PageList.SelectedItems.Count;
            root.Items.Add(MakeMenuItem(
                selectedForOcr > 1 ? $"OCR {selectedForOcr} Selected _Pages to Clipboard"
                                   : "OCR Current _Page to Clipboard",
                (_, _) => OcrPagesToClipboard(SelectedPageIndicesForOcr()),
                "Ctrl+Shift+O",
                selectedForOcr > 1
                    ? "Recognize every selected page and copy the combined text to the clipboard"
                    : "Recognize the current page's text and copy it to the clipboard", "\uEE6F"));
            root.Items.Add(MakeMenuItem("OCR _Region to Clipboard", (_, _) => BeginOcrRegion(),
                null, "Drag a box over an area to recognize just that region", "\uE7A8"));
            root.Items.Add(new Separator());
            root.Items.Add(MakeMenuItem("Make _Searchable PDF…", (_, _) => MakeSearchablePdf(),
                null, "OCR every page and save a copy with an invisible, searchable text layer", "\uE721"));
            root.Items.Add(MakeMenuItem("_Extract All Text…", (_, _) => ExtractAllText(),
                null, "OCR every page and save the text to a .txt or .md file", "\uE8A5"));
            root.Items.Add(new Separator());
            root.Items.Add(BuildLanguageMenu());
        }

        // Builds the multi-select Language submenu. Installed languages are checkable and stay toggled in the
        // open menu; not-yet-installed ones offer a one-time download. At least one language stays selected.
        private MenuItem BuildLanguageMenu()
        {
            string tessDir = OcrNativeBootstrap.EnsureTessDataDir();
            var selected = GetSelectedOcrLanguages();
            bool hqPref = OcrHighQuality;

            var root = new MenuItem { Header = "_Language" };

            // Header with the Tesseract language code right-aligned, mirroring the app's language lists.
            FrameworkElement LangHeader(string name, string code, string? suffix = null)
            {
                var dp = new DockPanel { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 180 };
                var codeTb = new TextBlock
                {
                    Text = code,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("TextSecondary"),
                    Margin = new Thickness(20, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(codeTb, Dock.Right);
                dp.Children.Add(codeTb);
                dp.Children.Add(new TextBlock
                {
                    Text = suffix is null ? name : $"{name}  {suffix}",
                    VerticalAlignment = VerticalAlignment.Center
                });
                return dp;
            }

            foreach (var (code, name) in OcrLanguageCatalog)
            {
                bool installed = File.Exists(Path.Combine(tessDir, code + ".traineddata"));
                if (installed)
                {
                    var item = new MenuItem
                    {
                        Header = LangHeader(name, code),
                        IsCheckable = true,
                        IsChecked = selected.Contains(code),
                        StaysOpenOnClick = true,
                    };
                    item.Click += (s, _) =>
                    {
                        var mi = (MenuItem)s!;
                        var sel = GetSelectedOcrLanguages();
                        if (mi.IsChecked) { if (!sel.Contains(code)) sel.Add(code); }
                        else
                        {
                            if (sel.Count <= 1) { mi.IsChecked = true; return; }   // keep at least one selected
                            sel.Remove(code);
                        }
                        SetSelectedOcrLanguages(sel);
                        SetStatus($"OCR language: {string.Join("+", sel)}");
                    };
                    root.Items.Add(item);
                }
                else
                {
                    var item = new MenuItem { Header = LangHeader(name, code, hqPref ? "(download HQ)" : "(download)") };
                    item.Click += (_, _) => DownloadOcrLanguage(code, name);
                    root.Items.Add(item);
                }
            }

            // High-quality toggle. Enabling it upgrades the selected languages and makes future downloads pull
            // the "best" models too.
            root.Items.Add(new Separator());
            var hq = new MenuItem
            {
                Header = "Use High Quality Models",
                IsChecked = hqPref,
            };
            hq.Click += (_, _) =>
            {
                bool now = !OcrHighQuality;
                OcrHighQuality = now;
                if (now) RedownloadSelectedHighQuality();
                else SetStatus("Standard OCR models will be used for new downloads");
            };
            root.Items.Add(hq);
            return root;
        }

        private static HttpClient MakeDownloadClient()
        {
            // Timeout covers connect + headers; the body is bounded by the cancellation token instead.
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TDPdf-OCR");
            return http;
        }

        // Streams one traineddata file to destFile, showing MB progress and honoring the cancel token; writes
        // via a .part file and atomically moves into place only on full success. Throws on cancel/error.
        private async Task DownloadTrainedDataAsync(HttpClient http, string url, string destFile, string label, CancellationToken ct)
        {
            string part = destFile + ".part";
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;
                using var netStream = await resp.Content.ReadAsStreamAsync(ct);
                using var fileStream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await netStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    double mb = read / 1048576.0;
                    SetWorkerStatus(total.HasValue
                        ? $"{label} {mb:F1} / {total.Value / 1048576.0:F1} MB  (Esc to cancel)"
                        : $"{label} {mb:F1} MB  (Esc to cancel)");
                }
            }
            if (File.Exists(destFile)) File.Delete(destFile);
            File.Move(part, destFile);
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
        }

        // Downloads a single language's traineddata (standard or HQ, per the toggle) and selects it.
        private async void DownloadOcrLanguage(string code, string name)
        {
            var ct = BeginCancellableOp($"Downloading {name} language data...");
            string tessDir = OcrNativeBootstrap.EnsureTessDataDir();
            string dest = Path.Combine(tessDir, code + ".traineddata");
            try
            {
                using var http = MakeDownloadClient();
                await DownloadTrainedDataAsync(http, LanguageDataUrl(code), dest, $"Downloading {name}...", ct);
                MarkLanguageHq(code, OcrHighQuality);

                var sel = GetSelectedOcrLanguages();
                if (!sel.Contains(code)) { sel.Add(code); SetSelectedOcrLanguages(sel); }
                SetStatus($"{name} installed - OCR language: {string.Join("+", GetSelectedOcrLanguages())}");
            }
            catch (OperationCanceledException)
            {
                TryDeleteFile(dest + ".part");
                if (ct.IsCancellationRequested) SetStatus($"{name} download cancelled");
                else TdpDialog.Show(this, $"Downloading {name} timed out. Check your connection and try again.",
                    "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                TryDeleteFile(dest + ".part");
                TdpDialog.Show(this, $"Could not download {name} language data:\n{ex.Message}", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EndCancellableOp();
            }
        }

        // Re-downloads every currently-selected language in high quality (tessdata_best), replacing the
        // standard copies. Triggered when the user enables "Use High Quality Models". Cancellable; a single
        // language's failure is reported but doesn't abort the rest, and a failed file never replaces a
        // working one (temp+move).
        private async void RedownloadSelectedHighQuality()
        {
            // Only UPGRADE languages that are actually installed and not already HQ. A language the user has
            // selected but hasn't downloaded yet must NOT be auto-downloaded here - that would surprise the
            // user with no prompt. It is fetched on the first OCR instead, via EnsureOcrModelsReadyAsync.
            var hq = GetHqLanguages();
            var toDownload = new List<string>();
            foreach (var c in GetSelectedOcrLanguages())
                if (IsLanguageInstalled(c) && !hq.Contains(c)) toDownload.Add(c);

            if (toDownload.Count == 0)
            {
                bool anyInstalled = false;
                foreach (var c in GetSelectedOcrLanguages()) if (IsLanguageInstalled(c)) { anyInstalled = true; break; }
                SetStatus(anyInstalled
                    ? "All selected languages are already high quality"
                    : "High quality models will be used the next time you run OCR");
                return;
            }

            var ct = BeginCancellableOp("Downloading high quality language models...");
            string tessDir = OcrNativeBootstrap.EnsureTessDataDir();
            var failed = new List<string>();
            try
            {
                using var http = MakeDownloadClient();
                int i = 0;
                foreach (var code in toDownload)
                {
                    if (ct.IsCancellationRequested) break;
                    i++;
                    string name = NameForCode(code);
                    string dest = Path.Combine(tessDir, code + ".traineddata");
                    string url = $"https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/main/{code}.traineddata";
                    try
                    {
                        await DownloadTrainedDataAsync(http, url, dest, $"Downloading {name} (HQ) - {i} of {toDownload.Count} -", ct);
                        MarkLanguageHq(code, true);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch { failed.Add(name); TryDeleteFile(dest + ".part"); }
                }
                if (ct.IsCancellationRequested) SetStatus("High quality download cancelled");
                else if (failed.Count > 0) SetStatus($"High quality models installed; failed: {string.Join(", ", failed)}");
                else SetStatus($"High quality models installed for: {string.Join("+", toDownload)}");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"High quality download failed:\n{ex.Message}", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EndCancellableOp();
            }
        }

        // Ensures the language models OCR is about to use are present on disk. Nothing is bundled, so on the
        // first OCR (or after the user adds a new language) the model is downloaded here, behind a heads-up
        // dialog. Returns true only when every required model is installed and OCR may proceed.
        private async Task<bool> EnsureOcrModelsReadyAsync()
        {
            var desired = new List<string>(
                (Properties.Settings.Default.OcrLanguages ?? "eng").Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries));
            if (desired.Count == 0) desired.Add("eng");

            var missing = new List<string>();
            foreach (var c in desired) if (!IsLanguageInstalled(c) && !missing.Contains(c)) missing.Add(c);
            if (missing.Count == 0) return true;

            string names = string.Join(", ", missing.ConvertAll(NameForCode));
            var choice = TdpDialog.Show(this,
                $"A language model ({names}) will be downloaded now so OCR can run.\n\n" +
                "You can add more languages or switch to higher quality models any time from the OCR menu.",
                "TDPdf", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (choice != MessageBoxResult.OK) return false;

            var ct = BeginCancellableOp("Downloading language model...");
            try
            {
                string tessDir = OcrNativeBootstrap.EnsureTessDataDir();
                using var http = MakeDownloadClient();
                for (int i = 0; i < missing.Count; i++)
                {
                    string code = missing[i];
                    string name = NameForCode(code);
                    string dest = Path.Combine(tessDir, code + ".traineddata");
                    await DownloadTrainedDataAsync(http, LanguageDataUrl(code), dest,
                        missing.Count == 1 ? $"Downloading {name}..." : $"Downloading {name} - {i + 1} of {missing.Count} -", ct);
                    MarkLanguageHq(code, OcrHighQuality);
                    if (ct.IsCancellationRequested) return false;
                }
                foreach (var c in missing) if (!IsLanguageInstalled(c)) return false;
                return true;
            }
            catch (OperationCanceledException)
            {
                SetStatus("Language download cancelled");
                return false;
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Could not download the language model:\n{ex.Message}",
                    "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                EndCancellableOp();
            }
        }

        // ============================================================
        // OCR the current page to the clipboard
        // ============================================================

        // Rasterize the page, recognize text off the UI thread, and drop the result on the clipboard. Render +
        // OCR are both slow, so they run inside Task.Run behind the busy state; the clipboard write happens back
        // on the UI thread. Rotations are already baked into the working file (SaveTempAndReload writes /Rotate,
        // which PDFium honors on render), so no pixel rotation is needed here.
        private void OcrPageToClipboard(int pageIdx) => OcrPagesToClipboard([pageIdx]);

        /// <summary>
        /// The page indices currently selected in the sidebar, in document order. Falls back to the
        /// current page so the OCR actions behave the same when nothing is multi-selected.
        /// </summary>
        private int[] SelectedPageIndicesForOcr()
        {
            int[] pages = PageList.SelectedItems.Cast<object>()
                .Select(o => PageList.Items.IndexOf(o))
                .Where(i => i >= 0)
                .OrderBy(i => i)
                .ToArray();
            return pages.Length > 0 ? pages : [Math.Max(0, PageList.SelectedIndex)];
        }

        // Upstream KillerPDF #297: OCR every selected page and combine the text, rather than only
        // the current one. The document reader and the recognition engine are built ONCE for the
        // whole batch — constructing an OcrService per page is the expensive part, and on a ten-page
        // selection that alone is the difference between usable and not.
        private async void OcrPagesToClipboard(IReadOnlyList<int> pageIndices)
        {
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            int[] pages = [.. pageIndices.Distinct().Where(p => p >= 0 && p < _doc.PageCount).OrderBy(p => p)];
            if (pages.Length == 0) return;
            if (!await EnsureOcrModelsReadyAsync()) return;

            string file = _currentFile;
            string lang = CurrentOcrLanguageString();

            var ct = BeginCancellableOp(pages.Length == 1
                ? "Running OCR...  (Esc to cancel)"
                : $"Running OCR on {pages.Length} pages...  (Esc to cancel)");
            try
            {
                List<OcrResult> results = await Task.Run(() =>
                {
                    using var docReader = DocLib.Instance.GetDocReader(file, new PageDimensions(OcrRenderMax, OcrRenderMax));
                    using var ocr = new OcrService(language: lang);   // engine is not thread-safe: one per operation
                    var recognized = new List<OcrResult>(pages.Length);
                    for (int i = 0; i < pages.Length; i++)
                    {
                        // Checked between pages: a single page still cannot be interrupted
                        // mid-recognition, but a long selection now stops at the next boundary.
                        if (ct.IsCancellationRequested) break;
                        if (pages.Length > 1)
                            SetWorkerStatus($"Running OCR... page {i + 1} of {pages.Length}  (Esc to cancel)");

                        using var pageReader = docReader.GetPageReader(pages[i]);
                        int w = pageReader.GetPageWidth();
                        int h = pageReader.GetPageHeight();
                        byte[] bgra = pageReader.GetImage();
                        recognized.Add(ocr.RecognizeBgra(bgra, w, h));
                    }
                    return recognized;
                });

                // Cooperative cancel: discard a partial batch rather than copying half of it.
                if (ct.IsCancellationRequested) { SetStatus("OCR cancelled"); return; }

                string text = string.Join(Environment.NewLine + Environment.NewLine,
                    results.Select(r => r.Text.Trim()).Where(t => t.Length > 0));
                if (text.Length == 0)
                {
                    SetStatus(pages.Length == 1
                        ? $"OCR: no text found on page {pages[0] + 1}"
                        : $"OCR: no text found on the {pages.Length} selected pages");
                    return;
                }

                Clipboard.SetText(text);
                double confidence = results.Count == 0 ? 0 : results.Average(r => r.MeanConfidence);
                SetStatus(pages.Length == 1
                    ? $"OCR: copied {text.Length} chars from page {pages[0] + 1} ({confidence:P0} confidence)"
                    : $"OCR: copied {text.Length} chars from {pages.Length} pages ({confidence:P0} mean confidence)");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"OCR failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EndCancellableOp();
            }
        }

        // ============================================================
        // OCR a dragged region to the clipboard
        // ============================================================

        // Armed by the menu item; the next box-drag (Select tool) crops that area of the page bitmap and OCRs
        // only it to the clipboard. Works on scans that have no text layer to extract from. Consumed in the
        // Select-tool mouse-up handler (see the _isSelecting block in the pointer code).
        private bool _ocrRegionMode;

        private void BeginOcrRegion()
        {
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            SetTool(EditTool.Select);
            _ocrRegionMode = true;
            SetStatus("Drag a box over the area to recognize");
        }

        private async void OcrRegion(int pageIdx, Rect canvasBounds)
        {
            if (_doc is null || _currentFile is null) return;
            if (pageIdx < 0 || pageIdx >= _doc.PageCount) return;
            if (!_renderDims.TryGetValue(pageIdx, out var rd) || rd.w <= 0 || rd.h <= 0) return;
            if (canvasBounds.Width < 4 || canvasBounds.Height < 4) { SetStatus("OCR region too small"); return; }
            if (!await EnsureOcrModelsReadyAsync()) return;

            string file = _currentFile;
            string lang = CurrentOcrLanguageString();
            int renderW = rd.w, renderH = rd.h;
            Rect cb = canvasBounds;

            var ct = BeginCancellableOp("Recognizing region...  (Esc to cancel)");
            try
            {
                OcrResult result = await Task.Run(() =>
                {
                    using var docReader = DocLib.Instance.GetDocReader(file, new PageDimensions(OcrRenderMax, OcrRenderMax));
                    using var pageReader = docReader.GetPageReader(pageIdx);
                    int w = pageReader.GetPageWidth();
                    int h = pageReader.GetPageHeight();
                    byte[] bgra = pageReader.GetImage();

                    // Map the on-screen canvas rect (renderW x renderH space) to the OCR render's pixel space.
                    double sx = (double)w / renderW, sy = (double)h / renderH;
                    byte[] crop = CropBgra(bgra, w, h,
                        (int)Math.Round(cb.Left * sx), (int)Math.Round(cb.Top * sy),
                        (int)Math.Round(cb.Width * sx), (int)Math.Round(cb.Height * sy),
                        out int cw, out int chh);

                    using var ocr = new OcrService(language: lang);
                    return ocr.RecognizeBgra(crop, cw, chh);
                });

                if (ct.IsCancellationRequested) { SetStatus("OCR cancelled"); return; }

                string text = result.Text.Trim();
                if (text.Length == 0) { SetStatus("OCR: no text found in the selected region"); return; }
                Clipboard.SetText(text);
                SetStatus($"OCR: copied {text.Length} chars from the region ({result.MeanConfidence:P0} confidence)");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"OCR failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                EndCancellableOp();
            }
        }

        private static byte[] CropBgra(byte[] src, int srcW, int srcH, int x, int y, int cw, int ch, out int outW, out int outH)
        {
            x = Math.Max(0, Math.Min(x, srcW - 1));
            y = Math.Max(0, Math.Min(y, srcH - 1));
            outW = Math.Max(1, Math.Min(cw, srcW - x));
            outH = Math.Max(1, Math.Min(ch, srcH - y));
            var dst = new byte[outW * outH * 4];
            for (int row = 0; row < outH; row++)
                Array.Copy(src, ((y + row) * srcW + x) * 4, dst, row * outW * 4, outW * 4);
            return dst;
        }

        // ============================================================
        // Make Searchable PDF - OCR every page and write an invisible text
        // layer aligned to the image, so the existing PdfPig search and text
        // selection start working on scans.
        // ============================================================

        private async void MakeSearchablePdf()
        {
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            if (!await EnsureOcrModelsReadyAsync()) return;
            CommitActiveTextBox();

            var dlg = new SaveFileDialog
            {
                Filter = "PDF files|*.pdf",
                Title = "Save Searchable PDF",
                FileName = SuggestSearchableName(),
                CheckFileExists = false,
                CheckPathExists = true
            };
            if (dlg.ShowDialog(this) != true) return;
            string outPath = dlg.FileName;

            // Snapshot the current document (with its rotations) to a temp; we render and re-open from this so
            // the live _doc is never touched. Unburned overlay annotations are not included.
            string src = MakeTempPdfPath("ocrsrc");
            try { _doc.Save(src); }
            catch (Exception ex)
            {
                TryDeleteFile(src);
                TdpDialog.Show(this, $"Could not prepare the document:\n{ex.Message}", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var ct = BeginCancellableOp("Making searchable PDF...  (Esc to cancel)");
            void report(int i, int n) => SetWorkerStatus($"Making searchable PDF... page {i + 1} of {n}  (Esc to cancel)");
            string lang = CurrentOcrLanguageString();

            try
            {
                var (pages, words) = await Task.Run(() => BuildSearchablePdf(src, outPath, report, ct, lang));
                if (ct.IsCancellationRequested) { SetStatus("Searchable PDF cancelled (no file written)"); return; }
                SetStatus($"Searchable PDF saved: {pages} pages, {words} words recognized");
                TdpDialog.Show(this,
                    $"Saved searchable PDF:\n{outPath}\n\n{pages} pages processed, {words} words recognized.",
                    "TDPdf", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Searchable PDF failed:\n{ex.Message}", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                TryDeleteFile(src);
                EndCancellableOp();
            }
        }

        // Renders each page, OCRs it, and appends an invisible (alpha 0) text layer positioned over the
        // recognized words. The text is real content-stream text, so PdfPig extracts it for search/select;
        // alpha 0 keeps it from showing or printing. Runs entirely off the UI thread.
        private static (int pages, int words) BuildSearchablePdf(string src, string outPath, Action<int, int> report, CancellationToken ct, string language)
        {
            // Cache one XFont per integer point size so a page of words doesn't allocate thousands of fonts.
            var fontCache = new Dictionary<int, XFont>();
            XFont FontFor(double heightPt)
            {
                int key = Math.Max(4, (int)Math.Round(heightPt));
                if (!fontCache.TryGetValue(key, out var f))
                {
                    try { f = new XFont("Arial", key, XFontStyle.Regular); }
                    catch { f = new XFont("Segoe UI", key, XFontStyle.Regular); }
                    fontCache[key] = f;
                }
                return f;
            }

            int totalWords = 0;
            var invisible = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));

            using var docReader = DocLib.Instance.GetDocReader(src, new PageDimensions(OcrRenderMax, OcrRenderMax));
            using var ocr = new OcrService(language: language);   // one engine reused across the whole document (single-threaded here)

            var outDoc = PdfReader.Open(src, PdfDocumentOpenMode.Modify);
            int pages = outDoc.PageCount;
            for (int i = 0; i < pages; i++)
            {
                // Cooperative cancel: bail before the next page; the caller sees the cancelled token and the
                // file is never saved (outDoc.Save is past the loop), so no partial output is written.
                if (ct.IsCancellationRequested) { outDoc.Close(); return (i, totalWords); }
                report(i, pages);

                using var pr = docReader.GetPageReader(i);
                int w = pr.GetPageWidth();
                int h = pr.GetPageHeight();
                byte[] bgra = pr.GetImage();
                if (bgra is null || bgra.Length == 0 || w <= 0 || h <= 0) continue;

                OcrResult result = ocr.RecognizeBgra(bgra, w, h);
                if (result.Words.Count == 0) continue;

                var page = outDoc.Pages[i];
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

                // OCR boxes are top-left pixel space; XGraphics is top-left point space. Same convention,
                // so mapping is a straight scale.
                double sx = page.Width.Point / w;
                double sy = page.Height.Point / h;

                foreach (var word in result.Words)
                {
                    double bx = word.Left * sx;
                    double by = word.Top * sy;
                    double bh = Math.Max(1, (word.Bottom - word.Top) * sy);
                    try
                    {
                        // (bx, by) is the top-left of the text by default (Near/Near alignment).
                        gfx.DrawString(word.Text, FontFor(bh), invisible, bx, by);
                        totalWords++;
                    }
                    catch { /* a single word that won't lay out should not abort the page */ }
                }
            }

            outDoc.Save(outPath);
            outDoc.Close();
            return (pages, totalWords);
        }

        // ============================================================
        // Extract All Text - OCR every page and save the plain text to a .txt or .md file.
        // ============================================================

        private async void ExtractAllText()
        {
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            if (!await EnsureOcrModelsReadyAsync()) return;
            CommitActiveTextBox();

            var dlg = new SaveFileDialog
            {
                Filter = "Text file|*.txt|Markdown|*.md",
                Title = "Extract All Text",
                FileName = SuggestBaseName() + ".txt",
                CheckFileExists = false,
                CheckPathExists = true
            };
            if (dlg.ShowDialog(this) != true) return;
            string outPath = dlg.FileName;
            bool markdown = Path.GetExtension(outPath).Equals(".md", StringComparison.OrdinalIgnoreCase);

            string src = MakeTempPdfPath("ocrtxt");
            int pageCount;
            try { _doc.Save(src); pageCount = _doc.PageCount; }
            catch (Exception ex)
            {
                TryDeleteFile(src);
                TdpDialog.Show(this, $"Could not prepare the document:\n{ex.Message}", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var ct = BeginCancellableOp("Extracting text...  (Esc to cancel)");
            void report(int i, int n) => SetWorkerStatus($"Extracting text... page {i + 1} of {n}  (Esc to cancel)");
            string lang = CurrentOcrLanguageString();

            try
            {
                int pages = await Task.Run(() => ExtractText(src, pageCount, outPath, markdown, report, ct, lang));
                if (ct.IsCancellationRequested) { SetStatus("Text extraction cancelled (no file written)"); return; }
                SetStatus($"Text extracted from {pages} pages -> {Path.GetFileName(outPath)}");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Text extraction failed:\n{ex.Message}", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                TryDeleteFile(src);
                EndCancellableOp();
            }
        }

        // OCR each page and concatenate the text into one file. Markdown gets a "## Page N" heading per page;
        // plain text uses a simple divider. Cancellable - nothing is written if cancelled.
        private static int ExtractText(string src, int pageCount, string outPath, bool markdown,
            Action<int, int> report, CancellationToken ct, string language)
        {
            string nl = Environment.NewLine;
            var sb = new StringBuilder();
            using var docReader = DocLib.Instance.GetDocReader(src, new PageDimensions(OcrRenderMax, OcrRenderMax));
            using var ocr = new OcrService(language: language);

            for (int i = 0; i < pageCount; i++)
            {
                if (ct.IsCancellationRequested) return 0;
                report(i, pageCount);

                using var pr = docReader.GetPageReader(i);
                int w = pr.GetPageWidth();
                int h = pr.GetPageHeight();
                byte[] bgra = pr.GetImage();
                string text = (bgra is null || bgra.Length == 0 || w <= 0 || h <= 0)
                    ? string.Empty
                    : ocr.RecognizeBgra(bgra, w, h).Text.TrimEnd();
                // Normalize Tesseract's LF line breaks to the platform's so .txt opens cleanly everywhere.
                text = text.Replace("\r\n", "\n").Replace("\n", nl);

                if (markdown)
                    sb.Append("## Page ").Append(i + 1).Append(nl).Append(nl).Append(text).Append(nl).Append(nl);
                else
                    sb.Append("----- Page ").Append(i + 1).Append(" -----").Append(nl).Append(text).Append(nl).Append(nl);
            }

            if (ct.IsCancellationRequested) return 0;
            File.WriteAllText(outPath, sb.ToString());
            return pageCount;
        }

        // ============================================================
        // Helpers
        // ============================================================

        // Base name for a suggested output file, derived from the document's display name (never a temp path).
        private string SuggestBaseName()
        {
            string name = string.IsNullOrWhiteSpace(_ctx.DisplayName) ? "document" : _ctx.DisplayName;
            name = Path.GetFileNameWithoutExtension(name);
            return string.IsNullOrWhiteSpace(name) ? "document" : name;
        }

        private string SuggestSearchableName() => SuggestBaseName() + "-searchable.pdf";

        // Shared with the image export (ExportImages.cs); TryDeleteFile above is its cleanup twin.
        private static string MakeTempPdfPath(string purpose) =>
            Path.Combine(Path.GetTempPath(), $"tdpdf_{purpose}_{Guid.NewGuid():N}.pdf");
    }
}
