using FeatherCad.Kernel.Native;

namespace FeatherCad.Kernel;

/// <summary>
/// Fillets and chamfers on native B-rep edges: tools are built from the exact edge curve and the adjacent
/// faces' surfaces (no tessellation), then subtracted (convex) or added (concave) with native booleans.
/// Supported: lines between planes, and arcs between a plane and a perpendicular cylinder.
/// </summary>
public static class NativeBlend
{
    public static Solid Apply(Solid body, IReadOnlyList<NEdge> edges, double size, bool fillet, string prefix, List<string> errors)
    {
        if (size <= 0) { errors.Add("Size must be greater than zero."); return body; }
        body.Native!.Link();
        var cutTools = new List<Solid>();
        var addTools = new List<Solid>();
        var done = new HashSet<NEdge>(ReferenceEqualityComparer.Instance);
        int i = 0;
        foreach (var e in edges)
        {
            if (!done.Add(e)) continue;
            var name = $"{prefix}.{i++}";
            var (fa, fb) = Faces(e);
            try
            {
                if (e.Curve is NCircle c)
                {
                    // arcs of one circle between the same faces form one rim (bodies split full circles)
                    var group = new List<NEdge> { e };
                    foreach (var o in edges)
                        if (!done.Contains(o) && o.Curve is NCircle oc && SameCircle(c, oc) && SameFaces(e, o)) { group.Add(o); done.Add(o); }
                    var (tools, convex) = ArcTools(group, size, fillet, name);
                    if (tools == null) { errors.Add($"Edge {fa.Tag.Name} / {fb.Tag.Name}: unsupported edge geometry."); continue; }
                    (convex ? cutTools : addTools).AddRange(tools);
                }
                else
                {
                    var (tool, convex) = LineTool(e, size, fillet, name);
                    if (tool == null) { errors.Add($"Edge {fa.Tag.Name} / {fb.Tag.Name}: unsupported edge geometry."); continue; }
                    (convex ? cutTools : addTools).Add(tool);
                }
            }
            catch (Exception ex) { errors.Add($"Edge {fa.Tag.Name} / {fb.Tag.Name}: {ex.Message}"); }
        }
        var result = body;
        if (cutTools.Count > 0) result = Csg.Subtract(result, Csg.UnionAll(cutTools));
        if (addTools.Count > 0) result = Csg.Union(result, Csg.UnionAll(addTools));
        return result;
    }

    static (NFace a, NFace b) Faces(NEdge e) => (e.Uses[0].Face, e.Uses[1].Face);

    static Vec3 Outward(NFace f, Vec3 p) => f.Surface.Normal(p) * (f.Sense ? 1 : -1);

    /// <summary>Direction of the edge as traversed by the coedge, at curve parameter t.</summary>
    static Vec3 Tangent(NCoedge c, double t) => (c.Edge.Curve.Deriv(t) * (c.Forward ? 1 : -1)).Normalized;

    static (Solid? tool, bool convex) LineTool(NEdge e, double size, bool fillet, string name)
    {
        var cA = e.Uses[0]; var cB = e.Uses[1];
        if (e.Curve is not NLine || cA.Face.Surface.Kind != SurfaceKind.Plane || cB.Face.Surface.Kind != SurfaceKind.Plane) return (null, false);
        var E = cA.Start.P; var dir = cA.End.P - E; var L = dir.Length; var t = dir / L;
        var nA = Outward(cA.Face, E); var nB = Outward(cB.Face, E);
        // faces lie to the left of their loops (seen from outside): n × direction points into the face
        var uA = Vec3.Cross(nA, t).Normalized;
        var uB = Vec3.Cross(nB, -t).Normalized;
        bool convex = Vec3.Dot(uB, nA) < 0;
        var x = uA; var y = Vec3.Cross(t, x).Normalized;
        var lframe = new Frame(E, x, y, t);
        Vec2 P(Vec3 v) => new(Vec3.Dot(v, x), Vec3.Dot(v, y));
        var loop = EdgeBlend.Profile2D(P(uA), P(uB), P(nA), P(nB), Vec2.Zero, size, fillet, convex);
        if (loop == null) return (null, convex);
        return (Builders.Extrude(new[] { new ProfileRegion(loop) }, lframe, 0, L, name), convex);
    }

    static (List<Solid>? tools, bool convex) ArcTools(List<NEdge> arcs, double size, bool fillet, string name)
    {
        var e = arcs[0];
        var cP = e.Uses[0]; var cC = e.Uses[1];
        if (cP.Face.Surface.Kind != SurfaceKind.Plane) (cP, cC) = (cC, cP);
        if (cP.Face.Surface.Kind != SurfaceKind.Plane || cC.Face.Surface.Kind != SurfaceKind.Cylinder) return (null, false);
        var cs = cC.Face.Surface;
        var ax = cs.Axis;
        var tm = 0.5 * (e.T0 + e.T1);
        var M = e.Curve.Eval(tm);
        var nP = Outward(cP.Face, M); var nC = Outward(cC.Face, M);
        if (Math.Abs(Vec3.Dot(ax, nP)) < 0.999) return (null, false);
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
        var prof = EdgeBlend.Profile2D(uP2, uC2, nP2, nC2, new Vec2(cs.R, h0), size, fillet, convex);
        if (prof == null) return (null, convex);
        if (prof.Verts.Any(v => v.P.X < -1e-9)) throw new InvalidOperationException("size too large for this edge");

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
        var tools = new List<Solid>();
        if (ranges.Sum(r => r.sweep) > 2 * Math.PI - 1e-6)
        {
            var frame = new Frame(cs.Origin, refDir, ax, Vec3.Cross(refDir, ax));
            tools.Add(Builders.Revolve(new[] { new ProfileRegion(prof) }, frame, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, name));
            return (tools, convex);
        }
        int k = 0;
        foreach (var (a0, sweep) in ranges)
        {
            if (sweep < 1e-9) continue;
            var startDir = refDir * Math.Cos(a0) + yDir * Math.Sin(a0);
            var pframe = new Frame(cs.Origin, startDir, ax, Vec3.Cross(startDir, ax));
            tools.Add(Builders.Revolve(new[] { new ProfileRegion(prof) }, pframe, Vec2.Zero, new Vec2(0, 1), sweep, ranges.Count == 1 ? name : $"{name}.{k++}"));
        }
        return (tools, convex);
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
                    bool sibling = e.Curve is NCircle ec && o.Curve is NCircle oc && SameCircle(ec, oc) && SameFaces(e, o);
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
