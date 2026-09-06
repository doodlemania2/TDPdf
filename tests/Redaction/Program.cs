using System.Reflection;
using System.Runtime.InteropServices;
using PdfSharpCore.Drawing;
using PdfSharpCore.Fonts;
using TDPdf.Services;

// ── Put the platform's PDFium where the shipping code expects to find it ───────────────
// PdfiumInterop probes its capabilities with NativeLibrary.TryLoad("pdfium.dll"), which does
// NOT consult a DllImportResolver. On Windows that file sits beside the exe, so it just
// works; here the package nests it under runtimes/<rid>/native with a platform name. Copying
// it to the output as "pdfium.dll" reproduces the shipping layout instead of weakening the
// probe to suit the test.
{
    string want = Path.Combine(AppContext.BaseDirectory, "pdfium.dll");
    if (!File.Exists(want))
    {
        foreach (var c in Directory.GetFiles(AppContext.BaseDirectory, "*pdfium*", SearchOption.AllDirectories))
        {
            if (!NativeLibrary.TryLoad(c, out var probe)) continue;   // wrong architecture
            NativeLibrary.Free(probe);
            File.Copy(c, want, overwrite: true);
            Console.WriteLine($"using native: {Path.GetRelativePath(AppContext.BaseDirectory, c)}");
            break;
        }
    }
}

// ── Make DllImport("pdfium.dll") resolve to that same file ─────────────────────────────
NativeLibrary.SetDllImportResolver(typeof(PdfiumInterop).Assembly, (name, asm, path) =>
{
    if (!name.StartsWith("pdfium", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
    // Non-RID builds nest natives under runtimes/<rid>/native/, so search recursively.
    foreach (var c in Directory.GetFiles(AppContext.BaseDirectory, "*pdfium*", SearchOption.AllDirectories))
        if (NativeLibrary.TryLoad(c, out var h)) return h;
    return IntPtr.Zero;
});

// ── A minimal font resolver so PdfSharpCore can lay out text headlessly ────────────────
GlobalFontSettings.FontResolver = new R();

int failures = 0;
void Check(string label, bool ok, string detail = "")
{
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label}{(detail.Length > 0 ? "  — " + detail : "")}");
}

string tmp = Path.Combine(Path.GetTempPath(), "tdpdf-redact-test");
Directory.CreateDirectory(tmp);

// ── Build a page with three text runs at known positions ───────────────────────────────
// PdfSharpCore's origin is top-left; PDFium page space is bottom-left. A 792pt-tall page
// means a run drawn at y=100 sits at PDF y≈692.
string src = Path.Combine(tmp, "src.pdf");
{
    var doc = new PdfSharpCore.Pdf.PdfDocument();
    var page = doc.AddPage();          // 595 x 842 (A4) by default
    using var gfx = XGraphics.FromPdfPage(page);
    var font = new XFont("Helvetica", 14);
    gfx.DrawString("SECRETPASSWORD12345", font, XBrushes.Black, 60, 100);
    gfx.DrawString("KEEPTHISVISIBLE", font, XBrushes.Black, 60, 300);
    gfx.DrawString("ALSOKEEPTHIS", font, XBrushes.Black, 60, 500);
    doc.Save(src);
}
Console.WriteLine($"pdfium edit API: {PdfiumInterop.AvailableEditApi}");
Console.WriteLine($"  CanEditPageContent      : {PdfiumInterop.CanEditPageContent}");
Console.WriteLine($"  CanEditFormXObjectContent: {PdfiumInterop.CanEditFormXObjectContent}");
Console.WriteLine();

static (string text, double height) Extract(string path)
{
    using var d = UglyToad.PdfPig.PdfDocument.Open(path);
    var p = d.GetPage(1);
    return (p.Text, p.Height);
}

var (beforeText, h) = Extract(src);
Check("fixture contains all three runs",
      beforeText.Contains("SECRETPASSWORD12345") && beforeText.Contains("KEEPTHISVISIBLE") && beforeText.Contains("ALSOKEEPTHIS"),
      beforeText.Replace("\n", " "));

// PdfSharpCore drew the secret at top-y=100 with a 14pt font, so in PDF space it spans
// roughly y = h-100-14 .. h-100. Take a generous band around it, but one that must not
// reach the run at top-y=300.
var band = new PdfiumInterop.PdfRect(Left: 0, Bottom: h - 130, Right: 595, Top: h - 80);
Console.WriteLine($"page height {h:F1}pt; redaction band y {band.Bottom:F1}..{band.Top:F1}\n");

string dest = Path.Combine(tmp, "redacted.pdf");
var outcome = PdfiumInterop.RemoveObjectsIntersecting(
    src, dest,
    new Dictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> { [0] = new[] { band } },
    removePartialOverlaps: true);

Check("apply succeeded", outcome.Ok, outcome.Error ?? "");
Console.WriteLine($"      objects removed={outcome.ObjectsRemoved} invisibleText={outcome.InvisibleTextRemoved} partial={outcome.Partial.Count}");

if (outcome.Ok && File.Exists(dest))
{
    var (afterText, _) = Extract(dest);
    Console.WriteLine($"      text after: \"{afterText.Replace("\n", " ")}\"");

    Check("THE SECRET IS GONE from extracted text", !afterText.Contains("SECRETPASSWORD12345"));
    Check("untouched run survives (KEEPTHISVISIBLE)", afterText.Contains("KEEPTHISVISIBLE"));
    Check("untouched run survives (ALSOKEEPTHIS)", afterText.Contains("ALSOKEEPTHIS"));

    // The bytes, not just the text layer: a covered-not-removed redaction would still
    // have the string somewhere in the file.
    var raw = File.ReadAllBytes(dest);
    bool literal = System.Text.Encoding.ASCII.GetString(raw).Contains("SECRETPASSWORD12345");
    Check("secret not present as a literal string in the file bytes", !literal);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;

file sealed class R : IFontResolver
{
    public string DefaultFontName => "Helvetica";
    public byte[] GetFont(string faceName)
    {
        foreach (var p in new[] { "/System/Library/Fonts/Supplemental/Arial.ttf",
                                  "/Library/Fonts/Arial.ttf",
                                  "/System/Library/Fonts/Supplemental/Times New Roman.ttf" })
            if (File.Exists(p)) return File.ReadAllBytes(p);
        throw new FileNotFoundException("no usable system font");
    }
    public FontResolverInfo ResolveTypeface(string f, bool b, bool i) => new("Helvetica");
}
