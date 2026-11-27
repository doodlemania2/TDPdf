using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using TDPdf.Services;

/// <summary>
/// Merging keeps the form: imported widgets' fields are registered in the target /AcroForm, the
/// source /DR fonts come along, and different fields that share a name are not tied together.
/// </summary>
internal static class FormMergeTests
{
    private static PdfiumInterop.PdfRect R(double l, double b, double w, double h) => new(l, b, l + w, b + h);

    private static void Make(string path, string[] names, bool withFont)
    {
        var doc = new PdfDocument();
        var page = doc.AddPage();
        double y = 700;
        foreach (var n in names)
        {
            PdfFormBuilder.Add(doc, page, new[] { R(60, y, 200, 22) },
                new PdfFormBuilder.FieldSpec { Kind = PdfFormBuilder.FieldKind.Text, Name = n });
            y -= 40;
        }
        if (withFont)
        {
            var font = new PdfDictionary(doc);
            font.Elements["/Type"] = new PdfName("/Font");
            font.Elements["/Subtype"] = new PdfName("/Type1");
            font.Elements["/BaseFont"] = new PdfName("/Courier");
            doc.Internals.AddObject(font);
            doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")!.Elements.GetDictionary("/DR")!
                .Elements.GetDictionary("/Font")!.Elements["/F9"] = font.Reference!;
        }
        doc.Save(path);
    }

    private static List<string> FieldNames(PdfDocument d)
    {
        var res = new List<string>();
        var f = d.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields");
        for (int i = 0; i < (f?.Elements.Count ?? 0); i++)
            if (PdfItems.DerefDict(f!.Elements[i])?.Elements["/T"] is PdfString t) res.Add(t.Value);
        return res;
    }

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nMerge keeps form fields");
        string a = Path.Combine(tmp, "merge-a.pdf"), b = Path.Combine(tmp, "merge-b.pdf"), plain = Path.Combine(tmp, "merge-plain.pdf");
        Make(a, new[] { "Name", "City" }, false);
        Make(b, new[] { "Name", "Other" }, true);
        using (var pd = new PdfDocument()) { pd.AddPage(); pd.Save(plain); }

        // Form into form, with one clashing name.
        using (var target = PdfReader.Open(a, PdfDocumentOpenMode.Modify))
        {
            int offset = target.PageCount;
            using var src = PdfReader.Open(b, PdfDocumentOpenMode.Import);
            for (int i = 0; i < src.PageCount; i++) target.AddPage(src.Pages[i]);
            var r = PdfFormMerge.AdoptImportedWidgets(target, offset, src);
            var names = FieldNames(target);
            Check("merge: all four fields are registered", names.Count == 4, string.Join(",", names));
            Check("merge: names stay unique", names.Distinct().Count() == 4, string.Join(",", names));
            Check("merge: the clashing field is renamed, the original is not",
                  names.Contains("Name") && names.Contains("Name_2") && names.Contains("Other") && names.Contains("City"), string.Join(",", names));
            Check("merge: reports one rename", r.FieldsRenamed == 1 && r.FieldsAdopted == 2, $"{r.FieldsRenamed}/{r.FieldsAdopted}");
            var fonts = target.Internals.Catalog.Elements.GetDictionary("/AcroForm")!.Elements.GetDictionary("/DR")!.Elements.GetDictionary("/Font")!;
            Check("merge: the source's /DR font is imported", fonts.Elements.ContainsKey("/F9") && PdfItems.DerefDict(fonts.Elements["/F9"])?.Elements.GetName("/BaseFont") == "/Courier",
                  string.Join(",", fonts.Elements.Keys));
            Check("merge: the target's own /Helv is untouched", fonts.Elements.ContainsKey("/Helv"), "");

            // Adopting twice must not duplicate.
            var again = PdfFormMerge.AdoptImportedWidgets(target, offset, src);
            Check("merge: a second pass adds nothing", again.FieldsAdopted == 0 && FieldNames(target).Count == 4, $"{again.FieldsAdopted}");

            string outp = Path.Combine(tmp, "merge-out.pdf");
            target.Save(outp);
            using var re = PdfReader.Open(outp, PdfDocumentOpenMode.Import);
            Check("merge: the saved file reopens with the same fields", FieldNames(re).Count == 4, string.Join(",", FieldNames(re)));
        }

        // Form into a document with no /AcroForm at all.
        using (var target = PdfReader.Open(plain, PdfDocumentOpenMode.Modify))
        {
            int offset = target.PageCount;
            using var src = PdfReader.Open(b, PdfDocumentOpenMode.Import);
            for (int i = 0; i < src.PageCount; i++) target.AddPage(src.Pages[i]);
            PdfFormMerge.AdoptImportedWidgets(target, offset, src);
            var names = FieldNames(target);
            Check("merge: an /AcroForm is created when the target had none", names.Count == 2 && names.Contains("Name") && names.Contains("Other"), string.Join(",", names));
            var af = target.Internals.Catalog.Elements.GetDictionary("/AcroForm");
            Check("merge: it does not turn on /NeedAppearances itself", af is not null && !af.Elements.ContainsKey("/NeedAppearances"), "");
        }

        // Plain pages in: nothing happens.
        using (var target = PdfReader.Open(a, PdfDocumentOpenMode.Modify))
        {
            int offset = target.PageCount;
            using var src = PdfReader.Open(plain, PdfDocumentOpenMode.Import);
            target.AddPage(src.Pages[0]);
            var r = PdfFormMerge.AdoptImportedWidgets(target, offset, src);
            Check("merge: formless pages change nothing", r.FieldsAdopted == 0 && FieldNames(target).Count == 2, "");
        }
    }
}
