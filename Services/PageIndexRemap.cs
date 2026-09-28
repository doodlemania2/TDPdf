using System;
using System.Collections.Generic;
using System.Linq;

namespace TDPdf.Services
{
    // ============================================================
    // Where each page goes when the page list is restructured (upstream KillerPDF #429, widened).
    //
    // Unsaved overlay annotations, and the page-snapshot undo history, are addressed by page INDEX.
    // Every structural page edit — insert, delete, reorder, duplicate — changes which page an index
    // names, and each of them used to deal with that by throwing every unsaved annotation in the
    // document away, including those on pages the edit never touched. None of those edits changes a
    // page's GEOMETRY (that is rotate / crop / transform), so an annotation is still valid exactly
    // where it is drawn; it only needs to follow its page to the page's new number.
    //
    // So each edit is expressed once, as a map from old index to new index, and one routine applies
    // that map to everything index-keyed. The map lives here, away from WPF, for the same reason
    // PageBlockMove does: the index arithmetic is the part that is easy to get wrong, and it is the
    // only part tests/PdfCore can pin without a ListBox and a message pump.
    //
    // A map is an int[] the length of the document BEFORE the edit: map[old] is the page's index
    // after it, or <see cref="Removed"/> when the edit deleted that page.
    // ============================================================
    internal static class PageIndexRemap
    {
        /// <summary>The map entry for a page the edit removed.</summary>
        public const int Removed = -1;

        /// <summary>
        /// Deleting <paramref name="deleted"/> (any order; duplicates and out-of-range indices
        /// ignored): each surviving page moves down by the number of deleted pages before it.
        /// </summary>
        public static int[] ForDelete(int pageCount, IEnumerable<int>? deleted)
        {
            var gone = new HashSet<int>((deleted ?? Array.Empty<int>()).Where(i => i >= 0 && i < pageCount));
            var map = new int[Math.Max(0, pageCount)];
            int next = 0;
            for (int i = 0; i < map.Length; i++)
                map[i] = gone.Contains(i) ? Removed : next++;
            return map;
        }

        /// <summary>
        /// Inserting <paramref name="count"/> new pages so the first of them lands at
        /// <paramref name="insertAt"/>: every page at or after that index moves up by
        /// <paramref name="count"/>. Also the map for Duplicate, whose copies are inserted as one run
        /// — the copies themselves are new pages, so nothing maps onto them.
        /// </summary>
        public static int[] ForInsert(int pageCount, int insertAt, int count)
        {
            var map = new int[Math.Max(0, pageCount)];
            int shift = Math.Max(0, count);
            for (int i = 0; i < map.Length; i++)
                map[i] = i < insertAt ? i : i + shift;
            return map;
        }

        /// <summary>
        /// Inverts a new page order — <paramref name="newOrder"/>[newIndex] = oldIndex, the shape
        /// <see cref="PageBlockMove.Apply(int, PageBlockMove.Plan)"/> returns — into an old→new map.
        /// A source index the order does not mention is treated as removed.
        /// </summary>
        public static int[] ForOrder(int pageCount, IReadOnlyList<int> newOrder)
        {
            var map = new int[Math.Max(0, pageCount)];
            Array.Fill(map, Removed);
            for (int n = 0; n < newOrder.Count; n++)
            {
                int old = newOrder[n];
                if (old < 0 || old >= map.Length)
                    throw new ArgumentException($"Page order names page {old}, outside 0..{map.Length - 1}.", nameof(newOrder));
                if (map[old] != Removed)
                    throw new ArgumentException($"Page order names page {old} twice.", nameof(newOrder));
                map[old] = n;
            }
            return map;
        }

        /// <summary>
        /// Where <paramref name="oldIndex"/> goes. An index outside the map — a key for a page the
        /// document did not have before the edit — has nowhere to go and reads as removed; nothing can
        /// display or save it anyway.
        /// </summary>
        public static int Map(int[] map, int oldIndex) =>
            oldIndex >= 0 && oldIndex < map.Length ? map[oldIndex] : Removed;

        /// <summary>
        /// Re-keys <paramref name="source"/> through <paramref name="map"/>, dropping entries whose
        /// page was removed, into a NEW dictionary — the caller commits it, so a map that fails
        /// here leaves the original untouched.
        /// </summary>
        /// <exception cref="ArgumentException">Two surviving entries map onto one page: the map is not
        /// a valid page edit, and merging them silently would lose one of them.</exception>
        public static Dictionary<int, T> RemapKeys<T>(IEnumerable<KeyValuePair<int, T>> source, int[] map)
        {
            var result = new Dictionary<int, T>();
            foreach (var (oldIndex, value) in source)
            {
                int newIndex = Map(map, oldIndex);
                if (newIndex == Removed) continue;
                if (!result.TryAdd(newIndex, value))
                    throw new ArgumentException($"Two pages map onto page {newIndex}.", nameof(map));
            }
            return result;
        }
    }
}
