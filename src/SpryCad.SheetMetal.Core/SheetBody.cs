namespace SpryCad.SheetMetal;

/// <summary>
/// A sheet metal body as stored in a part (sheet metal spec section 4): what it is made of and how it lies flat. Its
/// features (base flange, edge flanges…) name it by <see cref="Id"/>. Its rules come from the shop profile by
/// <see cref="Material"/> and <see cref="Gauge"/> alone, plus <see cref="Overrides"/>.
/// </summary>
public sealed class SheetMetalBody
{
    /// <summary>The body's id ("Sheet1"): the id of the solid it makes, too.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Material { get; set; } = "";
    /// <summary>The gauge's name in the profile's gauge table ("16 ga"); it sets the thickness.</summary>
    public string Gauge { get; set; } = "";
    /// <summary>The panel that stays in place and orients the flat pattern (null: the base flange's first).</summary>
    public string? FixedPanel { get; set; }
    /// <summary>The grain's direction in the flat, degrees from the flat's x axis (null: none set).</summary>
    public double? GrainAngle { get; set; }
    /// <summary>Shown flat (the flat pattern as the body) instead of folded.</summary>
    public bool Flattened { get; set; }
    /// <summary>Profile values overridden for the whole body, by "table/column" ("sm.tooling/inside radius"): expressions.</summary>
    public SortedDictionary<string, string>? Overrides { get; set; }

    public SheetMetalBody Clone() => new()
    {
        Id = Id, Name = Name, Material = Material, Gauge = Gauge, FixedPanel = FixedPanel, GrainAngle = GrainAngle, Flattened = Flattened,
        Overrides = Overrides == null ? null : new SortedDictionary<string, string>(Overrides, StringComparer.Ordinal),
    };
}

/// <summary>
/// Profile values the user overrode on one bend, corner or hem, by its persistent id (sheet metal spec section 4):
/// only edited entities are stored; the rest follow the profile.
/// </summary>
public sealed class SheetMetalOverride
{
    /// <summary>The bend's, corner's or hem's persistent id ("EdgeFlange2.0.B").</summary>
    public string Target { get; set; } = "";
    /// <summary>Values by "table/column" ("sm.tooling/inside radius", "sm.tooling/toolset"): expressions.</summary>
    public SortedDictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
    public string? Note { get; set; }
}

/// <summary>
/// A part's copy of the shop profile rows its sheets can use (section 34's snapshots, for sheet metal): every row of
/// the profile whose keys match the part's materials and gauges ("*" included), so the part rebuilds exactly the same
/// with no library installed, and opening it can tell whether the profile in use has changed for it.
/// </summary>
public sealed class ProfileSnapshot
{
    public string ProfileName { get; set; } = "";
    public List<ShopProfileTable> Tables { get; set; } = new();

    public ShopProfile Profile() => new(ProfileName, Tables);

    /// <summary>The rows of <paramref name="profile"/> that sheets of these materials and gauges can use.</summary>
    public static ProfileSnapshot Of(ShopProfile profile, IEnumerable<(string material, string gauge)> sheets)
    {
        var list = sheets.Distinct().ToList();
        bool Fits(ShopProfileTable t, ProfileRow r)
        {
            // tables are keyed by material first and gauge second (any further keys: tool set, hem type, angle: all kept)
            if (t.KeyColumns.Count == 0) return true;
            return list.Any(s => (r.Keys[0] == ProfileRow.Any || r.Keys[0] == s.material)
                && (t.KeyColumns.Count < 2 || r.Keys[1] == ProfileRow.Any || r.Keys[1] == s.gauge));
        }
        return new ProfileSnapshot
        {
            ProfileName = profile.Name,
            Tables = profile.Tables.Values.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t =>
            {
                var c = t.Clone();
                c.Rows = c.Rows.Where(r => Fits(t, r)).ToList();
                return c;
            }).ToList(),
        };
    }

    /// <summary>The differences from <paramref name="newer"/> in words, one per changed row (none: the same).</summary>
    public List<string> Differences(ProfileSnapshot newer)
    {
        var d = new List<string>();
        if (newer.ProfileName != ProfileName) d.Add($"Shop profile: {ProfileName} → {newer.ProfileName}");
        var keys = Tables.Select(t => t.Key).Union(newer.Tables.Select(t => t.Key)).OrderBy(k => k, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var a = Tables.FirstOrDefault(t => t.Key == key);
            var b = newer.Tables.FirstOrDefault(t => t.Key == key);
            var title = ProfileTables.Of(key)?.Title ?? key;
            if (a == null || b == null) { d.Add(a == null ? $"{title}: added" : $"{title}: removed"); continue; }
            if (a.Interpolate != b.Interpolate) d.Add($"{title}: interpolation {(b.Interpolate ? "on" : "off")}");
            var ra = a.Rows.ToDictionary(r => r.KeyText);
            var rb = b.Rows.ToDictionary(r => r.KeyText);
            foreach (var k in ra.Keys.Union(rb.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                if (!rb.TryGetValue(k, out var nb)) { d.Add($"{title} {k}: removed"); continue; }
                if (!ra.TryGetValue(k, out var na)) { d.Add($"{title} {k}: added"); continue; }
                var cols = b.Columns.Select(c => c.Name).ToList();
                var changes = cols.Select((c, i) => (c, old: a.Column(c) is var j && j >= 0 ? na.Cells[j] : "", now: nb.Cells[i]))
                    .Where(x => x.old != x.now).Select(x => $"{x.c} {Shown(x.old)} → {Shown(x.now)}").ToList();
                if (changes.Count > 0) d.Add($"{title} {k}: {string.Join(", ", changes)}");
            }
        }
        return d;
    }

    static string Shown(string s) => s.Length == 0 ? "(none)" : s;
}
