using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using TDPdf.Services;

/// <summary>
/// Pins Sign This Document's WPF-free core (<see cref="DocumensoSelfSignPlan"/>): you are the one
/// Signer, your fields are adopted from Send for Signature but nobody else's are, a placeholder for
/// r1 is enough and one for r2 is refused, and the signing page that opens is yours.
/// </summary>
internal static class DocumensoSelfSign
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("Electronic Signature — sign this document");

        var s = DocumensoSelfSignPlan.NewSession("Lease", " me@x.example ", "Me");
        Check("one recipient, you, as Signer", s.Recipients.Count == 1 && s.Recipients[0].Email == "me@x.example"
              && s.Recipients[0].Role == DocumensoRecipientRole.Signer, "");
        Check("parallel order (there is nobody to wait for)", !s.SequentialOrder, "");

        var none = Array.Empty<DocumensoPlaceholder>();
        Check("no fields and no placeholder: the user places one",
              DocumensoSelfSignPlan.Source(s, none) == ESignSelfFieldSource.NeedsPlacement, "");
        var r1 = new[] { new DocumensoPlaceholder("{{signature, r1}}", DocumensoFieldType.Signature, 1, 1) };
        Check("a signature placeholder for r1 is enough", DocumensoSelfSignPlan.Source(s, r1) == ESignSelfFieldSource.Placeholder, "");
        var nameOnly = new[] { new DocumensoPlaceholder("{{name, r1}}", DocumensoFieldType.Name, 1, 1) };
        Check("a non-signature placeholder is not", DocumensoSelfSignPlan.Source(s, nameOnly) == ESignSelfFieldSource.NeedsPlacement, "");
        Check("…and with r1 the shared check passes", DocumensoSendPlan.Check(s, r1).CanSend, string.Join(" / ", DocumensoSendPlan.Check(s, r1).Errors));
        var r2 = new[] { r1[0], new DocumensoPlaceholder("{{signature, r2}}", DocumensoFieldType.Signature, 2, 1) };
        Check("placeholders for r2 cannot be self-signed (the shared check says why)", !DocumensoSendPlan.Check(s, r2).CanSend, "");

        // Adoption: your row's fields come over, re-keyed; someone else's stay put.
        var send = new ESignSendSession();
        var you = new ESignRecipientEntry { Email = "ME@x.example", Role = DocumensoRecipientRole.Signer };
        var them = new ESignRecipientEntry { Email = "them@x.example", Role = DocumensoRecipientRole.Signer };
        send.Recipients.Add(them);
        send.Recipients.Add(you);
        var rect = new DocumensoFieldRect(10, 10, 20, 5);
        send.Fields.Add(new ESignFieldDraft(you.Key, DocumensoFieldType.Signature, 0, rect));
        send.Fields.Add(new ESignFieldDraft(you.Key, DocumensoFieldType.Date, 0, rect with { Y = 20 }));
        send.Fields.Add(new ESignFieldDraft(them.Key, DocumensoFieldType.Signature, 1, rect));
        var self = DocumensoSelfSignPlan.NewSession("Lease", "me@x.example", null);
        int n = DocumensoSelfSignPlan.AdoptFields(self, send);
        Check("your two fields are adopted (address matched case-insensitively)", n == 2 && self.Fields.Count == 2, $"{n}");
        Check("…re-keyed to the self-sign recipient", self.Fields.All(f => f.RecipientKey == self.Recipients[0].Key), "");
        Check("…and the other signer's field is not taken", self.Fields.All(f => f.PageIndex == 0), "");
        Check("the Send session is left untouched", send.Fields.Count == 3 && send.Fields.Count(f => f.RecipientKey == you.Key) == 2, "");
        Check("adopted fields count as placed", DocumensoSelfSignPlan.Source(self, none) == ESignSelfFieldSource.Placed, "");
        Check("nothing to adopt from a missing session", DocumensoSelfSignPlan.AdoptFields(DocumensoSelfSignPlan.NewSession("x", "me@x.example", null), null) == 0, "");

        var req = DocumensoSendPlan.BuildCreateRequest(self);
        Check("the create payload: one Signer, with your fields inline",
              req.Recipients is { Count: 1 } rs && rs[0].Role == DocumensoRecipientRole.Signer && rs[0].Fields?.Count == 2
              && rs[0].SigningOrder is null, "");

        // The page that opens is yours.
        var links = new List<ESignSigningLink> { new(self.Recipients[0].Key, "me", "https://sign.tdpdf.com/sign/mine") };
        Check("your signing link is the one opened",
              DocumensoSelfSignPlan.SigningUrlFor("ME@x.example", links, self.Recipients) == "https://sign.tdpdf.com/sign/mine", "");
        Check("no link for you, nothing opened", DocumensoSelfSignPlan.SigningUrlFor("me@x.example", Array.Empty<ESignSigningLink>(), self.Recipients) is null, "");

        Check("states: pending waits, completed completes, rejected/cancelled end",
              DocumensoSelfSignPlan.StateOf(DocumensoEnvelopeStatus.Pending) == ESignSelfState.Waiting
              && DocumensoSelfSignPlan.StateOf(DocumensoEnvelopeStatus.Completed) == ESignSelfState.Completed
              && DocumensoSelfSignPlan.StateOf(DocumensoEnvelopeStatus.Rejected) == ESignSelfState.Rejected
              && DocumensoSelfSignPlan.StateOf(DocumensoEnvelopeStatus.Cancelled) == ESignSelfState.Cancelled, "");
    }
}

/// <summary>
/// Sign This Document against the real instance, up to the browser: a one-page PDF with a
/// <c>{{signature, r1}}</c> placeholder, sent to TDPDF_DOCUMENSO_TEST_EMAIL alone, checked for a
/// signing link and a PENDING envelope, then deleted. Nothing is signed. SKIPs unless both
/// TDPDF_DOCUMENSO_TEST_TOKEN and TDPDF_DOCUMENSO_TEST_EMAIL are set; it really emails that address.
/// </summary>
internal static class DocumensoSelfSignLive
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("Electronic Signature — live sign this document");
        string? token = Environment.GetEnvironmentVariable("TDPDF_DOCUMENSO_TEST_TOKEN");
        string? email = Environment.GetEnvironmentVariable("TDPDF_DOCUMENSO_TEST_EMAIL");
        if (string.IsNullOrWhiteSpace(token)) { Console.WriteLine("  SKIP  TDPDF_DOCUMENSO_TEST_TOKEN is not set"); return; }
        if (string.IsNullOrWhiteSpace(email)) { Console.WriteLine("  SKIP  TDPDF_DOCUMENSO_TEST_EMAIL is not set"); return; }
        string baseUrl = Environment.GetEnvironmentVariable("TDPDF_DOCUMENSO_TEST_BASEURL") is { Length: > 0 } b
            ? b : DocumensoConfig.DefaultBaseUrl;
        RunAsync(Check, tmp, new DocumensoClient(baseUrl, token.Trim()), email.Trim()).GetAwaiter().GetResult();
    }

    private static async Task RunAsync(Action<string, bool, string> Check, string tmp, DocumensoClient client, string email)
    {
        string path = Path.Combine(tmp, "esign-live-self.pdf");
        {
            var doc = new PdfDocument();
            var page = doc.AddPage();
            using var g = XGraphics.FromPdfPage(page);
            g.DrawString("TDPdf live self-sign test — safe to delete", new XFont("Helvetica", 14), XBrushes.Black, 60, 100);
            g.DrawString("{{signature, r1}}", new XFont("Helvetica", 14), XBrushes.Black, 60, 400);
            doc.Save(path);
        }
        var placeholders = DocumensoPlaceholders.Scan(path);
        var s = DocumensoSelfSignPlan.NewSession("TDPdf live self-sign test — safe to delete", email, "TDPdf live test");
        Check("live self: the placeholder is found and is enough",
              DocumensoSelfSignPlan.Source(s, placeholders) == ESignSelfFieldSource.Placeholder, $"{placeholders.Count} placeholder(s)");

        var res = await DocumensoSender.SendAsync(client, DocumensoSendPlan.BuildCreateRequest(s), File.ReadAllBytes(path),
            "tdpdf-live-self.pdf", null, CancellationToken.None);
        Check("live self: sent", res.Ok, res.Ok ? "" : $"{res.Outcome} {res.Failure?.StatusCode} {res.Failure?.ErrorCode}");
        if (!res.Ok || res.EnvelopeId is null) return;
        try
        {
            var url = DocumensoSelfSignPlan.SigningUrlFor(email, DocumensoSendPlan.MatchLinks(s.Recipients, res.Recipients), s.Recipients);
            Check("live self: a signing page to open", url is not null && url.StartsWith(client.BaseUrl + "/sign/", StringComparison.Ordinal), "");
            var env = await client.GetEnvelopeAsync(res.EnvelopeId, CancellationToken.None);
            Check("live self: Refresh reads it as waiting", DocumensoSelfSignPlan.StateOf(env.Status) == ESignSelfState.Waiting, env.Status.ToString());
            Check("live self: the placeholder became your signature field",
                  env.Fields.Count(f => f.Type == DocumensoFieldType.Signature) == 1, $"{env.Fields.Count} field(s)");
        }
        finally
        {
            bool gone = false;
            try { await client.DeleteAsync(res.EnvelopeId, CancellationToken.None); gone = true; }
            catch (DocumensoApiException)
            {
                try
                {
                    await client.CancelAsync(res.EnvelopeId, "TDPdf live test", CancellationToken.None);
                    await client.DeleteAsync(res.EnvelopeId, CancellationToken.None);
                    gone = true;
                }
                catch (DocumensoApiException) { }
            }
            Check("live self: the test envelope was deleted", gone, "");
        }
    }
}
