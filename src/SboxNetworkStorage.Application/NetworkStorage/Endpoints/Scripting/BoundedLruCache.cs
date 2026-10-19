namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// A thread-safe cache with a fixed capacity. When full, the least recently used entry is evicted, so a hot script
/// stays compiled while cold ones age out. Clearing the whole cache at the limit would make every script
/// recompile at once.
/// </summary>
internal sealed class BoundedLruCache<TKey, TValue>(int capacity)
    where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _index = new();
    private readonly LinkedList<(TKey Key, TValue Value)> _recency = new();
    private readonly Lock _sync = new();

    public int Count
    {
        get
        {
            lock (_sync) return _index.Count;
        }
    }

    /// <summary>Returns the cached value and marks it most recently used.</summary>
    public bool TryGet(TKey key, out TValue value)
    {
        lock (_sync)
        {
            if (_index.TryGetValue(key, out var node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    /// <summary>Stores a value, evicting the least recently used entry when the cache is full.</summary>
    public void Set(TKey key, TValue value)
    {
        lock (_sync)
        {
            if (_index.TryGetValue(key, out var existing))
            {
                _recency.Remove(existing);
                _index.Remove(key);
            }
            else if (_index.Count >= capacity)
            {
                var oldest = _recency.Last!;
                _recency.RemoveLast();
                _index.Remove(oldest.Value.Key);
            }

            _index[key] = _recency.AddFirst((key, value));
        }
    }
}
