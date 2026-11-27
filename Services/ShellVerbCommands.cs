namespace TDPdf.Services
{
    /// <summary>
    /// The command lines written under a file type's <c>shell\open</c>, <c>shell\print</c> and
    /// <c>shell\printto</c> registry keys. Each part the shell fills in (the file, the printer) is
    /// quoted, so a path or a printer name with spaces reaches us as one argument; the executable
    /// path is quoted too.
    /// </summary>
    internal static class ShellVerbCommands
    {
        /// <summary><c>"exe" "%1"</c>: open the file.</summary>
        public static string Open(string exePath) => $"\"{exePath}\" \"%1\"";

        /// <summary><c>"exe" --print "%1"</c>: Print in Explorer's context menu, to the default printer.</summary>
        public static string Print(string exePath) => $"\"{exePath}\" --print \"%1\"";

        /// <summary>
        /// <c>"exe" --print "%1" --printer "%2"</c>: what the shell runs to print to a named printer
        /// (drag a file onto a printer, or an application calling ShellExecute with "printto").
        /// </summary>
        public static string PrintTo(string exePath) => $"\"{exePath}\" --print \"%1\" --printer \"%2\"";
    }
}
