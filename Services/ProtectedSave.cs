using System;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfSharpCore.Pdf.Security;

namespace TDPdf.Services
{
    /// <summary>
    /// Writes a document with the protection its source file had, and proves the result opens.
    /// </summary>
    /// <remarks>
    /// The live document is decrypted, so a plain <c>doc.Save</c> produces an unprotected file.
    /// The protection is applied for the one save and taken off again afterwards: the same document
    /// is also saved to temp snapshots (annotation bake/restore, flatten sources) that TDPdf reopens
    /// without a password, and those must stay plain.
    /// <para/>
    /// After writing, the staged file is reopened with the user password and its page count compared.
    /// A protected save is the only path that writes through PdfSharpCore's encryption, which has no
    /// other test than this; if it ever produced pages nobody can open, the throw happens before the
    /// atomic writer replaces the user's file.
    /// </remarks>
    internal static class ProtectedSave
    {
        /// <summary>
        /// A save delegate for the atomic writer: protected when <paramref name="protection"/> is
        /// non-null, a plain <c>doc.Save</c> otherwise.
        /// </summary>
        internal static Action<string> For(PdfDocument doc, PdfProtection? protection)
        {
            if (protection is null) return path => doc.Save(path);
            return path => Save(doc, protection, path);
        }

        internal static void Save(PdfDocument doc, PdfProtection protection, string path)
        {
            if (!protection.ApplyTo(doc))
            {
                doc.Save(path);
                return;
            }
            try
            {
                doc.Save(path);
            }
            finally
            {
                PdfProtection.Detach(doc);
            }

            int expected = doc.PageCount;
            using var check = PdfReader.Open(path, protection.UserPassword ?? string.Empty, PdfDocumentOpenMode.ReadOnly);
            if (check.OriginalProtection is null)
                throw new InvalidOperationException("The protected copy was written without its encryption.");
            if (check.PageCount != expected)
                throw new InvalidOperationException(
                    $"The protected copy has {check.PageCount} page(s) where {expected} were written.");
        }

        /// <summary>The status-bar note for a save that kept the file's protection.</summary>
        internal static string KeptNote(PdfProtection p)
        {
            bool ownerLost = !p.KeepsOwnerPassword && p.OwnerPassword is null;
            if (p.Downgraded && ownerLost)
                return " (password protection kept, as 128-bit RC4; the original owner password no longer applies)";
            if (p.Downgraded)
                return " (password protection kept, as 128-bit RC4)";
            if (ownerLost)
                return " (password protection kept; the original owner password no longer applies)";
            return " (password protection kept)";
        }
    }
}
