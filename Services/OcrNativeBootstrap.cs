using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

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

        private static readonly object _gate = new();
        private static bool _nativeReady;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

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

                // Belt and suspenders: add the native dir to the DLL search path and preload the libs.
                // leptonica must load before tesseract50, which depends on it.
                try
                {
                    SetDllDirectory(nativeDir);
                    foreach (string dll in Directory.GetFiles(nativeDir, "leptonica*.dll")) LoadLibrary(dll);
                    foreach (string dll in Directory.GetFiles(nativeDir, "tesseract*.dll")) LoadLibrary(dll);
                }
                catch { /* loader search paths above still apply */ }

                _nativeReady = true;
                return TessDataDir;
            }
        }

        private static void ExtractResource(Assembly asm, string resourceName, string targetPath)
        {
            using var src = asm.GetManifestResourceStream(resourceName);
            if (src == null) return;

            // A length match means the cached copy is already the current one - skip the rewrite. A version
            // change lands in a fresh cache dir, so this only ever no-ops within a single app version.
            if (File.Exists(targetPath) && new FileInfo(targetPath).Length == src.Length) return;

            string tmp = targetPath + ".tmp";
            using (var dst = File.Create(tmp))
                src.CopyTo(dst);
            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(tmp, targetPath);
        }
    }
}
