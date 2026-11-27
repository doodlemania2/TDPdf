using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;

namespace TDPdf.Services
{
    internal enum FormTargetKind { Text, Choice, CheckBox, Radio }

    /// <summary>
    /// One fillable field of the open document, as form import sees it. <see cref="Key"/> is the key
    /// the pending-value stores use: the field key for text, choice and check boxes
    /// (<see cref="FormFieldSiblings.FieldKey"/>) and, for a radio group, the name the radio store
    /// is keyed by (<see cref="RadioName"/>).
    /// </summary>
    internal sealed class FormTarget
    {
        public required string QualifiedName { get; init; }
        public required FormTargetKind Kind { get; init; }
        public int Key { get; init; }
        public string RadioName { get; init; } = "";
        /// <summary>Check box and radio: every on-state the field's widgets can take, with the leading slash (/Yes).</summary>
        public List<string> OnValues { get; } = [];
        /// <summary>Choice fields: display strings and the export values that go in /V, index for index.</summary>
        public List<string> Options { get; } = [];
        public List<string> OptionExports { get; } = [];
        public int CombLen { get; set; }
        public bool ReadOnly { get; init; }
    }

    /// <summary>How an imported value is to be stored, for one target.</summary>
    internal sealed record FormAssignment(FormTarget Target, string Value);

    /// <summary>What an import would do. Built by <see cref="FormImport.Plan"/>.</summary>
    internal sealed class FormImportPlan
    {
        public List<FormAssignment> Assignments { get; } = [];
        /// <summary>Names in the file that no field of the form has.</summary>
        public List<string> Unmatched { get; } = [];
        /// <summary>Fields found but left alone, with the reason (read-only, value not offered by the list...).</summary>
        public List<string> Skipped { get; } = [];
        public int FieldsMatched { get; set; }
    }

    /// <summary>
    /// Fills a form from imported data: finds the form's fields by fully qualified name (scanning
    /// every page's widgets, so a damaged <c>/AcroForm /Fields</c> list does not matter), decides the
    /// value each should take, and reports the rest. Pure PdfSharpCore, tested in tests/PdfCore; the
    /// window applies the plan to its pending-value stores in one undo step.
    /// </summary>
    internal static class FormImport
    {
        private static readonly string[] Truthy = ["yes", "on", "true", "1", "x", "checked", "y"];
        private static readonly string[] Falsy = ["off", "no", "false", "0", "", "unchecked", "n"];

        /// <summary>
        /// Every fillable field on every page's widgets. Widgets sharing a field key (one field, several
        /// places) are one target; fields sharing a name stay separate targets, so a value reaches all of them.
        /// </summary>
        internal static List<FormTarget> BuildTargets(PdfDocument doc)
        {
            var byKey = new Dictionary<string, FormTarget>();
            var result = new List<FormTarget>();
            for (int p = 0; p < doc.PageCount; p++)
            {
                PdfArray? annots;
                try { annots = doc.Pages[p].Elements.GetArray("/Annots"); } catch (Exception) { continue; }
                if (annots is null) continue;
                int count = Math.Min(annots.Elements.Count, 100_000);
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var elem = annots.Elements[i];
                        var widget = PdfItems.DerefDict(elem);
                        if (widget is null || widget.Elements["/Subtype"]?.ToString()?.Contains("Widget") != true) continue;

                        string ft = "";
                        int flags = 0;
                        bool haveFlags = false;
                        string leafName = "";
                        var options = new List<string>();
                        var exports = new List<string>();
                        foreach (var node in PdfItems.Ancestors(widget))
                        {
                            if (ft.Length == 0 && node.Elements["/FT"] is { } fti) ft = fti.ToString() ?? "";
                            if (!haveFlags && node.Elements["/Ff"] is PdfInteger fi) { flags = fi.Value; haveFlags = true; }
                            if (leafName.Length == 0 && node.Elements["/T"] is PdfString ts) leafName = ts.Value;
                            if (options.Count == 0 && PdfItems.Deref(node.Elements["/Opt"]) is PdfArray opt)
                            {
                                foreach (var o in opt.Elements.Take(10_000))
                                {
                                    var ro = PdfItems.Deref(o);
                                    if (ro is PdfString ps) { options.Add(ps.Value); exports.Add(ps.Value); }
                                    else if (ro is PdfArray pa && pa.Elements.Count >= 2)
                                    {
                                        exports.Add((PdfItems.Deref(pa.Elements[0]) as PdfString)?.Value ?? "");
                                        options.Add((PdfItems.Deref(pa.Elements[1]) as PdfString)?.Value ?? "");
                                    }
                                }
                            }
                        }
                        if (ft.Length == 0) ft = "/Tx";
                        string qualified = FormFieldSiblings.QualifiedName(widget);
                        if (qualified.Length == 0) continue;

                        bool readOnly = (flags & 1) != 0;
                        FormTargetKind kind;
                        if (ft.Contains("Tx")) kind = FormTargetKind.Text;
                        else if (ft.Contains("Ch")) kind = FormTargetKind.Choice;
                        else if (ft.Contains("Btn"))
                        {
                            if ((flags & (1 << 16)) != 0) continue;   // push button: holds no value
                            kind = (flags & (1 << 15)) != 0 ? FormTargetKind.Radio : FormTargetKind.CheckBox;
                        }
                        else continue;

                        int objNum = elem is PdfReference r ? r.ObjectNumber : widget.Reference?.ObjectNumber ?? -1;
                        if (objNum < 0) objNum = -(p * 10000 + i);
                        int key = FormFieldSiblings.FieldKey(widget, objNum);

                        // A radio group is one field: every widget of the same name joins it, since the
                        // radio store is keyed by name, not by widget.
                        string mapKey = kind == FormTargetKind.Radio ? "R|" + qualified : kind + "|" + key;
                        if (!byKey.TryGetValue(mapKey, out var target))
                        {
                            target = new FormTarget
                            {
                                QualifiedName = qualified, Kind = kind, Key = key, RadioName = leafName, ReadOnly = readOnly,
                            };
                            target.Options.AddRange(options);
                            target.OptionExports.AddRange(exports);
                            if (kind == FormTargetKind.Text) target.CombLen = CombLen(widget);
                            byKey[mapKey] = target;
                            result.Add(target);
                        }
                        if (kind is FormTargetKind.CheckBox or FormTargetKind.Radio)
                        {
                            string on = OnStateOf(widget);
                            if (!target.OnValues.Contains(on)) target.OnValues.Add(on);
                        }
                    }
                    catch (Exception) { /* one damaged widget must not stop the import */ }
                }
            }
            return result;
        }

        private static string OnStateOf(PdfDictionary widget)
        {
            try
            {
                var nDict = PdfItems.DerefDict(PdfItems.DerefDict(widget.Elements["/AP"])?.Elements["/N"]);
                if (nDict is not null)
                    foreach (var k in nDict.Elements.Keys)
                        if (k != "/Off") return k;
            }
            catch (Exception) { }
            return "/Yes";
        }

        private static int CombLen(PdfDictionary widget)
        {
            int flags = 0, maxLen = 0;
            bool haveFlags = false;
            foreach (var node in PdfItems.Ancestors(widget))
            {
                if (!haveFlags && node.Elements["/Ff"] is PdfInteger fi) { flags = fi.Value; haveFlags = true; }
                if (maxLen == 0 && node.Elements["/MaxLen"] is PdfInteger ml) maxLen = ml.Value;
            }
            return (flags & (1 << 24)) != 0 && (flags & 4096) == 0 && maxLen > 0 ? maxLen : 0;
        }

        /// <summary>Decides what each imported field does to the form.</summary>
        internal static FormImportPlan Plan(IEnumerable<ImportedField> imported, IReadOnlyList<FormTarget> targets)
        {
            var plan = new FormImportPlan();
            var exact = new Dictionary<string, List<FormTarget>>(StringComparer.Ordinal);
            var folded = new Dictionary<string, List<FormTarget>>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in targets)
            {
                (exact.TryGetValue(t.QualifiedName, out var a) ? a : exact[t.QualifiedName] = []).Add(t);
                (folded.TryGetValue(t.QualifiedName, out var b) ? b : folded[t.QualifiedName] = []).Add(t);
            }

            var done = new HashSet<FormTarget>();   // a name given twice in the file: the first value wins
            foreach (var f in imported)
            {
                if (!exact.TryGetValue(f.Name, out var matches))
                {
                    // Same name in a different case: accepted when it points at one field name only.
                    if (folded.TryGetValue(f.Name, out var alt) && alt.Select(t => t.QualifiedName).Distinct().Count() == 1) matches = alt;
                }
                if (matches is null) { if (!plan.Unmatched.Contains(f.Name)) plan.Unmatched.Add(f.Name); continue; }
                plan.FieldsMatched++;

                foreach (var t in matches)
                {
                    if (!done.Add(t)) continue;
                    if (t.ReadOnly) { plan.Skipped.Add($"{f.Name} (read-only)"); continue; }
                    if (f.Value is null) continue;   // the file gives this field no value: leave the form as it is

                    string? value = ValueFor(t, f, out string? why);
                    if (value is null) { plan.Skipped.Add($"{f.Name} ({why})"); continue; }
                    plan.Assignments.Add(new FormAssignment(t, value));
                }
            }
            return plan;
        }

        private static string? ValueFor(FormTarget t, ImportedField f, out string? why)
        {
            why = null;
            string raw = f.Value ?? "";
            switch (t.Kind)
            {
                case FormTargetKind.Text:
                    return FormFieldSiblings.FitToComb(raw, t.CombLen);

                case FormTargetKind.Choice:
                {
                    if (t.Options.Count == 0) return raw;   // no list to check against
                    int i = t.OptionExports.IndexOf(raw);
                    if (i < 0) i = t.Options.IndexOf(raw);
                    if (i < 0) i = t.OptionExports.FindIndex(o => string.Equals(o, raw, StringComparison.OrdinalIgnoreCase));
                    if (i < 0) i = t.Options.FindIndex(o => string.Equals(o, raw, StringComparison.OrdinalIgnoreCase));
                    if (i < 0) { why = "not one of the list's choices"; return null; }
                    return i < t.OptionExports.Count ? t.OptionExports[i] : t.Options[i];
                }

                case FormTargetKind.CheckBox:
                case FormTargetKind.Radio:
                {
                    string bare = raw.TrimStart('/');
                    string? on = t.OnValues.FirstOrDefault(o => o.TrimStart('/') == bare)
                                 ?? t.OnValues.FirstOrDefault(o => string.Equals(o.TrimStart('/'), bare, StringComparison.OrdinalIgnoreCase));
                    if (on is not null) return on;
                    if (Falsy.Contains(bare.Trim().ToLowerInvariant())) return "/Off";
                    if (t.Kind == FormTargetKind.CheckBox && Truthy.Contains(bare.Trim().ToLowerInvariant()) && t.OnValues.Count > 0)
                        return t.OnValues[0];
                    why = "not one of the button's states";
                    return null;
                }
            }
            return null;
        }
    }
}
