// MainWindow — Electronic Signature: Verify Signatures and Document Info's "Digital signatures".
// Design: docs/design/esignature-documenso.md §6 Phase 5 and §7.1.
//
// Informational only. Services/PdfSignatureInspector reads what each signature states — signer and
// issuer from the CMS certificate, the claimed time, the type, whether its byte range reaches the
// end of the file — and never checks a signature: no CheckSignature, no chain, no revocation, no
// digest. Nothing here says "valid". The evidence that counts is the signing service's certificate
// of completion and audit log, offered for a request this device sent, and the user's own PDF
// reader, which actually verifies.

using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow
    {
        /// <summary>
        /// Verify Signatures. Needs no sign-in for the local read; connecting is asked for only if the
        /// user asks for the certificate or audit log.
        /// </summary>
        private async Task ESignVerifyAsync()
        {
            if (!ESignRequireDocument()) return;
            if (ESignOnDiskPath() is not { } path)
            {
                SetStatus("Save the document first — Verify Signatures reads the file on disk.");
                return;
            }
            var ctx = _ctx;
            SetStatus("Reading the document's signatures…");
            var (report, bytes) = await Task.Run(() => ESignInspectFile(path));
            if (!ReferenceEquals(ctx, _ctx)) return;
            report ??= bytes is not null && _doc is not null ? ESignInspectLive(_doc, bytes) : null;
            SetStatus("Ready");
            if (report is null)
            {
                SetStatus("TDPdf could not read this document's signatures.");
                return;
            }
            ESignShowVerifyWindow(this, report, bytes, path);
        }

        /// <summary>The verify window over <paramref name="owner"/> — from the menu or from Document Info.</summary>
        private void ESignShowVerifyWindow(Window owner, SignatureObservations report, byte[]? bytes, string path)
        {
            // The server's evidence, for a request this device sent: matched by the fingerprint
            // recorded at download, else by name when the file says it was sealed by this instance.
            DocumensoRequestStore.Entry? match = null;
            bool exact = false;
            string? host = null;
            if (DocumensoConfig.TryResolveBaseUrl() is { } resolved)
            {
                host = DocumensoConfig.HostOf(resolved.BaseUrl);
                string? sha = bytes is null ? null
                    : System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                bool sealedHere = report.Signatures.Any(s => s.Location is { } loc
                    && DocumensoConfig.Normalise(loc) is { } l && DocumensoConfig.HostOf(l) == host);
                (match, exact) = DocumensoRequestView.MatchTracked(DocumensoRequestStore.Load(), sha, Path.GetFileName(path), sealedHere);
            }

            DocumensoVerifyWindow.Show(owner, new DocumensoVerifyWindow.Inputs
            {
                DocumentName = string.IsNullOrWhiteSpace(_ctx.DisplayName) ? Path.GetFileName(path) : _ctx.DisplayName,
                Observations = report,
                Dirty = _isDirty,
                Host = host,
                Match = match,
                MatchIsExact = exact,
                DownloadAsync = match is null ? null : async (win, what) =>
                {
                    if (ESignConnectedClient() is not { } client) return "Not connected, so nothing was downloaded.";
                    var env = new DocumensoEnvelope { Id = match.EnvelopeId, Title = match.Title };
                    return (await ESignDownloadArtifactAsync(win, client, env, what, openAfter: true))?.Message;
                },
                OpenInReader = win => ESignOpenInPdfReader(win, path),
            });
        }

        /// <summary>
        /// The file whose signatures are described: the user's document on disk (what was signed),
        /// else the working copy. Null when neither exists.
        /// </summary>
        private string? ESignOnDiskPath()
        {
            if (!string.IsNullOrEmpty(_ctx.OriginalPath) && File.Exists(_ctx.OriginalPath)) return _ctx.OriginalPath;
            return !string.IsNullOrEmpty(_currentFile) && File.Exists(_currentFile) ? _currentFile : null;
        }

        /// <summary>
        /// Reads <paramref name="path"/> and parses it on its own (read-only), so the ranges are
        /// measured against exactly the bytes they describe. Returns a null report when the file
        /// cannot be parsed standalone — a password-protected original — so the caller can fall back
        /// to the live document, which is that same file decrypted.
        /// </summary>
        private static (SignatureObservations? Report, byte[]? Bytes) ESignInspectFile(string path)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch { return (null, null); }
            try
            {
                using var doc = PdfReader.Open(new MemoryStream(bytes, writable: false), PdfDocumentOpenMode.ReadOnly);
                return (PdfSignatureInspector.Inspect(doc, bytes), bytes);
            }
            catch
            {
                return (null, bytes);
            }
        }

        private static SignatureObservations? ESignInspectLive(PdfDocument doc, byte[] bytes)
        {
            try { return PdfSignatureInspector.Inspect(doc, bytes); }
            catch { return null; }
        }

        // ============================================================
        // Document Info (F12) — "Digital signatures"
        // ============================================================

        /// <summary>
        /// Document Info's "Digital signatures" section for a signed document: the same observations
        /// as Verify Signatures, compact, plus a way to the full view and to a PDF reader. Null (no
        /// section) for an unsigned one. Independent of Electronic Signature being configured — a
        /// signed PDF from anywhere gets it.
        /// </summary>
        private FrameworkElement? ESignBuildDocumentInfoSignatures(PdfDocument doc, Window owner)
        {
            if (PdfSignatureScan.SignedFields(doc).Count == 0) return null;
            string? path = ESignOnDiskPath();
            if (path is null) return null;
            var (report, bytes) = ESignInspectFile(path);
            report ??= bytes is not null ? ESignInspectLive(doc, bytes) : null;
            if (report is null) return null;

            var section = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            section.Children.Add(new TextBlock
            {
                Text = "Digital signatures",
                Foreground = BrushResource("TextSecondary"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 4),
            });
            section.Children.Add(DocumensoVerifyWindow.BuildObservations(report, compact: true));
            section.Children.Add(DocumensoUi.Para("Not checked by TDPdf. Saving this document in TDPdf removes its signatures.",
                "TextMuted", 11, new Thickness(0, 4, 0, 6)));
            var row = new WrapPanel();
            var details = DocumensoUi.SmallButton(owner, "Signature details…");
            details.Click += (_, _) => ESignShowVerifyWindow(owner, report, bytes, path);
            var reader = DocumensoUi.SmallButton(owner, "Open in your PDF reader…");
            var said = DocumensoUi.Para("", "TextSecondary", 11, new Thickness(0, 2, 0, 0));
            reader.Click += (_, _) => said.Text = ESignOpenInPdfReader(owner, path);
            row.Children.Add(details);
            row.Children.Add(reader);
            section.Children.Add(row);
            section.Children.Add(said);
            return section;
        }

        // ============================================================
        // "Open in your PDF reader for full validation"
        // ============================================================

        /// <summary>
        /// Opens the file in the user's PDF reader so that it, not TDPdf, checks the signature.
        /// When TDPdf is itself the default PDF app that would only reopen the file here, so Adobe
        /// Acrobat (or Reader) is launched directly if installed, and otherwise the user is told
        /// what to use. Returns the sentence to show.
        /// </summary>
        private string ESignOpenInPdfReader(Window owner, string path)
        {
            try
            {
                if (!ESignTdpdfIsDefaultPdfApp())
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    return "Opened in your default PDF app.";
                }
                if (ESignFindAcrobat() is { } acrobat)
                {
                    Process.Start(new ProcessStartInfo(acrobat) { UseShellExecute = false, ArgumentList = { path } });
                    return "Opened in Adobe Acrobat, which checks the signature, the certificate chain and the timestamp.";
                }
            }
            catch
            {
                return "Could not open a PDF reader.";
            }
            TdpDialog.Show(owner,
                "TDPdf is your default PDF app, so opening the file \"in your PDF reader\" would only open it here again.\n\n" +
                "To have the signature checked, open the file in Adobe Acrobat Reader (free from adobe.com). It verifies " +
                "the signature, the signer's certificate chain and the timestamp, and shows whether anything changed after signing.",
                "Verify Signatures", MessageBoxButton.OK, MessageBoxImage.Information);
            return "Open the file in Adobe Acrobat Reader to have the signature checked.";
        }

        /// <summary>Whether .pdf opens in TDPdf for this user (ProgID <c>TDPdf.pdf</c>, App.xaml.cs).</summary>
        private static bool ESignTdpdfIsDefaultPdfApp()
        {
            try
            {
                foreach (var key in new[]
                {
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice",
                    @"Software\Microsoft\Windows\Shell\Associations\FileAssociations\.pdf\UserChoice",
                })
                {
                    using var k = Registry.CurrentUser.OpenSubKey(key);
                    if (k?.GetValue("ProgId") is string progId)
                        return progId.Equals("TDPdf.pdf", System.StringComparison.OrdinalIgnoreCase);
                }
                using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.pdf");
                return (classes?.GetValue("") as string)?.Equals("TDPdf.pdf", System.StringComparison.OrdinalIgnoreCase) == true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Adobe Acrobat or Acrobat Reader's executable, from App Paths. Null when neither is installed.</summary>
        private static string? ESignFindAcrobat()
        {
            foreach (var exe in new[] { "Acrobat.exe", "AcroRd32.exe" })
            {
                foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                {
                    try
                    {
                        using var k = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                        if (k?.GetValue("") is string p && p.Trim('"') is { Length: > 0 } clean && File.Exists(clean)) return clean;
                    }
                    catch { /* unreadable key: try the next */ }
                }
            }
            return null;
        }
    }
}
