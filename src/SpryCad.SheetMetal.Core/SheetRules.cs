using System.Globalization;

namespace SpryCad.SheetMetal;

/// <summary>
/// The rules of one sheet, resolved from the shop profile by its material and gauge alone (sheet metal spec section 3):
/// every part of the same material and gauge gets the same thickness, tooling, allowance and rules, unless the user
/// overrides a value (<see cref="Overrides"/>, keyed "table/column": "sm.tooling/inside radius"). Lookups that find
/// nothing throw <see cref="ProfileMissException"/>; nothing falls back to a built-in value.
/// </summary>
public sealed class SheetRules
{
    public ShopProfile Profile { get; }
    public string Material { get; }
    public string Gauge { get; }
    public double Thickness { get; }
    public double ThicknessTolPlus { get; }
    public double ThicknessTolMinus { get; }
    /// <summary>The tool set bends use: the profile's default for the material and gauge, unless one was asked for.</summary>
    public string Toolset { get; }
    public double InsideRadius { get; }
    public double MinFlange { get; }
    public AllowanceMethod Method { get; }
    /// <summary>The K-factor (<see cref="AllowanceMethod.KFactor"/> only; else 0).</summary>
    public double K { get; }
    /// <summary>The part's geometric tolerance, used for every comparison (sheet metal spec section 2).</summary>
    public double Tolerance { get; }
    public FlangeReference DefaultFlangeReference { get; }
    public IReadOnlyDictionary<string, string> Overrides { get; }
    /// <summary>Whether the material's grain matters to its bends (sheet metal spec SM-GRAIN-01).</summary>
    public bool GrainSensitive { get; }
    /// <summary>The least angle (degrees) a bend line should cross the grain at (0: any).</summary>
    public double MinGrainAngle { get; }

    readonly SortedSet<string> _items = new(StringComparer.Ordinal);

    /// <summary>The profile rows these rules were read from ("sm.tooling/Steel|16 ga|T1"): what a part snapshots.</summary>
    public IReadOnlyCollection<string> Items => _items;

    SheetRules(ShopProfile profile, string material, string gauge, string? toolset, IReadOnlyDictionary<string, string>? overrides)
    {
        Profile = profile;
        Material = material;
        Gauge = gauge;
        Overrides = overrides ?? new Dictionary<string, string>();

        var mat = Use(Profile.Find(ProfileTables.Materials, material));
        GrainSensitive = Cell(mat, "grain sensitive") == "1";
        MinGrainAngle = GrainSensitive ? Num(mat, "min grain angle", 0) : 0;
        var g = Use(Profile.Find(ProfileTables.Gauges, material, gauge));
        // the thickness is the sheet's: never overridden (another thickness is another sheet body)
        Thickness = RuleExpr.Eval(g.Cell("thickness"));
        if (Thickness <= 0) throw new ProfileMissException(ProfileTables.Gauges, $"{material} {gauge}: the thickness must be more than 0.");
        ThicknessTolPlus = Num(g, "thickness tol plus", 0);
        ThicknessTolMinus = Num(g, "thickness tol minus", 0);

        var tool = Use(FindTooling(toolset));
        Toolset = tool.Row.Keys[2] == ProfileRow.Any ? toolset ?? "*" : tool.Row.Keys[2];
        InsideRadius = Num(tool, "inside radius", 0);
        if (InsideRadius < 0) throw new ProfileMissException(ProfileTables.Tooling, $"{material} {gauge} {Toolset}: the inside radius cannot be negative.");
        MinFlange = Num(tool, "min flange", InsideRadius);

        var a = Use(Profile.Find(ProfileTables.Allowance, material, gauge, Toolset));
        Method = ParseMethod(Cell(a, "method"));
        if (Method == AllowanceMethod.KFactor)
        {
            K = Num(a, "k", InsideRadius);
            if (K < 0 || K > 1) throw new ProfileMissException(ProfileTables.Allowance, $"{material} {gauge} {Toolset}: the K-factor must be from 0 to 1, not {K}.");
        }

        var conv = Use(Profile.Find(ProfileTables.Conventions));
        Tolerance = Num(conv, "tolerance", InsideRadius);
        if (Tolerance <= 0) throw new ProfileMissException(ProfileTables.Conventions, "The tolerance must be more than 0.");
        DefaultFlangeReference = ParseReference(Cell(conv, "flange reference"));
    }

    /// <summary>The rules for <paramref name="material"/> and <paramref name="gauge"/> from <paramref name="profile"/>.</summary>
    public static SheetRules Resolve(ShopProfile profile, string material, string gauge, IReadOnlyDictionary<string, string>? overrides = null) =>
        new(profile, material, gauge, null, overrides);

    /// <summary>The same sheet bent with another tool set (an override on a bend).</summary>
    public SheetRules WithToolset(string toolset) => toolset == Toolset ? this : new(Profile, Material, Gauge, toolset, Overrides);

    /// <summary>The same sheet with more overrides (a bend's on top of the part's).</summary>
    public SheetRules WithOverrides(IReadOnlyDictionary<string, string> more)
    {
        if (more.Count == 0) return this;
        var all = new Dictionary<string, string>(Overrides, StringComparer.Ordinal);
        foreach (var (k, v) in more) all[k] = v;
        string? tool = all.TryGetValue(ToolsetOverride, out var ts) ? ts : Toolset;
        return new SheetRules(Profile, Material, Gauge, tool, all);
    }

    /// <summary>The override key that picks a bend's tool set.</summary>
    public const string ToolsetOverride = "sm.tooling/toolset";

    ProfileHit Use(ProfileHit h) { _items.Add(h.Item); return h; }

    ProfileHit FindTooling(string? toolset)
    {
        var t = Profile.Table(ProfileTables.Tooling);
        var rows = Profile.Matches(t, new[] { Material, Gauge }).ToList();
        int Spec(ProfileRow r) => (r.Keys[0] != ProfileRow.Any ? 1 : 0) + (r.Keys[1] != ProfileRow.Any ? 1 : 0);
        string What = $"{Material} {Gauge}";
        if (rows.Count == 0) throw new ProfileMissException(ProfileTables.Tooling, $"The shop profile \"{Profile.Name}\" has no tooling for {What}.");
        if (toolset != null)
        {
            var named = rows.Where(r => r.Keys[2] == toolset).OrderByDescending(Spec).FirstOrDefault()
                ?? throw new ProfileMissException(ProfileTables.Tooling, $"The shop profile \"{Profile.Name}\" has no tool set \"{toolset}\" for {What}.");
            return new ProfileHit(t, named);
        }
        var best = rows.Max(Spec);
        var defaults = rows.Where(r => Spec(r) == best && new ProfileHit(t, r).Cell("default") == "1").ToList();
        if (defaults.Count != 1)
            throw new ProfileMissException(ProfileTables.Tooling, defaults.Count == 0
                ? $"The shop profile \"{Profile.Name}\" has no default tool set for {What}."
                : $"The shop profile \"{Profile.Name}\" has {defaults.Count} default tool sets for {What}: {string.Join(", ", defaults.Select(r => r.Keys[2]))}.");
        return new ProfileHit(t, defaults[0]);
    }

    string Cell(ProfileHit h, string column) => Overrides.TryGetValue(h.Table.Key + "/" + column, out var o) ? o : h.Cell(column);

    double Num(ProfileHit h, string column, double r)
    {
        var text = Cell(h, column);
        if (text.Length == 0) throw new ProfileMissException(h.Table.Key, $"{h.Item}: \"{column}\" is empty.");
        try { return RuleExpr.Eval(text, Vars(r)); }
        catch (RuleExprException ex) { throw new ProfileMissException(h.Table.Key, $"{h.Item}: {column}: {ex.Message}"); }
    }

    Dictionary<string, double> Vars(double r) => new(StringComparer.Ordinal) { ["t"] = Thickness, ["R"] = r };

    /// <summary>
    /// A rule's value for this sheet (relief width, corner gap, minimum distances…): the profile's row for its
    /// material and gauge (and <paramref name="extraKey"/> where the table has a third key, as hems by type),
    /// evaluated with t and R (the inside radius, or the bend's).
    /// </summary>
    public double Rule(string table, string column, double? radius = null, string? extraKey = null)
    {
        var h = Use(Find(table, extraKey));
        return Num(h, column, radius ?? InsideRadius);
    }

    /// <summary>A rule's text (a relief type, a layer name).</summary>
    public string RuleText(string table, string column, string? extraKey = null) => Cell(Use(Find(table, extraKey)), column);

    ProfileHit Find(string table, string? extraKey)
    {
        var keys = Profile.Table(table).KeyColumns.Count;
        return keys switch
        {
            0 => Profile.Find(table),
            2 => Profile.Find(table, Material, Gauge),
            3 => Profile.Find(table, Material, Gauge, extraKey ?? throw new ArgumentException($"{table} needs its third key.")),
            _ => throw new ArgumentException($"{table} is not a rule table."),
        };
    }

    /// <summary>
    /// The allowance of a bend of <paramref name="theta"/> (the bend angle, radians) at this sheet's inside radius:
    /// by the profile's method for the material, gauge and tool set, and where it came from.
    /// </summary>
    public (double Allowance, AllowanceSource Source) Allowance(double theta)
    {
        double r = InsideRadius, t = Thickness;
        bool overridden = Overrides.Keys.Any(k => k.StartsWith(ProfileTables.Allowance + "/", StringComparison.Ordinal) || k.StartsWith(ProfileTables.Tooling + "/", StringComparison.Ordinal));
        var deg = theta * 180 / Math.PI;
        switch (Method)
        {
            case AllowanceMethod.KFactor:
                return (BendMath.KFactorAllowance(theta, r, t, K), new AllowanceSource(Method, $"K-factor {K.ToString("0.####", CultureInfo.InvariantCulture)} ({Material} {Gauge} {Toolset})", overridden));
            case AllowanceMethod.Din6935:
                return (BendMath.Din6935Allowance(theta, r, t), new AllowanceSource(Method, $"DIN 6935 ({Material} {Gauge} {Toolset})", overridden));
            default:
                var (v, detail) = TableValue(deg);
                var ba = Method == AllowanceMethod.BendTable ? v : BendMath.AllowanceFromDeduction(theta, r, t, v);
                return (ba, new AllowanceSource(Method, $"{(Method == AllowanceMethod.BendTable ? "Bend table" : "Bend deduction")} {detail} ({Material} {Gauge} {Toolset})", overridden));
        }
    }

    /// <summary>The bend table's value at <paramref name="deg"/>: a row of that angle, or between two rows when the table interpolates.</summary>
    (double value, string detail) TableValue(double deg)
    {
        var t = Profile.Table(ProfileTables.BendTable);
        var rows = Profile.Matches(t, new[] { Material, Gauge, Toolset }).ToList();
        if (rows.Count == 0) throw new ProfileMissException(ProfileTables.BendTable, $"The shop profile \"{Profile.Name}\" has no bend table for {Material} {Gauge} {Toolset}.");
        // the most specific rows only (a material's own table, not a mix with the "*" one)
        int Spec(ProfileRow r) => r.Keys.Take(3).Count(k => k != ProfileRow.Any);
        var best = rows.Max(Spec);
        var pts = rows.Where(r => Spec(r) == best).Select(r =>
        {
            if (!double.TryParse(r.Keys[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var a))
                throw new ProfileMissException(ProfileTables.BendTable, $"{t.Key}/{r.KeyText}: the angle \"{r.Keys[3]}\" is not a number.");
            return (angle: a, row: r);
        }).OrderBy(p => p.angle).ToList();
        double Value(ProfileRow r) { var h = Use(new ProfileHit(t, r)); return Num(h, "value", InsideRadius); }
        const double eps = 1e-9;
        var exact = pts.FirstOrDefault(p => Math.Abs(p.angle - deg) < eps);
        if (exact.row != null) return (Value(exact.row), $"{Deg(deg)}");
        var lo = pts.LastOrDefault(p => p.angle < deg);
        var hi = pts.FirstOrDefault(p => p.angle > deg);
        string range = $"{Deg(pts[0].angle)} to {Deg(pts[^1].angle)}";
        if (lo.row == null || hi.row == null)
            throw new ProfileMissException(ProfileTables.BendTable, $"The bend table for {Material} {Gauge} {Toolset} has {range}: no value for {Deg(deg)} (tables are never extrapolated).");
        if (!t.Interpolate)
            throw new ProfileMissException(ProfileTables.BendTable, $"The bend table for {Material} {Gauge} {Toolset} has no row for {Deg(deg)}, and does not interpolate.");
        double a = Value(lo.row), b = Value(hi.row);
        var f = (deg - lo.angle) / (hi.angle - lo.angle);
        return (a + (b - a) * f, $"{Deg(deg)} (between {Deg(lo.angle)} and {Deg(hi.angle)})");
    }

    static string Deg(double d) => d.ToString("0.###", CultureInfo.InvariantCulture) + "°";

    public static AllowanceMethod ParseMethod(string s) => s.Replace("-", "").Replace(" ", "").ToLowerInvariant() switch
    {
        "kfactor" or "k" => AllowanceMethod.KFactor,
        "bendtable" or "ba" => AllowanceMethod.BendTable,
        "benddeduction" or "bd" => AllowanceMethod.BendDeduction,
        "din6935" or "din" => AllowanceMethod.Din6935,
        _ => throw new ProfileMissException(ProfileTables.Allowance, $"\"{s}\" is not an allowance method (K-factor, bend table, bend deduction or DIN 6935)."),
    };

    public static FlangeReference ParseReference(string s) => s.Replace("-", "").Replace(" ", "").ToLowerInvariant() switch
    {
        "oml" or "outsidemoldline" or "outside" => FlangeReference.OutsideMoldLine,
        "iml" or "insidemoldline" or "inside" => FlangeReference.InsideMoldLine,
        "tangent" => FlangeReference.Tangent,
        _ => throw new ProfileMissException(ProfileTables.Conventions, $"\"{s}\" is not a flange reference (OML, IML or tangent)."),
    };
}
