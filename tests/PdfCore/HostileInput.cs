using System.Text;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Filters;
using PdfSharpCore.Pdf.IO;
using TDPdf.Services;

/// <summary>
/// Crafted-PDF guards (cleanup 2.3.0.0): a .NET StackOverflowException cannot be caught, so one
/// hostile file used to be able to kill every open tab. Each case here would have crashed the process
/// or hung before the guard; the test run surviving at all is the main assertion.
/// </summary>
internal static class HostileInput
{
    /// <summary>A PDF whose object N (1-based) has the body objs[N-1]; object 1 is the catalog.</summary>
    private static byte[] Build(params string[] objs)
    {
        var ms = new MemoryStream();
        void Ascii(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        Ascii("%PDF-1.4\n");
        var offsets = new long[objs.Length + 1];
        for (int i = 0; i < objs.Length; i++)
        {
            offsets[i + 1] = ms.Position;
            Ascii($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        long xref = ms.Position;
        Ascii($"xref\n0 {objs.Length + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= objs.Length; i++) Ascii($"{offsets[i]:D10} 00000 n \n");
        Ascii($"trailer\n<< /Size {objs.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static PdfDocument Open(byte[] b) => PdfReader.Open(new MemoryStream(b), PdfDocumentOpenMode.Import);

    public static void Run(Action<string, bool, string> check, string tmp)
    {
        void Check(string label, bool ok, string detail = "") => check(label, ok, detail);
        Console.WriteLine("\nHostile-input guards");

        // Page tree: /Kids pointing at the node itself, and a two-node loop. Both used to recurse forever.
        {
            var self = Build("<< /Type /Catalog /Pages 2 0 R >>",
                             "<< /Type /Pages /Kids [2 0 R 3 0 R] /Count 1 >>",
                             "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] >>");
            try { using var d = Open(self); Check("page tree: self-referencing /Kids terminates, real page kept", d.PageCount == 1, $"pages={d.PageCount}"); }
            catch (Exception ex) { Check("page tree: self-referencing /Kids terminates", false, ex.GetType().Name + ": " + ex.Message); }

            var loop = Build("<< /Type /Catalog /Pages 2 0 R >>",
                             "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 1 >>",
                             "<< /Type /Pages /Kids [2 0 R] /Parent 2 0 R /Count 0 >>",
                             "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] >>");
            try { using var d = Open(loop); Check("page tree: two-node /Kids loop terminates", d.PageCount == 1, $"pages={d.PageCount}"); }
            catch (Exception ex) { Check("page tree: two-node /Kids loop terminates", false, ex.GetType().Name + ": " + ex.Message); }

            // A very deep (non-cyclic) chain of /Pages nodes hits the depth cap, not the stack.
            var objs = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>" };
            const int depth = 3000;
            for (int i = 0; i < depth; i++) objs.Add($"<< /Type /Pages /Kids [{i + 3} 0 R] /Count 1 >>");
            objs.Add("<< /Type /Page /MediaBox [0 0 200 300] >>");
            try { using var d = Open(Build(objs.ToArray())); Check("page tree: a 3000-deep chain does not overflow the stack", d.PageCount >= 0, $"pages={d.PageCount}"); }
            catch (Exception ex) { Check("page tree: a 3000-deep chain does not overflow the stack", true, "refused: " + ex.GetType().Name); }
        }

        // Outlines: /Next cycle, /First pointing at itself, /First pointing at the root.
        {
            string pages = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
            string page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] >>";

            var next = Build("<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>", pages, page,
                             "<< /Type /Outlines /First 5 0 R /Last 6 0 R /Count 2 >>",
                             "<< /Title (A) /Parent 4 0 R /Next 6 0 R >>",
                             "<< /Title (B) /Parent 4 0 R /Next 5 0 R >>");
            try { using var d = Open(next); int n = d.Outlines.Count; Check("outline: /Next cycle terminates with each item once", n == 2, $"items={n}"); }
            catch (Exception ex) { Check("outline: /Next cycle terminates", false, ex.GetType().Name + ": " + ex.Message); }

            var selfFirst = Build("<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>", pages, page,
                                  "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>",
                                  "<< /Title (A) /Parent 4 0 R /First 5 0 R /Last 5 0 R /Count 1 >>");
            try { using var d = Open(selfFirst); int n = d.Outlines.Count; Check("outline: /First pointing at itself terminates", n == 1, $"items={n}"); }
            catch (Exception ex) { Check("outline: /First pointing at itself terminates", false, ex.GetType().Name + ": " + ex.Message); }

            var rootFirst = Build("<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>", pages, page,
                                  "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>",
                                  "<< /Title (A) /Parent 4 0 R /First 4 0 R /Last 4 0 R /Count 1 >>");
            try { using var d = Open(rootFirst); int n = d.Outlines.Count; Check("outline: /First pointing at the root terminates", n == 1, $"items={n}"); }
            catch (Exception ex) { Check("outline: /First pointing at the root terminates", false, ex.GetType().Name + ": " + ex.Message); }

            var ok = Build("<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>", pages, page,
                           "<< /Type /Outlines /First 5 0 R /Last 6 0 R /Count 2 >>",
                           "<< /Title (A) /Parent 4 0 R /Next 6 0 R /First 7 0 R /Last 7 0 R /Count 1 >>",
                           "<< /Title (B) /Parent 4 0 R /Prev 5 0 R >>",
                           "<< /Title (A1) /Parent 5 0 R >>");
            try { using var d = Open(ok); Check("outline: a normal two-level tree still loads", d.Outlines.Count == 2 && d.Outlines[0].Outlines.Count == 1, $"items={d.Outlines.Count}"); }
            catch (Exception ex) { Check("outline: a normal two-level tree still loads", false, ex.GetType().Name + ": " + ex.Message); }
        }

        // Parser nesting: ~10k nested arrays/dictionaries must throw a catchable parse error.
        {
            const int levels = 10_000;
            string arrays = new string('[', levels) + new string(']', levels);
            var deepArr = Build("<< /Type /Catalog /Pages 2 0 R /Junk 4 0 R >>",
                                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] >>", arrays);
            try { using var d = Open(deepArr); Check("parser: 10,000 nested arrays rejected with a catchable error", false, "opened"); }
            catch (Exception) { Check("parser: 10,000 nested arrays rejected with a catchable error", true); }

            var sb = new StringBuilder();
            for (int i = 0; i < levels; i++) sb.Append("<< /A ");
            sb.Append("0");
            for (int i = 0; i < levels; i++) sb.Append(" >>");
            var deepDict = Build("<< /Type /Catalog /Pages 2 0 R /Junk 4 0 R >>",
                                 "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                                 "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] >>", sb.ToString());
            try { using var d = Open(deepDict); Check("parser: 10,000 nested dictionaries rejected with a catchable error", false, "opened"); }
            catch (Exception) { Check("parser: 10,000 nested dictionaries rejected with a catchable error", true); }

            string fine = new string('[', 200) + new string(']', 200);
            var okNest = Build("<< /Type /Catalog /Pages 2 0 R /Junk 4 0 R >>",
                               "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                               "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] >>", fine);
            try { using var d = Open(okNest); Check("parser: 200 levels of nesting still opens", d.PageCount == 1); }
            catch (Exception ex) { Check("parser: 200 levels of nesting still opens", false, ex.Message); }
        }

        // Flate bomb: a small stream that inflates past the cap is refused.
        {
            var raw = new byte[8 * 1024 * 1024];
            var fd = new FlateDecode();
            byte[] packed = fd.Encode(raw);
            bool refused = false;
            try { fd.Decode(packed, new FilterParms(null), 1 * 1024 * 1024); }
            catch (InvalidOperationException) { refused = true; }
            Check("flate: output past the cap throws a catchable exception", refused, $"{packed.Length} bytes in, {raw.Length} out");
            Check("flate: output under the cap decodes intact", fd.Decode(packed, new FilterParms(null), 16 * 1024 * 1024).Length == raw.Length);
            Check("flate: the production cap is 512 MB", FlateDecode.MaxDecodedLength == 512L * 1024 * 1024);
        }

        // PdfItems
        {
            var doc = new PdfDocument();
            var d = new PdfDictionary(doc); doc.Internals.AddObject(d);
            Check("PdfItems.Deref follows a reference", ReferenceEquals(PdfItems.Deref(d.Reference), d));
            Check("PdfItems.Deref leaves a direct item alone", PdfItems.Deref(new PdfInteger(3)) is PdfInteger { Value: 3 });
            Check("PdfItems.Deref(null) is null", PdfItems.Deref(null) is null);
            Check("PdfItems.ObjectNumber of a reference", PdfItems.ObjectNumber(d.Reference) == d.Reference.ObjectNumber && d.Reference.ObjectNumber > 0);
            Check("PdfItems.ObjectNumber of a direct item or null is -1", PdfItems.ObjectNumber(d) == -1 && PdfItems.ObjectNumber(null) == -1);

            var a = new PdfDictionary(doc); doc.Internals.AddObject(a);
            var b = new PdfDictionary(doc); doc.Internals.AddObject(b);
            a.Elements["/Parent"] = b.Reference; b.Elements["/Parent"] = a.Reference;
            var chain = PdfItems.Ancestors(a).ToList();
            Check("PdfItems.Ancestors stops on a /Parent cycle", chain.Count == 2 && ReferenceEquals(chain[0], a) && ReferenceEquals(chain[1], b), $"count={chain.Count}");
            var self = new PdfDictionary(doc); doc.Internals.AddObject(self);
            self.Elements["/Parent"] = self.Reference;
            Check("PdfItems.Ancestors stops on a self-parent", PdfItems.Ancestors(self).Count() == 1);
            PdfDictionary node = new PdfDictionary(doc); doc.Internals.AddObject(node);
            for (int i = 0; i < 500; i++)
            {
                var child = new PdfDictionary(doc); doc.Internals.AddObject(child);
                child.Elements["/Parent"] = node.Reference; node = child;
            }
            Check("PdfItems.Ancestors caps a very long chain", PdfItems.Ancestors(node).Count() == PdfItems.MaxAncestorDepth);
        }

        // Named destinations: a self-referencing /Kids cycle still resolves, and still terminates when the name is absent.
        {
            var doc = new PdfDocument();
            var page = doc.AddPage();
            var dest = new PdfArray(doc); dest.Elements.Add(page.Reference); dest.Elements.Add(new PdfName("/Fit"));

            var root = new PdfDictionary(doc); doc.Internals.AddObject(root);
            var leaf = new PdfDictionary(doc); doc.Internals.AddObject(leaf);
            var names = new PdfArray(doc);
            names.Elements.Add(new PdfString("chapter1")); names.Elements.Add(dest);
            leaf.Elements["/Names"] = names;
            var kids = new PdfArray(doc);
            kids.Elements.Add(root.Reference);   // cycle back to the root
            kids.Elements.Add(leaf.Reference);
            root.Elements["/Kids"] = kids;
            var namesDict = new PdfDictionary(doc); namesDict.Elements["/Dests"] = root.Reference;
            doc.Internals.Catalog.Elements["/Names"] = namesDict;

            Check("named dest: found through a /Kids cycle", ReferenceEquals(PdfNamedDestinations.Resolve(doc, new PdfString("chapter1")), dest));
            Check("named dest: a missing name returns null instead of looping", PdfNamedDestinations.Resolve(doc, new PdfString("nope")) is null);
            Check("named dest: a PdfName key resolves too", ReferenceEquals(PdfNamedDestinations.Resolve(doc, new PdfName("/chapter1")), dest));
            var map = PdfNamedDestinations.BuildMap(doc);
            Check("named dest: BuildMap over a cycle maps the name to its page", map.Count == 1 && map["chapter1"] == 0, $"count={map.Count}");

            // Deep acyclic tree is capped, not recursed to the stack's end.
            var cur = new PdfDictionary(doc); doc.Internals.AddObject(cur);
            var deepRoot = cur;
            for (int i = 0; i < 5000; i++)
            {
                var next = new PdfDictionary(doc); doc.Internals.AddObject(next);
                var k = new PdfArray(doc); k.Elements.Add(next.Reference);
                cur.Elements["/Kids"] = k; cur = next;
            }
            PdfNamedDestinations.WalkNameTree(deepRoot, (_, _) => false);
            Check("named dest: a 5000-deep name tree walk terminates", true);

            // Link rewrite after a merge still works.
            var link = new PdfDictionary(doc); doc.Internals.AddObject(link);
            link.Elements["/Subtype"] = new PdfName("/Link");
            link.Elements["/Dest"] = new PdfString("chapter1");
            var annots = new PdfArray(doc); annots.Elements.Add(link.Reference);
            page.Elements["/Annots"] = annots;
            PdfNamedDestinations.RewriteLinks(doc, 0, map);
            Check("named dest: a named link is rewritten to an explicit [page /Fit]",
                  link.Elements["/Dest"] is PdfArray { Elements.Count: 2 } rw && ReferenceEquals(PdfItems.Deref(rw.Elements[0]), page));
        }

        // Zip limits
        {
            Check("zip: a modest archive is accepted", ZipLimits.Refusal(10, 1_000_000) is null);
            Check("zip: too many entries is refused", ZipLimits.Refusal(10_001, 1) is { } r1 && r1.Contains("entries"));
            Check("zip: too many bytes is refused", ZipLimits.Refusal(1, ZipLimits.MaxTotalBytes + 1) is { } r2 && r2.Contains("GB"));
            Check("zip: exactly at the limits is accepted", ZipLimits.Refusal(ZipLimits.MaxEntries, ZipLimits.MaxTotalBytes) is null);

            string zp = Path.Combine(tmp, "limits.zip");
            if (File.Exists(zp)) File.Delete(zp);
            using (var z = System.IO.Compression.ZipFile.Open(zp, System.IO.Compression.ZipArchiveMode.Create))
                for (int i = 0; i < 5; i++)
                    using (var w = new StreamWriter(z.CreateEntry($"f{i}.txt").Open())) w.Write(new string('x', 1000));
            Check("zip: a real archive passes the default limits", ZipLimits.Refusal(zp) is null);
            Check("zip: a real archive over a tiny entry cap is refused", ZipLimits.Refusal(zp, maxEntries: 3) is not null);
            Check("zip: a real archive over a tiny byte cap is refused", ZipLimits.Refusal(zp, maxTotalBytes: 4000) is not null);
            Check("zip: an unreadable archive is left to extraction to report", ZipLimits.Refusal(Path.Combine(tmp, "missing.zip")) is null);
        }
    }
}
