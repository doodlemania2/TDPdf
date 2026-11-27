using System.IO;
using System.Net.Http;

namespace TDPdf.Services
{
    /// <summary>
    /// Fetches one Tesseract language model into place. The one downloader behind the OCR menu, the
    /// first-run prompt and the <c>--ocr</c> command line, which used to carry two copies of this
    /// (and the command line's copy forgot to record that a high-quality model had arrived).
    /// </summary>
    /// <remarks>
    /// The file is streamed to <c>&lt;dest&gt;.part</c> and only moved over <c>&lt;dest&gt;</c> after
    /// it hashes to the SHA-256 pinned in <see cref="OcrModelCatalog"/> for that language and tier. A
    /// file that fails the check, and any failure or cancel part-way, leaves the destination as it
    /// was and removes the <c>.part</c>.
    /// </remarks>
    internal static class OcrModelDownloader
    {
        /// <summary>A client with the connect timeout and user agent the model downloads use.</summary>
        internal static HttpClient CreateClient()
        {
            // Timeout covers connect + headers; the body is bounded by the cancellation token instead.
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TDPdf-OCR");
            return http;
        }

        /// <summary>
        /// Downloads <paramref name="code"/> in the given tier to <paramref name="destFile"/>.
        /// <paramref name="progress"/> gets (bytes so far, total bytes when the server said). Throws on
        /// cancel, a network error, an unknown language, or a failed integrity check.
        /// </summary>
        internal static Task DownloadAsync(HttpClient http, string code, bool highQuality, string destFile,
            Action<long, long?>? progress, CancellationToken ct) =>
            DownloadAsync(http, code, highQuality, destFile, progress, ct,
                (file, c, hq) => OcrModelCatalog.Verify(file, c, hq));

        /// <summary>The download with the integrity check supplied (the tests give it their own).</summary>
        internal static async Task DownloadAsync(HttpClient http, string code, bool highQuality, string destFile,
            Action<long, long?>? progress, CancellationToken ct, Func<string, string, bool, bool> verify)
        {
            string url = OcrModelCatalog.UrlFor(code, highQuality)
                ?? throw new InvalidOperationException($"No pinned OCR model for language '{code}'.");
            string part = destFile + ".part";
            try
            {
                using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    long? total = resp.Content.Headers.ContentLength;
                    using var netStream = await resp.Content.ReadAsStreamAsync(ct);
                    using var fileStream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                    var buffer = new byte[81920];
                    long read = 0;
                    int n;
                    while ((n = await netStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, n), ct);
                        read += n;
                        progress?.Invoke(read, total);
                    }
                }
                if (!verify(part, code, highQuality))
                    throw new InvalidDataException(
                        $"The downloaded {code} language data failed its integrity check (SHA-256 mismatch), so it was discarded.");
                // One atomic replace, so there is no moment with no model on disk (the old Delete then
                // Move left one, and lost the model altogether if the move failed).
                File.Move(part, destFile, overwrite: true);
            }
            catch
            {
                TempFilePolicy.TryDelete(part);
                throw;
            }
        }
    }
}
