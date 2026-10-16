namespace SpryCad.Core.Library;

/// <summary>
/// The part families SpryCAD carries itself (section 34, layer 2; section 25): socket head cap screws, hex nuts and
/// washers, one row per designation ("M8x30", "M8"), in millimetres. Each row names its screw size in its text
/// (<see cref="Fasteners.SizeKey"/>: "M8", "1/4"), as the clearance hole tables name their rows, so a hole sized from
/// the library knows its screws. Not yet verified against the published standards (<see cref="LibraryTable.Verified"/>).
/// Keys and designations never change meaning (parts and assemblies name them).
/// </summary>
public static class FastenerFamilies
{
    /// <summary>The built-in part family library's identity (fixed: snapshots name it).</summary>
    public static readonly Guid LibraryId = new("b3e0d7a4-52c1-4f6e-8a19-6d2c7e40f5b8");
    public const string Name = "SpryCAD part families";

    static double In(double inches) => inches * 25.4;

    // (built on first use: after the size tables below are)
    static IReadOnlyList<LibraryTable>? _tables;
    public static IReadOnlyList<LibraryTable> Tables => _tables ??= Build();

    // ISO 4762 / ISO 4032 / ISO 7089 by size: thread (d, p); head (dk, k), hex socket (s, t) and reference thread length b;
    // the standard lengths each size comes in; the nut's width across flats and height; the washer's bore, outside and thickness
    static readonly (string size, double d, double p, double dk, double k, double s, double t, double b, double lmin, double lmax)[] MetricScrews =
    {
        ("M2", 2, 0.4, 3.8, 2, 1.5, 1, 16, 3, 20), ("M2.5", 2.5, 0.45, 4.5, 2.5, 2, 1.1, 17, 4, 25), ("M3", 3, 0.5, 5.5, 3, 2.5, 1.3, 18, 5, 30),
        ("M4", 4, 0.7, 7, 4, 3, 2, 20, 6, 40), ("M5", 5, 0.8, 8.5, 5, 4, 2.5, 22, 8, 50), ("M6", 6, 1, 10, 6, 5, 3, 24, 10, 60),
        ("M8", 8, 1.25, 13, 8, 6, 4, 28, 12, 80), ("M10", 10, 1.5, 16, 10, 8, 5, 32, 16, 100), ("M12", 12, 1.75, 18, 12, 10, 6, 36, 20, 120),
        ("M16", 16, 2, 24, 16, 14, 8, 44, 25, 160), ("M20", 20, 2.5, 30, 20, 17, 10, 52, 30, 200), ("M24", 24, 3, 36, 24, 19, 12, 60, 40, 200),
    };
    static readonly double[] MetricLengths = { 3, 4, 5, 6, 8, 10, 12, 16, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160, 180, 200 };
    static readonly (string size, double d, double p, double s, double m)[] MetricNuts =
    {
        ("M2", 2, 0.4, 4, 1.6), ("M2.5", 2.5, 0.45, 5, 2), ("M3", 3, 0.5, 5.5, 2.4), ("M4", 4, 0.7, 7, 3.2), ("M5", 5, 0.8, 8, 4.7),
        ("M6", 6, 1, 10, 5.2), ("M8", 8, 1.25, 13, 6.8), ("M10", 10, 1.5, 16, 8.4), ("M12", 12, 1.75, 18, 10.8), ("M16", 16, 2, 24, 14.8),
        ("M20", 20, 2.5, 30, 18), ("M24", 24, 3, 36, 21.5),
    };
    static readonly (string size, double d1, double d2, double h)[] MetricWashers =
    {
        ("M2", 2.2, 5, 0.3), ("M2.5", 2.7, 6, 0.5), ("M3", 3.2, 7, 0.5), ("M4", 4.3, 9, 0.8), ("M5", 5.3, 10, 1), ("M6", 6.4, 12, 1.6),
        ("M8", 8.4, 16, 1.6), ("M10", 10.5, 20, 2), ("M12", 13, 24, 2.5), ("M16", 17, 30, 3), ("M20", 21, 37, 3), ("M24", 25, 44, 4),
    };
    // ASME B18.3 (inches): size, threads per inch, major diameter, head diameter and height, hex key and its engagement, lengths
    static readonly (string size, int tpi, double d, double dk, double k, double s, double t, double lmin, double lmax)[] InchScrews =
    {
        ("#4", 40, 0.112, 0.183, 0.112, 0.09375, 0.051, 0.25, 1), ("#6", 32, 0.138, 0.226, 0.138, 0.109375, 0.067, 0.25, 1.5),
        ("#8", 32, 0.164, 0.270, 0.164, 0.140625, 0.080, 0.25, 2), ("#10", 24, 0.190, 0.312, 0.190, 0.15625, 0.093, 0.375, 2.5),
        ("1/4", 20, 0.25, 0.375, 0.25, 0.1875, 0.120, 0.375, 3), ("5/16", 18, 0.3125, 0.469, 0.3125, 0.25, 0.151, 0.5, 3),
        ("3/8", 16, 0.375, 0.562, 0.375, 0.3125, 0.183, 0.5, 4), ("1/2", 13, 0.5, 0.750, 0.5, 0.375, 0.245, 0.75, 4),
        ("5/8", 11, 0.625, 0.938, 0.625, 0.5, 0.307, 1, 4), ("3/4", 10, 0.75, 1.125, 0.75, 0.625, 0.370, 1, 4),
    };
    static readonly (string name, double inches)[] InchLengths =
    {
        ("1/4", 0.25), ("3/8", 0.375), ("1/2", 0.5), ("5/8", 0.625), ("3/4", 0.75), ("7/8", 0.875), ("1", 1), ("1-1/4", 1.25), ("1-1/2", 1.5),
        ("1-3/4", 1.75), ("2", 2), ("2-1/4", 2.25), ("2-1/2", 2.5), ("3", 3), ("3-1/2", 3.5), ("4", 4),
    };

    static SortedDictionary<string, string> Size(string size) => new(StringComparer.Ordinal) { [Fasteners.SizeKey] = size };

    static List<LibraryTable> Build()
    {
        var t = new List<LibraryTable>();
        t.Add(new LibraryTable
        {
            Key = "ISO4762", Title = "Socket head cap screws (metric)", Kind = TableKinds.Screw, Standard = "ISO 4762",
            Columns = Fasteners.ScrewColumns.ToList(),
            Rows = MetricScrews.SelectMany(s => MetricLengths.Where(l => l >= s.lmin && l <= s.lmax).Select(l =>
                new LibraryRow($"{s.size}x{Expr.Format(l)}", s.d, s.p, s.dk, s.k, s.s, s.t, Math.Min(l, s.b), l) { Text = Size(s.size) })).ToList(),
        });
        t.Add(new LibraryTable
        {
            Key = "ASME-B18.3-SHCS", Title = "Socket head cap screws (inch)", Kind = TableKinds.Screw, Standard = "ASME B18.3",
            Columns = Fasteners.ScrewColumns.ToList(),
            // (thread length 2D + 1/2", the whole screw when shorter)
            Rows = InchScrews.SelectMany(s => InchLengths.Where(l => l.inches >= s.lmin && l.inches <= s.lmax).Select(l =>
                new LibraryRow($"{s.size}-{s.tpi} x {l.name}", In(s.d), 25.4 / s.tpi, In(s.dk), In(s.k), In(s.s), In(s.t), In(Math.Min(l.inches, 2 * s.d + 0.5)), In(l.inches))
                { Text = Size(s.size) })).ToList(),
        });
        t.Add(new LibraryTable
        {
            Key = "ISO4032", Title = "Hex nuts (metric)", Kind = TableKinds.Nut, Standard = "ISO 4032",
            Columns = Fasteners.NutColumns.ToList(),
            Rows = MetricNuts.Select(n => new LibraryRow(n.size, n.d, n.p, n.s, n.m) { Text = Size(n.size) }).ToList(),
        });
        t.Add(new LibraryTable
        {
            Key = "ISO7089", Title = "Plain washers, normal series (metric)", Kind = TableKinds.Washer, Standard = "ISO 7089",
            Columns = Fasteners.WasherColumns.ToList(),
            Rows = MetricWashers.Select(w => new LibraryRow(w.size, w.d1, w.d2, w.h) { Text = Size(w.size) }).ToList(),
        });
        return t;
    }

    /// <summary>The built-in families as a loaded library (above the built-in standards in every <see cref="LibrarySet"/>).</summary>
    public static LoadedLibrary AsLibrary() => new(LibraryId, Name, LibraryLayer.PartFamilies, LibraryFile.ContentRevision(Tables, null), null, Tables, Array.Empty<LibraryRestriction>());
}
