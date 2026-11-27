using System.IO;
using System.Windows;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using TDPdf.Services;

namespace TDPdf
{
    public static class CropService
    {
        public static string Apply(string pdfDocPath, int pageIndex, Rect cropRectInPdfCoords) =>
            Apply(pdfDocPath, pageIndex, cropRectInPdfCoords, applyToAllPages: false);

        public static string Apply(string pdfDocPath, int pageIndex, Rect cropRectInPdfCoords, bool applyToAllPages)
        {
            if (string.IsNullOrWhiteSpace(pdfDocPath)) throw new ArgumentException("PDF path is required.", nameof(pdfDocPath));
            if (!File.Exists(pdfDocPath)) throw new FileNotFoundException("PDF file not found.", pdfDocPath);
            if (cropRectInPdfCoords.Width <= 0 || cropRectInPdfCoords.Height <= 0)
                throw new ArgumentException("Crop rectangle must have positive width and height.", nameof(cropRectInPdfCoords));

            using var doc = PdfReader.Open(pdfDocPath, PdfDocumentOpenMode.Modify);
            if (pageIndex < 0 || pageIndex >= doc.PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex), "Page index is outside the document.");

            if (applyToAllPages)
            {
                // The page the crop was drawn on gets exactly what was drawn. Every other page gets
                // the same margins from the edges of ITS OWN visible box, as displayed (see
                // CropMargins), so pages of another size, box origin or /Rotate are not cropped in
                // the wrong place.
                var src = doc.Pages[pageIndex];
                var sb = PdfPageGeometry.VisibleBox(src);
                var margins = CropMargins.FromRect(sb.X, sb.Y, sb.Width, sb.Height, PdfPageGeometry.Rotation(src),
                    new CropMargins.UserRect(cropRectInPdfCoords.X, cropRectInPdfCoords.Y,
                        cropRectInPdfCoords.X + cropRectInPdfCoords.Width, cropRectInPdfCoords.Y + cropRectInPdfCoords.Height));
                for (int i = 0; i < doc.PageCount; i++)
                {
                    if (i == pageIndex) { SetCropBox(doc.Pages[i], cropRectInPdfCoords); continue; }
                    var page = doc.Pages[i];
                    var box = PdfPageGeometry.VisibleBox(page);
                    var r = CropMargins.ToRect(box.X, box.Y, box.Width, box.Height, PdfPageGeometry.Rotation(page), margins);
                    SetCropBox(page, new Rect(r.Left, r.Bottom, r.Right - r.Left, r.Top - r.Bottom));
                }
            }
            else
            {
                SetCropBox(doc.Pages[pageIndex], cropRectInPdfCoords);
            }

            string outputPath = CreateOutputPath(pdfDocPath);
            doc.Save(outputPath);
            return outputPath;
        }

        private static void SetCropBox(PdfPage page, Rect requestedRect)
        {
            var media = page.MediaBox;
            double minX = Math.Min(media.X1, media.X2);
            double maxX = Math.Max(media.X1, media.X2);
            double minY = Math.Min(media.Y1, media.Y2);
            double maxY = Math.Max(media.Y1, media.Y2);

            double requestedLeft = requestedRect.X;
            double requestedRight = requestedRect.X + requestedRect.Width;
            double requestedBottom = requestedRect.Y;
            double requestedTop = requestedRect.Y + requestedRect.Height;

            double left = Math.Max(minX, Math.Min(maxX, requestedLeft));
            double right = Math.Max(minX, Math.Min(maxX, requestedRight));
            double bottom = Math.Max(minY, Math.Min(maxY, requestedBottom));
            double top = Math.Max(minY, Math.Min(maxY, requestedTop));

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
