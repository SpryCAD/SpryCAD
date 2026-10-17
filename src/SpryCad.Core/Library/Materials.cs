using SpryCad.Core.Model;

namespace SpryCad.Core.Library;

/// <summary>What a material is, as a material row's "type" text names it (an unknown name reads as <see cref="Other"/>).</summary>
public enum MaterialType
{
    Steel, StainlessSteel, CastIron, Aluminum, CopperAlloy, Titanium, Nickel, Magnesium, Zinc,
    Plastic, Elastomer, Composite, Wood, Ceramic, Glass, Other,
}

/// <summary>
/// Material and appearance tables (<see cref="TableKinds.Material"/>, <see cref="TableKinds.Appearance"/>): their
/// columns, the units each is stored and shown in, and a typed view of a row (<see cref="MaterialInfo"/>), as
/// <see cref="Fasteners"/> is for screw rows. Values are SI (kg/m³, Pa, 1/K, W/(m·K), J/(kg·K)); a value not known is
/// NaN. Every material table has the same columns, so tables of the same key merge row by row across libraries.
/// </summary>
public static class Materials
{
    /// <summary>The text naming a row's <see cref="MaterialType"/>.</summary>
    public const string TypeKey = "type";
    public const string CategoryKey = "category";
    /// <summary>The specification the row is to ("ASTM B209").</summary>
    public const string SpecKey = "spec";
    public const string DescriptionKey = "description";
    /// <summary>The scale <c>hardness</c> is on ("HB", "HRC", "Shore A").</summary>
    public const string HardnessScaleKey = "hardnessScale";

    /// <summary>The appearance columns (each 0-1), in a material row and an appearance row alike.</summary>
    public static readonly IReadOnlyList<string> AppearanceColumns = new[] { "r", "g", "b", "metallic", "roughness", "opacity" };

    /// <summary>
    /// Material columns: "density" (kg/m³, the only one required); Young's "modulus" and "shear" modulus (Pa);
    /// "poisson"; "yield" and "tensile" strength (Pa); "elongation" at break (a fraction); "hardness" (on the row's
    /// <see cref="HardnessScaleKey"/>); thermal "expansion" (1/K), "conductivity" (W/(m·K)), "specificHeat"
    /// (J/(kg·K)); then the <see cref="AppearanceColumns"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> Columns = new[]
    {
        "density", "modulus", "poisson", "shear", "yield", "tensile", "elongation", "hardness",
        "expansion", "conductivity", "specificHeat",
    }.Concat(AppearanceColumns).ToArray();

    /// <summary>
    /// How a column is shown in <paramref name="units"/>: the stored (SI) amount one shown unit is, and the unit's
    /// label ("" for a plain number). A shown value is the stored one divided by <c>scale</c>, as for
    /// <see cref="LibraryTable.Scale"/>.
    /// </summary>
    public static (double scale, string label) Unit(string column, UnitSystem units)
    {
        var u = units == UnitSystem.IPS ? TableUnits.Inch : TableUnits.Metric;
        var q = Schemas.UnitOf(TableKinds.Material, column);
        return (q.ToBase(1, u), q.Label(u));
    }

    /// <summary>The type a "type" text names (<see cref="MaterialType.Other"/> when none, or one from a newer SpryCAD).</summary>
    public static MaterialType TypeOf(string? text) => Enum.TryParse<MaterialType>(text, out var t) ? t : MaterialType.Other;

    /// <summary>How a type is listed ("Stainless steel", "Copper alloys").</summary>
    public static string TypeName(MaterialType t) => t switch
    {
        MaterialType.StainlessSteel => "Stainless steel",
        MaterialType.CastIron => "Cast iron",
        MaterialType.CopperAlloy => "Copper alloys",
        MaterialType.Nickel => "Nickel alloys",
        MaterialType.Plastic => "Plastics",
        MaterialType.Elastomer => "Elastomers",
        MaterialType.Composite => "Composites",
        MaterialType.Ceramic => "Ceramics",
        _ => t.ToString(),
    };

    /// <summary>A material row read (null if the table is not a material table).</summary>
    public static MaterialInfo? Of(LibraryTable t, LibraryRow r) =>
        t.Kind == TableKinds.Material ? new MaterialInfo(r.Key, t.Units, t.Columns, r.Values, r.Text) : null;

    /// <summary>A part's snapshot of a material row read (null if it is not one: no density column).</summary>
    public static MaterialInfo? Of(LibrarySnapshot s) =>
        s.Kind == TableKinds.Material ? new MaterialInfo(s.Row, s.Units, s.Columns, s.Values, s.Text) : null;

    /// <summary>An appearance row (or a material row's appearance columns) as an <see cref="Appearance"/>; null when it has no color.</summary>
    public static Appearance? AppearanceOf(string name, IReadOnlyList<string> columns, IReadOnlyList<double> values)
    {
        double V(string c, double fallback) { var i = columns.IndexOf(c); return i >= 0 && i < values.Count && double.IsFinite(values[i]) ? values[i] : fallback; }
        var r = V("r", double.NaN); var g = V("g", double.NaN); var b = V("b", double.NaN);
        if (!double.IsFinite(r) || !double.IsFinite(g) || !double.IsFinite(b)) return null;
        return new Appearance(new[] { r, g, b }, V("metallic", 0), V("roughness", 0.5), V("opacity", 1), name);
    }

    static int IndexOf(this IReadOnlyList<string> list, string s) { for (int i = 0; i < list.Count; i++) if (list[i] == s) return i; return -1; }
}

/// <summary>
/// A material row (or a part's snapshot of one) by property: SI values (the row's, in its table's units, converted),
/// null where the row does not give one.
/// </summary>
public sealed class MaterialInfo(string name, TableUnits units, IReadOnlyList<string> columns, IReadOnlyList<double> values, IReadOnlyDictionary<string, string>? text)
{
    public string Name { get; } = name;

    /// <summary>The column's value in SI (null: no such column, or not known).</summary>
    public double? Value(string column)
    {
        for (int i = 0; i < columns.Count && i < values.Count; i++)
            if (columns[i] == column) return double.IsFinite(values[i]) ? Schemas.UnitOf(TableKinds.Material, column).ToBase(values[i], units) : null;
        return null;
    }

    public string? Text(string key) => text != null && text.TryGetValue(key, out var s) ? s : null;

    public MaterialType Type => Materials.TypeOf(Text(Materials.TypeKey));
    /// <summary>kg/m³ (steel's when the row gives none, which a valid table never does).</summary>
    public double Density => Value("density") ?? CadDocument.DefaultDensity;
    public double? Modulus => Value("modulus");
    public double? Poisson => Value("poisson");
    /// <summary>The shear modulus given, else E / 2(1 + ν) when both are known.</summary>
    public double? Shear => Value("shear") ?? (Modulus is { } e && Poisson is { } v ? e / (2 * (1 + v)) : null);
    public double? Yield => Value("yield");
    public double? Tensile => Value("tensile");
    public double? Elongation => Value("elongation");
    public double? Hardness => Value("hardness");
    public double? Expansion => Value("expansion");
    public double? Conductivity => Value("conductivity");
    public double? SpecificHeat => Value("specificHeat");
    /// <summary>The material's look (null: the row gives no color).</summary>
    public Appearance? Appearance => Materials.AppearanceOf(Name, columns, values);
}
