namespace SpryCad.Kernel;

/// <summary>Arc information carried by profile segments that came from a circle/arc.</summary>
public sealed record ArcInfo(Vec2 Center, double Radius, int Segments);

/// <summary>A vertex of a closed 2D profile loop. The segment from this vertex to the next one
/// produces a face named <see cref="Name"/>; <see cref="Arc"/> is set when that segment is a facet of an arc.</summary>
public readonly record struct ProfileVertex(Vec2 P, string Name, ArcInfo? Arc = null);

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
            r.Verts.Add(new ProfileVertex(Verts[i].P, Verts[segOwner].Name, Verts[segOwner].Arc));
        }
        return r;
    }

    public bool Contains(Vec2 p) => FaceMerger.PointInPolygon(Points, p);

    public ProfileLoop Transformed(Func<Vec2, Vec2> f, bool reverse)
    {
        var l = new ProfileLoop(Verts.Select(v => v with { P = f(v.P), Arc = v.Arc == null ? null : v.Arc with { Center = f(v.Arc.Center) } }));
        return reverse ? l.Reversed() : l;
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

    /// <summary>Groups arbitrary closed loops into regions with even-odd nesting.</summary>
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
                if (loops[j].Contains(probe))
                {
                    depth[i]++;
                    if (parent[i] < 0 || area[j] < area[parent[i]]) parent[i] = j;
                }
            }
        }
        var regions = new Dictionary<int, ProfileRegion>();
        for (int i = 0; i < n; i++)
            if (depth[i] % 2 == 0) regions[i] = new ProfileRegion(loops[i]);
        for (int i = 0; i < n; i++)
            if (depth[i] % 2 == 1 && parent[i] >= 0 && regions.TryGetValue(parent[i], out var r)) r.Holes.Add(loops[i]);
        return regions.Values.Select(r => r.Normalized()).ToList();
    }
}
