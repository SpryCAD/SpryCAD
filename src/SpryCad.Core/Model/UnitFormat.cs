using System.Globalization;

namespace SpryCad.Core.Model;

/// <summary>What a typed value is: a plain number (a count, a ratio), a length or an angle.</summary>
public enum ValueKind { Number, Length, Angle }

/// <summary>
/// A part's units (<see cref="CadDocument.Units"/>) are for showing and typing values only: lengths are kept in millimetres
/// and angles in degrees whatever they are. A value typed with no unit takes the part's (<see cref="TryEnter"/>: "10" in an
/// inch part is kept as "10in"), so every value says what it is and units mix freely ("10in" in a millimetre part);
/// values worked out (a measurement, a dragged dimension) are shown in the part's units.
/// </summary>
public static class UnitFormat
{
    public static string Symbol(this UnitSystem u) => u == UnitSystem.IPS ? "in" : "mm";

    /// <summary>Millimetres per one of the units' length.</summary>
    public static double MmPer(this UnitSystem u) => u == UnitSystem.IPS ? 25.4 : 1;

    static string Fmt(double v, int decimals)
    {
        v = Math.Round(v, decimals);
        if (Math.Abs(v) < 1e-12) v = 0;
        return v.ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture);
    }

    // (an inch wants a decimal more than a millimetre for the same precision)
    static int Decimals(UnitSystem u) => u == UnitSystem.IPS ? 5 : 4;

    /// <summary>A length (millimetres) as a number in the units: "12.7", "0.5".</summary>
    public static string Number(this UnitSystem u, double mm) => Fmt(mm / u.MmPer(), Decimals(u));

    /// <summary>A length (millimetres) shown in the units: "12.7 mm", "0.5 in".</summary>
    public static string Length(this UnitSystem u, double mm) => $"{u.Number(mm)} {u.Symbol()}";

    /// <summary>An area (mm²) in the units: "645.16 mm²", "1 in²".</summary>
    public static string Area(this UnitSystem u, double mm2) => $"{Fmt(mm2 / (u.MmPer() * u.MmPer()), Decimals(u))} {u.Symbol()}²";

    /// <summary>A volume (mm³) in the units.</summary>
    public static string Volume(this UnitSystem u, double mm3) => $"{Fmt(mm3 / Math.Pow(u.MmPer(), 3), Decimals(u))} {u.Symbol()}³";

    /// <summary>A value with its units shown in the units: "0.5 in", "45°", "6".</summary>
    public static string Show(this UnitSystem u, Quantity q) =>
        q.IsNumber ? Expr.Format(q.Value) : q.IsLength ? u.Length(q.Value) : q.IsAngle ? Angle(q.Value)
        : q.Angle == 0 && q.Length == 2 ? u.Area(q.Value) : q.Angle == 0 && q.Length == 3 ? u.Volume(q.Value) : q.ToString();

    /// <summary>An angle (degrees) shown: "45°".</summary>
    public static string Angle(double deg) => Expr.Format(deg) + "°";

    /// <summary>The text a field is given for a value worked out: a length in the units with its unit ("12.7mm", "0.5in"), an angle in degrees ("45deg").</summary>
    public static string Entry(this UnitSystem u, double v, ValueKind kind = ValueKind.Length) => kind switch
    {
        ValueKind.Length => u.Number(v) + u.Symbol(),
        ValueKind.Angle => Expr.Format(v) + "deg",
        _ => Expr.Format(v),
    };

    /// <summary>
    /// A kept value as a field shows it: as it is, but a bare number for a length or an angle (kept in millimetres or
    /// degrees, as files written by programs may hold) with its unit added.
    /// </summary>
    public static string Show(string? kept, ValueKind kind)
    {
        kept = kept?.Trim() ?? "";
        if (!Expr.IsPlainNumber(kept)) return kept;
        return kind switch { ValueKind.Length => kept + "mm", ValueKind.Angle => kept + "deg", _ => kept };
    }

    /// <summary>
    /// A typed value as it is kept: a length or an angle typed with no unit gets the part's ("10" → "10in", "W/2" → "(W/2)in";
    /// angles "deg"), so it means the same whatever the part's units become. False, with why, when it does not evaluate or
    /// is the wrong kind (an area for a length, a length for a count). <paramref name="value"/> is in millimetres or degrees.
    /// </summary>
    public static bool TryEnter(this UnitSystem u, string? typed, ValueKind kind, Func<string, Quantity?>? lookup,
        out string kept, out Quantity value, out string? error)
    {
        kept = typed?.Trim() ?? "";
        if (!Expr.TryEvaluate(kept, lookup, out value, out error)) return false;
        if (kind == ValueKind.Number)
        {
            if (value.IsNumber) return true;
            error = $"Expected a number without units, not {Expr.Kind(value)}";
            return false;
        }
        if (kind == ValueKind.Length ? value.IsLength : value.IsAngle) return true;
        if (!value.IsNumber)
        {
            error = $"Expected {(kind == ValueKind.Length ? "a length" : "an angle")}, not {Expr.Kind(value)}";
            return false;
        }
        var unit = kind == ValueKind.Length ? u.Symbol() : "deg";
        kept = Expr.IsPlainNumber(kept) ? kept + unit : $"({kept}){unit}";
        value = kind == ValueKind.Length ? Quantity.Mm(value.Value * u.MmPer()) : Quantity.Deg(value.Value);
        return true;
    }

    /// <summary>A kept length's value in millimetres, as features are built (a bare number is millimetres).</summary>
    public static bool TryLength(string? kept, Func<string, Quantity?>? lookup, out double mm, out string? error)
    {
        mm = 0; error = null;
        if (string.IsNullOrWhiteSpace(kept)) { error = "Empty value"; return false; }
        try { mm = Expr.Length(kept, lookup); return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>
    /// A dimension's value as a label shows it: a number in the part's units, or (when typed so) the number in its own
    /// units ("10in" in a millimetre part).
    /// </summary>
    public static string Label(this UnitSystem u, double mm, string? kept)
    {
        if (Expr.IsLiteral(kept, out var unit) && unit is not ("" or "deg" or "°" or "rad")
            && unit != u.Symbol() && !(unit == "\"" && u == UnitSystem.IPS))
            return Fmt(mm / Expr.UnitFactor(unit), 5) + unit;
        return u.Number(mm);
    }
}
