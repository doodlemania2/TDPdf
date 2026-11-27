using System.Globalization;
using System.IO;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;

namespace TDPdf.Services
{
    /// <summary>
    /// Saves a <see cref="StampAnnotation"/> as a real PDF /Stamp annotation with an appearance
    /// stream (3.0), instead of burning it into the page like the other marks.
    /// </summary>
    /// <remarks>
    /// Why a real annotation: a stamp is the one mark whose meaning is "this was stamped", and other
    /// viewers list and show /Stamp annotations as such. The appearance is a form XObject drawn with
    /// PdfSharpCore (so the words are in an embedded font, whatever the text), and its /Matrix carries
    /// the stamp's turn and the page's /Rotate, so it lands where the canvas showed it on any page.
    /// PDFium draws it (the FPDF_ANNOT pass), so Save Flattened, print and redaction, which all work
    /// from PDFium's view of the page, see it too. The opacity is baked into the appearance, because
    /// viewers disagree on whether /CA applies to an annotation that has an appearance.
    /// </remarks>
    internal static class StampWriter
    {
        /// <summary>Where a stamp's appearance sits on the page: the form-to-page matrix and the /Rect.</summary>
        internal readonly record struct Placement(
            double A, double B, double C, double D, double E, double F,
            double Left, double Bottom, double Right, double Top,
            double Width, double Height);

        /// <summary>
        /// Maps the stamp's box (canvas pixels, turned about its centre) into PDF user space.
        /// </summary>
        /// <param name="s">The stamp.</param>
        /// <param name="sx">Canvas pixel to visual point, horizontally (<see cref="PdfPageGeometry.RasterToXGraphics"/>).</param>
        /// <param name="sy">The same, vertically.</param>
        /// <param name="visualToPage">The matrix <see cref="PdfPageGeometry.RasterToXGraphics"/> gave, null for none.</param>
        /// <param name="pageHeight">The height XGraphics flips about (<c>page.Height.Point</c>).</param>
        internal static Placement Locate(StampAnnotation s, double sx, double sy, XMatrix? visualToPage, double pageHeight)
        {
            double wC = s.SourceWidth * s.Scale, hC = s.SourceHeight * s.Scale;
            double w = Math.Max(1, wC * sx), h = Math.Max(1, hC * sy);
            double x0 = s.Position.X * sx, y0 = s.Position.Y * sy;
            double cx = x0 + w / 2, cy = y0 + h / 2;
            double rad = AnnotationFrame.Normalize(s.Rotation) * Math.PI / 180.0;
            double cos = Math.Round(Math.Cos(rad), 12), sin = Math.Round(Math.Sin(rad), 12);

            // Form space is (u right, v up) from the box's lower left. Turn about the centre in the
            // y-down visual frame (positive is clockwise there), then into XGraphics, then flip into user space.
            (double X, double Y) ToPage(double u, double v)
            {
                double vx = x0 + u, vy = y0 + (h - v);
                double dx = vx - cx, dy = vy - cy;
                double rx = cx + dx * cos - dy * sin, ry = cy + dx * sin + dy * cos;
                if (visualToPage is XMatrix m)
                {
                    var p = m.Transform(new XPoint(rx, ry));
                    rx = p.X; ry = p.Y;
                }
                return (rx, pageHeight - ry);
            }

            var o = ToPage(0, 0); var px = ToPage(w, 0); var py = ToPage(0, h); var pxy = ToPage(w, h);
            double minX = Math.Min(Math.Min(o.X, px.X), Math.Min(py.X, pxy.X));
            double maxX = Math.Max(Math.Max(o.X, px.X), Math.Max(py.X, pxy.X));
            double minY = Math.Min(Math.Min(o.Y, px.Y), Math.Min(py.Y, pxy.Y));
            double maxY = Math.Max(Math.Max(o.Y, px.Y), Math.Max(py.Y, pxy.Y));
            return new Placement((px.X - o.X) / w, (px.Y - o.Y) / w, (py.X - o.X) / h, (py.Y - o.Y) / h, o.X, o.Y,
                                 minX, minY, maxX, maxY, w, h);
        }

        /// <summary>
        /// Draws the stamp's appearance and adds the annotation to the page. Returns false (and
        /// leaves the page alone) when the stamp has nothing drawable, such as a picture that will not decode.
        /// </summary>
        internal static bool Write(PdfDocument doc, PdfPage page, StampAnnotation s,
                                   double sx, double sy, XMatrix? visualToPage)
        {
            if (!(s.SourceWidth > 0) || !(s.SourceHeight > 0) || !(s.Scale > 0)) return false;
            var at = Locate(s, sx, sy, visualToPage, page.Height.Point);

            XForm form;
            try
            {
                form = new XForm(doc, new XSize(at.Width, at.Height));
                using (var gfx = XGraphics.FromForm(form))
                {
                    gfx.ScaleTransform(at.Width / s.SourceWidth, at.Height / s.SourceHeight);
                    if (!DrawAppearance(gfx, s)) return false;
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException
                                          or NotSupportedException or IOException)
            {
                return false;   // a picture that will not decode: skip the stamp, never fail the save
            }

            form.DrawingFinished();   // writes the content stream; the opacity wrapper below needs it
            var pdfForm = form.PdfForm;
            // A picture has no colour operators of its own, so the whole appearance is wrapped in an opacity
            // state; text and frame carry the opacity in their own colours (XGraphics resets the alpha state
            // before every coloured object, which would undo a wrapper).
            if (s.Kind == StampKind.Image) ApplyOpacity(doc, pdfForm, s.Opacity);
            var matrix = new PdfArray(doc);
            foreach (double n in new[] { at.A, at.B, at.C, at.D, at.E, at.F }) matrix.Elements.Add(new PdfReal(n));
            pdfForm.Elements["/Matrix"] = matrix;

            var ann = new PdfDictionary(doc);
            ann.Elements["/Type"] = new PdfName("/Annot");
            ann.Elements["/Subtype"] = new PdfName("/Stamp");
            ann.Elements["/Name"] = new PdfName(NameFor(s));
            ann.Elements["/F"] = new PdfInteger(4);   // Print
            ann.Elements["/Contents"] = new PdfString(s.Kind == StampKind.Image ? "Stamp" : s.Label);
            ann.Elements["/NM"] = new PdfString("TDPdf-" + Guid.NewGuid().ToString("N"));
            var rect = new PdfArray(doc);
            foreach (double n in new[] { at.Left, at.Bottom, at.Right, at.Top }) rect.Elements.Add(new PdfReal(n));
            ann.Elements["/Rect"] = rect;
            var ap = new PdfDictionary(doc);
            ap.Elements["/N"] = pdfForm.Reference!;
            ann.Elements["/AP"] = ap;
            doc.Internals.AddObject(ann);

            var annots = page.Elements.GetArray("/Annots");
            if (annots is null)
            {
                annots = new PdfArray(doc);
                page.Elements["/Annots"] = annots;
            }
            annots.Elements.Add(ann.Reference!);
            return true;
        }

        /// <summary>The /Name entry: the standard stamp's id, or the generic names viewers use for custom ones.</summary>
        internal static string NameFor(StampAnnotation s) => s.Kind switch
        {
            StampKind.Standard when s.StampId.Length > 0 => "/" + new string(s.StampId.Where(char.IsLetterOrDigit).ToArray()),
            StampKind.Image => "/TDPdfImage",
            _ => "/TDPdfCustom"
        };

        /// <summary>Draws the stamp upright in its design box (0,0 to SourceWidth,SourceHeight), top-down.</summary>
        private static bool DrawAppearance(XGraphics gfx, StampAnnotation s)
        {
            double w = s.SourceWidth, h = s.SourceHeight;
            if (s.Kind == StampKind.Image)
            {
                if (string.IsNullOrEmpty(s.ImageData)) return false;
                var bytes = Convert.FromBase64String(s.ImageData);
                var img = XImage.FromStream(() => new MemoryStream(bytes));
                gfx.DrawImage(img, 0, 0, w, h);
                return true;
            }

            int alpha = (int)Math.Round(Math.Clamp(s.Opacity, 0.0, 1.0) * 255);
            var color = XColor.FromArgb(alpha, s.ColorR, s.ColorG, s.ColorB);
            double border = 3.5, inset = border / 2 + 1;
            gfx.DrawRoundedRectangle(new XPen(color, border), inset, inset, w - inset * 2, h - inset * 2, 16, 16);

            // The same family the text annotations use, so a stamp and typed text agree on the face.
            // A custom label may hold characters Arial lacks; PickFamily moves to one that has them.
            string family = FontCoverage.PickFamily("Arial", s.Label);
            double size = StampLibrary.DesignFont;
            var font = TdpFontResolver.TryCreate(family, size, XFontStyle.Bold);
            if (font is null) return true;   // the frame alone is still a stamp; never fail the save over a face
            double room = w - 28;
            double measured = gfx.MeasureString(s.Label, font).Width;
            if (measured > room && measured > 0)
            {
                size = Math.Max(6, size * room / measured);
                font = TdpFontResolver.TryCreate(family, size, XFontStyle.Bold) ?? font;
            }
            gfx.DrawString(s.Label, font, new XSolidBrush(color), new XRect(0, 0, w, h), XStringFormats.Center);
            return true;
        }

        /// <summary>
        /// Wraps the appearance's content in a graphics state that sets stroke and fill opacity, so the
        /// whole stamp (frame, words or picture) is drawn at <paramref name="opacity"/>.
        /// </summary>
        private static void ApplyOpacity(PdfDocument doc, PdfFormXObject form, double opacity)
        {
            if (opacity >= 0.999) return;
            var stream = form.Stream;
            if (stream is null) return;
            var res = form.Resources;
            var stroke = doc.ExtGStateTable.GetExtGStateStroke(opacity, false);
            var fill = doc.ExtGStateTable.GetExtGStateNonStroke(opacity, false);
            string gs1 = res.AddExtGState(stroke), gs2 = res.AddExtGState(fill);
            byte[] body = stream.UnfilteredValue;
            string head = string.Format(CultureInfo.InvariantCulture, "q {0} gs {1} gs\n", gs1, gs2);
            var all = new List<byte>(System.Text.Encoding.ASCII.GetBytes(head));
            all.AddRange(body);
            all.AddRange(System.Text.Encoding.ASCII.GetBytes("\nQ\n"));
            // The finished stream may already be flate-compressed; the wrapped one is written plain.
            form.Elements.Remove("/Filter");
            form.Elements.Remove("/DecodeParms");
            stream.Value = all.ToArray();
        }
    }
}
