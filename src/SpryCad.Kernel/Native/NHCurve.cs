namespace SpryCad.Kernel.Native;

/// <summary>A point in homogeneous coordinates: (w x, w y, w z, w).</summary>
internal readonly record struct H(Vec3 V, double W)
{
    public static H operator +(H a, H b) => new(a.V + b.V, a.W + b.W);
    public static H operator -(H a, H b) => new(a.V - b.V, a.W - b.W);
    public static H operator *(H a, double s) => new(a.V * s, a.W * s);
    public static H operator *(double s, H a) => new(a.V * s, a.W * s);
    /// <summary>The Cartesian point.</summary>
    public Vec3 P => V / W;
    public static H Of(Vec3 p, double w = 1) => new(p * w, w);
}

/// <summary>
/// A clamped (rational) B-spline curve with homogeneous poles, for building surfaces: exact lines and arcs, degree
/// elevation, knot refinement (Piegl &amp; Tiller ch. 5) and global interpolation with end derivatives (ch. 9).
/// </summary>
internal sealed class HCurve
{
    public readonly int P;
    public readonly double[] U;
    public readonly H[] Q;

    public HCurve(int p, double[] u, H[] q)
    {
        if (u.Length != q.Length + p + 1) throw new ArgumentException("B-spline curve: knots do not match the poles");
        P = p; U = u; Q = q;
    }

    public double T0 => U[P];
    public double T1 => U[^(P + 1)];

    public H EvalH(double t)
    {
        int n = Q.Length - 1;
        t = Math.Clamp(t, T0, T1);
        int span = BSplineCurve.FindSpan(n, P, t, U);
        Span<double> N = stackalloc double[P + 1];
        BSplineCurve.Basis(span, t, P, U, N);
        var r = new H(Vec3.Zero, 0);
        for (int i = 0; i <= P; i++) r += Q[span - P + i] * N[i];
        return r;
    }

    public Vec3 Eval(double t) => EvalH(t).P;

    /// <summary>The straight line from a to b (degree 1, parameter 0..1).</summary>
    public static HCurve Line(Vec3 a, Vec3 b) => new(1, new double[] { 0, 0, 1, 1 }, new[] { H.Of(a), H.Of(b) });

    /// <summary>
    /// The circular arc about c of radius r from c + r x, turning by sweep (0 &lt; sweep ≤ 2π) towards y (x, y unit and
    /// perpendicular): rational quadratic, one Bézier piece per quarter turn or less (Piegl &amp; Tiller A7.1), 0..1.
    /// </summary>
    public static HCurve Arc(Vec3 c, Vec3 x, Vec3 y, double r, double sweep)
    {
        int n = sweep <= Math.PI / 2 + 1e-12 ? 1 : sweep <= Math.PI + 1e-12 ? 2 : sweep <= 1.5 * Math.PI + 1e-12 ? 3 : 4;
        double d = sweep / n, w1 = Math.Cos(d / 2);
        var q = new H[2 * n + 1];
        q[0] = H.Of(c + x * r);
        for (int i = 1; i <= n; i++)
        {
            double a = d * i, m = a - d / 2;
            q[2 * i - 1] = H.Of(c + (x * Math.Cos(m) + y * Math.Sin(m)) * (r / w1), w1);
            q[2 * i] = H.Of(c + (x * Math.Cos(a) + y * Math.Sin(a)) * r);
        }
        var u = new double[2 * n + 4];
        u[0] = u[1] = u[2] = 0;
        for (int i = 1; i < n; i++) u[1 + 2 * i] = u[2 + 2 * i] = (double)i / n;
        u[^1] = u[^2] = u[^3] = 1;
        return new HCurve(2, u, q);
    }

    /// <summary>The same curve with its poles mapped (e.g. a degenerate copy collapsed to a point).</summary>
    public HCurve WithPoles(Func<int, H, H> f) => new(P, U, Q.Select((q, i) => f(i, q)).ToArray());

    /// <summary>Distinct interior knot values and their multiplicities.</summary>
    public List<(double u, int m)> InteriorKnots()
    {
        var r = new List<(double, int)>();
        for (int i = P + 1; i < U.Length - P - 1; i++)
        {
            if (r.Count > 0 && r[^1].Item1 == U[i]) r[^1] = (U[i], r[^1].Item2 + 1);
            else r.Add((U[i], 1));
        }
        return r;
    }

    /// <summary>Knot refinement: the same curve with the sorted knots x inserted (Piegl &amp; Tiller A5.4).</summary>
    public HCurve Refine(IReadOnlyList<double> x)
    {
        if (x.Count == 0) return this;
        int n = Q.Length - 1, p = P, m = n + p + 1, r = x.Count - 1;
        int a = BSplineCurve.FindSpan(n, p, x[0], U), b = BSplineCurve.FindSpan(n, p, x[r], U) + 1;
        var ub = new double[m + r + 2];
        var qw = new H[n + r + 2];
        for (int j = 0; j <= a - p; j++) qw[j] = Q[j];
        for (int j = b - 1; j <= n; j++) qw[j + r + 1] = Q[j];
        for (int j = 0; j <= a; j++) ub[j] = U[j];
        for (int j = b + p; j <= m; j++) ub[j + r + 1] = U[j];
        int i = b + p - 1, k = b + p + r;
        for (int j = r; j >= 0; j--)
        {
            while (x[j] <= U[i] && i > a)
            {
                qw[k - p - 1] = Q[i - p - 1];
                ub[k] = U[i];
                k--; i--;
            }
            qw[k - p - 1] = qw[k - p];
            for (int l = 1; l <= p; l++)
            {
                int ind = k - p + l;
                var alfa = ub[k + l] - x[j];
                if (Math.Abs(alfa) == 0) qw[ind - 1] = qw[ind];
                else
                {
                    alfa /= ub[k + l] - U[i - p + l];
                    qw[ind - 1] = qw[ind - 1] * alfa + qw[ind] * (1 - alfa);
                }
            }
            ub[k] = x[j];
            k--;
        }
        return new HCurve(p, ub, qw);
    }

    /// <summary>The curve raised to degree q (unchanged when it is already that high): split into Bézier pieces, each elevated.</summary>
    public HCurve Elevate(int q)
    {
        if (P >= q) return this;
        // Bézier pieces: every interior knot to multiplicity p
        var ins = new List<double>();
        foreach (var (u, m) in InteriorKnots()) for (int k = m; k < P; k++) ins.Add(u);
        var c = Refine(ins);
        int pieces = c.InteriorKnots().Count + 1;
        var poles = new List<H>();
        for (int s = 0; s < pieces; s++)
        {
            var b = new H[P + 1];
            for (int i = 0; i <= P; i++) b[i] = c.Q[s * P + i];
            for (int d = P; d < q; d++)
            {
                // degree d to d + 1: B'_i = i/(d+1) B_(i-1) + (1 - i/(d+1)) B_i
                var nb = new H[d + 2];
                nb[0] = b[0]; nb[d + 1] = b[d];
                for (int i = 1; i <= d; i++) nb[i] = b[i - 1] * ((double)i / (d + 1)) + b[i] * (1 - (double)i / (d + 1));
                b = nb;
            }
            for (int i = s == 0 ? 0 : 1; i <= q; i++) poles.Add(b[i]);
        }
        var knots = new List<double>();
        for (int i = 0; i <= q; i++) knots.Add(c.T0);
        foreach (var (u, _) in c.InteriorKnots()) for (int i = 0; i < q; i++) knots.Add(u);
        for (int i = 0; i <= q; i++) knots.Add(c.T1);
        return new HCurve(q, knots.ToArray(), poles.ToArray());
    }

    /// <summary>The curves on one knot vector: the same degree (the highest) and every knot of each.</summary>
    public static List<HCurve> Compatible(IReadOnlyList<HCurve> curves, int minDegree = 1)
    {
        int p = Math.Max(minDegree, curves.Max(c => c.P));
        var el = curves.Select(c => c.Elevate(p)).ToList();
        // the union: each value with the highest multiplicity any curve has (values from equal fractions are equal)
        var all = new SortedDictionary<double, int>();
        foreach (var c in el)
            foreach (var (u, m) in c.InteriorKnots())
                all[u] = Math.Max(all.GetValueOrDefault(u), m);
        return el.Select(c =>
        {
            var own = c.InteriorKnots().ToDictionary(k => k.u, k => k.m);
            var ins = new List<double>();
            foreach (var (u, m) in all) for (int k = own.GetValueOrDefault(u); k < m; k++) ins.Add(u);
            return c.Refine(ins);
        }).ToList();
    }

    /// <summary>The curve with its parameter range mapped linearly onto [a, b].</summary>
    public HCurve Reparam(double a, double b)
    {
        double t0 = T0, t1 = T1;
        return new HCurve(P, U.Select(u => u == t0 ? a : u == t1 ? b : a + (u - t0) / (t1 - t0) * (b - a)).ToArray(), Q);
    }

    /// <summary>Joins curves end to end (each starting where the one before ends, consecutive ranges, one degree): C0 at the joins.</summary>
    public static HCurve Concat(IReadOnlyList<HCurve> parts)
    {
        if (parts.Count == 1) return parts[0];
        int p = parts[0].P;
        var knots = new List<double>(); var poles = new List<H>();
        for (int k = 0; k < parts.Count; k++)
        {
            var c = parts[k];
            if (c.P != p) throw new ArgumentException("concatenated curves must have one degree");
            // knots: the first's start (p + 1 times), every piece's interior knots, the joins p times, the last's end
            if (k == 0) for (int i = 0; i <= p; i++) knots.Add(c.T0);
            for (int i = p + 1; i < c.U.Length - p - 1; i++) knots.Add(c.U[i]);
            if (k < parts.Count - 1) for (int i = 0; i < p; i++) knots.Add(c.T1);
            else for (int i = 0; i <= p; i++) knots.Add(c.T1);
            for (int i = k == 0 ? 0 : 1; i < c.Q.Length; i++) poles.Add(c.Q[i]);
        }
        return new HCurve(p, knots.ToArray(), poles.ToArray());
    }

    /// <summary>The curve cut at t (inside its range) into the piece before and the piece after.</summary>
    public (HCurve before, HCurve after) Split(double t)
    {
        int mult = U.Count(u => u == t);
        var c = Refine(Enumerable.Repeat(t, Math.Max(0, P - mult)).ToList());
        int s = Array.IndexOf(c.U, t);
        var ua = c.U.Take(s).Concat(Enumerable.Repeat(t, c.P + 1)).ToArray();
        var ub = Enumerable.Repeat(t, c.P + 1).Concat(c.U.Skip(s + c.P)).ToArray();
        return (new HCurve(c.P, ua, c.Q.Take(s).ToArray()), new HCurve(c.P, ub, c.Q.Skip(s - 1).ToArray()));
    }

    /// <summary>The non-rational curve (all weights 1) as a B-spline of Cartesian poles.</summary>
    public BSplineCurve ToBSpline() => new(P, Q.Select(q => q.P).ToArray(), U);
}

/// <summary>
/// Global interpolation (Piegl &amp; Tiller 9.2.1-9.2.2): a clamped B-spline through values at given parameters,
/// optionally with given first derivatives at the ends. The system depends only on the parameters, so one
/// factorization serves any number of right-hand sides (every column of a skinned surface).
/// </summary>
internal sealed class Interpolation
{
    public readonly int P;
    public readonly double[] U;
    readonly double[] _t;
    readonly bool _d0, _d1;
    readonly double[,] _lu;
    readonly int[] _piv;
    public int Count => _piv.Length;

    public Interpolation(IReadOnlyList<double> t, int degree, bool dStart, bool dEnd)
    {
        int np = t.Count;
        if (np < 2) throw new ArgumentException("interpolation needs two points");
        _t = t.ToArray(); _d0 = dStart; _d1 = dEnd;
        int m = np - 1 + (dStart ? 1 : 0) + (dEnd ? 1 : 0); // last pole index
        int p = Math.Min(degree, m);
        P = p;
        // knots by averaging the parameters (each end parameter counted again for its derivative)
        var a = new List<double>();
        if (dStart) a.Add(t[0]);
        a.AddRange(t);
        if (dEnd) a.Add(t[^1]);
        U = new double[m + p + 2];
        for (int i = 0; i <= p; i++) { U[i] = t[0]; U[m + 1 + i] = t[^1]; }
        for (int j = 1; j <= m - p; j++)
        {
            double s = 0;
            for (int i = j; i <= j + p - 1; i++) s += a[i];
            U[j + p] = s / p;
            // an average that is one of the parameters (equal steps) is that parameter exactly: else it lands a
            // rounding error beside the knots other curves have there, and the union of the two leaves a span of
            // nothing, whose derivatives blow up
            foreach (var tk in t) if (Math.Abs(U[j + p] - tk) < 1e-12 * Math.Max(1, Math.Abs(tk))) U[j + p] = tk;
        }
        // rows: the points in order, the start derivative after the first, the end derivative before the last
        int n = m + 1;
        var A = new double[n, n];
        int row = 0;
        Span<double> N = stackalloc double[p + 1];
        for (int k = 0; k < np; k++)
        {
            int span = BSplineCurve.FindSpan(m, p, t[k], U);
            BSplineCurve.Basis(span, t[k], p, U, N);
            for (int i = 0; i <= p; i++) A[row, span - p + i] = N[i];
            row++;
            if (k == 0 && dStart)
            {
                // C'(t0) = p / (U[p+1] - U[1]) (P1 - P0)
                var f = p / (U[p + 1] - U[1]);
                A[row, 0] = -f; A[row, 1] = f; row++;
            }
            if (k == np - 2 && dEnd)
            {
                // C'(t1) = p / (U[m+p] - U[m]) (Pm - Pm-1)
                var f = p / (U[m + p] - U[m]);
                A[row, m - 1] = -f; A[row, m] = f; row++;
            }
        }
        // LU with partial pivoting
        _piv = new int[n];
        for (int i = 0; i < n; i++) _piv[i] = i;
        for (int k = 0; k < n; k++)
        {
            int best = k; double bv = Math.Abs(A[k, k]);
            for (int i = k + 1; i < n; i++) if (Math.Abs(A[i, k]) > bv) { bv = Math.Abs(A[i, k]); best = i; }
            if (bv < 1e-300) throw new ArgumentException("the interpolation is singular (two sections at the same place?)");
            if (best != k)
            {
                for (int j = 0; j < n; j++) (A[k, j], A[best, j]) = (A[best, j], A[k, j]);
                (_piv[k], _piv[best]) = (_piv[best], _piv[k]);
            }
            for (int i = k + 1; i < n; i++)
            {
                var f = A[i, k] / A[k, k];
                if (f == 0) continue;
                A[i, k] = f;
                for (int j = k + 1; j < n; j++) A[i, j] -= f * A[k, j];
            }
        }
        _lu = A;
    }

    /// <summary>The poles through the values (one per parameter) with the end derivatives (when set up with them).</summary>
    public H[] Solve(IReadOnlyList<H> values, H d0 = default, H d1 = default)
    {
        int n = _piv.Length;
        var b = new H[n];
        int row = 0;
        for (int k = 0; k < values.Count; k++)
        {
            b[row++] = values[k];
            if (k == 0 && _d0) b[row++] = d0;
            if (k == values.Count - 2 && _d1) b[row++] = d1;
        }
        var x = new H[n];
        for (int i = 0; i < n; i++) x[i] = b[_piv[i]];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < i; j++) if (_lu[i, j] != 0) x[i] -= x[j] * _lu[i, j];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = i + 1; j < n; j++) if (_lu[i, j] != 0) x[i] -= x[j] * _lu[i, j];
            x[i] *= 1 / _lu[i, i];
        }
        // the ends are the end values exactly
        x[0] = values[0]; x[n - 1] = values[^1];
        return x;
    }

    public HCurve Curve(IReadOnlyList<H> values, H d0 = default, H d1 = default) => new(P, U, Solve(values, d0, d1));
}
