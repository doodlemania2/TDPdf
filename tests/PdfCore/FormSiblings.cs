using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using TDPdf.Services;

/// <summary>
/// Pins how widgets that share one AcroForm field share its value: <see cref="FormFieldSiblings"/>.
/// </summary>
/// <remarks>
/// Pending values were keyed by widget, so a field printed twice kept two values and the last
/// widget saved won /V. The properties that must hold, read back from a saved file:
///
///   * <b>siblings share one key</b>, the field's object number; a widget that is its own field
///     keeps its own key, so single-widget fields behave exactly as before.
///   * <b>/V lands on the field</b>, and a stale /V on a widget below it is overwritten rather than
///     left to shadow the field's value.
///   * <b>check box siblings with different on-states</b> each show their own state for one value.
///   * <b>a /Parent cycle</b> in a malformed file cannot hang the walk.
/// </remarks>
internal static class FormSiblings
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("── Form fields with several widgets share one value ───────────");

        string path = Path.Combine(tmp, "siblings.pdf");
        {
            var doc = new PdfDocument();
            var p1 = doc.AddPage();
            var p2 = doc.AddPage();

            PdfDictionary Widget(PdfPage page, PdfDictionary? parent)
            {
                var w = new PdfDictionary(doc);
                w.Elements["/Type"] = new PdfName("/Annot");
                w.Elements["/Subtype"] = new PdfName("/Widget");
                w.Elements["/Rect"] = new PdfArray(doc, new PdfReal(10), new PdfReal(10), new PdfReal(110), new PdfReal(30));
                doc.Internals.AddObject(w);
                if (parent is not null) w.Elements["/Parent"] = parent.Reference;
                var annots = page.Elements.GetArray("/Annots");
                if (annots is null) { annots = new PdfArray(doc); page.Elements["/Annots"] = annots; }
                annots.Elements.Add(w.Reference);
                return w;
            }
            PdfDictionary Field(string name, string ft)
            {
                var f = new PdfDictionary(doc);
                f.Elements["/T"] = new PdfString(name);
                f.Elements["/FT"] = new PdfName(ft);
                doc.Internals.AddObject(f);
                return f;
            }
            void Kids(PdfDictionary field, params PdfDictionary[] kids)
            {
                var arr = new PdfArray(doc);
                foreach (var k in kids) arr.Elements.Add(k.Reference);
                field.Elements["/Kids"] = arr;
            }
            void OnState(PdfDictionary w, string on)
            {
                var n = new PdfDictionary(doc);
                n.Elements[on] = new PdfDictionary(doc);
                n.Elements["/Off"] = new PdfDictionary(doc);
                var ap = new PdfDictionary(doc);
                ap.Elements["/N"] = n;
                w.Elements["/AP"] = ap;
            }

            var name = Field("Name", "/Tx");
            var n1 = Widget(p1, name);
            var n2 = Widget(p2, name);
            n2.Elements["/V"] = new PdfString("stale");   // malformed: a value on the widget itself
            Kids(name, n1, n2);

            var merged = Widget(p1, null);
            merged.Elements["/T"] = new PdfString("Merged");
            merged.Elements["/FT"] = new PdfName("/Tx");

            var agree = Field("Agree", "/Btn");
            var c1 = Widget(p1, agree); OnState(c1, "/A");
            var c2 = Widget(p2, agree); OnState(c2, "/B");
            Kids(agree, c1, c2);

            var fields = new PdfArray(doc, name.Reference, merged.Reference, agree.Reference);
            var acro = new PdfDictionary(doc);
            acro.Elements["/Fields"] = fields;
            doc.Internals.Catalog.Elements["/AcroForm"] = acro;
            doc.Save(path);
        }

        using var re = PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);
        PdfDictionary Ann(int page, int i) =>
            (PdfDictionary)((PdfReference)re.Pages[page].Elements.GetArray("/Annots")!.Elements[i]).Value;
        int Num(PdfDictionary d) => d.Reference?.ObjectNumber ?? -1;

        var w1 = Ann(0, 0);       // Name, page 1
        var merged2 = Ann(0, 1);  // Merged
        var cb1 = Ann(0, 2);      // Agree /A
        var w2 = Ann(1, 0);       // Name, page 2
        var cb2 = Ann(1, 1);      // Agree /B
        var nameField = FormFieldSiblings.TerminalField(w1);

        int k1 = FormFieldSiblings.FieldKey(w1, Num(w1)), k2 = FormFieldSiblings.FieldKey(w2, Num(w2));
        Check("two widgets of one field share a key", k1 == k2, $"{k1} vs {k2}");
        Check("that key is the field's object number", k1 == Num(nameField) && k1 != Num(w1), $"{k1}");
        Check("the terminal field is the node with /T",
              (nameField.Elements["/T"] as PdfString)?.Value == "Name", "");
        Check("a widget that is its own field keeps its own key",
              FormFieldSiblings.FieldKey(merged2, Num(merged2)) == Num(merged2), "");
        Check("check box siblings share a key",
              FormFieldSiblings.FieldKey(cb1, Num(cb1)) == FormFieldSiblings.FieldKey(cb2, Num(cb2)), "");
        Check("a direct widget with no field above keeps its synthetic key",
              FormFieldSiblings.FieldKey(new PdfDictionary(), -42) == -42, "");

        // The save loop writes the value once per widget of the field, as here.
        FormFieldSiblings.SetValue(w1, new PdfString("Ada"));
        FormFieldSiblings.SetValue(w2, new PdfString("Ada"));
        Check("/V lands on the field", (nameField.Elements["/V"] as PdfString)?.Value == "Ada", "");
        Check("a widget without /V does not gain one", !w1.Elements.ContainsKey("/V"), "");
        Check("a stale /V on a sibling widget is overwritten",
              (w2.Elements["/V"] as PdfString)?.Value == "Ada", (w2.Elements["/V"] as PdfString)?.Value ?? "none");

        FormFieldSiblings.SetValue(merged2, new PdfString("Solo"));
        Check("a merged field/widget gets /V on itself",
              (merged2.Elements["/V"] as PdfString)?.Value == "Solo", "");

        // Check box on-states: one value, each widget its own /AS.
        Check("the selected sibling shows its own on-state",
              FormFieldSiblings.CheckStateFor("/A", "/A") == "/A", "");
        Check("the other sibling shows /Off", FormFieldSiblings.CheckStateFor("/A", "/B") == "/Off", "");
        Check("/Off turns every sibling off",
              FormFieldSiblings.CheckStateFor("/Off", "/A") == "/Off" && !FormFieldSiblings.IsOn("/Off", "/A"), "");
        Check("ticking an unticked widget selects its on-state", FormFieldSiblings.Toggle(false, "/B") == "/B", "");
        Check("ticking a ticked widget clears the field", FormFieldSiblings.Toggle(true, "/B") == "/Off", "");
        Check("initial: /V naming this widget ticks it",
              FormFieldSiblings.InitiallyChecked("/A", "/A", "/Off"), "");
        Check("initial: /V naming a sibling leaves this one off",
              !FormFieldSiblings.InitiallyChecked("/A", "/B", "/Off"), "");
        Check("initial: a string /V names the same state as the /AP key",
              FormFieldSiblings.InitiallyChecked("Yes", "/Yes", ""), "");
        Check("initial: an odd /V defers to the widget's own /AS",
              FormFieldSiblings.InitiallyChecked("/1", "/Yes", "/Yes")
              && !FormFieldSiblings.InitiallyChecked("/1", "/Yes", "/Off"), "");
        Check("initial: no /V stays unticked, as before",
              !FormFieldSiblings.InitiallyChecked("", "/Yes", "/Yes")
              && !FormFieldSiblings.InitiallyChecked("/Off", "/Yes", "/Yes"), "");

        // A /Parent cycle with no /T: the walk gives up and treats the widget as its own field.
        {
            var doc = new PdfDocument();
            var a = new PdfDictionary(doc); doc.Internals.AddObject(a);
            var b = new PdfDictionary(doc); doc.Internals.AddObject(b);
            a.Elements["/Parent"] = b.Reference;
            b.Elements["/Parent"] = a.Reference;
            Check("a /Parent cycle terminates", ReferenceEquals(FormFieldSiblings.TerminalField(a), a)
                  && FormFieldSiblings.FieldKey(a, 7) == 7, "");
        }
    }
}
