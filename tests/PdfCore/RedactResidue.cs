using System.Text;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using TDPdf.Services;

/// <summary>
/// Redaction also clears the hidden copies: /PieceInfo, page thumbnails, structure-tree
/// descriptions and bookmark titles. Each is asserted against the BYTES of the output, and the
/// post-redaction verifier is shown to see each one on the unredacted source.
/// </summary>
internal static class RedactResidue
{
    private const string Secret = "ZEBRAQUILL";

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nRedaction: PieceInfo, thumbnails, structure descriptions, bookmarks");

        string src = Path.Combine(tmp, "redact-residue.pdf");
        Build(src, depth: 3, cyclic: true);

        double l, b, r, t;
        using (var d = UglyToad.PdfPig.PdfDocument.Open(src))
        {
            var box = d.GetPage(1).GetWords().First(w => w.Text.Contains(Secret)).BoundingBox;
            (l, b, r, t) = (box.Left, box.Bottom, box.Right, box.Top);
        }
        var mark = new PdfiumInterop.PdfRect(Left: l - 6, Bottom: b - 4, Right: r + 6, Top: t + 4);
        var rects = new Dictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> { [0] = new[] { mark } };

        var terms = PdfRedaction.CollectRemovedTerms(src, rects);
        Check("removed text is read from the source", terms.Any(x => x.Equals(Secret, StringComparison.OrdinalIgnoreCase)),
              $"[{string.Join(", ", terms)}]");
        Check("a word outside the mark is not collected", !terms.Any(x => x.Contains("KEEPWORD")), "");

        var before = PdfRedaction.FindResidue(src, rects, terms);
        Check("verifier sees PieceInfo, thumbnail, structure text and bookmark in the source",
              before.Count >= 5 && before.Any(x => x.Contains("thumbnail")) && before.Any(x => x.Contains("bookmark"))
              && before.Any(x => x.Contains("PieceInfo")) && before.Any(x => x.Contains("/ActualText")), $"[{string.Join("; ", before)}]");

        string dest = Path.Combine(tmp, "redact-residue-out.pdf");
        if (File.Exists(dest)) File.Delete(dest);
        var res = PdfRedaction.Apply(src, dest, new PdfRedaction.Request
        {
            RectsByPage = rects, RemovePartialOverlaps = true, ScrubMetadata = true,
        });
        Check("redaction succeeds and verifies clean", res.Ok, res.Error ?? "");
        if (!res.Ok || !File.Exists(dest)) return;

        string bytes = Encoding.Latin1.GetString(File.ReadAllBytes(dest));
        foreach (var gone in new[] { "SECRET-PIECE-DOC", "SECRET-PIECE-PAGE", "SECRET-THUMB", "SECRET-ACTUAL",
                                     "SECRET-ALT", "SECRET-EXPAND", "SECRET-TITLE", "SECRET-ANCESTOR",
                                     Secret, Secret.ToLowerInvariant() })
            Check($"\"{gone}\" is gone from the file", !bytes.Contains(gone), "");
        foreach (var keep in new[] { "KEEP-ALT", "SECRET-MENTION", "[redacted]" })
            Check($"\"{keep}\" survives", bytes.Contains(keep), "");
        using (var o = PdfSharpCore.Pdf.IO.PdfReader.Open(dest, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import))
        {
            var titles = o.Outlines.Cast<PdfSharpCore.Pdf.PdfOutline>().Select(x => x.Title).ToList();
            Check("untouched bookmark keeps its title", titles.Contains("Appendix"), $"[{string.Join("|", titles)}]");
            Check("marked bookmark is cleaned but kept", titles.Contains("Report on [redacted]"), $"[{string.Join("|", titles)}]");
        }
        Check("report counts each vector", res.Residue.PieceInfoRemoved == 2 && res.Residue.ThumbnailsRemoved == 1
              && res.Residue.StructElementsCleared >= 2 && res.Residue.BookmarkTitlesCleaned == 1,
              $"{res.Residue.PieceInfoRemoved}/{res.Residue.ThumbnailsRemoved}/{res.Residue.StructElementsCleared}/{res.Residue.BookmarkTitlesCleaned}");
        Check("verifier reports the output clean", PdfRedaction.FindResidue(dest, rects, terms).Count == 0, "");

        // A structure tree nested past the bound is refused, not half cleaned.
        string deep = Path.Combine(tmp, "redact-residue-deep.pdf");
        Build(deep, depth: 400, cyclic: false);
        string deepOut = Path.Combine(tmp, "redact-residue-deep-out.pdf");
        if (File.Exists(deepOut)) File.Delete(deepOut);
        var deepRes = PdfRedaction.Apply(deep, deepOut, new PdfRedaction.Request { RectsByPage = rects });
        Check("a too-deep structure tree is refused with a clear message",
              !deepRes.Ok && (deepRes.Error ?? "").Contains("structure tree") && !File.Exists(deepOut), deepRes.Error ?? "");
    }

    private static void Build(string path, int depth, bool cyclic)
    {
        var doc = new PdfDocument();
        var p1 = doc.AddPage();
        var p2 = doc.AddPage();
        using (var g = XGraphics.FromPdfPage(p1))
        {
            var f = new XFont("Helvetica", 24);
            g.DrawString(Secret, f, XBrushes.Black, 200, 300);
            g.DrawString("KEEPWORD", f, XBrushes.Black, 200, 600);
        }

        PdfDictionary Indirect(Action<PdfDictionary> fill)
        {
            var d = new PdfDictionary(doc);
            fill(d);
            doc.Internals.AddObject(d);
            return d;
        }

        // Private editor data: on the catalog and on the marked page.
        doc.Internals.Catalog.Elements["/PieceInfo"] =
            Indirect(d => d.Elements["/App"] = new PdfString("SECRET-PIECE-DOC"));
        p1.Elements["/PieceInfo"] = Indirect(d => d.Elements["/App"] = new PdfString("SECRET-PIECE-PAGE"));

        // A thumbnail: an image of the unredacted page.
        var thumb = Indirect(d =>
        {
            d.Elements["/Type"] = new PdfName("/XObject");
            d.Elements["/Subtype"] = new PdfName("/Image");
            d.Elements["/Width"] = new PdfInteger(1);
            d.Elements["/Height"] = new PdfInteger(1);
            d.Elements["/ColorSpace"] = new PdfName("/DeviceGray");
            d.Elements["/BitsPerComponent"] = new PdfInteger(8);
        });
        thumb.CreateStream(Encoding.ASCII.GetBytes("SECRET-THUMB"));
        p1.Elements["/Thumb"] = thumb.Reference;

        // Structure tree: Document > [P on page 1, P on page 2, P mentioning the text elsewhere].
        var onMarked = Indirect(d =>
        {
            d.Elements["/Type"] = new PdfName("/StructElem");
            d.Elements["/S"] = new PdfName("/P");
            d.Elements["/Pg"] = p1.Reference;
            d.Elements["/K"] = new PdfInteger(0);
            d.Elements["/ActualText"] = new PdfString("SECRET-ACTUAL");
            d.Elements["/Alt"] = new PdfString("SECRET-ALT");
            d.Elements["/E"] = new PdfString("SECRET-EXPAND");
            d.Elements["/T"] = new PdfString("SECRET-TITLE");
        });
        var onOther = Indirect(d =>
        {
            d.Elements["/Type"] = new PdfName("/StructElem");
            d.Elements["/S"] = new PdfName("/P");
            d.Elements["/Pg"] = p2.Reference;
            d.Elements["/K"] = new PdfInteger(0);
            d.Elements["/Alt"] = new PdfString("KEEP-ALT");
        });
        var mention = Indirect(d =>
        {
            d.Elements["/Type"] = new PdfName("/StructElem");
            d.Elements["/S"] = new PdfName("/Figure");
            d.Elements["/Pg"] = p2.Reference;
            d.Elements["/K"] = new PdfInteger(1);
            d.Elements["/Alt"] = new PdfString("a photo of " + Secret.ToLowerInvariant() + " SECRET-MENTION");
        });
        var kids = new PdfArray(doc);
        kids.Elements.Add(onMarked.Reference);
        kids.Elements.Add(onOther.Reference);
        kids.Elements.Add(mention.Reference);
        var top = Indirect(d =>
        {
            d.Elements["/Type"] = new PdfName("/StructElem");
            d.Elements["/S"] = new PdfName("/Document");
            d.Elements["/Alt"] = new PdfString("SECRET-ANCESTOR");
            d.Elements["/K"] = kids;
        });
        if (cyclic) kids.Elements.Add(top.Reference);   // a malformed back-reference

        // Optionally bury the whole thing under a long chain.
        PdfDictionary rootKid = top;
        for (int i = 0; i < depth - 1 && !cyclic; i++)
        {
            var wrapper = Indirect(d =>
            {
                d.Elements["/Type"] = new PdfName("/StructElem");
                d.Elements["/S"] = new PdfName("/Div");
                d.Elements["/K"] = rootKid.Reference;
            });
            rootKid = wrapper;
        }
        var root = Indirect(d =>
        {
            d.Elements["/Type"] = new PdfName("/StructTreeRoot");
            d.Elements["/K"] = rootKid.Reference;
        });
        doc.Internals.Catalog.Elements["/StructTreeRoot"] = root.Reference;

        // Bookmarks: one naming the secret, one not.
        doc.Outlines.Add("Report on " + Secret, p1, true);
        doc.Outlines.Add("Appendix", p2, true);

        doc.Save(path);
    }
}
