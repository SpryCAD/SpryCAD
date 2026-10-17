using System.Globalization;
using ClosedXML.Excel;

namespace SpryCad.Core.Library;

/// <summary>What the workbooks share: sheet names Excel accepts, cells read as text or numbers, the problem's place.</summary>
public static class Sheets
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

    /// <summary>The cell's text as shown (a number in invariant form, so "6.6" reads back as written), trimmed; a merged cell's, its range's.</summary>
    public static string Read(IXLCell cell)
    {
        if (cell.IsMerged() && cell.MergedRange() is { } m && m.FirstCell().Address != cell.Address) cell = m.FirstCell();
        return cell.DataType switch
        {
            XLDataType.Blank => "",
            XLDataType.Number => cell.GetDouble().ToString("R", CultureInfo.InvariantCulture),
            XLDataType.Boolean => cell.GetBoolean() ? "true" : "false",
            _ => cell.GetFormattedString().Trim(),
        };
    }

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
    public static List<string> HeaderOf(IXLWorksheet ws, int row = 1)
    {
        var names = new List<string>();
        for (int c = 1; ; c++) { var s = Read(ws.Cell(row, c)); if (s.Length == 0) break; names.Add(s); }
        return names;
    }

    /// <summary>The rows after <paramref name="header"/> that have anything in their first <paramref name="width"/> cells.</summary>
    public static IEnumerable<int> Rows(IXLWorksheet ws, int width, int header = 1)
    {
        var last = ws.LastRowUsed()?.RowNumber() ?? header;
        for (int r = header + 1; r <= last; r++)
            if (Enumerable.Range(1, Math.Max(1, width)).Any(c => Read(ws.Cell(r, c)).Length > 0)) yield return r;
    }

    public static bool Yes(string s) => s.Trim().ToLowerInvariant() is "true" or "yes" or "y" or "1" or "x";

    /// <summary>"Sheet ISO273, cell C5" for a problem's place.</summary>
    public static string At(IXLWorksheet ws, int row, int col) => $"Sheet {ws.Name}, cell {XLHelper.GetColumnLetterFromNumber(col)}{row}";
}
