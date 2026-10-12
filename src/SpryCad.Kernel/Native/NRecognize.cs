namespace SpryCad.Kernel.Native;

/// <summary>
/// Recognizes B-splines that are exactly planes, cylinders, lines or circles (many exporters write every surface
/// and curve as a B-spline: flat faces as linear patches, bends as rational quadratics). Analytic geometry is what
/// the rest of the kernel handles best (exact offsets, intersections, sheet metal unfolding). A candidate is accepted
/// only when every sample of the B-spline lies on it within the tolerance.
/// </summary>
public static class NRecognize
{
    const int N = 9; // samples per direction

    /// <summary>The analytic surface the B-spline is, or null. <paramref name="flip"/>: its natural normal is opposite.</summary>
    /// <param name="fitted">
    /// The radius tolerance, relative to the radius, for a cylinder fitted by its writer (a thread's root swept along the
    /// helix: every normal exactly perpendicular to the axis, the radius wobbling a few microns between the knots). Such a
    /// strip and its copy a pitch along cross each other all over within the noise, which no boolean resolves; as the
    /// cylinder it is, they coincide. 0: exact cylinders only.
    /// </param>
    public static NSurface? Surface(NurbsSurface s, double tol, out bool flip, double fitted = 0)
    {
        flip = false;
        double um = 0.5 * (s.U0 + s.U1), vm = 0.5 * (s.V0 + s.V1);
        var nm = s.Normal(um, vm);
        if (!(nm.LengthSq > 0.5)) return null;

        // plane: the surface lies in the hull of its poles
        var pm = s.Eval(um, vm);
        var pl = Plane.FromPointNormal(pm, nm);
        bool planar = true;
        foreach (var p in s.P) if (Math.Abs(pl.Distance(p)) > tol) { planar = false; break; }
        if (planar) return NSurface.FromPlane(pl); // natural normal = the spline's

        // cylinder: every normal is perpendicular to the axis, every point at radius r from it
        var pts = new List<Vec3>(N * N); var nrm = new List<Vec3>(N * N);
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
            {
                double u = s.U0 + (s.U1 - s.U0) * i / (N - 1), v = s.V0 + (s.V1 - s.V0) * j / (N - 1);
                var n = s.Normal(u, v);
                if (!(n.LengthSq > 0.5)) return null; // degenerate (a pole): not a cylinder
                pts.Add(s.Eval(u, v)); nrm.Add(n);
            }
        Vec3 axis = default; double best = 0;
        for (int i = 0; i < nrm.Count; i++)
            for (int j = i + 1; j < nrm.Count; j++)
            {
                var c = Vec3.Cross(nrm[i], nrm[j]);
                if (c.LengthSq > best) { best = c.LengthSq; axis = c; }
            }
        if (best < 1e-6) return null; // normals (nearly) parallel: no axis to find
        axis = axis.Normalized;
        foreach (var n in nrm)
            if (Math.Abs(Vec3.Dot(n, axis)) > 1e-7)
                return Cone(s, pts, nrm, tol, out flip) ?? (fitted > 0 ? Screw(s, tol, fitted, out flip) : null);

        // circle through the points projected along the axis: |p - c|² = r² is linear in (c, r² - |c|²)
        var x = axis.AnyPerpendicular(); var y = Vec3.Cross(axis, x);
        var o = pts[0];
        Vec2 P2(Vec3 p) => new(Vec3.Dot(p - o, x), Vec3.Dot(p - o, y));
        double a11 = 0, a12 = 0, a13 = 0, a22 = 0, a23 = 0, a33 = 0, b1 = 0, b2 = 0, b3 = 0;
        foreach (var p in pts)
        {
            var q = P2(p); var rhs = q.X * q.X + q.Y * q.Y;
            a11 += 4 * q.X * q.X; a12 += 4 * q.X * q.Y; a13 += 2 * q.X; a22 += 4 * q.Y * q.Y; a23 += 2 * q.Y; a33 += 1;
            b1 += 2 * q.X * rhs; b2 += 2 * q.Y * rhs; b3 += rhs;
        }
        if (!Solve3(a11, a12, a13, a22, a23, a33, b1, b2, b3, out var cx, out var cy, out var k)) return null;
        var r2 = k + cx * cx + cy * cy;
        if (!(r2 > 0)) return null;
        var r = Math.Sqrt(r2);
        var center = o + x * cx + y * cy;
        // (fitted: the radius within its share, the normals radial within the matching angle)
        double rtol = Math.Max(tol, fitted * r), ntol = Math.Max(1e-7, fitted * 0.5);
        bool outward = true;
        for (int i = 0; i < pts.Count; i++)
        {
            var d = pts[i] - center; d -= axis * Vec3.Dot(d, axis);
            if (Math.Abs(d.Length - r) > rtol) return null;
            var dot = Vec3.Dot(d.Normalized, nrm[i]);
            if (Math.Abs(Math.Abs(dot) - 1) > ntol) return null;
            if (i == 0) outward = dot > 0;
            else if (dot > 0 != outward) return null;
        }
        // also between the samples: the poles' mid-span points
        for (int i = 0; i + 1 < N; i++)
            for (int j = 0; j + 1 < N; j++)
            {
                var p = s.Eval(s.U0 + (s.U1 - s.U0) * (i + 0.5) / (N - 1), s.V0 + (s.V1 - s.V0) * (j + 0.5) / (N - 1));
                var d = p - center; d -= axis * Vec3.Dot(d, axis);
                if (Math.Abs(d.Length - r) > rtol) return null;
            }
        if (fitted > 0) Round(s, tol, ref axis, ref center, ref r);
        flip = !outward; // a cylinder's natural normal points away from the axis
        var refDir = (pts[0] - center) - axis * Vec3.Dot(pts[0] - center, axis);
        return new NSurface(new SurfaceDef { Kind = SurfaceKind.Cylinder, Origin = center, Axis = axis, RefDir = refDir, Radius = r, Segments = SurfaceDef.SegmentsFor(r) });
    }

    /// <summary>
    /// The screw surface (a line or arc swept along helices: a thread's flank, or its rounded root) the fitted
    /// B-spline is, or null. Every normal of a screw surface is perpendicular to the motion's velocity a×(p−O) + K·a:
    /// with b = K·a + O×a that is a·(p×n) + b·n = 0, linear in (a, b): the axis, centre and lead are the least
    /// eigenvector of the samples' 6×6 normal matrix. Seen in the motion's frame as (r, z − K·θ), every point then lies
    /// on the generator: a line or a circle there, within the fitting noise. Rounded as fitted cylinders are.
    /// </summary>
    static NSurface? Screw(NurbsSurface s, double tol, double fitted, out bool flip)
    {
        flip = false;
        const int M = 32;
        var pts = new List<Vec3>((M + 1) * (M + 1)); var nrm = new List<Vec3>(pts.Capacity);
        for (int i = 0; i <= M; i++)
            for (int j = 0; j <= M; j++)
            {
                double u = s.U0 + (s.U1 - s.U0) * i / M, v = s.V0 + (s.V1 - s.V0) * j / M;
                var n = s.Normal(u, v);
                if (!(n.LengthSq > 0.5)) return null;
                pts.Add(s.Eval(u, v)); nrm.Add(n);
            }
        var c0 = Vec3.Zero; foreach (var p in pts) c0 += p; c0 /= pts.Count;
        var m = new double[6, 6];
        var row = new double[6];
        for (int k = 0; k < pts.Count; k++)
        {
            var w = Vec3.Cross(pts[k] - c0, nrm[k]); var n = nrm[k];
            row[0] = w.X; row[1] = w.Y; row[2] = w.Z; row[3] = n.X; row[4] = n.Y; row[5] = n.Z;
            for (int i = 0; i < 6; i++) for (int j = 0; j < 6; j++) m[i, j] += row[i] * row[j];
        }
        var (vals, vecs) = Eigen6(m);
        // one motion only (a plane, a cylinder or a surface of revolution has more)
        if (!(vals[1] > 1e-8 * vals[5])) return null;
        var a = new Vec3(vecs[0, 0], vecs[1, 0], vecs[2, 0]); var b = new Vec3(vecs[3, 0], vecs[4, 0], vecs[5, 0]);
        var al = a.Length;
        if (al < 1e-9) return null;
        a /= al; b /= al;
        var K = Vec3.Dot(a, b);
        var o = c0 + Vec3.Cross(a, b);
        var size = s.Box.Diagonal;
        if (Math.Abs(K) < 1e-6 * size) return null;
        var rtol = Math.Max(tol, fitted * pts.Max(p => { var d = p - o; return (d - a * Vec3.Dot(d, a)).Length; }));
        var g = Profile(pts, a, o, K, null);
        // (from noisy normals the motion is a little off, the profile with it: rounded, it may come within)
        if (g == null || g.Value.err > 50 * rtol) return null;
        // a surface of revolution (a fillet's, a torus), or one nearly so, fits a screw of next to no lead about as
        // well: a lead within a few times the noise tells nothing (a real thread's is many times it)
        if (Math.Abs(2 * Math.PI * K) < 20 * rtol) return null;
        if (Profile(pts, a, o, 0, null) is { } rev && rev.err <= Math.Max(rtol, 2 * g.Value.err)) return null;
        bool circle = g.Value.circle;
        // the roundest axis, centre and lead keeping the profile within its noise (always: an accurate fit's lead
        // and axis are off in the seventh digit too, and the faces of one thread must share one motion)
        {
            // (up to three times the fit's own error: the least-squares motion from noisy normals is itself off)
            var limit = Math.Max(g.Value.err * 1.05 + tol, Math.Min(rtol, 3 * g.Value.err));
            static Vec3 R(Vec3 v, int d) => new(Math.Round(v.X, d), Math.Round(v.Y, d), Math.Round(v.Z, d));
            for (int d = 0; d <= 9; d++)
            {
                var ax = R(a, d);
                if (!(ax.LengthSq > 0.5)) continue;
                ax = ax.Normalized;
                if (Profile(pts, ax, o, K, circle) is { } e && e.err <= limit) { a = ax; break; }
            }
            for (int d = 0; d <= 9; d++)
            {
                var c = R(o, d);
                if (Profile(pts, a, c, K, circle) is { } e && e.err <= limit) { o = c; break; }
            }
            var lead0 = 2 * Math.PI * K;
            for (int d = 0; d <= 9; d++)
            {
                var l = Math.Round(lead0, d);
                if (l == 0) continue;
                if (Profile(pts, a, o, l / (2 * Math.PI), circle) is { } e && e.err <= limit) { K = l / (2 * Math.PI); break; }
            }
            g = Profile(pts, a, o, K, circle);
        if (g == null || g.Value.err > rtol) return null;
        }
        // (Profile's frame is the motion's: the same axis, centre and perpendicular)
        var motion = ScrewMotion.About(o, a, 2 * Math.PI * K);
        var (_, _, c2, r2, d2) = g.Value;
        var lead = 2 * Math.PI * K;
        // the generator in the frame's x–z half plane: (r, ζ) ↦ O + X·r + A·ζ
        NCurve gen = circle
            ? new NCircle(motion.O + motion.X * c2.X + motion.A * c2.Y, Vec3.Cross(motion.X, motion.A), motion.X, r2)
            : new NLine(motion.O + motion.X * c2.X + motion.A * c2.Y, motion.X * d2.X + motion.A * d2.Y);
        // the ranges: each point's generator parameter, and the turn that carries it there (z = ζ(v) + K·u)
        double v0 = double.MaxValue, v1 = double.MinValue, u0 = double.MaxValue, u1 = double.MinValue;
        foreach (var p in pts)
        {
            var l = motion.Local(p);
            var r = Math.Sqrt(l.X * l.X + l.Y * l.Y);
            var zeta = l.Z - K * Math.Atan2(l.Y, l.X);
            // the turn of ζ nearest the generator at this radius
            var zref = circle || Math.Abs(d2.X) < 1e-9 ? c2.Y : c2.Y + d2.Y * (r - c2.X) / d2.X;
            zeta -= lead * Math.Round((zeta - zref) / lead);
            var q = new Vec2(r, zeta);
            var v = circle ? Math.Atan2(q.Y - c2.Y, q.X - c2.X) : Vec2.Dot(q - c2, d2);
            var u = (l.Z - motion.Local(gen.Eval(v)).Z) / K;
            v0 = Math.Min(v0, v); v1 = Math.Max(v1, v); u0 = Math.Min(u0, u); u1 = Math.Max(u1, u);
        }
        // (an arc's angles: one branch, unless it crosses ±π; a thread of a thousand turns is no thread)
        if (circle && v1 - v0 > Math.PI || u1 - u0 > 2 * Math.PI * 1000) return null;
        var padV = 0.02 * (v1 - v0) + (circle ? 1e-3 : 1e-3 * size); var padU = 0.02 * (u1 - u0) + 0.05;
        var screw = new ScrewSurface(motion, gen, v0 - padV, v1 + padV, u0 - padU, u1 + padU);
        // the surface's points on the screw, and on its side
        int agree = 0;
        for (int k = 0; k < pts.Count; k += 7)
        {
            var (cu, cv, _, dist) = screw.Closest(pts[k]);
            if (dist > rtol * 1.5) return null;
            agree += Vec3.Dot(screw.NormalAt(cu, cv), nrm[k]) > 0 ? 1 : -1;
        }
        flip = agree < 0;
        return NSurface.ScrewOf(screw);
    }

    /// <summary>
    /// The points seen as (r, z − K·θ) in the motion's frame (one turn of ζ: nearest the first), fitted by a line
    /// (principal direction) or a circle: its kind, the largest distance of a point from it, a point of it (the line's
    /// centroid or the circle's centre), the circle's radius and the line's direction. <paramref name="circle"/>:
    /// that kind only.
    /// </summary>
    static (bool circle, double err, Vec2 c, double r, Vec2 d)? Profile(List<Vec3> pts, Vec3 a, Vec3 o, double K, bool? circle)
    {
        var motion = ScrewMotion.About(o, a, 2 * Math.PI * K);
        var lead = 2 * Math.PI * K;
        var q = new Vec2[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            var l = motion.Local(pts[i]);
            var zeta = l.Z - K * Math.Atan2(l.Y, l.X);
            if (i > 0 && lead != 0) zeta -= lead * Math.Round((zeta - q[0].Y) / lead);
            q[i] = new Vec2(Math.Sqrt(l.X * l.X + l.Y * l.Y), zeta);
        }
        Vec2 mean = default; foreach (var p in q) mean += p; mean /= q.Length;
        double sxx = 0, sxy = 0, syy = 0;
        foreach (var p in q) { var e = p - mean; sxx += e.X * e.X; sxy += e.X * e.Y; syy += e.Y * e.Y; }
        var ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        var dir = new Vec2(Math.Cos(ang), Math.Sin(ang));
        double lineErr = 0;
        foreach (var p in q) { var e = p - mean; lineErr = Math.Max(lineErr, Math.Abs(e.X * dir.Y - e.Y * dir.X)); }
        (bool, double, Vec2, double, Vec2) line = (false, lineErr, mean, 0, dir);
        if (circle == false) return line;
        // circle: |p − c|² = r², linear in (c, r² − |c|²)
        double a11 = 0, a12 = 0, a13 = 0, a22 = 0, a23 = 0, a33 = 0, b1 = 0, b2 = 0, b3 = 0;
        foreach (var p0 in q)
        {
            var p = p0 - mean; var rhs = p.X * p.X + p.Y * p.Y;
            a11 += 4 * p.X * p.X; a12 += 4 * p.X * p.Y; a13 += 2 * p.X; a22 += 4 * p.Y * p.Y; a23 += 2 * p.Y; a33 += 1;
            b1 += 2 * p.X * rhs; b2 += 2 * p.Y * rhs; b3 += rhs;
        }
        if (!Solve3(a11, a12, a13, a22, a23, a33, b1, b2, b3, out var cx, out var cy, out var k) || !(k + cx * cx + cy * cy > 0))
            return circle == true ? null : line;
        var c = mean + new Vec2(cx, cy); var r = Math.Sqrt(k + cx * cx + cy * cy);
        double circErr = 0;
        foreach (var p in q) circErr = Math.Max(circErr, Math.Abs((p - c).Length - r));
        if (circle == true || circErr < lineErr * 0.5) return (true, circErr, c, r, dir);
        return line;
    }

    /// <summary>Eigenvalues (ascending) and eigenvectors (columns) of a symmetric 6×6 matrix (cyclic Jacobi).</summary>
    static (double[] vals, double[,] vecs) Eigen6(double[,] m)
    {
        const int n = 6;
        var a = (double[,])m.Clone();
        var v = new double[n, n];
        for (int i = 0; i < n; i++) v[i, i] = 1;
        for (int sweep = 0; sweep < 100; sweep++)
        {
            double off = 0, diag = 0;
            for (int p = 0; p < n; p++) { diag += a[p, p] * a[p, p]; for (int q = p + 1; q < n; q++) off += a[p, q] * a[p, q]; }
            if (off <= 1e-30 * diag) break;
            for (int p = 0; p < n; p++)
                for (int q = p + 1; q < n; q++)
                {
                    if (a[p, q] == 0) continue;
                    var theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    var t = (theta >= 0 ? 1 : -1) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    var c = 1 / Math.Sqrt(t * t + 1); var s = t * c;
                    for (int k = 0; k < n; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq; a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk; a[q, k] = s * apk + c * aqk;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq; v[k, q] = s * vkp + c * vkq;
                    }
                }
        }
        var idx = Enumerable.Range(0, n).OrderBy(i => a[i, i]).ToArray();
        var vals = idx.Select(i => a[i, i]).ToArray();
        var vecs = new double[n, n];
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++) vecs[i, j] = v[i, idx[j]];
        return (vals, vecs);
    }

    /// <summary>
    /// A fitted cylinder's axis, centre and radius made the roundest values that keep the patch within the band its
    /// radius wobbles in: least squares on the noise lands anywhere in it (two copies of one strip, fitted apart,
    /// a few tenths of a micron different, which no boolean takes as coincident); the round values are the same for
    /// both, and most likely what was drawn. An exact cylinder (band within tol) is left as found.
    /// </summary>
    static void Round(NurbsSurface s, double tol, ref Vec3 axis, ref Vec3 center, ref double r)
    {
        const int M = 64;
        var pts = new List<Vec3>((M + 1) * (M + 1));
        for (int i = 0; i <= M; i++)
            for (int j = 0; j <= M; j++)
                pts.Add(s.Eval(s.U0 + (s.U1 - s.U0) * i / M, s.V0 + (s.V1 - s.V0) * j / M));
        (double lo, double hi) Band(Vec3 ax, Vec3 c)
        {
            double lo = double.MaxValue, hi = 0;
            foreach (var p in pts)
            {
                var d = p - c; d -= ax * Vec3.Dot(d, ax);
                var l = d.Length; lo = Math.Min(lo, l); hi = Math.Max(hi, l);
            }
            return (lo, hi);
        }
        var (lo0, hi0) = Band(axis, center);
        if (hi0 - lo0 <= 2 * tol) return;
        var width = hi0 - lo0 + 0.05 * (hi0 - lo0) + tol;
        static Vec3 R(Vec3 v, int d) => new(Math.Round(v.X, d), Math.Round(v.Y, d), Math.Round(v.Z, d));
        for (int d = 0; d <= 9; d++)
        {
            var ax = R(axis, d);
            if (!(ax.LengthSq > 0.5)) continue;
            ax = ax.Normalized;
            var (lo, hi) = Band(ax, center);
            if (hi - lo <= width) { axis = ax; break; }
        }
        for (int d = 0; d <= 9; d++)
        {
            var c = R(center, d);
            var (lo, hi) = Band(axis, c);
            if (hi - lo <= width) { center = c; break; }
        }
        var (blo, bhi) = Band(axis, center);
        for (int d = 0; d <= 9; d++)
        {
            var f = Math.Pow(10, d);
            var rr = Math.Ceiling(blo * f) / f;
            if (rr <= bhi) { r = rr; return; }
        }
        r = 0.5 * (blo + bhi);
    }

    /// <summary>
    /// The cone the surface is, or null: every normal at one angle to the axis (the differences of the normals are
    /// perpendicular to it), every normal line through it, and the radius linear along it.
    /// </summary>
    static NSurface? Cone(NurbsSurface s, List<Vec3> pts, List<Vec3> nrm, double tol, out bool flip)
    {
        flip = false;
        // the axis: perpendicular to the normals' differences
        Vec3 axis = default; double best = 0;
        for (int i = 1; i < nrm.Count; i++)
            for (int j = i + 1; j < nrm.Count; j++)
            {
                var c = Vec3.Cross(nrm[i] - nrm[0], nrm[j] - nrm[0]);
                if (c.LengthSq > best) { best = c.LengthSq; axis = c; }
            }
        if (best < 1e-12) return null;
        axis = axis.Normalized;
        var cos = Vec3.Dot(nrm[0], axis);
        foreach (var n in nrm) if (Math.Abs(Vec3.Dot(n, axis) - cos) > 1e-7) return null;
        if (Math.Abs(cos) > 1 - 1e-6) return null;
        // a point of the axis: where the normal lines (seen along the axis) meet, by least squares
        var x = axis.AnyPerpendicular(); var y = Vec3.Cross(axis, x);
        var o = pts[0];
        double a11 = 0, a12 = 0, a22 = 0, b1 = 0, b2 = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var q = new Vec2(Vec3.Dot(pts[i] - o, x), Vec3.Dot(pts[i] - o, y));
            var m = new Vec2(Vec3.Dot(nrm[i], x), Vec3.Dot(nrm[i], y)).Normalized;
            // (I - m mᵀ) c = (I - m mᵀ) q
            double p11 = 1 - m.X * m.X, p12 = -m.X * m.Y, p22 = 1 - m.Y * m.Y;
            a11 += p11; a12 += p12; a22 += p22;
            b1 += p11 * q.X + p12 * q.Y; b2 += p12 * q.X + p22 * q.Y;
        }
        var det = a11 * a22 - a12 * a12;
        if (Math.Abs(det) < 1e-12 * pts.Count * pts.Count) return null;
        var c2 = new Vec2((b1 * a22 - b2 * a12) / det, (a11 * b2 - a12 * b1) / det);
        var centre = o + x * c2.X + y * c2.Y;
        // the radius along the axis: r = r0 + k h
        var h = pts.Select(p => Vec3.Dot(p - centre, axis)).ToList();
        var r = pts.Select((p, i) => { var d = p - centre; return (d - axis * h[i]).Length; }).ToList();
        double hm = h.Average(), rm = r.Average(), shh = 0, shr = 0;
        for (int i = 0; i < h.Count; i++) { shh += (h[i] - hm) * (h[i] - hm); shr += (h[i] - hm) * (r[i] - rm); }
        if (shh < 1e-24) return null;
        var k = shr / shh;
        if (Math.Abs(k) < 1e-9) return null;
        double R(double hh) => rm + k * (hh - hm);
        for (int i = 0; i < h.Count; i++) if (Math.Abs(r[i] - R(h[i])) > tol) return null;
        // also between the samples
        for (int i = 0; i + 1 < N; i++)
            for (int j = 0; j + 1 < N; j++)
            {
                var p = s.Eval(s.U0 + (s.U1 - s.U0) * (i + 0.5) / (N - 1), s.V0 + (s.V1 - s.V0) * (j + 0.5) / (N - 1));
                var hh = Vec3.Dot(p - centre, axis);
                var d = p - centre - axis * hh;
                if (Math.Abs(d.Length - R(hh)) > tol) return null;
            }
        // the axis towards the growing radius, the origin at the first sample's height (its radius there)
        if (k < 0) { axis = -axis; k = -k; for (int i = 0; i < h.Count; i++) h[i] = -h[i]; }
        var origin = centre + axis * h[0];
        var refDir = pts[0] - origin; refDir -= axis * Vec3.Dot(refDir, axis);
        var cone = new NSurface(new SurfaceDef { Kind = SurfaceKind.Cone, Origin = origin, Axis = axis, RefDir = refDir.Normalized, Radius = r[0], Param2 = Math.Atan(k), Segments = SurfaceDef.SegmentsFor(Math.Max(r.Max(), 1e-3)) });
        // the normals agree (one side throughout)
        int agree = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var dot = Vec3.Dot(cone.Normal(pts[i]), nrm[i]);
            if (Math.Abs(Math.Abs(dot) - 1) > 1e-6) return null;
            agree += dot > 0 ? 1 : -1;
        }
        if (Math.Abs(agree) != pts.Count) return null;
        flip = agree < 0;
        return cone;
    }

    /// <summary>The line or circle a curve (on [0, 1], increasing in the edge's direction) is, or null.</summary>
    public static NCurve? Curve(Func<double, Vec3> f, double tol)
    {
        const int n = 24;
        var pts = Enumerable.Range(0, n + 1).Select(i => f((double)i / n)).ToArray();
        var a = pts[0]; var b = pts[^1];
        var len = Vec3.Distance(a, b);
        if (len > tol)
        {
            var d = (b - a) * (1 / len);
            bool straight = true;
            foreach (var p in pts) { var q = p - a; if ((q - d * Vec3.Dot(q, d)).Length > tol) { straight = false; break; } }
            if (straight)
            {
                // a line runs one way (a spline doubling back is not an edge)
                for (int i = 1; i <= n; i++) if (Vec3.Dot(pts[i] - pts[i - 1], d) <= 0) return null;
                return new NLine(a, d);
            }
        }
        // circle through three of the points
        var p1 = pts[n / 3]; var p2 = pts[2 * n / 3];
        var axis = Vec3.Cross(p1 - a, p2 - a);
        if (axis.LengthSq < 1e-24) return null;
        axis = axis.Normalized;
        var c = Circumcenter(a, p1, p2, axis);
        var r = Vec3.Distance(a, c);
        foreach (var t in Enumerable.Range(0, 2 * n + 1).Select(i => (double)i / (2 * n)))
        {
            var p = f(t);
            if (Math.Abs(Vec3.Dot(p - c, axis)) > tol || Math.Abs(Vec3.Distance(p, c) - r) > tol) return null;
        }
        // counter-clockwise about the axis in the curve's direction
        if (Vec3.Dot(Vec3.Cross(pts[0] - c, pts[1] - c), axis) < 0) axis = -axis;
        return new NCircle(c, axis, a - c, r);
    }

    static Vec3 Circumcenter(Vec3 a, Vec3 b, Vec3 c, Vec3 n)
    {
        var ab = b - a; var ac = c - a;
        var num = Vec3.Cross(n, ab) * ac.LengthSq - Vec3.Cross(n, ac) * ab.LengthSq;
        return a + num * (1 / (2 * Vec3.Dot(Vec3.Cross(ab, ac), n)));
    }

    /// <summary>Symmetric 3×3 system by Cramer's rule.</summary>
    static bool Solve3(double a11, double a12, double a13, double a22, double a23, double a33, double b1, double b2, double b3, out double x1, out double x2, out double x3)
    {
        var det = a11 * (a22 * a33 - a23 * a23) - a12 * (a12 * a33 - a23 * a13) + a13 * (a12 * a23 - a22 * a13);
        x1 = x2 = x3 = 0;
        if (Math.Abs(det) < 1e-300) return false;
        x1 = (b1 * (a22 * a33 - a23 * a23) - a12 * (b2 * a33 - a23 * b3) + a13 * (b2 * a23 - a22 * b3)) / det;
        x2 = (a11 * (b2 * a33 - a23 * b3) - b1 * (a12 * a33 - a23 * a13) + a13 * (a12 * b3 - b2 * a13)) / det;
        x3 = (a11 * (a22 * b3 - b2 * a23) - a12 * (a12 * b3 - b2 * a13) + b1 * (a12 * a23 - a22 * a13)) / det;
        return true;
    }
}
