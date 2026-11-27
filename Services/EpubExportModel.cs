using System.Text;

namespace TDPdf.Services
{
    /// <summary>A stretch of text in one style. <see cref="FontKey"/> names the PDF font (subset tag removed).</summary>
    internal sealed record EpubRun(string Text, bool Bold, bool Italic, string? FontKey);

    internal enum BookBlockKind { Heading, Paragraph, ListItem, Figure, Table }

    /// <summary>One piece of the book's content, in reading order.</summary>
    internal sealed class BookBlock
    {
        internal BookBlockKind Kind;
        /// <summary>Heading level 1 to 6.</summary>
        internal int Level;
        /// <summary>For a list item: true when the source numbered it.</summary>
        internal bool Ordered;
        internal List<EpubRun> Runs = new();
        internal string? ImageId;
        internal string? Alt;
        /// <summary>For a table: rows of cells; the bool marks a header cell.</summary>
        internal List<List<(string Text, bool Header)>>? Rows;
        internal int PageIndex;
        /// <summary>Vertical extent on the page, points, y up. Used for running heads and figure placement.</summary>
        internal double Top, Bottom, Left, Right, PageHeight;
        internal bool FromTags;

        internal string Text => string.Concat(Runs.Select(r => r.Text)).Trim();
    }

    internal sealed class EpubChapter
    {
        internal string Title = "";
        internal int FirstPage;
        internal List<BookBlock> Blocks = new();
    }

    /// <summary>One line of the table of contents. <see cref="Fragment"/> is an id inside the chapter, or null for its top.</summary>
    internal sealed record EpubNavEntry(string Title, int Level, int ChapterIndex, string? Fragment);

    internal sealed record EpubImage(string Id, byte[] Bytes, string Extension, string MediaType);

    internal sealed record EpubEmbeddedFont(string Key, string Family, byte[] Bytes, string Extension, string MediaType);

    internal sealed class EpubExportBook
    {
        internal string Title = "";
        internal string? Author;
        internal string Language = "en";
        internal string Identifier = "urn:uuid:" + Guid.NewGuid();
        internal DateTime ModifiedUtc = DateTime.UtcNow;
        internal List<EpubChapter> Chapters = new();
        internal List<EpubNavEntry> Nav = new();
        internal List<EpubImage> Images = new();
        internal List<EpubEmbeddedFont> Fonts = new();
    }

    internal enum EpubChapterSplit { Bookmarks, Headings, None }

    internal sealed class EpubExportOptions
    {
        internal bool IncludePictures = true;
        internal bool DropHeadersAndFooters = true;
        internal EpubChapterSplit Split = EpubChapterSplit.Bookmarks;
        /// <summary>Zero-based page indexes to export, or null for every page.</summary>
        internal IReadOnlyList<int>? Pages;
        internal bool EmbedFonts = true;
        /// <summary>When no bookmark or heading splits the book, a new chapter starts every this many pages.</summary>
        internal int PagesPerChapter = 25;
    }

    internal sealed class EpubExportResult
    {
        internal EpubExportBook Book = new();
        internal int Pages, TaggedPages, ScannedPages, PictureCount;
        internal List<string> EmbeddedFonts = new();
        /// <summary>Fonts left out, each with the reason, for the notice after the export.</summary>
        internal List<string> SkippedFonts = new();
        internal List<string> Notes = new();
    }

    /// <summary>XML text helpers: escaping and removal of characters XML 1.0 cannot carry.</summary>
    internal static class EpubExportXml
    {
        internal static string Escape(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default:
                        if (c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD))
                            sb.Append(c);
                        else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                        { sb.Append(c).Append(s[i + 1]); i++; }
                        break;   // anything else is not allowed in XML and is dropped
                }
            }
            return sb.ToString();
        }
    }
}
