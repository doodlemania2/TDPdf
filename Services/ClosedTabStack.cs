namespace TDPdf.Services
{
    /// <summary>
    /// Where a tab was when it was closed or when the session was saved: the user's own file (never
    /// a temp or decrypted working copy), the page, the zoom and the view. Fit and view mode are
    /// kept as their enum names so this stays free of WPF and survives a renamed enum member (an
    /// unknown name simply falls back to the default when applied).
    /// </summary>
    /// <param name="Path">The user's original path.</param>
    /// <param name="Page">Zero-based page index.</param>
    /// <param name="Zoom">True zoom (1 = natural size); 0 means "no remembered zoom".</param>
    /// <param name="Fit">"None", "Width" or "Page".</param>
    /// <param name="ViewMode">"Single", "Continuous", "TwoPage" or "Grid"; empty when unknown.</param>
    internal sealed record TabViewState(string Path, int Page, double Zoom, string Fit, string ViewMode);

    /// <summary>
    /// The per-window "recently closed tabs" stack behind Ctrl+Shift+T. Newest last; capped so a
    /// long session cannot grow it without bound. Entries whose file has since gone are skipped
    /// (and reported) rather than failing the reopen.
    /// </summary>
    internal sealed class ClosedTabStack
    {
        public const int Capacity = 20;

        private readonly List<TabViewState> _items = new();

        public int Count => _items.Count;

        /// <summary>Remembers a closed tab. Entries without a path are ignored; the oldest drops off past <see cref="Capacity"/>.</summary>
        public void Push(TabViewState? entry)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path)) return;
            _items.Add(entry);
            while (_items.Count > Capacity) _items.RemoveAt(0);
        }

        /// <summary>
        /// Removes and returns the most recently closed tab whose file still exists. Newer entries
        /// whose file is gone are discarded on the way and their paths added to
        /// <paramref name="missing"/>. Null when nothing reopenable is left.
        /// </summary>
        public TabViewState? TakeLatestExisting(Func<string, bool> exists, List<string> missing)
        {
            while (_items.Count > 0)
            {
                var entry = _items[^1];
                _items.RemoveAt(_items.Count - 1);
                bool there;
                try { there = exists(entry.Path); } catch { there = false; }
                if (there) return entry;
                missing.Add(entry.Path);
            }
            return null;
        }
    }
}
