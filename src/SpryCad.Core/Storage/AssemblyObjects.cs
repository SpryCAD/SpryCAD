using System.Text.Json;
using SpryCad.Core.Assembly;
using SpryCad.Core.Model;

namespace SpryCad.Core.Storage;

/// <summary>
/// An assembly as the objects a revision lists, and back (as <see cref="PartObjects"/> for parts): one per component,
/// mate and pattern, and one each for the order they come in, the configurations, the exploded views, the counters
/// and the settings (units, and which embedded document each embedded name is).
/// <para>
/// Not in revisions: what is not the model, kept with the file (<see cref="State"/>): which components are hidden or
/// lightweight, the active configuration, and where each part was last found and which revision of it was seen (the
/// resolver's hints). Display caches and embedded documents are the file's (<see cref="AssemblyFile"/>).
/// </para>
/// </summary>
public static class AssemblyObjects
{
    /// <summary>The assembly's objects. <paramref name="embedded"/>: the documents embedded in it, by name (their ids).</summary>
    public static Dictionary<ObjectId, byte[]> From(AssemblyDocument doc, IReadOnlyDictionary<string, string> embedded)
    {
        var o = new Dictionary<ObjectId, byte[]>();
        void Add(ObjectKind kind, string? id, object value) =>
            o.Add(id == null ? PartObjects.Single(kind) : PartObjects.IdOf(kind, id), PartObjects.Envelope(kind, PartObjects.Json(value)));
        foreach (var c in doc.Components) Add(ObjectKind.Component, c.Id, Versioned(c));
        foreach (var m in doc.Mates) Add(ObjectKind.Mate, m.Id, m);
        foreach (var p in doc.Patterns ?? new()) Add(ObjectKind.ComponentPattern, p.Id, p);
        Add(ObjectKind.AssemblyOrder, null, new OrderData
        {
            Components = doc.Components.Select(c => c.Id).ToList(),
            Mates = doc.Mates.Select(m => m.Id).ToList(),
            Patterns = doc.Patterns?.Select(p => p.Id).ToList(),
        });
        Add(ObjectKind.AssemblyConfigurations, null, doc.Configurations ?? new());
        Add(ObjectKind.Explodes, null, doc.Explodes ?? new());
        Add(ObjectKind.AssemblyCounters, null, new CountersData
        {
            NextComponentId = doc.NextComponentId, NextMateId = doc.NextMateId, NextPatternId = doc.NextPatternId,
            MateCounters = new SortedDictionary<string, int>(doc.MateCounters, StringComparer.Ordinal),
        });
        Add(ObjectKind.AssemblySettings, null, new SettingsData
        {
            Units = doc.Units,
            Embedded = embedded.Count > 0 ? new SortedDictionary<string, string>(embedded.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal) : null,
        });
        return o;
    }

    /// <summary>The assembly the objects make, and the documents embedded in it (name → id). State and files are the caller's.</summary>
    public static (AssemblyDocument doc, Dictionary<string, string> embedded) ToDocument(IReadOnlyDictionary<ObjectId, byte[]> objects)
    {
        T Data<T>(ObjectKind kind) => objects.TryGetValue(PartObjects.Single(kind), out var b) ? PartObjects.Open<T>(b, kind)
            : throw new InvalidDataException($"The file is damaged: its {kind} is missing.");
        T Item<T>(ObjectKind kind, string id) => objects.TryGetValue(PartObjects.IdOf(kind, id), out var b) ? PartObjects.Open<T>(b, kind)
            : throw new InvalidDataException($"The file is damaged: {kind} {id} is missing.");
        var order = Data<OrderData>(ObjectKind.AssemblyOrder);
        var counters = Data<CountersData>(ObjectKind.AssemblyCounters);
        var settings = Data<SettingsData>(ObjectKind.AssemblySettings);
        var doc = new AssemblyDocument
        {
            Units = settings.Units,
            Components = order.Components.Select(id => Item<Component>(ObjectKind.Component, id)).ToList(),
            Mates = order.Mates.Select(id => Item<Mate>(ObjectKind.Mate, id)).ToList(),
            Patterns = order.Patterns?.Select(id => Item<ComponentPattern>(ObjectKind.ComponentPattern, id)).ToList(),
            NextComponentId = counters.NextComponentId, NextMateId = counters.NextMateId, NextPatternId = counters.NextPatternId,
            MateCounters = new Dictionary<string, int>(counters.MateCounters ?? new()),
        };
        var configs = Data<List<AssemblyConfiguration>>(ObjectKind.AssemblyConfigurations);
        doc.Configurations = configs.Count > 0 ? configs : null;
        var explodes = Data<List<ExplodeView>>(ObjectKind.Explodes);
        doc.Explodes = explodes.Count > 0 ? explodes : null;
        foreach (var m in doc.Mates) Mate.Normalize(m);
        return (doc, settings.Embedded != null ? new Dictionary<string, string>(settings.Embedded) : new());
    }

    /// <summary>The documents embedded in an assembly (name → id), from its settings object.</summary>
    internal static Dictionary<string, string> EmbeddedOf(byte[] settings) =>
        PartObjects.Open<SettingsData>(settings, ObjectKind.AssemblySettings).Embedded is { } e ? new(e) : new();

    /// <summary>A component as its revision keeps it: without what is not the model (<see cref="State"/>).</summary>
    static Component Versioned(Component c)
    {
        var v = (Component)Serialization.CloneObject(c, typeof(Component));
        v.Hidden = false; v.Lightweight = false;
        v.Part.Path = null; v.Part.AbsolutePath = null; v.Part.Revision = null;
        return v;
    }

    // ------------------------------------------------------------------------------------------
    // what is not in revisions

    /// <summary>The assembly's state that is not the model, as JSON (kept with the file, not in its history).</summary>
    public static string State(AssemblyDocument doc) => JsonSerializer.Serialize(new StateData
    {
        ActiveConfiguration = doc.ActiveConfiguration,
        Components = new SortedDictionary<string, ComponentState>(doc.Components.ToDictionary(c => c.Id, c => new ComponentState
        {
            Hidden = c.Hidden ? true : null, Lightweight = c.Lightweight ? true : null,
            Path = c.Part.Path, AbsolutePath = c.Part.AbsolutePath, Revision = c.Part.Revision,
        }), StringComparer.Ordinal),
    }, Serialization.Compact);

    /// <summary>Puts the state kept with the file (<see cref="State"/>) back into the assembly.</summary>
    public static void ApplyState(AssemblyDocument doc, string? json)
    {
        if (json == null) return;
        var s = JsonSerializer.Deserialize<StateData>(json, Serialization.Options);
        if (s == null) return;
        doc.ActiveConfiguration = s.ActiveConfiguration;
        foreach (var c in doc.Components)
            if (s.Components?.TryGetValue(c.Id, out var cs) == true)
            {
                c.Hidden = cs.Hidden == true; c.Lightweight = cs.Lightweight == true;
                c.Part.Path = cs.Path; c.Part.AbsolutePath = cs.AbsolutePath; c.Part.Revision = cs.Revision;
            }
    }

    sealed class OrderData
    {
        public List<string> Components { get; set; } = new();
        public List<string> Mates { get; set; } = new();
        public List<string>? Patterns { get; set; }
    }

    sealed class CountersData
    {
        public int NextComponentId { get; set; } = 1;
        public int NextMateId { get; set; } = 1;
        public int NextPatternId { get; set; } = 1;
        public SortedDictionary<string, int>? MateCounters { get; set; }
    }

    sealed class SettingsData
    {
        public UnitSystem Units { get; set; }
        /// <summary>The documents embedded in the assembly: its name for each ("parts/Bolt.sprt") → the document's id.</summary>
        public SortedDictionary<string, string>? Embedded { get; set; }
    }

    sealed class StateData
    {
        public string? ActiveConfiguration { get; set; }
        public SortedDictionary<string, ComponentState>? Components { get; set; }
    }

    sealed class ComponentState
    {
        public bool? Hidden { get; set; }
        public bool? Lightweight { get; set; }
        public string? Path { get; set; }
        public string? AbsolutePath { get; set; }
        public string? Revision { get; set; }
    }
}
