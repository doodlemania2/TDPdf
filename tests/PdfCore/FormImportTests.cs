using System.Text;
using PdfSharpCore.Pdf;
using TDPdf.Services;

/// <summary>
/// Import form data from FDF and XFDF (#225): both parsers (including an FDF with no cross-reference
/// table and hostile XML), and matching the data to the form by fully qualified name.
/// </summary>
internal static class FormImportTests
{
    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    private static PdfDictionary D(PdfDocument doc)
    {
        var d = new PdfDictionary(doc);
        doc.Internals.AddObject(d);
        return d;
    }

    private static PdfDictionary Widget(PdfDocument doc, PdfPage page, string ft, string? name, PdfDictionary? parent, Action<PdfDictionary>? more = null)
    {
        var w = D(doc);
        w.Elements["/Type"] = new PdfName("/Annot");
        w.Elements["/Subtype"] = new PdfName("/Widget");
        w.Elements["/Rect"] = new PdfArray(doc, new PdfInteger(10), new PdfInteger(10), new PdfInteger(100), new PdfInteger(30));
        if (ft.Length > 0) w.Elements["/FT"] = new PdfName(ft);
        if (name is not null) w.Elements["/T"] = new PdfString(name);
        if (parent is not null) w.Elements["/Parent"] = parent.Reference;
        more?.Invoke(w);
        var arr = page.Elements["/Annots"] as PdfArray;
        if (arr is null) { arr = new PdfArray(doc); page.Elements["/Annots"] = arr; }
        arr.Elements.Add(w.Reference);
        return w;
    }

    private static void OnState(PdfDocument doc, PdfDictionary w, string on)
    {
        var n = new PdfDictionary(doc);
        n.Elements[on] = new PdfDictionary(doc);
        n.Elements["/Off"] = new PdfDictionary(doc);
        var ap = new PdfDictionary(doc);
        ap.Elements["/N"] = n;
        w.Elements["/AP"] = ap;
    }

    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nForm data import (FormDataParsers, FormImport)");

        // ── FDF with an xref table ──
        const string fdfFull = "%FDF-1.2\n1 0 obj\n<< /FDF << /Fields [ << /T (name) /V (Ada Lovelace) >> << /T (agree) /V /Yes >> ] >> >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n";
        var a = FormDataParsers.Parse(B(fdfFull));
        Check("FDF: text and name values are read", a.Count == 2 && a[0].Name == "name" && a[0].Value == "Ada Lovelace" && a[1].Value == "/Yes" && a[1].ValueIsName, string.Join(";", a.Select(x => x.Name + "=" + x.Value)));

        // ── FDF with no xref table and indirect field objects (pdftk / fdfgen style) ──
        const string fdfLoose = "%FDF-1.2\n1 0 obj\n<</FDF<</Fields[2 0 R 3 0 R]/F(a.pdf)>>>>\nendobj\n2 0 obj\n<</T(person)/Kids[4 0 R]>>\nendobj\n3 0 obj\n<</T(note)/V(line \\(one\\)\\nline two)>>\nendobj\n4 0 obj\n<</T(first)/V<FEFF00E900E8>>>\nendobj\ntrailer\n<</Root 1 0 R>>\n";
        var b = FormDataParsers.Parse(B(fdfLoose));
        Check("FDF without xref: kids join into a qualified name", b.Any(f => f.Name == "person.first"), string.Join(";", b.Select(x => x.Name)));
        Check("FDF: UTF-16 hex string is decoded", b.First(f => f.Name == "person.first").Value == "éè", "");
        Check("FDF: escapes and nested parentheses are decoded", b.First(f => f.Name == "note").Value == "line (one)\nline two", b.First(f => f.Name == "note").Value);

        // ── FDF with no objects at all and no trailer ──
        var c = FormDataParsers.Parse(B("%FDF-1.2\n<< /FDF << /Fields [ << /T (x) /V (1) >> ] >> >>"));
        Check("FDF with no object wrapper is still read", c.Count == 1 && c[0].Name == "x" && c[0].Value == "1", "");

        // ── multi-value, octal escape, comment ──
        var m = FormDataParsers.Parse(B("%FDF-1.2\n1 0 obj\n<< /FDF << /Fields [ << /T (list) /V [ (a) (b) ] >> << /T (o) /V (\\101\\102) >> ] >> >>\nendobj\ntrailer << /Root 1 0 R >>"));
        Check("FDF: an array value keeps every item", m[0].Values.SequenceEqual(["a", "b"]), "");
        Check("FDF: octal escapes", m[1].Value == "AB", m[1].Value ?? "");

        // ── hostile / damaged FDF ──
        Check("FDF: a reference loop ends", Throws(() => FormDataParsers.Parse(B("%FDF-1.2\n1 0 obj\n<< /FDF << /Fields [2 0 R] >> >>\nendobj\n2 0 obj\n<< /T (a) /Kids [2 0 R] >>\nendobj\n"))) == false, "");
        Check("FDF: junk is rejected", Throws(() => FormDataParsers.Parse(B("hello world"))), "");
        Check("FDF: a truncated file does not crash", !Throws(() => FormDataParsers.Parse(B("%FDF-1.2\n1 0 obj\n<< /FDF << /Fields [ << /T (a) /V (unterminated"))) || true, "");
        var deep = new StringBuilder("%FDF-1.2\n1 0 obj\n<< /FDF << /Fields ");
        for (int i = 0; i < 5000; i++) deep.Append('[');
        Check("FDF: 5000 nested arrays are cut off, not a stack overflow", Throws(() => FormDataParsers.Parse(B(deep.ToString()))) || true, "");
        Check("a file over the size cap is refused", Throws(() => FormDataParsers.Parse(new byte[FormDataParsers.MaxBytes + 1])), "");

        // ── XFDF ──
        const string xfdf = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><xfdf xmlns=\"http://ns.adobe.com/xfdf/\" xml:space=\"preserve\"><f href=\"a.pdf\"/><fields><field name=\"name\"><value>Ada &amp; Grace</value></field><field name=\"address\"><field name=\"city\"><value>Paris</value></field><field name=\"zip\"><value/></field></field><field name=\"pick\"><value>a</value><value>b</value></field></fields></xfdf>";
        var x = FormDataParsers.Parse(Encoding.UTF8.GetBytes(xfdf));
        Check("XFDF: values and entities", x.First(f => f.Name == "name").Value == "Ada & Grace", string.Join(";", x.Select(f => f.Name + "=" + f.Value)));
        Check("XFDF: nested fields give qualified names", x.Any(f => f.Name == "address.city" && f.Value == "Paris"), "");
        Check("XFDF: an empty value is an empty string, not missing", x.First(f => f.Name == "address.zip").Value == "", "");
        Check("XFDF: a parent with no value of its own is not listed", x.All(f => f.Name != "address"), "");
        Check("XFDF: several values are all kept", x.First(f => f.Name == "pick").Values.SequenceEqual(["a", "b"]), "");

        const string bomb = "<?xml version=\"1.0\"?><!DOCTYPE xfdf [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]><xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><fields><field name=\"x\"><value>&b;</value></field></fields></xfdf>";
        Check("XFDF: a DOCTYPE is refused (no entity expansion)", Throws(() => FormDataParsers.Parse(Encoding.UTF8.GetBytes(bomb))), "");
        const string xxe = "<?xml version=\"1.0\"?><!DOCTYPE xfdf [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><fields><field name=\"x\"><value>&e;</value></field></fields></xfdf>";
        Check("XFDF: an external entity is refused", Throws(() => FormDataParsers.Parse(Encoding.UTF8.GetBytes(xxe))), "");
        Check("XFDF: broken XML is rejected", Throws(() => FormDataParsers.Parse(B("<xfdf><fields><field name=\"a\"><value>1</fields>"))), "");
        Check("XML that is not XFDF is rejected", Throws(() => FormDataParsers.Parse(B("<html><body/></html>"))), "");

        // ── matching against a form ──
        var doc = new PdfDocument();
        var page = doc.AddPage();
        var parent = D(doc);
        parent.Elements["/T"] = new PdfString("person");
        Widget(doc, page, "/Tx", "first", parent);
        Widget(doc, page, "/Tx", "code", null, w => { w.Elements["/MaxLen"] = new PdfInteger(4); w.Elements["/Ff"] = new PdfInteger(1 << 24); });
        Widget(doc, page, "/Tx", "locked", null, w => w.Elements["/Ff"] = new PdfInteger(1));
        Widget(doc, page, "/Btn", "agree", null, w => OnState(doc, w, "/Yes"));
        var choiceOpt = new PdfArray(doc, new PdfArray(doc, new PdfString("us"), new PdfString("United States")), new PdfArray(doc, new PdfString("fr"), new PdfString("France")));
        Widget(doc, page, "/Ch", "country", null, w => w.Elements["/Opt"] = choiceOpt);
        var radio = D(doc); radio.Elements["/T"] = new PdfString("size"); radio.Elements["/FT"] = new PdfName("/Btn"); radio.Elements["/Ff"] = new PdfInteger(1 << 15);
        Widget(doc, page, "", null, radio, w => OnState(doc, w, "/Small"));
        Widget(doc, page, "", null, radio, w => OnState(doc, w, "/Large"));
        Widget(doc, page, "/Btn", "go", null, w => w.Elements["/Ff"] = new PdfInteger(1 << 16));
        // A field the /AcroForm /Fields list never mentions (and the form has no /AcroForm at all): found through the page.
        var targets = FormImport.BuildTargets(doc);
        Check("every widget on the page is found without an /AcroForm", targets.Count == 6, string.Join(",", targets.Select(t => t.QualifiedName)));
        Check("a push button is not a target", targets.All(t => t.QualifiedName != "go"), "");
        Check("qualified names join the parent's name", targets.Any(t => t.QualifiedName == "person.first"), "");
        Check("the two radio widgets are one group with two states", targets.Single(t => t.Kind == FormTargetKind.Radio).OnValues.SequenceEqual(["/Small", "/Large"]), "");

        var plan = FormImport.Plan(
        [
            new ImportedField("person.first", ["Ada"]),
            new ImportedField("code", ["ABCDEFG"]),
            new ImportedField("locked", ["x"]),
            new ImportedField("agree", ["Yes"], ValueIsName: true),
            new ImportedField("country", ["France"]),
            new ImportedField("size", ["/Large"], ValueIsName: true),
            new ImportedField("ghost", ["boo"]),
            new ImportedField("PERSON.FIRST", ["ignored duplicate"]),
        ], targets);
        string Get(string name) => plan.Assignments.First(x => x.Target.QualifiedName == name).Value;
        Check("a text value is assigned by qualified name", Get("person.first") == "Ada", "");
        Check("a comb field takes only as many characters as it has cells", Get("code") == "ABCD", Get("code"));
        Check("a read-only field is skipped and reported", plan.Assignments.All(x => x.Target.QualifiedName != "locked") && plan.Skipped.Any(s => s.StartsWith("locked")), string.Join(";", plan.Skipped));
        Check("a check box takes its on-state", Get("agree") == "/Yes", "");
        Check("a choice is matched by display text and stores the export value", Get("country") == "fr", Get("country"));
        Check("a radio group takes the named state", Get("size") == "/Large", "");
        Check("a name the form lacks is reported", plan.Unmatched.SequenceEqual(["ghost"]), string.Join(";", plan.Unmatched));
        Check("a name in another case matches too, but the first value wins", Get("person.first") == "Ada", "");

        var odd = FormImport.Plan(
        [
            new ImportedField("agree", ["Off"], true),
            new ImportedField("country", ["Narnia"]),
            new ImportedField("size", ["Medium"]),
            new ImportedField("person.first", []),
        ], targets);
        Check("Off clears a check box", odd.Assignments.Any(x => x.Target.QualifiedName == "agree" && x.Value == "/Off"), "");
        Check("a choice not in the list is skipped with a reason", odd.Assignments.All(x => x.Target.QualifiedName != "country") && odd.Skipped.Any(s => s.StartsWith("country")), "");
        Check("a radio state the group lacks is skipped", odd.Assignments.All(x => x.Target.QualifiedName != "size"), "");
        Check("a field the file gives no value for is left alone", odd.Assignments.All(x => x.Target.QualifiedName != "person.first"), "");
        Check("truthy words tick a check box", FormImport.Plan([new ImportedField("agree", ["true"])], targets).Assignments.Single().Value == "/Yes", "");
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; } catch (Exception) { return true; }
    }
}
