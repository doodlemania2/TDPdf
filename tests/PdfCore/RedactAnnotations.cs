using System.Text;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using TDPdf.Services;

/// <summary>
/// Redaction removes annotations and form values under a mark, not only page content.
/// </summary>
/// <remarks>
/// A comment, a link or a filled field sitting over a redacted name is not page content, so the
/// PDFium object pass never sees it. The fixture puts one of each under the mark, plus the things
/// that repeat an annotation's text elsewhere (its popup, a reply) and a field whose other widget
/// lies outside the mark, and checks the secret strings are gone from the BYTES of the output, not
/// merely from the structure. An unrelated comment and field elsewhere must survive untouched.
///
/// Run on a turned, cropped page too: annotation rectangles are user space, as the marks are, so
/// no conversion is needed and a mistaken one would show up here.
/// </remarks>
internal static class RedactAnnotations
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nRedaction: annotations and form fields under a mark");

        foreach (int rotate in new[] { 0, 90 })
        {
            string label = $"/Rotate {rotate}";
            string src = Path.Combine(tmp, $"redact-annots-{rotate}.pdf");
            Build(src, rotate);

            var mark = new PdfiumInterop.PdfRect(Left: 150, Bottom: 400, Right: 400, Top: 560);
            var rects = new Dictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> { [0] = new[] { mark } };

            // The check must see them on the unredacted source, or a clean result means nothing.
            var before = PdfRedaction.FindSurvivingAnnotations(src, rects);
            Check($"{label}: verification sees the annotations and fields under the mark",
                  before.Count >= 4, $"[{string.Join(", ", before)}]");

            string dest = Path.Combine(tmp, $"redact-annots-{rotate}-out.pdf");
            if (File.Exists(dest)) File.Delete(dest);
            var res = PdfRedaction.Apply(src, dest, new PdfRedaction.Request
            {
                RectsByPage = rects,
                RemovePartialOverlaps = true,
                ScrubMetadata = true,
            });
            Check($"{label}: redaction succeeds and verifies clean", res.Ok,
                  (res.Error ?? "") + $" survivors [{string.Join(", ", res.Survivors)}]");
            if (!res.Ok || !File.Exists(dest)) continue;
            Check($"{label}: annotations removed are counted", res.AnnotationsRemoved >= 7,
                  $"{res.AnnotationsRemoved}");

            string bytes = Encoding.Latin1.GetString(File.ReadAllBytes(dest));
            foreach (var secret in new[] { "SECRET-NOTE", "SECRET-POPUP", "SECRET-REPLY", "SECRET-LINK",
                                           "123-45-6789", "SECRET-AP", "Jane Secret", "SECRET-NORECT",
                                           "SECRET-XFA" })
                Check($"{label}: \"{secret}\" is gone from the file", !bytes.Contains(secret), "");
            foreach (var keep in new[] { "KEEP-ME", "keep value" })
                Check($"{label}: \"{keep}\" survives", bytes.Contains(keep), "");

            using var doc = PdfSharpCore.Pdf.IO.PdfReader.Open(dest, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import);
            var annots = doc.Pages[0].Elements.GetArray("/Annots");
            var left = new List<string>();
            for (int i = 0; i < (annots?.Elements.Count ?? 0); i++)
                if (annots!.Elements[i] is PdfReference r && r.Value is PdfDictionary d)
                    left.Add(d.Elements.GetString("/Contents") + d.Elements.GetString("/T") + "|" + d.Elements.GetName("/Subtype"));
            Check($"{label}: only the unrelated annotation and the widgets outside the mark remain",
                  left.Count == 3, string.Join(", ", left));

            var acro = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
            var fields = acro?.Elements.GetArray("/Fields");
            var names = new List<string>();
            PdfDictionary? name = null;
            for (int i = 0; i < (fields?.Elements.Count ?? 0); i++)
                if (fields!.Elements[i] is PdfReference r && r.Value is PdfDictionary f)
                {
                    names.Add(f.Elements.GetString("/T"));
                    if (f.Elements.GetString("/T") == "name") name = f;
                }
            Check($"{label}: the field under the mark is dropped, the others stay",
                  names.Count == 2 && names.Contains("name") && names.Contains("keep"), string.Join(", ", names));
            Check($"{label}: a field that lost one of two widgets has its value cleared",
                  name is not null && !name.Elements.ContainsKey("/V")
                  && name.Elements.GetArray("/Kids")?.Elements.Count == 1, "");
            var survivor = name?.Elements.GetArray("/Kids")?.Elements[0] is PdfReference kr ? kr.Value as PdfDictionary : null;
            Check($"{label}: its surviving widget no longer draws the old value",
                  survivor is not null && !survivor.Elements.ContainsKey("/AP"), "");
            Check($"{label}: the hybrid form's XFA copy is dropped",
                  acro is not null && !acro.Elements.ContainsKey("/XFA"), "");
            Check($"{label}: verification on the output reports clean",
                  PdfRedaction.FindSurvivingAnnotations(dest, rects).Count == 0, "");
        }
    }

    private static void Build(string path, int rotate)
    {
        var doc = new PdfDocument();
        var page = doc.AddPage();   // 612 x 792
        using (var g = XGraphics.FromPdfPage(page))
            g.DrawString("MARK", new XFont("Helvetica", 24), XBrushes.Black, 200, 300);

        PdfDictionary Annot(string subtype, double[]? rect, string? contents)
        {
            var d = new PdfDictionary(doc);
            d.Elements["/Type"] = new PdfName("/Annot");
            d.Elements["/Subtype"] = new PdfName(subtype);
            if (rect is not null) d.Elements["/Rect"] = Rect(doc, rect);
            if (contents is not null) d.Elements["/Contents"] = new PdfString(contents);
            d.Elements["/P"] = page.Reference;
            doc.Internals.AddObject(d);
            return d;
        }
        PdfDictionary Ap(string text)
        {
            var s = new PdfDictionary(doc);
            s.Elements["/Type"] = new PdfName("/XObject");
            s.Elements["/Subtype"] = new PdfName("/Form");
            s.Elements["/BBox"] = Rect(doc, new double[] { 0, 0, 100, 20 });
            s.CreateStream(Encoding.ASCII.GetBytes($"% ({text})\n0 0 1 rg 0 0 10 10 re f\n"));
            doc.Internals.AddObject(s);
            var ap = new PdfDictionary(doc);
            ap.Elements["/N"] = s.Reference;
            return ap;
        }

        // Under the mark: a comment with a popup and a reply elsewhere, a link, an annotation
        // with no rectangle at all, and a filled text field.
        var note = Annot("/Text", new double[] { 210, 470, 230, 490 }, "SECRET-NOTE");
        var popup = Annot("/Popup", new double[] { 450, 100, 600, 200 }, "SECRET-POPUP");
        popup.Elements["/Parent"] = note.Reference;
        note.Elements["/Popup"] = popup.Reference;
        var reply = Annot("/Text", new double[] { 500, 600, 520, 620 }, "SECRET-REPLY");
        reply.Elements["/IRT"] = note.Reference;
        var link = Annot("/Link", new double[] { 300, 420, 350, 440 }, "SECRET-LINK");
        var noRect = Annot("/Text", null, "SECRET-NORECT");

        var ssn = Annot("/Widget", new double[] { 160, 410, 380, 430 }, null);
        ssn.Elements["/FT"] = new PdfName("/Tx");
        ssn.Elements["/T"] = new PdfString("ssn");
        ssn.Elements["/V"] = new PdfString("123-45-6789");
        ssn.Elements["/AP"] = Ap("SECRET-AP");

        // One field, two widgets: one under the mark, one far away.
        var nameField = new PdfDictionary(doc);
        nameField.Elements["/FT"] = new PdfName("/Tx");
        nameField.Elements["/T"] = new PdfString("name");
        nameField.Elements["/V"] = new PdfString("Jane Secret");
        doc.Internals.AddObject(nameField);
        var w1 = Annot("/Widget", new double[] { 160, 530, 300, 550 }, null);
        var w2 = Annot("/Widget", new double[] { 50, 700, 200, 720 }, null);
        w1.Elements["/Parent"] = nameField.Reference;
        w2.Elements["/Parent"] = nameField.Reference;
        w2.Elements["/AP"] = Ap("Jane Secret");
        nameField.Elements["/Kids"] = new PdfArray(doc, w1.Reference, w2.Reference);

        // Elsewhere: must survive.
        var keepNote = Annot("/Text", new double[] { 50, 50, 70, 70 }, "KEEP-ME");
        var keep = Annot("/Widget", new double[] { 420, 700, 560, 720 }, null);
        keep.Elements["/FT"] = new PdfName("/Tx");
        keep.Elements["/T"] = new PdfString("keep");
        keep.Elements["/V"] = new PdfString("keep value");

        page.Elements["/Annots"] = new PdfArray(doc,
            note.Reference, popup.Reference, reply.Reference, link.Reference, noRect.Reference,
            ssn.Reference, w1.Reference, w2.Reference, keepNote.Reference, keep.Reference);

        var acro = new PdfDictionary(doc);
        acro.Elements["/Fields"] = new PdfArray(doc, ssn.Reference, nameField.Reference, keep.Reference);
        acro.Elements["/XFA"] = new PdfString("<datasets>SECRET-XFA</datasets>");
        doc.Internals.AddObject(acro);
        doc.Internals.Catalog.Elements["/AcroForm"] = acro.Reference;

        page.Elements["/CropBox"] = Rect(doc, new double[] { 20, 30, 600, 780 });
        page.Rotate = rotate;
        doc.Save(path);
    }

    private static PdfArray Rect(PdfDocument doc, double[] v)
    {
        var a = new PdfArray(doc);
        foreach (double x in v) a.Elements.Add(new PdfReal(x));
        return a;
    }
}
