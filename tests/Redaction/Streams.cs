using System.Text;
using PdfSharpCore.Pdf;

/// <summary>
/// Looks at what is actually left in the FILE, rather than at what a reader chooses to show.
/// </summary>
/// <remarks>
/// Text extraction proves a viewer no longer displays something. It does not prove the bytes are
/// gone, which is the entire claim redaction makes. These helpers decompress every stream in a
/// document so a test can ask the harder question.
///
/// Note what does NOT work here: searching for the secret as a literal string. PdfSharpCore embeds
/// a subset TrueType font and writes glyph indices, so "SECRET…" never appears in a content stream
/// in the first place — an absence test against it passes on a file where the text is entirely
/// intact. The reliable signal is the PDF operator, which is always plain ASCII: a page that shows
/// text contains Tj or TJ, and a page that shows none contains neither.
/// </remarks>
internal static class Streams
{
    /// <summary>Every decompressed stream in the file, concatenated.</summary>
    public static string All(string path)
    {
        using var doc = PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);
        var sb = new StringBuilder();
        foreach (var obj in doc.Internals.GetAllObjects())
        {
            if (obj is not PdfDictionary dict || dict.Stream is null) continue;
            byte[]? bytes = null;
            try { bytes = dict.Stream.UnfilteredValue; } catch { }
            try { bytes ??= dict.Stream.Value; } catch { }
            if (bytes is not null) sb.Append(Encoding.Latin1.GetString(bytes));
        }
        return sb.ToString();
    }

    /// <summary>How many text-showing operators the file's streams contain.</summary>
    /// <remarks>
    /// Counts the operator, not the string: <c>Tj</c> and <c>TJ</c> are ASCII in the content stream
    /// whatever encoding the font uses. Preceded by a delimiter so an identifier ending in "Tj"
    /// cannot inflate the count.
    /// </remarks>
    public static int TextShowingOps(string path)
    {
        string s = All(path);
        int n = 0;
        for (int i = 1; i + 1 < s.Length; i++)
        {
            if (s[i] != 'T' || (s[i + 1] != 'j' && s[i + 1] != 'J')) continue;
            char before = s[i - 1];
            if (before is ')' or ']' or ' ' or '\n' or '\r' or '\t') n++;
        }
        return n;
    }

    /// <summary>Every image XObject in the file, as (width, height, filter).</summary>
    public static List<(int W, int H, string Filter)> Images(string path)
    {
        using var doc = PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);
        var found = new List<(int, int, string)>();
        foreach (var obj in doc.Internals.GetAllObjects())
        {
            if (obj is not PdfDictionary d) continue;
            if (d.Elements.GetName("/Subtype") != "/Image") continue;
            found.Add((d.Elements.GetInteger("/Width"),
                       d.Elements.GetInteger("/Height"),
                       d.Elements.GetName("/Filter")));
        }
        return found;
    }
}
