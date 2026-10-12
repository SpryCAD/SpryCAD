using SpryCad.Core.Model;
using SpryCad.Kernel;

namespace SpryCad.Core.Regen;

public sealed class FeatureException(string msg) : Exception(msg);

/// <summary>Resolves persistent references (faces, edges, planes, directions) against a model state.</summary>
public static class Resolver
{
    public static Frame StdPlane(string name) => name switch
    {
        "Front" => Frame.Front,
        "Top" => Frame.Top,
        "Right" => Frame.Right,
        _ => throw new FeatureException($"Unknown plane '{name}'"),
    };

    public static Frame ResolvePlane(PlaneRef r, ModelState st)
    {
        if (r.Std != null) return StdPlane(r.Std);
        if (r.Feature != null)
        {
            if (st.Planes.TryGetValue(r.Feature, out var f)) return f;
            throw new FeatureException("Referenced plane is missing.");
        }
        if (r.Face != null)
        {
            if (NativePlane(r.Face, st) is { } np) return Frame.FromPlane(np);
            var (_, face) = ResolveFace(r.Face, st);
            if (face.Tag.Surface.IsCurved) throw new FeatureException("Selected face is not planar.");
            return Frame.FromPlane(face.Plane);
        }
        throw new FeatureException("No plane selected.");
    }

    /// <summary>
    /// Outward plane of a named planar face, looked up on the native B-rep (no tessellation). Null when the
    /// bodies are not native or no planar face of that name exists (the facet lookup then decides).
    /// </summary>
    static Plane? NativePlane(FaceRef r, ModelState st)
    {
        if (NativeFace(r, st) is not { } hit || hit.face.Surface.Kind != SurfaceKind.Plane) return null;
        return hit.face.Sense ? hit.face.Surface.Plane! : hit.face.Surface.Plane!.Flipped();
    }

    /// <summary>
    /// Native face by name nearest the hint, else a planar face with the reference normal through the hint.
    /// Null when the bodies are not native or nothing matches.
    /// </summary>
    internal static (Body body, Kernel.Native.NFace face)? NativeFace(FaceRef r, ModelState st)
    {
        var bodies = OrderBodies(st, r.Body).ToList();
        if (bodies.Count == 0 || bodies.Any(b => b.Solid.Native == null)) return null;
        foreach (var byName in new[] { true, false })
        {
            if (!byName && r.Normal.LengthSq < 0.5) break;
            Body? bb = null; Kernel.Native.NFace? best = null; double bestD = double.MaxValue;
            foreach (var b in bodies)
                foreach (var f in b.Solid.Native!.Faces)
                {
                    if (byName ? f.Tag.Name != r.Name : f.Surface.Kind != SurfaceKind.Plane) continue;
                    var box = f.Bounds();
                    double d;
                    if (f.Surface.Kind == SurfaceKind.Plane)
                    {
                        var pl = f.Sense ? f.Surface.Plane! : f.Surface.Plane!.Flipped();
                        if (!byName && (Vec3.Dot(pl.Normal, r.Normal) < 0.9999 || Math.Abs(pl.Distance(r.Hint)) > 1e-3)) continue;
                        var proj = pl.Project(r.Hint);
                        d = Math.Abs(pl.Distance(r.Hint)) + Vec3.Max(Vec3.Max(box.Min - proj, proj - box.Max), Vec3.Zero).Length;
                    }
                    else d = Math.Abs(f.Surface.F_(r.Hint)) + Vec3.Max(Vec3.Max(box.Min - r.Hint, r.Hint - box.Max), Vec3.Zero).Length;
                    if (d < bestD) { bestD = d; best = f; bb = b; }
                }
            if (best != null) return (bb!, best);
        }
        return null;
    }

    public static (Body body, Face face) ResolveFace(FaceRef r, ModelState st)
    {
        Body? bestBody = null; Face? best = null; double bestD = double.MaxValue;
        foreach (var b in OrderBodies(st, r.Body))
            foreach (var f in b.Solid.Faces)
            {
                if (f.Tag.Name != r.Name) continue;
                var d = DistanceToFace(f, r.Hint);
                if (d < bestD) { bestD = d; best = f; bestBody = b; }
            }
        if (best != null) return (bestBody!, best);
        // geometric fallback: a planar face with the same normal containing the hint point
        if (r.Normal.LengthSq > 0.5)
        {
            foreach (var b in OrderBodies(st, r.Body))
                foreach (var f in b.Solid.Faces)
                {
                    if (Vec3.Dot(f.Plane.Normal, r.Normal) < 0.9999) continue;
                    if (Math.Abs(f.Plane.Distance(r.Hint)) > 1e-3) continue;
                    var d = DistanceToFace(f, r.Hint);
                    if (d < bestD) { bestD = d; best = f; bestBody = b; }
                }
            if (best != null) return (bestBody!, best);
        }
        throw new FeatureException($"Face '{r.Name}' could not be found.");
    }

    /// <summary>All faces (facets) sharing the referenced face's name in its body.</summary>
    public static (Body body, string name) ResolveFaceName(FaceRef r, ModelState st)
    {
        if (NativeFace(r, st) is { } hit) return (hit.body, hit.face.Tag.Name);
        var (b, f) = ResolveFace(r, st);
        return (b, f.Tag.Name);
    }

    static IEnumerable<Body> OrderBodies(ModelState st, string? preferred)
    {
        if (preferred != null)
        {
            var p = st.FindBody(preferred);
            if (p != null) yield return p;
        }
        foreach (var b in st.Bodies) if (b.Id != preferred) yield return b;
    }

    static double DistanceToFace(Face f, Vec3 p)
    {
        var d = Math.Abs(f.Plane.Distance(p));
        var proj = f.Plane.Project(p);
        var bb = f.Bounds();
        var outside = Vec3.Max(Vec3.Max(bb.Min - proj, proj - bb.Max), Vec3.Zero).Length;
        return d + outside;
    }

    public static (Body body, TopoEdge edge) ResolveEdge(EdgeRef r, ModelState st)
    {
        Body? bb = null; TopoEdge? best = null; double bestD = double.MaxValue;
        foreach (var b in OrderBodies(st, r.Body))
            foreach (var e in b.Solid.Topology.EdgesBetween(r.A, r.B))
            {
                var d = DistanceToEdge(e, r.Hint);
                if (d < bestD) { bestD = d; best = e; bb = b; }
            }
        if (best != null) return (bb!, best);
        // fallback: any edge passing through the hint point
        foreach (var b in OrderBodies(st, r.Body))
            foreach (var e in b.Solid.Topology.Edges)
            {
                var d = DistanceToEdge(e, r.Hint);
                if (d < 1e-3 && d < bestD) { bestD = d; best = e; bb = b; }
            }
        if (best != null) return (bb!, best);
        throw new FeatureException($"Edge between '{r.A}' and '{r.B}' could not be found.");
    }

    /// <summary>
    /// Edge between faces named A and B nearest the hint, on the native B-rep (no tessellation). Null when the
    /// body is not native or nothing matches (the facet lookup then decides).
    /// </summary>
    public static (Body body, Kernel.Native.NEdge edge)? ResolveNativeEdge(EdgeRef r, ModelState st)
    {
        var bodies = OrderBodies(st, r.Body).ToList();
        if (bodies.Any(b => b.Solid.Native == null)) return null;
        // 1) an edge between the named faces, nearest the hint; 2) any edge through the hint point
        foreach (var named in new[] { true, false })
        {
            Body? bb = null; Kernel.Native.NEdge? best = null; double bestD = named ? double.MaxValue : 1e-3;
            foreach (var b in bodies)
            {
                var nb = b.Solid.Native!;
                nb.Link();
                foreach (var e in nb.Edges)
                {
                    if (e.Uses.Count != 2) continue;
                    if (named)
                    {
                        var fa = e.Uses[0].Face.Tag.Name; var fb = e.Uses[1].Face.Tag.Name;
                        if (!((fa == r.A && fb == r.B) || (fa == r.B && fb == r.A))) continue;
                    }
                    var d = DistanceToPolyline(e.Samples(1e-3), r.Hint);
                    if (d < bestD) { bestD = d; bb = b; best = e; }
                }
            }
            if (best != null) return (bb!, best);
        }
        // 3) the face named B runs on tangentially into another (a round between it and the next face, a draft moving
        // the edge onto that round): the edge between face A and that face, nearest the hint
        {
            Body? bb = null; Kernel.Native.NEdge? best = null; double bestD = double.MaxValue;
            foreach (var b in bodies)
                foreach (var e in b.Solid.Native!.Edges)
                {
                    if (e.Uses.Count != 2) continue;
                    var fa = e.Uses[0].Face; var fb = e.Uses[1].Face;
                    if (!(fa.Tag.Name == r.A && TangentTo(fb, r.B) || fb.Tag.Name == r.A && TangentTo(fa, r.B)
                          || fa.Tag.Name == r.B && TangentTo(fb, r.A) || fb.Tag.Name == r.B && TangentTo(fa, r.A))) continue;
                    var d = DistanceToPolyline(e.Samples(1e-3), r.Hint);
                    if (d < bestD) { bestD = d; bb = b; best = e; }
                }
            if (best != null) return (bb!, best);
        }
        // 4) one of the faces is gone, merged into a neighbour in its plane (a side made flush with the body's): the
        // other's edge nearest the hint, near against its length
        {
            var names = new HashSet<string>(bodies.SelectMany(b => b.Solid.Native!.Faces.Select(f => f.Tag.Name)));
            var kept = names.Contains(r.A) && !names.Contains(r.B) ? r.A : names.Contains(r.B) && !names.Contains(r.A) ? r.B : null;
            if (kept == null) return null;
            Body? bb = null; Kernel.Native.NEdge? best = null; double bestD = double.MaxValue, bestL = 0;
            foreach (var b in bodies)
                foreach (var e in b.Solid.Native!.Edges)
                {
                    if (e.Uses.Count != 2 || !e.Uses.Any(u => u.Face.Tag.Name == kept)) continue;
                    var pts = e.Samples(1e-3);
                    var d = DistanceToPolyline(pts, r.Hint);
                    if (d < bestD) { bestD = d; bb = b; best = e; bestL = pts.Zip(pts.Skip(1), Vec3.Distance).Sum(); }
                }
            if (best != null && bestD < 0.1 * bestL) return (bb!, best);
        }
        return null;
    }

    /// <summary>Whether a face meets a face of the given name along an edge, tangentially (the two run on smoothly).</summary>
    static bool TangentTo(Kernel.Native.NFace f, string name)
    {
        if (f.Tag.Name == name) return false;
        foreach (var c in f.Coedges)
        {
            var e = c.Edge;
            if (e.Uses.Count != 2) continue;
            var other = ReferenceEquals(e.Uses[0].Face, f) ? e.Uses[1].Face : e.Uses[0].Face;
            if (other.Tag.Name != name) continue;
            var m = e.Curve.Eval(0.5 * (e.T0 + e.T1));
            var n1 = f.Surface.Normal(m) * (f.Sense ? 1 : -1); var n2 = other.Surface.Normal(m) * (other.Sense ? 1 : -1);
            if (Vec3.Dot(n1, n2) > 1 - 1e-6) return true;
        }
        return false;
    }

    static double DistanceToPolyline(List<Vec3> pts, Vec3 p)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < pts.Count; i++) best = Math.Min(best, DistanceToSegment(p, pts[i], pts[i + 1]));
        return pts.Count == 1 ? Vec3.Distance(pts[0], p) : best;
    }

    public static double DistanceToEdge(TopoEdge e, Vec3 p)
    {
        double best = double.MaxValue;
        foreach (var s in e.Segs) best = Math.Min(best, DistanceToSegment(p, s.A, s.B));
        return best;
    }

    public static double DistanceToSegment(Vec3 p, Vec3 a, Vec3 b)
    {
        var d = b - a; var l2 = d.LengthSq;
        var t = l2 > 0 ? Math.Clamp(Vec3.Dot(p - a, d) / l2, 0, 1) : 0;
        return (a + d * t - p).Length;
    }

    /// <summary>Position of a model vertex or sketch point.</summary>
    public static Vec3 ResolvePoint(PointRef r, ModelState st)
    {
        if (r.Sketch != null)
        {
            if (!st.Sketches.TryGetValue(r.Sketch, out var sk)) throw new FeatureException("The sketch of the reference point is missing.");
            if (sk.Sketch.Point(r.SketchPoint) is not { } p) throw new FeatureException("The reference sketch point no longer exists.");
            return sk.Frame.ToWorld(p.P);
        }
        if (r.Vertex is { } v)
        {
            // the end of the referenced edge nearest the remembered position
            if (ResolveNativeEdge(v, st) is { } ne) return NearestEnd(ne.edge.V0.P, ne.edge.V1.P, v.Hint);
            var (_, te) = ResolveEdge(v, st);
            return NearestEnd(te.Start, te.End, v.Hint);
        }
        if (r.Center is { } ce)
        {
            // the circle's center, wherever the edge went
            if (ResolveNativeEdge(ce, st) is { edge.Curve: Kernel.Native.NCircle c }) return c.C;
            throw new FeatureException("The edge of the reference center is gone, or no longer round.");
        }
        throw new FeatureException("No reference point selected.");

        static Vec3 NearestEnd(Vec3 a, Vec3 b, Vec3 hint) => Vec3.Distance(a, hint) <= Vec3.Distance(b, hint) ? a : b;
    }

    /// <summary>
    /// Point on the curve nearest <paramref name="near"/> and the unit tangent there (oriented from the curve's
    /// start to its end).
    /// </summary>
    public static (Vec3 point, Vec3 tangent) CurveTangent(CurveRef r, Vec3 near, ModelState st)
    {
        if (r.Sketch != null)
        {
            if (!st.Sketches.TryGetValue(r.Sketch, out var sr)) throw new FeatureException("The sketch of the reference curve is missing.");
            var sk = sr.Sketch; var fr = sr.Frame;
            var q = fr.ToLocal(near);
            switch (sk.Entity(r.SketchEntity))
            {
                case Sketching.SkLine l:
                    {
                        var a = sk.P(l.P1); var b = sk.P(l.P2); var d = b - a;
                        if (d.Length < 1e-12) break;
                        var t = Math.Clamp(Vec2.Dot(q - a, d) / d.LengthSq, 0, 1);
                        return (fr.ToWorld(a + d * t), (fr.ToWorld(b) - fr.ToWorld(a)).Normalized);
                    }
                case Sketching.SkArc arc:
                    {
                        var c = sk.P(arc.Center); var rad = (sk.P(arc.Start) - c).Length;
                        var dir = q - c; if (dir.Length < 1e-12) dir = sk.P(arc.Start) - c;
                        var p = c + dir.Normalized * rad;
                        var tan = new Vec2(-(p - c).Y, (p - c).X); // counter-clockwise, start to end
                        return (fr.ToWorld(p), (fr.ToWorld(p + tan) - fr.ToWorld(p)).Normalized);
                    }
                case Sketching.SkCircle ci:
                    {
                        var c = sk.P(ci.Center);
                        var dir = q - c; if (dir.Length < 1e-12) dir = new Vec2(1, 0);
                        var p = c + dir.Normalized * ci.R;
                        var tan = new Vec2(-(p - c).Y, (p - c).X);
                        return (fr.ToWorld(p), (fr.ToWorld(p + tan) - fr.ToWorld(p)).Normalized);
                    }
            }
            throw new FeatureException("The reference curve must be a sketch line, arc or circle.");
        }
        if (r.Edge is { } er)
        {
            if (ResolveNativeEdge(er, st) is { } ne)
            {
                var e = ne.edge;
                double lo = Math.Min(e.T0, e.T1), hi = Math.Max(e.T0, e.T1);
                var t = Math.Clamp(e.Curve.Param(near, e.Curve.Periodic ? 0.5 * (lo + hi) : null), lo, hi);
                var tan = e.Curve.Deriv(t) * (e.T1 >= e.T0 ? 1 : -1);
                return (e.Curve.Eval(t), tan.Normalized);
            }
            var (_, te) = ResolveEdge(er, st);
            EdgeSeg best = te.Segs[0]; double bd = double.MaxValue;
            foreach (var s in te.Segs) { var dd = DistanceToSegment(near, s.A, s.B); if (dd < bd) { bd = dd; best = s; } }
            var sd = best.B - best.A; var tt = Math.Clamp(Vec3.Dot(near - best.A, sd) / sd.LengthSq, 0, 1);
            return (best.A + sd * tt, sd.Normalized);
        }
        throw new FeatureException("No reference curve selected.");
    }

    /// <summary>
    /// Direction of a straight edge, or the axis of a round face beside a curved one, as the facet lookup gives
    /// them: a straight edge runs along the loop of the face whose name sorts first. Null when the edge is not
    /// found on the native B-rep (the facet lookup then decides).
    /// </summary>
    static (Vec3 origin, Vec3 dir)? NativeEdgeDir(EdgeRef r, ModelState st)
    {
        if (ResolveNativeEdge(r, st) is not { edge: var e }) return null;
        var (first, second) = string.CompareOrdinal(e.Uses[0].Face.Tag.Name, e.Uses[1].Face.Tag.Name) <= 0
            ? (e.Uses[0], e.Uses[1]) : (e.Uses[1], e.Uses[0]);
        if (IsStraight(e)) { var a = first.Start.P; return (a, (first.End.P - a).Normalized); }
        foreach (var u in new[] { first, second })
            if (u.Face.Tag.Surface is { IsCurved: true } s) return (s.Origin, s.Axis.Normalized);
        return null;

        // as TopoEdge.IsLinear on the edge's samples
        static bool IsStraight(Kernel.Native.NEdge e)
        {
            if (e.Curve is Kernel.Native.NLine) return true;
            if (ReferenceEquals(e.V0, e.V1)) return false;
            var pts = e.Samples(1e-3);
            var a = pts[0]; var d = (pts[^1] - a).Normalized;
            foreach (var p in pts) { var v = p - a; if ((v - d * Vec3.Dot(v, d)).Length > 1e-5) return false; }
            return true;
        }
    }

    /// <summary>Resolves a direction/axis to (point on axis, unit direction).</summary>
    public static (Vec3 origin, Vec3 dir) ResolveDir(DirRef r, ModelState st)
    {
        if (r.Axis != null)
            return r.Axis switch
            {
                "X" => (Vec3.Zero, Vec3.UnitX),
                "Y" => (Vec3.Zero, Vec3.UnitY),
                "Z" => (Vec3.Zero, Vec3.UnitZ),
                _ => throw new FeatureException("Bad axis"),
            };
        if (r.Curve is { Sketch: { } skId } cr)
        {
            if (!st.Sketches.TryGetValue(skId, out var sr)) throw new FeatureException("The sketch of the axis is missing.");
            if (sr.Sketch.Entity(cr.SketchEntity) is not Sketching.SkLine l) throw new FeatureException("The axis must be a sketch line.");
            Vec3 a = sr.Frame.ToWorld(sr.Sketch.P(l.P1)), b = sr.Frame.ToWorld(sr.Sketch.P(l.P2));
            if ((b - a).Length < 1e-12) throw new FeatureException("The axis line has no length.");
            return (a, (b - a).Normalized);
        }
        if (r.Plane != null)
        {
            var f = ResolvePlane(r.Plane, st);
            return (f.Origin, f.Normal);
        }
        if (r.Edge != null)
        {
            // from the exact edge (finding it among the facets would tessellate the whole body)
            if (NativeEdgeDir(r.Edge, st) is { } nd) return nd;
            var (b, e) = ResolveEdge(r.Edge, st);
            if (e.IsLinear) return (e.Start, (e.End - e.Start).Normalized);
            // circular edge: use the axis of the adjacent cylinder
            foreach (var s in new[] { e.Segs[0].FaceA, e.Segs[0].FaceB })
            {
                var surf = b.Solid.Faces[s].Tag.Surface;
                if (surf.IsCurved) return (surf.Origin, surf.Axis.Normalized);
            }
            throw new FeatureException("Edge has no usable direction.");
        }
        if (r.Face != null)
        {
            // a round face's axis from the exact face (finding it among the facets would tessellate the whole body)
            if (NativeFace(r.Face, st) is { } hit && hit.face.Tag.Surface is { IsCurved: true } ns) return (ns.Origin, ns.Axis.Normalized);
            var (_, face) = ResolveFace(r.Face, st);
            var s = face.Tag.Surface;
            if (s.IsCurved) return (s.Origin, s.Axis.Normalized);
            return (face.Centroid(), face.Plane.Normal);
        }
        throw new FeatureException("No direction selected.");
    }
}
