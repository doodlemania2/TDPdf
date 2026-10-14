using System.Text;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

/// <summary>
/// An xref stream with no /Index whose /Size undercounts its own entries must still open whole.
/// Upstream KillerPDF #406; the TDPdf patch is in third_party/PdfSharpCore/Pdf.IO/Parser.cs.
/// </summary>
/// <remarks>
/// With no /Index the default subsection is [0 /Size]. PdfSharpCore took that literally, so a
/// writer that emitted /Size too small had every entry past it dropped without a word — here the
/// page object itself, leaving a document whose one page cannot be resolved. The parser now covers
/// max(/Size, entries actually in the stream data). The file is built byte by byte so the
/// malformation is exactly the one under test and nothing else: an uncompressed xref stream,
/// /W [1 4 2], five entries, and a /Size of 3 that stops short of the page (object 3) and the xref
/// stream (object 4).
/// </remarks>
internal static class XrefStreamSize
{
    /// <summary>A one-page PDF 1.5 whose cross-reference is a stream declaring <paramref name="size"/>.</summary>
    internal static byte[] Build(int size)
    {
        var ms = new MemoryStream();
        void Ascii(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

        Ascii("%PDF-1.5\n");
        var offsets = new long[5];
        offsets[1] = ms.Position; Ascii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = ms.Position; Ascii("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = ms.Position; Ascii("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] >>\nendobj\n");
        offsets[4] = ms.Position;

        // Five entries of 1 + 4 + 2 bytes: the free head of the list, then objects 1-4 in use.
        var data = new List<byte>();
        void Entry(int type, long f2, int f3)
        {
            data.Add((byte)type);
            for (int k = 3; k >= 0; k--) data.Add((byte)(f2 >> (8 * k)));
            data.Add((byte)(f3 >> 8)); data.Add((byte)f3);
        }
        Entry(0, 0, 65535);
        for (int o = 1; o <= 4; o++) Entry(1, offsets[o], 0);

        Ascii($"4 0 obj\n<< /Type /XRef /Size {size} /W [1 4 2] /Root 1 0 R /Length {data.Count} >>\nstream\n");
        ms.Write(data.ToArray(), 0, data.Count);
        Ascii($"\nendstream\nendobj\nstartxref\n{offsets[4]}\n%%EOF\n");
        return ms.ToArray();
    }

    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("── xref stream with /Size too small and no /Index (#406) ─────");

        // Control: the same file with an honest /Size. If this one fails, the fixture is wrong
        // and the undersized case below proves nothing.
        static (int pages, double width, string error) Open(byte[] bytes)
        {
            try
            {
                using var doc = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Modify);
                if (doc.PageCount == 0) return (0, 0, "");
                var box = doc.Pages[0].Elements.GetArray("/MediaBox");
                return (doc.PageCount, box?.Elements.GetReal(2) ?? 0, "");
            }
            catch (Exception ex) { return (-1, 0, $"{ex.GetType().Name}: {ex.Message}"); }
        }

        var honest = Open(Build(5));
        Check("control: an honest /Size 5 opens with its one 200pt page",
              honest.pages == 1 && Math.Abs(honest.width - 200) < 1e-9,
              $"pages={honest.pages} width={honest.width} {honest.error}");

        var shortSize = Open(Build(3));
        Check("/Size 3 over five entries still opens with its one 200pt page",
              shortSize.pages == 1 && Math.Abs(shortSize.width - 200) < 1e-9,
              $"pages={shortSize.pages} width={shortSize.width} {shortSize.error}");

        // And the round trip: the recovered page survives a save and a strict reopen.
        try
        {
            using var doc = PdfReader.Open(new MemoryStream(Build(3)), PdfDocumentOpenMode.Modify);
            var outMs = new MemoryStream();
            doc.Save(outMs, false);
            var again = Open(outMs.ToArray());
            Check("/Size 3: the recovered page survives save and reopen",
                  again.pages == 1 && Math.Abs(again.width - 200) < 1e-9,
                  $"pages={again.pages} width={again.width} {again.error}");
        }
        catch (Exception ex)
        {
            Check("/Size 3: the recovered page survives save and reopen", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
