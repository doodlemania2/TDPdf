using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core.Readers;

namespace TDPdf.Services
{
    /// <summary>
    /// The one "render a page with its annotations, fall back to the plain raster, wrap it in a frozen
    /// BGRA bitmap" sequence that the viewer, continuous view, print preview and the open/recovery paths
    /// each used to carry their own copy of. Callers keep deciding the flags (form fields, print mode),
    /// the render size and any display-only transform such as the dark-mode invert, which runs on the
    /// returned buffer before it is wrapped.
    /// </summary>
    internal static class PageRaster
    {
        /// <summary>
        /// Rasterizes one page at the size the already-open <paramref name="pageReader"/> reports, with the
        /// annotations the file carries (see <see cref="PdfiumInterop.RenderPageWithAnnotations"/>), falling
        /// back to Docnet's plain <c>GetImage()</c> when that render returns nothing. The fallback prints /
        /// shows WITHOUT annotations; it is a last resort, not a mode.
        /// </summary>
        /// <param name="pageReader">The page, already open on a Docnet reader for <paramref name="path"/>.</param>
        /// <param name="path">The file the reader was opened on (PDFium re-opens it for the annotation render).</param>
        /// <param name="pageIndex">Zero-based page.</param>
        /// <param name="width">Pixel width, from <c>pageReader.GetPageWidth()</c>.</param>
        /// <param name="height">Pixel height, from <c>pageReader.GetPageHeight()</c>.</param>
        /// <param name="includeFormFields">False only for the primary tile, which draws live field overlays.</param>
        /// <param name="forPrint">Render as for print, so each annotation's Print flag decides.</param>
        /// <returns>BGRA bytes, or null when neither render produced anything.</returns>
        public static byte[]? RenderBgra(
            IPageReader pageReader, string path, int pageIndex, int width, int height,
            bool includeFormFields = true, bool forPrint = false)
        {
            return PdfiumInterop.RenderPageWithAnnotations(
                       path, pageIndex, width, height, includeFormFields: includeFormFields, forPrint: forPrint)
                   ?? pageReader.GetImage();
        }

        /// <summary>
        /// Convenience for a caller that holds the document reader rather than a page reader: opens the
        /// page, reads its size, and renders it with <see cref="RenderBgra(IPageReader,string,int,int,int,bool,bool)"/>.
        /// </summary>
        public static byte[]? RenderBgra(
            IDocReader docReader, string path, int pageIndex, out int width, out int height,
            bool includeFormFields = true, bool forPrint = false)
        {
            using var pageReader = docReader.GetPageReader(pageIndex);
            width = pageReader.GetPageWidth();
            height = pageReader.GetPageHeight();
            return RenderBgra(pageReader, path, pageIndex, width, height, includeFormFields, forPrint);
        }

        /// <summary>True when <paramref name="bgra"/> is a usable <paramref name="width"/> x <paramref name="height"/> raster.</summary>
        public static bool IsUsable(byte[]? bgra, int width, int height) =>
            width > 0 && height > 0 && bgra is { Length: > 0 };

        /// <summary>
        /// Wraps BGRA bytes in a frozen <see cref="WriteableBitmap"/> at the given density, so it can be
        /// built off the UI thread and handed across. Returns null for an unusable raster.
        /// </summary>
        public static BitmapSource? ToFrozenBitmap(byte[]? bgra, int width, int height, double dpiX, double dpiY)
        {
            if (!IsUsable(bgra, width, height)) return null;
            var bitmap = new WriteableBitmap(width, height, dpiX, dpiY, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra!, width * 4, 0);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
