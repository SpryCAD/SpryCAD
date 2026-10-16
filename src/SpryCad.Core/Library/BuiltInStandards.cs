namespace SpryCad.Core.Library;

/// <summary>
/// The standards data SpryCAD carries itself (section 34, layer 1): what a part falls back to when no library is
/// installed or configured, and what <c>standards.sprlib</c> is first written from. Every table is marked not yet
/// verified against its published standard (<see cref="LibraryTable.Verified"/>): the values are from secondary
/// sources until checked. Keys and row designations never change meaning (parts name them).
/// </summary>
public static class BuiltInStandards
{
    /// <summary>The built-in library's identity (fixed: parts' snapshots name it).</summary>
    public static readonly Guid LibraryId = new("6f1d3c52-8a4e-4b7f-9d21-0c5e8b7a2f14");
    public const string Name = "SpryCAD standards";

    static double In(double inches) => inches * 25.4;

    public static readonly IReadOnlyList<LibraryTable> Tables = Build();

    static List<LibraryTable> Build()
    {
        var t = new List<LibraryTable>();

        // ---- threads
        t.Add(new LibraryTable
        {
            Key = "ISO261-coarse", Title = "ISO metric, coarse pitch", Kind = TableKinds.Thread, Standard = "ISO 261 / ISO 262",
            Columns = { "major", "pitch" },
            Rows = new[]
            {
                ("M1.6", 1.6, 0.35), ("M2", 2, 0.4), ("M2.5", 2.5, 0.45), ("M3", 3, 0.5), ("M4", 4, 0.7), ("M5", 5, 0.8),
                ("M6", 6, 1.0), ("M8", 8, 1.25), ("M10", 10, 1.5), ("M12", 12, 1.75), ("M14", 14, 2), ("M16", 16, 2),
                ("M18", 18, 2.5), ("M20", 20, 2.5), ("M22", 22, 2.5), ("M24", 24, 3), ("M27", 27, 3), ("M30", 30, 3.5),
                ("M33", 33, 3.5), ("M36", 36, 4), ("M42", 42, 4.5), ("M48", 48, 5),
            }.Select(r => new LibraryRow(r.Item1, r.Item2, r.Item3)).ToList(),
        });
        t.Add(new LibraryTable
        {
            Key = "ISO261-fine", Title = "ISO metric, fine pitch", Kind = TableKinds.Thread, Standard = "ISO 261 / ISO 262",
            Columns = { "major", "pitch" },
            Rows = new[]
            {
                (8, 1.0), (10, 1.25), (10, 1.0), (12, 1.5), (12, 1.25), (14, 1.5), (16, 1.5), (18, 1.5), (20, 1.5),
                (22, 1.5), (24, 2.0), (27, 2.0), (30, 2.0),
            }.Select(r => new LibraryRow($"M{r.Item1}x{Expr.Format(r.Item2)}", r.Item1, r.Item2)).ToList(),
        });
        t.Add(new LibraryTable
        {
            Key = "ASME-B1.1-UNC", Title = "Unified inch, coarse (UNC)", Kind = TableKinds.Thread, Standard = "ASME B1.1", Units = LibraryTable.Inch,
            Columns = { "major", "pitch" },
            Rows = Unified("UNC", ("#2", 0.086, 56), ("#4", 0.112, 40), ("#6", 0.138, 32), ("#8", 0.164, 32), ("#10", 0.190, 24),
                ("#12", 0.216, 24), ("1/4", 0.25, 20), ("5/16", 0.3125, 18), ("3/8", 0.375, 16), ("7/16", 0.4375, 14), ("1/2", 0.5, 13),
                ("9/16", 0.5625, 12), ("5/8", 0.625, 11), ("3/4", 0.75, 10), ("7/8", 0.875, 9), ("1", 1.0, 8)),
        });
        t.Add(new LibraryTable
        {
            Key = "ASME-B1.1-UNF", Title = "Unified inch, fine (UNF)", Kind = TableKinds.Thread, Standard = "ASME B1.1", Units = LibraryTable.Inch,
            Columns = { "major", "pitch" },
            Rows = Unified("UNF", ("#2", 0.086, 64), ("#4", 0.112, 48), ("#6", 0.138, 40), ("#8", 0.164, 36), ("#10", 0.190, 32),
                ("#12", 0.216, 28), ("1/4", 0.25, 28), ("5/16", 0.3125, 24), ("3/8", 0.375, 24), ("7/16", 0.4375, 20), ("1/2", 0.5, 20),
                ("9/16", 0.5625, 18), ("5/8", 0.625, 18), ("3/4", 0.75, 16), ("7/8", 0.875, 14), ("1", 1.0, 12)),
        });

        // ---- holes for screws
        t.Add(new LibraryTable
        {
            Key = "ISO273", Title = "Metric clearance holes", Kind = TableKinds.Clearance, Standard = "ISO 273",
            Columns = { "close", "normal", "loose" },
            Rows = new[]
            {
                ("M1.6", 1.7, 1.8, 2.0), ("M2", 2.2, 2.4, 2.6), ("M2.5", 2.7, 2.9, 3.1), ("M3", 3.2, 3.4, 3.6), ("M4", 4.3, 4.5, 4.8),
                ("M5", 5.3, 5.5, 5.8), ("M6", 6.4, 6.6, 7.0), ("M8", 8.4, 9.0, 10), ("M10", 10.5, 11, 12), ("M12", 13, 13.5, 14.5),
                ("M14", 15, 15.5, 16.5), ("M16", 17, 17.5, 18.5), ("M18", 19, 20, 21), ("M20", 21, 22, 24), ("M22", 23, 24, 26),
                ("M24", 25, 26, 28), ("M27", 28, 30, 32), ("M30", 31, 33, 35), ("M33", 34, 36, 38), ("M36", 37, 39, 42),
                ("M42", 43, 45, 48), ("M48", 50, 52, 56),
            }.Select(r => new LibraryRow(r.Item1, r.Item2, r.Item3, r.Item4)).ToList(),
        });
        t.Add(new LibraryTable
        {
            Key = "ASME-B18.2.8", Title = "Inch clearance holes", Kind = TableKinds.Clearance, Standard = "ASME B18.2.8", Units = LibraryTable.Inch,
            Columns = { "close", "normal" },
            Rows = new[]
            {
                ("#2", 0.0935, 0.096), ("#4", 0.116, 0.1285), ("#6", 0.144, 0.1495), ("#8", 0.1695, 0.177), ("#10", 0.196, 0.201),
                ("1/4", 0.257, 0.266), ("5/16", 0.323, 0.332), ("3/8", 0.386, 0.397), ("7/16", 0.453, 0.469), ("1/2", 0.516, 0.531),
                ("5/8", 0.641, 0.656), ("3/4", 0.766, 0.781), ("1", 1.016, 1.031),
            }.Select(r => new LibraryRow(r.Item1, In(r.Item2), In(r.Item3))).ToList(),
        });
        t.Add(new LibraryTable
        {
            Key = "ISO4762-cbore", Title = "Counterbores for socket head cap screws (metric)", Kind = TableKinds.Counterbore, Standard = "ISO 4762 / DIN 974-1",
            Columns = { "diameter", "depth" },
            Rows = new[]
            {
                ("M3", 6.5, 3.3), ("M4", 8.0, 4.4), ("M5", 10.0, 5.4), ("M6", 11.0, 6.5), ("M8", 15.0, 8.6), ("M10", 18.0, 10.8),
                ("M12", 20.0, 13.0), ("M16", 26.0, 17.5), ("M20", 33.0, 21.5), ("M24", 40.0, 25.5),
            }.Select(r => new LibraryRow(r.Item1, r.Item2, r.Item3)).ToList(),
        });
        t.Add(new LibraryTable
        {
            Key = "ASME-B18.3-cbore", Title = "Counterbores for socket head cap screws (inch)", Kind = TableKinds.Counterbore, Standard = "ASME B18.3", Units = LibraryTable.Inch,
            Columns = { "diameter", "depth" },
            Rows = new[]
            {
                ("#4", 0.21875, 0.112), ("#6", 0.28125, 0.138), ("#8", 0.3125, 0.164), ("#10", 0.375, 0.190), ("1/4", 0.4375, 0.25),
                ("5/16", 0.53125, 0.3125), ("3/8", 0.625, 0.375), ("1/2", 0.8125, 0.5), ("5/8", 1.0, 0.625), ("3/4", 1.1875, 0.75),
            }.Select(r => new LibraryRow(r.Item1, In(r.Item2), In(r.Item3))).ToList(),
        });
        t.Add(new LibraryTable
        {
            Key = "ISO10642-csink", Title = "Countersinks for flat head screws (metric)", Kind = TableKinds.Countersink, Standard = "ISO 10642",
            Columns = { "diameter", "angle" },
            Rows = new[]
            {
                ("M3", 6.6), ("M4", 8.6), ("M5", 10.4), ("M6", 12.4), ("M8", 16.4), ("M10", 20.4), ("M12", 24.4),
                ("M14", 28.4), ("M16", 32.4), ("M20", 40.4), // (ISO 10642 has no M18)
            }.Select(r => new LibraryRow(r.Item1, r.Item2, 90)).ToList(),
        });
        t.Add(new LibraryTable
        {
            Key = "ASME-B18.6.3-csink", Title = "Countersinks for flat head screws (inch)", Kind = TableKinds.Countersink, Standard = "ASME B18.6.3", Units = LibraryTable.Inch,
            Columns = { "diameter", "angle" },
            Rows = new[]
            {
                ("#4", 0.225), ("#6", 0.279), ("#8", 0.332), ("#10", 0.385), ("1/4", 0.507), ("5/16", 0.635), ("3/8", 0.762),
            }.Select(r => new LibraryRow(r.Item1, In(r.Item2), 82)).ToList(),
        });
        return t;
    }

    static List<LibraryRow> Unified(string series, params (string size, double major, int tpi)[] rows) =>
        rows.Select(r => new LibraryRow($"{r.size}-{r.tpi} {series}", In(r.major), 25.4 / r.tpi)).ToList();

    /// <summary>The built-in data as a loaded library (the bottom layer of every <see cref="LibrarySet"/>).</summary>
    public static LoadedLibrary AsLibrary() => new(LibraryId, Name, LibraryLayer.Standards, LibraryFile.ContentRevision(Tables, null), null, Tables, Array.Empty<LibraryRestriction>());
}
