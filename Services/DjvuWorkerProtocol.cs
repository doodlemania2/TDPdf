using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace TDPdf.Services
{
    /// <summary>What a frame on the pipe between TDPdf and its DjVu worker carries.</summary>
    internal enum DjvuFrame : byte
    {
        /// <summary>Worker to parent, once, first: the worker is up and speaks this protocol version.</summary>
        Hello = (byte)'H',
        /// <summary>Parent to worker: open the book at this path. Reply: <see cref="Open"/>.</summary>
        Open = (byte)'O',
        /// <summary>Parent to worker: render one page. Reply: <see cref="Page"/>.</summary>
        Page = (byte)'P',
        /// <summary>Parent to worker: the page's hidden text. Reply: <see cref="Text"/>, an s-expression.</summary>
        Text = (byte)'T',
        /// <summary>Parent to worker: the bookmarks. Reply: <see cref="Outline"/>, an s-expression.</summary>
        Outline = (byte)'L',
        /// <summary>Parent to worker: finish up and exit. No reply.</summary>
        Quit = (byte)'Q',
        /// <summary>Worker to parent, in place of any reply: this request failed, and why.</summary>
        Error = (byte)'E',
    }

    /// <summary>What the worker found out about a book when it opened it.</summary>
    internal sealed record DjvuBookHeader(IReadOnlyList<DjvuPageInfo> Pages, IReadOnlyDictionary<string, int> PageIds);

    /// <summary>
    /// The wire format between TDPdf and the process that runs DjVuLibre (<c>TDPdf.exe --djvu-worker</c>), on
    /// the worker's stdin and stdout. Both sides are TDPdf, but the worker reads a book an attacker may have
    /// made and the parent must treat everything it says as untrusted: every length is capped before anything
    /// is allocated, every count is checked, and anything malformed is an <see cref="InvalidDataException"/>
    /// the caller turns into "this DjVu file could not be opened".
    /// <para>A frame is <c>0xD7</c>, a type byte, a 4-byte little-endian payload length and the payload.
    /// Integers inside payloads are little-endian; strings are UTF-8 with a 4-byte length in front.</para>
    /// </summary>
    internal static class DjvuWorkerProtocol
    {
        internal const int Version = 1;
        internal const byte FrameMagic = 0xD7;
        internal const int HeaderBytes = 6;
        /// <summary>The worker never needs to be told more than a path; a bigger request is a bug.</summary>
        internal const int MaxRequestBytes = 64 * 1024;
        internal const int MaxPages = 50_000;
        internal const int MaxIds = 200_000;
        internal const int MaxStringBytes = 4096;
        internal const int MaxErrorChars = 300;
        private static readonly byte[] HelloMagic = { (byte)'T', (byte)'D', (byte)'J', (byte)'W' };

        /// <summary>The argument that makes TDPdf.exe a DjVu worker.</summary>
        internal const string WorkerSwitch = "--djvu-worker";

        // ── frames ─────────────────────────────────────────────────────────────────────────────

        internal static void WriteFrame(Stream s, DjvuFrame type, ReadOnlySpan<byte> payload)
        {
            Span<byte> head = stackalloc byte[HeaderBytes];
            head[0] = FrameMagic;
            head[1] = (byte)type;
            BinaryPrimitives.WriteUInt32LittleEndian(head[2..], (uint)payload.Length);
            s.Write(head);
            s.Write(payload);
            s.Flush();
        }

        /// <summary>
        /// Reads one frame. A payload longer than <paramref name="maxPayload"/> is refused before it is read,
        /// so a lying length cannot make the parent allocate. Throws <see cref="InvalidDataException"/> (not a
        /// frame, or too big) or <see cref="EndOfStreamException"/> (the pipe ended: the worker is gone).
        /// </summary>
        internal static (DjvuFrame Type, byte[] Payload) ReadFrame(Stream s, long maxPayload)
        {
            Span<byte> head = stackalloc byte[HeaderBytes];
            s.ReadExactly(head);
            if (head[0] != FrameMagic) throw new InvalidDataException("the DjVu worker sent something that is not a frame");
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(head[2..]);
            if (len > maxPayload || len > int.MaxValue - 64) throw new InvalidDataException($"the DjVu worker sent a frame of {len:N0} bytes, more than allowed");
            var payload = new byte[len];
            s.ReadExactly(payload);
            return ((DjvuFrame)head[1], payload);
        }

        // ── hello ──────────────────────────────────────────────────────────────────────────────

        internal static byte[] EncodeHello()
        {
            var b = new byte[8];
            HelloMagic.CopyTo(b, 0);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), Version);
            return b;
        }

        internal static void CheckHello(DjvuFrame type, byte[] payload)
        {
            if (type != DjvuFrame.Hello || payload.Length != 8 || !payload.AsSpan(0, 4).SequenceEqual(HelloMagic) ||
                BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4)) != Version)
                throw new InvalidDataException("the DjVu worker did not introduce itself");
        }

        // ── open ───────────────────────────────────────────────────────────────────────────────

        internal static byte[] EncodeOpenRequest(string path)
        {
            byte[] b = Encoding.UTF8.GetBytes(path);
            if (b.Length is 0 or > MaxRequestBytes) throw new ArgumentException("the book's path is empty or too long", nameof(path));
            return b;
        }

        internal static string DecodeOpenRequest(byte[] payload)
        {
            if (payload.Length is 0 or > MaxRequestBytes) throw new InvalidDataException("bad open request");
            return Encoding.UTF8.GetString(payload);
        }

        internal static byte[] EncodeOpenReply(DjvuBookHeader h)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            w.Write(h.Pages.Count);
            foreach (var p in h.Pages) { w.Write(p.Width); w.Write(p.Height); w.Write(p.Dpi); }
            w.Write(h.PageIds.Count);
            foreach (var kv in h.PageIds) { WriteString(w, kv.Key); w.Write(kv.Value); }
            w.Flush();
            return ms.ToArray();
        }

        internal static DjvuBookHeader DecodeOpenReply(byte[] payload)
        {
            try
            {
                using var ms = new MemoryStream(payload, writable: false);
                using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
                int n = r.ReadInt32();
                if (n < 1 || n > MaxPages) throw new InvalidDataException($"the book reports {n} pages");
                if (ms.Length - ms.Position < (long)n * 12) throw new InvalidDataException("the page list is cut short");
                var pages = new List<DjvuPageInfo>(n);
                for (int i = 0; i < n; i++)
                {
                    int w = r.ReadInt32(), h = r.ReadInt32(), dpi = r.ReadInt32();
                    if (w < 1 || h < 1 || (long)w * h > DjvuPnm.MaxPixels * 4) throw new InvalidDataException("a page has an impossible size");
                    pages.Add(new DjvuPageInfo(w, h, dpi));
                }
                int ids = r.ReadInt32();
                if (ids < 0 || ids > MaxIds) throw new InvalidDataException("too many page names");
                var map = new Dictionary<string, int>(Math.Min(ids, 4096), StringComparer.Ordinal);
                for (int i = 0; i < ids; i++)
                {
                    string id = ReadString(r);
                    int page = r.ReadInt32();
                    if (page >= 0 && page < n) map.TryAdd(id, page);
                }
                if (ms.Position != ms.Length) throw new InvalidDataException("trailing bytes after the page list");
                return new DjvuBookHeader(pages, map);
            }
            catch (EndOfStreamException) { throw new InvalidDataException("the page list is cut short"); }
            catch (DecoderFallbackException) { throw new InvalidDataException("a page name is not text"); }
        }

        // ── page ───────────────────────────────────────────────────────────────────────────────

        internal static byte[] EncodePageRequest(int pageIndex, long maxPixels)
        {
            var b = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(b, pageIndex);
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(4), maxPixels);
            return b;
        }

        internal static (int PageIndex, long MaxPixels) DecodePageRequest(byte[] payload, int pageCount)
        {
            if (payload.Length != 12) throw new InvalidDataException("bad page request");
            int i = BinaryPrimitives.ReadInt32LittleEndian(payload);
            long max = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(4));
            if (i < 0 || i >= pageCount) throw new InvalidDataException("page out of range");
            if (max < 1) throw new InvalidDataException("bad pixel cap");
            return (i, Math.Min(max, DjvuPnm.MaxPixels));
        }

        internal static byte[] EncodeTextRequest(int pageIndex)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, pageIndex);
            return b;
        }

        internal static int DecodeTextRequest(byte[] payload, int pageCount)
        {
            if (payload.Length != 4) throw new InvalidDataException("bad text request");
            int i = BinaryPrimitives.ReadInt32LittleEndian(payload);
            if (i < 0 || i >= pageCount) throw new InvalidDataException("page out of range");
            return i;
        }

        internal static long RasterBytes(DjvuPageKind kind, int width, int height) => kind switch
        {
            DjvuPageKind.Bitonal => (long)((width + 7) / 8) * height,
            DjvuPageKind.Gray => (long)width * height,
            _ => (long)width * height * 3,
        };

        internal static byte[] EncodePageReply(DjvuRaster r)
        {
            var b = new byte[9 + r.Data.Length];
            b[0] = (byte)r.Kind;
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(1), r.Width);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), r.Height);
            r.Data.CopyTo(b, 9);
            return b;
        }

        /// <summary>Writes a page reply frame straight from the pixel buffer, without making a second copy of it.</summary>
        internal static void WritePageReply(Stream s, DjvuPageKind kind, int width, int height, ReadOnlySpan<byte> data)
        {
            Span<byte> head = stackalloc byte[HeaderBytes + 9];
            head[0] = FrameMagic;
            head[1] = (byte)DjvuFrame.Page;
            BinaryPrimitives.WriteUInt32LittleEndian(head[2..], (uint)(9 + data.Length));
            head[6] = (byte)kind;
            BinaryPrimitives.WriteInt32LittleEndian(head[7..], width);
            BinaryPrimitives.WriteInt32LittleEndian(head[11..], height);
            s.Write(head);
            s.Write(data);
            s.Flush();
        }

        internal static DjvuRaster DecodePageReply(byte[] payload)
        {
            if (payload.Length < 9) throw new InvalidDataException("the page picture is cut short");
            if (payload[0] > (byte)DjvuPageKind.Color) throw new InvalidDataException("unknown picture kind");
            var kind = (DjvuPageKind)payload[0];
            int w = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(1)), h = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(5));
            if (w < 1 || h < 1 || (long)w * h > DjvuPnm.MaxPixels) throw new InvalidDataException("the page picture has an impossible size");
            long expected = RasterBytes(kind, w, h);
            if (payload.Length - 9 != expected) throw new InvalidDataException("the page picture does not match its size");
            var data = new byte[expected];
            Buffer.BlockCopy(payload, 9, data, 0, (int)expected);
            return new DjvuRaster(kind, w, h, data);
        }

        // ── text, outline, errors ──────────────────────────────────────────────────────────────

        /// <summary>The s-expression text exactly as DjVuLibre printed it: bytes in, Latin-1 string out, as <see cref="DjvuText"/> expects.</summary>
        internal static string DecodeSexp(byte[] payload) => Encoding.Latin1.GetString(payload);

        internal static byte[] EncodeError(string message)
        {
            string m = message.Length > MaxErrorChars ? message[..MaxErrorChars] : message;
            return Encoding.UTF8.GetBytes(m);
        }

        /// <summary>The worker's reason text, cleaned: no control characters, bounded.</summary>
        internal static string DecodeError(byte[] payload)
        {
            string s = Encoding.UTF8.GetString(payload, 0, Math.Min(payload.Length, MaxErrorChars * 4));
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (sb.Length >= MaxErrorChars) break;
                sb.Append(char.IsControl(c) ? ' ' : c);
            }
            return sb.ToString().Trim();
        }

        // ── strings ────────────────────────────────────────────────────────────────────────────

        private static void WriteString(BinaryWriter w, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            if (b.Length > MaxStringBytes) b = b[..MaxStringBytes];
            w.Write(b.Length);
            w.Write(b);
        }

        private static string ReadString(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > MaxStringBytes) throw new InvalidDataException("a name is too long");
            byte[] b = r.ReadBytes(n);
            if (b.Length != n) throw new InvalidDataException("a name is cut short");
            return new UTF8Encoding(false, true).GetString(b);
        }

        // ── the worker's command line ──────────────────────────────────────────────────────────

        /// <summary>
        /// True when <paramref name="args"/> is exactly <c>--djvu-worker &lt;protocol version&gt;</c>, the only
        /// way the parent starts the worker. Anything else (extra arguments, another version, a typo) is not a
        /// worker launch: a person who types the switch by hand gets the normal app, not a silent process.
        /// </summary>
        internal static bool IsWorkerLaunch(string[] args) =>
            args.Length == 2 && args[0] == WorkerSwitch &&
            args[1] == Version.ToString(System.Globalization.CultureInfo.InvariantCulture);

        internal static string[] WorkerArguments() => new[] { WorkerSwitch, Version.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    }
}
