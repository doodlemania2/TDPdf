using PdfSharpCore.Pdf;

namespace TDPdf.Services
{
    /// <summary>One bookmark to write: a title, the 0-based page it opens and the bookmarks under it.</summary>
    internal sealed class OutlineNode
    {
        public string Title { get; init; } = "";
        public int PageIndex { get; init; }
        public List<OutlineNode> Children { get; } = new();
    }

    /// <summary>
    /// Turns a book's contents tree into PDF bookmarks. The target of each entry is found by name in the
    /// destinations Chromium wrote when it printed the page; an entry whose target cannot be found falls
    /// back to the start of its chapter, and then to the page of the entry before it, so the tree always
    /// keeps its shape and never points past the last page.
    /// </summary>
    internal static class EpubOutline
    {
        internal const int MaxNodes = 20000;

        /// <summary>
        /// The page an entry opens, from the printed document's named destinations: the element's own
        /// destination, else its chapter's. Null when neither is there.
        /// </summary>
        internal static Func<EpubTocEntry, int?> PageResolver(EpubBook book, IReadOnlyDictionary<string, int> destinationPages) =>
            entry =>
            {
                string? own = book.DestinationFor(entry.Path, entry.Fragment);
                if (own is not null && destinationPages.TryGetValue(own, out int p)) return p;
                string? chapter = book.DestinationFor(entry.Path, null);
                if (chapter is not null && destinationPages.TryGetValue(chapter, out int c)) return c;
                return null;
            };

        /// <summary>
        /// Writes the book's contents into a printed PDF as bookmarks, finding each target in the named
        /// destinations Chromium wrote. When none of the book's destination names are in the file there is
        /// nothing to aim at, and the outline is left out with a note rather than pointing everything at page 1.
        /// </summary>
        internal static bool ApplyTo(PdfDocument doc, EpubBook book, EpubPackage package, List<string> notices)
        {
            if (package.Toc.Count == 0 || doc.PageCount == 0) return false;
            var destinationPages = PdfNamedDestinations.BuildMap(doc);
            if (!destinationPages.Keys.Any(k => k.StartsWith("epub-c", StringComparison.Ordinal)))
            {
                notices.Add("The book's table of contents could not be placed on its pages, so it has no bookmarks.");
                return false;
            }
            var nodes = Build(package.Toc, PageResolver(book, destinationPages), doc.PageCount);
            return BookOutline.Apply(doc, nodes) > 0;
        }

        /// <summary>Builds the bookmark tree. <paramref name="pageOf"/> returns a page or null; nulls take the previous entry's page.</summary>
        internal static List<OutlineNode> Build(IReadOnlyList<EpubTocEntry> toc, Func<EpubTocEntry, int?> pageOf, int pageCount)
        {
            var result = new List<OutlineNode>();
            if (pageCount <= 0) return result;
            int remaining = MaxNodes;
            int last = 0;
            void Fill(IEnumerable<EpubTocEntry> entries, List<OutlineNode> into, int depth)
            {
                foreach (var e in entries)
                {
                    if (remaining-- <= 0 || depth >= EpubLimits.MaxTocDepth) return;
                    int page = Math.Clamp(pageOf(e) ?? last, 0, pageCount - 1);
                    last = page;
                    string title = string.IsNullOrWhiteSpace(e.Title) ? "Untitled" : e.Title.Trim();
                    var node = new OutlineNode { Title = title.Length > 200 ? title[..200] : title, PageIndex = page };
                    Fill(e.Children, node.Children, depth + 1);
                    into.Add(node);
                }
            }
            Fill(toc, result, 0);
            return result;
        }

        /// <inheritdoc cref="BookOutline.Apply"/>
        internal static int Apply(PdfDocument doc, IReadOnlyList<OutlineNode> nodes) => BookOutline.Apply(doc, nodes);
    }

    /// <summary>Writes a bookmark tree into a PDF's outline (shared by the EPUB and DjVu converters).</summary>
    internal static class BookOutline
    {
        /// <summary>Writes the tree into the document's outline; returns the number of bookmarks written.</summary>
        internal static int Apply(PdfDocument doc, IReadOnlyList<OutlineNode> nodes)
        {
            int count = 0;
            void Write(IReadOnlyList<OutlineNode> list, PdfOutlineCollection into)
            {
                foreach (var n in list)
                {
                    if (n.PageIndex < 0 || n.PageIndex >= doc.PageCount) continue;
                    var o = into.Add(n.Title, doc.Pages[n.PageIndex], opened: false);
                    count++;
                    Write(n.Children, o.Outlines);
                }
            }
            Write(nodes, doc.Outlines);
            return count;
        }
    }
}
