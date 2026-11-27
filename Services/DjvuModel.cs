using System.IO;
using System.Text;

namespace TDPdf.Services
{
    internal enum DjvuPageKind { Bitonal, Gray, Color }

    /// <summary>A page's size in pixels at its own resolution, and that resolution.</summary>
    internal readonly record struct DjvuPageInfo(int Width, int Height, int Dpi);

    /// <summary>
    /// One rendered page. <see cref="Data"/> is raw samples: for <see cref="DjvuPageKind.Bitonal"/> packed
    /// rows (each starting on a byte, most significant bit first) where 1 is black, as in a PBM file; for
    /// Gray one byte per pixel; for Color three (RGB). The size may be smaller than the page's own when the
    /// page was too large to render whole.
    /// </summary>
    internal sealed record DjvuRaster(DjvuPageKind Kind, int Width, int Height, byte[] Data);

    /// <summary>A word of the hidden text layer, in pixels from the top-left of the page at its own size.</summary>
    internal readonly record struct DjvuWord(string Text, int Left, int Top, int Right, int Bottom);

    internal sealed class DjvuOutlineItem
    {
        public string Title { get; init; } = "";
        /// <summary>0-based page, or null when the bookmark points somewhere TDPdf cannot follow.</summary>
        public int? PageIndex { get; init; }
        public List<DjvuOutlineItem> Children { get; } = new();
    }

    /// <summary>
    /// What reading a DjVu book needs from DjVuLibre. The real one runs DjVuLibre's own programs as
    /// separate processes (<see cref="DjvuLibreDecoder"/>); tests use a fake, so the page, text and
    /// outline pipeline is pinned without any native code.
    /// </summary>
    internal interface IDjvuDecoder : IDisposable
    {
        int PageCount { get; }
        DjvuPageInfo GetPageInfo(int pageIndex);

        /// <summary>Renders a page, at most <paramref name="maxPixels"/> pixels (smaller pages at their own size).</summary>
        DjvuRaster Render(int pageIndex, long maxPixels, CancellationToken ct);

        /// <summary>The page's hidden text, one entry per word (or per line or block when the book has no word boxes).</summary>
        IReadOnlyList<DjvuWord> GetWords(int pageIndex);

        IReadOnlyList<DjvuOutlineItem> GetOutline();
    }

    /// <summary>The PNM (PBM, PGM, PPM) files DjVuLibre's ddjvu writes.</summary>
    internal static class DjvuPnm
    {
        internal const long MaxPixels = 400_000_000;

        /// <summary>Parses a binary PBM (P4), PGM (P5) or PPM (P6) with 8-bit samples. Throws <see cref="InvalidDataException"/>.</summary>
        internal static DjvuRaster Parse(byte[] data)
        {
            int pos = 0;
            if (data.Length < 3 || data[0] != (byte)'P') throw new InvalidDataException("not a PNM image");
            char magic = (char)data[1];
            if (magic is not ('4' or '5' or '6')) throw new InvalidDataException("unsupported PNM type");
            pos = 2;

            int ReadInt()
            {
                // Skip whitespace and comments, then read digits.
                while (pos < data.Length)
                {
                    if (data[pos] == (byte)'#') { while (pos < data.Length && data[pos] != '\n') pos++; }
                    else if (data[pos] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') pos++;
                    else break;
                }
                long v = 0; int digits = 0;
                while (pos < data.Length && data[pos] >= '0' && data[pos] <= '9')
                {
                    v = v * 10 + (data[pos] - '0');
                    if (v > int.MaxValue) throw new InvalidDataException("PNM header is out of range");
                    pos++; digits++;
                }
                if (digits == 0) throw new InvalidDataException("bad PNM header");
                return (int)v;
            }

            int w = ReadInt(), h = ReadInt();
            if (magic != '4')
            {
                int max = ReadInt();
                if (max != 255) throw new InvalidDataException("only 8-bit PNM samples are supported");
            }
            pos++;   // the single whitespace byte before the pixels
            if (w <= 0 || h <= 0 || (long)w * h > MaxPixels) throw new InvalidDataException("PNM size is out of range");

            long expected = magic switch
            {
                '4' => (long)((w + 7) / 8) * h,
                '5' => (long)w * h,
                _ => (long)w * h * 3,
            };
            if (data.Length - pos < expected) throw new InvalidDataException("PNM image is truncated");
            var samples = new byte[expected];
            Buffer.BlockCopy(data, pos, samples, 0, (int)expected);
            var kind = magic switch { '4' => DjvuPageKind.Bitonal, '5' => DjvuPageKind.Gray, _ => DjvuPageKind.Color };
            return new DjvuRaster(kind, w, h, samples);
        }
    }

    /// <summary>
    /// The s-expressions and command listings DjVuLibre's djvused prints: hidden text, outline, page list
    /// and the file structure dump. Pure text in, values out, so every shape is pinned by a test.
    /// </summary>
    internal static class DjvuText
    {
        // ── s-expressions ──────────────────────────────────────────────────────────────────────

        internal sealed class Sx
        {
            public string? Atom;
            public bool IsString;
            public List<Sx>? Items;
            public bool IsList => Items is not null;
        }

        private const int MaxDepth = 64;
        private const int MaxNodes = 5_000_000;

        /// <summary>Parses the first s-expression in <paramref name="text"/>; null when there is none or it is malformed.</summary>
        internal static Sx? ParseSexp(string text)
        {
            int i = 0, nodes = 0;
            SkipWs(text, ref i);
            if (i >= text.Length || text[i] != '(') return null;
            return ParseNode(text, ref i, 0, ref nodes);
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]))) i++;
        }

        private static Sx? ParseNode(string s, ref int i, int depth, ref int nodes)
        {
            if (depth > MaxDepth || ++nodes > MaxNodes) return null;
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            if (s[i] == '(')
            {
                i++;
                var list = new Sx { Items = new List<Sx>() };
                while (true)
                {
                    SkipWs(s, ref i);
                    if (i >= s.Length) return null;
                    if (s[i] == ')') { i++; return list; }
                    var child = ParseNode(s, ref i, depth + 1, ref nodes);
                    if (child is null) return null;
                    list.Items.Add(child);
                }
            }
            if (s[i] == '"')
            {
                i++;
                var bytes = new List<byte>();
                while (i < s.Length && s[i] != '"')
                {
                    char c = s[i++];
                    if (c != '\\') { AddChar(bytes, c); continue; }
                    if (i >= s.Length) return null;
                    char e = s[i++];
                    if (e >= '0' && e <= '7')
                    {
                        int v = e - '0';
                        for (int k = 0; k < 2 && i < s.Length && s[i] >= '0' && s[i] <= '7'; k++) v = v * 8 + (s[i++] - '0');
                        bytes.Add((byte)v);
                    }
                    else switch (e)
                    {
                        case 'n': bytes.Add((byte)'\n'); break;
                        case 'r': bytes.Add((byte)'\r'); break;
                        case 't': bytes.Add((byte)'\t'); break;
                        case 'b': bytes.Add(8); break;
                        case 'f': bytes.Add(12); break;
                        case 'v': bytes.Add(11); break;
                        case 'a': bytes.Add(7); break;
                        default: AddChar(bytes, e); break;
                    }
                }
                if (i >= s.Length) return null;
                i++;   // closing quote
                return new Sx { Atom = Encoding.UTF8.GetString(bytes.ToArray()), IsString = true };
            }
            int start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '(' && s[i] != ')' && s[i] != '"') i++;
            if (i == start) return null;
            return new Sx { Atom = s[start..i] };
        }

        // The tool's output is read as Latin-1 so that every byte survives; a character above 255 cannot
        // come from it, and one that does anyway is written as UTF-8.
        private static void AddChar(List<byte> bytes, char c)
        {
            if (c < 256) bytes.Add((byte)c);
            else bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
        }

        // ── hidden text ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The words of one page's text s-expression, in pixels from the top-left. DjVu boxes are measured
        /// from the bottom-left, so they are flipped using the page's own height. A zone with no word zones
        /// under it (a book whose text was only zoned down to lines or the whole page) is returned whole.
        /// </summary>
        internal static List<DjvuWord> ParsePageText(string sexp, int pageHeight)
        {
            var words = new List<DjvuWord>();
            var root = ParseSexp(sexp);
            if (root?.Items is null) return words;
            Collect(root, pageHeight, words, 0);
            return words;
        }

        private static void Collect(Sx zone, int pageHeight, List<DjvuWord> into, int depth)
        {
            var it = zone.Items;
            if (it is null || it.Count < 5 || depth > MaxDepth) return;
            string kind = it[0].Atom ?? "";
            if (!int.TryParse(it[1].Atom, out int x1) || !int.TryParse(it[2].Atom, out int y1) ||
                !int.TryParse(it[3].Atom, out int x2) || !int.TryParse(it[4].Atom, out int y2)) return;

            string? text = null;
            int before = into.Count;
            for (int k = 5; k < it.Count; k++)
            {
                var child = it[k];
                if (child.IsList)
                {
                    if (kind == "word" || kind == "char") continue;   // a word's own letters add nothing
                    Collect(child, pageHeight, into, depth + 1);
                }
                else if (child.IsString) text ??= child.Atom;
            }

            bool leaf = kind == "word" || into.Count == before;
            if (leaf && !string.IsNullOrWhiteSpace(text))
            {
                int left = Math.Min(x1, x2), right = Math.Max(x1, x2);
                int bottom = pageHeight - Math.Min(y1, y2), top = pageHeight - Math.Max(y1, y2);
                if (right > left && bottom > top) into.Add(new DjvuWord(text.Trim(), left, top, right, bottom));
            }
        }

        /// <summary>
        /// Splits <c>djvused -e output-txt</c> into per-page s-expressions. <paramref name="pageOfId"/> maps a
        /// page's file name to its 0-based index; a "# page N" comment or a plain number is used first.
        /// </summary>
        internal static Dictionary<int, string> ParseOutputTxt(string output, Func<string, int?> pageOfId)
        {
            var result = new Dictionary<int, string>();
            int? current = null;
            bool inText = false;
            var buffer = new StringBuilder();
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (inText)
                {
                    if (line.Trim() == ".")
                    {
                        inText = false;
                        if (current is int p && buffer.Length > 0) result[p] = buffer.ToString();
                        buffer.Clear();
                    }
                    else buffer.Append(line).Append('\n');
                    continue;
                }
                string t = line.Trim();
                if (t.StartsWith("select", StringComparison.Ordinal))
                {
                    current = null;
                    var m = System.Text.RegularExpressions.Regex.Match(t,
                        "^select\\s+(?:\"((?:[^\"\\\\]|\\\\.)*)\"|(\\S+))\\s*(?:#\\s*page\\s+(\\d+))?");
                    if (!m.Success) continue;
                    if (m.Groups[3].Success && int.TryParse(m.Groups[3].Value, out int pn) && pn >= 1) current = pn - 1;
                    else if (m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out int num) && num >= 1) current = num - 1;
                    else if (m.Groups[1].Success) current = pageOfId(m.Groups[1].Value);
                }
                else if (t == "set-txt") { inText = true; buffer.Clear(); }
            }
            return result;
        }

        // ── outline ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The bookmark tree of <c>djvused -e print-outline</c>. A destination is "#N" (page N), "#name" (a
        /// page's file name) or a relative or external one, which gives a bookmark with no page.
        /// </summary>
        internal static List<DjvuOutlineItem> ParseOutline(string sexp, Func<string, int?> pageOfId, int pageCount)
        {
            var result = new List<DjvuOutlineItem>();
            var root = ParseSexp(sexp);
            if (root?.Items is null || root.Items.Count == 0 || root.Items[0].Atom != "bookmarks") return result;
            int remaining = 20000;
            void Read(IEnumerable<Sx> items, List<DjvuOutlineItem> into, int depth)
            {
                foreach (var item in items)
                {
                    if (remaining-- <= 0 || depth > 12) return;
                    var parts = item.Items;
                    if (parts is null || parts.Count < 1 || !parts[0].IsString) continue;
                    int? page = null;
                    if (parts.Count > 1 && parts[1].IsString && parts[1].Atom is { Length: > 1 } dest && dest[0] == '#')
                    {
                        string d = dest[1..];
                        if (int.TryParse(d, out int n) && d[0] != '+' && d[0] != '-') page = n >= 1 && n <= pageCount ? n - 1 : null;
                        else if (d[0] != '+' && d[0] != '-') page = pageOfId(d);
                    }
                    var node = new DjvuOutlineItem { Title = parts[0].Atom ?? "", PageIndex = page };
                    Read(parts.Skip(parts.Count > 1 && parts[1].IsString ? 2 : 1).Where(p => p.IsList), node.Children, depth + 1);
                    into.Add(node);
                }
            }
            Read(root.Items.Skip(1).Where(p => p.IsList), result, 0);
            return result;
        }

        // ── listings ───────────────────────────────────────────────────────────────────────────

        private static readonly System.Text.RegularExpressions.Regex InfoLine = new(
            @"^\s*INFO\s*\[\d+\]\s+DjVu\s+(\d+)x(\d+),\s*v\d+,\s*(\d+)\s*dpi",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);

        /// <summary>The size and resolution of each page, in order, from <c>djvused -e dump</c>.</summary>
        internal static List<DjvuPageInfo> ParseDump(string dump)
        {
            var pages = new List<DjvuPageInfo>();
            foreach (System.Text.RegularExpressions.Match m in InfoLine.Matches(dump))
            {
                if (int.TryParse(m.Groups[1].Value, out int w) && int.TryParse(m.Groups[2].Value, out int h) &&
                    int.TryParse(m.Groups[3].Value, out int dpi))
                    pages.Add(new DjvuPageInfo(w, h, dpi));
            }
            return pages;
        }

        /// <summary>Page file name -> 0-based page, from <c>djvused -e ls</c> ("  1 P  90  a.djvu").</summary>
        internal static Dictionary<string, int> ParseLs(string ls)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         ls, @"^\s*(\d+)\s+P\s+\d+\s+(\S+)", System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                if (int.TryParse(m.Groups[1].Value, out int n) && n >= 1) map.TryAdd(m.Groups[2].Value, n - 1);
            }
            return map;
        }
    }
}
