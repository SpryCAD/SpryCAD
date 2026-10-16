namespace SpryCad.Core.Library;

/// <summary>
/// One table of standards data (the file format spec, section 34): thread sizes, clearance holes, counterbores. Rows
/// are keyed by designation ("M8", "1/4-20 UNC") and listed in the order a picker shows them; values are numbers in
/// the table's column units (lengths always in millimetres, as the model is), plus optional text (part numbers,
/// suppliers) for company libraries.
/// </summary>
public sealed class LibraryTable
{
    /// <summary>The table's stable key ("ISO261-coarse"): what parts' snapshots name it by. Never reused for other data.</summary>
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>What the table is for (<see cref="TableKinds"/>): the commands that list it.</summary>
    public string Kind { get; set; } = "";
    /// <summary>The standard the values come from ("ISO 261").</summary>
    public string Standard { get; set; } = "";
    /// <summary>Whether every value was checked against the published standard (not a secondary source).</summary>
    public bool Verified { get; set; }
    /// <summary>
    /// The units the table's lengths are shown and typed in: <see cref="Inch"/> for an inch standard, else millimetres
    /// (null). Values are stored in millimetres either way; angle columns (named "angle") are degrees in both.
    /// </summary>
    public string? Units { get; set; }
    public List<string> Columns { get; set; } = new();
    public List<LibraryRow> Rows { get; set; } = new();

    public const string Inch = "in";

    /// <summary>Millimetres per unit the column is shown in: 25.4 for a length column of an inch table, else 1.</summary>
    public double Scale(string column) => Units == Inch && !column.Contains("angle", StringComparison.OrdinalIgnoreCase) ? 25.4 : 1;

    public LibraryRow? Row(string key) => Rows.FirstOrDefault(r => r.Key == key);

    public int Column(string name) => Columns.IndexOf(name);
}

public sealed class LibraryRow
{
    public string Key { get; set; } = "";
    /// <summary>One per column of the table.</summary>
    public List<double> Values { get; set; } = new();
    /// <summary>Text the row carries (part number, supplier), sorted by name; null when none.</summary>
    public SortedDictionary<string, string>? Text { get; set; }

    public LibraryRow() { }
    public LibraryRow(string key, params double[] values) { Key = key; Values = values.ToList(); }
}

/// <summary>The kinds of table the app reads (<see cref="LibraryTable.Kind"/>) and their columns.</summary>
public static class TableKinds
{
    /// <summary>Thread sizes: "major" diameter and "pitch" (mm).</summary>
    public const string Thread = "thread";
    /// <summary>Clearance holes for a screw size: "close", "normal" and "loose" diameters (mm).</summary>
    public const string Clearance = "clearance";
    /// <summary>Counterbores for a screw size: "diameter" and "depth" (mm).</summary>
    public const string Counterbore = "counterbore";
    /// <summary>Countersinks for a screw size: "diameter" (mm) and "angle" (degrees).</summary>
    public const string Countersink = "countersink";
    /// <summary>Screws, a row per size and length ("M8x30"): <see cref="Fasteners.ScrewColumns"/> (mm).</summary>
    public const string Screw = "screw";
    /// <summary>Nuts, a row per size: <see cref="Fasteners.NutColumns"/> (mm).</summary>
    public const string Nut = "nut";
    /// <summary>Washers, a row per size: <see cref="Fasteners.WasherColumns"/> (mm).</summary>
    public const string Washer = "washer";
}

/// <summary>
/// What a company library allows of a table (section 34, layer 3): only the listed rows (preferred sizes), or every
/// row but the denied ones; <see cref="Hidden"/> hides the whole table. An entry names a row, or a size: every row of
/// that size (a part family's rows, "M8x30", by their size "M8": <see cref="Fasteners.SizeKey"/>).
/// </summary>
public sealed class LibraryRestriction
{
    public string Table { get; set; } = "";
    /// <summary>The only rows offered (null: all).</summary>
    public List<string>? Allow { get; set; }
    /// <summary>Rows never offered.</summary>
    public List<string>? Deny { get; set; }
    public bool Hidden { get; set; }

    public bool Allows(string row, string? size = null) =>
        !Hidden && (Allow == null || Allow.Contains(row) || size != null && Allow.Contains(size))
        && Deny?.Contains(row) != true && (size == null || Deny?.Contains(size) != true);
}

/// <summary>
/// The rows of library data a part uses, copied into it (section 34): the part rebuilds from these alone, with no
/// library installed, and never changes when a library does until the user accepts the update. Keyed by
/// <see cref="Item"/> ("ISO261-coarse/M8").
/// </summary>
public sealed class LibrarySnapshot
{
    public string Table { get; set; } = "";
    public string Row { get; set; } = "";
    /// <summary>The library the row came from, and the revision of it.</summary>
    public Guid LibraryId { get; set; }
    public Guid LibraryRevision { get; set; }
    public string LibraryName { get; set; } = "";
    public List<string> Columns { get; set; } = new();
    public List<double> Values { get; set; } = new();
    public SortedDictionary<string, string>? Text { get; set; }

    public string Item => ItemKey(Table, Row);

    public static string ItemKey(string table, string row) => table + "/" + row;

    public static (string table, string row) Split(string item)
    {
        var i = item.IndexOf('/');
        return i < 0 ? (item, "") : (item[..i], item[(i + 1)..]);
    }

    public double? Value(string column) { var i = Columns.IndexOf(column); return i >= 0 && i < Values.Count ? Values[i] : null; }

    /// <summary>Whether the data is the same (where it came from aside).</summary>
    public bool SameData(LibrarySnapshot o) =>
        Columns.SequenceEqual(o.Columns) && Values.SequenceEqual(o.Values)
        && (Text ?? new()).SequenceEqual(o.Text ?? new());
}
