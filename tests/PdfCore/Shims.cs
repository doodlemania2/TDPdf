using System.Runtime.InteropServices;

// Minimal stand-ins so the real PdfiumInterop.cs compiles and runs outside the WPF app.
//
// DocLib.Instance is how the shipping code forces PDFium initialisation (see the header
// comment in PdfiumInterop.cs). The real Docnet.Core calls FPDF_InitLibrary there, so this
// shim must too — an empty stand-in segfaults on the first FPDF_LoadDocument, which is
// exactly what happened the first time this harness ran.
namespace Docnet.Core
{
    public sealed class DocLib
    {
        [DllImport("pdfium.dll", EntryPoint = "FPDF_InitLibrary", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_InitLibrary();

        private static readonly Lazy<DocLib> _i = new(() => { FPDF_InitLibrary(); return new DocLib(); });
        public static DocLib Instance => _i.Value;
    }
}

namespace TDPdf.Diagnostics
{
    public static class Telemetry
    {
        public static void TrackEvent(string name, IDictionary<string, string>? props = null) { }
    }
}

// DocumensoConfig reads the user's own instance URL from Settings.Default. The real class is an
// ApplicationSettingsBase backed by user.config; this stand-in is a plain property the Documenso
// suite sets and restores around each resolution check.
namespace TDPdf.Properties
{
    internal sealed class Settings
    {
        public static Settings Default { get; } = new();
        public string DocumensoBaseUrl { get; set; } = "";
    }
}

// Models/Annotations.cs is compiled into this harness unchanged, and it is written against WPF's
// value types. These are the few members it touches, with WPF's semantics, so the real annotation
// classes (and the real AnnotationBaker) run here without a WPF dependency.
namespace System.Windows
{
    public struct Point
    {
        public Point(double x, double y) { X = x; Y = y; }
        public double X { get; set; }
        public double Y { get; set; }
    }

    public struct Rect
    {
        public Rect(double x, double y, double width, double height)
        { X = x; Y = y; Width = width; Height = height; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        /// <summary>Grows this rectangle to cover <paramref name="other"/> too, as WPF's does.</summary>
        public void Union(Rect other)
        {
            double l = Math.Min(X, other.X), t = Math.Min(Y, other.Y);
            double r = Math.Max(X + Width, other.X + other.Width), b = Math.Max(Y + Height, other.Y + other.Height);
            X = l; Y = t; Width = r - l; Height = b - t;
        }
    }
}

namespace System.Windows.Media
{
    public struct Color
    {
        public byte A { get; set; }
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public static Color FromArgb(byte a, byte r, byte g, byte b) => new() { A = a, R = r, G = g, B = b };
    }
}
