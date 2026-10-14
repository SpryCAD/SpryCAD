using System.Globalization;
using SpryCad.Core;
using SpryCad.Core.Model;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.App.Sketcher;

/// <summary>
/// Dimensions to a centerline, as SolidWorks makes them for revolved parts: a distance between a construction line
/// (the axis, the middle of the diameter) and a line or point is the distance itself when its value is placed on
/// the geometry's side of the axis, and a diameter (twice the distance, ⌀) when it is placed across the axis. It
/// follows the value as it is dragged across, the number changing with it so nothing moves. Only construction lines
/// do this: a distance to a solid line is always a distance.
/// </summary>
public sealed partial class SketchEditor
{
    /// <summary>The axis of a distance to a centerline and the point it is measured from on the other geometry; null if not one.</summary>
    (SkLine axis, Vec2 from)? CenterlineOf(SkConstraint c)
    {
        if (c.Type != CType.Distance || c.Refs.Length != 2) return null;
        var axis = c.Refs.Select(Sk.Entity).OfType<SkLine>().FirstOrDefault(l => l.Construction);
        if (axis == null) return null;
        // the other reference is the geometry: a point, or a solid line parallel to the axis
        if (c.Refs.All(r => Sk.Entity(r) is SkLine { Construction: true })) return null;
        var (a, b) = DimPoints(c);
        var (la, lb) = LinePts(axis.Id);
        var d = lb - la;
        if (d.LengthSq < 1e-18) return null;
        d = d.Normalized;
        double Off(Vec2 p) => Math.Abs(Vec2.Cross(d, p - la));
        return (axis, Off(a) >= Off(b) ? a : b);
    }

    /// <summary>A point mirrored across a line (through <paramref name="a"/>, direction <paramref name="d"/>, unit).</summary>
    static Vec2 Mirror(Vec2 p, Vec2 a, Vec2 d) => (a + d * Vec2.Dot(p - a, d)) * 2 - p;

    /// <summary>Whether a value at <paramref name="label"/> makes the dimension a diameter: across the axis from the geometry. Null if it is not one to a centerline.</summary>
    bool? DoubledAt(SkConstraint c, Vec2 label)
    {
        if (CenterlineOf(c) is not { } cl) return null;
        var (la, lb) = LinePts(cl.axis.Id);
        var n = (lb - la).Normalized.Perp;
        var geometrySide = Vec2.Dot(cl.from - la, n);
        if (Math.Abs(geometrySide) < 1e-9) return null; // on the axis: nothing to double
        return Math.Sign(Vec2.Dot(label - la, n)) != Math.Sign(geometrySide);
    }

    /// <summary>
    /// A distance to a centerline whose value is dragged across the axis becomes a diameter (and back), its number
    /// following so the geometry stays. One whose value is an equation is left as it is.
    /// </summary>
    void UpdateDoubled(SkConstraint c, Vec2 label)
    {
        if (DoubledAt(c, label) is not { } doubled || doubled == c.Doubled) return;
        if (!string.IsNullOrEmpty(c.Expr) && !double.TryParse(c.Expr, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return;
        c.Doubled = doubled;
        c.Value = SketchSolver.Measure(Sk, c);
        c.Expr = Units.Entry(c.Value, c.ValueKind);
    }

    /// <summary>The two ends a diameter to a centerline is drawn between: the geometry, and its mirror across the axis.</summary>
    (Vec2 a, Vec2 b)? DoubledPoints(SkConstraint c)
    {
        if (!c.Doubled || CenterlineOf(c) is not { } cl) return null;
        var (la, lb) = LinePts(cl.axis.Id);
        return (cl.from, Mirror(cl.from, la, (lb - la).Normalized));
    }
}
