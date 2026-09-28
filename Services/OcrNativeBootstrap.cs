using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TDPdf.Services
{
    /// <summary>
    /// Keeps the single-exe build self-sufficient for OCR. The native Tesseract DLLs (x64) are embedded as
    /// resources and self-extracted on first use, the same pattern the single-file host uses for the managed
    /// assemblies. Native libs go in a per-version cache (they must match the app); language data lives in a
    /// STABLE folder so user-downloaded packs survive app updates. No language data is bundled - English (and
    /// any other language) is downloaded on demand on the first OCR. Thread-safe; best-effort/guarded.
    /// </summary>
    internal static class OcrNativeBootstrap
    {
        private const string NativePrefix = "TDPdf.OcrNative.";
        // The exact names TDPdf.csproj embeds. Loading by these names, rather than whatever matches a
        // wildcard in the cache, is what keeps a DLL someone else dropped into that folder from loading.
        private const string LeptonicaFileName = "leptonica-1.82.0.dll";
        private const string TesseractFileName = "tesseract50.dll";
        private const uint LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x00000100;
        private const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

        private static readonly object _gate = new();
        private static bool _nativeReady;

        [DllImport("kernel32", EntryPoint = "LoadLibraryExW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        /// <summary>
        /// Version-independent tessdata folder. Downloaded language packs are written here, so they persist
        /// across app updates (unlike the native cache, which is keyed on the app version).
        /// </summary>
        public static string TessDataDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TDPdf", "tessdata");

        /// <summary>
        /// Ensures the stable tessdata folder exists and returns it. Light - does not touch the native
        /// libraries, so it is safe to call just to inspect or list installed languages (e.g. when building
        /// the language menu, or before downloading a pack).
        /// </summary>
        public static string EnsureTessDataDir()
        {
            Directory.CreateDirectory(TessDataDir);
            return TessDataDir;
        }

        /// <summary>
        /// Extracts the native libs to a per-version cache, configures Tesseract's native loader, ensures the
        /// tessdata folder exists, and returns it for OcrService. Call before constructing OcrService.
        /// </summary>
        public static string EnsureReady()
        {
            EnsureTessDataDir();
            if (_nativeReady) return TessDataDir;
            lock (_gate)
            {
                if (_nativeReady) return TessDataDir;

                var asm = typeof(OcrNativeBootstrap).Assembly;
                string version = asm.GetName().Version?.ToString() ?? "0";
                string baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TDPdf", "ocr", version);
                string nativeDir = Path.Combine(baseDir, "x64");
                Directory.CreateDirectory(nativeDir);

                foreach (string res in asm.GetManifestResourceNames())
                {
                    if (res.StartsWith(NativePrefix, StringComparison.Ordinal))
                    {
                        string file = res[NativePrefix.Length..];
                        // Tesseract's loader looks in the x64 subfolder; the flat copy covers any loader
                        // path that does not append the platform name.
                        ExtractResource(asm, res, Path.Combine(nativeDir, file));
                        ExtractResource(asm, res, Path.Combine(baseDir, file));
                    }
                }

                // Point Tesseract's native loader at the cache. Reflection avoids a compile-time bind in
                // case the loader type's visibility differs across package versions; the preload below is
                // the hard guarantee regardless.
                try
                {
                    var loaderType = Type.GetType("InteropDotNet.LibraryLoader, Tesseract");
                    object? instance = loaderType?
                        .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?
                        .GetValue(null);
                    loaderType?.GetProperty("CustomSearchPath")?.SetValue(instance, baseDir);
                }
                catch { /* fall through to the preload */ }

                // Belt and suspenders: preload the two bundled libs by exact path. leptonica must load
                // before tesseract50, which depends on it.
                //
                // This used to call SetDllDirectory(nativeDir) and load every leptonica*/tesseract* file it
                // found there. SetDllDirectory is process-wide and permanent, so from the first OCR onward
                // every unqualified DLL load in the process - pdfium's included - searched a folder under
                // %LOCALAPPDATA% that any process running as the user can write to. LoadLibraryEx with
                // SEARCH_DLL_LOAD_DIR | SEARCH_SYSTEM32 resolves each library's dependencies from its own
                // folder and System32 only, and changes nothing for anyone else. (Upstream KillerPDF 1.8.70.)
                try
                {
                    const uint flags = LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32;
                    LoadLibraryEx(Path.Combine(nativeDir, LeptonicaFileName), IntPtr.Zero, flags);
                    LoadLibraryEx(Path.Combine(nativeDir, TesseractFileName), IntPtr.Zero, flags);
                }
                catch { /* Tesseract's own loader, pointed at the cache above, still applies */ }

                _nativeReady = true;
                return TessDataDir;
            }
        }

        private static void ExtractResource(Assembly asm, string resourceName, string targetPath)
        {
            using var src = asm.GetManifestResourceStream(resourceName);
            if (src == null) return;

            // A hash match means the cached copy is already the current one - skip the rewrite. This was a
            // length check, which trusted any same-length file in a user-writable folder and then loaded it
            // as native code. Hashing two ~5 MB files once per launch that uses OCR is cheap by comparison.
            if (File.Exists(targetPath))
            {
                byte[] expected = SHA256.HashData(src);
                byte[] actual;
                using (var existing = File.OpenRead(targetPath))
                    actual = SHA256.HashData(existing);
                if (CryptographicOperations.FixedTimeEquals(expected, actual)) return;
                src.Position = 0;
            }

            // A unique temp name opened CreateNew, so nothing pre-placed at a predictable path is written
            // through, and one atomic replace so a reader never sees a half-written library.
            string tmp = $"{targetPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    src.CopyTo(dst);
                File.Move(tmp, targetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
    }
}
