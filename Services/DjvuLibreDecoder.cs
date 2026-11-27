using System.IO;
using System.Text;

namespace TDPdf.Services
{
    /// <summary>
    /// Reads a DjVu book by running DjVuLibre's <c>djvused</c> (page list, sizes, hidden text, outline) and
    /// <c>ddjvu</c> (one page rendered to a PNM file) as child processes, each under
    /// <see cref="DjvuProcessLimits"/>. A book that crashes or hangs DjVuLibre ends that process and surfaces
    /// here as an exception for that page; the open tabs are never in the same process.
    /// </summary>
    internal sealed class DjvuLibreDecoder : IDjvuDecoder
    {
        private readonly string _ddjvu, _djvused, _book, _workDir;
        private readonly CancellationToken _ct;
        private List<DjvuPageInfo> _pages = new();
        private Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        private Dictionary<int, string>? _text;

        private DjvuLibreDecoder(string ddjvu, string djvused, string book, CancellationToken ct)
        {
            _ddjvu = ddjvu;
            _djvused = djvused;
            _book = Path.GetFullPath(book);
            _ct = ct;
            _workDir = Path.Combine(Path.GetTempPath(), "tdpdf-djvu-" + Guid.NewGuid().ToString("N")[..12]);
        }

        /// <summary>Opens the book: asks DjVuLibre for the page count, the sizes and the page names. Throws <see cref="BookConversionException"/>.</summary>
        internal static DjvuLibreDecoder Open(DjvuTools tools, string bookPath, CancellationToken ct)
        {
            var d = new DjvuLibreDecoder(tools.Ddjvu, tools.Djvused, bookPath, ct);
            try
            {
                string n = d.Sed("n").Trim();
                if (!int.TryParse(n, out int count) || count < 1)
                    throw new BookConversionException("This DjVu book has no pages.");
                if (count > 50000) throw new BookConversionException($"This DjVu book has {count:N0} pages, more than TDPdf opens.");
                d._pages = DjvuText.ParseDump(d.Sed("dump"));
                d._ids = DjvuText.ParseLs(d.Sed("ls"));
                // An indirect or oddly built book may not list every page's INFO in the dump: the pages it does
                // not list are taken as 300 dpi at the size the first listed page has, and rendering corrects the size.
                while (d._pages.Count < count)
                    d._pages.Add(d._pages.Count > 0 ? d._pages[^1] : new DjvuPageInfo(2550, 3300, 300));
                if (d._pages.Count > count) d._pages.RemoveRange(count, d._pages.Count - count);
                return d;
            }
            catch (Exception ex) when (ex is not BookConversionException and not OperationCanceledException)
            {
                d.Dispose();
                throw new BookConversionException("This DjVu book could not be read: " + ex.Message, ex);
            }
            catch { d.Dispose(); throw; }
        }

        public int PageCount => _pages.Count;
        public DjvuPageInfo GetPageInfo(int pageIndex) => _pages[pageIndex];

        private string Sed(string command)
        {
            var r = DjvuProcess.Run(_djvused, new[] { _book, "-e", command }, DjvuProcessLimits.ForListing, _ct);
            if (r.ExitCode != 0) throw new InvalidDataException("djvused: " + FirstLine(r.Stderr));
            return Encoding.Latin1.GetString(r.Stdout);
        }

        private static string FirstLine(string stderr)
        {
            foreach (string l in stderr.Split('\n'))
            {
                string t = l.Trim().TrimStart('*').Trim();
                if (t.Length > 0) return t.Length > 200 ? t[..200] : t;
            }
            return "failed";
        }

        public DjvuRaster Render(int pageIndex, long maxPixels, CancellationToken ct)
        {
            var info = _pages[pageIndex];
            var args = new List<string> { "-format=pnm", $"-page={pageIndex + 1}" };
            long pixels = (long)info.Width * info.Height;
            if (pixels > maxPixels && pixels > 0)
            {
                double f = Math.Sqrt((double)maxPixels / pixels);
                args.Add($"-size={Math.Max(1, (int)(info.Width * f))}x{Math.Max(1, (int)(info.Height * f))}");
            }
            Directory.CreateDirectory(_workDir);
            string outFile = Path.Combine(_workDir, $"p{pageIndex + 1}.pnm");
            args.Add(_book);
            args.Add(outFile);
            try
            {
                var r = DjvuProcess.Run(_ddjvu, args, DjvuProcessLimits.ForPage, ct);
                if (r.ExitCode != 0 || !File.Exists(outFile))
                    throw new InvalidDataException("ddjvu: " + FirstLine(r.Stderr));
                if (new FileInfo(outFile).Length > DjvuProcessLimits.ForPage.MaxOutputBytes)
                    throw new InvalidDataException("the rendered page is too large");
                return DjvuPnm.Parse(File.ReadAllBytes(outFile));
            }
            finally { try { File.Delete(outFile); } catch { } }
        }

        public IReadOnlyList<DjvuWord> GetWords(int pageIndex)
        {
            if (_text is null)
            {
                try
                {
                    _text = DjvuText.ParseOutputTxt(Sed("output-txt"), id => _ids.TryGetValue(id, out int p) ? p : null);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or TimeoutException)
                {
                    _text = new Dictionary<int, string>();   // no text layer: the pages still convert
                }
            }
            return _text.TryGetValue(pageIndex, out var sexp)
                ? DjvuText.ParsePageText(sexp, _pages[pageIndex].Height)
                : Array.Empty<DjvuWord>();
        }

        public IReadOnlyList<DjvuOutlineItem> GetOutline()
        {
            try
            {
                return DjvuText.ParseOutline(Sed("print-outline"), id => _ids.TryGetValue(id, out int p) ? p : null, PageCount);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or TimeoutException)
            {
                return Array.Empty<DjvuOutlineItem>();
            }
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true); } catch { }
        }
    }
}
