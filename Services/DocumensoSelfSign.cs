using System;
using System.Collections.Generic;
using System.Linq;

namespace TDPdf.Services
{
    /// <summary>Where the one signature field of a Sign This Document comes from.</summary>
    internal enum ESignSelfFieldSource
    {
        /// <summary>Fields already placed for you on the page (adopted from Send for Signature, or placed now).</summary>
        Placed,
        /// <summary>A <c>{{signature, r1}}</c> placeholder in the PDF, which Documenso turns into your field.</summary>
        Placeholder,
        /// <summary>Neither: the user has to place a signature field before anything is sent.</summary>
        NeedsPlacement,
    }

    /// <summary>What the self-sign window should say about the envelope after a Refresh.</summary>
    internal enum ESignSelfState { Waiting, Completed, Rejected, Cancelled, Unknown }

    /// <summary>
    /// Sign This Document's WPF-free core (design §5.4b): it is Send for Signature with exactly one
    /// recipient — the connected account, as a Signer — and at least one signature field, followed
    /// by a browser handoff to that recipient's signing page. Nothing here signs anything: Documenso
    /// has no public signing endpoint (design §1.6), so the signing happens on the service's own
    /// page, and these rules only decide what is sent.
    /// </summary>
    internal static class DocumensoSelfSignPlan
    {
        /// <summary>A session with you as the only recipient, a Signer, in parallel order.</summary>
        internal static ESignSendSession NewSession(string title, string email, string? name)
        {
            var s = new ESignSendSession { Title = title, SequentialOrder = false };
            s.Recipients.Add(new ESignRecipientEntry
            {
                Email = email.Trim(),
                Name = (name ?? "").Trim(),
                Role = DocumensoRecipientRole.Signer,
            });
            return s;
        }

        /// <summary>
        /// Takes over the fields already placed on this document for you in Send for Signature —
        /// those of the recipient row whose address is yours — re-keyed to the self-sign session's
        /// one recipient. Fields placed for anyone else are left alone: they are someone else's
        /// boxes, and moving them onto you would sign in the wrong place. Returns how many were taken.
        /// </summary>
        internal static int AdoptFields(ESignSendSession self, ESignSendSession? send)
        {
            if (send is null || self.Recipients.Count != 1) return 0;
            var me = self.Recipients[0];
            var mine = send.Recipients
                .Where(r => DocumensoSendPlan.TakesFields(r.Role)
                            && string.Equals(r.Email.Trim(), me.Email.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Key)
                .ToHashSet();
            if (mine.Count == 0) return 0;
            var adopted = send.Fields.Where(f => mine.Contains(f.RecipientKey)).Select(f => f with { RecipientKey = me.Key }).ToList();
            self.Fields.AddRange(adopted);
            return adopted.Count;
        }

        /// <summary>
        /// Placed fields with a signature among them win; failing that, a signature placeholder for
        /// <c>r1</c> (which, with one recipient, is you); failing that, the user places one. A
        /// document with placeholders for r2 and beyond is not refused here — the shared
        /// <see cref="DocumensoSendPlan.Check"/> says why it cannot be self-signed.
        /// </summary>
        internal static ESignSelfFieldSource Source(ESignSendSession self, IReadOnlyList<DocumensoPlaceholder> placeholders)
        {
            if (self.Fields.Any(f => DocumensoSendPlan.IsSignatureType(f.Type))) return ESignSelfFieldSource.Placed;
            if (placeholders.Any(p => p.IsSignature && p.RecipientNumber == 1)) return ESignSelfFieldSource.Placeholder;
            return ESignSelfFieldSource.NeedsPlacement;
        }

        internal static ESignSelfState StateOf(DocumensoEnvelopeStatus s) => s switch
        {
            DocumensoEnvelopeStatus.Pending or DocumensoEnvelopeStatus.Draft => ESignSelfState.Waiting,
            DocumensoEnvelopeStatus.Completed => ESignSelfState.Completed,
            DocumensoEnvelopeStatus.Rejected => ESignSelfState.Rejected,
            DocumensoEnvelopeStatus.Cancelled => ESignSelfState.Cancelled,
            _ => ESignSelfState.Unknown,
        };

        /// <summary>
        /// The signing link distribute returned for you. With one recipient there is one link; it is
        /// still matched by address so a team's default recipients (which Documenso may add) can
        /// never be the page that opens.
        /// </summary>
        internal static string? SigningUrlFor(string email, IReadOnlyList<ESignSigningLink> links, IReadOnlyList<ESignRecipientEntry> rows)
        {
            var me = rows.FirstOrDefault(r => string.Equals(r.Email.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase));
            if (me is null) return null;
            return links.FirstOrDefault(l => l.RecipientKey == me.Key)?.SigningUrl is { Length: > 0 } url ? url : null;
        }
    }
}
