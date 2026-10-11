using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf.Services
{
    /// <summary>One <c>{{type, rN, …}}</c> placeholder Documenso will turn into a field.</summary>
    /// <param name="Text">The placeholder as it appears, braces included.</param>
    /// <param name="Type">The field it becomes.</param>
    /// <param name="RecipientNumber">N in <c>rN</c>: 1 is the first recipient in the list.</param>
    /// <param name="PageNumber">1-based.</param>
    internal sealed record DocumensoPlaceholder(string Text, DocumensoFieldType Type, int RecipientNumber, int PageNumber)
    {
        /// <summary>Counts toward Documenso's one-signature-field-per-signer rule.</summary>
        public bool IsSignature => Type is DocumensoFieldType.Signature or DocumensoFieldType.FreeSignature;
    }

    /// <summary>
    /// Finds the <c>{{…}}</c> field placeholders in a PDF that Documenso will auto-place, using the
    /// exact grammar Documenso v2.18.0 applies, so TDPdf can say "these will be used" — and refuse
    /// to send when they could not be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Grammar</b>, from <c>packages/lib/server-only/pdf/auto-place-fields.ts</c> and
    /// <c>helpers.ts</c> at v2.18.0: the regex <c>/\{\{([^}]+)\}\}/g</c> over each page's text; the
    /// inside split on commas and trimmed; the first part is the field type, matched
    /// case-insensitively against SIGNATURE, FREE_SIGNATURE, INITIALS, NAME, EMAIL, DATE, TEXT,
    /// NUMBER, RADIO, CHECKBOX, DROPDOWN (anything else is skipped, not an error); the second must
    /// match <c>/^r\d+$/i</c> (a placeholder with no recipient, like <c>{{name}}</c>, is skipped —
    /// Documenso reserves it); the rest are <c>key=value</c> field options TDPdf does not need.
    /// </para>
    /// <para>
    /// <b>What the server does with them:</b> at <c>/envelope/create</c> it finds them, draws a
    /// white box over the text, and creates the fields itself, with <c>rN</c> resolved to the Nth
    /// recipient in the create payload. So TDPdf sends NO explicit placeholder fields (they would
    /// be duplicates), passes its recipients inside the create call, and must not let a send go out
    /// with an <c>rN</c> beyond the recipient count — the server answers that with a 400.
    /// </para>
    /// <para>
    /// Text comes from PdfPig, already the app's text extractor. Documenso uses its own (libpdf),
    /// so on an exotic PDF the two could disagree about a placeholder; the dialog's count is
    /// therefore phrased as what TDPdf found, and the server remains the authority.
    /// </para>
    /// No WPF types: tests/PdfCore compiles this file.
    /// </remarks>
    internal static class DocumensoPlaceholders
    {
        private static readonly Regex Placeholder = new(@"\{\{([^}]+)\}\}", RegexOptions.CultureInvariant);
        private static readonly Regex RecipientRef = new(@"^r(\d+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>Placeholders in one page's text, in reading order.</summary>
        internal static List<DocumensoPlaceholder> Parse(string pageText, int pageNumber)
        {
            var found = new List<DocumensoPlaceholder>();
            if (string.IsNullOrEmpty(pageText)) return found;

            foreach (Match m in Placeholder.Matches(pageText))
            {
                string[] parts = m.Groups[1].Value.Split(',').Select(p => p.Trim()).ToArray();
                if (parts.Length < 2) continue;
                if (ParseType(parts[0]) is not { } type) continue;
                var r = RecipientRef.Match(parts[1]);
                if (!r.Success || !int.TryParse(r.Groups[1].Value, out int n)) continue;
                found.Add(new DocumensoPlaceholder(m.Value, type, n, pageNumber));
            }
            return found;
        }

        /// <summary>
        /// Every placeholder in the PDF at <paramref name="path"/>. Empty — never an exception —
        /// when the file cannot be read: detection is advisory, and the server does its own scan.
        /// </summary>
        internal static List<DocumensoPlaceholder> Scan(string path)
        {
            var all = new List<DocumensoPlaceholder>();
            try
            {
                using var doc = PdfPigDoc.Open(path);
                for (int i = 1; i <= doc.NumberOfPages; i++)
                {
                    string text;
                    try { text = doc.GetPage(i).Text; }
                    catch { continue; }   // one unreadable page should not hide the others
                    all.AddRange(Parse(text, i));
                }
            }
            catch
            {
                // Encrypted, damaged, or gone: say nothing rather than something wrong.
            }
            return all;
        }

        /// <summary>
        /// Documenso's <c>parseFieldTypeFromPlaceholder</c>: upper-cased, trimmed, one of the eleven
        /// field types. Null for anything else.
        /// </summary>
        internal static DocumensoFieldType? ParseType(string raw) => raw.Trim().ToUpperInvariant() switch
        {
            "SIGNATURE" => DocumensoFieldType.Signature,
            "FREE_SIGNATURE" => DocumensoFieldType.FreeSignature,
            "INITIALS" => DocumensoFieldType.Initials,
            "NAME" => DocumensoFieldType.Name,
            "EMAIL" => DocumensoFieldType.Email,
            "DATE" => DocumensoFieldType.Date,
            "TEXT" => DocumensoFieldType.Text,
            "NUMBER" => DocumensoFieldType.Number,
            "RADIO" => DocumensoFieldType.Radio,
            "CHECKBOX" => DocumensoFieldType.Checkbox,
            "DROPDOWN" => DocumensoFieldType.Dropdown,
            _ => null,
        };
    }
}
