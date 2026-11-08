namespace PdfSharpCore.Pdf.Security
{
    internal interface IEncryptor
    {
        bool PasswordValid { get; }

        bool HaveOwnerPermission { get; }

        /// <summary>The user password recovered from /O when the owner password validated (R2-R4 only), else null.</summary>
        string RecoveredUserPassword { get; }

        void Initialize(PdfDocument document, PdfDictionary encryptionDict);

        void InitEncryptionKey(string password);

        bool ValidatePassword(string password);

        void SetEncryptionKey(byte[] key);

        void CreateHashKey(PdfObjectID objectId);

        byte[] Encrypt(byte[] bytes);
    }
}