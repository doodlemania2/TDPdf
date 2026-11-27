using System.IO;
using System.Text.Json;

namespace TDPdf.Services
{
    /// <summary>One menu kind's saved arrangement: the order of its items and the ones hidden.</summary>
    public sealed record KindLayout(IReadOnlyList<string> Order, IReadOnlyCollection<string> Hidden)
    {
        public static readonly KindLayout Empty = new(Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>
    /// The user's customised right-click menus (drag to rearrange, uncheck to hide), kept as one JSON
    /// string in settings. Reading is forgiving on purpose: a corrupt string, an id from a newer or
    /// older version, or a kind that no longer exists never loses the menu, it just falls back to the
    /// default for whatever it cannot place. Pure; no WPF.
    /// </summary>
    public static class MenuLayouts
    {
        /// <summary>Parses the saved JSON. Never throws: anything unreadable yields an empty set.</summary>
        public static Dictionary<string, KindLayout> Parse(string? json)
        {
            var result = new Dictionary<string, KindLayout>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(json)) return result;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
                if (!doc.RootElement.TryGetProperty("menus", out var menus) || menus.ValueKind != JsonValueKind.Object) return result;
                foreach (var kind in menus.EnumerateObject())
                {
                    if (kind.Value.ValueKind != JsonValueKind.Object) continue;
                    var order = ReadStrings(kind.Value, "order");
                    var hidden = ReadStrings(kind.Value, "hidden");
                    result[kind.Name] = new KindLayout(order, hidden);
                }
            }
            catch (JsonException) { return new Dictionary<string, KindLayout>(StringComparer.Ordinal); }
            catch (InvalidOperationException) { return new Dictionary<string, KindLayout>(StringComparer.Ordinal); }
            return result;
        }

        private static List<string> ReadStrings(JsonElement obj, string name)
        {
            var list = new List<string>();
            if (!obj.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s) list.Add(s);
            return list;
        }

        /// <summary>Writes the layouts, omitting kinds that are empty (the default).</summary>
        public static string Serialize(IReadOnlyDictionary<string, KindLayout> layouts)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteNumber("v", 1);
                w.WriteStartObject("menus");
                foreach (var (kind, layout) in layouts.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (layout.Order.Count == 0 && layout.Hidden.Count == 0) continue;
                    w.WriteStartObject(kind);
                    w.WriteStartArray("order");
                    foreach (var id in layout.Order) w.WriteStringValue(id);
                    w.WriteEndArray();
                    w.WriteStartArray("hidden");
                    foreach (var id in layout.Hidden.OrderBy(h => h, StringComparer.Ordinal)) w.WriteStringValue(id);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }

        /// <summary>
        /// Merges a saved layout with the current default order. Saved ids that no longer exist are
        /// dropped; ids the saved layout never heard of (added by a newer version) are placed right
        /// after the item that precedes them by default, or first when nothing does, so an upgrade
        /// never loses a command. Hidden applies only to ids that exist.
        /// </summary>
        public static List<(string Id, bool Hidden)> Resolve(IReadOnlyList<string> defaultOrder, KindLayout? saved)
        {
            var known = new HashSet<string>(defaultOrder, StringComparer.Ordinal);
            var order = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (saved is not null)
                foreach (var id in saved.Order)
                    if (known.Contains(id) && seen.Add(id)) order.Add(id);

            for (int i = 0; i < defaultOrder.Count; i++)
            {
                string id = defaultOrder[i];
                if (seen.Contains(id)) continue;
                int insertAt = 0;
                for (int j = i - 1; j >= 0; j--)
                {
                    int at = order.IndexOf(defaultOrder[j]);
                    if (at >= 0) { insertAt = at + 1; break; }
                }
                order.Insert(insertAt, id);
                seen.Add(id);
            }

            var hidden = saved is null ? new HashSet<string>() : new HashSet<string>(saved.Hidden.Where(known.Contains));
            return order.Select(id => (id, hidden.Contains(id))).ToList();
        }

        /// <summary>The layout that results from rows in a given order (what the editor saves). A default arrangement saves as empty.</summary>
        public static KindLayout FromRows(IReadOnlyList<string> defaultOrder, IReadOnlyList<(string Id, bool Hidden)> rows)
        {
            bool isDefault = rows.Count == defaultOrder.Count
                && rows.Select(r => r.Id).SequenceEqual(defaultOrder, StringComparer.Ordinal)
                && rows.All(r => !r.Hidden);
            if (isDefault) return KindLayout.Empty;
            return new KindLayout(rows.Select(r => r.Id).ToList(), rows.Where(r => r.Hidden).Select(r => r.Id).ToList());
        }
    }

    /// <summary>
    /// The state behind the "Edit menu" window: rows that can be dragged to a new place, moved with
    /// the keyboard, hidden, and reset. Pure so the order logic is tested without a window.
    /// </summary>
    public sealed class MenuLayoutEditor
    {
        private readonly IReadOnlyList<string> _defaults;
        private readonly List<(string Id, bool Hidden)> _rows;

        public MenuLayoutEditor(IReadOnlyList<string> defaultOrder, KindLayout? saved)
        {
            _defaults = defaultOrder;
            _rows = MenuLayouts.Resolve(defaultOrder, saved);
        }

        public IReadOnlyList<(string Id, bool Hidden)> Rows => _rows;

        public int Count => _rows.Count;

        /// <summary>Moves the row at <paramref name="from"/> so it ends at <paramref name="to"/>. Returns the new index.</summary>
        public int Move(int from, int to)
        {
            if (from < 0 || from >= _rows.Count) return from;
            to = Math.Clamp(to, 0, _rows.Count - 1);
            if (to == from) return from;
            var row = _rows[from];
            _rows.RemoveAt(from);
            _rows.Insert(to, row);
            return to;
        }

        /// <summary>Moves a row one step up (-1) or down (+1), the keyboard way. Returns the new index.</summary>
        public int MoveBy(int index, int delta) => Move(index, index + delta);

        public void SetHidden(int index, bool hidden)
        {
            if (index < 0 || index >= _rows.Count) return;
            _rows[index] = (_rows[index].Id, hidden);
        }

        public bool ToggleHidden(int index)
        {
            if (index < 0 || index >= _rows.Count) return false;
            bool now = !_rows[index].Hidden;
            _rows[index] = (_rows[index].Id, now);
            return now;
        }

        public void Reset()
        {
            _rows.Clear();
            _rows.AddRange(_defaults.Select(id => (id, false)));
        }

        public bool IsDefault => ReferenceEquals(ToLayout(), KindLayout.Empty);

        public KindLayout ToLayout() => MenuLayouts.FromRows(_defaults, _rows);
    }

    /// <summary>
    /// Raise and lower: reorders the marks of a page (list order is paint and save order). Pure and
    /// generic so the movement rules are tested without a document.
    /// </summary>
    public static class MarkOrder
    {
        /// <summary>
        /// Moves every selected item one step toward the end of the list (<paramref name="direction"/> +1,
        /// "raise": painted later, so on top) or the start (-1, "lower"), past the nearest unselected
        /// neighbour that <paramref name="overlaps"/> the selection. Neighbours that do not overlap are
        /// stepped over so the move is always visible. The selected items keep their relative order.
        /// Returns false (list untouched) when nothing can move.
        /// </summary>
        public static bool Move<T>(List<T> list, IReadOnlyCollection<T> selected, int direction, Func<T, bool>? overlaps = null) where T : class
        {
            if (direction is not (1 or -1) || selected.Count == 0) return false;
            var sel = new HashSet<T>(selected, ReferenceEqualityComparer.Instance as IEqualityComparer<T>);
            var idx = new List<int>();
            for (int i = 0; i < list.Count; i++) if (sel.Contains(list[i])) idx.Add(i);
            if (idx.Count == 0 || idx.Count == list.Count) return false;

            // Work from the leading edge so a block never steps over its own members.
            var order = direction > 0 ? idx.OrderByDescending(i => i).ToList() : idx.OrderBy(i => i).ToList();
            bool moved = false;
            foreach (int start in order)
            {
                int i = start;
                int j = i + direction;
                // Skip neighbours that are selected (they move as a block) when they sit directly ahead.
                if (j < 0 || j >= list.Count) continue;
                if (sel.Contains(list[j])) continue;
                // Step past the nearest overlapping unselected neighbour; if none overlaps, step one.
                int target = j;
                if (overlaps is not null)
                {
                    int k = j;
                    while (k >= 0 && k < list.Count && !sel.Contains(list[k]) && !overlaps(list[k])) k += direction;
                    target = k >= 0 && k < list.Count && !sel.Contains(list[k]) ? k : j;
                }
                var item = list[i];
                list.RemoveAt(i);
                list.Insert(target, item);
                moved = true;
            }
            return moved;
        }
    }
}
