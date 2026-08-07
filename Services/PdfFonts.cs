using System.IO;
using PdfSharpCore.Drawing;
using PdfSharpCore.Fonts;
using SixLabors.Fonts;

namespace TDPdf.Services
{
    // ============================================================
    // Font resolution for the SAVE path (upstream KillerPDF #168).
    //
    // The on-screen editor is a WPF TextBox: it falls back per CHARACTER across
    // every installed font, so anything typed looks right while it is being
    // typed. The save path is PdfSharpCore, which resolves exactly ONE face and
    // emits .notdef - an empty box - for every codepoint that face lacks. So
    // Japanese, Chinese, Korean, Indic and Thai text rendered correctly on
    // screen and then saved as a row of boxes, silently.
    //
    // Three things had to change, and all three live outside third_party/:
    //
    //  1. The stock vendored resolver (third_party/PdfSharpCore/Utils/
    //     FontResolver.cs) enumerates "*.ttf" ONLY. On Windows nearly every CJK
    //     family ships as a TrueType Collection (.ttc): Yu Gothic, MS Gothic,
    //     Meiryo, Microsoft YaHei, JhengHei, SimSun, MingLiU. Those were never
    //     even seen. This resolver indexes *.ttf, *.ttc AND *.otf.
    //
    //  2. The vendored parser rejects collection bytes outright -
    //         OpenTypeFontface.Read(): if (startTag == TTCF) throw ...
    //             "TrueType collection fonts are not yet supported by PdfSharpCore."
    //     - so a collection has to be split BEFORE the engine sees it.
    //     ExtractTtcFace below rebuilds one face as a standalone sfnt, which is
    //     why nothing in third_party/ needed patching: no 'ttcf' tag ever
    //     reaches it.
    //
    //  3. The stock resolver matches on family name alone and, on a miss, hands
    //     back whatever font happened to be indexed first - with no check that
    //     it covers the text. Services/FontCoverage.cs adds that check and the
    //     per-script fallback chain; this file supplies it the face bytes.
    //
    // NOTE ON FILE SIZE: embedded fonts are SUBSET (PdfTrueTypeFont / PdfCIDFont
    // call CreateFontSubSet), so a few Japanese characters cost tens of KB in the
    // saved file, not megabytes. The exception is faces with no 'loca' table -
    // CFF outlines, i.e. .otf - which PdfCIDFont embeds WHOLE. That is why .otf
    // is indexed last and only wins when nothing else covers the text.
    //
    // SAFETY: this type sits on the critical path of EVERY save. An exception
    // escaping ResolveTypeface or GetFont surfaces at DrawString time and takes
    // the whole document down, not just its non-Latin text. So every method here
    // swallows its own failures, a missing or unreadable font directory is simply
    // skipped, and both interface methods fall back to something drawable rather
    // than to null - PdfSharpCore turns a null from either one into a throw
    // (XGlyphTypeface: "No appropriate font found."; XFontSource.GetOrCreateFrom
    // dereferences the byte[] immediately).
    // ============================================================
    internal sealed class TdpFontResolver : IFontResolver
    {
        /// <summary>Only consulted by PdfSharpCore's barcode renderer (unused here); kept in step
        /// with <see cref="PdfFontStyle.DefaultFamily"/> so the app has one default family.</summary>
        public string DefaultFontName => PdfFontStyle.DefaultFamily;

        // faceKey -> the physical face. faceKey is what we hand PdfSharpCore in FontResolverInfo
        // and get back in GetFont, so it only has to be unique and stable within one run.
        private static readonly Dictionary<string, FaceFile> Faces = new(StringComparer.OrdinalIgnoreCase);

        // invariant family name -> style -> faceKey.
        private static readonly Dictionary<string, Dictionary<XFontStyle, string>> Families =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly object Gate = new();
        private static bool _indexed;

        private readonly record struct FaceFile(string FilePath, int FaceIndex);

        /// <summary>
        /// Installs this resolver process-wide. Call ONCE at startup, before any <see cref="XFont"/>
        /// exists anywhere in the process: PdfSharpCore caches the resolver on first use and
        /// <c>GlobalFontSettings.FontResolver</c> throws "Must not change font resolver after is was
        /// once used" on a later swap. Indexing is lazy, so this costs nothing at startup and is
        /// safe on the headless install / CLI paths that never draw a glyph.
        /// </summary>
        internal static void Install()
        {
            // A failure here is not fatal: PdfSharpCore falls back to its own stock resolver, which
            // still handles Latin text exactly as it did before this landed.
            try { GlobalFontSettings.FontResolver = new TdpFontResolver(); }
            catch { /* already in use, or a resolver is already installed */ }
        }

        /// <summary>
        /// Best-effort <see cref="XFont"/>: the requested family, then the app default, then the
        /// stock Windows faces. Null when nothing can be resolved at all or the size is not a usable
        /// number - which lets a burn site skip ONE annotation instead of failing the whole save.
        /// </summary>
        internal static XFont? TryCreate(string family, double emSize, XFontStyle style)
        {
            if (double.IsNaN(emSize) || double.IsInfinity(emSize) || emSize <= 0) return null;
            foreach (string candidate in new[] { family, PdfFontStyle.DefaultFamily, "Arial", "Tahoma" })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try { return new XFont(candidate, emSize, style); }
                catch { /* not resolvable / not parseable - try the next one */ }
            }
            return null;
        }

        // ── Index ─────────────────────────────────────────────────────────────────────────────

        private static void EnsureIndexed()
        {
            lock (Gate)
            {
                if (_indexed) return;
                _indexed = true;   // set first: a failed scan must not be retried on every glyph
                try { ScanFontDirectories(); }
                catch { /* keep whatever was indexed before the failure */ }
            }
        }

        private static void ScanFontDirectories()
        {
            // Pattern outer, directory inner: AddFace keeps the FIRST face registered for a
            // (family, style), so this loop order IS the preference order globally rather than
            // per directory. .otf comes last because CFF faces embed unsubsetted (see the header).
            foreach (string pattern in new[] { "*.ttf", "*.ttc", "*.otf" })
                foreach (string dir in FontDirectories())
                    foreach (string file in SafeEnumerateFiles(dir, pattern))
                        IndexFile(file);
        }

        private static IEnumerable<string> FontDirectories()
        {
            var dirs = new List<string>();
            void Add(string raw)
            {
                try
                {
                    string p = Environment.ExpandEnvironmentVariables(raw);
                    // Existence is checked here rather than trusted: the stock resolver calls
                    // Directory.GetFiles on %SystemRoot%\Fonts unconditionally, which throws
                    // outright if the variable is unset or the folder is missing.
                    if (p.Length > 0 && Directory.Exists(p)) dirs.Add(p);
                }
                catch { /* unexpandable / unreachable path - just skip it */ }
            }
            Add(@"%SystemRoot%\Fonts");
            // Fonts installed without admin rights ("Install for me only") live here.
            Add(@"%LOCALAPPDATA%\Microsoft\Windows\Fonts");
            return dirs;
        }

        /// <summary>
        /// Enumerates font files without ever throwing. Streaming rather than Directory.GetFiles so
        /// that an unreadable subdirectory partway through costs only the files after it - GetFiles
        /// is all-or-nothing and would drop the entire directory.
        /// </summary>
        private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern)
        {
            IEnumerator<string> walk;
            try { walk = Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).GetEnumerator(); }
            catch { yield break; }

            using (walk)
            {
                while (true)
                {
                    string current;
                    try
                    {
                        if (!walk.MoveNext()) break;
                        current = walk.Current;
                    }
                    catch { break; }   // access denied / path too long: keep what was found so far
                    yield return current;
                }
            }
        }

        private static void IndexFile(string path)
        {
            try
            {
                if (path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase))
                {
                    // One description per face inside the collection, and the ARRAY INDEX is the
                    // face index - which is exactly what ExtractTtcFace needs later.
                    var descriptions = FontDescription.LoadFontCollectionDescriptions(path);
                    for (int i = 0; i < descriptions.Length; i++) AddFace(descriptions[i], path, i);
                }
                else
                {
                    AddFace(FontDescription.LoadDescription(path), path, 0);
                }
            }
            catch { /* unreadable, locked or exotic font file - skip it, never fail the scan */ }
        }

        private static void AddFace(FontDescription description, string path, int faceIndex)
        {
            string family = description.FontFamilyInvariantCulture;
            if (string.IsNullOrWhiteSpace(family)) return;

            var style = description.Style switch
            {
                SixLabors.Fonts.FontStyle.Bold       => XFontStyle.Bold,
                SixLabors.Fonts.FontStyle.Italic     => XFontStyle.Italic,
                SixLabors.Fonts.FontStyle.BoldItalic => XFontStyle.BoldItalic,
                _                                    => XFontStyle.Regular,
            };

            string faceKey = family + "#" + style + "#" + faceIndex + "#" + Path.GetFileName(path);
            if (!Faces.ContainsKey(faceKey)) Faces[faceKey] = new FaceFile(path, faceIndex);

            if (!Families.TryGetValue(family, out var byStyle))
                Families[family] = byStyle = new Dictionary<XFontStyle, string>();
            if (!byStyle.ContainsKey(style)) byStyle[style] = faceKey;   // first wins = pattern order
        }

        // ── Family lookup ─────────────────────────────────────────────────────────────────────

        /// <summary>The indexed styles of a family, or null when nothing matches. Caller holds Gate.</summary>
        private static Dictionary<XFontStyle, string>? LookupFamily(string familyName)
        {
            if (string.IsNullOrWhiteSpace(familyName)) return null;
            if (Families.TryGetValue(familyName, out var byStyle)) return byStyle;

            // A requested name can differ from the invariant family only in spacing or punctuation:
            // WPF's font list on a localized Windows, and PDF font resources lifted by the in-place
            // text editor ("SegoeUI", "TimesNewRomanPS"), both do this. Match loosely so a font the
            // user can actually see still resolves - and so FontCoverage reads the SAME face that
            // ResolveTypeface will hand PdfSharpCore.
            string flat = Flatten(familyName);
            if (flat.Length == 0) return null;
            foreach (var kv in Families)
                if (Flatten(kv.Key).Equals(flat, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        private static string Flatten(string s) => s.Replace(" ", "").Replace("-", "").Replace("_", "");

        // The stock resolver answered an unknown family with "whatever font was indexed first",
        // which is arbitrary but kept saves WORKING. Returning null instead would regress every
        // document whose font names match no installed family, so an unknown family lands on a
        // deterministic default here rather than on an exception.
        private static readonly string[] LastResortFamilies =
            ["Segoe UI", "Arial", "Tahoma", "Calibri", "Verdana", "Times New Roman"];

        /// <summary>A family to fall back on when the requested one is not installed. Caller holds Gate.</summary>
        private static Dictionary<XFontStyle, string>? LastResortFamily()
        {
            foreach (string family in LastResortFamilies)
                if (Families.TryGetValue(family, out var byStyle) && byStyle.Count > 0) return byStyle;

            // Nothing familiar is installed. Take the alphabetically first indexed family so the
            // choice is at least stable from run to run.
            string? any = Families.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            return any is not null ? Families[any] : null;
        }

        // ── IFontResolver ─────────────────────────────────────────────────────────────────────

        // Both members are declared nullable here even though IFontResolver spells them
        // non-nullable: the vendored assembly predates nullable annotations (it is oblivious, so
        // this is not a mismatch), and its own doc comments define null as "the request cannot be
        // satisfied". Saying so in the signature beats a null-forgiving `null!` on a value that
        // genuinely can be null. In practice neither returns null unless the machine has no
        // readable fonts at all - see the fallbacks below.
        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            try
            {
                EnsureIndexed();
                lock (Gate)
                {
                    var byStyle = LookupFamily(familyName) ?? LastResortFamily();
                    if (byStyle is null) return null;   // no fonts indexed at all

                    var want = (isBold, isItalic) switch
                    {
                        (true, true)  => XFontStyle.BoldItalic,
                        (true, false) => XFontStyle.Bold,
                        (false, true) => XFontStyle.Italic,
                        _             => XFontStyle.Regular,
                    };

                    // Exact style, else regular, else whatever this family has. PdfSharpCore
                    // simulates the missing emphasis, which beats failing to resolve at all.
                    if (byStyle.TryGetValue(want, out string? exact))
                        return new FontResolverInfo(exact);
                    if (byStyle.TryGetValue(XFontStyle.Regular, out string? regular))
                        return new FontResolverInfo(regular, isBold, isItalic);
                    string? any = byStyle.Values.FirstOrDefault();
                    return any is null ? null : new FontResolverInfo(any, isBold, isItalic);
                }
            }
            catch { return null; }
        }

        public byte[]? GetFont(string faceName)
        {
            try
            {
                EnsureIndexed();
                lock (Gate)
                {
                    if (Faces.TryGetValue(faceName, out var face) && ReadFace(face) is byte[] bytes)
                        return bytes;

                    // The face was indexed but has since been deleted or locked, or turned out to be
                    // a malformed collection. PdfSharpCore feeds this straight into
                    // XFontSource.GetOrCreateFrom, which dereferences it - so returning null here
                    // would throw out of the middle of a save. Any readable face beats that: wrong
                    // glyphs are recoverable, a lost document is not. Only the small stock Latin
                    // faces are tried, so this stays bounded instead of walking (and reading) every
                    // font on the machine.
                    foreach (string family in LastResortFamilies)
                    {
                        if (!Families.TryGetValue(family, out var byStyle)) continue;
                        if (!byStyle.TryGetValue(XFontStyle.Regular, out string? key)) continue;
                        if (string.Equals(key, faceName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (Faces.TryGetValue(key, out var alt) && ReadFace(alt) is byte[] fallback)
                            return fallback;
                    }
                    return null;   // nothing on this machine is readable; unrecoverable either way
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// The regular face of a family as standalone font bytes, or null when the family is not
        /// installed. Used by <see cref="FontCoverage"/> to read the 'cmap': the collection split has
        /// already happened here, so callers never have to know a face came out of a .ttc.
        /// </summary>
        internal static byte[]? RegularFaceBytes(string family)
        {
            try
            {
                EnsureIndexed();
                lock (Gate)
                {
                    // Deliberately NO last-resort fallback: coverage must answer for the family that
                    // was actually asked about, or FontCoverage would think an uninstalled family
                    // covers text it has never seen.
                    var byStyle = LookupFamily(family);
                    if (byStyle is null) return null;
                    if (!byStyle.TryGetValue(XFontStyle.Regular, out string? key))
                    {
                        key = byStyle.Values.FirstOrDefault();
                        if (key is null) return null;
                    }
                    return Faces.TryGetValue(key, out var face) ? ReadFace(face) : null;
                }
            }
            catch { return null; }
        }

        private static byte[]? ReadFace(FaceFile face)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(face.FilePath);
                // A collection must be split before PdfSharpCore sees it (it throws on 'ttcf').
                return IsCollection(bytes) ? ExtractTtcFace(bytes, face.FaceIndex) : bytes;
            }
            catch { return null; }
        }

        // ── TrueType Collection -> standalone face ────────────────────────────────────────────
        // A .ttc is one file holding several faces that SHARE table data: a 'ttcf' header, then one
        // offset table per face, whose directory entries point at tables anywhere in the file. So a
        // face is extracted by copying its tables out into a fresh sfnt with rewritten offsets - no
        // glyph data is touched or re-encoded, and every table checksum still stands because the
        // table BYTES are identical and the pad bytes are zero (which is what a checksum assumes).
        //
        // 'head'.checkSumAdjustment (a whole-file checksum) does go stale, but nothing reads it:
        // PdfSharpCore parses it into a field (Fonts.OpenType/OpenTypeFontTables.cs) and never
        // validates it, and PDF font embedding does not carry it either.

        private static bool IsCollection(byte[] b) =>
            b.Length >= 4 && b[0] == 0x74 && b[1] == 0x74 && b[2] == 0x63 && b[3] == 0x66;   // 'ttcf'

        private static uint ReadU32(byte[] b, int p) =>
            (uint)((b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]);

        private static ushort ReadU16(byte[] b, int p) => (ushort)((b[p] << 8) | b[p + 1]);

        private static void WriteU32(byte[] b, int p, uint v)
        {
            b[p] = (byte)(v >> 24); b[p + 1] = (byte)(v >> 16); b[p + 2] = (byte)(v >> 8); b[p + 3] = (byte)v;
        }

        private static void WriteU16(byte[] b, int p, ushort v) { b[p] = (byte)(v >> 8); b[p + 1] = (byte)v; }

        /// <summary>
        /// Rebuilds face <paramref name="faceIndex"/> of a TrueType Collection as a standalone font
        /// file. Returns null if the collection is malformed or the index is out of range, which
        /// sends the caller back to its own fallback rather than up the stack.
        /// </summary>
        private static byte[]? ExtractTtcFace(byte[] ttc, int faceIndex)
        {
            try
            {
                // TTCHeader: ttcTag(4) majorVersion(2) minorVersion(2) numFonts(4),
                // then numFonts tableDirectoryOffsets (4 each) from offset 12.
                if (ttc.Length < 12) return null;
                uint numFonts = ReadU32(ttc, 8);
                if (faceIndex < 0 || faceIndex >= numFonts) return null;
                int offsetPos = 12 + faceIndex * 4;
                if (offsetPos + 4 > ttc.Length) return null;
                int tableDir = (int)ReadU32(ttc, offsetPos);
                if (tableDir < 0 || tableDir + 12 > ttc.Length) return null;

                // Offset table: sfntVersion(4) numTables(2) searchRange(2) entrySelector(2)
                // rangeShift(2), then numTables 16-byte records: tag(4) checkSum(4) offset(4) len(4).
                // Those record offsets are absolute FROM THE START OF THE FILE, i.e. of the .ttc.
                uint sfntVersion = ReadU32(ttc, tableDir);
                int numTables = ReadU16(ttc, tableDir + 4);
                if (numTables <= 0 || numTables > 512) return null;
                int entries = tableDir + 12;
                if (entries + numTables * 16 > ttc.Length) return null;

                // Lay the new file out: 12-byte header, the directory, then each table padded to a
                // 4-byte boundary (required by the sfnt spec and assumed by the table checksums).
                // 12 + 16n is itself a multiple of 4, so every table stays aligned.
                int headerSize = 12 + numTables * 16;
                int total = headerSize;
                var tabs = new (uint tag, uint checksum, int srcOff, int len)[numTables];
                for (int i = 0; i < numTables; i++)
                {
                    int e = entries + i * 16;
                    uint tag = ReadU32(ttc, e);
                    uint sum = ReadU32(ttc, e + 4);
                    int off = (int)ReadU32(ttc, e + 8);
                    int len = (int)ReadU32(ttc, e + 12);
                    if (off < 0 || len < 0 || off > ttc.Length - len) return null;
                    tabs[i] = (tag, sum, off, len);
                    total += (len + 3) & ~3;
                    if (total < 0) return null;   // implausible sizes; treat as corrupt
                }

                var outBytes = new byte[total];
                WriteU32(outBytes, 0, sfntVersion);
                WriteU16(outBytes, 4, (ushort)numTables);
                // searchRange / entrySelector / rangeShift are derived, and some parsers do read
                // them: searchRange = 16 * 2^floor(log2(numTables)), entrySelector = that exponent,
                // rangeShift = 16*numTables - searchRange.
                int pow2 = 1, selector = 0;
                while (pow2 * 2 <= numTables) { pow2 *= 2; selector++; }
                WriteU16(outBytes, 6, (ushort)(pow2 * 16));
                WriteU16(outBytes, 8, (ushort)selector);
                WriteU16(outBytes, 10, (ushort)(numTables * 16 - pow2 * 16));

                int write = headerSize;
                for (int i = 0; i < numTables; i++)
                {
                    var t = tabs[i];
                    int e = 12 + i * 16;
                    WriteU32(outBytes, e, t.tag);
                    WriteU32(outBytes, e + 4, t.checksum);   // data copied verbatim, so it still stands
                    WriteU32(outBytes, e + 8, (uint)write);
                    WriteU32(outBytes, e + 12, (uint)t.len);
                    Buffer.BlockCopy(ttc, t.srcOff, outBytes, write, t.len);
                    write += (t.len + 3) & ~3;               // the pad bytes stay zero
                }
                return outBytes;
            }
            catch { return null; }
        }
    }
}
