namespace SpryCad.Kernel.Native;

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
        var count = Prof.Enabled;
        // (counted while profiling: the evaluations are the cost, a closest-point search each on a pipe or B-spline)
        double F(double t) { if (count) Prof.Count("edgesurface.eval", 1); return s.F_(c.Eval(t)); }
        double lo = Math.Min(e.T0, e.T1), hi = Math.Max(e.T0, e.T1);
        var res = new List<double>();

        // a helix about a coaxial cylinder, or on a screw of its own motion, keeps its distance all the way: on the
        // surface everywhere or nowhere (one point tells; the probe below would search the screw seventeen times)
        if (c is NHelix hx && Math.Abs(hx.Motion.K) > 1e-12
            && (s.Kind == SurfaceKind.Cylinder && Vec3.Cross(s.Def.Axis.Normalized, hx.Motion.A).Length < 1e-12 && Vec3.Cross(s.Def.Origin - hx.Motion.O, hx.Motion.A).Length < tol
                || s.Kind == SurfaceKind.Screw && s.Screw!.Motion.SameHelices(hx.Motion)))
        {
            // (on a screw, past its sweep the helix leaves it: the ends and middle all on it)
            bool on = Math.Abs(F(0.5 * (lo + hi))) <= tol && (s.Kind == SurfaceKind.Cylinder || Math.Abs(F(lo)) <= tol && Math.Abs(F(hi)) <= tol);
            return new EdgeHits(res, on);
        }

        // edge lying on the surface (fitted curves only within their fit tolerance: a boolean fits traced
        // sections within 50 tol of the exact intersection)
        const int probe = 16;
        bool allOn = true;
        var onTol = c is NSpline or NPolyline ? Math.Max(tol * 100, c is NSpline ns ? ns.FitTol * 2 : 0) : tol;
        // each value worked out once: the probe's points, the root search's first ones (every other probe point: the
        // same parameters, as scaling by 2, 8 and 16 is exact) and the ends (the probe's last point is lo + (hi − lo),
        // which need not be hi to the last bit: the hi end is its own)
        Span<double> probed = stackalloc double[probe + 1];
        probed.Fill(double.NaN);
        double Probed(int i, Span<double> pv) => double.IsNaN(pv[i]) ? pv[i] = F(lo + (hi - lo) * i / probe) : pv[i];
        for (int i = 0; i <= probe && allOn; i++) if (Math.Abs(Probed(i, probed)) > onTol) allOn = false;
        if (allOn) return new EdgeHits(res, true);

        bool searched = false;
        double fLo = Probed(0, probed), fHi = F(hi);
        double fT0 = e.T0 == lo ? fLo : fHi, fT1 = e.T1 == lo ? fLo : fHi;
        if (Math.Abs(fT0) < tol) res.Add(e.T0);
        if (Math.Abs(fT1) < tol) res.Add(e.T1);
        var exact = AnalyticRoots(c, s, tol) ?? (s.Kind == SurfaceKind.Screw ? ScrewRoots(c, s.Screw!, lo, hi, tol) : null);
        if (exact != null)
        {
            foreach (var t0 in exact)
            {
                var t = t0;
                if (c.Periodic) { while (t < lo - 1e-12) t += c.Period; while (t > hi + 1e-12) t -= c.Period; }
                if (t >= lo - 1e-12 && t <= hi + 1e-12) res.Add(Math.Clamp(t, lo, hi));
            }
        }
        else if (s.Kind == SurfaceKind.Screw)
        {
            // a screw surface's signed distance jumps between its turns (no Lipschitz bound to prune with): sign changes
            // on a fine scan (an eighth of the lead or of the radius), each checked to lie on the surface
            var sc = s.Screw!;
            var step = Math.Max(1e-9, Math.Min(Math.Abs(sc.Motion.Lead) > 0 ? Math.Abs(sc.Motion.Lead) / 8 : double.MaxValue, Math.Max(sc.MaxRadius, 1e-3) / 8));
            int n = Math.Clamp((int)Math.Ceiling(ArcBound(c, lo, hi) / step), 16, 20000);
            double pt = lo, pf = F(lo), zeroSign = 0;
            for (int i = 1; i <= n; i++)
            {
                var t = lo + (hi - lo) * i / n; var f = F(t);
                if (pf * f < 0)
                {
                    var r = Roots.Bisect(F, pt, t);
                    if (Math.Abs(F(r)) < tol * 1e3) res.Add(r);
                }
                // a crossing exactly at a sample (the scan steps are round fractions of the edge, like the crossings)
                if (pf == 0 && i > 1 && zeroSign * f < 0) res.Add(pt);
                if (f == 0 && pf != 0) zeroSign = pf;
                pt = t; pf = f;
            }
        }
        else
        {
            searched = true;
            double total = ArcBound(c, lo, hi);
            // leaves no shorter than this: two roots closer together are a tangential touch, which does not split
            // edges anyway, and an edge running alongside a surface would otherwise be subdivided into 1e5 pieces
            double minL = Math.Max(total * 1e-5, tol * 1000);
            // the sign before a zero sample (a crossing exactly at a subdivision point, e.g. a helix crossing a plane
            // at a whole turn): the leaves on either side of it meet there, in order (neither can be pruned)
            double zeroT = double.NaN, zeroSign = 0;
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
                        var r = Roots.Bracketed(F, ta, tb);
                        if (s.Kind != SurfaceKind.Screw || Math.Abs(F(r)) < tol * 1e3) res.Add(r);
                    }
                    if (fa == 0 && ta == zeroT && zeroSign * fb < 0) res.Add(ta);
                    if (fb == 0 && fa != 0) { zeroT = tb; zeroSign = fa; }
                    return;
                }
                var m = 0.5 * (ta + tb); var fm = F(m);
                // an edge grazing the surface: the distance changes far slower than the arc length, and the bound
                // above cannot rule out a root next to a crossing until the pieces are tiny (thousands of evaluations
                // for one crossing at a shallow angle). The parabola through the three values: on one side, and
                // clear of zero by more than the bend it shows (4 times its second difference), the piece holds no
                // root unless the distance bends far more sharply between the samples than across them.
                // a piece lying on the surface (ends and middle within tol of it, e.g. a fillet's boundary running along
                // the B-spline it blends into): the sign there is rounding, and a run along the surface is at most one
                // crossing, settled from either side of it (OneCrossingPerRun); where the edge leaves the surface, a
                // piece with an end off it is still searched
                if (depth >= 2 && Math.Abs(fa) < tol && Math.Abs(fm) < tol && Math.Abs(fb) < tol) return;
                if (depth >= 2 && fa * fm > 0 && fm * fb > 0)
                {
                    double d2 = fa - 2 * fm + fb, lowest = Math.Min(Math.Min(Math.Abs(fa), Math.Abs(fm)), Math.Abs(fb));
                    if (d2 != 0)
                    {
                        var x = (fa - fb) / (2 * d2);
                        if (Math.Abs(x) < 1)
                        {
                            var ext = fm - (fb - fa) * (fb - fa) / (8 * d2);
                            lowest = ext * fm <= 0 ? 0 : Math.Min(lowest, Math.Abs(ext));
                        }
                    }
                    if (lowest > 4 * Math.Abs(d2) + tol) return;
                }
                Iso(ta, m, fa, fm, depth + 1);
                Iso(m, tb, fm, fb, depth + 1);
            }
            // start from a few segments so the arc bound of curved edges stays tight
            const int n = 8; double prevT = lo, prevF = fLo;
            for (int i = 1; i <= n; i++)
            {
                var t = lo + (hi - lo) * i / n; var f = Probed(i * (probe / n), probed);
                Iso(prevT, t, prevF, f, 0);
                prevT = t; prevF = f;
            }
        }
        res.Sort();
        // merge duplicates; an end point lying on the surface absorbs crossings right next to it (no micro edges)
        bool startOn = Math.Abs(fT0) < tol, endOn = Math.Abs(fT1) < tol;
        Vec3 p0 = e.V0.P, p1 = e.V1.P;
        var merged = new List<double>();
        foreach (var t in res)
        {
            var p = c.Eval(t);
            bool isEnd = t == e.T0 || t == e.T1;
            if (!isEnd && ((startOn && Vec3.Distance(p, p0) < tol * 100) || (endOn && Vec3.Distance(p, p1) < tol * 100))) continue;
            if (merged.Count == 0 || Vec3.Distance(p, c.Eval(merged[^1])) > tol) merged.Add(t);
        }
        if (searched && merged.Count >= 2) merged = OneCrossingPerRun(merged);
        // A stretch where the edge runs along the surface (within tol of it) crosses it at most once: the sign of the
        // distance there is rounding, and the search found "crossings" all along it (126 in 14 mm of a line lying on
        // a pipe), each a split of the edge. Crossings with the edge on the surface between them are one run: the run
        // is a crossing (in its middle) if the edge is on opposite sides just before and after it, a touch if not; a
        // run reaching an end of the edge keeps that end (its vertex is on the surface).
        List<double> OneCrossingPerRun(List<double> ts)
        {
            var kept = new List<double>();
            for (int i = 0; i < ts.Count;)
            {
                int j = i;
                while (j + 1 < ts.Count && Math.Abs(F(0.5 * (ts[j] + ts[j + 1]))) < tol) j++;
                if (j == i) { kept.Add(ts[i]); i++; continue; }
                bool hasEnd = false;
                for (int k = i; k <= j; k++) if (ts[k] == e.T0 || ts[k] == e.T1) { kept.Add(ts[k]); hasEnd = true; }
                if (!hasEnd)
                {
                    // the sides just outside the run: halfway to the crossing before it (or the start), and after it
                    double before = 0.5 * ((i > 0 ? ts[i - 1] : lo) + ts[i]), after = 0.5 * (ts[j] + (j + 1 < ts.Count ? ts[j + 1] : hi));
                    if (F(before) * F(after) < 0) kept.Add(ts[(i + j) / 2]);
                }
                i = j + 1;
            }
            return kept;
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
    /// <summary>
    /// Where a line along the screw's axis, a circle about it, or a line square to it through it crosses the screw
    /// surface, from the generator alone (no closest-point searches). The motion keeps a point's distance r from the
    /// axis, turns it by u and raises it K·u; a generator point at v (radius r(v), angle a(v), height z(v)):
    /// <list type="bullet">
    /// <item>a line along the axis at radius d, angle φ: the points with r(v) = d, at the turns u = φ − a(v) + 2πk;</item>
    /// <item>a circle about the axis at height H, radius R: the points with r(v) = R, at u = (H − z(v)) / K;</item>
    /// <item>a line through the axis square to it at height H: the v where the point raised to H, at angle
    /// a(v) + (H − z(v)) / K, lies on the line's direction (or the opposite one), at t = foot ± r(v).</item>
    /// </list>
    /// Generator parameters a little past the swept part, as for the surface's other closed forms. Null for anything
    /// else (the caller scans).
    /// </summary>
    static List<double>? ScrewRoots(NCurve c, ScrewSurface sc, double lo, double hi, double tol)
    {
        var m = sc.Motion;
        if (Math.Abs(m.K) < 1e-12) return null;
        // a helix of the same motion lies on the surface or beside it all the way (on it: found by the caller's probe)
        if (c is NHelix hx && m.SameHelices(hx.Motion)) return new List<double>();
        double pad = (sc.V1 - sc.V0) * 0.01 + 1e-9, v0 = sc.V0 - pad, v1 = sc.V1 + pad;
        const int n = 256;
        Vec3 G(double v) => m.Local(sc.Gen.Eval(v));
        // the v where f changes sign (not across a jump of f: angles wrap by 2π)
        List<double> SignChanges(Func<double, double> f, double jump)
        {
            var r = new List<double>();
            double pv = v0, pf = f(v0);
            for (int i = 1; i <= n; i++)
            {
                var v = v0 + (v1 - v0) * i / n; var fv = f(v);
                if (pf == 0) r.Add(pv);
                else if (pf * fv < 0 && Math.Abs(fv - pf) < jump) r.Add(Roots.Bisect(f, pv, v));
                pv = v; pf = fv;
            }
            if (pf == 0) r.Add(pv);
            return r;
        }
        double Radius(Vec3 l) => Math.Sqrt(l.X * l.X + l.Y * l.Y);
        double Wrap(double a) => Math.IEEERemainder(a, 2 * Math.PI);
        if (c is NLine ln)
        {
            var la = m.Local(ln.P); var dz = Vec3.Dot(ln.D, m.A);
            // farther from the axis than the surface reaches (a nut's hex edges about its thread): nothing
            var cr = Vec3.Cross(ln.D, m.A);
            if (cr.Length > 1e-12 && Math.Abs(Vec3.Dot(ln.P - m.O, cr.Normalized)) > sc.MaxRadius * 1.01 + tol) return new List<double>();
            if (Math.Abs(Math.Abs(dz) - 1) < 1e-12)
            {
                // along the axis
                var d = Radius(la); var phi = Math.Atan2(la.Y, la.X);
                var res = new List<double>();
                foreach (var v in SignChanges(v => Radius(G(v)) - d, double.MaxValue))
                {
                    var g = G(v);
                    var u0 = phi - Math.Atan2(g.Y, g.X);
                    // t = dz·(z(v) + K·(u0 + 2πk) − z_line), for the k landing in [lo, hi]
                    double t0 = dz * (g.Z + m.K * u0 - la.Z), step = dz * m.K * 2 * Math.PI;
                    double ka = (lo - t0) / step, kb = (hi - t0) / step;
                    long k0 = (long)Math.Ceiling(Math.Min(ka, kb) - 1e-9), k1 = (long)Math.Floor(Math.Max(ka, kb) + 1e-9);
                    if (k1 - k0 > 100000) return null;
                    for (long k = k0; k <= k1; k++) res.Add(t0 + step * k);
                }
                return res;
            }
            if (Math.Abs(dz) < 1e-12)
            {
                // square to the axis: through it?
                var dl = new Vec3(Vec3.Dot(ln.D, m.X), Vec3.Dot(ln.D, m.Y), 0);
                var tf = -(la.X * dl.X + la.Y * dl.Y); // the foot of the axis on the line
                var foot = new Vec3(la.X + dl.X * tf, la.Y + dl.Y * tf, la.Z);
                if (Radius(foot) > tol) return null;
                var phi = Math.Atan2(dl.Y, dl.X); var H = la.Z;
                double Ang(double v) { var g = G(v); return Math.Atan2(g.Y, g.X) + (H - g.Z) / m.K; }
                var res = new List<double>();
                foreach (var (off, sign) in new[] { (0.0, 1.0), (Math.PI, -1.0) })
                    foreach (var v in SignChanges(v => Wrap(Ang(v) - phi - off), Math.PI))
                        res.Add(tf + sign * Radius(G(v)));
                return res;
            }
            return null;
        }
        if (c is NCircle ci && Math.Abs(Math.Abs(Vec3.Dot(ci.Axis, m.A)) - 1) < 1e-12)
        {
            var lc = m.Local(ci.C);
            if (Radius(lc) > tol) return null;
            var res = new List<double>();
            foreach (var v in SignChanges(v => Radius(G(v)) - ci.R, double.MaxValue))
            {
                var g = sc.Gen.Eval(v);
                var p = m.Move((lc.Z - m.Local(g).Z) / m.K, g);
                res.Add(ci.Param(p));
            }
            return res;
        }
        return null;
    }

    static List<double>? AnalyticRoots(NCurve c, NSurface s, double tol)
    {
        if (c is NHelix hx && Math.Abs(hx.Motion.K) > 1e-12)
        {
            var m = hx.Motion;
            // a coaxial cylinder: the helix keeps its radius, on it all the way (the caller's probe) or beside it
            if (s.Kind == SurfaceKind.Cylinder && Vec3.Cross(s.Def.Axis.Normalized, m.A).Length < 1e-12 && Vec3.Cross(s.Def.Origin - m.O, m.A).Length < tol)
                return new List<double>();
            // a plane square to the axis: the height rises K a radian
            if (s.Kind == SurfaceKind.Plane && Vec3.Cross(s.Plane!.Normal, m.A).Length < 1e-12)
            {
                var sgn = Vec3.Dot(s.Plane.Normal, m.A) > 0 ? 1 : -1;
                return new List<double> { (sgn * s.Plane.D - Vec3.Dot(m.A, hx.P0)) / m.K };
            }
        }
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
    /// <paramref name="chordTol"/>: the chord error of curves given as exact points (a screw surface across a coaxial
    /// plane or cone), as for traced curves (default 25 × <paramref name="tol"/>).
    /// </summary>
    public static List<NCurve>? Analytic(NSurface a, NSurface b, double tol, double chordTol = 0)
    {
        if (chordTol <= 0) chordTol = 25 * tol;
        if (a.Kind == SurfaceKind.Plane && b.Kind == SurfaceKind.Plane) return PlanePlane(a.Plane!, b.Plane!);
        if (a.Kind == SurfaceKind.Pipe) return PipeOther(a, b, tol);
        if (b.Kind == SurfaceKind.Pipe) return PipeOther(b, a, tol);
        if (a.Kind == SurfaceKind.Screw) return ScrewOther(a, b, tol, chordTol);
        if (b.Kind == SurfaceKind.Screw) return ScrewOther(b, a, tol, chordTol);

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
        if (a.Kind == SurfaceKind.Plane && b.Kind == SurfaceKind.Cone) return PlaneCone(a.Plane!, b, tol);
        if (b.Kind == SurfaceKind.Plane && a.Kind == SurfaceKind.Cone) return PlaneCone(b.Plane!, a, tol);
        if (a.Kind == SurfaceKind.Plane && b.Kind == SurfaceKind.Torus) return PlaneTorus(a.Plane!, b, tol, chordTol);
        if (b.Kind == SurfaceKind.Plane && a.Kind == SurfaceKind.Torus) return PlaneTorus(b.Plane!, a, tol, chordTol);
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
    /// the cylinder's radius, each such generator point carried by the motion. A plane square to the axis or a cone
    /// on it: one curve, each generator point meeting it at one turn (<see cref="ScrewAcross"/>). Anything else:
    /// traced (null).
    /// </summary>
    static List<NCurve>? ScrewOther(NSurface screw, NSurface o, double tol, double chordTol)
    {
        // a plane along the axis farther from it than the surface reaches (a nut's flats about its thread): nothing
        if (o.Kind == SurfaceKind.Plane && Math.Abs(Vec3.Dot(o.Plane!.Normal, screw.Screw!.Motion.A)) < 1e-12
            && Math.Abs(o.Plane.Distance(screw.Screw.Motion.O)) > screw.Screw.MaxRadius * 1.01 + tol)
            return new List<NCurve>();
        if (o.Kind == SurfaceKind.Screw) return ScrewScrew(screw.Screw!, o.Screw!, tol);
        // a plane through the axis, the generator in a plane through it too (thread and coil profiles): the generator
        // carried round to the plane, each half turn of the sweep (a sweep's end caps against another sweep)
        if (o.Kind == SurfaceKind.Plane && Math.Abs(screw.Screw!.Motion.K) > 1e-12)
        {
            var ss = screw.Screw; var mo = ss.Motion; var pl = o.Plane!;
            if (Math.Abs(Vec3.Dot(pl.Normal, mo.A)) < 1e-12 && Math.Abs(pl.Distance(mo.O)) < tol)
            {
                double? thetaG = null; bool planar = true;
                for (int i = 0; i <= 16 && planar; i++)
                {
                    var l = mo.Local(ss.Gen.Eval(ss.V0 + (ss.V1 - ss.V0) * i / 16));
                    if (Math.Sqrt(l.X * l.X + l.Y * l.Y) < 1e3 * tol) continue;
                    var th = Math.Atan2(l.Y, l.X);
                    thetaG ??= th;
                    // the same half plane, or the opposite one (the generator crossing the axis's line)
                    if (Math.Abs(Math.IEEERemainder(th - thetaG.Value, Math.PI)) > 1e-9) planar = false;
                }
                if (planar && thetaG is { } tg)
                {
                    var d = Vec3.Cross(pl.Normal, mo.A);
                    var phi = Math.Atan2(Vec3.Dot(d, mo.Y), Vec3.Dot(d, mo.X));
                    double upad = (ss.U1 - ss.U0) * 0.01 + 1e-9;
                    var base0 = phi - tg;
                    long k0 = (long)Math.Ceiling((ss.U0 - upad - base0) / Math.PI), k1 = (long)Math.Floor((ss.U1 + upad - base0) / Math.PI);
                    if (k1 - k0 <= 10000)
                    {
                        var copies = new List<NCurve>();
                        for (long k = k0; k <= k1; k++) copies.Add(ss.Gen.Transformed(mo.At(base0 + k * Math.PI)));
                        return copies;
                    }
                }
            }
        }
        if (o.Kind is SurfaceKind.Plane or SurfaceKind.Cone)
            return Math.Abs(screw.Screw!.Motion.K) > 1e-12 && o.ProfileAbout(screw.Screw.Motion.O, screw.Screw.Motion.A, tol) is { } prof
                ? ScrewAcross(screw.Screw, prof, chordTol) : null;
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

    /// <summary>
    /// Two screw surfaces of the same helices (the same axis, lead and hand: a multi-start thread's grooves, a
    /// thread patterned about its axis). A point is fixed on its helix by its distance r from the axis and its height
    /// carried back along it, ζ = z − K·θ (modulo the lead); each surface is its generator's curve in (r, ζ), and the
    /// surfaces meet along the helices through the points where the two curves cross. Null when the motions differ
    /// or the curves run along each other (traced, or coincident).
    /// </summary>
    static List<NCurve>? ScrewScrew(ScrewSurface a, ScrewSurface b, double tol)
    {
        var m = a.Motion;
        if (Math.Abs(m.K) < 1e-12) return null;
        var lead = Math.Abs(2 * Math.PI * m.K);
        double R(Vec3 p) { var l = m.Local(p); return Math.Sqrt(l.X * l.X + l.Y * l.Y); }
        double Zeta(Vec3 p) { var l = m.Local(p); return l.Z - m.K * Math.Atan2(l.Y, l.X); }
        double Wrap(double z) => Math.IEEERemainder(z, lead);
        // b's motion must carry its points along a's helices
        foreach (var v in new[] { b.V0, 0.5 * (b.V0 + b.V1), b.V1 })
        {
            var p = b.Gen.Eval(v);
            if (R(p) < 1e3 * tol) continue;
            foreach (var u in new[] { 0.7, -2.1 })
            {
                var q = b.Motion.Move(u, p);
                if (Math.Abs(R(q) - R(p)) > tol || Math.Abs(Wrap(Zeta(q) - Zeta(p))) > tol) return null;
            }
        }
        // each generator as a curve in (r, ζ), ζ unwrapped along it; a little past the swept part, as elsewhere
        const int n = 96;
        (double[] v, Vec2[] q) Curve(ScrewSurface s)
        {
            double pad = (s.V1 - s.V0) * 0.01 + 1e-9, v0 = s.V0 - pad, v1 = s.V1 + pad;
            var vs = new double[n + 1]; var qs = new Vec2[n + 1];
            for (int i = 0; i <= n; i++)
            {
                vs[i] = v0 + (v1 - v0) * i / n;
                var p = s.Gen.Eval(vs[i]);
                var z = Zeta(p);
                if (i > 0) z = qs[i - 1].Y + Wrap(z - qs[i - 1].Y);
                qs[i] = new Vec2(R(p), z);
            }
            return (vs, qs);
        }
        var (va, qa) = Curve(a); var (vb, qb) = Curve(b);
        // a crossing of a's and b's curves (b shifted by whole leads), refined on both generators
        (double va, double vb)? Refine(double x, double y)
        {
            double Dz(double p, double q) => Wrap(Zeta(a.Gen.Eval(p)) - Zeta(b.Gen.Eval(q)));
            for (int it = 0; it < 40; it++)
            {
                double f1 = R(a.Gen.Eval(x)) - R(b.Gen.Eval(y)), f2 = Dz(x, y);
                if (Math.Abs(f1) < 1e-13 && Math.Abs(f2) < 1e-13) break;
                double h = 1e-7 * (1 + Math.Abs(a.V1 - a.V0)), g = 1e-7 * (1 + Math.Abs(b.V1 - b.V0));
                double a11 = (R(a.Gen.Eval(x + h)) - R(a.Gen.Eval(x - h))) / (2 * h), a12 = -(R(b.Gen.Eval(y + g)) - R(b.Gen.Eval(y - g))) / (2 * g);
                double a21 = (Dz(x + h, y) - Dz(x - h, y)) / (2 * h), a22 = (Dz(x, y + g) - Dz(x, y - g)) / (2 * g);
                var det = a11 * a22 - a12 * a21;
                if (Math.Abs(det) < 1e-300) return null;
                x -= (a22 * f1 - a12 * f2) / det; y -= (a11 * f2 - a21 * f1) / det;
            }
            var pa = a.Gen.Eval(x); var pb = b.Gen.Eval(y);
            return Math.Abs(R(pa) - R(pb)) < tol && Math.Abs(Wrap(Zeta(pa) - Zeta(pb))) < tol ? (x, y) : null;
        }
        var res = new List<NCurve>();
        var found = new List<Vec2>();
        double zaMin = qa.Min(q => q.Y), zaMax = qa.Max(q => q.Y), zbMin = qb.Min(q => q.Y), zbMax = qb.Max(q => q.Y);
        int k0 = (int)Math.Floor((zaMin - zbMax) / lead) - 1, k1 = (int)Math.Ceiling((zaMax - zbMin) / lead) + 1;
        for (int k = k0; k <= k1; k++)
            for (int i = 0; i < n; i++)
            {
                Vec2 p0 = qa[i], p1 = qa[i + 1], d1 = p1 - p0;
                for (int j = 0; j < n; j++)
                {
                    Vec2 s0 = qb[j] + new Vec2(0, k * lead), s1 = qb[j + 1] + new Vec2(0, k * lead), d2 = s1 - s0;
                    var den = Vec2.Cross(d1, d2);
                    var w = s0 - p0;
                    if (Math.Abs(den) < 1e-14 * (d1.Length * d2.Length + 1e-300))
                    {
                        // parallel: running along each other (coincident in part) is not a crossing of curves
                        if (Math.Abs(Vec2.Cross(d1, w)) < tol * d1.Length && d1.Length > 0
                            && Math.Max(Vec2.Dot(w, d1), Vec2.Dot(s1 - p0, d1)) >= 0 && Math.Min(Vec2.Dot(w, d1), Vec2.Dot(s1 - p0, d1)) <= d1.LengthSq)
                            return null;
                        continue;
                    }
                    double sa = Vec2.Cross(w, d2) / den, sb = Vec2.Cross(w, d1) / den;
                    if (sa < -1e-9 || sa > 1 + 1e-9 || sb < -1e-9 || sb > 1 + 1e-9) continue;
                    var hit = Refine(va[i] + (va[i + 1] - va[i]) * sa, vb[j] + (vb[j + 1] - vb[j]) * sb);
                    if (hit is not { } h) continue;
                    var pa = a.Gen.Eval(h.va);
                    var key = new Vec2(R(pa), Wrap(Zeta(pa)));
                    if (found.Any(f => Math.Abs(f.X - key.X) < tol && Math.Abs(Wrap(f.Y - key.Y)) < tol)) continue;
                    found.Add(key);
                    res.Add(new NHelix(m, pa));
                }
            }
        return res;
    }

    /// <summary>
    /// A screw surface across a coaxial plane (h = H) or cone (a line in the (r, h) half plane): the motion keeps a
    /// point's distance r from the axis and raises it K·u, so the generator point at v (radius r, height z) meets
    /// it at the one turn u = (h(r) − z) / K. The curve as exact points, halved in v to the chord tolerance: no
    /// marching, no projections. Null when the cone's line runs parallel to the axis (a cylinder: helices).
    /// </summary>
    static List<NCurve>? ScrewAcross(ScrewSurface sc, NSurface.Profile prof, double chordTol)
    {
        var m = sc.Motion;
        if (prof is NSurface.SLine sl0 && Math.Abs(sl0.Dir.X) < 1e-12) return null;
        Vec3 At(double v)
        {
            var g = sc.Gen.Eval(v);
            var l = m.Local(g);
            var r = Math.Sqrt(l.X * l.X + l.Y * l.Y);
            double h = prof switch
            {
                NSurface.HLine hl => hl.H,
                NSurface.SLine sl => sl.P.Y + (r - sl.P.X) / sl.Dir.X * sl.Dir.Y,
                _ => throw new InvalidOperationException(),
            };
            return m.Move((h - l.Z) / m.K, g);
        }
        // the generator a little past the part that was swept (crossings at its very ends), as for helices
        double pad = (sc.V1 - sc.V0) * 0.01 + 1e-9, v0 = sc.V0 - pad, v1 = sc.V1 + pad;
        var pts = new List<Vec3>();
        void Sub(double va, Vec3 pa, double vb, Vec3 pb, int depth)
        {
            if (depth > 14) return;
            var vm = 0.5 * (va + vb); var pm = At(vm);
            if (depth > 0 && Vec3.Distance(pm, (pa + pb) * 0.5) < chordTol) return;
            Sub(va, pa, vm, pm, depth + 1);
            pts.Add(pm);
            Sub(vm, pm, vb, pb, depth + 1);
        }
        const int n0 = 16;
        var prev = At(v0);
        for (int i = 0; i < n0; i++)
        {
            double va = v0 + (v1 - v0) * i / n0, vb = v0 + (v1 - v0) * (i + 1) / n0;
            var pb = At(vb);
            pts.Add(prev);
            Sub(va, prev, vb, pb, 0);
            prev = pb;
        }
        pts.Add(prev);
        return new List<NCurve> { new NPolyline(pts, false) };
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

    /// <summary>
    /// A plane across a cone (one nappe: r = R + z·tan(half) ≥ 0, as the surface is): a conic. In the plane's frame
    /// about the apex's foot (x along the axis's slant in the plane, y across it, d the apex's distance, β the angle
    /// between the plane's normal and the nappe's axis, α the half angle) the cone is (x sin β − d cos β)² =
    /// cos²α (x² + y² + d²): with A = sin²β − cos²α, A (x − x₀)² − cos²α y² = d² cos²α sin²α / A, x₀ = d sin β cos β / A,
    /// an ellipse (A &lt; 0), a hyperbola (A &gt; 0: the branch on this nappe) or, A = 0, a parabola. Planes square to the
    /// axis (circles) are the coaxial case, through the apex lines (<see cref="ApexLines"/>); nearly a parabola
    /// without being one: traced (null), as is anything that does not check out on both surfaces.
    /// </summary>
    static List<NCurve>? PlaneCone(Plane pl, NSurface cone, double tol)
    {
        var tanH = Math.Tan(cone.R2);
        if (Math.Abs(tanH) < 1e-12) return null;
        var apex = cone.Origin - cone.Axis * (cone.R / tanH);
        var w = tanH > 0 ? cone.Axis : -cone.Axis; // into the nappe
        var n = pl.Normal;
        var d = pl.Distance(apex);
        double alpha = Math.Abs(cone.R2), ca = Math.Cos(alpha), sa = Math.Sin(alpha), c2 = ca * ca;
        var cosb = Vec3.Dot(n, w);
        if (Math.Abs(d) < tol) return ApexLines(apex, w, n, cosb, ca, sa, tol);
        var e1v = w - n * cosb; var sinb = e1v.Length;
        if (sinb < 1e-9) return null;
        var e1 = e1v / sinb; var e2 = Vec3.Cross(n, e1);
        var foot = apex - n * d;
        bool OnNappe(Vec3 q) => Vec3.Dot(q - apex, w) > 0;
        var A = sinb * sinb - c2;
        var res = new List<NCurve>();
        if (Math.Abs(A) < 1e-12)
        {
            // x = x_v − (tan β / 2d) y²: a parabola opening away from the slant's side, focal distance |d| / (2 |tan β|)
            var tanb = sinb / cosb;
            var xv = d * (cosb * cosb - sinb * sinb) / (2 * sinb * cosb);
            var k = tanb / (2 * d);
            var vertex = foot + e1 * xv;
            if (OnNappe(vertex)) res.Add(new NParabola(vertex, e1 * -Math.Sign(k), e2, 1 / (4 * Math.Abs(k))));
        }
        else if (Math.Abs(A) < 1e-6) return null;
        else
        {
            var x0 = d * sinb * cosb / A;
            var center = foot + e1 * x0;
            if (A < 0)
            {
                double ax = Math.Abs(d) * ca * sa / -A, ay = Math.Abs(d) * sa / Math.Sqrt(-A);
                if (OnNappe(center)) res.Add(ax >= ay ? new NEllipse(center, e1, e2, ax, ay) : new NEllipse(center, e2, -e1, ay, ax));
            }
            else
            {
                double a = Math.Abs(d) * ca * sa / A, b = Math.Abs(d) * sa / Math.Sqrt(A);
                foreach (var side in new[] { 1.0, -1.0 })
                    if (OnNappe(center + e1 * (side * a))) res.Add(new NHyperbola(center, e1 * side, e2, a, b));
            }
        }
        // on both surfaces (a few points across each curve's usual range), or traced after all
        foreach (var c in res)
            for (int i = 0; i <= 8; i++)
            {
                var p = c.Eval(c.Periodic ? i * Math.PI / 4 : -2 + i * 0.5);
                if (Math.Abs(pl.Distance(p)) > tol * 1e-2 || Math.Abs(cone.F_(p)) > tol * 1e-2) return null;
            }
        return res;
    }

    /// <summary>
    /// A plane through a cone's apex (a radial plane of a chamfered gear, a keyway's wall): the nappe's generators
    /// g = cos α w + sin α (cos θ f₁ + sin θ f₂) in it, f₁ the plane's normal square to the axis w, where
    /// cos α cos β + sin α sin β cos θ = 0, so cos θ = −cot α cot β: two lines from the apex, or none. Square to the
    /// axis (the apex alone) none; touching along one generator: traced (null).
    /// </summary>
    static List<NCurve>? ApexLines(Vec3 apex, Vec3 w, Vec3 n, double cosb, double ca, double sa, double tol)
    {
        var res = new List<NCurve>();
        var f1v = n - w * cosb; var sinb = f1v.Length;
        if (sinb < 1e-12) return res;
        var f1 = f1v / sinb; var f2 = Vec3.Cross(w, f1);
        var cth = -ca * cosb / (sa * sinb);
        if (Math.Abs(Math.Abs(cth) - 1) < 1e-9) return null;
        if (Math.Abs(cth) > 1) return res;
        var sth = Math.Sqrt(1 - cth * cth);
        foreach (var s in new[] { sth, -sth })
            res.Add(new NLine(apex, w * ca + (f1 * cth + f2 * s) * sa));
        return res;
    }

    /// <summary>
    /// A plane across a torus (centre O, axis A, radii R &gt; r): on the meridian at u (e(u) = X cos u + Y sin u) a
    /// point O + (R + r cos v) e + r sin v A lies on the plane n·p = k where a cos v + b sin v = −c, with s = n·e(u),
    /// a = r s, b = r (n·A), c = n·O − k + R s: v = atan2(b, a) ± acos(−c/√(a² + b²)), exact and in closed form. Such v
    /// exist where r²(s² + (n·A)²) ≥ c², a quadratic in s, so over arcs of u: across each arc the two roots run out and
    /// back as one closed curve (u = u_A + (u_B − u_A)(1 − cos φ)/2, the + root for φ in (0, π), the − root back, which
    /// is smooth where they meet), or, the plane meeting every meridian, as two curves round the axis. The curves as
    /// exact points halved to the chord tolerance, as traced ones (no marching, no projections). A plane through the
    /// axis: its two meridian circles. Square to the axis (circles) is the coaxial case; tangencies and the curve
    /// pinching to a point or a crossing: traced (null), as are tori with r ≥ R.
    /// </summary>
    static List<NCurve>? PlaneTorus(Plane pl, NSurface tor, double tol, double chordTol)
    {
        double R = tor.R, r = tor.R2;
        if (!(r > 0) || r >= R) return null;
        var n = pl.Normal; var ax = tor.Axis; var o = tor.Origin;
        var X = tor.F.XDir; var Y = Vec3.Cross(ax, X);
        double al = Vec3.Dot(n, X), be = Vec3.Dot(n, Y), ga = Vec3.Dot(n, ax), de = pl.Distance(o);
        var sig = Math.Sqrt(al * al + be * be);
        if (sig < 1e-9) return null;
        var u0 = Math.Atan2(be, al);
        Vec3 E(double u) => X * Math.Cos(u) + Y * Math.Sin(u);
        if (Math.Abs(ga) < 1e-12 && Math.Abs(de) < tol)
            return new List<NCurve> { new NCircle(o + E(u0 + Math.PI / 2) * R, n, E(u0 + Math.PI / 2), r), new NCircle(o + E(u0 - Math.PI / 2) * R, n, E(u0 - Math.PI / 2), r) };
        // r²(s² + γ²) − (δ + R s)² ≥ 0 between its roots s1 < s2 (the s² coefficient r² − R² is negative)
        double qa = r * r - R * R, qb = -2 * de * R, qc = r * r * ga * ga - de * de;
        var disc = qb * qb - 4 * qa * qc;
        var res = new List<NCurve>();
        var eps = 1e-9 * (1 + Math.Abs(qb) + Math.Abs(qa));
        if (disc < -eps * eps) return res;
        if (disc < eps * eps) return null; // touching
        var sq = Math.Sqrt(disc);
        var qq = -0.5 * (qb + Math.Sign(qb == 0 ? 1 : qb) * sq);
        double r1 = qq / qa, r2 = qc / qq;
        double s1 = Math.Min(r1, r2), s2 = Math.Max(r1, r2);
        const double edge = 1e-9;
        // s runs over [−σ, σ]: arcs of u where it is in [s1, s2]; near a transition (a pinch, a tangency): traced
        if (Math.Abs(s1 - sig) < edge || Math.Abs(s1 + sig) < edge || Math.Abs(s2 - sig) < edge || Math.Abs(s2 + sig) < edge) return null;
        if (s1 > sig || s2 < -sig) return res;
        Vec3 At(double u, double sign)
        {
            var s = al * Math.Cos(u) + be * Math.Sin(u);
            double a = r * s, b = r * ga, c = de + R * s;
            var rho = Math.Sqrt(a * a + b * b);
            var v = Math.Atan2(b, a) + sign * Math.Acos(Math.Clamp(rho > 0 ? -c / rho : 0, -1, 1));
            return o + E(u) * (R + r * Math.Cos(v)) + ax * (r * Math.Sin(v));
        }
        var curves = new List<Func<double, Vec3>>();
        if (s1 < -sig && s2 > sig)
        {
            curves.Add(t => At(t, 1));
            curves.Add(t => At(t, -1));
        }
        else
        {
            var arcs = new List<(double ua, double ub)>();
            if (s2 > sig) { var th = Math.Acos(s1 / sig); arcs.Add((u0 - th, u0 + th)); }
            else if (s1 < -sig) { var th = Math.Acos(s2 / sig); arcs.Add((u0 + th, u0 + 2 * Math.PI - th)); }
            else
            {
                double tl = Math.Acos(s1 / sig), th = Math.Acos(s2 / sig);
                arcs.Add((u0 + th, u0 + tl)); arcs.Add((u0 - tl, u0 - th));
            }
            foreach (var (ua, ub) in arcs)
                curves.Add(phi => At(ua + (ub - ua) * (1 - Math.Cos(phi)) * 0.5, Math.Sin(phi) >= 0 ? 1 : -1));
        }
        foreach (var f in curves)
        {
            // over φ (or u) in [0, 2π), halved where the curve leaves the chord
            var pts = new List<Vec3>();
            void Sub(double ta, Vec3 pa, double tb, Vec3 pb, int depth)
            {
                if (depth > 14) return;
                var tm = 0.5 * (ta + tb); var pm = f(tm);
                if (depth > 0 && Vec3.Distance(pm, (pa + pb) * 0.5) < chordTol) return;
                Sub(ta, pa, tm, pm, depth + 1);
                pts.Add(pm);
                Sub(tm, pm, tb, pb, depth + 1);
            }
            const int n0 = 32;
            var prev = f(0);
            for (int i = 0; i < n0; i++)
            {
                double ta = 2 * Math.PI * i / n0, tb = 2 * Math.PI * (i + 1) / n0;
                var pb = f(tb);
                pts.Add(prev);
                Sub(ta, prev, tb, pb, 0);
                prev = pb;
            }
            foreach (var p in pts)
                if (Math.Abs(pl.Distance(p)) > tol * 1e-2 || Math.Abs(tor.F_(p)) > tol * 1e-2) return null;
            res.Add(new NPolyline(pts, true));
        }
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
    [ThreadStatic] static int[]? _cross, _seg0, _seg1, _deg;
    [ThreadStatic] static Vec2[]? _crossPt;
    [ThreadStatic] static Vec3[]? _pos;
    [ThreadStatic] static int[]? _posStamp;
    [ThreadStatic] static bool[]? _live;

    /// <summary>
    /// Whether the face's surface over the parameter box keeps clear of sb: the box split in halves (across its
    /// longer side in space) until every piece's distance from sb at its centre exceeds its radius (as the grid's
    /// blocks are ruled out). A piece within tol of sb, or too many pieces, and it is not known: false.
    /// </summary>
    static bool Clear(NDomain dom, NSurface sb, double u0, double u1, double v0, double v1, double tol)
    {
        const int budget = 600;
        var stack = new Stack<(double u0, double u1, double v0, double v1)>();
        stack.Push((u0, u1, v0, v1));
        int evals = 0;
        while (stack.Count > 0)
        {
            var (a0, a1, b0, b1) = stack.Pop();
            double am = 0.5 * (a0 + a1), bm = 0.5 * (b0 + b1);
            var c = dom.Eval(new Vec2(am, bm));
            Vec3 E(double u, double v) => dom.Eval(new Vec2(u, v));
            Vec3 pu0 = E(a0, bm), pu1 = E(a1, bm), pv0 = E(am, b0), pv1 = E(am, b1);
            double r = 0;
            foreach (var q in new[] { E(a0, b0), E(a1, b0), E(a0, b1), E(a1, b1), pu0, pu1, pv0, pv1 }) r = Math.Max(r, Vec3.Distance(c, q));
            var f = Math.Abs(sb.F_(c));
            if (!double.IsFinite(f) || f <= tol || ++evals > budget) return false;
            if (f > r * 1.25 + 1e-9) continue;
            if (Vec3.Distance(pu0, pu1) >= Vec3.Distance(pv0, pv1)) { stack.Push((a0, am, b0, b1)); stack.Push((am, a1, b0, b1)); }
            else { stack.Push((a0, a1, b0, bm)); stack.Push((a0, a1, bm, b1)); }
        }
        return true;
    }

    public static List<NPolyline> Trace(NFace fa, NSurface sb, double tol, NDomain? domain = null, Box3? within = null)
    {
        // (domain: fa's domain at max(tol, 1e-3), when the caller has it)
        var dom = domain ?? NDomain.Of(fa, Math.Max(tol, 1e-3));
        var sa = fa.Surface;
        double du = dom.UMax - dom.UMin, dv = dom.VMax - dom.VMin;
        double mu = du * 0.02 + 1e-9, mv = dv * 0.02 + 1e-9;
        double u0 = dom.UMin - mu, u1 = dom.UMax + mu, v0 = dom.VMin - mv, v1 = dom.VMax + mv;
        // a free-form surface's distance is a closest-point search, and the grid asks it at thousands of nodes: first
        // a cheap proof that the face keeps clear of it (a thread's flank against the next start's), and nothing traced
        if (sb.Kind is SurfaceKind.BSpline or SurfaceKind.Offset)
            using (Prof.Time("trace.clear"))
                if (Clear(dom, sb, u0, u1, v0, v1, tol)) return new();
        const int nu = 96, nv = 96, block = 8;
        Vec2 UV(int i, int j) => new(u0 + (u1 - u0) * i / nu, v0 + (v1 - v0) * j / nv);
        // fine grid values are computed on demand: only blocks that may contain the curve are visited (in this
        // thread's grid, valid where stamped with this trace's number: nothing is allocated or cleared per trace)
        var g = _grid ??= new double[(nu + 1) * (nv + 1)];
        var have = _gridStamp ??= new int[(nu + 1) * (nv + 1)];
        int nk = (nu + 1) * (nv + 1) * 2;
        var cross = _cross ??= new int[nk];  // stamp·2 + 1: crosses, stamp·2: does not
        var cpt = _crossPt ??= new Vec2[nk];
        var seg0 = _seg0 ??= new int[nk];    // a crossing's segments in the order they were added
        var seg1 = _seg1 ??= new int[nk];
        var deg = _deg ??= new int[nk];      // stamp·4 + degree
        // the surface point at each grid node, shared by the block test and the grid values
        var pos = _pos ??= new Vec3[(nu + 1) * (nv + 1)];
        var posHave = _posStamp ??= new int[(nu + 1) * (nv + 1)];
        if (++_stamp == int.MaxValue / 4) { Array.Clear(have); Array.Clear(cross); Array.Clear(deg); Array.Clear(posHave); _stamp = 1; }
        int stamp = _stamp;
        Vec3 At(int i, int j)
        {
            int k = i * (nv + 1) + j;
            if (posHave[k] == stamp) return pos[k];
            posHave[k] = stamp;
            return pos[k] = dom.Eval(UV(i, j));
        }
        double G(int i, int j)
        {
            int k = i * (nv + 1) + j;
            if (have[k] == stamp) return g[k];
            // (a node the block test placed is reused; the rest of the fine grid is not stored)
            var v = Height(posHave[k] == stamp ? pos[k] : dom.Eval(UV(i, j)));
            have[k] = stamp;
            return g[k] = v == 0 ? 1e-300 : v;
        }
        // the other surface's height at a point, whose sign changes where the curve crosses: the signed distance, but
        // for a bounded patch (a B-spline) the offset from its nearest point along the normal there. Past the patch's
        // edge the nearest point is on the edge and the offset runs along the surface, its sign meaningless (two
        // strips of one cylinder would show sign changes a whole gap high); its height above the patch there does not.
        double Height(Vec3 p)
        {
            if (sb.Kind != SurfaceKind.BSpline) return sb.F_(p);
            var c = sb.Nurbs!.Closest(p);
            return Vec3.Dot(p - c.c, sb.Nurbs.Normal(c.u, c.v));
        }
        var pg = Prof.Time("trace.grid");
        // a square of cells can hold a zero only if |F(centre)| ≤ its 3D radius (F_ grows no faster than distance)
        bool CanHoldZero(int i0, int j0, int size)
        {
            int i1 = i0 + size, j1 = j0 + size, im = i0 + size / 2, jm = j0 + size / 2;
            var c = dom.Eval(Vec2.Lerp(UV(i0, j0), UV(i1, j1), 0.5));
            double r = 0;
            for (int q = 0; q < 8; q++)
            {
                var (ii, jj) = q switch { 0 => (i0, j0), 1 => (i1, j0), 2 => (i0, j1), 3 => (i1, j1), 4 => (im, j0), 5 => (im, j1), 6 => (i0, jm), _ => (i1, jm) };
                r = Math.Max(r, Vec3.Distance(c, At(ii, jj)));
            }
            return Math.Abs(sb.F_(c)) <= r * 1.25 + 1e-9;
        }
        // (within: only where the face comes near that box, the other face's: a surface traced past its face, a shell's
        // inner wall's extension through a fillet outside it, gives long curves that are all cut away)
        bool Near(int i0, int j0, int size)
        {
            if (within is not { } w) return true;
            var bb = Box3.Empty;
            foreach (var (ii, jj) in new[] { (i0, j0), (i0 + size, j0), (i0, j0 + size), (i0 + size, j0 + size), (i0 + size / 2, j0 + size / 2) }) bb.Add(At(ii, jj));
            var pad = 0.5 * bb.Diagonal + tol;
            return bb.Overlaps(w, pad);
        }
        int nb = nu / block;
        Span<bool> active = stackalloc bool[nb * nb];
        for (int bi = 0; bi < nb; bi++)
            for (int bj = 0; bj < nb; bj++)
                active[bi * nb + bj] = Near(bi * block, bj * block, block) && CanHoldZero(bi * block, bj * block, block);
        pg.Dispose();
        var pm = Prof.Time("trace.march");
        bool jumps = sb.Kind is SurfaceKind.BSpline or SurfaceKind.Offset;
        bool fitted = jumps || sa.Kind is SurfaceKind.BSpline or SurfaceKind.Offset;
        // marching squares: crossing points on cell edges, numbered (i, j, dir 0=u,1=v) → (i·(nv+1) + j)·2 + dir so
        // neighbours share them; this thread's arrays, valid where stamped with this trace's number. A crossing
        // lies on the edges of at most two cells, each giving it one segment, so its degree is at most 2.
        // the other surface within tol of this face at the nodes beside an edge of the grid (the rows either side of it)
        bool Flat(int i, int j, int dir)
        {
            for (int s = -1; s <= 1; s += 2)
            {
                int i0 = dir == 0 ? i : i + s, j0 = dir == 0 ? j + s : j, i1 = dir == 0 ? i + 1 : i + s, j1 = dir == 0 ? j + s : j + 1;
                if (i0 < 0 || j0 < 0 || i1 > nu || j1 > nv || i0 > nu || j0 > nv || i1 < 0 || j1 < 0) return false;
                if (Math.Abs(G(i0, j0)) >= tol || Math.Abs(G(i1, j1)) >= tol) return false;
            }
            return true;
        }
        bool Cross(int i, int j, int dir)
        {
            int key = (i * (nv + 1) + j) * 2 + dir;
            if (cross[key] >> 1 == stamp) return (cross[key] & 1) != 0;
            double a = G(i, j), b = dir == 0 ? G(i + 1, j) : G(i, j + 1);
            // (by the signs: two zeros, stored as 1e-300, multiply to 0, which read as a crossing at 0/0; and not where a
            // value is not a finite number, a surface evaluated past its patch in the grid's margin: a crossing there
            // would be NaN, and every point projected from it the slow way)
            if ((a < 0) == (b < 0) || !double.IsFinite(a) || !double.IsFinite(b)) { cross[key] = stamp * 2; return false; }
            // a free-form surface's signed distance changes no faster than the point moves, except where its nearest
            // point jumps to another part of it (a thread's next turn, which faces the other way): a sign change too
            // big for the step between the nodes is such a jump, not a crossing
            if (jumps && Math.Abs(a) + Math.Abs(b) > 1.5 * Vec3.Distance(At(i, j), dir == 0 ? At(i + 1, j) : At(i, j + 1))) { cross[key] = stamp * 2; return false; }
            // free-form surfaces within the tolerance of each other all round, at these nodes and the rows either side,
            // lie on each other there (fitted patches of one cylinder, say): a sign change is their fitting noise, not
            // a crossing. (Where they only touch, e.g. along a tangent edge, they part away from the curve.)
            if (fitted && Math.Max(Math.Abs(a), Math.Abs(b)) < tol && Flat(i, j, dir)) { cross[key] = stamp * 2; return false; }
            var t = a / (a - b);
            var pa = UV(i, j); var pb = dir == 0 ? UV(i + 1, j) : UV(i, j + 1);
            cpt[key] = Vec2.Lerp(pa, pb, t);
            cross[key] = stamp * 2 + 1;
            return true;
        }
        int Key(int i, int j, int dir) => (i * (nv + 1) + j) * 2 + dir;
        var segA = new List<int>(); var segB = new List<int>();
        var nodes = new List<int>(); // in first-use order
        void Link(int k, int s)
        {
            int d = deg[k] >> 2 == stamp ? deg[k] & 3 : 0;
            if (d == 0) { nodes.Add(k); seg0[k] = s; } else seg1[k] = s;
            deg[k] = stamp * 4 + d + 1;
        }
        void AddSeg(int a, int b) { int s = segA.Count; segA.Add(a); segB.Add(b); Link(a, s); Link(b, s); }
        Span<int> keys = stackalloc int[4];
        // within an active block, the same test on its quarters and theirs, down to 2 × 2 cells: cells in a part that
        // cannot hold a zero are skipped (no crossing on their edges; the rest are visited in the same order, so the
        // segments and their order are the same). About a third of the grid values a trace computed before.
        const int fine = 2, nf = block / fine;
        var live = _live ??= new bool[nf * nf];
        void Mark(int i0, int j0, int size, int bi0, int bj0)
        {
            if (!CanHoldZero(i0, j0, size)) return;
            if (size == fine) { live[(i0 - bi0) / fine * nf + (j0 - bj0) / fine] = true; return; }
            int h = size / 2;
            Mark(i0, j0, h, bi0, bj0); Mark(i0 + h, j0, h, bi0, bj0); Mark(i0, j0 + h, h, bi0, bj0); Mark(i0 + h, j0 + h, h, bi0, bj0);
        }
        for (int bi = 0; bi < nb; bi++)
            for (int bj = 0; bj < nb; bj++)
            {
                if (!active[bi * nb + bj]) continue;
                int bi0 = bi * block, bj0 = bj * block, hb = block / 2;
                Array.Clear(live);
                Mark(bi0, bj0, hb, bi0, bj0); Mark(bi0 + hb, bj0, hb, bi0, bj0); Mark(bi0, bj0 + hb, hb, bi0, bj0); Mark(bi0 + hb, bj0 + hb, hb, bi0, bj0);
                for (int i = bi * block; i < (bi + 1) * block; i++)
                    for (int j = bj * block; j < (bj + 1) * block; j++)
                    {
                        if (!live[(i - bi0) / fine * nf + (j - bj0) / fine]) continue;
                        int n = 0;
                        if (Cross(i, j, 0)) keys[n++] = Key(i, j, 0);
                        if (Cross(i + 1, j, 1)) keys[n++] = Key(i + 1, j, 1);
                        if (Cross(i, j + 1, 0)) keys[n++] = Key(i, j + 1, 0);
                        if (Cross(i, j, 1)) keys[n++] = Key(i, j, 1);
                        if (n == 2) AddSeg(keys[0], keys[1]);
                        else if (n == 4)
                        {
                            // saddle: decide by the centre value
                            var c = Height(dom.Eval(Vec2.Lerp(UV(i, j), UV(i + 1, j + 1), 0.5)));
                            if (c * G(i, j) > 0) { AddSeg(keys[0], keys[1]); AddSeg(keys[2], keys[3]); }
                            else { AddSeg(keys[0], keys[3]); AddSeg(keys[1], keys[2]); }
                        }
                    }
            }
        pm.Dispose();
        using var pp = Prof.Time("trace.project");
        // a B-spline (or its offset) traced against an analytic surface: points kept on it by Newton steps in its own
        // parameters, with the other surface's exact distance
        bool onParams = sa.Kind is SurfaceKind.BSpline or SurfaceKind.Offset && sb.Kind is not (SurfaceKind.BSpline or SurfaceKind.Offset or SurfaceKind.Pipe or SurfaceKind.Screw)
                        && sa.TryPartials(dom.UMin, dom.VMin * dom.VSign, out _, out _, out _);
        // traced on an analytic face against a B-spline (a small face it crosses near the B-spline's edge, where the
        // trace on the B-spline missed it): the points projected in the B-spline's parameters instead, each found
        // there once (its closest-point search is slow)
        bool onOther = !onParams && sb.Kind == SurfaceKind.BSpline && sa.Kind is not (SurfaceKind.BSpline or SurfaceKind.Offset or SurfaceKind.Pipe or SurfaceKind.Screw);
        var anyRange = (double.MinValue, double.MaxValue, double.MinValue, double.MaxValue);
        bool InPatch(Vec2 uv)
        {
            var nb = sb.Nurbs!;
            double eu = 1e-6 * (nb.U1 - nb.U0), ev = 1e-6 * (nb.V1 - nb.V0);
            return (nb.ClosedU || uv.X >= nb.U0 - eu && uv.X <= nb.U1 + eu) && (nb.ClosedV || uv.Y >= nb.V0 - ev && uv.Y <= nb.V1 + ev);
        }
        // (in surface parameters: v signed as the face's; moves within a grid cell's few widths)
        var range = (u0, u1, Math.Min(v0 * dom.VSign, v1 * dom.VSign), Math.Max(v0 * dom.VSign, v1 * dom.VSign));
        var maxMove = 4 * Math.Max(Vec3.Distance(dom.Eval(UV(0, 0)), dom.Eval(UV(1, 1))), tol);
        // chain segments: ends (degree 1) first, then the rest, each in first-use order
        var used = new bool[segA.Count];
        var result = new List<NPolyline>();
        int Deg(int k) => deg[k] & 3;
        int Other(int s, int k) => segA[s] == k ? segB[s] : segA[s];
        var order = new List<int>(nodes.Count);
        foreach (var k in nodes) if (Deg(k) == 1) order.Add(k);
        foreach (var k in nodes) if (Deg(k) != 1) order.Add(k);
        var chain = new List<int>();
        foreach (var start in order)
        {
            for (int f = 0; f < Deg(start); f++)
            {
                int s = f == 0 ? seg0[start] : seg1[start];
                if (used[s]) continue;
                chain.Clear(); chain.Add(start);
                used[s] = true;
                var cur = Other(s, start);
                while (true)
                {
                    chain.Add(cur);
                    if (cur == start) break;
                    int next = -1;
                    if (!used[seg0[cur]]) next = seg0[cur];
                    else if (Deg(cur) == 2 && !used[seg1[cur]]) next = seg1[cur];
                    if (next < 0) break;
                    used[next] = true;
                    cur = Other(next, cur);
                }
                bool closed = chain.Count > 3 && chain[0] == chain[^1];
                List<Vec3> pts;
                if (onParams)
                {
                    // on a free-form face: projected in its parameters (its closest-point search is slow), then
                    // densified between parameters
                    var uvs = new List<Vec2>(chain.Count);
                    pts = new List<Vec3>(chain.Count);
                    foreach (var k in chain) { var (q, uv) = ProjectOnParams(sa, sb, new Vec2(cpt[k].X, cpt[k].Y * dom.VSign), range, maxMove); pts.Add(q); uvs.Add(uv); }
                    if (closed) { pts.RemoveAt(pts.Count - 1); uvs.RemoveAt(uvs.Count - 1); }
                    pts = DensifyOnParams(pts, uvs, closed, sa, sb, tol, range, maxMove);
                }
                else if (onOther)
                {
                    var uvs = new List<Vec2>(chain.Count);
                    pts = new List<Vec3>(chain.Count);
                    Vec2? prev = null;
                    foreach (var k in chain)
                    {
                        var p = dom.Eval(cpt[k]);
                        var uvB = prev is { } h ? sb.UV(p, h.X, h.Y) : sb.UV(p);
                        var (q, uv) = ProjectOnParams(sb, sa, uvB, anyRange, maxMove);
                        pts.Add(q); uvs.Add(uv); prev = uv;
                    }
                    if (closed) { pts.RemoveAt(pts.Count - 1); uvs.RemoveAt(uvs.Count - 1); }
                    // (past the patch's edge the B-spline's distance changes sign with nothing crossing: a chain all
                    // off the patch is not an intersection)
                    if (!uvs.Any(InPatch)) continue;
                    pts = DensifyOnParams(pts, uvs, closed, sb, sa, tol, anyRange, maxMove);
                }
                else
                {
                    pts = new List<Vec3>(chain.Count);
                    var dirs = new List<Vec3>(chain.Count);
                    // (each projection's closest-point searches start where the one before, at the point before, ended)
                    var hints = new NSurface.ProjHints();
                    foreach (var k in chain) { pts.Add(NSurface.ProjectToIntersection(sa, sb, dom.Eval(cpt[k]), out var d, hints, stopStep: StopStep)); dirs.Add(d); }
                    if (closed) { pts.RemoveAt(pts.Count - 1); dirs.RemoveAt(dirs.Count - 1); }
                    pts = Densify(pts, dirs, closed, sa, sb, tol, hints);
                }
                if (pts.Count >= 2) result.Add(new NPolyline(pts, closed));
            }
        }
        return result;
    }

    /// <summary>Inserts exact midpoints until the chord error is below tol.</summary>
    /// <summary>
    /// The point of a ∩ b near a's point at surface parameters uv, found on a (Newton steps in its parameters, each the
    /// smallest move across b's level sets): exactly on a, on b within rounding. The steps stay inside the parameter
    /// box (the face's, with the tracing margin) and within maxMove of the start; otherwise, or where they do not
    /// converge, <see cref="NSurface.ProjectToIntersection"/>.
    /// </summary>
    static (Vec3 p, Vec2 uv) ProjectOnParams(NSurface a, NSurface b, Vec2 uv, (double u0, double u1, double v0, double v1) range, double maxMove)
    {
        var start = uv;
        var p0 = a.Eval(start.X, start.Y);
        for (int it = 0; it < 30; it++)
        {
            if (!a.TryPartials(uv.X, uv.Y, out var S, out var Su, out var Sv) || Vec3.Distance(S, p0) > maxMove) break;
            var f = b.FGrad(S, out var g);
            if (Math.Abs(f) < 1e-12 * Math.Max(1, S.Length)) return (S, uv);
            double ju = Vec3.Dot(g, Su), jv = Vec3.Dot(g, Sv);
            double guu = Vec3.Dot(Su, Su), guv = Vec3.Dot(Su, Sv), gvv = Vec3.Dot(Sv, Sv);
            var det = guu * gvv - guv * guv;
            if (!(det > 1e-300)) break;
            // G⁻¹ J: the direction in parameters of steepest change of f per unit of distance on a
            double du = (gvv * ju - guv * jv) / det, dv = (guu * jv - guv * ju) / det;
            var den = ju * du + jv * dv;
            if (!(Math.Abs(den) > 1e-300)) break;
            uv = new Vec2(uv.X - f * du / den, uv.Y - f * dv / den);
            if (uv.X < range.u0 || uv.X > range.u1 || uv.Y < range.v0 || uv.Y > range.v1) break;
        }
        return (NSurface.ProjectToIntersection(a, b, p0), start);
    }

    static List<Vec3> DensifyOnParams(List<Vec3> pts, List<Vec2> uvs, bool closed, NSurface a, NSurface b, double tol,
        (double u0, double u1, double v0, double v1) range, double maxMove)
    {
        var res = new List<Vec3>();
        int n = pts.Count;
        int segs = closed ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            res.Add(pts[i]);
            Sub(pts[i], uvs[i], pts[(i + 1) % n], uvs[(i + 1) % n], 0);
        }
        if (!closed) res.Add(pts[^1]);
        return res;

        void Sub(Vec3 p, Vec2 pu, Vec3 q, Vec2 qu, int depth)
        {
            if (depth > 6) return;
            var (m, mu) = ProjectOnParams(a, b, Vec2.Lerp(pu, qu, 0.5), range, maxMove);
            if (Vec3.Distance(m, (p + q) * 0.5) < tol * 0.25 && depth > 0) return;
            if (Vec3.Distance(p, q) < tol) return;
            Sub(p, pu, m, mu, depth + 1);
            res.Add(m);
            Sub(m, mu, q, qu, depth + 1);
        }
    }

    // a Gauss-Newton step this short ends a trace's projection (what is left is far below the 1e-12 it aims for)
    const double StopStep = 1e-7;

    /// <summary>
    /// Inserts points of the intersection between the chain's until the chord error is below tol: each the projection
    /// of a segment's middle, started from the cubic through its ends along the curve's directions there.
    /// </summary>
    static List<Vec3> Densify(List<Vec3> pts, List<Vec3> dirs, bool closed, NSurface a, NSurface b, double tol, NSurface.ProjHints? hints = null)
    {
        var res = new List<Vec3>();
        int n = pts.Count;
        int segs = closed ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            var p = pts[i]; var q = pts[(i + 1) % n];
            res.Add(p);
            Sub(p, dirs[i], q, dirs[(i + 1) % n], 0, double.MaxValue);
        }
        if (!closed) res.Add(pts[^1]);
        return res;

        void Sub(Vec3 p, Vec3 tp, Vec3 q, Vec3 tq, int depth, double parentDev)
        {
            if (depth > 6) return;
            var mid = (p + q) * 0.5;
            // started from the cubic through both ends along the curve's directions there, at its middle (where
            // they turn little: one Gauss-Newton step from it is usually enough)
            var start = mid;
            var c = q - p; double lp = tp.Length, lq = tq.Length, L = c.Length;
            if (lp > 1e-300 && lq > 1e-300 && L > 0)
            {
                var up = tp / lp * Math.Sign(Vec3.Dot(tp, c)); var uq = tq / lq * Math.Sign(Vec3.Dot(tq, c));
                if (Vec3.Dot(up, uq) > 0.5) start = mid + (up - uq) * (L / 8);
            }
            // (a chain segment already within tolerance too: the trace grid is usually much finer than tol)
            var m = NSurface.ProjectToIntersection(a, b, start, out var tm, hints, stopStep: StopStep);
            var dev = Vec3.Distance(m, mid);
            if (dev < tol * 0.25) return;
            if (Vec3.Distance(p, q) < tol) return;
            // within the tolerance, and halving the segment hardly lessened it: not the curve's bend (which falls
            // fourfold) but the surfaces' own noise (fitted patches a few microns off): no finer
            if (dev < tol && dev > 0.5 * parentDev) return;
            Sub(p, tp, m, tm, depth + 1, dev);
            res.Add(m);
            Sub(m, tm, q, tq, depth + 1, dev);
        }
    }
}
