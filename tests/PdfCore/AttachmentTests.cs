using System.Text;
using PdfSharpCore.Pdf;
using TDPdf.Services;

/// <summary>
/// Extract file attachments (#224): the name rules that keep a PDF-supplied name from escaping its
/// folder or becoming a device, the risky-type list, and the bounded name-tree walk.
/// </summary>
internal static class AttachmentTests
{
    private static PdfDictionary Indirect(PdfDocument doc, Action<PdfDictionary> fill)
    {
        var d = new PdfDictionary(doc);
        doc.Internals.AddObject(d);
        fill(d);
        return d;
    }

    private static PdfDictionary FileSpec(PdfDocument doc, string name, string payload, string? desc = null, string? modDate = null) => Indirect(doc, fs =>
    {
        fs.Elements["/Type"] = new PdfName("/Filespec");
        fs.Elements["/F"] = new PdfString(name);
        if (desc is not null) fs.Elements["/Desc"] = new PdfString(desc);
        var ef = Indirect(doc, s =>
        {
            s.Elements["/Type"] = new PdfName("/EmbeddedFile");
            if (modDate is not null)
            {
                var pars = new PdfDictionary(doc);
                pars.Elements["/ModDate"] = new PdfString(modDate);
                pars.Elements["/Size"] = new PdfInteger(payload.Length);
                s.Elements["/Params"] = pars;
            }
            s.CreateStream(Encoding.ASCII.GetBytes(payload));
        });
        var efd = new PdfDictionary(doc);
        efd.Elements["/F"] = ef.Reference;
        fs.Elements["/EF"] = efd;
    });

    private static PdfDictionary Node(PdfDocument doc, Action<PdfDictionary> fill) => Indirect(doc, fill);

    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nAttachments (AttachmentNames, PdfAttachments)");

        // ── names ──
        foreach (var bad in new[] { "CON", "con.txt", "NUL", "Aux.pdf", "COM1", "com9.log", "LPT3.txt", "PRN" })
        {
            var (n, changed) = AttachmentNames.Sanitise(bad);
            Check($"reserved name {bad} is not kept as is", changed && n.StartsWith('_') && n.EndsWith(bad, StringComparison.Ordinal), n);
        }
        Check("COM10 is an ordinary name", AttachmentNames.Sanitise("COM10.txt") is ("COM10.txt", false), "");
        Check("a plain name is unchanged", AttachmentNames.Sanitise("report 2024.pdf") is ("report 2024.pdf", false), "");
        Check("trailing dots and spaces are dropped", AttachmentNames.Sanitise("note.txt. . ").Name == "note.txt", AttachmentNames.Sanitise("note.txt. . ").Name);
        Check("an alternate stream suffix cannot survive", !AttachmentNames.Sanitise("a.txt:evil.exe").Name.Contains(':'), AttachmentNames.Sanitise("a.txt:evil.exe").Name);
        Check("a stream-only name is made ordinary", !AttachmentNames.Sanitise(":secret").Name.Contains(':'), "");
        Check("a Windows path keeps only the last part", AttachmentNames.Sanitise(@"C:\Users\x\..\evil.dll").Name == "evil.dll", AttachmentNames.Sanitise(@"C:\Users\x\..\evil.dll").Name);
        Check("a traversal path keeps only the last part", AttachmentNames.Sanitise("../../etc/passwd").Name == "passwd", "");
        Check("a trailing separator leaves a generic name", AttachmentNames.Sanitise("folder/").Name == "attachment", "");
        Check("dots only leave a generic name", AttachmentNames.Sanitise("..").Name == "attachment", "");
        Check("empty and null leave a generic name", AttachmentNames.Sanitise("").Name == "attachment" && AttachmentNames.Sanitise(null).Name == "attachment", "");
        Check("control and wildcard characters become underscores", AttachmentNames.Sanitise("a\u0001b*c?.txt").Name == "a_b_c_.txt", AttachmentNames.Sanitise("a\u0001b*c?.txt").Name);
        Check("a right-to-left override is neutralised", AttachmentNames.Sanitise("invoice\u202Etxt.exe").Name == "invoice_txt.exe", "");
        string longName = new string('a', 300) + ".pdf";
        var capped = AttachmentNames.Sanitise(longName).Name;
        Check("a long name is shortened and keeps its extension", capped.Length <= AttachmentNames.MaxNameLength && capped.EndsWith(".pdf"), $"{capped.Length}");

        // ── types ──
        foreach (var risky in new[] { "a.exe", "A.EXE", "x.dll", "x.bat", "x.cmd", "x.ps1", "x.js", "x.vbs", "x.msi", "x.lnk", "x.scr", "x.hta", "x.jar", "x.reg", "x.url", "x.appref-ms", "x.docm", "invoice.pdf.exe", "x.exe ", "x.exe." })
            Check($"{risky} is flagged", AttachmentNames.IsRisky(risky), "");
        foreach (var ok in new[] { "a.pdf", "a.txt", "a.xml", "a.png", "a.docx", "exe", "readme" })
            Check($"{ok} is not flagged", !AttachmentNames.IsRisky(ok), "");

        // ── helpers ──
        Check("zone marker is the Internet zone", AttachmentNames.ZoneIdentifierText() == "[ZoneTransfer]\r\nZoneId=3\r\n", "");
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.txt", "a (2).txt" };
        Check("a free name is kept", AttachmentNames.Unique("b.txt", taken.Contains) == "b.txt", "");
        Check("a taken name gets the next number", AttachmentNames.Unique("a.txt", taken.Contains) == "a (3).txt", "");
        Check("a taken name without an extension", AttachmentNames.Unique("a", n => n == "a") == "a (2)", "");

        // ── dates ──
        Check("a full PDF date reads", AttachmentNames_Date("D:20240315093000Z") == new DateTime(2024, 3, 15, 9, 30, 0, DateTimeKind.Utc), "");
        Check("an offset is applied", AttachmentNames_Date("D:20240315093000+02'00'") == new DateTime(2024, 3, 15, 7, 30, 0, DateTimeKind.Utc), "");
        Check("a year alone reads", AttachmentNames_Date("D:2020") == new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), "");
        Check("garbage is no date", AttachmentNames_Date("yesterday") is null && AttachmentNames_Date("D:2024139999") is null && AttachmentNames_Date(null) is null, "");

        // ── the name-tree walk ──
        {
            var doc = new PdfDocument();
            doc.AddPage();
            var leaf1 = Node(doc, n => n.Elements["/Names"] = new PdfArray(doc, new PdfString("a"), FileSpec(doc, "a.txt", "AAA", "first", "D:20240101000000Z").Reference));
            var leaf2 = Node(doc, n => n.Elements["/Names"] = new PdfArray(doc, new PdfString("b"), FileSpec(doc, "b.bin", "BBBB").Reference,
                                                                           new PdfString("c"), FileSpec(doc, "c.exe", "CCCCC").Reference));
            var root = Node(doc, n => n.Elements["/Kids"] = new PdfArray(doc, leaf1.Reference, leaf2.Reference));
            var names = new PdfDictionary(doc);
            names.Elements["/EmbeddedFiles"] = root.Reference;
            doc.Internals.Catalog.Elements["/Names"] = names;
            var list = PdfAttachments.List(doc);
            Check("a two-level tree lists every file in order", list.Select(e => e.Name).SequenceEqual(["a.txt", "b.bin", "c.exe"]), string.Join(",", list.Select(e => e.Name)));
            Check("description, size and date are read", list[0].Description == "first" && list[0].Size == 3 && list[0].Modified == new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), "");
            Check("a file with no declared size reports its stored size", list[1].Size == 4, $"{list[1].Size}");
            Check("the bytes come back", Encoding.ASCII.GetString(list[2].ReadBytes()) == "CCCCC", "");
            Check("tree entries are marked as the document's", list.All(e => e.Source == "Document"), "");
        }

        // A cycle (a kid that names its parent) ends instead of looping.
        {
            var doc = new PdfDocument();
            doc.AddPage();
            var a = Node(doc, _ => { });
            var b = Node(doc, _ => { });
            a.Elements["/Kids"] = new PdfArray(doc, b.Reference);
            b.Elements["/Kids"] = new PdfArray(doc, a.Reference);
            a.Elements["/Names"] = new PdfArray(doc, new PdfString("x"), FileSpec(doc, "x.txt", "X").Reference);
            var names = new PdfDictionary(doc);
            names.Elements["/EmbeddedFiles"] = a.Reference;
            doc.Internals.Catalog.Elements["/Names"] = names;
            var list = PdfAttachments.List(doc);
            Check("a cyclic tree lists its file once and stops", list.Count == 1, $"{list.Count}");
        }

        // Nesting deeper than the cap is not followed past it.
        {
            var doc = new PdfDocument();
            doc.AddPage();
            PdfDictionary? child = null;
            for (int depth = 0; depth < PdfAttachments.MaxDepth + 5; depth++)
            {
                var prev = child;
                int d = depth;
                child = Node(doc, n =>
                {
                    if (prev is not null) n.Elements["/Kids"] = new PdfArray(doc, prev.Reference);
                    else n.Elements["/Names"] = new PdfArray(doc, new PdfString("deep"), FileSpec(doc, "deep.txt", "D").Reference);
                });
            }
            var names = new PdfDictionary(doc);
            names.Elements["/EmbeddedFiles"] = child!.Reference;
            doc.Internals.Catalog.Elements["/Names"] = names;
            Check("a tree nested past the depth cap is cut off", PdfAttachments.List(doc).Count == 0, "");
        }

        // A file-attachment annotation, and the same file reached by both routes listed once.
        {
            var doc = new PdfDocument();
            doc.AddPage();
            var page2 = doc.AddPage();
            var shared = FileSpec(doc, "both.txt", "BOTH");
            var annotOnly = FileSpec(doc, "note.txt", "NOTE");
            var a1 = Node(doc, a => { a.Elements["/Type"] = new PdfName("/Annot"); a.Elements["/Subtype"] = new PdfName("/FileAttachment"); a.Elements["/FS"] = annotOnly.Reference; a.Elements["/Contents"] = new PdfString("see this"); });
            var a2 = Node(doc, a => { a.Elements["/Type"] = new PdfName("/Annot"); a.Elements["/Subtype"] = new PdfName("/FileAttachment"); a.Elements["/FS"] = shared.Reference; });
            page2.Elements["/Annots"] = new PdfArray(doc, a1.Reference, a2.Reference);
            var leaf = Node(doc, n => n.Elements["/Names"] = new PdfArray(doc, new PdfString("both"), shared.Reference));
            var names = new PdfDictionary(doc);
            names.Elements["/EmbeddedFiles"] = leaf.Reference;
            doc.Internals.Catalog.Elements["/Names"] = names;
            var list = PdfAttachments.List(doc);
            Check("tree and annotation files are listed, the shared one once", list.Count == 2, string.Join(",", list.Select(e => e.Name + "@" + e.Source)));
            var note = list.FirstOrDefault(e => e.Name == "note.txt");
            Check("an annotation file carries its page and its contents note", note is { Source: "Page 2", Description: "see this" }, "");
        }

        // No attachments, and a spec with no embedded stream, list nothing.
        {
            var doc = new PdfDocument();
            doc.AddPage();
            Check("a document with no attachments lists none", PdfAttachments.List(doc).Count == 0, "");
            var bare = Indirect(doc, fs => fs.Elements["/F"] = new PdfString("link.txt"));
            var leaf = Node(doc, n => n.Elements["/Names"] = new PdfArray(doc, new PdfString("l"), bare.Reference));
            var names = new PdfDictionary(doc);
            names.Elements["/EmbeddedFiles"] = leaf.Reference;
            doc.Internals.Catalog.Elements["/Names"] = names;
            Check("a file spec with no embedded stream is skipped", PdfAttachments.List(doc).Count == 0, "");
        }

        // A declared size past the cap is refused before anything is decoded.
        {
            var e = new AttachmentEntry { Name = "huge.bin", Source = "Document", Size = PdfAttachments.MaxBytes + 1 };
            bool refused = false;
            try { e.ReadBytes(); } catch (InvalidDataException) { refused = true; }
            Check("an oversized or missing attachment is refused", refused, "");
        }
    }

    private static DateTime? AttachmentNames_Date(string? s) => PdfAttachments.ParsePdfDate(s);
}
