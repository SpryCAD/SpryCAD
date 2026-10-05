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
/// STEP AP214 export as MANIFOLD_SOLID_BREP with planar ADVANCED_FACEs. Curved faces are written
/// as their exact planar facets for now (analytic surfaces are kept in the model for a later upgrade).
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

    public static void Write(Stream stream, IEnumerable<(string name, Solid solid)> bodies, string productName, double scale = 1.0)
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
            foreach (var shell in WriteSolid(c, s))
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
