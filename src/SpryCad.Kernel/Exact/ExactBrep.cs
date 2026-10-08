namespace SpryCad.Kernel;

public sealed class ExactEdge
{
    public int Index { get; internal set; }
    public required ExactCurve Curve { get; init; }
    public required int V0 { get; init; }
    public required int V1 { get; init; }
    public bool Closed => V0 == V1;
    /// <summary>Points along the edge from V0 to V1 (facet vertices, projected onto the exact curve).</summary>
    public required List<Vec3> Points { get; init; }
    /// <summary>Face using the edge in its forward direction / reversed direction (-1 if none).</summary>
    public int FaceA { get; internal set; } = -1;
    public int FaceB { get; internal set; } = -1;
}

public sealed class ExactLoop
{
    public List<(int Edge, bool Forward)> Edges { get; } = new();
}

public sealed class ExactFace
{
    public int Index { get; internal set; }
    public required string Name { get; init; }
    public required ExactSurface Surface { get; init; }
    /// <summary>True when the face's outward normal agrees with the surface's natural normal.</summary>
    public bool SameSense { get; internal set; } = true;
    public List<int> Facets { get; } = new();
    public List<ExactLoop> Loops { get; } = new();
    public int Lump { get; internal set; }
}

/// <summary>
/// Exact boundary representation rebuilt from a solid: facets that belong to the same analytic surface
/// are merged into one face with its true surface (plane, cylinder, cone, sphere, torus); boundaries
/// become exact lines and circles, and general intersection curves become cubic B-splines through
/// points projected onto both surfaces.
/// </summary>
public sealed class ExactBrep
{
    public List<Vec3> Vertices { get; } = new();
    public List<ExactEdge> Edges { get; } = new();
    public List<ExactFace> Faces { get; } = new();
    public int LumpCount { get; private set; }
    internal void SetLumpCount(int n) => LumpCount = n;
    public List<string> Warnings { get; } = new();

    public int Count(SurfaceKind k) => Faces.Count(f => f.Surface.Kind == k);

    public static ExactBrep Build(Solid solid)
    {
        if (solid.Native != null)
        {
            try
            {
                var nb = ExactBrepFactory.FromNative(solid.Native);
                if (nb.Warnings.Count == 0) return nb;
            }
            catch (Exception) { /* fall back to reconstruction from facets */ }
        }
        var b = new ExactBrep();
        if (solid.IsEmpty) return b;
        var topo = solid.Topology;
        var faces = solid.Faces;
        int nf = faces.Count;
        var scale = Math.Max(1, solid.Bounds.Diagonal);
        double tol = 1e-7 * scale;

        // ---- 1. half-edge owners
        var owner = new Dictionary<(int, int), int>();
        for (int fi = 0; fi < nf; fi++)
            foreach (var l in topo.FaceLoops[fi])
                for (int i = 0; i < l.Length; i++) owner.TryAdd((l[i], l[(i + 1) % l.Length]), fi);

        // ---- 2. group facets of the same analytic surface (connected, same name)
        var parent = Enumerable.Range(0, nf).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var ((a, c), f) in owner)
        {
            if (!owner.TryGetValue((c, a), out var g) || g == f) continue;
            var ff = faces[f]; var fg = faces[g];
            if (ff.Tag.Name != fg.Tag.Name) continue;
            if (!ff.Tag.Surface.IsCurved && !ff.Plane.IsCoplanar(fg.Plane, 1e-9, tol * 10)) continue;
            if (ff.Tag.Surface.IsCurved != fg.Tag.Surface.IsCurved) continue;
            parent[Find(f)] = Find(g);
        }
        var groupOf = new int[nf];
        var groupIndex = new Dictionary<int, int>();
        for (int fi = 0; fi < nf; fi++)
        {
            var r = Find(fi);
            if (!groupIndex.TryGetValue(r, out var gi))
            {
                gi = b.Faces.Count;
                groupIndex[r] = gi;
                var f = faces[fi];
                var surf = f.Tag.Surface.IsCurved ? new ExactSurface(f.Tag.Surface) : new ExactSurface(SurfaceDef.PlaneDef, f.Plane);
                b.Faces.Add(new ExactFace { Index = gi, Name = f.Tag.Name, Surface = surf });
            }
            groupOf[fi] = gi;
            b.Faces[gi].Facets.Add(fi);
        }

        // orientation of each exact face relative to its surface
        foreach (var ef in b.Faces)
        {
            if (ef.Surface.Kind == SurfaceKind.Plane) { ef.SameSense = true; continue; }
            double vote = 0;
            foreach (var fi in ef.Facets)
            {
                var f = faces[fi];
                var c = ef.Surface.Project(f.Centroid());
                vote += Vec3.Dot(ef.Surface.NaturalNormal(c), f.Plane.Normal) * Math.Abs(f.Area());
            }
            ef.SameSense = vote >= 0;
        }

        // ---- 3. vertices: which groups touch each topology vertex
        var vertGroups = new Dictionary<int, HashSet<int>>();
        for (int fi = 0; fi < nf; fi++)
            foreach (var l in topo.FaceLoops[fi])
                foreach (var v in l)
                {
                    if (!vertGroups.TryGetValue(v, out var set)) vertGroups[v] = set = new HashSet<int>();
                    set.Add(groupOf[fi]);
                }

        // ---- 4. boundary half-edges per group
        var boundary = new Dictionary<int, Dictionary<int, List<(int to, int nbr)>>>();
        for (int fi = 0; fi < nf; fi++)
        {
            var g = groupOf[fi];
            foreach (var l in topo.FaceLoops[fi])
                for (int i = 0; i < l.Length; i++)
                {
                    int a = l[i], c = l[(i + 1) % l.Length];
                    int nbr = owner.TryGetValue((c, a), out var tf) ? groupOf[tf] : -1;
                    if (nbr == g) continue;
                    if (!boundary.TryGetValue(g, out var m)) boundary[g] = m = new Dictionary<int, List<(int, int)>>();
                    if (!m.TryGetValue(a, out var outs)) m[a] = outs = new List<(int, int)>(1);
                    outs.Add((c, nbr));
                }
        }

        // projected (exact) vertex positions, computed lazily
        var vtxMap = new Dictionary<int, int>();
        int VertexId(int v)
        {
            if (vtxMap.TryGetValue(v, out var id)) return id;
            var p = topo.Vertices[v];
            var surfs = vertGroups.TryGetValue(v, out var gs) ? gs.Select(g => b.Faces[g].Surface).ToList() : new();
            if (surfs.Count >= 2)
            {
                var q = p;
                for (int it = 0; it < 100; it++)
                {
                    var prev = q;
                    foreach (var s in surfs) q = s.Project(q);
                    if ((q - prev).LengthSq < 1e-24) break;
                }
                if ((q - p).Length < 1e-3 * scale && surfs.All(s => s.Distance(q) < 1e-6 * scale)) p = q;
            }
            id = b.Vertices.Count;
            b.Vertices.Add(p);
            vtxMap[v] = id;
            return id;
        }

        // ---- 5. loops -> runs -> edges
        var edgeOfHalf = new Dictionary<(int, int), (int edge, bool forward)>();
        foreach (var (g, outMap) in boundary.OrderBy(k => k.Key))
        {
            var face = b.Faces[g];
            var used = new HashSet<(int, int)>();
            foreach (var start in outMap.Keys.OrderBy(k => k).ToList())
            {
                foreach (var (firstTo, _) in outMap[start].ToList())
                {
                    if (used.Contains((start, firstTo))) continue;
                    // trace a loop
                    var segs = new List<(int a, int c, int nbr)>();
                    int cur = start, nxt = firstTo;
                    int guard = 0;
                    while (guard++ < 1_000_000)
                    {
                        var nbr = outMap[cur].First(o => o.to == nxt).nbr;
                        used.Add((cur, nxt));
                        segs.Add((cur, nxt, nbr));
                        if (nxt == start) break;
                        if (!outMap.TryGetValue(nxt, out var outs)) break;
                        var cand = outs.Where(o => !used.Contains((nxt, o.to))).ToList();
                        if (cand.Count == 0) break;
                        var pickIdx = cand.FindIndex(o => o.nbr == nbr);
                        cur = nxt;
                        nxt = pickIdx >= 0 ? cand[pickIdx].to : cand[0].to;
                    }
                    if (segs.Count == 0) continue;
                    face.Loops.Add(BuildLoop(b, face, segs, topo, edgeOfHalf, vertGroups, VertexId, tol));
                }
            }
        }

        // ---- 5b. closed surfaces without boundary (full spheres): split at the equator
        foreach (var f in b.Faces.ToList())
        {
            if (f.Loops.Count > 0) continue;
            if (f.Surface.Kind != SurfaceKind.Sphere) { b.Warnings.Add($"face {f.Name} has no boundary"); continue; }
            var d = f.Surface.Def;
            var ax = d.Axis.Normalized;
            var refd = d.RefDir.LengthSq > 0.5 ? d.RefDir.Normalized : ax.AnyPerpendicular();
            var circle = new CircleCurve(d.Origin, ax, refd, d.Radius);
            var vi = b.Vertices.Count;
            b.Vertices.Add(circle.At(0));
            var pts = circle.Sample(circle.At(0), circle.At(0), true, d.Radius * 1e-3).ToList();
            var edge = new ExactEdge { Curve = circle, V0 = vi, V1 = vi, Points = pts, Index = b.Edges.Count };
            b.Edges.Add(edge);
            var lower = new ExactFace { Index = b.Faces.Count, Name = f.Name + ".2", Surface = f.Surface, SameSense = f.SameSense };
            b.Faces.Add(lower);
            // upper cap: CCW about its outward normal at the pole
            var upperLoop = new ExactLoop(); upperLoop.Edges.Add((edge.Index, f.SameSense));
            var lowerLoop = new ExactLoop(); lowerLoop.Edges.Add((edge.Index, !f.SameSense));
            f.Loops.Add(upperLoop); lower.Loops.Add(lowerLoop);
            edge.FaceA = f.SameSense ? f.Index : lower.Index;
            edge.FaceB = f.SameSense ? lower.Index : f.Index;
            // split facets by hemisphere so per-face data (e.g. surface placement) stays meaningful
            foreach (var fi in f.Facets.ToList())
                if (Vec3.Dot(faces[fi].Centroid() - d.Origin, ax) < 0) { f.Facets.Remove(fi); lower.Facets.Add(fi); }
            if (lower.Facets.Count == 0) lower.Facets.Add(f.Facets[0]);
            if (f.Facets.Count == 0) f.Facets.Add(lower.Facets[0]);
        }

        // ---- 6. lumps
        var lp = Enumerable.Range(0, b.Faces.Count).ToArray();
        int LFind(int x) { while (lp[x] != x) x = lp[x] = lp[lp[x]]; return x; }
        foreach (var e in b.Edges) if (e.FaceA >= 0 && e.FaceB >= 0) lp[LFind(e.FaceA)] = LFind(e.FaceB);
        var lumpIds = new Dictionary<int, int>();
        foreach (var f in b.Faces)
        {
            var r = LFind(f.Index);
            if (!lumpIds.TryGetValue(r, out var id)) lumpIds[r] = id = lumpIds.Count;
            f.Lump = id;
        }
        b.LumpCount = lumpIds.Count;
        foreach (var e in b.Edges) if (e.FaceA < 0 || e.FaceB < 0) { b.Warnings.Add("open edges"); break; }
        return b;
    }

    static ExactLoop BuildLoop(ExactBrep b, ExactFace face, List<(int a, int c, int nbr)> segs, Topology topo,
        Dictionary<(int, int), (int edge, bool forward)> edgeOfHalf, Dictionary<int, HashSet<int>> vertGroups, Func<int, int> vertexId, double tol)
    {
        int n = segs.Count;
        var P = topo.Vertices;
        // break points: neighbour changes, sharp corners, vertices shared by 3+ faces
        var breaks = new List<int>();
        for (int i = 0; i < n; i++)
        {
            var prev = segs[(i - 1 + n) % n]; var s = segs[i];
            bool brk = prev.nbr != s.nbr;
            if (!brk && vertGroups.TryGetValue(s.a, out var gs) && gs.Count >= 3) brk = true;
            if (!brk)
            {
                var d1 = (P[prev.c] - P[prev.a]).Normalized; var d2 = (P[s.c] - P[s.a]).Normalized;
                if (Vec3.Dot(d1, d2) < Math.Cos(35 * Math.PI / 180)) brk = true;
            }
            if (brk) breaks.Add(i);
        }
        var loop = new ExactLoop();
        var runs = new List<List<(int a, int c, int nbr)>>();
        if (breaks.Count == 0)
        {
            // closed run: start at the smallest vertex id (both sides pick the same start)
            int s0 = 0;
            for (int i = 1; i < n; i++) if (segs[i].a < segs[s0].a) s0 = i;
            runs.Add(Enumerable.Range(0, n).Select(k => segs[(s0 + k) % n]).ToList());
        }
        else
        {
            for (int k = 0; k < breaks.Count; k++)
            {
                int from = breaks[k], to = breaks[(k + 1) % breaks.Count];
                var run = new List<(int, int, int)>();
                for (int i = from; ; i = (i + 1) % n)
                {
                    if (run.Count > 0 && i == to) break;
                    run.Add(segs[i]);
                    if (run.Count > n) break;
                }
                runs.Add(run);
            }
        }

        foreach (var run in runs)
        {
            var first = run[0];
            if (edgeOfHalf.TryGetValue((first.c, first.a), out var existing))
            {
                // the neighbour already created this edge in the opposite direction
                var e = b.Edges[existing.edge];
                loop.Edges.Add((existing.edge, !existing.forward));
                if (!existing.forward) e.FaceA = face.Index; else e.FaceB = face.Index;
                continue;
            }
            var ids = new List<int> { run[0].a };
            foreach (var s in run) ids.Add(s.c);
            bool closed = ids[0] == ids[^1];
            var other = first.nbr >= 0 ? b.Faces[first.nbr].Surface : null;
            var edge = MakeEdge(b, face.Surface, other, ids, closed, topo, vertexId, tol);
            edge.Index = b.Edges.Count;
            edge.FaceA = face.Index;
            b.Edges.Add(edge);
            foreach (var s in run) edgeOfHalf[(s.a, s.c)] = (edge.Index, true);
            loop.Edges.Add((edge.Index, true));
        }
        return loop;
    }

    static ExactEdge MakeEdge(ExactBrep b, ExactSurface s1, ExactSurface? s2, List<int> ids, bool closed, Topology topo, Func<int, int> vertexId, double tol)
    {
        var v0 = vertexId(ids[0]);
        var v1 = closed ? v0 : vertexId(ids[^1]);
        var pts = ids.Select(i => topo.Vertices[i]).ToList();
        pts[0] = b.Vertices[v0];
        pts[^1] = b.Vertices[v1];
        var curve = FitCurve(s1, s2, pts, closed, tol, out var refined);
        return new ExactEdge { Curve = curve, V0 = v0, V1 = v1, Points = refined };
    }

    /// <summary>Recognizes lines and circles; falls back to a B-spline through exact intersection points.</summary>
    static ExactCurve FitCurve(ExactSurface s1, ExactSurface? s2, List<Vec3> pts, bool closed, double tol, out List<Vec3> refined)
    {
        refined = pts;
        var scale = 0.0;
        foreach (var p in pts) scale = Math.Max(scale, Vec3.Distance(p, pts[0]));
        var ftol = Math.Max(tol, 1e-7 * scale) * 10;

        // line
        if (!closed)
        {
            var a = pts[0]; var d = pts[^1] - a; var len = d.Length;
            if (len > 1e-12)
            {
                d /= len;
                if (pts.All(p => ((p - a) - d * Vec3.Dot(p - a, d)).Length < ftol)) return new LineCurve(a, d);
            }
        }

        // circle (through three well spread points)
        if (pts.Count >= 3)
        {
            int m = pts.Count;
            var a = pts[0]; var c = closed ? pts[(m - 1) * 2 / 3] : pts[m - 1]; var mid = pts[closed ? (m - 1) / 3 : (m - 1) / 2];
            var circ = Circle3(a, mid, c);
            if (circ is { } cc)
            {
                var (center, axis, r) = cc;
                bool ok = pts.All(p => Math.Abs(Vec3.Distance(p, center) - r) < ftol && Math.Abs(Vec3.Dot(p - center, axis)) < ftol);
                if (ok)
                {
                    // orient the axis so the points run counter-clockwise
                    var n = Vec3.Zero;
                    for (int i = 0; i + 1 < pts.Count; i++) n += Vec3.Cross(pts[i] - center, pts[i + 1] - center);
                    if (Vec3.Dot(n, axis) < 0) axis = -axis;
                    var refDir = (a - center).Normalized;
                    return new CircleCurve(center, axis.Normalized, refDir, r);
                }
            }
        }

        // general intersection curve: project onto both exact surfaces and interpolate
        var q = new List<Vec3>(pts.Count) { pts[0] };
        for (int i = 1; i < pts.Count - 1; i++)
            q.Add(s2 != null ? ExactSurface.ProjectToIntersection(s1, s2, pts[i]) : s1.Project(pts[i]));
        q.Add(pts[^1]);
        // drop near-duplicate points (they make the interpolation ill conditioned)
        var clean = new List<Vec3> { q[0] };
        for (int i = 1; i < q.Count; i++) if (Vec3.Distance(q[i], clean[^1]) > ftol || i == q.Count - 1) clean.Add(q[i]);
        refined = clean;
        if (clean.Count == 2) return new LineCurve(clean[0], (clean[1] - clean[0]).Normalized);
        return BSplineCurve.Interpolate(clean, 3);
    }

    static (Vec3 center, Vec3 axis, double r)? Circle3(Vec3 a, Vec3 b, Vec3 c)
    {
        var ab = b - a; var ac = c - a;
        var n = Vec3.Cross(ab, ac);
        var n2 = n.LengthSq;
        if (n2 < 1e-24) return null;
        var center = a + (Vec3.Cross(n, ab) * ac.LengthSq + Vec3.Cross(ac, n) * ab.LengthSq) / (2 * n2);
        return (center, n / Math.Sqrt(n2), Vec3.Distance(center, a));
    }
}
