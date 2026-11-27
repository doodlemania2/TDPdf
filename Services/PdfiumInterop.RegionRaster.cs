using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Docnet.Core;

namespace TDPdf.Services
{
    // The PDFium half of "Rasterize a region" (#233): render one rectangle of a page's own content
    // (no annotations, no form fields: marks stay marks), and remove what sits under it.
    //
    // Same discipline as RemoveObjectsIntersecting in PdfiumInterop.cs: the whole
    // document / page lifetime lives inside one method that holds PdfiumLock, objects removed from a
    // page are destroyed only after the save, and every capability is probed before use.
    internal static partial class PdfiumInterop
    {
        // ---- Raw externs -------------------------------------------------------------------------

        [DllImport("pdfium.dll", EntryPoint = "FPDFText_CountChars", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFText_CountCharsRaw(IntPtr textPage);

        [DllImport("pdfium.dll", EntryPoint = "FPDFText_GetUnicode", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FPDFText_GetUnicodeRaw(IntPtr textPage, int index);

        [DllImport("pdfium.dll", EntryPoint = "FPDFText_GetTextObject", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFText_GetTextObjectRaw(IntPtr textPage, int index);

        [DllImport("pdfium.dll", EntryPoint = "FPDFText_GetCharBox", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFText_GetCharBoxRaw(
            IntPtr textPage, int index, out double left, out double right, out double bottom, out double top);

        [DllImport("pdfium.dll", EntryPoint = "FPDFText_GetCharOrigin", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFText_GetCharOriginRaw(IntPtr textPage, int index, out double x, out double y);

        [StructLayout(LayoutKind.Sequential)]
        private struct FsMatrix
        {
            public float A, B, C, D, E, F;
        }

        [DllImport("pdfium.dll", EntryPoint = "FPDFPageObj_GetMatrix", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFPageObj_GetMatrixRaw(IntPtr pageObject, out FsMatrix matrix);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPageObj_SetMatrix", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFPageObj_SetMatrixRaw(IntPtr pageObject, ref FsMatrix matrix);

        [DllImport("pdfium.dll", EntryPoint = "FPDFTextObj_GetFontSize", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFTextObj_GetFontSizeRaw(IntPtr textObject, out float size);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPageObj_CreateTextObj", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFPageObj_CreateTextObjRaw(IntPtr document, IntPtr font, float fontSize);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPageObj_GetFillColor", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFPageObj_GetFillColorRaw(
            IntPtr pageObject, out uint r, out uint g, out uint b, out uint a);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPageObj_SetFillColor", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFPageObj_SetFillColorRaw(IntPtr pageObject, uint r, uint g, uint b, uint a);

        [DllImport("pdfium.dll", EntryPoint = "FPDFTextObj_SetTextRenderMode", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFTextObj_SetTextRenderModeRaw(IntPtr textObject, int mode);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPage_InsertObject", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFPage_InsertObjectRaw(IntPtr page, IntPtr pageObject);

        [DllImport("pdfium.dll", EntryPoint = "FPDFPath_GetDrawMode", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFPath_GetDrawModeRaw(IntPtr path, out int fillMode, out int stroke);

        // ---- Capability --------------------------------------------------------------------------

        private static readonly Lazy<bool> s_canSplitText = new(ProbeSplitText);

        /// <summary>
        /// Whether this PDFium can write a text run back in pieces (create a text object, position
        /// it, colour it). False degrades to removing only the runs that lie wholly inside the box.
        /// </summary>
        internal static bool CanSplitText => CanEditText && s_canSplitText.Value;

        private static bool ProbeSplitText()
        {
            try
            {
                if (!NativeLibrary.TryLoad("pdfium.dll", typeof(PdfiumInterop).Assembly, null, out var h)) return false;
                foreach (var n in new[]
                {
                    "FPDFText_CountChars", "FPDFText_GetUnicode", "FPDFText_GetTextObject", "FPDFText_GetCharBox",
                    "FPDFText_GetCharOrigin", "FPDFPageObj_GetMatrix", "FPDFPageObj_SetMatrix",
                    "FPDFTextObj_GetFontSize", "FPDFPageObj_CreateTextObj", "FPDFPageObj_GetFillColor",
                    "FPDFPageObj_SetFillColor", "FPDFTextObj_SetTextRenderMode", "FPDFPage_InsertObject",
                })
                    if (!NativeLibrary.TryGetExport(h, n, out _)) return false;
                return true;
            }
            catch { return false; }
        }

        // ---- Render ------------------------------------------------------------------------------

        /// <summary>
        /// Renders one region of the page's own content: BGRA, opaque white behind it, no
        /// annotations and no form fields (they are marks, not page content, and stay where they are).
        /// The region is the one <see cref="RegionRaster.Plan"/> chose, drawn at exactly the scale
        /// the whole page would be at that resolution, so it lines up with any other render of the
        /// page at the same DPI.
        /// </summary>
        internal static byte[]? RenderRegionContent(string srcPath, int pageIndex, RegionRaster.RegionPlan plan)
        {
            if (plan.Width <= 0 || plan.Height <= 0 || plan.Pixels > RegionRaster.MaxPixels) return null;
            try
            {
                try { _ = DocLib.Instance; } catch { /* force PDFium init */ }

                int stride = checked(plan.Width * 4);
                var bytes = new byte[checked((long)stride * plan.Height) > int.MaxValue ? 0 : stride * plan.Height];
                if (bytes.Length == 0) return null;
                var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try
                {
                    lock (PdfiumLock)
                    {
                        IntPtr doc = FPDF_LoadDocumentRaw(srcPath, null);
                        if (doc == IntPtr.Zero) return null;
                        try
                        {
                            IntPtr page = FPDF_LoadPageRaw(doc, pageIndex);
                            if (page == IntPtr.Zero) return null;
                            try
                            {
                                IntPtr bitmap = FPDFBitmap_CreateExRaw(
                                    plan.Width, plan.Height, FPDFBitmapBgra, pinned.AddrOfPinnedObject(), stride);
                                if (bitmap == IntPtr.Zero) return null;
                                try
                                {
                                    FPDFBitmap_FillRectRaw(bitmap, 0, 0, plan.Width, plan.Height, OpaqueWhite);
                                    // Greyscale smoothing, not LCD: the pixels end up in a file,
                                    // where a colour fringe tuned to one monitor is just noise.
                                    FPDF_RenderPageBitmapRaw(bitmap, page, -plan.X, -plan.Y,
                                        plan.FullWidth, plan.FullHeight, 0, 0);
                                }
                                finally { FPDFBitmap_DestroyRaw(bitmap); }
                            }
                            finally { FPDF_ClosePageRaw(page); }
                        }
                        finally { FPDF_CloseDocumentRaw(doc); }
                    }
                    return bytes;
                }
                finally { pinned.Free(); }
            }
            catch { return null; }
        }

        // ---- Removal -----------------------------------------------------------------------------

        internal sealed class RegionContentResult
        {
            internal bool Ok { get; set; }
            internal string? Error { get; set; }

            /// <summary>Text runs removed because every glyph was inside the box.</summary>
            internal int TextRunsRemoved { get; set; }

            /// <summary>Runs that straddled the box edge and were rewritten without the glyphs inside it.</summary>
            internal int TextRunsRewritten { get; set; }

            /// <summary>Visible paths and shadings removed.</summary>
            internal int ShapesRemoved { get; set; }
            internal int ImagesRemoved { get; set; }

            /// <summary>
            /// Content that is partly under the box and could not be taken out (a run this PDFium cannot
            /// split, something inside a transformed form). It stays where it is, searchable.
            /// </summary>
            internal int LeftPartlyCovered { get; set; }

            /// <summary>The words that were removed, in reading order, for the picture's alternate text.</summary>
            internal string RemovedText { get; set; } = "";
        }

        private sealed class RunInfo
        {
            internal IntPtr Obj;
            internal List<int> Chars = new();
            internal StringBuilder Text = new();
            internal List<(double X, double Y)> Origins = new();
            internal bool[] Inside = Array.Empty<bool>();
            internal RegionRaster.RunAction Action;
            internal double Left, Top;
            internal bool LengthOk;
        }

        /// <summary>
        /// Removes what lies under <paramref name="rect"/> on one page and writes the result to
        /// <paramref name="destPath"/>: glyphs fully inside the box (a run crossing the edge keeps
        /// the glyphs outside it), and paths, shadings, images and forms fully inside. Invisible
        /// paths (clipping only) are left alone and not counted.
        /// </summary>
        internal static RegionContentResult RemoveRegionContent(
            string srcPath, string destPath, int pageIndex, PdfRect rect)
        {
            var result = new RegionContentResult();
            if (!CanEditPageContent)
            {
                result.Error = DescribeEditApi();
                return result;
            }

            try { _ = DocLib.Instance; } catch { /* force PDFium init */ }

            lock (PdfiumLock)
            {
                IntPtr doc = FPDF_LoadDocumentRaw(srcPath, null);
                if (doc == IntPtr.Zero) { result.Error = "the document could not be opened"; return result; }

                var orphaned = new List<IntPtr>();
                try
                {
                    IntPtr page = FPDF_LoadPageRaw(doc, pageIndex);
                    if (page == IntPtr.Zero) { result.Error = "the page could not be loaded"; return result; }
                    try
                    {
                        var words = new List<(double Top, double Left, string Text)>();
                        bool changed = RegionPass(doc, page, rect, result, orphaned, words);

                        if (changed && !FPDFPage_GenerateContentRaw(page))
                        {
                            result.Error = "the page content could not be regenerated";
                            return result;
                        }

                        result.RemovedText = string.Join(" ", words
                            .OrderByDescending(w => Math.Round(w.Top / 2))
                            .ThenBy(w => w.Left)
                            .Select(w => w.Text.Trim())
                            .Where(t => t.Length > 0));

                        if (!PdfiumSave(doc, destPath))
                        {
                            result.Error = "the edited document could not be written";
                            return result;
                        }
                        result.Ok = true;
                        return result;
                    }
                    finally { FPDF_ClosePageRaw(page); }
                }
                finally
                {
                    // After the save, never before (see RemoveObjectsIntersecting).
                    foreach (var o in orphaned) { try { FPDFPageObj_DestroyRaw(o); } catch { } }
                    FPDF_CloseDocumentRaw(doc);
                }
            }
        }

        private static bool RegionPass(
            IntPtr doc, IntPtr page, PdfRect rect, RegionContentResult result,
            List<IntPtr> orphaned, List<(double Top, double Left, string Text)> words)
        {
            bool changed = false;
            bool canSplit = CanSplitText;

            // ---- Gather. The text page maps every character back to its text object; it is read
            // completely and closed BEFORE anything is removed, because its pointers die with the
            // objects they name.
            var runs = new Dictionary<IntPtr, RunInfo>();
            if (canSplit)
            {
                IntPtr tp = FPDFText_LoadPageRaw(page);
                if (tp != IntPtr.Zero)
                {
                    try
                    {
                        int n = FPDFText_CountCharsRaw(tp);
                        var boxes = new Dictionary<IntPtr, List<RegionRaster.GlyphBox>>();
                        for (int i = 0; i < n; i++)
                        {
                            IntPtr obj = FPDFText_GetTextObjectRaw(tp, i);
                            if (obj == IntPtr.Zero) continue;
                            if (!FPDFText_GetCharBoxRaw(tp, i, out double l, out double r, out double b, out double t)) continue;
                            if (!FPDFText_GetCharOriginRaw(tp, i, out double ox, out double oy)) continue;
                            uint u = FPDFText_GetUnicodeRaw(tp, i);
                            if (!runs.TryGetValue(obj, out var run))
                            {
                                run = new RunInfo { Obj = obj };
                                runs[obj] = run;
                                boxes[obj] = new List<RegionRaster.GlyphBox>();
                            }
                            run.Chars.Add(i);
                            run.Text.Append((char)u);
                            run.Origins.Add((ox, oy));
                            boxes[obj].Add(new RegionRaster.GlyphBox(l, b, r, t));
                        }
                        foreach (var (obj, run) in runs)
                        {
                            run.Inside = RegionRaster.InsideFlags(boxes[obj], rect);
                            run.Action = RegionRaster.Classify(run.Inside);
                            // The rewrite writes the kept characters back as text, so the run's own
                            // reading of itself has to agree with the text page's before it is trusted.
                            if (run.Action == RegionRaster.RunAction.Rewrite)
                            {
                                try { run.LengthOk = ReadTextObject(obj, tp).Length == run.Chars.Count; }
                                catch { run.LengthOk = false; }
                            }
                            if (FPDFPageObj_GetBoundsRaw(obj, out float bl, out _, out _, out float bt))
                            { run.Left = bl; run.Top = bt; }
                        }
                    }
                    finally { FPDFText_ClosePageRaw(tp); }
                }
            }

            // ---- Walk the page objects, deciding. Backwards, so a removal cannot shift an index
            // not yet visited.
            var toRewrite = new List<RunInfo>();
            int count = FPDFPage_CountObjectsRaw(page);
            for (int i = count - 1; i >= 0; i--)
            {
                IntPtr obj = FPDFPage_GetObjectRaw(page, i);
                if (obj == IntPtr.Zero) continue;
                if (!FPDFPageObj_GetBoundsRaw(obj, out float l, out float b, out float r, out float t)) continue;
                if (!rect.Intersects(l, b, r, t)) continue;

                int type = FPDFPageObj_GetTypeRaw(obj);
                bool inside = rect.Contains(l, b, r, t);

                if (type == PageObjText)
                {
                    if (runs.TryGetValue(obj, out var run) && run.Chars.Count > 0)
                    {
                        if (run.Action == RegionRaster.RunAction.Keep) continue;
                        bool trusted = run.Action == RegionRaster.RunAction.RemoveAll || run.LengthOk;
                        if (run.Action == RegionRaster.RunAction.RemoveAll)
                        {
                            AddWords(words, run, takeInside: true);
                            RemoveTopLevel(page, obj, orphaned);
                            result.TextRunsRemoved++;
                            changed = true;
                        }
                        else if (trusted)
                        {
                            AddWords(words, run, takeInside: true);
                            toRewrite.Add(run);
                        }
                        else result.LeftPartlyCovered++;
                        continue;
                    }

                    // No character map (older PDFium, or a run the text page did not report):
                    // whole-run rule on the object's own box.
                    if (inside)
                    {
                        RemoveTopLevel(page, obj, orphaned);
                        result.TextRunsRemoved++;
                        changed = true;
                    }
                    else result.LeftPartlyCovered++;
                    continue;
                }

                if (type == PageObjForm)
                {
                    if (inside)
                    {
                        RemoveTopLevel(page, obj, orphaned);
                        result.ShapesRemoved++;
                        changed = true;
                    }
                    else if (CanEditFormXObjectContent && IsIdentityMatrix(obj))
                    {
                        if (RegionForm(obj, rect, result, orphaned)) changed = true;
                    }
                    else result.LeftPartlyCovered++;
                    continue;
                }

                if (!inside)
                {
                    if (type == PageObjImage || (type == PageObjPath && IsVisiblePath(obj)) || type == PageObjShading)
                        result.LeftPartlyCovered++;
                    continue;
                }

                if (type == PageObjPath && !IsVisiblePath(obj)) continue;   // clipping only: not content, not counted

                if (FPDFPage_RemoveObjectRaw(page, obj))
                {
                    orphaned.Add(obj);
                    if (type == PageObjImage) result.ImagesRemoved++; else result.ShapesRemoved++;
                    changed = true;
                }
            }

            // ---- Rewrite the runs that cross the edge.
            foreach (var run in toRewrite)
            {
                if (!RewriteRun(doc, page, run, orphaned, out string? why))
                {
                    result.Error = why ?? "a text run could not be rewritten";
                    return changed;
                }
                result.TextRunsRewritten++;
                changed = true;
            }
            return changed;
        }

        private static void AddWords(List<(double Top, double Left, string Text)> words, RunInfo run, bool takeInside)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < run.Inside.Length; i++)
                if (run.Inside[i] == takeInside) sb.Append(run.Text[i]);
            if (sb.Length > 0) words.Add((run.Top, run.Left, sb.ToString()));
        }

        private static void RemoveTopLevel(IntPtr page, IntPtr obj, List<IntPtr> orphaned)
        {
            if (FPDFPage_RemoveObjectRaw(page, obj)) orphaned.Add(obj);
        }

        private static bool IsVisiblePath(IntPtr path)
        {
            try
            {
                if (!FPDFPath_GetDrawModeRaw(path, out int fill, out int stroke)) return true;
                return fill != 0 || stroke != 0;
            }
            catch { return true; }
        }

        private static bool IsIdentityMatrix(IntPtr obj)
        {
            try
            {
                if (!FPDFPageObj_GetMatrixRaw(obj, out var m)) return false;
                return Math.Abs(m.A - 1) < 1e-4 && Math.Abs(m.D - 1) < 1e-4
                    && Math.Abs(m.B) < 1e-4 && Math.Abs(m.C) < 1e-4
                    && Math.Abs(m.E) < 1e-4 && Math.Abs(m.F) < 1e-4;
            }
            catch { return false; }
        }

        /// <summary>One level into a form that has no transform: children wholly inside the box go.</summary>
        private static bool RegionForm(IntPtr form, PdfRect rect, RegionContentResult result, List<IntPtr> orphaned)
        {
            bool changed = false;
            int count = FPDFFormObj_CountObjectsRaw(form);
            for (int i = count - 1; i >= 0; i--)
            {
                IntPtr child = FPDFFormObj_GetObjectRaw(form, (uint)i);
                if (child == IntPtr.Zero) continue;
                if (!FPDFPageObj_GetBoundsRaw(child, out float l, out float b, out float r, out float t)) continue;
                if (!rect.Intersects(l, b, r, t)) continue;
                int type = FPDFPageObj_GetTypeRaw(child);

                if (!rect.Contains(l, b, r, t))
                {
                    result.LeftPartlyCovered++;
                    continue;
                }
                if (type == PageObjPath && !IsVisiblePath(child)) continue;
                if (type == PageObjForm) { result.LeftPartlyCovered++; continue; }

                if (FPDFFormObj_RemoveObjectRaw(form, child))
                {
                    orphaned.Add(child);
                    if (type == PageObjText) result.TextRunsRemoved++;
                    else if (type == PageObjImage) result.ImagesRemoved++;
                    else result.ShapesRemoved++;
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>
        /// Writes the characters of a run that are NOT inside the box back as text, and drops the
        /// rest. A leading stretch stays on the original object (so its font, colour and position are
        /// untouched); every other stretch becomes a new text object with the same font, size, matrix
        /// direction, colour and render mode, started at the character's own origin.
        /// </summary>
        private static bool RewriteRun(IntPtr doc, IntPtr page, RunInfo run, List<IntPtr> orphaned, out string? error)
        {
            error = null;
            var spans = RegionRaster.KeptSpans(run.Inside);
            if (spans.Count == 0) return true;

            string full = run.Text.ToString();
            bool reuse = spans[0].Start == 0;

            IntPtr font = FPDFTextObj_GetFontRaw(run.Obj);
            bool haveMatrix = FPDFPageObj_GetMatrixRaw(run.Obj, out var matrix);
            bool haveSize = FPDFTextObj_GetFontSizeRaw(run.Obj, out float size);
            bool haveFill = FPDFPageObj_GetFillColorRaw(run.Obj, out uint fr, out uint fg, out uint fb, out uint fa);
            int mode = 0;
            try { mode = AvailableEditApi.HasFlag(EditApi.TextRenderMode) ? FPDFTextObj_GetTextRenderModeRaw(run.Obj) : 0; }
            catch { mode = 0; }

            for (int s = 0; s < spans.Count; s++)
            {
                string text = full.Substring(spans[s].Start, spans[s].Length);
                if (s == 0 && reuse)
                {
                    if (!FPDFText_SetTextRaw(run.Obj, text)) { error = "a text run could not be shortened"; return false; }
                    continue;
                }

                if (font == IntPtr.Zero || !haveMatrix || !haveSize)
                {
                    error = "a text run's font could not be read";
                    return false;
                }
                IntPtr made = FPDFPageObj_CreateTextObjRaw(doc, font, size);
                if (made == IntPtr.Zero) { error = "a text run could not be rebuilt"; return false; }
                if (!FPDFText_SetTextRaw(made, text))
                {
                    try { FPDFPageObj_DestroyRaw(made); } catch { }
                    error = "a text run could not be rebuilt";
                    return false;
                }
                var origin = run.Origins[spans[s].Start];
                var m = matrix;
                m.E = (float)origin.X;
                m.F = (float)origin.Y;
                if (!FPDFPageObj_SetMatrixRaw(made, ref m))
                {
                    try { FPDFPageObj_DestroyRaw(made); } catch { }
                    error = "a text run could not be placed";
                    return false;
                }
                if (haveFill) FPDFPageObj_SetFillColorRaw(made, fr, fg, fb, fa);
                if (mode != 0) FPDFTextObj_SetTextRenderModeRaw(made, mode);
                FPDFPage_InsertObjectRaw(page, made);   // the page owns it now
            }

            if (!reuse)
            {
                if (FPDFPage_RemoveObjectRaw(page, run.Obj)) orphaned.Add(run.Obj);
            }
            return true;
        }
    }
}
