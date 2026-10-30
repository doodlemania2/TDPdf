using PdfSharpCore.Drawing;
using TDPdf.Services;

/// <summary>
/// Rotated, upside-down and mirrored text get real boxes: <see cref="PdfBoxExtent"/> and the
/// line grouping in <see cref="TextRunService"/>. Upstream KillerPDF #437, #452, #453.
/// </summary>
/// <remarks>
/// PdfPig documents Left/Right/Top/Bottom as valid only for an unrotated box. PdfPig 0.1.14 (what
/// TDPdf ships) happens to normalise them for 180° and mirrored boxes, but not for a quarter
/// turn: text drawn at 90° reports Top below Bottom and Right left of Left. Read raw, that made
/// the line grouping's minimum height negative, the line band inverted and the selection quad
/// collapse to its 2px floor. The fixture proves PdfPig really reports the inverted raw values,
/// so a pass cannot be vacuous, and keeps the 180° and mirrored cases as regressions for a
/// PdfPig upgrade that stops normalising them.
/// </remarks>
internal static class InvertedText
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("Rotated, upside-down and mirrored text boxes (upstream #437 #452 #453)");

        string path = Path.Combine(tmp, "inverted-text.pdf");
        {
            var doc = new PdfSharpCore.Pdf.PdfDocument();
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(400);
            page.Height = XUnit.FromPoint(400);
            using var gfx = XGraphics.FromPdfPage(page);
            var font = new XFont("Helvetica", 20);
            gfx.DrawString("UPRIGHT LINE", font, XBrushes.Black, 40, 60);
            var s = gfx.Save();
            gfx.RotateAtTransform(180, new XPoint(200, 200));
            gfx.DrawString("UPSIDE DOWN", font, XBrushes.Black, 120, 200);
            gfx.Restore(s);
            s = gfx.Save();
            gfx.MultiplyTransform(new XMatrix(-1, 0, 0, 1, 400, 0));
            gfx.DrawString("MIRROR TEXT", font, XBrushes.Black, 120, 330);
            gfx.Restore(s);
            s = gfx.Save();
            gfx.RotateAtTransform(90, new XPoint(30, 120));
            gfx.DrawString("QUARTER", font, XBrushes.Black, 30, 120);
            gfx.Restore(s);
            s = gfx.Save();
            gfx.RotateAtTransform(-90, new XPoint(370, 300));
            gfx.DrawString("SIDEWAYS", font, XBrushes.Black, 370, 300);
            gfx.Restore(s);
            doc.Save(path);
        }

        List<UglyToad.PdfPig.Content.Word> words;
        using (var pig = UglyToad.PdfPig.PdfDocument.Open(path))
            words = pig.GetPage(1).GetWords().ToList();
        static string Sorted(string s) => string.Concat(s.Select(c => c.ToString()).OrderBy(c => c, StringComparer.Ordinal));
        string LettersOf(IEnumerable<UglyToad.PdfPig.Content.Word> ws) =>
            Sorted(string.Concat(ws.SelectMany(w => w.Letters).Select(l => l.Value)));
        // PdfPig's word splitter does not keep inverted words whole ("N WOD EDI SPU"), so words
        // are grouped by drawing, by where their ink is.
        static (double L, double B, double R, double T) E(UglyToad.PdfPig.Content.Word w) => PdfBoxExtent.Of(w.BoundingBox);
        var upright = words.Where(w => w.Text == "UPRIGHT").ToList();
        var upside = words.Where(w => E(w).B > 150 && E(w).T < 250 && E(w).L > 100 && E(w).R < 300).ToList();
        var mirror = words.Where(w => E(w).B > 50 && E(w).T < 100 && E(w).L > 100 && E(w).R < 300).ToList();
        var quarter = words.Where(w => E(w).R < 60).ToList();
        var sideways = words.Where(w => E(w).L > 340).ToList();
        Check("fixture: every drawing is found",
              upright.Count == 1 && LettersOf(upside) == Sorted("UPSIDEDOWN") && LettersOf(mirror) == Sorted("MIRRORTEXT")
              && LettersOf(quarter) == Sorted("QUARTER") && LettersOf(sideways) == Sorted("SIDEWAYS"),
              string.Join(" ", words.Select(w => w.Text)));
        if (upright.Count != 1 || quarter.Count == 0 || sideways.Count == 0 || upside.Count == 0 || mirror.Count == 0) return;

        bool Inverted(UglyToad.PdfPig.Core.PdfRectangle b) => b.Top < b.Bottom || b.Right < b.Left;
        Check("fixture: PdfPig reports inverted raw Top/Bottom or Left/Right for quarter-turned text",
              quarter.Concat(sideways).Any(w => Inverted(w.BoundingBox)),
              string.Join("; ", quarter.Concat(sideways).Select(w =>
                  $"{w.Text} L{w.BoundingBox.Left:F0} R{w.BoundingBox.Right:F0} T{w.BoundingBox.Top:F0} B{w.BoundingBox.Bottom:F0}")));

        // ── the shared extent ───────────────────────────────────────────────────────────────
        var (pl, pbm, pr, pt) = E(upright[0]);
        double em = pt - pbm;   // upright glyph height
        foreach (var w in upside.Concat(mirror))
        {
            var (l, b, r, t) = E(w);
            Check($"extent: inverted word \"{w.Text}\" is as tall as upright text and wider than 1pt",
                  Math.Abs((t - b) - em) < em * 0.25 && r - l > 5, $"w {r - l:F1} h {t - b:F1} vs {em:F1}");
        }
        foreach (var w in quarter.Concat(sideways))
        {
            var (l, b, r, t) = E(w);
            // PdfPig splits vertical text one letter per word; each is one em across, not 0.
            Check($"extent: quarter-turned letter \"{w.Text}\" is as wide as upright text is tall",
                  Math.Abs((r - l) - em) < em * 0.25 && t - b > 1, $"w {r - l:F1} h {t - b:F1} vs {em:F1}");
        }
        var ub0 = upright[0].BoundingBox;
        Check("extent: an upright box is exactly its raw Left/Bottom/Right/Top",
              pl == ub0.Left && pr == ub0.Right && pt == ub0.Top && pbm == ub0.Bottom, "");

        // ── selection geometry ──────────────────────────────────────────────────────────────
        var runs = new TextRunService().GetPage(path, 0);
        Check("runs: the page builds", runs is not null, "");
        if (runs is null) return;
        string LineText(RunLine l) => string.Concat(runs.Chars.Skip(l.Start).Take(l.Count).Select(c => c.Value));
        var lines = runs.Lines.Select(l => (Line: l, Text: LineText(l))).ToList();
        string all = string.Join(" | ", lines.Select(l => l.Text));
        Check("runs: the upside-down words share one line", lines.Any(l => Sorted(l.Text) == Sorted("UPSIDEDOWN")), all);
        Check("runs: the mirrored words share one line", lines.Any(l => Sorted(l.Text) == Sorted("MIRRORTEXT")), all);
        foreach (var (line, text) in lines)
        {
            double h = line.Top - line.Bottom;
            Check($"runs: line \"{text}\" has a real height (selection quad not collapsed)", h > 1, $"{h:F1}");
            bool charsOk = true;
            for (int i = line.Start; i < line.End; i++)
                charsOk &= runs.Chars[i].Right - runs.Chars[i].Left > 1;
            Check($"runs: every char of \"{text}\" has a real width", charsOk, "");
        }
        foreach (var w in quarter.Concat(sideways))
        {
            var (_, b, _, t) = E(w);
            Check($"runs: quarter-turned \"{w.Text}\" has a line band covering its ink",
                  runs.Lines.Any(l => l.Top >= t - 0.01 && l.Bottom <= b + 0.01), $"ink [{b:F1},{t:F1}]");
        }
    }
}
