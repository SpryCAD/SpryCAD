namespace FeatherCad.Kernel;

/// <summary>Spatial hash that merges points closer than a tolerance into a single index.</summary>
public sealed class VertexWelder
{
    readonly double _tol, _tol2, _cell;
    readonly Dictionary<(long, long, long), List<int>> _grid = new();
    public readonly List<Vec3> Points = new();

    public VertexWelder(double tol = 1e-6)
    {
        _tol = tol; _tol2 = tol * tol; _cell = tol * 8;
    }

    (long, long, long) Key(Vec3 p) => ((long)Math.Floor(p.X / _cell), (long)Math.Floor(p.Y / _cell), (long)Math.Floor(p.Z / _cell));

    // most repeats are bit-identical copies (shared facet vertices): one lookup instead of 27 grid probes
    readonly Dictionary<(double, double, double), int> _exact = new();

    public int Add(Vec3 p)
    {
        if (_exact.TryGetValue((p.X, p.Y, p.Z), out var hit)) return hit;
        var r = AddNear(p);
        _exact[(p.X, p.Y, p.Z)] = r;
        return r;
    }

    int AddNear(Vec3 p)
    {
        var (kx, ky, kz) = Key(p);
        // a neighbouring cell can only hold a match if p is within the tolerance of the wall between them
        var reach = _tol * (1 + 1e-9);
        long x0 = p.X - kx * _cell <= reach ? kx - 1 : kx, x1 = (kx + 1) * _cell - p.X <= reach ? kx + 1 : kx;
        long y0 = p.Y - ky * _cell <= reach ? ky - 1 : ky, y1 = (ky + 1) * _cell - p.Y <= reach ? ky + 1 : ky;
        long z0 = p.Z - kz * _cell <= reach ? kz - 1 : kz, z1 = (kz + 1) * _cell - p.Z <= reach ? kz + 1 : kz;
        for (long x = x0; x <= x1; x++)
            for (long y = y0; y <= y1; y++)
                for (long z = z0; z <= z1; z++)
                    if (_grid.TryGetValue((x, y, z), out var list))
                        foreach (var i in list)
                            if ((Points[i] - p).LengthSq <= _tol2) return i;
        var idx = Points.Count;
        Points.Add(p);
        var k = (kx, ky, kz);
        if (!_grid.TryGetValue(k, out var l)) _grid[k] = l = new List<int>(2);
        l.Add(idx);
        return idx;
    }

    public double Tolerance => _tol;
}

/// <summary>Finds welded vertices lying in the interior of segments (T-junctions).</summary>
public sealed class SegmentVertexIndex
{
    readonly List<Vec3> _pts;
    readonly int[] _order; // indices sorted by X
    readonly double[] _xs;
    readonly double _tol;

    public SegmentVertexIndex(List<Vec3> pts, double tol)
    {
        _pts = pts; _tol = tol;
        _order = Enumerable.Range(0, pts.Count).ToArray();
        _xs = new double[pts.Count];
        Array.Sort(_order, (a, b) => pts[a].X.CompareTo(pts[b].X));
        for (int i = 0; i < _order.Length; i++) _xs[i] = pts[_order[i]].X;
    }

    /// <summary>Returns vertex ids strictly between a and b (within tol of the segment), sorted from a to b.</summary>
    public void Between(int ia, int ib, List<int> outIds, Func<int, bool>? filter = null)
    {
        outIds.Clear();
        var a = _pts[ia]; var b = _pts[ib];
        var d = b - a; var len2 = d.LengthSq;
        if (len2 < _tol * _tol) return;
        double minX = Math.Min(a.X, b.X) - _tol, maxX = Math.Max(a.X, b.X) + _tol;
        double minY = Math.Min(a.Y, b.Y) - _tol, maxY = Math.Max(a.Y, b.Y) + _tol;
        double minZ = Math.Min(a.Z, b.Z) - _tol, maxZ = Math.Max(a.Z, b.Z) + _tol;
        int lo = LowerBound(minX);
        List<(double t, int id)>? hits = null;
        for (int k = lo; k < _order.Length && _xs[k] <= maxX; k++)
        {
            int id = _order[k];
            if (id == ia || id == ib) continue;
            var p = _pts[id];
            if (p.Y < minY || p.Y > maxY || p.Z < minZ || p.Z > maxZ) continue;
            if (filter != null && !filter(id)) continue;
            var t = Vec3.Dot(p - a, d) / len2;
            if (t <= 0 || t >= 1) continue;
            var proj = a + d * t;
            if ((proj - p).LengthSq > _tol * _tol * 4) continue;
            (hits ??= new()).Add((t, id));
        }
        if (hits == null) return;
        hits.Sort((x, y) => x.t.CompareTo(y.t));
        foreach (var h in hits) outIds.Add(h.id);
    }

    int LowerBound(double x)
    {
        int lo = 0, hi = _xs.Length;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (_xs[mid] < x) lo = mid + 1; else hi = mid; }
        return lo;
    }
}
