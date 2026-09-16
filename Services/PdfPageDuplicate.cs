using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace TDPdf.Services
{
    // ============================================================
    // Duplicating pages inside the open document (#135 item 5).
    //
    // WHY THIS IS NOT A ONE-LINER
    // The obvious spelling is
    //
    //     doc.Pages.Insert(i, doc.Pages[j]);
    //
    // and it cannot work, because there is no copy in it. PdfSharpCore's PdfPages.Insert takes the
    // "already owned by this document" branch for a page whose Owner is this document, and that
    // branch inserts page.Reference — the SAME indirect reference — a second time into the page
    // array, which is one page appearing twice rather than two pages. That branch exists
    // for "removed, then put back somewhere else", which is what the reorder path does, and it is
    // not a duplication mechanism at all: there is no branch in it that copies anything.
    //
    // What stops the naive call is one line at the top of that branch:
    //
    //     if (ReferenceEquals(this[idx], page)) throw new InvalidOperationException(...)
    //
    // so `doc.Pages.Insert(i, doc.Pages[j])` throws "the document already owned this page" rather
    // than corrupting anything — verified, and pinned by tests/PdfCore so that an upstream change
    // of heart cannot turn it back into a silent success. But note WHAT that guard tests: object
    // identity of the wrapper, not the object id of the page. It holds only because the indexer
    // substitutes its wrapper back into the indirect reference (PdfObject's copy constructor sets
    // obj._iref.Value = this), so the wrapper it hands out really is the one in the page tree. Any
    // route that presents a second PdfPage wrapper around the same dictionary walks straight past
    // it.
    //
    // What ships then is one page object appearing twice. The page count goes up, the thumbnails
    // are right, the file opens in every reader — and /Rotate, a crop, an inline text edit or a
    // deletion applied to "one" of them silently lands on both, because there is only one of them.
    // That is a data-loss bug the user cannot see until the damage is already saved, which is why
    // the tests for this file mutate one page of each pair and assert on the other instead of
    // counting pages.
    //
    // THE FIX
    // Insert takes a completely different branch for a page from a FOREIGN document: it calls
    // ImportExternalPage, which builds `new PdfPage(_document)` and clones /Resources, /Contents,
    // /MediaBox, /CropBox, /Rotate, /BleedBox, /TrimBox, /ArtBox and /Annots into it — the referenced
    // objects going through ImportClosure, which registers fresh copies in the target document's
    // object table. The result is an independent page dictionary with its own object id and its own
    // content stream, which is exactly what a duplicate has to be.
    //
    // So the document is made foreign to itself: serialize it and reopen the bytes in Import mode.
    // ImportExternalPage refuses anything else outright ("A PDF document must be opened with
    // PdfDocumentOpenMode.Import to import pages from it"), so there is no shortcut where the live
    // Modify-mode document imports from itself.
    // ============================================================
    internal static class PdfPageDuplicate
    {
        /// <summary>
        /// The pages a selection actually duplicates: ascending, distinct, in range. A selection
        /// arrives as whatever the sidebar's Extended-mode ListBox handed back, so it may be in
        /// click order, may repeat, and may name a page that no longer exists.
        /// </summary>
        public static int[] Normalize(IEnumerable<int>? selection, int pageCount)
            => (selection ?? Array.Empty<int>())
                .Where(i => i >= 0 && i < pageCount)
                .Distinct()
                .OrderBy(i => i)
                .ToArray();

        /// <summary>
        /// The index the first copy lands at: immediately after the LAST selected page.
        /// </summary>
        /// <remarks>
        /// One block after the last original, rather than each copy tucked in behind its own
        /// original. Both are defensible and for a contiguous run they agree; they differ only for a
        /// non-contiguous selection, and there the per-original variant interleaves copies through
        /// the document at positions the user has to simulate in their head to predict — select
        /// pages 1, 4 and 9 of a 20-page document and the result is a page order nobody can check at
        /// a glance. One contiguous block after the last original is the version a reader can
        /// reason about, it matches where <see cref="PageBlockMove"/> already gathers a scattered
        /// selection, and it leaves the copies adjacent and selected so a follow-up drag moves them
        /// somewhere else in one gesture.
        /// </remarks>
        public static int InsertIndex(int[] pages) => pages.Length == 0 ? -1 : pages[^1] + 1;

        /// <summary>
        /// Inserts an independent copy of each page in <paramref name="selection"/> into
        /// <paramref name="doc"/>, as one block immediately after the last selected page and in the
        /// originals' relative order.
        /// </summary>
        /// <returns>
        /// The index of the first copy, or -1 when nothing was duplicated (empty or wholly
        /// out-of-range selection). The copies occupy that index and the <c>selection.Length - 1</c>
        /// indices after it.
        /// </returns>
        /// <remarks>
        /// The scratch import document is the whole document round-tripped through memory, which is
        /// the price of PdfSharpCore's "import only from a foreign Import-mode document" rule. It is
        /// paid once per invocation of a user-initiated command, and it is worth preferring over
        /// importing from the on-disk working file: this copy is derived from the PdfDocument being
        /// mutated, so it cannot be a stale or diverged version of it.
        ///
        /// Each PdfPage carries its own /Rotate, and ImportExternalPage clones that key onto the new
        /// page, so a rotated original yields an equally rotated copy with no extra bookkeeping —
        /// and, because the two dictionaries are now separate objects, rotating one afterwards
        /// leaves the other alone. Both halves of that are pinned by tests/PdfCore.
        /// </remarks>
        public static int Duplicate(PdfDocument doc, IEnumerable<int>? selection)
        {
            if (doc is null) throw new ArgumentNullException(nameof(doc));

            int[] pages = Normalize(selection, doc.PageCount);
            if (pages.Length == 0) return -1;
            int insertAt = InsertIndex(pages);

            using var scratch = new MemoryStream();
            // Save(stream, closeStream: false) rewinds the stream itself when it keeps it open.
            doc.Save(scratch, false);
            using var import = PdfReader.Open(scratch, PdfDocumentOpenMode.Import);

            // Ascending source order, inserted at consecutive indices, so the copies keep the
            // originals' relative order. Every insert happens at or after insertAt, which is past
            // every source index, so the source pages never shift under the loop — but the pages are
            // read from `import` anyway, a document nothing here mutates.
            for (int k = 0; k < pages.Length; k++)
                doc.Pages.Insert(insertAt + k, import.Pages[pages[k]]);

            return insertAt;
        }
    }
}
