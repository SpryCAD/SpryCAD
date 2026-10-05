namespace FeatherCad.Kernel;

/// <summary>Small thread-safe least-recently-used cache.</summary>
public sealed class Lru<TKey, TValue> where TKey : notnull
{
    readonly int _capacity;
    readonly Dictionary<TKey, LinkedListNode<(TKey key, TValue value)>> _map = new();
    readonly LinkedList<(TKey key, TValue value)> _order = new();
    readonly object _lock = new();

    public Lru(int capacity) { _capacity = capacity; }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node); _order.AddFirst(node);
                value = node.Value.value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Add(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var old)) _order.Remove(old);
            _map[key] = _order.AddFirst((key, value));
            while (_map.Count > _capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.key);
            }
        }
    }

    public void Clear() { lock (_lock) { _map.Clear(); _order.Clear(); } }
}
