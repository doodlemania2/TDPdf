using TDPdf.Services;

/// <summary>
/// Pins <see cref="FileFingerprint"/>: the save-in-place check for a file another program changed
/// or removed since TDPdf opened it.
/// </summary>
internal static class OutsideChange
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("── file changed on disk since it was opened ──────────────────");

        string path = Path.Combine(tmp, "outside-change.pdf");
        File.WriteAllText(path, "%PDF-1.7 original content");
        var opened = FileFingerprint.TryCapture(path);
        Check("a readable file is fingerprinted", opened is not null, "");

        Check("untouched file reads as unchanged",
              FileFingerprint.Compare(opened, path) == FileChangeState.Unchanged, "");

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));
        Check("a timestamp-only touch is not a change",
              FileFingerprint.Compare(opened, path) == FileChangeState.Unchanged, "");

        // Same length, same timestamp, different bytes: the case size+mtime cannot see.
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "%PDF-1.7 ORIGINAL content");
        File.SetLastWriteTimeUtc(path, stamp);
        Check("same size and timestamp but different content is a change",
              FileFingerprint.Compare(opened, path) == FileChangeState.Changed, "");

        File.WriteAllText(path, "%PDF-1.7 a longer rewrite of the content");
        Check("a different length is a change", FileFingerprint.Compare(opened, path) == FileChangeState.Changed, "");

        File.Delete(path);
        Check("a deleted file reads as missing", FileFingerprint.Compare(opened, path) == FileChangeState.Missing, "");

        Check("no baseline gives no warning", FileFingerprint.Compare(null, path) == FileChangeState.Unknown, "");
        Check("an unreadable path fingerprints as null", FileFingerprint.TryCapture(path) is null, "");
    }
}
