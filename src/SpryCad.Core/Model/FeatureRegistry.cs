using System.Text.Json.Serialization.Metadata;
using SpryCad.Core.Regen;

namespace SpryCad.Core.Model;

/// <summary>What a feature from another project sees of the rebuild: the part, the model so far, and its values.</summary>
public sealed class FeatureContext
{
    readonly Func<ModelState> _get;
    readonly Action<ModelState> _set;
    readonly Func<string?, string, ValueKind, double> _value;

    internal FeatureContext(CadDocument doc, Func<ModelState> get, Action<ModelState> set,
        Func<string?, string, ValueKind, double> value, CancellationToken cancel)
    {
        Doc = doc; _get = get; _set = set; _value = value; Cancel = cancel;
    }

    public CadDocument Doc { get; }
    public CancellationToken Cancel { get; }

    /// <summary>The model before the feature; set it to the model after.</summary>
    public ModelState State { get => _get(); set => _set(value); }

    /// <summary>A length (mm) from an expression of the part's variables; throws <see cref="FeatureException"/> naming <paramref name="what"/>.</summary>
    public double Length(string? expr, string what) => _value(expr, what, ValueKind.Length);
    /// <summary>An angle in degrees.</summary>
    public double Angle(string? expr, string what) => _value(expr, what, ValueKind.Angle);
    public double Number(string? expr, string what) => _value(expr, what, ValueKind.Number);
}

/// <summary>
/// Features defined outside SpryCad.Core (the sheet metal features): their JSON names, so parts save and open them,
/// and how each rebuilds. Registered once at startup, before any part is read (<see cref="Serialization.Options"/>
/// reads the list the first time it meets a feature).
/// </summary>
public static class FeatureRegistry
{
    sealed record Entry(Type Type, string Name, Action<Feature, FeatureContext> Execute, Func<Feature, CadDocument, string?>? Key);

    static readonly List<Entry> Entries = new();
    static readonly object Gate = new();

    /// <summary>
    /// Registers a feature type: its JSON name, how it rebuilds, and (<paramref name="key"/>) what else of the part
    /// its result depends on, as text for the rebuild cache (the feature itself and those before it are always in it).
    /// </summary>
    public static void Register<T>(string name, Action<T, FeatureContext> execute, Func<T, CadDocument, string?>? key = null) where T : Feature
    {
        lock (Gate)
        {
            if (Entries.Any(e => e.Type == typeof(T))) return;
            if (Entries.Any(e => e.Name == name)) throw new InvalidOperationException($"A feature is already registered as \"{name}\".");
            Entries.Add(new Entry(typeof(T), name, (f, c) => execute((T)f, c), key == null ? null : (f, d) => key((T)f, d)));
        }
    }

    /// <summary>What else of the part a registered feature depends on (null: nothing, or not registered).</summary>
    internal static string? KeyOf(Feature f, CadDocument doc)
    {
        Entry? e;
        lock (Gate) e = Entries.FirstOrDefault(x => x.Type == f.GetType());
        return e?.Key?.Invoke(f, doc);
    }

    /// <summary>Adds the registered features to <see cref="Feature"/>'s JSON names (a type info modifier).</summary>
    internal static void AddDerivedTypes(JsonTypeInfo ti)
    {
        if (ti.Type != typeof(Feature) || ti.PolymorphismOptions == null) return;
        lock (Gate)
            foreach (var e in Entries) ti.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(e.Type, e.Name));
    }

    /// <summary>Rebuilds a registered feature; false when <paramref name="f"/>'s type is not registered.</summary>
    internal static bool TryExecute(Feature f, FeatureContext ctx)
    {
        Entry? e;
        lock (Gate) e = Entries.FirstOrDefault(x => x.Type == f.GetType());
        if (e == null) return false;
        e.Execute(f, ctx);
        return true;
    }
}
