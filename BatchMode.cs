using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace TDPdf
{
    // ============================================================
    // Headless CLI batch mode        (upstream KillerPDF v1.6.4)
    // ============================================================
    //
    // TDPdf.exe --batch-resave <input.pdf|inputDir> <output.pdf|outputDir> [--log <file.csv>] [--quiet]
    //
    // Resaves one PDF (or every *.pdf under a folder tree, mirroring the
    // relative structure into the output folder) through the same pre-save
    // pipeline a GUI save uses: PdfReader.Open(Modify), NormalizeDocumentForSave
    // (the /Outlines, /CropBox, and dead-signature scrubs), StripLinkAnnotationBorders,
    // Save. No window, no dialogs, no repair fallbacks, no encryption stripping —
    // files that cannot go through the plain Modify pipeline are reported as SKIP
    // with a reason instead of silently faking a result.
    //
    // Built for a veraPDF-style validation harness: baseline a corpus,
    // --batch-resave it, then validate the output tree. The claim being tested
    // is "a TDPdf save does not degrade standards conformance".
    //
    // Exit codes: 0 = every file OK or SKIP, 1 = at least one FAIL
    // (file opened but the save failed), 2 = bad usage or bad paths.
    //
    // Invoked from App.OnStartup BEFORE the single-instance mutex, so a batch
    // run works even while a GUI instance is open and never forwards to it.
    //
    // TDPdf builds as a GUI-subsystem exe, so it has no console of its own;
    // AttachConsole(-1) latches onto the parent terminal when launched from one.
    // Output interleaves with the prompt (standard GUI-app quirk) — the
    // authoritative record is the --log CSV and the exit code.
    public partial class MainWindow
    {
        [DllImport("kernel32.dll", EntryPoint = "AttachConsole", SetLastError = true)]
        private static extern bool BatchAttachConsole(int dwProcessId);
        private const int BatchAttachParentProcess = -1;

        /// <summary>
        /// Entry point for CLI batch mode. Returns false when args do not
        /// request it (normal GUI launch); otherwise runs the whole batch and
        /// returns true with the process exit code in <paramref name="exitCode"/>.
        /// </summary>
        internal static bool TryRunBatch(string[] args, out int exitCode)
        {
            exitCode = 0;
            int flagIdx = Array.FindIndex(args,
                a => string.Equals(a, "--batch-resave", StringComparison.OrdinalIgnoreCase));
            if (flagIdx < 0) return false;

            var con = OpenBatchConsole();

            // Positional args after the flag: input, output. Options anywhere after the flag.
            string? input = null, output = null, logPath = null;
            bool quiet = false, badUsage = false;
            for (int i = flagIdx + 1; i < args.Length; i++)
            {
                var a = args[i];
                if (string.Equals(a, "--log", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) logPath = args[++i];
                    else badUsage = true;
                }
                else if (string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase))
                {
                    quiet = true;
                }
                else if (input is null)  input  = a;
                else if (output is null) output = a;
                else badUsage = true;   // extra positional arg
            }

            if (badUsage || string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
            {
                con.WriteLine("Usage: TDPdf.exe --batch-resave <input.pdf|inputDir> <output.pdf|outputDir> [--log <file.csv>] [--quiet]");
                exitCode = 2;
                return true;
            }

            try
            {
                exitCode = RunBatchResave(input!, output!, logPath, quiet, con);
            }
            catch (Exception ex)
            {
                con.WriteLine("Batch mode failed: " + FlattenBatchDetail(ex.Message));
                exitCode = 2;
            }
            return true;
        }

        private static int RunBatchResave(string input, string output, string? logPath, bool quiet, TextWriter con)
        {
            // Build the work list: (relative path, source, destination).
            var work = new List<(string Rel, string Src, string Dst)>();

            if (File.Exists(input))
            {
                string dst = Directory.Exists(output)
                    ? Path.Combine(output, Path.GetFileName(input))
                    : output;
                work.Add((Path.GetFileName(input), Path.GetFullPath(input), Path.GetFullPath(dst)));
            }
            else if (Directory.Exists(input))
            {
                string inRoot  = Path.GetFullPath(input).TrimEnd('\\', '/');
                string outRoot = Path.GetFullPath(output).TrimEnd('\\', '/');
                // Snapshot before any output is written, so an output folder nested
                // under the input tree cannot feed the enumeration.
                foreach (var f in Directory.GetFiles(inRoot, "*.pdf", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(inRoot.Length).TrimStart('\\', '/');
                    work.Add((rel, f, Path.Combine(outRoot, rel)));
                }
            }
            else
            {
                con.WriteLine($"Input not found: {input}");
                return 2;
            }

            var log = new List<string> { "File,Status,Detail" };
            int ok = 0, skip = 0, fail = 0;

            foreach (var item in work)
            {
                string status, detail;
                try
                {
                    var dstDir = Path.GetDirectoryName(item.Dst);
                    if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
                    status = BatchResaveOne(item.Src, item.Dst, out detail);
                }
                catch (Exception ex)
                {
                    status = "FAIL";
                    detail = FlattenBatchDetail(ex.Message);
                }

                if (status == "OK") ok++;
                else if (status == "SKIP") skip++;
                else fail++;

                if (!quiet)
                    con.WriteLine(detail.Length > 0 ? $"{status} {item.Rel} ({detail})" : $"{status} {item.Rel}");
                log.Add($"{BatchCsvField(item.Rel)},{status},{BatchCsvField(detail)}");
            }

            con.WriteLine($"Done. {work.Count} files: {ok} OK, {skip} skipped, {fail} failed.");

            if (!string.IsNullOrWhiteSpace(logPath))
            {
                try
                {
                    File.WriteAllLines(logPath, log, new UTF8Encoding(false));
                    con.WriteLine($"Log written to {logPath}");
                }
                catch (Exception ex)
                {
                    con.WriteLine("Could not write log: " + FlattenBatchDetail(ex.Message));
                }
            }

            return fail > 0 ? 1 : 0;
        }

        /// <summary>
        /// Resaves a single PDF through the standard save pipeline.
        /// Returns "OK", "SKIP" (could not enter the plain Modify pipeline;
        /// reason in <paramref name="detail"/>), or "FAIL" (opened but the
        /// save itself failed - the case the harness exists to catch).
        /// </summary>
        private static string BatchResaveOne(string src, string dst, out string detail)
        {
            detail = string.Empty;

            // The GUI strips encryption at open time (PDFium round-trip) before editing.
            // Batch mode deliberately does not: an encryption strip is not a plain resave,
            // and reporting it as one would poison the conformance comparison.
            try
            {
                if (CliPdfFileHasEncryption(src))
                {
                    detail = "encrypted - batch mode does not strip encryption";
                    return "SKIP";
                }
            }
            catch (Exception ex)
            {
                detail = "unreadable: " + FlattenBatchDetail(ex.Message);
                return "SKIP";
            }

            PdfDocument doc;
            try
            {
                doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify);
            }
            catch (Exception ex)
            {
                detail = "open failed: " + FlattenBatchDetail(ex.Message);
                return "SKIP";
            }

            try
            {
                // Same pre-save pipeline as SaveInPlace for a document with no user edits.
                NormalizeDocumentForSave(doc);    // /Outlines, /CropBox, dead-signature scrubs
                StripLinkAnnotationBorders(doc);  // link borders are stripped on every GUI save
                doc.Save(dst);
                doc.Close();
                return "OK";
            }
            catch (Exception ex)
            {
                try { doc.Close(); } catch { }
                detail = "save failed: " + FlattenBatchDetail(ex.Message);
                return "FAIL";
            }
        }

        // Attaches to the parent terminal's console when launched from one.
        // Returns TextWriter.Null when there is no parent console (e.g. double-click),
        // so CLI code can write unconditionally.
        private static TextWriter OpenBatchConsole()
        {
            try
            {
                if (BatchAttachConsole(BatchAttachParentProcess))
                    return new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            }
            catch { }
            return TextWriter.Null;
        }

        /// <summary>
        /// True if the PDF file has an /Encrypt entry in its trailer. Scans the
        /// last 2 KB so it is fast, and works regardless of how PdfSharpCore reports
        /// security state after authenticating with an empty password.
        /// </summary>
        private static bool CliPdfFileHasEncryption(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                long scan = Math.Min(2048, fs.Length);
                fs.Seek(-scan, SeekOrigin.End);
                var buf = new byte[scan];
                _ = fs.Read(buf, 0, buf.Length);
                var text = Encoding.GetEncoding(1252).GetString(buf);
                return text.Contains("/Encrypt");
            }
            catch { return false; }
        }

        private static string FlattenBatchDetail(string? s) =>
            (s ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();

        private static string BatchCsvField(string s)
        {
            if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
