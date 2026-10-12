namespace SpryCad.Kernel;

/// <summary>Arc information carried by profile segments that came from a circle/arc.</summary>
public sealed record ArcInfo(Vec2 Center, double Radius, int Segments);

/// <summary>
/// Spline information carried by profile segments that came from a sketch spline: the whole curve (in the sketch's
/// x–y, z = 0) and the piece of it from this vertex to the next, T0 to T1 (decreasing where the loop runs against the
/// spline). Pieces of one spline share the curve object.
/// </summary>
public sealed record SplineInfo(BSplineCurve Curve, double T0, double T1)
{
    /// <summary>The same piece run the other way.</summary>
    public SplineInfo Reversed() => this with { T0 = T1, T1 = T0 };
}

/// <summary>A vertex of a closed 2D profile loop. The segment from this vertex to the next one
/// produces a face named <see cref="Name"/>; <see cref="Arc"/> is set when that segment is a facet of an arc,
/// <see cref="Spline"/> when it is a piece of a spline.</summary>
public readonly record struct ProfileVertex(Vec2 P, string Name, ArcInfo? Arc = null, SplineInfo? Spline = null);

public sealed class ProfileLoop
{
    public List<ProfileVertex> Verts { get; } = new();

    public ProfileLoop() { }
    public ProfileLoop(IEnumerable<ProfileVertex> v) { Verts.AddRange(v); }

    public double SignedArea()
    {
        double a = 0;
        for (int i = 0, j = Verts.Count - 1; i < Verts.Count; j = i++)
            a += Verts[j].P.X * Verts[i].P.Y - Verts[i].P.X * Verts[j].P.Y;
        return a / 2;
    }

    public Vec2[] Points => Verts.Select(v => v.P).ToArray();

    /// <summary>Returns a reversed loop keeping each segment's name/arc attached to the same geometric segment.</summary>
    public ProfileLoop Reversed()
    {
        var n = Verts.Count;
        var r = new ProfileLoop();
        // segment i: Verts[i] -> Verts[i+1] named Verts[i].Name. Reversed: start at Verts[i+1] -> Verts[i]
        for (int k = 0; k < n; k++)
        {
            int i = (n - k) % n;               // vertex index in original
            int segOwner = (i - 1 + n) % n;   // segment from original (i-1) -> i becomes i -> i-1
            r.Verts.Add(new ProfileVertex(Verts[i].P, Verts[segOwner].Name, Verts[segOwner].Arc, Verts[segOwner].Spline?.Reversed()));
        }
        return r;
    }

    public bool Contains(Vec2 p) => FaceMerger.PointInPolygon(Points, p);

    /// <summary>The loop mapped by f (an affine map: a spline's poles are mapped as its points).</summary>
    public ProfileLoop Transformed(Func<Vec2, Vec2> f, bool reverse)
    {
        // (each spline once: its pieces keep sharing the mapped curve)
        var curves = new Dictionary<BSplineCurve, BSplineCurve>(ReferenceEqualityComparer.Instance);
        SplineInfo? Map(SplineInfo? s)
        {
            if (s == null) return null;
            if (!curves.TryGetValue(s.Curve, out var c))
                curves[s.Curve] = c = s.Curve with { Poles = s.Curve.Poles.Select(q => { var m = f(new Vec2(q.X, q.Y)); return new Vec3(m.X, m.Y, 0); }).ToArray() };
            return s with { Curve = c };
        }
        var l = new ProfileLoop(Verts.Select(v => v with { P = f(v.P), Arc = v.Arc == null ? null : v.Arc with { Center = f(v.Arc.Center) }, Spline = Map(v.Spline) }));
        return reverse ? l.Reversed() : l;
    }

    /// <summary>
    /// Appends a spline's points from parameter t0 to t1 (excluding the one at t1), each carrying its piece: enough
    /// of them for the polygon to follow the curve (eight a span, at least sixteen).
    /// </summary>
    public void AddSpline(BSplineCurve c, double t0, double t1, string name)
    {
        int spans = Math.Max(1, c.Poles.Length - c.Degree);
        int n = Math.Max(16, 8 * spans);
        for (int i = 0; i < n; i++)
        {
            double a = t0 + (t1 - t0) * i / n, b = t0 + (t1 - t0) * (i + 1) / n;
            var p = c.Eval(a);
            Verts.Add(new ProfileVertex(new Vec2(p.X, p.Y), name, null, new SplineInfo(c, a, b)));
        }
    }

    /// <summary>Appends arc points (excluding the end point) aligned to the global angular grid
    /// so that facets of coaxial features line up exactly.</summary>
    public void AddArc(Vec2 center, double radius, double a0, double a1, bool ccw, string name, int segments = 0)
    {
        if (segments <= 0) segments = SurfaceDef.SegmentsFor(radius);
        var info = new ArcInfo(center, radius, segments);
        var step = 2 * Math.PI / segments;
        Verts.Add(new ProfileVertex(center + Vec2.FromAngle(a0, radius), name, info));
        if (ccw)
        {
            while (a1 <= a0 + 1e-12) a1 += 2 * Math.PI;
            var k = Math.Floor(a0 / step + 1e-9) + 1;
            for (var a = k * step; a < a1 - step * 0.05; a += step)
                if (a > a0 + step * 0.05) Verts.Add(new ProfileVertex(center + Vec2.FromAngle(a, radius), name, info));
        }
        else
        {
            while (a1 >= a0 - 1e-12) a1 -= 2 * Math.PI;
            var k = Math.Ceiling(a0 / step - 1e-9) - 1;
            for (var a = k * step; a > a1 + step * 0.05; a -= step)
                if (a < a0 - step * 0.05) Verts.Add(new ProfileVertex(center + Vec2.FromAngle(a, radius), name, info));
        }
    }

    public static ProfileLoop Circle(Vec2 center, double r, string name, int segments = 0)
    {
        if (segments <= 0) segments = SurfaceDef.SegmentsFor(r);
        var info = new ArcInfo(center, r, segments);
        var l = new ProfileLoop();
        for (int i = 0; i < segments; i++)
            l.Verts.Add(new ProfileVertex(center + Vec2.FromAngle(2 * Math.PI * i / segments, r), name, info));
        return l;
    }

    public static ProfileLoop Polygon(IEnumerable<Vec2> pts, string namePrefix)
    {
        var l = new ProfileLoop();
        int i = 0;
        foreach (var p in pts) l.Verts.Add(new ProfileVertex(p, namePrefix + (i++)));
        return l;
    }
}

/// <summary>A region: one outer loop with optional holes.</summary>
public sealed class ProfileRegion
{
    public ProfileLoop Outer { get; set; }
    public List<ProfileLoop> Holes { get; } = new();

    public ProfileRegion(ProfileLoop outer, IEnumerable<ProfileLoop>? holes = null)
    {
        Outer = outer;
        if (holes != null) Holes.AddRange(holes);
    }

    /// <summary>Ensure outer loop CCW and holes CW.</summary>
    public ProfileRegion Normalized()
    {
        var o = Outer.SignedArea() < 0 ? Outer.Reversed() : Outer;
        var hs = Holes.Select(h => h.SignedArea() > 0 ? h.Reversed() : h);
        return new ProfileRegion(o, hs);
    }

    public IEnumerable<ProfileLoop> AllLoops() { yield return Outer; foreach (var h in Holes) yield return h; }

    /// <summary>
    /// Groups arbitrary closed loops into regions with even-odd nesting. Loops whose outlines cross (a circle over a
    /// rectangle's end) do not nest: each is a region of its own, and the regions overlap (see <see cref="Overlap"/>).
    /// </summary>
    public static List<ProfileRegion> FromLoops(IList<ProfileLoop> loops)
    {
        var n = loops.Count;
        var area = loops.Select(l => Math.Abs(l.SignedArea())).ToArray();
        var depth = new int[n];
        var parent = new int[n];
        for (int i = 0; i < n; i++)
        {
            parent[i] = -1;
            var probe = loops[i].Verts[0].P;
            for (int j = 0; j < n; j++)
            {
                if (i == j || area[j] <= area[i]) continue;
                if (loops[j].Contains(probe) && !Cross(loops[i], loops[j]))
                {
                    depth[i]++;
                    if (parent[i] < 0 || area[j] < area[parent[i]]) parent[i] = j;
                }
            }
        }
        var regions = new Dictionary<int, ProfileRegion>();
        // (in loop order: the same regions every time)
        for (int i = 0; i < n; i++)
            if (depth[i] % 2 == 0) regions[i] = new ProfileRegion(loops[i]);
        for (int i = 0; i < n; i++)
            if (depth[i] % 2 == 1 && parent[i] >= 0 && regions.TryGetValue(parent[i], out var r)) r.Holes.Add(loops[i]);
        return regions.Values.Select(r => r.Normalized()).ToList();
    }

    /// <summary>Whether two regions overlap: their outer outlines cross.</summary>
    public static bool Overlap(ProfileRegion a, ProfileRegion b) => Cross(a.Outer, b.Outer);

    /// <summary>Whether two loops' outlines cross (a segment of each passing through the other, not just touching).</summary>
    public static bool Cross(ProfileLoop a, ProfileLoop b)
    {
        var pa = a.Points; var pb = b.Points;
        if (pa.Length < 3 || pb.Length < 3) return false;
        double minAx = pa.Min(p => p.X), maxAx = pa.Max(p => p.X), minAy = pa.Min(p => p.Y), maxAy = pa.Max(p => p.Y);
        double minBx = pb.Min(p => p.X), maxBx = pb.Max(p => p.X), minBy = pb.Min(p => p.Y), maxBy = pb.Max(p => p.Y);
        if (maxAx < minBx || maxBx < minAx || maxAy < minBy || maxBy < minAy) return false;
        var eps = 1e-9 * Math.Max(1, Math.Max(maxAx - minAx, maxAy - minAy));
        static double Orient(Vec2 p, Vec2 q, Vec2 r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        for (int i = 0; i < pa.Length; i++)
        {
            Vec2 a0 = pa[i], a1 = pa[(i + 1) % pa.Length];
            for (int j = 0; j < pb.Length; j++)
            {
                Vec2 b0 = pb[j], b1 = pb[(j + 1) % pb.Length];
                double d1 = Orient(b0, b1, a0), d2 = Orient(b0, b1, a1), d3 = Orient(a0, a1, b0), d4 = Orient(a0, a1, b1);
                double la = (a1 - a0).Length, lb = (b1 - b0).Length;
                // strictly on opposite sides, both ways (a crossing, not a touch at an end or along a segment)
                if ((d1 > eps * lb && d2 < -eps * lb || d1 < -eps * lb && d2 > eps * lb) && (d3 > eps * la && d4 < -eps * la || d3 < -eps * la && d4 > eps * la))
                    return true;
            }
        }
        return false;
    }
}
