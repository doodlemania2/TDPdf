using System.Text.RegularExpressions;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfSharpCore.Pdf.Security;
using TDPdf.Services;

/// <summary>
/// Every protected save gets a fresh first /ID. RC4 keys derive from it, so two revisions of one
/// file saved under one password must not share it.
/// </summary>
internal static class ProtectedSaveId
{
    private static string? FirstId(string path)
    {
        string raw = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path));
        var m = Regex.Match(raw, @"/ID\s*\[\s*<([0-9A-Fa-f]{32})>");
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        string src = Path.Combine(tmp, "id-src.pdf");
        var seed = new PdfDocument();
        seed.AddPage();
        seed.SecuritySettings.DocumentSecurityLevel = PdfDocumentSecurityLevel.Encrypted128Bit;
        seed.SecuritySettings.UserPassword = "userpw";
        seed.SecuritySettings.OwnerPassword = "ownerpw";
        seed.Save(src);
        string? srcId = FirstId(src);

        using var doc = PdfReader.OpenAllowingUserPassword(src, "userpw");
        var prot = doc.OriginalProtection!;
        string a = Path.Combine(tmp, "id-a.pdf"), b = Path.Combine(tmp, "id-b.pdf");
        ProtectedSave.Save(doc, prot, a);
        ProtectedSave.Save(doc, prot, b);
        string? idA = FirstId(a), idB = FirstId(b);

        Check("protected save: the source file has a first /ID (test premise)", srcId is not null, srcId ?? "none");
        Check("protected save: the first /ID is new, not the source's", idA is not null && idA != srcId, $"{srcId} -> {idA}");
        Check("protected save: two saves of one document get different first /IDs", idA is not null && idB is not null && idA != idB, $"{idA} / {idB}");
        Check("protected save: the new file still opens with the user password",
              PdfReader.Open(b, "userpw", PdfDocumentOpenMode.ReadOnly).PageCount == 1, "");
        Check("protected save: the generated ID is 16 bytes",
              ProtectedSave.NewDocumentId().Length == 16 && ProtectedSave.NewDocumentId() != ProtectedSave.NewDocumentId(), "");
    }
}
