namespace SpryCad.Kernel.Native;

/// <summary>
/// Tessellates a native body into the planar-facet <see cref="Solid"/> used for display, picking, STL
/// and the fallback boolean engine. Edges are sampled once and shared by both faces, so the facets are
/// watertight; curved faces are triangulated in their UV domain and refined against the true surface.
/// </summary>
public static class NTess
{
    public const double DefaultTol = 0.02;

    /// <summary>A solid backed by the body; its facets are tessellated on first use.</summary>
    public static Solid ToSolid(NBody body, double tol = DefaultTol) => new(body, () => Facets(body, tol));

    public static List<Face> Facets(NBody body, double tol = DefaultTol)
    {
        // shared edges are sampled once up front, so neighbouring faces use identical boundary points;
        // faces are then independent and tessellate in parallel
        foreach (var e in body.Edges) e.Samples(tol);
        var perFace = new List<Face>[body.Faces.Count];
        Parallel.For(0, body.Faces.Count, i =>
        {
            var l = new List<Face>();
            Tessellate(body.Faces[i], tol, l);
            perFace[i] = l;
        });
        var faces = new List<Face>();
        foreach (var l in perFace) faces.AddRange(l);
        return faces;
    }

    static void Tessellate(NFace f, double tol, List<Face> outFaces)
    {
        var dom = NDomain.Of(f, tol);
        if (f.Surface.Kind == SurfaceKind.Plane)
        {
            var plane = f.Sense ? f.Surface.Plane! : f.Surface.Plane!.Flipped();
            // loops are already oriented with the interior on the left of the outward normal
            var areas = dom.Loops.Select(LoopArea).ToList();
            var outers = Enumerable.Range(0, dom.Loops.Count).Where(i => areas[i] > 0).OrderByDescending(i => areas[i]).ToList();
            var holes = Enumerable.Range(0, dom.Loops.Count).Where(i => areas[i] <= 0).ToList();
            var groups = outers.ToDictionary(i => i, _ => new List<int>());
            foreach (var h in holes)
            {
                var hl = dom.Loops[h];
                var probe = Vec2.Lerp(hl[0], hl[1 % hl.Length], 0.5) + (hl[1 % hl.Length] - hl[0]).Perp * 1e-3;
                var owner = outers.Where(o => PointIn(dom.Loops[o], probe)).OrderBy(o => areas[o]).FirstOrDefault(-1);
                if (owner >= 0) groups[owner].Add(h);
            }
            foreach (var (o, hs) in groups)
            {
                var loops = new List<Vec3[]> { dom.Loops3[o] };
                loops.AddRange(hs.Select(h => dom.Loops3[h]));
                outFaces.Add(new Face(plane, f.Tag, loops));
            }
            return;
        }

        // long helical faces: in slabs (see Slabs); anything else: the whole face
        if (f.Surface.Kind == SurfaceKind.Screw && Slabs(dom, tol) is { } slabs)
        {
            foreach (var (luv, l3) in slabs) Curved(f, dom, new[] { luv }, new[] { l3 }, tol, outFaces);
            return;
        }
        Curved(f, dom, dom.Loops, dom.Loops3, tol, outFaces);
    }

    /// <summary>
    /// A curved face's triangles: its (u, v) loops triangulated, inner points on a grid spaced from the surface's
    /// curvature, interior edges refined against the surface. <paramref name="loopsUv"/> are the face's loops, or a
    /// piece of them (<see cref="Slabs"/>).
    /// </summary>
    static void Curved(NFace f, NDomain dom, IReadOnlyList<Vec2[]> loopsUv, IReadOnlyList<Vec3[]> loops3, double tol, List<Face> outFaces)
    {
        var uv = loopsUv.SelectMany(l => l).ToList();
        var p3 = loops3.SelectMany(l => l).ToList();
        var boundary = new HashSet<(int, int)>();
        int offs = 0;
        foreach (var l in loopsUv)
        {
            for (int i = 0; i < l.Length; i++)
            {
                int a = offs + i, b = offs + (i + 1) % l.Length;
                boundary.Add(a < b ? (a, b) : (b, a));
            }
            offs += l.Length;
        }
        var idx = Triangulator.Triangulate(loopsUv);
        var tris = new List<(int, int, int)>();
        for (int i = 0; i + 2 < idx.Count; i += 3) tris.Add((idx[i], idx[i + 1], idx[i + 2]));
        // metric so that UV distances approximate surface distances (Delaunay quality)
        var (su, sv) = MetricScale(f.Surface);
        var metric = uv.Select(q => new Vec2(q.X * su, q.Y * sv)).ToList();
        var mesh = new CdtMesh(metric, tris, boundary);
        mesh.MakeDelaunay();
        // interior points on a grid spaced from the surface curvature
        var (hu, hv) = Spacing(f.Surface, tol);
        double uMin = uv.Min(q => q.X), uMax = uv.Max(q => q.X), vMin = uv.Min(q => q.Y), vMax = uv.Max(q => q.Y);
        double mu = (uMax - uMin) * su, mv = (vMax - vMin) * sv;
        // a helical face runs many turns along u and little along v: its grid may be long in u (within the same node
        // budget), else the refinement below has to split coarse cells over and over
        int capU = f.Surface.Kind == SurfaceKind.Screw ? 40000 : 400;
        int nu = (int)Math.Clamp(Math.Ceiling(mu / hu), 1, capU), nv = (int)Math.Clamp(Math.Ceiling(mv / hv), 1, 400);
        if ((long)nu * nv > 40000) { var k = Math.Sqrt(40000.0 / ((long)nu * nv)); nu = Math.Max(1, (int)(nu * k)); nv = Math.Max(1, (int)(nv * k)); }
        var minGap = 0.45 * Math.Min(mu / nu, mv / nv);
        // each row's boundary crossings once (the inside test of its points), and the boundary in boxed runs (the
        // distance test skips runs that are far)
        var rows = new double[nv][];
        for (int j = 1; j < nv; j++) rows[j] = Crossings(loopsUv, vMin + (vMax - vMin) * j / nv);
        var runs = BoundaryRuns(loops3);
        for (int i = 1; i < nu; i++)
            for (int j = 1; j < nv; j++)
            {
                var q = new Vec2(uMin + (uMax - uMin) * i / nu, vMin + (vMax - vMin) * j / nv);
                if (!Inside(rows[j], q.X)) continue;
                var pq = dom.Eval(q);
                if (NearBoundary(loops3, runs, pq, minGap)) continue;
                uv.Add(q); p3.Add(pq); metric.Add(new Vec2(q.X * su, q.Y * sv));
                if (!mesh.Insert(uv.Count - 1)) { uv.RemoveAt(uv.Count - 1); p3.RemoveAt(p3.Count - 1); metric.RemoveAt(metric.Count - 1); }
            }
        tris = mesh.Triangles.ToList();
        Vec2 M(int i) => new(uv[i].X * su, uv[i].Y * sv);
        var mids = new Dictionary<(int, int), int>();
        for (int level = 0; level < 7; level++)
        {
            bool changed = false;
            var next = new List<(int, int, int)>(tris.Count * 2);
            int Mid(int a, int b)
            {
                var k = a < b ? (a, b) : (b, a);
                if (mids.TryGetValue(k, out var m)) return m;
                if (boundary.Contains(k)) { mids[k] = -1; return -1; }
                var muv = (uv[a] + uv[b]) * 0.5;
                var mp = dom.Eval(muv);
                // near poles/apexes a tiny chord error can hide a huge angular span: limit the span too
                bool wide = f.Surface.PeriodicU && Math.Abs(uv[a].X - uv[b].X) > Math.PI / 12;
                if (!wide && Vec3.Distance(mp, (p3[a] + p3[b]) * 0.5) < tol) { mids[k] = -1; return -1; }
                m = uv.Count; uv.Add(muv); p3.Add(mp);
                mids[k] = m;
                return m;
            }
            foreach (var (a, b, c) in tris)
            {
                int mab = Mid(a, b), mbc = Mid(b, c), mca = Mid(c, a);
                int splits = (mab >= 0 ? 1 : 0) + (mbc >= 0 ? 1 : 0) + (mca >= 0 ? 1 : 0);
                if (splits == 0) { next.Add((a, b, c)); continue; }
                changed = true;
                if (splits == 3) { next.Add((a, mab, mca)); next.Add((mab, b, mbc)); next.Add((mca, mbc, c)); next.Add((mab, mbc, mca)); }
                else if (splits == 1)
                {
                    if (mab >= 0) { next.Add((a, mab, c)); next.Add((mab, b, c)); }
                    else if (mbc >= 0) { next.Add((a, b, mbc)); next.Add((a, mbc, c)); }
                    else { next.Add((a, b, mca)); next.Add((mca, b, c)); }
                }
                else
                {
                    if (mca < 0) { next.Add((mab, b, mbc)); next.Add((a, mab, mbc)); next.Add((a, mbc, c)); }
                    else if (mab < 0) { next.Add((mbc, c, mca)); next.Add((b, mbc, mca)); next.Add((b, mca, a)); }
                    else { next.Add((mca, a, mab)); next.Add((c, mca, mab)); next.Add((c, mab, b)); }
                }
            }
            tris = next;
            if (!changed) break;
            // (no flips between refinement levels: splits already keep a valid tiling)
        }
        foreach (var (a, b, c) in tris)
        {
            var pl = Plane.FromPoints(p3[a], p3[b], p3[c]);
            if (pl == null) continue; // degenerate (pole)
            outFaces.Add(new Face(pl, f.Tag, p3[a], p3[b], p3[c]) { Pool = p3, PoolA = a, PoolB = b, PoolC = c });
        }
    }

    /// <summary>
    /// Where the line v = y crosses the loops (the even-odd rule's crossings for points on it, sorted): a point
    /// (x, y) is inside when an odd number of them lie beyond x.
    /// </summary>
    static double[] Crossings(IReadOnlyList<Vec2[]> loops, double y)
    {
        var xs = new List<double>();
        foreach (var poly in loops)
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                var a = poly[i]; var b = poly[j];
                if ((a.Y > y) != (b.Y > y)) xs.Add((b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X);
            }
        xs.Sort();
        return xs.ToArray();
    }

    static bool Inside(double[] crossings, double x)
    {
        // how many lie beyond x: from the first one greater than x
        int lo = 0, hi = crossings.Length;
        while (lo < hi) { int m = (lo + hi) >> 1; if (crossings[m] > x) hi = m; else lo = m + 1; }
        return ((crossings.Length - lo) & 1) == 1;
    }

    const int RunLength = 16;

    /// <summary>The boxes of the loops' segments in runs of <see cref="RunLength"/> (in loop order).</summary>
    static List<Box3> BoundaryRuns(IReadOnlyList<Vec3[]> loops)
    {
        var boxes = new List<Box3>();
        foreach (var l in loops)
            for (int s = 0; s < l.Length; s += RunLength)
            {
                var b = Box3.Empty;
                for (int i = s; i < Math.Min(s + RunLength, l.Length); i++) { b.Add(l[i]); b.Add(l[(i + 1) % l.Length]); }
                boxes.Add(b);
            }
        return boxes;
    }

    /// <summary>
    /// Whether p is closer than gap to a boundary segment (the least of their distances below gap), testing only
    /// the runs whose box comes that close.
    /// </summary>
    static bool NearBoundary(IReadOnlyList<Vec3[]> loops, List<Box3> runs, Vec3 p, double gap)
    {
        // a box farther than this holds no segment that rounding could bring under gap
        var far = gap * (1 + 1e-9) + 1e-12 * (Math.Abs(p.X) + Math.Abs(p.Y) + Math.Abs(p.Z) + 1);
        int r = 0;
        foreach (var l in loops)
            for (int s = 0; s < l.Length; s += RunLength, r++)
            {
                var bx = runs[r];
                double dx = Math.Max(0, Math.Max(bx.Min.X - p.X, p.X - bx.Max.X)), dy = Math.Max(0, Math.Max(bx.Min.Y - p.Y, p.Y - bx.Max.Y)),
                       dz = Math.Max(0, Math.Max(bx.Min.Z - p.Z, p.Z - bx.Max.Z));
                if (dx * dx + dy * dy + dz * dz > far * far) continue;
                for (int i = s; i < Math.Min(s + RunLength, l.Length); i++)
                {
                    var a = l[i]; var b = l[(i + 1) % l.Length];
                    var d = b - a; var l2 = d.LengthSq;
                    var t = l2 > 0 ? Math.Clamp(Vec3.Dot(p - a, d) / l2, 0, 1) : 0;
                    if ((a + d * t - p).Length < gap) return true;
                }
            }
        return false;
    }

    /// <summary>
    /// A long helical face (many turns along u) in slabs of about half a turn: its one loop cut by diagonals between
    /// its own boundary points (one on each side, where the outline runs on along u), so the slabs share those points
    /// and nothing new appears on the face's boundary. Null when the face is short, has holes, or offers no clean
    /// cut. A single long thin outline triangulates into long fans the refinement then splits over and over.
    /// The cuts are sampled on the surface to the chord tolerance (the same points for the slabs either side): a
    /// cut is boundary to both, never refined, and a bare chord across a strongly curved face (round wire: half
    /// the circle) would show as a flat crease.
    /// </summary>
    static List<(Vec2[] uv, Vec3[] p3)>? Slabs(NDomain dom, double tol)
    {
        if (dom.Loops.Count != 1) return null;
        var uv = dom.Loops[0]; var p3 = dom.Loops3[0];
        int n = uv.Length;
        if (n < 8 || dom.UMax - dom.UMin < 1.5 * Math.PI) return null;
        int s = 0, e = 0;
        for (int i = 1; i < n; i++) { if (uv[i].X < uv[s].X) s = i; if (uv[i].X > uv[e].X) e = i; }
        // the loop runs s → e along one side and e → s along the other
        var a = new List<int>(); for (int i = s; ; i = (i + 1) % n) { a.Add(i); if (i == e) break; }
        var b = new List<int>(); for (int i = e; ; i = (i + 1) % n) { b.Add(i); if (i == s) break; }
        b.Reverse();
        // where a side runs on along u (increasing over a few samples around k)
        bool Runs(List<int> c, int k)
        {
            if (k < 3 || k > c.Count - 4) return false;
            for (int i = k - 3; i < k + 3; i++) if (!(uv[c[i + 1]].X > uv[c[i]].X)) return false;
            return true;
        }
        var cuts = new List<(int ia, int ib)>();
        double step = Math.PI / 2, umin = uv[s].X, umax = uv[e].X;
        int ja = 0, jb = 0;
        for (double cu = umin + step; cu < umax - step / 2; cu += step)
        {
            while (ja < a.Count - 1 && uv[a[ja]].X < cu) ja++;
            while (jb < b.Count - 1 && uv[b[jb]].X < cu) jb++;
            if (!Runs(a, ja) || !Runs(b, jb) || Math.Abs(uv[a[ja]].X - uv[b[jb]].X) > step / 4) continue;
            if (cuts.Count > 0 && (ja <= cuts[^1].ia || jb <= cuts[^1].ib)) continue;
            cuts.Add((ja, jb));
        }
        if (cuts.Count == 0) return null;
        // each cut's inner points, from side a to side b: halved in (u, v) while the surface leaves the chord
        List<(Vec2 uv, Vec3 p)> Inner(int ia, int ib)
        {
            var res = new List<(Vec2, Vec3)>();
            void Split(Vec2 q0, Vec3 p0, Vec2 q1, Vec3 p1, int depth)
            {
                var qm = (q0 + q1) * 0.5; var pm = dom.Eval(qm);
                if (depth >= 10 || Vec3.Distance(pm, (p0 + p1) * 0.5) < tol && depth > 0) return;
                Split(q0, p0, qm, pm, depth + 1);
                res.Add((qm, pm));
                Split(qm, pm, q1, p1, depth + 1);
            }
            Split(uv[a[ia]], p3[a[ia]], uv[b[ib]], p3[b[ib]], 0);
            return res;
        }
        var inner = cuts.Select(c => Inner(c.ia, c.ib)).ToList();
        var slabs = new List<(Vec2[], Vec3[])>();
        int pa = 0, pb = 0;
        for (int k = 0; k <= cuts.Count; k++)
        {
            var (ia, ib) = k < cuts.Count ? cuts[k] : (a.Count - 1, b.Count - 1);
            // this slab: along side a from the previous cut to this one, across it, back along side b, back across
            // the previous cut
            var pts = new List<(Vec2 uv, Vec3 p)>();
            int last = -1;
            void Add(int i) { if (i != last) pts.Add((uv[i], p3[i])); last = i; }
            for (int i = pa; i <= ia; i++) Add(a[i]);
            if (k < cuts.Count) { pts.AddRange(inner[k]); last = -1; }
            for (int i = ib; i >= pb; i--) Add(b[i]);
            if (k > 0) pts.AddRange(Enumerable.Reverse(inner[k - 1]));
            else if (pts.Count > 1 && last == a[pa]) pts.RemoveAt(pts.Count - 1);
            slabs.Add((pts.Select(x => x.uv).ToArray(), pts.Select(x => x.p).ToArray()));
            pa = ia; pb = ib;
        }
        return slabs;
    }

    /// <summary>Grid spacing (metric units) along u and v for a chord tolerance.</summary>
    static (double hu, double hv) Spacing(NSurface s, double tol)
    {
        double Ang(double r) => 2 * Math.Acos(Math.Max(-1, 1 - Math.Min(1, tol / Math.Max(r, 1e-9))));
        switch (s.Kind)
        {
            case SurfaceKind.Cylinder: { var hu = Ang(s.R) * s.R; return (hu, Math.Max(hu * 8, 1e-6)); }
            case SurfaceKind.Cone: { var r = Math.Max(s.R, 1e-3); var hu = Ang(r) * r; return (hu, Math.Max(hu * 4, 1e-6)); }
            case SurfaceKind.Sphere: { var h = Ang(s.R) * s.R; return (h, h); }
            case SurfaceKind.Torus: { var hu = Ang(s.R + s.R2) * (s.R + s.R2); var hv = Ang(s.R2) * s.R2; return (hu, hv); }
            case SurfaceKind.Offset:
                return Spacing(s.Base!, tol);
            case SurfaceKind.BSpline:
                {
                    // one grid cell per knot span (the surface is a single polynomial piece in each); the refinement
                    // below then adds points only where the surface really curves away from the facets. (A uniform
                    // grid sized for the tightest bend would fill large flat areas of the same face with tiny facets.)
                    var nb = s.Nurbs!;
                    var (su, sv) = MetricScale(s);
                    var rho = Math.Min(nb.MinCurvatureRadius(), 1e6);
                    var hc = Ang(rho) * rho;
                    var hu = Math.Max(hc, (nb.U1 - nb.U0) * su / Math.Max(1, nb.SpansU));
                    var hv = Math.Max(hc, (nb.V1 - nb.V0) * sv / Math.Max(1, nb.SpansV));
                    return (Math.Max(hu, 1e-6), Math.Max(hv, 1e-6));
                }
            case SurfaceKind.Screw:
                {
                    // around the axis at the largest radius; along the generator its own curvature (a line: long)
                    var sc = s.Screw!;
                    var r = Math.Max(sc.MaxRadius, 1e-3);
                    var hu = Ang(r) * r;
                    // a line generator is straight, but the strip needs interior rows (as fine as across) to triangulate
                    // into well-shaped triangles: a bare boundary of a long strip fans into long diagonals
                    var hv = sc.Gen is NCircle gc ? Math.Min(Ang(gc.R) * gc.R, hu) : hu;
                    return (hu, Math.Max(hv, 1e-6));
                }
            case SurfaceKind.Pipe:
                {
                    // around: the pipe radius; along: the tightest bend of the surface (inner side of the spine)
                    var hu = Ang(s.R) * s.R;
                    var bend = Math.Max(s.Spine!.MinCurvatureRadius - s.R, s.R * 0.05);
                    var hv = double.IsInfinity(bend) ? hu * 8 : Math.Min(hu * 8, Ang(bend) * bend);
                    return (hu, Math.Max(hv, 1e-6));
                }
        }
        return (1, 1);
    }

    static (double su, double sv) MetricScale(NSurface s) => s.Kind switch
    {
        SurfaceKind.Cylinder => (s.R, 1),
        SurfaceKind.Cone => (Math.Max(s.R, 1e-3), 1),
        SurfaceKind.Sphere => (s.R, s.R),
        SurfaceKind.Torus => (s.R + s.R2, s.R2),
        SurfaceKind.Pipe => (s.R, 1),
        SurfaceKind.Screw => (Math.Max(s.Screw!.MaxRadius, 1e-3), 1),
        SurfaceKind.BSpline => s.Nurbs!.MetricScale(),
        SurfaceKind.Offset => MetricScale(s.Base!),
        _ => (1, 1),
    };

    /// <summary>Lawson edge flips towards a Delaunay triangulation (constrained by boundary edges).</summary>
    static void DelaunayFlip(List<(int a, int b, int c)> tris, Func<int, Vec2> P, HashSet<(int, int)> fixedEdges)
    {
        for (int pass = 0; pass < 200; pass++)
        {
            var edgeTri = new Dictionary<(int, int), int>();
            for (int i = 0; i < tris.Count; i++)
            {
                var (a, b, c) = tris[i];
                edgeTri[(a, b)] = i; edgeTri[(b, c)] = i; edgeTri[(c, a)] = i;
            }
            bool flipped = false;
            var dirty = new bool[tris.Count];
            for (int i = 0; i < tris.Count; i++)
            {
                if (dirty[i]) continue;
                var t = tris[i];
                foreach (var (x, y, z) in new[] { (t.a, t.b, t.c), (t.b, t.c, t.a), (t.c, t.a, t.b) })
                {
                    var key = x < y ? (x, y) : (y, x);
                    if (fixedEdges.Contains(key)) continue;
                    if (!edgeTri.TryGetValue((y, x), out var j) || dirty[j] || j == i) continue;
                    var o = tris[j];
                    int w = o.a != x && o.a != y ? o.a : o.b != x && o.b != y ? o.b : o.c;
                    // quad x-w-y-z must be convex for a flip
                    var px = P(x); var py = P(y); var pz = P(z); var pw = P(w);
                    if (Vec2.Cross(px - pz, pw - pz) <= 1e-18 || Vec2.Cross(py - pw, pz - pw) <= 1e-18) continue; // new triangles (z,x,w), (w,y,z) must be CCW
                    if (!InCircle(px, py, pz, pw)) continue;
                    tris[i] = (z, x, w); tris[j] = (w, y, z);
                    dirty[i] = dirty[j] = true;
                    flipped = true;
                    break;
                }
            }
            if (!flipped) break;
        }
    }

    /// <summary>True if d lies inside the circumcircle of the counter-clockwise triangle (a, b, c).</summary>
    static bool InCircle(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        double adx = a.X - d.X, ady = a.Y - d.Y, bdx = b.X - d.X, bdy = b.Y - d.Y, cdx = c.X - d.X, cdy = c.Y - d.Y;
        double det = (adx * adx + ady * ady) * (bdx * cdy - cdx * bdy) - (bdx * bdx + bdy * bdy) * (adx * cdy - cdx * ady) + (cdx * cdx + cdy * cdy) * (adx * bdy - bdx * ady);
        var scale = (adx * adx + ady * ady + bdx * bdx + bdy * bdy + cdx * cdx + cdy * cdy);
        return det > 1e-12 * scale * scale;
    }

    static double LoopArea(Vec2[] l)
    {
        double a = 0;
        for (int i = 0, j = l.Length - 1; i < l.Length; j = i++) a += l[j].X * l[i].Y - l[i].X * l[j].Y;
        return a / 2;
    }

    static bool PointIn(Vec2[] poly, Vec2 p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var a = poly[i]; var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}
