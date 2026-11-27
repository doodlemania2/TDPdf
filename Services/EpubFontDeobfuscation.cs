using System.Security.Cryptography;
using System.Text;

namespace TDPdf.Services
{
    /// <summary>
    /// The two font obfuscation schemes the EPUB specification defines. They are not encryption: they
    /// stop a font file from being used straight out of the zip, and every reader is expected to undo them.
    /// Any other algorithm named in META-INF/encryption.xml is treated as DRM and is not touched.
    /// </summary>
    internal static class EpubFontDeobfuscation
    {
        internal const string IdpfAlgorithm = "http://www.idpf.org/2008/embedding";
        internal const string AdobeAlgorithm = "http://ns.adobe.com/pdf/enc#RC";

        /// <summary>IDPF obfuscates the first 1040 bytes; Adobe's scheme the first 1024.</summary>
        internal const int IdpfLength = 1040, AdobeLength = 1024;

        internal static bool IsObfuscation(string? algorithm) =>
            string.Equals(algorithm, IdpfAlgorithm, StringComparison.Ordinal) ||
            string.Equals(algorithm, AdobeAlgorithm, StringComparison.Ordinal);

        /// <summary>SHA-1 of the package's unique identifier with spaces, tabs and line breaks removed.</summary>
        internal static byte[] IdpfKey(string uniqueIdentifier)
        {
            var sb = new StringBuilder(uniqueIdentifier.Length);
            foreach (char c in uniqueIdentifier)
                if (c is not (' ' or '\t' or '\r' or '\n')) sb.Append(c);
            return SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        /// <summary>
        /// The 16-byte key from a "urn:uuid:" identifier, or null when the identifier is not a UUID
        /// (then the font cannot be unlocked and the page falls back to another font).
        /// </summary>
        internal static byte[]? AdobeKey(string uniqueIdentifier)
        {
            string s = uniqueIdentifier.Trim();
            if (s.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase)) s = s[9..];
            s = s.Replace("-", "");
            if (s.Length != 32) return null;
            var key = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                if (!byte.TryParse(s.AsSpan(i * 2, 2), System.Globalization.NumberStyles.AllowHexSpecifier, null, out key[i]))
                    return null;
            }
            return key;
        }

        /// <summary>A copy of <paramref name="data"/> with its first <paramref name="length"/> bytes XORed with the repeating key.</summary>
        internal static byte[] Xor(byte[] data, byte[] key, int length)
        {
            var copy = (byte[])data.Clone();
            int n = Math.Min(length, copy.Length);
            for (int i = 0; i < n; i++) copy[i] ^= key[i % key.Length];
            return copy;
        }

        /// <summary>
        /// Undoes <paramref name="algorithm"/> on a font file, or returns null when the algorithm is not
        /// one of the two, or the identifier cannot make a key for it.
        /// </summary>
        internal static byte[]? Deobfuscate(string algorithm, string uniqueIdentifier, byte[] data)
        {
            if (string.Equals(algorithm, IdpfAlgorithm, StringComparison.Ordinal))
                return Xor(data, IdpfKey(uniqueIdentifier), IdpfLength);
            if (string.Equals(algorithm, AdobeAlgorithm, StringComparison.Ordinal))
                return AdobeKey(uniqueIdentifier) is { } key ? Xor(data, key, AdobeLength) : null;
            return null;
        }
    }
}
