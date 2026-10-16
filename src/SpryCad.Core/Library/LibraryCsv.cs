using System.Globalization;
using System.Text;

namespace SpryCad.Core.Library;

/// <summary>
/// A library table's rows as CSV, to edit in a spreadsheet: a header row (the first cell names the row column, then
/// the table's columns, then its text as <c>text:name</c>), then a row per designation: its key, its values (numbers,
/// invariant culture, round-trippable) and its text (empty: none). Quoted as RFC 4180; tab-separated text (pasted from
/// a spreadsheet) reads too.
/// </summary>
public static class LibraryCsv
{
    public const string TextPrefix = "text:";

    /// <summary>The table's rows as CSV (CRLF line ends, as spreadsheets write them).</summary>
    public static string Write(LibraryTable t)
    {
        var texts = TextColumns(t.Rows);
        var sb = new StringBuilder();
        Line(sb, new[] { "row" }.Concat(t.Columns).Concat(texts.Select(n => TextPrefix + n)));
        foreach (var r in t.Rows)
            Line(sb, new[] { r.Key }
                .Concat(r.Values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))
                .Concat(texts.Select(n => r.Text != null && r.Text.TryGetValue(n, out var s) ? s : "")));
        return sb.ToString();
    }

    /// <summary>The text names the rows use, sorted.</summary>
    public static List<string> TextColumns(IEnumerable<LibraryRow> rows) =>
        rows.Where(r => r.Text != null).SelectMany(r => r.Text!.Keys).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The columns and rows the CSV holds. Throws <see cref="FormatException"/> naming the line and cell when a value is
    /// not a number, a row key is empty or repeated, or a row has more cells than the header.
    /// </summary>
    public static (List<string> columns, List<LibraryRow> rows) Read(string csv)
    {
        var lines = Parse(csv).Where(l => l.cells.Any(c => c.Length > 0)).ToList();
        if (lines.Count == 0) throw new FormatException("The file is empty: it needs a header row (row, then the columns).");
        var header = lines[0].cells.Select(c => c.Trim()).ToList();
        if (header.Count < 1) throw new FormatException("The header row has no columns.");
        var columns = new List<string>();
        var texts = new List<(int index, string name)>();
        var numbers = new List<int>();
        for (int i = 1; i < header.Count; i++)
        {
            var h = header[i];
            if (h.Length == 0) throw new FormatException($"Line {lines[0].line}: column {i + 1} has no name.");
            if (h.StartsWith(TextPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var name = h[TextPrefix.Length..].Trim();
                if (name.Length == 0) throw new FormatException($"Line {lines[0].line}: column {i + 1} ({h}) names no text.");
                texts.Add((i, name));
            }
            else
            {
                if (columns.Contains(h)) throw new FormatException($"Line {lines[0].line}: two columns are named \"{h}\".");
                columns.Add(h);
                numbers.Add(i);
            }
        }
        var rows = new List<LibraryRow>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (line, cells) in lines.Skip(1))
        {
            if (cells.Count > header.Count && cells.Skip(header.Count).Any(c => c.Trim().Length > 0))
                throw new FormatException($"Line {line}: {cells.Count} cells for {header.Count} columns.");
            string Cell(int i) => i < cells.Count ? cells[i].Trim() : "";
            var key = Cell(0);
            if (key.Length == 0) throw new FormatException($"Line {line}: the row has no designation (first cell).");
            if (!keys.Add(key)) throw new FormatException($"Line {line}: {key} is listed twice.");
            var row = new LibraryRow { Key = key };
            foreach (var i in numbers)
            {
                var s = Cell(i);
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
                    throw new FormatException($"Line {line}: {key}'s {header[i]} is not a number (\"{s}\").");
                row.Values.Add(v);
            }
            foreach (var (i, name) in texts)
                if (Cell(i) is { Length: > 0 } s) (row.Text ??= new SortedDictionary<string, string>(StringComparer.Ordinal))[name] = s;
            rows.Add(row);
        }
        return (columns, rows);
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
