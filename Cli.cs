using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Printing;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using TDPdf.Services;

namespace TDPdf
{
    // ============================================================
    // Command-line interface        (upstream KillerPDF v1.6.4)
    // ============================================================
    //
    // Dispatcher for every headless CLI command. Invoked from App.OnStartup
    // BEFORE the single-instance mutex, so CLI runs work while a GUI instance
    // is open, never forward to it, and never show a window. A launch with no
    // recognized command flag falls through to the normal GUI (including the
    // classic "TDPdf.exe file.pdf" file-association open).
    //
    // Each command reuses the same pipeline its GUI equivalent runs - the merge
    // named-destination rewrite (BuildNamedDestMap / RewriteNamedDestLinks), the
    // pre-save scrubs (NormalizeDocumentForSave + StripLinkAnnotationBorders),
    // the PDFium decrypt (PdfDocumentService.TryPdfiumRepair), the 150-DPI
    // rasterize (mirrors PdfDocumentService.SaveFlattenedAsync), and the OCR
    // text-layer builder (BuildSearchablePdf) - so CLI output is the kind of
    // file the GUI would produce.
    //
    // Every method here is static and headless: it never constructs a Window or
    // touches UI state. Console output rides on AttachConsole (GUI-subsystem exe,
    // see BatchMode.cs); lines can interleave with the shell prompt. Exit codes
    // are the scripting contract: 0 = success, 1 = operation failed, 2 = bad usage.
    public partial class MainWindow
    {
        // Options that consume the next argument as their value.
        private static readonly string[] CliValueOptions =
        [
            "--log", "--dpi", "--format", "--pages", "--printer", "--lang", "--password", "--copies",
        ];

        // Valueless switches. These need no entry above (and must not have one, or they
        // would swallow the following argument); ParseCliArgs records any other "--x" with
        // an empty value, so the commands just probe them with options.ContainsKey.
        //   --quiet         (--batch-resave)
        //   --transparent   (--to-image, png only)

        // Temp files a CLI run creates (decrypt copies, flatten sources); deleted in TryRunCli's finally.
        private static readonly List<string> _cliTemps = new();

        /// <summary>
        /// Entry point for all CLI commands. Returns false when args carry no
        /// recognized command (normal GUI launch); otherwise runs the command
        /// and returns true with the process exit code set.
        /// </summary>
        internal static bool TryRunCli(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args is null || args.Length == 0) return false;

            // The validation resave keeps its dedicated runner in BatchMode.cs.
            if (args.Any(a => Eq(a, "--batch-resave")))
                return TryRunBatch(args, out exitCode);

            string? command = args.FirstOrDefault(a =>
                Eq(a, "--help") || Eq(a, "-h") || Eq(a, "/?") ||
                Eq(a, "--version") || Eq(a, "-v") ||
                Eq(a, "--merge") || Eq(a, "--extract-pages") || Eq(a, "--split") ||
                Eq(a, "--decrypt") || Eq(a, "--to-image") || Eq(a, "--flatten") ||
                Eq(a, "--print") || Eq(a, "--ocr"));
            if (command is null) return false;

            var con = OpenBatchConsole();
            var (positionals, options) = ParseCliArgs(args, command);

            try
            {
                switch (command.ToLowerInvariant())
                {
                    case "--help":
                    case "-h":
                    case "/?":
                        con.WriteLine(CliHelpText());
                        break;
                    case "--version":
                    case "-v":
                        con.WriteLine(CliVersionString());
                        break;
                    case "--merge":
                        exitCode = CliMerge(positionals, con);
                        break;
                    case "--extract-pages":
                        exitCode = CliExtractPages(positionals, con);
                        break;
                    case "--split":
                        exitCode = CliSplit(positionals, con);
                        break;
                    case "--decrypt":
                        exitCode = CliDecrypt(positionals, options, con);
                        break;
                    case "--to-image":
                        exitCode = CliToImage(positionals, options, con);
                        break;
                    case "--flatten":
                        exitCode = CliFlatten(positionals, options, con);
                        break;
                    case "--print":
                        exitCode = CliPrint(positionals, options, con);
                        break;
                    case "--ocr":
                        exitCode = CliOcr(positionals, options, con);
                        break;
                }
            }
            catch (Exception ex)
            {
                con.WriteLine("Error: " + FlattenBatchDetail(ex.Message));
                exitCode = 1;
            }
            finally
            {
                CliCleanupTemps();   // drop any decrypt/flatten temps the run created
            }
            return true;
        }

        private static bool Eq(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string CliVersionString() =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

        private static string CliHelpText() => string.Join(Environment.NewLine,
        [
            "TDPdf " + CliVersionString() + " - command line usage",
            "",
            "  TDPdf.exe <file.pdf>                                        open in the app",
            "  TDPdf.exe --version | -v                                    print version",
            "  TDPdf.exe --help | -h | /?                                  this text",
            "",
            "  --merge <out.pdf> <in1> <in2> ...        merge PDFs (and images) into one PDF",
            "  --extract-pages <in.pdf> <pages> <out.pdf>",
            "                                           pull pages into a new PDF (pages like 1-3,5,9-12)",
            "  --split <in.pdf> <outDir>                write one PDF per page",
            "  --decrypt <in.pdf> <out.pdf> [--password <p>]",
            "                                           remove encryption (lossless when possible)",
            "  --to-image <in.pdf> <outDir> [--dpi <n>] [--format png|jpg] [--pages <range>] [--transparent]",
            "                                           render pages to images (default 150 dpi, png;",
            "                                           the page background composites to white unless",
            "                                           --transparent is given, which needs --format png)",
            "  --flatten <in.pdf> <out.pdf> [--dpi <n>] rasterize into an uneditable PDF (default 150 dpi)",
            "  --print <in.pdf> [--printer <name>] [--pages <range>] [--copies <n>]",
            "                                           print silently (default printer if none named)",
            "  --ocr <in.pdf> <out.pdf> [--lang <code>] add an invisible searchable text layer (default eng;",
            "                                           other languages download on first use)",
            "  --batch-resave <in> <out> [--log <f.csv>] [--quiet]",
            "                                           resave a file or tree through the standard",
            "                                           open/save pipeline (validation harness)",
            "",
            "Exit codes: 0 success, 1 operation failed, 2 bad usage.",
            "Runs headless and works while the TDPdf window is open.",
        ]);

        /// <summary>
        /// Splits args into positionals (everything after the command flag that
        /// is not an option) and an option dictionary (case-insensitive keys).
        /// </summary>
        private static (List<string> Positionals, Dictionary<string, string> Options)
            ParseCliArgs(string[] args, string command)
        {
            var positionals = new List<string>();
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int start = Array.FindIndex(args, a => Eq(a, command)) + 1;
            for (int i = start; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--", StringComparison.Ordinal))
                {
                    if (CliValueOptions.Any(o => Eq(o, a)) && i + 1 < args.Length)
                        options[a] = args[++i];
                    else
                        options[a] = string.Empty;
                }
                else
                {
                    positionals.Add(a);
                }
            }
            return (positionals, options);
        }

        /// <summary>
        /// Parses a 1-based page range spec like "1-3,5,9-12" into sorted,
        /// distinct 0-based indices. Returns null with a message in error when
        /// the spec is malformed or out of range.
        /// </summary>
        private static List<int>? CliParsePageRange(string spec, int pageCount, out string error)
        {
            error = string.Empty;
            var pages = new SortedSet<int>();
            foreach (var rawPart in spec.Split(','))
            {
                var part = rawPart.Trim();
                if (part.Length == 0) continue;
                int a, b;
                int dash = part.IndexOf('-');
                if (dash > 0)
                {
                    if (!int.TryParse(part[..dash].Trim(), out a) ||
                        !int.TryParse(part[(dash + 1)..].Trim(), out b))
                    { error = $"Bad page range: \"{part}\""; return null; }
                }
                else
                {
                    if (!int.TryParse(part, out a)) { error = $"Bad page number: \"{part}\""; return null; }
                    b = a;
                }
                if (a > b) (a, b) = (b, a);
                if (a < 1 || b > pageCount)
                { error = $"Pages {part} out of range - the document has {pageCount} pages"; return null; }
                for (int p = a; p <= b; p++) pages.Add(p - 1);
            }
            if (pages.Count == 0) { error = "Empty page range"; return null; }
            return [.. pages];
        }

        // ============================================================
        // --merge <out.pdf> <in1> <in2> ...
        // ============================================================
        // Mirrors the GUI merge (Merge_Click): per source PDF, harvest named
        // destinations from a ReadOnly open, copy pages from an Import open, then
        // rewrite named-destination links against the page offset. Image inputs go
        // through the same importer the GUI drop pipeline uses (AddImagePagesFromFile).
        private static int CliMerge(List<string> pos, TextWriter con)
        {
            if (pos.Count < 3)
            {
                con.WriteLine("Usage: TDPdf.exe --merge <out.pdf> <in1.pdf> <in2.pdf> ...");
                return 2;
            }
            string outPath = Path.GetFullPath(pos[0]);
            var inputs = pos.Skip(1).Select(Path.GetFullPath).ToList();

            foreach (var f in inputs)
            {
                if (!File.Exists(f)) { con.WriteLine($"Input not found: {f}"); return 2; }
                if (string.Equals(f, outPath, StringComparison.OrdinalIgnoreCase))
                { con.WriteLine("Output file cannot also be an input."); return 2; }
            }

            using var outPdf = new PdfDocument();
            foreach (var f in inputs)
            {
                if (IsPdfPath(f))
                {
                    int pageOffset = outPdf.PageCount;
                    Dictionary<string, int> namedDestMap;
                    using (var srcRead = PdfReader.Open(f, PdfDocumentOpenMode.ReadOnly))
                        namedDestMap = BuildNamedDestMap(srcRead);
                    using var src = PdfReader.Open(f, PdfDocumentOpenMode.Import);
                    for (int i = 0; i < src.PageCount; i++)
                        outPdf.AddPage(src.Pages[i]);
                    if (namedDestMap.Count > 0)
                        RewriteNamedDestLinks(outPdf, pageOffset, namedDestMap);
                }
                else
                {
                    AddImagePagesFromFile(outPdf, f);
                }
            }

            NormalizeDocumentForSave(outPdf);
            CliEnsureParentDir(outPath);
            outPdf.Save(outPath);
            con.WriteLine($"Merged {inputs.Count} files ({outPdf.PageCount} pages) -> {outPath}");
            return 0;
        }

        // ============================================================
        // --extract-pages <in.pdf> <range> <out.pdf>
        // ============================================================
        private static int CliExtractPages(List<string> pos, TextWriter con)
        {
            if (pos.Count != 3)
            {
                con.WriteLine("Usage: TDPdf.exe --extract-pages <in.pdf> <pages> <out.pdf>   (pages like 1-3,5,9-12)");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), spec = pos[1], outPath = Path.GetFullPath(pos[2]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }

            using var importDoc = PdfReader.Open(inPath, PdfDocumentOpenMode.Import);
            var indices = CliParsePageRange(spec, importDoc.PageCount, out string err);
            if (indices is null) { con.WriteLine(err); return 2; }

            using var newDoc = new PdfDocument();
            foreach (var idx in indices)
                newDoc.AddPage(importDoc.Pages[idx]);
            NormalizeDocumentForSave(newDoc);
            CliEnsureParentDir(outPath);
            newDoc.Save(outPath);
            con.WriteLine($"Extracted {indices.Count} pages -> {outPath}");
            return 0;
        }

        // ============================================================
        // --split <in.pdf> <outDir>
        // ============================================================
        private static int CliSplit(List<string> pos, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: TDPdf.exe --split <in.pdf> <outputFolder>");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outDir = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            Directory.CreateDirectory(outDir);

            using var importDoc = PdfReader.Open(inPath, PdfDocumentOpenMode.Import);
            string baseName = Path.GetFileNameWithoutExtension(inPath);
            int digits = Math.Max(3, importDoc.PageCount.ToString().Length);
            for (int i = 0; i < importDoc.PageCount; i++)
            {
                using var single = new PdfDocument();
                single.AddPage(importDoc.Pages[i]);
                NormalizeDocumentForSave(single);
                single.Save(Path.Combine(outDir, $"{baseName}-page-{(i + 1).ToString().PadLeft(digits, '0')}.pdf"));
            }
            con.WriteLine($"Split {importDoc.PageCount} pages into {outDir}");
            return 0;
        }

        // ============================================================
        // --decrypt <in.pdf> <out.pdf> [--password <p>]
        // ============================================================
        // Without a password: the same lossless PDFium strip the GUI uses at open
        // time (PdfDocumentService.TryPdfiumRepair, owner/permissions encryption),
        // with an Import-rebuild fallback. With a password: PdfSharpCore opens with
        // the password and saves a decrypted copy, the same sequence as the GUI password path.
        private static int CliDecrypt(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: TDPdf.exe --decrypt <in.pdf> <out.pdf> [--password <password>]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            CliEnsureParentDir(outPath);

            options.TryGetValue("--password", out string? password);
            if (!string.IsNullOrEmpty(password))
            {
                using var doc = PdfReader.Open(inPath, password!, PdfDocumentOpenMode.Modify);
                NormalizeDocumentForSave(doc);
                doc.Save(outPath);
                con.WriteLine($"Decrypted -> {outPath}");
                return 0;
            }

            if (PdfDocumentService.TryPdfiumRepair(inPath, outPath))
            {
                con.WriteLine($"Decrypted (lossless) -> {outPath}");
                return 0;
            }
            if (CliTryImportRepair(inPath, outPath))
            {
                con.WriteLine($"Decrypted via page rebuild -> {outPath} (bookmarks/forms may be dropped)");
                return 0;
            }
            con.WriteLine("Could not decrypt. If the file needs a password to open, pass --password.");
            return 1;
        }

        // ============================================================
        // --to-image <in.pdf> <outDir> [--dpi n] [--format png|jpg] [--pages range] [--transparent]
        // ============================================================
        // PDFium leaves unpainted background pixels at BGRA 0,0,0,0, so a bare export
        // used to come out black in JPEG (no alpha channel to honor) and needlessly
        // alpha-laden in PNG. PageImageExporter.Encode composites over white by default;
        // --transparent keeps the raw alpha, and only PNG can carry it.
        //
        // The rasterize/encode/name half of this command lives in PageImageExporter, which the
        // GUI's File ▸ Export Pages as Images… drives with the same arguments; only the source
        // preparation differs (here: decrypt-on-demand; there: burn pending annotations).
        private static int CliToImage(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: TDPdf.exe --to-image <in.pdf> <outputFolder> [--dpi <n>] [--format png|jpg] [--pages <range>] [--transparent]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outDir = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            double dpi = CliParseDpi(options, PageImageExporter.DefaultDpi);
            options.TryGetValue("--format", out var fmtRaw);
            if (!PageImageExporter.TryNormalizeFormat(fmtRaw, out string fmt))
            { con.WriteLine("--format must be png or jpg"); return 2; }

            // JPEG has no alpha channel, so --transparent can only mean something for png.
            // Say so rather than silently writing opaque files the caller did not expect.
            bool transparent = options.ContainsKey("--transparent");
            if (transparent && fmt != PageImageExporter.PngFormat)
            {
                con.WriteLine("--transparent ignored: JPEG has no alpha channel; background composited to white.");
                transparent = false;
            }

            options.TryGetValue("--password", out var password);
            string renderPath = CliPrepareRenderSource(inPath, password);

            List<int>? selected = null;
            if (options.TryGetValue("--pages", out var rangeSpec))
            {
                selected = CliParsePageRange(rangeSpec, PageImageExporter.GetPageCount(renderPath), out string err);
                if (selected is null) { con.WriteLine(err); return 2; }
            }

            string baseName = Path.GetFileNameWithoutExtension(inPath);
            int written = PageImageExporter.Export(renderPath, outDir, baseName, selected, dpi, fmt,
                                                   transparent, report: null, CancellationToken.None);
            con.WriteLine($"Rendered {written} pages at {dpi:0} dpi ({fmt}{(transparent ? ", transparent" : "")}) into {outDir}");
            return 0;
        }

        // ============================================================
        // --flatten <in.pdf> <out.pdf> [--dpi n]
        // ============================================================
        // The headless twin of the GUI's Save Flattened: NormalizeDocumentForSave,
        // then rasterize every page through Docnet/PDFium and rebuild a PNG-per-page
        // PDF sized in points - the same algorithm as PdfDocumentService.SaveFlattenedAsync
        // (150 dpi default), generalized here to honor --dpi.
        private static int CliFlatten(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: TDPdf.exe --flatten <in.pdf> <out.pdf> [--dpi <n>]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            double dpi = CliParseDpi(options, PageImageExporter.DefaultDpi);
            // --transparent belongs to --to-image only: a flattened PDF is a print-ready
            // substitute for the original, so its page images are always opaque.
            if (options.ContainsKey("--transparent"))
                con.WriteLine("--transparent ignored: flattened pages are always opaque.");
            options.TryGetValue("--password", out var password);

            // Build the flatten source exactly as the GUI does: open, scrub, capture point sizes,
            // then rasterize from that normalized copy.
            string renderPath = CliPrepareRenderSource(inPath, password);
            var pageSizes = new List<(double WPt, double HPt)>();
            if (renderPath == inPath)
            {
                // Not already a temp: normalize into one so the raster source matches a GUI save.
                string norm = CliTempFile("cliflat");
                using (var doc = PdfReader.Open(inPath, PdfDocumentOpenMode.Modify))
                {
                    NormalizeDocumentForSave(doc);
                    for (int i = 0; i < doc.PageCount; i++)
                        pageSizes.Add((doc.Pages[i].Width.Point, doc.Pages[i].Height.Point));
                    doc.Save(norm);
                }
                renderPath = norm;
            }
            else
            {
                using var doc = PdfReader.Open(renderPath, PdfDocumentOpenMode.Modify);
                for (int i = 0; i < doc.PageCount; i++)
                    pageSizes.Add((doc.Pages[i].Width.Point, doc.Pages[i].Height.Point));
            }

            using var dr = DocLib.Instance.GetDocReader(renderPath, new PageDimensions(dpi / 72.0));
            int pageCount = dr.GetPageCount();

            using var outDoc = new PdfDocument();
            for (int i = 0; i < pageCount; i++)
            {
                byte[] raw; int w, h;
                using (var pr = dr.GetPageReader(i))
                {
                    raw = pr.GetImage();
                    w = pr.GetPageWidth();
                    h = pr.GetPageHeight();
                }
                if (raw is null || raw.Length == 0 || w <= 0 || h <= 0) continue;
                var png = PageImageExporter.Encode(raw, w, h, PageImageExporter.PngFormat, transparent: false);

                double wPt = i < pageSizes.Count ? pageSizes[i].WPt : w * 72.0 / dpi;
                double hPt = i < pageSizes.Count ? pageSizes[i].HPt : h * 72.0 / dpi;

                var newPage = outDoc.AddPage();
                newPage.Width = XUnit.FromPoint(wPt);
                newPage.Height = XUnit.FromPoint(hPt);
                using var xi = XImage.FromStream(() => new MemoryStream(png));
                using var gfx = XGraphics.FromPdfPage(newPage);
                gfx.DrawImage(xi, 0, 0, newPage.Width.Point, newPage.Height.Point);
            }
            CliEnsureParentDir(outPath);
            outDoc.Save(outPath);
            con.WriteLine($"Flattened {pageCount} pages at {dpi:0} dpi -> {outPath}");
            return 0;
        }

        // ============================================================
        // --print <in.pdf> [--printer name] [--pages range] [--copies n]
        // ============================================================
        // Headless twin of the GUI print spool (PrintPreviewWindow): rasterize at
        // 300 dpi, fit-scale each page centered on the printable area, build a
        // FixedDocument, and hand it to the queue via PrintDialog.PrintDocument.
        // Runs on the WPF UI thread from OnStartup without ever showing a window.
        private static int CliPrint(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 1)
            {
                con.WriteLine("Usage: TDPdf.exe --print <in.pdf> [--printer <name>] [--pages <range>] [--copies <n>]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }

            int copies = 1;
            if (options.TryGetValue("--copies", out var copiesRaw) &&
                (!int.TryParse(copiesRaw, out copies) || copies < 1 || copies > 99))
            { con.WriteLine("--copies must be 1-99"); return 2; }

            options.TryGetValue("--password", out var password);
            string renderPath = CliPrepareRenderSource(inPath, password);

            // Resolve the print queue. Match --printer against FullName, exact first
            // then substring, both case-insensitive.
            using var server = new LocalPrintServer();
            PrintQueue? queue;
            if (options.TryGetValue("--printer", out var printerName) && !string.IsNullOrWhiteSpace(printerName))
            {
                var queues = server.GetPrintQueues(
                    [EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]).ToList();
                queue = queues.FirstOrDefault(q =>
                            string.Equals(q.FullName, printerName, StringComparison.OrdinalIgnoreCase))
                     ?? queues.FirstOrDefault(q =>
                            q.FullName.IndexOf(printerName, StringComparison.OrdinalIgnoreCase) >= 0);
                if (queue is null)
                {
                    con.WriteLine($"Printer not found: {printerName}. Available:");
                    foreach (var q in queues) con.WriteLine("  " + q.FullName);
                    return 2;
                }
            }
            else
            {
                queue = LocalPrintServer.GetDefaultPrintQueue();
                if (queue is null) { con.WriteLine("No default printer is configured."); return 1; }
            }

            // Rasterize the selected pages at 300 dpi.
            var bitmaps = new List<(BitmapSource Bs, int W, int H)>();
            List<int> selected;
            using (var dr = DocLib.Instance.GetDocReader(renderPath, new PageDimensions(300.0 / 72.0)))
            {
                int pageCount = dr.GetPageCount();
                if (options.TryGetValue("--pages", out var rangeSpec))
                {
                    var parsed = CliParsePageRange(rangeSpec, pageCount, out string err);
                    if (parsed is null) { con.WriteLine(err); return 2; }
                    selected = parsed;
                }
                else
                {
                    selected = [.. Enumerable.Range(0, pageCount)];
                }
                foreach (var idx in selected)
                {
                    byte[] raw; int w, h;
                    using (var pr = dr.GetPageReader(idx))
                    {
                        raw = pr.GetImage();
                        w = pr.GetPageWidth();
                        h = pr.GetPageHeight();
                    }
                    if (raw is null || raw.Length == 0 || w <= 0 || h <= 0) continue;
                    var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, raw, w * 4);
                    bs.Freeze();
                    bitmaps.Add((bs, w, h));
                }
            }

            if (bitmaps.Count == 0) { con.WriteLine("No pages could be rendered for printing."); return 1; }

            // Orient the sheet to the majority of the selected pages.
            bool landscape = bitmaps.Count(b => b.W > b.H) * 2 > bitmaps.Count;

            var pd = new System.Windows.Controls.PrintDialog { PrintQueue = queue };
            var ticket = pd.PrintTicket;
            // Copies ride the ticket (matches PrintPreviewWindow - no manual copy loop, which
            // double-printed on some drivers).
            ticket.CopyCount = copies;
            ticket.PageOrientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
            pd.PrintTicket = ticket;
            double aw = pd.PrintableAreaWidth, ah = pd.PrintableAreaHeight;
            if (landscape && ah > aw) (aw, ah) = (ah, aw);
            if (aw <= 0 || ah <= 0) { con.WriteLine("Printer reported no printable area."); return 1; }

            var fixedDoc = new FixedDocument();
            foreach (var (bs, w, h) in bitmaps)
            {
                double wDip = w * 96.0 / 300.0, hDip = h * 96.0 / 300.0;
                double s = Math.Min(aw / wDip, ah / hDip);
                double sw = wDip * s, sh = hDip * s;
                var img = new System.Windows.Controls.Image { Source = bs, Width = sw, Height = sh };
                var fp = new FixedPage { Width = aw, Height = ah };
                FixedPage.SetLeft(img, (aw - sw) / 2);
                FixedPage.SetTop(img, (ah - sh) / 2);
                fp.Children.Add(img);
                fp.Measure(new Size(aw, ah));
                fp.Arrange(new Rect(0, 0, aw, ah));
                fp.UpdateLayout();
                var pc = new PageContent();
                ((IAddChild)pc).AddChild(fp);
                fixedDoc.Pages.Add(pc);
            }

            pd.PrintDocument(fixedDoc.DocumentPaginator, "TDPdf");
            con.WriteLine($"Sent {selected.Count} pages x{copies} to \"{queue.FullName}\".");
            return 0;
        }

        // ============================================================
        // --ocr <in.pdf> <out.pdf> [--lang code]
        // ============================================================
        // Reuses the GUI's searchable-PDF core (BuildSearchablePdf in Ocr.cs):
        // Docnet render, Tesseract per page, invisible text drawn over each word.
        // The GUI's model-download gate is dialog-driven, so the CLI has its own
        // silent equivalent honoring the OcrHighQuality setting.
        private static int CliOcr(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: TDPdf.exe --ocr <in.pdf> <out.pdf> [--lang <code>]   (default eng)");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            options.TryGetValue("--lang", out var langRaw);
            string lang = string.IsNullOrWhiteSpace(langRaw) ? "eng" : langRaw!.Trim().ToLowerInvariant();

            if (!CliEnsureOcrLanguage(lang, con)) return 1;

            options.TryGetValue("--password", out var password);
            string srcForOcr = CliPrepareRenderSource(inPath, password);

            CliEnsureParentDir(outPath);
            var (pages, words) = BuildSearchablePdf(srcForOcr, outPath,
                (i, n) => { if (i == 0 || i == n - 1 || i % 10 == 0) con.WriteLine($"OCR page {i + 1}/{n}"); },
                CancellationToken.None, lang);

            con.WriteLine($"OCR complete: {pages} pages, {words} words -> {outPath}");
            return 0;
        }

        /// <summary>
        /// Silent equivalent of the GUI's model-download gate (EnsureOcrModelsReadyAsync):
        /// nothing is bundled, so a missing language model streams from the tessdata repos
        /// on first use, honoring the app's High Quality setting, with the same
        /// .part-then-move atomicity. Runs the download on the thread pool - OnStartup's
        /// dispatcher is not pumping, so awaiting on the captured WPF context would deadlock.
        /// </summary>
        private static bool CliEnsureOcrLanguage(string lang, TextWriter con)
        {
            OcrNativeBootstrap.EnsureTessDataDir();
            var dest = Path.Combine(OcrNativeBootstrap.TessDataDir, lang + ".traineddata");
            if (File.Exists(dest)) return true;

            bool hq = TDPdf.Properties.Settings.Default.OcrHighQuality;
            string url = (hq
                ? "https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/main/"
                : "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/") + lang + ".traineddata";
            con.WriteLine($"Downloading OCR language '{lang}' ({(hq ? "high quality" : "standard")})...");
            try
            {
                Task.Run(async () =>
                {
                    using var http = MakeDownloadClient();
                    using var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    var part = dest + ".part";
                    using (var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var f = File.Create(part))
                        await s.CopyToAsync(f).ConfigureAwait(false);
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(part, dest);
                }).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception ex)
            {
                con.WriteLine($"Could not download language '{lang}': " + FlattenBatchDetail(ex.Message));
                con.WriteLine("Check the language code (e.g. eng, spa, fra, deu, jpn, tur, rus, chi_sim, chi_tra) and your connection.");
                return false;
            }
        }

        // ============================================================
        // Shared CLI helpers
        // ============================================================

        // Produces a render/OCR source that Docnet can open: a decrypted copy when the
        // file is encrypted (password path, else the lossless PDFium strip with an
        // Import-rebuild fallback), otherwise the original path unchanged.
        private static string CliPrepareRenderSource(string inPath, string? password)
        {
            if (!CliPdfFileHasEncryption(inPath)) return inPath;

            string dec = CliTempFile("clidec");
            if (!string.IsNullOrEmpty(password))
            {
                using var pdoc = PdfReader.Open(inPath, password!, PdfDocumentOpenMode.Modify);
                NormalizeDocumentForSave(pdoc);
                pdoc.Save(dec);
                return dec;
            }
            if (PdfDocumentService.TryPdfiumRepair(inPath, dec) || CliTryImportRepair(inPath, dec))
                return dec;
            throw new InvalidOperationException(
                "File is encrypted and could not be unlocked - pass --password if it needs one.");
        }

        // Import-rebuild fallback (mirrors the GUI's TryImportRepairToPath): copy pages
        // into a fresh document and save. Drops bookmarks/forms but recovers many files
        // PDFium cannot strip. Returns false on any failure.
        private static bool CliTryImportRepair(string sourcePath, string destPath)
        {
            try
            {
                using var importDoc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
                using var cleanDoc = new PdfDocument();
                for (int i = 0; i < importDoc.PageCount; i++)
                    cleanDoc.AddPage(importDoc.Pages[i]);
                cleanDoc.Save(destPath);
                return true;
            }
            catch { return false; }
        }

        private static void CliEnsureParentDir(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        // Same 24-1200 window the export dialog enforces (PageImageExporter owns the bounds);
        // anything unparsable or out of range falls back to the caller's default.
        private static double CliParseDpi(Dictionary<string, string> options, double fallback)
        {
            if (options.TryGetValue("--dpi", out var s) && PageImageExporter.TryParseDpi(s, out double d))
                return d;
            return fallback;
        }

        private static string CliTempFile(string tag)
        {
            string p = Path.Combine(Path.GetTempPath(), $"tdpdf_{tag}_{Guid.NewGuid():N}.pdf");
            _cliTemps.Add(p);
            return p;
        }

        private static void CliCleanupTemps()
        {
            foreach (var p in _cliTemps)
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
            _cliTemps.Clear();
        }
    }
}
