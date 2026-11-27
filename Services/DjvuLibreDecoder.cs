using System.IO;

namespace TDPdf.Services
{
    /// <summary>How the parent starts the DjVu worker. Production is TDPdf.exe itself; tests substitute a fake.</summary>
    internal sealed record DjvuWorkerLaunch(string Exe, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? ExtraEnvironment = null)
    {
        /// <summary>This very program with <c>--djvu-worker 1</c>.</summary>
        internal static DjvuWorkerLaunch? ForThisApp()
        {
            string? exe = Environment.ProcessPath;
            return string.IsNullOrEmpty(exe) ? null : new DjvuWorkerLaunch(exe, DjvuWorkerProtocol.WorkerArguments());
        }
    }

    /// <summary>
    /// Reads a DjVu book through DjVuLibre's library, which runs in a separate process (<c>TDPdf.exe
    /// --djvu-worker</c>, see <see cref="DjvuWorker"/>) so that a book that crashes or hangs DjVuLibre ends
    /// that process and surfaces here as an exception for that request; the open tabs are never in the same
    /// process. One worker serves the whole book. If it dies, the next request starts a fresh one and opens
    /// the book again; three deaths in a row mean the book is not readable and the whole open is refused.
    /// </summary>
    internal sealed class DjvuLibreDecoder : IDjvuDecoder
    {
        /// <summary>What a person reads when the book cannot be opened.</summary>
        internal const string CouldNotOpen = "This DjVu file couldn't be opened.";
        private const int MaxConsecutiveDeaths = 3;

        private readonly DjvuWorkerLaunch _launch;
        private readonly string _book;
        private readonly CancellationToken _ct;
        private readonly DjvuProcessLimits _pageLimits, _listingLimits;
        private readonly object _gate = new();
        private DjvuWorkerProcess? _worker;
        private DjvuBookHeader _header = new(Array.Empty<DjvuPageInfo>(), new Dictionary<string, int>());
        private int _deaths;
        private bool _disposed;
        private Exception? _broken;

        private DjvuLibreDecoder(DjvuWorkerLaunch launch, string book, CancellationToken ct, DjvuProcessLimits? page, DjvuProcessLimits? listing)
        {
            _launch = launch;
            _pageLimits = page ?? DjvuProcessLimits.ForPage;
            _listingLimits = listing ?? DjvuProcessLimits.ForListing;
            _book = Path.GetFullPath(book);
            _ct = ct;
        }

        /// <summary>Opens the book: starts the worker, asks it for the page count, sizes and page names. Throws <see cref="BookConversionException"/>.</summary>
        internal static DjvuLibreDecoder Open(DjvuWorkerLaunch launch, string bookPath, CancellationToken ct,
            DjvuProcessLimits? pageLimits = null, DjvuProcessLimits? listingLimits = null)
        {
            var d = new DjvuLibreDecoder(launch, bookPath, ct, pageLimits, listingLimits);
            try
            {
                d._header = d.OpenWorker();
                return d;
            }
            catch (BookConversionException) { d.Dispose(); throw; }
            catch (OperationCanceledException) { d.Dispose(); throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException)
            {
                d.Dispose();
                throw new BookConversionException(CouldNotOpen, ex);
            }
            catch { d.Dispose(); throw; }
        }

        public int PageCount => _header.Pages.Count;
        public DjvuPageInfo GetPageInfo(int pageIndex) => _header.Pages[pageIndex];

        // ── the worker ─────────────────────────────────────────────────────────────────────────

        /// <summary>Starts a worker and has it open the book. The caller holds the gate, or is the constructor path.</summary>
        private DjvuBookHeader OpenWorker()
        {
            var limits = _listingLimits;
            var w = DjvuWorkerProcess.Start(_launch.Exe, _launch.Args, _launch.ExtraEnvironment, limits.MaxMemoryBytes, limits.Timeout < TimeSpan.FromSeconds(60) ? limits.Timeout : TimeSpan.FromSeconds(60), _ct);
            try
            {
                var (type, payload) = w.Request(DjvuFrame.Open, DjvuWorkerProtocol.EncodeOpenRequest(_book), limits, _ct);
                if (type == DjvuFrame.Error)
                    throw new BookConversionException(CouldNotOpen + (DjvuWorkerProtocol.DecodeError(payload) is { Length: > 0 } why ? " " + Capitalize(why) : ""));
                if (type != DjvuFrame.Open) throw new InvalidDataException("the DjVu worker answered with the wrong kind of message");
                var header = DjvuWorkerProtocol.DecodeOpenReply(payload);
                _worker = w;
                return header;
            }
            catch { w.Dispose(); throw; }
        }

        private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..] + (s.EndsWith('.') ? "" : ".");

        /// <summary>
        /// Sends a request and returns the reply, restarting a dead worker first. A reply of type Error becomes
        /// an <see cref="InvalidDataException"/> (this request failed; the worker is fine). A worker death
        /// becomes an <see cref="IOException"/> for this request; the next one starts over.
        /// </summary>
        private byte[] Ask(DjvuFrame type, byte[] payload, DjvuProcessLimits limits, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(DjvuLibreDecoder));
                if (_broken is not null) throw new BookConversionException(CouldNotOpen, _broken);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct, ct);
                var token = linked.Token;
                try
                {
                    if (_worker is null || !_worker.IsAlive)
                    {
                        _worker?.Dispose();
                        _worker = null;
                        var again = OpenWorker();
                        if (again.Pages.Count != _header.Pages.Count) throw new InvalidDataException("the book changed while it was being read");
                    }
                    var (rtype, rpayload) = _worker!.Request(type, payload, limits, token);
                    if (rtype == DjvuFrame.Error) { _deaths = 0; throw new InvalidDataException(DjvuWorkerProtocol.DecodeError(rpayload)); }
                    if (rtype != type) throw new DjvuWorkerDiedException("the DjVu worker answered with the wrong kind of message");
                    _deaths = 0;
                    return rpayload;
                }
                catch (Exception ex) when (ex is DjvuWorkerDiedException or TimeoutException)
                {
                    _worker?.Dispose();
                    _worker = null;
                    if (++_deaths >= MaxConsecutiveDeaths)
                    {
                        _broken = ex;
                        throw new BookConversionException(CouldNotOpen, ex);
                    }
                    throw;
                }
                catch (BookConversionException ex)
                {
                    // The reopen after a death was refused: the file will not open again.
                    _worker?.Dispose();
                    _worker = null;
                    if (++_deaths >= MaxConsecutiveDeaths) { _broken = ex; throw; }
                    throw new IOException(ex.Message, ex);
                }
            }
        }

        // ── IDjvuDecoder ───────────────────────────────────────────────────────────────────────

        public DjvuRaster Render(int pageIndex, long maxPixels, CancellationToken ct)
        {
            byte[] reply = Ask(DjvuFrame.Page, DjvuWorkerProtocol.EncodePageRequest(pageIndex, maxPixels), _pageLimits, ct);
            return DjvuWorkerProtocol.DecodePageReply(reply);
        }

        public IReadOnlyList<DjvuWord> GetWords(int pageIndex)
        {
            try
            {
                string sexp = DjvuWorkerProtocol.DecodeSexp(Ask(DjvuFrame.Text, DjvuWorkerProtocol.EncodeTextRequest(pageIndex), _listingLimits, CancellationToken.None));
                return DjvuText.ParsePageText(sexp, _header.Pages[pageIndex].Height);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or TimeoutException)
            {
                return Array.Empty<DjvuWord>();   // no readable text layer: the page still converts
            }
        }

        public IReadOnlyList<DjvuOutlineItem> GetOutline()
        {
            try
            {
                string sexp = DjvuWorkerProtocol.DecodeSexp(Ask(DjvuFrame.Outline, Array.Empty<byte>(), _listingLimits, CancellationToken.None));
                return DjvuText.ParseOutline(sexp, id => _header.PageIds.TryGetValue(id, out int p) ? p : null, PageCount);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or TimeoutException)
            {
                return Array.Empty<DjvuOutlineItem>();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _worker?.Dispose();
                _worker = null;
            }
        }
    }
}
