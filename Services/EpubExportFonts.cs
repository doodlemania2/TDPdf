namespace TDPdf.Services
{
    /// <summary>Why a PDF font was, or was not, copied into an exported EPUB.</summary>
    internal enum EpubFontDecision
    {
        Embed,
        /// <summary>fsType restricted-licence bit: the font may not be embedded.</summary>
        DeniedRestricted,
        /// <summary>fsType preview-and-print bit: embedding is for viewing only, not for a book the reader keeps.</summary>
        DeniedPreviewPrint,
        /// <summary>fsType bitmap-only bit: no outlines may be used.</summary>
        DeniedBitmapOnly,
        /// <summary>The font file has no OS/2 table, so no licence flags exist to read.</summary>
        DeniedNoLicenceInfo,
        /// <summary>Type 1, Type 3, bare CFF, or anything else that is not a standalone TrueType/OpenType file.</summary>
        DeniedFormat,
        /// <summary>A subset with a custom encoding: no Unicode map, or one that misses characters the book uses.</summary>
        DeniedEncoding,
        /// <summary>The font could not be extracted from the PDF at all (not embedded, or unreadable).</summary>
        DeniedNotExtractable,
    }

    internal enum EpubFontKind { Unknown, TrueType, OpenTypeCff, BareCff, Type1 }

    /// <summary>What was read from a font file's headers.</summary>
    internal sealed record EpubFontInfo(EpubFontKind Kind, ushort? FsType, CmapCoverage? Cmap);

    /// <summary>
    /// The licence decision for putting a PDF's fonts into an EPUB. The PDF's embedded fonts are
    /// copied only where the font's own OS/2 fsType flags allow it, and only where the copy would
    /// actually draw the book's text.
    /// </summary>
    internal static class EpubFontPolicy
    {
        /// <summary>
        /// The fsType decision table. Bits 0-3 are the usage permission, and at most one should be
        /// set: 0 installable and 8 editable allow embedding; 4 preview and print, and 2 restricted,
        /// do not. Bit 9 (0x0200) is bitmap-only. Where a file sets several bits, the stricter
        /// one wins, and the reserved bit 0 is treated as restricted.
        /// </summary>
        internal static EpubFontDecision FromFsType(ushort fsType)
        {
            if ((fsType & 0x0200) != 0) return EpubFontDecision.DeniedBitmapOnly;
            int usage = fsType & 0x000F;
            if ((usage & 0x2) != 0) return EpubFontDecision.DeniedRestricted;
            if ((usage & 0x4) != 0) return EpubFontDecision.DeniedPreviewPrint;
            if ((usage & 0x1) != 0) return EpubFontDecision.DeniedRestricted;
            return EpubFontDecision.Embed;
        }

        /// <summary>
        /// Decides for one extracted font file. <paramref name="usedChars"/> are the characters the
        /// book sets in this font; a font whose Unicode map lacks any of them is a subset with a
        /// custom encoding and is left out, because the browser would draw those characters from
        /// somewhere else.
        /// </summary>
        internal static EpubFontDecision Decide(EpubFontInfo? info, IReadOnlyCollection<int> usedChars)
        {
            if (info is null) return EpubFontDecision.DeniedNotExtractable;
            if (info.Kind is not (EpubFontKind.TrueType or EpubFontKind.OpenTypeCff)) return EpubFontDecision.DeniedFormat;
            if (info.FsType is not { } fs) return EpubFontDecision.DeniedNoLicenceInfo;
            var lic = FromFsType(fs);
            if (lic != EpubFontDecision.Embed) return lic;
            if (info.Cmap is null) return EpubFontDecision.DeniedEncoding;
            foreach (int cp in usedChars)
            {
                if (cp <= 0x20 || cp == 0xA0) continue;   // spaces are drawn from nothing
                if (!info.Cmap.Covers(cp)) return EpubFontDecision.DeniedEncoding;
            }
            return EpubFontDecision.Embed;
        }

        internal static string Describe(EpubFontDecision d) => d switch
        {
            EpubFontDecision.Embed => "embedded",
            EpubFontDecision.DeniedRestricted => "its licence does not allow embedding",
            EpubFontDecision.DeniedPreviewPrint => "its licence allows preview and print only",
            EpubFontDecision.DeniedBitmapOnly => "its licence allows bitmap use only",
            EpubFontDecision.DeniedNoLicenceInfo => "it carries no licence flags",
            EpubFontDecision.DeniedFormat => "it is not a TrueType or OpenType font file",
            EpubFontDecision.DeniedEncoding => "it is a subset with a custom encoding",
            _ => "it could not be extracted from the PDF",
        };

        /// <summary>Strips the six-letter subset tag: "ABCDEF+Arial" becomes "Arial".</summary>
        internal static string BaseName(string? fontName)
        {
            if (string.IsNullOrEmpty(fontName)) return "";
            int plus = fontName.IndexOf('+');
            return plus == 6 && fontName.Take(6).All(c => c is >= 'A' and <= 'Z') ? fontName[7..] : fontName;
        }
    }

    /// <summary>Reads just enough of a font file to classify it and find its licence flags. Total: never throws.</summary>
    internal static class EpubFontFile
    {
        internal static EpubFontInfo Inspect(byte[] data)
        {
            try
            {
                if (data.Length < 4) return new(EpubFontKind.Unknown, null, null);
                if (data[0] == (byte)'%' && data[1] == (byte)'!' || data[0] == 0x80 && data[1] == 0x01)
                    return new(EpubFontKind.Type1, null, null);

                uint ver = U32(data, 0);
                bool isCff = ver == 0x4F54544F;                       // 'OTTO'
                bool isTt = ver is 0x00010000 or 0x74727565;          // 1.0, 'true'
                if (!isCff && !isTt)
                {
                    // A bare CFF table starts with major version 1.
                    return new(data[0] == 1 && data[1] == 0 ? EpubFontKind.BareCff : EpubFontKind.Unknown, null, null);
                }
                if (data.Length < 12) return new(EpubFontKind.Unknown, null, null);

                int numTables = U16(data, 4);
                bool head = false, outlines = false;
                ushort? fs = null;
                for (int i = 0; i < numTables; i++)
                {
                    int e = 12 + i * 16;
                    if (e + 16 > data.Length) break;
                    uint tag = U32(data, e);
                    int off = (int)U32(data, e + 8);
                    switch (tag)
                    {
                        case 0x68656164: head = true; break;                                   // 'head'
                        case 0x676C7966: case 0x43464620: outlines = true; break;              // 'glyf', 'CFF '
                        case 0x4F532F32:                                                       // 'OS/2'
                            if (off >= 0 && off + 10 <= data.Length) fs = U16(data, off + 8);
                            break;
                    }
                }
                if (!head || !outlines) return new(EpubFontKind.Unknown, null, null);
                return new(isCff ? EpubFontKind.OpenTypeCff : EpubFontKind.TrueType, fs, CmapCoverage.Parse(data));
            }
            catch { return new(EpubFontKind.Unknown, null, null); }
        }

        private static ushort U16(byte[] b, int p) => (ushort)((b[p] << 8) | b[p + 1]);
        private static uint U32(byte[] b, int p) => (uint)((b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]);
    }
}
