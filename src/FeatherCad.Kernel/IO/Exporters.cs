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
                    ExactBrep brep;
                    using (Prof.Time("step.brep")) brep = ExactBrep.Build(s);
                    using (Prof.Time("step.write")) if (brep.Warnings.Count == 0) shells = WriteExact(c, brep, s);
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
        var _edges = Prof.Time("step.edges");
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

        _edges.Dispose();
        var facesByLump = new Dictionary<int, List<int>>();
        // blend (pipe) surfaces are fitted up front, in parallel (each face is independent)
        var pipes = b.Faces.Where(f => f.Loops.Count > 0 && f.Surface.Kind == SurfaceKind.Pipe).ToList();
        var fits = new BSplineSurface[pipes.Count];
        using (Prof.Time("step.pipefits")) Parallel.For(0, pipes.Count, i => fits[i] = PipeFit(pipes[i], b));
        var fitOf = new Dictionary<ExactFace, BSplineSurface>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < pipes.Count; i++) fitOf[pipes[i]] = fits[i];
        foreach (var f in b.Faces)
        {
            if (f.Loops.Count == 0) continue;
            int surf = fitOf.TryGetValue(f, out var fit) ? WriteBSplineSurface(c, fit) : WriteSurface(c, f, b);
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
            case SurfaceKind.Pipe: return WriteBSplineSurface(c, PipeFit(f, b));
            case SurfaceKind.BSpline:
            case SurfaceKind.Offset:
                {
                    // faces reaching past their patch (onto its tangent extension, e.g. inner walls of a shell) have no
                    // STEP surface: those get a B-spline fitted over the face instead
                    var nb = d.Kind == SurfaceKind.BSpline ? d.Nurbs! : d.Base!.Nurbs!;
                    double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
                    foreach (var p in BoundaryPoints(f, b))
                    {
                        var q = nb.Closest(p);
                        u0 = Math.Min(u0, q.u); u1 = Math.Max(u1, q.u); v0 = Math.Min(v0, q.v); v1 = Math.Max(v1, q.v);
                    }
                    double eu = (nb.U1 - nb.U0) * 1e-9, ev = (nb.V1 - nb.V0) * 1e-9;
                    bool inside = u0 >= nb.U0 - eu && u1 <= nb.U1 + eu && v0 >= nb.V0 - ev && v1 <= nb.V1 + ev;
                    double off = d.Kind == SurfaceKind.Offset ? d.Radius : 0;
                    // (offsets of closed surfaces are fitted too: readers do not all handle them)
                    if (inside && (off == 0 || !(nb.ClosedU || nb.ClosedV)))
                        return off == 0 ? WriteNurbs(c, nb) : c.Add($"OFFSET_SURFACE('',#{WriteNurbs(c, nb)},{R(off)},.F.)");
                    var du = (u1 - u0) * 0.02 + 1e-9; var dv = (v1 - v0) * 0.02 + 1e-9;
                    // round a closed direction: exactly one period (a padded fit would overlap itself)
                    if (nb.ClosedU && u1 - u0 > 0.9 * (nb.U1 - nb.U0)) { u0 = nb.U0; u1 = nb.U1; du = 0; }
                    if (nb.ClosedV && v1 - v0 > 0.9 * (nb.V1 - nb.V0)) { v0 = nb.V0; v1 = nb.V1; dv = 0; }
                    var fit = BSplineSurface.Fit((u, v) => nb.Eval(u, v) + (off == 0 ? Vec3.Zero : nb.Normal(u, v) * off),
                        u0 - du, u1 + du, v0 - dv, v1 + dv, 1e-5, 8, 8);
                    return WriteBSplineSurface(c, fit);
                }
        }
        throw new InvalidOperationException("unknown surface");
    }

    /// <summary>No pipe surface in AP214: a bicubic B-spline fitted over the face's (angle, spine) box.</summary>
    static BSplineSurface PipeFit(ExactFace f, ExactBrep b)
    {
        var d = f.Surface.Def;
        var sp = d.Spine!; var rad = d.Radius;
        // the pipe's own parameterization is only C1 along the spine (Hermite nodes) and so fits slowly; fit the same
        // surface through a C2 spline of the spine nodes (same points within ~1e-8) with a smooth frame instead
        var range = BoundaryPoints(f, b).Select(p => sp.Closest(p).s).ToList();
        double s0 = range.Min(), s1 = range.Max(), pad = (s1 - s0) * 0.05 + 1e-3;
        var nodes = Enumerable.Range(0, sp.P.Length).Where(i => sp.S[i] >= s0 - pad && sp.S[i] <= s1 + pad).ToList();
        if (nodes.Count < 4)
        {
            // too few nodes in range: sample the Hermite spine instead
            nodes.Clear();
        }
        double sa = nodes.Count > 0 ? sp.S[nodes[0]] : Math.Max(sp.S0, s0 - pad), sb = nodes.Count > 0 ? sp.S[nodes[^1]] : Math.Min(sp.S1, s1 + pad);
        var pts = nodes.Count > 0 ? nodes.Select(i => sp.P[i]).ToList() : Enumerable.Range(0, 9).Select(k => sp.Eval(sa + (sb - sa) * k / 8)).ToList();
        var c2 = BSplineCurve.Interpolate(pts, 3);
        // chord-length parameters of the interpolation ≈ spine arc length: s ↦ t linear
        double T(double s) => Math.Clamp((s - sa) / (sb - sa), 0, 1);
        Vec3 C(double s) => c2.Eval(T(s));
        Vec3 Tan(double s) { const double h = 1e-6; var t = T(s); double a = Math.Max(0, t - h), z = Math.Min(1, t + h); return (c2.Eval(z) - c2.Eval(a)).Normalized; }
        // frame: a fixed reference direction made perpendicular to the tangent (the normal of the plane the spine turns
        // in, else any direction across it)
        var refv = Vec3.Zero;
        for (int k = 0; k < 16; k++) refv += Vec3.Cross(Tan(sa + (sb - sa) * k / 16), Tan(sa + (sb - sa) * (k + 1) / 16));
        refv = refv.Length > 1e-9 ? refv.Normalized : Tan(sa).AnyPerpendicular();
        (Vec3 n1, Vec3 n2) Frame(double s) { var t = Tan(s); var n = (refv - t * Vec3.Dot(refv, t)).Normalized; return (n, Vec3.Cross(t, n)); }
        Vec3 Surface(double th, double s) { var (n1, n2) = Frame(s); return C(s) + (n1 * Math.Cos(th) + n2 * Math.Sin(th)) * rad; }
        double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue, hint = double.NaN;
        foreach (var p in BoundaryPoints(f, b))
        {
            var s = sp.Closest(p).s;
            var (n1, n2) = Frame(s); var r = p - C(s);
            var th = Math.Atan2(Vec3.Dot(r, n2), Vec3.Dot(r, n1));
            if (!double.IsNaN(hint)) th = Native.NCurve.Wrap(th, hint);
            hint = th;
            u0 = Math.Min(u0, th); u1 = Math.Max(u1, th); v0 = Math.Min(v0, s); v1 = Math.Max(v1, s);
        }
        var du = (u1 - u0) * 0.02 + 1e-4; var dv = (v1 - v0) * 0.02 + 1e-4;
        u0 -= du; u1 += du; v0 = Math.Max(sa, v0 - dv); v1 = Math.Min(sb, v1 + dv);
        // start near the final size (cubic interpolation error ~ h⁴ / (384 ρ³) for curvature radius ρ): around
        // the pipe ρ is its radius, along it the tightest bend of the surface (inner side of the spine)
        const double fitTol = 1e-5;
        var du0 = Math.Pow(384 * fitTol / Math.Max(rad, 1e-3), 0.25);
        var bend = Math.Max(Math.Min(sp.MinCurvatureRadius - rad, 1e6), rad * 0.25);
        var dv0 = Math.Pow(384 * fitTol * bend * bend * bend, 0.25);
        return BSplineSurface.Fit(Surface, u0, u1, v0, v1, fitTol,
            Math.Max(4, (int)Math.Ceiling((u1 - u0) / du0)), Math.Clamp((int)Math.Ceiling((v1 - v0) / dv0), 4, 192));
    }

    /// <summary>A B-spline surface as it is (rational ones as the complex entity STEP uses).</summary>
    static int WriteNurbs(Ctx c, Native.NurbsSurface s)
    {
        int nu = s.P.GetLength(0), nv = s.P.GetLength(1);
        var rows = new List<string>();
        for (int i = 0; i < nu; i++)
            rows.Add("(" + string.Join(",", Enumerable.Range(0, nv).Select(j => "#" + c.Add(Pt(s.P[i, j])))) + ")");
        var (ku, mu) = BSplineSurface.KnotVector(s.U);
        var (kv, mv) = BSplineSurface.KnotVector(s.V);
        var poles = $"({string.Join(",", rows)})";
        var knots = $"({string.Join(",", mu)}),({string.Join(",", mv)}),({string.Join(",", ku.Select(R))}),({string.Join(",", kv.Select(R))}),.UNSPECIFIED.";
        if (s.W == null)
            return c.Add($"B_SPLINE_SURFACE_WITH_KNOTS('',{s.Pu},{s.Pv},{poles},.UNSPECIFIED.,.F.,.F.,.F.,{knots})");
        var wrows = string.Join(",", Enumerable.Range(0, nu).Select(i => "(" + string.Join(",", Enumerable.Range(0, nv).Select(j => R(s.W[i, j]))) + ")"));
        return c.Add($"(BOUNDED_SURFACE()B_SPLINE_SURFACE({s.Pu},{s.Pv},{poles},.UNSPECIFIED.,.F.,.F.,.F.)B_SPLINE_SURFACE_WITH_KNOTS({knots})" +
                     $"GEOMETRIC_REPRESENTATION_ITEM()RATIONAL_B_SPLINE_SURFACE(({wrows}))REPRESENTATION_ITEM('')SURFACE())");
    }

    static int WriteBSplineSurface(Ctx c, BSplineSurface srf)
    {
        int nu = srf.Poles.GetLength(0), nv = srf.Poles.GetLength(1);
        var rows = new List<string>();
        for (int i = 0; i < nu; i++)
            rows.Add("(" + string.Join(",", Enumerable.Range(0, nv).Select(j => "#" + c.Add(Pt(srf.Poles[i, j])))) + ")");
        var (ku, mu) = BSplineSurface.KnotVector(srf.KnotsU);
        var (kv, mv) = BSplineSurface.KnotVector(srf.KnotsV);
        return c.Add($"B_SPLINE_SURFACE_WITH_KNOTS('',{srf.DegreeU},{srf.DegreeV},({string.Join(",", rows)}),.UNSPECIFIED.,.F.,.F.,.F.," +
                     $"({string.Join(",", mu)}),({string.Join(",", mv)}),({string.Join(",", ku.Select(R))}),({string.Join(",", kv.Select(R))}),.UNSPECIFIED.)");
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
