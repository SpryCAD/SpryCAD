using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SpryCad.Core.Model;

/// <summary>
/// Where a part's variables are used (features' values, sketch dimensions, Suppress When conditions, other equations,
/// design table cells), and renaming one everywhere it is. Only values are looked at, never names and ids: a feature
/// called "Boss-Extrude1" is not Boss minus Extrude1.
/// </summary>
public static class VariableUses
{
    /// <summary>A use: the feature (null for an equation or a design table), what holds the value, and what that is.</summary>
    public sealed record Use(string? FeatureId, string Owner, string What);

    // properties that are names, ids and references, never values
    static readonly HashSet<string> NotValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "Name", "Id", "Description", "File", "Sha256", "Key", "Selected", "Kind", "Std", "Standard", "Size", "Fit", "Item",
        "Designation", "Body", "Feature", "A", "B", "Format", "Revision", "DerivedFrom", "Material", "MainBody", "Library", "Profile",
        "Face", "Edge", "Type", "Mode", "End", "End2",
    };
    static bool IsValueProperty(string p) => !NotValues.Contains(p) && !p.EndsWith("Id", StringComparison.Ordinal) && !p.EndsWith("Ids", StringComparison.Ordinal);
    /// <summary>Whether a feature's property (by its JSON name) holds a value, not a name, an id or a reference.</summary>
    public static bool IsValue(string property) => IsValueProperty(property);

    /// <summary>Every variable used in the part, with its uses.</summary>
    public static Dictionary<string, List<Use>> Map(CadDocument doc)
    {
        var map = new Dictionary<string, List<Use>>(StringComparer.OrdinalIgnoreCase);
        void Add(string text, Use u)
        {
            foreach (var id in Expr.Identifiers(text).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!map.TryGetValue(id, out var list)) map[id] = list = new();
                if (!list.Contains(u)) list.Add(u);
            }
        }
        foreach (var f in doc.Features)
            Walk(JsonNode.Parse(CadDocument.JsonOf(f)), null, f.Name, (text, what) => Add(text, new Use(f.Id, f.Name, what)));
        foreach (var e in doc.Equations) Add(e.Expr, new Use(null, e.Name, "equation"));
        foreach (var t in doc.Tables ?? new())
            foreach (var r in t.Rows)
                foreach (var v in r.Values) Add(v, new Use(null, t.Name, "design table"));
        return map;
    }

    // each value under the node: its text, and what holds it (a sketch dimension's name, else the property's: "Depth")
    static void Walk(JsonNode? node, JsonObject? parent, string featureName, Action<string, string> value)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                {
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var s))
                    {
                        if (IsValueProperty(k) && s.Length > 0)
                            value(s, o.TryGetPropertyValue("Name", out var n) && n is JsonValue nv && nv.TryGetValue<string>(out var nm) && nm != featureName ? nm : k);
                    }
                    else Walk(v, o, featureName, value);
                }
                break;
            case JsonArray a:
                foreach (var v in a) Walk(v, parent, featureName, value);
                break;
        }
    }

    /// <summary>
    /// Renames variable <paramref name="from"/> to <paramref name="to"/> everywhere in the part: its equation or design
    /// table column, and every value using it. The number of values changed.
    /// </summary>
    public static int Rename(CadDocument doc, string from, string to)
    {
        var rx = new Regex($@"(?<![A-Za-z0-9_.]){Regex.Escape(from)}(?![A-Za-z0-9_(])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        int n = 0;
        string Swap(string s)
        {
            if (!Expr.Identifiers(s).Any(i => i.Equals(from, StringComparison.OrdinalIgnoreCase))) return s;
            var r = rx.Replace(s, to);
            if (r != s) n++;
            return r;
        }
        foreach (var e in doc.Equations)
        {
            if (e.Name.Trim().Equals(from, StringComparison.OrdinalIgnoreCase)) e.Name = to;
            e.Expr = Swap(e.Expr);
        }
        foreach (var t in doc.Tables ?? new())
        {
            if (t.Name.Trim().Equals(from, StringComparison.OrdinalIgnoreCase)) t.Name = to;
            for (int c = 0; c < t.Columns.Count; c++) if (t.Columns[c].Trim().Equals(from, StringComparison.OrdinalIgnoreCase)) t.Columns[c] = to;
            foreach (var r in t.Rows) for (int c = 0; c < r.Values.Count; c++) r.Values[c] = Swap(r.Values[c]);
        }
        for (int i = 0; i < doc.Features.Count; i++)
        {
            var node = JsonNode.Parse(CadDocument.JsonOf(doc.Features[i]));
            var before = n;
            Replace(node, Swap);
            if (n != before && node!.Deserialize(typeof(Feature), Serialization.Compact) is Feature f) doc.Features[i] = f;
        }
        return n;
    }

    static void Replace(JsonNode? node, Func<string, string> swap)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var k in o.Select(kv => kv.Key).ToList())
                {
                    var v = o[k];
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) { if (IsValueProperty(k) && swap(s) is var r && r != s) o[k] = r; }
                    else Replace(v, swap);
                }
                break;
            case JsonArray a:
                foreach (var v in a) Replace(v, swap);
                break;
        }
    }
}
