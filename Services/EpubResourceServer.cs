using System.IO;
using System.Text;

namespace TDPdf.Services
{
    /// <summary>
    /// Answers every request the laid-out book makes, from the zip and from nowhere else. The page lives at
    /// a made-up host name, so the only things it can ever fetch are its own parts; the WebView2 handler
    /// answers 403 to every other host before this class is asked.
    /// </summary>
    internal sealed class EpubResourceServer
    {
        /// <summary>A name that cannot resolve (the .invalid top-level domain is reserved for that).</summary>
        internal const string Host = "tdpdf-book.invalid";
        internal const string BookPath = "/book.xhtml";
        internal const string ResourcePrefix = "/z/";

        /// <summary>The page may use its own pictures, fonts and style, and may not run or load anything else.</summary>
        internal const string ContentSecurityPolicy =
            "default-src 'none'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; font-src 'self' data:; base-uri 'none'; form-action 'none'";

        internal sealed record Response(int Status, string ContentType, byte[] Body);

        private readonly IEpubArchive _archive;
        private readonly EpubPackage _package;
        private readonly byte[] _book;
        private readonly object _gate = new();
        private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

        internal EpubResourceServer(IEpubArchive archive, EpubPackage package, EpubBook book)
        {
            _archive = archive;
            _package = package;
            _book = Encoding.UTF8.GetBytes(book.Html);
        }

        /// <summary>Files the page asked for that could not be served (missing, DRM, too big), for the notice.</summary>
        internal IReadOnlyCollection<string> Failed { get { lock (_gate) return _failed.ToArray(); } }

        /// <summary>The response for an absolute path such as <c>/z/OEBPS/images/a%20b.png</c>.</summary>
        internal Response Get(string absolutePath)
        {
            lock (_gate)
            {
                if (absolutePath == BookPath) return new Response(200, "application/xhtml+xml; charset=utf-8", _book);
                if (!absolutePath.StartsWith(ResourcePrefix, StringComparison.Ordinal)) return NotFound();

                string zipPath;
                try { zipPath = Uri.UnescapeDataString(absolutePath[ResourcePrefix.Length..]); }
                catch (UriFormatException) { return NotFound(); }
                if (zipPath.Length == 0 || zipPath.Contains("..", StringComparison.Ordinal)) return NotFound();

                if (_package.IsEncrypted(zipPath)) { _failed.Add(zipPath); return NotFound(); }

                byte[]? data;
                try { data = _archive.Read(zipPath); }
                catch (InvalidDataException) { _failed.Add(zipPath); return NotFound(); }
                if (data is null) { _failed.Add(zipPath); return NotFound(); }

                if (_package.ObfuscationAlgorithm(zipPath) is { } alg)
                {
                    byte[]? clear = EpubFontDeobfuscation.Deobfuscate(alg, _package.UniqueIdentifier, data);
                    if (clear is null) { _failed.Add(zipPath); return NotFound(); }   // a font the identifier cannot unlock: the page falls back to another
                    data = clear;
                }
                return new Response(200, ContentTypeFor(zipPath), data);
            }
        }

        private static Response NotFound() => new(404, "text/plain", Array.Empty<byte>());

        internal static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".css" => "text/css; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" or ".jpe" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".otf" => "font/otf",
            ".ttf" => "font/ttf",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };
    }
}
