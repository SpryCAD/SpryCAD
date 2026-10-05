using FeatherCad.Core.Model;
using FeatherCad.Kernel;

namespace FeatherCad.Core.Regen;

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
    static (Body body, Kernel.Native.NFace face)? NativeFace(FaceRef r, ModelState st)
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
        return null;
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
        if (r.Plane != null)
        {
            var f = ResolvePlane(r.Plane, st);
            return (f.Origin, f.Normal);
        }
        if (r.Edge != null)
        {
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
            var (_, face) = ResolveFace(r.Face, st);
            var s = face.Tag.Surface;
            if (s.IsCurved) return (s.Origin, s.Axis.Normalized);
            return (face.Centroid(), face.Plane.Normal);
        }
        throw new FeatureException("No direction selected.");
    }
}
