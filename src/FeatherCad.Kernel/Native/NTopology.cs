namespace FeatherCad.Kernel.Native;

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
    List<Vec3>? _samples; double _sampleTol;

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

    /// <summary>Points from V0 to V1 within the chord tolerance (end points are exactly the vertices).</summary>
    public List<Vec3> Samples(double tol)
    {
        if (_samples != null && _sampleTol <= tol * 1.0001) return _samples;
        int n = Curve is NLine ? 1 : Math.Max(Curve.Samples(T0, T1, tol), Curve is NSpline ? 2 : 1);
        if (Curve is NCircle ci) n = Math.Max(n, (int)Math.Ceiling((T1 - T0) / (2 * Math.PI) * SurfaceDef.SegmentsFor(ci.R)));
        var pts = new List<Vec3>(n + 1);
        for (int i = 0; i <= n; i++) pts.Add(Curve.Eval(T0 + (T1 - T0) * i / n));
        pts[0] = V0.P; pts[^1] = V1.P;
        _samples = pts; _sampleTol = tol;
        return pts;
    }

    public void InvalidateSamples() => _samples = null;

    public Box3 Bounds(double tol = 1e-3)
    {
        var b = Box3.Empty;
        foreach (var p in Samples(tol)) b.Add(p);
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
    public List<Vec3> Points(double tol)
    {
        var s = Edge.Samples(tol);
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

    public NFace(NSurface s, bool sense, FaceTag tag) { Surface = s; Sense = sense; Tag = tag; }

    public IEnumerable<NCoedge> Coedges => Loops.SelectMany(l => l.Coedges);

    public Box3 Bounds(double tol = 1e-3)
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
    public IEnumerable<NVertex> Vertices => Edges.SelectMany(e => new[] { e.V0, e.V1 }).Distinct();

    public Box3 Bounds
    {
        get { var b = Box3.Empty; foreach (var f in Faces) b.Add(f.Bounds()); return b; }
    }

    /// <summary>Connects coedges to their faces and edges (call after building or editing).</summary>
    public void Link()
    {
        foreach (var e in Edges) e.Uses.Clear();
        foreach (var f in Faces)
            foreach (var l in f.Loops)
                foreach (var c in l.Coedges) { c.Face = f; c.Edge.Uses.Add(c); }
    }

    /// <summary>Checks the body is a closed, consistently oriented 2-manifold.</summary>
    public string? Validate()
    {
        Link();
        foreach (var f in Faces)
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
        foreach (var e in Edges)
        {
            if (e.Uses.Count != 2) return $"edge used {e.Uses.Count} times";
            if (e.Uses[0].Forward == e.Uses[1].Forward) return "edge used twice in the same direction";
        }
        return null;
    }

    public NBody Clone() => Map(c => c, s => s, x => x, false);

    public NBody Transformed(in Transform t, string? prefix = null)
    {
        var tt = t;
        var mirror = t.IsMirror;
        return Map(c => c.Transformed(tt), s => s.Transformed(tt), p => tt.Point(p), mirror, prefix);
    }

    /// <summary>Reverses orientation (inside out): used for the B operand of a subtraction.</summary>
    public NBody Flipped()
    {
        var b = Map(c => c, s => s, p => p, true);
        foreach (var f in b.Faces) f.Sense = !f.Sense;
        return b;
    }

    NBody Map(Func<NCurve, NCurve> curve, Func<NSurface, NSurface> surf, Func<Vec3, Vec3> point, bool reverseLoops, string? prefix = null)
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
                tmap[f.Tag] = tag = ReferenceEquals(s, f.Surface) && prefix == null ? f.Tag
                    : new FaceTag(prefix == null ? f.Tag.Name : prefix + f.Tag.Name, s.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : s.Def, f.Tag.Seq);
            }
            var nf = new NFace(s, f.Sense, tag);
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
