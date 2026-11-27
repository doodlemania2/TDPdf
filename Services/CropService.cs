using System.Windows;
using TDPdf.Services;

namespace TDPdf
{
    public static class CropService
    {
        public static string Apply(string pdfDocPath, int pageIndex, Rect cropRectInPdfCoords) =>
            Apply(pdfDocPath, pageIndex, cropRectInPdfCoords, applyToAllPages: false);

        /// <summary>
        /// Crops a copy of the PDF; the work is in <see cref="CropDocument"/>, which does not depend on WPF.
        /// </summary>
        public static string Apply(string pdfDocPath, int pageIndex, Rect cropRectInPdfCoords, bool applyToAllPages) =>
            CropDocument.Apply(pdfDocPath, pageIndex,
                new CropMargins.UserRect(cropRectInPdfCoords.X, cropRectInPdfCoords.Y,
                    cropRectInPdfCoords.X + cropRectInPdfCoords.Width, cropRectInPdfCoords.Y + cropRectInPdfCoords.Height),
                applyToAllPages);
    }
}
