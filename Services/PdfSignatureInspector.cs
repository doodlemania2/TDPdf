using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;

namespace TDPdf.Services
{
    /// <summary>
    /// One digital signature as the file states it. Every member is an observation — what the
    /// bytes say — and nothing here is a judgement about whether the signature is valid.
    /// </summary>
    internal sealed record ObservedSignature
    {
        /// <summary>The signature field's name (<c>/T</c>), or "" when it has none.</summary>
        public string FieldName { get; init; } = "";
        /// <summary><c>/Type /DocTimeStamp</c>: an archival document timestamp rather than a person's signature.</summary>
        public bool IsDocumentTimestamp { get; init; }
        /// <summary><c>/SubFilter</c> without the slash, e.g. <c>ETSI.CAdES.detached</c>.</summary>
        public string? SubFilter { get; init; }

        /// <summary>The signer certificate's common name, as the certificate states it.</summary>
        public string? SignerName { get; init; }
        /// <summary>The signer certificate's email address, as the certificate states it.</summary>
        public string? SignerEmail { get; init; }
        /// <summary>Who issued the signer certificate, as the certificate states it.</summary>
        public string? IssuerName { get; init; }

        /// <summary>The time the signer claims: the CMS signing-time attribute, else the dictionary's <c>/M</c>.</summary>
        public DateTimeOffset? ClaimedSigningTime { get; init; }
        /// <summary>When a timestamp token is embedded: the time its authority states.</summary>
        public DateTimeOffset? TimestampTime { get; init; }
        /// <summary>The timestamp authority's name, as its certificate states it.</summary>
        public string? TimestampAuthority { get; init; }

        public string? Reason { get; init; }
        public string? Location { get; init; }
        public string? ContactInfo { get; init; }

        /// <summary>The four <c>/ByteRange</c> numbers, or null when absent or malformed.</summary>
        public long[]? ByteRange { get; init; }
        /// <summary>
        /// True when the ranges start at 0, leave exactly one gap (the signature itself), and end at
        /// the end of the file — the signature was made over the file as it is now (allowing only
        /// trailing whitespace). False when bytes were added after it. Null when it cannot be told.
        /// </summary>
        public bool? CoversWholeFile { get; init; }
        /// <summary>How many <c>%%EOF</c>-terminated updates follow the bytes this signature covers.</summary>
        public int UpdatesAfter { get; init; }
        /// <summary>Whether the signature's CMS container could be read at all.</summary>
        public bool CmsReadable { get; init; }
    }

    /// <summary>What a document's signatures state, taken together.</summary>
    /// <param name="BytesAfterLastSignature">Bytes after the end of the furthest-reaching signature
    /// (trailing whitespace counts as none): an incremental update nobody signed.</param>
    /// <param name="AppendedIncludesDss">Whether those appended bytes carry a <c>/DSS</c> entry
    /// (long-term validation data, which PAdES-LTA writers append after signing).</param>
    internal sealed record SignatureObservations(
        IReadOnlyList<ObservedSignature> Signatures,
        long FileLength,
        long BytesAfterLastSignature,
        bool AppendedIncludesDss)
    {
        public static readonly SignatureObservations None = new(Array.Empty<ObservedSignature>(), 0, 0, false);
        public bool Any => Signatures.Count > 0;
    }

    /// <summary>
    /// Reads what a PDF's digital signatures state, for Verify Signatures and Document Info
    /// (design §6 Phase 5). <b>A read, not a validation</b> (design §7.1 / decision Q4): it decodes
    /// the CMS container only to read the certificate subject and the claimed times, and never calls
    /// <c>CheckSignature</c>, builds a chain, checks revocation or hashes the covered bytes. What it
    /// reports about the byte ranges is arithmetic on the numbers in the file. Nothing in TDPdf turns
    /// any of this into "valid".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fields come from <see cref="PdfSignatureScan.SignedFields"/> — the same walk the save
    /// warning and the scrub use — so Document Info, the footer indicator, the warning and what a
    /// save removes always agree on which signatures exist.
    /// </para>
    /// <para>
    /// The signature bytes are taken from the file itself, from the gap the <c>/ByteRange</c> leaves
    /// (which is where the hex <c>/Contents</c> string sits in every PAdES writer), rather than from
    /// PdfSharpCore's decoded string, so they are exactly what was written.
    /// </para>
    /// No WPF types: tests/PdfCore compiles this file.
    /// </remarks>
    internal static class PdfSignatureInspector
    {
        private const string OidSigningTime = "1.2.840.113549.1.9.5";
        private const string OidSignatureTimeStampToken = "1.2.840.113549.1.9.16.2.14";
        private const string OidTstInfo = "1.2.840.113549.1.9.16.1.4";

        /// <param name="document">The document parsed from <paramref name="file"/>.</param>
        /// <param name="file">The exact bytes on disk the ranges refer to.</param>
        internal static SignatureObservations Inspect(PdfDocument document, byte[] file)
        {
            var fields = PdfSignatureScan.SignedFields(document);
            if (fields.Count == 0) return SignatureObservations.None with { FileLength = file.LongLength };

            var list = new List<ObservedSignature>(fields.Count);
            foreach (var field in fields)
            {
                try { list.Add(Observe(field, file)); }
                catch { /* one unreadable signature must not hide the others */ }
            }

            long lastEnd = list.Select(s => s.ByteRange is { } br ? br[2] + br[3] : 0).DefaultIfEmpty(0).Max();
            long after = lastEnd <= 0 ? 0 : TrailingBytes(file, lastEnd);
            bool dss = after > 0 && IndexOf(file, Encoding.ASCII.GetBytes("/DSS"), lastEnd) >= 0;
            return new SignatureObservations(list, file.LongLength, after, dss);
        }

        private static ObservedSignature Observe(PdfDictionary field, byte[] file)
        {
            var sig = Deref(field.Elements["/V"]) as PdfDictionary;
            string name = Str(field, "/T") ?? "";
            if (sig is null) return new ObservedSignature { FieldName = name };

            bool docTs = sig.Elements.GetName("/Type") == "/DocTimeStamp";
            string? subFilter = sig.Elements.GetName("/SubFilter") is { Length: > 1 } sf ? sf.TrimStart('/') : null;
            long[]? br = ReadByteRange(sig.Elements["/ByteRange"]);
            bool? covers = br is null ? null : CoversWholeFile(br, file);
            int updatesAfter = br is null ? 0 : CountEofAfter(file, br[2] + br[3]);

            var o = new ObservedSignature
            {
                FieldName = name,
                IsDocumentTimestamp = docTs,
                SubFilter = subFilter,
                ClaimedSigningTime = ParsePdfDate(Str(sig, "/M")),
                Reason = Str(sig, "/Reason"),
                Location = Str(sig, "/Location"),
                ContactInfo = Str(sig, "/ContactInfo"),
                ByteRange = br,
                CoversWholeFile = covers,
                UpdatesAfter = updatesAfter,
                SignerName = Str(sig, "/Name"),
            };

            byte[]? der = br is null ? null : ContentsFromGap(file, br);
            if (der is null) return o;
            return ReadCms(der, o);
        }

        // ── ByteRange ───────────────────────────────────────────────────────────────────────

        internal static long[]? ReadByteRange(PdfItem? item)
        {
            if (Deref(item) is not PdfArray a || a.Elements.Count != 4) return null;
            var v = new long[4];
            for (int i = 0; i < 4; i++)
            {
                long? n = Deref(a.Elements[i]) switch
                {
                    PdfInteger x => x.Value,
                    PdfIntegerObject x => x.Value,
                    PdfUInteger x => x.Value,
                    PdfUIntegerObject x => x.Value,
                    PdfReal x when x.Value >= 0 && x.Value == Math.Floor(x.Value) => (long)x.Value,
                    _ => null,
                };
                if (n is not { } k || k < 0) return null;
                v[i] = k;
            }
            return v;
        }

        /// <summary>
        /// [a b c d]: true when a = 0, the gap b..c is non-empty, and c + d reaches the end of the
        /// file (anything after it being only whitespace). False when it ends early. Null when the
        /// numbers do not describe the file at all (ranges past its end, overlapping, no gap).
        /// </summary>
        internal static bool? CoversWholeFile(long[] br, byte[] file)
        {
            long len = file.LongLength;
            if (br[0] != 0 || br[1] <= 0 || br[2] <= br[1] || br[3] < 0 || br[2] + br[3] > len) return null;
            return TrailingBytes(file, br[2] + br[3]) == 0;
        }

        /// <summary>Bytes from <paramref name="from"/> to the end, not counting trailing whitespace or NULs.</summary>
        internal static long TrailingBytes(byte[] file, long from)
        {
            long end = file.LongLength;
            while (end > from && file[end - 1] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or (byte)'\f' or 0) end--;
            return Math.Max(0, end - from);
        }

        private static int CountEofAfter(byte[] file, long from)
        {
            if (from >= file.LongLength) return 0;
            var eof = Encoding.ASCII.GetBytes("%%EOF");
            int n = 0;
            long i = from;
            while ((i = IndexOf(file, eof, i)) >= 0) { n++; i += eof.Length; }
            return n;
        }

        /// <summary>
        /// The signature container: the hex string that fills the gap between the two ranges,
        /// decoded, with the zero padding PAdES writers reserve cut off at the DER length.
        /// </summary>
        internal static byte[]? ContentsFromGap(byte[] file, long[] br)
        {
            long start = br[1], end = br[2];
            if (start < 0 || end > file.LongLength || end - start < 2) return null;
            if (file[start] != (byte)'<') return null;
            var bytes = new List<byte>((int)Math.Min(int.MaxValue, (end - start) / 2));
            int hi = -1;
            for (long i = start + 1; i < end; i++)
            {
                byte c = file[i];
                if (c == (byte)'>') break;
                int v = c switch
                {
                    >= (byte)'0' and <= (byte)'9' => c - '0',
                    >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
                    >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
                    _ => -1,
                };
                if (v < 0) continue;   // whitespace inside a hex string is allowed
                if (hi < 0) hi = v;
                else { bytes.Add((byte)(hi << 4 | v)); hi = -1; }
            }
            var raw = bytes.ToArray();
            int der = DerLength(raw);
            return der <= 0 ? null : raw[..der];
        }

        /// <summary>The length of the DER SEQUENCE at the start of <paramref name="data"/>, or -1.</summary>
        internal static int DerLength(byte[] data)
        {
            if (data.Length < 2 || data[0] != 0x30) return -1;
            int first = data[1];
            if (first < 0x80) return 2 + first <= data.Length ? 2 + first : -1;
            int n = first & 0x7F;
            if (n is 0 or > 4 || data.Length < 2 + n) return -1;
            long len = 0;
            for (int i = 0; i < n; i++) len = (len << 8) | data[2 + i];
            long total = 2 + n + len;
            return total <= data.Length && total <= int.MaxValue ? (int)total : -1;
        }

        // ── CMS: decode only ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the signer certificate's subject and issuer and the claimed times out of the CMS
        /// container. <see cref="SignedCms.Decode(byte[])"/> parses; it does not verify. No
        /// <c>CheckSignature</c>, no <c>X509Chain</c>, no digest comparison — by decision.
        /// </summary>
        private static ObservedSignature ReadCms(byte[] der, ObservedSignature o)
        {
            try
            {
                var cms = new SignedCms();
                cms.Decode(der);
                var signer = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0] : null;
                var cert = signer is null ? null : CertificateOf(signer, cms);

                if (cms.ContentInfo.ContentType.Value == OidTstInfo)
                {
                    // An RFC 3161 token (a /DocTimeStamp's contents): the "signer" is the timestamp
                    // authority, and the time is the one in its TSTInfo.
                    var at = Rfc3161TimestampTokenInfo.TryDecode(cms.ContentInfo.Content, out var info, out _) ? info!.Timestamp : (DateTimeOffset?)null;
                    return o with
                    {
                        CmsReadable = true,
                        TimestampTime = at,
                        TimestampAuthority = NameOf(cert),
                        SignerName = NameOf(cert) ?? o.SignerName,
                        IssuerName = IssuerOf(cert),
                    };
                }

                DateTimeOffset? claimed = o.ClaimedSigningTime;
                string? tsa = null;
                DateTimeOffset? tsTime = null;
                if (signer is not null)
                {
                    foreach (var attr in signer.SignedAttributes)
                    {
                        if (attr.Oid?.Value != OidSigningTime) continue;
                        foreach (var v in attr.Values)
                            if (v is Pkcs9SigningTime st)
                                claimed = new DateTimeOffset(DateTime.SpecifyKind(st.SigningTime, DateTimeKind.Utc));
                    }
                    foreach (var attr in signer.UnsignedAttributes)
                    {
                        if (attr.Oid?.Value != OidSignatureTimeStampToken || attr.Values.Count == 0) continue;
                        (tsTime, tsa) = ReadTimestampToken(attr.Values[0].RawData);
                    }
                }

                return o with
                {
                    CmsReadable = true,
                    SignerName = NameOf(cert) ?? o.SignerName,
                    SignerEmail = EmailOf(cert),
                    IssuerName = IssuerOf(cert),
                    ClaimedSigningTime = claimed,
                    TimestampTime = tsTime,
                    TimestampAuthority = tsa,
                };
            }
            catch (CryptographicException)
            {
                return o with { CmsReadable = false };
            }
        }

        private static (DateTimeOffset? Time, string? Authority) ReadTimestampToken(byte[] token)
        {
            try
            {
                var ts = new SignedCms();
                ts.Decode(token);
                DateTimeOffset? at = ts.ContentInfo.ContentType.Value == OidTstInfo
                    && Rfc3161TimestampTokenInfo.TryDecode(ts.ContentInfo.Content, out var info, out _) ? info!.Timestamp : null;
                var s = ts.SignerInfos.Count > 0 ? ts.SignerInfos[0] : null;
                return (at, s is null ? null : NameOf(CertificateOf(s, ts)));
            }
            catch (CryptographicException)
            {
                return (null, null);
            }
        }

        /// <summary>The signer's certificate as carried in the container — looked up, never chained.</summary>
        private static X509Certificate2? CertificateOf(SignerInfo signer, SignedCms cms)
        {
            if (signer.Certificate is { } c) return c;
            foreach (var cert in cms.Certificates)
            {
                var id = signer.SignerIdentifier;
                if (id.Type == SubjectIdentifierType.IssuerAndSerialNumber && id.Value is System.Security.Cryptography.Xml.X509IssuerSerial s
                    && string.Equals(cert.SerialNumber, s.SerialNumber, StringComparison.OrdinalIgnoreCase))
                    return cert;
                if (id.Type == SubjectIdentifierType.SubjectKeyIdentifier && id.Value is string ski
                    && cert.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Any(e => string.Equals(e.SubjectKeyIdentifier, ski, StringComparison.OrdinalIgnoreCase)))
                    return cert;
            }
            return null;
        }

        private static string? NameOf(X509Certificate2? c) => Blank(c?.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        private static string? EmailOf(X509Certificate2? c) => Blank(c?.GetNameInfo(X509NameType.EmailName, forIssuer: false));
        private static string? IssuerOf(X509Certificate2? c) => Blank(c?.GetNameInfo(X509NameType.SimpleName, forIssuer: true));
        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        // ── PDF primitives ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// A PDF date (<c>D:YYYYMMDDHHmmSSOHH'mm'</c>, every part after the year optional) as an
        /// offset time. Null for anything that is not one.
        /// </summary>
        internal static DateTimeOffset? ParsePdfDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];
            int i = 0;
            int Num(int width, int fallback)
            {
                if (i + width > s.Length || !s.Substring(i, width).All(char.IsAsciiDigit)) return fallback;
                int v = int.Parse(s.AsSpan(i, width), NumberStyles.None, CultureInfo.InvariantCulture);
                i += width;
                return v;
            }
            int year = Num(4, -1);
            if (year < 0) return null;
            int month = Num(2, 1), day = Num(2, 1), hour = Num(2, 0), minute = Num(2, 0), second = Num(2, 0);
            var offset = TimeSpan.Zero;
            if (i < s.Length && s[i] is '+' or '-')
            {
                int sign = s[i] == '-' ? -1 : 1;
                i++;
                int oh = Num(2, 0);
                if (i < s.Length && s[i] == '\'') i++;
                int om = Num(2, 0);
                offset = TimeSpan.FromMinutes(sign * (oh * 60 + om));
            }
            try
            {
                return new DateTimeOffset(year, month, day, hour, minute, second, offset);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static string? Str(PdfDictionary d, string key)
        {
            try
            {
                return Deref(d.Elements[key]) is PdfString ps && !string.IsNullOrWhiteSpace(ps.Value) ? ps.Value.Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static long IndexOf(byte[] data, byte[] pattern, long from)
        {
            for (long i = Math.Max(0, from); i <= data.LongLength - pattern.Length; i++)
            {
                int j = 0;
                while (j < pattern.Length && data[i + j] == pattern[j]) j++;
                if (j == pattern.Length) return i;
            }
            return -1;
        }

        private static PdfItem? Deref(PdfItem? item) => item is PdfReference r ? r.Value : item;
    }
}

namespace TDPdf.Services
{
    /// <summary>
    /// The sentences Verify Signatures and Document Info show for a <see cref="SignatureObservations"/>.
    /// Every one is framed as what the file <em>states</em>; none says, or implies, that a signature
    /// is valid, trusted or verified (design §7.1). WPF-free so tests/PdfCore can hold it to that.
    /// </summary>
    internal static class SignatureObservationText
    {
        internal const string Disclaimer =
            "TDPdf reads what the signatures state; it does not check them (not the cryptography, the signer's " +
            "certificate chain, revocation or the timestamp). To have a signature checked, open the file in a PDF " +
            "reader that verifies signatures, such as Adobe Acrobat Reader.";

        internal static string Heading(ObservedSignature s, int index)
        {
            string what = s.IsDocumentTimestamp ? "Document timestamp" : $"Signature {index + 1}";
            return s.FieldName.Length > 0 ? $"{what} (field “{s.FieldName}”)" : what;
        }

        /// <summary>The detail lines for one signature, in reading order.</summary>
        internal static List<string> Lines(ObservedSignature s)
        {
            var lines = new List<string>();
            string? who = s.SignerName is null ? null
                : s.SignerEmail is { } e && !string.Equals(e, s.SignerName, StringComparison.OrdinalIgnoreCase) ? $"{s.SignerName} <{e}>"
                : s.SignerName;
            if (s.IsDocumentTimestamp)
                lines.Add(s.TimestampAuthority is { } tsa
                    ? $"Timestamped by {tsa} (as stated in the timestamp)"
                    : "The timestamp authority's certificate could not be read.");
            else
                lines.Add(who is not null
                    ? $"Signed by {who} (as stated in the signature)"
                    : s.CmsReadable ? "The signature does not name its signer." : "The signature's contents could not be read.");
            if (!s.IsDocumentTimestamp && s.IssuerName is { } issuer && issuer != s.SignerName)
                lines.Add($"Signer's certificate issued by {issuer} (as stated)");
            if (s.ClaimedSigningTime is { } t)
                lines.Add($"Signing time claimed by the signer: {FormatTime(t)}");
            if (s.TimestampTime is { } ts)
                lines.Add(s.IsDocumentTimestamp || s.TimestampAuthority is null
                    ? $"Time stated in the timestamp: {FormatTime(ts)}"
                    : $"Timestamp: {FormatTime(ts)} (as stated by {s.TimestampAuthority})");
            if (s.SubFilter is { } sf) lines.Add($"Signature type: {TypeLabel(sf)}");
            if (s.Reason is { } r) lines.Add($"Reason (as stated): {r}");
            if (s.Location is { } l) lines.Add($"Location (as stated): {l}");
            lines.Add(Coverage(s));
            return lines;
        }

        internal static string Coverage(ObservedSignature s) => s.CoversWholeFile switch
        {
            true => "Covers the whole file as it is now.",
            false => s.UpdatesAfter > 0
                ? $"Covers the file as it was when signed; {s.UpdatesAfter} later update{(s.UpdatesAfter == 1 ? " was" : "s were")} added after it."
                : "Covers the file as it was when signed; more was added after it.",
            _ => "Its byte range does not describe this file, so what it covers cannot be told.",
        };

        /// <summary>The one line about the document as a whole: was anything added after the last signature?</summary>
        internal static string Summary(SignatureObservations r)
        {
            if (!r.Any) return "This document carries no digital signature.";
            if (r.BytesAfterLastSignature <= 0) return "Nothing was added to the file after the last signature.";
            string size = r.BytesAfterLastSignature < 1024 ? $"{r.BytesAfterLastSignature} bytes" : $"{r.BytesAfterLastSignature / 1024.0:0.#} KB";
            return r.AppendedIncludesDss
                ? $"{size} were added after the last signature, in an update nobody signed. It includes long-term validation data " +
                  "(/DSS), which signing services add this way; TDPdf cannot tell what else it contains."
                : $"{size} were added after the last signature, in an update nobody signed. A PDF reader that checks signatures " +
                  "can show what changed.";
        }

        internal static string TypeLabel(string subFilter) => subFilter switch
        {
            "ETSI.CAdES.detached" => "PAdES (ETSI.CAdES.detached)",
            "adbe.pkcs7.detached" => "PKCS #7 detached (adbe.pkcs7.detached)",
            "adbe.pkcs7.sha1" => "PKCS #7 SHA-1 (adbe.pkcs7.sha1)",
            "adbe.x509.rsa_sha1" => "X.509 RSA SHA-1 (adbe.x509.rsa_sha1)",
            "ETSI.RFC3161" => "RFC 3161 document timestamp (ETSI.RFC3161)",
            _ => subFilter,
        };

        internal static string FormatTime(DateTimeOffset t)
            => t.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture) + $" (UTC{t.ToLocalTime():zzz})";
    }
}
