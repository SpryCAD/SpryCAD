using System.Text.Json;
using System.Text.Json.Serialization;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Core.Model;

public enum UnitSystem { MMGS, IPS }

/// <summary>
/// The native part document: the full parametric history (equations, sketches with constraints, features).
/// A *.sprt file is either plain, human-readable JSON, or — when the part embeds other files (e.g. an imported
/// STEP model) or the user prefers it — a ZIP container holding that same JSON as <c>model.json</c>, the
/// embedded files under <c>attachments/</c> and a <c>thumbnail.png</c> preview. Both open the same way.
/// </summary>
public sealed class CadDocument
{
    public const string FileExtension = ".sprt";
    /// <summary>
    /// 3: lengths in millimetres whatever <see cref="Units"/> (2 kept an inch part's in inches); 2: random ids (<see cref="Ids"/>);
    /// 1: counted ids, renewed on opening (<see cref="LegacyIds"/>).
    /// </summary>
    public const int CurrentFormatVersion = 3;

    public string Format { get; set; } = "SpryCAD";
    public int FormatVersion { get; set; } = CurrentFormatVersion;
    /// <summary>
    /// The part's identity (a GUID), given on its first save and kept for good: assemblies find the part by it
    /// wherever the file goes. Save As to another file gives the copy a new one (<see cref="ForkIdentity"/>).
    /// </summary>
    public string? Id { get; set; }
    /// <summary>A new GUID on every save: assemblies tell a changed part from the one they last saw.</summary>
    public string? Revision { get; set; }
    /// <summary>The part this one was saved as a copy of (its <see cref="Id"/>), if any.</summary>
    public string? DerivedFrom { get; set; }
    /// <summary>
    /// The units values are shown in, and a value typed without a unit is taken in (see <see cref="UnitFormat"/>). Only that:
    /// the model is in millimetres (and degrees) whatever they are, so changing them changes nothing in it.
    /// </summary>
    public UnitSystem Units { get; set; } = UnitSystem.MMGS;
    public List<Equation> Equations { get; set; } = new();
    /// <summary>Design tables: rows of values for sets of variables, one row in use (<see cref="DesignTable"/>). Null when none.</summary>
    public List<DesignTable>? Tables { get; set; }
    public List<Feature> Features { get; set; } = new();
    /// <summary>Number of active features (rollback bar position); null = all.</summary>
    public int? RollbackIndex { get; set; }
    public Dictionary<string, int> NameCounters { get; set; } = new();
    /// <summary>Part appearance (RGB 0-1).</summary>
    public double[] Color { get; set; } = { 0.74, 0.76, 0.80 };
    /// <summary>Material name, for mass properties (null: none set; steel's density is assumed).</summary>
    public string? Material { get; set; }
    /// <summary>Density in kg/m³ (null: steel, 7850).</summary>
    public double? Density { get; set; }
    public const double DefaultDensity = 7850;
    /// <summary>
    /// The library rows the features use (thread sizes, clearance holes), by item ("ISO261-coarse/M8"), copied in so the
    /// part rebuilds the same with or without the library (<see cref="Library.LibraryLink"/>); null when none.
    /// </summary>
    public SortedDictionary<string, Library.LibrarySnapshot>? Library { get; set; }
    /// <summary>The part's sheet metal bodies (sheet metal spec section 4); null when none.</summary>
    public List<SheetMetal.SheetMetalBody>? SheetBodies { get; set; }
    /// <summary>Profile values overridden on single bends, corners and hems; null when none.</summary>
    public List<SheetMetal.SheetMetalOverride>? SheetOverrides { get; set; }
    /// <summary>The shop profile rows the sheet metal bodies can use, copied in (null: none, or no sheet metal).</summary>
    public SheetMetal.ProfileSnapshot? SheetProfile { get; set; }

    public SheetMetal.SheetMetalBody? SheetBody(string id) => SheetBodies?.FirstOrDefault(b => b.Id == id);
    /// <summary>
    /// Sketches, reference planes and axes shown (true) or hidden (false) by the user, by feature id; one not listed is shown while no
    /// feature uses it. Display only: kept out of the features, so showing or hiding one rebuilds nothing.
    /// Null when nothing is set.
    /// </summary>
    public Dictionary<string, bool>? SketchVisibility { get; set; }

    /// <summary>Whether a sketch shows in the graphics area: as the user set it, else while no active feature uses it.</summary>
    public bool SketchShown(string id, bool consumed) => SketchVisibility?.TryGetValue(id, out var v) == true ? v : !consumed;

    /// <summary>Shows or hides a sketch; the automatic choice (shown while unused) when that is what it would be anyway.</summary>
    public void SetSketchShown(string id, bool shown, bool consumed)
    {
        if (shown == !consumed) SketchVisibility?.Remove(id);
        else (SketchVisibility ??= new())[id] = shown;
        if (SketchVisibility is { Count: 0 }) SketchVisibility = null;
    }

    /// <summary>
    /// What an active, unsuppressed feature uses: the sketches it consumes, and the reference planes and axes it refers
    /// to (a sketch on a plane, a plane offset from one, an extrude up to one). Used ones are hidden unless shown.
    /// </summary>
    public HashSet<string> UsedByActive()
    {
        var used = ConsumedByActive();
        var refs = Features.Take(ActiveCount).Where(f => f is PlaneFeature or AxisFeature && !f.Suppressed).Select(f => f.Id).ToHashSet();
        if (refs.Count == 0) return used;
        foreach (var f in Features.Take(ActiveCount))
        {
            if (f.Suppressed) continue;
            if (f is SketchFeature s) { if (s.Plane.Feature is { } pf && refs.Contains(pf)) used.Add(pf); continue; } // (a sketch's own geometry refers to no plane)
            if (refs.All(used.Contains)) break;
            var json = JsonOf(f);
            foreach (var id in refs) if (id != f.Id && Refers(json, id)) used.Add(id);
        }
        return used;
    }

    /// <summary>The features an active, unsuppressed feature uses (sketches hidden by default).</summary>
    public HashSet<string> ConsumedByActive()
    {
        var consumed = new HashSet<string>();
        foreach (var f in Features.Take(ActiveCount)) if (!f.Suppressed) foreach (var c in f.Consumes) consumed.Add(c);
        return consumed;
    }

    /// <summary>
    /// Index of the embedded files (name, size, SHA-256), written by <see cref="Save"/>; null when there are none.
    /// It keeps model.json self-describing (and a changed attachment visible in a diff).
    /// </summary>
    public List<AttachmentInfo>? Attachments { get; set; }

    /// <summary>Embedded files by name (e.g. "imports/bracket.step"); saved in the container, not in the JSON.</summary>
    [JsonIgnore] public Dictionary<string, byte[]> Files { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Preview image (PNG) stored in containers.</summary>
    [JsonIgnore] public byte[]? Thumbnail { get; set; }

    /// <summary>
    /// The part's display as last built (a display cache, <see cref="Assembly.MeshCache"/>), saved in containers so a slow
    /// part shows at once when opened, while it is rebuilt. Read only when made for this revision by the current
    /// geometry code; null otherwise, and when none was saved.
    /// </summary>
    [JsonIgnore] public byte[]? Display { get; set; }

    /// <summary>
    /// The part as built, given to a save to make the new revision's preview (its changes shown) when there is no
    /// <see cref="Display"/> to make it from; never saved itself.
    /// </summary>
    [JsonIgnore] public Kernel.DisplayMesh? PreviewMesh { get; set; }

    /// <summary>A new identity for a copy saved under another name: the original keeps its own.</summary>
    public void ForkIdentity()
    {
        if (Id != null) DerivedFrom = Id;
        Id = null; Revision = null;
    }

    public static string NewGuid() => Guid.NewGuid().ToString("D");

    public Feature? Find(string id) => Features.FirstOrDefault(f => f.Id == id);
    public int IndexOf(string id) => Features.FindIndex(f => f.Id == id);

    public string NewId() => Ids.NewFeatureId();

    /// <summary>SolidWorks style auto names: Sketch1, Boss-Extrude1, Fillet1...</summary>
    public string NewName(string kind)
    {
        var n = NameCounters.GetValueOrDefault(kind) + 1;
        NameCounters[kind] = n;
        return kind + n;
    }

    public T AddFeature<T>(T f, int? insertAt = null) where T : Feature
    {
        if (string.IsNullOrEmpty(f.Id)) f.Id = NewId();
        if (string.IsNullOrEmpty(f.Name)) f.Name = NewName(f.Kind);
        var at = insertAt ?? (RollbackIndex ?? Features.Count);
        Features.Insert(Math.Clamp(at, 0, Features.Count), f);
        if (RollbackIndex != null) RollbackIndex = at + 1;
        return f;
    }

    public int ActiveCount => Math.Clamp(RollbackIndex ?? Features.Count, 0, Features.Count);

    /// <summary>
    /// Features that refer to any of the given ones, directly or through other dependents: sketch/sketch-plane,
    /// face, edge and plane references, pattern seeds, model references in sketches. Found by looking for the
    /// ids in each feature's serialized definition, so new reference kinds are covered automatically.
    /// </summary>
    public List<Feature> DependentsOf(IEnumerable<string> ids)
    {
        var gone = new HashSet<string>(ids);
        var result = new List<Feature>();
        var jsons = Features.Select(JsonOf).ToList();
        bool grew = true;
        while (grew)
        {
            grew = false;
            for (int i = 0; i < Features.Count; i++)
            {
                var f = Features[i];
                if (gone.Contains(f.Id)) continue;
                var json = jsons[i];
                if (gone.Any(id => Refers(json, id)))
                {
                    gone.Add(f.Id); result.Add(f); grew = true;
                }
            }
        }
        return result;
    }

    public static string JsonOf(Feature f) => JsonSerializer.Serialize(f, typeof(Feature), Serialization.Compact);

    /// <summary>
    /// Whether a feature's serialized definition refers to feature <paramref name="id"/>: the id as a whole value
    /// (sketchId, seeds) or as a face/edge name prefix ("&lt;id&gt;.End").
    /// </summary>
    public static bool Refers(string featureJson, string id) => featureJson.Contains($"\"{id}\"") || featureJson.Contains($"\"{id}.");

    /// <summary>Features that consume the given one (e.g. the extrude using a sketch).</summary>
    public IEnumerable<Feature> ConsumersOf(string id) => Features.Where(f => f.Consumes.Contains(id) ||
        (f is LinearPatternFeature lp && lp.Seeds.Contains(id)) ||
        (f is CircularPatternFeature cp && cp.Seeds.Contains(id)) ||
        (f is MirrorFeature mf && mf.Seeds.Contains(id)));

    /// <summary>The document as JSON: indented as files are written, or compact (copies, undo snapshots).</summary>
    public string ToJson(bool indented = true) => JsonSerializer.Serialize(this, indented ? Serialization.Options : Serialization.Compact);
    public static CadDocument FromJson(string json)
    {
        var d = JsonSerializer.Deserialize<CadDocument>(json, Serialization.Options) ?? throw new InvalidDataException("Empty document");
        // (parts made before the product was renamed say FeatherCAD; saved again they say SpryCAD)
        if (d.Format == "FeatherCAD") d.Format = "SpryCAD";
        if (d.Format != "SpryCAD") throw new InvalidDataException("Not a SpryCAD document");
        if (d.FormatVersion <= LegacyIds.LastVersion) LegacyIds.Upgrade(d, json);
        return d;
    }

    /// <summary>Deep copy of the history; embedded files and thumbnail are shared (they are never modified in place).</summary>
    public CadDocument Clone()
    {
        var d = FromJson(ToJson(indented: false));
        d.Files = new Dictionary<string, byte[]>(Files, StringComparer.Ordinal);
        d.Thumbnail = Thumbnail;
        return d;
    }

    /// <summary>Embeds (or replaces) a file. Names are relative paths with forward slashes ("imports/part.step").</summary>
    public void AddFile(string name, byte[] content)
    {
        DocumentContainer.CheckName(name);
        Files[name] = content;
    }

    /// <summary>
    /// Saves the part: plain JSON, or a container when it has embedded files or <paramref name="container"/> is set.
    /// Written to a temporary file first, so a failed save never destroys the previous version.
    /// </summary>
    public void Save(string path, string? author = null) => DocumentContainer.Save(this, path, author);

    /// <summary>Loads plain JSON or container files.</summary>
    public static CadDocument Load(string path) => DocumentContainer.Load(path);

}

/// <summary>An embedded file as listed in model.json.</summary>
public sealed class AttachmentInfo
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public static class Serialization
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(), new Vec3Converter(), new Vec2Converter() },
        IncludeFields = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                // derived/computed properties never belong in the file
                ti =>
                {
                    for (int i = ti.Properties.Count - 1; i >= 0; i--)
                        if (ti.Properties[i].Set == null && ti.Properties[i].Name is "kind" or "consumes" or "pointIds" or "isDimension")
                            ti.Properties.RemoveAt(i);
                },
                // features of other projects (sheet metal) by their registered names
                FeatureRegistry.AddDerivedTypes,
            },
        },
    };

    /// <summary>The same without indentation: JSON that stays in memory (copies, undo, cache keys), about half the size.</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static object CloneObject(object o, Type t)
        => JsonSerializer.Deserialize(JsonSerializer.Serialize(o, t, Compact), t, Compact)!;

    public static T Clone<T>(T o) where T : class => (T)CloneObject(o, typeof(T));
}

/// <summary>Writes Vec3 as a compact [x, y, z] array.</summary>
public sealed class Vec3Converter : JsonConverter<Vec3>
{
    public override Vec3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected [x,y,z]");
        reader.Read(); var x = reader.GetDouble();
        reader.Read(); var y = reader.GetDouble();
        reader.Read(); var z = reader.GetDouble();
        reader.Read();
        return new Vec3(x, y, z);
    }

    public override void Write(Utf8JsonWriter writer, Vec3 v, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(Math.Round(v.X, 9)); writer.WriteNumberValue(Math.Round(v.Y, 9)); writer.WriteNumberValue(Math.Round(v.Z, 9));
        writer.WriteEndArray();
    }
}

public sealed class Vec2Converter : JsonConverter<Vec2>
{
    public override Vec2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected [x,y]");
        reader.Read(); var x = reader.GetDouble();
        reader.Read(); var y = reader.GetDouble();
        reader.Read();
        return new Vec2(x, y);
    }

    public override void Write(Utf8JsonWriter writer, Vec2 v, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(Math.Round(v.X, 9)); writer.WriteNumberValue(Math.Round(v.Y, 9));
        writer.WriteEndArray();
    }
}
