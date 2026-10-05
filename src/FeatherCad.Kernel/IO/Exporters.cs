using System.Globalization;
using System.Text;

namespace FeatherCad.Kernel;

/// <summary>Binary STL export (watertight: uses the stitched topology so T-junctions are resolved).</summary>
public static class StlWriter
{
    public static void Write(Stream stream, IEnumerable<Solid> solids, double scale = 1.0, string header = "FeatherCAD")
    {
        var tris = new List<Tri>();
        foreach (var s in solids) tris.AddRange(Triangles(s));
        using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        var h = new byte[80];
        Encoding.ASCII.GetBytes(header.Length > 80 ? header[..80] : header).CopyTo(h, 0);
        w.Write(h);
        w.Write((uint)tris.Count);
        foreach (var t in tris)
        {
            var n = Vec3.Cross(t.B - t.A, t.C - t.A).Normalized;
            WriteV(w, n, 1); WriteV(w, t.A, scale); WriteV(w, t.B, scale); WriteV(w, t.C, scale);
            w.Write((ushort)0);
        }
    }

    static void WriteV(BinaryWriter w, Vec3 v, double s) { w.Write((float)(v.X * s)); w.Write((float)(v.Y * s)); w.Write((float)(v.Z * s)); }

    public static List<Tri> Triangles(Solid s)
    {
        var topo = s.Topology;
        var res = new List<Tri>();
        for (int fi = 0; fi < s.Faces.Count; fi++)
        {
            var loops = topo.FaceLoops[fi].Select(l => l.Select(v => topo.Vertices[v]).ToArray()).ToList();
            res.AddRange(Triangulator.TriangulateLoops(loops, s.Faces[fi].Plane.Normal));
        }
        return res;
    }
}

/// <summary>
/// STEP AP214 export as MANIFOLD_SOLID_BREP. By default faces are written with their exact analytic
/// surfaces (PLANE, CYLINDRICAL_, CONICAL_, SPHERICAL_, TOROIDAL_SURFACE) bounded by exact LINE, CIRCLE and
/// B_SPLINE_CURVE_WITH_KNOTS edges. If the exact reconstruction fails the faceted form is written instead.
/// </summary>
public static class StepWriter
{
    sealed class Ctx
    {
        public readonly StringBuilder Sb = new();
        int _id;
        public int Add(string entity) { var id = ++_id; Sb.Append('#').Append(id).Append('=').Append(entity).Append(";\n"); return id; }
    }

    static string R(double v)
    {
        if (Math.Abs(v) < 1e-12) v = 0;
        var s = v.ToString("0.0##########", CultureInfo.InvariantCulture);
        return s;
    }

    static string Pt(Vec3 p) => $"CARTESIAN_POINT('',({R(p.X)},{R(p.Y)},{R(p.Z)}))";
    static string Dir(Vec3 d) => $"DIRECTION('',({R(d.X)},{R(d.Y)},{R(d.Z)}))";

    /// <summary>Why the last exact export fell back to facets (diagnostics).</summary>
    public static Exception? LastError;

    public static void Write(Stream stream, IEnumerable<(string name, Solid solid)> bodies, string productName, double scale = 1.0, bool exact = true)
    {
        var c = new Ctx();
        var appCtx = c.Add("APPLICATION_CONTEXT('core data for automotive mechanical design processes')");
        c.Add($"APPLICATION_PROTOCOL_DEFINITION('international standard','automotive_design',2000,#{appCtx})");
        var prodCtx = c.Add($"PRODUCT_CONTEXT('',#{appCtx},'mechanical')");
        var safe = Esc(productName);
        var product = c.Add($"PRODUCT('{safe}','{safe}','',(#{prodCtx}))");
        var pdf = c.Add($"PRODUCT_DEFINITION_FORMATION('','',#{product})");
        var pdc = c.Add($"PRODUCT_DEFINITION_CONTEXT('part definition',#{appCtx},'design')");
        var pd = c.Add($"PRODUCT_DEFINITION('design','',#{pdf},#{pdc})");
        var pds = c.Add($"PRODUCT_DEFINITION_SHAPE('','',#{pd})");
        c.Add($"PRODUCT_RELATED_PRODUCT_CATEGORY('part',$,(#{product}))");
        var lenUnit = c.Add("(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.))");
        var angUnit = c.Add("(NAMED_UNIT(*)PLANE_ANGLE_UNIT()SI_UNIT($,.RADIAN.))");
        var solUnit = c.Add("(NAMED_UNIT(*)SI_UNIT($,.STERADIAN.)SOLID_ANGLE_UNIT())");
        var unc = c.Add($"UNCERTAINTY_MEASURE_WITH_UNIT(LENGTH_MEASURE(1.E-06),#{lenUnit},'distance_accuracy_value','confusion accuracy')");
        var geomCtx = c.Add($"(GEOMETRIC_REPRESENTATION_CONTEXT(3)GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT((#{unc}))GLOBAL_UNIT_ASSIGNED_CONTEXT((#{lenUnit},#{angUnit},#{solUnit}))REPRESENTATION_CONTEXT('Context3D','3D Context with UNIT and UNCERTAINTY'))");
        var o = c.Add(Pt(Vec3.Zero)); var dz = c.Add(Dir(Vec3.UnitZ)); var dx = c.Add(Dir(Vec3.UnitX));
        var origin = c.Add($"AXIS2_PLACEMENT_3D('',#{o},#{dz},#{dx})");

        var items = new List<int> { origin };
        foreach (var (name, solid) in bodies)
        {
            var s = Math.Abs(scale - 1) > 1e-15 ? solid.Transformed(Transform.Scale(scale)) : solid;
            List<int>? shells = null;
            if (exact)
            {
                try
                {
                    var brep = ExactBrep.Build(s);
                    if (brep.Warnings.Count == 0) shells = WriteExact(c, brep, s);
                }
                catch (Exception ex) { LastError = ex; shells = null; }
            }
            shells ??= WriteSolid(c, s);
            foreach (var shell in shells)
                items.Add(c.Add($"MANIFOLD_SOLID_BREP('{Esc(name)}',#{shell})"));
        }
        var rep = c.Add($"ADVANCED_BREP_SHAPE_REPRESENTATION('{safe}',({string.Join(",", items.Select(i => "#" + i))}),#{geomCtx})");
        c.Add($"SHAPE_DEFINITION_REPRESENTATION(#{pds},#{rep})");

        var sb = new StringBuilder();
        sb.Append("ISO-10303-21;\nHEADER;\n");
        sb.Append("FILE_DESCRIPTION(('FeatherCAD model'),'2;1');\n");
        sb.Append($"FILE_NAME('{safe}.step','{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss}',(''),(''),'FeatherCAD','FeatherCAD','');\n");
        sb.Append("FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }'));\nENDSEC;\nDATA;\n");
        sb.Append(c.Sb);
        sb.Append("ENDSEC;\nEND-ISO-10303-21;\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        stream.Write(bytes, 0, bytes.Length);
    }

    static string Esc(string s) => s.Replace("'", "''");

    static int Axis2(Ctx c, Vec3 origin, Vec3 z, Vec3 x)
    {
        z = z.Normalized;
        x = (x - z * Vec3.Dot(x, z));
        if (x.Length < 1e-9) x = z.AnyPerpendicular();
        x = x.Normalized;
        var o = c.Add(Pt(origin)); var dz = c.Add(Dir(z)); var dx = c.Add(Dir(x));
        return c.Add($"AXIS2_PLACEMENT_3D('',#{o},#{dz},#{dx})");
    }

    /// <summary>Writes an exact B-rep. Returns one closed shell id per lump.</summary>
    static List<int> WriteExact(Ctx c, ExactBrep b, Solid s)
    {
        var vtx = new int[b.Vertices.Count];
        for (int i = 0; i < vtx.Length; i++) vtx[i] = c.Add($"VERTEX_POINT('',#{c.Add(Pt(b.Vertices[i]))})");

        var edgeIds = new int[b.Edges.Count];
        foreach (var e in b.Edges)
        {
            int geom;
            switch (e.Curve)
            {
                case LineCurve l:
                    {
                        var len = Math.Max(Vec3.Distance(b.Vertices[e.V0], b.Vertices[e.V1]), 1e-9);
                        var vec = c.Add($"VECTOR('',#{c.Add(Dir(l.Dir))},{R(len)})");
                        geom = c.Add($"LINE('',#{c.Add(Pt(b.Vertices[e.V0]))},#{vec})");
                        break;
                    }
                case CircleCurve ci:
                    geom = c.Add($"CIRCLE('',#{Axis2(c, ci.Center, ci.Axis, ci.RefDir)},{R(ci.Radius)})");
                    break;
                case EllipseCurve el:
                    // e.g. mitered fillet corners (equal-radius cylinders meeting at an angle)
                    geom = c.Add($"ELLIPSE('',#{Axis2(c, el.Center, el.Axis, el.XDir)},{R(el.Major)},{R(el.Minor)})");
                    break;
                case BSplineCurve bs:
                    {
                        var poles = string.Join(",", bs.Poles.Select(p => "#" + c.Add(Pt(p))));
                        var (vals, mults) = bs.KnotVector();
                        geom = c.Add($"B_SPLINE_CURVE_WITH_KNOTS('',{bs.Degree},({poles}),.UNSPECIFIED.,.F.,.F.,({string.Join(",", mults)}),({string.Join(",", vals.Select(R))}),.UNSPECIFIED.)");
                        break;
                    }
                default: throw new InvalidOperationException("unknown curve");
            }
            edgeIds[e.Index] = c.Add($"EDGE_CURVE('',#{vtx[e.V0]},#{vtx[e.V1]},#{geom},.T.)");
        }

        var facesByLump = new Dictionary<int, List<int>>();
        foreach (var f in b.Faces)
        {
            if (f.Loops.Count == 0) continue;
            int surf = WriteSurface(c, f, b);
            // planar faces: the loop enclosing the largest area is the outer bound
            int outer = -1;
            if (f.Surface.Kind == SurfaceKind.Plane)
            {
                double best = double.NegativeInfinity;
                for (int li = 0; li < f.Loops.Count; li++)
                {
                    var area = LoopArea(b, f.Loops[li], f.Surface.Plane!.Normal);
                    if (area > best) { best = area; outer = li; }
                }
            }
            var bounds = new List<int>();
            for (int li = 0; li < f.Loops.Count; li++)
            {
                var oes = f.Loops[li].Edges.Select(x => c.Add($"ORIENTED_EDGE('',*,*,#{edgeIds[x.Edge]},{(x.Forward ? ".T." : ".F.")})")).ToList();
                var loop = c.Add($"EDGE_LOOP('',({string.Join(",", oes.Select(i => "#" + i))}))");
                bounds.Add(c.Add(li == outer ? $"FACE_OUTER_BOUND('',#{loop},.T.)" : $"FACE_BOUND('',#{loop},.T.)"));
            }
            var face = c.Add($"ADVANCED_FACE('{Esc(f.Name)}',({string.Join(",", bounds.Select(i => "#" + i))}),#{surf},{(f.SameSense ? ".T." : ".F.")})");
            if (!facesByLump.TryGetValue(f.Lump, out var list)) facesByLump[f.Lump] = list = new List<int>();
            list.Add(face);
        }
        return facesByLump.Values.Select(fs => c.Add($"CLOSED_SHELL('',({string.Join(",", fs.Select(i => "#" + i))}))")).ToList();
    }

    static double LoopArea(ExactBrep b, ExactLoop loop, Vec3 n)
    {
        var pts = new List<Vec3>();
        foreach (var (ei, fwd) in loop.Edges)
        {
            var e = b.Edges[ei];
            var p = fwd ? e.Points : Enumerable.Reverse(e.Points).ToList();
            pts.AddRange(p.Take(p.Count - 1));
        }
        var sum = Vec3.Zero;
        for (int i = 0; i < pts.Count; i++) sum += Vec3.Cross(pts[i], pts[(i + 1) % pts.Count]);
        return Math.Abs(Vec3.Dot(sum, n)) / 2;
    }

    static IEnumerable<Vec3> BoundaryPoints(ExactFace f, ExactBrep b)
        => f.Loops.SelectMany(l => l.Edges).SelectMany(x => b.Edges[x.Item1].Points);

    static int WriteSurface(Ctx c, ExactFace f, ExactBrep b)
    {
        var d = f.Surface.Def;
        switch (f.Surface.Kind)
        {
            case SurfaceKind.Plane:
                {
                    var pl = f.Surface.Plane!;
                    var anchor = pl.Project(BoundaryPoints(f, b).First());
                    return c.Add($"PLANE('',#{Axis2(c, anchor, pl.Normal, pl.Normal.AnyPerpendicular())})");
                }
            case SurfaceKind.Cylinder:
                return c.Add($"CYLINDRICAL_SURFACE('',#{Axis2(c, d.Origin, d.Axis, d.RefDir)},{R(d.Radius)})");
            case SurfaceKind.Sphere:
                return c.Add($"SPHERICAL_SURFACE('',#{Axis2(c, d.Origin, d.Axis, d.RefDir)},{R(d.Radius)})");
            case SurfaceKind.Torus:
                return c.Add($"TOROIDAL_SURFACE('',#{Axis2(c, d.Origin, d.Axis, d.RefDir)},{R(d.Radius)},{R(d.Param2)})");
            case SurfaceKind.Cone:
                {
                    // place the cone's reference circle inside the face so its radius is positive
                    var ax = d.Axis.Normalized;
                    double hmax = double.MinValue;
                    foreach (var p in BoundaryPoints(f, b)) hmax = Math.Max(hmax, Vec3.Dot(p - d.Origin, ax));
                    var r = d.Radius + hmax * Math.Tan(d.Param2);
                    if (r < 1e-9) { hmax += 1; r = d.Radius + hmax * Math.Tan(d.Param2); }
                    return c.Add($"CONICAL_SURFACE('',#{Axis2(c, d.Origin + ax * hmax, ax, d.RefDir)},{R(r)},{R(d.Param2)})");
                }
        }
        throw new InvalidOperationException("unknown surface");
    }


    /// <summary>Writes the closed shells of a solid (one per connected lump). Returns shell ids.</summary>
    static List<int> WriteSolid(Ctx c, Solid s)
    {
        var topo = s.Topology;
        int nf = s.Faces.Count;
        // half-edge owner map for lump detection
        var owner = new Dictionary<(int, int), int>();
        for (int fi = 0; fi < nf; fi++)
            foreach (var l in topo.FaceLoops[fi])
                for (int i = 0; i < l.Length; i++) owner.TryAdd((l[i], l[(i + 1) % l.Length]), fi);
        var parent = Enumerable.Range(0, nf).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var ((a, b), f) in owner)
            if (owner.TryGetValue((b, a), out var g)) parent[Find(f)] = Find(g);

        var vtx = new Dictionary<int, int>();
        int Vertex(int v)
        {
            if (vtx.TryGetValue(v, out var id)) return id;
            var p = c.Add(Pt(topo.Vertices[v]));
            return vtx[v] = c.Add($"VERTEX_POINT('',#{p})");
        }
        var edges = new Dictionary<(int, int), int>(); // keyed (min,max)
        int Edge(int a, int b, out bool sense)
        {
            var key = a < b ? (a, b) : (b, a);
            sense = a < b;
            if (edges.TryGetValue(key, out var id)) return id;
            var pa = topo.Vertices[key.Item1]; var pb = topo.Vertices[key.Item2];
            var d = pb - pa; var len = d.Length;
            var pt = c.Add(Pt(pa));
            var dir = c.Add(Dir(len > 0 ? d / len : Vec3.UnitX));
            var vec = c.Add($"VECTOR('',#{dir},{R(Math.Max(len, 1e-9))})");
            var line = c.Add($"LINE('',#{pt},#{vec})");
            return edges[key] = c.Add($"EDGE_CURVE('',#{Vertex(key.Item1)},#{Vertex(key.Item2)},#{line},.T.)");
        }

        var facesByLump = new Dictionary<int, List<int>>();
        for (int fi = 0; fi < nf; fi++)
        {
            var f = s.Faces[fi];
            var bounds = new List<int>();
            for (int li = 0; li < topo.FaceLoops[fi].Count; li++)
            {
                var l = topo.FaceLoops[fi][li];
                if (l.Length < 3) continue;
                var oes = new List<int>();
                for (int i = 0; i < l.Length; i++)
                {
                    var e = Edge(l[i], l[(i + 1) % l.Length], out var sense);
                    oes.Add(c.Add($"ORIENTED_EDGE('',*,*,#{e},{(sense ? ".T." : ".F.")})"));
                }
                var loop = c.Add($"EDGE_LOOP('',({string.Join(",", oes.Select(i => "#" + i))}))");
                bounds.Add(c.Add(li == 0 ? $"FACE_OUTER_BOUND('',#{loop},.T.)" : $"FACE_BOUND('',#{loop},.T.)"));
            }
            if (bounds.Count == 0) continue;
            var n = f.Plane.Normal;
            var p0 = c.Add(Pt(topo.Vertices[topo.FaceLoops[fi][0][0]]));
            var nz = c.Add(Dir(n));
            var nx = c.Add(Dir(n.AnyPerpendicular()));
            var ax = c.Add($"AXIS2_PLACEMENT_3D('',#{p0},#{nz},#{nx})");
            var plane = c.Add($"PLANE('',#{ax})");
            var face = c.Add($"ADVANCED_FACE('{Esc(f.Tag.Name)}',({string.Join(",", bounds.Select(i => "#" + i))}),#{plane},.T.)");
            var lump = Find(fi);
            if (!facesByLump.TryGetValue(lump, out var list)) facesByLump[lump] = list = new List<int>();
            list.Add(face);
        }
        var shells = new List<int>();
        foreach (var faces in facesByLump.Values)
            shells.Add(c.Add($"CLOSED_SHELL('',({string.Join(",", faces.Select(i => "#" + i))}))"));
        return shells;
    }
}
