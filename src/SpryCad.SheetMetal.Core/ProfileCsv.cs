using System.Globalization;
using System.Text;

namespace SpryCad.SheetMetal;

/// <summary>
/// A shop profile table as CSV (how a shop's tables are brought in; sheet metal spec section 3): a header row naming
/// the key columns, then the columns, by name in any order; then a row per key. Cells are numbers or expressions
/// ("max(t, 0.5mm)"), "*" for any key. In an inch table a plain number in a length column is inches. Quoted as RFC
/// 4180; tab-separated text (pasted from a spreadsheet) reads too.
/// </summary>
public static class ProfileCsv
{
    /// <summary>The table's rows as CSV (CRLF line ends, as spreadsheets write them).</summary>
    public static string Write(ShopProfileTable t)
    {
        var sb = new StringBuilder();
        Line(sb, t.KeyColumns.Concat(t.Columns.Select(c => c.Name)));
        foreach (var r in t.Rows)
            Line(sb, r.Keys.Concat(r.Cells.Select((c, i) => Shown(t, t.Columns[i], c))));
        return sb.ToString();
    }

    static string Shown(ShopProfileTable t, ProfileColumn c, string cell) =>
        t.Units == ShopProfileTable.Inch && c.Kind == ProfileValueKind.Length && cell.EndsWith("in", StringComparison.Ordinal)
        && double.TryParse(cell.AsSpan(0, cell.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? cell[..^2] : cell;

    /// <summary>
    /// The table <paramref name="key"/> (<see cref="ProfileTables"/>) read from CSV, in <paramref name="units"/> (null:
    /// mm, "in"). Throws <see cref="FormatException"/> naming the line and cell for a column that is missing or
    /// unknown, a key repeated, or a value that does not read.
    /// </summary>
    public static ShopProfileTable Read(string key, string csv, string? units = null)
    {
        var t = ProfileTables.Empty(key, units);
        var lines = Parse(csv).Where(l => l.cells.Any(c => c.Trim().Length > 0)).ToList();
        if (lines.Count == 0) throw new FormatException($"The file is empty: it needs a header row ({string.Join(", ", t.KeyColumns.Concat(t.Columns.Select(c => c.Name)))}).");
        var header = lines[0].cells.Select(c => c.Trim()).ToList();
        int Index(string name)
        {
            var i = header.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) throw new FormatException($"Line {lines[0].line}: there is no \"{name}\" column.");
            return i;
        }
        var keyAt = t.KeyColumns.Select(Index).ToList();
        var colAt = t.Columns.Select(c => Index(c.Name)).ToList();
        var known = new HashSet<int>(keyAt.Concat(colAt));
        for (int i = 0; i < header.Count; i++)
            if (!known.Contains(i) && header[i].Length > 0) throw new FormatException($"Line {lines[0].line}: \"{header[i]}\" is not a column of {t.Title}.");
        foreach (var (line, cells) in lines.Skip(1))
        {
            string Cell(int i) => i < cells.Count ? cells[i].Trim() : "";
            var row = new ProfileRow { Keys = keyAt.Select(i => Cell(i)).ToList() };
            if (row.Keys.Any(k => k.Length == 0)) throw new FormatException($"Line {line}: a key is empty (use * for any).");
            for (int c = 0; c < t.Columns.Count; c++)
            {
                var s = Cell(colAt[c]);
                if (units == ShopProfileTable.Inch && t.Columns[c].Kind == ProfileValueKind.Length
                    && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) s += "in";
                row.Cells.Add(s);
            }
            t.Rows.Add(row);
        }
        var problems = t.Problems().ToList();
        if (problems.Count > 0) throw new FormatException(string.Join("\n", problems));
        return t;
    }

    static void Line(StringBuilder sb, IEnumerable<string> cells)
    {
        sb.Append(string.Join(",", cells.Select(Quote)));
        sb.Append("\r\n");
    }

    static string Quote(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>The CSV's records with the line each starts on; the separator is a tab when the first line has tabs and no commas.</summary>
    static List<(int line, List<string> cells)> Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var first = text.Split('\n', 2)[0];
        char sep = first.Contains('\t') && !first.Contains(',') ? '\t' : ',';
        var records = new List<(int, List<string>)>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        int line = 1, start = 1;
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else { if (ch == '\n') line++; cell.Append(ch); }
            }
            else if (ch == '"' && cell.ToString().Trim().Length == 0) { cell.Clear(); quoted = true; }
            else if (ch == sep) { cells.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\r') { }
            else if (ch == '\n')
            {
                cells.Add(cell.ToString()); cell.Clear();
                records.Add((start, cells));
                cells = new List<string>();
                start = ++line;
            }
            else cell.Append(ch);
        }
        if (quoted) throw new FormatException($"Line {start}: a quoted cell is not closed.");
        if (cell.Length > 0 || cells.Count > 0) { cells.Add(cell.ToString()); records.Add((start, cells)); }
        return records;
    }
}
