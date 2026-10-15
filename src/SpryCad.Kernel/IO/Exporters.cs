using System.Globalization;
using System.Text;

namespace SpryCad.Kernel;

/// <summary>Binary STL export (watertight: uses the stitched topology so T-junctions are resolved).</summary>
public static class StlWriter
{
    public static void Write(Stream stream, IEnumerable<Solid> solids, double scale = 1.0, string header = "SpryCAD")
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
        int _ctx2d;
        /// <summary>The parameter space of surfaces, where pcurves are drawn (written once, when first used).</summary>
        public int Ctx2D => _ctx2d != 0 ? _ctx2d
            : _ctx2d = Add("(GEOMETRIC_REPRESENTATION_CONTEXT(2)PARAMETRIC_REPRESENTATION_CONTEXT()REPRESENTATION_CONTEXT('2D SPACE',''))");
    }

    static string R(double v)
    {
        if (Math.Abs(v) < 1e-12) v = 0;
        // as "0.0##########" (at least one decimal, at most eleven), by the standard fixed format and the zeros trimmed:
        // several times faster (a big part writes hundreds of thousands of numbers)
        Span<char> buf = stackalloc char[96];
        if (!v.TryFormat(buf, out int n, "F11", CultureInfo.InvariantCulture)) return v.ToString("0.0##########", CultureInfo.InvariantCulture);
        while (n > 2 && buf[n - 1] == '0' && buf[n - 2] != '.') n--;
        return new string(buf[..n]);
    }

    static string Pt(Vec3 p) => $"CARTESIAN_POINT('',({R(p.X)},{R(p.Y)},{R(p.Z)}))";
    static string Dir(Vec3 d) => $"DIRECTION('',({R(d.X)},{R(d.Y)},{R(d.Z)}))";

    /// <summary>Why the last exact export fell back to facets (diagnostics).</summary>
    public static Exception? LastError;

    public static void Write(Stream stream, IEnumerable<(string name, Solid solid)> bodies, string productName, double scale = 1.0, bool exact = true)
    {
        var c = new Ctx();
        var (appCtx, prodCtx, pdc, geomCtx) = Contexts(c);
        var safe = Esc(productName);
        var (_, pds, _) = Product(c, productName, prodCtx, pdc);
        var items = new List<int> { Origin(c) };
        items.AddRange(Bodies(c, bodies, scale, exact));
        var rep = c.Add($"ADVANCED_BREP_SHAPE_REPRESENTATION('{safe}',({string.Join(",", items.Select(i => "#" + i))}),#{geomCtx})");
        c.Add($"SHAPE_DEFINITION_REPRESENTATION(#{pds},#{rep})");
        Finish(stream, c, safe);
    }

    /// <summary>
    /// An assembly as AP214 writes them: a product per part (its solids in its own coordinates), the assembly's
    /// product, and per component a NEXT_ASSEMBLY_USAGE_OCCURRENCE whose placement is a
    /// REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION from the part's shape to the assembly's (an
    /// ITEM_DEFINED_TRANSFORMATION from the part's origin to the component's frame). Rigid placements only.
    /// </summary>
    public static void WriteAssembly(Stream stream, string assemblyName, IReadOnlyList<(string name, IReadOnlyList<(string name, Solid solid)> bodies)> parts,
        IReadOnlyList<(int part, string name, Transform placement)> components, double scale = 1.0, bool exact = true)
    {
        var c = new Ctx();
        var (appCtx, prodCtx, pdc, geomCtx) = Contexts(c);
        var (_, asmPds, asmPd) = Product(c, assemblyName, prodCtx, pdc);
        var asmItems = new List<int> { Origin(c) };
        var partRep = new int[parts.Count]; var partPd = new int[parts.Count]; var partOrigin = new int[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            var (pname, bodies) = parts[i];
            var (_, pds, pd) = Product(c, pname, prodCtx, pdc);
            partPd[i] = pd;
            partOrigin[i] = Origin(c);
            var items = new List<int> { partOrigin[i] };
            items.AddRange(Bodies(c, bodies, scale, exact));
            partRep[i] = c.Add($"ADVANCED_BREP_SHAPE_REPRESENTATION('{Esc(pname)}',({string.Join(",", items.Select(x => "#" + x))}),#{geomCtx})");
            c.Add($"SHAPE_DEFINITION_REPRESENTATION(#{pds},#{partRep[i]})");
        }
        // the assembly's shape: its origin and each component's frame
        var frames = new List<int>();
        foreach (var (_, _, t) in components)
        {
            var o = c.Add(Pt(t.T * scale)); var z = c.Add(Dir(t.Vector(Vec3.UnitZ))); var x = c.Add(Dir(t.Vector(Vec3.UnitX)));
            frames.Add(c.Add($"AXIS2_PLACEMENT_3D('',#{o},#{z},#{x})"));
        }
        asmItems.AddRange(frames);
        var asmRep = c.Add($"SHAPE_REPRESENTATION('{Esc(assemblyName)}',({string.Join(",", asmItems.Select(x => "#" + x))}),#{geomCtx})");
        c.Add($"SHAPE_DEFINITION_REPRESENTATION(#{asmPds},#{asmRep})");
        for (int k = 0; k < components.Count; k++)
        {
            var (pi, cname, _) = components[k];
            var nauo = c.Add($"NEXT_ASSEMBLY_USAGE_OCCURRENCE('{k + 1}','{Esc(cname)}','',#{asmPd},#{partPd[pi]},$)");
            var ipds = c.Add($"PRODUCT_DEFINITION_SHAPE('{Esc(cname)}','',#{nauo})");
            var idt = c.Add($"ITEM_DEFINED_TRANSFORMATION('','',#{partOrigin[pi]},#{frames[k]})");
            var rel = c.Add($"(REPRESENTATION_RELATIONSHIP('','',#{partRep[pi]},#{asmRep})REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#{idt})SHAPE_REPRESENTATION_RELATIONSHIP())");
            c.Add($"CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#{rel},#{ipds})");
        }
        Finish(stream, c, Esc(assemblyName));
    }

    static (int appCtx, int prodCtx, int pdc, int geomCtx) Contexts(Ctx c)
    {
        var appCtx = c.Add("APPLICATION_CONTEXT('core data for automotive mechanical design processes')");
        c.Add($"APPLICATION_PROTOCOL_DEFINITION('international standard','automotive_design',2000,#{appCtx})");
        var prodCtx = c.Add($"PRODUCT_CONTEXT('',#{appCtx},'mechanical')");
        var pdc = c.Add($"PRODUCT_DEFINITION_CONTEXT('part definition',#{appCtx},'design')");
        var lenUnit = c.Add("(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.))");
        var angUnit = c.Add("(NAMED_UNIT(*)PLANE_ANGLE_UNIT()SI_UNIT($,.RADIAN.))");
        var solUnit = c.Add("(NAMED_UNIT(*)SI_UNIT($,.STERADIAN.)SOLID_ANGLE_UNIT())");
        var unc = c.Add($"UNCERTAINTY_MEASURE_WITH_UNIT(LENGTH_MEASURE(1.E-06),#{lenUnit},'distance_accuracy_value','confusion accuracy')");
        var geomCtx = c.Add($"(GEOMETRIC_REPRESENTATION_CONTEXT(3)GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT((#{unc}))GLOBAL_UNIT_ASSIGNED_CONTEXT((#{lenUnit},#{angUnit},#{solUnit}))REPRESENTATION_CONTEXT('Context3D','3D Context with UNIT and UNCERTAINTY'))");
        return (appCtx, prodCtx, pdc, geomCtx);
    }

    /// <summary>A product and its definition: (product, product definition shape, product definition).</summary>
    static (int product, int pds, int pd) Product(Ctx c, string name, int prodCtx, int pdc)
    {
        var safe = Esc(name);
        var product = c.Add($"PRODUCT('{safe}','{safe}','',(#{prodCtx}))");
        var pdf = c.Add($"PRODUCT_DEFINITION_FORMATION('','',#{product})");
        var pd = c.Add($"PRODUCT_DEFINITION('design','',#{pdf},#{pdc})");
        var pds = c.Add($"PRODUCT_DEFINITION_SHAPE('','',#{pd})");
        c.Add($"PRODUCT_RELATED_PRODUCT_CATEGORY('part',$,(#{product}))");
        return (product, pds, pd);
    }

    static int Origin(Ctx c)
    {
        var o = c.Add(Pt(Vec3.Zero)); var dz = c.Add(Dir(Vec3.UnitZ)); var dx = c.Add(Dir(Vec3.UnitX));
        return c.Add($"AXIS2_PLACEMENT_3D('',#{o},#{dz},#{dx})");
    }

    /// <summary>Solids as MANIFOLD_SOLID_BREPs (exact; faceted if that fails): their ids.</summary>
    static List<int> Bodies(Ctx c, IEnumerable<(string name, Solid solid)> bodies, double scale, bool exact)
    {
        var items = new List<int>();
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
        return items;
    }

    static void Finish(Stream stream, Ctx c, string safe)
    {
        var sb = new StringBuilder();
        sb.Append("ISO-10303-21;\nHEADER;\n");
        sb.Append("FILE_DESCRIPTION(('SpryCAD model'),'2;1');\n");
        sb.Append($"FILE_NAME('{safe}.step','{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss}',(''),(''),'SpryCAD','SpryCAD','');\n");
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

        // the faces' surfaces first: the pcurves of edges on B-spline faces refer to them. Blend (pipe) and screw
        // surfaces are fitted up front, in parallel (each face is independent); screw faces that are cylinders are
        // written exactly
        var pipes = b.Faces.Where(f => f.Loops.Count > 0 && (f.Surface.Kind == SurfaceKind.Pipe || f.Surface.Kind == SurfaceKind.Screw && ScrewCylinder(f) == null)).ToList();
        var fits = new BSplineSurface[pipes.Count];
        using (Prof.Time("step.fits")) Parallel.For(0, pipes.Count, i => fits[i] = pipes[i].Surface.Kind == SurfaceKind.Pipe ? PipeFit(pipes[i], b) : ScrewFit(pipes[i], b));
        var fitOf = new Dictionary<ExactFace, BSplineSurface>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < pipes.Count; i++) fitOf[pipes[i]] = fits[i];
        var surfIds = new int[b.Faces.Count];
        var senses = new bool[b.Faces.Count];
        var param = new ParamSurface?[b.Faces.Count];
        foreach (var f in b.Faces)
        {
            if (f.Loops.Count == 0) continue;
            var sameSense = f.SameSense;
            int surf;
            ParamSurface? ps = null;
            if (fitOf.TryGetValue(f, out var fit)) { surf = WriteBSplineSurface(c, fit); ps = ParamSurface.Of(fit); }
            else if (ScrewCylinder(f) is { } cyl)
            {
                // a screw surface swept by a line along its axis (a thread's root or crest) is a cylinder: written
                // exactly, its sense taken against the cylinder's outward normal
                surf = c.Add($"CYLINDRICAL_SURFACE('',#{Axis2(c, cyl.origin, cyl.axis, cyl.refDir)},{R(cyl.r)})");
                if (!cyl.outward) sameSense = !sameSense;
            }
            else surf = WriteSurface(c, f, b, out ps);
            surfIds[f.Index] = surf; senses[f.Index] = sameSense; param[f.Index] = ps;
        }
        var pcTol = 1e-4 * Math.Max(1, s.Bounds.Diagonal / 100);
        // the edges' parameter curves on B-spline faces, worked out in parallel (each edge on its own), written below
        var pc2d = new BSplineCurve?[b.Edges.Count, 2];
        using (Prof.Time("step.pcurves"))
            Parallel.For(0, b.Edges.Count, k =>
            {
                var e = b.Edges[k];
                if (e.FaceA == e.FaceB) return;
                for (int side = 0; side < 2; side++)
                    if ((side == 0 ? e.FaceA : e.FaceB) is var fi and >= 0 && param[fi] is { } ps) pc2d[k, side] = ParamCurve(ps, e, pcTol);
            });

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
                case HyperbolaCurve hy:
                    // a plane across a cone (e.g. a gear tooth's flank across its chamfer)
                    geom = c.Add($"HYPERBOLA('',#{Axis2(c, hy.Center, hy.Axis, hy.XDir)},{R(hy.Semi)},{R(hy.SemiImag)})");
                    break;
                case ParabolaCurve pa:
                    geom = c.Add($"PARABOLA('',#{Axis2(c, pa.Center, pa.Axis, pa.XDir)},{R(pa.Focal)})");
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
            // on B-spline faces the edge also in the face's parameters (a pcurve each): readers need not find them by
            // projecting it, which is slow and ambiguous on a surface wrapping round (a thread's turns)
            {
                var pcs = new List<int>();
                for (int side = 0; side < 2; side++)
                    if (pc2d[e.Index, side] is { } pc) pcs.Add(WritePcurve(c, pc, surfIds[side == 0 ? e.FaceA : e.FaceB]));
                if (pcs.Count > 0) geom = c.Add($"SURFACE_CURVE('',#{geom},({string.Join(",", pcs.Select(i => "#" + i))}),.CURVE_3D.)");
            }
            edgeIds[e.Index] = c.Add($"EDGE_CURVE('',#{vtx[e.V0]},#{vtx[e.V1]},#{geom},.T.)");
        }

        _edges.Dispose();
        var facesByLump = new Dictionary<int, List<int>>();
        foreach (var f in b.Faces)
        {
            if (f.Loops.Count == 0) continue;
            var sameSense = senses[f.Index];
            int surf = surfIds[f.Index];
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
            var face = c.Add($"ADVANCED_FACE('{Esc(f.Name)}',({string.Join(",", bounds.Select(i => "#" + i))}),#{surf},{(sameSense ? ".T." : ".F.")})");
            if (!facesByLump.TryGetValue(f.Lump, out var list)) facesByLump[f.Lump] = list = new List<int>();
            list.Add(face);
        }
        return facesByLump.Values.Select(fs => c.Add($"CLOSED_SHELL('',({string.Join(",", fs.Select(i => "#" + i))}))")).ToList();
    }

    /// <summary>A surface as written: (u, v) to a point over its parameter box (B-spline faces, where pcurves are written).</summary>
    sealed record ParamSurface(Func<double, double, Vec3> Eval, double U0, double U1, double V0, double V1)
    {
        /// <summary>A first guess at a point's parameters (without it, the nearest of a grid of samples).</summary>
        public Func<Vec3, Vec2>? Seed { get; init; }

        public static ParamSurface Of(BSplineSurface f)
        {
            int nu = f.Poles.GetLength(0) - 1, nv = f.Poles.GetLength(1) - 1;
            // the control points lie close to the surface, each near its Greville parameters: the nearest one's
            static double[] Greville(double[] k, int p, int n) =>
                Enumerable.Range(0, n + 1).Select(i => Enumerable.Range(i + 1, p).Sum(j => k[j]) / p).ToArray();
            var gu = Greville(f.KnotsU, f.DegreeU, nu); var gv = Greville(f.KnotsV, f.DegreeV, nv);
            return new ParamSurface(f.Eval, f.KnotsU[f.DegreeU], f.KnotsU[nu + 1], f.KnotsV[f.DegreeV], f.KnotsV[nv + 1])
            {
                Seed = p =>
                {
                    double best = double.MaxValue; int bi = 0, bj = 0;
                    for (int i = 0; i <= nu; i++)
                        for (int j = 0; j <= nv; j++)
                        {
                            var d = (f.Poles[i, j] - p).LengthSq;
                            if (d < best) { best = d; bi = i; bj = j; }
                        }
                    return new Vec2(gu[bi], gv[bj]);
                },
            };
        }

        /// <summary>
        /// The parameters of a point of the surface: by Gauss-Newton from <paramref name="hint"/> (the point before
        /// along an edge, so a surface wrapping round stays on the same turn), else from the nearest of a grid of
        /// samples. Null when it does not come within tol.
        /// </summary>
        public Vec2? Find(Vec3 p, Vec2? hint, double tol, bool retried = false)
        {
            Vec2 uv;
            if (hint is { } h) uv = h;
            else if (Seed != null && !retried) uv = Seed(p);
            else
            {
                const int gu = 240, gv = 40;
                double best = double.MaxValue; uv = new Vec2(U0, V0);
                for (int i = 0; i <= gu; i++)
                    for (int j = 0; j <= gv; j++)
                    {
                        var q = new Vec2(U0 + (U1 - U0) * i / gu, V0 + (V1 - V0) * j / gv);
                        var d = (Eval(q.X, q.Y) - p).LengthSq;
                        if (d < best) { best = d; uv = q; }
                    }
                // (zoomed in round the nearest, a few times: where the surface narrows to a point (a fading fillet's tip,
                // a corner patch's), the grid's step along it is far wider than the surface is there, and Newton from
                // its nearest sample stalls)
                // (at such a point every u is the same place: the first zooms take all of u, along a narrower v)
                double wu = (U1 - U0) / gu, wv = (V1 - V0) / gv;
                for (int zoom = 0; zoom < 5; zoom++)
                {
                    var c0 = uv;
                    bool allU = zoom < 2;
                    int nu = allU ? gu : 10;
                    for (int i = allU ? 0 : -10; i <= nu; i++)
                        for (int j = -10; j <= 10; j++)
                        {
                            var qu = allU ? U0 + (U1 - U0) * i / gu : Math.Clamp(c0.X + wu * i / 10, U0, U1);
                            var q = new Vec2(qu, Math.Clamp(c0.Y + wv * j / 10, V0, V1));
                            var d = (Eval(q.X, q.Y) - p).LengthSq;
                            if (d < best) { best = d; uv = q; }
                        }
                    if (!allU) wu /= 5;
                    wv /= 5;
                }
            }
            double hu = 1e-6 * (U1 - U0), hv = 1e-6 * (V1 - V0);
            // (Newton from here may wander off where the surface narrows: the start kept where it ends farther)
            var start = uv; var startD = Vec3.Distance(Eval(uv.X, uv.Y), p);
            for (int it = 0; it < 40; it++)
            {
                var r = Eval(uv.X, uv.Y) - p;
                if (r.Length < tol * 1e-3) break;
                var su = (Eval(Math.Min(U1, uv.X + hu), uv.Y) - Eval(Math.Max(U0, uv.X - hu), uv.Y)) / (Math.Min(U1, uv.X + hu) - Math.Max(U0, uv.X - hu));
                var sv = (Eval(uv.X, Math.Min(V1, uv.Y + hv)) - Eval(uv.X, Math.Max(V0, uv.Y - hv))) / (Math.Min(V1, uv.Y + hv) - Math.Max(V0, uv.Y - hv));
                double a = Vec3.Dot(su, su), bb = Vec3.Dot(su, sv), cc = Vec3.Dot(sv, sv), det = a * cc - bb * bb;
                if (!(det > 1e-300)) break;
                double gu2 = Vec3.Dot(su, r), gv2 = Vec3.Dot(sv, r);
                var du = -(cc * gu2 - bb * gv2) / det; var dv = -(a * gv2 - bb * gu2) / det;
                var next = new Vec2(Math.Clamp(uv.X + du, U0, U1), Math.Clamp(uv.Y + dv, V0, V1));
                var moved = Math.Abs(next.X - uv.X) / (U1 - U0) + Math.Abs(next.Y - uv.Y) / (V1 - V0);
                uv = next;
                if (moved < 1e-15) break;
            }
            if (Vec3.Distance(Eval(uv.X, uv.Y), p) > startD) uv = start;
            if (Vec3.Distance(Eval(uv.X, uv.Y), p) <= tol) return uv;
            // a hint or a seed that led astray: once more from the seed, then from the grid
            if (hint != null) return Find(p, null, tol);
            return Seed != null && !retried ? Find(p, null, tol, retried: true) : null;
        }
    }

    /// <summary>
    /// An edge's curve in a B-spline face's parameters: its points' parameters (followed along it), a 2D cubic
    /// through them (z = 0). Null when a point is not on the surface within tol.
    /// </summary>
    static BSplineCurve? ParamCurve(ParamSurface ps, ExactEdge e, double tol)
    {
        var pts = e.Points;
        if (pts.Count < 2) return null;
        // (at most 256 points: the parameter curve is smooth, a helix on a screw face nearly straight)
        int stride = Math.Max(1, (pts.Count + 255) / 256);
        var sel = new List<Vec3>();
        for (int i = 0; i < pts.Count; i += stride) sel.Add(pts[i]);
        if (sel[^1] != pts[^1]) sel.Add(pts[^1]);
        var uvs = new List<Vec3>(sel.Count);
        Vec2? hint = null;
        foreach (var p in sel)
        {
            if (ps.Find(p, hint, tol) is not { } uv) return null;
            uvs.Add(new Vec3(uv.X, uv.Y, 0)); hint = uv;
        }
        // (points the same in the parameters, e.g. at a pole, would make the interpolation singular)
        for (int i = uvs.Count - 2; i > 0; i--) if (Vec3.Distance(uvs[i], uvs[i - 1]) < 1e-14) { uvs.RemoveAt(i); sel.RemoveAt(i); }
        if (uvs.Count < 2 || Vec3.Distance(uvs[0], uvs[^1]) < 1e-14 && uvs.Count < 3) return null;
        // through every few of the points, as few as keep each point's place on the surface within tol: the
        // parameter curve is smooth (on a screw face a helix is nearly straight), the file stays small
        for (int every = Math.Max(1, uvs.Count / 16); ; every /= 2)
        {
            var idx = new List<int>();
            for (int i = 0; i < uvs.Count; i += every) idx.Add(i);
            if (idx[^1] != uvs.Count - 1) idx.Add(uvs.Count - 1);
            var bs = BSplineCurve.Interpolate(idx.Select(i => uvs[i]).ToList(), Math.Min(3, idx.Count - 1));
            if (every == 1) return bs;
            bool ok = true; double t = 0;
            for (int i = 0; i < uvs.Count && ok; i++)
            {
                t = bs.ClosestParam(uvs[i], 0, 1, t);
                var q = bs.Eval(t);
                if (Vec3.Distance(ps.Eval(q.X, q.Y), sel[i]) > tol) ok = false;
            }
            if (ok) return bs;
        }
    }

    /// <summary>A parameter curve as PCURVE(surface, its definitional representation).</summary>
    static int WritePcurve(Ctx c, BSplineCurve bs, int surf)
    {
        static string Pt2(Vec3 q) => $"CARTESIAN_POINT('',({R(q.X)},{R(q.Y)}))";
        var poles = string.Join(",", bs.Poles.Select(q => "#" + c.Add(Pt2(q))));
        var (vals, mults) = bs.KnotVector();
        var curve = c.Add($"B_SPLINE_CURVE_WITH_KNOTS('',{bs.Degree},({poles}),.UNSPECIFIED.,.F.,.F.,({string.Join(",", mults)}),({string.Join(",", vals.Select(R))}),.UNSPECIFIED.)");
        var rep = c.Add($"DEFINITIONAL_REPRESENTATION('',(#{curve}),#{c.Ctx2D})");
        return c.Add($"PCURVE('',#{surf},#{rep})");
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

    static int WriteSurface(Ctx c, ExactFace f, ExactBrep b, out ParamSurface? written)
    {
        written = null;
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
            case SurfaceKind.Pipe: { var pf = PipeFit(f, b); written = ParamSurface.Of(pf); return WriteBSplineSurface(c, pf); }
            case SurfaceKind.Screw: { var sf = ScrewFit(f, b); written = ParamSurface.Of(sf); return WriteBSplineSurface(c, sf); }
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
                    {
                        Func<Vec3, Vec2> seed = p => { var q = nb.Closest(p); return new Vec2(q.u, q.v); };
                        written = off == 0 ? new ParamSurface(nb.Eval, nb.U0, nb.U1, nb.V0, nb.V1) { Seed = seed }
                            : new ParamSurface((u, v) => nb.Eval(u, v) + nb.Normal(u, v) * off, nb.U0, nb.U1, nb.V0, nb.V1) { Seed = seed };
                        return off == 0 ? WriteNurbs(c, nb) : c.Add($"OFFSET_SURFACE('',#{WriteNurbs(c, nb)},{R(off)},.F.)");
                    }
                    var du = (u1 - u0) * 0.02 + 1e-9; var dv = (v1 - v0) * 0.02 + 1e-9;
                    // round a closed direction: exactly one period (a padded fit would overlap itself)
                    if (nb.ClosedU && u1 - u0 > 0.9 * (nb.U1 - nb.U0)) { u0 = nb.U0; u1 = nb.U1; du = 0; }
                    if (nb.ClosedV && v1 - v0 > 0.9 * (nb.V1 - nb.V0)) { v0 = nb.V0; v1 = nb.V1; dv = 0; }
                    var fit = BSplineSurface.Fit((u, v) => nb.Eval(u, v) + (off == 0 ? Vec3.Zero : nb.Normal(u, v) * off),
                        u0 - du, u1 + du, v0 - dv, v1 + dv, 1e-5, 8, 8);
                    written = ParamSurface.Of(fit);
                    return WriteBSplineSurface(c, fit);
                }
        }
        throw new InvalidOperationException("unknown surface");
    }

    /// <summary>No pipe surface in AP214: a bicubic B-spline fitted over the face's (angle, spine) box.</summary>
    /// <summary>STEP has no helical surface: a B-spline fitted over the face's (u, v) box.</summary>
    /// <summary>
    /// The cylinder a screw face lies on when its generator is a line along the motion's axis (the radius its distance
    /// from the axis), and whether the screw surface's natural normal points out of it; null for other screw faces.
    /// </summary>
    static (Vec3 origin, Vec3 axis, Vec3 refDir, double r, bool outward)? ScrewCylinder(ExactFace f)
    {
        if (f.Surface.Kind != SurfaceKind.Screw || f.Surface.Def.Screw is not { Gen: Native.NLine ln } sc) return null;
        var m = sc.Motion;
        if (Vec3.Cross(ln.D.Normalized, m.A).Length > 1e-12) return null;
        var d = ln.P - m.O;
        var r = (d - m.A * Vec3.Dot(d, m.A)).Length;
        if (r < 1e-9) return null;
        double u = 0.5 * (sc.U0 + sc.U1), v = 0.5 * (sc.V0 + sc.V1);
        var p = sc.Eval(u, v); var q = p - m.O;
        var radial = q - m.A * Vec3.Dot(q, m.A);
        return (m.O, m.A, m.X, r, Vec3.Dot(sc.NormalAt(u, v), radial) > 0);
    }

    static BSplineSurface ScrewFit(ExactFace f, ExactBrep b)
    {
        var sc = f.Surface.Def.Screw!;
        double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
        double hu = double.NaN, hv = double.NaN;
        foreach (var p in BoundaryPoints(f, b))
        {
            var q = sc.Closest(p, hu, hv);
            hu = q.u; hv = q.v;
            u0 = Math.Min(u0, q.u); u1 = Math.Max(u1, q.u); v0 = Math.Min(v0, q.v); v1 = Math.Max(v1, q.v);
        }
        var du = (u1 - u0) * 0.01 + 1e-4; var dv = (v1 - v0) * 0.01 + 1e-4;
        u0 -= du; u1 += du; v0 -= dv; v1 += dv;
        // about 16 spans a turn around the axis; along a line generator a cubic is exact
        const double fitTol = 1e-5;
        // quintic: about 6 spans a turn reach the tolerance (cubics need many more); long threads may need hundreds
        int nu = Math.Max(5, (int)Math.Ceiling((u1 - u0) / (Math.PI / 3)));
        int nv = sc.Gen is Native.NLine ? 5 : Math.Max(5, (int)Math.Ceiling((v1 - v0) / (Math.PI / 3)));
        return BSplineSurface.Fit(sc.Eval, u0, u1, v0, v1, fitTol, nu, nv, degree: 5, maxU: 1024, maxV: 256);
    }

    static BSplineSurface PipeFit(ExactFace f, ExactBrep b)
    {
        var d = f.Surface.Def;
        var sp = d.Spine!; var rad = d.Radius;
        // the pipe's own parameterization is only C1 along the spine (Hermite nodes) and so fits slowly; fit the same
        // surface through a C2 spline of the spine nodes (same points within ~1e-8) with a smooth frame instead
        var range = BoundaryPoints(f, b).Select(p => sp.Ball(p, rad).s).ToList();
        double s0 = range.Min(), s1 = range.Max(), pad = (s1 - s0) * 0.05 + 1e-3;
        var nodes = Enumerable.Range(0, sp.P.Length).Where(i => sp.S[i] >= s0 - pad && sp.S[i] <= s1 + pad).ToList();
        if (nodes.Count < 4)
        {
            // too few nodes in range: sample the Hermite spine instead
            nodes.Clear();
        }
        double sa = nodes.Count > 0 ? sp.S[nodes[0]] : Math.Max(sp.S0, s0 - pad), sb = nodes.Count > 0 ? sp.S[nodes[^1]] : Math.Min(sp.S1, s1 + pad);
        var ss = nodes.Count > 0 ? nodes.Select(i => sp.S[i]).ToList() : Enumerable.Range(0, 9).Select(k => sa + (sb - sa) * k / 8).ToList();
        // (more points where the C2 curve strays from the spine: a composite fillet's spine is only G1 where the ball
        // switches faces, its curvature jumping, and a C2 curve through nodes a few hundredths apart misses it there by
        // ~1e-3, the surface written then off its own edges)
        BSplineCurve c2;
        for (int round = 0; ; round++)
        {
            // (its ends along the spine's: a face ending square across the pipe, as a fillet's end or where its tip
            // takes over, is the section square to the spine there, and a tilted end is off it by the radius times the
            // tilt)
            c2 = BSplineCurve.InterpolateWithEnds(ss.Select(sp.Eval).ToList(), sp.Deriv(ss[0]), sp.Deriv(ss[^1]));
            if (round == 12) break;
            var add = new List<double>();
            double t = 0;
            for (int i = 0; i + 1 < ss.Count; i++)
            {
                var sm = 0.5 * (ss[i] + ss[i + 1]); var q = sp.Eval(sm);
                t = c2.ClosestParam(q, 0, 1, Math.Clamp((sm - sa) / (sb - sa), 0, 1));
                if (Vec3.Distance(c2.Eval(t), q) > 1e-7 * Math.Max(1, rad)) add.Add(sm);
            }
            if (add.Count == 0) break;
            ss = ss.Concat(add).OrderBy(x => x).ToList();
        }
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
        // (a variable radius: its section at s, set back along the spine and smaller)
        Vec3 Surface(double th, double s)
        {
            var (n1, n2) = Frame(s);
            var (back, q) = sp.Section(s, rad);
            return C(s) - Tan(s) * back + (n1 * Math.Cos(th) + n2 * Math.Sin(th)) * q;
        }
        double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue, hint = double.NaN;
        var samples = new List<(double s, double th)>();
        foreach (var p in BoundaryPoints(f, b))
        {
            var s = sp.Ball(p, rad).s;
            var (n1, n2) = Frame(s); var r = p - C(s);
            // (a fading pipe's tip, its radius come to nothing: no angle there, its place along the spine only)
            if (sp.Variable && r.Length < 0.02 * rad) { v0 = Math.Min(v0, s); v1 = Math.Max(v1, s); continue; }
            var th = Math.Atan2(Vec3.Dot(r, n2), Vec3.Dot(r, n1));
            if (!double.IsNaN(hint)) th = Native.NCurve.Wrap(th, hint);
            hint = th;
            samples.Add((s, th));
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
        int nv0 = Math.Clamp((int)Math.Ceiling((v1 - v0) / dv0 / 3), 5, 192);
        // (sites along the spine uniform at first, then refined where the fit is off: dense where the spine is only G1
        // (its curvature jumping where a composite fillet's ball switches faces); a uniform grid fine enough there
        // would be fine everywhere)
        var sites = Enumerable.Range(0, nv0 + 1).Select(k => v0 + (v1 - v0) * k / nv0).ToList();
        // quintic, from a third of the cubic's spans (its error falls as h⁶, not h⁴): far fewer control points.
        // (Over the band the face takes up around the pipe at each place along it, between its contact edges, rather
        // than the angles it takes up anywhere along it: a long fillet turning round, as a rim rolling up a cut's side,
        // takes up a different band at each end, and all of them at once take in the pipe's inner side where its spine
        // bends tighter than its radius, where it folds over itself)
        if (Band(samples, v0, v1, rad) is { } band)
        {
            Vec3 OnBand(double u, double s) { var (l, h) = band(s); return Surface(l + (h - l) * u, s); }
            var width = Enumerable.Range(0, 65).Max(k => { var (l, h) = band(v0 + (v1 - v0) * k / 64); return h - l; });
            return BSplineSurface.FitAdaptiveV(OnBand, 0, 1, v0, v1, fitTol, Math.Max(5, (int)Math.Ceiling(width / du0 / 3)), sites, degree: 5);
        }
        return BSplineSurface.FitAdaptiveV(Surface, u0, u1, v0, v1, fitTol, Math.Max(5, (int)Math.Ceiling((u1 - u0) / du0 / 3)), sites, degree: 5);
    }

    /// <summary>
    /// A pipe face's band of angles at each place along its spine, from its boundary's (s, angle) samples: the least
    /// and greatest angle near each place (a window of a quarter of the radius each way along the spine), a margin
    /// out, as smooth functions of s. Null where the band goes all the way round (a band that wraps).
    /// </summary>
    static Func<double, (double lo, double hi)>? Band(List<(double s, double th)> samples, double v0, double v1, double rad)
    {
        if (samples.Count < 4 || v1 <= v0) return null;
        int nb = Math.Clamp((int)Math.Ceiling((v1 - v0) / (rad * 0.25)), 4, 256);
        var h = (v1 - v0) / nb;
        var sorted = samples.OrderBy(x => x.s).ToList();
        var keys = sorted.Select(x => x.s).ToList();
        var pts = new List<Vec3>(); var uk = new List<double>();
        for (int i = 0; i <= nb; i++)
        {
            var s = v0 + h * i;
            // (both contacts within the window: a fillet's are sampled at their own places along it)
            double w = Math.Max(1.5 * h, 1e-9), lo = double.MaxValue, hi = double.MinValue;
            int at = keys.BinarySearch(s - w); if (at < 0) at = ~at;
            for (int k = at; k < sorted.Count && sorted[k].s <= s + w; k++) { lo = Math.Min(lo, sorted[k].th); hi = Math.Max(hi, sorted[k].th); }
            if (lo > hi) continue;
            pts.Add(new Vec3(lo, hi, 0)); uk.Add((s - v0) / (v1 - v0));
        }
        if (pts.Count < 2) return null;
        if (pts.Any(p => p.Y - p.X > 2 * Math.PI - 0.2)) return null;
        // (the parameters from 0 to 1: the windows at the ends hold the end values)
        var u = uk.ToArray(); var ua = u[0]; var ub = u[^1];
        if (ub - ua < 1e-12) return null;
        for (int i = 0; i < u.Length; i++) u[i] = (u[i] - ua) / (ub - ua);
        var curve = BSplineCurve.Interpolate(pts, u, Math.Min(3, pts.Count - 1));
        var widest = pts.Max(p => p.Y - p.X);
        (double lo, double hi) Raw(double s)
        {
            var t = Math.Clamp(((s - v0) / (v1 - v0) - ua) / (ub - ua), 0, 1);
            var q = curve.Eval(t);
            return (q.X, Math.Max(q.Y, q.X));
        }
        // (every boundary sample inside the band: where the face's edge swings faster than the windows follow (a
        // corner's patch, its contact turning round the corner), the margin widened by as much as one falls outside)
        double outside = 0;
        foreach (var (s, th) in samples)
        {
            var (lo, hi) = Raw(s);
            outside = Math.Max(outside, Math.Max(lo - th, th - hi));
        }
        var margin = 0.02 * widest + 0.02 + 1.25 * outside;
        if (widest + 2 * margin > 2 * Math.PI - 0.1) return null;
        return s => { var (lo, hi) = Raw(s); return (lo - margin, hi + margin); };
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
