using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharpCore.Pdf;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf.Services
{
    /// <summary>
    /// Changes the words in a PDF, rather than covering them with new ones.
    /// </summary>
    /// <remarks>
    /// TDPdf has always "edited" existing text by painting a white rectangle over it and drawing
    /// the replacement on top. That works and it is a lie the file tells about itself: the original
    /// words are still in there, still selectable, still in every extraction, still on any machine
    /// the file reaches. This replaces the string on the text object itself.
    ///
    /// THE FONT IS THE WHOLE PROBLEM. Almost every PDF embeds a SUBSET — the glyphs the document
    /// actually used and nothing else — so a document that never contained a "Z" has no "Z" to draw
    /// with. PDFium reports nothing at all when asked for one: it accepts the string, writes the
    /// charcodes, and the missing letters come out as blanks. On a fixture here, replacing a run
    /// with "PUZZLED SPHINX" produced "    LE     IN" in the saved file, reported as a success.
    ///
    /// The font's own cmap is not a reliable pre-check either. A CID-keyed subset — the commonest
    /// embedded shape — maps charcodes to glyphs through the PDF's encoding rather than a cmap
    /// table, and typically ships without a usable one; the fixture above reads as "unparseable",
    /// which cannot be treated as "covers nothing" without refusing edits that would have worked.
    /// So the cmap check stays as a cheap early filter and nothing rests on it.
    ///
    /// What everything rests on is the same rule redaction uses: DO THE WORK, THEN READ THE
    /// FINISHED FILE AND CHECK. If the saved document does not say what the user typed, no file is
    /// handed back and the caller is told which characters the font could not draw — so it can fall
    /// back to the overlay, which is uglier and always works.
    /// </remarks>
    internal static class PdfTextEdit
    {
        internal sealed class Result
        {
            internal bool Ok { get; set; }
            internal string? Error { get; set; }

            /// <summary>Edits that reached the finished file intact.</summary>
            internal int Replaced { get; set; }

            /// <summary>Characters the run's font could not draw, if that is why this failed.</summary>
            internal string MissingCharacters { get; set; } = "";

            /// <summary>Pages that cannot be edited in place without collateral damage.</summary>
            internal IReadOnlyList<string> UnsafePages { get; set; } = Array.Empty<string>();

            /// <summary>True when the caller should fall back to the white-out overlay.</summary>
            /// <remarks>
            /// Distinguishes "this document cannot be edited this way" — which has a perfectly good
            /// answer, the way TDPdf has always done it — from "something went wrong", which does
            /// not. Only the first should quietly take the other path.
            /// </remarks>
            internal bool ShouldUseOverlay { get; set; }
        }

        /// <summary>
        /// Applies text replacements to <paramref name="srcPath"/>, writing to
        /// <paramref name="destPath"/> only if every one of them survived to the finished file.
        /// </summary>
        internal static Result Apply(
            string srcPath, string destPath,
            IReadOnlyList<PdfiumInterop.TextEditRequest> edits,
            PdfDocument? openDocument = null)
        {
            var result = new Result();
            if (edits.Count == 0) { result.Ok = true; return result; }

            if (!PdfiumInterop.CanEditText)
            {
                result.Error = PdfiumInterop.DescribeEditApi();
                result.ShouldUseOverlay = true;
                return result;
            }

            // The same gate redaction uses, for the same reason: regenerating a content stream
            // discards non-device colour, shadings, inline images and soft masks. Editing one word
            // on a CMYK invoice is not worth recolouring the rest of it, and here there IS a good
            // fallback — the overlay this feature replaces.
            var unsafePages = new List<string>();
            if (openDocument is not null)
                foreach (int pageIndex in edits.Select(e => e.PageIndex).Distinct().OrderBy(i => i))
                {
                    if (pageIndex < 0 || pageIndex >= openDocument.PageCount) continue;
                    var hazard = PdfContentInspector.Inspect(openDocument.Pages[pageIndex]);
                    if (hazard != ContentHazard.None)
                        unsafePages.Add($"page {pageIndex + 1}: {PdfContentInspector.Describe(hazard)}");
                }
            if (unsafePages.Count > 0)
            {
                result.UnsafePages = unsafePages;
                result.ShouldUseOverlay = true;
                result.Error = "this page carries content that in-place editing would damage";
                return result;
            }

            string workDir = Path.GetDirectoryName(destPath) ?? Path.GetTempPath();
            string staged = Path.Combine(workDir, $"tdpdf_textedit_{Guid.NewGuid():N}.pdf");
            try
            {
                var engine = PdfiumInterop.ReplaceText(srcPath, staged, edits);
                if (!engine.Ok)
                {
                    result.Error = engine.Error ?? "the text could not be replaced";
                    result.ShouldUseOverlay = true;
                    return result;
                }

                var refusedFont = engine.Items
                    .Where(i => i.Outcome == PdfiumInterop.TextEditOutcome.FontCannotRender)
                    .ToList();
                if (refusedFont.Count > 0)
                {
                    result.MissingCharacters = new string(
                        refusedFont.SelectMany(i => i.Detail).Distinct().ToArray());
                    result.Error = $"the font in this document has no glyph for {Describe(result.MissingCharacters)}";
                    result.ShouldUseOverlay = true;
                    return result;
                }

                var notFound = engine.Items
                    .Where(i => i.Outcome == PdfiumInterop.TextEditOutcome.NotFound)
                    .ToList();
                if (notFound.Count > 0)
                {
                    result.Error = "the text to replace could not be found on the page any more";
                    result.ShouldUseOverlay = true;
                    return result;
                }

                // The check that actually decides it.
                string dropped = FindDroppedCharacters(staged, edits);
                if (dropped.Length > 0)
                {
                    result.MissingCharacters = dropped;
                    result.Error = $"the font in this document cannot draw {Describe(dropped)}";
                    result.ShouldUseOverlay = true;
                    return result;
                }

                File.Copy(staged, destPath, overwrite: true);
                result.Replaced = engine.Replaced;
                result.Ok = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.ShouldUseOverlay = true;
                return result;
            }
            finally
            {
                try { if (File.Exists(staged)) File.Delete(staged); } catch { /* temp file */ }
            }
        }

        /// <summary>
        /// Characters the user asked for that did not survive into <paramref name="path"/>.
        /// </summary>
        /// <remarks>
        /// Compared with whitespace removed, because PDF text is POSITIONED rather than spaced: a
        /// run often holds no space characters at all and gets its gaps from the text matrix, so
        /// extraction and the typed string differ by exactly the spaces even on a perfect edit.
        ///
        /// Reporting the individual characters rather than just "it did not work" is what lets the
        /// message be useful — "this document's font has no ‘Z’ or ‘X’" tells someone why, and why
        /// a different word would be fine.
        /// </remarks>
        private static string FindDroppedCharacters(
            string path, IReadOnlyList<PdfiumInterop.TextEditRequest> edits)
        {
            var missing = new List<char>();
            using var doc = PdfPigDoc.Open(path);

            foreach (var group in edits.GroupBy(e => e.PageIndex))
            {
                if (group.Key < 0 || group.Key >= doc.NumberOfPages) continue;
                string pageText = Squash(doc.GetPage(group.Key + 1).Text);

                foreach (var edit in group)
                {
                    string want = Squash(edit.NewText);
                    if (want.Length == 0 || pageText.Contains(want, StringComparison.Ordinal)) continue;

                    // Which characters are simply absent from the page now. A letter that survived
                    // elsewhere is not the problem, so only report the ones with no instance left.
                    foreach (char c in want)
                        if (!pageText.Contains(c) && !missing.Contains(c))
                            missing.Add(c);

                    // The replacement did not land and no single character explains it — say so
                    // with a marker the caller turns into a general message rather than a list.
                    if (missing.Count == 0) missing.Add('\0');
                }
            }
            return new string(missing.ToArray());
        }

        private static string Describe(string characters)
        {
            var real = new string(characters.Where(c => c != '\0').ToArray());
            return real.Length == 0
                ? "the replacement text"
                : string.Join(", ", real.Select(c => $"“{c}”"));
        }

        private static string Squash(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }
    }
}
