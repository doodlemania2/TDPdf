using TDPdf.Services;

/// <summary>
/// Pins the Select tool's hover-cursor priority: <see cref="SelectCursorPolicy"/>.
/// </summary>
/// <remarks>
/// The cursor is the only thing that distinguishes the Select tool's three behaviours before the
/// user commits to a gesture, and each mix-up is its own bug rather than a cosmetic wobble:
///
///   * text losing to a link would make a link that sits ON a paragraph — which is nearly all of
///     them — stop advertising that clicking navigates;
///   * a link losing to text would do the reverse, and the click would still navigate, so the user
///     is told one thing and gets another;
///   * an I-beam over empty page promises a text selection that a scan with no text layer can never
///     deliver, and hides the rectangle marquee that annotation box-select, region copy and OCR
///     capture all start from;
///   * and "not known yet" collapsing into a concrete answer is the one that costs real time rather
///     than clarity: it is what lets a cold cache be treated as "no text here", which is how a
///     mouse-move handler ends up parsing a page with PdfPig to answer it.
///
/// So this asserts the whole truth table, including the overlaps, rather than one case per outcome.
/// It is a pure decision by design — no WPF, no PDF — which is what lets it be pinned here at all.
/// </remarks>
internal static class SelectCursor
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("Select tool hover cursor (#135 item 5)");

        static SelectHoverCursor R(bool link, bool known, bool text)
            => SelectCursorPolicy.Resolve(link, known, text);

        // ── The three plain cases ──────────────────────────────────────────────────────────
        Check("selectable text gives the I-beam",
              R(false, true, true) == SelectHoverCursor.IBeam, R(false, true, true).ToString());
        Check("empty page stays the arrow",
              R(false, true, false) == SelectHoverCursor.Arrow, R(false, true, false).ToString());
        Check("a link gives the hand",
              R(true, true, false) == SelectHoverCursor.Hand, R(true, true, false).ToString());

        // ── The overlap: a link drawn over a paragraph. The hand has to win. ───────────────
        Check("a link over text still gives the hand",
              R(true, true, true) == SelectHoverCursor.Hand, R(true, true, true).ToString());

        // ── A cold text cache. Nothing may be invented, and a link must still win. ─────────
        Check("unknown text geometry leaves the cursor alone",
              R(false, false, false) == SelectHoverCursor.Unchanged, R(false, false, false).ToString());
        Check("a link wins even with unknown text geometry",
              R(true, false, false) == SelectHoverCursor.Hand, R(true, false, false).ToString());

        // The caller passes overText=false whenever geometry is unknown, but the policy must not
        // depend on that discipline: an unknown page is unknown whatever the stale flag says.
        Check("unknown geometry ignores a stale over-text flag",
              R(false, false, true) == SelectHoverCursor.Unchanged, R(false, false, true).ToString());

        // ── The affordance is read-only. Nothing above can reach a document. ───────────────
        // Guarded by construction (the policy takes three bools and returns an enum), asserted here
        // so a later "just pass the annotation in" refactor has to break a test to happen.
        Check("the policy is a pure decision over primitives",
              typeof(SelectCursorPolicy).GetMethod("Resolve")?.GetParameters()
                  .All(p => p.ParameterType == typeof(bool)) == true,
              "Resolve takes only bools");
    }
}
