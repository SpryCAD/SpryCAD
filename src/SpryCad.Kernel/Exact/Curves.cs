namespace SpryCad.Kernel;

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
        Span<double> N = stackalloc double[p + 1];
        Basis(span, u, p, Knots, N);
        var r = Vec3.Zero;
        for (int i = 0; i <= p; i++) r += Poles[span - p + i] * N[i];
        return r;
    }

    /// <summary>Point, first and second derivative at u (Piegl &amp; Tiller A2.3).</summary>
    public void Derivs(double u, out Vec3 c, out Vec3 d1, out Vec3 d2)
    {
        int p = Degree, n = Poles.Length - 1;
        u = Math.Clamp(u, Knots[p], Knots[n + 1]);
        int span = FindSpan(n, p, u, Knots);
        int w = p + 1;
        // ndu: basis values (upper triangle, row r column j at [r·w + j]) and knot differences (lower)
        Span<double> ndu = p < 12 ? stackalloc double[w * w] : new double[w * w];
        Span<double> left = p < 12 ? stackalloc double[w] : new double[w];
        Span<double> right = p < 12 ? stackalloc double[w] : new double[w];
        Span<double> a = p < 12 ? stackalloc double[2 * w] : new double[2 * w];
        Span<double> ders = p < 12 ? stackalloc double[3 * w] : new double[3 * w];
        ndu[0] = 1;
        for (int j = 1; j <= p; j++)
        {
            left[j] = u - Knots[span + 1 - j];
            right[j] = Knots[span + j] - u;
            double saved = 0;
            for (int r = 0; r < j; r++)
            {
                ndu[j * w + r] = right[r + 1] + left[j - r];
                var temp = ndu[r * w + j - 1] / ndu[j * w + r];
                ndu[r * w + j] = saved + right[r + 1] * temp;
                saved = left[j - r] * temp;
            }
            ndu[j * w + j] = saved;
        }
        int nd = Math.Min(2, p);
        for (int j = 0; j <= p; j++) ders[j] = ndu[j * w + p];
        for (int r = 0; r <= p; r++)
        {
            int s1 = 0, s2 = w;
            a[0] = 1;
            for (int k = 1; k <= nd; k++)
            {
                double d = 0;
                int rk = r - k, pk = p - k;
                if (r >= k) { a[s2] = a[s1] / ndu[(pk + 1) * w + rk]; d = a[s2] * ndu[rk * w + pk]; }
                int j1 = rk >= -1 ? 1 : -rk, j2 = r - 1 <= pk ? k - 1 : p - r;
                for (int j = j1; j <= j2; j++)
                {
                    a[s2 + j] = (a[s1 + j] - a[s1 + j - 1]) / ndu[(pk + 1) * w + rk + j];
                    d += a[s2 + j] * ndu[(rk + j) * w + pk];
                }
                if (r <= pk) { a[s2 + k] = -a[s1 + k - 1] / ndu[(pk + 1) * w + r]; d += a[s2 + k] * ndu[r * w + pk]; }
                ders[k * w + r] = d;
                (s1, s2) = (s2, s1);
            }
        }
        double f = p;
        for (int k = 1; k <= nd; k++) { for (int j = 0; j <= p; j++) ders[k * w + j] *= f; f *= p - k; }
        c = d1 = d2 = Vec3.Zero;
        for (int j = 0; j <= p; j++)
        {
            var P = Poles[span - p + j];
            c += P * ders[j];
            d1 += P * ders[w + j];
            if (nd == 2) d2 += P * ders[2 * w + j];
        }
    }

    /// <summary>
    /// Parameter of the point nearest p in [lo, hi], from t: Newton on (c - p)·c' kept inside the bracket (bisection
    /// where it would leave it) when the bracket holds a minimum, else the nearer of the ends and clamped Newton.
    /// NaN if the derivatives are not finite (coincident knots).
    /// </summary>
    public double ClosestParam(Vec3 p, double lo, double hi, double t)
    {
        double G(double s, out double gp, out double d2)
        {
            Derivs(s, out var c, out var d1, out var dd);
            var e = c - p;
            gp = Vec3.Dot(d1, d1) + Vec3.Dot(e, dd);
            d2 = e.LengthSq;
            return Vec3.Dot(e, d1);
        }
        double glo = G(lo, out _, out var dlo), ghi = G(hi, out _, out var dhi);
        if (!double.IsFinite(glo) || !double.IsFinite(ghi)) return double.NaN;
        var eps = 1e-14;
        if (glo < 0 && ghi > 0)
        {
            double a = lo, b = hi, s = Math.Clamp(t, lo, hi);
            for (int it = 0; it < 80; it++)
            {
                var g = G(s, out var gp, out _);
                if (!double.IsFinite(g)) return double.NaN;
                if (g == 0) break;
                if (g < 0) a = s; else b = s;
                var sn = gp > 1e-300 ? s - g / gp : 0.5 * (a + b);
                if (!(sn > a && sn < b)) sn = 0.5 * (a + b);
                bool done = Math.Abs(sn - s) < eps || b - a < eps;
                s = sn;
                if (done) break;
            }
            return s;
        }
        // no sign change: an end, or a minimum Newton finds from t
        double best = dlo <= dhi ? lo : hi, bd = Math.Min(dlo, dhi);
        double q = Math.Clamp(t, lo, hi);
        for (int it = 0; it < 40; it++)
        {
            var g = G(q, out var gp, out _);
            if (!double.IsFinite(g) || gp <= 1e-300) break;
            var qn = Math.Clamp(q - g / gp, lo, hi);
            bool done = Math.Abs(qn - q) < eps;
            q = qn;
            if (done) break;
        }
        G(q, out _, out var dq);
        return dq < bd ? q : best;
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
        var N = new double[p + 1];
        Basis(i, u, p, U, N);
        return N;
    }

    /// <summary>The p + 1 non-zero basis functions at u in span i (Piegl &amp; Tiller A2.2), into N.</summary>
    internal static void Basis(int i, double u, int p, double[] U, Span<double> N)
    {
        // evaluated per point (curves, surfaces): scratch on the stack
        Span<double> left = p < 16 ? stackalloc double[p + 1] : new double[p + 1];
        Span<double> right = p < 16 ? stackalloc double[p + 1] : new double[p + 1];
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
        // banded system A P = Q: row k holds the p + 1 basis values at uk[k], from column span - p
        var spans = new int[n + 1];
        var basis = new double[n + 1][];
        for (int k = 0; k <= n; k++)
        {
            spans[k] = FindSpan(n, p, uk[k], U);
            basis[k] = Basis(spans[k], uk[k], p, U);
        }
        var P = SolveBanded(spans, basis, pts.ToArray(), n + 1, p);
        return new BSplineCurve(p, P, U);
    }

    /// <summary>
    /// Gaussian elimination limited to the band of the interpolation matrix (row k has non-zeros only in columns
    /// span-p..span); collocation matrices with averaged knots are totally positive, so no pivoting is needed.
    /// O(n p²) time; only the band is stored: every entry the elimination reads or writes lies within 2·(2p + 1) - 1
    /// columns centred on the diagonal (O(n p) memory: imported curves have thousands of points).
    /// </summary>
    static Vec3[] SolveBanded(int[] spans, double[][] basis, Vec3[] b, int n, int p)
    {
        int w = 2 * p + 1, W = 2 * w - 1;
        // M[r, k] at r·W + (k - r + w - 1)
        var M = new double[(long)n * W];
        for (int r = 0; r < n; r++)
            for (int i = 0; i <= p; i++)
            {
                int k = spans[r] - p + i, o = k - r + w - 1;
                // outside the band: never read (the dense elimination below does not reach it either)
                if (o >= 0 && o < W) M[(long)r * W + o] = basis[r][i];
            }
        double At(int r, int k) => M[(long)r * W + (k - r + w - 1)];
        var x = (Vec3[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            var piv = At(c, c);
            if (Math.Abs(piv) < 1e-300) return SolveDense(Dense(spans, basis, n, p), b, n); // not banded as expected: the general solver
            for (int r = c + 1; r < Math.Min(n, c + w); r++)
            {
                var f = At(r, c) / piv;
                if (f == 0) continue;
                for (int k = c; k < Math.Min(n, c + w); k++) M[(long)r * W + (k - r + w - 1)] -= f * At(c, k);
                x[r] -= x[c] * f;
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            var s = x[r];
            for (int k = r + 1; k < Math.Min(n, r + w); k++) s -= x[k] * At(r, k);
            x[r] = s / At(r, r);
        }
        return x;
    }

    static double[,] Dense(int[] spans, double[][] basis, int n, int p)
    {
        var A = new double[n, n];
        for (int r = 0; r < n; r++)
            for (int i = 0; i <= p; i++) A[r, spans[r] - p + i] = basis[r][i];
        return A;
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

/// <summary>Hyperbola branch: Center + Semi cosh t * XDir + SemiImag sinh t * (Axis × XDir) (STEP's hyperbola).</summary>
public sealed record HyperbolaCurve(Vec3 Center, Vec3 Axis, Vec3 XDir, double Semi, double SemiImag) : ExactCurve
{
    public Vec3 At(double t) => Center + XDir * (Semi * Math.Cosh(t)) + Vec3.Cross(Axis, XDir) * (SemiImag * Math.Sinh(t));
    public double ParamOf(Vec3 p) => Math.Asinh(Vec3.Dot(p - Center, Vec3.Cross(Axis, XDir)) / SemiImag);
    public override IEnumerable<Vec3> Sample(Vec3 from, Vec3 to, bool closed, double chordTol)
    {
        double t0 = ParamOf(from), t1 = ParamOf(to);
        int n = Math.Clamp((int)Math.Ceiling(Math.Abs(t1 - t0) / 0.02), 1, 2000);
        yield return from;
        for (int i = 1; i < n; i++) yield return At(t0 + (t1 - t0) * i / n);
        yield return to;
    }
}

/// <summary>Parabola: Center + Focal (t² XDir + 2t (Axis × XDir)) (STEP's parabola).</summary>
public sealed record ParabolaCurve(Vec3 Center, Vec3 Axis, Vec3 XDir, double Focal) : ExactCurve
{
    public Vec3 At(double t) => Center + XDir * (Focal * t * t) + Vec3.Cross(Axis, XDir) * (2 * Focal * t);
    public double ParamOf(Vec3 p) => Vec3.Dot(p - Center, Vec3.Cross(Axis, XDir)) / (2 * Focal);
    public override IEnumerable<Vec3> Sample(Vec3 from, Vec3 to, bool closed, double chordTol)
    {
        double t0 = ParamOf(from), t1 = ParamOf(to);
        // chord error 2F·h²/8 per step
        var h = Math.Sqrt(4 * Math.Max(chordTol, 1e-9) / Math.Max(Focal, 1e-12));
        int n = Math.Clamp((int)Math.Ceiling(Math.Abs(t1 - t0) / h), 1, 2000);
        yield return from;
        for (int i = 1; i < n; i++) yield return At(t0 + (t1 - t0) * i / n);
        yield return to;
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
