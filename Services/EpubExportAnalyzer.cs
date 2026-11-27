using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace TDPdf.Services
{
    /// <summary>One line of text with its style, before headings and lists are decided.</summary>
    internal sealed class EpubProtoLine
    {
        internal List<EpubRun> Runs = new();
        internal string Text = "";
        internal double Size;
        internal bool Bold;
        internal double Top, Bottom, Left, Right;
        internal int Chars;
    }

    /// <summary>A group of lines in one style that sit together on a page.</summary>
    internal sealed class EpubProtoBlock
    {
        internal int PageIndex;
        internal double PageHeight;
        internal List<EpubProtoLine> Lines = new();
        internal double Top => Lines.Max(l => l.Top);
        internal double Bottom => Lines.Min(l => l.Bottom);
        internal double Left => Lines.Min(l => l.Left);
        internal double Right => Lines.Max(l => l.Right);
    }

    /// <summary>
    /// Turns PDF pages into book blocks. Untagged pages: words from the nearest-neighbour extractor
    /// (which keeps the spaces of PDFs that place each word by position), text blocks from Docstrum,
    /// order from the unsupervised reading-order detector, and headings from font size and weight.
    /// Tagged pages: the structure tree decides what is a heading, paragraph, list item, picture or table.
    /// </summary>
    internal static class EpubExportAnalyzer
    {
        // ── Untagged pages ────────────────────────────────────────────────────────────────────

        internal static List<EpubProtoBlock> SegmentPage(Page page, int pageIndex, IReadOnlyList<Word>? wordsIn = null)
        {
            var words = (wordsIn ?? page.GetWords(NearestNeighbourWordExtractor.Instance).ToList())
                .Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            var result = new List<EpubProtoBlock>();
            if (words.Count == 0) return result;

            var blockLines = new List<List<List<Word>>>();   // block -> line -> words
            try
            {
                var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
                var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks.ToList()).ToList();
                foreach (var b in ordered)
                    blockLines.Add(b.TextLines.Select(l => l.Words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList())
                                              .Where(l => l.Count > 0).ToList());
            }
            catch
            {
                // A layout analysis that gives up must not lose the page: one block of lines, top down.
                blockLines.Clear();
                blockLines.Add(GroupIntoLines(words));
            }

            foreach (var block in blockLines)
            {
                var lines = block.Select(MakeLine).Where(l => l.Chars > 0).ToList();
                if (lines.Count == 0) continue;
                foreach (var grp in SplitByStyle(lines))
                    result.Add(new EpubProtoBlock { PageIndex = pageIndex, PageHeight = page.Height, Lines = grp });
            }
            return result;
        }

        internal static List<List<Word>> GroupIntoLines(IEnumerable<Word> words)
        {
            var sorted = words.OrderByDescending(w => w.BoundingBox.Top).ThenBy(w => w.BoundingBox.Left).ToList();
            var lines = new List<List<Word>>();
            foreach (var w in sorted)
            {
                var cur = lines.Count > 0 ? lines[^1] : null;
                double tol = Math.Max(1, w.BoundingBox.Height * 0.5);
                if (cur != null && Math.Abs(cur[0].BoundingBox.Bottom - w.BoundingBox.Bottom) < tol) cur.Add(w);
                else lines.Add(new List<Word> { w });
            }
            foreach (var l in lines) l.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
            return lines;
        }

        internal static EpubProtoLine MakeLine(IReadOnlyList<Word> words)
        {
            var line = new EpubProtoLine { Top = double.MinValue, Bottom = double.MaxValue, Left = double.MaxValue, Right = double.MinValue };
            var sizes = new List<double>();
            int bold = 0, total = 0;
            var sb = new StringBuilder();
            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                var r = w.BoundingBox;
                line.Top = Math.Max(line.Top, r.Top); line.Bottom = Math.Min(line.Bottom, r.Bottom);
                line.Left = Math.Min(line.Left, r.Left); line.Right = Math.Max(line.Right, r.Right);
                var first = w.Letters.Count > 0 ? w.Letters[0] : null;
                bool b = first != null && IsBold(first), it = first != null && IsItalic(first);
                string text = w.Text + (i < words.Count - 1 ? " " : "");
                string? key = first is null ? null : EpubFontPolicy.BaseName(first.FontName);
                AddRun(line.Runs, new EpubRun(text, b, it, key));
                sb.Append(text);
                foreach (var l in w.Letters) { sizes.Add(l.PointSize); total++; if (IsBold(l)) bold++; }
            }
            line.Text = sb.ToString();
            line.Chars = total;
            if (sizes.Count > 0) { sizes.Sort(); line.Size = Math.Round(sizes[sizes.Count / 2] * 2) / 2; }
            line.Bold = total > 0 && bold * 2 > total;
            return line;
        }

        internal static bool IsBold(Letter l) =>
            l.FontDetails.IsBold || (l.FontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) ?? false)
                                 || (l.FontName?.Contains("Black", StringComparison.OrdinalIgnoreCase) ?? false);

        internal static bool IsItalic(Letter l) =>
            l.FontDetails.IsItalic || (l.FontName?.Contains("Italic", StringComparison.OrdinalIgnoreCase) ?? false)
                                   || (l.FontName?.Contains("Oblique", StringComparison.OrdinalIgnoreCase) ?? false);

        internal static void AddRun(List<EpubRun> runs, EpubRun run)
        {
            if (run.Text.Length == 0) return;
            if (runs.Count > 0 && runs[^1] is { } last && last.Bold == run.Bold && last.Italic == run.Italic && last.FontKey == run.FontKey)
                runs[^1] = last with { Text = last.Text + run.Text };
            else runs.Add(run);
        }

        private static IEnumerable<List<EpubProtoLine>> SplitByStyle(List<EpubProtoLine> lines)
        {
            var pitches = new List<double>();
            for (int i = 1; i < lines.Count; i++) pitches.Add(Math.Abs(lines[i - 1].Bottom - lines[i].Bottom));
            double pitch = 0;
            if (pitches.Count >= 2) { pitches.Sort(); pitch = pitches[pitches.Count / 2]; }

            var cur = new List<EpubProtoLine> { lines[0] };
            for (int i = 1; i < lines.Count; i++)
            {
                var prev = lines[i - 1];
                var l = lines[i];
                bool styleChange = Math.Abs(l.Size - prev.Size) >= 1.0 || l.Bold != prev.Bold;
                bool gap = pitch > 0 && Math.Abs(prev.Bottom - l.Bottom) > pitch * 1.5;
                if (styleChange || gap) { yield return cur; cur = new List<EpubProtoLine>(); }
                cur.Add(l);
            }
            yield return cur;
        }

        // ── Heading, list and paragraph decisions ─────────────────────────────────────────────

        private static readonly Regex Bullet = new(@"^\s*[•·▪◦‣●○■□▶►\-–—*]\s+(?=\S)", RegexOptions.Compiled);
        private static readonly Regex Numbered = new(@"^\s*(?:\d{1,3}[.)]|\(\d{1,3}\)|[a-zA-Z]\))\s+(?=\S)", RegexOptions.Compiled);

        /// <summary>Body size is the commonest size by characters; sizes clearly above it become h1 to h3.</summary>
        internal static List<BookBlock> Classify(IReadOnlyList<EpubProtoBlock> protos)
        {
            var bySize = new Dictionary<double, int>();
            int boldChars = 0, allChars = 0;
            foreach (var p in protos)
                foreach (var l in p.Lines)
                {
                    bySize[l.Size] = bySize.GetValueOrDefault(l.Size) + l.Chars;
                    allChars += l.Chars; if (l.Bold) boldChars += l.Chars;
                }
            double body = bySize.Count == 0 ? 0 : bySize.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
            bool boldRuleOn = allChars > 0 && boldChars * 100 < allChars * 40;

            bool IsBig(EpubProtoBlock p) => body > 0 && p.Lines[0].Size >= body * 1.15 && p.Lines[0].Size >= body + 1
                                            && p.Lines.Count <= 3 && p.Lines.Sum(l => l.Chars) <= 200;
            var headSizes = protos.Where(IsBig).Select(p => p.Lines[0].Size).Distinct().OrderByDescending(s => s).ToList();

            var blocks = new List<BookBlock>();
            foreach (var p in protos)
            {
                string joined = string.Concat(p.Lines.Select(l => l.Text.TrimEnd() + " ")).Trim();
                if (IsBig(p) && !Bullet.IsMatch(joined))
                {
                    int level = Math.Min(3, headSizes.IndexOf(p.Lines[0].Size) + 1);
                    blocks.Add(Geometry(p, new BookBlock { Kind = BookBlockKind.Heading, Level = level, Runs = PlainRuns(JoinText(p.Lines)) }));
                    continue;
                }
                if (boldRuleOn && p.Lines.Count == 1 && p.Lines[0].Bold && p.Lines[0].Size >= body * 0.95
                    && joined.Length <= 80 && !Regex.IsMatch(joined, @"[.,;:]$") && !Bullet.IsMatch(joined))
                {
                    blocks.Add(Geometry(p, new BookBlock { Kind = BookBlockKind.Heading, Level = 4, Runs = PlainRuns(joined) }));
                    continue;
                }
                blocks.AddRange(BodyBlocks(p));
            }
            return blocks;
        }

        private static List<EpubRun> PlainRuns(string text) => new() { new EpubRun(text, false, false, null) };

        private static string JoinText(IEnumerable<EpubProtoLine> lines)
        {
            var runs = new List<EpubRun>();
            foreach (var l in lines) JoinLine(runs, l.Runs);
            return string.Concat(runs.Select(r => r.Text)).Trim();
        }

        private static IEnumerable<BookBlock> BodyBlocks(EpubProtoBlock p)
        {
            BookBlock? cur = null;
            double left = p.Left;
            EpubProtoLine? prev = null;
            var list = new List<BookBlock>();
            foreach (var line in p.Lines)
            {
                var runs = CloneRuns(line.Runs);
                bool marker = false, ordered = false;
                string t = line.Text;
                Match m;
                if ((m = Bullet.Match(t)).Success) { marker = true; }
                else if ((m = Numbered.Match(t)).Success) { marker = true; ordered = true; }

                bool indentBreak = prev != null && line.Left - left > Math.Max(8, line.Size * 1.2)
                                   && Regex.IsMatch(prev.Text.TrimEnd(), @"[.!?:”""')]$");
                if (marker)
                {
                    StripPrefix(runs, m!.Length);
                    cur = new BookBlock { Kind = BookBlockKind.ListItem, Ordered = ordered, Runs = new List<EpubRun>() };
                    list.Add(Geometry(p, cur));
                    JoinLine(cur.Runs, runs);
                }
                else if (cur == null || indentBreak)
                {
                    cur = new BookBlock { Kind = BookBlockKind.Paragraph, Runs = new List<EpubRun>() };
                    list.Add(Geometry(p, cur));
                    JoinLine(cur.Runs, runs);
                }
                else JoinLine(cur.Runs, runs);
                if (cur != null) { cur.Bottom = Math.Min(cur.Bottom, line.Bottom); cur.Top = Math.Max(cur.Top, line.Top); }
                prev = line;
            }
            return list;
        }

        private static BookBlock Geometry(EpubProtoBlock p, BookBlock b)
        {
            b.PageIndex = p.PageIndex; b.PageHeight = p.PageHeight;
            b.Top = p.Lines[0].Top; b.Bottom = p.Lines[0].Bottom; b.Left = p.Left; b.Right = p.Right;
            if (b.Kind == BookBlockKind.Heading) { b.Top = p.Top; b.Bottom = p.Bottom; }
            return b;
        }

        private static List<EpubRun> CloneRuns(List<EpubRun> runs) => new(runs);

        private static void StripPrefix(List<EpubRun> runs, int chars)
        {
            while (chars > 0 && runs.Count > 0)
            {
                var r = runs[0];
                if (r.Text.Length <= chars) { chars -= r.Text.Length; runs.RemoveAt(0); }
                else { runs[0] = r with { Text = r.Text[chars..] }; chars = 0; }
            }
        }

        /// <summary>
        /// Appends a line to a paragraph: a hyphen at the end of the previous line is dropped when the
        /// next line carries on in lower-case letters, otherwise the two are joined by one space.
        /// </summary>
        internal static void JoinLine(List<EpubRun> para, List<EpubRun> line)
        {
            if (line.Count == 0) return;
            if (para.Count > 0)
            {
                var last = para[^1];
                string lt = last.Text.TrimEnd();
                string first = line[0].Text;
                if (lt.Length >= 2 && lt[^1] == '-' && char.IsLetter(lt[^2]) && first.Length > 0 && char.IsLower(first[0]))
                    para[^1] = last with { Text = lt[..^1] };
                else if (!last.Text.EndsWith(' ')) para[^1] = last with { Text = lt + " " };
            }
            foreach (var r in line) AddRun(para, r);
            if (para.Count > 0) para[^1] = para[^1] with { Text = para[^1].Text.TrimEnd() };
        }

        // ── Running heads and page numbers ────────────────────────────────────────────────────

        /// <summary>
        /// Drops blocks that repeat in the top or bottom margin on at least 40% of the pages (at least
        /// three), comparing text with digits and punctuation removed so "Page 3" and "Page 4" match.
        /// Blocks that came from a structure tree are kept: a tagged PDF already leaves these out.
        /// </summary>
        // Never dropped: tagged content, pictures, tables, long text, and real headings (a "Chapter 1"
        // at the top of each chapter's first page looks like a running head once its digits are removed).
        private static bool Exempt(BookBlock b) =>
            b.FromTags || b.Kind is BookBlockKind.Figure or BookBlockKind.Table || b.Text.Length > 120
            || (b.Kind == BookBlockKind.Heading && b.Level <= 3);

        internal static List<BookBlock> DropRunningHeads(List<BookBlock> blocks, int pageCount)
        {
            if (pageCount < 3) return blocks;
            string Key(BookBlock b)
            {
                string t = b.Text;
                var letters = new string(t.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());
                return letters.Length > 0 ? letters : (t.Any(char.IsDigit) ? "#number" : "");
            }
            int Band(BookBlock b)
            {
                if (b.PageHeight <= 0) return 0;
                double c = ((b.Top + b.Bottom) / 2) / b.PageHeight;
                return c >= 0.88 ? 1 : c <= 0.12 ? -1 : 0;
            }
            var pagesByKey = new Dictionary<(string, int), HashSet<int>>();
            foreach (var b in blocks)
            {
                if (Exempt(b)) continue;
                int band = Band(b); string key = Key(b);
                if (band == 0 || key.Length == 0) continue;
                if (!pagesByKey.TryGetValue((key, band), out var set)) pagesByKey[(key, band)] = set = new();
                set.Add(b.PageIndex);
            }
            double min = Math.Max(3, pageCount * 0.4);
            var drop = pagesByKey.Where(kv => kv.Value.Count >= min).Select(kv => kv.Key).ToHashSet();
            if (drop.Count == 0) return blocks;
            return blocks.Where(b => Exempt(b) || !drop.Contains((Key(b), Band(b)))).ToList();
        }

        // ── Pictures in an untagged page ──────────────────────────────────────────────────────

        /// <summary>Puts each picture before the first block below its top edge that shares its columns.</summary>
        internal static List<BookBlock> PlaceFigures(List<BookBlock> pageBlocks, List<(PdfRectangle Bounds, string Id)> figures, int pageIndex, double pageHeight)
        {
            var result = new List<BookBlock>(pageBlocks);
            foreach (var (bounds, id) in figures.OrderByDescending(f => f.Bounds.Top))
            {
                var fig = new BookBlock
                {
                    Kind = BookBlockKind.Figure, ImageId = id, Alt = "", PageIndex = pageIndex, PageHeight = pageHeight,
                    Top = bounds.Top, Bottom = bounds.Bottom, Left = bounds.Left, Right = bounds.Right,
                };
                int at = result.FindIndex(b => b.Kind != BookBlockKind.Figure && b.Top <= bounds.Top - 1 && Overlap(b, bounds) >= 0.3);
                if (at < 0) result.Add(fig); else result.Insert(at, fig);
            }
            return result;
        }

        private static double Overlap(BookBlock b, PdfRectangle r)
        {
            double w = Math.Min(b.Right, r.Right) - Math.Max(b.Left, r.Left);
            double d = Math.Min(b.Right - b.Left, r.Right - r.Left);
            return d <= 0 ? 0 : Math.Max(0, w) / d;
        }

        // ── Tagged pages ──────────────────────────────────────────────────────────────────────

        private sealed class McContent
        {
            internal readonly List<Letter> Letters = new();
            internal readonly List<PdfRectangle> ImageBoxes = new();
            internal readonly List<PdfRectangle> PathBoxes = new();
        }

        /// <summary>
        /// Builds the blocks of one page from its structure tree. Returns null when the tree does not
        /// account for the page's text (a partly tagged page), so the caller reads it as untagged.
        /// </summary>
        internal static List<BookBlock>? AnalyzeTagged(Page page, int pageIndex,
            List<PdfiumInterop.EpubStructNode> roots, Func<PdfRectangle, string?> makePicture)
        {
            var map = new Dictionary<int, McContent>();
            int artifactLetters = 0;
            try
            {
                foreach (var e in page.GetMarkedContents()) FlattenMarked(e, map, ref artifactLetters);
            }
            catch { return null; }

            int totalLetters = page.Letters.Count(l => !string.IsNullOrWhiteSpace(l.Value));
            var visited = new HashSet<int>();
            var blocks = new List<BookBlock>();
            double ph = page.Height;

            List<Letter> LettersOf(List<int> mcids)
            {
                var res = new List<Letter>();
                foreach (int id in mcids) if (map.TryGetValue(id, out var c)) res.AddRange(c.Letters);
                return res;
            }
            void Mark(List<int> mcids) { foreach (int id in mcids) visited.Add(id); }

            BookBlock TextBlock(BookBlockKind kind, PdfiumInterop.EpubStructNode n, List<int> mcids)
            {
                var letters = LettersOf(mcids);
                var b = new BookBlock { Kind = kind, PageIndex = pageIndex, PageHeight = ph, FromTags = true };
                if (!string.IsNullOrWhiteSpace(n.ActualText))
                    b.Runs = PlainRuns(n.ActualText!.Trim());
                else
                    b.Runs = RunsFromLetters(letters);
                SetBox(b, letters);
                return b;
            }

            void Emit(PdfiumInterop.EpubStructNode n, int depth)
            {
                if (depth > 64) return;
                string t = n.Type.ToUpperInvariant();
                var all = Gather(n);
                switch (t)
                {
                    case "H1": case "H2": case "H3": case "H4": case "H5": case "H6":
                    case "H": case "TITLE":
                    {
                        var b = TextBlock(BookBlockKind.Heading, n, all);
                        b.Level = t == "H" ? 2 : t == "TITLE" ? 1 : t[1] - '0';
                        b.Runs = PlainRuns(b.Text);
                        Mark(all); if (b.Text.Length > 0) blocks.Add(b);
                        return;
                    }
                    case "LI":
                    {
                        var b = TextBlock(BookBlockKind.ListItem, n, all);
                        string text = b.Text;
                        var m = Bullet.Match(text); bool ord = false;
                        if (!m.Success) { m = Numbered.Match(text); ord = m.Success; }
                        if (m.Success) StripPrefix(b.Runs, m.Length);
                        b.Ordered = ord;
                        Mark(all); if (b.Text.Length > 0) blocks.Add(b);
                        return;
                    }
                    case "FIGURE":
                    {
                        var boxes = new List<PdfRectangle>();
                        foreach (int id in all) if (map.TryGetValue(id, out var c)) boxes.AddRange(c.ImageBoxes.Count > 0 ? c.ImageBoxes : c.PathBoxes);
                        Mark(all);
                        string? imageId = boxes.Count > 0 ? makePicture(Union(boxes)) : null;
                        string alt = (n.Alt ?? "").Trim();
                        if (imageId is null && alt.Length == 0) return;
                        if (imageId is null)
                        {
                            blocks.Add(new BookBlock { Kind = BookBlockKind.Paragraph, Runs = PlainRuns(alt), PageIndex = pageIndex, PageHeight = ph, FromTags = true });
                            return;
                        }
                        var u = Union(boxes);
                        blocks.Add(new BookBlock
                        {
                            Kind = BookBlockKind.Figure, ImageId = imageId, Alt = alt, PageIndex = pageIndex, PageHeight = ph,
                            Top = u.Top, Bottom = u.Bottom, Left = u.Left, Right = u.Right, FromTags = true,
                        });
                        return;
                    }
                    case "TABLE":
                    {
                        var rows = new List<List<(string, bool)>>();
                        CollectRows(n, rows, map);
                        Mark(all);
                        if (rows.Count > 0)
                        {
                            var letters = LettersOf(all);
                            var b = new BookBlock { Kind = BookBlockKind.Table, Rows = rows, PageIndex = pageIndex, PageHeight = ph, FromTags = true };
                            SetBox(b, letters);
                            blocks.Add(b);
                            return;
                        }
                        break;   // no rows: read it as ordinary structure
                    }
                    case "P": case "CAPTION": case "BLOCKQUOTE": case "QUOTE": case "CODE": case "NOTE": case "FORMULA":
                    case "LBL": case "SPAN": case "LINK": case "REFERENCE": case "BIBENTRY": case "TD": case "TH": case "LBODY":
                    {
                        var b = TextBlock(BookBlockKind.Paragraph, n, all);
                        Mark(all); if (b.Text.Length > 0) blocks.Add(b);
                        return;
                    }
                }

                // Containers: the content they hold directly first, then their children in order.
                var direct = n.Children.OfType<int>().ToList();
                if (direct.Count > 0)
                {
                    var b = TextBlock(BookBlockKind.Paragraph, new PdfiumInterop.EpubStructNode(), direct);
                    Mark(direct); if (b.Text.Length > 0) blocks.Add(b);
                }
                foreach (var child in n.Children.OfType<PdfiumInterop.EpubStructNode>()) Emit(child, depth + 1);
            }

            foreach (var r in roots) Emit(r, 0);

            // Letters the tree did not account for, apart from artifacts: this page is not really tagged.
            int covered = 0;
            foreach (int id in visited) if (map.TryGetValue(id, out var c)) covered += c.Letters.Count(l => !string.IsNullOrWhiteSpace(l.Value));
            int uncovered = totalLetters - covered - artifactLetters;
            if (blocks.Count == 0 || (uncovered > 3 && uncovered * 20 > totalLetters)) return null;
            return blocks;
        }

        private static List<int> Gather(PdfiumInterop.EpubStructNode n)
        {
            var res = new List<int>();
            void Rec(PdfiumInterop.EpubStructNode x, int d)
            {
                if (d > 64) return;
                foreach (var c in x.Children)
                {
                    if (c is int i) res.Add(i);
                    else if (c is PdfiumInterop.EpubStructNode child) Rec(child, d + 1);
                }
            }
            Rec(n, 0);
            return res;
        }

        private static void CollectRows(PdfiumInterop.EpubStructNode n, List<List<(string, bool)>> rows, Dictionary<int, McContent> map)
        {
            foreach (var c in n.Children.OfType<PdfiumInterop.EpubStructNode>())
            {
                string t = c.Type.ToUpperInvariant();
                if (t == "TR")
                {
                    var cells = new List<(string, bool)>();
                    foreach (var cell in c.Children.OfType<PdfiumInterop.EpubStructNode>())
                    {
                        string ct = cell.Type.ToUpperInvariant();
                        if (ct is not ("TD" or "TH")) continue;
                        string text = !string.IsNullOrWhiteSpace(cell.ActualText) ? cell.ActualText!.Trim()
                            : string.Concat(RunsFromLetters(Gather(cell).SelectMany(id => map.TryGetValue(id, out var m) ? m.Letters : new List<Letter>()).ToList()).Select(r => r.Text)).Trim();
                        cells.Add((text, ct == "TH"));
                    }
                    if (cells.Count > 0) rows.Add(cells);
                }
                else CollectRows(c, rows, map);
            }
        }

        private static void FlattenMarked(MarkedContentElement e, Dictionary<int, McContent> map, ref int artifactLetters)
        {
            if (e.IsArtifact)
            {
                artifactLetters += CountLetters(e);
                return;
            }
            if (e.MarkedContentIdentifier >= 0)
            {
                if (!map.TryGetValue(e.MarkedContentIdentifier, out var c)) map[e.MarkedContentIdentifier] = c = new McContent();
                AddAll(c, e, 0);
            }
            else
            {
                foreach (var child in e.Children) FlattenMarked(child, map, ref artifactLetters);
            }
        }

        private static int CountLetters(MarkedContentElement e) =>
            e.Letters.Count(l => !string.IsNullOrWhiteSpace(l.Value)) + e.Children.Sum(CountLetters);

        private static void AddAll(McContent c, MarkedContentElement e, int depth)
        {
            c.Letters.AddRange(e.Letters);
            foreach (var img in e.Images) c.ImageBoxes.Add(img.BoundingBox);
            foreach (var p in e.Paths)
            {
                try { if (p.GetBoundingRectangle() is { } r) c.PathBoxes.Add(r); } catch { }
            }
            if (depth < 32) foreach (var child in e.Children) AddAll(c, child, depth + 1);
        }

        private static PdfRectangle Union(List<PdfRectangle> boxes) => new(
            boxes.Min(b => Math.Min(b.Left, b.Right)), boxes.Min(b => Math.Min(b.Bottom, b.Top)),
            boxes.Max(b => Math.Max(b.Left, b.Right)), boxes.Max(b => Math.Max(b.Bottom, b.Top)));

        private static void SetBox(BookBlock b, List<Letter> letters)
        {
            var real = letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
            if (real.Count == 0) { b.Top = b.Bottom = b.PageHeight / 2; return; }
            b.Top = real.Max(l => l.BoundingBox.Top); b.Bottom = real.Min(l => l.BoundingBox.Bottom);
            b.Left = real.Min(l => l.BoundingBox.Left); b.Right = real.Max(l => l.BoundingBox.Right);
        }

        /// <summary>The letters of one structure element as styled runs, words spaced by position, lines hyphen-joined.</summary>
        internal static List<EpubRun> RunsFromLetters(List<Letter> letters)
        {
            var runs = new List<EpubRun>();
            var real = letters.Where(l => !string.IsNullOrEmpty(l.Value)).ToList();
            if (real.Count == 0) return runs;
            List<Word> words;
            try { words = NearestNeighbourWordExtractor.Instance.GetWords(real).Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList(); }
            catch { return runs; }

            // Content order is the author's reading order; a new line starts when the baseline moves.
            var line = new List<Word>();
            void Flush() { if (line.Count > 0) { JoinLine(runs, MakeLine(line).Runs); line.Clear(); } }
            foreach (var w in words)
            {
                if (line.Count > 0)
                {
                    var p = line[^1].BoundingBox;
                    bool newLine = Math.Abs(p.Bottom - w.BoundingBox.Bottom) > Math.Max(1, p.Height * 0.5);
                    if (newLine) Flush();
                }
                line.Add(w);
            }
            Flush();
            return runs;
        }
    }
}
