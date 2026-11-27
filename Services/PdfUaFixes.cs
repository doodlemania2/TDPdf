using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;

namespace TDPdf.Services
{
    /// <summary>
    /// The simple repairs the accessibility check offers (#243): set the document language, set the title,
    /// ask for the title to be shown in the window's title bar, and mark a document that already has tags as
    /// tagged. Each changes the open document's own objects only; the caller owns the undo step and the
    /// dirty flag. Nothing here adds tags: tagging a document is a human job.
    /// </summary>
    internal static class PdfUaFixes
    {
        /// <summary>True when <paramref name="fix"/> needs a value from the user (the language, the title).</summary>
        internal static bool NeedsValue(PdfUaFix fix) => fix is PdfUaFix.SetLanguage or PdfUaFix.SetTitle;

        /// <summary>A language to offer first: the one the user's Windows is set to, as a BCP 47 tag.</summary>
        internal static string SuggestLanguage() =>
            PdfUaValidator.IsLanguageTag(System.Globalization.CultureInfo.CurrentUICulture.Name)
                ? System.Globalization.CultureInfo.CurrentUICulture.Name : "en-US";

        /// <summary>
        /// Applies one fix. Returns false (and changes nothing) when it does not apply: a bad language tag, an
        /// empty title, or marking a document that has no structure tree as tagged.
        /// </summary>
        internal static bool Apply(PdfDocument doc, PdfUaFix fix, string? value = null)
        {
            var catalog = doc.Internals.Catalog;
            switch (fix)
            {
                case PdfUaFix.SetLanguage:
                    if (!PdfUaValidator.IsLanguageTag(value)) return false;
                    catalog.Elements.SetString("/Lang", value!.Trim());
                    return true;

                case PdfUaFix.SetTitle:
                    if (string.IsNullOrWhiteSpace(value)) return false;
                    doc.Info.Title = value.Trim();
                    return true;

                case PdfUaFix.SetDisplayDocTitle:
                    {
                        var vp = catalog.Elements["/ViewerPreferences"] is PdfReference r ? r.Value as PdfDictionary : catalog.Elements["/ViewerPreferences"] as PdfDictionary;
                        if (vp is null)
                        {
                            vp = new PdfDictionary(doc);
                            catalog.Elements["/ViewerPreferences"] = vp;
                        }
                        vp.Elements.SetBoolean("/DisplayDocTitle", true);
                        return true;
                    }

                case PdfUaFix.MarkTagged:
                    {
                        var root = catalog.Elements["/StructTreeRoot"] is PdfReference sr ? sr.Value : catalog.Elements["/StructTreeRoot"];
                        if (root is not PdfDictionary) return false;
                        var mi = catalog.Elements["/MarkInfo"] is PdfReference mr ? mr.Value as PdfDictionary : catalog.Elements["/MarkInfo"] as PdfDictionary;
                        if (mi is null)
                        {
                            mi = new PdfDictionary(doc);
                            catalog.Elements["/MarkInfo"] = mi;
                        }
                        mi.Elements.SetBoolean("/Marked", true);
                        return true;
                    }
            }
            return false;
        }

        /// <summary>The title to offer first when the document has none: its file name without the extension.</summary>
        internal static string SuggestTitle(string? displayName)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(displayName ?? "");
            return name.Replace('_', ' ').Replace('-', ' ').Trim();
        }
    }
}
