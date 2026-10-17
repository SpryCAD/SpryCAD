using System.Globalization;
using ClosedXML.Excel;
using SpryCad.Core.Model;

namespace SpryCad.Core.Library;

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
