using System.IO;
using TDPdf.Services;

/// <summary>Pins the pure parts of the file dialogs (#238): purpose GUIDs, filters, recent folders, path checks.</summary>
internal static class FileDialogTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        // The GUID Windows keys a purpose's remembered folder on must never change.
        Check("file dialogs: Open GUID is pinned",
              FileDialogPolicy.GuidFor(FileDialogPurpose.Open) == new Guid("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a1001"), "");
        Check("file dialogs: SaveAs GUID is pinned",
              FileDialogPolicy.GuidFor(FileDialogPurpose.SaveAs) == new Guid("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a1006"), "");
        var all = Enum.GetValues<FileDialogPurpose>();
        var guids = new HashSet<Guid>();
        bool allMapped = true;
        foreach (var p in all) { try { guids.Add(FileDialogPolicy.GuidFor(p)); } catch { allMapped = false; } }
        Check("file dialogs: every purpose has a GUID", allMapped, "");
        Check("file dialogs: GUIDs are all different", guids.Count == all.Length, $"{guids.Count}/{all.Length}");

        // Filters.
        var parsed = FileDialogPolicy.ParseFilter("PDF files|*.pdf|All files|*.*");
        Check("file dialogs: filter parses into pairs",
              parsed.Count == 2 && parsed[0] == ("PDF files", "*.pdf") && parsed[1] == ("All files", "*.*"), $"{parsed.Count}");
        Check("file dialogs: filter drops a dangling name", FileDialogPolicy.ParseFilter("PDF files|*.pdf|Stray").Count == 1, "");
        Check("file dialogs: empty filter is empty", FileDialogPolicy.ParseFilter(null).Count == 0, "");
        Check("file dialogs: filter round-trips",
              FileDialogPolicy.BuildFilter(parsed) == "PDF files|*.pdf|All files|*.*", "");
        Check("file dialogs: Open filter is PDF-only today", FileDialogFilters.OpenDocuments == "PDF files|*.pdf", FileDialogFilters.OpenDocuments);
        string withEpub = FileDialogFilters.BuildOpenFilter(new[] { ("EPUB books", "*.epub") });
        Check("file dialogs: extra types lead with All supported",
              withEpub == "All supported documents|*.pdf;*.epub|PDF files|*.pdf|EPUB books|*.epub", withEpub);

        // Recent folders (platform-neutral temp folders).
        string root = Path.Combine(Path.GetTempPath(), "tdpdf-fd-test");
        string[] f = Enumerable.Range(1, 10).Select(i => Path.Combine(root, "f" + i)).ToArray();
        var list = new List<string>();
        foreach (var x in f.Take(3)) list = FileDialogPolicy.AddRecent(list, x);
        Check("file dialogs: newest folder first", list.Count == 3 && list[0] == f[2] && list[2] == f[0], string.Join(",", list));
        list = FileDialogPolicy.AddRecent(list, f[0].ToUpperInvariant() == f[0] ? f[0] : f[0]);
        Check("file dialogs: reused folder moves to the front, no duplicate", list.Count == 3 && list[0] == f[0], string.Join(",", list));
        list = FileDialogPolicy.AddRecent(list, f[1] + Path.DirectorySeparatorChar);
        Check("file dialogs: trailing separator is the same folder", list.Count == 3 && list[0] == f[1], string.Join(",", list));
        var many = new List<string>();
        foreach (var x in f) many = FileDialogPolicy.AddRecent(many, x);
        Check("file dialogs: list is capped", many.Count == FileDialogPolicy.RecentFoldersKept && many[0] == f[9], $"{many.Count}");
        Check("file dialogs: null folder changes nothing", FileDialogPolicy.AddRecent(list, null).SequenceEqual(list), "");
        var round = FileDialogPolicy.ParseRecent(FileDialogPolicy.SerializeRecent(many));
        Check("file dialogs: setting value round-trips", round.SequenceEqual(many), "");

        var places = FileDialogPolicy.PlacesFor(f[5], many);
        Check("file dialogs: document folder leads the places", places[0] == f[5], string.Join(",", places));
        Check("file dialogs: places hold each folder once", places.Distinct(StringComparer.OrdinalIgnoreCase).Count() == places.Count, "");
        Check("file dialogs: places are capped", places.Count == FileDialogPolicy.PlacesShown, $"{places.Count}");
        Check("file dialogs: folder of a file", FileDialogPolicy.FolderOf(Path.Combine(f[0], "a.pdf"), false) == f[0], "");
        Check("file dialogs: folder of a folder", FileDialogPolicy.FolderOf(f[0], true) == f[0], "");

        // Path checks (written for Windows paths, evaluated the same on every OS).
        Check("path check: full path is fine", FilePathPolicy.CheckDialogPath(@"C:\Docs\a.pdf") is null, "");
        Check("path check: UNC path is fine", FilePathPolicy.CheckDialogPath(@"\\server\share\a.pdf") is null, "");
        Check("path check: relative path refused", FilePathPolicy.CheckDialogPath(@"docs\a.pdf") is not null, "");
        Check("path check: bare name refused", FilePathPolicy.CheckDialogPath("a.pdf") is not null, "");
        Check("path check: empty refused", FilePathPolicy.CheckDialogPath("  ") is not null, "");
        Check("path check: wildcard refused", FilePathPolicy.CheckDialogPath(@"C:\Docs\*.pdf") is not null, "");
        Check("path check: reserved name refused", FilePathPolicy.CheckDialogPath(@"C:\Docs\NUL.pdf") is not null, "");
        Check("path check: COM1 refused", FilePathPolicy.CheckDialogPath(@"C:\com1") is not null, "");
        Check("path check: COM10 is an ordinary name", FilePathPolicy.CheckDialogPath(@"C:\Docs\com10.pdf") is null, "");
        Check("path check: stray colon refused", FilePathPolicy.CheckDialogPath(@"C:\Docs\a:b.pdf") is not null, "");
        Check("path check: extended prefix is fine", FilePathPolicy.CheckDialogPath(@"\\?\C:\Docs\a.pdf") is null, "");
    }
}
