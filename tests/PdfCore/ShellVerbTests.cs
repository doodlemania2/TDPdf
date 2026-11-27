using TDPdf.Services;

/// <summary>Pins the command lines registered for the open, print and printto shell verbs.</summary>
internal static class ShellVerbTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        const string exe = @"C:\Users\A B\AppData\Local\Programs\TDPdf\TDPdf.exe";
        Check("shell verb: open", ShellVerbCommands.Open(exe) == "\"" + exe + "\" \"%1\"", ShellVerbCommands.Open(exe));
        Check("shell verb: print", ShellVerbCommands.Print(exe) == "\"" + exe + "\" --print \"%1\"", ShellVerbCommands.Print(exe));
        Check("shell verb: printto", ShellVerbCommands.PrintTo(exe) == "\"" + exe + "\" --print \"%1\" --printer \"%2\"", ShellVerbCommands.PrintTo(exe));

        // What the shell hands us once it has substituted a file and a printer containing spaces: the
        // command line must still split into exactly the arguments the --print command expects.
        string line = ShellVerbCommands.PrintTo(exe).Replace("%1", @"C:\Docs\My File.pdf").Replace("%2", "HP LaserJet 400");
        var args = SplitWindowsCommandLine(line);
        Check("shell verb: printto arguments survive spaces",
              args.Count == 5 && args[0] == exe && args[1] == "--print" && args[2] == @"C:\Docs\My File.pdf"
              && args[3] == "--printer" && args[4] == "HP LaserJet 400", string.Join("|", args));
    }

    // The simple CommandLineToArgvW rule that matters here: a quoted run is one argument.
    private static List<string> SplitWindowsCommandLine(string line)
    {
        var args = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuotes = false, have = false;
        foreach (char c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; have = true; }
            else if (c == ' ' && !inQuotes) { if (have) { args.Add(cur.ToString()); cur.Clear(); have = false; } }
            else { cur.Append(c); have = true; }
        }
        if (have) args.Add(cur.ToString());
        return args;
    }
}
