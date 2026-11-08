using System.Diagnostics.CodeAnalysis;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;

namespace TDPdf.Services
{
    // ============================================================
    // One AcroForm field can have several widgets: the same name printed on page 1 and page 3, or
    // a check box repeated in two places. They share ONE value. Pending values used to be keyed by
    // widget, so typing in one copy left the others stale and two copies edited differently raced
    // for the field's /V on save. These helpers key a value by its FIELD and decide what each
    // widget shows for it.
    // ============================================================
    internal static class FormFieldSiblings
    {
        private const int MaxDepth = 64;   // a /Parent cycle in a malformed file must not hang

        /// <summary>
        /// The terminal field a widget belongs to: the nearest node, starting at the widget itself,
        /// that carries a partial name /T. A widget merged with its field (the common single-widget
        /// case) is its own field; a widget with no /T anywhere above it is treated the same way.
        /// </summary>
        internal static PdfDictionary TerminalField(PdfDictionary widget)
        {
            PdfDictionary? node = widget;
            for (int depth = 0; node is not null && depth < MaxDepth; depth++)
            {
                if (node.Elements["/T"] is PdfString) return node;
                node = Deref(node.Elements["/Parent"]);
            }
            return widget;
        }

        /// <summary>
        /// The key pending values are stored under. A widget that is its own field keeps
        /// <paramref name="widgetKey"/> (its object number, or the synthetic negative key for a
        /// direct-object widget), so single-widget fields behave exactly as before. A widget whose
        /// field is a separate indirect object uses that object's number, which every sibling
        /// shares. A direct-object parent has no number to share, so it falls back to the widget.
        /// </summary>
        internal static int FieldKey(PdfDictionary widget, int widgetKey)
        {
            var field = TerminalField(widget);
            if (ReferenceEquals(field, widget)) return widgetKey;
            int n = field.Reference?.ObjectNumber ?? 0;
            return n > 0 ? n : widgetKey;
        }

        /// <summary>
        /// Writes a field value: /V on the terminal field, plus the same value over any stale /V
        /// sitting on a node between the widget and that field, which a reader walking up from the
        /// widget would otherwise find first.
        /// </summary>
        internal static void SetValue(PdfDictionary widget, PdfItem value)
        {
            var field = TerminalField(widget);
            PdfDictionary? node = widget;
            for (int depth = 0; node is not null && depth < MaxDepth; depth++)
            {
                if (ReferenceEquals(node, field)) break;
                if (node.Elements.ContainsKey("/V")) node.Elements["/V"] = value;
                node = Deref(node.Elements["/Parent"]);
            }
            field.Elements["/V"] = value;
        }

        /// <summary>True when the field value <paramref name="fieldValue"/> turns on the widget whose on-state is <paramref name="widgetOn"/>.</summary>
        internal static bool IsOn(string? fieldValue, string widgetOn) =>
            !IsOffValue(fieldValue) && SameName(fieldValue, widgetOn);

        /// <summary>
        /// The /AS a check box widget gets for a field value: its OWN on-state name when the value
        /// selects it, else /Off. Siblings can carry different on-state names (export values), so
        /// one value can turn one widget on and leave another off.
        /// </summary>
        internal static string CheckStateFor(string? fieldValue, string widgetOn) =>
            IsOn(fieldValue, widgetOn) ? widgetOn : "/Off";

        /// <summary>The field value after clicking a check box widget.</summary>
        internal static string Toggle(bool widgetChecked, string widgetOn) => widgetChecked ? "/Off" : widgetOn;

        /// <summary>
        /// Whether a check box widget starts ticked, from the field's /V and the widget's own /AS.
        /// /V naming this widget's on-state settles it. /V naming something else (a sibling's
        /// on-state, or a value spelled differently from the /AP key) defers to the widget's /AS,
        /// which is what every viewer actually draws.
        /// </summary>
        internal static bool InitiallyChecked(string? fieldValue, string widgetOn, string? widgetState)
        {
            if (IsOffValue(fieldValue)) return false;
            if (SameName(fieldValue, widgetOn)) return true;
            return !IsOffValue(widgetState) && SameName(widgetState, widgetOn);
        }

        // ------------------------------------------------------------------------------------
        // Fields that share a fully qualified name. Per the PDF spec two fields with the same
        // fully qualified name are ONE logical field, even when they are separate top-level
        // objects (a name printed on page 1 and again on page 3 by a form generator that wrote two
        // fields instead of one field with two widgets). Pending values are stored per FieldKey, so
        // an edit is copied to every key in the same group.
        // ------------------------------------------------------------------------------------

        /// <summary>One field's identity for name grouping.</summary>
        internal readonly record struct FieldRef(string GroupId, int Key, int CombLen);

        /// <summary>The dotted fully qualified name: every /T from the root field down to the widget's field.</summary>
        internal static string QualifiedName(PdfDictionary widget)
        {
            var parts = new List<string>();
            PdfDictionary? node = widget;
            for (int depth = 0; node is not null && depth < MaxDepth; depth++)
            {
                if (node.Elements["/T"] is PdfString t && t.Value.Length > 0) parts.Add(t.Value);
                node = Deref(node.Elements["/Parent"]);
            }
            parts.Reverse();
            return string.Join(".", parts);
        }

        /// <summary>
        /// "Tx|name", "Ch|name" or "Btn|name" for a field that can share its value by name; "" for
        /// anything that cannot (no name, push button, radio group, which already pools by name).
        /// A text field and a check box that happen to share a name are different fields.
        /// </summary>
        internal static string GroupIdOf(PdfDictionary widget)
        {
            string ft = "";
            int flags = 0;
            bool haveFlags = false;
            PdfDictionary? node = widget;
            for (int depth = 0; node is not null && depth < MaxDepth; depth++)
            {
                if (ft.Length == 0 && node.Elements["/FT"] is { } ftItem) ft = ftItem.ToString() ?? "";
                if (!haveFlags && node.Elements["/Ff"] is PdfInteger fi) { flags = fi.Value; haveFlags = true; }
                node = Deref(node.Elements["/Parent"]);
            }
            if (ft.Length == 0) ft = "/Tx";
            string kind = ft.Contains("Tx") ? "Tx" : ft.Contains("Ch") ? "Ch" : ft.Contains("Btn") ? "Btn" : "";
            if (kind.Length == 0) return "";
            if (kind == "Btn" && (flags & ((1 << 16) | (1 << 15))) != 0) return "";   // push button or radio
            return MakeGroupId(kind, QualifiedName(widget));
        }

        /// <summary>The group id for a type and fully qualified name; "" when the name is empty.</summary>
        internal static string MakeGroupId(string kind, string qualifiedName) =>
            string.IsNullOrEmpty(qualifiedName) ? "" : kind + "|" + qualifiedName;

        /// <summary>Every field key sharing <paramref name="groupId"/>, the origin included, without repeats.</summary>
        internal static List<FieldRef> Siblings(IEnumerable<FieldRef> all, string groupId)
        {
            var result = new List<FieldRef>();
            if (string.IsNullOrEmpty(groupId)) return result;
            var seen = new HashSet<int>();
            foreach (var f in all)
                if (f.GroupId == groupId && seen.Add(f.Key)) result.Add(f);
            return result;
        }

        /// <summary>A text value as a field with <paramref name="combLen"/> cells can hold it (0 = no cap).</summary>
        internal static string FitToComb(string value, int combLen) =>
            combLen > 0 && value.Length > combLen ? value.Substring(0, combLen) : value;

        /// <summary>
        /// Scans every page's widgets into <see cref="FieldRef"/>s, using the same key the overlay
        /// and the save path use (<see cref="FieldKey"/> over the widget's object number, or the
        /// synthetic negative page/position key for a direct-object widget).
        /// </summary>
        internal static List<FieldRef> BuildIndex(PdfDocument doc)
        {
            var result = new List<FieldRef>();
            for (int p = 0; p < doc.PageCount; p++)
            {
                var annots = doc.Pages[p].Elements.GetArray("/Annots");
                if (annots is null) continue;
                for (int i = 0; i < annots.Elements.Count; i++)
                {
                    var elem = annots.Elements[i];
                    var widget = Deref(elem);
                    if (widget is null) continue;
                    if (widget.Elements["/Subtype"]?.ToString()?.Contains("Widget") != true) continue;
                    string gid = GroupIdOf(widget);
                    if (gid.Length == 0) continue;
                    int objNum = elem is PdfReference r ? r.ObjectNumber : widget.Reference?.ObjectNumber ?? -1;
                    if (objNum < 0) objNum = -(p * 10000 + i);
                    int comb = 0;
                    if (gid.StartsWith("Tx|", StringComparison.Ordinal)) comb = CombLenOf(widget);
                    result.Add(new FieldRef(gid, FieldKey(widget, objNum), comb));
                }
            }
            return result;
        }

        private static int CombLenOf(PdfDictionary widget)
        {
            int flags = 0, maxLen = 0;
            bool haveFlags = false;
            PdfDictionary? node = widget;
            for (int depth = 0; node is not null && depth < MaxDepth; depth++)
            {
                if (!haveFlags && node.Elements["/Ff"] is PdfInteger fi) { flags = fi.Value; haveFlags = true; }
                if (maxLen == 0 && node.Elements["/MaxLen"] is PdfInteger ml) maxLen = ml.Value;
                node = Deref(node.Elements["/Parent"]);
            }
            bool multi = (flags & 4096) != 0, comb = (flags & (1 << 24)) != 0;
            return comb && !multi && maxLen > 0 ? maxLen : 0;
        }

        private static bool IsOffValue([NotNullWhen(false)] string? v) =>
            string.IsNullOrEmpty(v) || SameName(v, "/Off");

        // "/Yes" from a PdfName and "Yes" from a PdfString name the same state.
        private static bool SameName(string a, string b) =>
            string.Equals(a.TrimStart('/'), b.TrimStart('/'), StringComparison.Ordinal);

        private static PdfDictionary? Deref(PdfItem? item) =>
            item as PdfDictionary ?? (item as PdfReference)?.Value as PdfDictionary;
    }
}
