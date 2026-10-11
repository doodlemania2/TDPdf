using System.Text.Json;
using TDPdf.Services;

/// <summary>
/// Pins Signature Requests' WPF-free core (<see cref="DocumensoRequestView"/>): what each status
/// offers — above all that a completed envelope can never be deleted from the list and only a
/// pending one cancelled — the signing-link and web-page URLs, download names, the 429 back-off,
/// the merge of this device's sends with the server's list, and the store's new remove /
/// signed-copy fingerprint.
/// </summary>
internal static class DocumensoRequests
{
    private const string Base = "https://sign.tdpdf.com";

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("Electronic Signature — signature requests");
        Actions(Check);
        Words(Check);
        Urls(Check);
        Names(Check);
        Backoff(Check);
        Merge(Check);
        Wire(Check);
        Store(Check, tmp);
    }

    private static void Actions(Action<string, bool, string> Check)
    {
        var c = DocumensoRequestView.Actions(DocumensoEnvelopeStatus.Completed);
        Check("completed: all three downloads, no cancel, NO delete",
              c.DownloadSignedCopy && c.DownloadCertificate && c.DownloadAuditLog && !c.Cancel && !c.Delete, c.ToString());
        var p = DocumensoRequestView.Actions(DocumensoEnvelopeStatus.Pending);
        Check("pending: cancel only — no downloads, no delete",
              p.Cancel && !p.Delete && !p.DownloadSignedCopy && !p.DownloadCertificate && !p.DownloadAuditLog, p.ToString());
        foreach (var s in new[] { DocumensoEnvelopeStatus.Draft, DocumensoEnvelopeStatus.Cancelled, DocumensoEnvelopeStatus.Rejected })
        {
            var a = DocumensoRequestView.Actions(s);
            Check($"{s.ToString().ToLowerInvariant()}: delete, no cancel, no downloads",
                  a.Delete && !a.Cancel && !a.DownloadSignedCopy, a.ToString());
        }
        var u = DocumensoRequestView.Actions(DocumensoEnvelopeStatus.Unknown);
        Check("an unknown status offers nothing destructive", !u.Delete && !u.Cancel, u.ToString());
    }

    private static void Words(Action<string, bool, string> Check)
    {
        Check("statuses read as plain words",
              DocumensoRequestView.StatusLabel(DocumensoEnvelopeStatus.Pending) == "Pending"
              && DocumensoRequestView.StatusLabel(DocumensoEnvelopeStatus.Cancelled) == "Cancelled"
              && DocumensoRequestView.StatusLabel(DocumensoEnvelopeStatus.Unknown) == "Unknown", "");

        var signer = new DocumensoRecipient { Role = DocumensoRecipientRole.Signer, SigningStatus = DocumensoSigningStatus.NotSigned };
        Check("a signer who has not opened it", DocumensoRequestView.RecipientStatus(signer, DocumensoEnvelopeStatus.Pending) == "not signed yet", "");
        signer.ReadStatus = DocumensoReadStatus.Opened;
        Check("a signer who opened it", DocumensoRequestView.RecipientStatus(signer, DocumensoEnvelopeStatus.Pending) == "opened, not signed yet", "");
        signer.SigningStatus = DocumensoSigningStatus.Signed;
        signer.SignedAt = "2026-10-10T12:00:00.000Z";
        Check("a signer who signed shows when", DocumensoRequestView.RecipientStatus(signer, DocumensoEnvelopeStatus.Pending).StartsWith("signed "),
              DocumensoRequestView.RecipientStatus(signer, DocumensoEnvelopeStatus.Pending));
        var cc = new DocumensoRecipient { Role = DocumensoRecipientRole.Cc };
        Check("a CC is never 'not signed yet'", DocumensoRequestView.RecipientStatus(cc, DocumensoEnvelopeStatus.Pending) == "gets a copy", "");
        var rejected = new DocumensoRecipient { Role = DocumensoRecipientRole.Signer, SigningStatus = DocumensoSigningStatus.Rejected };
        Check("a rejection reads as rejected", DocumensoRequestView.RecipientStatus(rejected, DocumensoEnvelopeStatus.Rejected) == "rejected", "");
        var unsent = new DocumensoRecipient { Role = DocumensoRecipientRole.Signer };
        Check("on a draft nobody has been sent anything",
              DocumensoRequestView.RecipientStatus(unsent, DocumensoEnvelopeStatus.Draft) == "not sent yet", "");
        Check("recipient label: name and address", DocumensoRequestView.RecipientLabel(new DocumensoRecipient { Name = "Ann", Email = "ann@x.example" }) == "Ann <ann@x.example>", "");
        Check("recipient label: address alone", DocumensoRequestView.RecipientLabel(new DocumensoRecipient { Email = "ann@x.example" }) == "ann@x.example", "");
    }

    private static void Urls(Action<string, bool, string> Check)
    {
        Check("signing link is {base}/sign/{token} (formatSigningLink)",
              DocumensoRequestView.SigningUrl(Base + "/", "tok_123") == Base + "/sign/tok_123",
              DocumensoRequestView.SigningUrl(Base + "/", "tok_123") ?? "null");
        Check("no token, no link", DocumensoRequestView.SigningUrl(Base, "  ") is null, "");
        Check("a token is escaped, never able to change the path",
              DocumensoRequestView.SigningUrl(Base, "a/../b") == Base + "/sign/a%2F..%2Fb", DocumensoRequestView.SigningUrl(Base, "a/../b") ?? "");
        Check("the envelope page is /t/{team}/documents/{id}",
              DocumensoRequestView.WebUrl(Base, "personal_abc", "envelope_xyz") == Base + "/t/personal_abc/documents/envelope_xyz",
              DocumensoRequestView.WebUrl(Base, "personal_abc", "envelope_xyz"));
        Check("no team known: the instance's front page", DocumensoRequestView.WebUrl(Base, null, "envelope_xyz") == Base + "/", "");

        var pending = new DocumensoRecipient { Role = DocumensoRecipientRole.Signer, SigningStatus = DocumensoSigningStatus.NotSigned, Token = "t" };
        Check("copy link: a pending signer with a token", DocumensoRequestView.CanCopySigningLink(DocumensoEnvelopeStatus.Pending, pending), "");
        Check("copy link: not once it is completed", !DocumensoRequestView.CanCopySigningLink(DocumensoEnvelopeStatus.Completed, pending), "");
        Check("copy link: never for a CC",
              !DocumensoRequestView.CanCopySigningLink(DocumensoEnvelopeStatus.Pending, new DocumensoRecipient { Role = DocumensoRecipientRole.Cc, Token = "t" }), "");
        Check("copy link: not after they signed",
              !DocumensoRequestView.CanCopySigningLink(DocumensoEnvelopeStatus.Pending,
                  new DocumensoRecipient { Role = DocumensoRecipientRole.Signer, SigningStatus = DocumensoSigningStatus.Signed, Token = "t" }), "");
        Check("copy link: not without a token",
              !DocumensoRequestView.CanCopySigningLink(DocumensoEnvelopeStatus.Pending, new DocumensoRecipient { Role = DocumensoRecipientRole.Signer }), "");
    }

    private static void Names(Action<string, bool, string> Check)
    {
        Check("signed copy name", DocumensoRequestView.DefaultFileName("Lease 2026", DocumensoArtifact.SignedCopy) == "Lease 2026 (signed).pdf", "");
        Check("certificate name", DocumensoRequestView.DefaultFileName("Lease 2026.pdf", DocumensoArtifact.Certificate) == "Lease 2026 (certificate).pdf", "");
        Check("audit log name", DocumensoRequestView.DefaultFileName("Lease", DocumensoArtifact.AuditLog) == "Lease (audit log).pdf", "");
        string bad = DocumensoRequestView.DefaultFileName("a/b\\c:d*e?f\"g<h>i|j\u0001. ", DocumensoArtifact.SignedCopy);
        Check("reserved and control characters are replaced, trailing dots trimmed",
              bad == "a_b_c_d_e_f_g_h_i_j_ (signed).pdf", bad);
        Check("an empty title still gives a name", DocumensoRequestView.DefaultFileName("  ", DocumensoArtifact.SignedCopy) == "document (signed).pdf", "");
        Check("a device name is defused", DocumensoRequestView.SafeFileStem("CON") == "_CON" && DocumensoRequestView.SafeFileStem("com1") == "_com1", "");
        Check("a very long title is capped", DocumensoRequestView.SafeFileStem(new string('x', 500)).Length == 120, "");
    }

    private static void Backoff(Action<string, bool, string> Check)
    {
        var b = DocumensoRequestView.RefreshInterval;
        Check("the base interval is 30 s", b == TimeSpan.FromSeconds(30), b.ToString());
        var one = DocumensoRequestView.NextRefreshInterval(b, DocumensoFailure.RateLimited);
        Check("a 429 doubles it", one == TimeSpan.FromSeconds(60), one.ToString());
        var t = b;
        for (int i = 0; i < 10; i++) t = DocumensoRequestView.NextRefreshInterval(t, DocumensoFailure.RateLimited);
        Check("…up to the cap", t == DocumensoRequestView.MaxRefreshInterval, t.ToString());
        Check("a longer Retry-After is honoured",
              DocumensoRequestView.NextRefreshInterval(b, DocumensoFailure.RateLimited, TimeSpan.FromSeconds(200)) == TimeSpan.FromSeconds(200), "");
        Check("…but never past the cap",
              DocumensoRequestView.NextRefreshInterval(b, DocumensoFailure.RateLimited, TimeSpan.FromHours(2)) == DocumensoRequestView.MaxRefreshInterval, "");
        Check("a success resets it", DocumensoRequestView.NextRefreshInterval(t, null) == b, "");
        Check("being offline neither speeds up nor resets a back-off",
              DocumensoRequestView.NextRefreshInterval(TimeSpan.FromSeconds(120), DocumensoFailure.Network) == TimeSpan.FromSeconds(120), "");
    }

    private static void Merge(Action<string, bool, string> Check)
    {
        var t0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var tracked = new List<DocumensoRequestStore.Entry>
        {
            new("e_new", "New", t0.AddDays(2), 1),
            new("e_gone", "Deleted on the server", t0.AddDays(1), 1),
            new("e_old", "Old", t0, 1),
        };
        var trackedEnvs = new List<DocumensoEnvelope>
        {
            new() { Id = "e_old", Title = "Old", Status = DocumensoEnvelopeStatus.Completed },
            new() { Id = "e_new", Title = "New", Status = DocumensoEnvelopeStatus.Pending },
        };
        var listed = new List<DocumensoEnvelope>
        {
            new() { Id = "e_new", Title = "New", Status = DocumensoEnvelopeStatus.Pending },
            new() { Id = "e_web", Title = "Sent from the web", Status = DocumensoEnvelopeStatus.Draft, CreatedAt = "2026-09-01T00:00:00Z" },
        };
        var (mine, others) = DocumensoRequestView.Merge(tracked, trackedEnvs, listed);
        Check("this device's sends come first, in sent order, newest first",
              string.Join(",", mine.Select(r => r.Envelope.Id)) == "e_new,e_old", string.Join(",", mine.Select(r => r.Envelope.Id)));
        Check("one the server no longer returns is simply not shown", mine.All(r => r.Envelope.Id != "e_gone"), "");
        Check("nothing is listed twice", others.Count == 1 && others[0].Envelope.Id == "e_web", string.Join(",", others.Select(r => r.Envelope.Id)));
        Check("the sent date is this device's record", mine[0].SentUtc == t0.AddDays(2), "");
        Check("others carry the server's createdAt", others[0].SentUtc == new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), $"{others[0].SentUtc:o}");

        var (mine2, _) = DocumensoRequestView.Merge(tracked, Array.Empty<DocumensoEnvelope>(), listed);
        Check("if get-many fails, a tracked envelope on the page is still 'mine'",
              mine2.Count == 1 && mine2[0].Envelope.Id == "e_new", "");

        string a = DocumensoRequestView.Fingerprint(mine);
        trackedEnvs[1].Recipients.Add(new DocumensoRecipient { Id = 7, SigningStatus = DocumensoSigningStatus.Signed });
        var (mine3, _) = DocumensoRequestView.Merge(tracked, trackedEnvs, listed);
        Check("a signature arriving changes the fingerprint (the list redraws)", DocumensoRequestView.Fingerprint(mine3) != a, "");
        Check("nothing new, same fingerprint (no redraw, no focus theft)",
              DocumensoRequestView.Fingerprint(mine3) == DocumensoRequestView.Fingerprint(DocumensoRequestView.Merge(tracked, trackedEnvs, listed).Mine), "");
    }

    private static void Wire(Action<string, bool, string> Check)
    {
        // Trimmed from the live list endpoint's shape (OpenAPI, v2.18.0): CANCELLED status,
        // recipient tokens, team url, ISO dates.
        const string json = """
        {"data":[{"id":"envelope_a","title":"T","status":"CANCELLED","createdAt":"2026-10-10T10:00:00.000Z",
          "team":{"id":3,"url":"personal_x"},
          "recipients":[{"id":1,"email":"a@b.example","name":"A","role":"SIGNER","signingStatus":"NOT_SIGNED",
                         "readStatus":"OPENED","sendStatus":"SENT","token":"tok","signingOrder":null,"signedAt":null}]}],
         "count":1,"currentPage":1,"perPage":20,"totalPages":1}
        """;
        var page = JsonSerializer.Deserialize<DocumensoEnvelopePage>(json, DocumensoClient.Json)!;
        var e = page.Data.Single();
        Check("CANCELLED parses (not Unknown)", e.Status == DocumensoEnvelopeStatus.Cancelled, e.Status.ToString());
        Check("the recipient token and statuses parse",
              e.Recipients[0].Token == "tok" && e.Recipients[0].ReadStatus == DocumensoReadStatus.Opened && e.Recipients[0].SigningStatus == DocumensoSigningStatus.NotSigned, "");
        Check("the team url parses (Open in browser)", e.Team?.Url == "personal_x", "");
        Check("list query for page 2", DocumensoClient.BuildListQuery(new DocumensoEnvelopeQuery(Page: 2, PerPage: DocumensoRequestView.PageSize))
              .StartsWith("?page=2&perPage=20&type=DOCUMENT", StringComparison.Ordinal), "");
    }

    private static void Store(Action<string, bool, string> Check, string tmp)
    {
        string path = Path.Combine(tmp, "esign-requests-3.json");
        if (File.Exists(path)) File.Delete(path);
        var t0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        DocumensoRequestStore.Add(new("e1", "One", t0, 1), path);
        DocumensoRequestStore.Add(new("e2", "Two", t0.AddMinutes(1), 1), path);

        string sha = new string('a', 64);
        Check("a signed copy's fingerprint is recorded", DocumensoRequestStore.RecordSignedCopy("e1", sha, path)
              && DocumensoRequestStore.Load(path).Single(e => e.EnvelopeId == "e1").SignedSha256 == sha, "");
        Check("…and survives a later send", DocumensoRequestStore.Add(new("e3", "Three", t0.AddMinutes(2), 1), path)
              && DocumensoRequestStore.Load(path).Single(e => e.EnvelopeId == "e1").SignedSha256 == sha, "");
        Check("an envelope this device did not send is not added by a download",
              !DocumensoRequestStore.RecordSignedCopy("elsewhere", sha, path) && DocumensoRequestStore.Load(path).Count == 3, "");
        Check("a malformed fingerprint is refused", !DocumensoRequestStore.RecordSignedCopy("e2", "not-a-hash", path), "");
        Check("remove forgets one envelope", DocumensoRequestStore.Remove("e2", path)
              && DocumensoRequestStore.Load(path).Select(e => e.EnvelopeId).SequenceEqual(new[] { "e3", "e1" }), "");
        Check("removing an unknown id is a no-op", !DocumensoRequestStore.Remove("nope", path), "");
        File.WriteAllText(path, """{"requests":[{"envelopeId":"e9","title":"x","signedSha256":"zz"}]}""");
        Check("a hand-edited bad fingerprint loads as none", DocumensoRequestStore.Load(path).Single().SignedSha256 is null, "");
    }
}

/// <summary>
/// Signature Requests' two reads against the real instance: one page of the list, then
/// <c>get-many</c> over whatever that page held. Read-only — it creates, cancels and deletes
/// nothing. SKIPs unless TDPDF_DOCUMENSO_TEST_TOKEN is set.
/// </summary>
internal static class DocumensoRequestsLive
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("Electronic Signature — live signature requests");
        string? token = Environment.GetEnvironmentVariable("TDPDF_DOCUMENSO_TEST_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) { Console.WriteLine("  SKIP  TDPDF_DOCUMENSO_TEST_TOKEN is not set"); return; }
        string baseUrl = Environment.GetEnvironmentVariable("TDPDF_DOCUMENSO_TEST_BASEURL") is { Length: > 0 } b
            ? b : DocumensoConfig.DefaultBaseUrl;
        RunAsync(Check, new DocumensoClient(baseUrl, token.Trim())).GetAwaiter().GetResult();
    }

    private static async Task RunAsync(Action<string, bool, string> Check, DocumensoClient client)
    {
        try
        {
            var page = await client.ListEnvelopesAsync(new DocumensoEnvelopeQuery(PerPage: DocumensoRequestView.PageSize), CancellationToken.None);
            Check("live: the list returns a page", page.CurrentPage == 1 && page.TotalPages >= 0, $"count={page.Count} pages={page.TotalPages}");
            Check("live: every listed status is one TDPdf knows", page.Data.All(e => e.Status != DocumensoEnvelopeStatus.Unknown),
                  string.Join(",", page.Data.Select(e => e.Status).Distinct()));
            var ids = page.Data.Select(e => e.Id).Take(DocumensoClient.GetManyLimit).ToList();
            if (ids.Count == 0)
            {
                Console.WriteLine("  SKIP  live get-many: the account has no envelopes");
                return;
            }
            var many = await client.GetManyAsync(ids, CancellationToken.None);
            Check("live: get-many returns the listed envelopes", many.Count == ids.Count && many.All(e => ids.Contains(e.Id)),
                  $"asked {ids.Count}, got {many.Count}");
            Check("live: get-many carries envelope items (what a download needs)", many.All(e => e.EnvelopeItems.Count > 0), "");
            var (mine, others) = DocumensoRequestView.Merge(Array.Empty<DocumensoRequestStore.Entry>(), many, page.Data);
            Check("live: the merge shows each envelope once", mine.Count + others.Count == page.Data.Count, "");
        }
        catch (DocumensoApiException ex)
        {
            Check("live: signature requests", false, $"{ex.Failure} {ex.StatusCode} {ex.ErrorCode}");
        }
    }
}
