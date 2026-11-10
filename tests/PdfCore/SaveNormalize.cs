using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using TDPdf.Services;

/// <summary>
/// Pins <see cref="PdfSaveNormalizer"/>: the three scrubs every save runs (dangling /Outlines,
/// degenerate page boxes, dead signatures). Each test builds a document, round-trips it through a
/// real save so the entries are parsed arrays/references the way a file from disk has them, runs the
/// scrub, and checks both what was removed and what was deliberately left alone. A healthy document
/// must pass through untouched, because that is the case every save hits.
/// </summary>
internal static class SaveNormalize
{
    private static PdfArray Box(PdfDocument d, double a, double b, double c, double e) =>
        new(d, new PdfReal(a), new PdfReal(b), new PdfReal(c), new PdfReal(e));

    /// <summary>Saves and reopens so every entry is in its on-disk (parsed) shape.</summary>
    private static PdfDocument Reopen(PdfDocument d)
    {
        var ms = new MemoryStream();
        d.Save(ms, false);
        ms.Position = 0;
        return PdfReader.Open(ms, PdfDocumentOpenMode.Modify);
    }

    private static PdfDocument OnePage(Action<PdfDocument, PdfPage>? setup = null)
    {
        var d = new PdfDocument();
        var p = d.AddPage();
        p.Elements["/MediaBox"] = Box(d, 0, 0, 612, 792);
        setup?.Invoke(d, p);
        return d;
    }

    private static double[]? Nums(PdfItem? item)
    {
        if (item is PdfRectangle r) return new[] { r.X1, r.Y1, r.X2, r.Y2 };
        if (item is not PdfArray a) return null;
        return a.Elements.Select(i => i is PdfReal x ? x.Value : i is PdfInteger n ? (double)n.Value : double.NaN).ToArray();
    }

    private static bool SameBox(PdfItem? item, params double[] want)
    {
        var got = Nums(item);
        return got is not null && got.Length == want.Length &&
               got.Zip(want).All(t => Math.Abs(t.First - t.Second) < 1e-6);
    }

    private static PdfDictionary SigField(PdfDocument d, bool signed)
    {
        var f = new PdfDictionary(d);
        f.Elements["/FT"] = new PdfName("/Sig");
        f.Elements["/T"] = new PdfString("Sig1");
        if (signed)
        {
            var v = new PdfDictionary(d);
            v.Elements["/Type"] = new PdfName("/Sig");
            d.Internals.AddObject(v);
            f.Elements["/V"] = v.Reference;
        }
        d.Internals.AddObject(f);
        return f;
    }

    private static void AddAcroForm(PdfDocument d, params PdfDictionary[] fields)
    {
        var arr = new PdfArray(d);
        foreach (var f in fields) arr.Elements.Add(f.Reference);
        var acro = new PdfDictionary(d);
        acro.Elements["/Fields"] = arr;
        d.Internals.AddObject(acro);
        d.Internals.Catalog.Elements["/AcroForm"] = acro.Reference;
    }

    internal static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nPre-save normalisation");

        // ── Outlines ─────────────────────────────────────────────────────────────────
        {
            var d = OnePage((doc, _) =>
            {
                var o = new PdfDictionary(doc);
                o.Elements["/Type"] = new PdfName("/Outlines");
                doc.Internals.AddObject(o);
                doc.Internals.Catalog.Elements["/Outlines"] = o.Reference;
            });
            PdfSaveNormalizer.ScrubEmptyOutlines(d);
            Check("an /Outlines with no /First is dropped", d.Internals.Catalog.Elements["/Outlines"] is null, "");
        }
        {
            var d = OnePage((doc, _) =>
            {
                var item = new PdfDictionary(doc);
                item.Elements["/Title"] = new PdfString("Chapter 1");
                doc.Internals.AddObject(item);
                var o = new PdfDictionary(doc);
                o.Elements["/Type"] = new PdfName("/Outlines");
                o.Elements["/First"] = item.Reference;
                o.Elements["/Last"] = item.Reference;
                doc.Internals.AddObject(o);
                doc.Internals.Catalog.Elements["/Outlines"] = o.Reference;
            });
            var r = Reopen(d);
            PdfSaveNormalizer.ScrubEmptyOutlines(r);
            Check("a real bookmark tree (/First present) survives, after a round trip",
                  r.Internals.Catalog.Elements["/Outlines"] is not null, "");
        }
        {
            var d = OnePage();
            PdfSaveNormalizer.ScrubEmptyOutlines(d);
            Check("a document with no /Outlines is left without one", d.Internals.Catalog.Elements["/Outlines"] is null, "");
        }
        {
            // The catalog entry is a direct dictionary, not a reference.
            var d = OnePage((doc, _) => doc.Internals.Catalog.Elements["/Outlines"] = new PdfDictionary(doc));
            PdfSaveNormalizer.ScrubEmptyOutlines(d);
            Check("a direct empty /Outlines dictionary is dropped too", d.Internals.Catalog.Elements["/Outlines"] is null, "");
        }

        // ── Page boxes ───────────────────────────────────────────────────────────────
        {
            var r = Reopen(OnePage((d, p) => p.Elements["/CropBox"] = Box(d, 0, 0, 0, 0)));
            PdfSaveNormalizer.ScrubDegeneratePageBoxes(r);
            Check("a [0 0 0 0] /CropBox is removed", r.Pages[0].Elements["/CropBox"] is null, "");
            Check("...and the real /MediaBox is untouched", SameBox(r.Pages[0].Elements["/MediaBox"], 0, 0, 612, 792), "");
        }
        {
            var r = Reopen(OnePage((d, p) => p.Elements["/CropBox"] = Box(d, 10, 10, 10.5, 700)));
            PdfSaveNormalizer.ScrubDegeneratePageBoxes(r);
            Check("a /CropBox under one point wide is removed", r.Pages[0].Elements["/CropBox"] is null, "");
        }
        {
            // Inverted corners are the same rectangle, normalised: legal, so must not be deleted.
            var r = Reopen(OnePage((d, p) => p.Elements["/CropBox"] = Box(d, 500, 700, 100, 100)));
            PdfSaveNormalizer.ScrubDegeneratePageBoxes(r);
            Check("an inverted but sizeable /CropBox is kept", r.Pages[0].Elements["/CropBox"] is not null, "");
        }
        {
            var r = Reopen(OnePage((d, p) => p.Elements["/CropBox"] = Box(d, 0, 0, 700, 900)));
            PdfSaveNormalizer.ScrubDegeneratePageBoxes(r);
            Check("a /CropBox that pokes outside the /MediaBox is removed (#169)",
                  r.Pages[0].Elements["/CropBox"] is null, "");
        }
        {
            var r = Reopen(OnePage((d, p) => p.Elements["/CropBox"] = Box(d, 0.005, 0.005, 612.005, 792.005)));
            PdfSaveNormalizer.ScrubDegeneratePageBoxes(r);
            Check("a /CropBox within the 0.01pt tolerance is kept", r.Pages[0].Elements["/CropBox"] is not null, "");
        }
        {
            var r = Reopen(OnePage((d, p) => p.Elements["/CropBox"] = Box(d, 50, 60, 500, 700)));
            PdfSaveNormalizer.ScrubDegeneratePageBoxes(r);
            Check("a normal inset /CropBox passes through unchanged",
                  SameBox(r.Pages[0].Elements["/CropBox"], 50, 60, 500, 700), "");
        }
        {
            // Zero /MediaBox on the page, real one on the parent /Pages node: re-planted from the tree.
            var r = Reopen(OnePage((d, p) =>
            {
                p.Elements["/MediaBox"] = Box(d, 0, 0, 0, 0);
                p.Elements.GetDictionary("/Parent")!.Elements["/MediaBox"] = Box(d, 0, 0, 595, 842);
            }));
            PdfSaveNormalizer.ScrubDegeneratePageBoxes(r);
            Check("a zero /MediaBox is healed from the inherited page-tree box",
                  SameBox(r.Pages[0].Elements["/MediaBox"], 0, 0, 595, 842), "");
        }
        {
            // Nothing inheritable at all: left absent rather than zero-size.
            var r = Reopen(OnePage((d, p) =>
            {
                p.Elements["/MediaBox"] = Box(d, 0, 0, 0, 0);
            }));
            PdfSaveNormalizer.ScrubDegeneratePageBoxes(r);
            Check("a zero /MediaBox with nothing to inherit is dropped, not left zero-size",
                  r.Pages[0].Elements["/MediaBox"] is null, "");
        }
        {
            // A bad media box must never be a reason to delete a good crop box.
            var r = Reopen(OnePage((d, p) =>
            {
                p.Elements["/MediaBox"] = Box(d, 0, 0, 0, 0);
                p.Elements["/CropBox"] = Box(d, 0, 0, 300, 300);
            }));
            PdfSaveNormalizer.ScrubDegeneratePageBoxes(r);
            Check("a good /CropBox survives when the /MediaBox is unusable",
                  SameBox(r.Pages[0].Elements["/CropBox"], 0, 0, 300, 300), "");
        }
        {
            // Non-numeric garbage is "cannot interpret", which must be left alone.
            var r = Reopen(OnePage((d, p) =>
                p.Elements["/CropBox"] = new PdfArray(d, new PdfName("/x"), new PdfReal(0), new PdfReal(0), new PdfReal(0))));
            PdfSaveNormalizer.ScrubDegeneratePageBoxes(r);
            Check("an uninterpretable /CropBox is left alone rather than destroyed",
                  r.Pages[0].Elements["/CropBox"] is not null, "");
        }
        {
            // Only the bad page is touched in a multi-page file.
            var d = OnePage((doc, p) => p.Elements["/CropBox"] = Box(doc, 0, 0, 0, 0));
            var p2 = d.AddPage();
            p2.Elements["/MediaBox"] = Box(d, 0, 0, 200, 300);
            p2.Elements["/CropBox"] = Box(d, 10, 10, 100, 100);
            var r = Reopen(d);
            PdfSaveNormalizer.ScrubDegeneratePageBoxes(r);
            Check("only the degenerate page loses its crop box",
                  r.Pages[0].Elements["/CropBox"] is null && SameBox(r.Pages[1].Elements["/CropBox"], 10, 10, 100, 100), "");
        }

        // ── Dead signatures ──────────────────────────────────────────────────────────
        {
            var d = OnePage();
            var signed = SigField(d, true);
            var empty = SigField(d, false);
            AddAcroForm(d, signed, empty);
            d.Internals.Catalog.Elements["/Perms"] = new PdfDictionary(d);
            var r = Reopen(d);
            Check("fixture: the reopened document reports one signed field",
                  PdfSignatureScan.SignedFields(r).Count == 1 && PdfSignatureScan.HasUsageRights(r), "");
            PdfSaveNormalizer.ScrubDeadSignatures(r);
            Check("the signature value is stripped", PdfSignatureScan.SignedFields(r).Count == 0, "");
            Check("/Perms (certification / usage rights) is stripped", !PdfSignatureScan.HasUsageRights(r), "");
            var acro = r.Internals.Catalog.Elements.GetDictionary("/AcroForm");
            var fields = acro?.Elements.GetArray("/Fields");
            Check("the now-empty signature fields stay so the file can be re-signed",
                  fields is { Elements.Count: 2 }, fields?.Elements.Count.ToString() ?? "no fields");
        }
        {
            var d = OnePage();
            AddAcroForm(d, SigField(d, false));
            PdfSaveNormalizer.ScrubDeadSignatures(d);
            var acro = d.Internals.Catalog.Elements.GetDictionary("/AcroForm");
            Check("an unsigned form passes through unchanged", acro?.Elements.GetArray("/Fields") is { Elements.Count: 1 }, "");
        }

        // ── Whole pipeline ───────────────────────────────────────────────────────────
        {
            var d = OnePage((doc, p) => p.Elements["/CropBox"] = Box(doc, 50, 60, 500, 700));
            var item = new PdfDictionary(d);
            item.Elements["/Title"] = new PdfString("Only bookmark");
            d.Internals.AddObject(item);
            var o = new PdfDictionary(d);
            o.Elements["/First"] = item.Reference;
            d.Internals.AddObject(o);
            d.Internals.Catalog.Elements["/Outlines"] = o.Reference;
            AddAcroForm(d, SigField(d, false));

            var r = Reopen(d);
            PdfSaveNormalizer.Normalize(r);
            Check("Normalize leaves a healthy document's outlines, boxes and unsigned fields intact",
                  r.Internals.Catalog.Elements["/Outlines"] is not null
                  && SameBox(r.Pages[0].Elements["/CropBox"], 50, 60, 500, 700)
                  && SameBox(r.Pages[0].Elements["/MediaBox"], 0, 0, 612, 792)
                  && r.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields") is { Elements.Count: 1 }, "");

            var r2 = Reopen(r);
            Check("...and it still reopens after the normalised save", r2.PageCount == 1, "");
        }
        {
            var d = OnePage((doc, p) => p.Elements["/CropBox"] = Box(doc, 0, 0, 0, 0));
            d.Internals.Catalog.Elements["/Outlines"] = new PdfDictionary(d);
            AddAcroForm(d, SigField(d, true));
            var r = Reopen(d);
            PdfSaveNormalizer.Normalize(r);
            var back = Reopen(r);
            Check("Normalize applies all three scrubs together and the result round-trips",
                  back.Internals.Catalog.Elements["/Outlines"] is null
                  && back.Pages[0].Elements["/CropBox"] is null
                  && PdfSignatureScan.SignedFields(back).Count == 0, "");
        }
    }
}
