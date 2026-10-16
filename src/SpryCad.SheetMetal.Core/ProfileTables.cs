namespace SpryCad.SheetMetal;

/// <summary>
/// The tables a shop profile has (sheet metal spec section 3), their key columns and columns: what CSV import expects
/// and what <see cref="SheetRules"/> reads. Every table is keyed by material and gauge first ("*" for any), so the
/// rules of a sheet depend on its material and gauge and on nothing else.
/// </summary>
public static class ProfileTables
{
    public const string Materials = "sm.materials";
    public const string Gauges = "sm.gauges";
    public const string Tooling = "sm.tooling";
    public const string Allowance = "sm.allowance";
    public const string BendTable = "sm.bendtable";
    public const string Corners = "sm.corners";
    public const string Relief = "sm.relief";
    public const string Hems = "sm.hems";
    public const string Distances = "sm.distances";
    public const string Output = "sm.output";
    public const string Conventions = "sm.conventions";

    public sealed record Template(string Key, string Title, string[] KeyColumns, ProfileColumn[] Columns);

    static ProfileColumn L(string n) => new(n, ProfileValueKind.Length);
    static ProfileColumn A(string n) => new(n, ProfileValueKind.Angle);
    static ProfileColumn N(string n) => new(n, ProfileValueKind.Number);
    static ProfileColumn B(string n) => new(n, ProfileValueKind.Bool);
    static ProfileColumn C(string n) => new(n, ProfileValueKind.Choice);
    static ProfileColumn T(string n) => new(n, ProfileValueKind.Text);

    public static readonly IReadOnlyList<Template> All = new Template[]
    {
        new(Materials, "Materials", new[] { "material" },
            new[] { N("density"), B("grain sensitive"), T("notes") }),
        new(Gauges, "Gauges", new[] { "material", "gauge" },
            new[] { L("thickness"), L("thickness tol plus"), L("thickness tol minus") }),
        new(Tooling, "Tooling", new[] { "material", "gauge", "toolset" },
            new[] { B("default"), L("punch radius"), A("punch angle"), L("die width"), A("die angle"), L("die shoulder radius"),
                    L("inside radius"), L("min flange"), N("max tonnage per m") }),
        new(Allowance, "Bend allowance", new[] { "material", "gauge", "toolset" },
            new[] { C("method"), N("k") }),
        new(BendTable, "Bend table", new[] { "material", "gauge", "toolset", "angle" },
            new[] { L("value") }),
        new(Corners, "Corner rules", new[] { "material", "gauge" },
            new[] { C("treatment"), L("gap"), T("allowed"), B("zero gap allowed") }),
        new(Relief, "Relief rules", new[] { "material", "gauge" },
            new[] { C("bend relief"), L("bend relief width"), L("bend relief depth"),
                    C("corner relief"), L("corner relief size"), L("min sliver") }),
        new(Hems, "Hem rules", new[] { "material", "gauge", "hem" },
            new[] { B("allowed"), L("min length"), L("max length"), L("gap"), L("allowance"), L("model gap") }),
        new(Distances, "Distance rules", new[] { "material", "gauge" },
            new[] { L("hole to bend"), L("edge to bend"), L("cut to bend"), L("hole to edge") }),
        new(Output, "Flat output", Array.Empty<string>(),
            new[] { T("outline layer"), T("bend up layer"), T("bend down layer"), T("etch layer"), T("bend note") }),
        new(Conventions, "Conventions", Array.Empty<string>(),
            new[] { C("flange reference"), L("tolerance") }),
    };

    public static Template? Of(string key) => All.FirstOrDefault(t => t.Key == key);

    public static string Title(string key) => Of(key)?.Title.ToLowerInvariant() ?? key;

    /// <summary>An empty table of a kind, with its columns.</summary>
    public static ShopProfileTable Empty(string key, string? units = null)
    {
        var t = Of(key) ?? throw new ArgumentException($"\"{key}\" is not a shop profile table.");
        return new ShopProfileTable { Key = t.Key, Title = t.Title, Units = units, KeyColumns = t.KeyColumns.ToList(), Columns = t.Columns.ToList() };
    }
}
