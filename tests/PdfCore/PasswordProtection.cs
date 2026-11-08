using System.Security.Cryptography;
using System.Text;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfSharpCore.Pdf.Security;
using TDPdf.Services;

/// <summary>
/// Password-protected PDFs (upstream KillerPDF 1.9.1 / 1.9.2, #470): a user-password open, saves that
/// keep the protection, an unencrypted /Metadata stream, and the /Encrypt scan.
/// </summary>
internal static class PasswordProtection
{
    private static readonly byte[] Pad =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var o = new byte[data.Length];
        for (int n = 0, x = 0, y = 0; n < data.Length; n++)
        {
            x = (x + 1) & 255; y = (y + s[x]) & 255;
            (s[x], s[y]) = (s[y], s[x]);
            o[n] = (byte)(data[n] ^ s[(s[x] + s[y]) & 255]);
        }
        return o;
    }

    private static byte[] Padded(string pw)
    {
        var b = Encoding.Latin1.GetBytes(pw);
        var r = new byte[32];
        Array.Copy(b, r, Math.Min(32, b.Length));
        if (b.Length < 32) Array.Copy(Pad, 0, r, b.Length, 32 - b.Length);
        return r;
    }

    private static string Hex(byte[] b) => "<" + Convert.ToHexString(b) + ">";

    /// <summary>
    /// A hand-built V4/R4 RC4 file (user password "", owner password "owner") with /EncryptMetadata
    /// false: its XMP stream is plain, its content stream is ciphertext unless <paramref name="clearStreams"/>
    /// names the /Identity crypt filter for streams.
    /// </summary>
    internal static byte[] BuildV4(bool clearStreams)
    {
        var id = Enumerable.Range(1, 16).Select(i => (byte)(i * 7)).ToArray();
        const int p = -4;

        // Algorithm 3: /O from the owner password.
        var d = MD5.HashData(Padded("owner"));
        for (int i = 0; i < 50; i++) d = MD5.HashData(d);
        var okey = d.AsSpan(0, 16).ToArray();
        var o = Rc4(okey, Padded(""));
        for (int i = 1; i <= 19; i++) o = Rc4(okey.Select(k => (byte)(k ^ i)).ToArray(), o);

        // Algorithm 2: the file key (EncryptMetadata false adds 0xFFFFFFFF).
        var pb = BitConverter.GetBytes(p);
        var h = MD5.HashData(Padded("").Concat(o).Concat(pb).Concat(id).Concat(new byte[] { 255, 255, 255, 255 }).ToArray());
        for (int i = 0; i < 50; i++) h = MD5.HashData(h.AsSpan(0, 16).ToArray());
        var key = h.AsSpan(0, 16).ToArray();

        // Algorithm 5: /U.
        var u = Rc4(key, MD5.HashData(Pad.Concat(id).ToArray()));
        for (int i = 1; i <= 19; i++) u = Rc4(key.Select(k => (byte)(k ^ i)).ToArray(), u);
        u = u.Concat(new byte[16]).ToArray();

        byte[] ObjKey(int num)
        {
            var k = MD5.HashData(key.Concat(new[] { (byte)num, (byte)(num >> 8), (byte)(num >> 16), (byte)0, (byte)0 }).ToArray());
            return k.AsSpan(0, 16).ToArray();
        }

        var content = Encoding.ASCII.GetBytes(ContentText);
        var contentOut = clearStreams ? content : Rc4(ObjKey(4), content);
        var xmp = Encoding.ASCII.GetBytes(XmpText);

        var ms = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        var off = new long[8];
        W("%PDF-1.6\n");
        off[1] = ms.Position; W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Metadata 5 0 R >>\nendobj\n");
        off[2] = ms.Position; W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        off[3] = ms.Position; W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Contents 4 0 R >>\nendobj\n");
        off[4] = ms.Position; W($"4 0 obj\n<< /Length {contentOut.Length} >>\nstream\n"); ms.Write(contentOut); W("\nendstream\nendobj\n");
        off[5] = ms.Position; W($"5 0 obj\n<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n"); ms.Write(xmp); W("\nendstream\nendobj\n");
        string stm = clearStreams ? "/Identity" : "/StdCF";
        off[6] = ms.Position;
        W($"6 0 obj\n<< /Filter /Standard /V 4 /R 4 /Length 128 /CF << /StdCF << /CFM /V2 /AuthEvent /DocOpen /Length 16 >> >> " +
          $"/StmF {stm} /StrF /StdCF /O {Hex(o)} /U {Hex(u)} /P {p} /EncryptMetadata false >>\nendobj\n");
        long xref = ms.Position;
        W("xref\n0 7\n0000000000 65535 f \n");
        for (int n = 1; n <= 6; n++) W($"{off[n]:D10} 00000 n \n");
        W($"trailer\n<< /Size 7 /Root 1 0 R /Encrypt 6 0 R /ID [{Hex(id)} {Hex(id)}] >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private const string ContentText = "BT /F1 12 Tf 20 100 Td (HELLOCONTENT) Tj ET";
    private const string XmpText = "<x:xmpmeta>CLEARXMPMARKER</x:xmpmeta>";

    private static string Safe(Func<string> f)
    {
        try { return f(); } catch (Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>A one-page document protected with the given passwords and level.</summary>
    private static string MakeProtected(string dir, string name, string user, string owner,
        PdfDocumentSecurityLevel level, bool noPrint)
    {
        string path = Path.Combine(dir, name);
        var doc = new PdfDocument();
        doc.AddPage();
        doc.Info.Title = "Protected title";
        doc.SecuritySettings.DocumentSecurityLevel = level;
        doc.SecuritySettings.UserPassword = user;
        doc.SecuritySettings.OwnerPassword = owner;
        doc.SecuritySettings.PermitPrint = !noPrint;
        doc.SecuritySettings.PermitModifyDocument = false;
        doc.Save(path);
        return path;
    }

    private static int PermissionsOf(string path, string pw)
    {
        using var d = PdfReader.Open(path, pw, PdfDocumentOpenMode.ReadOnly);
        return d.OriginalProtection!.Permissions;
    }

    public static void Run(Action<string, bool, string> check)
    {
        void Check(string label, bool ok, string detail = "") => check(label, ok, detail);
        Console.WriteLine();
        Console.WriteLine("── password-protected PDFs: user-password open, protection kept on save (#470) ─────");
        string dir = Path.Combine(Path.GetTempPath(), "tdpdf-pw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        foreach (var level in new[] { PdfDocumentSecurityLevel.Encrypted128Bit, PdfDocumentSecurityLevel.Encrypted40Bit })
        {
            string tag = level == PdfDocumentSecurityLevel.Encrypted128Bit ? "128-bit" : "40-bit";
            string src = MakeProtected(dir, $"src{tag}.pdf", "userpw", "ownerpw", level, noPrint: true);

            // The stock reader still refuses a user-password Modify open; the opt-in does not.
            string stock = Safe(() => { using var d = PdfReader.Open(src, "userpw", PdfDocumentOpenMode.Modify); return "opened"; });
            Check($"{tag}: the stock reader still demands the owner password for a user-password Modify open",
                  stock.Contains("owner", StringComparison.OrdinalIgnoreCase), stock);

            using var doc = PdfReader.OpenAllowingUserPassword(src, "userpw");
            var prot = doc.OriginalProtection;
            Check($"{tag}: a user-password open succeeds and records user-only access",
                  doc.PageCount == 1 && prot is { OpenedWithUserPasswordOnly: true, CanReapply: true, UserPassword: "userpw" }
                  && !doc.SecuritySettings.HasOwnerPermissions,
                  $"pages={doc.PageCount} prot={(prot == null ? "null" : prot.UserPassword)}");
            Check($"{tag}: the original /O is reusable, so the owner password will keep working", prot!.KeepsOwnerPassword);
            Check($"{tag}: nothing is reported as weakened", !prot.Downgraded);
            Check($"{tag}: the plain scan sees /Encrypt in the source", PdfEncryptionScan.FileHasEncryption(src));

            // Save through the shipping helper, which also reopens its own output.
            string outPath = Path.Combine(dir, $"out{tag}.pdf");
            ProtectedSave.Save(doc, prot, outPath);
            Check($"{tag}: a protected save writes /Encrypt", PdfEncryptionScan.FileHasEncryption(outPath));
            Check($"{tag}: the user password opens the saved file",
                  Safe(() => { using var d = PdfReader.Open(outPath, "userpw", PdfDocumentOpenMode.ReadOnly); return d.PageCount.ToString(); }) == "1");
            Check($"{tag}: the owner password still opens it with owner rights",
                  Safe(() => { using var d = PdfReader.Open(outPath, "ownerpw", PdfDocumentOpenMode.Modify); return d.SecuritySettings.HasOwnerPermissions ? "owner" : "user"; }) == "owner");
            Check($"{tag}: a wrong password is refused",
                  Safe(() => { using var d = PdfReader.Open(outPath, "nope", PdfDocumentOpenMode.ReadOnly); return "opened"; }) != "opened");
            Check($"{tag}: the permission flags survive unchanged",
                  PermissionsOf(outPath, "userpw") == prot.Permissions,
                  $"{PermissionsOf(outPath, "userpw"):X} vs {prot.Permissions:X}");
            Check($"{tag}: the strength is preserved",
                  Safe(() => { using var d = PdfReader.Open(outPath, "userpw", PdfDocumentOpenMode.ReadOnly); return d.OriginalProtection!.KeyBits.ToString(); })
                  == (level == PdfDocumentSecurityLevel.Encrypted128Bit ? "128" : "40"));

            // The protection was taken back off the live document: a later plain save is plain.
            string plain = Path.Combine(dir, $"plain{tag}.pdf");
            doc.Save(plain);
            Check($"{tag}: the live document saves unprotected again afterwards (snapshots stay plain)",
                  !PdfEncryptionScan.FileHasEncryption(plain) && Safe(() => { using var d = PdfReader.Open(plain, PdfDocumentOpenMode.Modify); return d.PageCount.ToString(); }) == "1");

            // Opened by the OWNER: the user password is recovered from /O, so a save needs no prompt.
            using var asOwner = PdfReader.Open(src, "ownerpw", PdfDocumentOpenMode.Modify);
            Check($"{tag}: an owner-password open recovers the user password",
                  asOwner.OriginalProtection is { UserPassword: "userpw", OwnerPassword: "ownerpw", OpenedWithUserPasswordOnly: false });
        }

        // Empty user password with owner restrictions: the file opens with no prompt, and still
        // re-protects (the empty user password is the user password).
        {
            string src = MakeProtected(dir, "emptyuser.pdf", "", "ownerpw", PdfDocumentSecurityLevel.Encrypted128Bit, noPrint: false);
            using var doc = PdfReader.OpenAllowingUserPassword(src, "");
            string outPath = Path.Combine(dir, "emptyuser-out.pdf");
            ProtectedSave.Save(doc, doc.OriginalProtection!, outPath);
            Check("empty user password: the saved file is still protected and opens with the empty password",
                  PdfEncryptionScan.FileHasEncryption(outPath)
                  && Safe(() => { using var d = PdfReader.Open(outPath, "", PdfDocumentOpenMode.ReadOnly); return d.PageCount.ToString(); }) == "1");
            Check("empty user password: the owner password still unlocks it",
                  Safe(() => { using var d = PdfReader.Open(outPath, "ownerpw", PdfDocumentOpenMode.Modify); return d.SecuritySettings.HasOwnerPermissions ? "owner" : "user"; }) == "owner");
        }

        // Not protected at all: nothing to carry.
        {
            string plainSrc = Path.Combine(dir, "plainsrc.pdf");
            var d0 = new PdfDocument(); d0.AddPage(); d0.Save(plainSrc);
            using var doc = PdfReader.Open(plainSrc, PdfDocumentOpenMode.Modify);
            Check("an unencrypted file has no OriginalProtection", doc.OriginalProtection is null);
            Check("ProtectedSave.For(null) is a plain save",
                  Safe(() => { string o = Path.Combine(dir, "plainout.pdf"); ProtectedSave.For(doc, null)(o); return PdfEncryptionScan.FileHasEncryption(o) ? "encrypted" : "plain"; }) == "plain");
        }

        // ── Unencrypted metadata (upstream #470) ───────────────────────────────────────────
        string v4 = Path.Combine(dir, "v4.pdf");
        File.WriteAllBytes(v4, BuildV4(clearStreams: false));
        {
            using var doc = PdfReader.OpenAllowingUserPassword(v4, "");
            string xmp = "", content = "";
            foreach (var iref in doc.Internals.GetAllObjects())
            {
                if (iref is PdfDictionary dict && dict.Stream != null)
                {
                    string text = Encoding.Latin1.GetString(dict.Stream.Value);
                    if (dict.Elements.GetName("/Type") == "/Metadata") xmp = text; else content = text;
                }
            }
            Check("/EncryptMetadata false: the plain XMP stream is left alone", xmp == XmpText, xmp);
            Check("/EncryptMetadata false: the content stream still decrypts", content == ContentText, content);
            Check("V4 file: protection is captured as RC4 128-bit at revision 4",
                  doc.OriginalProtection is { Revision: 4, KeyBits: 128, WasAes: false, CanReapply: true });
        }
        string v4id = Path.Combine(dir, "v4id.pdf");
        File.WriteAllBytes(v4id, BuildV4(clearStreams: true));
        {
            using var doc = PdfReader.OpenAllowingUserPassword(v4id, "");
            string content = "";
            foreach (var iref in doc.Internals.GetAllObjects())
                if (iref is PdfDictionary dict && dict.Stream != null && dict.Elements.GetName("/Type") != "/Metadata")
                    content = Encoding.Latin1.GetString(dict.Stream.Value);
            Check("/StmF /Identity: streams are passed through untouched", content == ContentText, content);
        }
        {
            // A V4 file re-saved through ProtectedSave keeps its owner password (the /O is reused).
            using var doc = PdfReader.OpenAllowingUserPassword(v4, "");
            string outPath = Path.Combine(dir, "v4-out.pdf");
            ProtectedSave.Save(doc, doc.OriginalProtection!, outPath);
            Check("V4 file: the re-protected copy keeps the owner password working",
                  Safe(() => { using var d = PdfReader.Open(outPath, "owner", PdfDocumentOpenMode.Modify); return d.SecuritySettings.HasOwnerPermissions ? "owner" : "user"; }) == "owner");
        }

        // ── The /Encrypt scan ───────────────────────────────────────────────────────────────
        Check("scan: /EncryptMetadata alone is not an /Encrypt key", !PdfEncryptionScan.ContainsEncryptKey("<< /EncryptMetadata false >>"));
        Check("scan: /Encrypt 6 0 R is", PdfEncryptionScan.ContainsEncryptKey("trailer << /Size 7 /Encrypt 6 0 R >>"));
        Check("scan: /Encrypt directly before a dictionary is", PdfEncryptionScan.ContainsEncryptKey("/Encrypt<</Filter/Standard>>"));
        Check("scan: a name that merely starts with /Encrypt is not", !PdfEncryptionScan.ContainsEncryptKey("/EncryptedPayload 1"));
        Check("scan: the hand-built V4 file is encrypted", PdfEncryptionScan.FileHasEncryption(v4));

        // Linearized: the first-page trailer is at the START; the closing trailer has only /Prev.
        {
            var sb = new StringBuilder("%PDF-1.5\n1 0 obj\n<< /Linearized 1 /L 90000 /O 3 >>\nendobj\n");
            sb.Append("xref\n0 1\n0000000000 65535 f \ntrailer\n<< /Size 9 /Root 2 0 R /Encrypt 8 0 R /ID [<00><00>] /Prev 80000 >>\nstartxref\n0\n%%EOF\n");
            sb.Append(new string('x', 200_000));
            sb.Append("\ntrailer\n<< /Size 9 /Prev 1234 >>\nstartxref\n1234\n%%EOF\n");
            string lin = Path.Combine(dir, "lin.pdf");
            File.WriteAllText(lin, sb.ToString(), Encoding.Latin1);
            Check("scan: a linearized file's first-page trailer is found though the tail has none",
                  PdfEncryptionScan.FileHasEncryption(lin));
        }
        // Big xref stream: /Encrypt in the stream dictionary, with the data pushing it past the tail window.
        {
            var ms = new MemoryStream();
            void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
            W("%PDF-1.5\n");
            W(new string('y', 100_000) + "\n");
            long x = ms.Position;
            W("9 0 obj\n<< /Type /XRef /Size 10 /W [1 4 2] /Root 1 0 R /Encrypt 8 0 R /Length 150000 >>\nstream\n");
            ms.Write(new byte[150_000]);
            W($"\nendstream\nendobj\nstartxref\n{x}\n%%EOF\n");
            string big = Path.Combine(dir, "bigxref.pdf");
            File.WriteAllBytes(big, ms.ToArray());
            Check("scan: /Encrypt in a large xref stream's dictionary is found via startxref",
                  PdfEncryptionScan.FileHasEncryption(big));
        }
        {
            string clean = Path.Combine(dir, "clean.pdf");
            File.WriteAllText(clean, "%PDF-1.5\n" + new string('z', 300_000) + "\ntrailer\n<< /Size 1 /Root 1 0 R >>\nstartxref\n0\n%%EOF\n", Encoding.Latin1);
            Check("scan: a plain file is not encrypted", !PdfEncryptionScan.FileHasEncryption(clean));
        }

        try { Directory.Delete(dir, true); } catch { }
    }
}
