using System.IO;
using PdfSharpCore.Pdf;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf.Services
{
    /// <summary>
    /// Rewrites a whole paragraph in the PDF itself, then reads the finished file to check it.
    /// </summary>
    /// <remarks>
    /// The sibling of <see cref="PdfTextEdit"/> for a block of lines. The same three steps, for the
    /// same reasons: refuse pages whose content PDFium's generator would damage, let the engine do
    /// the work, and then READ THE FINISHED FILE, because a font that accepts a string can still
    /// draw nothing for half of it. A file that fails the check is never handed back.
    /// </remarks>
    internal static class PdfParagraphEdit
    {
        internal sealed class Result
        {
            internal bool Ok { get; set; }
            /// <summary>Why it did not happen, in words fit for a dialog.</summary>
            internal string? Error { get; set; }
            internal string MissingCharacters { get; set; } = "";
            internal IReadOnlyList<PdfiumInterop.ParagraphLineResult> Lines { get; set; } = Array.Empty<PdfiumInterop.ParagraphLineResult>();
            internal PdfiumInterop.ParagraphOutcome Outcome { get; set; } = PdfiumInterop.ParagraphOutcome.Failed;
        }

        internal static Result Apply(
            string srcPath, string destPath, PdfiumInterop.ParagraphRequest request, PdfDocument? openDocument = null)
        {
            var result = new Result();
            if (!PdfiumInterop.CanRewriteParagraphs)
            {
                result.Error = PdfiumInterop.CanEditText ? "this build's PDF engine cannot create text" : PdfiumInterop.DescribeEditApi();
                result.Outcome = PdfiumInterop.ParagraphOutcome.Unsupported;
                return result;
            }

            if (openDocument is not null && request.PageIndex >= 0 && request.PageIndex < openDocument.PageCount)
            {
                var hazard = PdfContentInspector.Inspect(openDocument.Pages[request.PageIndex]);
                if (hazard != ContentHazard.None)
                {
                    result.Error = $"page {request.PageIndex + 1}: {PdfContentInspector.Describe(hazard)}";
                    result.Outcome = PdfiumInterop.ParagraphOutcome.Unsupported;
                    return result;
                }
            }

            string workDir = Path.GetDirectoryName(destPath) ?? Path.GetTempPath();
            string staged = Path.Combine(workDir, $"tdpdf_paragraph_{Guid.NewGuid():N}.pdf");
            try
            {
                var engine = PdfiumInterop.ReplaceParagraph(srcPath, staged, request);
                result.Outcome = engine.Outcome;
                if (!engine.Ok)
                {
                    result.Error = engine.Error ?? "the paragraph could not be rewritten";
                    if (engine.Outcome == PdfiumInterop.ParagraphOutcome.FontCannotRender)
                    {
                        result.MissingCharacters = engine.Detail;
                        result.Error = $"the font in this document has no glyph for {Describe(engine.Detail)}";
                    }
                    return result;
                }

                string dropped = FindDroppedCharacters(staged, request, engine.Lines);
                if (dropped.Length > 0)
                {
                    result.MissingCharacters = dropped;
                    result.Error = dropped == "\0"
                        ? "the finished file does not hold the new text"
                        : $"the font in this document cannot draw {Describe(dropped)}";
                    result.Outcome = PdfiumInterop.ParagraphOutcome.FontCannotRender;
                    return result;
                }

                File.Copy(staged, destPath, overwrite: true);
                result.Lines = engine.Lines;
                result.Ok = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.Outcome = PdfiumInterop.ParagraphOutcome.Failed;
                return result;
            }
            finally
            {
                try { if (File.Exists(staged)) File.Delete(staged); } catch { /* temp file */ }
            }
        }

        /// <summary>
        /// Characters of the new lines that are absent from the finished page. A single NUL means the
        /// lines are missing and no one character explains it.
        /// </summary>
        private static string FindDroppedCharacters(
            string path, PdfiumInterop.ParagraphRequest request, IReadOnlyList<PdfiumInterop.ParagraphLineResult> lines)
        {
            using var doc = PdfPigDoc.Open(path);
            if (request.PageIndex < 0 || request.PageIndex >= doc.NumberOfPages) return "";
            string pageText = Squash(doc.GetPage(request.PageIndex + 1).Text);

            var missing = new List<char>();
            foreach (var line in lines)
            {
                string want = Squash(line.Text);
                if (want.Length == 0 || pageText.Contains(want, StringComparison.Ordinal)) continue;
                foreach (char c in want)
                    if (!pageText.Contains(c) && !missing.Contains(c)) missing.Add(c);
                if (missing.Count == 0) missing.Add('\0');
            }
            return new string(missing.ToArray());
        }

        private static string Describe(string characters)
        {
            var real = new string(characters.Where(c => c != '\0').ToArray());
            return real.Length == 0
                ? "the new text"
                : string.Join(", ", real.Select(c => $"“{c}”"));
        }

        private static string Squash(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }
    }
}
