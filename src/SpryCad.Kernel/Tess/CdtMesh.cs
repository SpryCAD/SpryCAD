namespace SpryCad.Kernel;

/// <summary>
/// Small constrained Delaunay triangulation: starts from any valid triangulation of a polygon with holes,
/// legalizes it with Lawson flips (boundary edges are constraints), and supports incremental insertion of
/// interior points. Coordinates are supplied in a metric space so anisotropic surfaces mesh well.
/// </summary>
public sealed class CdtMesh
{
    readonly List<Vec2> _p;
    readonly List<int[]> _t = new();       // vertices (ccw)
    readonly List<int[]> _n = new();       // neighbour across edge i (opposite vertex i), -1 = none
    readonly HashSet<(int, int)> _fixed;
    int _last;

    public CdtMesh(List<Vec2> metricPoints, IEnumerable<(int a, int b, int c)> tris, HashSet<(int, int)> constrained)
    {
        _p = metricPoints; _fixed = constrained;
        foreach (var (a, b, c) in tris)
        {
            if (Orient(a, b, c) <= 0) continue; // drop degenerate / inverted input
            _t.Add(new[] { a, b, c });
            _n.Add(new[] { -1, -1, -1 });
        }
        var map = new Dictionary<(int, int), (int tri, int edge)>();
        for (int i = 0; i < _t.Count; i++)
            for (int e = 0; e < 3; e++)
            {
                var (u, v) = (_t[i][(e + 1) % 3], _t[i][(e + 2) % 3]);
                if (map.TryGetValue((v, u), out var o)) { _n[i][e] = o.tri; _n[o.tri][o.edge] = i; }
                else map[(u, v)] = (i, e);
            }
    }

    public IEnumerable<(int a, int b, int c)> Triangles => _t.Select(t => (t[0], t[1], t[2]));
    public int Count => _t.Count;

    double Orient(int a, int b, int c) => Vec2.Cross(_p[b] - _p[a], _p[c] - _p[a]);

    static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

    bool InCircle(int a, int b, int c, int d)
    {
        Vec2 pa = _p[a], pb = _p[b], pc = _p[c], pd = _p[d];
        double adx = pa.X - pd.X, ady = pa.Y - pd.Y, bdx = pb.X - pd.X, bdy = pb.Y - pd.Y, cdx = pc.X - pd.X, cdy = pc.Y - pd.Y;
        double det = (adx * adx + ady * ady) * (bdx * cdy - cdx * bdy) - (bdx * bdx + bdy * bdy) * (adx * cdy - cdx * ady) + (cdx * cdx + cdy * cdy) * (adx * bdy - bdx * ady);
        var s = adx * adx + ady * ady + bdx * bdx + bdy * bdy + cdx * cdx + cdy * cdy;
        return det > 1e-12 * s * s;
    }

    /// <summary>Flips edge e of triangle i if that improves the Delaunay property. Returns true if flipped.</summary>
    bool Legalize(int i, int e, Stack<(int, int)> work)
    {
        var j = _n[i][e];
        if (j < 0) return false;
        var t = _t[i];
        int a = t[e], b = t[(e + 1) % 3], c = t[(e + 2) % 3];  // edge (b,c) opposite a
        if (_fixed.Contains(Key(b, c))) return false;
        var tj = _t[j];
        // vertex of j opposite the shared edge
        int d = tj[0] != b && tj[0] != c ? tj[0] : tj[1] != b && tj[1] != c ? tj[1] : tj[2];
        if (!InCircle(a, b, c, d)) return false;
        // convexity (new triangles must be ccw)
        if (Orient(a, b, d) <= 0 || Orient(a, d, c) <= 0) return false;
        // neighbours of the quad
        int nAB = NeighbourAcross(i, a, b), nCA = NeighbourAcross(i, c, a);
        int nBD = NeighbourAcross(j, b, d), nDC = NeighbourAcross(j, d, c);
        _t[i] = new[] { a, b, d }; _t[j] = new[] { a, d, c };
        // triangle i: edges (b,d) opp a -> nBD, (d,a) opp b -> j, (a,b) opp d -> nAB
        _n[i] = new[] { nBD, j, nAB };
        // triangle j: edges (d,c) opp a -> nDC, (c,a) opp d -> nCA, (a,d) opp c -> i
        _n[j] = new[] { nDC, nCA, i };
        Relink(nBD, j, i); Relink(nCA, i, j);
        work.Push((i, 0)); work.Push((j, 0));
        return true;
    }

    int NeighbourAcross(int tri, int u, int v)
    {
        var t = _t[tri];
        for (int e = 0; e < 3; e++)
        {
            int x = t[(e + 1) % 3], y = t[(e + 2) % 3];
            if ((x == u && y == v) || (x == v && y == u)) return _n[tri][e];
        }
        return -1;
    }

    void Relink(int tri, int oldN, int newN)
    {
        if (tri < 0) return;
        for (int e = 0; e < 3; e++) if (_n[tri][e] == oldN) { _n[tri][e] = newN; return; }
    }

    public void MakeDelaunay()
    {
        var work = new Stack<(int, int)>();
        for (int i = 0; i < _t.Count; i++) for (int e = 0; e < 3; e++) work.Push((i, e));
        int guard = 0;
        while (work.Count > 0 && guard++ < 2_000_000)
        {
            var (i, e) = work.Pop();
            for (int k = 0; k < 3; k++) if (Legalize(i, (e + k) % 3, work)) break;
        }
    }

    /// <summary>Locates the triangle containing p (walking), or -1 when outside the domain.</summary>
    int Locate(Vec2 p)
    {
        if (_t.Count == 0) return -1;
        int cur = Math.Clamp(_last, 0, _t.Count - 1);
        for (int steps = 0; steps < _t.Count + 10; steps++)
        {
            var t = _t[cur];
            int next = -2;
            for (int e = 0; e < 3; e++)
            {
                var a = _p[t[(e + 1) % 3]]; var b = _p[t[(e + 2) % 3]];
                if (Vec2.Cross(b - a, p - a) < -1e-12 * ((b - a).LengthSq + 1e-30))
                {
                    next = _n[cur][e];
                    break;
                }
            }
            if (next == -2) { _last = cur; return cur; }
            if (next < 0) break; // walked out of the domain (holes/boundary): fall back to a scan
            cur = next;
        }
        for (int i = 0; i < _t.Count; i++)
        {
            var t = _t[i];
            if (Vec2.Cross(_p[t[1]] - _p[t[0]], p - _p[t[0]]) >= 0 && Vec2.Cross(_p[t[2]] - _p[t[1]], p - _p[t[1]]) >= 0 && Vec2.Cross(_p[t[0]] - _p[t[2]], p - _p[t[2]]) >= 0)
            { _last = i; return i; }
        }
        return -1;
    }

    /// <summary>Inserts point index k (already in the point list) if it lies inside the domain.</summary>
    public bool Insert(int k)
    {
        var p = _p[k];
        int i = Locate(p);
        if (i < 0) return false;
        var t = _t[i];
        int a = t[0], b = t[1], c = t[2];
        // reject points too close to an existing vertex
        foreach (var v in t) if ((_p[v] - p).LengthSq < 1e-20) return false;
        int n0 = _n[i][0], n1 = _n[i][1], n2 = _n[i][2];   // across (b,c), (c,a), (a,b)
        int i1 = _t.Count, i2 = _t.Count + 1;
        _t[i] = new[] { a, b, k }; _t.Add(new[] { b, c, k }); _t.Add(new[] { c, a, k });
        // tri i (a,b,k): edge (b,k) opp a -> i1, (k,a) opp b -> i2, (a,b) opp k -> n2
        _n[i] = new[] { i1, i2, n2 };
        _n.Add(new[] { i2, i, n0 });   // (b,c,k): (c,k) opp b -> i2, (k,b) opp c -> i, (b,c) opp k -> n0
        _n.Add(new[] { i, i1, n1 });   // (c,a,k): (a,k) opp c -> i, (k,c) opp a -> i1, (c,a) opp k -> n1
        Relink(n0, i, i1); Relink(n1, i, i2);
        var work = new Stack<(int, int)>();
        work.Push((i, 2)); work.Push((i1, 2)); work.Push((i2, 2));
        int guard = 0;
        while (work.Count > 0 && guard++ < 100000)
        {
            var (ti, e) = work.Pop();
            Legalize(ti, e, work);
        }
        return true;
    }
}
