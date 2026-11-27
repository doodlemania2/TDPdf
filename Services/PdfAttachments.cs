using System.Globalization;
using System.IO;
using System.Text;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Filters;

namespace TDPdf.Services
{
    /// <summary>One file attached to a PDF, as listed by <see cref="PdfAttachments.List"/>.</summary>
    internal sealed class AttachmentEntry
    {
        /// <summary>The name the PDF gives the file (not yet safe to use on disk, see <see cref="AttachmentNames"/>).</summary>
        public required string Name { get; init; }
        public string? Description { get; init; }
        /// <summary>The size the file declares, or the stored size when it declares none; null when unknown.</summary>
        public long? Size { get; init; }
        public DateTime? Modified { get; init; }
        /// <summary>"Document" for the embedded-files list, or "Page N" for a file-attachment annotation.</summary>
        public required string Source { get; init; }
        internal PdfDictionary? StreamDict { get; init; }

        /// <summary>
        /// The file's bytes. Refuses a file that declares more than <see cref="PdfAttachments.MaxBytes"/>
        /// and stops at the decoder's own decompression cap, so a hostile attachment cannot exhaust memory.
        /// </summary>
        /// <exception cref="InvalidDataException">The file is missing, too large or cannot be decoded.</exception>
        public byte[] ReadBytes()
        {
            if (StreamDict?.Stream is not { } stream) throw new InvalidDataException("The attachment has no data in the PDF.");
            if (Size is > PdfAttachments.MaxBytes) throw new InvalidDataException("The attachment is too large to extract.");
            byte[] bytes;
            try { bytes = stream.UnfilteredValue; }
            catch (InvalidOperationException ex) { throw new InvalidDataException("The attachment is too large to extract.", ex); }
            catch (Exception ex) { throw new InvalidDataException("The attachment could not be decoded: " + ex.Message, ex); }
            if (bytes.LongLength > PdfAttachments.MaxBytes) throw new InvalidDataException("The attachment is too large to extract.");
            return bytes;
        }
    }

    /// <summary>
    /// Lists the files attached to a PDF: the catalog's <c>/Names /EmbeddedFiles</c> name tree and
    /// <c>/FileAttachment</c> annotations. The walk is bounded in depth and in entries and survives
    /// loops, so a damaged or hostile tree ends the listing rather than hanging it. Pure
    /// PdfSharpCore, so tests/PdfCore compiles this exact file.
    /// </summary>
    internal static class PdfAttachments
    {
        /// <summary>Deepest name-tree nesting followed.</summary>
        internal const int MaxDepth = 16;
        /// <summary>Most attachments listed from one document.</summary>
        internal const int MaxEntries = 5_000;
        /// <summary>Most tree nodes visited.</summary>
        internal const int MaxNodes = 20_000;
        /// <summary>Largest attachment extracted: the same cap the Flate decoder enforces per stream.</summary>
        internal const long MaxBytes = FlateDecode.MaxDecodedLength;

        internal static IReadOnlyList<AttachmentEntry> List(PdfDocument doc)
        {
            var result = new List<AttachmentEntry>();
            var seenStreams = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

            try
            {
                if (PdfItems.DerefDict(doc.Internals.Catalog.Elements["/Names"]) is { } names &&
                    PdfItems.DerefDict(names.Elements["/EmbeddedFiles"]) is { } root)
                    WalkTree(root, result, seenStreams);
            }
            catch (Exception) { /* a damaged tree ends the walk; whatever was read stays */ }

            try
            {
                for (int p = 0; p < doc.PageCount && result.Count < MaxEntries; p++)
                {
                    if (PdfItems.Deref(doc.Pages[p].Elements["/Annots"]) is not PdfArray annots) continue;
                    int count = Math.Min(annots.Elements.Count, MaxEntries);
                    for (int i = 0; i < count && result.Count < MaxEntries; i++)
                    {
                        if (PdfItems.DerefDict(annots.Elements[i]) is not { } a || NameOf(a.Elements["/Subtype"]) != "/FileAttachment") continue;
                        var entry = FromFileSpec(PdfItems.Deref(a.Elements["/FS"]), TextOf(a.Elements["/Contents"]), null, $"Page {p + 1}", seenStreams);
                        if (entry is not null) result.Add(entry);
                    }
                }
            }
            catch (Exception) { /* same: keep what was read */ }
            return result;
        }

        private static void WalkTree(PdfDictionary root, List<AttachmentEntry> result, HashSet<PdfDictionary> seenStreams)
        {
            var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<(PdfDictionary Node, int Depth)>();
            stack.Push((root, 0));
            int nodes = 0;
            while (stack.Count > 0 && nodes < MaxNodes && result.Count < MaxEntries)
            {
                var (node, depth) = stack.Pop();
                if (!visited.Add(node)) continue;
                nodes++;

                if (PdfItems.Deref(node.Elements["/Names"]) is PdfArray pairs)
                {
                    for (int i = 0; i + 1 < pairs.Elements.Count && result.Count < MaxEntries; i += 2)
                    {
                        var entry = FromFileSpec(PdfItems.Deref(pairs.Elements[i + 1]), null, TextOf(pairs.Elements[i]), "Document", seenStreams);
                        if (entry is not null) result.Add(entry);
                    }
                }

                if (depth < MaxDepth && PdfItems.Deref(node.Elements["/Kids"]) is PdfArray kids)
                {
                    // Pushed in reverse so the first kid is read first.
                    int n = Math.Min(kids.Elements.Count, MaxNodes);
                    for (int i = n - 1; i >= 0; i--)
                        if (PdfItems.DerefDict(kids.Elements[i]) is { } kid) stack.Push((kid, depth + 1));
                }
            }
        }

        private static AttachmentEntry? FromFileSpec(PdfItem? spec, string? annotDescription, string? treeKey, string source,
            HashSet<PdfDictionary> seenStreams)
        {
            if (spec is not PdfDictionary fs) return null;
            if (PdfItems.DerefDict(fs.Elements["/EF"]) is not { } ef) return null;
            var file = PdfItems.DerefDict(ef.Elements["/UF"]) ?? PdfItems.DerefDict(ef.Elements["/F"]) ?? PdfItems.DerefDict(ef.Elements["/DOS"]);
            if (file?.Stream is null) return null;
            if (!seenStreams.Add(file)) return null;   // the same file reached by two routes is listed once

            string name = TextOf(fs.Elements["/UF"]) ?? TextOf(fs.Elements["/F"]) ?? treeKey ?? "attachment";
            string? desc = TextOf(fs.Elements["/Desc"]) ?? annotDescription;
            var pars = PdfItems.DerefDict(file.Elements["/Params"]);

            long? size = null;
            if (pars is not null && PdfItems.Deref(pars.Elements["/Size"]) is PdfInteger sz && sz.Value >= 0) size = sz.Value;
            size ??= file.Stream.Length;

            DateTime? modified = ParsePdfDate(TextOf(pars?.Elements["/ModDate"]) ?? TextOf(fs.Elements["/M"]) ?? TextOf(pars?.Elements["/CreationDate"]));

            return new AttachmentEntry
            {
                Name = name, Description = string.IsNullOrWhiteSpace(desc) ? null : desc, Size = size,
                Modified = modified, Source = source, StreamDict = file,
            };
        }

        private static string? NameOf(PdfItem? item) => PdfItems.Deref(item) is PdfName n ? n.Value : null;

        /// <summary>A PDF text string, with the UTF-16 byte-order-mark form decoded.</summary>
        private static string? TextOf(PdfItem? item)
        {
            if (PdfItems.Deref(item) is not PdfString s) return null;
            string v = s.Value;
            if (v.Length >= 2 && v[0] == 'þ' && v[1] == 'ÿ' && v.All(c => c <= 0xFF))
                return Encoding.BigEndianUnicode.GetString(Encoding.Latin1.GetBytes(v), 2, v.Length - 2);
            return v;
        }

        /// <summary>
        /// A PDF date (<c>D:YYYYMMDDHHmmSS+HH'mm'</c>, with every part after the year optional) as UTC,
        /// or null when it is not one. A missing offset reads as UTC.
        /// </summary>
        internal static DateTime? ParsePdfDate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string s = text.Trim();
            if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];
            int i = 0;
            int Take(int len, int fallback)
            {
                if (i + len > s.Length || !s.AsSpan(i, len).ToString().All(char.IsAsciiDigit)) return fallback;
                int v = int.Parse(s.AsSpan(i, len), NumberStyles.None, CultureInfo.InvariantCulture);
                i += len;
                return v;
            }
            int year = Take(4, -1);
            if (year < 1) return null;
            int month = Take(2, 1), day = Take(2, 1), hour = Take(2, 0), min = Take(2, 0), sec = Take(2, 0);
            try
            {
                var dt = new DateTime(year, month, day, hour, min, Math.Min(sec, 59), DateTimeKind.Utc);
                if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                {
                    int sign = s[i] == '-' ? -1 : 1;
                    i++;
                    int oh = Take(2, 0);
                    if (i < s.Length && s[i] == '\'') i++;
                    int om = Take(2, 0);
                    dt = dt.AddMinutes(-sign * (oh * 60 + om));
                }
                return dt;
            }
            catch (ArgumentOutOfRangeException) { return null; }
        }
    }
}
