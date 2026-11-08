using System;
using PdfSharpCore.Pdf.Advanced;
using PdfSharpCore.Pdf.IO;
using PdfSharpCore.Pdf.Internal;

namespace PdfSharpCore.Pdf.Security
{
    /// <summary>
    /// TDPdf: the encryption a file had when it was opened, and the means to write it back.
    /// </summary>
    /// <remarks>
    /// PdfSharpCore decrypts on read and writes no /Encrypt unless a password is set, so without this
    /// every save of a protected file silently produced an unprotected one. The writer can only do
    /// the standard handler with RC4 (V1/R2 40-bit, V2/R3 128-bit); a file that was AES is written
    /// back as 128-bit RC4, which <see cref="Downgraded"/> reports.
    ///
    /// The owner password is never needed. When the original /O is the RC4 form (R2-R4) it is written
    /// back verbatim, so the owner's own password keeps working. Only an R5/R6 (AES-256) file has a
    /// /O that cannot be reused; there the owner password is the one the user typed, or a random one
    /// if they opened with the user password (the permissions then still bind but nobody holds the
    /// owner password).
    /// </remarks>
    public sealed class PdfProtection
    {
        /// <summary>The standard handler revision (/R) of the original.</summary>
        public int Revision { get; private set; }

        /// <summary>The original key length in bits.</summary>
        public int KeyBits { get; private set; }

        /// <summary>The original permission flags (/P).</summary>
        public int Permissions { get; private set; }

        /// <summary>True if the original used AES, which PdfSharpCore cannot write.</summary>
        public bool WasAes { get; private set; }

        /// <summary>The user password; null if it could not be established.</summary>
        public string UserPassword { get; private set; }

        /// <summary>The owner password if the user supplied it, else null.</summary>
        public string OwnerPassword { get; private set; }

        /// <summary>True if the file was opened with the user password only (no owner authority).</summary>
        public bool OpenedWithUserPasswordOnly { get; private set; }

        /// <summary>The original /O, raw; non-null only when it can be written back as-is.</summary>
        string _reusableOwnerValue;

        /// <summary>True if <see cref="ApplyTo"/> can reproduce a protection for this file.</summary>
        public bool CanReapply { get { return UserPassword != null; } }

        /// <summary>True when the written strength is weaker than the original (AES-128/256 become RC4-128).</summary>
        public bool Downgraded { get { return WasAes || Revision >= 5; } }

        /// <summary>True when the original owner password still works on the re-protected file.</summary>
        public bool KeepsOwnerPassword { get { return _reusableOwnerValue != null; } }

        /// <summary>The strength that will be written.</summary>
        public PdfDocumentSecurityLevel WrittenLevel
        {
            get { return Revision <= 2 ? PdfDocumentSecurityLevel.Encrypted40Bit : PdfDocumentSecurityLevel.Encrypted128Bit; }
        }

        internal static PdfProtection Capture(PdfStandardSecurityHandler handler, string typedPassword, PasswordValidity validity)
        {
            var p = new PdfProtection();
            p.Revision = handler.Elements.GetInteger(PdfStandardSecurityHandler.Keys.R);
            p.Permissions = handler.Elements.GetInteger(PdfStandardSecurityHandler.Keys.P);
            int v = handler.Elements.GetInteger(PdfSecurityHandler.Keys.V);
            int bits = handler.Elements.GetInteger(PdfSecurityHandler.Keys.Length);
            if (bits <= 0) bits = v >= 4 ? 128 : 40;
            p.KeyBits = bits;

            PdfDictionary cf = handler.Elements.GetDictionary(PdfSecurityHandler.Keys.CF);
            if (v >= 4 && cf != null)
            {
                foreach (string name in new[] { handler.Elements.GetName(PdfSecurityHandler.Keys.StmF), handler.Elements.GetName(PdfSecurityHandler.Keys.StrF) })
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    PdfDictionary f = cf.Elements.GetDictionary(name);
                    string cfm = f != null ? f.Elements.GetName(PdfSecurityHandler.Keys.CFM) : null;
                    if (cfm != null && cfm.StartsWith("/AESV")) p.WasAes = true;
                }
            }

            string typed = typedPassword ?? "";
            p.OpenedWithUserPasswordOnly = validity == PasswordValidity.UserPassword;
            if (validity == PasswordValidity.UserPassword)
                p.UserPassword = typed;
            else if (validity == PasswordValidity.OwnerPassword)
            {
                p.OwnerPassword = typed;
                p.UserPassword = handler.RecoveredUserPassword;   // null for R5/R6
            }

            // The RC4-form /O (R2-R4) can be written straight back when the written level matches the
            // one the file was keyed for: R2 is 40-bit, R3/R4 are 128-bit.
            string o = handler.Elements.GetString(PdfStandardSecurityHandler.Keys.O);
            bool shapeOk = o != null && o.Length == 32 &&
                           ((p.Revision == 2) || (p.Revision == 3 && bits == 128) || p.Revision == 4);
            if (shapeOk)
                p._reusableOwnerValue = o;
            return p;
        }

        /// <summary>Sets this protection on <paramref name="document"/> so its next Save writes it.</summary>
        /// <returns>False (and changes nothing) if <see cref="CanReapply"/> is false.</returns>
        public bool ApplyTo(PdfDocument document)
        {
            if (!CanReapply) return false;
            PdfSecuritySettings settings = document.SecuritySettings;
            settings.DocumentSecurityLevel = WrittenLevel;
            PdfStandardSecurityHandler handler = settings.SecurityHandler;
            handler._userPassword = UserPassword;
            if (_reusableOwnerValue != null)
            {
                handler._preservedOwnerValue = _reusableOwnerValue;
                handler._ownerPassword = null;
            }
            else
            {
                handler._preservedOwnerValue = null;
                handler._ownerPassword = !string.IsNullOrEmpty(OwnerPassword) ? OwnerPassword
                    : Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            }
            handler.Permission = (PdfUserAccessPermission)Permissions;
            return true;
        }

        /// <summary>
        /// Takes the protection back off <paramref name="document"/> after a protected save, so later
        /// saves of the live document (snapshots, working copies) are plain again and the handler
        /// object is not left behind as an orphan.
        /// </summary>
        public static void Detach(PdfDocument document)
        {
            PdfSecuritySettings settings = document._securitySettings;
            if (settings == null || settings.DocumentSecurityLevel == PdfDocumentSecurityLevel.None) return;
            PdfTrailer trailer = document._trailer;
            PdfStandardSecurityHandler handler = trailer != null ? trailer._securityHandler : null;
            settings.DocumentSecurityLevel = PdfDocumentSecurityLevel.None;
            if (handler != null)
            {
                handler._userPassword = null;
                handler._ownerPassword = null;
                handler._preservedOwnerValue = null;
                if (handler.Reference != null)
                    document._irefTable.Remove(handler.Reference);
                trailer._securityHandler = null;
            }
            if (trailer != null)
                trailer.Elements.Remove(PdfTrailer.Keys.Encrypt);
        }
    }
}
