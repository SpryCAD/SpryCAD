using System.Globalization;
using ClosedXML.Excel;

namespace SpryCad.Core.Library;

/// <summary>
/// Autodesk Inventor's thread and clearance spreadsheets (Design Data\XLS: thread.xlsx, clearance.xlsx) read as
/// library tables (docs/library-v2.md, "Inventor import"), for a company that has them to take into its own library:
/// <list type="bullet">
/// <item>A straight thread sheet (Size, Thread Designation, Pitch or TPI, the external and internal classes and their
/// limits, Tap Drill) is a thread table, a row per designation and class, as the sheet has them.</item>
/// <item>A taper thread sheet (NPT, BSPT, ISO 7) is a taper-thread table.</item>
/// <item>A clearance sheet (Fastener Type, Size, the shaft diameters, the head's diameter and depth or angle, its head
/// type) is a fastener holes table; the fastener families that go in its holes are left for the library's owner.</item>
/// </list>
/// Units come from the sheet (inch or mm), a thread's TPI read as its pitch. Rows a table could not take (a designation
/// listed twice) are said as notes; a sheet of another layout is left out, said too.
/// </summary>
public static class InventorWorkbook
{
    public sealed record Result(List<LibraryTable> Tables, List<string> Notes);

    public static Result Read(Stream stream)
    {
        using var wb = new XLWorkbook(stream);
        var tables = new List<LibraryTable>();
        var notes = new List<string>();
        foreach (var ws in wb.Worksheets)
        {
            var row2 = Sheets.Read(ws.Cell(2, 1));
            var row3 = Sheets.Read(ws.Cell(3, 3));
            int before = tables.Count;
            if (row3 == "Thread Designation") Straight(ws, tables, notes);
            else if (row2.StartsWith("Thread Designation", StringComparison.Ordinal)) Taper(ws, tables, notes);
            else if (row2 == "Fastener Type") Clearance(ws, tables, notes);
            else { notes.Add($"Sheet {ws.Name}: not a thread or clearance sheet; left out."); continue; }
            foreach (var t in tables.Skip(before).ToList())
                if (Schemas.Check(t) is { } problem) { tables.Remove(t); notes.Add($"Sheet {ws.Name}: {problem} Left out."); }
        }
        return new Result(tables, notes);
    }

    /// <summary>A table key for a sheet ("ISO Metric profile" → "ISO-Metric-profile"), with a suffix.</summary>
    static string Key(string sheet, string suffix = "")
    {
        var chars = sheet.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' ? c : '-').ToArray();
        var key = string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return suffix.Length > 0 ? $"{key}-{suffix}" : key;
    }

    // the sheet's units: the last heading cell of rows 1-2 says ("Inch", "mm", "inch-taper", "mm-taper"); else the
    // first cell's code ("I Q", "M P")
    static TableUnits Units(IXLWorksheet ws)
    {
        var last = ws.LastColumnUsed()?.ColumnNumber() ?? 1;
        foreach (var r in new[] { 1, 2 })
        {
            var s = Sheets.Read(ws.Cell(r, last)).ToLowerInvariant();
            if (s.StartsWith("inch", StringComparison.Ordinal)) return TableUnits.Inch;
            if (s.StartsWith("mm", StringComparison.Ordinal)) return TableUnits.Metric;
        }
        return Sheets.Read(ws.Cell(1, 1)).StartsWith('I') ? TableUnits.Inch : TableUnits.Metric;
    }

    static double Num(IXLCell c) => Sheets.Number(c) ?? double.NaN;

    static void Straight(IXLWorksheet ws, List<LibraryTable> tables, List<string> notes)
    {
        var units = Units(ws);
        var last = ws.LastColumnUsed()?.ColumnNumber() ?? 1;
        // the blocks by their headings: the two Class columns, then Tap Drill
        var classCols = Enumerable.Range(1, last).Where(c => Sheets.Read(ws.Cell(3, c)) == "Class").ToList();
        if (classCols.Count < 2) { notes.Add($"Sheet {ws.Name}: no external and internal Class columns; left out."); return; }
        int ext = classCols[0], inn = classCols[1];
        int tap = Enumerable.Range(1, last).FirstOrDefault(c => Sheets.Read(ws.Cell(2, c)) == "Tap Drill");
        bool tpi = Sheets.Read(ws.Cell(3, 5)) == "TPI";
        var columns = new (string path, int col)[]
        {
            ("external.major.max", ext + 1), ("external.major.min", ext + 2), ("external.pitch.max", ext + 3),
            ("external.pitch.min", ext + 4), ("external.minor.max", ext + 5), ("external.minor.min", ext + 6),
            ("internal.minor.min", inn + 1), ("internal.minor.max", inn + 2), ("internal.pitch.min", inn + 3),
            ("internal.pitch.max", inn + 4), ("internal.major.min", inn + 5), ("internal.major.max", inn + 6),
        };
        var title = Sheets.Read(ws.Cell(1, 2));
        var t = new LibraryTable
        {
            Key = Key(ws.Name), Title = ws.Name, Kind = TableKinds.Thread, Standard = title, Units = units,
            Columns = new[] { "major", "pitch" }.Concat(columns.Select(c => c.path)).Append("tap.diameter").ToList(),
        };
        tables.Add(t);
        int dropped = 0, noSize = 0, noClass = 0;
        for (int r = 4; r <= (ws.LastRowUsed()?.RowNumber() ?? 3); r++)
        {
            var des = Sheets.Read(ws.Cell(r, 3));
            if (des.Length == 0) continue;
            var extClass = Sheets.Read(ws.Cell(r, ext));
            if (extClass.Length == 0) { noClass++; continue; }
            var key = LibraryRow.JoinKeys(new[] { des, extClass });
            if (t.Rows.Any(x => x.Key == key)) { dropped++; continue; }
            double major = Num(ws.Cell(r, 1)), p = Num(ws.Cell(r, 5));
            // (a TPI as the pitch in the table's units)
            if (tpi) p = p > 0 ? (units == TableUnits.Inch ? 1 : 25.4) / p : double.NaN;
            // (a size given as text, a pipe thread's "1/8": the major diameter its limits give)
            if (!double.IsFinite(major)) major = new[] { Num(ws.Cell(r, inn + 5)), Num(ws.Cell(r, ext + 1)) }.FirstOrDefault(double.IsFinite, double.NaN);
            if (!double.IsFinite(major) || !(p > 0)) { noSize++; continue; }
            var row = new LibraryRow { Keys = { des, extClass }, Values = { major, p } };
            row.Values.AddRange(columns.Select(c => Num(ws.Cell(r, c.col))));
            var tapText = tap > 0 ? Sheets.Read(ws.Cell(r, tap)) : "";
            var tapD = tap > 0 ? Num(ws.Cell(r, tap)) : double.NaN;
            row.Values.Add(tapD);
            var text = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (Sheets.Read(ws.Cell(r, inn)) is { Length: > 0 } ic) text["internal.class"] = ic;
            if (double.IsNaN(tapD) && tapText.Length > 0) text["tap.size"] = tapText;
            if (Sheets.Read(ws.Cell(r, 4)) is { Length: > 0 } custom && custom != des) text["display"] = custom;
            row.Text = text.Count > 0 ? text : null;
            t.Rows.Add(row);
        }
        Trim(t);
        if (dropped > 0) notes.Add($"Sheet {ws.Name}: {dropped} rows listed a designation and class twice; the first kept.");
        if (noClass > 0) notes.Add($"Sheet {ws.Name}: {noClass} rows without a class; left out.");
        if (noSize > 0) notes.Add($"Sheet {ws.Name}: {noSize} rows without a major diameter or pitch; left out.");
    }

    static void Taper(IXLWorksheet ws, List<LibraryTable> tables, List<string> notes)
    {
        var units = Units(ws);
        // the columns as Inventor's taper sheets have them
        var columns = new (string path, int col)[]
        {
            ("od", 3), ("pitch", 5), ("e0", 6), ("l1", 7), ("e1", 8), ("l2", 9), ("e2", 10), ("l3", 11), ("e3", 12),
            ("vanish", 13), ("l4", 14), ("l5", 15), ("e5", 16), ("height", 17), ("minor", 18), ("tap.diameter", 19),
            ("taper", 20), ("useful", 21),
        };
        var t = new LibraryTable
        {
            Key = Key(ws.Name), Title = ws.Name, Kind = TableKinds.TaperThread, Standard = Sheets.Read(ws.Cell(1, 2)), Units = units,
            Columns = columns.Select(c => c.path).ToList(),
        };
        int dropped = 0;
        for (int r = 4; r <= (ws.LastRowUsed()?.RowNumber() ?? 3); r++)
        {
            var des = Sheets.Read(ws.Cell(r, 1));
            if (des.Length == 0) continue;
            if (t.Rows.Any(x => x.Key == des)) { dropped++; continue; }
            var row = new LibraryRow { Keys = { des }, Values = columns.Select(c => Num(ws.Cell(r, c.col))).ToList() };
            // (a pitch not given: from the TPI)
            if (double.IsNaN(row.Values[1]) && Num(ws.Cell(r, 4)) is var n && n > 0) row.Values[1] = units == TableUnits.Inch ? 1 / n : 25.4 / n;
            if (Sheets.Read(ws.Cell(r, 2)) is { Length: > 0 } custom && custom != des) row.Text = new(StringComparer.Ordinal) { ["display"] = custom };
            t.Rows.Add(row);
        }
        Trim(t);
        tables.Add(t);
        if (dropped > 0) notes.Add($"Sheet {ws.Name}: {dropped} rows listed a designation twice; the first kept.");
    }

    static void Clearance(IXLWorksheet ws, List<LibraryTable> tables, List<string> notes)
    {
        var t = new LibraryTable
        {
            Key = Key(ws.Name, "holes"), Title = $"{ws.Name} holes", Kind = TableKinds.FastenerHoles, Standard = ws.Name,
            Units = Sheets.Read(ws.Cell(1, 1)).StartsWith('I') ? TableUnits.Inch : TableUnits.Metric,
            Columns = { "close", "normal", "loose", "head.diameter", "head.depth", "head.angle" },
        };
        int dropped = 0;
        for (int r = 4; r <= (ws.LastRowUsed()?.RowNumber() ?? 3); r++)
        {
            var fastener = Sheets.Read(ws.Cell(r, 1)); var size = Sheets.Read(ws.Cell(r, 2));
            if (fastener.Length == 0 || size.Length == 0) continue;
            var key = LibraryRow.JoinKeys(new[] { fastener, size });
            if (t.Rows.Any(x => x.Key == key)) { dropped++; continue; }
            // head type: 1 a counterbore (its depth), 2 a countersink (its angle), else none
            var head = Sheets.Read(ws.Cell(r, 8)) switch { "1" => "counterbore", "2" => "countersink", _ => null };
            double hd = Num(ws.Cell(r, 6)), da = Num(ws.Cell(r, 7));
            var row = new LibraryRow
            {
                Keys = { fastener, size },
                Values =
                {
                    Num(ws.Cell(r, 3)), Num(ws.Cell(r, 4)), Num(ws.Cell(r, 5)),
                    head != null ? hd : double.NaN, head == "counterbore" ? da : double.NaN, head == "countersink" ? da : double.NaN,
                },
            };
            if (head != null) row.Text = new(StringComparer.Ordinal) { ["head"] = head };
            t.Rows.Add(row);
        }
        Trim(t);
        tables.Add(t);
        if (dropped > 0) notes.Add($"Sheet {ws.Name}: {dropped} rows listed a fastener and size twice; the first kept.");
    }

    // the columns no row gives a value left out
    static void Trim(LibraryTable t)
    {
        var keep = Enumerable.Range(0, t.Columns.Count).Where(i => t.Rows.Any(r => double.IsFinite(r.Values[i]))
            || Schemas.Of(t.Kind)?.Column(t.Columns[i])?.Required == true).ToList();
        t.Columns = keep.Select(i => t.Columns[i]).ToList();
        foreach (var r in t.Rows) r.Values = keep.Select(i => r.Values[i]).ToList();
    }
}
