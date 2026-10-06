namespace FeatherCad.Kernel;

/// <summary>Contiguous triangle range of one named (UI) face.</summary>
public sealed record FaceRange(string Name, int IndexStart, int IndexCount, SurfaceDef Surface, Plane FirstPlane, Box3 Bounds);

/// <summary>Contiguous line range of one model edge.</summary>
public sealed record EdgeRange(int EdgeIndex, int VertexStart, int VertexCount);

/// <summary>
/// GPU ready tessellation of a solid with face/edge ranges for highlighting and picking.
/// Curved faces are refined adaptively against their exact surfaces (and boundaries against the exact
/// intersection curves), so silhouettes are smooth while the model data stays compact.
/// </summary>
public sealed class DisplayMesh
{
    public float[] Positions = Array.Empty<float>();
    public float[] Normals = Array.Empty<float>();
    public int[] Indices = Array.Empty<int>();
    public List<FaceRange> Faces = new();
    /// <summary>Edge line vertices (pairs, xyz).</summary>
    public float[] EdgeLines = Array.Empty<float>();
    public List<EdgeRange> Edges = new();
    public Box3 Bounds = Box3.Empty;
    /// <summary>Volume of the refined tessellation (close to the exact volume).</summary>
    public double Volume;

    /// <summary>Maximum refinement levels for curved faces (each level halves chord length).</summary>
    public static int MaxLevels = 3;

    sealed class Refiner
    {
        public readonly List<Vec3> P;
        readonly Dictionary<(int, int), int> _mid = new();
        readonly Func<int, int, (ExactSurface? a, ExactSurface? b)> _surfacesOfEdge;
        readonly double _tol;

        public Refiner(List<Vec3> pts, Func<int, int, (ExactSurface?, ExactSurface?)> surfacesOfEdge, double tol)
        {
            P = pts; _surfacesOfEdge = surfacesOfEdge; _tol = tol;
        }

        /// <summary>Midpoint vertex of edge (a,b) projected onto the exact geometry, or -1 if no split is needed.</summary>
        public int Mid(int a, int b, ExactSurface? s1, ExactSurface? s2)
        {
            var key = a < b ? (a, b) : (b, a);
            if (_mid.TryGetValue(key, out var m)) return m;
            if (s1 == null && s2 == null) { _mid[key] = -1; return -1; }
            var mp = (P[a] + P[b]) * 0.5;
            Vec3 q;
            if (s1 != null && s2 != null && !ReferenceEquals(s1, s2)) q = ExactSurface.ProjectToIntersection(s1, s2, mp, 40);
            else q = (s1 ?? s2)!.Project(mp);
            if (Vec3.Distance(q, mp) < _tol || Vec3.Distance(q, mp) > Vec3.Distance(P[a], P[b])) { _mid[key] = -1; return -1; }
            m = P.Count; P.Add(q);
            _mid[key] = m;
            return m;
        }

        public int Known(int a, int b) => _mid.TryGetValue(a < b ? (a, b) : (b, a), out var m) ? m : -1;
    }

    public static DisplayMesh Build(Solid solid)
    {
        var m = new DisplayMesh();
        var ptopo = Prof.Time("mesh.topology");
        var topo = solid.Topology;
        ptopo.Dispose();
        var ptri = Prof.Time("mesh.triangulate");
        int nf = solid.Faces.Count;
        var scale = Math.Max(1, solid.Bounds.Diagonal);
        var tol = Math.Max(1e-4, scale * 2.5e-4);

        // exact surfaces per facet (curved only) and per-face exact plane
        var surf = new ExactSurface?[nf];
        var surfCache = new Dictionary<FaceTag, ExactSurface>(ReferenceEqualityComparer.Instance);
        for (int fi = 0; fi < nf; fi++)
        {
            var tag = solid.Faces[fi].Tag;
            if (!tag.Surface.IsCurved) continue;
            if (!surfCache.TryGetValue(tag, out var es)) surfCache[tag] = es = new ExactSurface(tag.Surface);
            surf[fi] = es;
        }
        var planeSurf = new ExactSurface[nf];
        ExactSurface PlaneOf(int fi) => planeSurf[fi] ??= new ExactSurface(SurfaceDef.PlaneDef, solid.Faces[fi].Plane);

        // base triangles (indices into topology vertices) per facet
        var pts = new List<Vec3>(topo.Vertices);
        // facets are independent: triangulate in parallel, then concatenate in facet order (deterministic)
        var perFacet = new List<(int a, int b, int c, int face)>[nf];
        Parallel.For(0, nf, fi =>
        {
            var loops = topo.FaceLoops[fi];
            var local = new List<(int, int, int, int)>();
            if (loops.Count == 1 && loops[0].Length == 3)
            {
                var l = loops[0];
                local.Add((l[0], l[1], l[2], fi)); // already a triangle (most facets of curved faces)
            }
            else
            {
                var loops2 = new List<Vec2[]>();
                var ids = new List<int>();
                var n = solid.Faces[fi].Plane.Normal;
                var u = n.AnyPerpendicular(); var v = Vec3.Cross(n, u);
                foreach (var l in loops)
                {
                    loops2.Add(l.Select(i => new Vec2(Vec3.Dot(pts[i], u), Vec3.Dot(pts[i], v))).ToArray());
                    ids.AddRange(l);
                }
                var idx = Triangulator.Triangulate(loops2);
                for (int k = 0; k < idx.Count; k += 3) local.Add((ids[idx[k]], ids[idx[k + 1]], ids[idx[k + 2]], fi));
            }
            perFacet[fi] = local;
        });
        var tris = new List<(int a, int b, int c, int face)>();
        foreach (var l in perFacet) tris.AddRange(l);

        ptri.Dispose();
        // which facets use each edge
        Dictionary<(int, int), List<int>> EdgeFaces(List<(int a, int b, int c, int face)> ts)
        {
            var map = new Dictionary<(int, int), List<int>>();
            void Add(int x, int y, int f)
            {
                var k = x < y ? (x, y) : (y, x);
                if (!map.TryGetValue(k, out var l)) map[k] = l = new List<int>(2);
                if (!l.Contains(f)) l.Add(f);
            }
            foreach (var t in ts) { Add(t.a, t.b, t.face); Add(t.b, t.c, t.face); Add(t.c, t.a, t.face); }
            return map;
        }

        // native solids are already tessellated against their exact surfaces (adaptive, finer than this
        // tolerance): re-refining would only re-project every curved edge to find nothing to split
        bool anyCurved = solid.Native == null && surf.Any(s => s != null);
        var refiner = new Refiner(pts, (_, _) => (null, null), tol);
        // boundary edges between faces: remember their exact surfaces for edge-line refinement
        for (int level = 0; anyCurved && level < MaxLevels; level++)
        {
            var ef = EdgeFaces(tris);
            (ExactSurface?, ExactSurface?) Surfaces(int x, int y)
            {
                var fs = ef[x < y ? (x, y) : (y, x)];
                ExactSurface? s1 = null, s2 = null; bool curved = false;
                foreach (var f in fs)
                {
                    var s = surf[f] ?? PlaneOf(f);
                    if (surf[f] != null) curved = true;
                    if (s1 == null) s1 = s;
                    else if (!ReferenceEquals(s1, s) && !(s1.Kind == SurfaceKind.Plane && s.Kind == SurfaceKind.Plane && s1.Plane!.IsCoplanar(s.Plane!))) s2 ??= s;
                }
                return curved ? (s1, s2) : (null, null);
            }
            var next = new List<(int, int, int, int)>(tris.Count * 2);
            bool changed = false;
            foreach (var (a, b, c, f) in tris)
            {
                int mab = Split(a, b), mbc = Split(b, c), mca = Split(c, a);
                int Split(int x, int y)
                {
                    var (s1, s2) = Surfaces(x, y);
                    return refiner.Mid(x, y, s1, s2);
                }
                int splits = (mab >= 0 ? 1 : 0) + (mbc >= 0 ? 1 : 0) + (mca >= 0 ? 1 : 0);
                if (splits == 0) { next.Add((a, b, c, f)); continue; }
                changed = true;
                if (splits == 3)
                {
                    next.Add((a, mab, mca, f)); next.Add((mab, b, mbc, f)); next.Add((mca, mbc, c, f)); next.Add((mab, mbc, mca, f));
                }
                else if (splits == 1)
                {
                    if (mab >= 0) { next.Add((a, mab, c, f)); next.Add((mab, b, c, f)); }
                    else if (mbc >= 0) { next.Add((a, b, mbc, f)); next.Add((a, mbc, c, f)); }
                    else { next.Add((a, b, mca, f)); next.Add((mca, b, c, f)); }
                }
                else
                {
                    if (mca < 0) { next.Add((mab, b, mbc, f)); next.Add((a, mab, mbc, f)); next.Add((a, mbc, c, f)); }
                    else if (mab < 0) { next.Add((mbc, c, mca, f)); next.Add((b, mbc, mca, f)); next.Add((b, mca, a, f)); }
                    else { next.Add((mca, a, mab, f)); next.Add((c, mca, mab, f)); next.Add((c, mab, b, f)); }
                }
            }
            tris = next;
            if (!changed) break;
        }

        // ---- emit, ordered by face name so each UI face is contiguous
        using var pemit = Prof.Time("mesh.emit");
        var pos = new List<float>(); var nrm = new List<float>(); var idxOut = new List<int>();
        var byFacet = tris.GroupBy(t => t.face).ToDictionary(g => g.Key, g => g.ToList());
        var order = Enumerable.Range(0, nf).OrderBy(i => solid.Faces[i].Tag.Name, StringComparer.Ordinal).ToList();
        // smooth normals of curved faces: once per vertex of each face (a vertex is shared by ~6 triangles), faces
        // in parallel (on free-form surfaces each normal is a closest-point search)
        var normals = new Dictionary<int, Vec3>[nf];
        Parallel.For(0, nf, fi =>
        {
            var f = solid.Faces[fi];
            if (!f.Tag.Surface.IsCurved || !byFacet.TryGetValue(fi, out var list)) return;
            var d = new Dictionary<int, Vec3>();
            var fn = f.Plane.Normal;
            foreach (var (a, b, c, _) in list)
                foreach (var k in new[] { a, b, c })
                    if (!d.ContainsKey(k)) d[k] = f.Tag.Surface.NormalAt(pts[k], fn);
            normals[fi] = d;
        });
        string? curName = null; int curStart = 0; SurfaceDef? curSurf = null; Plane? curPlane = null; var curBox = Box3.Empty;
        void Flush()
        {
            if (curName != null && idxOut.Count > curStart)
                m.Faces.Add(new FaceRange(curName, curStart, idxOut.Count - curStart, curSurf!, curPlane!, curBox));
        }
        double vol = 0;
        foreach (var fi in order)
        {
            var f = solid.Faces[fi];
            if (f.Tag.Name != curName)
            {
                Flush();
                curName = f.Tag.Name; curStart = idxOut.Count; curSurf = f.Tag.Surface; curPlane = f.Plane; curBox = Box3.Empty;
            }
            if (!byFacet.TryGetValue(fi, out var list)) continue;
            var fn = f.Plane.Normal;
            var curved = f.Tag.Surface.IsCurved;
            var fnorm = normals[fi];
            foreach (var (a, b, c, _) in list)
            {
                var pa = pts[a]; var pb = pts[b]; var pc = pts[c];
                vol += Vec3.Dot(pa, Vec3.Cross(pb, pc));
                foreach (var k in new[] { a, b, c })
                {
                    var p = pts[k];
                    idxOut.Add(pos.Count / 3);
                    pos.Add((float)p.X); pos.Add((float)p.Y); pos.Add((float)p.Z);
                    var n = curved && fnorm != null ? fnorm[k] : fn;
                    nrm.Add((float)n.X); nrm.Add((float)n.Y); nrm.Add((float)n.Z);
                    curBox.Add(p);
                    m.Bounds.Add(p);
                }
            }
        }
        Flush();
        m.Volume = vol / 6;
        m.Positions = pos.ToArray(); m.Normals = nrm.ToArray(); m.Indices = idxOut.ToArray();

        // ---- edge lines, refined with the same midpoints as the surfaces
        var lines = new List<float>();
        foreach (var e in topo.Edges)
        {
            int start = lines.Count / 3;
            foreach (var s in e.Segs)
            {
                var poly = new List<int> { s.VA, s.VB };
                for (int level = 0; anyCurved && level < MaxLevels; level++)
                {
                    var np = new List<int> { poly[0] };
                    bool any = false;
                    for (int i = 0; i + 1 < poly.Count; i++)
                    {
                        var mid = refiner.Known(poly[i], poly[i + 1]);
                        if (mid >= 0) { np.Add(mid); any = true; }
                        np.Add(poly[i + 1]);
                    }
                    poly = np;
                    if (!any) break;
                }
                for (int i = 0; i + 1 < poly.Count; i++)
                {
                    var a = pts[poly[i]]; var b = pts[poly[i + 1]];
                    lines.Add((float)a.X); lines.Add((float)a.Y); lines.Add((float)a.Z);
                    lines.Add((float)b.X); lines.Add((float)b.Y); lines.Add((float)b.Z);
                }
            }
            m.Edges.Add(new EdgeRange(e.Index, start, lines.Count / 3 - start));
        }
        m.EdgeLines = lines.ToArray();
        return m;
    }
}
