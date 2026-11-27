using System.Globalization;
using System.IO;
using System.Text;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.Advanced;
using PdfSharpCore.Pdf.IO;

namespace TDPdf.Services
{
    /// <summary>
    /// Rasterize a region (#233): replaces a rectangle of one page with a picture of what it shows,
    /// at a chosen resolution, and removes the text and graphics under it so they cannot be
    /// searched, selected or copied from beneath the picture.
    /// </summary>
    /// <remarks>
    /// This is NOT redaction, and the dialog says so. Redaction destroys content and then proves it
    /// is gone from the whole file; this keeps what the box shows (as pixels) and takes out the
    /// objects only so they do not sit behind the picture. Annotations, links and form fields are
    /// marks, not page content: they are neither rendered into the picture nor removed.
    ///
    /// Order matters. The region is rendered FIRST, from the page as it is, because removing the
    /// objects first would render an empty box.
    /// </remarks>
    internal static class PdfRegionRasterizer
    {
        internal sealed class Request
        {
            internal int PageIndex { get; init; }

            /// <summary>The dragged box, in PDF user space.</summary>
            internal PdfiumInterop.PdfRect Rect { get; init; }

            internal int Dpi { get; init; } = RegionRaster.DefaultDpi;

            /// <summary>
            /// Remove the text and graphics under the box. False only for a page PDFium cannot rewrite
            /// without damaging other content (see <see cref="PdfContentInspector"/>), where the user
            /// agreed to put the picture over the content and leave it in place.
            /// </summary>
            internal bool RemoveContent { get; init; } = true;
        }

        internal sealed class Result
        {
            internal bool Ok { get; set; }
            internal string? Error { get; set; }
            internal RegionRaster.RegionPlan? Plan { get; set; }
            internal PdfiumInterop.RegionContentResult? Removal { get; set; }

            /// <summary>The page is tagged and the picture got an /Alt description.</summary>
            internal bool AltAdded { get; set; }
        }

        internal static Result Apply(string srcPath, string destPath, Request request)
        {
            var result = new Result();
            string stripped = Path.Combine(Path.GetDirectoryName(destPath) ?? Path.GetTempPath(),
                $"tdpdf_region_{Guid.NewGuid():N}.pdf");
            try
            {
                // 1. Geometry, from the page's own boxes and /Rotate.
                PdfPageGeometry.PageBox box;
                int rotation;
                using (var geo = PdfReader.Open(srcPath, PdfDocumentOpenMode.ReadOnly))
                {
                    if (request.PageIndex < 0 || request.PageIndex >= geo.PageCount)
                    { result.Error = "that page is not in the document"; return result; }
                    var page = geo.Pages[request.PageIndex];
                    box = PdfPageGeometry.VisibleBox(page);
                    rotation = PdfPageGeometry.Rotation(page);
                }

                var plan = RegionRaster.Plan(box, rotation, request.Rect, request.Dpi);
                if (plan is null) { result.Error = "the box is not on the page"; return result; }
                result.Plan = plan;
                if (plan.Pixels > RegionRaster.MaxPixels)
                {
                    result.Error = "that area is too large at this resolution; choose a lower resolution or a smaller area";
                    return result;
                }

                // 2. Render first.
                byte[]? bgra = PdfiumInterop.RenderRegionContent(srcPath, request.PageIndex, plan);
                if (bgra is null || bgra.Length < plan.Width * plan.Height * 4)
                { result.Error = "the area could not be rendered"; return result; }

                // 3. Remove what is under the box (or just copy, when the page cannot be rewritten).
                if (request.RemoveContent)
                {
                    var removal = PdfiumInterop.RemoveRegionContent(srcPath, stripped, request.PageIndex, plan.SnappedRect);
                    result.Removal = removal;
                    if (!removal.Ok)
                    { result.Error = removal.Error ?? "the content under the box could not be removed"; return result; }
                }
                else
                {
                    File.Copy(srcPath, stripped, overwrite: true);
                    result.Removal = new PdfiumInterop.RegionContentResult { Ok = true };
                }

                // 4. Put the picture over the box.
                using (var doc = PdfReader.Open(stripped, PdfDocumentOpenMode.Modify))
                {
                    var page = doc.Pages[request.PageIndex];
                    result.AltAdded = Place(doc, page, bgra, plan, rotation, result.Removal.RemovedText);
                    doc.Save(destPath);
                }
                result.Ok = File.Exists(destPath);
                if (!result.Ok) result.Error = "the result could not be written";
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
            finally
            {
                try { if (File.Exists(stripped)) File.Delete(stripped); } catch { /* temp file; the sweep gets it */ }
            }
        }

        /// <summary>
        /// Draws the region's picture on top of the page's existing content, over exactly
        /// <see cref="RegionRaster.RegionPlan.SnappedRect"/>. Returns true when the picture was given
        /// an alternate description (tagged document).
        /// </summary>
        private static bool Place(PdfDocument doc, PdfPage page, byte[] bgra,
                                  RegionRaster.RegionPlan plan, int rotation, string removedText)
        {
            var image = BuildImage(doc, bgra, plan.Width, plan.Height);
            string name = "/TdpRgn" + Guid.NewGuid().ToString("N").Substring(0, 8);

            var resources = OwnResources(doc, page);
            var xobjects = resources.Elements.GetDictionary("/XObject");
            if (xobjects is null)
            {
                xobjects = new PdfDictionary(doc);
                resources.Elements["/XObject"] = xobjects;
            }
            xobjects.Elements[name] = image.Reference!;

            // Marked content, on a tagged document, so the picture is a Figure with the words it
            // replaced as its description.
            string open = "", close = "";
            bool alt = false;
            if (PdfTaggedFigure.IsTagged(doc))
            {
                string text = RegionRaster.BuildAlt(removedText);
                if (PdfTaggedFigure.TryAdd(doc, page, text, out int mcid))
                    open = $"/Figure <</MCID {mcid.ToString(CultureInfo.InvariantCulture)}>> BDC\n";
                else
                    open = $"/Figure <</Alt {PdfTaggedFigure.AltHexString(text)}>> BDC\n";
                close = "EMC\n";
                alt = true;
            }

            string ops = open + "q\n" + RegionRaster.CmOperator(RegionRaster.PlacementMatrix(plan.SnappedRect, rotation))
                         + "\n" + name + " Do\nQ\n" + close;

            // Existing content is bracketed in q / Q so whatever graphics state it leaves behind
            // cannot move or tint the picture.
            var contents = new PdfArray(doc);
            contents.Elements.Add(NewStream(doc, "q\n"));
            var current = page.Elements["/Contents"];
            var resolved = PdfItems.Deref(current);
            if (resolved is PdfArray existing)
            {
                foreach (var item in existing.Elements) contents.Elements.Add(item);
            }
            else if (current is PdfReference single)
            {
                contents.Elements.Add(single);
            }
            else if (current is PdfDictionary direct && direct.Reference is not null)
            {
                contents.Elements.Add(direct.Reference);
            }
            contents.Elements.Add(NewStream(doc, "\nQ\n"));
            contents.Elements.Add(NewStream(doc, ops));
            page.Elements["/Contents"] = contents;
            return alt;
        }

        private static PdfReference NewStream(PdfDocument doc, string text)
        {
            var d = new PdfDictionary(doc);
            d.CreateStream(Encoding.ASCII.GetBytes(text));
            doc.Internals.AddObject(d);
            return d.Reference!;
        }

        /// <summary>The page's own /Resources, copying an inherited one down so it can be extended.</summary>
        private static PdfDictionary OwnResources(PdfDocument doc, PdfPage page)
        {
            var own = page.Elements.GetDictionary("/Resources");
            if (own is not null) return own;

            PdfDictionary? inherited = null;
            var node = PdfItems.ParentOf(page);
            for (int depth = 0; node is not null && depth < 32; depth++)
            {
                inherited = node.Elements.GetDictionary("/Resources");
                if (inherited is not null) break;
                node = PdfItems.ParentOf(node);
            }

            var copy = new PdfDictionary(doc);
            if (inherited is not null)
                foreach (string key in inherited.Elements.Keys.ToList())
                    copy.Elements[key] = inherited.Elements[key]!;
            page.Elements["/Resources"] = copy;
            return copy;
        }

        /// <summary>
        /// The picture as an image XObject: 1 bit per pixel when it really is black and white (a
        /// scan of text), otherwise 8-bit RGB deflated with PNG "Up" prediction, which is what makes
        /// flat colour and text compress well. Lossless either way.
        /// </summary>
        private static PdfDictionary BuildImage(PdfDocument doc, byte[] bgra, int width, int height)
        {
            var image = new PdfDictionary(doc);
            image.Elements["/Type"] = new PdfName("/XObject");
            image.Elements["/Subtype"] = new PdfName("/Image");
            image.Elements["/Width"] = new PdfInteger(width);
            image.Elements["/Height"] = new PdfInteger(height);
            image.Elements["/Filter"] = new PdfName("/FlateDecode");

            var bitonal = PdfPageImageEncoder.TryEncodeBitonal(bgra, width, height);
            if (bitonal is not null)
            {
                image.Elements["/ColorSpace"] = new PdfName("/DeviceGray");
                image.Elements["/BitsPerComponent"] = new PdfInteger(1);
                image.CreateStream(bitonal.Bytes);
            }
            else
            {
                image.Elements["/ColorSpace"] = new PdfName("/DeviceRGB");
                image.Elements["/BitsPerComponent"] = new PdfInteger(8);
                var parms = new PdfDictionary(doc);
                parms.Elements["/Predictor"] = new PdfInteger(15);
                parms.Elements["/Colors"] = new PdfInteger(3);
                parms.Elements["/BitsPerComponent"] = new PdfInteger(8);
                parms.Elements["/Columns"] = new PdfInteger(width);
                image.Elements["/DecodeParms"] = parms;
                image.CreateStream(PdfPageImageEncoder.Deflate(PackRgbUp(bgra, width, height)));
            }
            doc.Internals.AddObject(image);
            return image;
        }

        /// <summary>BGRA to RGB, one PNG filter-2 ("Up") row at a time.</summary>
        internal static byte[] PackRgbUp(byte[] bgra, int width, int height)
        {
            int rowBytes = width * 3;
            var packed = new byte[checked((rowBytes + 1) * height)];
            var prev = new byte[rowBytes];
            var cur = new byte[rowBytes];
            for (int y = 0; y < height; y++)
            {
                long at = (long)y * width * 4;
                for (int x = 0, o = 0; x < width; x++, at += 4, o += 3)
                {
                    cur[o] = bgra[at + 2];
                    cur[o + 1] = bgra[at + 1];
                    cur[o + 2] = bgra[at];
                }
                int row = y * (rowBytes + 1);
                packed[row] = 2;
                for (int i = 0; i < rowBytes; i++) packed[row + 1 + i] = (byte)(cur[i] - prev[i]);
                (prev, cur) = (cur, prev);
            }
            return packed;
        }
    }
}
