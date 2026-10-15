using System.Net;
using System.Text;
using System.Text.Json;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using TDPdf.Services;

/// <summary>
/// Pins Send for Signature's WPF-free core: the canvas → Documenso field geometry (against a model
/// of Documenso's own seal), placeholder detection with Documenso's grammar, the session check, the
/// create payload, signing-link matching, the create → distribute pipeline and its rollback, and
/// the local request store.
/// </summary>
internal static class DocumensoSend
{
    private const string Token = "api_TESTtoken0123456789abcdef";
    private const string Base = "https://sign.tdpdf.com";

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("Electronic Signature — field geometry");
        Geometry(Check);

        Console.WriteLine();
        Console.WriteLine("Electronic Signature — placeholders");
        Placeholders(Check, tmp);

        Console.WriteLine();
        Console.WriteLine("Electronic Signature — send check and payload");
        CheckRules(Check);
        Payload(Check);
        Links(Check);

        Console.WriteLine();
        Console.WriteLine("Electronic Signature — send pipeline");
        Pipeline(Check).GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine("Electronic Signature — request store");
        Store(Check, tmp);
    }

    // ── Geometry ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Where Documenso v2.18.0's seal burns a field into the page, in PDF user space — a model of
    /// seal-document.handler.ts (internalVersion 2) on top of @libpdf/core 0.4.2:
    ///   page.width/height = effective box (CropBox if smaller than MediaBox, else MediaBox), with
    ///   width and height swapped on a 90/270 page; an overlay of that size, field at
    ///   positionX/Y percent from its top-left; drawn with drawPage at (tx, ty) rotated by
    ///   page.rotation about that point, where (tx, ty) is (0,0) / (H,0) / (W,H) / (0,W) for
    ///   0 / 90 / 180 / 270, and libpdf's rotate is the standard counter-clockwise matrix
    ///   [cos sin -sin cos]. Note what is absent: the box's origin is never added.
    /// </summary>
    private static PdfiumInterop.PdfRect SealModel(DocumensoFieldRect f, PdfPageGeometry.PageBox media,
        PdfPageGeometry.PageBox? crop, int rotation)
    {
        var box = crop is { } c && (c.Width < media.Width || c.Height < media.Height) ? c : media;
        bool quarter = rotation is 90 or 270;
        double W = quarter ? box.Height : box.Width;   // libpdf page.width
        double H = quarter ? box.Width : box.Height;   // libpdf page.height

        double ox1 = f.X / 100 * W, ox2 = (f.X + f.Width) / 100 * W;
        double oyTop = H - f.Y / 100 * H, oyBot = H - (f.Y + f.Height) / 100 * H;

        var (tx, ty) = rotation switch { 90 => (H, 0.0), 180 => (W, H), 270 => (0.0, W), _ => (0.0, 0.0) };
        double rad = rotation * Math.PI / 180, cos = Math.Round(Math.Cos(rad)), sin = Math.Round(Math.Sin(rad));
        (double X, double Y) T(double x, double y) => (tx + x * cos - y * sin, ty + x * sin + y * cos);

        var a = T(ox1, oyTop);
        var b = T(ox2, oyBot);
        return new PdfiumInterop.PdfRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    private static void Geometry(Action<string, bool, string> Check)
    {
        // Pure conversions first.
        var r = DocumensoFieldGeometry.CanvasToPercent(150, 300, 300, 75, 1500, 1500);
        Check("canvas → percent is a plain division", r is { X: 10, Y: 20, Width: 20, Height: 5 }, $"{r}");
        r = DocumensoFieldGeometry.CanvasToPercent(450, 375, -300, -75, 1500, 1500);
        Check("a drag up-and-left normalises to the same rect", r is { X: 10, Y: 20, Width: 20, Height: 5 }, $"{r}");
        r = DocumensoFieldGeometry.CanvasToPercent(1400, -50, 300, 150, 1500, 1500);
        Check("a drag off the page is clipped to the page", r is { X: 93.3333, Y: 0, Width: 6.6667, Height: 6.6667 }, $"{r}");
        Check("a rect entirely off the page places nothing",
              DocumensoFieldGeometry.CanvasToPercent(1600, 10, 50, 50, 1500, 1500) is null, "");
        Check("a zero canvas places nothing", DocumensoFieldGeometry.CanvasToPercent(1, 1, 5, 5, 0, 100) is null, "");
        var back = DocumensoFieldGeometry.PercentToCanvas(new DocumensoFieldRect(10, 20, 20, 5), 1187, 1536);
        Check("percent → canvas inverts it", Math.Abs(back.X - 118.7) < 1e-9 && Math.Abs(back.Y - 307.2) < 1e-9
              && Math.Abs(back.W - 237.4) < 1e-9 && Math.Abs(back.H - 76.8) < 1e-9, $"{back}");
        var d = DocumensoFieldGeometry.DefaultAt(DocumensoFieldType.Signature, 1480, 1490, 1187, 1536);
        Check("a click near the corner places a default-size field kept inside the page",
              d is { } dd && dd.X + dd.Width <= 100.0001 && dd.Y + dd.Height <= 100.0001 && dd.Width > 20, $"{d}");

        // Against the seal: a canvas rect on every quarter turn, with and without a crop, must burn
        // in where TDPdf's own canvas→PDF table says the rect is.
        var media = new PdfPageGeometry.PageBox(0, 0, 612, 792);
        var crops = new (string Name, PdfPageGeometry.PageBox? Box)[]
        {
            ("no CropBox", null),
            ("CropBox at the origin", new PdfPageGeometry.PageBox(0, 0, 500, 700)),
            ("CropBox offset 60,90", new PdfPageGeometry.PageBox(60, 90, 460, 610)),
        };
        foreach (var (name, crop) in crops)
        foreach (int rotate in new[] { 0, 90, 180, 270 })
        {
            var doc = new PdfDocument();
            var page = doc.AddPage();
            page.Elements["/MediaBox"] = BoxArray(media);
            if (crop is { } cb) page.Elements["/CropBox"] = BoxArray(cb);
            page.Elements["/Rotate"] = new PdfInteger(rotate);

            // The canvas the app would lay this page on (EnsureRenderDims / AnnotationCanvasSize).
            var (dw, dh) = PdfPageGeometry.DisplaySize(page);
            double scale = 1536 / Math.Max(dw, dh);
            double cw = Math.Round(dw * scale), ch = Math.Round(dh * scale);
            double x = cw * 0.21, y = ch * 0.63, w = cw * 0.27, h = ch * 0.06;

            var tdpdf = PdfPageGeometry.CanvasRectToPdf(page, x, y, w, h, cw, ch);
            var pct = DocumensoFieldGeometry.CanvasToPercent(x, y, w, h, cw, ch)!.Value;
            var sealed_ = SealModel(pct, media, crop, rotate);
            var box = PdfPageGeometry.VisibleBox(page);

            // Where it should land given Documenso's known blind spot: shifted by the box origin.
            double ex = DocumensoFieldGeometry.HasOffsetOrigin(box.X, box.Y) ? box.X : 0;
            double ey = DocumensoFieldGeometry.HasOffsetOrigin(box.X, box.Y) ? box.Y : 0;
            double drift = Math.Max(
                Math.Max(Math.Abs(sealed_.Left - (tdpdf.Left - ex)), Math.Abs(sealed_.Right - (tdpdf.Right - ex))),
                Math.Max(Math.Abs(sealed_.Bottom - (tdpdf.Bottom - ey)), Math.Abs(sealed_.Top - (tdpdf.Top - ey))));
            // 0.01 pt: the percentages are rounded to 4 decimals, which is ~0.0008 pt on this page.
            Check(ex == 0 && ey == 0
                    ? $"{name}, /Rotate {rotate}: Documenso seals the field exactly where it was placed"
                    : $"{name}, /Rotate {rotate}: Documenso seals it offset by the box origin (known v2.18 caveat)",
                  drift < 0.01, $"drift {drift:F4} pt, sealed L{sealed_.Left:F1} B{sealed_.Bottom:F1}");
        }
        Check("HasOffsetOrigin ignores float noise", !DocumensoFieldGeometry.HasOffsetOrigin(0.2, -0.3)
              && DocumensoFieldGeometry.HasOffsetOrigin(60, 0), "");
    }

    private static PdfArray BoxArray(PdfPageGeometry.PageBox b)
    {
        var a = new PdfArray();
        foreach (double v in new[] { b.X, b.Y, b.X + b.Width, b.Y + b.Height })
            a.Elements.Add(new PdfReal(v));
        return a;
    }

    // ── Placeholders ────────────────────────────────────────────────────────────────────────

    private static void Placeholders(Action<string, bool, string> Check, string tmp)
    {
        var p = DocumensoPlaceholders.Parse("Sign here {{signature, r1}} on {{ DATE ,R2, required=true }} x {{name}} {{bogus, r1}} {{text, someone}}", 3);
        Check("Documenso's grammar: two valid placeholders, the rest skipped", p.Count == 2, string.Join(" | ", p));
        Check("type, recipient number and page parsed (case-insensitive, trimmed, options ignored)",
              p.Count == 2 && p[0].Type == DocumensoFieldType.Signature && p[0].RecipientNumber == 1 && p[0].PageNumber == 3
              && p[1].Type == DocumensoFieldType.Date && p[1].RecipientNumber == 2 && p[0].IsSignature && !p[1].IsSignature,
              string.Join(" | ", p));
        Check("FREE_SIGNATURE counts as a signature",
              DocumensoPlaceholders.Parse("{{free_signature,r4}}", 1) is [{ IsSignature: true, RecipientNumber: 4 }], "");
        Check("nested or unterminated braces are not placeholders",
              DocumensoPlaceholders.Parse("{{signature, r1} {{signature r1}}", 1).Count == 0, "");

        // Through a real PDF and PdfPig, the way the app reads it.
        string path = Path.Combine(tmp, "esign-placeholders.pdf");
        {
            var doc = new PdfDocument();
            var font = new XFont("Helvetica", 11);
            var p1 = doc.AddPage();
            using (var g = XGraphics.FromPdfPage(p1))
            {
                g.DrawString("Tenant: {{signature, r1}}", font, XBrushes.Black, 60, 600);
                g.DrawString("Landlord: {{signature, r2}}   Date: {{date, r2}}", font, XBrushes.Black, 60, 640);
            }
            var p2 = doc.AddPage();
            using (var g = XGraphics.FromPdfPage(p2))
                g.DrawString("Initial: {{initials,r1}} and a stray {{name}}", font, XBrushes.Black, 60, 100);
            doc.Save(path);
        }
        var scanned = DocumensoPlaceholders.Scan(path);
        Check("a generated PDF yields its four placeholders", scanned.Count == 4, string.Join(" | ", scanned.Select(s => s.Text)));
        Check("pages and recipients come out right",
              scanned.Count(s => s.PageNumber == 1) == 3 && scanned.Single(s => s.PageNumber == 2).Type == DocumensoFieldType.Initials
              && scanned.Count(s => s.IsSignature) == 2 && scanned.Max(s => s.RecipientNumber) == 2, "");
        Check("an unreadable file yields nothing rather than throwing",
              DocumensoPlaceholders.Scan(Path.Combine(tmp, "no-such-file.pdf")).Count == 0, "");
    }

    // ── Check ───────────────────────────────────────────────────────────────────────────────

    private static ESignSendSession Session(params (string name, string email, DocumensoRecipientRole role)[] rows)
    {
        var s = new ESignSendSession { Title = "Lease" };
        foreach (var (n, e, r) in rows) s.Recipients.Add(new ESignRecipientEntry { Name = n, Email = e, Role = r });
        return s;
    }

    private static ESignFieldDraft Field(ESignRecipientEntry r, DocumensoFieldType t, int page = 0)
        => new(r.Key, t, page, new DocumensoFieldRect(10, 80, 30, 6));

    private static void CheckRules(Action<string, bool, string> Check)
    {
        var none = Array.Empty<DocumensoPlaceholder>();

        var s = Session();
        s.Title = " ";
        var c = DocumensoSendPlan.Check(s, none);
        Check("no title and no recipients are both errors", !c.CanSend && c.Errors.Count == 2, string.Join(" / ", c.Errors));

        s = Session(("Ann", "ann@example.com", DocumensoRecipientRole.Signer));
        c = DocumensoSendPlan.Check(s, none);
        Check("a signer with no signature field blocks the send (Documenso refuses it)",
              !c.CanSend && c.Errors.Single().Contains("Ann has no Signature field"), string.Join(" / ", c.Errors));

        s.Fields.Add(Field(s.Recipients[0], DocumensoFieldType.Date));
        Check("a Date field does not count as a signature", !DocumensoSendPlan.Check(s, none).CanSend, "");
        s.Fields.Add(Field(s.Recipients[0], DocumensoFieldType.Signature));
        c = DocumensoSendPlan.Check(s, none);
        Check("a placed Signature field satisfies the signer", c.CanSend && c.Warnings.Count == 0, string.Join(" / ", c.Errors));

        s = Session(("Ann", "ann@example.com", DocumensoRecipientRole.Signer), ("Bob", "bob@example.com", DocumensoRecipientRole.Signer));
        var ph = DocumensoPlaceholders.Parse("{{signature,r1}} {{signature,r2}}", 1);
        Check("signature placeholders satisfy signers by position", DocumensoSendPlan.Check(s, ph).CanSend, "");
        s.Move(1, -1);
        Check("reordering keeps them satisfied (placeholders follow the order)", DocumensoSendPlan.Check(s, ph).CanSend, "");
        c = DocumensoSendPlan.Check(s, DocumensoPlaceholders.Parse("{{signature,r1}} {{signature,r2}} {{date,r3}}", 1));
        Check("a placeholder beyond the recipient count is an error",
              !c.CanSend && c.Errors.Any(e => e.Contains("r3")), string.Join(" / ", c.Errors));

        s = Session(("", "not-an-email", DocumensoRecipientRole.Viewer), ("", "", DocumensoRecipientRole.Cc),
                    ("x", "Dup@Example.com", DocumensoRecipientRole.Approver), ("y", "dup@example.com", DocumensoRecipientRole.Approver));
        c = DocumensoSendPlan.Check(s, none);
        Check("bad, missing and duplicate (case-insensitive) emails are each reported",
              c.Errors.Count == 3 && c.Errors.Any(e => e.Contains("not a valid")) && c.Errors.Any(e => e.Contains("Recipient 2 needs"))
              && c.Errors.Any(e => e.Contains("more than once")), string.Join(" / ", c.Errors));
        Check("viewers, CCs and approvers need no signature field", !c.Errors.Any(e => e.Contains("Signature field")), "");

        Check("email shape", DocumensoSendPlan.IsValidEmail("a.b+c@sub.example.org") && !DocumensoSendPlan.IsValidEmail("a@b")
              && !DocumensoSendPlan.IsValidEmail("a b@c.com") && !DocumensoSendPlan.IsValidEmail("a@b.com.")
              && !DocumensoSendPlan.IsValidEmail("@b.com"), "");

        s = Session(("Ann", "ann@example.com", DocumensoRecipientRole.Signer), ("Cy", "cy@example.com", DocumensoRecipientRole.Cc));
        s.Fields.Add(Field(s.Recipients[0], DocumensoFieldType.Signature, page: 2));
        s.Fields.Add(Field(s.Recipients[1], DocumensoFieldType.Text, page: 0));
        c = DocumensoSendPlan.Check(s, none, page => page == 2);
        Check("a field for a CC warns that it will be left out", c.CanSend && c.Warnings.Any(w => w.Contains("Cy") && w.Contains("left out")),
              string.Join(" / ", c.Warnings));
        Check("a field on a page with an offset box warns, naming the 1-based page",
              c.Warnings.Any(w => w.StartsWith("Page 3 is cropped")), string.Join(" / ", c.Warnings));

        int dropped = s.RemoveRecipient(s.Recipients[1].Key);
        Check("removing a recipient removes their fields", dropped == 1 && s.Fields.Count == 1 && s.Recipients.Count == 1, "");
    }

    private static void Payload(Action<string, bool, string> Check)
    {
        var s = Session(("Ann", " ann@example.com ", DocumensoRecipientRole.Signer),
                        ("Cy", "cy@example.com", DocumensoRecipientRole.Cc),
                        ("Bob", "bob@example.com", DocumensoRecipientRole.Approver));
        s.SequentialOrder = true;
        s.Subject = "Please sign";
        s.Message = "  ";
        s.Fields.Add(new ESignFieldDraft(s.Recipients[0].Key, DocumensoFieldType.Signature, 1, new DocumensoFieldRect(12.5, 80, 30, 6)));
        s.Fields.Add(new ESignFieldDraft(s.Recipients[1].Key, DocumensoFieldType.Text, 0, new DocumensoFieldRect(1, 1, 1, 1)));

        var req = DocumensoSendPlan.BuildCreateRequest(s, "America/Chicago");
        using var content = DocumensoClient.BuildCreateEnvelopeContent(req, new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7\n")), "Lease.pdf");
        string body = content.ReadAsStringAsync().GetAwaiter().GetResult();
        int start = body.IndexOf("{\"title\"", StringComparison.Ordinal);
        int end = body.IndexOf("\r\n--", start, StringComparison.Ordinal);
        var p = JsonDocument.Parse(body[start..end]).RootElement;
        var rs = p.GetProperty("recipients");

        Check("payload carries the recipients in list order", rs.GetArrayLength() == 3
              && rs[0].GetProperty("email").GetString() == "ann@example.com" && rs[1].GetProperty("role").GetString() == "CC"
              && rs[2].GetProperty("role").GetString() == "APPROVER", rs.ToString());
        Check("sequential: signers/approvers numbered 1, 2 skipping the CC, who has none",
              rs[0].GetProperty("signingOrder").GetInt32() == 1 && !rs[1].TryGetProperty("signingOrder", out _)
              && rs[2].GetProperty("signingOrder").GetInt32() == 2, "");
        var f = rs[0].GetProperty("fields")[0];
        Check("an inline field: type, 1-based page, positionX/Y/width/height, no recipientId",
              f.GetProperty("type").GetString() == "SIGNATURE" && f.GetProperty("page").GetInt32() == 2
              && f.GetProperty("positionX").GetDouble() == 12.5 && f.GetProperty("positionY").GetDouble() == 80
              && f.GetProperty("width").GetDouble() == 30 && f.GetProperty("height").GetDouble() == 6
              && !f.TryGetProperty("recipientId", out _) && !f.TryGetProperty("identifier", out _), f.ToString());
        Check("the CC's field is left out, and a recipient with no fields sends no 'fields' key",
              !rs[1].TryGetProperty("fields", out _) && !rs[2].TryGetProperty("fields", out _), "");
        var meta = p.GetProperty("meta");
        Check("meta: subject, SEQUENTIAL, timezone; a blank message is omitted",
              meta.GetProperty("subject").GetString() == "Please sign" && meta.GetProperty("signingOrder").GetString() == "SEQUENTIAL"
              && meta.GetProperty("timezone").GetString() == "America/Chicago" && !meta.TryGetProperty("message", out _), meta.ToString());
        Check("externalId is an opaque tdpdf- id, not the title or a path",
              p.GetProperty("externalId").GetString() is { } x && x.StartsWith("tdpdf-") && x.Length == 38 && !x.Contains("Lease"), "");

        s.SequentialOrder = false;
        var parallel = DocumensoSendPlan.BuildCreateRequest(s);
        Check("parallel: no signingOrder on anyone, meta PARALLEL",
              parallel.Recipients!.All(r => r.SigningOrder is null) && parallel.Meta!.SigningOrder == DocumensoSigningOrder.Parallel, "");

        s.Title = new string('t', 300);
        Check("the title is cut to the API's 255", DocumensoSendPlan.BuildCreateRequest(s).Title.Length == 255, "");
    }

    private static void Links(Action<string, bool, string> Check)
    {
        var s = Session(("Ann", "ann@example.com", DocumensoRecipientRole.Signer), ("", "cy@example.com", DocumensoRecipientRole.Cc),
                        ("", "bob@example.com", DocumensoRecipientRole.Approver));
        var dist = new List<DocumensoDistributedRecipient>
        {
            new() { Id = 3, Email = "BOB@example.com", SigningUrl = Base + "/sign/b" },
            new() { Id = 9, Email = "team-default@example.com", SigningUrl = Base + "/sign/t" },
            new() { Id = 1, Email = "ann@example.com", SigningUrl = Base + "/sign/a" },
            new() { Id = 2, Email = "cy@example.com", SigningUrl = Base + "/sign/c" },
        };
        var links = DocumensoSendPlan.MatchLinks(s.Recipients, dist);
        Check("links matched by email regardless of server order; CC and unknown team defaults left out",
              links.Count == 2 && links[0].Label == "Ann" && links[0].SigningUrl.EndsWith("/a")
              && links[1].Label == "bob@example.com" && links[1].SigningUrl.EndsWith("/b"),
              string.Join(" | ", links.Select(l => $"{l.Label}={l.SigningUrl}")));
    }

    // ── Pipeline ────────────────────────────────────────────────────────────────────────────

    private static async Task Pipeline(Action<string, bool, string> Check)
    {
        byte[] pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%fixture\n");
        var req = new DocumensoCreateEnvelopeRequest { Title = "T", Recipients = new[] { new DocumensoCreateRecipient { Email = "a@example.com" } } };

        // Success: exactly create then distribute.
        var h = new Documenso.Recorder();
        var client = new DocumensoClient(Base, Token, () => h);
        h.Respond(HttpStatusCode.OK, """{"id":"envelope_ok"}""");
        h.Respond(HttpStatusCode.OK, """{"success":true,"id":"envelope_ok","recipients":[{"id":1,"name":"A","email":"a@example.com","token":"t","role":"SIGNER","signingOrder":null,"signingUrl":"https://sign.tdpdf.com/sign/t"}]}""");
        var stages = new List<DocumensoSendStage>();
        var res = await DocumensoSender.SendAsync(client, req, pdf, "doc.pdf", new SyncProgress(stages.Add), CancellationToken.None);
        Check("success: create then distribute, nothing else",
              res.Ok && res.Outcome == "ok" && h.All.Count == 2 && h.All[0].Url.EndsWith("/envelope/create") && h.All[1].Url.EndsWith("/envelope/distribute"),
              string.Join(", ", h.All.Select(a => a.Url)));
        Check("success: recipients with their signing links, and no rollback",
              res.EnvelopeId == "envelope_ok" && res.Recipients.Single().SigningUrl.EndsWith("/sign/t") && res.RolledBack is null, "");
        Check("progress reports Uploading then Sending",
              stages.SequenceEqual(new[] { DocumensoSendStage.Uploading, DocumensoSendStage.Sending }), string.Join(",", stages));

        // Distribute fails → the envelope is deleted again.
        h = new Documenso.Recorder();
        client = new DocumensoClient(Base, Token, () => h);
        h.Respond(HttpStatusCode.OK, """{"id":"envelope_half"}""");
        h.Respond(HttpStatusCode.BadRequest, """{"code":"MISSING_SIGNATURE_FIELD"}""");
        h.Respond(HttpStatusCode.OK, """{"success":true}""");
        res = await DocumensoSender.SendAsync(client, req, pdf, "doc.pdf", null, CancellationToken.None);
        var del = h.All.Last();
        Check("distribute fails → POST /envelope/delete for that envelope, and the result says it was withdrawn",
              !res.Ok && res.RolledBack == true && del.Url.EndsWith("/envelope/delete")
              && JsonDocument.Parse(del.Body).RootElement.GetProperty("envelopeId").GetString() == "envelope_half",
              string.Join(", ", h.All.Select(a => a.Url)));
        Check("…and carries the failure and its code, outcome 'rejected'",
              res.Failure?.ErrorCode == "MISSING_SIGNATURE_FIELD" && res.Outcome == "rejected", res.Outcome);

        // Create fails → nothing to roll back.
        h = new Documenso.Recorder();
        client = new DocumensoClient(Base, Token, () => h);
        h.Respond(HttpStatusCode.Unauthorized, """{"code":"UNAUTHORIZED"}""");
        res = await DocumensoSender.SendAsync(client, req, pdf, "doc.pdf", null, CancellationToken.None);
        Check("create fails → no delete, RolledBack null, outcome authfailed",
              h.All.Count == 1 && res.RolledBack is null && res.Outcome == "authfailed", $"{h.All.Count} requests");

        // The rollback itself fails → reported as such, still no exception.
        h = new Documenso.Recorder();
        client = new DocumensoClient(Base, Token, () => h);
        h.Respond(HttpStatusCode.OK, """{"id":"envelope_stuck"}""");
        h.Respond(HttpStatusCode.BadGateway, "<html/>");
        h.Respond(HttpStatusCode.BadGateway, "<html/>");
        res = await DocumensoSender.SendAsync(client, req, pdf, "doc.pdf", null, CancellationToken.None);
        Check("a failed rollback is reported (RolledBack false), outcome server",
              res.RolledBack == false && res.Outcome == "server" && h.All.Count == 3, "");

        // Esc after the envelope exists, before distribute → withdrawn, never distributed.
        h = new Documenso.Recorder();
        client = new DocumensoClient(Base, Token, () => h);
        using var cts = new CancellationTokenSource();
        h.Respond(HttpStatusCode.OK, """{"id":"envelope_esc"}""");
        h.Respond(HttpStatusCode.OK, """{"success":true}""");
        // The user presses Esc just as the upload finishes and the progress line flips to "Sending".
        var escAtBrink = new SyncProgress(stage => { if (stage == DocumensoSendStage.Sending) cts.Cancel(); });
        res = await DocumensoSender.SendAsync(client, req, pdf, "doc.pdf", escAtBrink, cts.Token);
        Check("cancel after create: deleted, distribute never called, outcome cancelled",
              res.Cancelled && res.RolledBack == true && res.Outcome == "cancelled"
              && !h.All.Any(a => a.Url.EndsWith("/distribute")) && h.All.Last().Url.EndsWith("/envelope/delete"),
              string.Join(", ", h.All.Select(a => a.Url)));
    }

    private sealed class SyncProgress(Action<DocumensoSendStage> report) : IProgress<DocumensoSendStage>
    {
        public void Report(DocumensoSendStage value) => report(value);
    }

    // ── Store ───────────────────────────────────────────────────────────────────────────────

    private static void Store(Action<string, bool, string> Check, string tmp)
    {
        string path = Path.Combine(tmp, "esign-requests.json");
        if (File.Exists(path)) File.Delete(path);
        Check("a missing file loads as empty", DocumensoRequestStore.Load(path).Count == 0, "");

        var t0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 205; i++)
            DocumensoRequestStore.Add(new DocumensoRequestStore.Entry($"envelope_{i}", $"Doc {i}", t0.AddMinutes(i), 1), path);
        var all = DocumensoRequestStore.Load(path);
        Check("capped at 200, newest first", all.Count == 200 && all[0].EnvelopeId == "envelope_204" && all[^1].EnvelopeId == "envelope_5",
              $"{all.Count}: {all[0].EnvelopeId}..{all[^1].EnvelopeId}");

        DocumensoRequestStore.Add(new DocumensoRequestStore.Entry("envelope_100", "Again", t0.AddDays(1), 3), path);
        all = DocumensoRequestStore.Load(path);
        Check("re-adding an id replaces it rather than duplicating", all.Count(e => e.EnvelopeId == "envelope_100") == 1
              && all[0].Title == "Again" && all[0].RecipientCount == 3, "");

        string text = File.ReadAllText(path);
        Check("the file holds no email address", !text.Contains('@'), "");

        File.WriteAllText(path, "{ not json");
        Check("a corrupt file loads as empty", DocumensoRequestStore.Load(path).Count == 0, "");
        Check("…and a send still records over it", DocumensoRequestStore.Add(new("envelope_x", "X", t0, 1), path)
              && DocumensoRequestStore.Load(path).Single().EnvelopeId == "envelope_x", "");
        File.WriteAllText(path, """{"requests":[{"envelopeId":"","title":"no id"},{"title":null,"envelopeId":"e1","recipientCount":-4}]}""");
        var odd = DocumensoRequestStore.Load(path);
        Check("entries without an id are dropped; nulls and negatives tolerated",
              odd.Count == 1 && odd[0].Title == "" && odd[0].RecipientCount == 0, "");
    }
}

/// <summary>
/// Send for Signature against the real instance: a one-page PDF, one signer
/// (TDPDF_DOCUMENSO_TEST_EMAIL), one Signature field placed through the canvas geometry, sent,
/// read back, then deleted. SKIPs unless both TDPDF_DOCUMENSO_TEST_TOKEN and
/// TDPDF_DOCUMENSO_TEST_EMAIL are set. It really emails that address — use one you own.
/// </summary>
internal static class DocumensoSendLive
{
    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("Electronic Signature — live send");
        string? token = Environment.GetEnvironmentVariable("TDPDF_DOCUMENSO_TEST_TOKEN");
        string? email = Environment.GetEnvironmentVariable("TDPDF_DOCUMENSO_TEST_EMAIL");
        if (string.IsNullOrWhiteSpace(token)) { Console.WriteLine("  SKIP  TDPDF_DOCUMENSO_TEST_TOKEN is not set"); return; }
        if (string.IsNullOrWhiteSpace(email)) { Console.WriteLine("  SKIP  TDPDF_DOCUMENSO_TEST_EMAIL is not set"); return; }
        string baseUrl = Environment.GetEnvironmentVariable("TDPDF_DOCUMENSO_TEST_BASEURL") is { Length: > 0 } b
            ? b : DocumensoConfig.DefaultBaseUrl;
        RunAsync(Check, tmp, baseUrl, token.Trim(), email.Trim()).GetAwaiter().GetResult();
    }

    private static async Task RunAsync(Action<string, bool, string> Check, string tmp, string baseUrl, string token, string email)
    {
        string path = Path.Combine(tmp, "esign-live.pdf");
        {
            var doc = new PdfDocument();
            var page = doc.AddPage();
            using var g = XGraphics.FromPdfPage(page);
            g.DrawString("TDPdf live test — safe to delete", new XFont("Helvetica", 14), XBrushes.Black, 60, 100);
            doc.Save(path);
        }

        var s = new ESignSendSession { Title = "TDPdf live test — safe to delete" };
        var me = new ESignRecipientEntry { Name = "TDPdf live test", Email = email };
        s.Recipients.Add(me);
        // As if dragged on the 1187x1536 canvas of a US Letter page.
        var rect = DocumensoFieldGeometry.CanvasToPercent(200, 1100, 330, 90, 1187, 1536)!.Value;
        s.Fields.Add(new ESignFieldDraft(me.Key, DocumensoFieldType.Signature, 0, rect));
        Check("live: the session passes the check", DocumensoSendPlan.Check(s, Array.Empty<DocumensoPlaceholder>()).CanSend, "");

        var client = new DocumensoClient(baseUrl, token);
        var res = await DocumensoSender.SendAsync(client, DocumensoSendPlan.BuildCreateRequest(s, DocumensoSendPlan.LocalIanaTimezone()),
            File.ReadAllBytes(path), "tdpdf-live-test.pdf", null, CancellationToken.None);
        Check("live: sent", res.Ok, res.Ok ? "" : $"{res.Outcome} {res.Failure?.StatusCode} {res.Failure?.ErrorCode} rolledBack={res.RolledBack}");
        if (!res.Ok || res.EnvelopeId is null) return;

        try
        {
            Check("live: distribute returned a signing link for the signer",
                  DocumensoSendPlan.MatchLinks(s.Recipients, res.Recipients).Count == 1, "");
            var env = await client.GetEnvelopeAsync(res.EnvelopeId, CancellationToken.None);
            Check("live: the envelope reads back PENDING", env.Status == DocumensoEnvelopeStatus.Pending, env.Status.ToString());
            var f = env.Fields.SingleOrDefault();
            bool roundTrips = f is not null && f.Type == DocumensoFieldType.Signature && f.Page == 1
                && Math.Abs(f.PositionX - rect.X) < 0.01 && Math.Abs(f.PositionY - rect.Y) < 0.01
                && Math.Abs(f.Width - rect.Width) < 0.01 && Math.Abs(f.Height - rect.Height) < 0.01;
            Check("live: the field's position round-trips", roundTrips,
                  f is null ? "no field" : $"sent {rect}, got {f.PositionX},{f.PositionY} {f.Width}x{f.Height} p{f.Page}");
            Check("live: the owner is readable (how the account email is learned)", !string.IsNullOrEmpty(env.User?.Email), "");
        }
        finally
        {
            // Never leave a test envelope behind: delete, and if the server will not delete a
            // pending one, cancel it and try again.
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
            Check("live: the test envelope was deleted", gone, "");
        }
    }
}
