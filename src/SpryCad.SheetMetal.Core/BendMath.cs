namespace SpryCad.SheetMetal;

/// <summary>How a bend's length in the flat is found (sheet metal spec section 5), chosen by the shop profile.</summary>
public enum AllowanceMethod
{
    /// <summary>BA = θ (R + K t).</summary>
    KFactor,
    /// <summary>BA by angle from the profile's bend table.</summary>
    BendTable,
    /// <summary>BD by angle from the profile's bend table; BA = 2 OSSB − BD.</summary>
    BendDeduction,
    /// <summary>DIN 6935: the neutral fiber at k t/2, k = 0.65 + ½ log₁₀(R/t) (1 from R/t = 5); none under 15°.</summary>
    Din6935,
}

/// <summary>Where a flange's length is measured to at a bend (sheet metal spec section 5). An option of each flange.</summary>
public enum FlangeReference
{
    /// <summary>The outside mold line: the virtual sharp of the outside surfaces.</summary>
    OutsideMoldLine,
    /// <summary>The inside mold line: the virtual sharp of the inside surfaces.</summary>
    InsideMoldLine,
    /// <summary>The tangent line, where the bend starts.</summary>
    Tangent,
}

public enum BendDirection { Up, Down }

/// <summary>Which rule produced a bend's allowance (sheet metal spec section 5): for the bend report and diffs.</summary>
public sealed record AllowanceSource(AllowanceMethod Method, string Detail, bool Overridden = false)
{
    public override string ToString() => Overridden ? $"{Detail} (override)" : Detail;
}

/// <summary>
/// The bend formulas (sheet metal spec section 5). θ is the bend (deflection) angle in radians, 0 &lt; θ &lt; π: a
/// shallow bend is a small θ. R is the inside radius, t the thickness.
/// </summary>
public static class BendMath
{
    public static double KFactorAllowance(double theta, double r, double t, double k) => theta * (r + k * t);

    /// <summary>Outside setback: from the tangent line to the virtual sharp of the outside surfaces.</summary>
    public static double Ossb(double theta, double r, double t) => (r + t) * Math.Tan(theta / 2);

    /// <summary>Inside setback: from the tangent line to the virtual sharp of the inside surfaces.</summary>
    public static double Issb(double theta, double r) => r * Math.Tan(theta / 2);

    public static double Deduction(double theta, double r, double t, double ba) => 2 * Ossb(theta, r, t) - ba;

    public static double AllowanceFromDeduction(double theta, double r, double t, double bd) => 2 * Ossb(theta, r, t) - bd;

    /// <summary>
    /// DIN 6935's allowance: the neutral fiber at k × t/2 from the inside, k = 0.65 + ½ log₁₀(R/t), 1 from R/t = 5.
    /// For bends under 15° (opening angles over 165°) the standard gives no compensation: the flat is the outside lengths
    /// to the sharp, 2 (R + t) tan(θ/2) for the bend.
    /// </summary>
    public static double Din6935Allowance(double theta, double r, double t)
    {
        if (theta < 15 * Math.PI / 180) return 2 * Ossb(theta, r, t);
        return theta * (r + DinK(r, t) * t / 2);
    }

    /// <summary>DIN 6935's correction factor (relative to half the thickness).</summary>
    public static double DinK(double r, double t) => r >= 5 * t ? 1 : Math.Clamp(0.65 + 0.5 * Math.Log10(r / t), 0, 1);

    /// <summary>
    /// What a flange's dimension to a bend leaves of the flat panel: the length from the flange's far edge to the
    /// bend's tangent line, for a flange <paramref name="length"/> long measured to <paramref name="reference"/>.
    /// </summary>
    public static double FlatContribution(double length, FlangeReference reference, double theta, double r, double t) => reference switch
    {
        FlangeReference.OutsideMoldLine => length - Ossb(theta, r, t),
        FlangeReference.InsideMoldLine => length - Issb(theta, r),
        _ => length,
    };

    /// <summary>The dimensioned length a flat panel length gives (the inverse of <see cref="FlatContribution"/>).</summary>
    public static double DimensionOf(double flat, FlangeReference reference, double theta, double r, double t) => reference switch
    {
        FlangeReference.OutsideMoldLine => flat + Ossb(theta, r, t),
        FlangeReference.InsideMoldLine => flat + Issb(theta, r),
        _ => flat,
    };
}
