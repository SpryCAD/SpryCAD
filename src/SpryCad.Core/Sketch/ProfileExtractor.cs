using SpryCad.Kernel;

namespace SpryCad.Core.Sketching;

/// <summary>
/// Finds closed regions in a sketch. Lines and arcs form a planar graph through shared points;
/// for each connected component the outer boundary is traced (union of all its regions), circles are
/// loops on their own, then loops are nested with the even-odd rule to produce regions with holes.
/// </summary>
public static class ProfileExtractor
{
    sealed class GEdge
    {
        public int A, B;              // point ids
        public List<ProfileVertex> Fwd = new(); // vertices from A (inclusive) to B (exclusive)
        public SkEntity Entity = null!;
    }

    public static string FaceName(SkEntity e) => e switch
    {
        SkLine => "L" + e.Id,
        SkArc => "A" + e.Id,
        SkCircle => "C" + e.Id,
        _ => "E" + e.Id,
    };

    public static List<ProfileRegion> Extract(Sketch sk, out List<string> warnings, ISet<int>? excludeEntities = null)
    {
        warnings = new List<string>();
        var loops = new List<ProfileLoop>();
        var edges = new List<GEdge>();
        foreach (var e in sk.Entities)
        {
            if (e.Construction || e.IsReference || (excludeEntities?.Contains(e.Id) ?? false)) continue;
            switch (e)
            {
                case SkCircle c:
                    if (c.R > 1e-9) loops.Add(ProfileLoop.Circle(sk.P(c.Center), c.R, FaceName(c)));
                    break;
                case SkLine l:
                    if (l.P1 == l.P2 || Vec2.Distance(sk.P(l.P1), sk.P(l.P2)) < 1e-9) break;
                    edges.Add(new GEdge { A = l.P1, B = l.P2, Entity = l, Fwd = { new ProfileVertex(sk.P(l.P1), FaceName(l)) } });
                    break;
                case SkArc a:
                    {
                        var c = sk.P(a.Center); var s = sk.P(a.Start); var en = sk.P(a.End);
                        var r = (s - c).Length;
                        if (r < 1e-9) break;
                        var tmp = new ProfileLoop();
                        tmp.AddArc(c, r, (s - c).Angle, (en - c).Angle, true, FaceName(a));
                        var ge = new GEdge { A = a.Start, B = a.End, Entity = a };
                        ge.Fwd.AddRange(tmp.Verts);
                        if (a.Start == a.End) { loops.Add(new ProfileLoop(ge.Fwd)); break; }
                        edges.Add(ge);
                        break;
                    }
            }
        }

        // prune dangling edges (degree 1 vertices) repeatedly
        while (true)
        {
            var deg = new Dictionary<int, int>();
            foreach (var e in edges) { deg[e.A] = deg.GetValueOrDefault(e.A) + 1; deg[e.B] = deg.GetValueOrDefault(e.B) + 1; }
            var before = edges.Count;
            edges.RemoveAll(e => deg[e.A] < 2 || deg[e.B] < 2);
            if (edges.Count == before) break;
            if (before - edges.Count > 0 && warnings.Count == 0) warnings.Add("Open contour segments were ignored.");
        }

        if (edges.Count > 0) loops.AddRange(TraceOuterBoundaries(sk, edges));
        return ProfileRegion.FromLoops(loops);
    }

    /// <summary>Half-edge traversal of the planar graph, returning each component's outer boundary (CCW).</summary>
    static List<ProfileLoop> TraceOuterBoundaries(Sketch sk, List<GEdge> edges)
    {
        // half edge h = 2*i (A->B) or 2*i+1 (B->A)
        int nh = edges.Count * 2;
        int From(int h) => h % 2 == 0 ? edges[h / 2].A : edges[h / 2].B;
        int To(int h) => h % 2 == 0 ? edges[h / 2].B : edges[h / 2].A;
        List<ProfileVertex> Verts(int h)
        {
            var e = edges[h / 2];
            if (h % 2 == 0) return e.Fwd;
            // reversed: vertices from B to A (exclusive of A), keep names
            var pts = e.Fwd.Select(v => v.P).ToList(); pts.Add(sk.P(e.B));
            pts.Reverse();
            var name = e.Fwd[0].Name; var arc = e.Fwd[0].Arc;
            return pts.Take(pts.Count - 1).Select(p => new ProfileVertex(p, name, arc)).ToList();
        }
        Vec2 DirOut(int h)
        {
            var v = Verts(h);
            var p0 = v[0].P;
            var p1 = v.Count > 1 ? v[1].P : sk.P(To(h));
            return (p1 - p0).Normalized;
        }
        var outgoing = new Dictionary<int, List<int>>();
        for (int h = 0; h < nh; h++)
        {
            if (!outgoing.TryGetValue(From(h), out var l)) outgoing[From(h)] = l = new List<int>();
            l.Add(h);
        }
        // sort outgoing half-edges at each vertex by angle
        foreach (var l in outgoing.Values) l.Sort((x, y) => DirOut(x).Angle.CompareTo(DirOut(y).Angle));

        int Next(int h)
        {
            // arriving at v via h; the twin points back. Take the next edge clockwise from the twin
            // (i.e. previous in CCW angle order) -> traces faces with interior on the left.
            int twin = h ^ 1;
            var l = outgoing[To(h)];
            int idx = l.IndexOf(twin);
            return l[(idx - 1 + l.Count) % l.Count];
        }

        var used = new bool[nh];
        var result = new List<ProfileLoop>();
        for (int h0 = 0; h0 < nh; h0++)
        {
            if (used[h0]) continue;
            var loop = new ProfileLoop();
            int h = h0; int guard = 0;
            while (!used[h] && guard++ < 100000)
            {
                used[h] = true;
                loop.Verts.AddRange(Verts(h));
                h = Next(h);
            }
            if (loop.Verts.Count < 3) continue;
            // outer face of a component is traced clockwise
            if (loop.SignedArea() < -1e-12) result.Add(loop.Reversed());
        }
        return result;
    }
}
