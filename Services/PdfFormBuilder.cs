using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;

namespace TDPdf.Services
{
    /// <summary>
    /// Creates AcroForm fields — the authoring half of the form support TDPdf has always had the
    /// filling half of.
    /// </summary>
    /// <remarks>
    /// Everything here writes the raw dictionaries rather than going through XGraphics, because a
    /// form field is structure, not drawing: a field object in the document's /AcroForm /Fields, a
    /// widget annotation in the page's /Annots, and an appearance stream so viewers that do not
    /// generate their own still show something.
    ///
    /// FIELD AND WIDGET ARE THE SAME DICTIONARY here, which the spec explicitly allows (PDF 32000-1
    /// 12.7.3.1) whenever a field has exactly one widget. It is what Acrobat itself writes for
    /// ordinary fields, it halves the object count, and — the reason that matters to this codebase —
    /// it is the shape TDPdf's own reader already walks: GetPageFormFields starts at the page's
    /// /Annots and climbs /Parent for inherited attributes, so a merged dictionary is found in one
    /// step. Radio groups are the one exception, because a group is several widgets sharing one
    /// value and therefore genuinely needs a parent field with /Kids.
    ///
    /// APPEARANCE STREAMS ARE WRITTEN ANYWAY, even though /NeedAppearances is set. The flag asks a
    /// viewer to build appearances itself, and the good ones do — but it is a request, not a
    /// guarantee: plenty of viewers, most PDF printing pipelines, and TDPdf's own flatten path
    /// render what is in /AP and nothing else. A field with no /AP is invisible in all of them.
    ///
    /// ONE THING TO KNOW WHEN CHECKING THE OUTPUT: a plain FPDF_RenderPageBitmap with FPDF_ANNOT
    /// draws nothing for these fields. PDFium leaves widget annotations to the form-fill layer, so
    /// they appear only once a form environment exists and FPDF_FFLDraw runs — which is exactly
    /// what PdfiumInterop.RenderPageWithAnnotations sets up, and therefore what TDPdf's print,
    /// flatten and image-export paths all get. A bare render showing a blank page is not evidence
    /// the fields are missing; it is evidence the wrong renderer was asked.
    /// </remarks>
    internal static class PdfFormBuilder
    {
        // /Ff bit numbers are 1-based in the spec; these are the shifts that produce them.
        private const int FfReadOnly  = 1 << 0;    // bit 1
        private const int FfRequired  = 1 << 1;    // bit 2
        private const int FfMultiline = 1 << 12;   // bit 13
        private const int FfRadio     = 1 << 15;   // bit 16
        private const int FfCombo     = 1 << 17;   // bit 18
        private const int FfEdit      = 1 << 18;   // bit 19  (editable combo)
        private const int FfComb      = 1 << 24;   // bit 25

        /// <summary>Annotation /F: Print. Without it the field shows on screen and vanishes on paper.</summary>
        private const int AnnotPrint = 4;

        /// <summary>Resource names for the two base-14 fonts every AcroForm needs.</summary>
        private const string HelvName = "/Helv";
        private const string ZaDbName = "/ZaDb";

        internal enum FieldKind { Text, CheckBox, RadioGroup, Dropdown, Signature }

        internal sealed class FieldSpec
        {
            internal FieldKind Kind { get; init; } = FieldKind.Text;

            /// <summary>Fully qualified field name. Blank asks for a generated one.</summary>
            internal string Name { get; init; } = "";

            /// <summary>/TU, which viewers show as the field's tooltip and screen readers read out.</summary>
            internal string Tooltip { get; init; } = "";

            internal bool ReadOnly { get; init; }
            internal bool Required { get; init; }

            /// <summary>Text fields only.</summary>
            internal bool Multiline { get; init; }

            /// <summary>Text fields only. 0 = unlimited; with <see cref="Comb"/>, the cell count.</summary>
            internal int MaxLength { get; init; }

            /// <summary>Text fields only: one character per evenly spaced cell. Needs a MaxLength.</summary>
            internal bool Comb { get; init; }

            /// <summary>Dropdowns and radio groups.</summary>
            internal IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();

            /// <summary>Dropdowns only: let the user type a value that is not in the list.</summary>
            internal bool Editable { get; init; }

            /// <summary>Points. 0 means auto-size, which is what /DA "0 Tf" asks for.</summary>
            internal double FontSize { get; init; }
        }

        /// <summary>
        /// Adds one field to <paramref name="page"/> at <paramref name="rect"/> (PDF user space).
        /// </summary>
        /// <param name="rects">
        /// One rectangle per widget. Every kind but <see cref="FieldKind.RadioGroup"/> uses the
        /// first and ignores the rest; a radio group places one button per rectangle, paired with
        /// <see cref="FieldSpec.Options"/> in order.
        /// </param>
        /// <returns>The field dictionary, or null if the request was not usable.</returns>
        internal static PdfDictionary? Add(
            PdfDocument doc, PdfPage page, IReadOnlyList<PdfiumInterop.PdfRect> rects, FieldSpec spec)
        {
            if (doc is null || page is null || rects is null || rects.Count == 0) return null;

            var acroForm = EnsureAcroForm(doc);
            string name = string.IsNullOrWhiteSpace(spec.Name)
                ? UniqueName(doc, DefaultNameStem(spec.Kind))
                : spec.Name;

            PdfDictionary field = spec.Kind == FieldKind.RadioGroup
                ? BuildRadioGroup(doc, page, rects, spec, name)
                : BuildSingleWidgetField(doc, page, rects[0], spec, name);

            acroForm.Elements.GetArray("/Fields")!.Elements.Add(field.Reference!);
            return field;
        }

        // ── The field kinds ──────────────────────────────────────────────────────────────

        private static PdfDictionary BuildSingleWidgetField(
            PdfDocument doc, PdfPage page, PdfiumInterop.PdfRect rect, FieldSpec spec, string name)
        {
            double w = rect.Right - rect.Left, h = rect.Top - rect.Bottom;

            var field = new PdfDictionary(doc);
            doc.Internals.AddObject(field);

            field.Elements["/Type"]    = new PdfName("/Annot");
            field.Elements["/Subtype"] = new PdfName("/Widget");
            field.Elements["/Rect"]    = RectArray(doc, rect);
            field.Elements["/F"]       = new PdfInteger(AnnotPrint);
            field.Elements["/P"]       = page.Reference!;
            field.Elements["/T"]       = new PdfString(name);
            if (!string.IsNullOrEmpty(spec.Tooltip)) field.Elements["/TU"] = new PdfString(spec.Tooltip);

            int flags = (spec.ReadOnly ? FfReadOnly : 0) | (spec.Required ? FfRequired : 0);

            switch (spec.Kind)
            {
                case FieldKind.CheckBox:
                    field.Elements["/FT"] = new PdfName("/Btn");
                    field.Elements["/V"]  = new PdfName("/Off");
                    field.Elements["/AS"] = new PdfName("/Off");
                    // /MK /CA is the CAPTION: which ZapfDingbats glyph the viewer draws for the on
                    // state. "4" is the check mark, and it is what Acrobat writes by default.
                    field.Elements["/MK"] = MarkupDict(doc, caption: "4");
                    field.Elements["/AP"] = OnOffAppearance(doc, w, h, "/Yes", round: false);
                    break;

                case FieldKind.Dropdown:
                    field.Elements["/FT"] = new PdfName("/Ch");
                    field.Elements["/V"]  = new PdfString("");
                    field.Elements["/Opt"] = OptionArray(doc, spec.Options);
                    field.Elements["/DA"] = new PdfString(DaString(spec.FontSize));
                    field.Elements["/MK"] = MarkupDict(doc);
                    flags |= FfCombo | (spec.Editable ? FfEdit : 0);
                    AttachAppearance(doc, field, BoxAppearance(doc, w, h));
                    break;

                case FieldKind.Signature:
                    // No /V and no /DA: a signature field holds a signature dictionary, not text.
                    field.Elements["/FT"] = new PdfName("/Sig");
                    field.Elements["/MK"] = MarkupDict(doc);
                    AttachAppearance(doc, field, BoxAppearance(doc, w, h, dashed: true));
                    break;

                default:   // Text
                    field.Elements["/FT"] = new PdfName("/Tx");
                    field.Elements["/V"]  = new PdfString("");
                    field.Elements["/DA"] = new PdfString(DaString(spec.FontSize));
                    field.Elements["/MK"] = MarkupDict(doc);
                    if (spec.Multiline) flags |= FfMultiline;
                    // Comb is meaningless — and rendered wrong by most viewers — without the cell
                    // count, so the two travel together or not at all.
                    if (spec.MaxLength > 0)
                    {
                        field.Elements["/MaxLen"] = new PdfInteger(spec.MaxLength);
                        if (spec.Comb && !spec.Multiline) flags |= FfComb;
                    }
                    AttachAppearance(doc, field, BoxAppearance(doc, w, h));
                    break;
            }

            if (flags != 0) field.Elements["/Ff"] = new PdfInteger(flags);
            AppendToPage(page, field);
            return field;
        }

        /// <summary>
        /// A radio group: one parent field holding the shared value, one widget per button.
        /// </summary>
        /// <remarks>
        /// The only kind that cannot merge field and widget, because several widgets share one
        /// value. Each button's "on" state is named after its export value, so the parent's /V
        /// selects exactly one of them; every /AS starts at /Off so the group opens with nothing
        /// chosen, which is what a form asking a question should do.
        /// </remarks>
        private static PdfDictionary BuildRadioGroup(
            PdfDocument doc, PdfPage page, IReadOnlyList<PdfiumInterop.PdfRect> rects, FieldSpec spec, string name)
        {
            var parent = new PdfDictionary(doc);
            doc.Internals.AddObject(parent);
            parent.Elements["/FT"] = new PdfName("/Btn");
            parent.Elements["/T"]  = new PdfString(name);
            parent.Elements["/V"]  = new PdfName("/Off");
            if (!string.IsNullOrEmpty(spec.Tooltip)) parent.Elements["/TU"] = new PdfString(spec.Tooltip);

            int flags = FfRadio
                      | (spec.ReadOnly ? FfReadOnly : 0)
                      | (spec.Required ? FfRequired : 0);
            parent.Elements["/Ff"] = new PdfInteger(flags);

            var kids = new PdfArray(doc);
            for (int i = 0; i < rects.Count; i++)
            {
                double w = rects[i].Right - rects[i].Left, h = rects[i].Top - rects[i].Bottom;
                string export = ExportName(spec.Options, i);

                var kid = new PdfDictionary(doc);
                doc.Internals.AddObject(kid);
                kid.Elements["/Type"]    = new PdfName("/Annot");
                kid.Elements["/Subtype"] = new PdfName("/Widget");
                kid.Elements["/Rect"]    = RectArray(doc, rects[i]);
                kid.Elements["/F"]       = new PdfInteger(AnnotPrint);
                kid.Elements["/P"]       = page.Reference!;
                kid.Elements["/Parent"]  = parent.Reference!;
                kid.Elements["/AS"]      = new PdfName("/Off");
                // "l" is the ZapfDingbats filled circle — the radio dot, as Acrobat writes it.
                kid.Elements["/MK"]      = MarkupDict(doc, caption: "l");
                kid.Elements["/AP"]      = OnOffAppearance(doc, w, h, export, round: true);

                kids.Elements.Add(kid.Reference!);
                AppendToPage(page, kid);
            }
            parent.Elements["/Kids"] = kids;
            return parent;
        }

        /// <summary>A radio button's export value: its option text, made into a legal PDF name.</summary>
        private static string ExportName(IReadOnlyList<string> options, int index)
        {
            string raw = index < options.Count && !string.IsNullOrWhiteSpace(options[index])
                ? options[index]
                : $"Choice{index + 1}";

            var sb = new StringBuilder("/");
            foreach (char c in raw)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            string name = sb.ToString();
            // "/Off" is reserved for the unselected state, so an option literally called "Off"
            // would make the button impossible to deselect.
            return name is "/" or "/Off" ? $"/Choice{index + 1}" : name;
        }

        // ── The document-level AcroForm ──────────────────────────────────────────────────

        /// <summary>
        /// The document's /AcroForm, created — with the resources fields need — if absent.
        /// </summary>
        /// <remarks>
        /// /DR is not optional decoration. A field's /DA names a font ("/Helv 0 Tf 0 g") and that
        /// name is resolved against the AcroForm's /DR /Font; without the entry, a viewer
        /// regenerating the appearance has no font to draw with and the field comes out empty. Both
        /// base-14 fonts go in: /Helv for text and choices, /ZaDb for the check and radio glyphs.
        /// </remarks>
        internal static PdfDictionary EnsureAcroForm(PdfDocument doc)
        {
            var catalog = doc.Internals.Catalog;
            var acro = catalog.Elements.GetDictionary("/AcroForm");
            if (acro is null)
            {
                acro = new PdfDictionary(doc);
                doc.Internals.AddObject(acro);
                catalog.Elements["/AcroForm"] = acro.Reference!;
            }

            if (acro.Elements.GetArray("/Fields") is null)
                acro.Elements["/Fields"] = new PdfArray(doc);

            if (acro.Elements["/DA"] is null)
                acro.Elements["/DA"] = new PdfString(DaString(0));

            var dr = acro.Elements.GetDictionary("/DR");
            if (dr is null)
            {
                dr = new PdfDictionary(doc);
                acro.Elements["/DR"] = dr;
            }
            var fonts = dr.Elements.GetDictionary("/Font");
            if (fonts is null)
            {
                fonts = new PdfDictionary(doc);
                dr.Elements["/Font"] = fonts;
            }
            if (fonts.Elements[HelvName] is null) fonts.Elements[HelvName] = BaseFont(doc, "/Helvetica", winAnsi: true);
            if (fonts.Elements[ZaDbName] is null) fonts.Elements[ZaDbName] = BaseFont(doc, "/ZapfDingbats", winAnsi: false);

            // Ask viewers to build their own appearances as well. We still write /AP — see the
            // class remarks — but a viewer that regenerates will lay the text out better than a
            // stream generated before anyone typed anything.
            acro.Elements["/NeedAppearances"] = new PdfBoolean(true);
            return acro;
        }

        private static PdfDictionary BaseFont(PdfDocument doc, string baseFont, bool winAnsi)
        {
            var f = new PdfDictionary(doc);
            f.Elements["/Type"]     = new PdfName("/Font");
            f.Elements["/Subtype"]  = new PdfName("/Type1");
            f.Elements["/BaseFont"] = new PdfName(baseFont);
            // ZapfDingbats has its own built-in encoding; forcing WinAnsi on it would remap the
            // very glyphs the check and radio captions rely on.
            if (winAnsi) f.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
            return f;
        }

        /// <summary>
        /// A field name not already used in this document.
        /// </summary>
        /// <remarks>
        /// Duplicate names are legal and mean something specific — two widgets sharing one value —
        /// so generating one by accident would silently tie two unrelated boxes together.
        /// </remarks>
        internal static string UniqueName(PdfDocument doc, string stem)
        {
            var taken = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var obj in doc.Internals.GetAllObjects())
                    if (obj is PdfDictionary d && d.Elements["/T"] is PdfString t)
                        taken.Add(t.Value);
            }
            catch { /* a malformed document must not stop the user placing a field */ }

            for (int i = 1; i < 100_000; i++)
            {
                string candidate = stem + i.ToString(CultureInfo.InvariantCulture);
                if (taken.Add(candidate)) return candidate;
            }
            return stem + Guid.NewGuid().ToString("N")[..8];
        }

        private static string DefaultNameStem(FieldKind kind) => kind switch
        {
            FieldKind.CheckBox   => "Check",
            FieldKind.RadioGroup => "Group",
            FieldKind.Dropdown   => "Dropdown",
            FieldKind.Signature  => "Signature",
            _                    => "Text",
        };

        // ── Appearance streams ───────────────────────────────────────────────────────────

        /// <summary>The field's box: white fill, thin dark border. Matches /MK below.</summary>
        private static PdfDictionary BoxAppearance(PdfDocument doc, double w, double h, bool dashed = false)
        {
            string ops = Ops(
                "q",
                "1 1 1 rg",
                $"0 0 {N(w)} {N(h)} re f",
                "0.35 0.35 0.35 RG",
                "1 w",
                dashed ? "[3 2] 0 d" : "[] 0 d",
                $"0.5 0.5 {N(w - 1)} {N(h - 1)} re S",
                "Q");
            return BuildFormXObject(doc, HelvName, w, h, ops, zapf: false);
        }

        /// <summary>
        /// The /AP for a check box or radio button: an /N dictionary with an /Off state and an on
        /// state named after the export value.
        /// </summary>
        /// <remarks>
        /// Both states must exist. A widget with only an on state renders as nothing when cleared
        /// in some viewers and as the on state in others, which is how a form comes back with every
        /// box apparently ticked.
        /// </remarks>
        private static PdfDictionary OnOffAppearance(
            PdfDocument doc, double w, double h, string onName, bool round)
        {
            double s = Math.Min(w, h);

            string frame = round
                ? Ops("q", "1 1 1 rg", Circle(w / 2, h / 2, s / 2 - 0.5, fill: true),
                      "0.35 0.35 0.35 RG", "1 w", Circle(w / 2, h / 2, s / 2 - 0.5, fill: false), "Q")
                : Ops("q", "1 1 1 rg", $"0 0 {N(w)} {N(h)} re f",
                      "0.35 0.35 0.35 RG", "1 w", $"0.5 0.5 {N(w - 1)} {N(h - 1)} re S", "Q");

            // The mark itself, in ZapfDingbats: "4" is the check, "l" the filled circle. Sized to
            // the box and centred by its own metrics rather than guessed at.
            double mark = Math.Max(4, s * (round ? 0.5 : 0.72));
            string glyph = round ? "l" : "4";
            double tx = (w - mark * 0.78) / 2;
            double ty = (h - mark * 0.72) / 2;
            string on = frame + Ops(
                "q", "0 0 0 rg", "BT", $"{ZaDbName} {N(mark)} Tf", $"{N(tx)} {N(ty)} Td",
                $"({glyph}) Tj", "ET", "Q");

            var offX = BuildFormXObject(doc, ZaDbName, w, h, frame, zapf: true);
            var onX  = BuildFormXObject(doc, ZaDbName, w, h, on,    zapf: true);

            var n = new PdfDictionary(doc);
            n.Elements["/Off"] = offX.Reference!;
            n.Elements[onName] = onX.Reference!;

            var ap = new PdfDictionary(doc);
            ap.Elements["/N"] = n;
            return ap;
        }

        /// <summary>A circle as four Bézier arcs — PDF has no circle operator.</summary>
        private static string Circle(double cx, double cy, double r, bool fill)
        {
            const double k = 0.5523;   // circular-arc control-point ratio
            double o = r * k;
            return Ops(
                $"{N(cx - r)} {N(cy)} m",
                $"{N(cx - r)} {N(cy + o)} {N(cx - o)} {N(cy + r)} {N(cx)} {N(cy + r)} c",
                $"{N(cx + o)} {N(cy + r)} {N(cx + r)} {N(cy + o)} {N(cx + r)} {N(cy)} c",
                $"{N(cx + r)} {N(cy - o)} {N(cx + o)} {N(cy - r)} {N(cx)} {N(cy - r)} c",
                $"{N(cx - o)} {N(cy - r)} {N(cx - r)} {N(cy - o)} {N(cx - r)} {N(cy)} c",
                fill ? "f" : "S").TrimEnd('\n');
        }

        /// <summary>
        /// Mirrors MainWindow.BuildFormXObject, which is the shape every appearance TDPdf writes
        /// already has: an inline font resource, and CreateStream so /Length is written.
        /// </summary>
        /// <remarks>
        /// CreateStream matters more than it looks. Attaching a PdfStream by hand skips /Length,
        /// which every PDF stream is required to carry; PdfSharpCore's own parser then refuses to
        /// reopen the file. That was a real defect in the fill path (#180) and there is no reason
        /// to reintroduce it in the authoring path.
        /// </remarks>
        private static PdfDictionary BuildFormXObject(
            PdfDocument doc, string fontName, double w, double h, string content, bool zapf)
        {
            var xobj = new PdfDictionary(doc);
            xobj.Elements["/Type"]     = new PdfName("/XObject");
            xobj.Elements["/Subtype"]  = new PdfName("/Form");
            xobj.Elements["/FormType"] = new PdfInteger(1);

            var bbox = new PdfArray(doc);
            bbox.Elements.Add(new PdfReal(0));
            bbox.Elements.Add(new PdfReal(0));
            bbox.Elements.Add(new PdfReal(w));
            bbox.Elements.Add(new PdfReal(h));
            xobj.Elements["/BBox"] = bbox;

            var fontDict = new PdfDictionary(doc);
            fontDict.Elements[fontName] = BaseFont(doc, zapf ? "/ZapfDingbats" : "/Helvetica", winAnsi: !zapf);
            var res = new PdfDictionary(doc);
            res.Elements["/Font"] = fontDict;
            xobj.Elements["/Resources"] = res;

            xobj.CreateStream(Encoding.GetEncoding("iso-8859-1").GetBytes(content));
            doc.Internals.AddObject(xobj);
            return xobj;
        }

        private static void AttachAppearance(PdfDocument doc, PdfDictionary widget, PdfDictionary xobj)
        {
            var ap = new PdfDictionary(doc);
            ap.Elements["/N"] = xobj.Reference!;
            widget.Elements["/AP"] = ap;
        }

        // ── Small pieces ─────────────────────────────────────────────────────────────────

        /// <summary>/MK: the border and background colours a viewer uses when it regenerates.</summary>
        private static PdfDictionary MarkupDict(PdfDocument doc, string? caption = null)
        {
            var mk = new PdfDictionary(doc);
            var bc = new PdfArray(doc);
            foreach (double v in new[] { 0.35, 0.35, 0.35 }) bc.Elements.Add(new PdfReal(v));
            var bg = new PdfArray(doc);
            foreach (double v in new[] { 1.0, 1.0, 1.0 }) bg.Elements.Add(new PdfReal(v));
            mk.Elements["/BC"] = bc;
            mk.Elements["/BG"] = bg;
            if (caption is not null) mk.Elements["/CA"] = new PdfString(caption);
            return mk;
        }

        private static PdfArray RectArray(PdfDocument doc, PdfiumInterop.PdfRect r)
        {
            var a = new PdfArray(doc);
            a.Elements.Add(new PdfReal(r.Left));
            a.Elements.Add(new PdfReal(r.Bottom));
            a.Elements.Add(new PdfReal(r.Right));
            a.Elements.Add(new PdfReal(r.Top));
            return a;
        }

        private static PdfArray OptionArray(PdfDocument doc, IReadOnlyList<string> options)
        {
            var a = new PdfArray(doc);
            foreach (string o in options) a.Elements.Add(new PdfString(o ?? ""));
            return a;
        }

        /// <summary>A /DA string. Size 0 means "auto", which viewers read as fit-to-box.</summary>
        private static string DaString(double fontSize) =>
            $"{HelvName} {N(fontSize)} Tf 0 g";

        private static void AppendToPage(PdfPage page, PdfDictionary widget)
        {
            var annots = page.Elements.GetArray("/Annots");
            if (annots is null)
            {
                annots = new PdfArray(page.Owner);
                page.Elements["/Annots"] = annots;
            }
            annots.Elements.Add(widget.Reference!);
        }

        private static string Ops(params string[] lines) => string.Join("\n", lines) + "\n";

        /// <summary>Invariant, fixed-point number formatting. A comma decimal separator would
        /// silently corrupt every content stream this writes.</summary>
        private static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
