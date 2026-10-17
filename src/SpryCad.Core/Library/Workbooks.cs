using System.Globalization;
using ClosedXML.Excel;
using SpryCad.Core.Model;

namespace SpryCad.Core.Library;

/// <summary>What the workbooks share: sheet names Excel accepts, cells read as text or numbers, the problem's place.</summary>
static class Sheets
{
    /// <summary>A sheet name for <paramref name="name"/>: Excel's limits (31 characters, none of []:*?/\), unique in <paramref name="taken"/>.</summary>
    public static string Name(string name, ISet<string> taken)
    {
        var clean = new string(name.Select(c => "[]:*?/\\".Contains(c) ? '_' : c).ToArray()).Trim('\'').Trim();
        if (clean.Length == 0) clean = "Sheet";
        if (clean.Length > 31) clean = clean[..31];
        var n = clean;
        for (int i = 2; taken.Contains(n); i++) { var tail = $" ({i})"; n = (clean.Length + tail.Length > 31 ? clean[..(31 - tail.Length)] : clean) + tail; }
        taken.Add(n);
        return n;
    }

    /// <summary>A header row, bold, frozen above the rows.</summary>
    public static void Header(IXLWorksheet ws, IReadOnlyList<string> cells)
    {
        for (int i = 0; i < cells.Count; i++) ws.Cell(1, i + 1).SetValue(cells[i]);
        ws.Row(1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);
    }

    /// <summary>Text as text (a "1/4" or "M6" kept, not taken for a date or number).</summary>
    public static void Text(IXLCell cell, string text)
    {
        cell.SetValue(text);
        cell.Style.NumberFormat.Format = "@";
    }

    /// <summary>The cell's text as shown (a number in invariant form, so "6.6" reads back as written), trimmed.</summary>
    public static string Read(IXLCell cell) => cell.DataType switch
    {
        XLDataType.Blank => "",
        XLDataType.Number => cell.GetDouble().ToString("R", CultureInfo.InvariantCulture),
        XLDataType.Boolean => cell.GetBoolean() ? "true" : "false",
        _ => cell.GetFormattedString().Trim(),
    };

    /// <summary>The cell as a number: a number cell, or text that is one (invariant); NaN blank; null not a number.</summary>
    public static double? Number(IXLCell cell)
    {
        if (cell.DataType == XLDataType.Blank) return double.NaN;
        if (cell.DataType == XLDataType.Number) return cell.GetDouble();
        var s = Read(cell);
        if (s.Length == 0) return double.NaN;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    /// <summary>The header row's names, up to the first blank.</summary>
    public static List<string> HeaderOf(IXLWorksheet ws)
    {
        var names = new List<string>();
        for (int c = 1; ; c++) { var s = Read(ws.Cell(1, c)); if (s.Length == 0) break; names.Add(s); }
        return names;
    }

    /// <summary>The rows after the header that have anything in their first <paramref name="width"/> cells.</summary>
    public static IEnumerable<int> Rows(IXLWorksheet ws, int width)
    {
        var last = ws.LastRowUsed()?.RowNumber() ?? 1;
        for (int r = 2; r <= last; r++)
            if (Enumerable.Range(1, Math.Max(1, width)).Any(c => Read(ws.Cell(r, c)).Length > 0)) yield return r;
    }

    public static bool Yes(string s) => s.Trim().ToLowerInvariant() is "true" or "yes" or "y" or "1" or "x";

    /// <summary>"Sheet ISO273, cell C5" for a problem's place.</summary>
    public static string At(IXLWorksheet ws, int row, int col) => $"Sheet {ws.Name}, cell {XLHelper.GetColumnLetterFromNumber(col)}{row}";
}

/// <summary>
/// Library tables as an Excel workbook (.xlsx), to edit in a spreadsheet (as Inventor's hole and thread tables are):
/// a <see cref="IndexSheet"/> sheet first, listing each table's sheet, key, title, kind, standard, units and whether
/// verified; then a sheet per table laid out as its CSV (<see cref="LibraryCsv"/>: a header row of "row", the value
/// columns, then text:name columns; a row per designation), in the table's own units. Reading checks every cell and
/// names the sheet and cell of anything wrong.
/// </summary>
public static class LibraryWorkbook
{
    public const string IndexSheet = "Tables";
    static readonly string[] IndexColumns = { "sheet", "key", "title", "kind", "standard", "units", "verified" };

    /// <summary>The tables (their values in their own units, as <see cref="LibraryCsv"/> writes them) to a workbook.</summary>
    public static void Write(IEnumerable<LibraryTable> tables, Stream stream)
    {
        using var wb = new XLWorkbook();
        var index = wb.AddWorksheet(IndexSheet);
        Sheets.Header(index, IndexColumns);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { IndexSheet };
        int ir = 2;
        foreach (var t in tables)
        {
            var name = Sheets.Name(t.Key, taken);
            Sheets.Text(index.Cell(ir, 1), name); Sheets.Text(index.Cell(ir, 2), t.Key); Sheets.Text(index.Cell(ir, 3), t.Title);
            Sheets.Text(index.Cell(ir, 4), t.Kind); Sheets.Text(index.Cell(ir, 5), t.Standard); Sheets.Text(index.Cell(ir, 6), t.Units ?? "mm");
            index.Cell(ir, 7).SetValue(t.Verified ? "yes" : "no");
            ir++;
            var ws = wb.AddWorksheet(name);
            var texts = LibraryCsv.TextColumns(t.Rows);
            Sheets.Header(ws, new[] { "row" }.Concat(t.Columns).Concat(texts.Select(n => LibraryCsv.TextPrefix + n)).ToList());
            int r = 2;
            foreach (var row in t.Rows)
            {
                Sheets.Text(ws.Cell(r, 1), row.Key);
                for (int c = 0; c < t.Columns.Count; c++)
                    if (c < row.Values.Count && double.IsFinite(row.Values[c])) ws.Cell(r, c + 2).SetValue(row.Values[c]);
                for (int i = 0; i < texts.Count; i++)
                    if (row.Text != null && row.Text.TryGetValue(texts[i], out var s)) Sheets.Text(ws.Cell(r, t.Columns.Count + 2 + i), s);
                r++;
            }
            ws.Columns().AdjustToContents();
        }
        index.Columns().AdjustToContents();
        wb.SaveAs(stream);
    }

    /// <summary>
    /// The workbook's tables (values in each table's own units, as <see cref="LibraryCsv.Read"/> gives them), and what
    /// is wrong with it (each with its sheet and cell; tables with problems are left out).
    /// </summary>
    public static (List<LibraryTable> tables, List<string> problems) Read(Stream stream)
    {
        var tables = new List<LibraryTable>();
        var problems = new List<string>();
        using var wb = new XLWorkbook(stream);
        if (!wb.TryGetWorksheet(IndexSheet, out var index))
        {
            problems.Add($"The workbook has no {IndexSheet} sheet listing its tables (export a library to see the layout).");
            return (tables, problems);
        }
        var head = Sheets.HeaderOf(index).Select(h => h.ToLowerInvariant()).ToList();
        int Col(string name) => head.IndexOf(name) + 1;
        if (Col("sheet") == 0 || Col("key") == 0) { problems.Add($"Sheet {IndexSheet}: the header row needs sheet and key columns."); return (tables, problems); }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ir in Sheets.Rows(index, head.Count))
        {
            string Cell(string name) => Col(name) is var c and > 0 ? Sheets.Read(index.Cell(ir, c)) : "";
            var sheet = Cell("sheet"); var key = Cell("key");
            if (key.Length == 0) { problems.Add($"{Sheets.At(index, ir, Col("key"))}: the table has no key."); continue; }
            if (!keys.Add(key)) { problems.Add($"{Sheets.At(index, ir, Col("key"))}: {key} is listed twice."); continue; }
            if (!wb.TryGetWorksheet(sheet.Length > 0 ? sheet : key, out var ws)) { problems.Add($"{Sheets.At(index, ir, Col("sheet"))}: there is no sheet {sheet}."); continue; }
            var units = Cell("units").ToLowerInvariant();
            var t = new LibraryTable
            {
                Key = key, Title = Cell("title"), Kind = Cell("kind"), Standard = Cell("standard"),
                Units = units is "in" or "inch" or "inches" ? LibraryTable.Inch : null, Verified = Sheets.Yes(Cell("verified")),
            };
            if (ReadTable(ws, t, problems)) tables.Add(t);
        }
        return (tables, problems);
    }

    // a table's sheet into t (its columns and rows); false when anything in it is wrong
    static bool ReadTable(IXLWorksheet ws, LibraryTable t, List<string> problems)
    {
        int before = problems.Count;
        var header = Sheets.HeaderOf(ws);
        if (header.Count == 0) { problems.Add($"Sheet {ws.Name}: the header row is empty (row, then the columns)."); return false; }
        var numbers = new List<int>();
        var texts = new List<(int col, string name)>();
        for (int i = 1; i < header.Count; i++)
        {
            var h = header[i];
            if (h.StartsWith(LibraryCsv.TextPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var n = h[LibraryCsv.TextPrefix.Length..].Trim();
                if (n.Length == 0) problems.Add($"{Sheets.At(ws, 1, i + 1)}: {h} names no text.");
                else texts.Add((i + 1, n));
            }
            else if (t.Columns.Contains(h)) problems.Add($"{Sheets.At(ws, 1, i + 1)}: two columns are named {h}.");
            else { t.Columns.Add(h); numbers.Add(i + 1); }
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in Sheets.Rows(ws, header.Count))
        {
            var key = Sheets.Read(ws.Cell(r, 1));
            if (key.Length == 0) { problems.Add($"{Sheets.At(ws, r, 1)}: the row has no designation."); continue; }
            if (!keys.Add(key)) { problems.Add($"{Sheets.At(ws, r, 1)}: {key} is listed twice."); continue; }
            var row = new LibraryRow { Key = key };
            for (int i = 0; i < numbers.Count; i++)
            {
                var v = Sheets.Number(ws.Cell(r, numbers[i]));
                if (v == null) problems.Add($"{Sheets.At(ws, r, numbers[i])}: {key}'s {t.Columns[i]} is not a number (\"{Sheets.Read(ws.Cell(r, numbers[i]))}\").");
                else if (double.IsNaN(v.Value) && t.Kind != TableKinds.Material) problems.Add($"{Sheets.At(ws, r, numbers[i])}: {key}'s {t.Columns[i]} is blank.");
                row.Values.Add(v ?? double.NaN);
            }
            foreach (var (col, name) in texts)
                if (Sheets.Read(ws.Cell(r, col)) is { Length: > 0 } s) (row.Text ??= new(StringComparer.Ordinal))[name] = s;
            t.Rows.Add(row);
        }
        return problems.Count == before;
    }
}

/// <summary>
/// A part's design tables and equations as an Excel workbook: an <see cref="EquationsSheet"/> sheet (name, expression,
/// user-facing, description), a <see cref="IndexSheet"/> sheet listing each table's sheet, name, description, whether
/// user-facing and the row in use, then a sheet per table (a header row of "row" and the variables it sets, a row per
/// configuration; numbers as numbers, expressions and text as text).
/// </summary>
public static class DesignTableWorkbook
{
    public const string EquationsSheet = "Equations";
    public const string IndexSheet = "Tables";

    public static void Write(IEnumerable<Equation> equations, IEnumerable<DesignTable> tables, Stream stream)
    {
        using var wb = new XLWorkbook();
        var eq = wb.AddWorksheet(EquationsSheet);
        Sheets.Header(eq, new[] { "name", "expression", "user-facing", "description" });
        int er = 2;
        foreach (var e in equations)
        {
            Sheets.Text(eq.Cell(er, 1), e.Name); Value(eq.Cell(er, 2), e.Expr);
            eq.Cell(er, 3).SetValue(e.Exposed ? "yes" : "no");
            if (e.Description != null) Sheets.Text(eq.Cell(er, 4), e.Description);
            er++;
        }
        eq.Columns().AdjustToContents();
        var index = wb.AddWorksheet(IndexSheet);
        Sheets.Header(index, new[] { "sheet", "name", "description", "user-facing", "selected" });
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { EquationsSheet, IndexSheet };
        int ir = 2;
        foreach (var t in tables)
        {
            var name = Sheets.Name(t.Name, taken);
            Sheets.Text(index.Cell(ir, 1), name); Sheets.Text(index.Cell(ir, 2), t.Name);
            if (t.Description != null) Sheets.Text(index.Cell(ir, 3), t.Description);
            index.Cell(ir, 4).SetValue(t.Exposed ? "yes" : "no");
            if (t.Selected != null) Sheets.Text(index.Cell(ir, 5), t.Selected);
            ir++;
            var ws = wb.AddWorksheet(name);
            Sheets.Header(ws, new[] { "row" }.Concat(t.Columns).ToList());
            int r = 2;
            foreach (var row in t.Rows)
            {
                Sheets.Text(ws.Cell(r, 1), row.Key);
                for (int c = 0; c < t.Columns.Count && c < row.Values.Count; c++) Value(ws.Cell(r, c + 2), row.Values[c]);
                r++;
            }
            ws.Columns().AdjustToContents();
        }
        index.Columns().AdjustToContents();
        wb.SaveAs(stream);
    }

    // a plain number as a number (sorts and sums in the spreadsheet), anything else as text
    static void Value(IXLCell cell, string v)
    {
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
            && d.ToString("R", CultureInfo.InvariantCulture) == v.Trim()) cell.SetValue(d);
        else if (v.Length > 0) Sheets.Text(cell, v);
    }

    /// <summary>The workbook's equations and tables (each table checked: a name, its rows' keys there and once), and what is wrong.</summary>
    public static (List<Equation> equations, List<DesignTable> tables, List<string> problems) Read(Stream stream)
    {
        var equations = new List<Equation>();
        var tables = new List<DesignTable>();
        var problems = new List<string>();
        using var wb = new XLWorkbook(stream);
        if (wb.TryGetWorksheet(EquationsSheet, out var eq))
        {
            var head = Sheets.HeaderOf(eq).Select(h => h.ToLowerInvariant()).ToList();
            int Col(string n) => head.IndexOf(n) + 1;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in Sheets.Rows(eq, head.Count))
            {
                var name = Col("name") > 0 ? Sheets.Read(eq.Cell(r, Col("name"))) : "";
                if (name.Length == 0) { problems.Add($"{Sheets.At(eq, r, 1)}: the equation has no name."); continue; }
                if (!names.Add(name)) { problems.Add($"{Sheets.At(eq, r, 1)}: {name} is listed twice."); continue; }
                var expr = Col("expression") > 0 ? Sheets.Read(eq.Cell(r, Col("expression"))) : "";
                if (expr.Length == 0) { problems.Add($"{Sheets.At(eq, r, Math.Max(1, Col("expression")))}: {name} has no expression."); continue; }
                var desc = Col("description") > 0 ? Sheets.Read(eq.Cell(r, Col("description"))) : "";
                equations.Add(new Equation
                {
                    Name = name, Expr = expr, Exposed = Col("user-facing") > 0 && Sheets.Yes(Sheets.Read(eq.Cell(r, Col("user-facing")))),
                    Description = desc.Length > 0 ? desc : null,
                });
            }
        }
        if (wb.TryGetWorksheet(IndexSheet, out var index))
        {
            var head = Sheets.HeaderOf(index).Select(h => h.ToLowerInvariant()).ToList();
            int Col(string n) => head.IndexOf(n) + 1;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ir in Sheets.Rows(index, head.Count))
            {
                string Cell(string n) => Col(n) is var c and > 0 ? Sheets.Read(index.Cell(ir, c)) : "";
                var name = Cell("name");
                if (name.Length == 0) { problems.Add($"{Sheets.At(index, ir, Math.Max(1, Col("name")))}: the table has no name."); continue; }
                if (!seen.Add(name)) { problems.Add($"{Sheets.At(index, ir, Math.Max(1, Col("name")))}: {name} is listed twice."); continue; }
                var sheet = Cell("sheet");
                if (!wb.TryGetWorksheet(sheet.Length > 0 ? sheet : name, out var ws)) { problems.Add($"{Sheets.At(index, ir, Math.Max(1, Col("sheet")))}: there is no sheet {sheet}."); continue; }
                var desc = Cell("description"); var sel = Cell("selected");
                var t = new DesignTable
                {
                    Name = name, Description = desc.Length > 0 ? desc : null, Exposed = Col("user-facing") == 0 || Sheets.Yes(Cell("user-facing")),
                    Selected = sel.Length > 0 ? sel : null,
                };
                int before = problems.Count;
                var header = Sheets.HeaderOf(ws);
                for (int i = 1; i < header.Count; i++)
                    if (t.Columns.Contains(header[i])) problems.Add($"{Sheets.At(ws, 1, i + 1)}: two columns are named {header[i]}.");
                    else t.Columns.Add(header[i]);
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in Sheets.Rows(ws, header.Count))
                {
                    var key = Sheets.Read(ws.Cell(r, 1));
                    if (key.Length == 0) { problems.Add($"{Sheets.At(ws, r, 1)}: the row has no name."); continue; }
                    if (!keys.Add(key)) { problems.Add($"{Sheets.At(ws, r, 1)}: {key} is listed twice."); continue; }
                    t.Rows.Add(new DesignRow { Key = key, Values = Enumerable.Range(0, t.Columns.Count).Select(c => Sheets.Read(ws.Cell(r, c + 2))).ToList() });
                }
                if (problems.Count == before) tables.Add(t);
            }
        }
        if (equations.Count == 0 && tables.Count == 0 && problems.Count == 0)
            problems.Add($"The workbook has neither an {EquationsSheet} nor a {IndexSheet} sheet (export a part's parameters to see the layout).");
        return (equations, tables, problems);
    }
}
