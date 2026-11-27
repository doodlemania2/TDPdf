namespace TDPdf.Services
{
    /// <summary>How the lines of a paragraph sit against its column.</summary>
    internal enum ParagraphAlignment
    {
        Left,
        Centered,
        Right,
        /// <summary>Both edges square. Reflowed as Left: neither engine can set word spacing.</summary>
        Justified,
    }

    /// <summary>One printed character of a line, in the displayed page frame.</summary>
    internal readonly record struct ParagraphChar(double Left, double Right);

    /// <summary>
    /// One visual line of text as the finder sees it. The frame is PdfPig's text frame, which is the
    /// page AS DISPLAYED: points, y up, origin at the bottom left of the turned page.
    /// </summary>
    internal sealed class ParagraphLine
    {
        public string Text { get; init; } = "";
        public double Left { get; init; }
        public double Right { get; init; }
        /// <summary>Baseline y. Line spacing is measured between baselines, because box tops move with ascenders.</summary>
        public double Baseline { get; init; }
        public double Top { get; init; }
        public double Bottom { get; init; }
        public double FontSize { get; init; }
        /// <summary>The font most of the line's letters use, with any subset prefix removed.</summary>
        public string FontName { get; init; } = "";
        /// <summary>True when more than one font or size appears in the line.</summary>
        public bool MixedStyles { get; init; }
        public bool RightToLeft { get; init; }
        /// <summary>Text colour of the first letter, as 0xRRGGBB.</summary>
        public int Color { get; init; }
        /// <summary>One entry per UTF-16 unit of <see cref="Text"/>; spaces take the gap between words.</summary>
        public IReadOnlyList<ParagraphChar> Chars { get; init; } = Array.Empty<ParagraphChar>();
    }

    /// <summary>A paragraph found on a page.</summary>
    internal sealed class ParagraphBlock
    {
        /// <summary>Indices into the page's line list, top to bottom.</summary>
        public IReadOnlyList<int> LineIndices { get; init; } = Array.Empty<int>();
        public IReadOnlyList<ParagraphLine> Lines { get; init; } = Array.Empty<ParagraphLine>();
        public ParagraphAlignment Alignment { get; init; }
        /// <summary>Left edge of the column the paragraph sits in (the body lines' left edge).</summary>
        public double ColumnLeft { get; init; }
        public double ColumnRight { get; init; }
        /// <summary>First line's left edge minus <see cref="ColumnLeft"/>: positive for an indent, negative for an outdent.</summary>
        public double FirstLineOffset { get; init; }
        /// <summary>Baseline-to-baseline distance; 0 for a one-line paragraph with no neighbour to read it from.</summary>
        public double Pitch { get; init; }
        public double FontSize { get; init; }
        public bool MixedStyles { get; init; }
        public bool RightToLeft { get; init; }
        public double Left { get; init; }
        public double Right { get; init; }
        public double Top { get; init; }
        public double Bottom { get; init; }
        /// <summary>The paragraph as one string: lines joined by a space, or by nothing after a line-end hyphen.</summary>
        public string Text { get; init; } = "";
        /// <summary>Offset in <see cref="Text"/> where each line starts.</summary>
        public IReadOnlyList<int> LineStarts { get; init; } = Array.Empty<int>();
    }

    /// <summary>
    /// Groups the lines of a page into paragraphs, from geometry alone: same column, steady line
    /// spacing, same font and size, and left edges that agree once an indent is allowed for.
    /// </summary>
    /// <remarks>
    /// Pure, and written against <see cref="ParagraphLine"/> rather than PdfPig, so the grouping is
    /// tested on hand-made geometry as well as on real PDFs. When it is unsure it errs towards the
    /// smaller paragraph: a paragraph that is too short can be edited line by line, but one that
    /// swallows its neighbour reflows text that was never selected.
    /// </remarks>
    internal static class ParagraphFinder
    {
        // Distances are in ems of the seed line's font size, so the same rules fit 8 pt footnotes
        // and 24 pt headings.
        private const double SizeTolerance = 0.12;
        private const double EdgeTolerance = 0.35;
        private const double CentreTolerance = 0.6;
        private const double PitchTolerance = 0.15;
        private const double MinPitch = 0.85;
        private const double MaxPitch = 2.4;

        /// <summary>The paragraph holding <paramref name="seed"/>, or null when the index is out of range or the line is blank.</summary>
        public static ParagraphBlock? Find(IReadOnlyList<ParagraphLine> lines, int seed)
        {
            if (seed < 0 || seed >= lines.Count) return null;
            var seedLine = lines[seed];
            if (string.IsNullOrWhiteSpace(seedLine.Text) || seedLine.FontSize <= 0) return null;
            double em = seedLine.FontSize;

            // ---- 1. The stack: lines directly above and below that could belong to one block of
            // text, ignoring (for now) where each starts. ------------------------------------
            var above = Walk(lines, seed, up: true);
            var below = Walk(lines, seed, up: false);

            var gaps = new List<double>();
            int prev = seed;
            foreach (int i in above) { gaps.Add(lines[i].Baseline - lines[prev].Baseline); prev = i; }
            prev = seed;
            foreach (int i in below) { gaps.Add(lines[prev].Baseline - lines[i].Baseline); prev = i; }
            double pitch = gaps.Count == 0 ? 0 : Median(gaps);

            // Keep only the part of the stack whose spacing matches the pitch.
            var chain = new List<int>();
            {
                var up = new List<int>();
                double last = lines[seed].Baseline;
                foreach (int i in above)
                {
                    double gap = lines[i].Baseline - last;
                    if (!GapMatches(gap, pitch)) break;
                    up.Add(i); last = lines[i].Baseline;
                }
                up.Reverse();
                chain.AddRange(up);
                chain.Add(seed);
                last = lines[seed].Baseline;
                foreach (int i in below)
                {
                    double gap = last - lines[i].Baseline;
                    if (!GapMatches(gap, pitch)) break;
                    chain.Add(i); last = lines[i].Baseline;
                }
            }
            if (chain.Count == 1) pitch = 0;

            // ---- 2. Which alignment explains the chain, and where it breaks into paragraphs. --
            int seedPos = chain.IndexOf(seed);
            List<int> members;
            ParagraphAlignment alignment;

            var centreRun = Run(lines, chain, seedPos, (a, b) => Math.Abs(Mid(a) - Mid(b)) <= CentreTolerance * em);
            var rightRun = Run(lines, chain, seedPos, (a, b) => Math.Abs(a.Right - b.Right) <= EdgeTolerance * em);
            if (centreRun.Count >= 2 && LeftsVary(lines, centreRun, em) && !RightsAgree(lines, centreRun, em))
            {
                members = centreRun;
                alignment = ParagraphAlignment.Centered;
            }
            else if (rightRun.Count >= 2 && LeftsVary(lines, rightRun, em))
            {
                members = rightRun;
                alignment = ParagraphAlignment.Right;
            }
            else
            {
                members = SplitAtIndents(lines, chain, seedPos, em);
                alignment = LooksJustified(lines, members, em) ? ParagraphAlignment.Justified : ParagraphAlignment.Left;
            }

            return Build(lines, members, alignment, pitch);
        }

        private static ParagraphBlock Build(
            IReadOnlyList<ParagraphLine> all, List<int> members, ParagraphAlignment alignment, double pitch)
        {
            var ls = members.Select(i => all[i]).ToList();
            var first = ls[0];
            double em = first.FontSize;

            double bodyLeft = ls.Count > 1 ? Mode(ls.Skip(1).Select(l => l.Left), em * EdgeTolerance) : first.Left;
            // Justified and left-aligned body lines share a left edge; if the first line is further
            // left than the rest it is an outdent (hanging indent), and the body edge is still right.
            double colRight = ls.Max(l => l.Right);
            double colLeft = alignment == ParagraphAlignment.Left || alignment == ParagraphAlignment.Justified
                ? (ls.Count > 1 ? bodyLeft : first.Left)
                : ls.Min(l => l.Left);

            // A single line has no neighbour to read the column from. Lines elsewhere on the page that
            // start at the same left edge in the same size are the best evidence of the column's width.
            if (ls.Count == 1)
            {
                foreach (var other in all)
                {
                    if (ReferenceEquals(other, first)) continue;
                    if (Math.Abs(other.FontSize - first.FontSize) > SizeTolerance * em) continue;
                    if (Math.Abs(other.Left - first.Left) > EdgeTolerance * em) continue;
                    if (other.Right > colRight) colRight = other.Right;
                }
            }

            var starts = new List<int>();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < ls.Count; i++)
            {
                string text = ls[i].Text;
                if (i > 0)
                {
                    string prevText = ls[i - 1].Text;
                    bool hyphenJoin = prevText.Length > 1 && prevText[^1] == '-' && char.IsLetter(prevText[^2])
                                      && text.Length > 0 && char.IsLower(text[0]);
                    if (!hyphenJoin) sb.Append(' ');
                }
                starts.Add(sb.Length);
                sb.Append(text);
            }

            return new ParagraphBlock
            {
                LineIndices = members,
                Lines = ls,
                Alignment = alignment,
                ColumnLeft = colLeft,
                ColumnRight = colRight,
                FirstLineOffset = first.Left - colLeft,
                Pitch = pitch,
                FontSize = em,
                MixedStyles = ls.Any(l => l.MixedStyles) || ls.Any(l => !SameFont(l, first)),
                RightToLeft = ls.Any(l => l.RightToLeft),
                Left = ls.Min(l => l.Left),
                Right = ls.Max(l => l.Right),
                Top = ls.Max(l => l.Top),
                Bottom = ls.Min(l => l.Bottom),
                Text = sb.ToString(),
                LineStarts = starts,
            };
        }

        // ---- stack walking -------------------------------------------------------------------

        /// <summary>Lines stacked above (or below) <paramref name="from"/> that could continue its text, nearest first.</summary>
        private static List<int> Walk(IReadOnlyList<ParagraphLine> lines, int from, bool up)
        {
            var result = new List<int>();
            var seed = lines[from];
            int cur = from;
            for (int guard = 0; guard < lines.Count; guard++)
            {
                int next = Nearest(lines, cur, up);
                if (next < 0) break;
                var a = lines[cur];
                var b = lines[next];
                double gap = up ? b.Baseline - a.Baseline : a.Baseline - b.Baseline;
                double em = seed.FontSize;
                if (gap < MinPitch * em * 0.5 || gap > MaxPitch * em) break;
                if (Math.Abs(b.FontSize - seed.FontSize) > SizeTolerance * em) break;
                if (!SameFont(b, seed) && !b.MixedStyles && !seed.MixedStyles) break;
                result.Add(next);
                cur = next;
            }
            return result;
        }

        /// <summary>The closest line above or below that overlaps this one horizontally by at least half the narrower of the two.</summary>
        private static int Nearest(IReadOnlyList<ParagraphLine> lines, int from, bool up)
        {
            var a = lines[from];
            int best = -1;
            double bestGap = double.MaxValue;
            for (int i = 0; i < lines.Count; i++)
            {
                if (i == from) continue;
                var b = lines[i];
                if (string.IsNullOrWhiteSpace(b.Text)) continue;
                double gap = up ? b.Baseline - a.Baseline : a.Baseline - b.Baseline;
                if (gap <= a.FontSize * 0.3) continue;
                double overlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                double narrower = Math.Min(a.Right - a.Left, b.Right - b.Left);
                if (narrower <= 0 || overlap < narrower * 0.5) continue;
                if (gap < bestGap) { bestGap = gap; best = i; }
            }
            return best;
        }

        private static bool GapMatches(double gap, double pitch) =>
            pitch > 0 && Math.Abs(gap - pitch) <= Math.Max(PitchTolerance * pitch, 0.6);

        // ---- alignment ------------------------------------------------------------------------

        private static double Mid(ParagraphLine l) => (l.Left + l.Right) / 2;

        /// <summary>The run of consecutive chain lines around <paramref name="seedPos"/> that each agree with their neighbour.</summary>
        private static List<int> Run(
            IReadOnlyList<ParagraphLine> lines, List<int> chain, int seedPos,
            Func<ParagraphLine, ParagraphLine, bool> agree)
        {
            int lo = seedPos, hi = seedPos;
            while (lo > 0 && agree(lines[chain[lo - 1]], lines[chain[lo]])) lo--;
            while (hi < chain.Count - 1 && agree(lines[chain[hi]], lines[chain[hi + 1]])) hi++;
            return chain.GetRange(lo, hi - lo + 1);
        }

        private static bool LeftsVary(IReadOnlyList<ParagraphLine> lines, List<int> run, double em) =>
            run.Max(i => lines[i].Left) - run.Min(i => lines[i].Left) > EdgeTolerance * em;

        private static bool RightsAgree(IReadOnlyList<ParagraphLine> lines, List<int> run, double em) =>
            run.Max(i => lines[i].Right) - run.Min(i => lines[i].Right) <= EdgeTolerance * em;

        /// <summary>
        /// Splits a left-aligned chain where a new paragraph starts: a line indented against the one
        /// above it. The exception is a hanging indent, where the first line sits at the margin and
        /// every line after it is indented the same amount.
        /// </summary>
        private static List<int> SplitAtIndents(IReadOnlyList<ParagraphLine> lines, List<int> chain, int seedPos, double em)
        {
            double tol = EdgeTolerance * em;
            var starts = new List<int> { 0 };   // positions in chain where a paragraph begins
            for (int k = 1; k < chain.Count; k++)
            {
                double lk = lines[chain[k]].Left, lp = lines[chain[k - 1]].Left;
                int paraStart = starts[^1];
                int paraLen = k - paraStart;
                if (lk > lp + tol)
                {
                    // Indented against the line above.
                    bool hanging = paraLen == 1
                                   && k + 1 < chain.Count
                                   && Math.Abs(lines[chain[k + 1]].Left - lk) <= tol;
                    if (!hanging) starts.Add(k);
                }
                else if (lk < lp - tol)
                {
                    // Back out against the line above: the end of an indented first line (same
                    // paragraph) or the start of an outdented one. It is the first only when the
                    // paragraph so far is just that one line.
                    if (paraLen > 1) starts.Add(k);
                }
            }
            int s = starts.FindLastIndex(p => p <= seedPos);
            int from = starts[s];
            int to = s + 1 < starts.Count ? starts[s + 1] : chain.Count;
            return chain.GetRange(from, to - from);
        }

        /// <summary>Three or more lines whose right edges (bar the last) and left edges all agree.</summary>
        private static bool LooksJustified(IReadOnlyList<ParagraphLine> lines, List<int> members, double em)
        {
            if (members.Count < 3) return false;
            double tol = EdgeTolerance * em * 0.6;
            var body = members.Take(members.Count - 1).Select(i => lines[i]).ToList();
            // The first line may be indented, so the right edge decides it.
            double right = Median(body.Select(l => l.Right).ToList());
            if (body.Any(l => Math.Abs(l.Right - right) > tol)) return false;
            var last = lines[members[^1]];
            return last.Right <= right + tol;
        }

        // ---- small helpers --------------------------------------------------------------------

        private static bool SameFont(ParagraphLine a, ParagraphLine b) =>
            string.Equals(a.FontName, b.FontName, StringComparison.OrdinalIgnoreCase);

        private static double Median(List<double> values)
        {
            var s = values.OrderBy(v => v).ToList();
            return s.Count == 0 ? 0 : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
        }

        /// <summary>The most common value, with values within <paramref name="tol"/> counted as equal.</summary>
        private static double Mode(IEnumerable<double> values, double tol)
        {
            var list = values.ToList();
            double best = list[0];
            int bestCount = 0;
            foreach (double v in list)
            {
                int n = list.Count(w => Math.Abs(w - v) <= tol);
                if (n > bestCount) { bestCount = n; best = v; }
            }
            return best;
        }
    }
}
