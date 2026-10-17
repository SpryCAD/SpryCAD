using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.Core.Sketching;

/// <summary>
/// Projects model edges and vertices into a sketch as fixed reference geometry (used to dimension and
/// relate sketch entities to the existing part). References are re-evaluated on every rebuild.
/// </summary>
public static class SketchReferences
{
    /// <summary>Circle described by a model edge, when it lies in a plane parallel to the sketch.</summary>
    public static (Vec3 center, double r)? CircleOf(TopoEdge e, Frame frame)
    {
        if (e.IsLinear) return null;
        var pts = new List<Vec3> { e.Start };
        foreach (var s in e.Segs) pts.Add(s.B);
        if (pts.Count < 3) return null;
        var a = pts[0]; var b = pts[pts.Count / 3]; var c = pts[2 * pts.Count / 3];
        var ab = b - a; var ac = c - a; var n = Vec3.Cross(ab, ac); var n2 = n.LengthSq;
        if (n2 < 1e-20) return null;
        var center = a + (Vec3.Cross(n, ab) * ac.LengthSq + Vec3.Cross(ac, n) * ab.LengthSq) / (2 * n2);
        var r = Vec3.Distance(center, a);
        var axis = n / Math.Sqrt(n2);
        if (Math.Abs(Vec3.Dot(axis, frame.Normal)) < 0.9999) return null;
        foreach (var p in pts) if (Math.Abs(Vec3.Distance(p, center) - r) > 1e-4 * Math.Max(1, r)) return null;
        return (center, r);
    }

    /// <summary>Creates (or reuses) a reference entity for a model edge or vertex. Returns the entity, or null if unsupported.</summary>
    public static SkEntity? Create(Sketch sk, Frame frame, TopoEdge edge, string bodyId, bool vertex, Vec3 hint)
    {
        var ext = new SkExternal
        {
            Edge = new EdgeRef { A = edge.FaceA, B = edge.FaceB, Body = bodyId, Hint = edge.Midpoint },
            Vertex = vertex,
            Hint = vertex ? (Vec3.Distance(hint, edge.Start) <= Vec3.Distance(hint, edge.End) ? edge.Start : edge.End) : edge.Midpoint,
        };
        var existing = sk.Entities.FirstOrDefault(e => e.External != null && e.External.SameAs(ext));
        if (existing != null) return existing;

        if (vertex)
        {
            var p = sk.AddPoint(frame.ToLocal(ext.Hint), fix: true);
            return sk.Add(new SkPointEntity { P = p.Id, External = ext });
        }
        if (CircleOf(edge, frame) is { } circ)
        {
            var c = sk.AddPoint(frame.ToLocal(circ.center), fix: true);
            return sk.Add(new SkCircle { Center = c.Id, R = circ.r, External = ext });
        }
        var a = frame.ToLocal(edge.Start); var b = frame.ToLocal(edge.End);
        if (Vec2.Distance(a, b) < 1e-9) return null; // edge seen end-on
        var pa = sk.AddPoint(a, fix: true); var pb = sk.AddPoint(b, fix: true);
        return sk.Add(new SkLine { P1 = pa.Id, P2 = pb.Id, External = ext });
    }

    /// <summary>
    /// Creates (or reuses) a reference line along the sketch's horizontal ("H") or vertical ("V") axis, from the origin
    /// for <paramref name="length"/>: fixed, to dimension angles to the axis without drawing and defining a line for it.
    /// </summary>
    public static SkLine Axis(Sketch sk, string axis, double length)
    {
        if (sk.Entities.OfType<SkLine>().FirstOrDefault(e => e.External?.Axis == axis) is { } existing) return existing;
        var pa = sk.AddPoint(Vec2.Zero, fix: true);
        var pb = sk.AddPoint(axis == "H" ? new Vec2(length, 0) : new Vec2(0, length), fix: true);
        return (SkLine)sk.Add(new SkLine { P1 = pa.Id, P2 = pb.Id, External = new SkExternal { Axis = axis } });
    }

    /// <summary>
    /// Creates (or reuses) a reference entity for another sketch's point (<paramref name="point"/>) or its line or circle
    /// <paramref name="id"/>, as that sketch is solved in <paramref name="other"/> (a circle only in a plane parallel to
    /// this sketch's). Null if unsupported.
    /// </summary>
    public static SkEntity? CreateFromSketch(Sketch sk, Frame frame, string otherId, SketchResult other, bool point, long id)
    {
        var ext = new SkExternal { Sketch = otherId, SketchItem = id, Vertex = point };
        var existing = sk.Entities.FirstOrDefault(e => e.External != null && e.External.SameAs(ext));
        if (existing != null) return existing;
        SkEntity? made = null;
        if (point)
        {
            if (other.Sketch.Point(id) is not { } op) return null;
            var p = sk.AddPoint(frame.ToLocal(other.Frame.ToWorld(op.P)), fix: true);
            made = sk.Add(new SkPointEntity { P = p.Id, External = ext });
        }
        else switch (other.Sketch.Entity(id))
        {
            case SkLine ol:
                {
                    var a = frame.ToLocal(other.Frame.ToWorld(other.Sketch.P(ol.P1)));
                    var b = frame.ToLocal(other.Frame.ToWorld(other.Sketch.P(ol.P2)));
                    if (Vec2.Distance(a, b) < 1e-9) return null; // (seen end-on)
                    var pa = sk.AddPoint(a, fix: true); var pb = sk.AddPoint(b, fix: true);
                    made = sk.Add(new SkLine { P1 = pa.Id, P2 = pb.Id, External = ext });
                    break;
                }
            case SkCircle oc when Math.Abs(Vec3.Dot(other.Frame.Normal, frame.Normal)) > 0.9999:
                {
                    var c = sk.AddPoint(frame.ToLocal(other.Frame.ToWorld(other.Sketch.P(oc.Center))), fix: true);
                    made = sk.Add(new SkCircle { Center = c.Id, R = oc.R, External = ext });
                    break;
                }
        }
        return made;
    }

    /// <summary>Re-projects all reference entities from the model. Missing references produce warnings.</summary>
    public static void Update(Sketch sk, Frame frame, ModelState state, List<string> warnings)
    {
        foreach (var e in sk.Entities)
        {
            if (e.External is not { } ext || ext.Axis != null) continue;
            if (ext.Sketch != null) { UpdateFromSketch(sk, frame, state, e, ext, warnings); continue; }
            TopoEdge te;
            try { (_, te) = Resolver.ResolveEdge(ext.Edge, state); }
            catch (FeatureException) { warnings.Add("A dimension references a model edge that no longer exists."); continue; }
            switch (e)
            {
                case SkPointEntity pe when ext.Vertex:
                    {
                        var p = Vec3.Distance(ext.Hint, te.Start) <= Vec3.Distance(ext.Hint, te.End) ? te.Start : te.End;
                        Set(sk, pe.P, frame.ToLocal(p));
                        break;
                    }
                case SkLine l:
                    Set(sk, l.P1, frame.ToLocal(te.Start));
                    Set(sk, l.P2, frame.ToLocal(te.End));
                    break;
                case SkCircle c:
                    if (CircleOf(te, frame) is { } circ) { Set(sk, c.Center, frame.ToLocal(circ.center)); c.R = circ.r; }
                    break;
            }
        }
    }

    // another sketch's point, line or circle where that sketch now has it
    static void UpdateFromSketch(Sketch sk, Frame frame, ModelState state, SkEntity e, SkExternal ext, List<string> warnings)
    {
        const string gone = "A relation or dimension references geometry of another sketch that no longer exists.";
        if (!state.Sketches.TryGetValue(ext.Sketch!, out var o)) { warnings.Add(gone); return; }
        Vec2 L(long p) => frame.ToLocal(o.Frame.ToWorld(o.Sketch.P(p)));
        switch (e)
        {
            case SkPointEntity pe when ext.Vertex:
                if (o.Sketch.Point(ext.SketchItem) == null) { warnings.Add(gone); return; }
                Set(sk, pe.P, L(ext.SketchItem));
                break;
            case SkLine l when o.Sketch.Entity(ext.SketchItem) is SkLine ol:
                Set(sk, l.P1, L(ol.P1)); Set(sk, l.P2, L(ol.P2));
                break;
            case SkCircle c when o.Sketch.Entity(ext.SketchItem) is SkCircle oc:
                Set(sk, c.Center, L(oc.Center)); c.R = oc.R;
                break;
            default:
                warnings.Add(gone);
                break;
        }
    }

    static void Set(Sketch sk, long id, Vec2 p)
    {
        if (sk.Point(id) is { } pt) { pt.X = p.X; pt.Y = p.Y; pt.Fixed = true; }
    }
}
