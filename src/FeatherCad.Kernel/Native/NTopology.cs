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
    // one immutable reference so concurrent readers never see points from one tolerance with another tolerance
    sealed record SampleCache(List<Vec3> Points, double Tol);
    SampleCache? _samples;

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
        var cached = _samples;
        if (cached != null && cached.Tol <= tol * 1.0001) return cached.Points;
        if (Curve is NSpline or NPolyline)
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
            _samples = new SampleCache(res, tol);
            return res;
        }
        int n = Curve is NLine ? 1 : Math.Max(Curve.Samples(T0, T1, tol), 1);
        if (Curve is NCircle ci) n = Math.Max(n, (int)Math.Ceiling((T1 - T0) / (2 * Math.PI) * SurfaceDef.SegmentsFor(ci.R)));
        var pts = new List<Vec3>(n + 1);
        for (int i = 0; i <= n; i++) pts.Add(Curve.Eval(T0 + (T1 - T0) * i / n));
        pts[0] = V0.P; pts[^1] = V1.P;
        _samples = new SampleCache(pts, tol);
        return pts;
    }

    public void InvalidateSamples() => _samples = null;

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
    /// <summary>A point inside the face (or its parent face). Fixes the unwrap of periodic parameters:
    /// a face bounded only by seams (e.g. a half sphere) is otherwise ambiguous in UV.</summary>
    public Vec3? Anchor;

    public NFace(NSurface s, bool sense, FaceTag tag, Vec3? anchor = null) { Surface = s; Sense = sense; Tag = tag; Anchor = anchor; }

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

    /// <summary>
    /// Tight bounds: ruled faces (planes, cylinders, cones) reach their extremes on the boundary, so edge
    /// bounds are exact for them; spheres and tori also add surface samples inside the face.
    /// </summary>
    public Box3 TightBounds()
    {
        var b = Box3.Empty;
        foreach (var e in Edges) b.Add(e.TightBounds());
        foreach (var f in Faces)
            if (f.Surface.Kind is SurfaceKind.Sphere or SurfaceKind.Torus)
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
            if (e.Uses.Count != 2) return $"edge used {e.Uses.Count} times ({e.Curve.GetType().Name} {e.V0.P}->{e.V1.P} on {string.Join(",", e.Uses.Select(u => u.Face.Tag.Name))})";
            if (e.Uses[0].Forward == e.Uses[1].Forward) return $"edge used twice in the same direction ({e.Curve.GetType().Name} {e.V0.P}->{e.V1.P} on {e.Uses[0].Face.Tag.Name}/{e.Uses[0].Face.Sense}, {e.Uses[1].Face.Tag.Name}/{e.Uses[1].Face.Sense})";
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
            var nf = new NFace(s, f.Sense, tag, f.Anchor is { } an ? point(an) : null);
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
