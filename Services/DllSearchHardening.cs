using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TDPdf.Services
{
    /// <summary>
    /// Narrows the whole process's DLL search before anything else runs. Only TDPdf's own
    /// DllImports name System32; the .NET runtime, WPF, the image codecs, PDFium and WebView2 look a
    /// library up with the default search, which tries the exe's own folder first. For a portable
    /// copy that is Downloads, so a <c>version.dll</c> or <c>d3dcompiler_47.dll</c> dropped beside the
    /// exe would be loaded into TDPdf.
    /// </summary>
    /// <remarks>
    /// <para><b>What is searched afterwards.</b> System32, plus the folders the .NET host lists in
    /// <c>NATIVE_DLL_SEARCH_DIRECTORIES</c> (added with AddDllDirectory) except the exe's own folder.
    /// In the published single-file exe that list is the folder the bundle's natives are extracted
    /// to: PDFium, WPF's PresentationNative / wpfgfx / D3DCompiler / vcruntime copies and the like are
    /// found there, as before. <c>LOAD_LIBRARY_SEARCH_APPLICATION_DIR</c> is deliberately NOT
    /// requested, because the application directory of a single-file exe is the exe's folder.</para>
    /// <para><b>What is unaffected.</b> Libraries loaded by full path keep working: the OCR engine
    /// (<c>OcrNativeBootstrap</c>, which passes DLL_LOAD_DIR | SYSTEM32 itself) and the WebView2
    /// loader (<c>WebView2Bootstrap</c>, which hands WebView2 an exact folder). The host runtime
    /// (hostfxr, hostpolicy, coreclr) is already loaded when this runs.</para>
    /// <para>Never fatal: if any call fails the default search simply stays as it was.</para>
    /// </remarks>
    internal static class DllSearchHardening
    {
        private const uint LOAD_LIBRARY_SEARCH_USER_DIRS = 0x00000400;
        private const uint LOAD_LIBRARY_SEARCH_SYSTEM32  = 0x00000800;

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDefaultDllDirectories(uint directoryFlags);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr AddDllDirectory(string newDirectory);

        // Runs when the module is first touched, i.e. before App's static constructor and Main.
        [ModuleInitializer]
        internal static void Apply()
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return;

                // Collect the host's native folders first, then restrict. The order matters not for
                // correctness (AddDllDirectory entries only count once USER_DIRS is on) but it keeps
                // the window in which the search is half-configured empty.
                string? exeDir = null;
                try { exeDir = Path.GetDirectoryName(Environment.ProcessPath); } catch { /* none */ }
                var dirs = DllSearchPaths.NativeDirectories(
                    AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string, exeDir, Directory.Exists);

                if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_USER_DIRS))
                    return;
                foreach (string d in dirs)
                {
                    try { AddDllDirectory(d); } catch { /* one bad folder must not drop the rest */ }
                }
            }
            catch
            {
                // Hardening only; the app must start either way.
            }
        }
    }
}
