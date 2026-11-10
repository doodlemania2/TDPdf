using System;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace TDPdf.Services
{
    /// <summary>
    /// Pre-save document normalization: the structural scrubs every TDPdf save applies to the
    /// PdfSharpCore document immediately before writing it. Moved out of MainWindow unchanged so
    /// tests/PdfCore can exercise it (see PdfSaveNormalize.cs).
    /// </summary>
    internal static class PdfSaveNormalizer
    {
        private static PdfItem Deref(PdfItem item)
        {
            var valueProp = item.GetType().GetProperty("Value",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (valueProp?.GetValue(item) is PdfObject resolved) return resolved;
            return item;
        }

        // Pre-save document normalization (ports upstream KillerPDF v1.6.3/v1.6.4 conformance
        // fixes). Every TDPdf save fully rewrites the file through PdfSharpCore, so we scrub three
        // classes of structural corruption immediately before writing. All three are semantic
        // no-ops on healthy documents and also HEAL files damaged by other tools/older builds when
        // re-saved. Called on the UI thread before dispatching any doc.Save(...).
        internal static void Normalize(PdfDocument doc)
        {
            ScrubEmptyOutlines(doc);         // #103: never write a dangling /Outlines reference
            ScrubDegeneratePageBoxes(doc);   // never write a zero-size /CropBox or /MediaBox (Adobe out-of-range)
            ScrubDeadSignatures(doc);        // a rewrite voids signatures; never ship a dead one (PDF/A 6.4.3)
        }

        private static double RectNum(PdfItem item) =>
            item is PdfReal r ? r.Value : item is PdfInteger n ? n.Value : 0;

        // #103 (upstream v1.6.3): PdfSharpCore's writer can emit the catalog's /Outlines reference
        // without ever writing the (empty, lazily created) outlines object itself - a dangling xref
        // entry that strict parsers, including PdfSharpCore on reopen, refuse. An outlines dictionary
        // with no /First contains no bookmarks, so dropping the entry is a semantic no-op that keeps
        // the file consistent. Real bookmark trees (/First present) are left untouched.
        internal static void ScrubEmptyOutlines(PdfDocument doc)
        {
            try
            {
                var cat = doc.Internals.Catalog;
                var item = cat.Elements["/Outlines"];
                if (item is null) return;
                if (Deref(item) is not PdfDictionary o || o.Elements["/First"] is null)
                    cat.Elements.Remove("/Outlines");
            }
            catch { /* malformed catalog - leave the save as-is */ }
        }

        // Upstream v1.6.3 (/CropBox), extended here to /MediaBox: PdfSharpCore's PdfPage.MediaBox and
        // .CropBox property GETTERS have create-on-read semantics, so touching page.CropBox - or
        // page.Width/page.Height, which read MediaBox - on a page that carries no such entry plants an
        // empty [0 0 0 0] box into the page dictionary. A zero-size page box saves to disk and Adobe
        // then rejects the page as "dimensions out-of-range" (Chrome falls back to another box, which is
        // why such files still open there). Both boxes are INHERITABLE page attributes, so the pages
        // that get one planted are exactly the ones whose real box lives on an ancestor /Pages node.
        //
        // /CropBox: dropping a degenerate one is a semantic no-op - the page falls back to its MediaBox.
        // /MediaBox: every page needs one, so drop the degenerate entry and re-plant the box the page
        // tree really specifies; only when nothing usable is inheritable do we leave it absent, which at
        // least renders (viewers substitute a default page size) where a zero-size box does not.
        // Both HEAL files damaged by other tools or older builds when re-saved; real boxes are untouched.
        internal static void ScrubDegeneratePageBoxes(PdfDocument doc)
        {
            try
            {
                for (int i = 0; i < doc.PageCount; i++)
                {
                    var page = doc.Pages[i];
                    var elements = page.Elements;

                    if (IsDegenerateBox(elements["/CropBox"]))
                        elements.Remove("/CropBox");

                    if (IsDegenerateBox(elements["/MediaBox"]))
                    {
                        elements.Remove("/MediaBox");
                        // With the bad entry gone, ask the page tree what this page's box actually is.
                        // Re-planting it explicitly keeps the page valid no matter how the writer treats
                        // the inherited attribute, and is identical in meaning to inheriting it.
                        if (PdfPageGeometry.ReadInheritedPageBox(page, "/MediaBox") is { Width: > 1, Height: > 1 } box)
                            elements.SetRectangle("/MediaBox",
                                new PdfRectangle(new XPoint(box.X, box.Y), new XPoint(box.Right, box.Top)));
                    }

                    // Upstream v1.7.1 (#169): PDF 32000-1 14.11.2 requires /CropBox to lie INSIDE
                    // /MediaBox. A rotated page could be written with a portrait media box and a
                    // landscape crop box, a malformed combination that strict validators reject and
                    // that leaves renderers disagreeing about the page size. Removing the invalid crop
                    // is lossless: the page falls back to its complete media box rather than clipping
                    // content away. Done AFTER the media-box healing above so the comparison is against
                    // a box that is actually usable, and skipped entirely when it is not — a bad media
                    // box must never be a reason to delete a good crop box.
                    const double outsideTol = 0.01;
                    if (ReadOwnPageBox(elements["/CropBox"]) is { } crop &&
                        PdfPageGeometry.ReadInheritedPageBox(page, "/MediaBox") is { Width: > 1, Height: > 1 } media &&
                        (crop.X     < media.X     - outsideTol || crop.Y   < media.Y   - outsideTol ||
                         crop.Right > media.Right + outsideTol || crop.Top > media.Top + outsideTol))
                        elements.Remove("/CropBox");
                }
            }
            catch { /* malformed page tree - leave the save as-is */ }
        }

        // Reads a page's OWN /MediaBox or /CropBox entry — no /Parent walk — as a normalized PageBox,
        // or null when the entry is absent or cannot be interpreted with certainty. Deliberately
        // stricter than ReadInheritedPageBox, which reads for geometry: this one feeds the DESTRUCTIVE
        // scrub decisions above, so anything ambiguous reads as null and is then left alone rather than
        // deleted. The box can be a parsed PdfArray (loaded from disk), a PdfRectangle (planted in
        // memory by the lazy getter or by GetRectangle writing its conversion back), or an indirect
        // reference to either.
        private static PdfPageGeometry.PageBox? ReadOwnPageBox(PdfItem? item)
        {
            if (item is null) return null;
            if (item is not PdfArray and not PdfRectangle) item = Deref(item);

            if (item is PdfRectangle rect)
                return Normalize(rect.X1, rect.Y1, rect.X2, rect.Y2);
            if (item is PdfArray arr && arr.Elements.Count == 4 &&
                arr.Elements[0] is PdfReal or PdfInteger && arr.Elements[1] is PdfReal or PdfInteger &&
                arr.Elements[2] is PdfReal or PdfInteger && arr.Elements[3] is PdfReal or PdfInteger)
                return Normalize(RectNum(arr.Elements[0]), RectNum(arr.Elements[1]),
                                 RectNum(arr.Elements[2]), RectNum(arr.Elements[3]));
            return null;

            static PdfPageGeometry.PageBox Normalize(double x1, double y1, double x2, double y2) =>
                new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        }

        // True when the entry is present, readable as a rectangle, and zero/sub-point sized. Anything we
        // cannot interpret returns false so it is left alone rather than destroyed.
        private static bool IsDegenerateBox(PdfItem? item) =>
            ReadOwnPageBox(item) is { } box && (box.Width < 1 || box.Height < 1);

        // Upstream v1.6.4: a TDPdf save fully REWRITES the file, which mathematically invalidates any
        // existing digital signature: its /ByteRange and digest describe the old bytes (ISO 19005-2,
        // 6.4.3 requires the digest to cover the entire file). Carrying the dead signature forward
        // misleads viewers and fails PDF/A validation, so strip signature VALUES (/V) from signature
        // fields and the catalog's /Perms certification (DocMDP / usage rights) that references them.
        // The empty fields stay and can be re-signed.
        internal static void ScrubDeadSignatures(PdfDocument doc)
        {
            try
            {
                doc.Internals.Catalog.Elements.Remove("/Perms");
                // Same walk the warning counts with (PdfSignatureScan). Sharing it is the point:
                // when these were two walks, the warning could go stale the first time one learned
                // about a field shape the other did not — and the failure mode is staying silent
                // while still removing the signature.
                foreach (var field in PdfSignatureScan.SignedFields(doc))
                    field.Elements.Remove("/V");
            }
            catch { /* malformed catalog - leave the save as-is */ }
        }
    }
}
