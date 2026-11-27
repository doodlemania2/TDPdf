using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TDPdf.Services
{
    /// <summary>The ICC rendering intents, numbered as the ICC header and lcms2 number them.</summary>
    internal enum ColorIntent
    {
        Perceptual = 0,
        RelativeColorimetric = 1,
        Saturation = 2,
        AbsoluteColorimetric = 3,
    }

    /// <summary>A one-channel tone curve read from an ICC 'curv' or 'para' tag.</summary>
    internal sealed class IccToneCurve
    {
        private readonly Func<double, double> _eval;

        private IccToneCurve(Func<double, double> eval) { _eval = eval; }

        /// <summary>Encoded value (0..1) to linear light (0..1).</summary>
        public double ToLinear(double x) => _eval(Math.Clamp(x, 0.0, 1.0));

        /// <summary>The identity curve (a 'curv' tag with no entries).</summary>
        public static IccToneCurve Identity { get; } = new(x => x);

        public static IccToneCurve Gamma(double g) => new(x => Math.Pow(x, g));

        /// <summary>The sRGB curve, as the ICC parametric type 3.</summary>
        public static IccToneCurve Srgb { get; } = Parametric(3, [2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045, 0, 0]);

        /// <summary>A sampled curve: linear interpolation between evenly spaced entries.</summary>
        public static IccToneCurve Sampled(double[] table)
        {
            if (table.Length == 0) return Identity;
            if (table.Length == 1) return Gamma(table[0]);
            return new IccToneCurve(x =>
            {
                double p = x * (table.Length - 1);
                int i = (int)Math.Floor(p);
                if (i >= table.Length - 1) return table[^1];
                double f = p - i;
                return table[i] + (table[i + 1] - table[i]) * f;
            });
        }

        /// <summary>The five ICC parametric forms. Parameters are g, a, b, c, d, e, f in that order.</summary>
        public static IccToneCurve Parametric(int type, double[] p)
        {
            double g = p[0], a = p[1], b = p[2], c = p[3], d = p[4], e = p[5], f = p[6];
            static double Pos(double v) => v < 0 ? 0 : v;
            return type switch
            {
                0 => new(x => Math.Pow(x, g)),
                1 => new(x => a != 0 && x >= -b / a ? Math.Pow(Pos(a * x + b), g) : 0),
                2 => new(x => a != 0 && x >= -b / a ? Math.Pow(Pos(a * x + b), g) + c : c),
                3 => new(x => x >= d ? Math.Pow(Pos(a * x + b), g) : c * x),
                4 => new(x => x >= d ? Math.Pow(Pos(a * x + b), g) + e : c * x + f),
                _ => Identity,
            };
        }

        /// <summary>
        /// The 8-bit output code for a linear value, found by inverting the curve numerically. The
        /// returned table is indexed by linear light in 1/65535 steps.
        /// </summary>
        public byte[] BuildInverseLut8()
        {
            // The forward value of every 8-bit code, then for each linear step the code it falls at,
            // interpolated between neighbours in code space and rounded.
            var fwd = new double[256];
            for (int i = 0; i < 256; i++) fwd[i] = ToLinear(i / 255.0);
            // Force monotonic, as a profile with a noisy curve would otherwise break the search.
            for (int i = 1; i < 256; i++) if (fwd[i] < fwd[i - 1]) fwd[i] = fwd[i - 1];

            var lut = new byte[65536];
            int code = 0;
            for (int i = 0; i < lut.Length; i++)
            {
                double lin = i / 65535.0;
                while (code < 255 && fwd[code + 1] <= lin) code++;
                double v = code;
                if (code < 255 && fwd[code + 1] > fwd[code])
                    v = code + (lin - fwd[code]) / (fwd[code + 1] - fwd[code]);
                lut[i] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
            }
            return lut;
        }
    }

    /// <summary>
    /// A read-only view of an ICC profile: just enough to name it, classify it and, for the common
    /// RGB matrix/TRC kind, convert with it. Pure and cross-platform; tests/PdfCore compiles it as-is.
    /// A profile with <see cref="Bytes"/> null is the built-in sRGB, which has no file.
    /// </summary>
    internal sealed class IccProfile
    {
        private static readonly double[] SrgbRed = [0.4360747, 0.2225045, 0.0139322];
        private static readonly double[] SrgbGreen = [0.3850649, 0.7168786, 0.0971045];
        private static readonly double[] SrgbBlue = [0.1430804, 0.0606169, 0.7141733];

        public byte[]? Bytes { get; private init; }
        public string ColorSpace { get; private init; } = "RGB ";
        public string DeviceClass { get; private init; } = "mntr";
        public string Description { get; private init; } = "";
        public int MajorVersion { get; private init; } = 2;
        /// <summary>True when the profile carries lookup tables (A2B0), so a matrix reading would be wrong.</summary>
        public bool HasLut { get; private init; }
        /// <summary>The red, green and blue colorants as D50 XYZ, or null when the profile has no matrix.</summary>
        public double[][]? Colorants { get; private init; }
        public IccToneCurve[]? Curves { get; private init; }

        public bool IsRgb => ColorSpace == "RGB ";
        public bool IsCmyk => ColorSpace == "CMYK";
        public bool IsGray => ColorSpace == "GRAY";
        public bool IsBuiltInSrgb => Bytes is null;

        /// <summary>True for an RGB profile that is just three colorants and three curves.</summary>
        public bool IsMatrixTrc => IsRgb && !HasLut && Colorants is not null && Curves is not null;

        private string? _id;

        /// <summary>A stable key for caches: a hash of the bytes, or "srgb" for the built-in.</summary>
        public string Id => _id ??= Bytes is null ? "srgb" : Convert.ToHexString(SHA256.HashData(Bytes));

        /// <summary>The built-in sRGB (IEC 61966-2.1) profile.</summary>
        public static IccProfile Srgb { get; } = new()
        {
            Bytes = null,
            ColorSpace = "RGB ",
            DeviceClass = "mntr",
            Description = "sRGB IEC61966-2.1",
            Colorants = [SrgbRed, SrgbGreen, SrgbBlue],
            Curves = [IccToneCurve.Srgb, IccToneCurve.Srgb, IccToneCurve.Srgb],
        };

        /// <summary>Reads a profile from bytes, or returns null when they are not a usable ICC profile.</summary>
        public static IccProfile? Parse(byte[]? data)
        {
            try { return ParseCore(data); }
            catch { return null; }
        }

        private static IccProfile? ParseCore(byte[]? data)
        {
            if (data is null || data.Length < 132) return null;
            if (Encoding.ASCII.GetString(data, 36, 4) != "acsp") return null;
            uint declared = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
            if (declared < 132 || declared > data.Length) return null;

            uint tagCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(128, 4));
            if (tagCount > 1000 || 132 + (long)tagCount * 12 > data.Length) return null;
            var tags = new Dictionary<string, (int Offset, int Size)>();
            for (int i = 0; i < tagCount; i++)
            {
                int e = 132 + i * 12;
                string sig = Encoding.ASCII.GetString(data, e, 4);
                long off = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(e + 4, 4));
                long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(e + 8, 4));
                if (off < 128 || size < 8 || off + size > data.Length) continue;
                tags[sig] = ((int)off, (int)size);
            }

            bool hasLut = tags.ContainsKey("A2B0") || tags.ContainsKey("A2B1");
            double[][]? colorants = null;
            IccToneCurve[]? curves = null;
            if (tags.ContainsKey("rXYZ") && tags.ContainsKey("gXYZ") && tags.ContainsKey("bXYZ")
                && tags.ContainsKey("rTRC") && tags.ContainsKey("gTRC") && tags.ContainsKey("bTRC"))
            {
                var r = ReadXyz(data, tags["rXYZ"]);
                var g = ReadXyz(data, tags["gXYZ"]);
                var b = ReadXyz(data, tags["bXYZ"]);
                var cr = ReadCurve(data, tags["rTRC"]);
                var cg = ReadCurve(data, tags["gTRC"]);
                var cb = ReadCurve(data, tags["bTRC"]);
                if (r is not null && g is not null && b is not null && cr is not null && cg is not null && cb is not null)
                {
                    colorants = [r, g, b];
                    curves = [cr, cg, cb];
                }
            }

            return new IccProfile
            {
                Bytes = data,
                ColorSpace = Encoding.ASCII.GetString(data, 16, 4),
                DeviceClass = Encoding.ASCII.GetString(data, 12, 4),
                MajorVersion = data[8],
                Description = tags.TryGetValue("desc", out var d) ? ReadText(data, d) : "",
                HasLut = hasLut,
                Colorants = colorants,
                Curves = curves,
            };
        }

        private static double S15(byte[] d, int at) => BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(at, 4)) / 65536.0;

        private static double[]? ReadXyz(byte[] d, (int Offset, int Size) t)
        {
            if (t.Size < 20 || Encoding.ASCII.GetString(d, t.Offset, 4) != "XYZ ") return null;
            return [S15(d, t.Offset + 8), S15(d, t.Offset + 12), S15(d, t.Offset + 16)];
        }

        private static IccToneCurve? ReadCurve(byte[] d, (int Offset, int Size) t)
        {
            string type = Encoding.ASCII.GetString(d, t.Offset, 4);
            int end = t.Offset + t.Size;
            if (type == "curv")
            {
                if (t.Size < 12) return null;
                uint n = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(t.Offset + 8, 4));
                if (n == 0) return IccToneCurve.Identity;
                if (n > 65536 || t.Offset + 12 + (long)n * 2 > end) return null;
                if (n == 1) return IccToneCurve.Gamma(BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(t.Offset + 12, 2)) / 256.0);
                var table = new double[n];
                for (int i = 0; i < n; i++)
                    table[i] = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(t.Offset + 12 + i * 2, 2)) / 65535.0;
                return IccToneCurve.Sampled(table);
            }
            if (type == "para")
            {
                if (t.Size < 12) return null;
                int fn = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(t.Offset + 8, 2));
                int[] counts = [1, 3, 4, 5, 7];
                if (fn >= counts.Length || t.Offset + 12 + counts[fn] * 4 > end) return null;
                var p = new double[7];
                for (int i = 0; i < counts[fn]; i++) p[i] = S15(d, t.Offset + 12 + i * 4);
                return IccToneCurve.Parametric(fn, p);
            }
            return null;
        }

        private static string ReadText(byte[] d, (int Offset, int Size) t)
        {
            string type = Encoding.ASCII.GetString(d, t.Offset, 4);
            try
            {
                if (type == "desc" && t.Size >= 12)
                {
                    int n = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(t.Offset + 8, 4));
                    n = Math.Clamp(n, 0, t.Size - 12);
                    return Encoding.ASCII.GetString(d, t.Offset + 12, n).TrimEnd('\0');
                }
                if (type == "mluc" && t.Size >= 28)
                {
                    int len = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(t.Offset + 20, 4));
                    int off = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(t.Offset + 24, 4));
                    if (off >= 0 && len >= 0 && off + len <= t.Size)
                        return Encoding.BigEndianUnicode.GetString(d, t.Offset + off, len).TrimEnd('\0');
                }
            }
            catch { /* a bad name is not a bad profile */ }
            return "";
        }

        /// <summary>
        /// True when this profile describes sRGB, so a monitor with it needs no conversion. Matrix
        /// profiles are compared numerically (colorants and curve); a profile built from lookup tables
        /// is trusted only by its name.
        /// </summary>
        public bool IsSrgbLike()
        {
            if (IsBuiltInSrgb) return true;
            if (!IsRgb) return false;
            if (IsMatrixTrc)
            {
                double[][] want = [SrgbRed, SrgbGreen, SrgbBlue];
                for (int c = 0; c < 3; c++)
                    for (int k = 0; k < 3; k++)
                        if (Math.Abs(Colorants![c][k] - want[c][k]) > 0.004) return false;
                foreach (var curve in Curves!)
                    foreach (double x in new[] { 0.05, 0.2, 0.4, 0.6, 0.8, 0.95 })
                        if (Math.Abs(curve.ToLinear(x) - IccToneCurve.Srgb.ToLinear(x)) > 0.006) return false;
                return true;
            }
            return Description.Contains("sRGB", StringComparison.OrdinalIgnoreCase);
        }
    }
}
