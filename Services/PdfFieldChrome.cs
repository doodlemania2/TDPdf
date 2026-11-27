using System.Globalization;
using System.Text;
using PdfSharpCore.Pdf;

namespace TDPdf.Services
{
    /// <summary>A colour from a PDF /MK array: 1 component is gray, 3 RGB, 4 CMYK.</summary>
    internal readonly record struct FieldColor(int Components, double A, double B, double C, double D)
    {
        /// <summary>Content-stream operator that selects this colour for fill or stroke.</summary>
        internal string Operator(bool stroke)
        {
            string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            return Components switch
            {
                1 => $"{F(A)} {(stroke ? "G" : "g")}",
                3 => $"{F(A)} {F(B)} {F(C)} {(stroke ? "RG" : "rg")}",
                _ => $"{F(A)} {F(B)} {F(C)} {F(D)} {(stroke ? "K" : "k")}",
            };
        }

        /// <summary>The colour as 0..1 RGB (CMYK by the naive conversion every viewer's fallback uses).</summary>
        internal (double R, double G, double B) ToRgb() => Components switch
        {
            1 => (A, A, A),
            3 => (A, B, C),
            _ => ((1 - A) * (1 - D), (1 - B) * (1 - D), (1 - C) * (1 - D)),
        };

        internal FieldColor Scaled(double f) => this with { A = A * f, B = Components == 1 ? B : B * f, C = Components == 1 ? C : C * f };
    }

    internal enum FieldBorderStyle { Solid, Dashed, Beveled, Inset, Underline }

    /// <summary>
    /// A widget's own look (/MK background and border, /BS or /Border width and style), so the
    /// appearance TDPdf generates for a filled field keeps the box it was designed with. Writing
    /// only the text replaced the whole /AP and the field lost its frame and fill on save.
    /// </summary>
    internal sealed class FieldChrome
    {
        internal FieldColor? Background { get; init; }
        internal FieldColor? BorderColor { get; init; }
        /// <summary>Width actually painted: 0 when no border colour is set.</summary>
        internal double BorderWidth { get; init; }
        internal FieldBorderStyle Style { get; init; }
        internal double[] Dash { get; init; } = { 3 };
        /// <summary>/MK /R, normalised to 0, 90, 180 or 270. Not applied to the text; see remarks on <see cref="Read"/>.</summary>
        internal int Rotation { get; init; }

        /// <summary>
        /// Distance from the field edge to where text starts: border width plus 2, doubled border
        /// for the two 3-D styles, as viewers do.
        /// </summary>
        internal double TextInset =>
            BorderWidth * (Style is FieldBorderStyle.Beveled or FieldBorderStyle.Inset ? 2 : 1) + 2;

        /// <remarks>
        /// /MK /R is read but the generated text is not rotated: the widget rectangle is already in
        /// page space and the frame is symmetric, so only rotated text content is affected.
        /// </remarks>
        internal static FieldChrome Read(PdfDictionary? widget)
        {
            var mk = widget?.Elements.GetDictionary("/MK");
            var bg = ParseColor(mk?.Elements.GetArray("/BG"));
            var bc = ParseColor(mk?.Elements.GetArray("/BC"));

            double width = 1;
            var style = FieldBorderStyle.Solid;
            double[] dash = { 3 };
            var bs = widget?.Elements.GetDictionary("/BS");
            if (bs is not null)
            {
                if (Num(bs.Elements["/W"]) is { } w) width = w;
                style = bs.Elements.GetName("/S") switch
                {
                    "/D" => FieldBorderStyle.Dashed,
                    "/B" => FieldBorderStyle.Beveled,
                    "/I" => FieldBorderStyle.Inset,
                    "/U" => FieldBorderStyle.Underline,
                    _ => FieldBorderStyle.Solid,
                };
                if (bs.Elements.GetArray("/D") is { } d)
                {
                    var vals = new List<double>();
                    for (int i = 0; i < d.Elements.Count; i++)
                        if (Num(d.Elements[i]) is { } v && v > 0) vals.Add(v);
                    if (vals.Count > 0) dash = vals.ToArray();
                }
            }
            else if (widget?.Elements.GetArray("/Border") is { Elements.Count: >= 3 } b)
            {
                if (Num(b.Elements[2]) is { } w) width = w;
                if (b.Elements.Count >= 4 && b.Elements[3] is PdfArray da)
                {
                    var vals = new List<double>();
                    for (int i = 0; i < da.Elements.Count; i++)
                        if (Num(da.Elements[i]) is { } v && v > 0) vals.Add(v);
                    if (vals.Count > 0) { dash = vals.ToArray(); style = FieldBorderStyle.Dashed; }
                }
            }

            int rot = mk?.Elements["/R"] is PdfInteger ri ? ((ri.Value % 360) + 360) % 360 : 0;
            if (rot % 90 != 0) rot = 0;

            return new FieldChrome
            {
                Background = bg,
                BorderColor = bc,
                BorderWidth = bc is null ? 0 : Math.Max(0, Math.Min(width, 20)),
                Style = style,
                Dash = dash,
                Rotation = rot,
            };
        }

        /// <summary>
        /// Content-stream operators that paint the background and the border of a w x h box.
        /// Empty when the widget has neither.
        /// </summary>
        internal string ToContent(double w, double h)
        {
            string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            if (Background is { } bg)
                sb.Append($"q\n{bg.Operator(false)}\n0 0 {F(w)} {F(h)} re f\nQ\n");

            double bw = BorderWidth;
            if (BorderColor is not { } bc || bw <= 0) return sb.ToString();

            sb.Append("q\n").Append(bc.Operator(true)).Append('\n').Append($"{F(bw)} w\n");
            switch (Style)
            {
                case FieldBorderStyle.Underline:
                    sb.Append($"0 {F(bw / 2)} m {F(w)} {F(bw / 2)} l S\n");
                    break;
                case FieldBorderStyle.Dashed:
                    sb.Append("[").Append(string.Join(' ', Dash.Select(F))).Append("] 0 d\n");
                    sb.Append($"{F(bw / 2)} {F(bw / 2)} {F(w - bw)} {F(h - bw)} re S\n");
                    break;
                default:
                    sb.Append($"{F(bw / 2)} {F(bw / 2)} {F(w - bw)} {F(h - bw)} re S\n");
                    break;
            }
            sb.Append("Q\n");

            if (Style is FieldBorderStyle.Beveled or FieldBorderStyle.Inset)
            {
                // The 3-D edge sits just inside the border: the upper-left pair and the lower-right
                // pair of trapezoids, lit from the top left.
                bool bev = Style == FieldBorderStyle.Beveled;
                string light = bev ? "1 g" : "0.5 g";
                var baseRgb = (Background ?? new FieldColor(1, 1, 0, 0, 0)).Scaled(bev ? 0.5 : 0.75);
                string dark = baseRgb.Operator(false);
                double o = bw, i = bw * 2;
                sb.Append($"q\n{light}\n{F(o)} {F(o)} m {F(o)} {F(h - o)} l {F(w - o)} {F(h - o)} l {F(w - i)} {F(h - i)} l {F(i)} {F(h - i)} l {F(i)} {F(i)} l f\n");
                sb.Append($"{dark}\n{F(w - o)} {F(h - o)} m {F(w - o)} {F(o)} l {F(o)} {F(o)} l {F(i)} {F(i)} l {F(w - i)} {F(i)} l {F(w - i)} {F(h - i)} l f\nQ\n");
            }
            return sb.ToString();
        }

        /// <summary>Colour from a /MK array; null for an absent, empty (transparent) or malformed one.</summary>
        internal static FieldColor? ParseColor(PdfArray? arr)
        {
            if (arr is null) return null;
            int n = arr.Elements.Count;
            if (n != 1 && n != 3 && n != 4) return null;
            var v = new double[4];
            for (int i = 0; i < n; i++)
            {
                if (Num(arr.Elements[i]) is not { } d) return null;
                v[i] = Math.Max(0, Math.Min(1, d));
            }
            return new FieldColor(n, v[0], v[1], v[2], v[3]);
        }

        /// <summary>
        /// X of the text origin for /Q alignment (0 left, 1 centre, 2 right): the box is
        /// <paramref name="fieldW"/> wide, text starts no nearer the edge than <paramref name="inset"/>.
        /// </summary>
        internal static double AlignedX(int q, double fieldW, double textW, double inset)
        {
            double x = q switch
            {
                1 => (fieldW - textW) / 2,
                2 => fieldW - inset - textW,
                _ => inset,
            };
            return Math.Max(inset, x);
        }

        private static double? Num(PdfItem? item) => PdfItems.Deref(item) switch
        {
            PdfInteger i => i.Value,
            PdfReal r => r.Value,
            _ => null,
        };
    }
}
