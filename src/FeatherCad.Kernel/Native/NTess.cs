namespace FeatherCad.Kernel.Native;

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

        // curved: triangulate the UV domain, refine interior edges against the surface
        var uv = dom.Loops.SelectMany(l => l).ToList();
        var p3 = dom.Loops3.SelectMany(l => l).ToList();
        var boundary = new HashSet<(int, int)>();
        int offs = 0;
        foreach (var l in dom.Loops)
        {
            for (int i = 0; i < l.Length; i++)
            {
                int a = offs + i, b = offs + (i + 1) % l.Length;
                boundary.Add(a < b ? (a, b) : (b, a));
            }
            offs += l.Length;
        }
        var idx = Triangulator.Triangulate(dom.Loops);
        var tris = new List<(int, int, int)>();
        for (int i = 0; i + 2 < idx.Count; i += 3) tris.Add((idx[i], idx[i + 1], idx[i + 2]));
        // metric so that UV distances approximate surface distances (Delaunay quality)
        var (su, sv) = MetricScale(f.Surface);
        var metric = uv.Select(q => new Vec2(q.X * su, q.Y * sv)).ToList();
        var mesh = new CdtMesh(metric, tris, boundary);
        mesh.MakeDelaunay();
        // interior points on a grid spaced from the surface curvature
        var (hu, hv) = Spacing(f.Surface, tol);
        double mu = (dom.UMax - dom.UMin) * su, mv = (dom.VMax - dom.VMin) * sv;
        int nu = (int)Math.Clamp(Math.Ceiling(mu / hu), 1, 400), nv = (int)Math.Clamp(Math.Ceiling(mv / hv), 1, 400);
        if ((long)nu * nv > 40000) { var k = Math.Sqrt(40000.0 / ((long)nu * nv)); nu = Math.Max(1, (int)(nu * k)); nv = Math.Max(1, (int)(nv * k)); }
        var minGap = 0.45 * Math.Min(mu / nu, mv / nv);
        for (int i = 1; i < nu; i++)
            for (int j = 1; j < nv; j++)
            {
                var q = new Vec2(dom.UMin + (dom.UMax - dom.UMin) * i / nu, dom.VMin + (dom.VMax - dom.VMin) * j / nv);
                if (!dom.ContainsUV(q)) continue;
                var pq = dom.Eval(q);
                if (dom.BoundaryDistance(pq) < minGap) continue;
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
            outFaces.Add(new Face(pl, f.Tag, p3[a], p3[b], p3[c]));
        }
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
        }
        return (1, 1);
    }

    static (double su, double sv) MetricScale(NSurface s) => s.Kind switch
    {
        SurfaceKind.Cylinder => (s.R, 1),
        SurfaceKind.Cone => (Math.Max(s.R, 1e-3), 1),
        SurfaceKind.Sphere => (s.R, s.R),
        SurfaceKind.Torus => (s.R + s.R2, s.R2),
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
