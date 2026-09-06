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
            Console.WriteLine($"native: {Path.GetRelativePath(AppContext.BaseDirectory, c)}");
            break;
        }
    }
}

NativeLibrary.SetDllImportResolver(typeof(PdfiumInterop).Assembly, (name, asm, path) =>
{
    if (!name.StartsWith("pdfium", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
    foreach (var c in Directory.GetFiles(AppContext.BaseDirectory, "*pdfium*", SearchOption.AllDirectories))
        if (NativeLibrary.TryLoad(c, out var h)) return h;
    return IntPtr.Zero;
});

GlobalFontSettings.FontResolver = new R();

int failures = 0;
void Check(string label, bool ok, string detail = "")
{
    if (!ok) failures++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(detail.Length > 0 ? "  — " + detail : "")}");
}

string tmp = Path.Combine(Path.GetTempPath(), "tdpdf-redact-test");
Directory.CreateDirectory(tmp);

Console.WriteLine($"pdfium edit API: {PdfiumInterop.AvailableEditApi}");
Console.WriteLine($"  page content={PdfiumInterop.CanEditPageContent} formXObjects={PdfiumInterop.CanEditFormXObjectContent}");
Console.WriteLine();

// ── Fixture ────────────────────────────────────────────────────────────────────────────
// Three separate runs, plus metadata carrying the same secret — redacting a name from the
// body while leaving it in the Title is the classic miss.
string src = Path.Combine(tmp, "src.pdf");
{
    var doc = new PdfSharpCore.Pdf.PdfDocument();
    doc.Info.Title = "SECRETPASSWORD12345 quarterly report";
    doc.Info.Author = "SECRETPASSWORD12345";
    doc.Info.Subject = "confidential";
    var page = doc.AddPage();
    using var gfx = XGraphics.FromPdfPage(page);
    var font = new XFont("Helvetica", 14);
    gfx.DrawString("SECRETPASSWORD12345", font, XBrushes.Black, 60, 100);
    gfx.DrawString("KEEPTHISVISIBLE", font, XBrushes.Black, 60, 300);
    gfx.DrawString("ALSOKEEPTHIS", font, XBrushes.Black, 60, 500);
    doc.Save(src);
}

static (string text, double height) Read(string path)
{
    using var d = UglyToad.PdfPig.PdfDocument.Open(path);
    var p = d.GetPage(1);
    return (p.Text, p.Height);
}

static string MetaOf(string path)
{
    using var d = PdfSharpCore.Pdf.IO.PdfReader.Open(path, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.InformationOnly);
    return $"{d.Info.Title}|{d.Info.Author}|{d.Info.Subject}";
}

var (beforeText, h) = Read(src);
int opsBefore = Streams.TextShowingOps(src);
Console.WriteLine("Fixture");
Check("all three runs present",
      beforeText.Contains("SECRETPASSWORD12345") && beforeText.Contains("KEEPTHISVISIBLE") && beforeText.Contains("ALSOKEEPTHIS"));
Check("metadata carries the secret too", MetaOf(src).Contains("SECRETPASSWORD12345"), MetaOf(src));
Check("three text-drawing operators in the file", opsBefore == 3, $"{opsBefore}");

// Derive the geometry from the actual glyph boxes rather than guessing at font metrics —
// a hand-picked rectangle silently became the wrong shape once already.
static (double L, double B, double R, double T) BoxOf(string path, string word)
{
    using var d = UglyToad.PdfPig.PdfDocument.Open(path);
    var w = d.GetPage(1).GetWords().First(x => x.Text.Contains(word, StringComparison.Ordinal));
    var b = w.BoundingBox;
    return (b.Left, b.Bottom, b.Right, b.Top);
}

var secret = BoxOf(src, "SECRETPASSWORD12345");
Console.WriteLine($"secret glyph box: L={secret.L:F1} B={secret.B:F1} R={secret.R:F1} T={secret.T:F1}");

// Comfortably around the whole run, and nowhere near the next one.
var band = new PdfiumInterop.PdfRect(
    Left: secret.L - 10, Bottom: secret.B - 6, Right: secret.R + 10, Top: secret.T + 6);
Console.WriteLine($"\nRedacting band y {band.Bottom:F1}..{band.Top:F1} of a {h:F0}pt page\n");

// ── 1. The happy path, through the full pipeline ───────────────────────────────────────
Console.WriteLine("Apply");
string dest = Path.Combine(tmp, "redacted.pdf");
if (File.Exists(dest)) File.Delete(dest);

var res = PdfRedaction.Apply(src, dest, new PdfRedaction.Request
{
    RectsByPage = new Dictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> { [0] = new[] { band } },
    RemovePartialOverlaps = true,
    ScrubMetadata = true,
});

Check("succeeded", res.Ok, res.Error ?? "");
Console.WriteLine($"        removed={res.ObjectsRemoved} invisible={res.InvisibleTextRemoved} partial={res.Partial.Count} survivors={res.Survivors.Count}");

if (res.Ok && File.Exists(dest))
{
    var (after, _) = Read(dest);
    Console.WriteLine($"        text after: \"{after.Replace("\n", " ")}\"");
    Check("secret gone from extracted text", !after.Contains("SECRETPASSWORD12345"));
    Check("untouched run survives (KEEPTHISVISIBLE)", after.Contains("KEEPTHISVISIBLE"));
    Check("untouched run survives (ALSOKEEPTHIS)", after.Contains("ALSOKEEPTHIS"));
    Check("metadata scrubbed", !MetaOf(dest).Contains("SECRETPASSWORD12345"), MetaOf(dest));

    // The check that separates real redaction from theatre. Counting the OPERATOR, not the
    // string: PdfSharpCore embeds a subset font and writes glyph indices, so "SECRET…" is not in
    // the content stream even before the redaction — searching for it would pass on a file where
    // the text is completely intact. Tj/TJ is plain ASCII whatever the font does, and the fixture
    // draws three runs, so exactly one of them must have disappeared.
    Check("one text-drawing operator was removed from the file, leaving two",
          opsBefore == 3 && Streams.TextShowingOps(dest) == 2,
          $"{opsBefore} before, {Streams.TextShowingOps(dest)} after");
}

// ── 2. The refusal path ────────────────────────────────────────────────────────────────
// A rectangle that covers a run's centre but only partially overlaps its object, with
// partial removal switched OFF. The object survives, so text remains inside the redacted
// area — and the pipeline must REFUSE to produce a file rather than hand back one the user
// would reasonably believe was safe.
Console.WriteLine("\nRefusal when content would survive");
string refused = Path.Combine(tmp, "should-not-exist.pdf");
if (File.Exists(refused)) File.Delete(refused);

// Straddles the run: contains its CENTRE (so verification must see a survivor) but not its
// full extent (so the object is only a partial overlap and is therefore left in place).
double cx = (secret.L + secret.R) / 2.0;
var narrow = new PdfiumInterop.PdfRect(
    Left: cx - 25, Bottom: secret.B - 4, Right: cx + 25, Top: secret.T + 4);
Console.WriteLine($"  straddling rect L={narrow.Left:F1} R={narrow.Right:F1} (run is {secret.L:F1}..{secret.R:F1})");
var res2 = PdfRedaction.Apply(src, refused, new PdfRedaction.Request
{
    RectsByPage = new Dictionary<int, IReadOnlyList<PdfiumInterop.PdfRect>> { [0] = new[] { narrow } },
    RemovePartialOverlaps = false,          // leave partially-overlapping objects in place
    ScrubMetadata = true,
});

Console.WriteLine($"        removed={res2.ObjectsRemoved} partial={res2.Partial.Count} survivors={res2.Survivors.Count}");
Check("reported not-ok", !res2.Ok, res2.Error ?? "(no error given)");
Check("named the surviving text", res2.Survivors.Count > 0,
      res2.Survivors.Count > 0 ? string.Join(",", res2.Survivors) : "none");
Check("NO output file was written", !File.Exists(refused));

Geometry.Run(Check, tmp);
ImageGuard.Run(Check, tmp);
Raster.Run(Check, tmp, Geometry.RenderFirstPage);

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
                                  "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                                  "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf" })
            if (File.Exists(p)) return File.ReadAllBytes(p);
        throw new FileNotFoundException("no usable system font");
    }
    public FontResolverInfo ResolveTypeface(string f, bool b, bool i) => new("Helvetica");
}
