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

    /// <summary>Parameters where the edge meets the surface (sign changes and tangential touches).</summary>
    public static EdgeHits EdgeSurface(NEdge e, NSurface s, double tol)
    {
        var c = e.Curve;
        double F(double t) => s.F_(c.Eval(t));
        var res = new List<double>();
        if (c is NLine && s.Kind == SurfaceKind.Plane)
        {
            double f0 = F(e.T0), f1 = F(e.T1);
            if (Math.Abs(f0) < tol && Math.Abs(f1) < tol) return new EdgeHits(res, true);
            if (Math.Abs(f0) < tol) res.Add(e.T0);
            if (Math.Abs(f1) < tol) res.Add(e.T1);
            if (f0 * f1 < 0 && Math.Abs(f0) >= tol && Math.Abs(f1) >= tol) res.Add(e.T0 + (e.T1 - e.T0) * f0 / (f0 - f1));
            return new EdgeHits(res, false);
        }
        int n = c is NLine ? 32 : Math.Clamp(e.Samples(tol * 50).Count * 4, 32, 2048);
        var ts = new double[n + 1]; var fs = new double[n + 1];
        bool allOn = true;
        for (int i = 0; i <= n; i++)
        {
            ts[i] = e.T0 + (e.T1 - e.T0) * i / n;
            fs[i] = F(ts[i]);
            if (Math.Abs(fs[i]) > tol) allOn = false;
        }
        if (allOn) return new EdgeHits(res, true);
        if (Math.Abs(fs[0]) < tol) res.Add(e.T0);
        if (Math.Abs(fs[n]) < tol) res.Add(e.T1);
        for (int i = 0; i < n; i++)
        {
            double a = fs[i], b = fs[i + 1];
            if (Math.Abs(a) < tol && i > 0) continue; // already handled as a near-zero sample below
            if (a * b < 0 && Math.Abs(b) >= tol)
            {
                var t = Roots.Bisect(F, ts[i], ts[i + 1]);
                res.Add(t);
            }
        }
        // near-zero interior samples: either a crossing landing on a sample or a tangential touch
        for (int i = 1; i < n; i++)
        {
            if (Math.Abs(fs[i]) >= tol * 50) continue;
            if (Math.Abs(fs[i]) > Math.Abs(fs[i - 1]) || Math.Abs(fs[i]) > Math.Abs(fs[i + 1])) continue;
            var t = Roots.MinAbs(F, ts[i - 1], ts[i + 1]);
            if (Math.Abs(F(t)) < tol && !res.Any(r => Math.Abs(r - t) < (e.T1 - e.T0) * 1e-6)) res.Add(t);
        }
        res.Sort();
        // merge duplicates
        var merged = new List<double>();
        foreach (var t in res) if (merged.Count == 0 || Vec3.Distance(c.Eval(t), c.Eval(merged[^1])) > tol) merged.Add(t);
        return new EdgeHits(merged, false);
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
    public static List<NPolyline> Trace(NFace fa, NSurface sb, double tol)
    {
        var dom = NDomain.Of(fa, Math.Max(tol, 1e-3));
        var sa = fa.Surface;
        double du = dom.UMax - dom.UMin, dv = dom.VMax - dom.VMin;
        double mu = du * 0.02 + 1e-9, mv = dv * 0.02 + 1e-9;
        double u0 = dom.UMin - mu, u1 = dom.UMax + mu, v0 = dom.VMin - mv, v1 = dom.VMax + mv;
        const int nu = 96, nv = 96, block = 8;
        Vec2 UV(int i, int j) => new(u0 + (u1 - u0) * i / nu, v0 + (v1 - v0) * j / nv);
        // fine grid values are computed on demand: only blocks that may contain the curve are visited
        var g = new double[nu + 1, nv + 1];
        var have = new bool[nu + 1, nv + 1];
        double G(int i, int j)
        {
            if (have[i, j]) return g[i, j];
            var v = sb.F_(dom.Eval(UV(i, j)));
            have[i, j] = true;
            return g[i, j] = v == 0 ? 1e-300 : v;
        }
        var pg = Prof.Time("trace.grid");
        // a block can hold a zero only if |F(centre)| ≤ its 3D radius (F_ grows no faster than distance)
        int nb = nu / block;
        var active = new bool[nb, nb];
        for (int bi = 0; bi < nb; bi++)
            for (int bj = 0; bj < nb; bj++)
            {
                int i0 = bi * block, j0 = bj * block, i1 = i0 + block, j1 = j0 + block;
                var c = dom.Eval(Vec2.Lerp(UV(i0, j0), UV(i1, j1), 0.5));
                double r = 0;
                foreach (var (ii, jj) in new[] { (i0, j0), (i1, j0), (i0, j1), (i1, j1), ((i0 + i1) / 2, j0), ((i0 + i1) / 2, j1), (i0, (j0 + j1) / 2), (i1, (j0 + j1) / 2) })
                    r = Math.Max(r, Vec3.Distance(c, dom.Eval(UV(ii, jj))));
                active[bi, bj] = Math.Abs(sb.F_(c)) <= r * 1.25 + 1e-9;
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
                if (!active[bi, bj]) continue;
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
