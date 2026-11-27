namespace TDPdf.Services
{
    /// <summary>What a book conversion produced: the page count and what could not be kept.</summary>
    internal sealed record BookConversionResult(int Pages, IReadOnlyList<string> Notices);

    /// <summary>A book that cannot be converted, with a message fit to show to the person who opened it.</summary>
    internal sealed class BookConversionException : Exception
    {
        internal BookConversionException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>The kinds of book TDPdf converts to a PDF when it opens one.</summary>
    internal enum BookKind { None, Epub, Djvu }

    internal static class BookKinds
    {
        internal static readonly string[] EpubExtensions = { ".epub" };
        internal static readonly string[] DjvuExtensions = { ".djvu", ".djv" };

        internal static BookKind Of(string? path)
        {
            if (string.IsNullOrEmpty(path)) return BookKind.None;
            if (EpubExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return BookKind.Epub;
            if (DjvuExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return BookKind.Djvu;
            return BookKind.None;
        }

        /// <summary>The Open dialog's filter part for books, e.g. "*.epub;*.djvu;*.djv".</summary>
        internal static string FilterPattern => string.Join(";", EpubExtensions.Concat(DjvuExtensions).Select(e => "*" + e));

        /// <summary>The name a converted book is saved under by default: the book's own name with .pdf.</summary>
        internal static string PdfNameFor(string bookPath) => System.IO.Path.GetFileNameWithoutExtension(bookPath) + ".pdf";
    }
}
