namespace FeatherCad.Kernel;

/// <summary>One straight piece of a model edge, oriented as in face A's loop.</summary>
public readonly record struct EdgeSeg(Vec3 A, Vec3 B, int FaceA, int FaceB, int VA, int VB);

/// <summary>
/// A model edge: a chain of segments separating two differently named faces
/// (e.g. the circular rim between a planar face and a hole's cylindrical face).
/// </summary>
public sealed class TopoEdge
{
    public required string FaceA { get; init; }
    public required string FaceB { get; init; }
    public required List<EdgeSeg> Segs { get; init; }
    public bool Closed { get; init; }
    public int Index { get; internal set; }

    public double Length => Segs.Sum(s => Vec3.Distance(s.A, s.B));

    public Vec3 Midpoint
    {
        get
        {
            var half = Length / 2; double acc = 0;
            foreach (var s in Segs)
            {
                var l = Vec3.Distance(s.A, s.B);
                if (acc + l >= half) return Vec3.Lerp(s.A, s.B, l > 0 ? (half - acc) / l : 0);
                acc += l;
            }
            return Segs[^1].B;
        }
    }

    public bool IsLinear
    {
        get
        {
            if (Closed) return false;
            var a = Segs[0].A; var b = Segs[^1].B; var d = (b - a).Normalized;
            foreach (var s in Segs)
            {
                var v = s.B - a;
                if ((v - d * Vec3.Dot(v, d)).Length > 1e-5) return false;
            }
            return true;
        }
    }

    public Vec3 Start => Segs[0].A;
    public Vec3 End => Segs[^1].B;

    public bool Touches(string a, string b) => (FaceA == a && FaceB == b) || (FaceA == b && FaceB == a);
}

/// <summary>
/// Stitched topology of a solid: shared vertices, loops with T-junctions resolved
/// (watertight), twin half-edges and model edges.
/// </summary>
public sealed class Topology
{
    public List<Vec3> Vertices { get; } = new();
    /// <summary>Per face, per loop: vertex indices (T-junction vertices inserted).</summary>
    public List<List<int[]>> FaceLoops { get; } = new();
    public List<TopoEdge> Edges { get; } = new();
    /// <summary>Half edges with no matching twin (0 for a watertight solid).</summary>
    public int OpenHalfEdges { get; private set; }
    /// <summary>Diagnostics: open half-edges as they are found.</summary>
    public static Action<string>? Debug;

    public static Topology Build(Solid s)
    {
        // native tessellations normally share every boundary sample between neighbouring faces (no
        // T-junctions): try without the costly T-junction pass and redo with it if anything stayed open
        if (s.Native != null)
        {
            var quick = Build(s, resolveTJunctions: false);
            if (quick.OpenHalfEdges == 0) return quick;
        }
        return Build(s, resolveTJunctions: true);
    }

    static Topology Build(Solid s, bool resolveTJunctions)
    {
        var t = new Topology();
        var pw = Prof.Time("topo.weld");
        var welder = new VertexWelder(FaceMerger.WeldTol * 10);
        var raw = new List<List<int[]>>();
        foreach (var f in s.Faces)
        {
            var fl = new List<int[]>();
            foreach (var l in f.Loops)
            {
                var ids = new int[l.Length];
                for (int i = 0; i < l.Length; i++) ids[i] = welder.Add(l[i]);
                fl.Add(ids);
            }
            raw.Add(fl);
        }
        t.Vertices.AddRange(welder.Points);
        pw.Dispose();
        var ptj = Prof.Time("topo.tjunctions");
        var segIndex = resolveTJunctions ? new SegmentVertexIndex(t.Vertices, welder.Tolerance) : null;
        var between = new List<int>();
        foreach (var fl in raw)
        {
            var outLoops = new List<int[]>();
            foreach (var ids in fl)
            {
                var l = new List<int>(ids.Length);
                for (int i = 0; i < ids.Length; i++)
                {
                    int a = ids[i], b = ids[(i + 1) % ids.Length];
                    if (a == b) continue;
                    l.Add(a);
                    if (segIndex == null) continue;
                    segIndex.Between(a, b, between);
                    l.AddRange(between);
                }
                outLoops.Add(l.ToArray());
            }
            t.FaceLoops.Add(outLoops);
        }

        ptj.Dispose();
        using var prest = Prof.Time("topo.edges");
        // half-edge map
        var owner = new Dictionary<(int, int), int>(); // -> face index
        for (int fi = 0; fi < t.FaceLoops.Count; fi++)
            foreach (var l in t.FaceLoops[fi])
                for (int i = 0; i < l.Length; i++)
                    owner.TryAdd((l[i], l[(i + 1) % l.Length]), fi);

        // collect boundary segments between differently named faces
        var segsByPair = new Dictionary<(string, string), List<EdgeSeg>>();
        int open = 0;
        for (int fi = 0; fi < t.FaceLoops.Count; fi++)
        {
            var nameA = s.Faces[fi].Tag.Name;
            foreach (var l in t.FaceLoops[fi])
                for (int i = 0; i < l.Length; i++)
                {
                    int a = l[i], b = l[(i + 1) % l.Length];
                    if (!owner.TryGetValue((b, a), out var fj))
                    {
                        open++;
                        if (Debug != null && open <= 20) Debug($"open {(resolveTJunctions ? "full" : "quick")}: {nameA} {t.Vertices[a]} -> {t.Vertices[b]}");
                        continue;
                    }
                    var nameB = s.Faces[fj].Tag.Name;
                    if (nameA == nameB) continue;
                    // keep one orientation per edge: the one owned by the lexicographically smaller name
                    if (string.CompareOrdinal(nameA, nameB) > 0) continue;
                    var key = (nameA, nameB);
                    if (!segsByPair.TryGetValue(key, out var list)) segsByPair[key] = list = new List<EdgeSeg>();
                    list.Add(new EdgeSeg(t.Vertices[a], t.Vertices[b], fi, fj, a, b));
                }
        }
        t.OpenHalfEdges = open;

        // chain segments into model edges
        foreach (var ((na, nb), segs) in segsByPair)
        {
            var byStart = new Dictionary<int, List<int>>();
            var hasPred = new HashSet<int>();
            for (int i = 0; i < segs.Count; i++)
            {
                if (!byStart.TryGetValue(segs[i].VA, out var l)) byStart[segs[i].VA] = l = new List<int>(1);
                l.Add(i);
            }
            foreach (var sg in segs) hasPred.Add(sg.VB);
            var used = new bool[segs.Count];
            // open chains first (start vertices without predecessor), then closed loops
            var order = Enumerable.Range(0, segs.Count).OrderBy(i => hasPred.Contains(segs[i].VA) ? 1 : 0).ToList();
            foreach (var si in order)
            {
                if (used[si]) continue;
                var chain = new List<EdgeSeg>();
                int cur = si;
                while (cur >= 0 && !used[cur])
                {
                    used[cur] = true;
                    chain.Add(segs[cur]);
                    cur = -1;
                    if (byStart.TryGetValue(chain[^1].VB, out var nexts))
                        foreach (var nx in nexts) if (!used[nx]) { cur = nx; break; }
                    // stop chains at sharp corners for planar-planar pairs (keeps edges straight)
                    if (cur >= 0 && IsSharpTurn(chain[^1], segs[cur])) cur = -1;
                }
                bool closed = chain.Count > 2 && chain[^1].VB == chain[0].VA;
                t.Edges.Add(new TopoEdge { FaceA = na, FaceB = nb, Segs = chain, Closed = closed });
            }
        }
        // merge open chains that were started mid-way (prefix chains)
        for (int i = 0; i < t.Edges.Count; i++) t.Edges[i].Index = i;
        return t;
    }

    static bool IsSharpTurn(EdgeSeg a, EdgeSeg b)
    {
        var d1 = (a.B - a.A).Normalized; var d2 = (b.B - b.A).Normalized;
        return Vec3.Dot(d1, d2) < 0.5; // > 60 degrees
    }

    /// <summary>Model edges between faces named a and b.</summary>
    public IEnumerable<TopoEdge> EdgesBetween(string a, string b) => Edges.Where(e => e.Touches(a, b));
}
