using TDPdf.Services;

/// <summary>Pins which host native folders are added to the process DLL search.</summary>
internal static class DllSearchPathsTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        string sep = Path.DirectorySeparatorChar.ToString();
        string root = Path.GetFullPath(Path.GetTempPath());
        string ext = Path.Combine(root, ".net", "TDPdf", "abc");
        string rt = Path.Combine(root, "runtime");
        string exe = Path.Combine(root, "Downloads");

        var all = DllSearchPaths.NativeDirectories($"{ext};{exe};{rt};{ext};relative\\dir;;{rt}{sep}", exe, _ => true);
        Check("dll search: keeps the extraction and runtime folders, once each, in order",
              all.Count == 2 && all[0] == ext && all[1] == rt, string.Join("|", all));
        Check("dll search: the exe's own folder is never added",
              !all.Contains(exe) && DllSearchPaths.NativeDirectories(exe + sep, exe, _ => true).Count == 0, "");
        Check("dll search: folders that do not exist are skipped",
              DllSearchPaths.NativeDirectories($"{ext};{rt}", exe, d => d == rt).Count == 1, "");
        Check("dll search: no list, no folders",
              DllSearchPaths.NativeDirectories(null, exe, _ => true).Count == 0 &&
              DllSearchPaths.NativeDirectories("  ", exe, _ => true).Count == 0, "");
    }
}
