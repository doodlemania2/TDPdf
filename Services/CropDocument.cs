using System.IO;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace TDPdf.Services
{
    /// <summary>
    /// Writes a crop into a copy of a PDF: sets the CropBox of one page, or of every page. The WPF
    /// type-free core of <see cref="TDPdf.CropService"/>, so the harness can run it on real files.
    /// </summary>
    internal static class CropDocument
    {
        /// <summary>
        /// Crops <paramref name="pageIndex"/> to <paramref name="crop"/> (PDF user space, lower-left
        /// origin) in a copy beside the source and returns the copy's path. With
        /// <paramref name="applyToAllPages"/> every other page gets the same MARGINS from the edges
        /// of its own visible box as displayed (see <see cref="CropMargins"/>), so pages of another
        /// size, box origin or /Rotate are not cropped in the wrong place.
        /// </summary>
        internal static string Apply(string pdfDocPath, int pageIndex, CropMargins.UserRect crop, bool applyToAllPages)
        {
            if (string.IsNullOrWhiteSpace(pdfDocPath)) throw new ArgumentException("PDF path is required.", nameof(pdfDocPath));
            if (!File.Exists(pdfDocPath)) throw new FileNotFoundException("PDF file not found.", pdfDocPath);
            if (crop.Right - crop.Left <= 0 || crop.Top - crop.Bottom <= 0)
                throw new ArgumentException("Crop rectangle must have positive width and height.", nameof(crop));

            using var doc = PdfReader.Open(pdfDocPath, PdfDocumentOpenMode.Modify);
            if (pageIndex < 0 || pageIndex >= doc.PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex), "Page index is outside the document.");

            if (applyToAllPages)
            {
                // The page the crop was drawn on gets exactly what was drawn.
                var src = doc.Pages[pageIndex];
                var sb = PdfPageGeometry.VisibleBox(src);
                var margins = CropMargins.FromRect(sb.X, sb.Y, sb.Width, sb.Height, PdfPageGeometry.Rotation(src), crop);
                for (int i = 0; i < doc.PageCount; i++)
                {
                    if (i == pageIndex) { SetCropBox(doc.Pages[i], crop); continue; }
                    var page = doc.Pages[i];
                    var box = PdfPageGeometry.VisibleBox(page);
                    SetCropBox(page, CropMargins.ToRect(box.X, box.Y, box.Width, box.Height, PdfPageGeometry.Rotation(page), margins));
                }
            }
            else
            {
                SetCropBox(doc.Pages[pageIndex], crop);
            }

            string outputPath = CreateOutputPath(pdfDocPath);
            doc.Save(outputPath);
            return outputPath;
        }

        /// <summary>
        /// Sets the CropBox to the requested rectangle clamped to the page's MediaBox. Throws when
        /// less than a point survives, because a crop entirely off the page would leave nothing to see.
        /// </summary>
        private static void SetCropBox(PdfPage page, CropMargins.UserRect requested)
        {
            var media = page.MediaBox;
            double minX = Math.Min(media.X1, media.X2);
            double maxX = Math.Max(media.X1, media.X2);
            double minY = Math.Min(media.Y1, media.Y2);
            double maxY = Math.Max(media.Y1, media.Y2);

            double left = Math.Max(minX, Math.Min(maxX, requested.Left));
            double right = Math.Max(minX, Math.Min(maxX, requested.Right));
            double bottom = Math.Max(minY, Math.Min(maxY, requested.Bottom));
            double top = Math.Max(minY, Math.Min(maxY, requested.Top));

            if (right - left < 1 || top - bottom < 1)
                throw new InvalidOperationException("Crop rectangle is outside the page bounds.");

            page.CropBox = new PdfRectangle(new XPoint(left, bottom), new XPoint(right, top));
        }

        private static string CreateOutputPath(string sourcePath)
        {
            string? directory = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrEmpty(directory)) directory = AppDomain.CurrentDomain.BaseDirectory;
            string name = Path.GetFileNameWithoutExtension(sourcePath);
            return Path.Combine(directory, $"{name}.crop-{Guid.NewGuid():N}.pdf");
        }
    }
}
