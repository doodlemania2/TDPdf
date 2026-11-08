using System;

namespace PdfSharpCore.Pdf.Security
{
    /// <summary>
    /// The /Identity crypt filter: data passes through untouched. It still derives the file key the
    /// ordinary way (so password validation works when it stands in for /StrF); only Encrypt, which
    /// is what the reader's decrypt pass calls, is a no-op.
    /// </summary>
    internal sealed class IdentityEncryptor : AESEncryptor
    {
        public override byte[] Encrypt(byte[] bytes)
        {
            return bytes;
        }
    }
}
