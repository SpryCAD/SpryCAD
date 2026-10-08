namespace SpryCad.Kernel.Native;

/// <summary>
/// Spine of a pipe (canal) surface: a C1 cubic Hermite curve through exact points with exact unit tangents,
/// parameterized by cumulative chord length (≈ arc length). Rolling-ball blends keep the two surfaces the
/// ball rolls on (<see cref="SupportA"/>, <see cref="SupportB"/>): the pipe touches them tangentially.
/// A rotation-minimizing frame gives the angle parameter around the spine.
/// </summary>
public sealed class PipeSpine
{
    public Vec3[] P { get; }
    public Vec3[] D { get; }
    public double[] S { get; }
    public NSurface? SupportA { get; }
    public NSurface? SupportB { get; }
    /// <summary>
    /// Every surface the ball rolls on along its chain (a composite fillet switching from face to face): the blend of
    /// one segment touches the others' surfaces tangentially where it switches and lies on their open side elsewhere.
    /// </summary>
    public IReadOnlyList<NSurface> Rolls { get; init; } = Array.Empty<NSurface>();

    const int Sub = 8;
    readonly Vec3[] _sp;      // dense samples
    readonly double[] _ss;    // their parameters
    readonly Vec3[] _n1;      // rotation-minimizing normal at the samples
    readonly Vec3[] _segC;    // bounding sphere per Hermite segment
    readonly double[] _segR;
    // a bounding-sphere tree over the segments (node k: children 2k+1, 2k+2 or a leaf segment) for nearest-point searches
    readonly Vec3[] _bvC;
    readonly double[] _bvR;
    readonly int[] _bvLo, _bvHi;
    public Box3 Box { get; }
    /// <summary>Smallest radius of curvature of the spine (from the samples).</summary>
    public double MinCurvatureRadius { get; }

    public PipeSpine(IReadOnlyList<Vec3> pts, IReadOnlyList<Vec3> tangents, NSurface? supportA = null, NSurface? supportB = null)
    {
        if (pts.Count < 2 || pts.Count != tangents.Count) throw new ArgumentException("spine needs at least two nodes with tangents");
        P = pts.ToArray(); D = tangents.Select(t => t.Normalized).ToArray();
        SupportA = supportA; SupportB = supportB;
        S = new double[P.Length];
        for (int i = 1; i < P.Length; i++)
        {
            var h = Vec3.Distance(P[i], P[i - 1]);
            if (h < 1e-12) throw new ArgumentException("coincident spine nodes");
            S[i] = S[i - 1] + h;
        }
        int nseg = P.Length - 1;
        int ns = nseg * Sub + 1;
        _sp = new Vec3[ns]; _ss = new double[ns]; _n1 = new Vec3[ns];
        for (int i = 0; i < nseg; i++)
            for (int j = 0; j < Sub; j++)
            {
                var s = S[i] + (S[i + 1] - S[i]) * j / Sub;
                _ss[i * Sub + j] = s; _sp[i * Sub + j] = Eval(s);
            }
        _ss[^1] = S[^1]; _sp[^1] = P[^1];
        // rotation-minimizing frame by double reflection (Wang et al. 2008)
        var t0 = Deriv(_ss[0]).Normalized;
        _n1[0] = t0.AnyPerpendicular().Normalized;
        var tPrev = t0;
        double maxK = 0;
        for (int k = 0; k + 1 < ns; k++)
        {
            var tNext = Deriv(_ss[k + 1]).Normalized;
            var v1 = _sp[k + 1] - _sp[k]; var c1 = Vec3.Dot(v1, v1);
            var r = _n1[k];
            if (c1 < 1e-300) { _n1[k + 1] = r; tPrev = tNext; continue; }
            var rL = r - v1 * (2 / c1 * Vec3.Dot(v1, r));
            var tL = tPrev - v1 * (2 / c1 * Vec3.Dot(v1, tPrev));
            var v2 = tNext - tL; var c2 = Vec3.Dot(v2, v2);
            var rn = c2 < 1e-300 ? rL : rL - v2 * (2 / c2 * Vec3.Dot(v2, rL));
            rn -= tNext * Vec3.Dot(rn, tNext);
            _n1[k + 1] = rn.Normalized;
            var ang = Math.Acos(Math.Clamp(Vec3.Dot(tPrev, tNext), -1, 1));
            maxK = Math.Max(maxK, ang / Math.Sqrt(c1));
            tPrev = tNext;
        }
        MinCurvatureRadius = maxK > 1e-12 ? 1 / maxK : double.PositiveInfinity;
        _segC = new Vec3[nseg]; _segR = new double[nseg];
        var box = Box3.Empty;
        for (int i = 0; i < nseg; i++)
        {
            var c = Vec3.Zero;
            for (int j = 0; j <= Sub; j++) c += _sp[i * Sub + j];
            c /= Sub + 1;
            double rad = 0;
            for (int j = 0; j <= Sub; j++) rad = Math.Max(rad, Vec3.Distance(c, _sp[i * Sub + j]));
            // the curve between samples bulges by at most a fraction of the sample spacing
            _segC[i] = c; _segR[i] = rad + (S[i + 1] - S[i]) / Sub * 0.25 + 1e-12;
        }
        foreach (var p in _sp) box.Add(p);
        Box = box;
        int nodes = 1; while (nodes < 2 * nseg) nodes *= 2;
        _bvC = new Vec3[nodes]; _bvR = new double[nodes]; _bvLo = new int[nodes]; _bvHi = new int[nodes];
        BuildTree(0, 0, nseg);
    }

    void BuildTree(int k, int lo, int hi)
    {
        _bvLo[k] = lo; _bvHi[k] = hi;
        if (hi - lo == 1) { _bvC[k] = _segC[lo]; _bvR[k] = _segR[lo]; return; }
        int mid = (lo + hi) / 2;
        BuildTree(2 * k + 1, lo, mid); BuildTree(2 * k + 2, mid, hi);
        // the sphere enclosing both children's spheres
        Vec3 ca = _bvC[2 * k + 1], cb = _bvC[2 * k + 2]; double ra = _bvR[2 * k + 1], rb = _bvR[2 * k + 2];
        var d = Vec3.Distance(ca, cb);
        if (d + rb <= ra) { _bvC[k] = ca; _bvR[k] = ra; }
        else if (d + ra <= rb) { _bvC[k] = cb; _bvR[k] = rb; }
        else
        {
            var r = 0.5 * (d + ra + rb);
            _bvC[k] = ca + (cb - ca) * ((r - ra) / d);
            _bvR[k] = r * (1 + 1e-12) + 1e-12;
        }
    }

    public double S0 => S[0];
    public double S1 => S[^1];
    public double Length => S[^1] - S[0];

    int Seg(double s)
    {
        if (s <= S[0]) return 0;
        if (s >= S[^1]) return S.Length - 2;
        int lo = 0, hi = S.Length - 1;
        while (hi - lo > 1) { int m = (lo + hi) / 2; if (S[m] <= s) lo = m; else hi = m; }
        return lo;
    }

    public Vec3 Eval(double s)
    {
        int i = Seg(s); var h = S[i + 1] - S[i];
        var t = Math.Clamp((s - S[i]) / h, 0, 1);
        double t2 = t * t, t3 = t2 * t;
        return P[i] * (2 * t3 - 3 * t2 + 1) + D[i] * (h * (t3 - 2 * t2 + t)) + P[i + 1] * (-2 * t3 + 3 * t2) + D[i + 1] * (h * (t3 - t2));
    }

    /// <summary>d/ds of <see cref="Eval"/> (≈ unit tangent).</summary>
    public Vec3 Deriv(double s)
    {
        int i = Seg(s); var h = S[i + 1] - S[i];
        var t = Math.Clamp((s - S[i]) / h, 0, 1);
        double t2 = t * t;
        return (P[i] * (6 * t2 - 6 * t) + D[i] * (h * (3 * t2 - 4 * t + 1)) + P[i + 1] * (-6 * t2 + 6 * t) + D[i + 1] * (h * (3 * t2 - 2 * t))) / h;
    }

    public Vec3 Deriv2(double s)
    {
        int i = Seg(s); var h = S[i + 1] - S[i];
        var t = Math.Clamp((s - S[i]) / h, 0, 1);
        return (P[i] * (12 * t - 6) + D[i] * (h * (6 * t - 4)) + P[i + 1] * (-12 * t + 6) + D[i + 1] * (h * (6 * t - 2))) / (h * h);
    }

    /// <summary><see cref="Eval"/>, <see cref="Deriv"/> and <see cref="Deriv2"/> at once.</summary>
    void Derivs(double s, out Vec3 c, out Vec3 d1, out Vec3 d2)
    {
        int i = Seg(s); var h = S[i + 1] - S[i];
        var t = Math.Clamp((s - S[i]) / h, 0, 1);
        double t2 = t * t, t3 = t2 * t;
        c = P[i] * (2 * t3 - 3 * t2 + 1) + D[i] * (h * (t3 - 2 * t2 + t)) + P[i + 1] * (-2 * t3 + 3 * t2) + D[i + 1] * (h * (t3 - t2));
        d1 = (P[i] * (6 * t2 - 6 * t) + D[i] * (h * (3 * t2 - 4 * t + 1)) + P[i + 1] * (-6 * t2 + 6 * t) + D[i + 1] * (h * (3 * t2 - 2 * t))) / h;
        d2 = (P[i] * (12 * t - 6) + D[i] * (h * (6 * t - 4)) + P[i + 1] * (-12 * t + 6) + D[i + 1] * (h * (6 * t - 2))) / (h * h);
    }

    /// <summary>Closest point of the spine to p: parameter, point and distance.</summary>
    public (double s, Vec3 c, double dist) Closest(Vec3 p)
    {
        // the nearest sample (the first of equally near ones), nearer subtrees first
        double best = double.MaxValue; int bi = 0;
        Span<int> stack = stackalloc int[64];
        int top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            int k = stack[--top];
            var lb = Vec3.Distance(p, _bvC[k]) - _bvR[k];
            if (lb > 0 && lb * lb > best) continue;
            if (_bvHi[k] - _bvLo[k] == 1)
            {
                int i = _bvLo[k];
                for (int j = i * Sub; j <= (i + 1) * Sub; j++)
                {
                    var d2 = (_sp[j] - p).LengthSq;
                    if (d2 < best || d2 == best && j < bi) { best = d2; bi = j; }
                }
                continue;
            }
            int a = 2 * k + 1, b = 2 * k + 2;
            // push the farther child first so the nearer is searched first
            if ((p - _bvC[a]).LengthSq - _bvR[a] * _bvR[a] < (p - _bvC[b]).LengthSq - _bvR[b] * _bvR[b]) { stack[top++] = b; stack[top++] = a; }
            else { stack[top++] = a; stack[top++] = b; }
        }
        double lo = _ss[Math.Max(0, bi - 1)], hi = _ss[Math.Min(_ss.Length - 1, bi + 1)];
        var s = _ss[bi];
        // g = (c - p)·c' and its derivative, from one segment lookup
        double G(double t, out double gp)
        {
            Derivs(t, out var c, out var d1, out var d2);
            var e = c - p;
            gp = Vec3.Dot(d1, d1) + Vec3.Dot(e, d2);
            return Vec3.Dot(e, d1);
        }
        if (G(lo, out _) < 0 && G(hi, out _) > 0)
        {
            // the root of g is bracketed: Newton kept inside the bracket, bisection where it would leave it (it
            // converges slowly or not at all where the root sits at a node, across which c'' jumps)
            var eps = 1e-12 * Math.Max(1, Math.Abs(s));
            for (int it = 0; it < 80; it++)
            {
                var g = G(s, out var gp);
                if (g == 0) break;
                if (g < 0) lo = s; else hi = s;
                var sn = gp > 1e-12 ? s - g / gp : 0.5 * (lo + hi);
                if (!(sn > lo && sn < hi)) sn = 0.5 * (lo + hi);
                bool done = Math.Abs(sn - s) < eps || hi - lo < eps;
                s = sn;
                if (done) break;
            }
        }
        else
            for (int it = 0; it < 40; it++)
            {
                var g = G(s, out var gp);
                double sn;
                if (gp <= 1e-12) sn = g > 0 ? lo : hi; // not convex here: move towards the descent end
                else sn = s - g / gp;
                sn = Math.Clamp(sn, lo, hi);
                if (Math.Abs(sn - s) < 1e-15 * Math.Max(1, Math.Abs(s))) { s = sn; break; }
                s = sn;
            }
        // guard: Newton may stop at a bracket end that is worse than the best sample
        var cs = Eval(s);
        if ((cs - p).LengthSq > best) { s = _ss[bi]; cs = _sp[bi]; }
        return (s, cs, Vec3.Distance(cs, p));
    }

    /// <summary>Tangent and rotation-minimizing normals at s.</summary>
    public (Vec3 T, Vec3 N1, Vec3 N2) Frame(double s)
    {
        var T = Deriv(s).Normalized;
        int lo = 0, hi = _ss.Length - 1;
        if (s <= _ss[0]) hi = 1;
        else if (s >= _ss[^1]) lo = hi - 1;
        else while (hi - lo > 1) { int m = (lo + hi) / 2; if (_ss[m] <= s) lo = m; else hi = m; }
        var f = Math.Clamp((s - _ss[lo]) / (_ss[hi] - _ss[lo]), 0, 1);
        var n = _n1[lo] * (1 - f) + _n1[hi] * f;
        n -= T * Vec3.Dot(n, T);
        n = n.Normalized;
        return (T, n, Vec3.Cross(T, n));
    }

    /// <summary>Point of the pipe of radius r at angle θ (about the spine) and spine parameter s.</summary>
    public Vec3 Surface(double theta, double s, double r)
    {
        var (_, n1, n2) = Frame(s);
        return Eval(s) + (n1 * Math.Cos(theta) + n2 * Math.Sin(theta)) * r;
    }

    /// <summary>(θ, s) of a point near the pipe; θ is wrapped next to the hint.</summary>
    public Vec2 UV(Vec3 p, double uHint = double.NaN)
    {
        var (s, c, _) = Closest(p);
        var (_, n1, n2) = Frame(s);
        var d = p - c;
        var th = Math.Atan2(Vec3.Dot(d, n2), Vec3.Dot(d, n1));
        return new Vec2(double.IsNaN(uHint) ? th : NCurve.Wrap(th, uHint), s);
    }

    public Vec3 Project(Vec3 p, double r)
    {
        var (s, c, dist) = Closest(p);
        var d = dist > 1e-300 ? (p - c) / dist : Frame(s).N1;
        return c + d * r;
    }

    public Vec3 Normal(Vec3 p)
    {
        var (s, c, dist) = Closest(p);
        return dist > 1e-300 ? (p - c) / dist : Frame(s).N1;
    }

    public PipeSpine Transformed(in Transform t)
    {
        var tt = t;
        return new PipeSpine(P.Select(p => tt.Point(p)).ToList(), D.Select(d => tt.Vector(d)).ToList(),
            SupportA?.Transformed(t), SupportB?.Transformed(t)) { Rolls = Rolls.Select(s => s.Transformed(tt)).ToList() };
    }

    /// <summary>Same spine geometry (nodes and tangents within tol).</summary>
    public bool SameAs(PipeSpine o, double tol)
    {
        if (ReferenceEquals(this, o)) return true;
        if (P.Length != o.P.Length) return false;
        for (int i = 0; i < P.Length; i++)
            if (Vec3.Distance(P[i], o.P[i]) > tol || Vec3.Dot(D[i], o.D[i]) < 1 - 1e-10) return false;
        return true;
    }

    /// <summary>Ray parameters where o + t d crosses the pipe of radius r (isolation by the distance bound).</summary>
    public List<double> RayHits(Vec3 o, Vec3 d, double r)
    {
        var res = new List<double>();
        var dl = d.Length;
        if (dl < 1e-300) return res;
        // clip the ray to the padded spine box
        var pad = r * 1.01 + 1e-9;
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
        double F(double t) => Closest(o + d * t).dist - r;
        var minL = Math.Max(1e-9, r * 1e-7);
        void Iso(double a, double b, double fa, double fb, int depth)
        {
            var L = (b - a) * dl;
            if (Math.Abs(fa) + Math.Abs(fb) > L * 1.0001 + 1e-12) return;
            if (depth >= 60 || L < minL)
            {
                if (fa * fb < 0) res.Add(Roots.Bisect(F, a, b));
                else if (fa == 0) res.Add(a);
                return;
            }
            var m = 0.5 * (a + b); var fm = F(m);
            Iso(a, m, fa, fm, depth + 1);
            Iso(m, b, fm, fb, depth + 1);
        }
        const int n = 16;
        double pt = t0, pf = F(t0);
        for (int i = 1; i <= n; i++)
        {
            var t = t0 + (t1 - t0) * i / n; var f = F(t);
            Iso(pt, t, pf, f, 0);
            pt = t; pf = f;
        }
        res.Sort();
        for (int i = res.Count - 1; i > 0; i--) if (Math.Abs(res[i] - res[i - 1]) * dl < minL * 10) res.RemoveAt(i);
        return res;
    }
}

/// <summary>
/// An iso-parameter curve of a surface (face parameters): u fixed (axis 0, running along v) or v fixed (axis 1);
/// sign −1 runs it backwards so edges can keep increasing parameters.
/// </summary>
public sealed class NIso : NCurve
{
    public NSurface S { get; }
    public int Axis { get; }
    public double C { get; }
    public int Sign { get; }

    public NIso(NSurface s, int axis, double c, int sign) { S = s; Axis = axis; C = c; Sign = sign; }

    public override Vec3 Eval(double t) => Axis == 0 ? S.Eval(C, Sign * t) : S.Eval(Sign * t, C);
    public override Vec3 Deriv(double t)
    {
        const double h = 1e-6;
        return (Eval(t + h) - Eval(t - h)) / (2 * h);
    }
    public override double Param(Vec3 p, double? hint = null)
    {
        double? h = hint.HasValue ? Sign * hint.Value : null;
        var q = Axis == 0 ? S.UV(p, C, h ?? double.NaN) : S.UV(p, h ?? double.NaN, C);
        return Sign * (Axis == 0 ? q.Y : q.X);
    }
    public override NCurve Transformed(in Transform t) => new NIso(S.Transformed(t), Axis, C, Sign);
    public override int Samples(double t0, double t1, double tol) => 32;
    public override ExactCurve ToExact() => BSplineCurve.Interpolate(Enumerable.Range(0, 33).Select(i => Eval(i / 32.0)).ToList(), 3);
}

/// <summary>Where a rolling-ball pipe touches one of its support surfaces: the support's foot point of the spine.</summary>
public sealed class NContact : NCurve
{
    public PipeSpine Spine { get; }
    public bool OnB { get; }
    NSurface Support => OnB ? Spine.SupportB! : Spine.SupportA!;

    public NContact(PipeSpine spine, bool onB)
    {
        if ((onB ? spine.SupportB : spine.SupportA) == null) throw new ArgumentException("spine has no support surface");
        Spine = spine; OnB = onB;
    }

    public override Vec3 Eval(double t) => Support.Project(Spine.Eval(t));
    public override Vec3 Deriv(double t)
    {
        var h = 1e-6 * Math.Max(1, Spine.Length);
        double a = Math.Max(Spine.S0, t - h), b = Math.Min(Spine.S1, t + h);
        return (Eval(b) - Eval(a)) / (b - a);
    }
    public override double Param(Vec3 p, double? hint = null) => Spine.Closest(p).s;
    public override NCurve Transformed(in Transform t) => new NContact(Spine.Transformed(t), OnB);
    public override int Samples(double t0, double t1, double tol) => Math.Clamp((int)Math.Ceiling(Math.Abs(t1 - t0) / Math.Max(Spine.Length, 1e-12) * (Spine.P.Length - 1) * 4), 2, 600);
    public override ExactCurve ToExact()
    {
        var n = Math.Clamp((Spine.P.Length - 1) * 3, 8, 600);
        return BSplineCurve.Interpolate(Enumerable.Range(0, n + 1).Select(i => Eval(Spine.S0 + Spine.Length * i / n)).ToList(), 3);
    }
}

/// <summary>
/// The intersection curve of two surfaces between two points on it (short pieces, e.g. a blend cross-section on a
/// face): parameter λ ∈ [0, 1] maps to the intersection point nearest to the chord point P0 + λ (P1 − P0).
/// </summary>
public sealed class NSection : NCurve
{
    public NSurface A { get; }
    public NSurface B { get; }
    public Vec3 P0 { get; }
    public Vec3 P1 { get; }

    public NSection(NSurface a, NSurface b, Vec3 p0, Vec3 p1) { A = a; B = b; P0 = p0; P1 = p1; }

    public override Vec3 Eval(double t)
    {
        if (t == 0) return P0;
        if (t == 1) return P1;
        return NSurface.ProjectToIntersection(A, B, P0 + (P1 - P0) * t);
    }
    public override Vec3 Deriv(double t)
    {
        const double h = 1e-6;
        double a = Math.Max(0, t - h), b = Math.Min(1, t + h);
        return (Eval(b) - Eval(a)) / (b - a);
    }
    public override double Param(Vec3 p, double? hint = null)
    {
        var d = P1 - P0; var l2 = d.LengthSq;
        var t0 = l2 > 0 ? Vec3.Dot(p - P0, d) / l2 : 0;
        double lo = t0 - 0.25, hi = t0 + 0.25;
        for (int it = 0; it < 60; it++)
        {
            var m1 = lo + (hi - lo) * 0.382; var m2 = lo + (hi - lo) * 0.618;
            if ((Eval(m1) - p).LengthSq < (Eval(m2) - p).LengthSq) hi = m2; else lo = m1;
        }
        return 0.5 * (lo + hi);
    }
    public override NCurve Transformed(in Transform t) => new NSection(A.Transformed(t), B.Transformed(t), t.Point(P0), t.Point(P1));
    public override int Samples(double t0, double t1, double tol) => Math.Clamp((int)Math.Ceiling(Math.Abs(t1 - t0) * 16), 2, 200);
    public override ExactCurve ToExact() => BSplineCurve.Interpolate(Enumerable.Range(0, 17).Select(i => Eval(i / 16.0)).ToList(), 3);
}
