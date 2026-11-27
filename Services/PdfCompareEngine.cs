namespace TDPdf.Services
{
    internal enum ChangeKind { TextAdded, TextRemoved, TextChanged, Visual, PageAdded, PageRemoved, PageMoved }

    /// <summary>
    /// One difference. <see cref="PairIndex"/> is the row of <see cref="CompareResult.Pairs"/> it belongs to;
    /// the boxes are on the left and right pages and carry their own page numbers, because a change in a
    /// paragraph that runs onto the next page spans two rows.
    /// </summary>
    internal sealed record CompareChange(
        ChangeKind Kind, int PairIndex,
        IReadOnlyList<PageBox> LeftBoxes, IReadOnlyList<PageBox> RightBoxes, string Summary);

    internal readonly record struct CompareProgress(string Stage, double Fraction);

    /// <summary>One side of a comparison once its text has been read.</summary>
    internal sealed class CompareDocument
    {
        internal CompareDocument(string path) { Path = path; }
        internal string Path { get; }
        internal List<CompareWord> Words { get; } = new();
        /// <summary>Index into <see cref="Words"/> of each page's first word; one extra entry at the end.</summary>
        internal List<int> PageStart { get; } = new() { 0 };
        /// <summary>Each page's size in points as displayed.</summary>
        internal List<(double W, double H)> PageSizes { get; } = new();
        internal List<PageFingerprint> Fingerprints { get; } = new();
        internal int PageCount => PageSizes.Count;

        internal IReadOnlyList<CompareWord> PageWords(int page)
        {
            int s = PageStart[page], e = PageStart[page + 1];
            return Words.GetRange(s, e - s);
        }
    }

    /// <summary>The outcome of comparing two documents: how their pages line up and every difference found.</summary>
    internal sealed class CompareResult
    {
        internal CompareResult(CompareDocument left, CompareDocument right, List<PagePair> pairs)
        {
            Left = left; Right = right; Pairs = pairs;
        }
        internal CompareDocument Left { get; }
        internal CompareDocument Right { get; }
        internal IReadOnlyList<PagePair> Pairs { get; }
        internal List<CompareChange> Changes { get; } = new();
        /// <summary>True when the documents differed too much for one word diff and each page pair was diffed on its own.</summary>
        internal bool DiffedPerPage { get; set; }
        /// <summary>The page and text changes as <see cref="PdfCompareEngine.Analyze"/> left them: a snapshot the background visual pass reads while the UI adds to <see cref="Changes"/>.</summary>
        internal IReadOnlyList<CompareChange> BaseChanges { get; set; } = Array.Empty<CompareChange>();

        /// <summary>Puts the changes in reading order: by row, then top to bottom.</summary>
        internal void SortChanges() =>
            Changes.Sort((a, b) =>
            {
                int c = a.PairIndex.CompareTo(b.PairIndex);
                if (c != 0) return c;
                c = FirstY(a).CompareTo(FirstY(b));
                return c != 0 ? c : a.Kind.CompareTo(b.Kind);
            });

        private static double FirstY(CompareChange c) =>
            c.LeftBoxes.Count > 0 ? c.LeftBoxes[0].Y : c.RightBoxes.Count > 0 ? c.RightBoxes[0].Y : 0;
    }

    /// <summary>
    /// Compares two PDFs: reads their words, pairs their pages, diffs the text word by word, and (page pair by
    /// page pair, on demand) renders both pages to find changes that are not text. Read-only; works on the paths it is given.
    /// </summary>
    internal static class PdfCompareEngine
    {
        /// <summary>Resolution of the picture comparison. Modest on purpose: it finds changes, it does not display them.</summary>
        internal const double VisualDpi = 100;
        /// <summary>No rendered side is longer than this many pixels, whatever the page size.</summary>
        internal const int MaxRenderPixels = 1600;
        private const int ThumbWidth = 48;

        /// <summary>Reads both documents, pairs their pages and finds every text and page change.</summary>
        internal static CompareResult Analyze(
            string leftPath, string rightPath, IProgress<CompareProgress>? progress, CancellationToken ct)
        {
            var left = Read(leftPath, "Reading the first document", 0.0, 0.3, progress, ct);
            var right = Read(rightPath, "Reading the second document", 0.3, 0.6, progress, ct);

            progress?.Report(new CompareProgress("Pairing pages", 0.6));
            var pairs = PdfCompareAlign.Align(left.Fingerprints, right.Fingerprints);
            ct.ThrowIfCancellationRequested();

            var result = new CompareResult(left, right, pairs);
            progress?.Report(new CompareProgress("Comparing text", 0.7));
            AddPageChanges(result);
            AddTextChanges(result, ct);
            result.SortChanges();
            result.BaseChanges = result.Changes.ToArray();
            progress?.Report(new CompareProgress("Done", 1));
            return result;
        }

        private static CompareDocument Read(
            string path, string stage, double from, double to, IProgress<CompareProgress>? progress, CancellationToken ct)
        {
            var doc = new CompareDocument(path);
            using (var pig = UglyToad.PdfPig.PdfDocument.Open(path))
            {
                int count = pig.NumberOfPages;
                for (int i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (i % 8 == 0) progress?.Report(new CompareProgress(stage, from + (to - from) * i / Math.Max(1, count) * 0.9));
                    double pw = 612, ph = 792;
                    try
                    {
                        var page = pig.GetPage(i + 1);
                        pw = page.Width; ph = page.Height;
                        foreach (var w in PdfTextSearch.ExtractWords(page))
                        {
                            string text = string.Concat(w.Letters.Select(l => l.Value)).Trim();
                            if (text.Length == 0) continue;
                            double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
                            foreach (var ch in w.Letters)
                            {
                                l = Math.Min(l, ch.Left); b = Math.Min(b, ch.Bottom);
                                r = Math.Max(r, ch.Right); t = Math.Max(t, ch.Top);
                            }
                            doc.Words.Add(new CompareWord(text, i, l, ph - t, Math.Max(0, r - l), Math.Max(0, t - b)));
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A page PdfPig cannot read counts as having no text; its picture is still compared.
                    }
                    doc.PageSizes.Add((pw, ph));
                    doc.PageStart.Add(doc.Words.Count);
                }
            }

            for (int i = 0; i < doc.PageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                int s = doc.PageStart[i], e = doc.PageStart[i + 1];
                byte[]? thumb = null;
                if (e - s < PdfCompareAlign.MinWordsForText) thumb = Thumbnail(path, i, doc.PageSizes[i]);
                doc.Fingerprints.Add(PdfCompareAlign.Fingerprint(
                    Enumerable.Range(s, e - s).Select(k => PdfCompareText.Token(doc.Words[k].Text)), thumb));
            }
            return doc;
        }

        private static byte[]? Thumbnail(string path, int pageIndex, (double W, double H) size)
        {
            try
            {
                int h = Math.Max(1, (int)Math.Round(ThumbWidth * size.H / Math.Max(1, size.W)));
                var bgra = PdfiumInterop.RenderPageWithAnnotations(path, pageIndex, ThumbWidth, h);
                return bgra is null ? null : PdfCompareAlign.Thumbnail16(bgra, ThumbWidth, h);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        }

        private static void AddPageChanges(CompareResult r)
        {
            for (int i = 0; i < r.Pairs.Count; i++)
            {
                var p = r.Pairs[i];
                switch (p.Kind)
                {
                    case PairKind.Added:
                        r.Changes.Add(new CompareChange(ChangeKind.PageAdded, i, Array.Empty<PageBox>(), Array.Empty<PageBox>(),
                            $"Page {p.Right!.Value + 1} added"));
                        break;
                    case PairKind.Removed:
                        r.Changes.Add(new CompareChange(ChangeKind.PageRemoved, i, Array.Empty<PageBox>(), Array.Empty<PageBox>(),
                            $"Page {p.Left!.Value + 1} removed"));
                        break;
                    case PairKind.Moved:
                        r.Changes.Add(new CompareChange(ChangeKind.PageMoved, i, Array.Empty<PageBox>(), Array.Empty<PageBox>(),
                            $"Page {p.Left!.Value + 1} moved to page {p.Right!.Value + 1}"));
                        break;
                }
            }
        }

        private static void AddTextChanges(CompareResult r, CancellationToken ct)
        {
            // Matched pages go through ONE diff, so a paragraph that flows from one page onto the next in
            // one version and not the other is not reported as a pile of changes at the page break.
            var matched = new List<(int Pair, PagePair P)>();
            var leftPageToPair = new Dictionary<int, int>();
            var rightPageToPair = new Dictionary<int, int>();
            for (int i = 0; i < r.Pairs.Count; i++)
            {
                var p = r.Pairs[i];
                if (p.Left is { } l) leftPageToPair[l] = i;
                if (p.Right is { } rr) rightPageToPair[rr] = i;
                if (p.Kind == PairKind.Matched) matched.Add((i, p));
            }

            var leftSeq = new List<CompareWord>();
            var rightSeq = new List<CompareWord>();
            foreach (var (_, p) in matched)
            {
                leftSeq.AddRange(r.Left.PageWords(p.Left!.Value));
                rightSeq.AddRange(r.Right.PageWords(p.Right!.Value));
            }

            var hunks = PdfCompareText.Compare(leftSeq, rightSeq);
            ct.ThrowIfCancellationRequested();
            if (hunks is not null)
            {
                foreach (var h in hunks)
                    AddHunk(r, leftSeq, rightSeq, h, leftPageToPair, rightPageToPair);
            }
            else
            {
                r.DiffedPerPage = true;
                foreach (var (idx, p) in matched)
                    DiffPair(r, idx, p, ct);
            }

            // A moved page is compared with the page it moved to, on its own.
            for (int i = 0; i < r.Pairs.Count; i++)
                if (r.Pairs[i].Kind == PairKind.Moved) DiffPair(r, i, r.Pairs[i], ct);
        }

        private static void DiffPair(CompareResult r, int pairIndex, PagePair p, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var a = r.Left.PageWords(p.Left!.Value);
            var b = r.Right.PageWords(p.Right!.Value);
            var hunks = PdfCompareText.Compare(a, b) ?? new List<DiffHunk> { new(0, a.Count, 0, b.Count) };
            var l2p = new Dictionary<int, int> { [p.Left!.Value] = pairIndex };
            var r2p = new Dictionary<int, int> { [p.Right!.Value] = pairIndex };
            foreach (var h in hunks) AddHunk(r, a, b, h, l2p, r2p);
        }

        private static void AddHunk(
            CompareResult r, IReadOnlyList<CompareWord> a, IReadOnlyList<CompareWord> b, DiffHunk h,
            Dictionary<int, int> leftPageToPair, Dictionary<int, int> rightPageToPair)
        {
            if (h.ALength == 0 && h.BLength == 0) return;
            int pair = 0;
            if (h.ALength > 0 && leftPageToPair.TryGetValue(a[h.AStart].Page, out int pl)) pair = pl;
            else if (h.BLength > 0 && rightPageToPair.TryGetValue(b[h.BStart].Page, out int pr)) pair = pr;

            var kind = h.ALength > 0 && h.BLength > 0 ? ChangeKind.TextChanged
                     : h.ALength > 0 ? ChangeKind.TextRemoved : ChangeKind.TextAdded;
            string removed = PdfCompareText.Excerpt(a, h.AStart, h.ALength);
            string added = PdfCompareText.Excerpt(b, h.BStart, h.BLength);
            string summary = kind switch
            {
                ChangeKind.TextChanged => $"\"{removed}\" to \"{added}\"",
                ChangeKind.TextRemoved => $"Removed \"{removed}\"",
                _ => $"Added \"{added}\"",
            };
            r.Changes.Add(new CompareChange(kind, pair,
                PdfCompareText.LineBoxes(a, h.AStart, h.ALength),
                PdfCompareText.LineBoxes(b, h.BStart, h.BLength), summary));
        }

        // ── Visual comparison ─────────────────────────────────────────────────────────────

        /// <summary>The scale (pixels per point) a pair of pages of the given size is rendered at.</summary>
        internal static double RenderScale(double widthPt, double heightPt, double dpi = VisualDpi)
        {
            double scale = dpi / 72.0;
            double longest = Math.Max(widthPt, heightPt) * scale;
            return longest > MaxRenderPixels ? MaxRenderPixels / Math.Max(widthPt, heightPt) : scale;
        }

        /// <summary>Renders one page at <paramref name="scale"/> pixels per point. Null when PDFium gives nothing.</summary>
        internal static (byte[] Bgra, int Width, int Height)? RenderPage(string path, int pageIndex, (double W, double H) sizePt, double scale)
        {
            int w = Math.Max(1, (int)Math.Round(sizePt.W * scale)), h = Math.Max(1, (int)Math.Round(sizePt.H * scale));
            var bgra = PdfiumInterop.RenderPageWithAnnotations(path, pageIndex, w, h);
            return bgra is { Length: > 0 } && bgra.Length >= w * h * 4 ? (bgra, w, h) : null;
        }

        /// <summary>
        /// The changes on one row that are not explained by text: areas where the two pages look different.
        /// Nothing for an added or removed page. Safe to call off the UI thread; holds two page rasters at a time.
        /// </summary>
        internal static List<CompareChange> FindVisual(CompareResult r, int pairIndex, CancellationToken ct, double dpi = VisualDpi)
        {
            var found = new List<CompareChange>();
            var p = r.Pairs[pairIndex];
            if (p.Left is not { } li || p.Right is not { } ri) return found;

            var ls = r.Left.PageSizes[li];
            var rs = r.Right.PageSizes[ri];
            double cw = Math.Max(ls.W, rs.W), ch = Math.Max(ls.H, rs.H);
            double scale = RenderScale(cw, ch, dpi);
            int canvasW = Math.Max(1, (int)Math.Round(cw * scale)), canvasH = Math.Max(1, (int)Math.Round(ch * scale));

            var a = RenderPage(r.Left.Path, li, ls, scale);
            ct.ThrowIfCancellationRequested();
            var b = RenderPage(r.Right.Path, ri, rs, scale);
            ct.ThrowIfCancellationRequested();
            if (a is null || b is null) return found;

            var pa = PdfCompareRaster.PadToCanvas(a.Value.Bgra, a.Value.Width, a.Value.Height, canvasW, canvasH);
            var pb = PdfCompareRaster.PadToCanvas(b.Value.Bgra, b.Value.Width, b.Value.Height, canvasW, canvasH);
            var regions = PdfCompareRaster.Regions(pa, pb, canvasW, canvasH);
            if (regions.Count == 0) return found;

            var covers = new List<PixelRect>();
            foreach (var c in r.BaseChanges)
            {
                foreach (var box in c.LeftBoxes) if (box.Page == li) covers.Add(ToPixels(box, scale));
                foreach (var box in c.RightBoxes) if (box.Page == ri) covers.Add(ToPixels(box, scale));
            }
            var kept = PdfCompareRaster.SubtractCovered(regions, covers);

            foreach (var region in kept)
            {
                double x = region.X / scale, y = region.Y / scale, w = region.W / scale, h = region.H / scale;
                found.Add(new CompareChange(ChangeKind.Visual, pairIndex,
                    new[] { new PageBox(li, x, y, w, h) }, new[] { new PageBox(ri, x, y, w, h) },
                    "Looks different"));
            }
            return found;
        }

        /// <summary>
        /// A text box in pixels, grown by a tile on every side: the picture diff works in whole tiles, so a
        /// region around a changed word is a little bigger than the word's own box.
        /// </summary>
        private static PixelRect ToPixels(PageBox b, double scale)
        {
            int pad = PdfCompareRaster.TileSize;
            int x = (int)Math.Floor(b.X * scale) - pad, y = (int)Math.Floor(b.Y * scale) - pad;
            return new PixelRect(x, y, (int)Math.Ceiling(b.W * scale) + 2 * pad, (int)Math.Ceiling(b.H * scale) + 2 * pad);
        }
    }
}
