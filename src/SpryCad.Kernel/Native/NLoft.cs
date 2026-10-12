namespace SpryCad.Kernel.Native;

/// <summary>
/// Exact loft (skinning) through planar sections, optionally following guide curves.
/// <para>
/// Each section is a set of closed loops of lines and arcs (or a single point, first or last only). The loops are
/// matched from section to section (regions and holes by position, the start of each loop by least twist, or by
/// the first guide), and every loop is cut at the same number of breakpoints: all the sections' vertices, matched
/// one to one where the counts allow and by position along the loop where they do not, so each side face of the
/// loft spans one line or arc piece of every section. The pieces are exact rational B-splines (an arc is a
/// rational quadratic), made compatible (one degree, one knot vector), and their poles are interpolated across
/// the sections (Piegl &amp; Tiller's skinning): the surface passes through every section exactly, and the edges
/// between faces are the same curve on both (the interpolation of the breakpoints).
/// </para>
/// <para>
/// A guide curve passes through every section; its points there are breakpoints. The side faces next to it are
/// corrected in homogeneous coordinates the way a Gordon surface is: the difference between the guide and the
/// skinned edge is added across the face, weighted 1 at the guide and 0 at the face's other edge by a smoothstep
/// in u (flat at both edges, so faces that met smoothly still do). The guide becomes the edge exactly, the
/// sections are still interpolated exactly (the difference is zero there), and the weights are not changed.
/// The guides' parameter follows their arc length, fitted with cubic pieces between the sections (within 1e-7
/// of the size) through the sections' points exactly.
/// </para>
/// Side faces that come out planar or cylindrical are made planes and cylinders; straight side edges are lines.
/// </summary>
public static class NLoft
{
    /// <summary>A section: closed profile regions in the frame's plane, or a point (the loft's first or last section only).</summary>
    public sealed record Section(Frame Frame, IReadOnlyList<ProfileRegion> Regions, Vec3? Point = null);

    /// <summary>How the loft leaves its first section or arrives at its last.</summary>
    public enum Tangency { None, Normal }

    /// <summary>
    /// End conditions: the loft square to its end sections, the tangent's length a multiple of the loft's length.
    /// Closed: the loft runs on from the last section back to the first (smoothly, no end faces).
    /// </summary>
    public sealed record Options(Tangency Start = Tangency.None, double StartLength = 1, Tangency End = Tangency.None, double EndLength = 1, bool Closed = false)
    {
        /// <summary>
        /// A centerline (a tangent chain of lines and arcs through every section's plane): between the sections the
        /// loft follows it, the sections' shapes carried along it without twisting (and blended from one to the next).
        /// </summary>
        public IReadOnlyList<NSweep.Seg>? Centerline { get; init; }
    }

    // ------------------------------------------------------------------------------------------
    // section geometry

    /// <summary>A line or a circular arc (from A, about C, radius R, turning Angle from X towards Y).</summary>
    sealed class Seg3
    {
        public Vec3 A, B, C, X, Y;
        public double R, Angle;
        public bool IsArc;
        public string Name = "";

        public double Length => IsArc ? R * Angle : Vec3.Distance(A, B);

        public Vec3 At(double t)
        {
            if (t == 0) return A;
            if (t == 1) return B;
            return IsArc ? C + (X * Math.Cos(Angle * t) + Y * Math.Sin(Angle * t)) * R : Vec3.Lerp(A, B, t);
        }

        public Vec3 Tangent(double t) => IsArc ? X * -Math.Sin(Angle * t) + Y * Math.Cos(Angle * t) : (B - A).Normalized;

        public Seg3 Reversed() => IsArc
            ? new Seg3 { IsArc = true, A = B, B = A, C = C, R = R, Angle = Angle, Name = Name, X = X * Math.Cos(Angle) + Y * Math.Sin(Angle), Y = X * Math.Sin(Angle) - Y * Math.Cos(Angle) }
            : new Seg3 { A = B, B = A, Name = Name };

        /// <summary>The parameter (0..1) of the point nearest p.</summary>
        public double Closest(Vec3 p)
        {
            if (!IsArc)
            {
                var d = B - A; var l2 = d.LengthSq;
                return l2 > 0 ? Math.Clamp(Vec3.Dot(p - A, d) / l2, 0, 1) : 0;
            }
            var q = p - C;
            var phi = Math.Atan2(Vec3.Dot(q, Y), Vec3.Dot(q, X));
            if (phi < 0) phi += 2 * Math.PI;
            if (phi <= Angle) return phi / Angle;
            // past the end: the nearer end
            return phi - Angle < 2 * Math.PI - phi ? 1 : 0;
        }

        /// <summary>The piece from t0 to t1 (t1 may pass 1 on a full circle) as an exact curve from p0 to p1.</summary>
        public HCurve Piece(double t0, double t1, Vec3 p0, Vec3 p1)
        {
            HCurve c;
            if (!IsArc) c = HCurve.Line(p0, p1);
            else
            {
                double a0 = Angle * t0;
                var x = X * Math.Cos(a0) + Y * Math.Sin(a0);
                var y = X * -Math.Sin(a0) + Y * Math.Cos(a0);
                c = HCurve.Arc(C, x, y, R, Angle * (t1 - t0));
            }
            c.Q[0] = H.Of(p0); c.Q[^1] = H.Of(p1);
            return c;
        }

        /// <summary>The piece's model edge from va to vb, and whether it runs that way.</summary>
        public (NEdge e, bool fwd) Edge(double t0, NVertex va, NVertex vb)
        {
            if (!IsArc) return (new NEdge(new NLine(va.P, vb.P - va.P), 0, Vec3.Distance(va.P, vb.P), va, vb), true);
            double a0 = Angle * t0;
            var x = X * Math.Cos(a0) + Y * Math.Sin(a0);
            return NBuild.ArcEdge(new NCircle(C, Vec3.Cross(X, Y), x, R), va, vb, true);
        }
    }

    /// <summary>A closed loop of a section, oriented counter-clockwise about the loft's direction (holes clockwise).</summary>
    sealed class Loop3
    {
        public readonly List<Seg3> Segs;
        public readonly double[] Cum;
        public readonly double L;
        public readonly Vec3 Centroid;
        public readonly double Size;
        /// <summary>One full circle: no vertices, its seam can go anywhere.</summary>
        public bool FreeSeam => Segs.Count == 1;
        public int Vertices => FreeSeam ? 0 : Segs.Count;

        public Loop3(List<Seg3> segs)
        {
            Segs = segs;
            Cum = new double[segs.Count];
            double l = 0;
            for (int i = 0; i < segs.Count; i++) { Cum[i] = l; l += segs[i].Length; }
            L = l;
            const int n = 64;
            var c = Vec3.Zero;
            var pts = new Vec3[n];
            for (int i = 0; i < n; i++) { pts[i] = At((double)i / n); c += pts[i]; }
            Centroid = c / n;
            double s = 0;
            foreach (var p in pts) s += (p - Centroid).LengthSq;
            Size = Math.Max(Math.Sqrt(s / n), 1e-12);
        }

        public Loop3 Reversed() => new(Enumerable.Range(0, Segs.Count).Select(i => Segs[Segs.Count - 1 - i].Reversed()).ToList());

        public double VertexFraction(int i) => Cum[i] / L;

        /// <summary>Whether the loop turns at vertex i (the start of segment i).</summary>
        public bool Sharp(int i) => Vec3.Dot(Segs[(i - 1 + Segs.Count) % Segs.Count].Tangent(1), Segs[i].Tangent(0)) < Math.Cos(1e-3);

        public (int i, double t) Locate(double f)
        {
            var d = Frac(f) * L;
            int i = Segs.Count - 1;
            for (int k = 1; k < Segs.Count; k++) if (d < Cum[k]) { i = k - 1; break; }
            var len = Segs[i].Length;
            return (i, len > 0 ? Math.Clamp((d - Cum[i]) / len, 0, 1) : 0);
        }

        public Vec3 At(double f) { var (i, t) = Locate(f); return Segs[i].At(t); }

        public double FractionOf(int i, double t) => Frac((Cum[i] + t * Segs[i].Length) / L);

        /// <summary>The point of the loop nearest p: its segment and parameter, and the distance.</summary>
        public (int i, double t, double d) Closest(Vec3 p)
        {
            (int, double, double) best = (0, 0, double.MaxValue);
            for (int i = 0; i < Segs.Count; i++)
            {
                var t = Segs[i].Closest(p);
                var d = Vec3.Distance(Segs[i].At(t), p);
                if (d < best.Item3) best = (i, t, d);
            }
            // a point at the end of a segment is the next one's start (a vertex)
            if (best.Item2 == 1 && !FreeSeam) best = ((best.Item1 + 1) % Segs.Count, 0, best.Item3);
            return best;
        }
    }

    static double Frac(double f) { f -= Math.Floor(f); return f >= 1 ? 0 : f; }

    /// <summary>On a closed path (length L), arc lengths taken on round from the first: s0 ≤ s &lt; s0 + L.</summary>
    static double[] Round(double[] s, double L, bool closed)
    {
        if (!closed) return s;
        var r = (double[])s.Clone();
        for (int k = 1; k < r.Length; k++) r[k] = s[0] + (s[k] - s[0] - Math.Floor((s[k] - s[0]) / L) * L);
        return r;
    }

    static bool Increasing(double[] s) { for (int k = 1; k < s.Length; k++) if (!(s[k] > s[k - 1])) return false; return true; }

    /// <summary>Equal end derivatives for a closed run of values at parameters t (the last value the first again): the slopes either side of the seam averaged (Bessel).</summary>
    static H Bessel(IReadOnlyList<H> vals, IReadOnlyList<double> t)
    {
        int n = vals.Count;
        double hp = t[n - 1] - t[n - 2], hn = t[1] - t[0];
        return ((vals[1] - vals[0]) * (hp / hn) + (vals[0] - vals[n - 2]) * (hn / hp)) * (1 / (hp + hn));
    }

    /// <summary>A profile loop (in the frame) as exact 3D lines and arcs; a whole circle as one closed arc.</summary>
    static Loop3 LoopOf(ProfileLoop loop, Frame fr)
    {
        var segs = NBuild.Segments(loop);
        Seg3 Arc(NBuild.Seg s, bool full)
        {
            var c = fr.ToWorld(s.Arc!.Center);
            var a = fr.ToWorld(s.A); var b = fr.ToWorld(s.B);
            var x = (a - c).Normalized;
            var y = s.Ccw ? Vec3.Cross(fr.Normal, x) : Vec3.Cross(x, fr.Normal);
            double ang = 2 * Math.PI;
            if (!full)
            {
                var q = b - c;
                ang = Math.Atan2(Vec3.Dot(q, y), Vec3.Dot(q, x));
                if (ang <= 1e-12) ang += 2 * Math.PI;
            }
            return new Seg3 { IsArc = true, A = a, B = full ? a : b, C = c, X = x, Y = y, R = s.Arc.Radius, Angle = ang, Name = s.Name };
        }
        if (segs.Count == 2 && segs[0].Arc is { } a0 && segs[1].Arc is { } a1 && segs[0].Name == segs[1].Name
            && Vec2.Distance(a0.Center, a1.Center) < 1e-9 && Math.Abs(a0.Radius - a1.Radius) < 1e-9)
            return new Loop3(new List<Seg3> { Arc(segs[0], true) });
        return new Loop3(segs.Select(s => s.Arc == null ? new Seg3 { A = fr.ToWorld(s.A), B = fr.ToWorld(s.B), Name = s.Name } : Arc(s, false)).ToList());
    }

    // ------------------------------------------------------------------------------------------
    // guides

    /// <summary>A guide: a tangent chain of lines and arcs, by arc length.</summary>
    sealed class GuidePath
    {
        public readonly List<NSweep.Seg> Segs;
        readonly double[] _cum;
        public readonly double L;

        public GuidePath(List<NSweep.Seg> segs)
        {
            Segs = segs;
            _cum = new double[segs.Count + 1];
            for (int i = 0; i < segs.Count; i++) _cum[i + 1] = _cum[i] + segs[i].Length;
            L = _cum[^1];
        }

        /// <summary>The chain closes on itself: arc lengths past its end run on round it.</summary>
        public bool Closed => Segs.Count > 1 && Vec3.Distance(Segs[^1].B, Segs[0].A) < 1e-9 * Math.Max(1, L);

        (int i, double t) Locate(double s)
        {
            s = Closed ? s - Math.Floor(s / L) * L : Math.Clamp(s, 0, L);
            int i = Segs.Count - 1;
            for (int k = 1; k < Segs.Count; k++) if (s < _cum[k]) { i = k - 1; break; }
            var len = Segs[i].Length;
            return (i, len > 0 ? (s - _cum[i]) / len : 0);
        }

        static Vec3 SegAt(NSweep.Seg g, double t)
        {
            if (!g.IsArc) return t <= 0 ? g.A : t >= 1 ? g.B : Vec3.Lerp(g.A, g.B, t);
            var r = g.A - g.Center!.Value; var phi = g.Angle * t;
            return g.Center.Value + r * Math.Cos(phi) + Vec3.Cross(g.Axis!.Value, r) * Math.Sin(phi);
        }

        static Vec3 SegTangent(NSweep.Seg g, double t)
        {
            if (!g.IsArc) return (g.B - g.A).Normalized;
            var r = g.A - g.Center!.Value; var phi = g.Angle * t;
            return (r * -Math.Sin(phi) + Vec3.Cross(g.Axis!.Value, r) * Math.Cos(phi)).Normalized;
        }

        public Vec3 At(double s) { var (i, t) = Locate(s); return SegAt(Segs[i], t); }
        public Vec3 Tangent(double s) { var (i, t) = Locate(s); return SegTangent(Segs[i], t); }

        /// <summary>Whether consecutive pieces meet tangentially (and, closed, the last the first).</summary>
        public bool Smooth()
        {
            for (int i = 1; i < Segs.Count; i++)
                if (Vec3.Dot(SegTangent(Segs[i - 1], 1), SegTangent(Segs[i], 0)) < 1 - 1e-6) return false;
            return !Closed || SmoothAtClose();
        }

        public bool SmoothAtClose() => Vec3.Dot(SegTangent(Segs[^1], 1), SegTangent(Segs[0], 0)) >= 1 - 1e-6;

        public GuidePath Reversed()
        {
            var r = new List<NSweep.Seg>();
            for (int i = Segs.Count - 1; i >= 0; i--)
            {
                var s = Segs[i];
                r.Add(s.IsArc ? new NSweep.Seg(s.B, s.A, s.Center, -s.Axis!.Value, s.Angle) : new NSweep.Seg(s.B, s.A));
            }
            return new GuidePath(r);
        }

        /// <summary>Arc lengths where the guide meets the plane (within tol of it at a sample, or crossing it).</summary>
        public List<double> Crossings(Plane pl, double tol)
        {
            var res = new List<double>();
            for (int i = 0; i < Segs.Count; i++)
            {
                var g = Segs[i];
                int n = g.IsArc ? Math.Max(8, (int)Math.Ceiling(g.Angle / (Math.PI / 32))) : 1;
                double F(double t) => pl.Distance(SegAt(g, t));
                double pt = 0, pf = F(0);
                for (int k = 1; k <= n; k++)
                {
                    double t = (double)k / n, f = F(t);
                    if (Math.Abs(pf) <= tol) res.Add(_cum[i] + pt * g.Length);
                    else if (Math.Abs(f) > tol && pf * f < 0) res.Add(_cum[i] + Roots.Bisect(F, pt, t) * g.Length);
                    pt = t; pf = f;
                }
                if (i == Segs.Count - 1 && Math.Abs(pf) <= tol) res.Add(L);
            }
            res.Sort();
            for (int k = res.Count - 1; k > 0; k--) if (res[k] - res[k - 1] < 1e-9 * Math.Max(1, L)) res.RemoveAt(k);
            return res;
        }
    }

    /// <summary>An orthonormal frame at a point of a centerline (E3 along it).</summary>
    readonly record struct SpineFrame(Vec3 O, Vec3 E1, Vec3 E2, Vec3 E3)
    {
        /// <summary>A homogeneous point in the frame's coordinates (and back).</summary>
        public H ToLocal(H h)
        {
            var d = h.V - O * h.W;
            return new H(new Vec3(Vec3.Dot(d, E1), Vec3.Dot(d, E2), Vec3.Dot(d, E3)), h.W);
        }
        public H ToWorld(H h) => new(O * h.W + E1 * h.V.X + E2 * h.V.Y + E3 * h.V.Z, h.W);
    }

    /// <summary>A centerline with rotation-minimizing frames: constant along a line, turned with an arc about its axis.</summary>
    sealed class Spine
    {
        readonly GuidePath _path;
        readonly SpineFrame[] _start; // the frame at each piece's start
        public List<NSweep.Seg> Segs => _path.Segs;
        public double L => _path.L;

        public Spine(List<NSweep.Seg> segs)
        {
            _path = new GuidePath(segs);
            _start = new SpineFrame[segs.Count];
            if (segs.Count == 0) return;
            var t = _path.Tangent(0);
            var e1 = t.AnyPerpendicular().Normalized;
            var f = new SpineFrame(segs[0].A, e1, Vec3.Cross(t, e1), t);
            for (int i = 0; i < segs.Count; i++)
            {
                _start[i] = f with { O = segs[i].A };
                f = Turned(segs[i], _start[i], 1);
            }
        }

        static Vec3 Rot(Vec3 v, Vec3 a, double phi) => v * Math.Cos(phi) + Vec3.Cross(a, v) * Math.Sin(phi) + a * (Vec3.Dot(a, v) * (1 - Math.Cos(phi)));

        /// <summary>The frame a fraction t along piece g from its start frame.</summary>
        static SpineFrame Turned(NSweep.Seg g, SpineFrame f, double t)
        {
            if (!g.IsArc) return f with { O = Vec3.Lerp(g.A, g.B, t) };
            var a = g.Axis!.Value; var phi = g.Angle * t;
            var r = g.A - g.Center!.Value;
            return new SpineFrame(g.Center.Value + Rot(r, a, phi), Rot(f.E1, a, phi), Rot(f.E2, a, phi), Rot(f.E3, a, phi));
        }

        public bool Closed => _path.Closed;

        public SpineFrame FrameAt(double s)
        {
            s = Closed ? s - Math.Floor(s / L) * L : Math.Clamp(s, 0, L);
            double c = 0;
            for (int i = 0; i < Segs.Count; i++)
            {
                var len = Segs[i].Length;
                if (s <= c + len || i == Segs.Count - 1) return Turned(Segs[i], _start[i], len > 0 ? Math.Clamp((s - c) / len, 0, 1) : 0);
                c += len;
            }
            return _start[0];
        }

        /// <summary>The frames' turning rate at s (per unit length): the arc's axis over its radius, nothing on a line.</summary>
        public Vec3 Omega(double s)
        {
            s = Closed ? s - Math.Floor(s / L) * L : Math.Clamp(s, 0, L);
            double c = 0;
            for (int i = 0; i < Segs.Count; i++)
            {
                var g = Segs[i];
                if (s <= c + g.Length || i == Segs.Count - 1)
                    return g.IsArc ? g.Axis!.Value * (1 / (g.A - g.Center!.Value).Length) : Vec3.Zero;
                c += g.Length;
            }
            return Vec3.Zero;
        }

        public Vec3 At(double s) => _path.At(s);
        public bool Smooth() => _path.Smooth() && (!Closed || _path.SmoothAtClose());
        public List<double> Crossings(Plane pl, double tol) => _path.Crossings(pl, tol);
        public Spine Reversed() => new(_path.Reversed().Segs);
    }

    /// <summary>A guide through the sections: its arc lengths and (snapped) points at each, its loop group and locations.</summary>
    sealed class GuideInfo
    {
        public GuidePath Path = null!;
        public double[] S = null!;
        public Vec3[] Pts = null!;
        public int Group = -1;
        public (int i, double t)[] Loc = null!;
        public HCurve? Fit;
    }

    /// <summary>
    /// The guide as a cubic B-spline in the loft's parameter v: arc length s(v) a monotone cubic Hermite through
    /// (v_k, s_k), each piece between sections fitted through the section points with the end derivatives of the
    /// guide, refined until within tol.
    /// </summary>
    static HCurve FitGuide(GuideInfo g, double[] v, double tol, bool closed)
    {
        int n = v.Length;
        var s = g.S;
        // Fritsch-Carlson slopes
        var delta = new double[n - 1];
        for (int k = 0; k < n - 1; k++) delta[k] = (s[k + 1] - s[k]) / (v[k + 1] - v[k]);
        var m = new double[n];
        m[0] = delta[0]; m[n - 1] = delta[n - 2];
        for (int k = 1; k < n - 1; k++) m[k] = 0.5 * (delta[k - 1] + delta[k]);
        // closed: one slope at the seam, so the guide runs on smoothly
        if (closed) m[0] = m[n - 1] = 0.5 * (delta[0] + delta[n - 2]);
        for (int k = 0; k < n - 1; k++)
        {
            double a = m[k] / delta[k], b = m[k + 1] / delta[k], r = a * a + b * b;
            if (r > 9) { var tau = 3 / Math.Sqrt(r); m[k] = tau * a * delta[k]; m[k + 1] = tau * b * delta[k]; }
        }
        (double s, double ds) S(int k, double lam)
        {
            double h = v[k + 1] - v[k], t = lam, t2 = t * t, t3 = t2 * t;
            double h00 = 2 * t3 - 3 * t2 + 1, h10 = t3 - 2 * t2 + t, h01 = -2 * t3 + 3 * t2, h11 = t3 - t2;
            double d00 = 6 * t2 - 6 * t, d10 = 3 * t2 - 4 * t + 1, d01 = -6 * t2 + 6 * t, d11 = 3 * t2 - 2 * t;
            return (h00 * s[k] + h10 * h * m[k] + h01 * s[k + 1] + h11 * h * m[k + 1],
                (d00 * s[k] + d10 * h * m[k] + d01 * s[k + 1] + d11 * h * m[k + 1]) / h);
        }
        var parts = new List<HCurve>();
        for (int k = 0; k < n - 1; k++)
        {
            double h = v[k + 1] - v[k];
            Vec3 F(double lam) => lam <= 0 ? g.Pts[k] : lam >= 1 ? g.Pts[k + 1] : g.Path.At(S(k, lam).s);
            var (_, ds0) = S(k, 0); var (_, ds1) = S(k, 1);
            var d0 = new H(g.Path.Tangent(s[k]) * (ds0 * h), 0);
            var d1 = new H(g.Path.Tangent(s[k + 1]) * (ds1 * h), 0);
            HCurve? fit = null;
            for (int cnt = 4; cnt <= 512; cnt *= 2)
            {
                var lam = Enumerable.Range(0, cnt + 1).Select(i => (double)i / cnt).ToArray();
                var it = new Interpolation(lam, 3, true, true);
                fit = it.Curve(lam.Select(l => H.Of(F(l))).ToList(), d0, d1);
                double err = 0;
                for (int i = 0; i < cnt; i++)
                {
                    var l = (i + 0.5) / cnt;
                    err = Math.Max(err, Vec3.Distance(fit.Eval(l), F(l)));
                }
                if (err < tol) break;
            }
            parts.Add(fit!.Reparam(v[k], v[k + 1]));
        }
        return HCurve.Concat(parts);
    }

    // ------------------------------------------------------------------------------------------
    // matching

    /// <summary>A breakpoint of a loop group: where it lies on each section (relative to the section's start), and how it was found.</summary>
    sealed class Brk
    {
        public double Gamma;
        public double[] Rel = null!;
        public bool[] Member = null!;
        /// <summary>The section's vertex at this breakpoint (-1: none).</summary>
        public int[] Vertex = null!;
        public int Guide = -1;
    }

    /// <summary>The loops of one region's outline or one of its holes, through the sections (null at a point section).</summary>
    sealed class Group
    {
        public Loop3?[] Loops = null!;
        public int Region;
        public bool Hole;
        public double[] Tau = null!;
        public List<Brk> Brks = new();
        public List<GuideInfo> Guides = new();
    }

    /// <summary>Picks each section's start on its loop: the least twisted from the section before (by shape, about the centroids).</summary>
    static void Align(Group g, IReadOnlyList<int> ks)
    {
        const int M = 48;
        Vec3[] Shape(Loop3 l, double tau) => Enumerable.Range(0, M).Select(m => (l.At(tau + (double)m / M) - l.Centroid) / l.Size).ToArray();
        var first = g.Loops[ks[0]]!;
        int sharp0 = Enumerable.Range(0, first.Vertices).FirstOrDefault(i => first.Sharp(i), 0);
        g.Tau[ks[0]] = first.Vertices > 0 ? first.VertexFraction(sharp0) : 0;
        for (int q = 1; q < ks.Count; q++)
        {
            var prev = Shape(g.Loops[ks[q - 1]]!, g.Tau[ks[q - 1]]);
            var l = g.Loops[ks[q]]!;
            var cand = new List<(double tau, double pen)>();
            for (int i = 0; i < l.Vertices; i++) cand.Add((l.VertexFraction(i), 1));
            if (!Enumerable.Range(0, l.Vertices).Any(l.Sharp))
                for (int i = 0; i < 72; i++) cand.Add((i / 72.0, l.Vertices > 0 ? 1.05 : 1));
            double best = double.MaxValue, bt = 0;
            foreach (var (tau, pen) in cand)
            {
                var cur = Shape(l, tau);
                double e = 0;
                for (int m = 0; m < M; m++) e += (cur[m] - prev[m]).LengthSq;
                e *= pen;
                if (e < best - 1e-12) { best = e; bt = tau; }
            }
            g.Tau[ks[q]] = bt;
        }
    }

    /// <summary>The group's breakpoints: the start and the guides (on every section), then the vertices, matched.</summary>
    static void Breakpoints(Group g, IReadOnlyList<int> ks, int N)
    {
        Brk New(double gamma) => new() { Gamma = gamma, Rel = Enumerable.Repeat(double.NaN, N).ToArray(), Member = new bool[N], Vertex = Enumerable.Repeat(-1, N).ToArray() };
        // anchors: the start, the guides
        var anchors = new List<Brk>();
        var start = New(0);
        foreach (var k in ks) { start.Rel[k] = 0; start.Member[k] = true; }
        anchors.Add(start);
        for (int gi = 0; gi < g.Guides.Count; gi++)
        {
            var gd = g.Guides[gi];
            if (gi == 0) { start.Guide = 0; continue; }
            var a = New(0); a.Guide = gi;
            foreach (var k in ks)
            {
                var l = g.Loops[k]!;
                var r = Frac(l.FractionOf(gd.Loc[k].i, gd.Loc[k].t) - g.Tau[k]);
                if (r < 1e-9 || r > 1 - 1e-9) throw new ArgumentException("two guide curves meet a profile at the same point");
                a.Rel[k] = r; a.Member[k] = true;
            }
            anchors.Add(a);
        }
        anchors = anchors.OrderBy(a => a.Rel[ks[0]]).ToList();
        foreach (var k in ks)
            for (int i = 1; i < anchors.Count; i++)
                if (!(anchors[i].Rel[k] > anchors[i - 1].Rel[k] + 1e-9)) throw new ArgumentException("the guide curves must meet every profile in the same order");
        foreach (var a in anchors) a.Gamma = ks.Average(k => a.Rel[k]);

        // vertices: those at an anchor are that anchor's; the others matched within the stretch between two anchors
        var items = new List<(int k, double rel, int vi)>();
        foreach (var k in ks)
        {
            var l = g.Loops[k]!;
            for (int i = 0; i < l.Vertices; i++)
            {
                var r = Frac(l.VertexFraction(i) - g.Tau[k]);
                if (r > 1 - 1e-9) r = 0;
                var at = anchors.FirstOrDefault(a => Math.Abs(a.Rel[k] - r) < 1e-9);
                if (at != null) { at.Vertex[k] = i; continue; }
                items.Add((k, r, i));
            }
        }
        var clusters = new List<Brk>();
        for (int j = 0; j < anchors.Count; j++)
        {
            var a = anchors[j];
            double bGamma = j + 1 < anchors.Count ? anchors[j + 1].Gamma : 1;
            double BRel(int k) => j + 1 < anchors.Count ? anchors[j + 1].Rel[k] : 1;
            var inner = items.Where(x => x.rel > a.Rel[x.k] && x.rel < BRel(x.k))
                .Select(x => (x.k, x.rel, x.vi, gamma: a.Gamma + (x.rel - a.Rel[x.k]) / (BRel(x.k) - a.Rel[x.k]) * (bGamma - a.Gamma)))
                .OrderBy(x => x.gamma).ToList();
            if (inner.Count == 0) continue;
            var counts = ks.Select(k => inner.Count(x => x.k == k)).ToList();
            if (counts.All(c => c == counts[0]))
            {
                // as many on every section: one to one, in order
                for (int q = 0; q < counts[0]; q++)
                {
                    var c = New(0);
                    foreach (var k in ks)
                    {
                        var x = inner.Where(y => y.k == k).ElementAt(q);
                        c.Rel[k] = x.rel; c.Member[k] = true; c.Vertex[k] = x.vi; c.Gamma += x.gamma;
                    }
                    c.Gamma /= ks.Count;
                    clusters.Add(c);
                }
                continue;
            }
            // different counts: vertices close along the loop (and from different sections) are one breakpoint
            const double delta = 0.04;
            Brk? cur = null; double first = 0; int members = 0;
            foreach (var x in inner)
            {
                if (cur == null || x.gamma - first > delta || cur.Member[x.k])
                {
                    if (cur != null) { cur.Gamma /= members; clusters.Add(cur); }
                    cur = New(0); first = x.gamma; members = 0;
                }
                cur.Rel[x.k] = x.rel; cur.Member[x.k] = true; cur.Vertex[x.k] = x.vi; cur.Gamma += x.gamma; members++;
            }
            cur!.Gamma /= members; clusters.Add(cur);
        }
        var all = anchors.Concat(clusters).OrderBy(b => b.Gamma).ToList();
        // a loop needs two pieces at least (a closed edge would be its own seam)
        if (all.Count == 1) all.Add(New(0.5));
        // sections without a vertex at a breakpoint: along the loop in proportion between their neighbours that have one
        foreach (var k in ks)
            for (int b = 0; b < all.Count; b++)
            {
                if (all[b].Member[k]) continue;
                int p = b - 1; while (!all[p].Member[k]) p--;
                int nx = b + 1; while (nx < all.Count && !all[nx].Member[k]) nx++;
                double g1 = nx < all.Count ? all[nx].Gamma : 1, r1 = nx < all.Count ? all[nx].Rel[k] : 1;
                all[b].Rel[k] = all[p].Rel[k] + (all[b].Gamma - all[p].Gamma) / (g1 - all[p].Gamma) * (r1 - all[p].Rel[k]);
            }
        foreach (var k in ks)
            for (int b = 1; b < all.Count; b++)
                if (!(all[b].Rel[k] > all[b - 1].Rel[k])) throw new ArgumentException("the profiles' corners cannot be matched (they run in a different order)");
        g.Brks = all;
    }

    // ------------------------------------------------------------------------------------------
    // the loft

    /// <summary>
    /// Lofts through the sections (in order), following the guides (each a tangent chain of lines and arcs through
    /// every section). Faces are named prefix.S.(the first profile's segment name) and prefix.Start / prefix.End.
    /// Throws an <see cref="ArgumentException"/> saying what is wrong.
    /// </summary>
    public static NBody Loft(IReadOnlyList<Section> sectionList, IReadOnlyList<IReadOnlyList<NSweep.Seg>>? guides, Options? options, string prefix)
    {
        var opt = options ?? new Options();
        guides ??= Array.Empty<IReadOnlyList<NSweep.Seg>>();
        var sections = sectionList.ToList();
        int N = sections.Count;
        bool closed = opt.Closed;
        if (N < 2) throw new ArgumentException("a loft needs two sections at least");
        if (closed)
        {
            if (N < 3) throw new ArgumentException("a closed loft needs three sections at least");
            if (sections.Any(x => x.Point != null)) throw new ArgumentException("a closed loft cannot have a point section");
            if (opt.Start != Tangency.None || opt.End != Tangency.None) throw new ArgumentException("a closed loft has no ends to set a tangency at");
        }
        for (int k = 1; k < N - 1; k++) if (sections[k].Point != null) throw new ArgumentException("only the first and the last section can be a point");
        var ks = Enumerable.Range(0, N).Where(k => sections[k].Point == null).ToList();
        if (ks.Count == 0) throw new ArgumentException("a loft needs a closed profile in one section at least");
        foreach (var k in ks) if (sections[k].Regions.Count == 0) throw new ArgumentException($"section {k + 1} has no closed profile");
        if (sections[0].Point != null && opt.Start != Tangency.None || sections[^1].Point != null && opt.End != Tangency.None)
            throw new ArgumentException("a point section cannot have a tangency");

        // centres and the loft's direction at each section
        var centre = new Vec3[N];
        for (int k = 0; k < N; k++)
        {
            if (sections[k].Point is { } p) { centre[k] = p; continue; }
            var pts = sections[k].Regions.SelectMany(r => r.Outer.Verts).Select(v => sections[k].Frame.ToWorld(v.P)).ToList();
            centre[k] = pts.Aggregate(Vec3.Zero, (a, b) => a + b) / pts.Count;
        }
        var box = Box3.Empty;
        foreach (var k in ks) foreach (var r in sections[k].Regions) foreach (var v in r.Outer.Verts) box.Add(sections[k].Frame.ToWorld(v.P));
        foreach (var c in centre) box.Add(c);
        var scale = Math.Max(box.Diagonal, 1e-6);
        double tolOn = Math.Max(1e-6, 1e-5 * scale), tolFit = Math.Max(1e-9, 1e-7 * scale);
        double loftLength = 0;
        for (int k = 1; k < N; k++) loftLength += Vec3.Distance(centre[k], centre[k - 1]);
        if (closed) loftLength += Vec3.Distance(centre[0], centre[N - 1]);
        if (loftLength < 1e-6 * scale) throw new ArgumentException("the sections lie on top of each other");
        var normal = new Vec3[N];
        var flip = new bool[N];
        for (int k = 0; k < N; k++)
        {
            var d = (closed ? centre[(k + 1) % N] - centre[(k + N - 1) % N] : centre[Math.Min(k + 1, N - 1)] - centre[Math.Max(k - 1, 0)]).Normalized;
            var n = sections[k].Frame.Normal;
            if (sections[k].Point == null && Math.Abs(Vec3.Dot(n, d)) < 0.02)
                throw new ArgumentException($"section {k + 1} lies along the loft (its plane must cross the line between the sections)");
            flip[k] = Vec3.Dot(n, d) < 0;
            normal[k] = flip[k] ? -n : n;
        }

        // the loops, counter-clockwise about the loft's direction (holes clockwise), regions and holes matched in order
        var loops = new List<(Loop3 outer, List<Loop3> holes)>?[N];
        foreach (var k in ks)
        {
            var fr = sections[k].Frame;
            loops[k] = sections[k].Regions.Select(r0 =>
            {
                var r = r0.Normalized();
                Loop3 O(Loop3 l) => flip[k] ? l.Reversed() : l;
                return (O(LoopOf(r.Outer, fr)), r.Holes.Select(h => O(LoopOf(h, fr))).ToList());
            }).ToList();
        }
        int R = loops[ks[0]]!.Count;
        foreach (var k in ks)
            if (loops[k]!.Count != R) throw new ArgumentException($"every section must have the same number of closed profiles (section {ks[0] + 1} has {R}, section {k + 1} has {loops[k]!.Count})");
        if (ks.Count < N && (R > 1 || loops[ks[0]]!.Any(r => r.holes.Count > 0)))
            throw new ArgumentException("a loft to a point needs one closed profile without holes in each section");
        static List<T> MatchTo<T>(List<T> prev, List<T> cur, Func<T, Vec3> at)
        {
            var res = new T[prev.Count];
            var free = new List<T>(cur);
            var pairs = new List<(int i, int j, double d)>();
            for (int i = 0; i < prev.Count; i++) for (int j = 0; j < cur.Count; j++) pairs.Add((i, j, Vec3.Distance(at(prev[i]), at(cur[j]))));
            var usedI = new bool[prev.Count]; var usedJ = new bool[cur.Count];
            foreach (var (i, j, _) in pairs.OrderBy(p => p.d))
            {
                if (usedI[i] || usedJ[j]) continue;
                res[i] = cur[j]; usedI[i] = usedJ[j] = true;
            }
            return res.ToList();
        }
        for (int q = 1; q < ks.Count; q++)
        {
            var prev = loops[ks[q - 1]]!;
            var cur = MatchTo(prev, loops[ks[q]]!, r => r.outer.Centroid);
            for (int r = 0; r < R; r++)
            {
                if (cur[r].holes.Count != prev[r].holes.Count)
                    throw new ArgumentException($"matching profiles must have the same number of holes (sections {ks[q - 1] + 1} and {ks[q] + 1})");
                cur[r] = (cur[r].outer, MatchTo(prev[r].holes, cur[r].holes, h => h.Centroid));
            }
            loops[ks[q]] = cur;
        }
        var groups = new List<Group>();
        for (int r = 0; r < R; r++)
        {
            int nh = loops[ks[0]]![r].holes.Count;
            for (int h = -1; h < nh; h++)
            {
                var g = new Group { Region = r, Hole = h >= 0, Loops = new Loop3?[N], Tau = new double[N] };
                foreach (var k in ks) g.Loops[k] = h < 0 ? loops[k]![r].outer : loops[k]![r].holes[h];
                groups.Add(g);
            }
        }

        // guides: where they meet each section, on which loop
        var allGuides = new List<GuideInfo>();
        for (int gi = 0; gi < guides.Count; gi++)
        {
            var path = new GuidePath(guides[gi].ToList());
            if (path.Segs.Count == 0 || path.L < 1e-9 * scale) throw new ArgumentException($"guide curve {gi + 1} is empty");
            if (!path.Smooth()) throw new ArgumentException($"guide curve {gi + 1} has a sharp corner (guides must be tangent chains of lines and arcs)");
            if (path.Closed != closed) throw new ArgumentException(closed ? $"guide curve {gi + 1} must be closed, like the loft" : $"guide curve {gi + 1} is closed: close the loft too, or open the guide");
            double DistTo(int k, Vec3 p) => sections[k].Point is { } pt ? Vec3.Distance(p, pt) : groups.Min(g => g.Loops[k]!.Closest(p).d);
            double[] Cross(GuidePath gp)
            {
                var s = new double[N];
                for (int k = 0; k < N; k++)
                {
                    var pl = sections[k].Point is { } pt ? Plane.FromPointNormal(pt, normal[k]) : sections[k].Frame.Plane;
                    var c = gp.Crossings(pl, tolOn);
                    if (sections[k].Point is { } pp)
                    {
                        // a point section: the guide's nearest approach
                        if (c.Count == 0) c.AddRange(Enumerable.Range(0, 257).Select(i => gp.L * i / 256));
                    }
                    if (c.Count == 0) throw new ArgumentException($"guide curve {gi + 1} does not reach section {k + 1}");
                    var best = c.MinBy(x => DistTo(k, gp.At(x)));
                    var d = DistTo(k, gp.At(best));
                    if (d > tolOn) throw new ArgumentException($"guide curve {gi + 1} does not meet the profile of section {k + 1} (it passes {d:0.####} from it)");
                    s[k] = best;
                }
                return s;
            }
            var S = Round(Cross(path), path.L, closed);
            if (!Increasing(S)) { path = path.Reversed(); S = Round(Cross(path), path.L, closed); }
            for (int k = 1; k < N; k++)
                if (!(S[k] > S[k - 1] + 1e-9 * scale)) throw new ArgumentException($"guide curve {gi + 1} must run through the sections in order");
            var info = new GuideInfo { Path = path, S = S, Pts = new Vec3[N], Loc = new (int, double)[N] };
            // its loop: the nearest on the first profile section; the same group on all
            var p0 = path.At(S[ks[0]]);
            info.Group = Enumerable.Range(0, groups.Count).MinBy(i => groups[i].Loops[ks[0]]!.Closest(p0).d);
            var grp = groups[info.Group];
            for (int k = 0; k < N; k++)
            {
                var p = path.At(S[k]);
                if (sections[k].Point is { } pt) { info.Pts[k] = pt; continue; }
                var l = grp.Loops[k]!;
                var (i, t, d) = l.Closest(p);
                if (d > tolOn) throw new ArgumentException($"guide curve {gi + 1} meets section {k + 1} away from the matching profile");
                // at a vertex (within the tolerance): the vertex
                if (!l.FreeSeam)
                {
                    var len = l.Segs[i].Length;
                    if (t * len < tolOn) t = 0;
                    else if ((1 - t) * len < tolOn) { i = (i + 1) % l.Segs.Count; t = 0; }
                }
                info.Loc[k] = (i, t);
                info.Pts[k] = l.Segs[i].At(t);
            }
            grp.Guides.Add(info);
            allGuides.Add(info);
        }

        // start of each loop: the first guide, or the least twisted; then the breakpoints
        foreach (var g in groups)
        {
            if (g.Guides.Count > 0) foreach (var k in ks) g.Tau[k] = g.Loops[k]!.FractionOf(g.Guides[0].Loc[k].i, g.Guides[0].Loc[k].t);
            else Align(g, ks);
            Breakpoints(g, ks, N);
        }
        if (closed)
        {
            // the first section again at the end: the loft is built open from it round to itself
            sections.Add(sections[0]);
            T[] More<T>(T[] a) => a.Append(a[0]).ToArray();
            centre = More(centre); normal = More(normal); flip = More(flip);
            foreach (var gd in allGuides) { gd.S = gd.S.Append(gd.S[0] + gd.Path.L).ToArray(); gd.Pts = More(gd.Pts); gd.Loc = More(gd.Loc); }
            foreach (var g in groups)
            {
                g.Loops = More(g.Loops); g.Tau = More(g.Tau);
                foreach (var b in g.Brks) { b.Rel = More(b.Rel); b.Member = More(b.Member); b.Vertex = More(b.Vertex); }
            }
            ks.Add(N);
            N++;
        }

        // the loft's parameter at each section: the guides' arc length, or the mean distance between matched points
        var vk = new double[N];
        if (allGuides.Count > 0)
            for (int k = 0; k < N; k++) vk[k] = allGuides.Average(g => (g.S[k] - g.S[0]) / (g.S[N - 1] - g.S[0]));
        else
        {
            var dist = new double[N - 1];
            for (int k = 0; k < N - 1; k++)
            {
                double sum = 0; int cnt = 0;
                foreach (var g in groups)
                    for (int m = 0; m < 16; m++)
                    {
                        Vec3 P(int s) => sections[s].Point ?? g.Loops[s]!.At(g.Tau[s] + m / 16.0);
                        sum += Vec3.Distance(P(k), P(k + 1)); cnt++;
                    }
                dist[k] = sum / cnt;
            }
            var tot = dist.Sum();
            for (int k = 1; k < N; k++) vk[k] = vk[k - 1] + dist[k - 1] / tot;
        }
        Spine? spine = null; double[]? sk = null;
        if (opt.Centerline != null)
        {
            spine = new Spine(opt.Centerline.ToList());
            if (spine.Segs.Count == 0 || spine.L < 1e-9 * scale) throw new ArgumentException("the centerline is empty");
            if (!spine.Smooth()) throw new ArgumentException("the centerline has a sharp corner (it must be a tangent chain of lines and arcs)");
            double[] Cross(Spine sp)
            {
                var r = new double[N];
                for (int k = 0; k < N; k++)
                {
                    var pl = sections[k].Point is { } pt ? Plane.FromPointNormal(pt, normal[k]) : sections[k].Frame.Plane;
                    var c = sp.Crossings(pl, tolOn);
                    if (c.Count == 0) throw new ArgumentException($"the centerline does not cross section {k + 1}");
                    r[k] = c.MinBy(x => Vec3.Distance(sp.At(x), centre[k]));
                }
                return r;
            }
            if (spine.Closed != closed) throw new ArgumentException(closed ? "the centerline of a closed loft must be closed" : "the centerline is closed: close the loft too");
            // (a closed loft's last section is its first again: once round)
            double[] Stations(Spine sp)
            {
                if (!closed) return Cross(sp);
                var r = Round(Cross(sp).Take(N - 1).ToArray(), sp.L, true);
                return r.Append(r[0] + sp.L).ToArray();
            }
            sk = Stations(spine);
            if (!Increasing(sk)) { spine = spine.Reversed(); sk = Stations(spine); }
            for (int k = 1; k < N; k++)
                if (!(sk[k] > sk[k - 1] + 1e-9 * scale)) throw new ArgumentException("the centerline must cross the sections in order");
            for (int k = 0; k < N; k++) vk[k] = (sk[k] - sk[0]) / (sk[N - 1] - sk[0]);
            if (!closed) for (int k = 0; k < N; k++) if (sections[k].Point is { } pt && Vec3.Distance(spine.At(sk[k]), pt) > tolOn)
                throw new ArgumentException($"the centerline must pass through the point of section {k + 1}");
        }
        vk[0] = 0; vk[N - 1] = 1;
        for (int k = 1; k < N; k++)
            if (!(vk[k] > vk[k - 1] + 1e-9)) throw new ArgumentException($"sections {k} and {k + 1} are at the same place");
        foreach (var g in allGuides) g.Fit = FitGuide(g, vk, tolFit, closed);

        var interp = new Interpolation(vk, 3, closed || opt.Start != Tangency.None, closed || opt.End != Tangency.None);
        var D0 = normal[0] * (opt.StartLength * loftLength);
        var D1 = normal[N - 1] * (opt.EndLength * loftLength);
        // the end derivatives of a column of values across the sections: along the end sections' normals (scaled by the
        // poles' weights, so the surface's own derivative is D0 / D1 all along the end); on a closed loft the same at
        // both ends, the slopes either side of the seam averaged (Bessel), so the seam is smooth
        (H d0, H d1) Ends(IReadOnlyList<H> vals)
        {
            if (!closed) return (new H(D0 * vals[0].W, 0), new H(D1 * vals[^1].W, 0));
            var d = Bessel(vals, vk);
            return (d, d);
        }
        // a column of values (one per section) as a curve across the sections: interpolated, or along a centerline
        // carried in its frames (interpolated in them) and interpolated again through stations close enough to follow it
        Func<IReadOnlyList<H>, HCurve> column = vals => { var (e0, e1) = Ends(vals); return interp.Curve(vals, e0, e1); };
        if (spine != null)
        {
            var frames = Enumerable.Range(0, N).Select(k => spine.FrameAt(sk![k])).ToArray();
            double S(double v) => sk![0] + v * (sk[N - 1] - sk[0]);
            double Lc = sk![N - 1] - sk[0];
            // a world derivative D at section k, for a value h there, in the section's frame: what the value's own
            // motion must add to the frame's (dS/dv = (C' + ω × (p - C)) Lc + F q')
            H LocalD(int k, H h, Vec3 D)
            {
                var f = frames[k];
                var w = D - (f.E3 + Vec3.Cross(spine.Omega(sk[k]), h.P - f.O)) * Lc;
                return new H(new Vec3(Vec3.Dot(w, f.E1), Vec3.Dot(w, f.E2), Vec3.Dot(w, f.E3)) * h.W, 0);
            }
            // the exact blend at v of a column: its values in their sections' frames, interpolated, carried to v's frame
            Func<double, H> Exact(IReadOnlyList<H> vals)
            {
                var lv = vals.Select((h, k) => frames[k].ToLocal(h)).ToList();
                H l0 = default, l1 = default;
                if (closed) l0 = l1 = Bessel(lv, vk);
                else
                {
                    if (opt.Start != Tangency.None) l0 = LocalD(0, vals[0], D0);
                    if (opt.End != Tangency.None) l1 = LocalD(N - 1, vals[^1], D1);
                }
                var local = interp.Curve(lv, l0, l1);
                return v => spine.FrameAt(S(v)).ToWorld(local.EvalH(v));
            }
            // pieces between breaks: the sections and the centerline's joints (where its curvature jumps, so the blend
            // is only once differentiable there); each piece interpolated through stations with the blend's slopes at
            // its ends (taken inside it), its stations doubled until it follows the blend within the tolerance
            // (judged on the loops' points); the pieces joined at the breaks
            var breaks = new SortedSet<double>(vk);
            double cum = 0;
            for (int i = 0; i < spine.Segs.Count; i++)
            {
                cum += spine.Segs[i].Length;
                var sj = closed ? sk[0] + (cum - sk[0] - Math.Floor((cum - sk[0]) / spine.L) * spine.L) : cum;
                var vj = (sj - sk[0]) / Lc;
                if (vj > 1e-9 && vj < 1 - 1e-9) breaks.Add(vj);
            }
            var bv = breaks.ToArray();
            var probes = new List<IReadOnlyList<H>>();
            foreach (var g in groups)
                for (int q = 0; q < 16; q++)
                    probes.Add(Enumerable.Range(0, N).Select(k => H.Of(sections[k].Point ?? g.Loops[k]!.At(g.Tau[k] + q / 16.0))).ToList());
            var exacts = probes.Select(Exact).ToList();
            // the blend's derivative in v at a, taken on the side towards b
            static H Slope(Func<double, H> f, double a, double b)
            {
                var e = 1e-4 * (b - a);
                return (f(a) * -3 + f(a + e) * 4 - f(a + 2 * e)) * (1 / (2 * e));
            }
            // (at a tangent end the derivative set: exact)
            HCurve Piece(Func<double, H> f, double a, double b, Interpolation it, double[] st)
                => it.Curve(st.Select(f).ToList(),
                    a == 0 && !closed && opt.Start != Tangency.None ? new H(D0 * f(0).W, 0) : Slope(f, a, b),
                    b == 1 && !closed && opt.End != Tangency.None ? new H(D1 * f(1).W, 0) : Slope(f, b, a));
            var pieces = new List<(double a, double b, double[] st, Interpolation it)>();
            for (int j = 0; j + 1 < bv.Length; j++)
            {
                double a = bv[j], b = bv[j + 1];
                for (int cuts = 1; ; cuts *= 2)
                {
                    var st = Enumerable.Range(0, cuts + 1).Select(i => i == cuts ? b : a + (b - a) * i / cuts).ToArray();
                    var it = new Interpolation(st, 3, true, true);
                    double err = 0;
                    foreach (var ex in exacts)
                    {
                        var c = Piece(ex, a, b, it, st);
                        for (int i = 0; i < cuts; i++)
                        {
                            var v = a + (b - a) * (i + 0.5) / cuts;
                            err = Math.Max(err, Vec3.Distance(c.Eval(v), ex(v).P));
                        }
                    }
                    if (err < tolFit || cuts >= 64) { pieces.Add((a, b, st, it)); break; }
                }
            }
            column = vals => { var ex = Exact(vals); return HCurve.Concat(pieces.Select(pc => Piece(ex, pc.a, pc.b, pc.it, pc.st)).ToList()); };
        }

        // ---- topology
        var body = new NBody();
        var tol = 1e-9 * scale;
        NVertex? apex0 = sections[0].Point is { } q0 ? new NVertex(q0) : null;
        NVertex? apex1 = sections[^1].Point is { } q1 ? new NVertex(q1) : null;
        var names = new Dictionary<string, int>();
        var rings = new List<List<NurbsSurface>>();
        var startLoops = new List<(int region, bool hole, List<(NEdge e, bool fwd)> loop)>();
        var endLoops = new List<(int region, bool hole, List<(NEdge e, bool fwd)> loop)>();
        foreach (var g in groups)
        {
            int m = g.Brks.Count;
            // where each breakpoint lies on each section
            var loc = new (int i, double t)[N, m];
            var pt = new Vec3[N, m];
            foreach (var k in ks)
            {
                var l = g.Loops[k]!;
                for (int b = 0; b < m; b++)
                {
                    var br = g.Brks[b];
                    if (br.Vertex[k] >= 0) loc[k, b] = (br.Vertex[k], 0);
                    else if (br.Guide >= 0) loc[k, b] = g.Guides[br.Guide].Loc[k];
                    else loc[k, b] = l.Locate(g.Tau[k] + br.Rel[k]);
                    pt[k, b] = l.Segs[loc[k, b].i].At(loc[k, b].t);
                }
            }
            foreach (var k in Enumerable.Range(0, N).Except(ks)) for (int b = 0; b < m; b++) pt[k, b] = sections[k].Point!.Value;
            // the piece of each section's loop between breakpoints b and b + 1: (segment, t0, t1)
            (Seg3 s, double t0, double t1) Piece(int k, int b)
            {
                var l = g.Loops[k]!;
                var (i, t0) = loc[k, b];
                var (j, t1) = loc[k, (b + 1) % m];
                if (j == i && t1 > t0) return (l.Segs[i], t0, t1);
                if (t1 == 0 && j == (i + 1) % l.Segs.Count && !l.FreeSeam) return (l.Segs[i], t0, 1);
                if (l.FreeSeam) return (l.Segs[0], t0, t1 + (t1 <= t0 ? 1 : 0));
                throw new InvalidOperationException("loft: a piece spans a vertex");
            }
            static (NEdge e, bool fwd) Edge((Seg3 s, double t0, double t1) p, NVertex a, NVertex b) => p.s.Edge(p.t0, a, b);
            // vertices and the edges along the sections at the ends
            int kf = ks[0] == 0 ? 0 : -1, kl = ks[^1] == N - 1 ? N - 1 : -1;
            var vf = new NVertex[m]; var vl = new NVertex[m];
            for (int b = 0; b < m; b++)
            {
                vf[b] = apex0 ?? new NVertex(pt[0, b]);
                vl[b] = closed ? vf[b] : apex1 ?? new NVertex(pt[N - 1, b]);
            }
            // a closed loft's faces are cut at a middle section (no face runs all the way round)
            int km = closed ? N / 2 : 0;
            var vm = new NVertex[m];
            var mid = new (NEdge e, bool fwd)[m];
            if (km > 0)
            {
                for (int b = 0; b < m; b++) vm[b] = new NVertex(pt[km, b]);
                for (int b = 0; b < m; b++) mid[b] = Edge(Piece(km, b), vm[b], vm[(b + 1) % m]);
            }
            var bottom = new (NEdge e, bool fwd)[m]; var top = new (NEdge e, bool fwd)[m];
            for (int b = 0; b < m; b++)
            {
                if (kf >= 0) { var (s, t0, _) = Piece(0, b); bottom[b] = s.Edge(t0, vf[b], vf[(b + 1) % m]); }
                if (kl >= 0) top[b] = closed ? bottom[b] : Edge(Piece(N - 1, b), vl[b], vl[(b + 1) % m]);
            }
            // the side edges: the guide, or the interpolation of the breakpoint's points
            var guideAt = new GuideInfo?[m];
            for (int b = 0; b < m; b++) if (g.Brks[b].Guide >= 0) guideAt[b] = g.Guides[g.Brks[b].Guide];
            var side = new NEdge[m]; var side2 = new NEdge[m];
            for (int b = 0; b < m; b++)
            {
                HCurve curve;
                if (guideAt[b]?.Fit is { } fit) curve = fit;
                else
                {
                    curve = column(Enumerable.Range(0, N).Select(k => H.Of(pt[k, b])).ToList());
                }
                if (km > 0)
                {
                    var (ca, cb) = curve.Split(vk[km]);
                    side[b] = SideEdge(ca.ToBSpline(), vf[b], vm[b], tol);
                    side2[b] = SideEdge(cb.ToBSpline(), vm[b], vl[b], tol);
                }
                else side[b] = SideEdge(curve.ToBSpline(), vf[b], vl[b], tol);
            }
            // the faces
            var surfs = new List<NurbsSurface>();
            rings.Add(surfs);
            for (int b = 0; b < m; b++)
            {
                var pieces = new HCurve?[N];
                string? name = null;
                foreach (var k in ks)
                {
                    var (s, t0, t1) = Piece(k, b);
                    pieces[k] = s.Piece(t0, t1, pt[k, b], pt[k, (b + 1) % m]);
                    name ??= s.Name;
                }
                var g0 = guideAt[b]; var g1 = guideAt[(b + 1) % m];
                var (cols, U, pu) = Strip(pieces, sections, column, g0?.Fit, g1?.Fit);
                surfs.Add(SurfaceOf(cols, U, pu));
                void Face(NurbsSurface surf, (NEdge e, bool fwd)? lo, NEdge right, (NEdge e, bool fwd)? hi, NEdge left)
                {
                    var nm = $"{prefix}.S.{name}";
                    names[nm] = names.GetValueOrDefault(nm) + 1;
                    if (names[nm] > 1) nm += "." + names[nm];
                    var inside = surf.Eval(0.5 * (surf.U0 + surf.U1), 0.5 * (surf.V0 + surf.V1));
                    var rec = NRecognize.Surface(surf, 1e-7 * Math.Max(1, surf.Box.Diagonal), out var flipped);
                    var ns = rec ?? NSurface.BSpline(surf);
                    var face = new NFace(ns, !flipped, new FaceTag(nm, ns.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : ns.Def), inside);
                    var lp = new NLoop();
                    if (lo is { } l0) lp.Coedges.Add(new NCoedge(l0.e, l0.fwd));
                    lp.Coedges.Add(new NCoedge(right, true));
                    if (hi is { } h0) lp.Coedges.Add(new NCoedge(h0.e, !h0.fwd));
                    lp.Coedges.Add(new NCoedge(left, false));
                    face.Loops.Add(lp);
                    body.Faces.Add(face);
                }
                if (km > 0)
                {
                    var split = cols.Select(c => c.Split(vk[km])).ToArray();
                    Face(SurfaceOf(split.Select(x => x.before).ToArray(), U, pu), bottom[b], side[(b + 1) % m], mid[b], side[b]);
                    Face(SurfaceOf(split.Select(x => x.after).ToArray(), U, pu), mid[b], side2[(b + 1) % m], top[b], side2[b]);
                }
                else Face(SurfaceOf(cols, U, pu), kf >= 0 ? bottom[b] : null, side[(b + 1) % m], kl >= 0 ? top[b] : null, side[b]);
            }
            if (closed) continue;
            if (kf >= 0) startLoops.Add((g.Region, g.Hole, Enumerable.Range(0, m).Reverse().Select(b => (bottom[b].e, !bottom[b].fwd)).ToList()));
            if (kl >= 0) endLoops.Add((g.Region, g.Hole, Enumerable.Range(0, m).Select(b => top[b]).ToList()));
        }
        CheckCrossing(rings, vk);
        // the end caps: one plane face per region (its outline and its holes)
        void Caps(List<(int region, bool hole, List<(NEdge e, bool fwd)> loop)> ls, int k, string name)
        {
            foreach (var reg in ls.GroupBy(x => x.region))
            {
                var face = new NFace(NSurface.FromPlane(Plane.FromPointNormal(sections[k].Frame.Origin, k == 0 ? -normal[k] : normal[k])), true, new FaceTag(name, SurfaceDef.PlaneDef));
                foreach (var (_, _, loop) in reg.OrderBy(x => x.hole))
                {
                    var l = new NLoop();
                    foreach (var (e, fwd) in loop) l.Coedges.Add(new NCoedge(e, fwd));
                    face.Loops.Add(l);
                }
                body.Faces.Add(face);
            }
        }
        Caps(startLoops, 0, prefix + ".Start");
        Caps(endLoops, N - 1, prefix + ".End");
        body.Link();
        // inside out (the sections' loops turned the other way): turn it round
        if (body.Validate() == null && NMass.Of(body) is { } mp && mp.Volume < 0) body = body.Flipped();
        return body;
    }

    /// <summary>
    /// Throws if the loft passes through itself: between the sections, the rings across it (every loop at one v,
    /// seen along the loft) must neither cross themselves nor each other. A loft twisted past its profiles' corners
    /// or pulled across by a guide folds over like this.
    /// </summary>
    static void CheckCrossing(List<List<NurbsSurface>> groups, double[] vk)
    {
        const int perSpan = 8, perStrip = 12;
        var prevN = Vec3.Zero;
        List<Vec3[]> Rings(double v) => groups.Select(strips => strips.SelectMany(sf => Enumerable.Range(0, perStrip)
            .Select(i => sf.Eval(sf.U0 + (sf.U1 - sf.U0) * i / perStrip, v))).ToArray()).ToList();
        // twice the area of the first (outer) ring, along its normal (Newell)
        static Vec3 Area(Vec3[] o)
        {
            var n = Vec3.Zero;
            for (int i = 0; i < o.Length; i++) n += Vec3.Cross(o[i] - o[0], o[(i + 1) % o.Length] - o[0]);
            return n;
        }
        for (int k = 0; k + 1 < vk.Length; k++)
        {
            double area0 = Area(Rings(vk[k])[0]).Length, area1 = Area(Rings(vk[k + 1])[0]).Length;
            for (int q = 1; q < perSpan; q++)
            {
                var v = vk[k] + (vk[k + 1] - vk[k]) * q / perSpan;
                var loops = Rings(v);
                var o = loops[0];
                var nrm = Area(o);
                // much smaller than the sections either side: the loft pinches through itself (a half turn)
                if (nrm.Length < 0.02 * (area0 + (area1 - area0) * q / perSpan))
                    throw new ArgumentException($"the loft passes through itself between sections {k + 1} and {k + 2} (it twists too far: match the profiles' corners or add a section)");
                // the ring turned inside out (or shrunk to nothing) since the last: the loft folds back through itself
                if (!(Vec3.Dot(nrm, prevN) > 0) && prevN != Vec3.Zero)
                    throw new ArgumentException($"the loft passes through itself between sections {k + 1} and {k + 2} (it twists too far: match the profiles' corners or add a section)");
                if (nrm.Length < 1e-300) continue;
                nrm = nrm.Normalized;
                prevN = nrm;
                var x = nrm.AnyPerpendicular(); var y = Vec3.Cross(nrm, x);
                var p2 = loops.Select(l => l.Select(p => new Vec2(Vec3.Dot(p, x), Vec3.Dot(p, y))).ToArray()).ToList();
                double size = 0;
                foreach (var l in p2) foreach (var p in l) size = Math.Max(size, Math.Max(Math.Abs(p.X), Math.Abs(p.Y)));
                var eps = 1e-9 * Math.Max(size, 1);
                static double Orient(Vec2 a, Vec2 b, Vec2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                for (int a = 0; a < p2.Count; a++)
                    for (int b = a; b < p2.Count; b++)
                    {
                        var A = p2[a]; var B = p2[b];
                        for (int i = 0; i < A.Length; i++)
                        {
                            Vec2 a0 = A[i], a1 = A[(i + 1) % A.Length];
                            for (int j = a == b ? i + 2 : 0; j < B.Length; j++)
                            {
                                if (a == b && (j + 1) % B.Length == i) continue; // neighbours share a point
                                Vec2 b0 = B[j], b1 = B[(j + 1) % B.Length];
                                double d1 = Orient(b0, b1, a0), d2 = Orient(b0, b1, a1), d3 = Orient(a0, a1, b0), d4 = Orient(a0, a1, b1);
                                double la = (a1 - a0).Length, lb = (b1 - b0).Length;
                                if ((d1 > eps * lb && d2 < -eps * lb || d1 < -eps * lb && d2 > eps * lb) && (d3 > eps * la && d4 < -eps * la || d3 < -eps * la && d4 > eps * la))
                                    throw new ArgumentException($"the loft passes through itself between sections {k + 1} and {k + 2} ("
                                        + (a == b ? "it twists too far: match the profiles' corners or add a section" : "two of its loops run into each other") + ")");
                            }
                        }
                    }
            }
        }
    }

    /// <summary>The surface of pole columns (each a curve across the sections, one knot vector) over the u knots.</summary>
    static NurbsSurface SurfaceOf(HCurve[] cols, double[] U, int pu)
    {
        int nu = cols.Length, nv = cols[0].Q.Length;
        var poles = new Vec3[nu, nv];
        var w = new double[nu, nv];
        for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++) { poles[i, j] = cols[i].Q[j].P; w[i, j] = cols[i].Q[j].W; }
        return new NurbsSurface(pu, cols[0].P, poles, w, U, cols[0].U);
    }

    /// <summary>A side edge from va to vb on the (non-rational) curve: a line when it is straight.</summary>
    static NEdge SideEdge(BSplineCurve c, NVertex va, NVertex vb, double tol)
    {
        var d = vb.P - va.P; var len = d.Length;
        if (len > tol)
        {
            var u = d / len;
            bool straight = true;
            foreach (var p in c.Poles)
            {
                var w = p - va.P; var along = Vec3.Dot(w, u);
                if ((w - u * along).Length > tol || along < -tol || along > len + tol) { straight = false; break; }
            }
            if (straight) return new NEdge(new NLine(va.P, u), 0, len, va, vb);
        }
        return new NEdge(new NSpline(c), c.Knots[c.Degree], c.Knots[^(c.Degree + 1)], va, vb);
    }

    /// <summary>
    /// One side face's surface: the pieces (null at a point section) made compatible and their poles interpolated
    /// across the sections; corrected to the guides at its edges (u = 0 and u = 1) when given.
    /// </summary>
    static (HCurve[] cols, double[] U, int pu) Strip(HCurve?[] pieces, IReadOnlyList<Section> sections, Func<IReadOnlyList<H>, HCurve> column, HCurve? g0, HCurve? g1)
    {
        int N = pieces.Length;
        bool guided = g0 != null || g1 != null;
        var ks = Enumerable.Range(0, N).Where(k => pieces[k] != null).ToList();
        var comp = HCurve.Compatible(ks.Select(k => pieces[k]!).ToList(), guided ? 3 : 1);
        var cur = new HCurve[N];
        for (int q = 0; q < ks.Count; q++) cur[ks[q]] = comp[q];
        // a point section: the neighbouring section's curve collapsed to the point (its weights kept)
        for (int k = 0; k < N; k++)
            if (cur[k] == null)
            {
                var pt = sections[k].Point!.Value;
                cur[k] = cur[k == 0 ? ks[0] : ks[^1]].WithPoles((_, q) => H.Of(pt, q.W));
            }
        int nu = cur[0].Q.Length;
        var U = cur[0].U; int pu = cur[0].P;
        // every pole column across the sections
        var cols = new HCurve[nu];
        for (int i = 0; i < nu; i++)
        {
            var vals = Enumerable.Range(0, N).Select(k => cur[k].Q[i]).ToList();
            cols[i] = column(vals);
        }
        if (guided)
        {
            var list = cols.Select(c => c.Elevate(3)).ToList();
            if (g0 != null) list.Add(g0);
            if (g1 != null) list.Add(g1);
            var all = HCurve.Compatible(list, 3);
            for (int i = 0; i < nu; i++) cols[i] = all[i];
            HCurve? G0 = g0 != null ? all[nu] : null, G1 = g1 != null ? all[^1] : null;
            var K0 = cols[0].Q.ToArray(); var K1 = cols[nu - 1].Q.ToArray();
            for (int i = 0; i < nu; i++)
            {
                // the smoothstep 3u² - 2u³'s B-spline coefficient (its blossom at the pole's knots)
                double a = U[i + 1], b = U[i + 2], c = U[i + 3], h = a * b + a * c + b * c - 2 * a * b * c;
                var q = cols[i].Q;
                for (int j = 0; j < q.Length; j++)
                {
                    var dv = Vec3.Zero;
                    if (G0 != null) dv += (G0.Q[j].V - K0[j].V) * (1 - h);
                    if (G1 != null) dv += (G1.Q[j].V - K1[j].V) * h;
                    // times the pole's weight: the blend is then Σ N w h / Σ N w, flat at both edges whatever the weights
                    q[j] = new H(q[j].V + dv * q[j].W, q[j].W);
                }
            }
        }
        return (cols, U, pu);
    }
}
