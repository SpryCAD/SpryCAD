namespace FeatherCad.Kernel.Native;

/// <summary>
/// Exact shell: every face surface is offset analytically (planes shift, cylinder/sphere radii and torus
/// minor radii change, cones keep their angle), vertices and edges are recomputed as intersections of the
/// offset surfaces, and the resulting inner body is subtracted with the native boolean. Open faces are
/// offset outward so the inner body breaks through them.
/// </summary>
public static class NShell
{
    public static NBody Apply(NBody body, ISet<string> openFaces, double thickness, string prefix)
    {
        var inner = Offset(body, f => openFaces.Contains(f.Tag.Name) ? thickness : -thickness, prefix + ".I.");
        var err = inner.Validate();
        if (err != null) throw new InvalidOperationException("shell: " + err);
        return NBoolean.Run(body, inner, BoolOp.Subtract);
    }

    /// <summary>Offsets each face along its outward normal by dist(face) and rebuilds the topology.</summary>
    public static NBody Offset(NBody body, Func<NFace, double> dist, string tagPrefix)
    {
        body.Link();
        var scale = Math.Max(body.Bounds.Diagonal, 1e-9);
        var tol = 1e-9 * scale;

        var surf = new Dictionary<NFace, NSurface>(ReferenceEqualityComparer.Instance);
        var tags = new Dictionary<FaceTag, FaceTag>(ReferenceEqualityComparer.Instance);
        foreach (var f in body.Faces)
        {
            var d = dist(f) * (f.Sense ? 1 : -1); // along the natural normal
            surf[f] = OffsetSurface(f.Surface, d);
        }

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
            if (res > 1e-6 * scale) throw new InvalidOperationException("shell: offset corner has no solution");
            vmap[v] = new NVertex(p);
        }

        // edges
        var emap = new Dictionary<NEdge, NEdge>(ReferenceEqualityComparer.Instance);
        foreach (var e in body.Edges)
        {
            if (e.Uses.Count != 2) throw new InvalidOperationException("shell: open body");
            var sa = surf[e.Uses[0].Face]; var sb = surf[e.Uses[1].Face];
            emap[e] = OffsetEdge(e, sa, sb, vmap[e.V0], vmap[e.V1], tol, scale);
        }

        var res2 = new NBody();
        foreach (var f in body.Faces)
        {
            if (!tags.TryGetValue(f.Tag, out var tag))
            {
                var s = surf[f];
                tags[f.Tag] = tag = new FaceTag(tagPrefix + f.Tag.Name, s.Kind == SurfaceKind.Plane ? f.Tag.Surface : s.Def);
            }
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
        }
        throw new NotSupportedException();
    }

    static NEdge OffsetEdge(NEdge e, NSurface sa, NSurface sb, NVertex v0, NVertex v1, double tol, double scale)
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
            if (r > 1e-6 * scale) throw new InvalidOperationException("shell: offset edge has no solution");
            pts.Add(p);
        }
        pts.Add(v1.P);
        var fitTol = 1e-7 * scale;

        var cands = new List<NCurve>();
        if (!sa.CoincidentWith(sb, tol))
        {
            try { var an = NIntersect.Analytic(sa, sb, fitTol); if (an != null) cands.AddRange(an); } catch { }
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
