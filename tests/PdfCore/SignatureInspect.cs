using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using TDPdf.Services;

/// <summary>
/// Pins <see cref="PdfSignatureInspector"/> — Verify Signatures' read of what a signature states —
/// against PDFs signed here, the way Documenso's sealer (<c>@libpdf/core</c> 0.4.2) signs: an
/// incremental-update-free file whose one AcroForm field is a merged field + widget with
/// <c>/FT /Sig</c> and a <c>/V</c> signature dictionary (<c>/Type /Sig</c>, <c>/Filter
/// /Adobe.PPKLite</c>, <c>/SubFilter /ETSI.CAdES.detached</c>), a fixed-width <c>/ByteRange</c>
/// placeholder patched after saving, and a zero-padded hex <c>/Contents</c> holding a detached CMS
/// made with <see cref="SignedCms"/> and a throwaway self-signed certificate. Test-only crypto.
/// </summary>
/// <remarks>
/// The same fixture also pins the §5.5 claim that matters most: that <see cref="PdfSignatureScan"/>
/// — the walk behind the save warning and the scrub — sees a signature of that shape, before and
/// after an unsigned incremental update is appended to it.
/// </remarks>
internal static class SignatureInspect
{
    private const int ContentsBytes = 8192;
    private static readonly DateTime SignedAt = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("Digital signatures — what they state (Verify Signatures)");

        using var signerKey = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test Signer, E=signer@example.com, O=TDPdf Tests", signerKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // ── No signatures ──────────────────────────────────────────────────────────────────
        {
            var doc = new PdfDocument();
            doc.AddPage();
            using var ms = new MemoryStream();
            doc.Save(ms);
            var bytes = ms.ToArray();
            using var reopened = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.ReadOnly);
            var r = PdfSignatureInspector.Inspect(reopened, bytes);
            Check("an unsigned PDF reports no signatures", !r.Any && r.BytesAfterLastSignature == 0, "");
        }

        // ── Signed, nothing after ──────────────────────────────────────────────────────────
        byte[] signed = MakeSigned(cert, docTimestamp: false);
        File.WriteAllBytes(Path.Combine(tmp, "signed-fixture.pdf"), signed);
        using (var doc = PdfReader.Open(new MemoryStream(signed), PdfDocumentOpenMode.ReadOnly))
        {
            Check("§5.5: the save warning's walk sees a libpdf-shaped (merged field + widget) signature",
                  PdfSignatureScan.SignedFields(doc).Count == 1, $"{PdfSignatureScan.SignedFields(doc).Count}");
            var r = PdfSignatureInspector.Inspect(doc, signed);
            var s = r.Signatures.SingleOrDefault();
            Check("one signature observed", s is not null, $"{r.Signatures.Count}");
            if (s is not null)
            {
                Check("field name", s.FieldName == "Signature_1", s.FieldName);
                Check("SubFilter", s.SubFilter == "ETSI.CAdES.detached", s.SubFilter ?? "null");
                Check("the CMS container is read", s.CmsReadable, "");
                Check("signer name as the certificate states it", s.SignerName == "Test Signer", s.SignerName ?? "null");
                Check("signer email as the certificate states it", s.SignerEmail == "signer@example.com", s.SignerEmail ?? "null");
                Check("issuer as stated (self-signed: itself)", s.IssuerName == "Test Signer", s.IssuerName ?? "null");
                Check("claimed signing time from the CMS attribute", s.ClaimedSigningTime == new DateTimeOffset(SignedAt),
                      s.ClaimedSigningTime?.ToString("o") ?? "null");
                Check("location as stated", s.Location == "https://sign.tdpdf.com", s.Location ?? "null");
                Check("ByteRange read", s.ByteRange is { Length: 4 } br && br[0] == 0, string.Join(" ", s.ByteRange ?? Array.Empty<long>()));
                Check("covers the whole file", s.CoversWholeFile == true, $"{s.CoversWholeFile}");
                Check("no updates after it", s.UpdatesAfter == 0, $"{s.UpdatesAfter}");
                Check("not a document timestamp", !s.IsDocumentTimestamp, "");
            }
            Check("nothing after the last signature", r.BytesAfterLastSignature == 0, $"{r.BytesAfterLastSignature}");
        }

        // Trailing whitespace after %%EOF is not an update.
        {
            byte[] padded = signed.Concat(Encoding.ASCII.GetBytes("\r\n\n  ")).ToArray();
            using var doc = PdfReader.Open(new MemoryStream(padded), PdfDocumentOpenMode.ReadOnly);
            var r = PdfSignatureInspector.Inspect(doc, padded);
            Check("trailing whitespace is not counted as an appended update",
                  r.Signatures.Single().CoversWholeFile == true && r.BytesAfterLastSignature == 0, "");
        }

        // ── Signed, then an unsigned incremental update appended ───────────────────────────
        {
            byte[] appended = AppendIncrementalUpdate(signed);
            File.WriteAllBytes(Path.Combine(tmp, "signed-then-edited.pdf"), appended);
            using var doc = PdfReader.Open(new MemoryStream(appended), PdfDocumentOpenMode.ReadOnly);
            Check("§5.5: the walk still sees the signature after an incremental update", PdfSignatureScan.SignedFields(doc).Count == 1, "");
            Check("…and the update is what PdfSharpCore reads (the new revision is live)",
                  doc.Info.Producer == "edited after signing", doc.Info.Producer ?? "null");
            var r = PdfSignatureInspector.Inspect(doc, appended);
            var s = r.Signatures.Single();
            Check("appended update: the signature no longer covers the whole file", s.CoversWholeFile == false, $"{s.CoversWholeFile}");
            Check("appended update: one update after it", s.UpdatesAfter == 1, $"{s.UpdatesAfter}");
            // The update ends "%%EOF\n"; trailing whitespace is never counted, so one byte less.
            Check("appended update: the bytes after the last signature are reported",
                  r.BytesAfterLastSignature == appended.Length - signed.Length - 1, $"{r.BytesAfterLastSignature} vs {appended.Length - signed.Length - 1}");
            Check("appended update: no /DSS in it", !r.AppendedIncludesDss, "");
            Check("…the signer is still read", s.SignerName == "Test Signer", s.SignerName ?? "null");
        }

        // ── A document timestamp (RFC 3161), as libpdf adds for archival (B-LTA) ─────────────
        {
            byte[] ts = MakeSigned(cert, docTimestamp: true);
            using var doc = PdfReader.Open(new MemoryStream(ts), PdfDocumentOpenMode.ReadOnly);
            var s = PdfSignatureInspector.Inspect(doc, ts).Signatures.Single();
            Check("a /DocTimeStamp is recognised as one", s.IsDocumentTimestamp && s.SubFilter == "ETSI.RFC3161", s.SubFilter ?? "null");
            Check("its time is the one the timestamp authority states", s.TimestampTime == new DateTimeOffset(SignedAt),
                  s.TimestampTime?.ToString("o") ?? "null");
            Check("its authority is named as its certificate states", s.TimestampAuthority == "Test Signer", s.TimestampAuthority ?? "null");
        }

        // ── What is said about it: observed, never "valid" ───────────────────────────────────
        {
            using var doc = PdfReader.Open(new MemoryStream(signed), PdfDocumentOpenMode.ReadOnly);
            var r = PdfSignatureInspector.Inspect(doc, signed);
            byte[] appended = AppendIncrementalUpdate(signed);
            using var doc2 = PdfReader.Open(new MemoryStream(appended), PdfDocumentOpenMode.ReadOnly);
            var r2 = PdfSignatureInspector.Inspect(doc2, appended);
            var all = new List<string> { SignatureObservationText.Disclaimer, SignatureObservationText.Summary(r), SignatureObservationText.Summary(r2),
                SignatureObservationText.Summary(SignatureObservations.None) };
            foreach (var x in r.Signatures.Concat(r2.Signatures)) all.AddRange(SignatureObservationText.Lines(x));
            var claims = all.Where(t => Regex.IsMatch(t, @"\b(valid|verified|trusted|authentic|genuine)\b", RegexOptions.IgnoreCase)).ToList();
            Check("no sentence claims a signature is valid, verified or trusted", claims.Count == 0, string.Join(" | ", claims));
            var lines = SignatureObservationText.Lines(r.Signatures[0]);
            Check("the signer is framed as stated", lines.Any(l => l == "Signed by Test Signer <signer@example.com> (as stated in the signature)"), string.Join(" | ", lines));
            Check("whole-file coverage is said as such", lines.Contains("Covers the whole file as it is now."), "");
            Check("an appended update is said as such", SignatureObservationText.Lines(r2.Signatures[0])
                  .Contains("Covers the file as it was when signed; 1 later update was added after it."), "");
            Check("…and summarised for the document", SignatureObservationText.Summary(r2).Contains("added after the last signature"), SignatureObservationText.Summary(r2));
            Check("the disclaimer says TDPdf does not check", SignatureObservationText.Disclaimer.Contains("does not check"), "");
        }

        // ── Primitives ─────────────────────────────────────────────────────────────────────
        Check("PDF date with offset", PdfSignatureInspector.ParsePdfDate("D:20261010143000-05'00'")
              == new DateTimeOffset(2026, 10, 10, 14, 30, 0, TimeSpan.FromHours(-5)), "");
        Check("PDF date, Z and partial", PdfSignatureInspector.ParsePdfDate("D:2026101014Z")
              == new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.Zero), "");
        Check("not a PDF date", PdfSignatureInspector.ParsePdfDate("yesterday") is null && PdfSignatureInspector.ParsePdfDate("D:20261340") is null, "");
        Check("DER length, short form", PdfSignatureInspector.DerLength(new byte[] { 0x30, 0x03, 1, 2, 3, 0, 0 }) == 5, "");
        Check("DER length, long form", PdfSignatureInspector.DerLength(new byte[] { 0x30, 0x81, 0x02, 9, 9, 0 }) == 5, "");
        Check("DER length beyond the data", PdfSignatureInspector.DerLength(new byte[] { 0x30, 0x10, 1 }) == -1, "");
        var file = new byte[100];
        Check("ranges past the end of the file are not 'covered'", PdfSignatureInspector.CoversWholeFile(new long[] { 0, 10, 20, 500 }, file) is null, "");
        Check("ranges not starting at 0 are not 'covered'", PdfSignatureInspector.CoversWholeFile(new long[] { 5, 10, 20, 80 }, file) is null, "");
        Check("ranges with no gap are not 'covered'", PdfSignatureInspector.CoversWholeFile(new long[] { 0, 10, 10, 90 }, file) is null, "");
        Check("ranges ending early are an update", PdfSignatureInspector.CoversWholeFile(new long[] { 0, 10, 20, 50 }, Enumerable.Repeat((byte)'x', 100).ToArray()) == false, "");
    }

    /// <summary>
    /// A one-page PDF signed the way @libpdf/core signs: placeholders written, the file saved, the
    /// ByteRange patched in place, the covered bytes signed, the CMS hex patched into the gap.
    /// </summary>
    private static byte[] MakeSigned(X509Certificate2 cert, bool docTimestamp)
    {
        var doc = new PdfDocument();
        var page = doc.AddPage();
        using (var g = XGraphics.FromPdfPage(page))
            g.DrawString("Signed fixture", new XFont("Helvetica", 14), XBrushes.Black, 60, 100);

        var sig = new PdfDictionary(doc);
        sig.Elements["/Type"] = new PdfName(docTimestamp ? "/DocTimeStamp" : "/Sig");
        sig.Elements["/Filter"] = new PdfName("/Adobe.PPKLite");
        sig.Elements["/SubFilter"] = new PdfName(docTimestamp ? "/ETSI.RFC3161" : "/ETSI.CAdES.detached");
        sig.Elements["/ByteRange"] = new PdfLiteral("[0 ********** ********** **********]");
        sig.Elements["/Contents"] = new PdfLiteral("<" + new string('0', ContentsBytes * 2) + ">");
        if (!docTimestamp)
        {
            sig.Elements["/M"] = new PdfString("D:20261010120000+00'00'");
            sig.Elements["/Reason"] = new PdfString("Signed by Documenso");
            sig.Elements["/Location"] = new PdfString("https://sign.tdpdf.com");
        }
        doc.Internals.AddObject(sig);

        var field = new PdfDictionary(doc);
        field.Elements["/FT"] = new PdfName("/Sig");
        field.Elements["/T"] = new PdfString(docTimestamp ? "DocTimeStamp_1" : "Signature_1");
        field.Elements["/V"] = sig.Reference;
        field.Elements["/Type"] = new PdfName("/Annot");
        field.Elements["/Subtype"] = new PdfName("/Widget");
        field.Elements["/F"] = new PdfInteger(132);
        field.Elements["/P"] = page.Reference;
        field.Elements["/Rect"] = new PdfLiteral("[0 0 0 0]");
        doc.Internals.AddObject(field);
        page.Elements["/Annots"] = new PdfArray(doc, field.Reference!);

        var acro = new PdfDictionary(doc);
        acro.Elements["/Fields"] = new PdfArray(doc, field.Reference!);
        acro.Elements["/SigFlags"] = new PdfInteger(3);
        doc.Internals.AddObject(acro);
        doc.Internals.Catalog.Elements["/AcroForm"] = acro.Reference;

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            doc.Save(ms);
            bytes = ms.ToArray();
        }

        string latin = Encoding.Latin1.GetString(bytes);
        int brAt = latin.IndexOf("[0 ********** ********** **********]", StringComparison.Ordinal);
        int lt = latin.IndexOf("<" + new string('0', 16), brAt < 0 ? 0 : brAt, StringComparison.Ordinal);
        if (lt < 0) lt = latin.IndexOf("<" + new string('0', 16), StringComparison.Ordinal);
        int gt = latin.IndexOf('>', lt);
        long b = lt, c = gt + 1, d = bytes.Length - c;
        string range = $"[0 {b,-10} {c,-10} {d,-10}]";
        Encoding.ASCII.GetBytes(range).CopyTo(bytes, brAt);

        byte[] covered = bytes[..(int)b].Concat(bytes[(int)c..]).ToArray();
        byte[] der;
        if (docTimestamp)
        {
            var info = new Rfc3161TimestampTokenInfo(new Oid("1.2.3.4.1"), new Oid("2.16.840.1.101.3.4.2.1"),
                SHA256.HashData(covered), new byte[] { 0x01, 0x02 }, new DateTimeOffset(SignedAt));
            var cms = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()), detached: false);
            cms.ComputeSignature(new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert));
            der = cms.Encode();
        }
        else
        {
            var cms = new SignedCms(new ContentInfo(covered), detached: true);
            var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert)
            {
                DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
            };
            signer.SignedAttributes.Add(new Pkcs9SigningTime(SignedAt));
            cms.ComputeSignature(signer);
            der = cms.Encode();
        }
        string hex = Convert.ToHexString(der);
        if (hex.Length > ContentsBytes * 2) throw new InvalidOperationException("signature larger than its placeholder");
        Encoding.ASCII.GetBytes(hex).CopyTo(bytes, lt + 1);
        return bytes;
    }

    /// <summary>
    /// Appends a genuine incremental update — a new /Info dictionary, an xref section for it and a
    /// trailer chained to the original with /Prev — the way any editor that "saves incrementally"
    /// changes a signed file without touching the signed bytes.
    /// </summary>
    private static byte[] AppendIncrementalUpdate(byte[] signed)
    {
        string latin = Encoding.Latin1.GetString(signed);
        long prev = long.Parse(Regex.Matches(latin, @"startxref\s+(\d+)").Last().Groups[1].Value);
        var trailer = Regex.Matches(latin, @"trailer\s*<<(.*?)>>\s*startxref", RegexOptions.Singleline).Last().Groups[1].Value;
        int size = int.Parse(Regex.Match(trailer, @"/Size\s+(\d+)").Groups[1].Value);
        string root = Regex.Match(trailer, @"/Root\s+(\d+\s+\d+\s+R)").Groups[1].Value;

        var sb = new StringBuilder();
        string head = "\n";
        long objAt = signed.Length + head.Length;
        sb.Append(head);
        sb.Append($"{size} 0 obj\n<< /Producer (edited after signing) >>\nendobj\n");
        long xrefAt = signed.Length + Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n{size} 1\n{objAt:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {size + 1} /Root {root} /Info {size} 0 R /Prev {prev} >>\nstartxref\n{xrefAt}\n%%EOF\n");
        return signed.Concat(Encoding.ASCII.GetBytes(sb.ToString())).ToArray();
    }
}
