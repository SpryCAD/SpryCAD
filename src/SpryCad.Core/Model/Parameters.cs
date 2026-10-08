using System.Globalization;

namespace SpryCad.Core.Model;

/// <summary>
/// A design table: rows of values for a set of variables, one row in use. A screw's sizes: columns D, Pitch,
/// HeadD, HeadH; rows M3, M4, M5. Choosing a row sets every column's variable to that row's value (an expression,
/// usually a number). The table's own name is a variable too: the number of the row in use (1 = the first), for
/// conditions ("Size &gt;= 3"). User-facing tables are a list to choose from in the Configure dialog.
/// </summary>
public sealed class DesignTable
{
    /// <summary>The choice ("Size"), and the variable holding the row number.</summary>
    public string Name { get; set; } = "Size";
    /// <summary>Shown in the Configure dialog (a list of the rows); otherwise only the part sets it.</summary>
    public bool Exposed { get; set; } = true;
    public string? Description { get; set; }
    /// <summary>The variables the table sets, one per column.</summary>
    public List<string> Columns { get; set; } = new();
    public List<DesignRow> Rows { get; set; } = new();
    /// <summary>The key of the row in use; null (or a key no row has): the first row.</summary>
    public string? Selected { get; set; }

    /// <summary>The row in use (null: the table has none).</summary>
    public DesignRow? Current => Rows.FirstOrDefault(r => r.Key == Selected) ?? Rows.FirstOrDefault();

    public DesignRow? Row(string key) => Rows.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A table from spreadsheet cells (tab, semicolon or comma separated): the header's cells after the first are the
    /// columns, each further line a key and its values (blank: 0). Null when there is no such table.
    /// </summary>
    public static DesignTable? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2) return null;
        char sep = lines[0].Contains('\t') ? '\t' : lines[0].Contains(';') ? ';' : ',';
        string[] Cells(string l) => l.Split(sep).Select(c => c.Trim().Trim('"')).ToArray();
        var header = Cells(lines[0]);
        if (header.Length < 2) return null;
        var t = new DesignTable { Columns = header.Skip(1).ToList() };
        foreach (var l in lines.Skip(1))
        {
            var cells = Cells(l);
            if (cells[0].Length == 0) continue;
            // (decimal commas, from a spreadsheet in another locale: "0,7" between tabs or semicolons)
            string Value(int c) => c + 1 < cells.Length && cells[c + 1].Length > 0 ? (sep != ',' ? cells[c + 1].Replace(',', '.') : cells[c + 1]) : "0";
            t.Rows.Add(new DesignRow { Key = cells[0], Values = t.Columns.Select((_, c) => Value(c)).ToList() });
        }
        return t.Rows.Count == 0 ? null : t;
    }
}

/// <summary>One row of a design table: its key ("M4") and a value per column.</summary>
public sealed class DesignRow
{
    public string Key { get; set; } = "";
    public List<string> Values { get; set; } = new();

    public string ValueAt(int column) => column < Values.Count && !string.IsNullOrWhiteSpace(Values[column]) ? Values[column] : "0";
}

/// <summary>A user-facing parameter of a part, as the Configure dialog shows it.</summary>
public sealed record UserParameter(string Name, string? Description, string Default, IReadOnlyList<string>? Choices, double? Min, double? Max)
{
    /// <summary>A choice of a design table's rows (else a value).</summary>
    public bool IsChoice => Choices != null;
    /// <summary>A yes/no value: limits 0 and 1.</summary>
    public bool IsToggle => Choices == null && Min == 0 && Max == 1;
}

/// <summary>
/// Configurations: a part's user-facing parameters (<see cref="Equation.Exposed"/> variables and design tables)
/// given values, e.g. by an assembly for each instance. Values are by parameter name: a design table's row key, or a
/// variable's value (an expression of numbers). Only what differs from the part's own values is kept, so a part
/// whose defaults change is followed by every instance that did not set them.
/// </summary>
public static class Configurations
{
    /// <summary>The part's user-facing parameters: design tables first, then variables, as listed.</summary>
    public static List<UserParameter> UserParameters(this CadDocument doc)
    {
        var list = new List<UserParameter>();
        foreach (var t in doc.Tables ?? new())
            if (t.Exposed && !string.IsNullOrWhiteSpace(t.Name) && t.Rows.Count > 0)
                list.Add(new UserParameter(t.Name.Trim(), t.Description, t.Current!.Key, t.Rows.Select(r => r.Key).ToList(), null, null));
        foreach (var e in doc.Equations)
            if (e.Exposed && !string.IsNullOrWhiteSpace(e.Name))
                list.Add(new UserParameter(e.Name.Trim(), e.Description, e.Expr, null, e.Min, e.Max));
        return list;
    }

    /// <summary>
    /// Why a value cannot be given to a parameter (null: it can): not a user-facing parameter, not one of the
    /// table's rows, not a number, or outside the limits.
    /// </summary>
    public static string? Check(this CadDocument doc, string name, string value)
    {
        var p = doc.UserParameters().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (p == null) return $"{name} is not a user-facing parameter of the part.";
        if (p.IsChoice) return p.Choices!.Any(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase)) ? null : $"{name}: {value} is not one of {string.Join(", ", p.Choices!)}.";
        // (a number, or an expression of numbers and units: not of the part's other variables)
        if (!Expr.TryEval(value, null, out var v, out var err)) return $"{name}: {err}";
        if (p.Min is { } lo && v < lo - 1e-9) return $"{name} must be at least {Expr.Format(lo)}.";
        if (p.Max is { } hi && v > hi + 1e-9) return $"{name} must be at most {Expr.Format(hi)}.";
        return null;
    }

    /// <summary>
    /// The values of <paramref name="values"/> that differ from the part's own, by the parameters' names as the part
    /// spells them; values for parameters the part does not have, or cannot take, are left out. Null when none remain.
    /// </summary>
    public static Dictionary<string, string>? Normalize(this CadDocument doc, IReadOnlyDictionary<string, string>? values)
    {
        if (values == null || values.Count == 0) return null;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in doc.UserParameters())
        {
            var (_, v) = values.FirstOrDefault(kv => string.Equals(kv.Key, p.Name, StringComparison.OrdinalIgnoreCase));
            if (v == null || doc.Check(p.Name, v) != null) continue;
            v = v.Trim();
            if (p.IsChoice) v = p.Choices!.First(c => string.Equals(c, v, StringComparison.OrdinalIgnoreCase));
            if (SameValue(p, v, p.Default)) continue;
            result[p.Name] = v;
        }
        return result.Count == 0 ? null : result;
    }

    static bool SameValue(UserParameter p, string a, string b)
    {
        if (p.IsChoice) return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        if (a.Trim() == b.Trim()) return true;
        return Expr.TryEval(a, null, out var x, out _) && Expr.TryEval(b, null, out var y, out _) && Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(x));
    }

    /// <summary>
    /// The part as configured: a copy with the given values in place of its own (design tables' rows chosen,
    /// variables set); the part itself when there is nothing to change. Values it cannot take are ignored
    /// (<see cref="Normalize"/>).
    /// </summary>
    public static CadDocument Configured(this CadDocument doc, IReadOnlyDictionary<string, string>? values)
    {
        var set = doc.Normalize(values);
        if (set == null) return doc;
        var d = doc.Clone();
        foreach (var (name, v) in set)
        {
            if (d.Tables?.FirstOrDefault(t => string.Equals(t.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) is { } table)
                table.Selected = table.Row(v)!.Key;
            else if (d.Equations.FirstOrDefault(e => string.Equals(e.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) is { } eq)
                eq.Expr = v;
        }
        return d;
    }

    /// <summary>
    /// Sets the part's own values (its defaults) from a configuration, as <see cref="Configured"/> would on a copy.
    /// Returns whether anything changed.
    /// </summary>
    public static bool Apply(this CadDocument doc, IReadOnlyDictionary<string, string> values)
    {
        var set = doc.Normalize(values);
        if (set == null) return false;
        var c = doc.Configured(set);
        doc.Equations = c.Equations;
        doc.Tables = c.Tables;
        return true;
    }

    /// <summary>The part's values of its user-facing parameters, by name.</summary>
    public static Dictionary<string, string> Defaults(this CadDocument doc)
        => doc.UserParameters().ToDictionary(p => p.Name, p => p.Default, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A configuration as text, in the order the part lists its parameters: rows by their key, values by name
    /// ("M4, Length 12"); the part's own values for those not set. Empty for a part without user-facing parameters.
    /// </summary>
    public static string Label(this CadDocument doc, IReadOnlyDictionary<string, string>? values)
    {
        var set = doc.Normalize(values);
        var parts = new List<string>();
        foreach (var p in doc.UserParameters())
        {
            var v = set != null && set.TryGetValue(p.Name, out var x) ? x : p.Default;
            if (p.IsChoice) parts.Add(v);
            else if (p.IsToggle) { if (Expr.TryEval(v, null, out var b, out _) && Expr.Truthy(b)) parts.Add(p.Name); }
            else parts.Add($"{p.Name} {(Expr.TryEval(v, null, out var n, out _) ? Expr.Format(n) : v)}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>
    /// A key of stored values without the part at hand (the values as given, sorted; numbers as numbers): what an
    /// assembly's caches go by when the part is not loaded.
    /// </summary>
    public static string KeyOf(IReadOnlyDictionary<string, string>? values)
    {
        if (values == null || values.Count == 0) return "";
        return string.Join(";", values.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key.ToLowerInvariant() + "=" + Canon(kv.Value)));
    }

    static string Canon(string v) => Expr.TryEval(v, null, out var n, out _) ? n.ToString("R", CultureInfo.InvariantCulture) : v.Trim().ToLowerInvariant();
}
