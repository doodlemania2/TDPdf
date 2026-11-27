using System.IO;
using System.Runtime.InteropServices;
using Docnet.Core;

namespace TDPdf.Services
{
    // ── Paragraph rewriting through PDFium ───────────────────────────────────────────────────
    // A paragraph is several text objects, one per line (or per run), and PDFium's SetText only
    // swaps the string of one object, so it cannot add or remove a line. Rewriting a paragraph
    // therefore means: find the objects that make it up, build one new text object per reflowed
    // line from the first object's font, size, matrix and colour, insert them where the old ones
    // were, and remove the old ones. The font is the document's own, so the new lines look like
    // the old ones; whether that font can DRAW what was typed is checked here (cmap) and again by
    // the caller on the finished file (see PdfParagraphEdit).
    //
    // Everything here holds PdfiumLock for its whole length and uses the Raw externs, like the
    // other editing code in this class. The externs are kept in this file, under their own names,
    // so a second feature touching PdfiumInterop.cs does not collide with them.

    internal static partial class PdfiumInterop
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ParaMatrix { public float a, b, c, d, e, f; }

        [DllImport("pdfium.dll", EntryPoint = "FPDFPageObj_CreateTextObj", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr Para_CreateTextObj(IntPtr document, IntPtr font, float fontSize);

        [DllImport("pdfium.dll", EntryPoint = "FPDFText_LoadStandardFont", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr Para_LoadStandardFont(IntPtr document, [MarshalAs(UnmanagedType.LPStr)] string font);

        [DllImport("pdfium.dll", EntryPoint = "FPDFTextObj_GetFontSize", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool Para_GetFontSize(IntPtr textObject, out float size);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPageObj_GetMatrix", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool Para_GetMatrix(IntPtr pageObject, out ParaMatrix matrix);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPageObj_SetMatrix", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool Para_SetMatrix(IntPtr pageObject, ref ParaMatrix matrix);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPageObj_GetFillColor", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool Para_GetFillColor(IntPtr pageObject, out uint r, out uint g, out uint b, out uint a);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPageObj_SetFillColor", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool Para_SetFillColor(IntPtr pageObject, uint r, uint g, uint b, uint a);

        [DllImport("pdfium.dll", EntryPoint = "FPDFTextObj_SetTextRenderMode", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool Para_SetTextRenderMode(IntPtr textObject, int mode);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPage_InsertObject", CallingConvention = CallingConvention.Cdecl)]
        private static extern void Para_InsertObject(IntPtr page, IntPtr pageObject);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPage_InsertObjectAtIndex", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool Para_InsertObjectAtIndex(IntPtr page, IntPtr pageObject, UIntPtr index);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPageObj_HasTransparency", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool Para_HasTransparency(IntPtr pageObject);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPath_GetDrawMode", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool Para_GetDrawMode(IntPtr path, out int fillMode, out bool stroke);

        private static readonly Lazy<bool> s_paragraphApi = new(ProbeParagraphApi);

        /// <summary>Whether the PDFium in this process can create text objects and place them (what a paragraph rewrite needs).</summary>
        internal static bool CanRewriteParagraphs => CanEditText && s_paragraphApi.Value;

        private static bool ProbeParagraphApi()
        {
            try
            {
                if (!NativeLibrary.TryLoad("pdfium.dll", typeof(PdfiumInterop).Assembly, null, out var handle)) return false;
                foreach (var n in new[]
                {
                    "FPDFPageObj_CreateTextObj", "FPDFTextObj_GetFontSize", "FPDFPageObj_GetMatrix",
                    "FPDFPageObj_SetMatrix", "FPDFPageObj_GetFillColor", "FPDFPageObj_SetFillColor",
                    "FPDFPage_InsertObject", "FPDFText_LoadStandardFont", "FPDFTextObj_SetTextRenderMode",
                })
                    if (!NativeLibrary.TryGetExport(handle, n, out _)) return false;
                return true;
            }
            catch { return false; }
        }

        private static bool HasExport(string name)
        {
            try
            {
                return NativeLibrary.TryLoad("pdfium.dll", typeof(PdfiumInterop).Assembly, null, out var h)
                       && NativeLibrary.TryGetExport(h, name, out _);
            }
            catch { return false; }
        }

        // ---- what is there ---------------------------------------------------------------------

        /// <summary>One text object that belongs to the paragraph being edited.</summary>
        private sealed class ParaMember
        {
            public IntPtr Obj;
            /// <summary>Index on the page (page-level members) or -1 (inside a form).</summary>
            public int PageIndex = -1;
            public IntPtr Form = IntPtr.Zero;
            public float L, B, R, T;
            public IntPtr Font;
            public float Size;
            public ParaMatrix Matrix;
            public uint Cr, Cg, Cb, Ca;
            public int RenderMode;
        }

        /// <summary>What a look at the paragraph's text objects found, before anything is changed.</summary>
        internal sealed class ParagraphScan
        {
            internal int TextObjects { get; set; }
            /// <summary>Every member is invisible text (render mode 3): the layer OCR leaves over a scan.</summary>
            internal bool InvisibleOnly { get; set; }
            /// <summary>Some member sits inside a Form XObject.</summary>
            internal bool InForm { get; set; }
            /// <summary>More than one font, size or colour among the members.</summary>
            internal bool MixedStyles { get; set; }
            /// <summary>A member is turned or flipped, or drawn stroked.</summary>
            internal bool Unsupported { get; set; }
            /// <summary>An opaque path or picture is painted after the text, over it.</summary>
            internal bool OpaqueCover { get; set; }
            internal string? Error { get; set; }
        }

        private static List<ParaMember> CollectMembers(IntPtr page, PdfRect bounds, bool includeForms)
        {
            var members = new List<ParaMember>();
            int count = FPDFPage_CountObjectsRaw(page);
            for (int i = 0; i < count; i++)
            {
                IntPtr obj = FPDFPage_GetObjectRaw(page, i);
                if (obj == IntPtr.Zero) continue;
                int type = FPDFPageObj_GetTypeRaw(obj);
                if (type == PageObjText)
                {
                    var m = Describe(obj, bounds);
                    if (m is not null) { m.PageIndex = i; members.Add(m); }
                }
                else if (type == PageObjForm && includeForms && CanEditFormXObjectContent)
                {
                    int n = FPDFFormObj_CountObjectsRaw(obj);
                    for (int k = 0; k < n; k++)
                    {
                        IntPtr child = FPDFFormObj_GetObjectRaw(obj, (uint)k);
                        if (child == IntPtr.Zero || FPDFPageObj_GetTypeRaw(child) != PageObjText) continue;
                        var m = DescribeInForm(child, bounds);
                        if (m is not null) { m.Form = obj; members.Add(m); }
                    }
                }
            }
            return members;
        }

        private static ParaMember? Describe(IntPtr obj, PdfRect bounds)
        {
            if (!FPDFPageObj_GetBoundsRaw(obj, out float l, out float b, out float r, out float t)) return null;
            // The paragraph owns an object when the object's centre is inside the paragraph's box.
            // Containment would drop a line whose ascender pokes out, and intersection would take
            // the line above that merely touches.
            double cx = (l + r) / 2, cy = (b + t) / 2;
            if (cx < bounds.Left || cx > bounds.Right || cy < bounds.Bottom || cy > bounds.Top) return null;
            var m = new ParaMember { Obj = obj, L = l, B = b, R = r, T = t };
            m.Font = FPDFTextObj_GetFontRaw(obj);
            Para_GetFontSize(obj, out m.Size);
            Para_GetMatrix(obj, out m.Matrix);
            if (!Para_GetFillColor(obj, out m.Cr, out m.Cg, out m.Cb, out m.Ca)) { m.Cr = m.Cg = m.Cb = 0; m.Ca = 255; }
            m.RenderMode = FPDFTextObj_GetTextRenderModeRaw(obj);
            return m;
        }

        private static ParaMember? DescribeInForm(IntPtr obj, PdfRect pageBounds)
        {
            // A form's children are in the form's own space, which is not the page's, so their
            // boxes cannot be compared with page-space bounds. Invisible OCR text is the one thing
            // this is used for, and there the caller supplies the lines itself; every child that
            // is invisible text counts as a member of the page's text layer.
            if (FPDFTextObj_GetTextRenderModeRaw(obj) != TextRenderInvisible) return null;
            if (!FPDFPageObj_GetBoundsRaw(obj, out float l, out float b, out float r, out float t)) return null;
            double cx = (l + r) / 2, cy = (b + t) / 2;
            if (cx < pageBounds.Left || cx > pageBounds.Right || cy < pageBounds.Bottom || cy > pageBounds.Top) return null;
            return new ParaMember { Obj = obj, L = l, B = b, R = r, T = t, RenderMode = TextRenderInvisible };
        }

        private static bool SameStyle(ParaMember a, ParaMember b) =>
            a.Font == b.Font
            && Math.Abs(a.Size - b.Size) <= 0.01f * Math.Max(1f, a.Size)
            && Math.Abs(a.Matrix.a - b.Matrix.a) <= 0.01f * Math.Max(1f, Math.Abs(a.Matrix.a))
            && Math.Abs(a.Matrix.d - b.Matrix.d) <= 0.01f * Math.Max(1f, Math.Abs(a.Matrix.d))
            && a.Cr == b.Cr && a.Cg == b.Cg && a.Cb == b.Cb;

        private static bool Upright(ParaMember m) =>
            Math.Abs(m.Matrix.b) < 1e-3 && Math.Abs(m.Matrix.c) < 1e-3 && m.Matrix.a > 0 && m.Matrix.d > 0;

        /// <summary>
        /// Looks at the text objects inside <paramref name="bounds"/> without changing anything:
        /// how many, what style, whether they are an OCR layer, and whether something opaque is
        /// painted over them.
        /// </summary>
        internal static ParagraphScan ScanParagraph(string srcPath, int pageIndex, PdfRect bounds)
        {
            var scan = new ParagraphScan();
            if (!CanRewriteParagraphs) { scan.Error = "this build cannot rewrite text in place"; return scan; }
            try { _ = DocLib.Instance; } catch { }

            lock (PdfiumLock)
            {
                IntPtr doc = FPDF_LoadDocumentRaw(srcPath, null);
                if (doc == IntPtr.Zero) { scan.Error = "the document could not be opened"; return scan; }
                try
                {
                    IntPtr page = FPDF_LoadPageRaw(doc, pageIndex);
                    if (page == IntPtr.Zero) { scan.Error = "the page could not be opened"; return scan; }
                    try
                    {
                        var members = CollectMembers(page, bounds, includeForms: true);
                        scan.TextObjects = members.Count;
                        if (members.Count == 0) return scan;

                        scan.InvisibleOnly = members.All(m => m.RenderMode == TextRenderInvisible);
                        scan.InForm = members.Any(m => m.Form != IntPtr.Zero);
                        if (scan.InvisibleOnly) return scan;

                        var visible = members.Where(m => m.RenderMode != TextRenderInvisible).ToList();
                        scan.InForm = visible.Any(m => m.Form != IntPtr.Zero);
                        scan.MixedStyles = visible.Any(m => !SameStyle(visible[0], m));
                        scan.Unsupported = visible.Any(m => !Upright(m) || m.RenderMode != 0);
                        scan.OpaqueCover = HasOpaqueCover(page, visible);
                        return scan;
                    }
                    finally { FPDF_ClosePageRaw(page); }
                }
                finally { FPDF_CloseDocumentRaw(doc); }
            }
        }

        /// <summary>
        /// True when a filled path or a picture painted after one of the text objects covers most of it
        /// without being see-through. Gradients, soft masks, blend modes (a highlight set to Multiply)
        /// and alpha all count as see-through, which is what <c>FPDFPageObj_HasTransparency</c> reports.
        /// </summary>
        private static bool HasOpaqueCover(IntPtr page, List<ParaMember> text)
        {
            var pageLevel = text.Where(m => m.PageIndex >= 0).ToList();
            if (pageLevel.Count == 0) return false;
            int first = pageLevel.Min(m => m.PageIndex);
            bool canAskTransparency = HasExport("FPDFPageObj_HasTransparency");
            bool canAskDrawMode = HasExport("FPDFPath_GetDrawMode");

            int count = FPDFPage_CountObjectsRaw(page);
            for (int i = first + 1; i < count; i++)
            {
                IntPtr obj = FPDFPage_GetObjectRaw(page, i);
                if (obj == IntPtr.Zero) continue;
                int type = FPDFPageObj_GetTypeRaw(obj);
                if (type != PageObjPath && type != PageObjImage && type != PageObjShading) continue;
                if (!FPDFPageObj_GetBoundsRaw(obj, out float l, out float b, out float r, out float t)) continue;

                bool covers = false;
                foreach (var m in pageLevel)
                {
                    if (m.PageIndex >= i) continue;   // drawn after the shape, so it is on top of it
                    double ow = Math.Min(r, m.R) - Math.Max(l, m.L);
                    double oh = Math.Min(t, m.T) - Math.Max(b, m.B);
                    double area = Math.Max(m.R - m.L, 0.01) * Math.Max(m.T - m.B, 0.01);
                    if (ow > 0 && oh > 0 && ow * oh >= 0.5 * area) { covers = true; break; }
                }
                if (!covers) continue;

                if (type == PageObjShading) continue;   // a gradient is not an opaque cover by itself
                if (canAskTransparency && Para_HasTransparency(obj)) continue;
                if (type == PageObjPath)
                {
                    // Only a FILLED path hides what is under it; a stroked outline is a box round the text.
                    if (canAskDrawMode && Para_GetDrawMode(obj, out int fill, out _) && fill == 0) continue;
                    if (Para_GetFillColor(obj, out _, out _, out _, out uint a) && a < 255) continue;
                }
                return true;
            }
            return false;
        }

        // ---- the rewrite -----------------------------------------------------------------------

        internal enum ParagraphOutcome
        {
            Replaced,
            NotFound,
            /// <summary>The paragraph's font has no glyph for something typed. <see cref="ParagraphResult.Detail"/> lists them.</summary>
            FontCannotRender,
            MixedStyles,
            /// <summary>Turned text, text in a form, or something else this rewrite does not handle. Detail says what.</summary>
            Unsupported,
            Failed,
        }

        /// <summary>A line drawn at an exact place, for the invisible layer under a scan.</summary>
        internal readonly record struct ParagraphPresetLine(string Text, double Left, double Baseline, double FontSize);

        internal sealed class ParagraphRequest
        {
            internal int PageIndex { get; init; }
            /// <summary>PDF-space box of the paragraph, a little generous. Objects whose centre is inside it are the paragraph.</summary>
            internal PdfRect Bounds { get; init; }
            internal string NewText { get; init; } = "";
            /// <summary>PDF x of the first old line's leftmost glyph (what the finder read from the displayed page).</summary>
            internal double FirstLineLeft { get; init; }
            /// <summary>PDF x of the column's left edge and its width.</summary>
            internal double ColumnLeft { get; init; }
            internal double ColumnWidth { get; init; }
            /// <summary>The first line's start relative to the column's left edge (an indent is positive).</summary>
            internal double FirstLineOffset { get; init; }
            internal ParagraphAlignment Alignment { get; init; }
            /// <summary>Baseline-to-baseline distance in PDF units.</summary>
            internal double Pitch { get; init; }
            /// <summary>When set, the paragraph is an OCR layer: these invisible lines replace it and nothing is reflowed.</summary>
            internal IReadOnlyList<ParagraphPresetLine>? Invisible { get; init; }
        }

        /// <summary>A line as written, with where each character ends so highlights can follow.</summary>
        internal sealed class ParagraphLineResult
        {
            internal string Text { get; init; } = "";
            internal int Start { get; init; }
            internal double Left { get; init; }
            /// <summary>Width of each prefix of <see cref="Text"/> (length + 1 entries, the first 0), in PDF units.</summary>
            internal double[] Edges { get; init; } = Array.Empty<double>();
        }

        internal sealed class ParagraphResult
        {
            internal bool Ok { get; set; }
            internal string? Error { get; set; }
            internal ParagraphOutcome Outcome { get; set; } = ParagraphOutcome.Failed;
            internal string Detail { get; set; } = "";
            internal List<ParagraphLineResult> Lines { get; } = new();
            internal int ObjectsRemoved { get; set; }
        }

        /// <summary>
        /// Replaces the text objects of one paragraph with reflowed lines in the same font.
        /// </summary>
        internal static ParagraphResult ReplaceParagraph(string srcPath, string destPath, ParagraphRequest req)
        {
            var result = new ParagraphResult();
            if (!CanRewriteParagraphs) { result.Error = "this build cannot rewrite text in place"; result.Outcome = ParagraphOutcome.Unsupported; return result; }
            try { _ = DocLib.Instance; } catch { }

            lock (PdfiumLock)
            {
                IntPtr doc = FPDF_LoadDocumentRaw(srcPath, null);
                if (doc == IntPtr.Zero) { result.Error = "the document could not be opened"; return result; }
                var orphaned = new List<IntPtr>();
                try
                {
                    IntPtr page = FPDF_LoadPageRaw(doc, req.PageIndex);
                    if (page == IntPtr.Zero) { result.Error = "the page could not be opened"; return result; }
                    try
                    {
                        bool invisible = req.Invisible is not null;
                        var members = CollectMembers(page, req.Bounds, includeForms: invisible);
                        if (invisible) members = members.Where(m => m.RenderMode == TextRenderInvisible).ToList();
                        if (members.Count == 0)
                        {
                            result.Outcome = ParagraphOutcome.NotFound;
                            result.Error = "the paragraph's text could not be found on the page any more";
                            return result;
                        }

                        if (invisible)
                        {
                            if (!RewriteInvisible(doc, page, members, req, result, orphaned)) return result;
                        }
                        else
                        {
                            if (members.Any(m => m.Form != IntPtr.Zero))
                            {
                                result.Outcome = ParagraphOutcome.Unsupported;
                                result.Detail = "its text sits inside a form";
                                result.Error = "the paragraph's text sits inside a form";
                                return result;
                            }
                            if (members.Any(m => m.RenderMode == TextRenderInvisible))
                            {
                                result.Outcome = ParagraphOutcome.Unsupported;
                                result.Detail = "it mixes visible and invisible text";
                                result.Error = result.Detail;
                                return result;
                            }
                            if (!RewriteVisible(doc, page, members, req, result, orphaned)) return result;
                        }

                        if (!FPDFPage_GenerateContentRaw(page))
                        {
                            result.Outcome = ParagraphOutcome.Failed;
                            result.Error = $"content could not be regenerated for page {req.PageIndex + 1}";
                            return result;
                        }
                    }
                    finally { FPDF_ClosePageRaw(page); }

                    if (!PdfiumSave(doc, destPath))
                    {
                        result.Outcome = ParagraphOutcome.Failed;
                        result.Error = "the edited document could not be written";
                        return result;
                    }
                    result.Ok = true;
                    result.Outcome = ParagraphOutcome.Replaced;
                    return result;
                }
                finally
                {
                    foreach (var o in orphaned) { try { FPDFPageObj_DestroyRaw(o); } catch { } }
                    FPDF_CloseDocumentRaw(doc);
                }
            }
        }

        private static bool RewriteVisible(
            IntPtr doc, IntPtr page, List<ParaMember> members, ParagraphRequest req,
            ParagraphResult result, List<IntPtr> orphaned)
        {
            if (members.Any(m => !SameStyle(members[0], m)))
            {
                result.Outcome = ParagraphOutcome.MixedStyles;
                result.Detail = "it mixes fonts, sizes or colours";
                result.Error = "the paragraph mixes fonts, sizes or colours";
                return false;
            }
            if (members.Any(m => !Upright(m) || m.RenderMode != 0))
            {
                result.Outcome = ParagraphOutcome.Unsupported;
                result.Detail = "its text is turned, flipped or outlined";
                result.Error = "the paragraph's text is turned, flipped or outlined";
                return false;
            }
            if (HasOpaqueCover(page, members))
            {
                result.Outcome = ParagraphOutcome.Unsupported;
                result.Detail = "something opaque is drawn over it";
                result.Error = "something opaque is drawn over the paragraph";
                return false;
            }

            // The first object: the topmost, and the leftmost of those.
            var first = members.OrderByDescending(m => Math.Round(m.T, 1)).ThenBy(m => m.L).First();

            string missing = UnrenderableCharacters(first.Obj, req.NewText);
            if (missing.Length > 0)
            {
                result.Outcome = ParagraphOutcome.FontCannotRender;
                result.Detail = missing;
                result.Error = "the paragraph's font has no glyph for some of the new text";
                return false;
            }

            IntPtr scratch = Para_CreateTextObj(doc, first.Font, first.Size);
            if (scratch == IntPtr.Zero)
            {
                result.Outcome = ParagraphOutcome.Failed;
                result.Error = "the engine could not create a text object";
                return false;
            }
            orphaned.Add(scratch);
            double scale = first.Matrix.a;
            double Measure(string s)
            {
                if (s.Length == 0) return 0;
                if (!FPDFText_SetTextRaw(scratch, s)) return 0;
                return FPDFPageObj_GetBoundsRaw(scratch, out _, out _, out float r, out _) ? Math.Max(r, 0) * scale : 0;
            }

            double firstWidth = Math.Max(req.ColumnWidth - req.FirstLineOffset, 1);
            var reflowed = ParagraphReflow.Break(req.NewText, firstWidth, Math.Max(req.ColumnWidth, 1), Measure);
            if (reflowed.Count == 0)
            {
                result.Outcome = ParagraphOutcome.Failed;
                result.Error = "there is no text to write";
                return false;
            }

            // The new objects, built before anything is removed so a failure leaves the page alone.
            var created = new List<IntPtr>();
            for (int i = 0; i < reflowed.Count; i++)
            {
                var line = reflowed[i];
                double width = Measure(line.Text);
                double avail = i == 0 ? firstWidth : req.ColumnWidth;
                double left = req.ColumnLeft + (i == 0 ? req.FirstLineOffset : 0)
                              + ParagraphReflow.LeftOffset(req.Alignment, width, avail);

                IntPtr obj = Para_CreateTextObj(doc, first.Font, first.Size);
                if (obj == IntPtr.Zero || (line.Text.Length > 0 && !FPDFText_SetTextRaw(obj, line.Text)))
                {
                    foreach (var c in created) FPDFPageObj_DestroyRaw(c);
                    if (obj != IntPtr.Zero) FPDFPageObj_DestroyRaw(obj);
                    result.Outcome = ParagraphOutcome.Failed;
                    result.Error = "the engine refused one of the new lines";
                    return false;
                }
                var mx = first.Matrix;
                mx.e = (float)(first.Matrix.e + (left - req.FirstLineLeft));
                mx.f = (float)(first.Matrix.f - i * req.Pitch);
                Para_SetMatrix(obj, ref mx);
                Para_SetFillColor(obj, first.Cr, first.Cg, first.Cb, first.Ca);
                created.Add(obj);

                var edges = new double[line.Text.Length + 1];
                for (int k = 1; k <= line.Text.Length; k++) edges[k] = Measure(line.Text.Substring(0, k));
                result.Lines.Add(new ParagraphLineResult { Text = line.Text, Start = line.Start, Left = left, Edges = edges });
            }

            // Out with the old, from the back so indices hold, then in with the new at the first
            // one's position so the stacking order of everything else is unchanged.
            int insertAt = members.Min(m => m.PageIndex);
            foreach (var m in members.OrderByDescending(m => m.PageIndex))
            {
                if (FPDFPage_RemoveObjectRaw(page, m.Obj)) { orphaned.Add(m.Obj); result.ObjectsRemoved++; }
            }
            bool atIndex = HasExport("FPDFPage_InsertObjectAtIndex");
            for (int i = 0; i < created.Count; i++)
            {
                if (atIndex) Para_InsertObjectAtIndex(page, created[i], (UIntPtr)(uint)(insertAt + i));
                else Para_InsertObject(page, created[i]);
            }
            // Inserted objects belong to the page now; the scratch object was never inserted.
            return true;
        }

        private static bool RewriteInvisible(
            IntPtr doc, IntPtr page, List<ParaMember> members, ParagraphRequest req,
            ParagraphResult result, List<IntPtr> orphaned)
        {
            var preset = req.Invisible!;
            IntPtr font = Para_LoadStandardFont(doc, "Helvetica");
            if (font == IntPtr.Zero)
            {
                result.Outcome = ParagraphOutcome.Unsupported;
                result.Detail = "no font is available for the text layer";
                result.Error = result.Detail;
                return false;
            }

            var created = new List<IntPtr>();
            foreach (var line in preset)
            {
                if (line.Text.Length == 0) continue;
                IntPtr obj = Para_CreateTextObj(doc, font, (float)Math.Max(line.FontSize, 1));
                if (obj == IntPtr.Zero || !FPDFText_SetTextRaw(obj, line.Text))
                {
                    foreach (var c in created) FPDFPageObj_DestroyRaw(c);
                    if (obj != IntPtr.Zero) FPDFPageObj_DestroyRaw(obj);
                    result.Outcome = ParagraphOutcome.Failed;
                    result.Error = "the engine refused one of the new lines";
                    return false;
                }
                var mx = new ParaMatrix { a = 1, b = 0, c = 0, d = 1, e = (float)line.Left, f = (float)line.Baseline };
                Para_SetMatrix(obj, ref mx);
                Para_SetTextRenderMode(obj, TextRenderInvisible);
                created.Add(obj);
                result.Lines.Add(new ParagraphLineResult { Text = line.Text, Left = line.Left });
            }

            foreach (var m in members.OrderByDescending(m => m.PageIndex))
            {
                bool removed = m.Form != IntPtr.Zero
                    ? FPDFFormObj_RemoveObjectRaw(m.Form, m.Obj)
                    : FPDFPage_RemoveObjectRaw(page, m.Obj);
                if (removed) { orphaned.Add(m.Obj); result.ObjectsRemoved++; }
            }
            foreach (var obj in created) Para_InsertObject(page, obj);
            return true;
        }
    }
}
