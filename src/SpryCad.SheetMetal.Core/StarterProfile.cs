namespace SpryCad.SheetMetal;

/// <summary>
/// The shop profile that ships with SpryCAD (sheet metal spec section 3): a few materials and gauges, one tool set and
/// generic rules. Its values are examples, not any shop's: a shop imports its own tables (CSV) into its company
/// library, and that profile replaces this one whole.
/// </summary>
public static class StarterProfile
{
    public static readonly Guid LibraryId = new("5f3c7a12-6b0e-4c51-9d8e-2a4f1b7c9e30");
    public const string Name = "Starter profile (example values)";

    static readonly string[] Any2 = { "*", "*" };

    public static IReadOnlyList<ShopProfileTable> Tables { get; } = Build();

    public static ShopProfile Profile { get; } = new(Name, Tables);

    static ShopProfileTable Table(string key, string? units, params ProfileRow[] rows)
    {
        var t = ProfileTables.Empty(key, units);
        t.Title += " (example values)";
        t.Rows.AddRange(rows);
        return t;
    }

    static ProfileRow R(string[] keys, params string[] cells) => new(keys, cells);

    static IReadOnlyList<ShopProfileTable> Build()
    {
        const string steel = "Steel CRS", stainless = "Stainless 304", aluminum = "Aluminum 5052";
        ProfileRow G(string m, string g, string inches) => R(new[] { m, g }, inches + "in", "0.003in", "0.003in");
        return new[]
        {
            Table(ProfileTables.Materials, null,
                R(new[] { steel }, "7850", "0", "0", "Cold rolled steel"),
                R(new[] { stainless }, "8000", "0", "0", ""),
                R(new[] { aluminum }, "2680", "1", "45", "Bend across the grain where possible")),
            Table(ProfileTables.Gauges, ShopProfileTable.Inch,
                G(steel, "20 ga", "0.0359"), G(steel, "18 ga", "0.0478"), G(steel, "16 ga", "0.0598"),
                G(steel, "14 ga", "0.0747"), G(steel, "12 ga", "0.1046"), G(steel, "10 ga", "0.1345"),
                G(stainless, "20 ga", "0.0375"), G(stainless, "18 ga", "0.05"), G(stainless, "16 ga", "0.0625"),
                G(stainless, "14 ga", "0.078"), G(stainless, "11 ga", "0.125"),
                G(aluminum, "0.040", "0.040"), G(aluminum, "0.063", "0.063"), G(aluminum, "0.080", "0.080"),
                G(aluminum, "0.090", "0.090"), G(aluminum, "0.125", "0.125")),
            // one tool set for every material and gauge: inside radius = thickness, V-die 8 t
            Table(ProfileTables.Tooling, null,
                R(new[] { "*", "*", "Standard" }, "1", "t", "88", "8*t", "88", "max(0.5mm, t)", "t", "4*t", "")),
            Table(ProfileTables.Allowance, null,
                R(new[] { "*", "*", "*" }, "KFactor", "0.44"),
                R(new[] { aluminum, "*", "*" }, "KFactor", "0.42")),
            Table(ProfileTables.BendTable, null),
            Table(ProfileTables.Corners, null,
                R(Any2, "Open", "max(0.1mm, 0.2*t)", "Open Closed OverlapA OverlapB Miter", "0")),
            Table(ProfileTables.Relief, null,
                R(Any2, "Rectangular", "max(t, 0.5mm)", "R + t + 0.5mm", "Round", "2*(R + t)", "t")),
            Table(ProfileTables.Hems, null,
                R(new[] { "*", "*", "Closed" }, "1", "4*t", "20*t", "0", "0.43*t", "0.005in"),
                R(new[] { "*", "*", "Open" }, "1", "4*t", "20*t", "t", "0.5*t", "0"),
                R(new[] { "*", "*", "Teardrop" }, "1", "6*t", "20*t", "t", "0.5*t", "0"),
                R(new[] { "*", "*", "Rolled" }, "1", "6*t", "40*t", "2*t", "0.5*t", "0")),
            Table(ProfileTables.Distances, null,
                R(Any2, "2*t + R", "R + t", "2*t + R", "2*t")),
            Table(ProfileTables.Output, null,
                R(Array.Empty<string>(), "OUTLINE", "BEND_UP", "BEND_DOWN", "ETCH", "{direction} {angle} R{radius}")),
            Table(ProfileTables.Conventions, null,
                R(Array.Empty<string>(), "OML", "0.001mm")),
        };
    }
}
