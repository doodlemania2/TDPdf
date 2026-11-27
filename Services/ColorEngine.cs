namespace TDPdf.Services
{
    /// <summary>
    /// A ready colour conversion over 8-bit BGRA pixels. Alpha is never touched. Safe to share and to
    /// call from any thread.
    /// </summary>
    internal interface IColorTransform
    {
        /// <summary>True when applying it changes nothing, so callers can skip the copy.</summary>
        bool IsIdentity { get; }

        /// <summary>Converts <paramref name="bgra"/> (width x height, 4 bytes a pixel) in place.</summary>
        void ApplyBgra(byte[] bgra, int width, int height);
    }

    /// <summary>
    /// The seam between TDPdf and a colour-conversion library. The managed engine below always
    /// exists and handles sRGB and RGB matrix profiles; the lcms2 engine (<see cref="LcmsColorEngine"/>)
    /// replaces it for everything else (printer and CMYK profiles, black-point compensation,
    /// gamut warning) when its native library is present.
    /// </summary>
    internal interface IColorEngine
    {
        string Name { get; }
        bool IsNative { get; }

        /// <summary>
        /// Source to destination. Null when this engine cannot do it; <paramref name="reason"/> then
        /// says why, in words a user can read.
        /// </summary>
        IColorTransform? CreateTransform(IccProfile source, IccProfile destination, ColorIntent intent,
                                         bool blackPointCompensation, out string? reason);

        /// <summary>
        /// Source through <paramref name="proof"/> (how the page will look on that device) to
        /// <paramref name="display"/>. With <paramref name="gamutWarning"/>, colours the proof cannot
        /// reproduce are painted grey, when the engine can tell.
        /// </summary>
        IColorTransform? CreateProofingTransform(IccProfile source, IccProfile proof, IccProfile display,
                                                 ColorIntent intent, bool blackPointCompensation,
                                                 bool gamutWarning, out string? reason);
    }

    /// <summary>Which engine the app uses: lcms2 when it loaded, the managed one otherwise.</summary>
    internal static class ColorEngines
    {
        private static IColorEngine? _override;
        private static readonly object Gate = new();
        private static IColorEngine? _resolved;

        public static IColorEngine Managed { get; } = new ManagedColorEngine();

        /// <summary>The engine to use. Tests can replace it with <see cref="Use"/>.</summary>
        public static IColorEngine Current
        {
            get
            {
                if (_override is { } o) return o;
                lock (Gate)
                {
                    return _resolved ??= LcmsBootstrap.TryCreateEngine(out _) ?? Managed;
                }
            }
        }

        /// <summary>Why the native engine is not in use, or null when it is. For the settings text.</summary>
        public static string? NativeUnavailableReason
        {
            get
            {
                if (Current.IsNative) return null;
                LcmsBootstrap.TryCreateEngine(out var reason);
                return reason;
            }
        }

        internal static void Use(IColorEngine? engine) => _override = engine;
    }

    /// <summary>
    /// Pure managed conversions between RGB matrix/TRC profiles (what almost every monitor profile is)
    /// and sRGB. It does not do lookup-table profiles, so printer and CMYK profiles need lcms2.
    /// Matrix profiles have no black-point or perceptual tables, so every intent converts the same
    /// way and black-point compensation changes nothing.
    /// </summary>
    internal sealed class ManagedColorEngine : IColorEngine
    {
        public string Name => "Managed (sRGB and matrix profiles)";
        public bool IsNative => false;

        public IColorTransform? CreateTransform(IccProfile source, IccProfile destination, ColorIntent intent,
                                                bool blackPointCompensation, out string? reason)
        {
            reason = null;
            if (source.IsSrgbLike() && destination.IsSrgbLike()) return IdentityTransform.Instance;
            if (!source.IsMatrixTrc || !destination.IsMatrixTrc)
            {
                reason = "This profile needs the lcms2 colour engine, which is not available in this build.";
                return null;
            }
            return new MatrixTransform(source, destination);
        }

        public IColorTransform? CreateProofingTransform(IccProfile source, IccProfile proof, IccProfile display,
                                                        ColorIntent intent, bool blackPointCompensation,
                                                        bool gamutWarning, out string? reason)
        {
            var first = CreateTransform(source, proof, intent, blackPointCompensation, out reason);
            if (first is null) return null;
            var second = CreateTransform(proof, display, intent, blackPointCompensation, out reason);
            if (second is null) return null;
            if (gamutWarning) reason = "The gamut warning needs the lcms2 colour engine, which is not available in this build.";
            if (first.IsIdentity) return second;
            if (second.IsIdentity) return first;
            return new ChainedTransform(first, second);
        }
    }

    internal sealed class IdentityTransform : IColorTransform
    {
        public static IdentityTransform Instance { get; } = new();
        public bool IsIdentity => true;
        public void ApplyBgra(byte[] bgra, int width, int height) { }
    }

    internal sealed class ChainedTransform(IColorTransform first, IColorTransform second) : IColorTransform
    {
        public bool IsIdentity => first.IsIdentity && second.IsIdentity;

        public void ApplyBgra(byte[] bgra, int width, int height)
        {
            first.ApplyBgra(bgra, width, height);
            second.ApplyBgra(bgra, width, height);
        }
    }

    /// <summary>Source TRC, 3x3 through D50 XYZ, destination inverse TRC; one table lookup per channel.</summary>
    internal sealed class MatrixTransform : IColorTransform
    {
        private readonly float[][] _lin = new float[3][];     // [channel][8-bit code] -> linear light
        private readonly byte[][] _enc = new byte[3][];       // [channel][linear*65535] -> 8-bit code
        private readonly float[] _m = new float[9];           // linear source RGB -> linear destination RGB

        public bool IsIdentity => false;

        public MatrixTransform(IccProfile src, IccProfile dst)
        {
            for (int c = 0; c < 3; c++)
            {
                var lin = new float[256];
                for (int v = 0; v < 256; v++) lin[v] = (float)src.Curves![c].ToLinear(v / 255.0);
                _lin[c] = lin;
                _enc[c] = dst.Curves![c].BuildInverseLut8();
            }

            // Columns of each matrix are the colorants.
            double[,] ms = Columns(src.Colorants!);
            double[,] md = Invert(Columns(dst.Colorants!));
            for (int r = 0; r < 3; r++)
                for (int k = 0; k < 3; k++)
                {
                    double s = 0;
                    for (int j = 0; j < 3; j++) s += md[r, j] * ms[j, k];
                    _m[r * 3 + k] = (float)s;
                }
        }

        private static double[,] Columns(double[][] colorants)
        {
            var m = new double[3, 3];
            for (int c = 0; c < 3; c++)
                for (int r = 0; r < 3; r++) m[r, c] = colorants[c][r];
            return m;
        }

        private static double[,] Invert(double[,] a)
        {
            double det = a[0, 0] * (a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1])
                       - a[0, 1] * (a[1, 0] * a[2, 2] - a[1, 2] * a[2, 0])
                       + a[0, 2] * (a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0]);
            if (Math.Abs(det) < 1e-12) throw new InvalidOperationException("Singular colorant matrix.");
            double id = 1.0 / det;
            return new[,]
            {
                { (a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1]) * id, (a[0, 2] * a[2, 1] - a[0, 1] * a[2, 2]) * id, (a[0, 1] * a[1, 2] - a[0, 2] * a[1, 1]) * id },
                { (a[1, 2] * a[2, 0] - a[1, 0] * a[2, 2]) * id, (a[0, 0] * a[2, 2] - a[0, 2] * a[2, 0]) * id, (a[0, 2] * a[1, 0] - a[0, 0] * a[1, 2]) * id },
                { (a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0]) * id, (a[0, 1] * a[2, 0] - a[0, 0] * a[2, 1]) * id, (a[0, 0] * a[1, 1] - a[0, 1] * a[1, 0]) * id },
            };
        }

        public void ApplyBgra(byte[] bgra, int width, int height)
        {
            if (width <= 0 || height <= 0 || (long)width * height * 4 > bgra.Length) return;
            Parallel.For(0, height, y =>
            {
                int p = y * width * 4;
                for (int x = 0; x < width; x++, p += 4)
                {
                    float r = _lin[0][bgra[p + 2]], g = _lin[1][bgra[p + 1]], b = _lin[2][bgra[p]];
                    float or = _m[0] * r + _m[1] * g + _m[2] * b;
                    float og = _m[3] * r + _m[4] * g + _m[5] * b;
                    float ob = _m[6] * r + _m[7] * g + _m[8] * b;
                    bgra[p + 2] = _enc[0][Index(or)];
                    bgra[p + 1] = _enc[1][Index(og)];
                    bgra[p] = _enc[2][Index(ob)];
                }
            });
        }

        private static int Index(float linear)
        {
            if (linear <= 0f) return 0;
            if (linear >= 1f) return 65535;
            return (int)(linear * 65535f + 0.5f);
        }
    }
}
