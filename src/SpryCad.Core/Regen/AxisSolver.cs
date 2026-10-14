using SpryCad.Core.Model;
using SpryCad.Kernel;

namespace SpryCad.Core.Regen;

/// <summary>
/// A reference axis from what is picked, as SolidWorks': one line, edge or round face (along it), two planes (where
/// they meet), two points (through them), or a point and a plane (square to the plane, through the point).
/// </summary>
public static class AxisSolver
{
    /// <summary>What the references make, in words; null when they make no axis (yet).</summary>
    public static string? TypeOf(IReadOnlyList<AxisReference> refs)
    {
        int planes = refs.Count(r => r.Plane != null), lines = refs.Count(r => r.Line != null), points = refs.Count(r => r.Point != null);
        return (refs.Count, planes, lines, points) switch
        {
            (1, 0, 1, 0) => refs[0].Line!.Face != null ? "Along the round face" : "Along the line or edge",
            (2, 2, 0, 0) => "Where the two planes meet",
            (2, 0, 0, 2) => "Through the two points",
            (2, 1, 0, 1) => "Square to the plane, through the point",
            _ => null,
        };
    }

    /// <summary>The stretch of the axis to draw (it runs on both ways), or why the references make none.</summary>
    public static (Vec3 A, Vec3 B) Solve(IReadOnlyList<AxisReference> refs, ModelState st, double perMm = 1)
    {
        if (refs.Count == 0) throw new FeatureException("Select a line, edge or round face; or two planes; or two points; or a point and a plane.");
        if (TypeOf(refs) == null)
            throw new FeatureException(refs.Count == 1
                ? refs[0].Plane != null ? "A plane: also pick a second plane (where they meet) or a point (square to it through the point)."
                : "A point: also pick a second point, or a plane (square to it through the point)."
                : "These references make no axis: pick one line, edge or round face; two planes; two points; or a point and a plane.");

        Vec3 o, d;
        var span = new List<Vec3>();
        var planes = refs.Where(r => r.Plane != null).Select(r => Resolver.ResolvePlane(r.Plane!, st)).ToList();
        var points = refs.Where(r => r.Point != null).Select(r => Resolver.ResolvePoint(r.Point!, st)).ToList();
        if (refs.FirstOrDefault(r => r.Line != null)?.Line is { } line)
        {
            (o, d) = Resolver.ResolveDir(line, st);
            span.AddRange(LineSpan(line, st));
        }
        else if (planes.Count == 2)
        {
            d = Vec3.Cross(planes[0].Normal, planes[1].Normal);
            if (d.Length < 1e-9) throw new FeatureException("The two planes are parallel: they do not meet.");
            d = d.Normalized;
            // the point of the line nearest the origin: on both planes, and on the plane through the origin square to it
            double d1 = Vec3.Dot(planes[0].Normal, planes[0].Origin), d2 = Vec3.Dot(planes[1].Normal, planes[1].Origin);
            var (n1, n2) = (planes[0].Normal, planes[1].Normal);
            o = (Vec3.Cross(n2, d) * d1 + Vec3.Cross(d, n1) * d2) / Vec3.Dot(d, d);
        }
        else if (points.Count == 2)
        {
            d = points[1] - points[0];
            if (d.Length < 1e-9) throw new FeatureException("The two points are the same point.");
            d = d.Normalized; o = points[0];
            span.AddRange(points);
        }
        else
        {
            d = planes[0].Normal; o = points[0];
            span.Add(points[0]); span.Add(points[0] - d * Vec3.Dot(points[0] - planes[0].Origin, d));
        }
        return Stretch(o, d, span, st, perMm);
    }

    /// <summary>Where a line reference reaches: an edge's or sketch line's ends, a round face's extent.</summary>
    static IEnumerable<Vec3> LineSpan(DirRef r, ModelState st)
    {
        if (r.Edge != null && Resolver.ResolveNativeEdge(r.Edge, st) is { } ne) return [ne.edge.V0.P, ne.edge.V1.P];
        if (r.Curve is { Sketch: { } sk } c && st.Sketches.TryGetValue(sk, out var sr) && sr.Sketch.Entity(c.SketchEntity) is Sketching.SkLine l)
            return [sr.Frame.ToWorld(sr.Sketch.P(l.P1)), sr.Frame.ToWorld(sr.Sketch.P(l.P2))];
        if (r.Face != null && Resolver.NativeFace(r.Face, st) is { } hit)
        {
            var b = hit.face.Bounds();
            return Enumerable.Range(0, 8).Select(i => new Vec3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z));
        }
        if (r.Feature != null && st.Axes.TryGetValue(r.Feature, out var ax)) return [ax.A, ax.B];
        return [];
    }

    /// <summary>The stretch drawn: over what the references reach (else the model), a little past each end.</summary>
    static (Vec3, Vec3) Stretch(Vec3 o, Vec3 d, List<Vec3> span, ModelState st, double perMm)
    {
        if (span.Count < 2)
        {
            var b = st.Bounds;
            if (!b.IsEmpty)
                for (int i = 0; i < 8; i++) span.Add(new Vec3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z));
            else { span.Add(o - d * (25 * perMm)); span.Add(o + d * (25 * perMm)); } // (50 mm, in the part's units)
        }
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var p in span) { var t = Vec3.Dot(p - o, d); lo = Math.Min(lo, t); hi = Math.Max(hi, t); }
        var more = Math.Max(2 * perMm, (hi - lo) * 0.1);
        return (o + d * (lo - more), o + d * (hi + more));
    }
}
