using System.Text;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using TDPdf.Services;

/// <summary>
/// Redaction proves the images under a mark are gone from the file (not just from the page), prunes
/// the resources the removed content used to be drawn through, and removes attached files.
/// </summary>
internal static class RedactHidden
{
    private const string ImgInside = "inside";
    private const string ImgOutside = "outside";
    private const string ImgFormed = "formed";

    private static readonly Dictionary<string, string> Needles = new();

    /// <summary>
    /// A deterministic, incompressible payload, stored Flate-compressed so PDFium carries the
    /// stream through byte for byte. The needle is a slice of the COMPRESSED bytes, which is what
    /// appears in the file; the control check below proves it is findable before it is asserted gone.
    /// </summary>
    private static byte[] Compressed(string label)
    {
        var rng = new Random(label.GetHashCode() ^ 12345 + label.Length);
        var raw = new byte[64];
        rng.NextBytes(raw);
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        var c = ms.ToArray();
        Needles[label] = Encoding.Latin1.GetString(c, 8, 20);
        return c;
    }

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nRedaction: images under a mark, unused resources, attached files");
        Images(Check, tmp);
        StraddlingForm(Check, tmp);
        Names(Check);
        Prune(Check, tmp);
        Rotated(Check, tmp);
        Attachments(Check, tmp);
    }

    // ── builders ───────────────────────────────────────────────────────────────────────

    private static PdfDictionary Indirect(PdfDocument doc, Action<PdfDictionary> fill)
    {
        var d = new PdfDictionary(doc);
        fill(d);
        doc.Internals.AddObject(d);
        return d;
    }

    private static PdfDictionary Image(PdfDocument doc, string payload) => Indirect(doc, d =>
    {
        d.Elements["/Type"] = new PdfName("/XObject");
        d.Elements["/Subtype"] = new PdfName("/Image");
        d.Elements["/Width"] = new PdfInteger(8);
        d.Elements["/Height"] = new PdfInteger(8);
        d.Elements["/ColorSpace"] = new PdfName("/DeviceGray");
        d.Elements["/BitsPerComponent"] = new PdfInteger(8);
        d.Elements["/Filter"] = new PdfName("/FlateDecode");
        d.CreateStream(Compressed(payload));
    });

    private static void SetContent(PdfDocument doc, PdfPage page, string content)
    {
        var c = Indirect(doc, d => d.CreateStream(Encoding.ASCII.GetBytes(content)));
        page.Elements["/Contents"] = c.Reference;
    }

    private static PdfDictionary Dict(PdfDocument doc, params (string Key, PdfItem Value)[] items)
    {
        var d = new PdfDictionary(doc);
        foreach (var (k, v) in items) d.Elements[k] = v;
        return d;
    }

    private static Dictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> Mark(double l, double b, double r, double t) =>
        new() { [0] = new[] { new PdfiumInterop.PdfRect(l, b, r, t) } };

    private static bool Has(string path, string needle) =>
        Encoding.Latin1.GetString(File.ReadAllBytes(path)).Contains(needle);

    private static int ImageCount(string path)
    {
        using var d = UglyToad.PdfPig.PdfDocument.Open(path);
        return d.GetPage(1).GetImages().Count();
    }

    // ── images ─────────────────────────────────────────────────────────────────────────

    private static void Images(Action<string, bool, string> Check, string tmp)
    {
        // Page 1: image A (inside the mark), image B (outside), a form holding image C (inside a second mark).
        string src = Path.Combine(tmp, "hidden-images.pdf");
        {
            var doc = new PdfDocument();
            var page = doc.AddPage();
            var a = Image(doc, ImgInside);
            var b = Image(doc, ImgOutside);
            var c = Image(doc, ImgFormed);
            var form = Indirect(doc, d =>
            {
                d.Elements["/Type"] = new PdfName("/XObject");
                d.Elements["/Subtype"] = new PdfName("/Form");
                d.Elements["/BBox"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(60), new PdfInteger(80));
                d.Elements["/Resources"] = Dict(doc, ("/XObject", Dict(doc, ("/ImC", c.Reference))));
                d.CreateStream(Encoding.ASCII.GetBytes("q 60 0 0 80 0 0 cm /ImC Do Q"));
            });
            page.Elements["/Resources"] = Dict(doc,
                ("/XObject", Dict(doc, ("/ImA", a.Reference), ("/ImB", b.Reference), ("/FmC", form.Reference))));
            SetContent(doc, page,
                "q 60 0 0 80 200 300 cm /ImA Do Q\n" +
                "q 60 0 0 80 200 600 cm /ImB Do Q\n" +
                "q 1 0 0 1 400 300 cm /FmC Do Q\n");
            doc.Save(src);
        }
        Check("source holds all three payloads", Has(src, Needles[ImgInside]) && Has(src, Needles[ImgOutside]) && Has(src, Needles[ImgFormed]), "");

        var rects = new Dictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>>
        {
            [0] = new[]
            {
                new PdfiumInterop.PdfRect(190, 290, 270, 390),    // around image A
                new PdfiumInterop.PdfRect(390, 290, 470, 390),    // around the form (image C)
            },
        };

        var before = PdfRedaction.FindSurvivingImages(src, rects, Array.Empty<int>());
        Check("the image check sees images under the marks in the source", before.Count == 1 && before[0] == 0, string.Join(",", before));
        Check("the image check skips a rasterised page",
              PdfRedaction.FindSurvivingImages(src, rects, new[] { 0 }).Count == 0, "");
        Check("the image check ignores an image outside every mark",
              PdfRedaction.FindSurvivingImages(src, Mark(190, 100, 270, 200), Array.Empty<int>()).Count == 0, "");

        string dest = Path.Combine(tmp, "hidden-images-out.pdf");
        if (File.Exists(dest)) File.Delete(dest);
        var res = PdfRedaction.Apply(src, dest, new PdfRedaction.Request
        {
            RectsByPage = rects, RemovePartialOverlaps = true, ScrubMetadata = false,
        });
        Check("redaction with images under the marks succeeds", res.Ok && File.Exists(dest), res.Error ?? "");
        if (!res.Ok || !File.Exists(dest)) return;
        Check("an image inside a mark is gone from the file bytes", !Has(dest, Needles[ImgInside]), "");
        Check("an image inside a form inside a mark is gone from the file bytes", !Has(dest, Needles[ImgFormed]), "");
        Check("an image outside every mark survives, byte for byte", Has(dest, Needles[ImgOutside]) && ImageCount(dest) == 1, $"{ImageCount(dest)} images");
        Check("the output passes the image check", PdfRedaction.FindSurvivingImages(dest, rects, Array.Empty<int>()).Count == 0, "");
    }

    /// <summary>A form holding two images, placed by a matrix, with a mark over only one of them.</summary>
    private static void StraddlingForm(Action<string, bool, string> Check, string tmp)
    {
        string src = Path.Combine(tmp, "hidden-form-straddle.pdf");
        {
            var doc = new PdfDocument();
            var page = doc.AddPage();
            var c1 = Image(doc, ImgFormed);
            var c2 = Image(doc, ImgOutside);
            var form = Indirect(doc, d =>
            {
                d.Elements["/Type"] = new PdfName("/XObject");
                d.Elements["/Subtype"] = new PdfName("/Form");
                d.Elements["/BBox"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(200), new PdfInteger(80));
                d.Elements["/Resources"] = Dict(doc, ("/XObject", Dict(doc, ("/L", c1.Reference), ("/R", c2.Reference))));
                d.CreateStream(Encoding.ASCII.GetBytes("q 60 0 0 80 0 0 cm /L Do Q q 60 0 0 80 140 0 cm /R Do Q"));
            });
            page.Elements["/Resources"] = Dict(doc, ("/XObject", Dict(doc, ("/Fm", form.Reference))));
            SetContent(doc, page, "q 1 0 0 1 300 300 cm /Fm Do Q");
            doc.Save(src);
        }
        string dest = Path.Combine(tmp, "hidden-form-straddle-out.pdf");
        if (File.Exists(dest)) File.Delete(dest);
        var res = PdfRedaction.Apply(src, dest, new PdfRedaction.Request
        {
            RectsByPage = Mark(290, 290, 370, 390), RemovePartialOverlaps = true, ScrubMetadata = true,
        });
        bool refused = !res.Ok && !File.Exists(dest) && (res.Error ?? "").Contains("image", StringComparison.OrdinalIgnoreCase)
                       && (res.Error ?? "").Contains("page 1");
        bool removed = res.Ok && File.Exists(dest) && !Has(dest, Needles[ImgFormed]);
        Check("an image in a placed form that the engine cannot clear is refused naming the page, never written",
              refused || removed, res.Error ?? "(ok)");
    }

    // ── name scanning ──────────────────────────────────────────────────────────────────

    private static void Names(Action<string, bool, string> Check)
    {
        var n = PdfRedaction.ScanNames(Encoding.Latin1.GetBytes(
            "/F1 12 Tf (not a /Fake name \\) still string) Tj % /Comment\n" +
            "BI /W 1 /H 1 /CS /Cs1 ID \x01(\x02/Hidden EI\nQ /Im#31 Do <2F4865> Tj [(a)] TJ /Last gs"));
        Check("name scan finds names in operands", n.Contains("/F1") && n.Contains("/Last"), string.Join(",", n));
        Check("name scan decodes #xx escapes", n.Contains("/Im1"), string.Join(",", n));
        Check("name scan reads inline image dictionary names", n.Contains("/Cs1"), string.Join(",", n));
        Check("name scan ignores strings, comments and inline image data",
              !n.Contains("/Fake") && !n.Contains("/Comment") && !n.Contains("/Hidden"), string.Join(",", n));
    }

    // ── pruning ────────────────────────────────────────────────────────────────────────

    private static void Prune(Action<string, bool, string> Check, string tmp)
    {
        // Page 1 (marked) draws ImUsed and has ImGone, FGone and GsGone in its resources that nothing draws.
        // Page 2 is unmarked and shares nothing; page 3 shares page 1's resources and draws ImShared.
        string src = Path.Combine(tmp, "hidden-prune.pdf");
        {
            var doc = new PdfDocument();
            var p1 = doc.AddPage(); var p2 = doc.AddPage(); var p3 = doc.AddPage();
            var used = Image(doc, ImgOutside);
            var gone = Image(doc, ImgInside);
            var shared = Image(doc, ImgFormed);
            var res = Dict(doc, ("/XObject", Dict(doc, ("/ImUsed", used.Reference), ("/ImGone", gone.Reference), ("/ImShared", shared.Reference))),
                                ("/ExtGState", Dict(doc, ("/GsGone", Dict(doc, ("/CA", new PdfReal(0.5)))))));
            doc.Internals.AddObject(res);
            p1.Elements["/Resources"] = res.Reference;
            p3.Elements["/Resources"] = res.Reference;
            SetContent(doc, p1, "q 60 0 0 80 100 100 cm /ImUsed Do Q");
            SetContent(doc, p3, "q 60 0 0 80 100 100 cm /ImShared Do Q");
            doc.Save(src);
        }
        string dest = Path.Combine(tmp, "hidden-prune-out.pdf");
        if (File.Exists(dest)) File.Delete(dest);
        bool ok = PdfRedaction.TryPruneResources(src, dest, Mark(0, 0, 50, 50), Array.Empty<int>(), out int pruned, out string? err);
        Check("pruning succeeds", ok && File.Exists(dest), err ?? "");
        if (!ok || !File.Exists(dest)) return;
        Check("an image nothing draws is pruned from the file", !Has(dest, Needles[ImgInside]) && pruned >= 2, $"pruned {pruned}");
        Check("an unused graphics state is pruned", !Has(dest, "GsGone"), "");
        Check("an image the page draws is kept", Has(dest, Needles[ImgOutside]), "");
        Check("an image an unmarked page sharing the resources draws is kept", Has(dest, Needles[ImgFormed]), "");

        string untouched = Path.Combine(tmp, "hidden-prune-none.pdf");
        bool ok2 = PdfRedaction.TryPruneResources(src, untouched, Mark(0, 0, 50, 50), new[] { 0 }, out int none, out _);
        Check("a rasterised page is left to the rasteriser", ok2 && none == 0 && !File.Exists(untouched), $"{none}");
    }

    // ── rotation ───────────────────────────────────────────────────────────────────────

    private static void Rotated(Action<string, bool, string> Check, string tmp)
    {
        foreach (int rot in new[] { 90, 180, 270 })
        {
            string src = Path.Combine(tmp, $"hidden-rot{rot}.pdf");
            var doc = new PdfDocument();
            var page = doc.AddPage();
            page.Rotate = rot;
            var a = Image(doc, ImgInside);
            page.Elements["/Resources"] = Dict(doc, ("/XObject", Dict(doc, ("/ImA", a.Reference))));
            SetContent(doc, page, "q 60 0 0 80 200 300 cm /ImA Do Q");
            doc.Save(src);
            Check($"rotate {rot}: an image under a mark is found",
                  PdfRedaction.FindSurvivingImages(src, Mark(190, 290, 270, 390), Array.Empty<int>()).Count == 1, "");
            Check($"rotate {rot}: a mark elsewhere does not flag it",
                  PdfRedaction.FindSurvivingImages(src, Mark(190, 600, 270, 700), Array.Empty<int>()).Count == 0, "");
        }
    }

    // ── attachments ────────────────────────────────────────────────────────────────────

    private static PdfDictionary FileSpec(PdfDocument doc, string name, string payload) => Indirect(doc, fs =>
    {
        fs.Elements["/Type"] = new PdfName("/Filespec");
        fs.Elements["/F"] = new PdfString(name);
        var ef = Indirect(doc, s =>
        {
            s.Elements["/Type"] = new PdfName("/EmbeddedFile");
            s.CreateStream(Encoding.ASCII.GetBytes(payload));
        });
        fs.Elements["/EF"] = Dict(doc, ("/F", ef.Reference));
    });

    private static void Attachments(Action<string, bool, string> Check, string tmp)
    {
        string src = Path.Combine(tmp, "hidden-attach.pdf");
        {
            var doc = new PdfDocument();
            var p1 = doc.AddPage();
            var p2 = doc.AddPage();
            using (var g = XGraphics.FromPdfPage(p1))
            {
                g.DrawString("ZEBRAQUILL", new XFont("Helvetica", 24), XBrushes.Black, 200, 300);
            }

            // 1. Name tree.
            var fs1 = FileSpec(doc, "original.pdf", "ATTACH-SECRET-TREE");
            var names = Dict(doc, ("/Names", new PdfArray(doc, new PdfString("original.pdf"), fs1.Reference)));
            doc.Internals.Catalog.Elements["/Names"] = Dict(doc, ("/EmbeddedFiles", names));

            // 2. File attachment annotation on the UNMARKED page.
            var fs2 = FileSpec(doc, "note.txt", "ATTACH-SECRET-ANNOT");
            var annot = Indirect(doc, a =>
            {
                a.Elements["/Type"] = new PdfName("/Annot");
                a.Elements["/Subtype"] = new PdfName("/FileAttachment");
                a.Elements["/Rect"] = new PdfArray(doc, new PdfInteger(10), new PdfInteger(10), new PdfInteger(30), new PdfInteger(30));
                a.Elements["/FS"] = fs2.Reference;
            });
            p2.Elements["/Annots"] = new PdfArray(doc, annot.Reference);

            // 3. Associated files on the catalog and a page.
            var fs3 = FileSpec(doc, "assoc.xml", "ATTACH-SECRET-AFCAT");
            doc.Internals.Catalog.Elements["/AF"] = new PdfArray(doc, fs3.Reference);
            var fs4 = FileSpec(doc, "page.xml", "ATTACH-SECRET-AFPAGE");
            p2.Elements["/AF"] = new PdfArray(doc, fs4.Reference);
            doc.Save(src);
        }
        var rects = new Dictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>>();
        {
            using var d = UglyToad.PdfPig.PdfDocument.Open(src);
            var box = d.GetPage(1).GetWords().First(w => w.Text.Contains("ZEBRAQUILL")).BoundingBox;
            rects[0] = new[] { new PdfiumInterop.PdfRect(box.Left - 6, box.Bottom - 4, box.Right + 6, box.Top + 4) };
        }

        var planted = PdfRedaction.FindAttachments(src);
        Check("the attachment check flags the name tree, annotation and /AF in the source",
              planted.Any(x => x.Contains("embedded-files")) && planted.Any(x => x.Contains("file attachment"))
              && planted.Any(x => x.Contains("/AF on the document")) && planted.Any(x => x.Contains("/AF on page 2"))
              && planted.Any(x => x.Contains("embedded file stream")), string.Join("; ", planted));

        string dest = Path.Combine(tmp, "hidden-attach-out.pdf");
        if (File.Exists(dest)) File.Delete(dest);
        var res = PdfRedaction.Apply(src, dest, new PdfRedaction.Request
        {
            RectsByPage = rects, RemovePartialOverlaps = true, ScrubMetadata = true,
        });
        Check("redaction with attachments succeeds", res.Ok && File.Exists(dest), res.Error ?? "");
        if (!res.Ok || !File.Exists(dest)) return;
        foreach (var gone in new[] { "ATTACH-SECRET-TREE", "ATTACH-SECRET-ANNOT", "ATTACH-SECRET-AFCAT", "ATTACH-SECRET-AFPAGE", "EmbeddedFile", "/FileAttachment" })
            Check($"\"{gone}\" is gone from the file", !Has(dest, gone), "");
        Check("the result counts the attached files removed", res.AttachmentsRemoved == 4, $"{res.AttachmentsRemoved}");
        Check("the attachment check passes on the output", PdfRedaction.FindAttachments(dest).Count == 0, "");

        // A planted /AF on the finished output is flagged.
        string plant = Path.Combine(tmp, "hidden-attach-plant.pdf");
        {
            var doc = PdfSharpCore.Pdf.IO.PdfReader.Open(dest, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);
            doc.Pages[0].Elements["/AF"] = new PdfArray(doc);
            doc.Save(plant);
        }
        Check("a planted /AF on a page is flagged", PdfRedaction.FindAttachments(plant).Any(x => x.Contains("page 1")), "");
    }
}
