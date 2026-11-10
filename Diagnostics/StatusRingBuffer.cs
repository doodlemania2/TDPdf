namespace TDPdf.Diagnostics
{
    public sealed class StatusRingBuffer
    {
        private readonly string?[] _entries;
        private readonly object _gate = new object();
        private int _next;
        private int _count;

        public StatusRingBuffer(int capacity = 50)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _entries = new string?[capacity];
        }

        public int Capacity => _entries.Length;

        public void Add(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            lock (_gate)
            {
                _entries[_next] = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}";
                _next = (_next + 1) % _entries.Length;
                _count = Math.Min(_count + 1, _entries.Length);
            }
        }

        public string[] Snapshot()
        {
            lock (_gate)
            {
                var result = new string[_count];
                int start = (_next - _count + _entries.Length) % _entries.Length;

                for (int i = 0; i < _count; i++)
                    result[i] = _entries[(start + i) % _entries.Length] ?? string.Empty;

                return result;
            }
        }
    }
}
