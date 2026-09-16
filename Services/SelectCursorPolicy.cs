namespace TDPdf.Services
{
    /// <summary>
    /// What the Select tool's pointer should be over one point on the page. Deliberately not a WPF
    /// <c>Cursor</c> so the decision can be tested off Windows (tests/PdfCore) — MainWindow maps
    /// these onto the real cursors.
    /// </summary>
    internal enum SelectHoverCursor
    {
        /// <summary>
        /// Not enough is known yet to answer. The caller must leave the cursor exactly as it found
        /// it rather than guess — see <see cref="SelectCursorPolicy.Resolve"/>.
        /// </summary>
        Unchanged,

        /// <summary>Empty page: a drag here lays down the classic rectangle marquee.</summary>
        Arrow,

        /// <summary>Over selectable text: a drag here starts a flowing, browser-style selection.</summary>
        IBeam,

        /// <summary>Over a link: a click here navigates.</summary>
        Hand
    }

    /// <summary>
    /// The Select tool's hover affordance, as a pure decision (#135 item 5, from upstream
    /// KillerPDF #221).
    ///
    /// The Select tool has three behaviours under the pointer and the cursor is the only thing that
    /// tells them apart before the user commits to a drag: a link navigates, text selects as a
    /// flowing run, and empty page draws the marquee that annotation box-select, region copy and
    /// OCR capture all ride on. Showing an I-beam over the whole page — which is what a plain
    /// "Select tool ⇒ I-beam" rule would do — promises text selection on a scan that has no text
    /// layer at all.
    /// </summary>
    internal static class SelectCursorPolicy
    {
        /// <summary>
        /// Resolves the hover cursor. Priority is <b>link &gt; selectable text &gt; empty page</b>.
        ///
        /// Links win outright, including when the page's text geometry is not known yet: clicking a
        /// link navigates, and losing that affordance is worse than losing "there is text here" —
        /// most link rectangles sit ON text, so the two overlap almost every time they meet.
        ///
        /// <paramref name="textGeometryKnown"/> is the escape hatch that keeps this cheap. The
        /// character geometry behind <paramref name="overText"/> costs a full PdfPig parse of the
        /// page to build, which must never happen inline on a mouse-move; when the cache is cold
        /// the caller passes false and gets <see cref="SelectHoverCursor.Unchanged"/>, leaving the
        /// tool's own cursor in place until a background warm fills the cache in.
        /// </summary>
        /// <param name="overLink">The point is inside a link annotation's rectangle.</param>
        /// <param name="textGeometryKnown">
        /// The page's character geometry was available without parsing anything.
        /// </param>
        /// <param name="overText">
        /// The point is on selectable text. Only meaningful when <paramref name="textGeometryKnown"/>
        /// is true; a page with no text layer is "known, and not over text".
        /// </param>
        public static SelectHoverCursor Resolve(bool overLink, bool textGeometryKnown, bool overText)
        {
            if (overLink) return SelectHoverCursor.Hand;
            if (!textGeometryKnown) return SelectHoverCursor.Unchanged;
            return overText ? SelectHoverCursor.IBeam : SelectHoverCursor.Arrow;
        }
    }
}
