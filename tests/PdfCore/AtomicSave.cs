using TDPdf.Services;

/// <summary>
/// Pins <see cref="AtomicFileWriter"/>: a save that cannot be moved over the user's file must never
/// delete the only complete copy of their document.
/// </summary>
/// <remarks>
/// The dangerous path is File.Replace refused (SMB shares, cloud-sync folders) followed by an
/// overwrite that stops part-way. The destination is then truncated, and the old code deleted the
/// staged file on the way out. Both seams are injected here because neither failure can be
/// provoked portably.
/// </remarks>
internal static class AtomicSave
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("── atomic save keeps the good copy ───────────────────────────");

        string dir = Path.Combine(tmp, "atomic-save");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        string dest = Path.Combine(dir, "report.pdf");
        byte[] full = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        void Save(string p) => File.WriteAllBytes(p, full);
        string[] Others() => Directory.GetFiles(dir).Where(f => f != dest).ToArray();

        // ── new file, then a normal replace: no clutter either way ──────────────────────────
        AtomicFileWriter.Write(Save, dest, CancellationToken.None);
        Check("new destination is written in full", File.ReadAllBytes(dest).SequenceEqual(full), "");
        File.WriteAllText(dest, "old");
        AtomicFileWriter.Write(Save, dest, CancellationToken.None,
            replace: (s, d) => { File.Copy(s, d, true); File.Delete(s); });
        Check("successful replace leaves no staged or backup file", Others().Length == 0,
              string.Join(", ", Others().Select(Path.GetFileName)));

        // ── replace refused, plain overwrite succeeds ───────────────────────────────────────
        File.WriteAllText(dest, "old");
        AtomicFileWriter.Write(Save, dest, CancellationToken.None,
            replace: (_, _) => throw new IOException("replace refused"));
        Check("refused replace falls back to an overwrite", File.ReadAllBytes(dest).SequenceEqual(full), "");
        Check("fallback overwrite cleans up its staged file", Others().Length == 0, "");

        // ── replace refused, overwrite truncates the destination and fails ──────────────────
        File.WriteAllText(dest, "old");
        SaveRecoveredException? caught = null;
        try
        {
            AtomicFileWriter.Write(Save, dest, CancellationToken.None,
                replace: (_, _) => throw new UnauthorizedAccessException("replace refused"),
                copy: (_, d) => { File.WriteAllBytes(d, full.Take(100).ToArray()); throw new IOException("disk full"); });
        }
        catch (SaveRecoveredException ex) { caught = ex; }
        Check("failed overwrite throws SaveRecoveredException", caught != null, "");
        if (caught != null)
        {
            Check("the complete copy survives", File.Exists(caught.RecoveredPath)
                  && File.ReadAllBytes(caught.RecoveredPath).SequenceEqual(full), caught.RecoveredPath);
            Check("it sits next to the destination under a findable name",
                  Path.GetDirectoryName(caught.RecoveredPath) == dir
                  && Path.GetFileName(caught.RecoveredPath).StartsWith("report (TDPdf recovered ")
                  && caught.RecoveredPath.EndsWith(".pdf"), Path.GetFileName(caught.RecoveredPath));
            Check("the message tells the user where it is", caught.Message.Contains(caught.RecoveredPath), "");
            Check("no anonymous staged file is left behind",
                  !Directory.GetFiles(dir, ".tdpdf_save_*").Any(), "");
        }

        // ── the serializer itself fails: nothing is kept, the destination is untouched ──────
        foreach (var f in Others()) File.Delete(f);
        File.WriteAllText(dest, "old");
        bool threw = false;
        try { AtomicFileWriter.Write(p => { File.WriteAllText(p, "half"); throw new InvalidOperationException("boom"); }, dest, CancellationToken.None); }
        catch (InvalidOperationException) { threw = true; }
        Check("a failing serializer leaves the destination untouched",
              threw && File.ReadAllText(dest) == "old" && Others().Length == 0, "");
    }
}
