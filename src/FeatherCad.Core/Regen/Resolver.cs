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
            var (_, face) = ResolveFace(r.Face, st);
            if (face.Tag.Surface.IsCurved) throw new FeatureException("Selected face is not planar.");
            return Frame.FromPlane(face.Plane);
        }
        throw new FeatureException("No plane selected.");
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
