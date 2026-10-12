using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SpryCad.Core.Storage;

/// <summary>
/// What changed in an object, in words (the file format spec, section 7): "Fillet3: radius 2 → 3", "Sketch1: D1 25 →
/// 30; 2 lines added". Features are compared field by field; a sketch by its dimensions and the geometry it has;
/// the rest in a word or two.
/// </summary>
/// <summary>A change between two revisions with its object's name and what happened, in words.</summary>
public sealed record DescribedChange(ObjectChange Change, string Name, string Text);

public static class PartDescribe
{
    const int MaxItems = 5;

    /// <summary>
    /// What happened to the object between two versions (null: it was not there). <paramref name="name"/> is how it is
    /// called (a feature's name).
    /// </summary>
    public static string Describe(ObjectKind kind, byte[]? old, byte[]? @new, string name)
    {
        if (old == null) return kind == ObjectKind.File ? $"Embedded file {name} added" : $"{name} added";
        if (@new == null) return kind == ObjectKind.File ? $"Embedded file {name} removed" : $"{name} removed";
        switch (kind)
        {
            case ObjectKind.Feature:
                {
                    var a = Data(old); var b = Data(@new);
                    var items = new List<string>();
                    if (Str(a, "name") is { } an && Str(b, "name") is { } bn && an != bn) items.Add($"renamed {an} → {bn}");
                    if (a.TryGetProperty("sketch", out var sa) && b.TryGetProperty("sketch", out var sb)) items.AddRange(Sketch(sa, sb));
                    Fields(a, b, "", items, skip: new[] { "id", "name", "sketch", "$type" });
                    return $"{name}: " + (items.Count == 0 ? "changed" : Join(items));
                }
            case ObjectKind.FeatureOrder: return "Features reordered";
            case ObjectKind.Variables:
                {
                    var items = Equations(Data(old), Data(@new));
                    return "Equations: " + (items.Count == 0 ? "changed" : Join(items));
                }
            case ObjectKind.Material:
                {
                    var items = new List<string>();
                    Fields(Data(old), Data(@new), "", items, skip: Array.Empty<string>());
                    return "Material: " + (items.Count == 0 ? "changed" : Join(items));
                }
            case ObjectKind.Settings: return "Part settings changed (units, sheet metal or embedded files)";
            case ObjectKind.File: return $"Embedded file {name} replaced";
            default: return $"{name} changed";
        }
    }

    /// <summary>
    /// Every change from revision <paramref name="from"/> (null: none) to <paramref name="to"/>, with its object's name and
    /// what happened to it in words, features first in the order the newer revision has them.
    /// </summary>
    public static List<DescribedChange> Changes(PartDatabase db, Guid? from, Guid to)
    {
        Dictionary<ObjectId, byte[]> Load(Guid r) => db.GetObjects(r).ToDictionary(kv => kv.Key, kv => db.Blobs.Get(kv.Value));
        return Changes(from is { } f ? Load(f) : new(), Load(to));
    }

    /// <summary>Every change from one state of a part to another (two documents: the part as it is now, unsaved), described.</summary>
    public static List<DescribedChange> Changes(Model.CadDocument older, Model.CadDocument newer) => Changes(PartObjects.From(older), PartObjects.From(newer));

    static List<DescribedChange> Changes(Dictionary<ObjectId, byte[]> a, Dictionary<ObjectId, byte[]> b)
    {
        byte[]? Bytes(Dictionary<ObjectId, byte[]> o, ObjectId id) => o.TryGetValue(id, out var x) ? x : null;
        var settings = PartObjects.Single(ObjectKind.Settings);
        var files = new Dictionary<ObjectId, string>();
        foreach (var o in new[] { a, b })
            if (Bytes(o, settings) is { } sb) foreach (var n in PartObjects.FileNamesOf(sb)) files.TryAdd(PartObjects.FileId(n), n);
        var orderId = PartObjects.Single(ObjectKind.FeatureOrder);
        var order = Bytes(b, orderId) is { } ob ? PartObjects.OrderOf(ob) : new List<string>();
        var changes = new List<ObjectChange>();
        foreach (var (id, nb) in b)
        {
            if (id.Kind == ObjectKind.NameCounters) continue;
            if (!a.TryGetValue(id, out var na)) { if (id.Kind != ObjectKind.FeatureOrder) changes.Add(new(id, ChangeKind.Added, null, Hash.Of(nb))); }
            else if (!na.AsSpan().SequenceEqual(nb))
            {
                if (id.Kind != ObjectKind.FeatureOrder) changes.Add(new(id, ChangeKind.Modified, Hash.Of(na), Hash.Of(nb)));
                else
                {
                    // (features added or removed change the order object too: only another order of those both have is a reorder)
                    var x = PartObjects.OrderOf(na); var y = PartObjects.OrderOf(nb);
                    var common = x.Intersect(y).ToHashSet();
                    if (!x.Where(common.Contains).SequenceEqual(y.Where(common.Contains))) changes.Add(new(id, ChangeKind.Reordered, Hash.Of(na), Hash.Of(nb)));
                }
            }
        }
        foreach (var (id, na) in a)
            if (!b.ContainsKey(id) && id.Kind != ObjectKind.NameCounters) changes.Add(new(id, ChangeKind.Removed, Hash.Of(na), null));
        var list = new List<DescribedChange>();
        foreach (var c in changes)
        {
            var oldB = Bytes(a, c.ObjectId); var newB = Bytes(b, c.ObjectId);
            var name = c.ObjectId.Kind switch
            {
                ObjectKind.Feature => PartObjects.FeatureOf(newB ?? oldB!).name,
                ObjectKind.File => files.TryGetValue(c.ObjectId, out var n) ? n : "an embedded file",
                ObjectKind.FeatureOrder => "Feature order",
                ObjectKind.Variables => "Equations",
                _ => c.ObjectId.Kind.ToString(),
            };
            list.Add(new DescribedChange(c, name, Describe(c.ObjectId.Kind, oldB, newB, name)));
        }
        int Rank(DescribedChange d) => d.Change.ObjectId.Kind == ObjectKind.Feature
            ? (order.IndexOf(PartObjects.FeatureOf(Bytes(b, d.Change.ObjectId) ?? Bytes(a, d.Change.ObjectId)!).id) is var i and >= 0 ? i : order.Count) : order.Count + 1 + (int)d.Change.ObjectId.Kind;
        return list.OrderBy(Rank).ToList();
    }

    // a feature's (or any object's) fields: each changed value, by where it is ("depth", "end condition")
    static void Fields(JsonElement a, JsonElement b, string path, List<string> items, string[] skip)
    {
        if (items.Count > MaxItems) return;
        if (a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object)
        {
            var names = a.EnumerateObject().Select(p => p.Name).Union(b.EnumerateObject().Select(p => p.Name)).Where(n => !skip.Contains(n)).ToList();
            foreach (var n in names)
            {
                bool ha = a.TryGetProperty(n, out var va), hb = b.TryGetProperty(n, out var vb);
                var p = path.Length == 0 ? Words(n) : path + " " + Words(n);
                if (!ha) items.Add($"{p} set to {Value(vb)}");
                else if (!hb) items.Add($"{p} {Value(va)} cleared");
                else Fields(va, vb, p, items, Array.Empty<string>());
            }
            return;
        }
        if (a.ValueKind == JsonValueKind.Array && b.ValueKind == JsonValueKind.Array)
        {
            int na = a.GetArrayLength(), nb = b.GetArrayLength();
            if (na != nb) { items.Add($"{path}: {Count(na)} → {Count(nb)}"); return; }
            if (a.EnumerateArray().All(Leaf) && b.EnumerateArray().All(Leaf))
            {
                if (a.GetRawText() != b.GetRawText()) items.Add($"{path} {Value(a)} → {Value(b)}");
                return;
            }
            int k = 0;
            foreach (var (x, y) in a.EnumerateArray().Zip(b.EnumerateArray()))
            {
                k++;
                if (x.GetRawText() != y.GetRawText()) Fields(x, y, $"{path} {k}", items, Array.Empty<string>());
            }
            return;
        }
        if (a.GetRawText() != b.GetRawText()) items.Add($"{path} {Value(a)} → {Value(b)}");
    }

    static bool Leaf(JsonElement e) => e.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array);

    static string Count(int n) => n == 1 ? "1 item" : $"{n} items";

    // a sketch: its dimensions by name, and how much geometry and how many relations it has
    static IEnumerable<string> Sketch(JsonElement a, JsonElement b)
    {
        var items = new List<string>();
        Dictionary<string, string> Dims(JsonElement s) => Arr(s, "constraints")
            .Where(c => Str(c, "name") is not null)
            .GroupBy(c => Str(c, "name")!).ToDictionary(g => g.Key, g => Str(g.First(), "expr") ?? Num(g.First(), "value"));
        var da = Dims(a); var db = Dims(b);
        foreach (var (n, v) in db) if (da.TryGetValue(n, out var old) && old != v) items.Add($"{n} {old} → {v}");
        foreach (var n in db.Keys.Except(da.Keys)) items.Add($"dimension {n} added");
        foreach (var n in da.Keys.Except(db.Keys)) items.Add($"dimension {n} removed");
        // geometry by kind (ids: what was added or removed, not renumbered)
        foreach (var kind in new[] { "line", "arc", "circle", "point" })
        {
            var ia = Ids(a, "entities", kind); var ib = Ids(b, "entities", kind);
            int added = ib.Except(ia).Count(), removed = ia.Except(ib).Count();
            if (added > 0) items.Add($"{added} {Plural(kind, added)} added");
            if (removed > 0) items.Add($"{removed} {Plural(kind, removed)} removed");
        }
        var ca = Arr(a, "constraints").Where(c => Str(c, "name") is null).Select(c => c.GetProperty("id").GetRawText()).ToHashSet();
        var cb = Arr(b, "constraints").Where(c => Str(c, "name") is null).Select(c => c.GetProperty("id").GetRawText()).ToHashSet();
        int ra = cb.Except(ca).Count(), rr = ca.Except(cb).Count();
        if (ra > 0) items.Add($"{ra} relation{(ra == 1 ? "" : "s")} added");
        if (rr > 0) items.Add($"{rr} relation{(rr == 1 ? "" : "s")} removed");
        if (items.Count == 0 && Arr(a, "points").Select(p => p.GetRawText()).SequenceEqual(Arr(b, "points").Select(p => p.GetRawText())) is false)
            items.Add("geometry moved");
        if (items.Count == 0 && a.GetRawText() != b.GetRawText()) items.Add("sketch changed");
        return items;
    }

    static HashSet<string> Ids(JsonElement s, string list, string type) =>
        Arr(s, list).Where(e => Str(e, "$type") == type).Select(e => e.GetProperty("id").GetRawText()).ToHashSet();

    static string Plural(string kind, int n) => n == 1 ? kind : kind + "s";

    // equations by name: each changed, added or removed
    static List<string> Equations(JsonElement a, JsonElement b)
    {
        Dictionary<string, string> Of(JsonElement d) => Arr(d, "equations").Where(e => Str(e, "name") is not null)
            .GroupBy(e => Str(e, "name")!).ToDictionary(g => g.Key, g => Str(g.First(), "expr") ?? "");
        var ea = Of(a); var eb = Of(b);
        var items = new List<string>();
        foreach (var (n, v) in eb) if (ea.TryGetValue(n, out var old) && old != v) items.Add($"{n} = {old} → {v}");
        foreach (var n in eb.Keys.Except(ea.Keys)) items.Add($"{n} = {eb[n]} added");
        foreach (var n in ea.Keys.Except(eb.Keys)) items.Add($"{n} removed");
        var ta = a.TryGetProperty("tables", out var x) ? x.GetRawText() : ""; var tb = b.TryGetProperty("tables", out var y) ? y.GetRawText() : "";
        if (ta != tb) items.Add("design tables changed");
        return items;
    }

    // ------------------------------------------------------------------------------------------

    static JsonElement Data(byte[] envelope) => JsonDocument.Parse(envelope).RootElement.GetProperty("data").Clone();

    static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble().ToString("0.####", CultureInfo.InvariantCulture) : "";

    static string Value(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => e.GetDouble().ToString("0.####", CultureInfo.InvariantCulture),
        JsonValueKind.True => "on",
        JsonValueKind.False => "off",
        JsonValueKind.Null => "none",
        JsonValueKind.Array when e.EnumerateArray().All(Leaf) => "(" + string.Join(", ", e.EnumerateArray().Select(Value)) + ")",
        _ => "…",
    };

    /// <summary>A property's name as words: "endCondition" → "end condition".</summary>
    static string Words(string camel)
    {
        var sb = new StringBuilder();
        foreach (var c in camel)
        {
            if (char.IsUpper(c) && sb.Length > 0) sb.Append(' ');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    static string Join(List<string> items) => items.Count <= MaxItems ? string.Join("; ", items) : string.Join("; ", items.Take(MaxItems)) + $"; and {items.Count - MaxItems} more";
}
