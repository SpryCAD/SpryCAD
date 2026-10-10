namespace SpryCad.Kernel.Native;

/// <summary>
/// Exact mass properties of a native B-rep: volume, first and second moments and surface area integrated on the
/// exact surfaces, not on a mesh. By the divergence theorem each volume integral is a flux through the faces; on a
/// face, ∬ Φ(S)·(S_u × S_v) du dv over its parameter region is, by Green's theorem, ∮ G dv around its loops, with
/// G(u, v) = ∫ Φ(S)·(S_u × S_v) du from a base u. The loops are walked along the exact edge curves (Gauss points
/// mapped to the surface's parameters) and G by Gauss quadrature along u. The loops of a face run counter-clockwise
/// in (u, v) exactly when its outward normal is the surface's natural normal (S_u × S_v), so the loop integrals
/// already carry the outward sign; moving a whole closed loop by a period changes nothing.
/// </summary>
public static class NMass
{
    // Gauss-Legendre, 8 points on [-1, 1]
    static readonly double[] GX =
    {
        -0.9602898564975363, -0.7966664774136267, -0.5255324099163290, -0.1834346424956498,
        0.1834346424956498, 0.5255324099163290, 0.7966664774136267, 0.9602898564975363,
    };
    static readonly double[] GW =
    {
        0.1012285362903763, 0.2223810344533745, 0.3137066458778873, 0.3626837833783620,
        0.3626837833783620, 0.3137066458778873, 0.2223810344533745, 0.1012285362903763,
    };

    /// <summary>The integrals MassProps works from (V, ∫x, ∫y, ∫z, ∫x², ∫y², ∫z², ∫xy, ∫yz, ∫zx) and the area.</summary>
    public sealed record Integrals(double[] S, double Area);

    /// <summary>Mass properties at unit density, or null when a face cannot be integrated this way.</summary>
    public static MassProps? Of(NBody body)
    {
        var r = Integrate(body);
        if (r == null) return null;
        var mp = new MassProps();
        mp.AddIntegrals(r.S, r.Area);
        return mp.Finish();
    }

    // bodies are not modified once built: one integration per body (dropped with the body)
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NBody, StrongBox> _cache = new();
    sealed class StrongBox { public Integrals? Value; }

    /// <summary>The volume integrals of a closed body (kept for the body), or null when a face cannot be integrated this way.</summary>
    public static Integrals? Integrate(NBody body) => _cache.GetValue(body, b => new StrongBox { Value = Compute(b) }).Value;

    static Integrals? Compute(NBody body)
    {
        if (body.Faces.Count == 0) return new Integrals(new double[10], 0);
        // about the centre of the vertices' box (small coordinates: no cancellation in the second moments), moved
        // back below; not the body's bounds, which would sample (and cache) every edge: measuring changes nothing
        var box = Box3.Empty;
        foreach (var f in body.Faces) foreach (var c in f.Coedges) box.Add(c.Edge.V0.P);
        var o = box.Center;
        var scale = Math.Max(box.Diagonal, 1e-9);
        var perFace = new double[body.Faces.Count][];
        bool failed = false;
        Parallel.For(0, body.Faces.Count, i =>
        {
            try { perFace[i] = Face(body.Faces[i], o, scale); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { perFace[i] = null!; }
            if (perFace[i] == null) failed = true;
        });
        if (failed) return null;
        // summed in face order (the same result whatever the threads did)
        var q = new double[11];
        foreach (var f in perFace) for (int k = 0; k < 11; k++) q[k] += f[k];
        double V = q[0], mx = q[1], my = q[2], mz = q[3];
        double ox = o.X, oy = o.Y, oz = o.Z;
        var s = new double[10];
        s[0] = V;
        s[1] = mx + ox * V; s[2] = my + oy * V; s[3] = mz + oz * V;
        s[4] = q[4] + 2 * ox * mx + ox * ox * V;
        s[5] = q[5] + 2 * oy * my + oy * oy * V;
        s[6] = q[6] + 2 * oz * mz + oz * oz * V;
        s[7] = q[7] + ox * my + oy * mx + ox * oy * V;
        s[8] = q[8] + oy * mz + oz * my + oy * oz * V;
        s[9] = q[9] + oz * mx + ox * mz + oz * ox * V;
        return new Integrals(s, q[10]);
    }

    /// <summary>
    /// One face's contribution: the ten flux integrals (coordinates relative to o) and, last, its area. Null when a
    /// loop does not close in the surface's parameters.
    /// </summary>
    static double[]? Face(NFace f, Vec3 o, double scale)
    {
        var s = f.Surface;
        var res = new double[11];
        double areaSigned = 0;
        foreach (var loop in f.Loops)
        {
            // the loop's Gauss points in traversal order: parameters, dv/dt weight
            var pts = new List<(double u, double v, double wdv)>();
            double hu = double.NaN, hv = double.NaN;
            Vec2? first = null, last = null;
            foreach (var c in loop.Coedges)
            {
                var e = c.Edge;
                var breaks = Breaks(e, scale);
                int np = breaks.Count - 1;
                // the start vertex, for the closure check and the unwrapping hints
                var startP = c.Forward ? e.V0.P : e.V1.P;
                var su = s.UV(startP, hu, hv);
                if (!double.IsNaN(su.X)) hu = su.X;
                hv = su.Y;
                first ??= new Vec2(hu, hv);
                for (int pi = 0; pi < np; pi++)
                {
                    int pk = c.Forward ? pi : np - 1 - pi;
                    double ta = breaks[pk], tb = breaks[pk + 1];
                    double half = 0.5 * (tb - ta), mid = 0.5 * (ta + tb);
                    for (int gi = 0; gi < 8; gi++)
                    {
                        int g = c.Forward ? gi : 7 - gi;
                        var t = mid + half * GX[g];
                        var p = e.Curve.Eval(t);
                        var uv = s.UV(p, hu, hv);
                        if (double.IsNaN(uv.X)) uv = new Vec2(hu, uv.Y);
                        hu = uv.X; hv = uv.Y;
                        var (_, Su, Sv) = Partials(s, uv.X, uv.Y);
                        var d = e.Curve.Deriv(t);
                        // dP/dt = Su du/dt + Sv dv/dt: dv/dt by least squares (Su vanishes at a pole)
                        double a11 = Vec3.Dot(Su, Su), a12 = Vec3.Dot(Su, Sv), a22 = Vec3.Dot(Sv, Sv);
                        double b1 = Vec3.Dot(Su, d), b2 = Vec3.Dot(Sv, d);
                        var det = a11 * a22 - a12 * a12;
                        double dvdt = det > 1e-12 * a11 * a22 ? (a11 * b2 - a12 * b1) / det : a22 > 0 ? b2 / a22 : 0;
                        // traversal runs against t on a reversed coedge
                        pts.Add((uv.X, uv.Y, dvdt * GW[g] * half * (c.Forward ? 1 : -1)));
                    }
                }
                var endP = c.Forward ? e.V1.P : e.V0.P;
                var eu = s.UV(endP, hu, hv);
                if (!double.IsNaN(eu.X)) hu = eu.X;
                hv = eu.Y;
                last = new Vec2(hu, hv);
            }
            if (pts.Count == 0) continue;
            // the loop must close in (u, v): a face wrapping around its surface cannot be integrated this way
            var pu = s.PeriodicU ? 2 * Math.PI : double.PositiveInfinity;
            if (first is { } fs && last is { } ls)
            {
                var gap = Math.Abs(ls.X - fs.X);
                var gapV = Math.Abs(ls.Y - fs.Y);
                if (gap > 1e-6 * Math.Max(1, Math.Abs(fs.X)) && !s.IsPole(loop.Coedges[0].Forward ? loop.Coedges[0].Edge.V0.P : loop.Coedges[0].Edge.V1.P)) return null;
                if (gapV > 1e-6 * Math.Max(1, Math.Abs(fs.Y))) return null;
            }
            // base of the u integrals: the middle of the loop's u range (short inner integrals)
            double umin = double.MaxValue, umax = double.MinValue;
            foreach (var x in pts) { umin = Math.Min(umin, x.u); umax = Math.Max(umax, x.u); }
            double u0 = 0.5 * (umin + umax);
            // inner panel width: a quarter of the loop's u span (the integrals reach half of it), at most π/4 on angles
            var span = Math.Max(umax - umin, 1e-12);
            var panel = s.Kind == SurfaceKind.Plane ? double.PositiveInfinity : Math.Min(span / 4, s.PeriodicU ? Math.PI / 4 : double.PositiveInfinity);
            // each point's inner integral (in parallel on big faces), summed in order below
            var per = new double[pts.Count * 11];
            void Point(int i)
            {
                var (u, v, wdv) = pts[i];
                if (wdv == 0) return;
                var acc = new Span<double>(per, i * 11, 11);
                InnerU(s, u0, u, v, panel, o, acc);
                for (int k = 0; k < 11; k++) acc[k] *= wdv;
            }
            if (pts.Count >= 256 && s.Kind != SurfaceKind.Plane) Parallel.For(0, pts.Count, Point);
            else for (int i = 0; i < pts.Count; i++) Point(i);
            for (int i = 0; i < pts.Count; i++)
            {
                for (int k = 0; k < 10; k++) res[k] += per[i * 11 + k];
                areaSigned += per[i * 11 + 10];
            }
        }
        // the area integrand |Su × Sv| carries no orientation: the loops' turning sense gives the sum a sign
        res[10] = Math.Abs(areaSigned);
        return res;
    }

    /// <summary>Adds ∫ from u0 to u of the integrands at fixed v (Gauss-Legendre on panels of at most the given width).</summary>
    static void InnerU(NSurface s, double u0, double u, double v, double panel, Vec3 o, Span<double> acc)
    {
        var len = u - u0;
        if (len == 0) return;
        int n = double.IsInfinity(panel) ? 1 : Math.Max(1, (int)Math.Ceiling(Math.Abs(len) / panel));
        var h = len / n;
        for (int k = 0; k < n; k++)
        {
            double a = u0 + h * k, half = 0.5 * h, mid = a + half;
            for (int gi = 0; gi < 8; gi++)
            {
                var uu = mid + half * GX[gi];
                var (S, Su, Sv) = Partials(s, uu, v);
                var N = Vec3.Cross(Su, Sv);
                var w = GW[gi] * half;
                double x = S.X - o.X, y = S.Y - o.Y, z = S.Z - o.Z;
                acc[0] += w * (x * N.X + y * N.Y + z * N.Z) / 3;
                acc[1] += w * x * x * N.X / 2;
                acc[2] += w * y * y * N.Y / 2;
                acc[3] += w * z * z * N.Z / 2;
                acc[4] += w * x * x * x * N.X / 3;
                acc[5] += w * y * y * y * N.Y / 3;
                acc[6] += w * z * z * z * N.Z / 3;
                acc[7] += w * x * x * y * N.X / 2;
                acc[8] += w * y * y * z * N.Y / 2;
                acc[9] += w * z * z * x * N.Z / 2;
                acc[10] += w * N.Length;
            }
        }
    }

    /// <summary>Breakpoints of the edge's parameter range: panels short enough for 8-point Gauss on the curve.</summary>
    static List<double> Breaks(NEdge e, double scale)
    {
        double t0 = e.T0, t1 = e.T1;
        var res = new List<double>();
        int n;
        switch (e.Curve)
        {
            case NLine: n = 1; break;
            case NCircle or NEllipse: n = Math.Max(1, (int)Math.Ceiling(Math.Abs(t1 - t0) / (Math.PI / 8))); break;
            case NPolyline pl:
                {
                    // the polyline's own vertices (the integrand has a kink at each)
                    res.Add(t0);
                    double lo = Math.Min(t0, t1), hi = Math.Max(t0, t1);
                    foreach (var sv in pl.S) if (sv > lo + 1e-12 && sv < hi - 1e-12) res.Add(sv);
                    res.Add(t1);
                    if (t1 < t0) { res.Sort(); res.Reverse(); } else res.Sort();
                    return res;
                }
            // the curve's own sampling density (not the edge's sample cache: measuring must not change the body)
            // (8 Gauss points per panel: a quarter of the samples a polyline would need)
            default: n = Math.Clamp((e.Curve.Samples(Math.Min(t0, t1), Math.Max(t0, t1), scale * 1e-4) + 3) / 4, 2, 200); break;
        }
        for (int i = 0; i <= n; i++) res.Add(t0 + (t1 - t0) * i / n);
        return res;
    }

    /// <summary>The surface point and its parameter derivatives (exact; central differences only where no exact form applies).</summary>
    static (Vec3 S, Vec3 Su, Vec3 Sv) Partials(NSurface s, double u, double v)
    {
        var F = s.F;
        switch (s.Kind)
        {
            case SurfaceKind.Plane: return (F.ToWorld(u, v), F.XDir, F.YDir);
            case SurfaceKind.Cylinder:
                {
                    double c = Math.Cos(u), sn = Math.Sin(u), R = s.R;
                    return (F.ToWorld(R * c, R * sn, v), (F.XDir * -sn + F.YDir * c) * R, F.Normal);
                }
            case SurfaceKind.Cone:
                {
                    double c = Math.Cos(u), sn = Math.Sin(u), tn = Math.Tan(s.R2), r = s.R + v * tn;
                    return (F.ToWorld(r * c, r * sn, v), (F.XDir * -sn + F.YDir * c) * r, (F.XDir * c + F.YDir * sn) * tn + F.Normal);
                }
            case SurfaceKind.Sphere:
                {
                    double cu = Math.Cos(u), su = Math.Sin(u), cv = Math.Cos(v), sv = Math.Sin(v), R = s.R;
                    return (F.ToWorld(R * cv * cu, R * cv * su, R * sv),
                        (F.XDir * -su + F.YDir * cu) * (R * cv),
                        (F.XDir * cu + F.YDir * su) * (-R * sv) + F.Normal * (R * cv));
                }
            case SurfaceKind.Torus:
                {
                    double cu = Math.Cos(u), su = Math.Sin(u), cv = Math.Cos(v), sv = Math.Sin(v), r = s.R + s.R2 * cv;
                    return (F.ToWorld(r * cu, r * su, s.R2 * sv),
                        (F.XDir * -su + F.YDir * cu) * r,
                        (F.XDir * cu + F.YDir * su) * (-s.R2 * sv) + F.Normal * (s.R2 * cv));
                }
        }
        if (s.Kind == SurfaceKind.Pipe)
        {
            // exactly; beyond the spine's ends (where it is held) along it by central differences
            var sp = s.Spine!;
            if (sp.SurfacePartials(u, v, s.R, out var pp, out var pu, out var pv)) return (pp, pu, pv);
            var (_, n1, n2) = sp.Frame(v);
            double c = Math.Cos(u), sn = Math.Sin(u), hs = 1e-6 * Math.Max(1, Math.Abs(v));
            var pt = sp.Eval(v) + (n1 * c + n2 * sn) * s.R;
            var dsv = (sp.Surface(u, v + hs, s.R) - sp.Surface(u, v - hs, s.R)) / (2 * hs);
            return (pt, (n1 * -sn + n2 * c) * s.R, dsv);
        }
        // B-splines, screws, offsets of B-splines: exact
        if (s.TryPartials(u, v, out var es, out var esu, out var esv)) return (es, esu, esv);
        // otherwise central differences (steps relative to the parameter)
        double hu = 1e-6 * Math.Max(1, Math.Abs(u)), hv = 1e-6 * Math.Max(1, Math.Abs(v));
        var p = s.Eval(u, v);
        var du = (s.Eval(u + hu, v) - s.Eval(u - hu, v)) / (2 * hu);
        var dv = (s.Eval(u, v + hv) - s.Eval(u, v - hv)) / (2 * hv);
        return (p, du, dv);
    }
}
