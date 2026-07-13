namespace Smb3Editor.App;

/// <summary>Bounded, process-only ROM preview cache. Nothing is written to disk.</summary>
internal sealed class CatalogPreviewMemoryCache
{
    private const long MaximumBytes = 24L * 1024 * 1024;
    private readonly object _sync = new();
    private readonly Dictionary<string, (CatalogPreviewData? Preview, LinkedListNode<string> Node, long Bytes)> _entries = [];
    private readonly LinkedList<string> _lru = [];
    private long _bytes;

    public bool TryGet(string key, out CatalogPreviewData? preview)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                preview = null;
                return false;
            }
            _lru.Remove(entry.Node);
            _lru.AddLast(entry.Node);
            preview = entry.Preview;
            return true;
        }
    }

    public void Add(string key, CatalogPreviewData? preview)
    {
        lock (_sync)
        {
            if (_entries.ContainsKey(key)) return;
            var bytes = preview is null ? 1 : preview.Pixels.Count * sizeof(uint);
            var node = _lru.AddLast(key);
            _entries[key] = (preview, node, bytes);
            _bytes += bytes;
            while (_bytes > MaximumBytes && _lru.First is { } oldest)
            {
                _lru.RemoveFirst();
                var entry = _entries[oldest.Value];
                _entries.Remove(oldest.Value);
                _bytes -= entry.Bytes;
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _lru.Clear();
            _bytes = 0;
        }
    }
}
