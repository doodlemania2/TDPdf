using System.Text;
using TDPdf.Services;

/// <summary>
/// Document-scope duplex in Print Schema XML: read back from what a driver dialog returned, and
/// written so the job keeps it.
/// </summary>
/// <remarks>
/// WPF's typed Duplexing only knows the job-scope feature, so on a driver that keeps two-sided
/// printing as psk:DocumentDuplex a choice made in its Properties dialog read back as nothing and
/// was then overwritten by TDPdf's own combo. These pin the XML side, which is all that runs here.
/// </remarks>
internal static class PrintDuplex
{
    private const string Ns =
        "xmlns:psf=\"http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework\" " +
        "xmlns:psk=\"http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords\"";

    private static Stream S(string xml) => new MemoryStream(Encoding.UTF8.GetBytes(xml));

    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nPrint: document-scope duplex");

        string ticket = $"<psf:PrintTicket {Ns} version=\"1\">" +
                        "<psf:Feature name=\"psk:PageOrientation\"><psf:Option name=\"psk:Portrait\"/></psf:Feature>" +
                        "<psf:Feature name=\"psk:DocumentDuplex\"><psf:Option name=\"psk:TwoSidedShortEdge\"/></psf:Feature>" +
                        "</psf:PrintTicket>";
        Check("reads the driver's document-scope choice, short edge included",
              PrintTicketDuplex.Read(S(ticket)) == "TwoSidedShortEdge", PrintTicketDuplex.Read(S(ticket)) ?? "null");

        string jobOnly = $"<psf:PrintTicket {Ns} version=\"1\">" +
                         "<psf:Feature name=\"psk:JobDuplexAllDocumentsContiguously\"><psf:Option name=\"psk:TwoSidedLongEdge\"/></psf:Feature>" +
                         "</psf:PrintTicket>";
        Check("a job-scope-only ticket has no document-scope value", PrintTicketDuplex.Read(S(jobOnly)) is null, "");

        // A different prefix for the same namespace is the same keyword.
        string otherPrefix = "<a:PrintTicket xmlns:a=\"http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework\" " +
                             "xmlns:k=\"http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords\" version=\"1\">" +
                             "<a:Feature name=\"k:DocumentDuplex\"><a:Option name=\"k:TwoSidedLongEdge\"/></a:Feature></a:PrintTicket>";
        Check("matches by namespace, not by prefix", PrintTicketDuplex.Read(S(otherPrefix)) == "TwoSidedLongEdge", "");

        Check("an unchanged value is not rewritten",
              PrintTicketDuplex.Write(S(ticket), "TwoSidedShortEdge", addIfMissing: false) is null, "");

        using (var w = PrintTicketDuplex.Write(S(ticket), "OneSided", addIfMissing: false))
        {
            string after = w is null ? "" : new StreamReader(w).ReadToEnd();
            bool ok = w is not null && PrintTicketDuplex.Read(S(after)) == "OneSided" && after.Contains("psk:Portrait");
            Check("a changed value replaces the option and keeps the rest", ok, ok ? "" : after);
        }

        Check("a missing feature is left out unless the printer advertises it",
              PrintTicketDuplex.Write(S(jobOnly), "TwoSidedLongEdge", addIfMissing: false) is null, "");
        using (var w = PrintTicketDuplex.Write(S(jobOnly), "TwoSidedLongEdge", addIfMissing: true))
        {
            string after = w is null ? "" : new StreamReader(w).ReadToEnd();
            bool ok = PrintTicketDuplex.Read(S(after)) == "TwoSidedLongEdge";
            Check("and added when it does", ok, ok ? "" : after);
        }

        string caps = $"<psf:PrintCapabilities {Ns} version=\"1\">" +
                      "<psf:Feature name=\"psk:DocumentDuplex\"><psf:Option name=\"psk:OneSided\"/>" +
                      "<psf:Option name=\"psk:TwoSidedLongEdge\"/></psf:Feature></psf:PrintCapabilities>";
        string oneSidedCaps = $"<psf:PrintCapabilities {Ns} version=\"1\">" +
                              "<psf:Feature name=\"psk:DocumentDuplex\"><psf:Option name=\"psk:OneSided\"/></psf:Feature>" +
                              "</psf:PrintCapabilities>";
        Check("document-scope two-sided support is detected", PrintTicketDuplex.SupportsDocumentDuplex(S(caps)), "");
        Check("a one-sided-only feature does not count as support", !PrintTicketDuplex.SupportsDocumentDuplex(S(oneSidedCaps)), "");
    }
}
