using System.Collections.Generic;

namespace TDPdf.Services
{
    /// <summary>
    /// A small least-recently-used cache with a hard upper bound, for things that are expensive
    /// to build and cheap to rebuild — the sidebar's page thumbnails, which would otherwise grow
    /// by one bitmap per page of a 10,000-page document. Not thread-safe: the UI thread owns it.
    /// </summary>
    internal sealed class LruCache<TKey, TValue> where TKey : notnull
    {
        private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map = new();
        private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();   // first = most recent

        public LruCache(int capacity)
        {
            Capacity = Math.Max(1, capacity);
        }

        public int Capacity { get; }
        public int Count => _map.Count;

        /// <summary>Looks a key up and, when found, marks it most recently used.</summary>
        public bool TryGet(TKey key, out TValue value)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
            value = default!;
            return false;
        }

        /// <summary>Adds or replaces an entry as the most recently used, evicting the least
        /// recently used ones while over capacity.</summary>
        public void Set(TKey key, TValue value)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }
            var node = new LinkedListNode<KeyValuePair<TKey, TValue>>(new KeyValuePair<TKey, TValue>(key, value));
            _order.AddFirst(node);
            _map[key] = node;
            while (_map.Count > Capacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }

        public bool Remove(TKey key)
        {
            if (!_map.TryGetValue(key, out var node)) return false;
            _order.Remove(node);
            _map.Remove(key);
            return true;
        }

        public void Clear()
        {
            _map.Clear();
            _order.Clear();
        }
    }
}
