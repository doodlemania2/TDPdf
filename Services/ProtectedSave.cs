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

        /// <summary>What an in-place save would do to the protection the file has on disk.</summary>
        internal enum Impact
        {
            /// <summary>Unprotected source, or its protection is written back at full strength.</summary>
            None,
            /// <summary>The save would leave the file with no password at all.</summary>
            Removed,
            /// <summary>The password is kept but the encryption is weaker (AES becomes 128-bit RC4).</summary>
            Downgraded,
        }

        /// <summary>
        /// Pure form of <see cref="Assess(bool, PdfProtection?)"/>.
        /// <paramref name="reapplicable"/> is false when the protection could not be reproduced
        /// (an AES-256 file opened with its owner password, a PDFium-repaired or rasterised copy).
        /// </summary>
        internal static Impact Assess(bool wasProtected, bool reapplicable, bool downgraded)
        {
            if (!wasProtected) return Impact.None;
            if (!reapplicable) return Impact.Removed;
            return downgraded ? Impact.Downgraded : Impact.None;
        }

        internal static Impact Assess(bool wasProtected, PdfProtection? protection) =>
            Assess(wasProtected, protection is { CanReapply: true }, protection?.Downgraded ?? false);

        /// <summary>
        /// The wording of the confirmation shown before an in-place save that would remove or weaken
        /// the file's protection. States exactly what will be written.
        /// </summary>
        internal static string ImpactMessage(Impact impact, string fileName, PdfProtection? p)
        {
            switch (impact)
            {
                case Impact.Removed:
                    return $"\"{fileName}\" is password protected, but TDPdf cannot write that protection back " +
                           "for this document.\n\n" +
                           "Saving over it will leave the file with NO password and no restrictions: anyone will " +
                           "be able to open it. The original is replaced and cannot be recovered.";
                case Impact.Downgraded:
                {
                    bool ownerLost = p is not null && !p.KeepsOwnerPassword && p.OwnerPassword is null;
                    return $"\"{fileName}\" uses AES encryption, which TDPdf cannot write.\n\n" +
                           "Saving over it keeps the password but re-encrypts the file as 128-bit RC4, a weaker " +
                           "and older scheme" +
                           (ownerLost ? ", and the original owner password will no longer work" : "") +
                           ". The original is replaced and cannot be recovered.";
                }
                default:
                    return "";
            }
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
