using PdfSharpCore.Pdf;

namespace TDPdf.Services
{
    /// <summary>
    /// Keeps form fields alive across a merge. Importing a page copies its /Annots, so the widgets
    /// arrive, but the source /AcroForm does not: the widgets were then in no /Fields array (viewers
    /// that build the form from /AcroForm ignore or mis-handle them) and the /DR fonts their /DA
    /// strings name were gone.
    /// </summary>
    internal static class PdfFormMerge
    {
        internal sealed record Result(int FieldsAdopted, int FieldsRenamed, int FontsAdded, IReadOnlyList<string> Notes);

        private const int MaxParentDepth = 64;

        /// <summary>
        /// Registers the fields of pages <paramref name="pageOffset"/> onwards (the ones just
        /// imported from <paramref name="source"/>) in <paramref name="target"/>'s /AcroForm.
        /// </summary>
        /// <remarks>
        /// Different fields that share a fully-qualified name share one value in a PDF, so two
        /// unrelated forms merged together would silently tie their boxes. An incoming top-level
        /// field whose name is already used in the target is renamed with a numeric suffix
        /// ("Name" becomes "Name_2"); incoming fields that shared a name with each other keep sharing.
        /// /NeedAppearances is left exactly as the target has it. A /DR font whose resource name the
        /// target already uses for a different font is not copied (reported in the notes), because
        /// widgets of the target depend on that name.
        /// </remarks>
        internal static Result AdoptImportedWidgets(PdfDocument target, int pageOffset, PdfDocument? source)
        {
            var notes = new List<string>();
            if (target is null) return new Result(0, 0, 0, notes);

            // Top-level fields of the imported pages' widgets, in page order, each once.
            var roots = new List<PdfDictionary>();
            for (int p = Math.Max(0, pageOffset); p < target.PageCount; p++)
            {
                var annots = target.Pages[p].Elements.GetArray("/Annots");
                if (annots is null) continue;
                for (int i = 0; i < annots.Elements.Count; i++)
                {
                    var w = PdfItems.DerefDict(annots.Elements[i]);
                    if (w is null || w.Elements.GetName("/Subtype") != "/Widget") continue;
                    if (!PdfItems.Ancestors(w).Any(n => n.Elements["/FT"] is not null)) continue;   // not a field
                    var root = RootOf(w);
                    if (!roots.Any(r => ReferenceEquals(r, root))) roots.Add(root);
                }
            }

            var srcForm = source?.Internals.Catalog.Elements.GetDictionary("/AcroForm");
            if (roots.Count == 0 && srcForm is null) return new Result(0, 0, 0, notes);

            var acro = target.Internals.Catalog.Elements.GetDictionary("/AcroForm");
            if (acro is null && roots.Count > 0)
            {
                acro = new PdfDictionary(target);
                target.Internals.AddObject(acro);
                target.Internals.Catalog.Elements["/AcroForm"] = acro.Reference!;
            }
            if (acro is null) return new Result(0, 0, 0, notes);

            var fields = acro.Elements.GetArray("/Fields");
            if (fields is null)
            {
                fields = new PdfArray(target);
                acro.Elements["/Fields"] = fields;
            }

            // Names already taken by fields that were in the target before this merge.
            var taken = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < fields.Elements.Count; i++)
                if (PdfItems.DerefDict(fields.Elements[i]) is { } f && f.Elements["/T"] is PdfString t)
                    taken.Add(t.Value);

            int adopted = 0, renamed = 0;
            var renames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var root in roots)
            {
                if (fields.Elements.Count > 0 && Contains(fields, root)) continue;

                if (root.Elements["/T"] is PdfString name && taken.Contains(name.Value))
                {
                    if (!renames.TryGetValue(name.Value, out var fresh))
                    {
                        int n = 2;
                        do fresh = $"{name.Value}_{n++}"; while (taken.Contains(fresh));
                        renames[name.Value] = fresh;
                        taken.Add(fresh);
                    }
                    root.Elements["/T"] = new PdfString(fresh);
                    renamed++;
                    notes.Add($"field \"{name.Value}\" already existed, merged copy is \"{fresh}\"");
                }
                fields.Elements.Add(root.Reference ?? (PdfItem)root);
                adopted++;
            }

            int fontsAdded = 0;
            if (srcForm is not null)
            {
                if (acro.Elements["/DA"] is null && srcForm.Elements["/DA"] is PdfString da)
                    acro.Elements["/DA"] = new PdfString(da.Value);
                if (acro.Elements["/Q"] is null && srcForm.Elements["/Q"] is PdfInteger q)
                    acro.Elements["/Q"] = new PdfInteger(q.Value);

                var srcFonts = srcForm.Elements.GetDictionary("/DR")?.Elements.GetDictionary("/Font");
                if (srcFonts is not null && roots.Count > 0)
                {
                    var dr = acro.Elements.GetDictionary("/DR");
                    if (dr is null) { dr = new PdfDictionary(target); acro.Elements["/DR"] = dr; }
                    var fonts = dr.Elements.GetDictionary("/Font");
                    if (fonts is null) { fonts = new PdfDictionary(target); dr.Elements["/Font"] = fonts; }

                    foreach (var key in srcFonts.Elements.Keys.ToList())
                    {
                        var srcFont = PdfItems.DerefDict(srcFonts.Elements[key]);
                        if (srcFont is null) continue;
                        var have = PdfItems.DerefDict(fonts.Elements[key]);
                        if (have is not null)
                        {
                            if (!string.Equals(have.Elements.GetName("/BaseFont"), srcFont.Elements.GetName("/BaseFont"), StringComparison.Ordinal))
                                notes.Add($"font {key} differs between the documents; the first document's was kept");
                            continue;
                        }
                        try
                        {
                            fonts.Elements[key] = srcFont.Reference is not null
                                ? target.Pages.ImportExternalObject(srcFont)
                                : srcFont.Clone();
                            fontsAdded++;
                        }
                        catch (Exception ex)
                        {
                            notes.Add($"font {key} could not be carried across ({ex.GetType().Name})");
                        }
                    }
                }
            }
            return new Result(adopted, renamed, fontsAdded, notes);
        }

        private static PdfDictionary RootOf(PdfDictionary widget)
        {
            var cur = widget;
            for (int depth = 0; depth < MaxParentDepth; depth++)
            {
                var parent = PdfItems.ParentOf(cur);
                if (parent is null || ReferenceEquals(parent, cur)) break;
                cur = parent;
            }
            return cur;
        }

        private static bool Contains(PdfArray arr, PdfDictionary d)
        {
            for (int i = 0; i < arr.Elements.Count; i++)
                if (ReferenceEquals(PdfItems.DerefDict(arr.Elements[i]), d)) return true;
            return false;
        }
    }
}
