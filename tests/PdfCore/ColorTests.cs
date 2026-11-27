using System.Text;
using TDPdf.Services;

/// <summary>
/// #236 colour management: ICC reading, the managed engine, the lcms2 seam (against a system lcms2 when
/// one exists), the monitor-matching decision, the print ticket rewrite and, above all, the proof that
/// "Driver default" leaves the print ticket and the printed pixels exactly as they were.
/// </summary>
internal static class ColorTests
{
    // ---- a tiny ICC writer: a matrix/TRC RGB profile, or a stub with a lookup table ---------------------------------

    private static byte[] Be32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    private static byte[] S15(double v) => Be32((uint)(int)Math.Round(v * 65536));

    private static byte[] Xyz(double[] c) => [.. Encoding.ASCII.GetBytes("XYZ "), .. Be32(0), .. S15(c[0]), .. S15(c[1]), .. S15(c[2])];

    private static byte[] CurveGamma(double g) =>
        [.. Encoding.ASCII.GetBytes("curv"), .. Be32(0), .. Be32(1), (byte)((int)Math.Round(g * 256) >> 8), (byte)((int)Math.Round(g * 256) & 0xFF), 0, 0];

    private static byte[] CurveSrgb() =>
        [.. Encoding.ASCII.GetBytes("para"), .. Be32(0), 0, 3, 0, 0,
         .. S15(2.4), .. S15(1 / 1.055), .. S15(0.055 / 1.055), .. S15(1 / 12.92), .. S15(0.04045)];

    private static byte[] Desc(string text)
    {
        var ascii = Encoding.ASCII.GetBytes(text + "\0");
        return [.. Encoding.ASCII.GetBytes("desc"), .. Be32(0), .. Be32((uint)ascii.Length), .. ascii, .. new byte[78]];
    }

    private static byte[] Profile(string space, string cls, string desc, double[][]? colorants, byte[]? curve, bool lut = false)
    {
        var tags = new List<(string Sig, byte[] Data)> { ("desc", Desc(desc)) };
        if (colorants is not null && curve is not null)
        {
            tags.Add(("rXYZ", Xyz(colorants[0]))); tags.Add(("gXYZ", Xyz(colorants[1]))); tags.Add(("bXYZ", Xyz(colorants[2])));
            tags.Add(("rTRC", curve)); tags.Add(("gTRC", curve)); tags.Add(("bTRC", curve));
        }
        if (lut) tags.Add(("A2B0", new byte[32]));

        int tableEnd = 128 + 4 + tags.Count * 12;
        var body = new List<byte>();
        var table = new List<byte>(Be32((uint)tags.Count));
        foreach (var (sig, data) in tags)
        {
            int pad = (4 - data.Length % 4) % 4;
            table.AddRange(Encoding.ASCII.GetBytes(sig));
            table.AddRange(Be32((uint)(tableEnd + body.Count)));
            table.AddRange(Be32((uint)data.Length));
            body.AddRange(data); body.AddRange(new byte[pad]);
        }
        var header = new byte[128];
        Array.Copy(Be32((uint)(tableEnd + body.Count)), 0, header, 0, 4);
        header[8] = 2; header[9] = 0x10;
        Array.Copy(Encoding.ASCII.GetBytes(cls), 0, header, 12, 4);
        Array.Copy(Encoding.ASCII.GetBytes(space), 0, header, 16, 4);
        Array.Copy(Encoding.ASCII.GetBytes("XYZ "), 0, header, 20, 4);
        Array.Copy(Encoding.ASCII.GetBytes("acsp"), 0, header, 36, 4);
        return [.. header, .. table, .. body];
    }

    private static readonly double[][] SrgbColorants =
        [[0.4360747, 0.2225045, 0.0139322], [0.3850649, 0.7168786, 0.0971045], [0.1430804, 0.0606169, 0.7141733]];
    private static readonly double[][] AdobeColorants =
        [[0.60974, 0.31111, 0.01947], [0.20528, 0.62567, 0.06087], [0.14919, 0.06322, 0.74457]];

    private static byte[] SrgbBytes() => Profile("RGB ", "mntr", "sRGB IEC61966-2.1", SrgbColorants, CurveSrgb());
    private static byte[] AdobeBytes() => Profile("RGB ", "mntr", "Adobe RGB (1998)", AdobeColorants, CurveGamma(2.19921875));

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(x => x).ToArray();
    private static byte[] Px(byte r, byte g, byte b, byte a = 255) => [b, g, r, a];

    private static string? FindLcms()
    {
        string[] candidates =
        [
            "/opt/homebrew/opt/lcms2/lib/liblcms2.dylib", "/opt/homebrew/lib/liblcms2.dylib", "/usr/local/lib/liblcms2.dylib",
            "/usr/lib/x86_64-linux-gnu/liblcms2.so.2", "/usr/lib/aarch64-linux-gnu/liblcms2.so.2", "/usr/lib64/liblcms2.so.2",
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private const string SampleTicket = """
        <?xml version="1.0" encoding="UTF-8"?>
        <psf:PrintTicket xmlns:psf="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework"
                         xmlns:psk="http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords"
                         xmlns:vnd="http://vendor.example/ns" version="1">
          <psf:Feature name="psk:JobStapleAllDocuments"><psf:Option name="psk:StapleTopLeft"/></psf:Feature>
          <psf:Feature name="psk:JobOutputBin"><psf:Option name="psk:Bin2"/></psf:Feature>
          <psf:Feature name="psk:PageOutputColor"><psf:Option name="psk:Color"/></psf:Feature>
          <psf:Feature name="vnd:SecurePrintPin"><psf:Option name="vnd:Pin4821"/></psf:Feature>
          <psf:ParameterInit name="psk:JobCopiesAllDocuments"><psf:Value xsi:type="xsd:integer"
              xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">1</psf:Value></psf:ParameterInit>
        </psf:PrintTicket>
        """;

    private const string SampleCaps = """
        <?xml version="1.0" encoding="UTF-8"?>
        <psf:PrintCapabilities xmlns:psf="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework"
                               xmlns:psk="http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords" version="1">
          <psf:Feature name="psk:PageColorManagement">
            <psf:Option name="psk:Driver"/><psf:Option name="psk:None"/>
          </psf:Feature>
        </psf:PrintCapabilities>
        """;

    private static MemoryStream Xml(string s) => new(Encoding.UTF8.GetBytes(s));

    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nColour management: ICC, engines, monitor decision, print ticket");

        // ---- ICC parsing ------------------------------------------------------------------------------------------
        var srgb = IccProfile.Parse(SrgbBytes());
        var adobe = IccProfile.Parse(AdobeBytes());
        Check("icc: an sRGB matrix profile reads as a matrix profile with its name",
              srgb is { IsMatrixTrc: true, Description: "sRGB IEC61966-2.1", IsRgb: true }, $"{srgb?.Description}");
        Check("icc: it is recognised as sRGB", srgb is not null && srgb.IsSrgbLike(), "");
        Check("icc: Adobe RGB is not sRGB", adobe is { IsMatrixTrc: true } && !adobe.IsSrgbLike(), "");
        Check("icc: the built-in sRGB is sRGB", IccProfile.Srgb.IsSrgbLike() && IccProfile.Srgb.Id == "srgb", "");
        Check("icc: garbage is not a profile", IccProfile.Parse(new byte[300]) is null && IccProfile.Parse(null) is null && IccProfile.Parse([1, 2, 3]) is null, "");
        var truncated = SrgbBytes()[..140];
        Check("icc: a truncated profile is refused, not thrown on", IccProfile.Parse(truncated) is null, "");
        var cmyk = IccProfile.Parse(Profile("CMYK", "prtr", "Printer CMYK", null, null, lut: true));
        Check("icc: a lookup-table CMYK profile is classified, and is not a matrix profile",
              cmyk is { IsCmyk: true, HasLut: true, IsMatrixTrc: false }, "");
        var lutNamedSrgb = IccProfile.Parse(Profile("RGB ", "mntr", "sRGB calibrated", null, null, lut: true));
        Check("icc: a table-based RGB profile is trusted as sRGB only by name", lutNamedSrgb is not null && lutNamedSrgb.IsSrgbLike(), "");
        Check("icc: different bytes give different ids", srgb!.Id != adobe!.Id && srgb.Id == IccProfile.Parse(SrgbBytes())!.Id, "");

        // ---- Managed engine -------------------------------------------------------------------------------------------
        var managed = ColorEngines.Managed;
        var same = managed.CreateTransform(IccProfile.Srgb, srgb, ColorIntent.RelativeColorimetric, false, out var why0);
        Check("managed: sRGB to an sRGB profile is the identity", same is { IsIdentity: true } && why0 is null, "");
        var toAdobe = managed.CreateTransform(IccProfile.Srgb, adobe, ColorIntent.RelativeColorimetric, false, out _);
        Check("managed: sRGB to Adobe RGB is a real transform", toAdobe is { IsIdentity: false }, "");
        if (toAdobe is not null)
        {
            var px = Cat(Px(255, 0, 0, 200), Px(255, 255, 255), Px(0, 0, 0), Px(128, 128, 128, 7));
            toAdobe.ApplyBgra(px, 4, 1);
            Check("managed: sRGB red lands near Adobe RGB 218,0,0", Math.Abs(px[2] - 218) <= 3 && px[1] <= 2 && px[0] <= 2, $"{px[2]},{px[1]},{px[0]}");
            Check("managed: white and black stay put", px[4] == 255 && px[5] == 255 && px[6] == 255 && px[8] == 0 && px[9] == 0 && px[10] == 0, "");
            Check("managed: mid grey stays near mid grey", Math.Abs(px[14] - 128) <= 2 && Math.Abs(px[13] - 128) <= 2, $"{px[14]}");
            Check("managed: alpha is untouched", px[3] == 200 && px[15] == 7, $"{px[3]},{px[15]}");
            var back = managed.CreateTransform(adobe, IccProfile.Srgb, ColorIntent.RelativeColorimetric, false, out _);
            var rt = Cat(Px(40, 90, 200), Px(200, 150, 20), Px(10, 10, 10));
            var orig = (byte[])rt.Clone();
            toAdobe.ApplyBgra(rt, 3, 1);
            back!.ApplyBgra(rt, 3, 1);
            bool ok = true;
            for (int i = 0; i < rt.Length; i++) if (Math.Abs(rt[i] - orig[i]) > 3) ok = false;
            Check("managed: sRGB to Adobe and back returns the same pixels (within 3)", ok, string.Join(",", rt) + " vs " + string.Join(",", orig));
        }
        var needsLcms = managed.CreateTransform(IccProfile.Srgb, cmyk!, ColorIntent.Perceptual, true, out var why1);
        Check("managed: a CMYK printer profile is declined with a reason a user can read",
              needsLcms is null && why1 is { Length: > 10 } && why1.Contains("lcms2"), why1 ?? "");
        var proofManaged = managed.CreateProofingTransform(IccProfile.Srgb, adobe, IccProfile.Srgb, ColorIntent.RelativeColorimetric, false, true, out var why2);
        Check("managed: a proof through a matrix profile works and says the gamut warning needs lcms2",
              proofManaged is { IsIdentity: false } && why2 is not null && why2.Contains("gamut"), why2 ?? "");
        Check("managed: odd buffers are ignored, not crashed on", Survives(() => toAdobe!.ApplyBgra(new byte[8], 100, 100)), "");

        // ---- Transform cache ------------------------------------------------------------------------------------------
        ColorTransformCache.Clear();
        var c1 = ColorTransformCache.Get(managed, IccProfile.Srgb, adobe, ColorIntent.RelativeColorimetric, false, out _);
        var c2 = ColorTransformCache.Get(managed, IccProfile.Srgb, adobe, ColorIntent.RelativeColorimetric, false, out _);
        Check("cache: the same request returns the same transform", ReferenceEquals(c1, c2) && c1 is not null, "");
        var c3 = ColorTransformCache.Get(managed, IccProfile.Srgb, adobe, ColorIntent.Perceptual, false, out _);
        Check("cache: a different intent is a different entry", !ReferenceEquals(c1, c3), "");

        // ---- Display transform holder ---------------------------------------------------------------------------------
        DisplayColor.Set(null);
        var before = DisplayColor.Version;
        var untouched = Cat(Px(1, 2, 3), Px(250, 251, 252));
        var copy = (byte[])untouched.Clone();
        Check("display: with no transform no pixel changes", !DisplayColor.Apply(untouched, 2, 1) && untouched.SequenceEqual(copy), "");
        DisplayColor.Set(IdentityTransform.Instance);
        Check("display: an identity transform counts as none", !DisplayColor.IsActive, "");
        DisplayColor.Set(toAdobe);
        Check("display: a real transform is active and bumps the version", DisplayColor.IsActive && DisplayColor.Version > before, "");
        var shown = Cat(Px(255, 0, 0));
        Check("display: Apply converts when active", DisplayColor.Apply(shown, 1, 1) && shown[2] < 250, $"{shown[2]}");
        DisplayColor.Set(null);

        // ---- Monitor matching decision --------------------------------------------------------------------------------
        Check("monitor: setting off means no transform even with a wide-gamut profile",
              ColorMonitorPolicy.Decide(false, false, "p3.icc", adobe) == MonitorColorDecision.SettingOff, "");
        Check("monitor: no profile reported means no transform",
              ColorMonitorPolicy.Decide(true, false, null, null) == MonitorColorDecision.NoProfile
              && ColorMonitorPolicy.Decide(true, false, "  ", null) == MonitorColorDecision.NoProfile, "");
        Check("monitor: an unreadable profile means no transform",
              ColorMonitorPolicy.Decide(true, false, "x.icc", null) == MonitorColorDecision.Unreadable, "");
        Check("monitor: an sRGB profile looks exactly as today (no transform)",
              ColorMonitorPolicy.Decide(true, false, "srgb.icc", srgb) == MonitorColorDecision.Srgb, "");
        Check("monitor: HDR / Advanced Color is left to Windows",
              ColorMonitorPolicy.Decide(true, true, "p3.icc", adobe) == MonitorColorDecision.AdvancedColor, "");
        Check("monitor: a non-sRGB profile matches automatically",
              ColorMonitorPolicy.Decide(true, false, "adobe.icc", adobe) == MonitorColorDecision.Match, "");
        var s1 = new MonitorColorState(1, "a.icc", "a|1|1", false);
        Check("monitor: the same monitor and profile needs no refresh", !ColorMonitorPolicy.NeedsRefresh(s1, s1), "");
        Check("monitor: moving to another monitor refreshes", ColorMonitorPolicy.NeedsRefresh(s1, s1 with { MonitorId = 2 }), "");
        Check("monitor: a changed profile file refreshes", ColorMonitorPolicy.NeedsRefresh(s1, s1 with { ProfileId = "a|1|2" }), "");
        Check("monitor: HDR switched on refreshes", ColorMonitorPolicy.NeedsRefresh(s1, s1 with { AdvancedColor = true }), "");
        Check("monitor: the first look always decides", ColorMonitorPolicy.NeedsRefresh(null, s1), "");
        Check("monitor: every state has a sentence for Settings",
              Enum.GetValues<MonitorColorDecision>().All(d => ColorMonitorPolicy.Describe(d, "X", null).Length > 10), "");

        // ---- Print ticket: Driver default is a no-op ---------------------------------------------------------------------
        var def = PrintColorSettings.Default;
        Check("ticket: the default is Driver default and not managed", def.Mode == PrintColorMode.DriverDefault && !def.IsManaged, "");
        Check("ticket: Driver default never rewrites the ticket (the driver's ticket is spooled as is)",
              PrintTicketColor.ForJob(Xml(SampleTicket), def, conversionWorks: true) is null
              && PrintTicketColor.ForJob(Xml(SampleTicket), def with { SkipDriver = true, ProfilePath = "x.icc" }, conversionWorks: true) is null, "");
        var managedNoSkip = new PrintColorSettings { Mode = PrintColorMode.TdpManaged, ProfilePath = "x.icc" };
        Check("ticket: managed without 'skip' leaves the ticket alone", PrintTicketColor.ForJob(Xml(SampleTicket), managedNoSkip, true) is null, "");
        Check("ticket: managed without a profile leaves the ticket alone",
              PrintTicketColor.ForJob(Xml(SampleTicket), managedNoSkip with { ProfilePath = "", SkipDriver = true }, true) is null, "");
        Check("ticket: if the page conversion failed the driver is NOT asked to skip its colour management",
              PrintTicketColor.ForJob(Xml(SampleTicket), managedNoSkip with { SkipDriver = true }, conversionWorks: false) is null, "");

        // ---- Print ticket: managed rewrite -----------------------------------------------------------------------------
        var asked = managedNoSkip with { SkipDriver = true, Intent = ColorIntent.Perceptual };
        using var rewritten = PrintTicketColor.ForJob(Xml(SampleTicket), asked, true);
        Check("ticket: managed + skip + working conversion rewrites a copy", rewritten is not null, "");
        if (rewritten is not null)
        {
            var doc = System.Xml.Linq.XDocument.Load(rewritten);
            string xml = doc.ToString();
            Check("ticket: stapling, output bin, the vendor PIN and copies all pass through untouched",
                  xml.Contains("psk:StapleTopLeft") && xml.Contains("psk:Bin2") && xml.Contains("vnd:Pin4821")
                  && xml.Contains("psk:JobCopiesAllDocuments") && xml.Contains("psk:PageOutputColor"), xml);
            Check("ticket: it asks for None", xml.Contains("PageColorManagement") && xml.Contains(":None"), xml);
            Check("ticket: it names the intent (Perceptual is Photographs)", xml.Contains("PageICMRenderingIntent") && xml.Contains(":Photographs"), xml);
            rewritten.Position = 0;
            Check("ticket: the rewritten ticket selects None", PrintTicketColor.SelectsNone(rewritten), "");
            rewritten.Position = 0;
            Check("ticket: writing again changes nothing", PrintTicketColor.Write(rewritten, ColorIntent.Perceptual) is null, "");
            int features = doc.Root!.Elements().Count(e => e.Name.LocalName == "Feature");
            Check("ticket: exactly two features were added to the original four", features == 6, $"{features}");
        }
        var original = Xml(SampleTicket);
        _ = PrintTicketColor.Write(original, null);
        original.Position = 0;
        Check("ticket: the original stream is only read, never written", original.Length == Encoding.UTF8.GetByteCount(SampleTicket), "");
        Check("ticket: intents map to Print Schema keywords",
              PrintTicketColor.IntentKeyword(ColorIntent.Perceptual) == "Photographs"
              && PrintTicketColor.IntentKeyword(ColorIntent.Saturation) == "BusinessGraphics"
              && PrintTicketColor.IntentKeyword(ColorIntent.RelativeColorimetric) == "RelativeColorimetric"
              && PrintTicketColor.IntentKeyword(ColorIntent.AbsoluteColorimetric) == "AbsoluteColorimetric", "");

        // Replace an existing, different colour-management choice rather than adding a second one.
        string withDriverMgmt = SampleTicket.Replace("</psf:PrintTicket>",
            "<psf:Feature name=\"psk:PageColorManagement\"><psf:Option name=\"psk:Driver\"/></psf:Feature></psf:PrintTicket>");
        using (var replaced = PrintTicketColor.Write(Xml(withDriverMgmt), null))
        {
            string r = System.Xml.Linq.XDocument.Load(replaced!).ToString();
            Check("ticket: an existing Driver choice is replaced, not duplicated",
                  r.Split("PageColorManagement").Length == 2 && !r.Contains("psk:Driver") && r.Contains(":None"), r);
        }

        // ---- Driver confirmation ------------------------------------------------------------------------------------
        using var okTicket = PrintTicketColor.Write(Xml(SampleTicket), null)!;
        Check("confirm: offered and kept is confirmed", PrintTicketColor.DriverConfirmed(Xml(SampleCaps), okTicket), "");
        okTicket.Position = 0;
        Check("confirm: not offered is not confirmed",
              !PrintTicketColor.DriverConfirmed(Xml(SampleCaps.Replace("<psf:Option name=\"psk:None\"/>", "")), okTicket), "");
        Check("confirm: dropped by the driver's validation is not confirmed",
              !PrintTicketColor.DriverConfirmed(Xml(SampleCaps), Xml(SampleTicket)), "");
        Check("confirm: broken XML is not confirmed, not thrown", !PrintTicketColor.DriverConfirmed(Xml("<nope"), Xml(SampleTicket)), "");

        // ---- Per-printer settings -------------------------------------------------------------------------------------
        string stored = PrintColorStore.Set("", "HP LaserJet", asked);
        Check("store: a managed choice round-trips for its printer", PrintColorStore.Get(stored, "HP LaserJet") == asked, stored);
        Check("store: another printer is still on Driver default", PrintColorStore.Get(stored, "Epson") == PrintColorSettings.Default, "");
        string both = PrintColorStore.Set(stored, "Epson", managedNoSkip);
        Check("store: two printers keep separate choices",
              PrintColorStore.Get(both, "Epson") == managedNoSkip && PrintColorStore.Get(both, "HP LaserJet") == asked, "");
        string cleared = PrintColorStore.Set(both, "HP LaserJet", PrintColorSettings.Default);
        Check("store: going back to Driver default removes the entry", PrintColorStore.Get(cleared, "HP LaserJet") == PrintColorSettings.Default
              && PrintColorStore.Get(cleared, "Epson") == managedNoSkip, cleared);
        Check("store: nothing stored means Driver default", PrintColorStore.Get(null, "x") == PrintColorSettings.Default && PrintColorStore.Get("junk\n\t\t", "x") == PrintColorSettings.Default, "");
        Check("store: a tab in a printer name cannot break the line format",
              PrintColorStore.Get(PrintColorStore.Set("", "A\tB", managedNoSkip), "A B") == managedNoSkip, "");

        // ---- Printing is untouched: source pins -----------------------------------------------------------------------
        string? printSrc = FindRepoFile("Services/PrintPreviewWindow.cs");
        string src = printSrc is null ? "" : File.ReadAllText(printSrc);
        Check("pin: the print window source is readable", src.Length > 1000, "");
        Check("pin: the only ticket rewrite for colour is guarded by a working conversion AND the skip request",
              src.Contains("if (_printTransform is not null && _color.SkipDriver) ticket = WithColorManagement(ticket);")
              && src.Split("WithColorManagement(ticket)").Length == 2, "");
        Check("pin: colour never re-converts the ticket through a DEVMODE",
              !src.Contains("ConvertPrintTicketToDevMode(WithColorManagement") && !src.Contains("PrintTicketColor.Write(")
              && src.Contains("PrintTicketColor.ForJob(") && src.Split("PrintTicketColor.").Length - 1 == 2, $"{src.Split("PrintTicketColor.").Length - 1}");
        Check("pin: the DEVMODE conversions are still JobScope only",
              !src.Contains("PrintTicketScope.DocumentScope") && src.Split("PrintTicketScope.JobScope").Length - 1 >= 2, "");
        Check("pin: pages render with the job transform, which is null on Driver default",
              src.Contains("_owner._jobTransform") && src.Contains("private volatile IColorTransform? _jobTransform;"), "");
        Check("pin: Driver default is the first choice and the saved default",
              src.Contains("mode.Items.Add(\"Driver default\");") && src.Contains("private PrintColorSettings _color = PrintColorSettings.Default;"), "");
        string? rasterSrc = FindRepoFile("Services/PageRaster.cs");
        Check("pin: PageRaster applies a colour transform only when one is passed",
              rasterSrc is not null && File.ReadAllText(rasterSrc).Contains("if (colorTransform is { IsIdentity: false } && IsUsable(bgra, w, h))"), "");

        // ---- The native engine seam -----------------------------------------------------------------------------------
        Check("lcms: no pinned hash means no native library is used", LcmsBootstrap.PinnedSha256.Length == 0
              ? !LcmsBootstrap.MatchesPin([1, 2, 3]) : LcmsBootstrap.PinnedSha256.Length == 64, "");
        var sample = new byte[] { 1, 2, 3, 4, 5 };
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sample));
        Check("lcms: bytes matching the pin are accepted, others are not",
              LcmsBootstrap.MatchesPin(sample, hash) && LcmsBootstrap.MatchesPin(sample, hash.ToLowerInvariant())
              && !LcmsBootstrap.MatchesPin([1, 2, 3, 4, 6], hash) && !LcmsBootstrap.MatchesPin(sample, "") && !LcmsBootstrap.MatchesPin(sample, "abcd"), "");
        string? bundled = FindRepoFile("third_party/lcms2/win-x64/lcms2.dll");
        Check("lcms: a bundled lcms2.dll, if present, is exactly the pinned one",
              bundled is null || LcmsBootstrap.MatchesPin(File.ReadAllBytes(bundled)), bundled ?? "none bundled");
        Check("lcms: without the library the app engine is the managed one",
              OperatingSystem.IsWindows() || !ColorEngines.Current.IsNative, "");
        Check("lcms: the reason it is missing is stated", ColorEngines.Current.IsNative || ColorEngines.NativeUnavailableReason is { Length: > 10 }, "");

        string? lib = FindLcms();
        var native = lib is null ? null : LcmsLibrary.TryLoad(lib, out _);
        if (native is null)
        {
            Console.WriteLine("  (no system lcms2 here: native engine checks not run)");
        }
        else
        {
            var eng = new LcmsColorEngine(native);
            Check("lcms: the engine loads and reports a version", eng.IsNative && native.VersionNumber >= 2080, eng.Name);
            var n = eng.CreateTransform(IccProfile.Srgb, adobe, ColorIntent.RelativeColorimetric, false, out var nWhy);
            Check("lcms: sRGB to Adobe RGB builds", n is not null, nWhy ?? "");
            if (n is not null && toAdobe is not null)
            {
                var a = Cat(Px(255, 0, 0, 200), Px(255, 255, 255), Px(0, 0, 0), Px(128, 128, 128, 7), Px(40, 90, 200));
                var b = (byte[])a.Clone();
                n.ApplyBgra(a, 5, 1);
                toAdobe.ApplyBgra(b, 5, 1);
                int worst = 0;
                for (int i = 0; i < a.Length; i++) if (i % 4 != 3) worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
                Check("lcms: agrees with the managed engine within 2 levels", worst <= 2, $"worst {worst}");
                Check("lcms: alpha is carried through", a[3] == 200 && a[15] == 7, $"{a[3]},{a[15]}");
                Check("lcms: white and black stay put", a[4] == 255 && a[5] == 255 && a[6] == 255 && a[8] == 0 && a[9] == 0 && a[10] == 0, "");
            }
            var big = new byte[300 * 200 * 4];
            for (int i = 0; i < big.Length; i += 4) { big[i] = (byte)(i / 4 % 251); big[i + 1] = (byte)(i / 4 % 241); big[i + 2] = (byte)(i / 4 % 239); big[i + 3] = 255; }
            var bigCopy = (byte[])big.Clone();
            var one = n; one?.ApplyBgra(big, 300, 200);
            var rowwise = new byte[big.Length];
            for (int y = 0; y < 200; y++)
            {
                var row = new byte[300 * 4];
                Array.Copy(bigCopy, y * 300 * 4, row, 0, row.Length);
                one?.ApplyBgra(row, 300, 1);
                Array.Copy(row, 0, rowwise, y * 300 * 4, row.Length);
            }
            Check("lcms: chunked parallel conversion equals row-by-row conversion", big.SequenceEqual(rowwise), "");

            string sys = "/System/Library/ColorSync/Profiles/Generic CMYK Profile.icc";
            if (File.Exists(sys))
            {
                var cm = IccProfile.Parse(File.ReadAllBytes(sys));
                Check("lcms: a real CMYK printer profile parses as CMYK", cm is { IsCmyk: true }, "");
                if (cm is not null)
                {
                    var direct = eng.CreateTransform(IccProfile.Srgb, cm, ColorIntent.Perceptual, true, out var dWhy);
                    Check("lcms: sRGB straight into a CMYK profile is declined (RGB pixels cannot hold CMYK)", direct is null && dWhy is not null, dWhy ?? "");
                    var proof = eng.CreateProofingTransform(IccProfile.Srgb, cm, IccProfile.Srgb, ColorIntent.RelativeColorimetric, true, false, out var pWhy);
                    Check("lcms: the proof round trip through a CMYK profile builds", proof is not null, pWhy ?? "");
                    if (proof is not null)
                    {
                        var v = Cat(Px(0, 255, 0), Px(255, 255, 255), Px(0, 0, 0));
                        proof.ApplyBgra(v, 3, 1);
                        Check("lcms: pure sRGB green is pulled into the printer's range", v[1] < 250, $"{v[2]},{v[1]},{v[0]}");
                        Check("lcms: paper white stays white with black point compensation", v[4] >= 250 && v[5] >= 250 && v[6] >= 250, $"{v[6]},{v[5]},{v[4]}");
                    }
                    var warn = eng.CreateProofingTransform(IccProfile.Srgb, cm, IccProfile.Srgb, ColorIntent.RelativeColorimetric, false, true, out _);
                    if (warn is not null)
                    {
                        var g = Cat(Px(0, 255, 0));
                        warn.ApplyBgra(g, 1, 1);
                        Check("lcms: the gamut warning paints an out-of-gamut colour grey", Math.Abs(g[2] - g[1]) <= 3 && Math.Abs(g[1] - g[0]) <= 3, $"{g[2]},{g[1]},{g[0]}");
                    }
                }
            }
            ColorEngines.Use(eng);
            var viaApp = ColorTransformCache.Get(ColorEngines.Current, IccProfile.Srgb, adobe, ColorIntent.Perceptual, true, out _);
            Check("lcms: the app seam hands out lcms2 transforms once the engine is set", viaApp is LcmsTransform, "");
            ColorEngines.Use(null);
        }
    }

    private static bool Survives(Action a)
    {
        try { a(); return true; } catch { return false; }
    }
}
