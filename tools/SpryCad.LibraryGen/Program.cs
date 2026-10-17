using System.Globalization;
using ClosedXML.Excel;
using SpryCad.Core.Library;

// SpryCAD's built-in library, authored as workbooks (data/library/standards.xlsx, materials.xlsx) in the layout a
// company library is exported and imported in, generated at build time (docs/library-v2.md):
//
//   gen <out-dir> <workbook>...   each workbook's tables checked against their schemas and written as
//                                 <out-dir>/<workbook name>.json, which SpryCad.Core embeds; a problem fails the
//                                 build, said with its sheet and cell.
//   text <workbook>               the workbook as text (each sheet's cells, tab-separated), for git's diff driver
//                                 (.gitattributes: *.xlsx diff=xlsx; see docs/library-v2.md to set it up).
//   threads <workbook>            the workbook's thread tables filled in from each size's major diameter and pitch
//                                 by the standards' formulas (ThreadFormulas: Unified 2A/2B and 3A/3B for an inch
//                                 table, ISO metric 6g/6H for a metric one), a row per size and class, and the
//                                 workbook written back; run after adding a size (a row of any class).

Console.OutputEncoding = System.Text.Encoding.UTF8;

return args.Length switch
{
    >= 3 when args[0] == "gen" => Gen(args[1], args[2..]),
    2 when args[0] == "text" => Text(args[1]),
    2 when args[0] == "threads" => FillThreads(args[1]),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: SpryCad.LibraryGen gen <out-dir> <workbook>...  |  text <workbook>  |  threads <workbook>");
    return 2;
}

static int Gen(string outDir, string[] workbooks)
{
    Directory.CreateDirectory(outDir);
    int failed = 0;
    foreach (var path in workbooks)
    {
        List<LibraryTable> tables; List<string> problems;
        using (var fs = File.OpenRead(path)) (tables, problems) = LibraryWorkbook.Read(fs);
        foreach (var t in tables)
            if (Schemas.Check(t) is { } p) problems.Add(p);
        // (as MSBuild shows errors: the file, then the problem)
        foreach (var p in problems) Console.Error.WriteLine($"{Path.GetFullPath(path)} : error LIBGEN: {p}");
        if (problems.Count > 0) { failed++; continue; }
        var json = Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + ".json");
        var text = LibraryJson.Write(tables);
        // (written only when changed, so a build that changed nothing compiles nothing)
        if (!File.Exists(json) || File.ReadAllText(json) != text) File.WriteAllText(json, text);
        Console.WriteLine($"{Path.GetFileName(path)}: {tables.Count} tables, {tables.Sum(t => t.Rows.Count)} rows");
    }
    return failed == 0 ? 0 : 1;
}

static int FillThreads(string path)
{
    List<LibraryTable> tables; List<string> problems;
    using (var fs = File.OpenRead(path)) (tables, problems) = LibraryWorkbook.Read(fs);
    foreach (var p in problems) Console.Error.WriteLine($"{Path.GetFullPath(path)} : error LIBGEN: {p}");
    if (problems.Count > 0) return 1;
    var paths = Schemas.Of(TableKinds.Thread)!.Columns.Where(c => !c.IsText).Select(c => c.Path).ToList();
    foreach (var t in tables.Where(t => t.Kind == TableKinds.Thread))
    {
        var old = t.Columns.ToList();
        var classes = t.Units == TableUnits.Inch ? ThreadFormulas.UnifiedClasses : new[] { "6g" };
        // a size once (its first row), then a row for each class
        var sizes = t.Rows.GroupBy(r => r.Keys[0]).Select(g => g.First()).ToList();
        t.Rows = new List<LibraryRow>();
        foreach (var r in sizes)
        {
            double V(string c) => r.Values[old.IndexOf(c)];
            foreach (var cls in classes)
            {
                var (values, text) = t.Units == TableUnits.Inch ? ThreadFormulas.Unified(V("major"), V("pitch"), cls) : ThreadFormulas.IsoMetric(V("major"), V("pitch"));
                var row = new LibraryRow { Keys = { r.Keys[0], cls }, Values = paths.Select(c => c is "major" or "pitch" ? V(c) : values.TryGetValue(c, out var x) ? x : double.NaN).ToList() };
                row.Text = new(StringComparer.Ordinal);
                foreach (var (k, v) in text) row.Text[k] = v;
                t.Rows.Add(row);
            }
        }
        // (the columns no row fills left out)
        var keep = Enumerable.Range(0, paths.Count).Where(i => t.Rows.Any(r => double.IsFinite(r.Values[i]))).ToList();
        t.Columns = keep.Select(i => paths[i]).ToList();
        foreach (var r in t.Rows) r.Values = keep.Select(i => r.Values[i]).ToList();
        if (Schemas.Check(t) is { } problem) { Console.Error.WriteLine($"{Path.GetFullPath(path)} : error LIBGEN: {problem}"); return 1; }
        Console.WriteLine($"{t.Key}: {t.Rows.Count} rows, {t.Columns.Count} columns");
    }
    using (var fs = File.Create(path)) LibraryWorkbook.Write(tables, fs);
    return 0;
}

static int Text(string path)
{
    using var wb = new XLWorkbook(path);
    foreach (var ws in wb.Worksheets)
    {
        Console.WriteLine($"=== {ws.Name}");
        var used = ws.RangeUsed();
        if (used == null) continue;
        for (int r = 1; r <= used.LastRow().RowNumber(); r++)
        {
            var cells = Enumerable.Range(1, used.LastColumn().ColumnNumber()).Select(c => Sheets.Read(ws.Cell(r, c))).ToList();
            while (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
            if (cells.Count > 0) Console.WriteLine($"{r.ToString(CultureInfo.InvariantCulture),4}  {string.Join('\t', cells)}");
        }
    }
    return 0;
}
