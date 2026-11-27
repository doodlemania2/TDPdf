using System.Globalization;

namespace TDPdf.Services
{
    /// <summary>
    /// Pure image and ink helpers for the signature editor, on raw straight-alpha BGRA buffers
    /// (4 bytes a pixel, rows top to bottom, no padding), so tests/PdfCore can run them with no WPF.
    /// WPF only moves bitmaps in and out of these buffers.
    /// </summary>
    internal static class SignatureInk
    {
        /// <summary>Pen width, in signature-canvas units, of every signature drawn before the editor existed.</summary>
        internal const double DefaultPenWidth = 2;
        internal const double MinPenWidth = 0.5, MaxPenWidth = 12;

        /// <summary>The longest side an imported photo is processed at.</summary>
        internal const int MaxProcessSide = 2000;

        // ---------------------------------------------------------------- colour and width

        /// <summary>Parses "#RRGGBB" (or "RRGGBB"). False for anything else.</summary>
        internal static bool TryParseColor(string? hex, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            string s = hex.Trim();
            if (s.StartsWith('#')) s = s[1..];
            if (s.Length != 6) return false;
            return byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
                && byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
                && byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
        }

        internal static string FormatColor(byte r, byte g, byte b) =>
            "#" + r.ToString("X2", CultureInfo.InvariantCulture) + g.ToString("X2", CultureInfo.InvariantCulture)
                + b.ToString("X2", CultureInfo.InvariantCulture);

        /// <summary>The stored ink colour, or black when it is missing or unreadable (every older signature).</summary>
        internal static (byte R, byte G, byte B) ResolveColor(string? hex) =>
            TryParseColor(hex, out byte r, out byte g, out byte b) ? (r, g, b) : ((byte)0, (byte)0, (byte)0);

        /// <summary>The stored pen width, or the old fixed 2 when it is missing, not a number or out of range.</summary>
        internal static double ResolvePenWidth(double? width) =>
            width is { } w && double.IsFinite(w) && w > 0 ? Math.Clamp(w, MinPenWidth, MaxPenWidth) : DefaultPenWidth;

        // ---------------------------------------------------------------- date line

        /// <summary>The date line text: yyyy-MM-dd, whatever the user's regional settings.</summary>
        internal static string FormatDate(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>Height, in signature units, of the strip a date line adds under a signature of this height.</summary>
        internal static double DateStripFor(double signatureHeight) =>
            double.IsFinite(signatureHeight) && signatureHeight > 0 ? Math.Clamp(signatureHeight * 0.2, 14, 60) : 28;

        /// <summary>The date strip a signature really has: its stored strip when it has date text and the strip fits inside the box, else 0.</summary>
        internal static double EffectiveDateStrip(string? dateText, double strip, double sourceHeight) =>
            !string.IsNullOrEmpty(dateText) && double.IsFinite(strip) && strip > 0 && double.IsFinite(sourceHeight) && strip < sourceHeight
                ? strip : 0;

        /// <summary>Font size, in signature units, for a date line in a strip of this height and box of this width.</summary>
        internal static double DateFontSize(double strip, double boxWidth)
        {
            double size = strip * 0.62;
            // The text is ten characters; keep it inside a narrow box.
            if (double.IsFinite(boxWidth) && boxWidth > 0) size = Math.Min(size, boxWidth / 6.5);
            return Math.Max(size, 1);
        }

        // ---------------------------------------------------------------- pixel operations

        /// <summary>
        /// Turns luminance into alpha: pixels darker than <paramref name="threshold"/> (1 to 255) stay as ink,
        /// lighter ones (paper) become transparent. A short ramp keeps the edge from stair-stepping.
        /// A threshold of 0 or less changes nothing. Existing alpha is kept, never raised.
        /// </summary>
        internal static void ThresholdAlpha(byte[] px, int w, int h, int threshold)
        {
            if (threshold <= 0) return;
            threshold = Math.Min(threshold, 255);
            const int ramp = 24;
            int n = Math.Min(w * h, px.Length / 4);
            for (int i = 0; i < n; i++)
            {
                int o = i * 4;
                // Rec. 601 luma, integer.
                int lum = (px[o + 2] * 299 + px[o + 1] * 587 + px[o] * 114) / 1000;
                int keep = Math.Clamp((threshold - lum) * 255 / ramp, 0, 255);
                px[o + 3] = (byte)(px[o + 3] * keep / 255);
            }
        }

        /// <summary>Sets every pixel's colour, leaving alpha alone.</summary>
        internal static void Recolor(byte[] px, int w, int h, byte r, byte g, byte b)
        {
            int n = Math.Min(w * h, px.Length / 4);
            for (int i = 0; i < n; i++)
            {
                int o = i * 4;
                px[o] = b; px[o + 1] = g; px[o + 2] = r;
            }
        }

        /// <summary>Radius in pixels for a feather strength of 0 to 10 on an image this big; 0 for no feather.</summary>
        internal static int FeatherRadius(int strength, int w, int h)
        {
            if (strength <= 0) return 0;
            int side = Math.Max(w, h);
            return Math.Max(1, (int)Math.Round(Math.Min(strength, 10) * side / 500.0));
        }

        /// <summary>
        /// Softens the edge outwards: the ink grows a faint halo of the given radius. Alpha never drops
        /// below what it was, so the core of a thin line is as solid as before; colour in the halo is
        /// taken from the ink around it, so no fringe of paper colour appears.
        /// </summary>
        internal static void Feather(byte[] px, int w, int h, int radius)
        {
            if (radius <= 0 || w <= 0 || h <= 0) return;
            int n = w * h;
            var a = new float[n];
            var pr = new float[n];
            var pg = new float[n];
            var pb = new float[n];
            for (int i = 0; i < n; i++)
            {
                int o = i * 4;
                float al = px[o + 3] / 255f;
                a[i] = al;
                pr[i] = px[o + 2] * al; pg[i] = px[o + 1] * al; pb[i] = px[o] * al;
            }
            var ba = (float[])a.Clone();
            var br = (float[])pr.Clone();
            var bg = (float[])pg.Clone();
            var bb = (float[])pb.Clone();
            // Two box passes approximate a gaussian.
            foreach (var ch in new[] { ba, br, bg, bb })
            {
                BoxBlur(ch, w, h, radius);
                BoxBlur(ch, w, h, radius);
            }
            for (int i = 0; i < n; i++)
            {
                int o = i * 4;
                float orig = a[i];
                float soft = Math.Min(1f, ba[i] * 1.6f);   // the halo fades out before the blur's own tail
                if (soft <= orig || ba[i] <= 1e-6f) continue;
                px[o + 3] = (byte)Math.Clamp((int)Math.Round(soft * 255f), 0, 255);
                px[o + 2] = (byte)Math.Clamp((int)Math.Round(br[i] / ba[i]), 0, 255);
                px[o + 1] = (byte)Math.Clamp((int)Math.Round(bg[i] / ba[i]), 0, 255);
                px[o] = (byte)Math.Clamp((int)Math.Round(bb[i] / ba[i]), 0, 255);
            }
        }

        private static void BoxBlur(float[] ch, int w, int h, int r)
        {
            var tmp = new float[Math.Max(w, h)];
            // Horizontal.
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                float sum = 0;
                for (int x = -r; x <= r; x++) sum += ch[row + Math.Clamp(x, 0, w - 1)];
                float inv = 1f / (2 * r + 1);
                for (int x = 0; x < w; x++)
                {
                    tmp[x] = sum * inv;
                    sum += ch[row + Math.Min(x + r + 1, w - 1)] - ch[row + Math.Max(x - r, 0)];
                }
                Array.Copy(tmp, 0, ch, row, w);
            }
            // Vertical.
            for (int x = 0; x < w; x++)
            {
                float sum = 0;
                for (int y = -r; y <= r; y++) sum += ch[Math.Clamp(y, 0, h - 1) * w + x];
                float inv = 1f / (2 * r + 1);
                for (int y = 0; y < h; y++)
                {
                    tmp[y] = sum * inv;
                    sum += ch[Math.Min(y + r + 1, h - 1) * w + x] - ch[Math.Max(y - r, 0) * w + x];
                }
                for (int y = 0; y < h; y++) ch[y * w + x] = tmp[y];
            }
        }

        /// <summary>
        /// The editor's whole pipeline on a copy of the source: threshold, then feather, then ink colour
        /// (a null colour keeps the picture's own). Returns a new buffer; the source is untouched.
        /// </summary>
        internal static byte[] Process(byte[] source, int w, int h, int threshold, int featherStrength, string? inkColor)
        {
            var px = (byte[])source.Clone();
            ThresholdAlpha(px, w, h, threshold);
            Feather(px, w, h, FeatherRadius(featherStrength, w, h));
            if (TryParseColor(inkColor, out byte r, out byte g, out byte b)) Recolor(px, w, h, r, g, b);
            return px;
        }

        /// <summary>True when no pixel is see-through: a photo or scan rather than an already-clean signature.</summary>
        internal static bool IsFullyOpaque(byte[] px, int w, int h)
        {
            int n = Math.Min(w * h, px.Length / 4);
            for (int i = 0; i < n; i++) if (px[i * 4 + 3] != 255) return false;
            return true;
        }

        /// <summary>The smallest box holding every pixel with at least <paramref name="minAlpha"/> alpha, or null when there is none.</summary>
        internal static (int X, int Y, int W, int H)? InkBounds(byte[] px, int w, int h, int minAlpha = 8)
        {
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (px[(y * w + x) * 4 + 3] >= minAlpha)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
            return maxX < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        /// <summary>Copies a region of a buffer, padded with transparent pixels by <paramref name="pad"/> on every side.</summary>
        internal static byte[] Crop(byte[] px, int w, int h, (int X, int Y, int W, int H) box, int pad, out int outW, out int outH)
        {
            outW = box.W + pad * 2;
            outH = box.H + pad * 2;
            var dst = new byte[outW * outH * 4];
            for (int y = 0; y < box.H; y++)
            {
                int sy = box.Y + y;
                if (sy < 0 || sy >= h) continue;
                for (int x = 0; x < box.W; x++)
                {
                    int sx = box.X + x;
                    if (sx < 0 || sx >= w) continue;
                    Array.Copy(px, (sy * w + sx) * 4, dst, ((y + pad) * outW + x + pad) * 4, 4);
                }
            }
            return dst;
        }

        /// <summary>
        /// Draws pen strokes (canvas units) as anti-aliased round-capped lines of the given colour on a
        /// transparent buffer of the canvas size times <paramref name="scale"/>. A stroke of one point is skipped,
        /// as everywhere else a signature is drawn.
        /// </summary>
        internal static byte[] RasterizeStrokes(IReadOnlyList<IReadOnlyList<(double X, double Y)>> strokes,
            double canvasWidth, double canvasHeight, double penWidth, double scale, byte r, byte g, byte b,
            out int w, out int h)
        {
            w = Math.Max(1, (int)Math.Ceiling(canvasWidth * scale));
            h = Math.Max(1, (int)Math.Ceiling(canvasHeight * scale));
            var px = new byte[w * h * 4];
            double rad = ResolvePenWidth(penWidth) * scale / 2;
            foreach (var stroke in strokes)
            {
                for (int i = 0; i + 1 < stroke.Count; i++)
                {
                    double x1 = stroke[i].X * scale, y1 = stroke[i].Y * scale;
                    double x2 = stroke[i + 1].X * scale, y2 = stroke[i + 1].Y * scale;
                    if (!double.IsFinite(x1 + y1 + x2 + y2)) continue;
                    int x0 = Math.Max(0, (int)Math.Floor(Math.Min(x1, x2) - rad - 1));
                    int xe = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(x1, x2) + rad + 1));
                    int y0 = Math.Max(0, (int)Math.Floor(Math.Min(y1, y2) - rad - 1));
                    int ye = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(y1, y2) + rad + 1));
                    double dx = x2 - x1, dy = y2 - y1, len2 = dx * dx + dy * dy;
                    for (int y = y0; y <= ye; y++)
                        for (int x = x0; x <= xe; x++)
                        {
                            double cx = x + 0.5, cy = y + 0.5;
                            double t = len2 > 0 ? Math.Clamp(((cx - x1) * dx + (cy - y1) * dy) / len2, 0, 1) : 0;
                            double d = Math.Sqrt((cx - (x1 + t * dx)) * (cx - (x1 + t * dx)) + (cy - (y1 + t * dy)) * (cy - (y1 + t * dy)));
                            int alpha = (int)Math.Round(Math.Clamp(rad + 0.5 - d, 0, 1) * 255);
                            int o = (y * w + x) * 4;
                            if (alpha > px[o + 3])
                            {
                                px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = (byte)alpha;
                            }
                        }
                }
            }
            return px;
        }
    }
}
