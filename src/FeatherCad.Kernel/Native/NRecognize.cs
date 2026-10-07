namespace FeatherCad.Kernel.Native;

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
    public static NSurface? Surface(NurbsSurface s, double tol, out bool flip)
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
        foreach (var n in nrm) if (Math.Abs(Vec3.Dot(n, axis)) > 1e-7) return null;

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
        bool outward = true;
        for (int i = 0; i < pts.Count; i++)
        {
            var d = pts[i] - center; d -= axis * Vec3.Dot(d, axis);
            if (Math.Abs(d.Length - r) > tol) return null;
            var dot = Vec3.Dot(d.Normalized, nrm[i]);
            if (Math.Abs(Math.Abs(dot) - 1) > 1e-7) return null;
            if (i == 0) outward = dot > 0;
            else if (dot > 0 != outward) return null;
        }
        // also between the samples: the poles' mid-span points
        for (int i = 0; i + 1 < N; i++)
            for (int j = 0; j + 1 < N; j++)
            {
                var p = s.Eval(s.U0 + (s.U1 - s.U0) * (i + 0.5) / (N - 1), s.V0 + (s.V1 - s.V0) * (j + 0.5) / (N - 1));
                var d = p - center; d -= axis * Vec3.Dot(d, axis);
                if (Math.Abs(d.Length - r) > tol) return null;
            }
        flip = !outward; // a cylinder's natural normal points away from the axis
        var refDir = (pts[0] - center) - axis * Vec3.Dot(pts[0] - center, axis);
        return new NSurface(new SurfaceDef { Kind = SurfaceKind.Cylinder, Origin = center, Axis = axis, RefDir = refDir, Radius = r, Segments = SurfaceDef.SegmentsFor(r) });
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
