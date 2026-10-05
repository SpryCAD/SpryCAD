namespace FeatherCad.Kernel;

/// <summary>
/// Chamfers and fillets on model edges. A tool solid is built per edge from the exact
/// geometry of the two adjacent faces and then subtracted (convex edges) or added (concave edges).
/// Supported: straight edges between planar faces, and circular edges between a plane and a
/// perpendicular cylinder (hole and boss rims).
/// </summary>
public static class EdgeBlend
{
    public static Solid Apply(Solid body, IEnumerable<TopoEdge> edges, double size, bool fillet, string prefix, List<string> errors)
    {
        if (size <= 0) { errors.Add("Size must be greater than zero."); return body; }
        var cutTools = new List<Solid>();
        var addTools = new List<Solid>();
        int i = 0;
        foreach (var e in edges)
        {
            var name = $"{prefix}.{i++}";
            try
            {
                var (tool, convex) = BuildTool(body, e, size, fillet, name);
                if (tool == null) { errors.Add($"Edge {e.FaceA} / {e.FaceB}: unsupported edge geometry."); continue; }
                (convex ? cutTools : addTools).Add(tool);
            }
            catch (Exception ex) { errors.Add($"Edge {e.FaceA} / {e.FaceB}: {ex.Message}"); }
        }
        var result = body;
        if (cutTools.Count > 0) result = Csg.Subtract(result, Csg.UnionAll(cutTools));
        if (addTools.Count > 0) result = Csg.Union(result, Csg.UnionAll(addTools));
        return result;
    }

    static (Solid? tool, bool convex) BuildTool(Solid body, TopoEdge e, double size, bool fillet, string name)
    {
        var s = e.Segs[0];
        var fa = body.Faces[s.FaceA]; var fb = body.Faces[s.FaceB];
        var nA = fa.Plane.Normal; var nB = fb.Plane.Normal;
        var uA = Vec3.Cross(nA, s.B - s.A).Normalized;
        var uB = Vec3.Cross(nB, s.A - s.B).Normalized;
        bool convex = Vec3.Dot(uB, nA) < 0;

        var sa = fa.Tag.Surface; var sb = fb.Tag.Surface;
        if (!sa.IsCurved && !sb.IsCurved && e.IsLinear)
        {
            var E = e.Start; var t = (e.End - e.Start); var L = t.Length; t /= L;
            var x = uA; var y = Vec3.Cross(t, x).Normalized;
            var lframe = new Frame(E, x, y, t);
            Vec2 P(Vec3 v) => new(Vec3.Dot(v, x), Vec3.Dot(v, y));
            var loop = Profile2D(P(uA), P(uB), P(nA), P(nB), Vec2.Zero, size, fillet, convex);
            if (loop == null) return (null, convex);
            return (Builders.Extrude(new[] { new ProfileRegion(loop) }, lframe, 0, L, name), convex);
        }

        // plane + cylinder rim
        Face plane, cyl; Vec3 uP, uC, nP, nC;
        if (!sa.IsCurved && sb.Kind == SurfaceKind.Cylinder) { plane = fa; cyl = fb; uP = uA; uC = uB; nP = nA; nC = nB; }
        else if (!sb.IsCurved && sa.Kind == SurfaceKind.Cylinder) { plane = fb; cyl = fa; uP = uB; uC = uA; nP = nB; nC = nA; }
        else return (null, convex);
        var cs = cyl.Tag.Surface;
        var ax = cs.Axis.Normalized;
        if (Math.Abs(Vec3.Dot(ax, nP)) < 0.999) return (null, convex);
        convex = Vec3.Dot(uC, nP) < 0;

        var M = (s.A + s.B) * 0.5;
        var rel = M - cs.Origin;
        var h0 = Vec3.Dot(rel, ax);
        var rho = (rel - ax * h0).Normalized;
        // cross-section directions; clean them up to exact radial/axial values
        var uP2 = new Vec2(Math.Sign(Vec3.Dot(uP, rho)), 0);
        var uC2 = new Vec2(0, Math.Sign(Vec3.Dot(uC, ax)));
        var nP2 = new Vec2(0, Math.Sign(Vec3.Dot(nP, ax)));
        var nC2 = new Vec2(Math.Sign(Vec3.Dot(nC, rho)), 0);
        var E2 = new Vec2(cs.Radius, h0);
        var prof = Profile2D(uP2, uC2, nP2, nC2, E2, size, fillet, convex);
        if (prof == null) return (null, convex);
        if (prof.Verts.Any(v => v.P.X < -1e-9)) throw new InvalidOperationException("size too large for this edge");
        var refDir = cs.RefDir.LengthSq > 0.5 ? cs.RefDir : ax.AnyPerpendicular();
        var frame = new Frame(cs.Origin, refDir, ax, Vec3.Cross(refDir, ax));
        int segs = cs.Segments > 0 ? cs.Segments : SurfaceDef.SegmentsFor(cs.Radius);
        var tool = Builders.Revolve(new[] { new ProfileRegion(prof) }, frame, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, name, segs);
        return (tool, convex);
    }

    /// <summary>
    /// Cross-section of the blend tool around corner point E: uA/uB run along the two faces
    /// (into the faces), nA/nB are the outward face normals.
    /// </summary>
    static ProfileLoop? Profile2D(Vec2 uA, Vec2 uB, Vec2 nA, Vec2 nB, Vec2 E, double size, bool fillet, bool convex)
    {
        uA = uA.Normalized; uB = uB.Normalized;
        var cosT = Math.Clamp(Vec2.Dot(uA, uB), -1, 1);
        var theta = Math.Acos(cosT);
        if (theta < 1e-3 || theta > Math.PI - 1e-3) return null;
        // the tool reaches slightly past the corner (outside for cuts, inside for additions)
        var outDir = (nA + nB).Normalized;
        if (outDir.LengthSq < 0.5) return null;
        var X = E + outDir * (convex ? 1 : -1) * size * 0.5;
        var loop = new ProfileLoop();
        if (!fillet)
        {
            var TA = E + uA * size; var TB = E + uB * size;
            loop.Verts.Add(new ProfileVertex(TA, "a"));
            loop.Verts.Add(new ProfileVertex(X, "b"));
            loop.Verts.Add(new ProfileVertex(TB, "F"));
        }
        else
        {
            var r = size;
            var tl = r / Math.Tan(theta / 2);
            var TA = E + uA * tl; var TB = E + uB * tl;
            var bis = (uA + uB).Normalized;
            var C = E + bis * (r / Math.Sin(theta / 2));
            var aB = (TB - C).Angle; var aA = (TA - C).Angle;
            // choose the arc that passes on the corner side (direction -bis from C)
            var d = aA - aB; while (d <= 0) d += 2 * Math.PI;
            var midCcw = aB + d / 2;
            bool ccw = Vec2.Dot(Vec2.FromAngle(midCcw), bis) < 0;
            loop.Verts.Add(new ProfileVertex(TA, "a"));
            loop.Verts.Add(new ProfileVertex(X, "b"));
            loop.AddArc(C, r, aB, aA, ccw, "F");
        }
        if (loop.SignedArea() < 0) loop = loop.Reversed();
        return loop;
    }
}
