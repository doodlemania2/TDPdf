using System.IO;

namespace TDPdf.Services
{
    /// <summary>
    /// Full paths for the Windows tools TDPdf starts. A bare name such as <c>schtasks.exe</c> started
    /// with UseShellExecute = false is looked up in the application's own folder first, and for a
    /// portable copy that is usually Downloads, where anything can be sitting beside the exe.
    /// </summary>
    internal static class SystemTool
    {
        /// <summary>
        /// <paramref name="name"/> inside the System32 folder (<c>cmd.exe</c>, <c>schtasks.exe</c>,
        /// <c>ping.exe</c>). <paramref name="systemDir"/> exists for tests.
        /// </summary>
        internal static string Path(string name, string? systemDir = null) =>
            Combine(systemDir ?? Environment.SystemDirectory, name);

        /// <summary>
        /// <paramref name="name"/> inside the Windows folder itself (<c>explorer.exe</c> lives there,
        /// not in System32). <paramref name="windowsDir"/> exists for tests.
        /// </summary>
        internal static string WindowsPath(string name, string? windowsDir = null) =>
            Combine(windowsDir ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows), name);

        private static string Combine(string dir, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
                throw new ArgumentException("A bare file name is required.", nameof(name));
            if (string.IsNullOrEmpty(dir))
                throw new InvalidOperationException("The Windows folder could not be determined.");
            return System.IO.Path.Combine(dir, name);
        }
    }
}
