namespace SpryCad.Kernel;

/// <summary>
/// Rebuilds B-rep faces from the polygon soup produced by the BSP engine: welds vertices,
/// merges coplanar fragments, resolves T-junctions and extracts outer/hole loops.
/// This keeps face counts stable through long feature histories.
/// </summary>
internal static class FaceMerger
{
    public const double WeldTol = 1e-6;

    sealed class Group
    {
        public Plane Plane = null!;
        public readonly List<CsgPoly> Polys = new();
    }

    public static Solid Rebuild(List<CsgPoly> polys)
    {
        if (polys.Count == 0) return Solid.Empty;
        var welder = new VertexWelder(WeldTol);
        var polyIds = new List<int[]>(polys.Count);
        foreach (var p in polys)
        {
            var ids = new int[p.Pts.Length];
            for (int i = 0; i < ids.Length; i++) ids[i] = welder.Add(p.Pts[i]);
            polyIds.Add(ids);
        }
        var pts = welder.Points;
        var segIndex = new SegmentVertexIndex(pts, WeldTol);

        // --- group by plane (and by tag for curved surfaces) ---
        var planeByRef = new Dictionary<Plane, int>(ReferenceEqualityComparer.Instance);
        var uniquePlanes = new List<Plane>();
        var groups = new Dictionary<(int, string?), Group>();
        var groupOfPoly = new Group[polys.Count];
        for (int i = 0; i < polys.Count; i++)
        {
            var p = polys[i];
            if (!planeByRef.TryGetValue(p.Plane, out var pi))
            {
                pi = -1;
                for (int k = 0; k < uniquePlanes.Count; k++)
                {
                    var u = uniquePlanes[k];
                    if (Math.Abs(u.D - p.Plane.D) < 1e-6 && Vec3.Dot(u.Normal, p.Plane.Normal) > 1 - 1e-10) { pi = k; break; }
                }
                if (pi < 0) { pi = uniquePlanes.Count; uniquePlanes.Add(p.Plane); }
                planeByRef[p.Plane] = pi;
            }
            var key = (pi, p.Tag.Surface.IsCurved ? p.Tag.Name : null);
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = new Group { Plane = uniquePlanes[pi] };
            g.Polys.Add(p);
            groupOfPoly[i] = g;
        }

        var polyIndex = new Dictionary<CsgPoly, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < polys.Count; i++) polyIndex[polys[i]] = i;

        var faces = new List<Face>();
        var between = new List<int>();
        foreach (var g in groups.Values)
        {
            // half edges with source tag
            var edgeCount = new Dictionary<(int, int), int>();
            var edgeTag = new Dictionary<(int, int), FaceTag>();
            foreach (var poly in g.Polys)
            {
                var ids = polyIds[polyIndex[poly]];
                for (int i = 0; i < ids.Length; i++)
                {
                    int a = ids[i], b = ids[(i + 1) % ids.Length];
                    if (a == b) continue;
                    segIndex.Between(a, b, between);
                    int prev = a;
                    foreach (var mid in between) { AddEdge(prev, mid, poly.Tag); prev = mid; }
                    AddEdge(prev, b, poly.Tag);
                }
            }

            void AddEdge(int a, int b, FaceTag tag)
            {
                if (a == b) return;
                if (edgeCount.TryGetValue((b, a), out var c) && c > 0)
                {
                    if (c == 1) edgeCount.Remove((b, a)); else edgeCount[(b, a)] = c - 1;
                    return;
                }
                edgeCount[(a, b)] = edgeCount.TryGetValue((a, b), out var c2) ? c2 + 1 : 1;
                if (!edgeTag.TryGetValue((a, b), out var t) || tag.Seq < t.Seq) edgeTag[(a, b)] = tag;
            }

            if (edgeCount.Count == 0) continue;

            // 2D projection for this plane
            var n = g.Plane.Normal;
            var ux = n.AnyPerpendicular();
            var uy = Vec3.Cross(n, ux);
            Vec2 P2(int id) { var p = pts[id]; return new Vec2(Vec3.Dot(p, ux), Vec3.Dot(p, uy)); }

            var outgoing = new Dictionary<int, List<int>>();
            foreach (var kv in edgeCount)
            {
                for (int k = 0; k < kv.Value; k++)
                {
                    if (!outgoing.TryGetValue(kv.Key.Item1, out var l)) outgoing[kv.Key.Item1] = l = new List<int>(2);
                    l.Add(kv.Key.Item2);
                }
            }

            var loops = new List<(List<int> ids, FaceTag tag)>();
            foreach (var start in outgoing.Keys.ToList())
            {
                while (outgoing.TryGetValue(start, out var outs) && outs.Count > 0)
                {
                    var loop = new List<int> { start };
                    FaceTag? tag = null;
                    int prev = start, cur = outs[0];
                    outs.RemoveAt(0);
                    PickTag(ref tag, start, cur);
                    int guard = 0;
                    while (cur != start && guard++ < 1_000_000)
                    {
                        loop.Add(cur);
                        if (!outgoing.TryGetValue(cur, out var co) || co.Count == 0) { loop = null!; break; }
                        int next;
                        if (co.Count == 1) next = co[0];
                        else
                        {
                            var din = (P2(cur) - P2(prev)).Normalized;
                            double bestAng = double.NegativeInfinity; next = co[0];
                            foreach (var w in co)
                            {
                                var dout = (P2(w) - P2(cur)).Normalized;
                                var ang = Math.Atan2(Vec2.Cross(din, dout), Vec2.Dot(din, dout));
                                if (ang > bestAng) { bestAng = ang; next = w; }
                            }
                        }
                        co.Remove(next);
                        PickTag(ref tag, cur, next);
                        prev = cur; cur = next;
                    }
                    if (loop != null && loop.Count >= 3) loops.Add((loop, tag!));
                }
            }

            void PickTag(ref FaceTag? tag, int a, int b)
            {
                if (edgeTag.TryGetValue((a, b), out var t) && (tag == null || t.Seq < tag.Seq)) tag = t;
            }

            // simplify and classify
            var outers = new List<(Vec3[] pts3, Vec2[] pts2, double area, FaceTag tag, List<Vec3[]> holes)>();
            var holes = new List<(Vec3[] pts3, Vec2[] pts2)>();
            foreach (var (ids, tag) in loops)
            {
                var simp = Simplify(ids, P2);
                if (simp.Count < 3) continue;
                var p2 = simp.Select(P2).ToArray();
                var area = SignedArea(p2);
                if (Math.Abs(area) < 1e-12) continue;
                var p3 = simp.Select(i => pts[i]).ToArray();
                if (area > 0) outers.Add((p3, p2, area, tag ?? g.Polys[0].Tag, new List<Vec3[]>()));
                else holes.Add((p3, p2));
            }
            outers.Sort((a, b) => a.area.CompareTo(b.area)); // smallest first for containment search
            foreach (var h in holes)
            {
                var probe = h.pts2[0];
                // use midpoint of first edge nudged inward to avoid touching vertices
                foreach (var o in outers)
                {
                    if (PointInPolygon(o.pts2, probe) || PointInPolygon(o.pts2, Vec2.Lerp(h.pts2[0], h.pts2[1], 0.5)))
                    { o.holes.Add(h.pts3); break; }
                }
            }
            foreach (var o in outers)
            {
                var ls = new List<Vec3[]> { o.pts3 };
                ls.AddRange(o.holes);
                faces.Add(new Face(g.Plane, o.tag, ls));
            }
        }
        return new Solid(faces);
    }

    static List<int> Simplify(List<int> ids, Func<int, Vec2> p2)
    {
        var l = new List<int>(ids);
        bool changed = true;
        int guard = 0;
        while (changed && l.Count >= 3 && guard++ < 100)
        {
            changed = false;
            for (int i = 0; i < l.Count && l.Count >= 3; i++)
            {
                var a = p2(l[(i - 1 + l.Count) % l.Count]); var b = p2(l[i]); var c = p2(l[(i + 1) % l.Count]);
                var ab = b - a; var bc = c - b;
                var lab = ab.Length; var lbc = bc.Length;
                if (lab < 1e-9 || lbc < 1e-9) { l.RemoveAt(i); i--; changed = true; continue; }
                var cross = Vec2.Cross(ab, bc);
                // distance of b from line ac
                if (Math.Abs(cross) / Math.Max(lab + lbc, 1e-12) < 1e-7) { l.RemoveAt(i); i--; changed = true; }
            }
        }
        return l;
    }

    public static double SignedArea(Vec2[] p)
    {
        double a = 0;
        for (int i = 0, j = p.Length - 1; i < p.Length; j = i++) a += p[j].X * p[i].Y - p[i].X * p[j].Y;
        return a * 0.5;
    }

    public static bool PointInPolygon(Vec2[] poly, Vec2 p)
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
