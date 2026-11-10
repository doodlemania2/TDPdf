using System.IO;
using System.Text;

namespace TDPdf.Services
{
    /// <summary>
    /// Finds out whether a PDF file is encrypted without parsing it.
    /// </summary>
    /// <remarks>
    /// The signal is an <c>/Encrypt</c> key in a trailer dictionary or a cross-reference stream
    /// dictionary. The old check read only the last 2 KB, which missed two real layouts (upstream
    /// KillerPDF 1.9.2: protection is recognized wherever the file keeps its encryption entry):
    /// <list type="bullet">
    /// <item>a LINEARIZED file keeps its first-page trailer at the start of the file, and its
    /// closing trailer carries only /Prev;</item>
    /// <item>a file whose cross-reference is a STREAM keeps /Encrypt in the stream's dictionary,
    /// which sits before the stream data, so a large table pushes it out of the last 2 KB.</item>
    /// </list>
    /// So three windows are read: the head, the tail, and the dictionary at the offset
    /// <c>startxref</c> names. A hit must be the whole key, not a prefix, so <c>/EncryptMetadata</c>
    /// and a name like <c>/EncryptedPayload</c> do not count. Latin-1 so no code-page provider is
    /// needed; the marker is pure ASCII.
    /// </remarks>
    internal static class PdfEncryptionScan
    {
        private const int HeadBytes = 64 * 1024;
        private const int TailBytes = 64 * 1024;
        private const int XrefDictBytes = 16 * 1024;

        internal static bool FileHasEncryption(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                return StreamHasEncryption(fs);
            }
            catch { return false; }
        }

        internal static bool StreamHasEncryption(Stream fs)
        {
            long length = fs.Length;
            if (length == 0) return false;

            byte[] tail = ReadWindow(fs, Math.Max(0, length - TailBytes), (int)Math.Min(TailBytes, length));
            string tailText = Encoding.Latin1.GetString(tail);
            if (ContainsEncryptKey(tailText)) return true;

            byte[] head = ReadWindow(fs, 0, (int)Math.Min(HeadBytes, length));
            if (ContainsEncryptKey(Encoding.Latin1.GetString(head))) return true;

            // The cross-reference stream's dictionary, wherever startxref points. In a non-linearized
            // file with a large xref stream this is the only place /Encrypt is within reach.
            long xref = LastStartXref(tailText);
            if (xref > 0 && xref < length)
            {
                byte[] dict = ReadWindow(fs, xref, (int)Math.Min(XrefDictBytes, length - xref));
                if (ContainsEncryptKey(Encoding.Latin1.GetString(dict))) return true;
            }
            return false;
        }

        private static byte[] ReadWindow(Stream fs, long offset, int count)
        {
            var buf = new byte[count];
            fs.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < count)
            {
                int n = fs.Read(buf, read, count - read);
                if (n <= 0) break;
                read += n;
            }
            return read == count ? buf : buf.AsSpan(0, read).ToArray();
        }

        private static long LastStartXref(string tail)
        {
            int i = tail.LastIndexOf("startxref", StringComparison.Ordinal);
            if (i < 0) return -1;
            i += "startxref".Length;
            while (i < tail.Length && char.IsWhiteSpace(tail[i])) i++;
            int start = i;
            while (i < tail.Length && tail[i] >= '0' && tail[i] <= '9') i++;
            return i > start && long.TryParse(tail.AsSpan(start, i - start), out long v) ? v : -1;
        }

        /// <summary>True if <paramref name="text"/> holds the key <c>/Encrypt</c> as a complete name.</summary>
        internal static bool ContainsEncryptKey(string text)
        {
            const string key = "/Encrypt";
            int from = 0;
            while (true)
            {
                int i = text.IndexOf(key, from, StringComparison.Ordinal);
                if (i < 0) return false;
                int end = i + key.Length;
                // A PDF name ends at whitespace or a delimiter; anything else continues it.
                if (end >= text.Length || IsNameTerminator(text[end])) return true;
                from = end;
            }
        }

        private static bool IsNameTerminator(char c) =>
            char.IsWhiteSpace(c) || c == '\0' || c == '/' || c == '<' || c == '>' || c == '[' || c == ']' ||
            c == '(' || c == ')' || c == '%';
    }
}
