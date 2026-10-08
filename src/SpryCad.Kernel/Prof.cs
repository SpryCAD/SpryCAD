using System.Collections.Concurrent;
using System.Diagnostics;

namespace SpryCad.Kernel;

/// <summary>
/// Minimal hierarchical-free profiler: <c>using (Prof.Time("stage")) { ... }</c> accumulates wall time
/// and call counts per name. Disabled by default; costs one bool check when off.
/// </summary>
public static class Prof
{
    public static bool Enabled;
    static readonly ConcurrentDictionary<string, (long ticks, int calls)> _acc = new();

    public readonly struct Scope : IDisposable
    {
        readonly string? _name; readonly long _start;
        internal Scope(string name) { _name = name; _start = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            if (_name == null) return;
            var dt = Stopwatch.GetTimestamp() - _start;
            _acc.AddOrUpdate(_name, (dt, 1), (_, v) => (v.ticks + dt, v.calls + 1));
        }
    }

    public static Scope Time(string name) => Enabled ? new Scope(name) : default;

    public static void Add(string name, double ms)
    {
        var dt = (long)(ms * Stopwatch.Frequency / 1000.0);
        _acc.AddOrUpdate(name, (dt, 1), (_, v) => (v.ticks + dt, v.calls + 1));
    }

    /// <summary>Counts <paramref name="calls"/> events without time (e.g. how many faces a stage visited).</summary>
    public static void Count(string name, int calls) => _acc.AddOrUpdate(name, (0, calls), (_, v) => (v.ticks, v.calls + calls));

    public static void Reset() => _acc.Clear();

    public static IEnumerable<(string name, double ms, int calls)> Results()
        => _acc.Select(kv => (kv.Key, kv.Value.ticks * 1000.0 / Stopwatch.Frequency, kv.Value.calls)).OrderByDescending(x => x.Item2);
}
