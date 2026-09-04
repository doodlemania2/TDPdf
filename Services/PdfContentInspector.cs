using System;
using System.Collections.Generic;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Content;
using PdfSharpCore.Pdf.Content.Objects;

namespace TDPdf.Services
{
    /// <summary>
    /// Content a page carries that PDFium's content generator would silently destroy.
    /// </summary>
    [Flags]
    internal enum ContentHazard
    {
        None = 0,
        /// <summary>Any colour that is not DeviceRGB or DeviceGray: DeviceCMYK, ICCBased,
        /// Separation, Indexed, Lab, or a pattern fill.</summary>
        NonDeviceColor = 1 << 0,
        /// <summary>A shading (gradient) painted with <c>sh</c>.</summary>
        Shading = 1 << 1,
        /// <summary>An inline image (<c>BI … ID … EI</c>).</summary>
        InlineImage = 1 << 2,
        /// <summary>An ExtGState carrying a soft mask.</summary>
        SoftMask = 1 << 3,
        /// <summary>The content stream could not be parsed at all. Treated as hazardous, because
        /// "we could not read it" is never a reason to rewrite it.</summary>
        Unparseable = 1 << 4,
    }

    /// <summary>
    /// The pre-flight gate for surgical content-stream edits (redaction, real text editing).
    /// </summary>
    /// <remarks>
    /// PDFium's <c>FPDFPage_GenerateContent</c> re-serialises every DIRTY content stream and
    /// byte-preserves the rest — but inside that radius it drops things without reporting anything,
    /// and the losses are specific and known:
    ///
    ///   * <b>All non-device colour.</b> <c>CPDF_PageContentGenerator::WriteColorToStream</c>
    ///     returns early unless the colour space is DeviceRGB or DeviceGray, so DeviceCMYK,
    ///     ICCBased, Separation, Indexed, Lab and pattern fills emit NOTHING and the object
    ///     inherits default black. (DeviceGray support itself only landed in mid-2024.)
    ///   * <b>Shadings entirely.</b> <c>ProcessPageObject</c> dispatches only to Image/Form/Path/
    ///     Text; <c>FPDF_PAGEOBJ_SHADING</c> has no handler at all.
    ///   * <b>Inline images</b> — <c>ProcessImage</c> returns immediately for them.
    ///   * <b>ExtGState beyond ca/CA/BM</b>, which includes soft masks.
    ///
    /// On a scanned page — one image and one text run — none of that is present and the risk is
    /// nil. On a CMYK-printed form, a chart with a gradient, or an invoice with spot colour,
    /// removing one text object can recolour or delete unrelated content in the same stream.
    ///
    /// So: callers ask this first, and fall back to rasterising the page when the answer is
    /// anything but <see cref="ContentHazard.None"/>. That fallback is not a nicety — it is the
    /// difference between a redaction feature and a data-integrity bug.
    ///
    /// Note the deliberate conservatism on colour. An ICCBased/3 space is sRGB in practice and
    /// looks safe, but PDFium classifies it as PDFCS_ICCBASED rather than PDFCS_DEVICERGB, so it is
    /// dropped exactly like CMYK. Flagging it is faithful to what the generator actually does, not
    /// an over-reaction.
    /// </remarks>
    internal static class PdfContentInspector
    {
        /// <summary>The only two colour space names PDFium's generator round-trips.</summary>
        private static readonly HashSet<string> SafeColorSpaces =
            new(StringComparer.Ordinal) { "/DeviceRGB", "/DeviceGray" };

        /// <summary>
        /// True when <paramref name="page"/> can be handed to PDFium's content generator without
        /// losing anything. Fail-closed: an unreadable page reports unsafe.
        /// </summary>
        public static bool IsRegenerationSafe(PdfPage page) => Inspect(page) == ContentHazard.None;

        /// <summary>
        /// Everything on <paramref name="page"/> that content regeneration would destroy.
        /// Never throws — a page it cannot parse comes back as <see cref="ContentHazard.Unparseable"/>.
        /// </summary>
        public static ContentHazard Inspect(PdfPage page)
        {
            if (page is null) return ContentHazard.Unparseable;

            CSequence content;
            try
            {
                content = new CParser(page).ReadContent();
            }
            catch
            {
                return ContentHazard.Unparseable;
            }

            var found = ContentHazard.None;
            try
            {
                Walk(content, page, ref found);
            }
            catch
            {
                // A malformed operand mid-walk invalidates the verdict, not just the operator:
                // whatever was not reached might have been the hazard that mattered.
                return found | ContentHazard.Unparseable;
            }
            return found;
        }

        private static void Walk(CSequence seq, PdfPage page, ref ContentHazard found)
        {
            foreach (var obj in seq)
            {
                if (obj is CSequence nested) { Walk(nested, page, ref found); continue; }
                if (obj is not COperator op) continue;

                switch (op.Name)
                {
                    // Set colour space, stroking and non-stroking.
                    case "cs":
                    case "CS":
                        if (FirstName(op) is string csName && !SafeColorSpaces.Contains(csName))
                            found |= ContentHazard.NonDeviceColor;
                        break;

                    // Set CMYK colour directly — no colour space lookup needed to know this is lost.
                    case "k":
                    case "K":
                        found |= ContentHazard.NonDeviceColor;
                        break;

                    // scn/SCN with a NAME operand is a pattern fill; with numbers it is a colour in
                    // the space already selected by cs/CS, which the cs/CS arm above has judged.
                    case "scn":
                    case "SCN":
                        if (FirstName(op) is not null)
                            found |= ContentHazard.NonDeviceColor;
                        break;

                    case "sh":
                        found |= ContentHazard.Shading;
                        break;

                    case "BI":
                    case "ID":
                    case "EI":
                        found |= ContentHazard.InlineImage;
                        break;

                    case "gs":
                        if (FirstName(op) is string gsName && ExtGStateHasSoftMask(page, gsName))
                            found |= ContentHazard.SoftMask;
                        break;
                }
            }
        }

        /// <summary>The operator's first operand if it is a name (<c>/Foo</c>), else null.</summary>
        private static string? FirstName(COperator op)
        {
            var operands = op.Operands;
            for (int i = 0; i < operands.Count; i++)
                if (operands[i] is CName n) return n.Name;
            return null;
        }

        /// <summary>
        /// True when the named ExtGState carries a soft mask. <c>/SMask /None</c> is the explicit
        /// "no mask" form and is not a hazard.
        /// </summary>
        private static bool ExtGStateHasSoftMask(PdfPage page, string name)
        {
            try
            {
                var extGStates = InheritedResourceDict(page, "/ExtGState");
                if (extGStates is null) return false;

                var gs = extGStates.Elements.GetDictionary(name);
                if (gs is null) return false;
                if (!gs.Elements.ContainsKey("/SMask")) return false;

                var smask = gs.Elements["/SMask"];
                // A soft mask is a dictionary; the literal name /None disables masking.
                return smask is not PdfName pn || !string.Equals(pn.Value, "/None", StringComparison.Ordinal);
            }
            catch
            {
                // Unresolvable resources mean we cannot prove the page is safe, so say it is not.
                return true;
            }
        }

        /// <summary>
        /// Looks up a sub-dictionary of <c>/Resources</c>, walking <c>/Parent</c> because
        /// <c>/Resources</c> is an inheritable page attribute — the same reason
        /// <c>ReadInheritedPageBox</c> exists for the page boxes.
        /// </summary>
        private static PdfDictionary? InheritedResourceDict(PdfPage page, string key)
        {
            PdfDictionary? node = page;
            // Bounded rather than while(true): a corrupt file can contain a /Parent cycle, and this
            // runs on the save path where a hang is indistinguishable from a crash.
            for (int depth = 0; node is not null && depth < 32; depth++)
            {
                var res = node.Elements.GetDictionary("/Resources");
                var sub = res?.Elements.GetDictionary(key);
                if (sub is not null) return sub;
                node = node.Elements.GetDictionary("/Parent");
            }
            return null;
        }

        /// <summary>
        /// A short, user-facing reason a page had to be rasterised instead of edited surgically.
        /// Written to be shown in a dialog, not logged — see the redaction fallback notice.
        /// </summary>
        public static string Describe(ContentHazard hazard)
        {
            if (hazard == ContentHazard.None) return "no hazards";

            var parts = new List<string>(5);
            if (hazard.HasFlag(ContentHazard.NonDeviceColor)) parts.Add("non-RGB colour (CMYK, spot or ICC)");
            if (hazard.HasFlag(ContentHazard.Shading)) parts.Add("a gradient");
            if (hazard.HasFlag(ContentHazard.InlineImage)) parts.Add("an inline image");
            if (hazard.HasFlag(ContentHazard.SoftMask)) parts.Add("a soft mask");
            if (hazard.HasFlag(ContentHazard.Unparseable)) parts.Add("content this build cannot read");

            return parts.Count == 1
                ? parts[0]
                : string.Join(", ", parts.GetRange(0, parts.Count - 1)) + " and " + parts[^1];
        }
    }
}
