using SpryCad.Core.Library;
using SpryCad.Core.Model;

namespace SpryCad.Core.Regen;

/// <summary>
/// What a hole or a thread is, as a drawing's callout says it (ASME Y14.5 symbols): "⌀6.6 THRU ALL" over
/// "⌴ ⌀11 ↧ 6.5"; "⌀6.75 ↧ 15" over "M8-6H ↧ 12"; "1/4-20 UNC-2A ↧ 12". Lengths in the part's units, without the unit.
/// </summary>
public static class Callouts
{
    public const string Diameter = "⌀", Depth = "↧", Counterbore = "⌴", Countersink = "⌵";

    /// <summary>
    /// A thread row's designation and class as a callout gives them: an external thread's class its row's (2A), an
    /// internal one's the internal class that goes with it (2B); its display designation where the row has one.
    /// </summary>
    public static string ThreadName(LibrarySnapshot s, bool external)
    {
        var keys = LibraryRow.SplitKeys(s.Row);
        var designation = s.Text?.GetValueOrDefault("display") is { Length: > 0 } d ? d : keys[0];
        var rowClass = keys.Count > 1 ? keys[1] : "";
        var cls = external ? rowClass : s.Text?.GetValueOrDefault("internal.class") is { Length: > 0 } ic ? ic : rowClass;
        return cls.Length > 0 ? $"{designation}-{cls}" : designation;
    }

    /// <summary>A Thread feature's callout: its size and class, and its length where it is blind.</summary>
    public static string Thread(UnitSystem u, LibrarySnapshot? row, double major, double pitch, bool external, double? length)
    {
        var name = row != null ? ThreadName(row, external) : $"{Diameter}{u.Number(2 * major)} × P{u.Number(pitch)}";
        return length is { } l ? $"{name} {Depth} {u.Number(l)}" : name;
    }

    /// <summary>A hole feature's callout (as it was made: its library values in place), a line each for its parts.</summary>
    public static string Hole(UnitSystem u, HoleFeature h, int count, double diameter, double? depth, Func<string, double> len, Func<string, double> ang,
        LibrarySnapshot? thread, double? threadDepth)
    {
        string N(double mm) => u.Number(mm);
        static string A(double deg) => Expr.Format(Math.Round(deg, 3)) + "°";
        var lines = new List<string>();
        var times = count > 1 ? $"{count}X " : "";
        string Deep(double? d) => h.EndKind switch
        {
            HoleEnd.ThroughAll => " THRU ALL",
            HoleEnd.UpToNext => " THRU",
            _ => d is { } x ? $" {Depth} {N(x)}" : "",
        };
        if (h.LibraryType != null) { lines.Add($"{times}{h.LibraryType} {h.Size}"); return string.Join("\n", lines); }
        lines.Add($"{times}{Diameter}{N(diameter)}{Deep(depth)}");
        switch (h.Type)
        {
            case HoleType.Counterbore when h.Profile == null:
                lines.Add($"{Counterbore} {Diameter}{N(len(h.CbDiameter))} {Depth} {N(len(h.CbDepth))}");
                break;
            case HoleType.Countersink when h.Profile == null:
                lines.Add($"{Countersink} {Diameter}{N(len(h.CsDiameter))} X {A(ang(h.CsAngle))}");
                break;
            case HoleType.Tapped when thread != null:
                // (threaded to its thread depth; else as deep as the hole goes)
                lines.Add(ThreadName(thread, external: false) + (threadDepth is { } td ? $" {Depth} {N(td)}" : Deep(depth)));
                break;
        }
        if (h.NearCountersink && h.Profile == null && h.Type != HoleType.Countersink)
            lines.Add($"{Countersink} {Diameter}{N(len(h.NearCsDiameter))} X {A(ang(h.NearCsAngle))}");
        return string.Join("\n", lines);
    }
}
