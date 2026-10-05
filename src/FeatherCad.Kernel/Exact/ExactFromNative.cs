using FeatherCad.Kernel.Native;

namespace FeatherCad.Kernel;

public sealed partial class ExactBrepFactory
{
    /// <summary>
    /// Exact B-rep straight from a native body: seam-split faces of one surface are merged back into a
    /// single face, and arcs of the same circle are rejoined into full circles / single arcs.
    /// </summary>
    public static ExactBrep FromNative(NBody body, double tol = 1e-6)
    {
        body.Link();
        var faces = body.Faces;
        int nf = faces.Count;
        var index = new Dictionary<NFace, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < nf; i++) index[faces[i]] = i;

        // ---- merge faces of the same surface across seam edges
        var parent = Enumerable.Range(0, nf).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var e in body.Edges)
        {
            if (e.Uses.Count != 2) continue;
            var f = e.Uses[0].Face; var g = e.Uses[1].Face;
            if (ReferenceEquals(f, g) || f.Sense != g.Sense || f.Tag.Name != g.Tag.Name) continue;
            if (!ReferenceEquals(f.Surface, g.Surface) && !f.Surface.CoincidentWith(g.Surface, tol * 10)) continue;
            parent[Find(index[f])] = Find(index[g]);
        }
        // closed surfaces (e.g. two hemispheres) must stay split: a face needs at least one boundary loop
        foreach (var grpIdx in Enumerable.Range(0, nf).GroupBy(Find).Where(g => g.Count() > 1).ToList())
        {
            var set = new HashSet<NFace>(grpIdx.Select(i => faces[i]), ReferenceEqualityComparer.Instance);
            bool hasBoundary = set.SelectMany(f => f.Coedges).Any(c => !c.Edge.Uses.All(u => set.Contains(u.Face)));
            if (!hasBoundary) foreach (var i in grpIdx) parent[i] = i;
        }
        var groupOf = new int[nf];
        var groups = new List<List<NFace>>();
        var gid = new Dictionary<int, int>();
        for (int i = 0; i < nf; i++)
        {
            var r = Find(i);
            if (!gid.TryGetValue(r, out var g)) { g = groups.Count; gid[r] = g; groups.Add(new()); }
            groups[g].Add(faces[i]);
            groupOf[i] = g;
        }

        var b = new ExactBrep();
        var vIndex = new Dictionary<NVertex, int>(ReferenceEqualityComparer.Instance);
        int V(NVertex v)
        {
            if (vIndex.TryGetValue(v, out var i)) return i;
            i = b.Vertices.Count; b.Vertices.Add(v.P); vIndex[v] = i; return i;
        }

        // exact faces
        foreach (var grp in groups)
        {
            var f0 = grp[0];
            var surf = f0.Surface.Kind == SurfaceKind.Plane ? new ExactSurface(SurfaceDef.PlaneDef, f0.Surface.Plane!) : new ExactSurface(f0.Surface.Def);
            b.Faces.Add(new ExactFace { Index = b.Faces.Count, Name = f0.Tag.Name, Surface = surf, SameSense = f0.Sense });
        }

        // boundary coedges per group (edges whose two uses are in different groups)
        var edgeMap = new Dictionary<NEdge, (int index, bool forward)>(ReferenceEqualityComparer.Instance);
        foreach (var (grp, gi) in groups.Select((g, i) => (g, i)))
        {
            var set = new HashSet<NFace>(grp, ReferenceEqualityComparer.Instance);
            var bnd = grp.SelectMany(f => f.Coedges).Where(c => !c.Edge.Uses.All(u => set.Contains(u.Face))).ToList();
            // chain into loops
            var byStart = bnd.GroupBy(c => c.Start, ReferenceEqualityComparer.Instance).ToDictionary(x => (NVertex)x.Key!, x => x.ToList(), ReferenceEqualityComparer.Instance);
            var used = new HashSet<NCoedge>(ReferenceEqualityComparer.Instance);
            foreach (var c0 in bnd)
            {
                if (used.Contains(c0)) continue;
                var chain = new List<NCoedge>();
                var c = c0;
                while (c != null && used.Add(c))
                {
                    chain.Add(c);
                    if (ReferenceEquals(c.End, c0.Start)) break;
                    c = byStart.TryGetValue(c.End, out var nx) ? nx.FirstOrDefault(x => !used.Contains(x)) : null;
                }
                var loop = new ExactLoop();
                foreach (var run in JoinRuns(chain, groupOf, index))
                {
                    var (edgeIdx, fwd) = GetEdge(run);
                    loop.Edges.Add((edgeIdx, fwd));
                    var ee = b.Edges[edgeIdx];
                    if (fwd) ee.FaceA = gi; else ee.FaceB = gi;
                }
                b.Faces[gi].Loops.Add(loop);
            }
        }

        // lumps
        var lp = Enumerable.Range(0, b.Faces.Count).ToArray();
        int LF(int x) { while (lp[x] != x) x = lp[x] = lp[lp[x]]; return x; }
        foreach (var e in b.Edges) if (e.FaceA >= 0 && e.FaceB >= 0) lp[LF(e.FaceA)] = LF(e.FaceB);
        var lumps = new Dictionary<int, int>();
        foreach (var f in b.Faces) { var r = LF(f.Index); if (!lumps.TryGetValue(r, out var l)) lumps[r] = l = lumps.Count; f.Lump = l; }
        b.SetLumpCount(lumps.Count);
        foreach (var e in b.Edges) if (e.FaceA < 0 || e.FaceB < 0) { b.Warnings.Add("open edges"); break; }
        return b;

        // one exact edge per run of native coedges; the creating coedge's native edge and direction decide
        // whether a later (opposite) face uses it forward or reversed
        (int edge, bool fwd) GetEdge(List<NCoedge> run)
        {
            foreach (var c in run)
                if (edgeMap.TryGetValue(c.Edge, out var known))
                    return (known.index, c.Forward == known.forward);
            var start = run[0].Start; var end = run[^1].End;
            var pts = new List<Vec3>();
            foreach (var c in run) { var p = c.Points(1e-3); pts.AddRange(pts.Count == 0 ? p : p.Skip(1)); }
            ExactCurve curve = ToExact(run, pts);
            var ex = new ExactEdge { Curve = curve, V0 = V(start), V1 = V(end), Points = pts, Index = b.Edges.Count };
            b.Edges.Add(ex);
            foreach (var c in run) edgeMap[c.Edge] = (ex.Index, c.Forward);
            return (ex.Index, true);
        }
    }

    static ExactCurve ToExact(List<NCoedge> run, List<Vec3> pts)
    {
        var e = run[0].Edge;
        switch (e.Curve)
        {
            case NLine l:
                {
                    var d = (pts[^1] - pts[0]);
                    return new LineCurve(pts[0], d.Length > 0 ? d.Normalized : l.D);
                }
            case NCircle c:
                {
                    // the run follows the circle in the coedge direction: orient the axis accordingly
                    var axis = run[0].Forward ? c.Axis : -c.Axis;
                    return new CircleCurve(c.C, axis, (pts[0] - c.C).Normalized, c.R);
                }
            case NEllipse el:
                {
                    var axis = Vec3.Cross(el.X, el.Y);
                    if (!run[0].Forward) axis = -axis;
                    // keep the major axis but flip the frame handedness with the direction
                    var x = el.X;
                    return new EllipseCurve(el.C, axis, x, el.A, el.B);
                }
            default:
                {
                    var clean = new List<Vec3> { pts[0] };
                    foreach (var p in pts.Skip(1)) if (Vec3.Distance(p, clean[^1]) > 1e-9) clean.Add(p);
                    if (clean.Count < 2) clean.Add(pts[^1]);
                    if (e.Curve is NSpline sp && run.Count == 1)
                        return run[0].Forward ? sp.S : BSplineCurve.Interpolate(clean, 3);
                    return BSplineCurve.Interpolate(clean, 3);
                }
        }
    }

    /// <summary>Groups consecutive coedges that continue the same circle/line between the same two faces.</summary>
    static IEnumerable<List<NCoedge>> JoinRuns(List<NCoedge> chain, int[] groupOf, Dictionary<NFace, int> index)
    {
        int Other(NCoedge c)
        {
            var o = c.Edge.Uses.FirstOrDefault(u => !ReferenceEquals(u, c) && !ReferenceEquals(u.Face, c.Face)) ?? c.Edge.Uses.First(u => !ReferenceEquals(u, c));
            return groupOf[index[o.Face]];
        }
        bool Same(NCoedge a, NCoedge b)
        {
            if (Other(a) != Other(b)) return false;
            if (a.Edge.Curve is NCircle ca && b.Edge.Curve is NCircle cb)
                return Vec3.Distance(ca.C, cb.C) < 1e-9 && Math.Abs(ca.R - cb.R) < 1e-9 && Math.Abs(Math.Abs(Vec3.Dot(ca.Axis, cb.Axis)) - 1) < 1e-12;
            if (a.Edge.Curve is NLine la && b.Edge.Curve is NLine lb)
            {
                var d = (a.End.P - a.Start.P).Normalized; var d2 = (b.End.P - b.Start.P).Normalized;
                return Vec3.Dot(d, d2) > 1 - 1e-12;
            }
            return false;
        }
        int n = chain.Count;
        if (n == 0) yield break;
        // start at a break so runs do not wrap
        int start = 0;
        for (int i = 0; i < n; i++) if (!Same(chain[(i - 1 + n) % n], chain[i])) { start = i; break; }
        bool allSame = Enumerable.Range(0, n).All(i => Same(chain[(i - 1 + n) % n], chain[i]));
        var cur = new List<NCoedge>();
        for (int k = 0; k < n; k++)
        {
            var c = chain[(start + k) % n];
            if (cur.Count > 0 && !Same(cur[^1], c)) { yield return cur; cur = new(); }
            cur.Add(c);
        }
        if (cur.Count > 0) yield return cur;
        _ = allSame;
    }
}
