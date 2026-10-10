namespace SpryCad.Kernel.Native;

/// <summary>
/// Exact shell: every face surface is offset analytically (planes shift, cylinder/sphere radii and torus
/// minor radii change, cones keep their angle), vertices and edges are recomputed as intersections of the
/// offset surfaces, and the resulting inner body is subtracted with the native boolean. Open faces are
/// offset outward so the inner body breaks through them.
/// </summary>
public static class NShell
{
    /// <summary>Diagnostics (tests/tools).</summary>
    public static Action<string>? Debug;

    public static NBody Apply(NBody body, ISet<string> openFaces, double thickness, string prefix)
    {
        var inner = Offset(body, f => openFaces.Contains(f.Tag.Name) ? thickness : -thickness, prefix + ".I.");
        var err = inner.Validate();
        if (err != null) throw new InvalidOperationException("shell: " + err);
        return NBoolean.Run(body, inner, BoolOp.Subtract);
    }

    /// <summary>Offsets each face along its outward normal by dist(face) and rebuilds the topology.</summary>
    public static NBody Offset(NBody body, Func<NFace, double> dist, string tagPrefix) =>
        Rebuild(body,
            f => OffsetSurface(f.Surface, dist(f) * (f.Sense ? 1 : -1)), // along the natural normal
            (f, s) => new FaceTag(tagPrefix + f.Tag.Name, s.Kind == SurfaceKind.Plane ? f.Tag.Surface : s.Def),
            "shell");

    /// <summary>
    /// The body with new surfaces for its faces (<paramref name="surfaceOf"/>), its topology kept: each vertex moved
    /// onto its faces' new surfaces, each edge made again as the intersection of its two faces' new surfaces.
    /// <paramref name="tagOf"/>: a face's tag given its new surface (asked once per tag). Fails (with
    /// <paramref name="what"/> in the message) where a corner or an edge has no solution, i.e. where the new
    /// surfaces would change the topology.
    /// </summary>
    public static NBody Rebuild(NBody body, Func<NFace, NSurface> surfaceOf, Func<NFace, NSurface, FaceTag> tagOf, string what)
    {
        body.Link();
        var scale = Math.Max(body.Bounds.Diagonal, 1e-9);
        var tol = 1e-9 * scale;

        var surf = new Dictionary<NFace, NSurface>(ReferenceEqualityComparer.Instance);
        var tags = new Dictionary<FaceTag, FaceTag>(ReferenceEqualityComparer.Instance);
        foreach (var f in body.Faces) surf[f] = surfaceOf(f);

        // vertices: on all offset surfaces of their faces (minimum-norm move, so tangent faces offset along the normal)
        var vFaces = new Dictionary<NVertex, List<NSurface>>(ReferenceEqualityComparer.Instance);
        foreach (var f in body.Faces)
            foreach (var c in f.Coedges)
                foreach (var v in new[] { c.Edge.V0, c.Edge.V1 })
                {
                    if (!vFaces.TryGetValue(v, out var l)) vFaces[v] = l = new();
                    if (!l.Contains(surf[f])) l.Add(surf[f]);
                }
        var vmap = new Dictionary<NVertex, NVertex>(ReferenceEqualityComparer.Instance);
        foreach (var (v, ss) in vFaces)
        {
            var p = Solve(ss, v.P, tol, out var res);
            Debug?.Invoke($"vertex {v.P} -> {p} res {res:g2} on {string.Join(",", ss.Select(x => x.Kind))}");
            if (res > 1e-6 * scale) throw new InvalidOperationException(what + ": a corner has no solution");
            vmap[v] = new NVertex(p);
        }

        // edges
        var emap = new Dictionary<NEdge, NEdge>(ReferenceEqualityComparer.Instance);
        foreach (var e in body.Edges)
        {
            if (e.Uses.Count != 2) throw new InvalidOperationException(what + ": open body");
            var sa = surf[e.Uses[0].Face]; var sb = surf[e.Uses[1].Face];
            // an edge between two faces that keep their surfaces, whose ends stay: kept as it is
            emap[e] = ReferenceEquals(sa, e.Uses[0].Face.Surface) && ReferenceEquals(sb, e.Uses[1].Face.Surface)
                && vmap[e.V0].P == e.V0.P && vmap[e.V1].P == e.V1.P
                ? new NEdge(e.Curve, e.T0, e.T1, vmap[e.V0], vmap[e.V1])
                : OffsetEdge(e, sa, sb, vmap[e.V0], vmap[e.V1], tol, scale, what);
        }

        var res2 = new NBody();
        foreach (var f in body.Faces)
        {
            if (!tags.TryGetValue(f.Tag, out var tag)) tags[f.Tag] = tag = tagOf(f, surf[f]);
            Vec3? anchor = f.Anchor is { } a ? Solve(new[] { surf[f] }, a, tol, out _) : null;
            var nf = new NFace(surf[f], f.Sense, tag, anchor);
            foreach (var l in f.Loops)
            {
                var nl = new NLoop();
                foreach (var c in l.Coedges) nl.Coedges.Add(new NCoedge(emap[c.Edge], c.Forward));
                nf.Loops.Add(nl);
            }
            res2.Faces.Add(nf);
        }
        res2.Link();
        return res2;
    }

    public static NSurface OffsetSurface(NSurface s, double d)
    {
        switch (s.Kind)
        {
            case SurfaceKind.Plane: return NSurface.FromPlane(s.Plane!.Offset(d));
            case SurfaceKind.Cylinder:
            case SurfaceKind.Sphere:
                if (s.R + d <= 1e-9) throw new InvalidOperationException("shell: thickness exceeds a radius");
                return new NSurface(s.Def with { Radius = s.R + d });
            case SurfaceKind.Torus:
                if (s.R2 + d <= 1e-9) throw new InvalidOperationException("shell: thickness exceeds a fillet radius");
                return new NSurface(s.Def with { Param2 = s.R2 + d });
            case SurfaceKind.Cone:
                return new NSurface(s.Def with { Radius = s.R + d / Math.Cos(s.R2) });
            case SurfaceKind.Pipe:
                if (s.R + d <= 1e-9) throw new InvalidOperationException("shell: thickness exceeds a fillet radius");
                return new NSurface(s.Def with { Radius = s.R + d });
            case SurfaceKind.BSpline:
            case SurfaceKind.Offset:
                return NSurface.OffsetOf(s, d);
            case SurfaceKind.Screw:
                throw new InvalidOperationException("shell: helical faces cannot be offset yet");
        }
        throw new NotSupportedException();
    }

    static NEdge OffsetEdge(NEdge e, NSurface sa, NSurface sb, NVertex v0, NVertex v1, double tol, double scale, string what)
    {
        // points of the offset edge (projected from samples of the original)
        const int n = 12;
        var pts = new List<Vec3> { v0.P };
        var ss = new[] { sa, sb };
        for (int i = 1; i < n; i++)
        {
            double s = (double)i / n;
            var guess = e.At(s) + (v0.P - e.V0.P) * (1 - s) + (v1.P - e.V1.P) * s;
            var p = Solve(ss, guess, tol, out var r);
            if (r > 1e-6 * scale) throw new InvalidOperationException(what + ": an edge has no solution");
            pts.Add(p);
        }
        pts.Add(v1.P);
        // a transversal (non-tangent) intersection is followed continuously from vertex to vertex instead: solving
        // each sample on its own can jump between branches (e.g. the near and far side of a tube cut by a plane)
        if (e.Curve is not NLine && Vec3.Cross(sa.Grad(v0.P), sb.Grad(v0.P)).Length > 1e-3)
        {
            var dir0 = e.Curve.Deriv(e.T0).Normalized;
            var length = Math.Max(e.Length, Vec3.Distance(v0.P, v1.P));
            if (March(sa, sb, v0.P, v1.P, dir0, length, scale) is { } marched) pts = marched;
        }
        if (sa.Kind == SurfaceKind.Pipe || sb.Kind == SurfaceKind.Pipe)
            Debug?.Invoke($"edge {e.Curve.GetType().Name} {sa.Kind}/{sb.Kind}: " + string.Join(" ", pts.Select(p => p.ToString())));
        var fitTol = 1e-7 * scale;

        var cands = new List<NCurve>();
        if (!sa.CoincidentWith(sb, tol))
        {
            try { var an = NIntersect.Analytic(sa, sb, fitTol); if (an != null) cands.AddRange(an); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        // same-family fits (tangent faces give degenerate analytic intersections)
        switch (e.Curve)
        {
            case NLine:
                if ((v1.P - v0.P).Length > tol) cands.Add(new NLine(v0.P, v1.P - v0.P));
                break;
            case NCircle c:
                {
                    var ctr = c.C + c.Axis * Vec3.Dot(v0.P - c.C, c.Axis);
                    var r = (v0.P - ctr).Length;
                    if (r > tol) cands.Add(new NCircle(ctr, c.Axis, c.X, r));
                    break;
                }
        }
        foreach (var cv in cands)
        {
            if (!pts.All(p => Vec3.Distance(cv.Eval(cv.Param(p)), p) < fitTol)) continue;
            var edge = Trim(cv, pts, v0, v1);
            if (edge != null) return edge;
        }
        var bs = BSplineCurve.Interpolate(pts, 3);
        return new NEdge(new NSpline(bs), 0, 1, v0, v1);
    }

    /// <summary>
    /// Points along the intersection of a and b from p0 to p1, starting in direction dir0: steps along the tangent
    /// (∇a × ∇b), projected back onto both surfaces, turning at most a few degrees per step. Null when the curve
    /// cannot be followed to p1 (tangent surfaces, a different branch).
    /// </summary>
    static List<Vec3>? March(NSurface a, NSurface b, Vec3 p0, Vec3 p1, Vec3 dir0, double length, double scale)
    {
        Vec3? Tan(Vec3 p, Vec3 prev)
        {
            var t = Vec3.Cross(a.Grad(p), b.Grad(p));
            if (t.Length < 1e-6) return null;
            t = t.Normalized;
            return Vec3.Dot(t, prev) < 0 ? -t : t;
        }
        var pts = new List<Vec3> { p0 };
        var cur = p0;
        if (Tan(cur, dir0) is not { } ct) return null;
        double hmax = length / 8, hmin = length * 1e-6, h = hmax / 4, travelled = 0;
        var tol = 1e-9 * scale;
        for (int it = 0; it < 20000; it++)
        {
            if (Vec3.Distance(cur, p1) <= 1.5 * h && Vec3.Dot(p1 - cur, ct) > 0) { pts.Add(p1); return pts; }
            var nx = NSurface.ProjectToIntersection(a, b, cur + ct * h);
            var ok = Math.Abs(a.F_(nx)) < tol && Math.Abs(b.F_(nx)) < tol && Vec3.Dot(nx - cur, ct) > 0;
            Vec3? nt = ok ? Tan(nx, ct) : null;
            // small turns only: the interpolating spline through the points then follows the curve closely
            if (nt == null || Vec3.Dot(nt.Value, ct) < Math.Cos(Math.PI / 36) || Vec3.Distance(nx, cur) > 1.5 * h)
            {
                h /= 2;
                if (h < hmin) return null;
                continue;
            }
            travelled += Vec3.Distance(cur, nx);
            if (travelled > 4 * length + hmax) return null;
            pts.Add(nx); cur = nx; ct = nt.Value;
            if (Vec3.Dot(nt.Value, ct) > Math.Cos(Math.PI / 72)) h = Math.Min(h * 1.5, hmax);
        }
        return null;
    }

    /// <summary>Edge on curve c running through pts (from v0 to v1) with increasing parameter.</summary>
    static NEdge? Trim(NCurve c, List<Vec3> pts, NVertex v0, NVertex v1)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            var t0 = c.Param(pts[0]);
            double prev = t0; bool inc = true;
            for (int i = 1; i < pts.Count; i++)
            {
                var t = c.Param(pts[i], c.Periodic ? prev : null);
                if (t <= prev) { inc = false; break; }
                prev = t;
            }
            if (inc && prev - t0 > 1e-12) return new NEdge(c, t0, prev, v0, v1);
            // reverse the curve's direction and try again
            c = c switch
            {
                NLine l => new NLine(l.P, -l.D),
                NCircle ci => new NCircle(ci.C, -ci.Axis, ci.X, ci.R),
                NEllipse el => new NEllipse(el.C, el.X, -el.Y, el.A, el.B),
                _ => c,
            };
            if (c is not (NLine or NCircle or NEllipse)) return null;
        }
        return null;
    }

    /// <summary>
    /// Point on all surfaces closest (in the minimum-norm Gauss-Newton sense) to p0. Dependent constraints
    /// (tangent surfaces) are dropped per step, so the move stays along the shared normal.
    /// </summary>
    static Vec3 Solve(IReadOnlyList<NSurface> ss, Vec3 p0, double tol, out double residual)
    {
        var p = p0;
        residual = double.MaxValue;
        for (int it = 0; it < 60; it++)
        {
            var rows = new List<Vec3>(); var rhs = new List<double>(); var basis = new List<Vec3>();
            residual = 0;
            // largest residuals first so the dominant constraints are kept
            foreach (var s in ss.OrderByDescending(s => Math.Abs(s.F_(p))))
            {
                var f = s.F_(p);
                residual = Math.Max(residual, Math.Abs(f));
                var g = s.Grad(p);
                var o = g;
                foreach (var b in basis) o -= b * Vec3.Dot(o, b);
                if (o.Length < 1e-6 * Math.Max(g.Length, 1e-300) || basis.Count == 3) continue;
                basis.Add(o.Normalized);
                rows.Add(g); rhs.Add(f);
            }
            if (residual < tol * 1e-3) break;
            int k = rows.Count;
            var m = new double[k, k + 1];
            for (int i = 0; i < k; i++)
            {
                for (int j = 0; j < k; j++) m[i, j] = Vec3.Dot(rows[i], rows[j]);
                m[i, k] = rhs[i];
            }
            var y = SolveSmall(m, k);
            var step = Vec3.Zero;
            for (int i = 0; i < k; i++) step += rows[i] * y[i];
            p -= step;
            if (step.Length < tol * 1e-6) break;
        }
        residual = ss.Max(s => Math.Abs(s.F_(p)));
        return p;
    }

    static double[] SolveSmall(double[,] m, int k)
    {
        for (int c = 0; c < k; c++)
        {
            int piv = c;
            for (int r = c + 1; r < k; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c])) piv = r;
            if (piv != c) for (int j = 0; j <= k; j++) (m[c, j], m[piv, j]) = (m[piv, j], m[c, j]);
            var d = m[c, c];
            if (Math.Abs(d) < 1e-300) continue;
            for (int r = 0; r < k; r++)
            {
                if (r == c) continue;
                var fct = m[r, c] / d;
                for (int j = c; j <= k; j++) m[r, j] -= fct * m[c, j];
            }
        }
        var y = new double[k];
        for (int i = 0; i < k; i++) y[i] = Math.Abs(m[i, i]) > 1e-300 ? m[i, k] / m[i, i] : 0;
        return y;
    }
}
