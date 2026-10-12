using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SpryCad.Core.Sketching;

namespace SpryCad.Core.Model;

/// <summary>
/// Parts saved before random ids (format version 1) counted them: features "F1", "F2", …, sketch items 1, 2, ….
/// Opening one gives every feature and sketch item a random-looking id (<see cref="Ids"/>) and rewrites every
/// reference to them, face and edge names included ("F2.S.L6" → "Fk3x9q2m7b1a.S.Lq8w…").
/// <para>
/// The new ids are derived from the part's identity and the old id, not drawn at random: the same old part gives
/// the same ids wherever it is opened (its file, a copy embedded in an assembly), and an assembly saved before
/// translates its mates' face names with the same <see cref="Name"/>.
/// </para>
/// </summary>
public static partial class LegacyIds
{
    public const int LastVersion = 1;

    /// <summary>Re-ids a part read from <paramref name="json"/> (format version 1): its features, sketches and every reference.</summary>
    public static void Upgrade(CadDocument doc, string json)
    {
        // a part saved before identities has none: it gets one from its content, the same for every copy of it
        doc.Id ??= new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(json)).AsSpan(0, 16)).ToString("D");
        var seed = doc.Id;
        doc.Revision = Revision(seed, doc.Revision);
        if (doc.SketchVisibility != null)
            doc.SketchVisibility = doc.SketchVisibility.ToDictionary(kv => Feature(seed, kv.Key), kv => kv.Value);
        foreach (var f in doc.Features) Visit(f, seed);
        doc.FormatVersion = CadDocument.CurrentFormatVersion;
    }

    /// <summary>
    /// The revision a re-id'd part is given (its old one renewed): display caches made for the old names are not
    /// its own any more, and an assembly saved before renews the revision it last saw the same way.
    /// </summary>
    public static string? Revision(string seed, string? revision) =>
        revision == null ? null : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(seed + "/" + revision)).AsSpan(0, 16)).ToString("D");

    /// <summary>The new id of an old feature id ("F7"); anything else is returned as it is.</summary>
    public static string Feature(string seed, string id)
    {
        var m = OldFeature().Match(id);
        if (!m.Success) return id;
        return Ids.FeatureId(Ids.Hash(seed, "feature", m.Groups[1].Value)) + m.Groups[2].Value;
    }

    /// <summary>The new id of an old sketch id (0, the origin, stays).</summary>
    public static long Sketch(string seed, long id) => id <= 0 ? id : Ids.SketchId(Ids.Hash(seed, "sketch", id.ToString()));

    /// <summary>
    /// A face, edge or body name with its old ids replaced: every "F&lt;n&gt;" part (a feature, or "F&lt;n&gt;-&lt;k&gt;", an
    /// imported body) and every sketch entity after a side marker ("S.L6", "R.A3", "H.L2", "X.C4"). New names are
    /// left as they are (their ids always hold a letter), so it can be applied more than once.
    /// </summary>
    public static string Name(string seed, string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var parts = name.Split('.');
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = Feature(seed, parts[i]);
            if (i > 0 && parts[i - 1] is "S" or "R" or "H" or "X" && OldEntity().Match(parts[i]) is { Success: true } m)
                parts[i] = m.Groups[1].Value + Ids.Name(Sketch(seed, long.Parse(m.Groups[2].Value)));
        }
        return string.Join('.', parts);
    }

    [GeneratedRegex(@"^F(\d+)(-\d+)?$")] private static partial Regex OldFeature();
    [GeneratedRegex(@"^([LACE])(\d+)$")] private static partial Regex OldEntity();

    // Walks the saved object graph (what is serialized), rewriting by what each value is: references by their
    // type, feature ids by property name, and every long (only sketch ids are longs).
    static void Visit(object o, string seed)
    {
        switch (o)
        {
            case FaceRef fr: fr.Name = Name(seed, fr.Name); fr.Body = NameOrNull(seed, fr.Body); return;
            case EdgeRef er: er.A = Name(seed, er.A); er.B = Name(seed, er.B); er.Body = NameOrNull(seed, er.Body); return;
            case SkConvertGroup g: g.Body = Name(seed, g.Body); g.Face = NameOrNull(seed, g.Face); break;
        }
        foreach (var p in Props(o.GetType()))
        {
            var v = p.GetValue(o);
            if (v == null) continue;
            switch (v)
            {
                case long l: p.SetValue(o, Sketch(seed, l)); break;
                case long[] ls: for (int i = 0; i < ls.Length; i++) ls[i] = Sketch(seed, ls[i]); break;
                case string s when IsFeatureRef(p): p.SetValue(o, Name(seed, s)); break;
                case List<string> l when IsFeatureRef(p): for (int i = 0; i < l.Count; i++) l[i] = Name(seed, l[i]); break;
                case string: break;
                case IEnumerable items: foreach (var x in items) if (x != null && IsModel(x.GetType())) Visit(x, seed); break;
                default: if (IsModel(v.GetType())) Visit(v, seed); break;
            }
        }
    }

    static string? NameOrNull(string seed, string? name) => name == null ? null : Name(seed, name);

    /// <summary>String properties holding feature (or body) ids.</summary>
    static bool IsFeatureRef(PropertyInfo p) =>
        (p.DeclaringType == typeof(Feature) && p.Name == nameof(Model.Feature.Id))
        || p.Name.EndsWith("SketchId", StringComparison.Ordinal)
        || p.Name is "Seeds" or "MainBody" or "ToolBodies"
        || (p.Name is "Feature" && p.DeclaringType == typeof(PlaneRef))
        || (p.Name is "Sketch" && (p.DeclaringType == typeof(CurveRef) || p.DeclaringType == typeof(PointRef)));

    static bool IsModel(Type t) => t.Namespace is "SpryCad.Core.Model" or "SpryCad.Core.Sketching";

    static readonly Dictionary<Type, PropertyInfo[]> _props = new();
    static PropertyInfo[] Props(Type t)
    {
        lock (_props)
        {
            if (!_props.TryGetValue(t, out var ps))
                _props[t] = ps = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
                    .ToArray();
            return ps;
        }
    }
}
