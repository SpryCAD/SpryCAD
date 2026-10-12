namespace SpryCad.Kernel;

/// <summary>
/// Splines through points (a sketch spline's): cubic B-splines with centripetal parameters (the square root of the
/// chord between points: no loops or overshoot where the points bunch up). Open: clamped, through the first and last
/// point (Piegl &amp; Tiller A9.1). Closed: periodic, smooth (C²) where it closes too, written clamped at the first
/// point (the knots there raised to full multiplicity: the same curve).
/// </summary>
public static class SplineFit
{
    /// <summary>The spline through the points (points repeated in a row count once); null for fewer than two distinct ones (three closed).</summary>
    /// <param name="startDir">Open splines: the direction it leaves the first point in (tangent to what it continues), or null.</param>
    /// <param name="endDir">Open splines: the direction it arrives at the last point in, or null.</param>
    public static BSplineCurve? Through(IReadOnlyList<Vec3> points, bool closed, Vec3? startDir = null, Vec3? endDir = null)
    {
        var pts = new List<Vec3>();
        foreach (var p in points)
            if (pts.Count == 0 || Vec3.Distance(pts[^1], p) > 1e-9) pts.Add(p);
        if (closed && pts.Count > 1 && Vec3.Distance(pts[0], pts[^1]) <= 1e-9) pts.RemoveAt(pts.Count - 1);
        if (closed) return pts.Count >= 3 ? Closed(pts) : null;
        if (pts.Count < 2) return null;
        var uk = new double[pts.Count];
        for (int k = 1; k < pts.Count; k++) uk[k] = uk[k - 1] + Math.Sqrt(Vec3.Distance(pts[k], pts[k - 1]));
        for (int k = 1; k < pts.Count; k++) uk[k] /= uk[^1];
        uk[^1] = 1;
        var free = BSplineCurve.Interpolate(pts, uk, 3);
        if (startDir == null && endDir == null) return free;
        // an end tied to a direction: the derivative there along it, as long as the points' chords (an end left free
        // keeps the derivative the free fit has there)
        double len = 0;
        for (int k = 1; k < pts.Count; k++) len += Vec3.Distance(pts[k], pts[k - 1]);
        free.Derivs(0, out _, out var d0, out _); free.Derivs(1, out _, out var d1, out _);
        if (startDir is { } sd && sd.Length > 1e-12) d0 = sd.Normalized * len;
        if (endDir is { } ed && ed.Length > 1e-12) d1 = ed.Normalized * len;
        return WithEnds(pts, uk, d0, d1);
    }

    /// <summary>
    /// Cubic interpolation with the end derivatives given (Piegl &amp; Tiller 9.2.2): knots at the inner points'
    /// parameters, so n + 3 poles: the outer two at each end set by the point and the derivative there, the others
    /// solved so the curve passes through the inner points.
    /// </summary>
    static BSplineCurve WithEnds(List<Vec3> q, double[] uk, Vec3 d0, Vec3 d1)
    {
        const int p = 3;
        int n = q.Count - 1;
        var U = new double[n + 7];
        for (int i = n + 3; i < U.Length; i++) U[i] = 1;
        for (int k = 1; k <= n - 1; k++) U[k + 3] = uk[k];
        var P = new Vec3[n + 3];
        P[0] = q[0]; P[1] = q[0] + d0 * (U[4] / p);
        P[n + 2] = q[n]; P[n + 1] = q[n] - d1 * ((1 - U[n + 2]) / p);
        int m = n - 1; // inner poles P[2] .. P[n]
        if (m > 0)
        {
            var A = new double[m, m]; var rhs = new Vec3[m];
            for (int k = 1; k <= n - 1; k++)
            {
                int span = BSplineCurve.FindSpan(n + 2, p, uk[k], U);
                var N = BSplineCurve.Basis(span, uk[k], p, U);
                var r = q[k];
                for (int i = 0; i <= p; i++)
                {
                    int j = span - p + i;
                    if (j >= 2 && j <= n) A[k - 1, j - 2] += N[i];
                    else r -= P[j] * N[i];
                }
                rhs[k - 1] = r;
            }
            var x = Solve(A, rhs);
            for (int i = 0; i < m; i++) P[i + 2] = x[i];
        }
        return new BSplineCurve(p, P, U);
    }

    /// <summary>
    /// Periodic cubic interpolation: knots at the points' (periodic) parameters, so each point sees three basis
    /// functions there: a cyclic tridiagonal system for the n control points. Returned clamped over one period.
    /// </summary>
    static BSplineCurve Closed(List<Vec3> pts)
    {
        const int p = 3;
        int n = pts.Count;
        var t = new double[n + 1];
        for (int i = 0; i < n; i++) t[i + 1] = t[i] + Math.Sqrt(Vec3.Distance(pts[(i + 1) % n], pts[i]));
        double T = t[n];
        for (int i = 0; i <= n; i++) t[i] /= T;
        T = 1;
        // the periodic knot sequence K(j) = t[j mod n] + floor(j / n)
        double K(int j) { int q = (int)Math.Floor((double)j / n); return t[j - q * n] + q * T; }
        // B_j (support K(j) .. K(j + 4)) at K(i): Cox–de Boor on its five knots
        double B(int j, double u)
        {
            Span<double> k = stackalloc double[5];
            for (int a = 0; a < 5; a++) k[a] = K(j + a);
            Span<double> N = stackalloc double[4];
            for (int a = 0; a < 4; a++) N[a] = u >= k[a] && u < k[a + 1] ? 1 : 0;
            for (int d = 1; d <= p; d++)
                for (int a = 0; a <= 3 - d; a++)
                {
                    double l = k[a + d] - k[a], r = k[a + d + 1] - k[a + 1];
                    N[a] = (l > 0 ? (u - k[a]) / l * N[a] : 0) + (r > 0 ? (k[a + d + 1] - u) / r * N[a + 1] : 0);
                }
            return N[0];
        }
        // P_i = Σ B_j(t_i) C_(j mod n) over j = i-3 .. i-1
        var A = new double[n, n];
        var rhs = new Vec3[n];
        for (int i = 0; i < n; i++)
        {
            for (int j = i - 3; j <= i - 1; j++) A[i, ((j % n) + n) % n] += B(j, K(i));
            rhs[i] = pts[i];
        }
        var C = Solve(A, rhs);
        // the unclamped curve over [K(0), K(n)]: poles C_(-3) .. C_(n-1), knots K(-3) .. K(n+3)
        var poles = new Vec3[n + 3];
        for (int j = -3; j <= n - 1; j++) poles[j + 3] = C[((j % n) + n) % n];
        var knots = new double[n + 7];
        for (int j = -3; j <= n + 3; j++) knots[j + 3] = K(j);
        var c = new BSplineCurve(p, poles, knots);
        // clamped at both ends: K(0) and K(n) raised to multiplicity p, the knots and poles beyond them dropped
        c = Insert(Insert(c, K(0)), K(0));
        c = Insert(Insert(c, K(n)), K(n));
        // left: knots K(-3), K(-2), K(-1), K0 ×3 ...: the first two knots and poles go, the next knot becomes K0
        var kk = c.Knots.Skip(2).ToArray(); kk[0] = K(0);
        var pp = c.Poles.Skip(2).ToArray();
        // right: likewise
        kk = kk.Take(kk.Length - 2).ToArray(); kk[^1] = K(n);
        pp = pp.Take(pp.Length - 2).ToArray();
        return new BSplineCurve(p, pp, kk);
    }

    /// <summary>Boehm's knot insertion (Piegl &amp; Tiller A5.1): the same curve with u once more in its knots.</summary>
    public static BSplineCurve Insert(BSplineCurve c, double u)
    {
        int p = c.Degree, n = c.Poles.Length - 1;
        var U = c.Knots;
        // the span holding u (the last one whose knot is ≤ u)
        int k = p;
        while (k < U.Length - p - 2 && U[k + 1] <= u) k++;
        var Q = new Vec3[n + 2];
        for (int i = 0; i <= k - p; i++) Q[i] = c.Poles[i];
        for (int i = k - p + 1; i <= k; i++)
        {
            var a = (u - U[i]) / (U[i + p] - U[i]);
            Q[i] = c.Poles[i - 1] * (1 - a) + c.Poles[i] * a;
        }
        for (int i = k + 1; i <= n + 1; i++) Q[i] = c.Poles[i - 1];
        var V = new double[U.Length + 1];
        for (int i = 0; i <= k; i++) V[i] = U[i];
        V[k + 1] = u;
        for (int i = k + 1; i < U.Length; i++) V[i + 1] = U[i];
        return new BSplineCurve(p, Q, V);
    }

    /// <summary>Gaussian elimination with partial pivoting (a sketch spline's few dozen points).</summary>
    static Vec3[] Solve(double[,] A, Vec3[] b)
    {
        int n = b.Length;
        var M = (double[,])A.Clone(); var x = (Vec3[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(M[r, c]) > Math.Abs(M[piv, c])) piv = r;
            if (piv != c)
            {
                for (int k = 0; k < n; k++) (M[c, k], M[piv, k]) = (M[piv, k], M[c, k]);
                (x[c], x[piv]) = (x[piv], x[c]);
            }
            var d = M[c, c];
            if (Math.Abs(d) < 1e-300) continue;
            for (int r = c + 1; r < n; r++)
            {
                var f = M[r, c] / d;
                if (f == 0) continue;
                for (int k = c; k < n; k++) M[r, k] -= f * M[c, k];
                x[r] -= x[c] * f;
            }
        }
        for (int c = n - 1; c >= 0; c--)
        {
            var s = x[c];
            for (int k = c + 1; k < n; k++) s -= x[k] * M[c, k];
            x[c] = Math.Abs(M[c, c]) < 1e-300 ? s : s / M[c, c];
        }
        return x;
    }
}
