// MainWindow — Electronic Signature: Sign This Document (design §5.4b).
// Send for Signature with exactly one recipient — you, as Signer — and one signature field,
// followed by a handoff to your signing page in the default browser. TDPdf never signs:
// Documenso has no public signing endpoint (design §1.6), so the signature is made on the
// signing service's page and sealed by the signing service. The window that waits for it is
// Services/DocumensoSelfSignWindow; the pipeline is the one Send for Signature runs
// (ESignRunSendAsync), so a self-signed request is tracked, rolled back on failure and shown in
// Signature Requests exactly like a send.

using System.IO;
using System.Threading.Tasks;
using System.Windows;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow
    {
        private async Task ESignSelfAsync()
        {
            if (!ESignRequireDocument()) return;
            if (DocumensoSelfSignWindow.TryActivate()) return;
            if (_eSignPlacement is { Palette: { } palette })
            {
                palette.Activate();
                return;
            }
            if (!await ESignEnsureSavedAsync()) return;
            if (ESignConnectedClient() is not { } client) return;
            if (_doc is null || ESignSourcePath() is not { } path)
            {
                SetStatus("Open a PDF first.");
                return;
            }

            var ctx = _ctx;
            var doc = _doc;
            string host = DocumensoConfig.HostOf(client.BaseUrl);

            // You are the one recipient, so TDPdf needs your address. Documenso v2 cannot say who a
            // token belongs to until it has created something (see DocumensoAccount), so ask once.
            var meta = DocumensoCredentialStore.ReadMetadataFor(client.BaseUrl);
            string? email = meta?.AccountEmail;
            if (string.IsNullOrWhiteSpace(email))
            {
                email = DocumensoSelfSignWindow.AskEmail(this, host);
                if (email is null)
                {
                    SetStatus("Not signed. Nothing was sent.");
                    return;
                }
                DocumensoCredentialStore.RememberAccount(client.BaseUrl, new DocumensoAccount(email, null, null));
            }

            string title = "";
            try { title = Path.GetFileNameWithoutExtension(ctx.DisplayName) ?? ""; } catch { }
            var session = DocumensoSelfSignPlan.NewSession(title, email, meta?.AccountName);
            int adopted = DocumensoSelfSignPlan.AdoptFields(session, ctx.ESignSession);

            SetStatus("Electronic Signature: reading the document…");
            var placeholders = await Task.Run(() => DocumensoPlaceholders.Scan(path));
            SetStatus("Ready");
            if (!ReferenceEquals(ctx, _ctx) || ctx.Doc is null) return;

            var source = DocumensoSelfSignPlan.Source(session, placeholders);
            if (source == ESignSelfFieldSource.NeedsPlacement)
            {
                // Phase 2's placement, on this session: you are the only recipient in the palette
                // and Signature is the preselected type.
                var end = await ESignPlaceFieldsAsync(session);
                if (end != ESignPlacementEnd.Done || !ReferenceEquals(ctx, _ctx) || ctx.Doc is null)
                {
                    SetStatus("Not signed. Nothing was sent.");
                    return;
                }
                // Anything edited while placing makes the saved file stale; it is the saved file that goes.
                if ((_isDirty || PendingRedactionCount > 0) && !await ESignEnsureSavedAsync()) return;
                source = DocumensoSelfSignPlan.Source(session, placeholders);
                if (source == ESignSelfFieldSource.NeedsPlacement)
                {
                    SetStatus("No signature field was placed, so nothing was sent.");
                    return;
                }
            }

            var offsetPages = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < doc.PageCount; i++)
            {
                try
                {
                    var box = PdfPageGeometry.VisibleBox(doc.Pages[i]);
                    if (DocumensoFieldGeometry.HasOffsetOrigin(box.X, box.Y)) offsetPages.Add(i);
                }
                catch { /* a malformed page box: no warning rather than a crash */ }
            }
            var check = DocumensoSendPlan.Check(session, placeholders, offsetPages.Contains);
            if (!check.CanSend)
            {
                // Not a network failure — a document that cannot be self-signed as it stands (e.g.
                // placeholders for r2). Said once, plainly, before anything leaves the machine.
                TdpDialog.Show(this, string.Join("\n\n", check.Errors), "Sign This Document",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus("Not signed. Nothing was sent.");
                return;
            }

            int fieldCount = session.Fields.Count;
            string note = source == ESignSelfFieldSource.Placeholder && fieldCount == 0
                ? "Your signature goes where the document says {{signature, r1}}."
                : $"Your signature goes in the {fieldCount} field{(fieldCount == 1 ? "" : "s")} " +
                  $"{(adopted > 0 ? "you placed for yourself in Send for Signature" : "placed on the page")}.";
            if (check.Warnings.Count > 0) note += " " + string.Join(" ", check.Warnings);

            string signer = email;
            DocumensoSelfSignWindow.Show(this, new DocumensoSelfSignWindow.Inputs
            {
                Host = host,
                DocumentName = string.IsNullOrWhiteSpace(ctx.DisplayName) ? "this document" : ctx.DisplayName,
                Email = signer,
                FieldNote = note,
                SendAsync = async (progress, ct) =>
                {
                    var a = await ESignRunSendAsync(client, ctx, session, path, doc.PageCount, progress, ct, "DocumensoSelfSign");
                    if (!a.Ok) return new DocumensoSelfSignWindow.SendOutcome(false, a.Error, null, null);
                    SetStatus($"Sent to you for signature on {host} — finish signing in your browser.");
                    return new DocumensoSelfSignWindow.SendOutcome(true, null, a.EnvelopeId,
                        DocumensoSelfSignPlan.SigningUrlFor(signer, a.Links, session.Recipients));
                },
                GetEnvelopeAsync = (id, ct) => client.GetEnvelopeAsync(id, ct),
                DownloadSignedAsync = async (owner, env) =>
                {
                    var r = await ESignDownloadArtifactAsync(owner, client, env, DocumensoArtifact.SignedCopy, openAfter: false);
                    return r is null ? (null, "") : (r.Path, r.Message);
                },
                OpenAsync = OpenInTabAsync,
            });
        }
    }
}
