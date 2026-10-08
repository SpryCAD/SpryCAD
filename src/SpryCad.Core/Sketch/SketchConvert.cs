using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Core.Sketching;

/// <summary>
/// Convert Entities: model edges projected into a sketch as fixed geometry. Exact where the sketch has the curve:
/// lines as lines, circles and arcs facing the sketch as circles and arcs. Any other curve (a spline, a tilted
/// circle) as arcs and lines fitted to it, within <see cref="Tolerance"/>. Edges joined end to end share their points.
/// </summary>
public static class SketchConvert
{
    /// <summary>How far a fitted arc or line may stray from a curve with no exact sketch equivalent.</summary>
    public const double Tolerance = 1e-3;

    /// <summary>Converts one edge; false when it adds nothing (seen end on, or there already).</summary>
    public static bool Edge(Sketch sk, Frame frame, NEdge e)
    {
        Vec2 L(Vec3 p) => frame.ToLocal(p);
        var (a, b) = (L(e.V0.P), L(e.V1.P));
        switch (e.Curve)
        {
            case NLine:
                return Line(sk, a, b);
            case NCircle c when Math.Abs(Vec3.Dot(c.Axis.Normalized, frame.Normal)) > 1 - 1e-9:
                {
                    var center = Point(sk, L(c.C));
                    if (e.Closed)
                    {
                        if (sk.Entities.OfType<SkCircle>().Any(o => o.Center == center && Math.Abs(o.R - c.R) < 1e-9)) return false;
                        var ci = sk.Add(new SkCircle { Center = center, R = c.R });
                        var rc = sk.AddConstraint(CType.Radius, ci.Id); rc.Value = c.R; rc.Expr = Expr.Format(c.R);
                        rc.LabelPlaced = false; // made, not placed: laid out
                        return true;
                    }
                    return Arc(sk, center, a, b, L(e.Mid));
                }
        }
        // anything else, as it looks from the sketch
        var pts = e.Samples(Tolerance / 4).Select(L).ToList();
        if (pts.Count < 2) return false;
        pts[0] = a; pts[^1] = b;
        var before = sk.Entities.Count;
        Fit(sk, pts, 0, pts.Count - 1);
        return sk.Entities.Count > before;
    }

    /// <summary>A display polyline (no exact edge known) as lines.</summary>
    public static bool Polyline(Sketch sk, IReadOnlyList<Vec2> pts)
    {
        bool any = false;
        for (int i = 0; i + 1 < pts.Count; i++) any |= Line(sk, pts[i], pts[i + 1]);
        return any;
    }

    /// <summary>Points pts[i0..i1] as one line, one arc, or split in two and each fitted again.</summary>
    static void Fit(Sketch sk, List<Vec2> pts, int i0, int i1)
    {
        var (a, b) = (pts[i0], pts[i1]);
        if (i1 - i0 < 2) { Line(sk, a, b); return; }
        var closedSpan = Vec2.Distance(a, b) < 1e-9;
        bool Within(Func<Vec2, double> dev) { for (int k = i0 + 1; k < i1; k++) if (dev(pts[k]) > Tolerance) return false; return true; }
        if (!closedSpan && Within(p => SegmentDistance(p, a, b))) { Line(sk, a, b); return; }
        var im = (i0 + i1) / 2;
        if (!closedSpan && Circumcenter(a, pts[im], b) is { } cc)
        {
            var r = Vec2.Distance(cc, a);
            if (r < 1e6 && Within(p => Math.Abs(Vec2.Distance(p, cc) - r))) { Arc(sk, Point(sk, cc), a, b, pts[im]); return; }
        }
        Fit(sk, pts, i0, im);
        Fit(sk, pts, im, i1);
    }

    static double SegmentDistance(Vec2 p, Vec2 a, Vec2 b)
    {
        var d = b - a; var l2 = d.LengthSq;
        var t = l2 > 0 ? Math.Clamp(Vec2.Dot(p - a, d) / l2, 0, 1) : 0;
        return Vec2.Distance(a + d * t, p);
    }

    static Vec2? Circumcenter(Vec2 a, Vec2 b, Vec2 c)
    {
        var d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-12) return null;
        var ux = (a.LengthSq * (b.Y - c.Y) + b.LengthSq * (c.Y - a.Y) + c.LengthSq * (a.Y - b.Y)) / d;
        var uy = (a.LengthSq * (c.X - b.X) + b.LengthSq * (a.X - c.X) + c.LengthSq * (b.X - a.X)) / d;
        return new Vec2(ux, uy);
    }

    /// <summary>A fixed point at p: the one there already, else a new one.</summary>
    static int Point(Sketch sk, Vec2 p)
    {
        foreach (var q in sk.Points) if (Vec2.Distance(q.P, p) < 1e-6) return q.Id;
        return sk.AddPoint(p, fix: true).Id;
    }

    static bool Line(Sketch sk, Vec2 a, Vec2 b)
    {
        if (Vec2.Distance(a, b) < 1e-9) return false; // an edge seen end on
        int pa = Point(sk, a), pb = Point(sk, b);
        if (sk.Entities.OfType<SkLine>().Any(l => (l.P1 == pa && l.P2 == pb) || (l.P1 == pb && l.P2 == pa))) return false;
        sk.Add(new SkLine { P1 = pa, P2 = pb });
        return true;
    }

    /// <summary>An arc about a center from a to b, the way through m (sketch arcs run counter-clockwise).</summary>
    static bool Arc(Sketch sk, int center, Vec2 a, Vec2 b, Vec2 m)
    {
        int pa = Point(sk, a), pb = Point(sk, b);
        bool ccw = Vec2.Cross(b - a, m - a) < 0;
        var (s, e) = ccw ? (pa, pb) : (pb, pa);
        if (sk.Entities.OfType<SkArc>().Any(x => x.Center == center && x.Start == s && x.End == e)) return false;
        sk.Add(new SkArc { Center = center, Start = s, End = e });
        return true;
    }

    /// <summary>An edge and the edges joined to it end to end where they run on smoothly (tangent), both ways.</summary>
    public static List<NEdge> TangentChain(NBody body, NEdge start)
    {
        body.Link();
        var at = new Dictionary<NVertex, List<NEdge>>();
        foreach (var e in body.Edges)
            foreach (var v in new[] { e.V0, e.V1 }.Distinct())
                (at.TryGetValue(v, out var l) ? l : at[v] = new()).Add(e);
        // the direction an edge leaves a vertex by
        static Vec3 Away(NEdge e, NVertex v)
        {
            var d = ReferenceEquals(v, e.V0) ? e.Curve.Deriv(e.T0) : -e.Curve.Deriv(e.T1);
            if (e.T1 < e.T0) d = -d;
            return d.Length > 1e-12 ? d.Normalized : d;
        }
        var chain = new List<NEdge> { start };
        var seen = new HashSet<NEdge> { start };
        var todo = new Stack<(NEdge e, NVertex v)>();
        if (!start.Closed) { todo.Push((start, start.V0)); todo.Push((start, start.V1)); }
        const double cosTol = 0.99985; // 1°
        while (todo.Count > 0)
        {
            var (e, v) = todo.Pop();
            if (!at.TryGetValue(v, out var others)) continue;
            var dir = -Away(e, v); // arriving at v
            foreach (var o in others)
            {
                if (seen.Contains(o) || o.Closed) continue;
                if (Vec3.Dot(dir, Away(o, v)) < cosTol) continue;
                seen.Add(o); chain.Add(o);
                todo.Push((o, ReferenceEquals(o.V0, v) ? o.V1 : o.V0));
                break; // one way on
            }
        }
        return chain;
    }
}
