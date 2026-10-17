using System.Globalization;
using ClosedXML.Excel;

namespace SpryCad.Core.Library;

/// <summary>
/// Library tables as an Excel workbook (docs/library-v2.md), to edit in a spreadsheet: a sheet per table that describes
/// itself, so a sheet copied into another workbook still reads (there is no index sheet). Row 1 gives the table's key,
/// kind, units, standard and whether it is verified, each after its label; row 2 its title; then its schema's grouped
/// headings, merged across their columns, the last heading row each column's name with its unit ("Max (mm)"); then a
/// row per entry, in the table's units. Reading finds columns by their headings, so they may be reordered; a heading
/// the schema does not have is a text column of the table's own (a part number), or, for a kind whose columns are its
/// own (a hole type), a value column when it gives a unit. A table with r, g and b columns (a material's or an
/// appearance's color, 0-1) shows them as one Color column, the color as hex ("#C0C0C0") in a cell filled with it,
/// read back into r, g and b (to 1/255; a sheet that has Red, Green and Blue columns reads those instead).
/// </summary>
public static class LibraryWorkbook
{
    const string ColorHeading = "Color";
    const int TitleRow = 2, FirstHeadingRow = 3;
    static readonly XLColor HeadingFill = XLColor.FromHtml("#E8ECF1");

    // a column as written: its headings (groups, then its name), its unit's label, and what it holds
    sealed record Col(string[] Heading, string Unit, int Key = -1, int Value = -1, string? Text = null, bool Color = false);

    /// <summary>The tables (their values in their own units) to a workbook, a sheet each.</summary>
    public static void Write(IEnumerable<LibraryTable> tables, Stream stream)
    {
        using var wb = new XLWorkbook();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables) WriteSheet(wb.AddWorksheet(Sheets.Name(t.Key, taken)), t);
        if (wb.Worksheets.Count == 0) wb.AddWorksheet("Empty");
        wb.SaveAs(stream);
    }

    static void WriteSheet(IXLWorksheet ws, LibraryTable t)
    {
        var schema = t.Schema;
        var units = t.Units;
        // row 1: what the table is; row 2: its title
        var meta = new (string label, string value)[]
        {
            ("Table", t.Key), ("Kind", t.Kind), ("Units", UnitsText(t)), ("Standard", t.Standard), ("Verified", t.Verified ? "yes" : "no"),
        };
        for (int i = 0; i < meta.Length; i++)
        {
            ws.Cell(1, 2 * i + 1).SetValue(meta[i].label);
            ws.Cell(1, 2 * i + 1).Style.Font.Bold = true;
            Sheets.Text(ws.Cell(1, 2 * i + 2), meta[i].value);
        }
        Sheets.Text(ws.Cell(TitleRow, 1), t.Title);
        ws.Cell(TitleRow, 1).Style.Font.Bold = true;
        ws.Cell(TitleRow, 1).Style.Font.FontSize = 13;

        // the columns: the key's, the values in the table's order (r, g and b as one Color column), then the text
        var cols = new List<Col>();
        var keyDefs = schema?.Key ?? new[] { new ColumnDef("designation", "Designation", Measure.Text, true) };
        for (int k = 0; k < keyDefs.Length; k++) cols.Add(new Col(keyDefs[k].Heading, "", Key: k));
        bool colored = new[] { "r", "g", "b" }.All(t.Columns.Contains);
        for (int c = 0; c < t.Columns.Count; c++)
        {
            var path = t.Columns[c];
            var head = schema?.Column(path)?.Heading ?? new[] { path };
            if (colored && path is "r" or "g" or "b")
            {
                if (path == "r") cols.Add(new Col(head[..^1].Append(ColorHeading).ToArray(), "", Color: true));
                continue;
            }
            cols.Add(new Col(head, t.Label(path), Value: c));
        }
        var texts = TextColumns(t, schema);
        foreach (var n in texts) cols.Add(new Col(schema?.Column(n)?.Heading ?? new[] { n }, "", Text: n));
        // in the schema's order, values and text together (a thread's External Class before its limits), the table's
        // own columns after
        if (schema != null)
        {
            double Rank(Col c, int i)
            {
                var path = c.Text ?? (c.Value >= 0 ? t.Columns[c.Value] : c.Color ? "r" : null);
                var at = path == null ? -1 : Array.FindIndex(schema.Columns, d => d.Path == path);
                return at < 0 ? schema.Columns.Length + i : at;
            }
            var ordered = cols.Skip(keyDefs.Length).Select((c, i) => (c, r: Rank(c, i))).OrderBy(x => x.r).Select(x => x.c).ToList();
            cols = cols.Take(keyDefs.Length).Concat(ordered).ToList();
        }

        // the headings: the groups from the top, each column's name (with its unit) on the last heading row
        int depth = Math.Max(1, cols.Max(c => c.Heading.Length));
        int leafRow = FirstHeadingRow + depth - 1;
        for (int i = 0; i < cols.Count; i++)
        {
            var h = cols[i].Heading;
            for (int g = 0; g < h.Length - 1; g++) Sheets.Text(ws.Cell(FirstHeadingRow + g, i + 1), h[g]);
            Sheets.Text(ws.Cell(leafRow, i + 1), h[^1] + (cols[i].Unit.Length > 0 ? $" ({cols[i].Unit})" : ""));
        }
        // a group over the columns next to each other that share it (and the groups above it) merged into one cell
        for (int g = 0; g < depth - 1; g++)
            for (int i = 0; i < cols.Count;)
            {
                int j = i;
                string? Group(int c, int level) => level < cols[c].Heading.Length - 1 ? cols[c].Heading[level] : null;
                while (j + 1 < cols.Count && Group(i, g) != null && Enumerable.Range(0, g + 1).All(l => Group(j + 1, l) == Group(i, l))) j++;
                if (j > i) ws.Range(FirstHeadingRow + g, i + 1, FirstHeadingRow + g, j + 1).Merge().Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                i = j + 1;
            }
        var heading = ws.Range(FirstHeadingRow, 1, leafRow, cols.Count);
        heading.Style.Font.Bold = true;
        heading.Style.Fill.BackgroundColor = HeadingFill;
        ws.SheetView.Freeze(leafRow, keyDefs.Length);

        // a row per entry
        int r = leafRow + 1;
        foreach (var row in t.Rows)
        {
            for (int i = 0; i < cols.Count; i++)
            {
                var c = cols[i];
                var cell = ws.Cell(r, i + 1);
                if (c.Key >= 0) Sheets.Text(cell, c.Key < row.Keys.Count ? row.Keys[c.Key] : "");
                else if (c.Value >= 0) { if (c.Value < row.Values.Count && double.IsFinite(row.Values[c.Value])) cell.SetValue(row.Values[c.Value]); }
                else if (c.Text != null) { if (row.Text != null && row.Text.TryGetValue(c.Text, out var s)) Sheets.Text(cell, s); }
                else if (c.Color && Rgb(t, row) is { } rgb)
                {
                    Sheets.Text(cell, $"#{rgb.r:X2}{rgb.g:X2}{rgb.b:X2}");
                    cell.Style.Fill.BackgroundColor = XLColor.FromArgb(rgb.r, rgb.g, rgb.b);
                    cell.Style.Font.FontColor = rgb.r * 0.3 + rgb.g * 0.59 + rgb.b * 0.11 < 128 ? XLColor.White : XLColor.Black;
                }
            }
            r++;
        }
        ws.Columns(1, cols.Count).AdjustToContents(FirstHeadingRow, Math.Max(leafRow, r - 1));
    }

    // the table's units as row 1 gives them: mm or in, a material's metric or inch-pound (either read as either)
    static string UnitsText(LibraryTable t) => t.Kind is TableKinds.Material or TableKinds.Appearance
        ? t.Units == TableUnits.Inch ? "inch-pound" : "metric"
        : t.Units == TableUnits.Inch ? "in" : "mm";

    // a row's r, g, b (0-1) as bytes; null when it has no color
    static (int r, int g, int b)? Rgb(LibraryTable t, LibraryRow row)
    {
        int? B(string c) => t.Column(c) is var i && i >= 0 && i < row.Values.Count && double.IsFinite(row.Values[i]) ? (int)Math.Round(Math.Clamp(row.Values[i], 0, 1) * 255) : null;
        return B("r") is { } r && B("g") is { } g && B("b") is { } b ? (r, g, b) : null;
    }

    // the text columns: the schema's in its order, then the table's own (sorted)
    static List<string> TextColumns(LibraryTable t, TableSchema? schema)
    {
        var used = t.Rows.Where(r => r.Text != null).SelectMany(r => r.Text!.Keys).ToHashSet(StringComparer.Ordinal);
        // (the schema's all, so a company sees where its part numbers go; then the table's own)
        var list = schema?.Columns.Where(c => c.IsText).Select(c => c.Path).ToList() ?? new List<string>();
        list.AddRange(used.Where(n => !list.Contains(n)).OrderBy(n => n, StringComparer.Ordinal));
        return list;
    }

    /// <summary>
    /// The workbook's tables (values in each table's units, as written) and what is wrong with it, each with its sheet
    /// and cell; a sheet with problems is left out. A sheet that is not a table's (no Table and Kind in row 1) is skipped.
    /// </summary>
    public static (List<LibraryTable> tables, List<string> problems) Read(Stream stream)
    {
        var tables = new List<LibraryTable>();
        var problems = new List<string>();
        using var wb = new XLWorkbook(stream);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ws in wb.Worksheets)
        {
            int before = problems.Count;
            var t = ReadSheet(ws, problems);
            if (t == null || problems.Count > before) continue;
            if (!keys.Add(t.Key)) { problems.Add($"Sheet {ws.Name}: a table keyed {t.Key} is on another sheet too."); continue; }
            tables.Add(t);
        }
        if (tables.Count == 0 && problems.Count == 0) problems.Add("The workbook has no table sheets (row 1 gives a table's key and kind; export a library to see the layout).");
        return (tables, problems);
    }

    static LibraryTable? ReadSheet(IXLWorksheet ws, List<string> problems)
    {
        // row 1: each label followed by its value
        var meta = new Dictionary<string, (string value, int col)>(StringComparer.OrdinalIgnoreCase);
        var lastCol = ws.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
        for (int c = 1; c < lastCol; c++)
            if (Sheets.Read(ws.Cell(1, c)) is { Length: > 0 } label && !meta.ContainsKey(label)) meta[label] = (Sheets.Read(ws.Cell(1, c + 1)), c + 1);
        if (!meta.ContainsKey("Table") && !meta.ContainsKey("Kind")) return null; // (not a table's sheet: notes, a chart)
        string Meta(string label) => meta.TryGetValue(label, out var m) ? m.value : "";
        var key = Meta("Table");
        if (key.Length == 0) { problems.Add($"Sheet {ws.Name}, row 1: the table has no key (after Table)."); return null; }
        var kindText = Meta("Kind");
        var schema = Schemas.Of(kindText) ?? Schemas.All.FirstOrDefault(s => s.Title.Equals(kindText, StringComparison.OrdinalIgnoreCase));
        if (schema == null) { problems.Add($"Sheet {ws.Name}, row 1: {(kindText.Length == 0 ? "the table has no kind (after Kind)" : $"\"{kindText}\" is not a kind of table")}."); return null; }
        var unitsText = Meta("Units").ToLowerInvariant();
        var t = new LibraryTable
        {
            Key = key, Kind = schema.Kind, Title = Sheets.Read(ws.Cell(TitleRow, 1)), Standard = Meta("Standard"), Verified = Sheets.Yes(Meta("Verified")),
            Units = unitsText is "in" or "inch" or "inches" or "inch-pound" ? TableUnits.Inch : TableUnits.Metric,
        };

        // the headings: the row whose first cell is the first key column's name ends them
        var keyLeaf = schema.Key[0].Heading[^1];
        int leafRow = -1;
        for (int r = FirstHeadingRow; r <= FirstHeadingRow + 8; r++)
            if (Sheets.Read(ws.Cell(r, 1)).Equals(keyLeaf, StringComparison.OrdinalIgnoreCase)) { leafRow = r; break; }
        if (leafRow < 0) { problems.Add($"Sheet {ws.Name}: no heading row (its first column headed {keyLeaf}) in rows {FirstHeadingRow} to {FirstHeadingRow + 8}."); return null; }

        // each column found by its headings: a key column, a value column of the schema's (or the table's own, given a
        // unit, where the kind's columns are its own), the Color (r, g and b as hex), else a text column of the table's own
        var keyCols = new int[schema.Key.Length];
        Array.Fill(keyCols, -1);
        var valueCols = new List<(int col, string path)>();
        var textCols = new List<(int col, string name)>();
        int colorCol = -1, colorAt = 0;
        int width = ws.Row(leafRow).LastCellUsed()?.Address.ColumnNumber ?? 0;
        for (int c = 1; c <= width; c++)
        {
            var leaf = Sheets.Read(ws.Cell(leafRow, c));
            if (leaf.Length == 0) continue;
            var (name, unit) = SplitUnit(leaf);
            var groups = Enumerable.Range(FirstHeadingRow, leafRow - FirstHeadingRow).Select(r => Sheets.Read(ws.Cell(r, c))).Where(g => g.Length > 0).ToArray();
            var path = groups.Append(name).ToArray();
            // (a key column by its name, where no group is over it: a thread's Class, not its Internal Class)
            if (groups.Length == 0 && Array.FindIndex(schema.Key, k => k.Heading[^1].Equals(name, StringComparison.OrdinalIgnoreCase)) is var ki && ki >= 0) { keyCols[ki] = c; continue; }
            if (name.Equals(ColorHeading, StringComparison.OrdinalIgnoreCase) && unit == null) { colorCol = c; colorAt = valueCols.Count; continue; }
            var def = schema.Columns.FirstOrDefault(d => d.Heading.SequenceEqual(path, StringComparer.OrdinalIgnoreCase))
                ?? (schema.Columns.Where(d => d.Heading[^1].Equals(name, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: 1 } one ? one[0] : null)
                ?? schema.Columns.FirstOrDefault(d => d.Path.Equals(name, StringComparison.Ordinal));
            if (def != null)
            {
                if (def.IsText) { textCols.Add((c, def.Path)); continue; }
                var expected = def.Unit.Label(t.Units);
                if (unit != null && expected.Length > 0 && !unit.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{Sheets.At(ws, leafRow, c)}: {name} is given in {unit}, but the table's units are {(t.Units == TableUnits.Inch ? "inches" : "metric")} ({expected}).");
                if (valueCols.Any(v => v.path == def.Path)) problems.Add($"{Sheets.At(ws, leafRow, c)}: two columns are {def.Path}.");
                valueCols.Add((c, def.Path));
            }
            else if (schema.Open && unit != null) valueCols.Add((c, name));
            else if (textCols.Any(x => x.name == name)) problems.Add($"{Sheets.At(ws, leafRow, c)}: two columns are named {name}.");
            else textCols.Add((c, name));
        }
        for (int k = 0; k < keyCols.Length; k++)
            if (keyCols[k] < 0) problems.Add($"Sheet {ws.Name}, row {leafRow}: no {schema.Key[k].Heading[^1]} column.");
        // the Color read as r, g and b where the sheet does not give them (a sheet that does: an earlier export, exact)
        bool hex = colorCol > 0 && new[] { "r", "g", "b" }.All(x => valueCols.All(v => v.path != x)) && new[] { "r", "g", "b" }.All(x => schema.Column(x) != null);
        t.Columns = valueCols.Select(v => v.path).ToList();
        // (where the Color column is, as the table had them)
        if (hex) t.Columns.InsertRange(colorAt, new[] { "r", "g", "b" });

        // a row per entry
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in Sheets.Rows(ws, width, leafRow))
        {
            if (keyCols.Any(k => k < 0)) break;
            var rowKeys = keyCols.Select(k => Sheets.Read(ws.Cell(r, k))).ToList();
            if (rowKeys.Any(k => k.Length == 0)) { problems.Add($"{Sheets.At(ws, r, keyCols[rowKeys.FindIndex(k => k.Length == 0)])}: the row has no {schema.Key[rowKeys.FindIndex(k => k.Length == 0)].Heading[^1]}."); continue; }
            var row = new LibraryRow { Keys = rowKeys };
            if (!ids.Add(row.Key)) { problems.Add($"{Sheets.At(ws, r, keyCols[0])}: {row.Key} is listed twice."); continue; }
            foreach (var (c, path) in valueCols)
            {
                var v = Sheets.Number(ws.Cell(r, c));
                if (v == null) problems.Add($"{Sheets.At(ws, r, c)}: {row.Key}'s {path} is not a number (\"{Sheets.Read(ws.Cell(r, c))}\").");
                else if (double.IsNaN(v.Value) && schema.Column(path) is not { Required: false }) problems.Add($"{Sheets.At(ws, r, c)}: {row.Key}'s {path} is blank.");
                row.Values.Add(v ?? double.NaN);
            }
            if (hex)
            {
                var rgbValues = new List<double>();
                var text = Sheets.Read(ws.Cell(r, colorCol)).Trim().TrimStart('#');
                if (text.Length == 0)
                {
                    if (schema.Column("r") is not { Required: false }) problems.Add($"{Sheets.At(ws, r, colorCol)}: {row.Key}'s color is blank.");
                    rgbValues.AddRange(new[] { double.NaN, double.NaN, double.NaN });
                }
                else if (text.Length == 6 && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                    rgbValues.AddRange(new[] { (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255 }.Select(x => x / 255.0));
                else
                {
                    problems.Add($"{Sheets.At(ws, r, colorCol)}: {row.Key}'s color is not a hex color like #C0C0C0 (\"{Sheets.Read(ws.Cell(r, colorCol))}\").");
                    rgbValues.AddRange(new[] { double.NaN, double.NaN, double.NaN });
                }
                row.Values.InsertRange(colorAt, rgbValues);
            }
            foreach (var (c, name) in textCols)
                if (Sheets.Read(ws.Cell(r, c)) is { Length: > 0 } s) (row.Text ??= new(StringComparer.Ordinal))[name] = s;
            t.Rows.Add(row);
        }
        return t;
    }

    // "Max (mm)": the name and the unit ("Max", "mm"); "Expansion (µm/(m·K))" too (the bracket matching the last); a
    // name with no unit, null
    static (string name, string? unit) SplitUnit(string heading)
    {
        var h = heading.Trim();
        if (!h.EndsWith(')')) return (h, null);
        int depth = 0, i = h.Length - 1;
        for (; i >= 0; i--)
        {
            if (h[i] == ')') depth++;
            else if (h[i] == '(' && --depth == 0) break;
        }
        return i <= 0 ? (h, null) : (h[..i].Trim(), h[(i + 1)..^1].Trim());
    }
}
