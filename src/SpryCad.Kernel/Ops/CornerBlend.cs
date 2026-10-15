using SpryCad.Kernel.Native;

namespace SpryCad.Kernel;

/// <summary>
/// The corner where three fillets of one radius meet at a vertex of three planes (a box's corner rounded all
/// round): the ball touching the three planes at once rolls out of each fillet into the corner, so the corner is a
/// patch of that ball (a spherical triangle) and each fillet stops at its cross-section through the ball's centre.
/// Built as direct blend tools: each edge's tool shortened to that cross-section, and the corner piece between them
/// (its faces on the three planes, its ends on the three cross-sections, and the sphere), which the direct blend
/// glues at those ends into one tool.
/// </summary>
static class CornerBlend
{
    static readonly ReferenceEqualityComparer Ref = ReferenceEqualityComparer.Instance;

    /// <summary>A fillet on a straight edge between two planes: the edge, whether it is convex, its tool's name.</summary>
    public sealed record Line(NEdge E, bool Convex, string Name);

    /// <summary>
    /// For each vertex where three of the lines meet and nothing else does: the corner piece; and for each line with
    /// such a corner at an end, its direct blend tool shortened there (by the line's edge). Lines with no such corner
    /// are not in <c>strips</c>.
    /// </summary>
    public static (Dictionary<NEdge, Solid> strips, List<(Solid piece, bool convex)> corners) Build(IReadOnlyList<Line> lines, double r, string prefix)
    {
        var strips = new Dictionary<NEdge, Solid>(Ref);
        var corners = new List<(Solid, bool)>();
        var at = new Dictionary<NVertex, List<Line>>(Ref);
        foreach (var l in lines)
            foreach (var v in new[] { l.E.V0, l.E.V1 })
            {
                if (!at.TryGetValue(v, out var list)) at[v] = list = new();
                list.Add(l);
            }
        // per line: how far from its start (V0) and its end (V1) the tool stops
        var cut = new Dictionary<NEdge, (double t0, double t1)>(Ref);
        int k = 0;
        foreach (var (v, ls) in at)
        {
            if (ls.Count != 3 || ls.Any(l => l.Convex != ls[0].Convex)) continue;
            // exactly three faces round the vertex, each line between two of them, nothing else there
            var faces = ls.SelectMany(l => l.E.Uses.Select(u => u.Face)).Distinct(Ref).Cast<NFace>().ToList();
            if (faces.Count != 3 || faces.Any(f => f.Surface.Kind != SurfaceKind.Plane)) continue;
            var round = faces.SelectMany(f => f.Coedges).Select(c => c.Edge).Where(e => ReferenceEquals(e.V0, v) || ReferenceEquals(e.V1, v)).Distinct(Ref).Count();
            if (round != 3) continue;
            var piece = Corner(v, ls, faces, r, ls[0].Convex, $"{prefix}.C{k++}", out var t);
            if (piece == null) continue;
            corners.Add((piece, ls[0].Convex));
            for (int i = 0; i < 3; i++)
            {
                var e = ls[i].E;
                var c = cut.GetValueOrDefault(e);
                cut[e] = ReferenceEquals(e.V0, v) ? (t[i], c.t1) : (c.t0, t[i]);
            }
        }
        var selected = lines.Select(l => l.E).ToList();
        foreach (var l in lines)
            if (cut.TryGetValue(l.E, out var c) && Strip(l, r, c.t0, c.t1, selected) is { } s) strips[l.E] = s;
        return (strips, corners);
    }

    static Vec3 Out(NFace f, Vec3 p) => f.Surface.Normal(p) * (f.Sense ? 1 : -1);

    /// <summary>
    /// The line's direct blend tool (as <see cref="NativeBlend"/>'s line tool builds it) from t0 past its start to t1
    /// short of its end. An end with no corner (t 0) ends as the line tool's does: on the face it runs into, where
    /// that meets the edge at an angle (square there, it stood past that face on one side, a wing beside the fillet).
    /// </summary>
    static Solid? Strip(Line l, double r, double t0, double t1, IReadOnlyCollection<NEdge> selected)
    {
        var e = l.E;
        var cA = e.Uses[0]; var cB = e.Uses[1];
        var E = cA.Start.P; var dir = cA.End.P - E; var L = dir.Length; var t = dir / L;
        // the line tool's frame runs from the first use's start: its V0 or V1
        var (a, b) = ReferenceEquals(cA.Start, e.V0) ? (t0, t1) : (t1, t0);
        if (a + b >= L - 1e-9 * Math.Max(1, L)) return null;
        var nA = Out(cA.Face, E); var nB = Out(cB.Face, E);
        var uA = Vec3.Cross(nA, t).Normalized;
        var uB = Vec3.Cross(nB, -t).Normalized;
        var x = uA; var y = Vec3.Cross(t, x).Normalized;
        Vec2 P(Vec3 v) => new(Vec3.Dot(v, x), Vec3.Dot(v, y));
        var prof = EdgeBlend.Profile2D(P(uA), P(uB), P(nA), P(nB), Vec2.Zero, r, true, l.Convex, r, atCorner: true);
        if (prof == null) return null;
        var frame = new Frame(E, x, y, t);
        Plane? End(NVertex v, Vec3 dirOut) => NativeBlend.EndPlane(v, dirOut, cA.Face, cB.Face, new[] { e }, selected);
        var p0 = a <= 0 ? End(cA.Start, -t) : null;
        var p1 = b <= 0 ? End(cA.End, t) : null;
        if ((p0 != null || p1 != null) && NBuild.ExtrudeBetween(new[] { new ProfileRegion(prof) }, frame, p0, a, p1, L - b, l.Name) is { } nb)
            return NTess.ToSolid(nb);
        return Builders.Extrude(new[] { new ProfileRegion(prof) }, frame, a, L - b, l.Name);
    }

    /// <summary>
    /// The corner piece at vertex v: the corner of the three planes cut off by the fillets' cross-sections through
    /// the ball's centre, less the ball. t: per line, the distance from v to its cross-section.
    /// </summary>
    static Solid? Corner(NVertex v, List<Line> ls, List<NFace> faces, double r, bool convex, string name, out double[] t)
    {
        t = new double[3];
        var V = v.P;
        var n = faces.Select(f => Out(f, V)).ToArray();
        // the ball's centre: r from each plane, on the material side of a convex corner, the open side of a concave one
        var rho = convex ? -r : r;
        var det = Vec3.Dot(n[0], Vec3.Cross(n[1], n[2]));
        if (Math.Abs(det) < 1e-6) return null;
        var S = V + (Vec3.Cross(n[1], n[2]) * rho + Vec3.Cross(n[2], n[0]) * rho + Vec3.Cross(n[0], n[1]) * rho) / det;
        var F = n.Select(ni => S - ni * rho).ToArray();   // the ball's feet on the planes
        // per line: its two faces (indices), its direction away from v, where its cross-section through S meets it
        var Es = new Vec3[3]; var pairs = new (int a, int b)[3]; var dirs = new Vec3[3];
        for (int i = 0; i < 3; i++)
        {
            var e = ls[i].E;
            pairs[i] = (faces.IndexOf(e.Uses[0].Face), faces.IndexOf(e.Uses[1].Face));
            var far = ReferenceEquals(e.V0, v) ? e.V1.P : e.V0.P;
            var L = Vec3.Distance(far, V);
            dirs[i] = (far - V) / L;
            t[i] = Vec3.Dot(dirs[i], S - V);
            if (t[i] <= 1e-9 * Math.Max(1, L) || t[i] >= L) return null;
            Es[i] = V + dirs[i] * t[i];
        }
        var scale = Math.Max(1, r);
        var vv = new NVertex(V);
        var ve = Es.Select(p => new NVertex(p)).ToArray();
        var vf = F.Select(p => new NVertex(p)).ToArray();
        NEdge Line(NVertex p, NVertex q) => new(new NLine(p.P, q.P - p.P), 0, Vec3.Distance(p.P, q.P), p, q);
        NEdge Arc(NVertex p, NVertex q, Vec3 axis)
        {
            var c = new NCircle(S, axis, p.P - S, r);
            var tb = c.Param(q.P);
            if (tb > Math.PI) { c = new NCircle(S, -axis, p.P - S, r); tb = c.Param(q.P); }
            return new NEdge(c, 0, tb, p, q);
        }
        // edges: v along each line to its cross-section; from there to the feet on its two planes; the arcs between
        // the feet in each cross-section (on the ball)
        var along = new NEdge[3]; var toFoot = new Dictionary<(int line, int face), NEdge>(); var arc = new NEdge[3];
        for (int i = 0; i < 3; i++)
        {
            along[i] = Line(vv, ve[i]);
            toFoot[(i, pairs[i].a)] = Line(ve[i], vf[pairs[i].a]);
            toFoot[(i, pairs[i].b)] = Line(ve[i], vf[pairs[i].b]);
            arc[i] = Arc(vf[pairs[i].a], vf[pairs[i].b], dirs[i]);
        }
        var body = new NBody();
        // the face, its loop oriented so that the outward normal is out (from a point inside the loop)
        void Add(NSurface s, Vec3 outward, FaceTag tag, List<(NEdge e, bool fwd)> loop, Vec3 inside, Vec3? anchor = null)
        {
            // the loop's turning: a point inside, and the polygon of the loop's points round it
            var pts = new List<Vec3>();
            foreach (var (e, fwd) in loop) { var a = fwd ? e.V0.P : e.V1.P; pts.Add(a); pts.Add(e.At(0.5)); }
            var area = Vec3.Zero;
            for (int i = 0; i < pts.Count; i++) area += Vec3.Cross(pts[i] - inside, pts[(i + 1) % pts.Count] - inside);
            if (Vec3.Dot(area, outward) < 0) loop = loop.Select(x => (x.e, !x.fwd)).Reverse().ToList();
            var f = new NFace(s, Vec3.Dot(s.Normal(inside), outward) > 0, tag, anchor);
            var nl = new NLoop();
            foreach (var (e, fwd) in loop) nl.Coedges.Add(new NCoedge(e, fwd));
            f.Loops.Add(nl);
            body.Faces.Add(f);
        }
        bool Fwd(NEdge e, NVertex from) => ReferenceEquals(e.V0, from);
        // on each plane: v, the cross-section point of one of its lines, the foot, the other line's point
        for (int fi = 0; fi < 3; fi++)
        {
            var mine = Enumerable.Range(0, 3).Where(i => pairs[i].a == fi || pairs[i].b == fi).ToList();
            if (mine.Count != 2) return null;
            int i0 = mine[0], i1 = mine[1];
            var loop = new List<(NEdge, bool)>
            {
                (along[i0], true), (toFoot[(i0, fi)], true), (toFoot[(i1, fi)], false), (along[i1], false),
            };
            var inside = (V + Es[i0] + F[fi] + Es[i1]) * 0.25;
            Add(NSurface.FromPlane(Plane.FromPointNormal(V, n[fi])), convex ? n[fi] : -n[fi], new FaceTag($"{name}.P{fi}", SurfaceDef.PlaneDef), loop, inside);
        }
        // the cross-sections: the line's point, its feet, the arc between them (outward away from v)
        for (int i = 0; i < 3; i++)
        {
            var (a, b) = pairs[i];
            var loop = new List<(NEdge, bool)> { (toFoot[(i, a)], true), (arc[i], Fwd(arc[i], vf[a])), (toFoot[(i, b)], false) };
            var inside = (Es[i] + F[a] + F[b]) / 3;
            Add(NSurface.FromPlane(Plane.FromPointNormal(Es[i], dirs[i])), dirs[i], new FaceTag($"{name}.Q{i}", SurfaceDef.PlaneDef), loop, inside);
        }
        // the ball's patch, outward towards its centre (the piece lies outside the ball); a frame with the patch on
        // the equator about its middle, clear of the poles and the seam
        {
            var mid = (F[0] + F[1] + F[2] - S * 3).Normalized;
            var axis = mid.AnyPerpendicular();
            var def = new SurfaceDef { Kind = SurfaceKind.Sphere, Origin = S, Axis = axis, RefDir = mid, Radius = r, Segments = SurfaceDef.SegmentsFor(r) };
            var sph = new NSurface(def);
            // the arcs head to head round the triangle F0 F1 F2
            var loop = new List<(NEdge, bool)>();
            var cur = vf[0];
            var left = new List<NEdge>(arc);
            while (left.Count > 0)
            {
                var nx = left.FirstOrDefault(e => ReferenceEquals(e.V0, cur) || ReferenceEquals(e.V1, cur));
                if (nx == null) return null;
                var fwd = ReferenceEquals(nx.V0, cur);
                loop.Add((nx, fwd));
                cur = fwd ? nx.V1 : nx.V0;
                left.Remove(nx);
            }
            var inside = S + mid * r;
            Add(sph, -mid, new FaceTag($"{name}.Blend", def), loop, inside, inside);
        }
        body.Link();
        if (body.Validate() != null) return null;
        return NTess.ToSolid(body);
    }
}
