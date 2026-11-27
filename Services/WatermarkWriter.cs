using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace TDPdf.Services
{
    /// <summary>What <see cref="WatermarkWriter.Apply"/> did.</summary>
    /// <param name="Watermarks">Pages that got a watermark.</param>
    /// <param name="Numbered">Pages that got a page number.</param>
    /// <param name="Notice">Something the person should be told (a font that is not installed), or null.</param>
    internal sealed record WatermarkResult(int Watermarks, int Numbered, string? Notice);

    /// <summary>
    /// Writes watermarks and page numbers into a document's pages, and finds and removes exactly what
    /// it wrote (3.0).
    /// </summary>
    /// <remarks>
    /// Each mark is a form XObject drawn with PdfSharpCore (so text is in an embedded font) placed by
    /// its own small content stream, which is a marked-content <c>/Artifact</c> block of type
    /// Pagination (subtype Watermark, Header or Footer). Artifacts are what PDF calls content that is
    /// not part of the document's meaning, so screen readers skip them; the block also carries a
    /// <c>/TdpWmKind</c> entry that only TDPdf writes, and that is how Remove Watermark finds it again
    /// without guessing at anyone else's content. Nothing of the page's own content is rewritten: a
    /// watermark behind the page is a stream added in front of it, one over the page is added after a
    /// <c>q</c> / <c>Q</c> pair that keeps the page's own graphics state from leaking into it, and the
    /// removal takes those streams, the page-resource entries and the pair away again.
    /// </remarks>
    internal static class WatermarkWriter
    {
        /// <summary>The key only TDPdf writes into an artifact's property list.</summary>
        internal const string KindKey = "/TdpWmKind";

        /// <summary>Every resource name this writes starts with this, so a removal can tell them apart.</summary>
        internal const string ResourcePrefix = "TdpWm";

        private const string WrapComment = "%TdpWmWrap";

        /// <summary>
        /// Marks the pages of <paramref name="spec"/>.<see cref="WatermarkSpec.Pages"/>.
        /// </summary>
        /// <param name="doc">The document; changed in place.</param>
        /// <param name="spec">What to draw and where.</param>
        /// <param name="fontInstalled">Whether a font family is installed; decides the notice. Defaults to the app's own font index.</param>
        /// <param name="numbering">The number and total a page's number shows. Defaults to the page's place in <paramref name="doc"/>; a preview of one page passes the real ones.</param>
        internal static WatermarkResult Apply(PdfDocument doc, WatermarkSpec spec,
                                              Func<string, bool>? fontInstalled = null,
                                              Func<int, (int Number, int Total)>? numbering = null)
        {
            fontInstalled ??= TdpFontResolver.HasFamily;
            numbering ??= i => (i + 1, doc.PageCount);
            string id = Guid.NewGuid().ToString("N")[..6];
            int marks = 0, numbered = 0;
            string? notice = null;
            bool drawsText = (spec.HasWatermark && spec.Kind == WatermarkKind.Text && !string.IsNullOrWhiteSpace(spec.Text)) || spec.PageNumbers;
            if (drawsText && !string.IsNullOrWhiteSpace(spec.FontFamily) && !fontInstalled(spec.FontFamily))
                notice = $"The font \"{spec.FontFamily}\" is not installed on this computer, so it is drawn in a stand-in font. " +
                         "The name is kept as you entered it.";

            foreach (int i in spec.Pages.Distinct().OrderBy(x => x))
            {
                if (i < 0 || i >= doc.PageCount) continue;
                var page = doc.Pages[i];
                var (dw, dh) = PdfPageGeometry.DisplaySize(page);
                if (!(dw >= 1) || !(dh >= 1)) continue;

                if (spec.HasWatermark && TryBuildWatermark(doc, spec, dw, dh, out var wmForm))
                {
                    AddBlock(doc, page, wmForm!, FormToPage(page, dw, dh), $"{ResourcePrefix}{id}p{i}", "/Watermark", "watermark", id,
                             prepend: spec.Layer == WatermarkLayer.Behind);
                    marks++;
                }
                if (spec.PageNumbers)
                {
                    var (n, total) = numbering(i);
                    string text = WatermarkMath.FormatPageNumber(spec.PageNumberFormat, n, total);
                    if (TryBuildNumber(doc, spec, text, dw, dh, out var numForm))
                    {
                        bool top = spec.NumberPlace is PageNumberPlace.TopLeft or PageNumberPlace.TopCenter or PageNumberPlace.TopRight;
                        AddBlock(doc, page, numForm!, FormToPage(page, dw, dh), $"{ResourcePrefix}{id}n{i}", top ? "/Header" : "/Footer", "numbers", id,
                                 prepend: false);
                        numbered++;
                    }
                }
            }
            return new WatermarkResult(marks, numbered, notice);
        }

        // ---- drawing ------------------------------------------------------------------------------

        private static bool TryBuildWatermark(PdfDocument doc, WatermarkSpec spec, double dw, double dh, out PdfDictionary? wrapped)
        {
            wrapped = null;
            if (spec.Kind == WatermarkKind.Picture)
            {
                if (spec.ImageBytes is not { Length: > 0 } bytes) return false;
                var (w, h) = WatermarkMath.PictureSize(spec);
                if (!(w >= 1) || !(h >= 1)) return false;
                try
                {
                    var img = XImage.FromStream(() => new MemoryStream(bytes));
                    var f = new XForm(doc, new XSize(dw, dh));
                    using (var gfx = XGraphics.FromForm(f))
                    {
                        var (cx, cy, angle) = WatermarkMath.Place(spec, dw, dh, w, h);
                        gfx.TranslateTransform(cx, cy);
                        gfx.RotateTransform(-angle);
                        gfx.DrawImage(img, -w / 2, -h / 2, w, h);
                    }
                    f.DrawingFinished();
                    double opacity = WatermarkMath.ClampOpacityPercent(spec.Opacity * 100) / 100;
                    wrapped = opacity < 0.999 ? StampWriter.WrapWithOpacity(doc, f.PdfForm, dw, dh, opacity) : f.PdfForm;
                    return true;
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException
                                              or NotSupportedException or IOException)
                {
                    return false;   // a picture that will not decode: nothing to draw
                }
            }

            string text = Regex.Replace(spec.Text ?? "", @"\s+", " ").Trim();
            if (text.Length == 0) return false;
            string family = FontCoverage.PickFamily(spec.FontFamily, text);
            var font = TdpFontResolver.TryCreate(family, WatermarkMath.ClampFontSize(spec.FontSize),
                                                 spec.Bold ? XFontStyle.Bold : XFontStyle.Regular);
            if (font is null) return false;
            int alpha = (int)Math.Round(WatermarkMath.ClampOpacityPercent(spec.Opacity * 100) / 100 * 255);
            var brush = new XSolidBrush(XColor.FromArgb(alpha, spec.ColorR, spec.ColorG, spec.ColorB));
            var fm = new XForm(doc, new XSize(dw, dh));
            using (var gfx = XGraphics.FromForm(fm))
            {
                var size = gfx.MeasureString(text, font);
                var (cx, cy, angle) = WatermarkMath.Place(spec, dw, dh, size.Width, size.Height);
                gfx.TranslateTransform(cx, cy);
                gfx.RotateTransform(-angle);
                gfx.DrawString(text, font, brush, new XRect(-size.Width / 2, -size.Height / 2, size.Width, size.Height), XStringFormats.Center);
            }
            fm.DrawingFinished();
            fm.PdfForm.Elements["/TdpWmFont"] = new PdfString(spec.FontFamily);   // the name as chosen, installed or not
            wrapped = fm.PdfForm;
            return true;
        }

        private static bool TryBuildNumber(PdfDocument doc, WatermarkSpec spec, string text, double dw, double dh, out PdfDictionary? wrapped)
        {
            wrapped = null;
            if (string.IsNullOrEmpty(text)) return false;
            string family = FontCoverage.PickFamily(string.IsNullOrWhiteSpace(spec.FontFamily) ? "Arial" : spec.FontFamily, text);
            var font = TdpFontResolver.TryCreate(family, WatermarkMath.ClampNumberFontSize(spec.NumberFontSize), XFontStyle.Regular);
            if (font is null) return false;
            var fm = new XForm(doc, new XSize(dw, dh));
            using (var gfx = XGraphics.FromForm(fm))
            {
                var size = gfx.MeasureString(text, font);
                var (x, y) = WatermarkMath.NumberAnchor(spec.NumberPlace, dw, dh, size.Width, size.Height);
                gfx.DrawString(text, font, XBrushes.Black, new XRect(x, y, size.Width, size.Height), XStringFormats.TopLeft);
            }
            fm.DrawingFinished();
            wrapped = fm.PdfForm;
            return true;
        }

        /// <summary>
        /// The matrix that carries the form's own space (origin at its lower left, y up, one unit a point)
        /// onto the page as it is shown: through the page's /Rotate, its visible box, and into user space.
        /// </summary>
        internal static double[] FormToPage(PdfPage page, double w, double h)
        {
            var (_, _, visualToPage) = PdfPageGeometry.RasterToXGraphics(page, w, h);
            double pageHeight = page.Height.Point;
            (double X, double Y) ToPage(double u, double v)
            {
                double vx = u, vy = h - v;
                if (visualToPage is XMatrix m)
                {
                    var p = m.Transform(new XPoint(vx, vy));
                    vx = p.X; vy = p.Y;
                }
                return (vx, pageHeight - vy);
            }
            var o = ToPage(0, 0); var px = ToPage(1, 0); var py = ToPage(0, 1);
            return new[] { px.X - o.X, px.Y - o.Y, py.X - o.X, py.Y - o.Y, o.X, o.Y };
        }

        // ---- content ------------------------------------------------------------------------------

        private static void AddBlock(PdfDocument doc, PdfPage page, PdfDictionary pdfForm, double[] m, string resourceName,
                                     string subtype, string kind, string id, bool prepend)
        {
            var res = page.Elements.GetDictionary("/Resources");
            if (res is null)
            {
                res = new PdfDictionary(doc);
                page.Elements["/Resources"] = res;
            }
            var xo = res.Elements.GetDictionary("/XObject");
            if (xo is null)
            {
                xo = new PdfDictionary(doc);
                res.Elements["/XObject"] = xo;
            }
            xo.Elements["/" + resourceName] = pdfForm.Reference!;

            string block = string.Create(CultureInfo.InvariantCulture,
                $"/Artifact <</Type /Pagination /Subtype {subtype} {KindKey} ({kind}) /TdpWmId ({id})>> BDC\n" +
                $"q {N(m[0])} {N(m[1])} {N(m[2])} {N(m[3])} {N(m[4])} {N(m[5])} cm /{resourceName} Do Q\nEMC\n");

            var contents = page.Contents;
            if (!prepend && contents.Elements.Count > 0 && !IsWrapped(contents))
            {
                AddStream(contents, "q " + WrapComment + "\n", prepend: true);
                AddStream(contents, "Q " + WrapComment + "\n", prepend: false);
            }
            AddStream(contents, block, prepend);
        }

        private static void AddStream(PdfSharpCore.Pdf.Advanced.PdfContents contents, string text, bool prepend)
        {
            var c = prepend ? contents.PrependContent() : contents.AppendContent();
            c.CreateStream(Encoding.Latin1.GetBytes(text));
        }

        private static bool IsWrapped(PdfSharpCore.Pdf.Advanced.PdfContents contents) =>
            contents.Elements.Count > 0 &&
            PdfItems.Deref(contents.Elements[0]) is PdfDictionary first && first.Stream is { } s &&
            Encoding.Latin1.GetString(s.UnfilteredValue).Contains(WrapComment, StringComparison.Ordinal);

        private static string N(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

        // ---- finding and removing ----------------------------------------------------------------

        private static readonly Regex BlockStart = new(
            @"/Artifact\s*<<(?:(?!>>).)*?/TdpWmKind\s*\((?<kind>[a-z]+)\)(?:(?!>>).)*>>\s*BDC",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        private static readonly Regex MarkedToken = new(@"(?<![A-Za-z])(BDC|BMC|EMC)(?![A-Za-z])", RegexOptions.CultureInvariant);

        private static readonly Regex WrapLine = new(@"^[ \t]*[qQ][ \t]*" + WrapComment + @"[ \t]*\r?\n?", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex DoName = new(@"/(" + ResourcePrefix + @"[A-Za-z0-9]+)\s+Do", RegexOptions.CultureInvariant);

        /// <summary>How many watermark and page-number blocks TDPdf wrote that are still in <paramref name="doc"/>.</summary>
        internal static int Count(PdfDocument doc)
        {
            int count = 0;
            foreach (var page in doc.Pages)
                foreach (var text in StreamTexts(page))
                    count += BlockStart.Matches(text).Count;
            return count;
        }

        /// <summary>
        /// Takes out every block <see cref="Apply"/> wrote, its page-resource entry, and the
        /// <c>q</c> / <c>Q</c> pair that wrapped the page's content, leaving everything else as it was.
        /// Returns the number of blocks removed.
        /// </summary>
        internal static int Remove(PdfDocument doc)
        {
            int removed = 0;
            foreach (var page in doc.Pages)
            {
                var contents = page.Contents;
                var names = new HashSet<string>();
                for (int k = contents.Elements.Count - 1; k >= 0; k--)
                {
                    if (PdfItems.Deref(contents.Elements[k]) is not PdfDictionary stream || stream.Stream is not { } s) continue;
                    string text = Encoding.Latin1.GetString(s.UnfilteredValue);
                    if (!text.Contains(ResourcePrefix, StringComparison.Ordinal)) continue;
                    string stripped = StripBlocks(text, names, out int n);
                    if (n == 0 && !WrapLine.IsMatch(text)) continue;
                    removed += n;
                    stripped = WrapLine.Replace(stripped, "");
                    if (string.IsNullOrWhiteSpace(stripped))
                        contents.Elements.RemoveAt(k);
                    else
                    {
                        stream.Elements.Remove("/Filter");
                        stream.Elements.Remove("/DecodeParms");
                        s.Value = Encoding.Latin1.GetBytes(stripped);
                    }
                }
                if (names.Count > 0 && page.Elements.GetDictionary("/Resources") is { } res
                    && res.Elements.GetDictionary("/XObject") is { } xo)
                    foreach (var name in names) xo.Elements.Remove("/" + name);
            }
            return removed;
        }

        private static IEnumerable<string> StreamTexts(PdfPage page)
        {
            foreach (var item in page.Contents.Elements)
                if (PdfItems.Deref(item) is PdfDictionary d && d.Stream is { } s)
                    yield return Encoding.Latin1.GetString(s.UnfilteredValue);
        }

        /// <summary>Cuts each TDPdf block (its BDC to the EMC that closes it) out of <paramref name="text"/>.</summary>
        private static string StripBlocks(string text, HashSet<string> names, out int removed)
        {
            removed = 0;
            var sb = new StringBuilder();
            int pos = 0;
            while (true)
            {
                var m = BlockStart.Match(text, pos);
                if (!m.Success) break;
                int depth = 1, end = -1;
                foreach (Match t in MarkedToken.Matches(text, m.Index + m.Length))
                {
                    depth += t.Value == "EMC" ? -1 : 1;
                    if (depth == 0) { end = t.Index + t.Length; break; }
                }
                if (end < 0) break;   // an unclosed block is somebody else's damage; leave the rest alone
                foreach (Match d in DoName.Matches(text.Substring(m.Index, end - m.Index))) names.Add(d.Groups[1].Value);
                sb.Append(text, pos, m.Index - pos);
                while (end < text.Length && (text[end] == '\r' || text[end] == '\n')) end++;
                pos = end;
                removed++;
            }
            sb.Append(text, pos, text.Length - pos);
            return sb.ToString();
        }
    }
}
