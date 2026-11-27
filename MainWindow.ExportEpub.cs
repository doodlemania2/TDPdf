using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TDPdf.Diagnostics;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow
    {
        // ============================================================
        // Export as EPUB                                  (#247)
        // ============================================================
        //
        // File ▸ Export as EPUB… turns the open document into a reflowable EPUB 3 book. The analysis
        // and the package are EpubExporter / EpubExportPackage (pure, tested in tests/PdfCore); this
        // file only asks the questions, prepares the source and reports.
        //
        // The book is read from a temp save of the open document (the same source Export Pages as
        // Images uses), so unsaved edits and annotations are in it. Nothing here marks the document
        // dirty: the export writes a separate file.

        private async void ExportEpub_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            Telemetry.TrackEvent("File.ExportEpub");
            CommitActiveTextBox();

            // Marks that are drawn but not applied have removed nothing: the text under them would be in the book.
            if (_redactionMarks.Values.Any(list => list.Count > 0))
            {
                var go = TdpDialog.ShowYesNo(this,
                    "This document has redaction marks that have not been applied yet. The text under them is still in the file and would be in the book.\n\nExport anyway?",
                    "Export", "Cancel", "Export as EPUB", MessageBoxImage.Warning);
                if (go != MessageBoxResult.Yes) return;
            }

            if (ShowExportEpubDialog() is not { } choice) return;

            var dlg = new SaveFileDialog
            {
                Filter = "EPUB book|*.epub",
                Title = "Export as EPUB",
                FileName = SuggestBaseName() + ".epub",
                DefaultExt = ".epub",
                AddExtension = true,
                CheckPathExists = true,
                OverwritePrompt = true   // never replaces a book without asking
            };
            if (dlg.ShowDialog(this) != true) return;
            string outPath = dlg.FileName;

            var options = new EpubExportOptions
            {
                IncludePictures = choice.Pictures,
                DropHeadersAndFooters = choice.DropHeads,
                Split = choice.Split,
            };

            using var op = Telemetry.StartOperation("ExportEpub");
            var ct = BeginCancellableOp("Preparing the book...  (Esc to cancel)");
            string? source = null;
            try
            {
                await RunSaveWithRecoveryAsync(async () => source = await PrepareImageExportSourceAsync());
                string src = source ?? throw new InvalidOperationException("Could not prepare the document for export.");

                void Report(int page, int total) => SetWorkerStatus($"Exporting page {page} of {total}...  (Esc to cancel)");
                var result = await Task.Run(() => EpubExporter.Export(src, outPath, options, Report, ct), ct);

                SetStatus($"Exported {System.IO.Path.GetFileName(outPath)} ({result.Book.Chapters.Count} chapter(s), {result.PictureCount} picture(s))");
                var notes = new List<string>();
                if (result.ScannedPages > 0) notes.Add($"{result.ScannedPages} page(s) had no text and were added as pictures. Run OCR first to get readable text.");
                if (result.EmbeddedFonts.Count > 0) notes.Add($"Fonts from the PDF were included: {string.Join(", ", result.EmbeddedFonts)}.");
                notes.AddRange(result.SkippedFonts);
                notes.AddRange(result.Notes);
                if (result.ScannedPages > 0 || result.SkippedFonts.Count > 0 || result.Notes.Count > 0)
                    TdpDialog.Show(this, "The book was saved.\n\n" + string.Join("\n", notes.Take(12)), "Export as EPUB");
            }
            catch (OperationCanceledException)
            {
                SetStatus("EPUB export cancelled");
            }
            catch (Exception ex)
            {
                op.Fail(ex);
                Telemetry.TrackEvent("File.ExportFailed", new Dictionary<string, string>
                {
                    ["Operation"]     = "ExportEpub",
                    ["ExceptionType"] = ex.GetType().FullName ?? "Unknown",
                });
                EndCancellableOp();
                TdpDialog.Show(this, $"Export failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (source is not null && source != _currentFile) TDPdf.Services.TempFilePolicy.TryDelete(source);
                EndCancellableOp();
            }
        }

        private (bool Pictures, bool DropHeads, EpubChapterSplit Split)? ShowExportEpubDialog()
        {
            var bgPanel       = BrushResource("BgPanel");
            var borderDim     = BrushResource("BorderDim");
            var textPrimary   = BrushResource("TextPrimary");
            var textSecondary = BrushResource("TextSecondary");
            var accent        = BrushResource("AccentGreen");

            var (win, shellBody) = TdpDialog.CreateShell(this, "Export as EPUB", 400);
            win.ShowInTaskbar = false;
            win.Foreground = textPrimary;
            win.FontFamily = new FontFamily("Segoe UI");
            win.FontSize = 12;

            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock
            {
                Text = "Makes a reflowable book for phones and e-readers. Bookmarks become the table of contents.",
                Foreground = textSecondary, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
            });

            CheckBox Check(string text, bool on) => new()
            {
                Content = text, IsChecked = on, Foreground = textPrimary,
                Margin = new Thickness(0, 0, 0, 8), FontSize = 12
            };
            var pictures = Check("Include pictures", true);
            var heads = Check("Leave out repeated headers, footers and page numbers", true);
            root.Children.Add(pictures);
            root.Children.Add(heads);

            root.Children.Add(new TextBlock
            {
                Text = "Start a new chapter at", Foreground = textSecondary, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 6)
            });
            var split = new ComboBox { Style = (Style)FindResource("DarkComboBox"), Height = 28 };
            split.Items.Add("Each top-level bookmark");
            split.Items.Add("Each main heading");
            split.Items.Add("Nowhere (one chapter)");
            split.SelectedIndex = 0;
            root.Children.Add(split);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var cancelBtn = new Button
            {
                Content = "Cancel", Width = 96, Height = 30, Margin = new Thickness(0, 0, 8, 0),
                Background = bgPanel, Foreground = textPrimary, BorderBrush = borderDim, Cursor = Cursors.Hand, IsCancel = true
            };
            var okBtn = new Button
            {
                Content = "Export", Width = 96, Height = 30, Background = accent, Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold, BorderBrush = accent, Cursor = Cursors.Hand, IsDefault = true
            };
            buttons.Children.Add(cancelBtn);
            buttons.Children.Add(okBtn);
            root.Children.Add(buttons);
            shellBody.Children.Add(root);

            cancelBtn.Click += (_, _) => win.DialogResult = false;
            okBtn.Click += (_, _) => win.DialogResult = true;
            if (win.ShowDialog() != true) return null;
            return (pictures.IsChecked == true, heads.IsChecked == true,
                    split.SelectedIndex switch { 1 => EpubChapterSplit.Headings, 2 => EpubChapterSplit.None, _ => EpubChapterSplit.Bookmarks });
        }
    }
}
