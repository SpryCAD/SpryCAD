namespace FeatherCad.Kernel.Native;

/// <summary>
/// A (rational) B-spline surface: evaluation with derivatives, closest point, exact geometry for native faces.
/// Parameters are the knot values as given (u, v within [U0, U1] × [V0, V1]); the natural normal is Su × Sv.
/// Closed surfaces (first and last pole rows equal) are not periodic in parameter space: a seam point has two
/// parameters, and callers pass a hint to choose. Rows of poles that all coincide are poles of the surface
/// (u is free there, as at the poles of a sphere).
/// </summary>
public sealed class NurbsSurface
{
    public int Pu { get; }
    public int Pv { get; }
    public Vec3[,] P { get; }
    public double[,]? W { get; }
    public double[] U { get; }
    public double[] V { get; }
    public double U0 => U[Pu];
    public double U1 => U[^(Pu + 1)];
    public double V0 => V[Pv];
    public double V1 => V[^(Pv + 1)];
    public Box3 Box { get; }
    /// <summary>Points where a whole boundary row of poles collapses (sphere-like poles), with their v.</summary>
    public List<(Vec3 p, double v)> Poles { get; } = new();

    // closest-point seeds: a grid of surface points, in cells with bounding spheres
    const int G = 24;
    readonly Vec3[,] _grid = new Vec3[G + 1, G + 1];
    readonly double[] _gu = new double[G + 1], _gv = new double[G + 1];
    readonly Vec3[,] _cellC = new Vec3[G, G];
    readonly double[,] _cellR = new double[G, G];
    readonly double _scale;

    public NurbsSurface(int pu, int pv, Vec3[,] poles, double[,]? weights, double[] u, double[] v)
    {
        Pu = pu; Pv = pv; P = poles; U = u; V = v;
        int nu = poles.GetLength(0), nv = poles.GetLength(1);
        if (u.Length != nu + pu + 1 || v.Length != nv + pv + 1) throw new ArgumentException("B-spline surface: knots do not match the poles");
        if (weights != null)
        {
            bool uniform = true;
            foreach (var w in weights) if (Math.Abs(w - weights[0, 0]) > 1e-12 * Math.Abs(weights[0, 0])) uniform = false;
            if (!uniform) W = weights;
        }
        var box = Box3.Empty;
        foreach (var p in poles) box.Add(p);
        Box = box;
        _scale = Math.Max(box.Diagonal, 1e-9);
        for (int i = 0; i <= G; i++) { _gu[i] = U0 + (U1 - U0) * i / G; _gv[i] = V0 + (V1 - V0) * i / G; }
        for (int i = 0; i <= G; i++)
            for (int j = 0; j <= G; j++) _grid[i, j] = Eval(_gu[i], _gv[j]);
        for (int i = 0; i < G; i++)
            for (int j = 0; j < G; j++)
            {
                var c = (_grid[i, j] + _grid[i + 1, j] + _grid[i, j + 1] + _grid[i + 1, j + 1]) / 4;
                double r = 0;
                for (int a = 0; a <= 1; a++) for (int b = 0; b <= 1; b++) r = Math.Max(r, Vec3.Distance(c, _grid[i + a, j + b]));
                // the patch bulges between grid points: pad by the cell size
                _cellC[i, j] = c; _cellR[i, j] = r * 1.5 + 1e-12;
            }
        bool cu = true, cv = true;
        for (int j = 0; j < nv && cu; j++) if (Vec3.Distance(poles[0, j], poles[nu - 1, j]) > 1e-9 * _scale) cu = false;
        for (int i = 0; i < nu && cv; i++) if (Vec3.Distance(poles[i, 0], poles[i, nv - 1]) > 1e-9 * _scale) cv = false;
        ClosedU = cu; ClosedV = cv;
        // collapsed boundary rows (v = V0 / V1) are poles
        foreach (var (row, vv) in new[] { (0, V0), (nv - 1, V1) })
        {
            bool same = true;
            for (int i = 1; i < nu && same; i++) if (Vec3.Distance(poles[i, row], poles[0, row]) > 1e-9 * _scale) same = false;
            if (same) Poles.Add((poles[0, row], vv));
        }
    }

    /// <summary>The surface closes on itself in u (first and last pole columns coincide): u0 and u1 are the same points.</summary>
    public bool ClosedU { get; private set; }
    /// <summary>Closed in v (first and last pole rows coincide).</summary>
    public bool ClosedV { get; private set; }

    public bool IsPole(Vec3 p) => Poles.Any(q => Vec3.Distance(q.p, p) < 1e-7 * _scale);

    // ------------------------------------------------------------------------------------------
    // evaluation

    static double[,] DersBasis(int i, double u, int p, int n, double[] U)
    {
        // Piegl & Tiller A2.3: basis functions and their derivatives up to order n
        var ders = new double[n + 1, p + 1];
        var ndu = new double[p + 1, p + 1];
        var left = new double[p + 1]; var right = new double[p + 1];
        ndu[0, 0] = 1;
        for (int j = 1; j <= p; j++)
        {
            left[j] = u - U[i + 1 - j]; right[j] = U[i + j] - u;
            double saved = 0;
            for (int r = 0; r < j; r++)
            {
                ndu[j, r] = right[r + 1] + left[j - r];
                var temp = ndu[j, r] != 0 ? ndu[r, j - 1] / ndu[j, r] : 0;
                ndu[r, j] = saved + right[r + 1] * temp;
                saved = left[j - r] * temp;
            }
            ndu[j, j] = saved;
        }
        for (int j = 0; j <= p; j++) ders[0, j] = ndu[j, p];
        var a = new double[2, p + 1];
        for (int r = 0; r <= p; r++)
        {
            int s1 = 0, s2 = 1; a[0, 0] = 1;
            for (int k = 1; k <= n; k++)
            {
                double d = 0; int rk = r - k, pk = p - k;
                if (r >= k) { a[s2, 0] = ndu[pk + 1, rk] != 0 ? a[s1, 0] / ndu[pk + 1, rk] : 0; d = a[s2, 0] * ndu[rk, pk]; }
                int j1 = rk >= -1 ? 1 : -rk, j2 = r - 1 <= pk ? k - 1 : p - r;
                for (int j = j1; j <= j2; j++)
                {
                    a[s2, j] = ndu[pk + 1, rk + j] != 0 ? (a[s1, j] - a[s1, j - 1]) / ndu[pk + 1, rk + j] : 0;
                    d += a[s2, j] * ndu[rk + j, pk];
                }
                if (r <= pk) { a[s2, k] = ndu[pk + 1, r] != 0 ? -a[s1, k - 1] / ndu[pk + 1, r] : 0; d += a[s2, k] * ndu[r, pk]; }
                ders[k, r] = d;
                (s1, s2) = (s2, s1);
            }
        }
        int f = p;
        for (int k = 1; k <= n; k++) { for (int j = 0; j <= p; j++) ders[k, j] *= f; f *= p - k; }
        return ders;
    }

    /// <summary>Surface point and derivatives: S, Su, Sv, Suu, Suv, Svv.</summary>
    public (Vec3 s, Vec3 su, Vec3 sv, Vec3 suu, Vec3 suv, Vec3 svv) Derivs(double u, double v)
    {
        int nu = P.GetLength(0) - 1, nv = P.GetLength(1) - 1;
        u = Math.Clamp(u, U0, U1); v = Math.Clamp(v, V0, V1);
        int su = BSplineCurve.FindSpan(nu, Pu, u, U), sv = BSplineCurve.FindSpan(nv, Pv, v, V);
        var Nu = DersBasis(su, u, Pu, 2, U); var Nv = DersBasis(sv, v, Pv, 2, V);
        // homogeneous derivatives A(k,l) and weight derivatives w(k,l), k+l ≤ 2
        var A = new Vec3[3, 3]; var w = new double[3, 3];
        for (int k = 0; k <= 2; k++)
            for (int l = 0; l + k <= 2; l++)
            {
                Vec3 acc = Vec3.Zero; double wacc = 0;
                for (int i = 0; i <= Pu; i++)
                    for (int j = 0; j <= Pv; j++)
                    {
                        var b = Nu[k, i] * Nv[l, j];
                        if (b == 0) continue;
                        int pi = su - Pu + i, pj = sv - Pv + j;
                        var wt = W?[pi, pj] ?? 1;
                        acc += P[pi, pj] * (b * wt); wacc += b * wt;
                    }
                A[k, l] = acc; w[k, l] = wacc;
            }
        if (W == null) return (A[0, 0], A[1, 0], A[0, 1], A[2, 0], A[1, 1], A[0, 2]);
        // rational: quotient rule (Piegl & Tiller A4.4)
        var S = new Vec3[3, 3];
        for (int k = 0; k <= 2; k++)
            for (int l = 0; l + k <= 2; l++)
            {
                var vv = A[k, l];
                for (int j = 1; j <= l; j++) vv -= S[k, l - j] * (Binom(l, j) * w[0, j]);
                for (int i = 1; i <= k; i++)
                {
                    vv -= S[k - i, l] * (Binom(k, i) * w[i, 0]);
                    var v2 = Vec3.Zero;
                    for (int j = 1; j <= l; j++) v2 += S[k - i, l - j] * (Binom(l, j) * w[i, j]);
                    vv -= v2 * Binom(k, i);
                }
                S[k, l] = vv / w[0, 0];
            }
        return (S[0, 0], S[1, 0], S[0, 1], S[2, 0], S[1, 1], S[0, 2]);
    }

    static double Binom(int n, int k) => (n, k) switch { (_, 0) => 1, (1, 1) => 1, (2, 1) => 2, (2, 2) => 1, _ => throw new ArgumentException() };

    public Vec3 Eval(double u, double v)
    {
        int nu = P.GetLength(0) - 1, nv = P.GetLength(1) - 1;
        u = Math.Clamp(u, U0, U1); v = Math.Clamp(v, V0, V1);
        int su = BSplineCurve.FindSpan(nu, Pu, u, U), sv = BSplineCurve.FindSpan(nv, Pv, v, V);
        var Nu = BSplineCurve.Basis(su, u, Pu, U); var Nv = BSplineCurve.Basis(sv, v, Pv, V);
        Vec3 acc = Vec3.Zero; double wacc = 0;
        for (int i = 0; i <= Pu; i++)
            for (int j = 0; j <= Pv; j++)
            {
                int pi = su - Pu + i, pj = sv - Pv + j;
                var b = Nu[i] * Nv[j] * (W?[pi, pj] ?? 1);
                acc += P[pi, pj] * b; wacc += b;
            }
        return acc / wacc;
    }

    /// <summary>Unit natural normal Su × Sv (degenerate points: from a point slightly inside).</summary>
    public Vec3 Normal(double u, double v)
    {
        var (_, su, sv, _, _, _) = Derivs(u, v);
        var n = Vec3.Cross(su, sv);
        if (n.Length > 1e-12 * _scale * _scale) return n.Normalized;
        // collapsed row/column: step towards the middle of the patch
        double uu = u + (0.5 * (U0 + U1) - u) * 1e-4, vv = v + (0.5 * (V0 + V1) - v) * 1e-4;
        (_, su, sv, _, _, _) = Derivs(uu, vv);
        n = Vec3.Cross(su, sv);
        return n.Length > 0 ? n.Normalized : Vec3.UnitZ;
    }

    // ------------------------------------------------------------------------------------------
    // closest point

    /// <summary>Closest point of the patch to p: parameters, point, distance. A hint (on-surface parameters) is tried first.</summary>
    public (double u, double v, Vec3 c, double dist) Closest(Vec3 p, double uHint = double.NaN, double vHint = double.NaN)
    {
        if (!double.IsNaN(uHint) && !double.IsNaN(vHint))
        {
            // points on the surface near a known parameter (walking along a boundary): local Newton suffices, and
            // picks the hinted copy of a seam
            var (u, v) = Newton(p, uHint, vHint);
            var c = Eval(u, v); var d = Vec3.Distance(c, p);
            if (d < 1e-7 * _scale) return (u, v, c, d);
        }
        // grid seed: nearest sample among cells that can be nearer than the best so far
        double best = double.MaxValue; int bi = 0, bj = 0;
        for (int i = 0; i < G; i++)
            for (int j = 0; j < G; j++)
            {
                var lb = Vec3.Distance(p, _cellC[i, j]) - _cellR[i, j];
                if (lb > 0 && lb * lb > best) continue;
                for (int a = 0; a <= 1; a++)
                    for (int b = 0; b <= 1; b++)
                    {
                        var d2 = (_grid[i + a, j + b] - p).LengthSq;
                        if (d2 < best) { best = d2; bi = i + a; bj = j + b; }
                    }
            }
        var (u0, v0) = Newton(p, _gu[bi], _gv[bj]);
        var c0 = Eval(u0, v0);
        // a second start from a neighbouring cell centre guards against a stall on a boundary
        var res = (u0, v0, c0, Vec3.Distance(c0, p));
        if (res.Item4 > 1e-9 * _scale)
            foreach (var (du, dv) in new[] { (0.5, 0.5), (-0.5, 0.5), (0.5, -0.5), (-0.5, -0.5) })
            {
                var (u1, v1) = Newton(p, Math.Clamp(_gu[bi] + du * (U1 - U0) / G, U0, U1), Math.Clamp(_gv[bj] + dv * (V1 - V0) / G, V0, V1));
                var c1 = Eval(u1, v1); var d1 = Vec3.Distance(c1, p);
                if (d1 < res.Item4 - 1e-12 * _scale) res = (u1, v1, c1, d1);
            }
        // on a closed surface the search can stop against the boundary next to the seam while the point lies just
        // across it: retry from the other side
        if (res.Item4 > 1e-9 * _scale && (ClosedU || ClosedV))
        {
            double ur = res.Item1, vr = res.Item2;
            var starts = new List<(double, double)>();
            if (ClosedU && (ur <= U0 + 1e-12 || ur >= U1 - 1e-12)) starts.Add((ur <= U0 + 1e-12 ? U1 : U0, vr));
            if (ClosedV && (vr <= V0 + 1e-12 || vr >= V1 - 1e-12)) starts.Add((ur, vr <= V0 + 1e-12 ? V1 : V0));
            foreach (var (us, vs) in starts)
            {
                var (u2, v2) = Newton(p, us, vs);
                var c2 = Eval(u2, v2); var d2 = Vec3.Distance(c2, p);
                if (d2 < res.Item4) res = (u2, v2, c2, d2);
            }
        }
        if (!double.IsNaN(uHint) && !double.IsNaN(vHint))
        {
            // equally close points (a seam): the hinted one
            var (uh, vh) = Newton(p, uHint, vHint);
            var ch = Eval(uh, vh); var dh = Vec3.Distance(ch, p);
            if (dh <= res.Item4 + 1e-9 * _scale) res = (uh, vh, ch, dh);
        }
        return res;
    }

    /// <summary>Newton iteration on (S − p)·Su = 0, (S − p)·Sv = 0 within the parameter box.</summary>
    (double u, double v) Newton(Vec3 p, double u, double v)
    {
        for (int it = 0; it < 30; it++)
        {
            var (s, su, sv, suu, suv, svv) = Derivs(u, v);
            var r = s - p;
            double f = Vec3.Dot(r, su), g = Vec3.Dot(r, sv);
            double a = Vec3.Dot(su, su) + Vec3.Dot(r, suu), b = Vec3.Dot(su, sv) + Vec3.Dot(r, suv), d = Vec3.Dot(sv, sv) + Vec3.Dot(r, svv);
            var det = a * d - b * b;
            double du, dv;
            if (Math.Abs(det) > 1e-30 && a > 0 && d > 0 && det > 0) { du = (d * f - b * g) / det; dv = (a * g - b * f) / det; }
            else
            {
                // not convex here: a gradient step scaled by the first derivatives
                var su2 = Vec3.Dot(su, su); var sv2 = Vec3.Dot(sv, sv);
                du = su2 > 1e-300 ? f / su2 : 0; dv = sv2 > 1e-300 ? g / sv2 : 0;
            }
            var un = Math.Clamp(u - du, U0, U1); var vn = Math.Clamp(v - dv, V0, V1);
            var step = Math.Abs(un - u) / (U1 - U0) + Math.Abs(vn - v) / (V1 - V0);
            u = un; v = vn;
            if (step < 1e-15) break;
        }
        return (u, v);
    }

    public NurbsSurface Transformed(in Transform t)
    {
        var q = new Vec3[P.GetLength(0), P.GetLength(1)];
        for (int i = 0; i < q.GetLength(0); i++) for (int j = 0; j < q.GetLength(1); j++) q[i, j] = t.Point(P[i, j]);
        return new NurbsSurface(Pu, Pv, q, W, U, V);
    }

    /// <summary>Same poles, weights and knots (within tol).</summary>
    public bool SameAs(NurbsSurface o, double tol)
    {
        if (ReferenceEquals(this, o)) return true;
        if (Pu != o.Pu || Pv != o.Pv || P.GetLength(0) != o.P.GetLength(0) || P.GetLength(1) != o.P.GetLength(1)) return false;
        if (!U.SequenceEqual(o.U) || !V.SequenceEqual(o.V) || (W == null) != (o.W == null)) return false;
        for (int i = 0; i < P.GetLength(0); i++)
            for (int j = 0; j < P.GetLength(1); j++)
            {
                if (Vec3.Distance(P[i, j], o.P[i, j]) > tol) return false;
                if (W != null && Math.Abs(W[i, j] - o.W![i, j]) > 1e-12) return false;
            }
        return true;
    }

    /// <summary>Ray parameters where o + t d crosses the patch (distance-bound isolation, then bisection on the signed distance).</summary>
    public List<double> RayHits(Vec3 o, Vec3 d, Func<Vec3, double> signedDistance)
    {
        var res = new List<double>();
        var dl = d.Length;
        if (dl < 1e-300) return res;
        var pad = 1e-6 * _scale;
        double t0 = double.NegativeInfinity, t1 = double.PositiveInfinity;
        for (int k = 0; k < 3; k++)
        {
            double ok = k == 0 ? o.X : k == 1 ? o.Y : o.Z, dk = k == 0 ? d.X : k == 1 ? d.Y : d.Z;
            double lo = (k == 0 ? Box.Min.X : k == 1 ? Box.Min.Y : Box.Min.Z) - pad, hi = (k == 0 ? Box.Max.X : k == 1 ? Box.Max.Y : Box.Max.Z) + pad;
            if (Math.Abs(dk) < 1e-300) { if (ok < lo || ok > hi) return res; continue; }
            var a = (lo - ok) / dk; var b = (hi - ok) / dk;
            if (a > b) (a, b) = (b, a);
            t0 = Math.Max(t0, a); t1 = Math.Min(t1, b);
        }
        if (!(t1 > t0)) return res;
        double D(double t) => Closest(o + d * t).dist;
        var minL = 1e-7 * _scale;
        void Iso(double a, double b, double da, double db, int depth)
        {
            var L = (b - a) * dl;
            if (da + db > L * 1.0001 + 1e-12) return; // the patch is not that close anywhere in between
            if (depth >= 60 || L < minL * 100)
            {
                // a crossing: the signed distance changes sign within this short piece
                double fa = signedDistance(o + d * a), fb = signedDistance(o + d * b);
                if (fa * fb < 0) res.Add(Roots.Bisect(x => signedDistance(o + d * x), a, b));
                else if (fa == 0) res.Add(a);
                return;
            }
            var m = 0.5 * (a + b); var dm = D(m);
            Iso(a, m, da, dm, depth + 1);
            Iso(m, b, dm, db, depth + 1);
        }
        const int n = 16;
        double pt = t0, pd = D(t0);
        for (int i = 1; i <= n; i++)
        {
            var t = t0 + (t1 - t0) * i / n; var dd = D(t);
            Iso(pt, t, pd, dd, 0);
            pt = t; pd = dd;
        }
        res.Sort();
        for (int i = res.Count - 1; i > 0; i--) if (Math.Abs(res[i] - res[i - 1]) * dl < minL * 10) res.RemoveAt(i);
        return res;
    }

    /// <summary>Smallest radius of curvature over the grid (rough), for tessellation spacing.</summary>
    public double MinCurvatureRadius()
    {
        double kmax = 0;
        for (int i = 0; i <= 8; i++)
            for (int j = 0; j <= 8; j++)
            {
                var (_, su, sv, suu, _, svv) = Derivs(U0 + (U1 - U0) * i / 8, V0 + (V1 - V0) * j / 8);
                var n = Vec3.Cross(su, sv);
                if (n.Length < 1e-12) continue;
                n = n.Normalized;
                if (su.LengthSq > 1e-24) kmax = Math.Max(kmax, Math.Abs(Vec3.Dot(suu, n)) / su.LengthSq);
                if (sv.LengthSq > 1e-24) kmax = Math.Max(kmax, Math.Abs(Vec3.Dot(svv, n)) / sv.LengthSq);
            }
        return kmax > 1e-12 ? 1 / kmax : double.PositiveInfinity;
    }

    /// <summary>Average lengths of Su and Sv (parameter to distance scale).</summary>
    public (double su, double sv) MetricScale()
    {
        double a = 0, b = 0; int n = 0;
        for (int i = 0; i <= 4; i++)
            for (int j = 0; j <= 4; j++)
            {
                var (_, su, sv, _, _, _) = Derivs(U0 + (U1 - U0) * i / 4, V0 + (V1 - V0) * j / 4);
                a += su.Length; b += sv.Length; n++;
            }
        return (Math.Max(a / n, 1e-9), Math.Max(b / n, 1e-9));
    }
}
