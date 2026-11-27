using PdfSharpCore.Drawing;
using TDPdf.Services;

/// <summary>
/// The invisible OCR layer's font size, baseline and horizontal fit (OcrTextFit), and the layout the
/// searchable-PDF export builds from them, read back through PdfPig the way a viewer selects text.
/// </summary>
internal static class OcrTextFitTests
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nOCR text layer: size, baseline and horizontal fit");

        // Arial-like cell: ascent 905 + descent 212 per 1000 em.
        double size = OcrTextFit.FontSizeFor(11.17, 905, 212);
        Check("font size makes the ascent+descent cell fill the box", Math.Abs(size - 10) < 0.01, size.ToString("0.###"));
        Check("descent sign does not matter", OcrTextFit.FontSizeFor(11.17, 905, -212) == size, "");
        Check("unusable box or font falls back to the minimum size",
              OcrTextFit.FontSizeFor(0, 905, 212) == OcrTextFit.MinFontSize && OcrTextFit.FontSizeFor(10, 0, 0) == OcrTextFit.MinFontSize, "");
        Check("a tiny box never goes below the minimum size", OcrTextFit.FontSizeFor(0.2, 905, 212) == OcrTextFit.MinFontSize, "");

        double baseline = OcrTextFit.BaselineY(120, 10, -212);
        Check("baseline sits the descent above the box bottom", Math.Abs(baseline - 117.88) < 1e-9, baseline.ToString("0.###"));

        Check("horizontal scale matches the box width", Math.Abs(OcrTextFit.HScale(30, 20) - 1.5) < 1e-9, "");
        Check("horizontal scale is clamped low", OcrTextFit.HScale(1, 100) == OcrTextFit.MinHScale, "");
        Check("horizontal scale is clamped high", OcrTextFit.HScale(100, 1) == OcrTextFit.MaxHScale, "");
        Check("horizontal scale is 1 for unusable widths", OcrTextFit.HScale(0, 10) == 1 && OcrTextFit.HScale(10, 0) == 1, "");

        Check("a line box that contains the word is used", OcrTextFit.BoxFor(12, 18, 10, 20) == (10, 20), "");
        Check("a line box shorter than the word falls back to the word", OcrTextFit.BoxFor(10, 20, 12, 18) == (10, 20), "");
        Check("an empty line box falls back to the word", OcrTextFit.BoxFor(10, 20, 0, 0) == (10, 20), "");

        // Round trip: lay out two words on one line the way the exporter does, then read them back.
        string path = Path.Combine(tmp, "ocr-fit.pdf");
        const double pageH = 400;
        const double lineTop = 100, lineBottom = 122;               // the line box (points, y down)
        double expectedBaselineDown = 0;
        (string Text, double L, double R)[] words = { ("Hello", 40, 100), ("on", 112, 140) };
        {
            var doc = new PdfSharpCore.Pdf.PdfDocument();
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(400);
            page.Height = XUnit.FromPoint(pageH);
            using var gfx = XGraphics.FromPdfPage(page);
            var probe = new XFont("Helvetica", 10);
            double asc = probe.Metrics.Ascent * 1000.0 / probe.Metrics.UnitsPerEm;
            double desc = probe.Metrics.Descent * 1000.0 / probe.Metrics.UnitsPerEm;
            double fs = OcrTextFit.FontSizeFor(lineBottom - lineTop, asc, desc);
            var font = new XFont("Helvetica", fs);
            expectedBaselineDown = OcrTextFit.BaselineY(lineBottom, font.Size, desc);
            foreach (var (text, l, r) in words)
            {
                double b = expectedBaselineDown;
                double hs = OcrTextFit.HScale(r - l, gfx.MeasureString(text, font).Width);
                var st = gfx.Save();
                gfx.TranslateTransform(l, b);
                gfx.ScaleTransform(hs, 1);
                gfx.DrawString(text, font, XBrushes.Black, 0, 0, XStringFormats.BaseLineLeft);
                gfx.Restore(st);
            }
            doc.Save(path);
        }

        List<UglyToad.PdfPig.Content.Word> found;
        using (var pig = UglyToad.PdfPig.PdfDocument.Open(path))
            found = pig.GetPage(1).GetWords().ToList();
        Check("both words come back", found.Count == 2 && found[0].Text == "Hello" && found[1].Text == "on",
              string.Join("|", found.Select(w => w.Text)));
        if (found.Count == 2)
        {
            for (int i = 0; i < 2; i++)
            {
                var (_, l, r) = words[i];
                var bb = found[i].BoundingBox;
                Check($"word {i + 1} spans its box horizontally (+-2 pt)",
                      Math.Abs(bb.Left - l) < 2 && Math.Abs(bb.Right - r) < 2, $"{bb.Left:0.0}-{bb.Right:0.0} vs {l}-{r}");
                // Letters share the line's baseline: PdfPig y is up, so baseline = pageH - y_down.
                double by = found[i].Letters[0].StartBaseLine.Y;
                double expected = pageH - expectedBaselineDown;
                Check($"word {i + 1} sits on the shared line baseline (+-3 pt)", Math.Abs(by - expected) < 3, $"{by:0.0} vs {expected:0.0}");
            }
            Check("both words share one baseline",
                  Math.Abs(found[0].Letters[0].StartBaseLine.Y - found[1].Letters[0].StartBaseLine.Y) < 0.01, "");
        }
    }
}
