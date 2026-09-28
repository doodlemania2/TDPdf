using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using TDPdf.Services;

/// <summary>
/// Pins page duplication: <see cref="PdfPageDuplicate"/>.
/// </summary>
/// <remarks>
/// The one thing worth testing here is INDEPENDENCE, and it is the one thing an eyeball cannot
/// check. PdfSharpCore will happily insert a second reference to a page that is already in the
/// document (see the header comment on <see cref="PdfPageDuplicate"/>): the page count goes up, the
/// thumbnails look right, the file opens in every reader — and then rotating or editing "one" of
/// the two pages changes both, because there is only one page. A test that asserts
/// <c>PageCount == n + 1</c> passes on that broken document, so it is worse than no test at all.
///
/// So every case below MUTATES one of the pair and asserts the other did not move, and it does so
/// across a save/reopen round trip. In-memory object identity is not enough on its own: two
/// distinct PdfPage wrappers can still be writing into one underlying dictionary, and the file on
/// disk is what the user keeps. The round trip is also what the app really does — every structural
/// edit goes out through SaveTempAndReload — so the document these assertions run against is the
/// document TDPdf would have produced.
/// </remarks>
internal static class Duplicate
{
    /// <summary>A document with one word of distinct text per page, and per-page /Rotate values.</summary>
    private static string Fixture(string tmp, string name, string[] words, int[] rotations)
    {
        string path = Path.Combine(tmp, name);
        var doc = new PdfDocument();
        for (int i = 0; i < words.Length; i++)
        {
            var page = doc.AddPage();
            using (var gfx = XGraphics.FromPdfPage(page))
                gfx.DrawString(words[i], new XFont("Helvetica", 24), XBrushes.Black, 60, 120);
            page.Rotate = rotations[i];
        }
        doc.Save(path);
        return path;
    }

    /// <summary>The page order as extracted text, e.g. "AAA|BBB|AAA" — one entry per page.</summary>
    private static string TextOrder(string path)
    {
        using var d = UglyToad.PdfPig.PdfDocument.Open(path);
        return string.Join("|", d.GetPages().Select(p => p.Text.Trim()));
    }

    private static string RotateOrder(string path)
    {
        using var d = PdfReader.Open(path, PdfDocumentOpenMode.ReadOnly);
        return string.Join(",", Enumerable.Range(0, d.PageCount).Select(i => d.Pages[i].Rotate));
    }

    /// <summary>
    /// Runs a duplication against <paramref name="src"/> and saves the result, exactly the way the
    /// app does it: mutate the live Modify-mode document, then write it out and reopen.
    /// </summary>
    private static (string path, int insertAt) Run(string tmp, string src, string outName, int[] selection)
    {
        string outPath = Path.Combine(tmp, outName);
        using (var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify))
        {
            int insertAt = PdfPageDuplicate.Duplicate(doc, selection);
            doc.Save(outPath);
            return (outPath, insertAt);
        }
    }

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("Page duplication (#135 item 5)");

        // ── Where the copies land ──────────────────────────────────────────────────────────
        // Pure arithmetic, no document needed: the block goes after the LAST selected page.
        Check("one page: the copy lands directly after it",
              PdfPageDuplicate.InsertIndex([2]) == 3, $"{PdfPageDuplicate.InsertIndex([2])}");
        Check("a non-contiguous selection lands as ONE block after the last original",
              PdfPageDuplicate.InsertIndex([0, 2]) == 3, $"{PdfPageDuplicate.InsertIndex([0, 2])}");
        Check("nothing selected duplicates nothing", PdfPageDuplicate.InsertIndex([]) == -1, "");
        Check("a selection is sorted, deduplicated and clipped to the document",
              string.Join(",", PdfPageDuplicate.Normalize([3, 1, 1, -4, 99], 4)) == "1,3",
              string.Join(",", PdfPageDuplicate.Normalize([3, 1, 1, -4, 99], 4)));

        // ── A single page ──────────────────────────────────────────────────────────────────
        // Page 1 (0-based) starts rotated 90, and its neighbours are rotated differently, so a copy
        // that quietly picked up an inherited or default /Rotate would show up here.
        string src = Fixture(tmp, "dup-src.pdf", ["AAA", "BBB", "CCC"], [0, 90, 270]);
        Check("fixture reads back as expected", TextOrder(src) == "AAA|BBB|CCC", TextOrder(src));

        var (one, insertAt) = Run(tmp, src, "dup-one.pdf", [1]);
        Check("the copy is inserted right after its original", insertAt == 2, $"{insertAt}");
        Check("the document gained exactly one page and the copy is in the right place",
              TextOrder(one) == "AAA|BBB|BBB|CCC", TextOrder(one));
        Check("the copy keeps its own rotation, and its neighbours keep theirs",
              RotateOrder(one) == "0,90,90,270", RotateOrder(one));

        // ── INDEPENDENCE: the point of the whole exercise ──────────────────────────────────
        // Mutate page 1 — /Rotate, plus a page-level key of our own so the check does not rest on
        // one PdfSharpCore code path — and reopen. If the two pages are one object, page 2 follows.
        string mutated = Path.Combine(tmp, "dup-mutate-original.pdf");
        using (var doc = PdfReader.Open(one, PdfDocumentOpenMode.Modify))
        {
            doc.Pages[1].Rotate = 180;
            doc.Pages[1].Elements.SetString("/TDPdfProbe", "original");
            doc.Save(mutated);
        }
        Check("rotating the ORIGINAL after the duplication leaves the copy alone",
              RotateOrder(mutated) == "0,180,90,270", RotateOrder(mutated));
        using (var d = PdfReader.Open(mutated, PdfDocumentOpenMode.ReadOnly))
        {
            Check("a page-level key written to the original does not appear on the copy",
                  d.Pages[1].Elements.GetString("/TDPdfProbe") == "original"
                  && !d.Pages[2].Elements.ContainsKey("/TDPdfProbe"),
                  $"original=\"{d.Pages[1].Elements.GetString("/TDPdfProbe")}\" " +
                  $"copy has key={d.Pages[2].Elements.ContainsKey("/TDPdfProbe")}");
            Check("the two pages are separate indirect objects",
                  d.Pages[1].Reference?.ObjectID != d.Pages[2].Reference?.ObjectID,
                  $"{d.Pages[1].Reference?.ObjectID} vs {d.Pages[2].Reference?.ObjectID}");
        }

        // And the other direction: the copy is not a read-only shadow of the original either.
        string mutatedCopy = Path.Combine(tmp, "dup-mutate-copy.pdf");
        using (var doc = PdfReader.Open(one, PdfDocumentOpenMode.Modify))
        {
            doc.Pages[2].Rotate = 180;
            doc.Save(mutatedCopy);
        }
        Check("rotating the COPY leaves the original alone",
              RotateOrder(mutatedCopy) == "0,90,180,270", RotateOrder(mutatedCopy));

        // Content, not just page-level keys. The page dictionaries could be distinct while both
        // pointed at one shared content stream, and then an inline text edit — which rewrites that
        // stream — would still corrupt the twin. Replace the copy's content outright and check the
        // original still says what it always said.
        string mutatedContent = Path.Combine(tmp, "dup-mutate-content.pdf");
        using (var doc = PdfReader.Open(one, PdfDocumentOpenMode.Modify))
        {
            using (var gfx = XGraphics.FromPdfPage(doc.Pages[2], XGraphicsPdfPageOptions.Replace))
                gfx.DrawString("ZZZ", new XFont("Helvetica", 24), XBrushes.Black, 60, 120);
            doc.Save(mutatedContent);
        }
        Check("replacing the COPY's content stream leaves the original's text intact",
              TextOrder(mutatedContent) == "AAA|BBB|ZZZ|CCC", TextOrder(mutatedContent));

        // ── A multi-page, non-contiguous selection ─────────────────────────────────────────
        // Pages 0 and 2 of a four-page document: both copies go after page 2, in source order, and
        // page 3 is pushed down. Interleaving would have given AAA|AAA|BBB|CCC|CCC|DDD instead.
        string src4 = Fixture(tmp, "dup-src4.pdf", ["AAA", "BBB", "CCC", "DDD"], [0, 90, 180, 270]);
        var (many, manyAt) = Run(tmp, src4, "dup-many.pdf", [2, 0]);   // deliberately out of order
        Check("the block starts after the last selected page", manyAt == 3, $"{manyAt}");
        Check("copies arrive as one block, in the originals' relative order",
              TextOrder(many) == "AAA|BBB|CCC|AAA|CCC|DDD", TextOrder(many));
        Check("each copy carries its OWN rotation, not the block's first",
              RotateOrder(many) == "0,90,180,0,180,270", RotateOrder(many));

        // Independence has to hold for every page of a block, not just the one that got looked at.
        string manyMutated = Path.Combine(tmp, "dup-many-mutated.pdf");
        using (var doc = PdfReader.Open(many, PdfDocumentOpenMode.Modify))
        {
            doc.Pages[3].Rotate = 90;    // copy of page 0
            doc.Pages[4].Rotate = 90;    // copy of page 2
            doc.Save(manyMutated);
        }
        Check("rotating both copies leaves both originals alone",
              RotateOrder(manyMutated) == "0,90,180,90,90,270", RotateOrder(manyMutated));

        // ── Duplicating a duplicate ────────────────────────────────────────────────────────
        // The copy is a first-class page: it can itself be duplicated, and the grandchild is
        // independent of both. This is the case that catches a copy that is merely a cleverer
        // alias — an alias survives one round and falls over on the second.
        string twice = Path.Combine(tmp, "dup-twice.pdf");
        using (var doc = PdfReader.Open(one, PdfDocumentOpenMode.Modify))
        {
            PdfPageDuplicate.Duplicate(doc, [2]);   // duplicate the copy made above
            doc.Pages[3].Rotate = 180;              // mutate the grandchild
            doc.Save(twice);
        }
        Check("a copy can itself be duplicated, and the grandchild is independent too",
              TextOrder(twice) == "AAA|BBB|BBB|BBB|CCC" && RotateOrder(twice) == "0,90,90,180,270",
              $"{TextOrder(twice)} / {RotateOrder(twice)}");

        // ── The whole document ─────────────────────────────────────────────────────────────
        // Select-all then duplicate: the copies append, and nothing is aliased to anything.
        var (all, allAt) = Run(tmp, src, "dup-all.pdf", [0, 1, 2]);
        Check("duplicating every page appends the whole document to itself",
              allAt == 3 && TextOrder(all) == "AAA|BBB|CCC|AAA|BBB|CCC", $"{allAt} {TextOrder(all)}");
        string allMutated = Path.Combine(tmp, "dup-all-mutated.pdf");
        using (var doc = PdfReader.Open(all, PdfDocumentOpenMode.Modify))
        {
            for (int i = 3; i < 6; i++) doc.Pages[i].Rotate = 0;
            doc.Save(allMutated);
        }
        Check("zeroing every copy's rotation leaves every original's rotation untouched",
              RotateOrder(allMutated) == "0,90,270,0,0,0", RotateOrder(allMutated));

        // ── The naive spelling, pinned ─────────────────────────────────────────────────────
        // doc.Pages.Insert(i, doc.Pages[j]) is what this service exists instead of. PdfSharpCore
        // refuses it — the "already owned" branch has no copy in it, so all it could do is put the
        // same indirect reference into the page array twice. Pinning the refusal here means that if
        // a future PdfSharpCore ever accepts the call, this test fails rather than the app quietly
        // shipping documents whose two "pages" are one object.
        string naiveDetail;
        bool naiveRefused;
        using (var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify))
        {
            try
            {
                doc.Pages.Insert(2, doc.Pages[1]);
                naiveRefused = false;
                naiveDetail = $"accepted — {doc.PageCount} pages, the copy may be an alias";
            }
            catch (InvalidOperationException ex)
            {
                naiveRefused = true;
                naiveDetail = ex.Message;
            }
        }
        Check("PdfSharpCore refuses to re-insert a page the document already owns", naiveRefused, naiveDetail);

        // ── Nothing selected ───────────────────────────────────────────────────────────────
        // The context-menu row is disabled in this state, so this is the belt-and-braces path: it
        // must leave the document exactly as it was rather than duplicating page one unasked.
        using (var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify))
        {
            int before = doc.PageCount;
            Check("an empty selection duplicates nothing and touches nothing",
                  PdfPageDuplicate.Duplicate(doc, []) == -1 && doc.PageCount == before,
                  $"{doc.PageCount} page(s)");
            Check("a selection naming only pages that do not exist is also a no-op",
                  PdfPageDuplicate.Duplicate(doc, [7, 9]) == -1 && doc.PageCount == before,
                  $"{doc.PageCount} page(s)");
        }
    }
}
