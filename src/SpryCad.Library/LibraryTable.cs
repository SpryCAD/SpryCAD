using System.Text.Json.Serialization;

namespace SpryCad.Core.Library;

/// <summary>
/// One table of standards data (docs/library-v2.md): thread sizes, clearance holes, fastener families, materials. Its
/// kind's schema (<see cref="Schemas"/>) says what its columns are; its values are in its <see cref="Units"/>, as the
/// standard publishes them (an inch standard in inches), read in millimeters (or SI) through <see cref="Base"/>. Rows
/// are keyed by the schema's key columns and listed in the order a picker shows them.
/// </summary>
public sealed class LibraryTable
{
    /// <summary>The table's stable key ("ISO261-coarse"): what parts' snapshots name it by. Never reused for other data.</summary>
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>What the table is (<see cref="TableKinds"/>): its schema, and the commands that list it.</summary>
    public string Kind { get; set; } = "";
    /// <summary>The standard the values come from ("ISO 261").</summary>
    public string Standard { get; set; } = "";
    /// <summary>Whether every value was checked against the published standard (not a secondary source).</summary>
    public bool Verified { get; set; }
    /// <summary>The units the values are in, as the standard gives them.</summary>
    public TableUnits Units { get; set; }
    /// <summary>The version of its kind's schema the table was written to.</summary>
    public int SchemaVersion { get; set; } = 1;
    /// <summary>The value columns, by path, in the order shown; their values are the rows' <see cref="LibraryRow.Values"/>.</summary>
    public List<string> Columns { get; set; } = new();
    public List<LibraryRow> Rows { get; set; } = new();
    /// <summary>
    /// A hole type's profile (<see cref="TableKinds.HoleType"/>), as the sketch's JSON: half the hole in section, its
    /// dimensions using the table's columns by name, a row per size. Null for other tables.
    /// </summary>
    public string? Profile { get; set; }

    [JsonIgnore] public TableSchema? Schema => Schemas.Of(Kind);

    public LibraryRow? Row(string key) => Rows.FirstOrDefault(r => r.Key == key);

    public int Column(string path) => Columns.IndexOf(path);

    /// <summary>What a column's values are (an open kind's own columns: lengths, or angles when named so).</summary>
    public Measure Unit(string column) => Schemas.UnitOf(Kind, column);

    /// <summary>The unit a column is given in here ("mm", "in", "°", "ksi"; "" for a plain number).</summary>
    public string Label(string column) => Unit(column).Label(Units);

    /// <summary>A row's value of a column as code uses it (mm, degrees, SI); null when the table has no such column or the row no value.</summary>
    public double? Base(LibraryRow row, string column)
    {
        var i = Column(column);
        if (i < 0 || i >= row.Values.Count || double.IsNaN(row.Values[i])) return null;
        return Unit(column).ToBase(row.Values[i], Units);
    }
}

/// <summary>
/// A row: its key (the schema's key columns' values: a designation, or designation and class), a value per column of
/// its table (NaN: not known, where the column is not required), and text (part numbers, a material's type).
/// </summary>
public sealed class LibraryRow
{
    /// <summary>The key columns' values, in the schema's order.</summary>
    public List<string> Keys { get; set; } = new();
    /// <summary>One per column of the table, in the table's units.</summary>
    public List<double> Values { get; set; } = new();
    /// <summary>Text the row carries, by column, sorted; null when none.</summary>
    public SortedDictionary<string, string>? Text { get; set; }

    /// <summary>
    /// The row's identity within its table, as parts' item keys name it: its key values joined by ':' (a ':' or '\'
    /// in one escaped by '\'); a single key as it is. Set, split the same way (the editor's and a CSV file's rows).
    /// </summary>
    [JsonIgnore]
    public string Key
    {
        get => JoinKeys(Keys);
        set => Keys = SplitKeys(value);
    }

    public LibraryRow() { }
    public LibraryRow(string key, params double[] values) { Key = key; Values = values.ToList(); }

    public static string JoinKeys(IReadOnlyList<string> keys) =>
        keys.Count == 1 && keys[0].IndexOfAny(new[] { ':', '\\' }) < 0 ? keys[0]
            : string.Join(":", keys.Select(k => k.Replace("\\", "\\\\").Replace(":", "\\:")));

    /// <summary>A row's key values from its <see cref="Key"/> (the inverse of <see cref="JoinKeys"/>).</summary>
    public static List<string> SplitKeys(string key)
    {
        var keys = new List<string>();
        var cur = new System.Text.StringBuilder();
        for (int i = 0; i < key.Length; i++)
        {
            if (key[i] == '\\' && i + 1 < key.Length) cur.Append(key[++i]);
            else if (key[i] == ':') { keys.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(key[i]);
        }
        keys.Add(cur.ToString());
        return keys;
    }
}

/// <summary>What a table is: the key of its schema (<see cref="Schemas"/>).</summary>
public static class TableKinds
{
    /// <summary>
    /// Straight thread sizes (as Inventor's thread sheets): a row per designation and class (its external class: 6g,
    /// 2A, 3A), its major diameter and pitch, the limits of the class and of the internal class that goes with it (6H,
    /// 2B, 3B), and its tap drill.
    /// </summary>
    public const string Thread = "thread";
    /// <summary>
    /// Taper (pipe) thread sizes (as Inventor's taper sheets: NPT, BSPT, ISO 7): a row per designation, its outside
    /// diameter and pitch, the gauge lengths and their pitch diameters, the thread's height and the taper.
    /// </summary>
    public const string TaperThread = "taper-thread";
    /// <summary>
    /// Holes for fasteners (as Inventor's clearance sheets): a row per fastener type and size, its shaft's clearance
    /// diameters (close, normal, loose), its head's pocket (counterbore or countersink: diameter, depth or angle), and
    /// the fastener families that go in it (screws, nuts, washers: their tables' keys).
    /// </summary>
    public const string FastenerHoles = "fastener-holes";
    /// <summary>Screws, a row per size and length ("M8x30").</summary>
    public const string Screw = "screw";
    /// <summary>Nuts, a row per size.</summary>
    public const string Nut = "nut";
    /// <summary>Washers, a row per size.</summary>
    public const string Washer = "washer";
    /// <summary>Materials, a row per material ("Aluminum 6061"): properties, their type, and their look.</summary>
    public const string Material = "material";
    /// <summary>Appearances, a row per finish ("Brushed aluminum"): color and finish (0-1).</summary>
    public const string Appearance = "appearance";
    /// <summary>
    /// Hole types of the Hole Wizard, as Inventor's spreadsheets: a profile (<see cref="LibraryTable.Profile"/>) whose
    /// dimensions use the columns by name (lengths; a column named with "angle" an angle), a row per size.
    /// </summary>
    public const string HoleType = "hole-type";
}

/// <summary>
/// The schemas of the table kinds (docs/library-v2.md): what each column is, its headings, its units, whether it is
/// required. Any kind also takes text columns of its own (part numbers, a supplier).
/// </summary>
public static class Schemas
{
    static ColumnDef L(string path, string heading, bool required = false) => new(path, heading, Measure.Length, required);
    static ColumnDef T(string path, string heading) => new(path, heading, Measure.Text);

    static readonly ColumnDef Designation = new("designation", "Designation", Measure.Text, true);

    /// <summary>The fastener families' size text (a screw row "M8x30" is of size "M8").</summary>
    public const string SizeColumn = "size";

    /// <summary>A thread row's class, its second key: the external class (6g, 2A, 3A); the internal one is a text column.</summary>
    public const string ThreadClassColumn = "class";

    /// <summary>A fastener row's part number: the company's own, listed in bills of materials beside its designation.</summary>
    public const string PartNumberColumn = "partNumber";

    static readonly string[] Look = { "r", "g", "b", "metallic", "roughness", "opacity" };

    public static readonly IReadOnlyList<TableSchema> All = new[]
    {
        new TableSchema(TableKinds.Thread, "Thread sizes", new[] { Designation, new ColumnDef(ThreadClassColumn, "Class", Measure.Text, true) },
            new[]
            {
                L("major", "Major Diameter", true), L("pitch", "Pitch", true),
                new ColumnDef("external.major.max", new[] { "External", "Major Dia", "Max" }, Measure.Length),
                new ColumnDef("external.major.min", new[] { "External", "Major Dia", "Min" }, Measure.Length),
                new ColumnDef("external.pitch.max", new[] { "External", "Pitch Dia", "Max" }, Measure.Length),
                new ColumnDef("external.pitch.min", new[] { "External", "Pitch Dia", "Min" }, Measure.Length),
                new ColumnDef("external.minor.max", new[] { "External", "Minor Dia", "Max" }, Measure.Length),
                new ColumnDef("external.minor.min", new[] { "External", "Minor Dia", "Min" }, Measure.Length),
                new ColumnDef("internal.class", new[] { "Internal", "Class" }, Measure.Text),
                new ColumnDef("internal.minor.min", new[] { "Internal", "Minor Dia", "Min" }, Measure.Length),
                new ColumnDef("internal.minor.max", new[] { "Internal", "Minor Dia", "Max" }, Measure.Length),
                new ColumnDef("internal.pitch.min", new[] { "Internal", "Pitch Dia", "Min" }, Measure.Length),
                new ColumnDef("internal.pitch.max", new[] { "Internal", "Pitch Dia", "Max" }, Measure.Length),
                new ColumnDef("internal.major.min", new[] { "Internal", "Major Dia", "Min" }, Measure.Length),
                new ColumnDef("internal.major.max", new[] { "Internal", "Major Dia", "Max" }, Measure.Length),
                new ColumnDef("tap.diameter", new[] { "Tap Drill", "Diameter" }, Measure.Length),
                new ColumnDef("tap.size", new[] { "Tap Drill", "Size" }, Measure.Text),
            }),
        new TableSchema(TableKinds.TaperThread, "Taper thread sizes", new[] { Designation },
            new[]
            {
                L("od", "Outside Diameter", true), L("pitch", "Pitch", true),
                L("e0", "Pitch Dia at Small End"),
                new ColumnDef("l1", new[] { "Handtight Engagement", "Length" }, Measure.Length),
                new ColumnDef("e1", new[] { "Handtight Engagement", "Pitch Dia" }, Measure.Length),
                new ColumnDef("l2", new[] { "Effective Thread, External", "Length" }, Measure.Length),
                new ColumnDef("e2", new[] { "Effective Thread, External", "Pitch Dia" }, Measure.Length),
                new ColumnDef("l3", new[] { "Wrench Makeup, Internal", "Length" }, Measure.Length),
                new ColumnDef("e3", new[] { "Wrench Makeup, Internal", "Pitch Dia" }, Measure.Length),
                L("vanish", "Vanish Thread"), L("l4", "Overall Length, External"),
                new ColumnDef("l5", new[] { "Perfect External Threads", "Length" }, Measure.Length),
                new ColumnDef("e5", new[] { "Perfect External Threads", "Pitch Dia" }, Measure.Length),
                L("height", "Thread Height"), L("minor", "Minor Dia at Small End"),
                new ColumnDef("tap.diameter", new[] { "Tap Drill", "Diameter" }, Measure.Length),
                new ColumnDef("tap.size", new[] { "Tap Drill", "Size" }, Measure.Text),
                new ColumnDef("taper", "Taper Angle", Measure.Angle),
                L("useful", "Useful Thread Length"),
            }),
        new TableSchema(TableKinds.FastenerHoles, "Fastener holes",
            new[] { new ColumnDef("fastener", "Fastener", Measure.Text, true), new ColumnDef("size", "Size", Measure.Text, true) },
            new[]
            {
                new ColumnDef("close", new[] { "Shaft Clearance", "Close" }, Measure.Length),
                new ColumnDef("normal", new[] { "Shaft Clearance", "Normal" }, Measure.Length, true),
                new ColumnDef("loose", new[] { "Shaft Clearance", "Loose" }, Measure.Length),
                new ColumnDef("head", new[] { "Head", "Type" }, Measure.Text),
                new ColumnDef("head.diameter", new[] { "Head", "Diameter" }, Measure.Length),
                new ColumnDef("head.depth", new[] { "Head", "Depth" }, Measure.Length),
                new ColumnDef("head.angle", new[] { "Head", "Angle" }, Measure.Angle),
                new ColumnDef("screws", new[] { "Parts", "Screws" }, Measure.Text),
                new ColumnDef("nuts", new[] { "Parts", "Nuts" }, Measure.Text),
                new ColumnDef("washers", new[] { "Parts", "Washers" }, Measure.Text),
            }),
        new TableSchema(TableKinds.Screw, "Screws", new[] { Designation },
            new[]
            {
                L("d", "Thread Diameter", true), L("p", "Pitch", true), L("dk", "Head Diameter", true), L("k", "Head Height", true),
                L("s", "Socket Size", true), L("t", "Socket Depth", true), L("b", "Thread Length", true), L("l", "Length", true),
                T(SizeColumn, "Size"), T(PartNumberColumn, "Part Number"),
            }),
        new TableSchema(TableKinds.Nut, "Nuts", new[] { Designation },
            new[] { L("d", "Thread Diameter", true), L("p", "Pitch", true), L("s", "Width Across Flats", true), L("m", "Height", true), T(SizeColumn, "Size"), T(PartNumberColumn, "Part Number") }),
        new TableSchema(TableKinds.Washer, "Washers", new[] { Designation },
            new[] { L("d1", "Inside Diameter", true), L("d2", "Outside Diameter", true), L("h", "Thickness", true), T(SizeColumn, "Size"), T(PartNumberColumn, "Part Number") }),
        new TableSchema(TableKinds.Material, "Materials", new[] { new ColumnDef("name", "Name", Measure.Text, true) },
            new ColumnDef[]
            {
                new("density", new[] { "Physical", "Density" }, Measure.Density, true),
                new("modulus", new[] { "Mechanical", "Elastic Modulus" }, Measure.Modulus),
                new("poisson", new[] { "Mechanical", "Poisson's Ratio" }, Measure.Number),
                new("shear", new[] { "Mechanical", "Shear Modulus" }, Measure.Modulus),
                new("yield", new[] { "Mechanical", "Yield Strength" }, Measure.Strength),
                new("tensile", new[] { "Mechanical", "Tensile Strength" }, Measure.Strength),
                new("elongation", new[] { "Mechanical", "Elongation" }, Measure.Percent),
                new("hardness", new[] { "Mechanical", "Hardness" }, Measure.Number),
                new("expansion", new[] { "Thermal", "Expansion" }, Measure.Expansion),
                new("conductivity", new[] { "Thermal", "Conductivity" }, Measure.Conductivity),
                new("specificHeat", new[] { "Thermal", "Specific Heat" }, Measure.SpecificHeat),
            }.Concat(Look.Select(c => new ColumnDef(c, new[] { "Appearance", LookHeading(c) }, Measure.Number)))
            .Concat(new[] { T("type", "Type"), T("category", "Category"), T("spec", "Specification"), T("hardnessScale", "Hardness Scale"), T("description", "Description") })
            .ToArray()),
        new TableSchema(TableKinds.Appearance, "Appearances", new[] { new ColumnDef("name", "Name", Measure.Text, true) },
            Look.Select(c => new ColumnDef(c, LookHeading(c), Measure.Number, c is "r" or "g" or "b")).ToArray()),
        new TableSchema(TableKinds.HoleType, "Hole types", new[] { new ColumnDef("size", "Size", Measure.Text, true) },
            Array.Empty<ColumnDef>(), Open: true),
    };

    static string LookHeading(string c) => c switch
    {
        "r" => "Red", "g" => "Green", "b" => "Blue", "metallic" => "Metallic", "roughness" => "Roughness", _ => "Opacity",
    };

    static readonly Dictionary<string, TableSchema> ByKind = All.ToDictionary(s => s.Kind);

    /// <summary>
    /// What is wrong with a table by its kind's schema (null: nothing): a kind SpryCAD knows; value columns of the
    /// schema (an open kind's own too); every required column given, with a value in every row; rows keyed by the
    /// schema's key columns, each once.
    /// </summary>
    public static string? Check(LibraryTable t)
    {
        if (t.Schema is not { } schema) return t.Kind.Length == 0 ? $"{t.Key} has no kind." : $"{t.Key} is of a kind this SpryCAD does not know ({t.Kind}).";
        foreach (var c in t.Columns)
            if (schema.Column(c) is { IsText: true }) return $"{t.Key}: {c} is a text column, not a value column.";
            else if (schema.Column(c) == null && !schema.Open) return $"{t.Key}: {schema.Title.ToLowerInvariant()} have no column {c}.";
        foreach (var req in schema.Columns.Where(c => c.Required && !c.IsText))
            if (!t.Columns.Contains(req.Path)) return $"{t.Key} needs a {req.Path} column.";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in t.Rows)
        {
            if (r.Keys.Count != schema.Key.Length || r.Keys.Any(string.IsNullOrWhiteSpace))
                return $"{t.Key}: a row needs {string.Join(" and ", schema.Key.Select(k => k.Path))}.";
            if (!keys.Add(r.Key)) return $"{t.Key}: {r.Key} is listed twice.";
            if (r.Values.Count != t.Columns.Count) return $"{t.Key}:{r.Key} has {r.Values.Count} values for {t.Columns.Count} columns.";
            for (int c = 0; c < r.Values.Count; c++)
                if (!double.IsFinite(r.Values[c]) && (schema.Column(t.Columns[c])?.Required != false))
                    return $"{t.Key}:{r.Key} has no {t.Columns[c]}.";
        }
        return null;
    }

    /// <summary>A kind's schema (null for a kind this SpryCAD does not know).</summary>
    public static TableSchema? Of(string kind) => ByKind.GetValueOrDefault(kind);

    /// <summary>
    /// What a kind's column is: the schema's; an open kind's own column a length, or an angle when named with "angle"
    /// (a hole type's CsAngle); a column no schema knows, a plain number.
    /// </summary>
    public static Measure UnitOf(string kind, string column)
    {
        var s = Of(kind);
        if (s?.Column(column) is { } c) return c.Unit;
        if (s?.Open == true) return column.Contains("angle", StringComparison.OrdinalIgnoreCase) ? Measure.Angle : Measure.Length;
        return Measure.Number;
    }
}

/// <summary>
/// The rows of library data a part uses, copied into it: the part rebuilds from these alone, with no library
/// installed, and never changes when a library does until the user accepts the update. Keyed by <see cref="Item"/>
/// ("ISO261-coarse:M8").
/// </summary>
public sealed class LibrarySnapshot
{
    public string Table { get; set; } = "";
    /// <summary>The row's identity (<see cref="LibraryRow.Key"/>).</summary>
    public string Row { get; set; } = "";

    /// <summary>The row as said to people: its keys spaced, then its table ("Socket head cap screw M8 (ISO-holes)").</summary>
    [JsonIgnore] public string Name => $"{string.Join(" ", LibraryRow.SplitKeys(Row))} ({Table})";
    /// <summary>The table's kind: its schema, what the columns are.</summary>
    public string Kind { get; set; } = "";
    /// <summary>The units the values are in (the table's).</summary>
    public TableUnits Units { get; set; }
    /// <summary>The library the row came from, and the revision of it.</summary>
    public Guid LibraryId { get; set; }
    public Guid LibraryRevision { get; set; }
    public string LibraryName { get; set; } = "";
    public List<string> Columns { get; set; } = new();
    public List<double> Values { get; set; } = new();
    public SortedDictionary<string, string>? Text { get; set; }

    [JsonIgnore] public string Item => ItemKey(Table, Row);

    /// <summary>A row's item key: its table's key and its identity, joined by ':' (a table key holds no ':').</summary>
    public static string ItemKey(string table, string row) => table + ":" + row;

    public static (string table, string row) Split(string item)
    {
        var i = item.IndexOf(':');
        return i < 0 ? (item, "") : (item[..i], item[(i + 1)..]);
    }

    /// <summary>
    /// An item key as parts before library v2 wrote it ("ISO273/M8", the table and row joined by '/') in the present
    /// form ("ISO273:M8"); a present one as it is.
    /// </summary>
    public static string Upgrade(string item) =>
        item.Contains(':') || item.IndexOf('/') is var i && i < 0 ? item : item[..i] + ":" + item[(i + 1)..];

    /// <summary>
    /// A snapshot as parts before library v2 kept it (no <see cref="Kind"/>; lengths in millimeters, a material's
    /// properties in SI) made a present one: its kind found from its columns (the schema whose value columns it has),
    /// a material's values put in its table's metric units. Nothing for a present snapshot. False when no kind fits.
    /// </summary>
    public bool UpgradeV1()
    {
        if (Kind.Length > 0) return true;
        var cols = Columns.ToHashSet(StringComparer.Ordinal);
        var schema = Schemas.All.FirstOrDefault(s => !s.Open && s.Columns.Where(c => !c.IsText).Select(c => c.Path).ToHashSet().SetEquals(cols));
        if (schema == null) return false;
        Kind = schema.Kind;
        Units = TableUnits.Metric;
        if (Kind == TableKinds.Material)
            for (int i = 0; i < Columns.Count && i < Values.Count; i++)
                if (!double.IsNaN(Values[i])) Values[i] = Schemas.UnitOf(Kind, Columns[i]).FromBase(Values[i], TableUnits.Metric);
        return true;
    }

    /// <summary>A column's value as the table gives it (in its units); null when none.</summary>
    public double? Value(string column) { var i = Columns.IndexOf(column); return i >= 0 && i < Values.Count && !double.IsNaN(Values[i]) ? Values[i] : null; }

    /// <summary>A column's value as code uses it (mm, degrees, SI); null when none.</summary>
    public double? Base(string column) => Value(column) is { } v ? Schemas.UnitOf(Kind, column).ToBase(v, Units) : null;

    /// <summary>Whether the data is the same (where it came from aside).</summary>
    public bool SameData(LibrarySnapshot o) =>
        Kind == o.Kind && Units == o.Units && Columns.SequenceEqual(o.Columns) && Values.SequenceEqual(o.Values)
        && (Text ?? new()).SequenceEqual(o.Text ?? new());
}
