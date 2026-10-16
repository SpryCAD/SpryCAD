using System.Text;
using SpryCad.Kernel.Native;

namespace SpryCad.Kernel;

/// <summary>
/// SpryCAD's own file of exact solids (".sbrep"): native bodies written as they are (surfaces, curves, vertices,
/// edges, faces and their loops, each face's name) and read back the same, bit for bit. What an imported STEP
/// assembly's parts keep: each part's solids as the assembly file was read, not written to a STEP of their own and
/// read again (twice the work, and a round trip not every solid survives). Holds what a STEP import makes: planes,
/// cylinders, cones, spheres, tori, B-spline, offset and screw surfaces; lines, circles, conics, B-spline curves,
/// helices and iso-parameter curves. Anything else is refused (<see cref="NotSupportedException"/>), and the caller
/// keeps STEP for that part.
/// </summary>
public static class NBrepFile
{
    static readonly byte[] Magic = "SPRYBREP"u8.ToArray();
    const int Version = 1;

    /// <summary>Whether the bytes are such a file (its first eight bytes).</summary>
    public static bool Is(ReadOnlySpan<byte> data) => data.Length >= Magic.Length && data[..Magic.Length].SequenceEqual(Magic);

    /// <summary>
    /// The solids as a file; their faces' names written without <paramref name="prefix"/> (the import's, put back by
    /// <see cref="Read"/> with the reader's own).
    /// </summary>
    public static byte[] Write(IReadOnlyList<StepSolid> solids, string prefix, IEnumerable<string>? warnings = null)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(Version);
            var notes = warnings?.ToList() ?? new List<string>();
            w.Write(notes.Count);
            foreach (var n in notes) w.Write(n);
            w.Write(solids.Count);
            foreach (var s in solids) { w.Write(s.Name); WriteBody(w, s.Body, prefix); }
        }
        return ms.ToArray();
    }

    /// <summary>The solids of such a file, their faces named with <paramref name="prefix"/> (as a STEP import names them).</summary>
    public static StepImportResult Read(byte[] data, string prefix)
    {
        if (!Is(data)) throw new InvalidDataException("Not a SpryCAD solid file.");
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        r.ReadBytes(Magic.Length);
        var version = r.ReadInt32();
        if (version != Version) throw new InvalidDataException($"A SpryCAD solid file of version {version} (this version reads {Version}).");
        var res = new StepImportResult();
        int notes = r.ReadInt32();
        for (int i = 0; i < notes; i++) res.Warnings.Add(r.ReadString());
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            var name = r.ReadString();
            res.Solids.Add(new StepSolid(name, ReadBody(r, prefix)));
        }
        return res;
    }

    // ------------------------------------------------------------------------------------------
    // a body: its surfaces (an offset's base before it), curves, face tags, vertices, edges and faces, each table in
    // the order first met, every element by its index in its table (shared ones written once)

    enum CurveKind : byte { Line = 1, Circle, Ellipse, Hyperbola, Parabola, Spline, Helix, Iso }

    static void WriteBody(BinaryWriter w, NBody b, string prefix)
    {
        var surfaces = new List<NSurface>(); var surfIdx = new Dictionary<NSurface, int>(ReferenceEqualityComparer.Instance);
        var curves = new List<NCurve>(); var curveIdx = new Dictionary<NCurve, int>(ReferenceEqualityComparer.Instance);
        var tags = new List<FaceTag>(); var tagIdx = new Dictionary<FaceTag, int>(ReferenceEqualityComparer.Instance);
        var verts = new List<NVertex>(); var vertIdx = new Dictionary<NVertex, int>(ReferenceEqualityComparer.Instance);
        var edges = new List<NEdge>(); var edgeIdx = new Dictionary<NEdge, int>(ReferenceEqualityComparer.Instance);
        int Surf(NSurface s)
        {
            if (surfIdx.TryGetValue(s, out var i)) return i;
            if (s.Def.Base is { } baseS) Surf(baseS);
            surfIdx[s] = i = surfaces.Count; surfaces.Add(s);
            return i;
        }
        int Curve(NCurve c)
        {
            if (curveIdx.TryGetValue(c, out var i)) return i;
            if (c is NIso iso) Surf(iso.S);
            curveIdx[c] = i = curves.Count; curves.Add(c);
            return i;
        }
        int Vert(NVertex v)
        {
            if (vertIdx.TryGetValue(v, out var i)) return i;
            vertIdx[v] = i = verts.Count; verts.Add(v);
            return i;
        }
        foreach (var f in b.Faces)
        {
            Surf(f.Surface);
            if (!tagIdx.ContainsKey(f.Tag)) { tagIdx[f.Tag] = tags.Count; tags.Add(f.Tag); }
            foreach (var c in f.Coedges)
            {
                if (edgeIdx.ContainsKey(c.Edge)) continue;
                Curve(c.Edge.Curve); Vert(c.Edge.V0); Vert(c.Edge.V1);
                edgeIdx[c.Edge] = edges.Count; edges.Add(c.Edge);
            }
        }
        // a tag names its face's surface definition (a plane's: the shared plane definition)
        var defIdx = new Dictionary<SurfaceDef, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < surfaces.Count; i++) defIdx.TryAdd(surfaces[i].Def, i);

        w.Write(surfaces.Count);
        foreach (var s in surfaces) WriteSurface(w, s, surfIdx);
        w.Write(curves.Count);
        foreach (var c in curves) WriteCurve(w, c, surfIdx);
        w.Write(tags.Count);
        foreach (var t in tags)
        {
            w.Write(t.Name.StartsWith(prefix, StringComparison.Ordinal) ? t.Name[prefix.Length..] : t.Name);
            w.Write(t.Name.StartsWith(prefix, StringComparison.Ordinal));
            if (ReferenceEquals(t.Surface, SurfaceDef.PlaneDef)) w.Write(-1);
            else if (defIdx.TryGetValue(t.Surface, out var di)) w.Write(di);
            else throw new NotSupportedException("a face named by another surface than its own");
        }
        w.Write(verts.Count);
        foreach (var v in verts) V(w, v.P);
        w.Write(edges.Count);
        foreach (var e in edges) { w.Write(curveIdx[e.Curve]); w.Write(e.T0); w.Write(e.T1); w.Write(vertIdx[e.V0]); w.Write(vertIdx[e.V1]); }
        w.Write(b.Faces.Count);
        foreach (var f in b.Faces)
        {
            w.Write(surfIdx[f.Surface]); w.Write(f.Sense); w.Write(tagIdx[f.Tag]);
            w.Write(f.Anchor.HasValue);
            if (f.Anchor is { } an) V(w, an);
            w.Write(f.Loops.Count);
            foreach (var l in f.Loops)
            {
                w.Write(l.Coedges.Count);
                foreach (var c in l.Coedges) { w.Write(edgeIdx[c.Edge]); w.Write(c.Forward); }
            }
        }
    }

    static NBody ReadBody(BinaryReader r, string prefix)
    {
        var surfaces = new NSurface[r.ReadInt32()];
        for (int i = 0; i < surfaces.Length; i++) surfaces[i] = ReadSurface(r, surfaces);
        var curves = new NCurve[r.ReadInt32()];
        for (int i = 0; i < curves.Length; i++) curves[i] = ReadCurve(r, surfaces);
        var tags = new FaceTag[r.ReadInt32()];
        for (int i = 0; i < tags.Length; i++)
        {
            var name = r.ReadString(); var prefixed = r.ReadBoolean(); var si = r.ReadInt32();
            tags[i] = new FaceTag(prefixed ? prefix + name : name, si < 0 ? SurfaceDef.PlaneDef : surfaces[si].Def);
        }
        var verts = new NVertex[r.ReadInt32()];
        for (int i = 0; i < verts.Length; i++) verts[i] = new NVertex(ReadV(r));
        var edges = new NEdge[r.ReadInt32()];
        for (int i = 0; i < edges.Length; i++)
        {
            var c = curves[r.ReadInt32()]; double t0 = r.ReadDouble(), t1 = r.ReadDouble();
            var v0 = verts[r.ReadInt32()]; var v1 = verts[r.ReadInt32()];
            edges[i] = new NEdge(c, t0, t1, v0, v1);
        }
        var b = new NBody();
        int nf = r.ReadInt32();
        for (int i = 0; i < nf; i++)
        {
            var s = surfaces[r.ReadInt32()]; var sense = r.ReadBoolean(); var tag = tags[r.ReadInt32()];
            Vec3? anchor = r.ReadBoolean() ? ReadV(r) : null;
            var f = new NFace(s, sense, tag, anchor);
            int nl = r.ReadInt32();
            for (int k = 0; k < nl; k++)
            {
                var l = new NLoop();
                int nc = r.ReadInt32();
                for (int j = 0; j < nc; j++) { var e = edges[r.ReadInt32()]; l.Coedges.Add(new NCoedge(e, r.ReadBoolean())); }
                f.Loops.Add(l);
            }
            b.Faces.Add(f);
        }
        b.Link();
        return b;
    }

    // ------------------------------------------------------------------------------------------
    // surfaces: the definition's fields, and what it holds (a B-spline surface, an offset's base, a screw surface)

    static void WriteSurface(BinaryWriter w, NSurface s, Dictionary<NSurface, int> surfIdx)
    {
        var d = s.Def;
        if (d.Spine != null) throw new NotSupportedException("pipe surfaces");
        w.Write((byte)d.Kind); V(w, d.Origin); V(w, d.Axis); V(w, d.RefDir); w.Write(d.Radius); w.Write(d.Param2); w.Write(d.Segments);
        if (d.Kind == SurfaceKind.Plane)
        {
            var pl = s.Exact.Plane!;
            V(w, pl.Normal); w.Write(pl.D);
            w.Write(ReferenceEquals(d, SurfaceDef.PlaneDef));
        }
        w.Write(d.Nurbs != null);
        if (d.Nurbs is { } nb)
        {
            int nu = nb.P.GetLength(0), nv = nb.P.GetLength(1);
            w.Write(nb.Pu); w.Write(nb.Pv); w.Write(nu); w.Write(nv);
            for (int i = 0; i < nu; i++) for (int j = 0; j < nv; j++) V(w, nb.P[i, j]);
            w.Write(nb.W != null);
            if (nb.W != null) for (int i = 0; i < nu; i++) for (int j = 0; j < nv; j++) w.Write(nb.W[i, j]);
            Doubles(w, nb.U); Doubles(w, nb.V);
        }
        w.Write(d.Base != null ? surfIdx[d.Base] : -1);
        w.Write(d.Screw != null);
        if (d.Screw is { } sc)
        {
            Motion(w, sc.Motion);
            WriteCurve(w, sc.Gen, surfIdx);
            w.Write(sc.V0); w.Write(sc.V1); w.Write(sc.U0); w.Write(sc.U1);
        }
    }

    static NSurface ReadSurface(BinaryReader r, NSurface[] surfaces)
    {
        var kind = (SurfaceKind)r.ReadByte();
        var def = new SurfaceDef { Kind = kind, Origin = ReadV(r), Axis = ReadV(r), RefDir = ReadV(r), Radius = r.ReadDouble(), Param2 = r.ReadDouble(), Segments = r.ReadInt32() };
        Plane? plane = null;
        if (kind == SurfaceKind.Plane)
        {
            plane = new Plane(ReadV(r), r.ReadDouble());
            if (r.ReadBoolean()) def = SurfaceDef.PlaneDef;
        }
        if (r.ReadBoolean())
        {
            int pu = r.ReadInt32(), pv = r.ReadInt32(), nu = r.ReadInt32(), nv = r.ReadInt32();
            var p = new Vec3[nu, nv];
            for (int i = 0; i < nu; i++) for (int j = 0; j < nv; j++) p[i, j] = ReadV(r);
            double[,]? wts = null;
            if (r.ReadBoolean()) { wts = new double[nu, nv]; for (int i = 0; i < nu; i++) for (int j = 0; j < nv; j++) wts[i, j] = r.ReadDouble(); }
            def = def with { Nurbs = new NurbsSurface(pu, pv, p, wts, ReadDoubles(r), ReadDoubles(r)) };
        }
        var bi = r.ReadInt32();
        if (bi >= 0) def = def with { Base = surfaces[bi] };
        if (r.ReadBoolean())
        {
            var m = ReadMotion(r);
            var gen = ReadCurve(r, surfaces);
            double v0 = r.ReadDouble(), v1 = r.ReadDouble(), u0 = r.ReadDouble(), u1 = r.ReadDouble();
            def = def with { Screw = new ScrewSurface(m, gen, v0, v1, u0, u1) };
        }
        return new NSurface(def, plane);
    }

    // ------------------------------------------------------------------------------------------
    // curves

    static void WriteCurve(BinaryWriter w, NCurve c, Dictionary<NSurface, int> surfIdx)
    {
        switch (c)
        {
            case NLine l: w.Write((byte)CurveKind.Line); V(w, l.P); V(w, l.D); break;
            case NCircle ci: w.Write((byte)CurveKind.Circle); V(w, ci.C); V(w, ci.X); V(w, ci.Y); w.Write(ci.R); break;
            case NEllipse el: w.Write((byte)CurveKind.Ellipse); V(w, el.C); V(w, el.X); V(w, el.Y); w.Write(el.A); w.Write(el.B); break;
            case NHyperbola hy: w.Write((byte)CurveKind.Hyperbola); V(w, hy.C); V(w, hy.X); V(w, hy.Y); w.Write(hy.A); w.Write(hy.B); break;
            case NParabola pa: w.Write((byte)CurveKind.Parabola); V(w, pa.C); V(w, pa.X); V(w, pa.Y); w.Write(pa.F); break;
            case NSpline sp:
                w.Write((byte)CurveKind.Spline); w.Write(sp.FitTol); w.Write(sp.S.Degree);
                w.Write(sp.S.Poles.Length); foreach (var p in sp.S.Poles) V(w, p);
                Doubles(w, sp.S.Knots);
                break;
            case NHelix hx: w.Write((byte)CurveKind.Helix); Motion(w, hx.Motion); V(w, hx.P0); break;
            case NIso iso: w.Write((byte)CurveKind.Iso); w.Write(surfIdx[iso.S]); w.Write(iso.Axis); w.Write(iso.C); w.Write(iso.Sign); break;
            default: throw new NotSupportedException(c.GetType().Name + " curves");
        }
    }

    static NCurve ReadCurve(BinaryReader r, NSurface[] surfaces)
    {
        var kind = (CurveKind)r.ReadByte();
        switch (kind)
        {
            case CurveKind.Line: return NLine.Raw(ReadV(r), ReadV(r));
            case CurveKind.Circle: return NCircle.Raw(ReadV(r), ReadV(r), ReadV(r), r.ReadDouble());
            case CurveKind.Ellipse: return NEllipse.Raw(ReadV(r), ReadV(r), ReadV(r), r.ReadDouble(), r.ReadDouble());
            case CurveKind.Hyperbola: return NHyperbola.Raw(ReadV(r), ReadV(r), ReadV(r), r.ReadDouble(), r.ReadDouble());
            case CurveKind.Parabola: return NParabola.Raw(ReadV(r), ReadV(r), ReadV(r), r.ReadDouble());
            case CurveKind.Spline:
                {
                    var fit = r.ReadDouble(); var degree = r.ReadInt32();
                    var poles = new Vec3[r.ReadInt32()];
                    for (int i = 0; i < poles.Length; i++) poles[i] = ReadV(r);
                    return new NSpline(new BSplineCurve(degree, poles, ReadDoubles(r))) { FitTol = fit };
                }
            case CurveKind.Helix: return new NHelix(ReadMotion(r), ReadV(r));
            case CurveKind.Iso: return new NIso(surfaces[r.ReadInt32()], r.ReadInt32(), r.ReadDouble(), r.ReadInt32());
            default: throw new InvalidDataException($"A SpryCAD solid file with an unknown kind of curve ({(int)kind}).");
        }
    }

    // ------------------------------------------------------------------------------------------

    static void V(BinaryWriter w, Vec3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    static Vec3 ReadV(BinaryReader r) { double x = r.ReadDouble(), y = r.ReadDouble(), z = r.ReadDouble(); return new Vec3(x, y, z); }
    static void Doubles(BinaryWriter w, double[] a) { w.Write(a.Length); foreach (var x in a) w.Write(x); }
    static double[] ReadDoubles(BinaryReader r) { var a = new double[r.ReadInt32()]; for (int i = 0; i < a.Length; i++) a[i] = r.ReadDouble(); return a; }
    static void Motion(BinaryWriter w, ScrewMotion m) { V(w, m.O); V(w, m.X); V(w, m.Y); V(w, m.A); w.Write(m.K); }
    static ScrewMotion ReadMotion(BinaryReader r) => new(ReadV(r), ReadV(r), ReadV(r), ReadV(r), r.ReadDouble());
}
