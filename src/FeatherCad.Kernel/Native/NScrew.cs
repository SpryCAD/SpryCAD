namespace FeatherCad.Kernel.Native;

/// <summary>
/// A screw motion: turning by u about the axis through O while advancing K·u along it (K = lead / 2π; 0 is a plain
/// rotation). The frame (X, Y, A) is stored as it is: rotation happens in its coordinates, so a mirrored frame
/// (left-handed) gives the mirrored motion.
/// </summary>
public sealed record ScrewMotion(Vec3 O, Vec3 X, Vec3 Y, Vec3 A, double K)
{
    /// <summary>The motion about an axis (any perpendicular reference direction).</summary>
    public static ScrewMotion About(Vec3 origin, Vec3 axis, double lead, Vec3? refDir = null)
    {
        var a = axis.Normalized;
        var x = refDir is { } r ? r - a * Vec3.Dot(r, a) : a.AnyPerpendicular();
        if (x.Length < 1e-12) x = a.AnyPerpendicular();
        x = x.Normalized;
        return new ScrewMotion(origin, x, Vec3.Cross(a, x), a, lead / (2 * Math.PI));
    }

    /// <summary>Lead: the advance per full turn.</summary>
    public double Lead => K * 2 * Math.PI;

    /// <summary>Coordinates of p in the motion's frame (x, y across the axis, z along it).</summary>
    public Vec3 Local(Vec3 p) { var d = p - O; return new Vec3(Vec3.Dot(d, X), Vec3.Dot(d, Y), Vec3.Dot(d, A)); }

    public Vec3 Move(double u, Vec3 p)
    {
        var l = Local(p);
        double c = Math.Cos(u), s = Math.Sin(u);
        return O + X * (c * l.X - s * l.Y) + Y * (s * l.X + c * l.Y) + A * (l.Z + K * u);
    }

    /// <summary>A direction turned by u (directions do not advance).</summary>
    public Vec3 Turn(double u, Vec3 d)
    {
        double x = Vec3.Dot(d, X), y = Vec3.Dot(d, Y), z = Vec3.Dot(d, A);
        double c = Math.Cos(u), s = Math.Sin(u);
        return X * (c * x - s * y) + Y * (s * x + c * y) + A * z;
    }

    /// <summary>d/du of Move(u, p).</summary>
    public Vec3 Velocity(double u, Vec3 p)
    {
        var l = Local(p);
        double c = Math.Cos(u), s = Math.Sin(u);
        return X * (-s * l.X - c * l.Y) + Y * (c * l.X - s * l.Y) + A * K;
    }

    /// <summary>d²/du² of Move(u, p).</summary>
    public Vec3 Acceleration(double u, Vec3 p)
    {
        var l = Local(p);
        double c = Math.Cos(u), s = Math.Sin(u);
        return X * (-c * l.X + s * l.Y) + Y * (-s * l.X - c * l.Y);
    }

    /// <summary>The whole motion as a transform for a given u.</summary>
    public Transform At(double u)
    {
        var o = Move(u, O); var x = Turn(u, X) ; var y = Turn(u, Y); var a = A;
        // world = o + x·(p-O)_x + y·(p-O)_y + a·(p-O)_z, written as a matrix about the world origin
        Vec3 Col(Vec3 e) => x * Vec3.Dot(e, X) + y * Vec3.Dot(e, Y) + a * Vec3.Dot(e, A);
        var c0 = Col(Vec3.UnitX); var c1 = Col(Vec3.UnitY); var c2 = Col(Vec3.UnitZ);
        var t = o - (c0 * O.X + c1 * O.Y + c2 * O.Z);
        return new Transform(c0.X, c1.X, c2.X, c0.Y, c1.Y, c2.Y, c0.Z, c1.Z, c2.Z, t);
    }

    public ScrewMotion Transformed(in Transform t)
    {
        var x = t.Vector(X); var s = x.Length;
        return new ScrewMotion(t.Point(O), x.Normalized, t.Vector(Y).Normalized, t.Vector(A).Normalized, K * s);
    }

    public bool SameAs(ScrewMotion o, double tol)
        => Vec3.Distance(A, o.A) < 1e-9 && Math.Abs(K - o.K) < tol * 1e-3
           && Vec3.Cross(o.O - O, A).Length < tol && Vec3.Dot(Vec3.Cross(X, Y), A) * Vec3.Dot(Vec3.Cross(o.X, o.Y), o.A) > 0;
}

/// <summary>
/// A screw surface: a generator curve (line or circular arc) carried by a screw motion. S(u, v) = Motion(u)(G(v)).
/// The surface is the generator's whole helical sweep; a face is the part u0..u1 (U0, U1 hold the sweep's range,
/// so that ray casts and closest-point searches look only where the surface was made).
/// </summary>
public sealed class ScrewSurface
{
    public ScrewMotion Motion { get; }
    public NCurve Gen { get; }
    /// <summary>The generator's parameter range used by the sweep.</summary>
    public double V0 { get; }
    public double V1 { get; }
    /// <summary>The sweep's range of u (turns × 2π).</summary>
    public double U0 { get; }
    public double U1 { get; }
    readonly double[] _vs;   // generator samples (closest-point seeds)
    readonly Vec3[] _gs;

    public ScrewSurface(ScrewMotion motion, NCurve gen, double v0, double v1, double u0, double u1)
    {
        Motion = motion; Gen = gen; V0 = Math.Min(v0, v1); V1 = Math.Max(v0, v1); U0 = Math.Min(u0, u1); U1 = Math.Max(u0, u1);
        const int n = 16;
        _vs = new double[n + 1]; _gs = new Vec3[n + 1];
        for (int i = 0; i <= n; i++) { _vs[i] = V0 + (V1 - V0) * i / n; _gs[i] = Gen.Eval(_vs[i]); }
        MaxRadius = _gs.Max(g => Radial(g));
        MinRadius = _gs.Min(g => Radial(g));
    }

    public double MaxRadius { get; }
    public double MinRadius { get; }
    double Radial(Vec3 p) { var l = Motion.Local(p); return Math.Sqrt(l.X * l.X + l.Y * l.Y); }

    public Vec3 Eval(double u, double v) => Motion.Move(u, Gen.Eval(v));

    public (Vec3 S, Vec3 Su, Vec3 Sv) Partials(double u, double v)
    {
        var g = Gen.Eval(v);
        return (Motion.Move(u, g), Motion.Velocity(u, g), Motion.Turn(u, Gen.Deriv(v)));
    }

    /// <summary>Natural normal (Su × Sv) at (u, v).</summary>
    public Vec3 NormalAt(double u, double v) { var (_, su, sv) = Partials(u, v); return Vec3.Cross(su, sv).Normalized; }

    /// <summary>
    /// Closest point of the surface to p: (u, v), the point and the distance. Seeds: for generator samples, the u
    /// whose turn brings the sample to p's angle on the right turn; then Gauss-Newton. A hint is tried first.
    /// </summary>
    public (double u, double v, Vec3 c, double dist) Closest(Vec3 p, double uHint = double.NaN, double vHint = double.NaN)
    {
        // a hint next to a point on the surface (walking along an edge): Newton from it lands on the point itself
        if (!double.IsNaN(uHint) && !double.IsNaN(vHint))
        {
            var h = Fix(Solve(uHint, vHint, p));
            if (h.dist < 1e-9 * Math.Max(1, MaxRadius)) return h;
        }
        var lp = Motion.Local(p);
        double angP = Math.Atan2(lp.Y, lp.X);
        var seeds = new List<(double u, double v, double d2)>();
        if (!double.IsNaN(uHint) && !double.IsNaN(vHint)) seeds.Add((uHint, vHint, (Eval(uHint, vHint) - p).LengthSq));
        for (int i = 0; i < _vs.Length; i++)
        {
            var lg = Motion.Local(_gs[i]);
            double angG = Math.Atan2(lg.Y, lg.X);
            var u = angP - angG;
            // the turn that brings the sample to p's height (any turn when the motion does not advance)
            if (Math.Abs(Motion.K) > 1e-12)
            {
                var uh = (lp.Z - lg.Z) / Motion.K;
                u += Math.Round((uh - u) / (2 * Math.PI)) * 2 * Math.PI;
            }
            else if (!double.IsNaN(uHint)) u = NCurve.Wrap(u, uHint);
            seeds.Add((u, _vs[i], (Eval(u, _vs[i]) - p).LengthSq));
        }
        seeds.Sort((a, b) => a.d2.CompareTo(b.d2));
        (double u, double v, Vec3 c, double dist) best = (0, 0, p, double.MaxValue);
        foreach (var (su0, sv0, _) in seeds.Take(3))
        {
            var r = Solve(su0, sv0, p);
            if (r.dist < best.dist) best = r;
        }
        return Fix(best);
    }

    // Gauss-Newton for the foot point of p from (u, v)
    (double u, double v, Vec3 c, double dist) Solve(double u, double v, Vec3 p)
    {
        for (int it = 0; it < 30; it++)
        {
            var (s, a, b) = Partials(u, v);
            var e = s - p;
            double aa = Vec3.Dot(a, a), ab = Vec3.Dot(a, b), bb = Vec3.Dot(b, b);
            double ga = Vec3.Dot(a, e), gb = Vec3.Dot(b, e);
            var det = aa * bb - ab * ab;
            if (det < 1e-300) break;
            var du = -(bb * ga - ab * gb) / det; var dv = -(aa * gb - ab * ga) / det;
            // damped where a full step would leave the seed's turn
            var lim = Math.PI / 2;
            if (Math.Abs(du) > lim) { var k = lim / Math.Abs(du); du *= k; dv *= k; }
            u += du; v += dv;
            if (Math.Abs(du) < 1e-15 * (1 + Math.Abs(u)) && Math.Abs(dv) < 1e-15 * (1 + Math.Abs(v))) break;
        }
        var c = Eval(u, v);
        return (u, v, c, Vec3.Distance(c, p));
    }

    // a generator line along the axis sweeps a cylinder: (u + 2π, v ∓ lead) is the same point. Of those, the one on
    // the generator's swept range (the face's), not wherever the search ended
    (double u, double v, Vec3 c, double dist) Fix((double u, double v, Vec3 c, double dist) best)
    {
        if (Gen is NLine gl && Math.Abs(Vec3.Dot(gl.D, Motion.A)) > 1 - 1e-12 && Math.Abs(Motion.K) > 1e-12)
        {
            var step = Math.Sign(Vec3.Dot(gl.D, Motion.A)) * Motion.K * 2 * Math.PI; // v shift for one more turn of u
            var n = Math.Round((best.v - 0.5 * (V0 + V1)) / step);
            if (n != 0) best = (best.u + 2 * Math.PI * n, best.v - step * n, best.c, best.dist);
        }
        return best;
    }

    /// <summary>Signed distance (positive on the natural normal's side) and the foot point's parameters.</summary>
    public double SignedDistance(Vec3 p)
    {
        var (u, v, c, d) = Closest(p);
        return Vec3.Dot(p - c, NormalAt(u, v)) >= 0 ? d : -d;
    }

    public Vec3 Normal(Vec3 p) { var (u, v, _, _) = Closest(p); return NormalAt(u, v); }

    /// <summary>The box of the made part (the sweep's turns), padded.</summary>
    public Box3 Box(double pad)
    {
        var b = Box3.Empty;
        int nu = Math.Max(8, (int)Math.Ceiling((U1 - U0) / (Math.PI / 8)));
        for (int i = 0; i <= nu; i++)
            foreach (var g in _gs) b.Add(Motion.Move(U0 + (U1 - U0) * i / nu, g));
        // a turn's chord sags inside the circle: grow by the sag of π/8 steps (and the pad)
        var sag = MaxRadius * (1 - Math.Cos(Math.PI / 16)) + pad;
        b.Add(b.Min - new Vec3(sag, sag, sag)); b.Add(b.Max + new Vec3(sag, sag, sag));
        return b;
    }

    /// <summary>
    /// Crossings of the ray o + t·d with the made part of the surface (u in the sweep's range, v in the generator's):
    /// roots of the signed distance along the ray inside the part's box, each checked to lie on the surface (the
    /// signed distance also jumps between turns, which is no crossing).
    /// </summary>
    public List<double> RayHits(Vec3 o, Vec3 d)
    {
        var res = new List<double>();
        var dl = d.Length;
        if (dl < 1e-300) return res;
        var box = Box(0);
        var scale = Math.Max(box.Diagonal, 1);
        double t0 = double.NegativeInfinity, t1 = double.PositiveInfinity;
        for (int k = 0; k < 3; k++)
        {
            double ok = k == 0 ? o.X : k == 1 ? o.Y : o.Z, dk = k == 0 ? d.X : k == 1 ? d.Y : d.Z;
            double lo = k == 0 ? box.Min.X : k == 1 ? box.Min.Y : box.Min.Z, hi = k == 0 ? box.Max.X : k == 1 ? box.Max.Y : box.Max.Z;
            if (Math.Abs(dk) < 1e-300) { if (ok < lo || ok > hi) return res; continue; }
            var a = (lo - ok) / dk; var b = (hi - ok) / dk;
            if (a > b) (a, b) = (b, a);
            t0 = Math.Max(t0, a); t1 = Math.Min(t1, b);
        }
        if (!(t1 > t0)) return res;
        double F(double t) => SignedDistance(o + d * t);
        var onTol = 1e-7 * scale;
        // steps short against the gap between turns and the generator's size
        var step = Math.Max(1e-6 * scale, Math.Min(Math.Abs(Motion.Lead) > 0 ? Math.Abs(Motion.Lead) / 8 : scale, Math.Max(MaxRadius, 1e-3) / 8)) / dl;
        int n = Math.Clamp((int)Math.Ceiling((t1 - t0) / step), 8, 20000);
        double pt = t0, pf = F(t0);
        for (int i = 1; i <= n; i++)
        {
            var t = t0 + (t1 - t0) * i / n; var f = F(t);
            if (pf * f <= 0 && !(pf == 0 && f == 0))
            {
                var r = pf == 0 ? pt : f == 0 ? t : Roots.Bisect(F, pt, t);
                var q = o + d * r;
                var (u, v, _, dist) = Closest(q);
                // on the surface, and on the part that was made
                if (dist < onTol * 100 && u >= U0 - 1e-9 && u <= U1 + 1e-9 && v >= V0 - 1e-9 * (1 + Math.Abs(V0)) && v <= V1 + 1e-9 * (1 + Math.Abs(V1))
                    && (res.Count == 0 || Math.Abs(r - res[^1]) * dl > onTol * 10)) res.Add(r);
            }
            pt = t; pf = f;
        }
        return res;
    }

    public ScrewSurface Transformed(in Transform t) => new(Motion.Transformed(t), Gen.Transformed(t), V0, V1, U0, U1);

    /// <summary>Whether this is the cylinder of radius r about the given axis (a generator line parallel to the axis).</summary>
    public bool IsCylinder(Vec3 origin, Vec3 axis, double r, double tol)
    {
        if (Gen is not NLine l || Vec3.Cross(l.D, Motion.A).Length > 1e-9) return false;
        if (Vec3.Cross(axis.Normalized, Motion.A).Length > 1e-9 || Vec3.Cross(origin - Motion.O, Motion.A).Length > tol) return false;
        var g = Motion.Local(l.P);
        return Math.Abs(Math.Sqrt(g.X * g.X + g.Y * g.Y) - r) < tol;
    }

    public bool SameAs(ScrewSurface o, double tol)
    {
        if (!Motion.SameAs(o.Motion, tol)) return false;
        // the same generator, possibly moved along the motion: every sample of one on the other's surface
        foreach (var g in _gs) if (o.Closest(g).dist > tol) return false;
        foreach (var g in o._gs) if (Closest(g).dist > tol) return false;
        return true;
    }
}

/// <summary>A helix: a point carried by a screw motion (parameter u, the turn angle).</summary>
public sealed class NHelix : NCurve
{
    public ScrewMotion Motion { get; }
    public Vec3 P0 { get; }
    public double Radius { get; }

    public NHelix(ScrewMotion motion, Vec3 p0)
    {
        Motion = motion; P0 = p0;
        var l = motion.Local(p0); Radius = Math.Sqrt(l.X * l.X + l.Y * l.Y);
    }

    public override Vec3 Eval(double t) => Motion.Move(t, P0);
    public override Vec3 Deriv(double t) => Motion.Velocity(t, P0);

    public override double Param(Vec3 p, double? hint = null)
    {
        var lp = Motion.Local(p); var l0 = Motion.Local(P0);
        var u = Math.Atan2(lp.Y, lp.X) - Math.Atan2(l0.Y, l0.X);
        if (Math.Abs(Motion.K) > 1e-12)
        {
            var uh = (lp.Z - l0.Z) / Motion.K;
            u += Math.Round((uh - u) / (2 * Math.PI)) * 2 * Math.PI;
        }
        else if (hint is { } h) u = Wrap(u, h);
        // Newton on the squared distance
        for (int it = 0; it < 30; it++)
        {
            var e = Eval(u) - p; var d1 = Deriv(u); var d2 = Motion.Acceleration(u, P0);
            var g = Vec3.Dot(e, d1); var gp = Vec3.Dot(d1, d1) + Vec3.Dot(e, d2);
            if (gp <= 1e-300) break;
            var du = -g / gp;
            if (Math.Abs(du) > Math.PI / 2) du = Math.Sign(du) * Math.PI / 2;
            u += du;
            if (Math.Abs(du) < 1e-15 * (1 + Math.Abs(u))) break;
        }
        return u;
    }

    public override NCurve Transformed(in Transform t) => new NHelix(Motion.Transformed(t), t.Point(P0));

    public override int Samples(double t0, double t1, double tol)
    {
        var step = 2 * Math.Acos(Math.Max(-1, 1 - Math.Min(1, tol / Math.Max(Radius, 1e-12))));
        return Math.Clamp((int)Math.Ceiling(Math.Abs(t1 - t0) / Math.Max(step, 1e-4)), 2, 20000);
    }

    /// <summary>STEP has no helix: an interpolating B-spline through dense points (a quarter turn at most between them).</summary>
    public ExactCurve ToExact(double t0, double t1)
    {
        int n = Math.Max(8, (int)Math.Ceiling(Math.Abs(t1 - t0) / (Math.PI / 16)));
        return BSplineCurve.Interpolate(Enumerable.Range(0, n + 1).Select(i => Eval(t0 + (t1 - t0) * i / n)).ToList(), 3);
    }

    public override ExactCurve ToExact() => ToExact(0, 2 * Math.PI);
}
