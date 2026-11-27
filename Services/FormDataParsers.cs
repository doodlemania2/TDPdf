using System.IO;
using System.Globalization;
using System.Text;
using System.Xml;

namespace TDPdf.Services
{
    /// <summary>One field read from an FDF or XFDF file: its fully qualified name and its value.</summary>
    /// <param name="Name">Partial names joined with dots, root first.</param>
    /// <param name="Values">The values the file gives (more than one for a multi-select list); empty when it gives none.</param>
    /// <param name="ValueIsName">True when the FDF wrote the value as a PDF name (/Yes), which is how check boxes and radio buttons are exported.</param>
    internal sealed record ImportedField(string Name, IReadOnlyList<string> Values, bool ValueIsName = false)
    {
        /// <summary>The single value, or the values joined with a comma and space; null when there is none.</summary>
        public string? Value => Values.Count == 0 ? null : Values.Count == 1 ? Values[0] : string.Join(", ", Values);
    }

    /// <summary>
    /// Reads form data files: FDF (a small PDF-syntax file; many writers leave out the cross-reference
    /// table) and XFDF (XML). Both are hostile input, so every walk is bounded in size, depth and count,
    /// FDF is read by a tolerant tokenizer that never trusts offsets, and XML is read with DTD
    /// processing prohibited and no resolver. Pure, WPF-free, covered by tests/PdfCore.
    /// </summary>
    internal static class FormDataParsers
    {
        /// <summary>Largest file read (form data is small; this is far above anything real).</summary>
        internal const int MaxBytes = 32 * 1024 * 1024;
        /// <summary>Deepest field nesting followed.</summary>
        internal const int MaxDepth = 32;
        /// <summary>Most fields returned.</summary>
        internal const int MaxFields = 100_000;
        private const int MaxObjects = 200_000;
        private const int MaxValueDepth = 64;

        /// <summary>Reads FDF or XFDF, deciding by the first non-blank character. Throws <see cref="InvalidDataException"/> for anything else.</summary>
        internal static IReadOnlyList<ImportedField> Parse(byte[] data)
        {
            if (data.Length > MaxBytes) throw new InvalidDataException("The file is too large to be form data.");
            int i = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) i = 3;
            while (i < data.Length && (data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) i++;
            if (i < data.Length && data[i] == (byte)'<' && !StartsWith(data, i, "<<")) return ParseXfdf(data);
            if (data.Length >= 2 && (data[0] == 0xFF && data[1] == 0xFE || data[0] == 0xFE && data[1] == 0xFF)) return ParseXfdf(data);
            return ParseFdf(data);
        }

        private static bool StartsWith(byte[] d, int at, string s)
        {
            if (at + s.Length > d.Length) return false;
            for (int k = 0; k < s.Length; k++) if (d[at + k] != s[k]) return false;
            return true;
        }

        // ── XFDF ────────────────────────────────────────────────────────────────────────

        internal static IReadOnlyList<ImportedField> ParseXfdf(byte[] data)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,   // no entity expansion, no external subset
                XmlResolver = null,
                MaxCharactersInDocument = MaxBytes,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = false,
            };
            var result = new List<ImportedField>();
            try
            {
                using var stream = new MemoryStream(data, writable: false);
                using var reader = XmlReader.Create(stream, settings);

                var names = new List<string>();      // partial names of the open <field> elements
                var values = new Stack<List<string>>();
                var text = new StringBuilder();
                bool sawXfdf = false, inValue = false;
                int depth = 0;
                while (reader.Read())
                {
                    switch (reader.NodeType)
                    {
                        case XmlNodeType.Element:
                            if (++depth > MaxDepth * 4) throw new InvalidDataException("The form data is nested too deeply.");
                            string local = reader.LocalName;
                            bool empty = reader.IsEmptyElement;
                            if (local == "xfdf") sawXfdf = true;
                            else if (local == "field" && sawXfdf)
                            {
                                names.Add(reader.GetAttribute("name") ?? string.Empty);
                                values.Push([]);
                                if (names.Count > MaxDepth) throw new InvalidDataException("The form data is nested too deeply.");
                                if (empty) CloseField();
                            }
                            else if (local == "value" && values.Count > 0 && !inValue)
                            {
                                if (empty) values.Peek().Add(string.Empty);
                                else { inValue = true; text.Clear(); }
                            }
                            if (empty) depth--;
                            break;
                        case XmlNodeType.Text:
                        case XmlNodeType.CDATA:
                        case XmlNodeType.Whitespace:
                        case XmlNodeType.SignificantWhitespace:
                            if (inValue && text.Length < MaxBytes) text.Append(reader.Value);
                            break;
                        case XmlNodeType.EndElement:
                            depth--;
                            if (reader.LocalName == "value" && inValue) { values.Peek().Add(text.ToString()); inValue = false; }
                            else if (reader.LocalName == "field" && names.Count > 0) CloseField();
                            break;
                    }
                }
                if (!sawXfdf) throw new InvalidDataException("This is not an XFDF file.");

                void CloseField()
                {
                    var vals = values.Pop();
                    string full = string.Join(".", names.Where(n => n.Length > 0));
                    names.RemoveAt(names.Count - 1);
                    // A parent field with only child fields carries no value of its own.
                    if (vals.Count > 0 && full.Length > 0 && result.Count < MaxFields)
                        result.Add(new ImportedField(full, vals));
                }
            }
            catch (XmlException ex) { throw new InvalidDataException("The XFDF file is not valid XML: " + ex.Message, ex); }
            return result;
        }

        // ── FDF ─────────────────────────────────────────────────────────────────────────

        private abstract record Obj;
        private sealed record Dict(Dictionary<string, Obj> Map) : Obj;
        private sealed record Arr(List<Obj> Items) : Obj;
        private sealed record Str(string Text) : Obj;
        private sealed record Nm(string Name) : Obj;
        private sealed record Num(double Value) : Obj;
        private sealed record Ref(int Number) : Obj;
        private sealed record Other : Obj;

        /// <summary>
        /// Reads an FDF without relying on its cross-reference table or any offset: every
        /// <c>n g obj</c> in the file is parsed in turn, then the catalog (the object holding
        /// <c>/FDF</c>, or the trailer's <c>/Root</c>) is followed down <c>/Fields</c> and <c>/Kids</c>.
        /// </summary>
        internal static IReadOnlyList<ImportedField> ParseFdf(byte[] data)
        {
            var p = new Lexer(data);
            var objects = new Dictionary<int, Obj>();
            Dict? trailer = null;

            while (p.SkipToKeywordStart())
            {
                int save = p.Pos;
                if (p.TryReadObjectHeader(out int num))
                {
                    if (objects.Count >= MaxObjects) break;
                    var body = p.ReadValue(0);
                    if (body is not null && !objects.ContainsKey(num)) objects[num] = body;
                    continue;
                }
                p.Pos = save;
                if (p.TryReadWord("trailer"))
                {
                    if (p.ReadValue(0) is Dict t) trailer ??= t;
                    continue;
                }
                p.Pos = save + 1;
            }

            Dict? catalog = null;
            if (trailer is not null && trailer.Map.TryGetValue("/Root", out var root))
                catalog = Resolve(root, objects) as Dict;
            if (catalog is null || !catalog.Map.ContainsKey("/FDF"))
                catalog = objects.Values.OfType<Dict>().FirstOrDefault(d => d.Map.ContainsKey("/FDF")) ?? catalog;

            Obj? fdf = catalog is not null && catalog.Map.TryGetValue("/FDF", out var f) ? Resolve(f, objects) : null;

            // Some writers leave out the object wrapper altogether: "/FDF << /Fields [...] >>" inline.
            if (fdf is not Dict)
            {
                int at = p.IndexOf("/FDF");
                if (at >= 0)
                {
                    p.Pos = at + 4;
                    fdf = p.ReadValue(0);
                }
            }
            if (fdf is not Dict fdfDict || !fdfDict.Map.TryGetValue("/Fields", out var fieldsObj) || Resolve(fieldsObj, objects) is not Arr)
                throw new InvalidDataException("This is not an FDF file, or it has no form fields.");

            var result = new List<ImportedField>();
            var visited = new HashSet<int>();
            Walk(fieldsObj, string.Empty, 0, objects, visited, result);
            return result;
        }

        private static Obj? Resolve(Obj? o, Dictionary<int, Obj> objects, int hops = 0)
        {
            while (o is Ref r && hops++ < 16) o = objects.TryGetValue(r.Number, out var t) ? t : null;
            return o is Ref ? null : o;
        }

        private static void Walk(Obj list, string prefix, int depth, Dictionary<int, Obj> objects, HashSet<int> visited, List<ImportedField> result)
        {
            if (depth > MaxDepth || Resolve(list, objects) is not Arr arr) return;
            foreach (var item in arr.Items)
            {
                if (result.Count >= MaxFields) return;
                if (item is Ref r && !visited.Add(r.Number)) continue;   // a reference loop ends here
                if (Resolve(item, objects) is not Dict d) continue;

                string partial = d.Map.TryGetValue("/T", out var t) && Resolve(t, objects) is Str ts ? ts.Text : string.Empty;
                string full = prefix.Length == 0 ? partial : partial.Length == 0 ? prefix : prefix + "." + partial;

                if (d.Map.TryGetValue("/V", out var v) && full.Length > 0)
                {
                    var rv = Resolve(v, objects);
                    switch (rv)
                    {
                        case Str s: result.Add(new ImportedField(full, [s.Text])); break;
                        case Nm n: result.Add(new ImportedField(full, [n.Name], ValueIsName: true)); break;
                        case Num num: result.Add(new ImportedField(full, [num.Value.ToString("R", CultureInfo.InvariantCulture)])); break;
                        case Arr a:
                            var vals = new List<string>();
                            foreach (var e in a.Items)
                                switch (Resolve(e, objects)) { case Str es: vals.Add(es.Text); break; case Nm en: vals.Add(en.Name); break; }
                            result.Add(new ImportedField(full, vals));
                            break;
                    }
                }
                if (d.Map.TryGetValue("/Kids", out var kids)) Walk(kids, full, depth + 1, objects, visited, result);
            }
        }

        /// <summary>A tolerant tokenizer over PDF syntax. It never throws on malformed input: it gives up on the value instead.</summary>
        private sealed class Lexer(byte[] d)
        {
            private readonly byte[] _d = d;
            public int Pos;

            private static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
            private static bool IsDelim(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

            private void SkipWhite()
            {
                while (Pos < _d.Length)
                {
                    if (IsWhite(_d[Pos])) Pos++;
                    else if (_d[Pos] == (byte)'%') { while (Pos < _d.Length && _d[Pos] != 10 && _d[Pos] != 13) Pos++; }
                    else break;
                }
            }

            /// <summary>Moves to the next place a keyword could start; false at the end of the data.</summary>
            public bool SkipToKeywordStart()
            {
                SkipWhite();
                return Pos < _d.Length;
            }

            public int IndexOf(string s)
            {
                for (int i = 0; i + s.Length <= _d.Length; i++)
                    if (StartsWithAt(i, s)) return i;
                return -1;
            }

            private bool StartsWithAt(int at, string s)
            {
                if (at + s.Length > _d.Length) return false;
                for (int k = 0; k < s.Length; k++) if (_d[at + k] != s[k]) return false;
                return true;
            }

            public bool TryReadWord(string w)
            {
                SkipWhite();
                if (!StartsWithAt(Pos, w)) return false;
                int end = Pos + w.Length;
                if (end < _d.Length && !IsWhite(_d[end]) && !IsDelim(_d[end])) return false;
                Pos = end;
                return true;
            }

            private bool TryReadInt(out int value)
            {
                SkipWhite();
                int start = Pos;
                long v = 0;
                while (Pos < _d.Length && _d[Pos] is >= (byte)'0' and <= (byte)'9' && Pos - start < 10) { v = v * 10 + (_d[Pos] - '0'); Pos++; }
                value = (int)Math.Min(v, int.MaxValue);
                if (Pos == start || (Pos < _d.Length && !IsWhite(_d[Pos]) && !IsDelim(_d[Pos]))) { Pos = start; return false; }
                return true;
            }

            /// <summary>Reads "n g obj"; leaves the position after "obj".</summary>
            public bool TryReadObjectHeader(out int number)
            {
                int save = Pos;
                number = 0;
                if (TryReadInt(out int n) && TryReadInt(out _) && TryReadWord("obj")) { number = n; return true; }
                Pos = save;
                return false;
            }

            public Obj? ReadValue(int depth)
            {
                if (depth > MaxValueDepth) return null;
                SkipWhite();
                if (Pos >= _d.Length) return null;
                byte b = _d[Pos];

                if (b == (byte)'<')
                {
                    if (Pos + 1 < _d.Length && _d[Pos + 1] == (byte)'<') { Pos += 2; return ReadDict(depth); }
                    return ReadHex();
                }
                if (b == (byte)'[')
                {
                    Pos++;
                    var items = new List<Obj>();
                    while (true)
                    {
                        SkipWhite();
                        if (Pos >= _d.Length) return new Arr(items);
                        if (_d[Pos] == (byte)']') { Pos++; return new Arr(items); }
                        int before = Pos;
                        var v = ReadValue(depth + 1);
                        if (v is not null) items.Add(v);
                        if (Pos == before) Pos++;   // never stall on a byte that is no value
                        if (items.Count > MaxFields * 2) return new Arr(items);
                    }
                }
                if (b == (byte)'(') return ReadLiteral();
                if (b == (byte)'/') return new Nm(ReadName());

                // number, reference, keyword
                int start = Pos;
                while (Pos < _d.Length && !IsWhite(_d[Pos]) && !IsDelim(_d[Pos])) Pos++;
                if (Pos == start) { Pos++; return null; }
                string tok = Encoding.ASCII.GetString(_d, start, Pos - start);
                if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                {
                    if (int.TryParse(tok, NumberStyles.None, CultureInfo.InvariantCulture, out int n1))
                    {
                        // "n g R"
                        int save = Pos;
                        if (TryReadInt(out _) && TryReadWord("R")) return new Ref(n1);
                        Pos = save;
                    }
                    return new Num(num);
                }
                return new Other();
            }

            private Dict ReadDict(int depth)
            {
                var map = new Dictionary<string, Obj>();
                while (true)
                {
                    SkipWhite();
                    if (Pos >= _d.Length) return new Dict(map);
                    if (_d[Pos] == (byte)'>' && Pos + 1 < _d.Length && _d[Pos + 1] == (byte)'>') { Pos += 2; return new Dict(map); }
                    if (_d[Pos] != (byte)'/')
                    {
                        // A stray token inside a dictionary: skip a value, keep going.
                        int before = Pos;
                        ReadValue(depth + 1);
                        if (Pos == before) Pos++;
                        continue;
                    }
                    string key = ReadName();
                    int p0 = Pos;
                    var value = ReadValue(depth + 1);
                    if (Pos == p0) Pos++;
                    if (value is not null && !map.ContainsKey(key)) map[key] = value;
                    if (map.Count > MaxFields) return new Dict(map);
                }
            }

            private string ReadName()
            {
                Pos++;   // the slash
                var sb = new StringBuilder();
                var raw = new List<byte>();
                while (Pos < _d.Length && !IsWhite(_d[Pos]) && !IsDelim(_d[Pos]))
                {
                    if (_d[Pos] == (byte)'#' && Pos + 2 < _d.Length && Uri.IsHexDigit((char)_d[Pos + 1]) && Uri.IsHexDigit((char)_d[Pos + 2]))
                    {
                        raw.Add(Convert.ToByte(Encoding.ASCII.GetString(_d, Pos + 1, 2), 16));
                        Pos += 3;
                    }
                    else raw.Add(_d[Pos++]);
                    if (raw.Count > 4096) break;
                }
                sb.Append('/').Append(Encoding.UTF8.GetString([.. raw]));
                return sb.ToString();
            }

            private Obj ReadHex()
            {
                Pos++;   // '<'
                var bytes = new List<byte>();
                int hi = -1;
                while (Pos < _d.Length && _d[Pos] != (byte)'>')
                {
                    char c = (char)_d[Pos++];
                    if (!Uri.IsHexDigit(c)) continue;
                    int v = Convert.ToInt32(c.ToString(), 16);
                    if (hi < 0) hi = v; else { bytes.Add((byte)(hi * 16 + v)); hi = -1; }
                }
                if (hi >= 0) bytes.Add((byte)(hi * 16));
                if (Pos < _d.Length) Pos++;
                return new Str(TextString([.. bytes]));
            }

            private Obj ReadLiteral()
            {
                Pos++;   // '('
                var bytes = new List<byte>();
                int nest = 1;
                while (Pos < _d.Length)
                {
                    byte c = _d[Pos++];
                    if (c == (byte)'\\' && Pos < _d.Length)
                    {
                        byte e = _d[Pos++];
                        switch (e)
                        {
                            case (byte)'n': bytes.Add(10); break;
                            case (byte)'r': bytes.Add(13); break;
                            case (byte)'t': bytes.Add(9); break;
                            case (byte)'b': bytes.Add(8); break;
                            case (byte)'f': bytes.Add(12); break;
                            case 13: if (Pos < _d.Length && _d[Pos] == 10) Pos++; break;   // line continuation
                            case 10: break;
                            case >= (byte)'0' and <= (byte)'7':
                                int oct = e - '0';
                                for (int k = 0; k < 2 && Pos < _d.Length && _d[Pos] is >= (byte)'0' and <= (byte)'7'; k++) oct = oct * 8 + (_d[Pos++] - '0');
                                bytes.Add((byte)oct);
                                break;
                            default: bytes.Add(e); break;
                        }
                        continue;
                    }
                    if (c == (byte)'(') nest++;
                    else if (c == (byte)')' && --nest == 0) break;
                    bytes.Add(c);
                    if (bytes.Count > MaxBytes) break;
                }
                return new Str(TextString([.. bytes]));
            }

            /// <summary>A PDF text string: UTF-16 with a byte-order mark, UTF-8 with one, else Latin-1 (PDFDocEncoding for the usual range).</summary>
            private static string TextString(byte[] b)
            {
                if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
                if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
                if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
                return Encoding.Latin1.GetString(b);
            }
        }
    }
}
