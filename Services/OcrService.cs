using System.IO;
using System.Windows.Media.Imaging;
using Tesseract;

namespace TDPdf.Services
{
    /// <summary>A single recognized word with its confidence and pixel box (top-left origin, OCR image space).</summary>
    internal sealed class OcrWord
    {
        public string Text { get; set; } = "";
        public float Confidence { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
    }

    /// <summary>Result of recognizing one image/page: full text, mean confidence, and per-word boxes.</summary>
    internal sealed class OcrResult
    {
        public string Text { get; set; } = "";
        public float MeanConfidence { get; set; }
        public List<OcrWord> Words { get; } = new();
    }

    /// <summary>
    /// Local Tesseract OCR. The tessdata folder (with at least one *.traineddata) must exist; the native
    /// engine loads language data by path. A TesseractEngine is NOT thread-safe, so run OCR off the UI thread
    /// and create a fresh OcrService per operation (or serialize calls). Dispose when done.
    /// </summary>
    internal sealed class OcrService : IDisposable
    {
        private readonly TesseractEngine _engine;

        /// <param name="tessDataPath">Folder holding *.traineddata. Defaults to the self-extracted cache (OcrNativeBootstrap).</param>
        /// <param name="language">Tesseract language code(s), e.g. "eng" or "eng+spa".</param>
        public OcrService(string? tessDataPath = null, string language = "eng")
        {
            // EnsureReady() extracts the embedded natives and configures the native loader, so it must run
            // before the engine is constructed.
            string dataPath = tessDataPath ?? OcrNativeBootstrap.EnsureReady();
            _engine = new TesseractEngine(dataPath, language, EngineMode.Default);
        }

        /// <summary>OCR an image file on disk (PNG, TIFF, JPEG, BMP).</summary>
        public OcrResult RecognizeImageFile(string imagePath)
        {
            using var pix = Pix.LoadFromFile(imagePath);
            return Run(pix);
        }

        /// <summary>OCR an encoded image already in memory (e.g. PNG bytes).</summary>
        public OcrResult RecognizeImageBytes(byte[] encodedImage)
        {
            using var pix = Pix.LoadFromMemory(encodedImage);
            return Run(pix);
        }

        /// <summary>
        /// OCR a rendered page straight from the render pipeline (raw BGRA, 4 bytes/pixel).
        /// Encodes to PNG via WPF first so we avoid a System.Drawing dependency.
        /// </summary>
        public OcrResult RecognizeBgra(byte[] bgra, int width, int height)
            => RecognizeImageBytes(EncodePng(bgra, width, height));

        private OcrResult Run(Pix pix)
        {
            using var page = _engine.Process(pix);
            var res = new OcrResult
            {
                Text = page.GetText() ?? "",
                MeanConfidence = page.GetMeanConfidence(),
            };

            using var iter = page.GetIterator();
            iter.Begin();
            do
            {
                if (iter.TryGetBoundingBox(PageIteratorLevel.Word, out var r))
                {
                    string w = iter.GetText(PageIteratorLevel.Word) ?? "";
                    if (!string.IsNullOrWhiteSpace(w))
                    {
                        res.Words.Add(new OcrWord
                        {
                            Text = w,
                            Confidence = iter.GetConfidence(PageIteratorLevel.Word),
                            Left = r.X1,
                            Top = r.Y1,
                            Right = r.X2,
                            Bottom = r.Y2,
                        });
                    }
                }
            }
            while (iter.Next(PageIteratorLevel.Word));

            return res;
        }

        private static byte[] EncodePng(byte[] bgra, int width, int height)
        {
            var bmp = BitmapSource.Create(
                width, height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null,
                bgra, width * 4);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        public void Dispose() => _engine.Dispose();
    }
}
