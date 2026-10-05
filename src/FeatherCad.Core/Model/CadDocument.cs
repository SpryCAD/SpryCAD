using System.Text.Json;
using System.Text.Json.Serialization;
using FeatherCad.Core.Sketching;
using FeatherCad.Kernel;

namespace FeatherCad.Core.Model;

public enum UnitSystem { MMGS, IPS }

/// <summary>
/// The native part document. Serialized as human-readable JSON (*.fcad) holding the full
/// parametric history: equations, sketches with constraints, and features.
/// </summary>
public sealed class CadDocument
{
    public const string FileExtension = ".fcad";
    public const int CurrentFormatVersion = 1;

    public string Format { get; set; } = "FeatherCAD";
    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public UnitSystem Units { get; set; } = UnitSystem.MMGS;
    public List<Equation> Equations { get; set; } = new();
    public List<Feature> Features { get; set; } = new();
    /// <summary>Number of active features (rollback bar position); null = all.</summary>
    public int? RollbackIndex { get; set; }
    public int NextFeatureId { get; set; } = 1;
    public Dictionary<string, int> NameCounters { get; set; } = new();
    /// <summary>Part appearance (RGB 0-1).</summary>
    public double[] Color { get; set; } = { 0.74, 0.76, 0.80 };

    public Feature? Find(string id) => Features.FirstOrDefault(f => f.Id == id);
    public int IndexOf(string id) => Features.FindIndex(f => f.Id == id);

    public string NewId() => "F" + NextFeatureId++;

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
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var f in Features)
            {
                if (gone.Contains(f.Id)) continue;
                var json = JsonSerializer.Serialize(f, typeof(Feature), Serialization.Options);
                // "F7" as a whole value (sketchId, seeds) or "F7.…" as a face/edge name prefix ("F17" does not match)
                if (gone.Any(id => json.Contains($"\"{id}\"") || json.Contains($"\"{id}.")))
                {
                    gone.Add(f.Id); result.Add(f); grew = true;
                }
            }
        }
        return result;
    }

    /// <summary>Features that consume the given one (e.g. the extrude using a sketch).</summary>
    public IEnumerable<Feature> ConsumersOf(string id) => Features.Where(f => f.Consumes.Contains(id) ||
        (f is LinearPatternFeature lp && lp.Seeds.Contains(id)) ||
        (f is CircularPatternFeature cp && cp.Seeds.Contains(id)) ||
        (f is MirrorFeature mf && mf.Seeds.Contains(id)));

    public string ToJson() => JsonSerializer.Serialize(this, Serialization.Options);
    public static CadDocument FromJson(string json)
    {
        var d = JsonSerializer.Deserialize<CadDocument>(json, Serialization.Options) ?? throw new InvalidDataException("Empty document");
        if (d.Format != "FeatherCAD") throw new InvalidDataException("Not a FeatherCAD document");
        return d;
    }

    public CadDocument Clone() => FromJson(ToJson());

    public void Save(string path) => File.WriteAllText(path, ToJson());
    public static CadDocument Load(string path) => FromJson(File.ReadAllText(path));
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
            },
        },
    };

    public static object CloneObject(object o, Type t)
        => JsonSerializer.Deserialize(JsonSerializer.Serialize(o, t, Options), t, Options)!;

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
