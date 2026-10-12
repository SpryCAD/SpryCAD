using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SpryCad.Core.Model;
using SpryCad.Kernel;

namespace SpryCad.Core.Storage;

/// <summary>The kinds of object a part is kept as (the first byte of an object id, and the envelope's "kind").</summary>
public enum ObjectKind : byte
{
    /// <summary>One per feature (a sketch feature holds its sketch).</summary>
    Feature = 1,
    /// <summary>The features' order (exactly one): a reorder changes this alone.</summary>
    FeatureOrder = 2,
    /// <summary>Equations and design tables (exactly one).</summary>
    Variables = 3,
    /// <summary>Color, material and density (exactly one).</summary>
    Material = 4,
    /// <summary>Units, sheet metal settings and the embedded files' names (exactly one).</summary>
    Settings = 5,
    /// <summary>One per embedded file: its bytes as they are (no envelope).</summary>
    File = 6,
    /// <summary>
    /// The counters new features are named by ("Fillet3"; exactly one): apart from the settings, as every added feature
    /// changes them and the history does not list that as a change of its own.
    /// </summary>
    NameCounters = 7,
}

/// <summary>
/// A part as the objects a revision lists (id → bytes) and back. Objects are canonical JSON in an envelope
/// <c>{"kind":1,"v":1,"data":…}</c>: the same part always gives the same bytes (properties in declaration order, no
/// indentation, maps sorted), so unchanged objects keep their hashes and are stored once.
/// <para>
/// An object id is 16 bytes: its kind, then for a feature its id ("F" + 12 characters, as ASCII), for a file the
/// first 15 bytes of the SHA-256 of its name, and zeros for the objects a part has exactly one of.
/// </para>
/// <para>
/// Not in revisions: what is not the model (the rollback bar, which sketches are shown: the manifest), what is made
/// from it (thumbnail, display: their own tables), and the part's identity and revision (the file's tables).
/// </para>
/// </summary>
public static class PartObjects
{
    public const int SchemaVersion = 1;
    public const int IdSize = 16;

    public static Dictionary<ObjectId, byte[]> From(CadDocument doc)
    {
        var objects = new Dictionary<ObjectId, byte[]>();
        foreach (var f in doc.Features)
            objects.Add(FeatureId(f.Id), Envelope(ObjectKind.Feature, CadDocument.JsonOf(f)));
        objects.Add(Single(ObjectKind.FeatureOrder), Envelope(ObjectKind.FeatureOrder, Json(doc.Features.Select(f => f.Id).ToList())));
        objects.Add(Single(ObjectKind.Variables), Envelope(ObjectKind.Variables, Json(new VariablesData { Equations = doc.Equations, Tables = doc.Tables })));
        objects.Add(Single(ObjectKind.Material), Envelope(ObjectKind.Material, Json(new MaterialData { Color = doc.Color, Material = doc.Material, Density = doc.Density })));
        var files = doc.Files.Keys.Order(StringComparer.Ordinal).ToList();
        objects.Add(Single(ObjectKind.Settings), Envelope(ObjectKind.Settings, Json(new SettingsData
        {
            Units = doc.Units, SheetMetal = doc.SheetMetal,
            Files = files.Count > 0 ? files : null,
        })));
        objects.Add(Single(ObjectKind.NameCounters), Envelope(ObjectKind.NameCounters, Json(new SortedDictionary<string, int>(doc.NameCounters, StringComparer.Ordinal))));
        foreach (var name in files) objects.Add(FileId(name), doc.Files[name]);
        return objects;
    }

    /// <summary>The part the objects make (its identity, revision and what is not in revisions are set by the caller).</summary>
    public static CadDocument ToDocument(IReadOnlyDictionary<ObjectId, byte[]> objects)
    {
        var doc = new CadDocument();
        T Data<T>(ObjectKind kind) => objects.TryGetValue(Single(kind), out var b) ? Open<T>(b, kind) : throw new InvalidDataException($"The file is damaged: its {kind} is missing.");
        var order = Data<List<string>>(ObjectKind.FeatureOrder);
        foreach (var id in order)
        {
            if (!objects.TryGetValue(FeatureId(id), out var b)) throw new InvalidDataException($"The file is damaged: feature {id} is missing.");
            doc.Features.Add(Open<Feature>(b, ObjectKind.Feature));
        }
        var v = Data<VariablesData>(ObjectKind.Variables);
        doc.Equations = v.Equations ?? new(); doc.Tables = v.Tables;
        var m = Data<MaterialData>(ObjectKind.Material);
        doc.Color = m.Color ?? doc.Color; doc.Material = m.Material; doc.Density = m.Density;
        var s = Data<SettingsData>(ObjectKind.Settings);
        doc.Units = s.Units; doc.SheetMetal = s.SheetMetal;
        // (in the settings in files saved before they had an object of their own)
        doc.NameCounters = new Dictionary<string, int>(objects.TryGetValue(Single(ObjectKind.NameCounters), out var nc)
            ? Open<SortedDictionary<string, int>>(nc, ObjectKind.NameCounters) : s.NameCounters ?? new());
        foreach (var name in s.Files ?? new())
            doc.Files[name] = objects.TryGetValue(FileId(name), out var fb) ? fb : throw new InvalidDataException($"The file is damaged: the embedded file \"{name}\" is missing.");
        return doc;
    }

    public static ObjectId FeatureId(string id)
    {
        var b = new byte[IdSize];
        b[0] = (byte)ObjectKind.Feature;
        if (id.Length <= IdSize - 1 && id.All(c => c < 128)) Encoding.ASCII.GetBytes(id, b.AsSpan(1));
        else SHA256.HashData(Encoding.UTF8.GetBytes(id)).AsSpan(0, IdSize - 1).CopyTo(b.AsSpan(1));
        return new ObjectId(b);
    }

    public static ObjectId FileId(string name)
    {
        var b = new byte[IdSize];
        b[0] = (byte)ObjectKind.File;
        SHA256.HashData(Encoding.UTF8.GetBytes(name)).AsSpan(0, IdSize - 1).CopyTo(b.AsSpan(1));
        return new ObjectId(b);
    }

    public static ObjectId Single(ObjectKind kind) { var b = new byte[IdSize]; b[0] = (byte)kind; return new ObjectId(b); }

    static string Json<T>(T value) => JsonSerializer.Serialize(value, Serialization.Compact);

    /// <summary>The envelope around an object's JSON.</summary>
    static byte[] Envelope(ObjectKind kind, string data) => Encoding.UTF8.GetBytes($"{{\"kind\":{(int)kind},\"v\":{SchemaVersion},\"data\":{data}}}");

    static T Open<T>(byte[] bytes, ObjectKind kind)
    {
        using var d = JsonDocument.Parse(bytes);
        var root = d.RootElement;
        if (root.GetProperty("kind").GetInt32() != (int)kind) throw new InvalidDataException($"The file is damaged: a {kind} object is not one.");
        if (root.GetProperty("v").GetInt32() > SchemaVersion) throw new InvalidDataException($"The part was saved by a newer SpryCAD ({kind} version {root.GetProperty("v").GetInt32()}).");
        return root.GetProperty("data").Deserialize<T>(Serialization.Options) ?? throw new InvalidDataException($"The file is damaged: an empty {kind}.");
    }

    sealed class VariablesData
    {
        public List<Equation>? Equations { get; set; }
        public List<DesignTable>? Tables { get; set; }
    }

    sealed class MaterialData
    {
        public double[]? Color { get; set; }
        public string? Material { get; set; }
        public double? Density { get; set; }
    }

    sealed class SettingsData
    {
        public UnitSystem Units { get; set; }
        public SheetMetalSettings? SheetMetal { get; set; }
        /// <summary>Only in files saved before <see cref="ObjectKind.NameCounters"/> (read, never written).</summary>
        public SortedDictionary<string, int>? NameCounters { get; set; }
        public List<string>? Files { get; set; }
    }
}

/// <summary>An object's id within a part (16 bytes: <see cref="PartObjects"/>), compared by value.</summary>
public readonly struct ObjectId : IEquatable<ObjectId>, IComparable<ObjectId>
{
    readonly byte[] _b;
    public ObjectId(byte[] bytes) { if (bytes.Length != PartObjects.IdSize) throw new ArgumentException("An object id is 16 bytes."); _b = bytes; }
    public ReadOnlySpan<byte> Bytes => _b;
    public ObjectKind Kind => (ObjectKind)_b[0];
    public byte[] ToArray() => (byte[])_b.Clone();
    public bool Equals(ObjectId o) => _b.AsSpan().SequenceEqual(o._b);
    public override bool Equals(object? obj) => obj is ObjectId o && Equals(o);
    public override int GetHashCode() { var h = new HashCode(); h.AddBytes(_b); return h.ToHashCode(); }
    /// <summary>By bytes: the order the root hash lists objects in.</summary>
    public int CompareTo(ObjectId o) => _b.AsSpan().SequenceCompareTo(o._b);
    public override string ToString() => Kind == ObjectKind.Feature && _b[1] < 128 ? Encoding.ASCII.GetString(_b, 1, PartObjects.IdSize - 1).TrimEnd('\0') : Convert.ToHexString(_b);
}
