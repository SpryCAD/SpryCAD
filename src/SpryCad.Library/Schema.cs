namespace SpryCad.Core.Library;

/// <summary>The units a table's values are in, as its standard publishes them.</summary>
public enum TableUnits { Metric, Inch }

/// <summary>
/// What a column's values are, and the units they are in: as the table holds them (in its <see cref="TableUnits"/>)
/// and as code uses them (<see cref="ToBase"/>: millimeters for lengths, degrees for angles, SI for material
/// properties, plain numbers otherwise).
/// </summary>
public sealed class Measure
{
    public string Name { get; }
    readonly (double scale, string label) _metric, _inch;
    readonly Func<double, double>? _toBase, _fromBase;

    Measure(string name, (double, string) metric, (double, string) inch, Func<double, double>? toBase = null, Func<double, double>? fromBase = null)
    { Name = name; _metric = metric; _inch = inch; _toBase = toBase; _fromBase = fromBase; }

    /// <summary>The unit a table in <paramref name="units"/> gives this in ("mm", "in", "°", "ksi"; "" for a plain number).</summary>
    public string Label(TableUnits units) => (units == TableUnits.Inch ? _inch : _metric).label;

    /// <summary>A value as the table holds it, as code uses it.</summary>
    public double ToBase(double value, TableUnits units) =>
        _toBase != null ? _toBase(value) : value * (units == TableUnits.Inch ? _inch : _metric).scale;

    /// <summary>A value as code uses it, as a table in <paramref name="units"/> holds it.</summary>
    public double FromBase(double value, TableUnits units) =>
        _fromBase != null ? _fromBase(value) : value / (units == TableUnits.Inch ? _inch : _metric).scale;

    public bool IsText => this == Text;

    const double Psi = 6894.757293168, LbPerIn3 = 27679.9047102, BtuPerHFtF = 1.730734666, BtuPerLbF = 4186.8;

    /// <summary>A length: mm, or in for an inch table; used in mm.</summary>
    public static readonly Measure Length = new("length", (1, "mm"), (25.4, "in"));
    /// <summary>An angle: degrees in either.</summary>
    public static readonly Measure Angle = new("angle", (1, "°"), (1, "°"));
    /// <summary>Threads per inch: used as the pitch in mm (25.4 / TPI).</summary>
    public static readonly Measure Tpi = new("tpi", (1, "TPI"), (1, "TPI"), v => 25.4 / v, v => 25.4 / v);
    /// <summary>A plain number (a count, a ratio, a 0-1 appearance value, a hardness on the row's scale).</summary>
    public static readonly Measure Number = new("number", (1, ""), (1, ""));
    /// <summary>Text (a part number, a type).</summary>
    public static readonly Measure Text = new("text", (1, ""), (1, ""));
    /// <summary>A percentage: used as a fraction.</summary>
    public static readonly Measure Percent = new("percent", (0.01, "%"), (0.01, "%"));
    /// <summary>Density: g/cm³ or lb/in³; used in kg/m³.</summary>
    public static readonly Measure Density = new("density", (1000, "g/cm³"), (LbPerIn3, "lb/in³"));
    /// <summary>An elastic modulus: GPa or Msi; used in Pa.</summary>
    public static readonly Measure Modulus = new("modulus", (1e9, "GPa"), (1e6 * Psi, "Msi"));
    /// <summary>A strength: MPa or ksi; used in Pa.</summary>
    public static readonly Measure Strength = new("strength", (1e6, "MPa"), (1e3 * Psi, "ksi"));
    /// <summary>Thermal expansion: µm/(m·K) or µin/(in·°F); used in 1/K.</summary>
    public static readonly Measure Expansion = new("expansion", (1e-6, "µm/(m·K)"), (1.8e-6, "µin/(in·°F)"));
    /// <summary>Thermal conductivity: W/(m·K) or BTU/(h·ft·°F); used in W/(m·K).</summary>
    public static readonly Measure Conductivity = new("conductivity", (1, "W/(m·K)"), (BtuPerHFtF, "BTU/(h·ft·°F)"));
    /// <summary>Specific heat: J/(kg·K) or BTU/(lb·°F); used in J/(kg·K).</summary>
    public static readonly Measure SpecificHeat = new("specificHeat", (1, "J/(kg·K)"), (BtuPerLbF, "BTU/(lb·°F)"));

    public override string ToString() => Name;
}

/// <summary>
/// A column of a table kind: its stable <see cref="Path"/> (what code and parts name it by, "external.major.max"),
/// the headings it is shown under (the groups first: "External", "Major Dia", "Max"), what its values are, and
/// whether every row must give one.
/// </summary>
public sealed record ColumnDef(string Path, string[] Heading, Measure Unit, bool Required = false)
{
    public ColumnDef(string path, string heading, Measure unit, bool required = false) : this(path, new[] { heading }, unit, required) { }
    public bool IsText => Unit.IsText;
}

/// <summary>
/// A table kind's schema (docs/library-v2.md): the columns that key its rows (designation; designation and class;
/// fastener type and size), its value and text columns in the order shown, and whether it takes columns of its own
/// beyond them (<see cref="Open"/>: a hole type's parameters, named by its profile).
/// </summary>
public sealed record TableSchema(string Kind, string Title, ColumnDef[] Key, ColumnDef[] Columns, bool Open = false)
{
    /// <summary>The schema's column of a path (null: none, or an open kind's own).</summary>
    public ColumnDef? Column(string path) => Columns.FirstOrDefault(c => c.Path == path);
}
