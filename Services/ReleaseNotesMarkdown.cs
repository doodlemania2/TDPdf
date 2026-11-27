using System.Text;

namespace TDPdf.Services
{
    /// <summary>One styled run of text inside a block.</summary>
    internal readonly record struct NotesRun(string Text, bool Bold, bool Code);

    internal enum NotesBlockKind { Title, Heading, Paragraph, Bullet }

    /// <summary>One block of a release-notes document: a title, a heading, a paragraph or a bullet.</summary>
    internal sealed record NotesBlock(NotesBlockKind Kind, IReadOnlyList<NotesRun> Runs)
    {
        /// <summary>The block's text without styling.</summary>
        public string PlainText => string.Concat(Runs.Select(r => r.Text));
    }

    /// <summary>
    /// A tiny parser for the Markdown subset the files in <c>.github/release-notes/</c> use:
    /// <c># title</c>, <c>## heading</c> (deeper headings count as headings), <c>- bullets</c>,
    /// plain paragraphs (a <c>&gt;</c> quote marker is dropped), <c>**bold**</c> and
    /// <c>`code`</c>. A <c>[label](url)</c> link keeps its label. Pure text in, blocks out, so it is
    /// covered by <c>tests/PdfCore</c>; the WPF window only maps blocks to elements.
    /// </summary>
    internal static class ReleaseNotesMarkdown
    {
        public static IReadOnlyList<NotesBlock> Parse(string? markdown)
        {
            var blocks = new List<NotesBlock>();
            if (string.IsNullOrWhiteSpace(markdown)) return blocks;

            var para = new StringBuilder();
            void FlushParagraph()
            {
                if (para.Length == 0) return;
                blocks.Add(new NotesBlock(NotesBlockKind.Paragraph, ParseInline(para.ToString())));
                para.Clear();
            }

            foreach (string raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) { FlushParagraph(); continue; }

                if (line[0] == '#')
                {
                    FlushParagraph();
                    int level = 0;
                    while (level < line.Length && line[level] == '#') level++;
                    string text = line[level..].Trim();
                    if (text.Length == 0) continue;
                    blocks.Add(new NotesBlock(level == 1 ? NotesBlockKind.Title : NotesBlockKind.Heading, ParseInline(text)));
                }
                else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                {
                    FlushParagraph();
                    blocks.Add(new NotesBlock(NotesBlockKind.Bullet, ParseInline(line[2..].Trim())));
                }
                else
                {
                    if (line[0] == '>') line = line.TrimStart('>').TrimStart();
                    if (para.Length > 0) para.Append(' ');
                    para.Append(line);
                }
            }
            FlushParagraph();
            return blocks;
        }

        /// <summary>Splits one line into plain, bold and code runs. Unbalanced markers stay as literal text.</summary>
        public static IReadOnlyList<NotesRun> ParseInline(string text)
        {
            var runs = new List<NotesRun>();
            var sb = new StringBuilder();
            bool bold = false;

            void Flush(bool code = false)
            {
                if (sb.Length == 0) return;
                runs.Add(new NotesRun(sb.ToString(), bold, code));
                sb.Clear();
            }

            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '`')
                {
                    int end = text.IndexOf('`', i + 1);
                    if (end > i + 1)
                    {
                        Flush();
                        sb.Append(text, i + 1, end - i - 1);
                        Flush(code: true);
                        i = end + 1;
                        continue;
                    }
                }
                else if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    // Only toggle when a partner exists, so a stray ** is shown rather than swallowed.
                    if (bold || text.IndexOf("**", i + 2, StringComparison.Ordinal) > i + 2)
                    {
                        Flush();
                        bold = !bold;
                        i += 2;
                        continue;
                    }
                }
                else if (c == '[')
                {
                    int close = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                    int paren = close < 0 ? -1 : text.IndexOf(')', close + 2);
                    if (close > i + 1 && paren > close)
                    {
                        // Keep the label (which may itself carry bold) and drop the address.
                        string label = text[(i + 1)..close];
                        Flush();
                        foreach (var r in ParseInline(label))
                            runs.Add(r with { Bold = r.Bold || bold });
                        i = paren + 1;
                        continue;
                    }
                }
                sb.Append(c);
                i++;
            }
            Flush();
            return runs;
        }
    }
}
