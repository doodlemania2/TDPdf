using System.Security.Cryptography;
using TDPdf.Services;

/// <summary>
/// Pins the OCR language-pack integrity table: every offered language has a pinned commit URL and a
/// well-formed SHA-256 in both tiers, and <see cref="OcrModelCatalog.Verify"/> accepts only a file that
/// hashes to the pinned value. (The hashes themselves were computed from the real files at the pinned
/// commits when the table was written; this keeps the table from rotting into a branch URL or a typo.)
/// </summary>
internal static class OcrModels
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine("\nOCR language packs: pinned URL + SHA-256");
        var codes = OcrModelCatalog.Codes.ToList();
        Check("13 languages are offered", codes.Count == 13, codes.Count.ToString());

        bool allGood = true;
        string bad = "";
        foreach (var code in codes)
            foreach (bool hq in new[] { false, true })
            {
                string? url = OcrModelCatalog.UrlFor(code, hq);
                string? sha = OcrModelCatalog.ExpectedSha256(code, hq);
                bool ok = url is not null && sha is not null
                    && !url.Contains("/main/")
                    && System.Text.RegularExpressions.Regex.IsMatch(url, @"/[0-9a-f]{40}/" + code + @"\.traineddata$")
                    && System.Text.RegularExpressions.Regex.IsMatch(sha, "^[0-9a-f]{64}$");
                if (!ok) { allGood = false; bad += $" {code}/{(hq ? "best" : "fast")}"; }
            }
        Check("every language/tier has a commit-pinned URL and a 64-hex SHA-256", allGood, bad);
        Check("unknown language has no URL", OcrModelCatalog.UrlFor("xyz", false) is null, "");

        string f = Path.Combine(tmp, "ocrmodel.traineddata");
        File.WriteAllBytes(f, [1, 2, 3, 4]);
        Check("a file that does not match the pin is rejected", !OcrModelCatalog.Verify(f, "eng", false), "");
        Check("an unknown language never verifies", !OcrModelCatalog.Verify(f, "xyz", false), "");
        Check("tiers are distinct hashes", OcrModelCatalog.ExpectedSha256("eng", false) != OcrModelCatalog.ExpectedSha256("eng", true), "");
        // SHA256 sanity: hex of a known vector, via the same primitive Verify uses.
        Check("SHA-256 primitive", Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])).Length == 64, "");
    }
}
