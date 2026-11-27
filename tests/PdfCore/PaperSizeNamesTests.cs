using TDPdf.Services;

/// <summary>Pins <see cref="PaperSizeNames"/>: the standard sizes, both orientations, and the +/-2 pt tolerance.</summary>
internal static class PaperSizeNamesTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("Paper size names (PaperSizeNames)");

        (string Name, double W, double H)[] cases =
        {
            ("Letter", 612, 792), ("Legal", 612, 1008), ("Tabloid", 792, 1224), ("Executive", 522, 756),
            ("A3", 842, 1191), ("A4", 595, 842), ("A5", 420, 595), ("B4", 709, 1001), ("B5", 499, 709),
            ("ARCH A", 648, 864), ("ARCH B", 864, 1296), ("ARCH C", 1296, 1728), ("ARCH D", 1728, 2592), ("ARCH E", 2592, 3456),
        };
        foreach (var (name, w, h) in cases)
        {
            Check($"{name} portrait", PaperSizeNames.Name(w, h) == name, $"{PaperSizeNames.Name(w, h)}");
            string expectLandscape = name == "Tabloid" ? "Ledger" : name;
            Check($"{name} landscape", PaperSizeNames.Name(h, w) == expectLandscape, $"{PaperSizeNames.Name(h, w)}");
        }

        Check("2 pt off still matches", PaperSizeNames.Name(614, 790) == "Letter", $"{PaperSizeNames.Name(614, 790)}");
        Check("3 pt off does not", PaperSizeNames.Name(615, 792) is null, $"{PaperSizeNames.Name(615, 792)}");
        Check("a custom size has no name", PaperSizeNames.Name(500, 500) is null, "");
        Check("zero, negative and NaN have no name",
              PaperSizeNames.Name(0, 792) is null && PaperSizeNames.Name(-612, 792) is null && PaperSizeNames.Name(double.NaN, 792) is null, "");
    }
}
