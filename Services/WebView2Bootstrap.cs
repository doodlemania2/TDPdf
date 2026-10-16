using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Web.WebView2.Core;

namespace TDPdf.Services
{
    /// <summary>
    /// Makes WebView2 work from the single-file EXE, and answers "is the WebView2 Runtime here?".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The loader.</b> <c>Microsoft.Web.WebView2.Core.dll</c> needs the small native
    /// <c>WebView2Loader.dll</c> beside it. The package adds that file to a .NET project as loose
    /// <c>Content</c> (<c>runtimes\win-x64\native\WebView2Loader.dll</c>) rather than as a native
    /// runtime asset, so single-file publish would neither bundle-and-extract it the way
    /// <c>IncludeNativeLibrariesForSelfExtract</c> does for real native assets nor leave TDPdf a
    /// single file — and the managed wrapper, loaded from inside the bundle, has no directory of its
    /// own to look in. So it is handled exactly like the OCR natives (<see cref="OcrNativeBootstrap"/>):
    /// TDPdf.csproj embeds the x64 loader (and sets <c>WebView2NeverCopyLoaderDllToOutputDirectory</c>
    /// so no loose copy ships), this extracts it once per app version to
    /// <c>%LOCALAPPDATA%\TDPdf\webview2-loader\{version}</c> with a hash check, and
    /// <see cref="CoreWebView2Environment.SetLoaderDllFolderPath"/> points WebView2 at it. That call
    /// must precede every other WebView2 API, which is why every entry point comes through
    /// <see cref="EnsureLoader"/>.
    /// </para>
    /// <para>
    /// <b>The runtime.</b> The browser engine is the Evergreen WebView2 Runtime, shipped with
    /// Windows 11 and serviced by Windows Update on Windows 10; TDPdf does not bundle it.
    /// <see cref="RuntimeVersion"/> returns null when it is missing (or the loader could not be
    /// prepared), and callers fall back to the default browser with a status-bar note.
    /// </para>
    /// </remarks>
    internal static class WebView2Bootstrap
    {
        private const string LoaderResource = "TDPdf.WebView2.WebView2Loader.dll";
        private const string LoaderFileName = "WebView2Loader.dll";
        private static readonly object Gate = new();
        private static bool _attempted;
        private static bool _ready;

        /// <summary>
        /// Prepares the loader once. True when WebView2 can be called. Never throws. In a plain
        /// <c>dotnet build</c> (no embedded resource) WebView2's default probing is left alone.
        /// </summary>
        internal static bool EnsureLoader()
        {
            lock (Gate)
            {
                if (_attempted) return _ready;
                _attempted = true;
                try
                {
                    var asm = typeof(WebView2Bootstrap).Assembly;
                    using var src = asm.GetManifestResourceStream(LoaderResource);
                    if (src is null)
                    {
                        _ready = true;   // development build: the package's loose copy is beside the exe
                        return _ready;
                    }
                    string version = asm.GetName().Version?.ToString() ?? "0";
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TDPdf", "webview2-loader", version);
                    Directory.CreateDirectory(dir);
                    Extract(src, Path.Combine(dir, LoaderFileName));
                    CoreWebView2Environment.SetLoaderDllFolderPath(dir);
                    _ready = true;
                }
                catch
                {
                    _ready = false;
                }
                return _ready;
            }
        }

        /// <summary>The installed WebView2 Runtime's version, or null when there is none (or it cannot be reached).</summary>
        internal static string? RuntimeVersion()
        {
            if (!EnsureLoader()) return null;
            try
            {
                string? v = CoreWebView2Environment.GetAvailableBrowserVersionString();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
            catch
            {
                // WebView2RuntimeNotFoundException, a missing or wrong-architecture loader
                // (DllNotFoundException / BadImageFormatException), or a broken install: all mean
                // "use the browser instead".
                return null;
            }
        }

        // The OCR natives' extraction, for the same reasons: a hash match skips the rewrite (a
        // same-length check would trust any file in a user-writable folder), a unique temp name
        // opened CreateNew means nothing pre-placed is written through, and one atomic move means
        // a reader never sees half a library.
        private static void Extract(Stream src, string target)
        {
            if (File.Exists(target))
            {
                byte[] expected = SHA256.HashData(src);
                byte[] actual;
                using (var existing = File.OpenRead(target))
                    actual = SHA256.HashData(existing);
                if (CryptographicOperations.FixedTimeEquals(expected, actual)) return;
                src.Position = 0;
            }
            string tmp = $"{target}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    src.CopyTo(dst);
                File.Move(tmp, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
    }
}
