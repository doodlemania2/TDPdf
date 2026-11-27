using TDPdf.Services;

/// <summary>Pins <see cref="SystemTool"/>: tools are always started from an absolute system folder.</summary>
internal static class SystemToolTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        string p = SystemTool.Path("schtasks.exe", Path.Combine(Path.GetTempPath(), "sys32"));
        Check("system tool: lands in the given system folder",
              Path.IsPathRooted(p) && Path.GetFileName(p) == "schtasks.exe" && Path.GetFileName(Path.GetDirectoryName(p)) == "sys32", p);
        Check("system tool: windows folder variant",
              Path.GetFileName(SystemTool.WindowsPath("explorer.exe", Path.GetTempPath())) == "explorer.exe", "");

        foreach (string bad in new[] { "", "  ", @"..\evil.exe", "a/b.exe", @"C:\x.exe", "c:evil.exe" })
        {
            bool threw = false;
            try { SystemTool.Path(bad, Path.GetTempPath()); } catch (ArgumentException) { threw = true; }
            Check($"system tool: refuses '{bad}'", threw, "");
        }
        bool noDir = false;
        try { SystemTool.Path("x.exe", ""); } catch (InvalidOperationException) { noDir = true; }
        // An empty override falls through to Environment.SystemDirectory, which is empty off Windows.
        Check("system tool: never returns a relative path", noDir || Path.IsPathRooted(SystemTool.Path("x.exe", "")), "");
    }
}
