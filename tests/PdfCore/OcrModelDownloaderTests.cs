using System.Net;
using System.Net.Http;
using TDPdf.Services;

/// <summary>
/// Pins <see cref="OcrModelDownloader"/>: a model only replaces the installed one when it passes the
/// integrity check, and a failure or cancel leaves the old model in place and no .part behind.
/// </summary>
internal static class OcrModelDownloaderTests
{
    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastUrl;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri?.ToString();
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        string dir = Path.Combine(tmp, "ocrdl");
        Directory.CreateDirectory(dir);
        string dest = Path.Combine(dir, "eng.traineddata");
        byte[] model = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();

        // Success: verified, moved over the old file in one replace, progress reported, no .part left.
        File.WriteAllBytes(dest, [9, 9, 9]);
        var fake = new Fake(_ => Ok(model));
        long lastRead = 0; long? lastTotal = null;
        using (var http = new HttpClient(fake))
            OcrModelDownloader.DownloadAsync(http, "eng", true, dest, (r, t) => { lastRead = r; lastTotal = t; },
                CancellationToken.None, (f, c, hq) => c == "eng" && hq && new FileInfo(f).Length == model.Length)
                .GetAwaiter().GetResult();
        Check("ocr download: the model replaces the old file", File.ReadAllBytes(dest).SequenceEqual(model), "");
        Check("ocr download: no .part is left behind", !File.Exists(dest + ".part"), "");
        Check("ocr download: progress ends at the full size", lastRead == model.Length && lastTotal == model.Length, $"{lastRead} {lastTotal}");
        Check("ocr download: asks the pinned high-quality URL",
              fake.LastUrl == OcrModelCatalog.UrlFor("eng", true), fake.LastUrl ?? "");

        // Failed integrity check: old model stays, nothing left, and the error says so.
        File.WriteAllBytes(dest, [1, 1]);
        string? err = null;
        try
        {
            using var http = new HttpClient(new Fake(_ => Ok(model)));
            OcrModelDownloader.DownloadAsync(http, "eng", false, dest, null, CancellationToken.None, (_, _, _) => false)
                .GetAwaiter().GetResult();
        }
        catch (InvalidDataException ex) { err = ex.Message; }
        Check("ocr download: a failed check throws", err is not null && err.Contains("integrity"), err ?? "no throw");
        Check("ocr download: a failed check keeps the old model", File.ReadAllBytes(dest).SequenceEqual(new byte[] { 1, 1 }), "");
        Check("ocr download: a failed check removes the .part", !File.Exists(dest + ".part"), "");

        // HTTP error: same guarantees.
        bool threw = false;
        try
        {
            using var http = new HttpClient(new Fake(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
            OcrModelDownloader.DownloadAsync(http, "eng", false, dest, null, CancellationToken.None, (_, _, _) => true)
                .GetAwaiter().GetResult();
        }
        catch (HttpRequestException) { threw = true; }
        Check("ocr download: a 404 throws and keeps the old model", threw && File.ReadAllBytes(dest).Length == 2 && !File.Exists(dest + ".part"), "");

        // Cancelled up front: no file is written.
        bool cancelled = false;
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            try
            {
                using var http = new HttpClient(new Fake(_ => Ok(model)));
                OcrModelDownloader.DownloadAsync(http, "eng", false, dest, null, cts.Token, (_, _, _) => true)
                    .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { cancelled = true; }
        }
        Check("ocr download: a cancel throws and leaves the old model", cancelled && File.ReadAllBytes(dest).Length == 2 && !File.Exists(dest + ".part"), "");

        // A language with no pinned model is refused before any request.
        var never = new Fake(_ => Ok(model));
        bool refused = false;
        try
        {
            using var http = new HttpClient(never);
            OcrModelDownloader.DownloadAsync(http, "xyz", false, Path.Combine(dir, "xyz.traineddata"), null, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (InvalidOperationException) { refused = true; }
        Check("ocr download: an unpinned language is refused without a request", refused && never.LastUrl is null, "");
    }
}
