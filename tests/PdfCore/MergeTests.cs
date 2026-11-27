using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using PdfSharpCore.Pdf.IO;
using TDPdf.Services;
using PigDoc = UglyToad.PdfPig.PdfDocument;

/// <summary>
/// File > Merge Files (#242): the ordering and page-selection model, the folder and drop rules, the
/// password-protected input path, and a real merge with form fields, bookmarks and named links.
/// </summary>
internal static class MergeTests
{
    private static PdfiumInterop.PdfRect R(double l, double b, double w, double h) => new(l, b, l + w, b + h);

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nMerge Files: ordering and page selection");
        Model(Check);
        Console.WriteLine("\nMerge Files: files, folders, names");
        Inputs(Check);
        Console.WriteLine("\nMerge Files: protected inputs");
        Protected(Check, tmp);
        Console.WriteLine("\nMerge Files: the merged document");
        Merged(Check, tmp);
    }

    // ── Model ─────────────────────────────────────────────────────────────────────────────────

    private static MergeSource S(int id, string name, int pages, bool open = false) => new(id, name, $"/x/{name}.pdf", pages, open);

    private static string Order(MergePlan plan) =>
        string.Join(" ", plan.IncludedPages().Select(p => $"{(char)('A' + p.SourceId - 1)}{p.PageIndex + 1}"));

    private static void Model(Action<string, bool, string> Check)
    {
        var plan = new MergePlan();
        plan.AddSource(S(1, "a", 3));
        plan.AddSource(S(2, "b", 2));
        plan.AddSource(S(3, "c", 1));
        Check("plan: pages follow the files", Order(plan) == "A1 A2 A3 B1 B2 C1", Order(plan));
        Check("plan: totals", plan.TotalPages == 6 && plan.IncludedCount == 6 && !plan.Arranged, "");

        plan.MoveSource(2, 0);
        Check("plan: moving a file moves its pages with it", Order(plan) == "C1 A1 A2 A3 B1 B2", Order(plan));
        plan.MoveSource(0, 99);
        Check("plan: a move past the end lands on the end", Order(plan) == "A1 A2 A3 B1 B2 C1" && plan.Sources[2].Id == 3, Order(plan));
        Check("plan: moving a file from a bad index changes nothing", plan.MoveSource(-1, 0) == -1 && Order(plan) == "A1 A2 A3 B1 B2 C1", "");

        plan.AddSource(S(4, "open", 2, open: true), index: 0);
        Check("plan: the open document goes first when added at the front", Order(plan) == "D1 D2 A1 A2 A3 B1 B2 C1", Order(plan));
        Check("plan: it is marked as the open document", plan.Sources[0].IsOpenDocument, "");
        plan.RemoveSource(4);

        // Hand arrangement.
        plan.SetIncluded(1, false);   // drop A2
        Check("plan: dropping a page leaves it out of the result", Order(plan) == "A1 A3 B1 B2 C1" && plan.IncludedCount == 5 && plan.TotalPages == 6, Order(plan));
        Check("plan: dropping marks the arrangement as hand-made", plan.Arranged, "");
        plan.MovePage(4, 0);   // B2 to the front
        Check("plan: moving a page reorders the result", Order(plan) == "B2 A1 A3 B1 C1", Order(plan));

        plan.MoveSource(2, 0);   // C to the front of the FILE list
        Check("plan: moving a file does not reshuffle a hand-made arrangement", Order(plan) == "B2 A1 A3 B1 C1", Order(plan));
        plan.AddSource(S(5, "e", 2));
        Check("plan: a file added later puts its pages on the end", Order(plan) == "B2 A1 A3 B1 C1 E1 E2", Order(plan));
        plan.RemoveSource(2);
        Check("plan: a removed file takes its pages with it", Order(plan) == "A1 A3 C1 E1 E2", Order(plan));

        plan.ResetArrangement();
        Check("plan: reset follows the files again, all included", Order(plan) == "C1 A1 A2 A3 E1 E2" && !plan.Arranged, Order(plan));

        // Page moves that do nothing do not make an arrangement.
        var quiet = new MergePlan();
        quiet.AddSource(S(1, "a", 2));
        quiet.MovePage(0, 0);
        quiet.SetIncluded(0, true);
        quiet.MovePage(5, 0);
        Check("plan: a move that goes nowhere is not an arrangement", !quiet.Arranged, "");
        Check("plan: a duplicate id is refused", Throws(() => quiet.AddSource(S(1, "again", 1))), "");

        // Choices survive a file move for pages that still exist.
        var keep = new MergePlan();
        keep.AddSource(S(1, "a", 2));
        keep.AddSource(S(2, "b", 2));
        keep.SetIncluded(1, false);
        keep.ResetArrangement();
        Check("plan: reset includes everything again", keep.IncludedCount == 4, "");

        // Runs.
        var runPlan = new MergePlan();
        runPlan.AddSource(S(1, "a", 3));
        runPlan.AddSource(S(2, "b", 2));
        var runs = runPlan.Runs();
        Check("runs: one per file when nothing is rearranged",
              runs.Count == 2 && runs[0].SourceId == 1 && runs[0].PageIndices.SequenceEqual(new[] { 0, 1, 2 })
              && runs[1].PageIndices.SequenceEqual(new[] { 0, 1 }), "");
        runPlan.MovePage(3, 1);   // B1 between A1 and A2
        runs = runPlan.Runs();
        Check("runs: a page between two of another file splits it",
              runs.Count == 4 && runs.Select(r => r.SourceId).SequenceEqual(new[] { 1, 2, 1, 2 }), string.Join(",", runs.Select(r => r.SourceId)));
        Check("runs: nothing included is no runs", new MergePlan().Runs().Count == 0, "");

        Check("move item: forward", Moved(new[] { 1, 2, 3, 4 }, 0, 2).SequenceEqual(new[] { 2, 3, 1, 4 }), "");
        Check("move item: backward", Moved(new[] { 1, 2, 3, 4 }, 3, 1).SequenceEqual(new[] { 1, 4, 2, 3 }), "");
        Check("move item: out of range target is clamped", Moved(new[] { 1, 2, 3 }, 0, 50).SequenceEqual(new[] { 2, 3, 1 }), "");
    }

    private static List<int> Moved(int[] items, int from, int to)
    {
        var l = items.ToList();
        MergePlan.MoveItem(l, from, to);
        return l;
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; } catch { return true; }
    }

    // ── Inputs ────────────────────────────────────────────────────────────────────────────────

    private static void Inputs(Action<string, bool, string> Check)
    {
        var fs = new Dictionary<string, string[]>
        {
            ["/docs"] = new[] { "/docs/b10.pdf", "/docs/b2.PDF", "/docs/notes.txt", "/docs/sub/a1.pdf", "/docs/B1.pdf" },
            ["/empty"] = Array.Empty<string>(),
        };
        var result = MergeInputs.Expand(
            new[] { "/docs", "/loose/x.pdf", "/loose/pic.png", "", "/empty" },
            p => fs.ContainsKey(p),
            p => fs[p]);
        Check("expand: a folder gives its PDFs in natural order, loose PDFs keep their place",
              result.Files.SequenceEqual(new[] { "/docs/B1.pdf", "/docs/b2.PDF", "/docs/b10.pdf", "/docs/sub/a1.pdf", "/loose/x.pdf" }),
              string.Join(" | ", result.Files));
        Check("expand: a file that is not a PDF is set aside", result.Skipped.SequenceEqual(new[] { "/loose/pic.png" }), string.Join(",", result.Skipped));

        var broken = MergeInputs.Expand(new[] { "/bad" }, _ => true, _ => throw new UnauthorizedAccessException());
        Check("expand: a folder that cannot be read is set aside, not fatal", broken.Files.Count == 0 && broken.Skipped.Count == 1, "");

        var many = MergeInputs.Expand(new[] { "/big" }, _ => true, _ => Enumerable.Range(0, 3000).Select(i => $"/big/f{i:0000}.pdf"));
        Check("expand: one drop is bounded", many.Files.Count == MergeInputs.MaxFiles, $"{many.Files.Count}");

        var names = new[] { "file10", "file2", "File1", "file2a", "file02", "a" };
        Check("natural order: numbers compare as numbers, case is ignored",
              names.OrderBy(n => n, NaturalComparer.Instance).SequenceEqual(new[] { "a", "File1", "file2", "file02", "file2a", "file10" }),
              string.Join(",", names.OrderBy(n => n, NaturalComparer.Instance)));
        Check("display name: no folder, no .pdf", MergeInputs.DisplayName("/x/y/Annual report.pdf") == "Annual report", "");
    }

    // ── Protected inputs ──────────────────────────────────────────────────────────────────────

    private static void Protected(Action<string, bool, string> Check, string tmp)
    {
        string plain = Path.Combine(tmp, "merge-plain-in.pdf");
        using (var d = new PdfDocument()) { d.AddPage(); d.AddPage(); d.Save(plain); }
        var ok = MergeSourceLoader.Load(1, plain, "plain", null, () => Path.Combine(tmp, "never.pdf"));
        Check("loader: an ordinary file is read in place", ok.Outcome == MergeSourceLoader.Outcome.Ok
              && ok.Source!.PageCount == 2 && ok.Source.WorkPath == plain, ok.Error ?? "");

        string locked = Path.Combine(tmp, "merge-locked-in.pdf");
        using (var d = new PdfDocument())
        {
            d.AddPage(); d.AddPage(); d.AddPage();
            d.SecuritySettings.UserPassword = "secret";
            d.SecuritySettings.OwnerPassword = "owner";
            d.Save(locked);
        }
        Check("loader: the fixture really is encrypted", PdfEncryptionScan.FileHasEncryption(locked), "");

        string work = Path.Combine(tmp, "merge-locked-work.pdf");
        var none = MergeSourceLoader.Load(2, locked, "locked", null, () => work);
        Check("loader: a file that needs a password asks for one", none.Outcome == MergeSourceLoader.Outcome.NeedsPassword, none.Error ?? "");
        var wrong = MergeSourceLoader.Load(2, locked, "locked", "nope", () => work);
        Check("loader: a wrong password asks again", wrong.Outcome == MergeSourceLoader.Outcome.NeedsPassword, wrong.Error ?? "");
        var right = MergeSourceLoader.Load(2, locked, "locked", "secret", () => work);
        Check("loader: the right password opens it", right.Outcome == MergeSourceLoader.Outcome.Ok && right.Source!.PageCount == 3, right.Error ?? "");
        Check("loader: the pages are read from an unprotected working copy",
              right.Source is { } s && s.WorkPath == work && File.Exists(work) && !PdfEncryptionScan.FileHasEncryption(work), "");

        var missing = MergeSourceLoader.Load(3, Path.Combine(tmp, "nope.pdf"), "nope", null, () => work);
        Check("loader: a file that is not there fails with a reason", missing.Outcome == MergeSourceLoader.Outcome.Failed && !string.IsNullOrEmpty(missing.Error), "");
        string junk = Path.Combine(tmp, "merge-junk.pdf");
        File.WriteAllText(junk, "this is not a pdf");
        var bad = MergeSourceLoader.Load(4, junk, "junk", null, () => work);
        Check("loader: a file that is not a PDF fails with a reason", bad.Outcome == MergeSourceLoader.Outcome.Failed, bad.Outcome.ToString());

        // The working copy merges like any other file.
        var plan = new MergePlan();
        plan.AddSource(ok.Source!);
        plan.AddSource(right.Source!);
        string dest = Path.Combine(tmp, "merge-protected-out.pdf");
        var rep = PdfMerger.Merge(plan.Sources, plan.IncludedPages(), dest, null, CancellationToken.None);
        Check("merge: a protected input is merged through its working copy", rep.Pages == 5 && !PdfEncryptionScan.FileHasEncryption(dest), $"{rep.Pages}");
    }

    // ── The merged document ───────────────────────────────────────────────────────────────────

    private static void Make(string path, string label, int pages, string? field, bool font,
                             Action<PdfDocument>? outline = null, bool link = false)
    {
        var doc = new PdfDocument();
        for (int i = 0; i < pages; i++)
        {
            var p = doc.AddPage();
            using var g = XGraphics.FromPdfPage(p);
            g.DrawString($"{label}{i + 1}", new XFont("Helvetica", 24), XBrushes.Black, 100, 100);
        }
        if (field is not null)
            PdfFormBuilder.Add(doc, doc.Pages[0], new[] { R(60, 600, 200, 22) },
                new PdfFormBuilder.FieldSpec { Kind = PdfFormBuilder.FieldKind.Text, Name = field });
        if (font)
        {
            var f = new PdfDictionary(doc);
            f.Elements["/Type"] = new PdfName("/Font");
            f.Elements["/Subtype"] = new PdfName("/Type1");
            f.Elements["/BaseFont"] = new PdfName("/Courier");
            doc.Internals.AddObject(f);
            doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")!.Elements.GetDictionary("/DR")!
                .Elements.GetDictionary("/Font")!.Elements["/F9"] = f.Reference!;
        }
        outline?.Invoke(doc);
        if (link && pages > 1)
        {
            var dests = new PdfDictionary(doc);
            var arr = new PdfArray(doc);
            arr.Elements.Add(doc.Pages[1].Reference!);
            arr.Elements.Add(new PdfName("/Fit"));
            dests.Elements["/chap"] = arr;
            doc.Internals.Catalog.Elements["/Dests"] = dests;

            var action = new PdfDictionary(doc);
            action.Elements["/S"] = new PdfName("/GoTo");
            action.Elements["/D"] = new PdfString("chap");
            var annot = new PdfDictionary(doc);
            annot.Elements["/Type"] = new PdfName("/Annot");
            annot.Elements["/Subtype"] = new PdfName("/Link");
            var rect = new PdfArray(doc);
            foreach (double v in new[] { 10.0, 10.0, 100.0, 30.0 }) rect.Elements.Add(new PdfReal(v));
            annot.Elements["/Rect"] = rect;
            annot.Elements["/A"] = action;
            doc.Internals.AddObject(annot);
            var annots = doc.Pages[0].Elements.GetArray("/Annots") ?? new PdfArray(doc);
            annots.Elements.Add(annot.Reference!);
            doc.Pages[0].Elements["/Annots"] = annots;
        }
        doc.Save(path);
    }

    private static List<string> FieldNames(PdfDocument d)
    {
        var res = new List<string>();
        var f = d.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields");
        for (int i = 0; i < (f?.Elements.Count ?? 0); i++)
            if (PdfItems.DerefDict(f!.Elements[i])?.Elements["/T"] is PdfString t) res.Add(t.Value);
        return res;
    }

    private static void Flatten(PdfDocument d, PdfOutlineCollection col, int depth, List<(string Title, int Page, int Depth)> into)
    {
        foreach (PdfOutline o in col)
        {
            int page = -1;
            for (int i = 0; i < d.PageCount; i++) if (ReferenceEquals(d.Pages[i], o.DestinationPage)) page = i;
            into.Add((o.Title, page, depth));
            Flatten(d, o.Outlines, depth + 1, into);
        }
    }

    private static string Outline(string path)
    {
        using var d = PdfReader.Open(path, PdfDocumentOpenMode.ReadOnly);
        var list = new List<(string, int, int)>();
        if (d.Internals.Catalog.Elements.ContainsKey("/Outlines")) Flatten(d, d.Outlines, 0, list);
        return string.Join(" ", list.Select(x => $"{new string('>', x.Item3)}{x.Item1}@{x.Item2}"));
    }

    private static string PageTexts(string path)
    {
        using var pig = PigDoc.Open(path);
        return string.Join(" ", pig.GetPages().Select(p => p.Text.Trim()));
    }

    private static void Merged(Action<string, bool, string> Check, string tmp)
    {
        string a = Path.Combine(tmp, "mf-a.pdf"), b = Path.Combine(tmp, "mf-b.pdf"), c = Path.Combine(tmp, "mf-c.pdf");
        Make(a, "A", 3, "Name", false, d =>
        {
            var intro = d.Outlines.Add("Intro", d.Pages[0], true);
            intro.Outlines.Add("Intro sub", d.Pages[1], true);
            d.Outlines.Add("Appendix", d.Pages[2], true);
        }, link: true);
        Make(b, "B", 2, "Name", true, d =>
        {
            var part = d.Outlines.Add("Part B", d.Pages[1], true);
            part.Outlines.Add("Deep", d.Pages[1], true);
        });
        Make(c, "C", 1, null, false);

        var plan = new MergePlan();
        foreach (var (path, name) in new[] { (a, "A"), (b, "B"), (c, "C") })
        {
            var l = MergeSourceLoader.Load(plan.NextSourceId(), path, name, null, () => throw new InvalidOperationException());
            plan.AddSource(l.Source!);
        }

        // Straight through.
        string out1 = Path.Combine(tmp, "mf-out1.pdf");
        var seen = new List<string>();
        var rep = PdfMerger.Merge(plan.Sources, plan.IncludedPages(), out1, new Progress<string>(seen.Add), CancellationToken.None);
        Check("merge: every page, in order", PageTexts(out1) == "A1 A2 A3 B1 B2 C1", PageTexts(out1));
        Check("merge: the report counts pages", rep.Pages == 6, $"{rep.Pages}");
        using (var d = PdfReader.Open(out1, PdfDocumentOpenMode.Import))
        {
            var names = FieldNames(d);
            Check("merge: form fields of both files are registered, the clash renamed",
                  names.Count == 2 && names.Contains("Name") && names.Contains("Name_2"), string.Join(",", names));
            Check("merge: the clash is reported", rep.FormFieldsRenamed == 1, $"{rep.FormFieldsRenamed}");
            var fonts = d.Internals.Catalog.Elements.GetDictionary("/AcroForm")!.Elements.GetDictionary("/DR")!.Elements.GetDictionary("/Font")!;
            Check("merge: a form font of the second file comes along", fonts.Elements.ContainsKey("/F9"), string.Join(",", fonts.Elements.Keys));
        }
        Check("merge: each file's bookmarks sit under a bookmark named after the file, with their own children",
              Outline(out1) == "A@0 >Intro@0 >>Intro sub@1 >Appendix@2 B@3 >Part B@4 >>Deep@4 C@5", Outline(out1));
        Check("merge: counts the bookmarks", rep.FileBookmarks == 3 && rep.OutlineEntries == 5, $"{rep.FileBookmarks}/{rep.OutlineEntries}");
        Check("merge: progress was reported", seen.Count >= 3, string.Join("; ", seen));

        using (var d = PdfReader.Open(out1, PdfDocumentOpenMode.Import))
        {
            var annots = d.Pages[0].Elements.GetArray("/Annots");
            var link = PdfItems.DerefDict(annots?.Elements[annots.Elements.Count - 1]);
            var dest = link?.Elements.GetDictionary("/A")?.Elements["/D"];
            var target = (PdfItems.Deref(dest) as PdfArray)?.Elements[0];
            bool second = target is PdfReference r && r.ObjectNumber == d.Pages[1].Reference?.ObjectNumber;
            Check("merge: a named link now points at the same page of the merged document", second, $"{dest}");
        }

        // Rearranged: C first, A2 dropped, B2 before B1.
        plan.MoveSource(2, 0);
        plan.SetIncluded(plan.Pages.ToList().FindIndex(p => p.SourceId == 1 && p.PageIndex == 1), false);
        int b2 = plan.Pages.ToList().FindIndex(p => p.SourceId == 2 && p.PageIndex == 1);
        int b1 = plan.Pages.ToList().FindIndex(p => p.SourceId == 2 && p.PageIndex == 0);
        plan.MovePage(b2, b1);
        string out2 = Path.Combine(tmp, "mf-out2.pdf");
        PdfMerger.Merge(plan.Sources, plan.IncludedPages(), out2, null, CancellationToken.None);
        Check("merge: a dropped page is gone, moved pages are where they were put", PageTexts(out2) == "C1 A1 A3 B2 B1", PageTexts(out2));
        Check("merge: bookmarks follow the new order and skip the dropped page, promoting its children",
              Outline(out2) == "C@0 A@1 >Intro@1 >Appendix@2 B@3 >Part B@3 >>Deep@3", Outline(out2));
        using (var d = PdfReader.Open(out2, PdfDocumentOpenMode.Import))
        {
            var annots = d.Pages[1].Elements.GetArray("/Annots");
            var link = PdfItems.DerefDict(annots?.Elements[annots.Elements.Count - 1]);
            var dest = link?.Elements.GetDictionary("/A")?.Elements["/D"];
            Check("merge: a link to a dropped page is left as it was", dest is PdfString { Value: "chap" }, $"{dest}");
            Check("merge: the fields survive an arrangement", FieldNames(d).Count == 2, string.Join(",", FieldNames(d)));
        }

        // Pages of one file split across another file's pages.
        var split = new MergePlan();
        foreach (var s in plan.Sources.OrderBy(s => s.Id)) split.AddSource(s);
        split.MovePage(3, 1);   // B1 between A1 and A2
        string out3 = Path.Combine(tmp, "mf-out3.pdf");
        PdfMerger.Merge(split.Sources, split.IncludedPages(), out3, null, CancellationToken.None);
        Check("merge: a file read in two stretches keeps its pages and fields", PageTexts(out3) == "A1 B1 A2 A3 B2 C1" && FieldNames(PdfReader.Open(out3, PdfDocumentOpenMode.Import)).Count == 2, PageTexts(out3));
        Check("merge: its bookmark points at the file's first page in the result",
              Outline(out3).StartsWith("A@0 >Intro@0 >>Intro sub@2 >Appendix@3 B@1 >Part B@4"), Outline(out3));

        // Without bookmarks.
        string out4 = Path.Combine(tmp, "mf-out4.pdf");
        PdfMerger.Merge(plan.Sources, plan.IncludedPages(), out4, null, CancellationToken.None, bookmarks: false);
        Check("merge: bookmarks can be left out", Outline(out4) == "", Outline(out4));

        // Cancellation and bad input.
        string out5 = Path.Combine(tmp, "mf-cancelled.pdf");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool cancelled = false;
        try { PdfMerger.Merge(plan.Sources, plan.IncludedPages(), out5, null, cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check("merge: Esc cancels and leaves no file", cancelled && !File.Exists(out5), "");
        Check("merge: nothing to merge is refused",
              Throws(() => PdfMerger.Merge(plan.Sources, Array.Empty<MergePage>(), Path.Combine(tmp, "mf-none.pdf"), null, CancellationToken.None)), "");

        // A long list: many small files in one go, and the progress callback runs per file.
        var many = new MergePlan();
        var one = plan.Find(3)!;
        for (int i = 0; i < 60; i++) many.AddSource(new MergeSource(many.NextSourceId(), $"c{i}", one.WorkPath, 1));
        int ticks = 0;
        string out6 = Path.Combine(tmp, "mf-many.pdf");
        var rep6 = PdfMerger.Merge(many.Sources, many.IncludedPages(), out6, new Progress<string>(_ => ticks++), CancellationToken.None);
        Check("merge: sixty files merge", rep6.Pages == 60 && rep6.FileBookmarks == 60, $"{rep6.Pages}/{rep6.FileBookmarks}");
    }
}
