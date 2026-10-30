using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace TDPdf.Services
{
    /// <summary>
    /// Document-scope duplex in Print Schema XML, which WPF's typed <c>PrintTicket.Duplexing</c>
    /// and <c>PrintCapabilities.DuplexingCapability</c> do not see.
    /// </summary>
    /// <remarks>
    /// The typed property is backed by psk:JobDuplexAllDocumentsContiguously, the JOB-scope
    /// feature. Many drivers advertise two-sided printing only as psk:DocumentDuplex, so on those
    /// the typed API reports no duplex support, a two-sided choice made in the driver's own
    /// Properties dialog reads back as nothing, and setting the typed property does nothing. This
    /// reads and writes the document-scope feature directly. Pure XML, so tests/PdfCore can run it
    /// on any OS. Values are the Print Schema keywords "OneSided", "TwoSidedLongEdge" and
    /// "TwoSidedShortEdge", which are also the names of the WPF Duplexing members.
    /// (Upstream KillerPDF 1.9.0 #454.)
    /// </remarks>
    internal static class PrintTicketDuplex
    {
        private static readonly XNamespace Framework =
            "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
        private static readonly XNamespace Keywords =
            "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords";

        internal const string FeatureName = "DocumentDuplex";
        private static readonly string[] Known = { "OneSided", "TwoSidedLongEdge", "TwoSidedShortEdge" };

        /// <summary>True when the capabilities offer two-sided printing at document scope.</summary>
        internal static bool SupportsDocumentDuplex(Stream capabilitiesXml) =>
            XDocument.Load(capabilitiesXml).Root?.Elements(Framework + "Feature")
                .Where(f => HasKeyword(f, FeatureName))
                .SelectMany(f => f.Elements(Framework + "Option"))
                .Any(o => HasKeyword(o, "TwoSidedLongEdge") || HasKeyword(o, "TwoSidedShortEdge")) == true;

        /// <summary>The document-scope duplex option a ticket selects, or null when it has none.</summary>
        internal static string? Read(Stream ticketXml)
        {
            var option = XDocument.Load(ticketXml).Root?.Elements(Framework + "Feature")
                .FirstOrDefault(f => HasKeyword(f, FeatureName))?.Element(Framework + "Option");
            return option is null ? null : Known.FirstOrDefault(k => HasKeyword(option, k));
        }

        /// <summary>
        /// The ticket with its document-scope duplex set to <paramref name="keyword"/>, or null when
        /// nothing needed to change.
        /// </summary>
        /// <param name="addIfMissing">
        /// Add the feature when the ticket has none. Only when the printer advertises it: a feature
        /// the driver does not know is noise at best.
        /// </param>
        internal static MemoryStream? Write(Stream ticketXml, string keyword, bool addIfMissing)
        {
            var doc = XDocument.Load(ticketXml);
            var root = doc.Root;
            if (root is null) return null;
            var feature = root.Elements(Framework + "Feature").FirstOrDefault(f => HasKeyword(f, FeatureName));
            if (feature is null)
            {
                if (!addIfMissing) return null;
                feature = new XElement(Framework + "Feature");
                root.Add(feature);
            }
            else if (feature.Element(Framework + "Option") is { } current && HasKeyword(current, keyword))
                return null;

            string prefix = PrefixFor(feature);
            if (feature.Attribute("name") is null)
                feature.SetAttributeValue("name", $"{prefix}:{FeatureName}");
            feature.Elements(Framework + "Option").Remove();
            feature.Add(new XElement(Framework + "Option", new XAttribute("name", $"{prefix}:{keyword}")));

            var output = new MemoryStream();
            doc.Save(output);
            output.Position = 0;
            return output;
        }

        /// <summary>A prefix bound to the keywords namespace in scope, declaring one if needed.</summary>
        private static string PrefixFor(XElement element)
        {
            string? prefix = element.GetPrefixOfNamespace(Keywords);
            if (!string.IsNullOrEmpty(prefix)) return prefix;
            element.SetAttributeValue(XNamespace.Xmlns + "tdpduplex", Keywords.NamespaceName);
            return "tdpduplex";
        }

        private static bool HasKeyword(XElement element, string name)
        {
            string? value = (string?)element.Attribute("name");
            if (value is null) return false;
            int colon = value.IndexOf(':');
            if (colon < 0) return false;
            return value.Substring(colon + 1) == name
                   && element.GetNamespaceOfPrefix(value.Substring(0, colon)) == Keywords;
        }
    }
}
