using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using TDPdf.Services;

/// <summary>
/// AcroForm authoring: the fields TDPdf creates have to be real fields, not drawings of fields.
/// </summary>
/// <remarks>
/// Two independent things have to hold, and passing one while failing the other is easy.
///
/// The STRUCTURE has to be right — the field in the document's /AcroForm /Fields, the widget in the
/// page's /Annots, the right /FT, both states of a check box's /AP, the radio group's /Kids sharing
/// one value. A viewer that finds a widget with no field, or a check box with only an "on"
/// appearance, does something wrong rather than nothing: the classic result is a form that comes
/// back with every box apparently ticked.
///
/// And the field has to actually be VISIBLE. /NeedAppearances asks a viewer to draw the field
/// itself, and the good ones do — which means a missing or malformed /AP stream is invisible in
/// testing and invisible on paper. So the page is rendered through PDFium with FPDF_ANNOT and the
/// pixels inside each field are checked, exactly as the redaction tests do.
/// </remarks>
internal static class Forms
{
    private static PdfiumInterop.PdfRect R(double l, double b, double w, double h) =>
        new(l, b, l + w, b + h);

    /// <summary>Fraction of pixels in a page-space rectangle that are not white.</summary>
    private static double InkIn(byte[] bgra, int rw, int rh, double pageH,
                               PdfiumInterop.PdfRect rect)
    {
        // Page rendered at 1px per point with no rotation and a zero-origin box, so the mapping is
        // just the y flip.
        int x0 = (int)Math.Max(0, rect.Left), x1 = (int)Math.Min(rw, rect.Right);
        int y0 = (int)Math.Max(0, pageH - rect.Top), y1 = (int)Math.Min(rh, pageH - rect.Bottom);
        int ink = 0, total = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int at = (y * rw + x) * 4;
                if (bgra[at] < 235 || bgra[at + 1] < 235 || bgra[at + 2] < 235) ink++;
                total++;
            }
        return total == 0 ? 0 : (double)ink / total;
    }

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nAcroForm authoring");

        string path = Path.Combine(tmp, "form.pdf");
        var text   = R(60, 700, 200, 22);
        var multi  = R(60, 620, 200, 60);
        var check  = R(60, 580, 16, 16);
        var drop   = R(60, 520, 160, 22);
        var sig    = R(60, 440, 200, 50);
        var radios = new[] { R(60, 380, 14, 14), R(120, 380, 14, 14), R(180, 380, 14, 14) };
        double pageH;

        {
            var doc = new PdfDocument();
            var page = doc.AddPage();
            pageH = page.Height.Point;

            PdfFormBuilder.Add(doc, page, new[] { text },
                new PdfFormBuilder.FieldSpec { Kind = PdfFormBuilder.FieldKind.Text, Name = "FullName", Tooltip = "Your full legal name", Required = true });
            PdfFormBuilder.Add(doc, page, new[] { multi },
                new PdfFormBuilder.FieldSpec { Kind = PdfFormBuilder.FieldKind.Text, Multiline = true });
            PdfFormBuilder.Add(doc, page, new[] { check },
                new PdfFormBuilder.FieldSpec { Kind = PdfFormBuilder.FieldKind.CheckBox });
            PdfFormBuilder.Add(doc, page, new[] { drop },
                new PdfFormBuilder.FieldSpec { Kind = PdfFormBuilder.FieldKind.Dropdown, Options = new[] { "Yes", "No", "Maybe" } });
            PdfFormBuilder.Add(doc, page, new[] { sig },
                new PdfFormBuilder.FieldSpec { Kind = PdfFormBuilder.FieldKind.Signature });
            PdfFormBuilder.Add(doc, page, radios,
                new PdfFormBuilder.FieldSpec { Kind = PdfFormBuilder.FieldKind.RadioGroup, Options = new[] { "Small", "Medium", "Large" } });

            doc.Save(path);
        }

        // ── Structure, read back from the saved file rather than from the objects we just built ──
        using var re = PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);
        var acro = re.Internals.Catalog.Elements.GetDictionary("/AcroForm");
        Check("the document has an /AcroForm", acro is not null, "");
        if (acro is null) return;

        var fields = acro.Elements.GetArray("/Fields");
        Check("all six fields are registered in /AcroForm /Fields", fields?.Elements.Count == 6,
              $"{fields?.Elements.Count ?? 0}");

        var dr = acro.Elements.GetDictionary("/DR")?.Elements.GetDictionary("/Font");
        Check("/DR carries both base fonts a field's /DA can name",
              dr?.Elements.ContainsKey("/Helv") == true && dr?.Elements.ContainsKey("/ZaDb") == true,
              dr is null ? "no /DR /Font" : string.Join(",", dr.Elements.Keys));
        Check("/NeedAppearances is set", acro.Elements.GetBoolean("/NeedAppearances"), "");

        var page0 = re.Pages[0];
        var annots = page0.Elements.GetArray("/Annots");
        // Five single-widget fields plus three radio buttons.
        Check("every widget is on the page", annots?.Elements.Count == 8, $"{annots?.Elements.Count ?? 0}");

        PdfDictionary? Deref(PdfItem? i) =>
            i as PdfDictionary ?? (i as PdfReference)?.Value as PdfDictionary;

        var byName = new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);
        for (int i = 0; i < (fields?.Elements.Count ?? 0); i++)
            if (Deref(fields!.Elements[i]) is { } f && f.Elements["/T"] is PdfString t)
                byName[t.Value] = f;
        Console.WriteLine($"        fields: {string.Join(", ", byName.Keys)}");

        Check("an explicit name is used as given", byName.ContainsKey("FullName"), "");
        Check("generated names do not collide",
              byName.ContainsKey("Text1") || byName.ContainsKey("Text2"),
              string.Join(",", byName.Keys));

        if (byName.TryGetValue("FullName", out var nameField))
        {
            Check("the text field is /Tx with a /DA", nameField.Elements.GetName("/FT") == "/Tx"
                  && nameField.Elements["/DA"] is PdfString, "");
            Check("the text field is marked required (/Ff bit 2)",
                  (nameField.Elements.GetInteger("/Ff") & 2) != 0, $"/Ff={nameField.Elements.GetInteger("/Ff")}");
            Check("the text field carries its tooltip", nameField.Elements["/TU"] is PdfString, "");
            Check("the widget prints (/F bit 3)", (nameField.Elements.GetInteger("/F") & 4) != 0, "");
        }

        var checkField = byName.Values.FirstOrDefault(f => f.Elements.GetName("/FT") == "/Btn"
                                                          && !f.Elements.ContainsKey("/Kids"));
        if (checkField is not null)
        {
            var n = Deref(checkField.Elements["/AP"])?.Elements.GetDictionary("/N");
            Check("the check box has BOTH appearance states",
                  n?.Elements.ContainsKey("/Off") == true && n.Elements.Keys.Any(k => k != "/Off"),
                  n is null ? "no /AP /N" : string.Join(",", n.Elements.Keys));
            Check("the check box starts cleared", checkField.Elements.GetName("/AS") == "/Off", "");
        }
        else Check("a check box was created", false, "none found");

        var group = byName.Values.FirstOrDefault(f => f.Elements.ContainsKey("/Kids"));
        if (group is not null)
        {
            var kids = group.Elements.GetArray("/Kids");
            Check("the radio group has one widget per option", kids?.Elements.Count == 3,
                  $"{kids?.Elements.Count ?? 0}");
            Check("the radio flag is set (/Ff bit 16)", (group.Elements.GetInteger("/Ff") & (1 << 15)) != 0,
                  $"/Ff={group.Elements.GetInteger("/Ff")}");

            var exports = new List<string>();
            bool allOff = true, allParented = true;
            for (int i = 0; i < (kids?.Elements.Count ?? 0); i++)
            {
                var kid = Deref(kids!.Elements[i]);
                if (kid is null) continue;
                allParented &= kid.Elements.ContainsKey("/Parent");
                allOff &= kid.Elements.GetName("/AS") == "/Off";
                var kn = Deref(kid.Elements["/AP"])?.Elements.GetDictionary("/N");
                if (kn is not null) exports.AddRange(kn.Elements.Keys.Where(k => k != "/Off"));
            }
            Check("each button points back at the group", allParented, "");
            Check("the group opens with nothing selected", allOff && group.Elements.GetName("/V") == "/Off", "");
            Check("each button has its own export value, taken from the option text",
                  exports.Distinct().Count() == 3 && exports.Contains("/Medium"),
                  string.Join(",", exports));
        }
        else Check("a radio group was created", false, "none found");

        var dropField = byName.Values.FirstOrDefault(f => f.Elements.GetName("/FT") == "/Ch");
        if (dropField is not null)
        {
            var opt = dropField.Elements.GetArray("/Opt");
            Check("the dropdown carries its options", opt?.Elements.Count == 3, $"{opt?.Elements.Count ?? 0}");
            Check("the combo flag is set (/Ff bit 18)",
                  (dropField.Elements.GetInteger("/Ff") & (1 << 17)) != 0,
                  $"/Ff={dropField.Elements.GetInteger("/Ff")}");
        }
        else Check("a dropdown was created", false, "none found");

        Check("a signature field was created",
              byName.Values.Any(f => f.Elements.GetName("/FT") == "/Sig"), "");

        // ── And they are actually visible ────────────────────────────────────────────────
        // Through the SHIPPING renderer, not a bare FPDF_RenderPageBitmap: TDPdf's print, flatten
        // and image-export paths all go through RenderPageWithAnnotations, which sets up a
        // form-fill environment and calls FPDF_FFLDraw as well as passing FPDF_ANNOT. Widgets are
        // drawn by that second pass, so a bare render says nothing about whether a field is
        // visible in any output the user can actually produce.
        int rw = 612, rh = 792;
        var bgra = PdfiumInterop.RenderPageWithAnnotations(path, 0, rw, rh)
                   ?? throw new Exception("the page did not render at all");
        foreach (var (label, rect) in new (string, PdfiumInterop.PdfRect)[]
                 { ("text", text), ("multiline", multi), ("check box", check),
                   ("dropdown", drop), ("signature", sig), ("radio button", radios[1]) })
        {
            double ink = InkIn(bgra, rw, rh, pageH, rect);
            Console.WriteLine($"        {label,-14} {ink:P1} of its box is ink");
            // A hairline border round a wide box is only a few percent of its area; the point is
            // that SOMETHING drew, against a page that is otherwise blank white.
            Check($"the {label} field renders", ink > 0.01, $"{ink:P2}");
        }

        double blank = InkIn(bgra, rw, rh, pageH, R(300, 200, 200, 100));
        Check("the rest of the page is still blank", blank < 0.001, $"{blank:P2}");

        Edits(Check, tmp);
    }

    /// <summary>
    /// Renaming, flag toggles, and deletion — checked on the SAVED file, not on the objects.
    /// </summary>
    /// <remarks>
    /// Deletion is the one that repays the extra step. Unlinking a field from /AcroForm /Fields and
    /// leaving its widget in the page's /Annots produces an orphan a viewer still draws; unlinking
    /// the widget and leaving the field produces something invisible that still turns up in
    /// exported form data. Either way the field looks deleted in the app and comes back in the
    /// file, so the test reopens what was written and counts what is actually there.
    /// </remarks>
    private static void Edits(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nEditing fields that already exist");

        string path = Path.Combine(tmp, "form-edit.pdf");
        {
            var doc = new PdfDocument();
            var page = doc.AddPage();
            PdfFormBuilder.Add(doc, page, new[] { R(60, 700, 200, 22) },
                new PdfFormBuilder.FieldSpec { Name = "Keeper" });
            PdfFormBuilder.Add(doc, page, new[] { R(60, 650, 200, 22) },
                new PdfFormBuilder.FieldSpec { Name = "Doomed" });
            PdfFormBuilder.Add(doc, page,
                new[] { R(60, 600, 14, 14), R(100, 600, 14, 14), R(140, 600, 14, 14) },
                new PdfFormBuilder.FieldSpec { Kind = PdfFormBuilder.FieldKind.RadioGroup, Name = "Size", Options = new[] { "S", "M", "L" } });
            doc.Save(path);
        }

        PdfDictionary? WidgetNamed(PdfDocument d, string name)
        {
            var annots = d.Pages[0].Elements.GetArray("/Annots");
            for (int i = 0; i < (annots?.Elements.Count ?? 0); i++)
            {
                var a = annots!.Elements[i] as PdfDictionary ?? (annots.Elements[i] as PdfReference)?.Value as PdfDictionary;
                if (a is not null && PdfFormBuilder.NameOf(a) == name) return a;
            }
            return null;
        }

        string outPath = Path.Combine(tmp, "form-edited.pdf");
        {
            using var doc = PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);

            var keeper = WidgetNamed(doc, "Keeper")!;
            Check("a merged field finds itself", ReferenceEquals(PdfFormBuilder.FieldOf(keeper), keeper), "");

            // A radio button has to climb to the group that owns the name and the value.
            var radioKid = doc.Pages[0].Elements.GetArray("/Annots")!.Elements
                .Select(i => i as PdfDictionary ?? (i as PdfReference)?.Value as PdfDictionary)
                .First(a => a is not null && a.Elements.ContainsKey("/Parent") && !a.Elements.ContainsKey("/T"))!;
            Check("a radio button finds its group", PdfFormBuilder.NameOf(radioKid) == "Size",
                  PdfFormBuilder.NameOf(radioKid));

            Check("a duplicate name is refused",
                  !PdfFormBuilder.TryRename(doc, keeper, "Doomed", out string? dupErr), dupErr ?? "");
            Check("a name with a full stop is refused",
                  !PdfFormBuilder.TryRename(doc, keeper, "Person.Name", out _), "");
            Check("a free name is accepted",
                  PdfFormBuilder.TryRename(doc, keeper, "PrimaryName", out _), "");

            PdfFormBuilder.SetFlag(keeper, required: true, on: true);
            Check("the required flag goes on", PdfFormBuilder.GetFlag(keeper, required: true), "");
            PdfFormBuilder.SetFlag(keeper, required: true, on: false);
            Check("and comes off again, taking an empty /Ff with it",
                  !PdfFormBuilder.GetFlag(keeper, required: true) && !keeper.Elements.ContainsKey("/Ff"), "");

            Check("deleting a plain field reports success",
                  PdfFormBuilder.RemoveField(doc, WidgetNamed(doc, "Doomed")!), "");
            Check("deleting a radio group reports success",
                  PdfFormBuilder.RemoveField(doc, radioKid), "");

            doc.Save(outPath);
        }

        using (var re = PdfSharpCore.Pdf.IO.PdfReader.Open(outPath, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify))
        {
            var fields = re.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields");
            var annots = re.Pages[0].Elements.GetArray("/Annots");
            Console.WriteLine($"        after: {fields?.Elements.Count ?? 0} field(s), {annots?.Elements.Count ?? 0} widget(s)");

            Check("only the surviving field is left in /AcroForm /Fields", fields?.Elements.Count == 1,
                  $"{fields?.Elements.Count ?? 0}");
            // One field, one widget: the deleted radio group took all three of its buttons.
            Check("no orphaned widget is left on the page", annots?.Elements.Count == 1,
                  $"{annots?.Elements.Count ?? 0}");
            Check("the rename survived the save", WidgetNamed(re, "PrimaryName") is not null, "");
            Check("the deleted field is gone from the file",
                  WidgetNamed(re, "Doomed") is null && WidgetNamed(re, "Size") is null, "");
        }
    }
}
