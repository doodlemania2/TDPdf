using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;

namespace TDPdf.Services
{
    /// <summary>
    /// The one place that turns a PDF object reference into the object it points at. PdfReference is
    /// public in the vendored PdfSharpCore, so none of this needs reflection. Pure PdfSharpCore, no WPF,
    /// so the test project compiles this exact file.
    /// </summary>
    internal static class PdfItems
    {
        /// <summary>Maximum /Parent hops <see cref="Ancestors"/> follows (a /Parent cycle must not hang).</summary>
        internal const int MaxAncestorDepth = 64;

        /// <summary>
        /// The object an indirect reference points at (null when the reference is dangling), or the
        /// item itself when it is already direct (or null).
        /// </summary>
        internal static PdfItem? Deref(PdfItem? item) =>
            item is PdfReference r ? r.Value : item;

        /// <summary><see cref="Deref"/>, as a dictionary; null when it is anything else.</summary>
        internal static PdfDictionary? DerefDict(PdfItem? item) => Deref(item) as PdfDictionary;

        /// <summary>The object number of an indirect reference, or -1 for a direct item or null.</summary>
        internal static int ObjectNumber(PdfItem? item) =>
            item is PdfReference r ? r.ObjectNumber : -1;

        /// <summary>The dictionary a node's /Parent points at, or null.</summary>
        internal static PdfDictionary? ParentOf(PdfDictionary node) => DerefDict(node.Elements["/Parent"]);

        /// <summary>
        /// The node itself, then each /Parent above it. Stops at the root, after
        /// <paramref name="maxDepth"/> nodes, or the moment a node repeats, so a hostile /Parent cycle
        /// ends the walk instead of hanging the app.
        /// </summary>
        internal static IEnumerable<PdfDictionary> Ancestors(PdfDictionary? start, int maxDepth = MaxAncestorDepth)
        {
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            PdfDictionary? node = start;
            for (int d = 0; node is not null && d < maxDepth; d++)
            {
                if (!seen.Add(node)) yield break;
                yield return node;
                node = ParentOf(node);
            }
        }
    }
}
