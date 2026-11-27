namespace TDPdf.Services
{
    /// <summary>One line of a reflowed paragraph.</summary>
    /// <param name="Text">The line as drawn, without its trailing space.</param>
    /// <param name="Start">Offset of the first character in the text that was reflowed.</param>
    /// <param name="Length">Number of characters of that text the line shows.</param>
    internal readonly record struct ReflowLine(string Text, int Start, int Length);

    /// <summary>
    /// Greedy line breaking: put as many words on each line as fit, then start another.
    /// </summary>
    /// <remarks>
    /// Typed newlines are hard breaks and are kept. There is no hyphenation: a word wider than the
    /// column is split at the character that no longer fits, because a line that overflows into the
    /// next column is worse than an ugly break. The width function is the caller's, which is what
    /// lets the same breaker work against the document's own font (measured by PDFium) and against
    /// an installed one (measured by WPF).
    /// </remarks>
    internal static class ParagraphReflow
    {
        /// <param name="text">The paragraph. '\n' is a hard break; '\r' is ignored.</param>
        /// <param name="firstLineWidth">Width available to the first line (the column minus an indent).</param>
        /// <param name="bodyWidth">Width available to every other line.</param>
        /// <param name="measure">Width of a string in the font the lines will be drawn in.</param>
        internal static List<ReflowLine> Break(
            string text, double firstLineWidth, double bodyWidth, Func<string, double> measure)
        {
            var lines = new List<ReflowLine>();
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');

            int offset = 0;
            foreach (string hard in text.Split('\n'))
            {
                BreakSegment(hard, offset, lines, firstLineWidth, bodyWidth, measure);
                offset += hard.Length + 1;
            }
            return lines;
        }

        private static void BreakSegment(
            string segment, int offset, List<ReflowLine> lines,
            double firstLineWidth, double bodyWidth, Func<string, double> measure)
        {
            if (segment.Length == 0 || segment.All(char.IsWhiteSpace))
            {
                // A blank line the user typed on purpose.
                lines.Add(new ReflowLine("", offset, 0));
                return;
            }

            // Words with the whitespace that follows them, as [start, end) in the segment.
            var words = new List<(int Start, int End)>();
            int i = 0;
            while (i < segment.Length)
            {
                while (i < segment.Length && char.IsWhiteSpace(segment[i])) i++;
                if (i >= segment.Length) break;
                int s = i;
                while (i < segment.Length && !char.IsWhiteSpace(segment[i])) i++;
                words.Add((s, i));
            }

            int lineStart = -1, lineEnd = -1;
            bool firstOfSegment = true;
            double LimitFor(bool first) => first && lines.Count == 0 ? firstLineWidth : bodyWidth;

            void Flush()
            {
                if (lineStart < 0) return;
                lines.Add(new ReflowLine(segment.Substring(lineStart, lineEnd - lineStart), offset + lineStart, lineEnd - lineStart));
                lineStart = -1;
                firstOfSegment = false;
            }

            foreach (var (ws, we) in words)
            {
                int curStart = ws;
                // A word wider than a whole line is cut where it stops fitting.
                while (true)
                {
                    if (lineStart < 0)
                    {
                        double limit = LimitFor(firstOfSegment);
                        if (measure(segment.Substring(curStart, we - curStart)) <= limit || we - curStart <= 1)
                        {
                            lineStart = curStart; lineEnd = we;
                            break;
                        }
                        int cut = FitPrefix(segment, curStart, we, limit, measure);
                        lines.Add(new ReflowLine(segment.Substring(curStart, cut - curStart), offset + curStart, cut - curStart));
                        firstOfSegment = false;
                        curStart = cut;
                        continue;
                    }

                    double room = LimitFor(firstOfSegment);
                    if (measure(segment.Substring(lineStart, we - lineStart)) <= room)
                    {
                        lineEnd = we;
                        break;
                    }
                    Flush();
                    // The word goes round again as the first of a fresh line.
                }
            }
            Flush();
        }

        /// <summary>The largest end offset such that [start, end) still fits, and at least one character.</summary>
        private static int FitPrefix(string s, int start, int end, double limit, Func<string, double> measure)
        {
            int best = start + 1;
            for (int e = start + 1; e <= end; e++)
            {
                if (measure(s.Substring(start, e - start)) > limit) break;
                best = e;
            }
            return best;
        }

        /// <summary>
        /// How far a line of <paramref name="lineWidth"/> sits from the column's left edge.
        /// Justified lines are set flush left.
        /// </summary>
        internal static double LeftOffset(ParagraphAlignment alignment, double lineWidth, double columnWidth) =>
            alignment switch
            {
                ParagraphAlignment.Centered => Math.Max(0, (columnWidth - lineWidth) / 2),
                ParagraphAlignment.Right => Math.Max(0, columnWidth - lineWidth),
                _ => 0,
            };
    }
}
