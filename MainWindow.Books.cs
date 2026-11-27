using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TDPdf.Diagnostics;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow
    {
        // ============================================================
        // Books: EPUB and DjVu                                   (#235)
        // ============================================================
        //
        // A book is converted to a PDF in %TEMP% and that PDF is what opens, so every tool, the search,
        // print and the page views work on it unchanged. The tab is an UNSAVED tab (the same
        // FinalizeUnsavedTab the image import uses): Ctrl+S goes to Save As, which writes a PDF, and
        // nothing can write back over the .epub or .djvu. Conversion itself is in Services/Epub* (laid out by
        // WebView2, served from the zip with script off) and Services/Djvu* (DjVuLibre's own programs, run as
        // separate processes).

        private static bool IsBookPath(string p) => BookKinds.Of(p) != BookKind.None;

        /// <summary>Converts a book and opens the result in a tab. Failures are shown (or joined to a multi-file open's report).</summary>
        private async Task OpenBookAsync(string path)
        {
            var kind = BookKinds.Of(path);
            string name = System.IO.Path.GetFileName(path);
            if (!File.Exists(path))
            {
                ReportBookFailure(path, new FileNotFoundException("The file was not found."));
                return;
            }

            BookLayoutOptions? layout = null;
            if (kind == BookKind.Epub)
            {
                layout = CurrentBookLayout();
                if (!TDPdf.Properties.Settings.Default.BookLayoutChosen)
                {
                    // First EPUB: an EPUB has no pages, so ask once how they should look.
                    layout = ShowBookLayoutDialog(layout, firstOpen: true);
                    if (layout is null) { SetStatus("Open canceled"); return; }
                    SaveBookLayout(layout);
                }
            }

            Telemetry.TrackEvent("File.OpenBook", new Dictionary<string, string> { ["Kind"] = kind.ToString() });
            string temp = TempFilePolicy.NewPath("book");   // owned by the tab it opens in; deleted if the open fails
            var ct = BeginCancellableOp($"Opening {name}...  (Esc to cancel)");
            BookConversionResult? result = null;
            try
            {
                result = kind == BookKind.Epub
                    ? await EpubToPdf.ConvertAsync(path, temp, layout!, ct)
                    : await ConvertDjvuAsync(path, temp, ct);
            }
            catch (OperationCanceledException)
            {
                TempFilePolicy.TryDelete(temp);
                SetStatus("Open canceled");
                return;
            }
            catch (BookConversionException ex)
            {
                TempFilePolicy.TryDelete(temp);
                ReportBookFailure(path, ex);
                return;
            }
            catch (Exception ex)
            {
                Telemetry.TrackCrash(ex, "Open.Book", recoverable: true);
                TempFilePolicy.TryDelete(temp);
                ReportBookFailure(path, ex);
                return;
            }
            finally { EndCancellableOp(); }

            await OpenInTabAsync(temp);
            if (_doc is null || !string.Equals(_currentFile, temp, StringComparison.OrdinalIgnoreCase))
            {
                TempFilePolicy.TryDelete(temp);
                return;   // the converted file would not open; OpenFileAsync already said why
            }
            FinalizeUnsavedTab(temp, BookKinds.PdfNameFor(path),
                $"Opened {name} as a PDF: {result.Pages} page(s). Save As writes a PDF; the book is not changed", markDirty: false);

            if (result.Notices.Count > 0)
            {
                var lines = result.Notices.Take(12).Select(n => "- " + n);
                string more = result.Notices.Count > 12 ? $"\n- and {result.Notices.Count - 12} more." : "";
                TdpDialog.Show(this, $"{name} opened, with some changes:\n\n{string.Join("\n", lines)}{more}\n\nThe book itself is not changed.",
                    "TDPdf", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ReportBookFailure(string path, Exception ex)
        {
            Telemetry.TrackEvent("File.OpenFailed", new Dictionary<string, string>
            {
                ["ExceptionType"] = ex.GetType().FullName ?? "Unknown",
                ["Stage"] = "Book",
            });
            if (TryRecordOpenFailure(path, ex)) return;
            TdpDialog.Show(this, $"{System.IO.Path.GetFileName(path)} could not be opened:\n{ex.Message}",
                "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private Task<BookConversionResult> ConvertDjvuAsync(string path, string tempPdf, CancellationToken ct)
        {
            var tools = DjvuNativeBootstrap.Locate();
            if (tools is null)
                return Task.FromException<BookConversionResult>(new BookConversionException(DjvuMissingMessage));
            void Report((int Page, int Total) p) => SetWorkerStatus($"Opening book... page {Math.Min(p.Page + 1, p.Total)} of {p.Total}  (Esc to cancel)");
            return Task.Run(() =>
            {
                using var decoder = DjvuLibreDecoder.Open(tools, path, ct);
                return DjvuToPdf.Convert(decoder, tempPdf, null, new Progress<(int Page, int Total)>(Report), ct);
            }, ct);
        }

        internal const string DjvuMissingMessage =
            "Opening DjVu books needs DjVuLibre, and this copy of TDPdf does not include it. " +
            "Install DjVuLibre from djvu.sourceforge.net (it installs to Program Files\\DjVuLibre) and open the book again.";

        // ── layout options (EPUB) ─────────────────────────────────────────────────────────────

        private static BookLayoutOptions CurrentBookLayout()
        {
            var s = TDPdf.Properties.Settings.Default;
            return string.IsNullOrEmpty(s.BookPaper)
                ? BookLayoutOptions.Default(RegionInfo.CurrentRegion.IsMetric)
                : BookLayoutOptions.For(s.BookPaper, s.BookMarginMm, s.BookFontSizePt);
        }

        private static void SaveBookLayout(BookLayoutOptions o)
        {
            var s = TDPdf.Properties.Settings.Default;
            s.BookPaper = o.PaperName;
            s.BookMarginMm = o.MarginMm;
            s.BookFontSizePt = o.FontSizePt;
            s.BookLayoutChosen = true;
            try { s.Save(); } catch { /* the choice still applies for this session */ }
        }

        /// <summary>File ▸ Book Layout…: changes how books opened from now on are laid out.</summary>
        private void BookLayout_Click(object sender, RoutedEventArgs e)
        {
            var chosen = ShowBookLayoutDialog(CurrentBookLayout(), firstOpen: false);
            if (chosen is null) return;
            SaveBookLayout(chosen);
            SetStatus("Book layout saved. It applies to EPUB books you open from now on");
        }

        /// <summary>The page size, margin and text size dialog. Null when cancelled.</summary>
        private BookLayoutOptions? ShowBookLayoutDialog(BookLayoutOptions current, bool firstOpen)
        {
            var (win, body) = TdpDialog.CreateShell(this, "Book layout", 400);
            win.ShowInTaskbar = false;
            win.Foreground = BrushResource("TextPrimary");
            win.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
            win.FontSize = 12;
            var textSecondary = BrushResource("TextSecondary");

            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock
            {
                Text = firstOpen
                    ? "An EPUB has no pages of its own, so TDPdf lays it out as pages. Choose how they should look. This is asked once; File > Book Layout changes it later."
                    : "How EPUB books are laid out as pages when you open them.",
                Foreground = textSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            });

            TextBlock Label(string t) => new() { Text = t, Foreground = textSecondary, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) };
            TextBox Field(string v) => new()
            {
                Text = v, Foreground = BrushResource("TextPrimary"), Background = BrushResource("BgPanel"),
                BorderBrush = BrushResource("BorderDim"), BorderThickness = new Thickness(1), CaretBrush = BrushResource("AccentGreen"),
                Padding = new Thickness(6, 4, 6, 4), Height = 28, VerticalContentAlignment = VerticalAlignment.Center,
            };

            root.Children.Add(Label("Page size"));
            var paper = new ComboBox { Style = (Style)FindResource("DarkComboBox"), Height = 28 };
            foreach (var (n, _, _) in BookLayoutOptions.Papers) paper.Items.Add(n);
            paper.SelectedItem = current.PaperName;
            if (paper.SelectedIndex < 0) paper.SelectedIndex = 0;
            root.Children.Add(paper);

            root.Children.Add(Label("Margin (millimetres)"));
            var margin = Field(current.MarginMm.ToString("0.#", CultureInfo.CurrentCulture));
            root.Children.Add(margin);
            root.Children.Add(Label("Text size (points)"));
            var font = Field(current.FontSizePt.ToString("0.#", CultureInfo.CurrentCulture));
            root.Children.Add(font);
            var hint = new TextBlock { Foreground = textSecondary, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
                Text = $"Margin {BookLayoutOptions.MinMarginMm:0} to {BookLayoutOptions.MaxMarginMm:0} mm, text {BookLayoutOptions.MinFontPt:0} to {BookLayoutOptions.MaxFontPt:0} pt. A book's own style can still set its text sizes." };
            root.Children.Add(hint);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var cancel = new Button
            {
                Content = "Cancel", Width = 96, Height = 30, Margin = new Thickness(0, 0, 8, 0), Background = BrushResource("BgPanel"),
                Foreground = BrushResource("TextPrimary"), BorderBrush = BrushResource("BorderDim"), Cursor = System.Windows.Input.Cursors.Hand, IsCancel = true,
            };
            var ok = new Button
            {
                Content = firstOpen ? "Open book" : "Save", Width = 96, Height = 30, Background = BrushResource("AccentGreen"),
                Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.SemiBold, BorderBrush = BrushResource("AccentGreen"),
                Cursor = System.Windows.Input.Cursors.Hand, IsDefault = true,
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            root.Children.Add(buttons);
            body.Children.Add(root);

            BookLayoutOptions? result = null;
            cancel.Click += (_, _) => win.DialogResult = false;
            ok.Click += (_, _) =>
            {
                if (!double.TryParse(margin.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double m) || !double.IsFinite(m) ||
                    m < BookLayoutOptions.MinMarginMm || m > BookLayoutOptions.MaxMarginMm)
                {
                    hint.Text = $"Enter a margin between {BookLayoutOptions.MinMarginMm:0} and {BookLayoutOptions.MaxMarginMm:0} mm.";
                    hint.Foreground = BrushResource("DangerRed");
                    margin.Focus(); margin.SelectAll();
                    return;
                }
                if (!double.TryParse(font.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double f) || !double.IsFinite(f) ||
                    f < BookLayoutOptions.MinFontPt || f > BookLayoutOptions.MaxFontPt)
                {
                    hint.Text = $"Enter a text size between {BookLayoutOptions.MinFontPt:0} and {BookLayoutOptions.MaxFontPt:0} pt.";
                    hint.Foreground = BrushResource("DangerRed");
                    font.Focus(); font.SelectAll();
                    return;
                }
                result = BookLayoutOptions.For(paper.SelectedItem as string, m, f);
                win.DialogResult = true;
            };
            win.Loaded += (_, _) => ok.Focus();
            return win.ShowDialog() == true ? result : null;
        }

        // ── command line: TDPdf.exe --convert-book <book> <out.pdf> ─────────────────────────────

        // Runs a task that needs this thread's message loop (WebView2 does) to completion without
        // blocking the loop. App.OnStartup runs the CLI before any window exists, on the UI thread.
        private static T CliRunPumped<T>(Func<Task<T>> work)
        {
            var task = work();
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            if (!task.IsCompleted) Dispatcher.PushFrame(frame);
            return task.GetAwaiter().GetResult();
        }

        private static int CliConvertBook(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: TDPdf.exe --convert-book <book.epub|book.djvu> <out.pdf> [--paper Letter|A4|A5] [--margin <mm>] [--font <pt>]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            var kind = BookKinds.Of(inPath);
            if (kind == BookKind.None) { con.WriteLine("Not a book TDPdf converts (.epub, .djvu, .djv): " + inPath); return 2; }
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            if (string.Equals(inPath, outPath, StringComparison.OrdinalIgnoreCase)) { con.WriteLine("The output must be a different file from the book."); return 2; }
            if (!outPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) { con.WriteLine("The output file name must end in .pdf"); return 2; }

            options.TryGetValue("--paper", out var paperRaw);
            double margin = 20, font = 11;
            if (options.TryGetValue("--margin", out var mRaw) && !double.TryParse(mRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out margin)) { con.WriteLine("--margin must be a number of millimetres."); return 2; }
            if (options.TryGetValue("--font", out var fRaw) && !double.TryParse(fRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out font)) { con.WriteLine("--font must be a number of points."); return 2; }
            string paper = string.IsNullOrWhiteSpace(paperRaw) ? (RegionInfo.CurrentRegion.IsMetric ? "A4" : "Letter") : paperRaw!.Trim();
            if (!BookLayoutOptions.Papers.Any(p => p.Name.StartsWith(paper, StringComparison.OrdinalIgnoreCase)))
            {
                con.WriteLine("Unknown --paper. Choose one of: " + string.Join(", ", BookLayoutOptions.Papers.Select(p => p.Name)));
                return 2;
            }
            var layout = BookLayoutOptions.For(BookLayoutOptions.Papers.First(p => p.Name.StartsWith(paper, StringComparison.OrdinalIgnoreCase)).Name, margin, font);

            CliEnsureParentDir(outPath);
            string temp = TempFilePolicy.NewPath("book");
            try
            {
                BookConversionResult result;
                if (kind == BookKind.Epub)
                    result = CliRunPumped(() => EpubToPdf.ConvertAsync(inPath, temp, layout, CancellationToken.None));
                else
                {
                    var tools = DjvuNativeBootstrap.Locate() ?? throw new BookConversionException(DjvuMissingMessage);
                    using var decoder = DjvuLibreDecoder.Open(tools, inPath, CancellationToken.None);
                    result = DjvuToPdf.Convert(decoder, temp, null,
                        new Progress<(int Page, int Total)>(p => { if (p.Page == 0 || p.Page == p.Total || p.Page % 20 == 0) con.WriteLine($"Page {p.Page}/{p.Total}"); }),
                        CancellationToken.None);
                }
                AtomicFileWriter.Write(p => File.Copy(temp, p, overwrite: true), outPath, CancellationToken.None);
                foreach (var n in result.Notices) con.WriteLine("Note: " + n);
                con.WriteLine($"Converted {result.Pages} page(s) -> {outPath}");
                return 0;
            }
            catch (BookConversionException ex)
            {
                con.WriteLine("Error: " + ex.Message);
                return 1;
            }
            finally { TempFilePolicy.TryDelete(temp); }
        }
    }
}
