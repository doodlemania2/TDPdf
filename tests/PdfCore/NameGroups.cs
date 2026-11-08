using PdfSharpCore.Pdf;
using TDPdf.Services;

/// <summary>
/// Pins name-based field grouping (upstream #466) and the signatures.json backup/atomic-write helpers.
/// </summary>
internal static class NameGroups
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("── Fields sharing a name, and the signature store file ────────");

        var doc = new PdfDocument();
        var p1 = doc.AddPage();
        var p2 = doc.AddPage();

        PdfDictionary Widget(PdfPage page, string t, string ft, PdfDictionary? parent = null, int? ff = null, int? maxLen = null)
        {
            var w = new PdfDictionary(doc);
            w.Elements["/Type"] = new PdfName("/Annot");
            w.Elements["/Subtype"] = new PdfName("/Widget");
            w.Elements["/Rect"] = new PdfArray(doc, new PdfReal(10), new PdfReal(10), new PdfReal(110), new PdfReal(30));
            if (t.Length > 0) w.Elements["/T"] = new PdfString(t);
            if (ft.Length > 0) w.Elements["/FT"] = new PdfName(ft);
            if (ff is int f) w.Elements["/Ff"] = new PdfInteger(f);
            if (maxLen is int m) w.Elements["/MaxLen"] = new PdfInteger(m);
            doc.Internals.AddObject(w);
            if (parent is not null) w.Elements["/Parent"] = parent.Reference;
            var annots = page.Elements.GetArray("/Annots");
            if (annots is null) { annots = new PdfArray(doc); page.Elements["/Annots"] = annots; }
            annots.Elements.Add(w.Reference);
            return w;
        }

        // Two SEPARATE top-level text fields with the same name, on different pages.
        var a1 = Widget(p1, "City", "/Tx");
        var a2 = Widget(p2, "City", "/Tx");
        // A check box with the same name is a different field.
        var cb = Widget(p1, "City", "/Btn");
        // Comb field pair.
        var c1 = Widget(p1, "Zip", "/Tx", ff: 1 << 24, maxLen: 5);
        var c2 = Widget(p2, "Zip", "/Tx", ff: 1 << 24, maxLen: 3);
        // Hierarchical: Home.Phone and a flat "Home.Phone"-less "Phone" must not collide.
        var home = new PdfDictionary(doc); home.Elements["/T"] = new PdfString("Home"); doc.Internals.AddObject(home);
        var ph1 = Widget(p1, "Phone", "/Tx", home);
        var ph2 = Widget(p2, "Phone", "/Tx");
        // Radio and push button never pool by name here.
        var rad = Widget(p1, "Pick", "/Btn", ff: 1 << 15);
        var push = Widget(p1, "Go", "/Btn", ff: 1 << 16);
        var unnamed = Widget(p1, "", "/Tx");

        Check("fully qualified name joins parents root first", FormFieldSiblings.QualifiedName(ph1) == "Home.Phone", FormFieldSiblings.QualifiedName(ph1));
        Check("a flat name is itself", FormFieldSiblings.QualifiedName(ph2) == "Phone", "");
        Check("same name + type share a group", FormFieldSiblings.GroupIdOf(a1) == FormFieldSiblings.GroupIdOf(a2) && FormFieldSiblings.GroupIdOf(a1) != "", "");
        Check("same name, different type do not", FormFieldSiblings.GroupIdOf(a1) != FormFieldSiblings.GroupIdOf(cb), "");
        Check("Home.Phone is not Phone", FormFieldSiblings.GroupIdOf(ph1) != FormFieldSiblings.GroupIdOf(ph2), "");
        Check("radio, push button and unnamed fields have no group",
              FormFieldSiblings.GroupIdOf(rad) == "" && FormFieldSiblings.GroupIdOf(push) == "" && FormFieldSiblings.GroupIdOf(unnamed) == "", "");

        var index = FormFieldSiblings.BuildIndex(doc);
        string cityGroup = FormFieldSiblings.GroupIdOf(a1);
        var city = FormFieldSiblings.Siblings(index, cityGroup);
        Check("the index finds both City fields across pages, not the check box", city.Count == 2, $"{city.Count}");
        var zip = FormFieldSiblings.Siblings(index, FormFieldSiblings.GroupIdOf(c1));
        Check("comb fields group too and keep their cell count",
              zip.Count == 2 && zip.Select(z => z.CombLen).OrderBy(x => x).SequenceEqual(new[] { 3, 5 }), string.Join(",", zip.Select(z => z.CombLen)));
        Check("an empty group id matches nothing", FormFieldSiblings.Siblings(index, "").Count == 0, "");
        Check("a repeated key is listed once",
              FormFieldSiblings.Siblings(new[] { new FormFieldSiblings.FieldRef("g", 1, 0), new FormFieldSiblings.FieldRef("g", 1, 0) }, "g").Count == 1, "");
        Check("value fits a shorter comb", FormFieldSiblings.FitToComb("12345", 3) == "123", "");
        Check("value is untouched without a comb", FormFieldSiblings.FitToComb("12345", 0) == "12345", "");

        // signatures.json
        string dir = Path.Combine(tmp, "sigstore");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "signatures.json");
        File.WriteAllText(file, "{ not json");
        var now = new DateTime(2026, 11, 7, 14, 30, 5);
        Check("backup name is dated", SignatureStoreFile.DamagedBackupPath(file, now) == file + ".damaged-20261107-143005", "");
        string? b1 = SignatureStoreFile.TryBackUpDamaged(file, now);
        string? b2 = SignatureStoreFile.TryBackUpDamaged(file, now);
        Check("damaged file copied aside intact", b1 is not null && File.ReadAllText(b1) == "{ not json", b1 ?? "null");
        Check("a second backup in the same second does not replace the first", b2 is not null && b2 != b1 && File.Exists(b1!), b2 ?? "null");
        Check("the original is left in place by the backup", File.ReadAllText(file) == "{ not json", "");
        SignatureStoreFile.WriteAtomic(file, "[]");
        Check("atomic write replaces the file", File.ReadAllText(file) == "[]", "");
        Check("no staging file left behind", Directory.GetFiles(dir, ".tdpdf_save_*").Length == 0, "");
        Check("backup of a missing file fails cleanly", SignatureStoreFile.TryBackUpDamaged(Path.Combine(dir, "none.json"), now) is null, "");
    }
}
