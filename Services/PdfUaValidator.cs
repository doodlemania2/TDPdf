using System.Text.RegularExpressions;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using PdfSharpCore.Pdf.IO;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf.Services
{
    /// <summary>
    /// TDPdf's PDF/UA checker (#243): the problems a program can find, mapped to the Matterhorn Protocol's
    /// checkpoints. It reads objects with PdfSharpCore and page content with PdfPig, so every check runs (and is
    /// tested) on any machine.
    /// </summary>
    /// <remarks>
    /// A SUBSET, not a conformance validator: the report says "passed TDPdf's machine checks", never "compliant".
    /// Each check runs in a guard, so one malformed object becomes a "could not check" note instead of a crash,
    /// and the verdict then reads "partly checked". The checkpoint numbers follow the design note for #243;
    /// where the Matterhorn table offers a finer id the finding's message still says what is wrong in words.
    /// </remarks>
    internal static class PdfUaValidator
    {
        // ── Limits ────────────────────────────────────────────────────────────────────
        internal const int MaxStructNodes = 250_000;
        internal const int MaxStructDepth = 256;
        private const int MaxNumberTreeEntries = 200_000;
        private const int MaxFormDepth = 3;
        private const int MaxFindingsPerRule = 25;

        // ── Checkpoints ───────────────────────────────────────────────────────────────
        internal const string CpTagged = "01-007";            // MarkInfo / structure tree
        internal const string CpUntagged = "01-005";          // content neither tagged nor an artifact
        internal const string CpTreeIntegrity = "01-006";     // MCIDs and structure elements that do not meet
        internal const string CpRoleMap = "02-001";           // non-standard type with no standard mapping
        internal const string CpRoleMapCircular = "02-003";
        internal const string CpRoleMapStandard = "02-004";
        internal const string CpMetadata = "06-001";          // XMP present / identifies PDF/UA
        internal const string CpTitle = "06-003";             // dc:title / document title
        internal const string CpDisplayTitle = "07-001";      // ViewerPreferences DisplayDocTitle
        internal const string CpTagMisuse = "09-004";         // parent / child rules (tables, lists, TOC)
        internal const string CpUnicode = "10-001";           // no Unicode mapping
        internal const string CpLanguage = "11-001";          // /Lang
        internal const string CpFigureAlt = "13-004";
        internal const string CpHeadingFirst = "14-002";
        internal const string CpHeadingSkip = "14-003";
        internal const string CpHeadingMixed = "14-006";
        internal const string CpTableScope = "15-003";
        internal const string CpFormulaAlt = "17-002";
        internal const string CpNoteId = "19-003";
        internal const string CpNoteIdDuplicate = "19-004";
        internal const string CpOptionalContent = "20-001";
        internal const string CpEmbeddedFile = "21-001";
        internal const string CpXfa = "25-001";
        internal const string CpSecurity = "26-001";
        internal const string CpAnnotTree = "28-001";
        internal const string CpAnnotContents = "28-002";
        internal const string CpAnnotLink = "28-004";
        internal const string CpAnnotForm = "28-005";
        internal const string CpTabOrder = "28-006";
        internal const string CpFontEmbedded = "31-001";

        /// <summary>What a person has to check; no program can decide any of these.</summary>
        internal static readonly string[] ManualChecks =
        {
            "Alternative text says what each picture means, not just that it is a picture.",
            "The reading order of the tags matches the order a reader would expect.",
            "Headings, tables and lists are tagged for what they are, not for how they look.",
            "Anything decorative (rules, backgrounds, page numbers) is marked as an artifact, and nothing meaningful is.",
            "Text and graphics have enough contrast, and colour is never the only way to tell things apart.",
            "A change of language inside the text is marked, wherever the language of a passage differs from the document's.",
            "Form fields have labels that make sense when read aloud, and the tab order follows the page.",
            "Nothing flashes, and no sound plays by itself.",
        };

        /// <summary>The rule set the file asks for: PDF/UA-2 when its XMP says part 2, otherwise PDF/UA-1.</summary>
        internal static PdfUaStandard StandardOf(string? xmp) => XmpPart(xmp) == 2 ? PdfUaStandard.Ua2 : PdfUaStandard.Ua1;

        /// <summary>Checks the file at <paramref name="path"/>. Throws when it cannot be read at all (not a PDF, needs a password).</summary>
        internal static PdfUaReport Check(string path, CancellationToken ct = default)
        {
            using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            return Check(doc, path, ct);
        }

        /// <summary>Checks an already open document that was read from <paramref name="path"/>.</summary>
        internal static PdfUaReport Check(PdfDocument doc, string path, CancellationToken ct = default)
        {
            var c = new Context(doc, ct);
            string? xmp = ReadXmp(doc);
            c.Standard = StandardOf(xmp);
            c.ClaimsPdfUa = XmpPart(xmp) is not null;

            Guard(c, "document settings", () => CheckDocument(c, xmp));
            Guard(c, "structure tree", () => CheckStructure(c));
            Guard(c, "page content", () => CheckContent(c, path));
            Guard(c, "annotations", () => CheckAnnotations(c));
            Guard(c, "fonts", () => CheckFonts(c));

            if (c.Standard == PdfUaStandard.Ua2)
                c.Partial("PDF/UA-2 structure namespaces and PDF 2.0 rules beyond the structure types");

            var verdict = c.Findings.Any(f => f.Severity == PdfUaSeverity.Error) ? PdfUaVerdict.Failed
                : c.PartialReasons.Count > 0 ? PdfUaVerdict.PartlyChecked : PdfUaVerdict.PassedMachineChecks;
            var coverage = new List<string>(c.Coverage);
            foreach (var p in c.PartialReasons) coverage.Add("Not checked: " + p);
            return new PdfUaReport(c.Standard, verdict, c.Findings, ManualChecks, coverage, c.ClaimsPdfUa);
        }

        // ── Plumbing ──────────────────────────────────────────────────────────────────

        private sealed class Context
        {
            internal Context(PdfDocument doc, CancellationToken ct)
            {
                Doc = doc; Ct = ct;
                for (int i = 0; i < doc.PageCount; i++)
                {
                    var page = doc.Pages[i];
                    PageIndexByObject[ObjectKey(page)] = i;
                }
            }
            internal PdfDocument Doc { get; }
            internal CancellationToken Ct { get; }
            internal PdfUaStandard Standard { get; set; }
            internal bool ClaimsPdfUa { get; set; }
            internal List<PdfUaFinding> Findings { get; } = new();
            internal List<string> Coverage { get; } = new();
            internal List<string> PartialReasons { get; } = new();
            internal Dictionary<(int, int), int> PageIndexByObject { get; } = new();
            internal HashSet<(int Page, int Mcid)> ReferencedMcids { get; } = new();
            internal bool HasStructTree { get; set; }
            internal Dictionary<int, PdfItem>? ParentTree { get; set; }
            internal Dictionary<string, int> RuleCounts { get; } = new();

            internal void Add(string checkpoint, PdfUaSeverity sev, string message, int? page = null,
                PdfObject? obj = null, PdfUaBounds? bounds = null, PdfUaFix fix = PdfUaFix.None)
            {
                // Many pages share one fault; keep the report readable and say how many more there were.
                string key = checkpoint + "|" + message.Split(':')[0];
                RuleCounts.TryGetValue(key, out int n);
                RuleCounts[key] = n + 1;
                if (n >= MaxFindingsPerRule) return;
                int? num = null, gen = null;
                if (obj is not null && obj.Internals.ObjectID is { IsEmpty: false } id) { num = id.ObjectNumber; gen = id.GenerationNumber; }
                Findings.Add(new PdfUaFinding(Standard, checkpoint, sev, message, page, num, gen, bounds, fix));
            }

            internal void Partial(string what) { if (!PartialReasons.Contains(what)) PartialReasons.Add(what); }
        }

        private static void Guard(Context c, string what, Action check)
        {
            c.Ct.ThrowIfCancellationRequested();
            try { check(); c.Coverage.Add("Checked: " + what); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                c.Partial(what + " (" + FirstLine(ex.Message) + ")");
                c.Findings.Add(new PdfUaFinding(c.Standard, "-", PdfUaSeverity.Note,
                    $"Could not check the {what}: {FirstLine(ex.Message)}"));
            }
        }

        private static string FirstLine(string s)
        {
            int i = s.IndexOfAny(new[] { '\r', '\n' });
            return i < 0 ? s : s[..i];
        }

        private static (int, int) ObjectKey(PdfObject o) =>
            o.Internals.ObjectID is { IsEmpty: false } id ? (id.ObjectNumber, id.GenerationNumber) : (-1, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o));

        private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

        private static PdfDictionary? Dict(PdfItem? item) => Resolve(item) as PdfDictionary;

        private static string? Name(PdfItem? item) => Resolve(item) switch
        {
            PdfName n => n.Value.TrimStart('/'),
            PdfNameObject no => no.Value.TrimStart('/'),
            _ => null,
        };

        private static string? Str(PdfItem? item) => Resolve(item) switch
        {
            PdfString s => s.Value,
            PdfStringObject so => so.Value,
            _ => null,
        };

        private static bool? Bool(PdfItem? item) => Resolve(item) switch
        {
            PdfBoolean b => b.Value,
            PdfBooleanObject bo => bo.Value,
            _ => null,
        };

        private static int? Int(PdfItem? item) => Resolve(item) switch
        {
            PdfInteger i => i.Value,
            PdfIntegerObject io => io.Value,
            _ => null,
        };

        private static IEnumerable<PdfItem?> Items(PdfItem? item)
        {
            var r = Resolve(item);
            if (r is PdfArray arr)
                for (int i = 0; i < arr.Elements.Count; i++) yield return Resolve(arr.Elements[i]);
            else if (r is not null) yield return r;
        }

        private static PdfDictionary Catalog(PdfDocument doc) => doc.Internals.Catalog;

        // ── Metadata ──────────────────────────────────────────────────────────────────

        private static string? ReadXmp(PdfDocument doc)
        {
            try
            {
                var meta = Dict(Catalog(doc).Elements["/Metadata"]);
                if (meta?.Stream is null) return null;
                var bytes = meta.Stream.UnfilteredValue;
                return bytes is { Length: > 0 } ? System.Text.Encoding.UTF8.GetString(bytes) : null;
            }
            catch { return null; }
        }

        private static readonly Regex PartElement = new(@"<pdfuaid:part>\s*(\d+)\s*</pdfuaid:part>", RegexOptions.Compiled);
        private static readonly Regex PartAttribute = new(@"pdfuaid:part\s*=\s*[""'](\d+)[""']", RegexOptions.Compiled);
        private static readonly Regex DcTitle = new(@"<dc:title>(.*?)</dc:title>", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex ListItem = new(@"<rdf:li[^>]*>(.*?)</rdf:li>", RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>The PDF/UA part the XMP packet declares, or null when it declares none.</summary>
        internal static int? XmpPart(string? xmp)
        {
            if (string.IsNullOrEmpty(xmp)) return null;
            var m = PartElement.Match(xmp);
            if (!m.Success) m = PartAttribute.Match(xmp);
            return m.Success && int.TryParse(m.Groups[1].Value, out int part) ? part : null;
        }

        /// <summary>The XMP dc:title text, or null when there is none or it is empty.</summary>
        internal static string? XmpTitle(string? xmp)
        {
            if (string.IsNullOrEmpty(xmp)) return null;
            var t = DcTitle.Match(xmp);
            if (!t.Success) return null;
            var li = ListItem.Match(t.Groups[1].Value);
            string text = (li.Success ? li.Groups[1].Value : t.Groups[1].Value).Trim();
            return text.Length == 0 ? null : text;
        }

        private static readonly Regex LanguageTag = new(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{1,8})*$", RegexOptions.Compiled);

        /// <summary>True for a well-formed BCP 47 language tag such as "en", "en-US" or "zh-Hant-TW".</summary>
        internal static bool IsLanguageTag(string? tag) => tag is not null && LanguageTag.IsMatch(tag.Trim());

        private static void CheckDocument(Context c, string? xmp)
        {
            var doc = c.Doc;
            var cat = Catalog(doc);

            // Tagged: MarkInfo /Marked true, and not /Suspects.
            var markInfo = Dict(cat.Elements["/MarkInfo"]);
            c.HasStructTree = Dict(cat.Elements["/StructTreeRoot"]) is not null;
            bool marked = markInfo is not null && Bool(markInfo.Elements["/Marked"]) == true;
            if (!marked)
            {
                c.Add(CpTagged, PdfUaSeverity.Error,
                    c.HasStructTree
                        ? "The document has a structure tree but is not marked as tagged (MarkInfo / Marked is not true)."
                        : "The document is not tagged: it has no structure tree, so a screen reader has nothing to read from.",
                    fix: c.HasStructTree ? PdfUaFix.MarkTagged : PdfUaFix.None);
            }
            else if (!c.HasStructTree)
            {
                c.Add(CpTagged, PdfUaSeverity.Error, "The document says it is tagged but has no structure tree.");
            }
            if (markInfo is not null && Bool(markInfo.Elements["/Suspects"]) == true)
                c.Add(CpTagged, PdfUaSeverity.Error, "The tags are marked as suspect (MarkInfo / Suspects is true), so they cannot be relied on.");

            // Language.
            string? lang = Str(cat.Elements["/Lang"]);
            if (string.IsNullOrWhiteSpace(lang))
                c.Add(CpLanguage, PdfUaSeverity.Error, "The document has no language, so a screen reader cannot choose the right voice.", fix: PdfUaFix.SetLanguage);
            else if (!IsLanguageTag(lang))
                c.Add(CpLanguage, PdfUaSeverity.Error, $"The document language \"{lang}\" is not a valid language tag (use something like en-US).", fix: PdfUaFix.SetLanguage);

            // Title.
            string? infoTitle = null;
            try { infoTitle = doc.Info.Title; } catch (InvalidCastException) { }
            string? xmpTitle = XmpTitle(xmp);
            if (string.IsNullOrWhiteSpace(infoTitle) && xmpTitle is null)
                c.Add(CpTitle, PdfUaSeverity.Error, "The document has no title.", fix: PdfUaFix.SetTitle);
            else if (xmp is not null && xmpTitle is null)
                c.Add(CpTitle, PdfUaSeverity.Warning, "The title is in the Document Info only; the XMP metadata has no dc:title, which PDF/UA reads it from.");

            // Show the title in the window's title bar.
            var vp = Dict(cat.Elements["/ViewerPreferences"]);
            if (vp is null || Bool(vp.Elements["/DisplayDocTitle"]) != true)
                c.Add(CpDisplayTitle, PdfUaSeverity.Error, "The window title is not set to show the document title (ViewerPreferences / DisplayDocTitle is not true).", fix: PdfUaFix.SetDisplayDocTitle);

            // XMP and the PDF/UA identification.
            if (xmp is null)
                c.Add(CpMetadata, PdfUaSeverity.Warning, "The document has no XMP metadata, so it does not identify itself as PDF/UA.");
            else if (!c.ClaimsPdfUa)
                c.Add(CpMetadata, PdfUaSeverity.Warning, "The XMP metadata does not say the file is PDF/UA (no pdfuaid:part). It was checked against PDF/UA-1.");

            // Security: an owner password must not stop assistive technology reading the text (bit 10 of /P).
            try
            {
                if (doc.OriginalProtection is { } prot && (prot.Permissions & 0x200) == 0)
                    c.Add(CpSecurity, PdfUaSeverity.Error, "The document's security settings stop assistive technology from extracting its text.");
            }
            catch (Exception) { c.Partial("security settings"); }

            // XFA forms are not accessible.
            var acro = Dict(cat.Elements["/AcroForm"]);
            if (acro is not null && acro.Elements.ContainsKey("/XFA"))
                c.Add(CpXfa, PdfUaSeverity.Error, "The document contains an XFA form, which assistive technology cannot read.");

            // Optional content configurations have names.
            var oc = Dict(cat.Elements["/OCProperties"]);
            if (oc is not null)
            {
                var d = Dict(oc.Elements["/D"]);
                if (d is not null && string.IsNullOrWhiteSpace(Str(d.Elements["/Name"])))
                    c.Add(CpOptionalContent, PdfUaSeverity.Error, "The default optional content (layers) configuration has no name.");
                foreach (var cfg in Items(oc.Elements["/Configs"]))
                    if (cfg is PdfDictionary cd && string.IsNullOrWhiteSpace(Str(cd.Elements["/Name"])))
                        c.Add(CpOptionalContent, PdfUaSeverity.Error, "An optional content (layers) configuration has no name.");
            }

            // Embedded files carry both file name entries.
            CheckEmbeddedFiles(c, Dict(Dict(cat.Elements["/Names"])?.Elements["/EmbeddedFiles"]), 0, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance));
        }

        private static void CheckEmbeddedFiles(Context c, PdfDictionary? node, int depth, HashSet<PdfDictionary> seen)
        {
            if (node is null || depth > 32 || !seen.Add(node)) return;
            if (Resolve(node.Elements["/Names"]) is PdfArray names)
            {
                for (int i = 1; i < names.Elements.Count; i += 2)
                    if (Dict(names.Elements[i]) is { } spec
                        && (string.IsNullOrEmpty(Str(spec.Elements["/F"])) || string.IsNullOrEmpty(Str(spec.Elements["/UF"]))))
                        c.Add(CpEmbeddedFile, PdfUaSeverity.Error, "An embedded file's description is missing its F or UF file name.", obj: spec);
            }
            foreach (var kid in Items(node.Elements["/Kids"]))
                CheckEmbeddedFiles(c, kid as PdfDictionary, depth + 1, seen);
        }

        // ── Structure tree ────────────────────────────────────────────────────────────

        private static readonly HashSet<string> StandardTypes = new(StringComparer.Ordinal)
        {
            "Document", "Part", "Art", "Sect", "Div", "BlockQuote", "Caption", "TOC", "TOCI", "Index",
            "NonStruct", "Private", "P", "H", "H1", "H2", "H3", "H4", "H5", "H6", "L", "LI", "Lbl", "LBody",
            "Table", "TR", "TH", "TD", "THead", "TBody", "TFoot", "Span", "Quote", "Note", "Reference",
            "BibEntry", "Code", "Link", "Annot", "Ruby", "RB", "RT", "RP", "Warichu", "WT", "WP",
            "Figure", "Formula", "Form",
        };

        private static readonly HashSet<string> Ua2ExtraTypes = new(StringComparer.Ordinal)
        {
            "DocumentFragment", "Aside", "Title", "FENote", "Sub", "Em", "Strong", "Artifact",
        };

        private sealed class Elem
        {
            internal PdfDictionary Dict = null!;
            internal string Raw = "";
            internal string Role = "";
            internal Elem? Parent;
            internal PdfDictionary? Page;
            internal PdfDictionary? Table;     // the nearest Table element above (or this one)
        }

        private sealed class TableInfo { internal bool UsesHeaders; internal List<Elem> BareHeaders = new(); }

        private static readonly HashSet<string> TableKids = new() { "TR", "THead", "TBody", "TFoot", "Caption" };
        private static readonly HashSet<string> RowParents = new() { "Table", "THead", "TBody", "TFoot" };
        private static readonly HashSet<string> ListKids = new() { "LI", "L", "Caption" };
        private static readonly HashSet<string> TocKids = new() { "TOCI", "TOC", "Caption" };

        private static void CheckStructure(Context c)
        {
            var root = Dict(Catalog(c.Doc).Elements["/StructTreeRoot"]);
            if (root is null) { c.Coverage.Add("The document has no structure tree to check."); return; }

            var roleMap = Dict(root.Elements["/RoleMap"]);
            var roleCache = new Dictionary<string, (string Role, string? Problem)>(StringComparer.Ordinal);
            var allowed = new HashSet<string>(StandardTypes);
            if (c.Standard == PdfUaStandard.Ua2) allowed.UnionWith(Ua2ExtraTypes);

            (string Role, string? Problem) Resolve2(string raw)
            {
                if (roleCache.TryGetValue(raw, out var hit)) return hit;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                string cur = raw; string? problem = null;
                while (true)
                {
                    if (!seen.Add(cur)) { problem = "circular"; break; }
                    string? next = roleMap is null ? null : Name(roleMap.Elements["/" + cur]);
                    if (next is null) break;
                    if (allowed.Contains(cur) && next != cur) problem = "standard";
                    cur = next.TrimStart('/');
                    if (seen.Count > 32) { problem = "circular"; break; }
                }
                if (problem is null && !allowed.Contains(cur)) problem = "unmapped";
                return roleCache[raw] = (cur, problem);
            }

            var tables = new Dictionary<PdfDictionary, TableInfo>(ReferenceEqualityComparer.Instance);
            var noteIds = new HashSet<string>(StringComparer.Ordinal);
            var seenNodes = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance) { root };
            var roleReported = new HashSet<string>(StringComparer.Ordinal);
            int nodes = 0, lastHeading = 0, headingCount = 0; bool sawBareH = false, sawNumbered = false;
            bool capped = false;

            var stack = new Stack<(PdfDictionary Node, Elem? Parent, PdfDictionary? Pg, int Depth)>();
            foreach (var k in KidsReversed(root)) if (k is PdfDictionary kd && seenNodes.Add(kd)) stack.Push((kd, null, null, 1));

            while (stack.Count > 0)
            {
                c.Ct.ThrowIfCancellationRequested();
                var (node, parent, inheritedPg, depth) = stack.Pop();
                string type = Name(node.Elements["/Type"]) ?? "";
                var pg = Dict(node.Elements["/Pg"]) ?? inheritedPg;

                // Marked-content references and object references are leaves.
                if (type is "MCR" or "OBJR") continue;

                if (++nodes > MaxStructNodes || depth > MaxStructDepth)
                {
                    capped = true;
                    break;
                }

                string raw = Name(node.Elements["/S"]) ?? "";
                var (role, problem) = Resolve2(raw);
                var e = new Elem { Dict = node, Raw = raw, Role = role, Parent = parent, Page = pg };
                e.Table = role == "Table" ? node : parent?.Table;
                int? pageIndex = pg is not null && c.PageIndexByObject.TryGetValue(ObjectKey(pg), out int pi) ? pi : null;

                if (raw.Length == 0)
                    c.Add(CpRoleMap, PdfUaSeverity.Error, "A structure element has no type (S).", pageIndex, node);
                else if (problem is not null && roleReported.Add(raw))
                {
                    switch (problem)
                    {
                        case "circular":
                            c.Add(CpRoleMapCircular, PdfUaSeverity.Error, $"The structure type \"{raw}\" is mapped in a circle in the role map.", pageIndex, node);
                            break;
                        case "standard":
                            c.Add(CpRoleMapStandard, PdfUaSeverity.Error, $"The standard structure type \"{raw}\" is remapped in the role map.", pageIndex, node);
                            break;
                        default:
                            c.Add(CpRoleMap, PdfUaSeverity.Error, $"The structure type \"{raw}\" is not mapped to a standard type in the role map.", pageIndex, node);
                            break;
                    }
                }

                // Marked content this element owns, for the integrity check against the page content.
                foreach (var kid in Items(node.Elements["/K"]))
                {
                    if (kid is PdfInteger or PdfIntegerObject)
                    {
                        if (Int(kid) is { } m && pg is not null && c.PageIndexByObject.TryGetValue(ObjectKey(pg), out int p0)) c.ReferencedMcids.Add((p0, m));
                    }
                    else if (kid is PdfDictionary kd2 && Name(kd2.Elements["/Type"]) == "MCR")
                    {
                        var mpg = Dict(kd2.Elements["/Pg"]) ?? pg;
                        if (Int(kd2.Elements["/MCID"]) is { } m2 && mpg is not null && c.PageIndexByObject.TryGetValue(ObjectKey(mpg), out int p1)) c.ReferencedMcids.Add((p1, m2));
                    }
                }

                var kidElems = KidElements(node).Select(kd3 => (Dict: kd3, Role: Resolve2(Name(kd3.Elements["/S"]) ?? "").Role)).ToList();

                switch (role)
                {
                    case "Figure":
                        if (string.IsNullOrWhiteSpace(Str(node.Elements["/Alt"])) && string.IsNullOrWhiteSpace(Str(node.Elements["/ActualText"])))
                            c.Add(CpFigureAlt, PdfUaSeverity.Error, "A figure has no alternative text.", pageIndex, node);
                        break;
                    case "Formula":
                        if (string.IsNullOrWhiteSpace(Str(node.Elements["/Alt"])) && string.IsNullOrWhiteSpace(Str(node.Elements["/ActualText"])))
                            c.Add(CpFormulaAlt, PdfUaSeverity.Error, "A formula has no alternative text.", pageIndex, node);
                        break;
                    case "Note":
                        {
                            string? id = Str(node.Elements["/ID"]);
                            if (string.IsNullOrEmpty(id)) c.Add(CpNoteId, PdfUaSeverity.Error, "A note has no ID.", pageIndex, node);
                            else if (!noteIds.Add(id)) c.Add(CpNoteIdDuplicate, PdfUaSeverity.Error, $"Two notes share the ID \"{id}\".", pageIndex, node);
                            break;
                        }
                    case "H": case "H1": case "H2": case "H3": case "H4": case "H5": case "H6":
                        headingCount++;
                        if (role == "H") sawBareH = true; else sawNumbered = true;
                        if (role != "H")
                        {
                            int level = role[1] - '0';
                            if (lastHeading == 0 && level != 1)
                                c.Add(CpHeadingFirst, PdfUaSeverity.Error, $"The first heading is {role}, not H1.", pageIndex, node);
                            else if (lastHeading > 0 && level > lastHeading + 1)
                                c.Add(CpHeadingSkip, PdfUaSeverity.Error, $"A heading level is skipped: {role} follows H{lastHeading}.", pageIndex, node);
                            lastHeading = level;
                        }
                        break;
                    case "Table":
                        tables[node] = new TableInfo();
                        foreach (var k in kidElems.Where(k => !TableKids.Contains(k.Role) && k.Role.Length > 0))
                            c.Add(CpTagMisuse, PdfUaSeverity.Error, $"A table contains a {k.Role} directly; it may hold only rows, header, body or footer groups, and a caption.", pageIndex, k.Dict);
                        break;
                    case "TR":
                        if (parent is not null && !RowParents.Contains(parent.Role))
                            c.Add(CpTagMisuse, PdfUaSeverity.Error, $"A table row sits inside a {parent.Role}, not inside a table.", pageIndex, node);
                        foreach (var k in kidElems.Where(k => k.Role is not ("TH" or "TD") && k.Role.Length > 0))
                            c.Add(CpTagMisuse, PdfUaSeverity.Error, $"A table row contains a {k.Role}; it may hold only header and data cells.", pageIndex, k.Dict);
                        break;
                    case "TH":
                    case "TD":
                        if (parent is not null && parent.Role != "TR")
                            c.Add(CpTagMisuse, PdfUaSeverity.Error, $"A table cell sits inside a {parent.Role}, not inside a table row.", pageIndex, node);
                        if (e.Table is not null && tables.TryGetValue(e.Table, out var info))
                        {
                            if (AttributeValue(node, "/Headers", "Table") is not null) info.UsesHeaders = true;
                            else if (role == "TH" && AttributeValue(node, "/Scope", "Table") is null) info.BareHeaders.Add(e);
                        }
                        break;
                    case "L":
                        foreach (var k in kidElems.Where(k => !ListKids.Contains(k.Role) && k.Role.Length > 0))
                            c.Add(CpTagMisuse, PdfUaSeverity.Error, $"A list contains a {k.Role} directly; it may hold only list items and a caption.", pageIndex, k.Dict);
                        break;
                    case "LI":
                        if (parent is not null && parent.Role != "L")
                            c.Add(CpTagMisuse, PdfUaSeverity.Error, $"A list item sits inside a {parent.Role}, not inside a list.", pageIndex, node);
                        foreach (var k in kidElems.Where(k => k.Role is not ("Lbl" or "LBody") && k.Role.Length > 0))
                            c.Add(CpTagMisuse, PdfUaSeverity.Error, $"A list item contains a {k.Role}; it may hold only a label and a body.", pageIndex, k.Dict);
                        break;
                    case "Lbl":
                    case "LBody":
                        if (parent is not null && parent.Role != "LI")
                            c.Add(CpTagMisuse, PdfUaSeverity.Error, $"A list {(role == "Lbl" ? "label" : "body")} sits inside a {parent.Role}, not inside a list item.", pageIndex, node);
                        break;
                    case "TOC":
                        foreach (var k in kidElems.Where(k => !TocKids.Contains(k.Role) && k.Role.Length > 0))
                            c.Add(CpTagMisuse, PdfUaSeverity.Error, $"A table of contents holds a {k.Role} directly; it may hold only entries and a caption.", pageIndex, k.Dict);
                        break;
                    case "TOCI":
                        if (parent is not null && parent.Role != "TOC")
                            c.Add(CpTagMisuse, PdfUaSeverity.Error, $"A table of contents entry sits inside a {parent.Role}, not inside a table of contents.", pageIndex, node);
                        break;
                }

                foreach (var kd4 in KidElementsReversed(node))
                    if (seenNodes.Add(kd4)) stack.Push((kd4, e, pg, depth + 1));
            }

            if (sawBareH && sawNumbered && c.Standard == PdfUaStandard.Ua1)
                c.Add(CpHeadingMixed, PdfUaSeverity.Error, "The document mixes unnumbered headings (H) with numbered ones (H1 to H6).");

            foreach (var (table, info) in tables)
            {
                if (info.UsesHeaders || info.BareHeaders.Count == 0) continue;
                int? pageIndex = info.BareHeaders[0].Page is { } tp && c.PageIndexByObject.TryGetValue(ObjectKey(tp), out int ti) ? ti : null;
                c.Add(CpTableScope, PdfUaSeverity.Error,
                    $"{info.BareHeaders.Count} table header cell{(info.BareHeaders.Count == 1 ? "" : "s")} {(info.BareHeaders.Count == 1 ? "has" : "have")} no Scope (and the table does not use Headers).",
                    pageIndex, table);
            }

            c.Coverage.Add($"Structure tree: {nodes:N0} elements{(capped ? " (stopped at the limit)" : "")}.");
            if (capped) c.Partial($"the structure tree beyond {MaxStructNodes:N0} elements or {MaxStructDepth} levels");
        }

        private static IEnumerable<PdfDictionary> KidElements(PdfDictionary node)
        {
            foreach (var k in Items(node.Elements["/K"]))
                if (k is PdfDictionary d && Name(d.Elements["/Type"]) is not ("MCR" or "OBJR")) yield return d;
        }

        private static IEnumerable<PdfDictionary> KidElementsReversed(PdfDictionary node) => KidElements(node).Reverse();

        private static IEnumerable<PdfItem?> KidsReversed(PdfDictionary node) => Items(node.Elements["/K"]).Reverse();

        /// <summary>A value from a table attribute object (/A with owner Table), or null.</summary>
        private static PdfItem? AttributeValue(PdfDictionary elem, string key, string owner)
        {
            if (elem.Elements.ContainsKey(key) && key == "/Headers") return elem.Elements[key];
            foreach (var a in Items(elem.Elements["/A"]))
                if (a is PdfDictionary ad && (Name(ad.Elements["/O"]) == owner) && ad.Elements.ContainsKey(key))
                    return Resolve(ad.Elements[key]);
            return null;
        }

        // ── Page content ──────────────────────────────────────────────────────────────

        private static void CheckContent(Context c, string path)
        {
            if (!c.HasStructTree)
            {
                c.Coverage.Add("Page content was not checked against tags, because the document has no structure tree.");
                return;
            }
            using var pig = PdfPigDoc.Open(path);
            int pages = Math.Min(pig.NumberOfPages, c.Doc.PageCount);
            long untaggedPages = 0;
            for (int i = 0; i < pages; i++)
            {
                c.Ct.ThrowIfCancellationRequested();
                try
                {
                    var page = pig.GetPage(i + 1);
                    var marked = page.GetMarkedContents();
                    var tagged = new HashSet<object>(ReferenceEqualityComparer.Instance);
                    var mcids = new HashSet<int>();
                    var stack = new Stack<UglyToad.PdfPig.Content.MarkedContentElement>(marked);
                    while (stack.Count > 0)
                    {
                        var el = stack.Pop();
                        foreach (var l in el.Letters) tagged.Add(l);
                        foreach (var p in el.Paths) tagged.Add(p);
                        foreach (var im in el.Images) tagged.Add(im);
                        if (!el.IsArtifact && el.MarkedContentIdentifier >= 0) mcids.Add(el.MarkedContentIdentifier);
                        foreach (var ch in el.Children) stack.Push(ch);
                    }

                    var loose = page.Letters.Where(l => !tagged.Contains(l)).ToList();
                    int loosePaths = 0, looseImages = 0;
                    double px0 = double.MaxValue, py0 = double.MaxValue, px1 = double.MinValue, py1 = double.MinValue;
                    foreach (var l in loose.Take(30))
                    {
                        var (bl, bb, br, bt) = PdfBoxExtent.Of(l.BoundingBox);
                        px0 = Math.Min(px0, bl); py0 = Math.Min(py0, page.Height - bt);
                        px1 = Math.Max(px1, br); py1 = Math.Max(py1, page.Height - bb);
                    }
                    foreach (var p in page.Paths.Where(p => p.IsStroked || p.IsFilled))
                        if (!tagged.Contains(p)) loosePaths++;
                    foreach (var im in page.GetImages())
                        if (!tagged.Contains(im))
                        {
                            looseImages++;
                            var (il, ib, ir, it) = PdfBoxExtent.Of(im.BoundingBox);
                            if (loose.Count == 0)
                            {
                                px0 = Math.Min(px0, il); py0 = Math.Min(py0, page.Height - it);
                                px1 = Math.Max(px1, ir); py1 = Math.Max(py1, page.Height - ib);
                            }
                        }

                    if (loose.Count > 0 || looseImages > 0)
                    {
                        untaggedPages++;
                        var parts = new List<string>();
                        if (loose.Count > 0) parts.Add($"{loose.Count} character{(loose.Count == 1 ? "" : "s")} of text");
                        if (looseImages > 0) parts.Add($"{looseImages} picture{(looseImages == 1 ? "" : "s")}");
                        PdfUaBounds? bounds = px1 > px0 && py1 > py0 ? new PdfUaBounds(px0, py0, px1 - px0, py1 - py0) : null;
                        c.Add(CpUntagged, PdfUaSeverity.Error,
                            $"Page {i + 1} has {string.Join(" and ", parts)} that is neither tagged nor marked as an artifact.", i, c.Doc.Pages[i], bounds);
                    }
                    // Drawn lines and shapes are far more often decoration than content; counted, not failed on its own.
                    _ = loosePaths;

                    // The page's marked content and the tree must refer to each other.
                    foreach (int m in mcids)
                        if (!c.ReferencedMcids.Contains((i, m)))
                            c.Add(CpTreeIntegrity, PdfUaSeverity.Error, $"Page {i + 1} has tagged content (MCID {m}) that no structure element points to.", i, c.Doc.Pages[i]);
                    foreach (var (pg, m) in c.ReferencedMcids.Where(r => r.Page == i))
                        if (!mcids.Contains(m))
                            c.Add(CpTreeIntegrity, PdfUaSeverity.Error, $"A structure element points to content (MCID {m}) that is not on page {i + 1}.", i, c.Doc.Pages[i]);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    c.Partial($"page {i + 1} content ({FirstLine(ex.Message)})");
                }
            }
            c.Coverage.Add($"Page content: {pages:N0} pages read.");
        }

        // ── Annotations ───────────────────────────────────────────────────────────────

        private static Dictionary<int, PdfItem> LoadParentTree(Context c)
        {
            var map = new Dictionary<int, PdfItem>();
            var root = Dict(Catalog(c.Doc).Elements["/StructTreeRoot"]);
            var tree = Dict(root?.Elements["/ParentTree"]);
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<(PdfDictionary, int)>();
            if (tree is not null) stack.Push((tree, 0));
            while (stack.Count > 0 && map.Count < MaxNumberTreeEntries)
            {
                var (n, depth) = stack.Pop();
                if (depth > 32 || !seen.Add(n)) continue;
                if (Resolve(n.Elements["/Nums"]) is PdfArray nums)
                    for (int i = 0; i + 1 < nums.Elements.Count; i += 2)
                        if (Int(nums.Elements[i]) is { } key && Resolve(nums.Elements[i + 1]) is { } v) map[key] = v;
                foreach (var kid in Items(n.Elements["/Kids"])) if (kid is PdfDictionary kd) stack.Push((kd, depth + 1));
            }
            return map;
        }

        private static void CheckAnnotations(Context c)
        {
            c.ParentTree = c.HasStructTree ? LoadParentTree(c) : new Dictionary<int, PdfItem>();
            int annots = 0;
            for (int i = 0; i < c.Doc.PageCount; i++)
            {
                c.Ct.ThrowIfCancellationRequested();
                var page = c.Doc.Pages[i];
                var list = Items(page.Elements["/Annots"]).OfType<PdfDictionary>().ToList();
                if (list.Count == 0) continue;
                bool anyVisible = false;
                foreach (var a in list)
                {
                    string sub = Name(a.Elements["/Subtype"]) ?? "";
                    if (sub == "Popup") continue;
                    int flags = Int(a.Elements["/F"]) ?? 0;
                    if ((flags & 2) != 0) continue;   // hidden
                    annots++; anyVisible = true;
                    var bounds = AnnotBounds(page, a);

                    // In the structure tree, under the right kind of element.
                    string? parentRole = null;
                    if (Int(a.Elements["/StructParent"]) is { } sp && c.ParentTree!.TryGetValue(sp, out var pv) && pv is PdfDictionary pd)
                        parentRole = Name(pd.Elements["/S"]);
                    if (parentRole is null)
                        c.Add(CpAnnotTree, PdfUaSeverity.Error, $"{AnnotWord(sub)} on page {i + 1} is not part of the structure tree.", i, a, bounds);
                    else if (sub == "Link" && parentRole != "Link")
                        c.Add(CpAnnotLink, PdfUaSeverity.Error, $"A link on page {i + 1} is not inside a Link element.", i, a, bounds);
                    else if (sub == "Widget" && parentRole != "Form")
                        c.Add(CpAnnotForm, PdfUaSeverity.Error, $"A form field on page {i + 1} is not inside a Form element.", i, a, bounds);

                    if (sub != "Widget" && string.IsNullOrWhiteSpace(Str(a.Elements["/Contents"])))
                        c.Add(CpAnnotContents, PdfUaSeverity.Error, $"{AnnotWord(sub)} on page {i + 1} has no description (Contents).", i, a, bounds);
                }
                if (anyVisible && Name(page.Elements["/Tabs"]) != "S")
                    c.Add(CpTabOrder, PdfUaSeverity.Error, $"Page {i + 1} has links or fields but its tab order does not follow the structure (Tabs is not S).", i, page);
            }
            c.Coverage.Add($"Annotations: {annots:N0} checked.");
        }

        private static string AnnotWord(string sub) => sub switch
        {
            "Link" => "A link",
            "Widget" => "A form field",
            "" => "An annotation",
            _ => $"A {sub} annotation",
        };

        /// <summary>An annotation's rectangle in points from the page's top left, or null when the page is turned or the rectangle is unusable.</summary>
        private static PdfUaBounds? AnnotBounds(PdfPage page, PdfDictionary annot)
        {
            try
            {
                if (page.Rotate % 360 != 0) return null;
                if (Resolve(annot.Elements["/Rect"]) is not PdfArray r || r.Elements.Count < 4) return null;
                double Num(int k) => Resolve(r.Elements[k]) switch
                {
                    PdfInteger n => n.Value,
                    PdfReal d => d.Value,
                    _ => double.NaN,
                };
                double x0 = Math.Min(Num(0), Num(2)), x1 = Math.Max(Num(0), Num(2));
                double y0 = Math.Min(Num(1), Num(3)), y1 = Math.Max(Num(1), Num(3));
                if (double.IsNaN(x0 + x1 + y0 + y1)) return null;
                var box = page.CropBox.IsEmpty ? page.MediaBox : page.CropBox;
                double left = box.X1, top = box.Y2;
                return new PdfUaBounds(x0 - left, top - y1, x1 - x0, y1 - y0);
            }
            catch { return null; }
        }

        // ── Fonts ─────────────────────────────────────────────────────────────────────

        private static void CheckFonts(Context c)
        {
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            int fonts = 0;
            for (int i = 0; i < c.Doc.PageCount; i++)
            {
                c.Ct.ThrowIfCancellationRequested();
                var res = PageResources(c.Doc.Pages[i]);
                fonts += CheckFontsIn(c, res, i, seen, 0, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance));
            }
            c.Coverage.Add($"Fonts: {fonts:N0} checked.");
        }

        private static PdfDictionary? PageResources(PdfDictionary page)
        {
            var node = page;
            for (int depth = 0; node is not null && depth < 32; depth++)
            {
                if (Dict(node.Elements["/Resources"]) is { } res) return res;
                node = Dict(node.Elements["/Parent"]);
            }
            return null;
        }

        private static int CheckFontsIn(Context c, PdfDictionary? resources, int pageIndex, HashSet<PdfDictionary> seenFonts, int depth, HashSet<PdfDictionary> seenForms)
        {
            if (resources is null) return 0;
            int count = 0;
            if (Dict(resources.Elements["/Font"]) is { } fontDict)
            {
                foreach (var key in fontDict.Elements.Keys.ToList())
                {
                    if (Dict(fontDict.Elements[key]) is not { } font || !seenFonts.Add(font)) continue;
                    count++;
                    CheckFont(c, font, pageIndex);
                }
            }
            if (depth < MaxFormDepth && Dict(resources.Elements["/XObject"]) is { } xo)
            {
                foreach (var key in xo.Elements.Keys.ToList())
                    if (Dict(xo.Elements[key]) is { } form && Name(form.Elements["/Subtype"]) == "Form" && seenForms.Add(form))
                        count += CheckFontsIn(c, Dict(form.Elements["/Resources"]), pageIndex, seenFonts, depth + 1, seenForms);
            }
            return count;
        }

        private static void CheckFont(Context c, PdfDictionary font, int pageIndex)
        {
            string subtype = Name(font.Elements["/Subtype"]) ?? "";
            if (subtype == "Type3") return;     // drawn from the document's own glyph procedures
            string baseFont = (Name(font.Elements["/BaseFont"]) ?? "(unnamed)").TrimStart('/');
            baseFont = Regex.Replace(baseFont, "^[A-Z]{6}\\+", "");

            var descriptorOwner = font;
            if (subtype == "Type0" && Dict(Items(font.Elements["/DescendantFonts"]).FirstOrDefault()) is { } desc) descriptorOwner = desc;
            var fd = Dict(descriptorOwner.Elements["/FontDescriptor"]);
            bool embedded = fd is not null && (fd.Elements.ContainsKey("/FontFile") || fd.Elements.ContainsKey("/FontFile2") || fd.Elements.ContainsKey("/FontFile3"));
            if (!embedded)
                c.Add(CpFontEmbedded, PdfUaSeverity.Error, $"The font \"{baseFont}\" is not embedded in the file (first used on page {pageIndex + 1}).", pageIndex, font);

            bool unicode = font.Elements.ContainsKey("/ToUnicode");
            if (!unicode)
            {
                if (subtype == "Type0")
                {
                    string? enc = Name(font.Elements["/Encoding"]);
                    unicode = enc is not null && !enc.StartsWith("Identity", StringComparison.Ordinal);
                }
                else
                {
                    var encItem = Resolve(font.Elements["/Encoding"]);
                    unicode = encItem is PdfName or PdfNameObject
                        || (encItem is PdfDictionary ed && (ed.Elements.ContainsKey("/BaseEncoding") || ed.Elements.ContainsKey("/Differences")))
                        || (encItem is null && (fd is null || !IsSymbolic(fd)));
                }
            }
            if (!unicode)
                c.Add(CpUnicode, PdfUaSeverity.Error, $"The font \"{baseFont}\" has no way to map its characters to Unicode text (first used on page {pageIndex + 1}).", pageIndex, font);
        }

        private static bool IsSymbolic(PdfDictionary fd) => ((Int(fd.Elements["/Flags"]) ?? 0) & 4) != 0 && ((Int(fd.Elements["/Flags"]) ?? 0) & 32) == 0;
    }
}
