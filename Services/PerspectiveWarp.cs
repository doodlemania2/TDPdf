using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TDPdf.Services
{
    // Trapezoidal (keystone) perspective correction, ported from upstream KillerPDF v1.7.1 (#175).
    //
    // Pure WPF: no PDF library is involved. The caller hands in a rasterized page plus the four page
    // corners the user traced on the preview, in NORMALIZED (0..1) image coordinates and in the order
    // top-left, top-right, bottom-right, bottom-left. Apply solves the unit-square -> quadrilateral
    // homography, then walks the OUTPUT pixels and inverse-maps each one back into the source, so the
    // photographed trapezoid comes out as a straight rectangle.
    internal static class PerspectiveWarp
    {
        internal static bool IsIdentity(System.Collections.Generic.IReadOnlyList<Point> corners)
            => corners.Count == 4 &&
               Near(corners[0], new Point(0, 0)) && Near(corners[1], new Point(1, 0)) &&
               Near(corners[2], new Point(1, 1)) && Near(corners[3], new Point(0, 1));

        private static bool Near(Point a, Point b)
            => Math.Abs(a.X - b.X) < 0.0001 && Math.Abs(a.Y - b.Y) < 0.0001;

        internal static BitmapSource Apply(BitmapSource source, System.Collections.Generic.IReadOnlyList<Point> normalizedCorners)
        {
            if (normalizedCorners.Count != 4) throw new ArgumentException("Four corners are required.");
            Point[] q = normalizedCorners.Select(p => new Point(
                Math.Max(0, Math.Min(1, p.X)) * (source.PixelWidth - 1),
                Math.Max(0, Math.Min(1, p.Y)) * (source.PixelHeight - 1))).ToArray();

            double signedArea = 0;
            for (int i = 0; i < 4; i++)
            {
                Point next = q[(i + 1) % 4];
                signedArea += q[i].X * next.Y - next.X * q[i].Y;
            }
            if (Math.Abs(signedArea) < 1 || !IsConvex(q))
                throw new InvalidOperationException("The selected corners must form a non-crossing four-sided page.");

            double top = Distance(q[0], q[1]), bottom = Distance(q[3], q[2]);
            double left = Distance(q[0], q[3]), right = Distance(q[1], q[2]);
            int outW = Math.Max(2, (int)Math.Round((top + bottom) / 2));
            int outH = Math.Max(2, (int)Math.Round((left + right) / 2));

            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int srcStride = converted.PixelWidth * 4;
            byte[] src = new byte[srcStride * converted.PixelHeight];
            converted.CopyPixels(src, srcStride, 0);
            byte[] dst = new byte[outW * outH * 4];

            SquareToQuad(q, out double a, out double b, out double c,
                            out double d, out double e, out double f,
                            out double g, out double h);

            for (int y = 0; y < outH; y++)
            {
                double v = outH == 1 ? 0 : y / (double)(outH - 1);
                for (int x = 0; x < outW; x++)
                {
                    double u = outW == 1 ? 0 : x / (double)(outW - 1);
                    double den = g * u + h * v + 1;
                    double sx = (a * u + b * v + c) / den;
                    double sy = (d * u + e * v + f) / den;
                    SampleBilinear(src, converted.PixelWidth, converted.PixelHeight, srcStride,
                                   sx, sy, dst, (y * outW + x) * 4);
                }
            }

            var result = BitmapSource.Create(outW, outH, source.DpiX, source.DpiY,
                PixelFormats.Bgra32, null, dst, outW * 4);
            result.Freeze();
            return result;
        }

        private static double Distance(Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool IsConvex(Point[] p)
        {
            double sign = 0;
            for (int i = 0; i < 4; i++)
            {
                Point a = p[i], b = p[(i + 1) % 4], c = p[(i + 2) % 4];
                double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
                if (Math.Abs(cross) < 0.000001) return false;
                if (sign == 0) sign = Math.Sign(cross);
                else if (Math.Sign(cross) != sign) return false;
            }
            return true;
        }

        private static void SquareToQuad(Point[] p,
            out double a, out double b, out double c,
            out double d, out double e, out double f,
            out double g, out double h)
        {
            double dx1 = p[1].X - p[2].X, dx2 = p[3].X - p[2].X;
            double dy1 = p[1].Y - p[2].Y, dy2 = p[3].Y - p[2].Y;
            double dx3 = p[0].X - p[1].X + p[2].X - p[3].X;
            double dy3 = p[0].Y - p[1].Y + p[2].Y - p[3].Y;
            double den = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(dx3) < 0.000001 && Math.Abs(dy3) < 0.000001)
                g = h = 0;
            else
            {
                if (Math.Abs(den) < 0.000001) throw new InvalidOperationException("The selected corners do not form a usable page.");
                g = (dx3 * dy2 - dx2 * dy3) / den;
                h = (dx1 * dy3 - dx3 * dy1) / den;
            }
            a = p[1].X - p[0].X + g * p[1].X;
            b = p[3].X - p[0].X + h * p[3].X;
            c = p[0].X;
            d = p[1].Y - p[0].Y + g * p[1].Y;
            e = p[3].Y - p[0].Y + h * p[3].Y;
            f = p[0].Y;
        }

        private static void SampleBilinear(byte[] src, int width, int height, int stride,
            double x, double y, byte[] dst, int di)
        {
            x = Math.Max(0, Math.Min(width - 1, x));
            y = Math.Max(0, Math.Min(height - 1, y));
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            int x1 = Math.Min(width - 1, x0 + 1), y1 = Math.Min(height - 1, y0 + 1);
            double fx = x - x0, fy = y - y0;
            for (int channel = 0; channel < 4; channel++)
            {
                double top = src[y0 * stride + x0 * 4 + channel] * (1 - fx) + src[y0 * stride + x1 * 4 + channel] * fx;
                double bot = src[y1 * stride + x0 * 4 + channel] * (1 - fx) + src[y1 * stride + x1 * 4 + channel] * fx;
                dst[di + channel] = (byte)Math.Round(top * (1 - fy) + bot * fy);
            }
        }
    }
}
