namespace TDPdf.Services
{
    /// <summary>What to do about a paragraph edit.</summary>
    internal enum ParagraphPlan
    {
        /// <summary>Rewrite the paragraph's own text objects in the document's font.</summary>
        InPlace,
        /// <summary>Recognised text over a scan: rewrite the invisible layer and draw the change over the picture.</summary>
        ScanOverlay,
        /// <summary>Do not touch the paragraph, and say why.</summary>
        Refuse,
        /// <summary>Ask first, then cover the paragraph and draw the new text over it.</summary>
        AskThenCover,
        /// <summary>Cover and draw over it without asking (the user ticked "Don't ask again", or chose a different style).</summary>
        Cover,
    }

    /// <summary>What is known about a paragraph before anything is changed.</summary>
    internal sealed class ParagraphFacts
    {
        /// <summary>An opaque shape or picture, in the file or among TDPdf's unsaved marks, sits over the text.</summary>
        public bool OpaqueCover { get; init; }
        /// <summary>The text is the invisible layer an OCR pass put over a scan.</summary>
        public bool ScanText { get; init; }
        public bool RightToLeft { get; init; }
        public bool MixedStyles { get; init; }
        /// <summary>The page carries colour, gradients or images that rewriting its content would damage.</summary>
        public bool PageUnsafe { get; init; }
        /// <summary>The PDF engine in this build cannot rewrite text.</summary>
        public bool EngineMissing { get; init; }
        /// <summary>The paragraph's text objects are turned, scaled oddly, or sit inside a form.</summary>
        public bool Unsupported { get; init; }
        /// <summary>The user changed font, size, colour or style in the style bar, which only the overlay can honour.</summary>
        public bool StyleChanged { get; init; }
        /// <summary>The setting "Don't ask again" is on.</summary>
        public bool DontAsk { get; init; }
    }

    /// <summary>The verdict, with the reason in words fit for the status line or a dialog.</summary>
    internal sealed record ParagraphVerdict(ParagraphPlan Plan, string Reason);

    /// <summary>
    /// The decision table for editing a paragraph. Pure, so every row of it is tested.
    /// </summary>
    /// <remarks>
    /// Two moments need a decision. <see cref="Before"/> runs when the box opens, from what can be
    /// seen without changing anything. <see cref="AfterFailure"/> runs when the rewrite was
    /// attempted and did not survive: the font could not draw a typed character, the finished file
    /// failed the check that reads it back, or the engine refused.
    ///
    /// Refusing is reserved for the cases where drawing over would be a lie: an opaque cover means
    /// the visible words are not the ones in the text layer, so changing either is guesswork. Every
    /// other failure has the overlay as an answer, and the user is asked before it is used, because
    /// covering a whole paragraph is a bigger act than covering a line.
    /// </remarks>
    internal static class ParagraphEditPolicy
    {
        internal static ParagraphVerdict Before(ParagraphFacts f)
        {
            if (f.OpaqueCover)
                return new(ParagraphPlan.Refuse,
                    "something opaque is drawn over this text, so the words you see are not the ones in the file");
            if (f.RightToLeft)
                return new(ParagraphPlan.Refuse,
                    "right-to-left paragraphs cannot be reflowed yet; edit them a line at a time");
            if (f.ScanText)
                return new(ParagraphPlan.ScanOverlay, "this is recognised text over a scan");

            // Choosing a different font, size or colour is asking for the overlay on purpose.
            if (f.StyleChanged) return new(ParagraphPlan.Cover, "you changed the style");

            string? why =
                f.EngineMissing ? "this build cannot rewrite text in place" :
                f.PageUnsafe ? "rewriting this page would damage other content on it" :
                f.MixedStyles ? "the paragraph mixes fonts or sizes" :
                f.Unsupported ? "its text is turned or sits inside a form" :
                null;
            if (why is null) return new(ParagraphPlan.InPlace, "");
            return Fallback(why, f.DontAsk);
        }

        /// <summary>The rewrite was tried and failed.</summary>
        internal static ParagraphVerdict AfterFailure(string reason, bool dontAsk, bool scanText) =>
            scanText
                // A scan always has the overlay for the visible change; failing to rewrite the
                // invisible layer only costs searchability, which the caller says.
                ? new(ParagraphPlan.Cover, reason)
                : Fallback(reason, dontAsk);

        private static ParagraphVerdict Fallback(string reason, bool dontAsk) =>
            dontAsk ? new(ParagraphPlan.Cover, reason) : new(ParagraphPlan.AskThenCover, reason);
    }
}
