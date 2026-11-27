// MainWindow — Rasterize Region (#233).
// Tools > Rasterize Region..., or right-click a box selection: the dragged rectangle becomes a picture
// at the chosen resolution, and the text and graphics under it come off the page. Not redaction.

using System.Windows;
using System.Windows.Controls;
using TDPdf.Diagnostics;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        // Armed by the menu item; the next box-drag with the Select tool is the region. Consumed in the
        // Select-tool mouse-up handler, the same way the OCR region arm is.
        private bool _rasterRegionMode;

        /// <summary>The box last dragged with the Select tool while it is still on screen, for the right-click command.</summary>
        private (int PageIndex, Rect Canvas)? _boxSelection;


        private void RasterizeRegion_Click(object sender, RoutedEventArgs e) => BeginRasterizeRegion();

        /// <summary>
        /// The command. With a box already on the page it works on that box; otherwise it waits for the
        /// next drag.
        /// </summary>
        private void BeginRasterizeRegion()
        {
            Telemetry.TrackEvent("Page.RasterizeRegion");
            if (_doc is null || _currentFile is null) { TdpDialog.Show(this, "Open a PDF first."); return; }

            if (_boxSelection is { } box && _selectRect is not null)
            {
                var (pageIndex, bounds) = box;
                _boxSelection = null;
                ClearTextSelection();
                _ = RasterizeRegionAsync(pageIndex, bounds);
                return;
            }

            SetTool(EditTool.Select);
            _rasterRegionMode = true;
            SetStatus("Drag a box over the area to turn into a picture");
        }

        private async Task RasterizeRegionAsync(int pageIndex, Rect canvasBounds)
        {
            if (_doc is null || _currentFile is null) return;
            if (pageIndex < 0 || pageIndex >= _doc.PageCount) return;
            if (canvasBounds.Width < 4 || canvasBounds.Height < 4) { SetStatus("That box is too small to rasterize"); return; }
            CommitActiveTextBox();

            var dims = EnsureRenderDims(pageIndex);
            if (dims.w <= 0 || dims.h <= 0)
            {
                TdpDialog.Show(this, "That page's size could not be read, so the box cannot be placed on it.",
                    "Rasterize Region", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var page = _doc.Pages[pageIndex];
            var box = PdfPageGeometry.VisibleBox(page);
            int rotation = PdfPageGeometry.Rotation(page);
            var userRect = PdfPageGeometry.CanvasRectToPdf(
                box, rotation, canvasBounds.X, canvasBounds.Y, canvasBounds.Width, canvasBounds.Height, dims.w, dims.h);

            // A page PDFium cannot rewrite without damaging other content (spot colour, gradients,
            // inline images, soft masks): the picture can still go over the area, but the text under
            // it cannot be taken out. Say so and let the user decide.
            bool removeContent = true;
            var hazard = PdfContentInspector.Inspect(page);
            if (hazard != ContentHazard.None || !PdfiumInterop.CanEditPageContent)
            {
                string why = hazard != ContentHazard.None
                    ? PdfContentInspector.Describe(hazard)
                    : PdfiumInterop.DescribeEditApi();
                var answer = TdpDialog.ShowYesNo(this,
                    $"This page cannot have its content taken out safely: {why}.\n\n" +
                    "TDPdf can still put the picture over the area, but the text and graphics under it " +
                    "will stay in the file, and can still be searched, selected and copied.",
                    "Add picture only", "Cancel", "Rasterize Region", MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
                removeContent = false;
            }

            int? dpi = TdpDialog.ShowRasterizeRegion(this, d => RegionRaster.Plan(box, rotation, userRect, d));
            if (dpi is null) return;

            using var op = Telemetry.StartOperation("RasterizeRegion");
            SetFileOperationBusy(true, "Rasterizing the region...");
            string source = NewTempPath("region_src");
            string result = NewTempPath("region_out");
            try
            {
                // The CURRENT document, structural edits and all, not the file on disk.
                NormalizeLiveDocumentForTool();
                await _pdfDocumentService.SaveAsync(() => _doc!.Save(source), CancellationToken.None);

                int chosen = dpi.Value;
                var outcome = await Task.Run(() => PdfRegionRasterizer.Apply(source, result,
                    new PdfRegionRasterizer.Request
                    {
                        PageIndex = pageIndex,
                        Rect = userRect,
                        Dpi = chosen,
                        RemoveContent = removeContent,
                    }));

                if (!outcome.Ok)
                {
                    TdpDialog.Show(this,
                        $"The region was not rasterized: {outcome.Error}\n\nYour document has not been changed.",
                        "Rasterize Region", MessageBoxButton.OK, MessageBoxImage.Warning);
                    SetStatus("Rasterize Region failed. The document was not changed");
                    return;
                }

                // One undo step: a snapshot of the document as it was, then the new file.
                PushDocUndo();
                AdoptEditedFile(result);

                var rm = outcome.Removal;
                var parts = new List<string>();
                if (rm is not null)
                {
                    int text = rm.TextRunsRemoved + rm.TextRunsRewritten;
                    if (text > 0) parts.Add($"{text} text run{(text == 1 ? "" : "s")}");
                    if (rm.ShapesRemoved > 0) parts.Add($"{rm.ShapesRemoved} graphic{(rm.ShapesRemoved == 1 ? "" : "s")}");
                    if (rm.ImagesRemoved > 0) parts.Add($"{rm.ImagesRemoved} picture{(rm.ImagesRemoved == 1 ? "" : "s")}");
                }
                string status = $"Rasterized the region at {chosen} dpi";
                status += parts.Count > 0 ? $"; took {string.Join(", ", parts)} off the page" : "";
                if (!removeContent) status += "; the content under it was left in place";
                if (rm is { LeftPartlyCovered: > 0 })
                    status += $". {rm.LeftPartlyCovered} item{(rm.LeftPartlyCovered == 1 ? "" : "s")} only partly under the box " +
                              $"{(rm.LeftPartlyCovered == 1 ? "was" : "were")} left, and can still be searched";
                SetStatus(status + ". Save to write it to disk.");
            }
            catch (Exception ex)
            {
                op.Fail(ex);
                Telemetry.TrackCrash(ex, "RasterizeRegion", recoverable: true);
                TdpDialog.Show(this, $"The region was not rasterized: {ex.Message}\n\nYour document has not been changed.",
                    "Rasterize Region", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Rasterize Region failed. The document was not changed");
            }
            finally
            {
                SetFileOperationBusy(false);
                TempFilePolicy.TryDelete(source);
                if (System.IO.File.Exists(result) && !string.Equals(result, _currentFile, StringComparison.OrdinalIgnoreCase))
                    TempFilePolicy.TryDelete(result);
            }
        }

    }
}
