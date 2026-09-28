using PdfSharpCore.Drawing;
using TDPdf.Services;

/// <summary>
/// Pins the meeting point of PdfPig's text frame and PDF user space, on pages where the two are
/// NOT the same: an offset CropBox at every quarter turn.
/// </summary>
/// <remarks>
/// Two things depend on it and neither may be wrong on a cropped or turned page:
///
///   * <see cref="PdfRedaction.FindSurvivingText"/>, the post-redaction verification. It reads the
///     output's words through PdfPig and the redaction rectangles are PDF user space. Comparing
///     the two raw — as it once did, under a remark that said there was "nothing to get wrong" —
///     looks in the wrong place on these pages, so it can report a clean redaction over text that
///     is still there. That is the check that has to fail loudly, so (b) below runs it against a
///     file the word was never removed from.
///   * "Redact matches", which turns PdfPig search hits into redaction marks. (c) maps a hit
///     through <see cref="PdfPageGeometry.TextFrameToPdf(PdfPageGeometry.PageBox, int, PdfiumInterop.PdfRect)"/>
///     onto the canvas and asserts it lands on the ink PDFium draws for the word.
///
/// As in Geometry.cs and RegionCopy.cs, the renderer is the authority: the word's user-space
/// rectangle is the INK PDFium draws for it, mapped back through the canvas table the app's own
/// dragged marks use — not a rectangle derived from the conversion being tested.
/// </remarks>
internal static class RedactVerify
{
    private const string Word = "MARK";
    private const string Other = "HELLO";

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nRedaction verification: PdfPig text frame vs user space (cropped + rotated)");

        // Two crops: one inside the MediaBox, and one hanging off it on two sides, which PDFium
        // clips to the MediaBox (VisibleBox mirrors that). The second is what shows whether PdfPig's
        // frame starts at the CLIPPED box too.
        var crops = new (string Name, double[] Box)[]
        {
            ("offset CropBox", new[] { 100.0, 200.0, 500.0, 700.0 }),
            ("CropBox past the MediaBox", new[] { -50.0, 150.0, 480.0, 900.0 }),
        };

        foreach (var (cropName, cropBox) in crops)
        foreach (int rotate in new[] { 0, 90, 180, 270 })
        {
            string label = $"/Rotate {rotate} + {cropName}";
            string src = Path.Combine(tmp, $"redactverify-{rotate}-{cropBox[0]}.pdf");
            {
                var d = new PdfSharpCore.Pdf.PdfDocument();
                var p = d.AddPage();   // 612 x 792
                using (var g = XGraphics.FromPdfPage(p))
                {
                    var f = new XFont("Helvetica", 24);
                    g.DrawString(Word, f, XBrushes.Black, 200, 300);
                    // A second run inside the crop, well away from the first: it must survive the
                    // redaction and must not be reported as a survivor.
                    g.DrawString(Other, f, XBrushes.Black, 300, 520);
                }
                var arr = new PdfSharpCore.Pdf.PdfArray();
                foreach (double v in cropBox) arr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(v));
                p.Elements["/CropBox"] = arr;
                p.Rotate = rotate;   // after drawing: content stays in unrotated user space
                d.Save(src);
            }

            // The ink of the target word alone, from a PDFium render of the same page without the
            // other run.
            var ink = InkOfWordOnly(tmp, rotate, cropBox);
            if (ink is null) { Check($"{label}: fixture renders ink", false, "blank render"); continue; }
            var (ix, iy, iw, ih, rw, rh) = ink.Value;

            using var sharp = PdfSharpCore.Pdf.IO.PdfReader.Open(src, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import);
            var page = sharp.Pages[0];
            const double pad = 4;
            var userRect = PdfPageGeometry.CanvasRectToPdf(page, ix - pad, iy - pad, iw + 2 * pad, ih + 2 * pad, rw, rh);
            var rects = new Dictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> { [0] = new[] { userRect } };

            // (b) The check that must never report clean over text that is there: verify the SOURCE,
            //     from which nothing was removed.
            var survivors = PdfRedaction.FindSurvivingText(src, rects);
            string found = string.Concat(string.Concat(survivors).OrderBy(c => c));
            Check($"{label}: verification sees the word still inside its rectangle",
                  found == "AKMR",
                  $"survivors [{string.Join(", ", survivors)}] for rect "
                  + $"L={userRect.Left:F0} B={userRect.Bottom:F0} R={userRect.Right:F0} T={userRect.Top:F0}");

            // (a) The real pipeline over the same rectangle: the word goes, the other run stays, and
            //     the verification on the finished file agrees.
            string dest = Path.Combine(tmp, $"redactverify-{rotate}-{cropBox[0]}-out.pdf");
            if (File.Exists(dest)) File.Delete(dest);
            var res = PdfRedaction.Apply(src, dest, new PdfRedaction.Request
            {
                RectsByPage = rects,
                RemovePartialOverlaps = true,
                ScrubMetadata = true,
            });
            Check($"{label}: redaction succeeds and verifies clean", res.Ok,
                  res.Error ?? "" + (res.Survivors.Count > 0 ? $" survivors [{string.Join(", ", res.Survivors)}]" : ""));
            if (res.Ok && File.Exists(dest))
            {
                using var pig = UglyToad.PdfPig.PdfDocument.Open(dest);
                string text = pig.GetPage(1).Text;
                Check($"{label}: the word's letters are gone from the output",
                      !text.Any(c => "MARK".Contains(c)), $"text \"{text}\"");
                Check($"{label}: the other run survives",
                      string.Concat(text.Where(char.IsLetter).OrderBy(c => c)) == "EHLLO", $"text \"{text}\"");
                Check($"{label}: verification on the output reports clean",
                      PdfRedaction.FindSurvivingText(dest, rects).Count == 0, "");
            }

            // (c) A PdfPig search hit, turned into a redaction mark the way "Redact matches" does it
            //     — text frame -> user space -> canvas — must land on the ink of the word.
            using (var pig = UglyToad.PdfPig.PdfDocument.Open(src))
            {
                var letters = pig.GetPage(1).GetWords().Where(w => w.Text.All(c => Word.Contains(c))).ToList();
                double l = letters.Min(w => Math.Min(w.BoundingBox.Left, w.BoundingBox.Right));
                double r = letters.Max(w => Math.Max(w.BoundingBox.Left, w.BoundingBox.Right));
                double b = letters.Min(w => Math.Min(w.BoundingBox.Bottom, w.BoundingBox.Top));
                double t = letters.Max(w => Math.Max(w.BoundingBox.Bottom, w.BoundingBox.Top));
                var hit = new PdfiumInterop.PdfRect(Left: l, Bottom: b, Right: r, Top: t);
                var user = PdfPageGeometry.TextFrameToPdf(page, hit);
                var (mx, my, mw, mh) = PdfPageGeometry.PdfRectToCanvas(page, user, rw, rh);
                double inkCx = ix + iw / 2.0, inkCy = iy + ih / 2.0;
                bool centred = inkCx >= mx && inkCx <= mx + mw && inkCy >= my && inkCy <= my + mh;
                double overlap = Overlap(mx, my, mw, mh, ix, iy, iw, ih);
                Check($"{label}: a search hit mapped to a mark lands on the rendered word",
                      centred && overlap >= 0.8,
                      $"mark ({mx},{my} {mw}x{mh}) ink ({ix},{iy} {iw}x{ih}) covers {overlap:P0} of the ink");

                // The mapping "Redact matches" used before: the PdfPig box taken as user space. On
                // these pages it must MISS the word — that is the defect, pinned, so a PdfPig
                // upgrade that moved words into user space fails here rather than silently
                // doubling the conversion.
                var (ox, oy, ow, oh) = PdfPageGeometry.PdfRectToCanvas(page, hit, rw, rh);
                Check($"{label}: the raw PdfPig box, taken as user space, misses the word",
                      Overlap(ox, oy, ow, oh, ix, iy, iw, ih) < 0.2,
                      $"raw mark ({ox},{oy} {ow}x{oh}) ink ({ix},{iy} {iw}x{ih})");

                // And the round trip back into the text frame is the identity.
                var back = PdfPageGeometry.PdfToTextFrame(page, user);
                bool same = Math.Abs(back.Left - hit.Left) < 1e-6 && Math.Abs(back.Right - hit.Right) < 1e-6
                         && Math.Abs(back.Bottom - hit.Bottom) < 1e-6 && Math.Abs(back.Top - hit.Top) < 1e-6;
                Check($"{label}: text frame -> user space -> text frame is the identity", same,
                      $"L={back.Left:F2} B={back.Bottom:F2} R={back.Right:F2} T={back.Top:F2}");
            }
        }
    }

    /// <summary>
    /// The ink bounding box of <see cref="Word"/> alone: the fixture is re-drawn without the other
    /// run, with the same crop and turn, and rendered. Returns canvas pixels (1 px per point).
    /// </summary>
    private static (int X, int Y, int W, int H, int Rw, int Rh)? InkOfWordOnly(
        string tmp, int rotate, double[] cropBox)
    {
        string path = Path.Combine(tmp, $"redactverify-ink-{rotate}-{cropBox[0]}.pdf");
        {
            var d = new PdfSharpCore.Pdf.PdfDocument();
            var p = d.AddPage();
            using (var g = XGraphics.FromPdfPage(p))
                g.DrawString(Word, new XFont("Helvetica", 24), XBrushes.Black, 200, 300);
            var arr = new PdfSharpCore.Pdf.PdfArray();
            foreach (double v in cropBox) arr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(v));
            p.Elements["/CropBox"] = arr;
            p.Rotate = rotate;
            d.Save(path);
        }
        var (bgra, rw, rh) = Geometry.RenderFirstPage(path);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int i = (y * rw + x) * 4;
                if (bgra[i] > 200 && bgra[i + 1] > 200 && bgra[i + 2] > 200) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        if (minX > maxX) return null;
        return (minX, minY, maxX - minX + 1, maxY - minY + 1, rw, rh);
    }

    /// <summary>The fraction of rectangle B covered by rectangle A.</summary>
    private static double Overlap(double ax, double ay, double aw, double ah, double bx, double by, double bw, double bh)
    {
        double w = Math.Min(ax + aw, bx + bw) - Math.Max(ax, bx);
        double h = Math.Min(ay + ah, by + bh) - Math.Max(ay, by);
        return w <= 0 || h <= 0 || bw * bh <= 0 ? 0 : w * h / (bw * bh);
    }
}
