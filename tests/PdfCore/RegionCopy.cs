using PdfSharpCore.Drawing;
using TDPdf.Services;

/// <summary>
/// Pins the rectangle (region) text copy: a marquee drawn around a word on the RENDERED page must
/// pick that word out of PdfPig's text, on every quarter turn of a page whose CropBox does not
/// start at the origin.
/// </summary>
/// <remarks>
/// <c>MainWindow.ExtractTextFromRegion</c> maps the marquee with
/// <see cref="PdfPageGeometry.CanvasRectToTextFrame"/> — a scale and Y flip against PdfPig's own
/// page size — and NOT the user-space table the crop and redaction tools use. That looks like the
/// classic "ignores CropBox and /Rotate" bug and is not: PdfPig 0.1.14 reports words in the page
/// as displayed, crop origin subtracted and /Rotate applied, which is the view PDFium renders. The
/// "obvious" fix of routing the marquee through <see cref="PdfPageGeometry.CanvasRectToPdf"/> was
/// tried and this test caught it copying nothing on every one of these pages.
///
/// As in Geometry.cs, the renderer is the authority: the marquee is the INK PDFium draws for the
/// word, not a rectangle derived from the table being tested. And the second check pins PdfPig's
/// convention itself — the user-space mapping must MISS the word on a cropped page — so a PdfPig
/// upgrade that moved its words into user space fails here instead of silently breaking the copy.
/// </remarks>
internal static class RegionCopy
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nRegion copy: marquee on the render -> PdfPig words (cropped + rotated)");

        foreach (int rotate in new[] { 0, 90, 180, 270 })
        {
            string path = Path.Combine(tmp, $"regioncopy-{rotate}.pdf");
            {
                var d = new PdfSharpCore.Pdf.PdfDocument();
                var p = d.AddPage();   // 612 x 792
                using (var g = XGraphics.FromPdfPage(p))
                    g.DrawString("MARK", new XFont("Helvetica", 24), XBrushes.Black, 200, 300);
                var arr = new PdfSharpCore.Pdf.PdfArray();
                foreach (double v in new[] { 100.0, 200.0, 500.0, 700.0 })
                    arr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(v));
                p.Elements["/CropBox"] = arr;
                p.Rotate = rotate;   // after drawing: content stays in unrotated user space
                d.Save(path);
            }

            // The ink PDFium draws, in canvas pixels (1 px per point), padded the way a person
            // drags a marquee a little outside the word.
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
            if (minX > maxX) { Check($"/Rotate {rotate}: fixture renders ink", false, "blank render"); continue; }
            const double pad = 4;
            double mx = minX - pad, my = minY - pad, mw = maxX - minX + 1 + 2 * pad, mh = maxY - minY + 1 + 2 * pad;

            using var pig = UglyToad.PdfPig.PdfDocument.Open(path);
            var pigPage = pig.GetPage(1);
            var words = pigPage.GetWords().ToList();
            var region = PdfPageGeometry.CanvasRectToTextFrame(mx, my, mw, mh, rw, rh, pigPage.Width, pigPage.Height);
            var picked = words.Where(w => PdfPageGeometry.ContainsCenter(region,
                w.BoundingBox.Left, w.BoundingBox.Bottom, w.BoundingBox.Right, w.BoundingBox.Top)).ToList();

            // Letters, not the word: on a turned page PdfPig's word assembly splits the word into
            // letters (90/270) or hands it back reversed (180). That is text ORDERING, a separate
            // question from which glyphs a marquee takes — the geometry under test here — so the
            // assertion is that exactly MARK's four letters were taken, and nothing else.
            string letters = string.Concat(string.Concat(picked.Select(w => w.Text)).OrderBy(c => c));
            Check($"/Rotate {rotate} + offset CropBox: marquee around the rendered word copies it",
                  letters == "AKMR",
                  $"picked [{string.Join(", ", picked.Select(w => w.Text))}] from region "
                  + $"L={region.Left:F0} B={region.Bottom:F0} R={region.Right:F0} T={region.Top:F0}");

            // The mirror-image marquee — same size, the opposite corner of the canvas — must copy
            // nothing. A mapping that is a half turn out would pass the check above only by
            // accident, and fail this one.
            var mirror = PdfPageGeometry.CanvasRectToTextFrame(
                rw - mx - mw, rh - my - mh, mw, mh, rw, rh, pigPage.Width, pigPage.Height);
            bool mirrorEmpty = !words.Any(w => PdfPageGeometry.ContainsCenter(mirror,
                w.BoundingBox.Left, w.BoundingBox.Bottom, w.BoundingBox.Right, w.BoundingBox.Top));
            Check($"/Rotate {rotate} + offset CropBox: the opposite corner copies nothing", mirrorEmpty, "");

            // PdfPig's convention, pinned: the SAME marquee mapped to user space must not find the
            // word, because PdfPig's words are not in user space on a cropped page.
            using var sharp = PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import);
            var userSpace = PdfPageGeometry.CanvasRectToPdf(sharp.Pages[0], mx, my, mw, mh, rw, rh);
            bool userSpaceMisses = !words.Any(w => PdfPageGeometry.ContainsCenter(userSpace,
                w.BoundingBox.Left, w.BoundingBox.Bottom, w.BoundingBox.Right, w.BoundingBox.Top));
            Check($"/Rotate {rotate} + offset CropBox: PdfPig words are in the displayed frame, not user space",
                  userSpaceMisses, userSpaceMisses ? "" : "PdfPig now reports user space — revisit ExtractTextFromRegion and PdfRedaction");
        }

        // ContainsCenter is a centre test, not an overlap test: a marquee that clips the edge of
        // the next line's box must not take that line.
        var r = new PdfiumInterop.PdfRect(Left: 0, Bottom: 0, Right: 100, Top: 100);
        Check("ContainsCenter: a box centred inside is taken", PdfPageGeometry.ContainsCenter(r, 10, 10, 30, 30), "");
        Check("ContainsCenter: a box merely overlapping the edge is not",
              !PdfPageGeometry.ContainsCenter(r, 90, 80, 130, 95), "centre x = 110");
        Check("ContainsCenter: the boundary counts as inside", PdfPageGeometry.ContainsCenter(r, 90, 90, 110, 110), "");
    }
}
