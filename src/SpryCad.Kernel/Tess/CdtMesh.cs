namespace SpryCad.Kernel;

/// <summary>
/// Small constrained Delaunay triangulation: starts from any valid triangulation of a polygon with holes,
/// legalizes it with Lawson flips (boundary edges are constraints), and supports incremental insertion of
/// interior points. Coordinates are supplied in a metric space so anisotropic surfaces mesh well.
/// Triangles are kept in flat arrays (three vertices, ccw, and the three neighbours across the edges opposite them,
/// -1 = none): a flip or an insertion rewrites entries in place.
/// </summary>
public sealed class CdtMesh
{
    readonly List<Vec2> _p;
    int[] _tv = new int[48];    // vertices of triangle i: 3i..3i+2
    int[] _tn = new int[48];    // neighbour across the edge opposite vertex e of triangle i: 3i+e
    int _count;
    readonly HashSet<long> _fixed = new();
    readonly Stack<(int, int)> _work = new();
    int _last;

    public CdtMesh(List<Vec2> metricPoints, IEnumerable<(int a, int b, int c)> tris, HashSet<(int, int)> constrained)
    {
        _p = metricPoints;
        foreach (var (u, v) in constrained) _fixed.Add(Key(u, v));
        foreach (var (a, b, c) in tris)
        {
            if (Orient(a, b, c) <= 0) continue; // drop degenerate / inverted input
            Add(a, b, c, -1, -1, -1);
        }
        var map = new Dictionary<long, (int tri, int edge)>();
        for (int i = 0; i < _count; i++)
            for (int e = 0; e < 3; e++)
            {
                int u = _tv[3 * i + (e + 1) % 3], v = _tv[3 * i + (e + 2) % 3];
                if (map.TryGetValue(Directed(v, u), out var o)) { _tn[3 * i + e] = o.tri; _tn[3 * o.tri + o.edge] = i; }
                else map[Directed(u, v)] = (i, e);
            }
    }

    public IEnumerable<(int a, int b, int c)> Triangles
    {
        get { for (int i = 0; i < _count; i++) yield return (_tv[3 * i], _tv[3 * i + 1], _tv[3 * i + 2]); }
    }

    public int Count => _count;

    void Add(int a, int b, int c, int n0, int n1, int n2)
    {
        if (3 * _count + 3 > _tv.Length) { Array.Resize(ref _tv, _tv.Length * 2); Array.Resize(ref _tn, _tn.Length * 2); }
        Set(_count++, a, b, c, n0, n1, n2);
    }

    void Set(int i, int a, int b, int c, int n0, int n1, int n2)
    {
        _tv[3 * i] = a; _tv[3 * i + 1] = b; _tv[3 * i + 2] = c;
        _tn[3 * i] = n0; _tn[3 * i + 1] = n1; _tn[3 * i + 2] = n2;
    }

    double Orient(int a, int b, int c) => Vec2.Cross(_p[b] - _p[a], _p[c] - _p[a]);

    static long Key(int a, int b) => a < b ? Directed(a, b) : Directed(b, a);
    static long Directed(int a, int b) => ((long)a << 32) | (uint)b;

    bool InCircle(int a, int b, int c, int d)
    {
        Vec2 pa = _p[a], pb = _p[b], pc = _p[c], pd = _p[d];
        double adx = pa.X - pd.X, ady = pa.Y - pd.Y, bdx = pb.X - pd.X, bdy = pb.Y - pd.Y, cdx = pc.X - pd.X, cdy = pc.Y - pd.Y;
        double det = (adx * adx + ady * ady) * (bdx * cdy - cdx * bdy) - (bdx * bdx + bdy * bdy) * (adx * cdy - cdx * ady) + (cdx * cdx + cdy * cdy) * (adx * bdy - bdx * ady);
        var s = adx * adx + ady * ady + bdx * bdx + bdy * bdy + cdx * cdx + cdy * cdy;
        return det > 1e-12 * s * s;
    }

    /// <summary>Flips edge e of triangle i if that improves the Delaunay property. Returns true if flipped.</summary>
    bool Legalize(int i, int e)
    {
        var j = _tn[3 * i + e];
        if (j < 0) return false;
        int a = _tv[3 * i + e], b = _tv[3 * i + (e + 1) % 3], c = _tv[3 * i + (e + 2) % 3];  // edge (b,c) opposite a
        if (_fixed.Contains(Key(b, c))) return false;
        // vertex of j opposite the shared edge
        int j0 = _tv[3 * j], j1 = _tv[3 * j + 1], j2 = _tv[3 * j + 2];
        int d = j0 != b && j0 != c ? j0 : j1 != b && j1 != c ? j1 : j2;
        if (!InCircle(a, b, c, d)) return false;
        // convexity (new triangles must be ccw)
        if (Orient(a, b, d) <= 0 || Orient(a, d, c) <= 0) return false;
        // neighbours of the quad
        int nAB = NeighbourAcross(i, a, b), nCA = NeighbourAcross(i, c, a);
        int nBD = NeighbourAcross(j, b, d), nDC = NeighbourAcross(j, d, c);
        // triangle i: edges (b,d) opp a -> nBD, (d,a) opp b -> j, (a,b) opp d -> nAB
        Set(i, a, b, d, nBD, j, nAB);
        // triangle j: edges (d,c) opp a -> nDC, (c,a) opp d -> nCA, (a,d) opp c -> i
        Set(j, a, d, c, nDC, nCA, i);
        Relink(nBD, j, i); Relink(nCA, i, j);
        _work.Push((i, 0)); _work.Push((j, 0));
        return true;
    }

    int NeighbourAcross(int tri, int u, int v)
    {
        for (int e = 0; e < 3; e++)
        {
            int x = _tv[3 * tri + (e + 1) % 3], y = _tv[3 * tri + (e + 2) % 3];
            if ((x == u && y == v) || (x == v && y == u)) return _tn[3 * tri + e];
        }
        return -1;
    }

    void Relink(int tri, int oldN, int newN)
    {
        if (tri < 0) return;
        for (int e = 0; e < 3; e++) if (_tn[3 * tri + e] == oldN) { _tn[3 * tri + e] = newN; return; }
    }

    public void MakeDelaunay()
    {
        _work.Clear();
        for (int i = 0; i < _count; i++) for (int e = 0; e < 3; e++) _work.Push((i, e));
        int guard = 0;
        while (_work.Count > 0 && guard++ < 2_000_000)
        {
            var (i, e) = _work.Pop();
            for (int k = 0; k < 3; k++) if (Legalize(i, (e + k) % 3)) break;
        }
        _work.Clear();
    }

    /// <summary>Locates the triangle containing p (walking), or -1 when outside the domain.</summary>
    int Locate(Vec2 p)
    {
        if (_count == 0) return -1;
        int cur = Math.Clamp(_last, 0, _count - 1);
        for (int steps = 0; steps < _count + 10; steps++)
        {
            int next = -2;
            for (int e = 0; e < 3; e++)
            {
                var a = _p[_tv[3 * cur + (e + 1) % 3]]; var b = _p[_tv[3 * cur + (e + 2) % 3]];
                if (Vec2.Cross(b - a, p - a) < -1e-12 * ((b - a).LengthSq + 1e-30))
                {
                    next = _tn[3 * cur + e];
                    break;
                }
            }
            if (next == -2) { _last = cur; return cur; }
            if (next < 0) break; // walked out of the domain (holes/boundary): fall back to a scan
            cur = next;
        }
        for (int i = 0; i < _count; i++)
        {
            Vec2 p0 = _p[_tv[3 * i]], p1 = _p[_tv[3 * i + 1]], p2 = _p[_tv[3 * i + 2]];
            if (Vec2.Cross(p1 - p0, p - p0) >= 0 && Vec2.Cross(p2 - p1, p - p1) >= 0 && Vec2.Cross(p0 - p2, p - p2) >= 0)
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
        int a = _tv[3 * i], b = _tv[3 * i + 1], c = _tv[3 * i + 2];
        // reject points too close to an existing vertex
        if ((_p[a] - p).LengthSq < 1e-20 || (_p[b] - p).LengthSq < 1e-20 || (_p[c] - p).LengthSq < 1e-20) return false;
        int n0 = _tn[3 * i], n1 = _tn[3 * i + 1], n2 = _tn[3 * i + 2];   // across (b,c), (c,a), (a,b)
        int i1 = _count, i2 = _count + 1;
        // tri i (a,b,k): edge (b,k) opp a -> i1, (k,a) opp b -> i2, (a,b) opp k -> n2
        Set(i, a, b, k, i1, i2, n2);
        Add(b, c, k, i2, i, n0);   // (b,c,k): (c,k) opp b -> i2, (k,b) opp c -> i, (b,c) opp k -> n0
        Add(c, a, k, i, i1, n1);   // (c,a,k): (a,k) opp c -> i, (k,c) opp a -> i1, (c,a) opp k -> n1
        Relink(n0, i, i1); Relink(n1, i, i2);
        _work.Clear();
        _work.Push((i, 2)); _work.Push((i1, 2)); _work.Push((i2, 2));
        int guard = 0;
        while (_work.Count > 0 && guard++ < 100000)
        {
            var (ti, e) = _work.Pop();
            Legalize(ti, e);
        }
        _work.Clear();
        return true;
    }
}
