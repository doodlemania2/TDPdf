// MainWindow — Electronic Signature: Signature Requests, downloads, and the signed-document state.
// Design: docs/design/esignature-documenso.md §5.4(c) and §5.5.
//
// Signature Requests itself is Services/DocumensoRequestsWindow (modeless, polls only while open);
// this partial supplies what it cannot do on its own — saving a download where the user chooses and
// opening it in a tab — and owns the per-tab "this file is digitally signed" state behind the
// footer indicator and the Document Info row. The save-time warning that a TDPdf save removes the
// signature is ConfirmSaveOverDigitalSignature in MainWindow.Files.cs (commit 76aa9a1a); it is not
// duplicated here.

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow
    {
        // ============================================================
        // Signature Requests (design §5.4c)
        // ============================================================

        private Task ESignRequestsAsync()
        {
            if (ESignConnectedClient() is not { } client) return Task.CompletedTask;
            var meta = DocumensoCredentialStore.ReadMetadataFor(client.BaseUrl);
            DocumensoRequestsWindow.Show(this, new DocumensoRequestsWindow.Inputs
            {
                BaseUrl = client.BaseUrl,
                Host = DocumensoConfig.HostOf(client.BaseUrl),
                StoredTeamUrl = meta?.TeamUrl,
                TryClient = () => client,
                Connect = ESignReconnect,
                DownloadAsync = async (owner, c, env, what) => (await ESignDownloadArtifactAsync(owner, c, env, what, openAfter: true))?.Message,
            });
            return Task.CompletedTask;
        }

        /// <summary>
        /// The Connect dialog over <paramref name="owner"/>, for a window that found its sign-in
        /// rejected. A fresh client on success; null when the user did not connect.
        /// </summary>
        private DocumensoClient? ESignReconnect(Window owner)
        {
            if (DocumensoConfig.TryResolveBaseUrl() is not { } resolved) return null;
            if (DocumensoConnectWindow.Show(owner, resolved.BaseUrl) != DocumensoConnectWindow.Outcome.Connected) return null;
            return DocumensoCredentialStore.TryLoadToken(resolved.BaseUrl) is { } token
                ? new DocumensoClient(resolved.BaseUrl, token)
                : null;
        }

        // ============================================================
        // Download (design §5.5 step 1)
        // ============================================================

        /// <summary>What a download produced: the sentence to show, and the saved file when there is one.</summary>
        private sealed record ESignDownloaded(string Message, string? Path);

        /// <summary>
        /// Saves one PDF of an envelope where the user chooses and, when
        /// <paramref name="openAfter"/>, opens it in a new tab. Returns the sentence to show the
        /// user (and the saved path), or null when they cancelled the Save dialog. Throws
        /// <see cref="DocumensoApiException"/> for the caller to show inline.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The client writes to <c>.part</c> and moves it into place with a delete-then-move
        /// fallback (never <c>File.Replace</c>, which OneDrive folders refuse), and checks the
        /// bytes are a PDF before anything is written under the real name.
        /// </para>
        /// <para>
        /// A signed copy's SHA-256 is recorded against the envelope (when this device sent it), so
        /// Verify Signatures can later recognise that exact file without guessing from its name.
        /// </para>
        /// <para>
        /// An envelope can hold more than one document; TDPdf sends exactly one, so the first item
        /// (by order) is the signed copy, and the sentence says so when there were more.
        /// </para>
        /// </remarks>
        private async Task<ESignDownloaded?> ESignDownloadArtifactAsync(Window owner, DocumensoClient client,
            DocumensoEnvelope env, DocumensoArtifact what, bool openAfter)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "PDF files|*.pdf",
                DefaultExt = ".pdf",
                AddExtension = true,
                FileName = DocumensoRequestView.DefaultFileName(env.Title, what),
                Title = what switch
                {
                    DocumensoArtifact.Certificate => "Save certificate of completion",
                    DocumensoArtifact.AuditLog => "Save audit log",
                    _ => "Save signed copy",
                },
            };
            if (dlg.ShowDialog(owner) != true) return null;
            string dest = dlg.FileName;
            string name = Path.GetFileName(dest);
            string extra = "";

            SetStatus($"Electronic Signature: downloading {name}…");
            try
            {
                switch (what)
                {
                    case DocumensoArtifact.SignedCopy:
                        // The list endpoint carries no envelope items; get / get-many do.
                        var full = env.EnvelopeItems.Count > 0 ? env : await client.GetEnvelopeAsync(env.Id, CancellationToken.None);
                        if (full.Status != DocumensoEnvelopeStatus.Completed)
                            return new("That request is not completed yet, so there is no signed copy to download.", null);
                        var items = full.EnvelopeItems.OrderBy(i => i.Order).ToList();
                        if (items.Count == 0) return new($"{DocumensoConfig.HostOf(client.BaseUrl)} returned no document for that request.", null);
                        if (items.Count > 1) extra = $" The request holds {items.Count} documents; this is the first.";
                        await client.DownloadItemAsync(items[0].Id, DocumensoDownloadVersion.Signed, dest, CancellationToken.None);
                        try
                        {
                            string sha = await Task.Run(() => System.Convert.ToHexString(
                                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(dest))).ToLowerInvariant());
                            DocumensoRequestStore.RecordSignedCopy(env.Id, sha);
                        }
                        catch { /* only costs Verify its exact match; the download itself succeeded */ }
                        break;
                    case DocumensoArtifact.Certificate:
                        await client.DownloadCertificateAsync(env.Id, dest, CancellationToken.None);
                        break;
                    default:
                        await client.DownloadAuditLogAsync(env.Id, dest, CancellationToken.None);
                        break;
                }
            }
            catch (System.Exception ex) when (ex is IOException or System.UnauthorizedAccessException)
            {
                SetStatus("Electronic Signature: the download could not be saved.");
                return new($"TDPdf could not write {name}. It may be open in another program, or the folder may be read-only.", null);
            }
            catch (DocumensoApiException)
            {
                SetStatus("Electronic Signature: the download did not complete.");
                throw;
            }

            SetStatus($"Saved {name}.");
            if (openAfter) await OpenInTabAsync(dest);
            return new($"Saved {name}{(openAfter ? " and opened it" : "")}.{extra}", dest);
        }

        // ============================================================
        // The signed-document state (design §5.5 steps 2 and 4)
        // ============================================================

        /// <summary>
        /// Recomputes <c>HasDigitalSignature</c> for the active tab from the live document, then
        /// redraws the footer indicator. Called when a document finishes opening and after every
        /// save — a TDPdf save strips the signature from the live document (ScrubDeadSignatures),
        /// so after one the tab is no longer signed. Uses the same walk as the save warning and the
        /// scrub (<see cref="PdfSignatureScan.SignedFields"/>), so the three can never disagree.
        /// No cryptography: a signature dictionary being present is all this means.
        /// </summary>
        private void ESignRefreshSignedState()
        {
            _ctx.HasDigitalSignature = _doc is not null && PdfSignatureScan.SignedFields(_doc).Count > 0;
            ESignUpdateSignedBadge();
        }

        /// <summary>Shows or hides the footer's "Digitally signed" indicator for the active tab.</summary>
        private void ESignUpdateSignedBadge()
        {
            if (_eSignSignedBadge is null) return;   // before the constructor's FindName pass
            _eSignSignedBadge.Visibility = _doc is not null && _ctx.HasDigitalSignature ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ESignSignedBadge_Click(object sender, RoutedEventArgs e) => ShowDocumentInfoDialog();
    }
}
