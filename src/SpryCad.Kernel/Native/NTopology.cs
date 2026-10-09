namespace SpryCad.Kernel.Native;

public sealed class NVertex
{
    public Vec3 P;
    public NVertex(Vec3 p) { P = p; }
    public override string ToString() => $"V{P}";
}

/// <summary>Edge: a curve restricted to [T0, T1] (T0 &lt; T1) from V0 to V1.</summary>
public sealed class NEdge
{
    public NCurve Curve;
    public double T0, T1;
    public NVertex V0, V1;
    public readonly List<NCoedge> Uses = new();
    // one immutable reference so concurrent readers never see points from one tolerance with another tolerance
    sealed record SampleCache(List<Vec3> Points, double Tol);
    // bounds of the samples at Tol (unpadded); unlike the samples it survives cloning, so operations on a big part
    // can test the edges far from the change without sampling them (the samples themselves stay per body: the
    // tessellation follows them)
    sealed record BoxCache(Box3 Box, double Tol);
    BoxCache? _box;
    // the finest sampling asked for (what FinestSamples gives), and one near a coarser tolerance asked for later
    SampleCache? _samples, _banded;
    /// <summary>A sampling at most this much finer than asked for is used as it is; a finer one is not.</summary>
    const double Band = 4;
    /// <summary>Diagnostics: the tolerance the cached samples were taken at (NaN: none).</summary>
    public double SampledTol => _samples?.Tol ?? double.NaN;

    public NEdge(NCurve c, double t0, double t1, NVertex v0, NVertex v1)
    {
        Curve = c; T0 = t0; T1 = t1; V0 = v0; V1 = v1;
    }

    public bool Closed => ReferenceEquals(V0, V1);
    public Vec3 At(double s) => Curve.Eval(T0 + (T1 - T0) * s);
    public Vec3 Mid => At(0.5);
    public double Length
    {
        get { var s = Samples(1e-3); double l = 0; for (int i = 1; i < s.Count; i++) l += Vec3.Distance(s[i - 1], s[i]); return l; }
    }

    /// <summary>
    /// Points from V0 to V1 within the chord tolerance (end points are exactly the vertices): a cached sampling within
    /// the tolerance and not much finer than it.
    /// </summary>
    public List<Vec3> Samples(double tol) => SamplesAt(tol).Points;

    SampleCache SamplesAt(double tol)
    {
        bool Fits(SampleCache? c) => c != null && c.Tol <= tol * 1.0001 && c.Tol * Band >= tol;
        var fine = _samples;
        if (Fits(fine)) return fine!;
        var near = _banded;
        if (Fits(near)) return near!;
        // nothing cached is fine enough: the new finest; something much finer is: a sampling near tol beside it
        if (fine == null || fine.Tol > tol * 1.0001) return FinestAt(tol);
        return _banded = Sample(tol);
    }

    /// <summary>
    /// Points within the chord tolerance, the finest sampling taken so far if that is finer (the face split follows
    /// the boundary points that edge/surface intersection sampled finely).
    /// </summary>
    public List<Vec3> FinestSamples(double tol) => FinestAt(tol).Points;

    SampleCache FinestAt(double tol)
    {
        var cached = _samples;
        if (cached != null && cached.Tol <= tol * 1.0001) return cached;
        return _samples = Sample(tol);
    }

    SampleCache Sample(double tol)
    {
        if (Curve is NSpline or NPolyline or NContact or NSection or NIso)
        {
            // adaptive: bisect until the chord error is below tol
            var res = new List<Vec3> { V0.P };
            void Sub(double a, double b, Vec3 pa, Vec3 pb, int depth)
            {
                var m = 0.5 * (a + b);
                var pm = Curve.Eval(m);
                if (depth >= 3 && (depth > 12 || Vec3.Distance(pm, (pa + pb) * 0.5) < tol)) { res.Add(pb); return; }
                Sub(a, m, pa, pm, depth + 1);
                Sub(m, b, pm, pb, depth + 1);
            }
            Sub(T0, T1, V0.P, V1.P, 0);
            res[^1] = V1.P;
            return new SampleCache(res, tol);
        }
        int n = Curve is NLine ? 1 : Math.Max(Curve.Samples(T0, T1, tol), 1);
        if (Curve is NCircle ci) n = Math.Max(n, (int)Math.Ceiling((T1 - T0) / (2 * Math.PI) * SurfaceDef.SegmentsFor(ci.R)));
        var pts = new List<Vec3>(n + 1);
        for (int i = 0; i <= n; i++) pts.Add(Curve.Eval(T0 + (T1 - T0) * i / n));
        pts[0] = V0.P; pts[^1] = V1.P;
        return new SampleCache(pts, tol);
    }

    public void InvalidateSamples() { _samples = null; _banded = null; _box = null; }
    /// <summary>Takes over another edge's cached bounds (same curve, same parameter range: a clone).</summary>
    internal void CopyBounds(NEdge from) => _box = from._box;

    /// <summary>Exact bounds for lines/circles/ellipses (endpoints plus axis extremes inside the range).</summary>
    public Box3 TightBounds()
    {
        var b = Box3.Empty;
        b.Add(V0.P); b.Add(V1.P);
        Vec3 x, y; Vec3 c;
        switch (Curve)
        {
            case NLine: return b;
            case NCircle ci: c = ci.C; x = ci.X * ci.R; y = ci.Y * ci.R; break;
            case NEllipse el: c = el.C; x = el.X * el.A; y = el.Y * el.B; break;
            default: foreach (var p in Samples(1e-4)) b.Add(p); return b;
        }
        // p(t) = c + x cos t + y sin t: coordinate k is extreme at t = atan2(y_k, x_k) (+π)
        double lo = Math.Min(T0, T1), hi = Math.Max(T0, T1);
        for (int k = 0; k < 3; k++)
        {
            var t0 = Math.Atan2(k == 0 ? y.X : k == 1 ? y.Y : y.Z, k == 0 ? x.X : k == 1 ? x.Y : x.Z);
            for (int s = -3; s <= 3; s++)
            {
                var t = t0 + s * Math.PI;
                if (t > lo && t < hi) b.Add(Curve.Eval(t));
            }
        }
        return b;
    }

    public Box3 Bounds(double tol = 1e-3)
    {
        Box3 b;
        if (_box is { } bc && bc.Tol <= tol * 1.0001) b = bc.Box;
        else
        {
            var c = SamplesAt(tol);
            b = Box3.Empty;
            foreach (var p in c.Points) b.Add(p);
            _box = new BoxCache(b, c.Tol);
        }
        // pad by the chord tolerance so sampled bounds enclose the curve
        b.Add(b.Min - new Vec3(tol, tol, tol)); b.Add(b.Max + new Vec3(tol, tol, tol));
        return b;
    }
}

public sealed class NCoedge
{
    public NEdge Edge;
    public bool Forward;
    public NFace Face = null!;
    public NCoedge(NEdge e, bool forward) { Edge = e; Forward = forward; }
    public NVertex Start => Forward ? Edge.V0 : Edge.V1;
    public NVertex End => Forward ? Edge.V1 : Edge.V0;
    public List<Vec3> Points(double tol, bool finest = false)
    {
        var s = finest ? Edge.FinestSamples(tol) : Edge.Samples(tol);
        if (Forward) return s;
        var r = new List<Vec3>(s); r.Reverse(); return r;
    }
}

public sealed class NLoop
{
    public readonly List<NCoedge> Coedges = new();
}

public sealed class NFace
{
    public NSurface Surface;
    /// <summary>True when the outward normal equals the surface's natural normal.</summary>
    public bool Sense;
    public FaceTag Tag;
    public readonly List<NLoop> Loops = new();
    /// <summary>A point inside the face (or its parent face). Fixes the unwrap of periodic parameters:
    /// a face bounded only by seams (e.g. a half sphere) is otherwise ambiguous in UV.</summary>
    public Vec3? Anchor;

    public NFace(NSurface s, bool sense, FaceTag tag, Vec3? anchor = null) { Surface = s; Sense = sense; Tag = tag; Anchor = anchor; }

    public IEnumerable<NCoedge> Coedges => Loops.SelectMany(l => l.Coedges);

    // last computed bounds (faces keep their shape once built: splitting edges or cloning does not change it)
    internal sealed record BoundsEntry(double tol, Box3 box);
    // one reference: faces are read by parallel workers, a reader must never see half of a box
    internal BoundsEntry? BoundsCache;

    // a domain built for this face (or the face it was cloned from), valid while the loops have the same geometry
    internal sealed record DomainEntry(NDomain Dom, double Tol, double Sig);
    internal DomainEntry? DomainCache;

    /// <summary>A cheap fingerprint of the loops' geometry and the orientation (changes when loops are edited).</summary>
    internal double LoopSignature()
    {
        double h = Sense ? 1 : 2; int k = 0;
        foreach (var l in Loops)
        {
            h = h * 1.000123 + 17 * ++k;
            foreach (var c in l.Coedges)
            {
                var p = c.Start.P;
                h = h * 1.000017 + p.X + 1.31 * p.Y + 1.73 * p.Z + 0.37 * c.Edge.T0 + 0.61 * c.Edge.T1 + (c.Forward ? 0.5 : 0.25);
            }
        }
        return h;
    }

    public Box3 Bounds(double tol = 1e-3)
    {
        // a box sampled at a finer (or slightly coarser) tolerance is as good for overlap tests
        if (BoundsCache is { } bc && bc.tol <= tol * 4) return bc.box;
        var b = ComputeBounds(tol);
        BoundsCache = new(tol, b);
        return b;
    }

    Box3 ComputeBounds(double tol)
    {
        var b = Box3.Empty;
        foreach (var c in Coedges) b.Add(c.Edge.Bounds(tol));
        // curved faces can bulge beyond their boundary (e.g. a cylinder strip): add the surface extent
        if (Surface.Kind != SurfaceKind.Plane)
        {
            var dom = NDomain.Of(this, tol);
            foreach (var p in dom.InteriorSamples(6)) b.Add(p);
        }
        return b;
    }
}

/// <summary>A native exact B-rep body (one or more closed shells).</summary>
public sealed class NBody
{
    public readonly List<NFace> Faces = new();

    public IEnumerable<NEdge> Edges => Faces.SelectMany(f => f.Coedges).Select(c => c.Edge).Distinct();

    /// <summary>Connected components (shells joined through shared edges) as separate bodies sharing the faces.</summary>
    public List<NBody> Lumps()
    {
        Link();
        var idx = new Dictionary<NFace, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < Faces.Count; i++) idx[Faces[i]] = i;
        var parent = Enumerable.Range(0, Faces.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var e in Edges)
            for (int k = 1; k < e.Uses.Count; k++) parent[Find(idx[e.Uses[0].Face])] = Find(idx[e.Uses[k].Face]);
        var groups = new Dictionary<int, NBody>();
        for (int i = 0; i < Faces.Count; i++)
        {
            var r = Find(i);
            if (!groups.TryGetValue(r, out var b)) groups[r] = b = new NBody();
            b.Faces.Add(Faces[i]);
        }
        return groups.Values.ToList();
    }

    // one reference: a body may be hashed by parallel workers (the hash keys the boolean result cache)
    System.Runtime.CompilerServices.StrongBox<ulong>? _hash;
    /// <summary>Content hash (cached: bodies are not modified once built; operations work on clones).</summary>
    public ulong ContentHash => (_hash ??= new(NHash.Of(this))).Value;
    public IEnumerable<NVertex> Vertices => Edges.SelectMany(e => new[] { e.V0, e.V1 }).Distinct();

    public Box3 Bounds
    {
        get { var b = Box3.Empty; foreach (var f in Faces) b.Add(f.Bounds()); return b; }
    }

    /// <summary>
    /// Tight bounds: ruled faces (planes, cylinders, cones) reach their extremes on the boundary, so edge
    /// bounds are exact for them; spheres and tori also add surface samples inside the face.
    /// </summary>
    public Box3 TightBounds()
    {
        var b = Box3.Empty;
        foreach (var e in Edges) b.Add(e.TightBounds());
        foreach (var f in Faces)
            if (f.Surface.Kind is SurfaceKind.Sphere or SurfaceKind.Torus or SurfaceKind.Pipe or SurfaceKind.BSpline or SurfaceKind.Offset or SurfaceKind.Screw)
            {
                var dom = NDomain.Of(f, 1e-4);
                foreach (var q in dom.InteriorSamples(12))
                    if (dom.ContainsUV(dom.Map(q))) b.Add(q);
            }
        return b;
    }

    /// <summary>Connects coedges to their faces and edges (call after building or editing).</summary>
    public void Link()
    {
        // clearing an edge once per use is cheaper than collecting the distinct edges first
        foreach (var f in Faces)
            foreach (var l in f.Loops)
                foreach (var c in l.Coedges) c.Edge.Uses.Clear();
        foreach (var f in Faces)
            foreach (var l in f.Loops)
                foreach (var c in l.Coedges) { c.Face = f; c.Edge.Uses.Add(c); }
    }

    /// <summary>Checks the body is a closed, consistently oriented 2-manifold.</summary>
    public string? Validate() => Validate(Faces, null);

    /// <summary>Validates the loops of <paramref name="faces"/> and the uses of <paramref name="edges"/> (all edges
    /// of those faces when null), after linking the whole body.</summary>
    public string? Validate(IEnumerable<NFace> faces, IEnumerable<NEdge>? edges)
    {
        Link();
        foreach (var f in faces)
        {
            if (f.Loops.Count == 0) return $"face {f.Tag.Name} has no loops";
            foreach (var l in f.Loops)
            {
                if (l.Coedges.Count == 0) return $"empty loop on {f.Tag.Name}";
                for (int i = 0; i < l.Coedges.Count; i++)
                    if (!ReferenceEquals(l.Coedges[i].End, l.Coedges[(i + 1) % l.Coedges.Count].Start))
                        return $"open loop on {f.Tag.Name}";
            }
        }
        foreach (var e in edges ?? faces.SelectMany(f => f.Coedges).Select(c => c.Edge).Distinct())
        {
            if (e.Uses.Count != 2) return $"edge used {e.Uses.Count} times ({e.Curve.GetType().Name} {e.V0.P}->{e.V1.P} on {string.Join(",", e.Uses.Select(u => u.Face.Tag.Name))})";
            if (e.Uses[0].Forward == e.Uses[1].Forward) return $"edge used twice in the same direction ({e.Curve.GetType().Name} {e.V0.P}->{e.V1.P} on {e.Uses[0].Face.Tag.Name}/{e.Uses[0].Face.Sense}, {e.Uses[1].Face.Tag.Name}/{e.Uses[1].Face.Sense})";
        }
        return null;
    }

    /// <summary>A copy with new topology objects (operations edit clones); edge samples and face domains carry over.</summary>
    public NBody Clone() => Clone(out _);

    /// <summary>A copy (see <see cref="Clone()"/>) and which of its edges each of this body's edges became.</summary>
    public NBody Clone(out Dictionary<NEdge, NEdge> edgeMap)
    {
        // the hot path of every operation: presized maps, no per-element delegates, linked while copying
        int n = 0;
        foreach (var f in Faces) foreach (var l in f.Loops) n += l.Coedges.Count;
        var vmap = new Dictionary<NVertex, NVertex>(n / 2 + 8, ReferenceEqualityComparer.Instance);
        var emap = new Dictionary<NEdge, NEdge>(n / 2 + 8, ReferenceEqualityComparer.Instance);
        NVertex V(NVertex v)
        {
            if (!vmap.TryGetValue(v, out var nv)) vmap[v] = nv = new NVertex(v.P);
            return nv;
        }
        var b = new NBody();
        b.Faces.Capacity = Faces.Count;
        foreach (var f in Faces)
        {
            var nf = new NFace(f.Surface, f.Sense, f.Tag, f.Anchor) { BoundsCache = f.BoundsCache, DomainCache = f.DomainCache };
            foreach (var l in f.Loops)
            {
                var nl = new NLoop();
                nl.Coedges.Capacity = l.Coedges.Count;
                foreach (var c in l.Coedges)
                {
                    var e = c.Edge;
                    if (!emap.TryGetValue(e, out var ne))
                    {
                        emap[e] = ne = new NEdge(e.Curve, e.T0, e.T1, V(e.V0), V(e.V1));
                        ne.CopyBounds(e);
                    }
                    var nc = new NCoedge(ne, c.Forward) { Face = nf };
                    ne.Uses.Add(nc);
                    nl.Coedges.Add(nc);
                }
                nf.Loops.Add(nl);
            }
            b.Faces.Add(nf);
        }
        edgeMap = emap;
        return b;
    }

    /// <summary>Diagnostics: a copy with new edge objects (no sample caches).</summary>
    public static NBody CopyFresh(NBody b) => b.Map(c => c, s => s, x => x, false);

    public NBody Transformed(in Transform t, string? prefix = null)
    {
        var tt = t;
        var mirror = t.IsMirror;
        return Map(c => c.Transformed(tt), s => s.Transformed(tt), p => tt.Point(p), mirror, prefix, mirrored: mirror);
    }

    /// <summary>A moved copy whose face names are given by <paramref name="rename"/> (e.g. one pattern instance's faces as another's).</summary>
    public NBody Transformed(in Transform t, Func<string, string> rename)
    {
        var tt = t;
        return Map(c => c.Transformed(tt), s => s.Transformed(tt), p => tt.Point(p), t.IsMirror, null, rename, mirrored: t.IsMirror);
    }

    /// <summary>Reverses orientation (inside out): used for the B operand of a subtraction.</summary>
    public NBody Flipped()
    {
        var b = Map(c => c, s => s, p => p, true);
        foreach (var f in b.Faces) f.Sense = !f.Sense;
        return b;
    }

    NBody Map(Func<NCurve, NCurve> curve, Func<NSurface, NSurface> surf, Func<Vec3, Vec3> point, bool reverseLoops, string? prefix = null, Func<string, string>? rename = null, bool mirrored = false)
    {
        var vmap = new Dictionary<NVertex, NVertex>(ReferenceEqualityComparer.Instance);
        var emap = new Dictionary<NEdge, NEdge>(ReferenceEqualityComparer.Instance);
        var cmap = new Dictionary<NCurve, NCurve>(ReferenceEqualityComparer.Instance);
        var smap = new Dictionary<NSurface, NSurface>(ReferenceEqualityComparer.Instance);
        var tmap = new Dictionary<FaceTag, FaceTag>(ReferenceEqualityComparer.Instance);
        NVertex V(NVertex v) => vmap.TryGetValue(v, out var n) ? n : vmap[v] = new NVertex(point(v.P));
        NEdge E(NEdge e)
        {
            if (emap.TryGetValue(e, out var n)) return n;
            if (!cmap.TryGetValue(e.Curve, out var c)) cmap[e.Curve] = c = curve(e.Curve);
            // transformed curves keep their parameterization (lines/circles/ellipses/splines)
                return emap[e] = new NEdge(c, e.T0, e.T1, V(e.V0), V(e.V1));
        }
        var b = new NBody();
        foreach (var f in Faces)
        {
            if (!smap.TryGetValue(f.Surface, out var s)) smap[f.Surface] = s = surf(f.Surface);
            if (!tmap.TryGetValue(f.Tag, out var tag))
            {
                var name = rename != null ? rename(f.Tag.Name) : prefix == null ? f.Tag.Name : prefix + f.Tag.Name;
                tmap[f.Tag] = tag = ReferenceEquals(s, f.Surface) && name == f.Tag.Name ? f.Tag
                    : new FaceTag(name, s.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : s.Def, f.Tag.Seq);
            }
            // a mirrored screw surface keeps its parameterization, so its natural normal (Su × Sv) turns to the other side
            var nf = new NFace(s, mirrored && s.Kind == SurfaceKind.Screw ? !f.Sense : f.Sense, tag, f.Anchor is { } an ? point(an) : null);
            if (ReferenceEquals(s, f.Surface)) nf.BoundsCache = f.BoundsCache; // same geometry (clone / flip)
            foreach (var l in f.Loops)
            {
                var nl = new NLoop();
                foreach (var c in l.Coedges) nl.Coedges.Add(new NCoedge(E(c.Edge), reverseLoops ? !c.Forward : c.Forward));
                if (reverseLoops) nl.Coedges.Reverse();
                nf.Loops.Add(nl);
            }
            b.Faces.Add(nf);
        }
        b.Link();
        return b;
    }
}
