using TDPdf.Diagnostics;
using TDPdf.Services;

/// <summary>
/// Pure-logic suites: geometry helpers, form-text helpers, OCR field policy, wheel gate,
/// font-name styling, installer exit codes and the status ring buffer.
/// </summary>
internal static class PureLogic
{
    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

    internal static void Run(Action<string, bool, string> Check)
    {
        Geometry2(Check);
        FormText(Check);
        OcrPolicy(Check);
        WheelGate(Check);
        FontStyle(Check);
        ExitCodes(Check);
        Ring(Check);
    }

    private static void Geometry2(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nSegment distance and polygon hit-testing");
        var a = (0.0, 0.0); var b = (10.0, 0.0);
        Check("point above the middle of a segment: perpendicular distance",
              Near(MeasureGeometry.DistancePointToSegment((5, 3), a, b), 3), "");
        Check("point beyond the end clamps to the endpoint, not the infinite line",
              Near(MeasureGeometry.DistancePointToSegment((13, 4), a, b), 5), "");
        Check("point before the start clamps to the start",
              Near(MeasureGeometry.DistancePointToSegment((-3, -4), a, b), 5), "");
        Check("point on the segment is zero", Near(MeasureGeometry.DistancePointToSegment((7, 0), a, b), 0), "");
        Check("a zero-length segment degrades to point distance",
              Near(MeasureGeometry.DistancePointToSegment((3, 4), (0, 0), (0, 0)), 5), "");
        Check("a diagonal segment measures perpendicular distance",
              Near(MeasureGeometry.DistancePointToSegment((0, 10), (0, 0), (10, 10)), Math.Sqrt(50)), "");

        var square = new List<(double X, double Y)> { (0, 0), (10, 0), (10, 10), (0, 10) };
        Check("filled: interior point hits", MeasureGeometry.HitTestPolygon(square, (5, 5), 2, true), "");
        Check("outline only: interior point far from every edge misses", !MeasureGeometry.HitTestPolygon(square, (5, 5), 2, false), "");
        Check("outline only: a point within tolerance of an edge hits", MeasureGeometry.HitTestPolygon(square, (5, 1.5), 2, false), "");
        Check("the closing edge (last point back to first) counts", MeasureGeometry.HitTestPolygon(square, (-1, 5), 2, false), "");
        Check("tolerance boundary is inclusive", MeasureGeometry.HitTestPolygon(square, (5, 2), 2, false), "");
        Check("outside and beyond tolerance misses even when filled", !MeasureGeometry.HitTestPolygon(square, (15, 5), 2, true), "");
        Check("fewer than two points never hits", !MeasureGeometry.HitTestPolygon(new List<(double X, double Y)> { (1, 1) }, (1, 1), 5, true), "");
        var concave = new List<(double X, double Y)> { (0, 0), (10, 0), (10, 10), (5, 4), (0, 10) };
        Check("concave notch: a point inside the notch is outside the polygon", !MeasureGeometry.HitTestPolygon(concave, (5, 8), 1, true), "");
        Check("concave: a point in an arm is inside", MeasureGeometry.HitTestPolygon(concave, (1, 8), 0.5, true), "");
    }

    private static void FormText(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nForm text: DA parsing and PDF string escaping");
        var (f, s) = PdfFormText.ParseDaString("/Helv 12 Tf 0 g");
        Check("DA '/Helv 12 Tf 0 g' gives /Helv 12", f == "/Helv" && Near(s, 12), $"{f} {s}");
        (f, s) = PdfFormText.ParseDaString("0 g /Arial 9.5 Tf");
        Check("font operator after other operators is found", f == "/Arial" && Near(s, 9.5), $"{f} {s}");
        (f, s) = PdfFormText.ParseDaString("/F1 0 Tf");
        Check("size 0 (auto size) is preserved as 0", f == "/F1" && s == 0, $"{f} {s}");
        (f, s) = PdfFormText.ParseDaString(null);
        Check("null DA falls back to /Helv, size 0", f == "/Helv" && s == 0, "");
        (f, s) = PdfFormText.ParseDaString("   ");
        Check("blank DA falls back to /Helv, size 0", f == "/Helv" && s == 0, "");
        (f, s) = PdfFormText.ParseDaString("0 g");
        Check("DA with no Tf falls back", f == "/Helv" && s == 0, "");
        (f, s) = PdfFormText.ParseDaString("/Cour\tx Tf\r\n/TiRo 10 Tf");
        Check("a Tf with a non-numeric size is skipped; whitespace of any kind separates",
              f == "/TiRo" && Near(s, 10), $"{f} {s}");
        (f, s) = PdfFormText.ParseDaString("/Helv 1e1 Tf");
        Check("exponent sizes parse invariantly", f == "/Helv" && Near(s, 10), $"{f} {s}");

        Check("plain text is unchanged", PdfFormText.EscapePdfString("Hello 123") == "Hello 123", "");
        Check("parentheses and backslash are escaped",
              PdfFormText.EscapePdfString(@"a(b)c\d") == @"a\(b\)c\\d", PdfFormText.EscapePdfString(@"a(b)c\d"));
        Check("CR and LF become \\r and \\n", PdfFormText.EscapePdfString("a\r\nb") == "a\\r\\nb", "");
        Check("a Latin-1 character passes straight through", PdfFormText.EscapePdfString("café") == "café", "");
        Check("curly apostrophe folds to the WinAnsi byte 0x92",
              PdfFormText.EscapePdfString("Hunter’s") == "Hunter\u0092s", "");
        Check("euro, em dash and ellipsis fold to their WinAnsi slots",
              PdfFormText.EscapePdfString("€—…") == "\u0080\u0097\u0085", "");
        Check("a character with no WinAnsi slot becomes '?'", PdfFormText.EscapePdfString("中") == "?", "");
        Check("a high surrogate pair yields one '?' per UTF-16 unit", PdfFormText.EscapePdfString("😀") == "??", "");
        Check("prime folds to an apostrophe, which needs no escape", PdfFormText.EscapePdfString("5′") == "5'", "");
        Check("empty string stays empty", PdfFormText.EscapePdfString("") == "", "");
    }

    private static void OcrPolicy(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nForm OCR policy");
        Check("numeric field gets the numeric whitelist", FormOcrPolicy.WhitelistFor(true, false) == FormOcrPolicy.NumericChars, "");
        Check("comb and ordinary fields stay unrestricted",
              FormOcrPolicy.WhitelistFor(false, true) is null && FormOcrPolicy.WhitelistFor(false, false) is null, "");

        Check("Normalize: null and blank become empty", FormOcrPolicy.Normalize(null, false, 0, null) == "" && FormOcrPolicy.Normalize("  \n ", false, 0, null) == "", "");
        Check("Normalize: single-line field flattens line breaks",
              FormOcrPolicy.Normalize("John\r\nSmith\nJr", false, 0, null) == "John Smith Jr", "");
        Check("Normalize: multi-line field keeps its line breaks",
              FormOcrPolicy.Normalize("a\nb", true, 0, null) == "a\nb", "");
        Check("Normalize: runs of spaces collapse", FormOcrPolicy.Normalize("a    b  c", false, 0, null) == "a b c", "");
        Check("Normalize: /MaxLen truncates", FormOcrPolicy.Normalize("ABCDEFG", false, 4, null) == "ABCD", "");
        Check("Normalize: MaxLen 0 means unlimited", FormOcrPolicy.Normalize("ABCDEFG", false, 0, null) == "ABCDEFG", "");
        Check("Normalize: value exactly at MaxLen is untouched", FormOcrPolicy.Normalize("ABCD", false, 4, null) == "ABCD", "");

        var states = new[] { "California", "Colorado", "Connecticut" };
        Check("NearestOption: exact match ignoring case returns the option's own casing",
              FormOcrPolicy.NearestOption("colorado", states) == "Colorado", "");
        Check("NearestOption: one OCR slip snaps to the option",
              FormOcrPolicy.NearestOption("Cal1fornia", states) == "California", "");
        Check("NearestOption: 2 edits on a 10-letter option is inside the 40% budget (4)",
              FormOcrPolicy.NearestOption("Co1orad0", states) == "Colorado", "");
        Check("NearestOption: 2 edits on a 4-letter option exceeds the budget (1)",
              FormOcrPolicy.NearestOption("Farrn", new[] { "Farm" }) is null, "");
        Check("NearestOption: nothing close returns null", FormOcrPolicy.NearestOption("Texas", states) is null, "");
        Check("NearestOption: ambiguous between two equally near options returns null",
              FormOcrPolicy.NearestOption("Cab", new[] { "Car", "Cap" }) is null, "");
        Check("NearestOption: distance exactly 2 apart from runner-up is accepted",
              FormOcrPolicy.NearestOption("Yes.", new[] { "Yes", "No" }) == "Yes", "");
        Check("NearestOption: empty option list entries are skipped",
              FormOcrPolicy.NearestOption("Yez", new[] { "", "Yes" }) == "Yes", "");
        Check("NearestOption: all-empty options return null", FormOcrPolicy.NearestOption("x", new[] { "", "" }) is null, "");
        Check("NearestOption: budget is at least 1 edit for short options",
              FormOcrPolicy.NearestOption("Nx", new[] { "No" }) == "No", "");
        Check("NearestOption: too many edits for a short option is rejected",
              FormOcrPolicy.NearestOption("Xyz", new[] { "No" }) is null, "");
        Check("Normalize: a choice value is snapped before MaxLen truncation",
              FormOcrPolicy.Normalize("Cal1fornia", false, 3, states) == "California", "");

        Check("LooksNumeric: AFNumber_Format", FormOcrPolicy.LooksNumeric("AFNumber_Format(2,0,0,0,\"$\",true)"), "");
        Check("LooksNumeric: AFPercent_Keystroke", FormOcrPolicy.LooksNumeric("AFPercent_Keystroke(2,0)"), "");
        Check("LooksNumeric: a date format is not numeric", !FormOcrPolicy.LooksNumeric("AFDate_FormatEx('mm/dd')"), "");
        Check("LooksNumeric: null and case mismatch are not numeric",
              !FormOcrPolicy.LooksNumeric(null) && !FormOcrPolicy.LooksNumeric("afnumber_format()"), "");
    }

    private static void WheelGate(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nWheel page-flip gate");
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Check("a single geared notch (120) at a boundary turns the page", new WheelPageFlipGate().TryConfirm(120, t0), "");
        Check("a notch in the other direction works too", new WheelPageFlipGate().TryConfirm(-120, t0), "");
        Check("a zero delta never turns the page", !new WheelPageFlipGate().TryConfirm(0, t0), "");
        Check("a small delta alone does not", !new WheelPageFlipGate().TryConfirm(30, t0), "");

        var g = new WheelPageFlipGate();
        g.NoteContentScroll(t0);
        Check("momentum within the 250ms quiet period is dropped", !g.TryConfirm(120, t0.AddMilliseconds(249)), "");
        Check("...and the first event after it is a fresh gesture", g.TryConfirm(120, t0.AddMilliseconds(600)), "");

        g = new WheelPageFlipGate();
        g.NoteContentScroll(t0);
        Check("quiet period ends exactly at 250ms", g.TryConfirm(120, t0.AddMilliseconds(250)), "");

        g = new WheelPageFlipGate();
        bool any = false;
        for (int i = 0; i < 3; i++) any |= g.TryConfirm(30, t0.AddMilliseconds(i * 100));
        Check("touchpad deltas under 120 total do not flip", !any, "");
        Check("the delta that reaches 120 does", g.TryConfirm(30, t0.AddMilliseconds(300)), "");
        Check("the count restarts after a flip", !g.TryConfirm(30, t0.AddMilliseconds(400)), "");

        g = new WheelPageFlipGate();
        g.TryConfirm(60, t0);
        Check("reversing direction restarts the count", !g.TryConfirm(-60, t0.AddMilliseconds(100)), "");
        Check("...so 60 more in the new direction completes it", g.TryConfirm(-60, t0.AddMilliseconds(200)), "");

        g = new WheelPageFlipGate();
        g.TryConfirm(60, t0);
        Check("a pause beyond 650ms restarts the count", !g.TryConfirm(60, t0.AddMilliseconds(651)), "");
        g = new WheelPageFlipGate();
        g.TryConfirm(60, t0);
        Check("a gap of exactly 650ms still accumulates", g.TryConfirm(60, t0.AddMilliseconds(650)), "");

        g = new WheelPageFlipGate();
        g.NoteContentScroll(t0);
        g.Reset();
        Check("Reset clears the quiet period", g.TryConfirm(120, t0.AddMilliseconds(10)), "");
        g = new WheelPageFlipGate();
        g.TryConfirm(60, t0);
        g.Reset();
        Check("Reset clears a half-built gesture", !g.TryConfirm(60, t0.AddMilliseconds(50)), "");
    }

    private static void FontStyle(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nPDF font-name styling");
        var s = PdfFontStyle.FromPdfName("Helvetica-BoldOblique");
        Check("Helvetica-BoldOblique: Arial, bold, italic", s is { Family: "Arial", Bold: true, Italic: true }, s.ToString());
        s = PdfFontStyle.FromPdfName("ABCDEF+TimesNewRomanPS-BoldMT");
        Check("subset prefix stripped; Times New Roman bold", s is { Family: "Times New Roman", Bold: true, Italic: false }, s.ToString());
        s = PdfFontStyle.FromPdfName("Arial,BoldItalic");
        Check("comma style: Arial bold italic", s is { Family: "Arial", Bold: true, Italic: true }, s.ToString());
        s = PdfFontStyle.FromPdfName("ArialMT");
        Check("ArialMT maps to Arial, plain", s is { Family: "Arial", Bold: false, Italic: false }, s.ToString());
        s = PdfFontStyle.FromPdfName("Courier-Oblique");
        Check("Courier-Oblique: Courier New italic", s is { Family: "Courier New", Bold: false, Italic: true }, s.ToString());
        s = PdfFontStyle.FromPdfName("BookAntiqua");
        Check("unknown CamelCase family is split into words", s.Family == "Book Antiqua", s.Family);
        s = PdfFontStyle.FromPdfName("BellMT");
        Check("a real 'MT' family name keeps its tag", s.Family == "Bell MT", s.Family);
        s = PdfFontStyle.FromPdfName(null);
        Check("null name gives the default family", s is { Family: PdfFontStyle.DefaultFamily, Bold: false, Italic: false }, s.ToString());
        s = PdfFontStyle.FromPdfName("   ");
        Check("blank name gives the default family", s.Family == PdfFontStyle.DefaultFamily, s.ToString());
        s = PdfFontStyle.FromPdfName("Bold");
        Check("a name that is only a face token falls back to the default family", s.Family == PdfFontStyle.DefaultFamily && s.Bold, s.ToString());
        s = PdfFontStyle.FromPdfName("Verdana-Italic");
        Check("Verdana-Italic: family kept, italic only", s is { Family: "Verdana", Bold: false, Italic: true }, s.ToString());
        s = PdfFontStyle.FromPdfName("Calibri-SemiBold");
        Check("SemiBold counts as bold", s is { Family: "Calibri", Bold: true }, s.ToString());
        s = PdfFontStyle.FromPdfName("ZapfDingbats");
        Check("ZapfDingbats maps to Wingdings", s.Family == "Wingdings", s.Family);
        s = PdfFontStyle.FromPdfName("Segoe UI");
        Check("a name that already reads as a family is kept", s.Family == "Segoe UI", s.Family);
    }

    private static void ExitCodes(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nInstaller exit codes");
        Check("OperationCanceledException -> 1223", InstallerExitCodes.FromException(new OperationCanceledException()) == 1223, "");
        Check("UnauthorizedAccessException -> 5", InstallerExitCodes.FromException(new UnauthorizedAccessException()) == 5, "");
        Check("SecurityException -> 5", InstallerExitCodes.FromException(new System.Security.SecurityException()) == 5, "");
        Check("Win32Exception carrying 1618 passes through",
              InstallerExitCodes.FromException(new System.ComponentModel.Win32Exception(1618)) == 1618, "");
        Check("Win32Exception 112 -> disk full", InstallerExitCodes.FromException(new System.ComponentModel.Win32Exception(112)) == 112, "");
        Check("Win32Exception 39 (handle disk full) -> 112", InstallerExitCodes.FromException(new System.ComponentModel.Win32Exception(39)) == 112, "");
        Check("an unrecognised Win32 code is a plain failure", InstallerExitCodes.FromException(new System.ComponentModel.Win32Exception(1234)) == 1, "");
        Check("IOException with HRESULT low word 112 -> disk full",
              InstallerExitCodes.FromException(new IOException("full", unchecked((int)0x80070070))) == 112, "");
        Check("IOException with low word 39 -> disk full",
              InstallerExitCodes.FromException(new IOException("full", unchecked((int)0x80070027))) == 112, "");
        Check("an IOException with an unrelated HRESULT is a plain failure",
              InstallerExitCodes.FromException(new IOException("x", unchecked((int)0x80070002))) == 1, "");
        Check("a generic exception is a plain failure", InstallerExitCodes.FromException(new InvalidOperationException()) == 1, "");
        Check("the inner exception chain is walked",
              InstallerExitCodes.FromException(new InvalidOperationException("outer", new UnauthorizedAccessException())) == 5, "");
        Check("a deep chain finds a Busy code",
              InstallerExitCodes.FromException(new Exception("a", new Exception("b", new System.ComponentModel.Win32Exception(1618)))) == 1618, "");
        Check("the outermost matching exception wins",
              InstallerExitCodes.FromException(new UnauthorizedAccessException("o", new OperationCanceledException())) == 5, "");
    }

    private static void Ring(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nStatus ring buffer");
        bool threw = false;
        try { _ = new StatusRingBuffer(0); } catch (ArgumentOutOfRangeException) { threw = true; }
        Check("capacity 0 is rejected", threw, "");
        threw = false;
        try { _ = new StatusRingBuffer(-1); } catch (ArgumentOutOfRangeException) { threw = true; }
        Check("negative capacity is rejected", threw, "");
        Check("default capacity is 50", new StatusRingBuffer().Capacity == 50, "");

        var rb = new StatusRingBuffer(3);
        Check("a new buffer snapshots empty", rb.Snapshot().Length == 0, "");
        rb.Add(null); rb.Add(""); rb.Add("   ");
        Check("null, empty and whitespace messages are ignored", rb.Snapshot().Length == 0, "");
        rb.Add("one"); rb.Add("two");
        var snap = rb.Snapshot();
        Check("below capacity, snapshot is in insertion order",
              snap.Length == 2 && snap[0].EndsWith(" one") && snap[1].EndsWith(" two"), string.Join("|", snap));
        Check("entries are timestamped (yyyy-MM-dd prefix)", System.Text.RegularExpressions.Regex.IsMatch(snap[0], @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} "), snap[0]);
        rb.Add("three");
        Check("exactly at capacity keeps all", rb.Snapshot().Length == 3, "");
        rb.Add("four"); rb.Add("five");
        snap = rb.Snapshot();
        Check("past capacity the oldest are overwritten, order preserved (oldest first)",
              snap.Length == 3 && snap[0].EndsWith(" three") && snap[1].EndsWith(" four") && snap[2].EndsWith(" five"), string.Join("|", snap));
        for (int i = 0; i < 10; i++) rb.Add("n" + i);
        snap = rb.Snapshot();
        Check("many wraps still yield the last three", snap.Length == 3 && snap[0].EndsWith(" n7") && snap[2].EndsWith(" n9"), string.Join("|", snap));
        snap[0] = "mutated";
        Check("the snapshot is a copy", rb.Snapshot()[0].EndsWith(" n7"), "");
    }
}
