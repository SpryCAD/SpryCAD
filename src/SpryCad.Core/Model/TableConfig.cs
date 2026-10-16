namespace SpryCad.Core.Model;

/// <summary>
/// Design tables as named configurations. Besides the variables they set, a table's columns can be:
/// <list type="bullet">
/// <item>properties ($PartNumber, $Description, $Material: text, not variables, a "$" never starting a variable's
/// name): what the Bill of Materials and drawings say of the part in that row, and the material it is made of;</item>
/// <item>another table's name: the row of that table it picks ("M6"), so a row combines others ("M6x20, threaded");</item>
/// <item>an equation's name: the value it sets that equation to in that row.</item>
/// </list>
/// A table named after an equation lists that equation's standard values (Inventor's multi-value parameter): each row's
/// key is a value of it ("12", "16", "20" for L), the row picked setting it.
/// The first table to pick a row or set a value wins: a table of configurations is put before those it picks from.
/// </summary>
public static class TableConfig
{
    public const string PartNumber = "$PartNumber", Description = "$Description", Material = "$Material";
    public static readonly string[] Properties = { PartNumber, Description, Material };

    public static bool IsProperty(string? column) => column != null && column.TrimStart().StartsWith('$');

    /// <summary>A column's heading: a property's name ("Part Number"), else the variable's or table's.</summary>
    public static string Label(string column) => column.Trim() switch
    {
        PartNumber => "Part Number", Description => "Description", Material => "Material", var c => c.TrimStart('$'),
    };

    /// <summary>
    /// The part's design tables worked out together: each table's row in use (by its name; those another table picks
    /// as that one says), the equations they set (by name), and the properties the rows in use give.
    /// </summary>
    public sealed record Resolved(Dictionary<string, string> Rows, Dictionary<string, string> Sets, Dictionary<string, string> Props);

    public static Resolved Resolve(CadDocument doc)
    {
        var tables = (doc.Tables ?? new()).Where(t => !string.IsNullOrWhiteSpace(t.Name)).ToList();
        var picked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // the rows another table picks: settled in a few passes (a table may come before or after the one it picks from)
        for (int pass = 0; pass < 4; pass++)
        {
            var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tables)
            {
                if (RowOf(t, picked) is not { } row) continue;
                for (int c = 0; c < t.Columns.Count; c++)
                    if (TableNamed(tables, t.Columns[c], t) is { } other && !next.ContainsKey(other.Name.Trim()))
                        next[other.Name.Trim()] = Cell(row, c);
            }
            var same = next.Count == picked.Count && next.All(kv => picked.TryGetValue(kv.Key, out var v) && v == kv.Value);
            picked = next;
            if (same) break;
        }
        var rows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var equations = new HashSet<string>(doc.Equations.Select(e => e.Name.Trim()), StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables)
        {
            if (RowOf(t, picked) is not { } row) continue;
            rows[t.Name.Trim()] = row.Key;
            // (named after an equation: its standard values, the row's key the value)
            if (equations.Contains(t.Name.Trim())) sets.TryAdd(t.Name.Trim(), row.Key);
            for (int c = 0; c < t.Columns.Count; c++)
            {
                var col = t.Columns[c].Trim();
                if (IsProperty(col)) { if (Cell(row, c).Length > 0) props.TryAdd(col, Cell(row, c)); }
                else if (equations.Contains(col)) sets.TryAdd(col, row.ValueAt(c));
            }
        }
        return new Resolved(rows, sets, props);
    }

    static string Cell(DesignRow r, int c) => c < r.Values.Count ? r.Values[c].Trim() : "";

    static DesignRow? RowOf(DesignTable t, Dictionary<string, string> picked) =>
        picked.TryGetValue(t.Name.Trim(), out var k) && t.Row(k) is { } r ? r : t.Current;

    static DesignTable? TableNamed(List<DesignTable> tables, string column, DesignTable except) =>
        tables.FirstOrDefault(o => o != except && o.Name.Trim().Equals(column.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The table a column picks a row of (a combination), or null.</summary>
    public static DesignTable? PicksTable(CadDocument doc, DesignTable table, string column) =>
        IsProperty(column) ? null : TableNamed((doc.Tables ?? new()).ToList(), column, table);

    /// <summary>Whether a column sets an equation of the part (its value in each row in place of the equation's own).</summary>
    public static bool SetsEquation(CadDocument doc, string column) =>
        !IsProperty(column) && doc.Equations.Any(e => e.Name.Trim().Equals(column.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The part's properties as its design tables' rows in use give them ($PartNumber, $Description, $Material).</summary>
    public static Dictionary<string, string> PropertiesOf(CadDocument? doc) =>
        doc == null ? new(StringComparer.OrdinalIgnoreCase) : Resolve(doc).Props;

    /// <summary>
    /// The part made of the material its design tables' rows in use say ($Material: a library item), when they say one
    /// and it is not already; whether it changed.
    /// </summary>
    public static bool ApplyMaterial(CadDocument doc)
    {
        if (!PropertiesOf(doc).TryGetValue(Material, out var item) || item == doc.MaterialItem) return false;
        Library.PartMaterial.Set(doc, item);
        return true;
    }
}
