using System.IO;
using System.IO.Compression;

namespace TDPdf.Services
{
    /// <summary>
    /// Read access to the files inside an EPUB. The package reader and the page builder only see this
    /// interface, so they run against an in-memory book in tests/PdfCore and against the zip in the app.
    /// </summary>
    internal interface IEpubArchive
    {
        /// <summary>Every file name in the book, '/'-separated, without a leading slash.</summary>
        IReadOnlyCollection<string> EntryNames { get; }

        /// <summary>True when the book holds this file (exact name first, then ignoring case).</summary>
        bool Contains(string path);

        /// <summary>The file's bytes, or null when the book has no such file. Throws
        /// <see cref="InvalidDataException"/> when it is larger than <paramref name="maxBytes"/>.</summary>
        byte[]? Read(string path, long maxBytes = EpubLimits.MaxEntryBytes);
    }

    /// <summary>The caps that keep a hostile book from taking the machine down with it.</summary>
    internal static class EpubLimits
    {
        /// <summary>One file inside the book, after decompression.</summary>
        internal const long MaxEntryBytes = 128L * 1024 * 1024;

        /// <summary>The XML parts (container, package, contents, chapters).</summary>
        internal const long MaxXmlBytes = 32L * 1024 * 1024;

        /// <summary>Spine documents joined into one page. A book with more is refused with a message.</summary>
        internal const int MaxChapters = 5000;

        /// <summary>Contents entries followed, so a crafted nav cannot build an endless outline.</summary>
        internal const int MaxTocEntries = 20000;

        internal const int MaxTocDepth = 12;
    }

    /// <summary>
    /// The zip behind an .epub. The size and entry-count caps are <see cref="ZipLimits"/>'s, checked
    /// before anything is read, and every read counts the bytes it really gets because a zip can declare
    /// a small size and inflate to a large one.
    /// </summary>
    internal sealed class ZipEpubArchive : IEpubArchive, IDisposable
    {
        private readonly ZipArchive _zip;
        private readonly Dictionary<string, ZipArchiveEntry> _exact = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ZipArchiveEntry> _folded = new(StringComparer.OrdinalIgnoreCase);

        private ZipEpubArchive(ZipArchive zip)
        {
            _zip = zip;
            foreach (var e in zip.Entries)
            {
                string name = Normalize(e.FullName);
                if (name.Length == 0 || name.EndsWith('/')) continue;
                _exact.TryAdd(name, e);          // a duplicate name is ignored: the first one wins
                _folded.TryAdd(name, e);
            }
        }

        internal static string Normalize(string name) => name.Replace('\\', '/').TrimStart('/');

        /// <summary>Opens the book, or throws <see cref="InvalidDataException"/> with a reason a person can read.</summary>
        internal static ZipEpubArchive Open(string path)
        {
            string? refusal = ZipLimits.Refusal(path);
            if (refusal is not null) throw new InvalidDataException("this book was not opened because " + refusal);
            ZipArchive zip;
            try { zip = ZipFile.OpenRead(path); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException("this file is not a readable EPUB (" + ex.Message + ")");
            }
            return new ZipEpubArchive(zip);
        }

        public IReadOnlyCollection<string> EntryNames => _exact.Keys;

        public bool Contains(string path) => Find(path) is not null;

        private ZipArchiveEntry? Find(string path)
        {
            string p = Normalize(path);
            return _exact.TryGetValue(p, out var e) ? e : _folded.GetValueOrDefault(p);
        }

        public byte[]? Read(string path, long maxBytes = EpubLimits.MaxEntryBytes)
        {
            var entry = Find(path);
            if (entry is null) return null;
            if (entry.Length > maxBytes)
                throw new InvalidDataException($"\"{path}\" is larger than the {maxBytes / (1024 * 1024)} MB limit");
            using var input = entry.Open();
            using var ms = new MemoryStream((int)Math.Min(entry.Length, maxBytes));
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += n;
                if (total > maxBytes)
                    throw new InvalidDataException($"\"{path}\" expands past the {maxBytes / (1024 * 1024)} MB limit");
                ms.Write(buffer, 0, n);
            }
            return ms.ToArray();
        }

        public void Dispose() => _zip.Dispose();
    }
}
