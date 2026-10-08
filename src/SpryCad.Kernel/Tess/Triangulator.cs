namespace SpryCad.Kernel;

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
        // z-order index (long rings, see EarClip): Morton code of the point, neighbours in code order
        public int Z; public Node? PrevZ, NextZ;
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

    static void Remove(Node n)
    {
        n.Prev.Next = n.Next; n.Next.Prev = n.Prev;
        if (n.PrevZ != null) n.PrevZ.NextZ = n.NextZ;
        if (n.NextZ != null) n.NextZ.PrevZ = n.PrevZ;
    }

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

    static bool IsEar(Node ear, ZIndex? zi, bool relaxed = false)
    {
        var a = ear.Prev; var b = ear; var c = ear.Next;
        var area = Cross(a, b, c);
        var scale = Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y) + Math.Abs(c.X - b.X) + Math.Abs(c.Y - b.Y);
        if (area <= 1e-14 * scale * scale) return false; // reflex or degenerate
        if (zi != null && zi.BlockBox(a, b, c) is { } box)
        {
            // only points in the box can block: those of the ring with their codes in the box's range, a run of the
            // code order through the ear itself
            int zMin = zi.Code(box.x0, box.y0), zMax = zi.Code(box.x1, box.y1);
            for (var p = b.PrevZ; p != null && p.Z >= zMin; p = p.PrevZ)
                if (p != a && p != c && Blocks(p)) return false;
            for (var p = b.NextZ; p != null && p.Z <= zMax; p = p.NextZ)
                if (p != a && p != c && Blocks(p)) return false;
            return true;

            bool Blocks(Node p) => p.X >= box.x0 && p.X <= box.x1 && p.Y >= box.y0 && p.Y <= box.y1 &&
                !Same(p, a) && !Same(p, b) && !Same(p, c) && (relaxed ? BlocksRelaxed(a, b, c, p) : PointInTriangleStrict(a, b, c, p));
        }
        var q = c.Next;
        while (q != a)
        {
            if (!Same(q, a) && !Same(q, b) && !Same(q, c) &&
                (relaxed ? BlocksRelaxed(a, b, c, q) : PointInTriangleStrict(a, b, c, q)))
                return false;
            q = q.Next;
        }
        return true;
    }

    /// <summary>
    /// The ring's points in z-order (Morton codes of their positions in the ring's box): the points inside a box are
    /// those of its code range, so an ear tests only its neighbourhood instead of the whole ring (earcut's hashing).
    /// </summary>
    sealed class ZIndex
    {
        readonly double _x0, _y0, _inv, _size;

        public ZIndex(Node start)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            var nodes = new List<Node>();
            var p = start;
            do { nodes.Add(p); x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); p = p.Next; } while (p != start);
            _x0 = x0; _y0 = y0; _size = Math.Max(x1 - x0, y1 - y0);
            _inv = _size > 0 ? 32767 / _size : 0;
            foreach (var n in nodes) n.Z = Code(n.X, n.Y);
            nodes.Sort((m, n) => m.Z.CompareTo(n.Z));
            for (int i = 0; i < nodes.Count; i++)
            {
                nodes[i].PrevZ = i > 0 ? nodes[i - 1] : null;
                nodes[i].NextZ = i + 1 < nodes.Count ? nodes[i + 1] : null;
            }
        }

        /// <summary>Morton code of a point (coordinates clamped to the ring's box: non-decreasing in x and in y).</summary>
        public int Code(double x, double y)
        {
            int ix = (int)Math.Clamp((x - _x0) * _inv, 0, 32767), iy = (int)Math.Clamp((y - _y0) * _inv, 0, 32767);
            return (int)(Spread((uint)ix) | (Spread((uint)iy) << 1));
        }

        static uint Spread(uint v)
        {
            v = (v | (v << 8)) & 0x00FF00FF;
            v = (v | (v << 4)) & 0x0F0F0F0F;
            v = (v | (v << 2)) & 0x33333333;
            return (v | (v << 1)) & 0x55555555;
        }

        /// <summary>
        /// A box holding every point that <see cref="PointInTriangleStrict"/> (and so <see cref="BlocksRelaxed"/>) can
        /// count as blocking ear a-b-c: the triangle its tolerances allow (each side moved out by its slack), with a
        /// margin for rounding. Null for a sliver, whose moved corners are unreliable (then the whole ring is tested).
        /// </summary>
        public (double x0, double y0, double x1, double y1)? BlockBox(Node a, Node b, Node c)
        {
            var l2 = Math.Max(Math.Max((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y), (c.X - b.X) * (c.X - b.X) + (c.Y - b.Y) * (c.Y - b.Y)),
                              (a.X - c.X) * (a.X - c.X) + (a.Y - c.Y) * (a.Y - c.Y));
            var eps = -1e-11 * l2;
            // in coordinates about b: side i is A·x + B·y = K where its cross product equals eps
            double ax = a.X - b.X, ay = a.Y - b.Y, cx = c.X - b.X, cy = c.Y - b.Y;
            double A1 = ay, B1 = -ax, K1 = eps;                              // (b - a) × (p - a)
            double A2 = -cy, B2 = cx, K2 = eps;                              // (c - b) × (p - b)
            double A3 = -(ay - cy), B3 = ax - cx, K3 = eps + A3 * cx + B3 * cy; // (a - c) × (p - c)
            if (!Corner(A1, B1, K1, A2, B2, K2, out var x12, out var y12) ||
                !Corner(A2, B2, K2, A3, B3, K3, out var x23, out var y23) ||
                !Corner(A3, B3, K3, A1, B1, K1, out var x31, out var y31)) return null;
            double x0 = Math.Min(x12, Math.Min(x23, x31)), x1 = Math.Max(x12, Math.Max(x23, x31));
            double y0 = Math.Min(y12, Math.Min(y23, y31)), y1 = Math.Max(y12, Math.Max(y23, y31));
            var m = 1e-6 * (x1 - x0 + y1 - y0) + 1e-14 * (_size + Math.Abs(b.X - _x0) + Math.Abs(b.Y - _y0));
            return (b.X + x0 - m, b.Y + y0 - m, b.X + x1 + m, b.Y + y1 + m);
        }

        static bool Corner(double a1, double b1, double k1, double a2, double b2, double k2, out double x, out double y)
        {
            var det = a1 * b2 - a2 * b1;
            x = y = 0;
            if (!(Math.Abs(det) >= 1e-8 * Math.Sqrt((a1 * a1 + b1 * b1) * (a2 * a2 + b2 * b2)))) return false;
            x = (k1 * b2 - k2 * b1) / det; y = (a1 * k2 - a2 * k1) / det;
            return double.IsFinite(x) && double.IsFinite(y);
        }
    }

    /// <summary>
    /// Relaxed blocking test: only points inside the ear or on its new diagonal (c→a) block it; points merely
    /// touching the two polygon sides (a→b, b→c) do not (e.g. exactly collinear boundary runs).
    /// </summary>
    static bool BlocksRelaxed(Node a, Node b, Node c, Node p)
    {
        var d1 = Cross(a, b, p); var d2 = Cross(b, c, p); var d3 = Cross(c, a, p);
        if (d1 < 0 || d2 < 0 || d3 < 0) return false;
        if (d1 > 0 && d2 > 0) return true;    // inside, or on the diagonal
        return false;                          // on a side
    }

    static bool PointInTriangleStrict(Node a, Node b, Node c, Node p)
    {
        // inclusive of edges so points on edges block ears (prevents overlaps at bridges); "on" allows for
        // rounding (a boundary run that is collinear in exact arithmetic must not be cut across)
        var d1 = Cross(a, b, p); var d2 = Cross(b, c, p); var d3 = Cross(c, a, p);
        var l2 = Math.Max(Math.Max((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y), (c.X - b.X) * (c.X - b.X) + (c.Y - b.Y) * (c.Y - b.Y)),
                          (a.X - c.X) * (a.X - c.X) + (a.Y - c.Y) * (a.Y - c.Y));
        var eps = -1e-11 * l2;
        return d1 >= eps && d2 >= eps && d3 >= eps;
    }

    static void EarClip(Node start, List<int> tris)
    {
        var ear = start;
        int count = 0; var q = start; do { count++; q = q.Next; } while (q != start);
        // long rings: each ear tests only the points near it (the same answers as testing every point)
        var zi = count > 80 ? new ZIndex(start) : null;
        int stall = 0;
        int pass = 0;
        while (count > 3)
        {
            if (IsEar(ear, zi))
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
                // no ear found: try the relaxed test first (keeps every boundary vertex)
                stall = 0;
                var r = ear; Node? relaxedEar = null;
                for (int k = 0; k < count; k++) { if (IsEar(r, zi, relaxed: true)) { relaxedEar = r; break; } r = r.Next; }
                if (relaxedEar != null)
                {
                    tris.Add(relaxedEar.Prev.I); tris.Add(relaxedEar.I); tris.Add(relaxedEar.Next.I);
                    Remove(relaxedEar); count--; ear = relaxedEar.Next;
                    continue;
                }
                // then drop degenerate (collinear/duplicate) vertices, then force-clip
                pass++;
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
