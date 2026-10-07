namespace FeatherCad.Kernel;

/// <summary>
/// A planar face region: one outer loop (CCW about the plane normal) and zero or more
/// hole loops (CW). The plane is kept exactly as constructed (plane-based geometry), it is
/// never re-derived from the (rounded) vertex positions.
/// </summary>
public sealed class Face
{
    public Plane Plane { get; }
    public FaceTag Tag { get; }
    public List<Vec3[]> Loops { get; }

    public Face(Plane plane, FaceTag tag, List<Vec3[]> loops)
    {
        Plane = plane; Tag = tag; Loops = loops;
    }

    public Face(Plane plane, FaceTag tag, params Vec3[] outer) : this(plane, tag, new List<Vec3[]> { outer }) { }

    public Vec3[] Outer => Loops[0];

    public Face Transformed(in Transform t, string? prefix = null)
    {
        var mirror = t.IsMirror;
        var loops = new List<Vec3[]>(Loops.Count);
        foreach (var l in Loops)
        {
            var nl = new Vec3[l.Length];
            for (int i = 0; i < l.Length; i++) nl[i] = t.Point(l[i]);
            if (mirror) Array.Reverse(nl);
            loops.Add(nl);
        }
        return new Face(Plane.Transformed(t), Tag.Transformed(t, prefix), loops);
    }

    public Face Flipped()
    {
        var loops = new List<Vec3[]>(Loops.Count);
        foreach (var l in Loops) { var nl = (Vec3[])l.Clone(); Array.Reverse(nl); loops.Add(nl); }
        return new Face(Plane.Flipped(), Tag, loops);
    }

    public Box3 Bounds()
    {
        var b = Box3.Empty;
        foreach (var p in Outer) b.Add(p);
        return b;
    }

    public Vec3 Centroid()
    {
        var c = Vec3.Zero;
        foreach (var p in Outer) c += p;
        return c / Outer.Length;
    }

    /// <summary>Area (holes subtracted).</summary>
    public double Area()
    {
        double a = 0;
        foreach (var l in Loops) a += LoopArea(l);
        return a;
    }

    double LoopArea(Vec3[] l)
    {
        var s = Vec3.Zero;
        for (int i = 0; i < l.Length; i++) s += Vec3.Cross(l[i], l[(i + 1) % l.Length]);
        return 0.5 * Vec3.Dot(s, Plane.Normal);
    }
}

/// <summary>
/// A closed solid (possibly multiple lumps) bounded by planar faces.
/// Solids are treated as immutable once built.
/// </summary>
public sealed class Solid
{
    List<Face>? _faces;
    readonly Func<List<Face>>? _tessellate;
    readonly object _lock = new();

    /// <summary>Planar facets. For native solids they are tessellated on first access (intermediate results never are).</summary>
    public List<Face> Faces
    {
        get
        {
            if (_faces != null) return _faces;
            lock (_lock)
            {
                if (_faces == null)
                {
                    using (Prof.Time("solid.tessellate")) _faces = _tessellate!();
                }
                return _faces;
            }
        }
    }

    public Solid(List<Face> faces) { _faces = faces; }

    /// <summary>A solid backed by a native B-rep, tessellated lazily by <paramref name="tessellate"/>.</summary>
    public Solid(Native.NBody native, Func<List<Face>> tessellate) { Native = native; _tessellate = tessellate; }

    /// <summary>Whether the facets have been computed (diagnostics).</summary>
    public bool IsTessellated => _faces != null;

    /// <summary>Exact native B-rep this solid was tessellated from (null for solids from the facet engine).</summary>
    public Native.NBody? Native { get; init; }

    public static readonly Solid Empty = new(new List<Face>());

    public bool IsEmpty => _faces == null && Native != null ? Native.Faces.Count == 0 : Faces.Count == 0;

    Box3? _bounds, _tight;
    /// <summary>
    /// A box around the solid, for overlap tests and view fitting: for an exact body the union of its faces' cached
    /// boxes (they survive edits elsewhere, so this costs little), at most a sampling tolerance larger than tight.
    /// </summary>
    public Box3 Bounds
    {
        get
        {
            if (_bounds is { } b) return b;
            if (_faces == null && Native != null) return (_bounds = Native.Bounds).Value;
            var bb = Box3.Empty;
            foreach (var f in Faces) foreach (var l in f.Loops) foreach (var p in l) bb.Add(p);
            _bounds = bb;
            return bb;
        }
    }

    /// <summary>The smallest box around the exact solid (Mass Properties): slower than <see cref="Bounds"/>.</summary>
    public Box3 TightBounds()
    {
        if (_tight is { } t) return t;
        if (Native != null) return (_tight = Native.TightBounds()).Value;
        return (_tight = Bounds).Value;
    }

    public Solid Transformed(in Transform t, string? prefix = null)
    {
        if (Native != null && _faces == null) return Kernel.Native.NTess.ToSolid(Native.Transformed(t, prefix));
        var faces = new List<Face>(Faces.Count);
        foreach (var f in Faces) faces.Add(f.Transformed(t, prefix));
        return new Solid(faces) { Native = Native?.Transformed(t, prefix) };
    }

    public Solid Flipped()
    {
        if (Native != null && _faces == null) return Kernel.Native.NTess.ToSolid(Native.Flipped());
        var faces = new List<Face>(Faces.Count);
        foreach (var f in Faces) faces.Add(f.Flipped());
        return new Solid(faces) { Native = Native?.Flipped() };
    }

    public static Solid Combine(IEnumerable<Solid> solids)
    {
        var list = solids.ToList();
        if (list.Count > 0 && list.All(s => s.Native != null))
        {
            var body = new Native.NBody();
            foreach (var s in list) body.Faces.AddRange(s.Native!.Clone().Faces);
            body.Link();
            return Kernel.Native.NTess.ToSolid(body);
        }
        var faces = new List<Face>();
        foreach (var s in list) faces.AddRange(s.Faces);
        Native.NBody? nb = null;
        if (list.Count > 0 && list.All(s => s.Native != null))
        {
            nb = new Native.NBody();
            foreach (var s in list) nb.Faces.AddRange(s.Native!.Clone().Faces);
            nb.Link();
        }
        return new Solid(faces) { Native = nb };
    }

    Topology? _topo;
    /// <summary>Stitched topology (vertices, edges, face adjacency). Built lazily and cached.</summary>
    public Topology Topology => _topo ??= Topology.Build(this);

    /// <summary>Signed volume by the divergence theorem.</summary>
    public double Volume()
    {
        double v = 0;
        foreach (var f in Faces)
        {
            foreach (var tri in Triangulator.TriangulateFace(f))
                v += Vec3.Dot(tri.A, Vec3.Cross(tri.B, tri.C));
        }
        return v / 6.0;
    }

    public double SurfaceArea()
    {
        double a = 0;
        foreach (var f in Faces) a += f.Area();
        return a;
    }

    /// <summary>Volume centroid.</summary>
    public Vec3 Centroid()
    {
        double v = 0; var c = Vec3.Zero;
        foreach (var f in Faces)
            foreach (var t in Triangulator.TriangulateFace(f))
            {
                var tv = Vec3.Dot(t.A, Vec3.Cross(t.B, t.C)) / 6.0;
                v += tv;
                c += (t.A + t.B + t.C) * (tv / 4.0);
            }
        return Math.Abs(v) > 1e-300 ? c / v : Vec3.Zero;
    }

    /// <summary>Distinct face tag names in this solid.</summary>
    public IEnumerable<string> FaceNames() => Faces.Select(f => f.Tag.Name).Distinct();

    public IEnumerable<Face> FacesNamed(string name) => Faces.Where(f => f.Tag.Name == name);
}
