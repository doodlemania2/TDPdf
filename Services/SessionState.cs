using System.Text.Json;

namespace TDPdf.Services
{
    /// <summary>
    /// The saved-session view state (page, zoom, fit, view mode per document) and the pure parts of
    /// restoring a session. The list of paths itself is merged across windows by
    /// <see cref="FilePathPolicy.MergeSession"/>; this adds what each tab looked like. Stored as one
    /// small JSON string in the per-user settings. Never holds a password or document content.
    /// </summary>
    internal static class SessionState
    {
        /// <summary>Upper bound on what is read back, so a damaged or hand-edited setting cannot cost memory.</summary>
        private const int MaxEntries = 200;

        public static string Serialize(IEnumerable<TabViewState> states)
        {
            var list = Sanitize(states);
            return list.Count == 0 ? "" : JsonSerializer.Serialize(list);
        }

        /// <summary>Reads what <see cref="Serialize"/> wrote. Anything unreadable yields an empty list.</summary>
        public static List<TabViewState> Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();
            try
            {
                var raw = JsonSerializer.Deserialize<List<TabViewState?>>(json);
                return raw is null ? new() : Sanitize(raw);
            }
            catch { return new(); }
        }

        /// <summary>
        /// The saved states after one window closes: states already saved stay for the paths still in
        /// the merged session list, and this window's own states replace any saved for the same file.
        /// </summary>
        public static string Merge(string? savedJson, IEnumerable<string?> mergedPaths, IEnumerable<TabViewState> mine)
        {
            var keep = new HashSet<string>(FilePathPolicy.NormalizeDistinct(mergedPaths), StringComparer.OrdinalIgnoreCase);
            var byPath = new Dictionary<string, TabViewState>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in Parse(savedJson))
                if (keep.Contains(s.Path)) byPath[s.Path] = s;
            foreach (var s in Sanitize(mine))
                if (keep.Contains(s.Path)) byPath[s.Path] = s;
            return Serialize(byPath.Values);
        }

        /// <summary>The saved state for <paramref name="path"/>, or a bare "open it normally" state when there is none.</summary>
        public static TabViewState For(IEnumerable<TabViewState> states, string path)
        {
            foreach (var s in states)
                if (FilePathPolicy.SamePath(s.Path, path)) return s;
            return new TabViewState(path, 0, 0, "None", "");
        }

        /// <summary>
        /// Splits a saved path list into what can be reopened and what cannot: files that no longer
        /// exist, and anything under <paramref name="tempRoot"/> (a working copy, never a document to
        /// reopen on its own). Order is kept.
        /// </summary>
        public static (List<string> Present, List<string> Missing) SplitExisting(
            IEnumerable<string?> paths, Func<string, bool> exists, string? tempRoot)
        {
            var present = new List<string>();
            var missing = new List<string>();
            string? temp = FilePathPolicy.Normalize(tempRoot);
            foreach (var p in FilePathPolicy.NormalizeDistinct(paths))
            {
                if (temp is not null && p.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) continue;
                bool there;
                try { there = exists(p); } catch { there = false; }
                (there ? present : missing).Add(p);
            }
            return (present, missing);
        }

        private static List<TabViewState> Sanitize(IEnumerable<TabViewState?> states)
        {
            var result = new List<TabViewState>();
            foreach (var s in states)
            {
                if (s is null) continue;
                string? path = FilePathPolicy.Normalize(s.Path);
                if (path is null) continue;
                double zoom = double.IsNaN(s.Zoom) || double.IsInfinity(s.Zoom) || s.Zoom < 0 ? 0 : s.Zoom;
                result.Add(new TabViewState(path, Math.Max(0, s.Page), zoom, s.Fit ?? "None", s.ViewMode ?? ""));
                if (result.Count >= MaxEntries) break;
            }
            return result;
        }
    }
}
