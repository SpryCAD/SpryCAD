namespace FeatherCad.Kernel;

public readonly record struct Tri(Vec3 A, Vec3 B, Vec3 C);

/// <summary>
/// Ear-clipping triangulation of planar polygons with holes (hole bridging + ear clipping,
/// in the spirit of mapbox/earcut).
/// </summary>
public static class Triangulator
{
    public static List<Tri> TriangulateFace(Face f) => TriangulateLoops(f.Loops, f.Plane.Normal);

    public static List<Tri> TriangulateLoops(IReadOnlyList<Vec3[]> loops, Vec3 normal)
    {
        var all = new List<Vec3>();
        var loops2 = new List<Vec2[]>();
        var u = normal.AnyPerpendicular();
        var v = Vec3.Cross(normal, u);
        foreach (var l in loops)
        {
            var arr = new Vec2[l.Length];
            for (int i = 0; i < l.Length; i++) { arr[i] = new Vec2(Vec3.Dot(l[i], u), Vec3.Dot(l[i], v)); all.Add(l[i]); }
            loops2.Add(arr);
        }
        var idx = Triangulate(loops2);
        var res = new List<Tri>(idx.Count / 3);
        for (int i = 0; i < idx.Count; i += 3) res.Add(new Tri(all[idx[i]], all[idx[i + 1]], all[idx[i + 2]]));
        return res;
    }

    // ---- 2D core -------------------------------------------------------------------------

    sealed class Node
    {
        public int I; public double X, Y; public Node Prev = null!, Next = null!;
    }

    static double Area(Vec2[] l)
    {
        double a = 0;
        for (int i = 0, j = l.Length - 1; i < l.Length; j = i++) a += l[j].X * l[i].Y - l[i].X * l[j].Y;
        return a * 0.5; // positive for CCW
    }

    /// <summary>
    /// Triangulates loops (first = outer, rest = holes). Indices refer to the concatenated loop vertices.
    /// Orientation of the input is normalized internally. Output triangles are CCW.
    /// </summary>
    public static List<int> Triangulate(IReadOnlyList<Vec2[]> loops)
    {
        var result = new List<int>();
        if (loops.Count == 0 || loops[0].Length < 3) return result;
        int offset = 0;
        var outer = BuildRing(loops[0], offset, ccw: true);
        offset += loops[0].Length;
        if (outer == null) return result;

        if (loops.Count > 1)
        {
            var holes = new List<Node>();
            for (int h = 1; h < loops.Count; h++)
            {
                var ring = BuildRing(loops[h], offset, ccw: false);
                offset += loops[h].Length;
                if (ring == null) continue;
                holes.Add(Rightmost(ring));
            }
            holes.Sort((a, b) => b.X.CompareTo(a.X));
            foreach (var h in holes)
            {
                var bridged = EliminateHole(h, outer);
                if (bridged != null) outer = bridged;
            }
        }
        EarClip(outer, result);
        return result;
    }

    static Node? BuildRing(Vec2[] pts, int offset, bool ccw)
    {
        if (pts.Length < 3) return null;
        bool isCcw = Area(pts) > 0;
        Node? first = null, last = null;
        void Add(int i)
        {
            var n = new Node { I = offset + i, X = pts[i].X, Y = pts[i].Y };
            if (last == null) { n.Prev = n; n.Next = n; first = n; }
            else { n.Next = last.Next; n.Prev = last; last.Next.Prev = n; last.Next = n; }
            last = n;
        }
        if (isCcw == ccw) for (int i = 0; i < pts.Length; i++) Add(i);
        else for (int i = pts.Length - 1; i >= 0; i--) Add(i);
        return RemoveDuplicates(first!);
    }

    static Node? RemoveDuplicates(Node start)
    {
        var p = start; int guard = 0;
        int count = 0; var q = start; do { count++; q = q.Next; } while (q != start);
        while (count > 2 && guard++ < 100000)
        {
            if (Same(p, p.Next)) { Remove(p.Next); count--; if (p.Next == start || p == start) start = p; continue; }
            p = p.Next;
            if (p == start) break;
        }
        return count >= 3 ? start : null;
    }

    static bool Same(Node a, Node b) => Math.Abs(a.X - b.X) < 1e-12 && Math.Abs(a.Y - b.Y) < 1e-12;

    static void Remove(Node n) { n.Prev.Next = n.Next; n.Next.Prev = n.Prev; }

    static Node Rightmost(Node ring)
    {
        var p = ring; var best = ring;
        do { if (p.X > best.X || (p.X == best.X && p.Y < best.Y)) best = p; p = p.Next; } while (p != ring);
        return best;
    }

    static Node? EliminateHole(Node m, Node outer)
    {
        var bridge = FindBridge(m, outer);
        if (bridge == null) return null;
        // split: bridge -> m ... m2 -> bridge2 -> bridge.next
        var b2 = new Node { I = bridge.I, X = bridge.X, Y = bridge.Y };
        var m2 = new Node { I = m.I, X = m.X, Y = m.Y };
        var bNext = bridge.Next; var mPrev = m.Prev;
        bridge.Next = m; m.Prev = bridge;
        m2.Next = b2; b2.Prev = m2;
        mPrev.Next = m2; m2.Prev = mPrev;
        b2.Next = bNext; bNext.Prev = b2;
        return bridge;
    }

    static Node? FindBridge(Node m, Node outer)
    {
        var p = outer;
        double hx = m.X, hy = m.Y, qx = double.PositiveInfinity;
        Node? cand = null;
        do
        {
            var a = p; var b = p.Next;
            if (a.Y != b.Y && ((hy <= a.Y && hy >= b.Y) || (hy <= b.Y && hy >= a.Y)))
            {
                var x = a.X + (hy - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                if (x >= hx - 1e-12 && x < qx)
                {
                    qx = x;
                    cand = a.X > b.X ? a : b;
                }
            }
            p = p.Next;
        } while (p != outer);
        if (cand == null) return null;

        // vertices inside triangle (m, I, cand) could block visibility: choose the one with smallest angle
        var best = cand; double tanMin = double.PositiveInfinity;
        p = outer;
        do
        {
            if (p != cand && p.X >= hx && InTriAny(hx, hy, qx, hy, cand.X, cand.Y, p.X, p.Y))
            {
                var dx = p.X - hx;
                var tan = dx > 1e-300 ? Math.Abs(hy - p.Y) / dx : double.PositiveInfinity;
                if (Locally(p, m) && (tan < tanMin || (tan == tanMin && p.X > best.X)))
                { best = p; tanMin = tan; }
            }
            p = p.Next;
        } while (p != outer);
        return best;
    }

    static bool InTriAny(double ax, double ay, double bx, double by, double cx, double cy, double px, double py)
    {
        var d1 = (bx - ax) * (py - ay) - (by - ay) * (px - ax);
        var d2 = (cx - bx) * (py - by) - (cy - by) * (px - bx);
        var d3 = (ax - cx) * (py - cy) - (ay - cy) * (px - cx);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    // whether the diagonal a->b starts inside the polygon's interior angle at a (CCW polygon)
    static bool Locally(Node a, Node b)
    {
        if (Cross(a.Prev, a, a.Next) >= 0)
            return Cross(a, a.Next, b) >= 0 && Cross(a, b, a.Prev) >= 0;
        return Cross(a, a.Prev, b) <= 0 || Cross(a, b, a.Next) <= 0;
    }

    // >0 if p,q,r is CCW
    static double Cross(Node p, Node q, Node r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);

    static bool IsEar(Node ear)
    {
        var a = ear.Prev; var b = ear; var c = ear.Next;
        var area = Cross(a, b, c);
        var scale = Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y) + Math.Abs(c.X - b.X) + Math.Abs(c.Y - b.Y);
        if (area <= 1e-14 * scale * scale) return false; // reflex or degenerate
        var p = c.Next;
        while (p != a)
        {
            if (!Same(p, a) && !Same(p, b) && !Same(p, c) &&
                PointInTriangleStrict(a, b, c, p))
                return false;
            p = p.Next;
        }
        return true;
    }

    static bool PointInTriangleStrict(Node a, Node b, Node c, Node p)
    {
        // inclusive of edges so points on edges block ears (prevents overlaps at bridges)
        var d1 = Cross(a, b, p); var d2 = Cross(b, c, p); var d3 = Cross(c, a, p);
        return d1 >= 0 && d2 >= 0 && d3 >= 0;
    }

    static void EarClip(Node start, List<int> tris)
    {
        var ear = start;
        int count = 0; var q = start; do { count++; q = q.Next; } while (q != start);
        int stall = 0;
        int pass = 0;
        while (count > 3)
        {
            if (IsEar(ear))
            {
                tris.Add(ear.Prev.I); tris.Add(ear.I); tris.Add(ear.Next.I);
                Remove(ear);
                count--;
                ear = ear.Next;
                stall = 0;
                continue;
            }
            ear = ear.Next;
            if (++stall >= count)
            {
                // no ear found: drop degenerate (collinear/duplicate) vertices, then force-clip
                stall = 0; pass++;
                var p = ear; bool removed = false;
                for (int k = 0; k < count && count > 3; k++)
                {
                    var area = Cross(p.Prev, p, p.Next);
                    if (Math.Abs(area) < 1e-12 || Same(p, p.Next))
                    {
                        Remove(p); count--; removed = true; p = p.Next;
                    }
                    else p = p.Next;
                }
                ear = p;
                if (!removed || pass > 4)
                {
                    // force clip the most convex vertex
                    Node best = ear; double bestA = double.NegativeInfinity; p = ear;
                    for (int k = 0; k < count; k++) { var a = Cross(p.Prev, p, p.Next); if (a > bestA) { bestA = a; best = p; } p = p.Next; }
                    if (bestA > 0) { tris.Add(best.Prev.I); tris.Add(best.I); tris.Add(best.Next.I); }
                    Remove(best); count--; ear = best.Next;
                }
            }
        }
        if (count == 3 && Cross(ear.Prev, ear, ear.Next) > 0)
        { tris.Add(ear.Prev.I); tris.Add(ear.I); tris.Add(ear.Next.I); }
    }
}
