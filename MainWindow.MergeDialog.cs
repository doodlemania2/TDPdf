// MainWindow — File > Merge Files (#242).
// A dialog to pick, order and trim files and folders; the result opens as a new unsaved tab, with the
// document that was open (if any) listed first. File > Merge... still appends to the open document.

using System.Windows;
using TDPdf.Diagnostics;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        private bool _mergeFilesRunning;

        private async void MergeFiles_Click(object sender, RoutedEventArgs e)
        {
            Telemetry.TrackEvent("File.MergeFiles");
            if (_mergeFilesRunning) return;
            _mergeFilesRunning = true;
            try { await MergeFilesAsync(); }
            catch (Exception ex)
            {
                Telemetry.TrackCrash(ex, "MergeFiles", recoverable: true);
                TdpDialog.Show(this, $"The files could not be merged:\n{ex.Message}",
                    "Merge Files", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { _mergeFilesRunning = false; }
        }

        private async Task MergeFilesAsync()
        {
            CommitActiveTextBox();

            // The open document goes first, as it is NOW (unsaved edits and all), through a snapshot
            // so the merge never reads a file that is still changing.
            string? snapshot = null;
            MergeSource? open = null;
            if (_doc is not null)
            {
                NormalizeLiveDocumentForTool();
                if (HasPendingFormValues) WriteFormValuesToDocument();
                snapshot = TempFilePolicy.NewPath("mergeopen");
                _doc.Save(snapshot);
                open = new MergeSource(1, MergeInputs.DisplayName(string.IsNullOrEmpty(_ctx.DisplayName) ? "Untitled" : _ctx.DisplayName),
                                       snapshot, _doc.PageCount, isOpenDocument: true, isTemporary: true);
            }

            MergePlan? plan = null;
            string? merged = null;
            try
            {
                plan = TdpDialog.ShowMergeFiles(this, open);
                if (plan is null) return;

                var sources = plan.Sources.ToList();
                var pages = plan.IncludedPages();
                merged = TempFilePolicy.NewPath("merged");
                PdfMerger.Report? report = null;
                string target = merged;

                bool finished = await TdpDialog.RunCancellable(this, "Merging files", (progress, ct) =>
                {
                    report = PdfMerger.Merge(sources, pages, target, progress, ct);
                    return Task.CompletedTask;
                });
                if (!finished || report is null)
                {
                    TempFilePolicy.TryDelete(merged);
                    merged = null;
                    SetStatus("Merge cancelled");
                    return;
                }

                await OpenInTabAsync(target);
                string status = $"Merged {sources.Count} file{(sources.Count == 1 ? "" : "s")} into {report.Pages} page{(report.Pages == 1 ? "" : "s")}";
                if (report.FormFieldsRenamed > 0)
                    status += $"; {report.FormFieldsRenamed} form field name{(report.FormFieldsRenamed == 1 ? "" : "s")} already in use " +
                              $"{(report.FormFieldsRenamed == 1 ? "was" : "were")} renamed";
                if (report.Notes.Count > 0) status += $". {report.Notes[0]}";
                FinalizeUnsavedTab(target, "Merged.pdf", status + ". Save to keep it");
                merged = null;   // the new tab owns it now
            }
            finally
            {
                if (snapshot is not null) TempFilePolicy.TryDelete(snapshot);
                if (plan is not null)
                    foreach (var s in plan.Sources)
                        if (s.IsTemporary) TempFilePolicy.TryDelete(s.WorkPath);
                if (merged is not null) TempFilePolicy.TryDelete(merged);
            }
        }
    }
}
