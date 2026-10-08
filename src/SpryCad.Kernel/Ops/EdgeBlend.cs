namespace SpryCad.Kernel;

/// <summary>
/// Chamfer legs: <see cref="D1"/> along the reference face and <see cref="D2"/> along the other one, or, with an
/// <see cref="Angle"/> (radians, &gt; 0), D1 along the reference face and the chamfer face at that angle to it.
/// <see cref="Flip"/> makes the edge's other face the reference.
/// </summary>
public sealed record ChamferSize(double D1, double D2, double Angle = 0, bool Flip = false)
{
    public static ChamferSize Equal(double d) => new(d, d);

    /// <summary>(along the reference face, along the other face) where the faces meet at interior angle theta.</summary>
    public (double dRef, double dOther) Legs(double theta)
    {
        if (Angle <= 0) return (D1, D2);
        // triangle edge / reference leg end / other leg end: angle theta at the edge, Angle at the reference leg end
        if (theta + Angle >= Math.PI - 1e-6)
            throw new InvalidOperationException("the chamfer angle is too large for this edge: the chamfer would not reach the other face.");
        return (D1, D1 * Math.Sin(Angle) / Math.Sin(theta + Angle));
    }
}

/// <summary>
/// Chamfers and fillets on model edges. A tool solid is built per edge from the exact
/// geometry of the two adjacent faces and then subtracted (convex edges) or added (concave edges).
/// Supported: straight edges between planar faces, and circular edges between a plane and a
/// perpendicular cylinder (hole and boss rims).
/// </summary>
public static class EdgeBlend
{
    public static Solid Apply(Solid body, IEnumerable<TopoEdge> edges, double size, bool fillet, string prefix, List<string> errors, ChamferSize? chamfer = null)
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
                var (tool, convex) = BuildTool(body, e, size, fillet, name, chamfer ?? ChamferSize.Equal(size));
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

    static (Solid? tool, bool convex) BuildTool(Solid body, TopoEdge e, double size, bool fillet, string name, ChamferSize ch)
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
            var (dA, dB) = fillet ? (size, size) : ChamferLegs(P(uA), P(uB), ch, !ch.Flip);
            var loop = Profile2D(P(uA), P(uB), P(nA), P(nB), Vec2.Zero, dA, fillet, convex, dB);
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
        var (dP, dC) = fillet ? (size, size) : ChamferLegs(uP2, uC2, ch, !ch.Flip);
        var prof = Profile2D(uP2, uC2, nP2, nC2, E2, dP, fillet, convex, dC);
        if (prof == null) return (null, convex);
        if (prof.Verts.Any(v => v.P.X < -1e-9)) throw new InvalidOperationException("size too large for this edge");
        var refDir = cs.RefDir.LengthSq > 0.5 ? cs.RefDir : ax.AnyPerpendicular();
        refDir = (refDir - ax * Vec3.Dot(refDir, ax)).Normalized;
        int segs = cs.Segments > 0 ? cs.Segments : SurfaceDef.SegmentsFor(cs.Radius);
        if (e.Closed)
        {
            var frame = new Frame(cs.Origin, refDir, ax, Vec3.Cross(refDir, ax));
            return (Builders.Revolve(new[] { new ProfileRegion(prof) }, frame, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, name, segs), convex);
        }

        // partial arc (e.g. the rounded corner of a filleted box): sweep only over the arc, with
        // rotation steps on the cylinder's own facet angles so the tool lines up exactly
        var yDir = Vec3.Cross(ax, refDir);
        double Ang(Vec3 p) { var r = p - cs.Origin; return Math.Atan2(Vec3.Dot(r, yDir), Vec3.Dot(r, refDir)); }
        double Wrap(double a) { a %= 2 * Math.PI; return a < 0 ? a + 2 * Math.PI : a; }
        var p0 = e.Start; var p1 = e.End;
        var d01 = Ang(s.B) - Ang(s.A);
        while (d01 > Math.PI) d01 -= 2 * Math.PI; while (d01 < -Math.PI) d01 += 2 * Math.PI;
        if (d01 < 0) (p0, p1) = (p1, p0);
        var a0 = Ang(p0);
        var sweep = Wrap(Ang(p1) - a0);
        if (sweep < 1e-6) return (null, convex);
        var step = 2 * Math.PI / segs;
        var angles = new List<double> { 0 };
        for (int k = 0; k < segs; k++)
        {
            var ra = Wrap(k * step - a0);
            if (ra > step * 0.05 && ra < sweep - step * 0.05) angles.Add(ra);
        }
        angles.Sort();
        angles.Add(sweep);
        var startDir = refDir * Math.Cos(a0) + yDir * Math.Sin(a0);
        var pframe = new Frame(cs.Origin, startDir, ax, Vec3.Cross(startDir, ax));
        var partial = Builders.Revolve(new[] { new ProfileRegion(prof) }, pframe, Vec2.Zero, new Vec2(0, 1), sweep, name, segs, angles);
        if (partial.Volume() < 0) partial = partial.Flipped();
        return (partial, convex);
    }

    /// <summary>Chamfer leg lengths (along face A, along face B) for faces running along uA/uB from the edge.</summary>
    internal static (double dA, double dB) ChamferLegs(Vec2 uA, Vec2 uB, ChamferSize c, bool refIsA)
    {
        var theta = Math.Acos(Math.Clamp(Vec2.Dot(uA.Normalized, uB.Normalized), -1, 1));
        var (r, o) = c.Legs(theta);
        return refIsA ? (r, o) : (o, r);
    }

    /// <summary>
    /// Cross-section of the blend tool around corner point E: uA/uB run along the two faces
    /// (into the faces), nA/nB are the outward face normals. A chamfer runs from <paramref name="size"/> along
    /// face A to <paramref name="sizeB"/> (default: the same) along face B.
    /// </summary>
    internal static ProfileLoop? Profile2D(Vec2 uA, Vec2 uB, Vec2 nA, Vec2 nB, Vec2 E, double size, bool fillet, bool convex, double sizeB = double.NaN)
    {
        uA = uA.Normalized; uB = uB.Normalized;
        var cosT = Math.Clamp(Vec2.Dot(uA, uB), -1, 1);
        var theta = Math.Acos(cosT);
        if (theta < 1e-3 || theta > Math.PI - 1e-3) return null;
        if (double.IsNaN(sizeB)) sizeB = size;
        // the tool reaches slightly past the corner (outside for cuts, inside for additions); its sides from the
        // leg ends to there stay off the faces, so only the chamfer triangle acts on the body
        var outDir = (nA + nB).Normalized;
        if (outDir.LengthSq < 0.5) return null;
        var X = E + outDir * (convex ? 1 : -1) * Math.Max(size, sizeB) * 0.5;
        var loop = new ProfileLoop();
        if (!fillet)
        {
            var TA = E + uA * size; var TB = E + uB * sizeB;
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
