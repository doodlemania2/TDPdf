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

        private static bool IsOffValue([NotNullWhen(false)] string? v) =>
            string.IsNullOrEmpty(v) || SameName(v, "/Off");

        // "/Yes" from a PdfName and "Yes" from a PdfString name the same state.
        private static bool SameName(string a, string b) =>
            string.Equals(a.TrimStart('/'), b.TrimStart('/'), StringComparison.Ordinal);

        private static PdfDictionary? Deref(PdfItem? item) =>
            item as PdfDictionary ?? (item as PdfReference)?.Value as PdfDictionary;
    }
}
