using TDPdf.Services;

/// <summary>Pins <see cref="LruCache{TKey,TValue}"/>: the bound that keeps the page sidebar's
/// thumbnails from growing with the page count.</summary>
internal static class LruCacheTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        var c = new LruCache<int, string>(3);
        c.Set(1, "a"); c.Set(2, "b"); c.Set(3, "c");
        Check("lru: holds up to capacity", c.Count == 3, $"{c.Count}");

        c.Set(4, "d");
        Check("lru: over capacity evicts the oldest", c.Count == 3 && !c.TryGet(1, out _), $"{c.Count}");
        Check("lru: newer entries survive", c.TryGet(2, out var b) && b == "b" && c.TryGet(4, out var d) && d == "d", "");

        // 2 and 4 were just read, so 3 is now the least recently used.
        c.Set(5, "e");
        Check("lru: a read protects an entry from eviction", c.TryGet(2, out _) && !c.TryGet(3, out _), "");

        c.Set(2, "B");
        Check("lru: set replaces the value without growing", c.Count == 3 && c.TryGet(2, out var b2) && b2 == "B", $"{c.Count}");

        Check("lru: remove drops one entry", c.Remove(2) && !c.TryGet(2, out _) && !c.Remove(2), "");
        c.Clear();
        Check("lru: clear empties it", c.Count == 0 && !c.TryGet(4, out _), "");

        var big = new LruCache<int, int>(240);
        for (int i = 0; i < 10000; i++) big.Set(i, i);
        Check("lru: 10,000 inserts stay at the bound", big.Count == 240 && big.TryGet(9999, out _) && !big.TryGet(0, out _), $"{big.Count}");

        Check("lru: capacity below 1 is raised to 1", new LruCache<int, int>(0).Capacity == 1, "");
    }
}
