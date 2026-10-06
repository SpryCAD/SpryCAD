namespace FeatherCad.Kernel;

/// <summary>Exact edge geometry.</summary>
public abstract record ExactCurve
{
    /// <summary>Points along the curve between the given end points (for display / tessellation).</summary>
    public abstract IEnumerable<Vec3> Sample(Vec3 from, Vec3 to, bool closed, double chordTol);
}

public sealed record LineCurve(Vec3 Point, Vec3 Dir) : ExactCurve
{
    public override IEnumerable<Vec3> Sample(Vec3 from, Vec3 to, bool closed, double chordTol)
    {
        yield return from; yield return to;
    }
}

/// <summary>Circle (or arc). The edge runs counter-clockwise about <see cref="Axis"/>.</summary>
public sealed record CircleCurve(Vec3 Center, Vec3 Axis, Vec3 RefDir, double Radius) : ExactCurve
{
    public double AngleOf(Vec3 p)
    {
        var y = Vec3.Cross(Axis, RefDir);
        var d = p - Center;
        return Math.Atan2(Vec3.Dot(d, y), Vec3.Dot(d, RefDir));
    }

    public Vec3 At(double a)
    {
        var y = Vec3.Cross(Axis, RefDir);
        return Center + (RefDir * Math.Cos(a) + y * Math.Sin(a)) * Radius;
    }

    public override IEnumerable<Vec3> Sample(Vec3 from, Vec3 to, bool closed, double chordTol)
    {
        var a0 = AngleOf(from); var a1 = AngleOf(to);
        var sweep = a1 - a0;
        while (sweep <= 1e-12) sweep += 2 * Math.PI;
        if (closed) sweep = 2 * Math.PI;
        var step = 2 * Math.Acos(Math.Max(-1, 1 - Math.Min(1, chordTol / Math.Max(Radius, 1e-9))));
        int n = Math.Max(2, (int)Math.Ceiling(sweep / Math.Max(step, 1e-3)));
        n = Math.Min(n, 720);
        yield return from;
        for (int i = 1; i < n; i++) yield return At(a0 + sweep * i / n);
        yield return closed ? from : to;
    }
}

/// <summary>Non-rational B-spline curve (clamped).</summary>
public sealed record BSplineCurve(int Degree, Vec3[] Poles, double[] Knots) : ExactCurve
{
    /// <summary>Distinct knot values and their multiplicities (STEP form).</summary>
    public (double[] values, int[] mults) KnotVector()
    {
        var vals = new List<double>(); var mults = new List<int>();
        foreach (var k in Knots)
        {
            if (vals.Count > 0 && Math.Abs(vals[^1] - k) < 1e-14) mults[^1]++;
            else { vals.Add(k); mults.Add(1); }
        }
        return (vals.ToArray(), mults.ToArray());
    }

    public Vec3 Eval(double u)
    {
        int p = Degree, n = Poles.Length - 1;
        u = Math.Clamp(u, Knots[p], Knots[n + 1]);
        int span = FindSpan(n, p, u, Knots);
        var N = Basis(span, u, p, Knots);
        var r = Vec3.Zero;
        for (int i = 0; i <= p; i++) r += Poles[span - p + i] * N[i];
        return r;
    }

    public override IEnumerable<Vec3> Sample(Vec3 from, Vec3 to, bool closed, double chordTol)
    {
        int n = Math.Clamp(Poles.Length * 4, 8, 400);
        double u0 = Knots[Degree], u1 = Knots[^(Degree + 1)];
        yield return from;
        for (int i = 1; i < n; i++) yield return Eval(u0 + (u1 - u0) * i / n);
        yield return to;
    }

    internal static int FindSpan(int n, int p, double u, double[] U)
    {
        if (u >= U[n + 1]) return n;
        if (u <= U[p]) return p;
        int low = p, high = n + 1, mid = (low + high) / 2;
        while (u < U[mid] || u >= U[mid + 1])
        {
            if (u < U[mid]) high = mid; else low = mid;
            mid = (low + high) / 2;
        }
        return mid;
    }

    internal static double[] Basis(int i, double u, int p, double[] U)
    {
        var N = new double[p + 1]; var left = new double[p + 1]; var right = new double[p + 1];
        N[0] = 1;
        for (int j = 1; j <= p; j++)
        {
            left[j] = u - U[i + 1 - j];
            right[j] = U[i + j] - u;
            double saved = 0;
            for (int r = 0; r < j; r++)
            {
                var den = right[r + 1] + left[j - r];
                var temp = den != 0 ? N[r] / den : 0;
                N[r] = saved + right[r + 1] * temp;
                saved = left[j - r] * temp;
            }
            N[j] = saved;
        }
        return N;
    }

    /// <summary>Global interpolation through points (Piegl &amp; Tiller A9.1), chord-length parameters.</summary>
    public static BSplineCurve Interpolate(IReadOnlyList<Vec3> pts, int degree = 3)
    {
        int n = pts.Count - 1;
        if (n < 1) throw new ArgumentException("need at least two points");
        int p = Math.Min(degree, n);
        var uk = new double[n + 1];
        double total = 0;
        for (int k = 1; k <= n; k++) total += Vec3.Distance(pts[k], pts[k - 1]);
        if (total < 1e-15) total = 1;
        for (int k = 1; k <= n; k++) uk[k] = uk[k - 1] + Vec3.Distance(pts[k], pts[k - 1]) / total;
        uk[n] = 1;
        var U = new double[n + p + 2];
        for (int i = n + 1; i < U.Length; i++) U[i] = 1;
        for (int j = 1; j <= n - p; j++)
        {
            double s = 0;
            for (int i = j; i <= j + p - 1; i++) s += uk[i];
            U[j + p] = s / p;
        }
        // banded system A P = Q (solved densely; edges have at most a few hundred points)
        var A = new double[n + 1, n + 1];
        for (int k = 0; k <= n; k++)
        {
            int span = FindSpan(n, p, uk[k], U);
            var N = Basis(span, uk[k], p, U);
            for (int i = 0; i <= p; i++) A[k, span - p + i] = N[i];
        }
        var P = SolveBanded(A, pts.ToArray(), n + 1, p);
        return new BSplineCurve(p, P, U);
    }

    /// <summary>
    /// Gaussian elimination limited to the band of the interpolation matrix (row k has non-zeros only in columns
    /// span-p..span); collocation matrices with averaged knots are totally positive, so no pivoting is needed.
    /// O(n p²) instead of O(n³).
    /// </summary>
    static Vec3[] SolveBanded(double[,] A, Vec3[] b, int n, int p)
    {
        var M = (double[,])A.Clone(); var x = (Vec3[])b.Clone();
        int w = 2 * p + 1;
        for (int c = 0; c < n; c++)
        {
            var piv = M[c, c];
            if (Math.Abs(piv) < 1e-300) return SolveDense(A, b, n); // not banded as expected: the general solver
            for (int r = c + 1; r < Math.Min(n, c + w); r++)
            {
                var f = M[r, c] / piv;
                if (f == 0) continue;
                for (int k = c; k < Math.Min(n, c + w); k++) M[r, k] -= f * M[c, k];
                x[r] -= x[c] * f;
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            var s = x[r];
            for (int k = r + 1; k < Math.Min(n, r + w); k++) s -= x[k] * M[r, k];
            x[r] = s / M[r, r];
        }
        return x;
    }

    static Vec3[] SolveDense(double[,] A, Vec3[] b, int n)
    {
        var x = (Vec3[])b.Clone();
        var M = (double[,])A.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c; double best = Math.Abs(M[c, c]);
            for (int r = c + 1; r < n; r++) if (Math.Abs(M[r, c]) > best) { best = Math.Abs(M[r, c]); piv = r; }
            if (best < 1e-300) continue;
            if (piv != c)
            {
                for (int k = 0; k < n; k++) (M[c, k], M[piv, k]) = (M[piv, k], M[c, k]);
                (x[c], x[piv]) = (x[piv], x[c]);
            }
            for (int r = c + 1; r < n; r++)
            {
                var f = M[r, c] / M[c, c];
                if (f == 0) continue;
                for (int k = c; k < n; k++) M[r, k] -= f * M[c, k];
                x[r] -= x[c] * f;
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            var s = x[r];
            for (int k = r + 1; k < n; k++) s -= x[k] * M[r, k];
            x[r] = Math.Abs(M[r, r]) > 1e-300 ? s / M[r, r] : s;
        }
        return x;
    }
}

/// <summary>Ellipse: Center + Major cos t * XDir + Minor sin t * (Axis × XDir), counter-clockwise about Axis.</summary>
public sealed record EllipseCurve(Vec3 Center, Vec3 Axis, Vec3 XDir, double Major, double Minor) : ExactCurve
{
    public Vec3 At(double t) => Center + XDir * (Major * Math.Cos(t)) + Vec3.Cross(Axis, XDir) * (Minor * Math.Sin(t));
    public double AngleOf(Vec3 p)
    {
        var y = Vec3.Cross(Axis, XDir); var d = p - Center;
        return Math.Atan2(Vec3.Dot(d, y) / Minor, Vec3.Dot(d, XDir) / Major);
    }
    public override IEnumerable<Vec3> Sample(Vec3 from, Vec3 to, bool closed, double chordTol)
    {
        var a0 = AngleOf(from); var a1 = AngleOf(to);
        var sweep = a1 - a0; while (sweep <= 1e-12) sweep += 2 * Math.PI;
        if (closed) sweep = 2 * Math.PI;
        int n = Math.Clamp((int)Math.Ceiling(sweep / 0.05), 4, 720);
        yield return from;
        for (int i = 1; i < n; i++) yield return At(a0 + sweep * i / n);
        yield return closed ? from : to;
    }
}
