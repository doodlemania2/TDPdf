using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
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
        // Export pages as images        (upstream KillerPDF v1.6.5, #132)
        // ============================================================
        //
        // File ▸ Export Pages as Images… renders the chosen pages to PNG or JPEG at a chosen
        // DPI and writes them next to the base name the user picks, as <base>-page-001.png.
        //
        // The rasterize/encode/name half is PageImageExporter — the same pipeline the CLI's
        // --to-image command runs, so the two can't drift. What the GUI adds is the state the
        // CLI cannot see: pending annotations, filled form fields and in-app page rotations are
        // burned into a temp render source first (PrepareImageExportSourceAsync), so what lands
        // in the images is what is on screen.
        //
        // Nothing here mutates the open document or its dirty flag — the export writes separate
        // files, and the burn is done against a throwaway copy that is swapped back out.

        private async void ExportImages_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            Telemetry.TrackEvent("File.ExportImages");
            CommitActiveTextBox();

            int pageCount = _doc.PageCount;
            if (ShowExportImagesDialog(pageCount) is not { } choice) return;

            // Same page-range syntax (and blank = all rule) as the print dialog.
            var pages = Services.PrintPreviewWindow.ParseRange(choice.Range, pageCount);
            if (pages.Count == 0) { TdpDialog.Show(this, "That page range doesn't select any pages."); return; }

            string format = choice.Jpeg ? PageImageExporter.JpgFormat : PageImageExporter.PngFormat;
            var dlg = new SaveFileDialog
            {
                Filter = choice.Jpeg ? "JPEG image|*.jpg" : "PNG image|*.png",
                Title = "Export Pages as Images",
                FileName = SuggestBaseName() + "." + format,
                CheckFileExists = false,
                CheckPathExists = true
            };
            if (dlg.ShowDialog(this) != true) return;

            // The picked file is only a folder plus a base name: each page becomes its own file.
            string outDir = System.IO.Path.GetDirectoryName(dlg.FileName) ?? string.Empty;
            string baseName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
            if (outDir.Length == 0 || baseName.Length == 0)
            {
                TdpDialog.Show(this, "Pick a folder and a base file name for the exported images.",
                    "TDPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            using var op = Telemetry.StartOperation("ExportImages");
            var ct = BeginCancellableOp($"Exporting page 1 of {pages.Count}...  (Esc to cancel)");
            string? renderSource = null;
            try
            {
                // #106 recovery: the fragile step is the PdfSharpCore save that produces the
                // render source, so it runs through the same retry wrapper the saves use.
                await RunSaveWithRecoveryAsync(async () => renderSource = await PrepareImageExportSourceAsync());
                string source = renderSource ?? throw new InvalidOperationException("Could not prepare the pages for export.");

                double dpi = choice.Dpi;
                void Report(int page, int total) =>
                    SetWorkerStatus($"Exporting page {page} of {total}...  (Esc to cancel)");

                int written = await Task.Run(() => PageImageExporter.Export(
                    source, outDir, baseName, pages, dpi, format, transparent: false, Report, ct));

                SetStatus(ct.IsCancellationRequested
                    ? $"Image export cancelled ({written} of {pages.Count} page(s) written)"
                    : $"Exported {written} page image(s) to {DescribeFolder(outDir)}");
            }
            catch (Exception ex)
            {
                op.Fail(ex);
                Telemetry.TrackEvent("File.ExportFailed", new Dictionary<string, string>
                {
                    ["Operation"]     = "ExportImages",
                    ["ExceptionType"] = ex.GetType().FullName ?? "Unknown",
                });
                EndCancellableOp();
                TdpDialog.Show(this, $"Export failed:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // The clean copy may have become the live working file (see
                // PrepareImageExportSourceAsync); only the render source is ours to delete.
                if (renderSource is not null && renderSource != _currentFile) TryDeleteFile(renderSource);
                EndCancellableOp();
            }
        }

        /// <summary>
        /// Writes a temp PDF that matches what the user sees: pending annotations and form values
        /// burned in, and in-app page rotations included (they live in <c>_doc.Pages[i].Rotate</c>,
        /// which the save writes out, so rendering from the temp honors them for free).
        /// Returns the path to render from.
        /// </summary>
        /// <remarks>
        /// Mirrors the Save Flattened source preparation, clean-copy dance included: the burned
        /// document is swapped back out for an unburned copy before we return, so the annotations
        /// stay editable and a later save can't bake them twice. Never touches the dirty flag —
        /// this is an export, not a save.
        /// </remarks>
        private async Task<string> PrepareImageExportSourceAsync()
        {
            var doc = _doc ?? throw new InvalidOperationException("No document is open.");
            NormalizeDocumentForSave(doc);   // strip dangling /Outlines, zero-size /CropBox, dead signatures

            bool hasPending = _annotations.Values.Any(list => list.Count > 0) || HasPendingFormValues;
            if (!hasPending)
            {
                var temp = MakeTempPdfPath("exportsrc");
                await _pdfDocumentService.SaveAsync(() => doc.Save(temp), CancellationToken.None);
                return temp;
            }

            var tempClean  = MakeTempPdfPath("clean");
            var tempBurned = MakeTempPdfPath("burned");
            await _pdfDocumentService.SaveAsync(() => doc.Save(tempClean), CancellationToken.None);
            DrawAnnotationsOnDocument();

            ExceptionDispatchInfo? saveError = null;
            try
            {
                await _pdfDocumentService.SaveAsync(() => doc.Save(tempBurned), CancellationToken.None);
            }
            catch (Exception ex)
            {
                saveError = ExceptionDispatchInfo.Capture(ex);
            }

            // Restore first, rethrow second: the in-memory document must never be left burned,
            // even when the burned save is what failed.
            await RestoreDocumentAsync(doc, tempClean, CancellationToken.None);
            saveError?.Throw();
            return tempBurned;
        }

        /// <summary>Folder name for the status line, falling back to the full path for a drive root.</summary>
        private static string DescribeFolder(string dir)
        {
            var name = System.IO.Path.GetFileName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? dir : name;
        }

        /// <summary>
        /// Themed export options prompt (format / DPI / page range), built in the same
        /// hand-rolled style as the Insert Blank Page and Document Info dialogs.
        /// Returns null when the user cancels.
        /// </summary>
        private (bool Jpeg, double Dpi, string Range)? ShowExportImagesDialog(int pageCount)
        {
            var bgDark        = BrushResource("BgDark");
            var bgPanel       = BrushResource("BgPanel");
            var borderDim     = BrushResource("BorderDim");
            var textPrimary   = BrushResource("TextPrimary");
            var textSecondary = BrushResource("TextSecondary");
            var accent        = BrushResource("AccentGreen");
            var danger        = BrushResource("DangerRed");

            var win = new Window
            {
                Title = "Export Pages as Images",
                Width = 400,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = bgDark,
                Foreground = textPrimary,
                ShowInTaskbar = false,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12
            };

            var root = new StackPanel { Margin = new Thickness(16) };

            TextBlock SectionLabel(string text, double topMargin) => new()
            {
                Text = text,
                Foreground = textSecondary,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, topMargin, 0, 6)
            };

            TextBlock Hint(string text) => new()
            {
                Text = text,
                Foreground = textSecondary,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };

            TextBox Field(string initial) => new()
            {
                Text = initial,
                Foreground = textPrimary,
                Background = bgPanel,
                BorderBrush = borderDim,
                BorderThickness = new Thickness(1),
                CaretBrush = accent,
                Padding = new Thickness(6, 4, 6, 4),
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            root.Children.Add(SectionLabel("Format", 0));
            var formatBox = new ComboBox { Style = (Style)FindResource("DarkComboBox"), Height = 28 };
            formatBox.Items.Add("PNG");
            formatBox.Items.Add("JPEG");
            formatBox.SelectedIndex = 0;
            root.Children.Add(formatBox);

            root.Children.Add(SectionLabel("Resolution", 14));
            var dpiBox = Field(PageImageExporter.DefaultDpi.ToString("0"));
            root.Children.Add(dpiBox);
            var dpiHint = Hint($"DPI, {PageImageExporter.MinDpi:0}-{PageImageExporter.MaxDpi:0}. " +
                               $"{PageImageExporter.DefaultDpi:0} is screen quality, 300 is print quality.");
            root.Children.Add(dpiHint);
            // A DPI on its own tells nobody how big the files will be; this is the same number the
            // export is about to hand PDFium. Filled in by UpdatePixelHint once the range box below
            // exists, since the pages the user picked are what decides it.
            var pixelHint = Hint(string.Empty);
            root.Children.Add(pixelHint);

            root.Children.Add(SectionLabel("Pages", 14));
            var rangeBox = Field(string.Empty);
            root.Children.Add(rangeBox);
            root.Children.Add(Hint("e.g. 1-3,5  (blank = all)"));

            var countHint = Hint(string.Empty);
            root.Children.Add(countHint);
            void UpdateCount()
            {
                int selected = Services.PrintPreviewWindow.ParseRange(rangeBox.Text, pageCount).Count;
                countHint.Text = $"{selected} of {pageCount} page(s) will be exported, one file per page.";
            }

            // Live output size for the chosen DPI. PageImageExporter renders through
            // PageDimensions(dpi / 72), so a page's pixels are simply its VISIBLE size in points
            // (CropBox- and /Rotate-aware, exactly what PDFium lays out) times dpi/72 — the same
            // arithmetic, kept here rather than guessed at.
            //
            // A range can span pages of different sizes, and one number for all of them would be a
            // lie for most: the first selected page is reported and the readout says outright that
            // the others differ, rather than silently showing page 1's size for a mixed document.
            void UpdatePixelHint()
            {
                var pages = Services.PrintPreviewWindow.ParseRange(rangeBox.Text, pageCount);
                if (_doc is null || pages.Count == 0 || !PageImageExporter.TryParseDpi(dpiBox.Text, out double previewDpi))
                {
                    pixelHint.Text = string.Empty;
                    return;
                }

                var first = pages[0];
                if (first < 0 || first >= _doc.PageCount) { pixelHint.Text = string.Empty; return; }
                var (wPt, hPt) = VisiblePageSize(_doc.Pages[first]);
                double scale = previewDpi / 72.0;
                int px = Math.Max(1, (int)Math.Round(wPt * scale));
                int py = Math.Max(1, (int)Math.Round(hPt * scale));

                bool mixed = pages.Any(i =>
                {
                    if (i < 0 || i >= _doc.PageCount) return false;
                    var (w, h) = VisiblePageSize(_doc.Pages[i]);
                    return Math.Abs(w - wPt) > 0.5 || Math.Abs(h - hPt) > 0.5;
                });

                pixelHint.Text = mixed
                    ? $"Output: {px} × {py} px for page {first + 1}; the selected pages are not all the same size."
                    : $"Output: {px} × {py} px per image.";
            }

            void UpdateRangeReadouts() { UpdateCount(); UpdatePixelHint(); }
            rangeBox.TextChanged += (_, _) => UpdateRangeReadouts();
            dpiBox.TextChanged += (_, _) => UpdatePixelHint();
            UpdateRangeReadouts();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 96, Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                Background = bgPanel,
                Foreground = textPrimary,
                BorderBrush = borderDim,
                Cursor = Cursors.Hand,
                IsCancel = true
            };
            var exportBtn = new Button
            {
                Content = "Export",
                Width = 96, Height = 30,
                Background = accent,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                BorderBrush = accent,
                Cursor = Cursors.Hand,
                IsDefault = true
            };
            buttons.Children.Add(cancelBtn);
            buttons.Children.Add(exportBtn);
            root.Children.Add(buttons);

            win.Content = new Border
            {
                Background = bgPanel,
                BorderBrush = borderDim,
                BorderThickness = new Thickness(1),
                Child = root
            };

            double dpi = PageImageExporter.DefaultDpi;
            cancelBtn.Click += (_, _) => win.DialogResult = false;
            exportBtn.Click += (_, _) =>
            {
                // The one accepted DPI window, shared with the CLI's --dpi.
                if (!PageImageExporter.TryParseDpi(dpiBox.Text, out dpi))
                {
                    dpiHint.Text = $"Enter a DPI between {PageImageExporter.MinDpi:0} and {PageImageExporter.MaxDpi:0}.";
                    dpiHint.Foreground = danger;
                    dpiBox.Focus();
                    dpiBox.SelectAll();
                    return;
                }
                win.DialogResult = true;
            };

            win.Loaded += (_, _) => dpiBox.Focus();
            if (win.ShowDialog() != true) return null;
            return (formatBox.SelectedIndex == 1, dpi, rangeBox.Text.Trim());
        }
    }
}
