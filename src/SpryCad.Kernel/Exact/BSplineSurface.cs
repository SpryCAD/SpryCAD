namespace SpryCad.Kernel;

/// <summary>Non-rational tensor-product B-spline surface (clamped); used to export surfaces STEP has no type for.</summary>
public sealed record BSplineSurface(int DegreeU, int DegreeV, Vec3[,] Poles, double[] KnotsU, double[] KnotsV)
{
    public static Action<string>? Debug;

    public Vec3 Eval(double u, double v)
    {
        int nu = Poles.GetLength(0) - 1, nv = Poles.GetLength(1) - 1;
        u = Math.Clamp(u, KnotsU[DegreeU], KnotsU[nu + 1]);
        v = Math.Clamp(v, KnotsV[DegreeV], KnotsV[nv + 1]);
        int su = BSplineCurve.FindSpan(nu, DegreeU, u, KnotsU), sv = BSplineCurve.FindSpan(nv, DegreeV, v, KnotsV);
        Span<double> Nu = stackalloc double[DegreeU + 1], Nv = stackalloc double[DegreeV + 1];
        BSplineCurve.Basis(su, u, DegreeU, KnotsU, Nu); BSplineCurve.Basis(sv, v, DegreeV, KnotsV, Nv);
        var r = Vec3.Zero;
        for (int i = 0; i <= DegreeU; i++)
            for (int j = 0; j <= DegreeV; j++) r += Poles[su - DegreeU + i, sv - DegreeV + j] * (Nu[i] * Nv[j]);
        return r;
    }

    /// <summary>
    /// Bicubic interpolation of f on a uniform grid over [u0,u1]×[v0,v1], refined (up to a size limit) until the
    /// surface is within tol of f at the cell centres.
    /// </summary>
    public static BSplineSurface Fit(Func<double, double, Vec3> f, double u0, double u1, double v0, double v1, double tol, int nu = 4, int nv = 4,
        int degree = 3, int maxU = 128, int maxV = 256)
    {
        BSplineSurface s;
        nu = Math.Max(nu, degree); nv = Math.Max(nv, degree);
        while (true)
        {
            s = Interpolate(f, u0, u1, v0, v1, nu, nv, degree);
            double eu = 0, ev = 0, ec = 0;
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                {
                    double u = u0 + (u1 - u0) * (i + 0.5) / nu, v = v0 + (v1 - v0) * (j + 0.5) / nv;
                    double ua = u0 + (u1 - u0) * (i + 0.5) / nu, va = v0 + (v1 - v0) * j / nv;
                    double ub = u0 + (u1 - u0) * i / nu, vb = v0 + (v1 - v0) * (j + 0.5) / nv;
                    // error between samples along u (on a v grid line), along v, and at the cell centre
                    ec = Math.Max(ec, Vec3.Distance(s.Eval(u, v), f(u, v)));
                    eu = Math.Max(eu, Vec3.Distance(s.Eval(ua, va), f(ua, va)));
                    ev = Math.Max(ev, Vec3.Distance(s.Eval(ub, vb), f(ub, vb)));
                }
            Debug?.Invoke($"fit {nu}x{nv}: err u {eu:g2} v {ev:g2} centre {ec:g2}");
            if (Math.Max(ec, Math.Max(eu, ev)) <= tol || (nu >= maxU && nv >= maxV)) return s;
            // refine the direction whose error is above tolerance (both when both are)
            bool refU = eu > tol || (ec > tol && eu >= ev), refV = ev > tol || (ec > tol && ev > eu);
            if (refU && nu < maxU) nu = Math.Min(maxU, nu * 2);
            if (refV && nv < maxV) nv = Math.Min(maxV, nv * 2);
            if ((!refU || nu >= maxU) && (!refV || nv >= maxV)) return s;
        }
    }

    /// <summary>
    /// <see cref="Fit"/> with the interpolation sites along v placed where they are needed: uniform across u (doubled
    /// while the error across is above tol), along v refined locally, each interval whose error is above tol halved
    /// (a surface only C1 along v at places, as a fillet's blend where its ball switches faces and its spine's
    /// curvature jumps: uniform sites would have to be fine everywhere). vs: the first sites along v (in v0..v1).
    /// </summary>
    public static BSplineSurface FitAdaptiveV(Func<double, double, Vec3> f, double u0, double u1, double v0, double v1, double tol, int nu,
        IEnumerable<double> vs0, int degree = 3, int maxU = 128, int maxSites = 800)
    {
        // (sites a hair apart, as a computed end next to the end itself, make the system singular: one of them)
        var vs = new List<double>();
        foreach (var x in vs0.Select(v => Math.Clamp((v - v0) / (v1 - v0), 0, 1)).Append(0).Append(1).OrderBy(x => x))
            if (vs.Count == 0 || x - vs[^1] > 1e-9) vs.Add(x);
        vs[0] = 0; vs[^1] = 1;
        nu = Math.Max(nu, degree);
        while (vs.Count <= degree) { var j = Enumerable.Range(0, vs.Count - 1).MaxBy(k => vs[k + 1] - vs[k]); vs.Insert(j + 1, 0.5 * (vs[j] + vs[j + 1])); }
        while (true)
        {
            var us = Enumerable.Range(0, nu + 1).Select(i => (double)i / nu).ToArray();
            var s = Interpolate(f, u0, u1, v0, v1, us, vs.ToArray(), degree);
            double eu = 0, ev = 0;
            var bad = new List<int>();
            for (int j = 0; j + 1 < vs.Count; j++)
            {
                double worst = 0;
                var vm = v0 + (v1 - v0) * 0.5 * (vs[j] + vs[j + 1]); var vj = v0 + (v1 - v0) * vs[j];
                for (int i = 0; i < nu; i++)
                {
                    double um = u0 + (u1 - u0) * (i + 0.5) / nu, ui = u0 + (u1 - u0) * i / nu;
                    var dc = Vec3.Distance(s.Eval(um, vm), f(um, vm));
                    var da = Vec3.Distance(s.Eval(um, vj), f(um, vj));
                    var db = Vec3.Distance(s.Eval(ui, vm), f(ui, vm));
                    eu = Math.Max(eu, da); ev = Math.Max(ev, db);
                    worst = Math.Max(worst, Math.Max(dc, db));
                }
                if (worst > tol) bad.Add(j);
            }
            Debug?.Invoke($"fit {nu}x{vs.Count - 1} (adaptive): err u {eu:g2} v {ev:g2}, {bad.Count} intervals over");
            if (Debug != null && bad.Count > 0)
            {
                // (where along v the worst is)
                double wv = 0, we = 0;
                foreach (var j in bad)
                {
                    var vm = v0 + (v1 - v0) * 0.5 * (vs[j] + vs[j + 1]);
                    for (int i = 0; i < nu; i++) { double ui = u0 + (u1 - u0) * i / nu; var d = Vec3.Distance(s.Eval(ui, vm), f(ui, vm)); if (d > we) { we = d; wv = vm; } }
                }
                Debug($"  worst {we:g2} at v {wv:g4} (interval {(v1 - v0) * (vs[bad[0] + 1] - vs[bad[0]]):g2} wide at its first bad)");
            }
            bool refU = eu > tol && nu < maxU;
            if (!refU && (bad.Count == 0 || vs.Count + bad.Count > maxSites)) return s;
            if (refU) nu = Math.Min(maxU, nu * 2);
            if (vs.Count + bad.Count <= maxSites)
                foreach (var j in Enumerable.Reverse(bad)) vs.Insert(j + 1, 0.5 * (vs[j] + vs[j + 1]));
        }
    }

    static BSplineSurface Interpolate(Func<double, double, Vec3> f, double u0, double u1, double v0, double v1, int nu, int nv, int degree)
        => Interpolate(f, u0, u1, v0, v1, Enumerable.Range(0, nu + 1).Select(i => (double)i / nu).ToArray(),
            Enumerable.Range(0, nv + 1).Select(j => (double)j / nv).ToArray(), degree);

    /// <summary>Interpolation of f at the sites us × vs (each 0 .. 1, increasing), knots by averaging them.</summary>
    static BSplineSurface Interpolate(Func<double, double, Vec3> f, double u0, double u1, double v0, double v1, double[] us, double[] vs, int degree)
    {
        int nu = us.Length - 1, nv = vs.Length - 1;
        int pu = Math.Min(degree, nu), pv = Math.Min(degree, nv);
        var ku = Knots(us, pu); var kv = Knots(vs, pv);
        var Au = new Lu(Matrix(us, pu, ku)); var Av = new Lu(Matrix(vs, pv, kv));
        // rows along u for each v, then columns along v
        var R = new Vec3[nu + 1, nv + 1];
        for (int j = 0; j <= nv; j++)
        {
            var row = new Vec3[nu + 1];
            for (int i = 0; i <= nu; i++) row[i] = f(u0 + (u1 - u0) * us[i], v0 + (v1 - v0) * vs[j]);
            var sol = Au.Solve(row);
            for (int i = 0; i <= nu; i++) R[i, j] = sol[i];
        }
        var Pp = new Vec3[nu + 1, nv + 1];
        for (int i = 0; i <= nu; i++)
        {
            var col = new Vec3[nv + 1];
            for (int j = 0; j <= nv; j++) col[j] = R[i, j];
            var sol = Av.Solve(col);
            for (int j = 0; j <= nv; j++) Pp[i, j] = sol[j];
        }
        // knots on the real parameter ranges
        return new BSplineSurface(pu, pv, Pp, ku.Select(k => u0 + (u1 - u0) * k).ToArray(), kv.Select(k => v0 + (v1 - v0) * k).ToArray());
    }

    static double[] Knots(double[] uk, int p)
    {
        int n = uk.Length - 1;
        var U = new double[n + p + 2];
        for (int i = n + 1; i < U.Length; i++) U[i] = 1;
        for (int j = 1; j <= n - p; j++)
        {
            double s = 0;
            for (int i = j; i <= j + p - 1; i++) s += uk[i];
            U[j + p] = s / p;
        }
        return U;
    }

    static double[,] Matrix(double[] uk, int p, double[] U)
    {
        int n = uk.Length - 1;
        var A = new double[n + 1, n + 1];
        for (int k = 0; k <= n; k++)
        {
            int span = BSplineCurve.FindSpan(n, p, uk[k], U);
            var N = BSplineCurve.Basis(span, uk[k], p, U);
            for (int i = 0; i <= p; i++) A[k, span - p + i] = N[i];
        }
        return A;
    }

    /// <summary>LU factorization with partial pivoting, factored once and reused for every row/column.</summary>
    sealed class Lu
    {
        readonly double[,] _m;
        readonly int[] _perm;
        readonly int _n;

        public Lu(double[,] a)
        {
            _n = a.GetLength(0);
            _m = (double[,])a.Clone();
            _perm = Enumerable.Range(0, _n).ToArray();
            for (int c = 0; c < _n; c++)
            {
                int piv = c; double best = Math.Abs(_m[c, c]);
                for (int r = c + 1; r < _n; r++) if (Math.Abs(_m[r, c]) > best) { best = Math.Abs(_m[r, c]); piv = r; }
                if (piv != c)
                {
                    for (int k = 0; k < _n; k++) (_m[c, k], _m[piv, k]) = (_m[piv, k], _m[c, k]);
                    (_perm[c], _perm[piv]) = (_perm[piv], _perm[c]);
                }
                for (int r = c + 1; r < _n; r++)
                {
                    var f = _m[r, c] / _m[c, c];
                    _m[r, c] = f;
                    if (f == 0) continue;
                    for (int k = c + 1; k < _n; k++) _m[r, k] -= f * _m[c, k];
                }
            }
        }

        public Vec3[] Solve(Vec3[] b)
        {
            var x = new Vec3[_n];
            for (int i = 0; i < _n; i++) x[i] = b[_perm[i]];
            for (int r = 1; r < _n; r++)
            {
                var s = x[r];
                for (int k = 0; k < r; k++) if (_m[r, k] != 0) s -= x[k] * _m[r, k];
                x[r] = s;
            }
            for (int r = _n - 1; r >= 0; r--)
            {
                var s = x[r];
                for (int k = r + 1; k < _n; k++) if (_m[r, k] != 0) s -= x[k] * _m[r, k];
                x[r] = s / _m[r, r];
            }
            return x;
        }
    }

    static Vec3[] Solve(double[,] A0, Vec3[] b)
    {
        int n = b.Length;
        var M = (double[,])A0.Clone(); var x = (Vec3[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c; double best = Math.Abs(M[c, c]);
            for (int r = c + 1; r < n; r++) if (Math.Abs(M[r, c]) > best) { best = Math.Abs(M[r, c]); piv = r; }
            if (piv != c)
            {
                for (int k = 0; k < n; k++) (M[c, k], M[piv, k]) = (M[piv, k], M[c, k]);
                (x[c], x[piv]) = (x[piv], x[c]);
            }
            for (int r = c + 1; r < n; r++)
            {
                var fct = M[r, c] / M[c, c];
                if (fct == 0) continue;
                for (int k = c; k < n; k++) M[r, k] -= fct * M[c, k];
                x[r] -= x[c] * fct;
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            var s = x[r];
            for (int k = r + 1; k < n; k++) s -= x[k] * M[r, k];
            x[r] = s / M[r, r];
        }
        return x;
    }

    /// <summary>Distinct knot values and multiplicities (STEP form).</summary>
    public static (double[] values, int[] mults) KnotVector(double[] knots)
    {
        var vals = new List<double>(); var mults = new List<int>();
        foreach (var k in knots)
        {
            if (vals.Count > 0 && Math.Abs(vals[^1] - k) < 1e-14) mults[^1]++;
            else { vals.Add(k); mults.Add(1); }
        }
        return (vals.ToArray(), mults.ToArray());
    }
}
