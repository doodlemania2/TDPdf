using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TDPdf.Services
{
    /// <summary>
    /// Brings DjVuLibre's library into the DjVu worker process. A build carries the DLLs as embedded resources
    /// named <c>TDPdf.DjvuNative.&lt;file&gt;</c> (see TDPdf.csproj): <c>libdjvulibre-21.dll</c> and the four it
    /// depends on, all taken from MSYS2's UCRT64 packages (third_party/djvulibre/SOURCES.md). They are
    /// extracted on first use to a per-version folder the way <see cref="OcrNativeBootstrap"/> does it: a
    /// copy already there is kept only if its SHA-256 matches the embedded one, and each file is written to a
    /// unique temp name and moved into place. They are then loaded by their full path, by their exact names
    /// (never by a wildcard over the folder), with <c>LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | SEARCH_SYSTEM32</c>,
    /// so a DLL planted in the user's folders or beside the exe is never picked up. Only the worker process
    /// ever loads them; the TDPdf that has your tabs open never does.
    /// </summary>
    internal static class DjvuNativeBootstrap
    {
        private const string Prefix = "TDPdf.DjvuNative.";
        private const uint LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x00000100;
        private const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

        /// <summary>The library the worker calls into.</summary>
        internal const string MainLibrary = "libdjvulibre-21.dll";

        /// <summary>
        /// Everything the build embeds, dependencies first. This list and the files in
        /// third_party/djvulibre/win-x64 are the same set (the csproj embeds the folder and fails the build
        /// on an unpinned file); the test suite checks it against SHA256SUMS.
        /// </summary>
        internal static readonly string[] LibraryNames =
        {
            "libgcc_s_seh-1.dll",
            "libwinpthread-1.dll",
            "libstdc++-6.dll",
            "libjpeg-8.dll",
            MainLibrary,
        };

        private static readonly object Gate = new();
        private static IntPtr _main;

        /// <summary>True when this build carries DjVuLibre inside it.</summary>
        internal static bool IsBundled
        {
            get
            {
                var names = typeof(DjvuNativeBootstrap).Assembly.GetManifestResourceNames();
                return LibraryNames.All(n => names.Contains(Prefix + n, StringComparer.Ordinal));
            }
        }

        /// <summary>
        /// Extracts the libraries (if they are not already there, intact) and loads them. Returns the handle of
        /// <see cref="MainLibrary"/>. Throws <see cref="IOException"/>, <see cref="DllNotFoundException"/> or
        /// <see cref="Win32Exception"/>; the worker turns that into an error reply.
        /// </summary>
        internal static IntPtr LoadLibraries()
        {
            lock (Gate)
            {
                if (_main != IntPtr.Zero) return _main;
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DjVu support is part of TDPdf for Windows");
                var asm = typeof(DjvuNativeBootstrap).Assembly;
                string version = asm.GetName().Version?.ToString() ?? "0";
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TDPdf", "djvu", version);
                Directory.CreateDirectory(dir);
                foreach (string file in LibraryNames)
                    Extract(asm, Prefix + file, Path.Combine(dir, file));

                IntPtr main = IntPtr.Zero;
                foreach (string file in LibraryNames)
                {
                    IntPtr h = LoadLibraryEx(Path.Combine(dir, file), IntPtr.Zero, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
                    if (h == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "could not load " + file);
                    if (file == MainLibrary) main = h;
                }
                _main = main;
                return main;
            }
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32", EntryPoint = "LoadLibraryExW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        private static void Extract(Assembly asm, string resource, string target)
        {
            using var src = asm.GetManifestResourceStream(resource)
                ?? throw new FileNotFoundException("this build of TDPdf does not carry " + resource[Prefix.Length..]);
            if (File.Exists(target))
            {
                byte[] expected = SHA256.HashData(src);
                byte[] actual;
                using (var existing = File.OpenRead(target)) actual = SHA256.HashData(existing);
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
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }
    }
}
