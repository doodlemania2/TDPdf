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
            // The RC4 file key is derived from the first /ID, and the per-object keys from that key. A
            // document keeps its first /ID across edits, so every revision saved under the same
            // password would reuse the same keys for different content. Give each protected save a
            // fresh random first /ID before the security handler computes the keys at save time.
            doc.Internals.FirstDocumentID = NewDocumentId();
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

        /// <summary>16 random bytes as the raw string PdfSharpCore stores document IDs in.</summary>
        internal static string NewDocumentId()
        {
            byte[] id = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            return System.Text.Encoding.Latin1.GetString(id);
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
            /// <summary>
            /// The file was 40-bit RC4 (revision 2) and is written back as 40-bit RC4: no weaker than
            /// it was, but weak by any current standard, and a save is the moment to say so.
            /// </summary>
            Weak40Bit,
        }

        /// <summary>
        /// Pure form of <see cref="Assess(bool, PdfProtection?)"/>.
        /// <paramref name="reapplicable"/> is false when the protection could not be reproduced
        /// (an AES-256 file opened with its owner password, a PDFium-repaired or rasterised copy).
        /// </summary>
        internal static Impact Assess(bool wasProtected, bool reapplicable, bool downgraded, bool weak40Bit = false)
        {
            if (!wasProtected) return Impact.None;
            if (!reapplicable) return Impact.Removed;
            if (downgraded) return Impact.Downgraded;
            return weak40Bit ? Impact.Weak40Bit : Impact.None;
        }

        internal static Impact Assess(bool wasProtected, PdfProtection? protection) =>
            Assess(wasProtected, protection is { CanReapply: true }, protection?.Downgraded ?? false,
                   protection is { WrittenLevel: PdfDocumentSecurityLevel.Encrypted40Bit });

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
                case Impact.Weak40Bit:
                    return $"\"{fileName}\" is protected with 40-bit RC4, an old encryption that is weak by " +
                           "today's standards and can be broken quickly.\n\n" +
                           "Saving over it keeps that same weak protection, so it does not make the file any safer. " +
                           "To protect it properly, use Remove Password and then protect the document again " +
                           "with a stronger tool, saving the result as a separate file. " +
                           "The original is replaced and cannot be recovered.";
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
