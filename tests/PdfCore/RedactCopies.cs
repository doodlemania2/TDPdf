using System.Text;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Annotations;
using TDPdf.Services;

/// <summary>
/// A redacted word that survives in a link address, in document JavaScript or in kept metadata
/// refuses the save, and the message says where. Each source is tested alone, on a document that
/// is otherwise clean, so a pass cannot come from one of the others.
/// </summary>
internal static class RedactCopies
{
    private const string Secret = "ZEBRAQUILL";

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nRedaction: link addresses, JavaScript and metadata");

        string clean = Path.Combine(tmp, "redact-copies-clean.pdf");
        Build(clean, _ => { });
        var rects = MarkRects(clean);

        Result("a clean document redacts", clean, rects, scrub: true, expectOk: true, mustMention: null, Check, tmp);

        string link = Path.Combine(tmp, "redact-copies-link.pdf");
        Build(link, (doc) =>
        {
            var annot = new PdfDictionary(doc);
            annot.Elements["/Type"] = new PdfName("/Annot");
            annot.Elements["/Subtype"] = new PdfName("/Link");
            annot.Elements["/Rect"] = new PdfRectangle(new XRect(50, 50, 80, 20));
            var action = new PdfDictionary(doc);
            action.Elements["/S"] = new PdfName("/URI");
            action.Elements["/URI"] = new PdfString("https://example.org/people/" + Secret.ToLowerInvariant() + "?x=1");
            annot.Elements["/A"] = action;
            var arr = new PdfArray(doc);
            arr.Elements.Add(annot);
            doc.Pages[1].Elements["/Annots"] = arr;
        });
        Result("link address", link, rects, scrub: true, expectOk: false, mustMention: "link address", Check, tmp);

        string encoded = Path.Combine(tmp, "redact-copies-link-enc.pdf");
        Build(encoded, (doc) =>
        {
            var annot = new PdfDictionary(doc);
            annot.Elements["/Type"] = new PdfName("/Annot");
            annot.Elements["/Subtype"] = new PdfName("/Link");
            annot.Elements["/Rect"] = new PdfRectangle(new XRect(50, 50, 80, 20));
            var action = new PdfDictionary(doc);
            action.Elements["/S"] = new PdfName("/URI");
            action.Elements["/URI"] = new PdfString("https://example.org/%5A%45%42%52%41%51%55%49%4C%4C");   // percent-encoded
            annot.Elements["/A"] = action;
            var arr = new PdfArray(doc);
            arr.Elements.Add(annot);
            doc.Pages[1].Elements["/Annots"] = arr;
        });
        Result("percent-encoded link address", encoded, rects, scrub: true, expectOk: false, mustMention: "link address", Check, tmp);

        string openJs = Path.Combine(tmp, "redact-copies-openjs.pdf");
        Build(openJs, (doc) =>
        {
            var action = new PdfDictionary(doc);
            action.Elements["/S"] = new PdfName("/JavaScript");
            action.Elements["/JS"] = new PdfString("app.alert('hello " + Secret + "');");
            doc.Internals.Catalog.Elements["/OpenAction"] = action;
        });
        Result("open-action JavaScript", openJs, rects, scrub: true, expectOk: false, mustMention: "JavaScript", Check, tmp);

        string nameJs = Path.Combine(tmp, "redact-copies-namejs.pdf");
        Build(nameJs, (doc) =>
        {
            var action = new PdfDictionary(doc);
            action.Elements["/S"] = new PdfName("/JavaScript");
            var script = new PdfDictionary(doc);
            doc.Internals.AddObject(script);
            script.CreateStream(Encoding.ASCII.GetBytes("var who = '" + Secret + "';"));
            action.Elements["/JS"] = script.Reference;
            var pairs = new PdfArray(doc);
            pairs.Elements.Add(new PdfString("init"));
            pairs.Elements.Add(action);
            var tree = new PdfDictionary(doc);
            tree.Elements["/Names"] = pairs;
            var names = new PdfDictionary(doc);
            names.Elements["/JavaScript"] = tree;
            doc.Internals.Catalog.Elements["/Names"] = names;
        });
        Result("document JavaScript name tree (stream)", nameJs, rects, scrub: true, expectOk: false, mustMention: "JavaScript", Check, tmp);

        string aaJs = Path.Combine(tmp, "redact-copies-aajs.pdf");
        Build(aaJs, (doc) =>
        {
            var action = new PdfDictionary(doc);
            action.Elements["/S"] = new PdfName("/JavaScript");
            action.Elements["/JS"] = new PdfString("this.info = '" + Secret + "';");
            var aa = new PdfDictionary(doc);
            aa.Elements["/WC"] = action;
            doc.Internals.Catalog.Elements["/AA"] = aa;
        });
        Result("document additional-actions JavaScript", aaJs, rects, scrub: true, expectOk: false, mustMention: "JavaScript", Check, tmp);

        string info = Path.Combine(tmp, "redact-copies-info.pdf");
        Build(info, doc => doc.Info.Title = "Report on " + Secret);
        Result("Info dictionary, metadata kept", info, rects, scrub: false, expectOk: false, mustMention: "document information", Check, tmp);
        Result("Info dictionary, metadata scrubbed is fine", info, rects, scrub: true, expectOk: true, mustMention: null, Check, tmp);

        string xmp = Path.Combine(tmp, "redact-copies-xmp.pdf");
        Build(xmp, doc =>
        {
            var meta = new PdfDictionary(doc);
            meta.Elements["/Type"] = new PdfName("/Metadata");
            meta.Elements["/Subtype"] = new PdfName("/XML");
            doc.Internals.AddObject(meta);
            meta.CreateStream(Encoding.UTF8.GetBytes("<x:xmpmeta><dc:title>Report on " + Secret + "</dc:title></x:xmpmeta>"));
            doc.Internals.Catalog.Elements["/Metadata"] = meta.Reference;
        });
        Result("XMP packet, metadata kept", xmp, rects, scrub: false, expectOk: false, mustMention: "XMP", Check, tmp);
        Result("XMP packet, metadata scrubbed is fine", xmp, rects, scrub: true, expectOk: true, mustMention: null, Check, tmp);

        // A link that does not mention a redacted word is left alone.
        string other = Path.Combine(tmp, "redact-copies-otherlink.pdf");
        Build(other, (doc) =>
        {
            var annot = new PdfDictionary(doc);
            annot.Elements["/Type"] = new PdfName("/Annot");
            annot.Elements["/Subtype"] = new PdfName("/Link");
            annot.Elements["/Rect"] = new PdfRectangle(new XRect(50, 50, 80, 20));
            var action = new PdfDictionary(doc);
            action.Elements["/S"] = new PdfName("/URI");
            action.Elements["/URI"] = new PdfString("https://example.org/appendix");
            annot.Elements["/A"] = action;
            var arr = new PdfArray(doc);
            arr.Elements.Add(annot);
            doc.Pages[1].Elements["/Annots"] = arr;
        });
        Result("an unrelated link is untouched", other, rects, scrub: true, expectOk: true, mustMention: null, Check, tmp);
    }

    private static void Result(string label, string src, Dictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> rects,
        bool scrub, bool expectOk, string? mustMention, Action<string, bool, string> Check, string tmp)
    {
        string dest = Path.Combine(tmp, "redact-copies-out-" + Guid.NewGuid().ToString("N") + ".pdf");
        var res = PdfRedaction.Apply(src, dest, new PdfRedaction.Request
        {
            RectsByPage = rects, RemovePartialOverlaps = true, ScrubMetadata = scrub,
        });
        bool ok = expectOk
            ? res.Ok && File.Exists(dest)
            : !res.Ok && !File.Exists(dest) && (res.Error ?? "").Contains(mustMention!, StringComparison.OrdinalIgnoreCase)
              && (res.Error ?? "").Contains("redacted text", StringComparison.OrdinalIgnoreCase);
        Check($"redaction copies: {label} {(expectOk ? "passes" : "refuses the save and names where")}", ok, res.Error ?? "(ok)");
    }

    private static Dictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> MarkRects(string src)
    {
        using var d = UglyToad.PdfPig.PdfDocument.Open(src);
        var box = d.GetPage(1).GetWords().First(w => w.Text.Contains(Secret)).BoundingBox;
        var mark = new PdfiumInterop.PdfRect(Left: box.Left - 6, Bottom: box.Bottom - 4, Right: box.Right + 6, Top: box.Top + 4);
        return new Dictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> { [0] = new[] { mark } };
    }

    private static void Build(string path, Action<PdfDocument> extra)
    {
        var doc = new PdfDocument();
        doc.AddPage();
        doc.AddPage();
        using (var g = XGraphics.FromPdfPage(doc.Pages[0]))
        {
            var f = new XFont("Helvetica", 24);
            g.DrawString(Secret, f, XBrushes.Black, 200, 300);
            g.DrawString("KEEPWORD", f, XBrushes.Black, 200, 600);
        }
        extra(doc);
        doc.Save(path);
    }
}
