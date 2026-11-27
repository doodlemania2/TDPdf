using System.IO;
using System.Xml.Linq;

namespace TDPdf.Services
{
    internal enum PrintColorMode
    {
        /// <summary>Today's behaviour: the driver manages colour, TDPdf changes nothing.</summary>
        DriverDefault,
        /// <summary>TDPdf converts every page to the chosen printer profile before it is spooled.</summary>
        TdpManaged,
    }

    /// <summary>The print window's Color section, as saved per printer.</summary>
    internal sealed record PrintColorSettings
    {
        public PrintColorMode Mode { get; init; } = PrintColorMode.DriverDefault;
        public string ProfilePath { get; init; } = "";
        public ColorIntent Intent { get; init; } = ColorIntent.RelativeColorimetric;
        public bool BlackPointCompensation { get; init; } = true;
        /// <summary>Ask the driver, through the ticket copy, not to colour-manage the pages we already converted.</summary>
        public bool SkipDriver { get; init; }
        /// <summary>Show the preview as the page will print, instead of plain sRGB.</summary>
        public bool PreviewProof { get; init; }

        /// <summary>True only when TDPdf will convert pixels and rewrite the ticket: a mode and a profile.</summary>
        public bool IsManaged => Mode == PrintColorMode.TdpManaged && !string.IsNullOrWhiteSpace(ProfilePath);

        /// <summary>The default: nothing managed. The ticket and the pixels stay exactly as before.</summary>
        public static PrintColorSettings Default { get; } = new();
    }

    /// <summary>Saves <see cref="PrintColorSettings"/> per printer in one settings string.</summary>
    internal static class PrintColorStore
    {
        // One printer per line: name, mode, intent, bpc, skip, proof, profile path; fields joined by a tab.
        private const char Sep = '\t';

        public static PrintColorSettings Get(string? stored, string printer)
        {
            if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(printer)) return PrintColorSettings.Default;
            foreach (string line in stored.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.TrimEnd('\r').Split(Sep);
                if (f.Length < 7 || f[0] != printer) continue;
                return new PrintColorSettings
                {
                    Mode = f[1] == "Managed" ? PrintColorMode.TdpManaged : PrintColorMode.DriverDefault,
                    Intent = Enum.TryParse(f[2], out ColorIntent i) && Enum.IsDefined(i) ? i : ColorIntent.RelativeColorimetric,
                    BlackPointCompensation = f[3] != "0",
                    SkipDriver = f[4] == "1",
                    PreviewProof = f[5] == "1",
                    ProfilePath = f[6],
                };
            }
            return PrintColorSettings.Default;
        }

        public static string Set(string? stored, string printer, PrintColorSettings s)
        {
            if (string.IsNullOrEmpty(printer)) return stored ?? "";
            var lines = (stored ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r')).Where(l => !l.StartsWith(printer + Sep, StringComparison.Ordinal)).ToList();
            // A printer left on the default needs no entry: absent already means "Driver default".
            if (s != PrintColorSettings.Default)
            {
                string clean(string v) => v.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
                lines.Add(string.Join(Sep, clean(printer), s.Mode == PrintColorMode.TdpManaged ? "Managed" : "Driver",
                    s.Intent, s.BlackPointCompensation ? "1" : "0", s.SkipDriver ? "1" : "0",
                    s.PreviewProof ? "1" : "0", clean(s.ProfilePath)));
            }
            // Bounded, so a long-lived install with many printers cannot grow without limit.
            if (lines.Count > 64) lines.RemoveRange(0, lines.Count - 64);
            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// Colour-management features in Print Schema XML, added to a COPY of the driver's ticket the way
    /// <see cref="PrintTicketDuplex"/> adds two-sided: the XML is edited in place, so every Job*,
    /// Document* and vendor feature the driver's Properties dialog set passes through untouched.
    /// Nothing here converts a DEVMODE, and nothing calls it unless the Color section is on
    /// "Let TDPdf manage colours" and the page conversion is working.
    /// </summary>
    internal static class PrintTicketColor
    {
        private static readonly XNamespace Framework =
            "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
        private static readonly XNamespace Keywords =
            "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords";

        internal const string ColorManagementFeature = "PageColorManagement";
        internal const string IntentFeature = "PageICMRenderingIntent";
        internal const string NoneOption = "None";

        /// <summary>The Print Schema keyword for an ICC rendering intent.</summary>
        internal static string IntentKeyword(ColorIntent i) => i switch
        {
            ColorIntent.Perceptual => "Photographs",
            ColorIntent.Saturation => "BusinessGraphics",
            ColorIntent.AbsoluteColorimetric => "AbsoluteColorimetric",
            _ => "RelativeColorimetric",
        };

        /// <summary>
        /// The ticket to spool for the Color section's settings: null, meaning "use the driver's ticket
        /// exactly as it is", unless TDPdf is managing colour, the page conversion is working AND the
        /// user asked the driver to skip its own. Driver default always returns null.
        /// </summary>
        internal static MemoryStream? ForJob(Stream ticketXml, PrintColorSettings settings, bool conversionWorks)
        {
            if (!settings.IsManaged || !conversionWorks || !settings.SkipDriver) return null;
            return Write(ticketXml, settings.Intent);
        }

        /// <summary>
        /// A copy of the ticket asking the driver not to colour-manage the page (and naming the intent
        /// when one is given), or null when the ticket already says exactly that. The input is not changed.
        /// </summary>
        internal static MemoryStream? Write(Stream ticketXml, ColorIntent? intent)
        {
            var doc = XDocument.Load(ticketXml);
            var root = doc.Root;
            if (root is null) return null;

            bool changed = SetFeature(root, ColorManagementFeature, NoneOption);
            if (intent is { } i) changed |= SetFeature(root, IntentFeature, IntentKeyword(i));
            if (!changed) return null;

            var output = new MemoryStream();
            doc.Save(output);
            output.Position = 0;
            return output;
        }

        private static bool SetFeature(XElement root, string feature, string option)
        {
            var existing = root.Elements(Framework + "Feature").FirstOrDefault(f => HasKeyword(f, feature));
            if (existing?.Element(Framework + "Option") is { } cur && HasKeyword(cur, option)) return false;

            var f = existing;
            if (f is null)
            {
                f = new XElement(Framework + "Feature");
                root.Add(f);
            }
            string prefix = PrefixFor(f);
            if (f.Attribute("name") is null) f.SetAttributeValue("name", $"{prefix}:{feature}");
            f.Elements(Framework + "Option").Remove();
            f.Add(new XElement(Framework + "Option", new XAttribute("name", $"{prefix}:{option}")));
            return true;
        }

        /// <summary>True when the capabilities XML offers <c>psk:None</c> for PageColorManagement.</summary>
        internal static bool OffersNone(Stream capabilitiesXml) =>
            XDocument.Load(capabilitiesXml).Root?.Elements(Framework + "Feature")
                .Where(f => HasKeyword(f, ColorManagementFeature))
                .SelectMany(f => f.Elements(Framework + "Option"))
                .Any(o => HasKeyword(o, NoneOption)) == true;

        /// <summary>True when a ticket selects <c>psk:None</c> for PageColorManagement.</summary>
        internal static bool SelectsNone(Stream ticketXml) =>
            XDocument.Load(ticketXml).Root?.Elements(Framework + "Feature")
                .FirstOrDefault(f => HasKeyword(f, ColorManagementFeature))?.Element(Framework + "Option") is { } o
            && HasKeyword(o, NoneOption);

        /// <summary>
        /// The driver confirmed the request when it offers None and the ticket it validated still
        /// selects it. Anything else means the driver may colour-manage the page on top of ours.
        /// </summary>
        internal static bool DriverConfirmed(Stream capabilitiesXml, Stream validatedTicketXml)
        {
            try { return OffersNone(capabilitiesXml) && SelectsNone(validatedTicketXml); }
            catch { return false; }
        }

        private static string PrefixFor(XElement element)
        {
            string? prefix = element.GetPrefixOfNamespace(Keywords);
            if (!string.IsNullOrEmpty(prefix)) return prefix;
            element.SetAttributeValue(XNamespace.Xmlns + "tdpcolor", Keywords.NamespaceName);
            return "tdpcolor";
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
