using System.IO;
using System.Security.Cryptography;

namespace TDPdf.Services
{
    /// <summary>
    /// Where a Tesseract language model comes from and what it must hash to. The models are fetched on
    /// demand from the tesseract-ocr GitHub repos and then handed to native code (Tesseract/Leptonica
    /// parse them), so they are pinned twice: the URL names a fixed commit, not a branch, and the
    /// SHA-256 of every offered file is checked after download. A file that does not match is deleted.
    /// </summary>
    /// <remarks>
    /// To add a language or move to newer models: download each file from the new commit, recompute
    /// the hashes, and update <see cref="FastCommit"/> / <see cref="BestCommit"/> and both tables together.
    /// </remarks>
    internal static class OcrModelCatalog
    {
        /// <summary>tesseract-ocr/tessdata_fast, the standard tier.</summary>
        public const string FastCommit = "87416418657359cb625c412a48b6e1d6d41c29bd";
        /// <summary>tesseract-ocr/tessdata_best, the high-quality tier.</summary>
        public const string BestCommit = "e12c65a915945e4c28e237a9b52bc4a8f39a0cec";

        private static readonly Dictionary<string, string> Fast = new(StringComparer.Ordinal)
        {
            ["eng"] = "7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2",
            ["spa"] = "6f2e04d02774a18f01bed44b1111f2cd7f3ba7ac9dc4373cd3f898a40ea6b464",
            ["fra"] = "ced037562e8c80c13122dece28dd477d399af80911a28791a66a63ac1e3445ca",
            ["deu"] = "19d219bbb6672c869d20a9636c6816a81eb9a71796cb93ebe0cb1530e2cdb22d",
            ["ita"] = "b8f89e1e785118dac4d51ae042c029a64edb5c3ee42ef73027a6d412748d8827",
            ["por"] = "c4932b937207a9514b7514d518b931a99938c02a28a5a5a553f8599ed58b7deb",
            ["nld"] = "ced0e5e046a84c908a6aa7accbef9a232c4a5d9a8276691b81c6ee64d02963f6",
            ["tur"] = "7393381111e1152420fc4092cb44eef4237580d21b92bf30d7d221aad192c6b7",
            ["rus"] = "e16e5e036cce1d9ec2b00063cf8b54472625b9e14d893a169e2b0dedeb4df225",
            ["jpn"] = "1f5de9236d2e85f5fdf4b3c500f2d4926f8d9449f28f5394472d9e8d83b91b4d",
            ["kor"] = "6b85e11d9bbf07863b97b3523b1b112844c43e713df8b66418a081fd1060b3b2",
            ["chi_sim"] = "a5fcb6f0db1e1d6d8522f39db4e848f05984669172e584e8d76b6b3141e1f730",
            ["chi_tra"] = "529c5b5797d64b126065cd55f2bb4c7fd7b15790798091b1ff259941a829330b",
        };

        private static readonly Dictionary<string, string> Best = new(StringComparer.Ordinal)
        {
            ["eng"] = "8280aed0782fe27257a68ea10fe7ef324ca0f8d85bd2fd145d1c2b560bcb66ba",
            ["spa"] = "e2c1ffdad8b30f26c45d4017a9183d3a7f9aa69e59918be4f88b126fac99ab2c",
            ["fra"] = "907743d98915c91a3906dfbf6e48b97598346698fe53aaa797e1a064ffcac913",
            ["deu"] = "8407331d6aa0229dc927685c01a7938fc5a641d1a9524f74838cdac599f0d06e",
            ["ita"] = "8df9c89176fb93f56bf4b2d4ede04c01c1f31d4b7697fbd76cc336df700f3f38",
            ["por"] = "711de9dbb8052067bd42f16b9119967f30bada80d57e2ef24f65d09f531adb04",
            ["nld"] = "92e7a1ad4bf8082e268de57c7823316ec024935702c6ed2a1e473b3a071aa733",
            ["tur"] = "e0c3338dc17503dc7d335a507c9ae01b2b46cfd07561171e1e1ac55d85e8e438",
            ["rus"] = "b617eb6830ffabaaa795dd87ea7fd251adfe9cf0efe05eb9a2e8128b7728d6b6",
            ["jpn"] = "36bdf9ac823f5911e624c30d0553e890b8abc7c31a65b3ef14da943658c40b79",
            ["kor"] = "f888d4038348a0c3d25151e7f452bda0d74ca275b18cab146798bcbb94084fff",
            ["chi_sim"] = "4fef2d1306c8e87616d4d3e4c6c67faf5d44be3342290cf8f2f0f6e3aa7e735b",
            ["chi_tra"] = "1aa60488574cafa69486d919284f079ca9b68fcc7f6ad8dc1ff1b318dfd97028",
        };

        /// <summary>True if <paramref name="code"/> is a language we have a pinned hash for.</summary>
        public static bool IsKnown(string code) => Fast.ContainsKey(code);

        /// <summary>The pinned download URL for a language in the given tier, or null if it is not offered.</summary>
        public static string? UrlFor(string code, bool highQuality) =>
            IsKnown(code)
                ? highQuality
                    ? $"https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/{BestCommit}/{code}.traineddata"
                    : $"https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/{FastCommit}/{code}.traineddata"
                : null;

        /// <summary>The expected lowercase hex SHA-256 for a language in the given tier, or null if unknown.</summary>
        public static string? ExpectedSha256(string code, bool highQuality) =>
            (highQuality ? Best : Fast).TryGetValue(code, out var h) ? h : null;

        /// <summary>
        /// Hashes <paramref name="file"/> and compares it with the pinned value. An unknown language
        /// never verifies.
        /// </summary>
        public static bool Verify(string file, string code, bool highQuality)
        {
            string? expected = ExpectedSha256(code, highQuality);
            if (expected is null) return false;
            using var fs = File.OpenRead(file);
            string actual = Convert.ToHexString(SHA256.HashData(fs));
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Every language code offered (the same set for both tiers).</summary>
        public static IEnumerable<string> Codes => Fast.Keys;
    }
}
