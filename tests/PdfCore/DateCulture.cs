using System.Globalization;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

/// <summary>
/// PDF dates are always Gregorian with ASCII digits, whatever the user's regional settings say.
/// Under th-TH the current culture's calendar is Buddhist, so "yyyy" used to write 2569 for 2026.
/// </summary>
internal static class DateCulture
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo thai;
            try { thai = new CultureInfo("th-TH"); } catch { thai = CultureInfo.InvariantCulture; }
            // Without this the cases below could not fail, which would make them worthless.
            bool buddhist = thai.DateTimeFormat.Calendar is ThaiBuddhistCalendar;
            Check("date culture: th-TH really formats a Buddhist year here (test premise)",
                  buddhist && new DateTime(2026, 3, 4).ToString("yyyy", thai) == "2569", thai.Name);

            CultureInfo.CurrentCulture = thai;
            var when = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            string text = new PdfDate(when).ToString();
            Check("date culture: PdfDate writes the Gregorian year under th-TH",
                  text.StartsWith("D:20260304", StringComparison.Ordinal), text);

            // Through a real document: Info dates and an annotation /M go through the same writer.
            string path = Path.Combine(tmp, "date-culture.pdf");
            using (var doc = new PdfDocument())
            {
                doc.AddPage();
                doc.Info.CreationDate = when;
                doc.Info.ModificationDate = when;
                doc.Save(path);
            }
            string raw = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path));
            Check("date culture: saved file carries /CreationDate D:2026 and no Buddhist year",
                  raw.Contains("D:20260304") && !raw.Contains("D:2569"), raw.Contains("CreationDate") ? "" : "no /CreationDate in output");

            using var reopened = PdfReader.Open(path, PdfDocumentOpenMode.ReadOnly);
            Check("date culture: the date reads back as 2026 under th-TH",
                  reopened.Info.CreationDate.Year == 2026, reopened.Info.CreationDate.ToString("O", CultureInfo.InvariantCulture));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }
}
