using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TDPdf.Services
{
    /// <summary>
    /// A loaded lcms2 (Little CMS, MIT) native library. Function pointers are resolved by name from an
    /// explicit path, so nothing is ever found through the DLL search path.
    /// </summary>
    internal sealed class LcmsLibrary
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr OpenFromMem(IntPtr buffer, uint size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateSrgb();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Close(IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateXform(IntPtr inP, uint inFmt, IntPtr outP, uint outFmt, uint intent, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateProof(IntPtr inP, uint inFmt, IntPtr outP, uint outFmt, IntPtr proof, uint intent, uint proofIntent, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DeleteXform(IntPtr xform);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DoXform(IntPtr xform, IntPtr input, IntPtr output, uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetAlarm([MarshalAs(UnmanagedType.LPArray, SizeConst = 16)] ushort[] codes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Version();

        /// <summary>TYPE_BGRA_8 in lcms2.h: RGB, 8 bits, one extra channel, byte order swapped, extra first.</summary>
        internal const uint TypeBgra8 = 0x44499;
        internal const uint FlagBlackPointCompensation = 0x2000;
        internal const uint FlagGamutCheck = 0x1000;
        internal const uint FlagSoftProofing = 0x4000;
        internal const uint FlagCopyAlpha = 0x04000000;

        private readonly OpenFromMem _open;
        private readonly CreateSrgb _srgb;
        private readonly Close _close;
        private readonly CreateXform _create;
        private readonly CreateProof _proof;
        private readonly DeleteXform _delete;
        private readonly DoXform _do;
        private readonly SetAlarm _alarm;

        public string Path { get; }
        public int VersionNumber { get; }

        private LcmsLibrary(string path, IntPtr handle)
        {
            Path = path;
            T Get<T>(string name) where T : Delegate =>
                Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));
            _open = Get<OpenFromMem>("cmsOpenProfileFromMem");
            _srgb = Get<CreateSrgb>("cmsCreate_sRGBProfile");
            _close = Get<Close>("cmsCloseProfile");
            _create = Get<CreateXform>("cmsCreateTransform");
            _proof = Get<CreateProof>("cmsCreateProofingTransform");
            _delete = Get<DeleteXform>("cmsDeleteTransform");
            _do = Get<DoXform>("cmsDoTransform");
            _alarm = Get<SetAlarm>("cmsSetAlarmCodes");
            VersionNumber = Get<Version>("cmsGetEncodedCMMversion")();
        }

        /// <summary>Loads lcms2 from exactly <paramref name="path"/>. Null, with a reason, when it will not load.</summary>
        public static LcmsLibrary? TryLoad(string path, out string? reason)
        {
            reason = null;
            try
            {
                if (!File.Exists(path)) { reason = "lcms2 is not in this build."; return null; }
                var lib = new LcmsLibrary(path, NativeLibrary.Load(path));
                // lcms2 2.8 (2017) added the flag that carries alpha through a conversion.
                if (lib.VersionNumber < 2080) { reason = "The lcms2 library is older than 2.8."; return null; }
                return lib;
            }
            catch (Exception ex)
            {
                reason = "lcms2 could not be loaded: " + ex.Message;
                return null;
            }
        }

        internal IntPtr OpenProfile(IccProfile p)
        {
            if (p.Bytes is null) return _srgb();
            var h = GCHandle.Alloc(p.Bytes, GCHandleType.Pinned);
            try { return _open(h.AddrOfPinnedObject(), (uint)p.Bytes.Length); }
            finally { h.Free(); }
        }

        internal void CloseProfile(IntPtr p) { if (p != IntPtr.Zero) _close(p); }
        internal IntPtr CreateTransform(IntPtr a, IntPtr b, ColorIntent i, uint flags) => _create(a, TypeBgra8, b, TypeBgra8, (uint)i, flags);
        internal IntPtr CreateProofing(IntPtr a, IntPtr display, IntPtr proof, ColorIntent i, uint flags) => _proof(a, TypeBgra8, display, TypeBgra8, proof, (uint)i, (uint)i, flags);
        internal void DeleteTransform(IntPtr x) => _delete(x);
        internal void Do(IntPtr x, IntPtr input, IntPtr output, uint count) => _do(x, input, output, count);

        /// <summary>Out-of-gamut colour for a gamut warning. Global to lcms2, so set once and never changed.</summary>
        internal void SetGamutAlarmGrey()
        {
            var codes = new ushort[16];
            codes[0] = codes[1] = codes[2] = 0x8000;
            _alarm(codes);
        }
    }

    /// <summary>lcms2 behind <see cref="IColorEngine"/>: every profile kind, every intent, black-point compensation, proofing.</summary>
    internal sealed class LcmsColorEngine(LcmsLibrary lib) : IColorEngine
    {
        public string Name => $"lcms2 {lib.VersionNumber / 1000}.{lib.VersionNumber / 10 % 100}";
        public bool IsNative => true;

        public IColorTransform? CreateTransform(IccProfile source, IccProfile destination, ColorIntent intent,
                                                bool blackPointCompensation, out string? reason)
        {
            reason = null;
            IntPtr a = lib.OpenProfile(source), b = lib.OpenProfile(destination);
            try
            {
                if (a == IntPtr.Zero || b == IntPtr.Zero) { reason = "A profile could not be read."; return null; }
                uint flags = LcmsColorEngineFlags(blackPointCompensation) | LcmsLibrary.FlagCopyAlpha;
                IntPtr x = lib.CreateTransform(a, b, intent, flags);
                if (x == IntPtr.Zero) { reason = "The profiles cannot be combined for this kind of image."; return null; }
                return new LcmsTransform(lib, x);
            }
            finally { lib.CloseProfile(a); lib.CloseProfile(b); }
        }

        public IColorTransform? CreateProofingTransform(IccProfile source, IccProfile proof, IccProfile display,
                                                        ColorIntent intent, bool blackPointCompensation,
                                                        bool gamutWarning, out string? reason)
        {
            reason = null;
            IntPtr a = lib.OpenProfile(source), p = lib.OpenProfile(proof), d = lib.OpenProfile(display);
            try
            {
                if (a == IntPtr.Zero || p == IntPtr.Zero || d == IntPtr.Zero) { reason = "A profile could not be read."; return null; }
                uint flags = LcmsColorEngineFlags(blackPointCompensation) | LcmsLibrary.FlagCopyAlpha | LcmsLibrary.FlagSoftProofing;
                if (gamutWarning) { flags |= LcmsLibrary.FlagGamutCheck; lib.SetGamutAlarmGrey(); }
                IntPtr x = lib.CreateProofing(a, d, p, intent, flags);
                if (x == IntPtr.Zero) { reason = "The profiles cannot be combined for this kind of image."; return null; }
                return new LcmsTransform(lib, x);
            }
            finally { lib.CloseProfile(a); lib.CloseProfile(p); lib.CloseProfile(d); }
        }

        private static uint LcmsColorEngineFlags(bool bpc) => bpc ? LcmsLibrary.FlagBlackPointCompensation : 0;
    }

    internal sealed class LcmsTransform : IColorTransform, IDisposable
    {
        private const int RowsPerChunk = 64;
        private readonly LcmsLibrary _lib;
        private IntPtr _handle;

        internal LcmsTransform(LcmsLibrary lib, IntPtr handle) { _lib = lib; _handle = handle; }

        public bool IsIdentity => false;

        public void ApplyBgra(byte[] bgra, int width, int height)
        {
            if (width <= 0 || height <= 0 || (long)width * height * 4 > bgra.Length) return;
            IntPtr x = _handle;
            if (x == IntPtr.Zero) return;
            int chunks = (height + RowsPerChunk - 1) / RowsPerChunk;
            Parallel.For(0, chunks,
                () => new byte[RowsPerChunk * width * 4],
                (chunk, _, scratch) =>
                {
                    int y0 = chunk * RowsPerChunk;
                    int rows = Math.Min(RowsPerChunk, height - y0);
                    int bytes = rows * width * 4;
                    Buffer.BlockCopy(bgra, y0 * width * 4, scratch, 0, bytes);
                    var src = GCHandle.Alloc(scratch, GCHandleType.Pinned);
                    var dst = GCHandle.Alloc(bgra, GCHandleType.Pinned);
                    try
                    {
                        _lib.Do(x, src.AddrOfPinnedObject(),
                                dst.AddrOfPinnedObject() + y0 * width * 4, (uint)(rows * width));
                    }
                    finally { src.Free(); dst.Free(); }
                    return scratch;
                },
                _ => { });
            GC.KeepAlive(this);
        }

        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (h != IntPtr.Zero) _lib.DeleteTransform(h);
            GC.SuppressFinalize(this);
        }

        ~LcmsTransform() => Dispose();
    }

    /// <summary>
    /// Finds the bundled liblcms2-2.dll the way <c>OcrNativeBootstrap</c> finds the OCR libraries (embedded
    /// resource, extracted to a per-version cache, loaded by exact path), with one addition: the
    /// library is used only when its SHA-256 equals <see cref="PinnedSha256"/>. With no pin, or no
    /// binary in the build, the managed engine is used and nothing native loads.
    /// </summary>
    /// <remarks>
    /// The bundled file is the MSYS2 UCRT64 build of lcms2 2.20 (third_party/lcms2/win-x64/liblcms2-2.dll,
    /// provenance in third_party/lcms2/SOURCES.md). It keeps its MSYS2 name because it is always loaded by
    /// full path, never imported by name. It imports only Windows system libraries (kernel32 and the
    /// Universal CRT), so there is nothing else to extract. To update it, replace the file, SHA256SUMS and
    /// <see cref="PinnedSha256"/> together; TDPdf.csproj stops the build and a test fails if they disagree.
    /// </remarks>
    internal static class LcmsBootstrap
    {
        /// <summary>Hex SHA-256 of the one lcms2.dll this build may load. MSYS2 UCRT64 mingw-w64-lcms2 2.20-1; see third_party/lcms2/SOURCES.md.</summary>
        internal const string PinnedSha256 = "54ce5a43219a39a52eafd10f40180f75b659dda59c40204795d9be13350055a2";

        internal const string ResourceName = "TDPdf.ColorNative.liblcms2-2.dll";
        private const string FileName = "liblcms2-2.dll";
        private const uint LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x00000100;
        private const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32", EntryPoint = "LoadLibraryExW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        /// <summary>True when <paramref name="bytes"/> are exactly the pinned library.</summary>
        internal static bool MatchesPin(byte[] bytes, string pin = PinnedSha256)
        {
            if (string.IsNullOrEmpty(pin) || pin.Length != 64) return false;
            string actual = Convert.ToHexString(SHA256.HashData(bytes));
            return string.Equals(actual, pin, StringComparison.OrdinalIgnoreCase);
        }

        private static readonly object Gate = new();
        private static bool _tried;
        private static LcmsColorEngine? _engine;
        private static string? _reason;

        /// <summary>The lcms2 engine, or null with the reason it is not available.</summary>
        public static IColorEngine? TryCreateEngine(out string? reason)
        {
            lock (Gate)
            {
                if (!_tried)
                {
                    _tried = true;
                    _engine = Create(out _reason);
                }
                reason = _engine is null ? _reason : null;
                return _engine;
            }
        }

        private static LcmsColorEngine? Create(out string? reason)
        {
            reason = null;
            if (!OperatingSystem.IsWindows()) { reason = "The lcms2 library is bundled for Windows only."; return null; }
            if (string.IsNullOrEmpty(PinnedSha256)) { reason = "This build does not include the lcms2 colour engine."; return null; }
            try
            {
                var asm = typeof(LcmsBootstrap).Assembly;
                using var res = asm.GetManifestResourceStream(ResourceName);
                if (res is null) { reason = "This build does not include the lcms2 colour engine."; return null; }
                using var ms = new MemoryStream();
                res.CopyTo(ms);
                byte[] bytes = ms.ToArray();
                if (!MatchesPin(bytes)) { reason = "The bundled lcms2 library does not match its recorded hash, so it was not loaded."; return null; }

                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TDPdf", "color", asm.GetName().Version?.ToString() ?? "0");
                Directory.CreateDirectory(dir);
                string target = Path.Combine(dir, FileName);
                if (!File.Exists(target) || !MatchesPin(File.ReadAllBytes(target)))
                {
                    string tmp = $"{target}.{Guid.NewGuid():N}.tmp";
                    try
                    {
                        using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            f.Write(bytes);
                        File.Move(tmp, target, overwrite: true);
                    }
                    finally { if (File.Exists(tmp)) File.Delete(tmp); }
                }
                // Checked again after the write: what is loaded is what was hashed.
                if (!MatchesPin(File.ReadAllBytes(target))) { reason = "The extracted lcms2 library did not match its recorded hash."; return null; }

                LoadLibraryEx(target, IntPtr.Zero, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
                var lib = LcmsLibrary.TryLoad(target, out reason);
                return lib is null ? null : new LcmsColorEngine(lib);
            }
            catch (Exception ex)
            {
                reason = "The lcms2 colour engine could not start: " + ex.Message;
                return null;
            }
        }
    }
}
