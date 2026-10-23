using PdfSharpCore.Drawing;
using TDPdf.Services;
using UglyToad.PdfPig.Content;

/// <summary>
/// #187: the search hits that "Redact matches" turns into marks cover the matched letters, and
/// only them.
/// </summary>
/// <remarks>
/// The search this replaced returned, for every word within ~20 characters BEFORE an occurrence,
/// a box from that word to the end of the match. On the demo incident report "Jordan Ellis"
/// (4 occurrences) gave 17 boxes covering the run before each name, the table header row and the
/// phone and badge cells. Every check here is phrased in letters, through the same
/// centre-inside-a-rectangle test <see cref="PdfRedaction.FindSurvivingText"/> uses: a mark
/// "covers" exactly the letters whose centres it contains.
/// </remarks>
internal static class TextSearch
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nSearch matches cover only the matched letters (#187)");
        DemoIncidentReport(Check, tmp);
        foreach (int rotate in new[] { 0, 90, 180, 270 })
            Synthetic(Check, tmp, rotate);
        PartWord(Check, tmp);
    }

    // ── The file the issue was seen on ──────────────────────────────────────────────────────

    private static void DemoIncidentReport(Action<string, bool, string> Check, string tmp)
    {
        string? src = FindRepoFile("build/site/screenshots/demo/incident-report.pdf");
        if (src is null) { Check("demo incident-report.pdf found", false, "not found above " + AppContext.BaseDirectory); return; }

        List<PdfTextSearch.Match> matches;
        List<Word> words;
        List<Letter> letters;
        using (var pig = UglyToad.PdfPig.PdfDocument.Open(src))
        {
            var pg = pig.GetPage(1);
            words = pg.GetWords().ToList();
            letters = pg.Letters.ToList();
            matches = PdfTextSearch.FindMatches(pg, "jordan ellis");
        }
        var boxes = matches.SelectMany(m => m.Boxes).ToList();

        Check("demo: \"Jordan Ellis\" is found exactly 4 times", matches.Count == 4, $"{matches.Count} matches");
        Check("demo: one box per match (none of them wraps)", boxes.Count == 4, $"{boxes.Count} boxes");

        // What the marks cover, letter by letter, against what they should: the letters of the
        // four Jordans and the four Ellises — not the full stop after the first Ellis.
        var covered = Covered(letters, boxes);
        string expected = Sorted(string.Concat(Enumerable.Repeat("JordanEllis", 4)));
        Check("demo: the marks cover the letters of the four names and nothing else",
              Sorted(covered) == expected, $"covered \"{Sorted(covered)}\"");

        foreach (string neighbour in new[] { "Person", "Role", "Phone", "Badge", "badge", "30881,", "issued",
                                             "to", "Contractor,", "(555)", "014-2290", "30881", "21:58.", "told" })
        {
            var hits = words.Where(w => w.Text == neighbour)
                            .Where(w => w.Letters.Any(l => boxes.Any(b => CentreIn(b, l.BoundingBox)))).ToList();
            Check($"demo: no mark covers any letter of \"{neighbour}\"", hits.Count == 0,
                  $"{hits.Count} occurrence(s) covered");
        }

        double tallest = boxes.Count == 0 ? 0 : boxes.Max(b => b.Top - b.Bottom);
        Check("demo: every mark is one line high (under 12pt on 9pt text)", tallest > 0 && tallest < 12, $"tallest {tallest:F1}pt");

        // And the real pipeline over those marks — text frame -> user space, as "Redact matches"
        // converts them — removes the names and its own verification of the output passes.
        using var sharp = PdfSharpCore.Pdf.IO.PdfReader.Open(src, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import);
        var page = sharp.Pages[0];
        var rects = new Dictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>>
        {
            [0] = boxes.Select(b => PdfPageGeometry.TextFrameToPdf(page, b)).ToList()
        };
        string dest = Path.Combine(tmp, "textsearch-demo-out.pdf");
        if (File.Exists(dest)) File.Delete(dest);
        var res = PdfRedaction.Apply(src, dest, new PdfRedaction.Request
        {
            RectsByPage = rects, RemovePartialOverlaps = true, ScrubMetadata = true,
        });
        Check("demo: redacting the matches succeeds and verifies clean", res.Ok,
              (res.Error ?? "") + (res.Survivors.Count > 0 ? $" survivors [{string.Join(", ", res.Survivors)}]" : ""));
        if (res.Ok && File.Exists(dest))
        {
            using var outPig = UglyToad.PdfPig.PdfDocument.Open(dest);
            var outWords = outPig.GetPage(1).GetWords().Select(w => w.Text).ToList();
            Check("demo: neither name survives in the output",
                  !outWords.Any(w => w.StartsWith("Jordan") || w.StartsWith("Ellis")),
                  string.Join(" ", outWords.Where(w => w.StartsWith("Jordan") || w.StartsWith("Ellis"))));
            foreach (string kept in new[] { "Person", "Role", "Phone", "Badge", "Priya", "Okafor", "issued", "told", "Contractor,", "30881", "wedged" })
                Check($"demo: \"{kept}\" survives the redaction", outWords.Contains(kept), "");
        }
        // "Include partly covered objects" off. That switch decides what happens to a text object
        // a mark only partly covers; it plays no part in how big the mark is. Whatever the
        // pipeline does with it, it must never report success with a name still in the file.
        string strict = Path.Combine(tmp, "textsearch-demo-strict.pdf");
        if (File.Exists(strict)) File.Delete(strict);
        var res2 = PdfRedaction.Apply(src, strict, new PdfRedaction.Request
        {
            RectsByPage = rects, RemovePartialOverlaps = false, ScrubMetadata = true,
        });
        bool namesLeft = false;
        if (res2.Ok && File.Exists(strict))
        {
            using var strictPig = UglyToad.PdfPig.PdfDocument.Open(strict);
            namesLeft = strictPig.GetPage(1).GetWords().Any(w => w.Text.StartsWith("Jordan") || w.Text.StartsWith("Ellis"));
        }
        Check("demo, partly covered objects excluded: never a clean result with a name left behind",
              !(res2.Ok && namesLeft),
              $"ok={res2.Ok} partial={res2.Partial.Count} survivors={res2.Survivors.Count} {res2.Error}");
    }

    // ── A generated equivalent, at every quarter turn, with a match across a line break ─────

    private static void Synthetic(Action<string, bool, string> Check, string tmp, int rotate)
    {
        string label = $"/Rotate {rotate}";
        string src = Path.Combine(tmp, $"textsearch-{rotate}.pdf");
        {
            var d = new PdfSharpCore.Pdf.PdfDocument();
            var p = d.AddPage();
            using (var g = XGraphics.FromPdfPage(p))
            {
                var f = new XFont("Helvetica", 10);
                g.DrawString("Person Role Phone Badge", f, XBrushes.Black, 72, 100);
                g.DrawString("badge 30881, issued to Jordan Ellis. Jordan Ellis told", f, XBrushes.Black, 72, 114);
                g.DrawString("the door was wedged at 21:58 by Jordan", f, XBrushes.Black, 72, 128);
                g.DrawString("Ellis who has been reminded", f, XBrushes.Black, 72, 142);
            }
            p.Rotate = rotate;
            d.Save(src);
        }

        using var pig = UglyToad.PdfPig.PdfDocument.Open(src);
        var pg = pig.GetPage(1);
        var letters = pg.Letters.ToList();
        var matches = PdfTextSearch.FindMatches(pg, "jordan ellis");
        var boxes = matches.SelectMany(m => m.Boxes).ToList();

        Check($"{label}: three matches, one of them across a line break", matches.Count == 3,
              $"{matches.Count} matches");
        Check($"{label}: the wrapped match is two boxes, one per line",
              matches.Count == 3 && matches[2].Boxes.Count == 2 && matches.Take(2).All(m => m.Boxes.Count == 1),
              string.Join(",", matches.Select(m => m.Boxes.Count)));
        if (matches.Count == 3 && matches[2].Boxes.Count == 2)
        {
            var (a, b) = (matches[2].Boxes[0], matches[2].Boxes[1]);
            Check($"{label}: the two halves of the wrapped match do not overlap",
                  !(a.Left < b.Right && a.Right > b.Left && a.Bottom < b.Top && a.Top > b.Bottom), "");
        }

        string covered = Sorted(Covered(letters, boxes));
        Check($"{label}: the marks cover the letters of the three names and nothing else",
              covered == Sorted(string.Concat(Enumerable.Repeat("JordanEllis", 3))), $"covered \"{covered}\"");

        if (rotate == 0)
        {
            // Words are joined by one space, so a query typed with two (or a tab) must still find them.
            int spaced = PdfTextSearch.FindMatches(pg, "Jordan  Ellis").Count;
            Check($"{label}: a doubled space in the query finds the same three matches", spaced == 3, $"{spaced}");
        }
    }

    private static void PartWord(Action<string, bool, string> Check, string tmp)
    {
        string src = Path.Combine(tmp, "textsearch-partword.pdf");
        {
            var d = new PdfSharpCore.Pdf.PdfDocument();
            var p = d.AddPage();
            using (var g = XGraphics.FromPdfPage(p))
                g.DrawString("Reference ABCDEF-12345-XYZ", new XFont("Helvetica", 12), XBrushes.Black, 72, 100);
            d.Save(src);
        }
        using var pig = UglyToad.PdfPig.PdfDocument.Open(src);
        var pg = pig.GetPage(1);
        var boxes = PdfTextSearch.FindMatches(pg, "12345").SelectMany(m => m.Boxes).ToList();
        string covered = Sorted(Covered(pg.Letters.ToList(), boxes));
        Check("part of a word: the mark covers the matched characters, not the whole word",
              covered == Sorted("12345"), $"covered \"{covered}\"");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Every letter on the page whose centre falls inside one of the boxes.</summary>
    private static string Covered(IEnumerable<Letter> letters, IReadOnlyList<PdfiumInterop.PdfRect> boxes) =>
        string.Concat(letters
                           .Where(l => !string.IsNullOrWhiteSpace(l.Value))
                           .Where(l => boxes.Any(b => CentreIn(b, l.BoundingBox)))
                           .Select(l => l.Value));

    private static bool CentreIn(PdfiumInterop.PdfRect r, UglyToad.PdfPig.Core.PdfRectangle b) =>
        PdfPageGeometry.ContainsCenter(r, b.Left, b.Bottom, b.Right, b.Top);

    private static string Sorted(string s) => string.Concat(s.OrderBy(c => c));

    private static string? FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
