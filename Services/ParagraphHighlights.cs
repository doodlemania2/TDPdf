namespace TDPdf.Services
{
    /// <summary>A line of a paragraph laid out on the canvas: its box, and where each character sits.</summary>
    /// <param name="Start">Offset of the line's first character in the paragraph text.</param>
    /// <param name="CharLeft">Left edge of each character, canvas pixels.</param>
    /// <param name="CharRight">Right edge of each character, canvas pixels.</param>
    internal sealed record HighlightLine(
        int Start, double Left, double Top, double Right, double Bottom,
        IReadOnlyList<double> CharLeft, IReadOnlyList<double> CharRight)
    {
        internal int Length => CharLeft.Count;
    }

    /// <summary>A plain rectangle on the canvas (x, y, width, height), kept free of WPF types.</summary>
    internal readonly record struct CanvasBox(double X, double Y, double Width, double Height)
    {
        internal double Right => X + Width;
        internal double Bottom => Y + Height;
    }

    /// <summary>The outcome for one highlight.</summary>
    /// <param name="Index">Which input highlight this answers.</param>
    /// <param name="Boxes">New boxes, one per new line the highlight now crosses. Empty when <see cref="Kept"/> is false.</param>
    /// <param name="Kept">False when the highlight did not belong to the paragraph, or what it covered is gone.</param>
    /// <param name="Touched">True when the highlight lay on the paragraph at all.</param>
    internal sealed record HighlightRemap(int Index, IReadOnlyList<CanvasBox> Boxes, bool Kept, bool Touched);

    /// <summary>
    /// Moves unsaved coloured highlights onto a paragraph's new lines.
    /// </summary>
    /// <remarks>
    /// A highlight is a rectangle, so first it is turned back into the characters it covered, then
    /// those characters are found again in the new text, then a rectangle is drawn on each new line
    /// they now fall on. Finding them again uses what did not change: the text before the edit and
    /// the text after it keep their meaning, and whatever sat inside the edit is clamped to the
    /// edit's new extent. A highlight that covered the whole paragraph covers the whole of it again.
    /// </remarks>
    internal static class ParagraphHighlights
    {
        /// <summary>
        /// Maps characters [start, end) of the old text to the new text.
        /// </summary>
        internal static (int Start, int End)? MapRange(string oldText, string newText, int start, int end)
        {
            if (end <= start) return null;
            if (start <= 0 && end >= oldText.Length) return newText.Length == 0 ? null : (0, newText.Length);

            int prefix = 0;
            int max = Math.Min(oldText.Length, newText.Length);
            while (prefix < max && oldText[prefix] == newText[prefix]) prefix++;
            int suffix = 0;
            while (suffix < max - prefix
                   && oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix]) suffix++;

            int delta = newText.Length - oldText.Length;
            int changedOldEnd = oldText.Length - suffix;    // old text [prefix, changedOldEnd) was replaced
            int changedNewEnd = newText.Length - suffix;

            int a = start >= changedOldEnd ? start + delta : start <= prefix ? start : prefix;
            int b = end <= prefix ? end : end >= changedOldEnd ? end + delta : changedNewEnd;
            a = Math.Clamp(a, 0, newText.Length);
            b = Math.Clamp(b, 0, newText.Length);
            return b > a ? (a, b) : null;
        }

        /// <summary>The characters of the old layout a box covers, as a half-open range of paragraph offsets.</summary>
        internal static (int Start, int End)? CoveredRange(IReadOnlyList<HighlightLine> oldLines, CanvasBox box)
        {
            int lo = int.MaxValue, hi = int.MinValue;
            foreach (var line in oldLines)
            {
                double h = line.Bottom - line.Top;
                double overlap = Math.Min(box.Bottom, line.Bottom) - Math.Max(box.Y, line.Top);
                if (h <= 0 || overlap < h * 0.5) continue;
                for (int i = 0; i < line.Length; i++)
                {
                    double mid = (line.CharLeft[i] + line.CharRight[i]) / 2;
                    if (mid < box.X || mid > box.Right) continue;
                    lo = Math.Min(lo, line.Start + i);
                    hi = Math.Max(hi, line.Start + i + 1);
                }
            }
            return lo == int.MaxValue ? null : (lo, hi);
        }

        /// <summary>
        /// Remaps each highlight in <paramref name="highlights"/> from the old layout to the new one.
        /// </summary>
        internal static List<HighlightRemap> Remap(
            string oldText, IReadOnlyList<HighlightLine> oldLines,
            string newText, IReadOnlyList<HighlightLine> newLines,
            IReadOnlyList<CanvasBox> highlights)
        {
            var result = new List<HighlightRemap>();
            for (int h = 0; h < highlights.Count; h++)
            {
                var box = highlights[h];
                var covered = CoveredRange(oldLines, box);
                if (covered is null)
                {
                    result.Add(new HighlightRemap(h, Array.Empty<CanvasBox>(), Kept: false, Touched: false));
                    continue;
                }

                // A highlight spans the paragraph's separators too (a space between two lines), so
                // widen "covers everything" to a range that touches the first and last character.
                var (cs, ce) = covered.Value;
                int firstChar = oldLines.Count > 0 ? oldLines[0].Start : 0;
                var lastLine = oldLines.Count > 0 ? oldLines[^1] : null;
                int lastChar = lastLine is null ? 0 : lastLine.Start + lastLine.Length;
                if (cs <= firstChar && ce >= lastChar) { cs = 0; ce = oldText.Length; }

                var mapped = MapRange(oldText, newText, cs, ce);
                if (mapped is null)
                {
                    result.Add(new HighlightRemap(h, Array.Empty<CanvasBox>(), Kept: false, Touched: true));
                    continue;
                }

                // Vertical inset of the highlight inside the line it sat on, kept on the new line.
                var anchor = oldLines.FirstOrDefault(l => Math.Min(box.Bottom, l.Bottom) - Math.Max(box.Y, l.Top) >= (l.Bottom - l.Top) * 0.5);
                double topInset = anchor is null ? 0 : box.Y - anchor.Top;
                double height = box.Height;

                var boxes = new List<CanvasBox>();
                foreach (var line in newLines)
                {
                    int a = Math.Max(mapped.Value.Start, line.Start);
                    int b = Math.Min(mapped.Value.End, line.Start + line.Length);
                    if (b <= a) continue;
                    double left = line.CharLeft[a - line.Start];
                    double right = line.CharRight[b - line.Start - 1];
                    boxes.Add(new CanvasBox(left, line.Top + topInset, Math.Max(right - left, 1), height));
                }
                result.Add(new HighlightRemap(h, boxes, Kept: boxes.Count > 0, Touched: true));
            }
            return result;
        }

        /// <summary>
        /// Builds a layout from text that was set in a known font: each character's edge is the width
        /// of the prefix that ends at it, offset by <paramref name="left"/>.
        /// </summary>
        internal static HighlightLine LayOut(
            int start, string lineText, double left, double top, double bottom, Func<string, double> measure)
        {
            var l = new double[lineText.Length];
            var r = new double[lineText.Length];
            double prev = 0;
            for (int i = 0; i < lineText.Length; i++)
            {
                double w = measure(lineText.Substring(0, i + 1));
                l[i] = left + prev;
                r[i] = left + w;
                prev = w;
            }
            return new HighlightLine(start, left, top, left + prev, bottom, l, r);
        }
    }
}
