using FeatherCad.Kernel.Native;

namespace FeatherCad.Kernel;

/// <summary>
/// Fillets and chamfers on native B-rep edges: tools are built from the exact edge curve and the adjacent
/// faces' surfaces (no tessellation), then subtracted (convex) or added (concave) with native booleans.
/// Lines between planes and arcs between a plane and a perpendicular cylinder use extruded/revolved profiles;
/// fillets on any other pair of faces use rolling-ball tools (<see cref="RollingBall"/>).
/// </summary>
public static class NativeBlend
{
    /// <summary>
    /// Applies the blend. Edges whose blend runs past the faces it should end on are, with
    /// <paramref name="overflow"/>, trimmed where they meet the neighbouring geometry ("keep edge" overflow);
    /// otherwise they fail. Failures are reported in <paramref name="errors"/>, their edges (as polylines) in
    /// <paramref name="problemEdges"/>.
    /// </summary>
    public static Solid Apply(Solid body, IReadOnlyList<NEdge> edges, double size, bool fillet, string prefix, List<string> errors,
        bool overflow = true, List<Vec3[]>? problemEdges = null, List<(Solid tool, bool cut)>? applied = null, ChamferSize? chamfer = null)
    {
        if (size <= 0) { errors.Add("Size must be greater than zero."); return body; }
        body.Native!.Link();
        var ch = chamfer ?? ChamferSize.Equal(size);
        // an unequal chamfer is measured on the same face all along a chain (e.g. the top face around a box):
        // the reference is the first edge's face (the other one when flipped), carried to edges sharing it
        NFace? refA = null, refB = null;
        if (edges.Count > 0) { var (f0, f1) = Faces(edges[0]); (refA, refB) = ch.Flip ? (f1, f0) : (f0, f1); }
        static bool Like(NFace f, NFace? g) => g != null && (ReferenceEquals(f, g) || f.Surface.CoincidentWith(g.Surface, 1e-9));
        bool RefIsFirst(NFace x, NFace y) => Like(x, refA) || (!Like(y, refA) && (Like(y, refB) || !Like(x, refB) && !ch.Flip));
        var cutTools = new List<Solid>();
        var addTools = new List<Solid>();
        var done = new HashSet<NEdge>(ReferenceEqualityComparer.Instance);
        bool anyTrimmed = false;
        var trimmedEdges = new List<NEdge>();
        var failedChains = new HashSet<NEdge>(ReferenceEqualityComparer.Instance);
        int i = 0;
        foreach (var e in edges)
        {
            if (!done.Add(e)) continue;
            var name = $"{prefix}.{i++}";
            var (fa, fb) = Faces(e);
            var group = new List<NEdge> { e };
            if (e.Curve is NCircle c)
                // arcs of one circle between the same faces form one rim (bodies split full circles)
                foreach (var o in edges)
                    if (!done.Contains(o) && o.Curve is NCircle oc && SameCircle(c, oc) && SameFaces(e, o)) { group.Add(o); done.Add(o); }
            void Fail(string msg)
            {
                errors.Add($"Edge {fa.Tag.Name} / {fb.Tag.Name}: {msg}");
                problemEdges?.AddRange(group.Select(g => g.Samples(1e-3).ToArray()));
            }
            try
            {
                var (tools, convex, fits) = ForceRollingBall ? (null, false, false)
                    : e.Curve is NCircle ? ArcTools(group, size, fillet, name, ch, RefIsFirst) : LineTool(e, size, fillet, name, ch, RefIsFirst);
                if (tools == null && fillet)
                {
                    // any other pair of faces: rolling ball along the chain of edges this edge belongs to; first across
                    // faces meeting tangentially (one tool, the ball rolls from face to face), else between the same
                    // two surfaces only (separate tools, trimmed where they run past their faces)
                    RollingBall.Result? rb = null;
                    var tangent = TangentChain(e, edges, done);
                    // a tangent chain that failed as one tool is not tried again from each of its other edges
                    if (tangent.Count > Chain(e, edges).Count && !tangent.Any(failedChains.Contains))
                    {
                        var r2 = RollingBall.Build(tangent, size, name, OnFaces);
                        Debug?.Invoke($"tangent chain {name}: {tangent.Count} edges -> {r2.Error ?? (r2.Fits ? "fits" : "overflows")}");
                        if (r2.Error == null) { rb = r2; foreach (var o in tangent) if (!group.Contains(o)) { group.Add(o); done.Add(o); } }
                        else foreach (var o in tangent) failedChains.Add(o);
                    }
                    if (rb == null)
                    {
                        foreach (var o in Chain(e, edges)) if (!group.Contains(o)) { group.Add(o); done.Add(o); }
                        rb = RollingBall.Build(group, size, name, OnFaces);
                    }
                    if (rb.Error != null) { Fail(rb.Error); continue; }
                    (tools, convex, fits) = (new List<Solid> { rb.Tool! }, rb.Convex, rb.Fits);
                }
                if (Debug != null)
                    Debug($"blend {name}: {string.Join(", ", group.Select(g => $"{g.Curve.GetType().Name}{g.V0.P}->{g.V1.P} [{string.Join("|", g.Uses.Select(u => u.Face.Tag.Name + ":" + u.Face.Surface.Kind))}]"))} -> tools={tools?.Count} fits={fits}");
                if (tools == null) { Fail("unsupported edge geometry."); continue; }
                if (!fits)
                {
                    var trimmed = overflow ? TrimToEdge(body, tools, convex, group, fa, fb) : null;
                    if (trimmed == null)
                    {
                        Fail($"the {(fillet ? "fillet" : "chamfer")} does not fit: it runs past the faces it blends. Use a smaller size.");
                        continue;
                    }
                    tools = new List<Solid> { trimmed };
                    anyTrimmed = true;
                    trimmedEdges.AddRange(group);
                }
                (convex ? cutTools : addTools).AddRange(tools);
                applied?.AddRange(tools.Select(t => (t, convex)));
            }
            catch (Exception ex) { Fail(ex.Message); }
        }
        var result = body;
        if (anyTrimmed)
        {
            // trimmed blends of neighbouring edges can touch each other along tangent curves; merged with each
            // other first that contact is non-manifold, so they are applied to the body one at a time instead
            // no slow facet fallback here: a failure is reported right away with the edges involved
            var old = Csg.NoFallback;
            Csg.NoFallback = true;
            try
            {
                using (Prof.Time("blend.apply"))
                {
                    foreach (var t in cutTools) result = Csg.Subtract(result, t);
                    foreach (var t in addTools) result = Csg.Union(result, t);
                }
            }
            catch (InvalidOperationException)
            {
                errors.Add($"the {(fillet ? "fillets" : "chamfers")} that run into neighbouring geometry also run into each other where both are " +
                           "tangent to the same face; that corner cannot be blended yet. Remove one of these edges or use a smaller size.");
                problemEdges?.AddRange(trimmedEdges.Select(g => g.Samples(1e-3).ToArray()));
                return body;
            }
            finally { Csg.NoFallback = old; }
            return result;
        }
        Solid? cu = null, au = null;
        using (Prof.Time("blend.toolunion"))
        {
            if (cutTools.Count > 0) cu = Csg.UnionAll(cutTools);
            if (addTools.Count > 0) au = Csg.UnionAll(addTools);
        }
        using (Prof.Time("blend.apply"))
        {
            if (cu != null) result = Csg.Subtract(result, cu);
            if (au != null) result = Csg.Union(result, au);
        }
        return result;
    }

    static (NFace a, NFace b) Faces(NEdge e) => (e.Uses[0].Face, e.Uses[1].Face);

    /// <summary>The selected edges connected to e end to end through edges between the same two surfaces.</summary>
    static List<NEdge> Chain(NEdge e, IReadOnlyList<NEdge> selected)
    {
        var res = new List<NEdge> { e };
        for (int i = 0; i < res.Count; i++)
        {
            var c = res[i];
            foreach (var o in selected)
                if (!res.Contains(o) && (ReferenceEquals(o.V0, c.V0) || ReferenceEquals(o.V0, c.V1) || ReferenceEquals(o.V1, c.V0) || ReferenceEquals(o.V1, c.V1))
                    && RollingBall.SameSurfaces(e, o))
                    res.Add(o);
        }
        return res;
    }

    /// <summary>
    /// The selected edges connected to e end to end where one side stays on the same surface and the faces on the
    /// other side meet tangentially at the shared vertex (or are the same surface).
    /// </summary>
    static List<NEdge> TangentChain(NEdge e, IReadOnlyList<NEdge> selected, HashSet<NEdge> done)
    {
        static bool Same(NSurface x, NSurface y) => ReferenceEquals(x, y) || x.CoincidentWith(y, 1e-9);
        var res = new List<NEdge> { e };
        for (int i = 0; i < res.Count; i++)
        {
            var c = res[i];
            foreach (var o in selected)
            {
                if (res.Contains(o) || done.Contains(o) || o.Uses.Count != 2 || c.Uses.Count != 2) continue;
                NVertex? v = ReferenceEquals(o.V0, c.V0) || ReferenceEquals(o.V0, c.V1) ? o.V0 : ReferenceEquals(o.V1, c.V0) || ReferenceEquals(o.V1, c.V1) ? o.V1 : null;
                if (v == null) continue;
                if (RollingBall.SameSurfaces(c, o)) { res.Add(o); continue; }
                bool joined = false;
                foreach (var ca in c.Uses.Select(u => u.Face))
                    foreach (var oa in o.Uses.Select(u => u.Face))
                    {
                        if (joined || !Same(ca.Surface, oa.Surface) || ca.Sense != oa.Sense) continue;
                        var cb = c.Uses.Select(u => u.Face).First(f => !ReferenceEquals(f, ca));
                        var ob = o.Uses.Select(u => u.Face).First(f => !ReferenceEquals(f, oa));
                        if (Same(cb.Surface, ca.Surface) || Same(ob.Surface, oa.Surface)) continue;
                        joined = Vec3.Dot(Outward(cb, v.P), Outward(ob, v.P)) > 1 - 1e-4;
                    }
                if (joined) res.Add(o);
            }
        }
        return res;
    }

    static Vec3 Outward(NFace f, Vec3 p) => f.Surface.Normal(p) * (f.Sense ? 1 : -1);

    /// <summary>Direction of the edge as traversed by the coedge, at curve parameter t.</summary>
    static Vec3 Tangent(NCoedge c, double t) => (c.Edge.Curve.Deriv(t) * (c.Forward ? 1 : -1)).Normalized;

    static (List<Solid>? tools, bool convex, bool fits) LineTool(NEdge e, double size, bool fillet, string name, ChamferSize ch, Func<NFace, NFace, bool> refIsFirst)
    {
        var cA = e.Uses[0]; var cB = e.Uses[1];
        if (e.Curve is not NLine || cA.Face.Surface.Kind != SurfaceKind.Plane || cB.Face.Surface.Kind != SurfaceKind.Plane) return (null, false, false);
        var E = cA.Start.P; var dir = cA.End.P - E; var L = dir.Length; var t = dir / L;
        var nA = Outward(cA.Face, E); var nB = Outward(cB.Face, E);
        // faces lie to the left of their loops (seen from outside): n × direction points into the face
        var uA = Vec3.Cross(nA, t).Normalized;
        var uB = Vec3.Cross(nB, -t).Normalized;
        bool convex = Vec3.Dot(uB, nA) < 0;
        var x = uA; var y = Vec3.Cross(t, x).Normalized;
        var lframe = new Frame(E, x, y, t);
        Vec2 P(Vec3 v) => new(Vec3.Dot(v, x), Vec3.Dot(v, y));
        var (dA, dB) = fillet ? (size, size) : EdgeBlend.ChamferLegs(P(uA), P(uB), ch, refIsFirst(cA.Face, cB.Face));
        var loop = EdgeBlend.Profile2D(P(uA), P(uB), P(nA), P(nB), Vec2.Zero, dA, fillet, convex, dB);
        if (loop == null) return (null, convex, false);
        var (tlA, tlB) = TangentLengths(P(uA), P(uB), size, fillet, dA, dB);
        bool fits = true;
        for (int i = 0; i <= 16 && fits; i++)
        {
            var q = E + t * (L * i / 16);
            fits = OnFaces(cA.Face, q + uA * tlA) && OnFaces(cB.Face, q + uB * tlB);
        }
        var tools = new List<Solid> { Builders.Extrude(new[] { new ProfileRegion(loop) }, lframe, 0, L, name) };
        // ends meeting a face at an angle: an extension past the end, cut back to that face
        var ext = 3 * Math.Max(size, Math.Max(tlA, tlB));
        if (EndPlane(cA.Start, -t, cA.Face, cB.Face, new[] { e }) is { } p0)
            tools.Add(Trim(Builders.Extrude(new[] { new ProfileRegion(loop) }, lframe, -ext, 0, name), p0));
        if (EndPlane(cA.End, t, cA.Face, cB.Face, new[] { e }) is { } p1)
            tools.Add(Trim(Builders.Extrude(new[] { new ProfileRegion(loop) }, lframe, L, L + ext, name), p1));
        tools.RemoveAll(x => x.IsEmpty);
        return (tools, convex, fits);
    }

    static (List<Solid>? tools, bool convex, bool fits) ArcTools(List<NEdge> arcs, double size, bool fillet, string name, ChamferSize ch, Func<NFace, NFace, bool> refIsFirst)
    {
        var e = arcs[0];
        var cP = e.Uses[0]; var cC = e.Uses[1];
        if (cP.Face.Surface.Kind != SurfaceKind.Plane) (cP, cC) = (cC, cP);
        if (cP.Face.Surface.Kind != SurfaceKind.Plane || cC.Face.Surface.Kind != SurfaceKind.Cylinder) return (null, false, false);
        var cs = cC.Face.Surface;
        var ax = cs.Axis;
        var tm = 0.5 * (e.T0 + e.T1);
        var M = e.Curve.Eval(tm);
        var nP = Outward(cP.Face, M); var nC = Outward(cC.Face, M);
        if (Math.Abs(Vec3.Dot(ax, nP)) < 0.999) return (null, false, false);
        var uP = Vec3.Cross(nP, Tangent(cP, tm)).Normalized;
        var uC = Vec3.Cross(nC, Tangent(cC, tm)).Normalized;
        bool convex = Vec3.Dot(uC, nP) < 0;

        var rel = M - cs.Origin;
        var h0 = Vec3.Dot(rel, ax);
        var rho = (rel - ax * h0).Normalized;
        var uP2 = new Vec2(Math.Sign(Vec3.Dot(uP, rho)), 0);
        var uC2 = new Vec2(0, Math.Sign(Vec3.Dot(uC, ax)));
        var nP2 = new Vec2(0, Math.Sign(Vec3.Dot(nP, ax)));
        var nC2 = new Vec2(Math.Sign(Vec3.Dot(nC, rho)), 0);
        var (dP, dC) = fillet ? (size, size) : EdgeBlend.ChamferLegs(uP2, uC2, ch, refIsFirst(cP.Face, cC.Face));
        var prof = EdgeBlend.Profile2D(uP2, uC2, nP2, nC2, new Vec2(cs.R, h0), dP, fillet, convex, dC);
        if (prof == null) return (null, convex, false);
        if (prof.Verts.Any(v => v.P.X < -1e-9)) throw new InvalidOperationException("size too large for this edge");
        var (tlP, tlC) = TangentLengths(uP2, uC2, size, fillet, dP, dC);
        var tlc = Math.Max(tlP, tlC);
        var onPlane = new Vec2(cs.R, h0) + uP2 * tlP;   // (radius, height) of the tangent circle on the plane
        var onCyl = new Vec2(cs.R, h0) + uC2 * tlC;     // ... and on the cylinder

        var refDir = cs.F.XDir; var yDir = Vec3.Cross(ax, refDir);
        double Ang(Vec3 p) { var r = p - cs.Origin; return Math.Atan2(Vec3.Dot(r, yDir), Vec3.Dot(r, refDir)); }
        static double Wrap(double a) { a %= 2 * Math.PI; return a < 0 ? a + 2 * Math.PI : a; }
        // counter-clockwise (about the axis) range of each arc
        var ranges = new List<(double a0, double sweep)>();
        foreach (var arc in arcs)
        {
            var a0 = Ang(arc.V0.P); var sweep = Wrap(Ang(arc.V1.P) - a0);
            if (Wrap(Ang(arc.Mid) - a0) > sweep) { a0 = Ang(arc.V1.P); sweep = Wrap(Ang(arc.V0.P) - a0); }
            ranges.Add((a0, sweep));
        }
        bool fits = true;
        foreach (var (a0, sweep) in ranges)
            for (int i = 0; i <= 24 && fits; i++)
            {
                var a = a0 + sweep * i / 24;
                var dir = refDir * Math.Cos(a) + yDir * Math.Sin(a);
                fits = OnFaces(cP.Face, cs.Origin + ax * onPlane.Y + dir * onPlane.X) && OnFaces(cC.Face, cs.Origin + ax * onCyl.Y + dir * onCyl.X);
            }
        // chain ends meeting a face at an angle (e.g. a keyway wall across a bore): extensions past the ends, each
        // cut back to its face
        var extensions = new List<(double a0, double sweep, Plane plane)>();
        if (ranges.Sum(r => r.sweep) < 2 * Math.PI - 1e-6)
        {
            var delta = Math.Min(Math.Min(3 * Math.Max(size, tlc) / cs.R, Math.PI / 4), (2 * Math.PI - ranges.Sum(r => r.sweep)) / 2);
            static double Wrap2(double a) { a %= 2 * Math.PI; return a > Math.PI ? a - 2 * Math.PI : a < -Math.PI ? a + 2 * Math.PI : a; }
            Vec3 DirAt(double a) => -refDir * Math.Sin(a) + yDir * Math.Cos(a);
            for (int i = 0; i < arcs.Count; i++)
            {
                var arc = arcs[i]; var (a0, sweep) = ranges[i];
                var startV = Math.Abs(Wrap2(Ang(arc.V0.P) - a0)) < 1e-6 ? arc.V0 : arc.V1;
                var endV = ReferenceEquals(startV, arc.V0) ? arc.V1 : arc.V0;
                foreach (var atStart in new[] { true, false })
                {
                    var v = atStart ? startV : endV;
                    if (arcs.Count(o => ReferenceEquals(o.V0, v) || ReferenceEquals(o.V1, v)) > 1) continue; // joint inside the chain
                    var dirOut = atStart ? -DirAt(a0) : DirAt(a0 + sweep);
                    if (delta > 1e-9 && EndPlane(v, dirOut, cP.Face, cC.Face, arcs) is { } pl)
                        extensions.Add((atStart ? a0 - delta : a0 + sweep, delta, pl));
                }
            }
        }
        var tools = new List<Solid>();
        foreach (var (xa, xs, pl) in extensions)
        {
            var startDir = refDir * Math.Cos(xa) + yDir * Math.Sin(xa);
            var xf = new Frame(cs.Origin, startDir, ax, Vec3.Cross(startDir, ax));
            var piece = Trim(Builders.Revolve(new[] { new ProfileRegion(prof) }, xf, Vec2.Zero, new Vec2(0, 1), xs, name), pl);
            if (!piece.IsEmpty) tools.Add(piece);
        }
        if (ranges.Sum(r => r.sweep) > 2 * Math.PI - 1e-6)
        {
            var frame = new Frame(cs.Origin, refDir, ax, Vec3.Cross(refDir, ax));
            tools.Add(Builders.Revolve(new[] { new ProfileRegion(prof) }, frame, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, name));
            return (tools, convex, fits);
        }
        foreach (var (a0, sweep) in ranges)
        {
            if (sweep < 1e-9) continue;
            var startDir = refDir * Math.Cos(a0) + yDir * Math.Sin(a0);
            var pframe = new Frame(cs.Origin, startDir, ax, Vec3.Cross(startDir, ax));
            tools.Add(Builders.Revolve(new[] { new ProfileRegion(prof) }, pframe, Vec2.Zero, new Vec2(0, 1), sweep, name));
        }
        return (tools, convex, fits);
    }

    /// <summary>
    /// The plane of the face a blend's end vertex runs into, when that face is planar and meets the edge at an
    /// angle (a face square to the edge already matches the tool's square end). Oriented with the edge next to
    /// the vertex on its positive side. Null when there is no single such face.
    /// </summary>
    static Plane? EndPlane(NVertex v, Vec3 dirOut, NFace fa, NFace fb, IEnumerable<NEdge> blended)
    {
        var inside = v.P - dirOut.Normalized * 1e-3;
        Plane? found = null;
        foreach (var face in new[] { fa, fb })
            foreach (var c in face.Coedges)
            {
                var e = c.Edge;
                if (!ReferenceEquals(e.V0, v) && !ReferenceEquals(e.V1, v)) continue;
                if (blended.Contains(e) || IsSeam(e)) continue;
                foreach (var u in e.Uses)
                {
                    var f = u.Face;
                    if (ReferenceEquals(f, fa) || ReferenceEquals(f, fb)) continue;
                    if (f.Surface.CoincidentWith(fa.Surface, 1e-9) || f.Surface.CoincidentWith(fb.Surface, 1e-9)) continue;
                    if (f.Surface.Kind != SurfaceKind.Plane) return null;
                    var n = f.Surface.Plane!.Normal;
                    var cos = Math.Abs(Vec3.Dot(n, dirOut.Normalized));
                    if (cos > 0.9999 || cos < 0.05) return null; // square end, or a face along the edge
                    var pl = Plane.FromPointNormal(v.P, n);
                    if (pl.Distance(inside) < 0) pl = Plane.FromPointNormal(v.P, -n);
                    if (found != null && Math.Abs(Vec3.Dot(found.Normal, pl.Normal) - 1) > 1e-9) return null;
                    found = pl;
                }
            }
        return found;
    }

    /// <summary>The part of the tool on the positive side of the plane.</summary>
    static Solid Trim(Solid tool, Plane pl)
    {
        var box = tool.Bounds; var pad = box.Diagonal + 1;
        var n = pl.Normal; var origin = pl.Project(box.Center);
        var x = n.AnyPerpendicular(); var y = Vec3.Cross(n, x);
        var rect = ProfileLoop.Polygon(new[] { new Vec2(-pad, -pad), new Vec2(pad, -pad), new Vec2(pad, pad), new Vec2(-pad, pad) }, "trim");
        var slab = Builders.Extrude(new[] { new ProfileRegion(rect) }, new Frame(origin, x, y, n), -pad, 0, "trim");
        return Csg.Subtract(tool, slab);
    }

    /// <summary>Distances from the edge to where the fillet/chamfer meets face A and face B (as in the profile).</summary>
    static (double a, double b) TangentLengths(Vec2 uA, Vec2 uB, double size, bool fillet, double chamferA, double chamferB)
    {
        if (!fillet) return (chamferA, chamferB);
        var theta = Math.Acos(Math.Clamp(Vec2.Dot(uA.Normalized, uB.Normalized), -1, 1));
        var tl = size / Math.Tan(theta / 2);
        return (tl, tl);
    }

    /// <summary>
    /// The blend must end on the faces it blends (SolidWorks reports such fillets as not fitting): the tangent
    /// point has to lie on the face, or on another face of the body on the same surface (seam-split halves).
    /// </summary>
    /// <summary>Diagnostics of the overflow trimming (tests/tools).</summary>
    public static Action<string>? Debug;

    /// <summary>Use rolling-ball tools for every fillet (tests compare them with the exact profile tools).</summary>
    [ThreadStatic] public static bool ForceRollingBall;

    /// <summary>Verify that blends fit on their faces (off only for tests of the failure path).</summary>
    public static bool CheckFit = true;

    /// <summary>
    /// Whether a tangent point lies on the face it should (or on another face of the same surface, e.g. the
    /// other half of a seam-split cylinder). False means the blend runs past the face: it does not fit.
    /// </summary>
    static bool OnFaces(NFace face, Vec3 p)
    {
        if (!CheckFit) return true;
        const double tol = 1e-6;
        if (Fits(face, p, tol)) return true;
        foreach (var c in face.Coedges)
            foreach (var u in c.Edge.Uses)
                if (!ReferenceEquals(u.Face, face) && u.Face.Surface.CoincidentWith(face.Surface, 1e-9) && Fits(u.Face, p, tol)) return true;
        return false;
    }

    /// <summary>
    /// "Keep edge" overflow: a blend that runs into neighbouring geometry is trimmed by it. The tool is cut to
    /// where it acts (the open space next to a concave edge, the material at a convex edge); of the pieces left,
    /// only those touching the blended edge are kept (pieces beyond an obstacle are not part of the blend).
    /// Null if nothing sensible remains.
    /// </summary>
    static Solid? TrimToEdge(Solid body, List<Solid> tools, bool convex, List<NEdge> edges, NFace fa, NFace fb)
    {
        var tool = tools.Count == 1 ? tools[0] : Csg.UnionAll(tools);
        if (tool.Native == null) return null;
        // obstacles: faces (other than the two blended ones) the tool reaches through. The tool is trimmed by
        // the far side of each, never by the blended faces themselves: the blend surface is tangent to those,
        // and a boolean against them would be degenerate exactly where the obstacle cuts that tangency.
        var cutters = new List<Solid>();
        var box = tool.Bounds;
        var pad = box.Diagonal + 1;
        var samples = tool.Faces.SelectMany(f => f.Loops.SelectMany(l => l)).Distinct().ToList();
        var pts = edges.SelectMany(e => e.Samples(1e-3)).ToList();
        foreach (var f in body.Native!.Faces)
        {
            if (ReferenceEquals(f, fa) || ReferenceEquals(f, fb)) continue;
            if (f.Surface.CoincidentWith(fa.Surface, 1e-9) || f.Surface.CoincidentWith(fb.Surface, 1e-9)) continue;
            if (!f.Bounds().Overlaps(box, 1e-6)) continue;
            // the side the tool must not reach: the material behind the face (concave blends add material in
            // the open space) or the open space in front of it (convex blends remove material)
            double Side(Vec3 p) => f.Surface.F_(p) * (f.Sense ? 1 : -1) * (convex ? 1 : -1);
            var dom = NDomain.Of(f, 1e-3);
            // an obstacle stands in front of the edge (the edge is on its open side) and the tool reaches behind it
            if (pts.Any(p => Side(p) > 1e-6)) continue;
            if (!samples.Any(p => Side(p) > 1e-4 && dom.Classify(f.Surface.Project(p), 1e-4) >= 0)) continue;
            var cutter = FarSide(f, convex, box, pad);
            Debug?.Invoke($"obstacle {f.Tag.Name} {f.Surface.Kind} sense={f.Sense} cutter={(cutter == null ? "unsupported" : "ok")}");
            if (cutter == null) return null; // obstacle shape not supported: no overflow
            cutters.Add(cutter);
        }
        Solid part = tool;
        if (cutters.Count > 0) part = Csg.Subtract(tool, cutters.Count == 1 ? cutters[0] : Csg.UnionAll(cutters));
        Debug?.Invoke($"trimmed tool: native={part.Native != null} empty={part.IsEmpty} ({Csg.LastFallbackReason})");
        if (part.Native == null || part.IsEmpty) return null;
        var lumps = part.Native.Lumps();
        var kept = lumps.Where(l => Touches(l, pts)).ToList();
        Debug?.Invoke($"lumps {lumps.Count}, touching the edge {kept.Count}");
        if (kept.Count == 0) return null;
        var nb = new NBody();
        foreach (var l in kept) nb.Faces.AddRange(l.Faces);
        nb.Link();
        return NTess.ToSolid(nb);
    }

    /// <summary>
    /// Solid covering the side of face f the blend must not reach, around the tool's box: a slab behind/in front
    /// of a plane, or the solid cylinder of a boss (material inside) or hole (open space inside). Null otherwise.
    /// </summary>
    static Solid? FarSide(NFace f, bool convex, Box3 box, double pad)
    {
        var s = f.Surface;
        // outward normal times (concave: material side = behind, convex: open side = in front)
        var sign = (f.Sense ? 1 : -1) * (convex ? 1 : -1);
        if (s.Kind == SurfaceKind.Plane)
        {
            var n = s.Plane!.Normal;
            var origin = s.Plane.Project(box.Center);
            var x = n.AnyPerpendicular(); var y = Vec3.Cross(n, x);
            var frame = new Frame(origin, x, y, n);
            var rect = ProfileLoop.Polygon(new[] { new Vec2(-pad, -pad), new Vec2(pad, -pad), new Vec2(pad, pad), new Vec2(-pad, pad) }, "ov");
            // the slab lies on the side where F_ * sign > 0, i.e. along n * sign
            return sign > 0
                ? Builders.Extrude(new[] { new ProfileRegion(rect) }, frame, 0, pad, "overflow")
                : Builders.Extrude(new[] { new ProfileRegion(rect) }, frame, -pad, 0, "overflow");
        }
        if (s.Kind == SurfaceKind.Cylinder && sign < 0)
        {
            // the side to remove is inside the cylinder (F_ < 0 there)
            var ax = s.Axis;
            double h0 = double.MaxValue, h1 = double.MinValue;
            for (int c = 0; c < 8; c++)
            {
                var p = new Vec3((c & 1) == 0 ? box.Min.X : box.Max.X, (c & 2) == 0 ? box.Min.Y : box.Max.Y, (c & 4) == 0 ? box.Min.Z : box.Max.Z);
                var h = Vec3.Dot(p - s.Origin, ax);
                h0 = Math.Min(h0, h); h1 = Math.Max(h1, h);
            }
            return Builders.Cylinder(s.Origin + ax * (h0 - 1), ax, s.R, h1 - h0 + 2, "overflow", s.F.XDir);
        }
        return null;
    }

    /// <summary>Whether a piece of the tool belongs to the edge: some edge sample lies on or inside it (the
    /// tool of a concave blend reaches past the corner, so the edge runs through its interior).</summary>
    static bool Touches(NBody lump, List<Vec3> pts)
    {
        var box = lump.Bounds;
        var doms = new Dictionary<NFace, NDomain>(ReferenceEqualityComparer.Instance);
        foreach (var f in lump.Faces) doms[f] = NDomain.Of(f, 1e-3);
        foreach (var p in pts)
        {
            if (!box.Contains(p, 1e-4)) continue;
            foreach (var f in lump.Faces)
                if (Math.Abs(f.Surface.F_(p)) < 1e-5 && doms[f].Classify(f.Surface.Project(p), 1e-4) >= 0) return true;
            if (Inside(lump, doms, p)) return true;
        }
        return false;
    }

    static readonly Vec3[] ProbeDirs =
    {
        new Vec3(0.5773, 0.5774, 0.5775).Normalized, new Vec3(-0.3121, 0.8410, 0.4419).Normalized, new Vec3(0.7071, -0.1213, -0.6965).Normalized,
    };

    /// <summary>Whether p lies inside the body (ray parity on the exact faces).</summary>
    public static bool PointInside(NBody body, Vec3 p)
    {
        body.Link();
        var doms = new Dictionary<NFace, NDomain>(ReferenceEqualityComparer.Instance);
        foreach (var f in body.Faces) doms[f] = NDomain.Of(f, 1e-3);
        return Inside(body, doms, p);
    }

    /// <summary>Point-in-solid by ray parity; directions that graze a face boundary are skipped.</summary>
    static bool Inside(NBody body, Dictionary<NFace, NDomain> doms, Vec3 p)
    {
        foreach (var d in ProbeDirs)
        {
            int count = 0; bool ok = true;
            foreach (var f in body.Faces)
            {
                foreach (var t in f.Surface.RayHits(p, d))
                {
                    if (t <= 1e-9) continue;
                    var c = doms[f].Classify(p + d * t, 1e-6);
                    if (c == 0) { ok = false; break; }
                    if (c > 0) count++;
                }
                if (!ok) break;
            }
            if (ok) return count % 2 == 1;
        }
        return false;
    }

    static bool Fits(NFace f, Vec3 p, double tol)
    {
        var dom = NDomain.Of(f, 1e-3);
        return dom.Classify(f.Surface.Project(p), Math.Max(tol, 1e-4)) >= 0;
    }

    static bool SameCircle(NCircle a, NCircle b)
        => Vec3.Distance(a.C, b.C) < 1e-9 && Math.Abs(a.R - b.R) < 1e-9 && Math.Abs(Math.Abs(Vec3.Dot(a.Axis, b.Axis)) - 1) < 1e-12;

    static bool SameFaces(NEdge a, NEdge b)
    {
        var x = new[] { a.Uses[0].Face.Tag.Name, a.Uses[1].Face.Tag.Name }.OrderBy(s => s, StringComparer.Ordinal);
        var y = new[] { b.Uses[0].Face.Tag.Name, b.Uses[1].Face.Tag.Name }.OrderBy(s => s, StringComparer.Ordinal);
        return x.SequenceEqual(y);
    }

    /// <summary>A seam: both sides are the same named face on the same surface (an artifact of face splitting).</summary>
    public static bool IsSeam(NEdge e)
        => e.Uses.Count == 2 && e.Uses[0].Face.Tag.Name == e.Uses[1].Face.Tag.Name
           && e.Uses[0].Face.Surface.CoincidentWith(e.Uses[1].Face.Surface, 1e-9);

    /// <summary>
    /// Edges reachable from the seeds through vertices where the curves continue tangentially, plus the
    /// other arcs of seam-split circles (always: they are the same model edge).
    /// </summary>
    public static List<NEdge> Expand(NBody body, IEnumerable<NEdge> seeds, bool tangent)
    {
        body.Link();
        var at = new Dictionary<NVertex, List<NEdge>>(ReferenceEqualityComparer.Instance);
        foreach (var e in body.Edges)
        {
            if (IsSeam(e)) continue;
            foreach (var v in new[] { e.V0, e.V1 })
            {
                if (!at.TryGetValue(v, out var l)) at[v] = l = new();
                l.Add(e);
            }
        }
        var result = new List<NEdge>();
        var seen = new HashSet<NEdge>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<NEdge>();
        foreach (var s in seeds) if (seen.Add(s)) { result.Add(s); queue.Enqueue(s); }
        while (queue.Count > 0)
        {
            var e = queue.Dequeue();
            foreach (var atStart in new[] { true, false })
            {
                var v = atStart ? e.V0 : e.V1;
                var dirOut = atStart ? -e.Curve.Deriv(e.T0).Normalized : e.Curve.Deriv(e.T1).Normalized;
                if (!at.TryGetValue(v, out var others)) continue;
                foreach (var o in others)
                {
                    if (seen.Contains(o)) continue;
                    // the other pieces of a seam-split edge: same named faces on the same surfaces
                    bool sibling = SameFaces(e, o) && (e.Curve is NCircle ec && o.Curve is NCircle oc ? SameCircle(ec, oc) : RollingBall.SameSurfaces(e, o));
                    bool cont = false;
                    if (tangent && !sibling)
                        foreach (var oStart in new[] { true, false })
                        {
                            if (!ReferenceEquals(oStart ? o.V0 : o.V1, v)) continue;
                            var dirIn = oStart ? o.Curve.Deriv(o.T0).Normalized : -o.Curve.Deriv(o.T1).Normalized;
                            if (Vec3.Dot(dirOut, dirIn) > 0.9999) cont = true;
                        }
                    if (sibling || cont) { seen.Add(o); result.Add(o); queue.Enqueue(o); }
                }
            }
        }
        return result;
    }
}
