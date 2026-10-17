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
    /// <summary>Units and the embedded files' names (exactly one).</summary>
    Settings = 5,
    /// <summary>One per embedded file: its bytes as they are (no envelope).</summary>
    File = 6,
    /// <summary>
    /// The counters new features are named by ("Fillet3"; exactly one): apart from the settings, as every added feature
    /// changes them and the history does not list that as a change of its own.
    /// </summary>
    NameCounters = 7,
    /// <summary>One per library item the part uses ("ISO261-coarse:M8"): the rows it rebuilds from (section 34).</summary>
    LibrarySnapshot = 8,
    /// <summary>One per sheet metal body, by its id: material, gauge, fixed panel, overrides (sheet metal spec section 4).</summary>
    SheetMetalBody = 9,
    /// <summary>One per bend, corner or hem whose profile values were overridden, by its persistent id.</summary>
    SheetMetalOverride = 10,
    /// <summary>The shop profile rows the sheet metal bodies can use (at most one).</summary>
    SheetProfile = 11,

    // an assembly's (AssemblyObjects)
    /// <summary>Units and the documents (parts, sub-assemblies) embedded in it by name (exactly one).</summary>
    AssemblySettings = 20,
    /// <summary>The counters new components, mates and patterns are numbered and named by (exactly one; not listed in history).</summary>
    AssemblyCounters = 21,
    /// <summary>One per component.</summary>
    Component = 22,
    /// <summary>One per mate.</summary>
    Mate = 23,
    /// <summary>One per component pattern.</summary>
    ComponentPattern = 24,
    /// <summary>The order of the components, mates and patterns (exactly one).</summary>
    AssemblyOrder = 25,
    /// <summary>The configurations (exactly one).</summary>
    AssemblyConfigurations = 26,
    /// <summary>The exploded views (exactly one).</summary>
    Explodes = 27,

    // a library's (Library.LibraryFile)
    /// <summary>One per table, by its key.</summary>
    LibraryTable = 40,
    /// <summary>What a company library allows of the tables below it (at most one).</summary>
    LibraryRestrictions = 41,
    /// <summary>One per shop profile table (sheet metal), by its key.</summary>
    ShopProfileTable = 42,
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
        // (the color, material name and density as an older SpryCAD reads them, besides what they come from)
        objects.Add(Single(ObjectKind.Material), Envelope(ObjectKind.Material, Json(new MaterialData
        {
            MaterialItem = doc.MaterialItem, Appearance = doc.Appearance,
            Color = doc.Color, Material = doc.MaterialName, Density = doc.MaterialItem != null ? doc.EffectiveDensity : null,
        })));
        var files = doc.Files.Keys.Order(StringComparer.Ordinal).ToList();
        objects.Add(Single(ObjectKind.Settings), Envelope(ObjectKind.Settings, Json(new SettingsData
        {
            Units = doc.Units,
            Files = files.Count > 0 ? files : null,
        })));
        objects.Add(Single(ObjectKind.NameCounters), Envelope(ObjectKind.NameCounters, Json(new SortedDictionary<string, int>(doc.NameCounters, StringComparer.Ordinal))));
        foreach (var name in files) objects.Add(FileId(name), doc.Files[name]);
        foreach (var (item, snap) in doc.Library ?? new())
            objects.Add(IdOf(ObjectKind.LibrarySnapshot, item), Envelope(ObjectKind.LibrarySnapshot, Json(snap)));
        foreach (var b in doc.SheetBodies ?? new())
            objects.Add(IdOf(ObjectKind.SheetMetalBody, b.Id), Envelope(ObjectKind.SheetMetalBody, Json(b)));
        foreach (var o in doc.SheetOverrides ?? new())
            objects.Add(IdOf(ObjectKind.SheetMetalOverride, o.Target), Envelope(ObjectKind.SheetMetalOverride, Json(o)));
        if (doc.SheetProfile != null) objects.Add(Single(ObjectKind.SheetProfile), Envelope(ObjectKind.SheetProfile, Json(doc.SheetProfile)));
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
        doc.MaterialItem = m.MaterialItem; doc.Appearance = m.Appearance;
        // (from before material libraries: turned into a material and an appearance once the library rows are read, below)
        if (m.MaterialItem == null && m.Appearance == null) { doc.LegacyColor = m.Color; doc.LegacyMaterial = m.Material; doc.LegacyDensity = m.Density; }
        var s = Data<SettingsData>(ObjectKind.Settings);
        doc.Units = s.Units;
        // (in the settings in files saved before they had an object of their own)
        doc.NameCounters = new Dictionary<string, int>(objects.TryGetValue(Single(ObjectKind.NameCounters), out var nc)
            ? Open<SortedDictionary<string, int>>(nc, ObjectKind.NameCounters) : s.NameCounters ?? new());
        foreach (var name in s.Files ?? new())
            doc.Files[name] = objects.TryGetValue(FileId(name), out var fb) ? fb : throw new InvalidDataException($"The file is damaged: the embedded file \"{name}\" is missing.");
        foreach (var (id, b) in objects)
            if (id.Kind == ObjectKind.LibrarySnapshot)
            {
                var snap = Open<Library.LibrarySnapshot>(b, ObjectKind.LibrarySnapshot);
                (doc.Library ??= new(StringComparer.Ordinal))[snap.Item] = snap;
            }
        // sheet metal: bodies and overrides in a stable order (by id), whatever order the objects come in
        foreach (var (id, b) in objects.OrderBy(kv => kv.Key))
        {
            if (id.Kind == ObjectKind.SheetMetalBody) (doc.SheetBodies ??= new()).Add(Open<SheetMetal.SheetMetalBody>(b, ObjectKind.SheetMetalBody));
            else if (id.Kind == ObjectKind.SheetMetalOverride) (doc.SheetOverrides ??= new()).Add(Open<SheetMetal.SheetMetalOverride>(b, ObjectKind.SheetMetalOverride));
        }
        doc.SheetBodies?.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
        doc.SheetOverrides?.Sort((x, y) => string.CompareOrdinal(x.Target, y.Target));
        if (objects.TryGetValue(Single(ObjectKind.SheetProfile), out var sp)) doc.SheetProfile = Open<SheetMetal.ProfileSnapshot>(sp, ObjectKind.SheetProfile);
        Library.LibraryLink.Upgrade(doc);
        Library.PartMaterial.Upgrade(doc);
        return doc;
    }

    public static ObjectId FeatureId(string id) => IdOf(ObjectKind.Feature, id);

    /// <summary>An object's id by its kind and its own id: as ASCII when it fits, else the first bytes of its hash.</summary>
    public static ObjectId IdOf(ObjectKind kind, string id)
    {
        var b = new byte[IdSize];
        b[0] = (byte)kind;
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

    // ---- for reverting (PartRevert): the parts of objects it reads and makes

    /// <summary>The feature ids a feature order object lists, in order.</summary>
    internal static List<string> OrderOf(byte[] envelope) => Open<List<string>>(envelope, ObjectKind.FeatureOrder);

    internal static byte[] OrderObject(IEnumerable<string> ids) => Envelope(ObjectKind.FeatureOrder, Json(ids.ToList()));

    /// <summary>How a library snapshot object is called in history ("M8 (ISO261-coarse)").</summary>
    internal static string SnapshotName(byte[] envelope)
    {
        var s = Open<Library.LibrarySnapshot>(envelope, ObjectKind.LibrarySnapshot);
        return s.Name;
    }

    /// <summary>The embedded files' names a settings object lists.</summary>
    internal static List<string> FileNamesOf(byte[] settings) => Open<SettingsData>(settings, ObjectKind.Settings).Files ?? new();

    /// <summary>The settings object with another list of embedded files.</summary>
    internal static byte[] WithFiles(byte[] settings, IEnumerable<string> names)
    {
        var d = Open<SettingsData>(settings, ObjectKind.Settings);
        var l = names.Order(StringComparer.Ordinal).ToList();
        d.Files = l.Count > 0 ? l : null;
        d.NameCounters = null;
        return Envelope(ObjectKind.Settings, Json(d));
    }

    /// <summary>A feature object's feature id and name.</summary>
    internal static (string id, string name) FeatureOf(byte[] envelope)
    {
        using var d = JsonDocument.Parse(envelope);
        var data = d.RootElement.GetProperty("data");
        return (data.GetProperty("id").GetString() ?? "", data.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "");
    }

    internal static string Json<T>(T value) => JsonSerializer.Serialize(value, Serialization.Compact);

    /// <summary>The envelope around an object's JSON.</summary>
    internal static byte[] Envelope(ObjectKind kind, string data) => Encoding.UTF8.GetBytes($"{{\"kind\":{(int)kind},\"v\":{SchemaVersion},\"data\":{data}}}");

    internal static T Open<T>(byte[] bytes, ObjectKind kind)
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
        /// <summary>The part's material item and its own appearance (null: none): what the part is made from.</summary>
        public string? MaterialItem { get; set; }
        public Appearance? Appearance { get; set; }
        /// <summary>The color shown, the material's name and density (null: none set): all an older SpryCAD reads.</summary>
        public double[]? Color { get; set; }
        public string? Material { get; set; }
        public double? Density { get; set; }
    }

    sealed class SettingsData
    {
        public UnitSystem Units { get; set; }
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
