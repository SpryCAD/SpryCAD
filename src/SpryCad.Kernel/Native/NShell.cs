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
        // the topology kept, a face that should vanish at this thickness (a round tighter than the wall, a face
        // narrower than it) turns inside out instead, and its neighbours fold over it: its outline runs the other
        // way round, or crosses itself where it did not. Valid as topology, but the skin passes through itself.
        if (Folded(body, inner) is { } bad) throw new InvalidOperationException($"shell: the inner face of {bad} turns inside out at this thickness");
        // the inner body runs a wall's thickness inside the outer one and breaks out only through the open faces:
        // only an open face and an inner face, or an outer face and an open face's (outward) offset, can meet
        var openInner = openFaces.Select(n => prefix + ".I." + n).ToHashSet();
        return NBoolean.Run(body, inner, BoolOp.Subtract, (a, b) => openFaces.Contains(a) || openInner.Contains(b));
    }

    /// <summary>
    /// The first face whose rebuilt copy (the same index in <paramref name="rebuilt"/>) is turned inside out: its
    /// outline in its surface's parameters the other way round, or crossing itself where the original's does not.
    /// </summary>
    static string? Folded(NBody original, NBody rebuilt)
    {
        string? bad = null;
        Parallel.For(0, Math.Min(original.Faces.Count, rebuilt.Faces.Count), (i, st) =>
        {
            try
            {
                var a = NDomain.Of(original.Faces[i]); var b = NDomain.Of(rebuilt.Faces[i]);
                double sa = a.SignedArea(), sb = b.SignedArea();
                bool flipped = Math.Abs(sa) > 1e-12 && Math.Abs(sb) > 1e-12 && Math.Sign(sa) != Math.Sign(sb);
                if (flipped || Crossings(b) > Crossings(a)) { Debug?.Invoke($"folded {original.Faces[i].Tag.Name} {original.Faces[i].Surface.Kind}: area {sa:g4} -> {sb:g4}, crossings {Crossings(a)} -> {Crossings(b)}, loops {a.Loops.Count}"); lock (original) bad ??= original.Faces[i].Tag.Name; st.Stop(); }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { /* no domain: left to the boolean */ }
        });
        return bad;
    }

    /// <summary>
    /// How many times the domain's loops cross themselves (proper crossings of their segments). Not one loop another: a
    /// hole's offset running into the outline's is an overlap the boolean resolves; a loop through itself is a fold.
    /// </summary>
    static int Crossings(NDomain d)
    {
        var segs = new List<(Vec2 a, Vec2 b, int loop, int k, int n)>();
        for (int li = 0; li < d.Loops.Count; li++)
        {
            var l = d.Loops[li];
            for (int k = 0; k < l.Length; k++) segs.Add((l[k], l[(k + 1) % l.Length], li, k, l.Length));
        }
        static double O(Vec2 p, Vec2 q, Vec2 r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        int n = 0;
        for (int i = 0; i < segs.Count; i++)
            for (int j = i + 1; j < segs.Count; j++)
            {
                var (a0, a1, la, ka, na) = segs[i]; var (b0, b1, lb, kb, _) = segs[j];
                if (la != lb) continue;
                // (neighbours along a loop share an end: not a crossing)
                if (kb == ka + 1 || ka == 0 && kb == na - 1) continue;
                double o1 = O(a0, a1, b0), o2 = O(a0, a1, b1), o3 = O(b0, b1, a0), o4 = O(b0, b1, a1);
                if (o1 * o2 < 0 && o3 * o4 < 0) n++;
            }
        return n;
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
    public static NBody Rebuild(NBody body, Func<NFace, NSurface> surfaceOf, Func<NFace, NSurface, FaceTag> tagOf, string what, bool splitCorners = true)
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
        var pv = Prof.Time("shell.vertices");
        string? cornerFail = null; var unsolved = new List<NVertex>();
        foreach (var (v, all) in vFaces)
        {
            // surfaces tangent at the corner (within a degree) count once: there their offsets share the corner's
            // offset point, and solving both, as they curve apart beside it, only dithers
            var ss = new List<NSurface>();
            var normals = new List<Vec3>();
            foreach (var s in all)
            {
                var n = s.Grad(v.P);
                if (n.Length > 1e-300 && normals.Any(m => Math.Abs(Vec3.Dot(m, n.Normalized)) > Math.Cos(Math.PI / 180))) continue;
                ss.Add(s); normals.Add(n.Length > 1e-300 ? n.Normalized : n);
            }
            var p = Solve(ss, v.P, tol, out var res);
            Debug?.Invoke($"vertex {v.P} -> {p} res {res:g2} on {string.Join(",", ss.Select(x => x.Kind))}");
            if (res > 1e-6 * scale)
            {
                cornerFail ??= what + $": a corner has no solution (at {v.P}, {res:g2} off, on {string.Join(", ", ss.Select(x => x.Kind))}: {string.Join(", ", body.Faces.Where(f => f.Coedges.Any(c => ReferenceEquals(c.Start, v))).Select(f => f.Tag.Name))})";
                if (ss.Count >= 4) unsolved.Add(v);
                continue;
            }
            vmap[v] = new NVertex(p);
        }
        if (cornerFail != null)
        {
            // where four faces meet at a corner their new surfaces need not meet at one point: the corner becomes two,
            // joined by a short new edge between two faces that did not meet (the offset changes the topology there)
            pv.Dispose();
            if (splitCorners && unsolved.Count > 0 && SplitCorners(body, unsolved, surf, tol, scale) is { } split)
                return Rebuild(split, surfaceOf, tagOf, what, splitCorners: false);
            throw new InvalidOperationException(cornerFail);
        }

        pv.Dispose();
        using var pe = Prof.Time("shell.edges");
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

    /// <summary>
    /// A copy of the body with each corner of four faces whose new surfaces have no common point split in two: two
    /// opposite faces round it (Fa, Fc) made to meet along a new (for now zero-length) edge from a corner on Fa, Fb, Fc
    /// to one on Fc, Fd, Fa. Of the two ways, the one whose new corners each lie behind the new surface of the face
    /// it leaves out (inside what the faces bound). Null when no corner can be split.
    /// </summary>
    static NBody? SplitCorners(NBody body, List<NVertex> corners, Dictionary<NFace, NSurface> surf, double tol, double scale)
    {
        var copy = body.Clone(out var emap);
        copy.Link();
        var faceOf = new Dictionary<NFace, NFace>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < body.Faces.Count; i++) faceOf[copy.Faces[i]] = body.Faces[i];
        int done = 0;
        foreach (var v0 in corners)
        {
            // the corner in the copy
            var e0 = body.Edges.FirstOrDefault(e => ReferenceEquals(e.V0, v0) || ReferenceEquals(e.V1, v0));
            if (e0 == null) continue;
            var v = ReferenceEquals(e0.V0, v0) ? emap[e0].V0 : emap[e0].V1;
            // the faces round it in order, with the edge from each to the next: (face, its coedge arriving at v, its
            // coedge leaving v)
            var ring = new List<(NFace f, NLoop l, int iIn)>();
            NCoedge? Leaving(NFace f, out NLoop? loop, out int iIn)
            {
                foreach (var l in f.Loops)
                    for (int i = 0; i < l.Coedges.Count; i++)
                        if (ReferenceEquals(l.Coedges[i].End, v) && ReferenceEquals(l.Coedges[(i + 1) % l.Coedges.Count].Start, v))
                        { loop = l; iIn = i; return l.Coedges[(i + 1) % l.Coedges.Count]; }
                loop = null; iIn = -1; return null;
            }
            var start = copy.Faces.FirstOrDefault(f => f.Coedges.Any(c => ReferenceEquals(c.End, v)));
            if (start == null) continue;
            var cur = start; bool ok = true;
            for (int guard = 0; guard < 8; guard++)
            {
                var outC = Leaving(cur, out var loop, out var iIn);
                if (outC == null || loop == null || cur.Coedges.Count(c => ReferenceEquals(c.End, v)) != 1) { ok = false; break; }
                ring.Add((cur, loop, iIn));
                var next = outC.Edge.Uses.Where(u => !ReferenceEquals(u, outC)).Select(u => u.Face).FirstOrDefault();
                if (next == null || outC.Edge.Uses.Count != 2) { ok = false; break; }
                if (ReferenceEquals(next, start)) break;
                cur = next;
            }
            if (!ok || ring.Count != 4 || ring.Select(x => x.f).Distinct().Count() != 4) continue;
            NSurface S(int i) => surf[faceOf[ring[i % 4].f]];
            // behind face i's new surface (its outward side away), within the tolerance
            bool Behind(int i, Vec3 p) { var f = ring[i % 4].f; return S(i).F_(p) * (f.Sense ? 1 : -1) <= 1e-9 * scale; }
            (int a, Vec3 pa, Vec3 pb)? best = null;
            for (int a = 0; a < 2; a++)
            {
                var pa = Solve(new[] { S(a), S(a + 1), S(a + 2) }, v.P, tol, out var ra);
                var pb = Solve(new[] { S(a + 2), S(a + 3), S(a) }, v.P, tol, out var rb);
                Debug?.Invoke($"split corner {v.P} across faces {a},{a + 2}: {pa} ({ra:g2}) / {pb} ({rb:g2}), behind {Behind(a + 3, pa)}/{Behind(a + 1, pb)} ({S(a + 3).F_(pa) * (ring[(a + 3) % 4].f.Sense ? 1 : -1):g3}/{S(a + 1).F_(pb) * (ring[(a + 1) % 4].f.Sense ? 1 : -1):g3}) faces {string.Join(", ", ring.Select(x => x.f.Tag.Name + ":" + x.f.Surface.Kind))}");
                if (ra > 1e-6 * scale || rb > 1e-6 * scale || !Behind(a + 3, pa) || !Behind(a + 1, pb)) continue;
                if (best == null || Vec3.Distance(pa, pb) < Vec3.Distance(best.Value.pa, best.Value.pb)) best = (a, pa, pb);
            }
            if (best is not { } b) continue;
            // the corner into two: va on faces a, a+1, a+2 (the edges a→a+1, a+1→a+2), vb on a+2, a+3, a
            var va = new NVertex(v.P); var vb = new NVertex(v.P);
            // the edge leaving face i (towards face i+1) is the one face i+1 arrives on
            NEdge Between(int i) { var (f, l, k) = ring[i % 4]; return l.Coedges[(k + 1) % l.Coedges.Count].Edge; }
            var toA = new[] { Between(b.a), Between(b.a + 1) };
            var toB = new[] { Between(b.a + 2), Between(b.a + 3) };
            // where each face's loop passes the corner, before the edges are moved
            var at = ring.Select(x => (x.f, x.l, x.iIn)).ToList();
            foreach (var e in toA) { if (ReferenceEquals(e.V0, v)) e.V0 = va; if (ReferenceEquals(e.V1, v)) e.V1 = va; }
            foreach (var e in toB) { if (ReferenceEquals(e.V0, v)) e.V0 = vb; if (ReferenceEquals(e.V1, v)) e.V1 = vb; }
            var d = b.pb - b.pa;
            var ne = new NEdge(new NLine(v.P, d.Length > 1e-300 ? d : Vec3.UnitX), 0, 1e-9 * scale, va, vb);
            // faces a and a+2 take the new edge between their arriving and leaving coedges (from the one corner to the other)
            foreach (var i in new[] { b.a, b.a + 2 })
            {
                var (f, l, k) = at[i % 4];
                var cin = l.Coedges[k];
                var from = cin.End;
                l.Coedges.Insert(k + 1, new NCoedge(ne, ReferenceEquals(from, va)));
            }
            copy.Link();
            if (copy.Validate() is { } bad) { Debug?.Invoke($"split corner {v.P}: {bad}"); return null; }
            done++;
        }
        return done > 0 ? copy : null;
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
        // (each surface's searches start where the last ended: the points come in order along the edge)
        var na0 = new Near(sa); var nb0 = new Near(sb);
        var ss = new[] { na0, nb0 };
        // how far a point is off the two surfaces
        double Off2(Vec3 q) => Math.Max(Math.Abs(na0.F(q)), Math.Abs(nb0.F(q)));
        // the faces nearly tangent along the edge (within 2°, at its middle): their offsets cross at so small an angle
        // that solving for the crossing is ill-conditioned; each point goes along the mean of the two normals by the
        // mean offset instead, off both surfaces by about the offset times the angle squared over 8
        var qm = e.Mid;
        var ga = na0.Grad(qm); var gb = nb0.Grad(qm);
        bool nearTangent = ga.Length > 1e-300 && gb.Length > 1e-300 && Math.Abs(Vec3.Dot(ga.Normalized, gb.Normalized)) > Math.Cos(2 * Math.PI / 180);
        Vec3 Bisect(Vec3 q)
        {
            var fa = na0.F(q, out var ga1); var fb = nb0.F(q, out var gb1);
            var na = ga1.Normalized; var nb = gb1.Normalized;
            if (Vec3.Dot(na, nb) < 0) nb = -nb;
            var n = (na + nb).Normalized;
            // F = 0 on each offset: step back along n by their mean (F grows along the gradient)
            var f = 0.5 * (fa * Math.Sign(Vec3.Dot(ga1, n)) + fb * Math.Sign(Vec3.Dot(gb1, n)));
            return q - n * f;
        }
        // (the offset edge between its new ends: where that curve passes through them, the ends having moved along
        // the edge too, onto the offsets of the faces at its ends)
        double sFrom = 0, sTo = 1;
        if (nearTangent)
        {
            double Nearest(Vec3 target, double lo, double hi)
            {
                for (int it = 0; it < 60; it++)
                {
                    double m1 = lo + (hi - lo) * 0.382, m2 = lo + (hi - lo) * 0.618;
                    if (Vec3.Distance(Bisect(e.At(m1)), target) < Vec3.Distance(Bisect(e.At(m2)), target)) hi = m2; else lo = m1;
                }
                return 0.5 * (lo + hi);
            }
            sFrom = Nearest(v0.P, 0, 0.5); sTo = Nearest(v1.P, 0.5, 1);
        }
        // points of the offset edge (projected from samples of the original), n pieces
        List<Vec3> Points(int n)
        {
            var pts = new List<Vec3> { v0.P };
            for (int i = 1; i < n; i++)
            {
                double s = (double)i / n;
                if (nearTangent) { pts.Add(Bisect(e.At(sFrom + (sTo - sFrom) * s))); continue; }
                var guess = e.At(s) + (v0.P - e.V0.P) * (1 - s) + (v1.P - e.V1.P) * s;
                var p = Solve(ss, guess, tol, out var r);
                if (r > 1e-6 * scale)
                {
                    Debug?.Invoke($"edge {e.Curve.GetType().Name} {e.V0.P}->{e.V1.P} at {s:0.###}: {guess} -> {p} res {r:g2}, angle {Math.Acos(Math.Clamp(Math.Abs(Vec3.Dot(sa.Grad(guess).Normalized, sb.Grad(guess).Normalized)), 0, 1)) * 180 / Math.PI:0.####}° F {sa.F_(guess):g3} {sb.F_(guess):g3}");
                    throw new InvalidOperationException(what + ": an edge has no solution");
                }
                pts.Add(p);
            }
            pts.Add(v1.P);
            return pts;
        }
        var pts = Points(12);
        // a transversal (non-tangent) intersection is followed continuously from vertex to vertex instead: solving
        // each sample on its own can jump between branches (e.g. the near and far side of a tube cut by a plane)
        bool marched = false;
        if (!nearTangent && e.Curve is not NLine)
        {
            var length = Math.Max(e.Length, Vec3.Distance(v0.P, v1.P));
            if (Vec3.Cross(sa.Grad(v0.P), sb.Grad(v0.P)).Length > 1e-3)
            {
                if (March(sa, sb, v0.P, v1.P, e.Curve.Deriv(e.T0).Normalized, length, scale) is { } m) { pts = m; marched = true; }
            }
            // (from the other end where the faces are tangent at this one, as a loft leaving a collar: points solved
            // one by one there go back and forth along the curve where the surfaces cross at a shallow angle)
            else if (Vec3.Cross(sa.Grad(v1.P), sb.Grad(v1.P)).Length > 1e-3
                && March(sa, sb, v1.P, v0.P, -e.Curve.Deriv(e.T1).Normalized, length, scale) is { } m)
            {
                m.Reverse(); pts = m; marched = true;
            }
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
        // a free-form intersection (offsets of B-splines): a spline through the points, refined until it stays on both
        // surfaces between them too. A loose one leaves the edge off the surfaces by more than a boolean allows, and
        // where such an edge crosses another face (an open face of a shell) the crossing is not on the surfaces: the
        // boolean drops that section and the shell quietly removes nothing.
        var bs = BSplineCurve.Interpolate(pts, 3);
        // how far the spline strays off the surfaces between its points (no better asked than its points are)
        double Off(BSplineCurve c)
        {
            double worst = 0;
            int m = Math.Max(16, 2 * (pts.Count - 1));
            for (int i = 0; i < m; i++) worst = Math.Max(worst, Off2(c.Eval((i + 0.5) / m)));
            return worst;
        }
        double Floor() => 2 * pts.Max(Off2);
        var off = Off(bs);
        // (to a millionth of the part: the boolean takes points on such an edge as on its surfaces within its fit, and
        // the loft's offsets have slight kinks where its faces are only once differentiable, which a finer fit
        // chases with thousands of points)
        var splineTol = 1e-6 * scale;
        for (int n = Math.Max(24, 2 * (pts.Count - 1)); off > Math.Max(splineTol, Floor()) && n <= 384; n *= 2)
        {
            List<Vec3> dense;
            if (marched)
            {
                // between the marched points: each midpoint projected (the march stays on the right branch)
                dense = new List<Vec3> { pts[0] };
                for (int i = 1; i < pts.Count; i++) { dense.Add(Solve(ss, 0.5 * (pts[i - 1] + pts[i]), tol, out _)); dense.Add(pts[i]); }
            }
            else dense = Points(n);
            pts = dense;
            bs = BSplineCurve.Interpolate(pts, 3);
            off = Off(bs);
        }
        if (Prof.Enabled) { Prof.Count($"shell.edge.pts{(nearTangent ? ".tangent" : marched ? ".marched" : "")}", pts.Count); Prof.Count("shell.edge.offTol." + (off <= 1e-6 * scale ? "fit" : "floor"), 1); }
        return new NEdge(new NSpline(bs) { FitTol = off }, 0, 1, v0, v1);
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
    /// <summary>
    /// A surface's signed distance and its gradient from one search, a B-spline's (or its offset's) closest-point
    /// search started where the last one ended (points met in order along an edge: each next to the one before;
    /// the same answers as <see cref="NSurface.FGrad"/>).
    /// </summary>
    sealed class Near(NSurface s)
    {
        public readonly NSurface S = s;
        readonly NurbsSurface? _nb = s.Kind == SurfaceKind.BSpline ? s.Nurbs : s.Kind == SurfaceKind.Offset && s.Base!.Kind == SurfaceKind.BSpline ? s.Base.Nurbs : null;
        double _u = double.NaN, _v = double.NaN;

        public double F(Vec3 p, out Vec3 grad)
        {
            if (_nb == null) return S.FGrad(p, out grad);
            var c = _nb.ClosestFrom(p, _u, _v);
            _u = c.u; _v = c.v;
            grad = _nb.Normal(c.u, c.v);
            var d = Vec3.Dot(p - c.c, grad) >= 0 ? c.dist : -c.dist;
            return S.Kind == SurfaceKind.Offset ? d - S.R : d;
        }

        public double F(Vec3 p) => F(p, out _);
        public Vec3 Grad(Vec3 p) { F(p, out var g); return g; }
    }

    static Vec3 Solve(IReadOnlyList<NSurface> ss, Vec3 p0, double tol, out double residual) => Solve(ss.Select(x => new Near(x)).ToList(), p0, tol, out residual);

    static Vec3 Solve(IReadOnlyList<Near> ss, Vec3 p0, double tol, out double residual)
    {
        var p = p0;
        residual = double.MaxValue;
        for (int it = 0; it < 60; it++)
        {
            var rows = new List<Vec3>(); var rhs = new List<double>(); var basis = new List<Vec3>();
            residual = 0;
            double minSin = 1; // the least angle (its sine) between a row and the ones before it
            // largest residuals first so the dominant constraints are kept (each surface asked once)
            var fg = ss.Select(x => { var f0 = x.F(p, out var g0); return (f: f0, g: g0); }).OrderByDescending(x => Math.Abs(x.f)).ToList();
            foreach (var (f, g) in fg)
            {
                residual = Math.Max(residual, Math.Abs(f));
                var o = g;
                foreach (var b in basis) o -= b * Vec3.Dot(o, b);
                // (nearly tangent surfaces, within a few hundredths of a degree, as one: lofted faces that meet smoothly
                // at a profile are tangent there but only nearly so between, and two nearly parallel rows make the
                // step blow up; what that leaves over is about the thickness times the angle squared)
                if (o.Length < 1e-3 * Math.Max(g.Length, 1e-300) || basis.Count == 3) continue;
                if (basis.Count > 0) minSin = Math.Min(minSin, o.Length / g.Length);
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
            // damped: no further than a few times what is still off over the angle between the surfaces (a point off
            // two surfaces meeting at a small angle moves that much further to reach both); without a bound, two
            // nearly parallel surfaces, one counted and the other not quite tangent after a step, throw it far away
            var cap = 4 * residual / Math.Max(minSin, 0.01) + tol;
            if (step.Length > cap) step *= cap / step.Length;
            p -= step;
            if (step.Length < tol * 1e-6) break;
        }
        residual = ss.Max(x => Math.Abs(x.F(p)));
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
