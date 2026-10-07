namespace FeatherCad.Kernel.Native;

/// <summary>Piecewise linear curve through exact points (intermediate result of traced intersections).</summary>
public sealed class NPolyline : NCurve
{
    public Vec3[] Pts { get; }
    public double[] S { get; }   // cumulative length
    public bool IsClosed { get; }
    public NPolyline(IReadOnlyList<Vec3> pts, bool closed)
    {
        var l = pts.ToList();
        if (closed && Vec3.Distance(l[0], l[^1]) > 1e-12) l.Add(l[0]);
        Pts = l.ToArray(); IsClosed = closed;
        S = new double[Pts.Length];
        for (int i = 1; i < Pts.Length; i++) S[i] = S[i - 1] + Vec3.Distance(Pts[i], Pts[i - 1]);
    }
    public double Total => S[^1];
    public override Vec3 Eval(double t)
    {
        if (IsClosed) { t %= Total; if (t < 0) t += Total; }
        t = Math.Clamp(t, 0, Total);
        int lo = 0, hi = S.Length - 1;
        while (hi - lo > 1) { int m = (lo + hi) / 2; if (S[m] <= t) lo = m; else hi = m; }
        var seg = S[hi] - S[lo];
        return seg > 0 ? Vec3.Lerp(Pts[lo], Pts[hi], (t - S[lo]) / seg) : Pts[lo];
    }
    public override Vec3 Deriv(double t) => (Eval(t + 1e-6) - Eval(t - 1e-6)) / 2e-6;
    public override double Param(Vec3 p, double? hint = null)
    {
        double best = double.MaxValue, bt = 0;
        for (int i = 0; i + 1 < Pts.Length; i++)
        {
            var a = Pts[i]; var d = Pts[i + 1] - a; var l2 = d.LengthSq;
            var u = l2 > 0 ? Math.Clamp(Vec3.Dot(p - a, d) / l2, 0, 1) : 0;
            var dist = (a + d * u - p).LengthSq;
            if (dist < best) { best = dist; bt = S[i] + u * (S[i + 1] - S[i]); }
        }
        return bt;
    }
    public override bool Periodic => IsClosed;
    public override double Period => Total;
    public override NCurve Transformed(in Transform t) { var tt = t; return new NPolyline(Pts.Select(p => tt.Point(p)).ToList(), IsClosed); }
    public override int Samples(double t0, double t1, double tol) => Math.Max(1, (int)Math.Ceiling(Math.Abs(t1 - t0) / Math.Max(Total, 1e-12) * (Pts.Length - 1)));
    public override ExactCurve ToExact() => BSplineCurve.Interpolate(Pts, 3);

    /// <summary>The exact points between parameters t0 &lt; t1 (inclusive ends).</summary>
    public List<Vec3> Between(double t0, double t1)
    {
        var res = new List<Vec3> { Eval(t0) };
        if (IsClosed && t1 > Total)
        {
            for (int i = 0; i < Pts.Length; i++) if (S[i] > t0 + 1e-12) res.Add(Pts[i]);
            for (int i = 1; i < Pts.Length; i++) if (S[i] + Total < t1 - 1e-12) res.Add(Pts[i]);
        }
        else for (int i = 0; i < Pts.Length; i++) if (S[i] > t0 + 1e-12 && S[i] < t1 - 1e-12) res.Add(Pts[i]);
        res.Add(Eval(t1));
        return res;
    }
}

public static class NIntersect
{
    // ------------------------------------------------------------------------------------------
    // edge ∩ surface

    public sealed record EdgeHits(List<double> Params, bool OnSurface);

    /// <summary>
    /// Parameters where the edge crosses the surface, plus end points lying on it; pure tangential touches are
    /// not reported (they do not split the edge; contact there is resolved by the face pair). Lines against planes and
    /// quadrics and circles against planes are solved in closed form; other pairs use guaranteed root isolation
    /// (F_ grows no faster than distance, so an interval of arc length L holds a root only if |f(a)|+|f(b)| ≤ L).
    /// </summary>
    public static EdgeHits EdgeSurface(NEdge e, NSurface s, double tol)
    {
        var c = e.Curve;
        double F(double t) => s.F_(c.Eval(t));
        double lo = Math.Min(e.T0, e.T1), hi = Math.Max(e.T0, e.T1);
        var res = new List<double>();
        // the face-splitting stage relies on edges having been sampled at this tolerance first (the sample
        // cache keeps the first sampling that is fine enough); keep that order until the cache is per-tolerance
        e.Samples(tol * 50);

        // edge lying on the surface (fitted curves only within their fit tolerance: a boolean fits traced
        // sections within 50 tol of the exact intersection)
        const int probe = 16;
        bool allOn = true;
        var onTol = c is NSpline or NPolyline ? Math.Max(tol * 100, c is NSpline ns ? ns.FitTol * 2 : 0) : tol;
        for (int i = 0; i <= probe && allOn; i++) if (Math.Abs(F(lo + (hi - lo) * i / probe)) > onTol) allOn = false;
        if (allOn) return new EdgeHits(res, true);

        if (Math.Abs(F(e.T0)) < tol) res.Add(e.T0);
        if (Math.Abs(F(e.T1)) < tol) res.Add(e.T1);
        var exact = AnalyticRoots(c, s, tol);
        if (exact != null)
        {
            foreach (var t0 in exact)
            {
                var t = t0;
                if (c.Periodic) { while (t < lo - 1e-12) t += c.Period; while (t > hi + 1e-12) t -= c.Period; }
                if (t >= lo - 1e-12 && t <= hi + 1e-12) res.Add(Math.Clamp(t, lo, hi));
            }
        }
        else
        {
            double total = ArcBound(c, lo, hi);
            // leaves no shorter than this: two roots closer together are a tangential touch, which does not split
            // edges anyway, and an edge running alongside a surface would otherwise be subdivided into 1e5 pieces
            double minL = Math.Max(total * 1e-5, tol * 1000);
            void Iso(double ta, double tb, double fa, double fb, int depth)
            {
                var L = ArcBound(c, ta, tb);
                if (Math.Abs(fa) + Math.Abs(fb) > L * 1.001 + tol) return; // no root possible here
                if (depth >= 48 || L < minL)
                {
                    // crossings only: pure tangential touches do not split edges (see the summary); a screw surface's
                    // signed distance also jumps (from one turn to the next) where nothing is crossed
                    if (fa * fb < 0)
                    {
                        var r = Roots.Bisect(F, ta, tb);
                        if (s.Kind != SurfaceKind.Screw || Math.Abs(F(r)) < tol * 1e3) res.Add(r);
                    }
                    return;
                }
                var m = 0.5 * (ta + tb); var fm = F(m);
                Iso(ta, m, fa, fm, depth + 1);
                Iso(m, tb, fm, fb, depth + 1);
            }
            // start from a few segments so the arc bound of curved edges stays tight
            const int n = 8; double prevT = lo, prevF = F(lo);
            for (int i = 1; i <= n; i++)
            {
                var t = lo + (hi - lo) * i / n; var f = F(t);
                Iso(prevT, t, prevF, f, 0);
                prevT = t; prevF = f;
            }
        }
        res.Sort();
        // merge duplicates; an end point lying on the surface absorbs crossings right next to it (no micro edges)
        bool startOn = Math.Abs(F(e.T0)) < tol, endOn = Math.Abs(F(e.T1)) < tol;
        Vec3 p0 = e.V0.P, p1 = e.V1.P;
        var merged = new List<double>();
        foreach (var t in res)
        {
            var p = c.Eval(t);
            bool isEnd = t == e.T0 || t == e.T1;
            if (!isEnd && ((startOn && Vec3.Distance(p, p0) < tol * 100) || (endOn && Vec3.Distance(p, p1) < tol * 100))) continue;
            if (merged.Count == 0 || Vec3.Distance(p, c.Eval(merged[^1])) > tol) merged.Add(t);
        }
        return new EdgeHits(merged, false);
    }

    /// <summary>Report exact tangential touches (closed-form cases) as split points.</summary>
    public static bool TouchesSurface = true;

    /// <summary>Upper bound of the arc length of the curve over [a, b].</summary>
    static double ArcBound(NCurve c, double a, double b)
    {
        var d = Math.Abs(b - a);
        switch (c)
        {
            case NLine: return d;
            case NCircle ci: return ci.R * d;
            case NEllipse el: return Math.Max(el.A, el.B) * d;
            case NHelix hx: return d * Math.Sqrt(hx.Radius * hx.Radius + hx.Motion.K * hx.Motion.K) * 1.000001 + 1e-12;
            default:
                {
                    // splines/polylines: chord sum of samples with a safety factor
                    double l = 0; var p = c.Eval(a);
                    for (int i = 1; i <= 8; i++) { var q = c.Eval(a + (b - a) * i / 8); l += Vec3.Distance(p, q); p = q; }
                    return l * 1.5 + 1e-12;
                }
        }
    }

    /// <summary>All parameters where the (unbounded) curve meets the surface, or null without a closed form.</summary>
    static List<double>? AnalyticRoots(NCurve c, NSurface s, double tol)
    {
        if (c is NLine ln)
        {
            var l0 = s.F.ToLocal3(ln.P);
            var d = new Vec3(Vec3.Dot(ln.D, s.F.XDir), Vec3.Dot(ln.D, s.F.YDir), Vec3.Dot(ln.D, s.F.Normal));
            double F(double t) => s.F_(ln.Eval(t));
            switch (s.Kind)
            {
                case SurfaceKind.Plane:
                    return Math.Abs(d.Z) < 1e-15 ? new List<double>() : new List<double> { -l0.Z / d.Z };
                case SurfaceKind.Cylinder:
                    return Quadratic(d.X * d.X + d.Y * d.Y, 2 * (l0.X * d.X + l0.Y * d.Y), l0.X * l0.X + l0.Y * l0.Y - s.R * s.R, F, tol);
                case SurfaceKind.Sphere:
                    return Quadratic(1, 2 * (l0.X * d.X + l0.Y * d.Y + l0.Z * d.Z), l0.X * l0.X + l0.Y * l0.Y + l0.Z * l0.Z - s.R * s.R, F, tol);
                case SurfaceKind.Cone:
                    {
                        var k = Math.Tan(s.R2); var r0 = s.R + l0.Z * k;
                        var roots = Quadratic(d.X * d.X + d.Y * d.Y - d.Z * d.Z * k * k,
                            2 * (l0.X * d.X + l0.Y * d.Y) - 2 * r0 * d.Z * k,
                            l0.X * l0.X + l0.Y * l0.Y - r0 * r0, F, tol);
                        // keep the nappe the cone surface lives on (radius ≥ 0)
                        return roots.Where(t => s.R + (l0.Z + d.Z * t) * k >= -tol).ToList();
                    }
                default: return null;
            }
        }
        if (c is NCircle ci && s.Kind == SurfaceKind.Plane)
        {
            var pl = s.Plane!;
            double k = Vec3.Dot(pl.Normal, ci.C) - pl.D;
            double A = ci.R * Vec3.Dot(pl.Normal, ci.X), B = ci.R * Vec3.Dot(pl.Normal, ci.Y);
            var amp = Math.Sqrt(A * A + B * B);
            var roots = new List<double>();
            if (amp < 1e-15) return roots; // parallel (lying on the plane is handled by the caller)
            var phi = Math.Atan2(B, A);
            var x = -k / amp;
            if (Math.Abs(x) > 1 || Math.Abs(Math.Abs(x) - 1) < 1e-12)
            {
                // touching the plane (see Quadratic): one exact point
                if (TouchesSurface && Math.Abs(Math.Abs(k) - amp) < tol) roots.Add(x > 0 ? phi : phi + Math.PI);
                return roots;
            }
            var w = Math.Acos(x);
            roots.Add(phi + w);
            if (w > 1e-12) roots.Add(phi - w);
            return roots;
        }
        return null;
    }

    /// <summary>Real roots of a t² + b t + c (touches within tol included), via the stable formula.</summary>
    static List<double> Quadratic(double a, double b, double c, Func<double, double> f, double tol)
    {
        var r = new List<double>();
        if (Math.Abs(a) < 1e-14)
        {
            if (Math.Abs(b) > 1e-300) r.Add(-c / b);
            return r;
        }
        var disc = b * b - 4 * a * c;
        var sq = disc > 0 ? Math.Sqrt(disc) : 0;
        // a double root (the curve only touches the surface): one exact point, whatever the rounding of the
        // discriminant. Touch points matter when another face's boundary passes through them (two blends
        // tangent to the same face whose tangent curves cross); vertex imprinting keeps both sides consistent.
        if (disc < 0 || sq / Math.Abs(a) < tol)
        {
            var tt = -b / (2 * a);
            if (TouchesSurface && Math.Abs(f(tt)) < tol) r.Add(tt);
            return r;
        }
        var q = -0.5 * (b + (b >= 0 ? sq : -sq));
        var t1 = q / a;
        r.Add(t1);
        if (Math.Abs(q) > 1e-300) { var t2 = c / q; if (Math.Abs(t2 - t1) > 1e-15 * Math.Max(1, Math.Abs(t1))) r.Add(t2); }
        return r;
    }

    // ------------------------------------------------------------------------------------------
    // surface ∩ surface (analytic)

    /// <summary>
    /// Exact intersection curves of two analytic surfaces, or null when no closed form is implemented
    /// (the caller then traces the intersection). Coincident surfaces must be handled by the caller.
    /// </summary>
    public static List<NCurve>? Analytic(NSurface a, NSurface b, double tol)
    {
        if (a.Kind == SurfaceKind.Plane && b.Kind == SurfaceKind.Plane) return PlanePlane(a.Plane!, b.Plane!);
        if (a.Kind == SurfaceKind.Pipe) return PipeOther(a, b, tol);
        if (b.Kind == SurfaceKind.Pipe) return PipeOther(b, a, tol);
        if (a.Kind == SurfaceKind.Screw) return ScrewOther(a, b, tol);
        if (b.Kind == SurfaceKind.Screw) return ScrewOther(b, a, tol);

        // coaxial surfaces of revolution (incl. planes perpendicular to the axis and spheres centred on it)
        foreach (var (o, ax) in CandidateAxes(a, b))
        {
            var pa = a.ProfileAbout(o, ax, tol); var pb = b.ProfileAbout(o, ax, tol);
            if (pa == null || pb == null) continue;
            var pts = ProfilePoints(pa, pb, tol);
            if (pts == null) return null;
            var refDir = (a.IsRevolution ? a.F.XDir : b.IsRevolution ? b.F.XDir : ax.AnyPerpendicular());
            var curves = new List<NCurve>();
            foreach (var p in pts)
                if (p.X > tol) curves.Add(new NCircle(o + ax * p.Y, ax, refDir, p.X));
            return curves;
        }

        if (a.Kind == SurfaceKind.Plane && b.Kind == SurfaceKind.Cylinder) return PlaneCylinder(a.Plane!, b, tol);
        if (b.Kind == SurfaceKind.Plane && a.Kind == SurfaceKind.Cylinder) return PlaneCylinder(b.Plane!, a, tol);
        if (a.Kind == SurfaceKind.Cylinder && b.Kind == SurfaceKind.Cylinder && Math.Abs(Math.Abs(Vec3.Dot(a.Axis, b.Axis)) - 1) < 1e-10)
            return ParallelCylinders(a, b, tol);
        if (a.Kind == SurfaceKind.Cylinder && b.Kind == SurfaceKind.Cylinder && Math.Abs(a.R - b.R) < tol)
        {
            // equal radii with intersecting axes (e.g. mitered fillet corners): two ellipses in the bisector planes
            var p = AxisIntersection(a, b, tol);
            if (p != null)
            {
                var res = new List<NCurve>();
                foreach (var n in new[] { a.Axis + b.Axis, a.Axis - b.Axis })
                {
                    if (n.Length < 1e-9) continue;
                    res.AddRange(PlaneCylinder(Plane.FromPointNormal(p.Value, n.Normalized), a, tol));
                }
                return res;
            }
        }
        return null;
    }

    /// <summary>
    /// Pipe against another surface: a rolling-ball pipe only touches its support surfaces (along its contact
    /// curves, which are edges of the blend already: no section), and a plane normal to the spine through a
    /// spine node cuts it in that node's circle. Anything else is traced.
    /// </summary>
    /// <summary>
    /// A screw surface and a cylinder on its axis meet along helices: where the generator's distance from the axis is
    /// the cylinder's radius, each such generator point carried by the motion. Anything else: traced (null).
    /// </summary>
    static List<NCurve>? ScrewOther(NSurface screw, NSurface o, double tol)
    {
        if (o.Kind != SurfaceKind.Cylinder) return null;
        var sc = screw.Screw!; var m = sc.Motion;
        if (Vec3.Cross(o.Axis, m.A).Length > 1e-9 || Vec3.Cross(o.Origin - m.O, m.A).Length > tol) return null;
        double Radial(double v) { var l = m.Local(sc.Gen.Eval(v)); return Math.Sqrt(l.X * l.X + l.Y * l.Y) - o.R; }
        // the generator a little past the part that was swept (crossings at its very ends)
        double pad = (sc.V1 - sc.V0) * 0.01 + 1e-9, v0 = sc.V0 - pad, v1 = sc.V1 + pad;
        var res = new List<NCurve>();
        const int n = 64;
        double pv = v0, pf = Radial(v0);
        for (int i = 1; i <= n; i++)
        {
            var v = v0 + (v1 - v0) * i / n; var f = Radial(v);
            if (Math.Abs(pf) < tol * 1e-3 && Math.Abs(f) < tol * 1e-3) return null; // the generator lies on the cylinder: coincident
            if (pf == 0 || pf * f < 0)
            {
                var r = pf == 0 ? pv : Roots.Bisect(Radial, pv, v);
                if (res.Count == 0 || res[^1] is not NHelix last || Vec3.Distance(last.P0, sc.Gen.Eval(r)) > tol)
                    res.Add(new NHelix(m, sc.Gen.Eval(r)));
            }
            pv = v; pf = f;
        }
        return res;
    }

    static List<NCurve>? PipeOther(NSurface pipe, NSurface o, double tol)
    {
        var sp = pipe.Spine!;
        if (o.Kind != SurfaceKind.Pipe)
            foreach (var sup in new[] { sp.SupportA, sp.SupportB })
                if (sup != null && sup.Kind == o.Kind && sup.CoincidentWith(o, tol)) return new List<NCurve>();
        if (o.Kind == SurfaceKind.Plane)
        {
            var pl = o.Plane!;
            for (int i = 0; i < sp.P.Length; i++)
                if (Math.Abs(pl.Distance(sp.P[i])) < tol && Math.Abs(Vec3.Dot(pl.Normal, sp.D[i])) > 1 - 1e-12)
                {
                    // the node's cross-section; other crossings of a short blend pipe with such a plane do not occur
                    return new List<NCurve> { new NCircle(sp.P[i], sp.D[i], sp.D[i].AnyPerpendicular(), pipe.R) };
                }
        }
        return null;
    }

    static IEnumerable<(Vec3 o, Vec3 axis)> CandidateAxes(NSurface a, NSurface b)
    {
        if (a.Kind == SurfaceKind.Sphere && b.Kind == SurfaceKind.Sphere)
        {
            var d = b.Origin - a.Origin;
            if (d.Length > 1e-12) yield return (a.Origin, d.Normalized);
            else yield return (a.Origin, a.Axis);
            yield break;
        }
        if (a.Kind == SurfaceKind.Sphere && b.Kind == SurfaceKind.Plane) { yield return (a.Origin, b.Plane!.Normal); yield break; }
        if (b.Kind == SurfaceKind.Sphere && a.Kind == SurfaceKind.Plane) { yield return (b.Origin, a.Plane!.Normal); yield break; }
        if (a.IsRevolution && a.Kind != SurfaceKind.Sphere) yield return (a.Origin, a.Axis);
        if (b.IsRevolution && b.Kind != SurfaceKind.Sphere) yield return (b.Origin, b.Axis);
        if (a.Kind == SurfaceKind.Sphere && b.IsRevolution) yield return (b.Origin, b.Axis);
        if (b.Kind == SurfaceKind.Sphere && a.IsRevolution) yield return (a.Origin, a.Axis);
    }

    /// <summary>Intersection point of two (non-parallel) axis lines, if they meet.</summary>
    static Vec3? AxisIntersection(NSurface a, NSurface b, double tol)
    {
        var d1 = a.Axis; var d2 = b.Axis; var w = a.Origin - b.Origin;
        var bb = Vec3.Dot(d1, d2); var den = 1 - bb * bb;
        if (den < 1e-12) return null;
        var d = Vec3.Dot(d1, w); var e = Vec3.Dot(d2, w);
        var s = (bb * e - d) / den; var t = (e - bb * d) / den;
        var p1 = a.Origin + d1 * s; var p2 = b.Origin + d2 * t;
        return Vec3.Distance(p1, p2) < tol ? (p1 + p2) * 0.5 : null;
    }

    static List<NCurve> PlanePlane(Plane p, Plane q)
    {
        var d = Vec3.Cross(p.Normal, q.Normal);
        var l = d.Length;
        if (l < 1e-12) return new List<NCurve>();
        d /= l;
        // point on both planes closest to the origin
        var n1 = p.Normal; var n2 = q.Normal;
        var dot = Vec3.Dot(n1, n2);
        var det = 1 - dot * dot;
        var c1 = (p.D - q.D * dot) / det; var c2 = (q.D - p.D * dot) / det;
        return new List<NCurve> { new NLine(n1 * c1 + n2 * c2, d) };
    }

    static List<NCurve> PlaneCylinder(Plane pl, NSurface cyl, double tol)
    {
        var n = pl.Normal; var a = cyl.Axis; var R = cyl.R;
        var cosg = Vec3.Dot(n, a);
        var res = new List<NCurve>();
        if (Math.Abs(cosg) < 1e-12)
        {
            // plane parallel to the axis: 0, 1 (tangent) or 2 lines
            var d = pl.Distance(cyl.Origin);
            var foot = cyl.Origin - n * d;
            var side = Vec3.Cross(a, n).Normalized;
            if (Math.Abs(Math.Abs(d) - R) < tol) res.Add(new NLine(foot, a));
            else if (Math.Abs(d) < R)
            {
                var w = Math.Sqrt(R * R - d * d);
                res.Add(new NLine(foot + side * w, a));
                res.Add(new NLine(foot - side * w, a));
            }
            return res;
        }
        // oblique: ellipse centred where the axis pierces the plane
        var t = pl.IntersectRay(cyl.Origin, a);
        var center = cyl.Origin + a * t;
        var major = (a - n * cosg).Normalized;          // projection of the axis into the plane
        var minor = Vec3.Cross(n, major).Normalized;
        res.Add(new NEllipse(center, major, minor, R / Math.Abs(cosg), R));
        return res;
    }

    static List<NCurve> ParallelCylinders(NSurface a, NSurface b, double tol)
    {
        var ax = a.Axis;
        var x = a.F.XDir; var y = Vec3.Cross(ax, x);
        var d3 = b.Origin - a.Origin; d3 -= ax * Vec3.Dot(d3, ax);
        var c2 = new Vec2(Vec3.Dot(d3, x), Vec3.Dot(d3, y));
        var res = new List<NCurve>();
        foreach (var p in CircleCircle(Vec2.Zero, a.R, c2, b.R, tol))
            res.Add(new NLine(a.Origin + x * p.X + y * p.Y, ax));
        return res;
    }

    // ---- 2D profile intersections in the (r, h) half plane --------------------------------------

    static List<Vec2>? ProfilePoints(NSurface.Profile p, NSurface.Profile q, double tol)
    {
        switch (p, q)
        {
            case (NSurface.HLine h1, NSurface.HLine h2): return Math.Abs(h1.H - h2.H) < tol ? null : new();
            case (NSurface.VLine v1, NSurface.VLine v2): return Math.Abs(v1.R - v2.R) < tol ? null : new();
            case (NSurface.HLine h, NSurface.VLine v): return new() { new Vec2(v.R, h.H) };
            case (NSurface.VLine v, NSurface.HLine h): return new() { new Vec2(v.R, h.H) };
        }
        // lines as point + direction
        static (Vec2 p, Vec2 d)? AsLine(NSurface.Profile x) => x switch
        {
            NSurface.HLine h => (new Vec2(0, h.H), new Vec2(1, 0)),
            NSurface.VLine v => (new Vec2(v.R, 0), new Vec2(0, 1)),
            NSurface.SLine s => (s.P, s.Dir),
            _ => null,
        };
        var lp = AsLine(p); var lq = AsLine(q);
        if (lp != null && lq != null)
        {
            var (p1, d1) = lp.Value; var (p2, d2) = lq.Value;
            var den = Vec2.Cross(d1, d2);
            if (Math.Abs(den) < 1e-12)
            {
                // parallel: coincident or disjoint
                return Math.Abs(Vec2.Cross(d1, p2 - p1)) < tol ? null : new();
            }
            var t = Vec2.Cross(p2 - p1, d2) / den;
            return new() { p1 + d1 * t };
        }
        if (lp != null && q is NSurface.PCircle cq) return LineCircle(lp.Value.p, lp.Value.d, cq.C, cq.Rad, tol);
        if (lq != null && p is NSurface.PCircle cp) return LineCircle(lq.Value.p, lq.Value.d, cp.C, cp.Rad, tol);
        if (p is NSurface.PCircle a && q is NSurface.PCircle b)
        {
            if (Vec2.Distance(a.C, b.C) < tol && Math.Abs(a.Rad - b.Rad) < tol) return null;
            return CircleCircle(a.C, a.Rad, b.C, b.Rad, tol);
        }
        return new();
    }

    static List<Vec2> LineCircle(Vec2 p, Vec2 d, Vec2 c, double r, double tol)
    {
        d = d.Normalized;
        var t0 = Vec2.Dot(c - p, d);
        var foot = p + d * t0;
        var dist = Vec2.Distance(foot, c);
        if (Math.Abs(dist - r) < tol) return new() { foot };
        if (dist > r) return new();
        var w = Math.Sqrt(r * r - dist * dist);
        return new() { foot - d * w, foot + d * w };
    }

    public static List<Vec2> CircleCircle(Vec2 c1, double r1, Vec2 c2, double r2, double tol)
    {
        var d = Vec2.Distance(c1, c2);
        if (d < 1e-12) return new();
        var u = (c2 - c1) / d;
        if (Math.Abs(d - (r1 + r2)) < tol) return new() { c1 + u * r1 };                 // external tangency
        if (Math.Abs(d - Math.Abs(r1 - r2)) < tol) return new() { r1 >= r2 ? c1 + u * r1 : c1 - u * r1 }; // internal
        if (d > r1 + r2 || d < Math.Abs(r1 - r2)) return new();
        var a = (r1 * r1 - r2 * r2 + d * d) / (2 * d);
        var hh = Math.Sqrt(Math.Max(0, r1 * r1 - a * a));
        var m = c1 + (c2 - c1) * (a / d);
        var perp = (c2 - c1).Perp / d;
        return new() { m + perp * hh, m - perp * hh };
    }

    // ------------------------------------------------------------------------------------------
    // traced intersection inside a face's domain (general case)

    /// <summary>
    /// Traces SA ∩ SB inside the UV box of face A (marching squares on SB's implicit function evaluated on
    /// SA), then projects every point onto the exact intersection. Returns polylines of exact points.
    /// </summary>
    [ThreadStatic] static double[]? _grid;
    [ThreadStatic] static int[]? _gridStamp;
    [ThreadStatic] static int _stamp;

    public static List<NPolyline> Trace(NFace fa, NSurface sb, double tol, NDomain? domain = null)
    {
        // (domain: fa's domain at max(tol, 1e-3), when the caller has it)
        var dom = domain ?? NDomain.Of(fa, Math.Max(tol, 1e-3));
        var sa = fa.Surface;
        double du = dom.UMax - dom.UMin, dv = dom.VMax - dom.VMin;
        double mu = du * 0.02 + 1e-9, mv = dv * 0.02 + 1e-9;
        double u0 = dom.UMin - mu, u1 = dom.UMax + mu, v0 = dom.VMin - mv, v1 = dom.VMax + mv;
        const int nu = 96, nv = 96, block = 8;
        Vec2 UV(int i, int j) => new(u0 + (u1 - u0) * i / nu, v0 + (v1 - v0) * j / nv);
        // fine grid values are computed on demand: only blocks that may contain the curve are visited (in this
        // thread's grid, valid where stamped with this trace's number: nothing is allocated or cleared per trace)
        var g = _grid ??= new double[(nu + 1) * (nv + 1)];
        var have = _gridStamp ??= new int[(nu + 1) * (nv + 1)];
        if (++_stamp == int.MaxValue) { Array.Clear(have); _stamp = 1; }
        int stamp = _stamp;
        double G(int i, int j)
        {
            int k = i * (nv + 1) + j;
            if (have[k] == stamp) return g[k];
            var v = sb.F_(dom.Eval(UV(i, j)));
            have[k] = stamp;
            return g[k] = v == 0 ? 1e-300 : v;
        }
        var pg = Prof.Time("trace.grid");
        // a block can hold a zero only if |F(centre)| ≤ its 3D radius (F_ grows no faster than distance)
        int nb = nu / block;
        Span<bool> active = stackalloc bool[nb * nb];
        for (int bi = 0; bi < nb; bi++)
            for (int bj = 0; bj < nb; bj++)
            {
                int i0 = bi * block, j0 = bj * block, i1 = i0 + block, j1 = j0 + block;
                var c = dom.Eval(Vec2.Lerp(UV(i0, j0), UV(i1, j1), 0.5));
                double r = 0;
                int im = (i0 + i1) / 2, jm = (j0 + j1) / 2;
                for (int q = 0; q < 8; q++)
                {
                    var (ii, jj) = q switch { 0 => (i0, j0), 1 => (i1, j0), 2 => (i0, j1), 3 => (i1, j1), 4 => (im, j0), 5 => (im, j1), 6 => (i0, jm), _ => (i1, jm) };
                    r = Math.Max(r, Vec3.Distance(c, dom.Eval(UV(ii, jj))));
                }
                active[bi * nb + bj] = Math.Abs(sb.F_(c)) <= r * 1.25 + 1e-9;
            }
        pg.Dispose();
        var pm = Prof.Time("trace.march");
        // marching squares: crossing points on cell edges, keyed so neighbours share them
        var edgePts = new Dictionary<(int, int, int), Vec2>(); // (i, j, dir 0=u,1=v)
        bool Cross(int i, int j, int dir)
        {
            var key = (i, j, dir);
            if (edgePts.ContainsKey(key)) return true;
            double a = G(i, j), b = dir == 0 ? G(i + 1, j) : G(i, j + 1);
            if (a * b > 0) return false;
            var t = a / (a - b);
            var pa = UV(i, j); var pb = dir == 0 ? UV(i + 1, j) : UV(i, j + 1);
            edgePts[key] = Vec2.Lerp(pa, pb, t);
            return true;
        }
        var segs = new List<((int, int, int) a, (int, int, int) b)>();
        Span<(int, int, int)> keys = stackalloc (int, int, int)[4];
        for (int bi = 0; bi < nb; bi++)
            for (int bj = 0; bj < nb; bj++)
            {
                if (!active[bi * nb + bj]) continue;
                for (int i = bi * block; i < (bi + 1) * block; i++)
                    for (int j = bj * block; j < (bj + 1) * block; j++)
                    {
                        int n = 0;
                        if (Cross(i, j, 0)) keys[n++] = (i, j, 0);
                        if (Cross(i + 1, j, 1)) keys[n++] = (i + 1, j, 1);
                        if (Cross(i, j + 1, 0)) keys[n++] = (i, j + 1, 0);
                        if (Cross(i, j, 1)) keys[n++] = (i, j, 1);
                        if (n == 2) segs.Add((keys[0], keys[1]));
                        else if (n == 4)
                        {
                            // saddle: decide by the centre value
                            var c = sb.F_(dom.Eval(Vec2.Lerp(UV(i, j), UV(i + 1, j + 1), 0.5)));
                            if (c * G(i, j) > 0) { segs.Add((keys[0], keys[1])); segs.Add((keys[2], keys[3])); }
                            else { segs.Add((keys[0], keys[3])); segs.Add((keys[1], keys[2])); }
                        }
                    }
            }
        // chain segments
        var adj = new Dictionary<(int, int, int), List<(int, int, int)>>();
        foreach (var (a, b) in segs)
        {
            if (!adj.TryGetValue(a, out var la)) adj[a] = la = new(); la.Add(b);
            if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = new(); lb.Add(a);
        }
        pm.Dispose();
        using var pp = Prof.Time("trace.project");
        var used = new HashSet<((int, int, int), (int, int, int))>();
        var result = new List<NPolyline>();
        bool Use((int, int, int) a, (int, int, int) b) => used.Add((a, b)) & used.Add((b, a));
        foreach (var start in adj.Keys.OrderBy(k => adj[k].Count == 1 ? 0 : 1).ToList())
        {
            foreach (var first in adj[start].ToList())
            {
                if (used.Contains((start, first))) continue;
                var chain = new List<(int, int, int)> { start };
                var cur = first;
                Use(start, cur);
                while (true)
                {
                    chain.Add(cur);
                    if (cur == start) break;
                    var nexts = adj[cur].Where(n => !used.Contains((cur, n))).ToList();
                    if (nexts.Count == 0) break;
                    Use(cur, nexts[0]);
                    cur = nexts[0];
                }
                bool closed = chain.Count > 3 && chain[0] == chain[^1];
                var pts = new List<Vec3>();
                foreach (var k in chain) pts.Add(NSurface.ProjectToIntersection(sa, sb, dom.Eval(edgePts[k])));
                if (closed) pts.RemoveAt(pts.Count - 1);
                pts = Densify(pts, closed, sa, sb, tol);
                if (pts.Count >= 2) result.Add(new NPolyline(pts, closed));
            }
        }
        return result;
    }

    /// <summary>Inserts exact midpoints until the chord error is below tol.</summary>
    static List<Vec3> Densify(List<Vec3> pts, bool closed, NSurface a, NSurface b, double tol)
    {
        var res = new List<Vec3>();
        int n = pts.Count;
        int segs = closed ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            var p = pts[i]; var q = pts[(i + 1) % n];
            res.Add(p);
            Sub(p, q, 0);
        }
        if (!closed) res.Add(pts[^1]);
        return res;

        void Sub(Vec3 p, Vec3 q, int depth)
        {
            if (depth > 6) return;
            var m = NSurface.ProjectToIntersection(a, b, (p + q) * 0.5);
            if (Vec3.Distance(m, (p + q) * 0.5) < tol * 0.25 && depth > 0) return;
            if (Vec3.Distance(p, q) < tol) return;
            Sub(p, m, depth + 1);
            res.Add(m);
            Sub(m, q, depth + 1);
        }
    }
}
