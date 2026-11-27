using System.Security.Cryptography;
using System.Text;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using TDPdf.Services;

/// <summary>
/// Pins pure code that had no test: the quarter-turn geometry annotations are remapped with, the
/// crop writer, the font-dictionary lookup the text editor uses, and the non-DPAPI parts of the
/// Documenso credential store.
/// </summary>
internal static class UntestedPureCodeTests
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("Quarter turn, crop writer, font lookup, credential store");
        QuarterTurns(Check);
        Crop(Check, tmp);
        FontLookup(Check);
        CredentialStore(Check);
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

    private static void QuarterTurns(Action<string, bool, string> Check)
    {
        // A frame 100 wide and 200 tall.
        const double W = 100, H = 200;
        var p90 = QuarterTurn.MapPoint(90, W, H, 10, 20);
        Check("quarter turn: 90 sends (10,20) to (180,10)", Near(p90.X, 180) && Near(p90.Y, 10), $"{p90}");
        var p270 = QuarterTurn.MapPoint(270, W, H, 10, 20);
        Check("quarter turn: 270 sends (10,20) to (20,90)", Near(p270.X, 20) && Near(p270.Y, 90), $"{p270}");
        var p180 = QuarterTurn.MapPoint(180, W, H, 10, 20);
        Check("quarter turn: 180 sends (10,20) to (90,180)", Near(p180.X, 90) && Near(p180.Y, 180), $"{p180}");

        // The top-left corner goes to the top-right after a clockwise quarter turn.
        var corner = QuarterTurn.MapPoint(90, W, H, 0, 0);
        Check("quarter turn: 90 sends the top-left corner to the top-right of the turned frame", Near(corner.X, H) && Near(corner.Y, 0), $"{corner}");

        // Four quarter turns are the identity (the frame alternates between 100x200 and 200x100).
        double x = 13, y = 71, cw = W, ch = H;
        for (int i = 0; i < 4; i++)
        {
            (x, y) = QuarterTurn.MapPoint(90, cw, ch, x, y);
            (cw, ch) = (ch, cw);
        }
        Check("quarter turn: four turns of 90 come back to the start", Near(x, 13) && Near(y, 71) && Near(cw, W) && Near(ch, H), $"{x},{y}");
        var two = QuarterTurn.MapPoint(90, H, W, QuarterTurn.MapPoint(90, W, H, 13, 71).X, QuarterTurn.MapPoint(90, W, H, 13, 71).Y);
        var one80 = QuarterTurn.MapPoint(180, W, H, 13, 71);
        Check("quarter turn: two 90s equal one 180", Near(two.X, one80.X) && Near(two.Y, one80.Y), $"{two} {one80}");

        var r = QuarterTurn.MapRect(90, W, H, 10, 20, 30, 40);
        Check("quarter turn: a rectangle turns with its size swapped and stays normalised",
              Near(r.X, 140) && Near(r.Y, 10) && Near(r.W, 40) && Near(r.H, 30), $"{r}");
        var rs = QuarterTurn.MapRect(180, W, H, 0, 0, W, H);
        Check("quarter turn: the whole frame at 180 is the whole frame", Near(rs.X, 0) && Near(rs.Y, 0) && Near(rs.W, W) && Near(rs.H, H), $"{rs}");

        // Anchors: an upright 30x40 item at (10,20); the centre (25,40) turns to (160,25), so top-left (145,5).
        var a = QuarterTurn.MapAnchor(90, W, H, 10, 20, 30, 40);
        Check("quarter turn: an upright item's centre follows the page", Near(a.X, 145) && Near(a.Y, 5), $"{a}");
        // A tall item near the old long edge is pulled back inside the turned frame (new frame 200x100).
        var tall = QuarterTurn.MapAnchor(90, W, H, 60, 150, 30, 120);
        Check("quarter turn: an item that would land off the sheet is clamped inside it",
              Near(tall.X, 0) && Near(tall.Y, 0), $"{tall} (centre lands at (-10,75); the item is taller than the 100pt frame)");
        var huge = QuarterTurn.MapAnchor(90, W, H, 0, 0, 500, 500);
        Check("quarter turn: an item bigger than the sheet pins to the top-left", Near(huge.X, 0) && Near(huge.Y, 0), $"{huge}");
    }

    private static string MakePdf(string path, params (double W, double H, int Rotate)[] pages)
    {
        using var doc = new PdfDocument();
        foreach (var (w, h, rot) in pages)
        {
            var p = doc.AddPage();
            p.Width = w; p.Height = h; p.Rotate = rot;
        }
        doc.Save(path);
        return path;
    }

    private static void Crop(Action<string, bool, string> Check, string tmp)
    {
        string src = MakePdf(Path.Combine(tmp, "crop-src.pdf"), (612, 792, 0), (595, 842, 0));
        var crop = new CropMargins.UserRect(36, 72, 576, 720);

        string one = CropDocument.Apply(src, 0, crop, applyToAllPages: false);
        using (var d = PdfReader.Open(one, PdfDocumentOpenMode.ReadOnly))
        {
            var cb0 = d.Pages[0].CropBox; var cb1 = d.Pages[1].CropBox;
            Check("crop: the chosen page gets the drawn rectangle",
                  Near(cb0.X1, 36) && Near(cb0.Y1, 72) && Near(cb0.X2, 576) && Near(cb0.Y2, 720), $"{cb0}");
            Check("crop: the other page is left alone", Near(cb1.X2, 0) && Near(cb1.Y2, 0), $"{cb1} (an unset CropBox reads as empty)");
        }
        Check("crop: the source is not modified and the copy is a new file",
              one != src && File.Exists(one) && File.Exists(src), one);

        string all = CropDocument.Apply(src, 0, crop, applyToAllPages: true);
        using (var d = PdfReader.Open(all, PdfDocumentOpenMode.ReadOnly))
        {
            var cb1 = d.Pages[1].CropBox;
            // The same margins from each edge of A4: 36 left, 72 top, 36 right, 72 bottom.
            Check("crop: apply to all carries the margins, not the rectangle, to a page of another size",
                  Near(cb1.X1, 36) && Near(cb1.X2, 595 - 36) && Near(cb1.Y1, 72) && Near(cb1.Y2, 842 - 72), $"{cb1}");
        }

        // A rectangle partly off the page is clamped to the MediaBox.
        string clamped = CropDocument.Apply(src, 0, new CropMargins.UserRect(-50, -50, 300, 400), false);
        using (var d = PdfReader.Open(clamped, PdfDocumentOpenMode.ReadOnly))
        {
            var cb = d.Pages[0].CropBox;
            Check("crop: a rectangle past the page edge is clamped to it", Near(cb.X1, 0) && Near(cb.Y1, 0) && Near(cb.X2, 300) && Near(cb.Y2, 400), $"{cb}");
        }

        static bool Throws<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }
        Check("crop: a rectangle wholly off the page is refused",
              Throws<InvalidOperationException>(() => CropDocument.Apply(src, 0, new CropMargins.UserRect(700, 100, 800, 200), false)), "");
        Check("crop: an empty rectangle is refused",
              Throws<ArgumentException>(() => CropDocument.Apply(src, 0, new CropMargins.UserRect(10, 10, 10, 50), false)), "");
        Check("crop: a page index outside the document is refused",
              Throws<ArgumentOutOfRangeException>(() => CropDocument.Apply(src, 5, crop, false)), "");
        Check("crop: a missing file is refused",
              Throws<FileNotFoundException>(() => CropDocument.Apply(Path.Combine(tmp, "nope.pdf"), 0, crop, false)), "");
    }

    private static void FontLookup(Action<string, bool, string> Check)
    {
        var timesBold = new PdfFontDictInfo("ABCDEF+Times-Bold", null, null, 700, 0, 0);
        var plain = new PdfFontDictInfo("Helvetica", null, null, 400, 0, 0);
        var map = new Dictionary<string, PdfFontDictInfo>
        {
            ["/F1"] = plain,
            ["ABCDEF+Times-Bold"] = timesBold,
            ["Times-Bold"] = timesBold,
        };
        Check("font lookup: the name as written", PdfFontStyle.FindFontInfo(map, "/F1") == plain, "");
        Check("font lookup: a resource name without its slash", PdfFontStyle.FindFontInfo(map, "F1") == plain, "");
        Check("font lookup: a subset name finds the entry by its bare name",
              PdfFontStyle.FindFontInfo(map, "XYZABC+Times-Bold") == timesBold, "");
        Check("font lookup: a slash-led bare name", PdfFontStyle.FindFontInfo(map, "/Times-Bold") == timesBold, "");
        Check("font lookup: an unknown font is null", PdfFontStyle.FindFontInfo(map, "/F9") is null && PdfFontStyle.FindFontInfo(map, "Courier") is null, "");
    }

    private static void CredentialStore(Action<string, bool, string> Check)
    {
        // Pins the entropy format: changing it strands every stored token (see the store's remarks).
        byte[] e = DocumensoCredentialStore.Entropy("https://sign.example.com");
        byte[] want = SHA256.HashData(Encoding.UTF8.GetBytes("TDPdf/Documenso/v1https://sign.example.com"));
        Check("credential store: entropy is SHA-256 of the versioned prefix and the instance", e.SequenceEqual(want), Convert.ToHexString(e));
        Check("credential store: entropy differs per instance (a token cannot be replayed elsewhere)",
              !e.SequenceEqual(DocumensoCredentialStore.Entropy("https://sign.other.com")), "");
        Check("credential store: entropy is deterministic", e.SequenceEqual(DocumensoCredentialStore.Entropy("https://sign.example.com")), "");
        // Refusals that happen before anything touches disk or DPAPI.
        Check("credential store: a blank token is not saved", !DocumensoCredentialStore.Save("https://sign.example.com", "   ", null), "");
        Check("credential store: an unusable instance URL is not saved", !DocumensoCredentialStore.Save("not a url", "tok", null), "");
        Check("credential store: no token for an unusable instance URL", DocumensoCredentialStore.TryLoadToken("not a url") is null, "");
    }
}
