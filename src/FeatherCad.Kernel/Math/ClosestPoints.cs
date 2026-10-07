namespace FeatherCad.Kernel;

/// <summary>
/// A shape to measure between, as points, segments and triangles (a vertex; an edge's polyline; a face's
/// triangles), in world space.
/// </summary>
public sealed class MeasureShape
{
    public readonly List<Vec3> Points = new();
    public readonly List<(Vec3 A, Vec3 B)> Segments = new();
    public readonly List<(Vec3 A, Vec3 B, Vec3 C)> Triangles = new();
    public bool IsEmpty => Points.Count == 0 && Segments.Count == 0 && Triangles.Count == 0;
}

/// <summary>Minimum distance between shapes, and the closest points (on each).</summary>
public static class ClosestPoints
{
    readonly record struct Prim(int Kind, Vec3 A, Vec3 B, Vec3 C, Box3 Box); // Kind: 0 point, 1 segment, 2 triangle

    /// <summary>The minimum distance between two shapes and where it is: the closest points on A and on B.</summary>
    public static (double Distance, Vec3 OnA, Vec3 OnB) Between(MeasureShape a, MeasureShape b)
    {
        var pa = Prims(a); var pb = Prims(b);
        if (pa.Length == 0 || pb.Length == 0) return (double.NaN, default, default);
        // a first estimate, so most pairs are skipped by their boxes
        var best = Pair(pa[0], pb[0]);
        foreach (var x in pa)
            foreach (var y in pb)
            {
                if (BoxDistance(x.Box, y.Box) >= best.d) continue;
                var r = Pair(x, y);
                if (r.d < best.d) best = r;
                if (best.d == 0) return (0, best.p, best.q);
            }
        return (best.d, best.p, best.q);
    }

    static Prim[] Prims(MeasureShape s)
    {
        var list = new List<Prim>(s.Points.Count + s.Segments.Count + s.Triangles.Count);
        foreach (var p in s.Points) list.Add(new Prim(0, p, p, p, Box(p)));
        foreach (var (a, b) in s.Segments) list.Add(new Prim(1, a, b, b, Box(a, b)));
        foreach (var (a, b, c) in s.Triangles) list.Add(new Prim(2, a, b, c, Box(a, b, c)));
        return list.ToArray();
    }

    static Box3 Box(params Vec3[] ps) { var b = Box3.Empty; foreach (var p in ps) b.Add(p); return b; }

    static double BoxDistance(Box3 a, Box3 b)
    {
        double Gap(double a0, double a1, double b0, double b1) => Math.Max(0, Math.Max(a0 - b1, b0 - a1));
        var dx = Gap(a.Min.X, a.Max.X, b.Min.X, b.Max.X);
        var dy = Gap(a.Min.Y, a.Max.Y, b.Min.Y, b.Max.Y);
        var dz = Gap(a.Min.Z, a.Max.Z, b.Min.Z, b.Max.Z);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Closest points of two primitives (p on x, q on y).</summary>
    static (double d, Vec3 p, Vec3 q) Pair(Prim x, Prim y)
    {
        if (x.Kind > y.Kind) { var (d, p, q) = Pair(y, x); return (d, q, p); }
        (Vec3 p, Vec3 q) r = (x.Kind, y.Kind) switch
        {
            (0, 0) => (x.A, y.A),
            (0, 1) => (x.A, PointSegment(x.A, y.A, y.B)),
            (0, 2) => (x.A, PointTriangle(x.A, y.A, y.B, y.C)),
            (1, 1) => SegmentSegment(x.A, x.B, y.A, y.B),
            (1, 2) => SegmentTriangle(x.A, x.B, y.A, y.B, y.C),
            _ => TriangleTriangle(x, y),
        };
        return (Vec3.Distance(r.p, r.q), r.p, r.q);
    }

    public static Vec3 PointSegment(Vec3 p, Vec3 a, Vec3 b)
    {
        var ab = b - a; var l2 = Vec3.Dot(ab, ab);
        if (l2 < 1e-30) return a;
        return a + ab * Math.Clamp(Vec3.Dot(p - a, ab) / l2, 0, 1);
    }

    /// <summary>Closest point of a triangle to a point (Ericson, Real-Time Collision Detection 5.1.5).</summary>
    public static Vec3 PointTriangle(Vec3 p, Vec3 a, Vec3 b, Vec3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        double d1 = Vec3.Dot(ab, ap), d2 = Vec3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b;
        double d3 = Vec3.Dot(ab, bp), d4 = Vec3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = p - c;
        double d5 = Vec3.Dot(ab, cp), d6 = Vec3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        var denom = va + vb + vc;
        if (Math.Abs(denom) < 1e-300) return a; // degenerate
        return a + ab * (vb / denom) + ac * (vc / denom);
    }

    /// <summary>Closest points of two segments (Ericson 5.1.9).</summary>
    public static (Vec3 p, Vec3 q) SegmentSegment(Vec3 p1, Vec3 q1, Vec3 p2, Vec3 q2)
    {
        const double eps = 1e-30;
        var d1 = q1 - p1; var d2 = q2 - p2; var r = p1 - p2;
        double a = Vec3.Dot(d1, d1), e = Vec3.Dot(d2, d2), f = Vec3.Dot(d2, r);
        double s, t;
        if (a <= eps && e <= eps) { s = 0; t = 0; }
        else if (a <= eps) { s = 0; t = Math.Clamp(f / e, 0, 1); }
        else
        {
            var c = Vec3.Dot(d1, r);
            if (e <= eps) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
            else
            {
                var b = Vec3.Dot(d1, d2);
                var denom = a * e - b * b;
                s = denom > 1e-300 ? Math.Clamp((b * f - c * e) / denom, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
                else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0, 1); }
            }
        }
        return (p1 + d1 * s, p2 + d2 * t);
    }

    /// <summary>Closest points of a segment and a triangle (zero where the segment passes through it).</summary>
    static (Vec3 p, Vec3 q) SegmentTriangle(Vec3 s0, Vec3 s1, Vec3 a, Vec3 b, Vec3 c)
    {
        if (SegmentHitsTriangle(s0, s1, a, b, c) is { } hit) return (hit, hit);
        (Vec3 p, Vec3 q) best = (s0, PointTriangle(s0, a, b, c));
        void Try((Vec3 p, Vec3 q) r) { if (Vec3.Distance(r.p, r.q) < Vec3.Distance(best.p, best.q)) best = r; }
        Try((s1, PointTriangle(s1, a, b, c)));
        Try(SegmentSegment(s0, s1, a, b));
        Try(SegmentSegment(s0, s1, b, c));
        Try(SegmentSegment(s0, s1, c, a));
        return best;
    }

    static (Vec3 p, Vec3 q) TriangleTriangle(Prim x, Prim y)
    {
        (Vec3 p, Vec3 q)? best = null;
        void Try((Vec3 p, Vec3 q) r) { if (best == null || Vec3.Distance(r.p, r.q) < Vec3.Distance(best.Value.p, best.Value.q)) best = r; }
        foreach (var (s0, s1) in new[] { (x.A, x.B), (x.B, x.C), (x.C, x.A) }) Try(SegmentTriangle(s0, s1, y.A, y.B, y.C));
        foreach (var (s0, s1) in new[] { (y.A, y.B), (y.B, y.C), (y.C, y.A) })
        {
            var (q, p) = SegmentTriangle(s0, s1, x.A, x.B, x.C);
            Try((p, q));
        }
        return best!.Value;
    }

    /// <summary>Where a segment crosses a triangle (Möller-Trumbore), or null.</summary>
    static Vec3? SegmentHitsTriangle(Vec3 s0, Vec3 s1, Vec3 a, Vec3 b, Vec3 c)
    {
        var dir = s1 - s0;
        var e1 = b - a; var e2 = c - a;
        var h = Vec3.Cross(dir, e2);
        var det = Vec3.Dot(e1, h);
        if (Math.Abs(det) < 1e-18) return null; // parallel: the edge tests find the distance
        var inv = 1 / det;
        var s = s0 - a;
        var u = Vec3.Dot(s, h) * inv;
        if (u < 0 || u > 1) return null;
        var q = Vec3.Cross(s, e1);
        var v = Vec3.Dot(dir, q) * inv;
        if (v < 0 || u + v > 1) return null;
        var t = Vec3.Dot(e2, q) * inv;
        if (t < 0 || t > 1) return null;
        return s0 + dir * t;
    }
}
