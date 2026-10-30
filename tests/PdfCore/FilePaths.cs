using TDPdf.Services;

/// <summary>
/// Pins <see cref="FilePathPolicy"/>: Extract Page(s) refusing the open document as its target,
/// recent-file de-duplication, and the multi-window restore-on-launch merge.
/// </summary>
internal static class FilePaths
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("── file paths, recents and the shared session ────────────────");

        string dir = Path.Combine(tmp, "paths");
        string a = Path.Combine(dir, "a.pdf");
        string b = Path.Combine(dir, "b.pdf");
        string c = Path.Combine(dir, "c.pdf");
        string aUpper = Path.Combine(dir, "A.PDF");
        string aDotted = Path.Combine(dir, "sub", "..", "a.pdf");

        // ── same-file test used by Extract Page(s) ──────────────────────────────────────────
        Check("same path in a different case is the same file", FilePathPolicy.SamePath(a, aUpper), "");
        Check("a '..' segment does not hide the same file", FilePathPolicy.SamePath(aDotted, a), "");
        Check("different files differ", !FilePathPolicy.SamePath(a, b), "");
        Check("a null path never matches", !FilePathPolicy.SamePath(a, null) && !FilePathPolicy.SamePath(null, null), "");
        Check("an invalid path does not throw and does not match", !FilePathPolicy.SamePath("a\0b", a), "");

        // ── recents: normalise and de-dupe old entries ──────────────────────────────────────
        var rec = FilePathPolicy.NormalizeDistinct(new[] { a, aUpper, aDotted, "", null, "x\0y", b });
        Check("old duplicate recents collapse to one, order kept",
              rec.Count == 2 && rec[0] == a && rec[1] == b, string.Join(" | ", rec));

        // ── session merge ───────────────────────────────────────────────────────────────────
        // Window 1 restored {a, b}; window 2 opened c and closed first; window 1 then closes with
        // only a open.
        var afterW2 = FilePathPolicy.MergeSession(new[] { a, b }, Array.Empty<string>(), new[] { c });
        Check("a second window adds its tabs without dropping the first window's",
              afterW2.SequenceEqual(new[] { a, b, c }), string.Join(" | ", afterW2));
        var afterW1 = FilePathPolicy.MergeSession(afterW2, new[] { a, b }, new[] { aUpper });
        Check("a window replaces only its own earlier entries",
              afterW1.Count == 2 && afterW1[0] == c && FilePathPolicy.SamePath(afterW1[1], a), string.Join(" | ", afterW1));
        var empty = FilePathPolicy.MergeSession(new[] { a, c }, Array.Empty<string>(), Array.Empty<string>());
        Check("a window closing with nothing open leaves the others' entries alone",
              empty.SequenceEqual(new[] { a, c }), string.Join(" | ", empty));
        var cleared = FilePathPolicy.MergeSession(new[] { a, b }, new[] { b, a }, Array.Empty<string>());
        Check("the only window closing its last tab still empties the session", cleared.Count == 0, "");
    }
}
