using System.Text;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using PdfSharpCore.Pdf.IO;
using TDPdf.Services;

/// <summary>
/// #243 PDF/UA checking: every automated checkpoint on a small synthetic PDF that has exactly that fault,
/// the report and its JSON, and each of the simple fixes.
/// </summary>
internal static class PdfUaTests
{
    // ── A builder for small tagged PDFs ─────────────────────────────────────────────────

    private sealed class Fx
    {
        internal PdfDocument Doc = new();
        internal PdfPage Page;
        internal PdfDictionary Root;
        internal PdfArray RootKids;
        internal PdfArray ParentNums;
        internal PdfDictionary Cat => Doc.Internals.Catalog;
        private int _nextMcid;

        internal Fx(bool tagged = true, string? lang = "en-US", string? title = "Test document", bool displayTitle = true,
            bool marked = true, int xmpPart = 1, bool xmpTitle = true)
        {
            Page = Doc.AddPage();
            Root = New();
            Root.Elements["/Type"] = new PdfName("/StructTreeRoot");
            RootKids = new PdfArray(Doc);
            ParentNums = new PdfArray(Doc);
            var parentTree = New();
            parentTree.Elements["/Nums"] = ParentNums;
            Root.Elements["/ParentTree"] = parentTree.Reference;
            Root.Elements["/K"] = RootKids;
            if (tagged) Cat.Elements["/StructTreeRoot"] = Root.Reference;
            if (marked)
            {
                var mi = new PdfDictionary(Doc);
                mi.Elements["/Marked"] = new PdfBoolean(true);
                Cat.Elements["/MarkInfo"] = mi;
            }
            if (lang is not null) Cat.Elements.SetString("/Lang", lang);
            if (title is not null) Doc.Info.Title = title;
            if (displayTitle)
            {
                var vp = new PdfDictionary(Doc);
                vp.Elements["/DisplayDocTitle"] = new PdfBoolean(true);
                Cat.Elements["/ViewerPreferences"] = vp;
            }
            if (xmpPart > 0)
            {
                string xmp = "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>"
                    + "<rdf:Description xmlns:pdfuaid='http://www.aiim.org/pdfua/ns/id/' xmlns:dc='http://purl.org/dc/elements/1.1/'>"
                    + $"<pdfuaid:part>{xmpPart}</pdfuaid:part>"
                    + (xmpTitle ? "<dc:title><rdf:Alt><rdf:li xml:lang='x-default'>Test document</rdf:li></rdf:Alt></dc:title>" : "")
                    + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end='w'?>";
                var meta = New();
                meta.Elements["/Type"] = new PdfName("/Metadata");
                meta.Elements["/Subtype"] = new PdfName("/XML");
                meta.CreateStream(Encoding.UTF8.GetBytes(xmp));
                Cat.Elements["/Metadata"] = meta.Reference;
            }
        }

        internal PdfDictionary New()
        {
            var d = new PdfDictionary(Doc);
            Doc.Internals.AddObject(d);
            return d;
        }

        /// <summary>A structure element under <paramref name="parent"/> (null = the root), pointing at this page.</summary>
        internal PdfDictionary Elem(string type, PdfDictionary? parent = null, int? mcid = null, string? alt = null, string? actual = null)
        {
            var e = New();
            e.Elements["/Type"] = new PdfName("/StructElem");
            e.Elements["/S"] = new PdfName("/" + type);
            e.Elements["/P"] = (parent ?? Root).Reference;
            e.Elements["/Pg"] = Page.Reference;
            if (alt is not null) e.Elements.SetString("/Alt", alt);
            if (actual is not null) e.Elements.SetString("/ActualText", actual);
            if (mcid is not null) e.Elements["/K"] = new PdfInteger(mcid.Value);
            AddKid(parent ?? Root, e);
            return e;
        }

        internal void AddKid(PdfDictionary parent, PdfDictionary kid)
        {
            if (ReferenceEquals(parent, Root)) { RootKids.Elements.Add(kid.Reference); return; }
            var k = parent.Elements["/K"];
            if (k is PdfArray arr) arr.Elements.Add(kid.Reference);
            else
            {
                var a = new PdfArray(Doc);
                if (k is not null) a.Elements.Add(k);
                a.Elements.Add(kid.Reference);
                parent.Elements["/K"] = a;
            }
        }

        internal void SetAttr(PdfDictionary elem, string owner, string key, PdfItem value)
        {
            var a = new PdfDictionary(Doc);
            a.Elements["/O"] = new PdfName("/" + owner);
            a.Elements[key] = value;
            elem.Elements["/A"] = a;
        }

        /// <summary>Appends drawing to the page: <paramref name="body"/> is raw content-stream text.</summary>
        internal void Content(string body)
        {
            var c = New();
            c.CreateStream(Encoding.ASCII.GetBytes(body));
            Page.Elements["/Contents"] = c.Reference;
        }

        /// <summary>A filled square that is tagged content (a figure) with the next MCID, and the structure element for it.</summary>
        internal string TaggedSquare(string tag, int mcid, double x = 100) =>
            $"/{tag} <</MCID {mcid}>> BDC {x} 600 50 50 re f EMC\n";

        internal PdfDictionary AddFont(string name = "F1", bool embedded = false, bool toUnicode = false, string baseFont = "Helvetica", bool encoding = true, bool symbolic = false)
        {
            var f = New();
            f.Elements["/Type"] = new PdfName("/Font");
            f.Elements["/Subtype"] = new PdfName("/Type1");
            f.Elements["/BaseFont"] = new PdfName("/" + baseFont);
            if (encoding) f.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
            if (embedded)
            {
                var fd = New();
                fd.Elements["/Type"] = new PdfName("/FontDescriptor");
                fd.Elements["/Flags"] = new PdfInteger(symbolic ? 4 : 32);
                var ff = New();
                ff.CreateStream(new byte[] { 1, 2, 3 });
                fd.Elements["/FontFile"] = ff.Reference;
                f.Elements["/FontDescriptor"] = fd.Reference;
            }
            if (toUnicode)
            {
                var tu = New();
                tu.CreateStream(Encoding.ASCII.GetBytes("/CIDInit /ProcSet findresource begin end"));
                f.Elements["/ToUnicode"] = tu.Reference;
            }
            var res = Page.Elements["/Resources"] as PdfDictionary ?? new PdfDictionary(Doc);
            var fonts = res.Elements["/Font"] as PdfDictionary ?? new PdfDictionary(Doc);
            fonts.Elements["/" + name] = f.Reference;
            res.Elements["/Font"] = fonts;
            Page.Elements["/Resources"] = res;
            return f;
        }

        /// <summary>Hooks the elements into the page's parent tree so the integrity check can follow them.</summary>
        internal void Finish(params PdfDictionary[] mcidOwners)
        {
            Page.Elements["/StructParents"] = new PdfInteger(0);
            var arr = new PdfArray(Doc);
            foreach (var o in mcidOwners) arr.Elements.Add(o.Reference);
            ParentNums.Elements.Add(new PdfInteger(0));
            ParentNums.Elements.Add(arr);
        }

        internal string Save(string dir, string name)
        {
            string p = Path.Combine(dir, name);
            Doc.Save(p);
            return p;
        }
    }

    /// <summary>A document that passes every machine check: a tagged figure with alt text, nothing else.</summary>
    private static Fx Clean(bool figureAlt = true)
    {
        var fx = new Fx();
        var d = fx.Elem("Document");
        var fig = fx.Elem("Figure", d, mcid: 0, alt: figureAlt ? "A square" : null);
        fx.Content(fx.TaggedSquare("Figure", 0));
        fx.Finish(fig);
        return fx;
    }

    private static PdfUaReport Check(Fx fx, string dir, string name) => PdfUaValidator.Check(fx.Save(dir, name));

    private static bool Has(PdfUaReport r, string cp) => r.Findings.Any(f => f.Checkpoint == cp && f.Severity == PdfUaSeverity.Error);

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nPDF/UA checking (#243)");
        string dir = Path.Combine(tmp, "pdfua");
        Directory.CreateDirectory(dir);
        Document(Check, dir);
        Structure(Check, dir);
        Content(Check, dir);
        Annotations(Check, dir);
        Fonts(Check, dir);
        Json(Check, dir);
        Fixes(Check, dir);
        Hostile(Check, dir);
        Cli(Check, dir);
    }

    private static string Why(PdfUaReport r) => string.Join("; ", r.Findings.Select(f => $"{f.Checkpoint} {f.Severity} {f.Message}"));

    // ── Document level ──────────────────────────────────────────────────────────────────

    private static void Document(Action<string, bool, string> Check, string dir)
    {
        var ok = PdfUaTests.Check(Clean(), dir, "clean.pdf");
        Check("ua: a clean tagged document passes the machine checks", ok.Verdict == PdfUaVerdict.PassedMachineChecks && !ok.Findings.Any(f => f.Severity == PdfUaSeverity.Error), Why(ok) + " " + ok.Verdict);
        Check("ua: a clean document is checked as PDF/UA-1 and claims it", ok.Standard == PdfUaStandard.Ua1 && ok.ClaimsPdfUa, "");
        Check("ua: the manual checks are always listed", ok.ManualChecks.Count >= 6, "");
        Check("ua: the verdict text says machine checks, never compliant", ok.VerdictText.Contains("machine checks") && !ok.VerdictText.Contains("compliant"), ok.VerdictText);

        var noMark = PdfUaTests.Check(new Fx(marked: false).Also(f => { var d = f.Elem("Document"); f.Content(""); f.Finish(); }), dir, "nomark.pdf");
        Check("ua: a tree without Marked is reported and fixable", noMark.Findings.Any(f => f.Checkpoint == PdfUaValidator.CpTagged && f.Fix == PdfUaFix.MarkTagged), Why(noMark));

        var untagged = PdfUaTests.Check(new Fx(tagged: false, marked: false), dir, "untagged.pdf");
        Check("ua: an untagged document fails with no fix offered", untagged.Verdict == PdfUaVerdict.Failed && untagged.Findings.Any(f => f.Checkpoint == PdfUaValidator.CpTagged && f.Fix == PdfUaFix.None), Why(untagged));

        var suspects = Clean();
        ((PdfDictionary)suspects.Cat.Elements["/MarkInfo"]!).Elements["/Suspects"] = new PdfBoolean(true);
        Check("ua: Suspects true is reported", Has(PdfUaTests.Check(suspects, dir, "suspects.pdf"), PdfUaValidator.CpTagged), "");

        var noLang = PdfUaTests.Check(Clean().Also(f => f.Cat.Elements.Remove("/Lang")), dir, "nolang.pdf");
        Check("ua: a missing language is reported and fixable", noLang.Findings.Any(f => f.Checkpoint == PdfUaValidator.CpLanguage && f.Fix == PdfUaFix.SetLanguage), Why(noLang));
        var badLang = PdfUaTests.Check(Clean().Also(f => f.Cat.Elements.SetString("/Lang", "not a tag!")), dir, "badlang.pdf");
        Check("ua: a malformed language is reported", Has(badLang, PdfUaValidator.CpLanguage), Why(badLang));
        Check("ua: language tags: en, en-US, zh-Hant-TW are fine; empty, 'e' and spaces are not",
            PdfUaValidator.IsLanguageTag("en") && PdfUaValidator.IsLanguageTag("en-US") && PdfUaValidator.IsLanguageTag("zh-Hant-TW")
            && !PdfUaValidator.IsLanguageTag("") && !PdfUaValidator.IsLanguageTag("e") && !PdfUaValidator.IsLanguageTag("en US") && !PdfUaValidator.IsLanguageTag(null), "");

        var noTitle = PdfUaTests.Check(new Fx(title: null, xmpPart: 0).Also(f => { var d = f.Elem("Document"); f.Finish(); }), dir, "notitle.pdf");
        Check("ua: no title is reported and fixable", noTitle.Findings.Any(f => f.Checkpoint == PdfUaValidator.CpTitle && f.Fix == PdfUaFix.SetTitle), Why(noTitle));
        var infoOnly = PdfUaTests.Check(new Fx(xmpTitle: false).Also(f => { f.Elem("Document"); f.Finish(); }), dir, "infotitle.pdf");
        Check("ua: a title only in Document Info is a warning, not an error", infoOnly.Findings.Any(f => f.Checkpoint == PdfUaValidator.CpTitle && f.Severity == PdfUaSeverity.Warning), Why(infoOnly));

        var noDisplay = PdfUaTests.Check(new Fx(displayTitle: false).Also(f => { f.Elem("Document"); f.Finish(); }), dir, "nodisplay.pdf");
        Check("ua: DisplayDocTitle off is reported and fixable", noDisplay.Findings.Any(f => f.Checkpoint == PdfUaValidator.CpDisplayTitle && f.Fix == PdfUaFix.SetDisplayDocTitle), Why(noDisplay));

        var noXmp = PdfUaTests.Check(new Fx(xmpPart: 0).Also(f => { f.Elem("Document"); f.Finish(); }), dir, "noxmp.pdf");
        Check("ua: no XMP is a warning that the file does not identify as PDF/UA",
            noXmp.Findings.Any(f => f.Checkpoint == PdfUaValidator.CpMetadata && f.Severity == PdfUaSeverity.Warning) && !noXmp.ClaimsPdfUa, Why(noXmp));

        var ua2 = PdfUaTests.Check(new Fx(xmpPart: 2).Also(f => { f.Elem("Document"); f.Finish(); }), dir, "ua2.pdf");
        Check("ua: XMP part 2 means PDF/UA-2 rules, named in the report and every finding",
            ua2.Standard == PdfUaStandard.Ua2 && ua2.StandardName == "PDF/UA-2" && ua2.Findings.All(f => f.Standard == PdfUaStandard.Ua2), ua2.StandardName);
        Check("ua: a PDF/UA-2 file is only partly checked", ua2.Verdict is PdfUaVerdict.PartlyChecked or PdfUaVerdict.Failed && ua2.Coverage.Any(c => c.Contains("PDF/UA-2")), ua2.Verdict.ToString());
        Check("ua: PDF/UA-2 accepts PDF 2.0 structure types",
            !PdfUaTests.Check(new Fx(xmpPart: 2).Also(f => { var d = f.Elem("DocumentFragment"); f.Finish(); }), dir, "ua2type.pdf").Findings.Any(f => f.Checkpoint == PdfUaValidator.CpRoleMap), "");
        Check("ua: PDF/UA-1 does not accept them",
            Has(PdfUaTests.Check(new Fx(xmpPart: 1).Also(f => { f.Elem("DocumentFragment"); f.Finish(); }), dir, "ua1type.pdf"), PdfUaValidator.CpRoleMap), "");

        Check("ua: XMP part parsing (element, attribute, absent)",
            PdfUaValidator.XmpPart("<pdfuaid:part>2</pdfuaid:part>") == 2 && PdfUaValidator.XmpPart("<x pdfuaid:part=\"1\"/>") == 1 && PdfUaValidator.XmpPart("<x/>") is null && PdfUaValidator.XmpPart(null) is null, "");
        Check("ua: XMP title parsing", PdfUaValidator.XmpTitle("<dc:title><rdf:Alt><rdf:li xml:lang='x-default'> Hi </rdf:li></rdf:Alt></dc:title>") == "Hi" && PdfUaValidator.XmpTitle("<dc:title><rdf:Alt><rdf:li> </rdf:li></rdf:Alt></dc:title>") is null, "");

        var xfa = Clean();
        var acro = new PdfDictionary(xfa.Doc); acro.Elements["/XFA"] = new PdfArray(xfa.Doc); xfa.Cat.Elements["/AcroForm"] = acro;
        Check("ua: an XFA form is reported", Has(PdfUaTests.Check(xfa, dir, "xfa.pdf"), PdfUaValidator.CpXfa), "");

        var oc = Clean();
        var ocp = new PdfDictionary(oc.Doc); var d0 = new PdfDictionary(oc.Doc); ocp.Elements["/D"] = d0; oc.Cat.Elements["/OCProperties"] = ocp;
        Check("ua: an unnamed optional content configuration is reported", Has(PdfUaTests.Check(oc, dir, "oc.pdf"), PdfUaValidator.CpOptionalContent), "");

        var emb = Clean();
        var spec = emb.New(); spec.Elements["/Type"] = new PdfName("/Filespec"); spec.Elements.SetString("/F", "a.txt");
        var arr = new PdfArray(emb.Doc); arr.Elements.Add(new PdfString("a.txt")); arr.Elements.Add(spec.Reference);
        var ef = new PdfDictionary(emb.Doc); ef.Elements["/Names"] = arr;
        var names = new PdfDictionary(emb.Doc); names.Elements["/EmbeddedFiles"] = ef; emb.Cat.Elements["/Names"] = names;
        Check("ua: an embedded file without UF is reported", Has(PdfUaTests.Check(emb, dir, "emb.pdf"), PdfUaValidator.CpEmbeddedFile), "");

        // Security: an owner password that stops text extraction.
        var sec = Clean();
        sec.Doc.SecuritySettings.OwnerPassword = "owner";
        sec.Doc.SecuritySettings.PermitAccessibilityExtractContent = false;
        PdfUaReport secReport;
        try { secReport = PdfUaTests.Check(sec, dir, "sec.pdf"); Check("ua: security that blocks accessibility extraction is reported", Has(secReport, PdfUaValidator.CpSecurity), Why(secReport)); }
        catch (Exception ex) { Check("ua: security that blocks accessibility extraction is reported", false, ex.Message); }
        var secOk = Clean();
        secOk.Doc.SecuritySettings.OwnerPassword = "owner";
        secOk.Doc.SecuritySettings.PermitAccessibilityExtractContent = true;
        try { Check("ua: security that allows accessibility extraction is not reported", !Has(PdfUaTests.Check(secOk, dir, "secok.pdf"), PdfUaValidator.CpSecurity), ""); }
        catch (Exception ex) { Check("ua: security that allows accessibility extraction is not reported", false, ex.Message); }
    }

    // ── Structure tree ──────────────────────────────────────────────────────────────────

    private static void Structure(Action<string, bool, string> Check, string dir)
    {
        var fig = PdfUaTests.Check(Clean(figureAlt: false), dir, "figalt.pdf");
        Check("ua: a figure without alt text is reported, with its page and object", fig.Findings.Any(f => f.Checkpoint == PdfUaValidator.CpFigureAlt && f.PageIndex == 0 && f.ObjectNumber > 0), Why(fig));

        var actual = new Fx(); var d = actual.Elem("Document"); var fa = actual.Elem("Figure", d, 0, actual: "ok"); actual.Content(actual.TaggedSquare("Figure", 0)); actual.Finish(fa);
        Check("ua: a figure with ActualText is fine", !Has(PdfUaTests.Check(actual, dir, "figactual.pdf"), PdfUaValidator.CpFigureAlt), "");

        var formula = new Fx(); var fd = formula.Elem("Document"); var fm = formula.Elem("Formula", fd, 0); formula.Content(formula.TaggedSquare("Formula", 0)); formula.Finish(fm);
        Check("ua: a formula without alt text is reported", Has(PdfUaTests.Check(formula, dir, "formula.pdf"), PdfUaValidator.CpFormulaAlt), "");

        var notes = new Fx(); var nd = notes.Elem("Document");
        notes.Elem("Note", nd); var n2 = notes.Elem("Note", nd); var n3 = notes.Elem("Note", nd);
        n2.Elements.SetString("/ID", "n1"); n3.Elements.SetString("/ID", "n1"); notes.Finish();
        var nr = PdfUaTests.Check(notes, dir, "notes.pdf");
        Check("ua: a note without an ID and two notes with the same ID are reported", Has(nr, PdfUaValidator.CpNoteId) && Has(nr, PdfUaValidator.CpNoteIdDuplicate), Why(nr));

        var heads = new Fx(); var hd = heads.Elem("Document"); heads.Elem("H2", hd); heads.Elem("H4", hd); heads.Finish();
        var hr = PdfUaTests.Check(heads, dir, "heads.pdf");
        Check("ua: a first heading that is not H1 and a skipped level are reported", Has(hr, PdfUaValidator.CpHeadingFirst) && Has(hr, PdfUaValidator.CpHeadingSkip), Why(hr));
        var goodHeads = new Fx(); var gd = goodHeads.Elem("Document"); goodHeads.Elem("H1", gd); goodHeads.Elem("H2", gd); goodHeads.Elem("H3", gd); goodHeads.Elem("H2", gd); goodHeads.Finish();
        var ghr = PdfUaTests.Check(goodHeads, dir, "goodheads.pdf");
        Check("ua: H1, H2, H3, H2 is fine", !Has(ghr, PdfUaValidator.CpHeadingFirst) && !Has(ghr, PdfUaValidator.CpHeadingSkip), Why(ghr));
        var mixed = new Fx(); var md = mixed.Elem("Document"); mixed.Elem("H", md); mixed.Elem("H1", md); mixed.Finish();
        Check("ua: H mixed with H1 is reported", Has(PdfUaTests.Check(mixed, dir, "mixed.pdf"), PdfUaValidator.CpHeadingMixed), "");

        // Role map.
        var rm = new Fx(); var rd = rm.Elem("Document"); rm.Elem("MyHeading", rd); rm.Elem("Fancy", rd); rm.Elem("Loop1", rd); rm.Elem("P", rd);
        var map = new PdfDictionary(rm.Doc);
        map.Elements["/Fancy"] = new PdfName("/Sect");
        map.Elements["/Loop1"] = new PdfName("/Loop2"); map.Elements["/Loop2"] = new PdfName("/Loop1");
        map.Elements["/P"] = new PdfName("/Span");
        rm.Root.Elements["/RoleMap"] = map; rm.Finish();
        var rr = PdfUaTests.Check(rm, dir, "rolemap.pdf");
        Check("ua: an unmapped non-standard type is reported", rr.Findings.Any(f => f.Checkpoint == PdfUaValidator.CpRoleMap && f.Message.Contains("MyHeading")), Why(rr));
        Check("ua: a type mapped to a standard type is fine", !rr.Findings.Any(f => f.Message.Contains("Fancy")), Why(rr));
        Check("ua: a circular role map is reported", Has(rr, PdfUaValidator.CpRoleMapCircular), Why(rr));
        Check("ua: remapping a standard type is reported", Has(rr, PdfUaValidator.CpRoleMapStandard), Why(rr));

        // Tables.
        var t = new Fx(); var td = t.Elem("Document"); var tbl = t.Elem("Table", td); var tr = t.Elem("TR", tbl); t.Elem("TH", tr); t.Elem("TD", tr); t.Finish();
        var tr1 = PdfUaTests.Check(t, dir, "table.pdf");
        Check("ua: a header cell with no Scope is reported once for the table", tr1.Findings.Count(f => f.Checkpoint == PdfUaValidator.CpTableScope) == 1, Why(tr1));
        var ts = new Fx(); var tsd = ts.Elem("Document"); var tst = ts.Elem("Table", tsd); var tstr = ts.Elem("TR", tst); var th = ts.Elem("TH", tstr); ts.Elem("TD", tstr);
        ts.SetAttr(th, "Table", "/Scope", new PdfName("/Column")); ts.Finish();
        Check("ua: a header cell with Scope is fine", !Has(PdfUaTests.Check(ts, dir, "tablescope.pdf"), PdfUaValidator.CpTableScope), "");
        var th2 = new Fx(); var h2d = th2.Elem("Document"); var h2t = th2.Elem("Table", h2d); var h2r = th2.Elem("TR", h2t); th2.Elem("TH", h2r); var h2c = th2.Elem("TD", h2r);
        h2c.Elements["/Headers"] = new PdfArray(th2.Doc); th2.Finish();
        Check("ua: a table that uses Headers needs no Scope", !Has(PdfUaTests.Check(th2, dir, "tableheaders.pdf"), PdfUaValidator.CpTableScope), "");
        var badT = new Fx(); var btd = badT.Elem("Document"); var bt = badT.Elem("Table", btd); badT.Elem("TD", bt); var loneTr = badT.Elem("TR", btd); badT.Elem("P", loneTr); badT.Finish();
        var btr = PdfUaTests.Check(badT, dir, "badtable.pdf");
        Check("ua: a cell directly in a table, a row outside a table and a paragraph in a row are reported", btr.Findings.Count(f => f.Checkpoint == PdfUaValidator.CpTagMisuse) >= 3, Why(btr));

        // Lists and tables of contents.
        var l = new Fx(); var ld = l.Elem("Document"); var list = l.Elem("L", ld); var li = l.Elem("LI", list); l.Elem("Lbl", li); l.Elem("LBody", li); l.Finish();
        Check("ua: a well-formed list passes", !Has(PdfUaTests.Check(l, dir, "list.pdf"), PdfUaValidator.CpTagMisuse), "");
        var bl = new Fx(); var bld = bl.Elem("Document"); var blist = bl.Elem("L", bld); bl.Elem("P", blist); var bli = bl.Elem("LI", blist); bl.Elem("Span", bli); bl.Elem("LBody", bld); bl.Finish();
        Check("ua: a paragraph in a list, a span in a list item and a list body outside an item are reported", PdfUaTests.Check(bl, dir, "badlist.pdf").Findings.Count(f => f.Checkpoint == PdfUaValidator.CpTagMisuse) >= 3, "");
        var toc = new Fx(); var tcd = toc.Elem("Document"); var tc = toc.Elem("TOC", tcd); toc.Elem("P", tc); toc.Elem("TOCI", tcd); toc.Finish();
        Check("ua: a paragraph in a table of contents and an entry outside it are reported", PdfUaTests.Check(toc, dir, "toc.pdf").Findings.Count(f => f.Checkpoint == PdfUaValidator.CpTagMisuse) >= 2, "");
    }

    // ── Page content ────────────────────────────────────────────────────────────────────

    private static void Content(Action<string, bool, string> Check, string dir)
    {
        // Untagged text: a font that is embedded and has a Unicode map, so the only fault is the tagging.
        var u = new Fx(); var d = u.Elem("Document"); var p = u.Elem("P", d, mcid: 0);
        u.AddFont(embedded: true, toUnicode: true);
        u.Content("/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Tagged text) Tj ET EMC\nBT /F1 12 Tf 72 650 Td (Loose text here) Tj ET\n");
        u.Finish(p);
        var ur = PdfUaTests.Check(u, dir, "untagged-text.pdf");
        var loose = ur.Findings.FirstOrDefault(f => f.Checkpoint == PdfUaValidator.CpUntagged);
        Check("ua: text outside any tag is reported on its page", loose is { PageIndex: 0 }, Why(ur));
        Check("ua: the untagged text finding carries a box near where the text is",
            loose?.Bounds is { } b && b.Y > 100 && b.Y < 200 && b.W > 20, loose?.Bounds?.ToString() ?? "no bounds");

        // The same text inside an artifact is fine.
        var a = new Fx(); var ad = a.Elem("Document"); var ap = a.Elem("P", ad, mcid: 0);
        a.AddFont(embedded: true, toUnicode: true);
        a.Content("/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (Tagged text) Tj ET EMC\n/Artifact BMC BT /F1 12 Tf 72 650 Td (Page 1) Tj ET EMC\n");
        a.Finish(ap);
        var ar = PdfUaTests.Check(a, dir, "artifact-text.pdf");
        Check("ua: text marked as an artifact is not reported", !Has(ar, PdfUaValidator.CpUntagged) && !Has(ar, PdfUaValidator.CpTreeIntegrity), Why(ar));

        // Integrity: content with an MCID nobody points at, and a structure element pointing at nothing.
        var i = new Fx(); var id = i.Elem("Document"); var ip = i.Elem("P", id, mcid: 0); var ghost = i.Elem("P", id, mcid: 7);
        i.AddFont(embedded: true, toUnicode: true);
        i.Content("/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (One) Tj ET EMC\n/P <</MCID 3>> BDC BT /F1 12 Tf 72 650 Td (Two) Tj ET EMC\n");
        i.Finish(ip);
        var ir = PdfUaTests.Check(i, dir, "integrity.pdf");
        Check("ua: tagged content that no element points to is reported", ir.Findings.Any(f => f.Checkpoint == PdfUaValidator.CpTreeIntegrity && f.Message.Contains("MCID 3")), Why(ir));
        Check("ua: an element pointing at content that is not on the page is reported", ir.Findings.Any(f => f.Checkpoint == PdfUaValidator.CpTreeIntegrity && f.Message.Contains("MCID 7")), Why(ir));

        // An untagged picture-less page of an untagged document is one finding, not one per page.
        var un = new Fx(tagged: false, marked: false);
        un.AddFont(embedded: true, toUnicode: true);
        un.Content("BT /F1 12 Tf 72 700 Td (Loose) Tj ET\n");
        var unr = PdfUaTests.Check(un, dir, "no-tree-text.pdf");
        Check("ua: with no structure tree the content is not reported letter by letter", !Has(unr, PdfUaValidator.CpUntagged) && unr.Findings.Count(f => f.Checkpoint == PdfUaValidator.CpTagged) == 1, Why(unr));
    }

    // ── Annotations ─────────────────────────────────────────────────────────────────────

    private static PdfDictionary Annot(Fx fx, string subtype, double y, int? structParent = null, string? contents = null, int flags = 0)
    {
        var a = fx.New();
        a.Elements["/Type"] = new PdfName("/Annot");
        a.Elements["/Subtype"] = new PdfName("/" + subtype);
        var rect = new PdfArray(fx.Doc);
        foreach (double v in new[] { 100.0, y, 200.0, y + 20 }) rect.Elements.Add(new PdfReal(v));
        a.Elements["/Rect"] = rect;
        if (structParent is not null) a.Elements["/StructParent"] = new PdfInteger(structParent.Value);
        if (contents is not null) a.Elements.SetString("/Contents", contents);
        if (flags != 0) a.Elements["/F"] = new PdfInteger(flags);
        var annots = fx.Page.Elements["/Annots"] as PdfArray ?? new PdfArray(fx.Doc);
        annots.Elements.Add(a.Reference);
        fx.Page.Elements["/Annots"] = annots;
        return a;
    }

    private static void Annotations(Action<string, bool, string> Check, string dir)
    {
        var fx = new Fx(); var d = fx.Elem("Document");
        var link = fx.Elem("Link", d); var form = fx.Elem("Form", d); var para = fx.Elem("P", d);
        Annot(fx, "Link", 600, structParent: 10, contents: "Home");          // in a Link element: fine
        Annot(fx, "Link", 560);                                              // in no element, no Contents
        Annot(fx, "Link", 520, structParent: 11, contents: "x");             // in a P, not a Link
        Annot(fx, "Widget", 480, structParent: 12);                           // in a P, not a Form
        Annot(fx, "Widget", 440, structParent: 13, contents: null);           // in a Form: fine
        Annot(fx, "Text", 400, structParent: 13);                              // no Contents
        Annot(fx, "Popup", 360);                                               // ignored
        Annot(fx, "Link", 320, flags: 2);                                      // hidden: ignored
        fx.Page.Elements["/StructParents"] = new PdfInteger(0);
        var nums = fx.ParentNums;
        nums.Elements.Add(new PdfInteger(0)); nums.Elements.Add(new PdfArray(fx.Doc));
        foreach (var (k, e) in new[] { (10, link), (11, para), (12, para), (13, form) })
        { nums.Elements.Add(new PdfInteger(k)); nums.Elements.Add(e.Reference); }
        var r = PdfUaTests.Check(fx, dir, "annots.pdf");
        var byCp = r.Findings.Where(f => f.Checkpoint.StartsWith("28-")).ToList();
        Check("ua: a link in no structure element is reported, with its box", byCp.Any(f => f.Checkpoint == PdfUaValidator.CpAnnotTree && f.Bounds is { } b && Math.Abs(b.W - 100) < 0.01), Why(r));
        Check("ua: a link inside a paragraph, not a Link element, is reported", byCp.Any(f => f.Checkpoint == PdfUaValidator.CpAnnotLink), Why(r));
        Check("ua: a field inside a paragraph, not a Form element, is reported", byCp.Any(f => f.Checkpoint == PdfUaValidator.CpAnnotForm), Why(r));
        Check("ua: an annotation without Contents is reported", byCp.Count(f => f.Checkpoint == PdfUaValidator.CpAnnotContents) >= 2, Why(r));
        Check("ua: popups and hidden annotations are ignored", byCp.Count(f => f.Checkpoint == PdfUaValidator.CpAnnotTree) == 1, Why(r));
        Check("ua: a page with annotations and no /Tabs /S is reported", byCp.Any(f => f.Checkpoint == PdfUaValidator.CpTabOrder), Why(r));
        var tabbed = new Fx(); tabbed.Elem("Document"); Annot(tabbed, "Text", 400, contents: "n"); tabbed.Page.Elements["/Tabs"] = new PdfName("/S");
        Check("ua: /Tabs /S satisfies the tab order check", !PdfUaTests.Check(tabbed, dir, "tabs.pdf").Findings.Any(f => f.Checkpoint == PdfUaValidator.CpTabOrder), "");
    }

    // ── Fonts ───────────────────────────────────────────────────────────────────────────

    private static void Fonts(Action<string, bool, string> Check, string dir)
    {
        var f = new Fx(); f.Elem("Document");
        f.AddFont("F1", embedded: false, toUnicode: true, baseFont: "Helvetica");
        f.AddFont("F2", embedded: true, toUnicode: false, baseFont: "Custom", encoding: false, symbolic: true);
        f.AddFont("F3", embedded: true, toUnicode: true, baseFont: "Good");
        var r = PdfUaTests.Check(f, dir, "fonts.pdf");
        Check("ua: a font that is not embedded is reported by name", r.Findings.Any(x => x.Checkpoint == PdfUaValidator.CpFontEmbedded && x.Message.Contains("Helvetica")), Why(r));
        Check("ua: a font with no Unicode mapping is reported", r.Findings.Any(x => x.Checkpoint == PdfUaValidator.CpUnicode && x.Message.Contains("Custom")), Why(r));
        Check("ua: an embedded font with a Unicode map is fine", !r.Findings.Any(x => x.Message.Contains("\"Good\"")), Why(r));
        Check("ua: an embedded font with a standard encoding is fine", !r.Findings.Any(x => x.Checkpoint == PdfUaValidator.CpUnicode && x.Message.Contains("Helvetica")), Why(r));
    }

    // ── JSON ────────────────────────────────────────────────────────────────────────────

    private static void Json(Action<string, bool, string> Check, string dir)
    {
        var fx = Clean(figureAlt: false);
        string path = fx.Save(dir, "json.pdf");
        var report = PdfUaValidator.Check(path);
        string json = report.ToJson(path);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Check("ua json: it is valid JSON naming the file and the standard", root.GetProperty("standard").GetString() == "PDF/UA-1" && root.GetProperty("file").GetString() == path, json[..Math.Min(200, json.Length)]);
        Check("ua json: the verdict is a stable key", root.GetProperty("verdict").GetString() == "failed", root.GetProperty("verdict").GetString() ?? "");
        Check("ua json: findings carry checkpoint, severity, page and fixable", root.GetProperty("findings").EnumerateArray().Any(f => f.GetProperty("checkpoint").GetString() == "13-004" && f.GetProperty("severity").GetString() == "error" && f.GetProperty("page").GetInt32() == 1 && f.GetProperty("object").ValueKind == System.Text.Json.JsonValueKind.Number), "");
        Check("ua json: manual checks and a not-a-conformance-claim note are included", root.GetProperty("manualChecks").GetArrayLength() >= 6 && root.GetProperty("note").GetString()!.Contains("not a statement"), "");
        Check("ua json: two runs on one file are byte for byte the same", PdfUaValidator.Check(path).ToJson(path) == json, "");
        Check("ua json: quoting handles quotes, backslashes and control characters", PdfUaReport.Quote("a\"b\\c\n\u0001") == "\"a\\\"b\\\\c\\n\\u0001\"", PdfUaReport.Quote("a\"b\\c\n\u0001"));

        var ua2 = new Fx(xmpPart: 2); ua2.Elem("Document"); ua2.Finish();
        using var d2 = System.Text.Json.JsonDocument.Parse(PdfUaValidator.Check(ua2.Save(dir, "json2.pdf")).ToJson());
        Check("ua json: a PDF/UA-2 file names PDF/UA-2 and is partly checked", d2.RootElement.GetProperty("standard").GetString() == "PDF/UA-2" && d2.RootElement.GetProperty("verdict").GetString() is "partlyChecked" or "failed", "");
    }

    // ── Fixes ───────────────────────────────────────────────────────────────────────────

    private static void Fixes(Action<string, bool, string> Check, string dir)
    {
        // A tagged document missing all four fixable things.
        var fx = new Fx(lang: null, title: null, displayTitle: false, marked: false, xmpPart: 0);
        var d = fx.Elem("Document"); var fig = fx.Elem("Figure", d, 0, alt: "Square"); fx.Content(fx.TaggedSquare("Figure", 0)); fx.Finish(fig);
        string path = fx.Save(dir, "tofix.pdf");
        var before = PdfUaValidator.Check(path);
        var fixes = before.Findings.Where(f => f.Fix != PdfUaFix.None).Select(f => f.Fix).Distinct().ToList();
        Check("ua fix: the four fixable faults are offered", fixes.Count == 4 && fixes.Contains(PdfUaFix.SetLanguage) && fixes.Contains(PdfUaFix.SetTitle) && fixes.Contains(PdfUaFix.SetDisplayDocTitle) && fixes.Contains(PdfUaFix.MarkTagged), string.Join(",", fixes));

        using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            Check("ua fix: a bad language is refused", !PdfUaFixes.Apply(doc, PdfUaFix.SetLanguage, "not valid!") && !PdfUaFixes.Apply(doc, PdfUaFix.SetLanguage, ""), "");
            Check("ua fix: an empty title is refused", !PdfUaFixes.Apply(doc, PdfUaFix.SetTitle, "  ") && !PdfUaFixes.Apply(doc, PdfUaFix.SetTitle, null), "");
            Check("ua fix: set language", PdfUaFixes.Apply(doc, PdfUaFix.SetLanguage, "fr-CA"), "");
            string p1 = Path.Combine(dir, "fixed-lang.pdf"); doc.Save(p1);
            var r1 = PdfUaValidator.Check(p1);
            Check("ua fix: the language fault is gone and the others remain", !r1.Findings.Any(f => f.Fix == PdfUaFix.SetLanguage) && r1.Findings.Any(f => f.Fix == PdfUaFix.SetTitle), Why(r1));
            using var re1 = PdfReader.Open(p1, PdfDocumentOpenMode.Import);
            Check("ua fix: the catalog holds the language", re1.Internals.Catalog.Elements.GetString("/Lang") == "fr-CA", "");
        }
        using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            Check("ua fix: set title", PdfUaFixes.Apply(doc, PdfUaFix.SetTitle, "Annual report"), "");
            string p = Path.Combine(dir, "fixed-title.pdf"); doc.Save(p);
            var r = PdfUaValidator.Check(p);
            Check("ua fix: the title fault is gone", !r.Findings.Any(f => f.Fix == PdfUaFix.SetTitle), Why(r));
        }
        using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            Check("ua fix: set DisplayDocTitle", PdfUaFixes.Apply(doc, PdfUaFix.SetDisplayDocTitle), "");
            string p = Path.Combine(dir, "fixed-display.pdf"); doc.Save(p);
            Check("ua fix: the window-title fault is gone", !PdfUaValidator.Check(p).Findings.Any(f => f.Fix == PdfUaFix.SetDisplayDocTitle), "");
        }
        using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            Check("ua fix: mark as tagged", PdfUaFixes.Apply(doc, PdfUaFix.MarkTagged), "");
            string p = Path.Combine(dir, "fixed-marked.pdf"); doc.Save(p);
            Check("ua fix: the tagged-flag fault is gone", !PdfUaValidator.Check(p).Findings.Any(f => f.Fix == PdfUaFix.MarkTagged), "");
        }
        using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            PdfUaFixes.Apply(doc, PdfUaFix.SetLanguage, "en"); PdfUaFixes.Apply(doc, PdfUaFix.SetTitle, "T");
            PdfUaFixes.Apply(doc, PdfUaFix.SetDisplayDocTitle); PdfUaFixes.Apply(doc, PdfUaFix.MarkTagged);
            string p = Path.Combine(dir, "fixed-all.pdf"); doc.Save(p);
            var r = PdfUaValidator.Check(p);
            Check("ua fix: all four fixes together leave no error", !r.Findings.Any(f => f.Severity == PdfUaSeverity.Error), Why(r));
        }

        // Marking an untagged document as tagged is refused: TDPdf does not add tags.
        using (var doc = PdfReader.Open(new Fx(tagged: false, marked: false).Save(dir, "notree.pdf"), PdfDocumentOpenMode.Modify))
            Check("ua fix: a document with no structure tree is not marked as tagged", !PdfUaFixes.Apply(doc, PdfUaFix.MarkTagged) && doc.Internals.Catalog.Elements["/MarkInfo"] is null, "");

        // The fix keeps an existing ViewerPreferences dictionary's other entries.
        var vp = new Fx(displayTitle: false);
        var existing = new PdfDictionary(vp.Doc); existing.Elements["/HideToolbar"] = new PdfBoolean(true); vp.Cat.Elements["/ViewerPreferences"] = existing;
        PdfUaFixes.Apply(vp.Doc, PdfUaFix.SetDisplayDocTitle);
        var vpd = (PdfDictionary)vp.Cat.Elements["/ViewerPreferences"]!;
        Check("ua fix: DisplayDocTitle keeps other viewer preferences", vpd.Elements.GetBoolean("/DisplayDocTitle") && vpd.Elements.GetBoolean("/HideToolbar"), "");

        Check("ua fix: only language and title need a value", PdfUaFixes.NeedsValue(PdfUaFix.SetLanguage) && PdfUaFixes.NeedsValue(PdfUaFix.SetTitle) && !PdfUaFixes.NeedsValue(PdfUaFix.MarkTagged) && !PdfUaFixes.NeedsValue(PdfUaFix.SetDisplayDocTitle), "");
        Check("ua fix: the suggested language is a valid tag", PdfUaValidator.IsLanguageTag(PdfUaFixes.SuggestLanguage()), PdfUaFixes.SuggestLanguage());
        Check("ua fix: the suggested title is the file name, tidied", PdfUaFixes.SuggestTitle("annual_report-2026.pdf") == "annual report 2026", PdfUaFixes.SuggestTitle("annual_report-2026.pdf"));
    }

    // ── Hostile input ───────────────────────────────────────────────────────────────────

    private static void Hostile(Action<string, bool, string> Check, string dir)
    {
        bool threw = false;
        string junk = Path.Combine(dir, "junk.pdf");
        File.WriteAllText(junk, "this is not a pdf");
        try { PdfUaValidator.Check(junk); } catch (Exception) { threw = true; }
        Check("ua: a file that is not a PDF throws, for the caller to report as unreadable", threw, "");

        // A structure tree nested far deeper than any real document stops at the cap, partly checked, no crash.
        var deep = new Fx(); var parent = deep.Elem("Document");
        for (int i = 0; i < PdfUaValidator.MaxStructDepth + 40; i++) parent = deep.Elem("Div", parent);
        deep.Finish();
        PdfUaReport dr;
        try { dr = PdfUaTests.Check(deep, dir, "deep.pdf"); Check("ua: an absurdly deep tree is cut off and reported as partly checked", dr.Coverage.Any(c => c.Contains("Not checked: the structure tree")), dr.Verdict + " " + string.Join("|", dr.Coverage)); }
        catch (Exception ex) { Check("ua: an absurdly deep tree is cut off and reported as partly checked", false, ex.GetType().Name + ex.Message); }

        // A cycle in the tree does not loop.
        var cyc = new Fx(); var c1 = cyc.Elem("Document"); var c2 = cyc.Elem("Div", c1);
        cyc.AddKid(c2, c1);
        cyc.Finish();
        try { PdfUaTests.Check(cyc, dir, "cycle.pdf"); Check("ua: a cyclic structure tree does not loop", true, ""); }
        catch (Exception ex) { Check("ua: a cyclic structure tree does not loop", false, ex.Message); }

        // Cancellation stops a check.
        using var cts = new CancellationTokenSource(); cts.Cancel();
        bool cancelled = false;
        try { PdfUaValidator.Check(Path.Combine(dir, "clean.pdf"), cts.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check("ua: a cancelled check stops", cancelled, "");
    }

    // ── Command line ────────────────────────────────────────────────────────────────────

    private static void Cli(Action<string, bool, string> Check, string dir)
    {
        string cdir = Path.Combine(dir, "cli");
        Directory.CreateDirectory(cdir);
        string good = Clean().Save(cdir, "good.pdf");
        string bad = Clean(figureAlt: false).Save(cdir, "bad.pdf");
        string junk = Path.Combine(cdir, "junk.pdf");
        File.WriteAllText(junk, "not a pdf");

        int Run(string[] paths, bool json, out string output)
        {
            var sw = new StringWriter();
            int code = PdfUaCli.Run(paths, json, sw);
            output = sw.ToString();
            return code;
        }

        Check("ua cli: a passing file exits 0", Run(new[] { good }, false, out var o1) == 0 && o1.Contains("Passed TDPdf's machine checks against PDF/UA-1"), o1);
        Check("ua cli: a failing file exits 3 and prints the checkpoint and page", Run(new[] { bad }, false, out var o2) == 3 && o2.Contains("13-004") && o2.Contains("(page 1)"), o2);
        Check("ua cli: an unreadable file exits 1", Run(new[] { junk }, false, out var o3) == 1 && o3.Contains("could not be read"), o3);
        Check("ua cli: a missing path exits 2", Run(new[] { Path.Combine(cdir, "nope.pdf") }, false, out _) == 2, "");
        Check("ua cli: no arguments exits 2 with usage", Run(Array.Empty<string>(), false, out var o4) == 2 && o4.Contains("Usage"), o4);
        Check("ua cli: an empty folder exits 2", Run(new[] { Directory.CreateDirectory(Path.Combine(cdir, "empty")).FullName }, false, out _) == 2, "");

        Check("ua cli: --json for one file is one object naming the standard", Run(new[] { bad }, true, out var j1) == 3 && j1.TrimStart().StartsWith("{") && j1.Contains("\"standard\":\"PDF/UA-1\""), j1);
        string folder = Path.Combine(cdir, "folder"); Directory.CreateDirectory(folder);
        File.Copy(good, Path.Combine(folder, "a.pdf")); File.Copy(bad, Path.Combine(folder, "b.pdf"));
        int code = Run(new[] { folder }, true, out var j2);
        using var arr = System.Text.Json.JsonDocument.Parse(j2);
        Check("ua cli: a folder is checked file by file into a JSON array, worst result wins the exit code",
            code == 3 && arr.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array && arr.RootElement.GetArrayLength() == 2
            && arr.RootElement[0].GetProperty("file").GetString()!.EndsWith("a.pdf") && arr.RootElement[1].GetProperty("verdict").GetString() == "failed", j2);
        File.Copy(junk, Path.Combine(folder, "c.pdf"));
        code = Run(new[] { folder }, true, out var j3);
        using var arr3 = System.Text.Json.JsonDocument.Parse(j3);
        Check("ua cli: an unreadable file in a folder gives exit 1 and an error entry", code == 1 && arr3.RootElement.EnumerateArray().Any(e => e.TryGetProperty("error", out _)), j3);
        Check("ua cli: several files named directly give an array", Run(new[] { good, good }, true, out var j4) == 0 && j4.TrimStart().StartsWith("["), j4);
    }

    private static T Also<T>(this T value, Action<T> act) { act(value); return value; }
}
