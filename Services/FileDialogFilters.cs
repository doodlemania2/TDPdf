namespace TDPdf.Services
{
    /// <summary>
    /// The filter strings every file dialog uses, in one place. WPF-free so tests can pin them.
    /// </summary>
    /// <remarks>
    /// EXTENSION POINT for new document types: add the type to <see cref="ExtraOpenTypes"/> and it
    /// appears in File > Open as its own entry and in the leading "All supported documents" entry.
    /// Merge, Compare and the other PDF-only dialogs keep using <see cref="Pdf"/>.
    /// </remarks>
    internal static class FileDialogFilters
    {
        public const string Pdf = "PDF files|*.pdf";
        public const string Images = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*";
        public const string ImagesBasic = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*";
        public const string FormData = "Form data (*.fdf;*.xfdf)|*.fdf;*.xfdf|All files|*.*";

        /// <summary>
        /// Document types File > Open accepts besides PDF, as (name, pattern) pairs, for example
        /// ("EPUB books", "*.epub").
        /// </summary>
        public static readonly (string Name, string Pattern)[] ExtraOpenTypes =
        [
            ("EPUB books", "*.epub"),
            ("DjVu books", "*.djvu;*.djv"),
        ];

        /// <summary>The filter File > Open shows.</summary>
        public static string OpenDocuments => BuildOpenFilter(ExtraOpenTypes);

        /// <summary>
        /// "PDF files|*.pdf" when there are no extra types; otherwise an "All supported documents"
        /// entry first, then PDF, then each extra type.
        /// </summary>
        public static string BuildOpenFilter(IReadOnlyList<(string Name, string Pattern)> extras)
        {
            if (extras.Count == 0) return Pdf;
            var all = new List<string> { "*.pdf" };
            foreach (var e in extras) all.Add(e.Pattern);
            var entries = new List<(string, string)>
            {
                ("All supported documents", string.Join(";", all)),
                ("PDF files", "*.pdf"),
            };
            entries.AddRange(extras);
            return FileDialogPolicy.BuildFilter(entries);
        }
    }
}
