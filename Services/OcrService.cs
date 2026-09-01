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

    /// <summary>
    /// How a crop should be segmented. Deliberately neutral: the callers in Ocr.cs describe the
    /// SHAPE of what they are reading, and the Tesseract page-segmentation modes stay contained in
    /// this file alongside every other reference to the engine.
    /// </summary>
    internal enum OcrLayout
    {
        /// <summary>Whatever the engine decides — the default for a whole page.</summary>
        Auto,
        /// <summary>One line of text, e.g. an ordinary single-line form field.</summary>
        SingleLine,
        /// <summary>A block of lines, e.g. a multiline form field.</summary>
        Block,
        /// <summary>Exactly one glyph, e.g. one cell of a comb field.</summary>
        SingleChar,
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

        /// <summary>
        /// Recognize a crop under the constraints a PDF form field declares (#242): a segmentation
        /// mode suited to the field's shape, and optionally the only characters it may contain.
        /// </summary>
        /// <remarks>
        /// The whitelist is an engine-level variable and STICKY, so it is always cleared afterwards
        /// — one numeric field must not silently constrain every field recognized after it through
        /// the same engine, and the batch deliberately reuses one engine for speed.
        /// </remarks>
        public OcrResult RecognizeBgra(byte[] bgra, int width, int height,
            OcrLayout layout, string? charWhitelist)
        {
            bool applied = !string.IsNullOrEmpty(charWhitelist);
            if (applied) _engine.SetVariable("tessedit_char_whitelist", charWhitelist);
            try
            {
                using var pix = Pix.LoadFromMemory(EncodePng(bgra, width, height));
                return Run(pix, ToPageSegMode(layout));
            }
            finally
            {
                if (applied) _engine.SetVariable("tessedit_char_whitelist", "");
            }
        }

        private static PageSegMode? ToPageSegMode(OcrLayout layout) => layout switch
        {
            OcrLayout.SingleLine => PageSegMode.SingleLine,
            OcrLayout.Block      => PageSegMode.SingleBlock,
            OcrLayout.SingleChar => PageSegMode.SingleChar,
            _                    => null,
        };

        private OcrResult Run(Pix pix, PageSegMode? mode = null)
        {
            using var page = mode is null ? _engine.Process(pix) : _engine.Process(pix, mode.Value);
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
