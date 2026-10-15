using System.Collections.Generic;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;

namespace TDPdf.Services
{
    /// <summary>
    /// Finds the digital signatures and usage rights in a document — the things a TDPdf save
    /// destroys.
    /// </summary>
    /// <remarks>
    /// Every TDPdf save rewrites the whole file through PdfSharpCore, which voids any digital
    /// signature in it: the signature covers a byte range that no longer exists. The save path
    /// therefore strips the signature values rather than shipping a broken one (PDF/A 6.4.3), and
    /// that is correct — a signature that fails validation is worse than none, because it looks
    /// like tampering rather than like an unsigned document.
    ///
    /// What was missing is that the user was never told. Open a signed PDF, press Ctrl+S, and the
    /// signature was gone with nothing on screen to say so.
    ///
    /// The COUNTING and the STRIPPING walk the same tree here, deliberately. They were written
    /// twice once before, and the warning would have gone stale the first time the scrub learned
    /// about a field shape the counter did not — warning about nothing, or worse, staying silent
    /// while still removing the signature.
    /// </remarks>
    internal static class PdfSignatureScan
    {
        /// <summary>Defensive depth cap: a malformed file can make /Kids circular.</summary>
        private const int MaxFieldDepth = 8;

        /// <summary>
        /// Every signature field in the document that actually carries a signature.
        /// </summary>
        /// <remarks>
        /// A signature FIELD with no <c>/V</c> is an empty placeholder waiting to be signed — a
        /// form with a signature box on it. Nothing is lost by rewriting one, so it is not counted
        /// and must not be warned about; the file is only interesting here once it has been signed.
        /// </remarks>
        internal static List<PdfDictionary> SignedFields(PdfDocument document)
        {
            var found = new List<PdfDictionary>();
            try
            {
                var catalog = document.Internals.Catalog;
                if (Deref(catalog.Elements["/AcroForm"]) is not PdfDictionary acroForm) return found;
                if (Deref(acroForm.Elements["/Fields"]) is not PdfArray fields) return found;
                Walk(fields, 0, found);
            }
            catch { /* malformed catalog: report what was reachable, never throw out of a save */ }
            return found;
        }

        private static void Walk(PdfArray fields, int depth, List<PdfDictionary> found)
        {
            if (depth > MaxFieldDepth) return;
            foreach (var item in fields.Elements)
            {
                if (item is null || Deref(item) is not PdfDictionary field) continue;
                if (field.Elements.GetName("/FT") == "/Sig" && field.Elements["/V"] is not null)
                    found.Add(field);
                if (Deref(field.Elements["/Kids"]) is PdfArray kids)
                    Walk(kids, depth + 1, found);
            }
        }

        /// <summary>
        /// True when the catalog carries <c>/Perms</c> — usage rights, which a rewrite also voids.
        /// </summary>
        /// <remarks>
        /// Separate from a signature because it is a separate loss and reads differently to a user:
        /// Reader-enabled rights are what let some documents be filled in and saved by Acrobat
        /// Reader at all. A file can carry them with no signed field, so this is asked on its own.
        /// </remarks>
        internal static bool HasUsageRights(PdfDocument document)
        {
            try { return document.Internals.Catalog.Elements["/Perms"] is not null; }
            catch { return false; }
        }

        /// <summary>
        /// Resolves an indirect reference to the object it points at, leaving anything else alone.
        /// </summary>
        private static PdfItem? Deref(PdfItem? item) =>
            item is PdfReference reference ? reference.Value : item;
    }
}
