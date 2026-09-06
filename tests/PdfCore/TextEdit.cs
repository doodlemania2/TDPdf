using PdfSharpCore.Drawing;
using TDPdf.Services;

/// <summary>
/// Real text editing: changing the words in the file, not covering them with new ones.
/// </summary>
/// <remarks>
/// TDPdf has always "edited" text by painting a white rectangle over the old words and drawing new
/// ones on top. That works, and it is a lie the file tells about itself: the original text is still
/// there, still selectable, still in every extraction. This replaces the string on the text object
/// itself, so the old words stop existing.
///
/// The trap is the FONT. Almost every PDF embeds a subset — the glyphs the document actually used,
/// and nothing else — so a document containing no "Z" has no "Z" to draw with. PDFium reports
/// nothing when asked for one; the result is a blank or a notdef box, discovered by the person who
/// opens the file later. So the tests here deliberately ask for characters the fixture's font
/// cannot have, and require a refusal rather than a silent hole.
/// </remarks>
internal static class TextEdit
{
    private static (double L, double B, double R, double T) BoxOf(string path, string word)
    {
        using var d = UglyToad.PdfPig.PdfDocument.Open(path);
        var w = d.GetPage(1).GetWords().First(x => x.Text.Contains(word, StringComparison.Ordinal));
        var b = w.BoundingBox;
        return (b.Left, b.Bottom, b.Right, b.Top);
    }

    private static string TextOf(string path)
    {
        using var d = UglyToad.PdfPig.PdfDocument.Open(path);
        return d.GetPage(1).Text;
    }

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine($"\nReal text editing (engine: CanEditText={PdfiumInterop.CanEditText})");

        // The fixture's font is subsetted to exactly these characters, which is what makes the
        // coverage test below meaningful rather than theoretical.
        string src = Path.Combine(tmp, "textedit.pdf");
        {
            var doc = new PdfSharpCore.Pdf.PdfDocument();
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            var font = new XFont("Helvetica", 18);
            gfx.DrawString("INVOICE TOTAL", font, XBrushes.Black, 60, 100);
            gfx.DrawString("LEAVE ALONE", font, XBrushes.Black, 60, 300);
            doc.Save(src);
        }
        Check("the fixture has the run to edit", TextOf(src).Contains("INVOICE"), "");

        var box = BoxOf(src, "INVOICE");
        var bounds = new PdfiumInterop.PdfRect(box.L - 2, box.B - 2, box.R + 2, box.T + 2);

        // ── The happy path: same letters, rearranged ────────────────────────────────────
        // "TOTAL" and "INVOICE" between them use A C E I L N O T V — so "VITAL NOTICE" needs
        // nothing the subset does not already carry.
        {
            string dest = Path.Combine(tmp, "textedit-ok.pdf");
            if (File.Exists(dest)) File.Delete(dest);

            var res = PdfTextEdit.Apply(src, dest, new[]
            {
                new PdfiumInterop.TextEditRequest(0, bounds, "INVOICE TOTAL", "VITAL NOTICE"),
            });

            Console.WriteLine($"        ok={res.Ok} replaced={res.Replaced} {res.Error}");
            Check("the replacement succeeded", res.Ok && res.Replaced == 1, res.Error ?? "");
            if (res.Ok && File.Exists(dest))
            {
                string after = TextOf(dest);
                Console.WriteLine($"        text after: \"{after.Replace("\n", " ")}\"");
                Check("the new words are in the file", after.Contains("VITAL NOTICE"), after);
                // The point of the whole exercise: not covered, GONE.
                Check("the old words are gone, not covered", !after.Contains("INVOICE"), after);
                Check("the untouched run is untouched", after.Contains("LEAVE ALONE"), after);
                Check("no text-drawing operator was added or lost",
                      Streams.TextShowingOps(dest) == Streams.TextShowingOps(src),
                      $"{Streams.TextShowingOps(src)} -> {Streams.TextShowingOps(dest)}");
            }
        }

        // ── The refusal: a letter the embedded subset cannot possibly have ──────────────
        {
            string dest = Path.Combine(tmp, "textedit-refused.pdf");
            if (File.Exists(dest)) File.Delete(dest);

            var req = new PdfiumInterop.TextEditRequest(0, bounds, "INVOICE TOTAL", "PUZZLED SPHINX");
            var res = PdfTextEdit.Apply(src, dest, new[] { req });

            Console.WriteLine($"        ok={res.Ok} error={res.Error}");
            Console.WriteLine($"        font: {PdfiumInterop.DescribeFontCoverage(src, req)}");
            Check("an edit the font cannot draw is refused", !res.Ok, res.Error ?? "");
            Check("the refusal names the characters that could not be drawn",
                  res.MissingCharacters.Contains('P') && res.MissingCharacters.Contains('U')
                  && res.MissingCharacters.Contains('Z'),
                  $"\"{res.MissingCharacters}\"");
            Check("the caller is told to fall back to the overlay", res.ShouldUseOverlay, "");
            // The decisive one: a refusal must leave NO file, or the holes ship.
            Check("NO output file was written", !File.Exists(dest), "");
        }

        // ── Wrong original text: the edit must not land on a different run ──────────────
        {
            string dest = Path.Combine(tmp, "textedit-nomatch.pdf");
            if (File.Exists(dest)) File.Delete(dest);

            var res = PdfTextEdit.Apply(src, dest, new[]
            {
                new PdfiumInterop.TextEditRequest(0, bounds, "SOMETHING ELSE ENTIRELY", "NOT AT ALL"),
            });
            Console.WriteLine($"        ok={res.Ok} error={res.Error}");
            Check("an edit whose original text does not match is not applied", !res.Ok, res.Error ?? "");
            Check("no output file was written", !File.Exists(dest), "");
        }
    }
}
