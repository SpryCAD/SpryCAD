namespace FeatherCad.Kernel;

/// <summary>Non-rational tensor-product B-spline surface (clamped); used to export surfaces STEP has no type for.</summary>
public sealed record BSplineSurface(int DegreeU, int DegreeV, Vec3[,] Poles, double[] KnotsU, double[] KnotsV)
{
    public Vec3 Eval(double u, double v)
    {
        int nu = Poles.GetLength(0) - 1, nv = Poles.GetLength(1) - 1;
        u = Math.Clamp(u, KnotsU[DegreeU], KnotsU[nu + 1]);
        v = Math.Clamp(v, KnotsV[DegreeV], KnotsV[nv + 1]);
        int su = BSplineCurve.FindSpan(nu, DegreeU, u, KnotsU), sv = BSplineCurve.FindSpan(nv, DegreeV, v, KnotsV);
        var Nu = BSplineCurve.Basis(su, u, DegreeU, KnotsU); var Nv = BSplineCurve.Basis(sv, v, DegreeV, KnotsV);
        var r = Vec3.Zero;
        for (int i = 0; i <= DegreeU; i++)
            for (int j = 0; j <= DegreeV; j++) r += Poles[su - DegreeU + i, sv - DegreeV + j] * (Nu[i] * Nv[j]);
        return r;
    }

    /// <summary>
    /// Bicubic interpolation of f on a uniform grid over [u0,u1]×[v0,v1], refined (up to a size limit) until the
    /// surface is within tol of f at the cell centres.
    /// </summary>
    public static BSplineSurface Fit(Func<double, double, Vec3> f, double u0, double u1, double v0, double v1, double tol, int nu = 4, int nv = 4)
    {
        BSplineSurface s;
        while (true)
        {
            s = Interpolate(f, u0, u1, v0, v1, nu, nv);
            double eu = 0, ev = 0;
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                {
                    double u = u0 + (u1 - u0) * (i + 0.5) / nu, v = v0 + (v1 - v0) * (j + 0.5) / nv;
                    double ua = u0 + (u1 - u0) * (i + 0.5) / nu, va = v0 + (v1 - v0) * j / nv;
                    double ub = u0 + (u1 - u0) * i / nu, vb = v0 + (v1 - v0) * (j + 0.5) / nv;
                    var e = Vec3.Distance(s.Eval(u, v), f(u, v));
                    eu = Math.Max(eu, Math.Max(e, Vec3.Distance(s.Eval(ua, va), f(ua, va))));
                    ev = Math.Max(ev, Math.Max(e, Vec3.Distance(s.Eval(ub, vb), f(ub, vb))));
                }
            if (Math.Max(eu, ev) <= tol || (nu >= 96 && nv >= 192)) return s;
            if (eu >= ev && nu < 96) nu *= 2; else if (nv < 192) nv *= 2; else nu *= 2;
        }
    }

    static BSplineSurface Interpolate(Func<double, double, Vec3> f, double u0, double u1, double v0, double v1, int nu, int nv)
    {
        var us = Enumerable.Range(0, nu + 1).Select(i => (double)i / nu).ToArray();
        var vs = Enumerable.Range(0, nv + 1).Select(j => (double)j / nv).ToArray();
        int pu = Math.Min(3, nu), pv = Math.Min(3, nv);
        var ku = Knots(us, pu); var kv = Knots(vs, pv);
        var Au = Matrix(us, pu, ku); var Av = Matrix(vs, pv, kv);
        // rows along u for each v, then columns along v
        var R = new Vec3[nu + 1, nv + 1];
        for (int j = 0; j <= nv; j++)
        {
            var row = new Vec3[nu + 1];
            for (int i = 0; i <= nu; i++) row[i] = f(u0 + (u1 - u0) * us[i], v0 + (v1 - v0) * vs[j]);
            var sol = Solve(Au, row);
            for (int i = 0; i <= nu; i++) R[i, j] = sol[i];
        }
        var Pp = new Vec3[nu + 1, nv + 1];
        for (int i = 0; i <= nu; i++)
        {
            var col = new Vec3[nv + 1];
            for (int j = 0; j <= nv; j++) col[j] = R[i, j];
            var sol = Solve(Av, col);
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
