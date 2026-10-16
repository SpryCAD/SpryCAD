namespace SpryCad.Core.Model;

/// <summary>
/// The values a new feature (or command) starts with, round in the part's units: a fillet 2 mm in a millimetre part
/// and 0.125 in in an inch part, not 0.0787 in (2 mm shown in inches) nor 2 in (the number taken as inches). Kept with
/// their unit, as a typed value is (<see cref="UnitFormat.TryEnter"/>), so they mean the same if the units change.
/// The feature classes' own initial values stay as they are (millimetres): files that leave a value out read as before.
/// </summary>
public static class FeatureDefaults
{
    /// <summary>A default length: <paramref name="mm"/> in a millimetre part, <paramref name="inch"/> in an inch part.</summary>
    public static string Length(UnitSystem u, double mm, double inch) =>
        u == UnitSystem.IPS ? Expr.Format(inch) + "in" : Expr.Format(mm) + "mm";

    /// <summary>The same, in millimetres (for a value a command keeps as a number).</summary>
    public static double Mm(UnitSystem u, double mm, double inch) => u == UnitSystem.IPS ? inch * 25.4 : mm;

    /// <summary>A feature just made, its lengths given in the part's units; the feature.</summary>
    public static T Sized<T>(this T f, UnitSystem u) where T : Feature
    {
        string L(double mm, double inch) => Length(u, mm, inch);
        switch (f)
        {
            case ExtrudeFeature e: e.Depth = L(10, 0.5); break;
            case HelicalSweepFeature h: h.Pitch = L(5, 0.25); break;
            case ThreadFeature t: t.Pitch = L(1.5, 0.0625); break; // (a custom thread's: 16 per inch)
            case FilletFeature fi: fi.Radius = L(2, 0.125); break;
            case ChamferFeature c: c.Distance = c.Distance2 = L(1, 0.03); break;
            case ShellFeature s: s.Thickness = L(2, 0.0625); break;
            case LinearPatternFeature lp: lp.Spacing = lp.Spacing2 = L(20, 1); break;
            case PlaneFeature p: p.Offset = L(10, 0.5); break;
            case HoleFeature ho:
                // a normal clearance hole for an M6 screw (ISO 273), or for a 1/4 in one (ASME B18.2.8)
                ho.Diameter = L(6.6, 0.266); ho.Depth = L(10, 0.5);
                ho.CbDiameter = L(11, 0.438); ho.CbDepth = L(6.5, 0.25); ho.CsDiameter = L(13, 0.5);
                break;
        }
        return f;
    }
}
