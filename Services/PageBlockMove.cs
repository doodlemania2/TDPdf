using System;
using System.Collections.Generic;
using System.Linq;

namespace TDPdf.Services
{
    // ============================================================
    // Where a dragged block of pages actually lands (#135, upstream KillerPDF #233).
    //
    // The Pages sidebar is SelectionMode="Extended", so a drag can carry any set of pages —
    // contiguous or not — and the drop has to turn "the gap the pointer is over" into "the index to
    // insert at once those pages have been lifted out". Those are not the same number, and the
    // difference is exactly the bug that ships: the block lands one page late whenever part of the
    // selection sat above the drop point, and further late the more of it did.
    //
    // The arithmetic lives here, away from WPF, for two reasons. It is the part of the feature a
    // reader is most likely to get wrong, and it is the only part that can be pinned by
    // tests/PdfCore — MainWindow needs a ListBox, a PdfDocument and a message pump before it can
    // move a single page. The mutation in MainWindow follows <see cref="Apply"/> step for step
    // (lift in document order, remove from the end, insert as one run), so the model the tests
    // exercise is the model the document gets.
    // ============================================================
    internal static class PageBlockMove
    {
        /// <summary>The move a drop describes: which pages travel, and where they come to rest.</summary>
        internal readonly struct Plan
        {
            public Plan(int[] from, int insertAt, bool isNoOp)
            {
                From = from;
                InsertAt = insertAt;
                IsNoOp = isNoOp;
            }

            /// <summary>The moving pages' CURRENT indices: ascending, distinct, all in range.</summary>
            public int[] From { get; }

            /// <summary>
            /// The index the block is inserted at AFTER <see cref="From"/> has been removed — an
            /// index into the shortened document, not into the one the user is looking at.
            /// </summary>
            public int InsertAt { get; }

            /// <summary>
            /// True when carrying this move out would leave the page order exactly as it is. Worth a
            /// field rather than a caller-side guess: a reorder rewrites and reloads the document,
            /// which costs the user every unsaved annotation (SaveTempAndReload clears them for any
            /// structural edit), so a drag that changes nothing must cost nothing.
            /// </summary>
            public bool IsNoOp { get; }
        }

        /// <summary>
        /// Works out the move for dropping <paramref name="selection"/> into the gap at
        /// <paramref name="dropBefore"/>.
        /// </summary>
        /// <param name="pageCount">Pages in the document right now.</param>
        /// <param name="selection">The dragged pages' current indices, in any order.</param>
        /// <param name="dropBefore">
        /// The GAP the pointer is over, counted in the list as the user currently sees it: 0 is
        /// above the first thumbnail, <paramref name="pageCount"/> is below the last, and i is the
        /// gap immediately above page i.
        /// </param>
        public static Plan Compute(int pageCount, IEnumerable<int>? selection, int dropBefore)
        {
            int[] from = (selection ?? Array.Empty<int>())
                .Where(i => i >= 0 && i < pageCount)
                .Distinct()
                .OrderBy(i => i)
                .ToArray();
            if (from.Length == 0) return new Plan(Array.Empty<int>(), 0, true);

            int gap = Math.Clamp(dropBefore, 0, pageCount);

            // ---- The drop-index arithmetic; this is the part that is easy to get wrong. ----
            //
            // The gap is expressed in the list as it stands, block included, but the insert happens
            // after the block has been lifted out. Every selected page ABOVE the gap therefore pulls
            // the gap up by one, so subtracting them converts the gap the user pointed at into the
            // index it has in the shortened list. Selected pages BELOW the gap are irrelevant:
            // removing them shifts nothing above them.
            //
            // That single subtraction is also the answer for a selection that STRADDLES the drop
            // point. Pages 2, 5 and 9 dropped into the gap between 5 and 6 (gap 6) count 2 and 5 as
            // above it, giving insertAt 4 — the block lands where the gap was, and page 9 is pulled
            // up out of its old home to join it. That is the behaviour to want: a non-contiguous
            // selection always arrives as one contiguous run, in ascending source order, at the
            // place the insertion line promised, whichever side of the line each page started on.
            int insertAt = gap - from.Count(i => i < gap);

            // A no-op is defined as "the resulting order is the order we already have", computed
            // rather than pattern-matched. The tempting shortcut — contiguous && insertAt == from[0]
            // — is right for a contiguous run and silently wrong for every other shape, e.g. every
            // page selected, where any gap at all reproduces the document unchanged.
            return new Plan(from, insertAt, IsIdentity(Apply(pageCount, from, insertAt)));
        }

        /// <summary>
        /// The page order this move produces, as source indices — <c>[0, 2, 1]</c> meaning "the page
        /// that is at index 2 ends up second". The model of the mutation MainWindow performs.
        /// </summary>
        public static int[] Apply(int pageCount, Plan plan) => Apply(pageCount, plan.From, plan.InsertAt);

        private static int[] Apply(int pageCount, int[] from, int insertAt)
        {
            var order = new List<int>(Enumerable.Range(0, pageCount));
            var moving = from.Select(i => order[i]).ToList();
            // Remove from the end so the earlier indices stay valid while we do it.
            for (int k = from.Length - 1; k >= 0; k--) order.RemoveAt(from[k]);
            order.InsertRange(Math.Clamp(insertAt, 0, order.Count), moving);
            return order.ToArray();
        }

        private static bool IsIdentity(int[] order)
        {
            for (int i = 0; i < order.Length; i++)
                if (order[i] != i) return false;
            return true;
        }
    }
}
