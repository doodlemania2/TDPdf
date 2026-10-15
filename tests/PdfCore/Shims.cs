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
